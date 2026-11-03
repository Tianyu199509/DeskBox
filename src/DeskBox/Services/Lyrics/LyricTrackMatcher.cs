using System.Text;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Track query descriptor used across local and online lyric providers.
/// </summary>
public readonly record struct LyricsQuery(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// CJK- and Latin-aware track metadata cleaner and weighted candidate scorer for lyric matching.
/// </summary>
public static class LyricTrackMatcher
{
    public const double MinimumAcceptScore = 0.62;

    private static readonly string[] BracketNoiseKeywords =
    [
        "live",
        "现场",
        "現場",
        "remaster",
        "remastered",
        "deluxe",
        "explicit",
        "clean",
        "radio edit",
        "single version",
        "album version",
        "official",
        "mv",
        "audio",
        "video",
        "feat.",
        "feat ",
        "ft.",
        "ft ",
        "伴奏",
        "原唱",
        "翻唱",
        "版)",
        "版）",
        "ost",
        "插曲",
        "主题曲",
        "片尾曲",
        "片头曲",
        "推广曲"
    ];

    private static readonly string[] DashSuffixKeywords =
    [
        "remaster",
        "remastered",
        "live",
        "radio edit",
        "acoustic",
        "single version",
        "album version",
        "deluxe",
        "bonus track",
        "explicit",
        "20",
        "19"
    ];

    public static string CleanTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string text = rawTitle.Trim();
        text = UnwrapBookQuotes(text);
        text = StripNoiseBrackets(text);
        text = StripTrailingFeatClause(text);
        text = StripTrailingDashNoise(text);

        string result = CollapseWhitespace(text);
        return string.IsNullOrWhiteSpace(result) ? CollapseWhitespace(rawTitle) : result;
    }

    public static string CleanArtist(string? rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return string.Empty;
        }

        string text = StripNoiseBrackets(rawArtist.Trim());
        string result = CollapseWhitespace(text);
        return string.IsNullOrWhiteSpace(result) ? CollapseWhitespace(rawArtist) : result;
    }

    public static string ExtractPrimaryArtist(string? rawArtist)
    {
        string cleaned = CleanArtist(rawArtist);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = cleaned.AsSpan();
        int splitIndex = -1;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c is '/' or '、' or ';' or '；' or '｜' or '|')
            {
                splitIndex = i;
                break;
            }

            if (c == '&' && i > 0 && i < span.Length - 1 && (span[i - 1] == ' ' || span[i + 1] == ' '))
            {
                splitIndex = i;
                break;
            }
        }

        if (splitIndex > 0)
        {
            string first = span[..splitIndex].Trim().ToString();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        return cleaned;
    }

    public static string BuildNormalizedCacheIdentity(string? title, string? artist)
    {
        string normTitle = NormalizeForComparison(CleanTitle(title));
        if (string.IsNullOrEmpty(normTitle))
        {
            normTitle = NormalizeForComparison(title);
        }

        string normArtist = NormalizeForComparison(ExtractPrimaryArtist(artist));
        return $"{normTitle}|{normArtist}";
    }

    public static LrclibTrackDto? SelectBestCandidate(
        ReadOnlySpan<LrclibTrackDto> candidates,
        LyricsQuery query,
        out double bestScore)
    {
        bestScore = 0.0;
        LrclibTrackDto? best = null;

        for (int i = 0; i < candidates.Length; i++)
        {
            LrclibTrackDto candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate.SyncedLyrics) && !candidate.Instrumental)
            {
                continue;
            }

            double score = ScoreCandidate(candidate, query);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= MinimumAcceptScore ? best : null;
    }

    public static double ScoreCandidate(LrclibTrackDto candidate, LyricsQuery query)
    {
        string candidateTitle = !string.IsNullOrWhiteSpace(candidate.TrackName)
            ? candidate.TrackName!
            : candidate.Name ?? string.Empty;
        string candidateArtist = candidate.ArtistName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return 0.0;
        }

        double rawTitleSim = ComputeTextSimilarity(query.Title, candidateTitle);
        double cleanTitleSim = ComputeTextSimilarity(CleanTitle(query.Title), CleanTitle(candidateTitle));
        double titleScore = Math.Max(rawTitleSim, cleanTitleSim * 0.97);

        if (titleScore < 0.45)
        {
            return 0.0;
        }

        bool hasQueryArtist = !string.IsNullOrWhiteSpace(query.Artist);
        double artistScore;
        if (!hasQueryArtist)
        {
            artistScore = 0.65;
        }
        else
        {
            double fullArtistSim = ComputeTextSimilarity(CleanArtist(query.Artist), CleanArtist(candidateArtist));
            double primaryArtistSim = ComputeTextSimilarity(
                ExtractPrimaryArtist(query.Artist),
                ExtractPrimaryArtist(candidateArtist));
            double containmentBonus = ContainsNormalized(candidateArtist, ExtractPrimaryArtist(query.Artist)) ||
                                      ContainsNormalized(query.Artist, ExtractPrimaryArtist(candidateArtist))
                ? 0.85
                : 0.0;
            artistScore = Math.Max(fullArtistSim, Math.Max(primaryArtistSim * 0.95, containmentBonus));
        }

        bool hasDuration = query.Duration.TotalSeconds > 5.0 && candidate.Duration > 5.0;
        double totalScore;

        if (hasDuration)
        {
            double deltaSeconds = Math.Abs(query.Duration.TotalSeconds - candidate.Duration);
            double durationScore = deltaSeconds switch
            {
                <= 2.0 => 1.0,
                <= 5.0 => 0.85,
                <= 10.0 => 0.55,
                <= 20.0 => 0.20,
                _ => 0.0
            };

            totalScore = (0.55 * titleScore) + (0.30 * artistScore) + (0.15 * durationScore);
            if (deltaSeconds > 25.0)
            {
                totalScore -= 0.18;
            }
        }
        else
        {
            // Timeline-less player mode: tighten title & artist gates so generic titles do not mis-match.
            if (titleScore < 0.70 || (hasQueryArtist && artistScore < 0.35))
            {
                return 0.0;
            }

            totalScore = (0.65 * titleScore) + (0.35 * artistScore);
        }

        if (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics))
        {
            totalScore = Math.Min(1.0, totalScore + 0.03);
        }

        return Math.Clamp(totalScore, 0.0, 1.0);
    }

    internal static double ComputeTextSimilarity(string? left, string? right)
    {
        string a = NormalizeForComparison(left);
        string b = NormalizeForComparison(right);

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (a.Length == 1 || b.Length == 1)
        {
            return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)
                ? 0.75
                : 0.0;
        }

        // Character bigram Dice coefficient works uniformly across CJK (no spaces) and Latin strings.
        int aBigrams = a.Length - 1;
        int bBigrams = b.Length - 1;
        Span<bool> matched = bBigrams <= 256 ? stackalloc bool[bBigrams] : new bool[bBigrams];
        int intersection = 0;

        for (int i = 0; i < aBigrams; i++)
        {
            char a0 = a[i];
            char a1 = a[i + 1];
            for (int j = 0; j < bBigrams; j++)
            {
                if (!matched[j] && b[j] == a0 && b[j + 1] == a1)
                {
                    matched[j] = true;
                    intersection++;
                    break;
                }
            }
        }

        return (2.0 * intersection) / (aBigrams + bBigrams);
    }

    internal static string NormalizeForComparison(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        bool prevSpace = true;

        foreach (char rawChar in input)
        {
            char c = rawChar is >= '\uFF01' and <= '\uFF5E'
                ? (char)(rawChar - 0xFEE0) // Full-width ASCII -> half-width ASCII
                : char.ToLowerInvariant(rawChar);

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (char.IsWhiteSpace(c) && !prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool ContainsNormalized(string? haystack, string? needle)
    {
        string h = NormalizeForComparison(haystack);
        string n = NormalizeForComparison(needle);
        return h.Length > 0 && n.Length >= 2 && h.Contains(n, StringComparison.Ordinal);
    }

    private static string UnwrapBookQuotes(string text)
    {
        if (text.Length >= 3 && text[0] == '《')
        {
            int close = text.IndexOf('》');
            if (close == text.Length - 1)
            {
                return text[1..close].Trim();
            }
        }

        return text;
    }

    private static string StripNoiseBrackets(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char open = text[i];
            char close = open switch
            {
                '(' => ')',
                '（' => '）',
                '[' => ']',
                '【' => '】',
                _ => '\0'
            };

            if (close != '\0')
            {
                int closeIdx = text.IndexOf(close, i + 1);
                if (closeIdx > i)
                {
                    string inside = text[(i + 1)..closeIdx];
                    if (IsBracketNoise(inside))
                    {
                        i = closeIdx + 1;
                        continue;
                    }
                }
            }

            sb.Append(open);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsBracketNoise(string content)
    {
        for (int i = 0; i < BracketNoiseKeywords.Length; i++)
        {
            if (content.Contains(BracketNoiseKeywords[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripTrailingFeatClause(string text)
    {
        string[] markers = [" feat.", " feat ", " ft.", " ft "];
        foreach (string marker in markers)
        {
            int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                text = text[..idx];
            }
        }

        return text;
    }

    private static string StripTrailingDashNoise(string text)
    {
        string[] separators = [" - ", " – ", " — "];
        foreach (string sep in separators)
        {
            int idx = text.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            string suffix = text[(idx + sep.Length)..].Trim();
            for (int k = 0; k < DashSuffixKeywords.Length; k++)
            {
                if (suffix.Contains(DashSuffixKeywords[k], StringComparison.OrdinalIgnoreCase))
                {
                    return text[..idx];
                }
            }
        }

        return text;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool inSpace = true;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
