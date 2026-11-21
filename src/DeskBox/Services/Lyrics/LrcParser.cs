using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Allocation-light, regex-free parser for standard, multi-timestamp, bilingual, LRCX, QRC, YRC, and KRC lyrics.
/// </summary>
public static class LrcParser
{
    private const double BilingualMergeThresholdMs = 20.0;
    private const double CompanionTranslationMaxDeltaMs = 450.0;

    private readonly record struct RawTimedLine(
        TimeSpan Timestamp,
        int Order,
        string Text,
        bool IsExplicitTranslation = false);

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

            if (line[0] == '{' && line.StartsWith("{\"t\":", StringComparison.Ordinal))
            {
                if (TryParseYrcJsonTimedLine(line, out TimeSpan jsonTimestamp, out string? jsonText) &&
                    !string.IsNullOrWhiteSpace(jsonText))
                {
                    rawLines.Add(new RawTimedLine(jsonTimestamp, order++, jsonText));
                }

                continue;
            }

            lineTimestamps.Clear();
            bool isExplicitTranslation = false;
            bool skipLine = false;
            ReadOnlySpan<char> cursor = line;

            while (cursor.Length >= 3 && cursor[0] == '[')
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

                if (lineTimestamps.Count > 0)
                {
                    if (IsExplicitTranslationRoleTag(tagContent))
                    {
                        isExplicitTranslation = true;
                        cursor = cursor[(closeBracket + 1)..];
                        continue;
                    }

                    if (IsExplicitTransliterationRoleTag(tagContent))
                    {
                        skipLine = true;
                        break;
                    }
                }

                // Unrecognized bracket tag at start of line (e.g. [ar:...], [ti:...], [by:...], [language:...]):
                // if we haven't seen any timestamps on this line, skip the metadata line completely.
                if (lineTimestamps.Count == 0 && tagContent.IndexOf(':') > 0)
                {
                    cursor = ReadOnlySpan<char>.Empty;
                }

