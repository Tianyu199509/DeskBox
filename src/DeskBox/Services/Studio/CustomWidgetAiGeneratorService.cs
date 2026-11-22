using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Facade for Custom Widget Studio AI co-creation, agentic multi-turn editing,
/// and incremental locale translation. Delegates to cohesive services under
/// <c>Services/AI</c> and <c>Services/Studio</c>.
/// </summary>
public sealed class CustomWidgetAiGeneratorService
{
    public const string CustomProviderId = AiProviderCatalog.CustomProviderId;

    public static IReadOnlyList<CustomWidgetAiProviderPreset> SupportedProviders =>
        AiProviderCatalog.SupportedProviders;

    public static bool IsSimplifiedChineseRegion(string? cultureName = null) =>
        AiProviderCatalog.IsSimplifiedChineseRegion(cultureName);

    public static string GetDefaultStarterProviderId(string? cultureName = null) =>
        AiProviderCatalog.GetDefaultStarterProviderId(cultureName);

    public static IReadOnlyList<CustomWidgetAiProviderPreset> GetOrderedProvidersForCulture(string? cultureName = null) =>
        AiProviderCatalog.GetOrderedProvidersForCulture(cultureName);

    private readonly DeskBoxAiService _aiService;
    private readonly WidgetAiI18nTranslationService _i18nService;
    private readonly StudioAiTurnOrchestrator _turnOrchestrator;

    public static CustomWidgetAiGeneratorService Shared { get; } =
        new(DeskBoxAiService.Shared, CustomWidgetNetworkSourceEngine.Shared, CustomWidgetDiagnosticArchive.Shared);

    public CustomWidgetAiGeneratorService(
        CustomWidgetAiCredentialStore? credentialStore = null,
        HttpClient? httpClient = null,
        CustomWidgetNetworkSourceEngine? networkSourceEngine = null,
        CustomWidgetDiagnosticArchive? diagnosticArchive = null)
        : this(
            new DeskBoxAiService(credentialStore, CustomWidgetStudioPreferencesStore.Shared, httpClient),
            networkSourceEngine ?? (httpClient is null ? CustomWidgetNetworkSourceEngine.Shared : null),
            diagnosticArchive)
    {
    }

    public CustomWidgetAiGeneratorService(
        DeskBoxAiService aiService,
        CustomWidgetNetworkSourceEngine? networkSourceEngine = null,
        CustomWidgetDiagnosticArchive? diagnosticArchive = null)
    {
        ArgumentNullException.ThrowIfNull(aiService);
        _aiService = aiService;
        var preFlightProbe = new StudioPreFlightProbeService(networkSourceEngine);
        _i18nService = new WidgetAiI18nTranslationService(aiService);
        _turnOrchestrator = new StudioAiTurnOrchestrator(aiService, networkSourceEngine, diagnosticArchive, preFlightProbe);
    }

    public DeskBoxAiService AiService => _aiService;

    public static AiThinkingMode ParseThinkingMode(string? raw) =>
        AiProviderCatalog.ParseThinkingMode(raw);

    public static string ToThinkingWireValue(AiThinkingMode mode) =>
        AiProviderCatalog.ToThinkingWireValue(mode);

    public static CustomWidgetAiProviderPreset FindProviderPreset(string? providerId) =>
        AiProviderCatalog.FindProviderPreset(providerId);

    public IReadOnlyList<CustomWidgetAiProviderPreset> GetConfiguredProviders() =>
        _aiService.GetConfiguredProviders();

    public bool HasAnyConfiguredProvider() => _aiService.HasAnyConfiguredProvider();

    public ResolvedAiInvocation ResolveEffectiveSettings(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null) =>
        _turnOrchestrator.ResolveEffectiveSettings(providerId, endpointOverride, modelOverride, apiKeyOverride, thinkingOverride);

    public ResolvedAiInvocation ResolveEffectiveStudioInvocation(
        string providerId,
        string? endpointOverride = null,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        AiThinkingMode? thinkingOverride = null) =>
        _turnOrchestrator.ResolveEffectiveStudioInvocation(providerId, endpointOverride, modelOverride, apiKeyOverride, thinkingOverride);

    public static string NormalizeChatCompletionsEndpoint(string rawUrl) =>
        AiProviderCatalog.NormalizeChatCompletionsEndpoint(rawUrl);

    public static string DeriveModelsEndpoint(string chatCompletionsOrBaseUrl) =>
        AiProviderCatalog.DeriveModelsEndpoint(chatCompletionsOrBaseUrl);

    public static bool IsReasoningOnlyModel(string model) =>
        AiProviderCatalog.IsReasoningOnlyModel(model);

    public IReadOnlyList<string> GetSelectableModels(
        string providerId,
        IReadOnlyList<string>? transientFetchedModels = null) =>
        _aiService.GetSelectableModels(providerId, transientFetchedModels);

    public Task<bool> TryEnsureModelsFetchedOnceAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        _aiService.TryEnsureModelsFetchedOnceAsync(providerId, cancellationToken);

    public Task<AiModelListResult> FetchAvailableModelsAsync(
        string providerId,
        string? endpointOverride = null,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default,
        bool persistDiscoveredModels = true) =>
        _aiService.FetchAvailableModelsAsync(providerId, endpointOverride, apiKeyOverride, cancellationToken, persistDiscoveredModels);

