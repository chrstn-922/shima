# Implementation Plan & Software Architecture Blueprint: Shima

**Branch**: `001-shima-dynamic-island` | **Date**: `2026-11-21` | **Spec**: [spec.md](spec.md)  
**Input**: Approved specification [spec.md](spec.md) (including Clarifications Session `2026-11-21`), Phase 0 research [research.md](research.md), and [constitution.md](../../.specify/memory/constitution.md) (`v1.0.0`).

---

## Summary

Shima is an independent, local-first Windows Dynamic Island desktop companion anchored at the top-center display edge. The system uses a **5-layer modular architecture** built on **.NET 8 (LTS) / C# 12 with WPF hardware-accelerated vector composition, WinRT projections (`10.0.22621.0`), `CsWin32` P/Invoke/COM interop, and local SQLite (WAL) + atomic JSON storage**.

Core engineering pillars:
1. **Unobtrusive Native Shell**: `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` overlay with concave ear geometry, Per-Monitor DPI v2 scaling, strict fullscreen complete-hide + hotkey summon (`FR-SHELL-007`), and priority-arbitrated pinning (`OverlayPinController`) for the Advanced Color Picker (`FR-PICKER-006`).
2. **Event-Driven Performance**: Zero polling loops; all OS monitors (`GSMTC`, `WASAPI`, `UserNotificationListener`, `WM_CLIPBOARDUPDATE`, `SetWinEventHook`, `WM_POWERBROADCAST`) suspend UI ticks and composition animations when idle, collapsed, occluded, or on Battery Saver.
3. **8 Independent Widget Modules**: `Clock`, `Now Playing`, `Focus Timer`, `Notes`, `Inbox`, `Clipboard`, `Volume Mixer`, and `Developer Activity` (independent opt-in fail-open `ClaudeCodeAdapter` & `OpenCodeAdapter`) orchestrated behind `IWidgetModule` and `IWidgetRegistry` fault boundaries.
4. **Precision Color & Theme Engine**: Photoshop 22.3.0-inspired `HEX`/`RGB`/`HSB` color picker with real-time `WriteableBitmap` 2D field rendering, live dynamic island preview pinning, non-blocking WCAG contrast warnings (`4.5:1` text / `3.0:1` icons), and one-click `Restore Recommended Contrast`.

---

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 LTS (`net8.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` `10.0.19041.0`)  
**Primary Dependencies**:
- UI & Shell: WPF (`UseWPF`), native WPF `WriteableBitmap` / `BitmapSource` (2D color canvas), `Microsoft.Extensions.DependencyInjection`
- Windows OS Interop: `Microsoft.Windows.CsWin32` (source-generated Win32/COM bindings), Windows SDK WinRT projection (`GSMTC`, `UserNotificationListener`, `UISettings`, `StartupTask`), `NAudio` (`NAudio.Wasapi` for Volume Mixer CoreAudio sessions & Zen Mode DSP ambient audio synthesis)
- Storage & Packaging: `Microsoft.Data.Sqlite` (SQLite 3 WAL mode), `System.Text.Json` (source-generated atomic configs), `System.Security.Cryptography.ProtectedData` (DPAPI), `Velopack` + Sparse Package `AppxManifest.xml`
**Storage**: `%LOCALAPPDATA%\Shima\data\shima.db` (SQLite WAL) + `%LOCALAPPDATA%\Shima\config\{settings,appearance,presets}.json` (atomic temp-file swap)  
**Testing**: `xUnit`, `Moq` / `NSubstitute`, `FluentAssertions`, FlaUI / Windows UIA integration harness, Win32/COM adapter contract fakes, and BenchmarkDotNet / ETW profiling  
**Target Platform**: Windows 10 Version 2004 (Build 19041)+ & Windows 11 (`win-x64` and `win-arm64`), unpackaged/Velopack with optional Sparse MSIX Package Identity (graceful Inbox degradation on Windows 10 1809–1909 or un-registered runs)  
**Project Type**: Desktop application (modular multi-assembly solution)  
**Performance Goals**: `0.0%` steady-state idle CPU (`< 0.5%` 60s average), `< 50 MB` collapsed RAM (`< 85 MB` expanded), `< 400 ms` cold start, `>= 60 FPS` (`< 16.6 ms` frame budget) animations & color picker drags  
**Constraints**: `0` core outbound network bytes, standard non-admin user privileges, fail-safe Zen Mode display restoration, non-blocking WCAG contrast warnings (`FR-PICKER-006`)  
**Scale/Scope**: Single-user local workstation companion; 8 modular widgets; 10 settings sections; 6 built-in + unlimited custom color presets; bounded retention pools (Clipboard, Inbox, Focus sessions)

