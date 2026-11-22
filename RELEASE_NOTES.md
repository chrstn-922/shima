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
