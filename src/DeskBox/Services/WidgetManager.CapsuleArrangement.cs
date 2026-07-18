using DeskBox.Models;
using DeskBox.Views;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const double CapsuleBarEdgeMargin = 12;

    private readonly Dictionary<string, RectInt32> _lastCapsuleBarBounds =
        new(StringComparer.Ordinal);
    private string _lastEffectiveCapsuleArrangementMode = SettingsService.WidgetCapsuleArrangementFree;
    private string _lastCapsuleBarPlacement = SettingsService.WidgetCapsuleBarPlacementFloating;
    private string _lastCapsuleBarDirection = SettingsService.WidgetCapsuleBarDirectionAuto;
    private string _lastCapsuleBarGeometrySignature = string.Empty;
    private double _lastCapsuleBarSpacing = SettingsService.DefaultWidgetCapsuleBarSpacing;
    private string _lastCapsuleArrangementMemberSignature = string.Empty;
    private bool _isApplyingCapsuleArrangement;
    private CapsuleBarDragSession? _capsuleBarDragSession;

    private void InitializeCapsuleArrangementState()
    {
        AppSettings settings = _settingsService.Settings;
        _lastEffectiveCapsuleArrangementMode = ResolveEffectiveCapsuleArrangementMode();
        _lastCapsuleBarPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        _lastCapsuleBarDirection = SettingsService.NormalizeWidgetCapsuleBarDirection(
            settings.WidgetCapsuleBarDirection);
        _lastCapsuleBarSpacing = SettingsService.NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
    }

    internal void RefreshCapsuleBarLayout()
    {
        ApplyCapsuleArrangementIfChanged(force: true);
    }

    private void ApplyCapsuleArrangementIfChanged(bool force = false)
    {
        if (!HasUiThreadAccess())
        {
            App.UiDispatcherQueue.TryEnqueue(() => ApplyCapsuleArrangementIfChanged(force));
            return;
        }

        if (_isApplyingCapsuleArrangement || _capsuleBarDragSession is not null)
        {
            return;
        }

        AppSettings settings = _settingsService.Settings;
        string mode = ResolveEffectiveCapsuleArrangementMode();
        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        string direction = SettingsService.NormalizeWidgetCapsuleBarDirection(
            settings.WidgetCapsuleBarDirection);
        double spacing = SettingsService.NormalizeWidgetCapsuleBarSpacing(
            settings.WidgetCapsuleBarSpacing);
        IReadOnlyList<WidgetConfig> candidates = GetCapsuleArrangementCandidates(settings);
        string memberSignature = string.Join('|', candidates.Select(config => config.Id));
        string geometrySignature = BuildCapsuleBarGeometrySignature(settings, candidates);
        if (!force &&
            string.Equals(mode, _lastEffectiveCapsuleArrangementMode, StringComparison.Ordinal) &&
            string.Equals(placement, _lastCapsuleBarPlacement, StringComparison.Ordinal) &&
            string.Equals(direction, _lastCapsuleBarDirection, StringComparison.Ordinal) &&
            Math.Abs(spacing - _lastCapsuleBarSpacing) <= 0.0001 &&
            string.Equals(
                memberSignature,
                _lastCapsuleArrangementMemberSignature,
                StringComparison.Ordinal) &&
            string.Equals(
                geometrySignature,
                _lastCapsuleBarGeometrySignature,
                StringComparison.Ordinal))
        {
            return;
        }

        string previousMode = _lastEffectiveCapsuleArrangementMode;
        _lastEffectiveCapsuleArrangementMode = mode;
        _lastCapsuleBarPlacement = placement;
        _lastCapsuleBarDirection = direction;
        _lastCapsuleBarSpacing = spacing;
        _lastCapsuleArrangementMemberSignature = memberSignature;
        _lastCapsuleBarGeometrySignature = geometrySignature;

        _isApplyingCapsuleArrangement = true;
        try
        {
            bool changed = CleanupCapsuleArrangementState(settings);
            if (mode == SettingsService.WidgetCapsuleArrangementFree)
            {
                _lastCapsuleBarBounds.Clear();
                if (previousMode != SettingsService.WidgetCapsuleArrangementFree ||
                    settings.WidgetCapsuleFreePlacements.Count > 0)
                {
                    changed |= RestoreFreeCapsulePlacements(settings);
                }
            }
            else
            {
                bool rebuildOrder = previousMode == SettingsService.WidgetCapsuleArrangementFree;
                changed |= ArrangeCapsuleCandidates(
                    settings,
                    candidates,
                    placement,
                    direction,
                    spacing,
                    rebuildOrder);
            }

            if (changed)
            {
                _settingsService.SaveDebounced(notifySubscribers: false);
            }
        }
        finally
        {
            _isApplyingCapsuleArrangement = false;
        }
    }

    internal bool BeginCapsuleBarDrag(string widgetId, bool reorderMember)
    {
        if (_capsuleBarDragSession is not null ||
            ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar ||
            !_lastCapsuleBarBounds.TryGetValue(widgetId, out RectInt32 activeBounds))
        {
            return false;
        }

        RectInt32 workArea = DisplayArea.GetFromRect(
            activeBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        string workAreaKey = FormatWorkAreaKey(workArea);
        var memberBounds = _lastCapsuleBarBounds
            .Where(entry => FormatWorkAreaKey(DisplayArea.GetFromRect(
                entry.Value,
                DisplayAreaFallback.Nearest).WorkArea) == workAreaKey)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!memberBounds.ContainsKey(widgetId))
        {
            return false;
        }

        AppSettings settings = _settingsService.Settings;
        var memberOrder = settings.WidgetCapsuleBarOrder
            .Where(memberBounds.ContainsKey)
            .ToList();
        foreach (string id in memberBounds.Keys)
        {
            if (!memberOrder.Contains(id, StringComparer.Ordinal))
            {
                memberOrder.Add(id);
            }
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            settings.WidgetCapsuleBarPlacement);
        string direction = ResolveBarDirection(
            settings.WidgetCapsuleBarDirection,
            placement,
            workArea);
        string firstId = memberOrder[0];
        RectInt32 firstBounds = memberBounds[firstId];
        WidgetConfig? firstConfig = settings.Widgets.FirstOrDefault(config =>
            string.Equals(config.Id, firstId, StringComparison.Ordinal));
        (PointInt32 anchorPoint, string anchor) = ResolveBarAnchor(
            firstBounds,
            firstConfig?.CompactPlacement?.PositionAnchor ?? firstConfig?.PositionAnchor,
            workArea,
            placement);
        int spacing = Math.Max(0, (int)Math.Round(
            SettingsService.NormalizeWidgetCapsuleBarSpacing(settings.WidgetCapsuleBarSpacing) *
            WidgetPositioningService.GetDpiScale(workArea)));

        _capsuleBarDragSession = new CapsuleBarDragSession(
            widgetId,
            activeBounds,
            memberBounds,
            workArea,
            reorderMember,
            memberOrder,
            direction,
            anchorPoint,
            anchor,
            spacing);
        return true;
    }

    internal bool TryMoveCapsuleBar(
        string widgetId,
        RectInt32 proposedActiveBounds,
        out RectInt32 resolvedActiveBounds)
    {
        resolvedActiveBounds = proposedActiveBounds;
        if (_capsuleBarDragSession is not { } session ||
            !string.Equals(session.ActiveWidgetId, widgetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (session.ReordersMember)
        {
            return TryReorderCapsuleBarMember(
                session,
                proposedActiveBounds,
                out resolvedActiveBounds);
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            _settingsService.Settings.WidgetCapsuleBarPlacement);
        int deltaX = proposedActiveBounds.X - session.ActiveStartBounds.X;
        int deltaY = proposedActiveBounds.Y - session.ActiveStartBounds.Y;
        if (placement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            deltaY = 0;
        }
        else if (placement is SettingsService.WidgetCapsuleBarPlacementLeft or
                 SettingsService.WidgetCapsuleBarPlacementRight)
        {
            deltaX = 0;
        }

        RectInt32 workArea = placement == SettingsService.WidgetCapsuleBarPlacementFloating
            ? DisplayArea.GetFromRect(proposedActiveBounds, DisplayAreaFallback.Nearest).WorkArea
            : session.WorkArea;
        RectInt32 groupBounds = GetUnion(session.MemberStartBounds.Values);
        deltaX = ClampGroupDelta(
            groupBounds.X,
            groupBounds.Width,
            deltaX,
            workArea.X,
            workArea.Width);
        deltaY = ClampGroupDelta(
            groupBounds.Y,
            groupBounds.Height,
            deltaY,
            workArea.Y,
            workArea.Height);
        session.WorkArea = workArea;

        foreach ((string id, RectInt32 startBounds) in session.MemberStartBounds)
        {
            var target = new RectInt32(
                startBounds.X + deltaX,
                startBounds.Y + deltaY,
                startBounds.Width,
                startBounds.Height);
            _lastCapsuleBarBounds[id] = target;
            if (FindLoadedWindow(id) is { IsCompactArrangementActive: true } window)
            {
                window.PreviewCompactArrangement(target);
            }
        }

        resolvedActiveBounds = _lastCapsuleBarBounds[widgetId];
        return true;
    }

    private bool TryReorderCapsuleBarMember(
        CapsuleBarDragSession session,
        RectInt32 proposedActiveBounds,
        out RectInt32 resolvedActiveBounds)
    {
        IReadOnlyList<string> reordered = WidgetCapsuleOrderCalculator.MoveToNearestSlot(
            session.MemberOrder,
            session.MemberSlots,
            session.ActiveWidgetId,
            proposedActiveBounds,
            session.Direction);
        if (!session.MemberOrder.SequenceEqual(reordered, StringComparer.Ordinal))
        {
            session.MemberOrder = reordered.ToList();
            AppSettings settings = _settingsService.Settings;
            IReadOnlyList<string> merged = WidgetCapsuleOrderCalculator.MergeGroupOrder(
                settings.WidgetCapsuleBarOrder,
                session.MemberOrder);
            settings.WidgetCapsuleBarOrder.Clear();
            settings.WidgetCapsuleBarOrder.AddRange(merged);

            var items = session.MemberOrder
                .Select(id =>
                {
                    RectInt32 bounds = session.MemberStartBounds[id];
                    return new WidgetCapsuleArrangementItem(id, bounds.Width, bounds.Height);
                })
                .ToList();
            IReadOnlyDictionary<string, RectInt32> arranged =
                WidgetCapsuleArrangementCalculator.Calculate(
                    items,
                    session.WorkArea,
                    session.AnchorPoint,
                    session.PositionAnchor,
                    session.Direction,
                    session.Spacing);
            foreach ((string id, RectInt32 target) in arranged)
            {
                _lastCapsuleBarBounds[id] = target;
                if (FindLoadedWindow(id) is { IsCompactArrangementActive: true } window)
                {
                    window.PreviewCompactArrangement(target);
                }
            }
        }

        resolvedActiveBounds = _lastCapsuleBarBounds.TryGetValue(
            session.ActiveWidgetId,
            out RectInt32 activeBounds)
                ? activeBounds
                : session.ActiveStartBounds;
        return true;
    }

    internal void CompleteCapsuleBarDrag(string widgetId)
    {
        if (_capsuleBarDragSession is not { } session ||
            !string.Equals(session.ActiveWidgetId, widgetId, StringComparison.Ordinal))
        {
            return;
        }

        _capsuleBarDragSession = null;
        foreach (string id in session.MemberStartBounds.Keys)
        {
            if (!_lastCapsuleBarBounds.TryGetValue(id, out RectInt32 bounds))
            {
                continue;
            }

            WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(
                candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is null)
            {
                continue;
            }

            config.CompactPlacement = CreatePlacement(bounds);
            FindLoadedWindow(id)?.ApplyCompactArrangement(bounds, constrainSize: true);
        }

        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    internal bool MoveCapsuleBarFromExpandedWidget(
        string widgetId,
        int requestedDeltaX,
        int requestedDeltaY,
        out RectInt32 activeBounds)
    {
        activeBounds = default;
        if (ResolveEffectiveCapsuleArrangementMode() != SettingsService.WidgetCapsuleArrangementBar ||
            !_lastCapsuleBarBounds.TryGetValue(widgetId, out RectInt32 originalActiveBounds))
        {
            return false;
        }

        RectInt32 originalWorkArea = DisplayArea.GetFromRect(
            originalActiveBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        string workAreaKey = FormatWorkAreaKey(originalWorkArea);
        Dictionary<string, RectInt32> memberBounds = _lastCapsuleBarBounds
            .Where(entry => FormatWorkAreaKey(DisplayArea.GetFromRect(
                entry.Value,
                DisplayAreaFallback.Nearest).WorkArea) == workAreaKey)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (!memberBounds.ContainsKey(widgetId))
        {
            return false;
        }

        string placement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
            _settingsService.Settings.WidgetCapsuleBarPlacement);
        int deltaX = requestedDeltaX;
        int deltaY = requestedDeltaY;
        if (placement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            deltaY = 0;
        }
        else if (placement is SettingsService.WidgetCapsuleBarPlacementLeft or
                 SettingsService.WidgetCapsuleBarPlacementRight)
        {
            deltaX = 0;
        }

        RectInt32 proposedActive = new(
            originalActiveBounds.X + deltaX,
            originalActiveBounds.Y + deltaY,
            originalActiveBounds.Width,
            originalActiveBounds.Height);
        RectInt32 workArea = placement == SettingsService.WidgetCapsuleBarPlacementFloating
            ? DisplayArea.GetFromRect(proposedActive, DisplayAreaFallback.Nearest).WorkArea
            : originalWorkArea;
        RectInt32 groupBounds = GetUnion(memberBounds.Values);
        deltaX = ClampGroupDelta(
            groupBounds.X,
            groupBounds.Width,
            deltaX,
            workArea.X,
            workArea.Width);
        deltaY = ClampGroupDelta(
            groupBounds.Y,
            groupBounds.Height,
            deltaY,
            workArea.Y,
            workArea.Height);

        foreach ((string id, RectInt32 bounds) in memberBounds)
        {
            var target = new RectInt32(
                bounds.X + deltaX,
                bounds.Y + deltaY,
                bounds.Width,
                bounds.Height);
            _lastCapsuleBarBounds[id] = target;
            WidgetConfig? config = _settingsService.Settings.Widgets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is not null)
            {
                config.CompactPlacement = CreatePlacement(target);
            }
            FindLoadedWindow(id)?.ApplyCompactArrangement(target, constrainSize: true);
        }

        activeBounds = _lastCapsuleBarBounds[widgetId];
        _settingsService.SaveDebounced(notifySubscribers: false);
        return true;
    }

    private string ResolveEffectiveCapsuleArrangementMode()
    {
        AppSettings settings = _settingsService.Settings;
        return settings.WidgetCapsuleModeEnabled
            ? SettingsService.NormalizeWidgetCapsuleArrangementMode(
                settings.WidgetCapsuleArrangementMode)
            : SettingsService.WidgetCapsuleArrangementFree;
    }

    private IReadOnlyList<WidgetConfig> GetCapsuleArrangementCandidates(AppSettings settings)
    {
        if (!settings.WidgetCapsuleModeEnabled)
        {
            return [];
        }

        return settings.Widgets
            .Where(config =>
                config.IsVisible &&
                !config.IsDisabled &&
                _widgetRegistry.IsAvailableForSession(config, settings) &&
                WidgetCollapseBehaviorNames.Resolve(config, settings.WidgetCollapseBehavior) !=
                    WidgetCollapseBehavior.Expanded)
            .ToList();
    }

    private static string BuildCapsuleBarGeometrySignature(
        AppSettings settings,
        IReadOnlyList<WidgetConfig> candidates)
    {
        string widthMode = SettingsService.NormalizeWidgetCompactWidthMode(
            settings.WidgetCompactWidthMode);
        bool usesAlignedWidth = widthMode == SettingsService.WidgetCompactWidthModeAligned;
        string candidateWidths = string.Join(';', candidates.Select(config =>
        {
            string width = usesAlignedWidth
                ? config.Width.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : config.CompactWidth?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "auto";
            return $"{config.Id}:{config.WidgetKind}:{width}";
        }));
        return string.Join(
            '|',
            settings.WidgetCompactContentMode,
            settings.WidgetCompactHideSensitiveContent,
            widthMode,
            candidateWidths);
    }

    private static bool CleanupCapsuleArrangementState(AppSettings settings)
    {
        bool changed = false;
        var validIds = settings.Widgets
            .Select(config => config.Id)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int removedOrderEntries = settings.WidgetCapsuleBarOrder.RemoveAll(
            id => !validIds.Contains(id) || !seen.Add(id));
        changed |= removedOrderEntries > 0;
        foreach (string id in settings.WidgetCapsuleFreePlacements.Keys
                     .Where(id => !validIds.Contains(id))
                     .ToList())
        {
            settings.WidgetCapsuleFreePlacements.Remove(id);
            changed = true;
        }

        return changed;
    }

    private bool ArrangeCapsuleCandidates(
        AppSettings settings,
        IReadOnlyList<WidgetConfig> candidates,
        string placement,
        string configuredDirection,
        double logicalSpacing,
        bool rebuildOrder)
    {
        bool changed = false;
        var resolvedById = candidates.ToDictionary(
            config => config.Id,
            config => ResolveCandidate(config),
            StringComparer.Ordinal);
        foreach (ResolvedCapsuleCandidate candidate in resolvedById.Values)
        {
            if (!settings.WidgetCapsuleFreePlacements.ContainsKey(candidate.Config.Id))
            {
                settings.WidgetCapsuleFreePlacements[candidate.Config.Id] =
                    candidate.Config.CompactPlacement is { } freePlacement
                        ? ClonePlacement(freePlacement)
                        : CreatePlacement(candidate.Bounds);
                changed = true;
            }
        }

        if (rebuildOrder)
        {
            settings.WidgetCapsuleBarOrder.RemoveAll(resolvedById.ContainsKey);
            foreach (ResolvedCapsuleCandidate candidate in resolvedById.Values
                         .OrderBy(item => FormatWorkAreaKey(item.WorkArea), StringComparer.Ordinal)
                         .ThenBy(item => ResolveBarDirection(configuredDirection, placement, item.WorkArea) ==
                             SettingsService.WidgetCapsuleBarDirectionVertical
                                 ? item.Bounds.X
                                 : item.Bounds.Y)
                         .ThenBy(item => ResolveBarDirection(configuredDirection, placement, item.WorkArea) ==
                             SettingsService.WidgetCapsuleBarDirectionVertical
                                 ? item.Bounds.Y
                                 : item.Bounds.X))
            {
                settings.WidgetCapsuleBarOrder.Add(candidate.Config.Id);
            }

            changed = true;
        }
        else
        {
            foreach (WidgetConfig config in candidates)
            {
                if (!settings.WidgetCapsuleBarOrder.Contains(config.Id, StringComparer.Ordinal))
                {
                    settings.WidgetCapsuleBarOrder.Add(config.Id);
                    changed = true;
                }
            }
        }

        var orderedCandidates = settings.WidgetCapsuleBarOrder
            .Where(resolvedById.ContainsKey)
            .Select(id => resolvedById[id])
            .ToList();
        _lastCapsuleBarBounds.Clear();
        foreach (IGrouping<string, ResolvedCapsuleCandidate> group in orderedCandidates.GroupBy(
                     candidate => FormatWorkAreaKey(candidate.WorkArea),
                     StringComparer.Ordinal))
        {
            List<ResolvedCapsuleCandidate> members = group.ToList();
            if (members.Count == 0)
            {
                continue;
            }

            ResolvedCapsuleCandidate first = members[0];
            string direction = ResolveBarDirection(configuredDirection, placement, first.WorkArea);
            (PointInt32 anchorPoint, string anchor) = ResolveBarAnchor(
                first.Bounds,
                first.Config.CompactPlacement?.PositionAnchor ?? first.Config.PositionAnchor,
                first.WorkArea,
                placement);
            double dpiScale = WidgetPositioningService.GetDpiScale(first.WorkArea);
            int physicalSpacing = Math.Max(0, (int)Math.Round(logicalSpacing * dpiScale));
            var items = members
                .Select(member => new WidgetCapsuleArrangementItem(
                    member.Config.Id,
                    member.Bounds.Width,
                    member.Bounds.Height))
                .ToList();
            IReadOnlyDictionary<string, RectInt32> arranged =
                WidgetCapsuleArrangementCalculator.Calculate(
                    items,
                    first.WorkArea,
                    anchorPoint,
                    anchor,
                    direction,
                    physicalSpacing);

            foreach (ResolvedCapsuleCandidate member in members)
            {
                RectInt32 target = arranged[member.Config.Id];
                _lastCapsuleBarBounds[member.Config.Id] = target;
                if (!BoundsEqual(member.Bounds, target))
                {
                    member.Config.CompactPlacement = CreatePlacement(target);
                    changed = true;
                }

                FindLoadedWindow(member.Config.Id)?.ApplyCompactArrangement(
                    target,
                    constrainSize: true);
            }
        }

        return changed;
    }

    private bool RestoreFreeCapsulePlacements(AppSettings settings)
    {
        bool changed = false;
        foreach ((string id, WidgetCompactPlacement placement) in
                 settings.WidgetCapsuleFreePlacements.ToList())
        {
            WidgetConfig? config = settings.Widgets.FirstOrDefault(
                candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (config is null)
            {
                continue;
            }

            config.CompactPlacement = ClonePlacement(placement);
            RectInt32 target = ResolveCompactBounds(config);
            FindLoadedWindow(id)?.ApplyCompactArrangement(target, constrainSize: false);
            changed = true;
        }

        if (settings.WidgetCapsuleFreePlacements.Count > 0)
        {
            settings.WidgetCapsuleFreePlacements.Clear();
            changed = true;
        }

        return changed;
    }

    private ResolvedCapsuleCandidate ResolveCandidate(WidgetConfig config)
    {
        RectInt32 bounds = ResolveCompactBounds(config);
        RectInt32 workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        return new ResolvedCapsuleCandidate(config, bounds, workArea);
    }

    private RectInt32 ResolveCompactBounds(WidgetConfig config)
    {
        RectInt32 expandedBounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(config);
        RectInt32 workArea = DisplayArea.GetFromRect(
            expandedBounds,
            DisplayAreaFallback.Nearest).WorkArea;
        return WidgetCompactBoundsCalculator.Resolve(
            config,
            expandedBounds,
            WidgetPositioningService.GetDpiScale(workArea),
            WidgetCompactPrivacyPolicy.ResolveContentMode(
                _settingsService.Settings.WidgetCompactContentMode,
                _settingsService.Settings.WidgetCompactHideSensitiveContent,
                config.WidgetKind),
            alignToExpandedWidth:
                SettingsService.NormalizeWidgetCompactWidthMode(
                    _settingsService.Settings.WidgetCompactWidthMode) ==
                SettingsService.WidgetCompactWidthModeAligned);
    }

    private static string ResolveBarDirection(
        string configuredDirection,
        string placement,
        RectInt32 workArea)
    {
        string normalized = SettingsService.NormalizeWidgetCapsuleBarDirection(configuredDirection);
        if (normalized != SettingsService.WidgetCapsuleBarDirectionAuto)
        {
            return normalized;
        }

        string normalizedPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(placement);
        if (normalizedPlacement is SettingsService.WidgetCapsuleBarPlacementLeft or
            SettingsService.WidgetCapsuleBarPlacementRight)
        {
            return SettingsService.WidgetCapsuleBarDirectionVertical;
        }

        if (normalizedPlacement is SettingsService.WidgetCapsuleBarPlacementTop or
            SettingsService.WidgetCapsuleBarPlacementBottom)
        {
            return SettingsService.WidgetCapsuleBarDirectionHorizontal;
        }

        return workArea.Width >= workArea.Height
            ? SettingsService.WidgetCapsuleBarDirectionHorizontal
            : SettingsService.WidgetCapsuleBarDirectionVertical;
    }

    private static (PointInt32 Point, string Anchor) ResolveBarAnchor(
        RectInt32 firstBounds,
        string? originalAnchor,
        RectInt32 workArea,
        string placement)
    {
        string normalizedPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(placement);
        bool originalRight = originalAnchor is
            WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool originalBottom = originalAnchor is
            WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        bool anchorRight = normalizedPlacement == SettingsService.WidgetCapsuleBarPlacementRight ||
            normalizedPlacement is not SettingsService.WidgetCapsuleBarPlacementLeft && originalRight;
        bool anchorBottom = normalizedPlacement == SettingsService.WidgetCapsuleBarPlacementBottom ||
            normalizedPlacement is not SettingsService.WidgetCapsuleBarPlacementTop && originalBottom;
        string anchor = (anchorRight, anchorBottom) switch
        {
            (true, true) => WidgetPositionAnchors.RightBottom,
            (true, false) => WidgetPositionAnchors.RightTop,
            (false, true) => WidgetPositionAnchors.LeftBottom,
            _ => WidgetPositionAnchors.LeftTop
        };
        double scale = WidgetPositioningService.GetDpiScale(workArea);
        int margin = Math.Max(0, (int)Math.Round(CapsuleBarEdgeMargin * scale));
        int x = normalizedPlacement switch
        {
            SettingsService.WidgetCapsuleBarPlacementLeft => workArea.X + margin,
            SettingsService.WidgetCapsuleBarPlacementRight => workArea.X + workArea.Width - margin,
            _ => anchorRight ? firstBounds.X + firstBounds.Width : firstBounds.X
        };
        int y = normalizedPlacement switch
        {
            SettingsService.WidgetCapsuleBarPlacementTop => workArea.Y + margin,
            SettingsService.WidgetCapsuleBarPlacementBottom => workArea.Y + workArea.Height - margin,
            _ => anchorBottom ? firstBounds.Y + firstBounds.Height : firstBounds.Y
        };
        return (new PointInt32(x, y), anchor);
    }

    private IDesktopWidgetWindow? FindLoadedWindow(string widgetId)
    {
        if (_widgets.TryGetValue(widgetId, out var fileEntry))
        {
            return fileEntry.Window;
        }

        if (_quickCaptureWidgets.TryGetValue(widgetId, out var quickCaptureEntry))
        {
            return quickCaptureEntry.Window;
        }

        return _contentWidgets.TryGetValue(widgetId, out ContentWidgetWindow? contentWindow)
            ? contentWindow
            : null;
    }

    private static WidgetCompactPlacement CreatePlacement(RectInt32 bounds)
    {
        var config = new WidgetConfig();
        WidgetCompactBoundsCalculator.CapturePlacement(config, bounds);
        return config.CompactPlacement!;
    }

    private static WidgetCompactPlacement ClonePlacement(WidgetCompactPlacement placement)
    {
        return new WidgetCompactPlacement
        {
            X = placement.X,
            Y = placement.Y,
            PositionAnchor = placement.PositionAnchor,
            PositionMarginX = placement.PositionMarginX,
            PositionMarginY = placement.PositionMarginY,
            PositionMonitorKey = placement.PositionMonitorKey,
            PositionMonitorDeviceName = placement.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = placement.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = placement.BoundsCoordinateVersion
        };
    }

    private static RectInt32 GetUnion(IEnumerable<RectInt32> bounds)
    {
        RectInt32[] items = bounds.ToArray();
        int minX = items.Min(item => item.X);
        int minY = items.Min(item => item.Y);
        int maxX = items.Max(item => item.X + item.Width);
        int maxY = items.Max(item => item.Y + item.Height);
        return new RectInt32(minX, minY, maxX - minX, maxY - minY);
    }

    private static int ClampGroupDelta(
        int groupStart,
        int groupLength,
        int requestedDelta,
        int workStart,
        int workLength)
    {
        int minimum = workStart - groupStart;
        int maximum = workStart + workLength - (groupStart + groupLength);
        if (minimum > maximum)
        {
            return minimum;
        }

        return Math.Clamp(requestedDelta, minimum, maximum);
    }

    private static string FormatWorkAreaKey(RectInt32 workArea) =>
        $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";

    private static bool BoundsEqual(RectInt32 left, RectInt32 right) =>
        left.X == right.X &&
        left.Y == right.Y &&
        left.Width == right.Width &&
        left.Height == right.Height;

    private sealed record ResolvedCapsuleCandidate(
        WidgetConfig Config,
        RectInt32 Bounds,
        RectInt32 WorkArea);

    private sealed class CapsuleBarDragSession(
        string activeWidgetId,
        RectInt32 activeStartBounds,
        IReadOnlyDictionary<string, RectInt32> memberStartBounds,
        RectInt32 workArea,
        bool reordersMember,
        IReadOnlyList<string> memberOrder,
        string direction,
        PointInt32 anchorPoint,
        string positionAnchor,
        int spacing)
    {
        public string ActiveWidgetId { get; } = activeWidgetId;
        public RectInt32 ActiveStartBounds { get; } = activeStartBounds;
        public IReadOnlyDictionary<string, RectInt32> MemberStartBounds { get; } = memberStartBounds;
        public RectInt32 WorkArea { get; set; } = workArea;
        public bool ReordersMember { get; } = reordersMember;
        public List<string> MemberOrder { get; set; } = memberOrder.ToList();
        public IReadOnlyList<RectInt32> MemberSlots { get; } = memberOrder
            .Select(id => memberStartBounds[id])
            .ToArray();
        public string Direction { get; } = direction;
        public PointInt32 AnchorPoint { get; } = anchorPoint;
        public string PositionAnchor { get; } = positionAnchor;
        public int Spacing { get; } = spacing;
    }
}
