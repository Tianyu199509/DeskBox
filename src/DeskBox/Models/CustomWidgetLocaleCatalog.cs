using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Models;

public sealed record CustomWidgetSupportedLocaleInfo(
    string Code,
    string NativeName,
    string EnglishName);

public sealed record CustomWidgetLocaleMeta(
    int SchemaVersion,
    string BaseLocale,
    IReadOnlyDictionary<string, string> Hashes)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly IReadOnlyDictionary<string, string> EmptyHashes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static readonly CustomWidgetLocaleMeta Empty =
        new(CurrentSchemaVersion, "zh-CN", EmptyHashes);

    public static string ComputeTextHash(string? text)
    {
        string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(normalized), hash);
        return Convert.ToHexStringLower(hash[..4]);
    }

    public static CustomWidgetLocaleMeta BuildFromBaseDictionary(
        string baseLocale,
        IReadOnlyDictionary<string, string> baseEntries)
    {
        string normalizedBase = LocalizationService.NormalizeCultureCode(
            string.IsNullOrWhiteSpace(baseLocale) ? "zh-CN" : baseLocale);
        var hashes = new Dictionary<string, string>(baseEntries.Count, StringComparer.Ordinal);
        foreach ((string key, string value) in baseEntries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                hashes[key] = ComputeTextHash(value);
            }
        }

        return new CustomWidgetLocaleMeta(CurrentSchemaVersion, normalizedBase, hashes);
    }

    public static CustomWidgetLocaleMeta Create(
        string baseLocale,
        IReadOnlyDictionary<string, string> baseEntries) =>
        BuildFromBaseDictionary(baseLocale, baseEntries);

    public string SerializeToJson() =>
        CustomWidgetLocaleCatalog.FormatMetaJson(this);

    public void WriteTo(Utf8JsonWriter writer) =>
        CustomWidgetLocaleCatalog.WriteMetaElement(writer, this);

    public static CustomWidgetLocaleMeta? ParseJson(string? rawMetaJson) =>
        CustomWidgetLocaleCatalog.TryParseMetaJson(rawMetaJson);

    public static CustomWidgetLocaleMeta ParseElement(JsonElement el) =>
        CustomWidgetLocaleCatalog.ParseMetaElement(el);
}

/// <summary>
/// Canonical per-locale localization catalog for a DeskBox Custom Widget, backed on disk by
/// <c>locales/&lt;culture&gt;.json</c> and <c>locales/_meta.json</c>.
/// Supports lazy active-culture loading at runtime and lock-free per-locale atomic writes in Studio.
/// </summary>
public sealed class CustomWidgetLocaleCatalog
{
    public const string DirectoryName = "locales";
    public const string MetaFileName = "_meta.json";
    public const int CurrentMetaSchemaVersion = CustomWidgetLocaleMeta.CurrentSchemaVersion;
    public const int MaxSingleLocaleBytes = 64 * 1024;
    public const int MaxTotalCatalogBytes = 512 * 1024;
    public const int MaxSingleLocaleFileBytes = MaxSingleLocaleBytes;
    public const int MaxTotalLocaleCatalogBytes = MaxTotalCatalogBytes;

    private static readonly JsonWriterOptions IndentedWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions LenientReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyMap =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    public static CustomWidgetLocaleCatalog Empty { get; } = new(null, EmptyMap);

    public static IReadOnlyList<CustomWidgetSupportedLocaleInfo> SupportedLocales { get; } =
    [
        new("zh-CN", "简体中文", "Simplified Chinese"),
        new("en-US", "English", "English (US)"),
        new("zh-TW", "繁體中文", "Traditional Chinese"),
        new("ja-JP", "日本語", "Japanese"),
        new("ko-KR", "한국어", "Korean"),
        new("de-DE", "Deutsch", "German"),
        new("fr-FR", "Français", "French"),
        new("es-ES", "Español", "Spanish"),
        new("pt-BR", "Português", "Portuguese (Brazil)"),
        new("ru-RU", "Русский", "Russian"),
        new("ar-SA", "العربية", "Arabic"),
        new("hi-IN", "हिन्दी", "Hindi"),
        new("bn-BD", "বাংলা", "Bengali")
    ];

