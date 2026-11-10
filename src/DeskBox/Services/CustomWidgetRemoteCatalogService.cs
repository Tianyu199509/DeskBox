using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetRemoteCatalogDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public List<CustomWidgetRemoteCatalogItemDto> Widgets { get; set; } = [];
}

public sealed class CustomWidgetRemoteCatalogItemDto
{
    public string Id { get; set; } = string.Empty;
    public string? ManifestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public Dictionary<string, string>? Names { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DescriptionEn { get; set; }
    public Dictionary<string, string>? Descriptions { get; set; }
    public string? FeatureTag { get; set; }
    public string? FeatureTagEn { get; set; }
    public Dictionary<string, string>? FeatureTags { get; set; }
    public string IconGlyph { get; set; } = "\uE71D";
    public string BadgeColorHex { get; set; } = "#3B82F6";
    public string Version { get; set; } = "1.0.0";
    public int? MinSchemaVersion { get; set; }
    public string? MinAppVersion { get; set; }
    public bool Deprecated { get; set; }
    public int DefaultWidth { get; set; } = 324;
    public int DefaultHeight { get; set; } = 360;
    public string? BundleUrl { get; set; }
    public string? ManifestUrl { get; set; }
    public string? LogicUrl { get; set; }
    public JsonElement? Manifest { get; set; }
    public string? ManifestJson { get; set; }
    public string? LogicJs { get; set; }
    public string? BaseLocale { get; set; }
    public Dictionary<string, Dictionary<string, string>>? Locales { get; set; }
}

public sealed class CustomWidgetRemoteCatalogCacheMeta
{
    public string? ETag { get; set; }
    public string? SourceUrl { get; set; }
    public long LastCheckedUtcTicks { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CustomWidgetRemoteCatalogDocument))]
