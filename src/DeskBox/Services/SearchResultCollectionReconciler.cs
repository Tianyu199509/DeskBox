using System.Collections.ObjectModel;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Applies search-result changes without resetting the whole observable collection.
/// Keeping unchanged item instances stable lets ItemsRepeater retain realized rows,
/// selection visuals, and already-resolved shell icons across background refreshes.
/// </summary>
internal static class SearchResultCollectionReconciler
{
    public static bool HasSameIdentitySequence(
        IReadOnlyList<SearchResultItem> current,
        IReadOnlyList<SearchResultItem> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!string.Equals(
                    SearchResultRanker.GetIdentityKey(current[index]),
                    SearchResultRanker.GetIdentityKey(incoming[index]),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reuses existing instances for unchanged identities. This is intentionally used
    /// for background index refreshes only: a user-entered query still receives the
    /// latest result instances from its providers.
    /// </summary>
    public static List<SearchResultItem> ReuseExistingInstances(
        IReadOnlyList<SearchResultItem> current,
        IReadOnlyList<SearchResultItem> incoming)
    {
        var existingByIdentity = new Dictionary<string, SearchResultItem>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SearchResultItem item in current)
        {
            // Search responses are ranked and de-duplicated, but retaining the last
            // instance is still safer than throwing if a transient provider happens
            // to surface the same identity twice.
            existingByIdentity[SearchResultRanker.GetIdentityKey(item)] = item;
        }
        var merged = new List<SearchResultItem>(incoming.Count);

        foreach (SearchResultItem item in incoming)
        {
            if (existingByIdentity.TryGetValue(
                    SearchResultRanker.GetIdentityKey(item),
                    out SearchResultItem? existing))
            {
                if (HasSameDisplayPresentation(existing, item))
                {
                    // These values influence result ordering and the secondary columns.
                    // Preserve the resolved icon and other lazily loaded metadata on the
                    // stable instance instead of making every matching row start over.
                    existing.ModifiedAt = item.ModifiedAt;
                    existing.RelevanceScore = item.RelevanceScore;
                    existing.TypeDisplay = item.TypeDisplay ?? existing.TypeDisplay;
                    if (item.FileSize.HasValue)
                    {
                        existing.FileSize = item.FileSize;
                    }

                    if (!string.IsNullOrEmpty(item.SizeDisplay))
                    {
                        existing.SizeDisplay = item.SizeDisplay;
                    }

                    if (!string.IsNullOrEmpty(item.DateDisplay))
                    {
                        existing.DateDisplay = item.DateDisplay;
                    }

                    merged.Add(existing);
                }
                else
                {
                    item.Icon ??= existing.Icon;
                    item.IconResolved |= existing.IconResolved;
                    item.CreatedAt ??= existing.CreatedAt;
                    item.FileSize ??= existing.FileSize;
                    item.SizeDisplay ??= existing.SizeDisplay;
                    item.DateDisplay ??= existing.DateDisplay;
                    item.TypeDisplay ??= existing.TypeDisplay;
                    item.RecommendationKey ??= existing.RecommendationKey;
                    merged.Add(item);
                }
            }
            else
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static bool HasSameDisplayPresentation(SearchResultItem existing, SearchResultItem incoming) =>
        string.Equals(existing.Title, incoming.Title, StringComparison.Ordinal) &&
        string.Equals(existing.Subtitle, incoming.Subtitle, StringComparison.Ordinal) &&
        string.Equals(existing.Glyph, incoming.Glyph, StringComparison.Ordinal) &&
        existing.TodoIsCompleted == incoming.TodoIsCompleted &&
        string.Equals(existing.PreviewText, incoming.PreviewText, StringComparison.Ordinal) &&
        string.Equals(existing.PreviewImagePath, incoming.PreviewImagePath, StringComparison.Ordinal) &&
        string.Equals(existing.PreviewSourceApp, incoming.PreviewSourceApp, StringComparison.Ordinal) &&
        existing.PreviewCharCount == incoming.PreviewCharCount &&
        existing.PreviewLineCount == incoming.PreviewLineCount;

    /// <summary>
    /// Brings <paramref name="current"/> in line with <paramref name="target"/>
    /// using granular collection notifications. It never raises a Reset notification.
    /// </summary>
    public static bool Reconcile(
        ObservableCollection<SearchResultItem> current,
        IReadOnlyList<SearchResultItem> target)
    {
        bool changed = false;

        for (int index = 0; index < target.Count; index++)
        {
            SearchResultItem desired = target[index];
            if (index < current.Count && ReferenceEquals(current[index], desired))
            {
                continue;
            }

            int existingIndex = FindReferenceIndex(current, desired, index + 1);
            if (existingIndex >= 0)
            {
                current.Move(existingIndex, index);
                changed = true;
                continue;
            }

            if (index < current.Count && HasSameIdentity(current[index], desired))
            {
                current[index] = desired;
            }
            else
            {
                current.Insert(index, desired);
            }

            changed = true;
        }

        while (current.Count > target.Count)
        {
            current.RemoveAt(current.Count - 1);
            changed = true;
        }

        return changed;
    }

    private static int FindReferenceIndex(
        IReadOnlyList<SearchResultItem> items,
        SearchResultItem desired,
        int startIndex)
    {
        for (int index = startIndex; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], desired))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasSameIdentity(SearchResultItem left, SearchResultItem right) =>
        string.Equals(
            SearchResultRanker.GetIdentityKey(left),
            SearchResultRanker.GetIdentityKey(right),
            StringComparison.OrdinalIgnoreCase);
}
