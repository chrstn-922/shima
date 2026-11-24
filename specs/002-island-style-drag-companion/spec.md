# Feature Specification: Island Style, Movable Positioning & Below-Island Agent Companion

**Feature Branch**: `002-island-style-drag-companion`

**Created**: 2026-11-24

**Status**: Draft

**Input**: User description: "Implement three connected shell updates in Shima with smooth, cohesive animations: (1) Island Style Setting (Docked Notch vs. Floating Pill) with smooth style morphing and Floating Pill hover feedback; (2) Movable Island with Docked horizontal drag, Floating free X/Y work-area drag, multi-monitor work-area clamping on compact and expanded states, persistent custom position, Lock Position and Reset Position controls, drag tactile lift, ease-out snap/clamp glide, and smooth expand-into-bounds glide; (3) Move Green Agent Companion Pill below the collapsed island keeping Now Playing layout intact, hiding sub-pill when expanded, keeping urgent agent states at full compact-pill priority, and animating slide-down/scale/fade, working sweep/pulse, and slide-up exit; plus Reduced Motion compliance, hit-testing/click-through/DPI compatibility, and comprehensive verification."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Island Style Setting & Morphing (Priority: P1)

Users can switch the Dynamic Island presentation style in General Settings between **Docked Notch** (flush against the top screen edge with top concave notch curves) and **Floating Pill** (detached slightly below the top edge with full four-corner rounding and subtle elevation shadow). Toggling the setting updates the live island immediately with a smooth morphing animation across top offset, corner rounding, notch ear collapse/expansion, and shadow elevation.

**Why this priority**: The visual form factor of the overlay shell defines how users see and anchor Shima on their desktop and forms the spatial coordinate frame for dragging and companion pill placement.

**Independent Test**: Toggle Island Style between Docked Notch and Floating Pill in Settings, restart the application to confirm persistence, hover the Floating Pill to observe gentle elevation/scale feedback, and inspect reduced-motion instant snapping.

**Acceptance Scenarios**:

1. **Given** Shima is running with the default **Docked Notch** style, **When** the user switches Island Style to **Floating Pill** in General Settings, **Then** the live island smoothly morphs over `200–240 ms` from flush top edge with concave ears into a detached top-margin capsule (`12 px` default top offset) rounded on all four corners with subtle drop elevation.
2. **Given** Floating Pill mode is active and persisted, **When** Shima restarts, **Then** the island launches directly in Floating Pill mode at the saved configuration.
3. **Given** Floating Pill mode is active with animations enabled, **When** the pointer enters and leaves the capsule, **Then** the capsule performs a subtle scale and elevation lift on hover and eases cleanly back on leave without breaking click-to-expand or hover-to-expand.

---

### User Story 2 - Movable Island with Work-Area Clamping & Position Controls (Priority: P1)

Users can click and drag the Dynamic Island to reposition it on the active display without interfering with click-to-expand, widget switching, or inner widget controls. In **Docked Notch** mode, dragging is constrained horizontally along the top edge. In **Floating Pill** mode, users can drag freely along X and Y within the monitor's work area. Position persists across restarts, and General Settings provides **Lock Position** and **Reset Position** actions.

**Why this priority**: Desktop setups with webcams, dual taskbars, or centered menu bars require flexible horizontal or floating placement while guaranteeing the expanded panel never clips off-screen or overlaps taskbars.

**Independent Test**: Drag the island past the drag threshold in both Docked Notch and Floating Pill modes, drop near top-center (`<= 16 px` threshold) to verify magnetic snap, drop near edges/bottom and trigger expansion to verify smooth glide into safe work-area bounds, toggle Lock Position, click Reset Position, and verify click-through around the capsule.

**Acceptance Scenarios**:

1. **Given** **Lock Position** is disabled and the user clicks and releases below the drag threshold on the collapsed/active pill, **When** the pointer is released, **Then** the island expands normally without changing its saved position.
2. **Given** **Docked Notch** mode is active, **When** the user drags horizontally along the top edge and releases within `16 px` of center, **Then** vertical offset remains flush at the top edge and the island glides smoothly with ease-out motion to snap magnetically back to top-center.
3. **Given** **Floating Pill** mode is active and the user drags the compact pill near a monitor edge or bottom work-area boundary, **When** the island transitions to **Expanded** or **Notification** state, **Then** both the surface and its host bounds stay safely clamped inside the active monitor work area and glide smoothly into safe bounds rather than teleporting first before animating.
4. **Given** a custom position is saved, **When** the user enables **Lock Position**, **Then** dragging attempts are ignored while clicks and inner widget controls remain responsive; when the user clicks **Reset Position** (in Settings or system tray), the island glides back to default top-center and clears the custom offset across restarts.

---

### User Story 3 - Below-Island Green Agent Companion Pill (Priority: P2)

When a normal-priority widget (such as Now Playing) occupies the collapsed/active island pill and Developer Activity is active (`Working` or `Finished` dwell), the compact green progress pill appears centered directly beneath the main pill with a small vertical gap (`4–6 px`) and zero extra text, keeping the Now Playing layout (album art, track title, and trailing playing dot) intact. Urgent coding-agent states (`AwaitingApproval`, `NeedsAttention`, `ErrorFailed`) retain full compact-pill priority, and the below-island pill hides smoothly whenever the island expands or Developer Activity becomes idle.

