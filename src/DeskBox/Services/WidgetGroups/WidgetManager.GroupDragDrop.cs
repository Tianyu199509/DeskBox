using System.Diagnostics;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Drag-to-group gesture: while a widget is dragged by its title, the widget
/// under the pointer becomes a merge candidate when the pointer rests in its
/// title strip (fast lane) or the inner half of its surface (center zone).
/// A short dwell arms the merge so passing over a widget never merges it;
/// edge snapping is suspended while a candidate is engaged so the two
/// intents never fight; Alt opts a drag out entirely, Esc cancels one
/// target; and every merge offers a one-step undo.
/// </summary>
public sealed partial class WidgetManager
{
    private const int GroupDragVirtualKeyAlt = 0x12;
    private const int GroupDragVirtualKeyEscape = 0x1B;
    private static readonly TimeSpan GroupDragMonitorInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan GroupDragFrameCoalesceWindow = TimeSpan.FromMilliseconds(22);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _groupDragMonitorTimer;
    private string? _groupDragSourceId;
    private double _groupDragAnchorNormX = 0.5;
    private double _groupDragAnchorNormY = 0.06;
    private string? _groupDragTargetId;
    private WidgetGroupDropZone _groupDragZone;
    private bool _groupDragTargetCanJoin;
    private string? _groupDragRejectionKey;
    private long _groupDragDwellStartedTimestamp;
    private long _groupDragLastFrameTimestamp;
    private double _groupDragLastPointerOffsetY = double.NaN;
    private bool _groupDragDropReady;
    private bool _groupDragBlockedShown;
    private bool _groupDragSourceShrunk;
    private string? _groupDragSuppressedTargetId;
    private long _groupMergeUndoGeneration;

    /// <summary>
    /// Prepares candidate bounds/overlays up front when a title drag begins so
    /// crossing onto a target mid-drag never pays lazy XAML inflation.
    /// </summary>
    public void BeginWidgetGroupDragSession(
        string sourceWidgetId,
        double anchorNormalizedX,
        double anchorNormalizedY)
    {
        if (!IsWidgetGroupingEnabled)
        {
            return;
        }

        _groupDragSourceId = sourceWidgetId;
        _groupDragAnchorNormX = Math.Clamp(anchorNormalizedX, 0.05, 0.95);
        _groupDragAnchorNormY = Math.Clamp(anchorNormalizedY, 0.0, 0.95);
        EnsureWidgetGroupDragCandidateCache(sourceWidgetId);
    }

    /// <summary>
    /// Re-evaluates the merge candidate for the current cursor position.
    /// Returns true while a joinable target is engaged, in which case the
    /// caller must suspend edge snapping for this frame.
    /// </summary>
    public bool UpdateWidgetGroupDragPreview(string sourceWidgetId)
    {
        _groupDragSourceId = sourceWidgetId;
        _groupDragLastFrameTimestamp = Stopwatch.GetTimestamp();
        return EvaluateWidgetGroupDragTarget();
    }

    /// <summary>
    /// Checks at pointer release whether the drag is armed to merge into a
    /// target, honoring any last-moment Alt or Esc press.
    /// </summary>
    public bool IsWidgetGroupDragMergeReady(string sourceWidgetId)
    {
        if (!IsWidgetGroupingEnabled ||
            !string.Equals(_groupDragSourceId, sourceWidgetId, StringComparison.Ordinal))
        {
            return false;
        }

        _ = EvaluateWidgetGroupDragTarget();
        return _groupDragDropReady && _groupDragTargetId is not null;
    }

