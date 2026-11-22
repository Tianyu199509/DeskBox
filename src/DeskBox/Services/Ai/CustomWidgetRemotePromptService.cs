using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services.CustomWidgets;

namespace DeskBox.Services;

public sealed class CustomWidgetCapabilityPatchDto
{
    public string Id { get; set; } = string.Empty;
    public string? TitleZh { get; set; }
    public string? TitleEn { get; set; }
    public string? SummaryZh { get; set; }
    public string? SummaryEn { get; set; }
    public string? CompactContractMarkdown { get; set; }
    public string? GoldenSnippetJson { get; set; }
    public List<string>? TriggerKeywords { get; set; }
    public bool Deprecated { get; set; }
}

public sealed class CustomWidgetRemotePromptDocument
{
    public int SchemaVersion { get; set; } = CustomWidgetRemotePromptService.CurrentPromptSchemaVersion;
    public int PromptVersion { get; set; } = CustomWidgetRemotePromptService.BuiltInPromptVersion;
    public string? MinAppVersion { get; set; }
    public string? MaxAppVersion { get; set; }
    public string? UpdatedAtUtc { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SystemPromptZh { get; set; }
    public string? SystemPromptEn { get; set; }
    public string? ExtraDesignRules { get; set; }
    public string? SkillMarkdown { get; set; }
    public List<CustomWidgetCapabilityPatchDto>? CapabilityPatches { get; set; }
    public string? ContentSha256 { get; set; }
    public string? Signature { get; set; }
}

public sealed class CustomWidgetRemotePromptCacheMeta
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
[JsonSerializable(typeof(CustomWidgetRemotePromptDocument))]
[JsonSerializable(typeof(CustomWidgetRemotePromptCacheMeta))]
[JsonSerializable(typeof(CustomWidgetCapabilityPatchDto))]
[JsonSerializable(typeof(List<CustomWidgetCapabilityPatchDto>))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CustomWidgetRemotePromptJsonContext : JsonSerializerContext;

public sealed record CustomWidgetEffectivePromptPack(
    int PromptVersion,
    bool IsRemoteOverride,
    string? SystemPromptOverride,
    string? SystemPromptZh,
    string? SystemPromptEn,
    string ExtraDesignRules,
    string SkillMarkdown,
    IReadOnlyList<CustomWidgetCapabilityPatchDto> CapabilityPatches)
{
    public string? SystemPrompt => SystemPromptOverride;

    public string? ResolveSystemPromptOverride(string? cultureCode = null)
    {
        bool isZh = CustomWidgetRemotePromptService.IsChineseCulture(cultureCode);
        if (isZh && !string.IsNullOrWhiteSpace(SystemPromptZh))
        {
            return SystemPromptZh;
        }

        if (!isZh && !string.IsNullOrWhiteSpace(SystemPromptEn))
        {
            return SystemPromptEn;
        }

        return !string.IsNullOrWhiteSpace(SystemPromptOverride)
            ? SystemPromptOverride
            : (isZh ? SystemPromptEn : SystemPromptZh);
    }
}

/// <summary>
/// Fetches, validates, caches, and merges remote Custom Widget prompt overrides
/// (<c>prompts.v1.json</c>) with zero-latency stale-while-revalidate and automatic
/// downgrade protection against newer client builds.
/// </summary>
public sealed class CustomWidgetRemotePromptService
{
    public const int CurrentPromptSchemaVersion = 1;
    public const int BuiltInPromptVersion = 2;

    public const string PrimaryPromptUrl = "https://deskbox.fun/widgets/prompts.v1.json";
    public const string MirrorPromptUrl = "https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/prompts.v1.json";

    private static readonly TimeSpan RefreshMinInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan OfflineRetryBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const int MaxRemotePromptBytes = 256 * 1024;

    public static CustomWidgetRemotePromptService Shared { get; } = new();

    private readonly string _cacheDirectory;
    private readonly string _promptCachePath;
    private readonly string _metaCachePath;
    private readonly HttpClient _httpClient;
    private readonly Version? _currentAppVersionOverride;
    private readonly Func<CustomWidgetRemotePromptDocument, bool> _signatureVerifier;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private CustomWidgetRemotePromptDocument? _cachedRemoteDoc;
    private CustomWidgetEffectivePromptPack? _cachedEffectivePack;
    private bool _diskSnapshotLoaded;
    private long _lastRefreshAttemptUtcTicks;

    public event EventHandler? PromptsUpdated;

    public CustomWidgetRemotePromptService(
        string? cacheDirectory = null,
        HttpMessageHandler? httpMessageHandler = null,
        Version? currentAppVersionOverride = null)
        : this(cacheDirectory, httpMessageHandler, currentAppVersionOverride, signatureVerifier: null)
    {
    }

    internal CustomWidgetRemotePromptService(
        string? cacheDirectory,
        HttpMessageHandler? httpMessageHandler,
        Version? currentAppVersionOverride,
        Func<CustomWidgetRemotePromptDocument, bool>? signatureVerifier)
    {
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "cache", "widget-prompts")
            : cacheDirectory;
        _promptCachePath = Path.Combine(_cacheDirectory, "prompts.v1.json");
        _metaCachePath = Path.Combine(_cacheDirectory, "meta.json");
        _currentAppVersionOverride = currentAppVersionOverride;
        _signatureVerifier = signatureVerifier ?? VerifyPromptDefault;

        _httpClient = httpMessageHandler is not null
            ? new HttpClient(httpMessageHandler, disposeHandler: false) { Timeout = RequestTimeout }
            : CreateDefaultHttpClient();
    }

    private static bool VerifyPromptDefault(CustomWidgetRemotePromptDocument doc) =>
        CustomWidgetCatalogSignatureVerifier.VerifyPromptDocument(doc);

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

    public CustomWidgetEffectivePromptPack GetEffectivePromptPack()
    {
        lock (_snapshotLock)
        {
            if (!_diskSnapshotLoaded)
            {
                _diskSnapshotLoaded = true;
                _cachedRemoteDoc = LoadDiskCachedDocument();
                _cachedEffectivePack = null;
            }

            if (_cachedEffectivePack is not null)
            {
                return _cachedEffectivePack;
            }

            _cachedEffectivePack = BuildEffectivePromptPack(_cachedRemoteDoc);
            return _cachedEffectivePack;
        }
    }

    private CustomWidgetEffectivePromptPack BuildEffectivePromptPack(CustomWidgetRemotePromptDocument? remote)
    {
        if (remote is not null && IsValidCompatibleOverride(remote))
        {
            string effectiveSkill = !string.IsNullOrWhiteSpace(remote.SkillMarkdown)
                ? remote.SkillMarkdown.Trim()
                : CustomWidgetTemplates.DefaultAiAgentSkillMarkdown;
            string extraRules = remote.ExtraDesignRules?.Trim() ?? string.Empty;
            IReadOnlyList<CustomWidgetCapabilityPatchDto> patches = NormalizeCapabilityPatches(remote.CapabilityPatches);

            return new CustomWidgetEffectivePromptPack(
                PromptVersion: remote.PromptVersion,
                IsRemoteOverride: true,
                SystemPromptOverride: string.IsNullOrWhiteSpace(remote.SystemPrompt) ? null : remote.SystemPrompt.Trim(),
                SystemPromptZh: string.IsNullOrWhiteSpace(remote.SystemPromptZh) ? null : remote.SystemPromptZh.Trim(),
                SystemPromptEn: string.IsNullOrWhiteSpace(remote.SystemPromptEn) ? null : remote.SystemPromptEn.Trim(),
                ExtraDesignRules: extraRules,
                SkillMarkdown: AppendExtraRulesToSkillIfNeeded(effectiveSkill, extraRules),
                CapabilityPatches: patches);
        }

        return new CustomWidgetEffectivePromptPack(
            PromptVersion: BuiltInPromptVersion,
            IsRemoteOverride: false,
            SystemPromptOverride: null,
            SystemPromptZh: null,
            SystemPromptEn: null,
            ExtraDesignRules: string.Empty,
            SkillMarkdown: CustomWidgetTemplates.DefaultAiAgentSkillMarkdown,
            CapabilityPatches: []);
    }

    public static string DescribeTargetLanguage(string? cultureCode)
    {
        string normalized = LocalizationService.NormalizeCultureCode(
            string.IsNullOrWhiteSpace(cultureCode) ? Localized.ActiveCultureName : cultureCode);
        return normalized switch
        {
            "zh-CN" => "Simplified Chinese (简体中文)",
            "zh-TW" => "Traditional Chinese (繁體中文)",
            "en-US" => "English",
            "ja-JP" => "Japanese (日本語)",
            "ko-KR" => "Korean (한국어)",
            "de-DE" => "German (Deutsch)",
            "fr-FR" => "French (Français)",
            "es-ES" => "Spanish (Español)",
            "pt-BR" => "Portuguese (Português)",
            "ru-RU" => "Russian (Русский)",
            "ar-SA" => "Arabic (العربية)",
            "hi-IN" => "Hindi (हिन्दी)",
            "bn-BD" => "Bengali (বাংলা)",
            _ => "English"
        };
    }

    public static bool IsChineseCulture(string? cultureCode)
    {
        string normalized = string.IsNullOrWhiteSpace(cultureCode)
            ? Localized.ActiveCultureName
            : LocalizationService.NormalizeCultureCode(cultureCode);
        return normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    public static string ReplaceNamedTokens(
        string template,
        IReadOnlyDictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template) || tokens.Count == 0)
        {
            return template;
        }

        string result = template;
        foreach ((string key, string value) in tokens)
        {
            result = result.Replace($"{{{{{key}}}}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return result;
    }

    public async Task<bool> RefreshInBackgroundAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (!force &&
            nowTicks - Interlocked.Read(ref _lastRefreshAttemptUtcTicks) < RefreshMinInterval.Ticks)
        {
            return false;
        }

        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            CustomWidgetRemotePromptCacheMeta? meta = ReadCacheMeta();
            if (!force &&
                meta is not null &&
                meta.LastCheckedUtcTicks > 0 &&
                nowTicks - meta.LastCheckedUtcTicks < RefreshMinInterval.Ticks)
            {
                Interlocked.Exchange(ref _lastRefreshAttemptUtcTicks, meta.LastCheckedUtcTicks);
                return false;
            }

            Interlocked.Exchange(ref _lastRefreshAttemptUtcTicks, nowTicks);

            string[] endpoints = [PrimaryPromptUrl, MirrorPromptUrl];
            foreach (string endpoint in endpoints)
            {
                if (!IsAllowedRemoteUrl(endpoint))
                {
                    continue;
                }

                FetchOutcome outcome = await TryFetchEndpointAsync(endpoint, meta, cancellationToken);
                if (outcome.Kind == FetchOutcomeKind.NotModified)
                {
                    WriteCacheMeta(new CustomWidgetRemotePromptCacheMeta
                    {
                        ETag = meta?.ETag,
                        SourceUrl = endpoint,
                        LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                    });
                    return false;
                }

                if (outcome.Kind == FetchOutcomeKind.NoOverrideNeeded)
                {
                    bool hadActiveOverride = RetireSnapshotIfPresent(endpoint, outcome.ETag);
                    if (hadActiveOverride)
                    {
                        PromptsUpdated?.Invoke(this, EventArgs.Empty);
                    }

                    return false;
                }

                if (outcome.Kind == FetchOutcomeKind.Updated &&
                    outcome.Document is not null &&
                    !string.IsNullOrWhiteSpace(outcome.RawJson))
                {
                    PersistSnapshot(outcome.RawJson, outcome.Document, endpoint, outcome.ETag);
                    PromptsUpdated?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }

            Interlocked.Exchange(
                ref _lastRefreshAttemptUtcTicks,
                nowTicks - RefreshMinInterval.Ticks + OfflineRetryBackoff.Ticks);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[RemotePrompts] Background refresh skipped: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public bool IsValidCompatibleOverride(CustomWidgetRemotePromptDocument? doc) =>
        IsValidCompatibleOverrideCore(doc, signatureAlreadyVerified: false);

    private bool IsValidCompatibleOverrideCore(CustomWidgetRemotePromptDocument? doc, bool signatureAlreadyVerified)
    {
        if (doc is null ||
            doc.SchemaVersion != CurrentPromptSchemaVersion ||
            doc.PromptVersion <= BuiltInPromptVersion ||
            (!signatureAlreadyVerified && !_signatureVerifier(doc)))
        {
            return false;
        }

        Version rawCurrent = _currentAppVersionOverride ?? CustomWidgetManifestParser.CurrentAppVersion;
        Version currentNormalized = new(
            Math.Max(0, rawCurrent.Major),
            Math.Max(0, rawCurrent.Minor),
            Math.Max(0, rawCurrent.Build),
            Math.Max(0, rawCurrent.Revision));

        if (!string.IsNullOrWhiteSpace(doc.MinAppVersion) &&
            CustomWidgetManifestParser.TryParseLooseVersion(doc.MinAppVersion, out Version minVer) &&
            currentNormalized < minVer)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(doc.MaxAppVersion) &&
            CustomWidgetManifestParser.TryParseLooseVersion(doc.MaxAppVersion, out Version maxVer) &&
            currentNormalized > maxVer)
        {
            return false;
        }

        if (!ValidateSkillMarkdown(doc.SkillMarkdown) ||
            !ValidateCapabilityPatches(doc.CapabilityPatches))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(doc.SystemPrompt) ||
               !string.IsNullOrWhiteSpace(doc.SystemPromptZh) ||
               !string.IsNullOrWhiteSpace(doc.SystemPromptEn) ||
               !string.IsNullOrWhiteSpace(doc.ExtraDesignRules) ||
               !string.IsNullOrWhiteSpace(doc.SkillMarkdown) ||
               doc.CapabilityPatches is { Count: > 0 };
    }

    private static bool ValidateCapabilityPatches(List<CustomWidgetCapabilityPatchDto>? patches)
    {
        if (patches is null || patches.Count == 0)
        {
            return true;
        }

        if (patches.Count > 128)
        {
            return false;
        }

        foreach (CustomWidgetCapabilityPatchDto? patch in patches)
        {
            if (patch is null || string.IsNullOrWhiteSpace(patch.Id) || patch.Id.Trim().Length > 80)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<CustomWidgetCapabilityPatchDto> NormalizeCapabilityPatches(
        List<CustomWidgetCapabilityPatchDto>? patches)
    {
        if (patches is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<CustomWidgetCapabilityPatchDto>(patches.Count);
        foreach (CustomWidgetCapabilityPatchDto patch in patches)
        {
            if (patch is null || string.IsNullOrWhiteSpace(patch.Id))
            {
                continue;
            }

            result.Add(new CustomWidgetCapabilityPatchDto
            {
                Id = patch.Id.Trim(),
                TitleZh = string.IsNullOrWhiteSpace(patch.TitleZh) ? null : patch.TitleZh.Trim(),
                TitleEn = string.IsNullOrWhiteSpace(patch.TitleEn) ? null : patch.TitleEn.Trim(),
                SummaryZh = string.IsNullOrWhiteSpace(patch.SummaryZh) ? null : patch.SummaryZh.Trim(),
                SummaryEn = string.IsNullOrWhiteSpace(patch.SummaryEn) ? null : patch.SummaryEn.Trim(),
                CompactContractMarkdown = string.IsNullOrWhiteSpace(patch.CompactContractMarkdown) ? null : patch.CompactContractMarkdown.Trim(),
                GoldenSnippetJson = string.IsNullOrWhiteSpace(patch.GoldenSnippetJson) ? null : patch.GoldenSnippetJson.Trim(),
                TriggerKeywords = patch.TriggerKeywords is { Count: > 0 }
                    ? patch.TriggerKeywords
                        .Where(static k => !string.IsNullOrWhiteSpace(k))
                        .Select(static k => k.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : null,
                Deprecated = patch.Deprecated
            });
        }

        return result;
    }

    private static bool ValidateSkillMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return true;
        }

        return markdown.Contains("schemaVersion", StringComparison.Ordinal) &&
               markdown.Contains("view", StringComparison.Ordinal);
    }

    private static string AppendExtraRulesToSkillIfNeeded(string skillMarkdown, string extraRules)
    {
        if (string.IsNullOrWhiteSpace(extraRules) ||
            skillMarkdown.Contains(extraRules, StringComparison.Ordinal))
        {
            return skillMarkdown;
        }

        return $"{skillMarkdown.TrimEnd()}\n\n## Additional Official Design Rules (OTA)\n{extraRules.Trim()}\n";
    }

    private CustomWidgetRemotePromptDocument? LoadDiskCachedDocument()
    {
        try
        {
            if (!File.Exists(_promptCachePath))
            {
                return null;
            }

            var info = new FileInfo(_promptCachePath);
            if (info.Length <= 0 || info.Length > MaxRemotePromptBytes)
            {
                return null;
            }

            string json = File.ReadAllText(_promptCachePath, Encoding.UTF8);
            CustomWidgetRemotePromptDocument? doc = JsonSerializer.Deserialize(
                json,
                CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptDocument);
            return IsValidCompatibleOverride(doc) ? doc : null;
        }
        catch (Exception ex)
        {
            App.Log($"[RemotePrompts] Failed to load cached prompts: {ex.Message}");
            return null;
        }
    }

    private void PersistSnapshot(
        string rawJson,
        CustomWidgetRemotePromptDocument doc,
        string sourceUrl,
        string? etag)
    {
        lock (_snapshotLock)
        {
            _diskSnapshotLoaded = true;
            _cachedRemoteDoc = doc;
            _cachedEffectivePack = null;
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_promptCachePath, rawJson);

            WriteCacheMeta(new CustomWidgetRemotePromptCacheMeta
            {
                ETag = etag,
                SourceUrl = sourceUrl,
                LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
            });
        }
        catch (Exception ex)
        {
            App.Log($"[RemotePrompts] Failed to write prompt cache: {ex.Message}");
        }
    }

    private bool RetireSnapshotIfPresent(string sourceUrl, string? etag)
    {
        bool hadOverride;
        lock (_snapshotLock)
        {
            hadOverride = _cachedRemoteDoc is not null || File.Exists(_promptCachePath);
            _diskSnapshotLoaded = true;
            _cachedRemoteDoc = null;
            _cachedEffectivePack = null;
        }

        try
        {
            CustomWidgetStorageFileOps.DeleteFileIfExists(_promptCachePath);
        }
        catch (Exception ex)
        {
            App.Log($"[RemotePrompts] Failed to clear retired prompt cache: {ex.Message}");
        }

        WriteCacheMeta(new CustomWidgetRemotePromptCacheMeta
        {
            ETag = etag,
            SourceUrl = sourceUrl,
            LastCheckedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        });
        return hadOverride;
    }

    private CustomWidgetRemotePromptCacheMeta? ReadCacheMeta()
    {
        try
        {
            if (!File.Exists(_metaCachePath))
            {
                return null;
            }

            string raw = File.ReadAllText(_metaCachePath, Encoding.UTF8);
            return JsonSerializer.Deserialize(
                raw,
                CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptCacheMeta);
        }
        catch
        {
            return null;
        }
    }

    private void WriteCacheMeta(CustomWidgetRemotePromptCacheMeta meta)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string json = JsonSerializer.Serialize(
                meta,
                CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptCacheMeta);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_metaCachePath, json);
        }
        catch
        {
        }
    }

