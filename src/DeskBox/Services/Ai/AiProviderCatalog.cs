using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

internal static class AiProviderCatalog
{
    internal const string CustomProviderId = "studio-custom";


    internal static IReadOnlyList<CustomWidgetAiProviderPreset> SupportedProviders { get; } =
    [
        new(
            "deepseek",
            "DeepSeek",
            "https://api.deepseek.com/chat/completions",
            "deepseek-chat",
            ["deepseek-chat", "deepseek-reasoner"],
            AiThinkingProtocol.DeepSeekModelSwap,
            ConsoleUrl: "https://platform.deepseek.com/api_keys"),
        new(
            "openai",
            "OpenAI",
            "https://api.openai.com/v1/chat/completions",
            "gpt-4.1-mini",
            ["gpt-4.1-mini", "gpt-5-mini", "gpt-5", "gpt-4.1", "o4-mini", "gpt-4o-mini"],
            AiThinkingProtocol.ReasoningEffort,
            ConsoleUrl: "https://platform.openai.com/api-keys"),
        new(
            "anthropic",
            "Anthropic Claude",
            "https://api.anthropic.com/v1/chat/completions",
            "claude-sonnet-4-5",
            ["claude-sonnet-4-5", "claude-opus-4-5", "claude-haiku-4-5", "claude-sonnet-4-20250514", "claude-3-7-sonnet-latest"],
            AiThinkingProtocol.ThinkingTypeObject,
            ConsoleUrl: "https://console.anthropic.com/settings/keys"),
        new(
            "gemini",
            "Google Gemini",
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            "gemini-2.5-flash",
            ["gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite"],
            AiThinkingProtocol.ReasoningEffort,
            ConsoleUrl: "https://aistudio.google.com/apikey"),
        new(
            "openrouter",
            "OpenRouter",
            "https://openrouter.ai/api/v1/chat/completions",
            "deepseek/deepseek-chat-v3-0324",
            ["deepseek/deepseek-chat-v3-0324", "deepseek/deepseek-r1", "anthropic/claude-sonnet-4.5", "openai/gpt-5-mini", "openai/gpt-4.1-mini", "google/gemini-2.5-flash"],
            AiThinkingProtocol.OpenRouterReasoning,
            ConsoleUrl: "https://openrouter.ai/keys"),
        new(
            "groq",
            "Groq",
            "https://api.groq.com/openai/v1/chat/completions",
            "llama-3.3-70b-versatile",
            ["llama-3.3-70b-versatile", "openai/gpt-oss-120b", "qwen/qwen3-32b"],
            AiThinkingProtocol.None,
            ConsoleUrl: "https://console.groq.com/keys"),
        new(
            "ollama",
            "Ollama (Local)",
            "http://127.0.0.1:11434/v1/chat/completions",
            "qwen2.5-coder:7b",
            ["qwen2.5-coder:7b", "qwen3:8b", "deepseek-r1:8b", "gemma3:12b", "llama3.2"],
            AiThinkingProtocol.None,
            DisplayNameKey: "CustomWidget.AiApi.Provider.Ollama",
            RequiresApiKey: false,
            ConsoleUrl: "https://ollama.com/library"),
        new(
            "lmstudio",
            "LM Studio (Local)",
            "http://127.0.0.1:1234/v1/chat/completions",
            "local-model",
            ["local-model"],
            AiThinkingProtocol.None,
            DisplayNameKey: "CustomWidget.AiApi.Provider.LmStudio",
            RequiresApiKey: false,
            ConsoleUrl: "https://lmstudio.ai/"),
        new(
            "qwen",
            "Qwen (Alibaba Cloud)",
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            "qwen-plus",
            ["qwen-plus", "qwen3-max", "qwen3-coder-plus", "qwen-flash", "qwen-turbo", "qwq-32b"],
            AiThinkingProtocol.EnableThinkingFlag,
            DisplayNameKey: "CustomWidget.AiApi.Provider.Qwen",
            ConsoleUrl: "https://bailian.console.aliyun.com/",
            GlobalDefaultEndpoint: "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions",
            IsMainlandFocused: true),
        new(
            "glm",
            "Zhipu GLM",
            "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            "glm-4.5",
            ["glm-4.5", "glm-4.6", "glm-4.5-air", "glm-4-flash", "glm-z1-air"],
            AiThinkingProtocol.ThinkingTypeObject,
            DisplayNameKey: "CustomWidget.AiApi.Provider.Glm",
            ConsoleUrl: "https://open.bigmodel.cn/usercenter/apikeys",
            GlobalDefaultEndpoint: "https://api.z.ai/api/paas/v4/chat/completions",
            IsMainlandFocused: true),
        new(
            "doubao",
            "Doubao (Volcengine)",
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
            "doubao-seed-1-6-250615",
            ["doubao-seed-1-6-250615", "doubao-seed-1-6-flash-250828", "deepseek-v3-250324", "deepseek-r1-250528"],
            AiThinkingProtocol.ThinkingTypeObject,
            DisplayNameKey: "CustomWidget.AiApi.Provider.Doubao",
            ConsoleUrl: "https://console.volcengine.com/ark",
            IsMainlandFocused: true),
        new(
            "kimi",
            "Kimi (Moonshot)",
            "https://api.moonshot.cn/v1/chat/completions",
            "kimi-k2.5",
            ["kimi-k2.5", "kimi-k2-turbo-preview", "kimi-k2-0711-preview", "moonshot-v1-32k", "kimi-thinking-preview"],
            AiThinkingProtocol.ThinkingTypeObject,
            ConsoleUrl: "https://platform.moonshot.cn/console/api-keys",
            GlobalDefaultEndpoint: "https://api.moonshot.ai/v1/chat/completions",
            IsMainlandFocused: true),
        new(
            "siliconflow",
            "SiliconFlow",
            "https://api.siliconflow.cn/v1/chat/completions",
            "deepseek-ai/DeepSeek-V3",
            ["deepseek-ai/DeepSeek-V3", "deepseek-ai/DeepSeek-R1", "Qwen/Qwen3-32B", "Qwen/Qwen3-Coder-30B-A3B-Instruct", "Qwen/Qwen2.5-Coder-32B-Instruct"],
            AiThinkingProtocol.EnableThinkingFlag,
            DisplayNameKey: "CustomWidget.AiApi.Provider.SiliconFlow",
            ConsoleUrl: "https://cloud.siliconflow.cn/account/ak",
            GlobalDefaultEndpoint: "https://api.siliconflow.com/v1/chat/completions",
            IsMainlandFocused: true),
        new(
            CustomProviderId,
            "Custom OpenAI-Compatible API",
            "https://api.openai.com/v1/chat/completions",
            "gpt-4.1-mini",
            ["gpt-4.1-mini", "gpt-5-mini", "deepseek-chat", "deepseek-reasoner", "claude-sonnet-4-5", "gemini-2.5-flash", "qwen-plus"],
            AiThinkingProtocol.AutoDetectByModel,
            DisplayNameKey: "CustomWidget.AiApi.Provider.Custom")
    ];

