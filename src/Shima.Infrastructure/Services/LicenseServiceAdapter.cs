using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Shima.Core.Contracts;

namespace Shima.Infrastructure.Services;

/// <summary>
/// Signed offline-verifiable license payload bound optionally to expiration and salted hardware hash.
/// </summary>
public sealed record SignedLicensePayload(
    string LicenseId,
    string Edition,
    string? Licensee,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    string? HardwareHash = null);

/// <summary>
/// Strictly optional offline-first licensing service.
/// Shima works cleanly in <see cref="LicenseStateKind.CommunityOffline"/> without accounts or network I/O;
/// when a signed token is activated, it is verified locally using ECDSA P-256 and cached DPAPI-encrypted.
/// </summary>
public sealed class LicenseServiceAdapter : ILicenseService, IDisposable
{
    public const string TokenPrefix = "SHIMA1";
    private const string DefaultSalt = "shima-hwid-v1";

    public const string DefaultPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIYhUqps75fb44PJToBlnBuVtc0Fg
9eIL+u7KgO15SpfsSPKFGZHF3WAR+MoqbhPbgs+pC+4H2FHML937chrUtg==
-----END PUBLIC KEY-----
""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IAppPreferencesService _preferencesService;
    private readonly string _verificationPublicKeyPem;
    private readonly string? _activationEndpointUrl;
    private readonly HttpMessageHandler? _injectedHandler;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly Func<string> _machineIdentityProvider;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private HttpClient? _httpClient;
    private bool _disposed;

    public LicenseServiceAdapter(
        IAppPreferencesService preferencesService,
        string? verificationPublicKeyPem = null,
        string? activationEndpointUrl = null,
        HttpMessageHandler? httpMessageHandler = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        Func<string>? machineIdentityProvider = null,
        ILogger? logger = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _verificationPublicKeyPem = string.IsNullOrWhiteSpace(verificationPublicKeyPem)
            ? DefaultPublicKeyPem
            : verificationPublicKeyPem;
        _activationEndpointUrl = string.IsNullOrWhiteSpace(activationEndpointUrl)
            ? null
            : activationEndpointUrl.Trim();
        _injectedHandler = httpMessageHandler;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _machineIdentityProvider = machineIdentityProvider ?? ResolveMachineIdentity;
        _logger = logger;
    }

    public string ComputeHardwareFingerprintHash(string? salt = null)
    {
        var effectiveSalt = string.IsNullOrWhiteSpace(salt) ? DefaultSalt : salt.Trim();
        var identity = _machineIdentityProvider();
        var input = $"{effectiveSalt}|{RuntimeInformation.ProcessArchitecture}|{identity}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public LicenseStatus GetCachedStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cached = _preferencesService.ReadLicenseStatePayload();
        if (string.IsNullOrWhiteSpace(cached))
        {
            return new LicenseStatus(
                LicenseStateKind.CommunityOffline,
                IsValid: true,
                Edition: "Community (Offline)",
                Message: "Community offline edition — all local features active without an account.");
        }

        return VerifySignedToken(cached.Trim());
    }

    public async Task<LicenseStatus> ActivateLicenseAsync(string licenseKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var trimmed = licenseKey?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return new LicenseStatus(
                LicenseStateKind.Invalid,
                IsValid: false,
                Edition: "Unlicensed",
                Message: "License key cannot be empty.");
        }

        if (trimmed.StartsWith(TokenPrefix + ".", StringComparison.Ordinal))
        {
            var localStatus = VerifySignedToken(trimmed);
            if (localStatus.State == LicenseStateKind.Active)
            {
                await _preferencesService.SaveAsync(
                    _preferencesService.Current,
                    newLicenseKey: trimmed,
                    ct: ct).ConfigureAwait(false);
                _logger?.Info($"Activated offline signed license for edition '{localStatus.Edition}'.");
            }

            return localStatus;
        }

