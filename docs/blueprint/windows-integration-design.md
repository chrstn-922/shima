# Windows Operating System Integration & Visibility Design

**Document Role**: Detailed engineering design for every Windows OS boundary in `Shima.WindowsInterop`, including the Overlay Pinning & Visibility Management arbiter.  
**Contracts**: [windows-adapters-contract.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/contracts/windows-adapters-contract.md) | **Research**: [research.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/research.md)

---

## 1. Floating Overlay Windowing & Focus Preservation

### 1.1 Non-Activating Topmost Window Mechanics

- **Extended Styles Applied on `SourceInitialized`**:
  - `WS_EX_TOPMOST`: Keeps the island above normal and maximized desktop windows (`WS_MAXIMIZE`).
  - `WS_EX_TOOLWINDOW`: Excludes Shima from `Alt+Tab` and taskbar button strips.
  - `WS_EX_NOACTIVATE`: Prevents clicking buttons, playback controls, volume sliders, or world cities from stealing focus away from the user's active IDE, browser, or game.
- **Controlled Input-Focus Handoff**:
  - When the user clicks a text entry field (`Notes` scratchpad, `Clipboard` search, `Clock` city search), `FocusActivationHandler` temporarily clears `WS_EX_NOACTIVATE` and calls `SetForegroundWindow(islandHwnd)` while remembering `previousForegroundHwnd`.
  - When the user presses `Esc` or blurs the input, `WS_EX_NOACTIVATE` is immediately restored and focus returns cleanly without jarring window flashing.
- **Maximized Window Coexistence (`FR-SHELL-008`)**:
  - The collapsed window HWND is sized strictly to the compact pill (`136 × 28 DIP`)—never a full-width invisible strip—so top-row browser tabs and window caption buttons (`_`, `□`, `×`) remain 100% unobstructed.
  - Holding `Alt` + hovering the collapsed pill activates `WS_EX_TRANSPARENT` click-through and fades opacity to `25%` for `3 seconds` so even top-centered title-bar search bars (e.g., VS Code) remain reachable.

---

## 2. Overlay Pinning & Visibility Management (`OverlayPinController`)

To guarantee deterministic resolution when competing events occur (e.g., user opens Color Picker while fullscreen detection, mouse-leave auto-collapse, or notification peeks are active), visibility is governed by a strict **Priority State Arbiter**:

```text
Priority Order (Highest → Lowest):
1. SystemSuspended (Display Off / Windows Sleep / Session Lock)  → Hide Window & Suspend All Timers
2. ExplicitPin (Color Picker Open / Critical Modal Workflow)     → Force Visible & Immune to Mouse-Leave Collapse
3. FullscreenHardHide (Fullscreen Game/Video on Host Monitor)    → Hide Window & Disable Y=0 Hit-Box
4. GlobalHotkeySummon / Opt-In Critical Breakthrough             → Temporarily Visible Over Fullscreen
5. UserExpanded (Click / Hover / Keyboard Focus in Island)       → Expanded State
6. NotificationPeek (Incoming Toast / Agent Alert TTL)           → Notification State
7. CompactActive (Playing Media / Active Focus Timer / Agent)    → Active Pill State
8. CollapsedIdle (Default Resting State)                         → Collapsed Pill (0% CPU)
```

### 2.1 Color Picker Pinning Workflow (`FR-PICKER-006`)

1. **Pin Acquisition**: Opening the Advanced Color Picker calls `OverlayPinController.AcquirePin(PinReason.ColorPickerPreview, initialPreviewMode)` returning a disposable `PinToken`.
2. **Mouse-Leave Suppression**: While `IsPinned == true`, `IslandStateManager` ignores mouse-leave auto-collapse timers so the user can freely move their cursor inside the Settings / Color Picker window while inspecting the live top-center Dynamic Island.
3. **Focus Isolation**: The pinned Island keeps `WS_EX_NOACTIVATE` enforced so live preview updates and Collapsed/Expanded mode switches never steal keyboard focus or interrupt slider dragging inside the Settings window.
4. **Conflict Resolution**:
   - If the user moves Settings next to a maximized window, the pinned island stays visible at the top-center of its target monitor.
   - If the Settings window itself were maximized/fullscreen, `ExplicitPin` takes precedence over `FullscreenHardHide` while the Color Picker dialog is active, unless `SystemSuspended` (PC lock/display off) occurs.