    internal static readonly string[] MainlandProviderOrder =
    [
        "deepseek", "qwen", "doubao", "kimi", "glm", "siliconflow",
        "openai", "anthropic", "gemini", "openrouter", "groq",
        "ollama", "lmstudio", CustomProviderId
    ];

    internal static readonly string[] GlobalProviderOrder =
    [
        "openai", "anthropic", "gemini", "openrouter", "deepseek", "groq",
        "ollama", "lmstudio",
        "qwen", "kimi", "glm", "siliconflow", "doubao", CustomProviderId
    ];

    internal static bool IsSimplifiedChineseRegion(string? cultureName = null)
    {
        string effective = string.IsNullOrWhiteSpace(cultureName) ||
                           string.Equals(cultureName, "System", StringComparison.OrdinalIgnoreCase)
            ? Localized.ActiveCultureName
            : cultureName.Trim();

        if (effective.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            effective.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            effective.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
            effective.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return effective.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetDefaultStarterProviderId(string? cultureName = null) =>
        IsSimplifiedChineseRegion(cultureName) ? "deepseek" : "openai";

    internal static IReadOnlyList<CustomWidgetAiProviderPreset> GetOrderedProvidersForCulture(string? cultureName = null)
    {
        string[] order = IsSimplifiedChineseRegion(cultureName) ? MainlandProviderOrder : GlobalProviderOrder;
        var map = SupportedProviders.ToDictionary(static p => p.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<CustomWidgetAiProviderPreset>(SupportedProviders.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in order)
        {
            if (map.TryGetValue(id, out CustomWidgetAiProviderPreset? preset) && seen.Add(preset.Id))
            {
                result.Add(preset);
            }
        }

        foreach (CustomWidgetAiProviderPreset preset in SupportedProviders)
        {
            if (seen.Add(preset.Id))
            {
                result.Add(preset);
            }
        }

        return result;
    }


    internal static string RedactUrlForLog(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return "(empty)";
        }

        if (Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri) &&
            !string.IsNullOrWhiteSpace(uri.Scheme) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }

        return "(invalid-url)";
    }


    internal static AiThinkingMode ParseThinkingMode(string? raw)
    {
        if (string.Equals(raw?.Trim(), "enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw?.Trim(), "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return AiThinkingMode.Enabled;
        }

        if (string.Equals(raw?.Trim(), "disabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw?.Trim(), "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return AiThinkingMode.Disabled;
        }

        return AiThinkingMode.Auto;
    }

    internal static string ToThinkingWireValue(AiThinkingMode mode) => mode switch
    {
        AiThinkingMode.Enabled => "enabled",
        AiThinkingMode.Disabled => "disabled",
        _ => "auto"
    };

    internal static AiContextWindowMode ParseContextWindowMode(string? raw)
    {
        string trimmed = raw?.Trim() ?? string.Empty;
        if (string.Equals(trimmed, "16k", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "compact", StringComparison.OrdinalIgnoreCase))
        {
            return AiContextWindowMode.Compact16k;
        }

        if (string.Equals(trimmed, "32k", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return AiContextWindowMode.Standard32k;
        }

        if (string.Equals(trimmed, "128k", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "extended", StringComparison.OrdinalIgnoreCase))
        {
            return AiContextWindowMode.Extended128k;
        }

        return AiContextWindowMode.Auto;
    }

    internal static string ToContextWindowWireValue(AiContextWindowMode mode) => mode switch
    {
        AiContextWindowMode.Compact16k => "16k",
        AiContextWindowMode.Standard32k => "32k",
        AiContextWindowMode.Extended128k => "128k",
        _ => "auto"
    };

    internal static CustomWidgetAiProviderPreset FindProviderPreset(string? providerId) =>
        SupportedProviders.FirstOrDefault(p =>
            string.Equals(p.Id, providerId?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? SupportedProviders[0];


    internal static bool IsInternationalRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return false;
        }

        string trimmed = region.Trim();
        return string.Equals(trimmed, "global", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "intl", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return "cn";
        }

        string trimmed = region.Trim();
        if (IsInternationalRegion(trimmed))
        {
            return "global";
        }

        if (string.Equals(trimmed, "cn", StringComparison.OrdinalIgnoreCase))
        {
            return "cn";
        }

        return trimmed.ToLowerInvariant();
    }

    internal static string NormalizeChatCompletionsEndpoint(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return "https://api.openai.com/v1/chat/completions";
        }

        if (!TryNormalizeSchemePrefix(rawUrl, out string trimmed))
        {
            return string.Empty;
        }

        string candidate;
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed;
        }
        else if (trimmed.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed[..^"/messages".Length] + "/chat/completions";
        }
        else if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed[..^"/messages".Length] + "/v1/chat/completions";
        }
        else if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith("/v3", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith("/v4", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            candidate = trimmed + "/chat/completions";
        }
        else
        {
            candidate = trimmed + "/v1/chat/completions";
        }

        return IsValidHttpEndpointUri(candidate) ? candidate : string.Empty;
    }

    internal static string NormalizeGatewayBaseUrl(string? rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return string.Empty;
        }

        if (!TryNormalizeSchemePrefix(rawEndpoint, out string s))
        {
            return string.Empty;
        }

        ReadOnlySpan<string> suffixes =
        [
            "/v1/chat/completions",
            "/chat/completions",
            "/v1/messages",
            "/v1/models",
            "/v1"
        ];
        foreach (string suffix in suffixes)
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        return IsValidHttpEndpointUri(s) ? s : string.Empty;
    }

    private static bool TryNormalizeSchemePrefix(string rawInput, out string normalized)
    {
        normalized = string.Empty;
        string s = rawInput.Trim().TrimEnd('/');
        if (s.Length == 0)
        {
            return false;
        }

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = s;
            return true;
        }

        if (s.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            // Distinguish host:port (or [::1]:port) from non-HTTP schemes like file:, javascript:, data:, ftp:
            if (!s.StartsWith('['))
            {
                int slashIdx = s.IndexOf('/');
                if (slashIdx < 0 || colonIdx < slashIdx)
                {
                    ReadOnlySpan<char> afterColon = slashIdx >= 0
                        ? s.AsSpan(colonIdx + 1, slashIdx - colonIdx - 1)
                        : s.AsSpan(colonIdx + 1);
                    if (afterColon.IsEmpty || !ushort.TryParse(afterColon, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0)
                    {
                        return false;
                    }
                }
            }
        }

        bool isLoopback = s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                          s.StartsWith("127.0.0.1", StringComparison.Ordinal) ||
                          s.StartsWith("[::1]", StringComparison.Ordinal);
        normalized = (isLoopback ? "http://" : "https://") + s;
        return true;
    }

    private static bool IsValidHttpEndpointUri(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        int authorityStart = candidate.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart >= 0)
        {
            ReadOnlySpan<char> rest = candidate.AsSpan(authorityStart + 3);
            int delim = rest.IndexOfAny('/', '?', '#');
            ReadOnlySpan<char> authority = delim >= 0 ? rest[..delim] : rest;
            if (authority.Contains('@'))
            {
                return false;
            }
        }

        return true;
    }

