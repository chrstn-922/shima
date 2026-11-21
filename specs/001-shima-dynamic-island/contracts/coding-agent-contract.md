# Contract: Coding Agent Integrations (`Shima.Widgets.Developer` — Phase 2)

**Contract Scope**: Independent, strictly opt-in, fail-open adapters for **Claude Code** and **OpenCode** under `ICodingAgentAdapter`, normalizing session lifecycles into Shima's shared Developer Activity state model while preserving strict adapter isolation and explicit documentation of unsupported capabilities.

---

## 1. Unified Adapter Interface (`ICodingAgentAdapter`)

```text
enum AgentStatus
{
    Working,          // Actively processing (turn, generation, tool execution)
    NeedsAttention,   // Error, issue, or terminal user intervention needed
    AwaitingApproval, // Paused waiting for an explicit permission/confirmation decision
    Finished          // Task or session completed
}

enum AgentConnectionState
{
    Connected,
    Reconnecting,
    Disconnected,
    Unavailable
}

enum AdapterHealthStatus
{
    Healthy,
    Reconnecting,
    Disconnected,
    AuthenticationRequired,
    Unavailable
}

record AdapterHealthResult(
    AdapterHealthStatus Status,
    string? Message)

record AgentCapabilities(
    bool SupportsInteractiveApproval,
    bool SupportsSessionElapsedTimer,
    bool SupportsTerminalFocus,
    bool SupportsMultiSessionIsolation,
    bool IsOptInConfigured,
    IReadOnlyList<string> UnsupportedCapabilities)

record PermissionActionResult(
    bool Confirmed,
    string? ConfirmationSource,         // e.g., "opencode.http.replied", "opencode.plugin.ack", "claude.pipe.ack"
    string? ErrorMessage)

record AgentSessionSnapshot(
    string SessionId,                   // Strictly namespaced: "<adapterId>:<nativeSessionId>" (e.g. "opencode:ses_123")
    string AdapterId,                   // "claude-code" | "opencode"
    string WorkspaceName,
    string WorkspacePath,
    int? ProcessId,
    AgentStatus Status,
    bool IsFatalError,                  // Distinguishes full red Error/Failed bar from partial red NeedsAttention/AwaitingApproval
    AgentConnectionState ConnectionState,
    string? TaskSummary,
    string? PendingToolName,
    string? PendingActionSummary,
    string? PendingRequestId,
    bool HasActionablePermissionRequest, // True ONLY when Connected && SupportsInteractiveApproval && PendingRequestId != null
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastUpdatedAtUtc)

interface ICodingAgentAdapter
  string AdapterId { get; }
  string DisplayName { get; }
  AgentCapabilities Capabilities { get; }
  AgentConnectionState ConnectionState { get; }
  Task<AdapterHealthResult> CheckHealthAsync(CancellationToken ct = default)
  Task StartAsync(CancellationToken ct = default)       // Starts ONLY if user opted in
  Task StopAsync(CancellationToken ct = default)
  Task<PermissionActionResult> RespondToApprovalAsync(string sessionId, string requestId, bool allow, string? reason, CancellationToken ct = default)
  Task<bool> FocusTerminalAsync(string sessionId, CancellationToken ct = default)
  event EventHandler<AgentSessionSnapshot> SessionUpdated
  event EventHandler<string> SessionRemoved
  event EventHandler<AgentConnectionState> ConnectionStateChanged
```

### Unified Adapter Invariants

1. **Strict Adapter Independence**: `ClaudeCodeAdapter` and `OpenCodeAdapter` operate as completely separate adapter instances implementing `ICodingAgentAdapter`. Enabling, disabling, reconnecting, or faulting one adapter MUST NOT disrupt or block the other.
2. **Confirmed-Only Permission Transitions (`once` / `reject`)**: `RespondToApprovalAsync` MUST dispatch the decision to the native coding agent and return `Confirmed == true` **only after** the native agent confirms receipt/resolution. For OpenCode, inline **Allow** maps strictly to single-action `once` (never `always` implicitly) and **Deny** maps to `reject`, leaving session-wide `always` rules inside OpenCode's native terminal. Shima MUST NEVER optimistically assume approval, simulate approval locally, or bypass the agent's own permission engine.
3. **Actionable Gate**: The Developer Activity UI MUST enable/show **Allow** and **Deny** buttons **only** when `snapshot.HasActionablePermissionRequest == true`. If interactive approval is unsupported or the session is disconnected/reconnecting, `Allow`/`Deny` remain unavailable and the card renders accurate connection/capability status (with **Focus Terminal** shown/invoked only if `Capabilities.SupportsTerminalFocus == true`).
4. **Fail-Open Safety**: If Shima is closed, disconnected, or unresponsive during a permission gate, control MUST fail open back to the agent's native terminal prompt—never auto-denying or hanging the coding session.

