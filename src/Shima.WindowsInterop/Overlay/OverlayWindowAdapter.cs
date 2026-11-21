using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.Overlay;

internal interface IWin32OverlayOperations
{
    int GetWindowExStyle(nint hwnd);

    int SetWindowExStyle(nint hwnd, int exStyle);

    bool SetWindowPos(nint hwnd, nint insertAfterHwnd, int x, int y, int width, int height, uint flags);

    bool ShowWindow(nint hwnd, bool showNoActivate);

    bool IsWindowVisible(nint hwnd);

    nint GetForegroundWindow();

    bool SetForegroundWindow(nint hwnd);
}

internal sealed class Win32OverlayOperations : IWin32OverlayOperations
{
    public int GetWindowExStyle(nint hwnd) =>
        PInvoke.GetWindowLong(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

    public int SetWindowExStyle(nint hwnd, int exStyle) =>
        PInvoke.SetWindowLong(new HWND(hwnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);

    public bool SetWindowPos(nint hwnd, nint insertAfterHwnd, int x, int y, int width, int height, uint flags) =>
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            new HWND(insertAfterHwnd),
            x,
            y,
            width,
            height,
            (SET_WINDOW_POS_FLAGS)flags);

    public bool ShowWindow(nint hwnd, bool showNoActivate) =>
        PInvoke.ShowWindow(
            new HWND(hwnd),
            showNoActivate ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);

    public bool IsWindowVisible(nint hwnd) => PInvoke.IsWindowVisible(new HWND(hwnd));

    public nint GetForegroundWindow() => PInvoke.GetForegroundWindow().Value;

    public bool SetForegroundWindow(nint hwnd) => PInvoke.SetForegroundWindow(new HWND(hwnd));
}

public sealed class OverlayWindowAdapter : IOverlayWindowAdapter
{
    internal const int WsExTopmost = (int)WINDOW_EX_STYLE.WS_EX_TOPMOST;
    internal const int WsExTransparent = (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
    internal const int WsExToolWindow = (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
    internal const int WsExNoActivate = (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    internal const uint SwpNoSize = (uint)SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
    internal const uint SwpNoMove = (uint)SET_WINDOW_POS_FLAGS.SWP_NOMOVE;
    internal const uint SwpNoActivate = (uint)SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
    internal const uint SwpFrameChanged = (uint)SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED;
    internal const nint HwndTopmost = -1;

    private readonly IWin32OverlayOperations _ops;
    private nint _boundHwnd;
    private nint _previousForegroundHwnd;

    public OverlayWindowAdapter()
        : this(new Win32OverlayOperations())
    {
    }

    internal OverlayWindowAdapter(IWin32OverlayOperations ops)
    {
        _ops = ops ?? throw new ArgumentNullException(nameof(ops));
    }

    public static MonitorBounds CalculateAnchoredPixelRect(
        MonitorBounds monitorBounds,
        double dpiScale,
        DipSize dipSize)
    {
        var effectiveScale = dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;
        var safeWidthDip = dipSize.Width > 0 && double.IsFinite(dipSize.Width) ? dipSize.Width : 1.0;
        var safeHeightDip = dipSize.Height > 0 && double.IsFinite(dipSize.Height) ? dipSize.Height : 1.0;

        var pixelWidth = Math.Max(1, (int)Math.Round(safeWidthDip * effectiveScale, MidpointRounding.AwayFromZero));
        var pixelHeight = Math.Max(1, (int)Math.Round(safeHeightDip * effectiveScale, MidpointRounding.AwayFromZero));
        var x = monitorBounds.Left + (monitorBounds.Width - pixelWidth) / 2;
        var y = monitorBounds.Top;

        return new MonitorBounds(x, y, pixelWidth, pixelHeight);
    }

    public void ApplyNonActivatingStyles(nint hwnd)
    {
        ValidateHwnd(hwnd);
        _boundHwnd = hwnd;

        var currentStyle = _ops.GetWindowExStyle(hwnd);
        var updatedStyle = currentStyle | WsExTopmost | WsExToolWindow | WsExNoActivate;
        _ops.SetWindowExStyle(hwnd, updatedStyle);

        _ops.SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);
    }

    public void SetInteractiveFocusMode(nint hwnd, bool allowFocus)
    {
        ValidateHwnd(hwnd);

        var currentStyle = _ops.GetWindowExStyle(hwnd) | WsExTopmost | WsExToolWindow;
        if (allowFocus)
        {
            var foreground = _ops.GetForegroundWindow();
            if (foreground != 0 && foreground != hwnd)
            {
                _previousForegroundHwnd = foreground;
            }

            _ops.SetWindowExStyle(hwnd, currentStyle & ~WsExNoActivate);
            _ops.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);
            _ops.SetForegroundWindow(hwnd);
        }
        else
        {
            _ops.SetWindowExStyle(hwnd, currentStyle | WsExNoActivate);
            _ops.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);

            var targetRestore = _previousForegroundHwnd;
            _previousForegroundHwnd = 0;
            if (targetRestore != 0 && targetRestore != hwnd)
            {
                _ops.SetForegroundWindow(targetRestore);
            }
        }
    }

    public void SetClickThrough(nint hwnd, bool transparent)
    {
        ValidateHwnd(hwnd);

        var currentStyle = _ops.GetWindowExStyle(hwnd) | WsExTopmost | WsExToolWindow;
        var updatedStyle = transparent
            ? currentStyle | WsExTransparent
            : currentStyle & ~WsExTransparent;

        if (updatedStyle != currentStyle || (updatedStyle & WsExTransparent) != (currentStyle & WsExTransparent))
        {
            _ops.SetWindowExStyle(hwnd, updatedStyle);
        }

        _ops.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);
    }

    public void AnchorToMonitor(MonitorBounds bounds, double dpiScale, DipSize dipSize)
    {
        if (_boundHwnd == 0)
        {
            throw new InvalidOperationException("Overlay window handle must be initialized before anchoring.");
        }

        AnchorToMonitor(_boundHwnd, bounds, dpiScale, dipSize);
    }

    public void AnchorToMonitor(nint hwnd, MonitorBounds bounds, double dpiScale, DipSize dipSize)
    {
        ValidateHwnd(hwnd);
        _boundHwnd = hwnd;

        var pixelRect = CalculateAnchoredPixelRect(bounds, dpiScale, dipSize);
        _ops.SetWindowPos(
            hwnd,
            HwndTopmost,
            pixelRect.Left,
            pixelRect.Top,
            pixelRect.Width,
            pixelRect.Height,
            SwpNoActivate);
    }

    public void ReassertTopmostZOrder(nint hwnd)
    {
        ValidateHwnd(hwnd);
        _ops.SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    public void ShowOverlay(nint hwnd)
    {
        ValidateHwnd(hwnd);
        _ops.ShowWindow(hwnd, showNoActivate: true);
        ReassertTopmostZOrder(hwnd);
    }

    public void HideOverlay(nint hwnd)
    {
        ValidateHwnd(hwnd);
        _ops.ShowWindow(hwnd, showNoActivate: false);
    }

    public bool IsOverlayVisible(nint hwnd)
    {
        ValidateHwnd(hwnd);
        return _ops.IsWindowVisible(hwnd);
    }

    private static void ValidateHwnd(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("Window handle must be non-zero.", nameof(hwnd));
        }
    }
}
