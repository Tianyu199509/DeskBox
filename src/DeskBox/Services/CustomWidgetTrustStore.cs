using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

public enum CustomWidgetPermissionKind
{
    Notifications = 0,
    PublicNetwork = 1,
    LocalNetwork = 2,
    SystemAndClipboard = 3,
    LocalFileAccess = 4,
    CommandExecution = 5
}

public enum CustomWidgetPermissionRiskLevel
{
    Low = 0,
    Medium = 1,
    Elevated = 2,
    High = 3,
    Critical = 4
}

public sealed record CustomWidgetPermissionDescriptor(
    CustomWidgetPermissionKind Kind,
    string Key,
    string Glyph,
    CustomWidgetPermissionRiskLevel RiskLevel,
    string Title,
    string RiskBadgeText,
    string RiskDescription);

public sealed record CustomWidgetTrustProfile(
    string WidgetId,
    bool TrustedLocalAuthoring,
    IReadOnlyDictionary<CustomWidgetPermissionKind, bool> ExplicitOverrides)
{
    public bool IsPermissionGranted(CustomWidgetPermissionKind kind, bool isReadOnlyPublicGet = false)
    {
#if DESKBOX_STORE
        if (kind == CustomWidgetPermissionKind.CommandExecution)
        {
            return false;
        }
#endif
        if (ExplicitOverrides.TryGetValue(kind, out bool explicitDecision))
        {
            return explicitDecision;
        }

        if (TrustedLocalAuthoring)
        {
            return true;
        }

        return kind switch
        {
            CustomWidgetPermissionKind.Notifications => true,
            CustomWidgetPermissionKind.PublicNetwork => isReadOnlyPublicGet,
            _ => false
        };
    }
}

/// <summary>
/// AOT-safe host-side trust provenance and per-widget permission store
/// persisted in <c>%LOCALAPPDATA%\DeskBox\data\widget-permissions.json</c>.
/// Never trusts <c>manifest.json</c>'s self-reported <c>origin</c> field alone when importing external bundles.
/// </summary>
public sealed class CustomWidgetTrustStore
{
    private static readonly Lazy<CustomWidgetTrustStore> SharedInstance =
        new(() => new CustomWidgetTrustStore());

    private readonly object _sync = new();
    private readonly string _filePath;
    private Dictionary<string, StoredTrustEntry>? _cache;
    private long _lastLoadedWriteUtcTicks = -1;
    private long _lastLoadedLength = -1;

    private sealed class StoredTrustEntry
    {
        public bool TrustedLocalAuthoring { get; set; } = true;
        public Dictionary<string, bool> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public CustomWidgetTrustStore(string? rootDataDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "widget-permissions.json");
    }

    public static CustomWidgetTrustStore Shared => SharedInstance.Value;

    public string FilePath => _filePath;

    public void InvalidateCache()
    {
        lock (_sync)
        {
            _cache = null;
            _lastLoadedWriteUtcTicks = -1;
            _lastLoadedLength = -1;
        }
    }

    public static IReadOnlyList<CustomWidgetPermissionKind> GetSupportedPermissions()
    {
#if DESKBOX_STORE
        return
        [
            CustomWidgetPermissionKind.Notifications,
            CustomWidgetPermissionKind.PublicNetwork,
            CustomWidgetPermissionKind.LocalNetwork,
            CustomWidgetPermissionKind.SystemAndClipboard,
            CustomWidgetPermissionKind.LocalFileAccess
        ];
#else
        return
        [
            CustomWidgetPermissionKind.Notifications,
            CustomWidgetPermissionKind.PublicNetwork,
            CustomWidgetPermissionKind.LocalNetwork,
            CustomWidgetPermissionKind.SystemAndClipboard,
            CustomWidgetPermissionKind.LocalFileAccess,
            CustomWidgetPermissionKind.CommandExecution
        ];
#endif
    }

