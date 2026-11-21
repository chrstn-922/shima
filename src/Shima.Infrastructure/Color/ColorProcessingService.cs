using Shima.Core.Appearance;
using Shima.Core.Color;

namespace Shima.Infrastructure.Color;

/// <summary>
/// Infrastructure DI adapter delegating HSB/RGB/HEX math and WCAG contrast auditing to <see cref="CoreColorProcessingEngine"/>.
/// </summary>
public sealed class ColorProcessingService : IColorProcessingService, IContrastAuditor
{
    private readonly CoreColorProcessingEngine _engine = new();

    public bool TryParseHex(string? hexInput, out ShimaColor? color, double? preservedHue = null) =>
        _engine.TryParseHex(hexInput, out color, preservedHue);

    public ShimaColor FromHex(string hexInput, double? preservedHue = null) =>
        _engine.FromHex(hexInput, preservedHue);

    public ShimaColor FromRgb(byte r, byte g, byte b, double? preservedHue = null) =>
        _engine.FromRgb(r, g, b, preservedHue);

    public ShimaColor FromRgbClamped(int r, int g, int b, double? preservedHue = null) =>
        _engine.FromRgbClamped(r, g, b, preservedHue);

    public ShimaColor FromHsb(double hue, double saturation, double brightness) =>
        _engine.FromHsb(hue, saturation, brightness);

    public double CalculateRelativeLuminance(byte r, byte g, byte b) =>
        _engine.CalculateRelativeLuminance(r, g, b);

    public double CalculateRelativeLuminance(string hex) =>
        _engine.CalculateRelativeLuminance(hex);

    public double CalculateContrastRatio(string foregroundHex, string backgroundHex) =>
        _engine.CalculateContrastRatio(foregroundHex, backgroundHex);

    public ContrastAuditReport ValidatePaletteContrast(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded) =>
        _engine.ValidatePaletteContrast(palette, target);

    public ContrastAuditReport ValidateConfigurationContrast(AppearanceConfiguration configuration) =>
        _engine.ValidateConfigurationContrast(configuration);

    public string SolveRecommendedForeground(string foregroundHex, string backgroundHex, double targetContrastRatio) =>
        _engine.SolveRecommendedForeground(foregroundHex, backgroundHex, targetContrastRatio);

    public double RequiredMinimumFor(ContrastRole role) =>
        _engine.RequiredMinimumFor(role);

    public bool MeetsContrast(string foregroundHex, string backgroundHex, ContrastRole role) =>
        _engine.MeetsContrast(foregroundHex, backgroundHex, role);

    public ContrastAuditReport ValidatePalette(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded) =>
        _engine.ValidatePalette(palette, target);

    public ContrastAuditReport ValidateConfiguration(AppearanceConfiguration configuration) =>
        _engine.ValidateConfiguration(configuration);
}
