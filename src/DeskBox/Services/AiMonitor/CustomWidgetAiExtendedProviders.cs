using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public static partial class CustomWidgetAiMonitorEngine
{
    public const string ProviderGeminiCli = "gemini-cli";
    public const string ProviderMiniMax = "minimax";
    public const string ProviderSiliconFlow = "siliconflow";
    public const string ProviderOneApi = "oneapi";

    public sealed record GeminiTurnSample(
        DateTimeOffset Timestamp,
        string Model,
        long UncachedInputTokens,
        long CachedInputTokens,
        long OutputTokens,
        long ThoughtTokens,
        double CostUsd)
    {
        public long PromptTokens => UncachedInputTokens + CachedInputTokens;
        public long TotalTokens => UncachedInputTokens + CachedInputTokens + OutputTokens + ThoughtTokens;
    }

    public readonly record struct GeminiDailyRollup(
        DateOnly Date,
        long TotalTokens,
        double CostUsd,
        long CacheReadTokens,
        long PromptTokens);

    public sealed record CachedGeminiFileSummary(
        long LastWriteUtcTicks,
        long Length,
        IReadOnlyList<GeminiTurnSample> RecentTurns,
        IReadOnlyList<GeminiDailyRollup> DailyRollups,
        long ParsedLength = 0,
        ulong PrefixFingerprint = 0,
        long LastBytesScanned = 0,
        bool WasIncrementalTail = false);

    /// <summary>
    /// Evaluates whether a windowed subscription quota is burning significantly faster than
    /// elapsed window time (inspired by CodexBar / ClaudeBar pace indicators).
    /// </summary>
    public static bool EvaluateWindowPaceIsHot(
        double usedPercent,
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        TimeSpan windowDuration)
    {
        if (resetsAt is null || windowDuration <= TimeSpan.Zero || usedPercent < 30.0 || usedPercent >= 95.0)
        {
            return false;
        }

        TimeSpan remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero || remaining >= windowDuration)
        {
            return false;
        }

        double elapsedRatio = (windowDuration - remaining).TotalSeconds / windowDuration.TotalSeconds;
        if (elapsedRatio < 0.04)
        {
            return false;
        }

        double usedRatio = usedPercent / 100.0;
        return usedRatio >= Math.Max(elapsedRatio * 1.8, elapsedRatio + 0.25);
    }

    public static string FormatCompactCountdownShort(
        DateTimeOffset? resetsAt,
        DateTimeOffset now)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        TimeSpan rem = resetsAt.Value - now;
        if (rem <= TimeSpan.Zero)
        {
            return "0m";
        }

        if (rem.TotalDays >= 1.0)
        {
            int d = (int)rem.TotalDays;
            int h = rem.Hours;
            return h > 0
                ? FormattableString.Invariant($"{d}d{h}h")
                : FormattableString.Invariant($"{d}d");
        }

        if (rem.TotalHours >= 1.0)
        {
            return rem.Minutes > 0
                ? FormattableString.Invariant($"{(int)rem.TotalHours}h{rem.Minutes:D2}m")
                : FormattableString.Invariant($"{(int)rem.TotalHours}h");
        }

        return FormattableString.Invariant($"{Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes))}m");
    }

    /// <summary>
    /// Builds the single-line Scheme D ledger summary for a provider row:
    /// - Subscription window providers: e.g. <c>5h 剩 82% (2h18m) · 周余 96%</c>
    /// - Prepaid wallet providers: e.g. <c>今日 -¥1.20 · 基准 ¥100</c> or <c>API 钱包 · 基准 ¥100</c>
    /// </summary>
    public static string BuildProviderLedgerSummary(
        CustomWidgetAiProviderSnapshot provider,
        IReadOnlyDictionary<string, string>? settings,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        if (!string.IsNullOrWhiteSpace(provider.ErrorMessage))
        {
            return provider.ErrorMessage;
        }

        bool showRem = settings is null || IsRemainingMode(settings);
        if (provider.BillingKind == AiProviderBillingKind.WindowQuota)
        {
            var sb = new StringBuilder(48);
            if (provider.PrimaryWindow is { } pw)
            {
                double pVal = showRem ? pw.RemainingPercent : pw.UsedPercent;
                string modeShort = LocalizationService.TranslateForCulture(
                    c,
                    showRem ? "CustomWidget.AiMonitor.ShortRem" : "CustomWidget.AiMonitor.ShortUsed");
                sb.Append("5h ").Append(modeShort).Append(' ')
                  .Append(Math.Round(pVal).ToString("0", CultureInfo.InvariantCulture)).Append('%');

                string cd = FormatCompactCountdownShort(pw.ResetsAt, now);
                if (!string.IsNullOrEmpty(cd))
                {
                    sb.Append(" (").Append(cd).Append(')');
                }
            }

            if (provider.SecondaryWindow is { } sw)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" · ");
                }

                double sVal = showRem ? sw.RemainingPercent : sw.UsedPercent;
                bool isMcp = sw.Label.Contains("MCP", StringComparison.OrdinalIgnoreCase);
                string secKey = isMcp
                    ? "CustomWidget.AiMonitor.LedgerMcpPoolFormat"
                    : (showRem ? "CustomWidget.AiMonitor.WeeklyLeftFormat" : "CustomWidget.AiMonitor.LedgerWeeklyUsedFormat");
                sb.Append(LocalizationService.FormatForCulture(
                    c,
                    secKey,
                    Math.Round(sVal).ToString("0", CultureInfo.InvariantCulture)));
            }

            if (provider.IsPaceHot)
            {
                sb.Append(" · ").Append(LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.PaceFastBurn"));
            }

            return sb.Length > 0 ? sb.ToString() : provider.RadarSubtext;
        }

        if (provider.IsLocalCli && provider.BillingKind == AiProviderBillingKind.WalletBalance)
        {
            return !string.IsNullOrWhiteSpace(provider.WalletBreakdown)
                ? provider.WalletBreakdown
                : provider.RadarSubtext;
        }

        string walletCurrency = ResolveWalletCurrency(settings, c);
        double walletTarget = ResolveWalletTargetBalance(settings);
        string targetSummary = FormatWalletTargetSummary(walletTarget, walletCurrency);

        if (provider.WalletTodaySpend is > 0.004)
        {
            string spendFormatted = FormattableString.Invariant($"{provider.CurrencySymbol}{provider.WalletTodaySpend.Value:0.00}");
            return LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.LedgerWalletSpendAndTargetFormat",
                spendFormatted,
                targetSummary);
        }

        return LocalizationService.FormatForCulture(
            c,
            "CustomWidget.AiMonitor.LedgerWalletTargetOnlyFormat",
            targetSummary);
    }

    public static string ResolveProviderHealthBadgeLabel(
        CustomWidgetAiProviderSnapshot provider,
        AiQuotaHealth effectiveHealth,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        if (!provider.HasLiveData || !string.IsNullOrWhiteSpace(provider.ErrorMessage))
        {
            return LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.CapsuleErrorBadge");
        }

        return effectiveHealth switch
        {
            AiQuotaHealth.Critical => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.BadgeUrgent"),
            AiQuotaHealth.Warning => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.BadgeLow"),
            _ => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.BadgeHealthy")
        };
    }

    // =========================================================================
    // Gemini CLI Local Log Analyzer (~/.gemini/tmp/<project_hash>/chats)
    // =========================================================================

    public static double EstimateGeminiTurnCostUsd(
        string model,
        long uncachedInputTokens,
        long cachedInputTokens,
        long outputTokens,
        long thoughtTokens)
    {
        string m = (model ?? string.Empty).ToLowerInvariant();
        double inRate;
        double cacheRate;
        double outRate;

        if (m.Contains("pro", StringComparison.Ordinal))
        {
            inRate = 1.25;
            cacheRate = 0.3125;
            outRate = 10.0;
        }
        else if (m.Contains("lite", StringComparison.Ordinal))
        {
            inRate = 0.10;
            cacheRate = 0.025;
            outRate = 0.40;
        }
        else
        {
            inRate = 0.30;
            cacheRate = 0.075;
            outRate = 2.50;
        }

        long billableOut = Math.Max(0, outputTokens) + Math.Max(0, thoughtTokens);
        return (Math.Max(0, uncachedInputTokens) * inRate +
                Math.Max(0, cachedInputTokens) * cacheRate +
                billableOut * outRate) / 1_000_000.0;
    }

    public static (CustomWidgetAiProviderSnapshot Provider, IReadOnlyList<CachedGeminiFileSummary> Files, int FileCount)
        AnalyzeGeminiCliDirectory(
            string? geminiTmpDir,
            bool enabled,
            DateTimeOffset now,
            IDictionary<string, CachedGeminiFileSummary>? fileCache = null,
            string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var summaries = new List<CachedGeminiFileSummary>();
        int fileCount = 0;

        DateTimeOffset recentCutoff = now.AddDays(-8);
        DateOnly localToday = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DateOnly rollupCutoffDate = localToday.AddDays(-31);

        if (enabled && !string.IsNullOrWhiteSpace(geminiTmpDir) && Directory.Exists(geminiTmpDir))
        {
            DateTime cutoffUtc = now.UtcDateTime.AddDays(-32);
            var seenActiveFiles = fileCache is { Count: > 0 }
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;

            try
            {
                var rootDir = new DirectoryInfo(geminiTmpDir);
                foreach (FileInfo fi in EnumerateGeminiSessionCandidateFiles(rootDir))
                {
                    fileCount++;
                    string fullPath = fi.FullName;
                    DateTime lastWriteUtc = fi.LastWriteTimeUtc;
                    if (lastWriteUtc < cutoffUtc)
                    {
                        fileCache?.Remove(fullPath);
                        continue;
                    }

                    seenActiveFiles?.Add(fullPath);
                    long ticks = lastWriteUtc.Ticks;
                    long len = fi.Length;

                    if (fileCache is not null &&
                        fileCache.TryGetValue(fullPath, out CachedGeminiFileSummary? cached))
                    {
                        if (cached.LastWriteUtcTicks == ticks && cached.Length == len)
                        {
                            summaries.Add(cached);
                            continue;
                        }

                        CachedGeminiFileSummary updated = ReadOrTailGeminiSessionFile(
                            fullPath,
                            lastWriteUtc,
                            ticks,
                            len,
                            recentCutoff,
                            rollupCutoffDate,
                            previous: cached);
                        fileCache[fullPath] = updated;
                        summaries.Add(updated);
                        continue;
                    }

                    CachedGeminiFileSummary parsed = ReadOrTailGeminiSessionFile(
                        fullPath,
                        lastWriteUtc,
                        ticks,
                        len,
                        recentCutoff,
                        rollupCutoffDate,
                        previous: null);
                    if (fileCache is not null)
                    {
                        fileCache[fullPath] = parsed;
                    }
                    summaries.Add(parsed);
                }

                if (fileCache is not null && seenActiveFiles is not null)
                {
                    List<string>? stale = null;
                    foreach (string k in fileCache.Keys)
                    {
                        if (!seenActiveFiles.Contains(k))
                        {
                            (stale ??= []).Add(k);
                        }
                    }
                    if (stale is not null)
                    {
                        foreach (string k in stale)
                        {
                            fileCache.Remove(k);
                        }
                    }
                }
            }
            catch
            {
                // Ignore transient file access errors.
            }
        }
        else
        {
            fileCache?.Clear();
        }

        bool hasData = enabled && fileCount > 0;
        DateOnly weekCutoffDate = localToday.AddDays(-6);
        long todayTokens = 0;
        double todayCostUsd = 0.0;
        long weekTokens = 0;
        double weekCostUsd = 0.0;

        foreach (CachedGeminiFileSummary summary in summaries)
        {
            foreach (GeminiDailyRollup r in summary.DailyRollups)
            {
                if (r.Date == localToday)
                {
                    todayTokens += r.TotalTokens;
                    todayCostUsd += r.CostUsd;
                }

                if (r.Date >= weekCutoffDate && r.Date <= localToday)
                {
                    weekTokens += r.TotalTokens;
                    weekCostUsd += r.CostUsd;
                }
            }
        }

        string weekCostText = weekCostUsd.ToString("0.00", CultureInfo.InvariantCulture);
        string last7DaysCostLabel = LocalizationService.FormatForCulture(
            c,
            "CustomWidget.AiMonitor.Last7DaysCostFormat",
            weekCostText);

        string valueText;
        string breakdownText;
        if (!hasData)
        {
            valueText = "--";
            breakdownText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.WaitLocalSession");
        }
        else if (todayTokens > 0)
        {
            valueText = FormattableString.Invariant($"{FormatCompactTokens(todayTokens)} tok");
            breakdownText = FormattableString.Invariant($"~${todayCostUsd:0.00} · {last7DaysCostLabel}");
        }
        else if (weekTokens > 0)
        {
            valueText = FormattableString.Invariant($"{FormatCompactTokens(weekTokens)} tok");
            breakdownText = last7DaysCostLabel;
        }
        else
        {
            valueText = "0 tok";
            breakdownText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.StandbyStartChat");
        }

        var snapshot = new CustomWidgetAiProviderSnapshot(
            Id: ProviderGeminiCli,
            Name: "Gemini CLI",
            ShortName: "Gemini",
            Monogram: "Gm",
            BrandHex: "#4285F4",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: true,
            IsConfigured: hasData,
            HasLiveData: hasData,
            PlanBadge: "CLI",
            Health: hasData ? AiQuotaHealth.Healthy : AiQuotaHealth.Idle,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: todayTokens > 0 ? todayCostUsd : weekCostUsd,
            WalletFormatted: valueText,
            WalletBreakdown: breakdownText,
            WalletRemainingPercent: null,
            RadarValueText: valueText,
            RadarSubtext: breakdownText,
            ErrorMessage: null,
            UpdatedAt: now,
            WalletTodaySpend: todayCostUsd > 0.004 ? todayCostUsd : null,
            IsPaceHot: false);

        return (snapshot, summaries, fileCount);
    }

    private static IEnumerable<FileInfo> EnumerateGeminiSessionCandidateFiles(DirectoryInfo rootDir)
    {
        bool rootIsChats = string.Equals(rootDir.Name, "chats", StringComparison.OrdinalIgnoreCase);
        foreach (FileInfo fi in SafeEnumerateTopFiles(rootDir))
        {
            if (IsGeminiTranscriptExtension(fi.Extension) &&
                (rootIsChats || fi.Name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)))
            {
                yield return fi;
            }
        }

        DirectoryInfo[] subDirs;
        try
        {
            subDirs = rootDir.GetDirectories();
        }
        catch
        {
            yield break;
        }

        foreach (DirectoryInfo subDir in subDirs)
        {
            if (string.Equals(subDir.Name, "chats", StringComparison.OrdinalIgnoreCase))
            {
                foreach (FileInfo fi in SafeEnumerateTopFiles(subDir))
                {
                    if (IsGeminiTranscriptExtension(fi.Extension))
                    {
                        yield return fi;
                    }
                }
                continue;
            }

            foreach (FileInfo fi in SafeEnumerateTopFiles(subDir))
            {
                if (IsGeminiTranscriptExtension(fi.Extension) &&
                    fi.Name.StartsWith("session-", StringComparison.OrdinalIgnoreCase))
                {
                    yield return fi;
                }
            }

            string chatsPath = Path.Combine(subDir.FullName, "chats");
            if (Directory.Exists(chatsPath))
            {
                var chatsDir = new DirectoryInfo(chatsPath);
                foreach (FileInfo fi in SafeEnumerateTopFiles(chatsDir))
                {
                    if (IsGeminiTranscriptExtension(fi.Extension))
                    {
                        yield return fi;
                    }
                }
            }
        }
    }

    private static FileInfo[] SafeEnumerateTopFiles(DirectoryInfo dir)
    {
        try
        {
            return dir.GetFiles("*.*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    private static bool IsGeminiTranscriptExtension(string ext) =>
        string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ext, ".jsonl", StringComparison.OrdinalIgnoreCase);

    private static CachedGeminiFileSummary ReadOrTailGeminiSessionFile(
        string filePath,
        DateTime fallbackUtc,
        long ticks,
        long length,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate,
        CachedGeminiFileSummary? previous)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long actualLength = fs.Length;

            if (filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                if (previous is not null &&
                    previous.ParsedLength > 0 &&
                    actualLength > previous.ParsedLength &&
                    TryVerifyPrefixFingerprint(fs, previous.ParsedLength, previous.PrefixFingerprint))
                {
                    fs.Seek(previous.ParsedLength, SeekOrigin.Begin);
                    var tailTurns = new List<GeminiTurnSample>();
                    (long safeParsedLen, long bytesScanned) = ScanJsonlUtf8Stream(
                        fs,
                        previous.ParsedLength,
                        lineSpan => TryExtractGeminiTurnsFromUtf8Json(lineSpan, fallbackUtc, tailTurns));

                    ulong newFp = previous.ParsedLength >= PrefixFingerprintMaxBytes
                        ? previous.PrefixFingerprint
                        : ReadPrefixFingerprintFromStart(fs, safeParsedLen);

                    return MergeGeminiTailSummary(
                        previous,
                        ticks,
                        length,
                        safeParsedLen,
                        newFp,
                        bytesScanned,
                        tailTurns,
                        recentCutoff,
                        rollupCutoffDate);
                }

                fs.Seek(0, SeekOrigin.Begin);
                var jsonlTurns = new List<GeminiTurnSample>();
                (long fullParsedLen, long fullBytesScanned) = ScanJsonlUtf8Stream(
                    fs,
                    0,
                    lineSpan => TryExtractGeminiTurnsFromUtf8Json(lineSpan, fallbackUtc, jsonlTurns));
                ulong fullFp = ReadPrefixFingerprintFromStart(fs, fullParsedLen);

                return BuildGeminiFileSummary(
                    ticks,
                    length,
                    fullParsedLen,
                    fullFp,
                    fullBytesScanned,
                    wasIncrementalTail: false,
                    jsonlTurns,
                    recentCutoff,
                    rollupCutoffDate);
            }

            var sessionTurns = new List<GeminiTurnSample>();
            long streamBytes = StreamGeminiSessionJson(fs, fallbackUtc, sessionTurns);
            return BuildGeminiFileSummary(
                ticks,
                length,
                parsedLength: actualLength,
                prefixFingerprint: 0,
                bytesScanned: streamBytes,
                wasIncrementalTail: false,
                sessionTurns,
                recentCutoff,
                rollupCutoffDate);
        }
        catch
        {
            return previous ?? new CachedGeminiFileSummary(
                LastWriteUtcTicks: -1,
                Length: -1,
                RecentTurns: [],
                DailyRollups: []);
        }
    }

    private static CachedGeminiFileSummary BuildGeminiFileSummary(
        long ticks,
        long length,
        long parsedLength,
        ulong prefixFingerprint,
        long bytesScanned,
        bool wasIncrementalTail,
        List<GeminiTurnSample> turns,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate)
    {
        var rollupMap = new Dictionary<DateOnly, GeminiDailyRollup>();
        var recent = new List<GeminiTurnSample>();
        AccumulateGeminiTurns(turns, recentCutoff, rollupCutoffDate, rollupMap, recent);

        return new CachedGeminiFileSummary(
            LastWriteUtcTicks: ticks,
            Length: length,
            RecentTurns: recent.Count == 0 ? [] : recent.ToArray(),
            DailyRollups: rollupMap.Count == 0 ? [] : rollupMap.Values.OrderBy(static r => r.Date).ToArray(),
            ParsedLength: parsedLength,
            PrefixFingerprint: prefixFingerprint,
            LastBytesScanned: bytesScanned,
            WasIncrementalTail: wasIncrementalTail);
    }

    private static CachedGeminiFileSummary MergeGeminiTailSummary(
        CachedGeminiFileSummary previous,
        long ticks,
        long length,
        long parsedLength,
        ulong prefixFingerprint,
        long bytesScanned,
        List<GeminiTurnSample> tailTurns,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate)
    {
        if (tailTurns.Count == 0)
        {
            return previous with
            {
                LastWriteUtcTicks = ticks,
                Length = length,
                ParsedLength = parsedLength,
                PrefixFingerprint = prefixFingerprint,
                LastBytesScanned = bytesScanned,
                WasIncrementalTail = true
            };
        }

        var rollupMap = new Dictionary<DateOnly, GeminiDailyRollup>();
        foreach (GeminiDailyRollup r in previous.DailyRollups)
        {
            if (r.Date >= rollupCutoffDate)
            {
                rollupMap[r.Date] = r;
            }
        }

        var recent = new List<GeminiTurnSample>(previous.RecentTurns.Count + tailTurns.Count);
        foreach (GeminiTurnSample prevTurn in previous.RecentTurns)
        {
            if (prevTurn.Timestamp >= recentCutoff)
            {
                recent.Add(prevTurn);
            }
        }

        AccumulateGeminiTurns(tailTurns, recentCutoff, rollupCutoffDate, rollupMap, recent);
        return new CachedGeminiFileSummary(
            LastWriteUtcTicks: ticks,
            Length: length,
            RecentTurns: recent.Count == 0 ? [] : recent.ToArray(),
            DailyRollups: rollupMap.Count == 0 ? [] : rollupMap.Values.OrderBy(static r => r.Date).ToArray(),
            ParsedLength: parsedLength,
            PrefixFingerprint: prefixFingerprint,
            LastBytesScanned: bytesScanned,
            WasIncrementalTail: true);
    }

    private static void AccumulateGeminiTurns(
        List<GeminiTurnSample> turns,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate,
        Dictionary<DateOnly, GeminiDailyRollup> rollupMap,
        List<GeminiTurnSample> recent)
    {
        foreach (GeminiTurnSample t in turns)
        {
            DateOnly d = DateOnly.FromDateTime(t.Timestamp.ToLocalTime().DateTime);
            if (d >= rollupCutoffDate)
            {
                if (rollupMap.TryGetValue(d, out GeminiDailyRollup ex))
                {
                    rollupMap[d] = new GeminiDailyRollup(
                        d,
                        ex.TotalTokens + t.TotalTokens,
                        ex.CostUsd + t.CostUsd,
                        ex.CacheReadTokens + t.CachedInputTokens,
                        ex.PromptTokens + t.PromptTokens);
                }
                else
                {
                    rollupMap[d] = new GeminiDailyRollup(
                        d,
                        t.TotalTokens,
                        t.CostUsd,
                        t.CachedInputTokens,
                        t.PromptTokens);
                }
            }

            if (t.Timestamp >= recentCutoff)
            {
                recent.Add(t);
            }
        }
    }

    private readonly record struct RawGeminiTurnFields(
        DateTimeOffset? Timestamp,
        string? Model,
        long RawInput,
        long RawOutput,
        long Cached,
        long Thoughts);

    private enum GeminiJsonProp : byte
    {
        None,
        RootLastUpdated,
        RootTimestamp,
        RootStartTime,
        RootModel,
        RootMessages,
        RootTokens,
        MsgTimestamp,
        MsgModel,
        MsgTokens,
        TokInput,
        TokPrompt,
        TokOutput,
        TokCandidates,
        TokCached,
        TokThoughts
    }

    private struct GeminiJsonParseContext
    {
        public bool Started;
        public bool Rejected;
        public bool InMessagesArray;
        public bool InMessageObj;
        public bool InMsgTokensObj;
        public bool InRootTokensObj;
        public GeminiJsonProp Pending;
        public DateTimeOffset? RootLastUpdated;
        public DateTimeOffset? RootTimestamp;
        public DateTimeOffset? RootStartTime;
        public string? RootModel;
        public bool RootHasTokens;
        public long RootInput, RootPrompt, RootOutput, RootCandidates, RootCached, RootThoughts;
        public DateTimeOffset? MsgTs;
        public string? MsgModel;
        public bool MsgHasTokens;
        public long MsgInput, MsgPrompt, MsgOutput, MsgCandidates, MsgCached, MsgThoughts;
        public List<RawGeminiTurnFields>? RawTurns;

        public void ConsumeTokens(ref Utf8JsonReader reader)
        {
            while (reader.Read())
            {
                int depth = reader.CurrentDepth;
                if (!Started)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        Rejected = true;
                        return;
                    }
                    Started = true;
                    continue;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        Pending = GeminiJsonProp.None;
                        if (depth == 1)
                        {
                            if (reader.ValueTextEquals("lastUpdated"u8)) Pending = GeminiJsonProp.RootLastUpdated;
                            else if (reader.ValueTextEquals("timestamp"u8)) Pending = GeminiJsonProp.RootTimestamp;
                            else if (reader.ValueTextEquals("startTime"u8)) Pending = GeminiJsonProp.RootStartTime;
                            else if (reader.ValueTextEquals("model"u8)) Pending = GeminiJsonProp.RootModel;
                            else if (reader.ValueTextEquals("messages"u8)) Pending = GeminiJsonProp.RootMessages;
                            else if (reader.ValueTextEquals("tokens"u8)) Pending = GeminiJsonProp.RootTokens;
                        }
                        else if (InRootTokensObj && depth == 2)
                        {
                            Pending = ClassifyGeminiTokenProperty(ref reader);
                        }
                        else if (InMessageObj && depth == 3)
                        {
                            if (reader.ValueTextEquals("timestamp"u8)) Pending = GeminiJsonProp.MsgTimestamp;
                            else if (reader.ValueTextEquals("model"u8)) Pending = GeminiJsonProp.MsgModel;
                            else if (reader.ValueTextEquals("tokens"u8)) Pending = GeminiJsonProp.MsgTokens;
                        }
                        else if (InMsgTokensObj && depth == 4)
                        {
                            Pending = ClassifyGeminiTokenProperty(ref reader);
                        }
                        break;

                    case JsonTokenType.StartArray:
                        if (depth == 1 && Pending == GeminiJsonProp.RootMessages)
                        {
                            InMessagesArray = true;
                        }
                        Pending = GeminiJsonProp.None;
                        break;

                    case JsonTokenType.EndArray:
                        if (InMessagesArray && depth == 1)
                        {
                            InMessagesArray = false;
                        }
                        Pending = GeminiJsonProp.None;
                        break;

                    case JsonTokenType.StartObject:
                        if (depth == 1 && Pending == GeminiJsonProp.RootTokens)
                        {
                            InRootTokensObj = true;
                            RootHasTokens = true;
                        }
                        else if (InMessagesArray && depth == 2)
                        {
                            InMessageObj = true;
                            MsgTs = null;
                            MsgModel = null;
                            MsgHasTokens = false;
                            MsgInput = MsgPrompt = MsgOutput = MsgCandidates = MsgCached = MsgThoughts = 0;
                        }
                        else if (InMessageObj && depth == 3 && Pending == GeminiJsonProp.MsgTokens)
                        {
                            InMsgTokensObj = true;
                            MsgHasTokens = true;
                        }
                        Pending = GeminiJsonProp.None;
                        break;

                    case JsonTokenType.EndObject:
                        Pending = GeminiJsonProp.None;
                        if (InMsgTokensObj && depth == 3)
                        {
                            InMsgTokensObj = false;
                        }
                        else if (InMessageObj && depth == 2)
                        {
                            InMessageObj = false;
                            if (MsgHasTokens)
                            {
                                (RawTurns ??= []).Add(new RawGeminiTurnFields(
                                    MsgTs,
                                    MsgModel,
                                    MsgInput > 0 ? MsgInput : MsgPrompt,
                                    MsgOutput > 0 ? MsgOutput : MsgCandidates,
                                    Math.Max(0, MsgCached),
                                    Math.Max(0, MsgThoughts)));
                            }
                        }
                        else if (InRootTokensObj && depth == 1)
                        {
                            InRootTokensObj = false;
                        }
                        break;

                    case JsonTokenType.String:
                        switch (Pending)
                        {
                            case GeminiJsonProp.RootLastUpdated:
                                RootLastUpdated = TryParseIsoString(reader.GetString());
                                break;
                            case GeminiJsonProp.RootTimestamp:
                                RootTimestamp = TryParseIsoString(reader.GetString());
                                break;
                            case GeminiJsonProp.RootStartTime:
                                RootStartTime = TryParseIsoString(reader.GetString());
                                break;
                            case GeminiJsonProp.RootModel:
                                RootModel = reader.GetString();
                                break;
                            case GeminiJsonProp.MsgTimestamp:
                                MsgTs = TryParseIsoString(reader.GetString());
                                break;
                            case GeminiJsonProp.MsgModel:
                                MsgModel = reader.GetString();
                                break;
                            default:
                                if (Pending >= GeminiJsonProp.TokInput &&
                                    long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sv))
                                {
                                    if (InMsgTokensObj)
                                    {
                                        AssignGeminiTokenNumber(Pending, sv, ref MsgInput, ref MsgPrompt, ref MsgOutput, ref MsgCandidates, ref MsgCached, ref MsgThoughts);
                                    }
                                    else if (InRootTokensObj)
                                    {
                                        AssignGeminiTokenNumber(Pending, sv, ref RootInput, ref RootPrompt, ref RootOutput, ref RootCandidates, ref RootCached, ref RootThoughts);
                                    }
                                }
                                break;
                        }
                        Pending = GeminiJsonProp.None;
                        break;

                    case JsonTokenType.Number:
                        if (Pending >= GeminiJsonProp.TokInput && reader.TryGetInt64(out long nv))
                        {
                            if (InMsgTokensObj)
                            {
                                AssignGeminiTokenNumber(Pending, nv, ref MsgInput, ref MsgPrompt, ref MsgOutput, ref MsgCandidates, ref MsgCached, ref MsgThoughts);
                            }
                            else if (InRootTokensObj)
                            {
                                AssignGeminiTokenNumber(Pending, nv, ref RootInput, ref RootPrompt, ref RootOutput, ref RootCandidates, ref RootCached, ref RootThoughts);
                            }
                        }
                        Pending = GeminiJsonProp.None;
                        break;

                    default:
                        Pending = GeminiJsonProp.None;
                        break;
                }
            }
        }

        public readonly void EmitTurns(DateTime fallbackUtc, List<GeminiTurnSample> output)
        {
            if (Rejected)
            {
                return;
            }

            DateTimeOffset effectiveRootTs = RootLastUpdated ?? RootTimestamp ?? RootStartTime ?? fallbackUtc;
            string effectiveRootModel = string.IsNullOrWhiteSpace(RootModel) ? "gemini-2.5-flash" : RootModel;

            if (RawTurns is { Count: > 0 })
            {
                foreach (RawGeminiTurnFields rt in RawTurns)
                {
                    AppendValidatedGeminiTurn(rt, effectiveRootTs, effectiveRootModel, output);
                }
            }
            else if (RootHasTokens)
            {
                var rootTurn = new RawGeminiTurnFields(
                    effectiveRootTs,
                    effectiveRootModel,
                    RootInput > 0 ? RootInput : RootPrompt,
                    RootOutput > 0 ? RootOutput : RootCandidates,
                    Math.Max(0, RootCached),
                    Math.Max(0, RootThoughts));
                AppendValidatedGeminiTurn(rootTurn, effectiveRootTs, effectiveRootModel, output);
            }
        }
    }

    private static void TryExtractGeminiTurnsFromUtf8Json(
        ReadOnlySpan<byte> utf8Line,
        DateTime fallbackUtc,
        List<GeminiTurnSample> output)
    {
        if (utf8Line.Length >= 3 && utf8Line[0] == 0xEF && utf8Line[1] == 0xBB && utf8Line[2] == 0xBF)
        {
            utf8Line = utf8Line[3..];
        }

        if (utf8Line.Length < 16 || utf8Line.IndexOf("\"tokens\""u8) < 0)
        {
            return;
        }

        try
        {
            GeminiJsonParseContext ctx = default;
            var reader = new Utf8JsonReader(utf8Line);
            ctx.ConsumeTokens(ref reader);
            ctx.EmitTurns(fallbackUtc, output);
        }
        catch
        {
        }
    }

    private static long StreamGeminiSessionJson(
        Stream stream,
        DateTime fallbackUtc,
        List<GeminiTurnSample> output)
    {
        const int InitialBufferSize = 64 * 1024;
        const int MaxBufferSize = 32 * 1024 * 1024;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        int buffered = 0;
        long totalBytesRead = 0;
        JsonReaderState state = default;
        GeminiJsonParseContext ctx = default;

        try
        {
            while (true)
            {
                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= MaxBufferSize)
                    {
                        return totalBytesRead;
                    }

                    int nextSize = (int)Math.Min(MaxBufferSize, (long)buffer.Length * 2);
                    byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
                    buffer.AsSpan(0, buffered).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                totalBytesRead += Math.Max(0, read);
                buffered += Math.Max(0, read);
                bool isFinalBlock = read <= 0;

                if (buffered == 0 && isFinalBlock)
                {
                    break;
                }

                int startIdx = 0;
                if (!ctx.Started && buffered >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    startIdx = 3;
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(startIdx, buffered - startIdx), isFinalBlock, state);
                ctx.ConsumeTokens(ref reader);
                if (ctx.Rejected)
                {
                    return totalBytesRead;
                }

                state = reader.CurrentState;
                int consumed = startIdx + (int)reader.BytesConsumed;
                if (consumed > 0)
                {
                    int remaining = buffered - consumed;
                    if (remaining > 0)
                    {
                        buffer.AsSpan(consumed, remaining).CopyTo(buffer);
                    }
                    buffered = remaining;
                }

                if (isFinalBlock)
                {
                    break;
                }
            }

            ctx.EmitTurns(fallbackUtc, output);
        }
        catch
        {
            // Ignore malformed or truncated session files.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalBytesRead;
    }

    private static GeminiJsonProp ClassifyGeminiTokenProperty(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals("input"u8)) return GeminiJsonProp.TokInput;
        if (reader.ValueTextEquals("prompt"u8)) return GeminiJsonProp.TokPrompt;
        if (reader.ValueTextEquals("output"u8)) return GeminiJsonProp.TokOutput;
        if (reader.ValueTextEquals("candidates"u8)) return GeminiJsonProp.TokCandidates;
        if (reader.ValueTextEquals("cached"u8)) return GeminiJsonProp.TokCached;
        if (reader.ValueTextEquals("thoughts"u8)) return GeminiJsonProp.TokThoughts;
        return GeminiJsonProp.None;
    }

    private static void AssignGeminiTokenNumber(
        GeminiJsonProp prop,
        long value,
        ref long input,
        ref long prompt,
        ref long output,
        ref long candidates,
        ref long cached,
        ref long thoughts)
    {
        switch (prop)
        {
            case GeminiJsonProp.TokInput: input = value; break;
            case GeminiJsonProp.TokPrompt: prompt = value; break;
            case GeminiJsonProp.TokOutput: output = value; break;
            case GeminiJsonProp.TokCandidates: candidates = value; break;
            case GeminiJsonProp.TokCached: cached = value; break;
            case GeminiJsonProp.TokThoughts: thoughts = value; break;
        }
    }

    private static void AppendValidatedGeminiTurn(
        RawGeminiTurnFields rt,
        DateTimeOffset fallbackTs,
        string fallbackModel,
        List<GeminiTurnSample> output)
    {
        long uncachedInput = Math.Max(0, rt.RawInput - rt.Cached);
        long rawOutput = Math.Max(0, rt.RawOutput);
        if (uncachedInput + rt.Cached + rawOutput + rt.Thoughts <= 0)
        {
            return;
        }

        DateTimeOffset ts = rt.Timestamp ?? fallbackTs;
        string model = string.IsNullOrWhiteSpace(rt.Model) ? fallbackModel : rt.Model!;
        double cost = EstimateGeminiTurnCostUsd(model, uncachedInput, rt.Cached, rawOutput, rt.Thoughts);
        output.Add(new GeminiTurnSample(ts, model, uncachedInput, rt.Cached, rawOutput, rt.Thoughts, cost));
    }

    private static DateTimeOffset? TryParseIsoString(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) &&
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset dto))
        {
            return dto;
        }

        return null;
    }

    private static DateTimeOffset? TryReadIsoTimestamp(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el) &&
            el.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                el.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset dto))
        {
            return dto;
        }

        return null;
    }

    // =========================================================================
    // Cloud Providers: SiliconFlow, MiniMax Coding Plan, One-API / New-API
    // =========================================================================

    public static CustomWidgetAiProviderSnapshot ParseSiliconFlowBalanceJson(
        string json,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("status", out JsonElement statusEl) && statusEl.ValueKind == JsonValueKind.False)
        {
            string msg = root.TryGetProperty("message", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString() ?? "Error"
                : "Error";
            return CreateErrorSnapshot(ProviderSiliconFlow, msg, now, c);
        }

        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        double total = ParseStringOrNumber(data, "totalBalance");
        double charge = ParseStringOrNumber(data, "chargeBalance");
        double gift = ParseStringOrNumber(data, "balance");
        if (total <= 0 && (charge > 0 || gift > 0))
        {
            total = charge + gift;
        }

        bool isIntl = AiProviderCatalog.IsInternationalRegion(region);
        string currencyCode = isIntl ? "USD" : "CNY";
        string sym = isIntl ? "$" : "¥";
        AiQuotaHealth health = EvaluateWalletHealth(total, sym);
        string formatted = FormattableString.Invariant($"{sym} {total:0.00}");
        string chargeText = charge.ToString("0.00", CultureInfo.InvariantCulture);
        string giftText = gift.ToString("0.00", CultureInfo.InvariantCulture);
        string breakdown = gift > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownGrantedFormat",
                sym,
                chargeText,
                giftText)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownTopupFormat",
                currencyCode);

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderSiliconFlow,
            Name: GetProviderDisplayName(ProviderSiliconFlow, c),
            ShortName: "SiliconFlow",
            Monogram: "SF",
            BrandHex: "#7C3AED",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: currencyCode,
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: sym,
            WalletBalance: total,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: null,
            RadarValueText: formatted,
            RadarSubtext: gift > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextGrantedFormat", sym, giftText)
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextOfficial"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseMiniMaxQuotaJson(
        string json,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("base_resp", out JsonElement baseResp) &&
            baseResp.ValueKind == JsonValueKind.Object)
        {
            long statusCode = ReadLong(baseResp, "status_code");
            if (statusCode != 0)
            {
                string errMsg = statusCode == 1004
                    ? LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrMiniMaxPlanKey")
                    : (baseResp.TryGetProperty("status_msg", out JsonElement sm) && sm.ValueKind == JsonValueKind.String
                        ? sm.GetString() ?? $"Code {statusCode}"
                        : $"Code {statusCode}");
                return CreateErrorSnapshot(ProviderMiniMax, errMsg, now, c);
            }
        }

        if (root.TryGetProperty("code", out JsonElement codeEl) &&
            codeEl.ValueKind == JsonValueKind.Number &&
            codeEl.TryGetInt64(out long topCode) &&
            topCode != 0)
        {
            string errMsg = topCode == 1004
                ? LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrMiniMaxPlanKey")
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrInvalidKey");
            return CreateErrorSnapshot(ProviderMiniMax, errMsg, now, c);
        }

        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        string label5h = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window5hLabel");
        string label7d = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window7dAllowanceLabel");
        string autoCycleText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.AutoCycleReset");

        double intervalTotal = ParseStringOrNumber(data, "current_interval_total_count");
        double intervalUsed = ParseStringOrNumber(data, "current_interval_usage_count");
        DateTimeOffset? intervalReset = TryReadFlexibleResetTime(data, "current_interval_reset_time", now)
                                        ?? TryReadFlexibleResetTime(data, "end_time", now);

        if (intervalTotal <= 0 &&
            data.TryGetProperty("model_remains", out JsonElement mrArr) &&
            mrArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in mrArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                double tot = ParseStringOrNumber(item, "current_interval_total_count");
                if (tot <= 0)
                {
                    tot = ParseStringOrNumber(item, "total");
                }
                if (tot <= 0)
                {
                    continue;
                }

                intervalTotal = tot;
                if (item.TryGetProperty("remains", out _))
                {
                    double rem = ParseStringOrNumber(item, "remains");
                    intervalUsed = Math.Max(0.0, tot - rem);
                }
                else
                {
                    intervalUsed = ParseStringOrNumber(item, "current_interval_usage_count");
                }

                intervalReset ??= TryReadFlexibleResetTime(item, "end_time", now);
                if (intervalReset is null)
                {
                    double? remMs = ReadNullableDouble(item, "remains_time");
                    if (remMs is > 0)
                    {
                        intervalReset = remMs.Value > 86_400
                            ? now.AddMilliseconds(remMs.Value)
                            : now.AddSeconds(remMs.Value);
                    }
                }
                break;
            }
        }

        double primUsedPct = intervalTotal > 0
            ? Math.Clamp(intervalUsed / intervalTotal * 100.0, 0.0, 100.0)
            : 0.0;
        bool paceHot = EvaluateWindowPaceIsHot(primUsedPct, intervalReset, now, TimeSpan.FromHours(5));
        AiQuotaHealth primHealth = EvaluateWindowHealth(primUsedPct);
        if (paceHot && primHealth == AiQuotaHealth.Healthy)
        {
            primHealth = AiQuotaHealth.Warning;
        }

        var primaryWindow = new CustomWidgetAiWindowQuota(
            Label: label5h,
            UsedPercent: primUsedPct,
            ResetsAt: intervalReset,
            ResetText: intervalReset is not null ? FormatResetCountdown(intervalReset, now, c) : autoCycleText,
            Health: primHealth);

        double weeklyTotal = ParseStringOrNumber(data, "current_weekly_total_count");
        double weeklyUsed = ParseStringOrNumber(data, "current_weekly_usage_count");
        DateTimeOffset? weeklyReset = TryReadFlexibleResetTime(data, "current_weekly_reset_time", now);
        CustomWidgetAiWindowQuota? secondaryWindow = null;
        if (weeklyTotal > 0)
        {
            double secUsedPct = Math.Clamp(weeklyUsed / weeklyTotal * 100.0, 0.0, 100.0);
            secondaryWindow = new CustomWidgetAiWindowQuota(
                Label: label7d,
                UsedPercent: secUsedPct,
                ResetsAt: weeklyReset,
                ResetText: weeklyReset is not null ? FormatResetCountdown(weeklyReset, now, c) : autoCycleText,
                Health: EvaluateWindowHealth(secUsedPct));
        }

        AiQuotaHealth overall = secondaryWindow is not null
            ? (AiQuotaHealth)Math.Max((int)primaryWindow.Health, (int)secondaryWindow.Health)
            : primaryWindow.Health;

        string badge = AiProviderCatalog.IsInternationalRegion(region) ? "Global" : "Coding";

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderMiniMax,
            Name: GetProviderDisplayName(ProviderMiniMax, c),
            ShortName: "MiniMax",
            Monogram: "MM",
            BrandHex: "#EC4899",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: badge,
            Health: overall,
            PrimaryWindow: primaryWindow,
            SecondaryWindow: secondaryWindow,
            CurrencySymbol: "¥",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: FormattableString.Invariant($"{Math.Round(primaryWindow.RemainingPercent):0}%"),
            RadarSubtext: primaryWindow.ResetsAt is not null ? $"{primaryWindow.Label} · {primaryWindow.ResetText}" : $"MiniMax {badge}",
            ErrorMessage: null,
            UpdatedAt: now,
            IsPaceHot: paceHot);
    }

    public static string NormalizeGatewayBaseUrl(string? rawEndpoint) =>
        AiProviderCatalog.NormalizeGatewayBaseUrl(rawEndpoint);

    public static CustomWidgetAiProviderSnapshot ParseOneApiBillingJson(
        string combinedJson,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(combinedJson);
        JsonElement root = doc.RootElement;

        double hardLimitUsd = ParseStringOrNumber(root, "hard_limit_usd");
        if (hardLimitUsd <= 0)
        {
            hardLimitUsd = ParseStringOrNumber(root, "system_hard_limit_usd");
        }

        double usedUsd = root.TryGetProperty("used_usd", out _)
            ? ParseStringOrNumber(root, "used_usd")
            : ParseStringOrNumber(root, "total_usage") / 100.0;

        string hostBadge = root.TryGetProperty("host", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.String
            ? hEl.GetString() ?? "USD"
            : "USD";
        if (string.IsNullOrWhiteSpace(hostBadge))
        {
            hostBadge = "USD";
        }

        bool isUnlimited = hardLimitUsd >= 10_000_000.0;
        double available = isUnlimited ? 9999.0 : Math.Max(0.0, hardLimitUsd - usedUsd);
        double? remPct = !isUnlimited && hardLimitUsd > 0
            ? Math.Clamp(available / hardLimitUsd * 100.0, 0.0, 100.0)
            : (isUnlimited ? 100.0 : null);

        AiQuotaHealth health = isUnlimited ? AiQuotaHealth.Healthy : EvaluateWalletHealth(available, "$");
        string formatted = isUnlimited
            ? "∞"
            : FormattableString.Invariant($"$ {available:0.00}");
        string breakdown = isUnlimited
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.OneApiUnlimitedUsedFormat",
                FormattableString.Invariant($"${usedUsd:0.00}"))
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.OpenRouterBreakdownFormat",
                FormattableString.Invariant($"${hardLimitUsd:0.00}"),
                FormattableString.Invariant($"${usedUsd:0.00}"));

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderOneApi,
            Name: GetProviderDisplayName(ProviderOneApi, c),
            ShortName: "OneAPI",
            Monogram: "OA",
            BrandHex: "#F59E0B",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "USD",
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: available,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: remPct,
            RadarValueText: formatted,
            RadarSubtext: hostBadge,
            ErrorMessage: null,
            UpdatedAt: now);
    }

    internal static async Task<(CustomWidgetAiProviderSnapshot Snapshot, string? RawBody)> FetchMiniMaxProviderInternalAsync(
        HttpClient httpClient,
        AiProviderCredentialEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string culture)
    {
        bool isIntl = AiProviderCatalog.IsInternationalRegion(entry.Region);
        string[] candidateUrls = isIntl
            ?
            [
                "https://api.minimax.io/v1/token_plan/remains",
                "https://api.minimax.io/v1/api/openplatform/coding_plan/remains"
            ]
            :
            [
                "https://api.minimaxi.com/v1/token_plan/remains",
                "https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains"
            ];

        string? lastBody = null;
        int lastStatus = 0;
        for (int i = 0; i < candidateUrls.Length; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, candidateUrls[i]);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage resp = await httpClient.SendAsync(req, cancellationToken);
            lastStatus = (int)resp.StatusCode;
            lastBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(lastBody))
            {
                CustomWidgetAiProviderSnapshot parsed = ParseMiniMaxQuotaJson(lastBody, entry.Region, now, culture);
                if (parsed.ErrorMessage is null || i == candidateUrls.Length - 1)
                {
                    return (parsed, lastBody);
                }
            }
        }

        string err = lastStatus is 401 or 403
            ? LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.ErrInvalidKey")
            : LocalizationService.FormatForCulture(culture, "CustomWidget.AiMonitor.ErrHttpFormat", lastStatus);
        return (CreateErrorSnapshot(ProviderMiniMax, err, now, culture), null);
    }

    internal static async Task<(CustomWidgetAiProviderSnapshot Snapshot, string? RawBody)> FetchOneApiProviderInternalAsync(
        HttpClient httpClient,
        AiProviderCredentialEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string culture)
    {
        string baseRoot = NormalizeGatewayBaseUrl(entry.CustomEndpoint);
        if (string.IsNullOrEmpty(baseRoot))
        {
            return (CreateErrorSnapshot(
                ProviderOneApi,
                LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.ErrMissingEndpoint"),
                now,
                culture), null);
        }

        string subUrl = $"{baseRoot}/v1/dashboard/billing/subscription";
        using var subReq = new HttpRequestMessage(HttpMethod.Get, subUrl);
        subReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey.Trim());
        subReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage subResp = await httpClient.SendAsync(subReq, cancellationToken);
        string subBody = await subResp.Content.ReadAsStringAsync(cancellationToken);
        if (!subResp.IsSuccessStatusCode)
        {
            string err = (int)subResp.StatusCode is 401 or 403
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.ErrInvalidKey")
                : LocalizationService.FormatForCulture(culture, "CustomWidget.AiMonitor.ErrHttpFormat", (int)subResp.StatusCode);
            return (CreateErrorSnapshot(ProviderOneApi, err, now, culture), null);
        }

        double hardLimitUsd = 0;
        using (JsonDocument subDoc = JsonDocument.Parse(subBody))
        {
            hardLimitUsd = ParseStringOrNumber(subDoc.RootElement, "hard_limit_usd");
            if (hardLimitUsd <= 0)
            {
                hardLimitUsd = ParseStringOrNumber(subDoc.RootElement, "system_hard_limit_usd");
            }
        }

        double usedUsd = 0;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        string startStr = today.AddDays(-99).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string endStr = today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string usageUrl = $"{baseRoot}/v1/dashboard/billing/usage?start_date={startStr}&end_date={endStr}";
        try
        {
            using var usageReq = new HttpRequestMessage(HttpMethod.Get, usageUrl);
            usageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey.Trim());
            usageReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage usageResp = await httpClient.SendAsync(usageReq, cancellationToken);
            if (usageResp.IsSuccessStatusCode)
            {
                string usageBody = await usageResp.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument usageDoc = JsonDocument.Parse(usageBody);
                usedUsd = ParseStringOrNumber(usageDoc.RootElement, "total_usage") / 100.0;
            }
        }
        catch
        {
            // Keep subscription quota if /usage endpoint is disabled on gateway.
        }

        string hostLabel = Uri.TryCreate(baseRoot, UriKind.Absolute, out Uri? u) ? u.Host : "USD";
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("hard_limit_usd", hardLimitUsd);
            writer.WriteNumber("used_usd", usedUsd);
            writer.WriteString("host", hostLabel);
            writer.WriteEndObject();
        }

        string combinedJson = Encoding.UTF8.GetString(ms.ToArray());
        return (ParseOneApiBillingJson(combinedJson, now, culture), combinedJson);
    }

    private static DateTimeOffset? TryReadFlexibleResetTime(JsonElement obj, string propName, DateTimeOffset now)
    {
        DateTimeOffset? epoch = ReadEpochMsOrSec(obj, propName);
        if (epoch is not null)
        {
            return epoch;
        }

        return TryReadIsoTimestamp(obj, propName);
    }

    // =========================================================================
    // 28-Day Activity Intensity & Tone Classification
    // =========================================================================

    public static string ClassifyDailyBucketTone(
        CustomWidgetAiDailyBucket bucket,
        double meanActiveTokens,
        double meanActiveCostUsd)
    {
        if (bucket.TotalTokens <= 0 && bucket.CostUsd <= 0.0005)
        {
            return "subtle";
        }

        bool extremeBurn = bucket.CostUsd >= 50.0 ||
                           (meanActiveCostUsd > 1.0 && bucket.CostUsd >= meanActiveCostUsd * 6.0);
        if (extremeBurn)
        {
            return "danger";
        }

        bool highBurn = bucket.CostUsd >= 5.0 ||
                        (meanActiveCostUsd > 0.10 && bucket.CostUsd >= meanActiveCostUsd * 2.0) ||
                        (meanActiveTokens >= 20_000 && bucket.TotalTokens >= meanActiveTokens * 2.2);
        return highBurn ? "warning" : "emerald";
    }
}

