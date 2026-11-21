using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace DeskBox.Services.Lyrics;

internal readonly record struct DecodedLocalLyricsPayload(
    string? PrimaryText,
    string? TranslationText = null,
    bool IsInstrumental = false)
{
    public bool HasContent => IsInstrumental || !string.IsNullOrWhiteSpace(PrimaryText);
}

/// <summary>
/// Offline, Native-AOT-safe decoder for encrypted and structured local lyric formats:
/// QQMusic <c>.qrc</c> (QMC1 + Triple-DES + zlib / XML), Kugou <c>.krc</c> (<c>krc1</c> XOR + zlib + embedded Base64 language JSON),
/// NetEase CloudMusic <c>.yrc</c> and lyric JSON (<c>lrc</c>/<c>tlyric</c>/<c>yrc</c>/<c>ytlrc</c>),
/// SubRip <c>.srt</c>, WebVTT <c>.vtt</c>, and Apple Music / AMLL <c>.ttml</c>.
/// </summary>
internal static class LocalLyricsFormatDecoder
{
    private const int MaxDecompressedBytes = 2 * 1024 * 1024;

    private static ReadOnlySpan<byte> QrcQmcMagic =>
    [
        0x98, 0x25, 0xB0, 0xAC, 0xE3, 0x02, 0x83, 0x68, 0xE8, 0xFC, 0x6C
    ];

    private static ReadOnlySpan<byte> Qmc1StaticKey =>
    [
        0xC3, 0x4A, 0xD6, 0xCA, 0x90, 0x67, 0xF7, 0x52, 0xD8, 0xA1, 0x66, 0x62, 0x9F, 0x5B, 0x09, 0x00,
        0xC3, 0x5E, 0x95, 0x23, 0x9F, 0x13, 0x11, 0x7E, 0xD8, 0x92, 0x3F, 0xBC, 0x90, 0xBB, 0x74, 0x0E,
        0xC3, 0x47, 0x74, 0x3D, 0x90, 0xAA, 0x3F, 0x51, 0xD8, 0xF4, 0x11, 0x84, 0x9F, 0xDE, 0x95, 0x1D,
        0xC3, 0xC6, 0x09, 0xD5, 0x9F, 0xFA, 0x66, 0xF9, 0xD8, 0xF0, 0xF7, 0xA0, 0x90, 0xA1, 0xD6, 0xF3,
        0xC3, 0xF3, 0xD6, 0xA1, 0x90, 0xA0, 0xF7, 0xF0, 0xD8, 0xF9, 0x66, 0xFA, 0x9F, 0xD5, 0x09, 0xC6,
        0xC3, 0x1D, 0x95, 0xDE, 0x9F, 0x84, 0x11, 0xF4, 0xD8, 0x51, 0x3F, 0xAA, 0x90, 0x3D, 0x74, 0x47,
        0xC3, 0x0E, 0x74, 0xBB, 0x90, 0xBC, 0x3F, 0x92, 0xD8, 0x7E, 0x11, 0x13, 0x9F, 0x23, 0x95, 0x5E,
        0xC3, 0x00, 0x09, 0x5B, 0x9F, 0x62, 0x66, 0xA1, 0xD8, 0x52, 0xF7, 0x67, 0x90, 0xCA, 0xD6, 0x4A
    ];

    private static ReadOnlySpan<byte> Krc1Magic => "krc1"u8;

    private static ReadOnlySpan<byte> KrcXorKey =>
    [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

    private static readonly Lazy<(ulong[] K1Dec, ulong[] K2Enc, ulong[] K3Dec)> s_qrcSubkeys = new(
        static () => (
            BuildQqDesSubkeys("!@#)(NHL"u8, encrypt: false),
            BuildQqDesSubkeys("123ZXC!@"u8, encrypt: true),
            BuildQqDesSubkeys("!@#)(*$%"u8, encrypt: false)));

    public static DecodedLocalLyricsPayload DecodeBytes(byte[] rawBytes, string? fileExtension)
    {
        if (rawBytes.Length == 0)
        {
            return default;
        }

        string ext = (fileExtension ?? string.Empty).ToLowerInvariant();

        // 1. Kugou binary krc1 header
        if (rawBytes.AsSpan().StartsWith(Krc1Magic))
        {
            byte[]? decompressedKrc = TryDecryptKrcBytes(rawBytes);
            if (decompressedKrc is null)
            {
                return default;
            }

            string krcText = LocalFileLyricsProvider.DecodeLrcBytes(decompressedKrc);
            return UnpackKrcText(krcText);
        }

        // 2. QQMusic local QMC1-wrapped QRC header (98 25 B0 AC E3 02 83 68 E8 FC 6C)
        if (rawBytes.AsSpan().StartsWith(QrcQmcMagic))
        {
            byte[]? decompressedQrc = TryDecryptQrcBytes(rawBytes);
            if (decompressedQrc is null)
            {
                return default;
            }

            string qrcText = LocalFileLyricsProvider.DecodeLrcBytes(decompressedQrc);
            return UnpackTextPayload(qrcText, ".qrc");
        }

        // 3. Raw encrypted QRC (no QMC1 header, 8-byte block aligned binary in .qrc file)
        if (ext == ".qrc" && !LooksLikePlainText(rawBytes) && rawBytes.Length >= 16 && (rawBytes.Length % 8) == 0)
        {
            byte[]? decompressedQrc = TryDecryptQrcBytes(rawBytes);
            if (decompressedQrc is not null)
            {
                string qrcText = LocalFileLyricsProvider.DecodeLrcBytes(decompressedQrc);
                return UnpackTextPayload(qrcText, ".qrc");
            }
        }

        string decodedText = LocalFileLyricsProvider.DecodeLrcBytes(rawBytes);
        return UnpackTextPayload(decodedText, ext);
    }

    internal static DecodedLocalLyricsPayload UnpackTextPayload(string? rawText, string? fileExtension)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return default;
        }

