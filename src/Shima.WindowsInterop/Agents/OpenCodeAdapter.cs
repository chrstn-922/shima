using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.WindowsInterop.Agents;

/// <summary>
/// Independent opt-in OpenCode integration adapter supporting documented local server APIs
/// (loopback opencode serve GET /global/health, /doc, SSE /global/event or /event, REST snapshots
/// GET /session + GET /session/status, and POST single-action once/reject permission replies)
/// as well as optional ~/.config/opencode/plugins/shima-bridge.ts plugin bridge.
/// Completely decoupled from ClaudeCodeAdapter. Zero port scanning or terminal scraping.
/// </summary>
public sealed class OpenCodeAdapter : ICodingAgentAdapter, IOpenCodePluginManager
{
    public const string Id = "opencode";
    public const string DefaultEndpointUrl = "http://localhost:4096";
    public const string DefaultPluginPipeName = "shima-opencode-bridge";
    public static readonly TimeSpan InactiveSessionExpiryDuration = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan[] ReconnectBackoffSchedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const int DedupRingCapacity = 256;

    private readonly IAppPreferencesService _preferences;
    private readonly ILogger? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayProvider;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly ITerminalWindowFocuser _windowFocuser;
    private readonly string _pluginFilePath;
    private readonly string _pluginPipeName;
    private readonly IOneShotTimer _expiryTimer;
    private readonly bool _ownsExpiryTimer;
    private readonly int _maxReconnectFailuresBeforeDisconnect;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateSync = new();

    private readonly ConcurrentDictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _permissionToSession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPluginPrompt> _pendingPluginPrompts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _runCts;
    private Task? _sseLoopTask;
    private Task? _pluginLoopTask;
    private bool _shouldRunOnOptIn;
    private bool _preferencesSubscribed;
    private AgentConnectionState _connectionState = AgentConnectionState.Disconnected;
    private AdapterHealthResult _lastHealthResult = new(AdapterHealthStatus.Disconnected, "Not started.");
    private PermissionReplyRoute _replyRoute = PermissionReplyRoute.None;
    private bool? _pluginSupportsInteractiveHook;
    private bool _authBlocked;
    private long _duplicateEventsIgnored;
    private long _ambiguousEventsIgnored;
    private int _reconnectAttempts;
    private bool _disposed;

    public OpenCodeAdapter(IAppPreferencesService preferences, ILogger? logger = null)
        : this(
            preferences,
            logger,
            httpHandler: null,
            delayProvider: null,
            utcNowProvider: null,
            windowFocuser: null,
            pluginFilePath: null,
            pluginPipeName: null,
            expiryTimer: null,
            maxReconnectFailuresBeforeDisconnect: 3)
    {
    }

    internal OpenCodeAdapter(
        IAppPreferencesService preferences,
        ILogger? logger,
        HttpMessageHandler? httpHandler,
        Func<TimeSpan, CancellationToken, Task>? delayProvider,
        Func<DateTimeOffset>? utcNowProvider,
        ITerminalWindowFocuser? windowFocuser = null,
        string? pluginFilePath = null,
        string? pluginPipeName = null,
        IOneShotTimer? expiryTimer = null,
        int maxReconnectFailuresBeforeDisconnect = 3)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _logger = logger;
        if (httpHandler is not null)
        {
            _httpClient = new HttpClient(httpHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
            _ownsHttpClient = true;
        }

        _delayProvider = delayProvider ?? DefaultDelayWithJitterAsync;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _windowFocuser = windowFocuser ?? new Win32TerminalWindowFocuser();
        _pluginFilePath = string.IsNullOrWhiteSpace(pluginFilePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config",
                "opencode",
                "plugins",
                "shima-bridge.ts")
            : pluginFilePath;
        _pluginPipeName = string.IsNullOrWhiteSpace(pluginPipeName)
            ? DefaultPluginPipeName
            : pluginPipeName.Trim();
        _ownsExpiryTimer = expiryTimer is null;
        _expiryTimer = expiryTimer ?? new OneShotTimer();
        _maxReconnectFailuresBeforeDisconnect = Math.Max(1, maxReconnectFailuresBeforeDisconnect);
    }

    public string AdapterId => Id;

    public string DisplayName => "OpenCode";

    public string PluginFilePath => _pluginFilePath;

    internal long DuplicateEventsIgnoredCount => Interlocked.Read(ref _duplicateEventsIgnored);

    internal long AmbiguousEventsIgnoredCount => Interlocked.Read(ref _ambiguousEventsIgnored);

    internal int ReconnectAttemptCount => Volatile.Read(ref _reconnectAttempts);

    internal bool IsExpiryTimerPending => _expiryTimer.IsPending;

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
            var isOptedIn = _preferences.Current.OpenCodeOptInEnabled;
            var hasPid = _windowFocuser.IsSupported
                && _sessions.Values.Any(s => s.Snapshot.ProcessId is > 0);
            var serverReplySupported = _replyRoute.Kind != PermissionReplyKind.None;
            var pluginReplySupported = _pluginSupportsInteractiveHook == true;
            var supportsApproval = isOptedIn && (serverReplySupported || pluginReplySupported);

            var unsupported = new List<string>();
            if (!isOptedIn)
            {
                unsupported.Add("OpenCode integration is disabled until opted in under Settings.");
            }
            else if (!serverReplySupported && !pluginReplySupported)
            {
                unsupported.Add(_pluginSupportsInteractiveHook == false
                    ? "OpenCode plugin SDK lacks a compatible interactive permission hook; running in read-only monitoring mode."
                    : "Interactive approvals unavailable: /doc did not advertise a permission reply endpoint and no interactive plugin hook is active.");
            }

            if (!hasPid)
            {
                unsupported.Add("Focus Terminal unavailable for headless or HTTP-only opencode serve sessions without an OS ProcessId.");
            }

            unsupported.Add("Standalone TUI sessions without opencode serve or plugin bridge cannot be monitored (no terminal scraping or port scanning).");
            unsupported.Add("Session-wide 'always' rules and free-form text replies remain inside OpenCode's native terminal (Shima sends strictly 'once' or 'reject').");
            unsupported.Add("OpenCode global SSE has no Last-Event-ID cursor replay; missed events are recovered via REST snapshots on reconnect.");

