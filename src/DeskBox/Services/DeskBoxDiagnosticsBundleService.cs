using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record DeskBoxDiagnosticRect(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record DeskBoxDisplayDiagnostic(
    int Number,
    bool IsPrimary,
    double DpiScale,
    DeskBoxDiagnosticRect MonitorBounds,
    DeskBoxDiagnosticRect WorkAreaBounds);

public sealed record DeskBoxWidgetHostDiagnostic(
    int Number,
    WidgetKind WidgetKind,
    string HostKind,
    bool IsGroupSurface,
    bool Visible,
    bool Raised,
    bool Compact,
    bool ImportBusy,
    long? ImportBusyElapsedMilliseconds,
    DeskBoxDiagnosticRect AnimationBounds,
    DeskBoxDiagnosticRect RestingBounds);

public sealed record DeskBoxTrayQueueDiagnostic(
    int PendingCount,
    bool WorkerRunning,
    long TotalRequests,
    long EffectiveToggles,
    long FoldedNoOpBatches,
    string? LastSource,
    bool LastRequestFailed);

public sealed record DeskBoxFileHostDiagnostic(
    string Strategy,
    bool LegacyFallbackAvailable,
    long TotalStandaloneCreationCount,
    long UnifiedStandaloneCreationCount,
    long LegacyStandaloneCreationCount,
    double? UnifiedStandaloneUsagePercent,
    int LoadedStandaloneCount,
    int LoadedGroupedCount,
    int FallbackRequestCount,
    string? LastFallbackReason);

public sealed record DeskBoxWidgetManagerDiagnostic(
    bool WidgetsRaisedFromTray,
    string SessionState,
    bool InteractionActive,
    int LoadedSurfaceCount,
    int VisibleSurfaceCount,
    DeskBoxFileHostDiagnostic FileHosts,
    DeskBoxTrayQueueDiagnostic ToggleQueue,
    IReadOnlyList<DeskBoxWidgetHostDiagnostic> Hosts)
{
    public static DeskBoxWidgetManagerDiagnostic Empty { get; } = new(
        false,
        "Unavailable",
        false,
        0,
        0,
        new DeskBoxFileHostDiagnostic(
            "Unavailable",
            false,
            0,
            0,
            0,
            null,
            0,
            0,
            0,
            null),
        new DeskBoxTrayQueueDiagnostic(0, false, 0, 0, 0, null, false),
        []);
}

public sealed record DeskBoxSettingsDiagnostic(
    SettingsLoadRecoveryState LoadRecoveryState,
    bool HasPendingSave,
    string? LastSaveFailureOperation,
    DateTimeOffset? LastSaveFailureAtUtc);

/// <summary>
/// What this launch concluded about the previous one from the startup
/// session ledger — the first place to look when a report describes
/// restart loops or unattended kills (feedback #576).
/// </summary>
public sealed record DeskBoxStartupLedgerDiagnostic(
    bool StormGuardActive,
    string? AssessmentReason,
    DateTimeOffset? PreviousLaunchStartedAtUtc);

public sealed record DeskBoxShortcutNativeDiagnostic(
    string SelectedBackend,
    string ModuleName,
    bool ModuleExists,
    string? ModuleArchitecture,
    string? ModuleSha256,
    bool LoadAttempted,
    string LoadState,
    uint? AbiVersion,
    ulong? Capabilities);

public sealed record DeskBoxHotkeyDiagnostic(
    bool ToggleEnabled,
    bool ToggleRegistered,
    int ToggleModifiers,
    int ToggleVirtualKey,
    long ToggleReceivedCount,
    long ToggleInvocationCount,
    long ToggleDispatchFailureCount,
    bool ToggleUsesReservedHook,
    uint ToggleReservedHookThreadId,
    int ToggleReservedHookLastErrorCode,
    long ToggleReservedHookTriggerCount,
    long ToggleReservedHookPostFailureCount,
    long ToggleReservedHookInputFailureCount,
    bool ToggleRegistrationFailed,
    bool SearchEnabled,
    bool SearchRegistered,
    int SearchModifiers,
    int SearchVirtualKey);

/// <summary>
/// Field-wide observability for the callback-isolation invariant (drag-freeze
/// family): window operations that ran inside a native callback frame, per
/// Win32 entry point, plus dispatcher posts dropped during shutdown.
/// </summary>
public sealed record DeskBoxCallbackRadarDiagnostic(
    long TotalWindowOperationsInCallback,
    IReadOnlyDictionary<string, long> WindowOperationCounts,
    long PostAfterCallbackDropped);

/// <summary>
/// Wipe-recovery offer state: whether a configuration wipe was detected with
/// a surviving snapshot, which snapshot it pointed at, and what this session's
/// offer concluded. The first place to look when a report says settings were
/// lost after a restart (or that the offer never appeared).
/// </summary>
public sealed record DeskBoxRecoveryOfferDiagnostic(
    bool MarkerPresent,
    DateTimeOffset? DetectedAtUtc,
    DateTimeOffset? SnapshotCreatedAtUtc,
    string? SnapshotAppVersion,
    int SnapshotWidgetCount,
    string LastOutcome);

public sealed record DeskBoxDiagnosticSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    string DistributionChannel,
    bool IsPackaged,
    string OperatingSystem,
    string ProcessArchitecture,
    string UiCulture,
    DeskBoxHotkeyDiagnostic Hotkeys,
    DeskBoxSettingsDiagnostic Settings,
    DeskBoxRecoveryOfferDiagnostic RecoveryOffer,
    DeskBoxShortcutNativeDiagnostic ShortcutNative,
    AppRuntimeHealthSnapshot? RuntimeHealth,
    DeskBoxWidgetManagerDiagnostic WidgetManager,
    IReadOnlyList<DeskBoxDisplayDiagnostic> Displays,
    ManagedStorageMigrationDiagnostic? StorageMigration = null,
    DeskBoxStartupLedgerDiagnostic? StartupLedger = null,
    DeskBoxCallbackRadarDiagnostic? CallbackRadar = null);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(
    typeof(DeskBoxDiagnosticSnapshot),
    TypeInfoPropertyName = "DiagnosticSnapshot")]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Exports a deliberately narrow, privacy-filtered support package. It never
