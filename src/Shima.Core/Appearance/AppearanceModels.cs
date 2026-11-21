namespace Shima.Core.Appearance;

/// <summary>
/// Customizable 6-token appearance slot identifiers matching the Appearance &amp; Color Picker contracts.
/// </summary>
public enum AppearanceTokenSlot
{
    Background,
    Accent,
    Border,
    Glow,
    PrimaryText,
    SecondaryIcon
}

/// <summary>
/// Surface state targeted by an appearance token edit or reset operation.
/// </summary>
public enum IslandStateTarget
{
    Collapsed,
    Expanded,
    Both
}

/// <summary>
/// 6-token color palette driving an individual Dynamic Island surface state (Collapsed or Expanded).
/// </summary>
public sealed record IslandColorPalette
{
    public const string MidnightBackground = "#000000";
    public const string MidnightAccent = "#FFFFFF";
    public const string MidnightBorder = "#1F1F24";
    public const string MidnightGlow = "#000000";
    public const string MidnightPrimaryText = "#F8FAFC";
    public const string MidnightSecondaryIcon = "#94A3B8";

    public static IslandColorPalette Midnight { get; } = new();

    public string BackgroundHex { get; init; } = MidnightBackground;

    public string AccentHex { get; init; } = MidnightAccent;

    public string BorderHex { get; init; } = MidnightBorder;

    public string GlowHex { get; init; } = MidnightGlow;

    public string PrimaryTextHex { get; init; } = MidnightPrimaryText;

    public string SecondaryIconHex { get; init; } = MidnightSecondaryIcon;

    public static string GetMidnightDefault(AppearanceTokenSlot slot) => slot switch
    {
        AppearanceTokenSlot.Background => MidnightBackground,
        AppearanceTokenSlot.Accent => MidnightAccent,
        AppearanceTokenSlot.Border => MidnightBorder,
        AppearanceTokenSlot.Glow => MidnightGlow,
        AppearanceTokenSlot.PrimaryText => MidnightPrimaryText,
        AppearanceTokenSlot.SecondaryIcon => MidnightSecondaryIcon,
        _ => MidnightAccent
    };

    public string GetSlotHex(AppearanceTokenSlot slot) => slot switch
    {
        AppearanceTokenSlot.Background => BackgroundHex,
        AppearanceTokenSlot.Accent => AccentHex,
        AppearanceTokenSlot.Border => BorderHex,
        AppearanceTokenSlot.Glow => GlowHex,
        AppearanceTokenSlot.PrimaryText => PrimaryTextHex,
        AppearanceTokenSlot.SecondaryIcon => SecondaryIconHex,
        _ => AccentHex
    };

    public IslandColorPalette WithSlotHex(AppearanceTokenSlot slot, string hex) => slot switch
    {
        AppearanceTokenSlot.Background => this with { BackgroundHex = hex },
        AppearanceTokenSlot.Accent => this with { AccentHex = hex },
        AppearanceTokenSlot.Border => this with { BorderHex = hex },
        AppearanceTokenSlot.Glow => this with { GlowHex = hex },
        AppearanceTokenSlot.PrimaryText => this with { PrimaryTextHex = hex },
        AppearanceTokenSlot.SecondaryIcon => this with { SecondaryIconHex = hex },
        _ => this
    };
}

/// <summary>
/// Persisted appearance configuration stored atomically in %LOCALAPPDATA%\Shima\config\appearance.json per data-model.md.
/// </summary>
public sealed record AppearanceConfiguration
{
    public const string DefaultPresetId = "builtin.midnight";
    public const double DefaultGlowOpacity = 0.45;
    public const double DefaultBorderThicknessDip = 1.0;

    public static AppearanceConfiguration Default { get; } = new();

    public int SchemaVersion { get; init; } = 1;

    public string ActivePresetId { get; init; } = DefaultPresetId;

    public bool LinkCollapsedAndExpanded { get; init; } = true;

    public bool UsePerWidgetAccents { get; init; }

    public IslandColorPalette CollapsedTokens { get; init; } = IslandColorPalette.Midnight;

    public IslandColorPalette ExpandedTokens { get; init; } = IslandColorPalette.Midnight;

    public double GlowOpacity { get; init; } = DefaultGlowOpacity;

    public double BorderThicknessDip { get; init; } = DefaultBorderThicknessDip;

    public IReadOnlyDictionary<string, string> PerWidgetAccents { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Built-in or user-created theme preset pairing Collapsed and Expanded palettes.
/// </summary>
public sealed record ColorPreset
{
    public required string PresetId { get; init; }

    public required string Name { get; init; }

    public bool IsBuiltIn { get; init; }

    public required IslandColorPalette CollapsedTokens { get; init; }

    public required IslandColorPalette ExpandedTokens { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Named user swatch stored inside PresetCatalog.savedSwatches[].
/// </summary>
public sealed record SavedColorSwatch
{
    public required string SwatchId { get; init; }

    public required string Label { get; init; }

    public required string Hex { get; init; }
}

/// <summary>
/// Persisted preset catalog, saved swatches, and recent colors FIFO ring stored in config/presets.json.
/// </summary>
public sealed record PresetCatalog
{
    public const int MaxRecentColors = 12;

    public static PresetCatalog Empty { get; } = new();

    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<ColorPreset> CustomPresets { get; init; } = Array.Empty<ColorPreset>();

    public IReadOnlyList<SavedColorSwatch> SavedSwatches { get; init; } = Array.Empty<SavedColorSwatch>();

    public IReadOnlyList<string> RecentColors { get; init; } = Array.Empty<string>();
}
