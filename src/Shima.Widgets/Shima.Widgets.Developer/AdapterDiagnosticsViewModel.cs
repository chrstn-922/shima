using System.ComponentModel;
using System.Runtime.CompilerServices;
using Shima.Core.Contracts;

namespace Shima.Widgets.Developer;

/// <summary>
/// Read-only diagnostics snapshot for an independent coding-agent adapter in Settings,
/// explicitly displaying health, auth requirements, and unsupported capabilities.
/// </summary>
public sealed class AdapterDiagnosticsViewModel : INotifyPropertyChanged
{
    private AgentConnectionState _connectionState;
    private AdapterHealthStatus _healthStatus;
    private string _healthMessage;
    private AgentCapabilities _capabilities;
    private bool _isHookOrPluginInstalled;

    public AdapterDiagnosticsViewModel(
        string adapterId,
        string displayName,
        AgentConnectionState connectionState,
        AdapterHealthResult health,
        AgentCapabilities capabilities,
        bool isHookOrPluginInstalled)
    {
        AdapterId = adapterId;
        DisplayName = displayName;
        _connectionState = connectionState;
        _healthStatus = health.Status;
        _healthMessage = health.Message ?? string.Empty;
        _capabilities = capabilities;
        _isHookOrPluginInstalled = isHookOrPluginInstalled;
    }

    public string AdapterId { get; }

    public string DisplayName { get; }

    public bool IsClaudeCode => string.Equals(AdapterId, "claude-code", StringComparison.Ordinal);

    public bool IsOpenCode => string.Equals(AdapterId, "opencode", StringComparison.Ordinal);

    public AgentConnectionState ConnectionState => _connectionState;

    public AdapterHealthStatus HealthStatus => _healthStatus;

    public bool IsAuthenticationRequired => _healthStatus == AdapterHealthStatus.AuthenticationRequired;

    public string HealthStatusLabel => _healthStatus switch
    {
        AdapterHealthStatus.Healthy => "Healthy",
        AdapterHealthStatus.Reconnecting => "Reconnecting",
        AdapterHealthStatus.AuthenticationRequired => "Authentication required",
        AdapterHealthStatus.Unavailable => "Unavailable",
        _ => "Disconnected"
    };

    public string HealthMessage => _healthMessage;

    public string ConnectionStateLabel => _connectionState switch
    {
        AgentConnectionState.Connected => "Connected",
        AgentConnectionState.Reconnecting => "Reconnecting",
        AgentConnectionState.Unavailable => "Unavailable",
        _ => "Disconnected"
    };

    public bool IsOptInConfigured => _capabilities.IsOptInConfigured;

    public bool SupportsInteractiveApproval => _capabilities.SupportsInteractiveApproval;

    public bool SupportsTerminalFocus => _capabilities.SupportsTerminalFocus;

    public bool SupportsMultiSessionIsolation => _capabilities.SupportsMultiSessionIsolation;

    public bool IsHookOrPluginInstalled => _isHookOrPluginInstalled;

    public string InstallButtonLabel => IsClaudeCode
        ? (_isHookOrPluginInstalled ? "Reinstall Claude hooks" : "Install Claude hooks")
        : (_isHookOrPluginInstalled ? "Reinstall OpenCode plugin" : "Install OpenCode plugin");

    public string UninstallButtonLabel => IsClaudeCode ? "Uninstall hooks" : "Uninstall plugin";

    public IReadOnlyList<string> UnsupportedCapabilities => _capabilities.UnsupportedCapabilities;

    public bool HasUnsupportedCapabilities => _capabilities.UnsupportedCapabilities.Count > 0;

    public string CapabilitySummary =>
        $"Opt-in: {(IsOptInConfigured ? "Enabled" : "Disabled")} · " +
        $"Approvals: {(SupportsInteractiveApproval ? "Supported" : "Unsupported/Read-only")} · " +
        $"Focus terminal: {(SupportsTerminalFocus ? "Supported" : "Unavailable")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(
        AgentConnectionState connectionState,
        AdapterHealthResult health,
        AgentCapabilities capabilities,
        bool isHookOrPluginInstalled)
    {
        _connectionState = connectionState;
        _healthStatus = health.Status;
        _healthMessage = health.Message ?? string.Empty;
        _capabilities = capabilities;
        _isHookOrPluginInstalled = isHookOrPluginInstalled;
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
