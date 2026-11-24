# Implementation Plan: Island Style, Movable Positioning & Below-Island Agent Companion

**Branch**: `002-island-style-drag-companion` | **Date**: `2026-11-24` | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-island-style-drag-companion/spec.md`

## Summary

Upgrade Shima's overlay shell (`Shima.Core`, `Shima.Infrastructure`, `Shima.WindowsInterop`, `Shima.Presentation`, `Shima.Widgets.Developer`, and `Shima.App`) across three coordinated capabilities:
1. **Island Style (`DockedNotch` vs `FloatingPill`)** persisted via `AppPreferences` / `AppPreferencesSnapshot` and selectable live in General Settings, with smooth `~220 ms` `CubicEase EaseOut` morphing of top offset (`0 -> 12` DIP), 4-corner capsule rounding vs concave top ears, drop shadow opacity, plus subtle Floating Pill hover lift.
2. **Movable Island & Multi-Monitor Clamping**: `5` DIP threshold drag-to-reposition respecting interactive child controls (`ButtonBase`, `TextBoxBase`, `Slider`, `ScrollBar`, pager dots), constrained horizontally along top work-area edge in `DockedNotch` and free X/Y inside work area in `FloatingPill`, `16` DIP magnetic top-center snap, **Lock Position** and **Reset Position** settings/tray controls, grab tactile lift and smooth release glide, and synchronized no-teleport expand-into-bounds gliding across Per-Monitor DPI v2 work areas.
3. **Below-Island Agent Companion Pill**: remove inline `ActiveCompanionHost`, restore Now Playing's trailing playing dot, host `DeveloperCompactCompanionView` centered `5` DIP below the collapsed/active pill behind main Z-order with slide-down + scale (`0.92 -> 1.0`) + fade entry (`~200 ms`), `Working` green sweep/pulse settling cleanly on `Finished` dwell, slide-up + fade exit (`~170 ms`) on expand/idle/urgent takeover, full `IsReducedMotionEnabled` instant snapping, and `WM_NCHITTEST` + WPF hit-test click-through verification.

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 (`net8.0-windows10.0.22621.0`)
**Primary Dependencies**: WPF (`UseWPF`), Win32 `CsWin32` (`User32`), `System.Text.Json`, xUnit, FluentAssertions, NSubstitute, FlaUI.UIA3
**Storage**: Atomic JSON config `%LOCALAPPDATA%\Shima\config\settings.json` via `AppPreferencesStore` & `AppPreferencesService`
**Testing**: `dotnet build Shima.sln -c Release`, `dotnet test Shima.sln -c Release` across `Shima.Core.Tests`, `Shima.Infrastructure.Tests`, `Shima.WindowsInterop.Tests`, `Shima.Widgets.Tests`, `Shima.UI.Tests`
**Target Platform**: Windows 10 20H2+ & Windows 11 Per-Monitor DPI v2 (`100%`–`300%+`)
**Project Type**: Multi-project modular .NET desktop application (`src/` + `tests/`)
**Performance Goals**: `0.0%` CPU & GPU when idle/settled (detach `CompositionTarget.Rendering` and stop sweep storyboards when settled or Reduced Motion), smooth 60+ FPS morph/glide transitions
**Constraints**: Non-activating `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, zero hardcoded hex in views, strict layer boundaries (`Presentation`/`WindowsInterop`/`Infrastructure`/`Widgets` depend only on `Shima.Core`)
**Scale/Scope**: Core geometry/clamp math, preference persistence, `OverlayWindowAdapter` cursor/rect helpers, `IslandWindow` composition & drag controller, `DeveloperCompactCompanionView` sweep visual, `SettingsWindow` controls, `SystemTrayService` + `App` wiring, and unit/UI regression tests

## Constitution Check

*GATE: Checked pre-Phase 0 and re-verified post-Phase 1 design against `.specify/memory/constitution.md`.*