    public async Task<bool> CompleteWidgetGroupDragAsync(
        string sourceWidgetId,
        RectInt32 sourceOriginBounds,
        Action? onNotMergedFallback = null)
    {
        if (!IsWidgetGroupingEnabled)
        {
            ClearGroupDragPreview();
            onNotMergedFallback?.Invoke();
            return false;
        }

        if (string.Equals(_groupDragSourceId, sourceWidgetId, StringComparison.Ordinal))
        {
            _ = EvaluateWidgetGroupDragTarget();
        }

        string? targetId = string.Equals(
                _groupDragSourceId,
                sourceWidgetId,
                StringComparison.Ordinal) &&
            _groupDragDropReady
            ? _groupDragTargetId
            : null;
        if (targetId is null)
        {
            ClearGroupDragPreview();
            onNotMergedFallback?.Invoke();
            return false;
        }

        // Hide the dragged window on frame 0 before clearing the preview so it
        // never flashes back to full size/opacity on top of the target while
        // MergeWidgetsAsync persists and retires it.
        IDesktopWidgetWindow? sourceWindow = GetLoadedWindow(sourceWidgetId);
        _groupDragSourceShrunk = false;
        sourceWindow?.SetGroupDragSourceCommitting(true);
        ClearGroupDragPreview(preserveCommittingSource: true);

        AppSettings settings = _settingsService.Settings;
        bool sourceWasStandalone =
            WidgetGroupSettings.FindByMember(settings, sourceWidgetId) is null;
        bool mergedOk = false;
        try
        {
            mergedOk = await MergeWidgetsAsync(sourceWidgetId, targetId);
        }
        finally
        {
            if (!mergedOk)
            {
                sourceWindow?.SetGroupDragSourceCommitting(false);
                onNotMergedFallback?.Invoke();
            }
            else
            {
                RestoreTemporarilyRaisedWidgetsToDesktopLayer("group-merge-drag-ended");
            }
        }

        if (!mergedOk)
        {
            return false;
        }

        // The merge created a new group surface: commit its landing once
        // so the entry lands under the group surface id as authoritative
        // (spec 5.4 合并建组 entry).
        if (FindConfig(sourceWidgetId) is { } merged &&
            GetLoadedWindow(sourceWidgetId) is { } hostWindow &&
            Win32Helper.GetWindowRect(hostWindow.WindowHandle, out Win32Helper.RECT hostRect))
        {
            var hostBounds = new RectInt32(
                hostRect.Left, hostRect.Top,
                hostRect.Right - hostRect.Left, hostRect.Bottom - hostRect.Top);
            CommitUserPlacement(
                merged,
                hostBounds,
                hostBounds,
                WidgetPlacementSource.GroupMerge,
                captureIntent: false);
        }

        if (sourceWasStandalone)
        {
            OfferWidgetGroupMergeUndo(sourceWidgetId, targetId, sourceOriginBounds);
        }

        return true;
    }

    public void CancelWidgetGroupDrag(string sourceWidgetId)
    {
        if (string.Equals(_groupDragSourceId, sourceWidgetId, StringComparison.Ordinal))
        {
            ClearGroupDragPreview();
        }
    }

    internal SizeInt32 ResolveDetachedMemberPhysicalSize(
        string memberWidgetId,
        double dpiScale,
        SizeInt32 fallbackPhysicalSize) =>
        WidgetGroupStandaloneSizeMemory.ResolveDetachedPhysicalSize(
            FindConfig(memberWidgetId),
            dpiScale,
            fallbackPhysicalSize);

