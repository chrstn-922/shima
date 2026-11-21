# Security & Privacy Architecture: Shima

**Document Role**: Defines Shima's local-first data security controls, threat model, clipboard/notification privacy boundaries, coding-agent IPC hardening, and zero-telemetry network policy.

---

## 1. Local Data Protection & Secure Storage

- **User-Isolated Storage Root**: All data resides under `%LOCALAPPDATA%\Shima\` (`data/shima.db`, `config/*.json`, `cache/*`, `logs/*`) with explicit NTFS directory ACLs restricting read/write access exclusively to the current Windows user SID (`NT AUTHORITY\SYSTEM` + `CurrentUser`).
- **Encryption at Rest**:
  - Sensitive configuration secrets (such as optional license activation tokens) are encrypted using **Windows DPAPI** (`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`).
  - SQLite uses strictly parameterized queries (`SqliteCommand` parameters) across all repositories to prevent local SQL injection.
- **Input Validation**: All user inputs (world clock city names, preset names, HEX/RGB/HSB strings, OpenCode local URLs, note payloads) undergo schema length, regex, and boundary validation in `Shima.Core` before reaching persistence or OS layers.

---

## 2. Sensitive Feature Privacy Controls

### 2.1 Clipboard Privacy
- **Password-Manager Exclusion**: Before reading clipboard text, `ClipboardMonitorAdapter` inspects clipboard format identifiers for `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory` (`DWORD == 0`) and immediately aborts capture if present.
- **Size & Type Guardrails**: Caps text payloads at `1 MB` and image storage/thumbnails at `10 MB`; ignores raw file drops and proprietary binary formats.
- **Pause & Retention**: Users can pause clipboard capture in one click (island header or tray menu), configure auto-expiry (`1–365 days`, max `100` unpinned items default), and clear all unpinned items instantly.

### 2.2 Notification (`Inbox`) Privacy
- Strictly limited to native Windows toast APIs (`UserNotificationListener`)—zero accessibility window scraping of third-party apps.
- Supports per-application exclusion filters, configurable auto-expiry (`14 days` default), and an optional **"Mask notification body in popups"** privacy mode.

### 2.3 Sanitized Diagnostic Logging
- Local rotating file logs (`%LOCALAPPDATA%\Shima\logs\`, max `3 × 2 MB` files) record operational lifecycle events and sanitized exception stack traces only.
- **Strict PII Redaction**: Clipboard payloads, note contents, notification titles/bodies, and coding-agent prompts/tool inputs MUST NEVER be written to log files.

---

## 3. Network Communication Boundaries

| Component | Outbound Network | Inbound Listeners | Security Controls |
| :--- | :---: | :--- | :--- |
| **Core Island & All 8 Widgets** | **Forbidden (`0` bytes)** | None (except local agent Named Pipe) | Verified via socket integration tests. |
| **Coding Agent IPC (`ClaudeCodeAdapter`)** | None (Local Named Pipe only) | `\\.\pipe\shima-agent-bridge` | Windows `PipeSecurity` ACL locked to `CurrentUserSid`; zero TCP ports opened. |
| **Coding Agent IPC (`OpenCodeAdapter`)** | Loopback `127.0.0.1` / `localhost` only (opt-in) | None | Rejects any non-loopback host URL (`http://127.0.0.1:*` / `localhost:*` enforced). |
| **Update & License Adapters** | Opt-in HTTPS only (TLS 1.2+) | None | Signature/SHA-256 verification for updates; offline-cached signed token + salted HWID hash for optional licensing. |

---

## 4. Threat Model Matrix

| Threat ID | Threat & Attack Vector | Impact | Architectural Mitigation | Residual Risk |
| :--- | :--- | :--- | :--- | :--- |
| **T-01** | **Clipboard Credential Leakage**: Password manager copies secret to clipboard; Shima stores it plaintext on disk. | High | Honor `ExcludeClipboardContentFromMonitorProcessing` & `CanIncludeInClipboardHistory`; user-ACL directory; one-click pause; bounded retention & purge. | Password managers that fail to set OS exclusion flags (user can pause capture or delete entry). |
| **T-02** | **Local Agent Pipe Spoofing**: Malicious local process sends fake `Awaiting approval` prompt to trick user into clicking Allow. | Medium | Named Pipe ACL restricts connection to `CurrentUserSid`; Island displays full `WorkspacePath`, `ProcessId`, and exact command/tool arguments before Allow. | Compromised process already running under the same user account. |
| **T-03** | **2FA / Private Message Exposure via Inbox**: Sensitive OTP codes captured in Inbox history. | Medium | Per-app exclusion list, DND suppression, auto-purge retention policy, body masking option, and immediate Clear All. | Transient screen visibility if user leaves Inbox open while unattended. |
| **T-04** | **OpenCode Endpoint SSRF / Remote Redirect**: Malicious config points OpenCode adapter to external internet server. | Medium | Strict URI validator rejecting non-loopback hosts (`IPAddress.IsLoopback` check required before connecting). | Local malicious service listening on localhost port explicitly entered by user. |
| **T-05** | **Supply-Chain / Tampered Update Binary**: MITM or compromised release artifact during update. | High | HTTPS TLS pinning/validation + Authenticode signature & SHA-256 manifest verification before staging Velopack update. | Compromised upstream signing certificate. |
