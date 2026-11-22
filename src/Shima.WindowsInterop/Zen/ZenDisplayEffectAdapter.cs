using System.Globalization;
using System.Text.Json;
using Shima.Core.Contracts;
using Windows.Win32;
using Windows.Win32.UI.Magnification;

namespace Shima.WindowsInterop.Zen;

internal interface IZenDisplayNativeBackend
{
    bool IsColorEffectModified();

    bool ApplyColorMatrix(float[] matrix25);

    bool ResetColorMatrix();
}

internal interface IRuntimeJournalStore
{
    RuntimeRecoveryJournal? Read(out bool wasCorruptOrUnreadable);

    void WriteAtomic(RuntimeRecoveryJournal journal);
}

internal sealed unsafe class Win32ZenDisplayNativeBackend : IZenDisplayNativeBackend
{
    private readonly object _nativeSync = new();
    private bool _magInitialized;

    public bool IsColorEffectModified()
    {
        lock (_nativeSync)
        {
            var needTempInit = !_magInitialized;
            if (needTempInit && !PInvoke.MagInitialize())
            {
                return false;
            }

            try
            {
                MAGCOLOREFFECT effect = default;
                if (!PInvoke.MagGetFullscreenColorEffect(out effect))
                {
                    return false;
                }

                var span = new ReadOnlySpan<float>(&effect, 25);
                return !ZenDisplayEffectAdapter.IsIdentityMatrix(span);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (needTempInit)
                {
                    _ = PInvoke.MagUninitialize();
                }
            }
        }
    }

    public bool ApplyColorMatrix(float[] matrix25)
    {
        if (matrix25.Length != 25)
        {
            throw new ArgumentException("MAGCOLOREFFECT requires exactly 25 elements.", nameof(matrix25));
        }

        lock (_nativeSync)
        {
            if (!_magInitialized)
            {
                if (!PInvoke.MagInitialize())
                {
                    return false;
                }

                _magInitialized = true;
            }

            MAGCOLOREFFECT effect = default;
            var dst = new Span<float>(&effect, 25);
            matrix25.AsSpan().CopyTo(dst);
            return PInvoke.MagSetFullscreenColorEffect(effect);
        }
    }

    public bool ResetColorMatrix()
    {
        lock (_nativeSync)
        {
            var didInit = _magInitialized;
            if (!didInit && !PInvoke.MagInitialize())
            {
                return false;
            }

            try
            {
                MAGCOLOREFFECT identity = default;
                var dst = new Span<float>(&identity, 25);
                ZenDisplayEffectAdapter.IdentityMatrix.AsSpan().CopyTo(dst);
                return PInvoke.MagSetFullscreenColorEffect(identity);
            }
            finally
            {
                _ = PInvoke.MagUninitialize();
                _magInitialized = false;
            }
        }
    }
}

internal sealed class FileRuntimeJournalStore : IRuntimeJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _journalPath;

    public FileRuntimeJournalStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedRoot = Path.GetFullPath(dataRoot);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, RuntimeRecoveryJournal.DefaultRelativePath));
        var prefix = Path.TrimEndingDirectorySeparator(normalizedRoot) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Runtime journal path escaped data root.");
        }

        _journalPath = combined;
    }

    public RuntimeRecoveryJournal? Read(out bool wasCorruptOrUnreadable)
    {
        wasCorruptOrUnreadable = false;
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_journalPath);
            var parsed = JsonSerializer.Deserialize<RuntimeRecoveryJournal>(json, JsonOptions);
            if (parsed is null)
            {
                wasCorruptOrUnreadable = true;
                BackupCorruptJournal();
                return null;
            }

            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            wasCorruptOrUnreadable = true;
            BackupCorruptJournal();
            return null;
        }
    }

    public void WriteAtomic(RuntimeRecoveryJournal journal)
    {
        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _journalPath + ".tmp";
        var json = JsonSerializer.Serialize(journal, JsonOptions);
        File.WriteAllText(tempPath, json);

        if (File.Exists(_journalPath))
        {
            File.Replace(tempPath, _journalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _journalPath, overwrite: true);
        }
    }

    private void BackupCorruptJournal()
    {
        try
        {
            if (File.Exists(_journalPath))
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
                var dir = Path.GetDirectoryName(_journalPath) ?? Path.GetTempPath();
                var backup = Path.Combine(dir, $"runtime-journal.corrupt.{stamp}.json");
                File.Move(_journalPath, backup, overwrite: true);
            }
        }
        catch
        {
            // Ignore backup errors on corrupt recovery.
        }
    }
}

