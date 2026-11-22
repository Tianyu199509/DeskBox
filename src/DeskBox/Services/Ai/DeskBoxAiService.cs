using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Global NativeAOT-safe AI substrate for DeskBox. Owns provider presets, DPAPI credential resolution,
/// role-based model/thinking routing (<see cref="AiTaskProfile"/>), connectivity probes, model discovery,
/// SSE streaming, and fast structured JSON completion (<see cref="CompleteJsonAsync"/>).
/// </summary>
public sealed class DeskBoxAiService
{
    public const string CustomProviderId = AiProviderCatalog.CustomProviderId;
    public const int MaxMicroTaskPromptChars = 16_384;
    public const int MaxPromptChars = MaxMicroTaskPromptChars;

    public static IReadOnlyList<CustomWidgetAiProviderPreset> SupportedProviders =>
        AiProviderCatalog.SupportedProviders;

    public static IReadOnlyList<CustomWidgetAiProviderPreset> GetOrderedProvidersForCulture(string? cultureName = null) =>
        AiProviderCatalog.GetOrderedProvidersForCulture(cultureName);

    public static string GetDefaultStarterProviderId(string? cultureName = null) =>
        AiProviderCatalog.GetDefaultStarterProviderId(cultureName);

    public static bool IsSimplifiedChineseRegion(string? cultureName = null) =>
        AiProviderCatalog.IsSimplifiedChineseRegion(cultureName);

    public static bool IsInternationalRegion(string? region) =>
        AiProviderCatalog.IsInternationalRegion(region);

    public static string NormalizeRegion(string? region) =>
        AiProviderCatalog.NormalizeRegion(region);

    public static bool IsProviderConfigured(CustomWidgetAiProviderPreset preset, AiProviderCredentialEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (entry is null)
        {
            return false;
        }

        return preset.RequiresApiKey
            ? !string.IsNullOrWhiteSpace(entry.ApiKey)
            : entry.Enabled;
    }

    public static CustomWidgetAiProviderPreset FindProviderPreset(string? providerId) =>
        AiProviderCatalog.FindProviderPreset(providerId);

    public static AiThinkingMode ParseThinkingMode(string? raw) =>
        AiProviderCatalog.ParseThinkingMode(raw);

    public static string ToThinkingWireValue(AiThinkingMode mode) =>
        AiProviderCatalog.ToThinkingWireValue(mode);

    public static AiContextWindowMode ParseContextWindowMode(string? raw) =>
        AiProviderCatalog.ParseContextWindowMode(raw);

    public static string ToContextWindowWireValue(AiContextWindowMode mode) =>
        AiProviderCatalog.ToContextWindowWireValue(mode);

    private const string DefaultMicroTaskSystemPrompt =
        "You are a fast, precise structured JSON assistant inside DeskBox. " +
        "Respond ONLY with a single valid JSON object (or JSON array when explicitly requested). " +
        "Never include markdown explanations, code commentary, or surrounding prose.";

