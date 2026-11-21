# Shima UI/UX & Design System

**Document Role**: Defines Shima's original visual identity, geometry, typography, spacing, animation pacing, Settings navigation, and accessibility rules.  
**Reference Inputs**: Supplied Dynamic Island screenshots (Collapsed notch & Expanded Clock layout) and Developer Activity multi-session progress notch reference without copying third-party branding or proprietary assets.

---

## 1. Visual Identity & Core Aesthetic

- **Identity**: *Obsidian Precision* — a calm, geometric, high-contrast surface anchored cleanly into the upper monitor bezel, using pure matte surfaces (`Midnight` default `#000000`), crisp typography, and subtle `1px` edge definition so it feels native to Windows 11/10 dark and light desktops alike.
- **No Hardcoded Colors**: Every XAML view and widget binds strictly to semantic theme resource tokens (`SurfaceCollapsed`, `SurfaceExpanded`, `AccentBrush`, `BorderHairline`, `GlowBrush`, `TextPrimary`, `TextMuted`, plus Developer Activity status tokens `AgentStatusIdleBrush`, `AgentStatusTrackBrush`, `AgentStatusWorkingBrush`, `AgentStatusFinishedBrush`, `AgentStatusAttentionBrush`, `AgentStatusErrorBrush`).

---

## 2. Geometry, Dimensions & Per-Monitor DPI Scaling

All coordinates are authored in **Device-Independent Pixels (DIPs)** and scaled deterministically via `VisualTreeHelper.GetDpi` under `PerMonitorV2`:

| State | Target Dimensions (DIPs) | Geometry & Corner Radii |
| :--- | :--- | :--- |
| **Collapsed (Idle)** | `136 × 28` | Flush at `Y = 0`; concave top-left & top-right ear fillets (`r_ear = 12 DIPs`), rounded bottom corners (`r_bottom = 14 DIPs`). |
| **Active (Compact Pill)** | `200–256 × 32` | Smooth horizontal expansion; houses left mini badge (artwork/timer/agent dot) or horizontal Developer Activity summary status bar (`~4 DIP` height, `2 DIP` radius). |
| **Notification Peek** | `340 × 76` | Compact toast card showing `28×28` app icon, bold title, 1–2 line truncated body, and optional `Dismiss` / `Allow` / `Deny` action pills. |
| **Expanded Panel** | `412 × 168` (range `380–440 × 150–240` by widget) | Concave top fillets (`r_ear = 16 DIPs`), bottom corner radius `24 DIPs`, `1 DIP` inner hairline border, soft ambient drop shadow (`blur 28`, `y-offset 8`). |

- **8-Dot Vertical Pager**: Fixed `18 DIP` right-edge column inside the expanded viewport displaying `6 DIP` circular dots (`4 DIP` vertical gap); active widget dot uses `TextPrimary`/`AccentBrush` at `100%` opacity, inactive dots at `30%` opacity.

---

## 3. Typography & Spacing Scale

- **Font Family**: Windows native `Segoe UI Variable Display` / `Segoe UI Variable Text` (with automatic `Segoe UI` fallback on Windows 10) for effortless native integration and tabular numerals (`Typography.NumeralAlignment="Tabular"` for Clock & Focus Timer digits).
- **Type Ramp**:
  - `HeroMetric` (Clock digits `9:15`): `42 DIP`, Light (`300`), tight tracking (`-0.02em`), superscript `AM/PM` (`11 DIP`, Medium).
  - `TitlePrimary`: `14 DIP`, SemiBold (`600`).
  - `BodyDefault`: `12.5 DIP`, Regular (`400`).
  - `CaptionMuted` (date header, UTC offset, city list, repo subtitles, timestamps): `11.5 DIP`, Regular (`400`).
- **Spacing Grid**: `4 DIP` base unit (`8`, `12`, `16`, `20`, `24 DIP` padding tokens). Inner expanded content margin: `20 DIP` horizontal, `16 DIP` vertical.

---

## 4. Layout Blueprints (Island & Settings)

- **Expanded Clock Layout** (parity with reference Image 2):
  - **Left Pane (`62%`)**: Date caption (`Saturday, November 21`), `HeroMetric` time (`9:15 PM`), subtitle (`Manila · UTC+8`).
  - **Right Pane (`38%`)**: Up to 4 visible selectable city rows (active city prefixed with `•` and `PrimaryTextColor`; inactive cities in `SecondaryTextColor`; scrollable if `> 4`) + bottom-aligned Settings gear (`⚙`) action button.
