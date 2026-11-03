using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskBox.FileSafety;

/// <summary>外部 Agent 一次暂存写入的完整内容。</summary>
internal sealed record AgentStageRequest(
    string? WidgetId,
    string ManifestJson,
    string? LogicJs,
    bool HasLogicJs,
    string? LocalesJson,
    bool HasLocales,
    bool ResizeToDefault);

/// <summary>暂存区中读回的完整内容（供工坊预览与提交使用）。</summary>
internal sealed record AgentStageBundle(
    string StageId,
    string? WidgetId,
    string ManifestJson,
    string? LogicJs,
    bool HasLogicJs,
    string? LocalesJson,
    bool HasLocales,
    bool ResizeToDefault,
    DateTimeOffset CreatedUtc);

/// <summary>待确认暂存的摘要信息。</summary>
internal sealed record AgentStageSummary(
    string StageId,
    string? WidgetId,
    DateTimeOffset CreatedUtc);

/// <summary>
/// P2：外部 Agent 写入的默认落点是独立暂存区，不触碰格子工作区与桌面配置。
/// 只有用户确认（GUI「放到桌面」或带 stageId 的显式提交）后，
/// DeskBoxAgentBridgeServer 才会走原来的落盘路径。
///
/// 每个批次一个子目录：meta.txt（元数据，纯文本）、manifest.json、
/// 可选 logic.js / locales.json。目录位于 LocalApplicationData\DeskBox\AgentStage，
/// 处于 widgetsRoot 之外，因此不会触发工作区文件监听器的热重载。
/// 本类型归入 DeskBox.FileSafety：文件删除/清理属于 file-safety 域。
/// </summary>
internal static class CustomWidgetAgentStageStore
{
    private static readonly object Gate = new();
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>暂存区内容变化（新增 / 提交 / 忽略 / 过期清理），供工坊提示条实时刷新。</summary>
    internal static event Action? Changed;

    private static void NotifyChanged() => Changed?.Invoke();

    /// <summary>仅供测试隔离文件系统，生产环境保持空。</summary>
    internal static string RootOverride { get; set; } = string.Empty;

    internal static string StageRoot => string.IsNullOrWhiteSpace(RootOverride)
        ? Path.Combine(
            DeskBox.Services.DeskBoxDataPathService.Current.RootPath,
            "AgentStage")
        : RootOverride;

