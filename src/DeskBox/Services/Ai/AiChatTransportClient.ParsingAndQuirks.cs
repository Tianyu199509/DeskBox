using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

internal static partial class AiChatTransportClient
{
    [GeneratedRegex("\"name\"\\s*:\\s*\"(?<name>[^\"\\\\\\r\\n]{1,48})\"", RegexOptions.CultureInvariant)]
    private static partial Regex CreateStreamingNameRegex();

    internal static readonly Regex StreamingNameRegex = CreateStreamingNameRegex();

    internal static List<string> ParseModelListJson(string rawJson)
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

    internal static bool IsNonChatModelId(string modelId)
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
    /// Extracts a valid JSON object (<c>{...}</c>) or array (<c>[...]</c>) from model output,
    /// stripping <c>&lt;think&gt;</c> reasoning blocks and Markdown code fences.
    /// </summary>
    internal static string ExtractStructuredJson(string? rawText)
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

    internal static string StripAllThinkBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        SplitInlineThinkBlock(text, out _, out string visibleContent, out _);
        return visibleContent;
    }

    internal static string ExtractBalancedJsonValue(string text)
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

    internal static string SliceBalanced(string text, int startIndex, char openChar, char closeChar)
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

    internal static bool IsValidJsonDocument(string candidate)
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

    private static bool TryPlanVendorThinkingEmission(
        ResolvedAiInvocation resolved,
        bool includeThinkingParams,
        bool isProbe,
        ref int? effectiveMaxTokens,
        out AiThinkingProtocol effectiveProtocol,
        out bool enableThinking,
        out int? claudeBudgetTokens)
    {
        effectiveProtocol = AiThinkingProtocol.None;
        enableThinking = resolved.ThinkingMode == AiThinkingMode.Enabled;
        claudeBudgetTokens = null;

        if (!includeThinkingParams || resolved.ThinkingMode == AiThinkingMode.Auto)
        {
            return false;
        }

        effectiveProtocol = ResolveEffectiveThinkingProtocol(resolved.Preset.ThinkingProtocol, resolved.Model);
        if (effectiveProtocol == AiThinkingProtocol.None)
        {
            return false;
        }

        if (isProbe && enableThinking && effectiveProtocol == AiThinkingProtocol.ThinkingTypeObject)
        {
            return false;
        }

        if (enableThinking &&
            effectiveProtocol == AiThinkingProtocol.ThinkingTypeObject &&
            resolved.Model.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveMaxTokens is <= 1024)
            {
                return false;
            }

            effectiveMaxTokens ??= 8192;
            claudeBudgetTokens = Math.Min(
                4096,
                Math.Min(effectiveMaxTokens.Value - 1, Math.Max(1024, effectiveMaxTokens.Value / 2)));
        }

        return true;
    }

    internal static AiThinkingProtocol ResolveEffectiveThinkingProtocol(AiThinkingProtocol protocol, string model)
    {
        if (AiProviderCatalog.IsKimiK25Model(model))
        {
            return AiThinkingProtocol.ThinkingTypeObject;
        }

        string lower = model.Trim().ToLowerInvariant();
        string leaf = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        if (AiProviderCatalog.IsLegacyNonThinkingKimiModel(model) ||
            leaf.StartsWith("kimi-thinking", StringComparison.Ordinal))
        {
            return AiThinkingProtocol.None;
        }

        AiThinkingProtocol effectiveProtocol = protocol;
        if (effectiveProtocol == AiThinkingProtocol.AutoDetectByModel)
        {
            if (lower.Contains("qwen", StringComparison.Ordinal))
            {
                return AiThinkingProtocol.EnableThinkingFlag;
            }

            if (lower.Contains("claude", StringComparison.Ordinal) ||
                lower.Contains("glm", StringComparison.Ordinal) ||
                lower.Contains("doubao", StringComparison.Ordinal) ||
                (lower.Contains("deepseek", StringComparison.Ordinal) &&
                 !AiProviderCatalog.IsReasoningOnlyModel(model)))
            {
                return AiThinkingProtocol.ThinkingTypeObject;
            }

            if (leaf.StartsWith("o1", StringComparison.Ordinal) ||
                leaf.StartsWith("o3", StringComparison.Ordinal) ||
                leaf.StartsWith("o4", StringComparison.Ordinal) ||
                leaf.StartsWith("gpt-5", StringComparison.Ordinal) ||
                lower.Contains("gemini", StringComparison.Ordinal))
            {
                return AiThinkingProtocol.ReasoningEffort;
            }
        }
        else if (effectiveProtocol == AiThinkingProtocol.DeepSeekModelSwap &&
                 !string.Equals(lower, "deepseek-chat", StringComparison.Ordinal) &&
                 !string.Equals(lower, "deepseek-reasoner", StringComparison.Ordinal) &&
                 !AiProviderCatalog.IsReasoningOnlyModel(model))
        {
            // Custom DeepSeek hybrid variants (e.g. deepseek-flash, deepseek-v3.1/v3.2)
            // control reasoning via thinking.type rather than model ID swapping.
            return AiThinkingProtocol.ThinkingTypeObject;
        }

        return effectiveProtocol;
    }

    internal static bool ShouldIncludeAssistantReasoningContentInHistory(
        ResolvedAiInvocation resolved,
        bool includeThinkingParams,
        bool forceIncludeReasoningHistory = false)
    {
        if (forceIncludeReasoningHistory)
        {
            return true;
        }

        if (!includeThinkingParams || resolved.ThinkingMode == AiThinkingMode.Disabled)
        {
            return false;
        }

        if (AiProviderCatalog.IsKimiK25Model(resolved.Model))
        {
            return true;
        }

        AiThinkingProtocol effectiveProtocol = ResolveEffectiveThinkingProtocol(
            resolved.Preset.ThinkingProtocol,
            resolved.Model);
        if (effectiveProtocol is AiThinkingProtocol.ThinkingTypeObject or AiThinkingProtocol.DeepSeekModelSwap)
        {
            return true;
        }

        return includeThinkingParams && AiProviderCatalog.IsReasoningOnlyModel(resolved.Model);
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
        AiThinkingMode mode,
        int? maxTokens = null)
    {
        bool enable = mode == AiThinkingMode.Enabled;
        AiThinkingProtocol effectiveProtocol = ResolveEffectiveThinkingProtocol(protocol, model);
        if (effectiveProtocol == AiThinkingProtocol.None)
        {
            return;
        }

        int? claudeBudgetTokens = null;
        if (enable &&
            effectiveProtocol == AiThinkingProtocol.ThinkingTypeObject &&
            model.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            if (maxTokens is <= 1024)
            {
                return;
            }

            claudeBudgetTokens = maxTokens.HasValue
                ? Math.Min(4096, Math.Max(1024, maxTokens.Value / 2))
                : 4096;
        }

        WriteVendorThinkingParametersCore(writer, effectiveProtocol, model, enable, claudeBudgetTokens);
    }

    private static void WriteVendorThinkingParametersCore(
        Utf8JsonWriter writer,
        AiThinkingProtocol effectiveProtocol,
        string model,
        bool enable,
        int? claudeBudgetTokens)
    {
        switch (effectiveProtocol)
        {
            case AiThinkingProtocol.EnableThinkingFlag:
                writer.WriteBoolean("enable_thinking", enable);
                break;

            case AiThinkingProtocol.ThinkingTypeObject:
                writer.WriteStartObject("thinking");
                writer.WriteString("type", enable ? "enabled" : "disabled");
                if (enable && claudeBudgetTokens.HasValue)
                {
                    writer.WriteNumber("budget_tokens", claudeBudgetTokens.Value);
                }
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
                else if (AiProviderCatalog.IsReasoningOnlyModel(model) &&
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

    private struct DeltaTextCollector
    {
        private string? _first;
        private StringBuilder? _builder;

        public readonly bool HasValue => _builder is { Length: > 0 } || !string.IsNullOrEmpty(_first);

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (_builder is not null)
            {
                _builder.Append(text);
            }
            else if (_first is null)
            {
                _first = text;
            }
            else
            {
                _builder = new StringBuilder(_first.Length + text.Length);
                _builder.Append(_first);
                _builder.Append(text);
            }
        }

        public readonly string Consume() =>
            _builder is not null ? _builder.ToString() : (_first ?? string.Empty);
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
        out string? finishReason) =>
        TryAppendSseDeltaCore(
            dataJson,
            chunk => contentBuilder.Append(chunk),
            chunk => reasoningBuilder.Append(chunk),
            toolAccumulator: null,
            out streamError,
            out finishReason,
            out _,
            out _);

    internal static bool TryAppendSseDeltaCore(
        string dataJson,
        Action<string> appendContent,
        Action<string> appendReasoning,
        StreamingTurnAccumulator? toolAccumulator,
        out string? streamError,
        out string? finishReason,
        out SseChunkUsage usage,
        out bool isCompleteJson)
    {
        streamError = null;
        finishReason = null;
        usage = default;
        isCompleteJson = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            isCompleteJson = true;
            JsonElement root = doc.RootElement;

            if (TryExtractUsageMetrics(root, out int pt, out int cachedPt, out int comp))
            {
                usage = new SseChunkUsage(true, pt, cachedPt, comp);
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out JsonElement errEl) &&
                errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False))
            {
                streamError = TryExtractErrorFromElement(root) ?? Localized.T("CustomWidget.AiApi.Error.InvocationFailed");
                return false;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("choices", out JsonElement choices) &&
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
                DeltaTextCollector deltaContent = default;
                DeltaTextCollector deltaReason = default;

                if (first.TryGetProperty("delta", out JsonElement delta) &&
                    delta.ValueKind == JsonValueKind.Object)
                {
                    appended |= TryAppendMessageLikeFields(delta, ref deltaContent, ref deltaReason);
                    if (toolAccumulator is not null)
                    {
                        appended |= TryAppendStreamingToolCalls(delta, toolAccumulator);
                    }
                }

                // Some OpenAI-compatible relays emit an empty delta alongside a final
                // message object or top-level choice text in the closing SSE event.
                if (!appended &&
                    first.TryGetProperty("message", out JsonElement msg) &&
                    msg.ValueKind == JsonValueKind.Object)
                {
                    appended |= TryAppendMessageLikeFields(msg, ref deltaContent, ref deltaReason);
                    if (toolAccumulator is not null && !toolAccumulator.HasToolCalls)
                    {
                        appended |= TryAppendStreamingToolCalls(msg, toolAccumulator);
                    }
                }

                if (!appended &&
                    first.TryGetProperty("text", out JsonElement textEl))
                {
                    appended |= AppendContentElement(textEl, ref deltaContent, ref deltaReason);
                }

                if (deltaReason.HasValue)
                {
                    appendReasoning(deltaReason.Consume());
                }
                if (deltaContent.HasValue)
                {
                    appendContent(deltaContent.Consume());
                }

                return appended;
            }

            // OpenAI Responses-style SSE bridge events returned by modern API gateways.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out JsonElement typeEl) &&
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
                        appendContent(chunk);
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
                        appendReasoning(chunk);
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

    internal static bool TryAppendStreamingToolCalls(JsonElement obj, StreamingTurnAccumulator accumulator)
    {
        if (!obj.TryGetProperty("tool_calls", out JsonElement tcArr) ||
            tcArr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        bool any = false;
        int fallbackIdx = 0;
        foreach (JsonElement item in tcArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                fallbackIdx++;
                continue;
            }

            int idx = item.TryGetProperty("index", out JsonElement idxEl) &&
                      idxEl.ValueKind == JsonValueKind.Number &&
                      idxEl.TryGetInt32(out int parsedIdx)
                ? parsedIdx
                : fallbackIdx;
            fallbackIdx++;

            string? id = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            string? fnName = null;
            string? fnArgs = null;
            if (item.TryGetProperty("function", out JsonElement fnEl) && fnEl.ValueKind == JsonValueKind.Object)
            {
                if (fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    fnName = nameEl.GetString();
                }

                if (fnEl.TryGetProperty("arguments", out JsonElement argsEl) && argsEl.ValueKind == JsonValueKind.String)
                {
                    fnArgs = argsEl.GetString();
                }
            }

            if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(fnName) || !string.IsNullOrEmpty(fnArgs))
            {
                accumulator.AppendToolCallDelta(idx, id, fnName, fnArgs);
                any = true;
            }
        }

        return any;
    }

    private static bool TryAppendMessageLikeFields(
        JsonElement obj,
        ref DeltaTextCollector contentCollector,
        ref DeltaTextCollector reasoningCollector)
    {
        bool appended = false;
        if (obj.TryGetProperty("reasoning_content", out JsonElement rcEl))
        {
            appended |= AppendReasoningElement(rcEl, ref reasoningCollector);
        }
        else if (obj.TryGetProperty("reasoning", out JsonElement rEl))
        {
            appended |= AppendReasoningElement(rEl, ref reasoningCollector);
        }
        else if (obj.TryGetProperty("thinking", out JsonElement tEl))
        {
            appended |= AppendReasoningElement(tEl, ref reasoningCollector);
        }

        if (obj.TryGetProperty("content", out JsonElement cEl))
        {
            appended |= AppendContentElement(cEl, ref contentCollector, ref reasoningCollector);
        }

        return appended;
    }

    private static bool AppendReasoningElement(JsonElement el, ref DeltaTextCollector reasoningCollector)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string? text = el.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                reasoningCollector.Append(text);
                return true;
            }

            return false;
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in (ReadOnlySpan<string>)["thinking", "reasoning", "text", "content", "summary"])
            {
                if (el.TryGetProperty(key, out JsonElement child) &&
                    AppendReasoningElement(child, ref reasoningCollector))
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
                any |= AppendReasoningElement(item, ref reasoningCollector);
            }

            return any;
        }

        return false;
    }

    private static bool AppendContentElement(
        JsonElement el,
        ref DeltaTextCollector contentCollector,
        ref DeltaTextCollector reasoningCollector)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string? text = el.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                contentCollector.Append(text);
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
                        contentCollector.Append(s);
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
                    any |= AppendReasoningElement(part, ref reasoningCollector);
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
                            contentCollector.Append(chunk);
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
                contentCollector.Append(chunk);
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<char> ExtractSseDataFieldSlice(string line)
    {
        ReadOnlySpan<char> trimmedStart = line.AsSpan().TrimStart();
        ReadOnlySpan<char> rest = trimmedStart["data:".Length..];
        if (!rest.IsEmpty && rest[0] == ' ')
        {
            rest = rest[1..];
        }

        if (!rest.IsEmpty && rest[^1] == '\r')
        {
            rest = rest[..^1];
        }

        return rest;
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
        StringBuilder? pendingData = null;
        bool any = false;

        using var reader = new StringReader(sseBody);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                if (pendingData is { Length: > 0 })
                {
                    string combined = pendingData.ToString();
                    pendingData.Clear();
                    if (TryAppendSseDelta(combined, contentSb, reasoningSb, out _))
                    {
                        any = true;
                    }
                }
                continue;
            }

            string trimmedStart = line.TrimStart();
            if (!trimmedStart.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<char> slice = ExtractSseDataFieldSlice(line);
            ReadOnlySpan<char> sliceTrimmed = slice.Trim();
            if (sliceTrimmed.Equals("[DONE]", StringComparison.Ordinal))
            {
                if (pendingData is { Length: > 0 })
                {
                    string combined = pendingData.ToString();
                    pendingData.Clear();
                    if (TryAppendSseDelta(combined, contentSb, reasoningSb, out _))
                    {
                        any = true;
                    }
                }
                break;
            }

            if (pendingData is { Length: > 0 })
            {
                if (pendingData.Length + 1 + slice.Length <= MaxSseLineChars)
                {
                    pendingData.Append('\n').Append(slice);
                }
                continue;
            }

            if (sliceTrimmed.IsEmpty)
            {
                continue;
            }

            string singleLinePayload = sliceTrimmed.ToString();
            bool appended = TryAppendSseDeltaCore(
                singleLinePayload,
                chunk => contentSb.Append(chunk),
                chunk => reasoningSb.Append(chunk),
                toolAccumulator: null,
                out _,
                out _,
                out _,
                out bool isCompleteJson);
            if (isCompleteJson)
            {
                if (appended)
                {
                    any = true;
                }
            }
            else
            {
                (pendingData ??= new StringBuilder(slice.Length * 2)).Append(slice);
            }
        }

        if (pendingData is { Length: > 0 })
        {
            if (TryAppendSseDelta(pendingData.ToString(), contentSb, reasoningSb, out _))
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
        TryExtractNonStreamingReply(rawJson, out content, out reasoning, out _, out _);

    internal static bool TryExtractNonStreamingReply(
        string rawJson,
        out string content,
        out string reasoning,
        out string? finishReason,
        out List<AiToolCallSpec> toolCalls)
    {
        content = string.Empty;
        reasoning = string.Empty;
        finishReason = null;
        toolCalls = [];
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            DeltaTextCollector contentCol = default;
            DeltaTextCollector reasoningCol = default;

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
                    extracted |= TryAppendMessageLikeFields(msg, ref contentCol, ref reasoningCol);
                    if (msg.TryGetProperty("content", out JsonElement cEl) &&
                        cEl.ValueKind == JsonValueKind.String)
                    {
                        extracted = true;
                    }

                    if (TryExtractNonStreamingToolCalls(msg, toolCalls))
                    {
                        extracted = true;
                    }
                }

                if (!contentCol.HasValue && first.TryGetProperty("text", out JsonElement textEl))
                {
                    extracted |= AppendContentElement(textEl, ref contentCol, ref reasoningCol);
                }

                if (extracted || contentCol.HasValue || reasoningCol.HasValue || toolCalls.Count > 0)
                {
                    content = contentCol.Consume();
                    reasoning = reasoningCol.Consume();
                    return true;
                }
            }

            // Support OpenAI Responses API JSON payloads bridged by third-party relays.
            if (root.TryGetProperty("output_text", out JsonElement outputTextEl) &&
                AppendContentElement(outputTextEl, ref contentCol, ref reasoningCol))
            {
                content = contentCol.Consume();
                reasoning = reasoningCol.Consume();
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
                        anyOutput |= AppendReasoningElement(item, ref reasoningCol);
                    }
                    else if (item.TryGetProperty("content", out JsonElement itemContent))
                    {
                        anyOutput |= AppendContentElement(itemContent, ref contentCol, ref reasoningCol);
                    }
                }

                if (anyOutput)
                {
                    content = contentCol.Consume();
                    reasoning = reasoningCol.Consume();
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool TryExtractNonStreamingToolCalls(JsonElement msg, List<AiToolCallSpec> toolCalls)
    {
        if (!msg.TryGetProperty("tool_calls", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        int fallbackIdx = 0;
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                fallbackIdx++;
                continue;
            }

            string id = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? (idEl.GetString() ?? $"call_{fallbackIdx}")
                : $"call_{fallbackIdx}";
            fallbackIdx++;

            if (!item.TryGetProperty("function", out JsonElement fnEl) || fnEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = fnEl.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            string args = fnEl.TryGetProperty("arguments", out JsonElement argsEl)
                ? (argsEl.ValueKind == JsonValueKind.String ? (argsEl.GetString() ?? "{}") : argsEl.GetRawText())
                : "{}";
            if (!string.IsNullOrWhiteSpace(name))
            {
                toolCalls.Add(new AiToolCallSpec(id, name.Trim(), args));
            }
        }

        return toolCalls.Count > 0;
    }

    internal static bool TryExtractUsageFromJson(
        string? rawJson,
        out int promptTokens,
        out int cachedPromptTokens,
        out int completionTokens)
    {
        promptTokens = 0;
        cachedPromptTokens = 0;
        completionTokens = 0;
        if (!HasObjectUsageProperty(rawJson))
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

    private static bool HasObjectUsageProperty([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        int searchFrom = 0;
        while (searchFrom < rawJson.Length)
        {
            int idx = rawJson.IndexOf("\"usage\"", searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            int cursor = idx + "\"usage\"".Length;
            while (cursor < rawJson.Length && char.IsWhiteSpace(rawJson[cursor]))
            {
                cursor++;
            }

            if (cursor < rawJson.Length && rawJson[cursor] == ':')
            {
                cursor++;
                while (cursor < rawJson.Length && char.IsWhiteSpace(rawJson[cursor]))
                {
                    cursor++;
                }

                if (cursor < rawJson.Length && rawJson[cursor] == '{')
                {
                    return true;
                }
            }

            searchFrom = idx + "\"usage\"".Length;
        }

        return false;
    }

    internal static bool TryExtractUsageMetrics(
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
            return TryExtractErrorFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractErrorFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out JsonElement errEl) &&
            errEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined))
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

        if (!root.TryGetProperty("choices", out JsonElement choicesEl) ||
            choicesEl.ValueKind == JsonValueKind.Null ||
            (choicesEl.ValueKind == JsonValueKind.Array && choicesEl.GetArrayLength() == 0))
        {
            if (root.TryGetProperty("message", out JsonElement rootMsg) &&
                rootMsg.ValueKind == JsonValueKind.String &&
                rootMsg.GetString() is { Length: > 0 } msgText)
            {
                return msgText;
            }
        }

        return null;
    }

    internal static string? TryExtractApiErrorMessage(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        if (TryExtractStructuredJsonErrorMessage(rawJson) is { Length: > 0 } structured)
        {
            return structured;
        }

        string singleLine = rawJson.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 180 ? singleLine[..180] + "…" : singleLine;
    }

    internal static bool ErrorRequiresReasoningContentInHistory(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        return errorBody.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ErrorMentionsUnsupportedThinkingParams(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody) || ErrorRequiresReasoningContentInHistory(errorBody))
        {
            return false;
        }

        bool mentionsThinkingField =
            errorBody.Contains("enable_thinking", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("\"thinking\"", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("'thinking'", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("thinking.", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("thinking parameter", StringComparison.OrdinalIgnoreCase) ||
            errorBody.Contains("thinking mode", StringComparison.OrdinalIgnoreCase);
        if (!mentionsThinkingField)
        {
            return false;
        }

        return errorBody.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("not support", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("extra_forbidden", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("cannot", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ErrorMentionsUnsupportedTools(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody) ||
            ErrorRequiresReasoningContentInHistory(errorBody) ||
            ErrorMentionsUnsupportedThinkingParams(errorBody))
        {
            return false;
        }

        return errorBody.Contains("tools", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("tool_choice", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("function calling", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("functions", StringComparison.OrdinalIgnoreCase) ||
               errorBody.Contains("tool call", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ErrorLooksLikeMaxTokensRejection(string? errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody))
        {
            return false;
        }

        bool mentionsTokens = errorBody.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ||
                              errorBody.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase);
        if (!mentionsTokens)
        {
            return false;
        }

        return !errorBody.Contains("thinking", StringComparison.OrdinalIgnoreCase) &&
               !errorBody.Contains("enable_thinking", StringComparison.OrdinalIgnoreCase) &&
               !errorBody.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<AiChatTurnMessage> FlattenStructuredTurnsForBlockFallback(
        IReadOnlyList<AiChatTurnMessage> structuredTurns)
    {
        if (structuredTurns.Count == 0)
        {
            return structuredTurns;
        }

        bool needsFlatten = structuredTurns.Any(static t =>
            string.Equals(t.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
            (t.ToolCalls is { Count: > 0 }));
        if (!needsFlatten)
        {
            return structuredTurns;
        }

        var flattened = new List<AiChatTurnMessage>(structuredTurns.Count);
        var pendingToolOutputs = new StringBuilder();

        void FlushToolOutputs()
        {
            if (pendingToolOutputs.Length == 0)
            {
                return;
            }

            flattened.Add(new AiChatTurnMessage("user", pendingToolOutputs.ToString().TrimEnd()));
            pendingToolOutputs.Clear();
        }

        foreach (AiChatTurnMessage turn in structuredTurns)
        {
            if (string.Equals(turn.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                string label = string.IsNullOrWhiteSpace(turn.ToolName) ? "tool" : turn.ToolName;
                pendingToolOutputs.Append('[').Append(label).AppendLine("]");
                pendingToolOutputs.AppendLine(turn.Content ?? string.Empty);
                continue;
            }

            FlushToolOutputs();

            if (string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                turn.ToolCalls is { Count: > 0 })
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(turn.Content))
                {
                    sb.AppendLine(turn.Content);
                }
                foreach (AiToolCallSpec tc in turn.ToolCalls)
                {
                    sb.Append("[Tool call: ").Append(tc.Name).Append("] ").AppendLine(tc.ArgumentsJson);
                }
                flattened.Add(new AiChatTurnMessage("assistant", sb.ToString().TrimEnd(), turn.ReasoningContent));
            }
            else
            {
                flattened.Add(turn);
            }
        }

        FlushToolOutputs();
        return flattened;
    }

    internal static string SanitizeErrorBodyExcerpt(string? errBody, int maxChars = 240)
    {
        if (string.IsNullOrWhiteSpace(errBody))
        {
            return "(empty)";
        }

        var sb = new StringBuilder(Math.Min(errBody.Length, maxChars));
        bool lastWasSpace = false;
        foreach (char ch in errBody)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }

            if (sb.Length >= maxChars)
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    internal static void SplitInlineThinkBlock(
        string rawContent,
        out string inlineThinking,
        out string visibleContent,
        out bool stillInsideThink)
    {
        inlineThinking = string.Empty;
        visibleContent = rawContent;
        stillInsideThink = false;

        if (string.IsNullOrEmpty(rawContent))
        {
            return;
        }

        ReadOnlySpan<(string Open, string Close)> tags =
        [
            ("<think>", "</think>"),
            ("<thinking>", "</thinking>"),
            ("<thought>", "</thought>")
        ];

        bool hasAnyOpen = false;
        foreach ((string openTag, _) in tags)
        {
            if (rawContent.IndexOf(openTag, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hasAnyOpen = true;
                break;
            }
        }

        if (!hasAnyOpen)
        {
            return;
        }

        var thinkSb = new StringBuilder();
        var visibleSb = new StringBuilder();
        int cursor = 0;

        while (cursor < rawContent.Length)
        {
            int nextOpen = -1;
            string matchedOpen = string.Empty;
            string matchedClose = string.Empty;
            foreach ((string openTag, string closeTag) in tags)
            {
                int idx = rawContent.IndexOf(openTag, cursor, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (nextOpen < 0 || idx < nextOpen))
                {
                    nextOpen = idx;
                    matchedOpen = openTag;
                    matchedClose = closeTag;
                }
            }

            if (nextOpen < 0)
            {
                visibleSb.Append(rawContent, cursor, rawContent.Length - cursor);
                break;
            }

            if (nextOpen > cursor)
            {
                visibleSb.Append(rawContent, cursor, nextOpen - cursor);
            }

            int afterOpen = nextOpen + matchedOpen.Length;
            int closeIdx = rawContent.IndexOf(matchedClose, afterOpen, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                if (thinkSb.Length > 0)
                {
                    thinkSb.Append('\n');
                }
                thinkSb.Append(rawContent[afterOpen..].TrimStart());
                stillInsideThink = true;
                break;
            }

            string chunkThink = rawContent[afterOpen..closeIdx].Trim();
            if (chunkThink.Length > 0)
            {
                if (thinkSb.Length > 0)
                {
                    thinkSb.Append('\n');
                }
                thinkSb.Append(chunkThink);
            }

            cursor = closeIdx + matchedClose.Length;
        }

        inlineThinking = thinkSb.ToString();
        visibleContent = stillInsideThink
            ? visibleSb.ToString().Trim()
            : visibleSb.ToString().TrimStart();
    }

    internal static string MergeThinking(string deltaReasoning, string inlineThinking)
    {
        if (string.IsNullOrWhiteSpace(deltaReasoning))
        {
            return inlineThinking;
        }

        if (string.IsNullOrWhiteSpace(inlineThinking))
        {
            return deltaReasoning;
        }

        return deltaReasoning.TrimEnd() + "\n" + inlineThinking;
    }

    internal static string? TryExtractStreamingWidgetName(string partialContent)
    {
        if (string.IsNullOrWhiteSpace(partialContent))
        {
            return null;
        }

        Match match = StreamingNameRegex.Match(partialContent);
        if (!match.Success)
        {
            return null;
        }

        string candidate = match.Groups["name"].Value.Trim();
        return candidate.Length > 0 ? candidate : null;
    }
}