    public static IReadOnlyList<string> SupportedCultureCodes { get; } =
        SupportedLocales.Select(static x => x.Code).ToArray();

    public static IReadOnlyList<string> BilingualCultureCodes { get; } =
        ["zh-CN", "en-US"];

    public static IReadOnlyList<string> ResolveBilingualCultureCodes(
        string? activeUiCulture,
        string? baseLocale = null)
    {
        string normalizedUi = NormalizeLocaleFileCode(
            LocalizationService.NormalizeCultureCode(
                string.IsNullOrWhiteSpace(activeUiCulture) ? "zh-CN" : activeUiCulture));
        if (!SupportedCultureCodes.Contains(normalizedUi, StringComparer.OrdinalIgnoreCase))
        {
            normalizedUi = "zh-CN";
        }

        if (!string.Equals(normalizedUi, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return [normalizedUi, "en-US"];
        }

        if (!string.IsNullOrWhiteSpace(baseLocale))
        {
            string normalizedBase = NormalizeLocaleFileCode(
                LocalizationService.NormalizeCultureCode(baseLocale));
            if (SupportedCultureCodes.Contains(normalizedBase, StringComparer.OrdinalIgnoreCase) &&
                !string.Equals(normalizedBase, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                return ["en-US", normalizedBase];
            }
        }

        return ["en-US", "zh-CN"];
    }

    public CustomWidgetLocaleCatalog(
        CustomWidgetLocaleMeta? meta,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? locales)
    {
        var normalized = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (locales is not null)
        {
            foreach ((string rawCulture, IReadOnlyDictionary<string, string>? entries) in locales)
            {
                if (string.IsNullOrWhiteSpace(rawCulture) ||
                    rawCulture.StartsWith('_') ||
                    entries is null ||
                    entries.Count == 0)
                {
                    continue;
                }

                string culture = NormalizeLocaleFileCode(rawCulture);
                var cleanEntries = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
                foreach ((string k, string v) in entries)
                {
                    if (!string.IsNullOrWhiteSpace(k) && v is not null)
                    {
                        cleanEntries[k.Trim()] = v;
                    }
                }

                if (cleanEntries.Count > 0)
                {
                    normalized[culture] = cleanEntries;
                }
            }
        }

        Locales = normalized;
        Meta = meta;
    }

    public CustomWidgetLocaleCatalog(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? locales,
        CustomWidgetLocaleMeta? meta = null)
        : this(meta, locales)
    {
    }

    public CustomWidgetLocaleMeta? Meta { get; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Locales { get; }

    public bool IsEmpty => Locales.Count == 0;

    public bool HasAnyLocale => Locales.Count > 0;

    public int Count => Locales.Count;

    public static string NormalizeLocaleFileCode(string rawCode)
    {
        string trimmed = (rawCode ?? string.Empty).Trim().Replace('_', '-');
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^5];
        }

        foreach (CustomWidgetSupportedLocaleInfo info in SupportedLocales)
        {
            if (string.Equals(info.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return info.Code;
            }
        }

        return trimmed;
    }

    public static CustomWidgetSupportedLocaleInfo GetLocaleInfo(string cultureCode)
    {
        string normalized = NormalizeLocaleFileCode(cultureCode);
        foreach (CustomWidgetSupportedLocaleInfo info in SupportedLocales)
        {
            if (string.Equals(info.Code, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }
        }

        return new CustomWidgetSupportedLocaleInfo(normalized, normalized, normalized);
    }

    public CustomWidgetLocaleCatalog WithLocale(
        string cultureCode,
        IReadOnlyDictionary<string, string> entries,
        CustomWidgetLocaleMeta? updatedMeta = null)
    {
        string normalized = NormalizeLocaleFileCode(cultureCode);
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(Locales, StringComparer.OrdinalIgnoreCase)
        {
            [normalized] = new Dictionary<string, string>(entries, StringComparer.Ordinal)
        };
        return new CustomWidgetLocaleCatalog(updatedMeta ?? Meta, copy);
    }

    public CustomWidgetLocaleCatalog WithMeta(CustomWidgetLocaleMeta? meta) =>
        new(meta, Locales);

    public string ComputeSha256()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        byte[] canonicalBytes = SerializeToUtf8Bytes(indented: false);
        byte[] hash = SHA256.HashData(canonicalBytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeLocaleDictionaryHash16(IReadOnlyDictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactWriterOptions))
        {
            WriteStringDictionary(writer, entries);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(ms.ToArray(), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    public string SerializeToCanonicalJson(bool indented = true)
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(SerializeToUtf8Bytes(indented));
    }

    public void WriteCatalogObjectTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        if (Meta is not null && Meta.Hashes.Count > 0)
        {
            writer.WritePropertyName("_meta");
            WriteMetaElement(writer, Meta);
        }

        foreach (string code in OrderLocaleKeys(Locales.Keys))
        {
            if (!Locales.TryGetValue(code, out IReadOnlyDictionary<string, string>? dict) || dict.Count == 0)
            {
                continue;
            }

            writer.WritePropertyName(code);
            WriteStringDictionary(writer, dict);
        }

        writer.WriteEndObject();
    }

    private byte[] SerializeToUtf8Bytes(bool indented)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, indented ? IndentedWriterOptions : CompactWriterOptions))
        {
            WriteCatalogObjectTo(writer);
        }

        return ms.ToArray();
    }

