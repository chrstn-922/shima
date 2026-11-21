# Shima UI/UX & Design System

**Document Role**: Defines Shima's original visual identity, geometry, typography, spacing, animation pacing, Settings navigation, and accessibility rules.  
**Reference Inputs**: Supplied Dynamic Island screenshots (Collapsed notch & Expanded Clock layout) without copying third-party branding or proprietary assets.

---

## 1. Visual Identity & Core Aesthetic

- **Identity**: *Obsidian Precision* — a calm, geometric, high-contrast surface anchored cleanly into the upper monitor bezel, using pure matte surfaces (`Midnight` default `#000000`), crisp typography, and subtle `1px` edge definition so it feels native to Windows 11/10 dark and light desktops alike.
- **No Hardcoded Colors**: Every XAML view and widget binds strictly to semantic theme resource tokens (`SurfaceCollapsed`, `SurfaceExpanded`, `AccentBrush`, `BorderHairline`, `GlowBrush`, `TextPrimary`, `TextMuted`).

---

## 2. Geometry, Dimensions & Per-Monitor DPI Scaling

All coordinates are authored in **Device-Independent Pixels (DIPs)** and scaled deterministically via `VisualTreeHelper.GetDpi` under `PerMonitorV2`:

| State | Target Dimensions (DIPs) | Geometry & Corner Radii |
| :--- | :--- | :--- |
| **Collapsed (Idle)** | `136 × 28` | Flush at `Y = 0`; concave top-left & top-right ear fillets (`r_ear = 12 DIPs`), rounded bottom corners (`r_bottom = 14 DIPs`). |
| **Active (Compact Pill)** | `200–256 × 32` | Smooth horizontal expansion; houses left mini badge (artwork/timer/agent dot) and right mini telemetry (equalizer/countdown). |
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
  - `CaptionMuted` (date header, UTC offset, city list): `11.5 DIP`, Regular (`400`).
- **Spacing Grid**: `4 DIP` base unit (`8`, `12`, `16`, `20`, `24 DIP` padding tokens). Inner expanded content margin: `20 DIP` horizontal, `16 DIP` vertical.

---

## 4. Layout Blueprints (Island & Settings)

- **Expanded Clock Layout** (parity with reference Image 2):
  - **Left Pane (`62%`)**: Date caption (`Saturday, November 21`), `HeroMetric` time (`9:15 PM`), subtitle (`Manila · UTC+8`).
  - **Right Pane (`38%`)**: Up to 4 visible selectable city rows (active city prefixed with `•` and `PrimaryTextColor`; inactive cities in `SecondaryTextColor`; scrollable if `> 4`) + bottom-aligned Settings gear (`⚙`) action button.
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
- **Action vs. Status Separation**: Interactive buttons expose explicit action verbs in `AutomationProperties.Name` (e.g., `"Open Shima settings"`, `"Allow tool execution"`, `"Restore recommended contrast"`), while passive indicators expose descriptive state (`"Selected city Manila, UTC plus 8"`, `"Current contrast ratio 1.8 to 1"`).
- **Keyboard Traversal**: Full `Tab` navigation inside Expanded Island and Settings/Color Picker, `Left/Right` or `Up/Down` widget switching, `Esc` collapse, and visible `2 DIP` high-contrast focus rings.
