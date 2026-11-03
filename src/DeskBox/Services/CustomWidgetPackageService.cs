using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetIncompatibleVersionException(
    string message,
    IReadOnlyList<CustomWidgetValidationDiagnostic> diagnostics)
    : NotSupportedException(message)
{
    public IReadOnlyList<CustomWidgetValidationDiagnostic> Diagnostics { get; } = diagnostics;
}

public sealed record CustomWidgetExportReport(
    string ExportedFilePath,
    int SanitizedPathsCount,
    int StrippedSecretsCount);

/// <summary>
/// Handles <c>.deskbox-widget</c> (and <c>.dbbox</c> / <c>.json</c>) share-package
/// export, personal-path/secret sanitization, and version-gated package import.
/// </summary>
public static class CustomWidgetPackageService
{
    public const string SharePackageExtension = ".deskbox-widget";
    public const string LegacyPackageExtension = ".dbbox";
    public const string EnvelopeFormatId = "deskbox-widget-package";
    public const int CurrentPackageVersion = 1;
    private const long MaxPackageEntryBytes = 2 * 1024 * 1024; // 2 MB

    public static IReadOnlyList<string> SupportedImportExtensions { get; } =
    [
        SharePackageExtension,
        LegacyPackageExtension,
        ".json"
    ];

