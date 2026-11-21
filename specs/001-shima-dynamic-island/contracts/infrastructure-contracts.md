# Contract: Infrastructure, Storage, Update & License Adapters (`Shima.Infrastructure`)

**Contract Scope**: Defines repository contracts, atomic config/secret persistence, sanitized logging, and strictly isolated optional network adapters (`IUpdateService`, `ILicenseService`).

---

## 1. Local Repositories (`shima.db` + Atomic JSON)

- `INoteRepository`: `GetAllAsync()`, `SearchAsync(query)`, `UpsertAsync(NoteEntry)`, `DeleteAsync(noteId)`. (Debounced `500 ms` from UI).
- `IClipboardRepository`: `GetHistoryAsync(query, limit)`, `TryAddEntryAsync(ClipboardEntry)` (dedupes consecutive `content_hash` + enforces `clipboardMaxItems`), `SetPinnedAsync(id, bool)`, `DeleteAsync(id)`, `ClearUnpinnedAsync()`.
- `IInboxRepository`: `GetGroupedByAppAsync()`, `UpsertToastAsync(InboxNotificationItem)`, `MarkDismissedAsync(id)`, `ClearAppGroupAsync(appId)`, `ClearAllAsync()`, `PruneExpiredAsync()`.
- `IClockRepository`: `GetLocationsAsync()`, `SaveLocationsAsync(list)`, `SetPrimaryAsync(locationId)`.
- `IFocusRepository`: `RecordSessionAsync(FocusSession)`, `GetRecentStatsAsync()`.
- `IWidgetConfigRepository`: `GetAllAsync()`, `SaveAsync(WidgetConfig)`, `UpdateOrderAsync(orderedIds)`.

---

## 2. Corrupt-File Recovery & Secret Protection

```text
interface IAtomicConfigStore<T>
  Task<T> LoadOrRecoverAsync(string relativePath, Func<T> defaultFactory)
  Task SaveAtomicAsync(string relativePath, T value)

interface ISecureSecretStore
  byte[] ProtectForCurrentUser(byte[] plaintext)      // Windows DPAPI CurrentUser
  byte[] UnprotectForCurrentUser(byte[] ciphertext)
```

- `LoadOrRecoverAsync` catches JSON deserialization or checksum errors, renames the damaged file to `<name>.corrupt.<timestamp>.json`, returns `defaultFactory()`, and emits a non-blocking user warning notification.

---

## 3. Isolated Network Adapters (Zero-Telemetry Default)

```text
interface IUpdateService
  bool IsOfflineMode { get; set; }
  Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct)  // No-op Offline when IsOfflineMode == true
  Task<bool> DownloadAndStageUpdateAsync(UpdateReleaseInfo release, IProgress<double> progress)

interface ILicenseService
  LicenseStatus GetCachedStatus()                                      // Reads DPAPI-cached signed license locally
  Task<LicenseStatus> ActivateLicenseAsync(string licenseKey, CancellationToken ct)
  Task DeactivateLocalLicenseAsync()
```

- **Privacy Invariant**: Neither `IUpdateService` nor `ILicenseService` may ever read or transmit notes, clipboard entries, notification contents, or file paths. Both can be compiled/configured as offline-only in v1 without affecting any widget.
