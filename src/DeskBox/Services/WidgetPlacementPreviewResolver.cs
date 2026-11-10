using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services;

public readonly record struct WidgetSurfacePreviewItem(
    string DisplayName,
    string ShortLabel,
    string ApplyWidgetId,
    WidgetKind Kind,
    int GroupMemberCount,
    WidgetScreenBindingMode Mode,
    string? BoundScreenId,
    string? BoundScreenLabel,
    WidgetScreenInfo? ActualScreen,
    bool IsBorrowedFallback,
    bool IsSizeLocked,
    bool IsVisible,
    bool IsCompact,
    RectInt32? PreviewBounds,
    string? MembersText = null);

/// <summary>
/// Resolves active desktop surfaces (standalone widgets and widget groups)
/// onto the current display catalog with exact expanded vs capsule bounds,
/// excluding unimplemented or session-disabled widget kinds so the Settings
/// display preview mirrors the live desktop 1:1.
/// </summary>
public static class WidgetPlacementPreviewResolver
{
    private const double MinSurfaceSizeDips = 20;

    public static List<WidgetSurfacePreviewItem> ResolveSurfaces(
        AppSettings settings,
        IReadOnlyList<WidgetScreenInfo> screens,
        Func<int, string> formatGroupHeader,
        Func<string, string> formatGroupMembers)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(screens);
        ArgumentNullException.ThrowIfNull(formatGroupHeader);
        ArgumentNullException.ThrowIfNull(formatGroupMembers);

        WidgetLayoutSettingsSlice layout = settings.WidgetLayout;
        WidgetShellSettingsSlice shell = settings.WidgetShell;
        var groupedIds = new HashSet<string>(
            layout.WidgetGroups.SelectMany(group => group.MemberIds),
            StringComparer.Ordinal);

        var surfaces = new List<WidgetSurfacePreviewItem>();
        foreach (WidgetConfig widget in layout.Widgets)
        {
            if (widget.IsDisabled ||
                layout.DeletedWidgetIds.Contains(widget.Id) ||
                groupedIds.Contains(widget.Id) ||
                !WidgetRegistry.Default.IsAvailableForSession(widget, settings))
            {
                continue;
            }

            WidgetCollapseBehavior effectiveBehavior = WidgetCollapseBehaviorNames.Resolve(
                widget,
                shell.WidgetCollapseBehavior);
            bool isCompact = effectiveBehavior == WidgetCollapseBehavior.Smart ||
                (effectiveBehavior == WidgetCollapseBehavior.Click && widget.IsCollapsed);

            (WidgetScreenInfo? actual, bool borrowed, RectInt32? bounds) = ResolveSurfaceBounds(
                screens,
                shell,
                widget.WidgetKind,
                widget.ScreenBindingMode,
                widget.BoundScreenId,
                widget.PositionMonitorStableId,
                widget.PositionMonitorDeviceName,
                widget.PositionMonitorKey,
                widget.PositionMonitorWasPrimary,
                widget.X,
                widget.Y,
                widget.Width,
                widget.Height,
                widget.PositionAnchor,
                widget.PositionMarginX,
                widget.PositionMarginY,
                widget.BoundsCoordinateVersion,
                isCompact,
                widget.CompactPlacement,
                widget.CompactWidth);

            surfaces.Add(new WidgetSurfacePreviewItem(
                DisplayName: widget.Name,
                ShortLabel: widget.Name,
                ApplyWidgetId: widget.Id,
                Kind: widget.WidgetKind,
                GroupMemberCount: 1,
                Mode: widget.ScreenBindingMode,
                BoundScreenId: widget.BoundScreenId,
                BoundScreenLabel: widget.BoundScreenLabel,
                ActualScreen: actual,
                IsBorrowedFallback: borrowed,
                IsSizeLocked: widget.IsSizeLocked,
                IsVisible: widget.IsVisible,
                IsCompact: isCompact,
                PreviewBounds: bounds));
        }

