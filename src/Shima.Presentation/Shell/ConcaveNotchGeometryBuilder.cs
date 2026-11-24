using System.Windows;
using System.Windows.Media;
using Shima.Core.Contracts;
using Shima.Core.Shell;

namespace Shima.Presentation.Shell;

public readonly record struct IslandVisualSpec(
    double WidthDip,
    double HeightDip,
    double EarRadiusDip,
    double BottomRadiusDip)
{
    public DipSize ToDipSize() => new(WidthDip, HeightDip);
}

/// <summary>
/// Builds continuous C1-smooth top-bezel Dynamic Island contours with concave top ears
/// and convex rounded bottom corners, plus continuous morph contours toward a 4-corner
/// rounded floating capsule pill.
/// </summary>
public static class ConcaveNotchGeometryBuilder
{
    private const double CubicCircleKappa = 0.5522847498;

    public static readonly IslandVisualSpec CollapsedSpec = new(136, 28, 12, 14);
    public static readonly IslandVisualSpec ActiveSpec = new(264, 34, 12, 16);
    public static readonly IslandVisualSpec NotificationSpec = new(340, 76, 14, 20);
    public static readonly IslandVisualSpec ExpandedSpec = new(444, 176, 16, 24);

    public static IslandVisualSpec GetSpecForState(IslandShellState state) => state switch
    {
        IslandShellState.Active => ActiveSpec,
        IslandShellState.Notification => NotificationSpec,
        IslandShellState.Expanded => ExpandedSpec,
        _ => CollapsedSpec
    };

    public static StreamGeometry Build(IslandVisualSpec spec) =>
        Build(spec.WidthDip, spec.HeightDip, spec.EarRadiusDip, spec.BottomRadiusDip);

    public static StreamGeometry Build(IslandVisualSpec spec, double floatingProgress)
    {
        var t = Math.Clamp(double.IsFinite(floatingProgress) ? floatingProgress : 0.0, 0.0, 1.0);
        if (t <= 0.001)
        {
            return Build(spec);
        }

        var w = Math.Max(4.0, spec.WidthDip);
        var h = Math.Max(4.0, spec.HeightDip);
        var ear = Math.Clamp(spec.EarRadiusDip, 0.0, Math.Min(w / 4.0, h / 2.0));
        var bodyWidth = Math.Max(4.0, w - 2.0 * ear);

        var dockedBottom = Math.Clamp(spec.BottomRadiusDip, 0.0, Math.Min(bodyWidth / 2.0, Math.Max(0.0, h - ear)));
        var floatingCorner = Math.Min(
            bodyWidth / 2.0,
            h < 48.0 ? h / 2.0 : Math.Max(spec.BottomRadiusDip, h / 6.0));
        var bottom = IslandPositionMath.Lerp(dockedBottom, floatingCorner, t);

        var leftWall = ear;
        var rightWall = w - ear;
        var strokeTop = t > 0.02;

        // Top-right contour endpoints & handles:
        // Docked (t=0): concave quarter circle from (w, 0) -> (rightWall, ear)
        // Floating (t=1): convex quarter circle from (rightWall - floatingCorner, 0) -> (rightWall, floatingCorner)
        var trDockedStartX = w;
        var trDockedC1X = w - CubicCircleKappa * ear;
        var trDockedC2Y = (1.0 - CubicCircleKappa) * ear;
        var trDockedEndY = ear;

        var trFloatStartX = rightWall - floatingCorner;
        var trFloatC1X = rightWall - (1.0 - CubicCircleKappa) * floatingCorner;
        var trFloatC2Y = (1.0 - CubicCircleKappa) * floatingCorner;
        var trFloatEndY = floatingCorner;

        var trStartX = IslandPositionMath.Lerp(trDockedStartX, trFloatStartX, t);
        var trC1X = IslandPositionMath.Lerp(trDockedC1X, trFloatC1X, t);
        var trC2Y = IslandPositionMath.Lerp(trDockedC2Y, trFloatC2Y, t);
        var trEndY = Math.Min(h - bottom, IslandPositionMath.Lerp(trDockedEndY, trFloatEndY, t));

        var tlEndX = w - trStartX;
        var tlC2X = w - trC1X;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(tlEndX, 0), isFilled: true, isClosed: true);

