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

        // Command execution is never granted implicitly — not by Official-origin default
        // trust, not by Studio authoring. First run prompts the user; the explicit
        // override above then carries the remembered decision.
        if (kind == CustomWidgetPermissionKind.CommandExecution)
        {
            return false;
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
        public bool TrustedLocalAuthoring { get; set; }
        public Dictionary<string, bool> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ExecutableSurfaceHash { get; set; }
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
            _transientLoadFailed = false;
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
            case "notifications":
                kind = CustomWidgetPermissionKind.Notifications;
                return true;
            case "publicnetwork":
                kind = CustomWidgetPermissionKind.PublicNetwork;
                return true;
            case "localnetwork":
                kind = CustomWidgetPermissionKind.LocalNetwork;
                return true;
            case "systemandclipboard":
                kind = CustomWidgetPermissionKind.SystemAndClipboard;
                return true;
            case "localfileaccess":
                kind = CustomWidgetPermissionKind.LocalFileAccess;
                return true;
            case "commandexecution":
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

    public CustomWidgetTrustProfile GetProfile(
        string? widgetId,
        string? manifestOriginFallback = null,
        string? manifestTemplateId = null)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return new CustomWidgetTrustProfile(
                string.Empty,
                TrustedLocalAuthoring: false,
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

            string? trimmedOrigin = manifestOriginFallback?.Trim();
            string candidateTemplateId = !string.IsNullOrWhiteSpace(manifestTemplateId)
                ? manifestTemplateId.Trim()
                : key;
            bool defaultTrusted =
                string.Equals(trimmedOrigin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase) &&
                CustomWidgetTemplates.IsKnownBuiltInTemplate(candidateTemplateId);

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

    /// <summary>
    /// Fingerprints everything that can execute or reach out: the newline-normalized
    /// script plus the manifest's privileged declarative surface (capabilities, script,
    /// data sources, drop handlers, privileged actions, and clipboard/secret bindings).
    /// Only the hex digest is persisted, never content.
    /// </summary>
    public static string ComputeExecutableSurfaceHash(string? manifestJson, string? logicJs)
    {
        string privilegedNodes = ExtractPrivilegedManifestNodes(manifestJson);
        byte[] logicBytes = Encoding.UTF8.GetBytes(
            (logicJs ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        byte[] nodeBytes = Encoding.UTF8.GetBytes(privilegedNodes);
        byte[] combined = new byte[logicBytes.Length + 1 + nodeBytes.Length];
        logicBytes.AsSpan().CopyTo(combined);
        combined[logicBytes.Length] = 0;
        nodeBytes.AsSpan().CopyTo(combined[(logicBytes.Length + 1)..]);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(combined));
    }

    internal static bool HasPrivilegedSurfaceInManifestOrScript(
        CustomWidgetManifest? manifest,
        string? manifestJson,
        string? logicJs)
    {
        if (!string.IsNullOrWhiteSpace(logicJs) || manifest?.Script is not null)
        {
            return true;
        }

        if (manifest is not null)
        {
            foreach (string cap in manifest.Capabilities)
            {
                string? packId = CustomWidgetCapabilityRegistry.ResolveDomainPackId(cap);
                if (string.Equals(packId, "local-files-and-cli", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(packId, "network-and-feeds", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(packId, "script-sandbox", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cap?.Trim(), "clipboard-workspace", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cap?.Trim(), "system-control", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (CustomWidgetSourceSpec src in manifest.Sources)
            {
                if (IsPrivilegedSourceType(src.Type))
                {
                    return true;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                manifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            return ContainsPrivilegedAstElement(doc.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivilegedSourceType(string? type) =>
        !string.IsNullOrWhiteSpace(type) &&
        (string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "fileText", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "localFolder", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "rss", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "clipboard", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(type, "aiMonitor", StringComparison.OrdinalIgnoreCase));

    private static bool IsPrivilegedActionOrNodeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        string t = type.Trim();
        return string.Equals(t, "command.run", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "script.run", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "http.request", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "ai.run", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "system.control", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "system.openSetting", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t, "url.open", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("launcher.", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("file.", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("clipboard.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReferencesSensitiveRuntimeNamespace(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("$clipboard.", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("$secrets.", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsPrivilegedAstElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ReferencesSensitiveRuntimeNamespace(element.GetString());

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsPrivilegedAstElement(item))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String)
                {
                    string? typeStr = typeEl.GetString();
                    if (IsPrivilegedActionOrNodeType(typeStr) || IsPrivilegedSourceType(typeStr))
                    {
                        return true;
                    }
                }

                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (ContainsPrivilegedAstElement(prop.Value))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static string ExtractPrivilegedManifestNodes(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                manifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var fragments = new List<string>();
            if (root.TryGetProperty("capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement cap in caps.EnumerateArray())
                {
                    fragments.Add("cap:" + SerializeCanonicalJsonElement(cap));
                }
            }

            if (root.TryGetProperty("script", out JsonElement scriptEl) &&
                scriptEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                fragments.Add("script:" + SerializeCanonicalJsonElement(scriptEl));
            }

            if (root.TryGetProperty("sources", out JsonElement sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement src in sourcesEl.EnumerateArray())
                {
                    fragments.Add("source:" + SerializeCanonicalJsonElement(src));
                }
            }

            if (root.TryGetProperty("onDrop", out JsonElement dropEl) &&
                dropEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                fragments.Add("onDrop:" + SerializeCanonicalJsonElement(dropEl));
            }

            CollectPrivilegedElements(root, fragments);
            fragments.Sort(StringComparer.Ordinal);
            return string.Join("\n", fragments);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CollectPrivilegedElements(JsonElement element, List<string> fragments)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string? strVal = element.GetString();
                if (ReferencesSensitiveRuntimeNamespace(strVal))
                {
                    fragments.Add("expr:" + strVal);
                }

                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    IsPrivilegedActionOrNodeType(typeEl.GetString()))
                {
                    fragments.Add("node:" + SerializeCanonicalJsonElement(element));
                }

                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    CollectPrivilegedElements(prop.Value, fragments);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectPrivilegedElements(item, fragments);
                }

                break;
        }
    }

    private static string SerializeCanonicalJsonElement(JsonElement element) =>
        DeskBox.Services.CustomWidgets.CustomWidgetCatalogSignatureVerifier.SerializeCanonicalJsonElement(element, relaxedEscaping: true);

    private static readonly CustomWidgetPermissionKind[] RevocablePrivilegedPermissions =
    [
        CustomWidgetPermissionKind.PublicNetwork,
        CustomWidgetPermissionKind.LocalNetwork,
        CustomWidgetPermissionKind.SystemAndClipboard,
        CustomWidgetPermissionKind.LocalFileAccess,
        CustomWidgetPermissionKind.CommandExecution
    ];

    /// <summary>
    /// Records the executable surface after a workspace write. Host Studio writes preserve
    /// existing user-granted overrides; signed Official OTA preserves official baseline trust
    /// while revoking explicit overrides if the surface changed; external Agent/import writes
    /// downgrade local-authoring provenance and revoke positive privileged overrides whenever
    /// the surface changed.
    /// </summary>
    public void RecordExecutableSurfaceAfterWrite(
        string? widgetId,
        string? manifestJson,
        string? logicJs,
        bool trustedLocalAuthoring,
        bool preserveTrustedAuthoringOnExternalWrite = false,
        bool preserveExistingTrustedStateOnHostWrite = false)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        string key = widgetId.Trim();
        string hash = ComputeExecutableSurfaceHash(manifestJson, logicJs);
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (!_cache!.TryGetValue(key, out StoredTrustEntry? entry))
            {
                _cache[key] = new StoredTrustEntry
                {
                    TrustedLocalAuthoring = preserveTrustedAuthoringOnExternalWrite ||
                        (trustedLocalAuthoring && !preserveExistingTrustedStateOnHostWrite),
                    ExecutableSurfaceHash = hash
                };
                PersistNoLock();
                return;
            }

            bool surfaceChanged =
                string.IsNullOrEmpty(entry.ExecutableSurfaceHash) ||
                !string.Equals(entry.ExecutableSurfaceHash, hash, StringComparison.Ordinal);
            if (!trustedLocalAuthoring)
            {
                if (!preserveTrustedAuthoringOnExternalWrite)
                {
                    entry.TrustedLocalAuthoring = false;
                }

                if (surfaceChanged)
                {
                    foreach (CustomWidgetPermissionKind kind in RevocablePrivilegedPermissions)
                    {
                        string privilegedKey = ToKey(kind);
                        if (entry.Overrides.TryGetValue(privilegedKey, out bool enabled) && enabled)
                        {
                            entry.Overrides.Remove(privilegedKey);
                        }
                    }
                }
            }

            entry.ExecutableSurfaceHash = hash;
            PersistNoLock();
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
                    TrustedLocalAuthoring = defaultTrustedIfNew ?? false
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
                    TrustedLocalAuthoring = defaultTrustedIfNew ?? false
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

        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
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
                     string.Equals(t, "url.open", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "ai.run", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
                if (string.Equals(t, "http.request", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Url) &&
                    CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
                        act.Url,
                        allowLocalAndHttp: true,
                        out _,
                        out bool needsLocal,
                        out _) &&
                    needsLocal)
                {
                    set.Add(CustomWidgetPermissionKind.LocalNetwork);
                }
            }
            else if (string.Equals(t, "file.writeText", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "file.transform", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "file.listDir", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "file.manage", StringComparison.OrdinalIgnoreCase) ||
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
                     string.Equals(t, "clipboard.captureImage", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t, "launcher.open", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.SystemAndClipboard);
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

        void InspectMenuItemsExpr(IReadOnlyList<CustomWidgetMenuItemSpec>? items)
        {
            if (items is null)
            {
                return;
            }

            foreach (CustomWidgetMenuItemSpec item in items)
            {
                InspectExprForClipboard(item.Label);
                InspectExprForClipboard(item.When);
                InspectExprForClipboard(item.Value);
                InspectMenuItemsExpr(item.Children);
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (string.Equals(node.Type, "Image", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(node.Src) &&
                node.Src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
            }

            InspectExprForClipboard(node.Text);
            InspectExprForClipboard(node.Title);
            InspectExprForClipboard(node.Subtitle);
            InspectExprForClipboard(node.Value);
            InspectExprForClipboard(node.When);
            InspectExprForClipboard(node.EmptyState?.Title);
            InspectExprForClipboard(node.EmptyState?.Subtitle);
            InspectMenuItemsExpr(node.MenuItems);
            InspectMenuItemsExpr(node.ContextMenu);
            foreach (CustomWidgetTextSpanSpec span in node.Spans)
            {
                InspectExprForClipboard(span.Text);
                if (!string.IsNullOrWhiteSpace(span.Href) &&
                    (span.Href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     span.Href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                {
                    set.Add(CustomWidgetPermissionKind.PublicNetwork);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            if (logicJs.Contains("http.request", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("ai.run", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.PublicNetwork);
            }
            if (logicJs.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                set.Add(CustomWidgetPermissionKind.LocalNetwork);
            }
            if (logicJs.Contains("file.writeText", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("file.transform", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("file.listDir", StringComparison.OrdinalIgnoreCase) ||
                logicJs.Contains("file.manage", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.LocalFileAccess);
            }
            if (logicJs.Contains("clipboard.captureImage", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(CustomWidgetPermissionKind.SystemAndClipboard);
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

        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
        {
            if (string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(act.Method) &&
                !string.Equals(act.Method.Trim(), "GET", StringComparison.OrdinalIgnoreCase))
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
            ReadOnlyMemory<byte> jsonMemory = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? bytes.AsMemory(Encoding.UTF8.Preamble.Length)
                : bytes;
            using JsonDocument doc = JsonDocument.Parse(jsonMemory);
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

                    bool trusted = prop.Value.TryGetProperty("trustedLocalAuthoring", out JsonElement tEl) &&
                                   tEl.ValueKind == JsonValueKind.True;
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

                    string? surfaceHash = prop.Value.TryGetProperty("executableSurfaceHash", out JsonElement shEl) &&
                                          shEl.ValueKind == JsonValueKind.String
                        ? shEl.GetString()
                        : null;

                    loaded[prop.Name] = new StoredTrustEntry
                    {
                        TrustedLocalAuthoring = trusted,
                        Overrides = overrides,
                        ExecutableSurfaceHash = surfaceHash
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
                    if (!string.IsNullOrWhiteSpace(entry.ExecutableSurfaceHash))
                    {
                        writer.WriteString("executableSurfaceHash", entry.ExecutableSurfaceHash);
                    }
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
