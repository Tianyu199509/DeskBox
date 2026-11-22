using System.Globalization;
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
    bool ResizeToDefault,
    string? RawCallerManifestJson = null);

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
    private static readonly Dictionary<string, PendingStageCommit> s_pendingCommits = new(StringComparer.Ordinal);

    private sealed record PendingStageCommit(
        string Dir,
        string? WidgetId,
        string ManifestSha256,
        string ManifestCanonicalSha256,
        string? RawCallerManifestCanonicalSha256,
        bool HasLogicJs,
        string LogicSha256,
        bool HasLocales,
        string LocalesSha256);

    internal static bool HasPendingCommitLease(string stageId)
    {
        lock (Gate)
        {
            return s_pendingCommits.ContainsKey(stageId);
        }
    }

    /// <summary>
    /// 落盘成功后消费租约：删除批次目录并写幂等墓碑。崩溃时租约随进程消失，
    /// 批次目录保留，客户端可重试。
    /// </summary>
    internal static void CompleteCommit(string stageId, string? committedWidgetId = null)
    {
        lock (Gate)
        {
            if (!s_pendingCommits.TryGetValue(stageId, out PendingStageCommit? pending))
            {
                return;
            }

            s_pendingCommits.Remove(stageId);
            try
            {
                if (Directory.Exists(pending.Dir))
                {
                    Directory.Delete(pending.Dir, recursive: true);
                }
            }
            catch
            {
                // Tombstone below still makes retries idempotent; sweep handles the leftovers.
            }

            RecordCommittedLocked(
                stageId,
                !string.IsNullOrWhiteSpace(committedWidgetId) ? committedWidgetId : pending.WidgetId,
                pending.ManifestSha256,
                pending.ManifestCanonicalSha256,
                pending.RawCallerManifestCanonicalSha256,
                pending.HasLogicJs,
                pending.LogicSha256,
                pending.HasLocales,
                pending.LocalesSha256);
            NotifyChanged();
        }
    }

    /// <summary>落盘失败时释放租约，保留暂存包供重试。</summary>
    internal static void AbortCommit(string stageId)
    {
        lock (Gate)
        {
            s_pendingCommits.Remove(stageId);
        }
    }

    /// <summary>暂存区内容变化（新增 / 提交 / 忽略 / 过期清理），供工坊提示条实时刷新。</summary>
    internal static event Action? Changed;

    private static void NotifyChanged() => Changed?.Invoke();

    internal static void NotifyExternalChanged() => Changed?.Invoke();

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

    internal static string ComputeCanonicalJsonHash(string? rawJson) =>
        DeskBox.Services.CustomWidgets.CustomWidgetCatalogSignatureVerifier.ComputeCanonicalJsonHash(rawJson);

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
        string tempDir = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(StageRoot);
        Directory.CreateDirectory(tempDir);

        string normalizedLogic = request.HasLogicJs && !string.IsNullOrWhiteSpace(request.LogicJs)
            ? request.LogicJs
            : string.Empty;
        string logicHash = request.HasLogicJs ? ComputeManifestHash(normalizedLogic) : string.Empty;
        string localesHash = request.HasLocales ? ComputeCanonicalJsonHash(request.LocalesJson) : string.Empty;
        string rawCallerCanonicalHash = !string.IsNullOrWhiteSpace(request.RawCallerManifestJson)
            ? ComputeCanonicalJsonHash(request.RawCallerManifestJson)
            : string.Empty;

        // 纯文本元数据：避免把暂存记录计入生产 JSON 序列化基线契约。
        string[] meta =
        [
            "version=4",
            "stageId=" + stageId,
            "widgetId=" + (request.WidgetId ?? string.Empty),
            "manifestSha256=" + ComputeManifestHash(request.ManifestJson),
            "manifestCanonicalSha256=" + ComputeCanonicalJsonHash(request.ManifestJson),
            "rawCallerManifestCanonicalSha256=" + rawCallerCanonicalHash,
            "logicSha256=" + logicHash,
            "localesSha256=" + localesHash,
            "createdUtc=" + DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "hasLogicJs=" + (request.HasLogicJs ? "1" : "0"),
            "hasLocales=" + (request.HasLocales ? "1" : "0"),
            "resizeToDefault=" + (request.ResizeToDefault ? "1" : "0")
        ];

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "manifest.json"), request.ManifestJson, Encoding.UTF8);
            if (request.HasLogicJs && request.LogicJs is not null)
            {
                File.WriteAllText(Path.Combine(tempDir, "logic.js"), request.LogicJs, Encoding.UTF8);
            }
            if (request.HasLocales && request.LocalesJson is not null)
            {
                File.WriteAllText(Path.Combine(tempDir, "locales.json"), request.LocalesJson, Encoding.UTF8);
            }
            File.WriteAllLines(Path.Combine(tempDir, "meta.txt"), meta, Encoding.UTF8);

            lock (Gate)
            {
                Directory.Move(tempDir, dir);
                CleanupStaleLocked();
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }

            throw;
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
                                             DateTimeOffset.TryParse(rawCreated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
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
                                         DateTimeOffset.TryParse(rawCreated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
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
    /// 校验并消费完整暂存包（含可选 manifestJson、可选 logicJs 与 locales 一致性校验；
    /// 当 manifestJson 为 null 时允许仅凭 stageId 直接消费已暂存包），返回完整已暂存内容供落盘回填。
    /// </summary>
    internal static bool TryConsumeBundle(
        string? stageId,
        string? manifestJson,
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
                string canonicalHash = meta.TryGetValue("manifestCanonicalSha256", out string? storedCanonicalHash)
                    ? storedCanonicalHash
                    : string.Empty;
                string? widgetId = meta.TryGetValue("widgetId", out string? storedWidgetId) &&
                                   !string.IsNullOrEmpty(storedWidgetId)
                    ? storedWidgetId
                    : null;
                DateTimeOffset created = meta.TryGetValue("createdUtc", out string? storedCreated) &&
                                         DateTimeOffset.TryParse(storedCreated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

                if (DateTimeOffset.UtcNow - created > MaxAge)
                {
                    Directory.Delete(dir, recursive: true);
                    NotifyChanged();
                    error = T(cultureName, "CustomWidget.AgentStage.Error.Expired");
                    return false;
                }

                string manifestPath = Path.Combine(dir, "manifest.json");
                string stagedManifestJson = File.Exists(manifestPath)
                    ? File.ReadAllText(manifestPath, Encoding.UTF8)
                    : (manifestJson ?? string.Empty);

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

                string? rawCallerHashForIdempotency = meta.TryGetValue("rawCallerManifestCanonicalSha256", out string? rch) && !string.IsNullOrEmpty(rch)
                    ? rch
                    : null;

                if (manifestJson is not null)
                {
                    string callerHash = ComputeManifestHash(manifestJson);
                    string callerCanonicalHash = ComputeCanonicalJsonHash(manifestJson);
                    bool manifestMatches =
                        string.Equals(callerHash, hash, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(callerCanonicalHash, canonicalHash, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(rawCallerHashForIdempotency) &&
                         string.Equals(callerCanonicalHash, rawCallerHashForIdempotency, StringComparison.OrdinalIgnoreCase));
                    if (!manifestMatches)
                    {
                        bundle = null;
                        error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                        return false;
                    }
                }

                string effectiveLogicHash = stagedHasLogicJs && meta.TryGetValue("logicSha256", out string? slh)
                    ? slh
                    : string.Empty;
                if (hasCallerLogicJs)
                {
                    string normalizedCallerLogic = !string.IsNullOrWhiteSpace(callerLogicJs) ? callerLogicJs : string.Empty;
                    string actualLogicHash = ComputeManifestHash(normalizedCallerLogic);
                    if (!stagedHasLogicJs
                        ? !string.IsNullOrWhiteSpace(callerLogicJs)
                        : !string.Equals(actualLogicHash, effectiveLogicHash, StringComparison.OrdinalIgnoreCase))
                    {
                        bundle = null;
                        error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                        return false;
                    }
                }

                string effectiveLocalesHash = stagedHasLocales && meta.TryGetValue("localesSha256", out string? sloc)
                    ? sloc
                    : string.Empty;
                if (hasCallerLocales)
                {
                    string actualLocalesHash = ComputeCanonicalJsonHash(callerLocalesJson);
                    if (!stagedHasLocales
                        ? !string.IsNullOrWhiteSpace(callerLocalesJson)
                        : !string.Equals(actualLocalesHash, effectiveLocalesHash, StringComparison.OrdinalIgnoreCase))
                    {
                        bundle = null;
                        error = T(cultureName, "CustomWidget.AgentStage.Error.ContentMismatch");
                        return false;
                    }
                }

                // Three-state commit lease: the stage directory and its idempotency
                // tombstone are only consumed after the caller's workspace write
                // succeeds (CompleteCommit). A failed write keeps the bundle retryable
                // (AbortCommit), and a concurrent replay of the same stage is rejected
                // while a commit is in flight instead of creating a duplicate widget.
                if (s_pendingCommits.ContainsKey(stageId))
                {
                    bundle = null;
                    error = T(cultureName, "CustomWidget.AgentStage.Error.CommitInProgress");
                    return false;
                }

                s_pendingCommits[stageId] = new PendingStageCommit(
                    dir,
                    widgetId,
                    hash,
                    canonicalHash,
                    rawCallerHashForIdempotency,
                    stagedHasLogicJs,
                    effectiveLogicHash,
                    stagedHasLocales,
                    effectiveLocalesHash);
                return true;
            }
            catch (Exception ex)
            {
                error = F(cultureName, "CustomWidget.AgentStage.Error.ReadFailed", ex.Message);
                return false;
            }
        }
    }

    internal static async Task WriteCachedThumbnailPngAtomicallyAsync(string cachePngPath, byte[] pngBytes)
    {
        string? dir = Path.GetDirectoryName(cachePngPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{cachePngPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(tempPath, pngBytes).ConfigureAwait(false);
            File.Move(tempPath, cachePngPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>用户主动忽略一份外部暂存：只丢弃批次，桌面与格子工作区都不变。
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
            if (s_pendingCommits.ContainsKey(stageId!) || !File.Exists(MetaPath(stageId!)))
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
            if (s_pendingCommits.ContainsKey(stageId!))
            {
                CompleteCommit(stageId!, widgetId);
                return;
            }

            RecordCommittedLocked(stageId!, widgetId, manifestSha256: null, manifestCanonicalSha256: null);
        }
    }

    private static void RecordCommittedLocked(
        string stageId,
        string? widgetId,
        string? manifestSha256,
        string? manifestCanonicalSha256,
        string? rawCallerManifestCanonicalSha256 = null,
        bool? hasLogicJs = null,
        string? logicSha256 = null,
        bool? hasLocales = null,
        string? localesSha256 = null)
    {
        try
        {
            Directory.CreateDirectory(CommittedRoot);
            string path = CommittedPath(stageId);
            string? preservedHash = manifestSha256;
            string? preservedCanonicalHash = manifestCanonicalSha256;
            string? preservedRawCallerHash = rawCallerManifestCanonicalSha256;
            bool? preservedHasLogicJs = hasLogicJs;
            string? preservedLogicHash = logicSha256;
            bool? preservedHasLocales = hasLocales;
            string? preservedLocalesHash = localesSha256;

            if (File.Exists(path))
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (string.IsNullOrEmpty(preservedHash) &&
                        line.StartsWith("manifestSha256=", StringComparison.Ordinal))
                    {
                        preservedHash = line["manifestSha256=".Length..].Trim();
                    }
                    else if (string.IsNullOrEmpty(preservedCanonicalHash) &&
                             line.StartsWith("manifestCanonicalSha256=", StringComparison.Ordinal))
                    {
                        preservedCanonicalHash = line["manifestCanonicalSha256=".Length..].Trim();
                    }
                    else if (string.IsNullOrEmpty(preservedRawCallerHash) &&
                             line.StartsWith("rawCallerManifestCanonicalSha256=", StringComparison.Ordinal))
                    {
                        preservedRawCallerHash = line["rawCallerManifestCanonicalSha256=".Length..].Trim();
                    }
                    else if (preservedHasLogicJs is null &&
                             line.StartsWith("hasLogicJs=", StringComparison.Ordinal))
                    {
                        preservedHasLogicJs = line["hasLogicJs=".Length..].Trim() == "1";
                    }
                    else if (string.IsNullOrEmpty(preservedLogicHash) &&
                             line.StartsWith("logicSha256=", StringComparison.Ordinal))
                    {
                        preservedLogicHash = line["logicSha256=".Length..].Trim();
                    }
                    else if (preservedHasLocales is null &&
                             line.StartsWith("hasLocales=", StringComparison.Ordinal))
                    {
                        preservedHasLocales = line["hasLocales=".Length..].Trim() == "1";
                    }
                    else if (string.IsNullOrEmpty(preservedLocalesHash) &&
                             line.StartsWith("localesSha256=", StringComparison.Ordinal))
                    {
                        preservedLocalesHash = line["localesSha256=".Length..].Trim();
                    }
                }
            }
            else if (File.Exists(MetaPath(stageId)))
            {
                Dictionary<string, string> meta = ReadMeta(stageId);
                preservedHash ??= meta.GetValueOrDefault("manifestSha256");
                preservedCanonicalHash ??= meta.GetValueOrDefault("manifestCanonicalSha256");
                preservedRawCallerHash ??= meta.GetValueOrDefault("rawCallerManifestCanonicalSha256");
                if (preservedHasLogicJs is null && meta.TryGetValue("hasLogicJs", out string? mhl))
                {
                    preservedHasLogicJs = mhl == "1";
                }
                preservedLogicHash ??= meta.GetValueOrDefault("logicSha256");
                if (preservedHasLocales is null && meta.TryGetValue("hasLocales", out string? mhloc))
                {
                    preservedHasLocales = mhloc == "1";
                }
                preservedLocalesHash ??= meta.GetValueOrDefault("localesSha256");
            }

            var lines = new List<string>(8)
            {
                "widgetId=" + (widgetId ?? string.Empty)
            };
            if (!string.IsNullOrEmpty(preservedHash))
            {
                lines.Add("manifestSha256=" + preservedHash);
            }
            if (!string.IsNullOrEmpty(preservedCanonicalHash))
            {
                lines.Add("manifestCanonicalSha256=" + preservedCanonicalHash);
            }
            if (!string.IsNullOrEmpty(preservedRawCallerHash))
            {
                lines.Add("rawCallerManifestCanonicalSha256=" + preservedRawCallerHash);
            }
            if (preservedHasLogicJs is { } hlVal)
            {
                lines.Add("hasLogicJs=" + (hlVal ? "1" : "0"));
            }
            if (!string.IsNullOrEmpty(preservedLogicHash))
            {
                lines.Add("logicSha256=" + preservedLogicHash);
            }
            if (preservedHasLocales is { } hlocVal)
            {
                lines.Add("hasLocales=" + (hlocVal ? "1" : "0"));
            }
            if (!string.IsNullOrEmpty(preservedLocalesHash))
            {
                lines.Add("localesSha256=" + preservedLocalesHash);
            }

            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch
        {
            // 幂等记录是尽力而为：写不进去不影响本次提交本身。
        }
    }

    internal static bool PreviewMatchesCallerManifest(string? stageId, string? callerManifestJson)
    {
        if (!IsValidStageId(stageId) || string.IsNullOrWhiteSpace(callerManifestJson))
        {
            return false;
        }

        lock (Gate)
        {
            string metaPath = MetaPath(stageId!);
            string manifestPath = Path.Combine(DirectoryOf(stageId!), "manifest.json");
            if (!File.Exists(metaPath) || !File.Exists(manifestPath))
            {
                return false;
            }

            try
            {
                Dictionary<string, string> meta = ReadMeta(stageId!);
                string? recordedHash = meta.GetValueOrDefault("manifestSha256");
                string? recordedCanonicalHash = meta.GetValueOrDefault("manifestCanonicalSha256");
                string? rawCallerHash = meta.TryGetValue("rawCallerManifestCanonicalSha256", out string? rch) && !string.IsNullOrEmpty(rch)
                    ? rch
                    : null;
                return MatchesExpectedManifest(callerManifestJson, recordedHash, recordedCanonicalHash, rawCallerHash);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 查询某个 stageId 是否已经提交成功过。stageId 由服务端生成、绑定内容摘要且一次性，
    /// 本身就是幂等键——客户端重试不该被当成「stageId 不存在」。
    /// </summary>
    internal static bool TryGetCommitted(
        string? stageId,
        out string? widgetId,
        string? expectedManifestJson = null,
        bool hasCallerLogicJs = false,
        string? callerLogicJs = null,
        bool hasCallerLocales = false,
        string? callerLocalesJson = null)
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
                string? parsedWidgetId = null;
                string? storedHash = null;
                string? storedCanonicalHash = null;
                string? storedRawCallerHash = null;
                bool storedHasLogicJs = false;
                bool hasStoredLogicFlag = false;
                string? storedLogicHash = null;
                bool storedHasLocales = false;
                bool hasStoredLocalesFlag = false;
                string? storedLocalesHash = null;

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.StartsWith("widgetId=", StringComparison.Ordinal))
                    {
                        string value = line["widgetId=".Length..].Trim();
                        parsedWidgetId = value.Length == 0 ? null : value;
                    }
                    else if (line.StartsWith("manifestSha256=", StringComparison.Ordinal))
                    {
                        storedHash = line["manifestSha256=".Length..].Trim();
                    }
                    else if (line.StartsWith("manifestCanonicalSha256=", StringComparison.Ordinal))
                    {
                        storedCanonicalHash = line["manifestCanonicalSha256=".Length..].Trim();
                    }
                    else if (line.StartsWith("rawCallerManifestCanonicalSha256=", StringComparison.Ordinal))
                    {
                        storedRawCallerHash = line["rawCallerManifestCanonicalSha256=".Length..].Trim();
                    }
                    else if (line.StartsWith("hasLogicJs=", StringComparison.Ordinal))
                    {
                        storedHasLogicJs = line["hasLogicJs=".Length..].Trim() == "1";
                        hasStoredLogicFlag = true;
                    }
                    else if (line.StartsWith("logicSha256=", StringComparison.Ordinal))
                    {
                        storedLogicHash = line["logicSha256=".Length..].Trim();
                    }
                    else if (line.StartsWith("hasLocales=", StringComparison.Ordinal))
                    {
                        storedHasLocales = line["hasLocales=".Length..].Trim() == "1";
                        hasStoredLocalesFlag = true;
                    }
                    else if (line.StartsWith("localesSha256=", StringComparison.Ordinal))
                    {
                        storedLocalesHash = line["localesSha256=".Length..].Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(expectedManifestJson) &&
                    !MatchesExpectedManifest(expectedManifestJson, storedHash, storedCanonicalHash, storedRawCallerHash))
                {
                    return false;
                }

                if (hasCallerLogicJs)
                {
                    if (!hasStoredLogicFlag)
                    {
                        return false;
                    }

                    string normalizedCallerLogic = !string.IsNullOrWhiteSpace(callerLogicJs) ? callerLogicJs : string.Empty;
                    if (!storedHasLogicJs)
                    {
                        if (!string.IsNullOrWhiteSpace(callerLogicJs))
                        {
                            return false;
                        }
                    }
                    else if (string.IsNullOrEmpty(storedLogicHash) ||
                             !string.Equals(ComputeManifestHash(normalizedCallerLogic), storedLogicHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (hasCallerLocales)
                {
                    if (!hasStoredLocalesFlag)
                    {
                        return false;
                    }

                    if (!storedHasLocales)
                    {
                        if (!string.IsNullOrWhiteSpace(callerLocalesJson))
                        {
                            return false;
                        }
                    }
                    else if (string.IsNullOrEmpty(storedLocalesHash) ||
                             !string.Equals(ComputeCanonicalJsonHash(callerLocalesJson), storedLocalesHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                widgetId = parsedWidgetId;
                return true;
            }
            catch
            {
                widgetId = null;
                return false;
            }
        }
    }

    private static bool MatchesExpectedManifest(
        string expectedManifestJson,
        string? recordedHash,
        string? recordedCanonicalHash,
        string? rawCallerCanonicalHash)
    {
        if (string.IsNullOrEmpty(recordedHash) &&
            string.IsNullOrEmpty(recordedCanonicalHash) &&
            string.IsNullOrEmpty(rawCallerCanonicalHash))
        {
            return false;
        }

        string callerHash = ComputeManifestHash(expectedManifestJson);
        string callerCanonicalHash = ComputeCanonicalJsonHash(expectedManifestJson);
        return (!string.IsNullOrEmpty(recordedHash) &&
                string.Equals(callerHash, recordedHash, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(recordedCanonicalHash) &&
                string.Equals(callerCanonicalHash, recordedCanonicalHash, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(rawCallerCanonicalHash) &&
                string.Equals(callerCanonicalHash, rawCallerCanonicalHash, StringComparison.OrdinalIgnoreCase));
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
                        s_pendingCommits.Remove(stageId);
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
