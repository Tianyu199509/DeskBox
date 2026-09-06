using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private readonly List<DesktopOrganizationPreviewCard> _retainedCards = [];
    private readonly HashSet<string> _retainedSelection = new(StringComparer.OrdinalIgnoreCase);
    private int _retainedLayoutColumns;

    private void ReleaseRetainedCards()
    {
        foreach (var card in _retainedCards) card.Dispose();
        _retainedCards.Clear();
        RetainedRows.Children.Clear();
        RetainedRows.ColumnDefinitions.Clear();
        RetainedRows.RowDefinitions.Clear();
        _retainedLayoutColumns = 0;
    }

    private void RenderExcludedItems(DesktopOrganizationPlan plan)
    {
        ReleaseRetainedCards();
        var retained = GetRetainedPreviewItems();
        var optional = retained.Where(CanIncludeRetainedItem).ToList();
        _retainedSelection.IntersectWith(optional.Select(item => item.SourcePath));
        if (optional.Count > 0)
            AddRetainedCard("optional", T("DesktopOrganization.Layout.OptionalTitle"),
                T("DesktopOrganization.Layout.OptionalHelp"), optional, true, ExclusionDescription);
        var fixedItems = retained.Where(item => !CanIncludeRetainedItem(item)).ToList();
        if (fixedItems.Count > 0)
            AddRetainedCard("fixed", T(_hasCompletedExecution ? "DesktopOrganization.Layout.KeptTitle" : "DesktopOrganization.Layout.FixedTitle"),
                _hasCompletedExecution ? string.Empty : T("DesktopOrganization.Layout.FixedHelp"),
                fixedItems, false, ExclusionDescription);

        if (_runtimeRetainedItems.Count > 0)
        {
            var runtime = _runtimeRetainedItems.ToDictionary(item => item.SourcePath, StringComparer.OrdinalIgnoreCase);
            AddRetainedCard("runtime", T("DesktopOrganization.Result.RetainedDuringRun"), string.Empty,
                _runtimeRetainedItems.Select(item => FindPreviewSnapshot(item.SourcePath, item.Name, item.SourceScope)).ToList(),
                false, item => T("DesktopOrganization.Layout.RetentionHelp." + runtime[item.SourcePath].Reason));
        }
        if (_restoredItems.Count > 0)
            AddRetainedCard("restored", T("Common.Undone"), string.Empty,
                _restoredItems.Values.Select(item => FindPreviewSnapshot(item.SourcePath, item.Name, item.SourceScope) with
                { SourcePath = item.RestoredPath ?? item.SourcePath }).ToList(), false);

        RetainedEmptyText.Text = T("DesktopOrganization.Layout.NoRetained");
        RetainedEmptyText.Visibility = _retainedCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutRetainedCards();
        UpdateRetainedSelection();
    }

    private string ExclusionDescription(DesktopOrganizationFileSnapshot item) =>
        _newPaths.Contains(item.SourcePath) ? T("DesktopOrganization.Layout.NewHelp") :
        T("DesktopOrganization.Exclusion." + item.ExclusionReason);

    private bool CanIncludeRetainedItem(DesktopOrganizationFileSnapshot item) =>
        !_hasCompletedExecution && (item.CanOptIn || item.ExclusionReason == DesktopOrganizationExclusionReason.UserChoice);

    private DesktopOrganizationFileSnapshot FindPreviewSnapshot(string path, string name, DesktopOrganizationSourceScope scope) =>
        (_lastExecutionPlan ?? _plan)?.SourceItems.FirstOrDefault(item =>
            string.Equals(item.SourcePath, path, StringComparison.OrdinalIgnoreCase)) ??
        new DesktopOrganizationFileSnapshot(path, name, Path.GetExtension(path), 0, DateTime.MinValue,
            DesktopOrganizationCategoryIds.Other, null, DesktopOrganizationExclusionReason.Unavailable, SourceScope: scope);

    private void AddRetainedCard(string id, string title, string description,
        IReadOnlyList<DesktopOrganizationFileSnapshot> items, bool canSelect,
        Func<DesktopOrganizationFileSnapshot, string>? itemDescription = null)
    {
        var card = new DesktopOrganizationPreviewCard(id, title, description, items,
            _retainedSelection, canSelect, itemDescription) { VerticalAlignment = VerticalAlignment.Top };
        card.SetNewItems(_newPaths);
        card.SelectionChanged += (_, _) => UpdateRetainedSelection();
        _retainedCards.Add(card);
        RetainedRows.Children.Add(card);
    }

    private void UpdateRetainedSelection()
    {
        IncludeSelectedButton.Content = T("DesktopOrganization.Layout.Include");
        IncludeSelectedButton.IsEnabled = _retainedSelection.Count > 0 && !_isScanning && !_isExecuting && !_hasCompletedExecution;
        RetainedSelectionText.Text = Format("Widget.SelectedCount", _retainedSelection.Count);
        // A contextual command appears with its selection; "0 selected" is noise.
        RetainedSelectionToolbar.Visibility = _retainedSelection.Count > 0 && _showRetained &&
            !_hasCompletedExecution ? Visibility.Visible : Visibility.Collapsed;
    }

    private void IncludeSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null || _plan is null) return;
        var chosen = GetRetainedPreviewItems().Where(item =>
            CanIncludeRetainedItem(item) && _retainedSelection.Contains(item.SourcePath)).ToList();
        if (chosen.Count == 0) return;
        try
        {
            var chosenPaths = chosen.Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in chosen)
            {
                if (item.CanOptIn) _optionalIncludedPaths.Add(item.SourcePath);
                _excludedSourcePaths.Remove(item.SourcePath);
                _newPaths.Remove(item.SourcePath);
            }
            foreach (var target in _plan.Targets.Where(target => target.Items.Any(item => chosenPaths.Contains(item.SourcePath))))
                if (_targetSelections.TryGetValue(target.SourceBucketId, out var selection)) selection.IsSelected = true;
            _plan = Coordinator.CreatePreviewPlanWithOptionalItems(_basePlan, _optionalIncludedPaths,
                _plan.IncludePersonalDesktop, _plan.IncludePublicDesktop);
            // A category can have disappeared while its source was unchecked.
            foreach (var target in _plan.Targets.Where(target => target.Items.Any(item => chosenPaths.Contains(item.SourcePath))))
                if (_targetSelections.TryGetValue(target.SourceBucketId, out var selection)) selection.IsSelected = true;
            _retainedSelection.Clear();
            RenderPlan(_plan);
            ShowSelectionFeedback(Format("DesktopOrganization.Layout.Added", chosen.Count));
        }
        catch (Exception ex)
        {
            App.Log("[DesktopOrganization] Optional preview failed: " + ex);
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
        }
    }

    private void LayoutRetainedCards() => LayoutCards(RetainedRows, _retainedCards, ref _retainedLayoutColumns);
}
