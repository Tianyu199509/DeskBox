using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;
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
    DateTimeOffset UpdatedAt,
    double? WalletTodaySpend = null,
    bool IsPaceHot = false);

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
    IReadOnlyList<CustomWidgetAiDailyBucket> Last28Days,
    int DetectedGeminiFiles = 0)
{
    public IReadOnlyList<CustomWidgetAiDailyBucket> EffectiveLast28Days => Last28Days;
}

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
    string ThinkingMode = "auto",
    IReadOnlyList<string>? FetchedModels = null,
    string ContextWindowMode = "auto");

/// <summary>
/// Per-user Windows DPAPI (<c>CryptProtectData</c> / <c>CryptUnprotectData</c>) credential store
/// for AI API keys. Stored in <c>%LOCALAPPDATA%\DeskBox\data\ai-credentials.json</c>, completely
/// outside <c>manifest.json</c> and <c>store.json</c> so <c>.dbxwidget</c> exports never leak keys.
/// </summary>
public sealed class CustomWidgetAiCredentialStore
{
    private static readonly Lazy<CustomWidgetAiCredentialStore> SharedInstance =
        new(() => new CustomWidgetAiCredentialStore(null, watchExternalChanges: true));

    private readonly object _sync = new();
    private readonly string _filePath;
    private Dictionary<string, AiProviderCredentialEntry>? _cache;
    private long _lastLoadedWriteUtcTicks = -1;
    private long _generation = 1;
    private byte[]? _lastLoadedContentHash;
    private FileSystemWatcher? _watcher;
    private Timer? _watcherDebounceTimer;

