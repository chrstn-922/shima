using FluentAssertions;
using Shima.Core.Appearance;
using Shima.Core.Color;
using Xunit;

namespace Shima.Core.Tests;

public sealed class ContrastAuditTests
{
    private readonly CoreColorProcessingEngine _engine = new();

    [Fact]
    public void CalculateRelativeLuminanceAndContrast_MatchesW3cWcag21ReferenceValues()
    {
        _engine.CalculateRelativeLuminance("#000000").Should().BeApproximately(0.0, 1e-6);
        _engine.CalculateRelativeLuminance("#FFFFFF").Should().BeApproximately(1.0, 1e-6);
        _engine.CalculateRelativeLuminance("#FF0000").Should().BeApproximately(0.2126, 1e-4);
        _engine.CalculateRelativeLuminance("#00FF00").Should().BeApproximately(0.7152, 1e-4);
        _engine.CalculateRelativeLuminance("#0000FF").Should().BeApproximately(0.0722, 1e-4);

        _engine.CalculateContrastRatio("#FFFFFF", "#000000").Should().BeApproximately(21.0, 1e-3);
        _engine.CalculateContrastRatio("#000000", "#FFFFFF").Should().BeApproximately(21.0, 1e-3);
        _engine.CalculateContrastRatio("#123456", "#123456").Should().BeApproximately(1.0, 1e-6);

        _engine.CalculateContrastRatio("#767676", "#FFFFFF").Should().BeGreaterOrEqualTo(4.5);
        _engine.CalculateContrastRatio("#777777", "#FFFFFF").Should().BeLessThan(4.5);
    }

    [Fact]
    public void AllSixBuiltInPresets_PassAllWcagContrastThresholdsWithZeroWarnings()
    {
        var presetManager = new PresetManager(new NoOpPresetCatalogRepository(), _engine);

        presetManager.BuiltInPresets.Should().HaveCount(6);
        foreach (var preset in presetManager.BuiltInPresets)
        {
            var collapsedReport = _engine.ValidatePaletteContrast(preset.CollapsedTokens, IslandStateTarget.Collapsed);
            collapsedReport.HasWarnings.Should().BeFalse($"preset '{preset.Name}' Collapsed tokens must satisfy WCAG thresholds");
            collapsedReport.Issues.Should().BeEmpty();

            var expandedReport = _engine.ValidatePaletteContrast(preset.ExpandedTokens, IslandStateTarget.Expanded);
            expandedReport.HasWarnings.Should().BeFalse($"preset '{preset.Name}' Expanded tokens must satisfy WCAG thresholds");
            expandedReport.Issues.Should().BeEmpty();
        }
    }

    [Fact]
    public void ValidateConfigurationContrast_ReportsLowContrastTextIconAndAccentPerSurface()
    {
        var config = AppearanceConfiguration.Default with
        {
            LinkCollapsedAndExpanded = false,
            CollapsedTokens = new IslandColorPalette
            {
                BackgroundHex = "#0F172A",
                PrimaryTextHex = "#F8FAFC",
                SecondaryIconHex = "#94A3B8",
                AccentHex = "#1E293B"
            },
            ExpandedTokens = new IslandColorPalette
            {
                BackgroundHex = "#FFFFFF",
                PrimaryTextHex = "#94A3B8",
                SecondaryIconHex = "#D4D4D8",
                AccentHex = "#0F172A"
            }
        };

        var report = _engine.ValidateConfigurationContrast(config);

        report.HasWarnings.Should().BeTrue();
        report.Issues.Should().Contain(i =>
            i.Slot == AppearanceTokenSlot.Accent
            && i.TargetState == IslandStateTarget.Collapsed
            && i.Role == ContrastRole.Accent
            && i.RequiredMinimum == 3.0);

        report.Issues.Should().Contain(i =>
            i.Slot == AppearanceTokenSlot.PrimaryText
            && i.TargetState == IslandStateTarget.Expanded
            && i.Role == ContrastRole.PrimaryText
            && i.RequiredMinimum == 4.5);

        report.Issues.Should().Contain(i =>
            i.Slot == AppearanceTokenSlot.SecondaryIcon
            && i.TargetState == IslandStateTarget.Expanded
            && i.Role == ContrastRole.SecondaryIcon
            && i.RequiredMinimum == 3.0);
    }

    [Theory]
    [InlineData("#1E3A8A", "#000000", 4.5)]
    [InlineData("#93C5FD", "#F8FAFC", 4.5)]
    [InlineData("#262626", "#071324", 3.0)]
    [InlineData("#0000FF", "#333333", 4.5)]
    [InlineData("#CBD5E1", "#E2E8F0", 4.5)]
    public void SolveRecommendedForeground_PreservesBackground_AndReachesTargetRatio(
        string initialForegroundHex,
        string backgroundHex,
        double targetRatio)
    {
        var beforeColor = _engine.FromHex(initialForegroundHex);
        var solvedHex = _engine.SolveRecommendedForeground(initialForegroundHex, backgroundHex, targetRatio);
        var solvedColor = _engine.FromHex(solvedHex);
        var achievedRatio = _engine.CalculateContrastRatio(solvedHex, backgroundHex);

        achievedRatio.Should().BeGreaterOrEqualTo(targetRatio - 0.01);

        // When solved color retains chromatic saturation, verify hue stays within 8-bit quantization tolerance.
        if (beforeColor.Saturation > 5.0 && solvedColor.Saturation > 5.0)
        {
            var hueDiff = Math.Abs(beforeColor.Hue - solvedColor.Hue);
            hueDiff = Math.Min(hueDiff, 360.0 - hueDiff);
            var tolerance = beforeColor.Saturation < 20.0 ? 8.0 : 2.0;
            hueDiff.Should().BeLessThanOrEqualTo(tolerance);
        }
    }

    private sealed class NoOpPresetCatalogRepository : IPresetCatalogRepository
    {
        public Task<PresetCatalog> LoadAsync(CancellationToken ct = default) => Task.FromResult(PresetCatalog.Empty);

        public Task SaveAsync(PresetCatalog catalog, CancellationToken ct = default) => Task.CompletedTask;
    }
}
