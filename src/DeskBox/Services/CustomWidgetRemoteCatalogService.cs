using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetRemoteCatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string UpdatedAtUtc { get; set; } = string.Empty;
    public List<CustomWidgetRemoteCatalogItemDto> Widgets { get; set; } = [];
}

public sealed class CustomWidgetRemoteCatalogItemDto
{
    public string Id { get; set; } = string.Empty;
    public string? ManifestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DescriptionEn { get; set; }
    public string? FeatureTag { get; set; }
    public string? FeatureTagEn { get; set; }
    public string IconGlyph { get; set; } = "\uE71D";
    public string BadgeColorHex { get; set; } = "#3B82F6";
    public string Version { get; set; } = "1.0.0";
    public int? MinSchemaVersion { get; set; }
    public string? MinAppVersion { get; set; }
    public bool Deprecated { get; set; }
    public int DefaultWidth { get; set; } = 324;
    public int DefaultHeight { get; set; } = 360;
    public string? ManifestUrl { get; set; }
    public JsonElement? Manifest { get; set; }
    public string? ManifestJson { get; set; }
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
    public string Description { get; init; } = string.Empty;
    public string? DescriptionEn { get; init; }
    public string? FeatureTag { get; init; }
    public string? FeatureTagEn { get; init; }
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
        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(NameKey) && localization is not null)
        {
            string localized = localization.T(NameKey);
            if (!string.Equals(localized, NameKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(NameEn))
        {
            return NameEn;
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
        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(DescriptionKey) && localization is not null)
        {
            string localized = localization.T(DescriptionKey);
            if (!string.Equals(localized, DescriptionKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(DescriptionEn))
        {
            return DescriptionEn;
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
        bool isChinese = activeCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!IsRemoteOnly && !IsRemoteUpgraded && !string.IsNullOrWhiteSpace(FeatureTagKey) && localization is not null)
        {
            string localized = localization.T(FeatureTagKey);
            if (!string.IsNullOrWhiteSpace(localized) && !string.Equals(localized, FeatureTagKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        if (!isChinese && !string.IsNullOrWhiteSpace(FeatureTagEn))
        {
            return FeatureTagEn;
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
}

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

    public static CustomWidgetRemoteCatalogService Shared { get; } = new();

    private readonly string _cacheDirectory;
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
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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
        var consumedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetPresetInfo builtIn in CustomWidgetTemplates.Presets)
        {
            string normId = NormalizeTemplateId(builtIn.Id);
            consumedIds.Add(normId);

            string builtInJson = CustomWidgetTemplates.GetTemplateJson(builtIn.Id);
            string builtInVersion = ExtractManifestVersion(builtInJson);

            if (remoteByNormalizedId.TryGetValue(normId, out CustomWidgetRemoteCatalogItemDto? remoteDto))
            {
                if (remoteDto.Deprecated)
                {
                    continue;
                }

                string? remoteJson = ExtractInlineOrCachedManifestJson(remoteDto);
                bool remoteIsNewer = IsStrictlyNewerVersion(remoteDto.Version, builtInVersion);
                bool remoteRequiresUpgrade = CustomWidgetManifestParser.RequiresNewerClient(
                    remoteDto.MinSchemaVersion,
                    remoteDto.MinAppVersion,
                    out _);

                if (remoteIsNewer &&
                    !remoteRequiresUpgrade &&
                    !string.IsNullOrWhiteSpace(remoteJson) &&
                    IsValidManifestJson(remoteJson))
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
                        Description = remoteDto.Description,
                        DescriptionEn = remoteDto.DescriptionEn,
                        FeatureTag = remoteDto.FeatureTag,
                        FeatureTagEn = remoteDto.FeatureTagEn,
                        DefaultTitle = !string.IsNullOrWhiteSpace(remoteDto.Name) ? remoteDto.Name : builtIn.DefaultTitle,
                        IconGlyph = !string.IsNullOrWhiteSpace(remoteDto.IconGlyph) ? remoteDto.IconGlyph : builtIn.IconGlyph,
                        BadgeColorHex = !string.IsNullOrWhiteSpace(remoteDto.BadgeColorHex) ? remoteDto.BadgeColorHex : builtIn.BadgeColorHex,
                        Version = remoteDto.Version,
                        MinSchemaVersion = remoteDto.MinSchemaVersion,
                        MinAppVersion = remoteDto.MinAppVersion,
                        DefaultWidth = remoteDto.DefaultWidth > 0 ? remoteDto.DefaultWidth : builtIn.DefaultWidth,
                        DefaultHeight = remoteDto.DefaultHeight > 0 ? remoteDto.DefaultHeight : builtIn.DefaultHeight,
                        ManifestJson = remoteJson,
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
            string? remoteJson = ExtractInlineOrCachedManifestJson(remoteDto);

            // Only show a remote-only entry if it either has valid manifest JSON or explicitly flags a client upgrade
            if (!requiresUpgrade && (string.IsNullOrWhiteSpace(remoteJson) || !IsValidManifestJson(remoteJson)))
            {
                continue;
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
                Description = remoteDto.Description,
                DescriptionEn = remoteDto.DescriptionEn,
                FeatureTag = remoteDto.FeatureTag,
                FeatureTagEn = remoteDto.FeatureTagEn,
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
                IsRemoteOnly = true,
                IsRemoteUpgraded = false
            });
        }

        return merged;
    }

    public bool TryGetRemoteTemplateJson(string? templateId, out string? manifestJson)
    {
        manifestJson = null;
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
                manifestJson = entry.ManifestJson;
                return true;
            }

            return false;
        }

        return false;
    }

    public bool TryResolveNewerOfficialManifestJson(string existingManifestJson, out string? upgradedJson)
    {
        upgradedJson = null;
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

        if (!existingId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
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

        string? candidateJson = ExtractInlineOrCachedManifestJson(match);
        if (string.IsNullOrWhiteSpace(candidateJson) || !IsValidManifestJson(candidateJson))
        {
            return false;
        }

        upgradedJson = candidateJson;
        return true;
    }

    public async Task<bool> RefreshInBackgroundAsync(bool force = false, CancellationToken ct = default)
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (!force)
        {
            long prevAttempt = Interlocked.Read(ref _lastRefreshAttemptUtcTicks);
            if (prevAttempt > 0 && nowTicks - prevAttempt < RefreshMinInterval.Ticks)
            {
                return false;
            }
        }

        if (!await _refreshGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            Interlocked.Exchange(ref _lastRefreshAttemptUtcTicks, nowTicks);
            CustomWidgetRemoteCatalogCacheMeta meta = ReadCacheMeta();

            if (!force &&
                meta.LastCheckedUtcTicks > 0 &&
                nowTicks - meta.LastCheckedUtcTicks < RefreshMinInterval.Ticks &&
                File.Exists(_catalogCachePath))
            {
                return false;
            }

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
                            if (!force &&
                                !string.IsNullOrWhiteSpace(meta.ETag) &&
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
                        return false;
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
                    bool applied = await ApplyCatalogJsonAsync(json, etag, url, ct).ConfigureAwait(false);
                    if (applied)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Silent fallback to mirror / built-in templates when offline or endpoint not yet live
                }
            }

            return false;
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
        CustomWidgetRemoteCatalogDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(
                catalogJson,
                CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogDocument);
        }
        catch
        {
            return false;
        }

        if (doc?.Widgets is null)
        {
            return false;
        }

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

            if (string.IsNullOrWhiteSpace(item.ManifestJson) &&
                !item.Deprecated &&
                !requiresUpgrade &&
                !string.IsNullOrWhiteSpace(item.ManifestUrl) &&
                CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(item.ManifestUrl, out Uri? manifestUri, out _) &&
                manifestUri is not null &&
                CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(manifestUri))
            {
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
                        }
                    }
                }
                catch
                {
                    // Individual manifest fetch failure does not fail the rest of the catalog
                }
            }
        }

        Directory.CreateDirectory(_cacheDirectory);
        string normalizedCatalogJson = JsonSerializer.Serialize(
            doc,
            CustomWidgetRemoteCatalogJsonContext.Default.CustomWidgetRemoteCatalogDocument);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_catalogCachePath, normalizedCatalogJson);
        WriteCacheMeta(new CustomWidgetRemoteCatalogCacheMeta
        {
            ETag = etag,
            SourceUrl = sourceUrl ?? PrimaryCatalogUrl,
            LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        });

        lock (_snapshotLock)
        {
            _cachedRemoteItems = doc.Widgets;
        }

        CatalogUpdated?.Invoke(this, EventArgs.Empty);
        return true;
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
        return trimmed.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? trimmed["deskbox.".Length..]
            : trimmed;
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

    private static async Task<string?> ReadBoundedUtf8StringAsync(
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

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
