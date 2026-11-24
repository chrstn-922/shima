# Contract: Shell Geometry, Positioning, Hit-Testing & Companion Placement

**Feature**: `002-island-style-drag-companion`

## 1. Visual Dimensions & Constants

- **Docked Notch (`floatingProgress = 0.0`)**:
  - `Collapsed`: `136 x 28` DIP (`EarRadius = 12`, `BottomRadius = 14`), top flush `WorkArea.Top`.
  - `Active`: `264 x 34` DIP (`EarRadius = 12`, `BottomRadius = 16`), top flush `WorkArea.Top`.
  - `Notification`: `340 x 76` DIP (`EarRadius = 14`, `BottomRadius = 20`), top flush `WorkArea.Top`.
  - `Expanded`: `444 x 176` DIP (`EarRadius = 16`, `BottomRadius = 24`), top flush `WorkArea.Top`.
- **Floating Pill (`floatingProgress = 1.0`)**:
  - Capsule width equals body width (`spec.WidthDip - 2 * spec.EarRadiusDip`), zero top concave ears, default top offset `12.0` DIP below `WorkArea.Top`, 4-corner rounding (`Height/2` on `Collapsed` [`14`] & `Active` [`17`]; `BottomRadius` on `Notification` [`20`] & `Expanded` [`24`]), full perimeter `1px` `BorderHairline` stroke, and subtle drop shadow (`Opacity = 0.34` resting / `0.44` hover / `0.52` dragging).
- **Below-Island Companion Pill**:
  - Centered horizontally beneath `MainIslandSurfaceHost` when `CurrentState == Active` and `CompactCompanionWidgetId` is active (`Working` / `Finished` dwell), with `5.0` DIP vertical gap, compact pill height `16` DIP, zero extra text, green indicator dot + green `ProgressBar` with animated `Working` sweep/pulse settling cleanly on `Finished`.
  - Hidden when `CurrentState != Active` (e.g., `Expanded`, `Notification`, `Collapsed`), or when urgent Developer Activity (`AwaitingApproval`, `NeedsAttention`, `ErrorFailed`) promotes `DeveloperCompactView` into `ActiveLayer`.

## 2. Animation Timings & Reduced Motion (`IsReducedMotionEnabled`)

- **Style Toggle Morph (`DockedNotch` <-> `FloatingPill`)**: `220 ms` `CubicEase (EaseOut)` across `floatingProgress`, `topOffset`, and `shadowOpacity` (`0 ms` immediate snap when Reduced Motion).
- **Floating Hover Lift**: `160 ms` `CubicEase (EaseOut)`, `LiftScale = 1.02`, elevated shadow (`0 ms` when Reduced Motion).
- **Drag Grab / Release Lift**: `120 ms` grab (`1.03` scale + elevated shadow) -> `160 ms` release settle (`0 ms` when Reduced Motion).
- **Magnetic Snap / Edge Clamp / Reset Glide / Edge-Safe Expand Glide**: `180–220 ms` `CubicEase (EaseOut)` (`0 ms` when Reduced Motion).
- **Magnetic Center Threshold**: `<= 16.0` DIP horizontal (`offsetX -> 0.0`); `<= 16.0` DIP from default top margin when horizontally within `16.0` DIP in Floating mode (`topOffset -> 12.0`).
- **Companion Pill Entry / Exit**: `200 ms` `CubicEase (EaseOut)` slide-down + fade (`0 -> 1`) + scale (`0.92 -> 1.0`) from behind main pill; `170 ms` `CubicEase (EaseOut)` slide-up + fade (`1 -> 0`) on expand/idle (`0 ms` instant visibility/transform snap and disabled sweep storyboard when Reduced Motion).

## 3. Click-Through & Hit-Testing Invariant

- Empty areas around Floating Pill margins, the `5` DIP vertical gap above the sub-pill, and the horizontal flanks beside the narrow sub-pill MUST return hit-test miss in WPF (`VisualTreeHelper.HitTest` / `InputHitTest` / mouse handlers) and `HTTRANSPARENT (-1)` in `WM_NCHITTEST`, passing OS clicks cleanly through at `100%`, `125%`, `150%`, and `200%`+ Per-Monitor DPI v2 scales.
