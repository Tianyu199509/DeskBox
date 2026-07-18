namespace DeskBox.Services;

internal static class SettingsSearchMatcher
{
    internal const int NoMatch = int.MaxValue;

    internal static int GetScore(
        string? query,
        string title,
        string breadcrumb,
        string description)
    {
        string[] terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return NoMatch;
        }

        int totalScore = 0;
        foreach (string term in terms)
        {
            int termScore = GetTermScore(term, title, breadcrumb, description);
            if (termScore == NoMatch)
            {
                return NoMatch;
            }

            totalScore += termScore;
        }

        return totalScore;
    }

    private static int GetTermScore(
        string term,
        string title,
        string breadcrumb,
        string description)
    {
        if (title.Equals(term, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        int index = title.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return 20 + Math.Min(index, 9);
        }

        index = breadcrumb.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return 40 + Math.Min(index, 19);
        }

        index = description.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? 80 + Math.Min(index, 19)
            : NoMatch;
    }
}
