using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Widgets.Clipboard;
using Shima.Widgets.Clipboard.Views;
using Shima.Widgets.Inbox;
using Shima.Widgets.Inbox.Views;
using Shima.Widgets.Notes;
using Shima.Widgets.Notes.Views;
using Shima.WindowsInterop.Clipboard;
using Shima.WindowsInterop.Notifications;
using Xunit;

namespace Shima.Widgets.Tests;

public sealed class ProductivityWidgetsTests
{
    [Fact]
    public async Task ClipboardMonitorAdapter_ExclusionsRetryDibConversionAndSelfCopyGuard_BehaveDeterministically()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var backend = new FakeClipboardNativeBackend();
        var recordedDelays = new List<int>();

        using var adapter = new ClipboardMonitorAdapter(
            backend,
            logger: null,
            utcNowProvider: () => now,
            delayAsync: (ms, _) =>
            {
                recordedDelays.Add(ms);
                return Task.CompletedTask;
            });

        var captured = new List<ClipboardCaptureEventArgs>();
        adapter.ClipboardChanged += (_, args) => captured.Add(args);
        adapter.Start(12345);
        backend.AddedListenerHwnds.Should().ContainSingle().Which.Should().Be((nint)12345);

        // 1. ExcludeClipboardContentFromMonitorProcessing -> completely ignored
        backend.ResetFormats();
        backend.AvailableFormats.Add(backend.ExcludeMonitorId);
        backend.UnicodeText = "SuperSecretPassword!";
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().BeEmpty();

        // 2. CanIncludeInClipboardHistory == 0 -> excluded
        backend.ResetFormats();
        backend.AvailableFormats.Add(backend.CanIncludeHistoryId);
        backend.DwordValues[backend.CanIncludeHistoryId] = 0;
        backend.UnicodeText = "VaultSecret123";
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().BeEmpty();

        // 3. Clipboard Viewer Ignore -> excluded
        backend.ResetFormats();
        backend.AvailableFormats.Add(backend.ViewerIgnoreId);
        backend.UnicodeText = "KeePassSecret";
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().BeEmpty();

        // 4. CanIncludeInClipboardHistory == 1 -> ALLOWED, with 3-retry OpenClipboard backoff
        backend.ResetFormats();
        backend.AvailableFormats.Add(backend.CanIncludeHistoryId);
        backend.DwordValues[backend.CanIncludeHistoryId] = 1;
        backend.UnicodeText = "Safe public snippet";
        backend.SourceAppName = "notepad";
        backend.OpenClipboardFailuresRemaining = 3;

        await adapter.ProcessClipboardUpdateAsync();
        recordedDelays.Should().Equal(25, 50, 75);
        captured.Should().ContainSingle();
        captured[0].ContentType.Should().Be(ClipboardContentType.Text);
        captured[0].TextContent.Should().Be("Safe public snippet");
        captured[0].SourceAppName.Should().Be("notepad");

        // 5. Self-copy suppression loop guard blocks WM_CLIPBOARDUPDATE for recopied item
        (await adapter.TrySetTextAsync("Recopied line")).Should().BeTrue();
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().HaveCount(1);

        // Subsequent distinct external copy is captured normally
        backend.ResetFormats();
        backend.UnicodeText = "Fresh user copy";
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().HaveCount(2);

