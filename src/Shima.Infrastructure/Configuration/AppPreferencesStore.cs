using System.Text.Json;
using System.Text.Json.Serialization;
using Shima.Infrastructure.Storage;

namespace Shima.Infrastructure.Configuration;

[JsonConverter(typeof(TargetMonitorModeJsonConverter))]
public enum TargetMonitorMode
{
    Primary,
    ActiveCursor,
    SpecificMonitorId
}

public sealed class TargetMonitorModeJsonConverter : JsonConverter<TargetMonitorMode>
{
    public override TargetMonitorMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return Enum.TryParse<TargetMonitorMode>(value, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                ? parsed
                : TargetMonitorMode.Primary;
        }

        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(TargetMonitorMode), numeric))
        {
            return (TargetMonitorMode)numeric;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return TargetMonitorMode.Primary;
        }

        throw new JsonException($"Invalid target monitor mode token '{reader.TokenType}'.");
    }

    public override void Write(Utf8JsonWriter writer, TargetMonitorMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class AppPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public bool StartWithWindows { get; set; }
    public TargetMonitorMode TargetMonitorMode { get; set; } = TargetMonitorMode.Primary;
    public string? SpecificMonitorDeviceId { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public int HoverExpandDelayMs { get; set; } = 350;
    public int AutoCollapseDelayMs { get; set; } = 450;
    public string? GlobalToggleHotkey { get; set; }
    public bool FullscreenBreakthroughAlerts { get; set; }
    public bool SyncInboxDismissalWithActionCenter { get; set; } = true;
    public int ClipboardMaxItems { get; set; } = 100;
    public int ClipboardRetentionDays { get; set; } = 30;
    public int InboxRetentionDays { get; set; } = 14;
    public bool ClaudeCodeOptInEnabled { get; set; }
    public bool OpenCodeOptInEnabled { get; set; }
    public string? OpenCodeEndpointUrl { get; set; }
    public byte[]? OpenCodeServerPassword { get; set; }
    public bool UpdatesOfflineMode { get; set; }
    public byte[]? LicenseState { get; set; }
}

public sealed class AppPreferencesStore
{
    public const string DefaultRelativePath = "config/settings.json";

    private readonly IAtomicConfigStore<AppPreferences> _store;

    public AppPreferencesStore(IAtomicConfigStore<AppPreferences> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AppPreferences> LoadAsync()
    {
        var preferences = await _store
            .LoadOrRecoverAsync(DefaultRelativePath, static () => new AppPreferences())
            .ConfigureAwait(false);
        return Normalize(preferences);
    }

    public Task SaveAsync(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return _store.SaveAtomicAsync(DefaultRelativePath, Normalize(preferences));
    }

    public static AppPreferences Normalize(AppPreferences? preferences)
    {
        var result = preferences ?? new AppPreferences();
        result.SchemaVersion = Math.Max(1, result.SchemaVersion);
        result.HoverExpandDelayMs = Math.Clamp(result.HoverExpandDelayMs, 100, 1500);
        result.AutoCollapseDelayMs = Math.Clamp(result.AutoCollapseDelayMs, 200, 3000);
        result.ClipboardMaxItems = Math.Clamp(result.ClipboardMaxItems, 20, 1000);
        result.ClipboardRetentionDays = Math.Clamp(result.ClipboardRetentionDays, 1, 365);
        result.InboxRetentionDays = Math.Clamp(result.InboxRetentionDays, 1, 90);

        if (!Enum.IsDefined(result.TargetMonitorMode))
        {
            result.TargetMonitorMode = TargetMonitorMode.Primary;
        }

        if (result.TargetMonitorMode != TargetMonitorMode.SpecificMonitorId)
        {
            result.SpecificMonitorDeviceId = null;
        }

        if (!IsValidOpenCodeEndpoint(result.OpenCodeEndpointUrl))
        {
            result.OpenCodeEndpointUrl = null;
        }

        return result;
    }

    public static bool IsValidOpenCodeEndpoint(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal);
    }
}
