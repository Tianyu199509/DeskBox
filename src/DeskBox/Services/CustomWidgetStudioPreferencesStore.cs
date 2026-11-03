using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed record WebAiTargetPreset(
    string Id,
    string DefaultDisplayName,
    string LaunchUrl,
    bool IsCopyOnly = false,
    string? DisplayNameKey = null)
{
    public string DisplayName =>
        !string.IsNullOrEmpty(DisplayNameKey)
            ? Localized.T(DisplayNameKey)
            : DefaultDisplayName;
}

public sealed class CustomWidgetStudioPreferencesStore
{
    public const string TargetDoubao = "doubao";
    public const string TargetDeepSeek = "deepseek";
    public const string TargetKimi = "kimi";
    public const string TargetChatGpt = "chatgpt";
    public const string TargetClaude = "claude";
    public const string TargetGemini = "gemini";
    public const string TargetCopilot = "copilot";
    public const string TargetCopyOnly = "copy-only";
    public const string ChannelAuto = "";
    public const string ChannelDirect = "direct";
    public const string ChannelAgentMcp = "agent-mcp";
    public const string ChannelWeb = "web";

    public static IReadOnlyList<WebAiTargetPreset> WebAiTargets { get; } =
    [
        new(TargetDeepSeek, "DeepSeek", "https://chat.deepseek.com/"),
        new(TargetDoubao, "Doubao", "https://www.doubao.com/chat/", DisplayNameKey: "CustomWidget.WebAi.Doubao"),
        new(TargetKimi, "Kimi", "https://kimi.moonshot.cn/"),
        new(TargetChatGpt, "ChatGPT", "https://chatgpt.com/"),
        new(TargetClaude, "Claude", "https://claude.ai/new"),
        new(TargetGemini, "Google Gemini", "https://gemini.google.com/app"),
        new(TargetCopilot, "Microsoft Copilot", "https://copilot.microsoft.com/"),
        new(TargetCopyOnly, string.Empty, string.Empty, IsCopyOnly: true)
    ];

    private static readonly string[] MainlandWebTargetOrder =
    [
        TargetDeepSeek, TargetDoubao, TargetKimi, TargetChatGpt, TargetClaude, TargetGemini, TargetCopilot, TargetCopyOnly
    ];

    private static readonly string[] GlobalWebTargetOrder =
    [
        TargetChatGpt, TargetClaude, TargetGemini, TargetCopilot, TargetDeepSeek, TargetKimi, TargetDoubao, TargetCopyOnly
    ];

    public static IReadOnlyList<WebAiTargetPreset> GetOrderedWebAiTargets(
        string? currentLanguage = null,
        bool includeCopyOnly = true)
    {
        bool isMainland = CustomWidgetAiGeneratorService.IsSimplifiedChineseRegion(currentLanguage);
        string[] order = isMainland ? MainlandWebTargetOrder : GlobalWebTargetOrder;
        var map = WebAiTargets.ToDictionary(static t => t.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<WebAiTargetPreset>(WebAiTargets.Count);

        foreach (string id in order)
        {
            if (map.TryGetValue(id, out WebAiTargetPreset? preset) &&
                (includeCopyOnly || !preset.IsCopyOnly))
            {
                result.Add(preset);
            }
        }

        return result;
    }

    public static CustomWidgetStudioPreferencesStore Shared { get; } = new();

    private readonly object _gate = new();
    private readonly string _filePath;
    private bool _loaded;
    private long _lastObservedWriteTicks = -1;

    private bool _hasSeenWelcomeGuide;
    private bool _hasSeenPickElementTip;
    private bool _hasSeenAgentMcpGuide;
    private bool _hasSeenDirectStepTour;
    private bool _hasSeenAgentStepTour;
    private bool _hasSeenWebStepTour;
    private string _preferredWebAiTarget = string.Empty;
    private string _preferredStudioChannel = string.Empty;
    private int _preferredStudioMode;
    private bool _locateOnDesktopAfterCommit = true;
    private string _preferredAiProviderId = string.Empty;
    private bool _hasSeenKeyUpsell;

    public CustomWidgetStudioPreferencesStore(string? customFilePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(customFilePath)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, CustomWidgetDataStore.SubdirectoryName, "studio-prefs.json")
            : customFilePath;
    }

    internal string FilePath => _filePath;

