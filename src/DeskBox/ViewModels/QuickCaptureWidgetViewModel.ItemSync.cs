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

        MirrorFolderPath = data.MirrorFolderPath;

        var nonDeletedItems = data.Items
            .Where(item => !item.IsDeleted)
            .ToList();

        var archivedItems = nonDeletedItems
            .Where(item => item.ArchivedAt.HasValue)
            .OrderByDescending(item => item.ArchivedAt ?? item.UpdatedAt)
            .ThenBy(item => item.SortOrder)
            .ToList();

        var activeItems = nonDeletedItems
            .Where(item => !item.ArchivedAt.HasValue)
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        RecordCount = activeItems.Count;
        PinnedCount = activeItems.Count(item => item.IsPinned);
        ArchivedCount = archivedItems.Count;
        OnPropertyChanged(nameof(RecordsTabText));
        OnPropertyChanged(nameof(PinnedTabText));

        if (ShowArchived && ArchivedCount == 0)
        {
            _showArchived = false;
            OnPropertyChanged(nameof(ShowArchived));
            OnPropertyChanged(nameof(HasActiveFilter));
        }

        var sourcePoolForTags = ShowArchived ? archivedItems : activeItems;
        var tagCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (QuickCaptureItem item in sourcePoolForTags)
        {
            foreach (string tag in item.Tags ?? [])
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                if (tagCounts.TryGetValue(tag, out int existing))
                {
                    tagCounts[tag] = existing + 1;
                }
                else
                {
                    tagCounts[tag] = 1;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(ActiveTagFilter) &&
            !sourcePoolForTags.Any(item => ItemMatchesTagFilter(item, ActiveTagFilter)))
        {
            _activeTagFilter = null;
            OnPropertyChanged(nameof(ActiveTagFilter));
            OnPropertyChanged(nameof(HasActiveFilter));
        }

        var chips = new List<QuickCaptureFilterChipDescriptor>(tagCounts.Count + 1);
        foreach (var pair in tagCounts
                     .OrderByDescending(entry => entry.Value)
                     .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
                     .Take(16))
        {
            bool selected = string.Equals(
                ActiveTagFilter,
                pair.Key,
                StringComparison.CurrentCultureIgnoreCase);
            chips.Add(new QuickCaptureFilterChipDescriptor(
                pair.Key,
                $"#{pair.Key}",
                pair.Value,
                selected,
                IsArchiveChip: false));
        }

        if (ArchivedCount > 0)
        {
            chips.Add(new QuickCaptureFilterChipDescriptor(
                "__archived__",
                _localizationService.T("QuickCapture.ArchivedFilter"),
                ArchivedCount,
                ShowArchived,
                IsArchiveChip: true));
        }

        FilterChips = chips;

        bool canShowPinnedSortControls =
            SelectedView == QuickCaptureViewMode.Pinned &&
            !HasSearchText &&
            !HasActiveFilter;

        List<QuickCaptureItem> baseItems;
        if (ShowArchived)
        {
            baseItems = SelectedView == QuickCaptureViewMode.Pinned
                ? archivedItems.Where(item => item.IsPinned).ToList()
                : archivedItems;
        }
        else
        {
            baseItems = SelectedView switch
            {
                QuickCaptureViewMode.Pinned => activeItems
                    .Where(item => item.IsPinned)
                    .OrderBy(item => item.PinnedSortOrder < 0 ? int.MaxValue : item.PinnedSortOrder)
                    .ThenBy(item => item.SortOrder)
                    .ThenByDescending(item => item.UpdatedAt)
                    .ToList(),
                _ => activeItems
            };
        }

        var visibleItems = baseItems;
        if (HasSearchText)
        {
            var searchPool = ShowArchived ? archivedItems : activeItems;
            visibleItems = searchPool
                .Where(item => MatchesSearch(item, SearchText))
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.SortOrder)
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(ActiveTagFilter))
        {
            visibleItems = visibleItems
                .Where(item => ItemMatchesTagFilter(item, ActiveTagFilter))
                .ToList();
        }

        SyncVisibleItems(visibleItems, canShowPinnedSortControls);
        _cachedVisibleItemsSource = null;
        OnPropertyChanged(nameof(VisibleItemsSource));

        UpdateEmptyStateText();
        bool hasItems = Items.Count > 0;
        bool showEmptyAddSurface = !HasSearchText;
        EmptyStateVisibility = hasItems
            ? Visibility.Collapsed
            : Visibility.Visible;
        ListVisibility = hasItems || showEmptyAddSurface
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetViewSwitchLoading(false);
        ItemsViewTransitionToken++;
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
                contentTextSize: ContentTextSize,
                showPinnedSortControls: canShowPinnedSortControls,
                canMovePinnedUp: canMoveUp,
                canMovePinnedDown: canMoveDown);
            _itemViewModelCache[model.Id] = viewModel;
        }

        viewModel.Update(model);
        viewModel.UpdateAppearance(TextSize, IconSize, ContentTextSize);
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
        if (HasSearchText || HasActiveFilter)
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

        EmptyStateTitle = _localizationService.T("QuickCapture.Empty.RecordsTitle");
        EmptyStateText = _localizationService.T("QuickCapture.Empty.RecordsText");
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

        if (keyword.StartsWith('#') && keyword.Length > 1)
        {
            string tagQuery = keyword[1..];
            if ((item.Tags ?? []).Any(tag => tag.Contains(tagQuery, StringComparison.CurrentCultureIgnoreCase)))
            {
                return true;
            }
        }

        bool matchesMetadata = (!string.IsNullOrWhiteSpace(item.Title) &&
                                item.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)) ||
                               (item.Tags ?? []).Any(tag => tag.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));

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