    private bool EvaluateWidgetGroupDragTarget()
    {
        if (_groupDragSourceId is not { } sourceId ||
            !IsWidgetGroupingEnabled ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            ClearGroupDragTargetPreview();
            return false;
        }

        // Alt: "this drag only moves" — no candidate, snapping stays on.
        if (Win32Helper.IsKeyDown(GroupDragVirtualKeyAlt))
        {
            ClearGroupDragTargetPreview();
            return false;
        }

        WidgetGroupDragHit? hit = FindWidgetGroupDragCandidateAtPoint(
            sourceId,
            cursor.X,
            cursor.Y);
        string? targetId = hit?.Candidate.Window.Config.Id;

        // An Esc-cancelled target stays muted until the pointer leaves it.
        if (_groupDragSuppressedTargetId is not null &&
            !string.Equals(targetId, _groupDragSuppressedTargetId, StringComparison.Ordinal))
        {
            _groupDragSuppressedTargetId = null;
        }

        if (targetId is null ||
            string.Equals(targetId, _groupDragSuppressedTargetId, StringComparison.Ordinal))
        {
            ClearGroupDragTargetPreview();
            return false;
        }

        if (string.Equals(targetId, _groupDragTargetId, StringComparison.Ordinal) &&
            Win32Helper.IsKeyDown(GroupDragVirtualKeyEscape))
        {
            _groupDragSuppressedTargetId = targetId;
            ClearGroupDragTargetPreview();
            return false;
        }

        WidgetGroupDragHit resolved = hit!.Value;
        long now = Stopwatch.GetTimestamp();
        bool changed = false;
        if (!string.Equals(targetId, _groupDragTargetId, StringComparison.Ordinal))
        {
            ClearGroupDragTargetPreview();
            _groupDragTargetId = targetId;
            _groupDragZone = resolved.Zone;
            _groupDragTargetCanJoin = resolved.Candidate.Rule.CanJoin;
            _groupDragRejectionKey = resolved.Candidate.Rule.RejectionReasonKey;
            _groupDragDwellStartedTimestamp = now;
            _groupDragLastPointerOffsetY = double.NaN;
            changed = true;
            EnsureGroupDragMonitorTimer();
        }
        else if (resolved.Zone != _groupDragZone)
        {
            _groupDragZone = resolved.Zone;
            if (!_groupDragDropReady)
            {
                _groupDragDwellStartedTimestamp = now;
            }

            changed = true;
        }

        bool dwellElapsed =
            Stopwatch.GetElapsedTime(_groupDragDwellStartedTimestamp, now) >=
            WidgetGroupDropHitTestPolicy.GetDwell(_groupDragZone);
        if (dwellElapsed)
        {
            if (_groupDragTargetCanJoin && !_groupDragDropReady)
            {
                _groupDragDropReady = true;
                changed = true;
            }
            else if (!_groupDragTargetCanJoin && !_groupDragBlockedShown)
            {
                _groupDragBlockedShown = true;
                changed = true;
            }
        }

        double pointerOffsetY = resolved.SurfaceBounds is { } surfaceBounds
            ? (cursor.Y - surfaceBounds.Y) / Math.Max(0.1, resolved.DpiScale)
            : double.NaN;
        bool pointerShiftedSignificantly =
            _groupDragTargetCanJoin &&
            double.IsFinite(pointerOffsetY) &&
            (!double.IsFinite(_groupDragLastPointerOffsetY) ||
             Math.Abs(pointerOffsetY - _groupDragLastPointerOffsetY) >= 4.0);

        if (changed || pointerShiftedSignificantly)
        {
            _groupDragLastPointerOffsetY = pointerOffsetY;
            PushGroupDragTargetPreview(pointerOffsetY);
        }

        SetGroupDragSourceShrunk(_groupDragTargetCanJoin);
        return _groupDragTargetCanJoin;
    }

    private void PushGroupDragTargetPreview(double pointerOffsetY)
    {
        if (_groupDragTargetId is not { } targetId ||
            GetLoadedWindow(targetId) is not { } targetWindow)
        {
            return;
        }

        WidgetGroupDropPreviewZone zone = _groupDragZone == WidgetGroupDropZone.Title
            ? WidgetGroupDropPreviewZone.Title
            : WidgetGroupDropPreviewZone.Center;
        WidgetGroupDropPreviewState state;
        if (!_groupDragTargetCanJoin)
        {
            state = _groupDragBlockedShown
                ? new WidgetGroupDropPreviewState(
                    Visible: true,
                    Ready: false,
                    Blocked: true,
                    zone,
                    TimeSpan.Zero,
                    _localizationService.T(
                        _groupDragRejectionKey ?? "Widget.Group.RequiresVisibleTitle"),
                    double.NaN)
                : WidgetGroupDropPreviewState.Hidden;
        }
        else
        {
            state = new WidgetGroupDropPreviewState(
                Visible: true,
                Ready: _groupDragDropReady,
                Blocked: false,
                zone,
                _groupDragDropReady
                    ? TimeSpan.Zero
                    : WidgetGroupDropHitTestPolicy.GetDwell(_groupDragZone),
                _localizationService.T(
                    _groupDragDropReady ? "Widget.Group.DropReady" : "Widget.Group.DropWaiting"),
                pointerOffsetY);
        }

        targetWindow.SetGroupDropPreview(state);
    }

    /// <summary>
    /// The drag frame loop only runs on pointer motion. While a target is
    /// engaged this light timer keeps the dwell, Alt and Esc responsive when
    /// the user holds the pointer still, skipping ticks already covered by an
    /// active pointer-move frame.
    /// </summary>
    private void EnsureGroupDragMonitorTimer()
    {
        if (_groupDragMonitorTimer is null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = GroupDragMonitorInterval;
            timer.Tick += (_, _) =>
            {
                if (_groupDragTargetId is null)
                {
                    _groupDragMonitorTimer?.Stop();
                    return;
                }

                if (_groupDragLastFrameTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_groupDragLastFrameTimestamp) < GroupDragFrameCoalesceWindow)
                {
                    return;
                }

                _ = EvaluateWidgetGroupDragTarget();
            };
            _groupDragMonitorTimer = timer;
        }

