using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shima.WindowsInterop.System;

public sealed record TrayMenuItem(uint CommandId, string Label, bool IsChecked = false, bool IsSeparator = false);

public sealed unsafe class NativeTrayIconHost : IDisposable
{
    public const uint CallbackMessageId = PInvoke.WM_APP + 100;

    private const uint TrayIconId = 1;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const nuint IdiApplication = 32512;

    private readonly Func<IReadOnlyList<TrayMenuItem>> _menuItemsFactory;
    private readonly uint _taskbarCreatedMsg;

    private nint _hwnd;
    private HICON _iconHandle;
    private string _tooltip = "Shima Dynamic Island";
    private bool _isAdded;
    private bool _disposed;

    public NativeTrayIconHost(Func<IReadOnlyList<TrayMenuItem>> menuItemsFactory)
    {
        _menuItemsFactory = menuItemsFactory ?? throw new ArgumentNullException(nameof(menuItemsFactory));
        _taskbarCreatedMsg = PInvoke.RegisterWindowMessage("TaskbarCreated");
    }

    public event EventHandler? PrimaryActionInvoked;

    public event EventHandler<uint>? MenuItemClicked;

    public void Attach(nint hwnd, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hwnd == 0)
        {
            throw new ArgumentException("Window handle must be non-zero.", nameof(hwnd));
        }

        _hwnd = hwnd;
        _tooltip = TrimTooltip(tooltip);
        _iconHandle = PInvoke.LoadIcon(default, new PCWSTR((char*)IdiApplication));

        AddTrayIcon();
    }

    public void UpdateTooltip(string tooltip)
    {
        _tooltip = TrimTooltip(tooltip);
        if (!_isAdded || _hwnd == 0)
        {
            return;
        }

        var data = BuildNotifyIconData();
        ShellNotifyIconNative(NimModify, ref data);
    }

    public bool HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (_disposed || _hwnd == 0)
        {
            return false;
        }

        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            AddTrayIcon();
            return true;
        }

        if ((uint)msg != CallbackMessageId)
        {
            return false;
        }

        var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMsg)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                PrimaryActionInvoked?.Invoke(this, EventArgs.Empty);
                return true;

            case WmRButtonUp:
            case WmContextMenu:
                ShowContextMenu();
                return true;

            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isAdded && _hwnd != 0)
        {
            var data = BuildNotifyIconData();
            ShellNotifyIconNative(NimDelete, ref data);
            _isAdded = false;
        }
    }

    private static string TrimTooltip(string? tooltip)
    {
        var text = string.IsNullOrWhiteSpace(tooltip) ? "Shima Dynamic Island" : tooltip;
        return text.Length > 127 ? text[..127] : text;
    }

    private void AddTrayIcon()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var data = BuildNotifyIconData();
        _isAdded = ShellNotifyIconNative(NimAdd, ref data);
    }

    private NotifyIconData BuildNotifyIconData() =>
        new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessageId,
            hIcon = _iconHandle.Value,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

    private void ShowContextMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
        {
            return;
        }

        try
        {
            foreach (var item in _menuItemsFactory())
            {
                if (item.IsSeparator)
                {
                    PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
                }
                else
                {
                    var flags = MENU_ITEM_FLAGS.MF_STRING;
                    if (item.IsChecked)
                    {
                        flags |= MENU_ITEM_FLAGS.MF_CHECKED;
                    }

                    fixed (char* labelPtr = item.Label)
                    {
                        PInvoke.AppendMenu(menu, flags, item.CommandId, new PCWSTR(labelPtr));
                    }
                }
            }

            if (!PInvoke.GetCursorPos(out var point))
            {
                return;
            }

            PInvoke.SetForegroundWindow(new HWND(_hwnd));
            var selected = PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY,
                point.X,
                point.Y,
                0,
                new HWND(_hwnd),
                (RECT*)null);
            PInvoke.PostMessage(new HWND(_hwnd), 0, default, default);

            if (selected.Value != 0)
            {
                MenuItemClicked?.Invoke(this, (uint)selected.Value);
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconNative(uint dwMessage, ref NotifyIconData lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }
}