5. **Guaranteed Restoration**: Closing the Color Picker (via `Apply / OK`, `Cancel`, `Esc`, dialog close, or unhandled exception `finally` block) disposes `PinToken`, immediately re-evaluates `IslandStateManager`, restores normal auto-collapse behavior, and collapses the island if the cursor is outside its bounds.

---

## 3. Multi-Monitor, Per-Monitor DPI v2 & Fullscreen Detection

- **Multi-Monitor Anchoring**:
  - Supports three user modes in `General Settings`: `Primary Monitor`, `Monitor Under Active Cursor` (at trigger time), or `Pinned Specific Monitor (DeviceId)`.
  - Hooks `WM_DISPLAYCHANGE` and `SystemEvents.DisplaySettingsChanged`. If the host monitor is disconnected, Shima re-anchors to the new primary monitor within `< 500 ms`.
- **Per-Monitor DPI v2**:
  - `app.manifest` declares `PerMonitorV2`. `WM_DPICHANGED` updates WPF scale transforms and recalculates exact physical pixels (`SetWindowPos` using `SWP_NOACTIVATE`) anchored to `(MonitorBounds.Left + MonitorBounds.Width / 2 - PixelWidth / 2, MonitorBounds.Top)`.
- **Fullscreen Detection (`FR-SHELL-007`)**:
  - Driven exclusively by `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`) + `SHQueryUserNotificationState` — zero polling timers.
  - Triggers `FullscreenHardHide` ONLY for the monitor containing the fullscreen window (so fullscreen video on Monitor 2 does NOT hide Shima if Shima is anchored on Monitor 1).
  - Completely hides the overlay HWND (`ShowWindow(SW_HIDE)`) so **zero `Y = 0` hover hit-box remains** during fullscreen games/video.
  - Standard notifications queue silently to `Inbox`; users summon the island via the configured global hotkey (`RegisterHotKey`), and an optional toggle lets Focus Timer completions / Coding-Agent approvals peek over borderless fullscreen windows.

---

## 4. OS Subsystem Integrations Summary

| Subsystem | Windows APIs | Permissions / Identity | Failure & Fallback Behavior |
| :--- | :--- | :--- | :--- |
| **Notifications (`Inbox`)** | `UserNotificationListener` (`NotificationChanged`, `RemoveNotification`) | Requires **Sparse MSIX Package Identity** (`userNotificationListener` capability) + user OS consent. | If `IdentityUnavailable` or `DeniedByUserOrPolicy`, Inbox renders an inline guidance card with a Windows Settings deep-link (`ms-settings:privacy-notifications`). Never scrapes other apps' UI. |
| **Media (`Now Playing`)** | `GlobalSystemMediaTransportControlsSessionManager` | None (standard desktop user). | Disables seek slider when `IsPlaybackPositionEnabled == false`; shows clean themed placeholder when no session is active. |
| **Audio (`Volume Mixer`)** | CoreAudio WASAPI (`IMMDeviceEnumerator`, `IAudioSessionManager2`, `ISimpleAudioVolume`) | None; runs on dedicated MTA COM thread. | Auto-rebinds on `OnDefaultDeviceChanged`; debounces transient system sound sessions (`150 ms`). |
| **Clipboard** | `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE` | None. | Retries `OpenClipboard` (3× backoff); ignores password-manager private formats; suppresses self-writes during one-click recopy. |
| **Zen Display Filter** | `Magnification.dll` (`MagSetFullscreenColorEffect`) + click-through dim overlay | None; DWM process-scoped. | Writes `runtime-journal.json` pre-effect; DWM auto-resets on crash; startup watchdog guarantees cleanup; auto-suspends on fullscreen games. |
| **Startup & Power** | `StartupTask` / `HKCU\...\Run` & `RegisterPowerSettingNotification` | Standard user (no UAC admin). | Falls back to `HKCU\...\Run` when unpackaged; pauses animations/ticks on `GUID_POWER_SAVING_STATUS` & display off. |
