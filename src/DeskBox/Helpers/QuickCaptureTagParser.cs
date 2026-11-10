using System.Text;
using DeskBox.Models;

namespace DeskBox.Helpers;

/// <summary>
/// Lightweight, reflection-free parser for inline Quick Capture <c>#tags</c>
/// (including nested <c>#parent/child</c> tags), Markdown checklist progress,
/// and structured Quick Capture-to-Todo conversion.
/// </summary>
public static class QuickCaptureTagParser
{
    public const int MaxTagLength = 40;
    public const int MaxTagsPerItem = 24;

    public static List<string> ExtractInlineTags(string? body, string? title = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        if (!string.IsNullOrWhiteSpace(title))
        {
            ScanLineForTags(title.AsSpan(), results, seen);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return results;
        }

        bool inFence = false;
        char fenceChar = '\0';
        int fenceCount = 0;

        foreach (ReadOnlySpan<char> rawLine in EnumerateLines(body.AsSpan()))
        {
            ReadOnlySpan<char> trimmedStart = rawLine.TrimStart();
            if (TryMatchFenceMarker(trimmedStart, out char markerChar, out int markerLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = markerChar;
                    fenceCount = markerLength;
                    continue;
                }

                if (markerChar == fenceChar &&
                    markerLength >= fenceCount &&
                    trimmedStart[markerLength..].Trim().IsEmpty)
                {
                    inFence = false;
                    fenceChar = '\0';
                    fenceCount = 0;
                    continue;
                }
            }

            if (inFence)
            {
                continue;
            }

            ScanLineForTags(rawLine, results, seen);
            if (results.Count >= MaxTagsPerItem)
            {
                break;
            }
        }

        return results;
    }

    public static List<string> MergeInlineTags(
        IReadOnlyList<string>? existingTags,
        string? previousTitle,
        string? previousBody,
        string? nextTitle,
        string? nextBody)
    {
        List<string> previousInline = ExtractInlineTags(previousBody, previousTitle);
        List<string> nextInline = ExtractInlineTags(nextBody, nextTitle);

        var previousSet = new HashSet<string>(previousInline, StringComparer.CurrentCultureIgnoreCase);
        var nextSet = new HashSet<string>(nextInline, StringComparer.CurrentCultureIgnoreCase);

        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        if (existingTags is not null)
        {
            foreach (string rawTag in existingTags)
            {
                string? normalized = NormalizeTag(rawTag);
                if (normalized is null)
                {
                    continue;
                }

                // If the tag previously came from inline #tag text and the user
                // removed it from the note text, drop it; otherwise preserve it.
                if (previousSet.Contains(normalized) && !nextSet.Contains(normalized))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    merged.Add(normalized);
                }
            }
        }

        foreach (string inlineTag in nextInline)
        {
            if (merged.Count >= MaxTagsPerItem)
            {
                break;
            }

            if (seen.Add(inlineTag))
            {
                merged.Add(inlineTag);
            }
        }

