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

public sealed record CustomWidgetImportedBundle(
    CustomWidgetManifest Manifest,
    string RawJson,
    string? LogicJs = null,
    CustomWidgetLocaleCatalog? Locales = null);

/// <summary>
/// Handles standard ZIP widget share-package (<c>.deskbox-widget</c> / <c>.deskboxwidget</c> / <c>.dbbox</c>)
/// export, personal-path/secret sanitization, and version-gated package or raw <c>manifest.json</c> import.
/// </summary>
public static class CustomWidgetPackageService
{
    public const string SharePackageExtension = ".deskbox-widget";
    public const string CompactPackageExtension = ".deskboxwidget";
    public const string LegacyPackageExtension = ".dbbox";
    public const int CurrentPackageVersion = 1;
    private const long MaxPackageEntryBytes = 2 * 1024 * 1024; // 2 MB

    public static IReadOnlyList<string> SupportedImportExtensions { get; } =
    [
        SharePackageExtension,
        CompactPackageExtension,
        LegacyPackageExtension,
        ".json"
    ];

    private static readonly Regex WindowsUserProfileRegex = new(
        @"[A-Za-z]:(?:\\\\|\\|/)Users(?:\\\\|\\|/)[^\\/"":*?<>|\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly RegexUnixProfileWrapper UnixProfile = new();

    private static readonly Regex InlineSecretRegex = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AIza[A-Za-z0-9_\-]{30,}|xox[baprs]-[A-Za-z0-9\-]{16,}|AKIA[0-9A-Z]{16}|(?:ntn|secret)_[A-Za-z0-9]{20,}|Bearer\s+[A-Za-z0-9\._\-]{16,})\b",
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

        static bool LooksLikeNonPortableDrivePath(string value) =>
            value.Length >= 3 &&
            char.IsLetter(value[0]) &&
            value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/');