                break;
            }

            if (skipLine || lineTimestamps.Count == 0)
            {
                continue;
            }

            string cleanedText = StripInlineWordTimestamps(cursor.Trim());
            foreach (TimeSpan ts in lineTimestamps)
            {
                rawLines.Add(new RawTimedLine(ts, order++, cleanedText, isExplicitTranslation));
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
            if (cmp != 0)
            {
                return cmp;
            }

            int roleCmp = a.IsExplicitTranslation.CompareTo(b.IsExplicitTranslation);
            return roleCmp != 0 ? roleCmp : a.Order.CompareTo(b.Order);
        });

        // Merge bilingual pairs sharing identical (or <= 20ms) timestamps and drop pure-empty spacer lines.
        var merged = new List<LrcLine>(rawLines.Count);
        for (int i = 0; i < rawLines.Count; i++)
        {
            RawTimedLine current = rawLines[i];
            if (string.IsNullOrWhiteSpace(current.Text) || IsTranslationPlaceholder(current.Text))
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
                    IsTranslationPlaceholder(candidate) ||
                    string.Equals(primary, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                secondary ??= candidate;
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

    /// <summary>
    /// Pairs a separate or companion translation lyric document (e.g. QQMusic <c>*_qmts.qrc</c>, NetEase <c>tlyric</c>/<c>ytlrc</c>,
    /// KRC <c>[language:...]</c>, or sidecar <c>*_ts.lrc</c>) onto <paramref name="primary"/>'s <see cref="LrcLine.SecondaryText"/>.
    /// </summary>
    public static LrcDocument MergeTranslation(LrcDocument primary, string? translationText)
    {
        if (!primary.HasSyncedLines || string.IsNullOrWhiteSpace(translationText))
        {
            return primary;
        }

        LrcDocument translationDoc = Parse(translationText);
        if (!translationDoc.HasSyncedLines)
        {
            return primary;
        }

        double offsetAdjustmentMs = translationDoc.EmbeddedOffsetMs - primary.EmbeddedOffsetMs;
        IReadOnlyList<LrcLine> transLines = translationDoc.Lines;
        bool[] transUsed = new bool[transLines.Count];
        LrcLine[]? updated = null;
        int searchStart = 0;

        for (int i = 0; i < primary.Lines.Count; i++)
        {
            LrcLine pLine = primary.Lines[i];
            if (pLine.HasSecondaryText)
            {
                continue;
            }

            double pMs = pLine.Timestamp.TotalMilliseconds;
            double? nextPrimaryMs = i + 1 < primary.Lines.Count
                ? primary.Lines[i + 1].Timestamp.TotalMilliseconds
                : null;

            while (searchStart < transLines.Count &&
                   (transLines[searchStart].Timestamp.TotalMilliseconds + offsetAdjustmentMs) < pMs - CompanionTranslationMaxDeltaMs)
            {
                searchStart++;
            }

            int bestIdx = -1;
            double bestDelta = CompanionTranslationMaxDeltaMs + 1.0;

            for (int j = searchStart; j < transLines.Count; j++)
            {
                double tMs = transLines[j].Timestamp.TotalMilliseconds + offsetAdjustmentMs;
                if (tMs > pMs + CompanionTranslationMaxDeltaMs)
                {
                    break;
                }

                if (transUsed[j])
                {
                    continue;
                }

                string candidateText = transLines[j].PrimaryText;
                if (string.IsNullOrWhiteSpace(candidateText) ||
                    IsTranslationPlaceholder(candidateText) ||
                    string.Equals(pLine.PrimaryText, candidateText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double delta = Math.Abs(tMs - pMs);
                if (nextPrimaryMs.HasValue && Math.Abs(tMs - nextPrimaryMs.Value) < delta)
                {
                    // Closer to the next primary line than to this one; leave it for the next line.
                    continue;
                }

                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIdx = j;
                }
            }

            if (bestIdx >= 0)
            {
                transUsed[bestIdx] = true;
                if (updated is null)
                {
                    updated = new LrcLine[primary.Lines.Count];
                    for (int k = 0; k < primary.Lines.Count; k++)
                    {
                        updated[k] = primary.Lines[k];
                    }
                }

                updated[i] = new LrcLine(pLine.Timestamp, pLine.PrimaryText, transLines[bestIdx].PrimaryText);
            }
        }

        return updated is null
            ? primary
            : new LrcDocument(
                updated,
                primary.IsInstrumental,
                primary.TrackDuration,
                primary.EmbeddedOffsetMs,
                primary.UserOffsetMs);
    }

    internal static bool TryParseBracketTimestampForTranslationSync(ReadOnlySpan<char> tag, out long startMs)
    {
        if (TryParseTimestampTag(tag, out TimeSpan ts))
        {
            startMs = (long)Math.Round(ts.TotalMilliseconds);
            return true;
        }

        startMs = 0;
        return false;
    }

    internal static bool IsTranslationPlaceholder(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> trimmed = text.Trim();
        return !trimmed.IsEmpty && trimmed.Trim('/').IsEmpty;
    }

    private static bool IsExplicitTranslationRoleTag(ReadOnlySpan<char> tag) =>
        tag.Equals("tt", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("trans", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("chs", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("cht", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("cn", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitTransliterationRoleTag(ReadOnlySpan<char> tag) =>
        tag.Equals("romaji", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("roma", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("pron", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("kana", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("pinyin", StringComparison.OrdinalIgnoreCase) ||
        tag.Equals("jyutping", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseYrcJsonTimedLine(
        ReadOnlySpan<char> line,
        out TimeSpan timestamp,
        out string? text)
    {
        timestamp = default;
        text = null;

        try
        {
            int maxBytes = Encoding.UTF8.GetMaxByteCount(line.Length);
            byte[] utf8 = maxBytes <= 4096 ? new byte[Encoding.UTF8.GetByteCount(line)] : Encoding.UTF8.GetBytes(line.ToString());
            if (maxBytes <= 4096)
            {
                Encoding.UTF8.GetBytes(line, utf8);
            }

            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            long? tMs = null;
            var sb = new StringBuilder();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("t"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long parsedT))
                    {
                        tMs = parsedT;
                    }
                }
                else if (reader.ValueTextEquals("c"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType == JsonTokenType.StartObject)
                            {
                                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                                {
                                    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("tx"u8))
                                    {
                                        if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                        {
                                            sb.Append(reader.GetString());
                                        }
                                    }
                                    else if (reader.TokenType == JsonTokenType.PropertyName)
                                    {
                                        reader.Skip();
                                    }
                                }
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (tMs is >= 0 and <= 86_400_000)
            {
                string assembled = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(assembled))
                {
                    timestamp = TimeSpan.FromMilliseconds(tMs.Value);
                    text = assembled;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryParseTimestampTag(ReadOnlySpan<char> tag, out TimeSpan timestamp)
    {
        timestamp = default;

        int firstColon = tag.IndexOf(':');
        if (firstColon < 0)
        {
            // QRC / YRC / KRC millisecond line tag: [startMs,durationMs] or [startMs,durationMs,...]
            int comma = tag.IndexOf(',');
            if (comma <= 0 || comma >= tag.Length - 1)
            {
                return false;
            }

            ReadOnlySpan<char> startPart = tag[..comma].Trim();
            ReadOnlySpan<char> afterComma = tag[(comma + 1)..];
            int secondComma = afterComma.IndexOf(',');
            ReadOnlySpan<char> durPart = (secondComma >= 0 ? afterComma[..secondComma] : afterComma).Trim();

            if (!long.TryParse(startPart, NumberStyles.None, CultureInfo.InvariantCulture, out long startMs) ||
                !long.TryParse(durPart, NumberStyles.None, CultureInfo.InvariantCulture, out long durMs))
            {
                return false;
            }

            if (startMs is < 0 or > 86_400_000 || durMs is < 0 or > 86_400_000)
            {
                return false;
            }

            timestamp = TimeSpan.FromMilliseconds(startMs);
            return true;
        }

        if (firstColon == 0 || firstColon >= tag.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> firstSegment = tag[..firstColon];
        ReadOnlySpan<char> rest = tag[(firstColon + 1)..];

        // Support [hh:mm:ss.xxx] when there is a second colon AND a decimal separator in the seconds field,
        // while keeping [mm:ss:xx] (two colons, no decimal separator) as centiseconds for legacy LRC compatibility.
        int secondColonInRest = rest.IndexOf(':');
        if (secondColonInRest > 0 && secondColonInRest < rest.Length - 1)
        {
            ReadOnlySpan<char> afterSecondColon = rest[(secondColonInRest + 1)..];
            int decimalInThird = afterSecondColon.IndexOfAny('.', ',');
            if (decimalInThird > 0 && decimalInThird < afterSecondColon.Length - 1)
            {
                if (!int.TryParse(firstSegment, NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
                    !int.TryParse(rest[..secondColonInRest], NumberStyles.None, CultureInfo.InvariantCulture, out int hhMinutes) ||
                    !TryParseSecondsWithFraction(afterSecondColon, decimalInThird, out int hhSeconds, out int hhMilliseconds))
                {
                    return false;
                }

                if (hours < 0 || hhMinutes is < 0 or >= 60 || hhSeconds is < 0 or >= 60)
                {
                    return false;
                }

                timestamp = new TimeSpan(0, hours, hhMinutes, hhSeconds, hhMilliseconds);
                return true;
            }
        }

        if (!int.TryParse(firstSegment, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return false;
        }

        int fracSep = rest.IndexOfAny('.', ':', ',');
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
            if (!TryParseSecondsWithFraction(rest, fracSep, out seconds, out milliseconds))
            {
                return false;
            }
        }

        if (seconds < 0 || (seconds >= 60 && minutes > 0 && seconds >= 100))
        {
            return false;
        }

        timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    private static bool TryParseSecondsWithFraction(
        ReadOnlySpan<char> segment,
        int separatorIndex,
        out int seconds,
        out int milliseconds)
    {
        seconds = 0;
        milliseconds = 0;

        ReadOnlySpan<char> secondsPart = segment[..separatorIndex];
        ReadOnlySpan<char> fracPart = segment[(separatorIndex + 1)..];
        if (secondsPart.IsEmpty || fracPart.IsEmpty)
        {
            return false;
        }

        if (fracPart.Length > 3)
        {
            fracPart = fracPart[..3];
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

        if (key.Equals("total", StringComparison.OrdinalIgnoreCase))
        {
            if (parsedLength is null &&
                long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long totalMs) &&
                totalMs is > 0 and <= 86_400_000)
            {
                parsedLength = TimeSpan.FromMilliseconds(totalMs);
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

        if (text.IndexOf('<') < 0 && text.IndexOf('[') < 0 && text.IndexOf('(') < 0)
        {
            return text.ToString();
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch is '<' or '[' or '(')
            {
                char closeChar = ch switch
                {
                    '<' => '>',
                    '[' => ']',
                    _ => ')'
                };

                int closeIdx = text[i..].IndexOf(closeChar);
                if (closeIdx > 1)
                {
                    ReadOnlySpan<char> inner = text.Slice(i + 1, closeIdx - 1);
                    bool isWordTag = ch switch
                    {
                        '(' => IsNumericWordTimingTuple(inner),
                        '<' => TryParseTimestampTag(inner, out _) || IsNumericWordTimingTuple(inner),
                        _ => TryParseTimestampTag(inner, out _)
                    };

                    if (isWordTag)
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

    private static bool IsNumericWordTimingTuple(ReadOnlySpan<char> inner)
    {
        if (inner.Length < 3 || inner.Length > 36)
        {
            return false;
        }

        int commaCount = 0;
        int segmentIndex = 0;
        int segmentDigits = 0;
        bool atSegmentStart = true;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == ',')
            {
                if (segmentDigits == 0 || commaCount >= 2)
                {
                    return false;
                }

                commaCount++;
                segmentIndex++;
                segmentDigits = 0;
                atSegmentStart = true;
                continue;
            }

            if (c == '-' && atSegmentStart && segmentIndex > 0)
            {
                atSegmentStart = false;
                continue;
            }

            if (!char.IsAsciiDigit(c) || segmentDigits >= 10)
            {
                return false;
            }

            segmentDigits++;
            atSegmentStart = false;
        }

        return commaCount is 1 or 2 && segmentDigits > 0;
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
