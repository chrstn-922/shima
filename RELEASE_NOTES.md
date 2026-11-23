# Shima v1.0.1

Maintenance release focused on portable-build identity, OpenCode v2 compatibility, compact pill multitasking, and release automation.

## Fixes
- **Win32 error 15700 in portable `.zip` and `dotnet run` builds:** Shima now ships split application manifests. The default `app.manifest` no longer embeds the Sparse MSIX `<msix>` element, while installer/MSIX builds use `app.sparse.manifest` (`-p:UseSparsePackageManifest=true`). Unpackaged builds no longer claim a package identity they don't have, and release packaging verifies each `Shima.App.exe` contains or omits the element as intended.
- **Notification Inbox preferences-change race condition:** Serialized `OnPreferencesChanged` pruning and group refreshes through `_workGate` and added a monotonic `_refreshVersion` check in `InboxWidgetViewModel` so an in-flight or out-of-order refresh cannot overwrite a newer notification dismissal with a stale unread count.

## Dynamic Island & Developer Activity
- **Green Agent companion pill alongside Now Playing:** When media is playing and Developer Activity has an active background session (`Working` or `Finished` dwell), album art and the track title remain on the left while a compact green Agent progress pill smoothly animates into the right side of the compact pill in place of the playing accent dot. Urgent agent states (`AwaitingApproval`, `NeedsAttention`, `ErrorFailed`) retain full compact-pill priority, and the companion pill smoothly fades and collapses back to the normal music layout when idle (respecting Windows Reduced Motion).
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
