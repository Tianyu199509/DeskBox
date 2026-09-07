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
/// AppSettings like GlanceWidgetStore, so feature state can later be
/// classified for sync/backup independently and moved out of the global
/// settings file. The three legacy AppSettings fields (MusicUseArtworkBackdrop,
/// MusicEnableCoverHoverMotion, MusicDisplayMode) are copied here by
/// Migration_9_To_10 and remain as an inert compatibility source until the
/// N+2 cleanup release removes them.
/// </summary>
public sealed class MusicSettingsStore
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
    /// Loads settings for synchronous callers: the settings view model
    /// initializes music properties during construction, before async
    /// pipelines are available. File IO on a tiny settings file in
    /// first-use construction mirrors what SettingsService itself does.
    /// </summary>
    public MusicWidgetSettings Load()
    {
        MusicWidgetSettings settings = LoadAsync().GetAwaiter().GetResult();
        return settings;
    }

    public async Task<MusicWidgetSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _cached ??= await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize(
                    json,
                    MusicSettingsJsonContext.Default.Settings)),
                () => new MusicWidgetSettings(),
                nameof(MusicSettingsStore));
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(MusicWidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync();
        try
        {
            _cached = Normalize(Clone(settings));
            await PersistLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistLockedAsync()
    {
        await ResilientJsonStore.SaveAsync(
            _storePath,
            JsonSerializer.Serialize(_cached!, MusicSettingsJsonContext.Default.Settings));
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
