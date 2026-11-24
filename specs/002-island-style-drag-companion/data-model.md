# Data Model Extension: Island Style & Custom Positioning

**Feature**: `002-island-style-drag-companion` | **Date**: `2026-11-24`

## 1. `settings.json` (`AppPreferences` & `AppPreferencesSnapshot`) Extensions

| Field | Type | Default | Validation / Normalization Rule |
| :--- | :--- | :--- | :--- |
| `IslandStyle` | `IslandStyle` enum (`DockedNotch`, `FloatingPill`) | `DockedNotch` | Serialized as case-insensitive string (`IslandStyleJsonConverter`); invalid/unknown values recover to `DockedNotch`. |
| `LockIslandPosition` | `bool` | `false` | Boolean; prevents drag repositioning when `true`. |
| `IslandOffsetXDip` | `double` | `0.0` | Horizontal DIP offset from monitor work-area horizontal center; non-finite values reset to `0.0`, runtime placement clamps safely inside `[minOffsetX, maxOffsetX]` of active monitor work area. |
| `IslandFloatingTopOffsetDip` | `double?` | `null` | Custom Floating Pill top margin in DIPs relative to `WorkArea.Top`; `null` resolves to default `12.0` DIP; non-finite values recover to `null`; normalized/clamped >= `0.0` at runtime within active monitor work area height minus pill height. |

## 2. Derived Runtime Spatial Entities (`Shima.Core.Shell.IslandPositionMath`)

- **`IslandWorkAreaPlacement`**:
  - `VisualLeftDip`, `VisualTopDip`, `VisualWidthDip`, `VisualHeightDip`: visible island capsule/notch rectangle relative to screen origin (in DIPs), guaranteed to fit inside the monitor's `WorkArea` (in DIPs).
  - `WindowLeftDip`, `WindowTopDip`, `WindowWidthDip`, `WindowHeightDip`: host `HWND` rectangle including transparent shadow and below-island companion padding (`ShadowMargin` + `BelowCompanionReserve`), aligned so `(VisualLeftDip, VisualTopDip)` coincides with `(WindowLeftDip + PadLeft, WindowTopDip + PadTop)`.
  - `PixelRect` (`MonitorBounds`): physical-pixel `SetWindowPos` rectangle rounded deterministically using `DpiScale`.
