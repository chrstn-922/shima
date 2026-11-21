using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shima.WindowsInterop.System;

public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    internal const int DefaultHotkeyId = 0x5348;
    internal const int WmHotkey = (int)PInvoke.WM_HOTKEY;

    private const uint ModAlt = (uint)HOT_KEY_MODIFIERS.MOD_ALT;
    private const uint ModControl = (uint)HOT_KEY_MODIFIERS.MOD_CONTROL;
    private const uint ModShift = (uint)HOT_KEY_MODIFIERS.MOD_SHIFT;
    private const uint ModWin = (uint)HOT_KEY_MODIFIERS.MOD_WIN;
    private const uint ModNoRepeat = (uint)HOT_KEY_MODIFIERS.MOD_NOREPEAT;

    private nint _registeredHwnd;
    private bool _isRegistered;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;

    public static bool TryParseGesture(
        string? gestureText,
        out uint modifiers,
        out uint virtualKey,
        out string? errorMessage)
    {
        modifiers = ModNoRepeat;
        virtualKey = 0;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(gestureText))
        {
            errorMessage = "Hotkey gesture cannot be empty.";
            return false;
        }

        var parts = gestureText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            errorMessage = "Hotkey gesture must include at least one modifier key and a primary key.";
            return false;
        }

        uint modifierMask = 0;
        var hasKey = false;

        foreach (var token in parts)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifierMask |= ModControl;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Menu", StringComparison.OrdinalIgnoreCase))
            {
                modifierMask |= ModAlt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifierMask |= ModShift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || token.Equals("Super", StringComparison.OrdinalIgnoreCase))
            {
                modifierMask |= ModWin;
            }
            else if (!hasKey && TryMapVirtualKey(token, out var vk))
            {
                virtualKey = vk;
                hasKey = true;
            }
            else
            {
                errorMessage = $"Unsupported or duplicate hotkey token '{token}'.";
                return false;
            }
        }

        if (modifierMask == 0 || !hasKey)
        {
            errorMessage = "Hotkey gesture must specify at least one modifier and one non-modifier key.";
            return false;
        }

        modifiers |= modifierMask;
        return true;
    }

    public bool TryRegister(nint hwnd, string gestureText, out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (hwnd == 0)
        {
            errorMessage = "Target window handle is zero.";
            return false;
        }

        if (!TryParseGesture(gestureText, out var modifiers, out var virtualKey, out errorMessage))
        {
            return false;
        }

        Unregister();

        if (!PInvoke.RegisterHotKey(new HWND(hwnd), DefaultHotkeyId, (HOT_KEY_MODIFIERS)modifiers, virtualKey))
        {
            errorMessage = $"OS refused hotkey registration for '{gestureText}'. Another application may own it.";
            return false;
        }

        _registeredHwnd = hwnd;
        _isRegistered = true;
        errorMessage = null;
        return true;
    }

    public void Unregister()
    {
        if (!_isRegistered || _registeredHwnd == 0)
        {
            return;
        }

        PInvoke.UnregisterHotKey(new HWND(_registeredHwnd), DefaultHotkeyId);
        _registeredHwnd = 0;
        _isRegistered = false;
    }

    public bool HandleWindowMessage(int msg, nint wParam)
    {
        if (msg == WmHotkey && (int)wParam == DefaultHotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }

    private static bool TryMapVirtualKey(string token, out uint virtualKey)
    {
        if (token.Length == 1)
        {
            var upper = char.ToUpperInvariant(token[0]);
            if (upper is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = upper;
                return true;
            }
        }

        if (token.Length >= 2
            && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), out var fn)
            && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + (fn - 1));
            return true;
        }

        virtualKey = token.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" or "BACK" => 0x08,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "OEM3" or "TILDE" or "`" or "~" => 0xC0,
            "MINUS" or "-" => 0xBD,
            "PLUS" or "EQUALS" or "=" => 0xBB,
            _ => 0
        };

        return virtualKey != 0;
    }
}