    public static string ToKey(CustomWidgetPermissionKind kind) => kind switch
    {
        CustomWidgetPermissionKind.Notifications => "notifications",
        CustomWidgetPermissionKind.PublicNetwork => "publicNetwork",
        CustomWidgetPermissionKind.LocalNetwork => "localNetwork",
        CustomWidgetPermissionKind.SystemAndClipboard => "systemAndClipboard",
        CustomWidgetPermissionKind.LocalFileAccess => "localFileAccess",
        CustomWidgetPermissionKind.CommandExecution => "commandExecution",
        _ => "notifications"
    };

    public static bool TryParseKey(string? rawKey, out CustomWidgetPermissionKind kind)
    {
        kind = CustomWidgetPermissionKind.Notifications;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        switch (rawKey.Trim().ToLowerInvariant())
        {
            case "notifications" or "notification" or "toast":
                kind = CustomWidgetPermissionKind.Notifications;
                return true;
            case "publicnetwork" or "network" or "internet":
                kind = CustomWidgetPermissionKind.PublicNetwork;
                return true;
            case "localnetwork" or "lan" or "localhost":
                kind = CustomWidgetPermissionKind.LocalNetwork;
                return true;
            case "systemandclipboard" or "clipboard" or "system":
                kind = CustomWidgetPermissionKind.SystemAndClipboard;
                return true;
            case "localfileaccess" or "file" or "files" or "localfile":
                kind = CustomWidgetPermissionKind.LocalFileAccess;
                return true;
            case "commandexecution" or "command" or "shell":
                kind = CustomWidgetPermissionKind.CommandExecution;
                return true;
            default:
                return false;
        }
    }