        // 6. CF_DIB converts purely in managed memory to PNG with zero System.Drawing
        backend.ResetFormats();
        var rawImg = new RawBgraImage(2, 2,
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255
        ]);
        backend.DibBytes = PngImageEncoder.EncodeBgraToDib32(rawImg);
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().HaveCount(3);
        captured[2].ContentType.Should().Be(ClipboardContentType.Image);
        captured[2].ImagePngBytes.Should().NotBeNull();
        captured[2].ImagePngBytes!.Take(4).Should().Equal(137, 80, 78, 71); // PNG signature

        adapter.Stop();
        backend.RemovedListenerHwnds.Should().ContainSingle().Which.Should().Be((nint)12345);
    }

    [Fact]
    public async Task ClipboardWidgetModule_PauseRecopySuppressionSearchAndClearUnpinned_WorkEndToEnd()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 15, 0, TimeSpan.Zero);
        var backend = new FakeClipboardNativeBackend();
        using var adapter = new ClipboardMonitorAdapter(backend, utcNowProvider: () => now);
        var repo = new InMemoryClipboardRepository();

        await using var module = new ClipboardWidgetModule(adapter, repo, utcNowProvider: () => now);
        await module.InitializeAsync(CreateContext(ClipboardWidgetModule.Id), CancellationToken.None);

        await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "alpha-token",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("alpha-token"),
            ByteSize = 11,
            CapturedAtUtc = now
        });
        await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "beta-secret-safe",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("beta-secret-safe"),
            ByteSize = 16,
            CapturedAtUtc = now.AddSeconds(1)
        });

        module.ViewModel.Entries.Should().HaveCount(2);
        var firstRowBeforePin = module.ViewModel.Entries.First(e => e.PreviewText == "alpha-token");
        firstRowBeforePin.IsPinned.Should().BeFalse();
        firstRowBeforePin.PinButtonLabel.Should().Be("Pin");
        firstRowBeforePin.PinActionLabel.Should().Be("Pin clipboard item");
        firstRowBeforePin.PinStateLabel.Should().Be("Not pinned");
        firstRowBeforePin.RowAccessibleName.Should().Be("Clipboard item: alpha-token");

        var firstId = firstRowBeforePin.EntryId;
        await module.ViewModel.TogglePinAsync(firstId);

        var firstRowAfterPin = module.ViewModel.Entries.First(e => e.EntryId == firstId);
        firstRowAfterPin.IsPinned.Should().BeTrue();
        firstRowAfterPin.PinButtonLabel.Should().Be("Unpin");
        firstRowAfterPin.PinActionLabel.Should().Be("Unpin clipboard item");
        firstRowAfterPin.PinStateLabel.Should().Be("Pinned");
        firstRowAfterPin.RowAccessibleName.Should().Be("Pinned clipboard item: alpha-token");

        // Search narrows list
        await module.ViewModel.SetSearchQueryAsync("beta");
        module.ViewModel.Entries.Should().ContainSingle(e => e.PreviewText == "beta-secret-safe");
        await module.ViewModel.SetSearchQueryAsync(string.Empty);

        // Pause drops new events
        await module.ViewModel.SetPausedAsync(true);
        await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "ignored-while-paused",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("ignored-while-paused"),
            ByteSize = 20,
            CapturedAtUtc = now.AddSeconds(2)
        });
        module.ViewModel.Entries.Should().HaveCount(2);

        // One-click recopy arms self-copy guard without duplicating row
        await module.ViewModel.SetPausedAsync(false);
        (await module.ViewModel.RecopyEntryAsync(firstId)).Should().BeTrue();
        await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "alpha-token",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("alpha-token"),
            ByteSize = 11,
            CapturedAtUtc = now.AddSeconds(2)
        });
        module.ViewModel.Entries.Should().HaveCount(2);

        // Clear unpinned preserves pinned alpha-token
        await module.ViewModel.ClearUnpinnedAsync();
        module.ViewModel.Entries.Should().ContainSingle(e => e.PreviewText == "alpha-token");
    }

    [Fact]
    public async Task ClipboardWidgetViewModel_RecopyImageFromDisk_WritesDibAndPng_ShowsCopiedFeedbackFor1500ms_AndHandlesFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"shima-clip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var now = new DateTimeOffset(2026, 11, 22, 12, 10, 0, TimeSpan.Zero);
            var backend = new FakeClipboardNativeBackend();
            using var adapter = new ClipboardMonitorAdapter(backend, utcNowProvider: () => now);
            var repo = new InMemoryClipboardRepository(tempRoot);
            var feedbackTimer = new FakeOneShotTimer();

            await using var module = new ClipboardWidgetModule(
                adapter,
                repo,
                utcNowProvider: () => now,
                copyFeedbackTimer: feedbackTimer);
            await module.InitializeAsync(CreateContext(ClipboardWidgetModule.Id), CancellationToken.None);

            var pngBytes = PngImageEncoder.EncodeBgra32ToPng(
                new RawBgraImage(2, 2,
                [
                    10, 20, 30, 255, 40, 50, 60, 255,
                    70, 80, 90, 255, 100, 110, 120, 255
                ]));
            var hash = ClipboardMonitorAdapter.ComputeImageHash(pngBytes);

            await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
            {
                ContentType = ClipboardContentType.Image,
                ImagePngBytes = pngBytes,
                ContentHash = hash,
                SourceAppName = "SnippingTool",
                ByteSize = pngBytes.Length,
                CapturedAtUtc = now
            });

            module.ViewModel.Entries.Should().ContainSingle();
            var row = module.ViewModel.Entries[0];
            row.IsImage.Should().BeTrue();
            row.CopyButtonLabel.Should().Be("Copy");

            // 1. Success path: writes BOTH valid CF_DIB and PNG bytes, sets Copied state for 1500ms
            (await module.ViewModel.RecopyEntryAsync(row.EntryId)).Should().BeTrue();
            backend.PngBytes.Should().Equal(pngBytes);
            backend.DibBytes.Should().NotBeNull();
            BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(0, 4)).Should().Be(40);
            BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(4, 4)).Should().Be(2);
            BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(8, 4)).Should().Be(2);
            BinaryPrimitives.ReadInt16LittleEndian(backend.DibBytes.AsSpan(14, 2)).Should().Be(32);

            row.CopyButtonLabel.Should().Be("Copied");
            row.IsCopiedFeedbackActive.Should().BeTrue();
            module.ViewModel.StatusMessage.Should().Be("Copied");
            module.ViewModel.IsCopySuccessFeedbackActive.Should().BeTrue();
            module.ViewModel.IsStatusError.Should().BeFalse();
            feedbackTimer.IsPending.Should().BeTrue();
            feedbackTimer.LastDelay.Should().Be(TimeSpan.FromMilliseconds(1500));

            // Echoed self-copy capture must not create a duplicate row
            await module.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
            {
                ContentType = ClipboardContentType.Image,
                ImagePngBytes = pngBytes,
                ContentHash = hash,
                SourceAppName = "SnippingTool",
                ByteSize = pngBytes.Length,
                CapturedAtUtc = now.AddSeconds(1)
            });
            module.ViewModel.Entries.Should().ContainSingle();

            // Timer fires -> feedback cleanly resets (zero idle timer ticks afterwards)
            feedbackTimer.Fire();
            feedbackTimer.IsPending.Should().BeFalse();
            row.CopyButtonLabel.Should().Be("Copy");
            row.IsCopiedFeedbackActive.Should().BeFalse();
            module.ViewModel.StatusMessage.Should().BeEmpty();
            module.ViewModel.IsCopySuccessFeedbackActive.Should().BeFalse();
            module.ViewModel.IsStatusError.Should().BeFalse();

            // 2. Failure path (OpenClipboard fails): NEVER shows Copied; shows Copy failed
            backend.OpenClipboardFailuresRemaining = 10;
            (await module.ViewModel.RecopyEntryAsync(row.EntryId)).Should().BeFalse();
            row.CopyButtonLabel.Should().Be("Copy");
            row.IsCopiedFeedbackActive.Should().BeFalse();
            module.ViewModel.StatusMessage.Should().Be("Copy failed");
            module.ViewModel.IsStatusError.Should().BeTrue();
            module.ViewModel.IsCopySuccessFeedbackActive.Should().BeFalse();
            feedbackTimer.LastDelay.Should().Be(TimeSpan.FromMilliseconds(1500));

            feedbackTimer.Fire();
            module.ViewModel.StatusMessage.Should().BeEmpty();
            module.ViewModel.IsStatusError.Should().BeFalse();

            // 3. Failure path (missing cached PNG on disk) + module deactivation cancels timer
            Directory.Delete(Path.Combine(tempRoot, "cache"), recursive: true);
            (await module.ViewModel.RecopyEntryAsync(row.EntryId)).Should().BeFalse();
            row.CopyButtonLabel.Should().Be("Copy");
            module.ViewModel.StatusMessage.Should().Be("Copy failed");
            module.ViewModel.IsStatusError.Should().BeTrue();
            feedbackTimer.IsPending.Should().BeTrue();

            await module.DeactivateAsync();
            feedbackTimer.IsPending.Should().BeFalse();
            module.ViewModel.StatusMessage.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NotesWidgetViewModel_CopyActiveNote_ShowsCopiedFeedbackFor1500ms_AndSurfacesFailureWithoutCopied()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 20, 0, TimeSpan.Zero);
        var repo = new InMemoryNoteRepository();
        var debounceTimer = new FakeNotesDebounceTimer();
        var feedbackTimer = new FakeOneShotTimer();
        var copyShouldSucceed = true;

        using var vm = new NotesWidgetViewModel(
            repo,
            debounceTimer,
            clipboardAdapter: null,
            utcNowProvider: () => now,
            copyTextOverride: (_, _) => Task.FromResult(copyShouldSucceed),
            copyFeedbackTimer: feedbackTimer);

        await vm.InitializeAsync(CreateContext(NotesWidgetModule.Id), CancellationToken.None);
        await vm.CreateNewNoteAsync();
        vm.EditorContent = "Release checklist";
        await vm.FlushPendingSaveAsync();

        // 1. Success -> Copied for 1.5s, then reverts cleanly
        (await vm.CopyActiveNoteAsync()).Should().BeTrue();
        vm.CopyButtonLabel.Should().Be("Copied");
        vm.IsCopiedFeedbackActive.Should().BeTrue();
        vm.SaveStatusLabel.Should().Be("Copied");
        vm.IsCopyError.Should().BeFalse();
        feedbackTimer.IsPending.Should().BeTrue();
        feedbackTimer.LastDelay.Should().Be(TimeSpan.FromMilliseconds(1500));

        feedbackTimer.Fire();
        feedbackTimer.IsPending.Should().BeFalse();
        vm.CopyButtonLabel.Should().Be("Copy");
        vm.IsCopiedFeedbackActive.Should().BeFalse();
        vm.SaveStatusLabel.Should().Be("Saved");

        // 2. Failure -> never shows Copied; surfaces Copy failed
        copyShouldSucceed = false;
        (await vm.CopyActiveNoteAsync()).Should().BeFalse();
        vm.CopyButtonLabel.Should().Be("Copy");
        vm.IsCopiedFeedbackActive.Should().BeFalse();
        vm.SaveStatusLabel.Should().Be("Copy failed");
        vm.IsCopyError.Should().BeTrue();
        feedbackTimer.IsPending.Should().BeTrue();

        feedbackTimer.Fire();
        vm.IsCopyError.Should().BeFalse();
        vm.SaveStatusLabel.Should().Be("Saved");

        // 3. Module DeactivateAsync cancels pending copy feedback timer
        using var clipAdapter = new ClipboardMonitorAdapter(new FakeClipboardNativeBackend());
        var moduleFeedbackTimer = new FakeOneShotTimer();
        await using var module = new NotesWidgetModule(
            repo,
            clipAdapter,
            utcNowProvider: () => now,
            debounceTimer: new FakeNotesDebounceTimer(),
            copyFeedbackTimer: moduleFeedbackTimer);
        await module.InitializeAsync(CreateContext(NotesWidgetModule.Id), CancellationToken.None);

        (await module.ViewModel.CopyActiveNoteAsync()).Should().BeTrue();
        moduleFeedbackTimer.IsPending.Should().BeTrue();
        await module.DeactivateAsync();
        moduleFeedbackTimer.IsPending.Should().BeFalse();
        module.ViewModel.CopyButtonLabel.Should().Be("Copy");
        module.ViewModel.SaveStatusLabel.Should().Be("Saved");
    }

    [Fact]
    public async Task NotesWidgetModule_DebouncesAutoSave_FlushesOnCollapse_AndLocksFocusAgainstAutoCollapse()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);
        var repo = new InMemoryNoteRepository();
        var timer = new FakeNotesDebounceTimer();
        var events = new InMemoryEventDispatcher();
        var pinController = new OverlayPinController();
        using var stateManager = new IslandStateManager(pinController);

        using var focusSub = events.Subscribe<IslandInputFocusRequestedEvent>(
            evt => stateManager.SetInputFocusActive(evt.IsActive));

        await using var module = new NotesWidgetModule(
            repo,
            clipboardAdapter: null,
            utcNowProvider: () => now,
            debounceTimer: timer);

        await module.InitializeAsync(CreateContext(NotesWidgetModule.Id, events), CancellationToken.None);
        await module.ViewModel.CreateNewNoteAsync();

        // Typing arms 500ms debounce timer and does not save until timer elapses or flushes
        module.ViewModel.EditorContent = "Architecture ideas\nKeep layers isolated";
        module.IsAutoSavePending.Should().BeTrue();
        timer.LastDelay.Should().Be(TimeSpan.FromMilliseconds(500));

        timer.Fire();
        await Task.Delay(50);
        module.IsAutoSavePending.Should().BeFalse();
        (await repo.GetAllAsync()).Should().ContainSingle(n => n.ContentText.Contains("Keep layers isolated"));

        // Focus lock protects editor against accidental mouse-leave collapse
        stateManager.RequestUserExpand();
        module.ViewModel.NotifyInputFocusChanged(true);
        stateManager.RequestUserCollapse(force: false);
        stateManager.CurrentState.Should().Be(IslandShellState.Expanded);

        // Edit without firing debounce timer -> DeactivateAsync flushes save & releases focus lock
        module.ViewModel.EditorContent = "Updated before collapse";
        module.IsAutoSavePending.Should().BeTrue();
        await module.DeactivateAsync();

        module.IsAutoSavePending.Should().BeFalse();
        (await repo.GetAllAsync()).Should().ContainSingle(n => n.ContentText == "Updated before collapse");
        stateManager.RequestUserCollapse(force: false);
        stateManager.CurrentState.Should().Be(IslandShellState.Collapsed);
    }

    [Fact]
    public async Task InboxWidgetModule_IdentityUnavailableDndSuppressionExclusionsAndSyncToggle_WorkCleanly()
    {
        var now = new DateTimeOffset(2026, 11, 22, 11, 0, 0, TimeSpan.Zero);

        // 1. Verify live unpackaged NotificationListenerAdapter + IdentityUnavailable guidance card, deep-link & non-native notice
        using var liveUnpackagedAdapter = new NotificationListenerAdapter();
        (await liveUnpackagedAdapter.GetAccessStateAsync()).Should().Be(NotificationAccessState.IdentityUnavailable);

        var unpackagedBackend = new FakeNotificationNativeBackend
        {
            AccessState = NotificationAccessState.IdentityUnavailable
        };
        using var unpackagedAdapter = new NotificationListenerAdapter(unpackagedBackend);
        var openedUris = new List<string>();
        await using var unpackagedModule = new InboxWidgetModule(
            unpackagedAdapter,
            new InMemoryInboxRepository(),
            openUriAction: uri => openedUris.Add(uri),
            utcNowProvider: () => now);
        await unpackagedModule.InitializeAsync(CreateContext(InboxWidgetModule.Id), CancellationToken.None);

        unpackagedModule.ViewModel.ShowAccessGuidanceCard.Should().BeTrue();
        unpackagedModule.ViewModel.NonNativeNoticeText.Should().Contain("custom notification windows");
        unpackagedModule.ViewModel.OpenWindowsNotificationSettings();
        openedUris.Should().ContainSingle().Which.Should().Be("ms-settings:privacy-notifications");

        // 2. Verify DND suppression, per-app exclusion, body masking, and bidirectional dismissal toggle
        var allowedBackend = new FakeNotificationNativeBackend
        {
            AccessState = NotificationAccessState.Allowed,
            IsDndActive = true
        };
        using var allowedAdapter = new NotificationListenerAdapter(allowedBackend);
        var repo = new InMemoryInboxRepository();
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            SyncInboxDismissalWithActionCenter = true,
            InboxRetentionDays = 14
        });
        var pinController = new OverlayPinController();
        using var stateManager = new IslandStateManager(pinController);
        var requester = new IslandAttentionRequester(stateManager);
        var events = new InMemoryEventDispatcher();
        var peekEvents = new List<NotificationPeekContentEvent>();
        using var sub = events.Subscribe<NotificationPeekContentEvent>(e => peekEvents.Add(e));

        await using var module = new InboxWidgetModule(
            allowedAdapter,
            repo,
            prefs,
            utcNowProvider: () => now);
        await module.InitializeAsync(CreateContext(InboxWidgetModule.Id, events, requester), CancellationToken.None);
        await module.ViewModel.SetExcludedAppsAsync(["noisyapp"]);

        // Excluded app toast -> neither stored nor peeked
        allowedBackend.IsDndActive = false;
        await module.ViewModel.HandleNotificationArrivedAsync(
            new OsNotificationDto(1, "noisyapp", "NoisyApp", "Spam", "Ignore me", null, now));
        module.ViewModel.AppGroups.Should().BeEmpty();
        peekEvents.Should().BeEmpty();

        // DND active -> stored in Inbox, NO Island popup peek!
        allowedBackend.IsDndActive = true;
        await module.ViewModel.HandleNotificationArrivedAsync(
            new OsNotificationDto(2, "com.slack", "Slack", "Secret OTP", "Code 1234", null, now.AddSeconds(1)));
        module.ViewModel.TotalActiveCount.Should().Be(1);
        stateManager.CurrentState.Should().Be(IslandShellState.Collapsed);
        peekEvents.Should().BeEmpty();

        // DND off + privacy mask enabled -> Island peeks with masked body
        allowedBackend.IsDndActive = false;
        await module.ViewModel.SetMaskNotificationBodyAsync(true);
        await module.ViewModel.HandleNotificationArrivedAsync(
            new OsNotificationDto(3, "com.mail", "Mail", "Quarterly report", "Sensitive revenue body", null, now.AddSeconds(2)));

        stateManager.CurrentState.Should().Be(IslandShellState.Notification);
        peekEvents.Should().ContainSingle();
        peekEvents[0].Title.Should().Be("Quarterly report");
        peekEvents[0].Body.Should().Contain("hidden");

        // Bidirectional Action Center sync = true -> dismiss calls RemoveNotification on OS
        var mailItemId = module.ViewModel.AppGroups
            .Single(g => g.AppId == "com.mail")
            .Items.Single().NotificationId;
        await module.ViewModel.DismissNotificationAsync(mailItemId);
        allowedBackend.RemovedToastIds.Should().Equal(3u);

        // Toggle SyncInboxDismissalWithActionCenter = false -> local-only dismissal
        prefs.Update(prefs.Current with { SyncInboxDismissalWithActionCenter = false });
        var slackItemId = module.ViewModel.AppGroups
            .Single(g => g.AppId == "com.slack")
            .Items.Single().NotificationId;
        await module.ViewModel.DismissNotificationAsync(slackItemId);
        allowedBackend.RemovedToastIds.Should().Equal(3u); // Still only 3u

        // External Action Center removal marks dismissed only when sync is enabled
        prefs.Update(prefs.Current with { SyncInboxDismissalWithActionCenter = true });
        await module.ViewModel.HandleNotificationArrivedAsync(
            new OsNotificationDto(4, "com.git", "Git", "PR merged", "Merged main", null, now.AddSeconds(3)));
        await module.ViewModel.HandleNotificationRemovedExternallyAsync(4u);
        module.ViewModel.TotalActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task ProductivityWidgetViews_InstantiateCleanlyOnStaWpfDispatcher()
    {
        var now = new DateTimeOffset(2026, 11, 22, 11, 30, 0, TimeSpan.Zero);
        using var clipAdapter = new ClipboardMonitorAdapter(new FakeClipboardNativeBackend());
        using var notifAdapter = new NotificationListenerAdapter(new FakeNotificationNativeBackend());

        await using var notes = new NotesWidgetModule(new InMemoryNoteRepository(), utcNowProvider: () => now);
        await using var clip = new ClipboardWidgetModule(clipAdapter, new InMemoryClipboardRepository(), utcNowProvider: () => now);
        await using var inbox = new InboxWidgetModule(notifAdapter, new InMemoryInboxRepository(), utcNowProvider: () => now);

        await notes.InitializeAsync(CreateContext(NotesWidgetModule.Id), CancellationToken.None);
        await clip.InitializeAsync(CreateContext(ClipboardWidgetModule.Id), CancellationToken.None);
        await inbox.InitializeAsync(CreateContext(InboxWidgetModule.Id), CancellationToken.None);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var notesView = notes.CreateExpandedView().Should().BeOfType<NotesView>().Subject;
                notes.CreateSettingsView().Should().BeOfType<NotesSettingsView>();
                notesView.DataContext.Should().BeSameAs(notes.ViewModel);

                var clipView = clip.CreateExpandedView().Should().BeOfType<ClipboardView>().Subject;
                clip.CreateSettingsView().Should().BeOfType<ClipboardSettingsView>();
                clipView.DataContext.Should().BeSameAs(clip.ViewModel);

                var inboxView = inbox.CreateExpandedView().Should().BeOfType<InboxView>().Subject;
                inbox.CreateSettingsView().Should().BeOfType<InboxSettingsView>();
                inboxView.DataContext.Should().BeSameAs(inbox.ViewModel);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        completed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        staException.Should().BeNull();
    }

    [Fact]
    public async Task ClipboardView_PinnedAndUnpinnedRows_BindIndicatorPinLabelAndButtonsWithoutClippingOnStaDispatcher()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 30, 0, TimeSpan.Zero);
        using var clipAdapter = new ClipboardMonitorAdapter(new FakeClipboardNativeBackend(), utcNowProvider: () => now);
        await using var clip = new ClipboardWidgetModule(clipAdapter, new InMemoryClipboardRepository(), utcNowProvider: () => now);
        await clip.InitializeAsync(CreateContext(ClipboardWidgetModule.Id), CancellationToken.None);

        await clip.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "pinned-entry-with-very-long-preview-text-that-should-ellipsis-cleanly",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("pinned-entry-with-very-long-preview-text-that-should-ellipsis-cleanly"),
            ByteSize = 68,
            CapturedAtUtc = now
        });
        await clip.ViewModel.HandleClipboardCaptureAsync(new ClipboardCaptureEventArgs
        {
            ContentType = ClipboardContentType.Text,
            TextContent = "unpinned-entry",
            ContentHash = ClipboardMonitorAdapter.ComputeTextHash("unpinned-entry"),
            ByteSize = 14,
            CapturedAtUtc = now.AddSeconds(1)
        });

        var pinnedId = clip.ViewModel.Entries.Single(e => e.PreviewText.StartsWith("pinned-entry", StringComparison.Ordinal)).EntryId;
        await clip.ViewModel.TogglePinAsync(pinnedId);

        Exception? staException = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var clipView = clip.CreateExpandedView().Should().BeOfType<ClipboardView>().Subject;
                clipView.Measure(new Size(390, 240));
                clipView.Arrange(new Rect(0, 0, 390, 240));
                clipView.UpdateLayout();

                var list = FindVisualDescendants<ItemsControl>(clipView)
                    .Single(ic => AutomationProperties.GetAutomationId(ic) == "ClipboardEntriesList");

                var indicators = FindVisualDescendants<TextBlock>(list)
                    .Where(tb => AutomationProperties.GetAutomationId(tb) == "ClipboardPinnedIndicator")
                    .ToList();
                indicators.Should().HaveCount(2);
                indicators[0].Visibility.Should().Be(Visibility.Visible);
                indicators[0].Text.Should().Be("\uE718");
                AutomationProperties.GetName(indicators[0]).Should().Be("Pinned");
                indicators[1].Visibility.Should().Be(Visibility.Collapsed);

                var pinButtons = FindVisualDescendants<Button>(list)
                    .Where(b => AutomationProperties.GetAutomationId(b) == "ClipboardPinButton")
                    .ToList();
                pinButtons.Should().HaveCount(2);
                pinButtons[0].Content.Should().Be("Unpin");
                AutomationProperties.GetName(pinButtons[0]).Should().Be("Unpin clipboard item");
                pinButtons[1].Content.Should().Be("Pin");
                AutomationProperties.GetName(pinButtons[1]).Should().Be("Pin clipboard item");

                var rowButtons = FindVisualDescendants<Button>(list)
                    .Where(b => b.Content is string label && label is "Copy" or "Pin" or "Unpin" or "Remove")
                    .ToList();
                rowButtons.Should().HaveCount(6);
                foreach (var btn in rowButtons)
                {
                    btn.ActualWidth.Should().BeGreaterThan(0);
                    var topRight = btn.TranslatePoint(new Point(btn.ActualWidth, 0), clipView);
                    topRight.X.Should().BeLessOrEqualTo(390.1);
                }
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        completed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        staException.Should().BeNull();
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IWidgetContext CreateContext(
        string widgetId,
        IEventDispatcher? events = null,
        IIslandAttentionRequester? island = null)
    {
        string? json = "{}";
        var settingsStore = Substitute.For<IWidgetSettingsStore>();
        settingsStore.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<string?>(json));
        settingsStore.WriteJsonAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                json = ci.Arg<string>();
                return Task.CompletedTask;
            });

        var context = Substitute.For<IWidgetContext>();
        context.WidgetId.Returns(widgetId);
        context.Events.Returns(events ?? Substitute.For<IEventDispatcher>());
        context.Settings.Returns(settingsStore);
        context.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        context.Island.Returns(island ?? Substitute.For<IIslandAttentionRequester>());
        context.Logger.Returns(Substitute.For<ILogger>());
        return context;
    }

    private sealed class FakeClipboardNativeBackend : IClipboardNativeBackend
    {
        public uint ExcludeMonitorId => 50001;
        public uint CanIncludeHistoryId => 50002;
        public uint ViewerIgnoreId => 50003;
        public uint PngId => 50004;

        public HashSet<uint> AvailableFormats { get; } = [];
        public Dictionary<uint, uint> DwordValues { get; } = [];
        public string? UnicodeText { get; set; }
        public byte[]? PngBytes { get; set; }
        public byte[]? DibBytes { get; set; }
        public string? SourceAppName { get; set; }
        public int OpenClipboardFailuresRemaining { get; set; }
        public List<nint> AddedListenerHwnds { get; } = [];
        public List<nint> RemovedListenerHwnds { get; } = [];

        public void ResetFormats()
        {
            AvailableFormats.Clear();
            DwordValues.Clear();
            UnicodeText = null;
            PngBytes = null;
            DibBytes = null;
        }

        public uint RegisterClipboardFormat(string name) => name switch
        {
            "ExcludeClipboardContentFromMonitorProcessing" => ExcludeMonitorId,
            "CanIncludeInClipboardHistory" => CanIncludeHistoryId,
            "Clipboard Viewer Ignore" => ViewerIgnoreId,
            "PNG" => PngId,
            _ => 59999
        };

        public bool AddClipboardFormatListener(nint hwnd)
        {
            AddedListenerHwnds.Add(hwnd);
            return true;
        }

        public bool RemoveClipboardFormatListener(nint hwnd)
        {
            RemovedListenerHwnds.Add(hwnd);
            return true;
        }

        public bool OpenClipboard(nint hwndOwner)
        {
            if (OpenClipboardFailuresRemaining > 0)
            {
                OpenClipboardFailuresRemaining--;
                return false;
            }

            return true;
        }

        public bool CloseClipboard() => true;

        public bool IsClipboardFormatAvailable(uint format)
        {
            if (AvailableFormats.Contains(format))
            {
                return true;
            }

            return format switch
            {
                ClipboardMonitorAdapter.CfUnicodeText => UnicodeText is not null,
                ClipboardMonitorAdapter.CfDib => DibBytes is not null,
                _ => format == PngId && PngBytes is not null
            };
        }

        public bool TryReadDwordFormat(uint format, out uint value) =>
            DwordValues.TryGetValue(format, out value);

        public string? TryReadUnicodeText(int maxChars) =>
            UnicodeText is not null && UnicodeText.Length <= maxChars ? UnicodeText : null;

        public byte[]? TryReadFormatBytes(uint format, int maxBytes)
        {
            var data = format == PngId ? PngBytes : format == ClipboardMonitorAdapter.CfDib ? DibBytes : null;
            return data is not null && data.Length <= maxBytes ? data : null;
        }

        public bool ClearAndSetClipboard(string? unicodeText, byte[]? pngBytes, byte[]? dibBytes, uint pngFormatId)
        {
            ResetFormats();
            UnicodeText = unicodeText;
            PngBytes = pngBytes;
            DibBytes = dibBytes;
            return true;
        }

        public string? GetForegroundProcessName() => SourceAppName;
    }

    private sealed class FakeNotificationNativeBackend : INotificationNativeBackend
    {
        public NotificationAccessState AccessState { get; set; } = NotificationAccessState.IdentityUnavailable;
        public bool IsDndActive { get; set; }
        public List<uint> RemovedToastIds { get; } = [];

        public event EventHandler<OsNotificationEventArgs>? NotificationArrived
        {
            add { }
            remove { }
        }

        public event EventHandler<uint>? NotificationRemovedExternally
        {
            add { }
            remove { }
        }

        public bool IsSupportedOsVersion() => true;
        public bool HasPackageIdentity() => AccessState != NotificationAccessState.IdentityUnavailable;
        public Task<NotificationAccessState> GetAccessStateAsync(CancellationToken ct = default) => Task.FromResult(AccessState);
        public Task<NotificationAccessState> RequestAccessAsync(CancellationToken ct = default) => Task.FromResult(AccessState);
        public Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OsNotificationDto>>(Array.Empty<OsNotificationDto>());

        public Task<bool> TryRemoveFromActionCenterAsync(uint osToastId, CancellationToken ct = default)
        {
            RemovedToastIds.Add(osToastId);
            return Task.FromResult(true);
        }

        public bool IsDoNotDisturbActive() => IsDndActive;
        public Task StartListeningAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void StopListening() { }
        public void Dispose() { }
    }

    private sealed class FakeNotesDebounceTimer : INotesDebounceTimer
    {
        private Action? _callback;
        public bool IsPending { get; private set; }
        public TimeSpan? LastDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _callback = callback;
            IsPending = true;
        }

        public void Cancel()
        {
            IsPending = false;
            _callback = null;
        }

        public void Fire()
        {
            var cb = _callback;
            IsPending = false;
            _callback = null;
            cb?.Invoke();
        }

        public void Dispose() => Cancel();
    }

    private sealed class FakeOneShotTimer : IOneShotTimer
    {
        private Action? _callback;

        public bool IsPending { get; private set; }

        public TimeSpan? LastDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _callback = callback;
            IsPending = true;
        }

        public void Cancel()
        {
            IsPending = false;
            _callback = null;
        }

        public void Fire()
        {
            var cb = _callback;
            IsPending = false;
            _callback = null;
            cb?.Invoke();
        }

        public void Dispose() => Cancel();
    }

    private sealed class FakeAppPreferencesService(AppPreferencesSnapshot initial) : IAppPreferencesService
    {
        public AppPreferencesSnapshot Current { get; private set; } = initial;
        public event EventHandler<AppPreferencesSnapshot>? PreferencesChanged;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public string? ReadOpenCodeServerPassword() => null;
        public Task ResetToDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(
            AppPreferencesSnapshot snapshot,
            string? newOpenCodeServerPassword = null,
            bool clearOpenCodeServerPassword = false,
            string? newLicenseKey = null,
            bool clearLicenseKey = false,
            CancellationToken ct = default)
        {
            Update(snapshot);
            return Task.CompletedTask;
        }

        public void Update(AppPreferencesSnapshot next)
        {
            Current = next;
            PreferencesChanged?.Invoke(this, next);
        }
    }

    private sealed class InMemoryNoteRepository : INoteRepository
    {
        private readonly List<NoteEntry> _notes = [];

        public Task<IReadOnlyList<NoteEntry>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NoteEntry>>(
                _notes.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.UpdatedAtUtc).ToArray());

        public Task<IReadOnlyList<NoteEntry>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NoteEntry>>(
                _notes.Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || n.ContentText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(n => n.IsPinned)
                    .ThenByDescending(n => n.UpdatedAtUtc)
                    .ToArray());

        public Task<NoteEntry> UpsertAsync(NoteEntry note, CancellationToken ct = default)
        {
            _notes.RemoveAll(n => n.NoteId == note.NoteId);
            _notes.Add(note);
            return Task.FromResult(note);
        }

        public Task SetPinnedAsync(string noteId, bool isPinned, CancellationToken ct = default)
        {
            var idx = _notes.FindIndex(n => n.NoteId == noteId);
            if (idx >= 0)
            {
                _notes[idx] = _notes[idx] with { IsPinned = isPinned };
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string noteId, CancellationToken ct = default)
        {
            _notes.RemoveAll(n => n.NoteId == noteId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryClipboardRepository(string? dataRoot = null) : IClipboardRepository
    {
        private readonly List<ClipboardEntry> _entries = [];
        private readonly string? _dataRoot = dataRoot;

        public Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(
            string? query = null,
            int limit = 100,
            CancellationToken ct = default)
        {
            IEnumerable<ClipboardEntry> items = _entries;
            if (!string.IsNullOrWhiteSpace(query))
            {
                items = items.Where(e =>
                    e.TextContent?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
            }

            return Task.FromResult<IReadOnlyList<ClipboardEntry>>(
                items.OrderByDescending(e => e.IsPinned)
                    .ThenByDescending(e => e.CopiedAtUtc)
                    .Take(limit)
                    .ToArray());
        }

        public Task<ClipboardAddResult> TryAddEntryAsync(
            ClipboardEntry entry,
            int maxUnpinnedItems = 100,
            int retentionDays = 30,
            bool forcePrune = false,
            CancellationToken ct = default)
        {
            var latest = _entries.OrderByDescending(e => e.CopiedAtUtc).FirstOrDefault();
            if (latest is not null && latest.ContentHash == entry.ContentHash)
            {
                return Task.FromResult(new ClipboardAddResult(false, latest, true, 0));
            }

            _entries.Add(entry);
            return Task.FromResult(new ClipboardAddResult(true, entry, false, 0));
        }

        public Task SetPinnedAsync(string entryId, bool isPinned, CancellationToken ct = default)
        {
            var idx = _entries.FindIndex(e => e.EntryId == entryId);
            if (idx >= 0)
            {
                _entries[idx] = _entries[idx] with { IsPinned = isPinned };
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string entryId, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.EntryId == entryId);
            return Task.CompletedTask;
        }

        public Task<int> ClearUnpinnedAsync(CancellationToken ct = default) =>
            Task.FromResult(_entries.RemoveAll(e => !e.IsPinned));

        public Task<int> PruneAsync(
            int maxUnpinnedItems,
            int retentionDays,
            DateTimeOffset? utcNow = null,
            CancellationToken ct = default) => Task.FromResult(0);

        public async Task<string?> SaveImageAssetAsync(string entryId, byte[] pngBytes, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_dataRoot) || pngBytes.Length == 0)
            {
                return null;
            }

            var relative = Path.Combine("cache", "clip", $"{entryId}.png");
            var full = Path.Combine(_dataRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, pngBytes, ct);
            return relative;
        }

        public string? ResolveImageFullPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(_dataRoot) || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            return Path.Combine(_dataRoot, relativePath);
        }
    }

    private sealed class InMemoryInboxRepository : IInboxRepository
    {
        private readonly List<InboxNotificationItem> _items = [];

        public Task<IReadOnlyList<InboxAppGroup>> GetGroupedByAppAsync(
            bool includeDismissed = false,
            CancellationToken ct = default)
        {
            var groups = _items
                .Where(i => includeDismissed || !i.IsDismissed)
                .OrderByDescending(i => i.ReceivedAtUtc)
                .GroupBy(i => i.AppId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new InboxAppGroup(g.Key, g.First().AppName, g.First().AppIconRelPath, g.ToArray()))
                .ToArray();
            return Task.FromResult<IReadOnlyList<InboxAppGroup>>(groups);
        }

        public Task<IReadOnlyList<InboxNotificationItem>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InboxNotificationItem>>(
                _items.Where(i => !i.IsDismissed).OrderByDescending(i => i.ReceivedAtUtc).ToArray());

        public Task<InboxNotificationItem> UpsertToastAsync(InboxNotificationItem item, CancellationToken ct = default)
        {
            _items.RemoveAll(i => i.NotificationId == item.NotificationId);
            _items.Add(item);
            return Task.FromResult(item);
        }

        public Task<InboxNotificationItem?> FindByOsToastIdAsync(uint osToastId, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(i => i.OsToastId == osToastId));

        public Task MarkDismissedAsync(string notificationId, CancellationToken ct = default)
        {
            var idx = _items.FindIndex(i => i.NotificationId == notificationId);
            if (idx >= 0)
            {
                _items[idx] = _items[idx] with { IsDismissed = true };
            }

            return Task.CompletedTask;
        }

        public Task MarkDismissedByOsToastIdAsync(uint osToastId, CancellationToken ct = default)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].OsToastId == osToastId)
                {
                    _items[i] = _items[i] with { IsDismissed = true };
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<uint>> ClearAppGroupAsync(string appId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());

        public Task<IReadOnlyList<uint>> ClearAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());

        public Task<int> PruneExpiredAsync(
            int retentionDays = 14,
            int maxStoredItems = 500,
            DateTimeOffset? utcNow = null,
            CancellationToken ct = default) => Task.FromResult(0);

        public Task<string?> SaveAppIconAssetAsync(string appId, byte[] pngBytes, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public string? ResolveIconFullPath(string? relativePath) => null;
    }
}
