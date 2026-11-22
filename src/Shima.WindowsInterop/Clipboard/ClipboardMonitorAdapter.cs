using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shima.WindowsInterop.Clipboard;

internal interface IClipboardNativeBackend
{
    uint RegisterClipboardFormat(string name);

    bool AddClipboardFormatListener(nint hwnd);

    bool RemoveClipboardFormatListener(nint hwnd);

    bool OpenClipboard(nint hwndOwner);

    bool CloseClipboard();

    bool IsClipboardFormatAvailable(uint format);

    bool TryReadDwordFormat(uint format, out uint value);

    string? TryReadUnicodeText(int maxChars);

    byte[]? TryReadFormatBytes(uint format, int maxBytes);

    bool ClearAndSetClipboard(string? unicodeText, byte[]? pngBytes, byte[]? dibBytes, uint pngFormatId);

    string? GetForegroundProcessName();
}

internal sealed unsafe class Win32ClipboardNativeBackend : IClipboardNativeBackend
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    public uint RegisterClipboardFormat(string name) =>
        PInvoke.RegisterClipboardFormat(name);

    public bool AddClipboardFormatListener(nint hwnd) =>
        PInvoke.AddClipboardFormatListener(new HWND(hwnd));

    public bool RemoveClipboardFormatListener(nint hwnd) =>
        PInvoke.RemoveClipboardFormatListener(new HWND(hwnd));

    public bool OpenClipboard(nint hwndOwner) =>
        PInvoke.OpenClipboard(new HWND(hwndOwner));

    public bool CloseClipboard() =>
        PInvoke.CloseClipboard();

    public bool IsClipboardFormatAvailable(uint format) =>
        IsClipboardFormatAvailableNative(format);

    public bool TryReadDwordFormat(uint format, out uint value)
    {
        value = 0;
        var handle = GetClipboardDataNative(format);
        if (handle == 0)
        {
            return false;
        }

        var locked = GlobalLockNative(handle);
        if (locked == 0)
        {
            return false;
        }

        try
        {
            var size = GlobalSizeNative(handle);
            if (size < sizeof(uint))
            {
                return false;
            }

            value = unchecked((uint)Marshal.ReadInt32(locked));
            return true;
        }
        finally
        {
            GlobalUnlockNative(handle);
        }
    }

    public string? TryReadUnicodeText(int maxChars)
    {
        var handle = GetClipboardDataNative(CfUnicodeText);
        if (handle == 0)
        {
            return null;
        }

        var locked = GlobalLockNative(handle);
        if (locked == 0)
        {
            return null;
        }

        try
        {
            var byteSize = GlobalSizeNative(handle);
            if (byteSize == 0)
            {
                return null;
            }

            var maxAvailableChars = (int)Math.Min(byteSize / 2, (nuint)int.MaxValue);
            var span = new ReadOnlySpan<char>((void*)locked, maxAvailableChars);
            var nullIndex = span.IndexOf('\0');
            var actualChars = nullIndex >= 0 ? nullIndex : maxAvailableChars;
            if (actualChars > maxChars)
            {
                return null;
            }

            return actualChars == 0 ? string.Empty : new string(span[..actualChars]);
        }
        finally
        {
            GlobalUnlockNative(handle);
        }
    }

    public byte[]? TryReadFormatBytes(uint format, int maxBytes)
    {
        var handle = GetClipboardDataNative(format);
        if (handle == 0)
        {
            return null;
        }

        var locked = GlobalLockNative(handle);
        if (locked == 0)
        {
            return null;
        }

        try
        {
            var size = GlobalSizeNative(handle);
            if (size == 0 || size > (nuint)maxBytes)
            {
                return null;
            }

            var buffer = new byte[(int)size];
            Marshal.Copy(locked, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            GlobalUnlockNative(handle);
        }
    }

    public bool ClearAndSetClipboard(string? unicodeText, byte[]? pngBytes, byte[]? dibBytes, uint pngFormatId)
    {
        if (!EmptyClipboardNative())
        {
            return false;
        }

        var wroteAny = false;
        if (unicodeText is not null)
        {
            var charBytes = Encoding.Unicode.GetBytes(unicodeText + '\0');
            if (!TryWriteGlobalHandle(CfUnicodeText, charBytes))
            {
                return false;
            }

            wroteAny = true;
        }

        if (dibBytes is { Length: > 0 })
        {
            if (!TryWriteGlobalHandle(CfDib, dibBytes))
            {
                return false;
            }

            wroteAny = true;
        }

        if (pngBytes is { Length: > 0 })
        {
            if (pngFormatId == 0 || !TryWriteGlobalHandle(pngFormatId, pngBytes))
            {
                return false;
            }

            wroteAny = true;
        }

        return wroteAny;
    }

    public string? GetForegroundProcessName()
    {
        try
        {
            var ownerHwnd = GetClipboardOwnerNative();
            if (ownerHwnd == 0)
            {
                ownerHwnd = PInvoke.GetForegroundWindow().Value;
            }

            if (ownerHwnd == 0)
            {
                return null;
            }

            _ = GetWindowThreadProcessIdNative(ownerHwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryWriteGlobalHandle(uint format, byte[] payload)
    {
        var hMem = GlobalAllocNative(GmemMoveable | GmemZeroInit, (nuint)payload.Length);
        if (hMem == 0)
        {
            return false;
        }

        var locked = GlobalLockNative(hMem);
        if (locked == 0)
        {
            GlobalFreeNative(hMem);
            return false;
        }

        var copied = false;
        try
        {
            Marshal.Copy(payload, 0, locked, payload.Length);
            copied = true;
        }
        finally
        {
            GlobalUnlockNative(hMem);
            if (!copied)
            {
                GlobalFreeNative(hMem);
            }
        }

        if (SetClipboardDataNative(format, hMem) == 0)
        {
            GlobalFreeNative(hMem);
            return false;
        }

        return true;
    }

    [DllImport("user32.dll", EntryPoint = "IsClipboardFormatAvailable")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailableNative(uint format);

    [DllImport("user32.dll", EntryPoint = "GetClipboardData", SetLastError = true)]
    private static extern nint GetClipboardDataNative(uint uFormat);

    [DllImport("user32.dll", EntryPoint = "EmptyClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboardNative();

    [DllImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    private static extern nint SetClipboardDataNative(uint uFormat, nint hMem);

    [DllImport("user32.dll", EntryPoint = "GetClipboardOwner")]
    private static extern nint GetClipboardOwnerNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", EntryPoint = "GlobalAlloc", SetLastError = true)]
    private static extern nint GlobalAllocNative(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    private static extern nint GlobalLockNative(nint hMem);

    [DllImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlockNative(nint hMem);

    [DllImport("kernel32.dll", EntryPoint = "GlobalSize", SetLastError = true)]
    private static extern nuint GlobalSizeNative(nint hMem);

    [DllImport("kernel32.dll", EntryPoint = "GlobalFree", SetLastError = true)]
    private static extern nint GlobalFreeNative(nint hMem);
}

/// <summary>
/// Event-driven Win32 AddClipboardFormatListener adapter (WM_CLIPBOARDUPDATE) enforcing password-manager
/// exclusion formats, 3-retry OpenClipboard backoff, payload caps, and self-copy loop suppression.
/// Never logs clipboard content.
/// </summary>
public sealed class ClipboardMonitorAdapter : IClipboardMonitorAdapter
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const uint CfDib = 8;
    internal const uint CfUnicodeText = 13;
    internal const uint CfHDrop = 15;

    private static readonly int[] RetryDelaysMs = [25, 50, 75];
    private static readonly TimeSpan SelfCopySuppressionWindow = TimeSpan.FromSeconds(2);

    private readonly IClipboardNativeBackend _backend;
    private readonly ILogger? _logger;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _clipboardGate = new(1, 1);
    private readonly object _sync = new();

    private readonly uint _excludeMonitorFormatId;
    private readonly uint _canIncludeInHistoryFormatId;
    private readonly uint _clipboardViewerIgnoreFormatId;
    private readonly uint _pngFormatId;

    private nint _hostHwnd;
    private bool _isMonitoring;
    private bool _listenerRegistered;
    private bool _disposed;
    private string? _suppressedSelfCopyHash;
    private DateTimeOffset _suppressedExpiresAtUtc;

    public ClipboardMonitorAdapter(ILogger? logger = null)
        : this(new Win32ClipboardNativeBackend(), logger, null, null)
    {
    }

    internal ClipboardMonitorAdapter(
        IClipboardNativeBackend backend,
        ILogger? logger = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _delayAsync = delayAsync ?? ((ms, ct) => Task.Delay(ms, ct));

        _excludeMonitorFormatId = _backend.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeInHistoryFormatId = _backend.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        _clipboardViewerIgnoreFormatId = _backend.RegisterClipboardFormat("Clipboard Viewer Ignore");
        _pngFormatId = _backend.RegisterClipboardFormat("PNG");
    }

    public bool IsMonitoring
    {
        get
        {
            lock (_sync)
            {
                return _isMonitoring;
            }
        }
    }

    public event EventHandler<ClipboardCaptureEventArgs>? ClipboardChanged;

    public void AttachHostWindow(nint hostWindowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hostWindowHandle == 0)
        {
            throw new ArgumentException("Host window handle must be non-zero.", nameof(hostWindowHandle));
        }

        lock (_sync)
        {
            if (_hostHwnd == hostWindowHandle)
            {
                EnsureListenerRegisteredLocked();
                return;
            }

            UnregisterListenerLocked();
            _hostHwnd = hostWindowHandle;
            EnsureListenerRegisteredLocked();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _isMonitoring = true;
            EnsureListenerRegisteredLocked();
        }
    }

    public void Start(nint hostWindowHandle)
    {
        AttachHostWindow(hostWindowHandle);
        Start();
    }

    public void Stop()
    {
        lock (_sync)
        {
            _isMonitoring = false;
            UnregisterListenerLocked();
        }
    }

    public bool HandleWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (msg != WmClipboardUpdate || _disposed)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_isMonitoring)
            {
                return false;
            }
        }

        _ = ProcessClipboardUpdateSafeAsync();
        return true;
    }

    public async Task<bool> TrySetTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hash = ComputeTextHash(text);

        nint ownerHwnd;
        lock (_sync)
        {
            ownerHwnd = _hostHwnd;
        }

        await _clipboardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ArmSelfCopySuppression(hash);
            if (!await TryOpenClipboardWithRetryAsync(ownerHwnd, ct).ConfigureAwait(false))
            {
                ClearSelfCopySuppressionIfMatches(hash);
                return false;
            }

            try
            {
                var wrote = _backend.ClearAndSetClipboard(text, pngBytes: null, dibBytes: null, _pngFormatId);
                if (!wrote)
                {
                    ClearSelfCopySuppressionIfMatches(hash);
                }

                return wrote;
            }
            catch
            {
                ClearSelfCopySuppressionIfMatches(hash);
                throw;
            }
            finally
            {
                _backend.CloseClipboard();
            }
        }
        finally
        {
            _clipboardGate.Release();
        }
    }

    public async Task<bool> TrySetImagePngAsync(
        byte[] pngBytes,
        RawBgraImage? dibFallback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (pngBytes.Length == 0 || pngBytes.Length > ClipboardEntry.MaxImageBytes)
        {
            return false;
        }

        var normalizedPng = PngImageEncoder.TrimTrailingPngPadding(pngBytes);

        // Always publish BOTH a valid bottom-up CF_DIB (Paint, Office, Snipping Tool, legacy apps) and the
        // registered "PNG" format (Chrome, Discord, modern apps). Fail fast if CF_DIB cannot be built.
        var pixels = dibFallback is { IsValid: true }
            ? dibFallback
            : PngImageEncoder.TryDecodePngToBgra(normalizedPng);
        if (pixels is not { IsValid: true })
        {
            _logger?.Warn("Clipboard image restore could not decode PNG to BGRA for CF_DIB.");
            return false;
        }

        var dibBytes = PngImageEncoder.EncodeBgraToDib32(pixels);
        var hash = ComputeImageHash(normalizedPng);

        nint ownerHwnd;
        lock (_sync)
        {
            ownerHwnd = _hostHwnd;
        }

        await _clipboardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ArmSelfCopySuppression(hash);
            if (!await TryOpenClipboardWithRetryAsync(ownerHwnd, ct).ConfigureAwait(false))
            {
                ClearSelfCopySuppressionIfMatches(hash);
                return false;
            }

            try
            {
                var wrote = _backend.ClearAndSetClipboard(unicodeText: null, normalizedPng, dibBytes, _pngFormatId);
                if (!wrote)
                {
                    ClearSelfCopySuppressionIfMatches(hash);
                }

                return wrote;
            }
            catch
            {
                ClearSelfCopySuppressionIfMatches(hash);
                throw;
            }
            finally
            {
                _backend.CloseClipboard();
            }
        }
        finally
        {
            _clipboardGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _clipboardGate.Dispose();
    }

    public static string ComputeTextHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var buffer = new byte[5 + byteCount];
        Encoding.ASCII.GetBytes("text:", buffer.AsSpan(0, 5));
        Encoding.UTF8.GetBytes(text, buffer.AsSpan(5));
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }

    public static string ComputeImageHash(ReadOnlySpan<byte> pngBytes)
    {
        var buffer = new byte[6 + pngBytes.Length];
        Encoding.ASCII.GetBytes("image:", buffer.AsSpan(0, 6));
        pngBytes.CopyTo(buffer.AsSpan(6));
        return Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
    }

    internal async Task ProcessClipboardUpdateAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        nint ownerHwnd;
        lock (_sync)
        {
            if (!_isMonitoring)
            {
                return;
            }

            ownerHwnd = _hostHwnd;
        }

        ClipboardCaptureEventArgs? captured = null;
        await _clipboardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_isMonitoring)
            {
                return;
            }

            if (!await TryOpenClipboardWithRetryAsync(ownerHwnd, ct).ConfigureAwait(false))
            {
                _logger?.Warn("Clipboard locked by another process after 3 OpenClipboard retries; skipping update.");
                return;
            }

            try
            {
                if (ShouldExcludeCurrentClipboardLocked())
                {
                    return;
                }

                captured = ReadSupportedPayloadLocked();
            }
            finally
            {
                _backend.CloseClipboard();
            }
        }
        finally
        {
            _clipboardGate.Release();
        }

        if (captured is null || TryConsumeSuppressedSelfCopy(captured.ContentHash))
        {
            return;
        }

        ClipboardChanged?.Invoke(this, captured);
    }

    private async Task ProcessClipboardUpdateSafeAsync()
    {
        try
        {
            await ProcessClipboardUpdateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn("Unhandled error processing clipboard update.", ex);
        }
    }

    private bool ShouldExcludeCurrentClipboardLocked()
    {
        if (_excludeMonitorFormatId != 0 && _backend.IsClipboardFormatAvailable(_excludeMonitorFormatId))
        {
            return true;
        }

        if (_clipboardViewerIgnoreFormatId != 0 && _backend.IsClipboardFormatAvailable(_clipboardViewerIgnoreFormatId))
        {
            return true;
        }

        if (_canIncludeInHistoryFormatId != 0 && _backend.IsClipboardFormatAvailable(_canIncludeInHistoryFormatId))
        {
            if (!_backend.TryReadDwordFormat(_canIncludeInHistoryFormatId, out var canInclude) || canInclude == 0)
            {
                return true;
            }
        }

        if (_backend.IsClipboardFormatAvailable(CfHDrop))
        {
            return true;
        }

        return false;
    }

    private ClipboardCaptureEventArgs? ReadSupportedPayloadLocked()
    {
        var now = _utcNowProvider().ToUniversalTime();
        var sourceApp = _backend.GetForegroundProcessName();
        if (!string.IsNullOrWhiteSpace(sourceApp) && sourceApp.Length > ClipboardEntry.MaxSourceAppNameLength)
        {
            sourceApp = sourceApp[..ClipboardEntry.MaxSourceAppNameLength];
        }

        if (_backend.IsClipboardFormatAvailable(CfUnicodeText))
        {
            var text = _backend.TryReadUnicodeText(ClipboardEntry.MaxTextLength);
            if (!string.IsNullOrEmpty(text))
            {
                return new ClipboardCaptureEventArgs
                {
                    ContentType = ClipboardContentType.Text,
                    TextContent = text,
                    ContentHash = ComputeTextHash(text),
                    SourceAppName = sourceApp,
                    ByteSize = Encoding.UTF8.GetByteCount(text),
                    CapturedAtUtc = now
                };
            }

            return null;
        }

        if (_pngFormatId != 0 && _backend.IsClipboardFormatAvailable(_pngFormatId))
        {
            var pngBytes = _backend.TryReadFormatBytes(_pngFormatId, ClipboardEntry.MaxImageBytes);
            if (pngBytes is { Length: > 0 })
            {
                pngBytes = PngImageEncoder.TrimTrailingPngPadding(pngBytes);
                if (pngBytes.Length > 0)
                {
                    return new ClipboardCaptureEventArgs
                    {
                        ContentType = ClipboardContentType.Image,
                        ImagePngBytes = pngBytes,
                        ContentHash = ComputeImageHash(pngBytes),
                        SourceAppName = sourceApp,
                        ByteSize = pngBytes.Length,
                        CapturedAtUtc = now
                    };
                }
            }
        }

        if (_backend.IsClipboardFormatAvailable(CfDib))
        {
            // Accept up to 4x MaxImageBytes raw DIB uncompressed before PNG compression shrinks it under MaxImageBytes.
            var dibBytes = _backend.TryReadFormatBytes(CfDib, ClipboardEntry.MaxImageBytes * 4);
            if (dibBytes is { Length: >= 40 })
            {
                var bgra = PngImageEncoder.TryDecodeDibToBgra(dibBytes);
                if (bgra is { IsValid: true })
                {
                    var pngBytes = PngImageEncoder.EncodeBgra32ToPng(bgra);
                    if (pngBytes.Length <= ClipboardEntry.MaxImageBytes)
                    {
                        return new ClipboardCaptureEventArgs
                        {
                            ContentType = ClipboardContentType.Image,
                            ImagePngBytes = pngBytes,
                            ImageBgra = bgra,
                            ContentHash = ComputeImageHash(pngBytes),
                            SourceAppName = sourceApp,
                            ByteSize = pngBytes.Length,
                            CapturedAtUtc = now
                        };
                    }
                }
            }
        }

        return null;
    }

    private async Task<bool> TryOpenClipboardWithRetryAsync(nint ownerHwnd, CancellationToken ct)
    {
        if (_backend.OpenClipboard(ownerHwnd))
        {
            return true;
        }

        foreach (var delayMs in RetryDelaysMs)
        {
            ct.ThrowIfCancellationRequested();
            await _delayAsync(delayMs, ct).ConfigureAwait(false);
            if (_backend.OpenClipboard(ownerHwnd))
            {
                return true;
            }
        }

        return false;
    }

    private void ArmSelfCopySuppression(string hash)
    {
        lock (_sync)
        {
            _suppressedSelfCopyHash = hash;
            _suppressedExpiresAtUtc = _utcNowProvider().ToUniversalTime().Add(SelfCopySuppressionWindow);
        }
    }

    private void ClearSelfCopySuppressionIfMatches(string hash)
    {
        lock (_sync)
        {
            if (string.Equals(_suppressedSelfCopyHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                _suppressedSelfCopyHash = null;
                _suppressedExpiresAtUtc = default;
            }
        }
    }

    private bool TryConsumeSuppressedSelfCopy(string hash)
    {
        lock (_sync)
        {
            if (string.IsNullOrEmpty(_suppressedSelfCopyHash))
            {
                return false;
            }

            var now = _utcNowProvider().ToUniversalTime();
            var matches = now <= _suppressedExpiresAtUtc
                && string.Equals(_suppressedSelfCopyHash, hash, StringComparison.OrdinalIgnoreCase);
            if (matches || now > _suppressedExpiresAtUtc)
            {
                _suppressedSelfCopyHash = null;
                _suppressedExpiresAtUtc = default;
            }

            return matches;
        }
    }

    private void EnsureListenerRegisteredLocked()
    {
        if (_disposed || !_isMonitoring || _listenerRegistered || _hostHwnd == 0)
        {
            return;
        }

        _listenerRegistered = _backend.AddClipboardFormatListener(_hostHwnd);
        if (!_listenerRegistered)
        {
            _logger?.Warn("AddClipboardFormatListener failed on host window handle.");
        }
    }

    private void UnregisterListenerLocked()
    {
        if (!_listenerRegistered || _hostHwnd == 0)
        {
            _listenerRegistered = false;
            return;
        }

        try
        {
            _backend.RemoveClipboardFormatListener(_hostHwnd);
        }
        catch
        {
            // Ignore teardown errors if host HWND is already closing.
        }
        finally
        {
            _listenerRegistered = false;
        }
    }
}