    private static readonly Lazy<HttpClient> LazySharedHttpClient = new(
        static () => new HttpClient(new SameOriginAuthSafeRedirectHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedLocalOrPublicIpAsync
        }))
        {
            // Streaming requests wait until the upstream gateway/model finishes, errors out,
            // TCP keep-alive detects a dead connection, or the user cancels.
            Timeout = Timeout.InfiniteTimeSpan
        },
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly CustomWidgetAiCredentialStore _credentialStore;
    private readonly CustomWidgetStudioPreferencesStore _preferencesStore;
    private readonly HttpClient? _httpClientOverride;
    private readonly ConcurrentDictionary<string, byte> _thinkingExhaustedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _thinkingParamsUnsupportedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _toolsUnsupportedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _discoveredModelContextTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _learnedRouteContextTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _sessionAutoFetchedProviders = new(StringComparer.OrdinalIgnoreCase);
    private long _observedCredentialGeneration;

    private long _totalCalls;
    private long _autoRepairedCalls;
    private long _totalPromptTokens;
    private long _totalCachedPromptTokens;
    private long _totalCompletionTokens;
    private long _lastCallLatencyMs;

    public static DeskBoxAiService Shared { get; } = new();

    public DeskBoxAiService(
        CustomWidgetAiCredentialStore? credentialStore = null,
        CustomWidgetStudioPreferencesStore? preferencesStore = null,
        HttpClient? httpClient = null)
    {
        _credentialStore = credentialStore ?? CustomWidgetAiCredentialStore.Shared;
        _preferencesStore = preferencesStore ?? CustomWidgetStudioPreferencesStore.Shared;
        _httpClientOverride = httpClient;
        _observedCredentialGeneration = _credentialStore.Generation;
    }

    public void ClearRouteFallbacks()
    {
        _thinkingExhaustedRoutes.Clear();
        _thinkingParamsUnsupportedRoutes.Clear();
        _toolsUnsupportedRoutes.Clear();
        _learnedRouteContextTokens.Clear();
    }

    private void EnsureRouteCachesCurrent()
    {
        long gen = _credentialStore.Generation;
        long prev = Interlocked.Read(ref _observedCredentialGeneration);
        if (gen != prev && Interlocked.CompareExchange(ref _observedCredentialGeneration, gen, prev) == prev)
        {
            ClearRouteFallbacks();
        }
    }

    internal CustomWidgetAiCredentialStore CredentialStore => _credentialStore;

    internal HttpClient HttpClient => _httpClientOverride ?? LazySharedHttpClient.Value;

    public AiCallTelemetrySnapshot GetTelemetrySnapshot()
    {
        long calls = Interlocked.Read(ref _totalCalls);
        long repaired = Interlocked.Read(ref _autoRepairedCalls);
        long prompt = Interlocked.Read(ref _totalPromptTokens);
        long cached = Interlocked.Read(ref _totalCachedPromptTokens);
        long completion = Interlocked.Read(ref _totalCompletionTokens);
        long lastLatency = Interlocked.Read(ref _lastCallLatencyMs);
        double ratio = prompt > 0 ? Math.Clamp((double)cached / prompt, 0.0, 1.0) : 0.0;
        return new AiCallTelemetrySnapshot(
            calls,
            repaired,
            prompt,
            cached,
            completion,
            lastLatency,
            ratio);
    }

    public void ResetTelemetry()
    {
        Interlocked.Exchange(ref _totalCalls, 0);
        Interlocked.Exchange(ref _autoRepairedCalls, 0);
        Interlocked.Exchange(ref _totalPromptTokens, 0);
        Interlocked.Exchange(ref _totalCachedPromptTokens, 0);
        Interlocked.Exchange(ref _totalCompletionTokens, 0);
        Interlocked.Exchange(ref _lastCallLatencyMs, 0);
    }

    public void RecordCallTelemetry(
        int promptTokens,
        int cachedPromptTokens,
        int completionTokens,
        long latencyMs,
        bool isAutoRepair = false)
    {
        Interlocked.Increment(ref _totalCalls);
        if (isAutoRepair)
        {
            Interlocked.Increment(ref _autoRepairedCalls);
        }

        if (promptTokens > 0)
        {
            Interlocked.Add(ref _totalPromptTokens, promptTokens);
        }

        if (cachedPromptTokens > 0)
        {
            Interlocked.Add(ref _totalCachedPromptTokens, Math.Min(cachedPromptTokens, Math.Max(promptTokens, cachedPromptTokens)));
        }

        if (completionTokens > 0)
        {
            Interlocked.Add(ref _totalCompletionTokens, completionTokens);
        }

        if (latencyMs > 0)
        {
            Interlocked.Exchange(ref _lastCallLatencyMs, latencyMs);
        }
    }

    public IReadOnlyList<CustomWidgetAiProviderPreset> GetConfiguredProviders()
    {
        IReadOnlyDictionary<string, AiProviderCredentialEntry> all = _credentialStore.GetAll();
        var list = new List<CustomWidgetAiProviderPreset>();
        foreach (CustomWidgetAiProviderPreset preset in AiProviderCatalog.SupportedProviders)
        {
            if (all.TryGetValue(preset.Id, out AiProviderCredentialEntry? entry) &&
                IsProviderConfigured(preset, entry))
            {
                list.Add(preset);
            }
        }

        return list;
    }

    public bool HasAnyConfiguredProvider() => GetConfiguredProviders().Count > 0;

    /// <summary>
    /// Resolves the active provider ID using an optional override, falling back to the user's preferred
    /// default provider in <see cref="CustomWidgetStudioPreferencesStore"/> or the first configured provider.
    /// Returns <c>null</c> if no provider is configured.
    /// </summary>
    public string? ResolveActiveProviderId(string? providerIdOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(providerIdOverride))
        {
            CustomWidgetAiProviderPreset explicitPreset = AiProviderCatalog.FindProviderPreset(providerIdOverride);
            AiProviderCredentialEntry? entry = _credentialStore.Get(explicitPreset.Id);
            if (IsProviderConfigured(explicitPreset, entry))
            {
                return explicitPreset.Id;
            }
        }

        return CustomWidgetWorkshopFlow.ResolveDirectProvider(
            GetConfiguredProviders(),
            _preferencesStore.PreferredAiProviderId);
    }


    public IReadOnlyList<string> GetSelectableModels(
        string providerId,
        IReadOnlyList<string>? transientFetchedModels = null)
    {
        CustomWidgetAiProviderPreset preset = FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = _credentialStore.Get(preset.Id);

        IReadOnlyList<string> sourceList = transientFetchedModels is { Count: > 0 }
            ? transientFetchedModels
            : (entry?.FetchedModels is { Count: > 0 } ? entry.FetchedModels : preset.Models);

        var result = new List<string>(sourceList.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (transientFetchedModels is not { Count: > 0 } &&
            !string.IsNullOrWhiteSpace(entry?.CustomModel))
        {
            string custom = entry.CustomModel.Trim();
            if (!sourceList.Contains(custom, StringComparer.OrdinalIgnoreCase) && seen.Add(custom))
            {
                result.Add(custom);
            }
        }

        foreach (string m in sourceList)
        {
            if (!string.IsNullOrWhiteSpace(m))
            {
                string trimmed = m.Trim();
                if (seen.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }

    public ResolvedAiInvocation ResolveEffectiveSettings(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null)
    {
        CustomWidgetAiProviderPreset preset = AiProviderCatalog.FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = _credentialStore.Get(preset.Id);

        string apiKey = (apiKeyOverride ?? entry?.ApiKey)?.Trim() ?? string.Empty;
        AiThinkingMode thinkingMode = thinkingOverride ?? AiProviderCatalog.ParseThinkingMode(entry?.ThinkingMode);

        string rawCustomEndpoint = (endpointOverride ?? entry?.CustomEndpoint)?.Trim() ?? string.Empty;
        bool usesCustomEndpoint = !string.IsNullOrWhiteSpace(rawCustomEndpoint);
        string endpoint = AiProviderCatalog.ResolveConfiguredEndpointUrl(preset, entry?.Region, rawCustomEndpoint);

        string rawCustomModel = (modelOverride ?? entry?.CustomModel)?.Trim() ?? string.Empty;
        string fallbackDefault = entry?.FetchedModels is { Count: > 0 }
            ? entry.FetchedModels[0]
            : preset.DefaultModel;
        string model = string.IsNullOrWhiteSpace(rawCustomModel) ? fallbackDefault : rawCustomModel;

        // DeepSeek's classic dual-endpoint setup switches thinking via model ID (deepseek-chat <-> deepseek-reasoner).
        if (AiProviderCatalog.SupportsClassicDeepSeekModelSwap(preset, entry?.FetchedModels) &&
            (string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase)))
        {
            if (thinkingMode == AiThinkingMode.Enabled &&
                string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase))
            {
                model = "deepseek-reasoner";
            }
            else if (thinkingMode == AiThinkingMode.Disabled &&
                     string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
            {
                model = "deepseek-chat";
            }
        }

        AiContextWindowMode contextWindowMode = AiProviderCatalog.ParseContextWindowMode(entry?.ContextWindowMode);
        return new ResolvedAiInvocation(preset, endpoint, model, apiKey, thinkingMode, usesCustomEndpoint, contextWindowMode);
    }

    private const int MaxRouteCacheEntries = 128;

    private static void TrimCacheIfOverCapacity<TValue>(ConcurrentDictionary<string, TValue> dict)
    {
        if (dict.Count >= MaxRouteCacheEntries)
        {
            dict.Clear();
        }
    }

    internal void MarkThinkingExhaustedRoute(ResolvedAiInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.Model))
        {
            return;
        }

        TrimCacheIfOverCapacity(_thinkingExhaustedRoutes);
        _thinkingExhaustedRoutes[$"{invocation.Endpoint}|{invocation.Model}"] = 1;
    }

    internal bool IsThinkingExhaustedRoute(ResolvedAiInvocation invocation)
    {
        EnsureRouteCachesCurrent();
        return !string.IsNullOrWhiteSpace(invocation.Model) &&
               _thinkingExhaustedRoutes.ContainsKey($"{invocation.Endpoint}|{invocation.Model}");
    }

    internal void MarkThinkingParamsUnsupportedRoute(ResolvedAiInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.Model))
        {
            return;
        }

        TrimCacheIfOverCapacity(_thinkingParamsUnsupportedRoutes);
        _thinkingParamsUnsupportedRoutes[$"{invocation.Endpoint}|{invocation.Model}"] = 1;
    }

    internal bool IsThinkingParamsUnsupportedRoute(ResolvedAiInvocation invocation)
    {
        EnsureRouteCachesCurrent();
        return !string.IsNullOrWhiteSpace(invocation.Model) &&
               _thinkingParamsUnsupportedRoutes.ContainsKey($"{invocation.Endpoint}|{invocation.Model}");
    }

    internal bool IsToolsUnsupportedRoute(ResolvedAiInvocation invocation)
    {
        EnsureRouteCachesCurrent();
        return !string.IsNullOrWhiteSpace(invocation.Model) &&
               _toolsUnsupportedRoutes.ContainsKey($"{invocation.Endpoint}|{invocation.Model}");
    }

    internal void MarkToolsUnsupportedRoute(ResolvedAiInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.Model))
        {
            return;
        }

        TrimCacheIfOverCapacity(_toolsUnsupportedRoutes);
        _toolsUnsupportedRoutes[$"{invocation.Endpoint}|{invocation.Model}"] = 1;
    }

    internal void RecordLearnedContextLimit(ResolvedAiInvocation invocation, int contextTokens)
    {
        if (string.IsNullOrWhiteSpace(invocation.Model) || contextTokens < 2_048)
        {
            return;
        }

        TrimCacheIfOverCapacity(_learnedRouteContextTokens);
        string key = $"{invocation.Endpoint}|{invocation.Model}";
        _learnedRouteContextTokens.AddOrUpdate(
            key,
            contextTokens,
            (_, existing) => Math.Min(existing, contextTokens));
    }

    internal int? GetKnownContextTokens(ResolvedAiInvocation invocation)
    {
        EnsureRouteCachesCurrent();
        if (string.IsNullOrWhiteSpace(invocation.Model))
        {
            return null;
        }

        string key = $"{invocation.Endpoint}|{invocation.Model}";
        if (_learnedRouteContextTokens.TryGetValue(key, out int learned))
        {
            return learned;
        }

        if (_discoveredModelContextTokens.TryGetValue(key, out int discovered))
        {
            return discovered;
        }

        return null;
    }

    internal ResolvedAiInvocation CoerceSafeStudioAuthoringInvocation(ResolvedAiInvocation invocation)
    {
        EnsureRouteCachesCurrent();
        if (_thinkingParamsUnsupportedRoutes.ContainsKey($"{invocation.Endpoint}|{invocation.Model}") &&
            invocation.ThinkingMode != AiThinkingMode.Auto)
        {
            invocation = invocation with { ThinkingMode = AiThinkingMode.Auto };
        }

        if (invocation.ThinkingMode == AiThinkingMode.Auto &&
            _thinkingExhaustedRoutes.ContainsKey($"{invocation.Endpoint}|{invocation.Model}"))
        {
            AiProviderCredentialEntry? entry = _credentialStore.Get(invocation.Preset.Id);
            if (string.Equals(invocation.Model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase) &&
                AiProviderCatalog.SupportsClassicDeepSeekModelSwap(invocation.Preset, entry?.FetchedModels))
            {
                return invocation with
                {
                    Model = "deepseek-chat",
                    ThinkingMode = AiThinkingMode.Disabled
                };
            }

            if (!AiProviderCatalog.IsReasoningOnlyModel(invocation.Model))
            {
                return invocation with { ThinkingMode = AiThinkingMode.Disabled };
            }
        }

        return invocation;
    }

    /// <summary>
    /// Resolves invocation settings tailored for a specific <see cref="AiTaskProfile"/>.
    /// When <paramref name="profile"/> is <see cref="AiTaskProfile.FastMicroTask"/>, automatically forces
    /// <see cref="AiThinkingMode.Disabled"/> and maps known pure-reasoning models to their fast chat sibling
    /// while preserving <paramref name="originalModelFallback"/> in case a restricted relay rejects the sibling.
    /// </summary>
    public ResolvedAiInvocation ResolveEffectiveSettingsForProfile(
        string providerId,
        AiTaskProfile profile,
        out string? originalModelFallback)
    {
        originalModelFallback = null;
        if (profile == AiTaskProfile.StudioAuthoring)
        {
            return CoerceSafeStudioAuthoringInvocation(ResolveEffectiveSettings(providerId));
        }

        ResolvedAiInvocation baseStudio = ResolveEffectiveSettings(providerId);
        ResolvedAiInvocation fastResolved = ResolveEffectiveSettings(
            providerId,
            thinkingOverride: AiThinkingMode.Disabled);

        string? fastSibling = AiProviderCatalog.TryResolveFastSiblingModel(fastResolved.Preset, fastResolved.Model, fastResolved.UsesCustomEndpoint);
        if (!string.IsNullOrWhiteSpace(fastSibling) &&
            !string.Equals(fastSibling, fastResolved.Model, StringComparison.OrdinalIgnoreCase))
        {
            AiProviderCredentialEntry? entry = _credentialStore.Get(fastResolved.Preset.Id);
            bool skipNonExistentClassicChat =
                string.Equals(fastSibling, "deepseek-chat", StringComparison.OrdinalIgnoreCase) &&
                !AiProviderCatalog.SupportsClassicDeepSeekModelSwap(fastResolved.Preset, entry?.FetchedModels);
            if (!skipNonExistentClassicChat)
            {
                originalModelFallback = fastResolved.Model;
                fastResolved = fastResolved with { Model = fastSibling };
            }
        }
        else if (!string.Equals(baseStudio.Model, fastResolved.Model, StringComparison.OrdinalIgnoreCase))
        {
            originalModelFallback = baseStudio.Model;
        }

        return fastResolved;
    }


    public async Task<bool> TryEnsureModelsFetchedOnceAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        CustomWidgetAiProviderPreset preset = FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = _credentialStore.Get(preset.Id);
        if (!IsProviderConfigured(preset, entry) || entry?.FetchedModels is { Count: > 0 })
        {
            return false;
        }

        ResolvedAiInvocation resolved = ResolveEffectiveSettings(preset.Id);
        TrimCacheIfOverCapacity(_sessionAutoFetchedProviders);
        string cacheKey = $"{preset.Id}|{resolved.Endpoint}";
        if (!_sessionAutoFetchedProviders.TryAdd(cacheKey, 1))
        {
            return false;
        }

        try
        {
            AiModelListResult res = await FetchAvailableModelsAsync(
                preset.Id,
                cancellationToken: cancellationToken,
                persistDiscoveredModels: true).ConfigureAwait(false);
            return res.Success && res.Models.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<AiModelListResult> FetchAvailableModelsAsync(
        string providerId,
        string? endpointOverride = null,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default,
        bool persistDiscoveredModels = true)
    {
        ResolvedAiInvocation resolved = ResolveEffectiveSettings(
            providerId,
            endpointOverride: endpointOverride,
            apiKeyOverride: apiKeyOverride);

        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.EnterApiKeyFirst"));
        }

        string modelsUrl = AiProviderCatalog.DeriveModelsEndpoint(resolved.Endpoint);
        if (string.IsNullOrWhiteSpace(modelsUrl))
        {
            return new AiModelListResult(false, resolved.Preset.Models, "Invalid HTTP/HTTPS endpoint URL.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            AiChatTransportClient.ApplyAuthHeaders(req, resolved.Endpoint, resolved.ApiKey);

            using HttpResponseMessage resp = await HttpClient.SendAsync(req, timeoutCts.Token);
            string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string err = AiChatTransportClient.FormatHttpError(resp.StatusCode, body);
                return new AiModelListResult(false, resolved.Preset.Models, err);
            }

            List<string> discovered = AiChatTransportClient.ParseModelListJson(body);
            if (discovered.Count == 0)
            {
                return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.NoModelsReturned"));
            }

            Dictionary<string, int> parsedContextLengths = AiContextBudgetResolver.ParseModelContextLengthsFromModelsJson(body);
            if (_discoveredModelContextTokens.Count + parsedContextLengths.Count > 512)
            {
                _discoveredModelContextTokens.Clear();
            }
            foreach ((string modelId, int ctxLen) in parsedContextLengths)
            {
                _discoveredModelContextTokens[$"{resolved.Endpoint}|{modelId}"] = ctxLen;
            }

            var ordered = new List<string>(discovered.Count + resolved.Preset.Models.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rec in resolved.Preset.Models)
            {
                if (discovered.Contains(rec, StringComparer.OrdinalIgnoreCase) && seen.Add(rec))
                {
                    ordered.Add(rec);
                }
            }

            foreach (string id in discovered)
            {
                if (seen.Add(id))
                {
                    ordered.Add(id);
                }
            }

            if (persistDiscoveredModels && ordered.Count > 0)
            {
                AiProviderCredentialEntry? existingEntry = _credentialStore.Get(resolved.Preset.Id);
                bool matchesStoredEndpoint = string.IsNullOrWhiteSpace(endpointOverride) ||
                    string.Equals(
                        AiProviderCatalog.ResolveConfiguredEndpointUrl(resolved.Preset, existingEntry?.Region, existingEntry?.CustomEndpoint),
                        resolved.Endpoint,
                        StringComparison.OrdinalIgnoreCase);
                if (matchesStoredEndpoint &&
                    (existingEntry is not null || !resolved.Preset.RequiresApiKey))
                {
                    _credentialStore.UpdateFetchedModels(resolved.Preset.Id, ordered);
                }
            }

            return new AiModelListResult(true, ordered, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.ModelListTimeout"));
        }
        catch (OperationCanceledException)
        {
            return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.Cancelled"));
        }
        catch (Exception ex)
        {
            return new AiModelListResult(false, resolved.Preset.Models, ex.Message);
        }
    }

    public async Task<AiConnectionTestResult> TestConnectionAsync(
        string providerId,
        string? endpointOverride = null,
        string? apiKeyOverride = null,
        string? modelOverride = null,
        AiThinkingMode? thinkingOverride = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedAiInvocation resolved = ResolveEffectiveSettings(
            providerId,
            endpointOverride: endpointOverride,
            modelOverride: modelOverride,
            apiKeyOverride: apiKeyOverride,
            thinkingOverride: thinkingOverride);

        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return new AiConnectionTestResult(
                false,
                0,
                resolved.Endpoint,
                resolved.Model,
                null,
                Localized.T("CustomWidget.AiApi.Error.EnterApiKeyFirst"));
        }

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(28));

        (bool ok, string reply, string? error) = await AiChatTransportClient.SendProbeCompletionAsync(this, 
            resolved,
            includeThinkingParams: resolved.ThinkingMode != AiThinkingMode.Auto,
            timeoutCts.Token,
            cancellationToken);

        sw.Stop();
        if (!ok)
        {
            return new AiConnectionTestResult(
                false,
                sw.ElapsedMilliseconds,
                resolved.Endpoint,
                resolved.Model,
                null,
                error ?? Localized.T("CustomWidget.AiApi.Error.ConnectionFailed"));
        }

        string preview = string.IsNullOrWhiteSpace(reply)
            ? "OK"
            : (reply.Trim().Length > 36 ? reply.Trim()[..36] + "…" : reply.Trim());

        return new AiConnectionTestResult(
            true,
            Math.Max(1, sw.ElapsedMilliseconds),
            resolved.Endpoint,
            resolved.Model,
            preview,
            null);
    }


    public async Task<AiStructuredCompletionResult> CompleteJsonAsync(
        string userPrompt,
        string? systemPrompt = null,
        string? providerIdOverride = null,
        AiTaskProfile profile = AiTaskProfile.FastMicroTask,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        int? maxOutputTokens = null)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                Localized.T("CustomWidget.AiRun.Error.EmptyPrompt"),
                string.Empty,
                string.Empty,
                0);
        }

        string? activeProviderId = ResolveActiveProviderId(providerIdOverride);
        if (string.IsNullOrWhiteSpace(activeProviderId))
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                Localized.T("CustomWidget.AiApi.Error.MissingConfiguredApiKey"),
                providerIdOverride?.Trim() ?? string.Empty,
                string.Empty,
                0);
        }

        ResolvedAiInvocation resolved = ResolveEffectiveSettingsForProfile(
            activeProviderId,
            profile,
            out string? originalModelFallback);

        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                Localized.T("CustomWidget.AiApi.Error.MissingConfiguredApiKey"),
                resolved.Preset.Id,
                resolved.Model,
                0);
        }

        string effectiveSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultMicroTaskSystemPrompt
            : systemPrompt.Trim();

        AiContextBudget budget = AiContextBudgetResolver.Resolve(
            resolved,
            GetKnownContextTokens(resolved),
            estimatedPromptChars: effectiveSystemPrompt.Length + userPrompt.Length);

        int maxPromptChars = budget.EffectiveContextTokens <= 8_192
            ? 8_192
            : (profile == AiTaskProfile.StudioAuthoring
                ? Math.Clamp(budget.EffectiveContextTokens * 2, 8_192, 65_536)
                : MaxMicroTaskPromptChars);

        string boundedPrompt = userPrompt.Length > maxPromptChars
            ? userPrompt[..maxPromptChars]
            : userPrompt;

        int estimatedInputTokens = Math.Max(256, (effectiveSystemPrompt.Length + boundedPrompt.Length) / 3 + 256);
        int availableOutputTokens = Math.Max(256, budget.EffectiveContextTokens - estimatedInputTokens - 256);
        int defaultDesiredMaxOut = profile == AiTaskProfile.StudioAuthoring
            ? Math.Min(budget.MaxStudioOutputTokens, 4_096)
            : (AiProviderCatalog.IsReasoningOnlyModel(resolved.Model) ? 4_096 : 1_536);
        int effectiveMaxOutputTokens = Math.Min(maxOutputTokens ?? defaultDesiredMaxOut, availableOutputTokens);

        TimeSpan effectiveTimeout = timeout ?? (profile == AiTaskProfile.FastMicroTask
            ? TimeSpan.FromSeconds(35)
            : TimeSpan.FromSeconds(120));

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        (bool ok, string rawReply, string effectiveModel, string? error) = await SendStructuredCompletionWithFallbackAsync(
            resolved,
            originalModelFallback,
            effectiveSystemPrompt,
            boundedPrompt,
            timeoutCts.Token,
            cancellationToken,
            effectiveMaxOutputTokens);

        sw.Stop();
        long elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);

        if (!ok)
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                error ?? Localized.T("CustomWidget.AiApi.Error.InvocationFailed"),
                resolved.Preset.Id,
                effectiveModel,
                elapsedMs);
        }

        string extractedJson = AiChatTransportClient.ExtractStructuredJson(rawReply);
        if (string.IsNullOrWhiteSpace(extractedJson))
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                Localized.T("CustomWidget.AiApi.Error.NoJsonBlockReturned"),
                resolved.Preset.Id,
                effectiveModel,
                elapsedMs,
                RawText: rawReply);
        }

        return new AiStructuredCompletionResult(
            true,
            extractedJson,
            null,
            resolved.Preset.Id,
            effectiveModel,
            elapsedMs,
            RawText: rawReply);
    }

    private async Task<(bool Success, string Content, string EffectiveModel, string? Error)> SendStructuredCompletionWithFallbackAsync(
        ResolvedAiInvocation resolved,
        string? originalModelFallback,
        string systemPrompt,
        string userPrompt,
        CancellationToken requestToken,
        CancellationToken callerToken,
        int? maxOutputTokens = null)
    {
        bool includeThinkingParams = resolved.ThinkingMode != AiThinkingMode.Auto && !IsThinkingParamsUnsupportedRoute(resolved);
        (bool ok, int statusCode, string content, string? err) = await AiChatTransportClient.SendSingleNonStreamingCompletionAsync(
            this,
            resolved,
            systemPrompt,
            userPrompt,
            includeThinkingParams,
            isProbe: false,
            requestToken,
            callerToken,
            maxOutputTokens);

        if (ok)
        {
            return (true, content, resolved.Model, null);
        }

        if (statusCode is 400 or 422 &&
            AiContextBudgetResolver.ErrorLooksLikeContextOverflow(err, out int? parsedLimit))
        {
            int learnedLimit = parsedLimit ?? 16_384;
            RecordLearnedContextLimit(resolved, learnedLimit);
            int compactPromptChars = Math.Clamp(learnedLimit * 2, 2_048, MaxMicroTaskPromptChars);
            string compactPrompt = userPrompt.Length > compactPromptChars ? userPrompt[..compactPromptChars] : userPrompt;
            int compactMaxOut = Math.Clamp(learnedLimit / 4, 256, maxOutputTokens ?? 1_536);

            (bool ctxOk, int ctxCode, string ctxContent, string? ctxErr) = await AiChatTransportClient.SendSingleNonStreamingCompletionAsync(
                this,
                resolved,
                systemPrompt,
                compactPrompt,
                includeThinkingParams: false,
                isProbe: false,
                requestToken,
                callerToken,
                compactMaxOut,
                omitTemperature: true);
            if (ctxOk)
            {
                return (true, ctxContent, resolved.Model, null);
            }

            statusCode = ctxCode;
            err = ctxErr;
        }

        // Fallback 1: Strip vendor-specific thinking parameters & fixed temperature constraints if rejected with HTTP 400 / 422.
        if (statusCode is 400 or 422)
        {
            if (includeThinkingParams && AiChatTransportClient.ErrorMentionsUnsupportedThinkingParams(err))
            {
                MarkThinkingParamsUnsupportedRoute(resolved);
            }

            (bool retryOk, int retryCode, string retryContent, string? retryErr) = await AiChatTransportClient.SendSingleNonStreamingCompletionAsync(
                this,
                resolved,
                systemPrompt,
                userPrompt,
                includeThinkingParams: false,
                isProbe: false,
                requestToken,
                callerToken,
                maxOutputTokens,
                omitTemperature: true);

            if (retryOk)
            {
                return (true, retryContent, resolved.Model, null);
            }

            statusCode = retryCode;
            err = retryErr;
        }

        // Fallback 2: If FastMicroTask swapped a reasoning model to its fast sibling and a model-restricted relay rejected it, retry original model.
        if (!string.IsNullOrWhiteSpace(originalModelFallback) &&
            !string.Equals(originalModelFallback, resolved.Model, StringComparison.OrdinalIgnoreCase) &&
            statusCode is 400 or 403 or 404 or 422)
        {
            ResolvedAiInvocation fallbackInvocation = resolved with
            {
                Model = originalModelFallback,
                ThinkingMode = AiThinkingMode.Auto
            };

            (bool modelRetryOk, _, string modelRetryContent, string? modelRetryErr) = await AiChatTransportClient.SendSingleNonStreamingCompletionAsync(
                this,
                fallbackInvocation,
                systemPrompt,
                userPrompt,
                includeThinkingParams: false,
                isProbe: false,
                requestToken,
                callerToken,
                maxOutputTokens,
                omitTemperature: true);

            if (modelRetryOk)
            {
                return (true, modelRetryContent, fallbackInvocation.Model, null);
            }

            err = modelRetryErr ?? err;
            return (false, string.Empty, fallbackInvocation.Model, err);
        }

        return (false, string.Empty, resolved.Model, err);
    }

    public static bool SupportsClassicDeepSeekModelSwap(
        CustomWidgetAiProviderPreset preset,
        IReadOnlyList<string>? availableModels = null) =>
        AiProviderCatalog.SupportsClassicDeepSeekModelSwap(preset, availableModels);

    public static (string CustomModel, string ThinkingWire) SynchronizeModelAndThinkingSelection(
        CustomWidgetAiProviderPreset preset,
        string? existingCustomModel,
        string? existingThinkingWire,
        string? newChosenModel = null,
        string? newThinkingWire = null,
        IReadOnlyList<string>? availableModels = null) =>
        AiProviderCatalog.SynchronizeModelAndThinkingSelection(
            preset,
            existingCustomModel,
            existingThinkingWire,
            newChosenModel,
            newThinkingWire,
            availableModels);

    internal sealed class SameOriginAuthSafeRedirectHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        private const int MaxRedirects = 5;

        private static bool IsSensitiveAuthHeader(string headerName) =>
            string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "x-api-key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "api-key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "x-goog-api-key", StringComparison.OrdinalIgnoreCase);

        private static bool HasRealNetworkTransport(HttpMessageHandler? handler)
        {
            while (handler is DelegatingHandler delegating)
            {
                handler = delegating.InnerHandler;
            }

            return handler is SocketsHttpHandler or HttpClientHandler;
        }

        private static async Task<bool> IsLoopbackOrPrivateHostAsync(
            Uri uri,
            bool resolveDns,
            CancellationToken cancellationToken)
        {
            string httpsProbeUrl = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1
            }.Uri.AbsoluteUri;
            if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(httpsProbeUrl, out _, out _))
            {
                return true;
            }

            string host = uri.DnsSafeHost.Trim().TrimEnd('.');
            if (resolveDns && !IPAddress.TryParse(host, out _))
            {
                try
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                    if (addresses.Any(CustomWidgetNetworkSecurity.IsBlockedIpAddress))
                    {
                        return true;
                    }
                }
                catch (System.Net.Sockets.SocketException)
                {
                }
            }

            return false;
        }

        private static async Task<bool> IsValidPublicRedirectTargetAsync(
            Uri nextUri,
            bool failClosedOnDnsError,
            CancellationToken cancellationToken)
        {
            if (!CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(nextUri.AbsoluteUri, out _, out _))
            {
                return false;
            }

            string host = nextUri.DnsSafeHost.Trim().TrimEnd('.');
            if (!IPAddress.TryParse(host, out _))
            {
                try
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                    if (addresses.Length == 0 || addresses.Any(CustomWidgetNetworkSecurity.IsBlockedIpAddress))
                    {
                        return false;
                    }
                }
                catch (System.Net.Sockets.SocketException)
                {
                    if (failClosedOnDnsError)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri? initialUri = request.RequestUri;
            if (initialUri is null)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            byte[]? bufferedBody = null;
            string? contentTypeMediaType = null;
            string? contentTypeCharSet = null;
            if (request.Content is not null)
            {
                bufferedBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                contentTypeMediaType = request.Content.Headers.ContentType?.MediaType;
                contentTypeCharSet = request.Content.Headers.ContentType?.CharSet;
            }

            var originalHeaders = new List<(string Key, string[] Values)>();
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                originalHeaders.Add((header.Key, header.Value.ToArray()));
            }

            bool hasRealTransport = HasRealNetworkTransport(InnerHandler);
            bool? initialIsLoopbackOrPrivate = null;
            bool crossedOrigin = false;
            HttpMethod currentMethod = request.Method;
            HttpRequestMessage currentRequest = request;
            bool ownsCurrentRequest = false;

            try
            {
                for (int hop = 0; ; hop++)
                {
                    HttpResponseMessage response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);
                    int status = (int)response.StatusCode;
                    if (hop >= MaxRedirects ||
                        status is not (301 or 302 or 303 or 307 or 308) ||
                        response.Headers.Location is not { } location)
                    {
                        return response;
                    }

                    Uri currentUri = currentRequest.RequestUri ?? initialUri;
                    Uri? nextUri = location.IsAbsoluteUri
                        ? location
                        : (Uri.TryCreate(currentUri, location, out Uri? resolved) ? resolved : null);
                    if (nextUri is null ||
                        !string.IsNullOrEmpty(nextUri.UserInfo) ||
                        (!string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(nextUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
                        ((string.Equals(initialUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                         string.Equals(nextUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
                    {
                        return response;
                    }

                    bool hopSameOrigin = Uri.Compare(
                        initialUri,
                        nextUri,
                        UriComponents.SchemeAndServer,
                        UriFormat.Unescaped,
                        StringComparison.OrdinalIgnoreCase) == 0;
                    if (!hopSameOrigin)
                    {
                        crossedOrigin = true;
                    }

                    if (crossedOrigin)
                    {
                        initialIsLoopbackOrPrivate ??= await IsLoopbackOrPrivateHostAsync(
                            initialUri,
                            resolveDns: hasRealTransport,
                            cancellationToken).ConfigureAwait(false);
                        if (!initialIsLoopbackOrPrivate.Value &&
                            !await IsValidPublicRedirectTargetAsync(
                                nextUri,
                                failClosedOnDnsError: hasRealTransport,
                                cancellationToken).ConfigureAwait(false))
                        {
                            return response;
                        }
                    }

                    response.Dispose();

                    bool preserveMethodAndBody = !crossedOrigin && status is 307 or 308;
                    if (!preserveMethodAndBody && currentMethod != HttpMethod.Head)
                    {
                        currentMethod = HttpMethod.Get;
                        bufferedBody = null;
                    }

                    if (ownsCurrentRequest)
                    {
                        currentRequest.Dispose();
                    }

                    var redirected = new HttpRequestMessage(currentMethod, nextUri)
                    {
                        Version = request.Version,
                        VersionPolicy = request.VersionPolicy
                    };
                    foreach (KeyValuePair<string, object?> opt in request.Options)
                    {
                        ((IDictionary<string, object?>)redirected.Options)[opt.Key] = opt.Value;
                    }

                    if (crossedOrigin && initialIsLoopbackOrPrivate == false)
                    {
                        redirected.Options.Set(CustomWidgetNetworkSecurity.RequirePublicIpOnlyOption, true);
                    }

                    foreach ((string key, string[] values) in originalHeaders)
                    {
                        if (crossedOrigin && IsSensitiveAuthHeader(key))
                        {
                            continue;
                        }

                        redirected.Headers.TryAddWithoutValidation(key, values);
                    }

                    if (preserveMethodAndBody && bufferedBody is not null)
                    {
                        var bodyContent = new ByteArrayContent(bufferedBody);
                        if (!string.IsNullOrEmpty(contentTypeMediaType))
                        {
                            bodyContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentTypeMediaType)
                            {
                                CharSet = contentTypeCharSet
                            };
                        }

                        redirected.Content = bodyContent;
                    }

                    currentRequest = redirected;
                    ownsCurrentRequest = true;
                }
            }
            finally
            {
                if (ownsCurrentRequest)
                {
                    currentRequest.Dispose();
                }
            }
        }
    }
}