        ReadOnlySpan<char> trimmed = rawText.AsSpan().TrimStart('\uFEFF').Trim();
        if (trimmed.IsEmpty)
        {
            return default;
        }

        string ext = (fileExtension ?? string.Empty).ToLowerInvariant();

        // QRC XML container (<QrcInfos> ... <Lyric_1 LyricContent="..." />)
        if (trimmed.Contains("<QrcInfos", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Contains("LyricContent", StringComparison.OrdinalIgnoreCase))
        {
            var contents = ExtractQrcXmlLyricContents(trimmed);
            if (contents.Count > 0)
            {
                string primary = contents[0];
                string? translation = contents.Count > 1 ? contents[1] : null;
                return new DecodedLocalLyricsPayload(primary, translation);
            }
        }

        // Apple Music / AMLL TTML XML container (<tt ...> ... <p begin="...">)
        if (ext == ".ttml" ||
            (trimmed.StartsWith("<", StringComparison.Ordinal) &&
             (trimmed.Contains("<tt ", StringComparison.OrdinalIgnoreCase) ||
              trimmed.Contains("<tt>", StringComparison.OrdinalIgnoreCase)) &&
             trimmed.Contains("<p ", StringComparison.OrdinalIgnoreCase)))
        {
            return ParseTtmlToSyntheticLrc(rawText);
        }

        // NetEase CloudMusic lyric JSON ({"lrc":{"lyric":"..."},"tlyric":{"lyric":"..."},"yrc":{"lyric":"..."}})
        if (trimmed[0] == '{' &&
            !trimmed.StartsWith("{\"t\":", StringComparison.Ordinal) &&
            (ext == ".json" ||
             trimmed.Contains("\"lrc\"", StringComparison.Ordinal) ||
             trimmed.Contains("\"yrc\"", StringComparison.Ordinal)))
        {
            DecodedLocalLyricsPayload jsonPayload = TryParseNetEaseJson(rawText);
            if (jsonPayload.HasContent)
            {
                return jsonPayload;
            }
        }

        // Plaintext KRC with embedded [language:...] Base64 block
        if (ext == ".krc" || trimmed.Contains("[language:", StringComparison.OrdinalIgnoreCase))
        {
            return UnpackKrcText(rawText);
        }

        // WebVTT / SubRip subtitle cues
        if (ext is ".srt" or ".vtt" ||
            trimmed.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(" --> ", StringComparison.Ordinal))
        {
            DecodedLocalLyricsPayload cues = TryParseSubtitleCuesToSyntheticLrc(trimmed);
            if (cues.HasContent)
            {
                return cues;
            }
        }

        return new DecodedLocalLyricsPayload(rawText);
    }

    internal static byte[]? TryDecryptKrcBytes(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.Length <= 4 || !rawBytes.StartsWith(Krc1Magic))
        {
            return null;
        }