        if (!_groupDragMonitorTimer.IsRunning)
        {
            _groupDragMonitorTimer.Start();
        }
    }

    private void SetGroupDragSourceShrunk(bool shrunk)
    {
        if (_groupDragSourceShrunk == shrunk)
        {
            return;
        }

        _groupDragSourceShrunk = shrunk;
        if (_groupDragSourceId is { } sourceId)
        {
            GetLoadedWindow(sourceId)?.SetGroupDragSourcePreview(
                shrunk,
                _groupDragAnchorNormX,
                _groupDragAnchorNormY);
        }
    }

    private void ClearGroupDragPreview(bool preserveCommittingSource = false)
    {
        ClearGroupDragTargetPreview(preserveCommittingSource);
        _groupDragSourceId = null;
        _groupDragSuppressedTargetId = null;
        _groupDragLastFrameTimestamp = 0;
        ClearWidgetGroupDragCandidateCache();
    }

    private void ClearGroupDragTargetPreview(bool preserveCommittingSource = false)
    {
        _groupDragMonitorTimer?.Stop();
        if (!preserveCommittingSource)
        {
            SetGroupDragSourceShrunk(false);
        }

        if (_groupDragTargetId is not null)
        {
            GetLoadedWindow(_groupDragTargetId)?.SetGroupDropPreview(
                WidgetGroupDropPreviewState.Hidden);
        }

        _groupDragTargetId = null;
        _groupDragZone = WidgetGroupDropZone.None;
        _groupDragTargetCanJoin = false;
        _groupDragRejectionKey = null;
        _groupDragLastPointerOffsetY = double.NaN;
        _groupDragDropReady = false;
        _groupDragBlockedShown = false;
    }

    private void OfferWidgetGroupMergeUndo(
        string sourceWidgetId,
        string targetWidgetId,
        RectInt32 sourceOriginBounds)
    {
        long generation = ++_groupMergeUndoGeneration;
        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: string.Empty,
                Message: _localizationService.T("Widget.Group.MergedToast"),
                Severity: DesktopToastSeverity.Success,
                Priority: DesktopToastPriority.ContextualGuide,
                ActionButtonText: _localizationService.T("Widget.DragHud.Action.Undo"),
                OnActionAsync: () => UndoWidgetGroupMergeAsync(
                    generation,
                    sourceWidgetId,
                    targetWidgetId,
                    sourceOriginBounds),
                DurationMs: DesktopToastHudService.StandardHintDurationMs),
            _settingsService);
    }

    private async Task UndoWidgetGroupMergeAsync(
        long generation,
        string sourceWidgetId,
        string targetWidgetId,
        RectInt32 sourceOriginBounds)
    {
        if (!HasUiThreadAccess())
        {
            _ = await RunOnUiThreadAsync(async () =>
            {
                await UndoWidgetGroupMergeAsync(
                    generation,
                    sourceWidgetId,
                    targetWidgetId,
                    sourceOriginBounds);
                return true;
            });
            return;
        }

        if (generation != _groupMergeUndoGeneration)
        {
            return;
        }

        _groupMergeUndoGeneration++;

        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            sourceWidgetId);
        if (group is null ||
            !group.MemberIds.Contains(targetWidgetId, StringComparer.Ordinal))
        {
            App.Log(
                $"[WidgetGroup] Merge undo skipped; topology changed " +
                $"source={sourceWidgetId} target={targetWidgetId}");
            return;
        }

        if (!await RemoveWidgetFromGroupAsync(
                sourceWidgetId,
                revealStandalone: true,
                new PointInt32(sourceOriginBounds.X, sourceOriginBounds.Y)))
        {
            return;
        }

        if (GetLoadedWindow(sourceWidgetId) is { } window &&
            FindConfig(sourceWidgetId) is { } config &&
            sourceOriginBounds is { Width: > 0, Height: > 0 })
        {
            window.ApplyCoordinatedMoveBoundsFallback(sourceOriginBounds);
            CommitUserPlacement(
                config,
                sourceOriginBounds,
                sourceOriginBounds,
                WidgetPlacementSource.External);
        }
    }
}