        if (!string.IsNullOrWhiteSpace(_activationEndpointUrl)
            && !_preferencesService.Current.UpdatesOfflineMode)
        {
            if (!Uri.TryCreate(_activationEndpointUrl, UriKind.Absolute, out var activationUri)
                || !string.Equals(activationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new LicenseStatus(
                    LicenseStateKind.Invalid,
                    IsValid: false,
                    Edition: "Unlicensed",
                    Message: "License activation endpoint must use HTTPS.");
            }

            try
            {
                var client = GetOrCreateHttpClient();
                var payload = new OnlineActivationRequest(trimmed, ComputeHardwareFingerprintHash());
                using var content = JsonContent.Create(payload, options: JsonOptions);
                using var response = await client.PostAsync(activationUri, content, ct).ConfigureAwait(false);

                var finalScheme = response.RequestMessage?.RequestUri?.Scheme;
                if (!string.Equals(finalScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return new LicenseStatus(
                        LicenseStateKind.Invalid,
                        IsValid: false,
                        Edition: "Unlicensed",
                        Message: "License activation redirect downgraded away from HTTPS.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new LicenseStatus(
                        LicenseStateKind.Invalid,
                        IsValid: false,
                        Edition: "Unlicensed",
                        Message: $"License activation rejected with HTTP {(int)response.StatusCode}.");
                }

                var responseBody = await response.Content.ReadFromJsonAsync<OnlineActivationResponse>(JsonOptions, ct)
                    .ConfigureAwait(false);
                var signedToken = responseBody?.SignedToken?.Trim();
                if (string.IsNullOrWhiteSpace(signedToken)
                    || !signedToken.StartsWith(TokenPrefix + ".", StringComparison.Ordinal))
                {
                    return new LicenseStatus(
                        LicenseStateKind.Invalid,
                        IsValid: false,
                        Edition: "Unlicensed",
                        Message: "License server did not return a valid signed token.");
                }

                var onlineStatus = VerifySignedToken(signedToken);
                if (onlineStatus.State == LicenseStateKind.Active)
                {
                    await _preferencesService.SaveAsync(
                        _preferencesService.Current,
                        newLicenseKey: signedToken,
                        ct: ct).ConfigureAwait(false);
                    _logger?.Info($"Activated online-issued license for edition '{onlineStatus.Edition}'.");
                }

                return onlineStatus;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger?.Warn("Online license activation request failed.", ex);
                return new LicenseStatus(
                    LicenseStateKind.Invalid,
                    IsValid: false,
                    Edition: "Unlicensed",
                    Message: "Unable to reach license activation service.");
            }
        }

        return new LicenseStatus(
            LicenseStateKind.Invalid,
            IsValid: false,
            Edition: "Unlicensed",
            Message: "Offline activation requires a signed SHIMA1 license token.");
    }

    public async Task DeactivateLocalLicenseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await _preferencesService.SaveAsync(
            _preferencesService.Current,
            clearLicenseKey: true,
            ct: ct).ConfigureAwait(false);
        _logger?.Info("Deactivated local license cache and reverted to Community offline edition.");
    }

