using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(
    typeof(MusicWidgetSettings),
    TypeInfoPropertyName = "Settings")]
internal sealed partial class MusicSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Per-kind settings store for the Music feature (pluginization roadmap
/// stage 2 pilot). Owns data/music/settings.json, deliberately separate from
/// AppSettings like GlanceWidgetStore. The three legacy AppSettings fields
/// (MusicUseArtworkBackdrop, MusicEnableCoverHoverMotion, MusicDisplayMode)
/// are copied here by Migration_9_To_10 and kept as a real mirror (persisted
/// via the global settings debounce) until the N+2 cleanup release removes
/// them.
///
/// Write-path design (batch E fix + consistency fix): the cache lock is a
/// plain monitor lock held only for in-memory operations, so UI-thread
/// callers can never deadlock on a pending disk write. Disk persistence is
/// serialized through a single background chain enqueued under the cache
/// lock, so disk commit order always matches logical update order and one
/// failed write never blocks later ones (exceptions are observed and
/// logged). The store is consumed through the process-wide <see cref="Current"/>
/// singleton: every consumer (settings page, widget) must share one cached
/// state, or a second instance would pin its own stale cache forever. The
/// legacy AppSettings mirror is persisted through the global SaveDebounced
/// by the caller so both stores converge.
/// </summary>
public sealed class MusicSettingsStore
{
    private static readonly object s_currentGate = new();
    private static MusicSettingsStore? s_current;

    private readonly object _lock = new();
    private readonly object _persistGate = new();
    private Task _persistChain = Task.CompletedTask;
    private readonly string _storePath;
    private MusicWidgetSettings? _cached;

    /// <summary>
    /// Process-wide authoritative instance. Consumers must never construct
    /// their own store: each instance caches independently and would never
    /// see another instance's updates (the settings page and the widget read
    /// through this single instance so they observe the same state).
    /// </summary>
    public static MusicSettingsStore Current
    {
        get
        {
            lock (s_currentGate)
            {
                return s_current ??= new MusicSettingsStore();
            }
        }
    }

    internal static void ResetCurrentForTests() => s_current = null;

    public MusicSettingsStore()
        : this(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "music"))
    {
    }

    internal MusicSettingsStore(string musicDataDirectory)
    {
        Directory.CreateDirectory(musicDataDirectory);
        _storePath = Path.Combine(musicDataDirectory, "settings.json");
    }

    internal string StorePath => _storePath;

    /// <summary>
    /// Synchronous load from the in-memory cache; the first call reads the
    /// tiny settings file from disk. Safe to call from UI property setters -
    /// the monitor lock is only held for memory operations and the initial
    /// sequential file read, never for an async write.
    /// </summary>
    public MusicWidgetSettings Load()
    {
        lock (_lock)
        {
            _cached ??= LoadFromDisk();
            return Clone(_cached);
        }
    }

    /// <summary>
    /// Updates the cached settings synchronously (UI-safe), then persists
    /// asynchronously with exception logging. The update action runs under
    /// the lock so concurrent updates serialize against each other; the
    /// persistence write is enqueued under the same lock so the chain order
    /// matches the update order, while disk I/O itself runs outside the lock
    /// and never blocks callers.
    /// </summary>
    public void Update(Action<MusicWidgetSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            _cached ??= LoadFromDisk();
            update(_cached);
            EnqueuePersist(Clone(_cached));
        }
    }

    public Task SaveAsync(MusicWidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            _cached = Normalize(Clone(settings));
            return EnqueuePersist(Clone(_cached));
        }
    }

    /// <summary>
    /// Synchronous, failure-propagating write used by the settings migration
    /// pipeline (schema 9-to-10). Runs entirely synchronous I/O on the
    /// calling thread - never blocks on an async continuation, which would
    /// deadlock a UI-thread startup - and throws on failure so the pipeline
    /// can stop and retry the migration on the next launch.
    /// </summary>
    internal void SaveSynchronously(MusicWidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MusicWidgetSettings snapshot;
        lock (_lock)
        {
            _cached = Normalize(Clone(settings));
            snapshot = Clone(_cached);
        }

        string json = SerializeSnapshot(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        string tempPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_storePath))
            {
                File.Replace(
                    tempPath,
                    _storePath,
                    ResilientJsonStore.GetBackupPath(_storePath),
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _storePath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// Queues the write onto the single persistence chain and returns the
    /// task for this write (awaiting it also drains earlier queued writes).
    /// Called under the cache lock so the chain order can never diverge from
    /// the update order. Failures inside the chain are caught and logged by
    /// <see cref="PersistAsync"/>, so one failed write never faults the chain.
    /// </summary>
    private Task EnqueuePersist(MusicWidgetSettings snapshot)
    {
        lock (_persistGate)
        {
            _persistChain = _persistChain.ContinueWith(
                _ => PersistAsync(snapshot),
                TaskScheduler.Default).Unwrap();
            return _persistChain;
        }
    }

    private async Task PersistAsync(MusicWidgetSettings snapshot)
    {
        try
        {
            await ResilientJsonStore.SaveAsync(
                _storePath,
                SerializeSnapshot(snapshot));
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSettingsStore] Failed to persist '{_storePath}': {ex.Message}");
        }
    }

    private static string SerializeSnapshot(MusicWidgetSettings snapshot) =>
        JsonSerializer.Serialize(snapshot, MusicSettingsJsonContext.Default.Settings);

    /// <summary>
    /// Direct synchronous disk read for the first cache fill. Mirrors the
    /// ResilientJsonStore decision tree synchronously: a corrupt primary is
    /// quarantined, the backup is tried both after a corrupt primary AND
    /// when the primary is simply missing (an orphaned backup must not
    /// silently reset user settings to defaults), and a recovered backup is
    /// restored into the primary slot. The sync bootstrap avoids the async
    /// pipeline (and its SemaphoreSlim) on the UI thread.
    /// </summary>
    private MusicWidgetSettings LoadFromDisk()
    {
        if (File.Exists(_storePath))
        {
            try
            {
                return Normalize(JsonSerializer.Deserialize(
                    File.ReadAllText(_storePath),
                    MusicSettingsJsonContext.Default.Settings));
            }
            catch (Exception ex)
            {
                App.Log($"[MusicSettingsStore] Primary load failed, trying backup: {ex.Message}");
                QuarantineCorruptFile();
            }
        }

        string backupPath = ResilientJsonStore.GetBackupPath(_storePath);
        if (File.Exists(backupPath))
        {
            try
            {
                string backupJson = File.ReadAllText(backupPath);
                MusicWidgetSettings recovered = Normalize(JsonSerializer.Deserialize(
                    backupJson,
                    MusicSettingsJsonContext.Default.Settings));
                TryRestorePrimary(backupJson);
                return recovered;
            }
            catch (Exception ex)
            {
                App.Log($"[MusicSettingsStore] Backup load failed, using defaults: {ex.Message}");
            }
        }

        return new MusicWidgetSettings();
    }

    private void QuarantineCorruptFile()
    {
        string corruptPath = $"{_storePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_storePath, corruptPath);
            App.Log($"[MusicSettingsStore] Preserved corrupt store as '{Path.GetFileName(corruptPath)}'.");
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSettingsStore] Failed to quarantine corrupt store: {ex.Message}");
        }
    }

    private void TryRestorePrimary(string backupJson)
    {
        string tempPath = $"{_storePath}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            File.WriteAllText(tempPath, backupJson);
            File.Move(tempPath, _storePath, overwrite: true);
            App.Log($"[MusicSettingsStore] Restored store from backup.");
        }
        catch (Exception ex)
        {
            // A locked or read-only data directory must not turn a valid
            // backup into an apparent total settings loss.
            App.Log($"[MusicSettingsStore] Backup loaded, but primary restore failed: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
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

    internal static MusicWidgetSettings Normalize(MusicWidgetSettings? settings)
    {
        settings ??= new MusicWidgetSettings();
        if (settings.SchemaVersion < 1)
        {
            settings.SchemaVersion = 1;
        }

        settings.DisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.DisplayMode);
        return settings;
    }

    private static MusicWidgetSettings Clone(MusicWidgetSettings settings) => new()
    {
        SchemaVersion = settings.SchemaVersion,
        UseArtworkBackdrop = settings.UseArtworkBackdrop,
        EnableCoverHoverMotion = settings.EnableCoverHoverMotion,
        DisplayMode = settings.DisplayMode,
    };
}

/// <summary>
/// Music feature settings (the three fields migrated out of AppSettings by
/// schema version 10). Kept inside the store file so the feature inventory
/// grows by exactly one file.
/// </summary>
public sealed class MusicWidgetSettings
{
    public int SchemaVersion { get; set; } = 1;

    public bool UseArtworkBackdrop { get; set; } = true;

    public bool EnableCoverHoverMotion { get; set; } = true;

    public string DisplayMode { get; set; } = SettingsService.MusicDisplayModeAuto;
}