---

## 2. Claude Code Adapter Contract (`ClaudeCodeAdapter`)

- **Isolation Boundary**: Implemented in `src/Shima.WindowsInterop/Agents/ClaudeCodeAdapter.cs` using its own Named Pipe server and `Shima.AgentBridge.exe`, sharing only `ICodingAgentAdapter` contracts with `OpenCodeAdapter`.
- **Opt-In Registration**:
  - Disabled by default. User clicks **Install Claude Code Hooks** in `Settings → Developer Integrations`.
  - Creates a timestamped backup of `~/.claude/settings.json` and registers Shima's standalone `Shima.AgentBridge.exe` for `PreToolUse`, `PermissionRequest`, `Notification`, `Stop`, and `SubagentStop`.
  - **Uninstall Hooks** cleanly removes only Shima-owned hook entries.
- **IPC Transport (`\\.\pipe\shima-agent-bridge`)**:
  - Named Pipe created with Windows `PipeSecurity` ACL allowing `CurrentUserSid` ONLY (`NT AUTHORITY\SYSTEM` and other local users denied).
- **Hook Payload & Response Contract**:
  - `Shima.AgentBridge.exe` reads Claude Code's `stdin` JSON (`session_id`, `cwd`, `hook_event_name`, `tool_name`, `tool_input`).
  - For `PermissionRequest` / `PreToolUse`: sends prompt to Shima over Named Pipe and waits up to configurable timeout (default `120s`, or `0` = immediate fail-open if Shima closed).
  - **User clicks Allow**: Bridge prints `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","permissionDecision":"allow"}}` (or `PreToolUse` equivalent) to `stdout`, confirms back via `PermissionActionResult(Confirmed: true, ...)` and exits `0`.
  - **User clicks Deny**: Bridge prints `permissionDecision: "deny"` with reason to `stdout` and exits `0`.
  - **Fail-Open Passthrough (Mandatory Safety Invariant)**: If Named Pipe is not listening (Shima closed), pipe disconnects mid-prompt, or timeout expires → `Shima.AgentBridge.exe` outputs **nothing** to `stdout` and exits `0` immediately so Claude Code seamlessly shows its normal interactive terminal prompt without hanging or auto-denying.

---

## 3. OpenCode Adapter Contract (`OpenCodeAdapter`)

- **Isolation Boundary**: Implemented in `src/Shima.WindowsInterop/Agents/OpenCodeAdapter.cs` (`AdapterId = "opencode"`), completely decoupled from `ClaudeCodeAdapter`. Disabling/enabling, connecting, or encountering errors in `OpenCodeAdapter` leaves `ClaudeCodeAdapter` unaffected.

### 3.1 Supported Documented Event Listeners & Opt-In Channels

Disabled by default. User explicitly configures OpenCode in `Settings → Developer Integrations` via one of two documented integration mechanisms (with **zero silent config edits** and **zero background localhost port scanning**):

1. **Local Server SSE + REST Mode (`opencode serve`)**:
   - Connects strictly to the user-configured loopback base URL (`AppPreferences.openCodeEndpointUrl`, validated `http://127.0.0.1:<port>` or `http://localhost:<port>`, default `http://localhost:4096`).
   - Supports an optional local server password (`AppPreferences.openCodeServerPassword`, encrypted at rest via `DpapiSecretStore` using `DataProtectionScope.CurrentUser`; never logged or stored in plaintext), defaulting to unauthenticated loopback when blank and attaching Basic authentication headers only when configured.
   - Performs health and capability discovery via `GET /global/health` and optional `GET /doc` (OpenAPI spec check) to verify supported REST/permission routes cleanly. If the server returns HTTP `401 Unauthorized`, immediately surfaces `AdapterHealthStatus.AuthenticationRequired` (`ConnectionState = Disconnected`) and halts blind retries until credentials are updated or cleared.
   - Subscribes to the documented Server-Sent Events stream (`GET /global/event`, falling back to `GET /event` only if configured for single-project servers) to receive live session creation, updates, completion, errors, and permission requests.
   - Uses REST snapshot queries (`GET /session`, `GET /session/{id}`, `GET /session/status`) for initial hydration and missed-event recovery after reconnects.
