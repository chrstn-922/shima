using FluentAssertions;
using Shima.Core.Color;
using Xunit;

namespace Shima.Core.Tests;

public sealed class ColorProcessingServiceTests
{
    private readonly CoreColorProcessingEngine _sut = new();

    [Theory]
    [InlineData(255, 0, 0, 0.0, 100.0, 100.0, "#FF0000")]
    [InlineData(0, 255, 0, 120.0, 100.0, 100.0, "#00FF00")]
    [InlineData(0, 0, 255, 240.0, 100.0, 100.0, "#0000FF")]
    [InlineData(255, 255, 0, 60.0, 100.0, 100.0, "#FFFF00")]
    [InlineData(0, 255, 255, 180.0, 100.0, 100.0, "#00FFFF")]
    [InlineData(255, 0, 255, 300.0, 100.0, 100.0, "#FF00FF")]
    [InlineData(59, 130, 246, 217.219, 76.016, 96.471, "#3B82F6")]
    public void RgbHsbHex_RoundTripsWithinRoundingPrecision(
        byte r,
        byte g,
        byte b,
        double expectedHue,
        double expectedSaturation,
        double expectedBrightness,
        string expectedHex)
    {
        var fromRgb = _sut.FromRgb(r, g, b);
        fromRgb.Hex.Should().Be(expectedHex);
        fromRgb.Hue.Should().BeApproximately(expectedHue, 0.05);
        fromRgb.Saturation.Should().BeApproximately(expectedSaturation, 0.05);
        fromRgb.Brightness.Should().BeApproximately(expectedBrightness, 0.05);

        var fromHsb = _sut.FromHsb(fromRgb.Hue, fromRgb.Saturation, fromRgb.Brightness);
        fromHsb.R.Should().Be(r);
        fromHsb.G.Should().Be(g);
        fromHsb.B.Should().Be(b);
        fromHsb.Hex.Should().Be(expectedHex);

        var fromHex = _sut.FromHex(expectedHex);
        fromHex.R.Should().Be(r);
        fromHex.G.Should().Be(g);
        fromHex.B.Should().Be(b);
    }

    [Theory]
    [InlineData("#f80", "#FF8800")]
    [InlineData("0f8", "#00FF88")]
    [InlineData("  #3b82f6 ", "#3B82F6")]
    [InlineData("abcdef", "#ABCDEF")]
    public void TryParseHex_ExpandsShorthandAndNormalizesUppercase(string input, string expectedHex)
    {
        var ok = _sut.TryParseHex(input, out var color);

        ok.Should().BeTrue();
        color.Should().NotBeNull();
        color!.Hex.Should().Be(expectedHex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]
    [InlineData("#1234")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("not-a-color")]
    public void TryParseHex_RejectsMalformedInputs(string? input)
    {
        var ok = _sut.TryParseHex(input, out var color);

        ok.Should().BeFalse();
        color.Should().BeNull();
    }

    [Fact]
    public void FromHsb_UsesMidpointRoundingAwayFromZero_AndPreserves360HueBoundary()
    {
        // Pure red at exact 360.0 boundary preserves 360.0 while yielding #FF0000.
        var red360 = _sut.FromHsb(360.0, 100.0, 100.0);
        red360.Hex.Should().Be("#FF0000");
        red360.Hue.Should().Be(360.0);

        // Verify half-step midpoint 0.5 / 255 -> rounds away from zero:
        // H = 0, S = 100, B = (127.5 / 255) * 100 = 50.0 -> 0.5 * 255 = 127.5 -> AwayFromZero => 128 (#800000)
        var mid = _sut.FromHsb(0.0, 100.0, 50.0);
        mid.R.Should().Be(128);
        mid.G.Should().Be(0);
        mid.B.Should().Be(0);
        mid.Hex.Should().Be("#800000");
    }

    [Fact]
    public void GrayscaleAndBlack_PreserveHueSingularity()
    {
        var whiteWithHue = _sut.FromHsb(240.0, 0.0, 100.0);
        whiteWithHue.Hex.Should().Be("#FFFFFF");
        whiteWithHue.Hue.Should().Be(240.0);

        var blackWithHue = _sut.FromHsb(135.0, 82.0, 0.0);
        blackWithHue.Hex.Should().Be("#000000");
        blackWithHue.Hue.Should().Be(135.0);

        _sut.TryParseHex("#808080", out var gray, preservedHue: 275.5).Should().BeTrue();
        gray!.Hue.Should().Be(275.5);
    }

    [Fact]
    public void FromRgbClamped_And_FromHsb_ClampOutOfBoundsChannels()
    {
        var clampedRgb = _sut.FromRgbClamped(-25, 300, 128);
        clampedRgb.R.Should().Be(0);
        clampedRgb.G.Should().Be(255);
        clampedRgb.B.Should().Be(128);
        clampedRgb.Hex.Should().Be("#00FF80");

        var clampedHsb = _sut.FromHsb(195.0, 150.0, -10.0);
        clampedHsb.Saturation.Should().Be(100.0);
        clampedHsb.Brightness.Should().Be(0.0);
        clampedHsb.Hex.Should().Be("#000000");
    }
}
