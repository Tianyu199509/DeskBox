using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureWidgetViewModel
{
    private Task RefreshFromDataAsync(QuickCaptureStoreData data)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        var activeItems = data.Items
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();
        var recentItems = data.RecentItems
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        RecordCount = activeItems.Count;
        PinnedCount = activeItems.Count(item => item.IsPinned);
        RecentCount = recentItems.Count;
        OnPropertyChanged(nameof(RecordsTabText));
        OnPropertyChanged(nameof(PinnedTabText));
        OnPropertyChanged(nameof(RecentTabText));

        bool canShowPinnedSortControls = SelectedView == QuickCaptureViewMode.Pinned && !HasSearchText;
        var visibleItems = SelectedView switch
        {
            QuickCaptureViewMode.Pinned => activeItems
                .Where(item => item.IsPinned)
                .OrderBy(item => item.PinnedSortOrder < 0 ? int.MaxValue : item.PinnedSortOrder)
                .ThenBy(item => item.SortOrder)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList(),
            QuickCaptureViewMode.Recent => recentItems,
            _ => activeItems
        };

        if (HasSearchText)
        {
            visibleItems = activeItems
                .Concat(recentItems)
                .Where(item => MatchesSearch(item, SearchText))
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.IsRecent ? 1 : 0)
                .ThenBy(item => item.SortOrder)
                .ToList();
        }

        SyncVisibleItems(visibleItems, canShowPinnedSortControls);

        UpdateEmptyStateText();
        bool hasItems = Items.Count > 0;
        bool showEmptyRecordsAddSurface = IsRecordsView && !HasSearchText;
        EmptyStateVisibility = hasItems || showEmptyRecordsAddSurface
            ? Visibility.Collapsed
            : Visibility.Visible;
        ListVisibility = hasItems || showEmptyRecordsAddSurface
            ? Visibility.Visible
            : Visibility.Collapsed;
        OnPropertyChanged(nameof(RecentCaptureStatusText));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        ItemsViewTransitionToken++;
        SetViewSwitchLoading(false);
        return Task.CompletedTask;
    }

    private void SyncVisibleItems(IReadOnlyList<QuickCaptureItem> visibleItems, bool canShowPinnedSortControls)
    {
        var existingById = Items
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visibleIds = visibleItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        PruneItemViewModelCache(visibleItems);

        int existingVisibleOverlap = Items.Count(item => visibleIds.Contains(item.Id));
        bool shouldRebuildCollection =
            Items.Count == 0 ||
            visibleItems.Count == 0 ||
            existingVisibleOverlap < Math.Min(Items.Count, visibleItems.Count) / 2;

        if (shouldRebuildCollection)
        {
            foreach (var item in Items)
            {
                item.IsCopySelected = false;
            }

            Items.Clear();
            for (int targetIndex = 0; targetIndex < visibleItems.Count; targetIndex++)
            {
                Items.Add(GetOrCreateVisibleItemViewModel(
                    visibleItems[targetIndex],
                    targetIndex,
                    visibleItems.Count,
                    canShowPinnedSortControls,
                    existingById));
            }

            return;
        }

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!visibleIds.Contains(Items[index].Id))
            {
                Items[index].IsCopySelected = false;
                Items.RemoveAt(index);
            }
        }

        var currentIndexById = new Dictionary<string, int>(Items.Count, StringComparer.Ordinal);
        for (int i = 0; i < Items.Count; i++)
        {
            currentIndexById[Items[i].Id] = i;
        }

        for (int targetIndex = 0; targetIndex < visibleItems.Count; targetIndex++)
        {
            var model = visibleItems[targetIndex];
            var viewModel = GetOrCreateVisibleItemViewModel(
                model,
                targetIndex,
                visibleItems.Count,
                canShowPinnedSortControls,
                existingById);

            if (!currentIndexById.TryGetValue(viewModel.Id, out int currentIndex))
            {
                Items.Insert(targetIndex, viewModel);
                foreach (var key in currentIndexById.Keys)
                {
                    if (currentIndexById[key] >= targetIndex)
                    {
                        currentIndexById[key]++;
                    }
                }
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
                int lo = Math.Min(currentIndex, targetIndex);
                int hi = Math.Max(currentIndex, targetIndex);
                for (int i = lo; i <= hi; i++)
                {
                    currentIndexById[Items[i].Id] = i;
                }
            }
        }
    }

    private QuickCaptureItemViewModel GetOrCreateVisibleItemViewModel(
        QuickCaptureItem model,
        int index,
        int totalCount,
        bool canShowPinnedSortControls,
        IReadOnlyDictionary<string, QuickCaptureItemViewModel> existingById)
    {
        bool canMoveUp = canShowPinnedSortControls && index > 0;
        bool canMoveDown = canShowPinnedSortControls && index < totalCount - 1;

        if (!existingById.TryGetValue(model.Id, out var viewModel) &&
            !_itemViewModelCache.TryGetValue(model.Id, out viewModel))
        {
            viewModel = new QuickCaptureItemViewModel(
                model,
                _localizationService,
                TextSize,
                IconSize,
                SearchText,
                showPinnedSortControls: canShowPinnedSortControls,
                canMovePinnedUp: canMoveUp,
                canMovePinnedDown: canMoveDown);
            _itemViewModelCache[model.Id] = viewModel;
        }

        viewModel.Update(model);
        viewModel.UpdateAppearance(TextSize, IconSize);
        viewModel.UpdateSearchText(SearchText);
        viewModel.UpdatePinnedSortState(canShowPinnedSortControls, canMoveUp, canMoveDown);
        return viewModel;
    }

    private void PruneItemViewModelCache(IReadOnlyList<QuickCaptureItem> visibleItems)
    {
        if (_cachedData is null && _itemViewModelCache.Count == 0)
        {
            return;
        }

        HashSet<string> retainedIds = _cachedData is { } data
            ? data.Items
                .Concat(data.RecentItems)
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal)
            : visibleItems
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);

        foreach (string id in _itemViewModelCache.Keys.Where(id => !retainedIds.Contains(id)).ToList())
        {
            _itemViewModelCache.Remove(id);
        }
    }

    private void UpdateEmptyStateText()
    {
        if (HasSearchText)
        {
            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.SearchTitle");
            EmptyStateText = _localizationService.T("QuickCapture.Empty.SearchText");
            return;
        }

        if (SelectedView == QuickCaptureViewMode.Pinned)
        {
            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.PinnedTitle");
            EmptyStateText = _localizationService.T("QuickCapture.Empty.PinnedText");
            return;
        }

        if (SelectedView == QuickCaptureViewMode.Recent)
        {
            if (!IsRecentCaptureEnabled())
            {
                EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecentDisabledTitle");
                EmptyStateText = _localizationService.T("QuickCapture.Empty.RecentDisabledText");
                return;
            }

            EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecentTitle");
            EmptyStateText = _settingsService.Settings.QuickCaptureImageClipboardEnabled
                ? _localizationService.T("QuickCapture.Empty.RecentTextWithImages")
                : _localizationService.T("QuickCapture.Empty.RecentText");
            return;
        }

        EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecordsTitle");
        EmptyStateText = _localizationService.T("QuickCapture.Empty.RecordsText");
    }

    private bool IsRecentCaptureEnabled()
    {
        return _settingsService.Settings.QuickCaptureClipboardEnabled;
    }

    private bool MatchesSearch(QuickCaptureItem item, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        string keyword = searchText.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return true;
        }

        bool matchesMetadata = (!string.IsNullOrWhiteSpace(item.Title) &&
                                item.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)) ||
                               item.Tags.Any(tag => tag.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

        if (item.Type == QuickCaptureItemType.Image)
        {
            return matchesMetadata ||
                   "Image".Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                   _localizationService.T("QuickCapture.ImageItem").Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
        }

        return matchesMetadata ||
               item.Body.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (item.Url?.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }
}
