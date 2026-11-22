using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Services;

/// <summary>
/// Strictly opt-in/offline-capable update checker and SHA-256 staged update downloader.
/// Never opens sockets at startup or when <see cref="IsOfflineMode"/> is enabled,
/// and never sends user notes, clipboard, notifications, or paths.
/// </summary>
public sealed class UpdateServiceAdapter : IUpdateService, IDisposable
{
    private const long MaxPackageSizeBytes = 250L * 1024L * 1024L;
    private const string StagedManifestFileName = "staged-update.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IAppPreferencesService _preferencesService;
    private readonly string _dataRoot;
    private readonly string? _manifestEndpointUrl;
    private readonly string? _manifestPublicKeyPem;
    private readonly HttpMessageHandler? _injectedHandler;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private HttpClient? _httpClient;
    private bool _isOfflineMode;
    private StagedUpdateRecord? _stagedUpdate;
    private bool _disposed;

    public UpdateServiceAdapter(
        IAppPreferencesService preferencesService,
        string dataRoot,
        string? manifestEndpointUrl = null,
        string? currentVersion = null,
        string? currentArchitecture = null,
        string? manifestPublicKeyPem = null,
        HttpMessageHandler? httpMessageHandler = null,
        ILogger? logger = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        _dataRoot = Path.GetFullPath(dataRoot);
        _manifestEndpointUrl = string.IsNullOrWhiteSpace(manifestEndpointUrl) ? null : manifestEndpointUrl.Trim();
        _manifestPublicKeyPem = string.IsNullOrWhiteSpace(manifestPublicKeyPem) ? null : manifestPublicKeyPem;
        _injectedHandler = httpMessageHandler;
        _logger = logger;

        CurrentVersion = NormalizeVersionString(currentVersion) ?? ResolveAssemblyVersion();
        CurrentArchitecture = string.IsNullOrWhiteSpace(currentArchitecture)
            ? ResolveDefaultArchitecture()
            : currentArchitecture.Trim().ToLowerInvariant();
        _isOfflineMode = _preferencesService.Current.UpdatesOfflineMode;

        _preferencesService.PreferencesChanged += OnPreferencesChanged;
    }

    public string CurrentVersion { get; }

    public string CurrentArchitecture { get; }

    public bool IsOfflineMode
    {
        get
        {
            lock (_sync)
            {
                return _isOfflineMode;
            }
        }
        set
        {
            lock (_sync)
            {
                _isOfflineMode = value;
            }
        }
    }

    public StagedUpdateRecord? StagedUpdate
    {
        get
        {
            lock (_sync)
            {
                return _stagedUpdate;
            }
        }
        private set
        {
            lock (_sync)
            {
                _stagedUpdate = value;
            }
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (IsOfflineMode)
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.OfflineDisabled,
                CurrentVersion,
                Message: "Offline mode is enabled — update checks are disabled.");
        }