    public bool HasSeenWelcomeGuide
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenWelcomeGuide;
            }
        }
    }

    public bool HasSeenPickElementTip
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenPickElementTip;
            }
        }
    }

    public bool HasSeenAgentMcpGuide
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenAgentMcpGuide;
            }
        }
    }

    public string PreferredStudioChannel
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _preferredStudioChannel;
            }
        }
    }

    public int PreferredStudioMode
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return Math.Clamp(_preferredStudioMode, 0, 2);
            }
        }
    }

    public bool LocateOnDesktopAfterCommit
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _locateOnDesktopAfterCommit;
            }
        }
    }

    public string ResolveEffectiveWebAiTarget(string? currentLanguage)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!string.IsNullOrWhiteSpace(_preferredWebAiTarget) &&
                WebAiTargets.Any(t => string.Equals(t.Id, _preferredWebAiTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return _preferredWebAiTarget;
            }
        }

        bool isMainlandChinese = CustomWidgetAiGeneratorService.IsSimplifiedChineseRegion(currentLanguage);
        return isMainlandChinese ? TargetDeepSeek : TargetChatGpt;
    }

    public static WebAiTargetPreset ResolveWebAiPreset(string targetId)
    {
        return WebAiTargets.FirstOrDefault(t =>
                   string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase))
               ?? WebAiTargets[0];
    }

    public void MarkWelcomeGuideSeen()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_hasSeenWelcomeGuide)
            {
                return;
            }

            _hasSeenWelcomeGuide = true;
            SaveLocked();
        }
    }

    public void MarkPickElementTipSeen()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_hasSeenPickElementTip)
            {
                return;
            }

            _hasSeenPickElementTip = true;
            SaveLocked();
        }
    }

    public void MarkAgentMcpGuideSeen()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_hasSeenAgentMcpGuide)
            {
                return;
            }

            _hasSeenAgentMcpGuide = true;
            SaveLocked();
        }
    }

    public bool HasSeenChannelStepTour(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            EnsureLoadedLocked();
            return normalized switch
            {
                ChannelDirect => _hasSeenDirectStepTour,
                ChannelAgentMcp => _hasSeenAgentStepTour,
                ChannelWeb => _hasSeenWebStepTour,
                _ => false
            };
        }
    }

    public void MarkChannelStepTourSeen(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            EnsureLoadedLocked();
            switch (normalized)
            {
                case ChannelDirect:
                    if (_hasSeenDirectStepTour) return;
                    _hasSeenDirectStepTour = true;
                    break;
                case ChannelAgentMcp:
                    if (_hasSeenAgentStepTour) return;
                    _hasSeenAgentStepTour = true;
                    break;
                case ChannelWeb:
                    if (_hasSeenWebStepTour) return;
                    _hasSeenWebStepTour = true;
                    break;
                default:
                    return;
            }

            SaveLocked();
        }
    }

    public void SetPreferredStudioChannel(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (ChannelAuto or ChannelDirect or ChannelAgentMcp or ChannelWeb))
        {
            normalized = ChannelAuto;
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (string.Equals(_preferredStudioChannel, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _preferredStudioChannel = normalized;
            SaveLocked();
        }
    }

    public void SetPreferredWebAiTarget(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        string normalized = targetId.Trim().ToLowerInvariant();
        if (!WebAiTargets.Any(t => string.Equals(t.Id, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (string.Equals(_preferredWebAiTarget, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _preferredWebAiTarget = normalized;
            SaveLocked();
        }
    }

    public void SetPreferredStudioMode(int modeIndex)
    {
        int clamped = Math.Clamp(modeIndex, 0, 2);
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_preferredStudioMode == clamped)
            {
                return;
            }

            _preferredStudioMode = clamped;
            SaveLocked();
        }
    }

    public void SetLocateOnDesktopAfterCommit(bool locate)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_locateOnDesktopAfterCommit == locate)
            {
                return;
            }

            _locateOnDesktopAfterCommit = locate;
            SaveLocked();
        }
    }

    /// <summary>
    /// Provider the user picked in Settings for one-click generation. Empty
    /// means "first provider that has a key".
    /// </summary>
    public string PreferredAiProviderId
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _preferredAiProviderId;
            }
        }
    }

    public bool HasSeenKeyUpsell
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenKeyUpsell;
            }
        }
    }

    public void SetPreferredAiProviderId(string? providerId)
    {
        string normalized = providerId?.Trim() ?? string.Empty;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (string.Equals(_preferredAiProviderId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _preferredAiProviderId = normalized;
            SaveLocked();
        }
    }

    public void MarkKeyUpsellSeen()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_hasSeenKeyUpsell)
            {
                return;
            }

            _hasSeenKeyUpsell = true;
            SaveLocked();
        }
    }

    private void EnsureLoadedLocked()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _loaded = true;
                _lastObservedWriteTicks = 0;
                return;
            }

            long currentTicks = File.GetLastWriteTimeUtc(_filePath).Ticks;
            if (_loaded && currentTicks == _lastObservedWriteTicks)
            {
                return;
            }

            string json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("hasSeenWelcomeGuide", out JsonElement guideEl) &&
                        guideEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenWelcomeGuide = guideEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenPickElementTip", out JsonElement pickTipEl) &&
                        pickTipEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenPickElementTip = pickTipEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenStudioChannelOnboardingV1", out JsonElement mcpGuideEl) &&
                        mcpGuideEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenAgentMcpGuide = mcpGuideEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenDirectStepTour", out JsonElement directTourEl) &&
                        directTourEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenDirectStepTour = directTourEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenAgentStepTour", out JsonElement agentTourEl) &&
                        agentTourEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenAgentStepTour = agentTourEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenWebStepTour", out JsonElement webTourEl) &&
                        webTourEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenWebStepTour = webTourEl.GetBoolean();
                    }

                    if (root.TryGetProperty("preferredStudioChannel", out JsonElement channelEl) &&
                        channelEl.ValueKind == JsonValueKind.String)
                    {
                        string rawCh = (channelEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                        _preferredStudioChannel = rawCh is ChannelDirect or ChannelAgentMcp or ChannelWeb
                            ? rawCh
                            : ChannelAuto;
                    }

                    if (root.TryGetProperty("preferredWebAiTarget", out JsonElement webEl) &&
                        webEl.ValueKind == JsonValueKind.String)
                    {
                        _preferredWebAiTarget = webEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("preferredStudioMode", out JsonElement modeEl) &&
                        modeEl.ValueKind == JsonValueKind.Number &&
                        modeEl.TryGetInt32(out int modeVal))
                    {
                        _preferredStudioMode = Math.Clamp(modeVal, 0, 2);
                    }

                    if (root.TryGetProperty("locateOnDesktopAfterCommit", out JsonElement locateEl) &&
                        locateEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _locateOnDesktopAfterCommit = locateEl.GetBoolean();
                    }

                    if (root.TryGetProperty("preferredAiProviderId", out JsonElement providerEl) &&
                        providerEl.ValueKind == JsonValueKind.String)
                    {
                        _preferredAiProviderId = providerEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("hasSeenKeyUpsell", out JsonElement upsellEl) &&
                        upsellEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenKeyUpsell = upsellEl.GetBoolean();
                    }
                }
            }

            _loaded = true;
            _lastObservedWriteTicks = currentTicks;
        }
        catch
        {
            _loaded = true;
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("hasSeenWelcomeGuide", _hasSeenWelcomeGuide);
                writer.WriteBoolean("hasSeenPickElementTip", _hasSeenPickElementTip);
                writer.WriteBoolean("hasSeenStudioChannelOnboardingV1", _hasSeenAgentMcpGuide);
                writer.WriteBoolean("hasSeenDirectStepTour", _hasSeenDirectStepTour);
                writer.WriteBoolean("hasSeenAgentStepTour", _hasSeenAgentStepTour);
                writer.WriteBoolean("hasSeenWebStepTour", _hasSeenWebStepTour);
                writer.WriteString("preferredWebAiTarget", _preferredWebAiTarget);
                writer.WriteString("preferredStudioChannel", _preferredStudioChannel);
                writer.WriteNumber("preferredStudioMode", _preferredStudioMode);
                writer.WriteBoolean("locateOnDesktopAfterCommit", _locateOnDesktopAfterCommit);
                writer.WriteString("preferredAiProviderId", _preferredAiProviderId);
                writer.WriteBoolean("hasSeenKeyUpsell", _hasSeenKeyUpsell);
                writer.WriteEndObject();
            }

            string content = Encoding.UTF8.GetString(stream.ToArray());
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_filePath, content);
            if (File.Exists(_filePath))
            {
                _lastObservedWriteTicks = File.GetLastWriteTimeUtc(_filePath).Ticks;
            }
        }
        catch
        {
            // Ignore non-fatal preference persistence errors.
        }
    }
}
