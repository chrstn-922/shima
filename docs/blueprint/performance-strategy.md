# Performance Engineering Strategy: Shima

**Document Role**: Defines measurable performance targets, zero-polling guarantees, resource throttling under idle/battery conditions, and rendering optimizations for animations and the live Color Picker.

---

## 1. Measurable Resource & Latency Budgets

*All figures are original engineering targets for Shima (`NFR-PERF-001`..`004`), verified independently via BenchmarkDotNet, Windows Performance Recorder (WPR/ETW), and FlaUI automation.*

| Metric | Target Budget | Measurement Method |
| :--- | :--- | :--- |
| **Idle CPU (`Collapsed / Idle` / `Fullscreen`)** | **`0.0%` steady state** (`< 0.5%` 60s avg) | ETW CPU sampling over 10-minute collapsed desktop session. |
| **Active CPU (`Expanded` / Live Audio/Media)** | `< 2.5%` on 4-core mobile CPU | ETW profiling during media visualizer & volume mixer drag. |
| **Memory Working Set (`Collapsed`)** | **`< 50 MB`** private working set | Process memory snapshot 30s post-launch (inactive widget views unloaded). |
| **Memory Working Set (`Expanded`, 8 Widgets)** | **`< 85 MB`** private working set | After cycling all 8 widgets + Color Picker open. |
| **GPU Usage (`Collapsed / Static`)** | **`0.0%`** | Zero active WPF `CompositionTarget.Rendering` handlers or storyboards when idle. |
| **Cold Start to First Rendered Frame** | **`< 400 ms`** | Process entry timestamp → `IslandWindow.ContentRendered`. |
| **Widget Switch / Expand Latency** | **`< 16.6 ms`** first frame; `<= 220 ms` morph | Frame timing instrumentation via `CompositionTarget.Rendering`. |
| **SQLite Query Latency (Search / History Load)** | **`< 10 ms`** (p95 across 1,000 rows) | Indexed SQLite WAL queries (`idx_clipboard_copied_at`, `idx_inbox_received_at`). |
| **Color Picker Drag & Live Island Preview** | **`>= 60 FPS`** (`< 16.6 ms`/frame) | Continuous 2D field + Hue drag latency & zero disk I/O during drag. |

---

## 2. Anti-Polling & Idle Suspension Architecture

1. **Zero Polling Timers**:
   - Clipboard uses `WM_CLIPBOARDUPDATE`. Media uses GSMTC events. Audio Mixer uses WASAPI COM callbacks. Inbox uses `UserNotificationListener.NotificationChanged`. Fullscreen uses `SetWinEventHook`. Battery/Display uses `WM_POWERBROADCAST`.
   - The **only** active periodic timers in the entire system are:
     - **Clock Widget**: Single minute-aligned timer active *only* while `Expanded` on Clock; completely stopped on collapse.
     - **Focus Timer**: `1 Hz` countdown tick active *only* while a Focus session is `Running`.
     - **Now Playing Seek Interpolation**: Local `500 ms` progress tick active *only* while `Expanded` on Now Playing AND `PlaybackStatus == Playing`.
2. **View Virtualization**: `WidgetHost` renders only the active widget's visual tree; inactive widget views are detached (retaining lightweight view-model state only when background-active) and image thumbnails use `DecodePixelWidth` sizing (`<= 96 px`) to minimize bitmap memory.
3. **Battery & Display-Off Governor (`IPowerAndMotionMonitor`)**:
   - On `GUID_CONSOLE_DISPLAY_STATE == Off` or Session Lock: transitions to `SystemSuspended` and freezes all UI updates.
   - On `GUID_POWER_SAVING_STATUS == On` (Windows Energy Saver): disables decorative glow pulses, pauses audio visualizers, and switches island morphs to minimal/instant transitions.

---

## 3. Color Picker & Live Preview Rendering Optimization

- **2D Saturation/Brightness Canvas (`WriteableBitmap`)**:
  - Pre-allocates a single reusable `256×256` `Bgra32` byte buffer (`262 KB`) and `WriteableBitmap` once when the dialog opens (zero GC allocations during dragging).
  - Recomputes pixels *only* when **Hue (`H`)** changes (`< 0.6 ms` tight integer/float scanline loop per hue change), not when dragging `(S, B)` within the same hue!
- **Coalesced Live Island Preview**:
  - Pointer move events (`120–240 Hz` gaming mice) mutate the in-memory `AppearanceEditingSession.Draft` synchronously, while visual brush updates on the Dynamic Island are coalesced to `1` pass per VSync (`DispatcherPriority.Render` / dirty flag).
  - **Zero Persistence I/O During Preview**: `appearance.json` and `presets.json` are NEVER touched until the user explicitly clicks **Apply / OK**.