2. **OpenCode Plugin Hook Mode (`~/.config/opencode/plugins/shima-bridge.ts`)**:
   - Installed/uninstalled explicitly by user action with a timestamped backup if an existing file exists.
   - Uses OpenCode's documented plugin `event` subscriber (`session.created`, `session.updated`, `session.status`, `session.error`, `session.idle`, `session.deleted`, `permission.updated`, `permission.replied`, `tool.execute.before/after`) and, when running on an OpenCode plugin SDK version that exposes an interactive permission hook (`permission.ask` on v1.x or context permission evaluation hook on v2.x), forwards prompts to Shima's `CurrentUserSid`-ACL Named Pipe (`\\.\pipe\shima-opencode-bridge`) with strict fail-open timeout passthrough.

### 3.2 Unified 4-State Mapping (`AgentStatus`)

`OpenCodeAdapter` normalizes documented OpenCode events deterministically into Shima's four canonical states:

| Documented OpenCode Event / State | Canonical `AgentStatus` | Details & Notes |
| :--- | :--- | :--- |
| `session.created`, `session.updated` (active turn), `session.status` (`busy` / running), `tool.execute.before`, active message deltas | **`Working`** | Captures `workspace_path`, `WorkspaceName`, and active task/tool context (`TaskSummary`). `IsFatalError = false`. |
| `session.error`, `session.status` (`error` / retry stall), unhandled tool/provider failure | **`NeedsAttention`** | Surfaces error/issue summary so developer intervenes (`IsFatalError = true` for fatal `Error / Failed` collapsed bar when session error terminates/halts run; `false` for non-fatal intervention); does not fabricate `Allow`/`Deny` controls. |
| `permission.asked` / `permission.updated` (pending ask) or plugin `permission.ask` / evaluation hook | **`AwaitingApproval`** | Extracts `PendingRequestId`, `PendingToolName`, and `PendingActionSummary`. Sets `HasActionablePermissionRequest = true` ONLY when verified reply channel is active and `Connected`. |
| `session.idle`, `session.deleted`, explicit turn completion | **`Finished`** | Clears any obsolete pending request ID; triggers `10s` collapsed bar dwell and `15m` expanded row retention. |
| `permission.replied` (resolved in Shima or directly in OpenCode TUI) | Reverts to **`Working`** (or current reconciled state) | Immediately clears `PendingRequestId` / pending toast in Island (`Terminal Sync`); prevents double-actioning an already answered prompt. |

### 3.3 Listener Resilience: Reconnects, Duplicates, Missed Events, Auth & Dwell/Expiry

- **Reconnect Handling**:
  - SSE stream disconnection or mid-turn EOF transitions adapter `ConnectionState` to `Reconnecting` (using bounded exponential backoff with jitter: `1s, 2s, 4s, 8s, 15s, 30s`) and to `Disconnected` / `Unavailable` after threshold failures or unreachable host. HTTP `401 Unauthorized` stops automatic backoff immediately and reports `AdapterHealthStatus.AuthenticationRequired`.
  - **Invariant**: An unexpected SSE stream drop during an active turn MUST NOT falsely mark the session as `Finished`; instead, sessions retain last known status while marking `ConnectionState = Reconnecting/Disconnected` and disabling `Allow`/`Deny` until live state is re-verified.
- **Duplicate Event Handling**:
  - Maintains a bounded per-session deduplication ring/set keyed by `(nativeSessionId, eventType, eventOrPermissionId, timestampOrPayloadHash)` so re-delivered SSE/plugin messages do not trigger duplicate UI state transitions, notification peeks, or database writes.
- **Missed Event Recovery**:
  - Immediately after initial connection and upon **every** SSE reconnection, `OpenCodeAdapter` calls `GET /session` and `GET /session/status` (where exposed) to reconcile missed creations, completions, errors, and permissions answered offline while disconnected.
- **Collapsed Bar Dwell, Expanded Retention & Idle Timer Suspension**:
  - **`Finished` Dwell**: Collapsed bar holds full green `Finished` for `10 seconds` (or immediately clears on hover/open) before reverting to `Idle` gray, while retaining finished rows in the expanded multi-session list for `15 minutes` (or until dismissed).
  - **`Error / Failed` Acknowledgment Cap**: Collapsed bar holds full red/coral (`NeedsAttention` with `IsFatalError = true`) until hovered/opened or dismissed, auto-expiring after a `15-minute` maximum cap.
  - **Idle Suspension**: Once dwell/retention expires into `Idle` and no active sessions remain, all dwell/expiry timers suspend cleanly (`0.0%` idle CPU). Recent historical rows remain bounded in SQLite `AGENT_SESSION_LOG` per `data-model.md`.
  - Disconnected or unavailable OpenCode endpoints render an explicit non-crashing status indicator (`Disconnected` / `Unavailable` / `Authentication required`) in Developer Activity & Settings diagnostics without background UI stalls.

### 3.4 Confirmed Interactive Actions (`Allow` / `Deny`)

