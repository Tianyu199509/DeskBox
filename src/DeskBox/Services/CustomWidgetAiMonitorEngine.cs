using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum AiProviderBillingKind
{
    WindowQuota,
    WalletBalance
}

public enum AiQuotaHealth
{
    Idle,
    Healthy,
    Warning,
    Critical
}

public sealed record CustomWidgetAiWindowQuota(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string ResetText,
    AiQuotaHealth Health)
{
    public double RemainingPercent => Math.Clamp(100.0 - UsedPercent, 0.0, 100.0);
}

public sealed record CustomWidgetAiProviderSnapshot(
    string Id,
    string Name,
    string ShortName,
    string Monogram,
    string BrandHex,
    AiProviderBillingKind BillingKind,
    bool IsLocalCli,
    bool IsConfigured,
    bool HasLiveData,
    string PlanBadge,
    AiQuotaHealth Health,
    CustomWidgetAiWindowQuota? PrimaryWindow,
    CustomWidgetAiWindowQuota? SecondaryWindow,
    string CurrencySymbol,
    double WalletBalance,
    string WalletFormatted,
    string WalletBreakdown,
    double? WalletRemainingPercent,
    string RadarValueText,
    string RadarSubtext,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record CustomWidgetAiDailyBucket(
    DateOnly Date,
    long TotalTokens,
    double CostUsd);

public sealed record CustomWidgetAiLocalSummary(
    long TodayTokens,
    double TodayCostUsd,
    double CacheHitPercent,
    long Last30DaysTokens,
    double Last30DaysCostUsd,
    int DetectedClaudeFiles,
    int DetectedCodexFiles,
    IReadOnlyList<CustomWidgetAiDailyBucket> Last14Days);

public sealed record CustomWidgetAiMonitorSnapshot(
    IReadOnlyList<CustomWidgetAiProviderSnapshot> AllProviders,
    IReadOnlyList<CustomWidgetAiProviderSnapshot> ActiveProviders,
    CustomWidgetAiLocalSummary LocalSummary,
    DateTimeOffset UpdatedAt)
{
    public static CustomWidgetAiMonitorSnapshot Empty { get; } = new(
        AllProviders: [],
        ActiveProviders: [],
        LocalSummary: new CustomWidgetAiLocalSummary(0, 0, 0, 0, 0, 0, 0, []),
        UpdatedAt: DateTimeOffset.MinValue);
}

public sealed record AiProviderCredentialEntry(
    string ProviderId,
    string ApiKey,
    string Region = "cn",
    string PlanTier = "pro",
    bool Enabled = true,
    string CustomEndpoint = "",
    string CustomModel = "",
    string ThinkingMode = "auto");

/// <summary>
/// Per-user Windows DPAPI (<c>CryptProtectData</c> / <c>CryptUnprotectData</c>) credential store
/// for AI API keys. Stored in <c>%LOCALAPPDATA%\DeskBox\data\ai-credentials.json</c>, completely
/// outside <c>manifest.json</c> and <c>store.json</c> so <c>.dbbox</c> exports never leak keys.
/// </summary>
public sealed class CustomWidgetAiCredentialStore
{
    private static readonly Lazy<CustomWidgetAiCredentialStore> SharedInstance =
        new(() => new CustomWidgetAiCredentialStore());

    private readonly object _sync = new();
    private readonly string _filePath;
    private Dictionary<string, AiProviderCredentialEntry>? _cache;
    private long _lastLoadedWriteUtcTicks = -1;

    public CustomWidgetAiCredentialStore(string? rootDataDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "ai-credentials.json");
    }

    public static CustomWidgetAiCredentialStore Shared => SharedInstance.Value;

    public string FilePath => _filePath;

    public IReadOnlyDictionary<string, AiProviderCredentialEntry> GetAll()
    {
        lock (_sync)
        {
            EnsureLoadedNoLock();
            return new Dictionary<string, AiProviderCredentialEntry>(_cache!, StringComparer.OrdinalIgnoreCase);
        }
    }

    public AiProviderCredentialEntry? Get(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            return _cache!.TryGetValue(providerId.Trim(), out AiProviderCredentialEntry? entry)
                ? entry
                : null;
        }
    }

    public void Upsert(AiProviderCredentialEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            EnsureLoadedNoLock();
            _cache![entry.ProviderId] = entry;
            SaveNoLock();
        }
    }

    public void RemoveKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.TryGetValue(providerId, out AiProviderCredentialEntry? existing))
            {
                _cache[providerId] = existing with { ApiKey = string.Empty };
                SaveNoLock();
            }
        }
    }

    public static string MaskApiKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return string.Empty;
        }

        string trimmed = rawKey.Trim();
        if (trimmed.Length <= 8)
        {
            return "••••" + trimmed[^Math.Min(2, trimmed.Length)..];
        }

        return $"{trimmed[..4]}••••{trimmed[^4..]}";
    }

    private void EnsureLoadedNoLock()
    {
        long currentTicks = File.Exists(_filePath)
            ? File.GetLastWriteTimeUtc(_filePath).Ticks
            : 0;
        if (_cache is not null && _lastLoadedWriteUtcTicks == currentTicks)
        {
            return;
        }

        _lastLoadedWriteUtcTicks = currentTicks;
        _cache = new Dictionary<string, AiProviderCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        if (currentTicks == 0)
        {
            return;
        }

        try
        {
            string raw = File.ReadAllText(_filePath, Encoding.UTF8);
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("providers", out JsonElement provObj) ||
                provObj.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty prop in provObj.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string cipher = prop.Value.TryGetProperty("cipher", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() ?? string.Empty
                    : string.Empty;
                string region = prop.Value.TryGetProperty("region", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString() ?? "cn"
                    : "cn";
                string planTier = prop.Value.TryGetProperty("planTier", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString() ?? "pro"
                    : "pro";
                bool enabled = !prop.Value.TryGetProperty("enabled", out JsonElement eEl) ||
                               eEl.ValueKind != JsonValueKind.False;
                string customEndpoint = prop.Value.TryGetProperty("customEndpoint", out JsonElement ceEl) && ceEl.ValueKind == JsonValueKind.String
                    ? ceEl.GetString() ?? string.Empty
                    : string.Empty;
                string customModel = prop.Value.TryGetProperty("customModel", out JsonElement cmEl) && cmEl.ValueKind == JsonValueKind.String
                    ? cmEl.GetString() ?? string.Empty
                    : string.Empty;
                string thinkingMode = prop.Value.TryGetProperty("thinkingMode", out JsonElement tmEl) && tmEl.ValueKind == JsonValueKind.String
                    ? tmEl.GetString() ?? "auto"
                    : "auto";

                // Backward-compatible migration from legacy studio-custom entries that stored URL/model in Region/PlanTier.
                if (string.Equals(prop.Name, "studio-custom", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(customEndpoint) &&
                        region.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        customEndpoint = region;
                        region = "cn";
                    }

                    if (string.IsNullOrWhiteSpace(customModel) &&
                        !string.IsNullOrWhiteSpace(planTier) &&
                        !string.Equals(planTier, "pro", StringComparison.OrdinalIgnoreCase))
                    {
                        customModel = planTier;
                        planTier = "pro";
                    }
                }

                string apiKey = string.IsNullOrWhiteSpace(cipher)
                    ? string.Empty
                    : UnprotectString(cipher);

                _cache[prop.Name] = new AiProviderCredentialEntry(
                    ProviderId: prop.Name,
                    ApiKey: apiKey,
                    Region: region,
                    PlanTier: planTier,
                    Enabled: enabled,
                    CustomEndpoint: customEndpoint,
                    CustomModel: customModel,
                    ThinkingMode: thinkingMode);
            }
        }
        catch
        {
            // Corrupted credentials file falls back cleanly to empty dictionary.
        }
    }

    public void UpdateStudioSettings(
        string providerId,
        string? customEndpoint = null,
        string? customModel = null,
        string? thinkingMode = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        string id = providerId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            AiProviderCredentialEntry existing = _cache!.TryGetValue(id, out AiProviderCredentialEntry? cur)
                ? cur
                : new AiProviderCredentialEntry(id, string.Empty);

            _cache[id] = existing with
            {
                CustomEndpoint = customEndpoint is null ? existing.CustomEndpoint : customEndpoint.Trim(),
                CustomModel = customModel is null ? existing.CustomModel : customModel.Trim(),
                ThinkingMode = thinkingMode is null ? existing.ThinkingMode : thinkingMode.Trim()
            };
            SaveNoLock();
        }
    }

    private void SaveNoLock()
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("providers");
            if (_cache is not null)
            {
                foreach ((string id, AiProviderCredentialEntry entry) in _cache)
                {
                    writer.WriteStartObject(id);
                    writer.WriteString("cipher", string.IsNullOrWhiteSpace(entry.ApiKey)
                        ? string.Empty
                        : ProtectString(entry.ApiKey));
                    writer.WriteString("region", entry.Region);
                    writer.WriteString("planTier", entry.PlanTier);
                    writer.WriteBoolean("enabled", entry.Enabled);
                    if (!string.IsNullOrWhiteSpace(entry.CustomEndpoint))
                    {
                        writer.WriteString("customEndpoint", entry.CustomEndpoint);
                    }
                    if (!string.IsNullOrWhiteSpace(entry.CustomModel))
                    {
                        writer.WriteString("customModel", entry.CustomModel);
                    }
                    if (!string.IsNullOrWhiteSpace(entry.ThinkingMode) &&
                        !string.Equals(entry.ThinkingMode, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteString("thinkingMode", entry.ThinkingMode);
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        string tempPath = _filePath + ".tmp";
        File.WriteAllBytes(tempPath, ms.ToArray());
        CustomWidgetStorageFileOps.ReplaceJsonFileAtomically(tempPath, _filePath);
        _lastLoadedWriteUtcTicks = File.Exists(_filePath)
            ? File.GetLastWriteTimeUtc(_filePath).Ticks
            : 0;
    }

    internal static string ProtectString(string plaintext) =>
        DpapiSecretProtector.ProtectUtf8ToBase64(plaintext);

    internal static string UnprotectString(string base64Cipher) =>
        DpapiSecretProtector.UnprotectBase64ToUtf8(base64Cipher);
}

/// <summary>
/// Deterministic local JSONL analyzer (Claude Code &amp; OpenAI Codex CLI), official cloud API
/// balance/quota client (GLM Coding Plan, DeepSeek, Kimi/Moonshot, OpenRouter), and token
/// expression evaluator for <c>deskbox.ai-pulse</c>.
/// </summary>
public static class CustomWidgetAiMonitorEngine
{
    public const string ProviderClaude = "claude";
    public const string ProviderCodex = "codex";
    public const string ProviderGlm = "glm";
    public const string ProviderDeepSeek = "deepseek";
    public const string ProviderKimi = "kimi";
    public const string ProviderOpenRouter = "openrouter";

    public const string KeySelectedProvider = "selectedProvider";
    public const string KeyTrendMetric = "trendMetric";

    internal const int PrefixFingerprintMaxBytes = 512;

    public static IReadOnlyList<(string Id, string Name, string ShortName, string Monogram, string BrandHex, bool IsLocalCli)> Catalog =>
        GetCatalog();

    public static IReadOnlyList<(string Id, string Name, string ShortName, string Monogram, string BrandHex, bool IsLocalCli)> GetCatalog(
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return
        [
            (ProviderClaude, "Claude Code", "Claude", "C", "#D97757", true),
            (ProviderCodex, "OpenAI Codex", "Codex", "Cx", "#10A37F", true),
            (ProviderGlm, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Glm"), "GLM", "G", "#06B6D4", false),
            (ProviderDeepSeek, "DeepSeek", "DeepSeek", "DS", "#3B82F6", false),
            (ProviderKimi, LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Kimi"), "Kimi", "Km", "#6366F1", false),
            (ProviderOpenRouter, "OpenRouter", "Router", "OR", "#8B5CF6", false)
        ];
    }

    public static string GetProviderDisplayName(string providerId, string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return providerId.ToLowerInvariant() switch
        {
            ProviderClaude => "Claude Code",
            ProviderCodex => "OpenAI Codex",
            ProviderGlm => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Glm"),
            ProviderDeepSeek => "DeepSeek",
            ProviderKimi => LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Provider.Kimi"),
            ProviderOpenRouter => "OpenRouter",
            _ => providerId
        };
    }

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
        bool WasIncrementalTail = false);

    public static CustomWidgetAiProviderSnapshot? SelectFocusProvider(
        CustomWidgetAiMonitorSnapshot snapshot,
        IReadOnlyDictionary<string, string> state)
    {
        if (snapshot.ActiveProviders.Count == 0)
        {
            return null;
        }

        if (state.TryGetValue(KeySelectedProvider, out string? wanted) &&
            !string.IsNullOrWhiteSpace(wanted))
        {
            CustomWidgetAiProviderSnapshot? match = snapshot.ActiveProviders.FirstOrDefault(
                p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return snapshot.ActiveProviders[0];
    }

    public static bool IsRemainingMode(IReadOnlyDictionary<string, string> settings) =>
        !settings.TryGetValue("quotaDisplayMode", out string? mode) ||
        !string.Equals(mode, "used", StringComparison.OrdinalIgnoreCase);

    public static string FormatWindowPercentText(
        CustomWidgetAiWindowQuota window,
        bool showRemaining,
        out string modeWord,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        if (showRemaining)
        {
            modeWord = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.WordRemaining");
            return FormattableString.Invariant($"{Math.Round(window.RemainingPercent):0}%");
        }

        modeWord = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.WordUsed");
        return FormattableString.Invariant($"{Math.Round(window.UsedPercent):0}%");
    }

    public static string FormatCompactTokens(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000_000_000.0):0.1}B");
        }
        if (tokens >= 1_000_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000_000.0):0.1}M");
        }
        if (tokens >= 1_000)
        {
            return FormattableString.Invariant($"{(tokens / 1_000.0):0.#}K");
        }
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatResetCountdown(
        DateTimeOffset? resetsAt,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        if (resetsAt is null)
        {
            return LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ResetStandby");
        }

        TimeSpan rem = resetsAt.Value - now;
        if (rem <= TimeSpan.Zero)
        {
            return LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ResetDone");
        }

        if (rem.TotalDays >= 1.0)
        {
            int days = (int)rem.TotalDays;
            int hours = rem.Hours;
            return hours > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ResetDaysHoursFormat", days, hours)
                : LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ResetDaysFormat", days);
        }

        if (rem.TotalHours >= 1.0)
        {
            return LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.ResetHoursMinutesFormat",
                (int)rem.TotalHours,
                rem.Minutes.ToString("D2", CultureInfo.InvariantCulture));
        }

        return LocalizationService.FormatForCulture(
            c,
            "CustomWidget.AiMonitor.ResetMinutesFormat",
            Math.Max(1, (int)Math.Ceiling(rem.TotalMinutes)));
    }

    public static AiQuotaHealth EvaluateWindowHealth(double usedPercent, double warnRemainingPct = 20.0)
    {
        double remaining = Math.Clamp(100.0 - usedPercent, 0.0, 100.0);
        if (remaining <= Math.Min(10.0, warnRemainingPct * 0.5))
        {
            return AiQuotaHealth.Critical;
        }
        if (remaining <= warnRemainingPct)
        {
            return AiQuotaHealth.Warning;
        }
        return AiQuotaHealth.Healthy;
    }

    public static AiQuotaHealth EvaluateWalletHealth(double balance, string currencySymbol, double warnBalanceCny = 15.0)
    {
        double threshold = currencySymbol == "$" ? Math.Max(2.0, warnBalanceCny / 7.0) : warnBalanceCny;
        if (balance <= threshold * 0.25)
        {
            return AiQuotaHealth.Critical;
        }
        if (balance <= threshold)
        {
            return AiQuotaHealth.Warning;
        }
        return AiQuotaHealth.Healthy;
    }

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

    private static ulong ReadPrefixFingerprintFromStart(Stream stream, long limitLength)
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

    private static bool TryVerifyPrefixFingerprint(Stream stream, long parsedLength, ulong expectedFingerprint)
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
                foreach (FileInfo fi in rootDir.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
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

                    if (fileCache is not null)
                    {
                        CachedClaudeFileSummary fresh = ReadOrTailClaudeJsonlFile(
                            file,
                            lastWriteUtc,
                            ticks,
                            len,
                            recentCutoff,
                            rollupCutoffDate,
                            keepActiveMessages: lastWriteUtc >= activeSessionCutoffUtc,
                            previous: null);
                        fileCache[file] = fresh;
                        if (fresh.Turns.Count > 0)
                        {
                            recentTurns.AddRange(fresh.Turns);
                        }
                    }
                    else
                    {
                        IReadOnlyList<ClaudeTurnSample> uncachedTurns = ParseSingleClaudeJsonlFile(file, lastWriteUtc);
                        if (uncachedTurns.Count > 0)
                        {
                            recentTurns.AddRange(uncachedTurns);
                        }
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
        if (blockStart is not null && now < blockStart.Value.AddHours(5))
        {
            DateTimeOffset resetsAt = blockStart.Value.AddHours(5);
            double usedPct = Math.Clamp(blockCost / blockCeilingUsd * 100.0, 1.0, 100.0);
            AiQuotaHealth h = EvaluateWindowHealth(usedPct);
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
                ? $"5h · {primaryWindow.ResetText}"
                : last7DaysCostLabel,
            ErrorMessage: null,
            UpdatedAt: now);

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
            return new CachedClaudeFileSummary(
                ticks,
                length,
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

    internal static IReadOnlyList<ClaudeTurnSample> ParseSingleClaudeJsonlFile(string filePath, DateTime fallbackUtc)
    {
        var byMessageId = new Dictionary<string, ClaudeTurnSample>(StringComparer.Ordinal);
        var anonymousTurns = new List<ClaudeTurnSample>();

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _ = ScanClaudeJsonlStream(fs, 0, fallbackUtc, byMessageId, anonymousTurns);
        }
        catch
        {
            return [];
        }

        if (anonymousTurns.Count == 0)
        {
            return byMessageId.Values.ToList();
        }

        anonymousTurns.AddRange(byMessageId.Values);
        return anonymousTurns;
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
        const int MaxInlineBytes = 64 * 1024;
        byte[] ioBuf = new byte[16 * 1024];
        byte[] lineBuf = new byte[MaxInlineBytes];
        int lineLen = 0;
        bool overflowed = false;
        long consumedBytes = 0;
        long currentLineBytes = 0;
        long totalBytesRead = 0;
        ClaudeMegaLineParser megaParser = default;

        while (true)
        {
            int bytesRead = stream.Read(ioBuf, 0, ioBuf.Length);
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
                            megaParser.Feed(lineBuf.AsSpan(0, lineLen));
                        }
                        if (!segment.IsEmpty)
                        {
                            megaParser.Feed(segment);
                        }
                    }
                }
                else if (!segment.IsEmpty)
                {
                    megaParser.Feed(segment);
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
            else if (overflowed && megaParser.HasCompletedRootObject)
            {
                if (megaParser.TryFinish(fallbackUtc, out string msgId, out ClaudeTurnSample? sample) &&
                    sample is not null)
                {
                    RecordClaudeTurnSample(byMessageId, anonymousTurns, msgId, sample);
                }

                consumedBytes += currentLineBytes;
            }
        }

        return (startOffset + consumedBytes, totalBytesRead);
    }

    /// <summary>
    /// Zero-per-line-allocation streaming JSON brace-depth &amp; string state machine for &gt; 64 KB Claude JSONL lines.
    /// Accurately extracts top-level <c>type</c> and <c>timestamp</c> (braceDepth == 1) and <c>message.id</c>,
    /// <c>message.model</c>, and <c>message.usage</c> (braceDepth == 2) regardless of byte offset in multi-MB lines,
    /// without being fooled by <c>"usage"</c> inside string literals or nested <c>toolUseResult</c> payloads.
    /// </summary>
    private struct ClaudeMegaLineParser
    {
        private const int KeyOffset = 0;
        private const int KeyCapacity = 64;
        private const int ValOffset = 64;
        private const int ValCapacity = 256;
        private const int TsOffset = 320;
        private const int TsCapacity = 64;
        private const int IdOffset = 384;
        private const int IdCapacity = 160;
        private const int ModelOffset = 544;
        private const int ModelCapacity = 160;
        private const int UsageOffset = 704;
        private const int UsageCapacity = 4096;
        private const int ScratchTotalBytes = UsageOffset + UsageCapacity;

        private enum ExpectToken : byte
        {
            ExpectKey,
            ExpectColon,
            ExpectValue,
            ExpectCommaOrEnd
        }

        private enum TargetProp : byte
        {
            None,
            RootType,
            RootTimestamp,
            RootMessage,
            MsgId,
            MsgModel,
            MsgUsage
        }

        private enum StringCapture : byte
        {
            Ignore,
            Key,
            Value
        }

        private byte[]? _scratch;
        private int _braceDepth;
        private int _bracketDepth;
        private bool _started;
        private bool _rejected;
        private bool _finishedRoot;
        private bool _inString;
        private bool _escaped;
        private bool _inMessageObject;
        private bool _capturingUsage;
        private ExpectToken _expect;
        private TargetProp _pendingProp;
        private StringCapture _stringCapture;
        private TargetProp _valueTarget;
        private int _keyLen;
        private bool _keyOverflow;
        private bool _keyHasEscape;
        private int _valLen;
        private bool _valOverflow;
        private bool _valHasEscape;
        private int _tsLen;
        private bool _tsHasEscape;
        private int _idLen;
        private bool _idHasEscape;
        private int _modelLen;
        private bool _modelHasEscape;
        private int _usageLen;
        private bool _usageOverflow;
        private bool _hasUsage;

        public readonly bool HasCompletedRootObject => _finishedRoot && !_rejected;

        public void ResetForNewLine()
        {
            _braceDepth = 0;
            _bracketDepth = 0;
            _started = false;
            _rejected = false;
            _finishedRoot = false;
            _inString = false;
            _escaped = false;
            _inMessageObject = false;
            _capturingUsage = false;
            _expect = ExpectToken.ExpectKey;
            _pendingProp = TargetProp.None;
            _stringCapture = StringCapture.Ignore;
            _valueTarget = TargetProp.None;
            _keyLen = 0;
            _keyOverflow = false;
            _keyHasEscape = false;
            _valLen = 0;
            _valOverflow = false;
            _valHasEscape = false;
            _tsLen = 0;
            _tsHasEscape = false;
            _idLen = 0;
            _idHasEscape = false;
            _modelLen = 0;
            _modelHasEscape = false;
            _usageLen = 0;
            _usageOverflow = false;
            _hasUsage = false;
        }

        public void Feed(ReadOnlySpan<byte> chunk)
        {
            if (_rejected || _finishedRoot || chunk.IsEmpty)
            {
                return;
            }

            byte[] scratch = _scratch ??= new byte[ScratchTotalBytes];

            for (int i = 0; i < chunk.Length; i++)
            {
                if (_inString && !_escaped && _stringCapture == StringCapture.Ignore && !_capturingUsage)
                {
                    int nextSpecial = chunk[i..].IndexOfAny((byte)'"', (byte)'\\');
                    if (nextSpecial < 0)
                    {
                        return;
                    }

                    i += nextSpecial;
                }

                byte b = chunk[i];

                if (!_started)
                {
                    if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or 0xEF or 0xBB or 0xBF)
                    {
                        continue;
                    }

                    if (b != (byte)'{')
                    {
                        _rejected = true;
                        return;
                    }

                    _started = true;
                    _braceDepth = 1;
                    _expect = ExpectToken.ExpectKey;
                    continue;
                }

                if (_capturingUsage && !_usageOverflow)
                {
                    if (_usageLen < UsageCapacity)
                    {
                        scratch[UsageOffset + _usageLen++] = b;
                    }
                    else
                    {
                        _usageOverflow = true;
                    }
                }

                if (_inString)
                {
                    if (_escaped)
                    {
                        _escaped = false;
                        AppendStringByte(scratch, b);
                        continue;
                    }

                    if (b == (byte)'\\')
                    {
                        _escaped = true;
                        if (_stringCapture == StringCapture.Key)
                        {
                            _keyHasEscape = true;
                        }
                        else if (_stringCapture == StringCapture.Value)
                        {
                            _valHasEscape = true;
                        }

                        AppendStringByte(scratch, b);
                        continue;
                    }

                    if (b == (byte)'"')
                    {
                        _inString = false;
                        if (_stringCapture == StringCapture.Key)
                        {
                            CompleteKeyCapture(scratch);
                        }
                        else if (_stringCapture == StringCapture.Value)
                        {
                            CompleteValueCapture(scratch);
                            if (_rejected)
                            {
                                return;
                            }
                        }

                        _stringCapture = StringCapture.Ignore;
                        continue;
                    }

                    AppendStringByte(scratch, b);
                    continue;
                }

                if (b is (byte)' ' or (byte)'\t' or (byte)'\r')
                {
                    continue;
                }

                bool atRoot = _braceDepth == 1 && _bracketDepth == 0;
                bool atMessage = _inMessageObject && _braceDepth == 2 && _bracketDepth == 0;
                bool atTrackedObj = atRoot || atMessage;

                if (b == (byte)'"')
                {
                    _inString = true;
                    _escaped = false;
                    if (atTrackedObj && _expect == ExpectToken.ExpectKey)
                    {
                        _stringCapture = StringCapture.Key;
                        _keyLen = 0;
                        _keyOverflow = false;
                        _keyHasEscape = false;
                    }
                    else if (atTrackedObj && _expect == ExpectToken.ExpectValue)
                    {
                        if (_pendingProp is TargetProp.RootType or TargetProp.RootTimestamp or TargetProp.MsgId or TargetProp.MsgModel)
                        {
                            _stringCapture = StringCapture.Value;
                            _valueTarget = _pendingProp;
                            _valLen = 0;
                            _valOverflow = false;
                            _valHasEscape = false;
                        }
                        else
                        {
                            _stringCapture = StringCapture.Ignore;
                        }

                        _pendingProp = TargetProp.None;
                        _expect = ExpectToken.ExpectCommaOrEnd;
                    }
                    else
                    {
                        _stringCapture = StringCapture.Ignore;
                    }

                    continue;
                }

                if (b == (byte)':')
                {
                    if (atTrackedObj && _expect == ExpectToken.ExpectColon)
                    {
                        _expect = ExpectToken.ExpectValue;
                    }

                    continue;
                }

                if (b == (byte)',')
                {
                    if (atTrackedObj)
                    {
                        _expect = ExpectToken.ExpectKey;
                        _pendingProp = TargetProp.None;
                    }

                    continue;
                }

                if (b == (byte)'{')
                {
                    if (atRoot && _expect == ExpectToken.ExpectValue && _pendingProp == TargetProp.RootMessage)
                    {
                        _inMessageObject = true;
                        _braceDepth = 2;
                        _pendingProp = TargetProp.None;
                        _expect = ExpectToken.ExpectKey;
                    }
                    else if (atMessage && _expect == ExpectToken.ExpectValue && _pendingProp == TargetProp.MsgUsage)
                    {
                        _capturingUsage = true;
                        _usageLen = 1;
                        _usageOverflow = false;
                        scratch[UsageOffset] = (byte)'{';
                        _braceDepth = 3;
                        _pendingProp = TargetProp.None;
                        _expect = ExpectToken.ExpectCommaOrEnd;
                    }
                    else
                    {
                        if (atTrackedObj && _expect == ExpectToken.ExpectValue)
                        {
                            _pendingProp = TargetProp.None;
                            _expect = ExpectToken.ExpectCommaOrEnd;
                        }

                        _braceDepth++;
                    }

                    continue;
                }

                if (b == (byte)'}')
                {
                    _braceDepth--;
                    if (_capturingUsage && _braceDepth == 2)
                    {
                        _capturingUsage = false;
                        _hasUsage = !_usageOverflow && _usageLen >= 2;
                    }
                    else if (_inMessageObject && _braceDepth == 1)
                    {
                        _inMessageObject = false;
                        _expect = ExpectToken.ExpectCommaOrEnd;
                        _pendingProp = TargetProp.None;
                    }

                    if (_braceDepth <= 0)
                    {
                        _finishedRoot = true;
                        return;
                    }

                    continue;
                }

                if (b == (byte)'[')
                {
                    if (atTrackedObj && _expect == ExpectToken.ExpectValue)
                    {
                        _pendingProp = TargetProp.None;
                        _expect = ExpectToken.ExpectCommaOrEnd;
                    }

                    _bracketDepth++;
                    continue;
                }

                if (b == (byte)']')
                {
                    if (_bracketDepth > 0)
                    {
                        _bracketDepth--;
                    }

                    continue;
                }

                if (atTrackedObj && _expect == ExpectToken.ExpectValue)
                {
                    _pendingProp = TargetProp.None;
                    _expect = ExpectToken.ExpectCommaOrEnd;
                }
            }
        }

        private void AppendStringByte(byte[] scratch, byte b)
        {
            if (_stringCapture == StringCapture.Key)
            {
                if (!_keyOverflow)
                {
                    if (_keyLen < KeyCapacity)
                    {
                        scratch[KeyOffset + _keyLen++] = b;
                    }
                    else
                    {
                        _keyOverflow = true;
                    }
                }
            }
            else if (_stringCapture == StringCapture.Value && !_valOverflow)
            {
                if (_valLen < ValCapacity)
                {
                    scratch[ValOffset + _valLen++] = b;
                }
                else
                {
                    _valOverflow = true;
                }
            }
        }

        private void CompleteKeyCapture(byte[] scratch)
        {
            _pendingProp = TargetProp.None;
            _expect = ExpectToken.ExpectColon;
            if (_keyOverflow)
            {
                return;
            }

            ReadOnlySpan<byte> rawKey = scratch.AsSpan(KeyOffset, _keyLen);
            if (!_keyHasEscape)
            {
                if (_braceDepth == 1 && _bracketDepth == 0)
                {
                    if (rawKey.SequenceEqual("type"u8))
                    {
                        _pendingProp = TargetProp.RootType;
                    }
                    else if (rawKey.SequenceEqual("timestamp"u8))
                    {
                        _pendingProp = TargetProp.RootTimestamp;
                    }
                    else if (rawKey.SequenceEqual("message"u8))
                    {
                        _pendingProp = TargetProp.RootMessage;
                    }
                }
                else if (_inMessageObject && _braceDepth == 2 && _bracketDepth == 0)
                {
                    if (rawKey.SequenceEqual("id"u8))
                    {
                        _pendingProp = TargetProp.MsgId;
                    }
                    else if (rawKey.SequenceEqual("model"u8))
                    {
                        _pendingProp = TargetProp.MsgModel;
                    }
                    else if (rawKey.SequenceEqual("usage"u8))
                    {
                        _pendingProp = TargetProp.MsgUsage;
                    }
                }
            }
            else
            {
                string decodedKey = DecodeJsonInnerString(rawKey, hasEscape: true);
                if (_braceDepth == 1 && _bracketDepth == 0)
                {
                    _pendingProp = decodedKey switch
                    {
                        "type" => TargetProp.RootType,
                        "timestamp" => TargetProp.RootTimestamp,
                        "message" => TargetProp.RootMessage,
                        _ => TargetProp.None
                    };
                }
                else if (_inMessageObject && _braceDepth == 2 && _bracketDepth == 0)
                {
                    _pendingProp = decodedKey switch
                    {
                        "id" => TargetProp.MsgId,
                        "model" => TargetProp.MsgModel,
                        "usage" => TargetProp.MsgUsage,
                        _ => TargetProp.None
                    };
                }
            }
        }

        private void CompleteValueCapture(byte[] scratch)
        {
            if (_valOverflow)
            {
                if (_valueTarget == TargetProp.RootType)
                {
                    _rejected = true;
                }
                return;
            }

            ReadOnlySpan<byte> valSpan = scratch.AsSpan(ValOffset, _valLen);
            switch (_valueTarget)
            {
                case TargetProp.RootType:
                    if (!_valHasEscape)
                    {
                        if (!valSpan.SequenceEqual("assistant"u8))
                        {
                            _rejected = true;
                        }
                    }
                    else if (!string.Equals(DecodeJsonInnerString(valSpan, hasEscape: true), "assistant", StringComparison.Ordinal))
                    {
                        _rejected = true;
                    }
                    break;

                case TargetProp.RootTimestamp:
                    if (_valLen <= TsCapacity)
                    {
                        valSpan.CopyTo(scratch.AsSpan(TsOffset));
                        _tsLen = _valLen;
                        _tsHasEscape = _valHasEscape;
                    }
                    break;

                case TargetProp.MsgId:
                    if (_valLen <= IdCapacity)
                    {
                        valSpan.CopyTo(scratch.AsSpan(IdOffset));
                        _idLen = _valLen;
                        _idHasEscape = _valHasEscape;
                    }
                    break;

                case TargetProp.MsgModel:
                    if (_valLen <= ModelCapacity)
                    {
                        valSpan.CopyTo(scratch.AsSpan(ModelOffset));
                        _modelLen = _valLen;
                        _modelHasEscape = _valHasEscape;
                    }
                    break;
            }
        }

        public readonly bool TryFinish(DateTime fallbackUtc, out string msgId, out ClaudeTurnSample? sample)
        {
            msgId = string.Empty;
            sample = null;

            if (_rejected || !_hasUsage || _usageOverflow || _scratch is null || _usageLen < 2)
            {
                return false;
            }

            try
            {
                ReadOnlySpan<byte> usageObj = _scratch.AsSpan(UsageOffset, _usageLen);
                var usageReader = new Utf8JsonReader(usageObj);
                if (!usageReader.Read() || usageReader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                ReadClaudeUsageObject(
                    ref usageReader,
                    out long inputTok,
                    out long outputTok,
                    out long cacheWriteTok,
                    out long cacheReadTok);
                if (inputTok + outputTok + cacheWriteTok + cacheReadTok <= 0)
                {
                    return false;
                }

                DateTimeOffset ts = fallbackUtc;
                if (_tsLen > 0)
                {
                    string tsRaw = DecodeJsonInnerString(_scratch.AsSpan(TsOffset, _tsLen), _tsHasEscape);
                    if (DateTimeOffset.TryParse(
                            tsRaw,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out DateTimeOffset parsedTs))
                    {
                        ts = parsedTs;
                    }
                }

                string model = _modelLen > 0
                    ? DecodeJsonInnerString(_scratch.AsSpan(ModelOffset, _modelLen), _modelHasEscape)
                    : "claude-sonnet";
                if (string.IsNullOrEmpty(model))
                {
                    model = "claude-sonnet";
                }

                if (_idLen > 0)
                {
                    msgId = DecodeJsonInnerString(_scratch.AsSpan(IdOffset, _idLen), _idHasEscape);
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

        private static string DecodeJsonInnerString(ReadOnlySpan<byte> rawInner, bool hasEscape)
        {
            if (rawInner.IsEmpty)
            {
                return string.Empty;
            }

            if (!hasEscape)
            {
                return Encoding.UTF8.GetString(rawInner);
            }

            Span<byte> quoted = stackalloc byte[rawInner.Length + 2];
            quoted[0] = (byte)'"';
            rawInner.CopyTo(quoted[1..]);
            quoted[^1] = (byte)'"';
            var reader = new Utf8JsonReader(quoted);
            return reader.Read() && reader.TokenType == JsonTokenType.String
                ? (reader.GetString() ?? string.Empty)
                : Encoding.UTF8.GetString(rawInner);
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
                foreach (FileInfo fi in rootDir.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
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
        string standbyResetText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.StandbyReset");

        CustomWidgetAiWindowQuota primaryWindow;
        if (newestLimit?.PrimaryUsedPercent is { } pUsed &&
            (newestLimit.PrimaryResetsAt is null || newestLimit.PrimaryResetsAt.Value > now))
        {
            double clamped = Math.Clamp(pUsed, 0.0, 100.0);
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: clamped,
                ResetsAt: newestLimit.PrimaryResetsAt,
                ResetText: FormatResetCountdown(newestLimit.PrimaryResetsAt, now, c),
                Health: EvaluateWindowHealth(clamped));
        }
        else
        {
            primaryWindow = new CustomWidgetAiWindowQuota(
                Label: label5h,
                UsedPercent: 0,
                ResetsAt: null,
                ResetText: standbyResetText,
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
                ResetText: standbyResetText,
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
                ? $"5h · {primaryWindow.ResetText}"
                : LocalizationService.FormatForCulture(
                    c,
                    "CustomWidget.AiMonitor.WeeklyLeftFormat",
                    Math.Round(secondaryWindow.RemainingPercent).ToString("0", CultureInfo.InvariantCulture)),
            ErrorMessage: null,
            UpdatedAt: now);

        return (snapshot, sessions, fileCount);
    }

    internal static CachedCodexFileSummary ParseSingleCodexJsonlFile(
        string filePath,
        DateTime fallbackUtc,
        long ticks,
        long length) =>
        ParseOrTailCodexJsonlFile(filePath, fallbackUtc, ticks, length, previous: null);

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
                            ref latestLimit);
                    });

                prefixFp = ReadPrefixFingerprintFromStart(fs, safeParsedLen);
            }
        }
        catch
        {
            // Ignore read failure.
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
            WasIncrementalTail: wasIncremental);
    }

    private delegate void JsonlSpanLineHandler(ReadOnlySpan<byte> lineSpan);

    /// <summary>
    /// Streams a UTF-8 JSONL file using fixed SOH buffers (16 KB I/O + 64 KB line buffer)
    /// without allocating LOH strings via <see cref="StreamReader.ReadLine()"/> or renting
    /// multi-megabyte buckets from <c>ArrayPool&lt;byte&gt;.Shared</c> via <see cref="JsonDocument"/>.
    /// </summary>
    private static (long SafeParsedLength, long TotalBytesRead) ScanJsonlUtf8Stream(
        Stream stream,
        long startOffset,
        JsonlSpanLineHandler onLine)
    {
        const int MaxInlineBytes = 64 * 1024;

        byte[] ioBuf = new byte[16 * 1024];
        byte[] lineBuf = new byte[MaxInlineBytes];
        int lineLen = 0;
        bool overflowed = false;
        long consumedBytes = 0;
        long currentLineBytes = 0;
        long totalBytesRead = 0;
        byte lastNonWsOverflowByte = 0;

        while (true)
        {
            int bytesRead = stream.Read(ioBuf, 0, ioBuf.Length);
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
                        // Codex token_count events are compact (< 2 KB); > 64 KB lines are tool/file payloads.
                        overflowed = true;
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
                if (effIn >= maxIn) maxIn = effIn;
                if (effCached >= maxCachedIn) maxCachedIn = effCached;
                if (effOut >= maxOut) maxOut = effOut;
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
        DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DateOnly cutoff30 = today.AddDays(-29);
        DateOnly start14 = today.AddDays(-13);

        var dailyMap = new Dictionary<DateOnly, (long Tokens, double Cost)>(14);
        for (int i = 0; i < 14; i++)
        {
            dailyMap[start14.AddDays(i)] = (0, 0);
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
            start14,
            dailyMap,
            todayTokens,
            todayCost,
            todayCacheRead,
            todayPromptTotal,
            tokens30d,
            cost30d);
    }

    public static CustomWidgetAiLocalSummary BuildLocalSummary(
        IReadOnlyList<ClaudeTurnSample> claudeTurns,
        IReadOnlyList<CachedCodexFileSummary> codexSessions,
        int claudeFiles,
        int codexFiles,
        DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DateOnly cutoff30 = today.AddDays(-29);
        DateOnly start14 = today.AddDays(-13);

        var dailyMap = new Dictionary<DateOnly, (long Tokens, double Cost)>(14);
        for (int i = 0; i < 14; i++)
        {
            dailyMap[start14.AddDays(i)] = (0, 0);
        }

        long todayTokens = 0;
        double todayCost = 0;
        long todayCacheRead = 0;
        long todayPromptTotal = 0;
        long tokens30d = 0;
        double cost30d = 0;

        foreach (ClaudeTurnSample t in claudeTurns)
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

        return FinalizeLocalSummary(
            codexSessions,
            claudeFiles,
            codexFiles,
            today,
            cutoff30,
            start14,
            dailyMap,
            todayTokens,
            todayCost,
            todayCacheRead,
            todayPromptTotal,
            tokens30d,
            cost30d);
    }

    private static CustomWidgetAiLocalSummary FinalizeLocalSummary(
        IReadOnlyList<CachedCodexFileSummary> codexSessions,
        int claudeFiles,
        int codexFiles,
        DateOnly today,
        DateOnly cutoff30,
        DateOnly start14,
        Dictionary<DateOnly, (long Tokens, double Cost)> dailyMap,
        long todayTokens,
        double todayCost,
        long todayCacheRead,
        long todayPromptTotal,
        long tokens30d,
        double cost30d)
    {
        foreach (CachedCodexFileSummary s in codexSessions)
        {
            DateOnly d = DateOnly.FromDateTime(s.SessionTimestamp.ToLocalTime().DateTime);
            long tot = s.InputTokens + s.CachedInputTokens + s.OutputTokens;
            if (tot <= 0)
            {
                continue;
            }

            if (d == today)
            {
                todayTokens += tot;
                todayCost += s.CostUsd;
                todayCacheRead += s.CachedInputTokens;
                todayPromptTotal += s.InputTokens + s.CachedInputTokens;
            }
            if (d >= cutoff30)
            {
                tokens30d += tot;
                cost30d += s.CostUsd;
            }
            if (dailyMap.TryGetValue(d, out var bucket))
            {
                dailyMap[d] = (bucket.Tokens + tot, bucket.Cost + s.CostUsd);
            }
        }

        double cachePct = todayPromptTotal > 0
            ? Math.Clamp((double)todayCacheRead / todayPromptTotal * 100.0, 0.0, 100.0)
            : 0.0;

        var buckets14 = new List<CustomWidgetAiDailyBucket>(14);
        for (int i = 0; i < 14; i++)
        {
            DateOnly d = start14.AddDays(i);
            (long tok, double cost) = dailyMap[d];
            buckets14.Add(new CustomWidgetAiDailyBucket(d, tok, cost));
        }

        return new CustomWidgetAiLocalSummary(
            TodayTokens: todayTokens,
            TodayCostUsd: todayCost,
            CacheHitPercent: cachePct,
            Last30DaysTokens: tokens30d,
            Last30DaysCostUsd: cost30d,
            DetectedClaudeFiles: claudeFiles,
            DetectedCodexFiles: codexFiles,
            Last14Days: buckets14);
    }

    public static CustomWidgetAiProviderSnapshot ParseDeepSeekBalanceJson(
        string json,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string currency = "CNY";
        double total = 0;
        double granted = 0;
        double toppedUp = 0;

        if (root.TryGetProperty("balance_infos", out JsonElement arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string cur = item.TryGetProperty("currency", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() ?? "CNY"
                    : "CNY";
                double tot = ParseStringOrNumber(item, "total_balance");
                double gr = ParseStringOrNumber(item, "granted_balance");
                double top = ParseStringOrNumber(item, "topped_up_balance");

                if (tot > 0 || total == 0)
                {
                    currency = cur;
                    total = tot;
                    granted = gr;
                    toppedUp = top;
                    if (string.Equals(cur, "CNY", StringComparison.OrdinalIgnoreCase) && tot > 0)
                    {
                        break;
                    }
                }
            }
        }

        string sym = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
        AiQuotaHealth health = EvaluateWalletHealth(total, sym);
        string formatted = FormattableString.Invariant($"{sym} {total:0.00}");
        string toppedUpText = toppedUp.ToString("0.00", CultureInfo.InvariantCulture);
        string grantedText = granted.ToString("0.00", CultureInfo.InvariantCulture);
        string breakdown = granted > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownGrantedFormat",
                sym,
                toppedUpText,
                grantedText)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.DeepSeekBreakdownTopupFormat",
                currency);

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderDeepSeek,
            Name: "DeepSeek",
            ShortName: "DeepSeek",
            Monogram: "DS",
            BrandHex: "#3B82F6",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: currency,
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: sym,
            WalletBalance: total,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: null,
            RadarValueText: formatted,
            RadarSubtext: granted > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextGrantedFormat", sym, grantedText)
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.DeepSeekSubtextOfficial"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseKimiBalanceJson(
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
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        double available = ParseStringOrNumber(data, "available_balance");
        double cash = ParseStringOrNumber(data, "cash_balance");
        double voucher = ParseStringOrNumber(data, "voucher_balance");

        bool isIntl = string.Equals(region, "intl", StringComparison.OrdinalIgnoreCase);
        string sym = isIntl ? "$" : "¥";
        AiQuotaHealth health = EvaluateWalletHealth(available, sym);
        string formatted = FormattableString.Invariant($"{sym} {available:0.00}");
        string cashText = cash.ToString("0.00", CultureInfo.InvariantCulture);
        string voucherText = voucher.ToString("0.00", CultureInfo.InvariantCulture);
        string breakdown = voucher > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.KimiBreakdownVoucherFormat",
                sym,
                cashText,
                voucherText)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.KimiBreakdownCashFormat",
                isIntl ? "Moonshot AI" : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.NodeCn"));

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderKimi,
            Name: GetProviderDisplayName(ProviderKimi, c),
            ShortName: "Kimi",
            Monogram: "Km",
            BrandHex: "#6366F1",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: isIntl ? "Global" : "CN",
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: sym,
            WalletBalance: available,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: null,
            RadarValueText: formatted,
            RadarSubtext: voucher > 0
                ? LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.KimiSubtextVoucherFormat", sym, voucherText)
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.KimiSubtextLive"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseOpenRouterCreditsJson(
        string json,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        double totalCredits = ParseStringOrNumber(data, "total_credits");
        double totalUsage = ParseStringOrNumber(data, "total_usage");
        double available = Math.Max(0.0, totalCredits - totalUsage);
        double? remPct = totalCredits > 0
            ? Math.Clamp(available / totalCredits * 100.0, 0.0, 100.0)
            : null;

        AiQuotaHealth health = EvaluateWalletHealth(available, "$");
        string formatted = FormattableString.Invariant($"$ {available:0.00}");
        string breakdown = totalCredits > 0
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.OpenRouterBreakdownFormat",
                FormattableString.Invariant($"${totalCredits:0.00}"),
                FormattableString.Invariant($"${totalUsage:0.00}"))
            : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.OpenRouterPrepaid");

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderOpenRouter,
            Name: "OpenRouter",
            ShortName: "Router",
            Monogram: "OR",
            BrandHex: "#8B5CF6",
            BillingKind: AiProviderBillingKind.WalletBalance,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "Credits",
            Health: health,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "$",
            WalletBalance: available,
            WalletFormatted: formatted,
            WalletBreakdown: breakdown,
            WalletRemainingPercent: remPct,
            RadarValueText: formatted,
            RadarSubtext: remPct.HasValue
                ? LocalizationService.FormatForCulture(
                    c,
                    "CustomWidget.AiMonitor.RemainingPctFormat",
                    remPct.Value.ToString("0", CultureInfo.InvariantCulture))
                : LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.MultiModelGateway"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot ParseGlmQuotaJson(
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
        JsonElement data = root.TryGetProperty("data", out JsonElement dEl) && dEl.ValueKind == JsonValueKind.Object
            ? dEl
            : root;

        string level = data.TryGetProperty("level", out JsonElement lEl) && lEl.ValueKind == JsonValueKind.String
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase((lEl.GetString() ?? "Coding").ToLowerInvariant())
            : "Coding";

        string label5h = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window5hLabel");
        string label7d = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Window7dAllowanceLabel");
        string labelMcp = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.McpToolPoolLabel");
        string autoCycleText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.AutoCycleReset");

        CustomWidgetAiWindowQuota? fiveHour = null;
        CustomWidgetAiWindowQuota? secondary = null;

        if (data.TryGetProperty("limits", out JsonElement limitsArr) &&
            limitsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in limitsArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string type = item.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? string.Empty
                    : string.Empty;
                string window = item.TryGetProperty("window", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.String
                    ? wEl.GetString() ?? string.Empty
                    : string.Empty;

                double usedPct;
                if (item.TryGetProperty("percentage", out JsonElement pctEl) &&
                    (pctEl.ValueKind == JsonValueKind.Number || pctEl.ValueKind == JsonValueKind.String))
                {
                    usedPct = ParseStringOrNumber(item, "percentage");
                }
                else
                {
                    double limit = ParseStringOrNumber(item, "limit");
                    if (limit <= 0) limit = ParseStringOrNumber(item, "total");
                    double usage = ParseStringOrNumber(item, "usage");
                    if (usage <= 0 && limit > 0 && item.TryGetProperty("remaining", out _))
                    {
                        usage = Math.Max(0, limit - ParseStringOrNumber(item, "remaining"));
                    }
                    usedPct = limit > 0 ? Math.Clamp(usage / limit * 100.0, 0.0, 100.0) : 0.0;
                }

                DateTimeOffset? resetAt = ReadEpochMsOrSec(item, "nextResetTime") ?? ReadEpochMsOrSec(item, "resetTime");
                var q = new CustomWidgetAiWindowQuota(
                    Label: window.Contains("week", StringComparison.OrdinalIgnoreCase)
                        ? label7d
                        : type.Contains("TIME", StringComparison.OrdinalIgnoreCase)
                            ? labelMcp
                            : label5h,
                    UsedPercent: Math.Clamp(usedPct, 0.0, 100.0),
                    ResetsAt: resetAt,
                    ResetText: resetAt is not null ? FormatResetCountdown(resetAt, now, c) : autoCycleText,
                    Health: EvaluateWindowHealth(usedPct));

                if (fiveHour is null &&
                    (window.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
                     type.Contains("TOKENS", StringComparison.OrdinalIgnoreCase)))
                {
                    fiveHour = q with { Label = label5h };
                }
                else if (secondary is null)
                {
                    secondary = q;
                }
            }
        }

        fiveHour ??= new CustomWidgetAiWindowQuota(
            label5h,
            0,
            null,
            LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.QuotaHealthy"),
            AiQuotaHealth.Healthy);
        secondary ??= new CustomWidgetAiWindowQuota(
            LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.McpExtPoolLabel"),
            0,
            null,
            LocalizationService.TranslateForCulture(
                c,
                string.Equals(region, "intl", StringComparison.OrdinalIgnoreCase)
                    ? "CustomWidget.AiMonitor.NodeZai"
                    : "CustomWidget.AiMonitor.NodeBigModel"),
            AiQuotaHealth.Healthy);

        AiQuotaHealth overall = (AiQuotaHealth)Math.Max((int)fiveHour.Health, (int)secondary.Health);

        return new CustomWidgetAiProviderSnapshot(
            Id: ProviderGlm,
            Name: GetProviderDisplayName(ProviderGlm, c),
            ShortName: "GLM",
            Monogram: "G",
            BrandHex: "#06B6D4",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: false,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: level,
            Health: overall,
            PrimaryWindow: fiveHour,
            SecondaryWindow: secondary,
            CurrencySymbol: "¥",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: FormattableString.Invariant($"{Math.Round(fiveHour.RemainingPercent):0}%"),
            RadarSubtext: fiveHour.ResetsAt is not null ? $"5h · {fiveHour.ResetText}" : $"GLM {level}",
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static async Task<CustomWidgetAiProviderSnapshot> FetchCloudProviderAsync(
        HttpClient httpClient,
        AiProviderCredentialEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? cultureName = null)
    {
        var (snap, _) = await FetchCloudProviderInternalAsync(httpClient, entry, now, cancellationToken, cultureName);
        return snap;
    }

    internal static CustomWidgetAiProviderSnapshot ParseCloudProviderBody(
        string providerId,
        string rawBody,
        string region,
        DateTimeOffset now,
        string? cultureName = null)
    {
        return providerId.ToLowerInvariant() switch
        {
            ProviderDeepSeek => ParseDeepSeekBalanceJson(rawBody, now, cultureName),
            ProviderKimi => ParseKimiBalanceJson(rawBody, region, now, cultureName),
            ProviderGlm => ParseGlmQuotaJson(rawBody, region, now, cultureName),
            ProviderOpenRouter => ParseOpenRouterCreditsJson(rawBody, now, cultureName),
            _ => CreateUnconfiguredSnapshot(providerId, now, cultureName)
        };
    }

    internal static async Task<(CustomWidgetAiProviderSnapshot Snapshot, string? RawBody)> FetchCloudProviderInternalAsync(
        HttpClient httpClient,
        AiProviderCredentialEntry entry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        string id = entry.ProviderId.ToLowerInvariant();
        if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            return (CreateUnconfiguredSnapshot(id, now, c), null);
        }

        try
        {
            string url = id switch
            {
                ProviderDeepSeek => "https://api.deepseek.com/user/balance",
                ProviderKimi => string.Equals(entry.Region, "intl", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.moonshot.ai/v1/users/me/balance"
                    : "https://api.moonshot.cn/v1/users/me/balance",
                ProviderGlm => string.Equals(entry.Region, "intl", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.z.ai/api/monitor/usage/quota/limit"
                    : "https://open.bigmodel.cn/api/monitor/usage/quota/limit",
                ProviderOpenRouter => "https://openrouter.ai/api/v1/credits",
                _ => throw new InvalidOperationException($"Unsupported AI provider '{id}'.")
            };

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.ApiKey.Trim());
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage resp = await httpClient.SendAsync(req, cancellationToken);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                string err = (int)resp.StatusCode is 401 or 403
                    ? LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrInvalidKey")
                    : LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ErrHttpFormat", (int)resp.StatusCode);
                return (CreateErrorSnapshot(id, err, now, c), null);
            }

            return (ParseCloudProviderBody(id, body, entry.Region, now, c), body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (CreateErrorSnapshot(
                id,
                LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.ErrTimeout"),
                now,
                c), null);
        }
        catch (Exception ex)
        {
            return (CreateErrorSnapshot(
                id,
                LocalizationService.FormatForCulture(c, "CustomWidget.AiMonitor.ErrNetworkFormat", ex.GetType().Name),
                now,
                c), null);
        }
    }

    public static CustomWidgetAiProviderSnapshot CreateUnconfiguredSnapshot(
        string providerId,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var meta = GetCatalog(c).FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(meta.Id))
        {
            meta = (providerId, providerId, providerId, "AI", "#06B6D4", false);
        }

        AiProviderBillingKind kind = providerId.ToLowerInvariant() is ProviderClaude or ProviderCodex or ProviderGlm
            ? AiProviderBillingKind.WindowQuota
            : AiProviderBillingKind.WalletBalance;

        return new CustomWidgetAiProviderSnapshot(
            Id: meta.Id,
            Name: meta.Name,
            ShortName: meta.ShortName,
            Monogram: meta.Monogram,
            BrandHex: meta.BrandHex,
            BillingKind: kind,
            IsLocalCli: meta.IsLocalCli,
            IsConfigured: false,
            HasLiveData: false,
            PlanBadge: string.Empty,
            Health: AiQuotaHealth.Idle,
            PrimaryWindow: null,
            SecondaryWindow: null,
            CurrencySymbol: "¥",
            WalletBalance: 0,
            WalletFormatted: "--",
            WalletBreakdown: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.NoKeyConfigured"),
            WalletRemainingPercent: null,
            RadarValueText: LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Disconnected"),
            RadarSubtext: LocalizationService.TranslateForCulture(
                c,
                meta.IsLocalCli
                    ? "CustomWidget.AiMonitor.WaitLocalSession"
                    : "CustomWidget.AiMonitor.ClickToConfigureKey"),
            ErrorMessage: null,
            UpdatedAt: now);
    }

    public static CustomWidgetAiProviderSnapshot CreateErrorSnapshot(
        string providerId,
        string errorMessage,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        CustomWidgetAiProviderSnapshot baseSnap = CreateUnconfiguredSnapshot(providerId, now, c);
        return baseSnap with
        {
            IsConfigured = true,
            HasLiveData = false,
            Health = AiQuotaHealth.Warning,
            WalletFormatted = "--",
            WalletBreakdown = errorMessage,
            RadarValueText = LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.Retry"),
            RadarSubtext = errorMessage,
            ErrorMessage = errorMessage
        };
    }

    public static string ResolveToken(
        string path,
        CustomWidgetAiMonitorSnapshot snapshot,
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        bool showRem = IsRemainingMode(settings);
        CustomWidgetAiProviderSnapshot? focus = SelectFocusProvider(snapshot, state);

        // Capsule prioritizes the most urgent provider, falling back to focus provider
        CustomWidgetAiProviderSnapshot? urgent = snapshot.ActiveProviders
            .OrderByDescending(static p => (int)p.Health)
            .FirstOrDefault() ?? focus;

        return path switch
        {
            "capsuleTitle" => BuildCapsuleTitle(urgent, showRem, c),
            "capsuleSummary" => BuildCapsuleSummary(urgent, snapshot, c),
            "capsuleBadge" => BuildCapsuleBadge(urgent, showRem, c),
            "capsuleIsWarning" => urgent is { Health: AiQuotaHealth.Warning or AiQuotaHealth.Critical } ? "true" : "false",
            "capsuleProgress" => ResolveCapsuleProgress(urgent, showRem).ToString("0.0", CultureInfo.InvariantCulture),
            "activeCount" => snapshot.ActiveProviders.Count.ToString(CultureInfo.InvariantCulture),
            "todayTokens" or "tokens" => FormatCompactTokens(snapshot.LocalSummary.TodayTokens),
            "todayCost" or "cost" => FormattableString.Invariant($"${snapshot.LocalSummary.TodayCostUsd:0.00}"),
            "monthTokens" => FormatCompactTokens(snapshot.LocalSummary.Last30DaysTokens),
            "monthCost" => FormattableString.Invariant($"${snapshot.LocalSummary.Last30DaysCostUsd:0.00}"),
            "cacheHitPercent" => FormattableString.Invariant($"{snapshot.LocalSummary.CacheHitPercent:0}%"),
            "focus.name" => focus?.Name ?? string.Empty,
            "focus.value" or "balance" => focus?.RadarValueText ?? string.Empty,
            "focus.subtext" => focus?.RadarSubtext ?? string.Empty,
            "focus.badge" => focus?.PlanBadge ?? string.Empty,
            _ => string.Empty
        };
    }

    private static string BuildCapsuleTitle(CustomWidgetAiProviderSnapshot? p, bool showRem, string culture)
    {
        if (p is null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleDisconnectedTitle");
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            string pct = FormatWindowPercentText(w, showRem, out string word, culture);
            return LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.AiMonitor.CapsuleWindowTitleFormat",
                p.ShortName,
                word,
                pct);
        }

        return $"{p.ShortName} · {p.WalletFormatted}";
    }

    private static string BuildCapsuleSummary(
        CustomWidgetAiProviderSnapshot? p,
        CustomWidgetAiMonitorSnapshot snap,
        string culture)
    {
        if (p is null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleDisconnectedSummary");
        }

        if (p.ErrorMessage is not null)
        {
            return p.ErrorMessage;
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            return snap.LocalSummary.TodayTokens > 0
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.AiMonitor.CapsuleTodayTokensSummaryFormat",
                    w.ResetText,
                    FormatCompactTokens(snap.LocalSummary.TodayTokens))
                : w.ResetText;
        }

        return p.WalletBreakdown;
    }

    private static string BuildCapsuleBadge(CustomWidgetAiProviderSnapshot? p, bool showRem, string culture)
    {
        if (p is null)
        {
            return string.Empty;
        }

        if (p.ErrorMessage is not null)
        {
            return LocalizationService.TranslateForCulture(culture, "CustomWidget.AiMonitor.CapsuleErrorBadge");
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            return FormatWindowPercentText(w, showRem, out _, culture);
        }

        return p.WalletFormatted;
    }

    private static double ResolveCapsuleProgress(CustomWidgetAiProviderSnapshot? p, bool showRem)
    {
        if (p is null)
        {
            return 0;
        }

        if (p.BillingKind == AiProviderBillingKind.WindowQuota && p.PrimaryWindow is { } w)
        {
            return showRem ? w.RemainingPercent : w.UsedPercent;
        }

        return p.WalletRemainingPercent ?? 100.0;
    }

    private static long ReadLong(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement el))
        {
            return 0;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v))
        {
            return v;
        }

        if (el.ValueKind == JsonValueKind.String &&
            long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sv))
        {
            return sv;
        }

        return 0;
    }

    private static double? ReadNullableDouble(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d))
        {
            return d;
        }

        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd))
        {
            return sd;
        }

        return null;
    }

    private static double ParseStringOrNumber(JsonElement obj, string propName) =>
        ReadNullableDouble(obj, propName) ?? 0.0;

    private static DateTimeOffset? ReadResetTimestamp(JsonElement windowEl, DateTimeOffset lineTimestamp)
    {
        DateTimeOffset? epoch = ReadEpochMsOrSec(windowEl, "resets_at");
        if (epoch is not null)
        {
            return epoch;
        }

        double? inSec = ReadNullableDouble(windowEl, "resets_in_seconds");
        return inSec.HasValue ? lineTimestamp.AddSeconds(inSec.Value) : null;
    }

    private static DateTimeOffset? ReadEpochMsOrSec(JsonElement obj, string propName)
    {
        long raw = ReadLong(obj, propName);
        if (raw <= 0)
        {
            return null;
        }

        try
        {
            return raw > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                : DateTimeOffset.FromUnixTimeSeconds(raw);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Shared background telemetry &amp; quota coordinator for <c>deskbox.ai-pulse</c>.
/// Keeps file &amp; network I/O completely off the UI thread with incremental file caching.
/// </summary>
public sealed class CustomWidgetAiMonitorSource
{
    public static CustomWidgetAiMonitorSource Shared { get; } = new();

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly ConcurrentDictionary<object, int> _subscribers = new();
    private readonly ConcurrentDictionary<string, CustomWidgetAiMonitorEngine.CachedClaudeFileSummary> _claudeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CustomWidgetAiMonitorEngine.CachedCodexFileSummary> _codexCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CustomWidgetAiProviderSnapshot> _cloudCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string RawBody, string Region, DateTimeOffset UpdatedAt)> _cloudRawBodyCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _timerLock = new();
    private Timer? _timer;
    private DateTimeOffset _lastCloudFetchUtc = DateTimeOffset.MinValue;
    private string _lastRefreshCulture = string.Empty;
    private volatile CustomWidgetAiMonitorSnapshot _latest = CustomWidgetAiMonitorSnapshot.Empty;

    public CustomWidgetAiMonitorSnapshot Latest => _latest;

    public event Action<CustomWidgetAiMonitorSnapshot>? SnapshotUpdated;

    public void Subscribe(object subscriber, int intervalMs)
    {
        int clamped = Math.Clamp(intervalMs, 15_000, 900_000);
        bool wasEmpty = _subscribers.IsEmpty;
        _subscribers[subscriber] = clamped;
        ReconfigureTimer();

        if (wasEmpty || _latest.UpdatedAt == DateTimeOffset.MinValue)
        {
            _ = RefreshNowAsync(forceCloudRefresh: false);
        }
    }

    public void Unsubscribe(object subscriber)
    {
        _subscribers.TryRemove(subscriber, out _);
        ReconfigureTimer();
    }

    public async Task<(bool Success, string Message, CustomWidgetAiProviderSnapshot Snapshot)> TestAndSaveCredentialAsync(
        AiProviderCredentialEntry entry,
        CancellationToken cancellationToken = default,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        CustomWidgetAiCredentialStore.Shared.Upsert(entry);

        if (entry.ProviderId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase) ||
            entry.ProviderId.Equals(CustomWidgetAiMonitorEngine.ProviderCodex, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshNowAsync(forceCloudRefresh: false);
            CustomWidgetAiProviderSnapshot? localSnap = _latest.AllProviders.FirstOrDefault(
                p => p.Id.Equals(entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            return (
                true,
                LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.LocalConfigUpdated"),
                localSnap ?? CustomWidgetAiMonitorEngine.CreateUnconfiguredSnapshot(entry.ProviderId, DateTimeOffset.Now, c));
        }

        DateTimeOffset fetchedAt = DateTimeOffset.Now;
        var (snap, rawBody) = await CustomWidgetAiMonitorEngine.FetchCloudProviderInternalAsync(
            _httpClient,
            entry,
            fetchedAt,
            cancellationToken,
            c);

        _cloudCache[entry.ProviderId] = snap;
        if (!string.IsNullOrEmpty(rawBody))
        {
            _cloudRawBodyCache[entry.ProviderId] = (rawBody, entry.Region, fetchedAt);
        }
        await RefreshNowAsync(forceCloudRefresh: false);

        if (snap.ErrorMessage is not null)
        {
            return (false, snap.ErrorMessage, snap);
        }

        string detail = snap.BillingKind == AiProviderBillingKind.WalletBalance
            ? LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.ConnectedWalletFormat",
                snap.WalletFormatted)
            : LocalizationService.FormatForCulture(
                c,
                "CustomWidget.AiMonitor.ConnectedWindowFormat",
                Math.Round(snap.PrimaryWindow?.RemainingPercent ?? 100).ToString("0", CultureInfo.InvariantCulture));
        return (true, detail, snap);
    }

    public async Task RemoveCredentialAsync(string providerId)
    {
        CustomWidgetAiCredentialStore.Shared.RemoveKey(providerId);
        _cloudCache.TryRemove(providerId, out _);
        _cloudRawBodyCache.TryRemove(providerId, out _);
        await RefreshNowAsync(forceCloudRefresh: false);
    }

    public async Task<CustomWidgetAiMonitorSnapshot> RefreshNowAsync(bool forceCloudRefresh = true)
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return _latest;
        }

        try
        {
            return await Task.Run(async () =>
            {
                DateTimeOffset now = DateTimeOffset.Now;
                string culture = LocalizationService.ResolveAmbientCultureName();
                bool cultureChanged = !string.Equals(_lastRefreshCulture, culture, StringComparison.OrdinalIgnoreCase);
                _lastRefreshCulture = culture;

                IReadOnlyDictionary<string, AiProviderCredentialEntry> creds = CustomWidgetAiCredentialStore.Shared.GetAll();

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string claudeDir = Path.Combine(home, ".claude", "projects");
                string codexDir = Path.Combine(home, ".codex", "sessions");

                bool claudeEnabled = !creds.TryGetValue(CustomWidgetAiMonitorEngine.ProviderClaude, out var claudeCred) || claudeCred.Enabled;
                string claudeTier = claudeCred?.PlanTier ?? "pro";
                bool codexEnabled = !creds.TryGetValue(CustomWidgetAiMonitorEngine.ProviderCodex, out var codexCred) || codexCred.Enabled;

                var (claudeSnap, _, claudeFiles) = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                    claudeDir,
                    claudeTier,
                    claudeEnabled,
                    now,
                    _claudeCache,
                    culture);

                var (codexSnap, codexSessions, codexFiles) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                    codexDir,
                    codexEnabled,
                    now,
                    _codexCache,
                    culture);

                CustomWidgetAiLocalSummary localSummary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                    _claudeCache.Values,
                    codexSessions,
                    claudeFiles,
                    codexFiles,
                    now);

                bool shouldFetchCloud = forceCloudRefresh ||
                                        (now - _lastCloudFetchUtc) >= TimeSpan.FromMinutes(3);

                string[] cloudIds =
                [
                    CustomWidgetAiMonitorEngine.ProviderGlm,
                    CustomWidgetAiMonitorEngine.ProviderDeepSeek,
                    CustomWidgetAiMonitorEngine.ProviderKimi,
                    CustomWidgetAiMonitorEngine.ProviderOpenRouter
                ];

                var cloudTasks = new List<Task<CustomWidgetAiProviderSnapshot>>();
                foreach (string cid in cloudIds)
                {
                    if (creds.TryGetValue(cid, out AiProviderCredentialEntry? entry) &&
                        entry.Enabled &&
                        !string.IsNullOrWhiteSpace(entry.ApiKey))
                    {
                        if (!shouldFetchCloud &&
                            !cultureChanged &&
                            _cloudCache.TryGetValue(cid, out CustomWidgetAiProviderSnapshot? cachedSnap))
                        {
                            cloudTasks.Add(Task.FromResult(cachedSnap));
                        }
                        else if (!shouldFetchCloud &&
                                 cultureChanged &&
                                 _cloudRawBodyCache.TryGetValue(cid, out var cachedBody))
                        {
                            CustomWidgetAiProviderSnapshot reparsed = CustomWidgetAiMonitorEngine.ParseCloudProviderBody(
                                cid,
                                cachedBody.RawBody,
                                cachedBody.Region,
                                cachedBody.UpdatedAt,
                                culture);
                            _cloudCache[cid] = reparsed;
                            cloudTasks.Add(Task.FromResult(reparsed));
                        }
                        else
                        {
                            AiProviderCredentialEntry captured = entry;
                            cloudTasks.Add(Task.Run(async () =>
                            {
                                var (fetched, rawBody) = await CustomWidgetAiMonitorEngine.FetchCloudProviderInternalAsync(
                                    _httpClient,
                                    captured,
                                    now,
                                    cultureName: culture);
                                _cloudCache[captured.ProviderId] = fetched;
                                if (!string.IsNullOrEmpty(rawBody))
                                {
                                    _cloudRawBodyCache[captured.ProviderId] = (rawBody, captured.Region, now);
                                }
                                return fetched;
                            }));
                        }
                    }
                    else
                    {
                        _cloudCache.TryRemove(cid, out _);
                        _cloudRawBodyCache.TryRemove(cid, out _);
                        cloudTasks.Add(Task.FromResult(CustomWidgetAiMonitorEngine.CreateUnconfiguredSnapshot(cid, now, culture)));
                    }
                }

                CustomWidgetAiProviderSnapshot[] cloudResults = await Task.WhenAll(cloudTasks);
                if (shouldFetchCloud)
                {
                    _lastCloudFetchUtc = now;
                }

                var all = new List<CustomWidgetAiProviderSnapshot>(6)
                {
                    claudeSnap,
                    codexSnap
                };
                all.AddRange(cloudResults);

                List<CustomWidgetAiProviderSnapshot> active = all
                    .Where(static p => p.IsConfigured)
                    .ToList();

                var snapshot = new CustomWidgetAiMonitorSnapshot(
                    AllProviders: all,
                    ActiveProviders: active,
                    LocalSummary: localSummary,
                    UpdatedAt: now);

                if (_subscribers.IsEmpty)
                {
                    _claudeCache.Clear();
                    _codexCache.Clear();
                }

                _latest = snapshot;
                SnapshotUpdated?.Invoke(snapshot);
                return snapshot;
            });
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ReconfigureTimer()
    {
        lock (_timerLock)
        {
            if (_subscribers.IsEmpty)
            {
                _timer?.Dispose();
                _timer = null;
                _claudeCache.Clear();
                _codexCache.Clear();
                _cloudCache.Clear();
                _cloudRawBodyCache.Clear();
                _lastCloudFetchUtc = DateTimeOffset.MinValue;
                return;
            }

            int minMs = _subscribers.Values.Min();
            if (_timer is null)
            {
                _timer = new Timer(_ => _ = RefreshNowAsync(forceCloudRefresh: false), null, minMs, minMs);
            }
            else
            {
                _timer.Change(minMs, minMs);
            }
        }
    }
}