---

## Constitution Check

*GATE: Checked pre-Phase 0 and re-verified post-Phase 1 design against [constitution.md](../../.specify/memory/constitution.md).*

| Constitution Principle | Compliance Status | Architectural Enforcement |
| :--- | :---: | :--- |
| **I. Architecture & Code Quality** | **PASS** | Strict 5-layer dependency inversion: `Presentation`, `WindowsInterop`, `Widgets`, and `Infrastructure` depend *only* on `Shima.Core` abstractions; zero screen/widget logic in shared shell components; minimal NuGet footprint (`CsWin32`, `NAudio.Wasapi`, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.DependencyInjection`, `Velopack`). |
| **II. Windows Integration** | **PASS** | Documented Win32/WinRT/COM APIs; Per-Monitor DPI v2 (`WM_DPICHANGED`), `WM_DISPLAYCHANGE`, `SHQueryUserNotificationState` + `SetWinEventHook` fullscreen hiding, `WS_EX_NOACTIVATE` non-activating interaction, power/session lifecycle hooks. |
| **III. Performance & Resource Efficiency** | **PASS** | 100% event-driven adapters (`WM_CLIPBOARDUPDATE`, `GSMTC`, `IAudioSessionNotification`, `UserNotificationListener`); zero polling; UI/visualizer ticks suspended when `Collapsed`/`Idle`/`Occluded`; `GUID_POWER_SAVING_STATUS` + `UISettings.AnimationsEnabled` throttling; throttled live preview dispatch without DB writes. |
| **IV. Security & Privacy (Local-First)** | **PASS** | Local `%LOCALAPPDATA%\Shima\` storage with user-only NTFS ACLs + DPAPI secrets; clipboard privacy format exclusion (`ExcludeClipboardContentFromMonitorProcessing`); `0` core network I/O; user-SID ACL Named Pipe (`\\.\pipe\shima-agent-bridge`); opt-in fail-open agent integrations. |
| **V. User Experience & Accessibility** | **PASS** | Centralized `ThemeResourceDictionary` tokens (zero hardcoded hex in views); High Contrast + Reduced Motion support; full keyboard navigation (`Tab`, `Esc`, arrow keys in 2D color canvas); non-blocking WCAG contrast advisory + `Restore Recommended Contrast`; per-widget independent config & fault boundary. |
| **VI. Engineering Discipline & PM** | **PASS** | Measurable NFRs, ADRs (`docs/blueprint/architectural-decision-records.md`), full test matrix (`docs/blueprint/test-strategy.md`), and phased milestone roadmap (`docs/blueprint/development-roadmap.md`). |
| **VII. Spec-First & Evidence-Based** | **PASS** | Grounded in approved [spec.md](spec.md) + verified official Microsoft/Claude/OpenCode research in [research.md](research.md); no fabricated APIs; explicit `[NEEDS VALIDATION]` markers for Phase 0 prototypes. |

---

## Project Structure

### Documentation (this feature & architectural blueprint)

```text
specs/001-shima-dynamic-island/
├── spec.md                              # Approved product & reverse-engineering specification
├── research.md                          # Phase 0 Windows API, agent & stack research + weighted matrix
├── plan.md                              # This master implementation plan & blueprint index
├── data-model.md                        # SQLite relational schema, JSON config schemas & retention rules
├── quickstart.md                        # Developer prerequisites, build/test & manual validation procedures
├── tasks.md                             # Dependency-ordered phased task breakdown (no source code yet)
├── checklists/
│   └── requirements.md                  # Spec quality validation checklist
└── contracts/
    ├── widget-contract.md               # IWidgetModule, IWidgetRegistry, lifecycle & fault isolation
    ├── windows-adapters-contract.md     # Windowing, Pinning, Notification, Media, Audio, Clipboard, Power
    ├── coding-agent-contract.md         # ICodingAgentAdapter, ClaudeCode & OpenCode independent contracts
    ├── color-picker-contract.md         # Color picker dialog, HSB/RGB/HEX sync & live preview session
    ├── appearance-manager-contract.md   # Appearance tokens, presets, contrast audit & atomic persistence
    └── infrastructure-contracts.md      # Repositories, DPAPI secret store, IUpdateService, ILicenseService

docs/blueprint/
├── system-architecture.md               # 5-layer architecture & all 17 Mermaid system/sequence diagrams
├── ui-ux-design-system.md               # Shima visual identity, dimensions, DPI scaling, typography & a11y
├── windows-integration-design.md        # Deep Win32/WinRT/COM mechanics + Overlay Pinning & Visibility arbiter
├── security-privacy-architecture.md     # Local protection, clipboard/toast privacy, pipe ACLs & threat model
├── performance-strategy.md              # Resource budgets, zero-polling guarantees & preview rendering pipeline
├── color-picker-architecture.md         # 13-component Photoshop-style picker architecture & conversion math
├── appearance-system.md                 # Theme tokens, presets, live preview & pin lifecycle integration
├── color-contrast-validation.md         # WCAG 2.1 luminance math, non-blocking warnings & auto-contrast solver
├── test-strategy.md                     # Comprehensive unit, contract, UIA, DPI, soak & picker test suites
├── deployment-strategy.md               # Velopack + Sparse Package, signing, updates, x64/ARM64 matrix
├── risk-register.md                     # Technical risks, OS constraints & deterministic mitigations
├── development-roadmap.md               # Phased epics, milestones, dependencies & confidence estimates
└── architectural-decision-records.md    # ADRs 0001–0006 documenting key engineering trade-offs
```

### Target Source Code Layout (Repository Root — *Planned for Implementation Phase*)

```text
src/
├── Shima.Core/                          # Layer B: Application Core (Lifecycle, Registry, State, EventBus, Color/Appearance Models)
├── Shima.WindowsInterop/                # Layer C: Win32/WinRT/COM Adapters (Overlay, Pinning, Media, Audio, Notifications, Clipboard, Power, Agents)
├── Shima.Infrastructure/                # Layer E: SQLite WAL Repositories, Atomic JSON Storage, DPAPI, ColorService, Logging, Update/License Adapters
├── Shima.Presentation/                  # Layer A: WPF Island Shell, Concave Geometry, Widget Host, Settings Window, Color Picker UI, Theme Renderer
├── Shima.Widgets/                       # Layer D: Independent IWidgetModule Implementations
│   ├── Shima.Widgets.Clock/
│   ├── Shima.Widgets.NowPlaying/
│   ├── Shima.Widgets.FocusTimer/
│   ├── Shima.Widgets.Notes/
│   ├── Shima.Widgets.Inbox/
│   ├── Shima.Widgets.Clipboard/
│   ├── Shima.Widgets.VolumeMixer/
│   └── Shima.Widgets.Developer/         # Phase 2: ClaudeCodeAdapter + OpenCodeAdapter UI & ViewModels
├── Shima.AgentBridge/                   # Lightweight standalone CLI hook bridge for Claude Code PreToolUse/PermissionRequest Named Pipe IPC
└── Shima.App/                           # Composition Root, App Entry, Single-Instance Mutex, Tray Icon & Sparse Package Registration

tests/
├── Shima.Core.Tests/                    # State machine, WidgetRegistry fault isolation, Color conversion & Contrast math tests
├── Shima.Infrastructure.Tests/          # SQLite WAL migrations, retention pruning, corrupt-file recovery & JSON preset persistence tests
├── Shima.WindowsInterop.Tests/          # Adapter contract tests, mock COM/WinRT events, Named Pipe ACL & fail-open bridge tests
├── Shima.Widgets.Tests/                 # View-model & lifecycle tests for all 8 independent widgets
└── Shima.UI.Tests/                      # FlaUI automated UI tests (Color Picker pinning, Cancel rollback, Low-Contrast save, DPI/Fullscreen)
```

**Structure Decision**: Multi-assembly clean modular monolith (`Shima.Core` at the center with strict inward dependencies). Each widget sits in its own assembly/namespace under `Shima.Widgets.*`, communicating exclusively through `Shima.Core` interfaces so new widgets (9th+) plug into `Shima.App` composition root without touching unrelated modules.

---

## Architecture & Blueprint Navigation

To avoid duplicating prose across files, detailed design artifacts are cross-referenced directly:

1. **System Architecture & 17 Mermaid Diagrams**: See [system-architecture.md](../../docs/blueprint/system-architecture.md) for the complete 5-layer component breakdown and all 17 behavioral/sequence/state Mermaid diagrams.
2. **Widget Architecture & Contracts**: See [widget-contract.md](contracts/widget-contract.md) for `IWidgetModule`, `IWidgetContext`, lifecycle transitions, fault containment, and zero-coupling extensibility.
3. **Database & Persistence Model**: See [data-model.md](data-model.md) for the ER diagram, SQLite WAL tables, JSON appearance schemas, validation rules, retention limits, and update-safe schema migrations.
4. **Windows OS Integration & Overlay Pinning**: See [windows-integration-design.md](../../docs/blueprint/windows-integration-design.md) and [windows-adapters-contract.md](contracts/windows-adapters-contract.md) for non-activating windowing, priority-based `OverlayPinController`, fullscreen detection, Per-Monitor DPI v2, `UserNotificationListener` Sparse Package rules, GSMTC, CoreAudio WASAPI, `WM_CLIPBOARDUPDATE`, and crash-safe `MagSetFullscreenColorEffect` Zen Mode.
5. **Security & Privacy Architecture**: See [security-privacy-architecture.md](../../docs/blueprint/security-privacy-architecture.md) for local ACL/DPAPI storage, clipboard exclusion flags, pipe SID security, network matrix (`0` core bytes), and threat model.
6. **Performance Engineering**: See [performance-strategy.md](../../docs/blueprint/performance-strategy.md) for resource budgets, idle/battery tick suspension, `WriteableBitmap` pixel buffer rendering, and throttled live preview pipelines.
7. **UI/UX Design System**: See [ui-ux-design-system.md](../../docs/blueprint/ui-ux-design-system.md) for Shima's original design language, concave notch geometry, typography, Settings sidebar layout, and WCAG/Reduced-Motion accessibility.
8. **Advanced Color Picker, Appearance System & Contrast Validation (`FR-PICKER-006`)**:
   - [color-picker-architecture.md](../../docs/blueprint/color-picker-architecture.md) & [color-picker-contract.md](contracts/color-picker-contract.md)
   - [appearance-system.md](../../docs/blueprint/appearance-system.md) & [appearance-manager-contract.md](contracts/appearance-manager-contract.md)
   - [color-contrast-validation.md](../../docs/blueprint/color-contrast-validation.md)
9. **Coding Agent Integrations (`Claude Code` & `OpenCode`)**: See [coding-agent-contract.md](contracts/coding-agent-contract.md) for independent opt-in adapter contracts and fail-open terminal passthrough guarantees.
10. **QA, Packaging, Risks & Roadmap**:
    - [test-strategy.md](../../docs/blueprint/test-strategy.md)
    - [deployment-strategy.md](../../docs/blueprint/deployment-strategy.md)
    - [risk-register.md](../../docs/blueprint/risk-register.md)
    - [development-roadmap.md](../../docs/blueprint/development-roadmap.md)
    - [architectural-decision-records.md](../../docs/blueprint/architectural-decision-records.md)

---

## Complexity Tracking

| Architectural Addition | Why Needed | Simpler Alternative Rejected Because |
| :--- | :--- | :--- |
| **Sparse MSIX Package Identity alongside Velopack** | Windows `UserNotificationListener` API strictly refuses access without an AppX Package Identity containing `userNotificationListener`. | Full-only MSIX breaks seamless delta updates and virtualizes filesystem/registry paths needed by external CLI hook installers; raw unpackaged `.exe` cannot read Windows notifications without invasive UI Automation scraping (explicitly prohibited). |
| **Dedicated `Shima.AgentBridge` CLI Executable** | Claude Code hooks invoke a short-lived CLI command passing JSON on `stdin` and reading decision JSON on `stdout`. | Having Claude Code spawn the full WPF GUI app per hook invocation would introduce `300ms+` startup lag and heavy memory spikes; a minimal `~1–2 MB` AOT/small bridge executable connects instantaneously to Shima's Named Pipe and fails open in `< 50 ms` if Shima is closed. |
| **Double-Buffered Temporary Preview Session (`AppearanceEditingSession`)** | `FR-PICKER-006` requires real-time live island rendering while keeping persisted disk state and `Cancel` rollback 100% non-destructive. | Directly mutating the singleton `AppearanceProfile` in place causes partial writes on crash and complicates `Cancel` restoration across 6+ color tokens and per-widget accents. |
