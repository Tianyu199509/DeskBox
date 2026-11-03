using System.Security.Cryptography;
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
    public const int DefaultAgentHttpPort = 47821;

    public static IReadOnlyList<WebAiTargetPreset> WebAiTargets { get; } =
    [
        new(TargetDoubao, "Doubao", "https://www.doubao.com/chat/", DisplayNameKey: "CustomWidget.WebAi.Doubao"),
        new(TargetDeepSeek, "DeepSeek", "https://chat.deepseek.com/"),
        new(TargetKimi, "Kimi", "https://kimi.moonshot.cn/"),
        new(TargetChatGpt, "ChatGPT", "https://chatgpt.com/"),
        new(TargetClaude, "Claude", "https://claude.ai/new"),
        new(TargetGemini, "Google Gemini", "https://gemini.google.com/app"),
        new(TargetCopilot, "Microsoft Copilot", "https://copilot.microsoft.com/"),
        new(TargetCopyOnly, string.Empty, string.Empty, IsCopyOnly: true)
    ];

    private static readonly string[] MainlandWebTargetOrder =
    [
        TargetDoubao, TargetDeepSeek, TargetKimi, TargetChatGpt, TargetClaude, TargetGemini, TargetCopilot, TargetCopyOnly
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
    private string _preferredWebAiTarget = string.Empty;
    private int _preferredStudioMode;
    private bool _locateOnDesktopAfterCommit = true;
    private string _preferredAiProviderId = string.Empty;
    private bool _hasSeenKeyUpsell;
    private bool _agentHttpEnabled;
    private int _agentHttpPort = DefaultAgentHttpPort;
    private string _agentHttpTokenProtected = string.Empty;
    private string _agentHttpTokenFallbackPlain = string.Empty;
    private string _sessionUnpersistedAgentHttpToken = string.Empty;
    private readonly List<string> _customAgentConfigPaths = [];

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
        return isMainlandChinese ? TargetDoubao : TargetChatGpt;
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

    public bool AgentHttpEnabled
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _agentHttpEnabled;
            }
        }
    }

    public int AgentHttpPort
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return NormalizeAgentHttpPort(_agentHttpPort);
            }
        }
    }

    public IReadOnlyList<string> CustomAgentConfigPaths
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _customAgentConfigPaths.ToArray();
            }
        }
    }

    public void SetAgentHttpEnabled(bool enabled)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (enabled)
            {
                EnsureAgentHttpTokenLocked();
            }

            if (_agentHttpEnabled == enabled)
            {
                return;
            }

            _agentHttpEnabled = enabled;
            SaveLocked();
        }
    }

    public void SetAgentHttpPort(int port)
    {
        int clamped = NormalizeAgentHttpPort(port);
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_agentHttpPort == clamped)
            {
                return;
            }

            _agentHttpPort = clamped;
            SaveLocked();
        }
    }

    public string GetOrCreateAgentHttpToken()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            return EnsureAgentHttpTokenLocked();
        }
    }

    public string RegenerateAgentHttpToken()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            string fresh = GenerateFreshBearerToken();
            StoreAgentHttpTokenLocked(fresh);
            SaveLocked();
            return fresh;
        }
    }

    public void AddCustomAgentConfigPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath.Trim());
        }
        catch
        {
            fullPath = filePath.Trim();
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            _customAgentConfigPaths.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
            _customAgentConfigPaths.Insert(0, fullPath);
            if (_customAgentConfigPaths.Count > 12)
            {
                _customAgentConfigPaths.RemoveRange(12, _customAgentConfigPaths.Count - 12);
            }

            SaveLocked();
        }
    }

    public void RemoveCustomAgentConfigPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_customAgentConfigPaths.RemoveAll(p => string.Equals(p, filePath.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveLocked();
            }
        }
    }

    public static int NormalizeAgentHttpPort(int port) =>
        port is >= 1024 and <= 65535 ? port : DefaultAgentHttpPort;

    private string EnsureAgentHttpTokenLocked()
    {
        if (!string.IsNullOrWhiteSpace(_agentHttpTokenProtected))
        {
            string unprotected = DpapiSecretProtector.UnprotectBase64ToUtf8(_agentHttpTokenProtected);
            if (!string.IsNullOrWhiteSpace(unprotected))
            {
                return unprotected;
            }
        }

        if (!string.IsNullOrWhiteSpace(_sessionUnpersistedAgentHttpToken))
        {
            return _sessionUnpersistedAgentHttpToken;
        }

        if (!string.IsNullOrWhiteSpace(_agentHttpTokenFallbackPlain))
        {
            string fallback = _agentHttpTokenFallbackPlain;
            StoreAgentHttpTokenLocked(fallback);
            SaveLocked();
            return fallback;
        }

        string fresh = GenerateFreshBearerToken();
        StoreAgentHttpTokenLocked(fresh);
        SaveLocked();
        return fresh;
    }

    private void StoreAgentHttpTokenLocked(string token)
    {
        string protectedBase64 = DpapiSecretProtector.ProtectUtf8ToBase64(token);
        if (!string.IsNullOrWhiteSpace(protectedBase64))
        {
            _agentHttpTokenProtected = protectedBase64;
            _sessionUnpersistedAgentHttpToken = string.Empty;
        }
        else
        {
            _agentHttpTokenProtected = string.Empty;
            _sessionUnpersistedAgentHttpToken = token;
        }

        _agentHttpTokenFallbackPlain = string.Empty;
    }

    private static string GenerateFreshBearerToken()
    {
        Span<byte> randomBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(randomBytes);
        return "dbx_" + Convert.ToHexString(randomBytes).ToLowerInvariant();
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

                    if (root.TryGetProperty("agentHttpEnabled", out JsonElement httpEnEl) &&
                        httpEnEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _agentHttpEnabled = httpEnEl.GetBoolean();
                    }

                    if (root.TryGetProperty("agentHttpPort", out JsonElement httpPortEl) &&
                        httpPortEl.ValueKind == JsonValueKind.Number &&
                        httpPortEl.TryGetInt32(out int portVal))
                    {
                        _agentHttpPort = NormalizeAgentHttpPort(portVal);
                    }

                    if (root.TryGetProperty("agentHttpTokenProtected", out JsonElement tokProtEl) &&
                        tokProtEl.ValueKind == JsonValueKind.String)
                    {
                        _agentHttpTokenProtected = tokProtEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("agentHttpTokenFallback", out JsonElement tokPlainEl) &&
                        tokPlainEl.ValueKind == JsonValueKind.String)
                    {
                        _agentHttpTokenFallbackPlain = tokPlainEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("customAgentConfigPaths", out JsonElement pathsEl) &&
                        pathsEl.ValueKind == JsonValueKind.Array)
                    {
                        _customAgentConfigPaths.Clear();
                        foreach (JsonElement item in pathsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String &&
                                item.GetString() is { Length: > 0 } p)
                            {
                                _customAgentConfigPaths.Add(p);
                            }
                        }
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
                writer.WriteString("preferredWebAiTarget", _preferredWebAiTarget);
                writer.WriteNumber("preferredStudioMode", _preferredStudioMode);
                writer.WriteBoolean("locateOnDesktopAfterCommit", _locateOnDesktopAfterCommit);
                writer.WriteString("preferredAiProviderId", _preferredAiProviderId);
                writer.WriteBoolean("hasSeenKeyUpsell", _hasSeenKeyUpsell);
                writer.WriteBoolean("agentHttpEnabled", _agentHttpEnabled);
                writer.WriteNumber("agentHttpPort", NormalizeAgentHttpPort(_agentHttpPort));
                if (!string.IsNullOrWhiteSpace(_agentHttpTokenProtected))
                {
                    writer.WriteString("agentHttpTokenProtected", _agentHttpTokenProtected);
                }
                if (_customAgentConfigPaths.Count > 0)
                {
                    writer.WriteStartArray("customAgentConfigPaths");
                    foreach (string path in _customAgentConfigPaths)
                    {
                        writer.WriteStringValue(path);
                    }
                    writer.WriteEndArray();
                }
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