        foreach (WidgetGroupConfig group in layout.WidgetGroups)
        {
            List<WidgetConfig> members = group.MemberIds
                .Select(id => layout.Widgets.FirstOrDefault(widget =>
                    string.Equals(widget.Id, id, StringComparison.Ordinal)))
                .Where(member =>
                    member is not null &&
                    !member.IsDisabled &&
                    !layout.DeletedWidgetIds.Contains(member.Id) &&
                    WidgetRegistry.Default.IsAvailableForSession(member, settings))
                .Cast<WidgetConfig>()
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            WidgetConfig activeMember = members.FirstOrDefault(member =>
                    string.Equals(member.Id, group.ActiveMemberId, StringComparison.Ordinal))
                ?? members[0];

            WidgetCollapseBehavior groupOverride = WidgetCollapseBehaviorNames.Normalize(
                group.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true);
            WidgetCollapseBehavior effectiveBehavior = groupOverride == WidgetCollapseBehavior.System
                ? WidgetCollapseBehaviorNames.Normalize(shell.WidgetCollapseBehavior)
                : groupOverride;
            bool isCompact = effectiveBehavior == WidgetCollapseBehavior.Smart ||
                (effectiveBehavior == WidgetCollapseBehavior.Click && group.IsCollapsed);

            bool isSizeLocked = group.IsSizeLocked || members.Any(member => member.IsSizeLocked);
            (WidgetScreenInfo? actual, bool borrowed, RectInt32? bounds) = ResolveSurfaceBounds(
                screens,
                shell,
                activeMember.WidgetKind,
                group.ScreenBindingMode,
                group.BoundScreenId,
                group.PositionMonitorStableId,
                group.PositionMonitorDeviceName,
                group.PositionMonitorKey,
                group.PositionMonitorWasPrimary,
                group.X,
                group.Y,
                group.Width,
                group.Height,
                group.PositionAnchor,
                group.PositionMarginX,
                group.PositionMarginY,
                group.BoundsCoordinateVersion,
                isCompact,
                group.CompactPlacement ?? activeMember.CompactPlacement,
                group.CompactWidth ?? activeMember.CompactWidth);

            string shortLabel = BuildGroupShortLabel(members, activeMember);
            surfaces.Add(new WidgetSurfacePreviewItem(
                DisplayName: formatGroupHeader(members.Count),
                ShortLabel: shortLabel,
                ApplyWidgetId: members[0].Id,
                Kind: activeMember.WidgetKind,
                GroupMemberCount: members.Count,
                Mode: group.ScreenBindingMode,
                BoundScreenId: group.BoundScreenId,
                BoundScreenLabel: group.BoundScreenLabel,
                ActualScreen: actual,
                IsBorrowedFallback: borrowed,
                IsSizeLocked: isSizeLocked,
                IsVisible: group.IsVisible || members.Any(member => member.IsVisible),
                IsCompact: isCompact,
                PreviewBounds: bounds,
                MembersText: formatGroupMembers(string.Join(", ", members.Select(member => member.Name)))));
        }

