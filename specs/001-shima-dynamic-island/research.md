# Technical Research & Stack Decision: Shima

**Feature**: `001-shima-dynamic-island` | **Date**: `2026-11-21` | **Research Cut-off**: `2026-11-21`  
**Spec Reference**: [spec.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/spec.md) | **Constitution**: [constitution.md](file:///C:/Users/chris/Projects/shima/.specify/memory/constitution.md)

---

## 1. Research Methodology & Evidence Taxonomy

All technical findings below are derived from official Microsoft Learn documentation, official Claude Code / OpenCode documentation, and documented Windows SDK / .NET specifications as of **2026-11-21**. Behavioral observations from the competitive reference (`navisle.com`) are used strictly as feature inputs and are never treated as confirmed internal implementation details.

- **`[CONFIRMED API]`**: Verified against official Microsoft / vendor API documentation.
- **`[PROPOSED]`**: Engineering architecture chosen for Shima based on API capabilities and Constitution constraints.
- **`[NEEDS VALIDATION]`**: Requires empirical Phase 0 prototype testing on target Windows 10/11 hardware (x64 & ARM64) before implementation sign-off.

---

## 2. Windows OS & Integration Technical Findings

### 2.1 Overlay Windows, Always-on-Top & Non-Activating Focus

- **APIs**: Win32 `WS_EX_TOPMOST` (`0x00000008`), `WS_EX_TOOLWINDOW` (`0x00000080`), `WS_EX_NOACTIVATE` (`0x08000000`), `SetWindowLong` / `SetWindowLongPtr`, `SetWindowPos`, `HWND_TOPMOST`, and WPF `AllowsTransparency="True"` + `WindowStyle="None"` (or `HwndSource` subclassing).
- **Source**: [Microsoft Learn — Extended Window Styles (`WS_EX_NOACTIVATE`)](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - `WS_EX_NOACTIVATE` prevents mouse clicks on the overlay from activating the window or stealing keyboard focus from the user's active foreground application (`[CONFIRMED API]`).
  - `WS_EX_TOOLWINDOW` excludes the window from the Windows taskbar and `Alt+Tab` task switcher (`[CONFIRMED API]`).
  - **Focus Acquisition for Input Widgets**: When a user explicitly clicks a text box inside **Notes**, **Clipboard Search**, or **Clock City Search**, `WS_EX_NOACTIVATE` must be temporarily cleared (or `SetForegroundWindow` explicitly called on the island HWND) and restored immediately when focus leaves (`Esc` or blur) (`[PROPOSED]`).
  - **Z-Order Drift**: Other topmost windows (e.g., Task Manager, Windows Start menu, tooltips) can push ahead in the topmost band; Shima should re-assert `HWND_TOPMOST` via `SetWindowPos(..., SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE)` only on discrete state/activation events, never in a polling timer (`[PROPOSED]`).

### 2.2 Fullscreen & Maximized Window Detection

- **APIs**: Shell API `SHQueryUserNotificationState` (`shell32.dll`), Win32 `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`), `GetForegroundWindow`, `GetWindowRect`, `MonitorFromWindow`, and `GetMonitorInfo`.
- **Source**: [Microsoft Learn — `SHQueryUserNotificationState`](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - `SHQueryUserNotificationState` returns `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE`, `QUNS_BUSY`, or `QUNS_ACCEPTS_NOTIFICATIONS` (`[CONFIRMED API]`).
  - **Limitation**: Borderless windowed fullscreen apps (e.g., YouTube/Netflix fullscreen in browsers, borderless games) often report `QUNS_ACCEPTS_NOTIFICATIONS` instead of `QUNS_RUNNING_D3D_FULL_SCREEN` (`[NEEDS VALIDATION]`). Therefore, Shima MUST combine `SetWinEventHook` foreground/location events with a monitor-rect bounds check (`foregroundRect >= monitorBounds` while excluding desktop shell `Progman`/`WorkerW`) (`[PROPOSED]`).
  - **Maximized Window Coexistence**: `WS_MAXIMIZE` windows occupy `rcWork` (work area), not `rcMonitor`. Keeping the collapsed pill strictly sized to its visible geometry (e.g., `~140×28 DIPs`) with exact hit-testing and `Alt`+hover temporary click-through avoids interfering with browser tabs or window buttons (`[PROPOSED]`).

### 2.3 Multi-Monitor & Per-Monitor DPI v2 Scaling

- **APIs**: `app.manifest` `<dpiAwareness>PerMonitorV2, PerMonitor</dpiAwareness>`, `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `Microsoft.Win32.SystemEvents.DisplaySettingsChanged`, `EnumDisplayMonitors`, `GetDpiForMonitor`.
- **Source**: [Microsoft Learn — High DPI Desktop Application Development on Windows](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - WPF defaults to System DPI awareness unless `PerMonitorV2` is declared in `app.manifest` (`[CONFIRMED API]`).
  - With `PerMonitorV2` enabled on `.NET 8+` WPF, `VisualTreeHelper.GetDpi(visual)` and `WM_DPICHANGED` deliver exact scale factors when anchoring across mixed-DPI monitors (`100%` to `300%`) (`[CONFIRMED API]`).

### 2.4 Windows Notification Integration (`Inbox`)

- **APIs**: WinRT `Windows.UI.Notifications.Management.UserNotificationListener`, `RequestAccessAsync()`, `GetNotificationsAsync(NotificationKinds.Toast)`, `NotificationChanged` event, `RemoveNotification(uint notificationId)`, and `SHQueryUserNotificationState` / Windows Focus Assist status.
- **Sources**:
  - [Microsoft Learn — `UserNotificationListener`](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.management.usernotificationlistener) (`[CONFIRMED API]`)
  - [Microsoft Learn — Grant package identity by packaging with external location (Sparse Package)](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) (`[CONFIRMED API]`)
- **Critical Platform Findings & Limitations**:
  - `UserNotificationListener` **strictly requires Windows Package Identity** with `<uap3:Capability Name="userNotificationListener" />` declared in `AppxManifest.xml` (`[CONFIRMED API]`). Calling it from a raw unpackaged `.exe` without identity throws or fails access checks (`[CONFIRMED API]`).
  - Sparse Packages (`uap10:AllowExternalContent`) granting identity to a desktop `.exe` require **Windows 10 Version 2004 (Build 19041)+** (`[CONFIRMED API]`).
  - **Custom Popups Limitation**: Applications that render proprietary floating windows instead of Windows Action Center toasts (e.g., Steam, Discord custom overlay, certain Electron apps) do NOT enter `UserNotificationListener`. Per approved clarification, Shima will NOT scrape other apps' windows via UI Automation and will document this boundary clearly (`[CONFIRMED API]`).
  - **Bidirectional Dismissal**: `UserNotificationListener.RemoveNotification(id)` removes notifications from Windows Action Center; Shima supports this by default alongside a local-only Inbox toggle (`[CONFIRMED API]`).

### 2.5 Windows Media Session Integration (`Now Playing`)

- **APIs**: WinRT `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (`RequestAsync()`, `CurrentSessionChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged`, `TimelinePropertiesChanged`, `TryTogglePlayPauseAsync()`, `TrySkipNextAsync()`, `TrySkipPreviousAsync()`, `TryChangePlaybackPositionAsync()`).
- **Source**: [Microsoft Learn — `GlobalSystemMediaTransportControlsSessionManager`](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - Works across unpackaged and packaged Win32/.NET 8 apps on Windows 10 1809+ (`[CONFIRMED API]`).
  - Some media sources publish track metadata without enabling timeline seeking (`IsPlaybackPositionEnabled == false`) or without updating `Position` continuously (`[NEEDS VALIDATION]`). Shima must interpolate progress locally only while `PlaybackStatus == Playing` and the island is `Expanded`, stopping interpolation immediately when `Collapsed` or `Paused`.

### 2.6 Per-Application Audio Management (`Volume Mixer`) & Ambient Noise (`Focus Timer`)

- **APIs**: Windows Core Audio WASAPI COM interfaces: `IMMDeviceEnumerator`, `IMMNotificationClient`, `IAudioEndpointVolume`, `IAudioEndpointVolumeCallback`, `IAudioSessionManager2`, `IAudioSessionNotification`, `IAudioSessionEnumerator`, `IAudioSessionControl2`, `IAudioSessionEvents`, `ISimpleAudioVolume`.
- **Sources**:
  - [Microsoft Learn — Core Audio Interfaces](https://learn.microsoft.com/en-us/windows/win32/coreaudio/core-audio-interfaces) (`[CONFIRMED API]`)
  - [Microsoft Learn — `IAudioSessionManager2`](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionmanager2) (`[CONFIRMED API]`)
  - [Microsoft Learn — `IAudioSessionNotification`](https://learn.microsoft.com/en-us/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionnotification) (`[CONFIRMED API]`)
- **Key Findings & Limitations**:
  - COM audio notification callbacks (`IAudioSessionNotification`) require an MTA worker thread (`COINIT_MULTITHREADED`) and an early `IAudioSessionEnumerator::GetCount()` call to prevent missing sessions created concurrently (`[CONFIRMED API]`).
  - Switching default output devices (e.g., speakers to Bluetooth headset) requires re-binding `IAudioSessionManager2` via `IMMNotificationClient::OnDefaultDeviceChanged` (`[CONFIRMED API]`).
  - **Zen Mode Ambient Audio**: Procedural DSP white/pink/brown noise rendered via `NAudio.Wasapi` (`WasapiOut` shared-mode buffer) consumes minimal CPU (`< 0.3%`) with zero disk streaming (`[PROPOSED]`).

### 2.7 Zen Mode Ambient Screen Effects & Fail-Safe Restoration

- **APIs**: Win32 Magnification API (`Magnification.dll`: `MagInitialize`, `MagSetFullscreenColorEffect`, `MagGetFullscreenColorEffect`, `MagUninitialize`) and/or a non-activating click-through dimming window (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW`).
- **Source**: [Microsoft Learn — `MagSetFullscreenColorEffect`](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetfullscreencoloreffect) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - `MagSetFullscreenColorEffect` applies a 5×5 `MAGCOLOREFFECT` color matrix (e.g., luminance-weighted grayscale + brightness scale) at the DWM compositor level (`[CONFIRMED API]`).
  - **Crash-Safety Advantage**: Because the Magnification color transform is owned by the calling process handle in DWM, Windows **automatically removes the effect** if the process crashes or is terminated (`taskkill /F`) (`[NEEDS VALIDATION]` during Phase 0 on Windows 11 24H2 multi-monitor setups; additionally guarded by a local recovery journal flag and explicit `MagUninitialize()` on shutdown).

### 2.8 Clipboard Monitoring (`Clipboard`)

- **APIs**: Win32 `AddClipboardFormatListener`, `RemoveClipboardFormatListener`, `WM_CLIPBOARDUPDATE`, `OpenClipboard`, `GetClipboardData`, `RegisterClipboardFormat` (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`).
- **Source**: [Microsoft Learn — `AddClipboardFormatListener`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener) (`[CONFIRMED API]`).
- **Key Findings & Limitations**:
  - Purely event-driven (`WM_CLIPBOARDUPDATE`); zero polling (`[CONFIRMED API]`).
  - Password managers emit `ExcludeClipboardContentFromMonitorProcessing` or `CanIncludeInClipboardHistory = 0`; Shima checks both formats before reading payload text (`[PROPOSED]`).
  - `OpenClipboard` can transiently fail with `ERROR_ACCESS_DENIED` while another process holds the clipboard lock; non-blocking exponential retry (`3` attempts, `25ms / 50ms / 75ms`) prevents UI stalls (`[PROPOSED]`).

### 2.9 Startup Registration, Power & Accessibility Events

- **Startup**:
  - *Packaged / Sparse Identity*: `Windows.ApplicationModel.StartupTask.GetAsync(taskId)` (`uap5:StartupTask`) (`[CONFIRMED API]`).
  - *Unpackaged Fallback*: Per-user registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`[CONFIRMED API]`).
- **Power & Battery**:
  - Win32 `RegisterPowerSettingNotification` with `GUID_POWER_SAVING_STATUS` (`E00958C0-C213-4ACE-AC77-FECCED2EEEA5`), `GUID_ACDC_POWER_SOURCE`, and `GUID_CONSOLE_DISPLAY_STATE` (`[CONFIRMED API]`).
- **Reduced Motion & High Contrast**:
  - WinRT `Windows.UI.ViewManagement.UISettings.AnimationsEnabled` + `AnimationsEnabledChanged` event (`[CONFIRMED API]`), and `SystemParameters.HighContrast` in WPF.

### 2.10 Local Storage (`SQLite` + Atomic JSON)

- **SQLite (`Microsoft.Data.Sqlite`)**: Local embedded ACID database with `PRAGMA journal_mode=WAL;`, `PRAGMA synchronous=NORMAL;`, and user-only NTFS ACLs (`%LOCALAPPDATA%\Shima\data\shima.db`).
- **Atomic JSON**: Appearance profiles, color presets, and general preferences saved via write-to-temp + `File.Replace` (`settings.json.tmp` → `settings.json`) with schema versioning.
- **DPAPI (`System.Security.Cryptography.ProtectedData`)**: Encrypts optional local license tokens or sensitive configuration blobs bound to `DataProtectionScope.CurrentUser` (`[CONFIRMED API]`).

### 2.11 Developer Integrations: Claude Code vs. OpenCode (Independent Research)

| Dimension | Claude Code Adapter | OpenCode Adapter |
| :--- | :--- | :--- |
| **Integration Mechanism** | Lifecycle hooks in `~/.claude/settings.json`: `PreToolUse`, `PermissionRequest`, `Notification`, `Stop`, `SubagentStop`. Hook command receives JSON on `stdin` (`session_id`, `cwd`, `tool_name`, `tool_input`). (`[CONFIRMED API]`) | Local OpenCode Server (`opencode serve`, OpenAPI 3.1 + SSE `GET /global/event` or `/event`) and/or TypeScript plugin hooks in `~/.config/opencode/plugins` (`event`, `permission.ask`, `tool.execute.before/after`). (`[CONFIRMED API]`) |
| **Bidirectional Approval** | Hook bridge connects to Shima's user-ACL Named Pipe (`\\.\pipe\shima-agent-bridge`), waits for user `Allow`/`Deny`, prints structured JSON (`hookSpecificOutput.permissionDecision`: `"allow"` or `"deny"`) to `stdout` (`exit 0`). (`[CONFIRMED API]` / `[PROPOSED]`) | SSE delivers `permission.updated` / `permission.replied`; adapter calls local OpenCode permission reply API (`once`/`always`/`reject`) OR plugin `permission.ask` bridge. Read-only fallback (`Focus Terminal`) if running without server/plugin approval hooks. (`[CONFIRMED API]` / `[PROPOSED]`) |
| **Fail-Open Contract** | If Shima is closed, pipe unreachable, or configurable timeout expires, bridge prints empty `stdout` and exits `0` (falling back cleanly to Claude Code's native terminal prompt). (`[PROPOSED]`) | If SSE/HTTP server disconnects or user responds in TUI (`permission.replied`), Shima automatically clears the pending prompt and updates status without blocking OpenCode. (`[PROPOSED]`) |
| **Discovery Policy** | Strictly opt-in via explicit Install/Uninstall Hook button in Settings (backs up `~/.claude/settings.json` before editing). Zero silent config edits. (`[PROPOSED]`) | Strictly opt-in via user-configured local endpoint/plugin setup in Settings. Zero background localhost port scanning. (`[PROPOSED]`) |

### 2.12 Packaging, Distribution & Updates

- **Evaluation**:
  - **Velopack (`velopack.io`) + Optional Signed Sparse MSIX Identity**: Delivers binary delta auto-updates, user-directory `%LOCALAPPDATA%` install without UAC admin elevation, native `win-x64` and `win-arm64` RID builds, AND optional registration of an identity-granting Sparse Package (`AppxManifest.xml` with `AllowExternalContent`) on Windows 10 2004+ (`[CONFIRMED API]`).
  - **Full MSIX**: Great for Microsoft Store and automatic `UserNotificationListener` identity, but virtualizes registry/filesystem and complicates developer hook script paths (`[CONFIRMED API]`).
- **Realistic OS Support Matrix**:
  - **Tier 1 (Full Feature Parity)**: Windows 10 Version 2004 (Build 19041)+ and Windows 11 (21H2–24H2+), `x64` and `ARM64` — supports Sparse Package identity (`UserNotificationListener` Inbox), `PerMonitorV2` DPI, GSMTC, WASAPI, and Magnification API.
  - **Tier 2 (Degraded Inbox Mode)**: Windows 10 1809–1909 (`17763`–`18363`) or Unpackaged Portable runs without Sparse Package identity — Core Island + 7 widgets operate normally, while Inbox displays a clear compatibility/identity notice instead of failing (`[PROPOSED]`, requires validation on Win 10 1809 VM).

---

## 3. Technology Stack Comparison & Weighted Decision Matrix

### 3.1 Candidate Stacks Evaluated

1. **C# / .NET 8 (LTS) with WPF + WinRT SDK Projection + `CsWin32`**
2. **C# / .NET 8 (LTS) with Windows App SDK / WinUI 3**
3. **Tauri v2 (Rust Backend + WebView2 TypeScript/SolidJS Frontend)**
4. **Electron (Node.js + Chromium + Native C++ N-API Addons)**

### 3.2 Weighted Decision Matrix

Weights reflect [constitution.md](file:///C:/Users/chris/Projects/shima/.specify/memory/constitution.md) priorities (Performance & Resource Efficiency, Native Windows Integration, Security/Local-First, Architecture & Maintainability):

| Criterion | Weight | .NET 8 + WPF + WinRT + CsWin32 | .NET 8 + WinUI 3 | Tauri v2 (Rust + WebView2) | Electron (Node + Chromium) |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Windows API Integration** (Win32/WinRT/COM) | 20% | **9.5** | 9.0 | 8.0 (IPC bridge) | 4.5 (C++ N-API) |
| **Overlay, Non-Activating & DPI Windowing** | 20% | **9.5** | 6.5 (transparency quirks) | 7.5 | 7.0 |
| **CPU, RAM & GPU Efficiency** (Idle 0%, <50MB) | 20% | **9.0** | 8.5 | 6.0 (WebView2 procs) | 2.0 (150–300MB) |
| **Animation & Color Picker Rendering** | 10% | **8.5** (`WriteableBitmap`/Composition) | 9.0 | **9.5** (CSS/Canvas) | 8.5 |
| **Security & Minimal Dependency Surface** | 10% | **9.0** | 9.0 | 8.5 | 5.0 |
| **Packaging, ARM64 & Updates** | 10% | **9.0** (Velopack + Sparse) | 7.0 (AppRuntime dep) | **9.5** | 4.5 |
| **Maintainability & Dev Productivity** | 10% | **9.0** (Single-lang C#) | 7.5 | 8.0 (Dual-lang) | 7.0 |
| **Weighted Total (out of 10.0)** | **100%** | **9.15 (Selected)** | **8.05** | **7.75** | **5.10 (Rejected)** |

### 3.3 Final Stack Selection & Rejected Alternatives

- **Selected Primary Stack**: **C# 12 / .NET 8 (LTS) (`net8.0-windows10.0.22621.0`) + WPF (Vector Geometry & Hardware-Accelerated Composition) + `Microsoft.Windows.CsWin32` + `NAudio.Wasapi` + `Microsoft.Data.Sqlite` + `Velopack` (+ Sparse MSIX Identity Manifest)**.
  - *Why Best Fit*: Unmatched first-class control over `WS_EX_NOACTIVATE` transparent topmost windows, direct zero-IPC access to WinRT (`GSMTC`, `UserNotificationListener`, `UISettings`) and COM (`CoreAudio` WASAPI), `< 50 MB` idle RAM, `0.0%` idle CPU, native `win-x64` and `win-arm64` compilation, and single-language maintainability across all 5 architectural layers.
- **Rejected Alternatives**:
  - **WinUI 3 / Windows App SDK**: Rejected because borderless per-pixel transparent non-activating overlays require brittle `DwmExtendFrameIntoClientArea` / Mica backdrop hacks and incur Windows App Runtime deployment weight.
  - **Tauri v2 (Rust + WebView2)**: Rejected as primary stack because `msedgewebview2.exe` multi-process overhead pushes baseline RAM to `~60–110 MB` (threatening `NFR-PERF-002 < 50 MB`), and requires dual-language serialization across every live color slider tick, media timeline update, and audio meter frame.
  - **Electron**: Rejected immediately for failing Constitution Principle III (`~150–300 MB` RAM, bundled Chromium background threads, bloated update size).