    public static string SerializeSingleLocaleJson(
        IReadOnlyDictionary<string, string> entries,
        IReadOnlyList<string>? keyOrder = null) =>
        FormatLocaleDictionaryJson(entries, keyOrder);

    public static string FormatLocaleDictionaryJson(IReadOnlyDictionary<string, string> entries, IReadOnlyList<string>? keyOrder = null)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            WriteStringDictionary(writer, entries, keyOrder);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string FormatMetaJson(CustomWidgetLocaleMeta meta)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            WriteMetaElement(writer, meta);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static CustomWidgetLocaleCatalog LoadFromDirectory(string localesDirectory)
    {
        if (string.IsNullOrWhiteSpace(localesDirectory) || !Directory.Exists(localesDirectory))
        {
            return Empty;
        }

        try
        {
            CustomWidgetLocaleMeta? meta = null;
            string metaPath = Path.Combine(localesDirectory, MetaFileName);
            if (File.Exists(metaPath))
            {
                string? rawMeta = TryReadBoundedFile(metaPath, MaxSingleLocaleBytes);
                if (!string.IsNullOrWhiteSpace(rawMeta))
                {
                    meta = TryParseMetaJson(rawMeta);
                }
            }

            var locales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            int totalBytes = 0;

            foreach (string file in Directory.EnumerateFiles(localesDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('_') || fileName.StartsWith('.'))
                {
                    continue;
                }

                FileInfo info = new(file);
                if (info.Length <= 0 || info.Length > MaxSingleLocaleBytes)
                {
                    continue;
                }

                totalBytes += (int)info.Length;
                if (totalBytes > MaxTotalCatalogBytes)
                {
                    break;
                }

                string raw = File.ReadAllText(file, Encoding.UTF8);
                Dictionary<string, string> parsed = ParseSingleLocaleDictionary(raw);
                if (parsed.Count > 0)
                {
                    string code = NormalizeLocaleFileCode(Path.GetFileNameWithoutExtension(fileName));
                    locales[code] = parsed;
                }
            }

            return locales.Count == 0 ? Empty : new CustomWidgetLocaleCatalog(meta, locales);
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Lazy runtime loader for active desktop widgets: parses only the active UI culture file
    /// plus base/English fallback files so inactive languages consume zero RAM and parse time.
    /// </summary>
    public static CustomWidgetLocaleCatalog LoadLazyForRuntimeCulture(
        string localesDirectory,
        string? activeCultureName)
    {
        if (string.IsNullOrWhiteSpace(localesDirectory) || !Directory.Exists(localesDirectory))
        {
            return Empty;
        }

        try
        {
            string effective = string.IsNullOrWhiteSpace(activeCultureName)
                ? Localized.ActiveCultureName
                : LocalizationService.NormalizeCultureCode(activeCultureName);

            CustomWidgetLocaleMeta? meta = null;
            string metaPath = Path.Combine(localesDirectory, MetaFileName);
            if (File.Exists(metaPath))
            {
                string? rawMeta = TryReadBoundedFile(metaPath, MaxSingleLocaleBytes);
                if (!string.IsNullOrWhiteSpace(rawMeta))
                {
                    meta = TryParseMetaJson(rawMeta);
                }
            }

            var candidateCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeLocaleFileCode(effective),
                "en-US",
                "zh-CN"
            };

            if (!string.IsNullOrWhiteSpace(meta?.BaseLocale))
            {
                candidateCodes.Add(NormalizeLocaleFileCode(meta.BaseLocale));
            }

            if (effective.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                candidateCodes.Add("zh-TW");
            }

            var loaded = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string code in candidateCodes)
            {
                string filePath = Path.Combine(localesDirectory, code + ".json");
                string? raw = TryReadBoundedFile(filePath, MaxSingleLocaleBytes);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                Dictionary<string, string> entries = ParseSingleLocaleDictionary(raw);
                if (entries.Count > 0)
                {
                    loaded[code] = entries;
                }
            }

            // If exact active culture wasn't present, check language-prefix match (e.g., fr-CA -> fr-FR.json)
            if (!loaded.ContainsKey(effective))
            {
                int dash = effective.IndexOf('-');
                string langPrefix = (dash > 0 ? effective[..dash] : effective) + "-";
                foreach (string file in Directory.EnumerateFiles(localesDirectory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith('_') || name.StartsWith('.'))
                    {
                        continue;
                    }

                    if (name.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase) ||
                        loaded.Count == 0)
                    {
                        string? raw = TryReadBoundedFile(file, MaxSingleLocaleBytes);
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            Dictionary<string, string> entries = ParseSingleLocaleDictionary(raw);
                            if (entries.Count > 0)
                            {
                                loaded[NormalizeLocaleFileCode(name)] = entries;
                                if (name.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return loaded.Count == 0 ? Empty : new CustomWidgetLocaleCatalog(meta, loaded);
        }
        catch
        {
            return Empty;
        }
    }

    public static void SaveToDirectory(
        string localesDirectory,
        CustomWidgetLocaleCatalog? catalog,
        bool removeStaleFiles = true,
        IReadOnlyList<string>? preferredKeyOrder = null) =>
        (catalog ?? Empty).WriteToDirectoryAtomically(localesDirectory, removeStaleFiles, preferredKeyOrder);

    public static void WriteOrDeleteDirectoryAtomically(
        CustomWidgetLocaleCatalog? catalog,
        string localesDirectory) =>
        SaveToDirectory(localesDirectory, catalog, removeStaleFiles: true);

    public void WriteToDirectoryAtomically(
        string localesDirectory,
        bool removeStaleFiles = true,
        IReadOnlyList<string>? preferredKeyOrder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localesDirectory);

        if (IsEmpty)
        {
            if (Directory.Exists(localesDirectory))
            {
                DeleteDirectoryRecursive(localesDirectory);
            }
            return;
        }

        Directory.CreateDirectory(localesDirectory);
        var expectedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CustomWidgetLocaleMeta? effectiveMeta = Meta;
        if (effectiveMeta is null)
        {
            string inferredBase = Locales.ContainsKey("zh-CN")
                ? "zh-CN"
                : Locales.ContainsKey("en-US")
                    ? "en-US"
                    : Locales.Keys.First();
            effectiveMeta = CustomWidgetLocaleMeta.BuildFromBaseDictionary(inferredBase, Locales[inferredBase]);
        }

        if (effectiveMeta.Hashes.Count > 0)
        {
            string metaPath = Path.Combine(localesDirectory, MetaFileName);
            string metaJson = FormatMetaJson(effectiveMeta);
            ReplaceTextFileAtomically(metaPath, metaJson);
            expectedFileNames.Add(MetaFileName);
        }

        foreach ((string code, IReadOnlyDictionary<string, string> entries) in Locales)
        {
            if (entries.Count == 0 || !IsSafeLocaleCode(code))
            {
                continue;
            }

            string normalized = NormalizeLocaleFileCode(code);
            string fileName = normalized + ".json";
            string filePath = Path.Combine(localesDirectory, fileName);
            string json = FormatLocaleDictionaryJson(entries, preferredKeyOrder);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaxSingleLocaleBytes)
            {
                throw new InvalidDataException(
                    $"Locale file '{fileName}' ({byteCount} bytes) exceeds the {MaxSingleLocaleBytes / 1024} KB per-locale limit.");
            }

            ReplaceTextFileAtomically(filePath, json);
            expectedFileNames.Add(fileName);
        }

        if (removeStaleFiles)
        {
            foreach (string existingFile in Directory.EnumerateFiles(localesDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(existingFile);
                if (!expectedFileNames.Contains(fileName))
                {
                    DeleteFileIfExists(existingFile);
                }
            }
        }
    }

    public static void WriteSingleLocaleAtomically(
        string localesDirectory,
        string cultureCode,
        IReadOnlyDictionary<string, string> entries,
        CustomWidgetLocaleMeta? updatedMeta = null,
        IReadOnlyList<string>? preferredKeyOrder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localesDirectory);
        string normalized = NormalizeLocaleFileCode(cultureCode);
        if (!IsSafeLocaleCode(normalized))
        {
            throw new InvalidDataException($"Invalid locale code '{cultureCode}'.");
        }

        Directory.CreateDirectory(localesDirectory);
        if (updatedMeta is { Hashes.Count: > 0 })
        {
            string metaPath = Path.Combine(localesDirectory, MetaFileName);
            ReplaceTextFileAtomically(metaPath, FormatMetaJson(updatedMeta));
        }

        string filePath = Path.Combine(localesDirectory, normalized + ".json");
        if (entries.Count == 0)
        {
            DeleteFileIfExists(filePath);
            return;
        }

        string json = FormatLocaleDictionaryJson(entries, preferredKeyOrder);
        int byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxSingleLocaleBytes)
        {
            throw new InvalidDataException(
                $"Locale file '{normalized}.json' ({byteCount} bytes) exceeds the {MaxSingleLocaleBytes / 1024} KB per-locale limit.");
        }

        ReplaceTextFileAtomically(filePath, json);
    }

    public static CustomWidgetLocaleCatalog ParseCatalogJson(string? rawCatalogJson)
    {
        if (string.IsNullOrWhiteSpace(rawCatalogJson))
        {
            return Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawCatalogJson, LenientReadOptions);
            return ParseCatalogElement(doc.RootElement);
        }
        catch
        {
            return Empty;
        }
    }

