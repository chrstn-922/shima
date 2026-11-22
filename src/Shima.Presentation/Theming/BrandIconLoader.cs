using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shima.Presentation.Theming;

/// <summary>
/// Loads Shima's multi-size brand icon from embedded assembly/process resources without System.Drawing or pack:// dependencies,
/// and applies native Win32 small/large HICON handles so Windows title bars and the taskbar display crisp icons.
/// </summary>
internal static class BrandIconLoader
{
    private const string EmbeddedIconResourceName = "Shima.Presentation.Assets.Shima.ico";
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const uint ImageIcon = 1;
    private const uint LrDefaultColor = 0x00000000;
    private const int IdiApplication = 32512;

    public static void AttachWindowIcons(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Icon = TryLoadWindowIcon();

        nint ownedSmallIcon = 0;
        nint ownedBigIcon = 0;
        var isClosed = false;

        void ReleaseOwnedIcons()
        {
            if (ownedSmallIcon != 0)
            {
                DestroyIcon(ownedSmallIcon);
                ownedSmallIcon = 0;
            }

            if (ownedBigIcon != 0)
            {
                DestroyIcon(ownedBigIcon);
                ownedBigIcon = 0;
            }
        }

        void ApplyNativeIcons()
        {
            if (isClosed)
            {
                return;
            }

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
            {
                return;
            }

            var (smCx, smCy) = GetIconMetrics(hwnd, SmCxSmIcon, SmCySmIcon, 16);
            var (lgCx, lgCy) = GetIconMetrics(hwnd, SmCxIcon, SmCyIcon, 32);

            var nextSmall = TryLoadNativeIcon(smCx, smCy);
            var nextBig = TryLoadNativeIcon(lgCx, lgCy);

            var previousSmall = ownedSmallIcon;
            var previousBig = ownedBigIcon;

            if (nextSmall != 0)
            {
                SendMessageW(hwnd, WmSetIcon, (nint)IconSmall, nextSmall);
                ownedSmallIcon = nextSmall;
            }

            if (nextBig != 0)
            {
                SendMessageW(hwnd, WmSetIcon, (nint)IconBig, nextBig);
                ownedBigIcon = nextBig;
            }

            if (previousSmall != 0 && previousSmall != ownedSmallIcon)
            {
                DestroyIcon(previousSmall);
            }

            if (previousBig != 0 && previousBig != ownedBigIcon)
            {
                DestroyIcon(previousBig);
            }
        }

        window.SourceInitialized += (_, _) => ApplyNativeIcons();
        window.Closed += (_, _) =>
        {
            isClosed = true;
            ReleaseOwnedIcons();
        };

        if (new WindowInteropHelper(window).Handle != 0)
        {
            ApplyNativeIcons();
        }
    }

    public static ImageSource? TryLoadWindowIcon()
    {
        try
        {
            var iconBytes = ReadEmbeddedIconBytes();
            if (iconBytes is null || iconBytes.Length == 0)
            {
                return null;
            }

            using var buffer = new MemoryStream(iconBytes, writable: false);
            var decoder = new IconBitmapDecoder(
                buffer,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var bestFrame = decoder.Frames
                .OrderByDescending(f => f.PixelWidth * f.PixelHeight)
                .FirstOrDefault();
            if (bestFrame is null)
            {
                return null;
            }

            bestFrame.Freeze();
            return bestFrame;
        }
        catch
        {
            return null;
        }
    }

    private static nint TryLoadNativeIcon(int width, int height)
    {
        var moduleHandle = GetModuleHandleW(null);
        if (moduleHandle != 0)
        {
            var processIcon = LoadImageW(
                moduleHandle,
                unchecked((nint)IdiApplication),
                ImageIcon,
                width,
                height,
                LrDefaultColor);
            if (processIcon != 0)
            {
                return processIcon;
            }
        }

        var iconBytes = ReadEmbeddedIconBytes();
        return iconBytes is not null
            ? CreateIconFromIcoBytes(iconBytes, width, height)
            : 0;
    }

    private static byte[]? ReadEmbeddedIconBytes()
    {
        using var resourceStream = typeof(BrandIconLoader).Assembly.GetManifestResourceStream(EmbeddedIconResourceName);
        if (resourceStream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        resourceStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static (int Width, int Height) GetIconMetrics(nint hwnd, int cxIndex, int cyIndex, int fallback)
    {
        var dpi = GetDpiForWindow(hwnd);
        var cx = dpi > 0 ? GetSystemMetricsForDpi(cxIndex, dpi) : GetSystemMetrics(cxIndex);
        var cy = dpi > 0 ? GetSystemMetricsForDpi(cyIndex, dpi) : GetSystemMetrics(cyIndex);
        return (cx > 0 ? cx : fallback, cy > 0 ? cy : fallback);
    }

    private static nint CreateIconFromIcoBytes(byte[] icoBytes, int targetWidth, int targetHeight)
    {
        if (icoBytes.Length < 6)
        {
            return 0;
        }

        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(0, 2));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(2, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(4, 2));
        if (reserved != 0 || type != 1 || count == 0)
        {
            return 0;
        }

        var desiredSide = Math.Max(1, Math.Max(targetWidth, targetHeight));
        var bestOffset = 0;
        var bestSize = 0u;
        var bestScore = int.MaxValue;
        var bestBpp = -1;

        for (var i = 0; i < count; i++)
        {
            var entryOffset = 6 + (i * 16);
            if (entryOffset + 16 > icoBytes.Length)
            {
                break;
            }

            var width = icoBytes[entryOffset] == 0 ? 256 : icoBytes[entryOffset];
            var height = icoBytes[entryOffset + 1] == 0 ? 256 : icoBytes[entryOffset + 1];
            var bpp = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(entryOffset + 6, 2));
            var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(entryOffset + 8, 4));
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(entryOffset + 12, 4));

            if (imageSize == 0 || imageOffset > icoBytes.Length || imageSize > (uint)(icoBytes.Length - (int)imageOffset))
            {
                continue;
            }

            var side = Math.Max(width, height);
            var score = side >= desiredSide ? side - desiredSide : 100_000 + (desiredSide - side);
            if (score < bestScore || (score == bestScore && bpp > bestBpp))
            {
                bestScore = score;
                bestBpp = bpp;
                bestOffset = (int)imageOffset;
                bestSize = imageSize;
            }
        }

        if (bestSize == 0)
        {
            return 0;
        }

        var handle = GCHandle.Alloc(icoBytes, GCHandleType.Pinned);
        try
        {
            var framePtr = IntPtr.Add(handle.AddrOfPinnedObject(), bestOffset);
            return CreateIconFromResourceEx(
                framePtr,
                bestSize,
                fIcon: true,
                dwVer: 0x00030000,
                targetWidth,
                targetHeight,
                LrDefaultColor);
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern nint LoadImageW(nint hInst, nint name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(
        nint presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
}
