# Contract: Coding Agent Integrations (`Shima.Widgets.Developer` — Phase 2)

**Contract Scope**: Independent, strictly opt-in, fail-open adapters for **Claude Code** and **OpenCode** under `ICodingAgentAdapter`.

---

## 1. Unified Adapter Interface (`ICodingAgentAdapter`)

```text
enum AgentStatus { Working, NeedsAttention, AwaitingApproval, Finished }

record AgentCapabilities(
    bool SupportsInteractiveApproval,
    bool SupportsSessionElapsedTimer,
    bool SupportsTerminalFocus,
    bool IsOptInConfigured)

record AgentSessionSnapshot(
    string SessionId,
    string AdapterId,                   // "claude-code" | "opencode"
    string WorkspaceName,
    string WorkspacePath,
    int? ProcessId,
    AgentStatus Status,
    string? PendingToolName,
    string? PendingActionSummary,
    string? PendingRequestId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastUpdatedAtUtc)

interface ICodingAgentAdapter
  string AdapterId { get; }
  string DisplayName { get; }
  AgentCapabilities Capabilities { get; }
  Task<AdapterHealthResult> CheckHealthAsync()
  Task StartAsync(CancellationToken ct)                  // Starts only if user opted in
  Task StopAsync()
  Task<bool> RespondToApprovalAsync(string sessionId, string requestId, bool allow, string? reason)
  Task<bool> FocusTerminalAsync(string sessionId)
  event EventHandler<AgentSessionSnapshot> SessionUpdated
  event EventHandler<string> SessionRemoved
```

---

## 2. Claude Code Adapter Contract (`ClaudeCodeAdapter`)

- **Opt-In Registration**:
  - Disabled by default. User clicks **Install Claude Code Hooks** in `Settings → Developer Integrations`.
  - Creates a timestamped backup of `~/.claude/settings.json` and registers Shima's standalone `Shima.AgentBridge.exe` for `PreToolUse`, `PermissionRequest`, `Notification`, `Stop`, and `SubagentStop`.
  - **Uninstall Hooks** cleanly removes only Shima-owned hook entries.
- **IPC Transport (`\\.\pipe\shima-agent-bridge`)**:
  - Named Pipe created with Windows `PipeSecurity` ACL allowing `CurrentUserSid` ONLY (`NT AUTHORITY\SYSTEM` & other local users denied).
- **Hook Payload & Response Contract**:
  - `Shima.AgentBridge.exe` reads Claude Code's `stdin` JSON (`session_id`, `cwd`, `hook_event_name`, `tool_name`, `tool_input`).
  - For `PermissionRequest` / `PreToolUse`: sends prompt to Shima over Named Pipe and waits up to configurable timeout (default `120s`, or `0` = immediate fail-open if Shima closed).
  - **User clicks Allow**: Bridge prints `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","permissionDecision":"allow"}}` (or `PreToolUse` equivalent) to `stdout` and exits `0`.
  - **User clicks Deny**: Bridge prints `permissionDecision: "deny"` with reason to `stdout` and exits `0`.
  - **Fail-Open Passthrough (Mandatory Safety Invariant)**: If Named Pipe is not listening (Shima closed), pipe disconnects mid-prompt, or timeout expires → `Shima.AgentBridge.exe` outputs **nothing** to `stdout` and exits `0` immediately so Claude Code seamlessly shows its normal interactive terminal prompt without hanging or auto-denying.

---

## 3. OpenCode Adapter Contract (`OpenCodeAdapter`)

- **Opt-In Registration**:
  - Disabled by default. User explicitly enables OpenCode in `Settings → Developer Integrations` and selects either:
    1. **Local Server SSE Mode**: User-provided local base URL (validated `http://127.0.0.1:<port>` or `http://localhost:<port>`, default `http://localhost:4096`) — **zero background port scanning**.
    2. **OpenCode Plugin Hook Mode**: User-triggered install of a lightweight TypeScript plugin into `~/.config/opencode/plugins/shima-bridge.ts` bridging `event` and `permission.ask` to Shima's user-ACL Named Pipe.
- **Event Mapping**:
  - `session.created` / `tool.execute.before` → `AgentStatus.Working`
  - `permission.updated` / `permission.ask` → `AgentStatus.AwaitingApproval`
  - `session.idle` / `session.deleted` → `AgentStatus.Finished`
  - `permission.replied` (answered in terminal TUI) → immediately clears pending approval card in Island (`Fail-Open / Terminal Sync`).
- **Capability Degradation**:
  - If connected in read-only SSE observer mode without interactive permission reply token/plugin, `Capabilities.SupportsInteractiveApproval` is `false`: the UI renders live status + **Focus Terminal** (`SetForegroundWindow`) instead of non-functional Allow/Deny buttons.
