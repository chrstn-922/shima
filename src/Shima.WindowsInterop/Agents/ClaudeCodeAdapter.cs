using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Agents;

/// <summary>
/// Independent opt-in Claude Code integration adapter using a user-SID ACL Named Pipe server
/// (\\.\pipe\shima-agent-bridge) and ~/.claude/settings.json hook registration with timestamped backups.
/// Completely decoupled from OpenCodeAdapter.
/// </summary>
public sealed class ClaudeCodeAdapter : ICodingAgentAdapter, IClaudeCodeHookManager
{
    public const string Id = "claude-code";
    public const string DefaultPipeName = "shima-agent-bridge";
    public const string ShimaHookMarker = "--shima-managed";

    private static readonly string[] SupportedHookEvents =
    [
        "PreToolUse",
        "PermissionRequest",
        "Notification",
        "Stop",
        "SubagentStop"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAppPreferencesService _preferences;
    private readonly ILogger? _logger;
    private readonly string _pipeName;
    private readonly string _settingsFilePath;
    private readonly ITerminalWindowFocuser _windowFocuser;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly string? _defaultBridgeExecutablePath;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly ConcurrentDictionary<string, AgentSessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPipePrompt> _pendingPrompts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoopTask;
    private bool _shouldRunOnOptIn;
    private bool _preferencesSubscribed;
    private AgentConnectionState _connectionState = AgentConnectionState.Disconnected;
    private bool _disposed;

    public ClaudeCodeAdapter(IAppPreferencesService preferences, ILogger? logger = null)
        : this(
            preferences,
            logger,
            pipeName: null,
            settingsFilePath: null,
            windowFocuser: null,
            utcNowProvider: null,
            defaultBridgeExecutablePath: null)
    {
    }

    internal ClaudeCodeAdapter(
        IAppPreferencesService preferences,
        ILogger? logger,
        string? pipeName,
        string? settingsFilePath,
        ITerminalWindowFocuser? windowFocuser,
        Func<DateTimeOffset>? utcNowProvider,
        string? defaultBridgeExecutablePath = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
        _settingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")
            : settingsFilePath;
        _windowFocuser = windowFocuser ?? new Win32TerminalWindowFocuser();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _defaultBridgeExecutablePath = defaultBridgeExecutablePath;
    }

    public string AdapterId => Id;

    public string DisplayName => "Claude Code";

    public string SettingsFilePath => _settingsFilePath;

    public string PipeName => _pipeName;

    public AgentConnectionState ConnectionState
    {
        get
        {
            lock (_stateSync)
            {
                return _connectionState;
            }
        }
    }

    public AgentCapabilities Capabilities
    {
        get
        {
            var isOptedIn = _preferences.Current.ClaudeCodeOptInEnabled;
            var hookInstalled = IsHookInstalled();
            var hasTerminalPid = _windowFocuser.IsSupported
                && _sessions.Values.Any(s => s.ProcessId is > 0);

            var unsupported = new List<string>();
            if (!isOptedIn)
            {
                unsupported.Add("Claude Code integration is disabled until opted in under Settings.");
            }
            else if (!hookInstalled)
            {
                unsupported.Add("Claude Code hooks in ~/.claude/settings.json are not installed yet.");
            }

            if (!_windowFocuser.IsSupported || !hasTerminalPid)
            {
                unsupported.Add("Terminal window focus requires an active Claude Code process ID on Windows.");
            }

            return new AgentCapabilities(
                SupportsInteractiveApproval: isOptedIn,
                SupportsSessionElapsedTimer: true,
                SupportsTerminalFocus: hasTerminalPid,
                SupportsMultiSessionIsolation: true,
                IsOptInConfigured: isOptedIn,
                UnsupportedCapabilities: unsupported);
        }
    }

    public event EventHandler<AgentSessionSnapshot>? SessionUpdated;

    public event EventHandler<string>? SessionRemoved;

    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    internal static PipeSecurity CreatePipeSecurity() => PipeSecurityFactory.CreateCurrentUserOnlyPipeSecurity();

    public Task<AdapterHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_preferences.Current.ClaudeCodeOptInEnabled)
        {
            return Task.FromResult(new AdapterHealthResult(
                AdapterHealthStatus.Disconnected,
                "Disabled (opt-in required in Developer Integrations Settings)."));
        }

