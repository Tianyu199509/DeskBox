// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Contracts;
using DeskBox.Platform;
using Windows.Graphics;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private string? _groupDragCandidateSourceId;
    private WidgetGroupDragCandidate[] _groupDragCandidates = [];
    private int _widgetWindowsMovedVersion;
    private int _groupDragCandidateBoundsVersion = -1;
    private RectInt32?[] _groupDragCandidateBounds = [];
    private RectInt32?[] _groupDragCandidateSurfaceBounds = [];
    private double[] _groupDragCandidateDpiScales = [];
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

    private WidgetGroupDragHit? FindWidgetGroupDragCandidateAtPoint(
        string sourceWidgetId,
        int screenX,
        int screenY)
    {
        EnsureWidgetGroupDragCandidateCache(sourceWidgetId);
        RefreshWidgetGroupDragCandidateBoundsCacheIfStale();

        WidgetGroupDragHit? best = null;
        long bestArea = long.MaxValue;
        for (int index = 0; index < _groupDragCandidates.Length; index++)
        {
            WidgetGroupDragCandidate candidate = _groupDragCandidates[index];
            if (!candidate.Window.Visible)
            {
                continue;
            }

            // This hit-test runs on every drag frame; each uncached fetch
            // costs a visual-tree transform plus an AppWindow/DPI query per
            // candidate. Cache after the first fetch of the current
            // movement version.
            if (!_groupDragCandidateBoundsFetched[index])
            {
                RectInt32? fetchedTitle = candidate.Window.GetGroupMergeTitleScreenBounds();
                RectInt32? fetchedSurface = candidate.Window.GetGroupMergeSurfaceScreenBounds();
                double scale = 1.0;
                if (fetchedSurface is { } s)
                {
                    double queried = Win32Helper.GetDpiScaleForPoint(
                        s.X + (s.Width / 2),
                        s.Y + (s.Height / 2));
                    if (double.IsFinite(queried) && queried > 0)
                    {
                        scale = queried;
                    }
                }

                _groupDragCandidateBounds[index] = fetchedTitle;
                _groupDragCandidateSurfaceBounds[index] = fetchedSurface;
                _groupDragCandidateDpiScales[index] = scale;
                _groupDragCandidateBoundsFetched[index] = true;
            }

            RectInt32? surface = _groupDragCandidateSurfaceBounds[index];
            double dpiScale = _groupDragCandidateDpiScales[index];
            int minimumCenterSize = (int)Math.Round(
                WidgetGroupDropHitTestPolicy.CenterZoneMinimumLogicalSize *
                (dpiScale > 0 ? dpiScale : 1.0));
            WidgetGroupDropZone zone = WidgetGroupDropHitTestPolicy.ResolveZone(
                surface,
                _groupDragCandidateBounds[index],
                screenX,
                screenY,
                minimumCenterSize);
            if (zone == WidgetGroupDropZone.None)
            {
                continue;
            }

            // The title fast lane beats a center hit; among equal zones the
            // smallest surface wins so a widget resting on a larger one is
            // still reachable.
            RectInt32 areaBounds = surface ?? _groupDragCandidateBounds[index]!.Value;
            long area = (long)areaBounds.Width * areaBounds.Height;
            bool better = best is not { } current ||
                (zone == WidgetGroupDropZone.Title && current.Zone != WidgetGroupDropZone.Title) ||
                (zone == current.Zone && area < bestArea);
            if (better)
            {
                best = new WidgetGroupDragHit(candidate, zone, surface, dpiScale);
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

        _groupDragCandidateBounds = new RectInt32?[_groupDragCandidates.Length];
        _groupDragCandidateSurfaceBounds = new RectInt32?[_groupDragCandidates.Length];
        _groupDragCandidateDpiScales = new double[_groupDragCandidates.Length];
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
                if (rule.CanJoin)
                {
                    window.PrewarmGroupDropPreview();
                }

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
        _groupDragCandidateSurfaceBounds = [];
        _groupDragCandidateDpiScales = [];
        _groupDragCandidateBoundsFetched = [];
        _groupDragCandidateBoundsVersion = -1;
    }

    private readonly record struct WidgetGroupDragCandidate(
        IDesktopWidgetWindow Window,
        WidgetGroupJoinTarget Rule);

    private readonly record struct WidgetGroupDragHit(
        WidgetGroupDragCandidate Candidate,
        WidgetGroupDropZone Zone,
        RectInt32? SurfaceBounds,
        double DpiScale);
}