    public static CustomWidgetLocaleCatalog ParseCatalogElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        JsonElement target = root;
        if (root.TryGetProperty("locales", out JsonElement localesEl) &&
            localesEl.ValueKind == JsonValueKind.Object)
        {
            target = localesEl;
        }

        CustomWidgetLocaleMeta? meta = null;
        if (target.TryGetProperty("_meta", out JsonElement metaEl) &&
            metaEl.ValueKind == JsonValueKind.Object)
        {
            meta = ParseMetaElement(metaEl);
        }

        var locales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in target.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name) ||
                prop.Name.StartsWith('_') ||
                prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            FlattenLocaleObject(prop.Value, string.Empty, entries, depth: 0);
            if (entries.Count > 0)
            {
                locales[NormalizeLocaleFileCode(prop.Name)] = entries;
            }
        }

        return locales.Count == 0 ? Empty : new CustomWidgetLocaleCatalog(meta, locales);
    }

    public static Dictionary<string, string> ParseSingleLocaleJson(string? rawLocaleJson) =>
        ParseSingleLocaleDictionary(rawLocaleJson);

    public static Dictionary<string, string> ParseSingleLocaleElement(JsonElement element)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            FlattenLocaleObject(element, string.Empty, entries, depth: 0);
        }

        return entries;
    }

    public static Dictionary<string, string> ParseSingleLocaleDictionary(string? rawLocaleJson)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(rawLocaleJson))
        {
            return entries;
        }

        using JsonDocument doc = JsonDocument.Parse(rawLocaleJson, LenientReadOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Locale file root must be a JSON object mapping keys to translated strings.");
        }

        FlattenLocaleObject(doc.RootElement, string.Empty, entries, depth: 0);
        return entries;
    }

    public static CustomWidgetLocaleMeta? TryParseMetaJson(string? rawMetaJson)
    {
        if (string.IsNullOrWhiteSpace(rawMetaJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawMetaJson, LenientReadOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ParseMetaElement(doc.RootElement)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static CustomWidgetLocaleMeta ParseMetaElement(JsonElement el)
    {
        int schemaVersion = el.TryGetProperty("schemaVersion", out JsonElement svEl) &&
                            svEl.ValueKind == JsonValueKind.Number &&
                            svEl.TryGetInt32(out int sv)
            ? Math.Max(1, sv)
            : CustomWidgetLocaleMeta.CurrentSchemaVersion;

        string baseLocale = el.TryGetProperty("baseLocale", out JsonElement blEl) &&
                            blEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(blEl.GetString())
            ? NormalizeLocaleFileCode(blEl.GetString()!)
            : "zh-CN";

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (el.TryGetProperty("hashes", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in hEl.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(p.Name) && p.Value.ValueKind == JsonValueKind.String)
                {
                    hashes[p.Name] = p.Value.GetString() ?? string.Empty;
                }
            }
        }

        return new CustomWidgetLocaleMeta(schemaVersion, baseLocale, hashes);
    }

    public static string ComputeTextHash8(string? text) =>
        CustomWidgetLocaleMeta.ComputeTextHash(text);

    public static bool IsValidCultureCode(string? code) =>
        IsSafeLocaleCode(code);

    public static bool IsRightToLeftCulture(string? cultureCode)
    {
        if (string.IsNullOrWhiteSpace(cultureCode))
        {
            return false;
        }

        string trimmed = cultureCode.Trim();
        return trimmed.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("he", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("fa", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("ur", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeLocaleCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 24)
        {
            return false;
        }

        foreach (char ch in code)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> OrderLocaleKeys(IEnumerable<string> keys)
    {
        var remaining = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (CustomWidgetSupportedLocaleInfo supported in SupportedLocales)
        {
            if (remaining.Remove(supported.Code))
            {
                ordered.Add(supported.Code);
            }
        }

        ordered.AddRange(remaining.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    internal static void WriteMetaElement(Utf8JsonWriter writer, CustomWidgetLocaleMeta meta)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", Math.Max(1, meta.SchemaVersion));
        writer.WriteString("baseLocale", NormalizeLocaleFileCode(meta.BaseLocale));
        writer.WritePropertyName("hashes");
        writer.WriteStartObject();
        foreach ((string k, string v) in meta.Hashes.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            writer.WriteString(k, v);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void ReplaceTextFileAtomically(string destinationPath, string content) =>
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(destinationPath, content);

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            CustomWidgetStorageFileOps.DeleteFileIfExists(filePath);
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryRecursive(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                CustomWidgetStorageFileOps.DeleteDirectoryRecursive(directoryPath);
            }
        }
        catch
        {
        }
    }

    private static void WriteStringDictionary(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> entries,
        IReadOnlyList<string>? preferredKeyOrder = null)
    {
        writer.WriteStartObject();
        if (preferredKeyOrder is { Count: > 0 })
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in preferredKeyOrder)
            {
                if (entries.TryGetValue(key, out string? val) && written.Add(key))
                {
                    writer.WriteString(key, val);
                }
            }

            foreach ((string key, string val) in entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                if (written.Add(key))
                {
                    writer.WriteString(key, val);
                }
            }
        }
        else
        {
            foreach ((string key, string val) in entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, val);
            }
        }

        writer.WriteEndObject();
    }

    private static void FlattenLocaleObject(
        JsonElement obj,
        string prefix,
        Dictionary<string, string> destination,
        int depth)
    {
        if (depth > 3 || obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name) || prop.Name.StartsWith('_'))
            {
                continue;
            }

            string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenLocaleObject(prop.Value, key, destination, depth + 1);
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                destination[key] = prop.Value.GetString() ?? string.Empty;
            }
        }
    }

    private static string? TryReadBoundedFile(string filePath, int maxBytes)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length <= 0 || info.Length > maxBytes)
            {
                return null;
            }

            string content = File.ReadAllText(filePath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }
}