/// <summary>
/// Process-scoped DWM Magnification color-effect adapter (BT.709 grayscale &amp; brightness dimming)
/// with synchronous pre-effect runtime-journal.json crash-recovery watchdog.
/// </summary>
public sealed class ZenDisplayEffectAdapter : IZenDisplayEffectAdapter
{
    internal static readonly float[] IdentityMatrix =
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
        0f, 0f, 0f, 0f, 1f
    ];

    private readonly IZenDisplayNativeBackend _nativeBackend;
    private readonly IRuntimeJournalStore _journalStore;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private ZenDisplayOptions? _activeOptions;
    private bool _pausedByFullscreen;
    private bool _isEffectActive;
    private bool _wasCrashRecoveredOnStartup;
    private bool _disposed;

    public ZenDisplayEffectAdapter(string dataRoot, ILogger? logger = null)
        : this(
            new Win32ZenDisplayNativeBackend(),
            new FileRuntimeJournalStore(dataRoot),
            utcNowProvider: null,
            logger)
    {
    }

    internal ZenDisplayEffectAdapter(
        IZenDisplayNativeBackend nativeBackend,
        IRuntimeJournalStore journalStore,
        Func<DateTimeOffset>? utcNowProvider = null,
        ILogger? logger = null)
    {
        _nativeBackend = nativeBackend ?? throw new ArgumentNullException(nameof(nativeBackend));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    public bool IsEffectActive
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _isEffectActive && !_pausedByFullscreen;
            }
        }
    }

    public bool WasCrashRecoveredOnStartup
    {
        get
        {
            lock (_sync)
            {
                return _wasCrashRecoveredOnStartup;
            }
        }
    }

    public static float[] BuildColorMatrix(ZenDisplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dim = Math.Clamp(options.DimOpacity, 0.0, 0.75);
        var brightness = (float)(1.0 - dim);

        if (!options.GrayscaleEnabled && dim <= 0.001)
        {
            return (float[])IdentityMatrix.Clone();
        }

        if (options.GrayscaleEnabled)
        {
            var r = 0.2126f * brightness;
            var g = 0.7152f * brightness;
            var b = 0.0722f * brightness;
            return
            [
                r,  r,  r,  0f, 0f,
                g,  g,  g,  0f, 0f,
                b,  b,  b,  0f, 0f,
                0f, 0f, 0f, 1f, 0f,
                0f, 0f, 0f, 0f, 1f
            ];
        }

        return
        [
            brightness, 0f,         0f,         0f, 0f,
            0f,         brightness, 0f,         0f, 0f,
            0f,         0f,         brightness, 0f, 0f,
            0f,         0f,         0f,         1f, 0f,
            0f,         0f,         0f,         0f, 1f
        ];
    }

    internal static bool IsIdentityMatrix(ReadOnlySpan<float> matrix)
    {
        if (matrix.Length != 25)
        {
            return false;
        }

        for (var i = 0; i < 25; i++)
        {
            if (Math.Abs(matrix[i] - IdentityMatrix[i]) > 0.001f)
            {
                return false;
            }
        }

        return true;
    }

    public bool RecoverFromStartupIfNeeded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            var journal = _journalStore.Read(out var corruptOrUnreadable);
            var hadActiveZenFlag = corruptOrUnreadable || journal is { ZenDisplayEffectActive: true };
            var hadModifiedNative = _nativeBackend.IsColorEffectModified();

            if (hadActiveZenFlag || hadModifiedNative || journal is { LastExitClean: false })
            {
                _ = _nativeBackend.ResetColorMatrix();
                WriteJournalSafe(zenActive: false, lastExitClean: true);
            }

            _isEffectActive = false;
            _activeOptions = null;
            _pausedByFullscreen = false;
            _wasCrashRecoveredOnStartup = hadActiveZenFlag || hadModifiedNative;
            return _wasCrashRecoveredOnStartup;
        }
    }

    public bool TryApplyEffect(ZenDisplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clamped = options with { DimOpacity = Math.Clamp(options.DimOpacity, 0.0, 0.75) };
        if (!clamped.GrayscaleEnabled && clamped.DimOpacity <= 0.001)
        {
            RestoreDisplay();
            return true;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return false;
            }

            _activeOptions = clamped;
            if (_pausedByFullscreen && clamped.PauseDuringFullscreen)
            {
                _isEffectActive = true;
                return true;
            }

            // Write crash-recovery flag synchronously BEFORE altering DWM magnification color matrix.
            WriteJournalSafe(zenActive: true, lastExitClean: false);

            var matrix = BuildColorMatrix(clamped);
            if (!_nativeBackend.ApplyColorMatrix(matrix))
            {
                WriteJournalSafe(zenActive: false, lastExitClean: false);
                _isEffectActive = false;
                _activeOptions = null;
                return false;
            }

            _isEffectActive = true;
            return true;
        }
    }

    public void RestoreDisplay()
    {
        lock (_sync)
        {
            _activeOptions = null;
            _pausedByFullscreen = false;
            _isEffectActive = false;

            _ = _nativeBackend.ResetColorMatrix();
            WriteJournalSafe(zenActive: false, lastExitClean: false);
        }
    }

    public void MarkCleanExit()
    {
        lock (_sync)
        {
            _activeOptions = null;
            _pausedByFullscreen = false;
            _isEffectActive = false;

            _ = _nativeBackend.ResetColorMatrix();
            WriteJournalSafe(zenActive: false, lastExitClean: true);
        }
    }

    public void SetPausedByFullscreen(bool isFullscreen)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _pausedByFullscreen = isFullscreen;
            if (_activeOptions is not { } options)
            {
                return;
            }

            if (isFullscreen && options.PauseDuringFullscreen)
            {
                _ = _nativeBackend.ResetColorMatrix();
                WriteJournalSafe(zenActive: false, lastExitClean: false);
            }
            else if (!isFullscreen && _isEffectActive)
            {
                WriteJournalSafe(zenActive: true, lastExitClean: false);
                if (!_nativeBackend.ApplyColorMatrix(BuildColorMatrix(options)))
                {
                    WriteJournalSafe(zenActive: false, lastExitClean: false);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        MarkCleanExit();
        lock (_sync)
        {
            _disposed = true;
        }
    }

    private void WriteJournalSafe(bool zenActive, bool lastExitClean)
    {
        try
        {
            _journalStore.WriteAtomic(new RuntimeRecoveryJournal
            {
                SchemaVersion = 1,
                ZenDisplayEffectActive = zenActive,
                LastExitClean = lastExitClean,
                UpdatedAtUtc = _utcNowProvider().ToUniversalTime()
            });
        }
        catch (Exception ex)
        {
            _logger?.Warn("Failed to persist Zen runtime recovery journal.", ex);
        }
    }
}