- **I. Architecture & Code Quality**: PASS — Positioning math (`IslandPositionMath`) and `IslandStyle` live cleanly in `Shima.Core`; Win32 `SetWindowPos` / `GetCursorPos` remain in `Shima.WindowsInterop`; shell animation & XAML visuals live in `Shima.Presentation`; companion visuals remain inside `Shima.Widgets.Developer` with no shared-component widget leakage (`ICompactCompanionAware` untouched/compatible, `IReducedMotionAware` clean abstraction if needed).
- **II. Windows Integration**: PASS — Multi-monitor `WorkArea` (`rcWork`) clamping handles taskbars, negative coordinates, and `WM_DPICHANGED`; `WM_NCHITTEST` returns `HTTRANSPARENT` cleanly outside visible pill/sub-pill shapes; `WS_EX_NOACTIVATE` preserved throughout drag and clicks.
- **III. Performance & Resource Efficiency**: PASS — `CompositionTarget.Rendering` attaches only while motion interpolators run; `DropShadowEffect` detaches (`null`) in static `DockedNotch`; sweep storyboard stops on `Finished`, hidden, `Expanded`, `DisplayOff`, or `IsReducedMotionEnabled`.
- **IV. Security & Privacy**: PASS — Preferences extend existing atomic local `settings.json`; zero network calls.
- **V. User Experience & Accessibility**: PASS — Semantic theme tokens (`SurfaceCollapsed`, `BorderHairline`, `DropShadowColor`, `AgentStatusWorkingBrush`, `AgentStatusFinishedBrush`, `AgentStatusTrackBrush`) only; sentence-case UI labels; full `IsReducedMotionEnabled` zero-duration snapping.

## Project Structure

### Documentation (this feature)

```text
specs/002-island-style-drag-companion/
├── spec.md
├── research.md
├── plan.md
├── data-model.md
├── quickstart.md
├── tasks.md
├── checklists/
│   └── requirements.md
└── contracts/
    └── shell-geometry-and-companion.md
```

### Source Code Modifications

```text
src/
├── Shima.Core/
│   ├── Contracts/ISettingsServices.cs                 # IslandStyle enum & position fields on AppPreferencesSnapshot
│   ├── Contracts/WindowsAdapterContracts.cs           # Pixel rect placement / cursor query abstractions
│   └── Shell/IslandPositionMath.cs                    # Pure DPI work-area clamping, snap, morph & no-teleport math
├── Shima.Infrastructure/
│   └── Configuration/
│       ├── AppPreferencesStore.cs                     # IslandStyle enum JSON converter, schema normalization & clamp
│       └── SettingsServices.cs                        # Snapshot <-> AppPreferences mapping
├── Shima.WindowsInterop/
│   └── Overlay/OverlayWindowAdapter.cs                # PositionWindow / SetPixelRect & GetCursorScreenPosition
├── Shima.Presentation/
│   ├── Shell/
│   │   ├── ConcaveNotchGeometryBuilder.cs             # BuildMorph continuous cubic Bezier notch <-> capsule builder
│   │   ├── IslandWindow.xaml                          # Remove ActiveCompanionHost, add IslandShadowPath & BelowIslandCompanionHost
│   │   └── IslandWindow.xaml.cs                       # Style morph, hover/drag lift, drag threshold, glide, companion, WM_NCHITTEST
│   └── Settings/
│       ├── SettingsWindow.xaml                        # Island style selector, Lock Position toggle, Reset Position button
│       └── SettingsWindow.xaml.cs                     # Bind & save style/lock/reset with live sync
├── Shima.Widgets/Shima.Widgets.Developer/
│   └── Views/
│       ├── DeveloperCompactCompanionView.xaml         # Compact green pill (no extra text) + sweep/pulse visuals
│       └── DeveloperCompactCompanionView.xaml.cs      # Working sweep animation, Finished settle & Reduced Motion stop
└── Shima.App/
    ├── App.xaml.cs                                    # Persist dragged position & wire Reset Position
    └── SystemTrayService.cs                           # Trigger full ResetPosition flow
tests/
├── Shima.Core.Tests/IslandPositionMathTests.cs
├── Shima.Infrastructure.Tests/AppearancePersistenceTests.cs
├── Shima.WindowsInterop.Tests/OverlayWindowAdapterTests.cs
└── Shima.UI.Tests/IslandShellUiTests.cs
```
