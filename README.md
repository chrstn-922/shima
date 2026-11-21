# Shima

Shima is a Windows Dynamic Island desktop companion. It sits flush at the top-center edge of the display, stays collapsed and idle at ~0% CPU, and expands into 8 modular widgets on click, hover, or system events. It is an independent product; Navisle is used only as a public behavioral reference.

**Status: Phases 1–3 complete (T001–T018 done). Phase 4 (T019–T028) is next.** The solution now has core contracts, SQLite WAL persistence, atomic config storage, the widget registry, island state and pinning, Win32 overlay adapters, and the WPF island shell. The Release build passes with 0 warnings and 0 errors, and the test suite covers the core, persistence, interop, and shell layers. Widget implementations, Settings, and the Advanced Color Picker are still pending.

## Features

**Widgets** (`IWidgetModule` implementations, each independently enable/disable/reorder/configure):

- **Clock** — local time, date, UTC offset, multi-city world clock, 12h/24h.
- **Now Playing** — GSMTC media session metadata, artwork, transport and seek controls.
- **Focus Timer** — Pomodoro and custom intervals, Zen mode with white/pink/brown noise, crash-safe screen dimming/grayscale.
- **Notes** — multi-note scratchpad, auto-save within 500 ms, search.
- **Inbox** — native Windows notification capture grouped by app, DND-aware, bidirectional Action Center dismissal.
- **Clipboard** — local text/image history, pin, search, pause, clear, password-manager exclusion.
- **Volume Mixer** — per-app WASAPI volume sliders and mute, synced with Windows.
- **Developer Activity** — opt-in Claude Code and OpenCode adapters with `Working` / `Needs attention` / `Awaiting approval` / `Finished` states and Allow/Deny.

**Appearance system**: six island color tokens (Background, Accent, Border, Highlight/Glow, Text, Icon) for collapsed and expanded states, with a Photoshop-style Advanced Color Picker — 2D saturation/brightness field, 0–360° hue slider, synchronized HEX/RGB/HSB inputs, Current vs. New swatch, 6 built-in presets, custom presets, recent colors, live pinned island preview, and non-blocking WCAG contrast warnings with `Restore Recommended Contrast`.

**Local-first privacy**: preferences, widget state, notes, clipboard, and notification history stay in `%LOCALAPPDATA%\Shima\`. Sensitive content is never transmitted. Core functionality opens zero outbound network connections. Clipboard payloads flagged by password managers are skipped.

## Tech Stack

- .NET 8 (LTS) / C# 12, `net8.0-windows10.0.22621.0`
- WPF for the island shell and settings (`WriteableBitmap` for the color field)
- CsWin32 for Win32/COM bindings; WinRT projections for GSMTC, `UserNotificationListener`, `StartupTask`
- NAudio.Wasapi for CoreAudio sessions and Zen ambient noise
- Microsoft.Data.Sqlite in WAL mode, plus atomic JSON config files
- Velopack with a Sparse MSIX package for update delivery and notification identity
- xUnit, FluentAssertions, NSubstitute, FlaUI for tests

## Architecture & Docs

Five layers with inward-only dependencies. `Shima.Core` holds contracts and domain models and depends on nothing else. `Shima.Presentation` (WPF shell, widgets host, settings, color picker), `Shima.Widgets.*` (one assembly per widget), `Shima.WindowsInterop` (Win32/WinRT/COM adapters), and `Shima.Infrastructure` (SQLite, JSON, DPAPI, services) all depend only on `Shima.Core`. `Shima.App` is the composition root; `Shima.AgentBridge` is a small standalone CLI for Claude Code hooks.

- [spec.md](specs/001-shima-dynamic-island/spec.md) — product spec, user stories, edge cases, functional requirements
- [plan.md](specs/001-shima-dynamic-island/plan.md) — architecture, project layout, constitution check
- [research.md](specs/001-shima-dynamic-island/research.md) — Windows API and stack research
- [data-model.md](specs/001-shima-dynamic-island/data-model.md) — SQLite schema, JSON configs, retention
- [tasks.md](specs/001-shima-dynamic-island/tasks.md) — T001–T056, dependency-ordered
- [quickstart.md](specs/001-shima-dynamic-island/quickstart.md) — prerequisites and validation walkthrough
- [contracts/](specs/001-shima-dynamic-island/contracts/) — widget, Windows adapter, coding agent, color picker, appearance, infrastructure contracts
- [system-architecture.md](docs/blueprint/system-architecture.md) — layer breakdown and diagrams
- [windows-integration-design.md](docs/blueprint/windows-integration-design.md) — overlay window, fullscreen detection, DPI, pinning
- [color-picker-architecture.md](docs/blueprint/color-picker-architecture.md) and [appearance-system.md](docs/blueprint/appearance-system.md)
- [security-privacy-architecture.md](docs/blueprint/security-privacy-architecture.md) and [performance-strategy.md](docs/blueprint/performance-strategy.md)
- [test-strategy.md](docs/blueprint/test-strategy.md), [deployment-strategy.md](docs/blueprint/deployment-strategy.md), [risk-register.md](docs/blueprint/risk-register.md)
- [development-roadmap.md](docs/blueprint/development-roadmap.md), [architectural-decision-records.md](docs/blueprint/architectural-decision-records.md)

## Local Setup

Prerequisites:

- Windows 10 version 2004 (build 19041) or newer, or Windows 11 — x64 or ARM64
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) with the Windows Desktop workload
- Windows 10/11 SDK `10.0.22621.0` or newer (WinRT projections, `MakeAppx.exe`, `SignTool.exe`)
- PowerShell 5.1+ or pwsh 7+
- Optional, for the Developer widget: Claude Code and/or OpenCode CLI

Verify the environment:

```powershell
[System.Environment]::OSVersion.Version   # expect >= 10.0.19041
dotnet --info                             # expect an 8.0.x SDK
& ".specify/scripts/powershell/check-prerequisites.ps1" -Json
```

Build, test, and run from the repository root:

```powershell
dotnet build Shima.sln -c Release
dotnet test Shima.sln -c Release
dotnet run --project src/Shima.App -c Release
```

The Release build is expected to finish with 0 warnings and 0 errors. Full validation scenarios are in [quickstart.md](specs/001-shima-dynamic-island/quickstart.md).

## Roadmap

Order and milestones come from [development-roadmap.md](docs/blueprint/development-roadmap.md) and [tasks.md](specs/001-shima-dynamic-island/tasks.md).

| Phase | Tasks | Milestone | Status |
| :--- | :--- | :--- | :--- |
| Blueprint: spec, research, plan, data model, contracts, docs | — | — | Done |
| 1. Solution scaffolding + Phase 0 API spikes | T001–T004 | M0 | Done |
| 2. Core contracts, SQLite, config, widget registry | T005–T010 | M0/M1 | Done |
| 3. Island shell (states, DPI, fullscreen, pinning) | T011–T018 | M1 | Done |
| 4. Settings + Advanced Color Picker + appearance | T019–T028 | M2 | Next |
| 5. Clock widget | T029–T032 | M3 | Not started |
| 6. Now Playing + Volume Mixer | T033–T037 | M3 | Not started |
| 7. Notes + Clipboard + Inbox | T038–T044 | M4 | Not started |
| 8. Focus Timer + Zen Mode | T045–T048 | M3 | Not started |
| 9. Developer Activity (Claude Code, OpenCode) | T049–T053 | M5 | Not started |
| 10. Packaging, signing, release verification | T054–T056 | M6 | Not started |

Next step: Phase 4, starting with T019–T021 (color conversion, contrast audit, and appearance persistence tests) before the picker and Settings UI.
