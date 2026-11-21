using FluentAssertions;
using Shima.Core.Contracts;
using Shima.WindowsInterop.System;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class FullscreenDetectorTests
{
    private const int WsBorder = 0x00800000;
    private const int WsVisible = 0x10000000;
    private const int StandardMaximizedStyle = WsVisible | FullscreenDetector.WsCaption | FullscreenDetector.WsThickFrame | FullscreenDetector.WsMaximize;
    private const int StandardRestoredStyle = WsVisible | FullscreenDetector.WsCaption | FullscreenDetector.WsThickFrame;

    private static readonly MonitorBounds PrimaryMonitor = new(0, 0, 1920, 1080);
    private static readonly MonitorBounds PrimaryWorkAreaWithTaskbar = new(0, 0, 1920, 1032);

    [Theory]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("CabinetWClass")]
    [InlineData("ApplicationFrameWindow")]
    public void NormalMaximizedWindow_WithVisibleTaskbar_DoesNotTriggerFullscreen(string windowClass)
    {
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x1001,
            ClassName = windowClass,
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            // DWMWA_EXTENDED_FRAME_BOUNDS stops cleanly above the taskbar at rcWork (unlike GetWindowRect -8px overflow)
            WindowBounds = PrimaryWorkAreaWithTaskbar,
            HasWindowBounds = true,
            WindowStyle = StandardMaximizedStyle,
            IsMaximized = true
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1", StandardMaximizedStyle, true)]
    [InlineData("Chrome_WidgetWin_1", StandardRestoredStyle | FullscreenDetector.WsMaximize, false)]
    [InlineData("CabinetWClass", StandardMaximizedStyle, true)]
    public void NormalMaximizedWindow_EvenWithAutoHideTaskbarOrFullWorkArea_DoesNotTriggerFullscreen(
        string windowClass,
        int style,
        bool isZoomed)
    {
        // When taskbar auto-hide is enabled (rcWork == rcMonitor), a maximized Chrome/VS Code/Explorer
        // still retains IsZoomed / WS_MAXIMIZE + WS_CAPTION / WS_THICKFRAME and MUST NOT trigger FullscreenHardHide.
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x1002,
            ClassName = windowClass,
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryMonitor,
            WindowBounds = PrimaryMonitor,
            HasWindowBounds = true,
            WindowStyle = style,
            IsMaximized = isZoomed
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
    }

    [Fact]
    public void FramelessMaximizedWindow_ConstrainedToWorkArea_DoesNotTriggerFullscreen()
    {
        // Custom Chromium/Electron/WPF frameless window maximized to rcWork (not covering taskbar)
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x1003,
            ClassName = "Chrome_WidgetWin_1",
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            WindowBounds = PrimaryWorkAreaWithTaskbar,
            HasWindowBounds = true,
            WindowStyle = WsVisible | WsBorder | FullscreenDetector.WsMaximize,
            IsMaximized = true
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
    }

    [Fact]
    public void WindowShrunkInsideMonitorByExtendedFrameBounds_DoesNotTriggerFullscreen()
    {
        // Even if raw GetWindowRect had resize overhang, extended bounds leaving 2px uncovered cannot be fullscreen
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x1004,
            ClassName = "CustomBorderlessApp",
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            WindowBounds = new MonitorBounds(0, 0, 1920, 1078),
            HasWindowBounds = true,
            WindowStyle = WsVisible,
            IsMaximized = false
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1", WsVisible, false)]              // Chrome/VS Code F11 or fullscreen HTML5 video
    [InlineData("Chrome_WidgetWin_1", WsVisible | FullscreenDetector.WsMaximize, true)] // Borderless F11 covering full rcMonitor over taskbar
    [InlineData("UnrealWindow", WsVisible | WsBorder, false)]         // Borderless game with lone WS_BORDER bit (not WS_CAPTION)
    [InlineData("UnityWndClass", WsVisible, false)]                   // Unity borderless fullscreen game
    public void TrueBorderlessAndF11Fullscreen_CoveringFullMonitor_TriggersFullscreen(
        string windowClass,
        int style,
        bool isZoomed)
    {
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x2001,
            ClassName = windowClass,
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            WindowBounds = PrimaryMonitor,
            HasWindowBounds = true,
            WindowStyle = style,
            IsMaximized = isZoomed
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeTrue();
    }

    [Fact]
    public void BorderlessFullscreenOnNegativeCoordinateMonitor_TriggersFullscreen()
    {
        var leftMonitor = new MonitorBounds(-2560, 0, 2560, 1440);
        var leftWorkArea = new MonitorBounds(-2560, 0, 2560, 1392);

        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x2002,
            ClassName = "UnrealWindow",
            MonitorHandle = 0x2,
            MonitorBounds = leftMonitor,
            WorkArea = leftWorkArea,
            WindowBounds = leftMonitor,
            HasWindowBounds = true,
            WindowStyle = WsVisible,
            IsMaximized = false
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x2).Should().BeTrue();
    }

    [Theory]
    [InlineData((int)UserNotificationQueryState.RunningD3DFullscreen)]
    [InlineData((int)UserNotificationQueryState.PresentationMode)]
    public void ExclusiveD3dAndPresentationMode_AlwaysTriggerFullscreen(int notificationState)
    {
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x2003,
            ClassName = "Direct3DGameWindow",
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            WindowBounds = new MonitorBounds(0, 0, 1280, 720),
            HasWindowBounds = true,
            WindowStyle = WsVisible,
            IsMaximized = false,
            NotificationState = (UserNotificationQueryState)notificationState
        };

        using var detector = new FullscreenDetector(query);
        detector.EvaluateNow();

        detector.IsMonitorFullscreen(0x1).Should().BeTrue();
    }

    [Fact]
    public void MaximizeThenF11ThenRestoreToMaximize_EmitsFullscreenTransitionsOnlyForF11()
    {
        var query = new StubFullscreenQuerySource
        {
            ForegroundHwnd = 0x3001,
            ClassName = "Chrome_WidgetWin_1",
            MonitorHandle = 0x1,
            MonitorBounds = PrimaryMonitor,
            WorkArea = PrimaryWorkAreaWithTaskbar,
            WindowBounds = PrimaryWorkAreaWithTaskbar,
            HasWindowBounds = true,
            WindowStyle = StandardMaximizedStyle,
            IsMaximized = true
        };

        using var detector = new FullscreenDetector(query);
        var events = new List<FullscreenStateChangedEventArgs>();
        detector.FullscreenStateChanged += (_, e) => events.Add(e);

        // 1. User focuses normal maximized Chrome -> not fullscreen
        detector.EvaluateNow();
        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
        events.Should().BeEmpty();

        // 2. User presses F11 -> Chrome drops frame chrome & covers full rcMonitor -> enters fullscreen
        query.WindowBounds = PrimaryMonitor;
        query.WindowStyle = WsVisible;
        query.IsMaximized = false;
        detector.EvaluateNow();
        detector.IsMonitorFullscreen(0x1).Should().BeTrue();
        events.Should().ContainSingle().Which.Should().Be(new FullscreenStateChangedEventArgs(0x1, true));

        // 3. User exits F11 back to maximized Chrome -> exits fullscreen cleanly
        query.WindowBounds = PrimaryWorkAreaWithTaskbar;
        query.WindowStyle = StandardMaximizedStyle;
        query.IsMaximized = true;
        detector.EvaluateNow();
        detector.IsMonitorFullscreen(0x1).Should().BeFalse();
        events.Should().HaveCount(2);
        events[1].Should().Be(new FullscreenStateChangedEventArgs(0x1, false));
    }

    [Fact]
    public void Win32FullscreenQuerySource_HandlesZeroAndForegroundHandlesGracefully()
    {
        var source = new Win32FullscreenQuerySource();

        source.TryGetWindowBounds(0, out _).Should().BeFalse();
        source.GetWindowStyle(0).Should().Be(0);
        source.IsWindowMaximized(0).Should().BeFalse();
        source.GetMonitorForWindow(0).MonitorHandle.Should().Be(0);

        using var detector = new FullscreenDetector(source);
        var act = () => detector.EvaluateNow();
        act.Should().NotThrow();
    }

    private sealed class StubFullscreenQuerySource : IFullscreenQuerySource
    {
        public nint ForegroundHwnd { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public nint MonitorHandle { get; set; }
        public MonitorBounds MonitorBounds { get; set; }
        public MonitorBounds WorkArea { get; set; }
        public MonitorBounds WindowBounds { get; set; }
        public bool HasWindowBounds { get; set; }
        public int WindowStyle { get; set; }
        public bool IsMaximized { get; set; }
        public UserNotificationQueryState NotificationState { get; set; } = UserNotificationQueryState.AcceptsNotifications;

        public nint GetForegroundWindow() => ForegroundHwnd;

        public bool TryGetWindowBounds(nint hwnd, out MonitorBounds bounds)
        {
            bounds = WindowBounds;
            return HasWindowBounds;
        }

        public int GetWindowStyle(nint hwnd) => WindowStyle;

        public bool IsWindowMaximized(nint hwnd) => IsMaximized;

        public string GetWindowClassName(nint hwnd) => ClassName;

        public (nint MonitorHandle, MonitorBounds MonitorBounds, MonitorBounds WorkArea) GetMonitorForWindow(nint hwnd) =>
            (MonitorHandle, MonitorBounds, WorkArea);

        public UserNotificationQueryState QueryUserNotificationState() => NotificationState;
    }
}
