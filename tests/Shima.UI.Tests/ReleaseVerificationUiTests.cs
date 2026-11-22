using System.IO;
using IoPath = System.IO.Path;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Repositories;
using Shima.Infrastructure.Services;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Settings;
using Shima.Presentation.Theming;
using Shima.Widgets.Clipboard;
using Shima.Widgets.Clipboard.Views;
using Shima.Widgets.Inbox;
using Shima.Widgets.Inbox.Views;
using Shima.Widgets.Notes;
using Shima.Widgets.Notes.Views;
using Shima.WindowsInterop.Clipboard;
using Shima.WindowsInterop.Notifications;
using Shima.WindowsInterop.Overlay;
using Xunit;

namespace Shima.UI.Tests;

[Collection("IslandShellUi")]
public sealed class ReleaseVerificationUiTests
{
    [Fact]
    public async Task SettingsWindow_WiresUpdateOfflineCheck_AndOfflineLicenseActivationFlow()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();

        var rootDir = IoPath.Combine(IoPath.GetTempPath(), "shima-release-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);

        try
        {
            var prefsStore = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(rootDir));
            await prefsStore.SaveAsync(new AppPreferences { UpdatesOfflineMode = true });
            var prefsService = new AppPreferencesService(prefsStore, new DpapiSecretStore());
            await prefsService.InitializeAsync();

            var colorService = new ColorProcessingService();
            var pinController = new OverlayPinController();
            var presetManager = new PresetManager(
                new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(rootDir)),
                colorService);
            var appearanceManager = new AppearanceManager(
                new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(rootDir)),
                presetManager,
                colorService,
                pinController);
            await appearanceManager.InitializeAsync();
            var diagService = new DiagnosticLogService(IoPath.Combine(rootDir, "logs"));

            using var updateService = new UpdateServiceAdapter(
                prefsService,
                rootDir,
                manifestEndpointUrl: "https://updates.shima.test/manifest.json",
                currentVersion: "1.0.0",
                currentArchitecture: "win-x64");

            using var licenseService = new LicenseServiceAdapter(
                prefsService,
                verificationPublicKeyPem: publicPem,
                machineIdentityProvider: () => "release-ui-test-machine");

            using var host = new StaWpfTestHost();
            var window = await host.RunAsync(() =>
            {
                var themeRenderer = new ThemeRenderer(appearanceManager, colorService, Dispatcher.CurrentDispatcher);
                var settings = new SettingsWindow(
                    appearanceManager,
                    presetManager,
                    colorService,
                    prefsService,
                    diagService,
                    themeRenderer: themeRenderer,
                    updateService: updateService,
                    licenseService: licenseService);
                settings.Show();
                return settings;
            });

            await host.PumpAsync();

            (await host.RunAsync(() => window.Icon)).Should().NotBeNull("SettingsWindow must load Shima's brand icon");

            // 1. Verify Updates panel offline mode check
            var (installedText, updateStatusText, checkUpdatesButton) = await host.RunAsync(() =>
            {
                window.SelectSection("Updates");
                return (
                    Find<TextBlock>(window, "InstalledVersionText"),
                    Find<TextBlock>(window, "UpdateStatusText"),
                    Find<Button>(window, "CheckForUpdatesButton"));
            });
            await host.PumpAsync();

            (await host.RunAsync(() => installedText.Text)).Should().Contain("1.0.0").And.Contain("win-x64");
            await host.RunAsync(() => InvokeButton(checkUpdatesButton));
            await WaitUntilAsync(
                () => host.RunAsync(() => updateStatusText.Text.Contains("Offline mode is enabled", StringComparison.Ordinal)).GetAwaiter().GetResult(),
                () => host.PumpAsync());

            // 2. Verify License panel offline signed token activation + clear
            var signedToken = LicenseServiceAdapter.CreateSignedToken(
                new SignedLicensePayload(
                    "LIC-UI-01",
                    "Pro",
                    "UI Verifier",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddDays(30),
                    licenseService.ComputeHardwareFingerprintHash()),
                signingKey);

            var (licenseBox, licenseStateText, clearButton, saveButton) = await host.RunAsync(() =>
            {
                window.SelectSection("License");
                return (
                    Find<PasswordBox>(window, "LicenseKeyBox"),
                    Find<TextBlock>(window, "LicenseStateText"),
                    Find<Button>(window, "ClearLicenseKeyButton"),
                    FindByAutomationId<Button>(window, "SaveLicenseKeyButton"));
            });

            await host.RunAsync(() =>
            {
                licenseBox.Password = signedToken;
                InvokeButton(saveButton);
            });

            await WaitUntilAsync(
                () => prefsService.Current.HasLicenseKey,
                () => host.PumpAsync());

            (await host.RunAsync(() => licenseStateText.Text)).Should().Contain("Pro").And.Contain("UI Verifier");
            (await host.RunAsync(() => clearButton.IsEnabled)).Should().BeTrue();

            await host.RunAsync(() => InvokeButton(clearButton));
            await WaitUntilAsync(
                () => !prefsService.Current.HasLicenseKey,
                () => host.PumpAsync());

            (await host.RunAsync(() => licenseStateText.Text)).Should().Contain("Community offline edition");
            await host.RunAsync(() => window.Close());
        }
        finally
        {
            try
            {
                Directory.Delete(rootDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void OverlayGeometry_MultiDpiTopCenterAnchor_StaysCenteredAndWithinMonitorBounds(double dpiScale)
    {
        var primaryBounds = new MonitorBounds(0, 0, 1920, 1080);
        var secondaryBounds = new MonitorBounds(1920, 0, 2560, 1440);

        foreach (var bounds in new[] { primaryBounds, secondaryBounds })
        {
            var collapsed = OverlayWindowAdapter.CalculateAnchoredPixelRect(bounds, dpiScale, new DipSize(136, 28));
            var expanded = OverlayWindowAdapter.CalculateAnchoredPixelRect(bounds, dpiScale, new DipSize(444, 176));

            collapsed.Top.Should().Be(bounds.Top);
            expanded.Top.Should().Be(bounds.Top);

            collapsed.Width.Should().Be((int)Math.Round(136 * dpiScale, MidpointRounding.AwayFromZero));
            expanded.Width.Should().Be((int)Math.Round(444 * dpiScale, MidpointRounding.AwayFromZero));

            collapsed.Left.Should().BeGreaterOrEqualTo(bounds.Left);
            expanded.Left.Should().BeGreaterOrEqualTo(bounds.Left);
            (expanded.Left + expanded.Width).Should().BeLessOrEqualTo(bounds.Left + bounds.Width);

            var monitorCenter = bounds.Left + bounds.Width / 2.0;
            var expandedCenter = expanded.Left + expanded.Width / 2.0;
            Math.Abs(expandedCenter - monitorCenter).Should().BeLessOrEqualTo(1.0);
        }
    }

    [Fact]
    public async Task ThemeTokens_GlobalScrollBarStyle_AppliesSlimPillWithNoArrows_AcrossWidgetsAndSettings()
    {
        var rootDir = IoPath.Combine(IoPath.GetTempPath(), "shima-scrollbar-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);

        try
        {
            var dbPath = IoPath.Combine(rootDir, "shima.db");
            var migrations = new SqliteMigrationRunner(dbPath);
            await migrations.InitializeAsync();

            var noteRepo = new NoteRepository(dbPath);
            var clipRepo = new ClipboardRepository(dbPath, rootDir);
            var inboxRepo = new InboxRepository(dbPath, rootDir);
            var now = new DateTimeOffset(2026, 11, 22, 16, 10, 0, TimeSpan.Zero);

            for (var i = 1; i <= 6; i++)
            {
                await noteRepo.UpsertAsync(new NoteEntry
                {
                    NoteId = $"n{i}",
                    Title = $"Note {i}",
                    ContentText = string.Join(Environment.NewLine, Enumerable.Range(1, 15).Select(l => $"Note {i} line {l}")),
                    CreatedAtUtc = now.AddMinutes(-i),
                    UpdatedAtUtc = now.AddMinutes(i)
                });
                await clipRepo.TryAddEntryAsync(new ClipboardEntry
                {
                    EntryId = $"c{i}",
                    ContentType = ClipboardContentType.Text,
                    TextContent = $"Clip entry {i}",
                    ContentHash = $"hash-{i}",
                    ByteSize = 12,
                    CopiedAtUtc = now.AddMinutes(i)
                });
                await inboxRepo.UpsertToastAsync(new InboxNotificationItem
                {
                    NotificationId = $"i{i}",
                    AppId = $"app.{i}",
                    AppName = $"App {i}",
                    Title = $"Alert {i}",
                    BodyText = $"Message {i}",
                    ReceivedAtUtc = now.AddMinutes(i)
                });
            }

            var prefsStore = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(rootDir));
            var prefsService = new AppPreferencesService(prefsStore, new DpapiSecretStore());
            await prefsService.InitializeAsync();

            var colorService = new ColorProcessingService();
            var pinController = new OverlayPinController();
            var presetManager = new PresetManager(
                new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(rootDir)),
                colorService);
            var appearanceManager = new AppearanceManager(
                new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(rootDir)),
                presetManager,
                colorService,
                pinController);
            await appearanceManager.InitializeAsync();
            var diagService = new DiagnosticLogService(IoPath.Combine(rootDir, "logs"));

            var clipAdapter = Substitute.For<IClipboardMonitorAdapter>();
            var inboxAdapter = Substitute.For<INotificationListenerAdapter>();
            inboxAdapter.GetAccessStateAsync(Arg.Any<CancellationToken>()).Returns(NotificationAccessState.Allowed);

            await using (var notesModule = new NotesWidgetModule(noteRepo, clipAdapter, () => now))
            await using (var clipModule = new ClipboardWidgetModule(clipAdapter, clipRepo, prefsService, () => now))
            await using (var inboxModule = new InboxWidgetModule(inboxAdapter, inboxRepo, prefsService, utcNowProvider: () => now))
            {
                var dummyContext = CreateDummyWidgetContext("widget");
                await notesModule.InitializeAsync(dummyContext, CancellationToken.None);
                await clipModule.InitializeAsync(dummyContext, CancellationToken.None);
                await inboxModule.InitializeAsync(dummyContext, CancellationToken.None);

                await notesModule.ViewModel.CreateNewNoteAsync();
                notesModule.ViewModel.EditorContent = string.Join(
                    Environment.NewLine,
                    Enumerable.Range(1, 15).Select(l => $"eyes cant lie line {l}"));
                (await notesModule.ViewModel.SaveActiveNoteAsync()).Should().BeTrue();
                (await noteRepo.GetAllAsync()).Should().HaveCount(7);
                notesModule.ViewModel.NoteCountLabel.Should().Be("7 notes");

                using var host = new StaWpfTestHost();
                var (settingsScrollBars, widgetScrollBars, lightThumbColor, darkThumbColor) = await host.RunAsync(() =>
                {
                    var themeDict = new ResourceDictionary
                    {
                        Source = new Uri("/Shima.Presentation;component/Theming/ThemeTokens.xaml", UriKind.Relative)
                    };
                    if (Application.Current is not null
                        && !Application.Current.Resources.MergedDictionaries.Any(d => d.Source == themeDict.Source))
                    {
                        Application.Current.Resources.MergedDictionaries.Add(themeDict);
                    }

                    var themeRenderer = new ThemeRenderer(appearanceManager, colorService, Dispatcher.CurrentDispatcher);
                    themeRenderer.AttachResourceDictionary(themeDict);

                    // Dark theme check
                    themeRenderer.ApplyNow();
                    var darkThumb = ((SolidColorBrush)themeDict["ScrollBarThumbBrush"]).Color;

                    // Simulate light expanded surface palette check
                    var lightSession = appearanceManager.BeginEditingSession(acquireLivePreviewPin: false);
                    lightSession.UpdateToken(AppearanceTokenSlot.Background, IslandStateTarget.Expanded, "#F8FAFC");
                    themeRenderer.ApplyNow();
                    var lightThumb = ((SolidColorBrush)themeDict["ScrollBarThumbBrush"]).Color;
                    lightSession.Cancel();
                    themeRenderer.ApplyNow();

                    var settings = new SettingsWindow(
                        appearanceManager,
                        presetManager,
                        colorService,
                        prefsService,
                        diagService,
                        themeRenderer: themeRenderer)
                    {
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Left = -20000,
                        Top = -20000
                    };
                    settings.Show();
                    settings.SelectSection("General");
                    Find<TextBox>(settings, "DiagnosticsLogViewer").Text = string.Join(
                        Environment.NewLine,
                        Enumerable.Range(1, 30).Select(i => $"Log {i}: " + new string('X', 240)));
                    settings.UpdateLayout();

                    var settingsBars = InspectScrollBars(settings);

                    var notesView = (NotesView)notesModule.CreateExpandedView();
                    var inboxView = (InboxView)inboxModule.CreateExpandedView();
                    var clipView = (ClipboardView)clipModule.CreateExpandedView();

                    var widgetBarSummaries = new List<(string ViewName, Orientation Orientation, double Width, int ArrowButtons, double PillCornerRadius, bool IsVisible)>();
                    foreach (var (name, ctrl) in new (string, UserControl)[]
                             {
                                 ("Notes", notesView),
                                 ("Inbox", inboxView),
                                 ("Clipboard", clipView)
                             })
                    {
                        var hostWindow = new Window
                        {
                            Width = 348,
                            Height = 140,
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            ShowInTaskbar = false,
                            ShowActivated = false,
                            Left = -20000,
                            Top = -20000,
                            Content = ctrl
                        };
                        hostWindow.Resources.MergedDictionaries.Add(themeDict);
                        hostWindow.Show();
                        hostWindow.UpdateLayout();

                        widgetBarSummaries.AddRange(InspectScrollBars(hostWindow).Select(b => (name, b.Orientation, b.Width, b.ArrowButtons, b.PillCornerRadius, b.IsVisible)));
                        hostWindow.Close();
                    }

                    settings.Close();
                    return (settingsBars, widgetBarSummaries, lightThumb, darkThumb);
                });

                lightThumbColor.Should().NotBe(darkThumbColor);

                settingsScrollBars.Should().NotBeEmpty();
                settingsScrollBars.Should().Contain(b => b.Orientation == Orientation.Vertical && b.IsVisible);
                settingsScrollBars.Should().Contain(b => b.Orientation == Orientation.Horizontal && b.IsVisible);
                foreach (var bar in settingsScrollBars)
                {
                    bar.Width.Should().BeLessOrEqualTo(6.5);
                    bar.ArrowButtons.Should().Be(0);
                    bar.PillCornerRadius.Should().Be(3.0);
                }

                // Notes has both list ScrollViewer + editor TextBox ScrollViewer; Inbox & Clipboard also have ScrollViewers
                widgetScrollBars.Select(b => b.ViewName).Distinct().Should().Contain(["Notes", "Inbox", "Clipboard"]);
                widgetScrollBars.Count(b => b.ViewName == "Notes" && b.Orientation == Orientation.Vertical && b.IsVisible).Should().Be(2);
                widgetScrollBars.Should().Contain(b => b.ViewName == "Inbox" && b.Orientation == Orientation.Vertical && b.IsVisible);
                widgetScrollBars.Should().Contain(b => b.ViewName == "Clipboard" && b.Orientation == Orientation.Vertical && b.IsVisible);
                foreach (var bar in widgetScrollBars)
                {
                    bar.Width.Should().BeLessOrEqualTo(6.5);
                    bar.ArrowButtons.Should().Be(0);
                    bar.PillCornerRadius.Should().Be(3.0);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    private static IReadOnlyList<(Orientation Orientation, double Width, int ArrowButtons, double PillCornerRadius, bool IsVisible)> InspectScrollBars(DependencyObject root)
    {
        var results = new List<(Orientation Orientation, double Width, int ArrowButtons, double PillCornerRadius, bool IsVisible)>();
        foreach (var bar in FindAllDescendants<ScrollBar>(root))
        {
            bar.ApplyTemplate();
            var arrowCount = FindAllDescendants<RepeatButton>(bar)
                .Count(rb => rb.Command == ScrollBar.LineUpCommand
                          || rb.Command == ScrollBar.LineDownCommand
                          || rb.Command == ScrollBar.LineLeftCommand
                          || rb.Command == ScrollBar.LineRightCommand);

            var thumb = FindAllDescendants<Thumb>(bar).FirstOrDefault();
            thumb?.ApplyTemplate();
            var corner = thumb is not null
                ? FindAllDescendants<Border>(thumb).Select(b => b.CornerRadius.TopLeft).FirstOrDefault()
                : 0.0;

            var dim = bar.Orientation == Orientation.Vertical ? bar.Width : bar.Height;
            results.Add((bar.Orientation, dim, arrowCount, corner, bar.IsVisible));
        }

        return results;
    }

    private static IEnumerable<T> FindAllDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindAllDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IWidgetContext CreateDummyWidgetContext(string widgetId)
    {
        var store = Substitute.For<IWidgetSettingsStore>();
        store.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns("{}");
        var ctx = Substitute.For<IWidgetContext>();
        ctx.WidgetId.Returns(widgetId);
        ctx.Events.Returns(Substitute.For<IEventDispatcher>());
        ctx.Settings.Returns(store);
        ctx.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        ctx.Island.Returns(Substitute.For<IIslandAttentionRequester>());
        ctx.Logger.Returns(Substitute.For<ILogger>());
        return ctx;
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T
            ?? throw new InvalidOperationException($"Named element '{name}' of type {typeof(T).Name} was not found.");

    private static T FindByAutomationId<T>(DependencyObject root, string automationId) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is T typed
                && string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(current), automationId, StringComparison.Ordinal))
            {
                return typed;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(current, i));
            }
        }

        throw new InvalidOperationException($"Element with AutomationId '{automationId}' was not found.");
    }

    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
        {
            invoker.Invoke();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<Task> pump)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await pump();
            await Task.Delay(15);
        }

        await pump();
        condition().Should().BeTrue();
    }

    private sealed class StaWpfTestHost : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public StaWpfTestHost()
        {
            _thread = new Thread(() =>
            {
                if (Application.Current is null)
                {
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcherReady.SetResult(dispatcher);
                Dispatcher.Run();
            })
            {
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public async Task<T> RunAsync<T>(Func<T> func)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(func).Task.ConfigureAwait(false);
        }

        public async Task RunAsync(Action action)
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
        }

        public async Task PumpAsync()
        {
            var dispatcher = await _dispatcherReady.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_dispatcherReady.Task.IsCompletedSuccessfully)
            {
                var dispatcher = _dispatcherReady.Task.Result;
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }

            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
