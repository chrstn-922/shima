# Tasks: Island Style, Movable Positioning & Below-Island Agent Companion

**Input**: Design documents from `/specs/002-island-style-drag-companion/`
**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/shell-geometry-and-companion.md](contracts/shell-geometry-and-companion.md)

## Phase 1: Core Contracts, Math & Persistence Foundation

- [X] T001 Extend `AppPreferencesSnapshot`, `MonitorBounds`/`Overlay` contracts with `IslandStyle`, position fields (`LockIslandPosition`, `IslandOffsetXDip`, `IslandFloatingTopOffsetDip`), and cursor/pixel rect interop signatures in `src/Shima.Core/Contracts/ISettingsServices.cs` and `src/Shima.Core/Contracts/WindowsAdapterContracts.cs`
- [X] T002 Implement `IslandPositionMath` pure geometry, work-area clamping (compact & expanded), magnetic snap (`16` DIP), and no-teleport expand-glide interpolation helpers in `src/Shima.Core/Shell/IslandPositionMath.cs`
- [X] T003 [P] Extend `AppPreferences`, `IslandStyleJsonConverter`, normalization, and `AppPreferencesService` snapshot mapping in `src/Shima.Infrastructure/Configuration/AppPreferencesStore.cs` and `src/Shima.Infrastructure/Configuration/SettingsServices.cs`
- [X] T004 [P] Extend `OverlayWindowAdapter` and `IWin32OverlayOperations` with non-activating pixel-rect positioning (`SetPixelRect`) and screen cursor sampling (`TryGetCursorScreenPosition`) in `src/Shima.WindowsInterop/Overlay/OverlayWindowAdapter.cs`

---

## Phase 2: Geometry Morphing, Companion View & Shell Behavior (`US1`, `US2`, `US3`, `US4`)

- [X] T005 Implement continuous C1 cubic-Bezier morphing (`ConcaveNotchGeometryBuilder.Build` floating morph & rounded corners) in `src/Shima.Presentation/Shell/ConcaveNotchGeometryBuilder.cs`
- [X] T006 Update `DeveloperCompactCompanionView.xaml` & `.xaml.cs` to render compact green pill (no extra text), smooth `Working` bar sweep/pulse animation, smooth `Finished` settle, and Reduced Motion / hidden suspension in `src/Shima.Widgets/Shima.Widgets.Developer/Views/`
- [X] T007 Refactor `IslandWindow.xaml` to remove inline `ActiveCompanionHost`, add sibling non-hit-testable `IslandShadowPath` and behind-surface `BelowIslandCompanionHost` (5 DIP gap), and compose `MorphScaleTransform` + `LiftScaleTransform` cleanly
- [X] T008 Implement unified VSync motion coordinator, style morphing, Floating hover lift, drag threshold + interactive-control guard + drag lift, `16` DIP magnetic snap glide, edge/bottom clamped no-teleport expand glide, below-island companion slide/scale/fade transitions, Reduced Motion instant snapping, interrupted transition handoff, and `WM_NCHITTEST` `HTTRANSPARENT` click-through in `src/Shima.Presentation/Shell/IslandWindow.xaml.cs`

---

## Phase 3: Settings UI & App/Tray Integration

- [X] T009 Add **Island style**, **Lock island position**, and **Reset position** controls and live bidirectional preference sync to `src/Shima.Presentation/Settings/SettingsWindow.xaml` and `src/Shima.Presentation/Settings/SettingsWindow.xaml.cs`
- [X] T010 Wire custom drag-position persistence and unified Reset Position flow across `src/Shima.App/App.xaml.cs` and `src/Shima.App/SystemTrayService.cs`

---

## Phase 4: Comprehensive Tests & Release Verification Gate

- [X] T011 [P] Add pure clamping, DPI, Floating vs Docked, magnetic snap, and edge-expand glide math tests in `tests/Shima.Core.Tests/IslandPositionMathTests.cs`
- [X] T012 [P] Add preference persistence/normalization tests for style, lock, and position offsets in `tests/Shima.Infrastructure.Tests/AppearancePersistenceTests.cs`
- [X] T013 [P] Add `OverlayWindowAdapter` positioning & cursor sampling tests in `tests/Shima.WindowsInterop.Tests/OverlayWindowAdapterTests.cs`
- [X] T014 Update and add UI shell tests covering Docked vs Floating geometry & morphing, hover/drag lift, drag threshold vs click isolation, X-only vs free X/Y clamping, `16` DIP magnetic snap, Lock/Reset Position, below-island companion pill (Now Playing dot preserved, sweep, expand hide, urgent takeover), Reduced Motion zero-duration snap, interrupted transitions, and `WM_NCHITTEST`/WPF click-through in `tests/Shima.UI.Tests/IslandShellUiTests.cs`
- [X] T015 Execute `dotnet build Shima.sln -c Release` and `dotnet test Shima.sln -c Release`, verify 100% pass, and complete task tracking.