    internal static string ComputeManifestHash(string manifestJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson ?? string.Empty)))
            .ToLowerInvariant();

    internal static string ComputeCanonicalJsonHash(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return ComputeManifestHash(string.Empty);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                WriteCanonicalJsonElement(writer, doc.RootElement);
            }

            return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }
        catch
        {
            return ComputeManifestHash(rawJson);
        }
    }

    private static void WriteCanonicalJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty prop in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonicalJsonElement(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonicalJsonElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string T(string? cultureName, string key) =>
        DeskBox.Services.LocalizationService.TranslateForCulture(
            cultureName ?? DeskBox.Services.LocalizationService.ResolveAmbientCultureName(),
            key);

    private static string F(string? cultureName, string key, object arg0) =>
        DeskBox.Services.LocalizationService.FormatForCulture(
            cultureName ?? DeskBox.Services.LocalizationService.ResolveAmbientCultureName(),
            key,
            arg0);

    private static bool IsValidStageId(string? stageId) =>
        !string.IsNullOrWhiteSpace(stageId) &&
        stageId.Length == 32 &&
        stageId.All(static ch => char.IsAsciiHexDigit(ch));

    private static string DirectoryOf(string stageId) => Path.Combine(StageRoot, stageId);

    /// <summary>
    /// 已提交批次的幂等记录目录。名字不是 32 位十六进制，
    /// 因此 ListPending / CleanupStale 会把子目录当成非批次内容跳过或按过期清理。
    /// </summary>
    private static string CommittedRoot => Path.Combine(StageRoot, "_committed");

    private static string CommittedPath(string stageId) => Path.Combine(CommittedRoot, stageId + ".txt");

    private static string MetaPath(string stageId) => Path.Combine(DirectoryOf(stageId), "meta.txt");

    /// <summary>登记一份待确认的写入（含完整内容），返回 stageId。</summary>
    internal static string Stage(AgentStageRequest request)
    {
        string stageId = Guid.NewGuid().ToString("N");
        string dir = DirectoryOf(stageId);
        Directory.CreateDirectory(dir);

        string normalizedLogic = request.HasLogicJs && !string.IsNullOrWhiteSpace(request.LogicJs)
            ? request.LogicJs
            : string.Empty;
        string logicHash = request.HasLogicJs ? ComputeManifestHash(normalizedLogic) : string.Empty;
        string localesHash = request.HasLocales ? ComputeCanonicalJsonHash(request.LocalesJson) : string.Empty;

        // 纯文本元数据：避免把暂存记录计入生产 JSON 序列化基线契约。
        string[] meta =
        [
            "version=3",
            "stageId=" + stageId,
            "widgetId=" + (request.WidgetId ?? string.Empty),
            "manifestSha256=" + ComputeManifestHash(request.ManifestJson),
            "logicSha256=" + logicHash,
            "localesSha256=" + localesHash,
            "createdUtc=" + DateTimeOffset.UtcNow.ToString("O"),
            "hasLogicJs=" + (request.HasLogicJs ? "1" : "0"),
            "hasLocales=" + (request.HasLocales ? "1" : "0"),
            "resizeToDefault=" + (request.ResizeToDefault ? "1" : "0")
        ];

        lock (Gate)
        {
            File.WriteAllLines(MetaPath(stageId), meta, Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), request.ManifestJson, Encoding.UTF8);
            if (request.HasLogicJs && request.LogicJs is not null)
            {
                File.WriteAllText(Path.Combine(dir, "logic.js"), request.LogicJs, Encoding.UTF8);
            }
            if (request.HasLocales && request.LocalesJson is not null)
            {
                File.WriteAllText(Path.Combine(dir, "locales.json"), request.LocalesJson, Encoding.UTF8);
            }
            CleanupStaleLocked();
        }

        NotifyChanged();
        return stageId;
    }

    private static Dictionary<string, string> ReadMeta(string stageId)
    {
        return File.ReadAllLines(MetaPath(stageId), Encoding.UTF8)
            .Where(static line => line.Contains('='))
            .Select(static line => line.Split('=', 2))
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);
    }

    /// <summary>列出所有未过期、未提交的暂存批次。</summary>
    internal static IReadOnlyList<AgentStageSummary> ListPending()
    {
        var result = new List<AgentStageSummary>();
        if (!Directory.Exists(StageRoot))
        {
            return result;
        }

        lock (Gate)
        {
            foreach (string dir in Directory.EnumerateDirectories(StageRoot))
            {
                string stageId = Path.GetFileName(dir);
                if (!IsValidStageId(stageId) || !File.Exists(MetaPath(stageId)))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, string> meta = ReadMeta(stageId);
                    DateTimeOffset created = meta.TryGetValue("createdUtc", out string? rawCreated) &&
                                             DateTimeOffset.TryParse(rawCreated, out DateTimeOffset parsed)
                        ? parsed
                        : DateTimeOffset.MinValue;
                    if (DateTimeOffset.UtcNow - created > MaxAge)
                    {
                        continue;
                    }

                    string? widgetId = meta.TryGetValue("widgetId", out string? rawId) &&
                                       !string.IsNullOrEmpty(rawId)
                        ? rawId
                        : null;
                    result.Add(new AgentStageSummary(stageId, widgetId, created));
                }
                catch
                {
                    // 损坏的批次跳过，不影响其它批次。
                }
            }
        }

        return result.OrderByDescending(static x => x.CreatedUtc).ToArray();
    }

    /// <summary>
    /// 按 stageId、widgetId 或 manifest.id 查找最新一份待确认暂存包（不消费批次），
    /// 供 `deskbox_capture_widget_preview` 在用户确认上桌面之前先截取暂存画面做视觉自检。
    /// </summary>
    internal static bool TryFindLatestPendingBundle(string? widgetIdOrStageId, out AgentStageBundle? bundle)
    {
        bundle = null;
        string trimmed = widgetIdOrStageId?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (IsValidStageId(trimmed) && TryReadBundle(trimmed, out bundle, out _))
        {
            return bundle is not null;
        }

        foreach (AgentStageSummary summary in ListPending())
        {
            if (!TryReadBundle(summary.StageId, out AgentStageBundle? candidate, out _) || candidate is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidate.WidgetId) &&
                string.Equals(candidate.WidgetId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                bundle = candidate;
                return true;
            }

            string? manifestId = TryExtractManifestId(candidate.ManifestJson);
            if (!string.IsNullOrWhiteSpace(manifestId) &&
                string.Equals(manifestId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                bundle = candidate;
                return true;
            }
        }

        return false;
    }

    private static string? TryExtractManifestId(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifestJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("id", out JsonElement idElem) &&
                idElem.ValueKind == JsonValueKind.String)
            {
                return idElem.GetString();
            }
        }
        catch
        {
            // Ignore malformed JSON during lookup.
        }

        return null;
    }

    /// <summary>读取完整暂存内容，供工坊预览与提交使用，不消费批次。</summary>
    internal static bool TryReadBundle(
        string? stageId,
        out AgentStageBundle? bundle,
        out string error,
        string? cultureName = null)
    {
        bundle = null;
        error = string.Empty;
        if (!IsValidStageId(stageId))
        {
            error = T(cultureName, "CustomWidget.AgentStage.Error.InvalidId");
            return false;
        }

        string dir = DirectoryOf(stageId!);
        string manifestPath = Path.Combine(dir, "manifest.json");
        if (!Directory.Exists(dir) || !File.Exists(manifestPath))
        {
            error = T(cultureName, "CustomWidget.AgentStage.Error.MissingOrConsumed");
            return false;
        }

        lock (Gate)
        {
            try
            {
                Dictionary<string, string> meta = ReadMeta(stageId!);
                DateTimeOffset created = meta.TryGetValue("createdUtc", out string? rawCreated) &&
                                         DateTimeOffset.TryParse(rawCreated, out DateTimeOffset parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                if (DateTimeOffset.UtcNow - created > MaxAge)
                {
                    error = T(cultureName, "CustomWidget.AgentStage.Error.Expired");
                    return false;
                }

                string manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                string? widgetId = meta.TryGetValue("widgetId", out string? rawId) &&
                                   !string.IsNullOrEmpty(rawId)
                    ? rawId
                    : null;
                bool hasLogicJs = meta.TryGetValue("hasLogicJs", out string? rawLogic) && rawLogic == "1";
                bool hasLocales = meta.TryGetValue("hasLocales", out string? rawLocales) && rawLocales == "1";
                bool resize = meta.TryGetValue("resizeToDefault", out string? rawResize) && rawResize == "1";

                string? logicJs = hasLogicJs && File.Exists(Path.Combine(dir, "logic.js"))
                    ? File.ReadAllText(Path.Combine(dir, "logic.js"), Encoding.UTF8)
                    : null;
                string? localesJson = hasLocales && File.Exists(Path.Combine(dir, "locales.json"))
                    ? File.ReadAllText(Path.Combine(dir, "locales.json"), Encoding.UTF8)
                    : null;

                bundle = new AgentStageBundle(
                    stageId!, widgetId, manifestJson, logicJs, hasLogicJs,
                    localesJson, hasLocales, resize, created);
                return true;
            }
            catch (Exception ex)
            {
                error = F(cultureName, "CustomWidget.AgentStage.Error.ReadFailed", ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 校验并消费一个暂存批次。成功时返回目标 widgetId 并删除整个批次目录，
    /// 保证同一批次只能提交一次。
    /// </summary>
    internal static bool TryConsume(
        string? stageId,
        string manifestJson,
        out string? widgetId,
        out string error,
        string? cultureName = null)
    {
        bool ok = TryConsumeBundle(
            stageId,
            manifestJson,
            hasCallerLogicJs: false,
            callerLogicJs: null,
            hasCallerLocales: false,
            callerLocalesJson: null,
            out AgentStageBundle? bundle,
            out error,
            cultureName);
        widgetId = bundle?.WidgetId;
        return ok;
    }

    /// <summary>
    /// 校验并消费完整暂存包（含 manifestJson、可选 logicJs 与 locales 一致性校验），
    /// 返回完整已暂存内容供落盘回填。
    /// </summary>
    internal static bool TryConsumeBundle(
        string? stageId,
        string manifestJson,
        bool hasCallerLogicJs,
        string? callerLogicJs,
        bool hasCallerLocales,
        string? callerLocalesJson,
        out AgentStageBundle? bundle,
        out string error,
        string? cultureName = null)
    {
        bundle = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(stageId))
        {
            error = T(cultureName, "CustomWidget.AgentStage.Error.MissingStageIdForCommit");
            return false;
        }

        if (!IsValidStageId(stageId))
        {
            error = T(cultureName, "CustomWidget.AgentStage.Error.InvalidId");
            return false;
        }

        string dir = DirectoryOf(stageId);
        lock (Gate)
        {
            if (!File.Exists(MetaPath(stageId)))
            {
                error = T(cultureName, "CustomWidget.AgentStage.Error.MissingOrConsumed");
                return false;
            }

            try
            {
                Dictionary<string, string> meta = ReadMeta(stageId);
                string hash = meta.TryGetValue("manifestSha256", out string? storedHash) ? storedHash : string.Empty;
                string? widgetId = meta.TryGetValue("widgetId", out string? storedWidgetId) &&
                                   !string.IsNullOrEmpty(storedWidgetId)
                    ? storedWidgetId
                    : null;
                DateTimeOffset created = meta.TryGetValue("createdUtc", out string? storedCreated) &&
                                         DateTimeOffset.TryParse(storedCreated, out DateTimeOffset parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

                if (DateTimeOffset.UtcNow - created > MaxAge)
                {
                    Directory.Delete(dir, recursive: true);
                    NotifyChanged();
                    error = T(cultureName, "CustomWidget.AgentStage.Error.Expired");
                    return false;
                }

                string incomingManifestHash = ComputeManifestHash(manifestJson);
                if (!string.Equals(hash, incomingManifestHash, StringComparison.Ordinal))
                {
                    // 不删除：允许用正确内容重新提交同一批次。
                    error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                    return false;
                }

                bool stagedHasLogicJs = meta.TryGetValue("hasLogicJs", out string? rawLogic) && rawLogic == "1";
                bool stagedHasLocales = meta.TryGetValue("hasLocales", out string? rawLocales) && rawLocales == "1";
                bool stagedResize = meta.TryGetValue("resizeToDefault", out string? rawResize) && rawResize == "1";

                string logicPath = Path.Combine(dir, "logic.js");
                string? stagedLogicJs = stagedHasLogicJs && File.Exists(logicPath)
                    ? File.ReadAllText(logicPath, Encoding.UTF8)
                    : null;
                string localesPath = Path.Combine(dir, "locales.json");
                string? stagedLocalesJson = stagedHasLocales && File.Exists(localesPath)
                    ? File.ReadAllText(localesPath, Encoding.UTF8)
                    : null;

                if (hasCallerLogicJs)
                {
                    string expectedLogicHash = stagedHasLogicJs
                        ? (meta.TryGetValue("logicSha256", out string? slh) && !string.IsNullOrEmpty(slh)
                            ? slh
                            : ComputeManifestHash(stagedLogicJs ?? string.Empty))
                        : ComputeManifestHash(string.Empty);
                    string actualLogicHash = ComputeManifestHash(
                        string.IsNullOrWhiteSpace(callerLogicJs) ? string.Empty : callerLogicJs);
                    if (!string.Equals(expectedLogicHash, actualLogicHash, StringComparison.Ordinal))
                    {
                        error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                        return false;
                    }
                }

                if (hasCallerLocales)
                {
                    string expectedLocalesHash = stagedHasLocales
                        ? (meta.TryGetValue("localesSha256", out string? slch) && !string.IsNullOrEmpty(slch)
                            ? slch
                            : ComputeCanonicalJsonHash(stagedLocalesJson))
                        : ComputeCanonicalJsonHash(null);
                    string actualLocalesHash = ComputeCanonicalJsonHash(callerLocalesJson);
                    if (!string.Equals(expectedLocalesHash, actualLocalesHash, StringComparison.Ordinal))
                    {
                        error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                        return false;
                    }
                }

                string manifestPath = Path.Combine(dir, "manifest.json");
                string stagedManifestJson = File.Exists(manifestPath)
                    ? File.ReadAllText(manifestPath, Encoding.UTF8)
                    : manifestJson;

                bundle = new AgentStageBundle(
                    stageId,
                    widgetId,
                    stagedManifestJson,
                    stagedLogicJs,
                    stagedHasLogicJs,
                    stagedLocalesJson,
                    stagedHasLocales,
                    stagedResize,
                    created);

                Directory.Delete(dir, recursive: true);
                // 消费后留一条幂等记录：客户端重试或 Agent 重放同一批次时，
                // 应拿到「已提交」的成功结果，而不是「stageId 不存在」。
                RecordCommittedLocked(stageId, widgetId, incomingManifestHash);
                NotifyChanged();
                return true;
            }
            catch (Exception ex)
            {
                error = F(cultureName, "CustomWidget.AgentStage.Error.ReadFailed", ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 用户主动忽略一份外部暂存：只丢弃批次，桌面与格子工作区都不变。
    /// 与 TryConsume 分开，避免「拒绝」被误记成「已提交」。
    /// </summary>
    internal static bool TryDiscard(string? stageId, out string error, string? cultureName = null)
    {
        error = string.Empty;
        if (!IsValidStageId(stageId))
        {
            error = T(cultureName, "CustomWidget.AgentStage.Error.InvalidId");
            return false;
        }

        string dir = DirectoryOf(stageId!);
        lock (Gate)
        {
            if (!File.Exists(MetaPath(stageId!)))
            {
                error = T(cultureName, "CustomWidget.AgentStage.Error.MissingOrProcessed");
                return false;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                NotifyChanged();
                return true;
            }
            catch (Exception ex)
            {
                error = F(cultureName, "CustomWidget.AgentStage.Error.DeleteFailed", ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 记录一次成功的提交，供重复提交时返回幂等结果。
    /// 提交成功后会再用真实落盘 id 覆写一次（暂存时 widgetId 可能为空，由 manifest.id 决定）。
    /// </summary>
    internal static void RecordCommitted(string? stageId, string? widgetId)
    {
        if (!IsValidStageId(stageId))
        {
            return;
        }

        lock (Gate)
        {
            RecordCommittedLocked(stageId!, widgetId, manifestSha256: null);
        }
    }

    private static void RecordCommittedLocked(string stageId, string? widgetId, string? manifestSha256)
    {
        try
        {
            Directory.CreateDirectory(CommittedRoot);
            string path = CommittedPath(stageId);
            string? preservedHash = manifestSha256;
            if (string.IsNullOrEmpty(preservedHash) && File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("manifestSha256=", StringComparison.Ordinal))
                    {
                        preservedHash = line["manifestSha256=".Length..].Trim();
                        break;
                    }
                }
            }

            var lines = new List<string>(2)
            {
                "widgetId=" + (widgetId ?? string.Empty)
            };
            if (!string.IsNullOrEmpty(preservedHash))
            {
                lines.Add("manifestSha256=" + preservedHash);
            }

            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch
        {
            // 幂等记录是尽力而为：写不进去不影响本次提交本身。
        }
    }

    /// <summary>
    /// 查询某个 stageId 是否已经提交成功过。stageId 由服务端生成、绑定内容摘要且一次性，
    /// 本身就是幂等键——客户端重试不该被当成「stageId 不存在」。
    /// </summary>
    internal static bool TryGetCommitted(string? stageId, out string? widgetId, string? expectedManifestJson = null)
    {
        widgetId = null;
        if (!IsValidStageId(stageId))
        {
            return false;
        }

        string path = CommittedPath(stageId!);
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                string? storedManifestHash = null;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("widgetId=", StringComparison.Ordinal))
                    {
                        string value = line["widgetId=".Length..].Trim();
                        widgetId = value.Length == 0 ? null : value;
                    }
                    else if (line.StartsWith("manifestSha256=", StringComparison.Ordinal))
                    {
                        string value = line["manifestSha256=".Length..].Trim();
                        storedManifestHash = value.Length == 0 ? null : value;
                    }
                }

                if (expectedManifestJson is not null &&
                    !string.IsNullOrEmpty(storedManifestHash) &&
                    !string.Equals(storedManifestHash, ComputeManifestHash(expectedManifestJson), StringComparison.Ordinal))
                {
                    widgetId = null;
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>删除归属于指定自定义组件的所有待确认暂存批次；返回删除数量。</summary>
    internal static int DeleteStagesForWidget(string? widgetId)
    {
        string trimmed = widgetId?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || !Directory.Exists(StageRoot))
        {
            return 0;
        }

        int removed = 0;
        lock (Gate)
        {
            foreach (string dir in Directory.EnumerateDirectories(StageRoot).ToArray())
            {
                string stageId = Path.GetFileName(dir);
                if (!IsValidStageId(stageId))
                {
                    continue;
                }

                bool matches = false;
                try
                {
                    if (File.Exists(MetaPath(stageId)))
                    {
                        Dictionary<string, string> meta = ReadMeta(stageId);
                        if (meta.TryGetValue("widgetId", out string? rawId) &&
                            string.Equals(rawId, trimmed, StringComparison.OrdinalIgnoreCase))
                        {
                            matches = true;
                        }
                    }

                    if (!matches)
                    {
                        string manifestPath = Path.Combine(dir, "manifest.json");
                        if (File.Exists(manifestPath))
                        {
                            string? manifestId = TryExtractManifestId(File.ReadAllText(manifestPath, Encoding.UTF8));
                            if (string.Equals(manifestId, trimmed, StringComparison.OrdinalIgnoreCase))
                            {
                                matches = true;
                            }
                        }
                    }

                    if (matches)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
                catch
                {
                    // 尽力清理，不阻断组件删除流程。
                }
            }
        }

        if (removed > 0)
        {
            NotifyChanged();
        }

        return removed;
    }

    /// <summary>清理过期暂存目录；返回删除数量。</summary>
    internal static int CleanupStale()
    {
        int removed;
        lock (Gate)
        {
            removed = CleanupStaleLocked();
        }

        if (removed > 0)
        {
            NotifyChanged();
        }

        return removed;
    }

    private static int CleanupStaleLocked()
    {
        if (!Directory.Exists(StageRoot))
        {
            return 0;
        }

        int removed = 0;
        foreach (string dir in Directory.EnumerateDirectories(StageRoot))
        {
            try
            {
                if (DateTimeOffset.UtcNow - Directory.GetLastWriteTimeUtc(dir) > MaxAge)
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
            catch
            {
                // 暂存清理是尽力而为，不影响主流程。
            }
        }

        return removed;
    }
}
