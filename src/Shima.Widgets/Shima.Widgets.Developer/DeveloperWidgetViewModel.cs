using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.Widgets.Developer;

/// <summary>
/// ViewModel orchestrating Developer Activity across independent coding-agent adapters:
/// collapsed notch bar (Idle/Working/Attention/Finished/ErrorFailed), compact-pill coexistence,
/// 10s Finished dwell, 15m expanded retention & Error cap with idle timer suspension,
/// auto-open routing, confirmed-only Allow/Deny actions, and explicit unsupported capability diagnostics.
/// </summary>
public sealed class DeveloperWidgetViewModel : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan FinishedBarDwellDuration = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ErrorBarMaxDwellDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ExpandedRowRetentionDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ApprovalPeekTtl = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<ICodingAgentAdapter> _adapters;
    private readonly IAgentSessionLogRepository _logRepository;
    private readonly IAppPreferencesService _preferences;
    private readonly IOneShotTimer _finishedDwellTimer;
    private readonly IOneShotTimer _errorCapTimer;
    private readonly IOneShotTimer _retentionTimer;
    private readonly bool _ownsTimers;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly object _sync = new();

    private readonly Dictionary<string, ICodingAgentAdapter> _adaptersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionStateRecord> _sessionRecords = new(StringComparer.Ordinal);

    private IWidgetContext? _context;
    private Action<Action>? _uiDispatcher;
    private bool _isWidgetEnabled = true;
    private PowerProfile _powerProfile = PowerProfile.AC;
    private DeveloperBarVisualState _barVisualState = DeveloperBarVisualState.Hidden;
    private DateTimeOffset? _finishedBarExpiresAtUtc;
    private DateTimeOffset? _errorBarExpiresAtUtc;
    private bool _errorBarAcknowledged;
    private string? _lastPeekedRequestId;
    private string _settingsFeedbackMessage = string.Empty;
    private bool _disposed;

    public DeveloperWidgetViewModel(
        IReadOnlyList<ICodingAgentAdapter> adapters,
        IAgentSessionLogRepository logRepository,
        IAppPreferencesService preferences,
        IOneShotTimer? finishedDwellTimer = null,
        IOneShotTimer? errorCapTimer = null,
        IOneShotTimer? retentionTimer = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _logRepository = logRepository ?? throw new ArgumentNullException(nameof(logRepository));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _ownsTimers = finishedDwellTimer is null && errorCapTimer is null && retentionTimer is null;
        _finishedDwellTimer = finishedDwellTimer ?? new OneShotTimer();
        _errorCapTimer = errorCapTimer ?? new OneShotTimer();
        _retentionTimer = retentionTimer ?? new OneShotTimer();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

        foreach (var adapter in _adapters)
        {
            _adaptersById[adapter.AdapterId] = adapter;
            Diagnostics.Add(new AdapterDiagnosticsViewModel(
                adapter.AdapterId,
                adapter.DisplayName,
                adapter.ConnectionState,
                new AdapterHealthResult(AdapterHealthStatus.Disconnected, "Not checked yet."),
                adapter.Capabilities,
                IsHookOrPluginInstalled(adapter)));
        }
    }

    public ObservableCollection<AgentSessionRowViewModel> Sessions { get; } = [];

    public ObservableCollection<AdapterDiagnosticsViewModel> Diagnostics { get; } = [];

    public DeveloperBarVisualState BarVisualState
    {
        get
        {
            lock (_sync)
            {
                return _barVisualState;
            }
        }
    }

    public bool IsCollapsedBarVisible => BarVisualState != DeveloperBarVisualState.Hidden;

    public double BarFillPercent => BarVisualState switch
    {
        DeveloperBarVisualState.Working => 55.0,
        DeveloperBarVisualState.Attention => 62.0,
        DeveloperBarVisualState.Hidden => 0.0,
        _ => 100.0
    };

    public string BarFillBrushKey => BarVisualState switch
    {
        DeveloperBarVisualState.Working => "AgentStatusWorkingBrush",
        DeveloperBarVisualState.Attention => "AgentStatusAttentionBrush",
        DeveloperBarVisualState.Finished => "AgentStatusFinishedBrush",
        DeveloperBarVisualState.ErrorFailed => "AgentStatusErrorBrush",
        _ => "AgentStatusIdleBrush"
    };

    public string BarAccessibleStatus => BarVisualState switch
    {
        DeveloperBarVisualState.Hidden => "Coding agent summary status: Disabled",
        DeveloperBarVisualState.Idle => "Coding agent summary status: Idle",
        DeveloperBarVisualState.Working => "Coding agent summary status: Working",
        DeveloperBarVisualState.Attention => "Coding agent summary status: Needs attention",
        DeveloperBarVisualState.Finished => "Coding agent summary status: Finished",
        DeveloperBarVisualState.ErrorFailed => "Coding agent summary status: Error or failed",
        _ => "Coding agent summary status: Idle"
    };

    public bool IsEmpty => Sessions.Count == 0;

    public string EmptyStateLabel => "No active sessions";

    public string HeaderStatusSummary
    {
        get
        {
            lock (_sync)
            {
                var activeCount = _sessionRecords.Values.Count(r =>
                    r.Snapshot.Status is AgentStatus.Working or AgentStatus.AwaitingApproval or AgentStatus.NeedsAttention
                    && !r.IsDismissed);
                return activeCount == 0
                    ? "Idle"
                    : activeCount == 1 ? "1 active session" : $"{activeCount} active sessions";
            }
        }
    }

    public bool ShouldAutoSelectOnExpand
    {
        get
        {
            lock (_sync)
            {
                if (!_isWidgetEnabled || !HasAnyOptIn(_preferences.Current))
                {
                    return false;
                }

                return _sessionRecords.Values.Any(r =>
                    !r.IsDismissed
                    && (r.Snapshot.Status is AgentStatus.Working or AgentStatus.AwaitingApproval or AgentStatus.NeedsAttention));
            }
        }
    }

    public bool HasPendingTimers =>
        _finishedDwellTimer.IsPending || _errorCapTimer.IsPending || _retentionTimer.IsPending;

    public string SettingsFeedbackMessage
    {
        get
        {
            lock (_sync)
            {
                return _settingsFeedbackMessage;
            }
        }
        private set
        {
            lock (_sync)
            {
                if (string.Equals(_settingsFeedbackMessage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _settingsFeedbackMessage = value;
            }

            DispatchToUi(() => OnPropertyChanged());
        }
    }

    public string AccessibleStatus => $"Developer Activity: {HeaderStatusSummary}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AttachUiDispatcher(Action<Action> dispatch) =>
        _uiDispatcher = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public async Task InitializeAsync(IWidgetContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        foreach (var adapter in _adapters)
        {
            adapter.SessionUpdated += OnAdapterSessionUpdated;
            adapter.SessionRemoved += OnAdapterSessionRemoved;
            adapter.ConnectionStateChanged += OnAdapterConnectionStateChanged;
        }

        _preferences.PreferencesChanged += OnPreferencesChanged;

        RecomputeAllStates(emitEvents: true);
        await RefreshDiagnosticsAsync(ct).ConfigureAwait(false);
    }

    public void AcknowledgeCollapsedBarHoverOrOpen()
    {
        bool changed;
        lock (_sync)
        {
            changed = _finishedBarExpiresAtUtc is not null
                || (_errorBarExpiresAtUtc is not null && !_errorBarAcknowledged);

            _finishedBarExpiresAtUtc = null;
            if (_errorBarExpiresAtUtc is not null)
            {
                _errorBarAcknowledged = true;
                _errorBarExpiresAtUtc = null;
            }

            _finishedDwellTimer.Cancel();
            _errorCapTimer.Cancel();
        }

        if (changed)
        {
            RecomputeAllStates(emitEvents: true);
        }
    }

    public void RefreshTimestamps()
    {
        var now = _utcNowProvider().ToUniversalTime();
        DispatchToUi(() =>
        {
            foreach (var row in Sessions)
            {
                row.RefreshTimestamp(now);
            }
        });
    }

    public async Task<PermissionActionResult> ApproveSessionAsync(string sessionId, string requestId) =>
        await RespondToApprovalCoreAsync(sessionId, requestId, allow: true, reason: null).ConfigureAwait(false);

    public async Task<PermissionActionResult> DenySessionAsync(string sessionId, string requestId, string? reason = null) =>
        await RespondToApprovalCoreAsync(sessionId, requestId, allow: false, reason).ConfigureAwait(false);

    public async Task<bool> FocusSessionTerminalAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        SessionStateRecord? record;
        lock (_sync)
        {
            _sessionRecords.TryGetValue(sessionId, out record);
        }

        if (record is null || !_adaptersById.TryGetValue(record.Snapshot.AdapterId, out var adapter))
        {
            return false;
        }

        if (!adapter.Capabilities.SupportsTerminalFocus || record.Snapshot.ProcessId is not > 0)
        {
            return false;
        }

        return await adapter.FocusTerminalAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    public void DismissSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!_sessionRecords.TryGetValue(sessionId, out var record))
            {
                return;
            }

            if (record.Snapshot.Status is AgentStatus.Working or AgentStatus.AwaitingApproval
                && record.Snapshot.ConnectionState == AgentConnectionState.Connected)
            {
                return;
            }

            _sessionRecords.Remove(sessionId);
        }

        SyncRowsCollection();
        RecomputeAllStates(emitEvents: true);
        ArmOrCancelRetentionTimer();
    }

    public async Task RefreshDiagnosticsAsync(CancellationToken ct = default)
    {
        foreach (var adapter in _adapters)
        {
            ct.ThrowIfCancellationRequested();
            AdapterHealthResult health;
            try
            {
                health = await adapter.CheckHealthAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                health = new AdapterHealthResult(AdapterHealthStatus.Unavailable, ex.Message);
            }

            UpdateDiagnosticItem(adapter, health);
        }
    }

    public async Task CheckAdapterHealthAsync(string adapterId, CancellationToken ct = default)
    {
        if (!_adaptersById.TryGetValue(adapterId, out var adapter))
        {
            return;
        }

        AdapterHealthResult health;
        try
        {
            health = await adapter.CheckHealthAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            health = new AdapterHealthResult(AdapterHealthStatus.Unavailable, ex.Message);
        }

        UpdateDiagnosticItem(adapter, health);
        SettingsFeedbackMessage = $"{adapter.DisplayName}: {health.Message}";
    }

    public async Task InstallOrReinstallHooksAsync(string adapterId, CancellationToken ct = default)
    {
        if (!_adaptersById.TryGetValue(adapterId, out var adapter))
        {
            return;
        }

        HookInstallResult result;
        if (adapter is IClaudeCodeHookManager claudeManager)
        {
            result = await claudeManager.InstallHooksAsync(null, ct).ConfigureAwait(false);
        }
        else if (adapter is IOpenCodePluginManager pluginManager)
        {
            result = await pluginManager.InstallPluginAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return;
        }

        SettingsFeedbackMessage = result.Message;
        await CheckAdapterHealthAsync(adapterId, ct).ConfigureAwait(false);
    }

    public async Task UninstallHooksAsync(string adapterId, CancellationToken ct = default)
    {
        if (!_adaptersById.TryGetValue(adapterId, out var adapter))
        {
            return;
        }

        HookInstallResult result;
        if (adapter is IClaudeCodeHookManager claudeManager)
        {
            result = await claudeManager.UninstallHooksAsync(ct).ConfigureAwait(false);
        }
        else if (adapter is IOpenCodePluginManager pluginManager)
        {
            result = await pluginManager.UninstallPluginAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return;
        }

        SettingsFeedbackMessage = result.Message;
        await CheckAdapterHealthAsync(adapterId, ct).ConfigureAwait(false);
    }

    public void OnShellStateChanged(IslandShellState newState)
    {
        if (newState == IslandShellState.Suspended)
        {
            OnPowerProfileChanged(PowerProfile.DisplayOff);
        }
        else if (newState == IslandShellState.Expanded)
        {
            AcknowledgeCollapsedBarHoverOrOpen();
            RefreshTimestamps();
        }
    }

    public void OnPowerProfileChanged(PowerProfile profile)
    {
        var previous = _powerProfile;
        _powerProfile = profile;

        if (profile == PowerProfile.DisplayOff)
        {
            // Suspend all timers strictly while display is off / system suspended (0.0% idle CPU).
            _finishedDwellTimer.Cancel();
            _errorCapTimer.Cancel();
            _retentionTimer.Cancel();
            return;
        }

        if (previous == PowerProfile.DisplayOff)
        {
            PurgeExpiredRows(_utcNowProvider());
            RecomputeAllStates(emitEvents: true);
            ArmOrCancelRetentionTimer();
        }
    }

    public void SetWidgetEnabled(bool enabled)
    {
        _isWidgetEnabled = enabled;
        if (!enabled)
        {
            _finishedDwellTimer.Cancel();
            _errorCapTimer.Cancel();
            _retentionTimer.Cancel();
            _context?.Island.UpdateCompactPill(DeveloperWidgetModule.Id, null, CompactPillPriority.Background);
        }

        RecomputeAllStates(emitEvents: true);
    }

    public void OpenSettings() =>
        _context?.Events.Publish(new OpenSettingsRequestedEvent("DeveloperIntegrations"));

    internal void TriggerFinishedDwellExpiredForTesting() => OnFinishedBarDwellExpired();

    internal void TriggerErrorCapExpiredForTesting() => OnErrorBarCapExpired();

    internal void PurgeExpiredRowsForTesting(DateTimeOffset nowUtc) => PurgeExpiredRows(nowUtc);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var adapter in _adapters)
        {
            adapter.SessionUpdated -= OnAdapterSessionUpdated;
            adapter.SessionRemoved -= OnAdapterSessionRemoved;
            adapter.ConnectionStateChanged -= OnAdapterConnectionStateChanged;
        }

        _preferences.PreferencesChanged -= OnPreferencesChanged;

        if (_ownsTimers)
        {
            _finishedDwellTimer.Dispose();
            _errorCapTimer.Dispose();
            _retentionTimer.Dispose();
        }
        else
        {
            _finishedDwellTimer.Cancel();
            _errorCapTimer.Cancel();
            _retentionTimer.Cancel();
        }
    }

    private async Task<PermissionActionResult> RespondToApprovalCoreAsync(
        string sessionId,
        string requestId,
        bool allow,
        string? reason)
    {
        ICodingAgentAdapter? adapter;
        AgentSessionRowViewModel? row;

        lock (_sync)
        {
            if (!_sessionRecords.TryGetValue(sessionId, out var record)
                || !_adaptersById.TryGetValue(record.Snapshot.AdapterId, out adapter))
            {
                return new PermissionActionResult(false, null, "Session adapter is unavailable.");
            }

            row = record.Row;
        }

        DispatchToUi(() => row.SetApprovalPending(true, "Pending confirmation..."));

        PermissionActionResult result;
        try
        {
            result = await adapter.RespondToApprovalAsync(sessionId, requestId, allow, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new PermissionActionResult(false, null, ex.Message);
        }

        if (!result.Confirmed)
        {
            // Never simulate or commit unconfirmed approval! Restore actionable state cleanly with error message.
            DispatchToUi(() => row.SetApprovalPending(false, result.ErrorMessage ?? "Confirmation failed."));
            return result;
        }

        DispatchToUi(() => row.SetApprovalPending(false, null));
        if (string.Equals(_lastPeekedRequestId, requestId, StringComparison.Ordinal))
        {
            _lastPeekedRequestId = null;
            _context?.Events.Publish(new NotificationPeekDismissRequestedEvent(DeveloperWidgetModule.Id, requestId));
        }

        return result;
    }

    private void OnAdapterSessionUpdated(object? sender, AgentSessionSnapshot snapshot)
    {
        if (_disposed || sender is not ICodingAgentAdapter adapter)
        {
            return;
        }

        var now = _utcNowProvider().ToUniversalTime();
        var supportsFocus = adapter.Capabilities.SupportsTerminalFocus;
        string? dismissedPeekReqId = null;
        AgentSessionSnapshot? actionablePeekSnapshot = null;

        lock (_sync)
        {
            var existed = _sessionRecords.TryGetValue(snapshot.SessionId, out var existing);
            var previousStatus = existing?.Snapshot.Status;
            var previousReqId = existing?.Snapshot.PendingRequestId;
            var wasFatal = existing?.Snapshot.IsFatalError == true && previousStatus == AgentStatus.NeedsAttention;

            DateTimeOffset? retainedUntilUtc = null;
            if (snapshot.Status == AgentStatus.Finished
                || (snapshot.IsFatalError && snapshot.Status == AgentStatus.NeedsAttention))
            {
                retainedUntilUtc = now + ExpandedRowRetentionDuration;
            }

            if (existed && existing is not null)
            {
                existing.Snapshot = snapshot;
                existing.IsDismissed = false;
                existing.RetainedUntilUtc = retainedUntilUtc;
                DispatchToUi(() => existing.Row.Update(snapshot, supportsFocus, now));
            }
            else
            {
                var row = new AgentSessionRowViewModel(snapshot, supportsFocus, now);
                _sessionRecords[snapshot.SessionId] = new SessionStateRecord(snapshot, row, retainedUntilUtc);
            }

            // Detect permission terminal-sync dismissal (answered directly inside terminal)
            if (!string.IsNullOrWhiteSpace(previousReqId)
                && !string.Equals(previousReqId, snapshot.PendingRequestId, StringComparison.Ordinal))
            {
                dismissedPeekReqId = previousReqId;
            }

            // Detect new actionable permission request for notification peek
            if (snapshot.Status == AgentStatus.AwaitingApproval
                && snapshot.HasActionablePermissionRequest
                && !string.IsNullOrWhiteSpace(snapshot.PendingRequestId)
                && !string.Equals(_lastPeekedRequestId, snapshot.PendingRequestId, StringComparison.Ordinal))
            {
                _lastPeekedRequestId = snapshot.PendingRequestId;
                actionablePeekSnapshot = snapshot;
            }

            // Finished bar dwell: if session transitioned to Finished and no active sessions remain, hold 10s
            if (snapshot.Status == AgentStatus.Finished && previousStatus != AgentStatus.Finished)
            {
                _finishedBarExpiresAtUtc = now + FinishedBarDwellDuration;
                if (_powerProfile != PowerProfile.DisplayOff)
                {
                    _finishedDwellTimer.Schedule(FinishedBarDwellDuration, OnFinishedBarDwellExpired);
                }
            }
            else if (snapshot.Status is AgentStatus.Working or AgentStatus.AwaitingApproval)
            {
                _finishedBarExpiresAtUtc = null;
                _finishedDwellTimer.Cancel();
            }

            // Fatal error collapsed bar: hold up to 15m unless acknowledged
            var isNowFatal = snapshot.IsFatalError && snapshot.Status == AgentStatus.NeedsAttention;
            if (isNowFatal && !wasFatal)
            {
                _errorBarAcknowledged = false;
                _errorBarExpiresAtUtc = now + ErrorBarMaxDwellDuration;
                if (_powerProfile != PowerProfile.DisplayOff)
                {
                    _errorCapTimer.Schedule(ErrorBarMaxDwellDuration, OnErrorBarCapExpired);
                }
            }
        }

        if (dismissedPeekReqId is not null)
        {
            _context?.Events.Publish(new NotificationPeekDismissRequestedEvent(DeveloperWidgetModule.Id, dismissedPeekReqId));
        }

        if (actionablePeekSnapshot is not null)
        {
            PublishApprovalPeek(actionablePeekSnapshot);
        }

        SyncRowsCollection();
        RecomputeAllStates(emitEvents: true);
        ArmOrCancelRetentionTimer();
        PersistSessionLogAsync(snapshot);
    }

    private void OnAdapterSessionRemoved(object? sender, string sessionId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        bool removed;
        lock (_sync)
        {
            removed = _sessionRecords.Remove(sessionId);
        }

        if (removed)
        {
            SyncRowsCollection();
            RecomputeAllStates(emitEvents: true);
            ArmOrCancelRetentionTimer();
        }
    }

    private void OnAdapterConnectionStateChanged(object? sender, AgentConnectionState nextState)
    {
        if (_disposed || sender is not ICodingAgentAdapter adapter)
        {
            return;
        }

        var healthStatus = nextState switch
        {
            AgentConnectionState.Connected => AdapterHealthStatus.Healthy,
            AgentConnectionState.Reconnecting => AdapterHealthStatus.Reconnecting,
            AgentConnectionState.Unavailable => AdapterHealthStatus.Unavailable,
            _ => AdapterHealthStatus.Disconnected
        };

        var diag = Diagnostics.FirstOrDefault(d => string.Equals(d.AdapterId, adapter.AdapterId, StringComparison.Ordinal));
        if (diag is not null && diag.HealthStatus == AdapterHealthStatus.AuthenticationRequired && nextState == AgentConnectionState.Disconnected)
        {
            healthStatus = AdapterHealthStatus.AuthenticationRequired;
        }

        DispatchToUi(() =>
        {
            var existingMsg = diag?.HealthMessage ?? string.Empty;
            diag?.Update(
                nextState,
                new AdapterHealthResult(healthStatus, existingMsg),
                adapter.Capabilities,
                IsHookOrPluginInstalled(adapter));
        });

        _ = RefreshAdapterDiagnosticAsync(adapter);
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot updated)
    {
        if (_disposed)
        {
            return;
        }

        RecomputeAllStates(emitEvents: true);
        _ = RefreshDiagnosticsAsync(CancellationToken.None);
    }

    private async Task RefreshAdapterDiagnosticAsync(ICodingAgentAdapter adapter)
    {
        try
        {
            var health = await adapter.CheckHealthAsync(CancellationToken.None).ConfigureAwait(false);
            UpdateDiagnosticItem(adapter, health);
        }
        catch
        {
            // Never fault on background diagnostic refresh.
        }
    }

    private void UpdateDiagnosticItem(ICodingAgentAdapter adapter, AdapterHealthResult health)
    {
        var diag = Diagnostics.FirstOrDefault(d => string.Equals(d.AdapterId, adapter.AdapterId, StringComparison.Ordinal));
        if (diag is null)
        {
            return;
        }

        var installed = IsHookOrPluginInstalled(adapter);
        DispatchToUi(() => diag.Update(adapter.ConnectionState, health, adapter.Capabilities, installed));
    }

    private void PublishApprovalPeek(AgentSessionSnapshot snapshot)
    {
        if (_context is null || snapshot.PendingRequestId is null)
        {
            return;
        }

        var reqId = snapshot.PendingRequestId;
        var sessionId = snapshot.SessionId;
        var taskTitle = !string.IsNullOrWhiteSpace(snapshot.TaskSummary) ? snapshot.TaskSummary : snapshot.WorkspaceName;
        var body = string.IsNullOrWhiteSpace(snapshot.PendingActionSummary)
            ? $"{snapshot.PendingToolName ?? "Tool"} requires confirmation"
            : $"{snapshot.PendingToolName ?? "Approve"}: {snapshot.PendingActionSummary}";

        var allowAction = new NotificationPeekAction(
            "Allow",
            $"Allow permission request for {taskTitle}",
            IsPrimary: true,
            () => ApproveSessionAsync(sessionId, reqId));

        var denyAction = new NotificationPeekAction(
            "Deny",
            $"Deny permission request for {taskTitle}",
            IsPrimary: false,
            () => DenySessionAsync(sessionId, reqId));

        _context.Events.Publish(new NotificationPeekContentEvent(
            DeveloperWidgetModule.Id,
            $"{snapshot.WorkspaceName} · Awaiting approval",
            body,
            reqId,
            allowAction,
            denyAction));

        _context.Island.RequestPeekNotification(NotificationPriority.Critical, ApprovalPeekTtl);
    }

    private void OnFinishedBarDwellExpired()
    {
        lock (_sync)
        {
            _finishedBarExpiresAtUtc = null;
        }

        RecomputeAllStates(emitEvents: true);
    }

    private void OnErrorBarCapExpired()
    {
        lock (_sync)
        {
            _errorBarExpiresAtUtc = null;
            _errorBarAcknowledged = true;
        }

        RecomputeAllStates(emitEvents: true);
    }

    private void PurgeExpiredRows(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc.ToUniversalTime();
        var removedAny = false;

        lock (_sync)
        {
            foreach (var kvp in _sessionRecords.ToArray())
            {
                if (kvp.Value.RetainedUntilUtc.HasValue && kvp.Value.RetainedUntilUtc.Value <= cutoff)
                {
                    _sessionRecords.Remove(kvp.Key);
                    removedAny = true;
                }
            }
        }

        if (removedAny)
        {
            SyncRowsCollection();
            RecomputeAllStates(emitEvents: true);
        }

        ArmOrCancelRetentionTimer();
    }

    private void ArmOrCancelRetentionTimer()
    {
        if (_disposed || _powerProfile == PowerProfile.DisplayOff)
        {
            _retentionTimer.Cancel();
            return;
        }

        DateTimeOffset? nextExpiry;
        lock (_sync)
        {
            nextExpiry = _sessionRecords.Values
                .Where(r => r.RetainedUntilUtc.HasValue)
                .Select(r => r.RetainedUntilUtc)
                .OrderBy(dt => dt)
                .FirstOrDefault();
        }

        if (!nextExpiry.HasValue)
        {
            _retentionTimer.Cancel();
            return;
        }

        var delay = nextExpiry.Value - _utcNowProvider().ToUniversalTime();
        if (delay < TimeSpan.FromMilliseconds(50))
        {
            delay = TimeSpan.FromMilliseconds(50);
        }

        _retentionTimer.Schedule(delay, () => PurgeExpiredRows(_utcNowProvider()));
    }

    private void RecomputeAllStates(bool emitEvents)
    {
        var now = _utcNowProvider().ToUniversalTime();
        DeveloperBarVisualState previousBar;
        DeveloperBarVisualState nextBar;
        string? compactBadgeText = null;
        CompactPillPriority compactPriority = CompactPillPriority.Background;

        lock (_sync)
        {
            previousBar = _barVisualState;
            var optedIn = _isWidgetEnabled && HasAnyOptIn(_preferences.Current);

            if (!optedIn)
            {
                nextBar = DeveloperBarVisualState.Hidden;
                _finishedBarExpiresAtUtc = null;
                _errorBarExpiresAtUtc = null;
                _finishedDwellTimer.Cancel();
                _errorCapTimer.Cancel();
            }
            else
            {
                if (_finishedBarExpiresAtUtc.HasValue && _finishedBarExpiresAtUtc.Value <= now)
                {
                    _finishedBarExpiresAtUtc = null;
                }

                if (_errorBarExpiresAtUtc.HasValue && _errorBarExpiresAtUtc.Value <= now)
                {
                    _errorBarExpiresAtUtc = null;
                    _errorBarAcknowledged = true;
                }

                var hasApproval = _sessionRecords.Values.Any(r =>
                    !r.IsDismissed && r.Snapshot.Status == AgentStatus.AwaitingApproval);
                var hasNonFatalAttention = _sessionRecords.Values.Any(r =>
                    !r.IsDismissed && r.Snapshot.Status == AgentStatus.NeedsAttention && !r.Snapshot.IsFatalError);
                var hasFatalError = _sessionRecords.Values.Any(r =>
                    !r.IsDismissed && r.Snapshot.Status == AgentStatus.NeedsAttention && r.Snapshot.IsFatalError);
                var hasWorking = _sessionRecords.Values.Any(r =>
                    !r.IsDismissed && r.Snapshot.Status == AgentStatus.Working);
                var hasFinishedInDwell = _finishedBarExpiresAtUtc.HasValue
                    && _sessionRecords.Values.Any(r => !r.IsDismissed && r.Snapshot.Status == AgentStatus.Finished);

                // Strict priority: AwaitingApproval / NeedsAttention [incl. Error/Failed] > Working > Finished [10s dwell] > Idle
                if (hasApproval || hasNonFatalAttention)
                {
                    nextBar = DeveloperBarVisualState.Attention;
                    compactBadgeText = hasApproval ? "Approval needed" : "Needs attention";
                    compactPriority = CompactPillPriority.Urgent;
                }
                else if (hasFatalError && !_errorBarAcknowledged)
                {
                    nextBar = DeveloperBarVisualState.ErrorFailed;
                    compactBadgeText = "Agent error";
                    compactPriority = CompactPillPriority.Urgent;
                }
                else if (hasWorking)
                {
                    nextBar = DeveloperBarVisualState.Working;
                    var count = _sessionRecords.Values.Count(r => !r.IsDismissed && r.Snapshot.Status == AgentStatus.Working);
                    compactBadgeText = count == 1 ? "Agent working" : $"{count} agents working";
                    compactPriority = CompactPillPriority.Background;
                }
                else if (hasFinishedInDwell)
                {
                    nextBar = DeveloperBarVisualState.Finished;
                    compactBadgeText = "Agent finished";
                    compactPriority = CompactPillPriority.Background;
                }
                else
                {
                    nextBar = DeveloperBarVisualState.Idle;
                    compactBadgeText = null;
                    compactPriority = CompactPillPriority.Background;
                }
            }

            _barVisualState = nextBar;
        }

        if (emitEvents && _context is not null)
        {
            _context.Island.UpdateCompactPill(DeveloperWidgetModule.Id, compactBadgeText, compactPriority);

            if ((previousBar == DeveloperBarVisualState.Hidden) != (nextBar == DeveloperBarVisualState.Hidden))
            {
                _context.Events.Publish(new CollapsedStatusIndicatorChangedEvent(
                    DeveloperWidgetModule.Id,
                    nextBar != DeveloperBarVisualState.Hidden));
            }
        }

        DispatchToUi(() =>
        {
            OnPropertyChanged(nameof(BarVisualState));
            OnPropertyChanged(nameof(IsCollapsedBarVisible));
            OnPropertyChanged(nameof(BarFillPercent));
            OnPropertyChanged(nameof(BarFillBrushKey));
            OnPropertyChanged(nameof(BarAccessibleStatus));
            OnPropertyChanged(nameof(HeaderStatusSummary));
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    private void SyncRowsCollection()
    {
        List<AgentSessionRowViewModel> ordered;
        lock (_sync)
        {
            ordered = _sessionRecords.Values
                .Where(r => !r.IsDismissed)
                .OrderBy(r => GetSortPriority(r.Snapshot))
                .ThenByDescending(r => r.Snapshot.LastUpdatedAtUtc)
                .Select(r => r.Row)
                .ToList();
        }

        DispatchToUi(() =>
        {
            Sessions.Clear();
            foreach (var row in ordered)
            {
                Sessions.Add(row);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HeaderStatusSummary));
            OnPropertyChanged(nameof(AccessibleStatus));
        });
    }

    private void PersistSessionLogAsync(AgentSessionSnapshot snapshot)
    {
        var adapterType = string.Equals(snapshot.AdapterId, "claude-code", StringComparison.Ordinal)
            ? "ClaudeCode"
            : "OpenCode";

        var entry = new AgentSessionLogEntry
        {
            SessionId = snapshot.SessionId,
            AdapterType = adapterType,
            WorkspacePath = snapshot.WorkspacePath ?? string.Empty,
            ProcessId = snapshot.ProcessId,
            LastStatus = snapshot.Status,
            StartedAtUtc = snapshot.StartedAtUtc,
            LastSeenAtUtc = snapshot.LastUpdatedAtUtc
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await _logRepository.UpsertAsync(entry, CancellationToken.None).ConfigureAwait(false);
                if (snapshot.Status == AgentStatus.Finished)
                {
                    await _logRepository.PruneFinishedSessionsAsync(50, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Persistence errors must never disrupt live session rendering.
            }
        });
    }

    private static int GetSortPriority(AgentSessionSnapshot snapshot) => snapshot.Status switch
    {
        AgentStatus.AwaitingApproval => 0,
        AgentStatus.NeedsAttention => 1,
        AgentStatus.Working => 2,
        _ => 3
    };

    private static bool HasAnyOptIn(AppPreferencesSnapshot prefs) =>
        prefs.ClaudeCodeOptInEnabled || prefs.OpenCodeOptInEnabled;

    private static bool IsHookOrPluginInstalled(ICodingAgentAdapter adapter) => adapter switch
    {
        IClaudeCodeHookManager claude => claude.IsHookInstalled(),
        IOpenCodePluginManager openCode => openCode.IsPluginInstalled(),
        _ => false
    };

    private void DispatchToUi(Action action)
    {
        if (_uiDispatcher is null)
        {
            action();
            return;
        }

        _uiDispatcher(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class SessionStateRecord(
        AgentSessionSnapshot snapshot,
        AgentSessionRowViewModel row,
        DateTimeOffset? retainedUntilUtc)
    {
        public AgentSessionSnapshot Snapshot { get; set; } = snapshot;
        public AgentSessionRowViewModel Row { get; } = row;
        public DateTimeOffset? RetainedUntilUtc { get; set; } = retainedUntilUtc;
        public bool IsDismissed { get; set; }
    }
}
