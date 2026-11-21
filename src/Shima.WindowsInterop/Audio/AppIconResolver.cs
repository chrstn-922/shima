using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Audio;

internal interface IAppIconResolver
{
    string? TryGetExecutablePath(uint processId);

    string ResolveDisplayName(
        uint processId,
        string? rawSessionDisplayName,
        string? executablePath,
        bool isSystemSounds);

    RawBgraImage? TryResolveIcon(string? executablePath, string? iconPath);
}

/// <summary>
/// Pure Win32 GDI/Shell process metadata and HICON-to-BGRA pixel resolver with zero System.Drawing.Common dependency.
/// </summary>
internal sealed unsafe class Win32AppIconResolver : IAppIconResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;

    private readonly object _cacheSync = new();
    private readonly Dictionary<string, RawBgraImage?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public string? TryGetExecutablePath(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var capacity = (uint)buffer.Length;
            fixed (char* ptr = buffer)
            {
                if (QueryFullProcessImageNameW(handle, 0, ptr, ref capacity) && capacity > 0)
                {
                    return new string(buffer, 0, (int)capacity);
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public string ResolveDisplayName(
        uint processId,
        string? rawSessionDisplayName,
        string? executablePath,
        bool isSystemSounds)
    {
        if (isSystemSounds)
        {
            return "System Sounds";
        }

        if (!string.IsNullOrWhiteSpace(rawSessionDisplayName) && !rawSessionDisplayName.StartsWith('@'))
        {
            return rawSessionDisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                if (File.Exists(executablePath))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
                    if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                    {
                        return versionInfo.FileDescription.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
                    {
                        return versionInfo.ProductName.Trim();
                    }
                }
            }
            catch
            {
                // Fall back to filename or process name below.
            }

            var fileName = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        if (processId > 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                // Process may have exited.
            }
        }

        return "Application";
    }

    public RawBgraImage? TryResolveIcon(string? executablePath, string? iconPath)
    {
        var cacheKey = !string.IsNullOrWhiteSpace(executablePath)
            ? $"exe:{executablePath}"
            : !string.IsNullOrWhiteSpace(iconPath)
                ? $"icon:{iconPath}"
                : null;

        if (cacheKey is null)
        {
            return null;
        }

        lock (_cacheSync)
        {
            if (_iconCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var resolved = ExtractIconBgra(executablePath, iconPath);

        lock (_cacheSync)
        {
            if (_iconCache.Count >= 64)
            {
                _iconCache.Clear();
            }

            _iconCache[cacheKey] = resolved;
        }

        return resolved;
    }

    private static RawBgraImage? ExtractIconBgra(string? executablePath, string? iconPath)
    {
        var hIcon = TryAcquireIconHandle(executablePath, iconPath);
        if (hIcon == 0)
        {
            return null;
        }

        try
        {
            return ConvertHIconToBgra(hIcon);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static nint TryAcquireIconHandle(string? executablePath, string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            var shfi = new SHFILEINFOW();
            var result = SHGetFileInfoW(
                executablePath,
                0,
                ref shfi,
                (uint)sizeof(SHFILEINFOW),
                ShgfiIcon | ShgfiLargeIcon);
            if (result != 0 && shfi.hIcon != 0)
            {
                return shfi.hIcon;
            }
        }

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var (resolvedPath, iconIndex) = ParseIconResourceReference(iconPath);
            if (!string.IsNullOrWhiteSpace(resolvedPath) && File.Exists(resolvedPath))
            {
                nint largeIcon = 0;
                nint smallIcon = 0;
                var count = ExtractIconExW(resolvedPath, iconIndex, &largeIcon, &smallIcon, 1);
                if (smallIcon != 0)
                {
                    DestroyIcon(smallIcon);
                }

                if (count > 0 && largeIcon != 0)
                {
                    return largeIcon;
                }

                if (largeIcon != 0)
                {
                    DestroyIcon(largeIcon);
                }
            }
        }

        return 0;
    }

    private static (string? FilePath, int IconIndex) ParseIconResourceReference(string rawIconPath)
    {
        var trimmed = rawIconPath.Trim();
        if (trimmed.StartsWith('@'))
        {
            trimmed = trimmed[1..];
        }

        var index = 0;
        var commaIndex = trimmed.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(trimmed[(commaIndex + 1)..], out var parsedIndex))
        {
            index = parsedIndex;
            trimmed = trimmed[..commaIndex];
        }

        trimmed = Environment.ExpandEnvironmentVariables(trimmed).Trim().Trim('"');
        return (trimmed, index);
    }

    private static RawBgraImage? ConvertHIconToBgra(nint hIcon)
    {
        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return null;
        }

        nint screenDc = 0;
        nint memDc = 0;

        try
        {
            var targetBitmap = iconInfo.hbmColor != 0 ? iconInfo.hbmColor : iconInfo.hbmMask;
            if (targetBitmap == 0)
            {
                return null;
            }

            if (GetObjectW(targetBitmap, sizeof(BITMAP), out var bmp) == 0)
            {
                return null;
            }

            var width = bmp.bmWidth;
            var height = iconInfo.hbmColor != 0 ? Math.Abs(bmp.bmHeight) : Math.Abs(bmp.bmHeight) / 2;
            if (width <= 0 || height <= 0 || width > 256 || height > 256)
            {
                return null;
            }

            screenDc = GetDC(0);
            if (screenDc == 0)
            {
                return null;
            }

            memDc = CreateCompatibleDC(screenDc);
            ReleaseDC(0, screenDc);
            screenDc = 0;
            if (memDc == 0)
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // Top-down scanlines for BitmapSource.Create
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb
            };

            if (iconInfo.hbmColor != 0)
            {
                fixed (byte* pixelPtr = pixels)
                {
                    if (GetDIBits(memDc, iconInfo.hbmColor, 0, (uint)height, pixelPtr, ref bmi, DibRgbColors) == 0)
                    {
                        return null;
                    }
                }

                // Some legacy Win32 icons emit all-zero alpha channel bytes and encode transparency in hbmMask.
                var hasNonZeroAlpha = false;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        hasNonZeroAlpha = true;
                        break;
                    }
                }

                if (!hasNonZeroAlpha && iconInfo.hbmMask != 0)
                {
                    var maskPixels = new byte[width * height * 4];
                    fixed (byte* maskPtr = maskPixels)
                    {
                        if (GetDIBits(memDc, iconInfo.hbmMask, 0, (uint)height, maskPtr, ref bmi, DibRgbColors) != 0)
                        {
                            for (var i = 0; i < pixels.Length; i += 4)
                            {
                                pixels[i + 3] = maskPixels[i] == 0 ? (byte)255 : (byte)0;
                            }
                        }
                        else
                        {
                            FillOpaqueAlpha(pixels);
                        }
                    }
                }
            }
            else
            {
                // Monochrome mask-only icon: top half is AND mask, bottom half is XOR mask.
                var monoBmi = bmi with { biHeight = -(height * 2) };
                var maskPixels = new byte[width * (height * 2) * 4];
                fixed (byte* maskPtr = maskPixels)
                {
                    if (GetDIBits(memDc, iconInfo.hbmMask, 0, (uint)(height * 2), maskPtr, ref monoBmi, DibRgbColors) == 0)
                    {
                        return null;
                    }
                }

                var halfByteLen = width * height * 4;
                for (var i = 0; i < halfByteLen; i += 4)
                {
                    var andByte = maskPixels[i];
                    var xorByte = maskPixels[halfByteLen + i];
                    var opaque = andByte == 0;
                    pixels[i] = xorByte;
                    pixels[i + 1] = xorByte;
                    pixels[i + 2] = xorByte;
                    pixels[i + 3] = opaque ? (byte)255 : (byte)0;
                }
            }

            return new RawBgraImage(width, height, pixels);
        }
        finally
        {
            if (memDc != 0)
            {
                DeleteDC(memDc);
            }

            if (screenDc != 0)
            {
                ReleaseDC(0, screenDc);
            }

            if (iconInfo.hbmColor != 0)
            {
                DeleteObject(iconInfo.hbmColor);
            }

            if (iconInfo.hbmMask != 0)
            {
                DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static void FillOpaqueAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, nint* phiconLarge, nint* phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(nint h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, void* lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);
}