**Why this priority**: Keeping Now Playing controls and trailing indicator uncrowded while maintaining glanceable background agent progress prevents text truncation and visual clutter.

**Independent Test**: Trigger Now Playing playback concurrently with Developer Activity `Working`, `Finished`, `AwaitingApproval`, and `Expanded` shell transitions; verify the trailing playing dot stays visible inside Now Playing, the inline right slot is absent, the below-island pill animates smoothly below with a 5 px gap and sweep effect, hides on expand, and passes clicks cleanly in surrounding transparent areas.

**Acceptance Scenarios**:

1. **Given** Now Playing is actively playing and Developer Activity enters `Working`, **When** the active compact pill renders, **Then** Now Playing keeps its full width and trailing playing dot intact while only the compact green progress pill renders centered `5 px` underneath with no extra text.
2. **Given** the below-island green companion pill is entering from inactive state, **When** Developer Activity becomes `Working`, **Then** the sub-pill slides down from behind the main pill with opacity fade-in and subtle scale-in (`0.92 -> 1.0`, `~200 ms` ease-out) and renders a smooth animated sweep/pulse along the green bar while working.
3. **Given** the sub-pill is visible in `Working` state, **When** the last agent turn completes (`Finished` dwell) or goes idle or the island expands, **Then** `Finished` settles the sweep smoothly into solid green dwell, and going idle or expanding smoothly slides the sub-pill up and fades it out (`~160–180 ms`).
4. **Given** Now Playing is active with the below-island green pill visible, **When** Developer Activity raises `AwaitingApproval`, `NeedsAttention`, or `ErrorFailed`, **Then** the urgent Developer Activity compact pill takes full compact-pill priority and the below-island sub-pill hides.

---

### User Story 4 - Reduced Motion, Interrupted Transitions & Hit-Test Integrity (Priority: P2)

Every new animation across style morphing, hover/drag lift, snap/clamp gliding, edge-safe expand gliding, and companion entry/sweep/exit honors system Reduced Motion (`IsReducedMotionEnabled`) by snapping immediately with zero animation duration. Rapid interruptions hand off smoothly from live visual state, and transparent areas surrounding both the Floating Pill and the below-island companion pill remain click-through across all Per-Monitor DPI v2 scales.

**Why this priority**: Shell animations sit on top of all desktop applications; broken hit-testing blocks user clicks on desktop apps underneath, and unguarded animations break accessibility or stutter during rapid pointer changes.

**Independent Test**: Toggle Reduced Motion on and off during style switches, resets, companion entry/exit, and edge expands; verify zero-duration immediate completion and run automated hit-test checks around the capsule, gap, and below-island sub-pill across 100%, 150%, and 200% DPI scales.

**Acceptance Scenarios**:

1. **Given** Reduced Motion is enabled, **When** the user switches Island Style, resets position, drops near top-center, expands near a monitor edge, or triggers below-island companion visibility, **Then** the final geometry, position, and visibility snap immediately on the first layout pass without looping sweep timers or transition lag.
2. **Given** an interactive animation (style morph, snap glide, or sub-pill slide-down) is halfway complete, **When** a reverse action interrupts it (switching style back, grabbing the island, or expanding), **Then** the animation smoothly reverses from the current mid-flight value without flicker, snap-back, or stuck visibility/scale state.
3. **Given** Floating Pill and below-island green pill are both visible, **When** pointer events hit transparent regions around the floating corners, the vertical gap between pills, or the left/right flanks beside the narrow sub-pill across multiple DPI scales, **Then** clicks and mouse messages pass cleanly through to underlying windows without clipping the shadow or pill borders.

---

### Edge Cases

