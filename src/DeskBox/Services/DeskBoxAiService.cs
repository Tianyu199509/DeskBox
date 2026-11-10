using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Execution profile for DeskBox AI requests, inspired by role-based model routing:
/// <list type="bullet">
/// <item><see cref="StudioAuthoring"/>: Widget creation/refinement in Studio; honors user thinking settings and supports long SSE streams.</item>
/// <item><see cref="FastMicroTask"/>: Runtime <c>ai.run</c> actions and quick desktop micro-tasks; automatically routes to low-latency non-thinking parameters/models.</item>
/// </list>
/// </summary>
public enum AiTaskProfile
{
    StudioAuthoring = 0,
    FastMicroTask = 1
}

public sealed record AiStructuredCompletionResult(
    bool Success,
    string Json,
    string? ErrorMessage,
    string ProviderId,
    string EffectiveModel,
    long LatencyMs);

public sealed record AiCallTelemetrySnapshot(
    long TotalCalls,
    long AutoRepairedCalls,
    long TotalPromptTokens,
    long TotalCachedPromptTokens,
    long TotalCompletionTokens,
    long LastCallLatencyMs,
    double CacheHitRatio);

/// <summary>
/// Global NativeAOT-safe AI substrate for DeskBox. Owns provider presets, DPAPI credential resolution,
/// role-based model/thinking routing (<see cref="AiTaskProfile"/>), connectivity probes, model discovery,
/// SSE streaming, and fast structured JSON completion (<see cref="CompleteJsonAsync"/>).
/// </summary>
public sealed class DeskBoxAiService
{
    public const string CustomProviderId = CustomWidgetAiGeneratorService.CustomProviderId;
    public const int MaxMicroTaskPromptChars = 16_384;
    public const int MaxPromptChars = MaxMicroTaskPromptChars;

    public static IReadOnlyList<CustomWidgetAiProviderPreset> SupportedProviders =>
        CustomWidgetAiGeneratorService.SupportedProviders;

    public static IReadOnlyList<CustomWidgetAiProviderPreset> GetOrderedProvidersForCulture(string? cultureName = null) =>
        CustomWidgetAiGeneratorService.GetOrderedProvidersForCulture(cultureName);

    public static string GetDefaultStarterProviderId(string? cultureName = null) =>
        CustomWidgetAiGeneratorService.GetDefaultStarterProviderId(cultureName);

    public static bool IsSimplifiedChineseRegion(string? cultureName = null) =>
        CustomWidgetAiGeneratorService.IsSimplifiedChineseRegion(cultureName);

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
        CustomWidgetAiGeneratorService.FindProviderPreset(providerId);

    public static AiThinkingMode ParseThinkingMode(string? raw) =>
        CustomWidgetAiGeneratorService.ParseThinkingMode(raw);

    public static string ToThinkingWireValue(AiThinkingMode mode) =>
        CustomWidgetAiGeneratorService.ToThinkingWireValue(mode);

