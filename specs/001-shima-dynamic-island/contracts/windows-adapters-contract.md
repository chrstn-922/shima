# Contract: Windows OS Integration Adapters (`Shima.WindowsInterop`)

**Contract Scope**: Encapsulates all Win32, COM, and WinRT APIs behind clean, testable interfaces consumed by `Shima.Core` and `Shima.Widgets.*`.

---

## 1. Overlay Window, Visibility & Pinning (`IOverlayWindowAdapter` & `IOverlayPinController`)

```text
enum VisibilityPriority { FullscreenHardHide = 100, SystemSuspended = 90, ExplicitPin = 80, UserExpanded = 60, NotificationPeek = 40, CompactActive = 20, CollapsedIdle = 0 }

interface IOverlayWindowAdapter
  void ApplyNonActivatingStyles(nint hwnd)                    // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
  void SetInteractiveFocusMode(nint hwnd, bool allowFocus)   // Toggles WS_EX_NOACTIVATE only for explicit text inputs
  void SetClickThrough(nint hwnd, bool transparent)          // Toggles WS_EX_TRANSPARENT (Alt+hover pass-through)
  void AnchorToMonitor(MonitorBounds bounds, double dpiScale, Size dipSize)
  void ReassertTopmostZOrder(nint hwnd)                      // SetWindowPos SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE

interface IOverlayPinController
  PinToken AcquirePin(PinReason reason, PreviewTargetState targetState) // ColorPickerPreview | FocusAttention
  void ReleasePin(PinToken token)                            // Restores previous auto-collapse & visibility state
  bool IsPinned { get; }
```

**Conflict Resolution Invariant**:
- If `FullscreenHardHide` is active on the host monitor, normal mouse hover/notifications stay suppressed (`Collapsed/Hidden`), EXCEPT when `ExplicitPin` (Color Picker live preview while Settings window is foreground) or an opt-in `GlobalHotkey` summon / `CriticalFullscreenBreakthrough` overrides it temporarily.
- Closing Color Picker or releasing `PinToken` immediately re-evaluates `IslandStateManager` and restores normal auto-collapse behavior.

---

## 2. Display, Fullscreen & Power Monitoring (`IDisplayTopologyMonitor`, `IFullscreenDetector`, `IPowerAndMotionMonitor`)

| Interface | Events Emitted | Underlying OS Hook | Fallback |
| :--- | :--- | :--- | :--- |
| `IDisplayTopologyMonitor` | `MonitorChanged(MonitorInfo)`, `DpiChanged(double scale)` | `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `EnumDisplayMonitors` | Falls back to primary monitor work area if configured monitor disconnects. |
| `IFullscreenDetector` | `FullscreenStateChanged(nint monitorHandle, bool isFullscreen)` | `SetWinEventHook` (`EVENT_SYSTEM_FOREGROUND`, `EVENT_OBJECT_LOCATIONCHANGE`) + `SHQueryUserNotificationState` + foreground rect vs `rcMonitor` check | Excludes `Progman`/`WorkerW`; defaults to non-fullscreen if query fails. |
| `IPowerAndMotionMonitor` | `PowerProfileChanged(PowerProfile)`, `ReducedMotionChanged(bool)` | `RegisterPowerSettingNotification` (`GUID_POWER_SAVING_STATUS`, `GUID_ACDC_POWER_SOURCE`, `GUID_CONSOLE_DISPLAY_STATE`), `UISettings.AnimationsEnabledChanged` | Assumes animations enabled & AC power if WinRT call unavailable. |

---

## 3. Windows Notifications (`INotificationListenerAdapter`)

```text
enum NotificationAccessState { Allowed, DeniedByUserOrPolicy, IdentityUnavailable, UnsupportedOs }

interface INotificationListenerAdapter
  Task<NotificationAccessState> GetAccessStateAsync()
  Task<NotificationAccessState> RequestAccessAsync()
  Task<IReadOnlyList<OsNotificationDto>> GetCurrentToastSnapshotAsync()
  Task<bool> TryRemoveFromActionCenterAsync(uint osToastId)   // Bidirectional dismissal (FR-INBOX-003)
  bool IsDoNotDisturbActive()
  event EventHandler<OsNotificationEventArgs> NotificationArrived
  event EventHandler<uint> NotificationRemovedExternally
```

- **Strict Boundary**: Uses *only* `Windows.UI.Notifications.Management.UserNotificationListener`. Never scrapes third-party windows. Returns `IdentityUnavailable` gracefully when Sparse Package identity is absent.

---

## 4. Media Transport (`IMediaSessionAdapter`)

```text
interface IMediaSessionAdapter
  MediaSessionSnapshot? CurrentSession
  event EventHandler<MediaSessionSnapshot?> SessionChanged
  Task<bool> TogglePlayPauseAsync()
  Task<bool> NextTrackAsync()
  Task<bool> PreviousTrackAsync()
  Task<bool> SeekAsync(TimeSpan position)
```

- Backed by `GlobalSystemMediaTransportControlsSessionManager`. Only enables `SeekAsync` when `Controls.IsPlaybackPositionEnabled == true`.

---

## 5. Audio Sessions & Zen Synthesis (`IAudioMixerAdapter`, `IZenAudioSynthAdapter`, `IZenDisplayEffectAdapter`)

- `IAudioMixerAdapter`: Wraps CoreAudio WASAPI (`IMMDeviceEnumerator`, `IAudioSessionManager2`, `ISimpleAudioVolume`, `IAudioEndpointVolume`) on an MTA worker thread; debounces rapid transient session teardowns (`150 ms`).
- `IZenAudioSynthAdapter`: Generates local DSP white, pink, and brown noise buffers via WASAPI output without network assets.
- `IZenDisplayEffectAdapter`: Applies process-scoped `MagSetFullscreenColorEffect` grayscale matrix + click-through dimming overlay; writes `runtime-journal.json` before applying and guarantees cleanup on Stop, Dispose, AppExit, and next-startup crash recovery.

---

## 6. Clipboard & Startup (`IClipboardMonitorAdapter`, `IStartupRegistrationAdapter`)

- `IClipboardMonitorAdapter`: Subscribes via `AddClipboardFormatListener(hwnd)` (`WM_CLIPBOARDUPDATE`). Checks `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory` before reading `CF_UNICODETEXT` or `CF_DIB`. Retries `OpenClipboard` up to 3 times (`25/50/75 ms`).
- `IStartupRegistrationAdapter`: Uses `StartupTask.GetAsync` when Sparse Package identity is active, falling back to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` for unpackaged runs. Zero admin elevation required.
