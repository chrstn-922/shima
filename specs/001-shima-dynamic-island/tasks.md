# Tasks: Shima — Windows Dynamic Island Desktop Companion

**Input**: Design documents from `specs/001-shima-dynamic-island/` and `docs/blueprint/`  
**Prerequisites**: [plan.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/plan.md) (required), [spec.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/spec.md) (required), [research.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/research.md), [data-model.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/data-model.md), [contracts/](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/contracts/widget-contract.md)

**Tests**: Unit, contract, persistence, OS-adapter, and FlaUI UI tests are included across every story phase per `spec.md` Section 11.J, `test-strategy.md`, and Constitution Principle VI.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Maps to User Stories in [spec.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/spec.md) (`US1`–`US7`)

---

## Phase 1: Setup (Solution & Phase 0 API Validation)

**Purpose**: Validate Windows OS API assumptions and initialize the 5-layer .NET 8 solution structure without circular dependencies.

- [X] T001 Run Phase 0 API validation spikes (borderless fullscreen detection, `MagSetFullscreenColorEffect` multi-monitor/HDR, Sparse MSIX `UserNotificationListener` identity) and record results in `specs/001-shima-dynamic-island/research.md`
- [X] T002 Create `Shima.sln` and modular projects (`src/Shima.Core/`, `src/Shima.WindowsInterop/`, `src/Shima.Infrastructure/`, `src/Shima.Presentation/`, `src/Shima.App/`, `src/Shima.AgentBridge/`, and `src/Shima.Widgets/*`) per `specs/001-shima-dynamic-island/plan.md`
- [X] T003 [P] Configure `Directory.Build.props`, `net8.0-windows10.0.22621.0` target, nullable reference types, `PerMonitorV2` `src/Shima.App/app.manifest`, and `CsWin32` `NativeMethods.txt` in `src/Shima.WindowsInterop/`
- [X] T004 [P] Create test projects (`tests/Shima.Core.Tests/`, `tests/Shima.Infrastructure.Tests/`, `tests/Shima.WindowsInterop.Tests/`, `tests/Shima.Widgets.Tests/`, `tests/Shima.UI.Tests/`) with `xUnit`, `FluentAssertions`, and `NSubstitute`

---

## Phase 2: Foundational (Core Abstractions, Persistence & Registry)

**Purpose**: Core contracts, local storage, logging, and widget orchestration required before ANY user story can execute.

- [X] T005 Define core contracts (`IWidgetModule`, `IWidgetContext`, `IEventDispatcher`, `IIslandStateManager`, `IOverlayPinController`) in `src/Shima.Core/Contracts/` per `contracts/widget-contract.md` and `contracts/windows-adapters-contract.md`
- [X] T006 [P] Implement SQLite WAL database initializer, NTFS user-ACL directory setup, and `schema_migrations` (`schema_migrations.version`, `schema_migrations.applied_at_utc` per `data-model.md`) in `src/Shima.Infrastructure/Persistence/SqliteMigrationRunner.cs`
- [X] T007 [P] Implement `AtomicConfigStore<T>` with corrupt-file `.corrupt.<timestamp>.json` backup recovery, DPAPI `DpapiSecretStore`, and sanitized `SanitizedFileLogger` in `src/Shima.Infrastructure/Storage/`
- [X] T008 Implement `WidgetRegistry` with lifecycle management, `widget_configs` persistence (`widget_configs.widget_id`, `widget_configs.is_enabled`, `widget_configs.sort_order`, `widget_configs.allow_peek_notifications`, `widget_configs.settings_json`, `widget_configs.updated_at_utc` per `data-model.md`), and circuit-breaker fault isolation in `src/Shima.Core/Widgets/WidgetRegistry.cs`
- [X] T009 [P] Implement `AppPreferences` loader/validator (`AppPreferences.hoverExpandDelayMs`, `AppPreferences.autoCollapseDelayMs`, `AppPreferences.targetMonitorMode`, `AppPreferences.fullscreenBreakthroughAlerts`, `AppPreferences.clipboardMaxItems`, `AppPreferences.clipboardRetentionDays`, `AppPreferences.inboxRetentionDays`, `AppPreferences.claudeCodeOptInEnabled`, `AppPreferences.openCodeOptInEnabled`, `AppPreferences.openCodeEndpointUrl`, `AppPreferences.updatesOfflineMode`, `AppPreferences.licenseState` per `data-model.md`) in `src/Shima.Infrastructure/Configuration/AppPreferencesStore.cs`
- [X] T010 [P] Write foundational unit & persistence tests covering `WidgetRegistry` fault containment, WAL migrations, and corrupt-config recovery in `tests/Shima.Core.Tests/WidgetRegistryTests.cs` and `tests/Shima.Infrastructure.Tests/AtomicConfigStoreTests.cs`

