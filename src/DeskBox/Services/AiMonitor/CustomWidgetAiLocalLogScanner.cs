using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public static partial class CustomWidgetAiMonitorEngine
{
    public sealed record ClaudeTurnSample(
        DateTimeOffset Timestamp,
        string Model,
        long InputTokens,
        long OutputTokens,
        long CacheWriteTokens,
        long CacheReadTokens,
        double CostUsd)
    {
        public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;

        public long PromptTokens => InputTokens + CacheWriteTokens + CacheReadTokens;
    }

    public readonly record struct ClaudeDailyRollup(
        DateOnly Date,
        long TotalTokens,
        double CostUsd,
        long CacheReadTokens,
        long PromptTokens);

    public sealed record CachedClaudeFileSummary(
        long LastWriteUtcTicks,
        long Length,
        IReadOnlyList<ClaudeTurnSample> Turns,
        long ParsedLength = 0,
        ulong PrefixFingerprint = 0,
        IReadOnlyList<ClaudeDailyRollup>? DailyRollups = null,
        long LastBytesScanned = 0,
        bool WasIncrementalTail = false)
    {
        internal Dictionary<string, ClaudeTurnSample>? ActiveMessageTurns { get; init; }
    }

    public sealed record CodexRateLimitEvent(
        DateTimeOffset Timestamp,
        string PlanType,
        double? PrimaryUsedPercent,
        DateTimeOffset? PrimaryResetsAt,
        double? SecondaryUsedPercent,
        DateTimeOffset? SecondaryResetsAt);

    public sealed record CodexDailyRollup(
        DateOnly Date,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        double CostUsd);

    public sealed record CachedCodexFileSummary(
        long LastWriteUtcTicks,
        long Length,
        DateTimeOffset SessionTimestamp,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens,
        double CostUsd,
        CodexRateLimitEvent? LatestRateLimit,
        long ParsedLength = 0,
        ulong PrefixFingerprint = 0,
        long RawMaxInputTokens = 0,
        long LastBytesScanned = 0,
        bool WasIncrementalTail = false,
        IReadOnlyDictionary<DateOnly, CodexDailyRollup>? DailyRollups = null,
        long LastInputTokens = 0,
        long LastCachedInputTokens = 0,
        long LastOutputTokens = 0);


    internal static ulong ComputePrefixFingerprint(ReadOnlySpan<byte> prefix)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < prefix.Length; i++)
        {
            hash ^= prefix[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    internal static ulong ReadPrefixFingerprintFromStart(Stream stream, long limitLength)
    {
        int toRead = (int)Math.Min(PrefixFingerprintMaxBytes, Math.Max(0L, limitLength));
        if (toRead <= 0)
        {
            return ComputePrefixFingerprint(ReadOnlySpan<byte>.Empty);
        }

        Span<byte> buf = stackalloc byte[PrefixFingerprintMaxBytes];
        stream.Seek(0, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < toRead)
        {
            int n = stream.Read(buf.Slice(totalRead, toRead - totalRead));
            if (n <= 0)
            {
                break;
            }
            totalRead += n;
        }

        return ComputePrefixFingerprint(buf[..totalRead]);
    }

    internal static bool TryVerifyPrefixFingerprint(Stream stream, long parsedLength, ulong expectedFingerprint)
    {
        if (parsedLength <= 0)
        {
            return false;
        }

        int toVerify = (int)Math.Min(PrefixFingerprintMaxBytes, parsedLength);
        Span<byte> buf = stackalloc byte[PrefixFingerprintMaxBytes];
        stream.Seek(0, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < toVerify)
        {
            int n = stream.Read(buf.Slice(totalRead, toVerify - totalRead));
            if (n <= 0)
            {
                return false;
            }
            totalRead += n;
        }

        return ComputePrefixFingerprint(buf[..totalRead]) == expectedFingerprint;
    }

    public static (CustomWidgetAiProviderSnapshot Provider, IReadOnlyList<ClaudeTurnSample> Turns, int FileCount)
        AnalyzeClaudeDirectory(
            string? claudeProjectsDir,
            string planTier,
            bool enabled,
            DateTimeOffset now,
            IDictionary<string, CachedClaudeFileSummary>? fileCache = null,
            string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var recentTurns = new List<ClaudeTurnSample>();
        int fileCount = 0;
        DateTimeOffset recentCutoff = now.AddDays(-8);
        DateOnly rollupCutoffDate = DateOnly.FromDateTime(now.ToLocalTime().DateTime).AddDays(-31);
        DateTime activeSessionCutoffUtc = now.UtcDateTime.AddHours(-24);

        if (enabled && !string.IsNullOrWhiteSpace(claudeProjectsDir) && Directory.Exists(claudeProjectsDir))
        {
            DateTime cutoffUtc = now.UtcDateTime.AddDays(-32);
            HashSet<string>? seenActiveFiles = fileCache is { Count: > 0 }
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;
            try
            {
                var rootDir = new DirectoryInfo(claudeProjectsDir);
                foreach (FileInfo fi in rootDir.EnumerateFiles("*.jsonl", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    fileCount++;
                    string file = fi.FullName;
                    DateTime lastWriteUtc = fi.LastWriteTimeUtc;
                    if (lastWriteUtc < cutoffUtc)
                    {
                        fileCache?.Remove(file);
                        continue;
                    }

                    seenActiveFiles?.Add(file);
                    long ticks = lastWriteUtc.Ticks;
                    long len = fi.Length;
                    if (fileCache is not null &&
                        fileCache.TryGetValue(file, out CachedClaudeFileSummary? cached))
                    {
                        if (cached.LastWriteUtcTicks == ticks && cached.Length == len)
                        {
                            if (cached.Turns.Count > 0)
                            {
                                recentTurns.AddRange(cached.Turns);
                            }
                            continue;
                        }

                        CachedClaudeFileSummary updated = ReadOrTailClaudeJsonlFile(
                            file,
                            lastWriteUtc,
                            ticks,
                            len,
                            recentCutoff,
                            rollupCutoffDate,
                            keepActiveMessages: lastWriteUtc >= activeSessionCutoffUtc,
                            previous: cached);
                        fileCache[file] = updated;
                        if (updated.Turns.Count > 0)
                        {
                            recentTurns.AddRange(updated.Turns);
                        }
                        continue;
                    }

                    CachedClaudeFileSummary fresh = ReadOrTailClaudeJsonlFile(
                        file,
                        lastWriteUtc,
                        ticks,
                        len,
                        recentCutoff,
                        rollupCutoffDate,
                        keepActiveMessages: fileCache is not null && lastWriteUtc >= activeSessionCutoffUtc,
                        previous: null);
                    if (fileCache is not null)
                    {
                        fileCache[file] = fresh;
                    }
                    if (fresh.Turns.Count > 0)
                    {
                        recentTurns.AddRange(fresh.Turns);
                    }
                }

                if (fileCache is not null && seenActiveFiles is not null)
                {
                    List<string>? staleKeys = null;
                    foreach (string cachedPath in fileCache.Keys)
                    {
                        if (!seenActiveFiles.Contains(cachedPath))
                        {
                            (staleKeys ??= []).Add(cachedPath);
                        }
                    }

                    if (staleKeys is not null)
                    {
                        foreach (string staleKey in staleKeys)
                        {
                            fileCache.Remove(staleKey);
                        }
                    }
                }
            }
            catch
            {
                // Ignore transient directory enumeration errors.
            }
        }
        else
        {
            fileCache?.Clear();
        }

        bool hasData = enabled && fileCount > 0;
        (double blockCeilingUsd, double weeklyCeilingUsd, string tierBadge) = (planTier?.ToLowerInvariant()) switch
        {
            "max20x" => (320.0, 2200.0, "Max 20x"),
            "max5x" => (85.0, 600.0, "Max 5x"),
            _ => (18.0, 130.0, "Pro")
        };

        recentTurns.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // 1. Compute 5-hour active block (ccusage sliding-discrete 5h block model)
        DateTimeOffset? blockStart = null;
        double blockCost = 0;
        foreach (ClaudeTurnSample t in recentTurns)
        {
            if (t.Timestamp < recentCutoff)
            {
                continue;
            }

            if (blockStart is null || t.Timestamp >= blockStart.Value.AddHours(5))
            {
                blockStart = t.Timestamp;
                blockCost = t.CostUsd;
            }
            else
            {
                blockCost += t.CostUsd;
            }
        }

        string label5h = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window5hLabel");
        CustomWidgetAiWindowQuota primaryWindow;
        bool paceHot = false;
        if (blockStart is not null && now < blockStart.Value.AddHours(5))
        {
            DateTimeOffset resetsAt = blockStart.Value.AddHours(5);
            double usedPct = Math.Clamp(blockCost / blockCeilingUsd * 100.0, 1.0, 100.0);
            paceHot = EvaluateWindowPaceIsHot(usedPct, resetsAt, now, TimeSpan.FromHours(5));
            AiQuotaHealth h = EvaluateWindowHealth(usedPct);
            if (paceHot && h == AiQuotaHealth.Healthy)
            {
                h = AiQuotaHealth.Warning;
            }
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: usedPct,
                ResetsAt: resetsAt,
                ResetText: FormatResetCountdown(resetsAt, now, c),
                Health: h);
        }
        else
        {
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: 0,
                ResetsAt: null,
                ResetText: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.StandbyStartChat"),
                Health: AiQuotaHealth.Healthy);
        }

        // 2. Compute 7-day rolling usage window
        DateTimeOffset weekStart = now.AddDays(-7);
        double weekCost = 0;
        DateTimeOffset? oldestInWeek = null;
        foreach (ClaudeTurnSample t in recentTurns)
        {
            if (t.Timestamp >= weekStart)
            {
                weekCost += t.CostUsd;
                oldestInWeek ??= t.Timestamp;
            }
        }

        DateTimeOffset? weekReset = oldestInWeek?.AddDays(7);
        double weekUsedPct = weekCost > 0
            ? Math.Clamp(weekCost / weeklyCeilingUsd * 100.0, 1.0, 100.0)
            : 0.0;
        string weekCostText = weekCost.ToString("0.00", CultureInfo.InvariantCulture);
        string last7DaysCostLabel = LocalizationService.FormatForCulture(
            c,
            "CustomWidget.AiMonitor.Last7DaysCostFormat",
            weekCostText);
        var secondaryWindow = new CustomWidgetAiWindowQuota(
            Label: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window7dQuotaLabel"),
            UsedPercent: weekUsedPct,
            ResetsAt: weekReset,
            ResetText: weekReset is not null ? FormatResetCountdown(weekReset, now, c) : last7DaysCostLabel,
            Health: EvaluateWindowHealth(weekUsedPct));

        AiQuotaHealth overallHealth = !hasData
            ? AiQuotaHealth.Idle
            : (AiQuotaHealth)Math.Max((int)primaryWindow.Health, (int)secondaryWindow.Health);

        var snapshot = new CustomWidgetAiProviderSnapshot(
            Id: ProviderClaude,
            Name: "Claude Code",
            ShortName: "Claude",
            Monogram: "C",
            BrandHex: "#D97757",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: true,
            IsConfigured: hasData,
            HasLiveData: hasData,
            PlanBadge: tierBadge,
            Health: overallHealth,
            PrimaryWindow: primaryWindow,
            SecondaryWindow: secondaryWindow,
            CurrencySymbol: "$",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: FormattableString.Invariant($"{Math.Round(primaryWindow.RemainingPercent):0}%"),
            RadarSubtext: primaryWindow.ResetsAt is not null
                ? $"{primaryWindow.Label} · {primaryWindow.ResetText}"
                : last7DaysCostLabel,
            ErrorMessage: null,
            UpdatedAt: now,
            IsPaceHot: paceHot);

        return (snapshot, recentTurns, fileCount);
    }

    private static CachedClaudeFileSummary ReadOrTailClaudeJsonlFile(
        string filePath,
        DateTime fallbackUtc,
        long ticks,
        long length,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate,
        bool keepActiveMessages,
        CachedClaudeFileSummary? previous)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long actualLength = fs.Length;

            if (previous is not null &&
                previous.ParsedLength > 0 &&
                actualLength > previous.ParsedLength &&
                TryVerifyPrefixFingerprint(fs, previous.ParsedLength, previous.PrefixFingerprint))
            {
                fs.Seek(previous.ParsedLength, SeekOrigin.Begin);
                var tailByMsgId = new Dictionary<string, ClaudeTurnSample>(StringComparer.Ordinal);
                var tailAnon = new List<ClaudeTurnSample>();
                (long safeParsedLen, long bytesRead) = ScanClaudeJsonlStream(
                    fs,
                    previous.ParsedLength,
                    fallbackUtc,
                    tailByMsgId,
                    tailAnon);

                ulong newFp = previous.ParsedLength >= PrefixFingerprintMaxBytes
                    ? previous.PrefixFingerprint
                    : ReadPrefixFingerprintFromStart(fs, safeParsedLen);

                return MergeClaudeTailIntoCachedSummary(
                    previous,
                    ticks,
                    length,
                    safeParsedLen,
                    newFp,
                    bytesRead,
                    tailByMsgId,
                    tailAnon,
                    recentCutoff,
                    rollupCutoffDate,
                    keepActiveMessages);
            }

            fs.Seek(0, SeekOrigin.Begin);
            var fullByMsgId = new Dictionary<string, ClaudeTurnSample>(StringComparer.Ordinal);
            var fullAnon = new List<ClaudeTurnSample>();
            (long fullParsedLen, long fullBytesRead) = ScanClaudeJsonlStream(
                fs,
                0,
                fallbackUtc,
                fullByMsgId,
                fullAnon);
            ulong fullFp = ReadPrefixFingerprintFromStart(fs, fullParsedLen);

            return BuildClaudeCachedSummaryFromFullTurns(
                ticks,
                length,
                fullParsedLen,
                fullFp,
                fullBytesRead,
                fullByMsgId,
                fullAnon,
                recentCutoff,
                rollupCutoffDate,
                keepActiveMessages);
        }
        catch
        {
            return previous ?? new CachedClaudeFileSummary(
                LastWriteUtcTicks: -1,
                Length: -1,
                Turns: Array.Empty<ClaudeTurnSample>(),
                ParsedLength: 0,
                PrefixFingerprint: 0,
                DailyRollups: Array.Empty<ClaudeDailyRollup>(),
                LastBytesScanned: 0,
                WasIncrementalTail: false);
        }
    }

    private static CachedClaudeFileSummary BuildClaudeCachedSummaryFromFullTurns(
        long ticks,
        long length,
        long parsedLength,
        ulong prefixFingerprint,
        long bytesScanned,
        Dictionary<string, ClaudeTurnSample> byMessageId,
        List<ClaudeTurnSample> anonymousTurns,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate,
        bool keepActiveMessages)
    {
        var rollupMap = new Dictionary<DateOnly, ClaudeDailyRollup>();
        var recentList = new List<ClaudeTurnSample>();

        void Accumulate(ClaudeTurnSample t)
        {
            DateOnly d = DateOnly.FromDateTime(t.Timestamp.ToLocalTime().DateTime);
            if (d >= rollupCutoffDate)
            {
                if (rollupMap.TryGetValue(d, out ClaudeDailyRollup existing))
                {
                    rollupMap[d] = new ClaudeDailyRollup(
                        d,
                        existing.TotalTokens + t.TotalTokens,
                        existing.CostUsd + t.CostUsd,
                        existing.CacheReadTokens + t.CacheReadTokens,
                        existing.PromptTokens + t.PromptTokens);
                }
                else
                {
                    rollupMap[d] = new ClaudeDailyRollup(
                        d,
                        t.TotalTokens,
                        t.CostUsd,
                        t.CacheReadTokens,
                        t.PromptTokens);
                }
            }

            if (t.Timestamp >= recentCutoff)
            {
                recentList.Add(t);
            }
        }

        foreach (ClaudeTurnSample t in byMessageId.Values)
        {
            Accumulate(t);
        }

        foreach (ClaudeTurnSample t in anonymousTurns)
        {
            Accumulate(t);
        }

        recentList.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        IReadOnlyList<ClaudeTurnSample> compactRecent = recentList.Count == 0
            ? Array.Empty<ClaudeTurnSample>()
            : recentList.ToArray();

        ClaudeDailyRollup[] rollups = rollupMap.Count == 0
            ? []
            : rollupMap.Values.OrderBy(static r => r.Date).ToArray();

        Dictionary<string, ClaudeTurnSample>? activeDict = null;
        if (keepActiveMessages && byMessageId.Count > 0)
        {
            activeDict = PruneActiveMessageDictionary(byMessageId, recentCutoff);
        }

        return new CachedClaudeFileSummary(
            LastWriteUtcTicks: ticks,
            Length: length,
            Turns: compactRecent,
            ParsedLength: parsedLength,
            PrefixFingerprint: prefixFingerprint,
            DailyRollups: rollups,
            LastBytesScanned: bytesScanned,
            WasIncrementalTail: false)
        {
            ActiveMessageTurns = activeDict
        };
    }

    private static CachedClaudeFileSummary MergeClaudeTailIntoCachedSummary(
        CachedClaudeFileSummary previous,
        long ticks,
        long length,
        long parsedLength,
        ulong prefixFingerprint,
        long bytesScanned,
        Dictionary<string, ClaudeTurnSample> tailByMsgId,
        List<ClaudeTurnSample> tailAnon,
        DateTimeOffset recentCutoff,
        DateOnly rollupCutoffDate,
        bool keepActiveMessages)
    {
        if (tailByMsgId.Count == 0 && tailAnon.Count == 0)
        {
            return previous with
            {
                LastWriteUtcTicks = ticks,
                Length = length,
                ParsedLength = parsedLength,
                PrefixFingerprint = prefixFingerprint,
                LastBytesScanned = bytesScanned,
                WasIncrementalTail = true,
                ActiveMessageTurns = keepActiveMessages ? previous.ActiveMessageTurns : null
            };
        }

        var rollupMap = new Dictionary<DateOnly, ClaudeDailyRollup>();
        if (previous.DailyRollups is not null)
        {
            foreach (ClaudeDailyRollup r in previous.DailyRollups)
            {
                if (r.Date >= rollupCutoffDate)
                {
                    rollupMap[r.Date] = r;
                }
            }
        }

        var recentList = new List<ClaudeTurnSample>(previous.Turns.Count + tailByMsgId.Count + tailAnon.Count);
        foreach (ClaudeTurnSample t in previous.Turns)
        {
            if (t.Timestamp >= recentCutoff)
            {
                recentList.Add(t);
            }
        }

        Dictionary<string, ClaudeTurnSample>? activeMap = previous.ActiveMessageTurns is not null
            ? new Dictionary<string, ClaudeTurnSample>(previous.ActiveMessageTurns, StringComparer.Ordinal)
            : (keepActiveMessages ? new Dictionary<string, ClaudeTurnSample>(StringComparer.Ordinal) : null);

        void AddSample(ClaudeTurnSample t)
        {
            DateOnly d = DateOnly.FromDateTime(t.Timestamp.ToLocalTime().DateTime);
            if (d >= rollupCutoffDate)
            {
                if (rollupMap.TryGetValue(d, out ClaudeDailyRollup existing))
                {
                    rollupMap[d] = new ClaudeDailyRollup(
                        d,
                        existing.TotalTokens + t.TotalTokens,
                        existing.CostUsd + t.CostUsd,
                        existing.CacheReadTokens + t.CacheReadTokens,
                        existing.PromptTokens + t.PromptTokens);
                }
                else
                {
                    rollupMap[d] = new ClaudeDailyRollup(
                        d,
                        t.TotalTokens,
                        t.CostUsd,
                        t.CacheReadTokens,
                        t.PromptTokens);
                }
            }

            if (t.Timestamp >= recentCutoff)
            {
                recentList.Add(t);
            }
        }

        void SubtractSample(ClaudeTurnSample prev)
        {
            DateOnly d = DateOnly.FromDateTime(prev.Timestamp.ToLocalTime().DateTime);
            if (rollupMap.TryGetValue(d, out ClaudeDailyRollup existing))
            {
                long nextTok = Math.Max(0, existing.TotalTokens - prev.TotalTokens);
                double nextCost = Math.Max(0.0, existing.CostUsd - prev.CostUsd);
                long nextCacheRead = Math.Max(0, existing.CacheReadTokens - prev.CacheReadTokens);
                long nextPrompt = Math.Max(0, existing.PromptTokens - prev.PromptTokens);
                if (nextTok <= 0 && nextCost <= 0.0)
                {
                    rollupMap.Remove(d);
                }
                else
                {
                    rollupMap[d] = new ClaudeDailyRollup(d, nextTok, nextCost, nextCacheRead, nextPrompt);
                }
            }

            recentList.Remove(prev);
        }

        foreach ((string msgId, ClaudeTurnSample sample) in tailByMsgId)
        {
            if (activeMap is not null && activeMap.TryGetValue(msgId, out ClaudeTurnSample? prev))
            {
                if (sample.OutputTokens < prev.OutputTokens)
                {
                    continue;
                }

                SubtractSample(prev);
            }

            AddSample(sample);
            if (activeMap is not null)
            {
                activeMap[msgId] = sample;
            }
        }

        foreach (ClaudeTurnSample sample in tailAnon)
        {
            AddSample(sample);
        }

        recentList.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        IReadOnlyList<ClaudeTurnSample> compactRecent = recentList.Count == 0
            ? Array.Empty<ClaudeTurnSample>()
            : recentList.ToArray();

        ClaudeDailyRollup[] rollups = rollupMap.Count == 0
            ? []
            : rollupMap.Values.OrderBy(static r => r.Date).ToArray();

        if (!keepActiveMessages)
        {
            activeMap = null;
        }
        else if (activeMap is { Count: > 256 })
        {
            activeMap = PruneActiveMessageDictionary(activeMap, recentCutoff);
        }

        return new CachedClaudeFileSummary(
            LastWriteUtcTicks: ticks,
            Length: length,
            Turns: compactRecent,
            ParsedLength: parsedLength,
            PrefixFingerprint: prefixFingerprint,
            DailyRollups: rollups,
            LastBytesScanned: bytesScanned,
            WasIncrementalTail: true)
        {
            ActiveMessageTurns = activeMap
        };
    }

    private static Dictionary<string, ClaudeTurnSample>? PruneActiveMessageDictionary(
        Dictionary<string, ClaudeTurnSample> source,
        DateTimeOffset recentCutoff)
    {
        var pruned = new Dictionary<string, ClaudeTurnSample>(StringComparer.Ordinal);
        foreach ((string id, ClaudeTurnSample sample) in source)
        {
            if (sample.Timestamp >= recentCutoff)
            {
                pruned[id] = sample;
            }
        }

        return pruned.Count > 0 ? pruned : null;
    }

    private static void RecordClaudeTurnSample(
        Dictionary<string, ClaudeTurnSample> byMessageId,
        List<ClaudeTurnSample> anonymousTurns,
        string msgId,
        ClaudeTurnSample sample)
    {
        if (!string.IsNullOrEmpty(msgId))
        {
            if (!byMessageId.TryGetValue(msgId, out ClaudeTurnSample? prev) ||
                sample.OutputTokens >= prev.OutputTokens)
            {
                byMessageId[msgId] = sample;
            }
        }
        else
        {
            anonymousTurns.Add(sample);
        }
    }

    private static bool IsCompleteJsonObjectUtf8(ReadOnlySpan<byte> rawLine)
    {
        ReadOnlySpan<byte> trimmed = TrimUtf8Line(rawLine);
        if (trimmed.Length < 2 || trimmed[0] != (byte)'{' || trimmed[^1] != (byte)'}')
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(trimmed);
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject && reader.TrySkip();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseClaudeLineUtf8(
        ReadOnlySpan<byte> rawLine,
        DateTime fallbackUtc,
        out string msgId,
        out ClaudeTurnSample? sample)
    {
        msgId = string.Empty;
        sample = null;

        ReadOnlySpan<byte> line = TrimUtf8Line(rawLine);
        if (line.Length < 20 || line.IndexOf("\"usage\""u8) < 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            DateTimeOffset ts = fallbackUtc;
            string model = "claude-sonnet";
            long inputTok = 0;
            long outputTok = 0;
            long cacheWriteTok = 0;
            long cacheReadTok = 0;
            bool hasUsage = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("type"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.String &&
                        !reader.ValueTextEquals("assistant"u8))
                    {
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("timestamp"u8))
                {
                    if (reader.Read() &&
                        reader.TokenType == JsonTokenType.String &&
                        DateTimeOffset.TryParse(
                            reader.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out DateTimeOffset parsedTs))
                    {
                        ts = parsedTs;
                    }
                }
                else if (reader.ValueTextEquals("message"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.TrySkip();
                        return false;
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            continue;
                        }

                        if (reader.ValueTextEquals("model"u8))
                        {
                            if (reader.Read() && reader.TokenType == JsonTokenType.String)
                            {
                                model = reader.GetString() ?? "claude-sonnet";
                            }
                            else
                            {
                                reader.TrySkip();
                            }
                        }
                        else if (reader.ValueTextEquals("id"u8))
                        {
                            if (reader.Read() && reader.TokenType == JsonTokenType.String)
                            {
                                msgId = reader.GetString() ?? string.Empty;
                            }
                            else
                            {
                                reader.TrySkip();
                            }
                        }
                        else if (reader.ValueTextEquals("usage"u8))
                        {
                            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                            {
                                hasUsage = true;
                                ReadClaudeUsageObject(
                                    ref reader,
                                    out inputTok,
                                    out outputTok,
                                    out cacheWriteTok,
                                    out cacheReadTok);
                            }
                            else
                            {
                                reader.TrySkip();
                            }
                        }
                        else
                        {
                            if (!reader.Read())
                            {
                                return false;
                            }

                            reader.TrySkip();
                        }
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reader.TrySkip();
                }
            }

            if (!hasUsage || inputTok + outputTok + cacheWriteTok + cacheReadTok <= 0)
            {
                return false;
            }

            double cost = EstimateClaudeTurnCostUsd(model, inputTok, outputTok, cacheWriteTok, cacheReadTok);
            sample = new ClaudeTurnSample(ts, model, inputTok, outputTok, cacheWriteTok, cacheReadTok, cost);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (long SafeParsedLength, long TotalBytesRead) ScanClaudeJsonlStream(
        Stream stream,
        long startOffset,
        DateTime fallbackUtc,
        Dictionary<string, ClaudeTurnSample> byMessageId,
        List<ClaudeTurnSample> anonymousTurns)
    {
        const int IoBufferBytes = 16 * 1024;
        const int MaxInlineBytes = 64 * 1024;

        byte[] ioBuf = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        byte[] lineBuf = ArrayPool<byte>.Shared.Rent(MaxInlineBytes);
        ClaudeChunkedLineParser megaParser = default;

        try
        {
            int lineLen = 0;
            bool overflowed = false;
            long consumedBytes = 0;
            long currentLineBytes = 0;
            long totalBytesRead = 0;

            while (true)
            {
                int bytesRead = stream.Read(ioBuf, 0, IoBufferBytes);
                if (bytesRead <= 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                ReadOnlySpan<byte> chunk = ioBuf.AsSpan(0, bytesRead);
                while (!chunk.IsEmpty)
                {
                    int nl = chunk.IndexOf((byte)'\n');
                    ReadOnlySpan<byte> segment = nl >= 0 ? chunk[..nl] : chunk;

                    if (!overflowed)
                    {
                        if (lineLen + segment.Length <= MaxInlineBytes)
                        {
                            segment.CopyTo(lineBuf.AsSpan(lineLen));
                            lineLen += segment.Length;
                        }
                        else
                        {
                            overflowed = true;
                            megaParser.ResetForNewLine();
                            if (lineLen > 0)
                            {
                                megaParser.Feed(lineBuf.AsSpan(0, lineLen), isFinalBlock: false);
                            }
                            megaParser.Feed(segment, isFinalBlock: nl >= 0);
                        }
                    }
                    else
                    {
                        megaParser.Feed(segment, isFinalBlock: nl >= 0);
                    }

                    if (nl >= 0)
                    {
                        if (!overflowed)
                        {
                            if (lineLen > 0 &&
                                TryParseClaudeLineUtf8(lineBuf.AsSpan(0, lineLen), fallbackUtc, out string msgId, out ClaudeTurnSample? sample) &&
                                sample is not null)
                            {
                                RecordClaudeTurnSample(byMessageId, anonymousTurns, msgId, sample);
                            }
                        }
                        else if (megaParser.TryFinish(fallbackUtc, out string msgId, out ClaudeTurnSample? sample) &&
                                 sample is not null)
                        {
                            RecordClaudeTurnSample(byMessageId, anonymousTurns, msgId, sample);
                        }

                        consumedBytes += currentLineBytes + nl + 1;
                        currentLineBytes = 0;
                        lineLen = 0;
                        overflowed = false;
                        chunk = chunk[(nl + 1)..];
                    }
                    else
                    {
                        currentLineBytes += segment.Length;
                        break;
                    }
                }
            }

            if (currentLineBytes > 0)
            {
                if (!overflowed && lineLen > 0)
                {
                    if (IsCompleteJsonObjectUtf8(lineBuf.AsSpan(0, lineLen)))
                    {
                        if (TryParseClaudeLineUtf8(lineBuf.AsSpan(0, lineLen), fallbackUtc, out string msgId, out ClaudeTurnSample? sample) &&
                            sample is not null)
                        {
                            RecordClaudeTurnSample(byMessageId, anonymousTurns, msgId, sample);
                        }

                        consumedBytes += currentLineBytes;
                    }
                }
                else if (overflowed)
                {
                    megaParser.Feed(ReadOnlySpan<byte>.Empty, isFinalBlock: true);
                    if (megaParser.HasCompletedRootObject)
                    {
                        if (megaParser.TryFinish(fallbackUtc, out string msgId, out ClaudeTurnSample? sample) &&
                            sample is not null)
                        {
                            RecordClaudeTurnSample(byMessageId, anonymousTurns, msgId, sample);
                        }

                        consumedBytes += currentLineBytes;
                    }
                }
            }

            return (startOffset + consumedBytes, totalBytesRead);
        }
        finally
        {
            megaParser.Dispose();
            ArrayPool<byte>.Shared.Return(lineBuf);
            ArrayPool<byte>.Shared.Return(ioBuf);
        }
    }

    /// <summary>
    /// Standard chunked <see cref="Utf8JsonReader"/> + <see cref="JsonReaderState"/> parser for &gt; 64 KB Claude JSONL lines,
    /// backed by a pooled <see cref="ArrayPool{T}.Shared"/> carry buffer.
    /// </summary>
    private struct ClaudeChunkedLineParser
    {
        private const int MaxCarryBytes = 32 * 1024 * 1024;

        private enum PendingProp : byte
        {
            None,
            RootType,
            RootTimestamp,
            RootMessage,
            MsgId,
            MsgModel,
            MsgUsage,
            UsageInput,
            UsageOutput,
            UsageCacheWrite,
            UsageCacheRead
        }

        private byte[]? _carryBuf;
        private int _carryLen;
        private JsonReaderState _state;
        private PendingProp _pending;
        private bool _started;
        private bool _rejected;
        private bool _finishedRoot;
        private bool _inMessage;
        private bool _inUsage;
        private bool _hasUsage;
        private DateTimeOffset? _ts;
        private string? _msgId;
        private string? _model;
        private long _inputTok;
        private long _outputTok;
        private long _cacheWriteTok;
        private long _cacheReadTok;

        public readonly bool HasCompletedRootObject => _finishedRoot && !_rejected;

        public void ResetForNewLine()
        {
            _carryLen = 0;
            _state = default;
            _pending = PendingProp.None;
            _started = false;
            _rejected = false;
            _finishedRoot = false;
            _inMessage = false;
            _inUsage = false;
            _hasUsage = false;
            _ts = null;
            _msgId = null;
            _model = null;
            _inputTok = 0;
            _outputTok = 0;
            _cacheWriteTok = 0;
            _cacheReadTok = 0;
        }

        public void Feed(ReadOnlySpan<byte> chunk, bool isFinalBlock)
        {
            if (_rejected || _finishedRoot || (chunk.IsEmpty && !isFinalBlock))
            {
                return;
            }

            if (!chunk.IsEmpty)
            {
                long required = (long)_carryLen + chunk.Length;
                if (required > MaxCarryBytes)
                {
                    _rejected = true;
                    return;
                }

                if (_carryBuf is null)
                {
                    _carryBuf = ArrayPool<byte>.Shared.Rent(Math.Max(128 * 1024, (int)required));
                }
                else if (required > _carryBuf.Length)
                {
                    int nextSize = (int)Math.Min(MaxCarryBytes, Math.Max((long)_carryBuf.Length * 2, required));
                    byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
                    if (_carryLen > 0)
                    {
                        _carryBuf.AsSpan(0, _carryLen).CopyTo(grown);
                    }
                    ArrayPool<byte>.Shared.Return(_carryBuf);
                    _carryBuf = grown;
                }

                chunk.CopyTo(_carryBuf.AsSpan(_carryLen));
                _carryLen += chunk.Length;
            }

            if (_carryBuf is null || _carryLen == 0)
            {
                return;
            }

            int startIdx = 0;
            if (!_started && _carryLen >= 3 && _carryBuf[0] == 0xEF && _carryBuf[1] == 0xBB && _carryBuf[2] == 0xBF)
            {
                startIdx = 3;
            }

            try
            {
                var reader = new Utf8JsonReader(_carryBuf.AsSpan(startIdx, _carryLen - startIdx), isFinalBlock, _state);
                while (reader.Read())
                {
                    int depth = reader.CurrentDepth;
                    if (!_started)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            _rejected = true;
                            return;
                        }
                        _started = true;
                        continue;
                    }

                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            _pending = PendingProp.None;
                            if (depth == 1)
                            {
                                if (reader.ValueTextEquals("type"u8)) _pending = PendingProp.RootType;
                                else if (reader.ValueTextEquals("timestamp"u8)) _pending = PendingProp.RootTimestamp;
                                else if (reader.ValueTextEquals("message"u8)) _pending = PendingProp.RootMessage;
                            }
                            else if (_inMessage && depth == 2)
                            {
                                if (reader.ValueTextEquals("id"u8)) _pending = PendingProp.MsgId;
                                else if (reader.ValueTextEquals("model"u8)) _pending = PendingProp.MsgModel;
                                else if (reader.ValueTextEquals("usage"u8)) _pending = PendingProp.MsgUsage;
                            }
                            else if (_inUsage && depth == 3)
                            {
                                if (reader.ValueTextEquals("input_tokens"u8)) _pending = PendingProp.UsageInput;
                                else if (reader.ValueTextEquals("output_tokens"u8)) _pending = PendingProp.UsageOutput;
                                else if (reader.ValueTextEquals("cache_creation_input_tokens"u8)) _pending = PendingProp.UsageCacheWrite;
                                else if (reader.ValueTextEquals("cache_read_input_tokens"u8)) _pending = PendingProp.UsageCacheRead;
                            }
                            break;

                        case JsonTokenType.StartObject:
                            if (depth == 1 && _pending == PendingProp.RootMessage)
                            {
                                _inMessage = true;
                            }
                            else if (_inMessage && depth == 2 && _pending == PendingProp.MsgUsage)
                            {
                                _inUsage = true;
                                _hasUsage = true;
                            }
                            _pending = PendingProp.None;
                            break;

                        case JsonTokenType.EndObject:
                            _pending = PendingProp.None;
                            if (_inUsage && depth == 2)
                            {
                                _inUsage = false;
                            }
                            else if (_inMessage && depth == 1)
                            {
                                _inMessage = false;
                            }
                            else if (depth == 0)
                            {
                                _finishedRoot = true;
                            }
                            break;

                        case JsonTokenType.String:
                            switch (_pending)
                            {
                                case PendingProp.RootType:
                                    if (!reader.ValueTextEquals("assistant"u8))
                                    {
                                        _rejected = true;
                                        return;
                                    }
                                    break;
                                case PendingProp.RootTimestamp:
                                    if (DateTimeOffset.TryParse(
                                            reader.GetString(),
                                            CultureInfo.InvariantCulture,
                                            DateTimeStyles.AssumeUniversal,
                                            out DateTimeOffset dto))
                                    {
                                        _ts = dto;
                                    }
                                    break;
                                case PendingProp.MsgId:
                                    _msgId = reader.GetString();
                                    break;
                                case PendingProp.MsgModel:
                                    _model = reader.GetString();
                                    break;
                                case PendingProp.UsageInput:
                                    if (long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long si)) _inputTok = si;
                                    break;
                                case PendingProp.UsageOutput:
                                    if (long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long so)) _outputTok = so;
                                    break;
                                case PendingProp.UsageCacheWrite:
                                    if (long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long scw)) _cacheWriteTok = scw;
                                    break;
                                case PendingProp.UsageCacheRead:
                                    if (long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long scr)) _cacheReadTok = scr;
                                    break;
                            }
                            _pending = PendingProp.None;
                            break;

                        case JsonTokenType.Number:
                            switch (_pending)
                            {
                                case PendingProp.UsageInput:
                                    if (reader.TryGetInt64(out long ni)) _inputTok = ni;
                                    break;
                                case PendingProp.UsageOutput:
                                    if (reader.TryGetInt64(out long no)) _outputTok = no;
                                    break;
                                case PendingProp.UsageCacheWrite:
                                    if (reader.TryGetInt64(out long ncw)) _cacheWriteTok = ncw;
                                    break;
                                case PendingProp.UsageCacheRead:
                                    if (reader.TryGetInt64(out long ncr)) _cacheReadTok = ncr;
                                    break;
                            }
                            _pending = PendingProp.None;
                            break;

                        default:
                            _pending = PendingProp.None;
                            break;
                    }

                    if (_finishedRoot)
                    {
                        return;
                    }
                }

                _state = reader.CurrentState;
                int totalConsumed = startIdx + (int)reader.BytesConsumed;
                if (totalConsumed > 0)
                {
                    int remaining = _carryLen - totalConsumed;
                    if (remaining > 0)
                    {
                        _carryBuf.AsSpan(totalConsumed, remaining).CopyTo(_carryBuf);
                    }
                    _carryLen = remaining;
                }
            }
            catch (JsonException)
            {
                _rejected = true;
            }
        }

        public readonly bool TryFinish(DateTime fallbackUtc, out string msgId, out ClaudeTurnSample? sample)
        {
            msgId = string.Empty;
            sample = null;

            if (_rejected || !_hasUsage || _inputTok + _outputTok + _cacheWriteTok + _cacheReadTok <= 0)
            {
                return false;
            }

            string model = string.IsNullOrEmpty(_model) ? "claude-sonnet" : _model;
            msgId = _msgId ?? string.Empty;
            DateTimeOffset ts = _ts ?? fallbackUtc;
            double cost = EstimateClaudeTurnCostUsd(model, _inputTok, _outputTok, _cacheWriteTok, _cacheReadTok);
            sample = new ClaudeTurnSample(ts, model, _inputTok, _outputTok, _cacheWriteTok, _cacheReadTok, cost);
            return true;
        }

        public void Dispose()
        {
            if (_carryBuf is not null)
            {
                ArrayPool<byte>.Shared.Return(_carryBuf);
                _carryBuf = null;
            }
            _carryLen = 0;
        }
    }

    private static void ReadClaudeUsageObject(
        ref Utf8JsonReader reader,
        out long inputTok,
        out long outputTok,
        out long cacheWriteTok,
        out long cacheReadTok)
    {
        inputTok = 0;
        outputTok = 0;
        cacheWriteTok = 0;
        cacheReadTok = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("input_tokens"u8))
            {
                inputTok = ReadLongFromUtf8Reader(ref reader);
            }
            else if (reader.ValueTextEquals("output_tokens"u8))
            {
                outputTok = ReadLongFromUtf8Reader(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_tokens"u8))
            {
                cacheWriteTok = ReadLongFromUtf8Reader(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_input_tokens"u8))
            {
                cacheReadTok = ReadLongFromUtf8Reader(ref reader);
            }
            else if (reader.Read())
            {
                reader.TrySkip();
            }
        }
    }

    public static double EstimateClaudeTurnCostUsd(
        string model,
        long inputTokens,
        long outputTokens,
        long cacheWriteTokens,
        long cacheReadTokens)
    {
        string m = model.ToLowerInvariant();
        double inRate;
        double outRate;
        double cwRate;
        double crRate;

        if (m.Contains("opus-4-5", StringComparison.Ordinal) ||
            m.Contains("opus-4-6", StringComparison.Ordinal) ||
            m.Contains("opus-5", StringComparison.Ordinal))
        {
            inRate = 5.0;
            outRate = 25.0;
            cwRate = 6.25;
            crRate = 0.50;
        }
        else if (m.Contains("opus", StringComparison.Ordinal))
        {
            inRate = 15.0;
            outRate = 75.0;
            cwRate = 18.75;
            crRate = 1.50;
        }
        else if (m.Contains("haiku-3-5", StringComparison.Ordinal) ||
                 m.Contains("3-5-haiku", StringComparison.Ordinal))
        {
            inRate = 0.80;
            outRate = 4.0;
            cwRate = 1.00;
            crRate = 0.08;
        }
        else if (m.Contains("haiku", StringComparison.Ordinal))
        {
            inRate = 1.0;
            outRate = 5.0;
            cwRate = 1.25;
            crRate = 0.10;
        }
        else
        {
            inRate = 3.0;
            outRate = 15.0;
            cwRate = 3.75;
            crRate = 0.30;
        }

        return (inputTokens * inRate +
                outputTokens * outRate +
                cacheWriteTokens * cwRate +
                cacheReadTokens * crRate) / 1_000_000.0;
    }

    public static (CustomWidgetAiProviderSnapshot Provider, IReadOnlyList<CachedCodexFileSummary> Sessions, int FileCount)
        AnalyzeCodexDirectory(
            string? codexSessionsDir,
            bool enabled,
            DateTimeOffset now,
            IDictionary<string, CachedCodexFileSummary>? fileCache = null,
            string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var sessions = new List<CachedCodexFileSummary>();
        int fileCount = 0;

        if (enabled && !string.IsNullOrWhiteSpace(codexSessionsDir) && Directory.Exists(codexSessionsDir))
        {
            DateTime cutoffUtc = now.UtcDateTime.AddDays(-32);
            HashSet<string>? seenActiveFiles = fileCache is { Count: > 0 }
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;
            try
            {
                var rootDir = new DirectoryInfo(codexSessionsDir);
                foreach (FileInfo fi in rootDir.EnumerateFiles("*.jsonl", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    fileCount++;
                    string file = fi.FullName;
                    DateTime lastWriteUtc = fi.LastWriteTimeUtc;
                    if (lastWriteUtc < cutoffUtc)
                    {
                        fileCache?.Remove(file);
                        continue;
                    }

                    seenActiveFiles?.Add(file);
                    long ticks = lastWriteUtc.Ticks;
                    long len = fi.Length;
                    if (fileCache is not null &&
                        fileCache.TryGetValue(file, out CachedCodexFileSummary? cached))
                    {
                        if (cached.LastWriteUtcTicks == ticks && cached.Length == len)
                        {
                            sessions.Add(cached);
                            continue;
                        }

                        CachedCodexFileSummary updated = ParseOrTailCodexJsonlFile(
                            file,
                            lastWriteUtc,
                            ticks,
                            len,
                            previous: cached);
                        fileCache[file] = updated;
                        sessions.Add(updated);
                        continue;
                    }

                    CachedCodexFileSummary parsed = ParseOrTailCodexJsonlFile(
                        file,
                        lastWriteUtc,
                        ticks,
                        len,
                        previous: null);
                    if (fileCache is not null)
                    {
                        fileCache[file] = parsed;
                    }
                    sessions.Add(parsed);
                }

                if (fileCache is not null && seenActiveFiles is not null)
                {
                    List<string>? staleKeys = null;
                    foreach (string cachedPath in fileCache.Keys)
                    {
                        if (!seenActiveFiles.Contains(cachedPath))
                        {
                            (staleKeys ??= []).Add(cachedPath);
                        }
                    }

                    if (staleKeys is not null)
                    {
                        foreach (string staleKey in staleKeys)
                        {
                            fileCache.Remove(staleKey);
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
        CodexRateLimitEvent? newestLimit = sessions
            .Where(static s => s.LatestRateLimit is not null)
            .Select(static s => s.LatestRateLimit!)
            .OrderByDescending(static r => r.Timestamp)
            .FirstOrDefault();

        string planBadge = string.IsNullOrWhiteSpace(newestLimit?.PlanType)
            ? "Plus"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(newestLimit.PlanType.ToLowerInvariant());

        string label5h = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window5hLabel");
        string label7d = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window7dAllowanceLabel");
        string standbyPrimaryText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.StandbyStartChat");
        string standbySecondaryText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.QuotaHealthy");

        CustomWidgetAiWindowQuota primaryWindow;
        bool paceHot = false;
        if (newestLimit?.PrimaryUsedPercent is { } pUsed &&
            (newestLimit.PrimaryResetsAt is null || newestLimit.PrimaryResetsAt.Value > now))
        {
            double clamped = Math.Clamp(pUsed, 0.0, 100.0);
            paceHot = EvaluateWindowPaceIsHot(clamped, newestLimit.PrimaryResetsAt, now, TimeSpan.FromHours(5));
            AiQuotaHealth h = EvaluateWindowHealth(clamped);
            if (paceHot && h == AiQuotaHealth.Healthy)
            {
                h = AiQuotaHealth.Warning;
            }
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: clamped,
                ResetsAt: newestLimit.PrimaryResetsAt,
                ResetText: FormatResetCountdown(newestLimit.PrimaryResetsAt, now, c),
                Health: h);
        }
        else
        {
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: 0,
                ResetsAt: null,
                ResetText: standbyPrimaryText,
                Health: AiQuotaHealth.Healthy);
        }

        CustomWidgetAiWindowQuota secondaryWindow;
        if (newestLimit?.SecondaryUsedPercent is { } sUsed &&
            (newestLimit.SecondaryResetsAt is null || newestLimit.SecondaryResetsAt.Value > now))
        {
            double clamped = Math.Clamp(sUsed, 0.0, 100.0);
            secondaryWindow = new CustomWidgetAiWindowQuota(
                Label: label7d,
                UsedPercent: clamped,
                ResetsAt: newestLimit.SecondaryResetsAt,
                ResetText: FormatResetCountdown(newestLimit.SecondaryResetsAt, now, c),
                Health: EvaluateWindowHealth(clamped));
        }
        else
        {
            secondaryWindow = new CustomWidgetAiWindowQuota(
                Label: label7d,
                UsedPercent: 0,
                ResetsAt: null,
                ResetText: standbySecondaryText,
                Health: AiQuotaHealth.Healthy);
        }

        AiQuotaHealth overallHealth = !hasData
            ? AiQuotaHealth.Idle
            : (AiQuotaHealth)Math.Max((int)primaryWindow.Health, (int)secondaryWindow.Health);

        var snapshot = new CustomWidgetAiProviderSnapshot(
            Id: ProviderCodex,
            Name: "OpenAI Codex",
            ShortName: "Codex",
            Monogram: "Cx",
            BrandHex: "#10A37F",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: true,
            IsConfigured: hasData,
            HasLiveData: hasData,
            PlanBadge: planBadge,
            Health: overallHealth,
            PrimaryWindow: primaryWindow,
            SecondaryWindow: secondaryWindow,
            CurrencySymbol: "$",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: FormattableString.Invariant($"{Math.Round(primaryWindow.RemainingPercent):0}%"),
            RadarSubtext: primaryWindow.ResetsAt is not null
                ? $"{primaryWindow.Label} · {primaryWindow.ResetText}"
                : LocalizationService.FormatForCulture(
                    c,
                    "CustomWidget.AiMonitor.WeeklyLeftFormat",
                    Math.Round(secondaryWindow.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)),
            ErrorMessage: null,
            UpdatedAt: now,
            IsPaceHot: paceHot);

        return (snapshot, sessions, fileCount);
    }

    private static CachedCodexFileSummary ParseOrTailCodexJsonlFile(
        string filePath,
        DateTime fallbackUtc,
        long ticks,
        long length,
        CachedCodexFileSummary? previous)
    {
        DateTimeOffset sessionTs = fallbackUtc;
        long maxIn = 0;
        long maxCachedIn = 0;
        long maxOut = 0;
        long prevIn = 0;
        long prevCached = 0;
        long prevOut = 0;
        var dailyRollups = new Dictionary<DateOnly, CodexDailyRollup>();
        CodexRateLimitEvent? latestLimit = null;
        long safeParsedLen = 0;
        ulong prefixFp = 0;
        long bytesScanned = 0;
        bool wasIncremental = false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long actualLength = fs.Length;

            if (previous is not null &&
                previous.ParsedLength > 0 &&
                actualLength > previous.ParsedLength &&
                TryVerifyPrefixFingerprint(fs, previous.ParsedLength, previous.PrefixFingerprint))
            {
                wasIncremental = true;
                sessionTs = previous.SessionTimestamp;
                maxIn = previous.RawMaxInputTokens;
                maxCachedIn = previous.CachedInputTokens;
                maxOut = previous.OutputTokens;
                prevIn = previous.LastInputTokens;
                prevCached = previous.LastCachedInputTokens;
                prevOut = previous.LastOutputTokens;
                if (previous.DailyRollups is { Count: > 0 })
                {
                    foreach ((DateOnly d, CodexDailyRollup r) in previous.DailyRollups)
                    {
                        dailyRollups[d] = r;
                    }
                }
                latestLimit = previous.LatestRateLimit;

                fs.Seek(previous.ParsedLength, SeekOrigin.Begin);
                (safeParsedLen, bytesScanned) = ScanJsonlUtf8Stream(
                    fs,
                    previous.ParsedLength,
                    lineSpan =>
                    {
                        TryParseCodexLineUtf8(
                            lineSpan,
                            fallbackUtc,
                            ref sessionTs,
                            ref maxIn,
                            ref maxCachedIn,
                            ref maxOut,
                            ref prevIn,
                            ref prevCached,
                            ref prevOut,
                            dailyRollups,
                            ref latestLimit);
                    });

                prefixFp = previous.ParsedLength >= PrefixFingerprintMaxBytes
                    ? previous.PrefixFingerprint
                    : ReadPrefixFingerprintFromStart(fs, safeParsedLen);
            }
            else
            {
                fs.Seek(0, SeekOrigin.Begin);
                (safeParsedLen, bytesScanned) = ScanJsonlUtf8Stream(
                    fs,
                    0,
                    lineSpan =>
                    {
                        TryParseCodexLineUtf8(
                            lineSpan,
                            fallbackUtc,
                            ref sessionTs,
                            ref maxIn,
                            ref maxCachedIn,
                            ref maxOut,
                            ref prevIn,
                            ref prevCached,
                            ref prevOut,
                            dailyRollups,
                            ref latestLimit);
                    });

                prefixFp = ReadPrefixFingerprintFromStart(fs, safeParsedLen);
            }
        }
        catch
        {
            return previous ?? new CachedCodexFileSummary(
                LastWriteUtcTicks: -1,
                Length: -1,
                SessionTimestamp: fallbackUtc,
                InputTokens: 0,
                CachedInputTokens: 0,
                OutputTokens: 0,
                CostUsd: 0,
                LatestRateLimit: null,
                ParsedLength: 0,
                PrefixFingerprint: 0,
                RawMaxInputTokens: 0,
                LastBytesScanned: 0,
                WasIncrementalTail: false,
                DailyRollups: new Dictionary<DateOnly, CodexDailyRollup>(),
                LastInputTokens: 0,
                LastCachedInputTokens: 0,
                LastOutputTokens: 0);
        }

        long uncachedIn = Math.Max(0, maxIn - maxCachedIn);
        // GPT-5 / Codex standard API equivalent (~$1.75/1M in, $0.175/1M cached, $14/1M out)
        double costUsd = (uncachedIn * 1.75 + maxCachedIn * 0.175 + maxOut * 14.0) / 1_000_000.0;

        return new CachedCodexFileSummary(
            LastWriteUtcTicks: ticks,
            Length: length,
            SessionTimestamp: sessionTs,
            InputTokens: uncachedIn,
            CachedInputTokens: maxCachedIn,
            OutputTokens: maxOut,
            CostUsd: costUsd,
            LatestRateLimit: latestLimit,
            ParsedLength: safeParsedLen,
            PrefixFingerprint: prefixFp,
            RawMaxInputTokens: maxIn,
            LastBytesScanned: bytesScanned,
            WasIncrementalTail: wasIncremental,
            DailyRollups: dailyRollups,
            LastInputTokens: prevIn,
            LastCachedInputTokens: prevCached,
            LastOutputTokens: prevOut);
    }

    internal delegate void JsonlSpanLineHandler(ReadOnlySpan<byte> lineSpan);

    /// <summary>
    /// Streams a UTF-8 JSONL file using pooled <see cref="ArrayPool{T}.Shared"/> buffers (16 KB I/O + 64 KB initial line buffer,
    /// growing on demand up to 8 MB) without allocating LOH strings via <see cref="StreamReader.ReadLine()"/> or DOM trees via <see cref="JsonDocument"/>.
    /// </summary>
    internal static (long SafeParsedLength, long TotalBytesRead) ScanJsonlUtf8Stream(
        Stream stream,
        long startOffset,
        JsonlSpanLineHandler onLine)
    {
        const int IoBufferBytes = 16 * 1024;
        const int InitialInlineBytes = 64 * 1024;
        const int MaxOverflowLineBytes = 8 * 1024 * 1024;

        byte[] ioBuf = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        byte[] lineBuf = ArrayPool<byte>.Shared.Rent(InitialInlineBytes);
        try
        {
            int lineLen = 0;
            bool overflowed = false;
            long consumedBytes = 0;
            long currentLineBytes = 0;
            long totalBytesRead = 0;
            byte lastNonWsOverflowByte = 0;

            while (true)
            {
                int bytesRead = stream.Read(ioBuf, 0, IoBufferBytes);
                if (bytesRead <= 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                ReadOnlySpan<byte> chunk = ioBuf.AsSpan(0, bytesRead);
                while (!chunk.IsEmpty)
                {
                    int nl = chunk.IndexOf((byte)'\n');
                    ReadOnlySpan<byte> segment = nl >= 0 ? chunk[..nl] : chunk;

                    if (!overflowed)
                    {
                        long required = (long)lineLen + segment.Length;
                        if (required <= MaxOverflowLineBytes)
                        {
                            if (required > lineBuf.Length)
                            {
                                int nextCapacity = lineBuf.Length;
                                while (nextCapacity < required && nextCapacity < MaxOverflowLineBytes)
                                {
                                    nextCapacity = (int)Math.Min((long)nextCapacity * 2, MaxOverflowLineBytes);
                                }

                                byte[] grown = ArrayPool<byte>.Shared.Rent(nextCapacity);
                                if (lineLen > 0)
                                {
                                    lineBuf.AsSpan(0, lineLen).CopyTo(grown);
                                }
                                ArrayPool<byte>.Shared.Return(lineBuf);
                                lineBuf = grown;
                            }

                            segment.CopyTo(lineBuf.AsSpan(lineLen));
                            lineLen += segment.Length;
                        }
                        else
                        {
                            overflowed = true;
                            if (lineLen > 0)
                            {
                                UpdateTrailingNonWhitespaceByte(lineBuf.AsSpan(0, lineLen), ref lastNonWsOverflowByte);
                            }
                            UpdateTrailingNonWhitespaceByte(segment, ref lastNonWsOverflowByte);
                        }
                    }
                    else
                    {
                        UpdateTrailingNonWhitespaceByte(segment, ref lastNonWsOverflowByte);
                    }

                    if (nl >= 0)
                    {
                        if (!overflowed && lineLen > 0)
                        {
                            onLine(lineBuf.AsSpan(0, lineLen));
                        }

                        consumedBytes += currentLineBytes + nl + 1;
                        currentLineBytes = 0;
                        lineLen = 0;
                        overflowed = false;
                        lastNonWsOverflowByte = 0;
                        chunk = chunk[(nl + 1)..];
                    }
                    else
                    {
                        currentLineBytes += segment.Length;
                        break;
                    }
                }
            }

            if (currentLineBytes > 0)
            {
                if (!overflowed && lineLen > 0)
                {
                    if (IsCompleteJsonObjectUtf8(lineBuf.AsSpan(0, lineLen)))
                    {
                        onLine(lineBuf.AsSpan(0, lineLen));
                        consumedBytes += currentLineBytes;
                    }
                }
                else if (overflowed && lastNonWsOverflowByte == (byte)'}')
                {
                    consumedBytes += currentLineBytes;
                }
            }

            return (startOffset + consumedBytes, totalBytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuf);
            ArrayPool<byte>.Shared.Return(ioBuf);
        }
    }

    private static void UpdateTrailingNonWhitespaceByte(ReadOnlySpan<byte> span, ref byte lastNonWs)
    {
        for (int i = span.Length - 1; i >= 0; i--)
        {
            byte b = span[i];
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                lastNonWs = b;
                return;
            }
        }
    }

    private static void TryParseCodexLineUtf8(
        ReadOnlySpan<byte> rawLine,
        DateTime fallbackUtc,
        ref DateTimeOffset sessionTs,
        ref long maxIn,
        ref long maxCachedIn,
        ref long maxOut,
        ref long prevIn,
        ref long prevCached,
        ref long prevOut,
        Dictionary<DateOnly, CodexDailyRollup> dailyRollups,
        ref CodexRateLimitEvent? latestLimit)
    {
        ReadOnlySpan<byte> line = TrimUtf8Line(rawLine);
        if (line.Length < 16 || line.IndexOf("\"token_count\""u8) < 0)
        {
            return;
        }

        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return;
            }

            DateTimeOffset lineTs = fallbackUtc;
            bool hasPayloadObject = false;
            long rootIn = -1, rootCached = -1, rootOut = -1;
            long payloadIn = -1, payloadCached = -1, payloadOut = -1;
            ParsedRateLimitFields rootLimits = default;
            ParsedRateLimitFields payloadLimits = default;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("timestamp"u8))
                {
                    if (reader.Read() &&
                        reader.TokenType == JsonTokenType.String &&
                        DateTimeOffset.TryParse(
                            reader.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out DateTimeOffset parsedTs))
                    {
                        lineTs = parsedTs;
                        sessionTs = parsedTs;
                    }
                }
                else if (reader.ValueTextEquals("payload"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        hasPayloadObject = true;
                        ReadCodexPayloadFields(
                            ref reader,
                            ref payloadIn,
                            ref payloadCached,
                            ref payloadOut,
                            ref payloadLimits);
                    }
                    else
                    {
                        reader.TrySkip();
                    }
                }
                else if (reader.ValueTextEquals("info"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        ReadCodexInfoObject(ref reader, ref rootIn, ref rootCached, ref rootOut);
                    }
                    else
                    {
                        reader.TrySkip();
                    }
                }
                else if (reader.ValueTextEquals("rate_limits"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        ReadCodexRateLimitsObject(ref reader, ref rootLimits);
                    }
                    else
                    {
                        reader.TrySkip();
                    }
                }
                else if (reader.Read())
                {
                    reader.TrySkip();
                }
            }

            long effIn = hasPayloadObject ? payloadIn : rootIn;
            long effCached = hasPayloadObject ? payloadCached : rootCached;
            long effOut = hasPayloadObject ? payloadOut : rootOut;
            if (effIn >= 0)
            {
                long safeCached = Math.Max(0, effCached);
                long safeOut = Math.Max(0, effOut);
                if (effIn >= maxIn) maxIn = effIn;
                if (safeCached >= maxCachedIn) maxCachedIn = safeCached;
                if (safeOut >= maxOut) maxOut = safeOut;

                bool counterReset = effIn < prevIn || safeOut < prevOut;
                long deltaIn = counterReset ? effIn : effIn - prevIn;
                long deltaCached = counterReset ? safeCached : Math.Max(0, safeCached - prevCached);
                long deltaOut = counterReset ? safeOut : safeOut - prevOut;
                long deltaUncached = Math.Max(0, deltaIn - deltaCached);

                prevIn = effIn;
                prevCached = safeCached;
                prevOut = safeOut;

                if (deltaUncached > 0 || deltaCached > 0 || deltaOut > 0)
                {
                    double deltaCost = (deltaUncached * 1.75 + deltaCached * 0.175 + deltaOut * 14.0) / 1_000_000.0;
                    DateOnly lineDate = DateOnly.FromDateTime(lineTs.ToLocalTime().DateTime);
                    if (dailyRollups.TryGetValue(lineDate, out CodexDailyRollup? existing))
                    {
                        dailyRollups[lineDate] = new CodexDailyRollup(
                            lineDate,
                            existing.InputTokens + deltaUncached,
                            existing.CachedInputTokens + deltaCached,
                            existing.OutputTokens + deltaOut,
                            existing.CostUsd + deltaCost);
                    }
                    else
                    {
                        dailyRollups[lineDate] = new CodexDailyRollup(
                            lineDate,
                            deltaUncached,
                            deltaCached,
                            deltaOut,
                            deltaCost);
                    }
                }
            }

            ParsedRateLimitFields lim = hasPayloadObject ? payloadLimits : rootLimits;
            if (lim.HasAny && (lim.PrimPct.HasValue || lim.SecPct.HasValue))
            {
                DateTimeOffset? primReset = ResolveParsedReset(lim.PrimEpoch, lim.PrimInSec, lineTs);
                DateTimeOffset? secReset = ResolveParsedReset(lim.SecEpoch, lim.SecInSec, lineTs);
                latestLimit = new CodexRateLimitEvent(
                    lineTs,
                    lim.Plan ?? "plus",
                    lim.PrimPct,
                    primReset,
                    lim.SecPct,
                    secReset);
            }
        }
        catch
        {
            // Skip malformed JSON line.
        }
    }

    private struct ParsedRateLimitFields
    {
        public bool HasAny;
        public string? Plan;
        public double? PrimPct;
        public long PrimEpoch;
        public double? PrimInSec;
        public double? SecPct;
        public long SecEpoch;
        public double? SecInSec;
    }

    private static void ReadCodexPayloadFields(
        ref Utf8JsonReader reader,
        ref long inp,
        ref long cached,
        ref long outp,
        ref ParsedRateLimitFields limits)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("info"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadCodexInfoObject(ref reader, ref inp, ref cached, ref outp);
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.ValueTextEquals("rate_limits"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadCodexRateLimitsObject(ref reader, ref limits);
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.Read())
            {
                reader.TrySkip();
            }
        }
    }

    private static void ReadCodexInfoObject(
        ref Utf8JsonReader reader,
        ref long inp,
        ref long cached,
        ref long outp)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("total_token_usage"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    inp = 0;
                    cached = 0;
                    outp = 0;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            continue;
                        }

                        if (reader.ValueTextEquals("input_tokens"u8))
                        {
                            inp = ReadLongFromUtf8Reader(ref reader);
                        }
                        else if (reader.ValueTextEquals("cached_input_tokens"u8))
                        {
                            cached = ReadLongFromUtf8Reader(ref reader);
                        }
                        else if (reader.ValueTextEquals("output_tokens"u8))
                        {
                            outp = ReadLongFromUtf8Reader(ref reader);
                        }
                        else if (reader.Read())
                        {
                            reader.TrySkip();
                        }
                    }
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.Read())
            {
                reader.TrySkip();
            }
        }
    }

    private static void ReadCodexRateLimitsObject(
        ref Utf8JsonReader reader,
        ref ParsedRateLimitFields limits)
    {
        limits.HasAny = true;
        limits.Plan ??= "plus";
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("plan_type"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                {
                    limits.Plan = reader.GetString() ?? "plus";
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.ValueTextEquals("primary"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadCodexWindowObject(ref reader, out limits.PrimPct, out limits.PrimEpoch, out limits.PrimInSec);
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.ValueTextEquals("secondary"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadCodexWindowObject(ref reader, out limits.SecPct, out limits.SecEpoch, out limits.SecInSec);
                }
                else
                {
                    reader.TrySkip();
                }
            }
            else if (reader.Read())
            {
                reader.TrySkip();
            }
        }
    }

    private static void ReadCodexWindowObject(
        ref Utf8JsonReader reader,
        out double? usedPct,
        out long resetsAtEpoch,
        out double? resetsInSeconds)
    {
        usedPct = null;
        resetsAtEpoch = 0;
        resetsInSeconds = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("used_percent"u8))
            {
                usedPct = ReadNullableDoubleFromUtf8Reader(ref reader);
            }
            else if (reader.ValueTextEquals("resets_at"u8))
            {
                resetsAtEpoch = ReadLongFromUtf8Reader(ref reader);
            }
            else if (reader.ValueTextEquals("resets_in_seconds"u8))
            {
                resetsInSeconds = ReadNullableDoubleFromUtf8Reader(ref reader);
            }
            else if (reader.Read())
            {
                reader.TrySkip();
            }
        }
    }

    private static DateTimeOffset? ResolveParsedReset(long rawEpoch, double? inSec, DateTimeOffset lineTimestamp)
    {
        if (rawEpoch > 0)
        {
            try
            {
                return rawEpoch > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(rawEpoch)
                    : DateTimeOffset.FromUnixTimeSeconds(rawEpoch);
            }
            catch
            {
            }
        }

        return inSec.HasValue ? lineTimestamp.AddSeconds(inSec.Value) : null;
    }

    private static ReadOnlySpan<byte> TrimUtf8Line(ReadOnlySpan<byte> span)
    {
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        int start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t' || span[start] == (byte)'\r'))
        {
            start++;
        }

        int end = span.Length;
        while (end > start && (span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t' || span[end - 1] == (byte)'\r'))
        {
            end--;
        }

        return span[start..end];
    }

    private static long ReadLongFromUtf8Reader(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            return 0;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long v))
        {
            return v;
        }

        if (reader.TokenType == JsonTokenType.String &&
            long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sv))
        {
            return sv;
        }

        reader.TrySkip();
        return 0;
    }

    private static double? ReadNullableDoubleFromUtf8Reader(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double d))
        {
            return d;
        }

        if (reader.TokenType == JsonTokenType.String &&
            double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
        {
            return sd;
        }

        reader.TrySkip();
        return null;
    }

    public static CustomWidgetAiLocalSummary BuildLocalSummary(
        IEnumerable<CachedClaudeFileSummary> claudeFileSummaries,
        IReadOnlyList<CachedCodexFileSummary> codexSessions,
        int claudeFiles,
        int codexFiles,
        DateTimeOffset now,
        IReadOnlyList<CachedGeminiFileSummary>? geminiSessions = null,
        int geminiFiles = 0,
        IReadOnlyDictionary<DateOnly, double>? walletDailyUsd = null)
    {
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DateOnly cutoff30 = today.AddDays(-29);
        DateOnly start28 = today.AddDays(-27);

        var dailyMap = new Dictionary<DateOnly, (long Tokens, double Cost)>(28);
        for (int i = 0; i < 28; i++)
        {
            dailyMap[start28.AddDays(i)] = (0, 0);
        }

        long todayTokens = 0;
        double todayCost = 0;
        long todayCacheRead = 0;
        long todayPromptTotal = 0;
        long tokens30d = 0;
        double cost30d = 0;

        foreach (CachedClaudeFileSummary summary in claudeFileSummaries)
        {
            if (summary.DailyRollups is { Count: > 0 } rollups)
            {
                foreach (ClaudeDailyRollup r in rollups)
                {
                    if (r.Date == today)
                    {
                        todayTokens += r.TotalTokens;
                        todayCost += r.CostUsd;
                        todayCacheRead += r.CacheReadTokens;
                        todayPromptTotal += r.PromptTokens;
                    }
                    if (r.Date >= cutoff30)
                    {
                        tokens30d += r.TotalTokens;
                        cost30d += r.CostUsd;
                    }
                    if (dailyMap.TryGetValue(r.Date, out var bucket))
                    {
                        dailyMap[r.Date] = (bucket.Tokens + r.TotalTokens, bucket.Cost + r.CostUsd);
                    }
                }
            }
            else
            {
                foreach (ClaudeTurnSample t in summary.Turns)
                {
                    DateOnly d = DateOnly.FromDateTime(t.Timestamp.ToLocalTime().DateTime);
                    long tot = t.TotalTokens;
                    if (d == today)
                    {
                        todayTokens += tot;
                        todayCost += t.CostUsd;
                        todayCacheRead += t.CacheReadTokens;
                        todayPromptTotal += t.PromptTokens;
                    }
                    if (d >= cutoff30)
                    {
                        tokens30d += tot;
                        cost30d += t.CostUsd;
                    }
                    if (dailyMap.TryGetValue(d, out var bucket))
                    {
                        dailyMap[d] = (bucket.Tokens + tot, bucket.Cost + t.CostUsd);
                    }
                }
            }
        }

        return FinalizeLocalSummary(
            codexSessions,
            claudeFiles,
            codexFiles,
            today,
            cutoff30,
            start28,
            dailyMap,
            todayTokens,
            todayCost,
            todayCacheRead,
            todayPromptTotal,
            tokens30d,
            cost30d,
            geminiSessions,
            geminiFiles,
            walletDailyUsd);
    }

    private static CustomWidgetAiLocalSummary FinalizeLocalSummary(
        IReadOnlyList<CachedCodexFileSummary> codexSessions,
        int claudeFiles,
        int codexFiles,
        DateOnly today,
        DateOnly cutoff30,
        DateOnly start28,
        Dictionary<DateOnly, (long Tokens, double Cost)> dailyMap,
        long todayTokens,
        double todayCost,
        long todayCacheRead,
        long todayPromptTotal,
        long tokens30d,
        double cost30d,
        IReadOnlyList<CachedGeminiFileSummary>? geminiSessions,
        int geminiFiles,
        IReadOnlyDictionary<DateOnly, double>? walletDailyUsd)
    {
        foreach (CachedCodexFileSummary s in codexSessions)
        {
            if (s.DailyRollups is { Count: > 0 })
            {
                foreach ((DateOnly d, CodexDailyRollup rollup) in s.DailyRollups)
                {
                    long tot = rollup.InputTokens + rollup.CachedInputTokens + rollup.OutputTokens;
                    if (tot <= 0)
                    {
                        continue;
                    }

                    if (d == today)
                    {
                        todayTokens += tot;
                        todayCost += rollup.CostUsd;
                        todayCacheRead += rollup.CachedInputTokens;
                        todayPromptTotal += rollup.InputTokens + rollup.CachedInputTokens;
                    }
                    if (d >= cutoff30)
                    {
                        tokens30d += tot;
                        cost30d += rollup.CostUsd;
                    }
                    if (dailyMap.TryGetValue(d, out var bucket))
                    {
                        dailyMap[d] = (bucket.Tokens + tot, bucket.Cost + rollup.CostUsd);
                    }
                }
                continue;
            }

            DateOnly fallbackDate = DateOnly.FromDateTime(s.SessionTimestamp.ToLocalTime().DateTime);
            long fallbackTot = s.InputTokens + s.CachedInputTokens + s.OutputTokens;
            if (fallbackTot <= 0)
            {
                continue;
            }

            if (fallbackDate == today)
            {
                todayTokens += fallbackTot;
                todayCost += s.CostUsd;
                todayCacheRead += s.CachedInputTokens;
                todayPromptTotal += s.InputTokens + s.CachedInputTokens;
            }
            if (fallbackDate >= cutoff30)
            {
                tokens30d += fallbackTot;
                cost30d += s.CostUsd;
            }
            if (dailyMap.TryGetValue(fallbackDate, out var fallbackBucket))
            {
                dailyMap[fallbackDate] = (fallbackBucket.Tokens + fallbackTot, fallbackBucket.Cost + s.CostUsd);
            }
        }

        if (geminiSessions is { Count: > 0 })
        {
            foreach (CachedGeminiFileSummary gs in geminiSessions)
            {
                foreach (GeminiDailyRollup r in gs.DailyRollups)
                {
                    if (r.TotalTokens <= 0)
                    {
                        continue;
                    }

                    if (r.Date == today)
                    {
                        todayTokens += r.TotalTokens;
                        todayCost += r.CostUsd;
                        todayCacheRead += r.CacheReadTokens;
                        todayPromptTotal += r.PromptTokens;
                    }
                    if (r.Date >= cutoff30)
                    {
                        tokens30d += r.TotalTokens;
                        cost30d += r.CostUsd;
                    }
                    if (dailyMap.TryGetValue(r.Date, out var gBucket))
                    {
                        dailyMap[r.Date] = (gBucket.Tokens + r.TotalTokens, gBucket.Cost + r.CostUsd);
                    }
                }
            }
        }

        if (walletDailyUsd is { Count: > 0 })
        {
            foreach ((DateOnly d, double usdSpend) in walletDailyUsd)
            {
                if (usdSpend <= 0.0005)
                {
                    continue;
                }

                if (d == today)
                {
                    todayCost += usdSpend;
                }
                if (d >= cutoff30)
                {
                    cost30d += usdSpend;
                }
                if (dailyMap.TryGetValue(d, out var wBucket))
                {
                    dailyMap[d] = (wBucket.Tokens, wBucket.Cost + usdSpend);
                }
            }
        }

        double cachePct = todayPromptTotal > 0
            ? Math.Clamp((double)todayCacheRead / todayPromptTotal * 100.0, 0.0, 100.0)
            : 0.0;

        var buckets28 = new List<CustomWidgetAiDailyBucket>(28);
        for (int i = 0; i < 28; i++)
        {
            DateOnly d = start28.AddDays(i);
            (long tok, double cost) = dailyMap[d];
            buckets28.Add(new CustomWidgetAiDailyBucket(d, tok, cost));
        }

        return new CustomWidgetAiLocalSummary(
            TodayTokens: todayTokens,
            TodayCostUsd: todayCost,
            CacheHitPercent: cachePct,
            Last30DaysTokens: tokens30d,
            Last30DaysCostUsd: cost30d,
            DetectedClaudeFiles: claudeFiles,
            DetectedCodexFiles: codexFiles,
            Last28Days: buckets28,
            DetectedGeminiFiles: geminiFiles);
    }

}
