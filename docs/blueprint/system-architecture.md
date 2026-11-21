# Shima System Architecture & Behavioral Diagrams

**Document Role**: Master architectural breakdown across Shima's 5 decoupled layers (Presentation, Application Core, Windows Integration, Widget Modules, Infrastructure) and all 17 system, sequence, and data-flow diagrams.  
**Related**: [plan.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/plan.md) | [widget-contract.md](file:///C:/Users/chris/Projects/shima/specs/001-shima-dynamic-island/contracts/widget-contract.md)

---

## 1. Layered Responsibilities (Refined from Specification Section 2)

| Layer | Assembly | Responsibilities |
| :--- | :--- | :--- |
| **A. Presentation** | `Shima.Presentation` | Island Overlay Window, concave top-fillet vector renderer, 8-dot vertical carousel, `WidgetHost`, Settings Window (`10` sidebar sections), Hardware/Composition Animation Engine (reduced-motion aware), Theme Renderer, Advanced Color Picker Dialog, Live Appearance Preview binding. |
| **B. Application Core** | `Shima.Core` | `ApplicationLifecycleManager`, `WidgetRegistry`, `WidgetLifecycleManager`, `EventDispatcher` (typed pub/sub), `IslandStateManager`, `ConfigurationManager`, `BackgroundTaskCoordinator`, `AppearanceManager`, `AppearanceEditingSession`, `OverlayPinController`. |
| **C. Windows Integration** | `Shima.WindowsInterop` | `OverlayWindowAdapter`, `NotificationListenerAdapter` (`UserNotificationListener`), `MediaSessionAdapter` (`GSMTC`), `AudioMixerAdapter` (`CoreAudio`/`WASAPI`), `ClipboardMonitorAdapter` (`WM_CLIPBOARDUPDATE`), `StartupRegistrationAdapter`, `DisplayTopologyMonitor`, `FocusActivationHandler`, `FullscreenDetector`, `PowerAndMotionMonitor`, `ZenDisplayEffectAdapter` (`MagSetFullscreenColorEffect`). |
| **D. Widget Modules** | `Shima.Widgets.*` | 8 isolated `IWidgetModule` plugins: `Clock`, `NowPlaying`, `FocusTimer`, `Notes`, `Inbox`, `Clipboard`, `VolumeMixer`, and `Developer` (Phase 2: `ClaudeCodeAdapter` + `OpenCodeAdapter`). |
| **E. Infrastructure** | `Shima.Infrastructure` | `SqliteDatabase` (WAL mode + repositories), `AtomicConfigStore` (JSON + corrupt recovery), `DpapiSecretStore`, `SanitizedFileLogger`, `ColorProcessingService` (HSB/RGB/HEX + WCAG contrast), `PresetManager`, `UpdateServiceAdapter`, `LicenseServiceAdapter`. |

---

## 2. System Context & High-Level Component Architecture

### Diagram 1: System Context Diagram

```mermaid
flowchart LR
    User["Desktop User"] -->|"Click / Hotkey / Color Picker / Settings"| Shima["Shima Desktop Companion\n(Local-First Windows App)"]
    WinOS["Windows 10/11 OS\n(DWM, GSMTC, WASAPI, Action Center, Clipboard, Display)"] <-->|"Win32 / COM / WinRT Events"| Shima
    Agents["Local Coding Agents\n(Claude Code Hooks / OpenCode SSE)"] <-->|"Opt-In Named Pipe or Loopback IPC"| Shima
    LocalFS["Local Storage\n(%LOCALAPPDATA%\\Shima)"] <-->|"SQLite WAL + Atomic JSON + DPAPI"| Shima
    ExtNet["Optional Signed Update / License Endpoint"] <-.->|"Opt-In Only (0 Core Telemetry)"| Shima
```

### Diagram 2: High-Level Component Architecture

```mermaid
flowchart TD
    subgraph Pres["A. Presentation Layer"]
        IslandUI["Island Overlay Window & Concave Renderer"]
        WidgetView["Widget Viewport & 8-Dot Carousel"]
        SettingsUI["Settings Window (10 Sections)"]
        ColorPickerUI["Advanced Color Picker UI"]
        AnimSys["Composition Animation Engine"]
    end

    subgraph Core["B. Application Core"]
        AppLife["Lifecycle & Power Coordinator"]
        StateMgr["Island State Manager"]
        PinCtrl["Overlay Pin Controller"]
        Registry["Widget Registry & Fault Boundary"]
        EventBus["Typed Event Dispatcher"]
        AppearMgr["Appearance & Preview Session Manager"]
    end

    subgraph Widgets["D. Widget Module Layer"]
        W1["Clock"]
        W2["Now Playing"]
        W3["Focus Timer"]
        W4["Notes"]
        W5["Inbox"]
        W6["Clipboard"]
        W7["Volume Mixer"]
        W8["Developer (Phase 2)"]
    end

    subgraph WinAPI["C. Windows Integration Layer"]
        WinWin["Overlay, Focus, Display & Fullscreen"]
        WinNotif["UserNotificationListener"]
        WinMedia["GSMTC Media"]
        WinAudio["WASAPI CoreAudio"]
        WinClip["WM_CLIPBOARDUPDATE"]
    end

    subgraph Infra["E. Infrastructure Layer"]
        DB["SQLite WAL DB"]
        Cfg["Atomic JSON & DPAPI"]
        ColorSvc["Color & Contrast Service"]
        UpdLic["Update & License Adapters"]
    end

    Pres --> Core
    Widgets --> Core
    WinAPI --> Core
    Infra --> Core
    WidgetView -.-> Widgets
```

---

## 3. Core Lifecycle & Shell State Diagrams

### Diagram 3: Application Startup Sequence

```mermaid
sequenceDiagram
    participant OS as Windows User Session
    participant App as Shima.App (Composition Root)
    participant Journal as Runtime Journal & Zen Recovery
    participant Config as Atomic JSON & SQLite WAL
    participant Win as WindowsInterop Adapters
    participant Reg as WidgetRegistry
    participant Shell as Island Overlay Window

    OS->>App: Launch Executable (Single-Instance Mutex Check)
    App->>Journal: Check runtime-journal.json
    alt Previous Crash During Zen Display Filter
        Journal->>Win: Force MagUninitialize & Restore Display
    end
    App->>Config: Load settings.json, appearance.json, presets.json
    App->>Config: Open shima.db (WAL) & Run Forward Migrations
    App->>Win: Initialize DisplayTopology, FullscreenDetector, PowerMonitor
    App->>Shell: Create Non-Activating Topmost Window (Collapsed at Y=0)
    App->>Reg: Initialize Enabled Widgets (Lazy-Load Views)
    Reg->>Win: Bind Event Listeners (Media, Clipboard, Inbox, Audio)
```

### Diagram 4: Widget Lifecycle State Machine

```mermaid
stateDiagram-v2
    [*] --> Registered: DI Composition Root
    Registered --> Initialized: Enabled in settings (InitializeAsync)
    Registered --> Disabled: Disabled in settings (Zero Subscriptions)
    Disabled --> Initialized: User Enables in Settings

    Initialized --> Active: User Expands / Selects Widget (ActivateAsync)
    Active --> Initialized: Island Collapses or Switches Widget (DeactivateAsync)

    Initialized --> Faulted: Unhandled Exception Caught by Circuit Breaker
    Active --> Faulted: Unhandled Exception Caught by Circuit Breaker
    Faulted --> Initialized: User Clicks Reload Widget

    Initialized --> Disposed: User Disables Widget or App Exits (DisposeAsync)
    Active --> Disposed: App Exits
```

### Diagram 5: Island Expansion & Collapse State Flow

```mermaid
stateDiagram-v2
    [*] --> Collapsed: Startup (Zero Idle Timers)

    Collapsed --> Active: Media / Timer / Agent Working Badge
    Active --> Collapsed: Background Activity Ends

    Collapsed --> Notification: Toast / Agent Alert (Not Fullscreen & Not DND)
    Active --> Notification: Priority Alert
    Notification --> Collapsed: Peek TTL Expires (4s)
    Notification --> Expanded: User Clicks Preview

    Collapsed --> Expanded: Click / Hover / Hotkey / ColorPicker Pin
    Active --> Expanded: Click / Hotkey
    Expanded --> Collapsed: Esc / Click Outside / Leave Dwell (When Unpinned)

    Collapsed --> FullscreenHidden: Fullscreen App Detected (Y=0 Hit-Box Removed)
    Active --> FullscreenHidden: Fullscreen App Detected
    FullscreenHidden --> Collapsed: Fullscreen Exited
    FullscreenHidden --> Expanded: Global Hotkey Summon
```

---

## 4. Windows Integration & Widget Event Flows

### Diagram 6: Notification Handling Flow (`Inbox`)

```mermaid
sequenceDiagram
    participant OS as Windows Action Center
    participant Listener as UserNotificationListener
    participant Arbiter as IslandStateManager & DND Check
    participant Repo as InboxRepository (SQLite)
    participant UI as Island Notification / Inbox View

    OS->>Listener: NotificationChanged(Added, toastId)
    Listener->>Arbiter: Emit OsNotificationEventArgs
    Arbiter->>Repo: Persist Toast (If App Not Excluded)
    alt Fullscreen Active OR Windows DND Active OR Zen Mode Active
        Arbiter-->>UI: Suppress Popup (Silently Queued in Inbox)
    else Desktop Visible
        Arbiter->>UI: Show Notification Peek (4s TTL)
    end
    Note over UI,OS: User Dismisses in Shima Inbox
    UI->>Repo: MarkDismissedAsync(id)
    alt syncInboxDismissalWithActionCenter == true
        UI->>Listener: TryRemoveFromActionCenterAsync(osToastId)
    end
```

### Diagram 7: Media Session Handling (`Now Playing`)

```mermaid
sequenceDiagram
    participant Player as Spotify / Browser / VLC
    participant GSMTC as WinRT GSMTC Manager
    participant Adapter as MediaSessionAdapter
    participant Widget as NowPlayingWidget
    participant Shell as Island Shell

    Player->>GSMTC: Track / Playback State Updated
    GSMTC->>Adapter: CurrentSessionChanged / MediaPropertiesChanged
    Adapter->>Widget: Emit MediaSessionSnapshot
    alt Shell is Collapsed
        Widget->>Shell: UpdateCompactPill (Album Mini-Badge)
    else Shell is Expanded on Now Playing
        Widget->>Shell: Render Title, Artist, Artwork & Seek State
    end
    Note over Widget,Player: User Clicks Next / Play-Pause / Seek
    Widget->>Adapter: TrySkipNextAsync() / TryChangePlaybackPositionAsync()
    Adapter->>GSMTC: Forward Transport Command
```

### Diagram 8: Clipboard Collection & Retrieval (`Clipboard`)

```mermaid
sequenceDiagram
    participant User as Active Desktop App
    participant Win as Win32 WM_CLIPBOARDUPDATE
    participant ClipAdp as ClipboardMonitorAdapter
    participant Repo as ClipboardRepository (SQLite)
    participant Widget as ClipboardWidget

    User->>Win: Copies Text or Image (Ctrl+C)
    Win->>ClipAdp: Window Proc Message (Zero Polling)
    ClipAdp->>ClipAdp: Check ExcludeClipboardContentFromMonitorProcessing
    alt Marked Sensitive by Password Manager OR Monitoring Paused
        ClipAdp-->>ClipAdp: Discard Immediately
    else Safe & Within Size Budget
        ClipAdp->>Repo: Deduplicate SHA-256 & Save + Prune Retention Cap
        Repo-->>Widget: Refresh History List
    end
    Note over User,Widget: User Clicks Historical Entry
    Widget->>ClipAdp: SetClipboardPayload (Suppression Token Active)
    ClipAdp->>Win: Write to Clipboard (Skip Re-Capture Loop)
```

### Diagram 9: Audio Session Management (`Volume Mixer`)

```mermaid
sequenceDiagram
    participant App as Audio App (Browser/Game/Call)
    participant WASAPI as CoreAudio MTA Thread
    participant MixerAdp as AudioMixerAdapter
    participant Widget as VolumeMixerWidget

    App->>WASAPI: Opens Audio Stream
    WASAPI->>MixerAdp: IAudioSessionNotification::OnSessionCreated
    MixerAdp->>Widget: Emit SessionAdded(AppName, Icon, Vol, Mute)
    App->>WASAPI: External Volume/Mute Change in Windows
    WASAPI->>MixerAdp: IAudioSessionEvents::OnSimpleVolumeChanged
    MixerAdp->>Widget: Sync Slider & Mute State (< 200ms)
    Note over Widget,WASAPI: User Drags Slider in Shima
    Widget->>MixerAdp: SetSessionVolume(sessionId, scalar)
    MixerAdp->>WASAPI: ISimpleAudioVolume::SetMasterVolume
```

### Diagram 10: Developer Integration Event Flow (`Claude Code` & `OpenCode`)

```mermaid
sequenceDiagram
    participant Claude as Claude Code CLI (PreToolUse/PermissionRequest)
    participant Bridge as Shima.AgentBridge.exe
    participant Pipe as User-ACL Named Pipe
    participant OpenCode as OpenCode Server (Opt-In SSE)
    participant DevWidget as Developer Activity Widget

    Claude->>Bridge: Spawns Hook with stdin JSON
    Bridge->>Pipe: Connect (\\.\pipe\shima-agent-bridge)
    alt Shima Closed OR Pipe Unreachable OR Timeout
        Bridge-->>Claude: Exit 0 (Empty stdout = Fail-Open Terminal Prompt)
    else Shima Connected
        Pipe->>DevWidget: Snapshot(Status: AwaitingApproval)
        DevWidget-->>DevWidget: Show Island Prompt (Allow / Deny)
        alt User Clicks Allow or Deny
            DevWidget->>Pipe: Send Decision JSON
            Pipe->>Bridge: Decision Returned
            Bridge-->>Claude: Print permissionDecision JSON to stdout, Exit 0
        end
    end
    OpenCode-->>DevWidget: SSE Stream (permission.updated / session.idle)
    DevWidget->>OpenCode: Opt-In HTTP Permission Reply OR Focus Terminal
```

---

## 5. Persistence, Licensing & Color Appearance Flows

### Diagram 11: Local Persistence & Data Flow

```mermaid
flowchart LR
    Widgets["8 Widget Modules"] -->|"Notes / Clipboard / Inbox / Focus / Clocks"| SQLite[("shima.db\nSQLite WAL")]
    Settings["Settings & Appearance UI"] -->|"Atomic Write-Replace"| JSONFiles["settings.json\nappearance.json\npresets.json"]
    License["License Adapter"] -->|"DPAPI CurrentUser Encrypt"| JSONFiles
    Zen["Zen Screen Guard"] -->|"Pre-Effect Flag"| Journal["runtime-journal.json"]
```

### Diagram 12: License Validation Flow *(Optional Modular Adapter)*

```mermaid
flowchart TD
    Start["App Launch / Settings > License"] --> CheckLocal["Read DPAPI-Protected Local License Cache"]
    CheckLocal -->|"Valid Offline Signature"| Licensed["Unlock Commercial Features (0 Network Calls)"]
    CheckLocal -->|"Not Activated / Expired"| FreeOrTrial["Run Core Local Mode (Or User Enters Key)"]
    FreeOrTrial -->|"User Clicks Activate"| SendReq["HTTPS POST Key + Salted Hardware Hash"]
    SendReq -->|"Verified Signed Token"| SaveDPAPI["Encrypt & Store Locally via DPAPI"]
```

### Diagram 13: Color Picker Interaction Flow (`FR-PICKER-006`)

```mermaid
sequenceDiagram
    participant User as User
    participant Dialog as ColorPickerDialog
    participant Pin as OverlayPinController
    participant Session as AppearanceEditingSession
    participant Island as Real Top-Center Dynamic Island

    User->>Dialog: Click Color Token in Appearance Settings
    Dialog->>Pin: AcquirePin(ColorPickerPreview, Expanded)
    Pin->>Island: Pin Open & Suspend Auto-Collapse (No Focus Theft)
    Dialog->>Session: BeginEditingSession()
    User->>Dialog: Drag 2D S/B Field / Hue Slider / Edit HEX-RGB-HSB
    Dialog->>Session: UpdateToken(slot, newHex)
    Session-->>Dialog: Sync All Inputs + ContrastAuditReport (Non-Blocking)
    Session-->>Island: Live Preview Render (< 16.6ms, 0 Disk Writes)
    User->>Dialog: Toggle Preview Mode (Collapsed <-> Expanded)
    Dialog->>Pin: Switch PreviewTargetState
    alt User Clicks Apply / OK
        Dialog->>Session: CommitAsync() -> Save appearance.json + recentColors
        Dialog->>Pin: ReleasePin() -> Restore Normal Auto-Collapse
    else User Clicks Cancel / Esc
        Dialog->>Session: Cancel() -> Rollback Island to CurrentCommitted
        Dialog->>Pin: ReleasePin() -> Restore Normal Auto-Collapse
    end
```

### Diagram 14: Color Conversion & Synchronization Pipeline

```mermaid
flowchart LR
    Input["Source Input\n(Field2D | HueSlider | HEX | RGB | HSB)"] --> Guard["Loop-Prevention Source Tag"]
    Guard --> ColorSvc["IColorProcessingService\n(IEEE-754 Double Pipeline)"]
    ColorSvc -->|"FromHex / FromRgb / FromHsb"| Canon["Canonical ShimaColor\n(R,G,B + H,S,B + #RRGGBB)"]
    Canon -->|"Update Inactive Controls Only"| UIFields["2D Reticle + Hue Rail + HEX + R/G/B + H/S/B"]
    Canon --> Audit["WCAG 2.1 Contrast Auditor\n(4.5:1 Text / 3.0:1 Icon Advisory)"]
```

### Diagram 15: Live Appearance Preview & Render Coalescing

```mermaid
flowchart LR
    Drag["High-Frequency Pointer Drag\n(120-240 Hz)"] --> Draft["Update In-Memory Draft State"]
    Draft --> Coalesce["CompositionTarget.Rendering Coalescer\n(Max 1 Frame Pass per VSync)"]
    Coalesce --> ThemeDict["Update Dynamic Resource Brushes\n(Background, Accent, Border, Glow, Text, Icon)"]
    ThemeDict --> IslandWin["Real Dynamic Island"]
    ThemeDict --> MiniCard["Settings Preview Card"]
```

### Diagram 16: Island Pinning & Unpinning Lifecycle

```mermaid
stateDiagram-v2
    [*] --> UnpinnedNormal: Default Auto-Collapse Active
    UnpinnedNormal --> PinnedColorPreview: ColorPicker Opened (AcquirePin)
    PinnedColorPreview --> PinnedCollapsedPreview: User Toggles Collapsed Preview
    PinnedCollapsedPreview --> PinnedColorPreview: User Toggles Expanded Preview
    PinnedColorPreview --> UnpinnedNormal: Apply / Cancel / Esc / Dialog Dispose (ReleasePin)
    PinnedCollapsedPreview --> UnpinnedNormal: Apply / Cancel / Esc / Dialog Dispose (ReleasePin)
```

### Diagram 17: Appearance Configuration Persistence

```mermaid
flowchart TD
    Commit["User Clicks Apply / OK or Selects Preset"] --> Validate["Normalize #RRGGBB Tokens"]
    Validate --> WriteTmp["Write appearance.json.tmp & presets.json.tmp"]
    WriteTmp --> AtomicSwap["Atomic File.Replace in %LOCALAPPDATA%\\Shima\\config"]
    AtomicSwap --> UpdateRecent["Push Confirmed Hex to 12-Slot RecentColors Ring"]
    UpdateRecent --> Done["Persisted Across Restarts, Reboots & Updates"]
```
