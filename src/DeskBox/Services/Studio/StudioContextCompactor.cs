using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Provides deterministic multi-turn and intra-turn context compaction for Studio AI sessions:
/// filters superseded/synthetic turns, bounds chat messages and locale dictionaries,
/// compacts superseded <c>write_bundle</c> tool arguments, and keeps <c>brief.md</c> clean.
/// </summary>
public static class StudioContextCompactor
{
    public const int DefaultMaxCharsPerMessage = 700;
    private const int CompactableWriteBundleMinChars = 1_400;

    /// <summary>
    /// <see cref="StudioChatMessage.Kind"/> for system-composed repair / fix prompts (validation repair,
    /// script-error fix, health auto-fix). Structured tag: works in every UI language.
    /// </summary>
    public const string RepairMessageKind = "repair";

    public static bool IsRepairMessage(StudioChatMessage? message) =>
        message is not null &&
        (string.Equals(message.Kind, RepairMessageKind, StringComparison.OrdinalIgnoreCase) ||
         IsSyntheticRepairOrSystemPrompt(message.Text));

    public static bool IsSyntheticRepairOrSystemPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("[Target language:", StringComparison.OrdinalIgnoreCase) &&
            (trimmed.Contains("Please repair all validation diagnostics", StringComparison.OrdinalIgnoreCase) ||
             trimmed.Contains("Please repair all active diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return trimmed.StartsWith("Please repair all validation diagnostics", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Please repair all active diagnostics", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("请修复校验提示：", StringComparison.Ordinal) ||
               trimmed.StartsWith("一键修复体验诊断问题：", StringComparison.Ordinal) ||
               trimmed.StartsWith("Please repair validation prompt:", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "让 AI 自动修复", StringComparison.Ordinal) ||
               string.Equals(trimmed, "Auto-fix with AI", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<StudioChatMessage> FilterAndClampRecentMessages(
        IReadOnlyList<StudioChatMessage>? messages,
        int maxMessages = 6,
        int maxCharsPerMessage = DefaultMaxCharsPerMessage)
    {
        if (messages is null || messages.Count == 0 || maxMessages <= 0)
        {
            return [];
        }

        var eligible = new List<StudioChatMessage>(Math.Min(messages.Count, maxMessages * 2));
        foreach (StudioChatMessage msg in messages)
        {
            if (msg.IsSuperseded ||
                string.IsNullOrWhiteSpace(msg.Text) ||
                IsRepairMessage(msg))
            {
                continue;
            }

            eligible.Add(msg);
        }

        if (eligible.Count == 0)
        {
            return [];
        }

        int startIdx = Math.Max(0, eligible.Count - maxMessages);
        var result = new List<StudioChatMessage>(eligible.Count - startIdx);
        for (int i = startIdx; i < eligible.Count; i++)
        {
            StudioChatMessage msg = eligible[i];
            string trimmed = msg.Text.Trim();
            if (maxCharsPerMessage > 120 && trimmed.Length > maxCharsPerMessage)
            {
                int headLen = Math.Max(80, (maxCharsPerMessage * 2) / 3);
                int tailLen = Math.Max(40, maxCharsPerMessage - headLen - 8);
                if (headLen + tailLen < trimmed.Length)
                {
                    trimmed = $"{trimmed[..headLen].TrimEnd()} … {trimmed[^tailLen..].TrimStart()}";
                }
                else
                {
                    trimmed = trimmed[..maxCharsPerMessage];
                }
            }

            result.Add(ReferenceEquals(trimmed, msg.Text) ? msg : msg with { Text = trimmed });
        }

        return result;
    }

    public static string FormatBoundedBaseLocaleBlock(
        CustomWidgetLocaleCatalog? locales,
        bool isChinese,
        string? fallbackCulture,
        int maxKeys = 80,
        int maxChars = 4_500)
    {
        if (locales is not { HasAnyLocale: true })
        {
            return string.Empty;
        }

        string? selectedCulture = ResolveBaseLocaleCulture(locales, fallbackCulture);
        if (string.IsNullOrWhiteSpace(selectedCulture) ||
            !locales.Locales.TryGetValue(selectedCulture, out IReadOnlyDictionary<string, string>? dict) ||
            dict.Count == 0)
        {
            return string.Empty;
        }

        List<KeyValuePair<string, string>> sorted = dict
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        int totalCount = sorted.Count;
        int effectiveMaxKeys = Math.Max(1, maxKeys);
        int effectiveMaxChars = Math.Max(256, maxChars);

        int includedCount = 0;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            foreach ((string key, string val) in sorted)
            {
                if (includedCount >= effectiveMaxKeys)
                {
                    break;
                }

                if (includedCount > 0)
                {
                    writer.Flush();
                    if (ms.Length >= effectiveMaxChars - 128)
                    {
                        break;
                    }
                }

                writer.WriteString(key, val);
                includedCount++;
            }
            writer.WriteEndObject();
        }

        string dictJson = Encoding.UTF8.GetString(ms.ToArray());
        var sb = new StringBuilder(dictJson.Length + 512);
        if (isChinese)
        {
            sb.Append("## 当前格子的基准语言词典 (`locales/")
              .Append(selectedCulture)
              .AppendLine(".json`)");
            sb.AppendLine("当前 `manifest.json` 中的 `${$i18n.*}` / `${t(...)}` 键对应以下真实界面文案；未改动的文案请保持原 `${$i18n.*}` 绑定不变，新增或修改文案时直接在 `manifest.json` 对应字段写入明文字符串即可（工坊会自动增量同步词典）：");
        }
        else
        {
            sb.Append("## Current Base Locale Dictionary (`locales/")
              .Append(selectedCulture)
              .AppendLine(".json`)");
            sb.AppendLine("Existing `${$i18n.*}` / `${t(...)}` bindings in `manifest.json` resolve to these texts. Preserve untouched `${$i18n.*}` tokens as-is; for any newly added or modified UI copy, write the plain text directly on that property in `manifest.json` (Studio's compiler updates `locales/` automatically):");
        }

        sb.AppendLine("```json");
        sb.AppendLine(dictJson);
        sb.Append("```");

        if (includedCount < totalCount)
        {
            int omitted = totalCount - includedCount;
            sb.AppendLine();
            sb.Append(isChinese
                ? $"> 注：词典共 {totalCount} 项，已展示前 {includedCount} 项以节省上下文；其余 {omitted} 项可用 `read_workspace(section=\"locales\")` 按需读取。"
                : $"> Note: {totalCount} total locale keys; showing first {includedCount} to save context. Inspect the remaining {omitted} keys via `read_workspace(section=\"locales\")`.");
        }

        return sb.ToString();
    }

    public static IReadOnlyList<AiChatTurnMessage> CompactSupersededStructuredTurns(
        IReadOnlyList<AiChatTurnMessage> turns,
        bool compactAllWriteBundles = false,
        bool forceCompactLatestForOverflow = false)
    {
        if (turns is null || turns.Count == 0)
        {
            return [];
        }

        int lastLargeWriteBundleTurnIdx = -1;
        int lastWorkspaceMutationTurnIdx = -1;
        for (int i = 0; i < turns.Count; i++)
        {
            if (ContainsLargeWriteBundleCall(turns[i]))
            {
                lastLargeWriteBundleTurnIdx = i;
            }
            if (ContainsAnyWorkspaceMutationCall(turns[i]))
            {
                lastWorkspaceMutationTurnIdx = i;
            }
        }

        bool canElideStaleToolOutputs = forceCompactLatestForOverflow || (lastWorkspaceMutationTurnIdx >= 0 && turns.Count >= 6);
        if (lastLargeWriteBundleTurnIdx < 0 && !canElideStaleToolOutputs)
        {
            return turns;
        }

        var result = new List<AiChatTurnMessage>(turns.Count);
        const string CompactedPlaceholder = "{\"_compacted\":\"superseded write_bundle omitted; workspace holds latest state\"}";
        const string CompactedDiscoveryResultPlaceholder = "{\"_compacted\":\"reference contract applied; consult current workspace if needed\"}";

        for (int i = 0; i < turns.Count; i++)
        {
            AiChatTurnMessage turn = turns[i];
            // Never wipe the latest active write_bundle during normal repair rounds so the LLM sees the exact JSON/JS
            // it must fix (and intra-turn KV cache stays append-only). Only compact the latest bundle on HTTP 400 overflow.
            bool isOlderWriteBundleTurn = lastLargeWriteBundleTurnIdx >= 0 &&
                (i < lastLargeWriteBundleTurnIdx || (forceCompactLatestForOverflow && i == lastLargeWriteBundleTurnIdx));
            if (isOlderWriteBundleTurn && turn.ToolCalls is { Count: > 0 })
            {
                bool changedAnyCall = false;
                var compactedCalls = new List<AiToolCallSpec>(turn.ToolCalls.Count);
                foreach (AiToolCallSpec tc in turn.ToolCalls)
                {
                    if (string.Equals(tc.Name, StudioToolTransport.ToolWriteBundle, StringComparison.Ordinal) &&
                        tc.ArgumentsJson.Length > CompactableWriteBundleMinChars)
                    {
                        compactedCalls.Add(tc with { ArgumentsJson = CompactedPlaceholder });
                        changedAnyCall = true;
                    }
                    else
                    {
                        compactedCalls.Add(tc);
                    }
                }

                string? compactedReasoning = forceCompactLatestForOverflow &&
                                             !string.IsNullOrEmpty(turn.ReasoningContent) &&
                                             turn.ReasoningContent.Length > 600
                    ? "… " + turn.ReasoningContent[^400..]
                    : turn.ReasoningContent;

                result.Add(changedAnyCall || !ReferenceEquals(compactedReasoning, turn.ReasoningContent)
                    ? turn with { ToolCalls = compactedCalls, ReasoningContent = compactedReasoning }
                    : turn);
            }
            else if (canElideStaleToolOutputs &&
                     string.Equals(turn.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(turn.Content) &&
                     turn.Content.Length > 1_200 &&
                     (forceCompactLatestForOverflow ? i < turns.Count - 2 : i < lastWorkspaceMutationTurnIdx && i < turns.Count - 4) &&
                     turn.ToolName is StudioToolTransport.ToolInspectCapabilities
                         or StudioToolTransport.ToolReadWorkspace
                         or StudioToolTransport.ToolProbeHttp)
            {
                result.Add(turn with { Content = CompactedDiscoveryResultPlaceholder });
            }
            else if (forceCompactLatestForOverflow &&
                     string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrEmpty(turn.ReasoningContent) &&
                     turn.ReasoningContent.Length > 600 &&
                     i < turns.Count - 2)
            {
                result.Add(turn with { ReasoningContent = "… " + turn.ReasoningContent[^400..] });
            }
            else
            {
                result.Add(turn);
            }
        }

        _ = compactAllWriteBundles;
        return result;
    }

    public static CustomWidgetDesignBrief SanitizeAndRollBrief(
        CustomWidgetDesignBrief current,
        StudioBriefUpdateSpec? updates,
        CustomWidgetManifest? newManifest,
        string? rawUserPrompt,
        string? startDraftIdea,
        bool isInitialOrEmptyBrief)
    {
        ArgumentNullException.ThrowIfNull(current);

        CustomWidgetDesignBrief sanitized = current;
        if (IsSyntheticRepairOrSystemPrompt(sanitized.Purpose))
        {
            sanitized = sanitized with { Purpose = string.Empty };
        }

        if (sanitized.ConfirmedRequirements.Any(IsSyntheticRepairOrSystemPrompt))
        {
            sanitized = sanitized with
            {
                ConfirmedRequirements = sanitized.ConfirmedRequirements
                    .Where(static r => !IsSyntheticRepairOrSystemPrompt(r))
                    .ToList()
            };
        }

        bool effectivelyEmpty = sanitized.IsEmpty || string.IsNullOrWhiteSpace(sanitized.Purpose);
        string normalizedStartIdea = !string.IsNullOrWhiteSpace(startDraftIdea) && !IsSyntheticRepairOrSystemPrompt(startDraftIdea)
            ? NormalizeSingleLine(startDraftIdea, 240)
            : string.Empty;
        string normalizedRawPrompt = !string.IsNullOrWhiteSpace(rawUserPrompt) && !IsSyntheticRepairOrSystemPrompt(rawUserPrompt)
            ? NormalizeSingleLine(rawUserPrompt, 240)
            : string.Empty;

        if (updates is { HasAnyChange: true })
        {
            CustomWidgetDesignBrief applied = CustomWidgetStudioSessionStore.ApplyBriefUpdates(sanitized, updates);
            string anchorPurpose = !effectivelyEmpty ? sanitized.Purpose : normalizedStartIdea;
            if (updates.Reset != true &&
                !string.IsNullOrWhiteSpace(anchorPurpose) &&
                !string.IsNullOrWhiteSpace(normalizedRawPrompt) &&
                !string.Equals(anchorPurpose, normalizedRawPrompt, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(applied.Purpose) ||
                 string.Equals(NormalizeSingleLine(applied.Purpose, 240), normalizedRawPrompt, StringComparison.OrdinalIgnoreCase)))
            {
                applied = applied with { Purpose = anchorPurpose };
                applied = RollPromptLineIntoConfirmed(applied, rawUserPrompt!);
            }
            else if (string.IsNullOrWhiteSpace(applied.Purpose))
            {
                string fallback = ResolveCleanPurposeFallback(newManifest, rawUserPrompt, startDraftIdea);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    applied = applied with { Purpose = fallback };
                }
            }

            return applied;
        }

        if (effectivelyEmpty)
        {
            string seedPurpose = ResolveCleanPurposeFallback(newManifest, rawUserPrompt, startDraftIdea);
            if (!string.IsNullOrWhiteSpace(seedPurpose))
            {
                return sanitized with { Purpose = seedPurpose };
            }
        }

        // Never auto-roll raw user prompts or bug complaints into ConfirmedRequirements without structured updates.
        _ = isInitialOrEmptyBrief;
        return sanitized;
    }

    private static CustomWidgetDesignBrief RollPromptLineIntoConfirmed(
        CustomWidgetDesignBrief brief,
        string rawUserPrompt)
    {
        string rolledLine = NormalizeSingleLine(rawUserPrompt, 120);
        if (rolledLine.Length < 2 ||
            StudioTurnStageRouter.IsDiagnosticComplaintOnly(rolledLine) ||
            string.Equals(rolledLine, brief.Purpose, StringComparison.OrdinalIgnoreCase) ||
            brief.ConfirmedRequirements.Any(r => string.Equals(r, rolledLine, StringComparison.OrdinalIgnoreCase)))
        {
            return brief;
        }

        var updatedConfirmed = new List<string>(brief.ConfirmedRequirements) { rolledLine };
        while (updatedConfirmed.Count > CustomWidgetStudioSessionStore.MaxBulletsPerBriefSection)
        {
            updatedConfirmed.RemoveAt(0);
        }

        return brief with { ConfirmedRequirements = updatedConfirmed };
    }

    private static string ResolveCleanPurposeFallback(
        CustomWidgetManifest? newManifest,
        string? rawUserPrompt,
        string? startDraftIdea)
    {
        if (!string.IsNullOrWhiteSpace(startDraftIdea) &&
            !IsSyntheticRepairOrSystemPrompt(startDraftIdea))
        {
            return NormalizeSingleLine(startDraftIdea, 240);
        }

        if (newManifest is not null &&
            !string.IsNullOrWhiteSpace(newManifest.Description) &&
            !IsSyntheticRepairOrSystemPrompt(newManifest.Description))
        {
            return NormalizeSingleLine(newManifest.Description, 240);
        }

        if (!string.IsNullOrWhiteSpace(rawUserPrompt) &&
            !IsSyntheticRepairOrSystemPrompt(rawUserPrompt))
        {
            return NormalizeSingleLine(rawUserPrompt, 240);
        }

        if (newManifest is not null && !string.IsNullOrWhiteSpace(newManifest.Name))
        {
            return NormalizeSingleLine(newManifest.Name, 240);
        }

        return string.Empty;
    }

    private static bool ContainsLargeWriteBundleCall(AiChatTurnMessage turn)
    {
        if (!string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
            turn.ToolCalls is not { Count: > 0 })
        {
            return false;
        }

        foreach (AiToolCallSpec tc in turn.ToolCalls)
        {
            if (string.Equals(tc.Name, StudioToolTransport.ToolWriteBundle, StringComparison.Ordinal) &&
                tc.ArgumentsJson.Length > CompactableWriteBundleMinChars)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAnyWorkspaceMutationCall(AiChatTurnMessage turn)
    {
        if (!string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
            turn.ToolCalls is not { Count: > 0 })
        {
            return false;
        }

        foreach (AiToolCallSpec tc in turn.ToolCalls)
        {
            if (tc.Name is StudioToolTransport.ToolWriteBundle
                or StudioToolTransport.ToolEditManifest
                or StudioToolTransport.ToolPatchScript)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSingleLine(string text, int maxChars)
    {
        string singleLine = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return singleLine.Length <= maxChars ? singleLine : singleLine[..maxChars].TrimEnd();
    }

    private static string? ResolveBaseLocaleCulture(CustomWidgetLocaleCatalog locales, string? fallbackCulture)
    {
        string? normalizedFallback = !string.IsNullOrWhiteSpace(fallbackCulture)
            ? CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(fallbackCulture)
            : null;
        string? metaBase = locales.Meta?.BaseLocale;
        if (!string.IsNullOrWhiteSpace(normalizedFallback) && locales.Locales.ContainsKey(normalizedFallback))
        {
            return normalizedFallback;
        }
        if (!string.IsNullOrWhiteSpace(fallbackCulture) && locales.Locales.ContainsKey(fallbackCulture))
        {
            return fallbackCulture;
        }
        if (!string.IsNullOrWhiteSpace(metaBase) && locales.Locales.ContainsKey(metaBase))
        {
            return metaBase;
        }
        if (locales.Locales.ContainsKey("zh-CN"))
        {
            return "zh-CN";
        }
        return locales.Locales.Keys.FirstOrDefault();
    }
}