        void WriteScrubbedElement(Utf8JsonWriter writer, JsonElement element, bool insideSettings = false, bool insideState = false, string? propertyName = null)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty prop in element.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);
                        bool nextSettings = insideSettings || string.Equals(prop.Name, "settings", StringComparison.Ordinal);
                        bool nextState = insideState || (!insideSettings && string.Equals(prop.Name, "state", StringComparison.Ordinal));
                        WriteScrubbedElement(writer, prop.Value, nextSettings, nextState, prop.Name);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        WriteScrubbedElement(writer, item, insideSettings, insideState);
                    }
                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String:
                {
                    string scrubbedVal = ScrubString(element.GetString() ?? string.Empty);
                    bool isSensitivePathProp =
                        string.Equals(propertyName, "path", StringComparison.Ordinal) ||
                        string.Equals(propertyName, "workingDirectory", StringComparison.Ordinal) ||
                        string.Equals(propertyName, "executable", StringComparison.Ordinal);
                    if (((insideSettings && string.Equals(propertyName, "default", StringComparison.Ordinal)) ||
                         insideState ||
                         isSensitivePathProp) &&
                        LooksLikeNonPortableDrivePath(scrubbedVal))
                    {
                        scrubbedVal = string.Empty;
                        sanitizedPaths++;
                    }
                    writer.WriteStringValue(scrubbedVal);
                    break;
                }

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

    public static (string? SanitizedLogicJs, int SanitizedPathsCount, int StrippedSecretsCount) SanitizeLogicJsForSharing(
        string? rawLogicJs)
    {
        if (string.IsNullOrWhiteSpace(rawLogicJs))
        {
            return (null, 0, 0);
        }

        int sanitizedPaths = 0;
        int strippedSecrets = 0;
        string cleaned = rawLogicJs;
        string currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentProfile))
        {
            string currentProfileEscaped = currentProfile.Replace("\\", "\\\\");
            if (!string.Equals(currentProfileEscaped, currentProfile, StringComparison.Ordinal) &&
                cleaned.Contains(currentProfileEscaped, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Replace(currentProfileEscaped, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                sanitizedPaths++;
            }

            if (cleaned.Contains(currentProfile, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Replace(currentProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                sanitizedPaths++;
            }

            string currentProfileSlash = currentProfile.Replace('\\', '/');
            if (!string.Equals(currentProfileSlash, currentProfile, StringComparison.Ordinal) &&
                cleaned.Contains(currentProfileSlash, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Replace(currentProfileSlash, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
                sanitizedPaths++;
            }
        }

        if (WindowsUserProfileRegex.IsMatch(cleaned))
        {
            cleaned = WindowsUserProfileRegex.Replace(cleaned, _ =>
            {
                sanitizedPaths++;
                return "%USERPROFILE%";
            });
        }

        if (UnixProfile.Pattern.IsMatch(cleaned))
        {
            cleaned = UnixProfile.Pattern.Replace(cleaned, _ =>
            {
                sanitizedPaths++;
                return "~";
            });
        }

        if (InlineSecretRegex.IsMatch(cleaned))
        {
            cleaned = InlineSecretRegex.Replace(cleaned, _ =>
            {
                strippedSecrets++;
                return "<REDACTED_SECRET>";
            });
        }

        if (Encoding.UTF8.GetByteCount(cleaned) > CustomWidgetScriptValidator.MaxLogicJsBytes)
        {
            throw new InvalidDataException($"logic.js exceeds the {CustomWidgetScriptValidator.MaxLogicJsBytes / 1024} KB limit.");
        }

        return (cleaned, sanitizedPaths, strippedSecrets);
    }

    public static (IReadOnlyDictionary<string, string> SanitizedEntries, int SanitizedPathsCount, int StrippedSecretsCount)
        SanitizeLocaleEntriesForSharing(IReadOnlyDictionary<string, string> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return (entries ?? new Dictionary<string, string>(StringComparer.Ordinal), 0, 0);
        }

        int sanitizedPaths = 0;
        int strippedSecrets = 0;
        string currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string currentProfileSlash = currentProfile.Replace('\\', '/');

        string ScrubLocaleValue(string input)
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

        var resultDict = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in entries)
        {
            resultDict[pair.Key] = ScrubLocaleValue(pair.Value);
        }

        return (resultDict, sanitizedPaths, strippedSecrets);
    }

    public static async Task<CustomWidgetExportReport> ExportSharePackageAsync(
        string widgetId,
        string destinationPath,
        bool sanitizeForSharing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
        string rawManifestJson = await store.ReadOrCreateManifestJsonAsync();
        string? rawLogicJs = await store.ReadLogicJsOrNullAsync();
        CustomWidgetLocaleCatalog? locales = await store.ReadLocaleCatalogAsync();
        rawManifestJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            rawManifestJson,
            rawLogicJs,
            locales,
            out rawLogicJs,
            out locales);
        if (!string.IsNullOrWhiteSpace(rawLogicJs))
        {
            rawManifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(rawManifestJson, rawLogicJs);
        }

        string sanitizedJson;
        string? sanitizedLogicJs;
        int sanitizedPaths = 0;
        int strippedSecrets = 0;
        if (sanitizeForSharing)
        {
            (sanitizedJson, sanitizedPaths, strippedSecrets) =
                SanitizeManifestForSharing(rawManifestJson);
            (sanitizedLogicJs, int jsPaths, int jsSecrets) =
                SanitizeLogicJsForSharing(rawLogicJs);
            sanitizedPaths += jsPaths;
            strippedSecrets += jsSecrets;
        }
        else
        {
            sanitizedJson = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(rawManifestJson);
            sanitizedLogicJs = rawLogicJs;
        }

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = destinationPath + ".tmp";
        try
        {
            await using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(fs, ZipArchiveMode.Create))
            {
                await WriteZipTextEntryAsync(archive, "manifest.json", sanitizedJson);

                if (!string.IsNullOrWhiteSpace(sanitizedLogicJs))
                {
                    await WriteZipTextEntryAsync(archive, CustomWidgetDataStore.LogicScriptFileName, sanitizedLogicJs);
                }

                if (locales is { HasAnyLocale: true })
                {
                    if (locales.Meta is not null)
                    {
                        await WriteZipTextEntryAsync(
                            archive,
                            $"{CustomWidgetLocaleCatalog.DirectoryName}/{CustomWidgetLocaleCatalog.MetaFileName}",
                            locales.Meta.SerializeToJson());
                    }

                    foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> kv in
                             locales.Locales.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (kv.Value.Count == 0 || !CustomWidgetLocaleCatalog.IsValidCultureCode(kv.Key))
                        {
                            continue;
                        }

                        IReadOnlyDictionary<string, string> localeEntries = kv.Value;
                        if (sanitizeForSharing)
                        {
                            (localeEntries, int localePaths, int localeSecrets) =
                                SanitizeLocaleEntriesForSharing(kv.Value);
                            sanitizedPaths += localePaths;
                            strippedSecrets += localeSecrets;
                        }

                        string localeJson = CustomWidgetLocaleCatalog.SerializeSingleLocaleJson(localeEntries);
                        await WriteZipTextEntryAsync(
                            archive,
                            $"{CustomWidgetLocaleCatalog.DirectoryName}/{kv.Key}.json",
                            localeJson);
                    }
                }
            }

            CustomWidgetStorageFileOps.MoveFileOverwrite(tempPath, destinationPath);
        }
        finally
        {
            try
            {
                CustomWidgetStorageFileOps.DeleteFileIfExists(tempPath);
            }
            catch
            {
            }
        }

        return new CustomWidgetExportReport(destinationPath, sanitizedPaths, strippedSecrets);
    }

    private static async Task WriteZipTextEntryAsync(ZipArchive archive, string entryPath, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes);
    }

    public static async Task<(CustomWidgetManifest Manifest, string RawJson)> ReadPackageOrJsonAsync(
        string filePath,
        bool markExternalAsCommunity = true)
    {
        CustomWidgetImportedBundle bundle = await ReadPackageBundleAsync(filePath, markExternalAsCommunity);
        return (bundle.Manifest, bundle.RawJson);
    }

    public static async Task<CustomWidgetImportedBundle> ReadPackageBundleAsync(
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
        string manifestJson;
        string? effectiveLogicJs = null;
        CustomWidgetLocaleCatalog? effectiveLocales = null;

        if (isZip)
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using ZipArchive archive = new(fs, ZipArchiveMode.Read);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(static e =>
                string.Equals(e.FullName.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(e.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException("The widget package does not contain a manifest.json file.");
            }

            if (entry.Length > MaxPackageEntryBytes)
            {
                throw new InvalidDataException("The manifest.json inside the package exceeds the 2 MB limit.");
            }

            using (StreamReader reader = new(entry.Open(), Encoding.UTF8))
            {
                manifestJson = await reader.ReadToEndAsync();
            }

            ZipArchiveEntry? logicEntry = archive.Entries.FirstOrDefault(static e =>
                string.Equals(Path.GetFileName(e.FullName), CustomWidgetDataStore.LogicScriptFileName, StringComparison.OrdinalIgnoreCase));
            if (logicEntry is not null)
            {
                if (logicEntry.Length > CustomWidgetScriptValidator.MaxLogicJsBytes)
                {
                    throw new InvalidDataException($"logic.js inside the package exceeds the {CustomWidgetScriptValidator.MaxLogicJsBytes / 1024} KB limit.");
                }
                using StreamReader jsReader = new(logicEntry.Open(), Encoding.UTF8);
                effectiveLogicJs = await jsReader.ReadToEndAsync();
            }

            effectiveLocales = await ReadLocalesFromZipArchiveAsync(archive);
        }
        else
        {
            manifestJson = await File.ReadAllTextAsync(filePath, Encoding.UTF8);

            if (string.Equals(Path.GetFileName(filePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                string? parentDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    string siblingJs = Path.Combine(parentDir, CustomWidgetDataStore.LogicScriptFileName);
                    if (File.Exists(siblingJs))
                    {
                        var siblingJsInfo = new FileInfo(siblingJs);
                        if (siblingJsInfo.Length > CustomWidgetScriptValidator.MaxLogicJsBytes)
                        {
                            throw new InvalidDataException(
                                $"logic.js exceeds the {CustomWidgetScriptValidator.MaxLogicJsBytes / 1024} KB limit.");
                        }
                        effectiveLogicJs = await File.ReadAllTextAsync(siblingJs, Encoding.UTF8);
                    }

                    string siblingLocalesDir = Path.Combine(parentDir, CustomWidgetDataStore.LocalesDirectoryName);
                    if (Directory.Exists(siblingLocalesDir))
                    {
                        IReadOnlyList<CustomWidgetValidationDiagnostic> rawDiags =
                            CustomWidgetScriptValidator.ValidateRawLocaleDirectoryFiles(siblingLocalesDir);
                        CustomWidgetValidationDiagnostic? firstErr = rawDiags.FirstOrDefault(static d => d.IsError);
                        if (firstErr is not null)
                        {
                            throw new InvalidDataException(firstErr.Message);
                        }

                        effectiveLocales = CustomWidgetLocaleCatalog.LoadFromDirectory(siblingLocalesDir);
                    }
                }
            }
        }

        manifestJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales,
            out effectiveLogicJs,
            out effectiveLocales);
        if (!string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, effectiveLogicJs);
        }
        (CustomWidgetManifest manifest, string preparedJson) =
            ParseAndPrepareImportedManifest(
                manifestJson,
                markExternalAsCommunity,
                effectiveLogicJs,
                effectiveLocales,
                enforceBundledLocales: isZip);
        return new CustomWidgetImportedBundle(manifest, preparedJson, effectiveLogicJs, manifest.I18n.Count > 0 && effectiveLocales is null ? null : effectiveLocales);
    }

    public static async Task<CustomWidgetImportedBundle> ReadPackageBundleFromBytesAsync(
        byte[] payloadBytes,
        bool markExternalAsCommunity = true)
    {
        ArgumentNullException.ThrowIfNull(payloadBytes);
        if (payloadBytes.Length == 0)
        {
            throw new InvalidDataException("The widget payload is empty.");
        }

        if (payloadBytes.Length > MaxPackageEntryBytes * 2)
        {
            throw new InvalidDataException("The widget package exceeds the 4 MB maximum file size.");
        }

        bool isZip = payloadBytes.Length >= 4 &&
                     payloadBytes[0] == 0x50 &&
                     payloadBytes[1] == 0x4B &&
                     payloadBytes[2] == 0x03 &&
                     payloadBytes[3] == 0x04;
        string manifestJson;
        string? effectiveLogicJs = null;
        CustomWidgetLocaleCatalog? effectiveLocales = null;

        if (isZip)
        {
            using var ms = new MemoryStream(payloadBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(static e =>
                string.Equals(e.FullName.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(e.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidDataException("The widget package does not contain a manifest.json file.");
            }

            if (entry.Length > MaxPackageEntryBytes)
            {
                throw new InvalidDataException("The manifest.json inside the package exceeds the 2 MB limit.");
            }

            using (StreamReader reader = new(entry.Open(), Encoding.UTF8))
            {
                manifestJson = await reader.ReadToEndAsync();
            }

            ZipArchiveEntry? logicEntry = archive.Entries.FirstOrDefault(static e =>
                string.Equals(Path.GetFileName(e.FullName), CustomWidgetDataStore.LogicScriptFileName, StringComparison.OrdinalIgnoreCase));
            if (logicEntry is not null)
            {
                if (logicEntry.Length > CustomWidgetScriptValidator.MaxLogicJsBytes)
                {
                    throw new InvalidDataException($"logic.js inside the package exceeds the {CustomWidgetScriptValidator.MaxLogicJsBytes / 1024} KB limit.");
                }
                using StreamReader jsReader = new(logicEntry.Open(), Encoding.UTF8);
                effectiveLogicJs = await jsReader.ReadToEndAsync();
            }

            effectiveLocales = await ReadLocalesFromZipArchiveAsync(archive);
        }
        else
        {
            manifestJson = Encoding.UTF8.GetString(payloadBytes).TrimStart('\uFEFF');
        }

        manifestJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales,
            out effectiveLogicJs,
            out effectiveLocales);
        if (!string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, effectiveLogicJs);
        }

        (CustomWidgetManifest manifest, string preparedJson) =
            ParseAndPrepareImportedManifest(
                manifestJson,
                markExternalAsCommunity,
                effectiveLogicJs,
                effectiveLocales,
                enforceBundledLocales: isZip);
        return new CustomWidgetImportedBundle(manifest, preparedJson, effectiveLogicJs, manifest.I18n.Count > 0 && effectiveLocales is null ? null : effectiveLocales);
    }

    private static async Task<CustomWidgetLocaleCatalog?> ReadLocalesFromZipArchiveAsync(ZipArchive archive)
    {
        var locales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        CustomWidgetLocaleMeta? meta = null;
        long totalBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith(CustomWidgetLocaleCatalog.DirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string subPath = normalized[(CustomWidgetLocaleCatalog.DirectoryName.Length + 1)..];
            if (string.IsNullOrWhiteSpace(subPath) ||
                subPath.Contains('/') ||
                !subPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Length > CustomWidgetLocaleCatalog.MaxSingleLocaleFileBytes)
            {
                throw new InvalidDataException(
                    $"{normalized} inside the package exceeds the {CustomWidgetLocaleCatalog.MaxSingleLocaleFileBytes / 1024} KB limit.");
            }

            totalBytes += entry.Length;
            if (totalBytes > CustomWidgetLocaleCatalog.MaxTotalLocaleCatalogBytes)
            {
                throw new InvalidDataException(
                    $"locales/ inside the package exceeds the {CustomWidgetLocaleCatalog.MaxTotalLocaleCatalogBytes / 1024} KB total limit.");
            }

            string rawJson;
            using (StreamReader reader = new(entry.Open(), Encoding.UTF8))
            {
                rawJson = await reader.ReadToEndAsync();
            }

            if (string.Equals(subPath, CustomWidgetLocaleCatalog.MetaFileName, StringComparison.OrdinalIgnoreCase))
            {
                meta = CustomWidgetLocaleMeta.ParseJson(rawJson)
                       ?? throw new InvalidDataException("locales/_meta.json inside the package is not valid JSON.");
                continue;
            }

            string culture = Path.GetFileNameWithoutExtension(subPath);
            if (!CustomWidgetLocaleCatalog.IsValidCultureCode(culture))
            {
                throw new InvalidDataException($"Invalid locale filename '{subPath}' inside locales/.");
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    rawJson,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{normalized} root must be a JSON object.");
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Invalid JSON in {normalized}: {ex.Message}");
            }

            IReadOnlyDictionary<string, string> entries = CustomWidgetLocaleCatalog.ParseSingleLocaleJson(rawJson);
            if (entries.Count > 0)
            {
                locales[culture] = entries;
            }
        }

        return locales.Count > 0 || meta is not null
            ? new CustomWidgetLocaleCatalog(locales, meta)
            : null;
    }

    public static (CustomWidgetManifest Manifest, string RawJson) ParseAndPrepareImportedManifest(
        string rawManifestJson,
        bool markExternalAsCommunity = true,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        bool enforceBundledLocales = false)
    {
        string manifestJson = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            rawManifestJson,
            logicJs,
            locales,
            out string? effectiveLogicJs,
            out CustomWidgetLocaleCatalog? effectiveLocales);
        if (!string.IsNullOrWhiteSpace(effectiveLogicJs))
        {
            manifestJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, effectiveLogicJs);
        }

        if (!enforceBundledLocales && effectiveLocales is not { HasAnyLocale: true })
        {
            effectiveLocales = TryResolveBuiltInTemplateLocales(manifestJson);
        }

        CustomWidgetParseResult parsed = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            effectiveLogicJs,
            effectiveLocales);

        if (CustomWidgetManifestParser.IsIncompatibleClientVersion(parsed.Diagnostics))
        {
            string msg = parsed.Diagnostics
                .First(static d => d.Code is CustomWidgetDiagnosticCodes.IncompatibleSchemaVersion
                                          or CustomWidgetDiagnosticCodes.IncompatibleAppVersion)
                .Message;
            throw new CustomWidgetIncompatibleVersionException(msg, parsed.Diagnostics);
        }

        IReadOnlyList<CustomWidgetValidationDiagnostic> blockingErrors = enforceBundledLocales
            ? parsed.Diagnostics.Where(static d => d.IsError).ToList()
            : parsed.Diagnostics.Where(static d => d.IsError && !string.Equals(d.Code, "MissingLocaleCatalog", StringComparison.Ordinal)).ToList();

        if (parsed.Manifest is null || blockingErrors.Count > 0)
        {
            string msg = blockingErrors.FirstOrDefault()?.Message ?? "Invalid manifest.json";
            throw new InvalidDataException(msg);
        }

        CustomWidgetManifest finalManifest = parsed.Manifest;
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
                finalManifest = reparsed.Manifest;
            }
        }

        if (effectiveLocales is { HasAnyLocale: true })
        {
            finalManifest = finalManifest.WithLocales(effectiveLocales);
        }

        return (finalManifest, manifestJson);
    }

    private static CustomWidgetLocaleCatalog? TryResolveBuiltInTemplateLocales(string manifestJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                manifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("id", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string rawId = (idEl.GetString() ?? string.Empty).Trim();
            if (rawId.Length == 0)
            {
                return null;
            }

            string stripped = rawId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
                ? rawId["deskbox.".Length..]
                : rawId;
            foreach (string tplId in CustomWidgetTemplates.BuiltInTemplateIds)
            {
                if (string.Equals(tplId, rawId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tplId, stripped, StringComparison.OrdinalIgnoreCase))
                {
                    return CustomWidgetTemplates.GetTemplateBundle(tplId).Locales;
                }
            }
        }
        catch
        {
            // Ignore malformed JSON here; parser reports syntax diagnostics immediately after.
        }

        return null;
    }
}