[JsonSerializable(typeof(CustomWidgetRemoteCatalogCacheMeta))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class CustomWidgetRemoteCatalogJsonContext : JsonSerializerContext;

public sealed record CustomWidgetCatalogEntry
{
    public required string Id { get; init; }
    public required string ManifestId { get; init; }
    public string NameKey { get; init; } = string.Empty;
    public string DescriptionKey { get; init; } = string.Empty;
    public string FeatureTagKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? NameEn { get; init; }
    public IReadOnlyDictionary<string, string>? Names { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? DescriptionEn { get; init; }
    public IReadOnlyDictionary<string, string>? Descriptions { get; init; }
    public string? FeatureTag { get; init; }
    public string? FeatureTagEn { get; init; }
    public IReadOnlyDictionary<string, string>? FeatureTags { get; init; }
    public string DefaultTitle { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = "\uE71D";
    public string BadgeColorHex { get; init; } = "#3B82F6";
    public string Version { get; init; } = "1.0.0";
    public int? MinSchemaVersion { get; init; }
    public string? MinAppVersion { get; init; }
    public bool Deprecated { get; init; }
    public int DefaultWidth { get; init; } = 324;
    public int DefaultHeight { get; init; } = 360;
    public string? ManifestJson { get; init; }
    public string? LogicJs { get; init; }
    public CustomWidgetLocaleCatalog? Locales { get; init; }
    public bool IsRemoteOnly { get; init; }
    public bool IsRemoteUpgraded { get; init; }

    public bool RequiresClientUpgrade =>
        CustomWidgetManifestParser.RequiresNewerClient(MinSchemaVersion, MinAppVersion, out _);

    public string UpgradeRequirementLabel =>
        CustomWidgetManifestParser.RequiresNewerClient(MinSchemaVersion, MinAppVersion, out string label)
            ? label
            : string.Empty;

    public string ResolveDisplayName(LocalizationService? localization = null)
    {
        string activeCulture = localization?.CurrentCultureName ?? Localized.ActiveCultureName;
        if (TryLookupLocalizedMap(Names, activeCulture, out string? mappedName))
        {
            return mappedName!;
        }

        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(NameKey) && localization is not null)
        {
            string localized = localization.T(NameKey);
            if (!string.Equals(localized, NameKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        if (!isChinese && TryLookupLocalizedMap(Names, "en-US", out string? enMappedName))
        {
            return enMappedName!;
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(NameEn))
        {
            return NameEn;
        }

        if (TryLookupLocalizedMap(Names, "zh-CN", out string? zhMappedName))
        {
            return zhMappedName!;
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }

        if (!string.IsNullOrWhiteSpace(NameKey) && localization is not null)
        {
            return localization.T(NameKey);
        }

        return DefaultTitle;
    }

    public string ResolveDescription(LocalizationService? localization = null)
    {
        string activeCulture = localization?.CurrentCultureName ?? Localized.ActiveCultureName;
        if (TryLookupLocalizedMap(Descriptions, activeCulture, out string? mappedDesc))
        {
            return mappedDesc!;
        }

        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(DescriptionKey) && localization is not null)
        {
            string localized = localization.T(DescriptionKey);
            if (!string.Equals(localized, DescriptionKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        if (!isChinese && TryLookupLocalizedMap(Descriptions, "en-US", out string? enMappedDesc))
        {
            return enMappedDesc!;
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(DescriptionEn))
        {
            return DescriptionEn;
        }

        if (TryLookupLocalizedMap(Descriptions, "zh-CN", out string? zhMappedDesc))
        {
            return zhMappedDesc!;
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            return Description;
        }

        if (!string.IsNullOrWhiteSpace(DescriptionKey) && localization is not null)
        {
            return localization.T(DescriptionKey);
        }

        return string.Empty;
    }

    public string ResolveFeatureTag(LocalizationService? localization = null)
    {
        string activeCulture = localization?.CurrentCultureName ?? Localized.ActiveCultureName;
        if (TryLookupLocalizedMap(FeatureTags, activeCulture, out string? mappedTag))
        {
            return mappedTag!;
        }

        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(FeatureTagKey) && localization is not null)
        {
            string localized = localization.T(FeatureTagKey);
            if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, FeatureTagKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!isChinese && TryLookupLocalizedMap(FeatureTags, "en-US", out string? enMappedTag))
        {
            return enMappedTag!;
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(FeatureTagEn))
        {
            return FeatureTagEn;
        }

        if (TryLookupLocalizedMap(FeatureTags, "zh-CN", out string? zhMappedTag))
        {
            return zhMappedTag!;
        }

        if (!string.IsNullOrWhiteSpace(FeatureTag))
        {
            return FeatureTag;
        }

        if (!string.IsNullOrWhiteSpace(FeatureTagKey) && localization is not null)
        {
            string localized = localization.T(FeatureTagKey);
            if (!string.Equals(localized, FeatureTagKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        return string.Empty;
    }

    private static bool TryLookupLocalizedMap(
        IReadOnlyDictionary<string, string>? map,
        string? cultureName,
        out string? value)
    {
        value = null;
        if (map is null || map.Count == 0 || string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        string normalized = cultureName.Trim().Replace('_', '-');
        foreach ((string k, string v) in map)
        {
            if (!string.IsNullOrWhiteSpace(v) &&
                string.Equals(k.Trim().Replace('_', '-'), normalized, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        int dash = normalized.IndexOf('-');
        string langPrefix = dash > 0 ? normalized[..dash] : normalized;
        if (string.Equals(langPrefix, "zh", StringComparison.OrdinalIgnoreCase))
        {
            bool wantsTraditional = normalized.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                                    normalized.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                                    normalized.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                                    normalized.Contains("Hant", StringComparison.OrdinalIgnoreCase);
            string preferredZh = wantsTraditional ? "zh-TW" : "zh-CN";
            foreach ((string k, string v) in map)
            {
                if (!string.IsNullOrWhiteSpace(v) &&
                    string.Equals(k.Trim().Replace('_', '-'), preferredZh, StringComparison.OrdinalIgnoreCase))
                {
                    value = v;
                    return true;
                }
            }
        }

        foreach ((string k, string v) in map)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                continue;
            }

            string cleanKey = k.Trim().Replace('_', '-');
            if (string.Equals(cleanKey, langPrefix, StringComparison.OrdinalIgnoreCase) ||
                cleanKey.StartsWith(langPrefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        return false;
    }
}

public enum CustomWidgetCatalogSyncStatus
{
    Throttled,
    UpToDate,
    Updated,
    OfflineOrFailed
}

public sealed record CustomWidgetCatalogSyncResult(
    CustomWidgetCatalogSyncStatus Status,
    int ChangedCount,
    int TotalOfficialCount,
    DateTimeOffset? LastCheckedUtc,
    bool AppliedCatalogResponse = false);

/// <summary>
/// Fetches, validates, caches, and merges the official DeskBox Custom Widget remote
/// template catalog (<c>catalog.v1.json</c>) with silent offline fallback to built-in presets.
/// </summary>
public sealed class CustomWidgetRemoteCatalogService
{
    public const string PrimaryCatalogUrl = "https://deskbox.fun/widgets/catalog.v1.json";
    public const string MirrorCatalogUrl = "https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/widgets.v1.json";

    private static readonly TimeSpan RefreshMinInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxRemoteManifestBytes = 512 * 1024;
    private const int MaxRemoteBundleBytes = 4 * 1024 * 1024;
    private const int MaxRemoteLogicJsBytes = CustomWidgetScriptValidator.MaxLogicJsBytes;

    public static CustomWidgetRemoteCatalogService Shared { get; } = new();

    private readonly string _cacheDirectory;
    private readonly string _bundlesCacheDirectory;
    private readonly string _catalogCachePath;
    private readonly string _metaCachePath;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private List<CustomWidgetRemoteCatalogItemDto>? _cachedRemoteItems;
    private long _lastRefreshAttemptUtcTicks;

    public event EventHandler? CatalogUpdated;

    public CustomWidgetRemoteCatalogService(
        string? cacheDirectory = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "cache", "widget-catalog")
            : cacheDirectory;
        _bundlesCacheDirectory = Path.Combine(_cacheDirectory, "bundles");
        _catalogCachePath = Path.Combine(_cacheDirectory, "catalog.v1.json");
        _metaCachePath = Path.Combine(_cacheDirectory, "meta.json");

        _httpClient = httpMessageHandler is not null
            ? new HttpClient(httpMessageHandler, disposeHandler: false) { Timeout = RequestTimeout }
            : CreateDefaultHttpClient();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedPublicIpAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeskBox", "1.0"));
        return client;
    }

    public IReadOnlyList<CustomWidgetCatalogEntry> GetMergedOfficialTemplates()
    {
        IReadOnlyList<CustomWidgetRemoteCatalogItemDto> remoteItems = EnsureSnapshotLoaded();
        var remoteByNormalizedId = new Dictionary<string, CustomWidgetRemoteCatalogItemDto>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetRemoteCatalogItemDto dto in remoteItems)
        {
            string normId = NormalizeTemplateId(dto.Id);
            if (!string.IsNullOrWhiteSpace(normId))
            {
                remoteByNormalizedId[normId] = dto;
            }
        }

        var merged = new List<CustomWidgetCatalogEntry>();
        var consumedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "release-checklist",
            "file-basket",
            "quick-snippets",
            "mini-hardware",
            "work-logger",
            "milestone-tracker"
        };

        foreach (CustomWidgetPresetInfo builtIn in CustomWidgetTemplates.Presets)
        {
            string normId = NormalizeTemplateId(builtIn.Id);
            consumedIds.Add(normId);

            CustomWidgetTemplateBundle builtInBundle = CustomWidgetTemplates.GetTemplateBundle(builtIn.Id);
            string builtInJson = builtInBundle.ManifestJson;
            string builtInVersion = ExtractManifestVersion(builtInJson);

            if (remoteByNormalizedId.TryGetValue(normId, out CustomWidgetRemoteCatalogItemDto? remoteDto))
            {
                if (remoteDto.Deprecated)
                {
                    continue;
                }

                bool remoteIsNewer = IsStrictlyNewerVersion(remoteDto.Version, builtInVersion);
                bool remoteRequiresUpgrade = CustomWidgetManifestParser.RequiresNewerClient(
                    remoteDto.MinSchemaVersion,
                    remoteDto.MinAppVersion,
                    out _);

                if (remoteIsNewer &&
                    !remoteRequiresUpgrade &&
                    TryBuildValidatedBundle(
                        remoteDto,
                        fallbackLocales: builtInBundle.Locales,
                        fallbackLogicJs: builtInBundle.LogicJs,
                        out string? remoteJson,
                        out string? remoteLogicJs,
                        out CustomWidgetLocaleCatalog? remoteLocales))
                {
                    merged.Add(new CustomWidgetCatalogEntry
                    {
                        Id = builtIn.Id,
                        ManifestId = $"deskbox.{normId}",
                        NameKey = builtIn.NameKey,
                        DescriptionKey = builtIn.DescriptionKey,
                        FeatureTagKey = builtIn.FeatureTagKey,
                        Name = remoteDto.Name,
                        NameEn = remoteDto.NameEn,
                        Names = remoteDto.Names,
                        Description = remoteDto.Description,
                        DescriptionEn = remoteDto.DescriptionEn,
                        Descriptions = remoteDto.Descriptions,
                        FeatureTag = remoteDto.FeatureTag,
                        FeatureTagEn = remoteDto.FeatureTagEn,
                        FeatureTags = remoteDto.FeatureTags,
                        DefaultTitle = !string.IsNullOrWhiteSpace(remoteDto.Name) ? remoteDto.Name : builtIn.DefaultTitle,
                        IconGlyph = !string.IsNullOrWhiteSpace(remoteDto.IconGlyph) ? remoteDto.IconGlyph : builtIn.IconGlyph,
                        BadgeColorHex = !string.IsNullOrWhiteSpace(remoteDto.BadgeColorHex) ? remoteDto.BadgeColorHex : builtIn.BadgeColorHex,
                        Version = remoteDto.Version,
                        MinSchemaVersion = remoteDto.MinSchemaVersion,
                        MinAppVersion = remoteDto.MinAppVersion,
                        DefaultWidth = remoteDto.DefaultWidth > 0 ? remoteDto.DefaultWidth : builtIn.DefaultWidth,
                        DefaultHeight = remoteDto.DefaultHeight > 0 ? remoteDto.DefaultHeight : builtIn.DefaultHeight,
                        ManifestJson = remoteJson,
                        LogicJs = remoteLogicJs,
                        Locales = remoteLocales,
                        IsRemoteOnly = false,
                        IsRemoteUpgraded = true
                    });
                    continue;
                }
            }

            merged.Add(new CustomWidgetCatalogEntry
            {
                Id = builtIn.Id,
                ManifestId = $"deskbox.{normId}",
                NameKey = builtIn.NameKey,
                DescriptionKey = builtIn.DescriptionKey,
                FeatureTagKey = builtIn.FeatureTagKey,
                DefaultTitle = builtIn.DefaultTitle,
                IconGlyph = builtIn.IconGlyph,
                BadgeColorHex = builtIn.BadgeColorHex,
                Version = builtInVersion,
                DefaultWidth = builtIn.DefaultWidth,
                DefaultHeight = builtIn.DefaultHeight,
                ManifestJson = builtInJson,
                LogicJs = builtInBundle.LogicJs,
                Locales = builtInBundle.Locales,
                IsRemoteOnly = false,
                IsRemoteUpgraded = false
            });
        }

        foreach (CustomWidgetStarterSpec starter in CustomWidgetTemplates.StudioStarters)
        {
            string normId = NormalizeTemplateId(starter.Id);
            if (!consumedIds.Add(normId))
            {
                continue;
            }

            CustomWidgetTemplateBundle starterBundle = CustomWidgetTemplates.GetTemplateBundle(starter.Id);
            string starterJson = starterBundle.ManifestJson;
            string starterVersion = ExtractManifestVersion(starterJson);
            CustomWidgetManifest? starterManifest = CustomWidgetManifestParser.Parse(starterJson).Manifest;
            if (starterManifest is not null && starterBundle.Locales is { IsEmpty: false })
            {
                starterManifest = starterManifest.WithLocales(starterBundle.Locales);
            }
            string manifestId = starterManifest?.Id ?? $"starter.{normId}";
            consumedIds.Add(manifestId);
            int defaultW = starterManifest?.DefaultSize.Width ?? 324;
            int defaultH = starterManifest?.DefaultSize.Height ?? 360;
            string titleZh = starterManifest?.ResolveLocalizedName("zh-CN") ?? starter.Id;
            string titleEn = starterManifest?.ResolveLocalizedName("en-US") ?? titleZh;
            string descZh = starterManifest?.ResolveLocalizedDescription("zh-CN") ?? string.Empty;
            string descEn = starterManifest?.ResolveLocalizedDescription("en-US") ?? descZh;

            if (remoteByNormalizedId.TryGetValue(normId, out CustomWidgetRemoteCatalogItemDto? remoteDto))
            {
                if (remoteDto.Deprecated)
                {
                    continue;
                }

                bool remoteIsNewer = IsStrictlyNewerVersion(remoteDto.Version, starterVersion);
                bool remoteRequiresUpgrade = CustomWidgetManifestParser.RequiresNewerClient(
                    remoteDto.MinSchemaVersion,
                    remoteDto.MinAppVersion,
                    out _);

                if (remoteIsNewer &&
                    !remoteRequiresUpgrade &&
                    TryBuildValidatedBundle(
                        remoteDto,
                        fallbackLocales: starterBundle.Locales,
                        fallbackLogicJs: starterBundle.LogicJs,
                        out string? remoteJson,
                        out string? remoteLogicJs,
                        out CustomWidgetLocaleCatalog? remoteLocales))
                {
                    merged.Add(new CustomWidgetCatalogEntry
                    {
                        Id = starter.Id,
                        ManifestId = !string.IsNullOrWhiteSpace(remoteDto.ManifestId) ? remoteDto.ManifestId! : manifestId,
                        NameKey = starter.LabelKey,
                        DescriptionKey = starter.DescriptionKey,
                        Name = remoteDto.Name,
                        NameEn = remoteDto.NameEn,
                        Names = remoteDto.Names,
                        Description = remoteDto.Description,
                        DescriptionEn = remoteDto.DescriptionEn,
                        Descriptions = remoteDto.Descriptions,
                        FeatureTag = remoteDto.FeatureTag,
                        FeatureTagEn = remoteDto.FeatureTagEn,
                        FeatureTags = remoteDto.FeatureTags,
                        DefaultTitle = !string.IsNullOrWhiteSpace(remoteDto.Name) ? remoteDto.Name : titleZh,
                        IconGlyph = !string.IsNullOrWhiteSpace(remoteDto.IconGlyph) ? remoteDto.IconGlyph : starter.Emoji,
                        BadgeColorHex = !string.IsNullOrWhiteSpace(remoteDto.BadgeColorHex) ? remoteDto.BadgeColorHex : "#3B82F6",
                        Version = remoteDto.Version,
                        MinSchemaVersion = remoteDto.MinSchemaVersion,
                        MinAppVersion = remoteDto.MinAppVersion,
                        DefaultWidth = remoteDto.DefaultWidth > 0 ? remoteDto.DefaultWidth : defaultW,
                        DefaultHeight = remoteDto.DefaultHeight > 0 ? remoteDto.DefaultHeight : defaultH,
                        ManifestJson = remoteJson,
                        LogicJs = remoteLogicJs,
                        Locales = remoteLocales,
                        IsRemoteOnly = false,
                        IsRemoteUpgraded = true
                    });
                    continue;
                }
            }

            merged.Add(new CustomWidgetCatalogEntry
            {
                Id = starter.Id,
                ManifestId = manifestId,
                NameKey = starter.LabelKey,
                DescriptionKey = starter.DescriptionKey,
                Name = titleZh,
                NameEn = titleEn,
                Description = descZh,
                DescriptionEn = descEn,
                DefaultTitle = titleZh,
                IconGlyph = starter.Emoji,
                BadgeColorHex = "#3B82F6",
                Version = starterVersion,
                DefaultWidth = defaultW,
                DefaultHeight = defaultH,
                ManifestJson = starterJson,
                LogicJs = starterBundle.LogicJs,
                Locales = starterBundle.Locales,
                IsRemoteOnly = false,
                IsRemoteUpgraded = false
            });
        }

        foreach (CustomWidgetRemoteCatalogItemDto remoteDto in remoteItems)
        {
            string normId = NormalizeTemplateId(remoteDto.Id);
            if (string.IsNullOrWhiteSpace(normId) ||
                consumedIds.Contains(normId) ||
                remoteDto.Deprecated)
            {
                continue;
            }

            bool requiresUpgrade = CustomWidgetManifestParser.RequiresNewerClient(
                remoteDto.MinSchemaVersion,
                remoteDto.MinAppVersion,
                out _);
            string? remoteJson = null;
            string? remoteLogicJs = null;
            CustomWidgetLocaleCatalog? remoteLocales = null;
            bool hasValidBundle = !requiresUpgrade && TryBuildValidatedBundle(
                remoteDto,
                fallbackLocales: null,
                fallbackLogicJs: null,
                out remoteJson,
                out remoteLogicJs,
                out remoteLocales);

            // Only show a remote-only entry if it either has a valid full bundle or explicitly flags a client upgrade
            if (!requiresUpgrade && !hasValidBundle)
            {
                continue;
            }

            if (requiresUpgrade)
            {
                remoteJson = ExtractInlineOrCachedManifestJson(remoteDto);
            }

            consumedIds.Add(normId);
            merged.Add(new CustomWidgetCatalogEntry
            {
                Id = normId,
                ManifestId = !string.IsNullOrWhiteSpace(remoteDto.ManifestId)
                    ? remoteDto.ManifestId!
                    : $"deskbox.{normId}",
                Name = remoteDto.Name,
                NameEn = remoteDto.NameEn,
                Names = remoteDto.Names,
                Description = remoteDto.Description,
                DescriptionEn = remoteDto.DescriptionEn,
                Descriptions = remoteDto.Descriptions,
                FeatureTag = remoteDto.FeatureTag,
                FeatureTagEn = remoteDto.FeatureTagEn,
                FeatureTags = remoteDto.FeatureTags,
                DefaultTitle = !string.IsNullOrWhiteSpace(remoteDto.Name) ? remoteDto.Name : normId,
                IconGlyph = CustomWidgetManifestParser.NormalizeGlyph(
                    string.IsNullOrWhiteSpace(remoteDto.IconGlyph) ? "\uE71D" : remoteDto.IconGlyph),
                BadgeColorHex = string.IsNullOrWhiteSpace(remoteDto.BadgeColorHex) ? "#3B82F6" : remoteDto.BadgeColorHex,
                Version = string.IsNullOrWhiteSpace(remoteDto.Version) ? "1.0.0" : remoteDto.Version,
                MinSchemaVersion = remoteDto.MinSchemaVersion,
                MinAppVersion = remoteDto.MinAppVersion,
                DefaultWidth = remoteDto.DefaultWidth > 0 ? remoteDto.DefaultWidth : 324,
                DefaultHeight = remoteDto.DefaultHeight > 0 ? remoteDto.DefaultHeight : 360,
                ManifestJson = remoteJson,
                LogicJs = remoteLogicJs,
                Locales = remoteLocales,
                IsRemoteOnly = true,
                IsRemoteUpgraded = false
            });
        }

        return merged;
    }

    public bool TryGetRemoteTemplateBundle(string? templateId, out CustomWidgetTemplateBundle? bundle)
    {
        bundle = null;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return false;
        }

        string normId = NormalizeTemplateId(templateId);
        foreach (CustomWidgetCatalogEntry entry in GetMergedOfficialTemplates())
        {
            if (!string.Equals(NormalizeTemplateId(entry.Id), normId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.IsRemoteOnly || entry.IsRemoteUpgraded) &&
                !entry.RequiresClientUpgrade &&
                !string.IsNullOrWhiteSpace(entry.ManifestJson))
            {
                bundle = new CustomWidgetTemplateBundle(
                    entry.Id,
                    entry.ManifestJson,
                    entry.Locales,
                    entry.LogicJs);
                return true;
            }

            return false;
        }

        return false;
    }

    public bool TryGetRemoteTemplateJson(string? templateId, out string? manifestJson)
    {
        if (TryGetRemoteTemplateBundle(templateId, out CustomWidgetTemplateBundle? bundle) && bundle is not null)
        {
            manifestJson = bundle.ManifestJson;
            return true;
        }

        manifestJson = null;
        return false;
    }

    public bool TryResolveNewerOfficialBundle(string existingManifestJson, out CustomWidgetTemplateBundle? upgradedBundle)
    {
        upgradedBundle = null;
        if (string.IsNullOrWhiteSpace(existingManifestJson))
        {
            return false;
        }

        string existingId;
        string existingVersion;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                existingManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            existingId = doc.RootElement.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;
            existingVersion = doc.RootElement.TryGetProperty("version", out JsonElement verEl) && verEl.ValueKind == JsonValueKind.String
                ? verEl.GetString() ?? "1.0.0"
                : "1.0.0";
        }
        catch
        {
            return false;
        }

        if (!existingId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) &&
            !existingId.StartsWith("starter.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normId = NormalizeTemplateId(existingId);
        IReadOnlyList<CustomWidgetRemoteCatalogItemDto> remoteItems = EnsureSnapshotLoaded();
        CustomWidgetRemoteCatalogItemDto? match = remoteItems.FirstOrDefault(item =>
            string.Equals(NormalizeTemplateId(item.Id), normId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeTemplateId(item.ManifestId), normId, StringComparison.OrdinalIgnoreCase));
        if (match is null ||
            match.Deprecated ||
            CustomWidgetManifestParser.RequiresNewerClient(match.MinSchemaVersion, match.MinAppVersion, out _))
        {
            return false;
        }

        if (!IsStrictlyNewerVersion(match.Version, existingVersion))
        {
            return false;
        }

        CustomWidgetTemplateBundle? fallbackBuiltIn = null;
        if (CustomWidgetTemplates.TryNormalizeKnownTemplateId(existingId) is { } knownBuiltInId)
        {
            fallbackBuiltIn = CustomWidgetTemplates.GetTemplateBundle(knownBuiltInId);
        }

        if (!TryBuildValidatedBundle(
                match,
                fallbackLocales: fallbackBuiltIn?.Locales,
                fallbackLogicJs: fallbackBuiltIn?.LogicJs,
                out string? candidateJson,
                out string? candidateLogicJs,
                out CustomWidgetLocaleCatalog? candidateLocales) ||
            string.IsNullOrWhiteSpace(candidateJson))
        {
            return false;
        }

        upgradedBundle = new CustomWidgetTemplateBundle(
            normId,
            candidateJson,
            candidateLocales,
            candidateLogicJs);
        return true;
    }

    public bool TryResolveNewerOfficialManifestJson(string existingManifestJson, out string? upgradedJson)
    {
        if (TryResolveNewerOfficialBundle(existingManifestJson, out CustomWidgetTemplateBundle? bundle) &&
            bundle is not null)
        {
            upgradedJson = bundle.ManifestJson;
            return true;
        }

        upgradedJson = null;
        return false;
    }

    public DateTimeOffset? GetLastCheckedUtc()
    {
        long ticks = ReadCacheMeta().LastCheckedUtcTicks;
        return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    public async Task<bool> RefreshInBackgroundAsync(bool force = false, CancellationToken ct = default)
    {
        CustomWidgetCatalogSyncResult result = await SyncAsync(force, ct).ConfigureAwait(false);
        return result.AppliedCatalogResponse;
    }

    public async Task<CustomWidgetCatalogSyncResult> SyncAsync(bool force = false, CancellationToken ct = default)
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (!force)
        {
            long prevAttempt = Interlocked.Read(ref _lastRefreshAttemptUtcTicks);
            if (prevAttempt > 0 && nowTicks - prevAttempt < RefreshMinInterval.Ticks)
            {
                return new CustomWidgetCatalogSyncResult(
                    CustomWidgetCatalogSyncStatus.Throttled,
                    ChangedCount: 0,
                    TotalOfficialCount: GetMergedOfficialTemplates().Count,
                    LastCheckedUtc: GetLastCheckedUtc());
            }

            if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return new CustomWidgetCatalogSyncResult(
                    CustomWidgetCatalogSyncStatus.Throttled,
                    ChangedCount: 0,
                    TotalOfficialCount: GetMergedOfficialTemplates().Count,
                    LastCheckedUtc: GetLastCheckedUtc());
            }
        }
        else
        {
            await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            Interlocked.Exchange(ref _lastRefreshAttemptUtcTicks, nowTicks);
            CustomWidgetRemoteCatalogCacheMeta meta = ReadCacheMeta();

            if (!force &&
                meta.LastCheckedUtcTicks > 0 &&
                nowTicks - meta.LastCheckedUtcTicks < RefreshMinInterval.Ticks &&
                File.Exists(_catalogCachePath))
            {
                return new CustomWidgetCatalogSyncResult(
                    CustomWidgetCatalogSyncStatus.Throttled,
                    ChangedCount: 0,
                    TotalOfficialCount: GetMergedOfficialTemplates().Count,
                    LastCheckedUtc: GetLastCheckedUtc());
            }

            Dictionary<string, string> beforeFingerprints = BuildMergedTemplateFingerprints();
            string[] candidateUrls = [PrimaryCatalogUrl, MirrorCatalogUrl];
            foreach (string url in candidateUrls)
            {
                try
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpointUri))
                    {
                        continue;
                    }

                    using HttpResponseMessage resp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                        _httpClient,
                        endpointUri,
                        requireTrustedOfficialHost: true,
                        configureRequest: req =>
                        {
                            if (force)
                            {
                                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                            }
                            else if (!string.IsNullOrWhiteSpace(meta.ETag) &&
                                     string.Equals(meta.SourceUrl, url, StringComparison.OrdinalIgnoreCase))
                            {
                                req.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
                            }
                        },
                        cancellationToken: ct).ConfigureAwait(false);

                    if (resp.StatusCode == HttpStatusCode.NotModified)
                    {
                        WriteCacheMeta(new CustomWidgetRemoteCatalogCacheMeta
                        {
                            ETag = meta.ETag,
                            SourceUrl = url,
                            LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                        });
                        return new CustomWidgetCatalogSyncResult(
                            CustomWidgetCatalogSyncStatus.UpToDate,
                            ChangedCount: 0,
                            TotalOfficialCount: beforeFingerprints.Count,
                            LastCheckedUtc: GetLastCheckedUtc(),
                            AppliedCatalogResponse: false);
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    string? json = await ReadBoundedUtf8StringAsync(resp.Content, MaxRemoteManifestBytes, ct)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    string? etag = resp.Headers.ETag?.Tag;
                    (bool applied, int changedCount, int totalAfter) = await ApplyCatalogJsonCoreAsync(
                        json,
                        etag,
                        url,
                        beforeFingerprints,
                        ct).ConfigureAwait(false);
                    if (applied)
                    {
                        return new CustomWidgetCatalogSyncResult(
                            changedCount > 0 ? CustomWidgetCatalogSyncStatus.Updated : CustomWidgetCatalogSyncStatus.UpToDate,
                            ChangedCount: changedCount,
                            TotalOfficialCount: totalAfter,
                            LastCheckedUtc: GetLastCheckedUtc(),
                            AppliedCatalogResponse: true);
                    }
                }
                catch
                {
                    // Silent fallback to mirror / built-in templates when offline or endpoint not yet live
                }
            }

            return new CustomWidgetCatalogSyncResult(
                CustomWidgetCatalogSyncStatus.OfflineOrFailed,
                ChangedCount: 0,
                TotalOfficialCount: beforeFingerprints.Count,
                LastCheckedUtc: GetLastCheckedUtc(),
                AppliedCatalogResponse: false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal async Task<bool> ApplyCatalogJsonAsync(
        string catalogJson,
        string? etag = null,
        string? sourceUrl = null,
        CancellationToken ct = default)
    {
        Dictionary<string, string> beforeFingerprints = BuildMergedTemplateFingerprints();
        (bool applied, _, _) = await ApplyCatalogJsonCoreAsync(
            catalogJson,
            etag,
            sourceUrl,
            beforeFingerprints,
            ct).ConfigureAwait(false);
        return applied;
    }

    private async Task<(bool Applied, int ChangedCount, int TotalOfficialCount)> ApplyCatalogJsonCoreAsync(
        string catalogJson,
        string? etag,
        string? sourceUrl,
        Dictionary<string, string> beforeFingerprints,
        CancellationToken ct)
    {
        CustomWidgetRemoteCatalogDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(
                catalogJson,
                CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogDocument);
        }
        catch
        {
            return (false, 0, beforeFingerprints.Count);
        }

        if (doc?.Widgets is null)
        {
            return (false, 0, beforeFingerprints.Count);
        }

        bool anySubArtifactFetchFailed = false;
        var persistedBundleItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetRemoteCatalogItemDto item in doc.Widgets)
        {
            if (string.IsNullOrWhiteSpace(item.ManifestJson) &&
                item.Manifest is { ValueKind: JsonValueKind.Object } inlineElement)
            {
                item.ManifestJson = inlineElement.GetRawText();
                item.Manifest = null;
            }

            bool requiresUpgrade = CustomWidgetManifestParser.RequiresNewerClient(
                item.MinSchemaVersion,
                item.MinAppVersion,
                out _);

            if (!item.Deprecated &&
                !requiresUpgrade &&
                !string.IsNullOrWhiteSpace(item.BundleUrl) &&
                CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(item.BundleUrl, out Uri? bundleUri, out _) &&
                bundleUri is not null &&
                CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(bundleUri))
            {
                bool bundleLoaded = false;
                try
                {
                    using HttpResponseMessage bundleResp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                        _httpClient,
                        bundleUri,
                        requireTrustedOfficialHost: true,
                        cancellationToken: ct).ConfigureAwait(false);
                    if (bundleResp.IsSuccessStatusCode)
                    {
                        byte[]? bundleBytes = await ReadBoundedBytesAsync(
                            bundleResp.Content,
                            MaxRemoteBundleBytes,
                            ct).ConfigureAwait(false);
                        if (bundleBytes is { Length: > 0 })
                        {
                            CustomWidgetImportedBundle imported = await CustomWidgetPackageService
                                .ReadPackageBundleFromBytesAsync(bundleBytes, markExternalAsCommunity: false)
                                .ConfigureAwait(false);
                            item.ManifestJson = imported.RawJson;
                            item.LogicJs = imported.LogicJs;
                            item.Locales = imported.Locales is { HasAnyLocale: true }
                                ? ToDtoLocalesMap(imported.Locales)
                                : null;
                            item.BaseLocale = imported.Locales?.Meta?.BaseLocale;
                            bundleLoaded = true;
                        }
                    }
                }
                catch
                {
                    // Individual bundle fetch or validation failure rejects only that bundle
                }

                if (!bundleLoaded && string.IsNullOrWhiteSpace(item.ManifestJson) && string.IsNullOrWhiteSpace(item.ManifestUrl))
                {
                    anySubArtifactFetchFailed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(item.ManifestJson) &&
                !item.Deprecated &&
                !requiresUpgrade &&
                !string.IsNullOrWhiteSpace(item.ManifestUrl) &&
                CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(item.ManifestUrl, out Uri? manifestUri, out _) &&
                manifestUri is not null &&
                CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(manifestUri))
            {
                bool manifestLoaded = false;
                try
                {
                    using HttpResponseMessage manifestResp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                        _httpClient,
                        manifestUri,
                        requireTrustedOfficialHost: true,
                        cancellationToken: ct).ConfigureAwait(false);
                    if (manifestResp.IsSuccessStatusCode)
                    {
                        string? fetchedJson = await ReadBoundedUtf8StringAsync(
                            manifestResp.Content,
                            MaxRemoteManifestBytes,
                            ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(fetchedJson) && IsValidManifestJson(fetchedJson))
                        {
                            item.ManifestJson = fetchedJson;
                            manifestLoaded = true;
                        }
                    }
                }
                catch
                {
                    // Individual manifest fetch failure does not fail the rest of the catalog
                }

                if (!manifestLoaded)
                {
                    anySubArtifactFetchFailed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(item.LogicJs) &&
                !item.Deprecated &&
                !requiresUpgrade &&
                !string.IsNullOrWhiteSpace(item.LogicUrl) &&
                CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(item.LogicUrl, out Uri? logicUri, out _) &&
                logicUri is not null &&
                CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(logicUri))
            {
                bool logicLoaded = false;
                try
                {
                    using HttpResponseMessage logicResp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                        _httpClient,
                        logicUri,
                        requireTrustedOfficialHost: true,
                        cancellationToken: ct).ConfigureAwait(false);
                    if (logicResp.IsSuccessStatusCode)
                    {
                        string? fetchedLogic = await ReadBoundedUtf8StringAsync(
                            logicResp.Content,
                            MaxRemoteLogicJsBytes,
                            ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(fetchedLogic))
                        {
                            item.LogicJs = fetchedLogic;
                            logicLoaded = true;
                        }
                    }
                }
                catch
                {
                    // Individual logic.js fetch failure does not fail the rest of the catalog
                }

                if (!logicLoaded)
                {
                    anySubArtifactFetchFailed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.ManifestJson))
            {
                CustomWidgetLocaleCatalog? initialLocales = BuildCatalogFromDto(item.Locales, item.BaseLocale);
                string upgradedManifest = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
                    item.ManifestJson,
                    item.LogicJs,
                    initialLocales,
                    out string? extractedLogic,
                    out CustomWidgetLocaleCatalog? extractedLocales);
                if (!string.IsNullOrWhiteSpace(extractedLogic))
                {
                    upgradedManifest = CustomWidgetTemplates.SyncManifestScriptPropertyJson(upgradedManifest, extractedLogic);
                    item.LogicJs = extractedLogic;
                }
                else
                {
                    item.LogicJs = null;
                }

                CustomWidgetWorkspaceValidationResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                    upgradedManifest,
                    item.LogicJs,
                    extractedLocales,
                    runOnInitDryRun: true);
                if (!validation.IsValid)
                {
                    item.ManifestJson = null;
                    item.LogicJs = null;
                    item.Locales = null;
                    continue;
                }

                item.ManifestJson = upgradedManifest;
                if (extractedLocales is { HasAnyLocale: true })
                {
                    item.BaseLocale ??= extractedLocales.Meta?.BaseLocale;
                    item.Locales = ToDtoLocalesMap(extractedLocales);
                }
                else
                {
                    item.Locales = null;
                }

                if (PersistBundleArtifactCache(item, extractedLocales))
                {
                    persistedBundleItemIds.Add(item.Id);
                }
            }
        }

        Directory.CreateDirectory(_cacheDirectory);
        var leanDoc = new CustomWidgetRemoteCatalogDocument
        {
            SchemaVersion = doc.SchemaVersion,
            UpdatedAtUtc = doc.UpdatedAtUtc,
            Widgets = doc.Widgets.Select(w => persistedBundleItemIds.Contains(w.Id)
                ? CloneWithoutHydratedArtifacts(w)
                : w).ToList()
        };
        string normalizedCatalogJson = JsonSerializer.Serialize(
            leanDoc,
            CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogDocument);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_catalogCachePath, normalizedCatalogJson);
        WriteCacheMeta(new CustomWidgetRemoteCatalogCacheMeta
        {
            ETag = anySubArtifactFetchFailed ? null : etag,
            SourceUrl = sourceUrl ?? PrimaryCatalogUrl,
            LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        });

        lock (_snapshotLock)
        {
            _cachedRemoteItems = doc.Widgets;
        }

        Dictionary<string, string> afterFingerprints = BuildMergedTemplateFingerprints();
        int changedCount = CountFingerprintDiffs(beforeFingerprints, afterFingerprints);
        if (changedCount > 0)
        {
            CatalogUpdated?.Invoke(this, EventArgs.Empty);
        }

        return (true, changedCount, afterFingerprints.Count);
    }

    private Dictionary<string, string> BuildMergedTemplateFingerprints()
    {
        IReadOnlyList<CustomWidgetCatalogEntry> entries = GetMergedOfficialTemplates();
        var map = new Dictionary<string, string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetCatalogEntry entry in entries)
        {
            var sb = new StringBuilder(256);
            sb.Append(entry.Version).Append('|')
              .Append(entry.IsRemoteOnly ? '1' : '0').Append('|')
              .Append(entry.IsRemoteUpgraded ? '1' : '0').Append('|')
              .Append(entry.RequiresClientUpgrade ? '1' : '0').Append('|')
              .Append(entry.IconGlyph).Append('|')
              .Append(entry.DefaultTitle).Append('|')
              .Append(entry.Name).Append('|')
              .Append(entry.NameEn).Append('|')
              .Append(entry.Description).Append('|')
              .Append(entry.DescriptionEn).Append('|')
              .Append(entry.ManifestJson ?? string.Empty).Append('|')
              .Append(entry.LogicJs ?? string.Empty);
            if (entry.Locales is { HasAnyLocale: true })
            {
                foreach ((string culture, IReadOnlyDictionary<string, string> dict) in entry.Locales.Locales
                             .OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append('|').Append(culture).Append(':').Append(dict.Count);
                    foreach ((string k, string v) in dict.OrderBy(static p => p.Key, StringComparer.Ordinal))
                    {
                        sb.Append(';').Append(k).Append('=').Append(v);
                    }
                }
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            map[NormalizeTemplateId(entry.Id)] = Convert.ToHexString(hash);
        }

        return map;
    }

    private static int CountFingerprintDiffs(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        int diffs = 0;
        foreach ((string key, string afterHash) in after)
        {
            if (!before.TryGetValue(key, out string? beforeHash) ||
                !string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
            {
                diffs++;
            }
        }

        foreach (string key in before.Keys)
        {
            if (!after.ContainsKey(key))
            {
                diffs++;
            }
        }

        return diffs;
    }

    private IReadOnlyList<CustomWidgetRemoteCatalogItemDto> EnsureSnapshotLoaded()
    {
        lock (_snapshotLock)
        {
            if (_cachedRemoteItems is not null)
            {
                return _cachedRemoteItems;
            }
        }

        List<CustomWidgetRemoteCatalogItemDto> loaded = [];
        try
        {
            if (File.Exists(_catalogCachePath))
            {
                string json = File.ReadAllText(_catalogCachePath, Encoding.UTF8);
                CustomWidgetRemoteCatalogDocument? doc = JsonSerializer.Deserialize(
                    json,
                    CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogDocument);
                if (doc?.Widgets is not null)
                {
                    foreach (CustomWidgetRemoteCatalogItemDto item in doc.Widgets)
                    {
                        if (string.IsNullOrWhiteSpace(item.ManifestJson) &&
                            item.Manifest is { ValueKind: JsonValueKind.Object } el)
                        {
                            item.ManifestJson = el.GetRawText();
                        }

                        HydrateFromBundleArtifactCache(item);
                    }

                    loaded = doc.Widgets;
                }
            }
        }
        catch
        {
            loaded = [];
        }

        lock (_snapshotLock)
        {
            _cachedRemoteItems ??= loaded;
            return _cachedRemoteItems;
        }
    }

    private CustomWidgetRemoteCatalogCacheMeta ReadCacheMeta()
    {
        try
        {
            if (File.Exists(_metaCachePath))
            {
                string json = File.ReadAllText(_metaCachePath, Encoding.UTF8);
                return JsonSerializer.Deserialize(
                           json,
                           CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogCacheMeta)
                       ?? new CustomWidgetRemoteCatalogCacheMeta();
            }
        }
        catch
        {
            // Ignore malformed cache metadata
        }

        return new CustomWidgetRemoteCatalogCacheMeta();
    }

    private void WriteCacheMeta(CustomWidgetRemoteCatalogCacheMeta meta)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string json = JsonSerializer.Serialize(
                meta,
                CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogCacheMeta);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_metaCachePath, json);
        }
        catch
        {
            // Ignore non-fatal cache metadata write failure
        }
    }

    private bool PersistBundleArtifactCache(CustomWidgetRemoteCatalogItemDto item, CustomWidgetLocaleCatalog? locales)
    {
        try
        {
            string safeKey = SanitizeCacheKey(NormalizeTemplateId(item.Id));
            if (string.IsNullOrWhiteSpace(safeKey) || string.IsNullOrWhiteSpace(item.ManifestJson))
            {
                return false;
            }

            string bundleDir = Path.Combine(_bundlesCacheDirectory, safeKey);
            Directory.CreateDirectory(bundleDir);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
                Path.Combine(bundleDir, CustomWidgetDataStore.ManifestFileName),
                item.ManifestJson);

            string logicPath = Path.Combine(bundleDir, CustomWidgetDataStore.SidecarLogicFileName);
            if (!string.IsNullOrWhiteSpace(item.LogicJs))
            {
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(logicPath, item.LogicJs);
            }
            else
            {
                CustomWidgetStorageFileOps.DeleteFileIfExists(logicPath);
            }

            CustomWidgetLocaleCatalog.WriteOrDeleteDirectoryAtomically(
                locales,
                Path.Combine(bundleDir, CustomWidgetDataStore.LocalesDirectoryName));
            return true;
        }
        catch
        {
            // Ignore non-fatal bundle artifact cache persistence errors
            return false;
        }
    }

    private static CustomWidgetRemoteCatalogItemDto CloneWithoutHydratedArtifacts(CustomWidgetRemoteCatalogItemDto src) =>
        new()
        {
            Id = src.Id,
            ManifestId = src.ManifestId,
            Name = src.Name,
            NameEn = src.NameEn,
            Names = src.Names,
            Description = src.Description,
            DescriptionEn = src.DescriptionEn,
            Descriptions = src.Descriptions,
            FeatureTag = src.FeatureTag,
            FeatureTagEn = src.FeatureTagEn,
            FeatureTags = src.FeatureTags,
            IconGlyph = src.IconGlyph,
            BadgeColorHex = src.BadgeColorHex,
            Version = src.Version,
            MinSchemaVersion = src.MinSchemaVersion,
            MinAppVersion = src.MinAppVersion,
            Deprecated = src.Deprecated,
            DefaultWidth = src.DefaultWidth,
            DefaultHeight = src.DefaultHeight,
            BundleUrl = src.BundleUrl,
            ManifestUrl = src.ManifestUrl,
            LogicUrl = src.LogicUrl,
            BaseLocale = src.BaseLocale
        };

    private void HydrateFromBundleArtifactCache(CustomWidgetRemoteCatalogItemDto item)
    {
        try
        {
            string safeKey = SanitizeCacheKey(NormalizeTemplateId(item.Id));
            if (string.IsNullOrWhiteSpace(safeKey))
            {
                return;
            }

            string bundleDir = Path.Combine(_bundlesCacheDirectory, safeKey);
            if (!Directory.Exists(bundleDir))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(item.ManifestJson))
            {
                string manifestPath = Path.Combine(bundleDir, CustomWidgetDataStore.ManifestFileName);
                if (File.Exists(manifestPath))
                {
                    item.ManifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                }
            }

            if (string.IsNullOrWhiteSpace(item.LogicJs))
            {
                string logicPath = Path.Combine(bundleDir, CustomWidgetDataStore.SidecarLogicFileName);
                if (File.Exists(logicPath))
                {
                    item.LogicJs = File.ReadAllText(logicPath, Encoding.UTF8);
                }
            }

            if (item.Locales is not { Count: > 0 })
            {
                string localesDir = Path.Combine(bundleDir, CustomWidgetDataStore.LocalesDirectoryName);
                if (Directory.Exists(localesDir))
                {
                    CustomWidgetLocaleCatalog diskCatalog = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);
                    if (diskCatalog.HasAnyLocale)
                    {
                        item.BaseLocale ??= diskCatalog.Meta?.BaseLocale;
                        item.Locales = ToDtoLocalesMap(diskCatalog);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed disk bundle cache
        }
    }

    private static string SanitizeCacheKey(string rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(rawId.Length);
        foreach (char ch in rawId)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString().Trim('.');
    }

    private static CustomWidgetLocaleCatalog? BuildCatalogFromDto(
        Dictionary<string, Dictionary<string, string>>? dtoLocales,
        string? baseLocale)
    {
        if (dtoLocales is not { Count: > 0 })
        {
            return null;
        }

        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string culture, Dictionary<string, string>? entries) in dtoLocales)
        {
            if (!string.IsNullOrWhiteSpace(culture) && entries is { Count: > 0 })
            {
                map[culture] = entries;
            }
        }

        if (map.Count == 0)
        {
            return null;
        }

        string effectiveBase = string.IsNullOrWhiteSpace(baseLocale)
            ? (map.ContainsKey("zh-CN") ? "zh-CN" : map.Keys.First())
            : baseLocale;
        IReadOnlyDictionary<string, string> baseEntries = map.TryGetValue(effectiveBase, out IReadOnlyDictionary<string, string>? foundBase)
            ? foundBase
            : map.Values.First();
        CustomWidgetLocaleMeta meta = CustomWidgetLocaleMeta.BuildFromBaseDictionary(effectiveBase, baseEntries);
        CustomWidgetLocaleCatalog catalog = new(meta, map);
        return catalog.HasAnyLocale ? catalog : null;
    }

    private static Dictionary<string, Dictionary<string, string>>? ToDtoLocalesMap(CustomWidgetLocaleCatalog? catalog)
    {
        if (catalog is not { HasAnyLocale: true })
        {
            return null;
        }

        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string culture, IReadOnlyDictionary<string, string> entries) in catalog.Locales)
        {
            if (entries.Count == 0)
            {
                continue;
            }

            var inner = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
            foreach ((string k, string v) in entries)
            {
                inner[k] = v;
            }

            result[culture] = inner;
        }

        return result.Count > 0 ? result : null;
    }

    private static bool TryBuildValidatedBundle(
        CustomWidgetRemoteCatalogItemDto dto,
        CustomWidgetLocaleCatalog? fallbackLocales,
        string? fallbackLogicJs,
        out string? manifestJson,
        out string? logicJs,
        out CustomWidgetLocaleCatalog? locales)
    {
        manifestJson = ExtractInlineOrCachedManifestJson(dto);
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            logicJs = null;
            locales = null;
            return false;
        }

        bool manifestDeclaresScript = ManifestDeclaresScript(manifestJson);
        logicJs = !string.IsNullOrWhiteSpace(dto.LogicJs)
            ? dto.LogicJs
            : (manifestDeclaresScript ? fallbackLogicJs : null);
        locales = BuildCatalogFromDto(dto.Locales, dto.BaseLocale) ?? fallbackLocales;

        string normalizedManifest = CustomWidgetTemplates.UpgradeLegacyInlineBundle(
            manifestJson,
            logicJs,
            locales,
            out string? resolvedLogic,
            out CustomWidgetLocaleCatalog? resolvedLocales);
        if (!string.IsNullOrWhiteSpace(resolvedLogic))
        {
            normalizedManifest = CustomWidgetTemplates.SyncManifestScriptPropertyJson(normalizedManifest, resolvedLogic);
        }

        CustomWidgetWorkspaceValidationResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            normalizedManifest,
            resolvedLogic,
            resolvedLocales,
            runOnInitDryRun: true);
        if (!validation.IsValid)
        {
            return false;
        }

        manifestJson = normalizedManifest;
        logicJs = resolvedLogic;
        locales = resolvedLocales;
        return true;
    }

    private static bool ManifestDeclaresScript(string rawManifestJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return doc.RootElement.TryGetProperty("script", out JsonElement scriptEl) &&
                   scriptEl.ValueKind is JsonValueKind.Object or JsonValueKind.String;
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractInlineOrCachedManifestJson(CustomWidgetRemoteCatalogItemDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.ManifestJson))
        {
            return dto.ManifestJson;
        }

        if (dto.Manifest is { ValueKind: JsonValueKind.Object } el)
        {
            return el.GetRawText();
        }

        return null;
    }

    private static bool IsValidManifestJson(string json)
    {
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(json);
        return parsed.Manifest is not null && parsed.Diagnostics.All(d => !d.IsError);
    }

    private static string NormalizeTemplateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        string trimmed = id.Trim();
        if (trimmed.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["deskbox.".Length..];
        }

        if (trimmed.StartsWith("starter.", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["starter.".Length..];
        }

        return trimmed;
    }

    private static string ExtractManifestVersion(string rawManifestJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("version", out JsonElement verEl) &&
                verEl.ValueKind == JsonValueKind.String)
            {
                return verEl.GetString() ?? "1.0.0";
            }
        }
        catch
        {
            // Fall through
        }

        return "1.0.0";
    }

    private static bool IsStrictlyNewerVersion(string? candidateVersion, string? baselineVersion)
    {
        if (!CustomWidgetManifestParser.TryParseLooseVersion(candidateVersion, out Version candidate))
        {
            return false;
        }

        if (!CustomWidgetManifestParser.TryParseLooseVersion(baselineVersion, out Version baseline))
        {
            return true;
        }

        return candidate > baseline;
    }

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > maxBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static async Task<string?> ReadBoundedUtf8StringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        byte[]? bytes = await ReadBoundedBytesAsync(content, maxBytes, ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }
}
