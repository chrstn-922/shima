using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.System;

internal enum UserNotificationQueryState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullscreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7
}

internal interface IFullscreenQuerySource
{
    nint GetForegroundWindow();

    bool TryGetWindowRect(nint hwnd, out MonitorBounds rect);

    string GetWindowClassName(nint hwnd);

    (nint MonitorHandle, MonitorBounds MonitorBounds) GetMonitorForWindow(nint hwnd);

    UserNotificationQueryState QueryUserNotificationState();
}

internal sealed unsafe class Win32FullscreenQuerySource : IFullscreenQuerySource
{
    public nint GetForegroundWindow() => PInvoke.GetForegroundWindow().Value;

    public bool TryGetWindowRect(nint hwnd, out MonitorBounds rect)
    {
        if (hwnd == 0 || !PInvoke.GetWindowRect(new HWND(hwnd), out var winRect))
        {
            rect = default;
            return false;
        }

        rect = new MonitorBounds(
            winRect.left,
            winRect.top,
            winRect.right - winRect.left,
            winRect.bottom - winRect.top);
        return true;
    }

    public string GetWindowClassName(nint hwnd)
    {
        if (hwnd == 0)
        {
            return string.Empty;
        }

        char* buffer = stackalloc char[256];
        var length = PInvoke.GetClassName(new HWND(hwnd), buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public (nint MonitorHandle, MonitorBounds MonitorBounds) GetMonitorForWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return (0, default);
        }

        var hMonitor = PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (hMonitor.Value == 0)
        {
            return (0, default);
        }

        var info = new global::Windows.Win32.Graphics.Gdi.MONITORINFO
        {
            cbSize = (uint)sizeof(global::Windows.Win32.Graphics.Gdi.MONITORINFO)
        };
        if (!PInvoke.GetMonitorInfo(hMonitor, ref info))
        {
            return (hMonitor.Value, default);
        }

        var rc = info.rcMonitor;
        return (hMonitor.Value, new MonitorBounds(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top));
    }

    public UserNotificationQueryState QueryUserNotificationState()
    {
        var hr = PInvoke.SHQueryUserNotificationState(out var state);
        return hr.Succeeded ? (UserNotificationQueryState)(int)state : UserNotificationQueryState.AcceptsNotifications;
    }
}

public sealed class FullscreenDetector : IFullscreenDetector
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private readonly object _sync = new();
    private readonly IFullscreenQuerySource _querySource;
    private readonly Dictionary<nint, bool> _monitorFullscreenStates = new();
    private readonly WINEVENTPROC _winEventCallback;

    private nint _overlayHwnd;
    private HWINEVENTHOOK _foregroundHook;
    private HWINEVENTHOOK _locationHook;
    private bool _disposed;

    public FullscreenDetector()
        : this(new Win32FullscreenQuerySource())
    {
    }

    internal FullscreenDetector(IFullscreenQuerySource querySource)
    {
        _querySource = querySource ?? throw new ArgumentNullException(nameof(querySource));
        _winEventCallback = OnWinEvent;
    }

    public event EventHandler<FullscreenStateChangedEventArgs>? FullscreenStateChanged;

    public static bool IsShellDesktopClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    public static bool DoesRectCoverMonitor(MonitorBounds windowRect, MonitorBounds monitorBounds)
    {
        if (windowRect.IsEmpty || monitorBounds.IsEmpty)
        {
            return false;
        }

        return windowRect.Left <= monitorBounds.Left
            && windowRect.Top <= monitorBounds.Top
            && windowRect.Right >= monitorBounds.Right
            && windowRect.Bottom >= monitorBounds.Bottom;
    }

    public void Attach(nint overlayHwnd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _overlayHwnd = overlayHwnd;

        if (_foregroundHook.Value == 0)
        {
            _foregroundHook = PInvoke.SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                HMODULE.Null,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
        }

        if (_locationHook.Value == 0)
        {
            _locationHook = PInvoke.SetWinEventHook(
                EventObjectLocationChange,
                EventObjectLocationChange,
                HMODULE.Null,
                _winEventCallback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
        }

        EvaluateNow();
    }

    public bool IsMonitorFullscreen(nint monitorHandle)
    {
        lock (_sync)
        {
            return _monitorFullscreenStates.TryGetValue(monitorHandle, out var value) && value;
        }
    }

    public void EvaluateNow()
    {
        if (_disposed)
        {
            return;
        }

        var changes = new List<FullscreenStateChangedEventArgs>();

        try
        {
            var fgHwnd = _querySource.GetForegroundWindow();
            if (fgHwnd == 0 || fgHwnd == _overlayHwnd)
            {
                return;
            }

            var className = _querySource.GetWindowClassName(fgHwnd);
            var (monitorHandle, monitorBounds) = _querySource.GetMonitorForWindow(fgHwnd);
            if (monitorHandle == 0)
            {
                ClearAllActiveMonitorsLocked(changes);
                return;
            }

            var isFullscreen = false;
            if (!IsShellDesktopClass(className))
            {
                var coversMonitor = _querySource.TryGetWindowRect(fgHwnd, out var windowRect)
                    && DoesRectCoverMonitor(windowRect, monitorBounds);
                var notificationState = _querySource.QueryUserNotificationState();
                var exclusiveD3d = notificationState is UserNotificationQueryState.RunningD3DFullscreen
                    or UserNotificationQueryState.PresentationMode;

                isFullscreen = coversMonitor || exclusiveD3d;
            }

            lock (_sync)
            {
                // Non-active monitors can no longer be covered by a fullscreen foreground window.
                foreach (var handle in _monitorFullscreenStates.Keys.ToArray())
                {
                    if (handle != monitorHandle && _monitorFullscreenStates[handle])
                    {
                        _monitorFullscreenStates[handle] = false;
                        changes.Add(new FullscreenStateChangedEventArgs(handle, false));
                    }
                }

                var previous = _monitorFullscreenStates.TryGetValue(monitorHandle, out var existing) && existing;
                _monitorFullscreenStates[monitorHandle] = isFullscreen;
                if (previous != isFullscreen)
                {
                    changes.Add(new FullscreenStateChangedEventArgs(monitorHandle, isFullscreen));
                }
            }
        }
        catch
        {
            // Default defensively to non-fullscreen if OS query fails.
            lock (_sync)
            {
                ClearAllActiveMonitorsLocked(changes);
            }
        }

        foreach (var change in changes)
        {
            FullscreenStateChanged?.Invoke(this, change);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_foregroundHook.Value != 0)
        {
            PInvoke.UnhookWinEvent(_foregroundHook);
            _foregroundHook = default;
        }

        if (_locationHook.Value != 0)
        {
            PInvoke.UnhookWinEvent(_locationHook);
            _locationHook = default;
        }
    }

    private void ClearAllActiveMonitorsLocked(List<FullscreenStateChangedEventArgs> changes)
    {
        foreach (var handle in _monitorFullscreenStates.Keys.ToArray())
        {
            if (_monitorFullscreenStates[handle])
            {
                _monitorFullscreenStates[handle] = false;
                changes.Add(new FullscreenStateChangedEventArgs(handle, false));
            }
        }
    }

    private void OnWinEvent(
        HWINEVENTHOOK hWinEventHook,
        uint eventType,
        HWND hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || hwnd.Value == 0 || hwnd.Value == _overlayHwnd)
        {
            return;
        }

        if (eventType == EventObjectLocationChange && (idObject != 0 || idChild != 0))
        {
            return;
        }

        if (eventType == EventObjectLocationChange && hwnd.Value != _querySource.GetForegroundWindow())
        {
            return;
        }

        EvaluateNow();
    }
}
