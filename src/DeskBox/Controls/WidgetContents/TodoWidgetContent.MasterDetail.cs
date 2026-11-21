using DeskBox.Controls;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private readonly MasterDetailLayoutPolicy _masterDetailPolicy = new();
    private bool _isDualPane;
    private bool _wideSelectionWasAutomatic;
    private bool _isEnsuringWideDetailSelection;
    private double? _masterPaneWidth;

    internal bool IsDualPane => _isDualPane;

    internal bool WideSelectionWasAutomatic
    {
        get => _wideSelectionWasAutomatic;
        set => _wideSelectionWasAutomatic = value;
    }

    private void TodoWidgetContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInteractiveResizeActive)
        {
            return;
        }

        ApplyMasterDetailLayout(e.NewSize.Width);
        QueueDetailTitleHeightUpdate();
        QueueTodoSegmentedRestore();
    }

    private void ApplyMasterDetailLayout(double totalWidth)
    {
        double availableWidth = Math.Max(
            0,
            totalWidth - RootGrid.Padding.Left - RootGrid.Padding.Right);
        _masterPaneWidth ??= ViewModel?.PreferredMasterPaneWidth ??
            _masterDetailPolicy.Options.DefaultMasterWidth;
        MasterDetailLayoutSnapshot snapshot = _masterDetailPolicy.Resolve(
            availableWidth,
            _isDualPane,
            _masterPaneWidth,
            ViewModel?.LayoutPreference ?? MasterDetailLayoutPreference.Auto);
        bool enteringDualPane = !_isDualPane && snapshot.IsDualPane;
        bool leavingDualPane = _isDualPane && !snapshot.IsDualPane;
        _isDualPane = snapshot.IsDualPane;

        if (_isDualPane)
        {
            bool allowCompressedPanes =
                ViewModel?.LayoutPreference == MasterDetailLayoutPreference.DualPane &&
                availableWidth < _masterDetailPolicy.Options.MinimumMasterWidth +
                    _masterDetailPolicy.Options.SplitterWidth +
                    _masterDetailPolicy.Options.MinimumDetailWidth;
            if (!allowCompressedPanes)
            {
                _masterPaneWidth = snapshot.MasterWidth;
            }

            MasterColumn.MinWidth = allowCompressedPanes
                ? 0
                : _masterDetailPolicy.Options.MinimumMasterWidth;
            DetailColumn.MinWidth = allowCompressedPanes
                ? 0
                : _masterDetailPolicy.Options.MinimumDetailWidth;
            MasterColumn.Width = new GridLength(snapshot.MasterWidth);
            SplitterColumn.Width = new GridLength(snapshot.SplitterWidth);
            DetailColumn.Width = new GridLength(snapshot.DetailWidth);
            MasterDetailSplitter.Visibility = Visibility.Visible;
            Grid.SetColumn(ListHeaderArea, 0);
            Grid.SetColumn(ListArea, 0);
            Grid.SetColumn(ListFooterArea, 0);
            Grid.SetColumnSpan(ListHeaderArea, 1);
            Grid.SetColumnSpan(ListArea, 1);
            Grid.SetColumnSpan(ListFooterArea, 1);
            if (DetailPage is not null)
            {
                Grid.SetColumn(DetailPage, 2);
                Grid.SetColumnSpan(DetailPage, 1);
                DetailBackColumn.Width = new GridLength(0);
                DetailBackButton.Visibility = Visibility.Collapsed;
            }

            if (enteringDualPane)
            {
                ResetDetailTransition();
                EnsureWideDetailSelection();
            }
        }
        else
        {
            MasterColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            MasterColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
            MasterDetailSplitter.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ListHeaderArea, 0);
            Grid.SetColumn(ListArea, 0);
            Grid.SetColumn(ListFooterArea, 0);
            Grid.SetColumnSpan(ListHeaderArea, 3);
            Grid.SetColumnSpan(ListArea, 3);
            Grid.SetColumnSpan(ListFooterArea, 3);
            if (DetailPage is not null)
            {
                Grid.SetColumn(DetailPage, 0);
                Grid.SetColumnSpan(DetailPage, 3);
                DetailBackColumn.Width = new GridLength(30);
                DetailBackButton.Visibility = Visibility.Visible;
            }

            if (leavingDualPane && _wideSelectionWasAutomatic &&
                ViewModel?.IsCreatingDetailItem != true)
            {
                ViewModel?.CloseDetail();
                _wideSelectionWasAutomatic = false;
            }
        }

        ApplyMasterDetailVisibility();
    }

    private void ApplyMasterDetailVisibility()
    {
        bool hasDetail = ViewModel?.SelectedDetailItem is not null;
        bool showList = _isDualPane || !hasDetail;
        ListHeaderArea.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        ListArea.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        ListFooterArea.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        if (hasDetail && EnsureDetailPageRealized())
        {
            DetailPage.Visibility = Visibility.Visible;
        }
        else if (DetailPage is not null)
        {
            DetailPage.Visibility = Visibility.Collapsed;
        }
        WideDetailEmptyState.Visibility = _isDualPane && !hasDetail
            ? Visibility.Visible
            : Visibility.Collapsed;
        MasterDetailSplitter.Visibility = _isDualPane
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showList)
        {
            QueueTodoSegmentedRestore();
        }
        else
        {
            SuspendTodoSegmented();
        }
    }

    private void EnsureWideDetailSelection() => _ = EnsureWideDetailSelectionAsync();

    private async Task EnsureWideDetailSelectionAsync()
    {
        if (_isEnsuringWideDetailSelection ||
            _isChangingTodoFilter ||
            !_isDualPane ||
            ViewModel is null)
        {
            return;
        }

        _isEnsuringWideDetailSelection = true;
        try
        {
            if (ViewModel.SelectedDetailItem is { } selected &&
                (ViewModel.IsCreatingDetailItem || ViewModel.VisibleItems.Contains(selected)))
            {
                return;
            }

            if (ViewModel.SelectedDetailItem is not null)
            {
                if (!await PrepareForDetailSelectionChangeAsync(nextItemId: null))
                {
                    ApplyMasterDetailVisibility();
                    return;
                }

                ViewModel.CloseDetail();
            }

            if (!ViewModel.AutoSelectFirstInWideLayout ||
                ViewModel.VisibleItems.FirstOrDefault() is not TodoItemViewModel first)
            {
                _wideSelectionWasAutomatic = false;
                ApplyMasterDetailVisibility();
                return;
            }

            if (ViewModel.OpenDetail(first.Id) is not null)
            {
                _wideSelectionWasAutomatic = true;
            }

            ApplyMasterDetailVisibility();
        }
        finally
        {
            _isEnsuringWideDetailSelection = false;
        }
    }

    private void MarkDetailSelectionExplicit() => _wideSelectionWasAutomatic = false;

    private void MasterDetailSplitter_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e) =>
        CaptureAndPersistMasterPaneWidth();

    private void MasterDetailSplitter_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            CaptureAndPersistMasterPaneWidth();
        }
    }

    private void MasterDetailSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isDualPane)
        {
            return;
        }

        _masterPaneWidth = _masterDetailPolicy.Options.DefaultMasterWidth;
        ApplyMasterDetailLayout(ActualWidth);
        ViewModel?.PersistMasterPaneWidth(_masterPaneWidth.Value);
        e.Handled = true;
    }

    private void CaptureAndPersistMasterPaneWidth()
    {
        if (!_isDualPane || !double.IsFinite(MasterColumn.ActualWidth))
        {
            return;
        }

        double masterWidth = MasterColumn.ActualWidth;
        double minimumDualWidth = _masterDetailPolicy.Options.MinimumMasterWidth +
                                  _masterDetailPolicy.Options.SplitterWidth +
                                  _masterDetailPolicy.Options.MinimumDetailWidth;
        double availableWidth = Math.Max(
            0,
            ActualWidth - RootGrid.Padding.Left - RootGrid.Padding.Right);
        if (ViewModel?.LayoutPreference == MasterDetailLayoutPreference.DualPane &&
            availableWidth < minimumDualWidth)
        {
            double combinedPaneWidth = MasterColumn.ActualWidth + DetailColumn.ActualWidth;
            if (combinedPaneWidth > 0)
            {
                double masterRatio = Math.Clamp(
                    MasterColumn.ActualWidth / combinedPaneWidth,
                    0.01,
                    0.99);
                masterWidth = _masterDetailPolicy.Options.MinimumDetailWidth *
                              masterRatio /
                              (1 - masterRatio);
            }
        }

        _masterPaneWidth = _masterDetailPolicy.NormalizePersistedMasterWidth(masterWidth);
        ViewModel?.PersistMasterPaneWidth(_masterPaneWidth.Value);
    }
}
