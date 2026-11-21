using System.Globalization;
using Shima.Core.Appearance;

namespace Shima.Core.Color;

/// <summary>
/// Deterministic IEEE-754 HSB/RGB/HEX color math and W3C WCAG 2.1 contrast calculation engine.
/// </summary>
public sealed class CoreColorProcessingEngine : IColorProcessingService, IContrastAuditor
{
    public const double PrimaryTextMinimumContrast = 4.5;
    public const double SecondaryIconMinimumContrast = 3.0;
    public const double AccentMinimumContrast = 3.0;
    private const int BinarySearchIterations = 16;

    public bool TryParseHex(string? hexInput, out ShimaColor? color, double? preservedHue = null)
    {
        color = null;
        if (string.IsNullOrWhiteSpace(hexInput))
        {
            return false;
        }

        var span = hexInput.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        byte r;
        byte g;
        byte b;

        if (span.Length == 3)
        {
            if (!TryParseNibble(span[0], out var rn)
                || !TryParseNibble(span[1], out var gn)
                || !TryParseNibble(span[2], out var bn))
            {
                return false;
            }

            r = (byte)((rn << 4) | rn);
            g = (byte)((gn << 4) | gn);
            b = (byte)((bn << 4) | bn);
        }
        else if (span.Length == 6)
        {
            if (!byte.TryParse(span[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
                || !byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
                || !byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        color = FromRgb(r, g, b, preservedHue);
        return true;
    }

    public ShimaColor FromHex(string hexInput, double? preservedHue = null)
    {
        if (TryParseHex(hexInput, out var parsed, preservedHue) && parsed is not null)
        {
            return parsed;
        }

        throw new FormatException($"Invalid hex color value '{hexInput}'. Expected #RGB or #RRGGBB.");
    }

    public ShimaColor FromRgb(byte r, byte g, byte b, double? preservedHue = null)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;

        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        var brightness = max * 100.0;
        var saturation = max <= 0.0 ? 0.0 : (delta / max) * 100.0;

        double hue;
        if (delta <= 1e-12)
        {
            hue = preservedHue.HasValue ? ClampPreserveHueBoundary(preservedHue.Value) : 0.0;
        }
        else
        {
            if (max == rf)
            {
                hue = 60.0 * (((gf - bf) / delta) % 6.0);
            }
            else if (max == gf)
            {
                hue = 60.0 * (((bf - rf) / delta) + 2.0);
            }
            else
            {
                hue = 60.0 * (((rf - gf) / delta) + 4.0);
            }

            if (hue < 0.0)
            {
                hue += 360.0;
            }
        }

        var hex = FormatHex(r, g, b);
        return new ShimaColor(hex, r, g, b, hue, saturation, brightness);
    }

    public ShimaColor FromRgbClamped(int r, int g, int b, double? preservedHue = null) =>
        FromRgb(
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255),
            preservedHue);

    public ShimaColor FromHsb(double hue, double saturation, double brightness)
    {
        var storedHue = ClampPreserveHueBoundary(hue);
        var clampedS = double.IsFinite(saturation) ? Math.Clamp(saturation, 0.0, 100.0) : 0.0;
        var clampedB = double.IsFinite(brightness) ? Math.Clamp(brightness, 0.0, 100.0) : 0.0;

        var effectiveHue = storedHue >= 360.0 ? storedHue % 360.0 : storedHue;
        var sNorm = clampedS / 100.0;
        var vNorm = clampedB / 100.0;

        var c = vNorm * sNorm;
        var sector = effectiveHue / 60.0;
        var x = c * (1.0 - Math.Abs((sector % 2.0) - 1.0));
        var m = vNorm - c;

        var (r1, g1, b1) = sector switch
        {
            < 1.0 => (c, x, 0.0),
            < 2.0 => (x, c, 0.0),
            < 3.0 => (0.0, c, x),
            < 4.0 => (0.0, x, c),
            < 5.0 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        var r = (byte)Math.Clamp((int)Math.Round((r1 + m) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round((g1 + m) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round((b1 + m) * 255.0, MidpointRounding.AwayFromZero), 0, 255);

        return new ShimaColor(FormatHex(r, g, b), r, g, b, storedHue, clampedS, clampedB);
    }

    public double CalculateRelativeLuminance(byte r, byte g, byte b) =>
        (0.2126 * LinearizeChannel(r))
        + (0.7152 * LinearizeChannel(g))
        + (0.0722 * LinearizeChannel(b));

    public double CalculateRelativeLuminance(string hex)
    {
        var color = FromHex(hex);
        return CalculateRelativeLuminance(color.R, color.G, color.B);
    }

    public double CalculateContrastRatio(string foregroundHex, string backgroundHex)
    {
        var fg = FromHex(foregroundHex);
        var bg = FromHex(backgroundHex);

        var l1 = CalculateRelativeLuminance(fg.R, fg.G, fg.B);
        var l2 = CalculateRelativeLuminance(bg.R, bg.G, bg.B);

        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public double RequiredMinimumFor(ContrastRole role) => role switch
    {
        ContrastRole.PrimaryText => PrimaryTextMinimumContrast,
        ContrastRole.SecondaryIcon => SecondaryIconMinimumContrast,
        ContrastRole.Accent => AccentMinimumContrast,
        _ => PrimaryTextMinimumContrast
    };

    public bool MeetsContrast(string foregroundHex, string backgroundHex, ContrastRole role) =>
        CalculateContrastRatio(foregroundHex, backgroundHex) >= RequiredMinimumFor(role);

    public ContrastAuditReport ValidatePaletteContrast(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded) =>
        ValidatePalette(palette, target);

    public ContrastAuditReport ValidatePalette(IslandColorPalette palette, IslandStateTarget target = IslandStateTarget.Expanded)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var issues = new List<ContrastIssue>();
        CheckToken(AppearanceTokenSlot.PrimaryText, palette.PrimaryTextHex, palette.BackgroundHex, target, ContrastRole.PrimaryText, issues);
        CheckToken(AppearanceTokenSlot.SecondaryIcon, palette.SecondaryIconHex, palette.BackgroundHex, target, ContrastRole.SecondaryIcon, issues);
        CheckToken(AppearanceTokenSlot.Accent, palette.AccentHex, palette.BackgroundHex, target, ContrastRole.Accent, issues);

        return new ContrastAuditReport(issues.Count > 0, issues);
    }

    public ContrastAuditReport ValidateConfigurationContrast(AppearanceConfiguration configuration) =>
        ValidateConfiguration(configuration);

    public ContrastAuditReport ValidateConfiguration(AppearanceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ContrastIssue>();
        var collapsedReport = ValidatePalette(configuration.CollapsedTokens, IslandStateTarget.Collapsed);
        issues.AddRange(collapsedReport.Issues);

        if (!configuration.LinkCollapsedAndExpanded || configuration.ExpandedTokens != configuration.CollapsedTokens)
        {
            var expandedReport = ValidatePalette(configuration.ExpandedTokens, IslandStateTarget.Expanded);
            issues.AddRange(expandedReport.Issues);
        }

        return new ContrastAuditReport(issues.Count > 0, issues);
    }

    public string SolveRecommendedForeground(string foregroundHex, string backgroundHex, double targetContrastRatio)
    {
        var fg = FromHex(foregroundHex);
        var bg = FromHex(backgroundHex);

        var target = Math.Max(1.0, targetContrastRatio);
        var currentRatio = CalculateContrastRatio(fg.Hex, bg.Hex);
        if (currentRatio >= target)
        {
            return fg.Hex;
        }

        var lighterCandidate = TrySolveBrightnessDirection(fg, bg.Hex, target, moveBrighter: true, out var lighterDelta);
        var darkerCandidate = TrySolveBrightnessDirection(fg, bg.Hex, target, moveBrighter: false, out var darkerDelta);

        if (lighterCandidate is not null && darkerCandidate is not null)
        {
            return lighterDelta <= darkerDelta ? lighterCandidate : darkerCandidate;
        }

        if (lighterCandidate is not null)
        {
            return lighterCandidate;
        }

        if (darkerCandidate is not null)
        {
            return darkerCandidate;
        }

        var bgLuminance = CalculateRelativeLuminance(bg.R, bg.G, bg.B);
        var whiteContrast = CalculateContrastRatio("#FFFFFF", bg.Hex);
        var blackContrast = CalculateContrastRatio("#000000", bg.Hex);

        if (whiteContrast >= target && bgLuminance <= 0.5)
        {
            var lo = 0.0;
            var hi = fg.Saturation;
            var best = "#FFFFFF";
            for (var i = 0; i < BinarySearchIterations; i++)
            {
                var mid = (lo + hi) * 0.5;
                var trial = FromHsb(fg.Hue, mid, 100.0);
                if (CalculateContrastRatio(trial.Hex, bg.Hex) >= target)
                {
                    best = trial.Hex;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return best;
        }

        return blackContrast >= whiteContrast ? "#000000" : "#FFFFFF";
    }

    private string? TrySolveBrightnessDirection(
        ShimaColor fg,
        string backgroundHex,
        double target,
        bool moveBrighter,
        out double delta)
    {
        var extremeBrightness = moveBrighter ? 100.0 : 0.0;
        var extremeColor = FromHsb(fg.Hue, fg.Saturation, extremeBrightness);
        if (CalculateContrastRatio(extremeColor.Hex, backgroundHex) < target)
        {
            delta = double.MaxValue;
            return null;
        }

        var lo = moveBrighter ? fg.Brightness : 0.0;
        var hi = moveBrighter ? 100.0 : fg.Brightness;
        var bestHex = extremeColor.Hex;
        var bestBrightness = extremeBrightness;

        for (var i = 0; i < BinarySearchIterations; i++)
        {
            var mid = (lo + hi) * 0.5;
            var trial = FromHsb(fg.Hue, fg.Saturation, mid);
            var passes = CalculateContrastRatio(trial.Hex, backgroundHex) >= target;

            if (moveBrighter)
            {
                if (passes)
                {
                    bestHex = trial.Hex;
                    bestBrightness = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
            else
            {
                if (passes)
                {
                    bestHex = trial.Hex;
                    bestBrightness = mid;
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }
        }

        delta = Math.Abs(bestBrightness - fg.Brightness);
        return bestHex;
    }

    private void CheckToken(
        AppearanceTokenSlot slot,
        string fgHex,
        string bgHex,
        IslandStateTarget target,
        ContrastRole role,
        List<ContrastIssue> issues)
    {
        if (!TryParseHex(fgHex, out var fg) || fg is null || !TryParseHex(bgHex, out var bg) || bg is null)
        {
            return;
        }

        var minimum = RequiredMinimumFor(role);
        var ratio = CalculateContrastRatio(fg.Hex, bg.Hex);
        if (ratio + 1e-9 < minimum)
        {
            var recommended = SolveRecommendedForeground(fg.Hex, bg.Hex, minimum);
            var label = $"{GetSlotDisplayLabel(slot)} vs {target} Background";
            issues.Add(new ContrastIssue(slot, target, label, role, ratio, minimum, recommended));
        }
    }

    private static string GetSlotDisplayLabel(AppearanceTokenSlot slot) => slot switch
    {
        AppearanceTokenSlot.PrimaryText => "Primary Text",
        AppearanceTokenSlot.SecondaryIcon => "Secondary Icon",
        AppearanceTokenSlot.Accent => "Accent",
        AppearanceTokenSlot.Border => "Border",
        AppearanceTokenSlot.Glow => "Glow",
        _ => "Background"
    };

    private static double LinearizeChannel(byte channel)
    {
        var srgb = channel / 255.0;
        return srgb <= 0.04045
            ? srgb / 12.92
            : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    private static double ClampPreserveHueBoundary(double hue)
    {
        if (!double.IsFinite(hue))
        {
            return 0.0;
        }

        if (hue == 360.0)
        {
            return 360.0;
        }

        var wrapped = hue % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    private static bool TryParseNibble(char ch, out int value)
    {
        if (ch is >= '0' and <= '9')
        {
            value = ch - '0';
            return true;
        }

        if (ch is >= 'a' and <= 'f')
        {
            value = 10 + (ch - 'a');
            return true;
        }

        if (ch is >= 'A' and <= 'F')
        {
            value = 10 + (ch - 'A');
            return true;
        }

        value = 0;
        return false;
    }

    private static string FormatHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