- **Gating**: **Allow** and **Deny** actions are exposed and enabled **only** when OpenCode presents a live, actionable permission request (`PendingRequestId != null`, `ConnectionState == Connected`, and `Capabilities.SupportsInteractiveApproval == true`).
- **Action Execution & Confirmation**:
  - *Server REST Mode*: Dispatches `POST /session/{id}/permissions/{permissionId}` with verdict **`once`** for **Allow** and **`reject`** for **Deny** — Shima NEVER exposes or implicitly sends `always`, leaving session-wide `always` rules inside OpenCode's native terminal.
  - *Plugin Hook Mode*: Dispatches `allow` (`once`) / `deny` (`reject`) decision over the authenticated Named Pipe to the awaiting hook callback.
  - **Strict Confirmation Invariant**: Shima marks the permission UI state as `"Pending confirmation..."` on click and commits the resolution in Shima **only after** OpenCode returns a confirmed acknowledgment (`HTTP 2xx` + reconciled/confirmed `permission.replied` event or confirmed plugin bridge ack). If OpenCode rejects, times out, or disconnects, Shima displays a non-blocking failure notice, preserves/reconciles state from OpenCode, and NEVER simulates approval or bypasses OpenCode's security model.

### 3.5 Multi-Session Support & State Isolation

- Every OpenCode session is keyed in an isolated concurrent map by adapter-namespaced `SessionId = "opencode:<nativeSessionId>"` and carries its own `WorkspaceName`, `WorkspacePath`, `TaskSummary`, `Status`, `IsFatalError`, `ConnectionState`, and `PendingRequestId`.
- Events and permission requests MUST match `nativeSessionId` (or unique `permissionId → nativeSessionId` index) before mutating session state; state updates or approvals on Session A MUST NEVER overwrite Session B or clear Session B's pending permission card.
- If multiple OpenCode sessions are concurrently active and an incoming payload omits both `sessionID` and a resolvable `permissionID`, `OpenCodeAdapter` ignores/logs the ambiguous payload rather than guessing or mixing sessions, and records the limitation clearly.

### 3.6 Explicitly Documented Unsupported OpenCode Events & Actions

Shima MUST surface these unsupported or version-dependent boundaries explicitly in Settings capability diagnostics and never silently assume they exist:

| Capability / Edge Case | Actual OpenCode Boundary | Shima Required Behavior |
| :--- | :--- | :--- |
| **Standalone OpenCode TUI without `opencode serve` or Plugin** | Plain terminal execution with neither local HTTP server nor installed plugin exposes no documented IPC event stream. | `ConnectionState = Unavailable`. Never scrape terminal buffers or scan arbitrary ports; display opt-in setup instructions (`opencode serve` URL or plugin install). |
| **Read-Only SSE / Missing Permission Reply Route** | Server configurations or versions lacking an advertised permission reply endpoint/hook cannot receive remote `Allow`/`Deny` verdicts. | `Capabilities.SupportsInteractiveApproval = false`, `HasActionablePermissionRequest = false`. Render live 4-state status without `Allow`/`Deny` buttons. |
| **OpenCode v1 vs. v2 Plugin Permission Hook Differences** | Legacy v1 SDK used `permission.ask`; v2+ refactored permission evaluation hooks and some tool flows bypass plugin interception. | Verify supported plugin hook signature at handshake; disable interactive hook mode (`SupportsInteractiveApproval = false`) if unavailable rather than simulating approval. |
| **Window Focus (`Focus Terminal`) on Headless/Remote Server Session** | `opencode serve` REST/SSE payloads do not guarantee a local client terminal OS `ProcessId`. | `Capabilities.SupportsTerminalFocus = false` whenever `ProcessId == null`; hide/disable **Focus Terminal** instead of focusing the wrong window. |
| **Session-Wide `always` Approvals, Free-Form Text Replies & Arbitrary Abort** | Even where OpenCode's permission API accepts `once`/`always`/`reject`, free-form questions (`NeedsAttention`) have no remote text-reply API, and Shima intentionally restricts inline UI verdicts to least-privilege `once` (**Allow**) and `reject` (**Deny**). | Restrict inline approvals strictly to `once` (**Allow**) and `reject` (**Deny**); leave session-wide `always` rules and free-form replies in OpenCode's native terminal (`FocusTerminal` where PID is known). Never synthesize keystrokes or spoof terminal input. |
| **SSE `Last-Event-ID` Cursor Replay** | OpenCode global SSE does not guarantee durable historical replay queues after disconnects. | Never rely solely on SSE resume; run REST snapshot reconciliation (`GET /session`, `GET /session/status`) on every reconnect. |