- What happens when a user saves a custom Floating Pill position near the bottom-right of a `2560x1440` monitor and restarts on a smaller `1280x720` monitor or disconnected secondary monitor? Both compact and expanded placements clamp strictly within the active monitor's current work area (`rcWork`), never positioning off-screen or overlapping the taskbar.
- What happens when the taskbar sits at the top or left edge of a monitor? Docked Notch alignment and Floating/Expanded clamping anchor against the monitor work area (`WorkArea` when valid) so the island never disappears behind a top or side taskbar.
- What happens when dragging inside the Expanded island where sliders (Volume Mixer), text inputs (Notes/Clipboard search), or pager dots reside? Pointer presses inside interactive controls never trigger island dragging or steal control focus.
- What happens when Alt+hover click-through activates while the island is Docked/Floating? The entire island and sub-pill fade and become click-through together and restore cleanly after the click-through timer expires.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-SHELL-010**: System MUST provide an **Island Style** preference (`Docked Notch` default, `Floating Pill` alternative) in General Settings persisted locally across restarts and applied immediately to the live island.
- **FR-SHELL-011**: **Docked Notch** mode MUST render the island flush against the top work-area edge with top concave notch curves; **Floating Pill** mode MUST render the island detached with a `12 px` default top offset, full rounding on all four corners, zero top notch ears, and subtle elevation drop shadow.
- **FR-SHELL-012**: Switching Island Style MUST smoothly morph top offset, corner contours/ear retraction, and drop-shadow opacity over `~200–240 ms` using ease-out easing, and Floating Pill hover MUST animate a subtle scale and shadow elevation lift on pointer enter and ease back on leave.
- **FR-SHELL-013**: Users MUST be able to drag the island past a small drag threshold without breaking click-to-expand, widget switching, or inner interactive controls (`Buttons`, `Sliders`, `TextBoxes`, scrollbars, pager dots); dragging MUST be constrained horizontally along the top work-area edge in Docked Notch mode and free X/Y inside the active monitor work area in Floating Pill mode.
- **FR-SHELL-014**: System MUST clamp both compact/active and expanded/notification bounds safely within multi-monitor work areas (respecting Per-Monitor DPI v2 scaling), smoothly gliding into safe bounds as the island expands near an edge or bottom instead of teleporting first.
- **FR-SHELL-015**: Custom island position offsets and **Lock Position** state MUST persist across restarts, General Settings MUST expose **Lock Position** and **Reset Position** controls (alongside tray Reset), dragging MUST apply a tactile scale/shadow lift on grab and smooth settle on release, and releasing near screen edges or within `~16 px` of top-center or clicking **Reset Position** MUST animate an ease-out glide to the clamped/snapped/default target.
- **FR-SHELL-016**: Inline compact companion hosting inside the primary compact pill MUST be removed so Now Playing preserves its full collapsed layout (album art, title, trailing playing dot intact), and Developer Activity `Working` / `Finished` dwell companion state when paired with a normal-priority active widget MUST render only the compact green progress pill centered `~5 px` directly underneath the main pill with no extra text.
- **FR-SHELL-017**: The below-island green companion pill MUST hide smoothly on island expand (and when going idle), allow urgent agent states (`AwaitingApproval`, `NeedsAttention`, `ErrorFailed`) to take full compact-pill priority, animate slide-down + fade-in + subtle scale-in (`0.92 -> 1.0`, `~200 ms` ease-out) from behind the main pill on entry, display a smooth animated green bar sweep/pulse while `Working` settling smoothly on `Finished` dwell, and slide up + fade out (`~160–180 ms`) on exit.
- **FR-SHELL-018**: All new shell and companion animations MUST honor `IsReducedMotionEnabled` (immediate zero-duration state/position completion and stopped sweep timers), recover cleanly from mid-animation interruptions, and keep transparent areas around both the Floating Pill and below-island companion pill click-through without visual clipping across DPI scales.

### Key Entities

- **`IslandStyle`**: User preference choosing between top-flush `DockedNotch` and detached rounded `FloatingPill`.
- **`IslandPositionPreferences`**: Persisted horizontal center offset (`IslandOffsetXDip`), floating top margin override (`IslandFloatingTopOffsetDip`), and drag-lock flag (`LockIslandPosition`).
- **`MonitorWorkAreaPlacement`**: Clamped visual and window bounds derived from the target monitor work area, DPI scale, current visual state spec, and custom position offsets.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Switching between **Docked Notch** and **Floating Pill**, changing **Lock Position**, and clicking **Reset Position** take effect visually in under `250 ms` (or `0 ms` with Reduced Motion enabled) and persist across preference reload with 100% accuracy.
- **SC-002**: Clamping and multi-DPI placement calculations keep 100% of compact, active, notification, and expanded island bounds strictly inside active monitor work areas across `100%`, `125%`, `150%`, `200%`, and negative-coordinate multi-monitor configurations.
- **SC-003**: Drag gestures over interactive controls (`Buttons`, `Sliders`, `TextBoxes`) never accidentally reposition the island, and clicks below the drag threshold preserve 100% normal click-to-expand and widget switching behavior.
- **SC-004**: When Developer Activity coexists in `Working` / `Finished` dwell alongside Now Playing, Now Playing keeps its trailing dot visible, the below-island pill sits centered `4–6 px` beneath the main pill with zero extra text, hides upon expansion, and transparent surrounding points pass hit-tests cleanly through.
- **SC-005**: The full solution builds (`Release`) and passes 100% of automated Core, Infrastructure, WindowsInterop, Widgets, and UI tests (`dotnet test Shima.sln -c Release` = exit 0).

## Assumptions

- Top-center default in Docked Notch aligns flush with `WorkArea.Top` (`0 px` below taskbar-free top edge) and centered horizontally (`offsetX = 0`); top-center default in Floating Pill sits `12 px` below `WorkArea.Top` and centered horizontally.
- When custom horizontal/vertical offsets saved on a larger monitor exceed a newly active smaller monitor's work area, runtime positioning clamps cleanly without mutating invalid coordinates permanently until adjusted or reset.
- Existing compact active priority arbitration (`Urgent` Developer Attention > `Normal` Now Playing/Focus > `Background` Developer Working/Finished dwell) remains authoritative inside `IslandStateManager`.
