using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Shima.Core.Contracts;
using Shima.Infrastructure.Configuration;
using Shima.Infrastructure.Persistence;
using Shima.Infrastructure.Services;
using Shima.Infrastructure.Storage;
using Xunit;

namespace Shima.Infrastructure.Tests;

public sealed class UpdateAndLicenseServiceTests : TempDirectoryTest
{
    private const string ValidReleaseUrl = "https://updates.shima.test/manifest.json";
    private const string ValidInstallerUrl = "https://updates.shima.test/downloads/Shima-2.0.0-win-x64-Setup.exe";

    [Fact]
    public async Task UpdateService_WhenOfflineModeEnabled_PerformsZeroHttpRequestsForCheckOrDownload()
    {
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences { UpdatesOfflineMode = true });
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new UpdateServiceAdapter(
            prefsService,
            Root,
            ValidReleaseUrl,
            currentVersion: "1.0.0",
            currentArchitecture: "win-x64",
            httpMessageHandler: handler);

        var check = await service.CheckForUpdatesAsync();
        var dummyRelease = new UpdateReleaseInfo(
            "2.0.0",
            "stable",
            "win-x64",
            ValidInstallerUrl,
            new string('a', 64),
            16,
            "Shima-2.0.0-win-x64-Setup.exe",
            DateTimeOffset.UtcNow);
        var staged = await service.DownloadAndStageUpdateAsync(dummyRelease);

        check.Outcome.Should().Be(UpdateCheckOutcome.OfflineDisabled);
        staged.Should().BeFalse();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateService_SyncsIsOfflineMode_WhenPreferencesChange()
    {
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences { UpdatesOfflineMode = false });
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new UpdateServiceAdapter(
            prefsService,
            Root,
            ValidReleaseUrl,
            httpMessageHandler: handler);

        service.IsOfflineMode.Should().BeFalse();

        await prefsService.SaveAsync(prefsService.Current with { UpdatesOfflineMode = true });

        service.IsOfflineMode.Should().BeTrue();
        var result = await service.CheckForUpdatesAsync();
        result.Outcome.Should().Be(UpdateCheckOutcome.OfflineDisabled);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateService_RejectsNonHttpsManifestAndDownloadUrls_WithZeroRequests()
    {
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences { UpdatesOfflineMode = false });
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var insecureService = new UpdateServiceAdapter(
            prefsService,
            Root,
            "http://updates.shima.test/manifest.json",
            currentVersion: "1.0.0",
            currentArchitecture: "win-x64",
            httpMessageHandler: handler);

        var result = await insecureService.CheckForUpdatesAsync();
        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        handler.RecordedRequests.Should().BeEmpty();

        var httpRelease = new UpdateReleaseInfo(
            "2.0.0",
            "stable",
            "win-x64",
            "http://updates.shima.test/Shima.exe",
            new string('b', 64),
            12,
            "Shima.exe",
            DateTimeOffset.UtcNow);
        (await insecureService.DownloadAndStageUpdateAsync(httpRelease)).Should().BeFalse();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateService_ChecksManifest_VerifiesEcdsaAndSha256_AndPreservesUserData()
    {
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences { UpdatesOfflineMode = false });
        var dbPath = Path.Combine(Root, "data", "shima.db");
        await new SqliteMigrationRunner(dbPath).InitializeAsync();
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO notes (note_id, title, content_text, is_pinned, created_at_utc, updated_at_utc) VALUES ('n1', 'Keep', 'Local-only secret note', 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();
        }

        var payloadBytes = Encoding.UTF8.GetBytes("shima-verified-release-binary-payload-v2");
        var payloadSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var privateNoteMarker = "Local-only secret note";

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();

        var releaseForSig = new UpdateReleaseInfo(
            "2.0.0",
            "stable",
            "win-x64",
            ValidInstallerUrl,
            payloadSha,
            payloadBytes.Length,
            "Shima-2.0.0-win-x64-Setup.exe",
            DateTimeOffset.UtcNow);
        var validSig = UpdateServiceAdapter.SignManifestPayload(releaseForSig, signingKey);

