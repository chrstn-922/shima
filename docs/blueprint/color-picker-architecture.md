# Color Picker Architecture (`Shima.Presentation` & `Shima.Core`)

**Document Role**: Technical design for Shima's Photoshop 22.3.0-inspired Advanced Color Picker system (functional interaction reference only; original visual identity).  
**Contracts**: [color-picker-contract.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/contracts/color-picker-contract.md) | **Contrast**: [color-contrast-validation.md](file:///C:/Users/chris/Projects/shima/docs/blueprint/color-contrast-validation.md)

---

## 1. 13-Component Architecture & Responsibilities

| # | Component | Layer | Responsibility |
| :-: | :--- | :--- | :--- |
| **1** | `ColorPickerDialog` | Presentation | Modal host spawned from Appearance Settings; manages `Apply / OK`, `Cancel`, `Reset to Default`, `Collapsed/Expanded` preview mode toggle, keyboard shortcuts (`Esc` = Cancel, `Enter` = Apply), and `finally` pin cleanup. |
| **2** | `ColorSelectionField` | Presentation | `256×256 DIP` 2D Saturation (X: `0–100%`) × Brightness (Y: `100–0%`) gradient canvas rendered via reusable `WriteableBitmap` with an inverted high-contrast circular reticle; supports pointer capture & arrow keys (`1%` / `Shift+5%`). |
| **3** | `HueSpectrumSlider` | Presentation | Vertical `0°–360°` rainbow rail (`360°` red at top → `0°` red at bottom); supports click, drag, mouse wheel (`1°`), and `Up/Down`/`PageUp/PageDown` (`1°`/`15°`). |
| **4** | `ColorPreviewPanel` | Presentation | Split comparison swatch (`New Color` top half vs. `Current Color` bottom half); clicking `Current Color` snaps working selection back to baseline. |
| **5** | `HexInputComponent` | Presentation | Validates `#RRGGBB` (case-insensitive, optional `#`, expands 3-digit `#RGB` on commit); reverts on invalid blur. |
| **6** | `RgbInputComponent` | Presentation | Three synchronized `0–255` integer boxes (`R`, `G`, `B`) with clamping & up/down steppers. |
| **7** | `HsbInputComponent` | Presentation | Three synchronized HSB boxes (`H: 0–360°`, `S: 0–100%`, `B: 0–100%`) preserving `H` along the grayscale edge. |
| **8** | `ColorPresetManager` | Core / Infra | Serves the 6 immutable built-in presets + CRUD (`Create`, `Rename`, `Delete`) for user custom presets in `presets.json`. |
| **9** | `RecentColorsManager` | Core / Infra | Maintains the 12-slot FIFO ring buffer of confirmed colors (`Apply / OK` only). |
| **10** | `AppearanceConfigManager` | Core | Owns `CurrentCommitted`, `IAppearanceEditingSession` draft isolation, slot-only `Reset to Default`, and atomic persistence. |
| **11** | `ThemeRenderer` | Presentation | Updates WPF `ResourceDictionary` solid/gradient brushes dynamically on `RenderedAppearanceChanged` without rebuilding views. |
| **12** | `LiveIslandPreviewController` | Core / Pres | Coalesces high-frequency draft color edits (`<= 16.6 ms` VSync rate) onto the real Dynamic Island & Settings preview card with zero disk writes. |
| **13** | `IslandVisibilityPinController` | Core / WinInterop | Acquires `ExplicitPin` on dialog open (blocking mouse-leave collapse without stealing Settings focus) and releases pin deterministically on close. |

---

## 2. Centralized Color Processing & Precision Rules (`IColorProcessingService`)

### 2.1 Conversion Formulas & Internal Precision

- **Internal Working Type**: IEEE-754 `double` (`h ∈ [0, 360)`, `s ∈ [0, 1]`, `v ∈ [0, 1]`, `r,g,b ∈ [0, 1]`).
- **HSB → RGB**:
  - `C = v * s`, `X = C * (1 - |(h / 60) mod 2 - 1|)`, `m = v - C`.
  - Sector mapping (`0..5` across `60°` bands) adds `m` to `(r', g', b')` and scales by `255.0` using `Math.Round(..., MidpointRounding.AwayFromZero)` to produce exact `byte` RGB channels.
- **RGB → HSB**:
  - Normalized `r,g,b = R/255.0`; `max = Max(r,g,b)`, `min = Min(r,g,b)`, `delta = max - min`.
  - `V = max * 100.0`; `S = (max == 0) ? 0 : (delta / max) * 100.0`.
  - **Hue Singularity Preservation**: When `delta == 0` (pure white, gray, or black), `RgbToHsb` returns `H = previousHue` inside an active picker session instead of resetting `H` to `0°`, preventing the Hue slider and 2D gradient from jumping unexpectedly while dragging near the left/bottom edges.
- **Boundary Clamping**:
  - `360°` Hue input normalizes cleanly (`360°` displays as `360°` or `0°` on the spectrum top/bottom consistently without out-of-bounds math errors).
  - Out-of-range integers (`RGB > 255` or `< 0`, `S/B > 100`) clamp to `[min, max]` on commit/blur.

---

## 3. Interaction & Loop-Free Synchronization Flow

1. User drags `(x, y)` in `ColorSelectionField`: computes `S = clamp(x / width, 0, 1) * 100`, `B = clamp(1 - y / height, 0, 1) * 100` using current preserved `H` → constructs `ShimaColor` with `Source = Field2D`.
2. `ColorPickerViewModel` updates `HexInput`, `RgbInput`, `HsbInput` (skipping re-entry notifications for `Source == Field2D`), `ColorPreviewPanel.NewColor`, `ContrastAuditReport`, and `AppearanceEditingSession.UpdateToken(...)`.
3. User drags `HueSpectrumSlider`: updates `H` → triggers `ColorSelectionField` to re-fill its pre-allocated `256×256` `WriteableBitmap` for the new hue (`< 0.6 ms`), keeps `(S, B)` constant, and propagates `Source = HueSlider` to numeric inputs and live preview.
4. User types in `HEX`, `RGB`, or `HSB`: parses validated value → updates reticle `(S, B)` + Hue slider + sibling inputs + live preview.

---

## 4. Accessibility & Keyboard Mechanics

- **2D Field Keyboard Support**: Focusable custom control (`AutomationProperties.Name="Color saturation and brightness field"`) supporting `Left/Right` (Saturation `±1%`), `Up/Down` (Brightness `±1%`), and `Shift + Arrow` (`±5%`).
- **Hue Slider**: Focusable slider supporting `Up/Down` (`±1°`) and `PageUp/PageDown` (`±15°`).
- **Numeric Boxes**: Logical `Tab` order (`2D Field` → `Hue Slider` → `HEX` → `R,G,B` → `H,S,B` → `Contrast Fix` → `Preview Mode Toggle` → `Reset to Default` → `Cancel` → `Apply / OK`).
