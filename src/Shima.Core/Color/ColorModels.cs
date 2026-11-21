using Shima.Core.Appearance;
using Shima.Core.Contracts;

namespace Shima.Core.Color;

/// <summary>
/// Canonical color representation pairing normalized uppercase #RRGGBB hex, 8-bit sRGB channels, and IEEE-754 HSB values.
/// </summary>
public sealed record ShimaColor(
    string Hex,
    byte R,
    byte G,
    byte B,
    double Hue,
    double Saturation,
    double Brightness);

/// <summary>
/// Identifies the UI or programmatic source of a color picker edit.
/// </summary>
public enum ColorChangeSource
{
    GradientField,
    HueSlider,
    HexInput,
    HsbInput,
    RgbInput,
    RecentColorSwatch,
    SavedSwatch,
    OriginalSwatchRevert,
    Preset,
    Reset
}

/// <summary>
/// Request parameters passed when launching the Advanced Color Picker dialog.
/// </summary>
public sealed record ColorPickerRequest(
    AppearanceTokenSlot TargetSlot,
    IslandStateTarget TargetState,
    string DisplayName,
    string InitialHex,
    PreviewTargetState InitialPreviewState = PreviewTargetState.Expanded);

/// <summary>
/// Outcome returned when the Advanced Color Picker closes.
/// </summary>
public sealed record ColorPickerResult(
    bool Confirmed,
    ShimaColor SelectedColor,
    ColorChangeSource LastSource,
    bool WasReset = false);

/// <summary>
/// WCAG 2.1 contrast classification role for theme tokens.
/// </summary>
public enum ContrastRole
{
    PrimaryText,
    SecondaryIcon,
    Accent
}

/// <summary>
/// Non-blocking contrast advisory item detected against the targeted background surface.
/// </summary>
public sealed record ContrastIssue(
    AppearanceTokenSlot Slot,
    IslandStateTarget TargetState,
    string ElementLabel,
    ContrastRole Role,
    double Ratio,
    double RequiredMinimum,
    string RecommendedHex);

/// <summary>
/// Aggregated WCAG 2.1 contrast audit result across active island palette tokens.
/// </summary>
public sealed record ContrastAuditReport(
    bool HasWarnings,
    IReadOnlyList<ContrastIssue> Issues)
{
    public static ContrastAuditReport Empty { get; } = new(false, Array.Empty<ContrastIssue>());
}

/// <summary>
/// Color conversion, clamping, parsing, and contrast audit contract.
/// </summary>
public interface IColorProcessingService
{
    bool TryParseHex(string? hexInput, out ShimaColor? color, double? preservedHue = null);

    ShimaColor FromHex(string hexInput, double? preservedHue = null);

    ShimaColor FromRgb(byte r, byte g, byte b, double? preservedHue = null);

    ShimaColor FromRgbClamped(int r, int g, int b, double? preservedHue = null);

    ShimaColor FromHsb(double hue, double saturation, double brightness);

    double CalculateRelativeLuminance(byte r, byte g, byte b);

    double CalculateRelativeLuminance(string hex);

    double CalculateContrastRatio(string foregroundHex, string backgroundHex);

    ContrastAuditReport ValidatePaletteContrast(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded);

    ContrastAuditReport ValidateConfigurationContrast(AppearanceConfiguration configuration);

    string SolveRecommendedForeground(string foregroundHex, string backgroundHex, double targetContrastRatio);
}

/// <summary>
/// WCAG 2.1 contrast ratio verification and auto-remediation contract.
/// </summary>
public interface IContrastAuditor
{
    double RequiredMinimumFor(ContrastRole role);

    double CalculateContrastRatio(string foregroundHex, string backgroundHex);

    bool MeetsContrast(string foregroundHex, string backgroundHex, ContrastRole role);

    ContrastAuditReport ValidatePalette(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded);

    ContrastAuditReport ValidateConfiguration(AppearanceConfiguration configuration);

    string SolveRecommendedForeground(string foregroundHex, string backgroundHex, double targetRatio);
}
