using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskBox.Controls.Search;

public sealed partial class RecommendedAppsGridControl
{
    private void RecommendedAppsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecommendedAppsSurface.MinHeight = Math.Max(
            0,
            e.NewSize.Height - RecommendedAppsScrollViewer.Padding.Top - RecommendedAppsScrollViewer.Padding.Bottom);
    }

    private void RecommendedAppsSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_recIsDragging || (_host?.IsAnyOverlayOpen == true))
        {
            return;
        }

        var point = e.GetCurrentPoint(RecommendedAppsSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool isCtrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (!_isRecommendationEditMode)
        {
            if (!isCtrlPressed)
            {
                ClearRecommendedAppSelection();
                _host?.FocusSearchBox();
                return;
            }

            SetRecommendationEditMode(true);
        }

        StartRecommendationRubberBand(e, isAdditive: isCtrlPressed);
        e.Handled = true;
    }

    private void RecommendedAppsSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isRecRubberBanding)
        {
            return;
        }

        _recRubberBandPointerInViewport = e.GetCurrentPoint(RecommendedAppsScrollViewer).Position;
        UpdateRecommendationRubberBand(e.GetCurrentPoint(RecommendedAppsSurface).Position);
        e.Handled = true;
    }

    private void RecommendedAppsSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isRecRubberBanding)
        {
            return;
        }

        EndRecommendationRubberBand();
        e.Handled = true;
    }

    private void RecommendedAppsSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isRecRubberBanding)
        {
            EndRecommendationRubberBand();
        }
    }

    private void StartRecommendationRubberBand(PointerRoutedEventArgs e, bool isAdditive)
    {
        ClearHoveredRecommendedCard();
        _isRecRubberBanding = true;
        _recRubberBandBaseKeys.Clear();
        _recRubberBandKnownTileRects.Clear();
        if (isAdditive)
        {
            _recRubberBandBaseKeys.UnionWith(_recMultiSelectedKeys);
        }
        else if (_recMultiSelectedKeys.Count > 0)
        {
            _recMultiSelectedKeys.Clear();
            RefreshAllRecommendedCardEditVisuals();
            NotifyEditChromeChanged();
        }

        Point pos = e.GetCurrentPoint(RecommendedAppsSurface).Position;
        _recRubberBandStart = pos;
        _recRubberBandCurrent = pos;
        _recRubberBandPointerInViewport = e.GetCurrentPoint(RecommendedAppsScrollViewer).Position;
        UpdateRecommendationRubberBandRect(pos, pos);
        RecRubberBandRect.Visibility = Visibility.Visible;
        RecommendedAppsSurface.CapturePointer(e.Pointer);
        EnsureRecommendationRubberBandAutoScrollTimer();
        _recRubberBandAutoScrollTimer!.Start();
    }

    private void UpdateRecommendationRubberBand(Point currentInSurface)
    {
        _recRubberBandCurrent = currentInSurface;
        UpdateRecommendationRubberBandRect(_recRubberBandStart, currentInSurface);

        var rubberRect = new Rect(
            Math.Min(_recRubberBandStart.X, currentInSurface.X),
            Math.Min(_recRubberBandStart.Y, currentInSurface.Y),
            Math.Abs(currentInSurface.X - _recRubberBandStart.X),
            Math.Abs(currentInSurface.Y - _recRubberBandStart.Y));

        if (rubberRect.Width < 3 && rubberRect.Height < 3)
        {
            return;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is not FrameworkElement tile ||
                tile.Visibility != Visibility.Visible ||
                tile.ActualWidth <= 0 ||
                tile.ActualHeight <= 0 ||
                tile.DataContext is not RecommendedGridEntry entry)
            {
                continue;
            }

            var tileRect = new Rect(
                tile.TransformToVisual(RecommendedAppsSurface).TransformPoint(new Point(0, 0)),
                new Size(tile.ActualWidth, tile.ActualHeight));
            _recRubberBandKnownTileRects[entry.EntryKey] = tileRect;
        }

        _recMultiSelectedKeys.Clear();
        _recMultiSelectedKeys.UnionWith(_recRubberBandBaseKeys);

        foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
        {
            if (_recRubberBandKnownTileRects.TryGetValue(entry.EntryKey, out Rect tileRect) &&
                SearchRecommendationLayoutEngine.Intersects(rubberRect, tileRect))
            {
                _recMultiSelectedKeys.Add(entry.EntryKey);
            }
        }

        RefreshAllRecommendedCardEditVisuals();
        NotifyEditChromeChanged();
    }

    private void UpdateRecommendationRubberBandRect(Point start, Point end)
    {
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        RecRubberBandRect.Width = Math.Abs(end.X - start.X);
        RecRubberBandRect.Height = Math.Abs(end.Y - start.Y);
        RecRubberBandTransform.X = x;
        RecRubberBandTransform.Y = y;
    }

    private void EndRecommendationRubberBand()
    {
        if (!_isRecRubberBanding && RecRubberBandRect.Visibility != Visibility.Visible)
        {
            return;
        }

        _isRecRubberBanding = false;
        _recRubberBandAutoScrollTimer?.Stop();
        _recRubberBandBaseKeys.Clear();
        _recRubberBandKnownTileRects.Clear();
        RecRubberBandRect.Visibility = Visibility.Collapsed;
        RecommendedAppsSurface.ReleasePointerCaptures();
    }

    private void EnsureRecommendationRubberBandAutoScrollTimer()
    {
        if (_recRubberBandAutoScrollTimer is not null)
        {
            return;
        }

        _recRubberBandAutoScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };
        _recRubberBandAutoScrollTimer.Tick += OnRecommendationRubberBandAutoScrollTick;
    }

    private void OnRecommendationRubberBandAutoScrollTick(object? sender, object e)
    {
        if (!_isRecRubberBanding)
        {
            _recRubberBandAutoScrollTimer?.Stop();
            return;
        }

        double delta = SearchResultSelectionPolicy.GetAutoScrollDelta(
            _recRubberBandPointerInViewport.Y,
            RecommendedAppsScrollViewer.ViewportHeight);
        if (Math.Abs(delta) < 0.1)
        {
            return;
        }

        double previousOffset = RecommendedAppsScrollViewer.VerticalOffset;
        double targetOffset = Math.Clamp(
            previousOffset + delta,
            0,
            RecommendedAppsScrollViewer.ScrollableHeight);
        if (Math.Abs(targetOffset - previousOffset) < 0.1)
        {
            return;
        }

        RecommendedAppsScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
        _recRubberBandCurrent = new Point(
            _recRubberBandCurrent.X,
            _recRubberBandCurrent.Y + targetOffset - previousOffset);
        UpdateRecommendationRubberBand(_recRubberBandCurrent);
    }

    private void RecommendedApp_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid card || card.DataContext is not RecommendedGridEntry entry)
        {
            return;
        }

        if (SearchRecommendationLayoutEngine.FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is
            { Name: "CardQuickHideButton" })
        {
            return;
        }

        var pointInPanel = e.GetCurrentPoint(RecommendedAppsPanel);
        if (!pointInPanel.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _recWasCtrlPressedOnDown = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        _recAutoEnteredEditModeOnDown = false;

        if (!_isRecommendationEditMode && _recWasCtrlPressedOnDown)
        {
            _recAutoEnteredEditModeOnDown = true;
            SetRecommendationEditMode(true);
            _recMultiSelectedKeys.Add(entry.EntryKey);
            RefreshAllRecommendedCardEditVisuals();
            NotifyEditChromeChanged();
        }
        else if (!_isRecommendationEditMode)
        {
            SelectRecommendedEntry(card, entry);
        }

        _recPointerCard = card;
        _recPressedEntry = entry;
        _recPointerStartInPanel = pointInPanel.Position;
        _recIsDragging = false;
        _recExternalDragAttempted = false;
        card.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void RecommendedApp_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_recPressedEntry is null ||
            _recPointerCard is null ||
            !ReferenceEquals(sender, _recPointerCard))
        {
            return;
        }

        var pointInPanel = e.GetCurrentPoint(RecommendedAppsPanel);
        if (!pointInPanel.Properties.IsLeftButtonPressed)
        {
            CancelRecommendedDrag();
            return;
        }

        double dx = pointInPanel.Position.X - _recPointerStartInPanel.X;
        double dy = pointInPanel.Position.Y - _recPointerStartInPanel.Y;
        if (!_recIsDragging)
        {
            if ((dx * dx) + (dy * dy) < SearchRecommendationLayoutEngine.RecommendationDragThreshold * SearchRecommendationLayoutEngine.RecommendationDragThreshold)
            {
                return;
            }

            ClearHoveredRecommendedCard();
            ResetEditFolderDoubleClickTracking();
            _recPointerCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            _recIsDragging = true;
            PrepareRecommendedDragPayload(_recPressedEntry);
            // Freeze the grid geometry this gesture decides against: every
            // later decision and reflow projection anchors to these rects,
            // immune to the translations the preview itself applies.
            SnapshotRecommendedTileBaseRects();
            ResetRecommendedDropAnchor();
            App.Log(
                $"[RecDrag] start mode={(_isRecommendationEditMode ? "edit" : "normal")} " +
                $"dragged={_recDraggedEntryKeys.Count} apps={_recDraggedAppKeys.Count} " +
                $"selection={_recMultiSelectedKeys.Count}");
            SetDraggedRecommendedTilesOpacity(0.36);

            RecDragGhostTitle.Text = _recPressedEntry.Label;
            if (_recPressedEntry.IsFolder)
            {
                RecDragGhostImage.Source = null;
                RecDragGhostImage.Visibility = Visibility.Collapsed;
                RecDragGhostFolderGlyph.Visibility = Visibility.Visible;
            }
            else
            {
                ImageSource? ghostIcon = _recPressedEntry.AppItem?.Icon;
                RecDragGhostImage.Source = ghostIcon;
                RecDragGhostImage.Visibility = ghostIcon is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RecDragGhostFolderGlyph.Visibility = ghostIcon is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (_recDraggedEntryKeys.Count > 1)
            {
                RecDragGhostCountText.Text = _recDraggedEntryKeys.Count.ToString();
                RecDragGhostCountBadge.Visibility = Visibility.Visible;
            }
            else
            {
                RecDragGhostCountBadge.Visibility = Visibility.Collapsed;
            }

            RecDragGhostBorder.Visibility = Visibility.Visible;
            SearchRecommendationLayoutEngine.AnimateDragGhostPickup(
                RecDragGhostBorder,
                WindowsCompatibilityService.ShouldAnimate);
        }

        Point pointInRoot = e.GetCurrentPoint(GetPopupBoundsRoot()).Position;
        double rootWidth = GetPopupBoundsRoot().ActualWidth;
        double rootHeight = GetPopupBoundsRoot().ActualHeight;
        if (SearchRecommendationLayoutEngine.IsPointLeavingPopupDragBounds(pointInRoot, rootWidth, rootHeight))
        {
            if (!_recExternalDragAttempted)
            {
                _recExternalDragAttempted = true;
                Grid dragCard = _recPointerCard;
                List<string> candidatePaths = CollectDraggedRecommendationFilePaths(_recPressedEntry);
                if (_host?.TryStartRecommendedExternalDragOut(
                        dragCard,
                        e,
                        candidatePaths,
                        "recommend-grid",
                        SuspendRecommendedDragForExternalHandoff) == true)
                {
                    e.Handled = true;
                    return;
                }
            }

            if (pointInRoot.X < 0 ||
                pointInRoot.Y < 0 ||
                pointInRoot.X > rootWidth ||
                pointInRoot.Y > rootHeight)
            {
                RecDragGhostTransform.X = pointInPanel.Position.X - (RecDragGhostBorder.Width / 2);
                RecDragGhostTransform.Y = pointInPanel.Position.Y - (RecDragGhostBorder.Height / 2);
                ResetRecommendedDropAnchor();
                ClearRecommendedReflowPreview();
                HideRecommendedDropVisuals();
                e.Handled = true;
                return;
            }
        }
        else
        {
            _recExternalDragAttempted = false;
        }

        RecDragGhostTransform.X = pointInPanel.Position.X - (RecDragGhostBorder.Width / 2);
        RecDragGhostTransform.Y = pointInPanel.Position.Y - (RecDragGhostBorder.Height / 2);

        SearchRecommendationLayoutEngine.AutoScrollScrollViewerOnDrag(
            RecommendedAppsScrollViewer,
            e.GetCurrentPoint(RecommendedAppsScrollViewer).Position);

        Point pointInSurface = e.GetCurrentPoint(RecommendedAppsSurface).Position;
        RecommendedDropDecision decision = ResolveRecommendedDropDecision(pointInSurface);
        bool reflowActive = ApplyRecommendedReflowPreview(decision);
        ApplyRecommendedDropVisuals(decision, suppressInsertBar: reflowActive);
        e.Handled = true;
    }

    private List<string> CollectDraggedRecommendationFilePaths(RecommendedGridEntry pressedEntry)
    {
        var paths = new List<string>();
        if (_recDraggedEntryKeys.Count > 0)
        {
            foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
            {
                if (!_recDraggedEntryKeys.Contains(entry.EntryKey))
                {
                    continue;
                }

                AppendEntryFilePaths(entry, paths);
            }
        }

        if (paths.Count == 0)
        {
            AppendEntryFilePaths(pressedEntry, paths);
        }

        return paths;
    }

    private static void AppendEntryFilePaths(RecommendedGridEntry entry, List<string> target)
    {
        if (entry.IsFolder)
        {
            foreach (SearchResultItem member in entry.FolderItems)
            {
                if (!string.IsNullOrWhiteSpace(member.DetailPath))
                {
                    target.Add(member.DetailPath);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.AppItem?.DetailPath))
        {
            target.Add(entry.AppItem.DetailPath);
        }
    }

    private void PrepareRecommendedDragPayload(RecommendedGridEntry pressedEntry)
    {
        _recDraggedAppKeys.Clear();
        _recDraggedEntryKeys.Clear();

        if (_isRecommendationEditMode)
        {
            bool pressedWasSelected = _recMultiSelectedKeys.Contains(pressedEntry.EntryKey);
            if (!pressedWasSelected && _recWasCtrlPressedOnDown)
            {
                _recMultiSelectedKeys.Add(pressedEntry.EntryKey);
                pressedWasSelected = true;
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            }
            else if (!pressedWasSelected && _recMultiSelectedKeys.Count > 0)
            {
                // Dragging an unselected tile without Ctrl acts on just that
                // tile instead of leaving a sticky checkbox selection behind.
                _recMultiSelectedKeys.Clear();
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            }

            if (pressedWasSelected && _recMultiSelectedKeys.Count > 0)
            {
                foreach (RecommendedGridEntry entry in _viewModel.RecommendedEntries)
                {
                    if (!_recMultiSelectedKeys.Contains(entry.EntryKey))
                    {
                        continue;
                    }

                    _recDraggedEntryKeys.Add(entry.EntryKey);
                    if (!entry.IsFolder && entry.AppItem is { } selectedApp)
                    {
                        _recDraggedAppKeys.Add(Helpers.AppRecommendationIdentity.GetCanonicalKey(selectedApp));
                    }
                }

                if (_recDraggedEntryKeys.Count > 0)
                {
                    return;
                }
            }
        }

        if (!pressedEntry.IsFolder && pressedEntry.AppItem is { } singleApp)
        {
            _recDraggedAppKeys.Add(Helpers.AppRecommendationIdentity.GetCanonicalKey(singleApp));
        }

        _recDraggedEntryKeys.Add(pressedEntry.EntryKey);
    }

    private void SetDraggedRecommendedTilesOpacity(double opacity)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, i) is FrameworkElement tile &&
                tile.DataContext is RecommendedGridEntry entry &&
                _recDraggedEntryKeys.Contains(entry.EntryKey))
            {
                tile.Opacity = opacity;
            }
        }
    }

    private void RecommendedApp_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_recPressedEntry is not { } pressedEntry ||
            _recPointerCard is not { } pressedCard ||
            !ReferenceEquals(sender, pressedCard))
        {
            return;
        }

        // Snapshot all gesture state BEFORE ReleasePointerCapture: releasing
        // capture synchronously raises RecommendedApp_PointerCaptureLost ->
        // CancelRecommendedDrag(), which clears _recLastDropDecision,
        // _recDraggedAppKeys, and _recDraggedEntryKeys.
        bool wasDragging = _recIsDragging;
        bool autoEnteredOnDown = _recAutoEnteredEditModeOnDown;
        RecommendedDropDecision decision = _recLastDropDecision;
        string? lastDecisionSummary = _recLastDecisionSummary;
        List<string> draggedApps = wasDragging ? _recDraggedAppKeys.ToList() : [];
        List<string> draggedEntries = wasDragging ? _recDraggedEntryKeys.ToList() : [];
        Point pointInCard = e.GetCurrentPoint(pressedCard).Position;
        var cardBounds = new Rect(0, 0, pressedCard.ActualWidth, pressedCard.ActualHeight);
        pressedCard.ReleasePointerCapture(e.Pointer);

        if (wasDragging)
        {
            // Commit the anchored decision the pointer last owned — a fresh
            // evaluation here could disagree with the preview the user just
            // acted on.
            CancelRecommendedDrag();

            if (decision.IsMerge &&
                decision.MergeTarget is { } mergeTarget &&
                draggedApps.Count > 0)
            {
                _recMultiSelectedKeys.Clear();
                string? mergeFolderId = _viewModel.MergeRecommendationsIntoFolder(
                    draggedApps,
                    mergeTarget.EntryKey);
                App.Log(
                    $"[RecDrag] release merge target={mergeTarget.EntryKey} " +
                    $"apps={draggedApps.Count} folderId={(mergeFolderId is null ? "null" : "ok")}");
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            }
            else if (decision.ReorderTargetIndex >= 0)
            {
                bool moved;
                if (draggedEntries.Count > 1)
                {
                    moved = _viewModel.MoveRecommendationEntries(
                        draggedEntries,
                        decision.ReorderTargetIndex);
                }
                else
                {
                    moved = _viewModel.MoveRecommendationEntry(
                        pressedEntry.EntryKey,
                        decision.ReorderTargetIndex);
                }

                App.Log(
                    $"[RecDrag] release reorder index={decision.ReorderTargetIndex} " +
                    $"entries={draggedEntries.Count} moved={moved}");
                RefreshAllRecommendedCardEditVisuals();
                NotifyEditChromeChanged();
            }
            else
            {
                App.Log(
                    $"[RecDrag] release none entries={draggedEntries.Count} " +
                    $"summary={lastDecisionSummary ?? "none"}");
            }

            e.Handled = true;
            return;
        }

        CancelRecommendedDrag();
        if (cardBounds.Contains(pointInCard))
        {
            if (_isRecommendationEditMode)
            {
                if (!autoEnteredOnDown)
                {
                    long nowMs = Environment.TickCount64;
                    if (pressedEntry.IsFolder &&
                        string.Equals(_lastEditFolderClickKey, pressedEntry.EntryKey, StringComparison.OrdinalIgnoreCase) &&
                        nowMs - _lastEditFolderClickTickMs <= EditFolderDoubleClickThresholdMs)
                    {
                        bool restoreSelected = _lastEditFolderSelectedBeforeClick;
                        ResetEditFolderDoubleClickTracking();
                        if (restoreSelected)
                        {
                            _recMultiSelectedKeys.Add(pressedEntry.EntryKey);
                        }
                        else
                        {
                            _recMultiSelectedKeys.Remove(pressedEntry.EntryKey);
                        }

                        RefreshAllRecommendedCardEditVisuals();
                        NotifyEditChromeChanged();
                        _host?.OpenFolderOverlay(pressedEntry);
                    }
                    else
                    {
                        bool wasSelectedBeforeClick = _recMultiSelectedKeys.Contains(pressedEntry.EntryKey);
                        if (!wasSelectedBeforeClick)
                        {
                            _recMultiSelectedKeys.Add(pressedEntry.EntryKey);
                        }
                        else
                        {
                            _recMultiSelectedKeys.Remove(pressedEntry.EntryKey);
                        }

                        if (pressedEntry.IsFolder)
                        {
                            _lastEditFolderClickKey = pressedEntry.EntryKey;
                            _lastEditFolderClickTickMs = nowMs;
                            _lastEditFolderSelectedBeforeClick = wasSelectedBeforeClick;
                        }
                        else
                        {
                            ResetEditFolderDoubleClickTracking();
                        }

                        RefreshAllRecommendedCardEditVisuals();
                        NotifyEditChromeChanged();
                    }
                }
            }
            else if (pressedEntry.IsFolder)
            {
                _host?.OpenFolderOverlay(pressedEntry);
            }
            else if (pressedEntry.AppItem is { } appItem)
            {
                _viewModel.ExecuteItem(appItem);
            }
        }
        else
        {
            ClearHoveredRecommendedCard();
        }

        e.Handled = true;
    }

    private void RecommendedApp_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_recPointerCard is not null && ReferenceEquals(sender, _recPointerCard))
        {
            CancelRecommendedDrag();
        }
    }

    private void RecommendedApp_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_recIsDragging || _isRecRubberBanding || (_host?.IsDragOutInProgress == true) || sender is not Grid card)
        {
            return;
        }

        if (_hoveredAppCard is not null && !ReferenceEquals(_hoveredAppCard, card))
        {
            _hoveredAppCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
        }

        _hoveredAppCard = card;
        card.Background = ResolveLocalThemeBrush("SubtleFillColorSecondaryBrush");

        if (_isRecommendationEditMode && card.DataContext is RecommendedGridEntry editEntry)
        {
            ApplyRecommendedCardEditVisuals(card, editEntry, isPointerOver: true);
        }
    }

    private void RecommendedApp_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid card)
        {
            return;
        }

        if (ReferenceEquals(_hoveredAppCard, card))
        {
            _hoveredAppCard = null;
        }

        card.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;

        if (_isRecommendationEditMode)
        {
            ApplyRecommendedCardEditVisuals(
                card,
                card.DataContext as RecommendedGridEntry,
                isPointerOver: false);
        }
    }

    private void SuspendRecommendedDragForExternalHandoff()
    {
        SetDraggedRecommendedTilesOpacity(1.0);
        if (_recPointerCard is not null)
        {
            _recPointerCard.Opacity = 1;
            _recPointerCard.Background = SearchRecommendationLayoutEngine.TransparentCardBrush;
            _recPointerCard = null;
        }

        _recPressedEntry = null;
        _recIsDragging = false;
        _recExternalDragAttempted = false;
        _recWasCtrlPressedOnDown = false;
        _recAutoEnteredEditModeOnDown = false;
        _recDraggedAppKeys.Clear();
        _recDraggedEntryKeys.Clear();
        ClearRecommendedReflowPreview();
        _recTileBaseRects.Clear();
        ResetRecommendedDropAnchor();
        RecDragGhostBorder.Visibility = Visibility.Collapsed;
        RecDragGhostCountBadge.Visibility = Visibility.Collapsed;
        RecDragGhostImage.Source = null;
        HideRecommendedDropVisuals();
    }

    private void CancelRecommendedDrag()
    {
        SetDraggedRecommendedTilesOpacity(1.0);
        if (_recPointerCard is not null)
        {
            _recPointerCard.Opacity = 1;
            _recPointerCard.ReleasePointerCaptures();
            _recPointerCard = null;
        }

        _recPressedEntry = null;
        _recIsDragging = false;
        _recExternalDragAttempted = false;
        _recWasCtrlPressedOnDown = false;
        _recAutoEnteredEditModeOnDown = false;
        _recDraggedAppKeys.Clear();
        _recDraggedEntryKeys.Clear();
        // Unwinds the live-reflow translations with the same curve the commit
        // path relies on: for a committed drop they cancel against the layout
        // Offset animation started by the collection change in the same
        // frame; for a cancelled drag they simply close the gap.
        ClearRecommendedReflowPreview();
        _recTileBaseRects.Clear();
        ResetRecommendedDropAnchor();
        RecDragGhostBorder.Visibility = Visibility.Collapsed;
        RecDragGhostCountBadge.Visibility = Visibility.Collapsed;
        RecDragGhostImage.Source = null;
        HideRecommendedDropVisuals();
        _host?.CancelOverlayDrag();
    }

    private void HideRecommendedDropVisuals()
    {
        SetHoveredMergeTargetCard(null);
        RecMergeTargetHighlight.Visibility = Visibility.Collapsed;
        RecDropInsertIndicator.Visibility = Visibility.Collapsed;
    }

    private void ApplyRecommendedDropVisuals(
        RecommendedDropDecision decision,
        bool suppressInsertBar = false)
    {
        if (decision.IsMerge && decision.MergeTarget is not null)
        {
            SetHoveredMergeTargetCard(decision.MergeTargetElement);
            if (RecDropInsertIndicator.Visibility != Visibility.Collapsed)
            {
                RecDropInsertIndicator.Visibility = Visibility.Collapsed;
            }
            if (Math.Abs(RecMergeTargetHighlight.Width - decision.HighlightRect.Width) > 0.01)
            {
                RecMergeTargetHighlight.Width = decision.HighlightRect.Width;
            }
            if (Math.Abs(RecMergeTargetHighlight.Height - decision.HighlightRect.Height) > 0.01)
            {
                RecMergeTargetHighlight.Height = decision.HighlightRect.Height;
            }
            if (Math.Abs(RecMergeTargetTransform.X - decision.HighlightRect.X) > 0.01)
            {
                RecMergeTargetTransform.X = decision.HighlightRect.X;
            }
            if (Math.Abs(RecMergeTargetTransform.Y - decision.HighlightRect.Y) > 0.01)
            {
                RecMergeTargetTransform.Y = decision.HighlightRect.Y;
            }
            if (RecMergeTargetHighlight.Visibility != Visibility.Visible)
            {
                RecMergeTargetHighlight.Visibility = Visibility.Visible;
            }
            return;
        }

        SetHoveredMergeTargetCard(null);
        if (RecMergeTargetHighlight.Visibility != Visibility.Collapsed)
        {
            RecMergeTargetHighlight.Visibility = Visibility.Collapsed;
        }
        // When the live reflow preview is active the opened gap IS the
        // affordance; the insert bar only shows as the reduced-motion
        // fallback.
        if (decision.ReorderTargetIndex >= 0 && !suppressInsertBar)
        {
            if (Math.Abs(RecDropInsertTransform.X - decision.InsertBarPosition.X) > 0.01)
            {
                RecDropInsertTransform.X = decision.InsertBarPosition.X;
            }
            if (Math.Abs(RecDropInsertTransform.Y - decision.InsertBarPosition.Y) > 0.01)
            {
                RecDropInsertTransform.Y = decision.InsertBarPosition.Y;
            }
            if (RecDropInsertIndicator.Visibility != Visibility.Visible)
            {
                RecDropInsertIndicator.Visibility = Visibility.Visible;
            }
        }
        else if (RecDropInsertIndicator.Visibility != Visibility.Collapsed)
        {
            RecDropInsertIndicator.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Live reflow preview: projects the order a drop would commit and shifts
    /// tiles to their projected slots via Translation animations, so the grid
    /// opens the landing gap while the pointer is still down. The dragged
    /// tiles move to their projected slot too (still dimmed) — the ghost
    /// stays the pointer representation. Returns whether the preview engaged
    /// (used to suppress the static insert bar).
    /// </summary>
    private bool ApplyRecommendedReflowPreview(RecommendedDropDecision decision)
    {
        if (!WindowsCompatibilityService.ShouldAnimate ||
            decision.IsMerge ||
            decision.ReorderTargetIndex < 0)
        {
            ClearRecommendedReflowPreview();
            return false;
        }

        var entries = _viewModel.RecommendedEntries;
        int realizedChildCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        if (_recHasCachedReflowState &&
            _recCachedReflowTargetIndex == decision.ReorderTargetIndex &&
            _recCachedReflowRealizedCount == realizedChildCount &&
            _recCachedReflowEntriesCount == entries.Count)
        {
            return _recCachedReflowActive;
        }
        // EvaluateRecommendedDropTarget reports the remaining-list index for
        // single drags but the full-list slot for batch drags (the two Move
        // overloads' conventions); normalize to the remaining-list index.
        int insertIndex = decision.ReorderTargetIndex;
        if (_recDraggedEntryKeys.Count > 1)
        {
            int slot = Math.Clamp(decision.ReorderTargetIndex, 0, entries.Count);
            int draggedBeforeSlot = 0;
            for (int i = 0; i < slot; i++)
            {
                if (_recDraggedEntryKeys.Contains(entries[i].EntryKey))
                {
                    draggedBeforeSlot++;
                }
            }

            insertIndex = slot - draggedBeforeSlot;
        }

        List<string> projected = SearchRecommendationLayoutEngine.ProjectReorderedEntryKeys(
            entries,
            _recDraggedEntryKeys,
            insertIndex);
        if (projected.Count != entries.Count)
        {
            ClearRecommendedReflowPreview();
            return false;
        }

        var projectedSlotByKey = new Dictionary<string, int>(
            projected.Count,
            StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < projected.Count; i++)
        {
            projectedSlotByKey[projected[i]] = i;
        }

        var tileBases = CollectReflowTileBases();
        if (tileBases.Count == 0 ||
            !TryResolveReflowGridMetrics(
                tileBases,
                out double pitchX,
                out double pitchY,
                out int capacity,
                out Point origin))
        {
            ClearRecommendedReflowPreview();
            return false;
        }

        var desired = new Dictionary<string, (FrameworkElement Tile, System.Numerics.Vector3 Delta)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, tile, baseRect) in tileBases)
        {
            if (!projectedSlotByKey.TryGetValue(key, out int slot))
            {
                continue;
            }

            double targetX = origin.X + ((capacity > 0 ? slot % capacity : 0) * pitchX);
            double targetY = origin.Y + ((capacity > 0 ? slot / capacity : 0) * pitchY);
            double dx = targetX - baseRect.X;
            double dy = targetY - baseRect.Y;
            if (Math.Abs(dx) >= 0.5 || Math.Abs(dy) >= 0.5)
            {
                desired[key] = (tile, new System.Numerics.Vector3((float)dx, (float)dy, 0f));
            }
        }

        foreach (var (key, shift) in desired)
        {
            if (_recReflowTranslations.TryGetValue(key, out System.Numerics.Vector3 applied) &&
                Math.Abs(applied.X - shift.Delta.X) < 0.5 &&
                Math.Abs(applied.Y - shift.Delta.Y) < 0.5)
            {
                continue;
            }

            AnimateReflowTranslation(shift.Tile, shift.Delta);
            _recReflowTranslations[key] = shift.Delta;
        }

        foreach (string key in _recReflowTranslations.Keys.ToList())
        {
            if (desired.ContainsKey(key))
            {
                continue;
            }

            // Tile no longer needs a shift; unwind it.
            foreach (var (staleKey, staleTile, _) in tileBases)
            {
                if (string.Equals(staleKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    AnimateReflowTranslation(staleTile, System.Numerics.Vector3.Zero);
                    break;
                }
            }

            _recReflowTranslations.Remove(key);
        }

        _recHasCachedReflowState = true;
        _recCachedReflowActive = _recReflowTranslations.Count > 0;
        _recCachedReflowTargetIndex = decision.ReorderTargetIndex;
        _recCachedReflowRealizedCount = realizedChildCount;
        _recCachedReflowEntriesCount = entries.Count;
        return _recCachedReflowActive;
    }

    private void ClearRecommendedReflowPreview()
    {
        _recHasCachedReflowState = false;
        _recCachedReflowActive = false;
        _recCachedReflowTargetIndex = -1;
        _recCachedReflowRealizedCount = -1;
        _recCachedReflowEntriesCount = -1;
        if (_recReflowTranslations.Count == 0)
        {
            return;
        }

        foreach (var (key, tile, _) in CollectReflowTileBases())
        {
            if (_recReflowTranslations.ContainsKey(key))
            {
                AnimateReflowTranslation(tile, System.Numerics.Vector3.Zero);
            }
        }

        _recReflowTranslations.Clear();
    }

    private List<(string Key, FrameworkElement Tile, Rect Base)> CollectReflowTileBases()
    {
        var tileBases = new List<(string, FrameworkElement, Rect)>();
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int c = 0; c < childCount; c++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, c) is not FrameworkElement tile ||
                tile.Visibility != Visibility.Visible ||
                tile.ActualWidth <= 0 ||
                tile.ActualHeight <= 0 ||
                tile.DataContext is not RecommendedGridEntry tileEntry)
            {
                continue;
            }

            tileBases.Add((tileEntry.EntryKey, tile, GetTileBaseRect(tile, tileEntry.EntryKey)));
        }

        return tileBases;
    }

    private static bool TryResolveReflowGridMetrics(
        List<(string Key, FrameworkElement Tile, Rect Base)> tileBases,
        out double pitchX,
        out double pitchY,
        out int capacity,
        out Point origin)
    {
        pitchX = 0;
        pitchY = 0;
        capacity = 0;
        origin = new Point(double.PositiveInfinity, double.PositiveInfinity);

        if (tileBases.Count == 0)
        {
            return false;
        }

        // Rows keyed by Y rounded to 0.1px (sub-pixel drift tolerance).
        var rows = new SortedDictionary<double, List<(double X, double Y, Size Size)>>();
        foreach (var (_, _, baseRect) in tileBases)
        {
            double rowKey = Math.Round(baseRect.Y, 1);
            if (!rows.TryGetValue(rowKey, out var rowTiles))
            {
                rowTiles = [];
                rows[rowKey] = rowTiles;
            }

            rowTiles.Add((baseRect.X, baseRect.Y, new Size(baseRect.Width, baseRect.Height)));
            origin.X = Math.Min(origin.X, baseRect.X);
            origin.Y = Math.Min(origin.Y, baseRect.Y);
        }

        capacity = rows.Values.Max(static row => row.Count);
        List<(double X, double Y, Size Size)> firstRow =
            rows.First().Value.OrderBy(static tile => tile.X).ToList();
        Size cellSize = firstRow[0].Size;
        // Fallback pitch uses the UniformGridLayout 6px spacing declared in
        // the XAML (single-column rows / single-row grids have no neighbour
        // to measure from).
        pitchX = firstRow.Count >= 2
            ? firstRow[1].X - firstRow[0].X
            : cellSize.Width + 6;
        pitchY = rows.Count >= 2
            ? rows.Keys.Skip(1).First() - rows.Keys.First()
            : cellSize.Height + 6;
        return pitchX > 0 && pitchY > 0 && capacity > 0;
    }

    private static void AnimateReflowTranslation(
        FrameworkElement tile,
        System.Numerics.Vector3 target)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(tile, true);
        var visual = ElementCompositionPreview.GetElementVisual(tile);
        if (visual is null)
        {
            return;
        }

        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            visual.StopAnimation("Translation");
            // Translation is animatable but not a settable CLR property in
            // the WinUI 3 projection; it lives in the property store.
            visual.Properties.InsertVector3("Translation", target);
            return;
        }

        // Same curve and duration as the implicit Offset animation: when the
        // commit reorders the collection, the layout Offset animation and
        // this translation unwind cancel each other — no visual jump between
        // preview and settled state.
        var compositor = visual.Compositor;
        var decel = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.1f, 1.0f));
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.Target = "Translation";
        anim.InsertKeyFrame(1.0f, target, decel);
        anim.Duration = TimeSpan.FromMilliseconds(190);
        visual.StartAnimation("Translation", anim);
    }

    private void SetHoveredMergeTargetCard(FrameworkElement? targetCard)
    {
        if (ReferenceEquals(_recMergeHoveredCard, targetCard))
        {
            return;
        }

        if (_recMergeHoveredCard is { } previousCard &&
            SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(previousCard, "CardContentHost") is { } prevHost)
        {
            SearchRecommendationLayoutEngine.AnimateMergeTargetScale(prevHost, 1.0f, 120, WindowsCompatibilityService.ShouldAnimate);
        }

        _recMergeHoveredCard = targetCard;
        if (targetCard is not null &&
            SearchRecommendationLayoutEngine.FindVisualChild<FrameworkElement>(targetCard, "CardContentHost") is { } nextHost)
        {
            SearchRecommendationLayoutEngine.AnimateMergeTargetScale(nextHost, 1.07f, 140, WindowsCompatibilityService.ShouldAnimate);
        }
    }


    private Rect GetTileBaseRect(FrameworkElement tile, string entryKey)
    {
        if (_recTileBaseRects.TryGetValue(entryKey, out Rect cached))
        {
            return cached;
        }

        Point position = tile.TransformToVisual(RecommendedAppsSurface)
            .TransformPoint(new Point(0, 0));
        if (_recReflowTranslations.TryGetValue(entryKey, out System.Numerics.Vector3 applied))
        {
            position.X -= applied.X;
            position.Y -= applied.Y;
        }

        // A gesture starting within the previous gesture's 190ms unwind can
        // capture a residual offset here; it decays to zero by the next
        // snapshot and never accumulates.
        var rect = new Rect(position, new Size(tile.ActualWidth, tile.ActualHeight));
        _recTileBaseRects[entryKey] = rect;
        return rect;
    }

    private void SnapshotRecommendedTileBaseRects()
    {
        _recTileBaseRects.Clear();
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int c = 0; c < childCount; c++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, c) is not FrameworkElement tile ||
                tile.Visibility != Visibility.Visible ||
                tile.ActualWidth <= 0 ||
                tile.ActualHeight <= 0 ||
                tile.DataContext is not RecommendedGridEntry tileEntry)
            {
                continue;
            }

            _recTileBaseRects[tileEntry.EntryKey] = new Rect(
                tile.TransformToVisual(RecommendedAppsSurface).TransformPoint(new Point(0, 0)),
                new Size(tile.ActualWidth, tile.ActualHeight));
        }
    }

    /// <summary>
    /// Drop decision with sub-zone hysteresis: once a decision lands, the
    /// pointer owns that decision's specific sub-zone (merge center, left/right
    /// insertion strip, or trailing space) inflated by a small anti-jitter
    /// deadband. Leaving that sub-zone re-evaluates cleanly without locking out
    /// adjacent merge or insertion zones.
    /// </summary>
    private RecommendedDropDecision ResolveRecommendedDropDecision(Point pointInSurface)
    {
        if (_recHasDropAnchor && _recDropAnchorRect.Contains(pointInSurface))
        {
            return _recLastDropDecision;
        }

        if (_recPressedEntry is null)
        {
            return new RecommendedDropDecision(false, null, null, -1, default, default, default);
        }

        RecommendedDropDecision decision = EvaluateRecommendedDropTarget(
            pointInSurface,
            _recPressedEntry);
        if (decision.IsMerge || decision.ReorderTargetIndex >= 0)
        {
            _recDropAnchorRect = SearchRecommendationLayoutEngine.InflateDropAnchorRect(decision.AnchorRect, SearchRecommendationLayoutEngine.DropAnchorDeadband);
            _recHasDropAnchor = true;
        }
        else
        {
            _recHasDropAnchor = false;
        }

        _recLastDropDecision = decision;
        LogRecDragDecision(decision);
        return decision;
    }


    private void LogRecDragDecision(RecommendedDropDecision decision)
    {
        string summary = decision.IsMerge && decision.MergeTarget is { } mergeTarget
            ? $"merge:{mergeTarget.EntryKey}"
            : decision.ReorderTargetIndex >= 0
                ? $"insert:{decision.ReorderTargetIndex}"
                : "none";
        if (string.Equals(summary, _recLastDecisionSummary, StringComparison.Ordinal))
        {
            return;
        }

        _recLastDecisionSummary = summary;
        App.Log(
            $"[RecDrag] decision {summary} " +
            $"dragged={_recDraggedEntryKeys.Count} apps={_recDraggedAppKeys.Count} " +
            $"batch={_recDraggedEntryKeys.Count > 1}");
    }

    private void ResetRecommendedDropAnchor()
    {
        _recLastDropDecision = new RecommendedDropDecision(false, null, null, -1, default, default, default);
        _recHasDropAnchor = false;
        _recLastDecisionSummary = null;
        _recHasCachedReflowState = false;
    }

    private RecommendedDropDecision EvaluateRecommendedDropTarget(
        Point pointInSurface,
        RecommendedGridEntry draggedEntry)
    {
        var entries = _viewModel.RecommendedEntries;
        int count = entries.Count;
        if (count <= 1)
        {
            return new RecommendedDropDecision(false, null, null, -1, default, default, default);
        }

        var indexByKey = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            indexByKey[entries[i].EntryKey] = i;
        }

        if (!indexByKey.TryGetValue(draggedEntry.EntryKey, out int sourceIndex))
        {
            return new RecommendedDropDecision(false, null, null, -1, default, default, default);
        }

        bool isBatchDrag = _recDraggedEntryKeys.Count > 1;
        bool anyDraggedIsFolder = draggedEntry.IsFolder;
        if (!anyDraggedIsFolder && isBatchDrag)
        {
            foreach (string key in _recDraggedEntryKeys)
            {
                if (key.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
                {
                    anyDraggedIsFolder = true;
                    break;
                }
            }
        }

        var realizedTiles = new List<(int EntryIndex, RecommendedGridEntry Entry, FrameworkElement Tile, Rect Rect)>();
        int childCount = VisualTreeHelper.GetChildrenCount(RecommendedAppsRepeater);
        for (int c = 0; c < childCount; c++)
        {
            if (VisualTreeHelper.GetChild(RecommendedAppsRepeater, c) is not FrameworkElement tile ||
                tile.Visibility != Visibility.Visible ||
                tile.ActualWidth <= 0 ||
                tile.ActualHeight <= 0 ||
                tile.DataContext is not RecommendedGridEntry tileEntry ||
                !indexByKey.TryGetValue(tileEntry.EntryKey, out int entryIndex))
            {
                continue;
            }

            // Base rects, never live transforms: live rects include the
            // reflow translations and would feed decision→reflow→decision
            // oscillation. New tiles realized mid-drag capture lazily.
            var rect = GetTileBaseRect(tile, tileEntry.EntryKey);
            realizedTiles.Add((entryIndex, entries[entryIndex], tile, rect));
        }

        if (realizedTiles.Count == 0)
        {
            return new RecommendedDropDecision(false, null, null, -1, default, default, default);
        }

        realizedTiles.Sort(static (left, right) => left.EntryIndex.CompareTo(right.EntryIndex));

        int matchedTileIdx = -1;
        double bestCenterDistanceSq = double.MaxValue;
        for (int t = 0; t < realizedTiles.Count; t++)
        {
            Rect rect = realizedTiles[t].Rect;
            // Expand by half the UniformGridLayout 6px spacing (+2px overlap) so inter-tile gaps never become dead zones.
            var hitRect = new Rect(rect.X - 5, rect.Y - 5, rect.Width + 10, rect.Height + 10);
            if (!hitRect.Contains(pointInSurface))
            {
                continue;
            }

            double cx = rect.X + (rect.Width * 0.5);
            double cy = rect.Y + (rect.Height * 0.5);
            double distSq = ((pointInSurface.X - cx) * (pointInSurface.X - cx)) +
                            ((pointInSurface.Y - cy) * (pointInSurface.Y - cy));
            if (distSq < bestCenterDistanceSq)
            {
                bestCenterDistanceSq = distSq;
                matchedTileIdx = t;
            }
        }

        if (matchedTileIdx >= 0)
        {
            var (i, targetEntry, tile, rect) = realizedTiles[matchedTileIdx];
            if (i == sourceIndex)
            {
                return new RecommendedDropDecision(false, null, null, -1, default, default, default);
            }

            double relX = Math.Clamp((pointInSurface.X - rect.X) / Math.Max(1.0, rect.Width), 0.0, 1.0);
            double relY = Math.Clamp((pointInSurface.Y - rect.Y) / Math.Max(1.0, rect.Height), 0.0, 1.0);

            // Deliberate center zone merges dragged apps into a folder:
            // - Disabled whenever any dragged item is itself a folder.
            // - In batch drag, only merge onto an existing folder so multi-select drag onto app slots reliably reorders.
            bool canMergeOntoTarget =
                !anyDraggedIsFolder &&
                !_recDraggedEntryKeys.Contains(targetEntry.EntryKey) &&
                (!isBatchDrag || targetEntry.IsFolder);
            if (canMergeOntoTarget &&
                SearchRecommendationLayoutEngine.ShouldMergeOntoTile(relX, relY, targetEntry.IsFolder, isBatchDrag))
            {
                return new RecommendedDropDecision(
                    IsMerge: true,
                    MergeTarget: targetEntry,
                    MergeTargetElement: tile,
                    ReorderTargetIndex: -1,
                    HighlightRect: rect,
                    InsertBarPosition: default,
                    AnchorRect: SearchRecommendationLayoutEngine.ComputeTileMergeAnchorRect(rect, targetEntry.IsFolder));
            }

            bool beforeTile = relX < 0.5;
            int slotIndex = beforeTile ? i : i + 1;
            if (!isBatchDrag)
            {
                if (slotIndex == sourceIndex || slotIndex == sourceIndex + 1)
                {
                    return new RecommendedDropDecision(false, null, null, -1, default, default, default);
                }
            }
            else if (!SearchRecommendationLayoutEngine.WouldBatchReorderChangeOrder(entries, _recDraggedEntryKeys, slotIndex))
            {
                return new RecommendedDropDecision(false, null, null, -1, default, default, default);
            }

            int targetIndex = isBatchDrag
                ? slotIndex
                : (slotIndex > sourceIndex ? slotIndex - 1 : slotIndex);
            double barX = beforeTile ? Math.Max(1, rect.X - 4) : rect.Right + 2;
            double barY = rect.Y + ((rect.Height - RecDropInsertIndicator.Height) / 2);
            Rect reorderAnchor = SearchRecommendationLayoutEngine.ComputeTileReorderAnchorRect(
                rect,
                relX,
                relY,
                beforeTile,
                canMergeOntoTarget,
                targetEntry.IsFolder);
            return new RecommendedDropDecision(
                IsMerge: false,
                MergeTarget: null,
                MergeTargetElement: null,
                ReorderTargetIndex: targetIndex,
                HighlightRect: default,
                InsertBarPosition: new Point(barX, barY),
                AnchorRect: reorderAnchor);
        }

        // Trailing blank space after the last realized tile appends to the end.
        var lastTileInfo = realizedTiles[^1];
        Rect lastRect = lastTileInfo.Rect;
        if ((pointInSurface.Y > lastRect.Bottom - 8 ||
             (pointInSurface.Y >= lastRect.Y - 5 && pointInSurface.X >= lastRect.Right)) &&
            (isBatchDrag
                ? SearchRecommendationLayoutEngine.WouldBatchReorderChangeOrder(entries, _recDraggedEntryKeys, count)
                : sourceIndex < count - 1))
        {
            double barX = lastRect.Right + 2;
            double barY = lastRect.Y + ((lastRect.Height - RecDropInsertIndicator.Height) / 2);
            Rect appendAnchor = SearchRecommendationLayoutEngine.ComputeTrailingAppendAnchorRect(
                pointInSurface,
                lastRect,
                RecommendedAppsSurface.ActualWidth,
                RecommendedAppsSurface.ActualHeight);
            return new RecommendedDropDecision(
                IsMerge: false,
                MergeTarget: null,
                MergeTargetElement: null,
                ReorderTargetIndex: isBatchDrag ? count : count - 1,
                HighlightRect: default,
                InsertBarPosition: new Point(barX, barY),
                AnchorRect: appendAnchor);
        }

        return new RecommendedDropDecision(false, null, null, -1, default, default, default);
    }
}