    internal static string DeriveModelsEndpoint(string chatCompletionsOrBaseUrl)
    {
        string normalized = NormalizeChatCompletionsEndpoint(chatCompletionsOrBaseUrl);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[..^"/chat/completions".Length] + "/models";
        }

        return normalized.TrimEnd('/') + "/models";
    }

    internal static bool IsKimiK25Model(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string lower = model.Trim().ToLowerInvariant();
        string leaf = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        return leaf.StartsWith("kimi-k2.5", StringComparison.Ordinal) ||
               leaf.StartsWith("kimi-k2-5", StringComparison.Ordinal);
    }

    internal static bool IsLegacyNonThinkingKimiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || IsKimiK25Model(model))
        {
            return false;
        }

        string lower = model.Trim().ToLowerInvariant();
        string leaf = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        if (leaf.Contains("thinking", StringComparison.Ordinal))
        {
            return false;
        }

        return leaf.StartsWith("moonshot-", StringComparison.Ordinal) ||
               leaf.StartsWith("kimi-k2-", StringComparison.Ordinal) ||
               string.Equals(leaf, "kimi-latest", StringComparison.Ordinal);
    }

    internal static bool IsFixedTemperatureModel(string? model) =>
        IsKimiK25Model(model);

    internal static bool IsReasoningOnlyModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string lower = model.Trim().ToLowerInvariant();
        string leaf = lower.Contains('/') ? lower[(lower.LastIndexOf('/') + 1)..] : lower;
        return leaf.StartsWith("o1", StringComparison.Ordinal) ||
               leaf.StartsWith("o3", StringComparison.Ordinal) ||
               leaf.StartsWith("o4", StringComparison.Ordinal) ||
               (leaf.StartsWith("gpt-5", StringComparison.Ordinal) &&
                !leaf.StartsWith("gpt-5-chat", StringComparison.Ordinal)) ||
               leaf.Contains("reasoner", StringComparison.Ordinal) ||
               leaf.Contains("deepseek-r1", StringComparison.Ordinal) ||
               leaf.StartsWith("qwq", StringComparison.Ordinal) ||
               leaf.Contains("thinking", StringComparison.Ordinal) ||
               leaf.StartsWith("glm-z1", StringComparison.Ordinal);
    }


    internal static string ResolveConfiguredEndpointUrl(
        CustomWidgetAiProviderPreset preset,
        string? region,
        string? customEndpoint)
    {
        string rawCustomEndpoint = customEndpoint?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(rawCustomEndpoint))
        {
            return NormalizeChatCompletionsEndpoint(rawCustomEndpoint);
        }

        if (!string.IsNullOrWhiteSpace(preset.GlobalDefaultEndpoint) &&
            (IsInternationalRegion(region) ||
             (string.IsNullOrWhiteSpace(region) &&
              !IsSimplifiedChineseRegion(LocalizationService.ResolvePersistedOrAmbientCultureName()))))
        {
            return preset.GlobalDefaultEndpoint;
        }

        return preset.DefaultEndpoint;
    }

    internal static bool SupportsClassicDeepSeekModelSwap(
        CustomWidgetAiProviderPreset preset,
        IReadOnlyList<string>? availableModels = null)
    {
        if (preset.ThinkingProtocol != AiThinkingProtocol.DeepSeekModelSwap &&
            preset.ThinkingProtocol != AiThinkingProtocol.AutoDetectByModel)
        {
            return false;
        }

        if (availableModels is not { Count: > 0 })
        {
            return true;
        }

        return availableModels.Contains("deepseek-chat", StringComparer.OrdinalIgnoreCase) &&
               availableModels.Contains("deepseek-reasoner", StringComparer.OrdinalIgnoreCase);
    }


    internal static (string CustomModel, string ThinkingWire) SynchronizeModelAndThinkingSelection(
        CustomWidgetAiProviderPreset preset,
        string? existingCustomModel,
        string? existingThinkingWire,
        string? newChosenModel = null,
        string? newThinkingWire = null,
        IReadOnlyList<string>? availableModels = null)
    {
        string fallbackDefault = availableModels is { Count: > 0 }
            ? availableModels[0]
            : preset.DefaultModel;
        bool isSwapProtocol = SupportsClassicDeepSeekModelSwap(preset, availableModels);
        string chosenModel = newChosenModel is not null
            ? (string.IsNullOrWhiteSpace(newChosenModel) ? fallbackDefault : newChosenModel.Trim())
            : (string.IsNullOrWhiteSpace(existingCustomModel) ? fallbackDefault : existingCustomModel.Trim());
        AiThinkingMode mode = ParseThinkingMode(newThinkingWire ?? existingThinkingWire);

        if (isSwapProtocol)
        {
            static void ApplyModelPriority(ref string model, ref AiThinkingMode thinking)
            {
                if (string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase) &&
                    thinking == AiThinkingMode.Enabled)
                {
                    thinking = AiThinkingMode.Disabled;
                }
                else if (string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
                {
                    thinking = AiThinkingMode.Enabled;
                }
            }

            static void ApplyThinkingPriority(ref string model, AiThinkingMode thinking)
            {
                if (thinking == AiThinkingMode.Enabled &&
                    string.Equals(model, "deepseek-chat", StringComparison.OrdinalIgnoreCase))
                {
                    model = "deepseek-reasoner";
                }
                else if (thinking is AiThinkingMode.Disabled or AiThinkingMode.Auto &&
                         string.Equals(model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
                {
                    model = "deepseek-chat";
                }
            }

            if (newChosenModel is not null && newThinkingWire is null)
            {
                ApplyModelPriority(ref chosenModel, ref mode);
            }
            else if (newThinkingWire is not null && newChosenModel is null)
            {
                ApplyThinkingPriority(ref chosenModel, mode);
            }
            else if (newChosenModel is not null && newThinkingWire is not null)
            {
                string prevModel = string.IsNullOrWhiteSpace(existingCustomModel) ? fallbackDefault : existingCustomModel.Trim();
                AiThinkingMode prevMode = ParseThinkingMode(existingThinkingWire);
                bool modelChanged = !string.Equals(chosenModel, prevModel, StringComparison.OrdinalIgnoreCase);
                bool thinkingChanged = mode != prevMode;

                if (modelChanged && !thinkingChanged)
                {
                    ApplyModelPriority(ref chosenModel, ref mode);
                }
                else if (thinkingChanged && !modelChanged)
                {
                    ApplyThinkingPriority(ref chosenModel, mode);
                }
                else
                {
                    if (string.Equals(chosenModel, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase) &&
                        mode == AiThinkingMode.Auto)
                    {
                        mode = AiThinkingMode.Enabled;
                    }
                    else
                    {
                        ApplyThinkingPriority(ref chosenModel, mode);
                    }
                }
            }
        }

        string normalizedCustomModel = string.IsNullOrWhiteSpace(chosenModel)
            ? string.Empty
            : chosenModel;
        string normalizedThinkingWire = ToThinkingWireValue(mode);
        return (normalizedCustomModel, normalizedThinkingWire);
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

}
