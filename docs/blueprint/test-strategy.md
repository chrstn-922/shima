# Test & Quality Assurance Strategy: Shima

**Document Role**: Complete verification matrix across unit, integration, UIA, Windows OS interop, SQLite, security, performance, DPI/multi-monitor, fullscreen, soak stability, and `FR-PICKER-006` Color Picker tests.

---

## 1. Test Pyramid & Automation Suites

| Test Suite | Framework & Tooling | Scope & Target Coverage |
| :--- | :--- | :--- |
| **`Shima.Core.Tests`** (Unit) | `xUnit`, `FluentAssertions`, `NSubstitute` | `IslandStateManager` transitions, `OverlayPinController` priority resolution, `WidgetRegistry` circuit breaker & fault isolation, `ColorProcessingService` (`HSB↔RGB↔HEX`), WCAG 2.1 contrast math & `Restore Recommended Contrast` solver, `AppearanceEditingSession` draft isolation. |
| **`Shima.Infrastructure.Tests`** (DB & Persistence) | `xUnit` + temporary `%TEMP%\shima-test-*` SQLite/JSON sandboxes | WAL migrations (`v1 → vN`), retention pruning (`clipboard_entries`, `inbox_notifications`), consecutive SHA-256 clipboard dedup, atomic `File.Replace`, corrupt JSON backup & deterministic default recovery, DPAPI round-trip. |
| **`Shima.WindowsInterop.Tests`** (OS Adapters) | `xUnit` + Mock/Fake COM/WinRT & Live Windows Desktop Integration | `ClaudeCodeAdapter` Named Pipe SID ACL + fail-open empty-stdout exit `0` when pipe closed; `OpenCodeAdapter` loopback-only URI guard + SSE event parsing; `ClipboardMonitorAdapter` exclusion format filtering; `ZenDisplayEffectAdapter` pre-effect journal & crash restoration. |
| **`Shima.Widgets.Tests`** (Widget Modules) | `xUnit` | All 8 widget ViewModels (`Clock` TZ/DST math, `NowPlaying` null-seek fallback, `FocusTimer` phase machine, `Notes` 500ms debounce, `Inbox` app grouping & DND suppression, `Clipboard` pin/pause, `VolumeMixer` session churn debounce, `Developer` 4-status mapping). |
| **`Shima.UI.Tests`** (Automated UI / FlaUI) | `FlaUI.UIA3` + Windows Desktop Session | Real HWND `WS_EX_NOACTIVATE` focus-preservation verification, keyboard `Tab`/`Esc`/arrow traversal, Color Picker 2D field & Hue slider drags, Live Preview pinning, Low-Contrast advisory save verification. |

---

## 2. Dedicated Test Matrix for Color Picker & Appearance (`FR-PICKER-006`)

| Test Case ID | Verification Target | Assertion & Expected Result |
| :--- | :--- | :--- |
| `CP-01` | **HEX / RGB / HSB Bidirectional Sync & Validation** | `#RGB` expands to `#RRGGBB`; invalid hex reverts on blur; `RGB > 255` clamps; Hue `H` stays preserved when `S=0` or `V=0`. |
| `CP-02` | **2D Field & Hue Slider Interaction** | Dragging 2D canvas updates `S`, `B`, `RGB`, `HEX`; moving Hue `0–360°` updates 2D `WriteableBitmap` without altering `(S, B)`; keyboard `Arrow` / `Shift+Arrow` adjusts `1%` / `5%`. |
| `CP-03` | **Automatic Island Pinning & Focus Preservation** | Opening Color Picker sets `IsPinned == true`; moving pointer outside Island does NOT collapse it; Settings window keeps `GetForegroundWindow()` while dragging sliders. |
| `CP-04` | **Collapsed / Expanded Preview Mode Toggle** | Toggling preview mode inside Color Picker switches the real Island between `Collapsed` (`136×28`) and `Expanded` (`412×168`) using uncommitted `Draft` tokens (`0` disk writes). |
| `CP-05` | **Non-Blocking Low-Contrast Warning & Save** | Selecting `#111111` text on `#000000` background shows advisory contrast warning (`CR < 4.5:1`), keeps **`Apply / OK` ENABLED**, and successfully saves and persists the low-contrast combination when clicked. |
| `CP-06` | **Restore Recommended Contrast** | Clicking `Restore Recommended Contrast` raises Text `CR >= 4.5:1` and Icon `CR >= 3.0:1` while keeping `BackgroundHex` intact and updating Live Preview. |
| `CP-07` | **Cancel, Slot-Scoped Reset & Cleanup** | Picker `Reset to Default` resets only the edited slot in `Draft`; `Cancel` or `Esc` reverts Island to `CurrentCommitted`, leaves `appearance.json` untouched, releases `PinToken`, and restores normal auto-collapse. |
| `CP-08` | **Upgrade & Restart Persistence** | Applied custom presets, recent colors (max 12), and tokens survive full process restart and simulated Velopack app binary upgrade. |

---

## 3. Specialized OS, Security & Performance Verification Gates

- **Multi-Monitor & DPI Matrix**: Automated geometry assertions across `100%`, `125%`, `150%`, `200%` DPI and mixed-DPI dual-monitor arrangements (`WM_DPICHANGED`, monitor unplug failover `< 500 ms`).
- **Fullscreen & Maximized Tests**: Verify exclusive D3D fullscreen and borderless Chrome fullscreen trigger `ShowWindow(SW_HIDE)` within `500 ms`, top-edge mouse moves at `Y = 0` do not trigger the island, global hotkey restores it, and `Alt`+hover enables click-through over maximized apps.
- **Security & Privacy Audit**: Inspect active TCP/UDP sockets (`Get-NetTCPConnection`) during a full 8-widget run → assert `0` outbound connections; test password-manager clipboard exclusion flags; test Named Pipe rejection from non-owner user context.
- **Performance & Soak Stability**: 8-hour automated soak test confirming `0.0%` steady-state idle CPU, `< 50 MB` working set when collapsed, zero GDI/User handle leaks, and `>= 60 FPS` during Color Picker drags.
- **Zen Mode Crash Recovery**: Apply `MagSetFullscreenColorEffect`, execute `taskkill /F /IM Shima.App.exe`, verify display recovers automatically and `runtime-journal.json` cleans up cleanly on restart.
