using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using DeskBox.Models;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(
    typeof(GlanceWidgetData),
    TypeInfoPropertyName = "Preferences")]
internal sealed partial class GlancePreferencesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Owns the preferences for one Glance widget instance. The store is
/// deliberately separate from AppSettings so a future sync layer can classify
/// portable preferences, device-local paths, and disposable media independently.
/// </summary>
public sealed class GlanceWidgetStore
{
    private static readonly ConcurrentDictionary<string, GlanceWidgetStore> WidgetStores =
        new(StringComparer.Ordinal);

    internal static int CachedWidgetStoreCount => WidgetStores.Count;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath;
    private readonly string? _legacyStorePath;
    private GlanceWidgetData? _cached;

    public GlanceWidgetStore()
        : this(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "glance"))
    {
    }

    internal GlanceWidgetStore(string dataDirectory)
        : this(Path.Combine(dataDirectory, "glance.json"), legacyStorePath: null, exactPath: true)
    {
    }

    internal GlanceWidgetStore(string dataDirectory, string widgetId)
        : this(
            Path.Combine(dataDirectory, $"{GetSafeWidgetFileName(widgetId)}.json"),
            legacyStorePath: null,
            exactPath: true)
    {
    }

    private GlanceWidgetStore(string storePath, string? legacyStorePath, bool exactPath)
    {
        _ = exactPath;
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        _storePath = storePath;
        _legacyStorePath = legacyStorePath;
    }

    internal string StorePath => _storePath;

    public static GlanceWidgetStore ForWidget(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        return WidgetStores.GetOrAdd(
            widgetId,
            static id =>
            {
                string glanceDirectory = Path.Combine(
                    DeskBoxDataPathService.Current.DataDirectory,
                    "glance");
                return new GlanceWidgetStore(
                    Path.Combine(
                        glanceDirectory,
                        "widgets",
                        $"{GetSafeWidgetFileName(id)}.json"),
                    Path.Combine(glanceDirectory, "glance.json"),
                    exactPath: true);
            });
    }

    public event EventHandler? Changed;

    public async Task<GlanceWidgetData> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await MigrateLegacyStoreIfNeededLockedAsync();
            _cached ??= await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize(
                    json,
                    GlancePreferencesJsonContext.Default.Preferences)),
                () => new GlanceWidgetData(),
                nameof(GlanceWidgetStore));
            return Clone(_cached);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(GlanceWidgetData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _gate.WaitAsync();
        try
        {
            _cached = Normalize(Clone(data));
            await PersistLockedAsync();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task UpdateAsync(Action<GlanceWidgetData> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync();
        try
        {
            await MigrateLegacyStoreIfNeededLockedAsync();
            _cached ??= await ResilientJsonStore.LoadAsync(
                _storePath,
                json => Normalize(JsonSerializer.Deserialize(
                    json,
                    GlancePreferencesJsonContext.Default.Preferences)),
                () => new GlanceWidgetData(),
                nameof(GlanceWidgetStore));
            update(_cached);
            _cached = Normalize(_cached);
            await PersistLockedAsync();
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task ResetAsync()
    {
        await SaveAsync(new GlanceWidgetData());
    }

    public static async Task DeleteForWidgetAsync(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        GlanceWidgetStore store = ForWidget(widgetId);
        await store.DeleteAsync();
        WidgetStores.TryRemove(widgetId, out _);
    }

    private async Task DeleteAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _cached = null;
            TryDeleteFile(_storePath);
            TryDeleteFile(ResilientJsonStore.GetBackupPath(_storePath));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MigrateLegacyStoreIfNeededLockedAsync()
    {
        if (File.Exists(_storePath) ||
            string.IsNullOrWhiteSpace(_legacyStorePath) ||
            !File.Exists(_legacyStorePath))
        {
            return;
        }

        try
        {
            string legacyJson = await File.ReadAllTextAsync(_legacyStorePath);
            GlanceWidgetData migrated = Normalize(
                JsonSerializer.Deserialize(
                    legacyJson,
                    GlancePreferencesJsonContext.Default.Preferences));
            await ResilientJsonStore.SaveAsync(
                _storePath,
                JsonSerializer.Serialize(
                    migrated,
                    GlancePreferencesJsonContext.Default.Preferences));
            TryDeleteFile(_legacyStorePath);
            TryDeleteFile(ResilientJsonStore.GetBackupPath(_legacyStorePath));
            App.Log($"[GlanceWidgetStore] Migrated legacy preferences to '{_storePath}'.");
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceWidgetStore] Legacy preference migration failed: {ex}");
        }
    }

    private async Task PersistLockedAsync()
    {
        string json = JsonSerializer.Serialize(
            _cached,
            GlancePreferencesJsonContext.Default.Preferences);
        await ResilientJsonStore.SaveAsync(_storePath, json);
    }

    private static GlanceWidgetData Normalize(GlanceWidgetData? data)
    {
        data ??= new GlanceWidgetData();
        data.Version = GlanceWidgetData.CurrentVersion;
        data.LocalImagePaths ??= [];
        data.LocalImagePaths = data.LocalImagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        data.LocalFolderPath = string.IsNullOrWhiteSpace(data.LocalFolderPath)
            ? null
            : data.LocalFolderPath.Trim();
        if (!data.ShowDate)
        {
            data.ShowYear = false;
        }
        double[] supportedRotationIntervals =
        [
            0,
            10d / 60d,
            30d / 60d,
            1,
            2,
            5,
            10,
            30,
            60,
            360,
            1440
        ];
        double normalizedRotationInterval = supportedRotationIntervals.FirstOrDefault(
            interval => Math.Abs(interval - data.RotationIntervalMinutes) < 0.0001,
            double.NaN);
        data.RotationIntervalMinutes = double.IsNaN(normalizedRotationInterval)
            ? 30
            : normalizedRotationInterval;
        data.TimeScale = Math.Clamp(data.TimeScale, 0.75, 1.35);
        data.TimeFontFamily = string.IsNullOrWhiteSpace(data.TimeFontFamily)
            ? null
            : data.TimeFontFamily.Trim();
        data.TimeFormat = Enum.IsDefined(data.TimeFormat)
            ? data.TimeFormat
            : GlanceTimeFormatMode.FollowSystem;
        data.Layout = Enum.IsDefined(data.Layout) ? data.Layout : GlanceLayoutMode.Centered;
        data.BackgroundSource = Enum.IsDefined(data.BackgroundSource)
            ? data.BackgroundSource
            : GlanceBackgroundSource.Bing;
        data.OnlineImageCategory = Enum.IsDefined(data.OnlineImageCategory)
            ? data.OnlineImageCategory
            : GlanceOnlineImageCategory.Featured;
        data.Transition = Enum.IsDefined(data.Transition) ? data.Transition : GlanceTransitionMode.CrossFade;
        data.TransitionSpeed = Enum.IsDefined(data.TransitionSpeed) ? data.TransitionSpeed : GlanceTransitionSpeed.Standard;
        data.Readability = Enum.IsDefined(data.Readability) ? data.Readability : GlanceReadabilityMode.Soft;
        data.BackgroundImageTransparency = double.IsFinite(data.BackgroundImageTransparency)
            ? Math.Clamp(data.BackgroundImageTransparency, 0.0, 1.0)
            : 0.0;
        data.CalendarMaterialMode = Enum.IsDefined(data.CalendarMaterialMode)
            ? data.CalendarMaterialMode
            : GlanceCalendarMaterialMode.FollowSystem;
        data.CalendarImageMaterialTransparency = double.IsFinite(data.CalendarImageMaterialTransparency)
            ? Math.Clamp(data.CalendarImageMaterialTransparency, 0.0, 1.0)
            : 0.32;
        data.TraditionalCalendarMode = Enum.IsDefined(data.TraditionalCalendarMode)
            ? data.TraditionalCalendarMode
            : GlanceTraditionalCalendarMode.None;
        data.ImageFit = Enum.IsDefined(data.ImageFit) ? data.ImageFit : GlanceImageFitMode.Fill;
        data.ImageFocus = Enum.IsDefined(data.ImageFocus) ? data.ImageFocus : GlanceImageFocus.Center;
        return data;
    }

    private static GlanceWidgetData Clone(GlanceWidgetData data)
    {
        string json = JsonSerializer.Serialize(
            data,
            GlancePreferencesJsonContext.Default.Preferences);
        return JsonSerializer.Deserialize(
                   json,
                   GlancePreferencesJsonContext.Default.Preferences) ??
               new GlanceWidgetData();
    }

    // Internal for NativeGlanceDataMigration: the migration must resolve the
    // exact store path the built-in widget uses.
    internal static string GetSafeWidgetFileName(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(widgetId.Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
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
        catch (Exception ex)
        {
            App.Log($"[GlanceWidgetStore] Failed to delete '{path}': {ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        foreach (EventHandler handler in Changed?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                App.Log($"[GlanceWidgetStore] Observer failed: {ex}");
            }
        }
    }
}
