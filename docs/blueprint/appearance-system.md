# Appearance & Theme System Architecture (`Shima.Core` & `Shima.Presentation`)

**Document Role**: Defines the persistent appearance configuration model, Collapsed/Expanded token linking, Per-Widget accent evaluation, Preset Management, and the `FR-PICKER-006` temporary live preview & pinning workflow.  
**Contracts**: [appearance-manager-contract.md](../../specs/001-shima-dynamic-island/contracts/appearance-manager-contract.md) | [data-model.md](../../specs/001-shima-dynamic-island/data-model.md)

---

## 1. Appearance Configuration Model

Each island appearance configuration stores two `IslandColorPalette` objects (`collapsedTokens` and `expandedTokens`) plus global accent/chrome metadata:

- **Customizable Tokens**:
  1. `BackgroundHex`: Surface fill of the Dynamic Island.
  2. `AccentHex`: Active indicators, progress rings/bars, active city dot, active carousel dot, primary highlights.
  3. `BorderHex`: `1 DIP` rim defining the island silhouette against matching dark/light wallpapers.
  4. `GlowHex`: Outer ambient elevation shadow / optional active-state glow (`glowOpacity` `0.0–1.0`).
  5. `PrimaryTextHex`: High-contrast digits, headings, titles.
  6. `SecondaryIconHex`: Muted captions, unselected world cities, `⚙` icon.
- **Collapsed vs. Expanded Linking**:
  - `linkCollapsedAndExpanded = true` (Default): Editing a token updates both `collapsedTokens` and `expandedTokens` synchronously for seamless visual continuity.
  - `linkCollapsedAndExpanded = false`: Unlocks independent Collapsed vs. Expanded Surface/Border/Glow customization, smoothly interpolated via `ColorAnimation` during expand/collapse transitions.
- **Per-Widget Accent Colors (`usePerWidgetAccents`)**:
  - *Evaluated Conclusion*: **Supported in data model and Phase 2 Settings toggle, defaulting to `false` (Unified Island Accent) in MVP** so out-of-the-box visual consistency stays calm (Constitution Principle V), while enabling power users to bind per-widget accents (e.g., Green for Now Playing, Amber for Focus) via `perWidgetAccents[widgetId]`.

---

## 2. Preset & Recent Color Management

- **6 Immutable Built-in Presets**: `Midnight` (default `#000000`), `Snow` (`#F8FAFC`), `Ocean` (`#071324`), `Lavender` (`#130F24`), `Emerald` (`#061C14`), `Sunset` (`#1F1013`).
- **Custom Presets (`presets.json`)**:
  - Users can **Save Current as Custom Preset**, **Apply**, **Rename** (1–40 chars, unique), and **Delete** custom presets. Built-in presets cannot be deleted or overwritten.
  - Clicking **Reset All Appearance** on the main Appearance Settings page resets all 6 tokens to `Midnight` defaults (requiring explicit confirmation or reversible undo banner).
- **Recent Colors**: Stores the last 12 confirmed `#RRGGBB` colors selected via `Apply / OK` across any token slot.

---

## 3. Temporary Live Preview Workflow (`FR-PICKER-006`)

The editing lifecycle enforces strict isolation between temporary preview state and persisted disk state:

1. **Snapshot & Pin**: Opening the Color Picker calls `AppearanceManager.BeginEditingSession()` (cloning `CurrentCommitted` into `Draft`) and `OverlayPinController.AcquirePin(ColorPickerPreview, PreviewMode)`.
2. **Keep Alive & Focus Isolation**: Normal mouse-leave auto-collapse is disabled while pinned; the island maintains `WS_EX_NOACTIVATE` so keyboard focus remains cleanly inside the Settings/Color Picker dialog.
3. **Collapsed / Expanded Preview Toggle**: User switches `PreviewMode` (`Collapsed` ↔ `Expanded`) inside the Color Picker dialog to verify both states using the live `Draft` tokens without committing to disk.
4. **Real-Time Scrubbing**: Every color change mutates `Draft` in memory, triggers `ContrastAuditReport` recalculation, and updates WPF `DynamicResource` brushes on both the real Dynamic Island and Settings miniature card at VSync cadence (`0` database/JSON writes).
5. **Non-Blocking Advisory Check**: If contrast falls below `4.5:1` (text) or `3.0:1` (icons), a non-blocking warning banner appears alongside **`Restore Recommended Contrast`**, while **`Apply / OK` remains enabled**.
6. **Resolution on Close**:
   - **Apply / OK**: Normalizes `#RRGGBB`, runs atomic write-replace to `appearance.json` + updates `recentColors[]` in `presets.json`, releases `PinToken`, and restores normal auto-collapse.
   - **Cancel / Esc / Unexpected Close**: Discards `Draft`, emits `RenderedAppearanceChanged(CurrentCommitted)` so the real island snaps back cleanly, releases `PinToken`, and restores normal auto-collapse.