    private static readonly Regex WindowsUserProfileRegex = new(
        @"[A-Za-z]:(?:\\|/)Users(?:\\|/)[^\\/"":*?<>|\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly RegexUnixProfileWrapper UnixProfile = new();

    private static readonly Regex InlineSecretRegex = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[A-Za-z0-9\._\-]{16,})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class RegexUnixProfileWrapper
    {
        public readonly Regex Pattern = new(
            @"(?<![A-Za-z0-9_])/(?:Users|home)/[^/\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    public static (string SanitizedJson, int SanitizedPathsCount, int StrippedSecretsCount) SanitizeManifestForSharing(
        string rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return (rawManifestJson, 0, 0);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch
        {
            return (rawManifestJson, 0, 0);
        }

        int sanitizedPaths = 0;
        int strippedSecrets = 0;
        string currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string ScrubString(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            string result = input;
            if (!string.IsNullOrWhiteSpace(currentProfile) &&
                result.Contains(currentProfile, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(currentProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                sanitizedPaths++;
            }

            string currentProfileSlash = currentProfile.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(currentProfileSlash) &&
                result.Contains(currentProfileSlash, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(currentProfileSlash, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                sanitizedPaths++;
            }

            result = WindowsUserProfileRegex.Replace(result, _ =>
            {
                sanitizedPaths++;
                return "%USERPROFILE%";
            });

            result = UnixProfile.Pattern.Replace(result, _ =>
            {
                sanitizedPaths++;
                return "%USERPROFILE%";
            });

            result = InlineSecretRegex.Replace(result, _ =>
            {
                strippedSecrets++;
                return string.Empty;
            });

            return result;
        }

        void WriteScrubbedElement(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty prop in element.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteScrubbedElement(writer, prop.Value);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        WriteScrubbedElement(writer, item);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                    writer.WriteStringValue(ScrubString(element.GetString() ?? string.Empty));
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        using (doc)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       ms,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                WriteScrubbedElement(writer, doc.RootElement);
            }

            string scrubbed = Encoding.UTF8.GetString(ms.ToArray());
            return (CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(scrubbed), sanitizedPaths, strippedSecrets);
        }
    }

    public static string WrapInSharePackageEnvelope(string sanitizedManifestJson)
    {
        using JsonDocument manifestDoc = JsonDocument.Parse(
            sanitizedManifestJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", EnvelopeFormatId);
            writer.WriteNumber("packageVersion", CurrentPackageVersion);
            writer.WriteString("exportedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("appVersion", CustomWidgetManifestParser.CurrentAppVersion.ToString());
            writer.WritePropertyName("manifest");
            manifestDoc.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task<CustomWidgetExportReport> ExportSharePackageAsync(
        string widgetId,
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
        string rawManifestJson = await store.ReadOrCreateManifestJsonAsync();
        (string sanitizedJson, int sanitizedPaths, int strippedSecrets) =
            SanitizeManifestForSharing(rawManifestJson);

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (destinationPath.EndsWith(LegacyPackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            using FileStream fs = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(fs, ZipArchiveMode.Create);
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using Stream entryStream = manifestEntry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(sanitizedJson);
            await entryStream.WriteAsync(bytes);
        }
        else
        {
            string envelopeJson = WrapInSharePackageEnvelope(sanitizedJson);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(destinationPath, envelopeJson);
        }

        return new CustomWidgetExportReport(destinationPath, sanitizedPaths, strippedSecrets);
    }

    public static string UnwrapEnvelopeOrRawManifestJson(string fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            return fileContent;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                fileContent,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("manifest", out JsonElement manifestEl) &&
                manifestEl.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("format", out _) ||
                 doc.RootElement.TryGetProperty("packageVersion", out _)))
            {
                return manifestEl.GetRawText();
            }
        }
        catch
        {
            // Let downstream manifest parser produce accurate line/column diagnostics.
        }

        return fileContent;
    }

    public static async Task<(CustomWidgetManifest Manifest, string RawJson)> ReadPackageOrJsonAsync(
        string filePath,
        bool markExternalAsCommunity = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Custom widget template file not found.", filePath);
        }

        FileInfo fileInfo = new(filePath);
        if (fileInfo.Length > MaxPackageEntryBytes * 2)
        {
            throw new InvalidDataException("The widget package exceeds the 4 MB maximum file size.");
        }

        byte[] headerBytes = new byte[4];
        await using (FileStream probe = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = await probe.ReadAsync(headerBytes.AsMemory(0, 4));
        }

        bool isZip = headerBytes is [0x50, 0x4B, 0x03, 0x04];
        string rawText;
        if (isZip)
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new(fs, ZipArchiveMode.Read);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(SharePackageExtension, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException("The widget package does not contain a manifest.json file.");
            }

            if (entry.Length > MaxPackageEntryBytes)
            {
                throw new InvalidDataException("The manifest.json inside the package exceeds the 2 MB limit.");
            }

            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            rawText = await reader.ReadToEndAsync();
        }
        else
        {
            rawText = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        }

        string manifestJson = UnwrapEnvelopeOrRawManifestJson(rawText);
        return ParseAndPrepareImportedManifest(manifestJson, markExternalAsCommunity);
    }

    public static (CustomWidgetManifest Manifest, string RawJson) ParseAndPrepareImportedManifest(
        string rawManifestOrEnvelopeJson,
        bool markExternalAsCommunity = true)
    {
        string manifestJson = UnwrapEnvelopeOrRawManifestJson(rawManifestOrEnvelopeJson);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);

        if (CustomWidgetManifestParser.IsIncompatibleClientVersion(parsed.Diagnostics))
        {
            string msg = parsed.Diagnostics
                .First(static d => d.Code is CustomWidgetDiagnosticCodes.IncompatibleSchemaVersion
                                          or CustomWidgetDiagnosticCodes.IncompatibleAppVersion)
                .Message;
            throw new CustomWidgetIncompatibleVersionException(msg, parsed.Diagnostics);
        }

        if (parsed.Manifest is null || parsed.Diagnostics.Any(static d => d.IsError))
        {
            string msg = parsed.Diagnostics.FirstOrDefault(static d => d.IsError)?.Message ?? "Invalid manifest.json";
            throw new InvalidDataException(msg);
        }

        if (markExternalAsCommunity)
        {
            manifestJson = CustomWidgetManifestMetadataStamper.StampProvenance(
                manifestJson,
                origin: CustomWidgetOriginKinds.Community,
                isCustomized: parsed.Manifest.IsCustomized,
                forkedFromId: parsed.Manifest.ForkedFromId,
                forkedFromVersion: parsed.Manifest.ForkedFromVersion);
            CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(manifestJson);
            if (reparsed.Manifest is not null)
            {
                return (reparsed.Manifest, manifestJson);
            }
        }

        return (parsed.Manifest, manifestJson);
    }
}
