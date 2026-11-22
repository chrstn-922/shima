namespace Shima.Core.Contracts;

/// <summary>
/// Outcome of checking for a newer Shima release manifest.
/// </summary>
public enum UpdateCheckOutcome
{
    OfflineDisabled,
    UpToDate,
    UpdateAvailable,
    Failed
}

/// <summary>
/// Describes a signed, SHA-256 verified release binary for a specific Windows architecture.
/// </summary>
public sealed record UpdateReleaseInfo(
    string Version,
    string Channel,
    string Architecture,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string FileName,
    DateTimeOffset PublishedAtUtc,
    string? ReleaseNotes = null,
    string? SignatureBase64 = null);

/// <summary>
/// Result returned by <see cref="IUpdateService.CheckForUpdatesAsync"/>.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string CurrentVersion,
    UpdateReleaseInfo? AvailableRelease = null,
    string? Message = null);

/// <summary>
/// Metadata describing a locally downloaded, SHA-256 verified release installer awaiting application restart.
/// </summary>
public sealed record StagedUpdateRecord(
    string Version,
    string Architecture,
    string StagedFilePath,
    string Sha256,
    DateTimeOffset StagedAtUtc);

/// <summary>
/// Strictly opt-in/offline-capable update check and staged installer verification contract.
/// Never reads or transmits user notes, clipboard data, notifications, or file paths.
/// </summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    string CurrentArchitecture { get; }

    bool IsOfflineMode { get; set; }

    StagedUpdateRecord? StagedUpdate { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);

    Task<bool> DownloadAndStageUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Local activation state evaluated offline from a DPAPI-cached signed token.
/// </summary>
public enum LicenseStateKind
{
    CommunityOffline,
    Active,
    Expired,
    HardwareMismatch,
    Invalid
}

/// <summary>
/// Current resolved licensing status for Shima.
/// </summary>
public sealed record LicenseStatus(
    LicenseStateKind State,
    bool IsValid,
    string Edition,
    string? Licensee = null,
    string? LicenseId = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string Message = "");

/// <summary>
/// Strictly optional offline-first licensing adapter backed by DPAPI CurrentUser persistence
/// and salted SHA-256 hardware binding.
/// </summary>
public interface ILicenseService
{
    string ComputeHardwareFingerprintHash(string? salt = null);

    LicenseStatus GetCachedStatus();

    Task<LicenseStatus> ActivateLicenseAsync(string licenseKey, CancellationToken ct = default);

    Task DeactivateLocalLicenseAsync(CancellationToken ct = default);
}
