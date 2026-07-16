using DeskBox.Models;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

public enum WidgetCompactWidthTier
{
    Narrow,
    Standard,
    Wide
}

public static class WidgetCompactBoundsCalculator
{
    public const double MinWidth = 144;
    public const double MaxWidth = 480;
    public const double MinimalWidth = 172;
    public const double SummaryWidth = 248;
    public const double SmartWidth = 272;
    public const double SmartMediaWidth = 320;
    public const double Height = 42;
    public const double SmartDetailHeight = 52;
    public const double StandardWidthThreshold = 210;
    public const double WideWidthThreshold = 300;

    public static RectInt32 Calculate(
        RectInt32 expandedBounds,
        string? positionAnchor,
        double dpiScale,
        string? contentMode,
        WidgetKind widgetKind = WidgetKind.File,
        double? compactWidth = null)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        double logicalWidth = ResolveLogicalWidth(contentMode, widgetKind, compactWidth);
        int width = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        int height = Math.Max(1, (int)Math.Round(ResolveLogicalHeight(contentMode, widgetKind) * scale));
        bool anchorRight = positionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = positionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        int x = anchorRight ? expandedBounds.X + expandedBounds.Width - width : expandedBounds.X;
        int y = anchorBottom ? expandedBounds.Y + expandedBounds.Height - height : expandedBounds.Y;
        return new RectInt32(x, y, width, height);
    }

    public static RectInt32 Resolve(
        WidgetConfig config,
        RectInt32 expandedBounds,
        double dpiScale,
        string? contentMode)
    {
        if (config.CompactPlacement is not { } placement)
        {
            return Calculate(
                expandedBounds,
                config.PositionAnchor,
                dpiScale,
                contentMode,
                config.WidgetKind,
                config.CompactWidth);
        }

        var placementConfig = new WidgetConfig
        {
            X = placement.X,
            Y = placement.Y,
            Width = ResolveLogicalWidth(contentMode, config.WidgetKind, config.CompactWidth),
            Height = ResolveLogicalHeight(contentMode, config.WidgetKind),
            BoundsCoordinateVersion = placement.BoundsCoordinateVersion,
            PositionAnchor = placement.PositionAnchor,
            PositionMarginX = placement.PositionMarginX,
            PositionMarginY = placement.PositionMarginY,
            PositionMonitorKey = placement.PositionMonitorKey,
            PositionMonitorDeviceName = placement.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = placement.PositionMonitorWasPrimary
        };
        RectInt32 resolved = WidgetPositioningService.ResolveBoundsForCurrentTopology(placementConfig);
        RectInt32 workArea = DisplayArea.GetFromRect(resolved, DisplayAreaFallback.Nearest).WorkArea;
        double resolvedScale = WidgetPositioningService.GetDpiScale(workArea);
        return ApplyCompactSizeToResolvedPlacement(
            resolved,
            placement.PositionAnchor,
            resolvedScale,
            placementConfig.Width,
            placementConfig.Height);
    }

    public static RectInt32 ApplyCompactSizeToResolvedPlacement(
        RectInt32 resolvedBounds,
        string? positionAnchor,
        double dpiScale,
        double logicalWidth,
        double logicalHeight = Height)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        int width = Math.Max(1, (int)Math.Round(ClampLogicalWidth(logicalWidth) * scale));
        int height = Math.Max(1, (int)Math.Round(Math.Max(1, logicalHeight) * scale));
        bool anchorRight = positionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = positionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        int x = anchorRight ? resolvedBounds.X + resolvedBounds.Width - width : resolvedBounds.X;
        int y = anchorBottom ? resolvedBounds.Y + resolvedBounds.Height - height : resolvedBounds.Y;
        return new RectInt32(x, y, width, height);
    }

    public static RectInt32 ApplySizeToStablePlacement(
        RectInt32 stableBounds,
        int width,
        int height,
        string? positionAnchor)
    {
        int safeWidth = Math.Max(1, width);
        int safeHeight = Math.Max(1, height);
        bool anchorRight = positionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = positionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        return new RectInt32(
            anchorRight ? stableBounds.X + stableBounds.Width - safeWidth : stableBounds.X,
            anchorBottom ? stableBounds.Y + stableBounds.Height - safeHeight : stableBounds.Y,
            safeWidth,
            safeHeight);
    }

    public static RectInt32 AnchorExpandedBoundsToCompact(
        RectInt32 compactBounds,
        RectInt32 expandedBounds,
        string? positionAnchor,
        RectInt32 workArea)
    {
        int width = Math.Min(Math.Max(1, expandedBounds.Width), Math.Max(1, workArea.Width));
        int height = Math.Min(Math.Max(1, expandedBounds.Height), Math.Max(1, workArea.Height));
        bool anchorRight = positionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = positionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        int x = anchorRight
            ? compactBounds.X + compactBounds.Width - width
            : compactBounds.X;
        int y = anchorBottom
            ? compactBounds.Y + compactBounds.Height - height
            : compactBounds.Y;

        int maxX = workArea.X + workArea.Width - width;
        int maxY = workArea.Y + workArea.Height - height;
        return new RectInt32(
            Math.Clamp(x, workArea.X, maxX),
            Math.Clamp(y, workArea.Y, maxY),
            width,
            height);
    }

    public static double ResolveLogicalWidth(
        string? contentMode,
        WidgetKind widgetKind,
        double? compactWidth = null)
    {
        if (compactWidth is { } customWidth && double.IsFinite(customWidth))
        {
            return ClampLogicalWidth(customWidth);
        }

        if (string.Equals(
                contentMode,
                SettingsService.WidgetCompactContentModeMinimal,
                StringComparison.Ordinal))
        {
            return MinimalWidth;
        }

        if (string.Equals(
                contentMode,
                SettingsService.WidgetCompactContentModeSmart,
                StringComparison.Ordinal))
        {
            return widgetKind == WidgetKind.Music ? SmartMediaWidth : SmartWidth;
        }

        return SummaryWidth;
    }

    public static double ResolveLogicalHeight(string? contentMode, WidgetKind widgetKind)
    {
        bool usesSmartDetailLayout = string.Equals(
                contentMode,
                SettingsService.WidgetCompactContentModeSmart,
                StringComparison.Ordinal) &&
            widgetKind is WidgetKind.Music or WidgetKind.Weather or WidgetKind.QuickCapture;
        return usesSmartDetailLayout ? SmartDetailHeight : Height;
    }

    public static double ClampLogicalWidth(double width)
    {
        double finiteWidth = double.IsFinite(width) ? width : SummaryWidth;
        return Math.Clamp(finiteWidth, MinWidth, MaxWidth);
    }

    public static WidgetCompactWidthTier ResolveWidthTier(double logicalWidth)
    {
        double width = double.IsFinite(logicalWidth) ? logicalWidth : SummaryWidth;
        if (width < StandardWidthThreshold)
        {
            return WidgetCompactWidthTier.Narrow;
        }

        return width < WideWidthThreshold
            ? WidgetCompactWidthTier.Standard
            : WidgetCompactWidthTier.Wide;
    }

    public static double ResolveOuterCornerRadius(string? cornerPreference)
    {
        return cornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            _ => 8
        };
    }

    public static double ResolveInnerCornerRadius(string? cornerPreference)
    {
        return cornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 2,
            _ => 4
        };
    }

    public static double ResolveMediaCornerRadius(
        string? mode,
        string? cornerPreference)
    {
        return SettingsService.NormalizeWidgetCompactMediaCornerMode(mode) switch
        {
            SettingsService.WidgetCompactMediaCornerSquare => 0,
            SettingsService.WidgetCompactMediaCornerSmall => 4,
            SettingsService.WidgetCompactMediaCornerRound => Height / 2,
            _ => ResolveInnerCornerRadius(cornerPreference)
        };
    }

    public static void CapturePlacement(WidgetConfig config, RectInt32 bounds)
    {
        RectInt32 workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        var placementConfig = new WidgetConfig
        {
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
            X = bounds.X,
            Y = bounds.Y,
            Width = WidgetPositioningService.ToLogicalPixels(
                bounds.Width,
                WidgetPositioningService.GetDpiScale(workArea)),
            Height = WidgetPositioningService.ToLogicalPixels(
                bounds.Height,
                WidgetPositioningService.GetDpiScale(workArea))
        };
        WidgetPositioningService.CaptureAnchor(placementConfig, bounds, workArea);
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(placementConfig, bounds, workArea);

        config.CompactPlacement = new WidgetCompactPlacement
        {
            X = placementConfig.X,
            Y = placementConfig.Y,
            PositionAnchor = placementConfig.PositionAnchor,
            PositionMarginX = placementConfig.PositionMarginX,
            PositionMarginY = placementConfig.PositionMarginY,
            PositionMonitorKey = placementConfig.PositionMonitorKey,
            PositionMonitorDeviceName = placementConfig.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = placementConfig.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
        };
    }
}