**Checkpoint**: Foundation ready — user story phases can execute in priority order or parallel where independent.

---

## Phase 3: User Story 1 — Dynamic Island Shell & Context-Aware State Transitions (Priority: P1) 🎯 MVP

**Goal**: Deliver the top-center non-activating concave Dynamic Island window with deterministic state transitions, pinning arbiter, fullscreen complete-hide (`FR-SHELL-007`), Per-Monitor DPI v2, maximized coexistence, and zero idle CPU.

**Independent Test**: Launch Shima across single/multi-monitor DPI setups (`100%–200%`); verify collapsed position (`0.0%` idle CPU), non-activating focus preservation, fullscreen hide + global hotkey summon, and maximized window click-through modifier.

### Tests for User Story 1

- [X] T011 [P] [US1] Write unit tests for `IslandStateManager` and `OverlayPinController` priority arbitration (`SystemSuspended > ExplicitPin > FullscreenHardHide > HotkeySummon > Expanded > Peek > Active > Collapsed`) in `tests/Shima.Core.Tests/IslandVisibilityArbiterTests.cs`
- [X] T012 [P] [US1] Write interop tests for monitor fallback, `WM_DPICHANGED` geometry calculation, and `WS_EX_NOACTIVATE` style preservation in `tests/Shima.WindowsInterop.Tests/OverlayWindowAdapterTests.cs`

### Implementation for User Story 1

