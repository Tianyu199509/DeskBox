// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Contracts;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private string? _groupDragCandidateSourceId;
    private WidgetGroupDragCandidate[] _groupDragCandidates = [];
    private int _widgetWindowsMovedVersion;
    private int _groupDragCandidateBoundsVersion = -1;
    private Windows.Graphics.RectInt32?[] _groupDragCandidateBounds = [];
    private bool[] _groupDragCandidateBoundsFetched = [];

    /// <summary>
    /// Called by manager paths that physically move windows (capsule-bar
    /// previews, arrangement applies, coordinated moves). Cached candidate
    /// bounds are re-fetched afterwards so merge hit-testing never reads a
    /// stale position.
    /// </summary>
    private void NoteWidgetWindowsMoved()
    {
        _widgetWindowsMovedVersion++;
    }

    private WidgetGroupDragCandidate? FindWidgetGroupDragCandidateAtPoint(
        string sourceWidgetId,
        int screenX,
        int screenY)
    {
        EnsureWidgetGroupDragCandidateCache(sourceWidgetId);
        RefreshWidgetGroupDragCandidateBoundsCacheIfStale();

        WidgetGroupDragCandidate? best = null;
        long bestArea = long.MaxValue;
        for (int index = 0; index < _groupDragCandidates.Length; index++)
        {
            WidgetGroupDragCandidate candidate = _groupDragCandidates[index];
            if (!candidate.Window.Visible)
            {
                continue;
            }

            // This hit-test runs on every drag frame; each uncached fetch
            // costs a visual-tree transform plus an AppWindow query per
            // candidate. Cache after the first fetch of the current
            // movement version.
            Windows.Graphics.RectInt32? bounds = _groupDragCandidateBounds[index];
            if (!_groupDragCandidateBoundsFetched[index])
            {
                bounds = candidate.Window.GetGroupMergeTitleScreenBounds();
                _groupDragCandidateBounds[index] = bounds;
                _groupDragCandidateBoundsFetched[index] = true;
            }

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

    private void RefreshWidgetGroupDragCandidateBoundsCacheIfStale()
    {
        if (_groupDragCandidateBoundsVersion == _widgetWindowsMovedVersion &&
            _groupDragCandidateBounds.Length == _groupDragCandidates.Length)
        {
            return;
        }

        _groupDragCandidateBounds = new Windows.Graphics.RectInt32?[_groupDragCandidates.Length];
        _groupDragCandidateBoundsFetched = new bool[_groupDragCandidates.Length];
        _groupDragCandidateBoundsVersion = _widgetWindowsMovedVersion;
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
        _groupDragCandidateBounds = [];
        _groupDragCandidateBoundsFetched = [];
        _groupDragCandidateBoundsVersion = -1;
    }

    private readonly record struct WidgetGroupDragCandidate(
        IDesktopWidgetWindow Window,
        WidgetGroupJoinTarget Rule);
}
