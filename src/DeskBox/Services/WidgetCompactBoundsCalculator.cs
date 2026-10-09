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
    public const double MaxAlignedWidth = 1200;
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
        double? compactWidth = null,
        bool clampCustomWidth = true,
        string? heightContentMode = null)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        double logicalWidth = ResolveLogicalWidth(
            contentMode,
            widgetKind,
            compactWidth,
            clampCustomWidth);
        int width = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        int height = Math.Max(
            1,
            (int)Math.Round(ResolveLogicalHeight(heightContentMode ?? contentMode, widgetKind) * scale));
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
        string? contentMode,
        bool alignToExpandedWidth = false,
        string? heightContentMode = null)
    {
        double? compactWidth = alignToExpandedWidth
            ? config.Width
            : config.CompactWidth;
        if (config.CompactPlacement is not { } placement)
        {
            return Calculate(
                expandedBounds,
                config.PositionAnchor,
                dpiScale,
                contentMode,
                config.WidgetKind,
                compactWidth,
                clampCustomWidth: !alignToExpandedWidth,
                heightContentMode: heightContentMode);
        }

        var placementConfig = new WidgetConfig
        {
            X = placement.X,
            Y = placement.Y,
            Width = ResolveLogicalWidth(
                contentMode,
                config.WidgetKind,
                compactWidth,
                clampCustomWidth: !alignToExpandedWidth),
            Height = ResolveLogicalHeight(heightContentMode ?? contentMode, config.WidgetKind),
            BoundsCoordinateVersion = placement.BoundsCoordinateVersion,
            PositionAnchor = placement.PositionAnchor,
            PositionMarginX = placement.PositionMarginX,
            PositionMarginY = placement.PositionMarginY,
            // The capsule is derived from its surface (spec 4.4): monitor
            // identity comes from the surface so a stale capsule reference
            // can never pull the capsule onto the old screen; the capsule
            // itself only contributes the anchor and margins. Binding fields
            // share the surface's intent as before.
            PositionMonitorKey = config.PositionMonitorKey,
            PositionMonitorDeviceName = config.PositionMonitorDeviceName,
            PositionMonitorStableId = config.PositionMonitorStableId,
            PositionMonitorWasPrimary = config.PositionMonitorWasPrimary,
            ScreenBindingMode = config.ScreenBindingMode,
            BoundScreenId = config.BoundScreenId
        };
        RectInt32 resolved = WidgetPositioningService.ResolveBoundsForCurrentTopology(placementConfig);
        RectInt32 workArea = DisplayArea.GetFromRect(resolved, DisplayAreaFallback.Nearest).WorkArea;
        double resolvedScale = WidgetPositioningService.GetDpiScale(workArea);
        return ApplyCompactSizeToResolvedPlacement(
            resolved,
            placement.PositionAnchor,
            resolvedScale,
            placementConfig.Width,
            placementConfig.Height,
            clampLogicalWidth: !alignToExpandedWidth);
    }

    public static RectInt32 ApplyCompactSizeToResolvedPlacement(
        RectInt32 resolvedBounds,
        string? positionAnchor,
        double dpiScale,
        double logicalWidth,
        double logicalHeight = Height,
        bool clampLogicalWidth = true)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        double normalizedWidth = clampLogicalWidth
            ? ClampLogicalWidth(logicalWidth)
            : ClampAlignedLogicalWidth(logicalWidth);
        int width = Math.Max(1, (int)Math.Round(normalizedWidth * scale));
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
        WidgetCompactExpansionAnchor anchor =
            WidgetCompactExpansionCalculator.FromPositionAnchor(positionAnchor) ??
            WidgetCompactExpansionAnchor.LeftTop;
        return WidgetCompactExpansionCalculator.Resolve(
            compactBounds,
            new SizeInt32(expandedBounds.Width, expandedBounds.Height),
            workArea,
            [anchor]).ExpandedBounds;
    }

    public static SizeInt32 ResolveExpandedSizeForWidthMode(
        RectInt32 compactBounds,
        SizeInt32 expandedSize,
        string? widthMode)
    {
        int width = SettingsService.NormalizeWidgetCompactWidthMode(widthMode) ==
            SettingsService.WidgetCompactWidthModeAligned
                ? Math.Max(1, compactBounds.Width)
                : Math.Max(1, expandedSize.Width);
        return new SizeInt32(width, Math.Max(1, expandedSize.Height));
    }

    public static double ResolveLogicalWidth(
        string? contentMode,
        WidgetKind widgetKind,
        double? compactWidth = null,
        bool clampCustomWidth = true)
    {
        if (compactWidth is { } customWidth && double.IsFinite(customWidth))
        {
            return clampCustomWidth
                ? ClampLogicalWidth(customWidth)
                : ClampAlignedLogicalWidth(customWidth);
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
        // The Smart presentation is the shared high-density capsule treatment.
        // Keep every widget kind on the same outer height so mixed capsule bars
        // do not alternate between short and tall items.
        bool usesSmartDetailLayout = string.Equals(
            contentMode,
            SettingsService.WidgetCompactContentModeSmart,
            StringComparison.Ordinal);
        return usesSmartDetailLayout ? SmartDetailHeight : Height;
    }

    public static double ClampLogicalWidth(double width)
    {
        double finiteWidth = double.IsFinite(width) ? width : SummaryWidth;
        return Math.Clamp(finiteWidth, MinWidth, MaxWidth);
    }

    public static double ClampAlignedLogicalWidth(double width)
    {
        double finiteWidth = double.IsFinite(width) ? width : SummaryWidth;
        return Math.Clamp(finiteWidth, MinWidth, MaxAlignedWidth);
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

    public static void CapturePlacement(
        WidgetConfig config,
        RectInt32 bounds,
        string? expansionDirection = null)
    {
        // Kept for source compatibility with older callers. Compact position
        // capture is intentionally independent of expansion direction.
        _ = expansionDirection;
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
            // Derived fields (spec 4.4): the stored capsule monitor identity
            // always mirrors the surface's identity, never its own snapshot.
            PositionMonitorKey = config.PositionMonitorKey,
            PositionMonitorDeviceName = config.PositionMonitorDeviceName,
            PositionMonitorStableId = config.PositionMonitorStableId,
            PositionMonitorWasPrimary = config.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion
        };
    }
}