        var hookInstalled = IsHookInstalled();
        if (ConnectionState != AgentConnectionState.Connected)
        {
            return Task.FromResult(new AdapterHealthResult(
                AdapterHealthStatus.Disconnected,
                "Named Pipe listener is stopped."));
        }

        if (!hookInstalled)
        {
            return Task.FromResult(new AdapterHealthResult(
                AdapterHealthStatus.Healthy,
                "Pipe listening; install hooks in ~/.claude/settings.json to receive live Claude Code events."));
        }

        return Task.FromResult(new AdapterHealthResult(
            AdapterHealthStatus.Healthy,
            $"Listening on \\\\.\\pipe\\{_pipeName} with hooks installed."));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _shouldRunOnOptIn = true;
            if (!_preferencesSubscribed)
            {
                _preferences.PreferencesChanged += OnPreferencesChanged;
                _preferencesSubscribed = true;
            }

            if (!_preferences.Current.ClaudeCodeOptInEnabled)
            {
                await StopServerLockedAsync().ConfigureAwait(false);
                SetConnectionState(AgentConnectionState.Disconnected);
                return;
            }

            StartServerLocked();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _shouldRunOnOptIn = false;
            await StopServerLockedAsync().ConfigureAwait(false);
            SetConnectionState(AgentConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<PermissionActionResult> RespondToApprovalAsync(
        string sessionId,
        string requestId,
        bool allow,
        string? reason,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        if (ConnectionState != AgentConnectionState.Connected)
        {
            return new PermissionActionResult(false, null, "Claude Code adapter is not connected.");
        }

        if (!_pendingPrompts.TryGetValue(requestId, out var prompt)
            || !string.Equals(prompt.SessionId, sessionId, StringComparison.Ordinal))
        {
            return new PermissionActionResult(false, null, "Pending Claude Code permission request was not found for this session.");
        }

        try
        {
            // Serialize single-line NDJSON without newlines from pretty-printing.
            var compactLine = JsonSerializer.Serialize(new PipeDecisionMessage
            {
                RequestId = requestId,
                Decision = allow ? "allow" : "deny",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            });

            await prompt.WriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await prompt.Writer.WriteLineAsync(compactLine.AsMemory(), ct).ConfigureAwait(false);
                await prompt.Writer.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                prompt.WriteGate.Release();
            }

            using var ackTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ackTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var acked = await prompt.AckTcs.Task.WaitAsync(ackTimeoutCts.Token).ConfigureAwait(false);
            if (!acked)
            {
                ClearPendingPrompt(sessionId, requestId, AgentStatus.Working);
                return new PermissionActionResult(false, null, "Bridge pipe closed before emitting the permission decision.");
            }

            var nextStatus = AgentStatus.Working;
            if (_sessions.TryGetValue(sessionId, out var current))
            {
                var updated = current with
                {
                    Status = nextStatus,
                    PendingRequestId = null,
                    PendingToolName = null,
                    PendingActionSummary = null,
                    HasActionablePermissionRequest = false,
                    TaskSummary = allow
                        ? $"Approved {current.PendingToolName ?? "action"}"
                        : $"Denied {current.PendingToolName ?? "action"}",
                    LastUpdatedAtUtc = _utcNowProvider().ToUniversalTime()
                };
                _sessions[sessionId] = updated;
                SessionUpdated?.Invoke(this, updated);
            }

            return new PermissionActionResult(true, "claude.pipe.ack", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn("Failed to deliver Claude Code permission response over Named Pipe.", ex);
            ClearPendingPrompt(sessionId, requestId, AgentStatus.Working);
            return new PermissionActionResult(false, null, $"Pipe response failed: {ex.Message}");
        }
    }

    public Task<bool> FocusTerminalAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.ProcessId is not > 0)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_windowFocuser.TryFocusProcessWindow(session.ProcessId.Value));
    }

    public bool IsHookInstalled()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["hooks"] is not JsonObject hooksObj)
            {
                return false;
            }

            foreach (var eventName in SupportedHookEvents)
            {
                if (hooksObj[eventName] is JsonArray array && array.Any(IsShimaHookEntry))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<HookInstallResult> InstallHooksAsync(string? bridgeExecutablePath = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var resolvedExe = ResolveBridgeExecutablePath(bridgeExecutablePath);
        if (string.IsNullOrWhiteSpace(resolvedExe))
        {
            return new HookInstallResult(false, "Unable to locate Shima.AgentBridge.exe.");
        }

        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new HookInstallResult(false, "Invalid Claude settings file path.");
        }

        Directory.CreateDirectory(directory);

        string? backupPath = null;
        JsonObject root;

        if (File.Exists(_settingsFilePath))
        {
            backupPath = CreateTimestampedBackup(_settingsFilePath);
            var existingJson = await File.ReadAllTextAsync(_settingsFilePath, Utf8NoBom, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                root = new JsonObject();
            }
            else
            {
                try
                {
                    root = JsonNode.Parse(existingJson) as JsonObject
                        ?? throw new JsonException("Root settings.json element must be a JSON object.");
                }
                catch (Exception ex)
                {
                    return new HookInstallResult(
                        false,
                        $"Could not parse existing ~/.claude/settings.json ({ex.Message}); left untouched.",
                        backupPath);
                }
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooksObj)
        {
            hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
        }

        var commandString = $"\"{resolvedExe}\" {ShimaHookMarker}";

        foreach (var eventName in SupportedHookEvents)
        {
            var eventArray = hooksObj[eventName] as JsonArray ?? new JsonArray();
            RemoveShimaEntries(eventArray);

            var entryObj = new JsonObject();
            if (eventName is "PreToolUse" or "PermissionRequest" or "Notification")
            {
                entryObj["matcher"] = string.Empty;
            }

            entryObj["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = commandString
                });

            eventArray.Add(entryObj);
            hooksObj[eventName] = eventArray;
        }

        await WriteJsonAtomicAsync(_settingsFilePath, root, ct).ConfigureAwait(false);
        return new HookInstallResult(
            true,
            $"Installed Claude Code hooks in {_settingsFilePath}.",
            backupPath);
    }

    public async Task<HookInstallResult> UninstallHooksAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(_settingsFilePath))
        {
            return new HookInstallResult(true, "No ~/.claude/settings.json found; nothing to uninstall.");
        }

        var backupPath = CreateTimestampedBackup(_settingsFilePath);
        var existingJson = await File.ReadAllTextAsync(_settingsFilePath, Utf8NoBom, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return new HookInstallResult(true, "Claude settings file is empty.", backupPath);
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(existingJson) as JsonObject
                ?? throw new JsonException("Root settings.json element must be a JSON object.");
        }
        catch (Exception ex)
        {
            return new HookInstallResult(
                false,
                $"Could not parse ~/.claude/settings.json ({ex.Message}); left untouched.",
                backupPath);
        }

        if (root["hooks"] is JsonObject hooksObj)
        {
            var emptyEventKeys = new List<string>();
            foreach (var kvp in hooksObj)
            {
                if (kvp.Value is JsonArray array)
                {
                    RemoveShimaEntries(array);
                    if (array.Count == 0)
                    {
                        emptyEventKeys.Add(kvp.Key);
                    }
                }
            }

            foreach (var key in emptyEventKeys)
            {
                hooksObj.Remove(key);
            }

            if (hooksObj.Count == 0)
            {
                root.Remove("hooks");
            }
        }

        await WriteJsonAtomicAsync(_settingsFilePath, root, ct).ConfigureAwait(false);
        return new HookInstallResult(
            true,
            "Uninstalled Shima-managed hooks from ~/.claude/settings.json while preserving existing configuration.",
            backupPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_preferencesSubscribed)
        {
            _preferences.PreferencesChanged -= OnPreferencesChanged;
            _preferencesSubscribed = false;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    internal int PurgeInactiveSessions(DateTimeOffset utcNow)
    {
        var cutoff = utcNow.ToUniversalTime() - TimeSpan.FromMinutes(15);
        var removed = 0;

        foreach (var kvp in _sessions.ToArray())
        {
            var snap = kvp.Value;
            if ((snap.Status == AgentStatus.Finished
                    || snap.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable)
                && snap.LastUpdatedAtUtc <= cutoff
                && _sessions.TryRemove(kvp.Key, out _))
            {
                removed++;
                SessionRemoved?.Invoke(this, kvp.Key);
            }
        }

        return removed;
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot updated)
    {
        if (_disposed || !_shouldRunOnOptIn)
        {
            return;
        }

        _ = SyncOptInStateAsync(updated.ClaudeCodeOptInEnabled);
    }

    private async Task SyncOptInStateAsync(bool enabled)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_shouldRunOnOptIn)
            {
                return;
            }

            if (enabled)
            {
                StartServerLocked();
            }
            else
            {
                await StopServerLockedAsync().ConfigureAwait(false);
                SetConnectionState(AgentConnectionState.Disconnected);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartServerLocked()
    {
        if (_serverCts is not null)
        {
            return;
        }

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;
        _acceptLoopTask = Task.Run(() => RunPipeAcceptLoopAsync(token), CancellationToken.None);
        SetConnectionState(AgentConnectionState.Connected);
    }

    private async Task StopServerLockedAsync()
    {
        var cts = _serverCts;
        var loop = _acceptLoopTask;
        _serverCts = null;
        _acceptLoopTask = null;

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation races.
            }
        }

        foreach (var pending in _pendingPrompts.ToArray())
        {
            if (_pendingPrompts.TryRemove(pending.Key, out var prompt))
            {
                prompt.AckTcs.TrySetResult(false);
                prompt.Dispose();
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Accept loop shutdown timeout or cancellation.
            }
        }

        cts?.Dispose();
    }

    private async Task RunPipeAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    CreatePipeSecurity());

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connectedPipe = server;
                server = null;

                _ = Task.Run(() => HandlePipeClientAsync(connectedPipe, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _logger?.Warn("Claude Code Named Pipe accept error.", ex);
                SetConnectionState(AgentConnectionState.Reconnecting);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!ct.IsCancellationRequested && _preferences.Current.ClaudeCodeOptInEnabled)
                {
                    SetConnectionState(AgentConnectionState.Connected);
                }
            }
        }
    }

    private async Task HandlePipeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = false })
        {
            string? firstLine;
            try
            {
                firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return;
            }

            BridgeIncomingRequest? payload;
            try
            {
                payload = JsonSerializer.Deserialize<BridgeIncomingRequest>(firstLine, JsonOptions);
            }
            catch
            {
                return;
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.HookEventName))
            {
                return;
            }

            var nativeSessionId = string.IsNullOrWhiteSpace(payload.SessionId) ? "default" : payload.SessionId.Trim();
            var sessionId = $"{Id}:{nativeSessionId}";
            var now = payload.TimestampUtc == default
                ? _utcNowProvider().ToUniversalTime()
                : payload.TimestampUtc.ToUniversalTime();

            var workspacePath = payload.Cwd ?? string.Empty;
            var workspaceName = ResolveWorkspaceName(workspacePath);
            var existing = _sessions.TryGetValue(sessionId, out var prev) ? prev : null;
            var resolvedPid = payload.ProcessId > 0
                ? (_windowFocuser.ResolveHostProcessId(payload.ProcessId) ?? existing?.ProcessId)
                : existing?.ProcessId;

            if (!payload.ExpectsDecision)
            {
                var mappedStatus = MapNonDecisionStatus(payload.HookEventName, payload.NotificationType, out var isFatal);
                var taskSummary = BuildTaskSummary(payload, existing?.TaskSummary);

                var snapshot = new AgentSessionSnapshot(
                    SessionId: sessionId,
                    AdapterId: Id,
                    WorkspaceName: string.IsNullOrWhiteSpace(workspaceName) ? (existing?.WorkspaceName ?? "Workspace") : workspaceName,
                    WorkspacePath: string.IsNullOrWhiteSpace(workspacePath) ? (existing?.WorkspacePath ?? string.Empty) : workspacePath,
                    ProcessId: resolvedPid,
                    Status: mappedStatus,
                    IsFatalError: isFatal,
                    ConnectionState: ConnectionState,
                    TaskSummary: taskSummary,
                    PendingToolName: null,
                    PendingActionSummary: null,
                    PendingRequestId: null,
                    HasActionablePermissionRequest: false,
                    StartedAtUtc: existing?.StartedAtUtc ?? now,
                    LastUpdatedAtUtc: now);

                _sessions[sessionId] = snapshot;
                SessionUpdated?.Invoke(this, snapshot);

                try
                {
                    var ackLine = JsonSerializer.Serialize(new PipeDecisionMessage
                    {
                        RequestId = payload.RequestId,
                        Decision = "ack",
                        Reason = null
                    });
                    await writer.WriteLineAsync(ackLine.AsMemory(), ct).ConfigureAwait(false);
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Telemetry hook ack best-effort.
                }

                return;
            }

            // Decision prompt: hold pipe open waiting for RespondToApprovalAsync OR client disconnect/timeout.
            var requestId = string.IsNullOrWhiteSpace(payload.RequestId) ? Guid.NewGuid().ToString("N") : payload.RequestId;
            var prompt = new PendingPipePrompt(sessionId, requestId, writer);
            _pendingPrompts[requestId] = prompt;

            var awaitingSnapshot = new AgentSessionSnapshot(
                SessionId: sessionId,
                AdapterId: Id,
                WorkspaceName: string.IsNullOrWhiteSpace(workspaceName) ? (existing?.WorkspaceName ?? "Workspace") : workspaceName,
                WorkspacePath: string.IsNullOrWhiteSpace(workspacePath) ? (existing?.WorkspacePath ?? string.Empty) : workspacePath,
                ProcessId: resolvedPid,
                Status: AgentStatus.AwaitingApproval,
                IsFatalError: false,
                ConnectionState: ConnectionState,
                TaskSummary: BuildTaskSummary(payload, existing?.TaskSummary),
                PendingToolName: payload.ToolName,
                PendingActionSummary: BuildPendingActionSummary(payload),
                PendingRequestId: requestId,
                HasActionablePermissionRequest: ConnectionState == AgentConnectionState.Connected,
                StartedAtUtc: existing?.StartedAtUtc ?? now,
                LastUpdatedAtUtc: now);

            _sessions[sessionId] = awaitingSnapshot;
            SessionUpdated?.Invoke(this, awaitingSnapshot);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        // Bridge disconnected or timed out failing open to terminal prompt.
                        prompt.AckTcs.TrySetResult(false);
                        ClearPendingPrompt(sessionId, requestId, AgentStatus.Working);
                        break;
                    }

                    try
                    {
                        var ack = JsonSerializer.Deserialize<PipeAckMessage>(line, JsonOptions);
                        if (ack is not null
                            && string.Equals(ack.Type, "decision_emitted", StringComparison.Ordinal)
                            && string.Equals(ack.RequestId, requestId, StringComparison.Ordinal))
                        {
                            prompt.AckTcs.TrySetResult(true);
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore malformed secondary frames.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                prompt.AckTcs.TrySetResult(false);
            }
            catch
            {
                prompt.AckTcs.TrySetResult(false);
                ClearPendingPrompt(sessionId, requestId, AgentStatus.Working);
            }
            finally
            {
                _pendingPrompts.TryRemove(requestId, out _);
                prompt.Dispose();
            }
        }
    }

    private void ClearPendingPrompt(string sessionId, string requestId, AgentStatus fallbackStatus)
    {
        if (_pendingPrompts.TryRemove(requestId, out var prompt))
        {
            prompt.AckTcs.TrySetResult(false);
            prompt.Dispose();
        }

        if (_sessions.TryGetValue(sessionId, out var current)
            && string.Equals(current.PendingRequestId, requestId, StringComparison.Ordinal))
        {
            var updated = current with
            {
                Status = fallbackStatus,
                PendingRequestId = null,
                PendingToolName = null,
                PendingActionSummary = null,
                HasActionablePermissionRequest = false,
                LastUpdatedAtUtc = _utcNowProvider().ToUniversalTime()
            };
            _sessions[sessionId] = updated;
            SessionUpdated?.Invoke(this, updated);
        }
    }

    private void SetConnectionState(AgentConnectionState nextState)
    {
        AgentConnectionState previous;
        lock (_stateSync)
        {
            previous = _connectionState;
            if (previous == nextState)
            {
                return;
            }

            _connectionState = nextState;
        }

        foreach (var kvp in _sessions.ToArray())
        {
            var current = kvp.Value;
            var actionable = nextState == AgentConnectionState.Connected
                && !string.IsNullOrWhiteSpace(current.PendingRequestId)
                && current.Status == AgentStatus.AwaitingApproval;

            var updated = current with
            {
                ConnectionState = nextState,
                HasActionablePermissionRequest = actionable
            };
            _sessions[kvp.Key] = updated;
            SessionUpdated?.Invoke(this, updated);
        }

        ConnectionStateChanged?.Invoke(this, nextState);
    }

    private string? ResolveBridgeExecutablePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        if (!string.IsNullOrWhiteSpace(_defaultBridgeExecutablePath))
        {
            return Path.GetFullPath(_defaultBridgeExecutablePath);
        }

        var baseDir = AppContext.BaseDirectory;
        var exeCandidate = Path.Combine(baseDir, "Shima.AgentBridge.exe");
        if (File.Exists(exeCandidate))
        {
            return exeCandidate;
        }

        var dllCandidate = Path.Combine(baseDir, "Shima.AgentBridge.dll");
        return File.Exists(dllCandidate) ? dllCandidate : exeCandidate;
    }

    private static string CreateTimestampedBackup(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var backupPath = Path.Combine(
            directory,
            $"{nameWithoutExt}.backup.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}{ext}");
        File.Copy(filePath, backupPath, overwrite: true);
        return backupPath;
    }

    private static async Task WriteJsonAtomicAsync(string targetPath, JsonObject root, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
        var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tempPath, serialized + Environment.NewLine, Utf8NoBom, ct).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static void RemoveShimaEntries(JsonArray entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (IsShimaHookEntry(entries[i]))
            {
                entries.RemoveAt(i);
            }
        }
    }

    private static bool IsShimaHookEntry(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return false;
        }

        if (IsShimaCommand(obj["command"]?.GetValue<string>()))
        {
            return true;
        }

        if (obj["hooks"] is JsonArray nested)
        {
            foreach (var child in nested)
            {
                if (child is JsonObject childObj && IsShimaCommand(childObj["command"]?.GetValue<string>()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsShimaCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command)
        && (command.Contains("Shima.AgentBridge", StringComparison.OrdinalIgnoreCase)
            || command.Contains(ShimaHookMarker, StringComparison.Ordinal));

    private static AgentStatus MapNonDecisionStatus(string hookEventName, string? notificationType, out bool isFatal)
    {
        isFatal = false;
        if (hookEventName is "Stop" or "SubagentStop")
        {
            return AgentStatus.Finished;
        }

        if (string.Equals(hookEventName, "Notification", StringComparison.Ordinal))
        {
            isFatal = string.Equals(notificationType, "error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(notificationType, "fatal", StringComparison.OrdinalIgnoreCase);
            return AgentStatus.NeedsAttention;
        }

        return AgentStatus.Working;
    }

    private static string ResolveWorkspaceName(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return string.Empty;
        }

        var trimmed = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folder = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(folder) ? trimmed : folder;
    }

    private static string BuildTaskSummary(BridgeIncomingRequest payload, string? previousSummary)
    {
        if (payload.HookEventName is "Stop" or "SubagentStop")
        {
            return string.IsNullOrWhiteSpace(previousSummary) ? "Session finished" : previousSummary;
        }

        if (!string.IsNullOrWhiteSpace(payload.Message))
        {
            return payload.Message.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.ToolName))
        {
            return !string.IsNullOrWhiteSpace(payload.ToolInputSummary)
                ? $"{payload.ToolName}: {payload.ToolInputSummary}"
                : $"Running {payload.ToolName}";
        }

        return previousSummary ?? "Claude Code session";
    }

    private static string? BuildPendingActionSummary(BridgeIncomingRequest payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.ToolInputSummary))
        {
            return payload.ToolInputSummary;
        }

        return !string.IsNullOrWhiteSpace(payload.Message)
            ? payload.Message.Trim()
            : payload.ToolName;
    }

    private sealed class PendingPipePrompt(string sessionId, string requestId, StreamWriter writer) : IDisposable
    {
        public string SessionId { get; } = sessionId;
        public string RequestId { get; } = requestId;
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public TaskCompletionSource<bool> AckTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => WriteGate.Dispose();
    }

    private sealed class BridgeIncomingRequest
    {
        [JsonPropertyName("requestId")]
        public string RequestId { get; init; } = string.Empty;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; init; } = string.Empty;

        [JsonPropertyName("cwd")]
        public string Cwd { get; init; } = string.Empty;

        [JsonPropertyName("hookEventName")]
        public string HookEventName { get; init; } = string.Empty;

        [JsonPropertyName("toolName")]
        public string? ToolName { get; init; }

        [JsonPropertyName("toolInputSummary")]
        public string? ToolInputSummary { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("notificationType")]
        public string? NotificationType { get; init; }

        [JsonPropertyName("processId")]
        public int ProcessId { get; init; }

        [JsonPropertyName("expectsDecision")]
        public bool ExpectsDecision { get; init; }

        [JsonPropertyName("timestampUtc")]
        public DateTimeOffset TimestampUtc { get; init; }
    }

    private sealed class PipeDecisionMessage
    {
        [JsonPropertyName("requestId")]
        public required string RequestId { get; init; }

        [JsonPropertyName("decision")]
        public required string Decision { get; init; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }
    }

    private sealed class PipeAckMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; init; }

        [JsonPropertyName("decision")]
        public string? Decision { get; init; }
    }
}
