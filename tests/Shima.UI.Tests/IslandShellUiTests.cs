using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.UIA3;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Presentation.Shell;
using Shima.Widgets.Developer;
using Shima.Widgets.Developer.Views;
using Shima.Widgets.NowPlaying;
using Shima.Widgets.NowPlaying.Views;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;
using Xunit;

namespace Shima.UI.Tests;

[CollectionDefinition("IslandShellUi", DisableParallelization = true)]
public sealed class IslandShellUiCollectionDefinition;

[Collection("IslandShellUi")]
public sealed class IslandShellUiTests
{
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [Fact]
    public async Task IslandWindow_AppliesNonActivatingStyles_ExposesUiaTree_AndNeverStealsForegroundFocus()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host));

        await host.RunAsync(() =>
        {
            harness.HostWindow.Show();
            harness.HostWindow.Activate();
            harness.IslandWindow.Show();
        });

        await host.PumpAsync();

        var styles = await host.RunAsync(() =>
        {
            var islandHwnd = harness.IslandWindow.WindowHandle;
            return GetWindowLong(islandHwnd, GwlExStyle);
        });

        (styles & WsExTopmost).Should().NotBe(0);
        (styles & WsExToolWindow).Should().NotBe(0);
        (styles & WsExNoActivate).Should().NotBe(0);

        var (hostHwnd, islandHwnd, initialForeground) = await host.RunAsync(() => (
            new WindowInteropHelper(harness.HostWindow).Handle,
            harness.IslandWindow.WindowHandle,
            GetForegroundWindow()));

        // Verify FlaUI UIA3 exposes the shell automation element and carousel dot buttons
        using var automation = new UIA3Automation();
        var islandElement = automation.FromHandle(islandHwnd);
        islandElement.Should().NotBeNull();
        islandElement.Name.Should().Be("Shima Dynamic Island");

        await host.RunAsync(() => harness.StateManager.RequestUserExpand());
        await host.PumpAsync();

        var afterExpandForeground = GetForegroundWindow();
        afterExpandForeground.Should().NotBe(islandHwnd);
        if (initialForeground == hostHwnd)
        {
            afterExpandForeground.Should().Be(hostHwnd);
        }

        // Verify UIA finds action-verb labeled widget carousel dots once expanded
        var clockDot = islandElement.FindFirstDescendant(cf => cf.ByAutomationId("WidgetDot_shima.clock"));
        clockDot.Should().NotBeNull();
        clockDot!.Name.Should().Be("Switch to Clock widget");

        // Switch active widget via UIA / WidgetHost and collapse
        await host.RunAsync(() => harness.IslandWindow.WidgetHost.SelectWidget("shima.nowplaying"));
        await host.PumpAsync();
        harness.IslandWindow.WidgetHost.SelectedWidgetId.Should().Be("shima.nowplaying");

        var afterWidgetSwitchForeground = GetForegroundWindow();
        afterWidgetSwitchForeground.Should().NotBe(islandHwnd);

        await host.RunAsync(() => harness.StateManager.RequestUserCollapse());
        await host.PumpAsync();
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Collapsed);
    }

    [Fact]
    public async Task IslandWindow_HidesCompletelyOnFullscreen_AndSummonsViaGlobalHotkey()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host));

        await host.RunAsync(() =>
        {
            harness.HostWindow.Show();
            harness.IslandWindow.Show();
        });
        await host.PumpAsync();

        var islandHwnd = await host.RunAsync(() => harness.IslandWindow.WindowHandle);
        IsWindowVisible(islandHwnd).Should().BeTrue();

        // Enter fullscreen on host monitor -> window completely hidden via SW_HIDE (zero Y=0 hit-box)
        await host.RunAsync(() =>
            harness.FullscreenDetector.FullscreenStateChanged += Raise.Event<EventHandler<FullscreenStateChangedEventArgs>>(
                harness.FullscreenDetector,
                new FullscreenStateChangedEventArgs(harness.PrimaryMonitor.Handle, true)));
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Fullscreen);
        IsWindowVisible(islandHwnd).Should().BeFalse();

        // Normal expand attempt remains suppressed while fullscreen is active
        await host.RunAsync(() => harness.StateManager.RequestUserExpand());
        await host.PumpAsync();
        IsWindowVisible(islandHwnd).Should().BeFalse();

        // Global hotkey summons Expanded cleanly over fullscreen without activating overlay
        await host.RunAsync(() =>
            harness.HotkeyService.HotkeyPressed += Raise.Event<EventHandler>(harness.HotkeyService, EventArgs.Empty));
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);
        IsWindowVisible(islandHwnd).Should().BeTrue();
        GetForegroundWindow().Should().NotBe(islandHwnd);

        // Toggle hotkey again returns immediately to Fullscreen hard hide
        await host.RunAsync(() =>
            harness.HotkeyService.HotkeyPressed += Raise.Event<EventHandler>(harness.HotkeyService, EventArgs.Empty));
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Fullscreen);
        IsWindowVisible(islandHwnd).Should().BeFalse();
    }

    [Fact]
    public async Task IslandWindow_Pinning_BlocksAutoCollapse_AndRestoresOnRelease()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host));

        await host.RunAsync(() => harness.IslandWindow.Show());
        await host.PumpAsync();

        var islandHwnd = await host.RunAsync(() => harness.IslandWindow.WindowHandle);

        var pin = await host.RunAsync(() =>
            harness.PinController.AcquirePin(PinReason.ColorPickerPreview, PreviewTargetState.Expanded));
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);
        IsWindowVisible(islandHwnd).Should().BeTrue();

        // Simulate cursor leaving island -> must stay Expanded while Color Picker pin is held
        await host.RunAsync(() => harness.StateManager.RequestUserCollapse());
        await host.PumpAsync();
        harness.StateManager.CurrentState.Should().Be(IslandShellState.Expanded);

        // Release pin and collapse cleanly back to 136x28 resting pill
        await host.RunAsync(() =>
        {
            harness.PinController.ReleasePin(pin);
            harness.StateManager.RequestUserCollapse();
        });
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Collapsed);
        var (width, height) = await host.RunAsync(() => (harness.IslandWindow.Width, harness.IslandWindow.Height));
        width.Should().BeApproximately(136, 0.5);
        height.Should().BeApproximately(28, 0.5);
    }

    [Fact]
    public async Task IslandWindow_CompactPillAndCollapsedOverlay_ApplySymmetricConcaveWallInsets()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host));

        await host.RunAsync(() => harness.IslandWindow.Show());
        await host.PumpAsync();

        var (collapsedPadding, activePadding, activeSpec, activeMargin, collapsedPresenterVis) = await host.RunAsync(() =>
        {
            harness.IslandWindow.SetCollapsedStatusContent(null);
            var collapsedVis = ((FrameworkElement)harness.IslandWindow.FindName("CollapsedOverlayContentPresenter")).Visibility;
            harness.StateManager.SetCompactActive("shima.nowplaying", isActive: true, badgeText: "Playing");
            return (
                (Thickness)harness.IslandWindow.FindResource("IslandCollapsedOverlayPadding"),
                (Thickness)harness.IslandWindow.FindResource("IslandActivePillPadding"),
                ConcaveNotchGeometryBuilder.ActiveSpec,
                ((FrameworkElement)harness.IslandWindow.FindName("ActiveLayer")).Margin,
                collapsedVis);
        });
        await host.PumpAsync();

        collapsedPresenterVis.Should().Be(Visibility.Collapsed);
        collapsedPadding.Left.Should().Be(collapsedPadding.Right);
        (collapsedPadding.Left - ConcaveNotchGeometryBuilder.CollapsedSpec.EarRadiusDip).Should().BeGreaterOrEqualTo(10);

        activePadding.Left.Should().Be(activePadding.Right);
        activeMargin.Should().Be(activePadding);
        (activeMargin.Left - activeSpec.EarRadiusDip).Should().BeGreaterOrEqualTo(14);
        activeMargin.Left.Should().BeGreaterOrEqualTo(activeSpec.EarRadiusDip + activeSpec.BottomRadiusDip);

        var (activeW, activeH) = await host.RunAsync(() =>
            (harness.IslandWindow.ActualWidth, harness.IslandWindow.ActualHeight));
        activeW.Should().BeApproximately(activeSpec.WidthDip, 1.0);
        activeH.Should().BeApproximately(activeSpec.HeightDip, 1.0);
    }

    [Fact]
    public async Task IslandWindow_MediaWithActiveDeveloperAgent_RendersRightSideGreenCompanionAndRevertsSmoothly()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host, reducedMotion: false));
        await using var compHarness = await CreateMediaAndDeveloperHarnessAsync(harness);

        await host.RunAsync(() => harness.IslandWindow.Show());
        await host.PumpAsync();

        // 1. Media playing alone -> NowPlayingCompactView fills pill width with right accent dot visible
        compHarness.EmitMediaPlaying("Midnight City", "M83");
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Active);
        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();

        var (mediaOnlyWidth, initialDotVis, initialCompanionVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            return (nowPlayingView.ActualWidth, dot.Visibility, companionHost.Visibility);
        });
        initialDotVis.Should().Be(Visibility.Visible);
        initialCompanionVis.Should().Be(Visibility.Collapsed);

        // 2. Developer activity starts Working -> green companion pill animates in on right, replaces dot, music shrinks cleanly on left
        compHarness.EmitDeveloperSession(AgentStatus.Working, isFatalError: false);
        await Task.Delay(320);
        await host.PumpAsync();

        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().Be(DeveloperWidgetModule.Id);

        var (mediaWithCompanionWidth, activeDotVis, companionVis, companionWidth, companionOpacity, artworkRightX, companionLeftX) =
            await host.RunAsync(() =>
            {
                var activeLayer = (FrameworkElement)harness.IslandWindow.FindName("ActiveLayer");
                var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
                var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
                var artwork = (FrameworkElement)nowPlayingView.FindName("CompactArtworkBorder");
                var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
                var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
                var companionPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
                companionPresenter.Content.Should().BeOfType<DeveloperCompactCompanionView>();

                var artRight = artwork.TranslatePoint(new Point(artwork.ActualWidth, 0), activeLayer).X;
                var compLeft = companionHost.TranslatePoint(new Point(0, 0), activeLayer).X;
                return (
                    nowPlayingView.ActualWidth,
                    dot.Visibility,
                    companionHost.Visibility,
                    companionHost.ActualWidth,
                    companionHost.Opacity,
                    artRight,
                    compLeft);
            });

        activeDotVis.Should().Be(Visibility.Collapsed);
        companionVis.Should().Be(Visibility.Visible);
        companionWidth.Should().BeGreaterThan(25.0);
        companionOpacity.Should().BeApproximately(1.0, 0.05);
        artworkRightX.Should().BeLessThan(companionLeftX);
        mediaWithCompanionWidth.Should().BeLessThan(mediaOnlyWidth);
        compHarness.DeveloperModule.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Working);
        compHarness.DeveloperModule.ViewModel.BarFillBrushKey.Should().Be("AgentStatusWorkingBrush");

        // 3. Urgent attention (approval needed) still takes primary compact pill priority over Media
        compHarness.EmitDeveloperSession(AgentStatus.AwaitingApproval, isFatalError: false);
        await host.PumpAsync();

        harness.StateManager.CompactActiveWidgetId.Should().Be(DeveloperWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();
        await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            activePresenter.Content.Should().BeOfType<DeveloperCompactView>();
            companionHost.Visibility.Should().Be(Visibility.Collapsed);
            harness.StateManager.ClearNotificationPeek();
        });
        await host.PumpAsync();

        // 4. Returning to Working restores Media on left + green Agent pill on right
        compHarness.EmitDeveloperSession(AgentStatus.Working, isFatalError: false);
        await Task.Delay(320);
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Active);
        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().Be(DeveloperWidgetModule.Id);

        // 5. Developer finishes -> hover acknowledges bar to Idle -> companion smoothly collapses and music expands back
        compHarness.EmitDeveloperSession(AgentStatus.Finished, isFatalError: false);
        await host.PumpAsync();
        compHarness.DeveloperModule.OnCollapsedNotchHovered(); // Finished -> Idle immediately
        await Task.Delay(320);
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Active);
        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();

        var (revertedMediaWidth, revertedDotVis, revertedCompanionVis, revertedCompanionContent) =
            await host.RunAsync(() =>
            {
                harness.IslandWindow.UpdateLayout();
                var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
                var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
                var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
                var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
                var companionPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
                return (nowPlayingView.ActualWidth, dot.Visibility, companionHost.Visibility, companionPresenter.Content);
            });

        revertedCompanionVis.Should().Be(Visibility.Collapsed);
        revertedCompanionContent.Should().BeNull();
        revertedDotVis.Should().Be(Visibility.Visible);
        revertedMediaWidth.Should().BeApproximately(mediaOnlyWidth, 1.0);
    }

    [Fact]
    public async Task IslandWindow_ReducedMotionEnabled_SnapsCompanionOpenAndIdleRevertImmediately()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host, reducedMotion: true));
        await using var compHarness = await CreateMediaAndDeveloperHarnessAsync(harness);

        await host.RunAsync(() => harness.IslandWindow.Show());
        await host.PumpAsync();

        compHarness.EmitMediaPlaying("Starlight", "Muse");
        compHarness.EmitDeveloperSession(AgentStatus.Working, isFatalError: false);
        await host.PumpAsync();

        var (openWidth, openVis, openDotVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            return (companionHost.ActualWidth, companionHost.Visibility, dot.Visibility);
        });

        openVis.Should().Be(Visibility.Visible);
        openWidth.Should().BeGreaterThan(25.0);
        openDotVis.Should().Be(Visibility.Collapsed);

        compHarness.EmitDeveloperSession(AgentStatus.Finished, isFatalError: false);
        compHarness.DeveloperModule.OnCollapsedNotchHovered();
        await host.PumpAsync(); // Single pump with zero animation delay

        var (closedVis, closedContent, closedDotVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var companionHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var companionPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
            return (companionHost.Visibility, companionPresenter.Content, dot.Visibility);
        });

        closedVis.Should().Be(Visibility.Collapsed);
        closedContent.Should().BeNull();
        closedDotVis.Should().Be(Visibility.Visible);
    }

    private static UiTestHarness CreateHarness(StaWpfTestHost host, bool reducedMotion = true)
    {
        using var realTopology = new DisplayTopologyMonitor();
        var primaryMonitor = realTopology.GetPrimaryMonitor();

        var displayMonitor = Substitute.For<IDisplayTopologyMonitor>();
        displayMonitor.GetMonitors().Returns([primaryMonitor]);
        displayMonitor.GetPrimaryMonitor().Returns(primaryMonitor);
        displayMonitor.ResolveTargetMonitor(Arg.Any<MonitorSelectionMode>(), Arg.Any<string?>()).Returns(primaryMonitor);

        var fullscreenDetector = Substitute.For<IFullscreenDetector>();
        fullscreenDetector.IsMonitorFullscreen(Arg.Any<nint>()).Returns(false);

        var hotkeyService = Substitute.For<IGlobalHotkeyService>();
        var powerMonitor = Substitute.For<IPowerAndMotionMonitor>();
        powerMonitor.CurrentPowerProfile.Returns(PowerProfile.AC);
        powerMonitor.IsReducedMotionEnabled.Returns(reducedMotion);
        powerMonitor.IsSessionLocked.Returns(false);

        var pinController = new OverlayPinController();
        var stateManager = new IslandStateManager(pinController);
        var overlayAdapter = new OverlayWindowAdapter();

        var islandWindow = new IslandWindow(
            stateManager,
            pinController,
            overlayAdapter,
            displayMonitor,
            fullscreenDetector,
            hotkeyService,
            powerMonitor);
        islandWindow.ApplyBehaviorPreferences(
            MonitorSelectionMode.Primary,
            specificMonitorDeviceId: null,
            expandOnHover: false,
            hoverExpandDelayMs: 350,
            autoCollapseDelayMs: 600,
            fullscreenBreakthroughAlerts: false);

        islandWindow.SetWidgetSlots(
        [
            new WidgetSlotItem("shima.clock", "Clock"),
            new WidgetSlotItem("shima.nowplaying", "Now Playing"),
            new WidgetSlotItem("shima.focustimer", "Focus Timer")
        ],
        "shima.clock");

        var hostWindow = new Window
        {
            Title = "TestForegroundHost",
            Width = 320,
            Height = 200,
            Left = 40,
            Top = 260
        };

        return new UiTestHarness(
            host,
            primaryMonitor,
            pinController,
            stateManager,
            fullscreenDetector,
            hotkeyService,
            islandWindow,
            hostWindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private sealed class UiTestHarness(
        StaWpfTestHost host,
        MonitorInfo primaryMonitor,
        OverlayPinController pinController,
        IslandStateManager stateManager,
        IFullscreenDetector fullscreenDetector,
        IGlobalHotkeyService hotkeyService,
        IslandWindow islandWindow,
        Window hostWindow) : IAsyncDisposable
    {
        public MonitorInfo PrimaryMonitor { get; } = primaryMonitor;

        public OverlayPinController PinController { get; } = pinController;

        public IslandStateManager StateManager { get; } = stateManager;

        public IFullscreenDetector FullscreenDetector { get; } = fullscreenDetector;

        public IGlobalHotkeyService HotkeyService { get; } = hotkeyService;

        public IslandWindow IslandWindow { get; } = islandWindow;

        public Window HostWindow { get; } = hostWindow;

        public async ValueTask DisposeAsync()
        {
            await host.RunAsync(() =>
            {
                IslandWindow.Close();
                HostWindow.Close();
                StateManager.Dispose();
            }).ConfigureAwait(false);
        }
    }

    private static async Task<MediaDeveloperUiHarness> CreateMediaAndDeveloperHarnessAsync(UiTestHarness harness)
    {
        var mediaAdapter = Substitute.For<IMediaSessionAdapter>();
        var timer = Substitute.For<INowPlayingTimer>();
        var nowPlayingModule = new NowPlayingWidgetModule(mediaAdapter, progressTimer: timer);

        var agentAdapter = Substitute.For<ICodingAgentAdapter>();
        agentAdapter.AdapterId.Returns("opencode");
        agentAdapter.DisplayName.Returns("OpenCode");
        agentAdapter.ConnectionState.Returns(AgentConnectionState.Connected);
        agentAdapter.Capabilities.Returns(new AgentCapabilities(true, true, false, true, true, []));
        agentAdapter.CheckHealthAsync(Arg.Any<CancellationToken>())
            .Returns(new AdapterHealthResult(AdapterHealthStatus.Healthy, "Connected"));
        agentAdapter.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        agentAdapter.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var logRepo = Substitute.For<IAgentSessionLogRepository>();
        logRepo.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AgentSessionLogEntry>());
        logRepo.PruneFinishedSessionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(0);

        var prefs = Substitute.For<IAppPreferencesService>();
        prefs.Current.Returns(new AppPreferencesSnapshot { OpenCodeOptInEnabled = true });

        var developerModule = new DeveloperWidgetModule(
            [agentAdapter],
            logRepo,
            prefs,
            Substitute.For<IOneShotTimer>(),
            Substitute.For<IOneShotTimer>(),
            Substitute.For<IOneShotTimer>());

        var requester = new IslandAttentionRequester(harness.StateManager);
        await nowPlayingModule.InitializeAsync(CreateMockWidgetContext(NowPlayingWidgetModule.Id, requester), CancellationToken.None)
            .ConfigureAwait(false);
        await developerModule.InitializeAsync(CreateMockWidgetContext(DeveloperWidgetModule.Id, requester), CancellationToken.None)
            .ConfigureAwait(false);

        return new MediaDeveloperUiHarness(
            harness,
            mediaAdapter,
            agentAdapter,
            nowPlayingModule,
            developerModule);
    }

    private static IWidgetContext CreateMockWidgetContext(string widgetId, IIslandAttentionRequester requester)
    {
        var context = Substitute.For<IWidgetContext>();
        var settings = Substitute.For<IWidgetSettingsStore>();
        settings.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns("{}");

        context.WidgetId.Returns(widgetId);
        context.Events.Returns(Substitute.For<IEventDispatcher>());
        context.Settings.Returns(settings);
        context.Island.Returns(requester);
        context.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        context.Logger.Returns(Substitute.For<ILogger>());
        return context;
    }

    private sealed class MediaDeveloperUiHarness : IAsyncDisposable
    {
        private readonly UiTestHarness _shellHarness;
        private readonly IMediaSessionAdapter _mediaAdapter;
        private readonly ICodingAgentAdapter _agentAdapter;
        private readonly NowPlayingWidgetModule _nowPlayingModule;
        private readonly Dictionary<string, IWidgetModule> _modulesById;
        private string? _cachedPrimaryId;
        private object? _cachedPrimaryView;
        private string? _cachedCompanionId;
        private object? _cachedCompanionView;

        public MediaDeveloperUiHarness(
            UiTestHarness shellHarness,
            IMediaSessionAdapter mediaAdapter,
            ICodingAgentAdapter agentAdapter,
            NowPlayingWidgetModule nowPlayingModule,
            DeveloperWidgetModule developerModule)
        {
            _shellHarness = shellHarness;
            _mediaAdapter = mediaAdapter;
            _agentAdapter = agentAdapter;
            _nowPlayingModule = nowPlayingModule;
            DeveloperModule = developerModule;
            _modulesById = new Dictionary<string, IWidgetModule>(StringComparer.Ordinal)
            {
                [NowPlayingWidgetModule.Id] = _nowPlayingModule,
                [DeveloperWidgetModule.Id] = DeveloperModule
            };

            _shellHarness.StateManager.CompactPillUpdated += OnCompactPillUpdated;
            _shellHarness.IslandWindow.Dispatcher.BeginInvoke(SyncCompactViews);
        }

        public DeveloperWidgetModule DeveloperModule { get; }

        public void EmitMediaPlaying(string title, string artist)
        {
            var snapshot = new MediaSessionSnapshot
            {
                SourceAppId = "Spotify.exe",
                Title = title,
                Artist = artist,
                PlaybackState = MediaPlaybackState.Playing,
                CanPlayPause = true,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };
            _mediaAdapter.CurrentSession.Returns(snapshot);
            _mediaAdapter.SessionChanged += Raise.Event<EventHandler<MediaSessionSnapshot?>>(_mediaAdapter, snapshot);
        }

        public void EmitDeveloperSession(AgentStatus status, bool isFatalError)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = new AgentSessionSnapshot(
                SessionId: "opencode:ui-1",
                AdapterId: "opencode",
                WorkspaceName: "shima",
                WorkspacePath: @"C:\repos\shima",
                ProcessId: null,
                Status: status,
                IsFatalError: isFatalError,
                ConnectionState: AgentConnectionState.Connected,
                TaskSummary: status == AgentStatus.AwaitingApproval ? "Approve Bash" : "Implementing companion pill",
                PendingToolName: status == AgentStatus.AwaitingApproval ? "Bash" : null,
                PendingActionSummary: status == AgentStatus.AwaitingApproval ? "dotnet test" : null,
                PendingRequestId: status == AgentStatus.AwaitingApproval ? "req_ui_1" : null,
                HasActionablePermissionRequest: status == AgentStatus.AwaitingApproval,
                StartedAtUtc: now.AddMinutes(-1),
                LastUpdatedAtUtc: now);
            _agentAdapter.SessionUpdated += Raise.Event<EventHandler<AgentSessionSnapshot>>(_agentAdapter, snapshot);
        }

        public async ValueTask DisposeAsync()
        {
            _shellHarness.StateManager.CompactPillUpdated -= OnCompactPillUpdated;
            await _nowPlayingModule.DisposeAsync().ConfigureAwait(false);
            await DeveloperModule.DisposeAsync().ConfigureAwait(false);
        }

        private void OnCompactPillUpdated(object? sender, EventArgs e) =>
            _shellHarness.IslandWindow.Dispatcher.BeginInvoke(SyncCompactViews);

        private void SyncCompactViews()
        {
            var primaryId = _shellHarness.StateManager.CompactActiveWidgetId;
            if (string.IsNullOrWhiteSpace(primaryId) || !_modulesById.TryGetValue(primaryId, out var primaryModule))
            {
                _cachedPrimaryId = null;
                _cachedPrimaryView = null;
                _cachedCompanionId = null;
                _cachedCompanionView = null;
                _shellHarness.IslandWindow.SetCompactActiveContent(null);
                _shellHarness.IslandWindow.SetCompactCompanionContent(null);
                return;
            }

            if (!string.Equals(_cachedPrimaryId, primaryId, StringComparison.Ordinal))
            {
                _cachedPrimaryId = primaryId;
                _cachedPrimaryView = primaryModule.CreateCompactActiveView();
            }

            var companionId = _shellHarness.StateManager.CompactCompanionWidgetId;
            if (string.IsNullOrWhiteSpace(companionId)
                || !_modulesById.TryGetValue(companionId, out var companionModule)
                || companionModule is not ICompactCompanionViewProvider companionProvider)
            {
                _cachedCompanionId = null;
                _cachedCompanionView = null;
            }
            else if (!string.Equals(_cachedCompanionId, companionId, StringComparison.Ordinal))
            {
                _cachedCompanionId = companionId;
                _cachedCompanionView = companionProvider.CreateCompactCompanionView();
            }

            _shellHarness.IslandWindow.SetCompactActiveContent(_cachedPrimaryView);
            _shellHarness.IslandWindow.SetCompactCompanionContent(_cachedCompanionView);
        }
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
