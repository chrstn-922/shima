namespace Shima.Core.Contracts;

/// <summary>
/// Canonical coding agent session states normalized across independent adapters.
/// </summary>
public enum AgentStatus
{
    Working,
    NeedsAttention,
    AwaitingApproval,
    Finished
}

/// <summary>
/// Live transport state for a coding-agent adapter or session.
/// </summary>
public enum AgentConnectionState
{
    Connected,
    Reconnecting,
    Disconnected,
    Unavailable
}

/// <summary>
/// Diagnostic health status surfaced in Developer Integrations Settings.
/// </summary>
public enum AdapterHealthStatus
{
    Healthy,
    Reconnecting,
    Disconnected,
    AuthenticationRequired,
    Unavailable
}

public sealed record AdapterHealthResult(
    AdapterHealthStatus Status,
    string? Message);

public sealed record AgentCapabilities(
    bool SupportsInteractiveApproval,
    bool SupportsSessionElapsedTimer,
    bool SupportsTerminalFocus,
    bool SupportsMultiSessionIsolation,
    bool IsOptInConfigured,
    IReadOnlyList<string> UnsupportedCapabilities);

public sealed record PermissionActionResult(
    bool Confirmed,
    string? ConfirmationSource,
    string? ErrorMessage);

public sealed record AgentSessionSnapshot(
    string SessionId,
    string AdapterId,
    string WorkspaceName,
    string WorkspacePath,
    int? ProcessId,
    AgentStatus Status,
    bool IsFatalError,
    AgentConnectionState ConnectionState,
    string? TaskSummary,
    string? PendingToolName,
    string? PendingActionSummary,
    string? PendingRequestId,
    bool HasActionablePermissionRequest,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastUpdatedAtUtc);

public sealed record HookInstallResult(
    bool Success,
    string Message,
    string? BackupPath = null);

/// <summary>
/// Persisted coding-agent session log row stored in SQLite agent_session_logs per data-model.md.
/// </summary>
public sealed record AgentSessionLogEntry
{
    public required string SessionId { get; init; }

    public required string AdapterType { get; init; }

    public required string WorkspacePath { get; init; }

    public int? ProcessId { get; init; }

    public AgentStatus LastStatus { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset LastSeenAtUtc { get; init; }
}

/// <summary>
/// Independent opt-in coding-agent adapter contract (Claude Code, OpenCode, or future integrations).
/// </summary>
public interface ICodingAgentAdapter : IAsyncDisposable
{
    string AdapterId { get; }

    string DisplayName { get; }

    AgentCapabilities Capabilities { get; }

    AgentConnectionState ConnectionState { get; }

    Task<AdapterHealthResult> CheckHealthAsync(CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task<PermissionActionResult> RespondToApprovalAsync(
        string sessionId,
        string requestId,
        bool allow,
        string? reason,
        CancellationToken ct = default);

    Task<bool> FocusTerminalAsync(string sessionId, CancellationToken ct = default);

    event EventHandler<AgentSessionSnapshot>? SessionUpdated;

    event EventHandler<string>? SessionRemoved;

    event EventHandler<AgentConnectionState>? ConnectionStateChanged;
}

/// <summary>
/// Explicit opt-in installer/uninstaller for Claude Code hooks in ~/.claude/settings.json.
/// </summary>
public interface IClaudeCodeHookManager
{
    string SettingsFilePath { get; }

    bool IsHookInstalled();

    Task<HookInstallResult> InstallHooksAsync(string? bridgeExecutablePath = null, CancellationToken ct = default);

    Task<HookInstallResult> UninstallHooksAsync(CancellationToken ct = default);
}

/// <summary>
/// Explicit opt-in installer/uninstaller for OpenCode TypeScript plugin hook in ~/.config/opencode/plugins/shima-bridge.ts.
/// </summary>
public interface IOpenCodePluginManager
{
    string PluginFilePath { get; }

    bool IsPluginInstalled();

    Task<HookInstallResult> InstallPluginAsync(CancellationToken ct = default);

    Task<HookInstallResult> UninstallPluginAsync(CancellationToken ct = default);
}

/// <summary>
/// Local SQLite WAL repository managing historical and active agent session logs.
/// </summary>
public interface IAgentSessionLogRepository
{
    Task UpsertAsync(AgentSessionLogEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AgentSessionLogEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default);

    Task<int> PruneFinishedSessionsAsync(int keepFinishedCount = 50, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Optional widget capability exposing a compact status view inside the Collapsed island notch.
/// </summary>
public interface ICollapsedStatusViewProvider
{
    object? CreateCollapsedStatusView();
}

/// <summary>
/// Optional widget capability indicating whether expanding the island should auto-select this widget
/// and receiving hover notifications when the user hovers the collapsed notch.
/// </summary>
public interface IExpandAutoSelectProvider
{
    bool ShouldAutoSelectOnExpand { get; }

    void OnCollapsedNotchHovered();
}
