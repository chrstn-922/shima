# Color Contrast Validation & Auto-Repair Design (`FR-PICKER-006`)

**Document Role**: Technical specification for Shima's non-blocking WCAG 2.1 contrast auditor and one-click `Restore Recommended Contrast` solver.  
**Contract**: [appearance-manager-contract.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/contracts/appearance-manager-contract.md)

---

## 1. WCAG 2.1 Relative Luminance & Contrast Ratio Calculation

All color pairs are evaluated deterministically in linear sRGB space per W3C WCAG 2.1:

1. **Channel Linearization** (for normalized channel `c ∈ [0.0, 1.0]`):
   - If `c <= 0.04045`: `c_lin = c / 12.92`
   - Else: `c_lin = ((c + 0.055) / 1.055) ^ 2.4`
2. **Relative Luminance (`L`)**:
   - `L = 0.2126 * R_lin + 0.7152 * G_lin + 0.0722 * B_lin` (`L ∈ [0.0, 1.0]`)
3. **Contrast Ratio (`CR`)**:
   - `CR = (max(L_1, L_2) + 0.05) / (min(L_1, L_2) + 0.05)` (`CR ∈ [1.0, 21.0]`)

---

## 2. Thresholds & Non-Blocking Advisory Policy

| Token Pair Evaluated | Recommended Minimum Ratio | Failure Action (`FR-PICKER-006`) |
| :--- | :---: | :--- |
| `PrimaryTextHex` vs `BackgroundHex` | **`4.5 : 1`** (Normal text) | **Non-blocking advisory warning** (`"Primary text contrast 2.1:1 (recommended >= 4.5:1)"`). |
| `SecondaryIconHex` vs `BackgroundHex` | **`3.0 : 1`** (Icons & secondary UI) | **Non-blocking advisory warning** (`"Icon contrast 1.9:1 (recommended >= 3.0:1)"`). |
| `AccentHex` vs `BackgroundHex` | **`3.0 : 1`** (Active indicators) | **Non-blocking advisory warning** (`"Accent indicator contrast 1.5:1 (recommended >= 3.0:1)"`). |

### Mandatory Non-Blocking Invariant

- **Never Block `Apply / OK`**: Even when all three checks report warnings (`CR == 1.0:1`, e.g., black text on black background), the **Apply / OK** button MUST stay enabled and clickable so users retain full creative control.
- **No Silent Error Coercion**: Shima MUST NEVER silently alter a user's chosen hex color on save unless the user explicitly clicks **"Restore Recommended Contrast"**.

---

## 3. "Restore Recommended Contrast" Solver Algorithm

When the user clicks **Restore Recommended Contrast**:

1. **Preserve User Background & Hue Intent**: Keep the user's selected `BackgroundHex` fixed (and preserve `GlowHex` / `BorderHex` untouched) so the island retains their chosen base theme mood.
2. **Determine Target Polarity**:
   - Compare `CR(White #FFFFFF, Background)` vs. `CR(NearBlack #0F172A, Background)`. Choose the higher-contrast direction (light foreground on dark background, or dark foreground on light background).
3. **Minimal-Shift Binary Search (Preserving Foreground Hue)**:
   - For `PrimaryTextHex` (`target >= 4.5:1`), `SecondaryIconHex` (`target >= 3.0:1`, or `4.5:1` if preferred), and `AccentHex` (`target >= 3.0:1`):
     - If current `CR >= target`, keep the user's token unchanged!
     - Otherwise, retain the foreground color's current **Hue (`H`)** and adjust **Brightness (`V`)** (and reduce Saturation `S` gently toward neutral only if full brightness/darkness at high saturation cannot reach the target ratio) via 16-step binary search until `CR >= target` with minimal visual distance from the user's selected tint.
4. **Immediate Live Preview Update**: Writes adjusted tokens into `AppearanceEditingSession.Draft` so the user inspects the repaired island live and can further tweak or confirm with **Apply / OK**.

---

## 4. Known Contrast Calculation Limitations

- **Translucent / Alpha Surfaces**: If future phases introduce semi-transparent Acrylic/Mica backdrops, true foreground contrast depends on the live desktop wallpaper behind the island; WCAG contrast against a solid token is only an upper/lower approximation when opacity `< 1.0`.
- **Gradients, Borders & Ambient Glows**: Single-point `CR(fg, bg)` does not account for outer drop shadows (`GlowHex`) or `1px` rim borders (`BorderHex`) separating dark islands from dark wallpapers; therefore, border/glow contrast is not flagged as a text readability failure.
- **Color Vision Deficiency**: WCAG luminance contrast measures lightness separation, not red/green hue distinguishability; critical states (e.g., `Allow` vs `Deny` or `Awaiting approval`) therefore pair color with explicit icons and text labels.
