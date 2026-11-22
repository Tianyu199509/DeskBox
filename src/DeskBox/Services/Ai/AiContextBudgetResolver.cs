using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Services;

public sealed record AiContextBudget(
    int EffectiveContextTokens,
    int MaxStudioOutputTokens,
    int ManifestFoldCharThreshold,
    int ManifestFoldLineThreshold,
    int ScriptFoldCharThreshold,
    int ScriptFoldLineThreshold,
    int MaxRecentChatMessages,
    int MaxBaseLocaleKeys,
    int MaxBaseLocaleChars);

/// <summary>
/// Resolves context-window budgets, workspace folding thresholds, and output token caps
/// from explicit user overrides, discovered <c>/models</c> metadata, model heuristics, and learned HTTP 400 limits.
/// </summary>
public static partial class AiContextBudgetResolver
{
    private const int DefaultExtendedContextTokens = 131_072;
    private const int DefaultLocalContextTokens = 32_768;
    internal const int OllamaAssumedContextTokens = 16_384;

    [GeneratedRegex(
        @"(?:maximum\s+context\s+length|context\s+length|context\s+window|max_seq_len|max_model_len|limit)\s*(?:is|of|:|=)?\s*(?<tokens>\d{4,7})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextLimitTokensRegex();

    [GeneratedRegex(
        @"[-_/](?<k>8|16|32|64|128)k\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelNameContextTagRegex();

    public static AiContextBudget Resolve(
        ResolvedAiInvocation resolved,
        int? runtimeOrDiscoveredContextTokens = null,
        int estimatedPromptChars = 0,
        bool forceCompact = false)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        int effectiveContext = ResolveEffectiveContextTokens(resolved, runtimeOrDiscoveredContextTokens);

        int manifestFoldChars;
        int manifestFoldLines;
        int scriptFoldChars;
        int scriptFoldLines;
        int maxRecentChatMessages;
        int maxBaseLocaleKeys;
        int maxBaseLocaleChars;
        int baseMaxOut;

        if (forceCompact || effectiveContext <= 16_384)
        {
            manifestFoldChars = 4_500;
            manifestFoldLines = 120;
            scriptFoldChars = 3_500;
            scriptFoldLines = 100;
            maxRecentChatMessages = 2;
            maxBaseLocaleKeys = 30;
            maxBaseLocaleChars = 1_800;
            baseMaxOut = 4_096;
        }
        else if (effectiveContext <= 32_768)
        {
            manifestFoldChars = 10_000;
            manifestFoldLines = 260;
            scriptFoldChars = 7_000;
            scriptFoldLines = 180;
            maxRecentChatMessages = 4;
            maxBaseLocaleKeys = 50;
            maxBaseLocaleChars = 3_000;
            baseMaxOut = 8_192;
        }
        else
        {
            manifestFoldChars = StudioWorkspaceEngine.ManifestFoldCharThreshold;
            manifestFoldLines = StudioWorkspaceEngine.ManifestFoldLineThreshold;
            scriptFoldChars = StudioWorkspaceEngine.ScriptFoldCharThreshold;
            scriptFoldLines = StudioWorkspaceEngine.ScriptFoldLineThreshold;
            maxRecentChatMessages = 6;
            maxBaseLocaleKeys = 80;
            maxBaseLocaleChars = 4_500;
            baseMaxOut = AiChatTransportClient.IsStrict8kStudioEndpoint(resolved) ? 8_192 : 16_384;
        }

        int inputTokens = Math.Max(1_024, (Math.Max(0, estimatedPromptChars) / 3) + 1_200);
        int maxOut = baseMaxOut;
        if (effectiveContext <= 65_536)
        {
            maxOut = Math.Clamp(Math.Min(baseMaxOut, effectiveContext - inputTokens - 512), 2_048, 32_768);
        }

        return new AiContextBudget(
            EffectiveContextTokens: effectiveContext,
            MaxStudioOutputTokens: maxOut,
            ManifestFoldCharThreshold: manifestFoldChars,
            ManifestFoldLineThreshold: manifestFoldLines,
            ScriptFoldCharThreshold: scriptFoldChars,
            ScriptFoldLineThreshold: scriptFoldLines,
            MaxRecentChatMessages: maxRecentChatMessages,
            MaxBaseLocaleKeys: maxBaseLocaleKeys,
            MaxBaseLocaleChars: maxBaseLocaleChars);
    }

    public static int ResolveEffectiveContextTokens(
        ResolvedAiInvocation resolved,
        int? runtimeOrDiscoveredContextTokens = null)
    {
        switch (resolved.ContextWindowMode)
        {
            case AiContextWindowMode.Compact16k:
                return 16_384;
            case AiContextWindowMode.Standard32k:
                return 32_768;
            case AiContextWindowMode.Extended128k:
                return DefaultExtendedContextTokens;
        }

        if (runtimeOrDiscoveredContextTokens is >= 4_096 and <= 2_097_152)
        {
            return runtimeOrDiscoveredContextTokens.Value;
        }

        string lowerModel = resolved.Model?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!string.IsNullOrEmpty(lowerModel))
        {
            if (lowerModel.Contains("moonshot-v1-8k", StringComparison.Ordinal))
            {
                return 8_192;
            }

            if (lowerModel.Contains("moonshot-v1-32k", StringComparison.Ordinal) ||
                string.Equals(lowerModel, "qwq-32b", StringComparison.Ordinal))
            {
                return 32_768;
            }

            Match tagMatch = ModelNameContextTagRegex().Match(lowerModel);
            if (tagMatch.Success &&
                int.TryParse(tagMatch.Groups["k"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kValue))
            {
                return kValue switch
                {
                    8 => 8_192,
                    16 => 16_384,
                    32 => 32_768,
                    64 => 65_536,
                    _ => DefaultExtendedContextTokens
                };
            }
        }

        if (IsOllamaRoute(resolved))
        {
            // Ollama's OpenAI-compatible /v1 endpoint ignores per-request "options.num_ctx" and silently
            // truncates prompts beyond the server context (no HTTP 400), so self-healing never triggers.
            // Budget for the minimum Studio-capable window; users raise it via OLLAMA_CONTEXT_LENGTH.
            return OllamaAssumedContextTokens;
        }

        if (IsLocalModelRoute(resolved))
        {
            return DefaultLocalContextTokens;
        }

        return DefaultExtendedContextTokens;
    }

    public static bool TryParseContextLimitFromErrorBody(string? errBody, out int limitTokens)
    {
        limitTokens = 0;
        if (string.IsNullOrWhiteSpace(errBody))
        {
            return false;
        }

        Match match = ContextLimitTokensRegex().Match(errBody);
        if (match.Success &&
            int.TryParse(match.Groups["tokens"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed is >= 2_048 and <= 2_097_152)
        {
            limitTokens = parsed;
            return true;
        }

        return false;
    }

    public static bool ErrorLooksLikeContextOverflow(string? errBody, out int? parsedLimitTokens)
    {
        parsedLimitTokens = null;
        if (string.IsNullOrWhiteSpace(errBody))
        {
            return false;
        }

        bool hasOverflowPhrase =
            errBody.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("max_seq_len", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("max_model_len", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("reduce the length of the messages", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("context window", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("context_window_exceeded", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("greater than the context length", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("exceeds the context", StringComparison.OrdinalIgnoreCase);

        if (!hasOverflowPhrase)
        {
            return false;
        }

        if (TryParseContextLimitFromErrorBody(errBody, out int limit))
        {
            parsedLimitTokens = limit;
        }

        return true;
    }

    public static Dictionary<string, int> ParseModelContextLengthsFromModelsJson(string? rawJson)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return result;
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
                return result;
            }

            foreach (JsonElement item in arrayEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? id = null;
                if (item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    id = idEl.GetString();
                }
                else if (item.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    id = nameEl.GetString();
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

                if (AiChatTransportClient.IsNonChatModelId(cleaned))
                {
                    continue;
                }

                if (TryExtractModelContextTokens(item, out int ctxTokens))
                {
                    result[cleaned] = ctxTokens;
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static bool TryExtractModelContextTokens(JsonElement item, out int tokens)
    {
        tokens = 0;
        ReadOnlySpan<string> directKeys =
        [
            "context_length",
            "max_model_len",
            "loaded_context_length",
            "context_window",
            "inputTokenLimit"
        ];

        foreach (string key in directKeys)
        {
            if (item.TryGetProperty(key, out JsonElement valEl) &&
                TryReadValidContextInt(valEl, out tokens))
            {
                return true;
            }
        }

        if (item.TryGetProperty("top_provider", out JsonElement topProv) &&
            topProv.ValueKind == JsonValueKind.Object &&
            topProv.TryGetProperty("context_length", out JsonElement tpCtx) &&
            TryReadValidContextInt(tpCtx, out tokens))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadValidContextInt(JsonElement el, out int tokens)
    {
        tokens = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int numVal) && numVal is >= 2_048 and <= 2_097_152)
        {
            tokens = numVal;
            return true;
        }

        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int strVal) &&
            strVal is >= 2_048 and <= 2_097_152)
        {
            tokens = strVal;
            return true;
        }

        return false;
    }

    internal static bool IsOllamaRoute(ResolvedAiInvocation resolved) =>
        string.Equals(resolved.Preset.Id, "ollama", StringComparison.OrdinalIgnoreCase) ||
        resolved.Endpoint.Contains(":11434", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocalModelRoute(ResolvedAiInvocation resolved) =>
        IsOllamaRoute(resolved) ||
        string.Equals(resolved.Preset.Id, "lmstudio", StringComparison.OrdinalIgnoreCase) ||
        resolved.Endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        resolved.Endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(resolved.Endpoint, UriKind.Absolute, out Uri? uri) && uri.IsLoopback);
}
