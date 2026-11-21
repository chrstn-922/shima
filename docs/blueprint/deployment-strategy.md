# Packaging, Deployment & Distribution Strategy: Shima

**Document Role**: Evaluates Windows installer/packaging options, code signing, delta updates, startup registration, x64/ARM64 support, and realistic OS compatibility.

---

## 1. Packaging & Installer Strategy (`Velopack` + Optional Sparse MSIX Identity)

| Criterion | Velopack (`Setup.exe`) + Sparse Package Identity *(Selected)* | Full MSIX / Store Package Only | Standalone Portable `.zip` |
| :--- | :--- | :--- | :--- |
| **User-Mode Install (No Admin UAC)** | Installs to `%LOCALAPPDATA%\Shima` with zero UAC prompt. | Installs to protected `%ProgramFiles%\WindowsApps`. | Runs anywhere, no shortcuts/auto-update. |
| **Delta Binary Auto-Updates** | Native Rust-based fast differential patches (`~1–3 MB` deltas). | AppInstaller / Store managed only. | Manual re-download. |
| **`UserNotificationListener` Identity** | Granted via post-install `Add-AppxPackage -Register AppxManifest.xml -ExternalLocation` on Win 10 2004+. | Native out-of-the-box. | **Not available** (Inbox degrades gracefully). |
| **External CLI Hooks (`Claude Code`)** | Clean unvirtualized `%LOCALAPPDATA%\Shima\current\Shima.AgentBridge.exe` path. | Virtualized AppExecutionAlias / container paths complicate external hook paths. | Works cleanly. |

- **Decision**: Ship **Velopack** per-user installers (`Shima-win-x64-Setup.exe` & `Shima-win-arm64-Setup.exe`) bundled with a signed **Sparse Package `AppxManifest.xml`** (declaring `uap3:Capability Name="userNotificationListener"` and `uap5:StartupTask`). On Windows 10 2004+, the installer registers the Sparse Identity automatically; if registration is skipped (e.g., portable mode or Win 10 < 2004), Shima runs cleanly with Inbox displaying its non-blocking identity guidance card.

---

## 2. Code Signing, Updates & Crash Diagnostics

- **Code Signing**: Authenticode SHA-256 timestamped code signing (EV or trusted OV certificate / Azure Trusted Signing) for `Shima.App.exe`, `Shima.AgentBridge.exe`, the Sparse MSIX identity manifest, and Velopack installer—required to avoid Windows SmartScreen blocks and satisfy Sparse Package `Publisher` matching.
- **Update Flow (`IUpdateService`)**:
  - Defaults to opt-in/manual check or configurable launch check (strictly bypassable via **Offline Mode** toggle in `Settings → Updates`).
  - Downloads signed release manifest over HTTPS, validates SHA-256 checksums, stages binary delta in background, and applies on restart without touching `%LOCALAPPDATA%\Shima\config\` or `data\shima.db`.
- **Startup & Uninstallation**:
  - `StartupTask` (when Sparse Identity registered) or `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (fallback).
  - Uninstaller removes app binaries, unregisters Sparse Package (`Remove-AppxPackage`), removes startup key, and prompts whether to keep or purge user data in `%LOCALAPPDATA%\Shima\`.
- **Crash Diagnostics**: Global `AppDomain.UnhandledException` + `DispatcherUnhandledException` hooks immediately restore any Zen Mode screen filter, flush a sanitized local crash log (zero clipboard/note/toast data), and offer clean restart.

---

## 3. Realistic Windows Compatibility & Architecture Support Matrix

*Evaluated independently from Microsoft API requirements (rather than adopting `navisle.com`'s Windows 10 1809+ marketing claim verbatim):*

| Platform Tier | Target OS & Architecture | Verified Capability Status |
| :--- | :--- | :--- |
| **Tier 1 (Recommended — Full Support)** | Windows 10 **2004 (Build 19041)+** & Windows 11 (`21H2–24H2+`), **`win-x64` & `win-arm64`** | Full support: `PerMonitorV2` DPI, Sparse Package Identity (`UserNotificationListener` Inbox + bidirectional dismissal), `GSMTC`, CoreAudio `WASAPI`, `AddClipboardFormatListener`, `MagSetFullscreenColorEffect`, and `Velopack` native RID builds. |
| **Tier 2 (Partial Compatibility)** | Windows 10 **1809–1909 (`17763`–`18363`)** or Unpackaged Portable runs (`x64`) | Core Island + 7 widgets (`Clock`, `Now Playing`, `Focus Timer`, `Notes`, `Clipboard`, `Volume Mixer`, `Developer`) function normally; **Inbox** degrades cleanly to `IdentityUnavailable` guidance because `uap10:AllowExternalContent` Sparse Packages require Build `19041+` (unless distributed via full MSIX). |
