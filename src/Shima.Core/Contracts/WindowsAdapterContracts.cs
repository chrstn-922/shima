namespace Shima.Core.Contracts;

public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct DipSize(double Width, double Height);

public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    MonitorBounds Bounds,
    MonitorBounds WorkArea,
    double DpiScale,
    bool IsPrimary);

public enum MonitorSelectionMode
{
    Primary,
    ActiveCursor,
    SpecificMonitorId
}

public sealed record FullscreenStateChangedEventArgs(nint MonitorHandle, bool IsFullscreen);

public interface IOverlayWindowAdapter
{
    void ApplyNonActivatingStyles(nint hwnd);

    void SetInteractiveFocusMode(nint hwnd, bool allowFocus);

    void SetClickThrough(nint hwnd, bool transparent);

    void AnchorToMonitor(MonitorBounds bounds, double dpiScale, DipSize dipSize);

    void AnchorToMonitor(nint hwnd, MonitorBounds bounds, double dpiScale, DipSize dipSize);

    void ReassertTopmostZOrder(nint hwnd);

    void ShowOverlay(nint hwnd);

    void HideOverlay(nint hwnd);

    bool IsOverlayVisible(nint hwnd);
}

public interface IDisplayTopologyMonitor : IDisposable
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    MonitorInfo GetPrimaryMonitor();

    MonitorInfo ResolveTargetMonitor(MonitorSelectionMode mode, string? specificDeviceId = null);

    MonitorInfo? GetMonitorForWindow(nint hwnd);

    void HandleWindowMessage(int msg, nint wParam, nint lParam, nint hwnd);

    event EventHandler<MonitorInfo>? MonitorChanged;

    event EventHandler<double>? DpiChanged;
}

public interface IFullscreenDetector : IDisposable
{
    void Attach(nint overlayHwnd);

    bool IsMonitorFullscreen(nint monitorHandle);

    void EvaluateNow();

    event EventHandler<FullscreenStateChangedEventArgs>? FullscreenStateChanged;
}

public interface IGlobalHotkeyService : IDisposable
{
    bool TryRegister(nint hwnd, string gestureText, out string? errorMessage);

    void Unregister();

    bool HandleWindowMessage(int msg, nint wParam);

    event EventHandler? HotkeyPressed;
}

public interface IPowerAndMotionMonitor : IDisposable
{
    PowerProfile CurrentPowerProfile { get; }

    bool IsReducedMotionEnabled { get; }

    bool IsSessionLocked { get; }

    void Attach(nint hwnd);

    void HandleWindowMessage(int msg, nint wParam, nint lParam);

    event EventHandler<PowerProfile>? PowerProfileChanged;

    event EventHandler<bool>? ReducedMotionChanged;

    event EventHandler<bool>? SessionLockChanged;
}

public interface IStartupRegistrationAdapter
{
    bool UsesPackagedStartupTask { get; }

    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    Task<bool> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
