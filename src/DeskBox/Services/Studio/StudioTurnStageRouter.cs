using System.Text;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Stage-aware routing heuristics for DeskBox Studio Agent 2.0:
/// classifies Turn-0 clarification vs direct synthesis, distinguishes localized visual micro-edits
/// from cross-node/behavioral bug reports, routes per-round thinking mode, and builds compact
/// <c>#id</c> view outlines and dataflow spotlights.
/// </summary>
public static class StudioTurnStageRouter
{
    private static readonly JsonDocumentOptions PermissiveDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly string[] PureComplaintKeywords =
    [
        "没有任何数据", "没有数据", "没数据", "无数据", "数据为空",
        "一直在加载", "一直加载", "加载不出来",
        "搜索没生效", "搜索不生效", "筛选没生效", "没生效", "不生效", "没反应", "点不动",
        "看不到文字", "看不到", "不显示", "空白",
        "报错了", "出错了", "还是不行", "有问题",
        "nothing shows", "no data", "stuck loading", "search not working", "not working", "doesn't work", "still broken"
    ];

    private static readonly string[] FeatureAdditionVerbs =
    [
        "增加", "加上", "添加", "新增", "支持", "改成", "改为", "换成", "加入",
        "add ", "support ", "change to ", "replace with ", "include "
    ];

    private static readonly string[] ExplicitDirectBuildPhrases =
    [
        "直接生成", "直接做", "直接开始", "开始构建", "按所选方案", "按默认方案", "不用确认", "无需确认",
        "build directly", "just build", "start building", "confirmed plan"
    ];

    private static readonly string[] MinimalistSelfContainedPhrases =
    [
        "极简", "最简", "简单", "minimal", "minimalist", "simple"
    ];

    private static readonly string[] ExternalOrOpenScopeDomainKeywords =
    [
        "热点", "热搜", "热榜", "榜单", "资讯", "新闻", "头条", "日报",
        "股票", "行情", "A股", "港股", "美股", "基金", "加密货币", "币价", "汇率",
        "天气", "空气质量", "赛事", "比分", "票房", "监控", "看板", "仪表盘",
        "trending", "news", "stock", "market", "crypto", "weather", "dashboard", "monitor", "feed"
    ];

    public static bool IsDiagnosticComplaintOnly(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        string trimmed = prompt.Trim();
        if (trimmed.Length > 48)
        {
            return false;
        }

        if (ContainsAny(trimmed, FeatureAdditionVerbs))
        {
            return false;
        }

        return ContainsAny(trimmed, PureComplaintKeywords);
    }

    public static bool ShouldEnforceHardSubtreeGuard(string? targetNodePath, string? userPrompt)
    {
        // Treat targetNodePath as a soft focus hint in the prompt rather than a hard edit-rejection lock
        // so cross-element/state/script fixes (e.g. clicking a broken button or card) are never blocked.
        _ = targetNodePath;
        _ = userPrompt;
        return false;
    }

