# Contract: Appearance & Preset Manager (`Shima.Core` & `Shima.Infrastructure`)

**Contract Scope**: Defines the persistent appearance configuration model, live preview editing session isolation, built-in and custom preset management, contrast auditing, and atomic persistence.

---

## 1. Appearance Tokens (`IslandColorPalette`)

| Property | Type | Role | Midnight Default |
| :--- | :--- | :--- | :--- |
| `BackgroundHex` | `string` | Primary island container fill | `#000000` |
| `AccentHex` | `string` | Active indicators, progress, active dot | `#FFFFFF` |
| `BorderHex` | `string` | `1px` edge rim defining dark/light boundaries | `#1F1F24` |
| `GlowHex` | `string` | Active/Expanded ambient highlight/shadow tint | `#000000` |
| `PrimaryTextHex` | `string` | Clock digits, track title, headings | `#F8FAFC` |
| `SecondaryIconHex` | `string` | Date metadata, muted cities, `⚙` icon | `#94A3B8` |

---

## 2. Appearance Manager Interface (`IAppearanceManager`)

```text
interface IAppearanceManager
  AppearanceConfiguration CurrentCommitted { get; }
  AppearanceConfiguration ActiveRendered { get; }     // Returns PreviewDraft if session open, else CurrentCommitted
  event EventHandler<AppearanceConfiguration> RenderedAppearanceChanged

  IAppearanceEditingSession BeginEditingSession()
  Task ApplyPresetAsync(string presetId)
  Task ResetAllToDefaultAsync()                       // Main Appearance Settings page: resets all 6 tokens to Midnight

interface IAppearanceEditingSession : IDisposable
  AppearanceConfiguration Draft { get; }
  IslandPreviewMode PreviewMode { get; set; }         // Collapsed | Expanded
  ContrastAuditReport CurrentContrastAudit { get; }
  void UpdateToken(AppearanceTokenSlot slot, IslandStateTarget target, string hex)
  void ApplyRecommendedContrastFix()                  // Adjusts PrimaryText / SecondaryIcon against Background
  void ResetSlotToDefault(AppearanceTokenSlot slot)   // Color Picker slot-only reset into Draft
  Task CommitAsync()                                  // Atomically saves Draft -> appearance.json & updates RecentColors
  void Cancel()                                       // Discards Draft & restores CurrentCommitted rendering
```

---

## 3. Preset & Recent Color Manager (`IPresetManager`)

```text
interface IPresetManager
  IReadOnlyList<ColorPreset> GetAllPresets()          // 6 immutable built-ins + custom user presets
  IReadOnlyList<string> GetRecentColors()             // Up to 12 #RRGGBB strings
  Task<ColorPreset> CreateCustomPresetAsync(string name, AppearanceConfiguration source)
  Task RenameCustomPresetAsync(string presetId, string newName)
  Task DeleteCustomPresetAsync(string presetId)       // Rejects deletion of built-in presets
  Task RecordRecentColorAsync(string hex)             // Called only on CommitAsync / Apply
```

**Built-in Presets (Immutable)**: `Midnight`, `Snow`, `Ocean`, `Lavender`, `Emerald`, `Sunset`.

---

## 4. Contrast Audit Report (`ContrastAuditReport`)

```text
record ContrastIssue(
    string ElementLabel,          // e.g. "Primary Text vs Background"
    double Ratio,                 // e.g. 2.14
    double RecommendedMinRatio,   // 4.5 for Text, 3.0 for Icons/Accent
    bool IsBelowThreshold)

record ContrastAuditReport(
    bool HasWarnings,             // Advisory only — NEVER blocks CommitAsync
    IReadOnlyList<ContrastIssue> Issues)
```

---

## 5. Persistence & Upgrade Resilience

- Committed updates write atomically to `%LOCALAPPDATA%\Shima\config\appearance.json` and `presets.json`.
- Built-in preset modifications are prevented; user presets and recent colors reside strictly in `%LOCALAPPDATA%\Shima\config\` outside the application binary folder so Velopack/MSIX updates never overwrite user customizations.
