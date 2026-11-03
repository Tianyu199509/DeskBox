using System.Globalization;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public sealed record CustomWidgetTransformResult(
    bool Succeeded,
    string Operation,
    string SourcePath,
    string OutputPath,
    string OutputName,
    string SizeText,
    string ResultText,
    string? ErrorMessage,
    string JwtState = "")
{
    public string ResolveToken(string field) => field switch
    {
        "status" => Succeeded ? "ok" : "error",
        "op" or "operation" or "format" => Operation,
        "sourcePath" or "inputPath" => SourcePath,
        "outputPath" or "path" => OutputPath,
        "outputName" or "name" or "fileName" => OutputName,
        "sizeText" or "size" => SizeText,
        "result" or "resultText" or "text" or "hash" => ResultText,
        "error" => ErrorMessage ?? string.Empty,
        "jwtState" or "jwtStatus" => JwtState,
        _ => string.Empty
    };
}

/// <summary>
/// Sandboxed, NativeAOT-clean local file converter &amp; utility processor for Custom Widgets
/// (<c>file.transform</c> action). Uses built-in WinRT <see cref="BitmapDecoder"/>/<see cref="BitmapEncoder"/>
/// and zero-reflection <see cref="JsonDocument"/>/<see cref="Utf8JsonWriter"/> — zero external binaries.
/// </summary>
public static class CustomWidgetFileTransformService
{
    public const long MaxTextTransformBytes = 10 * 1024 * 1024; // 10 MB safety cap
    public const long MaxHashTransformBytes = 256L * 1024 * 1024; // 256 MB safety cap