    public static bool ShouldPreferInitialClarification(
        string? userPrompt,
        bool hasExistingManifest,
        IReadOnlyList<StudioChatMessage>? recentMessages)
    {
        if (hasExistingManifest || string.IsNullOrWhiteSpace(userPrompt))
        {
            return false;
        }

        // Never clarify twice or after conversation history already exists
        if (recentMessages is { Count: > 0 })
        {
            foreach (StudioChatMessage msg in recentMessages)
            {
                if (!msg.IsSuperseded &&
                    (string.Equals(msg.Kind, "clarify", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        string trimmed = userPrompt.Trim();
        if (ContainsAny(trimmed, ExplicitDirectBuildPhrases))
        {
            return false;
        }

        if (ContainsAny(trimmed, MinimalistSelfContainedPhrases) &&
            !ContainsAny(trimmed, ExternalOrOpenScopeDomainKeywords))
        {
            return false;
        }

        bool hasExplicitUrl = trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                              trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase);

        int cjkCount = CountCjkChars(trimmed);
        int clauseSeparators = CountClauseSeparators(trimmed);

        // Detailed multi-clause specifications (such as Start Gallery inspiration cards) have concrete structure & interactions
        bool isDetailedSpec = (cjkCount >= 46 || trimmed.Length >= 120) && clauseSeparators >= 2;
        if (isDetailedSpec && (!ContainsAny(trimmed, ExternalOrOpenScopeDomainKeywords) || hasExplicitUrl || cjkCount >= 56))
        {
            return false;
        }

        // Broad/open-ended one-liners or live-data topics without explicit feed/URL/breakdown benefit from fast Turn-0 confirmation
        if (cjkCount > 0 ? cjkCount <= 36 : trimmed.Length <= 80)
        {
            return true;
        }

        if (ContainsAny(trimmed, ExternalOrOpenScopeDomainKeywords) && !hasExplicitUrl && clauseSeparators <= 2)
        {
            return true;
        }

        return false;
    }

    public static AiThinkingMode ResolveAdaptiveStudioThinkingMode(
        ResolvedAiInvocation baseInvocation,
        string? userPrompt,
        bool hasExistingManifest,
        string? targetNodePath,
        IReadOnlyList<StudioChatMessage>? recentMessages,
        bool isPostMutationRound,
        bool thinkingParamsUnsupportedOnRoute,
        bool thinkingExhaustedOnRoute = false)
    {
        _ = targetNodePath;
        if (AiProviderCatalog.IsReasoningOnlyModel(baseInvocation.Model) || thinkingParamsUnsupportedOnRoute)
        {
            return AiThinkingMode.Auto;
        }

        AiThinkingProtocol protocol = AiChatTransportClient.ResolveEffectiveThinkingProtocol(
            baseInvocation.Preset.ThinkingProtocol,
            baseInvocation.Model);

        if (thinkingExhaustedOnRoute)
        {
            if (protocol == AiThinkingProtocol.DeepSeekModelSwap &&
                !baseInvocation.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
            {
                return AiThinkingMode.Auto;
            }

            return protocol == AiThinkingProtocol.None ? AiThinkingMode.Auto : AiThinkingMode.Disabled;
        }

        // Preserve explicit user-configured Enabled/Disabled on Round 0 (adaptive routing applies when user configured Auto)
        if (!isPostMutationRound && baseInvocation.ThinkingMode != AiThinkingMode.Auto)
        {
            return baseInvocation.ThinkingMode;
        }

        // Custom 3rd-party relay endpoints in Auto mode omit unsolicited vendor thinking params on Round 0
        if (!isPostMutationRound &&
            baseInvocation.UsesCustomEndpoint &&
            string.Equals(baseInvocation.Preset.Id, CustomWidgetAiGeneratorService.CustomProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return AiThinkingMode.Auto;
        }

        // Auto mode defaults to fast non-thinking tool flow (no fragile keyword-triggered
        // forced thinking that causes slow CoT hangs on GLM/DeepSeek). Explicit user Enabled is honored above.
        _ = userPrompt;
        _ = hasExistingManifest;
        _ = recentMessages;
        if (protocol == AiThinkingProtocol.DeepSeekModelSwap &&
            !baseInvocation.Model.Contains("reasoner", StringComparison.OrdinalIgnoreCase))
        {
            return AiThinkingMode.Auto;
        }

        return protocol == AiThinkingProtocol.None ? AiThinkingMode.Auto : AiThinkingMode.Disabled;
    }

    public static ResolvedAiInvocation DowngradeToFastNonThinkingInvocation(
        ResolvedAiInvocation current,
        bool thinkingParamsUnsupportedOnRoute,
        IReadOnlyList<string>? fetchedModels = null)
    {
        if (string.Equals(current.Model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase) &&
            AiProviderCatalog.SupportsClassicDeepSeekModelSwap(current.Preset, fetchedModels))
        {
            return current with
            {
                Model = "deepseek-chat",
                ThinkingMode = AiThinkingMode.Disabled
            };
        }

        if (current.ThinkingMode != AiThinkingMode.Disabled &&
            !AiProviderCatalog.IsReasoningOnlyModel(current.Model) &&
            !thinkingParamsUnsupportedOnRoute)
        {
            return current with { ThinkingMode = AiThinkingMode.Disabled };
        }

        return current;
    }

    public static ResolvedAiInvocation ResolveRoundInvocation(
        ResolvedAiInvocation turnInvocation,
        int structuredTurnCount,
        bool workspaceMutated,
        bool isRepairRound,
        bool synthesisThinkingConsumed,
        bool thinkingParamsUnsupportedOnRoute,
        out bool allowAutoSynthesisThinking) =>
        ResolveRoundInvocation(
            turnInvocation,
            structuredTurnCount,
            workspaceMutated,
            isRepairRound,
            synthesisThinkingConsumed,
            thinkingParamsUnsupportedOnRoute,
            fetchedModels: null,
            out allowAutoSynthesisThinking);

    public static ResolvedAiInvocation ResolveRoundInvocation(
        ResolvedAiInvocation turnInvocation,
        int structuredTurnCount,
        bool workspaceMutated,
        bool isRepairRound,
        bool synthesisThinkingConsumed,
        bool thinkingParamsUnsupportedOnRoute,
        IReadOnlyList<string>? fetchedModels,
        out bool allowAutoSynthesisThinking)
    {
        allowAutoSynthesisThinking =
            !isRepairRound &&
            !workspaceMutated &&
            !synthesisThinkingConsumed &&
            structuredTurnCount > 0;

        bool shouldDowngrade = isRepairRound || (structuredTurnCount > 0 && !allowAutoSynthesisThinking);
        return shouldDowngrade
            ? DowngradeToFastNonThinkingInvocation(turnInvocation, thinkingParamsUnsupportedOnRoute, fetchedModels)
            : turnInvocation;
    }

    public static string BuildDataflowSpotlightHint(
        string? manifestJson,
        string? targetNodePath,
        string? userPrompt,
        bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(targetNodePath))
        {
            return string.Empty;
        }

        _ = userPrompt;
        string cleanSelector = StudioWorkspaceEngine.ExtractCleanNodeSelector(targetNodePath) ?? targetNodePath.Trim();

        var sb = new StringBuilder(512);
        if (isChinese)
        {
            sb.AppendLine($"- 用户点选的关注元素：`{cleanSelector}`（软聚焦线索：优先检查该元素及其关联的 `state`/`sources`/`logic.js` 或相邻元素，用 `edit_manifest` 精准修改，跨元素联动问题可一并修复）。");
        }
        else
        {
            sb.AppendLine($"- User-clicked focus element: `{cleanSelector}` (soft focus hint: prioritize inspecting this element and any linked `state`/`sources`/`logic.js` or sibling elements via `edit_manifest`; cross-element fixes are allowed).");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifestJson, PermissiveDocOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sources", out JsonElement sourcesEl))
            {
                if ((sourcesEl.ValueKind == JsonValueKind.Array && sourcesEl.GetArrayLength() > 0) ||
                    (sourcesEl.ValueKind == JsonValueKind.Object && sourcesEl.EnumerateObject().Any()))
                {
                    sb.AppendLine(isChinese
                        ? "- 数据源状态契约：`sources` 网络引擎会自动维护 `state.sourceStatus`（`'loading'` | `'ok'` | `'error'`）；切勿自定义死锁的 `state.loading = true` 而不复位。"
                        : "- Source status contract: `sources` engine automatically updates `state.sourceStatus` (`'loading'` | `'ok'` | `'error'`); never leave a custom `state.loading = true` un-cleared.");
                }
            }
        }
        catch
        {
        }

        return sb.ToString().TrimEnd();
    }

    private static bool ContainsAny(string text, ReadOnlySpan<string> terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountCjkChars(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF'))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountClauseSeparators(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '，' or '；' or '。' or '：' or ',' or ';' or ':')
            {
                count++;
            }
        }
        return count;
    }
}
