using System.Text.Json.Serialization;
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
    long LatencyMs,
    string? RawText = null);

public sealed record AiCallTelemetrySnapshot(
    long TotalCalls,
    long AutoRepairedCalls,
    long TotalPromptTokens,
    long TotalCachedPromptTokens,
    long TotalCompletionTokens,
    long LastCallLatencyMs,
    double CacheHitRatio);


public enum AiThinkingMode
{
    Auto = 0,
    Enabled = 1,
    Disabled = 2
}

public enum AiContextWindowMode
{
    Auto = 0,
    Compact16k = 1,
    Standard32k = 2,
    Extended128k = 3
}

public enum AiThinkingProtocol
{
    None = 0,
    DeepSeekModelSwap = 1,
    EnableThinkingFlag = 2,
    ThinkingTypeObject = 3,
    OpenRouterReasoning = 4,
    ReasoningEffort = 5,
    AutoDetectByModel = 6
}

public enum AiGenerationStage
{
    Connecting = 0,
    Thinking = 1,
    GeneratingJson = 2,
    Validating = 3,
    AutoRepairing = 4,
    InspectingCapabilities = 5,
    ProbingApi = 6,
    ReadingWorkspace = 7,
    PatchingWorkspace = 8
}

public enum StudioStreamStepState
{
    Running = 0,
    Completed = 1,
    Warning = 2
}

public sealed record StudioStreamStepItem(
    string Id,
    StudioStreamStepState State,
    string Label);

public sealed record CustomWidgetAiProviderPreset(
    string Id,
    string DefaultDisplayName,
    string DefaultEndpoint,
    string DefaultModel,
    IReadOnlyList<string>? RecommendedModels = null,
    AiThinkingProtocol ThinkingProtocol = AiThinkingProtocol.None,
    string? DisplayNameKey = null,
    bool RequiresApiKey = true,
    string? ConsoleUrl = null,
    string? GlobalDefaultEndpoint = null,
    bool IsMainlandFocused = false)
{
    public string DisplayName =>
        !string.IsNullOrEmpty(DisplayNameKey)
            ? Localized.T(DisplayNameKey)
            : DefaultDisplayName;

    public IReadOnlyList<string> Models =>
        RecommendedModels is { Count: > 0 } ? RecommendedModels : [DefaultModel];
}

public sealed record ResolvedAiInvocation(
    CustomWidgetAiProviderPreset Preset,
    string Endpoint,
    string Model,
    string ApiKey,
    AiThinkingMode ThinkingMode,
    bool UsesCustomEndpoint,
    AiContextWindowMode ContextWindowMode = AiContextWindowMode.Auto);

public sealed record AiGenerationStreamProgress(
    AiGenerationStage Stage,
    string ProviderName,
    string Model,
    string ThinkingText,
    string ContentText,
    int ReasoningChars,
    int ContentChars,
    string? DetectedWidgetName,
    TimeSpan Elapsed,
    IReadOnlyList<StudioStreamStepItem>? Steps = null,
    string? ActiveToolName = null,
    string? StreamingHint = null,
    string? ProgressiveManifestJson = null,
    int ThinkingTokens = 0,
    int ContentTokens = 0,
    int TotalOutputTokens = 0);

public sealed record AiConnectionTestResult(
    bool Success,
    long LatencyMs,
    string EffectiveEndpoint,
    string EffectiveModel,
    string? ReplyPreview,
    string? ErrorMessage);

public sealed record AiModelListResult(
    bool Success,
    IReadOnlyList<string> Models,
    string? ErrorMessage);

public enum StudioLogicJsPatchMode
{
    KeepExisting = 0,
    Replace = 1,
    Remove = 2
}

public sealed record StudioTurnResult(
    bool Success,
    string Kind,
    string Reply,
    string ChangeSummary,
    StudioBriefUpdateSpec? BriefUpdates,
    IReadOnlyList<string> Suggestions,
    string? SampleDataJson,
    string ManifestJson,
    CustomWidgetParseResult? ParseResult,
    string? ErrorMessage,
    string ProviderId,
    bool WasAutoRepaired = false,
    string? EffectiveModel = null,
    StudioLogicJsPatchMode LogicJsPatchMode = StudioLogicJsPatchMode.KeepExisting,
    string? LogicJs = null,
    CustomWidgetLocaleCatalog? Locales = null,
    IReadOnlyList<string>? FineGrainedDiffPills = null,
    bool WasNoOpEdit = false,
    IReadOnlyList<StudioStreamStepItem>? ExecutionSteps = null,
    string? ThinkingText = null,
    IReadOnlyList<StudioClarifyQuestionSpec>? ClarifyQuestions = null,
    IReadOnlyList<string>? DiscoveredCapabilityPackIds = null,
    string? LimitationNote = null);

public enum LocaleTranslationJobState
{
    Queued = 0,
    Running = 1,
    Retrying = 2,
    Completed = 3,
    Failed = 4,
    SkippedUpToDate = 5,
    Cancelled = 6
}

public sealed record LocaleTranslationProgressUpdate(
    string Culture,
    LocaleTranslationJobState State,
    int TranslatedCount,
    int TotalDeltaCount,
    int ReadyTotalKeys,
    int RequiredTotalKeys,
    string? ErrorMessage = null);

public sealed record WidgetIncrementalI18nBatchResult(
    bool Success,
    bool Cancelled,
    string CompiledManifestJson,
    bool ManifestChanged,
    CustomWidgetLocaleCatalog UpdatedCatalog,
    IReadOnlyList<string> CompletedLocales,
    IReadOnlyList<string> SkippedLocales,
    IReadOnlyDictionary<string, string> FailedLocales,
    string? ErrorMessage,
    string ProviderId,
    string? EffectiveModel = null);

internal sealed record StudioStreamRoundOutcome(
    bool Success,
    string VisibleContent,
    string ThinkingText,
    IReadOnlyList<AiToolCallSpec> ToolCalls,
    string? FinishReason,
    string? ErrorMessage,
    bool RetriedWithoutTools = false,
    int PromptTokens = 0,
    int CachedPromptTokens = 0,
    int CompletionTokens = 0);