            return new AgentCapabilities(
                SupportsInteractiveApproval: supportsApproval,
                SupportsSessionElapsedTimer: true,
                SupportsTerminalFocus: hasPid,
                SupportsMultiSessionIsolation: true,
                IsOptInConfigured: isOptedIn,
                UnsupportedCapabilities: unsupported);
        }
    }

    public event EventHandler<AgentSessionSnapshot>? SessionUpdated;

    public event EventHandler<string>? SessionRemoved;

    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    public static bool TryValidateLoopbackEndpoint(string? endpointUrl, out Uri? validatedUri, out string? error)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            validatedUri = new Uri(DefaultEndpointUrl);
            error = null;
            return true;
        }

        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri))
        {
            validatedUri = null;
            error = "OpenCode endpoint URL is malformed.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            validatedUri = null;
            error = "OpenCode endpoint URL must use local http:// (non-loopback HTTPS/remote schemes are rejected).";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            validatedUri = null;
            error = "Configure the OpenCode server password using Developer Integrations Settings instead of URL credentials.";
            return false;
        }

        var isLoopbackHost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal);

        if (!isLoopbackHost)
        {
            validatedUri = null;
            error = "Only local loopback hosts (localhost or 127.0.0.1) are permitted for OpenCode integration.";
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        validatedUri = builder.Uri;
        error = null;
        return true;
    }

    public static string BuildPermissionReplyPayload(
        bool allow,
        string? reason,
        PermissionReplyKind kind = PermissionReplyKind.SessionPermissions)
    {
        // Strictly least-privilege 'once' (Allow) or 'reject' (Deny). Never 'always'.
        var verdict = allow ? "once" : "reject";
        var obj = new JsonObject();

        if (kind == PermissionReplyKind.GlobalPermissionReply)
        {
            obj["reply"] = verdict;
        }
        else
        {
            obj["response"] = verdict;
        }

        if (!allow && !string.IsNullOrWhiteSpace(reason))
        {
            obj["message"] = reason.Trim();
        }

        return obj.ToJsonString();
    }

    public async Task<AdapterHealthResult> CheckHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_preferences.Current.OpenCodeOptInEnabled)
        {
            return new AdapterHealthResult(
                AdapterHealthStatus.Disconnected,
                "Disabled (opt-in required in Developer Integrations Settings).");
        }

        var configuredUrl = _preferences.Current.OpenCodeEndpointUrl;
        if (!TryValidateLoopbackEndpoint(configuredUrl, out var baseUri, out var urlError) || baseUri is null)
        {
            return new AdapterHealthResult(AdapterHealthStatus.Unavailable, urlError);
        }

        try
        {
            using var healthResp = await SendAuthorizedAsync(HttpMethod.Get, new Uri(baseUri, "global/health"), content: null, ct)
                .ConfigureAwait(false);

            if (healthResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return _lastHealthResult;
            }

            if (!healthResp.IsSuccessStatusCode)
            {
                return new AdapterHealthResult(
                    AdapterHealthStatus.Disconnected,
                    $"OpenCode server health check returned HTTP {(int)healthResp.StatusCode}.");
            }

            await RefreshDocCapabilitiesAsync(baseUri, ct).ConfigureAwait(false);
            if (_authBlocked)
            {
                return _lastHealthResult;
            }

            var message = _replyRoute.Kind != PermissionReplyKind.None
                ? $"Connected to {baseUri.Authority} (interactive once/reject approvals supported)."
                : $"Connected to {baseUri.Authority} (read-only event stream; no permission reply route advertised).";

            var result = new AdapterHealthResult(AdapterHealthStatus.Healthy, message);
            lock (_stateSync)
            {
                _lastHealthResult = result;
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var isPluginActive = IsPluginInstalled() && ConnectionState == AgentConnectionState.Connected;
            if (isPluginActive)
            {
                return new AdapterHealthResult(
                    AdapterHealthStatus.Healthy,
                    $"Plugin bridge active (\\\\.\\pipe\\{_pluginPipeName}); server offline.");
            }

            return new AdapterHealthResult(
                AdapterHealthStatus.Unavailable,
                $"OpenCode endpoint {baseUri.Authority} unavailable ({ex.Message}). Start 'opencode serve' or install the plugin.");
        }
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

            await ApplyConfigurationLockedAsync(_preferences.Current, ct).ConfigureAwait(false);
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
            await StopLoopsLockedAsync().ConfigureAwait(false);
            _expiryTimer.Cancel();
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
            return new PermissionActionResult(false, null, "OpenCode adapter is disconnected; action cannot be confirmed.");
        }

        if (!_sessions.TryGetValue(sessionId, out var tracked))
        {
            return new PermissionActionResult(false, null, "OpenCode session was not found.");
        }

        if (!string.Equals(tracked.Snapshot.PendingRequestId, requestId, StringComparison.Ordinal))
        {
            return new PermissionActionResult(false, null, "Permission request ID does not match the active request for this session.");
        }

        // First check if this pending request originated via interactive plugin Named Pipe hook.
        if (_pendingPluginPrompts.TryGetValue(requestId, out var pluginPrompt)
            && string.Equals(pluginPrompt.SessionId, sessionId, StringComparison.Ordinal))
        {
            return await RespondViaPluginPipeAsync(pluginPrompt, allow, reason, ct).ConfigureAwait(false);
        }

        if (_replyRoute.Kind == PermissionReplyKind.None)
        {
            return new PermissionActionResult(
                false,
                null,
                "OpenCode endpoint does not advertise an interactive permission reply route.");
        }

        if (!TryValidateLoopbackEndpoint(_preferences.Current.OpenCodeEndpointUrl, out var baseUri, out var urlError) || baseUri is null)
        {
            return new PermissionActionResult(false, null, urlError ?? "Invalid OpenCode endpoint.");
        }

        var nativeSessionId = StripNamespace(sessionId);
        var relativePath = _replyRoute.BuildRelativePath(nativeSessionId, requestId);
        var body = BuildPermissionReplyPayload(allow, reason, _replyRoute.Kind);

        try
        {
            using var content = new StringContent(body, Utf8NoBom, "application/json");
            using var response = await SendAuthorizedAsync(
                HttpMethod.Post,
                new Uri(baseUri, relativePath.TrimStart('/')),
                content,
                ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return new PermissionActionResult(
                    false,
                    null,
                    "Authentication required (HTTP 401): OpenCode rejected the request credentials.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PermissionActionResult(
                    false,
                    null,
                    $"OpenCode rejected permission verdict with HTTP {(int)response.StatusCode}.");
            }

            // Commit resolved state in Shima ONLY after OpenCode confirmed 2xx.
            CommitConfirmedPermissionResolution(sessionId, requestId, allow);
            return new PermissionActionResult(true, "opencode.http.replied", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn("OpenCode permission reply request failed before confirmation.", ex);
            return new PermissionActionResult(
                false,
                null,
                $"OpenCode confirmation failed: {ex.Message}");
        }
    }

    public Task<bool> FocusTerminalAsync(string sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId)
            || !_sessions.TryGetValue(sessionId, out var tracked)
            || tracked.Snapshot.ProcessId is not > 0)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_windowFocuser.TryFocusProcessWindow(tracked.Snapshot.ProcessId.Value));
    }

    public bool IsPluginInstalled()
    {
        if (!File.Exists(_pluginFilePath))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(_pluginFilePath, Utf8NoBom);
            return text.Contains(_pluginPipeName, StringComparison.Ordinal)
                || text.Contains("shima-opencode-bridge", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task<HookInstallResult> InstallPluginAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(_pluginFilePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return new HookInstallResult(false, "Invalid OpenCode plugin file path.");
        }

        Directory.CreateDirectory(dir);
        string? backupPath = null;
        if (File.Exists(_pluginFilePath))
        {
            backupPath = CreateTimestampedBackup(_pluginFilePath);
        }

        var script = GeneratePluginScript(_pluginPipeName);
        var tempPath = $"{_pluginFilePath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tempPath, script, Utf8NoBom, ct).ConfigureAwait(false);
        File.Move(tempPath, _pluginFilePath, overwrite: true);

        return new HookInstallResult(
            true,
            $"Installed Shima OpenCode plugin at {_pluginFilePath}.",
            backupPath);
    }

    public Task<HookInstallResult> UninstallPluginAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(_pluginFilePath))
        {
            return Task.FromResult(new HookInstallResult(true, "OpenCode plugin not present; nothing to uninstall."));
        }

        var backupPath = CreateTimestampedBackup(_pluginFilePath);
        File.Delete(_pluginFilePath);

        return Task.FromResult(new HookInstallResult(
            true,
            "Uninstalled Shima OpenCode plugin with timestamped backup.",
            backupPath));
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
        if (_ownsExpiryTimer)
        {
            _expiryTimer.Dispose();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _lifecycleGate.Dispose();
    }

    internal async Task<bool> ConnectOnceAndReconcileForTestingAsync(CancellationToken ct)
    {
        if (!TryValidateLoopbackEndpoint(_preferences.Current.OpenCodeEndpointUrl, out var baseUri, out _) || baseUri is null)
        {
            SetConnectionState(AgentConnectionState.Unavailable);
            return false;
        }

        var health = await CheckHealthAsync(ct).ConfigureAwait(false);
        if (health.Status == AdapterHealthStatus.AuthenticationRequired)
        {
            return false;
        }

        if (health.Status != AdapterHealthStatus.Healthy)
        {
            SetConnectionState(AgentConnectionState.Unavailable);
            return false;
        }

        SetConnectionState(AgentConnectionState.Connected);
        await ReconcileSnapshotsAsync(baseUri, ct).ConfigureAwait(false);
        return true;
    }

    internal bool IngestEventJson(string rawJson) => ProcessSerializedEventPayload(rawJson);

    internal int PurgeInactiveSessions(DateTimeOffset utcNow)
    {
        var cutoff = utcNow.ToUniversalTime() - InactiveSessionExpiryDuration;
        var removedCount = 0;

        foreach (var kvp in _sessions.ToArray())
        {
            var snap = kvp.Value.Snapshot;
            var isInactiveEligible = snap.Status == AgentStatus.Finished
                || snap.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable;

            if (!isInactiveEligible || snap.LastUpdatedAtUtc > cutoff)
            {
                continue;
            }

            if (_sessions.TryRemove(kvp.Key, out _))
            {
                removedCount++;
                SessionRemoved?.Invoke(this, kvp.Key);
            }
        }

        ArmOrDisarmExpiryTimer();
        return removedCount;
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot updated)
    {
        if (_disposed || !_shouldRunOnOptIn)
        {
            return;
        }

        _ = ReconfigureAsync(updated);
    }

    private async Task ReconfigureAsync(AppPreferencesSnapshot updated)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_shouldRunOnOptIn)
            {
                return;
            }

            await StopLoopsLockedAsync().ConfigureAwait(false);
            _authBlocked = false;
            await ApplyConfigurationLockedAsync(updated, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ApplyConfigurationLockedAsync(AppPreferencesSnapshot prefs, CancellationToken ct)
    {
        if (!prefs.OpenCodeOptInEnabled)
        {
            SetConnectionState(AgentConnectionState.Disconnected);
            return;
        }

        var hasValidEndpoint = TryValidateLoopbackEndpoint(prefs.OpenCodeEndpointUrl, out var baseUri, out var urlError);
        if (!hasValidEndpoint && !string.IsNullOrWhiteSpace(prefs.OpenCodeEndpointUrl))
        {
            lock (_stateSync)
            {
                _lastHealthResult = new AdapterHealthResult(AdapterHealthStatus.Unavailable, urlError);
            }

            SetConnectionState(AgentConnectionState.Unavailable);
            return;
        }

        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        if (baseUri is not null)
        {
            _sseLoopTask = Task.Run(() => RunServerLoopAsync(baseUri, token), CancellationToken.None);
        }

        if (IsPluginInstalled())
        {
            _pluginLoopTask = Task.Run(() => RunPluginPipeAcceptLoopAsync(token), CancellationToken.None);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task StopLoopsLockedAsync()
    {
        var cts = _runCts;
        var sse = _sseLoopTask;
        var plugin = _pluginLoopTask;
        _runCts = null;
        _sseLoopTask = null;
        _pluginLoopTask = null;

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation race.
            }
        }

        foreach (var pending in _pendingPluginPrompts.ToArray())
        {
            if (_pendingPluginPrompts.TryRemove(pending.Key, out var prompt))
            {
                prompt.AckTcs.TrySetResult(false);
                prompt.Dispose();
            }
        }

        var tasks = new[] { sse, plugin }.Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown timeout or cancellation.
            }
        }

        cts?.Dispose();
    }

    private async Task RunServerLoopAsync(Uri baseUri, CancellationToken ct)
    {
        var failureIndex = 0;

        while (!ct.IsCancellationRequested && !_authBlocked)
        {
            try
            {
                using var healthResp = await SendAuthorizedAsync(
                    HttpMethod.Get,
                    new Uri(baseUri, "global/health"),
                    content: null,
                    ct).ConfigureAwait(false);

                if (healthResp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    MarkAuthenticationRequired();
                    return;
                }

                if (!healthResp.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Health returned HTTP {(int)healthResp.StatusCode}.");
                }

                await RefreshDocCapabilitiesAsync(baseUri, ct).ConfigureAwait(false);
                if (_authBlocked)
                {
                    return;
                }

                // Open SSE stream BEFORE snapshot reconciliation so no edge event is lost between connect & hydration.
                var sseRequestUri = new Uri(baseUri, "global/event");
                using var sseReq = CreateAuthorizedRequest(HttpMethod.Get, sseRequestUri, content: null);
                sseReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                var sseResponse = await _httpClient.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (sseResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    sseResponse.Dispose();
                    using var fallbackReq = CreateAuthorizedRequest(HttpMethod.Get, new Uri(baseUri, "event"), content: null);
                    fallbackReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    sseResponse = await _httpClient.SendAsync(fallbackReq, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }

                using (sseResponse)
                {
                    if (sseResponse.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        MarkAuthenticationRequired();
                        return;
                    }

                    if (!sseResponse.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"SSE stream returned HTTP {(int)sseResponse.StatusCode}.");
                    }

                    failureIndex = 0;
                    SetConnectionState(AgentConnectionState.Connected);
                    lock (_stateSync)
                    {
                        _lastHealthResult = new AdapterHealthResult(
                            AdapterHealthStatus.Healthy,
                            $"Connected to {baseUri.Authority}.");
                    }

                    // Initial hydration & every-reconnect missed-event snapshot recovery.
                    await ReconcileSnapshotsAsync(baseUri, ct).ConfigureAwait(false);
                    if (_authBlocked)
                    {
                        return;
                    }

                    await ReadSseStreamAsync(sseResponse, ct).ConfigureAwait(false);

                    // Clean EOF mid-stream still requires reconnect recovery.
                    if (!ct.IsCancellationRequested && !_authBlocked)
                    {
                        throw new IOException("OpenCode SSE stream ended unexpectedly.");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_authBlocked || ct.IsCancellationRequested)
                {
                    break;
                }

                var attempt = Interlocked.Increment(ref _reconnectAttempts);
                var delay = ReconnectBackoffSchedule[Math.Min(failureIndex, ReconnectBackoffSchedule.Length - 1)];
                failureIndex++;

                var nextState = failureIndex >= _maxReconnectFailuresBeforeDisconnect
                    ? AgentConnectionState.Disconnected
                    : AgentConnectionState.Reconnecting;

                SetConnectionState(nextState);
                lock (_stateSync)
                {
                    _lastHealthResult = new AdapterHealthResult(
                        nextState == AgentConnectionState.Reconnecting
                            ? AdapterHealthStatus.Reconnecting
                            : AdapterHealthStatus.Disconnected,
                        $"OpenCode stream interrupted ({ex.Message}); retrying ({attempt}).");
                }

                try
                {
                    await _delayProvider(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ReadSseStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false);

        var dataBuilder = new StringBuilder();
        string? pendingEventName = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (dataBuilder.Length > 0)
                {
                    ProcessSerializedEventPayload(dataBuilder.ToString(), pendingEventName);
                }

                break;
            }

            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    ProcessSerializedEventPayload(dataBuilder.ToString(), pendingEventName);
                    dataBuilder.Clear();
                }

                pendingEventName = null;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                pendingEventName = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (dataBuilder.Length > 0)
                {
                    dataBuilder.Append('\n');
                }

                dataBuilder.Append(value);
            }
        }
    }

    private async Task RefreshDocCapabilitiesAsync(Uri baseUri, CancellationToken ct)
    {
        try
        {
            using var docResp = await SendAuthorizedAsync(HttpMethod.Get, new Uri(baseUri, "doc"), content: null, ct)
                .ConfigureAwait(false);

            if (docResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return;
            }

            if (!docResp.IsSuccessStatusCode)
            {
                _replyRoute = PermissionReplyRoute.None;
                return;
            }

            var rawDoc = await docResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _replyRoute = ParseReplyRouteFromOpenApi(rawDoc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _replyRoute = PermissionReplyRoute.None;
        }
    }

    internal static PermissionReplyRoute ParseReplyRouteFromOpenApi(string? openApiJson)
    {
        if (string.IsNullOrWhiteSpace(openApiJson))
        {
            return PermissionReplyRoute.None;
        }

        try
        {
            using var doc = JsonDocument.Parse(openApiJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("paths", out var paths)
                || paths.ValueKind != JsonValueKind.Object)
            {
                return PermissionReplyRoute.None;
            }

            foreach (var pathProp in paths.EnumerateObject())
            {
                var path = pathProp.Name;
                if (!IsPostAllowedIfSpecified(pathProp.Value))
                {
                    continue;
                }

                if (path.Contains("/permissions/", StringComparison.OrdinalIgnoreCase)
                    && path.StartsWith("/session/", StringComparison.OrdinalIgnoreCase))
                {
                    return new PermissionReplyRoute(PermissionReplyKind.SessionPermissions, path);
                }

                if (path.StartsWith("/permission/", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("/reply", StringComparison.OrdinalIgnoreCase))
                {
                    return new PermissionReplyRoute(PermissionReplyKind.GlobalPermissionReply, path);
                }
            }
        }
        catch
        {
            return PermissionReplyRoute.None;
        }

        return PermissionReplyRoute.None;
    }

    private static bool IsPostAllowedIfSpecified(JsonElement pathItem)
    {
        if (pathItem.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasHttpMethodKeys = false;
        foreach (var prop in pathItem.EnumerateObject())
        {
            var name = prop.Name;
            if (name.Equals("post", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.Equals("get", StringComparison.OrdinalIgnoreCase)
                || name.Equals("put", StringComparison.OrdinalIgnoreCase)
                || name.Equals("delete", StringComparison.OrdinalIgnoreCase)
                || name.Equals("patch", StringComparison.OrdinalIgnoreCase))
            {
                hasHttpMethodKeys = true;
            }
        }

        // Empty route stub `{}` treated as advertised path; explicit method map without post rejects POST.
        return !hasHttpMethodKeys;
    }

    private async Task ReconcileSnapshotsAsync(Uri baseUri, CancellationToken ct)
    {
        string? sessionsRaw = null;
        string? statusesRaw = null;
        string? permissionsRaw = null;

        try
        {
            using var sessionsResp = await SendAuthorizedAsync(HttpMethod.Get, new Uri(baseUri, "session"), content: null, ct)
                .ConfigureAwait(false);
            if (sessionsResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return;
            }

            if (sessionsResp.IsSuccessStatusCode)
            {
                sessionsRaw = await sessionsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        var hasStatusResponse = false;
        try
        {
            using var statusResp = await SendAuthorizedAsync(HttpMethod.Get, new Uri(baseUri, "session/status"), content: null, ct)
                .ConfigureAwait(false);
            if (statusResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return;
            }

            if (statusResp.IsSuccessStatusCode)
            {
                statusesRaw = await statusResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                hasStatusResponse = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore partial status query failures without falsely finishing turns.
        }

        try
        {
            using var permResp = await SendAuthorizedAsync(HttpMethod.Get, new Uri(baseUri, "permission"), content: null, ct)
                .ConfigureAwait(false);
            if (permResp.StatusCode == HttpStatusCode.Unauthorized)
            {
                MarkAuthenticationRequired();
                return;
            }

            if (permResp.IsSuccessStatusCode)
            {
                permissionsRaw = await permResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Older OpenCode builds may not expose GET /permission.
        }

        var statusMap = ParseSessionStatusMap(statusesRaw);
        var pendingPermissions = ParsePendingPermissionsMap(permissionsRaw);
        var now = _utcNowProvider().ToUniversalTime();
        var activePermissionIds = new HashSet<string>(pendingPermissions.Values.Select(p => p.PermissionId), StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(sessionsRaw))
        {
            var seenSessionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawSession in EnumerateSessionObjects(sessionsRaw))
            {
                var nativeId = GetString(rawSession, "id")
                    ?? GetString(rawSession, "sessionID")
                    ?? GetString(rawSession, "sessionId");
                if (string.IsNullOrWhiteSpace(nativeId))
                {
                    continue;
                }

                var namespacedId = $"{Id}:{nativeId}";
                seenSessionIds.Add(namespacedId);

                var directory = GetString(rawSession, "directory")
                    ?? GetString(rawSession, "cwd")
                    ?? GetString(rawSession, "workspacePath")
                    ?? string.Empty;
                var title = GetString(rawSession, "title")
                    ?? GetString(rawSession, "summary")
                    ?? "OpenCode session";
                var pid = GetInt(rawSession, "pid") ?? GetInt(rawSession, "processId");
                var createdAt = ExtractTimestamp(rawSession, "time", "created") ?? now;
                var updatedAt = ExtractTimestamp(rawSession, "time", "updated") ?? now;

                _sessions.TryGetValue(namespacedId, out var existing);

                var reconciledStatus = existing?.Snapshot.Status ?? AgentStatus.Working;
                var reconciledFatal = existing?.Snapshot.IsFatalError ?? false;
                string? pendingReqId = null;
                string? pendingTool = null;
                string? pendingSummary = null;

                if (pendingPermissions.TryGetValue(nativeId, out var perm))
                {
                    reconciledStatus = AgentStatus.AwaitingApproval;
                    reconciledFatal = false;
                    pendingReqId = perm.PermissionId;
                    pendingTool = perm.ToolName;
                    pendingSummary = perm.Summary;
                    _permissionToSession[perm.PermissionId] = nativeId;
                }
                else if (statusMap.TryGetValue(nativeId, out var liveStatus))
                {
                    reconciledStatus = liveStatus.Status;
                    reconciledFatal = liveStatus.IsFatalError;
                }
                else if (TryExtractExplicitSessionStatus(rawSession, out var explicitStatus, out var explicitFatal))
                {
                    reconciledStatus = explicitStatus;
                    reconciledFatal = explicitFatal;
                }
                else if (hasStatusResponse && existing is not null)
                {
                    // Session present in /session while absent from /session/status & /permission means turn became idle.
                    if (existing.Snapshot.Status is AgentStatus.Working or AgentStatus.AwaitingApproval)
                    {
                        reconciledStatus = AgentStatus.Finished;
                        reconciledFatal = false;
                    }
                }
                else if (existing is null)
                {
                    // Skip stale historical sessions older than 15m unless currently active/busy.
                    if (updatedAt <= now - InactiveSessionExpiryDuration)
                    {
                        continue;
                    }

                    reconciledStatus = AgentStatus.Finished;
                }

                var updatedSnapshot = new AgentSessionSnapshot(
                    SessionId: namespacedId,
                    AdapterId: Id,
                    WorkspaceName: ResolveWorkspaceName(directory, existing?.Snapshot.WorkspaceName),
                    WorkspacePath: string.IsNullOrWhiteSpace(directory) ? (existing?.Snapshot.WorkspacePath ?? string.Empty) : directory,
                    ProcessId: pid ?? existing?.Snapshot.ProcessId,
                    Status: reconciledStatus,
                    IsFatalError: reconciledFatal,
                    ConnectionState: ConnectionState,
                    TaskSummary: string.IsNullOrWhiteSpace(title) ? existing?.Snapshot.TaskSummary : title,
                    PendingToolName: pendingTool,
                    PendingActionSummary: pendingSummary,
                    PendingRequestId: pendingReqId,
                    HasActionablePermissionRequest: ConnectionState == AgentConnectionState.Connected
                        && _replyRoute.Kind != PermissionReplyKind.None
                        && !string.IsNullOrWhiteSpace(pendingReqId),
                    StartedAtUtc: existing?.Snapshot.StartedAtUtc ?? createdAt,
                    LastUpdatedAtUtc: updatedAt);

                UpsertAndEmitIfChanged(updatedSnapshot);
            }

            // Reconcile any previously tracked awaiting session whose permission was answered offline when /permission responded cleanly.
            if (permissionsRaw is not null)
            {
                foreach (var kvp in _sessions.ToArray())
                {
                    var snap = kvp.Value.Snapshot;
                    if (snap.PendingRequestId is not null
                        && !activePermissionIds.Contains(snap.PendingRequestId)
                        && !_pendingPluginPrompts.ContainsKey(snap.PendingRequestId))
                    {
                        var nativeId = StripNamespace(kvp.Key);
                        var resolvedStatus = statusMap.TryGetValue(nativeId, out var s) ? s.Status : AgentStatus.Finished;
                        var resolvedFatal = statusMap.TryGetValue(nativeId, out var s2) && s2.IsFatalError;
                        var cleared = snap with
                        {
                            Status = resolvedStatus,
                            IsFatalError = resolvedFatal,
                            PendingRequestId = null,
                            PendingToolName = null,
                            PendingActionSummary = null,
                            HasActionablePermissionRequest = false,
                            LastUpdatedAtUtc = now
                        };
                        UpsertAndEmitIfChanged(cleared);
                    }
                }
            }
        }

        ArmOrDisarmExpiryTimer();
    }

    private bool ProcessSerializedEventPayload(string rawJson, string? fallbackEventType = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch
        {
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            string? topDirectory = GetString(root, "directory");
            JsonElement eventObj = root;

            if (root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind == JsonValueKind.Object)
            {
                eventObj = payloadEl;
            }

            var eventType = GetString(eventObj, "type")
                ?? GetString(eventObj, "event")
                ?? fallbackEventType;

            if (string.IsNullOrWhiteSpace(eventType))
            {
                return false;
            }

            var properties = eventObj.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object
                ? propsEl
                : eventObj;

            var permissionId = GetString(properties, "id")
                ?? GetString(properties, "permissionID")
                ?? GetString(properties, "permissionId")
                ?? GetString(properties, "requestID")
                ?? GetString(properties, "requestId");

            var nativeSessionId = ResolveNativeSessionId(properties, eventObj, permissionId);
            if (string.IsNullOrWhiteSpace(nativeSessionId))
            {
                // Strict multi-session isolation: ignore ambiguous payloads lacking session/permission ownership.
                Interlocked.Increment(ref _ambiguousEventsIgnored);
                _logger?.Warn($"Ignored ambiguous OpenCode event '{eventType}' with no resolvable sessionID.");
                return false;
            }

            var namespacedId = $"{Id}:{nativeSessionId}";
            var now = _utcNowProvider().ToUniversalTime();
            var dedupKey = BuildDedupKey(nativeSessionId, eventType, permissionId, properties, rawJson);
            var tracked = _sessions.GetOrAdd(
                namespacedId,
                _ => new TrackedSession(CreateDefaultSnapshot(namespacedId, topDirectory, now)));

            if (!tracked.TryRecordEventKey(dedupKey, DedupRingCapacity))
            {
                Interlocked.Increment(ref _duplicateEventsIgnored);
                return false;
            }

            var previous = tracked.Snapshot;
            var eventDirectory = GetString(properties, "directory")
                ?? GetString(properties, "cwd")
                ?? GetNestedString(properties, "info", "directory")
                ?? topDirectory
                ?? previous.WorkspacePath;
            var eventTitle = GetNestedString(properties, "info", "title")
                ?? GetString(properties, "title")
                ?? GetString(properties, "summary");
            var eventPid = GetInt(properties, "pid")
                ?? GetInt(properties, "processId")
                ?? GetNestedInt(properties, "info", "pid");

            var next = previous with
            {
                WorkspacePath = string.IsNullOrWhiteSpace(eventDirectory) ? previous.WorkspacePath : eventDirectory,
                WorkspaceName = ResolveWorkspaceName(eventDirectory, previous.WorkspaceName),
                ProcessId = eventPid ?? previous.ProcessId,
                ConnectionState = ConnectionState,
                LastUpdatedAtUtc = now
            };

            if (!string.IsNullOrWhiteSpace(eventTitle))
            {
                next = next with { TaskSummary = eventTitle };
            }

            switch (eventType.Trim())
            {
                case "session.created":
                case "session.updated":
                    if (TryExtractExplicitSessionStatus(properties, out var explicitStatus, out var explicitFatal))
                    {
                        next = next with
                        {
                            Status = explicitStatus,
                            IsFatalError = explicitFatal,
                            HasActionablePermissionRequest = explicitStatus == AgentStatus.AwaitingApproval
                                && ConnectionState == AgentConnectionState.Connected
                                && Capabilities.SupportsInteractiveApproval
                                && next.PendingRequestId is not null
                        };
                    }
                    else if (next.Status != AgentStatus.AwaitingApproval)
                    {
                        next = next with { Status = AgentStatus.Working, IsFatalError = false };
                    }

                    break;

                case "session.status":
                    var (statusVal, isFatalStatus) = ParseStatusProperty(properties);
                    next = next with
                    {
                        Status = statusVal,
                        IsFatalError = isFatalStatus,
                        PendingRequestId = statusVal is AgentStatus.Finished or AgentStatus.NeedsAttention ? null : next.PendingRequestId,
                        PendingToolName = statusVal is AgentStatus.Finished or AgentStatus.NeedsAttention ? null : next.PendingToolName,
                        PendingActionSummary = statusVal is AgentStatus.Finished or AgentStatus.NeedsAttention ? null : next.PendingActionSummary,
                        HasActionablePermissionRequest = statusVal == AgentStatus.AwaitingApproval
                            && ConnectionState == AgentConnectionState.Connected
                            && Capabilities.SupportsInteractiveApproval
                            && next.PendingRequestId is not null
                    };
                    var statusMessage = GetNestedString(properties, "status", "message") ?? GetString(properties, "message");
                    if (!string.IsNullOrWhiteSpace(statusMessage))
                    {
                        next = next with { TaskSummary = statusMessage };
                    }

                    break;

                case "tool.execute.before":
                case "tool.execute.after":
                case "message.updated":
                case "message.part.updated":
                    var toolName = GetString(properties, "tool")
                        ?? GetNestedString(properties, "part", "tool")
                        ?? GetString(properties, "toolName");
                    if (next.Status != AgentStatus.AwaitingApproval)
                    {
                        next = next with
                        {
                            Status = AgentStatus.Working,
                            IsFatalError = false,
                            TaskSummary = !string.IsNullOrWhiteSpace(toolName)
                                ? $"Running {toolName}"
                                : next.TaskSummary
                        };
                    }

                    break;

                case "session.error":
                case "tool.execute.error":
                    var isFatalError = GetBool(properties, "fatal")
                        ?? GetBool(properties, "isFatal")
                        ?? (GetNestedBool(properties, "error", "isRetryable") is true ? false : true);
                    var errMessage = GetNestedString(properties, "error", "data", "message")
                        ?? GetNestedString(properties, "error", "message")
                        ?? GetString(properties, "message")
                        ?? "Session error";
                    next = next with
                    {
                        Status = AgentStatus.NeedsAttention,
                        IsFatalError = isFatalError,
                        TaskSummary = errMessage,
                        PendingRequestId = null,
                        PendingToolName = null,
                        PendingActionSummary = null,
                        HasActionablePermissionRequest = false
                    };
                    break;

                case "session.idle":
                case "session.deleted":
                    next = next with
                    {
                        Status = AgentStatus.Finished,
                        IsFatalError = false,
                        PendingRequestId = null,
                        PendingToolName = null,
                        PendingActionSummary = null,
                        HasActionablePermissionRequest = false
                    };
                    break;

                case "permission.asked":
                case "permission.updated":
                    var replyStatus = GetString(properties, "status");
                    if (string.Equals(replyStatus, "replied", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(replyStatus, "resolved", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(replyStatus, "approved", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(replyStatus, "denied", StringComparison.OrdinalIgnoreCase))
                    {
                        next = next with
                        {
                            Status = AgentStatus.Working,
                            IsFatalError = false,
                            PendingRequestId = null,
                            PendingToolName = null,
                            PendingActionSummary = null,
                            HasActionablePermissionRequest = false
                        };
                        break;
                    }

                    var resolvedPermId = string.IsNullOrWhiteSpace(permissionId)
                        ? $"perm_{Guid.NewGuid():N}"
                        : permissionId;
                    _permissionToSession[resolvedPermId] = nativeSessionId;

                    var permTool = GetString(properties, "permission")
                        ?? GetString(properties, "tool")
                        ?? GetString(properties, "type")
                        ?? "Permission";
                    var permSummary = GetString(properties, "title")
                        ?? GetString(properties, "pattern")
                        ?? GetNestedString(properties, "metadata", "command")
                        ?? permTool;

                    next = next with
                    {
                        Status = AgentStatus.AwaitingApproval,
                        IsFatalError = false,
                        PendingRequestId = resolvedPermId,
                        PendingToolName = permTool,
                        PendingActionSummary = permSummary,
                        HasActionablePermissionRequest = ConnectionState == AgentConnectionState.Connected
                            && Capabilities.SupportsInteractiveApproval
                    };
                    break;

                case "permission.replied":
                    next = next with
                    {
                        Status = AgentStatus.Working,
                        IsFatalError = false,
                        PendingRequestId = null,
                        PendingToolName = null,
                        PendingActionSummary = null,
                        HasActionablePermissionRequest = false
                    };
                    break;

                default:
                    return false;
            }

            tracked.Snapshot = next;
            SessionUpdated?.Invoke(this, next);
            ArmOrDisarmExpiryTimer();
            return true;
        }
    }

    private async Task<PermissionActionResult> RespondViaPluginPipeAsync(
        PendingPluginPrompt prompt,
        bool allow,
        string? reason,
        CancellationToken ct)
    {
        if (_pluginSupportsInteractiveHook == false)
        {
            return new PermissionActionResult(false, null, "OpenCode plugin SDK reported interactive hooks unavailable.");
        }

        try
        {
            var verdict = allow ? "once" : "reject";
            var responseObj = new JsonObject
            {
                ["requestId"] = prompt.RequestId,
                ["decision"] = allow ? "allow" : "deny",
                ["verdict"] = verdict
            };
            if (!allow && !string.IsNullOrWhiteSpace(reason))
            {
                responseObj["reason"] = reason.Trim();
            }

            await prompt.WriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await prompt.Writer.WriteLineAsync(responseObj.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
                await prompt.Writer.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                prompt.WriteGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var acked = await prompt.AckTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!acked)
            {
                return new PermissionActionResult(false, null, "OpenCode plugin pipe closed before confirming permission verdict.");
            }

            CommitConfirmedPermissionResolution(prompt.SessionId, prompt.RequestId, allow);
            return new PermissionActionResult(true, "opencode.plugin.ack", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PermissionActionResult(false, null, $"OpenCode plugin bridge confirmation failed: {ex.Message}");
        }
    }

    private async Task RunPluginPipeAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pluginPipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 4096,
                    outBufferSize: 4096,
                    PipeSecurityFactory.CreateCurrentUserOnlyPipeSecurity());

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = server;
                server = null;

                _ = Task.Run(() => HandlePluginClientAsync(connected, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                try
                {
                    await _delayProvider(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandlePluginClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = false })
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var kind = GetString(root, "kind");
                    if (string.Equals(kind, "handshake", StringComparison.OrdinalIgnoreCase))
                    {
                        _pluginSupportsInteractiveHook = GetBool(root, "supportsInteractivePermissionHook") ?? false;
                        SetConnectionState(AgentConnectionState.Connected);
                        continue;
                    }

                    if (string.Equals(kind, "decision_ack", StringComparison.OrdinalIgnoreCase))
                    {
                        var ackReqId = GetString(root, "requestId");
                        if (!string.IsNullOrWhiteSpace(ackReqId)
                            && _pendingPluginPrompts.TryRemove(ackReqId, out var prompt))
                        {
                            prompt.AckTcs.TrySetResult(true);
                            prompt.Dispose();
                        }

                        continue;
                    }

                    if (string.Equals(kind, "permission_request", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_pluginSupportsInteractiveHook is null)
                        {
                            _pluginSupportsInteractiveHook = GetBool(root, "supportsInteractivePermissionHook") ?? true;
                        }

                        SetConnectionState(AgentConnectionState.Connected);
                        var rawSessionId = GetString(root, "sessionID") ?? GetString(root, "sessionId");
                        var reqId = GetString(root, "requestId") ?? Guid.NewGuid().ToString("N");
                        if (!string.IsNullOrWhiteSpace(rawSessionId))
                        {
                            var nsId = $"{Id}:{rawSessionId}";
                            var prompt = new PendingPluginPrompt(nsId, reqId, writer);
                            _pendingPluginPrompts[reqId] = prompt;
                        }

                        var synth = new JsonObject
                        {
                            ["type"] = "permission.asked",
                            ["properties"] = new JsonObject
                            {
                                ["id"] = reqId,
                                ["sessionID"] = rawSessionId,
                                ["permission"] = GetString(root, "permission") ?? GetString(root, "tool"),
                                ["title"] = GetString(root, "title") ?? GetString(root, "summary"),
                                ["directory"] = GetString(root, "directory")
                            }
                        };
                        ProcessSerializedEventPayload(synth.ToJsonString());
                        continue;
                    }

                    if (ConnectionState != AgentConnectionState.Connected)
                    {
                        SetConnectionState(AgentConnectionState.Connected);
                    }

                    ProcessSerializedEventPayload(line);
                }
                catch
                {
                    // Ignore malformed plugin frames without crashing pipe server.
                }
            }

            // Clean up any unresolved prompts associated with this disconnected plugin stream.
            foreach (var kvp in _pendingPluginPrompts.ToArray())
            {
                if (ReferenceEquals(kvp.Value.Writer, writer)
                    && _pendingPluginPrompts.TryRemove(kvp.Key, out var orphaned))
                {
                    orphaned.AckTcs.TrySetResult(false);
                    orphaned.Dispose();
                }
            }
        }
    }

    private void CommitConfirmedPermissionResolution(string sessionId, string requestId, bool allow)
    {
        if (!_sessions.TryGetValue(sessionId, out var tracked))
        {
            return;
        }

        var current = tracked.Snapshot;
        if (!string.Equals(current.PendingRequestId, requestId, StringComparison.Ordinal))
        {
            return;
        }

        var updated = current with
        {
            Status = AgentStatus.Working,
            PendingRequestId = null,
            PendingToolName = null,
            PendingActionSummary = null,
            HasActionablePermissionRequest = false,
            TaskSummary = allow
                ? $"Approved {current.PendingToolName ?? "permission"} (once)"
                : $"Denied {current.PendingToolName ?? "permission"}",
            LastUpdatedAtUtc = _utcNowProvider().ToUniversalTime()
        };
        tracked.Snapshot = updated;
        SessionUpdated?.Invoke(this, updated);
    }

    private void UpsertAndEmitIfChanged(AgentSessionSnapshot next)
    {
        if (_sessions.TryGetValue(next.SessionId, out var existing))
        {
            var prev = existing.Snapshot;
            if (prev.Status == next.Status
                && prev.IsFatalError == next.IsFatalError
                && prev.ConnectionState == next.ConnectionState
                && string.Equals(prev.PendingRequestId, next.PendingRequestId, StringComparison.Ordinal)
                && prev.HasActionablePermissionRequest == next.HasActionablePermissionRequest
                && string.Equals(prev.TaskSummary, next.TaskSummary, StringComparison.Ordinal))
            {
                return;
            }

            existing.Snapshot = next;
        }
        else
        {
            _sessions[next.SessionId] = new TrackedSession(next);
        }

        SessionUpdated?.Invoke(this, next);
    }

    private void MarkAuthenticationRequired()
    {
        _authBlocked = true;
        lock (_stateSync)
        {
            _lastHealthResult = new AdapterHealthResult(
                AdapterHealthStatus.AuthenticationRequired,
                "Authentication required (HTTP 401): configure or update OpenCode server password in Developer Integrations Settings.");
        }

        SetConnectionState(AgentConnectionState.Disconnected);
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

        // Preserve each session's last known AgentStatus across transient disconnects (never fabricate Finished!).
        foreach (var kvp in _sessions.ToArray())
        {
            var snap = kvp.Value.Snapshot;
            var actionable = nextState == AgentConnectionState.Connected
                && Capabilities.SupportsInteractiveApproval
                && snap.Status == AgentStatus.AwaitingApproval
                && !string.IsNullOrWhiteSpace(snap.PendingRequestId);

            var updated = snap with
            {
                ConnectionState = nextState,
                HasActionablePermissionRequest = actionable
            };
            kvp.Value.Snapshot = updated;
            SessionUpdated?.Invoke(this, updated);
        }

        ConnectionStateChanged?.Invoke(this, nextState);
    }

    private void ArmOrDisarmExpiryTimer()
    {
        if (_disposed)
        {
            return;
        }

        var hasExpirable = _sessions.Values.Any(s =>
            s.Snapshot.Status == AgentStatus.Finished
            || s.Snapshot.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable);

        if (!hasExpirable)
        {
            _expiryTimer.Cancel();
            return;
        }

        var earliestExpiry = _sessions.Values
            .Where(s => s.Snapshot.Status == AgentStatus.Finished
                || s.Snapshot.ConnectionState is AgentConnectionState.Disconnected or AgentConnectionState.Unavailable)
            .Min(s => s.Snapshot.LastUpdatedAtUtc + InactiveSessionExpiryDuration);

        var delay = earliestExpiry - _utcNowProvider().ToUniversalTime();
        if (delay < TimeSpan.FromMilliseconds(50))
        {
            delay = TimeSpan.FromMilliseconds(50);
        }

        _expiryTimer.Schedule(delay, () => PurgeInactiveSessions(_utcNowProvider()));
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, Uri uri, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, uri);
        if (content is not null)
        {
            request.Content = content;
        }

        var password = _preferences.ReadOpenCodeServerPassword();
        if (!string.IsNullOrEmpty(password))
        {
            var raw = Encoding.UTF8.GetBytes($"opencode:{password}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = CreateAuthorizedRequest(method, uri, content);
        return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }

    private string? ResolveNativeSessionId(JsonElement properties, JsonElement eventObj, string? permissionId)
    {
        var direct = GetString(properties, "sessionID")
            ?? GetString(properties, "sessionId")
            ?? GetString(properties, "session_id")
            ?? GetNestedString(properties, "info", "sessionID")
            ?? GetNestedString(properties, "info", "id")
            ?? GetNestedString(properties, "part", "sessionID")
            ?? GetString(eventObj, "sessionID")
            ?? GetString(eventObj, "sessionId");

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (!string.IsNullOrWhiteSpace(permissionId)
            && _permissionToSession.TryGetValue(permissionId, out var mappedSessionId))
        {
            return mappedSessionId;
        }

        return null;
    }

    private static string BuildDedupKey(
        string nativeSessionId,
        string eventType,
        string? permissionId,
        JsonElement properties,
        string rawJson)
    {
        var eventOrPermId = permissionId
            ?? GetString(properties, "eventId")
            ?? GetNestedString(properties, "part", "id")
            ?? GetNestedString(properties, "info", "id")
            ?? string.Empty;

        var timestamp = GetString(properties, "timestamp")
            ?? GetNestedString(properties, "time", "updated")
            ?? string.Empty;

        if (!string.IsNullOrEmpty(timestamp))
        {
            return $"{nativeSessionId}|{eventType}|{eventOrPermId}|{timestamp}";
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawJson));
        var shortHash = Convert.ToHexString(hashBytes.AsSpan(0, 8));
        return $"{nativeSessionId}|{eventType}|{eventOrPermId}|{shortHash}";
    }

    private static AgentSessionSnapshot CreateDefaultSnapshot(string sessionId, string? directory, DateTimeOffset now) =>
        new(
            SessionId: sessionId,
            AdapterId: Id,
            WorkspaceName: ResolveWorkspaceName(directory, null),
            WorkspacePath: directory ?? string.Empty,
            ProcessId: null,
            Status: AgentStatus.Working,
            IsFatalError: false,
            ConnectionState: AgentConnectionState.Connected,
            TaskSummary: "OpenCode session",
            PendingToolName: null,
            PendingActionSummary: null,
            PendingRequestId: null,
            HasActionablePermissionRequest: false,
            StartedAtUtc: now,
            LastUpdatedAtUtc: now);

    private static string StripNamespace(string namespacedSessionId)
    {
        const string prefix = Id + ":";
        return namespacedSessionId.StartsWith(prefix, StringComparison.Ordinal)
            ? namespacedSessionId[prefix.Length..]
            : namespacedSessionId;
    }

    private static string ResolveWorkspaceName(string? directory, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.IsNullOrWhiteSpace(fallback) ? "Workspace" : fallback;
        }

        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }

    private static IEnumerable<JsonElement> EnumerateSessionObjects(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item.Clone();
                }
            }

            yield break;
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var dataArr)
            && dataArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataArr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item.Clone();
                }
            }
        }
    }

    private static Dictionary<string, (AgentStatus Status, bool IsFatalError)> ParseSessionStatusMap(string? rawJson)
    {
        var map = new Dictionary<string, (AgentStatus, bool)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var (status, isFatal) = ParseStatusElement(prop.Value);
                    map[prop.Name] = (status, isFatal);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var sid = GetString(item, "sessionID") ?? GetString(item, "id");
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        map[sid] = ParseStatusElement(item);
                    }
                }
            }
        }
        catch
        {
            // Malformed status payload ignored cleanly.
        }

        return map;
    }

    private static Dictionary<string, PendingPermissionDto> ParsePendingPermissionsMap(string? rawJson)
    {
        var map = new Dictionary<string, PendingPermissionDto>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var sid = GetString(item, "sessionID") ?? GetString(item, "sessionId");
                var pid = GetString(item, "id") ?? GetString(item, "permissionID") ?? GetString(item, "requestID");
                if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(pid))
                {
                    continue;
                }

                var tool = GetString(item, "permission") ?? GetString(item, "tool") ?? "Permission";
                var summary = GetString(item, "title") ?? GetString(item, "pattern") ?? tool;
                map[sid] = new PendingPermissionDto(pid, tool, summary);
            }
        }
        catch
        {
            // Ignore malformed permission snapshot.
        }

        return map;
    }

    private static (AgentStatus Status, bool IsFatalError) ParseStatusProperty(JsonElement properties)
    {
        if (properties.TryGetProperty("status", out var statusEl))
        {
            return ParseStatusElement(statusEl);
        }

        return ParseStatusElement(properties);
    }

    private static (AgentStatus Status, bool IsFatalError) ParseStatusElement(JsonElement el)
    {
        string? typeName = el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : GetString(el, "type") ?? GetString(el, "status") ?? GetString(el, "state");

        var isFatalExplicit = GetBool(el, "fatal") ?? GetBool(el, "isFatal");

        return typeName?.Trim().ToLowerInvariant() switch
        {
            "idle" or "completed" or "finished" => (AgentStatus.Finished, false),
            "retry" => (AgentStatus.NeedsAttention, false),
            "error" or "failed" or "needsattention" or "needs_attention" => (AgentStatus.NeedsAttention, isFatalExplicit ?? true),
            "awaitingapproval" or "awaiting_approval" or "permission" => (AgentStatus.AwaitingApproval, false),
            _ => (AgentStatus.Working, false)
        };
    }

    private static bool TryExtractExplicitSessionStatus(
        JsonElement obj,
        out AgentStatus status,
        out bool isFatalError)
    {
        if (obj.TryGetProperty("status", out var statusEl))
        {
            (status, isFatalError) = ParseStatusElement(statusEl);
            return true;
        }

        if (obj.TryGetProperty("info", out var infoEl)
            && infoEl.ValueKind == JsonValueKind.Object
            && infoEl.TryGetProperty("status", out var nestedStatusEl))
        {
            (status, isFatalError) = ParseStatusElement(nestedStatusEl);
            return true;
        }

        status = AgentStatus.Working;
        isFatalError = false;
        return false;
    }

    private static DateTimeOffset? ExtractTimestamp(JsonElement obj, string parentKey, string childKey)
    {
        if (!obj.TryGetProperty(parentKey, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parent.TryGetProperty(childKey, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var epochMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }

        if (prop.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(prop.GetString(), out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static int? GetInt(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var prop)
        && prop.ValueKind == JsonValueKind.Number
        && prop.TryGetInt32(out var val)
            ? val
            : null;

    private static bool? GetBool(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var prop)
        && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static string? GetNestedString(JsonElement obj, string k1, string k2) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(k1, out var mid)
            ? GetString(mid, k2)
            : null;

    private static string? GetNestedString(JsonElement obj, string k1, string k2, string k3) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(k1, out var mid)
            ? GetNestedString(mid, k2, k3)
            : null;

    private static int? GetNestedInt(JsonElement obj, string k1, string k2) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(k1, out var mid)
            ? GetInt(mid, k2)
            : null;

    private static bool? GetNestedBool(JsonElement obj, string k1, string k2) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(k1, out var mid)
            ? GetBool(mid, k2)
            : null;

    private static string CreateTimestampedBackup(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var backupPath = Path.Combine(dir, $"{name}.backup.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}{ext}");
        File.Copy(filePath, backupPath, overwrite: true);
        return backupPath;
    }

    private static string GeneratePluginScript(string pipeName) =>
        $$"""
        // Auto-generated opt-in Shima OpenCode bridge plugin (fail-open).
        // Subscribes to documented OpenCode events and forwards them to \\.\pipe\{{pipeName}}.
        import net from "node:net";

        const PIPE_PATH = "\\\\.\\pipe\\{{pipeName}}";

        function sendEvent(payload) {
          return new Promise((resolve) => {
            const client = net.createConnection(PIPE_PATH, () => {
              client.write(JSON.stringify(payload) + "\n");
              client.end();
              resolve();
            });
            client.setTimeout(50, () => {
              client.destroy();
              resolve();
            });
            client.on("error", () => resolve());
          });
        }

        export const ShimaBridgePlugin = async () => {
          return {
            event: async ({ event }) => {
              if (!event || !event.type) return;
              await sendEvent(event);
            }
          };
        };

        export default ShimaBridgePlugin;
        """;

    private static async Task DefaultDelayWithJitterAsync(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = Random.Shared.Next(0, 250);
        await Task.Delay(baseDelay + TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);
    }

    public enum PermissionReplyKind
    {
        None = 0,
        SessionPermissions = 1,
        GlobalPermissionReply = 2
    }

    internal sealed record PermissionReplyRoute(PermissionReplyKind Kind, string Template)
    {
        public static readonly PermissionReplyRoute None = new(PermissionReplyKind.None, string.Empty);

        public string BuildRelativePath(string sessionId, string permissionId) =>
            Kind switch
            {
                PermissionReplyKind.SessionPermissions => $"/session/{Uri.EscapeDataString(sessionId)}/permissions/{Uri.EscapeDataString(permissionId)}",
                PermissionReplyKind.GlobalPermissionReply => $"/permission/{Uri.EscapeDataString(permissionId)}/reply",
                _ => string.Empty
            };
    }

    private sealed record PendingPermissionDto(string PermissionId, string ToolName, string Summary);

    private sealed class TrackedSession(AgentSessionSnapshot initialSnapshot)
    {
        private readonly object _dedupSync = new();
        private readonly Queue<string> _recentOrder = new();
        private readonly HashSet<string> _recentSet = new(StringComparer.Ordinal);

        public AgentSessionSnapshot Snapshot { get; set; } = initialSnapshot;

        public bool TryRecordEventKey(string key, int capacity)
        {
            lock (_dedupSync)
            {
                if (_recentSet.Contains(key))
                {
                    return false;
                }

                _recentSet.Add(key);
                _recentOrder.Enqueue(key);
                while (_recentOrder.Count > capacity)
                {
                    var oldest = _recentOrder.Dequeue();
                    _recentSet.Remove(oldest);
                }

                return true;
            }
        }
    }

    private sealed class PendingPluginPrompt(string sessionId, string requestId, StreamWriter writer) : IDisposable
    {
        public string SessionId { get; } = sessionId;
        public string RequestId { get; } = requestId;
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public TaskCompletionSource<bool> AckTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => WriteGate.Dispose();
    }
}