        ReadOnlySpan<byte> key = KrcXorKey;
        byte[] decoded = new byte[rawBytes.Length - 4];
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] = (byte)(rawBytes[i + 4] ^ key[i & 0x0F]);
        }

        return TryDecompressZlibBounded(decoded);
    }

    internal static byte[] EncryptKrcBytesForTesting(ReadOnlySpan<byte> utf8PlainBytes)
    {
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zs.Write(utf8PlainBytes);
            }

            compressed = ms.ToArray();
        }

        ReadOnlySpan<byte> key = KrcXorKey;
        byte[] output = new byte[4 + compressed.Length];
        Krc1Magic.CopyTo(output);
        for (int i = 0; i < compressed.Length; i++)
        {
            output[4 + i] = (byte)(compressed[i] ^ key[i & 0x0F]);
        }

        return output;
    }

    internal static byte[]? TryDecryptQrcBytes(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.Length < 16)
        {
            return null;
        }

        byte[] cipherBuffer;
        if (rawBytes.StartsWith(QrcQmcMagic))
        {
            int bodyLength = rawBytes.Length - QrcQmcMagic.Length;
            if (bodyLength < 8 || (bodyLength % 8) != 0)
            {
                return null;
            }

            ReadOnlySpan<byte> qmcKey = Qmc1StaticKey;
            cipherBuffer = new byte[bodyLength];
            for (int i = QrcQmcMagic.Length; i < rawBytes.Length; i++)
            {
                int maskIndex = i > 0x7FFF ? ((i % 0x7FFF) & 0x7F) : (i & 0x7F);
                cipherBuffer[i - QrcQmcMagic.Length] = (byte)(rawBytes[i] ^ qmcKey[maskIndex]);
            }
        }
        else
        {
            if ((rawBytes.Length % 8) != 0)
            {
                return null;
            }

            cipherBuffer = rawBytes.ToArray();
        }

        var (k1Dec, k2Enc, k3Dec) = s_qrcSubkeys.Value;
        for (int offset = 0; offset < cipherBuffer.Length; offset += 8)
        {
            Span<byte> block = cipherBuffer.AsSpan(offset, 8);
            ulong state = BinaryPrimitives.ReadUInt64LittleEndian(block);
            state = QqDesTransformBlock(state, k1Dec);
            state = QqDesTransformBlock(state, k2Enc);
            state = QqDesTransformBlock(state, k3Dec);
            BinaryPrimitives.WriteUInt64LittleEndian(block, state);
        }

        return TryDecompressZlibBounded(cipherBuffer);
    }

    internal static byte[] EncryptLocalQmcQrcBytesForTesting(ReadOnlySpan<byte> utf8PlainBytes)
    {
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zs.Write(utf8PlainBytes);
            }

            compressed = ms.ToArray();
        }

        int paddedLen = (compressed.Length + 7) & ~7;
        Array.Resize(ref compressed, paddedLen);

        ulong[] k3Enc = BuildQqDesSubkeys("!@#)(*$%"u8, encrypt: true);
        ulong[] k2Dec = BuildQqDesSubkeys("123ZXC!@"u8, encrypt: false);
        ulong[] k1Enc = BuildQqDesSubkeys("!@#)(NHL"u8, encrypt: true);

        for (int offset = 0; offset < compressed.Length; offset += 8)
        {
            Span<byte> block = compressed.AsSpan(offset, 8);
            ulong state = BinaryPrimitives.ReadUInt64LittleEndian(block);
            state = QqDesTransformBlock(state, k3Enc);
            state = QqDesTransformBlock(state, k2Dec);
            state = QqDesTransformBlock(state, k1Enc);
            BinaryPrimitives.WriteUInt64LittleEndian(block, state);
        }

        ReadOnlySpan<byte> prefix = "[offset:0]\n"u8;
        byte[] output = new byte[prefix.Length + compressed.Length];
        prefix.CopyTo(output);
        compressed.CopyTo(output.AsSpan(prefix.Length));

        ReadOnlySpan<byte> qmcKey = Qmc1StaticKey;
        for (int i = 0; i < output.Length; i++)
        {
            int maskIndex = i > 0x7FFF ? ((i % 0x7FFF) & 0x7F) : (i & 0x7F);
            output[i] ^= qmcKey[maskIndex];
        }

        return output;
    }

    private static byte[]? TryDecompressZlibBounded(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(Math.Min(compressed.Length * 3, 65_536));
            byte[] chunk = new byte[8192];
            int total = 0;
            while (true)
            {
                int read = zlib.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total > MaxDecompressedBytes)
                {
                    return null;
                }

                output.Write(chunk, 0, read);
            }

            return total > 0 ? output.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikePlainText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return true;
        }

        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        int i = 0;
        while (i < bytes.Length && i < 16 && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < bytes.Length && bytes[i] is (byte)'[' or (byte)'<' or (byte)'{';
    }

    private static List<string> ExtractQrcXmlLyricContents(ReadOnlySpan<char> xml)
    {
        var results = new List<string>(2);
        const string attrName = "LyricContent";
        int cursor = 0;

        while (cursor < xml.Length)
        {
            int idx = xml[cursor..].IndexOf(attrName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                break;
            }

            int pos = cursor + idx + attrName.Length;
            while (pos < xml.Length && char.IsWhiteSpace(xml[pos]))
            {
                pos++;
            }

            if (pos >= xml.Length || xml[pos] != '=')
            {
                cursor = pos;
                continue;
            }

            pos++;
            while (pos < xml.Length && char.IsWhiteSpace(xml[pos]))
            {
                pos++;
            }

            if (pos >= xml.Length || (xml[pos] != '"' && xml[pos] != '\''))
            {
                cursor = pos;
                continue;
            }

            char quote = xml[pos++];
            int closeQuote = xml[pos..].IndexOf(quote);
            if (closeQuote < 0)
            {
                break;
            }

            ReadOnlySpan<char> rawValue = xml.Slice(pos, closeQuote);
            string unescaped = UnescapeXmlAttributePreservingNewlines(rawValue);
            if (!string.IsNullOrWhiteSpace(unescaped))
            {
                results.Add(unescaped);
            }

            cursor = pos + closeQuote + 1;
        }

        return results;
    }

    private static string UnescapeXmlAttributePreservingNewlines(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('&') < 0)
        {
            return value.ToString();
        }

        var sb = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '&')
            {
                int semi = value[i..].IndexOf(';');
                if (semi > 1 && semi <= 12)
                {
                    ReadOnlySpan<char> entity = value.Slice(i + 1, semi - 1);
                    if (TryDecodeXmlEntity(entity, out string? decoded))
                    {
                        sb.Append(decoded);
                        i += semi + 1;
                        continue;
                    }
                }
            }

            sb.Append(value[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryDecodeXmlEntity(ReadOnlySpan<char> entity, out string? decoded)
    {
        decoded = null;
        if (entity.Equals("quot", StringComparison.Ordinal))
        {
            decoded = "\"";
            return true;
        }

        if (entity.Equals("apos", StringComparison.Ordinal))
        {
            decoded = "'";
            return true;
        }

        if (entity.Equals("amp", StringComparison.Ordinal))
        {
            decoded = "&";
            return true;
        }

        if (entity.Equals("lt", StringComparison.Ordinal))
        {
            decoded = "<";
            return true;
        }

        if (entity.Equals("gt", StringComparison.Ordinal))
        {
            decoded = ">";
            return true;
        }

        if (entity.Length >= 2 && entity[0] == '#')
        {
            int codePoint;
            bool ok = (entity[1] is 'x' or 'X')
                ? int.TryParse(entity[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint)
                : int.TryParse(entity[1..], NumberStyles.None, CultureInfo.InvariantCulture, out codePoint);
            if (ok && UnicodeScalarIsValid(codePoint))
            {
                decoded = char.ConvertFromUtf32(codePoint);
                return true;
            }
        }

        return false;
    }

    private static bool UnicodeScalarIsValid(int codePoint) =>
        codePoint is >= 0 and <= 0x10FFFF && (codePoint < 0xD800 || codePoint > 0xDFFF);

    private static DecodedLocalLyricsPayload UnpackKrcText(string krcText)
    {
        ReadOnlySpan<char> span = krcText.AsSpan().TrimStart('\uFEFF');
        List<string?>? rawTranslations = null;

        int langTagStart = span.IndexOf("[language:", StringComparison.OrdinalIgnoreCase);
        if (langTagStart >= 0)
        {
            int afterPrefix = langTagStart + "[language:".Length;
            int closeBracket = span[afterPrefix..].IndexOf(']');
            if (closeBracket > 0)
            {
                ReadOnlySpan<char> base64Slice = span.Slice(afterPrefix, closeBracket).Trim();
                rawTranslations = TryExtractKrcBase64Translations(base64Slice);
            }
        }

        if (rawTranslations is null || rawTranslations.Count == 0)
        {
            return new DecodedLocalLyricsPayload(krcText);
        }

        // Pair KRC line-indexed translations directly with each timed [startMs,durMs] line in the KRC body.
        var translationBuilder = new StringBuilder();
        int timedLineIndex = 0;
        foreach (Range lineRange in span.Split('\n'))
        {
            ReadOnlySpan<char> line = span[lineRange].Trim('\r').Trim();
            if (line.Length < 4 || line[0] != '[')
            {
                continue;
            }

            int closeBracket = line.IndexOf(']');
            if (closeBracket <= 1)
            {
                continue;
            }

            ReadOnlySpan<char> tag = line[1..closeBracket].Trim();
            if (!LrcParser.TryParseBracketTimestampForTranslationSync(tag, out long startMs))
            {
                continue;
            }

            if (timedLineIndex < rawTranslations.Count)
            {
                string? trans = rawTranslations[timedLineIndex];
                if (!string.IsNullOrWhiteSpace(trans) && !LrcParser.IsTranslationPlaceholder(trans))
                {
                    translationBuilder.Append('[')
                        .Append(startMs.ToString(CultureInfo.InvariantCulture))
                        .Append(",0]")
                        .AppendLine(trans.Trim());
                }
            }

            timedLineIndex++;
        }

        string? synthesizedTranslation = translationBuilder.Length > 0 ? translationBuilder.ToString() : null;
        return new DecodedLocalLyricsPayload(krcText, synthesizedTranslation);
    }

    private static List<string?>? TryExtractKrcBase64Translations(ReadOnlySpan<char> base64)
    {
        try
        {
            byte[] utf8Json = Convert.FromBase64String(base64.ToString());
            var reader = new Utf8JsonReader(utf8Json);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("content"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                    {
                        return null;
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            continue;
                        }

                        int itemType = -1;
                        List<string?>? lines = null;

                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName)
                            {
                                continue;
                            }

                            if (reader.ValueTextEquals("type"u8))
                            {
                                if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                                {
                                    itemType = reader.GetInt32();
                                }
                            }
                            else if (reader.ValueTextEquals("lyricContent"u8))
                            {
                                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                                {
                                    lines = [];
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                                    {
                                        if (reader.TokenType == JsonTokenType.StartArray)
                                        {
                                            var lineSb = new StringBuilder();
                                            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                                            {
                                                if (reader.TokenType == JsonTokenType.String)
                                                {
                                                    lineSb.Append(reader.GetString());
                                                }
                                            }

                                            lines.Add(lineSb.ToString());
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

                        // type == 1 is KRC bilingual translation (type == 0 is romaji/transliteration).
                        if (itemType == 1 && lines is { Count: > 0 })
                        {
                            return lines;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static DecodedLocalLyricsPayload TryParseNetEaseJson(string jsonText)
    {
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(jsonText);
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            string? lrc = null;
            string? tlyric = null;
            string? yrc = null;
            string? ytlrc = null;
            bool nolyric = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("lrc"u8))
                {
                    lrc = ReadNestedLyricField(ref reader);
                }
                else if (reader.ValueTextEquals("tlyric"u8))
                {
                    tlyric = ReadNestedLyricField(ref reader);
                }
                else if (reader.ValueTextEquals("yrc"u8))
                {
                    yrc = ReadNestedLyricField(ref reader);
                }
                else if (reader.ValueTextEquals("ytlrc"u8))
                {
                    ytlrc = ReadNestedLyricField(ref reader);
                }
                else if (reader.ValueTextEquals("nolyric"u8))
                {
                    if (reader.Read() && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                    {
                        nolyric = reader.GetBoolean();
                    }
                }
                else if (reader.ValueTextEquals("lyric"u8))
                {
                    // Flat {"lyric":"..."} NetEase local cache variant
                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        lrc ??= reader.GetString();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            string? primary = !string.IsNullOrWhiteSpace(yrc) ? yrc : lrc;
            string? translation = !string.IsNullOrWhiteSpace(yrc)
                ? (!string.IsNullOrWhiteSpace(ytlrc) ? ytlrc : tlyric)
                : (!string.IsNullOrWhiteSpace(tlyric) ? tlyric : ytlrc);

            if (!string.IsNullOrWhiteSpace(primary))
            {
                return new DecodedLocalLyricsPayload(primary, translation);
            }

            if (nolyric)
            {
                return new DecodedLocalLyricsPayload(null, null, IsInstrumental: true);
            }
        }
        catch
        {
        }

        return default;
    }

    private static string? ReadNestedLyricField(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        string? result = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("lyric"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    result = reader.GetString();
                }
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                reader.Skip();
            }
        }

        return result;
    }

    private static DecodedLocalLyricsPayload TryParseSubtitleCuesToSyntheticLrc(ReadOnlySpan<char> text)
    {
        var primaryBuilder = new StringBuilder();
        var translationBuilder = new StringBuilder();
        var blockLines = new List<string>(4);
        int cueCount = 0;

        void FlushBlock()
        {
            if (blockLines.Count == 0)
            {
                return;
            }

            int arrowLineIdx = -1;
            long cueStartMs = 0;
            for (int i = 0; i < blockLines.Count; i++)
            {
                int arrowPos = blockLines[i].IndexOf("-->", StringComparison.Ordinal);
                if (arrowPos > 0 &&
                    TryParseFlexibleTimecodeMs(blockLines[i].AsSpan(0, arrowPos).Trim(), out cueStartMs))
                {
                    arrowLineIdx = i;
                    break;
                }
            }

            if (arrowLineIdx >= 0 && arrowLineIdx + 1 < blockLines.Count)
            {
                var contentLines = new List<string>(2);
                for (int i = arrowLineIdx + 1; i < blockLines.Count; i++)
                {
                    string stripped = StripSubtitleMarkupTags(blockLines[i]);
                    if (!string.IsNullOrWhiteSpace(stripped))
                    {
                        contentLines.Add(stripped);
                    }
                }

                if (contentLines.Count > 0)
                {
                    cueCount++;
                    primaryBuilder.Append('[')
                        .Append(cueStartMs.ToString(CultureInfo.InvariantCulture))
                        .Append(",0]")
                        .AppendLine(contentLines[0]);

                    if (contentLines.Count > 1)
                    {
                        translationBuilder.Append('[')
                            .Append(cueStartMs.ToString(CultureInfo.InvariantCulture))
                            .Append(",0]")
                            .AppendLine(contentLines[1]);
                    }
                }
            }

            blockLines.Clear();
        }

        foreach (Range range in text.Split('\n'))
        {
            ReadOnlySpan<char> line = text[range].Trim('\r').Trim();
            if (line.IsEmpty)
            {
                FlushBlock();
                continue;
            }

            if (cueCount == 0 && blockLines.Count == 0 &&
                line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            blockLines.Add(line.ToString());
        }

        FlushBlock();
        if (cueCount == 0)
        {
            return default;
        }

        return new DecodedLocalLyricsPayload(
            primaryBuilder.ToString(),
            translationBuilder.Length > 0 ? translationBuilder.ToString() : null);
    }

    private static string StripSubtitleMarkupTags(string line)
    {
        if (line.IndexOf('<') < 0 && line.IndexOf('{') < 0)
        {
            return line.Trim();
        }

        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '<')
            {
                int close = line.IndexOf('>', i + 1);
                if (close > i)
                {
                    i = close + 1;
                    continue;
                }
            }
            else if (line[i] == '{' && i + 1 < line.Length && line[i + 1] == '\\')
            {
                int close = line.IndexOf('}', i + 2);
                if (close > i)
                {
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(line[i]);
            i++;
        }

        return sb.ToString().Trim();
    }

    private static DecodedLocalLyricsPayload ParseTtmlToSyntheticLrc(string xmlText)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };

            var primaryBuilder = new StringBuilder();
            var translationBuilder = new StringBuilder();
            int lineCount = 0;

            using var stringReader = new StringReader(xmlText);
            using var reader = XmlReader.Create(stringReader, settings);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element ||
                    !string.Equals(reader.LocalName, "p", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? beginAttr = reader.GetAttribute("begin");
                if (string.IsNullOrWhiteSpace(beginAttr) ||
                    !TryParseFlexibleTimecodeMs(beginAttr.AsSpan().Trim(), out long beginMs))
                {
                    continue;
                }

                if (reader.IsEmptyElement)
                {
                    continue;
                }

                var linePrimary = new StringBuilder();
                var lineTranslation = new StringBuilder();
                using XmlReader subtree = reader.ReadSubtree();
                subtree.Read(); // Move onto <p>

                int skipDepth = -1;
                int translationDepth = -1;

                while (subtree.Read())
                {
                    if (skipDepth >= 0)
                    {
                        if (subtree.NodeType == XmlNodeType.EndElement && subtree.Depth == skipDepth)
                        {
                            skipDepth = -1;
                        }

                        continue;
                    }

                    if (translationDepth >= 0)
                    {
                        if (subtree.NodeType == XmlNodeType.EndElement && subtree.Depth == translationDepth)
                        {
                            translationDepth = -1;
                        }
                        else if (subtree.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                        {
                            lineTranslation.Append(subtree.Value);
                        }

                        continue;
                    }

                    if (subtree.NodeType == XmlNodeType.Element &&
                        string.Equals(subtree.LocalName, "span", StringComparison.OrdinalIgnoreCase))
                    {
                        string? role = subtree.GetAttribute("role", "http://www.w3.org/ns/ttml#metadata")
                            ?? subtree.GetAttribute("ttm:role");
                        if (!subtree.IsEmptyElement)
                        {
                            if (string.Equals(role, "x-translation", StringComparison.OrdinalIgnoreCase))
                            {
                                translationDepth = subtree.Depth;
                                continue;
                            }

                            if (string.Equals(role, "x-roman", StringComparison.OrdinalIgnoreCase))
                            {
                                skipDepth = subtree.Depth;
                                continue;
                            }
                        }
                    }
                    else if (subtree.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    {
                        linePrimary.Append(subtree.Value);
                    }
                }

                string primaryLine = CollapseInlineWhitespace(linePrimary.ToString());
                if (!string.IsNullOrWhiteSpace(primaryLine))
                {
                    lineCount++;
                    primaryBuilder.Append('[')
                        .Append(beginMs.ToString(CultureInfo.InvariantCulture))
                        .Append(",0]")
                        .AppendLine(primaryLine);

                    string transLine = CollapseInlineWhitespace(lineTranslation.ToString());
                    if (!string.IsNullOrWhiteSpace(transLine) && !LrcParser.IsTranslationPlaceholder(transLine))
                    {
                        translationBuilder.Append('[')
                            .Append(beginMs.ToString(CultureInfo.InvariantCulture))
                            .Append(",0]")
                            .AppendLine(transLine);
                    }
                }
            }

            if (lineCount > 0)
            {
                return new DecodedLocalLyricsPayload(
                    primaryBuilder.ToString(),
                    translationBuilder.Length > 0 ? translationBuilder.ToString() : null);
            }
        }
        catch
        {
        }

        return default;
    }

    private static string CollapseInlineWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        bool prevSpace = true;
        foreach (char ch in input)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    internal static bool TryParseFlexibleTimecodeMs(ReadOnlySpan<char> token, out long milliseconds)
    {
        milliseconds = 0;
        if (token.IsEmpty)
        {
            return false;
        }

        // TTML offset-time suffixes: "12340ms" or "12.34s"
        if (token.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(token[..^2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double ms) &&
                ms is >= 0 and <= 86_400_000)
            {
                milliseconds = (long)Math.Round(ms);
                return true;
            }

            return false;
        }

        if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(token[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double sec) &&
                sec is >= 0 and <= 86_400)
            {
                milliseconds = (long)Math.Round(sec * 1000.0);
                return true;
            }

            return false;
        }

        // HH:MM:SS,mmm / HH:MM:SS.mmm / MM:SS.mmm / SS.mmm
        int firstColon = token.IndexOf(':');
        if (firstColon < 0)
        {
            if (double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double secOnly) &&
                secOnly is >= 0 and <= 86_400)
            {
                milliseconds = (long)Math.Round(secOnly * 1000.0);
                return true;
            }

            return false;
        }

        int hours = 0;
        int minutes;
        ReadOnlySpan<char> secAndFrac;

        ReadOnlySpan<char> afterFirst = token[(firstColon + 1)..];
        int secondColon = afterFirst.IndexOf(':');
        if (secondColon >= 0)
        {
            if (!int.TryParse(token[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(afterFirst[..secondColon], NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            {
                return false;
            }

            secAndFrac = afterFirst[(secondColon + 1)..];
        }
        else
        {
            if (!int.TryParse(token[..firstColon], NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            {
                return false;
            }

            secAndFrac = afterFirst;
        }

        int sep = secAndFrac.IndexOfAny('.', ',');
        int seconds;
        int msPart = 0;
        if (sep < 0)
        {
            if (!int.TryParse(secAndFrac, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                return false;
            }
        }
        else
        {
            ReadOnlySpan<char> frac = secAndFrac[(sep + 1)..];
            if (frac.Length > 3)
            {
                frac = frac[..3];
            }

            if (!int.TryParse(secAndFrac[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                (!frac.IsEmpty && !int.TryParse(frac, NumberStyles.None, CultureInfo.InvariantCulture, out msPart)))
            {
                return false;
            }

            msPart = frac.Length switch
            {
                1 => msPart * 100,
                2 => msPart * 10,
                3 => msPart,
                _ => 0
            };
        }

        if (hours < 0 || minutes < 0 || seconds is < 0 or >= 60)
        {
            return false;
        }

        milliseconds = (((long)hours * 3600L + (long)minutes * 60L + seconds) * 1000L) + msPart;
        return true;
    }

    // ── QQ-variant DES implementation (zero-allocation static tables) ──

    private static ReadOnlySpan<byte> KeyRndShifts =>
    [
        0x01, 0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
        0x01, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x01
    ];

    private static ReadOnlySpan<byte> LargeStateShifts =>
    [
        0x1A, 0x14, 0x0E, 0x08, 0x3A, 0x34, 0x2E, 0x28
    ];

    private static ReadOnlySpan<byte> SBoxesFlat =>
    [
        // S1
        14, 0, 4, 15, 13, 7, 1, 4, 2, 14, 15, 2, 11, 13, 8, 1, 3, 10, 10, 6, 6, 12, 12, 11, 5, 9, 9, 5, 0, 3, 7, 8,
        4, 15, 1, 12, 14, 8, 8, 2, 13, 4, 6, 9, 2, 1, 11, 7, 15, 5, 12, 11, 9, 3, 7, 14, 3, 10, 10, 0, 5, 6, 0, 13,
        // S2
        15, 3, 1, 13, 8, 4, 14, 7, 6, 15, 11, 2, 3, 8, 4, 15, 9, 12, 7, 0, 2, 1, 13, 10, 12, 6, 0, 9, 5, 11, 10, 5,
        0, 13, 14, 8, 7, 10, 11, 1, 10, 3, 4, 15, 13, 4, 1, 2, 5, 11, 8, 6, 12, 7, 6, 12, 9, 0, 3, 5, 2, 14, 15, 9,
        // S3
        10, 13, 0, 7, 9, 0, 14, 9, 6, 3, 3, 4, 15, 6, 5, 10, 1, 2, 13, 8, 12, 5, 7, 14, 11, 12, 4, 11, 2, 15, 8, 1,
        13, 1, 6, 10, 4, 13, 9, 0, 8, 6, 15, 9, 3, 8, 0, 7, 11, 4, 1, 15, 2, 14, 12, 3, 5, 11, 10, 5, 14, 2, 7, 12,
        // S4
        7, 13, 13, 8, 14, 11, 3, 5, 0, 6, 6, 15, 9, 0, 10, 3, 1, 4, 2, 7, 8, 2, 5, 12, 11, 1, 12, 10, 4, 14, 15, 9,
        10, 3, 6, 15, 9, 0, 0, 6, 12, 10, 11, 10, 7, 13, 13, 8, 15, 9, 1, 4, 3, 5, 14, 11, 5, 12, 2, 7, 8, 2, 4, 14,
        // S5
        2, 14, 12, 11, 4, 2, 1, 12, 7, 4, 10, 7, 11, 13, 6, 1, 8, 5, 5, 0, 3, 15, 15, 10, 13, 3, 0, 9, 14, 8, 9, 6,
        4, 11, 2, 8, 1, 12, 11, 7, 10, 1, 13, 14, 7, 2, 8, 13, 15, 6, 9, 15, 12, 0, 5, 9, 6, 10, 3, 4, 0, 5, 14, 3,
        // S6
        12, 10, 1, 15, 10, 4, 15, 2, 9, 7, 2, 12, 6, 9, 8, 5, 0, 6, 13, 1, 3, 13, 4, 14, 14, 0, 7, 11, 5, 3, 11, 8,
        9, 4, 14, 3, 15, 2, 5, 12, 2, 9, 8, 5, 12, 15, 3, 10, 7, 11, 0, 14, 4, 1, 10, 7, 1, 6, 13, 0, 11, 8, 6, 13,
        // S7
        4, 13, 11, 0, 2, 11, 14, 7, 15, 4, 0, 9, 8, 1, 13, 10, 3, 14, 12, 3, 9, 5, 7, 12, 5, 2, 10, 15, 6, 8, 1, 6,
        1, 6, 4, 11, 11, 13, 13, 8, 12, 1, 3, 4, 7, 10, 14, 7, 10, 9, 15, 5, 6, 0, 8, 15, 0, 14, 5, 2, 9, 3, 2, 12,
        // S8
        13, 1, 2, 15, 8, 13, 4, 8, 6, 10, 15, 3, 11, 7, 1, 4, 10, 12, 9, 5, 3, 6, 14, 11, 5, 0, 0, 14, 12, 9, 7, 2,
        7, 2, 11, 1, 4, 14, 1, 7, 9, 4, 12, 10, 14, 8, 2, 13, 0, 15, 6, 12, 10, 9, 13, 0, 15, 3, 3, 5, 5, 6, 8, 11
    ];

    private static ReadOnlySpan<byte> PBox =>
    [
        0x0F, 0x06, 0x13, 0x14, 0x1C, 0x0B, 0x1B, 0x10, 0x00, 0x0E, 0x16, 0x19, 0x04, 0x11, 0x1E, 0x09,
        0x01, 0x07, 0x17, 0x0D, 0x1F, 0x1A, 0x02, 0x08, 0x12, 0x0C, 0x1D, 0x05, 0x15, 0x0A, 0x03, 0x18
    ];

    private static ReadOnlySpan<byte> Ip =>
    [
        0x39, 0x31, 0x29, 0x21, 0x19, 0x11, 0x09, 0x01, 0x3B, 0x33, 0x2B, 0x23, 0x1B, 0x13, 0x0B, 0x03,
        0x3D, 0x35, 0x2D, 0x25, 0x1D, 0x15, 0x0D, 0x05, 0x3F, 0x37, 0x2F, 0x27, 0x1F, 0x17, 0x0F, 0x07,
        0x38, 0x30, 0x28, 0x20, 0x18, 0x10, 0x08, 0x00, 0x3A, 0x32, 0x2A, 0x22, 0x1A, 0x12, 0x0A, 0x02,
        0x3C, 0x34, 0x2C, 0x24, 0x1C, 0x14, 0x0C, 0x04, 0x3E, 0x36, 0x2E, 0x26, 0x1E, 0x16, 0x0E, 0x06
    ];

    private static ReadOnlySpan<byte> IpInv =>
    [
        0x27, 0x07, 0x2F, 0x0F, 0x37, 0x17, 0x3F, 0x1F, 0x26, 0x06, 0x2E, 0x0E, 0x36, 0x16, 0x3E, 0x1E,
        0x25, 0x05, 0x2D, 0x0D, 0x35, 0x15, 0x3D, 0x1D, 0x24, 0x04, 0x2C, 0x0C, 0x34, 0x14, 0x3C, 0x1C,
        0x23, 0x03, 0x2B, 0x0B, 0x33, 0x13, 0x3B, 0x1B, 0x22, 0x02, 0x2A, 0x0A, 0x32, 0x12, 0x3A, 0x1A,
        0x21, 0x01, 0x29, 0x09, 0x31, 0x11, 0x39, 0x19, 0x20, 0x00, 0x28, 0x08, 0x30, 0x10, 0x38, 0x18
    ];

    private static ReadOnlySpan<byte> KeyPermutationTable =>
    [
        0x38, 0x30, 0x28, 0x20, 0x18, 0x10, 0x08, 0x00, 0x39, 0x31, 0x29, 0x21, 0x19, 0x11, 0x09, 0x01,
        0x3A, 0x32, 0x2A, 0x22, 0x1A, 0x12, 0x0A, 0x02, 0x3B, 0x33, 0x2B, 0x23, 0x3E, 0x36, 0x2E, 0x26,
        0x1E, 0x16, 0x0E, 0x06, 0x3D, 0x35, 0x2D, 0x25, 0x1D, 0x15, 0x0D, 0x05, 0x3C, 0x34, 0x2C, 0x24,
        0x1C, 0x14, 0x0C, 0x04, 0x1B, 0x13, 0x0B, 0x03
    ];

    private static ReadOnlySpan<byte> KeyCompression =>
    [
        0x0D, 0x10, 0x0A, 0x17, 0x00, 0x04, 0x02, 0x1B, 0x0E, 0x05, 0x14, 0x09, 0x16, 0x12, 0x0B, 0x03,
        0x19, 0x07, 0x0F, 0x06, 0x1A, 0x13, 0x0C, 0x01, 0x2D, 0x38, 0x23, 0x29, 0x33, 0x3B, 0x22, 0x2C,
        0x37, 0x31, 0x25, 0x34, 0x30, 0x35, 0x2B, 0x3C, 0x26, 0x39, 0x32, 0x2E, 0x36, 0x28, 0x21, 0x24
    ];

    private static ReadOnlySpan<byte> KeyExpansion =>
    [
        0x1F, 0x00, 0x01, 0x02, 0x03, 0x04, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x07, 0x08, 0x09, 0x0A,
        0x0B, 0x0C, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x13, 0x14,
        0x15, 0x16, 0x17, 0x18, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x00
    ];

    private static ulong GetDesShiftMask(byte idx)
    {
        int i = idx & 0x3F;
        return i < 32
            ? 1UL << (31 - i)
            : 1UL << (63 - (i - 32));
    }

    private static ulong MapDesU64(ulong src, ReadOnlySpan<byte> table)
    {
        int mid = table.Length >> 1;
        ulong lo32 = 0;
        ulong hi32 = 0;
        for (int i = 0; i < mid; i++)
        {
            if ((GetDesShiftMask(table[i]) & src) != 0)
            {
                lo32 |= GetDesShiftMask((byte)i);
            }
        }

        for (int i = 0; i < mid; i++)
        {
            if ((GetDesShiftMask(table[mid + i]) & src) != 0)
            {
                hi32 |= GetDesShiftMask((byte)i);
            }
        }

        return (hi32 << 32) | lo32;
    }

    private static uint MapDesU32(uint src, ReadOnlySpan<byte> table)
    {
        ulong result = 0;
        for (int i = 0; i < table.Length; i++)
        {
            if ((GetDesShiftMask(table[i]) & src) != 0)
            {
                result |= GetDesShiftMask((byte)i);
            }
        }

        return (uint)result;
    }

    internal static ulong[] BuildQqDesSubkeys(ReadOnlySpan<byte> keyBytes, bool encrypt)
    {
        ulong key = BinaryPrimitives.ReadUInt64LittleEndian(keyBytes);
        ulong param = MapDesU64(key, KeyPermutationTable);
        uint c = (uint)param;
        uint d = (uint)(param >> 32);
        ReadOnlySpan<byte> shifts = KeyRndShifts;
        ulong[] subkeys = new ulong[16];

        for (int i = 0; i < 16; i++)
        {
            int sh = shifts[i];
            c = (c << sh) | ((c >> (28 - sh)) & 0xFFFFFFF0u);
            d = (d << sh) | ((d >> (28 - sh)) & 0xFFFFFFF0u);
            int targetIdx = encrypt ? i : 15 - i;
            subkeys[targetIdx] = MapDesU64(((ulong)d << 32) | c, KeyCompression);
        }

        return subkeys;
    }

    internal static ulong QqDesTransformBlock(ulong data, ReadOnlySpan<ulong> subkeys)
    {
        ulong state = MapDesU64(data, Ip);
        ReadOnlySpan<byte> shifts = LargeStateShifts;
        ReadOnlySpan<byte> sboxes = SBoxesFlat;
        ReadOnlySpan<byte> expTable = KeyExpansion;
        ReadOnlySpan<byte> pBox = PBox;

        for (int round = 0; round < 16; round++)
        {
            uint hi32 = (uint)(state >> 32);
            uint lo32 = (uint)state;
            ulong expanded = MapDesU64(((ulong)hi32 << 32) | hi32, expTable) ^ subkeys[round];

            uint sboxResult = 0;
            for (int s = 0; s < 8; s++)
            {
                int sboxIdx = (int)((expanded >> shifts[s]) & 0x3FUL);
                sboxResult = (sboxResult << 4) | sboxes[(s << 6) + sboxIdx];
            }

            uint nextLo32 = MapDesU32(sboxResult, pBox) ^ lo32;
            state = ((ulong)nextLo32 << 32) | hi32;
        }

        state = (state >> 32) | (state << 32);
        return MapDesU64(state, IpInv);
    }
}