/// <summary>
/// Lightweight local ledger tracking daily wallet balance deltas so prepaid API providers
/// (DeepSeek, SiliconFlow, Kimi, OpenRouter, OneAPI) surface today's spend and 28-day activity.
/// </summary>
public sealed class CustomWidgetAiWalletLedger
{
    private static readonly Lazy<CustomWidgetAiWalletLedger> SharedInstance =
        new(() => new CustomWidgetAiWalletLedger(null));

    public static CustomWidgetAiWalletLedger Shared => SharedInstance.Value;

    private sealed record DayBalanceState(
        double LastBalance,
        double AccumulatedSpend,
        string CurrencySymbol);

    private readonly object _sync = new();
    private readonly string? _filePath;
    private Dictionary<string, Dictionary<DateOnly, DayBalanceState>>? _providers;
    private bool _dirty;

    public CustomWidgetAiWalletLedger(string? rootDataDirectory, bool inMemoryOnly = false)
    {
        if (!inMemoryOnly)
        {
            string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
                ? DeskBoxDataPathService.Current.DataDirectory
                : rootDataDirectory;
            Directory.CreateDirectory(baseDir);
            _filePath = Path.Combine(baseDir, "ai-wallet-ledger.json");
        }
    }

    public void InvalidateCache()
    {
        lock (_sync)
        {
            _providers = null;
            _dirty = false;
        }
    }

