using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace DeskBox.Services;

internal readonly record struct SearchDetailsLoadedPreview(
    BitmapImage? Image,
    bool IsVideoPoster,
    string? TextSnippet);

/// <summary>
/// Lightweight, NativeAOT-safe static preview loader for the search Details sidebar.
/// Never loads video codecs or WebView2 into DeskBox.exe and always opens local files
/// with <c>FileShare.ReadWrite | FileShare.Delete</c> so user files are never locked.
/// </summary>
internal static class SearchDetailsPreviewLoader
{
    private const int MaxRasterImageDirectBytes = 32 * 1024 * 1024;
    private const int MaxPdfDirectBytes = 64 * 1024 * 1024;
    private const int MaxDocxArchiveBytes = 48 * 1024 * 1024;
    private const int MaxTextReadBytes = 4096;
    private const int MaxSnippetLines = 10;
    private const int MaxSnippetChars = 420;
    private const int PreviewDecodeWidth = 360;

    private static readonly HashSet<string> s_rasterImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".tif", ".tiff"
    };

    private static readonly HashSet<string> s_shellThumbnailDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".ppt", ".xlsx", ".xls", ".doc", ".svg", ".heic", ".heif", ".psd"
    };

    private static readonly HashSet<string> s_textAndCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".xml", ".csv", ".tsv", ".sql", ".env", ".gitignore",
        ".gitattributes", ".editorconfig", ".bat", ".cmd", ".ps1", ".psm1", ".sh",
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".xaml",
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".rs", ".go", ".py", ".pyw",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte",
        ".html", ".htm", ".css", ".scss", ".less", ".java", ".kt", ".kts",
        ".swift", ".rb", ".php", ".lua", ".r", ".dart", ".gradle", ".dockerfile"
    };

    public static bool CanAttemptAsyncFilePreview(SearchResultItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.PreviewImagePath))
        {
            return true;
        }

        if (item.Kind != SearchResultKind.File || string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return false;
        }

        return CanAttemptPathPreview(item.DetailPath);
    }

    public static bool CanAttemptPathPreview(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            string fileName = Path.GetFileName(filePath);
            return fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("README", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
        }

        return s_rasterImageExtensions.Contains(ext) ||
               IconHelper.IsVideoFile(filePath) ||
               IsPdfExtension(ext) ||
               IsDocxExtension(ext) ||
               s_textAndCodeExtensions.Contains(ext) ||
               s_shellThumbnailDocumentExtensions.Contains(ext);
    }

    public static bool IsTextOrDocumentSnippetCandidate(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(ext))
        {
            string fileName = Path.GetFileName(filePath);
            return fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("README", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
        }

        return IsDocxExtension(ext) || s_textAndCodeExtensions.Contains(ext);
    }

    public static async Task<SearchDetailsLoadedPreview> LoadAsync(
        SearchResultItem item,
        CancellationToken cancellationToken = default)
    {
        // 1. Explicit clipboard/note preview image path takes priority.
        if (!string.IsNullOrWhiteSpace(item.PreviewImagePath))
        {
            BitmapImage? clipImage = await TryLoadRasterBitmapAsync(item.PreviewImagePath, cancellationToken);
            return new SearchDetailsLoadedPreview(clipImage, IsVideoPoster: false, TextSnippet: null);
        }

        if (item.Kind != SearchResultKind.File || string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return default;
        }

        string filePath = item.DetailPath;
        string ext = Path.GetExtension(filePath);

        // 2. Raster image preview.
        if (s_rasterImageExtensions.Contains(ext))
        {
            BitmapImage? raster = await TryLoadRasterBitmapAsync(filePath, cancellationToken);
            return new SearchDetailsLoadedPreview(raster, IsVideoPoster: false, TextSnippet: null);
        }

        // 3. Video poster frame via isolated out-of-process ShellThumbnailProxy.
        if (IconHelper.IsVideoFile(filePath))
        {
            BitmapImage? poster = await TryLoadIsolatedShellThumbnailBitmapAsync(filePath, cancellationToken);
            return new SearchDetailsLoadedPreview(poster, IsVideoPoster: poster is not null, TextSnippet: null);
        }

        // 4. PDF first-page cover preview (WinRT PdfDocument with isolated ShellThumbnailProxy fallback).
        if (IsPdfExtension(ext))
        {
            BitmapImage? pdfCover = await TryLoadPdfFirstPageBitmapAsync(filePath, cancellationToken)
                                    ?? await TryLoadIsolatedShellThumbnailBitmapAsync(filePath, cancellationToken);
            return new SearchDetailsLoadedPreview(pdfCover, IsVideoPoster: false, TextSnippet: null);
        }

        // 5. Word (.docx) lightweight streaming XML paragraph excerpt (+ optional Shell thumbnail if embedded).
        if (IsDocxExtension(ext))
        {
            string? docxSnippet = await Task.Run(
                () => TryExtractDocxSnippet(filePath),
                cancellationToken);
            return new SearchDetailsLoadedPreview(Image: null, IsVideoPoster: false, TextSnippet: docxSnippet);
        }

        // 6. Plain text / Markdown / JSON / CSV / Source code 4 KB head snippet.
        if (IsTextOrDocumentSnippetCandidate(filePath))
        {
            string? textSnippet = await Task.Run(
                () => TryReadPlainTextSnippet(filePath),
                cancellationToken);
            return new SearchDetailsLoadedPreview(Image: null, IsVideoPoster: false, TextSnippet: textSnippet);
        }

        // 7. Other documents/media with registered Shell thumbnail handlers (.pptx, .xlsx, .svg, .heic, .psd).
        if (s_shellThumbnailDocumentExtensions.Contains(ext) &&
            await ShellThumbnailProxy.HasRegisteredThumbnailProviderAsync(filePath))
        {
            BitmapImage? shellThumb = await TryLoadIsolatedShellThumbnailBitmapAsync(filePath, cancellationToken);
            return new SearchDetailsLoadedPreview(shellThumb, IsVideoPoster: false, TextSnippet: null);
        }

        return default;
    }

    internal static string? TryReadPlainTextSnippet(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: MaxTextReadBytes,
                FileOptions.SequentialScan);

            if (fs.Length == 0)
            {
                return null;
            }

            int toRead = (int)Math.Min(MaxTextReadBytes, fs.Length);
            byte[] buffer = new byte[toRead];
            int totalRead = 0;
            while (totalRead < toRead)
            {
                int n = fs.Read(buffer, totalRead, toRead - totalRead);
                if (n <= 0)
                {
                    break;
                }
                totalRead += n;
            }

            if (totalRead == 0)
            {
                return null;
            }

            return DecodeAndFormatTextSnippet(buffer.AsSpan(0, totalRead), truncatedByByteCap: fs.Length > totalRead);
        }
        catch
        {
            return null;
        }
    }

    internal static string? DecodeAndFormatTextSnippet(ReadOnlySpan<byte> bytes, bool truncatedByByteCap)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        string decoded;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            int evenLen = (bytes.Length - 2) & ~1;
            decoded = Encoding.Unicode.GetString(bytes.Slice(2, evenLen));
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            int evenLen = (bytes.Length - 2) & ~1;
            decoded = Encoding.BigEndianUnicode.GetString(bytes.Slice(2, evenLen));
        }
        else
        {
            ReadOnlySpan<byte> body = bytes.Length >= 3 &&
                                      bytes[0] == 0xEF &&
                                      bytes[1] == 0xBB &&
                                      bytes[2] == 0xBF
                ? bytes[3..]
                : bytes;

            // Reject binary payloads masquerading under text extensions.
            if (body.IndexOf((byte)0) >= 0)
            {
                return null;
            }

            decoded = Encoding.UTF8.GetString(body);
            // If truncated mid-UTF8 multi-byte character at the 4 KB boundary, strip trailing replacement char.
            if (truncatedByByteCap && decoded.EndsWith('\uFFFD'))
            {
                decoded = decoded[..^1];
            }
        }

        return NormalizePreviewLines(decoded, truncatedByByteCap);
    }

    internal static string? TryExtractDocxSnippet(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxDocxArchiveBytes)
            {
                return null;
            }

            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? docEntry = archive.GetEntry("word/document.xml");
            if (docEntry is null)
            {
                return null;
            }

            using Stream entryStream = docEntry.Open();
            var xmlSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersFromEntities = 1024
            };

            using var reader = XmlReader.Create(entryStream, xmlSettings);
            var paragraphs = new List<string>(MaxSnippetLines);
            var currentParagraph = new StringBuilder();
            int totalChars = 0;
            bool truncated = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.LocalName)
                    {
                        case "t":
                            string text = reader.ReadElementContentAsString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                currentParagraph.Append(text);
                            }
                            break;

                        case "tab":
                            currentParagraph.Append(' ');
                            break;

                        case "br":
                        case "cr":
                            FlushParagraph(currentParagraph, paragraphs, ref totalChars, ref truncated);
                            break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement &&
                         string.Equals(reader.LocalName, "p", StringComparison.Ordinal))
                {
                    FlushParagraph(currentParagraph, paragraphs, ref totalChars, ref truncated);
                }

                if (paragraphs.Count >= 8 || totalChars >= MaxSnippetChars)
                {
                    truncated = true;
                    break;
                }
            }

            if (!truncated)
            {
                FlushParagraph(currentParagraph, paragraphs, ref totalChars, ref truncated);
            }

            if (paragraphs.Count == 0)
            {
                return null;
            }

            string joined = string.Join('\n', paragraphs);
            if (joined.Length > MaxSnippetChars)
            {
                joined = string.Concat(joined.AsSpan(0, MaxSnippetChars).TrimEnd(), "…");
            }
            else if (truncated)
            {
                joined = string.Concat(joined.TrimEnd(), "…");
            }

            return joined;
        }
        catch
        {
            return null;
        }
    }

    private static void FlushParagraph(
        StringBuilder current,
        List<string> paragraphs,
        ref int totalChars,
        ref bool truncated)
    {
        if (current.Length == 0)
        {
            return;
        }

        string cleaned = CollapseInlineHorizontalWhitespace(current.ToString()).Trim();
        current.Clear();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        paragraphs.Add(cleaned);
        totalChars += cleaned.Length + 1;
        if (totalChars > MaxSnippetChars)
        {
            truncated = true;
        }
    }

    private static string? NormalizePreviewLines(string raw, bool truncatedBySource)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = new List<string>(MaxSnippetLines);
        int totalChars = 0;
        bool hasMoreLines = false;

        using var sr = new StringReader(raw);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            string expanded = line.Replace("\t", "  ", StringComparison.Ordinal).TrimEnd();
            if (lines.Count == 0 && string.IsNullOrWhiteSpace(expanded))
            {
                continue;
            }

            // Collapse consecutive blank lines so dense information fits cleanly in the card.
            if (string.IsNullOrWhiteSpace(expanded) &&
                lines.Count > 0 &&
                string.IsNullOrWhiteSpace(lines[^1]))
            {
                continue;
            }

            if (lines.Count >= MaxSnippetLines || totalChars >= MaxSnippetChars)
            {
                hasMoreLines = true;
                break;
            }

            lines.Add(expanded);
            totalChars += expanded.Length + 1;
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        string joined = string.Join('\n', lines);
        if (joined.Length > MaxSnippetChars)
        {
            return string.Concat(joined.AsSpan(0, MaxSnippetChars).TrimEnd(), "…");
        }

        if (hasMoreLines || truncatedBySource)
        {
            return string.Concat(joined.TrimEnd(), "…");
        }

        return joined;
    }

    private static string CollapseInlineHorizontalWhitespace(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool prevSpace = false;
        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }

        return sb.ToString();
    }

    private static async Task<BitmapImage?> TryLoadRasterBitmapAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[]? bytes = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxRasterImageDirectBytes)
                {
                    return null;
                }

                using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 16384,
                    FileOptions.SequentialScan);
                byte[] data = new byte[fs.Length];
                int read = 0;
                while (read < data.Length)
                {
                    int n = fs.Read(data, read, data.Length - read);
                    if (n <= 0)
                    {
                        break;
                    }
                    read += n;
                }

                return read == data.Length ? data : data[..read];
            }, cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (bytes is { Length: > 0 })
            {
                return await DecodeBytesToBitmapImageAsync(bytes);
            }

            return await TryLoadIsolatedShellThumbnailBitmapAsync(filePath, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> TryLoadIsolatedShellThumbnailBitmapAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(filePath) || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            byte[]? bmpBytes = await ShellThumbnailProxy
                .TryLoadAsync(filePath, PreviewDecodeWidth)
                .ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || bmpBytes is not { Length: > 0 })
            {
                return null;
            }

            return await DecodeBytesToBitmapImageAsync(bmpBytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> TryLoadPdfFirstPageBitmapAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxPdfDirectBytes || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            using var renderStream = new InMemoryRandomAccessStream();
            bool rendered = await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream winRtStream = fs.AsRandomAccessStream();
                PdfDocument doc = await PdfDocument.LoadFromStreamAsync(winRtStream);
                if (doc.PageCount == 0 || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                using PdfPage page = doc.GetPage(0);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = PreviewDecodeWidth
                };
                await page.RenderToStreamAsync(renderStream, options);
                return renderStream.Size > 0;
            }, cancellationToken).ConfigureAwait(true);

            if (!rendered || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            renderStream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = PreviewDecodeWidth
            };
            await bitmap.SetSourceAsync(renderStream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage?> DecodeBytesToBitmapImageAsync(byte[] bytes)
    {
        using var memStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        memStream.Seek(0);
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = PreviewDecodeWidth
        };
        await bitmap.SetSourceAsync(memStream);
        return bitmap;
    }

    private static bool IsPdfExtension(string ext) =>
        ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocxExtension(string ext) =>
        ext.Equals(".docx", StringComparison.OrdinalIgnoreCase);
}