/// includes settings.json, widget content stores, file inventories, or user
/// attachment data.
/// </summary>
public sealed partial class DeskBoxDiagnosticsBundleService
{
    private const int MaximumLogTailBytes = 2 * 1024 * 1024;
    private const string ReadmeText = """
        DeskBox diagnostic package

        This archive contains a runtime snapshot, a sanitized tail of the application log, and metadata of recent
        crash events from the Windows Application event log (DeskBox and explorer.exe: times, exception codes,
        and faulting module names only — no event payloads or dump files).
        It does not contain settings.json, widget contents, file lists, clipboard records, tasks, or attachments.
        Paths, account names, email addresses, and Windows security identifiers are automatically redacted.
        Please review the archive before sharing it with support.

        DeskBox 诊断包

        本压缩包只包含运行状态快照、经过脱敏的应用日志末尾，以及 Windows 应用程序日志中近期崩溃事件的元数据
        （仅 DeskBox 与 explorer.exe 的时间、异常代码和故障模块名，不含事件正文或转储文件）。
        不包含设置文件、格子内容、文件列表、剪贴板记录、待办或附件。
        路径、账户名、电子邮箱和 Windows 安全标识符会自动隐藏。发送给支持人员前仍建议快速检查一次。
        """;

    [GeneratedRegex(
        "(?imx)\\b([a-z0-9_]*(?:path|folder|root|directory|dir|file|exe|commandline|token|secret|apikey|api_key|password|authorization)[a-z0-9_]*)\\s*=\\s*(?<value>'[^'\\r\\n]*'|\"[^\"\\r\\n]*\"|[^\\r\\n]*?)(?=\\s+[a-z0-9_]+\\s*=|\\r?$)")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(?:Bearer\s+[A-Za-z0-9\._\-]{12,}|sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AIza[A-Za-z0-9_\-]{30,})\b")]
    private static partial Regex InlineCredentialTokenRegex();

    [GeneratedRegex(@"(?i)(https?://[^\s\x27""<>?]+)\?[^\s\x27""<>]+")]
    private static partial Regex UrlQueryParametersRegex();

    [GeneratedRegex("(?i)(?<quote>['\"])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\r\\n]*?\\k<quote>")]
    private static partial Regex QuotedWindowsPathRegex();

    [GeneratedRegex("(?i)(?<![\\w])(?:[a-z]:[\\\\/]|\\\\\\\\)[^\\s'\",;)\\]]+")]
    private static partial Regex UnquotedWindowsPathRegex();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bS-1-(?:\d+-){1,14}\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityIdentifierRegex();

    [GeneratedRegex(@"^0x[0-9a-f]{1,8}[.,;:)]?$", RegexOptions.IgnoreCase)]
    private static partial Regex HexErrorCodeValueRegex();

    public async Task<string> ExportAsync(
        string destinationDirectory,
        DeskBoxDiagnosticSnapshot snapshot,
        string? logFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(destinationDirectory);
        string archivePath = GetAvailableArchivePath(destinationDirectory, snapshot.GeneratedAtUtc);
        string temporaryPath = archivePath + ".tmp";

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonEntryAsync(archive, snapshot, cancellationToken);
                await WriteTextEntryAsync(archive, "README.txt", ReadmeText, cancellationToken);

                string sanitizedLog = await ReadSanitizedLogTailAsync(logFilePath, cancellationToken);
                await WriteTextEntryAsync(
                    archive,
                    "DeskBox-sanitized.log",
                    sanitizedLog,
                    cancellationToken);

                var crashEventCollector = new WindowsCrashEventCollector();
                WindowsCrashEventReport crashEvents = await crashEventCollector.CollectAsync(
                    SanitizeDiagnosticsValue,
                    cancellationToken);
                await WriteTextEntryAsync(
                    archive,
                    "windows-events.json",
                    WindowsCrashEventCollector.SerializeReport(crashEvents),
                    cancellationToken);
            }

            File.Move(temporaryPath, archivePath);
            return archivePath;
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    internal static string SanitizeLog(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string sanitized = SensitiveAssignmentRegex().Replace(
            value,
            match =>
            {
                string raw = match.Groups["value"].Value;
                // The key keywords substring-match unrelated keys ("executeHr" contains
                // "exe"), but HRESULT hex values carry no user data, so keep them.
                return bool.TryParse(raw, out _) || HexErrorCodeValueRegex().IsMatch(raw)
                    ? match.Value
                    : $"{match.Groups[1].Value}=<REDACTED>";
            });
        sanitized = QuotedWindowsPathRegex().Replace(sanitized, "'<PATH>'");
        sanitized = UnquotedWindowsPathRegex().Replace(sanitized, "<PATH>");
        sanitized = InlineCredentialTokenRegex().Replace(sanitized, "<SECRET>");
        sanitized = UrlQueryParametersRegex().Replace(sanitized, "$1?<QUERY_REDACTED>");
        sanitized = EmailRegex().Replace(sanitized, "<EMAIL>");
        sanitized = SecurityIdentifierRegex().Replace(sanitized, "<SID>");

        foreach (string accountValue in GetAccountValues())
        {
            sanitized = sanitized.Replace(
                accountValue,
                "<USER>",
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    /// <summary>
    /// Redacts a single structured value (event-log properties). Unlike
    /// <see cref="SanitizeLog"/> this keeps the file name of a path — the
    /// image name is the diagnostic signal ("DeskBox.exe" vs
    /// "explorer.exe") while the directory is the private part. Log tails
    /// stay fully redacted.
    /// </summary>
    internal static string SanitizeDiagnosticsValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string sanitized = UnquotedWindowsPathRegex().Replace(
            value,
            match =>
            {
                string fileName = Path.GetFileName(match.Value.TrimEnd('.', ','));
                return string.IsNullOrEmpty(fileName) ? "<PATH>" : $"<PATH>\\{fileName}";
            });
        sanitized = InlineCredentialTokenRegex().Replace(sanitized, "<SECRET>");
        sanitized = UrlQueryParametersRegex().Replace(sanitized, "$1?<QUERY_REDACTED>");
        sanitized = EmailRegex().Replace(sanitized, "<EMAIL>");
        sanitized = SecurityIdentifierRegex().Replace(sanitized, "<SID>");

        foreach (string accountValue in GetAccountValues())
        {
            sanitized = sanitized.Replace(
                accountValue,
                "<USER>",
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive,
        DeskBoxDiagnosticSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            snapshot,
            DiagnosticsJsonContext.Default.DiagnosticSnapshot,
            cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static async Task<string> ReadSanitizedLogTailAsync(
        string? logFilePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
        {
            return "DeskBox log was not available when the package was created.";
        }

        await using var stream = new FileStream(
            logFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long start = Math.Max(0, stream.Length - MaximumLogTailBytes);
        stream.Position = start;
        byte[] buffer = new byte[checked((int)(stream.Length - start))];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        string log = Encoding.UTF8.GetString(buffer, 0, totalRead);
        if (start > 0)
        {
            int firstLineBreak = log.IndexOf('\n');
            log = firstLineBreak >= 0 ? log[(firstLineBreak + 1)..] : string.Empty;
        }

        return SanitizeLog(log);
    }

    private static IEnumerable<string> GetAccountValues()
    {
        string[] candidates =
        [
            Environment.UserName,
            Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty,
            Environment.GetEnvironmentVariable("USERDOMAIN") ?? string.Empty
        ];

        return candidates
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length);
    }

    private static string GetAvailableArchivePath(
        string destinationDirectory,
        DateTimeOffset generatedAtUtc)
    {
        string stem = $"DeskBox-Diagnostics-{generatedAtUtc.ToLocalTime():yyyyMMdd-HHmmss}";
        string path = Path.Combine(destinationDirectory, stem + ".zip");
        for (int suffix = 2; File.Exists(path) || File.Exists(path + ".tmp"); suffix++)
        {
            path = Path.Combine(destinationDirectory, $"{stem}-{suffix}.zip");
        }

        return path;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
