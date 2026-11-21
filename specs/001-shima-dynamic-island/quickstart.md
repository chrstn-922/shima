# Quickstart & Validation Guide: Shima

**Feature**: `001-shima-dynamic-island` | **Date**: `2026-11-21`  
**Audience**: Contributors preparing the local Windows development environment and verifying the Shima blueprint before and during implementation.

---

## 1. Prerequisites

- **Operating System**: Windows 10 Version 2004 (Build 19041) or Windows 11 (22H2/23H2/24H2) — `x64` or `ARM64`.
- **SDK & Tooling**:
  - [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`8.0.x` LTS) with **Windows Desktop (`Microsoft.WindowsDesktop.App`)** workload.
  - Windows 10/11 SDK (`10.0.22621.0` or newer) for WinRT projections and `MakeAppx.exe` / `SignTool.exe` (Sparse Package identity testing).
  - PowerShell 5.1+ / pwsh 7+.
  - Optional (Phase 2 Developer Widget validation): `Claude Code` CLI and/or `OpenCode` CLI installed locally.

---

## 2. Environment Verification (Pre-Implementation)

Confirm required SDKs and Windows build baseline before scaffolding projects:

```powershell
# Verify OS Build >= 19041 for Sparse Package + PerMonitorV2 support
[System.Environment]::OSVersion.Version

# Verify .NET 8 SDK & Windows Desktop Runtime
dotnet --info

# Verify SpecKit feature context
& ".specify/scripts/powershell/check-prerequisites.ps1" -Json
```

---

## 3. Planned Build & Automated Test Commands *(Execution Phase)*

Once source assemblies under `src/` and `tests/` are scaffolded per [plan.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/plan.md) and [tasks.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/tasks.md):

```powershell
# Restore & build all layers (Zero warnings as errors)
dotnet build Shima.sln -c Release

# Run fast deterministic unit & infrastructure tests
dotnet test tests/Shima.Core.Tests/Shima.Core.Tests.csproj
dotnet test tests/Shima.Infrastructure.Tests/Shima.Infrastructure.Tests.csproj
dotnet test tests/Shima.Widgets.Tests/Shima.Widgets.Tests.csproj

# Run Windows interop contract tests (requires Windows desktop session)
dotnet test tests/Shima.WindowsInterop.Tests/Shima.WindowsInterop.Tests.csproj

# Run automated UI & Color Picker FlaUI tests
dotnet test tests/Shima.UI.Tests/Shima.UI.Tests.csproj
```

---

## 4. End-to-End Validation Scenarios (Acceptance Walkthrough)

Refer to [test-strategy.md](file:///C:/Users/chris/Projects/shima/docs/blueprint/test-strategy.md) for detailed assertions. Key validation gates:

1. **Shell & Multi-Monitor DPI**:
   - Launch Shima; verify top-center flush anchor (`Y = 0`), concave top fillets, and `0.0%` idle CPU after 5 seconds.
   - Switch primary display scaling `100%` → `150%` → `200%`; confirm crisp vector edges and centered placement.
2. **Fullscreen & Maximized Window Coexistence**:
   - Maximize a browser; verify clicks on top-row tabs outside the compact pill are unobstructed, and `Alt`+hover enables temporary click-through.
   - Open a fullscreen video/game; confirm the island disappears completely within `500 ms`, top-edge mouse hits do not trigger it, and the global hotkey summons it on demand.
3. **Color Picker Live Preview, Pinning & Contrast (`FR-PICKER-006`)**:
   - Open `Settings → Appearance & Customization → Edit Background Color`.
   - Confirm the real top-center Dynamic Island pins open automatically and stays open while the cursor remains inside Settings.
   - Toggle **Collapsed / Expanded** preview mode; drag the 2D S/B field and Hue slider; confirm `< 16.6 ms` updates and synchronized `HEX`/`RGB`/`HSB` inputs.
   - Choose a low-contrast text/background pair (`< 4.5:1`); confirm the **non-blocking advisory warning** appears while **Apply / OK** remains enabled; test saving the low-contrast choice and verify **Restore Recommended Contrast** adjusts contrast back to `>= 4.5:1`.
   - Test **Cancel** rollback, **Reset to Default** (single-slot staging), and restart persistence (`%LOCALAPPDATA%\Shima\config\appearance.json`).
4. **Inbox & Coding-Agent Fail-Open Validation**:
   - Verify `UserNotificationListener` with Sparse Package registered vs. unregistered fallback guidance card.
   - With Shima closed, trigger the Claude Code hook bridge via stdin JSON and verify it exits `0` cleanly in `< 50 ms` (fail-open passthrough) rather than hanging.
5. **Privacy & Zen Mode Crash Recovery**:
   - Verify `0` outbound network sockets open during core widget use (`Get-NetTCPConnection -OwningProcess <PID>`).
   - Enable Zen Mode grayscale/dimming, terminate process abruptly (`taskkill /F /IM Shima.App.exe`), and confirm display returns to normal colors immediately and clean recovery journal runs on relaunch.
