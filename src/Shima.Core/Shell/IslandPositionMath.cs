using Shima.Core.Contracts;

namespace Shima.Core.Shell;

public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public readonly record struct IslandWorkAreaPlacement(
    DipRect VisualRectDip,
    DipRect WindowRectDip,
    MonitorBounds PixelRect,
    double ClampedOffsetXDip,
    double ClampedTopOffsetDip);

/// <summary>
/// Pure multi-monitor, DPI-aware geometry, clamping, magnetic snap, and transition glide math
/// for the Dynamic Island shell across Docked Notch and Floating Pill modes.
/// </summary>
public static class IslandPositionMath
{
    public const double DefaultFloatingTopMarginDip = 12.0;
    public const double MagneticCenterSnapThresholdDip = 16.0;
    public const double CompanionGapDip = 5.0;
    public const double CompanionPillHeightDip = 16.0;
    public const double DragThresholdDip = 5.0;

    public static MonitorBounds GetEffectiveWorkArea(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return !monitor.WorkArea.IsEmpty ? monitor.WorkArea : monitor.Bounds;
    }

    public static double GetEffectiveDpiScale(double dpiScale) =>
        dpiScale > 0 && double.IsFinite(dpiScale) ? dpiScale : 1.0;

    public static double Lerp(double from, double to, double progress) =>
        from + (to - from) * progress;

    public static (double InsetLeftDip, double VisualWidthDip, double VisualHeightDip) GetVisualDimensions(
        double fullSpecWidthDip,
        double fullSpecHeightDip,
        double earRadiusDip,
        double floatingProgress)
    {
        var clampedProgress = Math.Clamp(double.IsFinite(floatingProgress) ? floatingProgress : 0.0, 0.0, 1.0);
        var safeWidth = Math.Max(4.0, fullSpecWidthDip);
        var safeHeight = Math.Max(4.0, fullSpecHeightDip);
        var safeEar = Math.Clamp(earRadiusDip, 0.0, safeWidth / 4.0);

        var insetLeft = safeEar * clampedProgress;
        var visualWidth = Math.Max(4.0, safeWidth - 2.0 * insetLeft);
        return (insetLeft, visualWidth, safeHeight);
    }

    public static double ResolveEffectiveTopOffsetDip(
        IslandStyle style,
        double? customFloatingTopOffsetDip)
    {
        if (style != IslandStyle.FloatingPill)
        {
            return 0.0;
        }

        if (customFloatingTopOffsetDip is double top && double.IsFinite(top))
        {
            return Math.Max(0.0, top);
        }

        return DefaultFloatingTopMarginDip;
    }

    public static DipRect ClampVisualRectDip(
        MonitorBounds workAreaPx,
        double dpiScale,
        double visualWidthDip,
        double visualHeightDip,
        double desiredOffsetXDip,
        double desiredTopOffsetDip,
        double extraBottomClearanceDip = 0.0)
    {
        var scale = GetEffectiveDpiScale(dpiScale);
        var safeW = Math.Max(1.0, double.IsFinite(visualWidthDip) ? visualWidthDip : 1.0);
        var safeH = Math.Max(1.0, double.IsFinite(visualHeightDip) ? visualHeightDip : 1.0);
        var safeExtraBottom = Math.Max(0.0, double.IsFinite(extraBottomClearanceDip) ? extraBottomClearanceDip : 0.0);

        var workLeftDip = workAreaPx.Left / scale;
        var workTopDip = workAreaPx.Top / scale;
        var workWidthDip = Math.Max(safeW, workAreaPx.Width / scale);
        var workHeightDip = Math.Max(safeH + safeExtraBottom, workAreaPx.Height / scale);

        var workCenterX = workLeftDip + (workAreaPx.Width / scale) / 2.0;
        var safeOffsetX = double.IsFinite(desiredOffsetXDip) ? desiredOffsetXDip : 0.0;
        var rawLeft = workCenterX + safeOffsetX - safeW / 2.0;
        var maxLeft = Math.Max(workLeftDip, workLeftDip + workWidthDip - safeW);
        var clampedLeft = Math.Clamp(rawLeft, workLeftDip, maxLeft);

        var safeTopOffset = double.IsFinite(desiredTopOffsetDip) ? Math.Max(0.0, desiredTopOffsetDip) : 0.0;
        var rawTop = workTopDip + safeTopOffset;
        var maxTop = Math.Max(workTopDip, workTopDip + workHeightDip - safeH - safeExtraBottom);
        var clampedTop = Math.Clamp(rawTop, workTopDip, maxTop);

        return new DipRect(clampedLeft, clampedTop, safeW, safeH);
    }

