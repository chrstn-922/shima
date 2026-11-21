using System.Windows;
using System.Windows.Media;
using Shima.Core.Contracts;

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
/// and convex rounded bottom corners.
/// </summary>
public static class ConcaveNotchGeometryBuilder
{
    public static readonly IslandVisualSpec CollapsedSpec = new(136, 28, 12, 14);
    public static readonly IslandVisualSpec ActiveSpec = new(224, 32, 12, 16);
    public static readonly IslandVisualSpec NotificationSpec = new(340, 76, 14, 20);
    public static readonly IslandVisualSpec ExpandedSpec = new(412, 168, 16, 24);

    public static IslandVisualSpec GetSpecForState(IslandShellState state) => state switch
    {
        IslandShellState.Active => ActiveSpec,
        IslandShellState.Notification => NotificationSpec,
        IslandShellState.Expanded => ExpandedSpec,
        _ => CollapsedSpec
    };

    public static StreamGeometry Build(IslandVisualSpec spec) =>
        Build(spec.WidthDip, spec.HeightDip, spec.EarRadiusDip, spec.BottomRadiusDip);

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
