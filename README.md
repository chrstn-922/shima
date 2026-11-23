<div align="center">

<img src="assets/branding/shima-cradled-island.svg" alt="Shima — The Cradled Island" width="112" />

# Shima

**A Windows Dynamic Island desktop companion — ~0% idle CPU, 8 modular widgets.**

[**Download Installer for Windows (64-bit)**](https://github.com/chrstn-922/shima/releases/download/v1.0.1/Shima-Setup-1.0.1-win-x64.exe) ·
[ARM64](https://github.com/chrstn-922/shima/releases/download/v1.0.1/Shima-Setup-1.0.1-win-arm64.exe) ·
[All v1.0.1 Releases](https://github.com/chrstn-922/shima/releases/tag/v1.0.1)

</div>

Shima sits flush at the top-center edge of your Windows screen, stays collapsed at ~0% CPU when idle, and expands into 8 modular widgets on hover, click, or system events. All data stays local on your machine with zero outbound network connections.

## Screenshots

| Collapsed Dynamic Island | Now Playing Compact Pill |
| :---: | :---: |
| ![Collapsed Dynamic Island](docs/media/compact-pill.png) | ![Now Playing Compact Pill](docs/media/now-playing-compact-pill.png) |

| Expanded Widget View | Settings Window |
| :---: | :---: |
| ![Expanded Widget View](docs/media/extended-island.png) | ![Settings Window](docs/media/settings.png) |

## Installation

1. Download [**`Shima-Setup-1.0.1-win-x64.exe`**](https://github.com/chrstn-922/shima/releases/download/v1.0.1/Shima-Setup-1.0.1-win-x64.exe) (or [**`Shima-Setup-1.0.1-win-arm64.exe`**](https://github.com/chrstn-922/shima/releases/download/v1.0.1/Shima-Setup-1.0.1-win-arm64.exe) for ARM64 devices) from the [v1.0.1 release page](https://github.com/chrstn-922/shima/releases/tag/v1.0.1).
2. Run the installer. Because v1.0.1 uses a self-signed certificate, Windows SmartScreen may show *"Windows protected your PC"* — click **More info → Run anyway** to continue.
3. During setup on Windows 10 (build 19041+) or Windows 11, the installer automatically registers the Sparse MSIX identity (`Shima.Identity-<rid>.msix`) so the **Notification Inbox** connects to Windows Action Center right away.
4. Keep **Launch Shima** checked on the final screen.

> Portable `.zip` builds and standalone `.msix` identity packages are also available on the [Releases page](https://github.com/chrstn-922/shima/releases/tag/v1.0.1). Uninstalling from Windows **Installed apps** cleanly removes the app and unregisters the Sparse MSIX identity while keeping your data in `%LOCALAPPDATA%\Shima\`.

### One-Line Terminal Install (Optional)

Paste this into Command Prompt, PowerShell, or the Windows Run dialog (`Win + R`) to download and launch the v1.0.1 installer automatically:

```bat
powershell -Command "irm https://github.com/chrstn-922/shima/releases/download/v1.0.1/Shima-Setup-1.0.1-win-x64.exe -OutFile ([IO.Path]::GetTempPath()+'Shima-Setup.exe'); Start-Process ([IO.Path]::GetTempPath()+'Shima-Setup.exe')"
```

## Features

### 8 Modular Widgets
Each widget can be enabled, disabled, reordered, or configured independently in **Settings**:

- **Clock** — Local time, date, UTC offset, multi-city world clock, and 12h/24h formatting.
- **Now Playing** — System media metadata, album artwork, transport controls, and seek bar via Windows GSMTC.
- **Focus Timer** — Pomodoro and custom intervals with Zen mode (white/pink/brown ambient noise and crash-safe screen dimming/grayscale).
- **Notes** — Multi-note scratchpad with explicit Save (`Ctrl+S`), draft persistence, search, and note navigation.
- **Inbox** — Native Windows notification feed grouped by app, DND-aware, with bidirectional Action Center dismissal.
- **Clipboard** — Local text and image history with pin, search, pause, clear, and automatic password-manager exclusion.
- **Volume Mixer** — Per-app Windows audio volume sliders and mute toggles synced in real time.
- **Developer Activity** — Opt-in Claude Code and OpenCode integration showing `Working`, `Needs attention`, `Awaiting approval`, and `Finished` states with inline Allow/Deny actions.

### Developer Activity Setup

**What triggers it:** Developer Activity listens only to opt-in local events from AI coding agents (no background port scanning or Git/IDE polling):
- **Claude Code:** Runs `Shima.AgentBridge.exe` from lifecycle hooks (`PreToolUse`, `PermissionRequest`, `Notification`, `Stop`, `SubagentStop`) over a local Named Pipe (`\\.\pipe\shima-agent-bridge`).
- **OpenCode:** Connects to a local `opencode serve` loopback endpoint (`http://localhost:4096` / `http://127.0.0.1:*`) and/or the optional plugin bridge (`%USERPROFILE%\.config\opencode\plugins\shima-bridge.ts`). *(Standalone OpenCode TUI without `opencode serve` or the plugin is not monitored.)*

Active turns light up the collapsed notch status bar (**Working** green, **Awaiting approval** / **Needs attention** coral, **Finished** full green for 10s) and show session rows with inline **Allow** / **Deny** buttons when expanded.

**Configure in Settings:**
1. Open Shima **Settings** (via the system tray icon) and ensure **Developer Activity** is enabled under **Widgets**.
2. Open **Developer Integrations** in the sidebar:
   - **Claude Code:** Check **Enable Claude Code local hook bridge (opt-in)**, click **Install Claude hooks** (backs up `%USERPROFILE%\.claude\settings.json` first), then click **Check health**.
   - **OpenCode:** Check **Enable OpenCode local server tracking (opt-in)**, confirm your `http://localhost:*` / `http://127.0.0.1:*` URL (plus optional server password), click **Save developer settings**, optionally click **Install OpenCode plugin**, and click **Check health**.

**Quick test:** Run a task in Claude Code or `opencode serve`, or pipe a test hook event into `Shima.AgentBridge.exe` from PowerShell (with Claude Code opt-in enabled):
```powershell
'{"session_id":"demo-1","cwd":"C:\\repos\\demo","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dotnet test"}}' | & "$env:ProgramFiles\Shima\Shima.AgentBridge.exe" --shima-managed
```
Click **Allow** or **Deny** inside the expanded Dynamic Island (or swap `"PermissionRequest"` for `"Stop"` to trigger a quick green **Finished** flash).

### Customization & Privacy
- **Appearance System** — Six customizable color tokens (Background, Accent, Border, Highlight/Glow, Text, Icon) for both collapsed and expanded states, 6 built-in themes, custom presets, live preview, and WCAG contrast warnings.
- **Advanced Color Picker** — 2D saturation/brightness field, 0–360° hue slider, synchronized HEX/RGB/HSB inputs, and recent color swatches.
- **Local-First by Default** — Settings, notes, clipboard history, and notifications are stored locally in `%LOCALAPPDATA%\Shima\` using SQLite (WAL mode) and atomic JSON files. Core features make zero network requests.

## Tech Stack & Architecture

- **.NET 8 (LTS) / C# 12** targeting `net8.0-windows10.0.22621.0`
- **WPF** for the island shell, settings window, and custom color picker
- **CsWin32 & WinRT** for Win32/COM overlays, GSMTC media controls, `UserNotificationListener`, and `StartupTask`
- **NAudio.Wasapi** for CoreAudio volume sessions and ambient Zen audio
- **SQLite (`Microsoft.Data.Sqlite`)** in WAL mode for local persistence
- **Sparse MSIX + Inno Setup** for desktop installation and Windows notification identity

The solution is split into five inward-only dependency layers (`Shima.Core`, `Shima.Presentation`, `Shima.Widgets.*`, `Shima.WindowsInterop`, `Shima.Infrastructure`), with `Shima.App` as the composition root and `Shima.AgentBridge` as a standalone CLI for coding-agent hooks. Full design docs and specs live in [`docs/blueprint/`](docs/blueprint/) and [`specs/001-shima-dynamic-island/`](specs/001-shima-dynamic-island/).

## Building from Source

### Prerequisites
- Windows 10 (version 2004 / build 19041+) or Windows 11 (x64 or ARM64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) with the Windows Desktop workload
- Windows 10/11 SDK `10.0.22621.0` or newer (`MakeAppx.exe`, `SignTool.exe`, `MakePri.exe`)
- PowerShell 5.1+ or PowerShell 7+
- *(Optional)* [Inno Setup 6](https://jrsoftware.org/isinfo.php) to compile `Shima-Setup-*.exe` (`build-release.ps1` can install it automatically via `winget` when `-AutoInstallInnoSetup` is passed or when prompted interactively; GitHub Releases are also built and published automatically on `v*` tag push via [the release workflow](.github/workflows/release.yml))

### Build, Test & Run
```powershell
dotnet build Shima.sln -c Release
dotnet test Shima.sln -c Release
dotnet run --project src/Shima.App -c Release
```

### Package Release Artifacts
```powershell
.\build\build-release.ps1 -Sign -CreateDevSelfSignedCert
```

## Feedback & Bug Reports

Found a bug or have a widget idea? Open an issue on [GitHub Issues](https://github.com/chrstn-922/shima/issues). When reporting a bug, attach a sanitized log bundle via **Shima Settings → General → Diagnostic logs (sanitized) → Export bundle** to help speed up investigation.

## License

Shima is licensed under the [MIT License](LICENSE).