    public double RecordObservation(
        string providerId,
        DateOnly localDate,
        double currentBalance,
        string currencySymbol)
    {
        if (string.IsNullOrWhiteSpace(providerId) || !double.IsFinite(currentBalance) || currentBalance < 0)
        {
            return 0.0;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            string id = providerId.Trim().ToLowerInvariant();
            if (!_providers!.TryGetValue(id, out Dictionary<DateOnly, DayBalanceState>? byDate))
            {
                byDate = [];
                _providers[id] = byDate;
            }

            if (!byDate.TryGetValue(localDate, out DayBalanceState? prev))
            {
                // Seed from most recent earlier day if available so overnight spend is captured cleanly.
                double seedBaseline = currentBalance;
                DateOnly yesterday = localDate.AddDays(-1);
                if (byDate.TryGetValue(yesterday, out DayBalanceState? yState) &&
                    string.Equals(yState.CurrencySymbol, currencySymbol, StringComparison.Ordinal) &&
                    yState.LastBalance > currentBalance)
                {
                    seedBaseline = yState.LastBalance;
                }

                double initialSpend = Math.Max(0.0, seedBaseline - currentBalance);
                byDate[localDate] = new DayBalanceState(currentBalance, initialSpend, currencySymbol);
                PruneOldEntriesNoLock(localDate.AddDays(-35));
                _dirty = true;
                return initialSpend;
            }

            if (!string.Equals(prev.CurrencySymbol, currencySymbol, StringComparison.Ordinal))
            {
                byDate[localDate] = new DayBalanceState(currentBalance, 0.0, currencySymbol);
                _dirty = true;
                return 0.0;
            }

            double deltaSpend = prev.LastBalance - currentBalance;
            double nextSpend = deltaSpend > 0.0005
                ? prev.AccumulatedSpend + deltaSpend
                : prev.AccumulatedSpend;

            if (Math.Abs(prev.LastBalance - currentBalance) > 0.0005)
            {
                byDate[localDate] = new DayBalanceState(currentBalance, nextSpend, currencySymbol);
                _dirty = true;
            }

            return nextSpend;
        }
    }