    private const string DefaultMicroTaskSystemPrompt =
        "You are a fast, precise structured JSON assistant inside DeskBox. " +
        "Respond ONLY with a single valid JSON object (or JSON array when explicitly requested). " +
        "Never include markdown explanations, code commentary, or surrounding prose.";

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        // Per-request and per-chunk cancellation tokens enforce bounded timeouts
        // without killing long Studio reasoning streams prematurely.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly CustomWidgetAiCredentialStore _credentialStore;
    private readonly CustomWidgetStudioPreferencesStore _preferencesStore;
    private readonly HttpClient _httpClient;

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
        _httpClient = httpClient ?? SharedHttpClient;
    }

    internal CustomWidgetAiCredentialStore CredentialStore => _credentialStore;

    internal HttpClient HttpClient => _httpClient;

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
        foreach (CustomWidgetAiProviderPreset preset in CustomWidgetAiGeneratorService.SupportedProviders)
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
            CustomWidgetAiProviderPreset explicitPreset = CustomWidgetAiGeneratorService.FindProviderPreset(providerIdOverride);
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

    public ResolvedAiInvocation ResolveEffectiveSettings(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null)
    {
        CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = _credentialStore.Get(preset.Id);

        string apiKey = (apiKeyOverride ?? entry?.ApiKey)?.Trim() ?? string.Empty;
        AiThinkingMode thinkingMode = thinkingOverride ?? CustomWidgetAiGeneratorService.ParseThinkingMode(entry?.ThinkingMode);

        string rawCustomEndpoint = (endpointOverride ?? entry?.CustomEndpoint)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawCustomEndpoint) &&
            string.Equals(preset.Id, CustomProviderId, StringComparison.OrdinalIgnoreCase) &&
            entry?.Region is { Length: > 0 } legacyRegion &&
            legacyRegion.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            rawCustomEndpoint = legacyRegion.Trim();
        }

        string endpoint;
        bool usesCustomEndpoint = !string.IsNullOrWhiteSpace(rawCustomEndpoint);
        if (usesCustomEndpoint)
        {
            endpoint = CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint(rawCustomEndpoint);
        }
        else if (!string.IsNullOrWhiteSpace(preset.GlobalDefaultEndpoint) &&
                 (string.Equals(entry?.Region, "global", StringComparison.OrdinalIgnoreCase) ||
                  (string.IsNullOrWhiteSpace(entry?.Region) &&
                   !CustomWidgetAiGeneratorService.IsSimplifiedChineseRegion(LocalizationService.ResolvePersistedOrAmbientCultureName()))))
        {
            endpoint = preset.GlobalDefaultEndpoint;
        }
        else
        {
            endpoint = preset.DefaultEndpoint;
        }

        string rawCustomModel = (modelOverride ?? entry?.CustomModel)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawCustomModel) &&
            string.Equals(preset.Id, CustomProviderId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(entry?.PlanTier) &&
            !string.Equals(entry.PlanTier, "pro", StringComparison.OrdinalIgnoreCase))
        {
            rawCustomModel = entry.PlanTier.Trim();
        }

        string model = string.IsNullOrWhiteSpace(rawCustomModel) ? preset.DefaultModel : rawCustomModel;

        // DeepSeek's official endpoint switches thinking via model ID (deepseek-chat <-> deepseek-reasoner).
        if (preset.ThinkingProtocol == AiThinkingProtocol.DeepSeekModelSwap ||
            (preset.ThinkingProtocol == AiThinkingProtocol.AutoDetectByModel &&
             (string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))))
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

        return new ResolvedAiInvocation(preset, endpoint, model, apiKey, thinkingMode, usesCustomEndpoint);
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
            return ResolveEffectiveSettings(providerId);
        }

        ResolvedAiInvocation baseStudio = ResolveEffectiveSettings(providerId);
        ResolvedAiInvocation fastResolved = ResolveEffectiveSettings(
            providerId,
            thinkingOverride: AiThinkingMode.Disabled);

        string? fastSibling = TryResolveFastSiblingModel(fastResolved.Preset, fastResolved.Model, fastResolved.UsesCustomEndpoint);
        if (!string.IsNullOrWhiteSpace(fastSibling) &&
            !string.Equals(fastSibling, fastResolved.Model, StringComparison.OrdinalIgnoreCase))
        {
            originalModelFallback = fastResolved.Model;
            fastResolved = fastResolved with { Model = fastSibling };
        }
        else if (!string.Equals(baseStudio.Model, fastResolved.Model, StringComparison.OrdinalIgnoreCase))
        {
            originalModelFallback = baseStudio.Model;
        }

        return fastResolved;
    }

    internal static string? TryResolveFastSiblingModel(
        CustomWidgetAiProviderPreset preset,
        string currentModel,
        bool usesCustomEndpoint)
    {
        if (string.IsNullOrWhiteSpace(currentModel))
        {
            return null;
        }

        string trimmed = currentModel.Trim();
        if (string.Equals(trimmed, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
        {
            return "deepseek-chat";
        }

        if (usesCustomEndpoint)
        {
            return null;
        }

        if (string.Equals(preset.Id, "siliconflow", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "deepseek-ai/DeepSeek-R1", StringComparison.OrdinalIgnoreCase))
        {
            return "deepseek-ai/DeepSeek-V3";
        }

        if (string.Equals(preset.Id, "openrouter", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "deepseek/deepseek-r1", StringComparison.OrdinalIgnoreCase))
        {
            return "deepseek/deepseek-chat-v3-0324";
        }

        if (string.Equals(preset.Id, "qwen", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "qwq-32b", StringComparison.OrdinalIgnoreCase))
        {
            return "qwen-plus";
        }

        if (string.Equals(preset.Id, "glm", StringComparison.OrdinalIgnoreCase) &&
            trimmed.StartsWith("glm-z1", StringComparison.OrdinalIgnoreCase))
        {
            return "glm-4.5-air";
        }

        if (string.Equals(preset.Id, "kimi", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "kimi-thinking-preview", StringComparison.OrdinalIgnoreCase))
        {
            return "kimi-k2-0711-preview";
        }

        if (string.Equals(preset.Id, "doubao", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "deepseek-r1-250528", StringComparison.OrdinalIgnoreCase))
        {
            return "deepseek-v3-250324";
        }

        if (string.Equals(preset.Id, "openai", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            {
                return "gpt-4.1-mini";
            }

            if (string.Equals(trimmed, "gpt-5", StringComparison.OrdinalIgnoreCase))
            {
                return "gpt-5-mini";
            }
        }

        if (string.Equals(preset.Id, "gemini", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmed, "gemini-2.5-pro", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini-2.5-flash";
        }

        if (string.Equals(preset.Id, "anthropic", StringComparison.OrdinalIgnoreCase) &&
            trimmed.StartsWith("claude-opus-", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-haiku-4-5";
        }

        return null;
    }

    public async Task<AiModelListResult> FetchAvailableModelsAsync(
        string providerId,
        string? endpointOverride = null,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default)
    {
        ResolvedAiInvocation resolved = ResolveEffectiveSettings(
            providerId,
            endpointOverride: endpointOverride,
            apiKeyOverride: apiKeyOverride);

        if (resolved.Preset.RequiresApiKey && string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.EnterApiKeyFirst"));
        }

        string modelsUrl = CustomWidgetAiGeneratorService.DeriveModelsEndpoint(resolved.Endpoint);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            ApplyAuthHeaders(req, resolved.Endpoint, resolved.ApiKey);

            using HttpResponseMessage resp = await _httpClient.SendAsync(req, timeoutCts.Token);
            string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string err = FormatHttpError(resp.StatusCode, body);
                return new AiModelListResult(false, resolved.Preset.Models, err);
            }

            List<string> discovered = ParseModelListJson(body);
            if (discovered.Count == 0)
            {
                return new AiModelListResult(false, resolved.Preset.Models, Localized.T("CustomWidget.AiApi.Error.NoModelsReturned"));
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

        (bool ok, string reply, string? error) = await SendProbeCompletionAsync(
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

    private async Task<(bool Success, string Content, string? Error)> SendProbeCompletionAsync(
        ResolvedAiInvocation resolved,
        bool includeThinkingParams,
        CancellationToken requestToken,
        CancellationToken callerToken = default)
    {
        (bool ok, int statusCode, string content, string? err) = await SendSingleNonStreamingCompletionAsync(
            resolved,
            systemPrompt: null,
            userPrompt: "Reply with OK.",
            includeThinkingParams: includeThinkingParams,
            isProbe: true,
            requestToken: requestToken,
            callerToken: callerToken);

        if (!ok && includeThinkingParams && (statusCode == 400 || statusCode == 422))
        {
            return await SendProbeCompletionAsync(resolved, includeThinkingParams: false, requestToken, callerToken);
        }

        return (ok, string.IsNullOrWhiteSpace(content) ? "OK" : content, err);
    }

    private static List<string> ParseModelListJson(string rawJson)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return list;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            JsonElement arrayEl = default;
            bool hasArray = false;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out arrayEl) && arrayEl.ValueKind == JsonValueKind.Array)
                {
                    hasArray = true;
                }
                else if (root.TryGetProperty("models", out arrayEl) && arrayEl.ValueKind == JsonValueKind.Array)
                {
                    hasArray = true;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                arrayEl = root;
                hasArray = true;
            }

            if (!hasArray)
            {
                return list;
            }

            foreach (JsonElement item in arrayEl.EnumerateArray())
            {
                string? id = null;
                if (item.ValueKind == JsonValueKind.String)
                {
                    id = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        id = idEl.GetString();
                    }
                    else if (item.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        id = nameEl.GetString();
                    }
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string cleaned = id.Trim();
                if (cleaned.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned["models/".Length..];
                }

                if (IsNonChatModelId(cleaned))
                {
                    continue;
                }

                list.Add(cleaned);
            }
        }
        catch
        {
        }

        return list;
    }

    private static bool IsNonChatModelId(string modelId)
    {
        string lower = modelId.ToLowerInvariant();
        return lower.Contains("embedding", StringComparison.Ordinal) ||
               lower.Contains("rerank", StringComparison.Ordinal) ||
               lower.Contains("whisper", StringComparison.Ordinal) ||
               lower.Contains("tts", StringComparison.Ordinal) ||
               lower.Contains("dall-e", StringComparison.Ordinal) ||
               lower.Contains("moderation", StringComparison.Ordinal) ||
               lower.StartsWith("babbage-", StringComparison.Ordinal) ||
               lower.StartsWith("davinci-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Executes a bounded, AOT-safe structured JSON completion using role-based routing
    /// (defaults to <see cref="AiTaskProfile.FastMicroTask"/>).
    /// </summary>
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

        string boundedPrompt = userPrompt.Length > MaxMicroTaskPromptChars
            ? userPrompt[..MaxMicroTaskPromptChars]
            : userPrompt;
        string effectiveSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultMicroTaskSystemPrompt
            : systemPrompt.Trim();

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
            maxOutputTokens);

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

        string extractedJson = ExtractStructuredJson(rawReply);
        if (string.IsNullOrWhiteSpace(extractedJson))
        {
            return new AiStructuredCompletionResult(
                false,
                string.Empty,
                Localized.T("CustomWidget.AiApi.Error.NoJsonBlockReturned"),
                resolved.Preset.Id,
                effectiveModel,
                elapsedMs);
        }

        return new AiStructuredCompletionResult(
            true,
            extractedJson,
            null,
            resolved.Preset.Id,
            effectiveModel,
            elapsedMs);
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
        bool includeThinkingParams = resolved.ThinkingMode != AiThinkingMode.Auto;
        (bool ok, int statusCode, string content, string? err) = await SendSingleNonStreamingCompletionAsync(
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

        // Fallback 1: Strip vendor-specific thinking parameters if rejected with HTTP 400 / 422.
        if (includeThinkingParams && (statusCode == 400 || statusCode == 422))
        {
            (bool retryOk, int retryCode, string retryContent, string? retryErr) = await SendSingleNonStreamingCompletionAsync(
                resolved,
                systemPrompt,
                userPrompt,
                includeThinkingParams: false,
                isProbe: false,
                requestToken,
                callerToken,
                maxOutputTokens);

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

            (bool modelRetryOk, _, string modelRetryContent, string? modelRetryErr) = await SendSingleNonStreamingCompletionAsync(
                fallbackInvocation,
                systemPrompt,
                userPrompt,
                includeThinkingParams: false,
                isProbe: false,
                requestToken,
                callerToken,
                maxOutputTokens);

            if (modelRetryOk)
            {
                return (true, modelRetryContent, fallbackInvocation.Model, null);
            }

            err = modelRetryErr ?? err;
            return (false, string.Empty, fallbackInvocation.Model, err);
        }

        return (false, string.Empty, resolved.Model, err);
    }

    internal async Task<(bool Success, int StatusCode, string Content, string? Error)> SendSingleNonStreamingCompletionAsync(
        ResolvedAiInvocation resolved,
        string? systemPrompt,
        string userPrompt,
        bool includeThinkingParams,
        bool isProbe,
        CancellationToken requestToken,
        CancellationToken callerToken,
        int? maxOutputTokens = null)
    {
        try
        {
            byte[] payload = BuildChatRequestPayload(
                resolved,
                systemPrompt: systemPrompt,
                userPrompt: userPrompt,
                assistantPrefill: null,
                repairPrompt: null,
                stream: false,
                includeThinkingParams: includeThinkingParams,
                isProbe: isProbe,
                capMicroTaskTokens: !isProbe,
                microTaskMaxTokensOverride: maxOutputTokens);

            using var req = new HttpRequestMessage(HttpMethod.Post, resolved.Endpoint)
            {
                Content = CreateJsonContent(payload)
            };
            ApplyAuthHeaders(req, resolved.Endpoint, resolved.ApiKey);

            var callSw = Stopwatch.StartNew();
            using HttpResponseMessage resp = await _httpClient.SendAsync(req, requestToken);
            string respBody = await resp.Content.ReadAsStringAsync(requestToken);
            callSw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                return (false, (int)resp.StatusCode, string.Empty, FormatHttpError(resp.StatusCode, respBody));
            }

            if (TryExtractNonStreamingReply(respBody, out string content, out string reasoning))
            {
                CustomWidgetAiGeneratorService.SplitInlineThinkBlock(content, out _, out string visibleContent, out _);
                if (!string.IsNullOrWhiteSpace(visibleContent))
                {
                    if (!isProbe)
                    {
                        TryExtractUsageFromJson(respBody, out int pt, out int ctCached, out int comp);
                        RecordCallTelemetry(pt, ctCached, comp, Math.Max(1, callSw.ElapsedMilliseconds));
                    }

                    return (true, (int)resp.StatusCode, visibleContent, null);
                }

                if (isProbe)
                {
                    return (true, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(reasoning) ? reasoning : "OK", null);
                }

                return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
            }

            // Compatibility with OpenAI-compatible relays that return SSE chunks even when stream=false.
            if (respBody.Contains("data:", StringComparison.OrdinalIgnoreCase) &&
                TryExtractSseFullReply(respBody, out string sseContent, out string sseReasoning))
            {
                CustomWidgetAiGeneratorService.SplitInlineThinkBlock(sseContent, out _, out string visibleSse, out _);
                if (!string.IsNullOrWhiteSpace(visibleSse))
                {
                    return (true, (int)resp.StatusCode, visibleSse, null);
                }

                if (isProbe)
                {
                    return (true, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(sseReasoning) ? sseReasoning : "OK", null);
                }

                return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.EmptyContent"));
            }

            if (TryExtractStructuredJsonErrorMessage(respBody) is { Length: > 0 } http200Err)
            {
                return (false, (int)resp.StatusCode, string.Empty, http200Err);
            }

            if (isProbe)
            {
                return (true, (int)resp.StatusCode, "OK", null);
            }

            return (false, (int)resp.StatusCode, string.Empty, Localized.T("CustomWidget.AiApi.Error.MalformedResponse"));
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            return (false, 0, string.Empty, Localized.T(
                isProbe ? "CustomWidget.AiApi.Error.ProbeCancelled" : "CustomWidget.AiApi.Error.GenerationCancelled"));
        }
        catch (OperationCanceledException)
        {
            return (false, 0, string.Empty, Localized.T(
                isProbe ? "CustomWidget.AiApi.Error.ProbeTimeout" : "CustomWidget.AiApi.Error.StreamTimeout"));
        }
        catch (Exception ex)
        {
            return (false, 0, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Extracts a valid JSON object (<c>{...}</c>) or array (<c>[...]</c>) from model output,
    /// stripping <c>&lt;think&gt;</c> reasoning blocks and Markdown code fences.
    /// </summary>
    public static string ExtractStructuredJson(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string cleaned = StripAllThinkBlocks(rawText).Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        // 1. Prefer Markdown fenced blocks (```json ... ``` or ``` ... ```)
        int fenceStart = cleaned.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            int lineBreak = cleaned.IndexOf('\n', fenceStart + 3);
            if (lineBreak < 0)
            {
                break;
            }

            int fenceEnd = cleaned.IndexOf("```", lineBreak + 1, StringComparison.Ordinal);
            if (fenceEnd > lineBreak)
            {
                string candidate = cleaned[(lineBreak + 1)..fenceEnd].Trim();
                string extracted = ExtractBalancedJsonValue(candidate);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }

            fenceStart = fenceEnd >= 0
                ? cleaned.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal)
                : -1;
        }

        // 2. Fallback: extract first balanced JSON object or array in plain/mixed output
        return ExtractBalancedJsonValue(cleaned);
    }

    private static string StripAllThinkBlocks(string text)
    {
        ReadOnlySpan<(string Open, string Close)> tags =
        [
            ("<think>", "</think>"),
            ("<thinking>", "</thinking>"),
            ("<thought>", "</thought>")
        ];

        string current = text;
        for (int guard = 0; guard < 12; guard++)
        {
            int earliestOpen = -1;
            string matchedOpen = string.Empty;
            string matchedClose = string.Empty;
            foreach ((string openTag, string closeTag) in tags)
            {
                int idx = current.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (earliestOpen < 0 || idx < earliestOpen))
                {
                    earliestOpen = idx;
                    matchedOpen = openTag;
                    matchedClose = closeTag;
                }
            }

            if (earliestOpen < 0)
            {
                break;
            }

            int afterOpen = earliestOpen + matchedOpen.Length;
            int closeIdx = current.IndexOf(matchedClose, afterOpen, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                current = current[..earliestOpen];
                break;
            }

            current = current[..earliestOpen] + current[(closeIdx + matchedClose.Length)..];
        }

        return current;
    }

    private static string ExtractBalancedJsonValue(string text)
    {
        int firstObj = text.IndexOf('{');
        int firstArr = text.IndexOf('[');
        if (firstObj < 0 && firstArr < 0)
        {
            return string.Empty;
        }

        int primaryStart;
        char openChar;
        char closeChar;
        if (firstObj >= 0 && (firstArr < 0 || firstObj < firstArr))
        {
            primaryStart = firstObj;
            openChar = '{';
            closeChar = '}';
        }
        else
        {
            primaryStart = firstArr;
            openChar = '[';
            closeChar = ']';
        }

        string candidate = SliceBalanced(text, primaryStart, openChar, closeChar);
        if (IsValidJsonDocument(candidate))
        {
            return candidate;
        }

        // Try secondary delimiter if e.g. prose contained "[1]" before the actual "{...}" payload
        if (openChar == '[' && firstObj >= 0)
        {
            string objCandidate = SliceBalanced(text, firstObj, '{', '}');
            if (IsValidJsonDocument(objCandidate))
            {
                return objCandidate;
            }
        }
        else if (openChar == '{' && firstArr >= 0)
        {
            string arrCandidate = SliceBalanced(text, firstArr, '[', ']');
            if (IsValidJsonDocument(arrCandidate))
            {
                return arrCandidate;
            }
        }

        return string.Empty;
    }

    private static string SliceBalanced(string text, int startIndex, char openChar, char closeChar)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == openChar)
            {
                depth++;
            }
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                {
                    return text[startIndex..(i + 1)];
                }
            }
        }

        return string.Empty;
    }

    private static bool IsValidJsonDocument(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch
        {
            return false;
        }
    }

    internal static byte[] BuildChatRequestPayload(
        ResolvedAiInvocation resolved,
        string? systemPrompt,
        string userPrompt,
        string? assistantPrefill,
        string? repairPrompt,
        bool stream,
        bool includeThinkingParams,
        bool isProbe,
        bool capMicroTaskTokens = false,
        int? microTaskMaxTokensOverride = null,
        bool includeExplicitStudioMaxTokens = false,
        int? studioMaxTokensOverride = null,
        IReadOnlyList<(string Role, string Content)>? extraTurns = null)
    {
        bool reasoningOnly = CustomWidgetAiGeneratorService.IsReasoningOnlyModel(resolved.Model);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", resolved.Model);
            if (!reasoningOnly)
            {
                writer.WriteNumber("temperature", 0.2);
            }

            writer.WriteBoolean("stream", stream);
            if (isProbe)
            {
                if (reasoningOnly)
                {
                    writer.WriteNumber("max_completion_tokens", 64);
                }
                else
                {
                    writer.WriteNumber("max_tokens", 16);
                }
            }
            else if (capMicroTaskTokens)
            {
                if (reasoningOnly)
                {
                    int reasoningCap = Math.Clamp(microTaskMaxTokensOverride ?? 4096, 512, 8192);
                    writer.WriteNumber("max_completion_tokens", reasoningCap);
                }
                else
                {
                    int microCap = Math.Clamp(microTaskMaxTokensOverride ?? 1536, 256, 4096);
                    writer.WriteNumber("max_tokens", microCap);
                }
            }
            else if (includeExplicitStudioMaxTokens)
            {
                int defaultStudioTokens = IsStrict8kStudioEndpoint(resolved) ? 8192 : 16384;
                int effectiveStudioTokens = Math.Clamp(studioMaxTokensOverride ?? defaultStudioTokens, 2048, 32768);
                if (reasoningOnly)
                {
                    writer.WriteNumber("max_completion_tokens", effectiveStudioTokens);
                }
                else
                {
                    writer.WriteNumber("max_tokens", effectiveStudioTokens);
                }
            }

            if (includeThinkingParams && resolved.ThinkingMode != AiThinkingMode.Auto)
            {
                WriteVendorThinkingParameters(writer, resolved.Preset.ThinkingProtocol, resolved.Model, resolved.ThinkingMode);
            }

            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                string modelLeaf = resolved.Model.Contains('/')
                    ? resolved.Model[(resolved.Model.LastIndexOf('/') + 1)..]
                    : resolved.Model;
                writer.WriteStartObject();
                writer.WriteString(
                    "role",
                    reasoningOnly && modelLeaf.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ? "user" : "system");
                writer.WriteString("content", systemPrompt);
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", userPrompt);
            writer.WriteEndObject();

            if (extraTurns is { Count: > 0 })
            {
                foreach ((string role, string content) in extraTurns)
                {
                    if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("role", role);
                    writer.WriteString("content", content);
                    writer.WriteEndObject();
                }
            }
            else if (!string.IsNullOrWhiteSpace(assistantPrefill) &&
                     !string.IsNullOrWhiteSpace(repairPrompt))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "assistant");
                writer.WriteString("content", assistantPrefill);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", repairPrompt);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    internal static bool IsStrict8kStudioEndpoint(ResolvedAiInvocation resolved)
    {
        // Official api.deepseek.com rejects max_tokens > 8192 on deepseek-chat, whereas
        // deepseek-reasoner, deepseek-flash relays, and modern hybrid thinking models
        // share max_tokens across reasoning + output and need 16384 to avoid truncation.
        if (resolved.Endpoint.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resolved.Model, "deepseek-chat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (resolved.Model.Contains("-8k", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static void WriteVendorThinkingParameters(
        Utf8JsonWriter writer,
        AiThinkingProtocol protocol,
        string model,
        AiThinkingMode mode)
    {
        bool enable = mode == AiThinkingMode.Enabled;
        AiThinkingProtocol effectiveProtocol = protocol;
        string lower = model.Trim().ToLowerInvariant();
        if (effectiveProtocol == AiThinkingProtocol.AutoDetectByModel)
        {
            if (lower.Contains("qwen", StringComparison.Ordinal))
            {
                effectiveProtocol = AiThinkingProtocol.EnableThinkingFlag;
            }
            else if (lower.Contains("glm", StringComparison.Ordinal) ||
                     lower.Contains("doubao", StringComparison.Ordinal) ||
                     (lower.Contains("deepseek", StringComparison.Ordinal) &&
                      !CustomWidgetAiGeneratorService.IsReasoningOnlyModel(model)))
            {
                effectiveProtocol = AiThinkingProtocol.ThinkingTypeObject;
            }
            else if (lower.StartsWith("o1", StringComparison.Ordinal) ||
                     lower.StartsWith("o3", StringComparison.Ordinal) ||
                     lower.StartsWith("o4", StringComparison.Ordinal) ||
                     lower.StartsWith("gpt-5", StringComparison.Ordinal) ||
                     lower.Contains("gemini", StringComparison.Ordinal))
            {
                effectiveProtocol = AiThinkingProtocol.ReasoningEffort;
            }
        }
        else if (effectiveProtocol == AiThinkingProtocol.DeepSeekModelSwap &&
                 !string.Equals(lower, "deepseek-chat", StringComparison.Ordinal) &&
                 !string.Equals(lower, "deepseek-reasoner", StringComparison.Ordinal) &&
                 !CustomWidgetAiGeneratorService.IsReasoningOnlyModel(model))
        {
            // Custom DeepSeek hybrid variants (e.g. deepseek-flash, deepseek-v3.1/v3.2)
            // control reasoning via thinking.type rather than model ID swapping.
            effectiveProtocol = AiThinkingProtocol.ThinkingTypeObject;
        }

        switch (effectiveProtocol)
        {
            case AiThinkingProtocol.EnableThinkingFlag:
                writer.WriteBoolean("enable_thinking", enable);
                break;

            case AiThinkingProtocol.ThinkingTypeObject:
                writer.WriteStartObject("thinking");
                writer.WriteString("type", enable ? "enabled" : "disabled");
                writer.WriteEndObject();
                break;

            case AiThinkingProtocol.OpenRouterReasoning:
                writer.WriteStartObject("reasoning");
                writer.WriteBoolean("enabled", enable);
                writer.WriteEndObject();
                break;

            case AiThinkingProtocol.ReasoningEffort:
                if (enable)
                {
                    writer.WriteString("reasoning_effort", "medium");
                }
                else if (model.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("reasoning_effort", "none");
                }
                else if (CustomWidgetAiGeneratorService.IsReasoningOnlyModel(model) &&
                         SupportsLowReasoningEffort(model))
                {
                    writer.WriteString("reasoning_effort", "low");
                }
                break;
        }
    }

    internal static bool SupportsLowReasoningEffort(string model)
    {
        string lower = model.Trim().ToLowerInvariant();
        string leaf = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        if (leaf.StartsWith("o1-mini", StringComparison.Ordinal) ||
            leaf.StartsWith("o1-preview", StringComparison.Ordinal) ||
            leaf.StartsWith("gpt-5-pro", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    internal static ByteArrayContent CreateJsonContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return content;
    }

    internal static void ApplyAuthHeaders(HttpRequestMessage req, string endpoint, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://deskbox.app");
            req.Headers.TryAddWithoutValidation("X-Title", "DeskBox");
        }
    }

    internal static bool TryAppendSseDelta(
        string dataJson,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        out string? streamError) =>
        TryAppendSseDelta(dataJson, contentBuilder, reasoningBuilder, out streamError, out _);

    internal static bool TryAppendSseDelta(
        string dataJson,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        out string? streamError,
        out string? finishReason)
    {
        streamError = null;
        finishReason = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out JsonElement errEl) &&
                errEl.ValueKind != JsonValueKind.Null &&
                errEl.ValueKind != JsonValueKind.Undefined)
            {
                streamError = TryExtractApiErrorMessage(dataJson) ?? Localized.T("CustomWidget.AiApi.Error.InvocationFailed");
                return false;
            }

            if (root.TryGetProperty("choices", out JsonElement choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("finish_reason", out JsonElement frEl) &&
                    frEl.ValueKind == JsonValueKind.String)
                {
                    string? fr = frEl.GetString();
                    if (!string.IsNullOrWhiteSpace(fr))
                    {
                        finishReason = fr;
                    }
                }

                bool appended = false;
                if (first.TryGetProperty("delta", out JsonElement delta) &&
                    delta.ValueKind == JsonValueKind.Object)
                {
                    appended |= TryAppendMessageLikeFields(delta, contentBuilder, reasoningBuilder);
                }

                // Some OpenAI-compatible relays emit an empty delta alongside a final
                // message object or top-level choice text in the closing SSE event.
                if (!appended &&
                    first.TryGetProperty("message", out JsonElement msg) &&
                    msg.ValueKind == JsonValueKind.Object)
                {
                    appended |= TryAppendMessageLikeFields(msg, contentBuilder, reasoningBuilder);
                }

                if (!appended &&
                    first.TryGetProperty("text", out JsonElement textEl))
                {
                    appended |= AppendContentElement(textEl, contentBuilder, reasoningBuilder);
                }

                return appended;
            }

            // OpenAI Responses-style SSE bridge events returned by modern API gateways.
            if (root.TryGetProperty("type", out JsonElement typeEl) &&
                typeEl.ValueKind == JsonValueKind.String)
            {
                string eventType = typeEl.GetString() ?? string.Empty;
                if (eventType.EndsWith("output_text.delta", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("delta", out JsonElement outDelta) &&
                    outDelta.ValueKind == JsonValueKind.String)
                {
                    string? chunk = outDelta.GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        contentBuilder.Append(chunk);
                        return true;
                    }
                }
                else if ((eventType.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
                          eventType.Contains("thinking", StringComparison.OrdinalIgnoreCase)) &&
                         eventType.EndsWith(".delta", StringComparison.OrdinalIgnoreCase) &&
                         root.TryGetProperty("delta", out JsonElement reasonDelta) &&
                         reasonDelta.ValueKind == JsonValueKind.String)
                {
                    string? chunk = reasonDelta.GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        reasoningBuilder.Append(chunk);
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAppendMessageLikeFields(
        JsonElement obj,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder)
    {
        bool appended = false;
        if (obj.TryGetProperty("reasoning_content", out JsonElement rcEl))
        {
            appended |= AppendReasoningElement(rcEl, reasoningBuilder);
        }
        else if (obj.TryGetProperty("reasoning", out JsonElement rEl))
        {
            appended |= AppendReasoningElement(rEl, reasoningBuilder);
        }
        else if (obj.TryGetProperty("thinking", out JsonElement tEl))
        {
            appended |= AppendReasoningElement(tEl, reasoningBuilder);
        }

        if (obj.TryGetProperty("content", out JsonElement cEl))
        {
            appended |= AppendContentElement(cEl, contentBuilder, reasoningBuilder);
        }

        return appended;
    }

    private static bool AppendReasoningElement(JsonElement el, StringBuilder reasoningBuilder)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string? text = el.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                reasoningBuilder.Append(text);
                return true;
            }

            return false;
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in (ReadOnlySpan<string>)["thinking", "reasoning", "text", "content", "summary"])
            {
                if (el.TryGetProperty(key, out JsonElement child) &&
                    AppendReasoningElement(child, reasoningBuilder))
                {
                    return true;
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            bool any = false;
            foreach (JsonElement item in el.EnumerateArray())
            {
                any |= AppendReasoningElement(item, reasoningBuilder);
            }

            return any;
        }

        return false;
    }

    private static bool AppendContentElement(
        JsonElement el,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string? text = el.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                contentBuilder.Append(text);
                return true;
            }

            return false;
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            bool any = false;
            foreach (JsonElement part in el.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    string? s = part.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        contentBuilder.Append(s);
                        any = true;
                    }

                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string partType = part.TryGetProperty("type", out JsonElement pTypeEl) &&
                                  pTypeEl.ValueKind == JsonValueKind.String
                    ? (pTypeEl.GetString() ?? string.Empty)
                    : string.Empty;

                if (string.Equals(partType, "thinking", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partType, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(partType, "thought", StringComparison.OrdinalIgnoreCase))
                {
                    any |= AppendReasoningElement(part, reasoningBuilder);
                    continue;
                }

                foreach (string textKey in (ReadOnlySpan<string>)["text", "content", "output_text", "value"])
                {
                    if (part.TryGetProperty(textKey, out JsonElement textPart) &&
                        textPart.ValueKind == JsonValueKind.String)
                    {
                        string? chunk = textPart.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            contentBuilder.Append(chunk);
                            any = true;
                            break;
                        }
                    }
                }
            }

            return any;
        }

        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("text", out JsonElement objText) &&
            objText.ValueKind == JsonValueKind.String)
        {
            string? chunk = objText.GetString();
            if (!string.IsNullOrEmpty(chunk))
            {
                contentBuilder.Append(chunk);
                return true;
            }
        }

        return false;
    }

    internal static bool TryExtractSseFullReply(
        string sseBody,
        out string content,
        out string reasoning)
    {
        content = string.Empty;
        reasoning = string.Empty;
        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        bool any = false;

        using var reader = new StringReader(sseBody);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string payload = trimmed["data:".Length..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryAppendSseDelta(payload, contentSb, reasoningSb, out _))
            {
                any = true;
            }
        }

        content = contentSb.ToString();
        reasoning = reasoningSb.ToString();
        return any;
    }

    internal static bool TryExtractNonStreamingReply(
        string rawJson,
        out string content,
        out string reasoning) =>
        TryExtractNonStreamingReply(rawJson, out content, out reasoning, out _);

    internal static bool TryExtractNonStreamingReply(
        string rawJson,
        out string content,
        out string reasoning,
        out string? finishReason)
    {
        content = string.Empty;
        reasoning = string.Empty;
        finishReason = null;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            var contentSb = new StringBuilder();
            var reasoningSb = new StringBuilder();

            if (root.TryGetProperty("choices", out JsonElement choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("finish_reason", out JsonElement frEl) &&
                    frEl.ValueKind == JsonValueKind.String)
                {
                    finishReason = frEl.GetString();
                }

                bool extracted = false;
                if (first.TryGetProperty("message", out JsonElement msg) &&
                    msg.ValueKind == JsonValueKind.Object)
                {
                    extracted |= TryAppendMessageLikeFields(msg, contentSb, reasoningSb);
                    if (msg.TryGetProperty("content", out JsonElement cEl) &&
                        cEl.ValueKind == JsonValueKind.String)
                    {
                        extracted = true;
                    }
                }

                if (contentSb.Length == 0 && first.TryGetProperty("text", out JsonElement textEl))
                {
                    extracted |= AppendContentElement(textEl, contentSb, reasoningSb);
                }

                if (extracted || contentSb.Length > 0 || reasoningSb.Length > 0)
                {
                    content = contentSb.ToString();
                    reasoning = reasoningSb.ToString();
                    return true;
                }
            }

            // Support OpenAI Responses API JSON payloads bridged by third-party relays.
            if (root.TryGetProperty("output_text", out JsonElement outputTextEl) &&
                AppendContentElement(outputTextEl, contentSb, reasoningSb))
            {
                content = contentSb.ToString();
                reasoning = reasoningSb.ToString();
                return true;
            }

            if (root.TryGetProperty("output", out JsonElement outputArr) &&
                outputArr.ValueKind == JsonValueKind.Array)
            {
                bool anyOutput = false;
                foreach (JsonElement item in outputArr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string itemType = item.TryGetProperty("type", out JsonElement tEl) &&
                                      tEl.ValueKind == JsonValueKind.String
                        ? (tEl.GetString() ?? string.Empty)
                        : string.Empty;
                    if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase))
                    {
                        anyOutput |= AppendReasoningElement(item, reasoningSb);
                    }
                    else if (item.TryGetProperty("content", out JsonElement itemContent))
                    {
                        anyOutput |= AppendContentElement(itemContent, contentSb, reasoningSb);
                    }
                }

                if (anyOutput)
                {
                    content = contentSb.ToString();
                    reasoning = reasoningSb.ToString();
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool TryExtractUsageFromJson(
        string? rawJson,
        out int promptTokens,
        out int cachedPromptTokens,
        out int completionTokens)
    {
        promptTokens = 0;
        cachedPromptTokens = 0;
        completionTokens = 0;
        if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("usage", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            return TryExtractUsageMetrics(doc.RootElement, out promptTokens, out cachedPromptTokens, out completionTokens);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryExtractUsageMetrics(
        JsonElement root,
        out int promptTokens,
        out int cachedPromptTokens,
        out int completionTokens)
    {
        promptTokens = 0;
        cachedPromptTokens = 0;
        completionTokens = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (usage.TryGetProperty("prompt_tokens", out JsonElement ptEl) && ptEl.TryGetInt32(out int pt))
        {
            promptTokens = Math.Max(0, pt);
        }
        else if (usage.TryGetProperty("input_tokens", out JsonElement itEl) && itEl.TryGetInt32(out int it))
        {
            promptTokens = Math.Max(0, it);
        }

        if (usage.TryGetProperty("completion_tokens", out JsonElement ctEl) && ctEl.TryGetInt32(out int ct))
        {
            completionTokens = Math.Max(0, ct);
        }
        else if (usage.TryGetProperty("output_tokens", out JsonElement otEl) && otEl.TryGetInt32(out int ot))
        {
            completionTokens = Math.Max(0, ot);
        }

        if (usage.TryGetProperty("prompt_cache_hit_tokens", out JsonElement dsCacheEl) &&
            dsCacheEl.TryGetInt32(out int dsCached))
        {
            cachedPromptTokens = Math.Max(0, dsCached);
        }
        else if (usage.TryGetProperty("prompt_tokens_details", out JsonElement detailsEl) &&
                 detailsEl.ValueKind == JsonValueKind.Object &&
                 detailsEl.TryGetProperty("cached_tokens", out JsonElement oaiCacheEl) &&
                 oaiCacheEl.TryGetInt32(out int oaiCached))
        {
            cachedPromptTokens = Math.Max(0, oaiCached);
        }
        else if (usage.TryGetProperty("cache_read_input_tokens", out JsonElement antCacheEl) &&
                 antCacheEl.TryGetInt32(out int antCached))
        {
            cachedPromptTokens = Math.Max(0, antCached);
        }

        return promptTokens > 0 || completionTokens > 0 || cachedPromptTokens > 0;
    }

    internal static string FormatHttpError(HttpStatusCode statusCode, string rawBody)
    {
        int code = (int)statusCode;
        string? detail = TryExtractApiErrorMessage(rawBody);
        string prefix = code switch
        {
            401 => Localized.T("CustomWidget.AiApi.HttpError.401"),
            402 => Localized.T("CustomWidget.AiApi.HttpError.402"),
            403 => Localized.T("CustomWidget.AiApi.HttpError.403"),
            404 => Localized.T("CustomWidget.AiApi.HttpError.404"),
            429 => Localized.T("CustomWidget.AiApi.HttpError.429"),
            >= 500 => Localized.Format("CustomWidget.AiApi.HttpError.5xx", code),
            _ => $"HTTP {code}"
        };

        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
    }

    internal static string? TryExtractStructuredJsonErrorMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl) &&
                errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            {
                if (errEl.ValueKind == JsonValueKind.String && errEl.GetString() is { Length: > 0 } errStr)
                {
                    return errStr;
                }

                if (errEl.ValueKind == JsonValueKind.Object)
                {
                    if (errEl.TryGetProperty("message", out JsonElement msgEl) &&
                        msgEl.ValueKind == JsonValueKind.String &&
                        msgEl.GetString() is { Length: > 0 } objMsg)
                    {
                        return objMsg;
                    }

                    if (errEl.TryGetProperty("code", out JsonElement codeEl) &&
                        codeEl.ValueKind == JsonValueKind.String &&
                        codeEl.GetString() is { Length: > 0 } errCode)
                    {
                        return errCode;
                    }
                }
            }

            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choicesEl) ||
                choicesEl.ValueKind == JsonValueKind.Null)
            {
                if (doc.RootElement.TryGetProperty("message", out JsonElement rootMsg) &&
                    rootMsg.ValueKind == JsonValueKind.String &&
                    rootMsg.GetString() is { Length: > 0 } msgText)
                {
                    return msgText;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string? TryExtractApiErrorMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("error", out JsonElement errEl))
            {
                if (errEl.ValueKind == JsonValueKind.String)
                {
                    return errEl.GetString();
                }
                if (errEl.ValueKind == JsonValueKind.Object &&
                    errEl.TryGetProperty("message", out JsonElement msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String)
                {
                    return msgEl.GetString();
                }
            }

            if (doc.RootElement.TryGetProperty("message", out JsonElement rootMsg) &&
                rootMsg.ValueKind == JsonValueKind.String)
            {
                return rootMsg.GetString();
            }
        }
        catch
        {
        }

        string singleLine = rawJson.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 180 ? singleLine[..180] + "…" : singleLine;
    }
}