    public LicenseStatus VerifySignedToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return new LicenseStatus(
                LicenseStateKind.Invalid,
                IsValid: false,
                Edition: "Unlicensed",
                Message: "License token is empty.");
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], TokenPrefix, StringComparison.Ordinal)
            || !TryDecodeBase64Url(parts[1], out var payloadBytes)
            || !TryDecodeBase64Url(parts[2], out var signatureBytes)
            || payloadBytes.Length == 0
            || signatureBytes.Length == 0)
        {
            return new LicenseStatus(
                LicenseStateKind.Invalid,
                IsValid: false,
                Edition: "Unlicensed",
                Message: "License token format is invalid.");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(_verificationPublicKeyPem);
            var validSignature = ecdsa.VerifyData(
                payloadBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            if (!validSignature)
            {
                return new LicenseStatus(
                    LicenseStateKind.Invalid,
                    IsValid: false,
                    Edition: "Unlicensed",
                    Message: "License token signature verification failed.");
            }

            var payload = JsonSerializer.Deserialize<SignedLicensePayload>(payloadBytes, JsonOptions);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.LicenseId)
                || string.IsNullOrWhiteSpace(payload.Edition))
            {
                return new LicenseStatus(
                    LicenseStateKind.Invalid,
                    IsValid: false,
                    Edition: "Unlicensed",
                    Message: "License token payload is incomplete.");
            }

            var now = _utcNowProvider();
            if (payload.ExpiresAtUtc.HasValue && payload.ExpiresAtUtc.Value < now)
            {
                return new LicenseStatus(
                    LicenseStateKind.Expired,
                    IsValid: false,
                    Edition: payload.Edition.Trim(),
                    Licensee: payload.Licensee,
                    LicenseId: payload.LicenseId.Trim(),
                    ExpiresAtUtc: payload.ExpiresAtUtc,
                    Message: $"License '{payload.LicenseId}' expired on {payload.ExpiresAtUtc.Value:yyyy-MM-dd}.");
            }

            if (!string.IsNullOrWhiteSpace(payload.HardwareHash))
            {
                var expectedBytes = Encoding.ASCII.GetBytes(payload.HardwareHash.Trim().ToLowerInvariant());
                var localBytes = Encoding.ASCII.GetBytes(ComputeHardwareFingerprintHash());
                if (expectedBytes.Length != localBytes.Length
                    || !CryptographicOperations.FixedTimeEquals(expectedBytes, localBytes))
                {
                    return new LicenseStatus(
                        LicenseStateKind.HardwareMismatch,
                        IsValid: false,
                        Edition: payload.Edition.Trim(),
                        Licensee: payload.Licensee,
                        LicenseId: payload.LicenseId.Trim(),
                        ExpiresAtUtc: payload.ExpiresAtUtc,
                        Message: "License token is bound to a different device.");
                }
            }

            var summary = string.IsNullOrWhiteSpace(payload.Licensee)
                ? $"Licensed ({payload.Edition.Trim()}) — verified offline"
                : $"Licensed ({payload.Edition.Trim()}) for {payload.Licensee.Trim()}";

            return new LicenseStatus(
                LicenseStateKind.Active,
                IsValid: true,
                Edition: payload.Edition.Trim(),
                Licensee: payload.Licensee?.Trim(),
                LicenseId: payload.LicenseId.Trim(),
                ExpiresAtUtc: payload.ExpiresAtUtc,
                Message: summary);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            return new LicenseStatus(
                LicenseStateKind.Invalid,
                IsValid: false,
                Edition: "Unlicensed",
                Message: "License token could not be verified.");
        }
    }

    public static string CreateSignedToken(SignedLicensePayload payload, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signingKey);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signatureBytes = signingKey.SignData(
            payloadBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{TokenPrefix}.{EncodeBase64Url(payloadBytes)}.{EncodeBase64Url(signatureBytes)}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _httpClient?.Dispose();
            _httpClient = null;
        }
    }

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

            _httpClient.Timeout = TimeSpan.FromSeconds(20);
            return _httpClient;
        }
    }

    private static string ResolveMachineIdentity()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            if (key?.GetValue("MachineGuid") is string machineGuid && !string.IsNullOrWhiteSpace(machineGuid))
            {
                return machineGuid.Trim();
            }
        }
        catch
        {
            // Fall back cleanly when registry read is restricted.
        }

        return Environment.MachineName;
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecodeBase64Url(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Replace('-', '+').Replace('_', '/');
        var mod = normalized.Length % 4;
        if (mod == 1)
        {
            return false;
        }

        if (mod == 2)
        {
            normalized += "==";
        }
        else if (mod == 3)
        {
            normalized += "=";
        }

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record OnlineActivationRequest(string LicenseKey, string HardwareHash);

    private sealed class OnlineActivationResponse
    {
        public string? SignedToken { get; set; }
    }
}
