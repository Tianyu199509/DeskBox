using System.Globalization;
using System.Text;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Allocation-light, regex-free parser for standard, multi-timestamp, bilingual, and enhanced LRC lyrics.
/// </summary>
public static class LrcParser
{
    private const double BilingualMergeThresholdMs = 20.0;

    private readonly record struct RawTimedLine(TimeSpan Timestamp, int Order, string Text);

    public static LrcDocument Parse(string? lrcText, TimeSpan? knownDuration = null, int userOffsetMs = 0)
    {
        if (string.IsNullOrWhiteSpace(lrcText))
        {
            return LrcDocument.Empty;
        }

        ReadOnlySpan<char> span = lrcText.AsSpan().TrimStart('\uFEFF');
        var rawLines = new List<RawTimedLine>(64);
        var lineTimestamps = new List<TimeSpan>(4);
        int embeddedOffsetMs = 0;
        TimeSpan? parsedLength = null;
        int order = 0;

        foreach (Range lineRange in span.Split('\n'))
        {
            ReadOnlySpan<char> line = span[lineRange].Trim('\r').Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            lineTimestamps.Clear();
            ReadOnlySpan<char> cursor = line;
            while (cursor.Length >= 4 && cursor[0] == '[')
            {
                int closeBracket = cursor.IndexOf(']');
                if (closeBracket <= 0)
                {
                    break;
                }

                ReadOnlySpan<char> tagContent = cursor[1..closeBracket].Trim();
                if (TryParseTimestampTag(tagContent, out TimeSpan timestamp))
                {
                    lineTimestamps.Add(timestamp);
                    cursor = cursor[(closeBracket + 1)..];
                    continue;
                }

                if (TryParseMetadataTag(tagContent, ref embeddedOffsetMs, ref parsedLength))
                {
                    cursor = cursor[(closeBracket + 1)..];
                    continue;
                }

                // Unrecognized bracket tag at start of line (e.g. [ar:...], [ti:...], [by:...]):
                // if we haven't seen any timestamps on this line, skip the metadata line completely.
                if (lineTimestamps.Count == 0 && tagContent.IndexOf(':') > 0)
                {
                    cursor = ReadOnlySpan<char>.Empty;
                }

                break;
            }

            if (lineTimestamps.Count == 0)
            {
                continue;
            }

            string cleanedText = StripInlineWordTimestamps(cursor.Trim());
            foreach (TimeSpan ts in lineTimestamps)
            {
                rawLines.Add(new RawTimedLine(ts, order++, cleanedText));
            }
        }

        TimeSpan? effectiveDuration = knownDuration is { TotalSeconds: > 0 }
            ? knownDuration
            : parsedLength;

        if (rawLines.Count == 0)
        {
            bool plainInstrumental = IsInstrumentalHint(span);
            return plainInstrumental
                ? new LrcDocument([], isInstrumental: true, effectiveDuration, embeddedOffsetMs, userOffsetMs)
                : LrcDocument.Empty;
        }

        rawLines.Sort(static (a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        // Merge bilingual pairs sharing identical (or <= 20ms) timestamps and drop pure-empty spacer lines
        // unless they act as explicit instrumental breaks between sung verses.
        var merged = new List<LrcLine>(rawLines.Count);
        for (int i = 0; i < rawLines.Count; i++)
        {
            RawTimedLine current = rawLines[i];
            if (string.IsNullOrWhiteSpace(current.Text))
            {
                continue;
            }

            string primary = current.Text;
            string? secondary = null;

            while (i + 1 < rawLines.Count &&
                   Math.Abs((rawLines[i + 1].Timestamp - current.Timestamp).TotalMilliseconds) <= BilingualMergeThresholdMs)
            {
                i++;
                string candidate = rawLines[i].Text;
                if (string.IsNullOrWhiteSpace(candidate) ||
                    string.Equals(primary, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (secondary is null)
                {
                    secondary = candidate;
                }
            }

            merged.Add(new LrcLine(current.Timestamp, primary, secondary));
        }

        if (merged.Count == 0)
        {
            return LrcDocument.Empty;
        }

        if (merged.Count <= 2 && merged.All(static line => IsInstrumentalPhrase(line.PrimaryText)))
        {
            return new LrcDocument([], isInstrumental: true, effectiveDuration, embeddedOffsetMs, userOffsetMs);
        }

        return new LrcDocument(merged, isInstrumental: false, effectiveDuration, embeddedOffsetMs, userOffsetMs);
    }

    private static bool TryParseTimestampTag(ReadOnlySpan<char> tag, out TimeSpan timestamp)
    {
        timestamp = default;
        int firstColon = tag.IndexOf(':');
        if (firstColon <= 0 || firstColon >= tag.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> minutesPart = tag[..firstColon];
        if (!int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return false;
        }

        ReadOnlySpan<char> rest = tag[(firstColon + 1)..];
        int fracSep = rest.IndexOfAny('.', ':');
        int seconds;
        int milliseconds = 0;

        if (fracSep < 0)
        {
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                return false;
            }
        }
        else
        {
            ReadOnlySpan<char> secondsPart = rest[..fracSep];
            ReadOnlySpan<char> fracPart = rest[(fracSep + 1)..];
            if (secondsPart.IsEmpty || fracPart.IsEmpty || fracPart.Length > 3)
            {
                return false;
            }

            if (!int.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                !int.TryParse(fracPart, NumberStyles.None, CultureInfo.InvariantCulture, out int rawFraction))
            {
                return false;
            }

            milliseconds = fracPart.Length switch
            {
                1 => rawFraction * 100,
                2 => rawFraction * 10,
                3 => rawFraction,
                _ => 0
            };
        }

        if (seconds < 0 || seconds >= 60 && minutes > 0 && seconds >= 100)
        {
            return false;
        }

        timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    private static bool TryParseMetadataTag(
        ReadOnlySpan<char> tag,
        ref int embeddedOffsetMs,
        ref TimeSpan? parsedLength)
    {
        int colon = tag.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> key = tag[..colon].Trim();
        ReadOnlySpan<char> value = tag[(colon + 1)..].Trim();

        if (key.Equals("offset", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int offset))
            {
                embeddedOffsetMs = Math.Clamp(offset, -60_000, 60_000);
            }

            return true;
        }

        if (key.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseTimestampTag(value, out TimeSpan length) && length > TimeSpan.Zero)
            {
                parsedLength = length;
            }

            return true;
        }

        return false;
    }

    private static string StripInlineWordTimestamps(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        if (text.IndexOf('<') < 0 && text.IndexOf('[') < 0)
        {
            return text.ToString();
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch is '<' or '[')
            {
                char closeChar = ch == '<' ? '>' : ']';
                int closeIdx = text[i..].IndexOf(closeChar);
                if (closeIdx > 1)
                {
                    ReadOnlySpan<char> inner = text.Slice(i + 1, closeIdx - 1);
                    if (TryParseTimestampTag(inner, out _))
                    {
                        i += closeIdx + 1;
                        continue;
                    }
                }
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString().Trim();
    }

    private static bool IsInstrumentalHint(ReadOnlySpan<char> text)
    {
        string trimmed = text.Trim().ToString();
        return trimmed.Length <= 80 && IsInstrumentalPhrase(trimmed);
    }

    internal static bool IsInstrumentalPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim().Trim("[]()（）");
        return trimmed.Equals("Instrumental", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("纯音乐，请欣赏", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("纯音乐,请欣赏", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("纯音乐 请欣赏", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("纯音乐", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("純音樂，請欣賞", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("純音樂", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("纯音乐，请欣赏", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("純音樂，請欣賞", StringComparison.OrdinalIgnoreCase);
    }
}
