namespace Shima.Core.Contracts;

public enum IslandStyle
{
    DockedNotch = 0,
    FloatingPill = 1
}

/// <summary>
/// Read-safe snapshot of user preferences across all 10 Settings navigation categories without exposing raw secret ciphertext or plaintext.
/// </summary>
public sealed record AppPreferencesSnapshot
{
    public bool StartWithWindows { get; init; }
    public MonitorSelectionMode MonitorSelectionMode { get; init; } = MonitorSelectionMode.Primary;
    public string? SpecificMonitorDeviceId { get; init; }
    public IslandStyle IslandStyle { get; init; } = IslandStyle.DockedNotch;
    public bool LockIslandPosition { get; init; }
    public double IslandOffsetXDip { get; init; }
    public double? IslandFloatingTopOffsetDip { get; init; }
    public bool ExpandOnHover { get; init; } = true;
    public int HoverExpandDelayMs { get; init; } = 350;
    public int AutoCollapseDelayMs { get; init; } = 450;
    public string? GlobalToggleHotkey { get; init; } = "Ctrl+Alt+Space";
    public bool FullscreenBreakthroughAlerts { get; init; }
    public bool SyncInboxDismissalWithActionCenter { get; init; } = true;
    public int ClipboardMaxItems { get; init; } = 100;
    public int ClipboardRetentionDays { get; init; } = 30;
    public int InboxRetentionDays { get; init; } = 14;
    public bool ClaudeCodeOptInEnabled { get; init; }
    public bool OpenCodeOptInEnabled { get; init; }
    public string? OpenCodeEndpointUrl { get; init; }
    public bool HasOpenCodeServerPassword { get; init; }
    public bool UpdatesOfflineMode { get; init; }
    public bool HasLicenseKey { get; init; }
}

/// <summary>
/// Manages persistent application preferences and DPAPI-protected developer credentials.
/// </summary>
public interface IAppPreferencesService
{
    AppPreferencesSnapshot Current { get; }

    event EventHandler<AppPreferencesSnapshot>? PreferencesChanged;

    Task InitializeAsync(CancellationToken ct = default);

    Task SaveAsync(
        AppPreferencesSnapshot snapshot,
        string? newOpenCodeServerPassword = null,
        bool clearOpenCodeServerPassword = false,
        string? newLicenseKey = null,
        bool clearLicenseKey = false,
        CancellationToken ct = default);

    string? ReadOpenCodeServerPassword();

    string? ReadLicenseStatePayload() => null;

    Task ResetToDefaultsAsync(CancellationToken ct = default);
}

/// <summary>
/// Exposes redacted diagnostic log viewing and sanitized bundle export for About &amp; Diagnostics.
/// </summary>
public interface IDiagnosticLogService
{
    string LogDirectoryPath { get; }

    IReadOnlyList<string> GetRecentSanitizedLines(int maxLines = 100);

    void ClearLogs();

    string ExportRedactedBundle(string destinationDirectory);
}

/// <summary>
/// Read model describing a registered widget module for the Settings Widgets section.
/// </summary>
public sealed record WidgetManagementItem(
    string WidgetId,
    string DisplayName,
    bool IsEnabled,
    int SortOrder,
    bool IsFaulted);

/// <summary>
/// Thin UI coordinator bridging the Settings Widgets view to widget configuration persistence and runtime lifecycle.
/// </summary>
public interface IWidgetSettingsCoordinator
{
    Task<IReadOnlyList<WidgetManagementItem>> GetWidgetsAsync(CancellationToken ct = default);

    Task SetEnabledAsync(string widgetId, bool isEnabled, CancellationToken ct = default);

    Task ResetSettingsAsync(string widgetId, CancellationToken ct = default);

    Task UpdateOrderAsync(IReadOnlyList<string> orderedWidgetIds, CancellationToken ct = default);

    object? CreateSettingsView(string widgetId);
}