    private static string ResolvePermString(string culture, string key, string fallback)
    {
        string value = LocalizationService.TranslateForCulture(culture, key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    public static CustomWidgetPermissionDescriptor GetDescriptor(
        CustomWidgetPermissionKind kind,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? Localized.ActiveCultureName
            : LocalizationService.NormalizeCultureCode(cultureName);
        bool isZh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        return kind switch
        {
            CustomWidgetPermissionKind.Notifications => new(
                kind,
                ToKey(kind),
                "\uEA8F",
                CustomWidgetPermissionRiskLevel.Low,
                ResolvePermString(culture, "CustomWidget.Perm.Notifications.Title", isZh ? "桌面通知与提醒" : "Notifications & Reminders"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.Low", isZh ? "低风险" : "Low Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.Notifications.Desc",
                    isZh
                        ? "允许此格子弹出系统通知气泡与倒计时提醒声音；关闭后将保持静默免打扰。"
                        : "Allows desktop toast notifications and reminder alerts. Turn off to mute this widget.")),

            CustomWidgetPermissionKind.PublicNetwork => new(
                kind,
                ToKey(kind),
                "\uE774",
                CustomWidgetPermissionRiskLevel.Medium,
                ResolvePermString(culture, "CustomWidget.Perm.PublicNetwork.Title", isZh ? "互联网网络访问" : "Internet Network Access"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.Medium", isZh ? "中风险" : "Medium Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.PublicNetwork.Desc",
                    isZh
                        ? "允许连接外部互联网读取数据或提交 API/Webhook 请求。风险提示：若配置了密钥，请求将向目标服务器发送鉴权凭据。"
                        : "Allows connecting to external internet endpoints (GET/POST/PUT). Caution: configured secrets may be sent to target hosts.")),

            CustomWidgetPermissionKind.LocalNetwork => new(
                kind,
                ToKey(kind),
                "\uE968",
                CustomWidgetPermissionRiskLevel.Elevated,
                ResolvePermString(culture, "CustomWidget.Perm.LocalNetwork.Title", isZh ? "局域网与本机服务" : "Local Network & Localhost"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.Elevated", isZh ? "中高风险" : "Elevated Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.LocalNetwork.Desc",
                    isZh
                        ? "允许连接 localhost、127.0.0.1、明文 HTTP 及家庭/办公内网设备（如 NAS、本地模型）。风险提示：请勿对来源不明的外部格子开启，以防探测内网端口。"
                        : "Allows connecting to localhost, plaintext HTTP, and private LAN devices. Caution: untrusted widgets could probe internal services.")),

            CustomWidgetPermissionKind.SystemAndClipboard => new(
                kind,
                ToKey(kind),
                "\uE77F",
                CustomWidgetPermissionRiskLevel.Medium,
                ResolvePermString(culture, "CustomWidget.Perm.SystemAndClipboard.Title", isZh ? "剪贴板与系统控制" : "Clipboard & System Control"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.Medium", isZh ? "中风险" : "Medium Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.SystemAndClipboard.Desc",
                    isZh
                        ? "允许读取剪贴板文本、调节系统音量/亮度/媒体播放或唤起外部应用协议。风险提示：剪贴板可能包含复制的敏感文本。"
                        : "Allows reading clipboard text, controlling volume/brightness/media, or opening app URIs. Caution: clipboard may hold sensitive text.")),

            CustomWidgetPermissionKind.LocalFileAccess => new(
                kind,
                ToKey(kind),
                "\uE8A5",
                CustomWidgetPermissionRiskLevel.High,
                ResolvePermString(culture, "CustomWidget.Perm.LocalFileAccess.Title", isZh ? "本地文件读写" : "Local File Read & Write"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.High", isZh ? "高风险" : "High Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.LocalFileAccess.Desc",
                    isZh
                        ? "允许读取、监听或写入指定的本地文本/JSON/Markdown 文件。风险提示：开启后该格子可修改或覆盖目标文件内容。"
                        : "Allows reading, watching, or writing local files. High risk: enabled widgets can overwrite target file contents.")),

            CustomWidgetPermissionKind.CommandExecution => new(
                kind,
                ToKey(kind),
                "\uE756",
                CustomWidgetPermissionRiskLevel.Critical,
                ResolvePermString(culture, "CustomWidget.Perm.CommandExecution.Title", isZh ? "本地命令与脚本执行" : "Local Command Execution"),
                ResolvePermString(culture, "CustomWidget.Perm.Risk.Critical", isZh ? "高危权限" : "Critical Risk"),
                ResolvePermString(
                    culture,
                    "CustomWidget.Perm.CommandExecution.Desc",
                    isZh
                        ? "允许在后台运行本机终端程序（如 git、python、pwsh）。高危警告：具备与当前 Windows 账户完全一致的系统操作权限，严禁对不明来源的外部格子开启！"
                        : "Allows running local executables/scripts (git, python, pwsh). Critical warning: runs with your full Windows user privileges—never enable for untrusted widgets!")),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    public CustomWidgetTrustProfile GetProfile(string? widgetId, string? manifestOriginFallback = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return new CustomWidgetTrustProfile(
                string.Empty,
                TrustedLocalAuthoring: true,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }

        string key = widgetId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.TryGetValue(key, out StoredTrustEntry? entry))
            {
                return ToProfile(key, entry);
            }

            // Legacy widgets created before widget-permissions.json existed:
            // Community origin defaults to untrusted; local/official/ai/agent default to trusted.
            bool defaultTrusted = !string.Equals(
                manifestOriginFallback?.Trim(),
                CustomWidgetOriginKinds.Community,
                StringComparison.OrdinalIgnoreCase);

            return new CustomWidgetTrustProfile(
                key,
                defaultTrusted,
                new Dictionary<CustomWidgetPermissionKind, bool>());
        }
    }

    public void RegisterWidgetProvenance(string? widgetId, bool trustedLocalAuthoring)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        string key = widgetId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (!_cache!.TryGetValue(key, out StoredTrustEntry? entry))
            {
                entry = new StoredTrustEntry
                {
                    TrustedLocalAuthoring = trustedLocalAuthoring
                };
                _cache[key] = entry;
                PersistNoLock();
            }
            else if (entry.TrustedLocalAuthoring != trustedLocalAuthoring)
            {
                entry.TrustedLocalAuthoring = trustedLocalAuthoring;
                PersistNoLock();
            }
        }
    }

