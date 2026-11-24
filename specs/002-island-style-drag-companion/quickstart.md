# Quickstart Validation: Island Style, Movable Positioning & Below-Island Companion

**Feature**: `002-island-style-drag-companion`

## Build & Full Automated Suite

```powershell
dotnet build Shima.sln -c Release
dotnet test Shima.sln -c Release
```

## Covered Validation Scenarios

1. **Pure Math & Work-Area Clamping (`Shima.Core.Tests`)**: Multi-monitor positive/negative coordinates, Per-Monitor DPI `1.0x–3.0x` work-area clamping for both compact and expanded states, Floating vs Docked bounds, and `16` DIP magnetic snap resolution.
2. **Preferences Persistence (`Shima.Infrastructure.Tests`)**: JSON round-trip & normalization of `IslandStyle`, `LockIslandPosition`, `IslandOffsetXDip`, `IslandFloatingTopOffsetDip`, invalid/NaN recovery, and atomic `ResetToDefaultsAsync`.
3. **Overlay Positioning (`Shima.WindowsInterop.Tests`)**: `OverlayWindowAdapter.SetPixelRect` / `PositionOnMonitor` non-activating `SetWindowPos` and cursor query isolation.
4. **Live Shell & UIA (`Shima.UI.Tests`)**:
   - Live Settings style switch (`DockedNotch` <-> `FloatingPill`), persistence reload, contour/ear removal, shadow & hover lift, Reduced Motion instant snap.
   - Drag past threshold in Docked (X-only) and Floating (X/Y), click-below-threshold expand preservation, interactive Slider/TextBox drag suppression, `LockPosition` block, `ResetPosition` glide to center, `16` DIP magnetic snap, and edge/bottom expand-into-bounds clamping without teleport.
   - Below-island companion pill coexistence with Now Playing (dot preserved, centered `4–6` DIP gap, zero extra text, `Working` sweep -> `Finished` settle, hide on expand, urgent takeover priority, slide animations & Reduced Motion snap).
   - Hit-test & `WM_NCHITTEST` `HTTRANSPARENT` verification around Floating capsule, vertical gap, and lateral sub-pill flanks across DPI scales.
