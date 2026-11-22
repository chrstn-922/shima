<div align="center">

<img src="assets/branding/shima-cradled-island.svg" alt="Shima — The Cradled Island" width="112" />

# Shima

**A Windows Dynamic Island desktop companion — ~0% idle CPU, 8 modular widgets.**

[Download v1.0.0](https://github.com/chrstn-922/shima/releases/tag/v1.0.0) ·
[`Shima-Setup-1.0.0-win-x64.exe`](https://github.com/chrstn-922/shima/releases/download/v1.0.0/Shima-Setup-1.0.0-win-x64.exe) ·
[`Shima-Setup-1.0.0-win-arm64.exe`](https://github.com/chrstn-922/shima/releases/download/v1.0.0/Shima-Setup-1.0.0-win-arm64.exe)

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

1. Download **`Shima-Setup-1.0.0-win-x64.exe`** (or **`Shima-Setup-1.0.0-win-arm64.exe`** for ARM64 devices) from the [v1.0.0 release page](https://github.com/chrstn-922/shima/releases/tag/v1.0.0).
2. Run the installer. Because v1.0.0 uses a self-signed certificate, Windows SmartScreen may show *"Windows protected your PC"* — click **More info → Run anyway** to continue.
3. During setup on Windows 10 (build 19041+) or Windows 11, the installer automatically registers the Sparse MSIX identity (`Shima.Identity-<rid>.msix`) so the **Notification Inbox** connects to Windows Action Center right away.
4. Keep **Launch Shima** checked on the final screen.

> Portable `.zip` builds and standalone `.msix` identity packages are also available on the [Releases page](https://github.com/chrstn-922/shima/releases/tag/v1.0.0). Uninstalling from Windows **Installed apps** cleanly removes the app and unregisters the Sparse MSIX identity while keeping your data in `%LOCALAPPDATA%\Shima\`.

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
- *(Optional)* [Inno Setup 6](https://jrsoftware.org/isinfo.php) to compile `Shima-Setup-*.exe`

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