    public static IslandWorkAreaPlacement CalculatePlacement(
        MonitorBounds workAreaPx,
        double dpiScale,
        double fullSpecWidthDip,
        double fullSpecHeightDip,
        double earRadiusDip,
        double floatingProgress,
        double desiredOffsetXDip,
        double desiredTopOffsetDip,
        double padLeftDip = 0.0,
        double padTopDip = 0.0,
        double padRightDip = 0.0,
        double padBottomDip = 0.0,
        double extraBottomClearanceDip = 0.0)
    {
        var scale = GetEffectiveDpiScale(dpiScale);
        var (insetLeft, visualW, visualH) = GetVisualDimensions(
            fullSpecWidthDip,
            fullSpecHeightDip,
            earRadiusDip,
            floatingProgress);

        var visualRect = ClampVisualRectDip(
            workAreaPx,
            scale,
            visualW,
            visualH,
            desiredOffsetXDip,
            desiredTopOffsetDip,
            extraBottomClearanceDip);

        var workLeftDip = workAreaPx.Left / scale;
        var workTopDip = workAreaPx.Top / scale;
        var workCenterX = workLeftDip + (workAreaPx.Width / scale) / 2.0;

        var clampedOffsetX = (visualRect.Left + visualW / 2.0) - workCenterX;
        var clampedTopOffset = visualRect.Top - workTopDip;

        // Place the surface container so its visible left edge starts at visualRect.Left
        // even while ear insets shrink horizontally from fullSpecWidthDip during morph.
        var surfaceOriginX = visualRect.Left - insetLeft;
        var windowLeft = surfaceOriginX - padLeftDip;
        var windowTop = visualRect.Top - padTopDip;
        var windowWidth = Math.Max(1.0, fullSpecWidthDip + padLeftDip + padRightDip);
        var windowHeight = Math.Max(1.0, fullSpecHeightDip + padTopDip + padBottomDip);
        var windowRect = new DipRect(windowLeft, windowTop, windowWidth, windowHeight);

        var pixelWidth = Math.Max(1, (int)Math.Round(windowWidth * scale, MidpointRounding.AwayFromZero));
        var pixelHeight = Math.Max(1, (int)Math.Round(windowHeight * scale, MidpointRounding.AwayFromZero));
        var pixelLeft = (int)Math.Round(windowLeft * scale, MidpointRounding.AwayFromZero);
        var pixelTop = (int)Math.Round(windowTop * scale, MidpointRounding.AwayFromZero);

        // Guarantee physical pixel edges stay strictly inside work area when no shadow overhang padding applies.
        if (!workAreaPx.IsEmpty)
        {
            var padLeftPx = (int)Math.Round((padLeftDip + insetLeft) * scale, MidpointRounding.AwayFromZero);
            var padRightPx = (int)Math.Round((padRightDip + insetLeft) * scale, MidpointRounding.AwayFromZero);
            var padTopPx = (int)Math.Round(padTopDip * scale, MidpointRounding.AwayFromZero);
            var extraBottomPx = (int)Math.Round(extraBottomClearanceDip * scale, MidpointRounding.AwayFromZero);
            var visualWidthPx = Math.Max(1, pixelWidth - padLeftPx - padRightPx);
            var visualHeightPx = Math.Max(1, (int)Math.Round(fullSpecHeightDip * scale, MidpointRounding.AwayFromZero));

            var visualLeftPx = pixelLeft + padLeftPx;
            var maxVisualLeftPx = Math.Max(workAreaPx.Left, workAreaPx.Right - visualWidthPx);
            visualLeftPx = Math.Clamp(visualLeftPx, workAreaPx.Left, maxVisualLeftPx);

            var visualTopPx = pixelTop + padTopPx;
            var maxVisualTopPx = Math.Max(workAreaPx.Top, workAreaPx.Bottom - visualHeightPx - extraBottomPx);
            visualTopPx = Math.Clamp(visualTopPx, workAreaPx.Top, maxVisualTopPx);

            pixelLeft = visualLeftPx - padLeftPx;
            pixelTop = visualTopPx - padTopPx;
        }

        return new IslandWorkAreaPlacement(
            visualRect,
            windowRect,
            new MonitorBounds(pixelLeft, pixelTop, pixelWidth, pixelHeight),
            clampedOffsetX,
            clampedTopOffset);
    }