    public CustomWidgetAiCredentialStore(string? rootDataDirectory = null, bool watchExternalChanges = false)
    {
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "ai-credentials.json");
        if (watchExternalChanges)
        {
            TryStartFileWatcher(baseDir);
        }
    }

    public static CustomWidgetAiCredentialStore Shared => SharedInstance.Value;

    public string FilePath => _filePath;

    public long Generation => Interlocked.Read(ref _generation);

    public event EventHandler? CredentialsChanged;

    private void RaiseCredentialsChanged() =>
        CredentialsChanged?.Invoke(this, EventArgs.Empty);

    public void InvalidateCache()
    {
        lock (_sync)
        {
            _cache = null;
            _lastLoadedWriteUtcTicks = -1;
            _lastLoadedContentHash = null;
            Interlocked.Increment(ref _generation);
        }
    }

    public static IReadOnlyList<string>? NormalizeModelCatalog(IEnumerable<string>? models)
    {
        if (models is null)
        {
            return null;
        }

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in models)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.Count > 0 ? normalized : null;
    }

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
        // Validate DPAPI protection upfront so a transient failure never mutates in-memory or on-disk secrets.
        if (!string.IsNullOrWhiteSpace(entry.ApiKey))
        {
            _ = ProtectString(entry.ApiKey);
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            _cache![entry.ProviderId] = entry with
            {
                Region = AiProviderCatalog.NormalizeRegion(entry.Region),
                FetchedModels = NormalizeModelCatalog(entry.FetchedModels)
            };
            SaveNoLock();
        }

        RaiseCredentialsChanged();
    }

    public void RemoveKey(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        bool changed = false;
        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (_cache!.TryGetValue(providerId, out AiProviderCredentialEntry? existing))
            {
                _cache[providerId] = existing with
                {
                    ApiKey = string.Empty,
                    FetchedModels = null
                };
                SaveNoLock();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseCredentialsChanged();
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

    public bool CheckForExternalChangesAndNotify()
    {
        bool reloaded = false;
        lock (_sync)
        {
            long currentTicks = File.Exists(_filePath)
                ? File.GetLastWriteTimeUtc(_filePath).Ticks
                : 0;
            if (ShouldReloadFromExternalDiskChangeNoLock(currentTicks))
            {
                EnsureLoadedNoLock();
                reloaded = true;
            }
        }

        if (reloaded)
        {
            RaiseCredentialsChanged();
        }

        return reloaded;
    }

    private void TryStartFileWatcher(string baseDir)
    {
        try
        {
            _watcherDebounceTimer = new Timer(
                _ =>
                {
                    try
                    {
                        CheckForExternalChangesAndNotify();
                    }
                    catch
                    {
                    }
                },
                null,
                Timeout.Infinite,
                Timeout.Infinite);

            var watcher = new FileSystemWatcher(baseDir, "ai-credentials.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler schedule = (_, _) => ScheduleDebouncedExternalCheck();
            RenamedEventHandler scheduleRename = (_, _) => ScheduleDebouncedExternalCheck();
            watcher.Changed += schedule;
            watcher.Created += schedule;
            watcher.Deleted += schedule;
            watcher.Renamed += scheduleRename;
            _watcher = watcher;
        }
        catch
        {
            // Fallback to activation-driven checks when FileSystemWatcher is unavailable.
        }
    }

    private void ScheduleDebouncedExternalCheck()
    {
        try
        {
            _watcherDebounceTimer?.Change(120, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool ShouldReloadFromExternalDiskChangeNoLock(long currentTicks)
    {
        if (_cache is null)
        {
            return CredentialsChanged is not null && currentTicks != 0;
        }

        if (currentTicks == 0)
        {
            if (_lastLoadedWriteUtcTicks == 0 && _lastLoadedContentHash is null)
            {
                return false;
            }

            _lastLoadedWriteUtcTicks = -1;
            _lastLoadedContentHash = null;
            return true;
        }

        try
        {
            byte[] rawBytes = ReadCredentialBytesShared(_filePath);
            byte[] currentHash = SHA256.HashData(rawBytes);
            if (_lastLoadedContentHash is not null &&
                currentHash.AsSpan().SequenceEqual(_lastLoadedContentHash))
            {
                _lastLoadedWriteUtcTicks = currentTicks;
                return false;
            }

            _lastLoadedWriteUtcTicks = -1;
            return true;
        }
        catch
        {
            return _lastLoadedWriteUtcTicks != currentTicks;
        }
    }

    private static byte[] ReadCredentialBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
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

        if (_cache is not null)
        {
            Interlocked.Increment(ref _generation);
        }

        _lastLoadedWriteUtcTicks = currentTicks;
        _cache = new Dictionary<string, AiProviderCredentialEntry>(StringComparer.OrdinalIgnoreCase);
        if (currentTicks == 0)
        {
            _lastLoadedContentHash = null;
            return;
        }

        try
        {
            byte[] rawBytes = ReadCredentialBytesShared(_filePath);
            _lastLoadedContentHash = SHA256.HashData(rawBytes);
            string raw = Encoding.UTF8.GetString(rawBytes);
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
                    ? AiProviderCatalog.NormalizeRegion(rEl.GetString())
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
                string contextWindowMode = prop.Value.TryGetProperty("contextWindowMode", out JsonElement cwEl) && cwEl.ValueKind == JsonValueKind.String
                    ? AiProviderCatalog.ToContextWindowWireValue(AiProviderCatalog.ParseContextWindowMode(cwEl.GetString()))
                    : "auto";
                IReadOnlyList<string>? fetchedModels = null;
                if (prop.Value.TryGetProperty("fetchedModels", out JsonElement fmEl) &&
                    fmEl.ValueKind == JsonValueKind.Array)
                {
                    fetchedModels = NormalizeModelCatalog(
                        fmEl.EnumerateArray()
                            .Where(static el => el.ValueKind == JsonValueKind.String)
                            .Select(static el => el.GetString() ?? string.Empty));
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
                    ThinkingMode: thinkingMode,
                    FetchedModels: fetchedModels,
                    ContextWindowMode: contextWindowMode);
            }
        }
        catch
        {
            // Corrupted credentials file falls back cleanly to empty dictionary.
        }
    }

    public void UpdateFetchedModels(
        string providerId,
        IReadOnlyList<string> fetchedModels,
        string? preferredModel = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        IReadOnlyList<string>? normalized = NormalizeModelCatalog(fetchedModels);
        if (normalized is null)
        {
            return;
        }

        string id = providerId.Trim();
        bool changed = false;
        lock (_sync)
        {
            EnsureLoadedNoLock();
            AiProviderCredentialEntry existing = _cache!.TryGetValue(id, out AiProviderCredentialEntry? cur)
                ? cur
                : new AiProviderCredentialEntry(id, string.Empty);

            CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(id);
            string updatedModel = existing.CustomModel;
            string updatedThinking = existing.ThinkingMode;

            string? preferredMatch = !string.IsNullOrWhiteSpace(preferredModel)
                ? normalized.FirstOrDefault(m => string.Equals(m, preferredModel.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (preferredMatch is not null)
            {
                (updatedModel, updatedThinking) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
                    preset,
                    existing.CustomModel,
                    existing.ThinkingMode,
                    newChosenModel: preferredMatch,
                    newThinkingWire: null,
                    availableModels: normalized);
            }
            else if (!string.IsNullOrWhiteSpace(existing.CustomModel) &&
                     normalized.FirstOrDefault(m => string.Equals(m, existing.CustomModel.Trim(), StringComparison.OrdinalIgnoreCase)) is { } existingMatch)
            {
                updatedModel = existingMatch;
            }
            else
            {
                (updatedModel, updatedThinking) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
                    preset,
                    existing.CustomModel,
                    existing.ThinkingMode,
                    newChosenModel: normalized[0],
                    newThinkingWire: null,
                    availableModels: normalized);
            }

            bool catalogUnchanged = existing.FetchedModels is not null &&
                                    existing.FetchedModels.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase) &&
                                    string.Equals(existing.CustomModel, updatedModel, StringComparison.Ordinal) &&
                                    string.Equals(existing.ThinkingMode, updatedThinking, StringComparison.Ordinal);
            if (!catalogUnchanged)
            {
                _cache[id] = existing with
                {
                    FetchedModels = normalized,
                    CustomModel = updatedModel,
                    ThinkingMode = updatedThinking
                };
                SaveNoLock();
                changed = true;
            }
        }

        if (changed)
        {
            RaiseCredentialsChanged();
        }
    }

    public void UpdateStudioSettings(
        string providerId,
        string? customEndpoint = null,
        string? customModel = null,
        string? thinkingMode = null,
        IReadOnlyList<string>? fetchedModels = null,
        string? contextWindowMode = null)
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

            CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(id);
            string updatedEndpoint = customEndpoint is null ? existing.CustomEndpoint : customEndpoint.Trim();
            string prevEffectiveUrl = AiProviderCatalog.ResolveConfiguredEndpointUrl(preset, existing.Region, existing.CustomEndpoint);
            string nextEffectiveUrl = AiProviderCatalog.ResolveConfiguredEndpointUrl(preset, existing.Region, updatedEndpoint);
            bool endpointChanged = customEndpoint is not null &&
                                   !string.Equals(prevEffectiveUrl, nextEffectiveUrl, StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<string>? effectiveFetched = fetchedModels is not null
                ? NormalizeModelCatalog(fetchedModels)
                : (endpointChanged ? null : existing.FetchedModels);

            string updatedModel = existing.CustomModel;
            string updatedThinking = existing.ThinkingMode;
            string updatedContextWindow = contextWindowMode is null
                ? AiProviderCatalog.ToContextWindowWireValue(AiProviderCatalog.ParseContextWindowMode(existing.ContextWindowMode))
                : AiProviderCatalog.ToContextWindowWireValue(AiProviderCatalog.ParseContextWindowMode(contextWindowMode));
            string fallbackDefaultModel = effectiveFetched is { Count: > 0 } ? effectiveFetched[0] : preset.DefaultModel;

            if (customModel is not null || thinkingMode is not null)
            {
                string? requestedModel = customModel is null
                    ? null
                    : (string.IsNullOrWhiteSpace(customModel) ? fallbackDefaultModel : customModel.Trim());
                (updatedModel, updatedThinking) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
                    preset,
                    existing.CustomModel,
                    existing.ThinkingMode,
                    newChosenModel: requestedModel,
                    newThinkingWire: thinkingMode,
                    availableModels: effectiveFetched);
            }

            _cache[id] = existing with
            {
                CustomEndpoint = updatedEndpoint,
                CustomModel = updatedModel,
                ThinkingMode = updatedThinking,
                FetchedModels = effectiveFetched,
                ContextWindowMode = updatedContextWindow
            };
            SaveNoLock();
        }

        RaiseCredentialsChanged();
    }

    private void SaveNoLock()
    {
        Interlocked.Increment(ref _generation);
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
                    if (!string.IsNullOrWhiteSpace(entry.ContextWindowMode) &&
                        !string.Equals(entry.ContextWindowMode, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteString("contextWindowMode", entry.ContextWindowMode);
                    }
                    if (entry.FetchedModels is { Count: > 0 })
                    {
                        writer.WriteStartArray("fetchedModels");
                        foreach (string modelId in entry.FetchedModels)
                        {
                            if (!string.IsNullOrWhiteSpace(modelId))
                            {
                                writer.WriteStringValue(modelId.Trim());
                            }
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        string tempPath = _filePath + ".tmp";
        byte[] payload = ms.ToArray();
        File.WriteAllBytes(tempPath, payload);
        CustomWidgetStorageFileOps.ReplaceJsonFileAtomically(tempPath, _filePath);
        _lastLoadedContentHash = SHA256.HashData(payload);
        _lastLoadedWriteUtcTicks = File.Exists(_filePath)
            ? File.GetLastWriteTimeUtc(_filePath).Ticks
            : 0;
    }

    internal static string ProtectString(string plaintext) =>
        DpapiSecretProtector.ProtectUtf8ToBase64(plaintext);

    internal static string UnprotectString(string base64Cipher) =>
        DpapiSecretProtector.UnprotectBase64ToUtf8(base64Cipher);
}

public sealed record AiMonitorProviderPreference(
    string ProviderId,
    bool? Enabled = null,
    string? PlanTier = null,
    string? Region = null,
    string? ApiKey = null,
    string? CustomEndpoint = null);

/// <summary>
/// Per-user AI Monitor (<c>ai-pulse</c>) preferences and optional monitor-only DPAPI credentials
/// (<c>ai-monitor-prefs.json</c>). Decouples monitor visibility, plan tiers, and monitor-specific
/// endpoints/keys (e.g. OneAPI / MiniMax) from global Studio credentials in <see cref="CustomWidgetAiCredentialStore"/>.
/// </summary>
public sealed class CustomWidgetAiMonitorPreferencesStore
{
    private static readonly Lazy<CustomWidgetAiMonitorPreferencesStore> SharedInstance =
        new(() => new CustomWidgetAiMonitorPreferencesStore(null));

    private readonly object _sync = new();
    private readonly string _filePath;
    private Dictionary<string, AiMonitorProviderPreference>? _cache;
    private long _lastLoadedWriteUtcTicks = -1;

    public CustomWidgetAiMonitorPreferencesStore(string? rootDataDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? DeskBoxDataPathService.Current.DataDirectory
            : rootDataDirectory;
        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, "ai-monitor-prefs.json");
    }

    public static CustomWidgetAiMonitorPreferencesStore Shared => SharedInstance.Value;

    public string FilePath => _filePath;

    public event EventHandler? PreferencesChanged;

    private void RaisePreferencesChanged() =>
        PreferencesChanged?.Invoke(this, EventArgs.Empty);

    public void InvalidateCache()
    {
        lock (_sync)
        {
            _cache = null;
            _lastLoadedWriteUtcTicks = -1;
        }
    }

    public IReadOnlyDictionary<string, AiMonitorProviderPreference> GetAll()
    {
        lock (_sync)
        {
            EnsureLoadedNoLock();
            return new Dictionary<string, AiMonitorProviderPreference>(_cache!, StringComparer.OrdinalIgnoreCase);
        }
    }

    public AiMonitorProviderPreference? Get(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            return _cache!.TryGetValue(providerId.Trim(), out AiMonitorProviderPreference? pref)
                ? pref
                : null;
        }
    }

    public void Upsert(AiMonitorProviderPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (string.IsNullOrWhiteSpace(preference.ProviderId))
        {
            return;
        }

        string id = preference.ProviderId.Trim();
        if (!string.IsNullOrWhiteSpace(preference.ApiKey))
        {
            _ = DpapiSecretProtector.ProtectUtf8ToBase64(preference.ApiKey.Trim());
        }

        lock (_sync)
        {
            EnsureLoadedNoLock();
            _cache![id] = preference with
            {
                ProviderId = id,
                Region = string.IsNullOrWhiteSpace(preference.Region)
                    ? null
                    : AiProviderCatalog.NormalizeRegion(preference.Region),
                PlanTier = string.IsNullOrWhiteSpace(preference.PlanTier) ? null : preference.PlanTier.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(preference.ApiKey) ? null : preference.ApiKey.Trim(),
                CustomEndpoint = string.IsNullOrWhiteSpace(preference.CustomEndpoint) ? null : preference.CustomEndpoint.Trim()
            };
            SaveNoLock();
        }

        RaisePreferencesChanged();
    }

    public void SetProviderMonitorEnabled(string providerId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        string id = providerId.Trim();
        lock (_sync)
        {
            EnsureLoadedNoLock();
            AiMonitorProviderPreference existing = _cache!.TryGetValue(id, out AiMonitorProviderPreference? cur)
                ? cur
                : new AiMonitorProviderPreference(id);
            _cache[id] = existing with { Enabled = enabled };
            SaveNoLock();
        }

        RaisePreferencesChanged();
    }

    public void RemoveMonitorKeyOrHideProvider(
        string providerId,
        CustomWidgetAiCredentialStore? globalStore = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        string id = providerId.Trim();
        bool isLocalCli = id.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase) ||
                          id.Equals(CustomWidgetAiMonitorEngine.ProviderCodex, StringComparison.OrdinalIgnoreCase) ||
                          id.Equals(CustomWidgetAiMonitorEngine.ProviderGeminiCli, StringComparison.OrdinalIgnoreCase);

        AiProviderCredentialEntry? globalEntry = (globalStore ?? CustomWidgetAiCredentialStore.Shared).Get(id);
        bool hasGlobalKey = !string.IsNullOrWhiteSpace(globalEntry?.ApiKey);

        lock (_sync)
        {
            EnsureLoadedNoLock();
            if (hasGlobalKey || isLocalCli)
            {
                AiMonitorProviderPreference existing = _cache!.TryGetValue(id, out AiMonitorProviderPreference? cur)
                    ? cur
                    : new AiMonitorProviderPreference(id);
                _cache[id] = existing with
                {
                    Enabled = false,
                    ApiKey = null,
                    CustomEndpoint = null
                };
            }
            else
            {
                _cache!.Remove(id);
            }

            SaveNoLock();
        }

        RaisePreferencesChanged();
    }

    public AiProviderCredentialEntry? ResolveEffectiveCredential(
        string providerId,
        CustomWidgetAiCredentialStore? globalStore = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        string id = providerId.Trim();
        AiMonitorProviderPreference? pref = Get(id);
        AiProviderCredentialEntry? global = (globalStore ?? CustomWidgetAiCredentialStore.Shared).Get(id);
        return MergeMonitorAndGlobalCredential(id, pref, global);
    }

    public IReadOnlyDictionary<string, AiProviderCredentialEntry> ResolveAllEffectiveCredentials(
        CustomWidgetAiCredentialStore? globalStore = null)
    {
        IReadOnlyDictionary<string, AiMonitorProviderPreference> prefs = GetAll();
        IReadOnlyDictionary<string, AiProviderCredentialEntry> globals = (globalStore ?? CustomWidgetAiCredentialStore.Shared).GetAll();
        var merged = new Dictionary<string, AiProviderCredentialEntry>(StringComparer.OrdinalIgnoreCase);

        foreach ((string id, AiProviderCredentialEntry globalEntry) in globals)
        {
            prefs.TryGetValue(id, out AiMonitorProviderPreference? pref);
            if (MergeMonitorAndGlobalCredential(id, pref, globalEntry) is { } eff)
            {
                merged[id] = eff;
            }
        }

        foreach ((string id, AiMonitorProviderPreference pref) in prefs)
        {
            if (merged.ContainsKey(id))
            {
                continue;
            }

            if (MergeMonitorAndGlobalCredential(id, pref, null) is { } eff)
            {
                merged[id] = eff;
            }
        }

        return merged;
    }

    private static AiProviderCredentialEntry? MergeMonitorAndGlobalCredential(
        string providerId,
        AiMonitorProviderPreference? pref,
        AiProviderCredentialEntry? global)
    {
        if (pref is null && global is null)
        {
            return null;
        }

        bool enabled = pref?.Enabled ?? (global?.Enabled ?? true);
        string apiKey = !string.IsNullOrWhiteSpace(pref?.ApiKey)
            ? pref.ApiKey!.Trim()
            : (global?.ApiKey ?? string.Empty);
        string region = !string.IsNullOrWhiteSpace(pref?.Region)
            ? AiProviderCatalog.NormalizeRegion(pref.Region)
            : AiProviderCatalog.NormalizeRegion(global?.Region);
        string planTier = !string.IsNullOrWhiteSpace(pref?.PlanTier)
            ? pref.PlanTier!.Trim()
            : (global?.PlanTier ?? "pro");
        string customEndpoint = !string.IsNullOrWhiteSpace(pref?.CustomEndpoint)
            ? pref.CustomEndpoint!.Trim()
            : (global?.CustomEndpoint ?? string.Empty);

        return new AiProviderCredentialEntry(
            ProviderId: providerId,
            ApiKey: apiKey,
            Region: region,
            PlanTier: planTier,
            Enabled: enabled,
            CustomEndpoint: customEndpoint);
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
        _cache = new Dictionary<string, AiMonitorProviderPreference>(StringComparer.OrdinalIgnoreCase);
        if (currentTicks == 0)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument doc = JsonDocument.Parse(stream);
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

                bool? enabled = null;
                if (prop.Value.TryGetProperty("enabled", out JsonElement eEl) &&
                    eEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    enabled = eEl.GetBoolean();
                }

                string? planTier = prop.Value.TryGetProperty("planTier", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.String
                    ? pEl.GetString()
                    : null;
                string? region = prop.Value.TryGetProperty("region", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                    ? AiProviderCatalog.NormalizeRegion(rEl.GetString())
                    : null;
                string? customEndpoint = prop.Value.TryGetProperty("customEndpoint", out JsonElement ceEl) && ceEl.ValueKind == JsonValueKind.String
                    ? ceEl.GetString()
                    : null;
                string? cipher = prop.Value.TryGetProperty("cipher", out JsonElement cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString()
                    : null;
                string? apiKey = string.IsNullOrWhiteSpace(cipher)
                    ? null
                    : DpapiSecretProtector.UnprotectBase64ToUtf8(cipher);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    apiKey = null;
                }

                _cache[prop.Name] = new AiMonitorProviderPreference(
                    ProviderId: prop.Name,
                    Enabled: enabled,
                    PlanTier: string.IsNullOrWhiteSpace(planTier) ? null : planTier.Trim(),
                    Region: region,
                    ApiKey: apiKey,
                    CustomEndpoint: string.IsNullOrWhiteSpace(customEndpoint) ? null : customEndpoint.Trim());
            }
        }
        catch
        {
            // Corrupted preferences file falls back cleanly to empty dictionary.
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
                foreach ((string id, AiMonitorProviderPreference pref) in _cache)
                {
                    writer.WriteStartObject(id);
                    if (pref.Enabled.HasValue)
                    {
                        writer.WriteBoolean("enabled", pref.Enabled.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(pref.PlanTier))
                    {
                        writer.WriteString("planTier", pref.PlanTier);
                    }
                    if (!string.IsNullOrWhiteSpace(pref.Region))
                    {
                        writer.WriteString("region", AiProviderCatalog.NormalizeRegion(pref.Region));
                    }
                    if (!string.IsNullOrWhiteSpace(pref.CustomEndpoint))
                    {
                        writer.WriteString("customEndpoint", pref.CustomEndpoint);
                    }
                    if (!string.IsNullOrWhiteSpace(pref.ApiKey))
                    {
                        writer.WriteString("cipher", DpapiSecretProtector.ProtectUtf8ToBase64(pref.ApiKey));
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
}

/// <summary>
/// Shared background telemetry &amp; quota coordinator for <c>deskbox.ai-pulse</c>.
/// Keeps file &amp; network I/O completely off the UI thread with incremental file caching.
/// </summary>
public sealed class CustomWidgetAiMonitorSource
{
    public static CustomWidgetAiMonitorSource Shared { get; } = new();

    private int _suppressCredentialEventCount;

    private CustomWidgetAiMonitorSource()
    {
        EventHandler onConfigChanged = (_, _) =>
        {
            if (Volatile.Read(ref _suppressCredentialEventCount) > 0)
            {
                return;
            }

            _cloudCache.Clear();
            _cloudRawBodyCache.Clear();
            if (!_subscribers.IsEmpty)
            {
                _ = RefreshNowAsync(forceCloudRefresh: true);
            }
        };

        CustomWidgetAiCredentialStore.Shared.CredentialsChanged += onConfigChanged;
        CustomWidgetAiMonitorPreferencesStore.Shared.PreferencesChanged += onConfigChanged;
    }

    private readonly HttpClient _httpClient = new(new DeskBoxAiService.SameOriginAuthSafeRedirectHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectCallback = CustomWidgetNetworkSecurity.ConnectToValidatedLocalOrPublicIpAsync
    }))
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly ConcurrentDictionary<object, int> _subscribers = new();
    private readonly ConcurrentDictionary<string, CustomWidgetAiMonitorEngine.CachedClaudeFileSummary> _claudeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CustomWidgetAiMonitorEngine.CachedCodexFileSummary> _codexCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CustomWidgetAiMonitorEngine.CachedGeminiFileSummary> _geminiCache =
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

    public AiProviderCredentialEntry? GetEffectiveCredential(string providerId) =>
        CustomWidgetAiMonitorPreferencesStore.Shared.ResolveEffectiveCredential(providerId, CustomWidgetAiCredentialStore.Shared);

    public async Task<(bool Success, string Message, CustomWidgetAiProviderSnapshot Snapshot)> TestAndSaveCredentialAsync(
        AiProviderCredentialEntry entry,
        CancellationToken cancellationToken = default,
        string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        string normalizedRegion = AiProviderCatalog.NormalizeRegion(entry.Region);
        AiMonitorProviderPreference? prevPref = CustomWidgetAiMonitorPreferencesStore.Shared.Get(entry.ProviderId);

        if (entry.ProviderId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase) ||
            entry.ProviderId.Equals(CustomWidgetAiMonitorEngine.ProviderCodex, StringComparison.OrdinalIgnoreCase) ||
            entry.ProviderId.Equals(CustomWidgetAiMonitorEngine.ProviderGeminiCli, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _configMutationGeneration);
            Interlocked.Increment(ref _suppressCredentialEventCount);
            try
            {
                CustomWidgetAiMonitorPreferencesStore.Shared.Upsert(new AiMonitorProviderPreference(
                    ProviderId: entry.ProviderId,
                    Enabled: entry.Enabled,
                    PlanTier: entry.PlanTier,
                    Region: normalizedRegion,
                    ApiKey: string.IsNullOrWhiteSpace(entry.ApiKey) ? null : entry.ApiKey.Trim(),
                    CustomEndpoint: string.IsNullOrWhiteSpace(entry.CustomEndpoint) ? null : entry.CustomEndpoint.Trim()));
            }
            finally
            {
                Interlocked.Decrement(ref _suppressCredentialEventCount);
            }

            await RefreshNowAsync(forceCloudRefresh: false, waitIfBusy: true);
            CustomWidgetAiProviderSnapshot? localSnap = _latest.AllProviders.FirstOrDefault(
                p => p.Id.Equals(entry.ProviderId, StringComparison.OrdinalIgnoreCase));
            return (
                true,
                LocalizationService.TranslateForCulture(c, "CustomWidget.AiMonitor.LocalConfigUpdated"),
                localSnap ?? CustomWidgetAiMonitorEngine.CreateUnconfiguredSnapshot(entry.ProviderId, DateTimeOffset.Now, c));
        }

        AiProviderCredentialEntry candidateEntry = entry with
        {
            Region = normalizedRegion,
            ApiKey = string.IsNullOrWhiteSpace(entry.ApiKey)
                ? (GetEffectiveCredential(entry.ProviderId)?.ApiKey ?? string.Empty)
                : entry.ApiKey.Trim(),
            CustomEndpoint = string.IsNullOrWhiteSpace(entry.CustomEndpoint)
                ? (GetEffectiveCredential(entry.ProviderId)?.CustomEndpoint ?? string.Empty)
                : entry.CustomEndpoint.Trim()
        };

        DateTimeOffset fetchedAt = DateTimeOffset.Now;
        var (snap, rawBody) = await CustomWidgetAiMonitorEngine.FetchCloudProviderInternalAsync(
            _httpClient,
            candidateEntry,
            fetchedAt,
            cancellationToken,
            c);

        bool hasWorkingFallback = prevPref is null &&
                                  CustomWidgetAiCredentialStore.Shared.Get(entry.ProviderId) is { ApiKey.Length: > 0 };
        if (snap.ErrorMessage is not null && hasWorkingFallback)
        {
            // Do not persist a mistyped monitor override key that would permanently shadow a valid global key.
            return (false, snap.ErrorMessage, snap);
        }

        Interlocked.Increment(ref _configMutationGeneration);
        Interlocked.Increment(ref _suppressCredentialEventCount);
        try
        {
            CustomWidgetAiMonitorPreferencesStore.Shared.Upsert(new AiMonitorProviderPreference(
                ProviderId: entry.ProviderId,
                Enabled: entry.Enabled,
                PlanTier: entry.PlanTier,
                Region: normalizedRegion,
                ApiKey: string.IsNullOrWhiteSpace(entry.ApiKey) ? null : entry.ApiKey.Trim(),
                CustomEndpoint: string.IsNullOrWhiteSpace(entry.CustomEndpoint) ? null : entry.CustomEndpoint.Trim()));
        }
        finally
        {
            Interlocked.Decrement(ref _suppressCredentialEventCount);
        }

        if (snap.ErrorMessage is null)
        {
            _cloudCache[entry.ProviderId] = snap;
            if (!string.IsNullOrEmpty(rawBody))
            {
                _cloudRawBodyCache[entry.ProviderId] = (rawBody, candidateEntry.Region, fetchedAt);
            }
        }
        await RefreshNowAsync(forceCloudRefresh: false, waitIfBusy: true);

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

    private long _configMutationGeneration;

    public async Task SetProviderMonitorEnabledAsync(string providerId, bool enabled)
    {
        Interlocked.Increment(ref _configMutationGeneration);
        Interlocked.Increment(ref _suppressCredentialEventCount);
        try
        {
            CustomWidgetAiMonitorPreferencesStore.Shared.SetProviderMonitorEnabled(providerId, enabled);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressCredentialEventCount);
        }

        if (!enabled)
        {
            _cloudCache.TryRemove(providerId, out _);
            _cloudRawBodyCache.TryRemove(providerId, out _);
        }

        await RefreshNowAsync(forceCloudRefresh: enabled, waitIfBusy: true);
    }

    public async Task RemoveCredentialAsync(string providerId)
    {
        Interlocked.Increment(ref _configMutationGeneration);
        Interlocked.Increment(ref _suppressCredentialEventCount);
        try
        {
            CustomWidgetAiMonitorPreferencesStore.Shared.RemoveMonitorKeyOrHideProvider(
                providerId,
                CustomWidgetAiCredentialStore.Shared);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressCredentialEventCount);
        }
        _cloudCache.TryRemove(providerId, out _);
        _cloudRawBodyCache.TryRemove(providerId, out _);
        await RefreshNowAsync(forceCloudRefresh: false, waitIfBusy: true);
    }

    public Task<CustomWidgetAiMonitorSnapshot> RefreshNowAsync(bool forceCloudRefresh = true) =>
        RefreshNowAsync(forceCloudRefresh, waitIfBusy: forceCloudRefresh);

    private async Task<CustomWidgetAiMonitorSnapshot> RefreshNowAsync(bool forceCloudRefresh, bool waitIfBusy)
    {
        if (waitIfBusy)
        {
            await _refreshGate.WaitAsync();
        }
        else if (!await _refreshGate.WaitAsync(0))
        {
            return _latest;
        }

        try
        {
            return await Task.Run(async () =>
            {
                long startGen = Interlocked.Read(ref _configMutationGeneration);
                DateTimeOffset now = DateTimeOffset.Now;
                string culture = LocalizationService.ResolveAmbientCultureName();
                bool cultureChanged = !string.Equals(_lastRefreshCulture, culture, StringComparison.OrdinalIgnoreCase);
                _lastRefreshCulture = culture;

                IReadOnlyDictionary<string, AiProviderCredentialEntry> creds =
                    CustomWidgetAiMonitorPreferencesStore.Shared.ResolveAllEffectiveCredentials(
                        CustomWidgetAiCredentialStore.Shared);

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string claudeDir = ResolveClaudeProjectsDirectory(home);
                string codexDir = ResolveCodexSessionsDirectory(home);
                string geminiDir = ResolveGeminiCliDirectory(home);

                bool claudeEnabled = !creds.TryGetValue(CustomWidgetAiMonitorEngine.ProviderClaude, out var claudeCred) || claudeCred.Enabled;
                string claudeTier = claudeCred?.PlanTier ?? "pro";
                bool codexEnabled = !creds.TryGetValue(CustomWidgetAiMonitorEngine.ProviderCodex, out var codexCred) || codexCred.Enabled;
                bool geminiEnabled = !creds.TryGetValue(CustomWidgetAiMonitorEngine.ProviderGeminiCli, out var geminiCred) || geminiCred.Enabled;

                bool shouldFetchCloud = forceCloudRefresh ||
                                        (now - _lastCloudFetchUtc) >= TimeSpan.FromMinutes(3);

                string[] cloudIds =
                [
                    CustomWidgetAiMonitorEngine.ProviderGlm,
                    CustomWidgetAiMonitorEngine.ProviderMiniMax,
                    CustomWidgetAiMonitorEngine.ProviderDeepSeek,
                    CustomWidgetAiMonitorEngine.ProviderSiliconFlow,
                    CustomWidgetAiMonitorEngine.ProviderKimi,
                    CustomWidgetAiMonitorEngine.ProviderOpenRouter,
                    CustomWidgetAiMonitorEngine.ProviderOneApi
                ];

                var cloudTasks = new List<Task<CustomWidgetAiProviderSnapshot>>(cloudIds.Length);
                foreach (string cid in cloudIds)
                {
                    if (creds.TryGetValue(cid, out AiProviderCredentialEntry? entry) &&
                        entry.Enabled &&
                        !string.IsNullOrWhiteSpace(entry.ApiKey))
                    {
                        if (!shouldFetchCloud &&
                            !cultureChanged &&
                            _cloudCache.TryGetValue(cid, out CustomWidgetAiProviderSnapshot? cachedSnap) &&
                            cachedSnap.ErrorMessage is null)
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
                                if (Interlocked.Read(ref _configMutationGeneration) == startGen &&
                                    fetched.ErrorMessage is null)
                                {
                                    _cloudCache[captured.ProviderId] = fetched;
                                    if (!string.IsNullOrEmpty(rawBody))
                                    {
                                        _cloudRawBodyCache[captured.ProviderId] = (rawBody, captured.Region, now);
                                    }
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

                var (geminiSnap, geminiSessions, geminiFiles) = CustomWidgetAiMonitorEngine.AnalyzeGeminiCliDirectory(
                    geminiDir,
                    geminiEnabled,
                    now,
                    _geminiCache,
                    culture);

                CustomWidgetAiProviderSnapshot[] cloudResults = await Task.WhenAll(cloudTasks);
                if (shouldFetchCloud &&
                    cloudResults.Any(static cp => cp.IsConfigured && cp.ErrorMessage is null))
                {
                    _lastCloudFetchUtc = now;
                }

                DateOnly localToday = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
                for (int i = 0; i < cloudResults.Length; i++)
                {
                    CustomWidgetAiProviderSnapshot cp = cloudResults[i];
                    if (cp.BillingKind == AiProviderBillingKind.WalletBalance &&
                        cp.HasLiveData &&
                        cp.ErrorMessage is null &&
                        cp.WalletFormatted != "∞")
                    {
                        double todaySpend = CustomWidgetAiWalletLedger.Shared.RecordObservation(
                            cp.Id,
                            localToday,
                            cp.WalletBalance,
                            cp.CurrencySymbol);
                        if (todaySpend > 0.004)
                        {
                            cloudResults[i] = cp with { WalletTodaySpend = todaySpend };
                        }
                    }
                }
                CustomWidgetAiWalletLedger.Shared.FlushIfDirty();

                IReadOnlyDictionary<DateOnly, double> walletDailyUsd =
                    CustomWidgetAiWalletLedger.Shared.GetDailyUsdSpendRange(localToday.AddDays(-29), localToday);

                CustomWidgetAiLocalSummary localSummary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                    _claudeCache.Values,
                    codexSessions,
                    claudeFiles,
                    codexFiles,
                    now,
                    geminiSessions,
                    geminiFiles,
                    walletDailyUsd);

                var all = new List<CustomWidgetAiProviderSnapshot>(10)
                {
                    claudeSnap,
                    codexSnap,
                    geminiSnap
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
                    _geminiCache.Clear();
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
                _geminiCache.Clear();
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

    internal static string ResolveGeminiCliDirectory(string userProfileHome)
    {
        string? customHome = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        if (!string.IsNullOrWhiteSpace(customHome))
        {
            string trimmed = customHome.Trim();
            string leaf = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(leaf, "tmp", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
            if (string.Equals(leaf, ".gemini", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(trimmed, "tmp");
            }
            return Path.Combine(trimmed, ".gemini", "tmp");
        }

        return Path.Combine(userProfileHome, ".gemini", "tmp");
    }

    internal static string ResolveClaudeProjectsDirectory(string userProfileHome)
    {
        string? customDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(customDir))
        {
            string trimmed = customDir.Trim();
            string leaf = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(leaf, "projects", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : Path.Combine(trimmed, "projects");
        }

        return Path.Combine(userProfileHome, ".claude", "projects");
    }

    internal static string ResolveCodexSessionsDirectory(string userProfileHome)
    {
        string? customHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(customHome))
        {
            string trimmed = customHome.Trim();
            string leaf = Path.GetFileName(trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(leaf, "sessions", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : Path.Combine(trimmed, "sessions");
        }

        return Path.Combine(userProfileHome, ".codex", "sessions");
    }
}
