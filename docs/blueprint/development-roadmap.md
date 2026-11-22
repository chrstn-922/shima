# Project Management & Phased Development Roadmap: Shima

**Document Role**: Epics, milestones, dependencies, testing gates, complexity ratings, and confidence-bounded effort estimates (no false precision).  
**Task Granularity**: Actionable dependency-ordered items are tracked in [tasks.md](../../specs/001-shima-dynamic-island/tasks.md).

---

## Estimation Assumptions & Confidence Levels

- **Baseline**: 1–2 senior desktop/Windows engineers working sequentially through Spec Kit phases (`M0` → `M6`).
- **Confidence**: High on Core Shell, SQLite, Clock, Notes, Clipboard, and Color Picker; Medium on WASAPI edge cases, Sparse Package signing/CI, and Phase 2 external coding-agent CLI schema stability.

---

## Milestone Roadmap

### Milestone M0 — Phase 0 OS API Prototypes & Solution Scaffolding
- **Objectives**: Validate all `[NEEDS VALIDATION]` Windows API assumptions (`WS_EX_NOACTIVATE`, borderless fullscreen detection, `MagSetFullscreenColorEffect` on HDR/multi-monitor, Sparse Package `UserNotificationListener`) and scaffold clean 5-layer `.sln`.
- **Complexity / Confidence**: Medium | High Confidence (`1–2 weeks`).
- **Exit Gate**: Signed-off Phase 0 spike demos + compiling empty solution with DI wiring and `xUnit` test projects.

### Milestone M1 — Core Dynamic Island Shell, Pinning & Windows Substrate (Epic 1)
- **Features**: Top-center concave notch window (`Collapsed`, `Active`, `Notification`, `Expanded`, `Idle`, `Fullscreen`, `Maximized` coexistence), `OverlayPinController`, Per-Monitor DPI v2, display hot-plug failover, global hotkey summon, Tray Icon, Startup registration, Reduced Motion & Battery Saver governor.
- **Dependencies**: M0.
- **Testing & DoD**: `0.0%` idle CPU, `< 50 MB` RAM, `< 400 ms` cold start, multi-DPI & fullscreen FlaUI verification pass.
- **Complexity / Confidence**: High | Medium-High Confidence (`2–3 weeks`).

### Milestone M2 — Local Persistence, Widget Registry, Settings & Advanced Color Picker (Epic 2 & 3)
- **Features**: SQLite WAL + migrations, Atomic JSON config + DPAPI, `WidgetRegistry` with fault circuit-breaker & 8-dot carousel, 10-section Settings window, **Appearance System**, **Photoshop-inspired Advanced Color Picker** (`2D Field`, `Hue Slider`, `HEX/RGB/HSB` sync, 6 built-in presets, custom presets, 12 recent colors, `FR-PICKER-006` Live Preview Pinning, Collapsed/Expanded preview toggle, non-blocking WCAG contrast warning + `Restore Recommended Contrast`, slot-only `Reset to Default`).
- **Dependencies**: M1 (`OverlayPinController` required for live preview pinning).
- **Testing & DoD**: 100% unit test coverage on color math/contrast solver + FlaUI verification of `CP-01`..`CP-08` (including saving low-contrast themes).
- **Complexity / Confidence**: High | High Confidence (`2–3 weeks`).

### Milestone M3 — Flagship Media, Time & Focus Widgets (Epic 4)
- **Features**: **Clock** (Image 2 parity, world clock add/remove/reorder, 12h/24h), **Now Playing** (GSMTC metadata, artwork, playback/seek controls, active pill visualizer), **Volume Mixer** (CoreAudio WASAPI per-app sliders, mute, device hot-plug), **Focus Timer** (Pomodoro + custom intervals, local DSP white/pink/brown noise, fail-safe Zen screen grayscale/dimming).
- **Dependencies**: M2.
- **Testing & DoD**: Mock COM/WinRT unit tests + physical crash-recovery verification for Zen Mode display filter.
- **Complexity / Confidence**: High | Medium Confidence (`2–3 weeks`).

### Milestone M4 — Productivity & Capture Widgets (Epic 5)
- **Features**: **Notes** (multi-note auto-save `<= 500 ms`, search, focus-safe persistence), **Clipboard** (`WM_CLIPBOARDUPDATE` text/image capture, SHA-256 dedup, password-manager exclusion, pin/search/pause/clear), **Inbox** (`UserNotificationListener` grouped by app, bidirectional Action Center dismissal toggle, DND respect, graceful `IdentityUnavailable` card).
- **Dependencies**: M2 (SQLite repositories), M1 (Sparse Package identity setup).
- **Testing & DoD**: Zero outbound network socket audit (`0 bytes`), exclusion format unit tests, retention pruning tests.
- **Complexity / Confidence**: Medium | High Confidence (`2 weeks`).

### Milestone M5 — Phase 2 Developer Activity Widget (`Claude Code` & `OpenCode`) (Epic 6)
- **Features**: `ICodingAgentAdapter`, `Shima.AgentBridge.exe` CLI, user-SID ACL Named Pipe, opt-in `ClaudeCodeAdapter` hook installer + fail-open passthrough, opt-in `OpenCodeAdapter` SSE/plugin integration, `Working`/`Needs attention`/`Awaiting approval`/`Finished` live cards + `Allow`/`Deny`/`Focus Terminal`.
- **Dependencies**: M2, M4 (Notification peek pipeline).
- **Testing & DoD**: Simulated `stdin/stdout` fail-open tests with Shima closed/killed, Named Pipe ACL verification, live Claude Code & OpenCode CLI end-to-end validation.
- **Complexity / Confidence**: Medium-High | Medium Confidence (`2 weeks`).

### Milestone M6 — Hardening, Packaging, Signing & Release Readiness (Epic 7)
- **Features**: Velopack `win-x64` & `win-arm64` installers + signed Sparse Package registration, `IUpdateService` + optional offline-first `ILicenseService`, 8-hour memory/handle soak tests, accessibility audit, final release candidate sign-off.
- **Dependencies**: M1–M5.
- **Complexity / Confidence**: Medium | Medium-High Confidence (`1–2 weeks`).