    private enum FetchOutcomeKind
    {
        Failed,
        NotModified,
        NoOverrideNeeded,
        Updated
    }

    private readonly record struct FetchOutcome(
        FetchOutcomeKind Kind,
        CustomWidgetRemotePromptDocument? Document = null,
        string? RawJson = null,
        string? ETag = null);

    private async Task<FetchOutcome> TryFetchEndpointAsync(
        string url,
        CustomWidgetRemotePromptCacheMeta? meta,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpointUri))
            {
                return new FetchOutcome(FetchOutcomeKind.Failed);
            }

            using HttpResponseMessage resp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                _httpClient,
                endpointUri,
                requireTrustedOfficialHost: true,
                configureRequest: req =>
                {
                    if (meta is not null &&
                        !string.IsNullOrWhiteSpace(meta.ETag) &&
                        string.Equals(meta.SourceUrl, url, StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
                    }
                },
                cancellationToken: cancellationToken);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchOutcome(FetchOutcomeKind.NotModified);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new FetchOutcome(FetchOutcomeKind.Failed);
            }

            if (resp.Content.Headers.ContentLength is { } contentLen &&
                contentLen > MaxRemotePromptBytes)
            {
                return new FetchOutcome(FetchOutcomeKind.Failed);
            }

            await using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream();
            byte[] buffer = new byte[8192];
            int totalRead = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > MaxRemotePromptBytes)
                {
                    return new FetchOutcome(FetchOutcomeKind.Failed);
                }

                ms.Write(buffer, 0, read);
            }

            string rawJson = Encoding.UTF8.GetString(ms.ToArray());
            CustomWidgetRemotePromptDocument? doc = JsonSerializer.Deserialize(
                rawJson,
                CustomWidgetRemotePromptJsonContext.Default.CustomWidgetRemotePromptDocument);
            string? etag = resp.Headers.ETag?.Tag;
            if (doc is null ||
                doc.SchemaVersion != CurrentPromptSchemaVersion ||
                doc.PromptVersion < 0 ||
                !_signatureVerifier(doc))
            {
                return new FetchOutcome(FetchOutcomeKind.Failed);
            }

            if (doc.PromptVersion <= BuiltInPromptVersion)
            {
                return ValidateSkillMarkdown(doc.SkillMarkdown) && ValidateCapabilityPatches(doc.CapabilityPatches)
                    ? new FetchOutcome(FetchOutcomeKind.NoOverrideNeeded, ETag: etag)
                    : new FetchOutcome(FetchOutcomeKind.Failed);
            }

            return IsValidCompatibleOverrideCore(doc, signatureAlreadyVerified: true)
                ? new FetchOutcome(FetchOutcomeKind.Updated, doc, rawJson, etag)
                : new FetchOutcome(FetchOutcomeKind.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new FetchOutcome(FetchOutcomeKind.Failed);
        }
    }

    internal static bool IsAllowedRemoteUrl(string? rawUrl) =>
        CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(rawUrl, out Uri? uri, out _) &&
        uri is not null &&
        CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(uri);
}
