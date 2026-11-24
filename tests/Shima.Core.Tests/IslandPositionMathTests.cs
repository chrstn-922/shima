using FluentAssertions;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Xunit;

namespace Shima.Core.Tests;

public sealed class IslandPositionMathTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void CalculatePlacement_ClampsCompactAndExpandedInsideMultiMonitorWorkAreasAcrossDpi(double dpiScale)
    {
        var workAreas = new[]
        {
            new MonitorBounds(0, 0, 1920, 1032),           // Standard primary monitor with bottom taskbar
            new MonitorBounds(1920, 48, 2560, 1392),       // Secondary monitor with top taskbar
            new MonitorBounds(-2560, 120, 2560, 1392)      // Left negative-coordinate monitor
        };

        foreach (var workArea in workAreas)
        {
            // Floating compact pill dragged past right/bottom edges
            var compact = IslandPositionMath.CalculatePlacement(
                workArea,
                dpiScale,
                fullSpecWidthDip: 136,
                fullSpecHeightDip: 28,
                earRadiusDip: 12,
                floatingProgress: 1.0,
                desiredOffsetXDip: 5000,
                desiredTopOffsetDip: 5000,
                padLeftDip: 14,
                padTopDip: 10,
                padRightDip: 14,
                padBottomDip: 16);

            compact.VisualRectDip.Width.Should().BeApproximately(112.0, 0.01);
            (compact.VisualRectDip.Left * dpiScale).Should().BeGreaterOrEqualTo(workArea.Left - 0.5);
            (compact.VisualRectDip.Right * dpiScale).Should().BeLessOrEqualTo(workArea.Right + 0.5);
            (compact.VisualRectDip.Top * dpiScale).Should().BeGreaterOrEqualTo(workArea.Top - 0.5);
            (compact.VisualRectDip.Bottom * dpiScale).Should().BeLessOrEqualTo(workArea.Bottom + 0.5);

            // Pixel rect visual region inside workArea strictly
            (compact.PixelRect.Left + (int)Math.Round((14 + 12) * dpiScale)).Should().BeGreaterOrEqualTo(workArea.Left);
            (compact.PixelRect.Top + (int)Math.Round(10 * dpiScale)).Should().BeGreaterOrEqualTo(workArea.Top);

            // Expanding from parked far bottom-right clamps expanded panel safely inside work area
            var expanded = IslandPositionMath.CalculatePlacement(
                workArea,
                dpiScale,
                fullSpecWidthDip: 444,
                fullSpecHeightDip: 176,
                earRadiusDip: 16,
                floatingProgress: 1.0,
                desiredOffsetXDip: compact.ClampedOffsetXDip,
                desiredTopOffsetDip: compact.ClampedTopOffsetDip);

            var visualInsetPx = (int)Math.Round(16 * dpiScale, MidpointRounding.AwayFromZero);
            expanded.VisualRectDip.Left.Should().BeGreaterOrEqualTo(workArea.Left / dpiScale - 0.01);
            expanded.VisualRectDip.Right.Should().BeLessOrEqualTo(workArea.Right / dpiScale + 0.01);
            expanded.VisualRectDip.Top.Should().BeGreaterOrEqualTo(workArea.Top / dpiScale - 0.01);
            expanded.VisualRectDip.Bottom.Should().BeLessOrEqualTo(workArea.Bottom / dpiScale + 0.01);

            (expanded.PixelRect.Left + visualInsetPx).Should().BeGreaterOrEqualTo(workArea.Left);
            (expanded.PixelRect.Right - visualInsetPx).Should().BeLessOrEqualTo(workArea.Right);
            expanded.PixelRect.Top.Should().BeGreaterOrEqualTo(workArea.Top);
            expanded.PixelRect.Bottom.Should().BeLessOrEqualTo(workArea.Bottom);
        }
    }

    [Fact]
    public void ResolveDropTarget_EnforcesDockedHorizontalOnly_And16DipMagneticCenterSnap()
    {
        var workArea = new MonitorBounds(0, 0, 1920, 1032);

        // Docked Notch: drops within 16 DIP snap magnetically to 0, and Y is forced flush (null)
        var (dockedSnapX, dockedSnapTop, dockedDidSnap) = IslandPositionMath.ResolveDropTarget(
            workArea,
            dpiScale: 1.0,
            fullSpecWidthDip: 136,
            fullSpecHeightDip: 28,
            earRadiusDip: 12,
            IslandStyle.DockedNotch,
            candidateOffsetXDip: 15.5,
            candidateTopOffsetDip: 250);
        dockedSnapX.Should().Be(0.0);
        dockedSnapTop.Should().BeNull();
        dockedDidSnap.Should().BeTrue();

        // Docked Notch outside 16 DIP preserves X offset but keeps Y top-flush
        var (dockedCustomX, dockedCustomTop, dockedDidNotSnap) = IslandPositionMath.ResolveDropTarget(
            workArea,
            dpiScale: 1.0,
            fullSpecWidthDip: 136,
            fullSpecHeightDip: 28,
            earRadiusDip: 12,
            IslandStyle.DockedNotch,
            candidateOffsetXDip: 120.0,
            candidateTopOffsetDip: 250);
        dockedCustomX.Should().BeApproximately(120.0, 0.01);
        dockedCustomTop.Should().BeNull();
        dockedDidNotSnap.Should().BeFalse();

        // Floating Pill near top-center (within 16 DIP X & 16 DIP of default top margin 12) snaps to (0, null -> default 12)
        var (floatSnapX, floatSnapTop, floatDidSnap) = IslandPositionMath.ResolveDropTarget(
            workArea,
            dpiScale: 1.0,
            fullSpecWidthDip: 136,
            fullSpecHeightDip: 28,
            earRadiusDip: 12,
            IslandStyle.FloatingPill,
            candidateOffsetXDip: -14.0,
            candidateTopOffsetDip: 22.0);
        floatSnapX.Should().Be(0.0);
        floatSnapTop.Should().BeNull();
        floatDidSnap.Should().BeTrue();

        // Floating Pill far below top edge near horizontal center aligns X=0 while preserving Y
        var (floatMidX, floatMidTop, _) = IslandPositionMath.ResolveDropTarget(
            workArea,
            dpiScale: 1.0,
            fullSpecWidthDip: 136,
            fullSpecHeightDip: 28,
            earRadiusDip: 12,
            IslandStyle.FloatingPill,
            candidateOffsetXDip: 10.0,
            candidateTopOffsetDip: 400.0);
        floatMidX.Should().Be(0.0);
        floatMidTop.Should().BeApproximately(400.0, 0.01);
    }

    [Fact]
    public void InterpolateSurfaceTransitionWindowRect_ZeroTeleportAtStartAndExactTargetAtEnd()
    {
        var startVisual = new DipRect(1800, 1000, 112, 28);
        var targetVisual = new DipRect(1508, 856, 412, 176);

        var startWindow = IslandPositionMath.InterpolateSurfaceTransitionWindowRect(
            startVisual,
            targetVisual,
            targetFullSpecWidthDip: 444,
            targetFullSpecHeightDip: 176,
            targetEarInsetDip: 16,
            padLeftDip: 14,
            padTopDip: 10,
            windowWidthDip: 472,
            windowHeightDip: 202,
            transformOriginX: 0.5,
            transformOriginY: 0.0,
            easedProgress: 0.0);

        // At p=0, under ScaleX = 112/412 centered at targetWidth*0.5, the scaled visual left lands at startVisual.Left (no teleport)
        var ox = 0.5 * 444.0;
        var scaleX = startVisual.Width / targetVisual.Width;
        var renderedVisualLeft = startWindow.Left + 14.0 + ox + (16.0 - ox) * scaleX;
        var renderedVisualTop = startWindow.Top + 10.0;
        renderedVisualLeft.Should().BeApproximately(startVisual.Left, 0.01);
        renderedVisualTop.Should().BeApproximately(startVisual.Top, 0.01);

        var endWindow = IslandPositionMath.InterpolateSurfaceTransitionWindowRect(
            startVisual,
            targetVisual,
            targetFullSpecWidthDip: 444,
            targetFullSpecHeightDip: 176,
            targetEarInsetDip: 16,
            padLeftDip: 14,
            padTopDip: 10,
            windowWidthDip: 472,
            windowHeightDip: 202,
            transformOriginX: 0.5,
            transformOriginY: 0.0,
            easedProgress: 1.0);

        // At p=1, surface inset 16 aligns cleanly with targetVisual.Left
        (endWindow.Left + 14.0 + 16.0).Should().BeApproximately(targetVisual.Left, 0.01);
        (endWindow.Top + 10.0).Should().BeApproximately(targetVisual.Top, 0.01);
    }
}
