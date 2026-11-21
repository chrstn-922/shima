# Technical Risk Register & Mitigations: Shima

**Document Role**: Living risk register tracking Windows OS edge cases, third-party integration risks, and architectural mitigations before implementation begins.

---

| Risk ID | Risk Description | Likelihood | Impact | Mitigation & Fallback Strategy | Phase Gate |
| :--- | :--- | :---: | :---: | :--- | :--- |
| **R-01** | **Exclusive Fullscreen / Borderless Fullscreen Detection False Negatives** (`SHQueryUserNotificationState` misses borderless browser/game windows). | High | High | Combine `SetWinEventHook` + `SHQueryUserNotificationState` with foreground `GetWindowRect` vs `rcMonitor` check (excluding `Progman`/`WorkerW`). Validate against Chrome YouTube fullscreen, VLC, and DirectX games in Phase 0. | M1 |
| **R-02** | **Sparse Package Identity Registration Failures** (e.g., enterprise policy blocks `Add-AppxPackage` or mismatched signing cert). | Medium | Medium | Wrap Sparse Package registration gracefully in installer; `INotificationListenerAdapter` detects `IdentityUnavailable` and keeps all other 7 widgets fully operational with clear Settings guidance. | M1 / M4 |
| **R-03** | **Claude Code / OpenCode Hook Schema Drift** across CLI upgrades. | Medium | Medium | Isolate each agent behind `ICodingAgentAdapter`; pin version-tolerant JSON parsing with unknown-field tolerance; enforce fail-open `exit 0` empty-stdout passthrough so CLI never freezes. | M5 |
| **R-04** | **`MagSetFullscreenColorEffect` Multi-Monitor / HDR Artifacts** in Zen Mode. | Medium | High | Prototype on HDR and dual-monitor Windows 11 24H2 in Phase 0; support pure click-through dimming overlay fallback if `Magnification` conflicts with HDR; guard with `runtime-journal.json` crash recovery. | M3 |
| **R-05** | **`WS_EX_NOACTIVATE` Focus Glitch on Input Widgets** (Notes/Search fails to receive IME or steals focus permanently). | Medium | Medium | Explicit `FocusActivationHandler` state machine saves `previousForegroundHwnd`, clears `WS_EX_NOACTIVATE` only while text control has logical focus, and restores immediately on `Esc` or blur. | M1 / M3 |
| **R-06** | **WPF `AllowsTransparency` Software Rendering Fallback on Low-End GPUs** causing frame drops during morph animations or Color Picker drags. | Low–Med | Medium | Keep `ColorSelectionField` inside standard opaque Settings modal (`WriteableBitmap`); use `Clip` geometry + pre-baked shadow on Island rather than expensive per-frame `DropShadowEffect` shader blurs. | M1 / M2 |
| **R-07** | **Clipboard Lock Contention** (`OpenClipboard` throws `0x800401D0` when Excel/RDP locks clipboard). | High | Low | 3-step async backoff (`25/50/75 ms`); log sanitized warning and skip entry gracefully without blocking UI thread. | M4 |
