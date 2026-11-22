using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.Agents;

internal interface ITerminalWindowFocuser
{
    bool IsSupported { get; }

    int? ResolveHostProcessId(int processId);

    bool TryFocusProcessWindow(int processId);
}

internal sealed unsafe class Win32TerminalWindowFocuser : ITerminalWindowFocuser
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    public bool IsSupported => OperatingSystem.IsWindows();

    public int? ResolveHostProcessId(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (FindWindowForProcess(processId) != 0)
            {
                return processId;
            }

            var parents = CaptureParentProcessMap();
            var current = processId;
            for (var depth = 0; depth < 6; depth++)
            {
                if (!parents.TryGetValue(current, out var parentPid) || parentPid <= 0 || parentPid == current)
                {
                    break;
                }

                if (FindWindowForProcess(parentPid) != 0)
                {
                    return parentPid;
                }

                current = parentPid;
            }
        }
        catch
        {
            // Never fail hook processing because of process tree inspection.
        }

        return processId;
    }

    public bool TryFocusProcessWindow(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var hwnd = FindWindowForProcess(processId);
            if (hwnd == 0)
            {
                var hostPid = ResolveHostProcessId(processId);
                if (hostPid.HasValue && hostPid.Value != processId)
                {
                    hwnd = FindWindowForProcess(hostPid.Value);
                }
            }

            if (hwnd == 0)
            {
                return false;
            }

            var window = new HWND(hwnd);
            if (PInvoke.IsIconic(window))
            {
                PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            return PInvoke.SetForegroundWindow(window);
        }
        catch
        {
            return false;
        }
    }

    private static nint FindWindowForProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return 0;
            }

            if (process.MainWindowHandle != 0 && PInvoke.IsWindowVisible(new HWND(process.MainWindowHandle)))
            {
                return process.MainWindowHandle;
            }
        }
        catch
        {
            return 0;
        }

        var state = new EnumWindowSearchState((uint)processId);
        var gcHandle = GCHandle.Alloc(state);
        try
        {
            PInvoke.EnumWindows(EnumWindowsCallback, new LPARAM(GCHandle.ToIntPtr(gcHandle)));
        }
        finally
        {
            gcHandle.Free();
        }

        return state.FoundHwnd;
    }

    private static BOOL EnumWindowsCallback(HWND hwnd, LPARAM lParam)
    {
        var state = (EnumWindowSearchState?)GCHandle.FromIntPtr(lParam.Value).Target;
        if (state is null)
        {
            return false;
        }

        if (!PInvoke.IsWindowVisible(hwnd))
        {
            return true;
        }

        uint windowPid = 0;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &windowPid);
        if (windowPid == state.TargetProcessId)
        {
            state.FoundHwnd = hwnd.Value;
            return false;
        }

        return true;
    }

    private static Dictionary<int, int> CaptureParentProcessMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32W
            {
                dwSize = (uint)sizeof(PROCESSENTRY32W)
            };

            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    private sealed class EnumWindowSearchState(uint targetProcessId)
    {
        public uint TargetProcessId { get; } = targetProcessId;
        public nint FoundHwnd { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
