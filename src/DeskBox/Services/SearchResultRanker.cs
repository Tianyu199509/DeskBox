using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Normalizes, de-duplicates and globally ranks results returned by different providers.
/// Provider-specific scores are deliberately kept simple; this layer owns cross-provider
/// quality rules so the UI can always consume one trustworthy ordered list.
/// </summary>
internal static class SearchResultRanker
{
    private static readonly string[] s_noisyDirectorySegments =
    [
        "\\AppData\\Local\\Temp\\",
        "\\.git\\",
        "\\node_modules\\",
        "\\bin\\Debug\\",
        "\\bin\\Release\\",
        "\\obj\\Debug\\",
        "\\obj\\Release\\",
        "\\$Recycle.Bin\\",
        "\\System Volume Information\\"
    ];

    private static readonly HashSet<string> s_partialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".download", ".partial", ".part", ".tmp"
    };

    public static IReadOnlyList<SearchResultItem> MergeAndRank(
        IEnumerable<SearchResultItem> results,
        string query,
        int maxResults)
    {
        if (maxResults <= 0)
        {
            return [];
        }

        var byIdentity = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results)
        {
            item.RelevanceScore = AdjustScore(item, query);
            if (item.RelevanceScore <= 0)
            {
                continue;
            }

            string identity = GetIdentityKey(item);
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

        string extension = Path.GetExtension(path);
        if (s_partialExtensions.Contains(extension))
        {
            return true;
        }

        string normalized = path.Replace('/', '\\');
        return s_noisyDirectorySegments.Any(segment =>
            normalized.Contains(segment, StringComparison.OrdinalIgnoreCase)) ||
            normalized.Contains("\\Cache\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\Caches\\", StringComparison.OrdinalIgnoreCase);
    }

    private static double AdjustScore(SearchResultItem item, string query)
    {
        double score = item.RelevanceScore;
        score += item.Kind switch
        {
            SearchResultKind.Action => 6,
            SearchResultKind.Todo => 5,
            SearchResultKind.QuickCapture => 4,
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
