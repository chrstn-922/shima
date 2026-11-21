using System.Runtime.InteropServices;
using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.System;

internal interface IDisplayTopologySource
{
    IReadOnlyList<MonitorInfo> EnumerateMonitors();

    nint GetCursorMonitorHandle();

    nint GetWindowMonitorHandle(nint hwnd);
}

internal sealed unsafe class Win32DisplayTopologySource : IDisplayTopologySource
{
    private const uint MonitorInfoPrimary = 1;

    public IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            PInvoke.EnumDisplayMonitors(
                default,
                (RECT*)null,
                MonitorEnumCallback,
                new LPARAM(GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    public nint GetCursorMonitorHandle() =>
        PInvoke.GetCursorPos(out var point)
            ? PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value
            : 0;

    public nint GetWindowMonitorHandle(nint hwnd) =>
        hwnd == 0
            ? 0
            : PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value;

    private static BOOL MonitorEnumCallback(HMONITOR hMonitor, HDC hdc, RECT* lprcMonitor, LPARAM dwData)
    {
        var list = (List<MonitorInfo>?)GCHandle.FromIntPtr(dwData.Value).Target;
        if (list is null)
        {
            return false;
        }

        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&info))
        {
            return true;
        }

        var hr = PInvoke.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _);
        var dpiScale = hr.Succeeded && dpiX > 0 ? dpiX / 96.0 : 1.0;

        var rcMon = info.monitorInfo.rcMonitor;
        var rcWork = info.monitorInfo.rcWork;
        var bounds = new MonitorBounds(rcMon.left, rcMon.top, rcMon.right - rcMon.left, rcMon.bottom - rcMon.top);
        var workArea = new MonitorBounds(rcWork.left, rcWork.top, rcWork.right - rcWork.left, rcWork.bottom - rcWork.top);
        var isPrimary = (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0;

        var deviceSpan = info.szDevice.AsReadOnlySpan();
        var nullIndex = deviceSpan.IndexOf('\0');
        var deviceName = new string(nullIndex >= 0 ? deviceSpan[..nullIndex] : deviceSpan);
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = $"DISPLAY_{list.Count + 1}";
        }

        list.Add(new MonitorInfo(hMonitor.Value, deviceName, bounds, workArea, dpiScale, isPrimary));
        return true;
    }
}

public sealed class DisplayTopologyMonitor : IDisplayTopologyMonitor
{
    private const int WmDisplayChange = (int)PInvoke.WM_DISPLAYCHANGE;
    private const int WmDpiChanged = (int)PInvoke.WM_DPICHANGED;

    private static readonly MonitorInfo SyntheticFallbackMonitor = new(
        0,
        @"\\.\DISPLAY1",
        new MonitorBounds(0, 0, 1920, 1080),
        new MonitorBounds(0, 0, 1920, 1032),
        1.0,
        true);

    private readonly IDisplayTopologySource _source;

    public DisplayTopologyMonitor()
        : this(new Win32DisplayTopologySource())
    {
    }

    internal DisplayTopologyMonitor(IDisplayTopologySource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public event EventHandler<MonitorInfo>? MonitorChanged;

    public event EventHandler<double>? DpiChanged;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = _source.EnumerateMonitors();
        return list.Count > 0 ? list : [SyntheticFallbackMonitor];
    }

    public MonitorInfo GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public MonitorInfo ResolveTargetMonitor(MonitorSelectionMode mode, string? specificDeviceId = null)
    {
        var monitors = GetMonitors();

        switch (mode)
        {
            case MonitorSelectionMode.ActiveCursor:
                var cursorHandle = _source.GetCursorMonitorHandle();
                if (cursorHandle != 0)
                {
                    var underCursor = monitors.FirstOrDefault(m => m.Handle == cursorHandle);
                    if (underCursor is not null)
                    {
                        return underCursor;
                    }
                }

                break;

            case MonitorSelectionMode.SpecificMonitorId:
                if (!string.IsNullOrWhiteSpace(specificDeviceId))
                {
                    var matched = monitors.FirstOrDefault(
                        m => string.Equals(m.DeviceName, specificDeviceId, StringComparison.OrdinalIgnoreCase));
                    if (matched is not null)
                    {
                        return matched;
                    }
                }

                break;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public MonitorInfo? GetMonitorForWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var handle = _source.GetWindowMonitorHandle(hwnd);
        if (handle == 0)
        {
            return null;
        }

        return GetMonitors().FirstOrDefault(m => m.Handle == handle);
    }

    public void HandleWindowMessage(int msg, nint wParam, nint lParam, nint hwnd)
    {
        if (msg == WmDpiChanged)
        {
            var rawDpiX = (int)(wParam & 0xFFFF);
            var scale = rawDpiX > 0 ? rawDpiX / 96.0 : 1.0;
            DpiChanged?.Invoke(this, scale);

            var current = GetMonitorForWindow(hwnd) ?? GetPrimaryMonitor();
            MonitorChanged?.Invoke(this, current with { DpiScale = scale });
        }
        else if (msg == WmDisplayChange)
        {
            var current = GetMonitorForWindow(hwnd) ?? GetPrimaryMonitor();
            MonitorChanged?.Invoke(this, current);
        }
    }

    public void Dispose()
    {
    }
}
