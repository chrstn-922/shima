using System.Text;
using System.Text.RegularExpressions;
using Shima.Core.Contracts;
using Shima.Infrastructure.Storage;

namespace Shima.Infrastructure.Configuration;

/// <summary>
/// Persists application preferences atomically while encrypting sensitive credentials with current-user Windows DPAPI.
/// </summary>
public sealed class AppPreferencesService : IAppPreferencesService
{
    private readonly AppPreferencesStore _store;
    private readonly ISecureSecretStore _secretStore;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private AppPreferences _raw = new();
    private AppPreferencesSnapshot _snapshot = ToSnapshot(new AppPreferences());

    public AppPreferencesService(
        AppPreferencesStore store,
        ISecureSecretStore secretStore,
        ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _logger = logger;
    }

    public AppPreferencesSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<AppPreferencesSnapshot>? PreferencesChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var loaded = await _store.LoadAsync().ConfigureAwait(false);
        var snapshot = ToSnapshot(loaded);

        lock (_sync)
        {
            _raw = loaded;
            _snapshot = snapshot;
        }

        PreferencesChanged?.Invoke(this, snapshot);
    }

    public async Task SaveAsync(
        AppPreferencesSnapshot snapshot,
        string? newOpenCodeServerPassword = null,
        bool clearOpenCodeServerPassword = false,
        string? newLicenseKey = null,
        bool clearLicenseKey = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        byte[]? protectedPassword;
        byte[]? protectedLicense;

        lock (_sync)
        {
            protectedPassword = clearOpenCodeServerPassword
                ? null
                : !string.IsNullOrEmpty(newOpenCodeServerPassword)
                    ? _secretStore.ProtectForCurrentUser(Encoding.UTF8.GetBytes(newOpenCodeServerPassword))
                    : _raw.OpenCodeServerPassword;

            protectedLicense = clearLicenseKey
                ? null
                : !string.IsNullOrEmpty(newLicenseKey)
                    ? _secretStore.ProtectForCurrentUser(Encoding.UTF8.GetBytes(newLicenseKey))
                    : _raw.LicenseState;
        }

        var nextRaw = new AppPreferences
        {
            StartWithWindows = snapshot.StartWithWindows,
            TargetMonitorMode = ToTargetMode(snapshot.MonitorSelectionMode),
            SpecificMonitorDeviceId = snapshot.SpecificMonitorDeviceId,
            ExpandOnHover = snapshot.ExpandOnHover,
            HoverExpandDelayMs = Math.Clamp(snapshot.HoverExpandDelayMs, 0, 2000),
            AutoCollapseDelayMs = Math.Clamp(snapshot.AutoCollapseDelayMs, 100, 5000),
            GlobalToggleHotkey = string.IsNullOrWhiteSpace(snapshot.GlobalToggleHotkey) ? null : snapshot.GlobalToggleHotkey.Trim(),
            FullscreenBreakthroughAlerts = snapshot.FullscreenBreakthroughAlerts,
            SyncInboxDismissalWithActionCenter = snapshot.SyncInboxDismissalWithActionCenter,
            ClipboardMaxItems = Math.Clamp(snapshot.ClipboardMaxItems, 20, 1000),
            ClipboardRetentionDays = Math.Clamp(snapshot.ClipboardRetentionDays, 1, 365),
            InboxRetentionDays = Math.Clamp(snapshot.InboxRetentionDays, 1, 365),
            ClaudeCodeOptInEnabled = snapshot.ClaudeCodeOptInEnabled,
            OpenCodeOptInEnabled = snapshot.OpenCodeOptInEnabled,
            OpenCodeEndpointUrl = string.IsNullOrWhiteSpace(snapshot.OpenCodeEndpointUrl)
                ? null
                : snapshot.OpenCodeEndpointUrl.Trim(),
            OpenCodeServerPassword = protectedPassword,
            UpdatesOfflineMode = snapshot.UpdatesOfflineMode,
            LicenseState = protectedLicense
        };

        await _store.SaveAsync(nextRaw).ConfigureAwait(false);
        var nextSnapshot = ToSnapshot(nextRaw);

        lock (_sync)
        {
            _raw = nextRaw;
            _snapshot = nextSnapshot;
        }

        _logger?.Info("Updated application preferences (credentials stored DPAPI-encrypted).");
        PreferencesChanged?.Invoke(this, nextSnapshot);
    }

    public string? ReadOpenCodeServerPassword()
    {
        byte[]? ciphertext;
        lock (_sync)
        {
            ciphertext = _raw.OpenCodeServerPassword;
        }

        if (ciphertext is null || ciphertext.Length == 0)
        {
            return null;
        }

        try
        {
            var plaintext = _secretStore.UnprotectForCurrentUser(ciphertext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception)
        {
            _logger?.Warn("Unable to unprotect OpenCode server password from DPAPI store.", exception);
            return null;
        }
    }

    public string? ReadLicenseStatePayload()
    {
        byte[]? ciphertext;
        lock (_sync)
        {
            ciphertext = _raw.LicenseState;
        }

        if (ciphertext is null || ciphertext.Length == 0)
        {
            return null;
        }

        try
        {
            var plaintext = _secretStore.UnprotectForCurrentUser(ciphertext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception)
        {
            _logger?.Warn("Unable to unprotect license payload from DPAPI store.", exception);
            return null;
        }
    }

    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var defaults = new AppPreferences();
        await _store.SaveAsync(defaults).ConfigureAwait(false);
        var nextSnapshot = ToSnapshot(defaults);

        lock (_sync)
        {
            _raw = defaults;
            _snapshot = nextSnapshot;
        }

        _logger?.Info("Reset application preferences to defaults.");
        PreferencesChanged?.Invoke(this, nextSnapshot);
    }

    private static AppPreferencesSnapshot ToSnapshot(AppPreferences raw) => new()
    {
        StartWithWindows = raw.StartWithWindows,
        MonitorSelectionMode = ToMonitorSelectionMode(raw.TargetMonitorMode),
        SpecificMonitorDeviceId = raw.SpecificMonitorDeviceId,
        ExpandOnHover = raw.ExpandOnHover,
        HoverExpandDelayMs = raw.HoverExpandDelayMs,
        AutoCollapseDelayMs = raw.AutoCollapseDelayMs,
        GlobalToggleHotkey = raw.GlobalToggleHotkey,
        FullscreenBreakthroughAlerts = raw.FullscreenBreakthroughAlerts,
        SyncInboxDismissalWithActionCenter = raw.SyncInboxDismissalWithActionCenter,
        ClipboardMaxItems = raw.ClipboardMaxItems,
        ClipboardRetentionDays = raw.ClipboardRetentionDays,
        InboxRetentionDays = raw.InboxRetentionDays,
        ClaudeCodeOptInEnabled = raw.ClaudeCodeOptInEnabled,
        OpenCodeOptInEnabled = raw.OpenCodeOptInEnabled,
        OpenCodeEndpointUrl = raw.OpenCodeEndpointUrl,
        HasOpenCodeServerPassword = raw.OpenCodeServerPassword is { Length: > 0 },
        UpdatesOfflineMode = raw.UpdatesOfflineMode,
        HasLicenseKey = raw.LicenseState is { Length: > 0 }
    };

    private static TargetMonitorMode ToTargetMode(MonitorSelectionMode mode) => mode switch
    {
        MonitorSelectionMode.ActiveCursor => TargetMonitorMode.ActiveCursor,
        MonitorSelectionMode.SpecificMonitorId => TargetMonitorMode.SpecificMonitorId,
        _ => TargetMonitorMode.Primary
    };

    private static MonitorSelectionMode ToMonitorSelectionMode(TargetMonitorMode mode) => mode switch
    {
        TargetMonitorMode.ActiveCursor => MonitorSelectionMode.ActiveCursor,
        TargetMonitorMode.SpecificMonitorId => MonitorSelectionMode.SpecificMonitorId,
        _ => MonitorSelectionMode.Primary
    };
}