        return merged;
    }

    public static string? NormalizeTag(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return null;
        }

        ReadOnlySpan<char> span = rawTag.AsSpan().Trim();
        if (!span.IsEmpty && span[0] == '#')
        {
            span = span[1..].Trim();
        }

        span = span.Trim('/');
        if (span.IsEmpty || span.Length > MaxTagLength)
        {
            return null;
        }

        if (!IsValidTagBody(span))
        {
            return null;
        }

        return span.ToString();
    }

    public static (int Completed, int Total) ExtractChecklistStats(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (0, 0);
        }

        int completed = 0;
        int total = 0;
        bool inFence = false;
        char fenceChar = '\0';
        int fenceCount = 0;

        foreach (ReadOnlySpan<char> rawLine in EnumerateLines(body.AsSpan()))
        {
            ReadOnlySpan<char> trimmedStart = rawLine.TrimStart();
            if (TryMatchFenceMarker(trimmedStart, out char markerChar, out int markerLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = markerChar;
                    fenceCount = markerLength;
                    continue;
                }

                if (markerChar == fenceChar &&
                    markerLength >= fenceCount &&
                    trimmedStart[markerLength..].Trim().IsEmpty)
                {
                    inFence = false;
                    continue;
                }
            }

            if (inFence)
            {
                continue;
            }

            if (TryParseChecklistLine(rawLine, out bool isChecked, out _))
            {
                total++;
                if (isChecked)
                {
                    completed++;
                }
            }
        }

        return (completed, total);
    }

    public static (string Title, List<TodoStep> Steps, string? Notes) SplitForTodoConversion(
        QuickCaptureItem item,
        string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(item);

        var steps = new List<TodoStep>();
        var remainingLines = new List<string>();
        string? inferredHeadingTitle = null;

        string rawBody = item.Body ?? string.Empty;
        bool inFence = false;
        char fenceChar = '\0';
        int fenceCount = 0;

        foreach (ReadOnlySpan<char> rawLine in EnumerateLines(rawBody.AsSpan()))
        {
            string lineText = rawLine.ToString();
            ReadOnlySpan<char> trimmedStart = rawLine.TrimStart();
            if (TryMatchFenceMarker(trimmedStart, out char markerChar, out int markerLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = markerChar;
                    fenceCount = markerLength;
                }
                else if (markerChar == fenceChar &&
                         markerLength >= fenceCount &&
                         trimmedStart[markerLength..].Trim().IsEmpty)
                {
                    inFence = false;
                }

                remainingLines.Add(lineText);
                continue;
            }

            if (!inFence && TryParseChecklistLine(rawLine, out bool isChecked, out string stepText))
            {
                if (!string.IsNullOrWhiteSpace(stepText))
                {
                    steps.Add(new TodoStep
                    {
                        Text = stepText,
                        IsCompleted = isChecked,
                        SortOrder = steps.Count
                    });
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Title) &&
                inferredHeadingTitle is null &&
                !inFence &&
                !trimmedStart.IsEmpty)
            {
                string candidate = StripMarkdownHeadingPrefix(trimmedStart).Trim().ToString();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    inferredHeadingTitle = candidate.Length > 120 ? candidate[..120].Trim() : candidate;
                    continue;
                }
            }

            remainingLines.Add(lineText);
        }

        string? explicitTitle = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title.Trim();
        string resolvedTitle;
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            resolvedTitle = explicitTitle;
        }
        else if (!string.IsNullOrWhiteSpace(inferredHeadingTitle))
        {
            resolvedTitle = inferredHeadingTitle;
        }
        else if (steps.Count == 1)
        {
            resolvedTitle = steps[0].Text;
            steps.Clear();
        }
        else if (steps.Count > 1)
        {
            resolvedTitle = string.IsNullOrWhiteSpace(fallbackTitle) ? steps[0].Text : fallbackTitle.Trim();
        }
        else
        {
            resolvedTitle = string.IsNullOrWhiteSpace(fallbackTitle) ? "Todo" : fallbackTitle.Trim();
        }

        string joinedNotes = string.Join(Environment.NewLine, remainingLines).Trim();
        return (resolvedTitle, steps, string.IsNullOrWhiteSpace(joinedNotes) ? null : joinedNotes);
    }

    /// <summary>
    /// Converts a QuickCapture note into one or more Todo blueprints. When a note is a
    /// pure multi-line checklist without a title, heading, prose body, or attachments,
    /// each checklist line splits cleanly into its own top-level Todo item.
    /// </summary>
    public static IReadOnlyList<(string Title, bool IsCompleted, List<TodoStep> Steps, string? Notes)> SplitItemsForTodoConversion(
        QuickCaptureItem item,
        string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Title) &&
            (item.Attachments is null || item.Attachments.Count == 0))
        {
            var checklistEntries = new List<(string Text, bool IsCompleted)>();
            bool hasNonChecklistContent = false;
            bool inFence = false;
            char fenceChar = '\0';
            int fenceCount = 0;

            foreach (ReadOnlySpan<char> rawLine in EnumerateLines((item.Body ?? string.Empty).AsSpan()))
            {
                ReadOnlySpan<char> trimmedStart = rawLine.TrimStart();
                if (trimmedStart.IsEmpty)
                {
                    continue;
                }

                if (TryMatchFenceMarker(trimmedStart, out char markerChar, out int markerLength))
                {
                    hasNonChecklistContent = true;
                    if (!inFence)
                    {
                        inFence = true;
                        fenceChar = markerChar;
                        fenceCount = markerLength;
                    }
                    else if (markerChar == fenceChar &&
                             markerLength >= fenceCount &&
                             trimmedStart[markerLength..].Trim().IsEmpty)
                    {
                        inFence = false;
                    }

                    break;
                }

                if (!inFence && TryParseChecklistLine(rawLine, out bool isChecked, out string stepText))
                {
                    if (!string.IsNullOrWhiteSpace(stepText))
                    {
                        checklistEntries.Add((stepText.Trim(), isChecked));
                    }

                    continue;
                }

                hasNonChecklistContent = true;
                break;
            }

            if (!hasNonChecklistContent && checklistEntries.Count > 1)
            {
                return checklistEntries
                    .Select(entry => (entry.Text, entry.IsCompleted, new List<TodoStep>(), (string?)null))
                    .ToArray();
            }
        }

        (string title, List<TodoStep> steps, string? notes) = SplitForTodoConversion(item, fallbackTitle);
        bool singleCompleted = false;
        if (steps.Count == 0 && !string.IsNullOrWhiteSpace(item.Body))
        {
            foreach (ReadOnlySpan<char> rawLine in EnumerateLines(item.Body.AsSpan()))
            {
                if (!rawLine.TrimStart().IsEmpty)
                {
                    if (TryParseChecklistLine(rawLine, out bool parsedChecked, out _))
                    {
                        singleCompleted = parsedChecked;
                    }

                    break;
                }
            }
        }

        return [(title, singleCompleted, steps, notes)];
    }

    public static (string? Title, string Body, bool Pin) FormatTodoForQuickCapture(
        TodoItem item,
        string fallbackTitle = "Todo")
    {
        ArgumentNullException.ThrowIfNull(item);

        string title = string.IsNullOrWhiteSpace(item.Text)
            ? fallbackTitle
            : item.Text.Trim();
        var builder = new System.Text.StringBuilder();
        builder.Append("# ").AppendLine(title);

        if (item.Steps is { Count: > 0 })
        {
            builder.AppendLine();
            foreach (TodoStep step in item.Steps.OrderBy(s => s.SortOrder))
            {
                if (string.IsNullOrWhiteSpace(step.Text))
                {
                    continue;
                }

                builder.Append(step.IsCompleted ? "- [x] " : "- [ ] ")
                       .AppendLine(step.Text.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            builder.AppendLine();
            builder.AppendLine(item.Notes.Trim());
        }

        return (null, builder.ToString().Trim(), item.IsImportant);
    }

    public static bool TryParseChecklistLine(
        ReadOnlySpan<char> line,
        out bool isChecked,
        out string text)
    {
        isChecked = false;
        text = string.Empty;

        ReadOnlySpan<char> span = line.TrimStart();
        if (span.Length < 5)
        {
            return false;
        }

        int cursor = 0;
        if (span[0] is '-' or '*' or '+')
        {
            cursor = 1;
        }
        else if (char.IsAsciiDigit(span[0]))
        {
            while (cursor < span.Length && char.IsAsciiDigit(span[cursor]))
            {
                cursor++;
            }

            if (cursor >= span.Length || span[cursor] is not ('.' or ')'))
            {
                return false;
            }

            cursor++;
        }
        else
        {
            return false;
        }

        if (cursor >= span.Length || !char.IsWhiteSpace(span[cursor]))
        {
            return false;
        }

        while (cursor < span.Length && char.IsWhiteSpace(span[cursor]))
        {
            cursor++;
        }

        if (cursor + 3 > span.Length || span[cursor] != '[' || span[cursor + 2] != ']')
        {
            return false;
        }

        char stateChar = span[cursor + 1];
        if (stateChar is 'x' or 'X')
        {
            isChecked = true;
        }
        else if (stateChar != ' ')
        {
            return false;
        }

        cursor += 3;
        if (cursor < span.Length && !char.IsWhiteSpace(span[cursor]))
        {
            return false;
        }

        text = span[cursor..].Trim().ToString();
        return true;
    }

    private static ReadOnlySpan<char> StripMarkdownHeadingPrefix(ReadOnlySpan<char> trimmedLine)
    {
        int hashes = 0;
        while (hashes < trimmedLine.Length && hashes < 6 && trimmedLine[hashes] == '#')
        {
            hashes++;
        }

        if (hashes > 0 && hashes < trimmedLine.Length && char.IsWhiteSpace(trimmedLine[hashes]))
        {
            return trimmedLine[(hashes + 1)..];
        }

        return trimmedLine;
    }

    private static bool TryMatchFenceMarker(
        ReadOnlySpan<char> trimmedStart,
        out char markerChar,
        out int markerLength)
    {
        markerChar = '\0';
        markerLength = 0;
        if (trimmedStart.Length < 3)
        {
            return false;
        }

        char first = trimmedStart[0];
        if (first is not ('`' or '~'))
        {
            return false;
        }

        int count = 0;
        while (count < trimmedStart.Length && trimmedStart[count] == first)
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        markerChar = first;
        markerLength = count;
        return true;
    }

    private static void ScanLineForTags(
        ReadOnlySpan<char> line,
        List<string> results,
        HashSet<string> seen)
    {
        int i = 0;
        while (i < line.Length && results.Count < MaxTagsPerItem)
        {
            char c = line[i];

            // Skip inline code spans (`...`).
            if (c == '`')
            {
                int tickCount = 1;
                while (i + tickCount < line.Length && line[i + tickCount] == '`')
                {
                    tickCount++;
                }

                int searchStart = i + tickCount;
                int closingIndex = FindClosingBackticks(line, searchStart, tickCount);
                if (closingIndex >= 0)
                {
                    i = closingIndex + tickCount;
                    continue;
                }

                i = searchStart;
                continue;
            }

            // Skip URLs (http:// or https://) so URL fragments (#section) never become tags.
            if ((c is 'h' or 'H') && IsHttpPrefixAt(line, i))
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] is not (')' or ']' or '>' or '"' or '\''))
                {
                    i++;
                }

                continue;
            }

            if (c == '#')
            {
                // Skip multiple '#' runs (## Heading, ###, etc.)
                if (i + 1 < line.Length && line[i + 1] == '#')
                {
                    while (i < line.Length && line[i] == '#')
                    {
                        i++;
                    }

                    continue;
                }

                // '#' must be at line start or preceded by a delimiter (whitespace or opening/CJK punctuation),
                // never part of an HTML entity (&#xE710;), CSS property (color:#fff), or word#fragment.
                if (i > 0 && !IsValidTagPrecedingChar(line[i - 1]))
                {
                    i++;
                    continue;
                }

                int start = i + 1;
                int end = start;
                while (end < line.Length && IsAllowedTagChar(line[end]))
                {
                    end++;
                }

                // Trim trailing '/' or '-' so '#工作/' normalizes cleanly to '工作'.
                while (end > start && line[end - 1] is '/' or '-')
                {
                    end--;
                }

                if (end > start)
                {
                    ReadOnlySpan<char> candidate = line[start..end];
                    if (candidate.Length <= MaxTagLength && IsValidTagBody(candidate))
                    {
                        string tag = candidate.ToString();
                        if (seen.Add(tag))
                        {
                            results.Add(tag);
                        }
                    }

                    i = end;
                    continue;
                }
            }

            i++;
        }
    }

    private static int FindClosingBackticks(ReadOnlySpan<char> line, int startIndex, int tickCount)
    {
        int i = startIndex;
        while (i < line.Length)
        {
            if (line[i] == '`')
            {
                int run = 1;
                while (i + run < line.Length && line[i + run] == '`')
                {
                    run++;
                }

                if (run == tickCount)
                {
                    return i;
                }

                i += run;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static bool IsHttpPrefixAt(ReadOnlySpan<char> line, int index)
    {
        ReadOnlySpan<char> tail = line[index..];
        return tail.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               tail.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidTagPrecedingChar(char ch)
    {
        if (char.IsWhiteSpace(ch))
        {
            return true;
        }

        return ch is '(' or '（' or '[' or '【' or '{' or '「' or '『' or '《'
            or ',' or '，' or '、' or ';' or '；' or '。' or '!' or '！' or '?' or '？';
    }

    private static bool IsAllowedTagChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '_' or '-' or '/';
    }

    private static bool IsValidTagBody(ReadOnlySpan<char> candidate)
    {
        if (candidate.IsEmpty || candidate[0] is '/' or '-' || candidate[^1] is '/' or '-')
        {
            return false;
        }

        bool hasLetterOrNonAscii = false;
        bool allAsciiHex = true;
        bool hasAsciiUpper = false;
        bool hasAsciiDigit = false;
        bool prevWasSlash = false;

        for (int i = 0; i < candidate.Length; i++)
        {
            char ch = candidate[i];
            if (!IsAllowedTagChar(ch))
            {
                return false;
            }

            if (ch == '/')
            {
                if (prevWasSlash)
                {
                    return false;
                }

                prevWasSlash = true;
                allAsciiHex = false;
                continue;
            }

            prevWasSlash = false;
            if (char.IsLetter(ch) || ch > 0x7F)
            {
                hasLetterOrNonAscii = true;
            }

            if (char.IsAsciiDigit(ch))
            {
                hasAsciiDigit = true;
            }
            else if (ch is >= 'A' and <= 'F')
            {
                hasAsciiUpper = true;
            }
            else if (ch is not (>= 'a' and <= 'f'))
            {
                allAsciiHex = false;
            }
        }

        // Reject pure numeric tokens like '#123' or '#580' (issue numbers / rankings).
        if (!hasLetterOrNonAscii)
        {
            return false;
        }

        // Reject likely CSS/hex color literals (#FFF, #F00, #1E1E1E, #FF5733AA).
        if (allAsciiHex)
        {
            if (candidate.Length is 6 or 8 && (hasAsciiDigit || hasAsciiUpper || AllSameChar(candidate)))
            {
                return false;
            }

            if (candidate.Length == 3 && (hasAsciiDigit || hasAsciiUpper))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllSameChar(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        for (int i = 1; i < span.Length; i++)
        {
            if (span[i] != span[0])
            {
                return false;
            }
        }

        return true;
    }

    private static LineEnumerator EnumerateLines(ReadOnlySpan<char> text) => new(text);

    private ref struct LineEnumerator(ReadOnlySpan<char> remaining)
    {
        private ReadOnlySpan<char> _remaining = remaining;
        private ReadOnlySpan<char> _current = default;
        private bool _done = false;

        public readonly ReadOnlySpan<char> Current => _current;

        public readonly LineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_done)
            {
                return false;
            }

            int newlineIdx = _remaining.IndexOfAny('\r', '\n');
            if (newlineIdx < 0)
            {
                _current = _remaining;
                _remaining = default;
                _done = true;
                return true;
            }

            _current = _remaining[..newlineIdx];
            int advance = newlineIdx + 1;
            if (_remaining[newlineIdx] == '\r' &&
                advance < _remaining.Length &&
                _remaining[advance] == '\n')
            {
                advance++;
            }

            _remaining = _remaining[advance..];
            return true;
        }
    }
}
