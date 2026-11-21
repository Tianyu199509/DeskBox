using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Services;

internal static partial class SettingsSearchMatcher
{
    internal const int NoMatch = int.MaxValue;
    private const int SynonymPenalty = 15;

    // Bidirectional bilingual synonym groups (Native AOT safe static arrays; zero reflection).
    private static readonly string[][] SynonymGroups =
    [
        ["mica", "micaalt", "云母", "材质", "背景材质"],
        ["acrylic", "亚克力", "毛玻璃"],
        ["opacity", "transparency", "transparent", "alpha", "透明度", "透明", "不透明度"],
        ["hotkey", "shortcut", "keybind", "快捷键", "热键", "组合键"],
        ["autostart", "startup", "boot", "launch", "开机自动启动", "开机自启", "自启", "自启动"],
        ["webdav", "cloud", "sync", "云备份", "云同步", "同步"],
        ["backup", "snapshot", "备份", "快照"],
        ["restore", "reset", "还原", "恢复", "重置"],
        ["snap", "magnet", "align", "吸附", "贴靠", "对齐"],
        ["theme", "dark", "light", "appearance", "主题", "深色", "浅色", "暗色", "亮色", "夜间"],
        ["capsule", "collapse", "compact", "胶囊", "收起", "折叠"],
        ["stack", "stacks", "叠放", "堆叠"],
        ["tray", "systray", "notification", "托盘", "系统托盘"],
        ["shadow", "阴影"],
        ["corner", "radius", "rounded", "圆角", "方角"],
        ["memory", "ram", "cache", "trim", "performance", "内存", "缓存", "工作集", "性能"],
        ["display", "displays", "monitor", "screen", "dpi", "显示器", "多屏", "屏幕", "多显示器"],
        ["animation", "motion", "effect", "transition", "动画", "动效", "过渡", "过渡效果"],
        ["font", "fontsize", "textsize", "typography", "字体", "字号", "文字大小"],
        ["density", "spacing", "padding", "密度", "间距"],
        ["everything", "search", "搜索"],
        ["clipboard", "paste", "剪贴板"],
        ["weather", "forecast", "天气"],
        ["todo", "task", "tasks", "待办"],
        ["note", "notes", "markdown", "quickcapture", "随记", "便签"],
        ["language", "locale", "i18n", "语言"]
    ];

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
        for (int index = 0; index < terms.Length; index++)
        {
            int termScore = GetTermScore(terms[index], title, breadcrumb, description);
            if (termScore == NoMatch)
            {
                return NoMatch;
            }

            totalScore += termScore;
        }