    public IReadOnlyDictionary<DateOnly, double> GetDailyUsdSpendRange(DateOnly startDate, DateOnly endDate)
    {
        lock (_sync)
        {
            EnsureLoadedNoLock();
            var map = new Dictionary<DateOnly, double>();
            foreach (Dictionary<DateOnly, DayBalanceState> byDate in _providers!.Values)
            {
                foreach ((DateOnly d, DayBalanceState st) in byDate)
                {
                    if (d < startDate || d > endDate || st.AccumulatedSpend <= 0.0005)
                    {
                        continue;
                    }

                    string code = CustomWidgetAiMonitorEngine.MapCurrencySymbolToCode(st.CurrencySymbol);
                    double usd = CustomWidgetAiMonitorEngine.ConvertCurrencyAmount(st.AccumulatedSpend, code, "USD");
                    map[d] = map.TryGetValue(d, out double cur) ? cur + usd : usd;
                }
            }

            return map;
        }
    }

    public void FlushIfDirty()
    {
        lock (_sync)
        {
            if (!_dirty || string.IsNullOrEmpty(_filePath) || _providers is null)
            {
                return;
            }

            _dirty = false;
            try
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", 1);
                    writer.WriteStartObject("providers");
                    foreach ((string id, Dictionary<DateOnly, DayBalanceState> byDate) in _providers)
                    {
                        writer.WriteStartObject(id);
                        foreach ((DateOnly d, DayBalanceState st) in byDate.OrderBy(static kv => kv.Key))
                        {
                            writer.WriteStartObject(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                            writer.WriteNumber("last", Math.Round(st.LastBalance, 4));
                            writer.WriteNumber("spend", Math.Round(st.AccumulatedSpend, 4));
                            writer.WriteString("sym", st.CurrencySymbol);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                string tmp = _filePath + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                CustomWidgetStorageFileOps.ReplaceJsonFileAtomically(tmp, _filePath);
            }
            catch
            {
                // Non-critical telemetry persistence.
            }
        }
    }

    private void PruneOldEntriesNoLock(DateOnly cutoff)
    {
        if (_providers is null)
        {
            return;
        }

        foreach (Dictionary<DateOnly, DayBalanceState> byDate in _providers.Values)
        {
            List<DateOnly>? stale = null;
            foreach (DateOnly d in byDate.Keys)
            {
                if (d < cutoff)
                {
                    (stale ??= []).Add(d);
                }
            }
            if (stale is not null)
            {
                foreach (DateOnly d in stale)
                {
                    byDate.Remove(d);
                }
            }
        }
    }

    private void EnsureLoadedNoLock()
    {
        if (_providers is not null)
        {
            return;
        }

        _providers = new Dictionary<string, Dictionary<DateOnly, DayBalanceState>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(_filePath);
            using JsonDocument doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("providers", out JsonElement provEl) ||
                provEl.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty pProp in provEl.EnumerateObject())
            {
                if (pProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var byDate = new Dictionary<DateOnly, DayBalanceState>();
                foreach (JsonProperty dProp in pProp.Value.EnumerateObject())
                {
                    if (!DateOnly.TryParseExact(
                            dProp.Name,
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateOnly date) ||
                        dProp.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    double last = dProp.Value.TryGetProperty("last", out JsonElement lEl) && lEl.TryGetDouble(out double lv) ? lv : 0;
                    double spend = dProp.Value.TryGetProperty("spend", out JsonElement sEl) && sEl.TryGetDouble(out double sv) ? sv : 0;
                    string sym = dProp.Value.TryGetProperty("sym", out JsonElement symEl) && symEl.ValueKind == JsonValueKind.String
                        ? symEl.GetString() ?? "¥"
                        : "¥";
                    byDate[date] = new DayBalanceState(last, Math.Max(0, spend), sym);
                }

                if (byDate.Count > 0)
                {
                    _providers[pProp.Name] = byDate;
                }
            }
        }
        catch
        {
            // Fall back cleanly to fresh state.
        }
    }
}
