# Architectural Decision Records (ADRs): Shima

---

## ADR-0001: Primary Desktop Stack — .NET 8 WPF + WinRT Projection + CsWin32

- **Status**: Accepted (`2026-11-21`)
- **Context**: Shima requires borderless per-pixel transparent non-activating topmost windowing (`WS_EX_NOACTIVATE`), Per-Monitor DPI v2, direct WinRT (`GSMTC`, `UserNotificationListener`) and COM (`WASAPI`) access, `< 50 MB` collapsed RAM, and `0.0%` idle CPU across `x64` and `ARM64`.
- **Decision**: Adopt **C# 12 / .NET 8.0 LTS + WPF + `Microsoft.Windows.CsWin32` + Windows SDK WinRT projection**.
- **Consequences**: Eliminates multi-process WebView2/Chromium memory overhead (`Tauri`/`Electron`), avoids WinUI 3 transparent non-activating window workarounds, and yields single-language testability. Requires explicit `PerMonitorV2` app manifest and custom `WriteableBitmap` rendering for the Color Picker 2D canvas.

---

## ADR-0002: Hybrid Local Storage — SQLite (WAL) + Atomic Versioned JSON

- **Status**: Accepted (`2026-11-21`)
- **Context**: Notes, clipboard history, and inbox notifications require fast indexed search and retention pruning, whereas appearance tokens and app settings require instant startup loading and zero DB writes during live Color Picker scrubbing.
- **Decision**: Use `Microsoft.Data.Sqlite` (WAL mode) for collection entities and atomic `File.Replace` JSON files (`settings.json`, `appearance.json`, `presets.json`) for configurations and presets under `%LOCALAPPDATA%\Shima\`.
- **Consequences**: Survives application upgrades cleanly, prevents partial-write corruption, and ensures live color preview never blocks on disk I/O.

---

## ADR-0003: Packaging & Identity — Velopack + Signed Sparse MSIX Package

- **Status**: Accepted (`2026-11-21`)
- **Context**: Windows `UserNotificationListener` enforces AppX Package Identity (`userNotificationListener` capability), yet full MSIX virtualization complicates delta updates and external CLI hook script paths (`Shima.AgentBridge.exe`).
- **Decision**: Distribute via **Velopack** (per-user `%LOCALAPPDATA%` delta updates) paired with an optional signed **Sparse MSIX Package** (`AllowExternalContent`, Windows 10 2004+), with graceful `IdentityUnavailable` degradation in Inbox when unregistered or on older Windows 10 builds.
- **Consequences**: Combines fast delta updates and clean external hook paths with native Windows notification support on Tier 1 OS builds.

---

## ADR-0004: Fail-Safe Zen Mode Display Filter via Process-Scoped Magnification API

- **Status**: Accepted (`2026-11-21`)
- **Context**: Zen Mode screen grayscale/dimming must never leave a user's monitor stuck dimmed or grayscale if Shima crashes or is force-killed.
- **Decision**: Use DWM process-scoped `MagSetFullscreenColorEffect` (plus click-through layered dimming window) guarded by a pre-effect `runtime-journal.json` flag rather than persistent Windows Registry Color Filter mutations or hardware gamma ramps.
- **Consequences**: Windows DWM automatically tears down the visual effect upon process exit/termination, backed by startup journal recovery.

---

## ADR-0005: Non-Blocking Contrast Warnings & Temporary Preview Pinning (`FR-PICKER-006`)

- **Status**: Accepted (`2026-11-21`, User Clarified Option A)
- **Context**: Users editing island colors inside Settings would lose sight of an auto-collapsing island, while blocking `Apply / OK` on contrast errors restricts creative freedom.
- **Decision**: Isolate preview edits in `AppearanceEditingSession`, acquire an `ExplicitPin` via `OverlayPinController` (with Collapsed/Expanded preview toggle and focus isolation), evaluate WCAG 2.1 contrast as a **non-blocking advisory warning** with a one-click `Restore Recommended Contrast` solver, and keep picker `Reset to Default` slot-scoped in preview.
- **Consequences**: Predictable visual WYSIWYG editing with deterministic `Cancel` rollback and zero accidental overwrite of unrelated tokens.

---

## ADR-0006: Strictly Opt-In, Fail-Open Coding Agent Integrations (`Claude Code` & `OpenCode`)

- **Status**: Accepted (`2026-11-21`, User Clarified Option A)
- **Context**: Blocking a coding-agent `PreToolUse`/`PermissionRequest` hook when Shima is closed would freeze the user's terminal; scanning localhost ports violates zero-polling/privacy rules.
- **Decision**: Implement `ClaudeCodeAdapter` and `OpenCodeAdapter` as separate opt-in modules configured explicitly in Settings, communicate via user-SID-locked Named Pipe (`Shima.AgentBridge.exe`) or explicit loopback endpoint, and guarantee fail-open empty-stdout `exit 0` terminal passthrough on disconnect/timeout.
- **Consequences**: Zero risk of hanging developer terminals and zero unprompted file edits or port scans.