        if (string.IsNullOrWhiteSpace(_manifestEndpointUrl))
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.UpToDate,
                CurrentVersion,
                Message: "No remote update feed configured (local-only mode).");
        }

        if (!IsHttpsUri(_manifestEndpointUrl, out var baseUri))
        {
            return new UpdateCheckResult(
                UpdateCheckOutcome.Failed,
                CurrentVersion,
                Message: "Update manifest endpoint must use HTTPS.");
        }

        var separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        var requestUri = new Uri(
            $"{baseUri.AbsoluteUri}{separator}version={Uri.EscapeDataString(CurrentVersion)}&arch={Uri.EscapeDataString(CurrentArchitecture)}",
            UriKind.Absolute);

        try
        {
            var client = GetOrCreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("User-Agent", $"Shima/{CurrentVersion} (Windows; {CurrentArchitecture})");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var finalScheme = response.RequestMessage?.RequestUri?.Scheme;
            if (!string.Equals(finalScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: "Update manifest redirect downgraded away from HTTPS.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: $"Update check failed with HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<UpdateManifestDocument>(json, JsonOptions);
            if (document is null
                || string.IsNullOrWhiteSpace(document.Version)
                || !TryParseSemVer(document.Version, out var candidateVersion)
                || document.Packages is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: "Release manifest format is invalid.");
            }

            var pkg = document.Packages.FirstOrDefault(p =>
                string.Equals(p.Architecture?.Trim(), CurrentArchitecture, StringComparison.OrdinalIgnoreCase));
            if (pkg is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: $"Release manifest does not contain a package for '{CurrentArchitecture}'.");
            }

            if (!IsHttpsUri(pkg.DownloadUrl, out _)
                || !IsValidSha256Hex(pkg.Sha256)
                || !IsSafeFileName(pkg.FileName)
                || pkg.SizeBytes < 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: "Release manifest package metadata failed security validation.");
            }

            var channel = string.IsNullOrWhiteSpace(document.Channel) ? "stable" : document.Channel.Trim();
            var normalizedRelease = new UpdateReleaseInfo(
                document.Version.Trim(),
                channel,
                CurrentArchitecture,
                pkg.DownloadUrl!.Trim(),
                pkg.Sha256!.Trim().ToLowerInvariant(),
                pkg.SizeBytes,
                pkg.FileName!.Trim(),
                document.PublishedAtUtc,
                document.ReleaseNotes,
                pkg.SignatureBase64);

            if (!string.IsNullOrWhiteSpace(_manifestPublicKeyPem)
                && !VerifyManifestSignature(normalizedRelease, _manifestPublicKeyPem))
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Failed,
                    CurrentVersion,
                    Message: "Release manifest cryptographic signature verification failed.");
            }

            if (!TryParseSemVer(CurrentVersion, out var currentSemVer) || candidateVersion > currentSemVer)
            {
                return new UpdateCheckResult(
                    UpdateCheckOutcome.UpdateAvailable,
                    CurrentVersion,
                    normalizedRelease,
                    $"Version {normalizedRelease.Version} is available.");
            }

            return new UpdateCheckResult(
                UpdateCheckOutcome.UpToDate,
                CurrentVersion,
                Message: $"Shima {CurrentVersion} is up to date.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger?.Warn("Update manifest check failed.", ex);
            return new UpdateCheckResult(
                UpdateCheckOutcome.Failed,
                CurrentVersion,
                Message: "Unable to check for updates cleanly.");
        }
    }

    public async Task<bool> DownloadAndStageUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOfflineMode
            || release is null
            || !TryParseSemVer(release.Version, out _)
            || !IsHttpsUri(release.DownloadUrl, out var downloadUri)
            || !IsValidSha256Hex(release.Sha256)
            || !IsSafeFileName(release.FileName)
            || release.SizeBytes is < 0 or > MaxPackageSizeBytes)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_manifestPublicKeyPem)
            && !VerifyManifestSignature(release, _manifestPublicKeyPem))
        {
            _logger?.Warn("Rejected staged update download due to invalid manifest signature.");
            return false;
        }

        var stagedRoot = Path.GetFullPath(Path.Combine(_dataRoot, "updates", "staged"));
        var versionSegment = SanitizeSegment(release.Version);
        var stagingDir = Path.GetFullPath(Path.Combine(stagedRoot, versionSegment));
        if (!stagingDir.StartsWith(stagedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Directory.CreateDirectory(stagingDir);
        var targetFilePath = Path.Combine(stagingDir, release.FileName);
        var tempFilePath = targetFilePath + ".tmp";

        try
        {
            var client = GetOrCreateHttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.TryAddWithoutValidation("User-Agent", $"Shima/{CurrentVersion} (Windows; {CurrentArchitecture})");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var finalScheme = response.RequestMessage?.RequestUri?.Scheme;
            if (!string.Equals(finalScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !response.IsSuccessStatusCode)
            {
                TryDeleteFile(tempFilePath);
                return false;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxPackageSizeBytes)
            {
                TryDeleteFile(tempFilePath);
                return false;
            }

            if (release.SizeBytes > 0 && contentLength.HasValue && contentLength.Value != release.SizeBytes)
            {
                TryDeleteFile(tempFilePath);
                return false;
            }

            var expectedTotalBytes = release.SizeBytes > 0
                ? release.SizeBytes
                : (contentLength ?? 0L);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalRead = 0;

            await using (var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await responseStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxPackageSizeBytes
                        || (release.SizeBytes > 0 && totalRead > release.SizeBytes))
                    {
                        await fileStream.FlushAsync(ct).ConfigureAwait(false);
                        fileStream.Close();
                        TryDeleteFile(tempFilePath);
                        return false;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    if (expectedTotalBytes > 0)
                    {
                        var ratio = Math.Clamp((double)totalRead / expectedTotalBytes, 0.0, 0.99);
                        progress?.Report(ratio);
                    }
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (release.SizeBytes > 0 && totalRead != release.SizeBytes)
            {
                TryDeleteFile(tempFilePath);
                return false;
            }

            var actualDigest = hasher.GetHashAndReset();
            var expectedDigest = Convert.FromHexString(release.Sha256.Trim());
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                _logger?.Warn($"Rejected staged update '{release.Version}' due to SHA-256 verification failure.");
                TryDeleteFile(tempFilePath);
                return false;
            }

            if (File.Exists(targetFilePath))
            {
                File.Replace(tempFilePath, targetFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempFilePath, targetFilePath);
            }

            var normalizedSha = release.Sha256.Trim().ToLowerInvariant();
            var record = new StagedUpdateRecord(
                release.Version.Trim(),
                release.Architecture,
                targetFilePath,
                normalizedSha,
                DateTimeOffset.UtcNow);

            await WriteStagedManifestAtomicAsync(Path.Combine(stagingDir, StagedManifestFileName), record, ct)
                .ConfigureAwait(false);

            StagedUpdate = record;
            progress?.Report(1.0);
            _logger?.Info($"Verified and staged update {record.Version} ({record.Architecture}).");
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempFilePath);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDeleteFile(tempFilePath);
            _logger?.Warn("Update staging failed cleanly.", ex);
            return false;
        }
    }

    public static string BuildCanonicalManifestPayload(UpdateReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{release.Version.Trim()}|{release.Channel.Trim()}|{release.Architecture.Trim().ToLowerInvariant()}|{release.DownloadUrl.Trim()}|{release.Sha256.Trim().ToLowerInvariant()}|{release.SizeBytes}");
    }

    public static string SignManifestPayload(UpdateReleaseInfo release, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(signingKey);

        var bytes = Encoding.UTF8.GetBytes(BuildCanonicalManifestPayload(release));
        var signature = signingKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    public static bool VerifyManifestSignature(UpdateReleaseInfo release, string publicKeyPem)
    {
        if (release is null
            || string.IsNullOrWhiteSpace(release.SignatureBase64)
            || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            var signature = Convert.FromBase64String(release.SignatureBase64.Trim());
            var payload = Encoding.UTF8.GetBytes(BuildCanonicalManifestPayload(release));
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _preferencesService.PreferencesChanged -= OnPreferencesChanged;

        lock (_sync)
        {
            _httpClient?.Dispose();
            _httpClient = null;
        }
    }

    private void OnPreferencesChanged(object? sender, AppPreferencesSnapshot snapshot) =>
        IsOfflineMode = snapshot.UpdatesOfflineMode;

    private HttpClient GetOrCreateHttpClient()
    {
        lock (_sync)
        {
            if (_httpClient is not null)
            {
                return _httpClient;
            }

            if (_injectedHandler is not null)
            {
                _httpClient = new HttpClient(_injectedHandler, disposeHandler: false);
            }
            else
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 3,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }
                };
                _httpClient = new HttpClient(handler, disposeHandler: true);
            }

            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            return _httpClient;
        }
    }

    private static async Task WriteStagedManifestAtomicAsync(
        string manifestPath,
        StagedUpdateRecord record,
        CancellationToken ct)
    {
        var tempPath = manifestPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (File.Exists(manifestPath))
            {
                File.Replace(tempPath, manifestPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, manifestPath);
            }
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static string ResolveAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateServiceAdapter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var normalized = NormalizeVersionString(informational);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        var v = assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    private static string ResolveDefaultArchitecture() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    private static string? NormalizeVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var stripped = raw.Trim();
        var plusIndex = stripped.IndexOf('+');
        if (plusIndex >= 0)
        {
            stripped = stripped[..plusIndex];
        }

        var dashIndex = stripped.IndexOf('-');
        if (dashIndex >= 0)
        {
            stripped = stripped[..dashIndex];
        }

        return stripped.TrimStart('v', 'V');
    }

    private static bool TryParseSemVer(string? raw, out Version parsed)
    {
        parsed = new Version(0, 0, 0);
        var normalized = NormalizeVersionString(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!Version.TryParse(normalized, out var v))
        {
            return false;
        }

        parsed = new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        return true;
    }

    private static bool IsHttpsUri(string? rawUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsValidSha256Hex(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return false;
        }

        var trimmed = sha256.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var trimmed = fileName.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.IndexOfAny(['/', '\\', ':']) >= 0
            || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal);
    }

    private static string SanitizeSegment(string value)
    {
        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray();
        var cleaned = new string(chars).Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "staged" : cleaned;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class UpdateManifestDocument
    {
        public string? Version { get; set; }
        public string? Channel { get; set; }
        public DateTimeOffset PublishedAtUtc { get; set; }
        public string? ReleaseNotes { get; set; }
        public List<UpdateManifestPackageDocument>? Packages { get; set; }
    }

    private sealed class UpdateManifestPackageDocument
    {
        public string? Architecture { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public string? FileName { get; set; }
        public string? SignatureBase64 { get; set; }
    }
}
