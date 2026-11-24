# Shima v1.0.1

Maintenance release focused on shell customization (Movable Island & Floating Pill style), multitasking companion status bar, widget settings stability, portable-build identity, OpenCode v2 compatibility, and release automation.

## Fixes
- **Widget settings configuration button crash:** Fixed an issue where clicking the settings button in Now Playing or other widgets crashed or force-closed the app. Cleaned settings tab routing across all widgets through `IWidgetRegistrySettingsCoordinator`, added UI dispatcher thread guards on viewmodel notifications, and replaced blocking synchronous container disposal with clean async disposal on shutdown.
- **Win32 error 15700 in portable `.zip` and `dotnet run` builds:** Shima now ships split application manifests. The default `app.manifest` no longer embeds the Sparse MSIX `<msix>` element, while installer/MSIX builds use `app.sparse.manifest` (`-p:UseSparsePackageManifest=true`). Unpackaged builds no longer claim a package identity they don't have, and release packaging verifies each `Shima.App.exe` contains or omits the element as intended.
- **Notification Inbox preferences-change race condition:** Serialized `OnPreferencesChanged` pruning and group refreshes through `_workGate` and added a monotonic `_refreshVersion` check in `InboxWidgetViewModel` so an in-flight or out-of-order refresh cannot overwrite a newer notification dismissal with a stale unread count.

## Shell & Dynamic Island
- **Movable Island & Floating Pill style:**
  - **Island Style setting:** Switch between **Docked Notch** (flush against the top edge with concave outer ear curves) and **Floating Pill** (detached ~10–12px below top edge as a 4-corner rounded capsule with subtle elevation shadow). Persisted across restarts.
  - **Repositioning & dragging:** Drag the island freely (with a 5px gesture threshold so normal clicks and widget switching are never intercepted). Docked Notch mode constrains dragging horizontally along the monitor top edge; Floating Pill mode allows free X/Y positioning anywhere within the active display work area.
  - **Multi-monitor work-area clamping:** Automatically clamps both compact and expanded island bounds so expanding near display edges or bottoms never clips off-screen, smoothly gliding into safe bounds.
  - **Magnetic center snap & controls:** Snaps magnetically to center when dropped within 16px of top-center. Settings and tray menu include **Lock Position** and **Reset Position** options.
  - **Fluid animations:** Smooth morph transitions between Docked and Floating styles (~220ms CubicEase EaseOut), subtle lift/settle shadow on drag and hover, and zero-duration immediate snapping when Windows Reduced Motion is enabled. Non-client hit-testing (`WM_NCHITTEST`) ensures clicks around the floating capsule pass through cleanly to underlying desktop windows.

## Developer Activity
- **Bottom-edge compact companion bar:** When media is playing and Developer Activity has an active background task, the collapsed Now Playing pill stays intact (album art, track title, and playing dot) while a slim, rounded progress bar appears along the bottom inner edge of the collapsed pill:
  - **Working:** Smooth green sweep and subtle breathing animation while agents work.
  - **Done (`Finished` / `Idle`):** Immediately collapses and fades away (removing lingering dwell).
  - **Alerts (`NeedsAttention`, `AwaitingApproval`, `ErrorFailed`):** Turns solid amber (warning) or red (error) with animations held still. Stays visible until either the user expands the island to view Developer Activity or a 45-second fallback timeout expires.
  - **Hides on expansion:** Automatically hides when the island expands to full widget view. Fully respects Windows Reduced Motion.
- **OpenCode v2 compatibility:**
  - Plugin manifest now uses the v2 `{ id, setup(ctx) }` export shape.
  - Event parsing accepts the v2 `data` envelope and nested session/location fields.
  - Lifecycle events (`session.created`, tool start, `permission.asked` / `permission.replied`, `session.execution.succeeded` / `failed`) drive session state; high-frequency delta/catalog events are ignored without being counted as ambiguous.
  - Existing plugin files are backed up with a `.bak` suffix so OpenCode v2 auto-discovery never loads the backup as a plugin.
  - HTTP 401 responses from `opencode serve` no longer tear down sessions that are healthy over the named-pipe plugin bridge.

## Release Automation & Docs
- New GitHub Actions `Release` workflow runs tests, builds/signs x64 and ARM64 artifacts, and publishes the GitHub Release on `v*` tags.
- `build-release.ps1` can auto-install Inno Setup 6 through `winget` (`-AutoInstallInnoSetup`).
- README install steps and the Developer Activity setup guide now match the current installer and integration flow.

# Shima v1.0.0

First stable release of **Shima**, a modular Windows Dynamic Island built with WPF and WinRT/Win32 interop.

## Highlights
- **Dynamic Island Shell:** Smooth notch expansion, compact active pill arbitration, multi-DPI top-center anchoring, and semantic theming.
- **World Clock:** Offline timezone conversion with DST support, 12h/24h toggle, and multi-city switcher.
- **Now Playing & Volume Mixer:** WinRT media transport controls (artwork, seek, compact pill) and per-app WASAPI Core Audio volume sliders.
- **Productivity:** Quick Notes with auto-save, Clipboard History with image restore and password-manager exclusions, and a live Windows Notification Inbox.
- **Focus Timer & Zen Mode:** Pomodoro timer with compact countdown, pure-managed White/Pink/Brown noise, and process-scoped DWM grayscale/dimming.
- **Developer Activity:** Opt-in Claude Code (named-pipe hook bridge) and OpenCode (loopback SSE/REST + DPAPI auth) tracking with inline Allow/Deny approvals.
- **Performance:** 0.0% idle CPU, ~79 MB private memory footprint, 320 ms startup, zero idle polling timers, and 171/171 passing tests.

## Downloads
- **Windows x64:** Portable `.zip` + `Shima.Identity-win-x64.msix`
- **Windows ARM64:** Portable `.zip` + `Shima.Identity-win-arm64.msix`
- **Checksums:** `manifest-stable.json` (SHA-256 digests)

## Unlocking the Notification Inbox (Optional)
Windows requires package identity to read Action Center notifications. After extracting the zip, sign/trust and register the matching `Shima.Identity-*.msix` using `build/register-sparse-identity.ps1`. All other widgets work out of the box without registration.