        var manifestJson = JsonSerializer.Serialize(new
        {
            version = "2.0.0",
            channel = "stable",
            publishedAtUtc = DateTimeOffset.UtcNow,
            releaseNotes = "Security & performance updates.",
            packages = new[]
            {
                new
                {
                    architecture = "win-x64",
                    downloadUrl = ValidInstallerUrl,
                    sha256 = payloadSha,
                    sizeBytes = payloadBytes.Length,
                    fileName = "Shima-2.0.0-win-x64-Setup.exe",
                    signatureBase64 = validSig
                }
            }
        });

        var handler = new RecordingHttpHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
        });

        using var service = new UpdateServiceAdapter(
            prefsService,
            Root,
            ValidReleaseUrl,
            currentVersion: "1.0.0",
            currentArchitecture: "win-x64",
            manifestPublicKeyPem: publicPem,
            httpMessageHandler: handler);

        var check = await service.CheckForUpdatesAsync();
        check.Outcome.Should().Be(UpdateCheckOutcome.UpdateAvailable);
        check.AvailableRelease.Should().NotBeNull();

        handler.RecordedRequests.Should().ContainSingle();
        var manifestUri = handler.RecordedRequests[0].Uri!.Query;
        manifestUri.Should().Contain("version=1.0.0").And.Contain("arch=win-x64");
        handler.RecordedRequests[0].Body.Should().BeNullOrEmpty();

        var progressReports = new List<double>();
        var progress = new SyncProgress<double>(progressReports.Add);
        var stagedOk = await service.DownloadAndStageUpdateAsync(check.AvailableRelease!, progress);

        stagedOk.Should().BeTrue();
        service.StagedUpdate.Should().NotBeNull();
        File.Exists(service.StagedUpdate!.StagedFilePath).Should().BeTrue();
        File.ReadAllBytes(service.StagedUpdate.StagedFilePath).Should().Equal(payloadBytes);
        File.Exists(Path.Combine(Path.GetDirectoryName(service.StagedUpdate.StagedFilePath)!, "staged-update.json")).Should().BeTrue();
        progressReports.Should().NotBeEmpty();
        progressReports[^1].Should().Be(1.0);

        // Verify SQLite data & settings remain untouched and zero PII leaked to HTTP requests
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT content_text FROM notes WHERE note_id = 'n1';";
            (await cmd.ExecuteScalarAsync()).Should().Be(privateNoteMarker);
        }

        // Compare Equal / Older version -> UpToDate
        using var sameVerService = new UpdateServiceAdapter(
            prefsService,
            Root,
            ValidReleaseUrl,
            currentVersion: "2.0.0",
            currentArchitecture: "win-x64",
            manifestPublicKeyPem: publicPem,
            httpMessageHandler: handler);
        (await sameVerService.CheckForUpdatesAsync()).Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
    }

    [Fact]
    public async Task UpdateService_RejectsTamperedSha256AndTamperedSignature_CleaningTempFiles()
    {
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences { UpdatesOfflineMode = false });
        var genuineBytes = Encoding.UTF8.GetBytes("clean-binary");
        var tamperedBytes = Encoding.UTF8.GetBytes("tampered-binary");
        var expectedSha = Convert.ToHexString(SHA256.HashData(genuineBytes)).ToLowerInvariant();

        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();

        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(tamperedBytes)
        });

        using var service = new UpdateServiceAdapter(
            prefsService,
            Root,
            ValidReleaseUrl,
            currentVersion: "1.0.0",
            currentArchitecture: "win-x64",
            manifestPublicKeyPem: publicPem,
            httpMessageHandler: handler);

        var release = new UpdateReleaseInfo(
            "3.0.0",
            "stable",
            "win-x64",
            ValidInstallerUrl,
            expectedSha,
            genuineBytes.Length,
            "Shima-3.0.0-win-x64-Setup.exe",
            DateTimeOffset.UtcNow);
        var validSig = UpdateServiceAdapter.SignManifestPayload(release, signingKey);
        var signedRelease = release with { SignatureBase64 = validSig };

        // 1. Valid signature, tampered binary payload -> SHA-256 mismatch
        var downloadOk = await service.DownloadAndStageUpdateAsync(signedRelease);
        downloadOk.Should().BeFalse();
        service.StagedUpdate.Should().BeNull();
        Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();

        // 2. Tampered signature -> rejected before download
        var requestsBeforeSigFail = handler.RecordedRequests.Count;
        var badSigRelease = signedRelease with { SignatureBase64 = Convert.ToBase64String(new byte[64]) };
        (await service.DownloadAndStageUpdateAsync(badSigRelease)).Should().BeFalse();
        handler.RecordedRequests.Count.Should().Be(requestsBeforeSigFail);
    }

    [Fact]
    public async Task LicenseService_DefaultsToCommunityOffline_AndPersistsSignedTokenInDpapi()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences());

        using var service = new LicenseServiceAdapter(
            prefsService,
            verificationPublicKeyPem: publicPem,
            machineIdentityProvider: () => "unit-test-machine-guid");

        var initialStatus = service.GetCachedStatus();
        initialStatus.State.Should().Be(LicenseStateKind.CommunityOffline);
        initialStatus.IsValid.Should().BeTrue();

        var hwid1 = service.ComputeHardwareFingerprintHash();
        var hwid2 = service.ComputeHardwareFingerprintHash();
        hwid1.Should().Be(hwid2).And.HaveLength(64);
        hwid1.Should().NotContain("unit-test-machine-guid");

        var signedToken = LicenseServiceAdapter.CreateSignedToken(
            new SignedLicensePayload(
                "LIC-1001",
                "Pro",
                "Alice Developer",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(90),
                hwid1),
            signingKey);

        var activation = await service.ActivateLicenseAsync(signedToken);
        activation.State.Should().Be(LicenseStateKind.Active);
        activation.IsValid.Should().BeTrue();
        activation.Edition.Should().Be("Pro");
        activation.Licensee.Should().Be("Alice Developer");

        // Verify disk settings.json encrypts LicenseState via DPAPI (plaintext token / licensee absent)
        var settingsPath = Path.Combine(Root, AppPreferencesStore.DefaultRelativePath);
        var diskText = await File.ReadAllTextAsync(settingsPath);
        diskText.Should().NotContain(signedToken);
        diskText.Should().NotContain("Alice Developer");

        // Simulate full application restart
        var restartedPrefs = await CreatePreferencesServiceAsync();
        using var restartedLicense = new LicenseServiceAdapter(
            restartedPrefs,
            verificationPublicKeyPem: publicPem,
            machineIdentityProvider: () => "unit-test-machine-guid");

        var cached = restartedLicense.GetCachedStatus();
        cached.State.Should().Be(LicenseStateKind.Active);
        cached.LicenseId.Should().Be("LIC-1001");

        await restartedLicense.DeactivateLocalLicenseAsync();
        restartedLicense.GetCachedStatus().State.Should().Be(LicenseStateKind.CommunityOffline);
        restartedPrefs.Current.HasLicenseKey.Should().BeFalse();
    }

    [Fact]
    public async Task LicenseService_DetectsTamperedExpiredHardwareMismatch_AndOnlineHttpsFlow()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = signingKey.ExportSubjectPublicKeyInfoPem();
        var prefsService = await CreatePreferencesServiceAsync(new AppPreferences());

        var fixedNow = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        using var service = new LicenseServiceAdapter(
            prefsService,
            verificationPublicKeyPem: publicPem,
            utcNowProvider: () => fixedNow,
            machineIdentityProvider: () => "machine-a");

        var expiredToken = LicenseServiceAdapter.CreateSignedToken(
            new SignedLicensePayload("LIC-EXP", "Pro", "User", fixedNow.AddDays(-10), fixedNow.AddMinutes(-1)),
            signingKey);
        (await service.ActivateLicenseAsync(expiredToken)).State.Should().Be(LicenseStateKind.Expired);
        prefsService.Current.HasLicenseKey.Should().BeFalse();

        var wrongHardwareToken = LicenseServiceAdapter.CreateSignedToken(
            new SignedLicensePayload("LIC-HW", "Pro", "User", fixedNow, HardwareHash: new string('f', 64)),
            signingKey);
        (await service.ActivateLicenseAsync(wrongHardwareToken)).State.Should().Be(LicenseStateKind.HardwareMismatch);
        prefsService.Current.HasLicenseKey.Should().BeFalse();

        var parts = expiredToken.Split('.');
        var tamperedSig = parts[0] + "." + parts[1] + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        (await service.ActivateLicenseAsync(tamperedSig)).State.Should().Be(LicenseStateKind.Invalid);
        (await service.ActivateLicenseAsync("NOT-A-VALID-KEY")).State.Should().Be(LicenseStateKind.Invalid);

        // Online activation sends ONLY licenseKey + hardwareHash and enforces HTTPS
        var issuedToken = LicenseServiceAdapter.CreateSignedToken(
            new SignedLicensePayload("LIC-ONL", "Pro", "Online User", fixedNow, HardwareHash: service.ComputeHardwareFingerprintHash()),
            signingKey);
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { signedToken = issuedToken }), Encoding.UTF8, "application/json")
        });

        using var insecureOnline = new LicenseServiceAdapter(
            prefsService,
            verificationPublicKeyPem: publicPem,
            activationEndpointUrl: "http://license.shima.test/activate",
            httpMessageHandler: handler,
            utcNowProvider: () => fixedNow,
            machineIdentityProvider: () => "machine-a");
        (await insecureOnline.ActivateLicenseAsync("KEY-RAW-123")).State.Should().Be(LicenseStateKind.Invalid);
        handler.RecordedRequests.Should().BeEmpty();

        using var secureOnline = new LicenseServiceAdapter(
            prefsService,
            verificationPublicKeyPem: publicPem,
            activationEndpointUrl: "https://license.shima.test/activate",
            httpMessageHandler: handler,
            utcNowProvider: () => fixedNow,
            machineIdentityProvider: () => "machine-a");
        var onlineResult = await secureOnline.ActivateLicenseAsync("KEY-RAW-123");
        onlineResult.State.Should().Be(LicenseStateKind.Active);
        onlineResult.LicenseId.Should().Be("LIC-ONL");
        handler.RecordedRequests.Should().ContainSingle();
        handler.RecordedRequests[0].Body.Should().Contain("KEY-RAW-123").And.Contain("hardwareHash");
    }

    [Fact]
    public void SanitizedLogs_RedactSignedLicenseTokens()
    {
        var logDir = Path.Combine(Root, "logs");
        var logFile = Path.Combine(logDir, "shima.log");
        var token = "SHIMA1.eyJsaWNlbnNlSWQiOiJMSUMtMSJ9.abcdef123456_XYZ-999";

        var logger = new SanitizedFileLogger(logFile);
        logger.Log(LogLevel.Information, $"Rejected license token {token}");

        var diskLog = File.ReadAllText(logFile);
        diskLog.Should().NotContain(token);
        diskLog.Should().Contain("[redacted-license]");

        var diag = new DiagnosticLogService(logDir);
        File.AppendAllText(logFile, $"diagnostic line with {token}{Environment.NewLine}");
        var sanitizedLines = diag.GetRecentSanitizedLines(20);
        sanitizedLines.Should().NotContain(line => line.Contains(token, StringComparison.Ordinal));
        sanitizedLines.Should().Contain(line => line.Contains("[REDACTED_LICENSE]", StringComparison.Ordinal));
    }

    private async Task<AppPreferencesService> CreatePreferencesServiceAsync(AppPreferences? initial = null)
    {
        var store = new AppPreferencesStore(new AtomicConfigStore<AppPreferences>(Root));
        if (initial is not null)
        {
            await store.SaveAsync(initial);
        }

        var service = new AppPreferencesService(store, new DpapiSecretStore());
        await service.InitializeAsync();
        return service;
    }

    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record RecordedRequest(Uri? Uri, string? Body);

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> RecordedRequests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            RecordedRequests.Add(new RecordedRequest(request.RequestUri, body));
            var response = responder(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
