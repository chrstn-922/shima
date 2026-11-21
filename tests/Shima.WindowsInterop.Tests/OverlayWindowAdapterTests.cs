using FluentAssertions;
using Shima.Core.Contracts;
using Shima.WindowsInterop.Overlay;
using Shima.WindowsInterop.System;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class OverlayWindowAdapterTests
{
    [Fact]
    public void CalculateAnchoredPixelRect_ComputesCenteredTopFlushPixelsAcrossDpiScales()
    {
        var primary1080 = new MonitorBounds(0, 0, 1920, 1080);
        var collapsedDip = new DipSize(136, 28);

        OverlayWindowAdapter.CalculateAnchoredPixelRect(primary1080, 1.0, collapsedDip)
            .Should().Be(new MonitorBounds(892, 0, 136, 28));

        OverlayWindowAdapter.CalculateAnchoredPixelRect(primary1080, 1.25, collapsedDip)
            .Should().Be(new MonitorBounds(875, 0, 170, 35));

        OverlayWindowAdapter.CalculateAnchoredPixelRect(primary1080, 1.5, collapsedDip)
            .Should().Be(new MonitorBounds(858, 0, 204, 42));

        OverlayWindowAdapter.CalculateAnchoredPixelRect(primary1080, 2.0, collapsedDip)
            .Should().Be(new MonitorBounds(824, 0, 272, 56));

        OverlayWindowAdapter.CalculateAnchoredPixelRect(primary1080, 3.0, collapsedDip)
            .Should().Be(new MonitorBounds(756, 0, 408, 84));

        // Left-side negative-coordinate secondary monitor (Left = -2560, Top = 120)
        var leftMonitor = new MonitorBounds(-2560, 120, 2560, 1440);
        var expandedDip = new DipSize(412, 168);
        var leftRect = OverlayWindowAdapter.CalculateAnchoredPixelRect(leftMonitor, 1.5, expandedDip);
        leftRect.Should().Be(new MonitorBounds(-1589, 120, 618, 252));
    }

    [Fact]
    public void OverlayWindowAdapter_AppliesNonActivatingStyles_AndPreservesAcrossToggles()
    {
        const nint islandHwnd = 0x1234;
        const nint editorHwnd = 0x9999;

        var ops = new FakeWin32OverlayOperations
        {
            ForegroundHwnd = editorHwnd
        };
        var adapter = new OverlayWindowAdapter(ops);

        adapter.ApplyNonActivatingStyles(islandHwnd);

        var applied = ops.GetWindowExStyle(islandHwnd);
        (applied & OverlayWindowAdapter.WsExTopmost).Should().NotBe(0);
        (applied & OverlayWindowAdapter.WsExToolWindow).Should().NotBe(0);
        (applied & OverlayWindowAdapter.WsExNoActivate).Should().NotBe(0);

        // Alt+hover click-through preserves non-activating topmost styles
        adapter.SetClickThrough(islandHwnd, true);
        var transparentStyle = ops.GetWindowExStyle(islandHwnd);
        (transparentStyle & OverlayWindowAdapter.WsExTransparent).Should().NotBe(0);
        (transparentStyle & OverlayWindowAdapter.WsExNoActivate).Should().NotBe(0);

        adapter.SetClickThrough(islandHwnd, false);
        (ops.GetWindowExStyle(islandHwnd) & OverlayWindowAdapter.WsExTransparent).Should().Be(0);
        (ops.GetWindowExStyle(islandHwnd) & OverlayWindowAdapter.WsExNoActivate).Should().NotBe(0);

        // Interactive text-input handoff remembers editorHwnd and restores focus on blur
        adapter.SetInteractiveFocusMode(islandHwnd, allowFocus: true);
        (ops.GetWindowExStyle(islandHwnd) & OverlayWindowAdapter.WsExNoActivate).Should().Be(0);
        ops.ForegroundHwnd.Should().Be(islandHwnd);

        adapter.SetInteractiveFocusMode(islandHwnd, allowFocus: false);
        (ops.GetWindowExStyle(islandHwnd) & OverlayWindowAdapter.WsExNoActivate).Should().NotBe(0);
        ops.ForegroundHwnd.Should().Be(editorHwnd);

        // ShowOverlay uses SW_SHOWNOACTIVATE; HideOverlay uses SW_HIDE
        adapter.ShowOverlay(islandHwnd);
        ops.LastShowNoActivate.Should().BeTrue();
        adapter.IsOverlayVisible(islandHwnd).Should().BeTrue();

        adapter.HideOverlay(islandHwnd);
        ops.LastShowNoActivate.Should().BeFalse();
        adapter.IsOverlayVisible(islandHwnd).Should().BeFalse();
    }

    [Fact]
    public void DisplayTopologyMonitor_FallsBackToPrimaryWhenConfiguredMonitorDisconnects_AndHandlesDpiChange()
    {
        var primary = new MonitorInfo(
            0x101,
            @"\\.\DISPLAY1",
            new MonitorBounds(0, 0, 1920, 1080),
            new MonitorBounds(0, 0, 1920, 1032),
            1.0,
            true);
        var secondary = new MonitorInfo(
            0x202,
            @"\\.\DISPLAY2",
            new MonitorBounds(1920, 0, 2560, 1440),
            new MonitorBounds(1920, 0, 2560, 1392),
            1.5,
            false);

        var source = new FakeDisplayTopologySource
        {
            Monitors = [primary, secondary],
            WindowMonitorHandle = 0x202
        };

        using var monitor = new DisplayTopologyMonitor(source);

        monitor.ResolveTargetMonitor(MonitorSelectionMode.SpecificMonitorId, @"\\.\DISPLAY2")
            .Should().Be(secondary);

        // Disconnect secondary monitor -> falls back cleanly to primary
        source.Monitors = [primary];
        monitor.ResolveTargetMonitor(MonitorSelectionMode.SpecificMonitorId, @"\\.\DISPLAY2")
            .Should().Be(primary);

        // WM_DPICHANGED (144 DPI = 1.5x) updates scale cleanly
        double? observedScale = null;
        MonitorInfo? observedMonitor = null;
        monitor.DpiChanged += (_, scale) => observedScale = scale;
        monitor.MonitorChanged += (_, info) => observedMonitor = info;

        source.WindowMonitorHandle = 0x101;
        const int wmDpiChanged = 0x02E0;
        nint wParam144 = (144 << 16) | 144;
        monitor.HandleWindowMessage(wmDpiChanged, wParam144, 0, 0x555);

        observedScale.Should().Be(1.5);
        observedMonitor.Should().NotBeNull();
        observedMonitor!.Handle.Should().Be(0x101);
        observedMonitor.DpiScale.Should().Be(1.5);
    }

    [Fact]
    public void FullscreenDetector_ExcludesShellDesktopClasses_AndScopesEventsPerMonitor()
    {
        var query = new FakeFullscreenQuerySource();
        var detector = new FullscreenDetector(query);

        const nint monitor1 = 0x1;
        const nint monitor2 = 0x2;
        var bounds1 = new MonitorBounds(0, 0, 1920, 1080);
        var bounds2 = new MonitorBounds(1920, 0, 2560, 1440);

        var events = new List<FullscreenStateChangedEventArgs>();
        detector.FullscreenStateChanged += (_, args) => events.Add(args);

        // Case 1: Desktop Progman/WorkerW covering monitor1 must NOT report fullscreen
        query.ForegroundHwnd = 0xAA;
        query.ClassName = "Progman";
        query.MonitorHandle = monitor1;
        query.MonitorBounds = bounds1;
        query.WindowRect = bounds1;
        query.HasWindowRect = true;
        query.NotificationState = UserNotificationQueryState.AcceptsNotifications;

        detector.EvaluateNow();
        detector.IsMonitorFullscreen(monitor1).Should().BeFalse();
        events.Should().BeEmpty();

        // Case 2: Borderless game covering monitor2 triggers fullscreen ONLY for monitor2
        query.ForegroundHwnd = 0xBB;
        query.ClassName = "UnrealWindow";
        query.MonitorHandle = monitor2;
        query.MonitorBounds = bounds2;
        query.WindowRect = bounds2;

        detector.EvaluateNow();
        detector.IsMonitorFullscreen(monitor1).Should().BeFalse();
        detector.IsMonitorFullscreen(monitor2).Should().BeTrue();
        events.Should().ContainSingle(e => e.MonitorHandle == monitor2 && e.IsFullscreen);

        // Case 3: User switches to non-fullscreen window on monitor1 -> monitor2 exits fullscreen
        query.ForegroundHwnd = 0xCC;
        query.ClassName = "Chrome_WidgetWin_1";
        query.MonitorHandle = monitor1;
        query.MonitorBounds = bounds1;
        query.WindowRect = new MonitorBounds(100, 100, 900, 600);

        detector.EvaluateNow();
        detector.IsMonitorFullscreen(monitor2).Should().BeFalse();
        events.Should().Contain(e => e.MonitorHandle == monitor2 && !e.IsFullscreen);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", true)]
    [InlineData("Ctrl+Shift+I", true)]
    [InlineData("Win+F12", true)]
    [InlineData("Space", false)]
    [InlineData("Ctrl+Alt", false)]
    [InlineData("", false)]
    public void GlobalHotkeyService_ValidatesGesturesDeterministically(string gesture, bool expectedValid)
    {
        var ok = GlobalHotkeyService.TryParseGesture(gesture, out var modifiers, out var vk, out var error);

        ok.Should().Be(expectedValid);
        if (expectedValid)
        {
            modifiers.Should().NotBe(0u);
            vk.Should().NotBe(0u);
            error.Should().BeNull();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    private sealed class FakeWin32OverlayOperations : IWin32OverlayOperations
    {
        private readonly Dictionary<nint, int> _styles = new();
        private readonly Dictionary<nint, bool> _visible = new();

        public nint ForegroundHwnd { get; set; }

        public bool LastShowNoActivate { get; private set; }

        public int GetWindowExStyle(nint hwnd) => _styles.GetValueOrDefault(hwnd);

        public int SetWindowExStyle(nint hwnd, int exStyle)
        {
            var prev = _styles.GetValueOrDefault(hwnd);
            _styles[hwnd] = exStyle;
            return prev;
        }

        public bool SetWindowPos(nint hwnd, nint insertAfterHwnd, int x, int y, int width, int height, uint flags) => true;

        public bool ShowWindow(nint hwnd, bool showNoActivate)
        {
            LastShowNoActivate = showNoActivate;
            _visible[hwnd] = showNoActivate;
            return true;
        }

        public bool IsWindowVisible(nint hwnd) => _visible.GetValueOrDefault(hwnd);

        public nint GetForegroundWindow() => ForegroundHwnd;

        public bool SetForegroundWindow(nint hwnd)
        {
            ForegroundHwnd = hwnd;
            return true;
        }
    }

    private sealed class FakeDisplayTopologySource : IDisplayTopologySource
    {
        public IReadOnlyList<MonitorInfo> Monitors { get; set; } = [];

        public nint CursorMonitorHandle { get; set; }

        public nint WindowMonitorHandle { get; set; }

        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => Monitors;

        public nint GetCursorMonitorHandle() => CursorMonitorHandle;

        public nint GetWindowMonitorHandle(nint hwnd) => WindowMonitorHandle;
    }

    private sealed class FakeFullscreenQuerySource : IFullscreenQuerySource
    {
        public nint ForegroundHwnd { get; set; }

        public string ClassName { get; set; } = string.Empty;

        public nint MonitorHandle { get; set; }

        public MonitorBounds MonitorBounds { get; set; }

        public MonitorBounds WindowRect { get; set; }

        public bool HasWindowRect { get; set; }

        public UserNotificationQueryState NotificationState { get; set; } = UserNotificationQueryState.AcceptsNotifications;

        public nint GetForegroundWindow() => ForegroundHwnd;

        public bool TryGetWindowRect(nint hwnd, out MonitorBounds rect)
        {
            rect = WindowRect;
            return HasWindowRect;
        }

        public string GetWindowClassName(nint hwnd) => ClassName;

        public (nint MonitorHandle, MonitorBounds MonitorBounds) GetMonitorForWindow(nint hwnd) =>
            (MonitorHandle, MonitorBounds);

        public UserNotificationQueryState QueryUserNotificationState() => NotificationState;
    }
}