- **Developer Activity Layout — Collapsed & Expanded (Claude Code & OpenCode)**:
  - **Collapsed Notch Status Bar**: Displays the thin rounded horizontal progress/status bar (`4 DIP` height, `2 DIP` corner radius) inside the collapsed notch whenever **Claude Code** or **OpenCode** is opted in (including the muted gray `Idle` bar; resting as a pure black obsidian pill only when Developer Activity is disabled or neither adapter is opted in). The bar reflects the highest-priority active coding-agent session (`Awaiting approval` / `Needs attention` [including `Error / Failed`] > `Working` > `Finished` > `Idle`):
    - **Idle** (no active sessions): full-width muted gray bar (`AgentStatusIdleBrush`).
    - **Working** (active processing): partial green fill over muted track (`AgentStatusWorkingBrush` over `AgentStatusTrackBrush`).
    - **Awaiting approval / Needs attention**: partial red/coral fill over muted track (`AgentStatusAttentionBrush` over `AgentStatusTrackBrush`).
    - **Finished**: full-width green bar (`AgentStatusFinishedBrush`); dwells on the collapsed bar for `10 seconds` (or until hovered/opened), then reverts to `Idle` gray while suspending timers.
    - **Error / Failed** (fatal session/runtime error): full-width red/coral bar (`AgentStatusErrorBrush`); remains on the collapsed bar until hovered/dismissed (capped at `15 minutes` auto-expiry).
    - **Compact-Pill Coexistence**: When active alongside Now Playing or Focus Timer, `Awaiting approval` / `Needs attention` takes compact-pill precedence, while `Working` / `Finished` share or yield to an active Focus Timer or Now Playing pill.
    - All bar fills and track brushes MUST bind strictly to semantic theme resource tokens (`ThemeTokens` / `ThemeRenderer`), never hardcoded hex literals.
  - **Expanded Multi-Session List (Hover / Open)**:
    - **Auto-Open Routing**: When at least one coding-agent session is active or awaiting attention, expanding the island opens directly to **Developer Activity**; when `Idle`, expanding opens the user's last active/default widget (such as Clock).
    - Keeps the summary status bar pinned cleanly at the top (`8–12 DIP` below top edge) of the expanded Developer Activity view.
    - Below the bar, renders a vertical list of isolated active and recently finished/errored sessions (retaining finished/errored rows for `15 minutes` or until dismissed; scrollable when exceeding expanded height), each row laid out as:
      - **Left**: `24 × 24 DIP` agent/source badge icon identifying **Claude Code** or **OpenCode**.
      - **Center**: Bold task/session title (`TitlePrimary`, `TextPrimary`) stacked over muted repository/project subtitle (`CaptionMuted`, `TextMuted`).
      - **Right**: Right-aligned color-coded state label (`Working`, `Needs attention`, `Awaiting approval`, `Finished`, or `Disconnected` using sentence case and semantic status brushes) stacked over a tabular elapsed/relative timestamp (`CaptionMuted`, `TextMuted`, e.g., `"2 min"`, `"12 min"`, `"4 min ago"`).
      - **Row Interaction & Conditional Inline Approvals**: Clicking a non-approval session row focuses its terminal window whenever `SupportsTerminalFocus == true`. **Allow** (strictly mapped to single-action `once`, never `always` implicitly) and **Deny** (`reject`) action pill buttons appear **only** on rows whose session is in `Awaiting approval` AND has a verified, actionable permission request supported by its adapter (`HasActionablePermissionRequest == true`), updating only after native confirmation (with optional **Focus Terminal** where supported).
  - **Empty State**: When zero active or recent sessions are present, keeps the full muted gray summary bar pinned at the top over a compact left-aligned `"No active sessions"` label (`CaptionMuted`, `TextMuted`).
- **Settings Window (`860 × 620 DIP` minimum, standard resizable Fluent/WPF window)**:
  - **Left Navigation Rail (`220 DIP`)**: `General`, `Widgets`, `Notifications`, `Clipboard`, `Focus Timer`, `Clock`, `Developer Integrations`, `Appearance & Customization`, `Updates`, `License`.
  - **Right Content Canvas**: Scrollable section header, inline card groups, live miniature Island preview strip in Appearance, and modal launch trigger for the **Advanced Color Picker**.

---

## 5. Animation & Reduced Motion System

- **Morphing Curve**: Cubic/Quintic Ease-Out (`220 ms` expand, `180 ms` collapse) animating rendered clip geometry bounds and opacity on the WPF composition render thread.
- **Reduced Motion Compliance (`UISettings.AnimationsEnabled == false`)**: Duration snaps immediately to `0 ms` (instant layout state swap with zero frame ticks).
- **Battery Saver Mode**: Drops decorative glow pulses and live visualizer bar frequency from `30 Hz` to static/minimal `2 Hz` progress bar updates.

---

## 6. Light/Dark & Accessibility Semantics

- **Presets**: Ships with both dark (`Midnight`, `Ocean`, `Lavender`, `Emerald`, `Sunset`) and light (`Snow`) presets; Settings window adapts automatically to Windows Light/Dark app mode (`SystemParameters`) and High Contrast themes.
- **Action vs. Status Separation**: Interactive buttons expose explicit action verbs in `AutomationProperties.Name` (e.g., `"Open Shima settings"`, `"Allow permission request for refactor auth"`, `"Deny permission request for refactor auth"`, `"Restore recommended contrast"`), while passive indicators expose descriptive state (`"Selected city Manila, UTC plus 8"`, `"Coding agent summary status: Working"`, `"Session status: Working, 12 min elapsed"`, `"Current contrast ratio 1.8 to 1"`), matching sentence-case UI copy.
- **Keyboard Traversal**: Full `Tab` navigation inside Expanded Island and Settings/Color Picker, `Left/Right` or `Up/Down` widget switching, `Esc` collapse, and visible `2 DIP` high-contrast focus rings.
