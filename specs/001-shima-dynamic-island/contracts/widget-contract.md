# Contract: Widget Module & Registry (`Shima.Core`)

**Contract Scope**: Standardized lifecycle, metadata, configuration, fault isolation, and extensibility boundary for all 8 Shima widgets (`Clock`, `Now Playing`, `Focus Timer`, `Notes`, `Inbox`, `Clipboard`, `Volume Mixer`, `Developer Activity`) and any future 9th+ widget.

---

## 1. Standardized Widget Contract (`IWidgetModule`)

```text
interface IWidgetModule
  readonly WidgetDescriptor Metadata
  Task InitializeAsync(IWidgetContext context, CancellationToken ct)
  Task ActivateAsync(WidgetActivationReason reason)      // Widget selected/visible in Expanded Island
  Task DeactivateAsync()                                  // User switches away or Island collapses
  void OnShellStateChanged(IslandShellState newState)     // Collapsed | Active | Notification | Expanded | Fullscreen | Suspended
  void OnPowerProfileChanged(PowerProfile profile)        // AC | Battery | EnergySaver | DisplayOff
  object CreateExpandedView()                             // Presentation DataTemplate / ViewModel root
  object? CreateCompactActiveView()                       // Optional compact pill telemetry view
  object CreateSettingsView()                             // Settings sidebar / widget config panel
  Task ResetSettingsAsync()                               // Resets ONLY this widget's isolated settings
  ValueTask DisposeAsync()                                // Unsubscribes all event bus & OS hooks
```

### 1.1 Metadata (`WidgetDescriptor`)

| Field | Type | Validation / Invariant |
| :--- | :--- | :--- |
| `WidgetId` | `string` | Immutable reverse-DNS slug (`shima.clock`, `shima.music`, `shima.focus`, `shima.notes`, `shima.inbox`, `shima.clipboard`, `shima.mixer`, `shima.developer`). |
| `DisplayName` | `string` | Localized human-readable title (`1–32` chars). |
| `DefaultOrder` | `int` | Default 0-based slot index (`0..7`). |
| `RequiresBackgroundSubscription` | `bool` | True for event-driven background listeners (`Now Playing`, `Inbox`, `Clipboard`, `Developer`); false for view-only (`Clock`). |
| `SupportsCompactActiveState` | `bool` | Whether the widget can populate the `Active` collapsed pill badge. |
| `SupportsNotificationPeek` | `bool` | Whether the widget may request transient `Notification` previews. |
| `DefaultAccentHex` | `string` | Valid `#RRGGBB` fallback when per-widget accents are enabled. |

---

## 2. Widget Context (`IWidgetContext`)

Injected once per widget during `InitializeAsync` so widgets NEVER resolve singletons or OS handles directly:

```text
interface IWidgetContext
  string WidgetId
  IEventDispatcher Events           // Scoped pub/sub subscription registry (auto-cleaned on Dispose)
  IWidgetSettingsStore Settings     // Isolated JSON read/write + Reset for this WidgetId only
  IIslandAttentionRequester Island  // RequestPeekNotification(priority, ttl) / UpdateCompactPill(badge)
  IThemeTokenProvider Theme         // Live reactive theme tokens + per-widget accent lookup
  ILogger Logger                    // Redacted, PII-free structured logger
```

---

## 3. Lifecycle & Fault Isolation Rules

1. **Initialization**: `WidgetRegistry` initializes only **enabled** widgets (`widget_configs.is_enabled == 1`). Disabled widgets are never initialized and hold zero OS/event subscriptions.
2. **Dynamic Enable/Disable**: Toggling a widget OFF in Settings calls `DeactivateAsync()` → `DisposeAsync()` immediately, drops its dot from the vertical carousel, and persists `is_enabled = 0`. Toggling ON instantiates and calls `InitializeAsync()`.
3. **Activation & Deactivation**:
   - Views are lazy-instantiated on first `ActivateAsync()`.
   - When the Island transitions to `Collapsed`, `Fullscreen`, or `Suspended`, the active widget receives `DeactivateAsync()` and MUST stop all UI timers/storyboards (e.g., Clock minute ticks, Now Playing timeline interpolation).
4. **Fault Containment (`Circuit Breaker`)**:
   - Every widget lifecycle call (`Initialize`, `Activate`, `CreateExpandedView`, event callback) executes inside a guarded try/catch boundary owned by `WidgetRegistry`.
   - If a widget throws an unhandled exception, `WidgetRegistry` marks that widget `Faulted`, logs the sanitized stack trace, emits a safe `FaultedWidgetFallbackView` with a **"Reload Widget"** action, and isolates the failure from the Island shell and the remaining 7 widgets.
5. **Open-Closed Extensibility (Adding Future Widgets)**:
   - New widgets implement `IWidgetModule`, supply a unique `WidgetId`, register in the DI composition root (`Shima.App`), and append a default `widget_configs` row if absent.
   - Zero edits are required to `IslandStateManager`, `OverlayPinController`, `AppearanceManager`, or existing widgets.