        return surfaces;
    }

    private static string BuildGroupShortLabel(
        IReadOnlyList<WidgetConfig> members,
        WidgetConfig activeMember)
    {
        if (members.Count == 1)
        {
            return activeMember.Name;
        }

        string joined = string.Join(" / ", members.Take(2).Select(member => member.Name));
        return members.Count > 2 ? $"{joined} +{members.Count - 2}" : joined;
    }

    private static (WidgetScreenInfo? ActualScreen, bool IsBorrowedFallback, RectInt32? Bounds)
        ResolveSurfaceBounds(
            IReadOnlyList<WidgetScreenInfo> screens,
            WidgetShellSettingsSlice shell,
            WidgetKind kind,
            WidgetScreenBindingMode mode,
            string? boundScreenId,
            string? monitorStableId,
            string? monitorDeviceName,
            string? monitorKey,
            bool? monitorWasPrimary,
            double rawX,
            double rawY,
            double widthDips,
            double heightDips,
            string? anchor,
            double marginX,
            double marginY,
            int boundsCoordVersion,
            bool isCompact,
            WidgetCompactPlacement? compactPlacement,
            double? compactWidthDips)
    {
        if (screens.Count == 0)
        {
            return (null, false, null);
        }

        ResolvedDisplayPlacement resolved = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(mode, boundScreenId),
            new DisplayPlacementEntryReference(
                monitorStableId,
                monitorDeviceName,
                monitorKey,
                monitorWasPrimary),
            screens);
        WidgetScreenInfo actual = resolved.Display;
        double scale = Math.Max(0.5, actual.EffectiveDpiScale);

        double safeWidthDips = double.IsFinite(widthDips) && widthDips > 0
            ? Math.Max(MinSurfaceSizeDips, widthDips)
            : SettingsService.DefaultWidgetWidth;
        double safeHeightDips = double.IsFinite(heightDips) && heightDips > 0
            ? Math.Max(MinSurfaceSizeDips, heightDips)
            : SettingsService.DefaultWidgetHeight;

        RectInt32 expandedBounds = ResolvePlacementOnWorkArea(
            actual.WorkArea,
            scale,
            rawX,
            rawY,
            safeWidthDips,
            safeHeightDips,
            anchor,
            marginX,
            marginY,
            boundsCoordVersion);
        if (!isCompact)
        {
            return (actual, resolved.IsFallback, expandedBounds);
        }

        bool alignToExpandedWidth =
            SettingsService.NormalizeWidgetCompactWidthMode(shell.WidgetCompactWidthMode) ==
            SettingsService.WidgetCompactWidthModeAligned;
        string contentMode = WidgetCompactPrivacyPolicy.ResolveContentMode(
            shell.WidgetCompactContentMode,
            shell.WidgetCompactHideSensitiveContent,
            kind);
        double? desiredCompactWidth = alignToExpandedWidth ? safeWidthDips : compactWidthDips;

        if (compactPlacement is null)
        {
            RectInt32 calculated = WidgetCompactBoundsCalculator.Calculate(
                expandedBounds,
                anchor,
                scale,
                contentMode,
                kind,
                desiredCompactWidth,
                clampCustomWidth: !alignToExpandedWidth,
                heightContentMode: shell.WidgetCompactContentMode);
            return (actual, resolved.IsFallback, ClampToWorkArea(calculated, actual.WorkArea));
        }

        double compactLogicalWidth = WidgetCompactBoundsCalculator.ResolveLogicalWidth(
            contentMode,
            kind,
            desiredCompactWidth,
            clampCustomWidth: !alignToExpandedWidth);
        double compactLogicalHeight = WidgetCompactBoundsCalculator.ResolveLogicalHeight(
            shell.WidgetCompactContentMode,
            kind);
        RectInt32 resolvedPlacement = ResolvePlacementOnWorkArea(
            actual.WorkArea,
            scale,
            compactPlacement.X,
            compactPlacement.Y,
            compactLogicalWidth,
            compactLogicalHeight,
            compactPlacement.PositionAnchor,
            compactPlacement.PositionMarginX,
            compactPlacement.PositionMarginY,
            compactPlacement.BoundsCoordinateVersion);
        RectInt32 compactBounds = WidgetCompactBoundsCalculator.ApplyCompactSizeToResolvedPlacement(
            resolvedPlacement,
            compactPlacement.PositionAnchor,
            scale,
            compactLogicalWidth,
            compactLogicalHeight,
            clampLogicalWidth: !alignToExpandedWidth);
        return (actual, resolved.IsFallback, ClampToWorkArea(compactBounds, actual.WorkArea));
    }

    private static RectInt32 ResolvePlacementOnWorkArea(
        RectInt32 workArea,
        double scale,
        double rawX,
        double rawY,
        double logicalWidth,
        double logicalHeight,
        string? anchor,
        double marginX,
        double marginY,
        int boundsCoordVersion)
    {
        int physW = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        int physH = Math.Max(1, (int)Math.Round(logicalHeight * scale));

        int x;
        int y;
        if (IsCornerAnchor(anchor) && double.IsFinite(marginX) && double.IsFinite(marginY))
        {
            int marginXPx = Math.Max(0, (int)Math.Round(marginX * scale));
            int marginYPx = Math.Max(0, (int)Math.Round(marginY * scale));
            bool anchorRight = anchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
            bool anchorBottom = anchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
            x = anchorRight
                ? workArea.X + workArea.Width - physW - marginXPx
                : workArea.X + marginXPx;
            y = anchorBottom
                ? workArea.Y + workArea.Height - physH - marginYPx
                : workArea.Y + marginYPx;
        }
        else
        {
            double safeX = double.IsFinite(rawX) ? rawX : workArea.X;
            double safeY = double.IsFinite(rawY) ? rawY : workArea.Y;
            x = boundsCoordVersion >= WidgetConfig.CurrentBoundsCoordinateVersion
                ? (int)Math.Round(safeX)
                : (int)Math.Round(safeX * scale);
            y = boundsCoordVersion >= WidgetConfig.CurrentBoundsCoordinateVersion
                ? (int)Math.Round(safeY)
                : (int)Math.Round(safeY * scale);
        }

        return ClampToWorkArea(new RectInt32(x, y, physW, physH), workArea);
    }

    private static RectInt32 ClampToWorkArea(RectInt32 bounds, RectInt32 workArea)
    {
        int width = Math.Clamp(bounds.Width, 1, Math.Max(1, workArea.Width));
        int height = Math.Clamp(bounds.Height, 1, Math.Max(1, workArea.Height));
        int maxLeft = Math.Max(workArea.X, workArea.X + workArea.Width - width);
        int maxTop = Math.Max(workArea.Y, workArea.Y + workArea.Height - height);
        int x = Math.Clamp(bounds.X, workArea.X, maxLeft);
        int y = Math.Clamp(bounds.Y, workArea.Y, maxTop);
        return new RectInt32(x, y, width, height);
    }

    private static bool IsCornerAnchor(string? anchor) =>
        anchor is WidgetPositionAnchors.LeftTop or
            WidgetPositionAnchors.RightTop or
            WidgetPositionAnchors.LeftBottom or
            WidgetPositionAnchors.RightBottom;
}
