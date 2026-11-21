using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class WeatherCacheState
{
    internal const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("lastLocation")]
    public WeatherCachedLocation? LastLocation { get; set; }

    [JsonPropertyName("lastForecast")]
    public WeatherCachedForecast? LastForecast { get; set; }
}

internal sealed class WeatherCachedLocation
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("resolvedAtUtc")]
    public DateTimeOffset ResolvedAtUtc { get; set; }

    public bool IsValid =>
        double.IsFinite(Latitude) &&
        double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180 &&
        ResolvedAtUtc != default;
}

internal sealed class WeatherCachedForecast
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("locationName")]
    public string LocationName { get; set; } = string.Empty;

    [JsonPropertyName("requestedSource")]
    public string RequestedSource { get; set; } = SettingsService.WeatherDataSourceMsn;

    [JsonPropertyName("actualSource")]
    public string ActualSource { get; set; } = SettingsService.WeatherDataSourceMsn;

    [JsonPropertyName("fetchedAtUtc")]
    public DateTimeOffset FetchedAtUtc { get; set; }

    [JsonPropertyName("data")]
    public WeatherData? Data { get; set; }

    public bool IsValid =>
        double.IsFinite(Latitude) &&
        double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180 &&
        FetchedAtUtc != default &&
        Data?.Current is not null;
}

internal sealed class WeatherCacheStore
{
    internal const string FileName = "weather-cache.json";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathGates =
        new(StringComparer.OrdinalIgnoreCase);

    internal static int PathGateCount => s_pathGates.Count;

    public static WeatherCacheStore Current { get; } = new();

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate;

    public WeatherCacheStore(string? storePath = null)
    {
        _storePath = Path.GetFullPath(storePath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            FileName));
        _gate = s_pathGates.GetOrAdd(_storePath, static _ => new SemaphoreSlim(1, 1));
    }

    internal string StorePath => _storePath;

    public async Task<WeatherCacheState> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await LoadCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveLocationAsync(WeatherCachedLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsValid)
        {
            return false;
        }

        return await UpdateAsync(state => state.LastLocation = location);
    }

    public async Task<bool> SaveForecastAsync(WeatherCachedForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        if (!forecast.IsValid)
        {
            return false;
        }

        return await UpdateAsync(state =>
        {
            state.LastForecast = forecast;
        });
    }

    internal static bool IsSameLocation(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        const double coordinateTolerance = 0.01;
        return Math.Abs(firstLatitude - secondLatitude) <= coordinateTolerance &&
               Math.Abs(firstLongitude - secondLongitude) <= coordinateTolerance;
    }

    private async Task<bool> UpdateAsync(Action<WeatherCacheState> update)
    {
        await _gate.WaitAsync();
        try
        {
            WeatherCacheState state = await LoadCoreAsync();
            update(state);
            state.SchemaVersion = WeatherCacheState.CurrentSchemaVersion;
            await ResilientJsonStore.SaveAsync(
                _storePath,
                async tempPath =>
                {
                    await using var stream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await WeatherService.SerializeCacheStateAsync(stream, state).ConfigureAwait(false);
                });
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherCache] Save failed: {ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WeatherCacheState> LoadCoreAsync()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                try
                {
                    await using var stream = new FileStream(
                        _storePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    WeatherCacheState? loaded = await WeatherService
                        .DeserializeCacheStateAsync(stream)
                        .ConfigureAwait(false);
                    return Normalize(loaded ?? new WeatherCacheState());
                }
                catch
                {
                    // Fall back to ResilientJsonStore for corrupt-primary quarantine
                    // and backup restoration.
                }
            }

            WeatherCacheState state = await ResilientJsonStore.LoadAsync(
                _storePath,
                static json => WeatherService.DeserializeCacheState(json) ?? new WeatherCacheState(),
                static () => new WeatherCacheState(),
                "WeatherCache");
            return Normalize(state);
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherCache] Load failed: {ex.Message}");
            return new WeatherCacheState();
        }
    }

    private static WeatherCacheState Normalize(WeatherCacheState state)
    {
        if (state.SchemaVersion <= 0 ||
            state.SchemaVersion > WeatherCacheState.CurrentSchemaVersion)
        {
            return new WeatherCacheState();
        }

        if (state.LastLocation?.IsValid != true)
        {
            state.LastLocation = null;
        }

        if (state.LastForecast?.IsValid != true)
        {
            state.LastForecast = null;
        }

        state.SchemaVersion = WeatherCacheState.CurrentSchemaVersion;
        return state;
    }
}