- [X] T013 [P] [US1] Implement `OverlayWindowAdapter` (`WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, `SetInteractiveFocusMode`, `Alt`+hover `WS_EX_TRANSPARENT` click-through) in `src/Shima.WindowsInterop/Overlay/OverlayWindowAdapter.cs`
- [X] T014 [P] [US1] Implement `DisplayTopologyMonitor`, `FullscreenDetector` (`SetWinEventHook` + `SHQueryUserNotificationState` + rect check), `GlobalHotkeyService`, and `PowerAndMotionMonitor` (`GUID_POWER_SAVING_STATUS`, `UISettings.AnimationsEnabled`) in `src/Shima.WindowsInterop/System/`
- [X] T015 [US1] Implement `IslandStateManager` and `OverlayPinController` (`AcquirePin` / `ReleasePin`) in `src/Shima.Core/Shell/`
- [X] T016 [US1] Build WPF `IslandWindow`, concave top-fillet `StreamGeometry` renderer, 8-dot vertical carousel (`WidgetHost`), and Reduced-Motion-aware morph animations in `src/Shima.Presentation/Shell/`
- [X] T017 [P] [US1] Implement `StartupRegistrationAdapter` (`StartupTask` + `HKCU\...\Run` fallback), single-instance mutex, and System Tray icon menu in `src/Shima.App/`
- [X] T018 [US1] Add automated FlaUI shell checks verifying focus preservation (`GetForegroundWindow` unchanged) and fullscreen hide behavior in `tests/Shima.UI.Tests/IslandShellUiTests.cs`

**Checkpoint**: Core Dynamic Island shell operates independently at `0.0%` idle CPU with full DPI, fullscreen, and maximized-window compliance.

---

## Phase 4: User Story 6 — Advanced Color Picker, Settings Shell & Appearance System (Priority: P1) 🎯 MVP

**Goal**: Deliver the 10-section Settings window, 6-token Island Appearance system, and Photoshop 22.3.0-inspired Advanced Color Picker with real-time pinned Live Preview, Collapsed/Expanded preview toggle, non-blocking WCAG contrast advisories, `Restore Recommended Contrast`, and slot-scoped `Reset to Default` (`FR-PICKER-006`).

**Independent Test**: Execute `CP-01`..`CP-08` from `test-strategy.md`: drag 2D field & Hue slider, verify bidirectional `HEX`/`RGB`/`HSB` sync, verify real-time island pinning without focus theft, save a low-contrast combination despite advisory warning, verify `Cancel` rollback, and restart persistence.

### Tests for User Story 6

- [X] T019 [P] [US6] Write unit tests for `ColorProcessingService` (`HSB↔RGB↔HEX` rounding, `#RGB` shorthand, hue preservation at `S=0`/`V=0`, invalid clamping) in `tests/Shima.Core.Tests/ColorProcessingServiceTests.cs`
- [X] T020 [P] [US6] Write unit tests for WCAG 2.1 luminance/contrast calculation (`4.5:1` text / `3.0:1` icon), non-blocking `ContrastAuditReport`, and `SolveRecommendedForeground` in `tests/Shima.Core.Tests/ContrastAuditTests.cs`
- [X] T021 [P] [US6] Write persistence & session isolation tests for `AppearanceManager`, `PresetManager` (6 built-ins + custom CRUD + 12-slot `recentColors`), slot-scoped `ResetSlotToDefault`, and `Cancel` rollback in `tests/Shima.Infrastructure.Tests/AppearancePersistenceTests.cs`

### Implementation for User Story 6

- [X] T022 [P] [US6] Implement `ColorProcessingService` and `ContrastAuditor` (`IColorProcessingService`) in `src/Shima.Infrastructure/Color/ColorProcessingService.cs` per `docs/blueprint/color-picker-architecture.md` and `docs/blueprint/color-contrast-validation.md`
- [X] T023 [US6] Implement `AppearanceManager`, `AppearanceEditingSession`, and `PresetManager` persisting `AppearanceConfiguration.activePresetId`, `AppearanceConfiguration.linkCollapsedAndExpanded`, `AppearanceConfiguration.usePerWidgetAccents`, `AppearanceConfiguration.collapsedTokens`, `AppearanceConfiguration.expandedTokens`, `AppearanceConfiguration.glowOpacity`, `AppearanceConfiguration.borderThicknessDip`, `AppearanceConfiguration.perWidgetAccents`, `PresetCatalog.customPresets`, `ColorPreset.name`, `PresetCatalog.savedSwatches`, `SavedColorSwatch.label`, `SavedColorSwatch.hex`, and `PresetCatalog.recentColors` per `data-model.md` in `src/Shima.Core/Appearance/` and `src/Shima.Infrastructure/Appearance/`
- [X] T024 [P] [US6] Build `ThemeRenderer` (`DynamicResource` brush binding + `CompositionTarget.Rendering` VSync live preview coalescing) in `src/Shima.Presentation/Theming/ThemeRenderer.cs`
- [X] T025 [US6] Build `ColorSelectionField` (pre-allocated `256×256` `WriteableBitmap` + keyboard arrow navigation), `HueSpectrumSlider` (`0–360°`), `ColorPreviewPanel` (New vs Current), and synchronized `HexInput`/`RgbInput`/`HsbInput` controls in `src/Shima.Presentation/ColorPicker/`
- [X] T026 [US6] Build `ColorPickerDialog` wiring `OverlayPinController.AcquirePin`, Collapsed/Expanded preview toggle, non-blocking contrast warning + `Restore Recommended Contrast` button, always-enabled `Apply / OK`, slot-scoped `Reset to Default`, and `Cancel`/`finally` cleanup in `src/Shima.Presentation/ColorPicker/ColorPickerDialog.xaml.cs`
- [X] T027 [US6] Build the 10-section `SettingsWindow` (`General` with diagnostic log verbosity/folder/clear controls per `FR-SYS-001`, `Widgets` enable/reorder/reset, `Notifications` with per-app exclusion list & `AppPreferences.inboxRetentionDays`, `Clipboard` with `AppPreferences.clipboardMaxItems` & `AppPreferences.clipboardRetentionDays`, `Focus Timer`, `Clock`, `Developer Integrations` with `AppPreferences.openCodeEndpointUrl` & optional DPAPI-backed `AppPreferences.openCodeServerPassword` per `data-model.md`, `Appearance & Customization` with `Reset All Appearance` & live miniature preview card, `Updates`, `License`) in `src/Shima.Presentation/Settings/`
- [X] T028 [US6] Write FlaUI automated tests covering `CP-03` through `CP-08` (pinning, preview toggle, non-blocking low-contrast save, `Cancel` rollback, `Restore Recommended Contrast`) in `tests/Shima.UI.Tests/ColorPickerUiTests.cs`

**Checkpoint**: Settings and precision Color Picker customization are complete, accessible, and persist across restarts.

---

## Phase 5: User Story 2 — World Clock & Timezone Management (Priority: P1) 🎯 MVP

**Goal**: Deliver the flagship two-column Clock widget matching reference Image 2 layout with local/world city switching, timezone catalog management, and 12h/24h formatting.

**Independent Test**: Expand Island to Clock, switch active cities (`Manila`, `London`, `New York`, `Tokyo`), add/remove/reorder cities in Settings, toggle 12h/24h format, and verify minute-boundary timer suspends on collapse.

### Tests for User Story 2

- [X] T029 [P] [US2] Write unit tests for `ClockWidget` timezone/DST conversion, 12h/24h formatting, and timer suspension on `DeactivateAsync` in `tests/Shima.Widgets.Tests/ClockWidgetTests.cs`

### Implementation for User Story 2

- [X] T030 [P] [US2] Implement `ClockRepository` persisting `WORLD_CLOCK_LOCATION.location_id`, `WORLD_CLOCK_LOCATION.city_name`, `WORLD_CLOCK_LOCATION.country_name`, `WORLD_CLOCK_LOCATION.windows_tz_id`, `WORLD_CLOCK_LOCATION.iana_tz_id`, `WORLD_CLOCK_LOCATION.sort_order`, and `WORLD_CLOCK_LOCATION.is_primary` per `data-model.md` in `src/Shima.Infrastructure/Repositories/ClockRepository.cs`
- [X] T031 [US2] Implement `ClockWidgetModule` (`shima.clock`) with minute-aligned timer and `WM_TIMECHANGE` subscription in `src/Shima.Widgets/Shima.Widgets.Clock/`
- [X] T032 [US2] Build two-column Clock view (Date, `HeroMetric` time, `City · UTC+offset`, city selector list, `⚙` settings trigger) and Clock Settings panel in `src/Shima.Widgets/Shima.Widgets.Clock/Views/`

---

## Phase 6: User Story 3 — Now Playing Media Control & Volume Mixer (Priority: P1)

**Goal**: Deliver real-time GSMTC media transport/artwork controls and CoreAudio WASAPI bidirectional per-application volume mixing.

**Independent Test**: Play media in browser/player and run a second audio app; verify compact `Active` pill badge, playback/seek controls with unsupported-seek fallback, and real-time bidirectional per-app volume/mute sync across device hot-plugs.

### Tests for User Story 3

- [X] T033 [P] [US3] Write unit/contract tests for `MediaSessionAdapter` (missing artwork/seek fallback) and `AudioMixerAdapter` (session churn 150ms debounce, device switch) in `tests/Shima.WindowsInterop.Tests/MediaAndAudioTests.cs`

### Implementation for User Story 3

- [X] T034 [P] [US3] Implement `MediaSessionAdapter` (WinRT `GSMTC` events, `IsPlaybackPositionEnabled` guard, thumbnail stream decoding) in `src/Shima.WindowsInterop/Media/MediaSessionAdapter.cs`
- [X] T035 [P] [US3] Implement `AudioMixerAdapter` using `NAudio.Wasapi` CoreAudio wrappers (MTA COM `IAudioSessionManager2`, `ISimpleAudioVolume`, `IMMNotificationClient`, app icon resolver) in `src/Shima.WindowsInterop/Audio/AudioMixerAdapter.cs`
- [X] T036 [US3] Implement `NowPlayingWidgetModule` (`shima.music`, including compact `Active` pill badge & expanded view) in `src/Shima.Widgets/Shima.Widgets.NowPlaying/`
- [X] T037 [US3] Implement `VolumeMixerWidgetModule` (`shima.mixer`, master output + per-app sliders & mute toggles) in `src/Shima.Widgets/Shima.Widgets.VolumeMixer/`

---

## Phase 7: User Story 4 — Clipboard History, Quick Notes & Notification Inbox (Priority: P1)

**Goal**: Deliver 100% local workspace capture across Notes (debounced auto-save), Clipboard (`WM_CLIPBOARDUPDATE` + password-manager exclusions), and Inbox (`UserNotificationListener` Sparse Package integration with bidirectional Action Center dismissal & graceful guidance card).

**Independent Test**: Copy text/image + mock excluded password entry; create/search/edit notes; trigger Windows toast notifications; verify local SQLite persistence, bidirectional Action Center dismissal toggle, and `0` outbound network sockets.

### Tests for User Story 4

- [X] T038 [P] [US4] Write tests for `NoteRepository` (200KB cap), `ClipboardRepository` (`content_hash` dedup, retention pruning), and `InboxRepository` in `tests/Shima.Infrastructure.Tests/ProductivityRepositoriesTests.cs`
- [X] T039 [P] [US4] Write interop & widget tests for clipboard exclusion format filtering, self-copy suppression loop guard, DND popup suppression, and `IdentityUnavailable` Inbox fallback in `tests/Shima.Widgets.Tests/ProductivityWidgetsTests.cs`

### Implementation for User Story 4

- [X] T040 [P] [US4] Implement `NoteRepository` (`NOTE_ENTRY.note_id`, `NOTE_ENTRY.title`, `NOTE_ENTRY.content_text`, `NOTE_ENTRY.is_pinned`, `NOTE_ENTRY.created_at_utc`, `NOTE_ENTRY.updated_at_utc` per `data-model.md`), `ClipboardRepository` (`CLIPBOARD_ENTRY.entry_id`, `CLIPBOARD_ENTRY.content_type`, `CLIPBOARD_ENTRY.text_content`, `CLIPBOARD_ENTRY.content_hash`, `CLIPBOARD_ENTRY.source_app_name`, `CLIPBOARD_ENTRY.byte_size`, `CLIPBOARD_ENTRY.is_pinned`, `CLIPBOARD_ENTRY.copied_at_utc` per `data-model.md`), and `InboxRepository` (`INBOX_NOTIFICATION.notification_id`, `INBOX_NOTIFICATION.os_toast_id`, `INBOX_NOTIFICATION.app_id`, `INBOX_NOTIFICATION.app_name`, `INBOX_NOTIFICATION.title`, `INBOX_NOTIFICATION.body_text`, `INBOX_NOTIFICATION.is_dismissed`, `INBOX_NOTIFICATION.received_at_utc` per `data-model.md`) in `src/Shima.Infrastructure/Repositories/`
- [X] T041 [P] [US4] Implement `ClipboardMonitorAdapter` (`AddClipboardFormatListener`, exclusion format checks, 3-retry `OpenClipboard`) and `NotificationListenerAdapter` (`UserNotificationListener`, `RemoveNotification`, `IdentityUnavailable` detection) in `src/Shima.WindowsInterop/`
- [X] T042 [US4] Implement `NotesWidgetModule` (`shima.notes`, 500ms debounced save, focus-safe island lock, search) in `src/Shima.Widgets/Shima.Widgets.Notes/`
- [X] T043 [US4] Implement `ClipboardWidgetModule` (`shima.clipboard`, text/image preview, recopy suppression, pin/pause/clear) in `src/Shima.Widgets/Shima.Widgets.Clipboard/`
- [X] T044 [US4] Implement `InboxWidgetModule` (`shima.inbox`, per-app grouping, `AppPreferences.syncInboxDismissalWithActionCenter` bidirectional dismissal per `data-model.md`, non-native popup notice & access-guidance view) in `src/Shima.Widgets/Shima.Widgets.Inbox/`

---

## Phase 8: User Story 5 — Focus Timer & Zen Mode with Safe Display Restoration (Priority: P2)

**Goal**: Deliver Pomodoro/custom focus sessions with local DSP ambient noise (`White`, `Pink`, `Brown`) and fail-safe DWM Magnification grayscale/dimming that deterministically restores display state on stop, exit, or crash recovery.

**Independent Test**: Run Focus + Zen Mode (audio + grayscale/dim), verify `Active` pill countdown, terminate process abruptly (`taskkill /F`), and verify display returns to normal immediately and `runtime-journal.json` recovers cleanly on relaunch.

### Tests for User Story 5

- [X] T045 [P] [US5] Write unit tests for Pomodoro phase state machine, DSP noise generators, and `runtime-journal.json` pre-effect crash-recovery flagging in `tests/Shima.Widgets.Tests/FocusTimerWidgetTests.cs`

### Implementation for User Story 5

- [X] T046 [P] [US5] Implement `FocusRepository` (`FOCUS_SESSION.session_id`, `FOCUS_SESSION.phase_type`, `FOCUS_SESSION.planned_seconds`, `FOCUS_SESSION.completed_seconds`, `FOCUS_SESSION.zen_noise_type`, `FOCUS_SESSION.screen_effect_used`, `FOCUS_SESSION.started_at_utc` per `data-model.md`) in `src/Shima.Infrastructure/Repositories/FocusRepository.cs`
- [X] T047 [P] [US5] Implement `ZenAudioSynthAdapter` (`NAudio.Wasapi` `WasapiOut` DSP white/pink/brown noise) and `ZenDisplayEffectAdapter` (`MagSetFullscreenColorEffect` + click-through dim overlay + `runtime-journal.json` startup watchdog) in `src/Shima.WindowsInterop/Zen/`
- [X] T048 [US5] Implement `FocusTimerWidgetModule` (`shima.focus`, Pomodoro/custom intervals, compact `Active` countdown, Zen controls & Settings) in `src/Shima.Widgets/Shima.Widgets.FocusTimer/`

---

## Phase 9: User Story 7 — Developer Activity Widget for Coding Agents (Priority: P2 — Phase 2)

**Goal**: Deliver `ICodingAgentAdapter` and independent, strictly opt-in, fail-open integrations for **Claude Code** (Named Pipe + `Shima.AgentBridge.exe`) and **OpenCode** (documented SSE/REST or plugin hook listener), normalizing concurrent sessions cleanly into four unified states (`Working`, `NeedsAttention`, `AwaitingApproval`, `Finished`) with confirmed-only `Allow`/`Deny`, reconnect/missed-event recovery, and explicit reporting of unsupported OpenCode capabilities.

**Independent Test**: Run `Shima.AgentBridge.exe` with Shima closed (`exit 0` empty stdout `< 50 ms`); exercise `OpenCodeAdapter` against mocked/loopback SSE+REST and plugin endpoints to verify all 4 unified states, multi-session isolation, reconnect/dedup/snapshot recovery, confirmed-only `Allow`/`Deny` (no simulated approval), unsupported-capability hiding, and independent parallel operation alongside `ClaudeCodeAdapter`.

### Tests for User Story 7

- [X] T049 [P] [US7] Write unit and contract tests for `ClaudeCodeAdapter` / `Shima.AgentBridge` (fail-open timeout/closed-pipe `exit 0`, `CurrentUserSid` Named Pipe ACL), `OpenCodeAdapter` (loopback-only URI validation, optional DPAPI server password auth header + HTTP `401` `AuthenticationRequired` health diagnostic, 4-state mapping from documented SSE/REST/plugin events incl. `IsFatalError`, multi-session isolation, reconnect backoff + duplicate dedup + REST snapshot missed-event recovery + inactive expiry, confirmed-only single-action `once`/`reject` `Allow`/`Deny` rejecting `always` and simulated/bypassed approval, unsupported capability degradation, and fault/toggle independence from `ClaudeCodeAdapter`), AND `DeveloperWidgetModule` view-model UI states (opt-in collapsed notch bar visibility & compact-pill coexistence precedence `Awaiting approval / Needs attention` > Now Playing / Focus Timer, all 5 semantic bar visual states, `10s` `Finished` bar dwell / `15m` expanded retention & `Error / Failed` acknowledge cap with idle timer suspension, auto-open **Developer Activity** vs `Idle` default routing, non-approval row-click `FocusTerminal`, multi-session row formatting & conditional inline `Allow`/`Deny` visibility, and `"No active sessions"` empty state) in `tests/Shima.WindowsInterop.Tests/CodingAgentAdaptersTests.cs`

### Implementation for User Story 7

- [X] T050 [P] [US7] Implement `Shima.AgentBridge` standalone CLI executable (`stdin` JSON → user-ACL Named Pipe `\\.\pipe\shima-agent-bridge` → `stdout` `permissionDecision` or fail-open empty `exit 0`) in `src/Shima.AgentBridge/`
- [X] T051 [P] [US7] Implement `ClaudeCodeAdapter` (opt-in `~/.claude/settings.json` hook installer/uninstaller with timestamped backup + Named Pipe server returning confirmed `PermissionActionResult`, strictly independent of OpenCode) in `src/Shima.WindowsInterop/Agents/ClaudeCodeAdapter.cs`
- [X] T052 [P] [US7] Implement independent `OpenCodeAdapter` (validating loopback-only `AppPreferences.openCodeEndpointUrl`, optional `AppPreferences.openCodeServerPassword` [`dpapiEncryptedBlob?` per `data-model.md`] via `DpapiSecretStore` with HTTP `401` → `AdapterHealthStatus.AuthenticationRequired`, documented opt-in SSE `GET /global/event` + REST reconciliation `GET /session` / `GET /session/status` + optional plugin bridge, 4-state mapping to `Working`/`NeedsAttention`[`IsFatalError`]/`AwaitingApproval`/`Finished`, reconnect exponential backoff, duplicate event dedup, missed-event snapshot recovery, `10s`/`15m` dwell & inactive expiry support, multi-session `opencode:<id>` isolation, confirmed-only `once`/`reject` permission reply, graceful disconnected/unavailable states, and explicit `UnsupportedCapabilities` reporting) in `src/Shima.WindowsInterop/Agents/OpenCodeAdapter.cs`
- [X] T053 [US7] Implement `AgentSessionLogRepository` (`AGENT_SESSION_LOG.session_id`, `AGENT_SESSION_LOG.adapter_type`, `AGENT_SESSION_LOG.workspace_path`, `AGENT_SESSION_LOG.process_id`, `AGENT_SESSION_LOG.last_status`, `AGENT_SESSION_LOG.started_at_utc`, `AGENT_SESSION_LOG.last_seen_at_utc` per `data-model.md`) and `DeveloperWidgetModule` (`shima.developer` implementing opt-in collapsed notch summary status bar with compact-pill coexistence precedence and semantic theme tokens [zero hardcoded hex] across `Idle`/`Working`/`Awaiting approval or Needs attention`/`Finished`/`Error or Failed`, `10s` `Finished` bar dwell / `15m` expanded retention & `Error / Failed` acknowledge cap with idle timer suspension, auto-open **Developer Activity** vs `Idle` default widget routing, expanded pinned summary bar + multi-session row list with agent icon, bold task title, muted repo subtitle, right-aligned color-coded state label, elapsed/relative timestamp, non-approval row click → `FocusTerminal`, strictly single-action `once`/`reject` inline `Allow`/`Deny` on supported `Awaiting approval` rows, `"No active sessions"` empty state, and Settings health/explicit unsupported-capability matrix) in `src/Shima.Widgets/Shima.Widgets.Developer/`

### Acceptance Criteria for User Story 7 (OpenCode & Developer Activity Verification Gate)

- [X] OpenCode activity is detected reliably via OpenCode's supported documented event system, local server SSE/REST APIs (unauthenticated loopback by default or optional DPAPI-encrypted password with explicit `Authentication required` HTTP `401` diagnostic), or documented plugin hooks without port scanning or terminal scraping.
- [X] Supported OpenCode events deterministically map into all four unified Developer Activity states (`Working`, `Needs attention`, `Awaiting approval`, `Finished`).
- [X] Pending permission requests are detected with accurate session/task context, and inline **Allow** (strictly `once`, never `always` implicitly) / **Deny** (`reject`) controls are exposed **only** when supported by OpenCode and reflect the outcome **only after** OpenCode confirms the action (never simulated or bypassed).
- [X] Multiple concurrent active sessions remain cleanly isolated without state or permission mix-ups, and disconnected/unavailable sessions degrade gracefully.
- [X] Listener handles reconnects, duplicate events, missed events (via snapshot reconciliation), and inactive session cleanup without false `Finished` transitions.
- [X] `OpenCodeAdapter` and `ClaudeCodeAdapter` run independently in Shima's shared Developer Activity model without cross-adapter coupling or shared failure modes.
- [X] Collapsed notch status bar displays whenever Claude Code or OpenCode is opted in (`Awaiting approval / Needs attention` > Now Playing / Focus Timer coexistence priority; `Awaiting approval / Needs attention > Working > Finished > Idle`) across all 5 semantic-token visual states (zero hardcoded hex), holds `Finished` for `10s` (or hover/open) and `Error / Failed` until hovered/dismissed (`15m` cap) with idle timer suspension; expanding auto-opens **Developer Activity** when active (`Idle` opens default widget) with `15m` finished-row retention, non-approval row click → **Focus Terminal**, conditional inline `Allow`/`Deny`, and `"No active sessions"` empty state.

---

## Phase 10: Polish, Packaging, Security & Release Verification

**Purpose**: Cross-cutting packaging (`Velopack` + Sparse MSIX), optional offline-first Update/License adapters, and full release verification gates (`x64` & `ARM64`).

- [ ] T054 [P] Implement `UpdateServiceAdapter` (`AppPreferences.updatesOfflineMode` per `data-model.md` + SHA-256 manifest verification) and offline-first DPAPI-cached `LicenseServiceAdapter` (`AppPreferences.licenseState` per `data-model.md`) in `src/Shima.Infrastructure/Services/`
- [ ] T055 [P] Author Sparse Package `AppxManifest.xml` (`userNotificationListener` + `StartupTask`) and Velopack build/sign scripts for `win-x64` and `win-arm64` in `build/`
- [ ] T056 Execute full verification suite (`dotnet test`, 8-hour idle/memory soak profile, `0`-socket privacy check, multi-DPI & fullscreen walkthrough per `specs/001-shima-dynamic-island/quickstart.md` and `docs/blueprint/test-strategy.md`)

---

## Dependencies & Execution Order

### Story & Phase Dependencies

- **Phase 1 (Setup)** → **Phase 2 (Foundational)**: Blocks all user stories.
- **Phase 3 (`US1` Shell)** → Required before **Phase 4 (`US6` Live Preview Pinning)** and all visual widget host checks.
- **Phase 4 (`US6` Settings & Color Picker)** → Provides `SettingsWindow`, `ThemeRenderer`, and widget configuration hosts for `US2`–`US7`.
- **Phases 5 (`US2` Clock), 6 (`US3` Media/Mixer), 7 (`US4` Notes/Clipboard/Inbox), 8 (`US5` Focus/Zen)** → Independent widget modules that can proceed in parallel once Phase 4 completes.
- **Phase 9 (`US7` Developer Activity)** → Phase 2 milestone; depends on Phase 4 (`SettingsWindow`) and Phase 3 (`Notification` peek pipeline).
- **Phase 10 (Polish & Packaging)** → Depends on completion of target MVP (`US1`–`US6`) or Full (`US1`–`US7`) milestones.

### Parallel Opportunities

- Within **Phase 2**: `T006` (SQLite), `T007` (Atomic JSON/DPAPI), and `T009` (Preferences) run concurrently.
- Within **Phase 4 (`US6`)**: `T019`/`T020`/`T021` (tests) and `T022` (ColorService) + `T024` (ThemeRenderer) run in parallel before dialog wiring (`T026`).
- Across **Widgets (`US2`–`US5`)**: Separate module directories (`Shima.Widgets.Clock`, `NowPlaying`, `VolumeMixer`, `Notes`, `Clipboard`, `Inbox`, `FocusTimer`) allow full parallel team implementation.

### MVP Scope Recommendation

- **MVP Increment 1 (Core Shell & Customizer)**: Phases `1 → 2 → 3 (US1) → 4 (US6)` delivers the functional Dynamic Island shell, Settings, and Photoshop-inspired Color Picker.
- **MVP Increment 2 (Everyday 7 Widgets)**: Phases `5 (US2) → 6 (US3) → 7 (US4) → 8 (US5)` completes all 7 Phase 1 specification widgets.
- **Phase 2 Release (Developer Ecosystem)**: Phase `9 (US7)` adds opt-in fail-open Claude Code & OpenCode integrations, followed by Phase `10` distribution hardening.
