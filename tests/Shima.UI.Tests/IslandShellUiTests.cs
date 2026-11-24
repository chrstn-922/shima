using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.UIA3;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Infrastructure.Appearance;
using Shima.Infrastructure.Color;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Storage;
using Shima.Presentation.Settings;
using Shima.Presentation.Shell;
using Shima.Presentation.Theming;
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
    public async Task IslandWindow_MediaWithActiveDeveloperAgent_RendersBottomEdgeCompanionBarAcrossModesAndStates()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host, reducedMotion: false));
        await using var compHarness = await CreateMediaAndDeveloperHarnessAsync(harness);

        await host.RunAsync(() => harness.IslandWindow.Show());
        await host.PumpAsync();

        // 1. Media playing alone -> NowPlayingCompactView intact, bottom-edge bar collapsed
        compHarness.EmitMediaPlaying("Midnight City", "M83");
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Active);
        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();

        var (mediaOnlyWidth, initialDotVis, initialCompVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            return (nowPlayingView.ActualWidth, dot.Visibility, compHost.Visibility);
        });
        initialDotVis.Should().Be(Visibility.Visible);
        initialCompVis.Should().Be(Visibility.Collapsed);

        // 2. Working -> green slim bar tucked along bottom edge inside pill, Now Playing intact, zero extra text, sweep active
        compHarness.EmitDeveloperSession(AgentStatus.Working, isFatalError: false);
        await Task.Delay(260);
        await host.PumpAsync();

        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().Be(DeveloperWidgetModule.Id);

        var (mediaWithCompWidth, activeDotVis, compVis, compOpacity, compRectInMain, mainSize, compTextCount, isWorkingSweep) =
            await host.RunAsync(() =>
            {
                harness.IslandWindow.UpdateLayout();
                var mainHost = (FrameworkElement)harness.IslandWindow.FindName("MainIslandSurfaceHost");
                var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
                var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
                var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
                var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
                var compPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
                var compView = (DeveloperCompactCompanionView)compPresenter.Content;

                var topLeft = compHost.TranslatePoint(new Point(0, 0), mainHost);
                var bottomRight = compHost.TranslatePoint(new Point(compHost.ActualWidth, compHost.ActualHeight), mainHost);
                return (
                    nowPlayingView.ActualWidth,
                    dot.Visibility,
                    compHost.Visibility,
                    compHost.Opacity,
                    new Rect(topLeft, bottomRight),
                    new Size(mainHost.ActualWidth, mainHost.ActualHeight),
                    CountDescendants<TextBlock>(compView),
                    compView.IsWorkingSweepActive);
            });

        activeDotVis.Should().Be(Visibility.Visible);
        mediaWithCompWidth.Should().BeApproximately(mediaOnlyWidth, 1.0);
        compVis.Should().Be(Visibility.Visible);
        compOpacity.Should().BeApproximately(1.0, 0.05);
        compRectInMain.Left.Should().BeApproximately(28.0, 1.0);
        compRectInMain.Width.Should().BeApproximately(208.0, 1.0);
        compRectInMain.Bottom.Should().BeLessOrEqualTo(mainSize.Height - 1.5);
        compRectInMain.Bottom.Should().BeGreaterOrEqualTo(mainSize.Height - 5.0);
        compTextCount.Should().Be(0);
        isWorkingSweep.Should().BeTrue();
        compHarness.DeveloperModule.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Working);
        compHarness.DeveloperModule.ViewModel.BarFillBrushKey.Should().Be("AgentStatusWorkingBrush");

        // Switch to Floating Pill mode -> bottom-edge bar stays cleanly inside rounded floating capsule bounds
        await host.RunAsync(() => harness.IslandWindow.ApplyBehaviorPreferences(
            MonitorSelectionMode.Primary, null, false, 350, 600, false,
            IslandStyle.FloatingPill, lockIslandPosition: false, 0.0, null));
        await Task.Delay(260);
        await host.PumpAsync();

        (await host.RunAsync(() =>
        {
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            return compHost.Visibility;
        })).Should().Be(Visibility.Visible);

        // 3. Expanding hides bar smoothly; collapsing back restores it
        await host.RunAsync(() => harness.StateManager.RequestUserExpand());
        await Task.Delay(240);
        await host.PumpAsync();

        (await host.RunAsync(() => ((FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost")).Visibility))
            .Should().Be(Visibility.Collapsed);

        await host.RunAsync(() => harness.StateManager.RequestUserCollapse());
        await Task.Delay(240);
        await host.PumpAsync();

        (await host.RunAsync(() => ((FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost")).Visibility))
            .Should().Be(Visibility.Visible);

        // 4. Finished disappears right away (no lingering Finished dwell) while Now Playing stays intact
        compHarness.EmitDeveloperSession(AgentStatus.Finished, isFatalError: false);
        await Task.Delay(240);
        await host.PumpAsync();

        harness.StateManager.CurrentState.Should().Be(IslandShellState.Active);
        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();
        compHarness.DeveloperModule.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Idle);

        var (finishedVis, finishedContent, finishedDotVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var compPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
            return (compHost.Visibility, compPresenter.Content, dot.Visibility);
        });
        finishedVis.Should().Be(Visibility.Collapsed);
        finishedContent.Should().BeNull();
        finishedDotVis.Should().Be(Visibility.Visible);

        // 5. NeedsAttention / AwaitingApproval holds static amber bar alongside Now Playing (no sweep) until Developer viewed
        compHarness.EmitDeveloperSession(AgentStatus.AwaitingApproval, isFatalError: false);
        await Task.Delay(240);
        await host.PumpAsync();

        await host.RunAsync(() => harness.StateManager.ClearNotificationPeek());
        await host.PumpAsync();

        harness.StateManager.CompactActiveWidgetId.Should().Be(NowPlayingWidgetModule.Id);
        harness.StateManager.CompactCompanionWidgetId.Should().Be(DeveloperWidgetModule.Id);
        var (attnVis, attnSweep, attnBrushKey, attnColorHex) = await host.RunAsync(() =>
        {
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var compPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
            var compView = (DeveloperCompactCompanionView)compPresenter.Content;
            var bar = (ProgressBar)compView.FindName("CompanionProgressBar");
            var brush = (System.Windows.Media.SolidColorBrush)bar.Foreground;
            return (compHost.Visibility, compView.IsWorkingSweepActive, compHarness.DeveloperModule.ViewModel.BarFillBrushKey, brush.Color.ToString());
        });
        attnVis.Should().Be(Visibility.Visible);
        attnSweep.Should().BeFalse();
        attnBrushKey.Should().Be("AgentStatusAttentionBrush");
        attnColorHex.Should().Be("#FFF59E0B");

        // Viewing Developer Activity (ActivateAsync) clears the alert hold immediately
        await compHarness.DeveloperModule.ActivateAsync(WidgetActivationReason.UserSelection);
        await Task.Delay(240);
        await host.PumpAsync();

        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();
        (await host.RunAsync(() => ((FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost")).Visibility))
            .Should().Be(Visibility.Collapsed);

        // 6. ErrorFailed holds static red bar alongside Now Playing until fallback timeout expires
        compHarness.EmitDeveloperSession(AgentStatus.NeedsAttention, isFatalError: true);
        await Task.Delay(240);
        await host.PumpAsync();

        var (errVis, errSweep, errBrushKey) = await host.RunAsync(() =>
        {
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var compPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
            var compView = (DeveloperCompactCompanionView)compPresenter.Content;
            return (compHost.Visibility, compView.IsWorkingSweepActive, compHarness.DeveloperModule.ViewModel.BarFillBrushKey);
        });
        errVis.Should().Be(Visibility.Visible);
        errSweep.Should().BeFalse();
        errBrushKey.Should().Be("AgentStatusErrorBrush");

        compHarness.DeveloperModule.ViewModel.TriggerAlertTimeoutExpiredForTesting();
        await Task.Delay(240);
        await host.PumpAsync();

        harness.StateManager.CompactCompanionWidgetId.Should().BeNull();
        (await host.RunAsync(() => ((FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost")).Visibility))
            .Should().Be(Visibility.Collapsed);
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

        var (openVis, openOpacity, openDotVis, sweepActive) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var compView = (DeveloperCompactCompanionView)((ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter")).Content;
            return (compHost.Visibility, compHost.Opacity, dot.Visibility, compView.IsWorkingSweepActive);
        });

        openVis.Should().Be(Visibility.Visible);
        openOpacity.Should().Be(1.0);
        openDotVis.Should().Be(Visibility.Visible);
        sweepActive.Should().BeFalse();

        // Finished clears immediately in zero duration
        compHarness.EmitDeveloperSession(AgentStatus.Finished, isFatalError: false);
        await host.PumpAsync();

        var (closedVis, closedContent, closedDotVis) = await host.RunAsync(() =>
        {
            var activePresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCustomContentPresenter");
            var nowPlayingView = (NowPlayingCompactView)activePresenter.Content;
            var dot = (FrameworkElement)nowPlayingView.FindName("PlayingAccentDot");
            var compHost = (FrameworkElement)harness.IslandWindow.FindName("ActiveCompanionHost");
            var compPresenter = (ContentPresenter)harness.IslandWindow.FindName("ActiveCompanionContentPresenter");
            return (compHost.Visibility, compPresenter.Content, dot.Visibility);
        });

        closedVis.Should().Be(Visibility.Collapsed);
        closedContent.Should().BeNull();
        closedDotVis.Should().Be(Visibility.Visible);
    }

    [Fact]
    public async Task IslandWindow_StyleSwitchHoverLiftAndSettings_MorphsSmoothlyAndSnapsUnderReducedMotion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "shima-style-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var host = new StaWpfTestHost();
            var reducedMotion = false;
            await using var harness = await host.RunAsync(() =>
            {
                var h = CreateHarness(host, reducedMotion: false);
                h.PowerMonitor.IsReducedMotionEnabled.Returns(_ => reducedMotion);
                return h;
            });

            var prefsStore = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(tempDir));
            var prefsService = new AppPreferencesService(prefsStore, new DpapiSecretStore());
            await prefsService.InitializeAsync();

            var settingsWindow = await host.RunAsync(() =>
            {
                prefsService.PreferencesChanged += (_, updated) => harness.IslandWindow.Dispatcher.BeginInvoke(() =>
                    harness.IslandWindow.ApplyBehaviorPreferences(
                        updated.MonitorSelectionMode,
                        updated.SpecificMonitorDeviceId,
                        updated.ExpandOnHover,
                        updated.HoverExpandDelayMs,
                        updated.AutoCollapseDelayMs,
                        updated.FullscreenBreakthroughAlerts,
                        updated.IslandStyle,
                        updated.LockIslandPosition,
                        updated.IslandOffsetXDip,
                        updated.IslandFloatingTopOffsetDip));

                var colorService = new ColorProcessingService();
                var appearanceRepo = new AtomicAppearanceConfigRepository(new AtomicConfigStore<AppearanceConfiguration>(tempDir));
                var presetRepo = new AtomicPresetCatalogRepository(new AtomicConfigStore<PresetCatalog>(tempDir));
                var presets = new PresetManager(presetRepo, colorService);
                var themeManager = new AppearanceManager(appearanceRepo, presets, colorService, harness.PinController);
                var sw = new SettingsWindow(
                    themeManager,
                    presets,
                    colorService,
                    prefsService,
                    new DiagnosticLogService(Path.Combine(tempDir, "logs")));

                harness.IslandWindow.Show();
                sw.Show();
                return sw;
            });
            await host.PumpAsync();

            try
            {
                // Initial Docked Notch: Y flush with WorkArea.Top, floatingProgress = 0, no shadow
                var initialVisual = await host.RunAsync(() => (
                    harness.IslandWindow.CurrentIslandStyle,
                    harness.IslandWindow.CurrentFloatingProgress,
                    harness.IslandWindow.CurrentShadowOpacity,
                    harness.IslandWindow.CurrentVisualBoundsDip));
                initialVisual.CurrentIslandStyle.Should().Be(IslandStyle.DockedNotch);
                initialVisual.CurrentFloatingProgress.Should().Be(0.0);
                initialVisual.CurrentShadowOpacity.Should().Be(0.0);
                initialVisual.CurrentVisualBoundsDip.Top.Should().BeApproximately(
                    IslandPositionMath.GetEffectiveWorkArea(harness.PrimaryMonitor).Top / harness.PrimaryMonitor.DpiScale,
                    0.5);

                // Toggle to Floating Pill via Settings ComboBox -> live morph completes cleanly
                await host.RunAsync(() =>
                {
                    var styleCombo = (ComboBox)settingsWindow.FindName("IslandStyleCombo");
                    styleCombo.SelectedIndex = 1; // FloatingPill
                });
                await Task.Delay(320);
                await host.PumpAsync();

                prefsService.Current.IslandStyle.Should().Be(IslandStyle.FloatingPill);
                var floatingVisual = await host.RunAsync(() => (
                    harness.IslandWindow.CurrentIslandStyle,
                    harness.IslandWindow.CurrentFloatingProgress,
                    harness.IslandWindow.CurrentShadowOpacity,
                    harness.IslandWindow.CurrentVisualBoundsDip,
                    (System.Windows.Shapes.Path)harness.IslandWindow.FindName("NotchSurfacePath")));

                floatingVisual.CurrentIslandStyle.Should().Be(IslandStyle.FloatingPill);
                floatingVisual.CurrentFloatingProgress.Should().BeApproximately(1.0, 0.01);
                floatingVisual.CurrentShadowOpacity.Should().BeGreaterThan(0.25);
                var workTopDip = IslandPositionMath.GetEffectiveWorkArea(harness.PrimaryMonitor).Top / harness.PrimaryMonitor.DpiScale;
                floatingVisual.CurrentVisualBoundsDip.Top.Should().BeApproximately(
                    workTopDip + IslandPositionMath.DefaultFloatingTopMarginDip,
                    0.5);
                floatingVisual.CurrentVisualBoundsDip.Width.Should().BeApproximately(
                    ConcaveNotchGeometryBuilder.CollapsedSpec.WidthDip - 2 * ConcaveNotchGeometryBuilder.CollapsedSpec.EarRadiusDip,
                    0.5);

                // Verify Floating hover lift elevates on pointer enter and settles on leave
                await host.RunAsync(() => harness.IslandWindow.SetHoveringForTesting(true));
                await Task.Delay(220);
                await host.PumpAsync();

                var hoveredLift = await host.RunAsync(() => (
                    harness.IslandWindow.CurrentLiftScale,
                    harness.IslandWindow.CurrentShadowOpacity));
                hoveredLift.CurrentLiftScale.Should().BeApproximately(1.02, 0.005);
                hoveredLift.CurrentShadowOpacity.Should().BeApproximately(0.44, 0.02);

                await host.RunAsync(() => harness.IslandWindow.SetHoveringForTesting(false));
                await Task.Delay(220);
                await host.PumpAsync();

                (await host.RunAsync(() => harness.IslandWindow.CurrentLiftScale)).Should().BeApproximately(1.0, 0.005);

                // Toggle Lock Position & click Reset Position inside Settings
                await host.RunAsync(() =>
                {
                    var lockCheck = (CheckBox)settingsWindow.FindName("LockIslandPositionCheck");
                    lockCheck.IsChecked = true;
                });
                var lockDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (!prefsService.Current.LockIslandPosition && DateTime.UtcNow < lockDeadline)
                {
                    await Task.Delay(15);
                }
                prefsService.Current.LockIslandPosition.Should().BeTrue();

                // Interrupted morph: Floating -> Docked, then immediately back to Floating mid-flight (0 stuck state)
                await host.RunAsync(() =>
                {
                    var styleCombo = (ComboBox)settingsWindow.FindName("IslandStyleCombo");
                    styleCombo.SelectedIndex = 0; // Docked
                });
                await Task.Delay(60);
                await host.PumpAsync();
                await host.RunAsync(() =>
                {
                    var styleCombo = (ComboBox)settingsWindow.FindName("IslandStyleCombo");
                    styleCombo.SelectedIndex = 1; // Flip back mid-flight
                });
                await Task.Delay(320);
                await host.PumpAsync();

                (await host.RunAsync(() => (
                    harness.IslandWindow.CurrentFloatingProgress,
                    harness.IslandWindow.IsMotionAnimationActive))).Should().Be((1.0, false));

                // Under Reduced Motion, switching Docked <-> Floating snaps immediately with zero transition timer
                reducedMotion = true;
                await host.RunAsync(() =>
                {
                    var styleCombo = (ComboBox)settingsWindow.FindName("IslandStyleCombo");
                    styleCombo.SelectedIndex = 0;
                });
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while (prefsService.Current.IslandStyle != IslandStyle.DockedNotch && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(15);
                }
                await host.PumpAsync();

                var snappedDocked = await host.RunAsync(() => (
                    harness.IslandWindow.CurrentFloatingProgress,
                    harness.IslandWindow.CurrentShadowOpacity,
                    harness.IslandWindow.IsMotionAnimationActive));
                snappedDocked.CurrentFloatingProgress.Should().Be(0.0);
                snappedDocked.CurrentShadowOpacity.Should().Be(0.0);
                snappedDocked.IsMotionAnimationActive.Should().BeFalse();

                // Direct ApplyBehaviorPreferences also snaps synchronously in 0ms under Reduced Motion
                await host.RunAsync(() =>
                {
                    harness.IslandWindow.ApplyBehaviorPreferences(
                        MonitorSelectionMode.Primary, null, false, 350, 600, false,
                        IslandStyle.FloatingPill, lockIslandPosition: false, 0.0, null);
                    harness.IslandWindow.CurrentFloatingProgress.Should().Be(1.0);
                    harness.IslandWindow.IsMotionAnimationActive.Should().BeFalse();
                });
            }
            finally
            {
                await host.RunAsync(() => settingsWindow.Close());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IslandWindow_DragClampingMagneticSnapLockAndReset_OperatesCleanlyAcrossModes()
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() => CreateHarness(host, reducedMotion: false));

        var committedEvents = new List<IslandPositionCommittedEventArgs>();
        await host.RunAsync(() =>
        {
            harness.IslandWindow.PositionCommitted += (_, pos) => committedEvents.Add(pos);
            harness.IslandWindow.Show();
        });
        await host.PumpAsync();

        // 1. Sub-threshold gesture (< 5 DIP) does NOT move or commit position
        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(3.0, 2.0));
        await host.PumpAsync();
        committedEvents.Should().BeEmpty();

        // 2. Docked Notch drag constrains to horizontal top edge only
        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(140.0, 250.0));
        await Task.Delay(250);
        await host.PumpAsync();

        committedEvents.Should().ContainSingle();
        committedEvents[^1].OffsetXDip.Should().BeApproximately(140.0, 1.0);
        committedEvents[^1].FloatingTopOffsetDip.Should().BeNull();
        var workTopDip = IslandPositionMath.GetEffectiveWorkArea(harness.PrimaryMonitor).Top / harness.PrimaryMonitor.DpiScale;
        (await host.RunAsync(() => harness.IslandWindow.CurrentVisualBoundsDip.Top)).Should().BeApproximately(workTopDip, 0.5);

        // 3. Drop within 16 DIP of center snaps magnetically back to 0.0 with ease-out glide
        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(-130.0, 0.0)); // lands at +10 DIP <= 16
        await Task.Delay(260);
        await host.PumpAsync();
        committedEvents.Should().HaveCount(2);
        committedEvents[^1].OffsetXDip.Should().Be(0.0);

        // 4. Floating Pill allows free X/Y drag inside work area, Lock prevents drag, ResetPosition glides to center
        await host.RunAsync(() => harness.IslandWindow.ApplyBehaviorPreferences(
            MonitorSelectionMode.Primary, null, false, 350, 600, false,
            IslandStyle.FloatingPill, lockIslandPosition: false, 0.0, null));
        await Task.Delay(300);
        await host.PumpAsync();

        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(180.0, 140.0));
        await Task.Delay(260);
        await host.PumpAsync();

        committedEvents.Should().HaveCount(3);
        committedEvents[^1].OffsetXDip.Should().BeApproximately(180.0, 1.0);
        committedEvents[^1].FloatingTopOffsetDip.Should().BeApproximately(IslandPositionMath.DefaultFloatingTopMarginDip + 140.0, 1.0);

        // Lock position ignores subsequent drags
        await host.RunAsync(() => harness.IslandWindow.ApplyBehaviorPreferences(
            MonitorSelectionMode.Primary, null, false, 350, 600, false,
            IslandStyle.FloatingPill, lockIslandPosition: true, committedEvents[^1].OffsetXDip, committedEvents[^1].FloatingTopOffsetDip));
        await host.PumpAsync();
        var countBeforeLocked = committedEvents.Count;
        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(-120.0, -80.0));
        await host.PumpAsync();
        committedEvents.Count.Should().Be(countBeforeLocked);

        // ResetPosition glides cleanly back to default top-center (0, null) and emits exactly once
        await host.RunAsync(() => harness.IslandWindow.ResetPosition());
        await Task.Delay(260);
        await host.PumpAsync();
        committedEvents.Should().HaveCount(4);
        committedEvents[^1].Should().Be(new IslandPositionCommittedEventArgs(0.0, null));

        // 5. Capture-loss release re-entrancy commits PositionCommitted strictly once
        await host.RunAsync(() =>
        {
            harness.IslandWindow.ApplyBehaviorPreferences(
                MonitorSelectionMode.Primary, null, false, 350, 600, false,
                IslandStyle.FloatingPill, lockIslandPosition: false, 0.0, null);
        });
        await Task.Delay(200);
        await host.PumpAsync();
        var countBeforeReentryCheck = committedEvents.Count;
        await host.RunAsync(() =>
        {
            harness.IslandWindow.SimulateDragGestureForTesting(60.0, 40.0, completeDrop: false);
            harness.IslandWindow.CaptureMouse();
            harness.IslandWindow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            });
        });
        await Task.Delay(240);
        await host.PumpAsync();
        committedEvents.Should().HaveCount(countBeforeReentryCheck + 1);
        committedEvents[^1].OffsetXDip.Should().BeApproximately(60.0, 1.0);
        committedEvents[^1].FloatingTopOffsetDip.Should().BeApproximately(IslandPositionMath.DefaultFloatingTopMarginDip + 40.0, 1.0);

        // 6. Edge/bottom parked Floating Pill expands into safe bounds without clipping off-screen
        await host.RunAsync(() => harness.IslandWindow.SimulateDragGestureForTesting(5000.0, 5000.0));
        await Task.Delay(260);
        await host.PumpAsync();

        await host.RunAsync(() => harness.StateManager.RequestUserExpand());
        await Task.Delay(300);
        await host.PumpAsync();

        var workArea = IslandPositionMath.GetEffectiveWorkArea(harness.PrimaryMonitor);
        var scale = IslandPositionMath.GetEffectiveDpiScale(harness.PrimaryMonitor.DpiScale);
        var expandedRect = await host.RunAsync(() => harness.IslandWindow.CurrentVisualBoundsDip);
        (expandedRect.Left * scale).Should().BeGreaterOrEqualTo(workArea.Left - 1.0);
        (expandedRect.Right * scale).Should().BeLessOrEqualTo(workArea.Right + 1.0);
        (expandedRect.Top * scale).Should().BeGreaterOrEqualTo(workArea.Top - 1.0);
        (expandedRect.Bottom * scale).Should().BeLessOrEqualTo(workArea.Bottom + 1.0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public async Task IslandWindow_HitTestAndNcHitTest_PassClicksAroundFloatingCapsuleAndBelowSubPillAcrossDpi(double dpiScale)
    {
        using var host = new StaWpfTestHost();
        await using var harness = await host.RunAsync(() =>
        {
            var customMonitor = new MonitorInfo(
                0x101,
                @"\\.\DISPLAY1",
                new MonitorBounds(0, 0, 1920, 1080),
                new MonitorBounds(0, 0, 1920, 1032),
                dpiScale,
                true);

            var displayMonitor = Substitute.For<IDisplayTopologyMonitor>();
            displayMonitor.GetMonitors().Returns([customMonitor]);
            displayMonitor.GetPrimaryMonitor().Returns(customMonitor);
            displayMonitor.ResolveTargetMonitor(Arg.Any<MonitorSelectionMode>(), Arg.Any<string?>()).Returns(customMonitor);

            var fullscreenDetector = Substitute.For<IFullscreenDetector>();
            fullscreenDetector.IsMonitorFullscreen(Arg.Any<nint>()).Returns(false);
            var hotkeyService = Substitute.For<IGlobalHotkeyService>();
            var powerMonitor = Substitute.For<IPowerAndMotionMonitor>();
            powerMonitor.CurrentPowerProfile.Returns(PowerProfile.AC);
            powerMonitor.IsReducedMotionEnabled.Returns(true);
            powerMonitor.IsSessionLocked.Returns(false);

            var pinController = new OverlayPinController();
            var stateManager = new IslandStateManager(pinController);
            var islandWindow = new IslandWindow(
                stateManager,
                pinController,
                new OverlayWindowAdapter(),
                displayMonitor,
                fullscreenDetector,
                hotkeyService,
                powerMonitor);
            islandWindow.ApplyBehaviorPreferences(
                MonitorSelectionMode.Primary, null, false, 350, 600, false,
                IslandStyle.FloatingPill, lockIslandPosition: false, 0.0, null);
            return new UiTestHarness(
                host,
                customMonitor,
                pinController,
                stateManager,
                fullscreenDetector,
                hotkeyService,
                powerMonitor,
                islandWindow,
                new Window { Width = 200, Height = 120, ShowInTaskbar = false });
        });
        await using var compHarness = await CreateMediaAndDeveloperHarnessAsync(harness);

        await host.RunAsync(() => harness.IslandWindow.Show());
        compHarness.EmitMediaPlaying("Midnight City", "M83");
        compHarness.EmitDeveloperSession(AgentStatus.Working, isFatalError: false);
        await host.PumpAsync();

        var hitResults = await host.RunAsync(() =>
        {
            harness.IslandWindow.UpdateLayout();
            var mainHost = (FrameworkElement)harness.IslandWindow.FindName("MainIslandSurfaceHost");

            var mainCenter = mainHost.TranslatePoint(new Point(mainHost.ActualWidth * 0.5, mainHost.ActualHeight * 0.5), harness.IslandWindow);
            var bottomBarPoint = mainHost.TranslatePoint(new Point(mainHost.ActualWidth * 0.5, mainHost.ActualHeight - 3.0), harness.IslandWindow);
            var outerShadowLeft = new Point(2.0, mainCenter.Y);
            var outerBottomBelowPill = new Point(mainCenter.X, mainHost.TranslatePoint(new Point(0, mainHost.ActualHeight), harness.IslandWindow).Y + 4.0);

            var hwnd = harness.IslandWindow.WindowHandle;
            var rectOk = GetWindowRect(hwnd, out var winRect);
            rectOk.Should().BeTrue();

            static nint PackScreenPoint(int x, int y) => unchecked((nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));
            const int WmNcHitTest = 0x0084;

            var ncOuterBottomResult = SendMessageW(
                hwnd,
                WmNcHitTest,
                0,
                PackScreenPoint(
                    winRect.Left + (int)Math.Round(outerBottomBelowPill.X * dpiScale),
                    winRect.Top + (int)Math.Round(outerBottomBelowPill.Y * dpiScale)));
            var ncMainResult = SendMessageW(
                hwnd,
                WmNcHitTest,
                0,
                PackScreenPoint(
                    winRect.Left + (int)Math.Round(mainCenter.X * dpiScale),
                    winRect.Top + (int)Math.Round(mainCenter.Y * dpiScale)));

            return (
                HitMain: harness.IslandWindow.HitTestInteractiveSurfaceDip(mainCenter),
                HitBottomBarArea: harness.IslandWindow.HitTestInteractiveSurfaceDip(bottomBarPoint),
                HitOuterMargin: harness.IslandWindow.HitTestInteractiveSurfaceDip(outerShadowLeft),
                HitOuterBottom: harness.IslandWindow.HitTestInteractiveSurfaceDip(outerBottomBelowPill),
                NcOuterBottomResult: ncOuterBottomResult,
                NcMainResult: ncMainResult);
        });

        hitResults.HitMain.Should().BeTrue();
        hitResults.HitBottomBarArea.Should().BeTrue();
        hitResults.HitOuterMargin.Should().BeFalse();
        hitResults.HitOuterBottom.Should().BeFalse();
        hitResults.NcOuterBottomResult.Should().Be((nint)(-1)); // HTTRANSPARENT
        hitResults.NcMainResult.Should().NotBe((nint)(-1));
    }

    private static int CountDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T)
            {
                count++;
            }

            count += CountDescendants<T>(child);
        }

        return count;
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
            powerMonitor,
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class UiTestHarness(
        StaWpfTestHost host,
        MonitorInfo primaryMonitor,
        OverlayPinController pinController,
        IslandStateManager stateManager,
        IFullscreenDetector fullscreenDetector,
        IGlobalHotkeyService hotkeyService,
        IPowerAndMotionMonitor powerMonitor,
        IslandWindow islandWindow,
        Window hostWindow) : IAsyncDisposable
    {
        public MonitorInfo PrimaryMonitor { get; } = primaryMonitor;

        public OverlayPinController PinController { get; } = pinController;

        public IslandStateManager StateManager { get; } = stateManager;

        public IFullscreenDetector FullscreenDetector { get; } = fullscreenDetector;

        public IGlobalHotkeyService HotkeyService { get; } = hotkeyService;

        public IPowerAndMotionMonitor PowerMonitor { get; } = powerMonitor;

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