    public static (double OffsetXDip, double? FloatingTopOffsetDip, bool SnappedToCenter) ResolveDropTarget(
        MonitorBounds workAreaPx,
        double dpiScale,
        double fullSpecWidthDip,
        double fullSpecHeightDip,
        double earRadiusDip,
        IslandStyle style,
        double candidateOffsetXDip,
        double candidateTopOffsetDip,
        double extraBottomClearanceDip = 0.0)
    {
        var floatingProgress = style == IslandStyle.FloatingPill ? 1.0 : 0.0;
        var (_, visualW, visualH) = GetVisualDimensions(fullSpecWidthDip, fullSpecHeightDip, earRadiusDip, floatingProgress);
        var clamped = ClampVisualRectDip(
            workAreaPx,
            dpiScale,
            visualW,
            visualH,
            candidateOffsetXDip,
            style == IslandStyle.FloatingPill ? candidateTopOffsetDip : 0.0,
            extraBottomClearanceDip);

        var scale = GetEffectiveDpiScale(dpiScale);
        var workLeftDip = workAreaPx.Left / scale;
        var workTopDip = workAreaPx.Top / scale;
        var workCenterX = workLeftDip + (workAreaPx.Width / scale) / 2.0;

        var offsetX = (clamped.Left + visualW / 2.0) - workCenterX;
        var snappedHorizontal = false;
        if (Math.Abs(offsetX) <= MagneticCenterSnapThresholdDip)
        {
            offsetX = 0.0;
            snappedHorizontal = true;
        }

        if (style == IslandStyle.DockedNotch)
        {
            return (offsetX, null, snappedHorizontal);
        }

        var topOffset = clamped.Top - workTopDip;
        if (snappedHorizontal && Math.Abs(topOffset - DefaultFloatingTopMarginDip) <= MagneticCenterSnapThresholdDip)
        {
            return (0.0, null, true);
        }

        return (offsetX, topOffset, snappedHorizontal);
    }

    public static DipRect InterpolateSurfaceTransitionWindowRect(
        DipRect startVisualRect,
        DipRect targetVisualRect,
        double targetFullSpecWidthDip,
        double targetFullSpecHeightDip,
        double targetEarInsetDip,
        double padLeftDip,
        double padTopDip,
        double windowWidthDip,
        double windowHeightDip,
        double transformOriginX,
        double transformOriginY,
        double easedProgress)
    {
        var p = Math.Clamp(easedProgress, 0.0, 1.0);
        var fromScaleX = startVisualRect.Width / Math.Max(1.0, targetVisualRect.Width);
        var fromScaleY = startVisualRect.Height / Math.Max(1.0, targetVisualRect.Height);
        var scaleX = Lerp(fromScaleX, 1.0, p);
        var scaleY = Lerp(fromScaleY, 1.0, p);

        var desiredVisualLeft = Lerp(startVisualRect.Left, targetVisualRect.Left, p);
        var desiredVisualTop = Lerp(startVisualRect.Top, targetVisualRect.Top, p);

        // MorphScaleTransform runs on MainIslandSurfaceHost (size = targetFullSpecWidth x targetFullSpecHeight).
        // Under ScaleTransform(scaleX, scaleY) with origin (ox, oy):
        //   point (targetEarInsetDip, 0) in MainIslandSurfaceHost maps to:
        //   X = ox * W + (targetEarInsetDip - ox * W) * scaleX
        //   Y = oy * H + (0 - oy * H) * scaleY = oy * H * (1 - scaleY)
        var ox = transformOriginX * targetFullSpecWidthDip;
        var oy = transformOriginY * targetFullSpecHeightDip;
        var scaledVisualOffsetX = ox + (targetEarInsetDip - ox) * scaleX;
        var scaledVisualOffsetY = oy * (1.0 - scaleY);

        var surfaceOriginX = desiredVisualLeft - scaledVisualOffsetX;
        var surfaceOriginY = desiredVisualTop - scaledVisualOffsetY;

        return new DipRect(
            surfaceOriginX - padLeftDip,
            surfaceOriginY - padTopDip,
            windowWidthDip,
            windowHeightDip);
    }

    public static MonitorBounds DipRectToPixelRect(DipRect dipRect, double dpiScale)
    {
        var scale = GetEffectiveDpiScale(dpiScale);
        return new MonitorBounds(
            (int)Math.Round(dipRect.Left * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(dipRect.Top * scale, MidpointRounding.AwayFromZero),
            Math.Max(1, (int)Math.Round(dipRect.Width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(dipRect.Height * scale, MidpointRounding.AwayFromZero)));
    }
}
