using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private readonly List<DesktopOrganizationPreviewCard> _targetCards = [];
    private readonly List<DesktopOrganizationPreviewCard> _completedCards = [];
    private int _completedLayoutColumns;
    private readonly Dictionary<string, DesktopOrganizationTargetSelection> _targetSelections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OrganizationHistoryItem> _completedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrganizationHistoryItem> _restoredItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _showRetained;
    private bool _isPreviewReady;
    private double _previewScrollOffset;
    private double _retainedScrollOffset;
    private int _layoutColumns;
    private const double TargetCardGap = 12;

    private void ReleasePreviewCards()
    {
        _isPreviewReady = false;
        ExecuteButton.IsEnabled = false;
        foreach (var card in _targetCards)
        {
            if (card.IsExpanded) _expandedTargets.Add(card.SourceBucketId);
            else _expandedTargets.Remove(card.SourceBucketId);
            card.Dispose();
        }
        _targetCards.Clear();
        TargetRows.Children.Clear();
        TargetRows.ColumnDefinitions.Clear();
        TargetRows.RowDefinitions.Clear();
        _layoutColumns = 0;
    }

    private void RenderPlan(DesktopOrganizationPlan plan)
    {
        double offset = PreviewScrollViewer.VerticalOffset;
        ReleasePreviewCards();
        StoragePathText.Text = plan.StorageRootPath;
        RenderSources(plan);
        var destinations = CreateDestinationOptions();
        foreach (var target in plan.Targets)
        {
            if (!_targetSelections.TryGetValue(target.SourceBucketId, out var selection))
            {
                selection = new DesktopOrganizationTargetSelection
                {
                    SourceBucketId = target.SourceBucketId,
                    IsSelected = true,
                    DestinationMode = target.CreatesWidget ? DesktopOrganizationDestinationMode.Dynamic : DesktopOrganizationDestinationMode.ExistingWidget,
                    ExistingWidgetId = target.CreatesWidget ? null : target.TargetWidgetId
                };
                _targetSelections[target.SourceBucketId] = selection;
            }
            if (selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget &&
                !destinations.Any(item => item.Id == selection.ExistingWidgetId))
            {
                selection.DestinationMode = target.CreatesWidget ? DesktopOrganizationDestinationMode.Dynamic : DesktopOrganizationDestinationMode.ExistingWidget;
                selection.ExistingWidgetId = target.CreatesWidget ? null : target.TargetWidgetId;
                _excludedSourcePaths.UnionWith(target.Items.Select(item => item.SourcePath));
                ShowSelectionFeedback(T("DesktopOrganization.Layout.DestinationReset"), showPendingLink: false);
            }
            selection.IsSelected = target.Items.Any(item => !_excludedSourcePaths.Contains(item.SourcePath));
            var card = new DesktopOrganizationPreviewCard(target, selection, _excludedSourcePaths, destinations)
            {
                VerticalAlignment = VerticalAlignment.Top,
                IsExpanded = _expandedTargets.Contains(target.SourceBucketId)
            };
            card.SetNewItems(_newPaths);
            card.SelectionChanged += (_, _) =>
            {
                UpdateSummary(_plan);
                UpdateDestinationPaths();
                if (_showRetained && _plan is not null) RenderExcludedItems(_plan);
                LayoutTargetCards();
            };
            _targetCards.Add(card);
            TargetRows.Children.Add(card);
        }
        UpdateSummary(plan);
        RenderExcludedItems(plan);
        UpdateSectionVisibility();
        LayoutTargetCards();
        UpdateDestinationPaths();
        _isPreviewReady = true;
        UpdateSummary(plan);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded) PreviewScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        });
    }

    private IReadOnlyList<DesktopOrganizationDestinationOption> CreateDestinationOptions()
    {
        try { return Coordinator.GetDestinationOptions(); }
        catch { return []; }
    }

    private IReadOnlyList<DesktopOrganizationFileSnapshot> GetRetainedPreviewItems()
    {
        if (_hasCompletedExecution && _lastExecutionPlan is not null)
            return _lastExecutionPlan.ExcludedItems.Where(item =>
                DesktopOrganizationPreviewSummary.IncludesSource(_lastExecutionPlan, item)).ToList();
        if (_plan is null) return [];
        var sources = _plan.SourceItems.Count > 0 ? _plan.SourceItems
            : _plan.Targets.SelectMany(target => target.Items).Concat(_plan.ExcludedItems).ToList();
        var selected = _plan.Targets.Where(target =>
                !_targetSelections.TryGetValue(target.SourceBucketId, out var selection) || selection.IsSelected)
            .SelectMany(target => target.Items)
            .Where(item => !_excludedSourcePaths.Contains(item.SourcePath))
            .Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sources.Where(item => DesktopOrganizationPreviewSummary.IncludesSource(_plan, item) &&
            !selected.Contains(item.SourcePath)).Select(item =>
        {
            return item.IsEligible || _optionalIncludedPaths.Contains(item.SourcePath)
                    ? item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice } : item;
        }).DistinctBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void PreviewSections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_plan is null) return;
        SelectSection(sender.SelectedItem == ExcludedItemsButton);
    }

    private void SelectSection(bool retained)
    {
        if (_showRetained == retained) return;
        if (_showRetained) _retainedScrollOffset = PreviewScrollViewer.VerticalOffset;
        else _previewScrollOffset = PreviewScrollViewer.VerticalOffset;
        _showRetained = retained;
        if (_plan is not null && retained) RenderExcludedItems(_plan);
        UpdateSectionVisibility();
        UpdateSummary(_plan);
        LayoutTargetCards();
        double offset = retained ? _retainedScrollOffset : _previewScrollOffset;
        DispatcherQueue.TryEnqueue(() => PreviewScrollViewer.ChangeView(null, offset, null, disableAnimation: true));
    }

    private void UpdateSectionVisibility()
    {
        TargetSelectionHost.Visibility = !_showRetained && !_hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
        RetainedItemsPanel.Visibility = _showRetained ? Visibility.Visible : Visibility.Collapsed;
        CompletedItemsPanel.Visibility = !_showRetained && _hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreviewText.Visibility = !_showRetained && !_hasCompletedExecution && _plan is { EligibleItemCount: 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyPreviewText.Text = T(_plan is { IncludePersonalDesktop: false, IncludePublicDesktop: false }
            ? "DesktopOrganization.Layout.NoSource" : GetRetainedPreviewItems().Any(CanIncludeRetainedItem)
            ? "DesktopOrganization.Layout.NoEligible" : "DesktopOrganization.Preview.NothingAction");
        PreviewSections.SelectedItem = _showRetained ? ExcludedItemsButton : SummaryTitle;
        RetainedSelectionToolbar.Visibility = _showRetained && !_hasCompletedExecution && _retainedSelection.Count > 0 &&
            GetRetainedPreviewItems().Any(CanIncludeRetainedItem) ? Visibility.Visible : Visibility.Collapsed;
        ExecuteButton.Visibility = !_hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
        ResetSelectionMenuItem.IsEnabled = !_hasCompletedExecution && !_isExecuting && !_isScanning;
    }

    private void UpdateSummary(DesktopOrganizationPlan? plan)
    {
        if (plan is null) return;
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, _targetSelections, _excludedSourcePaths);
        int count = _hasCompletedExecution ? _completedItems.Count : summary.SelectedCount;
        SummaryTitle.Text = Format(_hasCompletedExecution ? "DesktopOrganization.Layout.CompletedCount" : "DesktopOrganization.Layout.MoveCount", count);
        SummaryDescription.Text = Format("DesktopOrganization.Layout.TotalCount", summary.TotalCount);
        ExcludedItemsButton.Text = Format("DesktopOrganization.Layout.RetainedCount", Math.Max(0, summary.TotalCount - count));
        FooterSummaryText.Text = _hasCompletedExecution
            ? T("DesktopOrganization.Preview.Completed")
            : !plan.IncludePersonalDesktop && !plan.IncludePublicDesktop ? T("DesktopOrganization.Layout.NoSource")
            : plan.EligibleItemCount == 0 ? T("DesktopOrganization.Preview.NothingAction")
            : summary.SelectedCount == 0 ? T("DesktopOrganization.Layout.NoSelection")
            : Format("DesktopOrganization.Layout.TargetSummary", summary.DestinationCount, summary.NewDestinationCount);
        ExecuteButton.Content = Format("DesktopOrganization.Layout.Execute", summary.SelectedCount);
        bool pendingUndo = App.Current.SettingsService.Settings.RecentOrganizationHistory.Any(entry =>
            entry.ActionType == OrganizationActionType.DesktopOrganization && entry.UndoStarted && entry.CanUndo);
        ExecuteButton.IsEnabled = _isPreviewReady && summary.SelectedCount > 0 && !_isScanning && !_isExecuting && !_hasCompletedExecution &&
            !pendingUndo && !HasPendingRecovery;
        UpdateRetainedSelection();
    }

    private void RenderExecutionResult(OrganizationHistoryEntry history)
    {
        foreach (var item in history.Items)
        {
            if (item.IsRestored)
            {
                _completedItems.Remove(item.SourcePath);
                _restoredItems[item.SourcePath] = item;
            }
            else
            {
                _completedItems[item.SourcePath] = item;
                _restoredItems.Remove(item.SourcePath);
            }
        }
        ReleaseCompletedCards();
        foreach (var group in _completedItems.Values.GroupBy(item => item.TargetWidgetId))
        {
            var items = group.Select(item => FindPreviewSnapshot(item.SourcePath, item.Name, item.SourceScope) with
            { SourcePath = item.DestinationPath, Name = Path.GetFileName(item.DestinationPath) }).ToList();
            var card = new DesktopOrganizationPreviewCard(group.Key, group.First().TargetWidgetName,
                string.Empty, items, [], false, item => item.SourcePath)
            { VerticalAlignment = VerticalAlignment.Top };
            _completedCards.Add(card);
            CompletedItemsPanel.Children.Add(card);
        }
        ReleasePreviewCards();
        RenderExcludedItems(_lastExecutionPlan ?? _plan!);
        UpdateSummary(_plan);
        UpdateSectionVisibility();
        UpdateDestinationPaths();
        LayoutTargetCards();
    }

    private void ReleaseCompletedCards()
    {
        foreach (var card in _completedCards) card.Dispose();
        _completedCards.Clear();
        CompletedItemsPanel.Children.Clear();
        CompletedItemsPanel.ColumnDefinitions.Clear();
        CompletedItemsPanel.RowDefinitions.Clear();
        _completedLayoutColumns = 0;
    }

    private void ShowSelectionFeedback(string text, bool showPendingLink = true)
    {
        SelectionFeedbackInfo.Message = text;
        ViewPendingButton.Visibility = showPendingLink ? Visibility.Visible : Visibility.Collapsed;
        SelectionFeedbackInfo.IsOpen = true;
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(SelectionFeedbackInfo)?
            .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void ViewPendingButton_Click(object sender, RoutedEventArgs e)
    {
        SelectSection(false);
        SelectionFeedbackInfo.IsOpen = false;
    }

    private void UpdateDestinationPaths()
    {
        DestinationPathsPanel.Children.Clear();
        if (_plan is null) return;
        var destinations = CreateDestinationOptions();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in (_hasCompletedExecution ? _lastExecutionPlan : _plan)?.Targets ?? [])
        {
            _targetSelections.TryGetValue(target.SourceBucketId, out var selection);
            var destination = !_hasCompletedExecution && selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                ? destinations.FirstOrDefault(item => item.Id == selection.ExistingWidgetId) : null;
            string path = destination?.DirectoryPath ?? target.TargetDirectoryPath;
            if (!seen.Add(path)) continue;
            DestinationPathsPanel.Children.Add(new TextBlock
            {
                Text = (destination?.DisplayName ?? target.SuggestedDisplayName) + "\n" + path,
                Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
                FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
            });
        }
    }

    private void TargetRows_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();
    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();
    private void PreviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();

    private void LayoutTargetCards()
    {
        LayoutCards(TargetRows, _targetCards, ref _layoutColumns);
        LayoutRetainedCards();
        LayoutCards(CompletedItemsPanel, _completedCards, ref _completedLayoutColumns);
    }

    private void LayoutCards(Grid rows, IReadOnlyList<DesktopOrganizationPreviewCard> cards, ref int layoutColumns)
    {
        if (cards.Count == 0) return;
        double width = PreviewScrollViewer.ViewportWidth;
        if (!double.IsFinite(width) || width <= 0) width = PreviewViewport.ActualWidth;
        if (!double.IsFinite(width) || width <= 0) return;
        if (!double.IsFinite(rows.Width) || Math.Abs(rows.Width - width) > .5) rows.Width = width;
        double available = Math.Max(1, width - rows.Padding.Left - rows.Padding.Right);
        double minimum = cards.Max(card => card.MinimumPreviewWidth);
        int columns = Math.Max(1, Math.Min(Math.Min(3, cards.Count),
            (int)Math.Floor((available + TargetCardGap) / (minimum + TargetCardGap))));
        double cardWidth = Math.Max(1, (available - (columns - 1) * TargetCardGap) / columns);
        if (layoutColumns != columns)
        {
            layoutColumns = columns;
            rows.ColumnDefinitions.Clear();
            rows.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++)
                rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < (int)Math.Ceiling(cards.Count / (double)columns); i++)
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int i = 0; i < cards.Count; i++)
        {
            Grid.SetColumn(cards[i], i % columns);
            Grid.SetRow(cards[i], i / columns);
            if (!double.IsFinite(cards[i].Width) || Math.Abs(cards[i].Width - cardWidth) > .5)
                cards[i].Width = cardWidth;
        }
    }
}
