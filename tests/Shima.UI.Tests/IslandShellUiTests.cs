using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FlaUI.UIA3;
using FluentAssertions;
using NSubstitute;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Presentation.Shell;
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

    private static UiTestHarness CreateHarness(StaWpfTestHost host)
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
        powerMonitor.IsReducedMotionEnabled.Returns(true);
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
