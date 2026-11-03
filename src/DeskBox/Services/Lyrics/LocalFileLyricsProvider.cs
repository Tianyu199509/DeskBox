using System.Text;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Resolves user-supplied local <c>.lrc</c> files from an optional folder, supporting UTF-8 and GB18030 encodings.
/// </summary>
public static class LocalFileLyricsProvider
{
    private const long MaxLrcFileBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static int s_codePagesRegistered;

    public static async Task<string?> TryReadLocalLrcAsync(
        LyricsQuery query,
        string? localFolder,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid || string.IsNullOrWhiteSpace(localFolder))
        {
            return null;
        }

        string folder;
        try
        {
            folder = Path.GetFullPath(localFolder.Trim());
            if (!Directory.Exists(folder))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        string rawTitle = SanitizeFileNameSegment(query.Title);
        string cleanTitle = SanitizeFileNameSegment(LyricTrackMatcher.CleanTitle(query.Title));
        string rawArtist = SanitizeFileNameSegment(query.Artist);
        string primaryArtist = SanitizeFileNameSegment(LyricTrackMatcher.ExtractPrimaryArtist(query.Artist));

        var candidates = new List<string>(8);
        AddCandidateNames(candidates, rawArtist, rawTitle);
        AddCandidateNames(candidates, primaryArtist, cleanTitle);
        AddSingleName(candidates, rawTitle);
        AddSingleName(candidates, cleanTitle);

        foreach (string fileName in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(folder, fileName + ".lrc");
            if (!File.Exists(fullPath))
            {
                continue;
            }

            string? text = await ReadTextWithEncodingFallbackAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    internal static string DecodeLrcBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            EnsureCodePagesRegistered();
            try
            {
                return Encoding.GetEncoding(54936).GetString(bytes); // GB18030 (superset of GBK/GB2312)
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    private static async Task<string?> ReadTextWithEncodingFallbackAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxLrcFileBytes)
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            return DecodeLrcBytes(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureCodePagesRegistered()
    {
        if (Interlocked.Exchange(ref s_codePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    private static void AddCandidateNames(List<string> list, string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        AddUnique(list, $"{artist} - {title}");
        AddUnique(list, $"{title} - {artist}");
    }

    private static void AddSingleName(List<string> list, string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            AddUnique(list, title);
        }
    }

    private static void AddUnique(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(value);
    }

    private static string SanitizeFileNameSegment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            if (Array.IndexOf(invalid, c) < 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }
}
