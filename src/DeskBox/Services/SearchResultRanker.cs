using System.Buffers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Normalizes, de-duplicates and globally ranks results returned by different providers.
/// Provider-specific scores are deliberately kept simple; this layer owns cross-provider
/// quality rules so the UI can always consume one trustworthy ordered list.
/// </summary>
internal static class SearchResultRanker
{
    private static readonly SearchValues<string> s_noisyPathSegments =
        SearchValues.Create(
        [
            "\\AppData\\Local\\Temp\\",
            "\\.git\\",
            "\\node_modules\\",
            "\\bin\\Debug\\",
            "\\bin\\Release\\",
            "\\obj\\Debug\\",
            "\\obj\\Release\\",
            "\\$Recycle.Bin\\",
            "\\System Volume Information\\",
            "\\Cache\\",
            "\\Caches\\"
        ],
        StringComparison.OrdinalIgnoreCase);

    private static readonly System.Collections.Frozen.FrozenSet<string> s_partialExtensions =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            ".crdownload", ".download", ".partial", ".part", ".tmp"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SearchResultItem> MergeAndRank(
        IEnumerable<SearchResultItem> results,
        string query,
        int maxResults,
        Func<string, double>? frecencyBoostProvider = null)
    {
        if (maxResults <= 0)
        {
            return [];
        }

        var byIdentity = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results)
        {
            if (item.RelevanceScore <= 0)
            {
                continue;
            }

            string identity = GetIdentityKey(item);
            if (item.IsFallback)
            {
                item.RelevanceScore = 0.25;
            }
            else
            {
                double adjustedScore = AdjustScore(item, query);
                if (frecencyBoostProvider is not null)
                {
                    double frecency = frecencyBoostProvider(identity);
                    if (double.IsFinite(frecency) && frecency > 0)
                    {
                        adjustedScore += frecency;
                    }
                }

                // No matching item is silently discarded. Noisy paths remain available
                // at the end of the continuously paged Everything result set.
                item.RelevanceScore = Math.Max(1, adjustedScore);
            }

            if (!byIdentity.TryGetValue(identity, out var existing) || IsBetterDuplicate(item, existing))
            {
                byIdentity[identity] = item;
            }
        }

        return byIdentity.Values
            .OrderByDescending(item => item.RelevanceScore)
            .ThenByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    public static string GetIdentityKey(SearchResultItem item)
    {
        if (item.CachedIdentityKey is { } cached)
        {
            return cached;
        }

        string computed = ComputeIdentityKey(item);
        item.CachedIdentityKey = computed;
        return computed;
    }

    private static string ComputeIdentityKey(SearchResultItem item)
    {
        if (item.Kind is SearchResultKind.File or SearchResultKind.Folder &&
            !string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return $"path:{NormalizePath(item.DetailPath)}";
        }

        if (item.Kind == SearchResultKind.Todo && !string.IsNullOrWhiteSpace(item.TodoItemId))
        {
            return $"todo:{item.TodoWidgetId}:{item.TodoItemId}";
        }

        if (item.Kind == SearchResultKind.QuickCapture && !string.IsNullOrWhiteSpace(item.QuickCaptureItemId))
        {
            return $"note:{item.QuickCaptureItemId}";
        }

        if (item.Kind == SearchResultKind.CustomWidget && !string.IsNullOrWhiteSpace(item.CustomWidgetId))
        {
            return $"custom-widget:{item.CustomWidgetId}:{item.CustomWidgetItemId ?? "root"}";
        }

        if (item.Kind == SearchResultKind.Clipboard && !string.IsNullOrWhiteSpace(item.ClipboardItemId))
        {
            return $"clipboard:{item.ClipboardItemId}";
        }

        if (item.Kind == SearchResultKind.Action && !string.IsNullOrWhiteSpace(item.ActionId))
        {
            return $"action:{item.ActionId}";
        }

        return $"{item.Kind}:{item.Title}:{item.Subtitle}";
    }

    public static bool IsNoisyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        ReadOnlySpan<char> span = path.AsSpan();
        ReadOnlySpan<char> extension = Path.GetExtension(span);
        if (!extension.IsEmpty && s_partialExtensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension))
        {
            return true;
        }

        if (span.IndexOf('/') < 0)
        {
            return span.ContainsAny(s_noisyPathSegments);
        }

        if (span.Length <= 512)
        {
            Span<char> normalized = stackalloc char[span.Length];
            span.Replace(normalized, '/', '\\');
            return normalized.ContainsAny(s_noisyPathSegments);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(span.Length);
        try
        {
            Span<char> normalized = rented.AsSpan(0, span.Length);
            span.Replace(normalized, '/', '\\');
            return normalized.ContainsAny(s_noisyPathSegments);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static double AdjustScore(SearchResultItem item, string query)
    {
        double score = item.RelevanceScore;
        score += item.Kind switch
        {
            SearchResultKind.Action => 6,
            SearchResultKind.Todo => 5,
            SearchResultKind.QuickCapture => 4,
            SearchResultKind.CustomWidget => 4,
            SearchResultKind.Clipboard => 3,
            SearchResultKind.Folder => 1,
            _ => 0
        };

        if (item.Kind == SearchResultKind.File &&
            FileCategoryHelper.Categorize(item.Title) == FileCategory.App)
        {
            score += 3;
        }

        if (IsNoisyPath(item.DetailPath))
        {
            // Exact queries can still surface a cache or partial file, but broad queries
            // should not let those entries crowd out user documents.
            bool exact = item.Title.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileNameWithoutExtension(item.Title)
                             .Equals(query, StringComparison.OrdinalIgnoreCase);
            score -= exact ? 35 : 70;
        }

        return score;
    }

    private static bool IsBetterDuplicate(SearchResultItem candidate, SearchResultItem existing)
    {
        if (candidate.RelevanceScore != existing.RelevanceScore)
        {
            return candidate.RelevanceScore > existing.RelevanceScore;
        }

        bool candidateHasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(candidate.Title));
        bool existingHasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(existing.Title));
        if (candidateHasExtension != existingHasExtension)
        {
            return candidateHasExtension;
        }

        return candidate.ModifiedAt > existing.ModifiedAt;
    }

    private static string NormalizePath(string path)
    {
        if (path.Length >= 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            path[2] == '\\' &&
            path.IndexOf('/') < 0 &&
            !path.Contains("\\.", StringComparison.Ordinal) &&
            !path.Contains("\\\\", StringComparison.Ordinal) &&
            !char.IsWhiteSpace(path[0]) &&
            !char.IsWhiteSpace(path[^1]))
        {
            return path.TrimEnd('\\');
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }
}
