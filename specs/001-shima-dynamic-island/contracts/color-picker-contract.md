# Contract: Advanced Color Picker (`Shima.Presentation` & `Shima.Core`)

**Contract Scope**: Defines the Photoshop 22.3.0-inspired Color Picker dialog contract, centralized color conversion service (`IColorProcessingService`), live island preview synchronization, and `FR-PICKER-006` compliance.

---

## 1. Internal Color Representation (`ShimaColor`)

```text
readonly record struct ShimaColor(
    byte R, byte G, byte B,          // Exact 8-bit sRGB channels [0..255]
    double H, double S, double V,    // Canonical HSB/HSV [H: 0..360, S: 0..100, V: 0..100]
    string Hex)                      // Uppercase "#RRGGBB"
```

- **Hue Preservation Invariant**: When `S == 0` or `V == 0` (grayscale/black axis), the picker state retains the user's current `H` value in memory so dragging horizontally out of white/black restores the previous hue instead of snapping to `0°` (red).

---

## 2. Color Processing Service (`IColorProcessingService`)

```text
interface IColorProcessingService
  ShimaColor FromHex(string hex)                         // Validates #RRGGBB or #RGB; throws/returns Result on invalid
  bool TryParseHex(string input, out ShimaColor color)
  ShimaColor FromRgb(int r, int g, int b)                // Clamps [0..255]
  ShimaColor FromHsb(double h, double s, double b)       // Clamps H [0..360], S [0..100], B [0..100]
  (byte R, byte G, byte B) HsbToRgb(double h, double s, double b)
  (double H, double S, double B) RgbToHsb(byte r, byte g, byte b)
  string RgbToHex(byte r, byte g, byte b)
  double ComputeContrastRatio(ShimaColor fg, ShimaColor bg)   // WCAG 2.1 relative luminance ratio [1.0..21.0]
  ShimaColor SolveRecommendedForeground(ShimaColor bg, ShimaColor preferredFg, double minRatio)
```

**Precision & Rounding Rules**:
- Internal math runs in IEEE-754 `double` (`[0.0..1.0]` normalized channels).
- Channel outputs (`R`, `G`, `B`) use `Math.Round(..., MidpointRounding.AwayFromZero)`.
- Numeric text boxes in the UI synchronize bidirectionally without infinite feedback loops using a single `ColorChangeSource` token (`Field2D | HueSlider | HexInput | RgbInput | HsbInput | Swatch | Reset`).

---

## 3. Color Picker Session Contract (`IColorPickerDialogService`)

```text
enum ColorPickerAction { Apply, Cancel }

record ColorPickerRequest(
    AppearanceTokenSlot TargetSlot,
    ShimaColor InitialAppliedColor,
    IslandPreviewMode InitialPreviewMode,   // Collapsed | Expanded
    IReadOnlyList<string> RecentColorsHex)

record ColorPickerResult(
    ColorPickerAction Action,
    ShimaColor FinalColor)
```

### Mandatory Behavioral Guarantees (`FR-PICKER-006`)

1. **On Open**:
   - Acquires an `ExplicitPin` from `IOverlayPinController` keeping the real top-center Dynamic Island visible and immune to mouse-leave auto-collapse.
   - Opens an `IAppearanceEditingSession` snapshot (isolated from disk).
   - Never steals keyboard focus away from the Settings / Color Picker window onto the Island overlay.
2. **During Editing**:
   - Exposes `PreviewMode` toggle (`Collapsed` ↔ `Expanded`) on the Color Picker dialog; toggling updates the real Dynamic Island immediately without saving.
   - Any drag on the **2D Saturation/Brightness Field**, **Vertical Hue Slider**, **HEX/RGB/HSB inputs**, **Current Swatch click**, **Recent Color**, or **Reset to Default** updates the live `IAppearanceEditingSession` and dispatches a frame-coalesced (`<= 16.6 ms`) render update to the Dynamic Island.
   - Zero SQLite or JSON disk writes occur during live scrubbing.
3. **Contrast Advisory & One-Click Repair**:
   - Evaluates `ComputeContrastRatio` whenever `Background`, `PrimaryText`, `SecondaryIcon`, or `Accent` changes.
   - Displays a **non-blocking advisory warning** when Text `< 4.5:1` or Icon/Accent `< 3.0:1`.
   - **`Apply / OK` MUST remain enabled at all times**, even when contrast is below threshold.
   - Clicking **`Restore Recommended Contrast`** invokes `SolveRecommendedForeground(...)` and updates the live preview immediately.
4. **Reset to Default (Slot-Scoped)**:
   - Clicking **`Reset to Default`** inside the Color Picker resets **only** `TargetSlot` to its `Midnight` factory default inside the temporary live preview (reversible via `Cancel`, persisted only upon `Apply / OK`).
5. **On Close (Apply, Cancel, Esc, or Unexpected Error)**:
   - **`Apply / OK`**: Commits `FinalColor`, persists `appearance.json` + updates `recentColors[]` in `presets.json`, releases `PinToken`, and restores normal auto-collapse.
   - **`Cancel` / `Esc` / Window Close**: Discards `IAppearanceEditingSession`, restores the exact pre-session island appearance, releases `PinToken`, and restores normal auto-collapse.
   - **`finally` Safety Guard**: Unhandled errors or dialog disposal guarantee `ReleasePin` + preview rollback so the island can never remain permanently pinned or show unsaved colors.
