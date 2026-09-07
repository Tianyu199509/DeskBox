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
/// Write-path design (batch E fix): the cache lock is a plain monitor lock
/// held only for in-memory operations, so UI-thread callers can never
/// deadlock on a pending disk write. Disk persistence happens outside the
/// lock as a fire-and-forget task with exception observation; the legacy
/// AppSettings mirror is persisted through the global SaveDebounced by the
/// caller so both stores converge.
/// </summary>
public sealed class MusicSettingsStore
{
    private readonly object _lock = new();
    private readonly string _storePath;
    private MusicWidgetSettings? _cached;

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
    /// the lock so concurrent updates serialize against each other; disk
    /// writes run outside the lock so they never block callers.
    /// </summary>
    public void Update(Action<MusicWidgetSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        MusicWidgetSettings snapshot;
        lock (_lock)
        {
            _cached ??= LoadFromDisk();
            update(_cached);
            snapshot = Clone(_cached);
        }

        _ = PersistAsync(snapshot);
    }

    public async Task SaveAsync(MusicWidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MusicWidgetSettings snapshot;
        lock (_lock)
        {
            _cached = Normalize(Clone(settings));
            snapshot = Clone(_cached);
        }

        await PersistAsync(snapshot);
    }

    private async Task PersistAsync(MusicWidgetSettings snapshot)
    {
        try
        {
            await ResilientJsonStore.SaveAsync(
                _storePath,
                JsonSerializer.Serialize(snapshot, MusicSettingsJsonContext.Default.Settings));
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSettingsStore] Failed to persist '{_storePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Direct synchronous disk read for the first cache fill. Reads the
    /// primary file and falls back to the .bak, then to defaults - a sync
    /// bootstrap that avoids the ResilientJsonStore async pipeline (and its
    /// SemaphoreSlim) on the UI thread.
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
            }

            string backupPath = ResilientJsonStore.GetBackupPath(_storePath);
            if (File.Exists(backupPath))
            {
                try
                {
                    return Normalize(JsonSerializer.Deserialize(
                        File.ReadAllText(backupPath),
                        MusicSettingsJsonContext.Default.Settings));
                }
                catch (Exception ex)
                {
                    App.Log($"[MusicSettingsStore] Backup load failed, using defaults: {ex.Message}");
                }
            }
        }

        return new MusicWidgetSettings();
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