            // Top horizontal span
            ctx.LineTo(new Point(trStartX, 0), isStroked: strokeTop, isSmoothJoin: true);

            // Top-right continuous concave-to-convex Bezier corner
            ctx.BezierTo(
                new Point(trC1X, 0),
                new Point(rightWall, trC2Y),
                new Point(rightWall, trEndY),
                isStroked: true,
                isSmoothJoin: true);

            // Right vertical body wall down to bottom-right corner
            ctx.LineTo(new Point(rightWall, Math.Max(trEndY, h - bottom)), isStroked: true, isSmoothJoin: true);

            // Bottom-right convex corner
            if (bottom > 0)
            {
                ctx.ArcTo(
                    new Point(rightWall - bottom, h),
                    new Size(bottom, bottom),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Bottom horizontal span
            ctx.LineTo(new Point(leftWall + bottom, h), isStroked: true, isSmoothJoin: true);

            // Bottom-left convex corner
            if (bottom > 0)
            {
                ctx.ArcTo(
                    new Point(leftWall, Math.Max(trEndY, h - bottom)),
                    new Size(bottom, bottom),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Left vertical body wall up to top-left Bezier corner start
            ctx.LineTo(new Point(leftWall, trEndY), isStroked: true, isSmoothJoin: true);

            // Top-left continuous concave-to-convex Bezier corner back to (tlEndX, 0)
            ctx.BezierTo(
                new Point(leftWall, trC2Y),
                new Point(tlC2X, 0),
                new Point(tlEndX, 0),
                isStroked: true,
                isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    public static StreamGeometry Build(
        double totalWidth,
        double height,
        double earRadius,
        double bottomRadius)
    {
        var w = Math.Max(4.0, totalWidth);
        var h = Math.Max(4.0, height);
        var ear = Math.Clamp(earRadius, 0.0, Math.Min(w / 4.0, h / 2.0));
        var bodyWidth = Math.Max(0.0, w - 2.0 * ear);
        var bottom = Math.Clamp(bottomRadius, 0.0, Math.Min(bodyWidth / 2.0, Math.Max(0.0, h - ear)));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);

            // Top flush edge along the monitor bezel (Y = 0)
            ctx.LineTo(new Point(w, 0), isStroked: false, isSmoothJoin: true);

            // Top-right concave ear curving downward into the right pill wall
            if (ear > 0)
            {
                ctx.ArcTo(
                    new Point(w - ear, ear),
                    new Size(ear, ear),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Counterclockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Right vertical body wall down to the bottom-right corner
            ctx.LineTo(new Point(w - ear, h - bottom), isStroked: true, isSmoothJoin: true);

            // Bottom-right convex corner
            if (bottom > 0)
            {
                ctx.ArcTo(
                    new Point(w - ear - bottom, h),
                    new Size(bottom, bottom),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Bottom horizontal edge
            ctx.LineTo(new Point(ear + bottom, h), isStroked: true, isSmoothJoin: true);

            // Bottom-left convex corner
            if (bottom > 0)
            {
                ctx.ArcTo(
                    new Point(ear, h - bottom),
                    new Size(bottom, bottom),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            // Left vertical body wall up to the top-left concave ear
            ctx.LineTo(new Point(ear, ear), isStroked: true, isSmoothJoin: true);

            // Top-left concave ear flaring smoothly back into Y = 0
            if (ear > 0)
            {
                ctx.ArcTo(
                    new Point(0, 0),
                    new Size(ear, ear),
                    rotationAngle: 0,
                    isLargeArc: false,
                    SweepDirection.Counterclockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