    public Task<AiConnectionTestResult> TestConnectionAsync(
        string providerId,
        string? endpointOverride = null,
        string? apiKeyOverride = null,
        string? modelOverride = null,
        AiThinkingMode? thinkingOverride = null,
        CancellationToken cancellationToken = default) =>
        _aiService.TestConnectionAsync(providerId, endpointOverride, apiKeyOverride, modelOverride, thinkingOverride, cancellationToken);

    public static string? ResolveEffectiveLogicJs(
        string? baseLogicJs,
        StudioLogicJsPatchMode patchMode,
        string? incomingLogicJs) =>
        StudioTurnResponseParser.ResolveEffectiveLogicJs(baseLogicJs, patchMode, incomingLogicJs);

    public Task<StudioTurnResult> RunStudioTurnAsync(
        string providerId,
        string userMessage,
        string? currentManifestJson = null,
        string? briefMarkdown = null,
        IReadOnlyList<StudioChatMessage>? recentMessages = null,
        CustomWidgetHealthReport? healthReport = null,
        string? targetNodePath = null,
        string? preferredEmoji = null,
        bool allowSampleData = false,
        CancellationToken cancellationToken = default,
        Action<AiGenerationStreamProgress>? onProgress = null,
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPackOverride = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null,
        string? currentLogicJs = null,
        CustomWidgetLocaleCatalog? currentLocales = null,
        bool isCapsuleViewport = false,
        int? defaultWidgetWidth = null,
        int? defaultWidgetHeight = null,
        string? existingSampleDataJson = null,
        AiThinkingMode? thinkingOverride = null,
        string? runtimeTraceSummary = null,
        IReadOnlyList<string>? sessionCapabilityPackIds = null) =>
        _turnOrchestrator.RunStudioTurnAsync(
            providerId,
            userMessage,
            currentManifestJson,
            briefMarkdown,
            recentMessages,
            healthReport,
            targetNodePath,
            preferredEmoji,
            allowSampleData,
            cancellationToken,
            onProgress,
            uiLanguage,
            promptPackOverride,
            pinnedCapabilityIds,
            currentLogicJs,
            currentLocales,
            isCapsuleViewport,
            defaultWidgetWidth,
            defaultWidgetHeight,
            existingSampleDataJson,
            thinkingOverride,
            runtimeTraceSummary,
            sessionCapabilityPackIds);

    public Task<WidgetIncrementalI18nBatchResult> RunIncrementalI18nQueueAsync(
        string providerId,
        CustomWidgetI18nSyncPlan plan,
        IReadOnlyList<string>? onlyCultures = null,
        Action<LocaleTranslationProgressUpdate>? onLocaleProgress = null,
        Func<string, IReadOnlyDictionary<string, string>, CustomWidgetLocaleCatalog, Task>? onLocaleCompletedAsync = null,
        CancellationToken cancellationToken = default,
        int? maxConcurrencyOverride = null) =>
        _i18nService.RunIncrementalI18nQueueAsync(
            providerId,
            plan,
            onlyCultures,
            onLocaleProgress,
            onLocaleCompletedAsync,
            cancellationToken,
            maxConcurrencyOverride);

    internal static string BuildDeltaLocaleUserPrompt(
        string baseLocale,
        CustomWidgetLocaleTaskPlan taskPlan,
        IReadOnlyDictionary<string, string> baseEntries,
        IReadOnlyList<string> keysToTranslate,
        bool isRepair) =>
        WidgetAiI18nTranslationService.BuildDeltaLocaleUserPrompt(baseLocale, taskPlan, baseEntries, keysToTranslate, isRepair);

    public static CustomWidgetLocaleCatalog? MergeLocaleCatalogs(
        CustomWidgetLocaleCatalog? baseCatalog,
        CustomWidgetLocaleCatalog? overlayCatalog) =>
        WidgetAiI18nTranslationService.MergeLocaleCatalogs(baseCatalog, overlayCatalog);

    public static string ExtractInlineManifestLocalesAndStrip(
        string manifestJson,
        CustomWidgetLocaleCatalog? existingLocales,
        out CustomWidgetLocaleCatalog? effectiveLocales) =>
        WidgetAiI18nTranslationService.ExtractInlineManifestLocalesAndStrip(manifestJson, existingLocales, out effectiveLocales);

    public static StudioTurnResult ParseStudioTurnResponse(
        string? rawAssistantText,
        string? preferredEmoji = null,
        string providerId = "",
        string? effectiveModel = null,
        bool isInitialCreation = false,
        string? currentLogicJs = null,
        CustomWidgetLocaleCatalog? currentLocales = null,
        string? fallbackCulture = null,
        string? currentManifestJson = null) =>
        StudioTurnResponseParser.ParseStudioTurnResponse(
            rawAssistantText,
            preferredEmoji,
            providerId,
            effectiveModel,
            isInitialCreation,
            currentLogicJs,
            currentLocales,
            fallbackCulture,
            currentManifestJson);

    public static void SplitInlineThinkBlock(
        string content,
        out string inlineThinking,
        out string visibleContent,
        out bool hasUnclosedOpenThinkTag) =>
        AiChatTransportClient.SplitInlineThinkBlock(content, out inlineThinking, out visibleContent, out hasUnclosedOpenThinkTag);
}