/// <summary>
/// Reads and exports redacted diagnostic logs for About &amp; Diagnostics without leaking credentials or user file paths.
/// </summary>
public sealed partial class DiagnosticLogService : IDiagnosticLogService
{
    private static readonly Regex SecretPattern = CreateSecretPattern();
    private static readonly Regex LicenseTokenPattern = CreateLicenseTokenPattern();

    public DiagnosticLogService(string? logDirectoryPath = null)
    {
        LogDirectoryPath = string.IsNullOrWhiteSpace(logDirectoryPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shima", "logs")
            : logDirectoryPath;
    }

    public string LogDirectoryPath { get; }

    public IReadOnlyList<string> GetRecentSanitizedLines(int maxLines = 100)
    {
        var bounded = Math.Clamp(maxLines, 1, 2000);
        if (!Directory.Exists(LogDirectoryPath))
        {
            return Array.Empty<string>();
        }

        var latestFile = Directory
            .EnumerateFiles(LogDirectoryPath, "*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latestFile is null)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        using var stream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(SanitizeLine(line));
        }

        return lines.Count <= bounded
            ? lines
            : lines.Skip(lines.Count - bounded).ToArray();
    }

    public void ClearLogs()
    {
        if (!Directory.Exists(LogDirectoryPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(LogDirectoryPath, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                stream.SetLength(0);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public string ExportRedactedBundle(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var fileName = $"shima-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt";
        var outputPath = Path.Combine(destinationDirectory, fileName);

        var lines = GetRecentSanitizedLines(500);
        var builder = new StringBuilder();
        builder.AppendLine($"Shima Sanitized Diagnostics Bundle ({DateTimeOffset.UtcNow:O})");
        builder.AppendLine($"Runtime: {Environment.Version}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine("--- Recent Log Lines (Sanitized) ---");
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
        return outputPath;
    }

    private static string SanitizeLine(string line)
    {
        var redacted = SecretPattern.Replace(line, "$1=[REDACTED]");
        return LicenseTokenPattern.Replace(redacted, "[REDACTED_LICENSE]");
    }

    [GeneratedRegex(@"(?i)\b(password|passphrase|secret|token|bearer|api[_-]?key|authorization)\b\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled)]
    private static partial Regex CreateSecretPattern();

    [GeneratedRegex(@"SHIMA1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex CreateLicenseTokenPattern();
}
