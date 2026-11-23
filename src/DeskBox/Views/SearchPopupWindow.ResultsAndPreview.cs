using System.Text;
using DeskBox.Controls;
using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace DeskBox.Views;

public sealed partial class SearchPopupWindow
{
    // Multi-selection state: tracks all items selected via rubber-band or Ctrl+click.
    // When non-empty, batch operations (copy/cut/delete) act on these items.
    private readonly HashSet<SearchResultItem> _multiSelectedItems = new();
    private readonly HashSet<SearchResultItem> _rubberBandBaseSelection = new();
    private bool _isRubberBanding;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;
    private Point _rubberBandPointerInViewport;
    private DispatcherTimer? _rubberBandAutoScrollTimer;
    private bool _suppressMultiSelectSync;
    private int _selectionAnchorIndex = -1;

    private void ResultsPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ResultsPanel);
        var source = e.OriginalSource as DependencyObject;
        var row = FindItemRow(source);
        var item = ResolveResultItem(source);
        _suppressNextSearchRightTap = false;

        bool isCtrlPressed = Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Shift);
        bool isLeft = point.Properties.IsLeftButtonPressed;
        bool isRight = !isLeft && point.Properties.IsRightButtonPressed;

        // Ctrl-click / Shift-click on a row: modify or preserve multi-selection
        // WITHOUT returning early before _dragOutCandidate is armed so Ctrl+Drag
        // (Copy) and Shift+Drag (Move) work just like Windows Explorer.
        _pendingCtrlToggleOffOnRelease = null;
        bool modifierHandledSelection = false;
        if (item is not null && isLeft && (isCtrlPressed || isShiftPressed))
        {
            int anchorIndex = _selectionAnchorIndex >= 0
                ? _selectionAnchorIndex
                : _viewModel.SelectedIndex;
            var modDecision = EvaluateSearchRowModifierPress(
                isCtrlPressed,
                isShiftPressed,
                _multiSelectedItems.Contains(item),
                ReferenceEquals(_viewModel.SelectedItem, item),
                anchorIndex >= 0);

            if (modDecision.DeferToggleOffToRelease)
            {
                // Keep the clicked item in _multiSelectedItems while dragging;
                // only toggle it off on PointerReleased when no drag started.
                _pendingCtrlToggleOffOnRelease = item;
                SelectResultItem(item, row);
                _selectionAnchorIndex = _viewModel.CurrentResults.IndexOf(item);
                modifierHandledSelection = true;
                e.Handled = true;
            }
            else if (isCtrlPressed)
            {
                SelectResultItem(item, row);
                _selectionAnchorIndex = _viewModel.CurrentResults.IndexOf(item);
                ToggleMultiSelectionItem(item);
                modifierHandledSelection = true;
                e.Handled = true;
            }
            else if (modDecision.PreserveExistingSelection)
            {
                if (!_multiSelectedItems.Contains(item))
                {
                    ToggleMultiSelectionItem(item);
                }

                SelectResultItem(item, row);
                modifierHandledSelection = true;
                e.Handled = true;
            }
            else if (modDecision.RangeSelect && anchorIndex >= 0)
            {
                _selectionAnchorIndex = anchorIndex;
                RangeSelectItems(anchorIndex, item);
                modifierHandledSelection = true;
                e.Handled = true;
            }
        }

        // Details-view rows are interactive across their complete width. Marquee
        // selection starts only from actual empty result-surface space, never from
        // a padding or inter-column gap inside a row.
        if (SearchResultSelectionPolicy.ShouldStartRubberBand(
                isLeft,
                isOverResultRow: row is not null || item is not null,
                isShiftPressed))
        {
            DisarmDragOutCandidate();
            StartRubberBand(e);
            e.Handled = true;
            return;
        }

        bool pointerIsOnDragHandle =
            item is not null &&
            row is not null &&
            item.Kind is SearchResultKind.File or SearchResultKind.Folder &&
            !string.IsNullOrWhiteSpace(item.DetailPath);
        bool preserveSelectionForDrag =
            item is not null &&
            SearchResultSelectionPolicy.ShouldPreserveSelectionForDrag(
                _multiSelectedItems.Contains(item),
                _multiSelectedItems.Count,
                pointerIsOnDragHandle);

        if (item is not null && isLeft && !modifierHandledSelection)
        {
            if (!preserveSelectionForDrag)
            {
                ClearMultiSelection();
            }
            SelectResultItem(item, row);
            _selectionAnchorIndex = _viewModel.CurrentResults.IndexOf(item);
        }

        if (!isLeft && !isRight)
        {
            DisarmDragOutCandidate();
            return;
        }

        // Allow dragging file/folder results from anywhere on the row
        // (excluding inline quick-action buttons), only from a physical-button
        // pointer: touch/pen contact reports IsLeftButtonPressed too but has
        // never carried this mouse drag pipeline's semantics (the grid's manual
        // drag filters them the same way). Capturing the panel pointer is part
        // of arming — without it a release outside the panel never reaches us
        // and the stale candidate would arm a phantom drag on the next hover move.
        // Right-button drag-out exists only on the elevated pointer pipeline;
        // the WinUI platform drag used for normal runs is primary-button only.
        PointerPoint pressedPoint = e.GetCurrentPoint(ResultsPanel);
        if (item is not null && row is not null && pointerIsOnDragHandle &&
            (!isRight || IsCurrentProcessElevatedForDrag()) &&
            pressedPoint.PointerDeviceType is
                PointerDeviceType.Mouse or PointerDeviceType.Touchpad &&
            ResultsPanel.CapturePointer(e.Pointer))
        {
            _dragOutCandidate = new SearchDragOutCandidate(
                e.Pointer.PointerId,
                item,
                row,
                pressedPoint.Position,
                IsRightButtonDrag: isRight);
        }
        else
        {
            DisarmDragOutCandidate();
        }
    }

    private void DisarmDragOutCandidate(PointerRoutedEventArgs? pointerArgs = null)
    {
        if (_dragOutCandidate is not { } candidate)
        {
            return;
        }

        _dragOutCandidate = null;
        if (pointerArgs is not null &&
            new Rect(0, 0, candidate.Row.ActualWidth, candidate.Row.ActualHeight)
                .Contains(pointerArgs.GetCurrentPoint(candidate.Row).Position))
        {
            candidate.Row.ClearPressedState();
        }
        else
        {
            candidate.Row.ClearTransientPointerStates();
        }

        // Releasing our own capture re-enters ResultsPanel_PointerCaptureLost,
        // which finds no candidate and stays a no-op.
        ResultsPanel.ReleasePointerCaptures();
    }

    private void ResultsPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Update rubber-band selection if active.
        if (_isRubberBanding)
        {
            _rubberBandPointerInViewport = e.GetCurrentPoint(ResultsPanel).Position;
            UpdateRubberBand(e.GetCurrentPoint(ResultsSurface).Position);
            e.Handled = true;
            return;
        }

        if (_dragOutCandidate is not { } candidate)
        {
            return;
        }

        PointerPoint current = e.GetCurrentPoint(ResultsPanel);
        var decision = SearchResultSelectionPolicy.EvaluateDragOutMove(
            candidateArmed: true,
            samePointer: e.Pointer.PointerId == candidate.PointerId,
            leftButtonPressed: candidate.IsRightButtonDrag
                ? current.Properties.IsRightButtonPressed
                : current.Properties.IsLeftButtonPressed,
            distanceUnits: Math.Sqrt(
                Math.Pow(current.Position.X - candidate.StartPoint.X, 2) +
                Math.Pow(current.Position.Y - candidate.StartPoint.Y, 2)));
        if (decision == SearchResultSelectionPolicy.DragOutMoveDecision.Disarm)
        {
            DisarmDragOutCandidate();
            return;
        }
        if (decision != SearchResultSelectionPolicy.DragOutMoveDecision.Start ||
            string.IsNullOrWhiteSpace(candidate.Item.DetailPath))
        {
            return;
        }

        // The gesture is consumed: the panel pointer capture hands control to
        // the platform drag loop, and the in-flight flag blocks re-arming
        // until the session completes.
        _dragOutCandidate = null;
        ResultsPanel.ReleasePointerCaptures();
        _dragOutInProgress = true;

        StartDragOut(candidate, e);
    }

    internal static (bool DeferToggleOffToRelease, bool RangeSelect, bool PreserveExistingSelection) EvaluateSearchRowModifierPress(
        bool ctrlDown,
        bool shiftDown,
        bool itemAlreadyMultiSelected,
        bool itemIsPrimarySelected,
        bool hasValidAnchor)
    {
        if (ctrlDown)
        {
            return (DeferToggleOffToRelease: itemAlreadyMultiSelected, RangeSelect: false, PreserveExistingSelection: false);
        }

        if (shiftDown)
        {
            if (itemAlreadyMultiSelected || itemIsPrimarySelected)
            {
                return (DeferToggleOffToRelease: false, RangeSelect: false, PreserveExistingSelection: true);
            }

            if (hasValidAnchor)
            {
                return (DeferToggleOffToRelease: false, RangeSelect: true, PreserveExistingSelection: false);
            }
        }

        return (false, false, false);
    }

    private void ResultsPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Finalize rubber-band selection.
        if (_isRubberBanding)
        {
            EndRubberBand();
            e.Handled = true;
            return;
        }

        // A normal click selects the row in ResultsPanel_PointerPressed. Opening is
        // intentionally reserved for DoubleTapped or Enter, matching Explorer and
        // Everything and preventing the first click of a double-click from hiding
        // the popup before the second click arrives.
        if (_pendingCtrlToggleOffOnRelease is { } pendingToggleOff && !_dragOutInProgress)
        {
            _pendingCtrlToggleOffOnRelease = null;
            ToggleMultiSelectionItem(pendingToggleOff);
        }
        else
        {
            _pendingCtrlToggleOffOnRelease = null;
        }

        DisarmDragOutCandidate(e);
    }

    private void ResultsPanel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = ResolveResultItem(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        SelectResultItem(item, FindItemRow(e.OriginalSource as DependencyObject));
        if (_viewModel.ExecuteItem(item))
        {
            e.Handled = true;
        }
    }

    private void SelectResultItem(SearchResultItem item, SearchResultRowControl? row = null)
    {
        int index = _viewModel.CurrentResults.IndexOf(item);
        if (index >= 0)
        {
            _viewModel.SelectedIndex = index;
        }

        _viewModel.SelectedItem = item;
        row ??= FindRowByDataContext(ResultsRepeater, item);
        if (row is not null)
        {
            row.IsTabStop = true;
            row.Focus(FocusState.Pointer);
        }
    }

    private void OnCurrentResultsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.IsBatchUpdatingResults)
        {
            return;
        }

        if (IsPopupVisible && !_isWindowClosed)
        {
            UpdatePopupDynamicBounds();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchPopupViewModel.SelectedItem):
                UpdateSelectionHighlight();
                UpdateSelectionActions();
                break;

            case nameof(SearchPopupViewModel.ActiveScopeBadgeText):
                UpdateScopePrefixBadge(SearchTextBox.Text);
                break;

            case nameof(SearchPopupViewModel.EffectiveHighlightQuery):
                RefreshPreparedResultRows();
                break;

            case nameof(SearchPopupViewModel.ShouldShowCategoryBar):
                if (!_viewModel.IsBatchUpdatingResults)
                {
                    UpdatePanelVisibility();
                }
                break;

            case nameof(SearchPopupViewModel.IsSearching):
            case nameof(SearchPopupViewModel.HasResults):
            case nameof(SearchPopupViewModel.HasCurrentResults):
                if (e.PropertyName == nameof(SearchPopupViewModel.HasResults) &&
                    _viewModel.HasResults)
                {
                    if (_viewModel.IsApplyingBackgroundResultRefresh)
                    {
                        ResultsPanel.Opacity = 1;
                    }
                }

                if (!_viewModel.IsBatchUpdatingResults)
                {
                    UpdatePanelVisibility();
                }
                break;

            case nameof(SearchPopupViewModel.SelectedTab):
                ResultsPanel.ChangeView(null, 0, null, disableAnimation: true);
                _measuredResultRowHeight = null;
                SyncTabSelection();
                RefreshPreparedResultRows();
                if (!_viewModel.IsBatchUpdatingResults)
                {
                    UpdatePanelVisibility();
                }
                UpdateSelectionHighlight();
                QueueTabSelectionRefreshAndFocus();
                break;

            case nameof(SearchPopupViewModel.ResultFilter):
                UpdatePanelVisibility();
                break;

            case nameof(SearchPopupViewModel.SortColumn):
            case nameof(SearchPopupViewModel.SortAscending):
                UpdateSortHeaders();
                break;

            case nameof(SearchPopupViewModel.HasRecommendedEntries):
            case nameof(SearchPopupViewModel.HasHiddenRecommendations):
            case nameof(SearchPopupViewModel.HiddenRecommendationCount):
                UpdateRecommendationHeaderState();
                UpdatePanelVisibility();
                break;

            case nameof(SearchPopupViewModel.IsEverythingPromptVisible):
            case nameof(SearchPopupViewModel.EverythingPromptText):
            case nameof(SearchPopupViewModel.EverythingPromptActionText):
            case nameof(SearchPopupViewModel.IsEverythingPromptWarning):
                UpdateEverythingPromptBanner();
                break;

            case nameof(SearchPopupViewModel.StatusText):
                // Result counts and timing remain diagnostic data, not persistent UI.
                break;
        }
    }

    private void QueueTabSelectionRefreshAndFocus()
    {
        int generation = ++_tabFocusRestoreGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (generation != _tabFocusRestoreGeneration || !IsPopupVisible)
            {
                return;
            }

            UpdateSelectionHighlight();

            // SelectionChanged runs inside ListView pointer handling. Restoring
            // focus synchronously can be overwritten when that pointer route
            // finishes, so perform it on the next low-priority dispatcher pass.
            SearchTextBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Highlights the row for the keyboard-selected result and brings it into view.
    /// </summary>
    private void UpdateSelectionHighlight()
    {
        ++_selectedRowHighlightGeneration;
        if (_selectedRow is not null)
        {
            _selectedRow.IsSelected = false;
            _selectedRow.IsTabStop = false;
            _selectedRow = null;
        }

        if (_viewModel.SelectedItem is not { } selected)
        {
            ScheduleSelectedItemDetailsRefresh();
            return;
        }

        if (FindRowByDataContext(ResultsRepeater, selected) is { } row)
        {
            SetSelectedRow(row);
            EnsureSelectedRowVisible(row);
        }
        else if (_viewModel.SelectedIndex >= 0)
        {
            // Element not realized (off-screen). Scroll the ScrollViewer
            // so the ItemsRepeater realizes it, then keep retrying the
            // highlight — realization completes asynchronously.
            ScrollToSelectedIndex();
            ScheduleSelectedRowHighlight();
        }

        ScheduleSelectedItemDetailsRefresh();
    }

    private void EnsureSelectedRowVisible(SearchResultRowControl row)
    {
        // Use the row's real viewport-relative position: arithmetic from a
        // cached row height drifts once heights vary or virtualization
        // realizes past the viewport, which made navigation skip rows.
        Windows.Foundation.Rect bounds = row.TransformToVisual(ResultsPanel)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
        double viewportHeight = ResultsPanel.ViewportHeight;

        if (bounds.Y >= 0 && (bounds.Y + bounds.Height) <= viewportHeight)
        {
            // Fully visible: keyboard moves must not scroll the list.
            return;
        }

        double scrollTarget = bounds.Y < 0
            ? Math.Max(0, ResultsPanel.VerticalOffset + bounds.Y)
            : ResultsPanel.VerticalOffset + bounds.Y + bounds.Height - viewportHeight;
        ResultsPanel.ChangeView(null, scrollTarget, null, disableAnimation: true);
    }

    private void SetSelectedRow(SearchResultRowControl row)
    {
        _selectedRow = row;
        row.IsSelected = true;
        row.IsTabStop = true;
        _measuredResultRowHeight = row.ActualHeight > 0
            ? row.ActualHeight + row.Margin.Top + row.Margin.Bottom
            : _measuredResultRowHeight;
    }

    private void ScheduleSelectedRowHighlight()
    {
        int generation = ++_selectedRowHighlightGeneration;
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Virtualization realizes the selected row a few frames after the
            // scroll lands; retry until it appears or the selection moves on.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(24);
                if (generation != _selectedRowHighlightGeneration ||
                    _viewModel.SelectedItem is not { } selected)
                {
                    return;
                }

                if (FindRowByDataContext(ResultsRepeater, selected) is { } row)
                {
                    if (_selectedRow is null)
                    {
                        SetSelectedRow(row);
                    }

                    EnsureSelectedRowVisible(row);

                    return;
                }
            }
        });
    }

    /// <summary>
    /// Scrolls the results ScrollViewer to bring the selected index into view.
    /// Used when the ItemsRepeater has not realized the element yet.
    /// </summary>
    private void ScrollToSelectedIndex()
    {
        int index = _viewModel.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        double rowHeight = GetMeasuredResultRowHeight();
        double targetTop = index * rowHeight;
        double targetBottom = targetTop + rowHeight;
        double viewTop = ResultsPanel.VerticalOffset;
        double viewBottom = viewTop + ResultsPanel.ViewportHeight;

        if (targetTop >= viewTop && targetBottom <= viewBottom)
        {
            // Already inside the viewport: scrolling would make the list jump.
            // Realization is handled by the highlight retry.
            return;
        }

        // Selection moves one row at a time, so align the row's edge with the
        // matching viewport edge — exactly one row of travel per keypress.
        double scrollTarget = targetTop < viewTop
            ? Math.Max(0, targetTop)
            : targetBottom - ResultsPanel.ViewportHeight;

        ResultsPanel.ChangeView(null, scrollTarget, null, disableAnimation: true);
    }

    private double GetMeasuredResultRowHeight()
    {
        if (_measuredResultRowHeight is { } cached && cached > 0)
        {
            return cached;
        }

        if (ResultsRepeater.ItemsSource is System.Collections.ICollection coll)
        {
            for (int i = 0; i < Math.Min(coll.Count, 5); i++)
            {
                if (ResultsRepeater.TryGetElement(i) is FrameworkElement fe && fe.ActualHeight > 0)
                {
                    double measured = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                    _measuredResultRowHeight = measured;
                    return measured;
                }
            }
        }

        return 48; // Fallback estimate
    }

    /// <summary>
    /// Freshly populated results are realized one layout pass after the collection
    /// changes, so a selection made before that finds no row. Re-apply the highlight
    /// and the lazy icon visuals as each row element is prepared.
    /// </summary>
    private void OnResultsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not SearchResultRowControl row)
        {
            return;
        }

        // ElementPrepared can run before the DataTemplate root receives its inherited
        // DataContext. Resolve the item from the repeater index so the one-time compiled
        // bindings never get refreshed against a transient null value. The DataContext
        // fallback keeps this safe if the source changes while an element is recycled.
        SearchResultItem? preparedItem = args.Index >= 0 &&
                                         args.Index < _viewModel.CurrentResults.Count
            ? _viewModel.CurrentResults[args.Index]
            : row.DataContext as SearchResultItem;

        row.DataContext = preparedItem;
        row.PrepareItem(preparedItem);
        row.ConfigureRowPresentation(_viewModel.EffectiveHighlightQuery);

        // Lazy shell icon: show the real icon once resolved, otherwise the glyph block.
        // Recycled rows can be re-bound to the same item instance (no DataContextChanged),
        // so this must run on every prepare.
        row.RefreshIconVisuals();
        row.SetFileColumnsVisible(AreFileColumnsVisible());
        if (row.Item is { IconResolved: false } item)
        {
            _ = EnrichPreparedResultRowAsync(row, item);
        }

        bool isSelectedRow = _viewModel.SelectedItem is { } selected &&
                             ReferenceEquals(row.Item, selected);
        if (isSelectedRow && !ReferenceEquals(row, _selectedRow))
        {
            if (_selectedRow is not null)
            {
                _selectedRow.IsSelected = false;
            }

            _selectedRow = row;
            row.IsSelected = true;
            row.IsTabStop = true;
        }
        else if (!isSelectedRow && row.IsSelected)
        {
            // A recycled element may still carry stale selection visuals.
            row.IsSelected = false;
            row.IsTabStop = false;
        }

        // Sync multi-selection state on recycled elements.
        row.IsMultiSelected = row.Item is { } rowItem && _multiSelectedItems.Contains(rowItem);
    }

    private void OnResultsElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not SearchResultRowControl row)
        {
            return;
        }

        if (ReferenceEquals(_selectedRow, row))
        {
            _selectedRow = null;
        }

        if (_dragOutCandidate is { } dragCandidate &&
            ReferenceEquals(dragCandidate.Row, row))
        {
            DisarmDragOutCandidate();
        }

        row.IsSelected = false;
        row.IsMultiSelected = false;
        row.IsTabStop = false;
        row.PrepareItem(null);
        row.RefreshIconVisuals();
        row.DataContext = null;
    }

    private async Task EnrichPreparedResultRowAsync(
        SearchResultRowControl row,
        SearchResultItem item)
    {
        await _viewModel.EnsureResultMetadataAsync(item);
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RefreshPreparedResultRow(row, item));
            return;
        }

        RefreshPreparedResultRow(row, item);
    }

    private void RefreshPreparedResultRow(
        SearchResultRowControl row,
        SearchResultItem item)
    {
        if (ReferenceEquals(row.Item, item))
        {
            row.RefreshIconVisuals();
        }

        if (ReferenceEquals(_displayedDetailsItem, item) &&
            DetailsPaneCard.Visibility == Visibility.Visible)
        {
            PopulateDetailsPane(item, immediate: true);
        }
    }

    private void ResultsPanel_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        DisarmDragOutCandidate();

        if (_suppressNextSearchRightTap)
        {
            _suppressNextSearchRightTap = false;
            e.Handled = true;
            return;
        }

        var item = ResolveResultItem(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            // Right-click on empty area with multi-selection: show batch context menu.
            if (_multiSelectedItems.Count > 0)
            {
                ShowBatchResultFlyout(ResultsPanel, e.GetPosition(ResultsPanel));
            }
            return;
        }

        // If the right-clicked item is already in multi-selection, show batch menu.
        if (_multiSelectedItems.Contains(item))
        {
            var batchAnchor = FindItemRow(e.OriginalSource as DependencyObject)
                              as UIElement ?? ResultsPanel;
            ShowBatchResultFlyout(batchAnchor, e.GetPosition(batchAnchor));
            e.Handled = true;
            return;
        }

        ClearMultiSelection();
        var row = FindItemRow(e.OriginalSource as DependencyObject);
        SelectResultItem(item, row);
        _selectionAnchorIndex = _viewModel.CurrentResults.IndexOf(item);
        var anchor = (UIElement?)row ?? ResultsPanel;
        ShowResultFlyout(item, anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    // ── Rubber-band multi-selection ──

    private void ClearAllResultRowTransientStates()
    {
        int childCount = VisualTreeHelper.GetChildrenCount(ResultsRepeater);
        for (int i = 0; i < childCount; i++)
        {
            if (VisualTreeHelper.GetChild(ResultsRepeater, i) is SearchResultRowControl row)
            {
                row.ClearTransientPointerStates();
            }
        }
    }

    private void StartRubberBand(PointerRoutedEventArgs e)
    {
        ClearAllResultRowTransientStates();
        _isRubberBanding = true;
        bool isAdditive = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        _rubberBandBaseSelection.Clear();
        if (isAdditive)
        {
            _rubberBandBaseSelection.UnionWith(_multiSelectedItems);
        }
        else
        {
            _multiSelectedItems.Clear();
            _selectionAnchorIndex = -1;
            SyncMultiSelectionVisuals();
            UpdateSelectionActions();
        }

        var position = e.GetCurrentPoint(ResultsSurface).Position;
        _rubberBandStart = position;
        _rubberBandCurrent = position;
        _rubberBandPointerInViewport = e.GetCurrentPoint(ResultsPanel).Position;
        RubberBandRect.Visibility = Visibility.Visible;
        UpdateRubberBandRect(position, position);
        ResultsPanel.CapturePointer(e.Pointer);
        EnsureRubberBandAutoScrollTimer();
        _rubberBandAutoScrollTimer!.Start();
    }

    private void UpdateRubberBand(Point position)
    {
        _rubberBandCurrent = position;
        UpdateRubberBandRect(_rubberBandStart, position);
        SelectItemsIntersectingRubberBand(_rubberBandStart, position);
    }

    private void EndRubberBand()
    {
        _isRubberBanding = false;
        _rubberBandAutoScrollTimer?.Stop();
        _rubberBandBaseSelection.Clear();
        RubberBandRect.Visibility = Visibility.Collapsed;
        ResultsPanel.ReleasePointerCaptures();
    }

    private void ResultsPanel_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Losing the panel capture ends the armed drag gesture: whatever took
        // the pointer owns it now, and a later move must not start a drag.
        DisarmDragOutCandidate();

        if (!_isRubberBanding)
        {
            return;
        }

        _isRubberBanding = false;
        _rubberBandAutoScrollTimer?.Stop();
        _rubberBandBaseSelection.Clear();
        RubberBandRect.Visibility = Visibility.Collapsed;
    }

    private void EnsureRubberBandAutoScrollTimer()
    {
        if (_rubberBandAutoScrollTimer is not null)
        {
            return;
        }

        _rubberBandAutoScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(32)
        };
        _rubberBandAutoScrollTimer.Tick += OnRubberBandAutoScrollTick;
    }

    private void OnRubberBandAutoScrollTick(object? sender, object e)
    {
        if (!_isRubberBanding)
        {
            _rubberBandAutoScrollTimer?.Stop();
            return;
        }

        double delta = SearchResultSelectionPolicy.GetAutoScrollDelta(
            _rubberBandPointerInViewport.Y,
            ResultsPanel.ViewportHeight);
        if (Math.Abs(delta) < 0.1)
        {
            return;
        }

        double previousOffset = ResultsPanel.VerticalOffset;
        double targetOffset = Math.Clamp(
            previousOffset + delta,
            0,
            ResultsPanel.ScrollableHeight);
        if (Math.Abs(targetOffset - previousOffset) < 0.1)
        {
            return;
        }

        ResultsPanel.ChangeView(
            null,
            targetOffset,
            null,
            disableAnimation: true);
        _rubberBandCurrent = new Point(
            _rubberBandCurrent.X,
            _rubberBandCurrent.Y + targetOffset - previousOffset);
        UpdateRubberBand(_rubberBandCurrent);
    }

    private void ResultsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsMorphSpringRunning)
        {
            return;
        }

        SyncResultsSurfaceMinHeight(e.NewSize.Height);
    }

    private void SyncResultsSurfaceMinHeight(double? viewportHeight = null)
    {
        // Keep the transparent selection surface at least as tall as the viewport,
        // so blank space below a short result list can also start a selection box.
        double height = viewportHeight ?? ResultsPanel.ActualHeight;
        double minHeight = Math.Max(
            0,
            height - ResultsPanel.Padding.Top - ResultsPanel.Padding.Bottom);
        if (Math.Abs(ResultsSurface.MinHeight - minHeight) > 0.1)
        {
            ResultsSurface.MinHeight = minHeight;
        }
    }

    private void ResultsPanel_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // The keyboard-selected row may finish realizing a frame after the
        // scroll lands; re-check the highlight whenever the view settles.
        if (_selectedRow is null && _viewModel.SelectedItem is not null)
        {
            ScheduleSelectedRowHighlight();
        }

        if (_isRubberBanding || !_viewModel.HasMoreResults || _viewModel.IsLoadingMore)
        {
            return;
        }

        double remaining = ResultsPanel.ScrollableHeight - ResultsPanel.VerticalOffset;
        double threshold = Math.Max(320, ResultsPanel.ViewportHeight * 0.75);
        if (remaining <= threshold)
        {
            _ = _viewModel.LoadMoreResultsAsync();
        }
    }

    private void UpdateRubberBandRect(Point start, Point end)
    {
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        double w = Math.Abs(end.X - start.X);
        double h = Math.Abs(end.Y - start.Y);
        RubberBandRect.Width = w;
        RubberBandRect.Height = h;
        // Pointer, rows, and rectangle all use ResultsSurface coordinates. This
        // stays correct across scrolling and avoids hard-coded padding offsets.
        RubberBandRect.RenderTransform = new TranslateTransform { X = x, Y = y };
    }

    private static bool Intersects(Rect a, Rect b) =>
        SearchRecommendationLayoutEngine.Intersects(a, b);

    private void SelectItemsIntersectingRubberBand(Point start, Point end)
    {
        var rubberRect = new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));

        // If the rubber band is too small, don't select anything yet.
        if (rubberRect.Width < 3 && rubberRect.Height < 3)
        {
            return;
        }

        _suppressMultiSelectSync = true;
        _multiSelectedItems.Clear();
        _multiSelectedItems.UnionWith(_rubberBandBaseSelection);

        if (_viewModel.CurrentResults is { } results)
        {
            for (int i = 0; i < results.Count; i++)
            {
                if (ResultsRepeater.TryGetElement(i) is not FrameworkElement fe)
                {
                    continue;
                }

                var elementRect = new Rect(
                    fe.TransformToVisual(ResultsSurface).TransformPoint(new Point(0, 0)),
                    new Size(fe.ActualWidth, fe.ActualHeight));

                if (Intersects(rubberRect, elementRect))
                {
                    _multiSelectedItems.Add(results[i]);
                }
            }
        }

        _suppressMultiSelectSync = false;
        SyncMultiSelectionVisuals();
        UpdateSelectionActions();
    }

    // ── Multi-selection helpers ──

    private void ToggleMultiSelectionItem(SearchResultItem item)
    {
        if (!_multiSelectedItems.Add(item))
        {
            _multiSelectedItems.Remove(item);
        }
        SyncMultiSelectionVisuals();
        UpdateSelectionActions();
    }

    private void RangeSelectItems(int fromIndex, SearchResultItem toItem)
    {
        int toIndex = _viewModel.CurrentResults.IndexOf(toItem);
        var range = SearchResultSelectionPolicy.GetRange(
            fromIndex,
            toIndex,
            _viewModel.CurrentResults.Count);
        if (range.Start < 0)
        {
            return;
        }

        _multiSelectedItems.Clear();
        for (int i = range.Start; i <= range.End; i++)
        {
            _multiSelectedItems.Add(_viewModel.CurrentResults[i]);
        }

        SelectResultItem(toItem);
        SyncMultiSelectionVisuals();
        UpdateSelectionActions();
    }

    private void ClearMultiSelection()
    {
        _selectionAnchorIndex = -1;
        if (_multiSelectedItems.Count == 0)
        {
            return;
        }
        _multiSelectedItems.Clear();
        SyncMultiSelectionVisuals();
        UpdateSelectionActions();
    }

    private void SyncMultiSelectionVisuals()
    {
        if (_suppressMultiSelectSync)
        {
            return;
        }

        if (_viewModel.CurrentResults is { } results)
        {
            for (int i = 0; i < results.Count; i++)
            {
                if (ResultsRepeater.TryGetElement(i) is SearchResultRowControl row)
                {
                    row.IsMultiSelected = _multiSelectedItems.Contains(results[i]);
                }
            }
        }
    }

    // ── Batch operations ──

    private void ShowBatchResultFlyout(UIElement anchor, Point point)
    {
        var flyout = BuildBatchContextMenu();
        if (flyout.Items.Count == 0)
        {
            return;
        }
        _restoreResultFocusAfterFlyout = true;
        flyout.Closed += (_, _) =>
        {
            ClearAllResultRowTransientStates();
            _restoreResultFocusAfterFlyout = false;
        };
        flyout.ShowAt(anchor, point);
    }

    private MenuFlyout BuildBatchContextMenu()
    {
        var flyout = new MenuFlyout();
        int count = _multiSelectedItems.Count;
        bool allFileSystem = count > 0 && _multiSelectedItems.All(i =>
            i.Kind is SearchResultKind.File or SearchResultKind.Folder &&
            !string.IsNullOrWhiteSpace(i.DetailPath) &&
            (File.Exists(i.DetailPath) || Directory.Exists(i.DetailPath)));

        if (!allFileSystem)
        {
            var openAll = new MenuFlyoutItem
            {
                Text = string.Format(_localizationService.T("Search.Batch.OpenAll"), count),
                Icon = new FontIcon { Glyph = "\uE8E5" }
            };
            openAll.Click += (_, _) => _ = OpenSelectedItemsWithGuardAsync();
            flyout.Items.Add(openAll);
            return flyout;
        }

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectedItemsAsync(DataPackageOperation.Copy);
        flyout.Items.Add(copyItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectedItemsAsync(DataPackageOperation.Move);
        flyout.Items.Add(cutItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Delete.Action"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private void OpenSelectedItems()
    {
        foreach (var item in _multiSelectedItems.ToList())
        {
            _viewModel.ExecuteItem(item);
        }
        ClearMultiSelection();
    }

    private void ApplyWin10SafeSearchGlyphs()
    {
        string hideGlyph = WindowsCompatibilityService.IsWindows11OrLater ? "\uED1A" : "\uE890";
        if (BatchHideSelectedIcon is not null)
        {
            BatchHideSelectedIcon.Glyph = hideGlyph;
        }

        if (HiddenAppsEntryIcon is not null)
        {
            HiddenAppsEntryIcon.Glyph = hideGlyph;
        }
    }

    private async Task OpenSelectedItemsWithGuardAsync()
    {
        // Deep review R18 (item C): the batch menu only appears for mixed
        // selections, where "open all" launches N Shell operations one by
        // one with no ceiling — a wide query's Ctrl+A could reach hundreds
        // of items. Explorer confirms past 15; match that precedent.
        var items = _multiSelectedItems.ToList();
        const int OpenAllConfirmationThreshold = 15;
        if (items.Count > OpenAllConfirmationThreshold)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(RootGrid),
                Title = _localizationService.Format("Search.Batch.OpenAll.ConfirmTitle", items.Count),
                Content = _localizationService.Format("Search.Batch.OpenAll.ConfirmBody", items.Count),
                PrimaryButtonText = _localizationService.T("Common.Open"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        OpenSelectedItems();
    }

    private async Task CopySelectedItemsAsync(DataPackageOperation operation)
    {
        var paths = _multiSelectedItems
            .Where(i => !string.IsNullOrWhiteSpace(i.DetailPath))
            .Select(i => i.DetailPath!)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            bool cut = operation == DataPackageOperation.Move &&
                !Platform.DriveMediaInterop.IsProtectedDragOutSource(paths) &&
                !Services.DragDrop.StoreVirtualizedPathPolicy.IsCopyOnlyVirtualizedSource(paths);
            if (!ShellClipboardHelper.TrySetFileDropList(paths, cut))
            {
                var data = new DataPackage
                {
                    RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy
                };
                await SetDragPayloadAsync(data, paths);
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }

            ShowTransientStatus(_localizationService.T(
                cut
                    ? "Search.Action.CutReady"
                    : "Search.Action.CopyReady"));
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Batch clipboard failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var items = _multiSelectedItems
            .Where(i => !string.IsNullOrWhiteSpace(i.DetailPath))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        await DeleteFileSystemItemsCoreAsync(
            items.Select(i => i.DetailPath!),
            clearMultiSelectionOnSuccess: true);
    }

    /// <summary>
    /// Deletes search-result entries through the Shell with an owner window,
    /// so the native confirmation (recycle or permanent, per the user's own
    /// Explorer settings) replaces the retired self-drawn dialog: that dialog
    /// promised a Recycle Bin while the underlying flags silently degraded to
    /// a permanent delete on volumes without one.
    /// </summary>
    private async Task DeleteFileSystemItemsCoreAsync(
        IEnumerable<string> paths,
        bool clearMultiSelectionOnSuccess)
    {
        var fileService = CurrentApp?.FileService;
        if (fileService is null || _hwnd == IntPtr.Zero)
        {
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
            return;
        }

        try
        {
            IReadOnlySet<string> deletedPaths = await fileService.DeleteEntriesWithShellAsync(
                paths,
                recycle: true,
                _hwnd);
            if (deletedPaths.Count > 0)
            {
                ShowTransientStatus(_localizationService.T("Search.Action.Deleted"));
                if (clearMultiSelectionOnSuccess)
                {
                    ClearMultiSelection();
                }
            }

            await RefreshResultsAfterFileOperationAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Delete failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async void CopySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await CopySelectedItemsAsync(DataPackageOperation.Copy);
    }

    private async void CutSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await CopySelectedItemsAsync(DataPackageOperation.Move);
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedItemsAsync();
    }

    private void ShowResultFlyout(SearchResultItem item, UIElement anchor, Windows.Foundation.Point point)
    {
        var flyout = BuildResultContextMenu(item);
        if (flyout.Items.Count == 0)
        {
            return;
        }

        _restoreResultFocusAfterFlyout = true;
        flyout.Closed += (_, _) =>
        {
            ClearAllResultRowTransientStates();
            if (_restoreResultFocusAfterFlyout && IsPopupVisible && _viewModel.SelectedItem is not null)
            {
                DispatcherQueue.TryEnqueue(() => FocusSelectedResult());
            }

            _restoreResultFocusAfterFlyout = false;
        };
        flyout.ShowAt(anchor, point);
    }

    /// <summary>
    /// Builds a context menu of secondary actions for a search result row.
    /// </summary>
    private MenuFlyout BuildResultContextMenu(SearchResultItem item)
    {
        var flyout = new MenuFlyout();

        bool isCopyAction = item.Kind == SearchResultKind.Clipboard ||
                            (item.Kind == SearchResultKind.Action &&
                             item.ActionId?.StartsWith("copy-calc:", StringComparison.Ordinal) == true);
        ImageSource? nativeSvg = Controls.SearchResultRowControl.ResolveNativeWidgetSvgIcon(item);
        var openItem = new MenuFlyoutItem
        {
            Text = isCopyAction ? _localizationService.T("Common.Copy") : ResolvePrimaryActionVerb(item),
            Icon = nativeSvg is not null
                ? new ImageIcon { Source = nativeSvg }
                : new FontIcon { Glyph = isCopyAction ? "\uE8C8" : "\uE8E5" }
        };
        openItem.Click += (_, _) =>
        {
            if (_viewModel.ExecuteItem(item))
            {
                HidePopup();
            }
        };
        flyout.Items.Add(openItem);

        if (IsQuickTodoCreateAction(item, out string ctxTodoText))
        {
            var openAfterSaveTodoItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Search.QuickCreate.CmdSaveAndOpenTodo"),
                Icon = new ImageIcon { Source = Controls.SearchResultRowControl.GetTodoSvgIcon() }
            };
            openAfterSaveTodoItem.Click += (_, _) =>
            {
                _viewModel.InvokeAction($"quick-todo-open:{ctxTodoText}");
                HidePopup();
            };
            flyout.Items.Add(openAfterSaveTodoItem);
            return flyout;
        }

        if (IsQuickNoteCreateAction(item, out string ctxNoteText))
        {
            var openAfterSaveNoteItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Search.QuickCreate.CmdSaveAndOpenNote"),
                Icon = new ImageIcon { Source = Controls.SearchResultRowControl.GetQuickCaptureSvgIcon() }
            };
            openAfterSaveNoteItem.Click += (_, _) =>
            {
                _viewModel.InvokeAction($"quick-note-open:{ctxNoteText}");
                HidePopup();
            };
            flyout.Items.Add(openAfterSaveNoteItem);
            return flyout;
        }

        bool isFileSystemItem = item.Kind is SearchResultKind.File or SearchResultKind.Folder &&
                                !string.IsNullOrWhiteSpace(item.DetailPath) &&
                                (File.Exists(item.DetailPath) || Directory.Exists(item.DetailPath));
        if (!isFileSystemItem)
        {
            return flyout;
        }

        if (CanAttachItem(item) || CanSaveItem(item))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            if (CanAttachItem(item))
            {
                var attachTodoItem = new MenuFlyoutItem
                {
                    Text = _localizationService.T("Search.Menu.AttachToTodo"),
                    Icon = new ImageIcon { Source = Controls.SearchResultRowControl.GetTodoSvgIcon() }
                };
                attachTodoItem.Click += async (_, _) => await AttachItemToTodoAsync(item);
                flyout.Items.Add(attachTodoItem);
            }

            if (CanSaveItem(item))
            {
                var saveNoteItem = new MenuFlyoutItem
                {
                    Text = _localizationService.T("Search.Menu.SaveToNote"),
                    Icon = new ImageIcon { Source = Controls.SearchResultRowControl.GetQuickCaptureSvgIcon() }
                };
                saveNoteItem.Click += async (_, _) => await SaveItemToNoteAsync(item);
                flyout.Items.Add(saveNoteItem);
            }
        }

        // Executables and administrator-requiring shortcuts keep the same
        // entry point here as the grid items menu (#381, #228).
        if (ElevatedFileLauncher.CanRunAsAdministrator(item.DetailPath!))
        {
            var runAsAdminItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.RunAsAdministrator"),
                Icon = new FontIcon { Glyph = "\uE7EF" }
            };
            runAsAdminItem.Click += (_, _) =>
                QueueRunItemAsAdministrator(item, hideOnLaunch: true);
            flyout.Items.Add(runAsAdminItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopyFileSystemItemAsync(item, DataPackageOperation.Move);
        flyout.Items.Add(cutItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopyFileSystemItemAsync(item, DataPackageOperation.Copy);
        flyout.Items.Add(copyItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            await RenameFileSystemItemAsync(item);
        };
        flyout.Items.Add(renameItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyPathItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.CopyPath"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) => CopyPathToClipboard(item);
        flyout.Items.Add(copyPathItem);

        var openLocationItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Menu.OpenLocation"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        openLocationItem.Click += (_, _) =>
            Services.ShellLaunchService.FireAndForgetRevealInExplorer(item.DetailPath!);
        flyout.Items.Add(openLocationItem);

        var propertiesItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Properties"),
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propertiesItem.Click += (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            ShowFileSystemItemProperties(item);
        };
        flyout.Items.Add(propertiesItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Search.Delete.Action"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) =>
        {
            _restoreResultFocusAfterFlyout = false;
            await DeleteFileSystemItemAsync(item);
        };
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private async Task CopyFileSystemItemAsync(SearchResultItem item, DataPackageOperation operation)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        try
        {
            bool cut = operation == DataPackageOperation.Move &&
                !Platform.DriveMediaInterop.IsProtectedDragOutSource([item.DetailPath]) &&
                !Services.DragDrop.StoreVirtualizedPathPolicy.IsCopyOnlyVirtualizedSource([item.DetailPath]);
            if (!ShellClipboardHelper.TrySetFileDropList([item.DetailPath], cut))
            {
                var data = new DataPackage
                {
                    RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy
                };
                await SetDragPayloadAsync(data, item.DetailPath);
                Clipboard.SetContent(data);
                Clipboard.Flush();
            }

            ShowTransientStatus(_localizationService.T(
                cut
                    ? "Search.Action.CutReady"
                    : "Search.Action.CopyReady"));
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Clipboard operation failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async Task RenameFileSystemItemAsync(SearchResultItem item)
    {
        string path = item.DetailPath ?? string.Empty;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        string initialName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        bool isDirectory = Directory.Exists(path);
        var nameBox = new TextBox
        {
            Text = initialName,
            MinWidth = 320
        };
        DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(nameBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(RootGrid),
            Title = _localizationService.T("Search.Rename.Title"),
            Content = nameBox,
            PrimaryButtonText = _localizationService.T("Common.Rename"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) =>
        {
            DeskBox.Helpers.InlineEditorFocus.FocusWhenLoaded(
                nameBox,
                tb =>
                {
                    int stemLength = !isDirectory && Path.GetFileNameWithoutExtension(tb.Text) is { Length: > 0 } stem
                        ? stem.Length
                        : tb.Text.Length;
                    tb.Select(0, stemLength);
                },
                DispatcherQueue,
                "SearchRenameDialog");
        };
        dialog.Closing += (_, args) =>
        {
            if (DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace())
            {
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowTransientStatus(_localizationService.T("Search.Action.InvalidName"));
            return;
        }

        string? parent = Path.GetDirectoryName(path.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        string targetPath = Path.Combine(parent, newName);
        if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(path))
                {
                    File.Move(path, targetPath);
                }
                else
                {
                    Directory.Move(path, targetPath);
                }
            });
            ShowTransientStatus(_localizationService.T("Search.Action.Renamed"));
            await RefreshResultsAfterFileOperationAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Rename failed: {ex.Message}");
            ShowTransientStatus(_localizationService.T("Search.Action.FileOperationFailed"));
        }
    }

    private async Task DeleteFileSystemItemAsync(SearchResultItem item)
    {
        string path = item.DetailPath ?? string.Empty;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        await DeleteFileSystemItemsCoreAsync(
            [path],
            clearMultiSelectionOnSuccess: false);
    }

    private void ShowFileSystemItemProperties(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        string detailPath = item.DetailPath;
        UiDispatch.PostAfterCallback(async () =>
        {
            try
            {
                IntPtr ownerHwnd = IsPopupVisible && !_isWindowClosed ? _hwnd : IntPtr.Zero;
                if (!await Services.ShellLaunchService.ShowFilePropertiesAsync(ownerHwnd, detailPath))
                {
                    App.Log($"[SearchPopup] Properties failed for '{detailPath}'.");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[SearchPopup] Properties failed: {ex.Message}");
            }
        });
    }

    private async Task RefreshResultsAfterFileOperationAsync()
    {
        await Task.Delay(120);
        await _viewModel.RefreshSearchAsync();
    }

    private bool CanAttachItem(SearchResultItem item) =>
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo) &&
        item.Kind == SearchResultKind.File &&
        !string.IsNullOrWhiteSpace(item.DetailPath) &&
        File.Exists(item.DetailPath);

    private bool CanSaveItem(SearchResultItem item) =>
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture) &&
        item.Kind == SearchResultKind.File &&
        !string.IsNullOrWhiteSpace(item.DetailPath) &&
        File.Exists(item.DetailPath);

    private void TryPreviewSelectedItem()
    {
        var item = _viewModel.SelectedItem;
        if (item is not null)
        {
            _ = PreviewItemAsync(item);
        }
    }

    private async Task PreviewItemAsync(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        bool shown = await _spacePreviewService.TryToggleAsync(item.DetailPath);
        if (!shown)
        {
            App.Log($"[SearchPopup] Space preview unavailable for '{item.DetailPath}'.");
        }
    }

    private async Task AttachItemToTodoAsync(SearchResultItem item)
    {
        var app = CurrentApp;
        var actionService = app?.SearchActionService;
        if (actionService is null)
        {
            return;
        }

        var result = await actionService.AttachFileToTodoWithResultAsync(item.DetailPath);
        ShowTransientStatus(_localizationService.T(
            result.Succeeded ? "Search.Action.AttachedToTodo" : "Search.Action.AttachFailed"));
        if (result.Succeeded && app is not null)
        {
            app.TryShowSearchQuickCreateHudForTodo(
                result.FileName,
                result.WidgetId,
                result.ItemId);
        }
    }

    private async Task SaveItemToNoteAsync(SearchResultItem item)
    {
        var app = CurrentApp;
        var actionService = app?.SearchActionService;
        if (actionService is null)
        {
            return;
        }

        var result = await actionService.SaveFileToNoteWithResultAsync(item.DetailPath);
        ShowTransientStatus(_localizationService.T(
            result.Succeeded ? "Search.Action.SavedToNote" : "Search.Action.SaveFailed"));
        if (result.Succeeded && app is not null)
        {
            app.TryShowSearchQuickCreateHudForNote(
                result.FileName,
                result.ItemId);
        }
    }

    private void CopyPathToClipboard(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(item.DetailPath);
            Clipboard.SetContent(dataPackage);
            ShowTransientStatus(_localizationService.T("Search.Action.PathCopied"));
        }
        catch (Exception ex)
        {
            App.Log($"[SearchPopup] Failed to copy path: {ex.Message}");
        }
    }

    // File-system clipboard and status helpers.

    /// <summary>
    /// Shows a transient status message in the footer, auto-hiding after a delay.
    /// </summary>
    private void ShowTransientStatus(string message)
    {
        SearchFeedbackPresenter.Show(new WidgetFeedbackRequest(
            message,
            WidgetFeedbackSeverity.Info,
            "search-status"));
    }

    private void AnimateResultsFirstExpand()
    {
        if (!AreSystemAnimationsEnabled() ||
            ResultsPanel.Visibility != Visibility.Visible)
        {
            ResultsPanel.Opacity = 1;
            ResultsTranslateTransform.Y = 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var opacityAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(135),
            EasingFunction = ease
        };
        Storyboard.SetTarget(opacityAnim, ResultsPanel);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");

        var slideAnim = new DoubleAnimation
        {
            From = -5.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(145),
            EasingFunction = ease
        };
        Storyboard.SetTarget(slideAnim, ResultsTranslateTransform);
        Storyboard.SetTargetProperty(slideAnim, "Y");

        storyboard.Children.Add(opacityAnim);
        storyboard.Children.Add(slideAnim);
        storyboard.Begin();
    }

    /// <summary>
    /// Populates the drag payload with the result's file or folder, falling back to
    /// the raw path as text when the item cannot be resolved.
    /// </summary>
    private static async Task SetDragPayloadAsync(DataPackage data, string path)
    {
        string resolved = Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(path);
        string targetPath = !string.IsNullOrWhiteSpace(resolved) ? resolved : path;
        try
        {
            if (Directory.Exists(targetPath))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(targetPath);
                data.SetStorageItems(new IStorageItem[] { folder });
                return;
            }

            if (File.Exists(targetPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(targetPath);
                data.SetStorageItems(new IStorageItem[] { file });
                return;
            }
        }
        catch
        {
            // Fall through to a plain-text path payload.
        }

        data.SetText(targetPath);
    }

    private static async Task SetDragPayloadAsync(DataPackage data, List<string> paths)
    {
        var items = new List<IStorageItem>();
        var fallbackText = new StringBuilder();
        foreach (var path in paths)
        {
            string resolved = Services.DragDrop.StoreVirtualizedPathPolicy.ResolveOutboundShellSourcePath(path);
            string targetPath = !string.IsNullOrWhiteSpace(resolved) ? resolved : path;
            try
            {
                if (Directory.Exists(targetPath))
                {
                    items.Add(await StorageFolder.GetFolderFromPathAsync(targetPath));
                }
                else if (File.Exists(targetPath))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(targetPath));
                }
                else
                {
                    fallbackText.AppendLine(targetPath);
                }
            }
            catch
            {
                fallbackText.AppendLine(targetPath);
            }
        }

        if (items.Count > 0)
        {
            data.SetStorageItems(items);
        }
        if (fallbackText.Length > 0 && items.Count == 0)
        {
            data.SetText(fallbackText.ToString().TrimEnd());
        }
    }

    private static SearchResultRowControl? FindItemRow(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is SearchResultRowControl row)
            {
                return row;
            }

            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static SearchResultItem? ResolveResultItem(DependencyObject? element)
    {
        // ItemsRepeater can prepare a row before its inherited DataContext settles.
        // The typed Item bridge is the authoritative projection in that window and
        // remains correct when a row is recycled. DataContext is retained only for
        // non-row surfaces such as recommendation buttons.
        return FindItemRow(element)?.Item ?? FindDataContext<SearchResultItem>(element);
    }

    private static SearchResultRowControl? FindRowByDataContext(DependencyObject root, object data)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is SearchResultRowControl row &&
                (ReferenceEquals(row.Item, data) || ReferenceEquals(row.DataContext, data)))
            {
                return row;
            }

            if (FindRowByDataContext(child, data) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? element) where T : class =>
        SearchRecommendationLayoutEngine.FindDataContext<T>(element);

    private void OnViewModelActionRequested(object? sender, string actionId)
    {
        HidePopup();
        ActionRequested?.Invoke(this, actionId);
    }

    private bool _lastAppliedFileColumnsVisible;

    private bool AreFileColumnsVisible()
    {
        bool showSortHeader = ResultsPanel.Visibility == Visibility.Visible &&
                              _viewModel.SelectedTab?.SupportsFileSort == true &&
                              _settingsService.Settings.Search.SearchShowSortHeader;
        bool detailsVisible = DetailsPaneCard.Visibility == Visibility.Visible;
        return SearchPopupSurfacePolicy.ShouldShowFullFileColumns(
            showSortHeader,
            detailsVisible,
            GetCurrentPopupLogicalWidth());
    }

    private void SyncSortHeaderAndRowColumns(bool forceRefreshRows = false)
    {
        bool fullColumns = AreFileColumnsVisible();
        if (SortHeaderRow is not null)
        {
            SortHeaderTypeCol.Width = fullColumns ? new GridLength(72) : GridLength.Auto;
            SortHeaderSizeCol.Width = fullColumns ? new GridLength(84) : GridLength.Auto;
            SortHeaderDateCol.Width = fullColumns ? new GridLength(106) : GridLength.Auto;
        }

        if (forceRefreshRows || _lastAppliedFileColumnsVisible != fullColumns)
        {
            _lastAppliedFileColumnsVisible = fullColumns;
            RefreshPreparedResultRows();
        }
    }

    private void RefreshPreparedResultRows()
    {
        RefreshPreparedResultRows(ResultsRepeater);
    }

    private void RefreshPreparedResultRows(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is SearchResultRowControl row)
            {
                row.SetFileColumnsVisible(AreFileColumnsVisible());
                row.ApplyQueryHighlight(_viewModel.EffectiveHighlightQuery);
            }

            RefreshPreparedResultRows(child);
        }
    }

    private void UpdateSelectionActions()
    {
        bool hasMulti = _multiSelectedItems.Count > 0;
        bool showBatchBar = hasMulti &&
                            ResultsPanel.Visibility == Visibility.Visible &&
                            StatusBar.Visibility != Visibility.Visible;
        SelectionActionBar.Visibility = showBatchBar ? Visibility.Visible : Visibility.Collapsed;

        bool showEmptyOrNoResults = EmptyTabHintPanel.Visibility == Visibility.Visible ||
                                    NoResultsPanel.Visibility == Visibility.Visible;
        bool showSingleResultCmdBar = !showBatchBar &&
                                      StatusBar.Visibility != Visibility.Visible &&
                                      ((ResultsPanel.Visibility == Visibility.Visible &&
                                        _viewModel.SelectedItem is not null) ||
                                       showEmptyOrNoResults);
        CmdPalResultFooterGrid.Visibility = showSingleResultCmdBar
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecommendationFooterRightPanel.Visibility = showSingleResultCmdBar
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (showSingleResultCmdBar)
        {
            UpdateCommandBarForSelectedItem();
        }

        bool showFooter = RecommendedAppsGrid.IsAppsPanelVisible ||
                          showBatchBar ||
                          StatusBar.Visibility == Visibility.Visible ||
                          showSingleResultCmdBar;
        Visibility targetFooterVisibility = showFooter ? Visibility.Visible : Visibility.Collapsed;
        bool footerChanged = FooterBar.Visibility != targetFooterVisibility;
        FooterBar.Visibility = targetFooterVisibility;

        if (showBatchBar)
        {
            CopySelectedButton.Visibility = Visibility.Visible;
            CutSelectedButton.Visibility = Visibility.Visible;
            DeleteSelectedButton.Visibility = Visibility.Visible;
            CopySelectedLabel.Text = _localizationService.T("Common.Copy");
            CutSelectedLabel.Text = _localizationService.T("Common.Cut");
            DeleteSelectedLabel.Text = _localizationService.T("Search.Delete.Action");
        }

        if (footerChanged && IsPopupVisible && !_isWindowClosed)
        {
            UpdatePopupDynamicBounds();
        }
    }

    // ── Sort headers ────────────────────────────────────────────────────────

    private void SortNameHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Name);
    }

    private void SortTypeHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Type);
    }

    private void SortSizeHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Size);
    }

    private void SortDateHeader_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleSort(ResultSortColumn.Date);
    }

    private void UpdateSortHeaders()
    {
        var column = _viewModel.SortColumn;
        bool ascending = _viewModel.SortAscending;
        SetSortIndicator(SortNameDirection, SortNameLabel, column == ResultSortColumn.Name, ascending);
        SetSortIndicator(SortTypeDirection, SortTypeLabel, column == ResultSortColumn.Type, ascending);
        SetSortIndicator(SortSizeDirection, SortSizeLabel, column == ResultSortColumn.Size, ascending);
        SetSortIndicator(SortDateDirection, SortDateLabel, column == ResultSortColumn.Date, ascending);
    }

    private static void SetSortIndicator(
        FontIcon icon,
        TextBlock label,
        bool active,
        bool ascending)
    {
        icon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        icon.Glyph = ascending ? "\uE74A" : "\uE74B";
        label.FontWeight = active
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        label.Foreground = NeutralInteractionBrush.ResolveThemedResource(
            active
                ? "TextFillColorPrimaryBrush"
                : "TextFillColorTertiaryBrush",
            label);
    }
}
