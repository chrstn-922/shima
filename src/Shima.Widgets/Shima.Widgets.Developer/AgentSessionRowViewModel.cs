using System.ComponentModel;
using System.Runtime.CompilerServices;
using Shima.Core.Contracts;

namespace Shima.Widgets.Developer;

/// <summary>
/// UI-bindable representation of an isolated coding-agent session in the Developer Activity list.
/// Exposes Allow/Deny ONLY when HasActionablePermissionRequest is true and updates confirmation status strictly from adapter acks.
/// </summary>
public sealed class AgentSessionRowViewModel : INotifyPropertyChanged
{
    private AgentSessionSnapshot _snapshot;
    private bool _isApprovalPending;
    private string? _approvalFeedbackText;
    private string _timestampLabel;

    public AgentSessionRowViewModel(
        AgentSessionSnapshot snapshot,
        bool adapterSupportsTerminalFocus,
        DateTimeOffset nowUtc)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        AdapterSupportsTerminalFocus = adapterSupportsTerminalFocus;
        _timestampLabel = FormatTimestamp(snapshot, nowUtc);
    }

    public string SessionId => _snapshot.SessionId;

    public string AdapterId => _snapshot.AdapterId;

    public string AdapterDisplayName => string.Equals(_snapshot.AdapterId, "claude-code", StringComparison.Ordinal)
        ? "Claude Code"
        : "OpenCode";

    public string AdapterBadgeText => string.Equals(_snapshot.AdapterId, "claude-code", StringComparison.Ordinal)
        ? "CC"
        : "OC";

    public bool IsClaudeCode => string.Equals(_snapshot.AdapterId, "claude-code", StringComparison.Ordinal);

    public string WorkspaceName => string.IsNullOrWhiteSpace(_snapshot.WorkspaceName)
        ? "Workspace"
        : _snapshot.WorkspaceName;

    public string WorkspacePath => _snapshot.WorkspacePath;

    public string TaskTitle => !string.IsNullOrWhiteSpace(_snapshot.TaskSummary)
        ? _snapshot.TaskSummary!
        : $"{AdapterDisplayName} session";

    public string RepositorySubtitle => string.IsNullOrWhiteSpace(_snapshot.WorkspacePath)
        ? $"{AdapterDisplayName} · {WorkspaceName}"
        : WorkspaceName;

    public AgentStatus Status => _snapshot.Status;

    public bool IsFatalError => _snapshot.IsFatalError;

    public AgentConnectionState ConnectionState => _snapshot.ConnectionState;

    public bool AdapterSupportsTerminalFocus { get; private set; }

    public int? ProcessId => _snapshot.ProcessId;

    public string? PendingToolName => _snapshot.PendingToolName;

    public string? PendingActionSummary => _snapshot.PendingActionSummary;

    public string? PendingRequestId => _snapshot.PendingRequestId;

    public DateTimeOffset StartedAtUtc => _snapshot.StartedAtUtc;

    public DateTimeOffset LastUpdatedAtUtc => _snapshot.LastUpdatedAtUtc;

    public bool HasActionablePermissionRequest => _snapshot.HasActionablePermissionRequest;

    public bool ShowApprovalButtons =>
        _snapshot.Status == AgentStatus.AwaitingApproval
        && _snapshot.HasActionablePermissionRequest
        && !string.IsNullOrWhiteSpace(_snapshot.PendingRequestId)
        && !_isApprovalPending;

    public bool IsApprovalPending
    {
        get => _isApprovalPending;
        private set
        {
            if (_isApprovalPending != value)
            {
                _isApprovalPending = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowApprovalButtons));
                OnPropertyChanged(nameof(HasPendingDetail));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public string? ApprovalFeedbackText
    {
        get => _approvalFeedbackText;
        private set
        {
            if (!string.Equals(_approvalFeedbackText, value, StringComparison.Ordinal))
            {
                _approvalFeedbackText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPendingDetail));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public bool HasPendingDetail =>
        _isApprovalPending
        || !string.IsNullOrWhiteSpace(_approvalFeedbackText)
        || (_snapshot.Status == AgentStatus.AwaitingApproval && !string.IsNullOrWhiteSpace(_snapshot.PendingActionSummary));

    public string DetailText
    {
        get
        {
            if (_isApprovalPending)
            {
                return "Pending confirmation...";
            }

            if (!string.IsNullOrWhiteSpace(_approvalFeedbackText))
            {
                return _approvalFeedbackText!;
            }

            if (!string.IsNullOrWhiteSpace(_snapshot.PendingActionSummary))
            {
                return string.IsNullOrWhiteSpace(_snapshot.PendingToolName)
                    ? _snapshot.PendingActionSummary!
                    : $"{_snapshot.PendingToolName}: {_snapshot.PendingActionSummary}";
            }

            return string.Empty;
        }
    }

    public bool CanFocusTerminal => AdapterSupportsTerminalFocus && _snapshot.ProcessId is > 0;

    public bool IsNonApprovalRowClickable => !ShowApprovalButtons && CanFocusTerminal;

    public bool CanDismiss =>
        _snapshot.Status == AgentStatus.Finished
        || _snapshot.IsFatalError
        || _snapshot.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable;

    public string StatusKey
    {
        get
        {
            if (_snapshot.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable
                && _snapshot.Status != AgentStatus.Finished)
            {
                return "Disconnected";
            }

            if (_snapshot.IsFatalError && _snapshot.Status == AgentStatus.NeedsAttention)
            {
                return "ErrorFailed";
            }

            return _snapshot.Status switch
            {
                AgentStatus.Working => "Working",
                AgentStatus.NeedsAttention => "NeedsAttention",
                AgentStatus.AwaitingApproval => "AwaitingApproval",
                AgentStatus.Finished => "Finished",
                _ => "Idle"
            };
        }
    }

    public string StatusLabel => StatusKey switch
    {
        "Working" => "Working",
        "NeedsAttention" => "Needs attention",
        "AwaitingApproval" => "Awaiting approval",
        "Finished" => "Finished",
        "ErrorFailed" => "Error / Failed",
        "Disconnected" => _snapshot.ConnectionState == AgentConnectionState.Unavailable ? "Unavailable" : "Disconnected",
        _ => "Idle"
    };

    public string TimestampLabel => _timestampLabel;

    public string AllowAutomationName => $"Allow permission request for {TaskTitle}";

    public string DenyAutomationName => $"Deny permission request for {TaskTitle}";

    public string FocusAutomationName => $"Focus terminal for {TaskTitle}";

    public string DismissAutomationName => $"Dismiss session {TaskTitle}";

    public string RowAutomationName => $"Session status: {StatusLabel}, {TimestampLabel}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(
        AgentSessionSnapshot snapshot,
        bool adapterSupportsTerminalFocus,
        DateTimeOffset nowUtc)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        AdapterSupportsTerminalFocus = adapterSupportsTerminalFocus;
        _timestampLabel = FormatTimestamp(snapshot, nowUtc);
        if (snapshot.Status != AgentStatus.AwaitingApproval && !snapshot.HasActionablePermissionRequest)
        {
            _isApprovalPending = false;
            _approvalFeedbackText = null;
        }

        OnPropertyChanged(string.Empty);
    }

    public void RefreshTimestamp(DateTimeOffset nowUtc)
    {
        var next = FormatTimestamp(_snapshot, nowUtc);
        if (!string.Equals(_timestampLabel, next, StringComparison.Ordinal))
        {
            _timestampLabel = next;
            OnPropertyChanged(nameof(TimestampLabel));
            OnPropertyChanged(nameof(RowAutomationName));
        }
    }

    public void SetApprovalPending(bool pending, string? feedbackText = null)
    {
        _isApprovalPending = pending;
        _approvalFeedbackText = feedbackText;
        OnPropertyChanged(nameof(IsApprovalPending));
        OnPropertyChanged(nameof(ApprovalFeedbackText));
        OnPropertyChanged(nameof(ShowApprovalButtons));
        OnPropertyChanged(nameof(IsNonApprovalRowClickable));
        OnPropertyChanged(nameof(HasPendingDetail));
        OnPropertyChanged(nameof(DetailText));
    }

    public static string FormatTimestamp(AgentSessionSnapshot snapshot, DateTimeOffset nowUtc)
    {
        var anchor = snapshot.Status == AgentStatus.Finished
            ? snapshot.LastUpdatedAtUtc
            : snapshot.StartedAtUtc;

        var elapsed = nowUtc.ToUniversalTime() - anchor.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var mins = (int)elapsed.TotalMinutes;
        if (snapshot.Status == AgentStatus.Finished)
        {
            return mins < 1 ? "Just now" : $"{mins} min ago";
        }

        if (mins < 1)
        {
            return "< 1 min";
        }

        if (mins < 60)
        {
            return $"{mins} min";
        }

        var hours = mins / 60;
        var remMins = mins % 60;
        return remMins == 0 ? $"{hours}h" : $"{hours}h {remMins}m";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