    public void SetPermissionOverride(string? widgetId, CustomWidgetPermissionKind kind, bool enabled, bool? defaultTrustedIfNew = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        string key = widgetId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (!_cache!.TryGetValue(key, out StoredTrustEntry? entry))
            {
                entry = new StoredTrustEntry
                {
                    TrustedLocalAuthoring = defaultTrustedIfNew ?? true
                };
                _cache[key] = entry;
            }

            entry.Overrides[ToKey(kind)] = enabled;
            PersistNoLock();
        }
    }

    public void ApplyPermissionMutations(
        string? widgetId,
        IReadOnlyDictionary<string, bool>? mutations,
        bool? defaultTrustedIfNew = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId) || mutations is not { Count: > 0 })
        {
            return;
        }

        string key = widgetId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (!_cache!.TryGetValue(key, out StoredTrustEntry? entry))
            {
                entry = new StoredTrustEntry
                {
                    TrustedLocalAuthoring = defaultTrustedIfNew ?? true
                };
                _cache[key] = entry;
            }

            bool changed = false;
            foreach ((string rawPermKey, bool enabled) in mutations)
            {
                if (TryParseKey(rawPermKey, out CustomWidgetPermissionKind parsedKind))
                {
                    string canonical = ToKey(parsedKind);
                    if (!entry.Overrides.TryGetValue(canonical, out bool existing) || existing != enabled)
                    {
                        entry.Overrides[canonical] = enabled;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                PersistNoLock();
            }
        }
    }

    public void RemoveWidget(string? widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.Remove(widgetId.Trim()))
            {
                PersistNoLock();
            }
        }
    }

    public static IReadOnlySet<CustomWidgetPermissionKind> DetectRequestedPermissions(
        CustomWidgetManifest? manifest,
        string? logicJs = null)
    {
        var set = new HashSet<CustomWidgetPermissionKind>();
        if (manifest is null)
        {
            return set;
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src.Type, "weather", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
                if (!string.IsNullOrWhiteSpace(src.Url) &&
                    CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
                        src.Url,
                        allowLocalAndHttp: true,
                        out _,
                        out bool needsLocal,
                        out _) &&
                    needsLocal)
                {
                    set.Add(CustomWidgetPermissionKind.LocalNetwork);
                }
            }
            else if (string.Equals(src.Type, "fileText", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.LocalFileAccess);
            }
            else if (string.Equals(src.Type, "command", StringComparison.OrdinalIgnoreCase))
            {
#if !DESKBOX_STORE
                set.Add(CustomWidgetPermissionKind.CommandExecution);
#endif
            }
            else if (string.Equals(src.Type, "clipboard", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(src.Type, "deviceState", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.SystemAndClipboard);
            }
        }

        void InspectActionList(IReadOnlyList<CustomWidgetActionSpec>? actions)
        {
            if (actions is null)
            {
                return;
            }

            foreach (CustomWidgetActionSpec act in actions)
            {
                string t = act.Type;
                if (string.Equals(t, "toast.show", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "reminder.notify", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("pomodoro.", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t, "timer.toggle", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(CustomWidgetPermissionKind.Notifications);
                }
                else if (string.Equals(t, "http.request", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "url.open", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(CustomWidgetPermissionKind.PublicNetwork);
                    if (string.Equals(t, "http.request", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(act.Url) &&
                        (act.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         act.Url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                         act.Url.Contains("127.0.0.1", StringComparison.Ordinal) ||
                         act.Url.Contains("192.168.", StringComparison.Ordinal) ||
                         act.Url.Contains("10.", StringComparison.Ordinal)))
                    {
                        set.Add(CustomWidgetPermissionKind.LocalNetwork);
                    }
                }
                else if (string.Equals(t, "file.writeText", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "file.transform", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "file.open", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "file.reveal", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(CustomWidgetPermissionKind.LocalFileAccess);
                }
                else if (string.Equals(t, "command.run", StringComparison.OrdinalIgnoreCase))
                {
#if !DESKBOX_STORE
                    set.Add(CustomWidgetPermissionKind.CommandExecution);
#endif
                }
                else if (string.Equals(t, "system.control", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "system.opensetting", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t, "launcher.open", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(CustomWidgetPermissionKind.SystemAndClipboard);
                }

                InspectActionList(act.Then);
            }
        }

        void InspectExprForClipboard(string? expr)
        {
            if (!string.IsNullOrWhiteSpace(expr) &&
                expr.Contains("$clipboard", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.SystemAndClipboard);
            }
        }

        void InspectNodeTree(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return;
            }

            if (string.Equals(node.Type, "Image", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(node.Src) &&
                node.Src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
            }

            InspectExprForClipboard(node.Text);
            InspectExprForClipboard(node.Value);
            InspectActionList(node.OnClick);
            InspectActionList(node.OnSubmit);
            InspectActionList(node.OnChange);
            InspectActionList(node.OnDropActions);
            InspectActionList(node.OnRowClick);
            InspectActionList(node.OnItemClick);
            InspectActionList(node.OnReorder);
            InspectActionList(node.OnInlineCommit);
            InspectNodeTree(node.ItemTemplate);
            foreach (CustomWidgetNodeSpec h in node.HoverActions)
            {
                InspectNodeTree(h);
            }
            foreach (CustomWidgetNodeSpec c in node.Children)
            {
                InspectNodeTree(c);
            }
        }

        InspectNodeTree(manifest.View);
        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            InspectNodeTree(pop);
        }
        InspectActionList(manifest.OnDrop?.Actions);
        InspectActionList(manifest.Capsule?.PrimaryAction?.OnClick);

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            if (logicJs.Contains("http.request", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
            }
            if (logicJs.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                set.Add(CustomWidgetPermissionKind.LocalNetwork);
            }
            if (logicJs.Contains("file.writeText", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.LocalFileAccess);
            }
#if !DESKBOX_STORE
            if (logicJs.Contains("command.run", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.CommandExecution);
            }
#endif
            if (logicJs.Contains("toast.show", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("reminder.notify", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.Notifications);
            }
        }

        return set;
    }

    public static bool ManifestUsesMutatingPublicNetwork(
        CustomWidgetManifest? manifest,
        string? logicJs = null)
    {
        if (manifest is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(logicJs) &&
            logicJs.Contains("http.request", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static bool HasMutatingHttpAction(IReadOnlyList<CustomWidgetActionSpec>? actions)
        {
            if (actions is null)
            {
                return false;
            }

            foreach (CustomWidgetActionSpec act in actions)
            {
                if (string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Method) &&
                    !string.Equals(act.Method.Trim(), "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (HasMutatingHttpAction(act.Then))
                {
                    return true;
                }
            }

            return false;
        }

        static bool NodeHasMutatingHttp(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return false;
            }

            if (HasMutatingHttpAction(node.OnClick) ||
                HasMutatingHttpAction(node.OnSubmit) ||
                HasMutatingHttpAction(node.OnChange) ||
                HasMutatingHttpAction(node.OnDropActions) ||
                HasMutatingHttpAction(node.OnRowClick) ||
                HasMutatingHttpAction(node.OnItemClick) ||
                HasMutatingHttpAction(node.OnReorder) ||
                HasMutatingHttpAction(node.OnInlineCommit) ||
                NodeHasMutatingHttp(node.ItemTemplate))
            {
                return true;
            }

            foreach (CustomWidgetNodeSpec h in node.HoverActions)
            {
                if (NodeHasMutatingHttp(h)) return true;
            }
            foreach (CustomWidgetNodeSpec c in node.Children)
            {
                if (NodeHasMutatingHttp(c)) return true;
            }

            return false;
        }

        if (NodeHasMutatingHttp(manifest.View) ||
            HasMutatingHttpAction(manifest.OnDrop?.Actions) ||
            HasMutatingHttpAction(manifest.Capsule?.PrimaryAction?.OnClick))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            if (NodeHasMutatingHttp(pop))
            {
                return true;
            }
        }

        return false;
    }

    private static CustomWidgetTrustProfile ToProfile(string widgetId, StoredTrustEntry entry)
    {
        var map = new Dictionary<CustomWidgetPermissionKind, bool>();
        foreach ((string k, bool v) in entry.Overrides)
        {
            if (TryParseKey(k, out CustomWidgetPermissionKind kind))
            {
                map[kind] = v;
            }
        }

        return new CustomWidgetTrustProfile(widgetId, entry.TrustedLocalAuthoring, map);
    }

    private bool _transientLoadFailed;

    private void EnsureLoadedNoLock()
    {
        if (!File.Exists(_filePath))
        {
            _transientLoadFailed = false;
            if (_cache is null || _lastLoadedWriteUtcTicks != 0)
            {
                _cache = new Dictionary<string, StoredTrustEntry>(StringComparer.Ordinal);
                _lastLoadedWriteUtcTicks = 0;
                _lastLoadedLength = 0;
            }
            return;
        }

        var info = new FileInfo(_filePath);
        if (_cache is not null &&
            !_transientLoadFailed &&
            info.LastWriteTimeUtc.Ticks == _lastLoadedWriteUtcTicks &&
            info.Length == _lastLoadedLength)
        {
            return;
        }

        byte[]? bytes = TryReadSharedFileBytesWithRetry(_filePath);
        if (bytes is null)
        {
            _transientLoadFailed = true;
            _cache ??= new Dictionary<string, StoredTrustEntry>(StringComparer.Ordinal);
            return;
        }

        _transientLoadFailed = false;
        var loaded = new Dictionary<string, StoredTrustEntry>(StringComparer.Ordinal);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("widgets", out JsonElement widgetsEl) &&
                widgetsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in widgetsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    bool trusted = !prop.Value.TryGetProperty("trustedLocalAuthoring", out JsonElement tEl) ||
                                   tEl.ValueKind != JsonValueKind.False;
                    var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    if (prop.Value.TryGetProperty("overrides", out JsonElement ovEl) &&
                        ovEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty ov in ovEl.EnumerateObject())
                        {
                            if (ov.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            {
                                overrides[ov.Name] = ov.Value.GetBoolean();
                            }
                        }
                    }

                    loaded[prop.Name] = new StoredTrustEntry
                    {
                        TrustedLocalAuthoring = trusted,
                        Overrides = overrides
                    };
                }
            }
        }
        catch
        {
            // Corrupted JSON file falls back safely without crashing host.
        }

        _cache = loaded;
        _lastLoadedWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
        _lastLoadedLength = info.Length;
    }

    private static byte[]? TryReadSharedFileBytesWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                int len = checked((int)fs.Length);
                byte[] buffer = new byte[len];
                int offset = 0;
                while (offset < len)
                {
                    int read = fs.Read(buffer, offset, len - offset);
                    if (read <= 0)
                    {
                        break;
                    }
                    offset += read;
                }
                return offset == len ? buffer : buffer[..offset];
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(12);
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    private void PersistNoLock()
    {
        if (_cache is null || _transientLoadFailed)
        {
            return;
        }

        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(
                       fs,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WritePropertyName("widgets");
                writer.WriteStartObject();
                foreach ((string widgetId, StoredTrustEntry entry) in _cache)
                {
                    writer.WritePropertyName(widgetId);
                    writer.WriteStartObject();
                    writer.WriteBoolean("trustedLocalAuthoring", entry.TrustedLocalAuthoring);
                    writer.WritePropertyName("overrides");
                    writer.WriteStartObject();
                    foreach ((string permKey, bool val) in entry.Overrides)
                    {
                        writer.WriteBoolean(permKey, val);
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            DeskBox.Platform.CustomWidgetStorageFileOps.ReplaceJsonFileAtomically(tempPath, _filePath);
            var info = new FileInfo(_filePath);
            _lastLoadedWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            _lastLoadedLength = info.Length;
        }
        catch
        {
            // Ignore transient disk write failures.
        }
        finally
        {
            DeskBox.Platform.CustomWidgetStorageFileOps.DeleteFileIfExists(tempPath);
        }
    }
}