        return totalScore;
    }

    internal static double ConvertMatchScoreToSearchRelevance(int matcherScore)
    {
        if (matcherScore == NoMatch || matcherScore < 0)
        {
            return 0;
        }

        return matcherScore switch
        {
            0 => 100,
            <= 12 => 84,
            <= 29 => 62,
            30 => 78,
            <= 38 => 70,
            <= 55 => 52,
            <= 80 => 42,
            _ => Math.Max(28, 96 - (matcherScore * 0.45))
        };
    }

    private static int GetTermScore(
        string term,
        string title,
        string breadcrumb,
        string description)
    {
        int bestScore = GetDirectTermScore(term, title, breadcrumb, description);
        if (bestScore == 0)
        {
            return 0;
        }

        // Bilingual synonym expansion: lets English queries match localized Chinese UI and vice versa.
        for (int groupIndex = 0; groupIndex < SynonymGroups.Length; groupIndex++)
        {
            string[] group = SynonymGroups[groupIndex];
            if (!GroupMatchesTerm(group, term))
            {
                continue;
            }

            for (int candidateIndex = 0; candidateIndex < group.Length; candidateIndex++)
            {
                string candidate = group[candidateIndex];
                if (string.Equals(candidate, term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int candidateDirect = GetDirectTermScore(candidate, title, breadcrumb, description);
                if (candidateDirect != NoMatch)
                {
                    bestScore = Math.Min(bestScore, candidateDirect + SynonymPenalty);
                }
            }
        }

        // CJK Pinyin initials & multi-reading syllable-boundary full Pinyin matching (2+ ASCII letters).
        if (term.Length >= 2 && IsAsciiLetters(term))
        {
            int titlePinyin = GetPinyinFieldScore(term, title, exactScore: 30, prefixScore: 34, substringBaseScore: 44);
            if (titlePinyin != NoMatch)
            {
                bestScore = Math.Min(bestScore, titlePinyin);
            }
            else
            {
                int breadcrumbPinyin = GetPinyinFieldScore(term, breadcrumb, exactScore: 62, prefixScore: 65, substringBaseScore: 70);
                if (breadcrumbPinyin != NoMatch)
                {
                    bestScore = Math.Min(bestScore, breadcrumbPinyin);
                }
                else
                {
                    int descriptionPinyin = GetPinyinFieldScore(term, description, exactScore: 105, prefixScore: 108, substringBaseScore: 112);
                    if (descriptionPinyin != NoMatch)
                    {
                        bestScore = Math.Min(bestScore, descriptionPinyin);
                    }
                }
            }
        }

        return bestScore;
    }

    private static int GetDirectTermScore(
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

    private static bool GroupMatchesTerm(string[] group, string term)
    {
        for (int index = 0; index < group.Length; index++)
        {
            string entry = group[index];
            if (entry.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                (term.Length >= 3 && entry.StartsWith(term, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiLetters(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char c = value[index];
            bool isUpper = c >= 'A' && c <= 'Z';
            bool isLower = c >= 'a' && c <= 'z';
            if (!isUpper && !isLower)
            {
                return false;
            }
        }

        return true;
    }

    private static int GetPinyinFieldScore(
        string asciiQuery,
        string text,
        int exactScore,
        int prefixScore,
        int substringBaseScore)
    {
        if (string.IsNullOrEmpty(text) || !ContainsCjk(text))
        {
            return NoMatch;
        }

        List<PinyinToken> tokens = ExtractPinyinTokens(text);
        if (tokens.Count == 0)
        {
            return NoMatch;
        }

        ReadOnlySpan<PinyinToken> tokenSpan = CollectionsMarshal.AsSpan(tokens);
        ReadOnlySpan<char> querySpan = asciiQuery.AsSpan();
        int best = NoMatch;

        // 1. Multi-reading Pinyin initials (e.g., "tmd" -> "透明度", "cz"/"zz" -> "重置").
        if (tokenSpan.Length >= querySpan.Length)
        {
            if (MatchesInitialsAt(tokenSpan, 0, querySpan))
            {
                if (tokenSpan.Length == querySpan.Length)
                {
                    return exactScore;
                }

                best = Math.Min(best, prefixScore);
            }
            else
            {
                int maxStart = tokenSpan.Length - querySpan.Length;
                for (int startToken = 1; startToken <= maxStart; startToken++)
                {
                    if (MatchesInitialsAt(tokenSpan, startToken, querySpan))
                    {
                        best = Math.Min(best, substringBaseScore + Math.Min(startToken, 9));
                        break;
                    }
                }
            }
        }

        // 2. Multi-reading syllable-boundary full Pinyin (e.g., "toumingdu", "chongzhi"/"zhongzhi", "suolvetu"/"suoluetu").
        if (MatchesFullPinyinFrom(tokenSpan, 0, querySpan, out bool consumedAllTokens))
        {
            if (consumedAllTokens)
            {
                return exactScore;
            }

            best = Math.Min(best, prefixScore);
        }
        else if (best > substringBaseScore)
        {
            for (int startToken = 1; startToken < tokenSpan.Length; startToken++)
            {
                int candidateScore = substringBaseScore + Math.Min(startToken, 9);
                if (candidateScore >= best)
                {
                    break;
                }

                if (MatchesFullPinyinFrom(tokenSpan, startToken, querySpan, out _))
                {
                    best = candidateScore;
                    break;
                }
            }
        }

        return best;
    }

    private static bool MatchesInitialsAt(
        ReadOnlySpan<PinyinToken> tokens,
        int startToken,
        ReadOnlySpan<char> query)
    {
        for (int index = 0; index < query.Length; index++)
        {
            char target = (char)(query[index] | 0x20);
            if (!tokens[startToken + index].HasInitial(target))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesFullPinyinFrom(
        ReadOnlySpan<PinyinToken> tokens,
        int startToken,
        ReadOnlySpan<char> query,
        out bool consumedAllTokens)
    {
        int queryLength = query.Length;
        Span<bool> reachable = queryLength <= 128
            ? stackalloc bool[queryLength + 1]
            : new bool[queryLength + 1];
        Span<bool> next = queryLength <= 128
            ? stackalloc bool[queryLength + 1]
            : new bool[queryLength + 1];

        reachable[0] = true;
        bool prefixMatched = false;

        for (int tokenIndex = startToken; tokenIndex < tokens.Length; tokenIndex++)
        {
            next.Clear();
            bool anyNext = false;
            PinyinToken token = tokens[tokenIndex];

            for (int offset = 0; offset < queryLength; offset++)
            {
                if (!reachable[offset])
                {
                    continue;
                }

                ReadOnlySpan<char> remaining = query.Slice(offset);
                for (int readingIndex = 0; readingIndex < token.Count; readingIndex++)
                {
                    ReadOnlySpan<char> reading = token[readingIndex].AsSpan();
                    if (reading.Length <= remaining.Length)
                    {
                        if (remaining.StartsWith(reading, StringComparison.OrdinalIgnoreCase))
                        {
                            int newOffset = offset + reading.Length;
                            next[newOffset] = true;
                            if (newOffset < queryLength)
                            {
                                anyNext = true;
                            }
                            else if (tokenIndex < tokens.Length - 1)
                            {
                                prefixMatched = true;
                            }
                        }
                    }
                    else if (reading.StartsWith(remaining, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixMatched = true;
                    }
                }
            }

            if (tokenIndex == tokens.Length - 1 && next[queryLength])
            {
                consumedAllTokens = startToken == 0;
                return true;
            }

            if (!anyNext)
            {
                consumedAllTokens = false;
                return prefixMatched;
            }

            next.CopyTo(reachable);
        }

        consumedAllTokens = false;
        return prefixMatched;
    }

    private static bool ContainsCjk(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch >= CjkUnifiedStart && ch <= CjkUnifiedEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static List<PinyinToken> ExtractPinyinTokens(string text)
    {
        var tokens = new List<PinyinToken>(text.Length);
        var asciiRun = new StringBuilder();

        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (TryGetCjkPinyinToken(ch, out PinyinToken cjkToken))
            {
                if (asciiRun.Length > 0)
                {
                    tokens.Add(new PinyinToken(asciiRun.ToString()));
                    asciiRun.Clear();
                }

                tokens.Add(cjkToken);
            }
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                asciiRun.Append(char.ToLowerInvariant(ch));
            }
            else if (asciiRun.Length > 0)
            {
                tokens.Add(new PinyinToken(asciiRun.ToString()));
                asciiRun.Clear();
            }
        }

        if (asciiRun.Length > 0)
        {
            tokens.Add(new PinyinToken(asciiRun.ToString()));
        }

        return tokens;
    }

    internal static bool TryGetCjkReadings(char ch, out string[] readings)
    {
        if (!TryGetCjkPinyinToken(ch, out PinyinToken token))
        {
            readings = [];
            return false;
        }

        readings = new string[token.Count];
        for (int index = 0; index < token.Count; index++)
        {
            readings[index] = token[index];
        }

        return true;
    }

    private static bool TryGetCjkPinyinToken(char ch, out PinyinToken token)
    {
        if (ch < CjkUnifiedStart || ch > CjkUnifiedEnd)
        {
            token = default;
            return false;
        }

        int byteOffset = (ch - CjkUnifiedStart) * 2;
        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(CjkPrimarySyllableMap.Slice(byteOffset, 2));
        if (code == 0)
        {
            token = default;
            return false;
        }

        var tokenBuilder = new PinyinToken(SyllablePalette[code - 1]);
        if (TryGetVToUVariant(tokenBuilder[0], out string? primaryUVariant))
        {
            tokenBuilder.AddUnique(primaryUVariant!);
        }

        int polyIndex = Array.BinarySearch(PolyphoneCodePoints, (ushort)ch);
        if (polyIndex >= 0)
        {
            string[] alternates = PolyphoneAlternates[polyIndex];
            for (int altIndex = 0; altIndex < alternates.Length; altIndex++)
            {
                string alternate = alternates[altIndex];
                tokenBuilder.AddUnique(alternate);
                if (TryGetVToUVariant(alternate, out string? altUVariant))
                {
                    tokenBuilder.AddUnique(altUVariant!);
                }
            }
        }

        token = tokenBuilder;
        return true;
    }

    private static bool TryGetVToUVariant(string syllable, out string? uVariant)
    {
        uVariant = syllable switch
        {
            "lv" => "lu",
            "lve" => "lue",
            "nv" => "nu",
            "nve" => "nue",
            _ => null
        };
        return uVariant is not null;
    }

    private struct PinyinToken
    {
        private string _r0;
        private string? _r1;
        private string? _r2;
        private string? _r3;
        private string? _r4;

        public PinyinToken(string primary)
        {
            _r0 = primary;
            _r1 = null;
            _r2 = null;
            _r3 = null;
            _r4 = null;
            Count = 1;
        }

        public byte Count { get; private set; }

        public readonly string this[int index] => index switch
        {
            0 => _r0,
            1 => _r1!,
            2 => _r2!,
            3 => _r3!,
            4 => _r4!,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        public readonly bool HasInitial(char lowerAscii)
        {
            for (int index = 0; index < Count; index++)
            {
                string reading = this[index];
                if (reading.Length > 0 && (char)(reading[0] | 0x20) == lowerAscii)
                {
                    return true;
                }
            }

            return false;
        }

        public void AddUnique(string candidate)
        {
            for (int index = 0; index < Count; index++)
            {
                if (string.Equals(this[index], candidate, StringComparison.Ordinal))
                {
                    return;
                }
            }

            switch (Count)
            {
                case 1:
                    _r1 = candidate;
                    Count = 2;
                    break;
                case 2:
                    _r2 = candidate;
                    Count = 3;
                    break;
                case 3:
                    _r3 = candidate;
                    Count = 4;
                    break;
                case 4:
                    _r4 = candidate;
                    Count = 5;
                    break;
            }
        }
    }
}
