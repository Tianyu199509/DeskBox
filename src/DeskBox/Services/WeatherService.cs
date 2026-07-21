using System.Net.Http;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Fetches weather data from the Open-Meteo API (no API key required).
/// Includes in-memory caching and geocoding for city name → coordinates.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private const string ForecastBaseUrl = "https://api.open-meteo.com/v1/forecast";
    private const string GeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ReverseGeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/get-by-id";
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private WeatherData? _cachedData;
    private DateTimeOffset _cacheTimestamp;
    private string _cacheLocationKey = string.Empty;
    private bool _isDisposed;

    public WeatherService()
    {
    }

    /// <summary>
    /// Search for a city by name and return matching results.
    /// </summary>
    public async Task<List<WeatherGeocodingItem>> SearchCityAsync(string query, string language = "zh")
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            string url = $"{GeocodingBaseUrl}?name={Uri.EscapeDataString(query)}&count=10&language={language}&format=json";
            string json = await s_httpClient.GetStringAsync(url);
            var result = JsonSerializer.Deserialize<WeatherGeocodingResult>(json, s_jsonOptions);
            return result?.Results ?? [];
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherService] SearchCityAsync failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetch weather data for the given coordinates.
    /// Uses caching to avoid excessive API calls.
    /// </summary>
    public async Task<WeatherData?> GetWeatherAsync(
        double latitude,
        double longitude,
        string locationName = "",
        bool forceRefresh = false,
        TimeSpan? cacheDuration = null)
    {
        string cacheKey = $"{latitude:F4},{longitude:F4}";
        TimeSpan effectiveCacheDuration = cacheDuration.GetValueOrDefault(DefaultCacheDuration);
        if (effectiveCacheDuration < TimeSpan.Zero)
        {
            effectiveCacheDuration = TimeSpan.Zero;
        }

        if (!forceRefresh &&
            _cachedData is not null &&
            string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _cacheTimestamp < effectiveCacheDuration)
        {
            _cachedData.LocationName = locationName;
            return _cachedData;
        }

        try
        {
            string url = BuildForecastUrl(latitude, longitude);
            string json = await s_httpClient.GetStringAsync(url);
            var data = JsonSerializer.Deserialize<WeatherData>(json, s_jsonOptions);

            if (data is not null)
            {
                data.LocationName = locationName;
                data.IsStale = false;
                _cachedData = data;
                _cacheTimestamp = DateTimeOffset.UtcNow;
                _cacheLocationKey = cacheKey;
            }

            return data;
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherService] GetWeatherAsync failed: {ex.Message}");
            // Only return stale cache if it's for the same location —
            // returning a different city's weather when the user just
            // switched cities would be misleading.
            if (string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal))
            {
                if (_cachedData is not null)
                {
                    _cachedData.IsStale = true;
                }
                return _cachedData;
            }
            return null;
        }
    }

    /// <summary>
    /// Try to resolve a city name to coordinates via geocoding.
    /// Returns null if not found.
    /// </summary>
    public async Task<WeatherGeocodingItem?> ResolveCityAsync(string cityName, string language = "zh")
    {
        var results = await SearchCityAsync(cityName, language);
        return results.Count > 0 ? results[0] : null;
    }

    private static string BuildForecastUrl(double lat, double lon)
    {
        return $"{ForecastBaseUrl}" +
               $"?latitude={lat:F4}" +
               $"&longitude={lon:F4}" +
               "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,pressure_msl,is_day" +
               "&hourly=temperature_2m,precipitation_probability,weather_code" +
               "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
               "&timezone=auto" +
               "&forecast_days=7" +
               "&wind_speed_unit=kmh" +
               "&temperature_unit=celsius" +
               "&precipitation_unit=mm";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        // Do not dispose the shared static HttpClient.
    }
}