    private static readonly HashSet<string> SupportedImageTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "tiff", "ico"
    };

    public static bool IsSupportedOperation(string? opOrFormat)
    {
        if (string.IsNullOrWhiteSpace(opOrFormat))
        {
            return false;
        }

        string norm = NormalizeOperation(opOrFormat);
        return SupportedImageTargets.Contains(norm) ||
               norm is "csvtojson" or "jsontocsv" or "jsonpretty" or "jsonminify"
                    or "mdtohtml" or "mdtotxt" or "base64encode" or "base64decode"
                    or "sha256" or "md5";
    }

    public static string NormalizeOperation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string clean = raw.Trim().TrimStart('.').ToLowerInvariant()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("->", "to", StringComparison.Ordinal);

        return clean switch
        {
            "imagepng" => "png",
            "imagejpg" or "imagejpeg" => "jpg",
            "imagewebp" => "webp",
            "imageico" or "icon" => "ico",
            "imagebmp" => "bmp",
            "jsonformat" or "formatjson" => "jsonpretty",
            "minifyjson" => "jsonminify",
            "markdowntohtml" => "mdtohtml",
            "markdowntotxt" or "markdowntotext" => "mdtotxt",
            "b64encode" or "base64" => "base64encode",
            "b64decode" => "base64decode",
            "hash" or "sha256sum" => "sha256",
            "md5sum" => "md5",
            _ => clean
        };
    }

    public static async Task<CustomWidgetTransformResult> TransformFileAsync(
        string? sourcePath,
        string? operationOrTargetFormat,
        int maxWidth = 0,
        double quality = 0.88,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string op = NormalizeOperation(operationOrTargetFormat);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Fail(op, string.Empty, "Missing source file path.");
        }

        string fullSourcePath;
        try
        {
            fullSourcePath = Path.GetFullPath(sourcePath.Trim());
        }
        catch (Exception ex)
        {
            return Fail(op, sourcePath, $"Invalid file path: {ex.Message}");
        }

        if (!File.Exists(fullSourcePath))
        {
            return Fail(op, fullSourcePath, $"File not found: {Path.GetFileName(fullSourcePath)}");
        }

        if (string.IsNullOrEmpty(op))
        {
            return Fail(op, fullSourcePath, "Missing transform format or operation.");
        }

        try
        {
            if (SupportedImageTargets.Contains(op))
            {
                return await ConvertImageAsync(fullSourcePath, op, maxWidth, quality, outputDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (op is "sha256" or "md5")
            {
                return await ComputeFileHashAsync(fullSourcePath, op, cancellationToken).ConfigureAwait(false);
            }

            return await TransformStructuredTextFileAsync(fullSourcePath, op, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(op, fullSourcePath, ex.Message);
        }
    }

    private static async Task<CustomWidgetTransformResult> ConvertImageAsync(
        string sourcePath,
        string targetExt,
        int maxWidth,
        double quality,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        string normalizedExt = targetExt == "jpeg" ? "jpg" : targetExt;
        string destPath = BuildUniqueOutputPath(sourcePath, normalizedExt, outputDirectory);

        await using var inputFs = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            useAsync: true);
        using IRandomAccessStream winRtIn = inputFs.AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(winRtIn).AsTask(cancellationToken).ConfigureAwait(false);

        uint srcW = decoder.PixelWidth;
        uint srcH = decoder.PixelHeight;
        if (srcW == 0 || srcH == 0)
        {
            return Fail(targetExt, sourcePath, "Source image has invalid dimensions.");
        }

        if (normalizedExt == "ico")
        {
            uint icoSize = maxWidth is > 0 and <= 256 ? (uint)maxWidth : Math.Min(256u, Math.Max(srcW, srcH));
            uint scaledW = srcW >= srcH ? icoSize : Math.Max(1u, (uint)Math.Round((double)srcW * icoSize / srcH));
            uint scaledH = srcH > srcW ? icoSize : Math.Max(1u, (uint)Math.Round((double)srcH * icoSize / srcW));

            using var pngStream = new InMemoryRandomAccessStream();
            BitmapEncoder pngEncoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream)
                .AsTask(cancellationToken).ConfigureAwait(false);
            using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask(cancellationToken).ConfigureAwait(false);
            pngEncoder.SetSoftwareBitmap(softwareBitmap);
            pngEncoder.BitmapTransform.ScaledWidth = scaledW;
            pngEncoder.BitmapTransform.ScaledHeight = scaledH;
            pngEncoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await pngEncoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

            byte[] pngBytes = new byte[pngStream.Size];
            pngStream.Seek(0);
            await pngStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None)
                .AsTask(cancellationToken).ConfigureAwait(false);

            byte[] icoBytes = BuildPngBackedIcoBytes(pngBytes, scaledW, scaledH);
            await File.WriteAllBytesAsync(destPath, icoBytes, cancellationToken).ConfigureAwait(false);
            return BuildSuccessResult("ico", sourcePath, destPath, $"{scaledW}x{scaledH} ICO");
        }

        Guid encoderId = ResolveBitmapEncoderId(normalizedExt);
        using var memOut = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        try
        {
            if (normalizedExt == "jpg")
            {
                var props = new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 0.1, 1.0), Windows.Foundation.PropertyType.Single)
                };
                encoder = await BitmapEncoder.CreateAsync(encoderId, memOut, props)
                    .AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(encoderId, memOut)
                    .AsTask(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (normalizedExt == "webp")
            {
                return Fail("webp", sourcePath, "Windows WebP encoder codec is not installed on this system; try PNG or JPG.");
            }
            throw;
        }

        using SoftwareBitmap bmp = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            normalizedExt is "jpg" or "bmp" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bmp);

        uint outW = srcW;
        uint outH = srcH;
        if (maxWidth > 0 && srcW > (uint)maxWidth)
        {
            outW = (uint)maxWidth;
            outH = Math.Max(1u, (uint)Math.Round((double)srcH * outW / srcW));
            encoder.BitmapTransform.ScaledWidth = outW;
            encoder.BitmapTransform.ScaledHeight = outH;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        await using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
        {
            memOut.Seek(0);
            await memOut.AsStreamForRead().CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);
        }

        return BuildSuccessResult(normalizedExt, sourcePath, destPath, $"{outW}x{outH} {normalizedExt.ToUpperInvariant()}");
    }

    internal static byte[] BuildPngBackedIcoBytes(byte[] pngPayload, uint width, uint height)
    {
        // Standard Windows Vista+ PNG-compressed ICO container (6-byte ICONDIR + 16-byte ICONDIRENTRY + PNG payload)
        byte[] ico = new byte[22 + pngPayload.Length];
        ico[0] = 0;
        ico[1] = 0;
        ico[2] = 1; // Type = 1 (Icon)
        ico[3] = 0;
        ico[4] = 1; // Count = 1 image
        ico[5] = 0;

        ico[6] = width >= 256 ? (byte)0 : (byte)width;
        ico[7] = height >= 256 ? (byte)0 : (byte)height;
        ico[8] = 0; // Palette
        ico[9] = 0; // Reserved
        ico[10] = 1; // Color planes
        ico[11] = 0;
        ico[12] = 32; // 32 bpp
        ico[13] = 0;

        BitConverter.TryWriteBytes(ico.AsSpan(14, 4), pngPayload.Length);
        BitConverter.TryWriteBytes(ico.AsSpan(18, 4), 22);
        System.Buffer.BlockCopy(pngPayload, 0, ico, 22, pngPayload.Length);
        return ico;
    }

    private static Guid ResolveBitmapEncoderId(string ext) => ext switch
    {
        "jpg" or "jpeg" => BitmapEncoder.JpegEncoderId,
        "bmp" => BitmapEncoder.BmpEncoderId,
        "tiff" => BitmapEncoder.TiffEncoderId,
        // Windows 10 1809+ WebP WIC encoder CLSID (gracefully caught if absent)
        "webp" => new Guid("0131be10-2001-4c5f-a9b0-cc88fab64ce8"),
        _ => BitmapEncoder.PngEncoderId
    };

    private static async Task<CustomWidgetTransformResult> ComputeFileHashAsync(
        string sourcePath,
        string algorithm,
        CancellationToken cancellationToken)
    {
        var fi = new FileInfo(sourcePath);
        if (fi.Length > MaxHashTransformBytes)
        {
            return Fail(algorithm, sourcePath, "File exceeds 256 MB hash safety limit.");
        }

        await using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] digest = algorithm == "md5"
            ? await MD5.HashDataAsync(fs, cancellationToken).ConfigureAwait(false)
            : await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        string hex = Convert.ToHexString(digest).ToLowerInvariant();
        return new CustomWidgetTransformResult(
            Succeeded: true,
            Operation: algorithm,
            SourcePath: sourcePath,
            OutputPath: sourcePath,
            OutputName: fi.Name,
            SizeText: FormatByteSize(fi.Length),
            ResultText: hex,
            ErrorMessage: null);
    }

    private static async Task<CustomWidgetTransformResult> TransformStructuredTextFileAsync(
        string sourcePath,
        string op,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        var fi = new FileInfo(sourcePath);
        if (fi.Length > MaxTextTransformBytes)
        {
            return Fail(op, sourcePath, "File exceeds 10 MB text transform limit.");
        }

        if (op == "base64encode")
        {
            byte[] rawBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            string b64 = Convert.ToBase64String(rawBytes);
            string dest = BuildUniqueOutputPath(sourcePath, "b64.txt", outputDirectory);
            await File.WriteAllTextAsync(dest, b64, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return BuildSuccessResult(op, sourcePath, dest, b64.Length <= 256 ? b64 : b64[..256] + "…");
        }

        string inputText = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        switch (op)
        {
            case "base64decode":
            {
                byte[] decoded = Convert.FromBase64String(inputText.Trim());
                string dest = BuildUniqueOutputPath(sourcePath, "decoded.bin", outputDirectory);
                await File.WriteAllBytesAsync(dest, decoded, cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, $"Decoded {FormatByteSize(decoded.Length)}");
            }

            case "jsonpretty":
            case "jsonminify":
            {
                using JsonDocument doc = JsonDocument.Parse(inputText, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
                {
                    Indented = op == "jsonpretty",
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }))
                {
                    doc.RootElement.WriteTo(writer);
                }

                string suffix = op == "jsonpretty" ? "pretty.json" : "min.json";
                string dest = BuildUniqueOutputPath(sourcePath, suffix, outputDirectory);
                await File.WriteAllBytesAsync(dest, ms.ToArray(), cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, op == "jsonpretty" ? "Formatted JSON" : "Minified JSON");
            }

            case "csvtojson":
            {
                byte[] jsonBytes = ConvertCsvToJsonUtf8(inputText, out int rowCount);
                string dest = BuildUniqueOutputPath(sourcePath, "json", outputDirectory);
                await File.WriteAllBytesAsync(dest, jsonBytes, cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, $"{rowCount} rows -> JSON");
            }

            case "jsontocsv":
            {
                string csv = ConvertJsonToCsv(inputText, out int rowCount);
                string dest = BuildUniqueOutputPath(sourcePath, "csv", outputDirectory);
                await File.WriteAllTextAsync(dest, csv, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, $"{rowCount} rows -> CSV");
            }

            case "mdtohtml":
            {
                string html = ConvertMarkdownToStandaloneHtml(inputText, Path.GetFileNameWithoutExtension(sourcePath));
                string dest = BuildUniqueOutputPath(sourcePath, "html", outputDirectory);
                await File.WriteAllTextAsync(dest, html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, "Markdown -> HTML");
            }

            case "mdtotxt":
            {
                string plain = StripSimpleMarkdown(inputText);
                string dest = BuildUniqueOutputPath(sourcePath, "txt", outputDirectory);
                await File.WriteAllTextAsync(dest, plain, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                return BuildSuccessResult(op, sourcePath, dest, "Markdown -> Plain Text");
            }

            default:
                return Fail(op, sourcePath, $"Unsupported transform operation '{op}'.");
        }
    }

    internal static byte[] ConvertCsvToJsonUtf8(string csvText, out int dataRowCount)
    {
        List<List<string>> rows = ParseDelimitedRows(csvText);
        dataRowCount = 0;
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        writer.WriteStartArray();
        if (rows.Count >= 2)
        {
            List<string> headers = rows[0];
            for (int r = 1; r < rows.Count; r++)
            {
                List<string> row = rows[r];
                if (row.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                writer.WriteStartObject();
                for (int c = 0; c < headers.Count; c++)
                {
                    string key = string.IsNullOrWhiteSpace(headers[c]) ? $"col{c + 1}" : headers[c].Trim();
                    string val = c < row.Count ? row[c] : string.Empty;
                    writer.WriteString(key, val);
                }

                writer.WriteEndObject();
                dataRowCount++;
            }
        }

        writer.WriteEndArray();
        writer.Flush();
        return ms.ToArray();
    }

    internal static string ConvertJsonToCsv(string jsonText, out int rowCount)
    {
        rowCount = 0;
        using JsonDocument doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        JsonElement arrayEl = doc.RootElement;
        if (arrayEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in arrayEl.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                {
                    arrayEl = p.Value;
                    break;
                }
            }
        }

        if (arrayEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("JSON root does not contain an array of objects to convert to CSV.");
        }

        var columns = new List<string>();
        var colSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty prop in item.EnumerateObject())
            {
                if (colSet.Add(prop.Name))
                {
                    columns.Add(prop.Name);
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(EscapeCsvCell)));

        foreach (JsonElement item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var cells = new string[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                cells[i] = item.TryGetProperty(columns[i], out JsonElement v)
                    ? EscapeCsvCell(JsonScalarToString(v))
                    : string.Empty;
            }

            sb.AppendLine(string.Join(",", cells));
            rowCount++;
        }

        return sb.ToString();
    }

    private static List<List<string>> ParseDelimitedRows(string text)
    {
        char delimiter = text.IndexOf('\t') >= 0 && text.IndexOf(',') < 0 ? '\t' : ',';
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == delimiter)
                {
                    currentRow.Add(cell.ToString());
                    cell.Clear();
                }
                else if (ch is '\r' or '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    currentRow.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(currentRow);
                    currentRow = [];
                }
                else
                {
                    cell.Append(ch);
                }
            }
        }

        if (cell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(cell.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }

    private static string EscapeCsvCell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }

    private static string JsonScalarToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => v.GetRawText()
    };

    private static string ConvertMarkdownToStandaloneHtml(string md, string title)
    {
        var body = new StringBuilder();
        string[] lines = md.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool inList = false;
        bool inCode = false;

        foreach (string rawLine in lines)
        {
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                body.AppendLine(inCode ? "</code></pre>" : "<pre><code>");
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                body.AppendLine(WebUtility.HtmlEncode(rawLine));
                continue;
            }

            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                body.Append("<h3>").Append(FormatInlineMarkdownHtml(trimmed[4..])).AppendLine("</h3>");
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                body.Append("<h2>").Append(FormatInlineMarkdownHtml(trimmed[3..])).AppendLine("</h2>");
            }
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                body.Append("<h1>").Append(FormatInlineMarkdownHtml(trimmed[2..])).AppendLine("</h1>");
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!inList) { body.AppendLine("<ul>"); inList = true; }
                body.Append("<li>").Append(FormatInlineMarkdownHtml(trimmed[2..])).AppendLine("</li>");
            }
            else
            {
                if (inList) { body.AppendLine("</ul>"); inList = false; }
                body.Append("<p>").Append(FormatInlineMarkdownHtml(trimmed)).AppendLine("</p>");
            }
        }

        if (inList) body.AppendLine("</ul>");
        if (inCode) body.AppendLine("</code></pre>");

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>{{WebUtility.HtmlEncode(title)}}</title>
              <style>
                body { font-family: "Segoe UI", system-ui, sans-serif; max-width: 780px; margin: 36px auto; padding: 0 20px; line-height: 1.65; color: #1f2328; }
                pre { background: #f6f8fa; padding: 14px; border-radius: 8px; overflow-x: auto; }
              </style>
            </head>
            <body>
            {{body.ToString().TrimEnd()}}
            </body>
            </html>
            """;
    }

    private static readonly System.Text.RegularExpressions.Regex InlineCodeRegex = new(
        @"`([^`]+)`",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex InlineBoldRegex = new(
        @"\*\*([^*]+)\*\*",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex InlineItalicRegex = new(
        @"(?<!\*)\*([^*]+)\*(?!\*)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string FormatInlineMarkdownHtml(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string encoded = WebUtility.HtmlEncode(raw);
        encoded = InlineCodeRegex.Replace(encoded, "<code>$1</code>");
        encoded = InlineBoldRegex.Replace(encoded, "<strong>$1</strong>");
        encoded = InlineItalicRegex.Replace(encoded, "<em>$1</em>");
        return encoded;
    }

    private static string StripSimpleMarkdown(string md)
    {
        var sb = new StringBuilder();
        foreach (string line in md.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string t = line.TrimStart('#', ' ');
            t = t.Replace("**", string.Empty, StringComparison.Ordinal)
                 .Replace("__", string.Empty, StringComparison.Ordinal)
                 .Replace("`", string.Empty, StringComparison.Ordinal);
            sb.AppendLine(t);
        }

        return sb.ToString().Trim();
    }

    internal static string BuildUniqueOutputPath(string sourcePath, string targetExtOrSuffix, string? outputDirectory = null)
    {
        string dir = !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory)
            ? outputDirectory
            : (Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "output";
        }

        string cleanSuffix = targetExtOrSuffix.TrimStart('.');
        string candidate = Path.Combine(dir, $"{stem}_converted.{cleanSuffix}");
        if (!File.Exists(candidate) && !string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        for (int i = 2; i < 1000; i++)
        {
            string numbered = Path.Combine(dir, $"{stem}_converted ({i}).{cleanSuffix}");
            if (!File.Exists(numbered))
            {
                return numbered;
            }
        }

        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}.{cleanSuffix}");
    }

    private static CustomWidgetTransformResult BuildSuccessResult(
        string op,
        string sourcePath,
        string outputPath,
        string summaryText)
    {
        long bytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
        return new CustomWidgetTransformResult(
            Succeeded: true,
            Operation: op,
            SourcePath: sourcePath,
            OutputPath: outputPath,
            OutputName: Path.GetFileName(outputPath),
            SizeText: FormatByteSize(bytes),
            ResultText: summaryText,
            ErrorMessage: null);
    }

    private static CustomWidgetTransformResult Fail(string op, string sourcePath, string error) =>
        new(
            Succeeded: false,
            Operation: op,
            SourcePath: sourcePath,
            OutputPath: string.Empty,
            OutputName: string.Empty,
            SizeText: string.Empty,
            ResultText: string.Empty,
            ErrorMessage: error);

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return FormattableString.Invariant($"{bytes / (1024.0 * 1024.0):0.1} MB");
        }

        if (bytes >= 1024)
        {
            return FormattableString.Invariant($"{bytes / 1024.0:0.1} KB");
        }

        return FormattableString.Invariant($"{bytes} B");
    }
}
