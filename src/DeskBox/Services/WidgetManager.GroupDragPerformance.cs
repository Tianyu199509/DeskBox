// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Contracts;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private string? _groupDragCandidateSourceId;
    private WidgetGroupDragCandidate[] _groupDragCandidates = [];

    private WidgetGroupDragCandidate? FindWidgetGroupDragCandidateAtPoint(
        string sourceWidgetId,
        int screenX,
        int screenY)
    {
        EnsureWidgetGroupDragCandidateCache(sourceWidgetId);

        WidgetGroupDragCandidate? best = null;
        long bestArea = long.MaxValue;
        foreach (WidgetGroupDragCandidate candidate in _groupDragCandidates)
        {
            if (!candidate.Window.Visible)
            {
                continue;
            }

            Windows.Graphics.RectInt32? bounds =
                candidate.Window.GetGroupMergeTitleScreenBounds();
            if (!WidgetGroupDropHitTestPolicy.Contains(bounds, screenX, screenY))
            {
                continue;
            }

            long area = (long)bounds!.Value.Width * bounds.Value.Height;
            if (area < bestArea)
            {
                best = candidate;
                bestArea = area;
            }
        }

        return best;
    }

    private void EnsureWidgetGroupDragCandidateCache(string sourceWidgetId)
    {
        if (string.Equals(
                _groupDragCandidateSourceId,
                sourceWidgetId,
                StringComparison.Ordinal))
        {
            return;
        }

        IReadOnlyList<WidgetGroupJoinTarget> joinTargets =
            GetWidgetGroupJoinTargets(sourceWidgetId);
        var rulesByTargetId = new Dictionary<string, WidgetGroupJoinTarget>(
            joinTargets.Count,
            StringComparer.Ordinal);
        foreach (WidgetGroupJoinTarget rule in joinTargets)
        {
            rulesByTargetId[rule.TargetWidgetId] = rule;
        }

        var candidates = new List<WidgetGroupDragCandidate>(rulesByTargetId.Count);
        foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
        {
            if (window.Visible &&
                !string.Equals(window.Config.Id, sourceWidgetId, StringComparison.Ordinal) &&
                rulesByTargetId.TryGetValue(window.Config.Id, out WidgetGroupJoinTarget? rule) &&
                rule is not null)
            {
                candidates.Add(new WidgetGroupDragCandidate(window, rule));
            }
        }

        _groupDragCandidateSourceId = sourceWidgetId;
        _groupDragCandidates = candidates.ToArray();
    }

    private void ClearWidgetGroupDragCandidateCache()
    {
        _groupDragCandidateSourceId = null;
        _groupDragCandidates = [];
    }

    private readonly record struct WidgetGroupDragCandidate(
        IDesktopWidgetWindow Window,
        WidgetGroupJoinTarget Rule);
}
