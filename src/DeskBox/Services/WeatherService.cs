using System.Net.Http;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Fetches weather data from multiple data sources with automatic fallback.
/// Supported sources: MSN Weather (default), Open-Meteo.
/// City geocoding always uses Open-Meteo (MSN has no geocoding endpoint).
/// </summary>
public sealed class WeatherService : IDisposable
{
    // ── Open-Meteo constants ──
    private const string OpenMeteoForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string GeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ReverseGeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/get-by-id";

    // ── MSN Weather constants ──
    private const string MsnWeatherUrl = "https://api.msn.com/weather/overview";
    private const string MsnApiKey = "UhJ4G66OjyLbn9mXARgajXLiLw6V75sHnfpU60aJBB";

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
    private string _cacheSourceKey = string.Empty;
    private bool _isDisposed;

    public WeatherService()
    {
    }

    /// <summary>
    /// Search for a city by name and return matching results.
    /// Always uses Open-Meteo geocoding (MSN has no geocoding endpoint).
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
    /// Respects the user's preferred data source setting, with automatic fallback.
    /// </summary>
    public async Task<WeatherData?> GetWeatherAsync(
        double latitude,
        double longitude,
        string locationName = "",
        bool forceRefresh = false,
        TimeSpan? cacheDuration = null,
        string? dataSource = null)
    {
        string cacheKey = $"{latitude:F4},{longitude:F4}";
        string sourceKey = dataSource ?? GetCurrentDataSource();
        TimeSpan effectiveCacheDuration = cacheDuration.GetValueOrDefault(DefaultCacheDuration);
        if (effectiveCacheDuration < TimeSpan.Zero)
        {
            effectiveCacheDuration = TimeSpan.Zero;
        }

        if (!forceRefresh &&
            _cachedData is not null &&
            string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal) &&
            string.Equals(_cacheSourceKey, sourceKey, StringComparison.Ordinal) &&
            DateTimeOffset.UtcNow - _cacheTimestamp < effectiveCacheDuration)
        {
            _cachedData.LocationName = locationName;
            return _cachedData;
        }

        // Try preferred source first, then fallback.
        WeatherData? data = await FetchFromSourceAsync(sourceKey, latitude, longitude);
        if (data is null)
        {
            string fallbackSource = sourceKey == SettingsService.WeatherDataSourceMsn
                ? SettingsService.WeatherDataSourceOpenMeteo
                : SettingsService.WeatherDataSourceMsn;
            App.Log($"[WeatherService] Primary source '{sourceKey}' failed, trying fallback '{fallbackSource}'");
            data = await FetchFromSourceAsync(fallbackSource, latitude, longitude);
            if (data is not null)
            {
                data.IsFallback = true;
            }
        }

        if (data is not null)
        {
            data.LocationName = locationName;
            data.IsStale = false;
            _cachedData = data;
            _cacheTimestamp = DateTimeOffset.UtcNow;
            _cacheLocationKey = cacheKey;
            _cacheSourceKey = sourceKey;
        }
        else
        {
            App.Log("[WeatherService] All weather data sources failed");
            // Return stale cache if it's for the same location.
            if (string.Equals(_cacheLocationKey, cacheKey, StringComparison.Ordinal) && _cachedData is not null)
            {
                _cachedData.IsStale = true;
                return _cachedData;
            }
        }

        return data;
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

    private static string GetCurrentDataSource()
    {
        try
        {
            return App.Current?.Services?.GetService(typeof(SettingsService)) is SettingsService svc
                ? svc.Settings.WeatherDataSource
                : SettingsService.WeatherDataSourceMsn;
        }
        catch
        {
            return SettingsService.WeatherDataSourceMsn;
        }
    }

    // ── Source dispatch ──

    private static async Task<WeatherData?> FetchFromSourceAsync(string source, double lat, double lon)
    {
        try
        {
            return source == SettingsService.WeatherDataSourceMsn
                ? await FetchMsnWeatherAsync(lat, lon)
                : await FetchOpenMeteoWeatherAsync(lat, lon);
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherService] FetchFromSourceAsync({source}) failed: {ex.Message}");
            return null;
        }
    }

    // ── Open-Meteo ──

    private static async Task<WeatherData?> FetchOpenMeteoWeatherAsync(double lat, double lon)
    {
        string url = BuildOpenMeteoForecastUrl(lat, lon);
        string json = await s_httpClient.GetStringAsync(url);
        return JsonSerializer.Deserialize<WeatherData>(json, s_jsonOptions);
    }

    private static string BuildOpenMeteoForecastUrl(double lat, double lon)
    {
        return $"{OpenMeteoForecastUrl}" +
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

    // ── MSN Weather ──

    private static async Task<WeatherData?> FetchMsnWeatherAsync(double lat, double lon)
    {
        string url = $"{MsnWeatherUrl}?apikey={MsnApiKey}&lat={lat:F4}&lon={lon:F4}&units=C";
        string json = await s_httpClient.GetStringAsync(url);
        var msnResponse = JsonSerializer.Deserialize<MsnWeatherResponse>(json, s_jsonOptions);

        var msnWeather = msnResponse?.Value?.FirstOrDefault()?.Responses?.FirstOrDefault()?.Weather?.FirstOrDefault();
        if (msnWeather is null)
        {
            App.Log("[WeatherService] MSN response structure unexpected: no weather data found");
            return null;
        }

        return ConvertMsnToWeatherData(msnWeather, lat, lon);
    }

    /// <summary>
    /// Converts MSN Weather API response to the unified WeatherData model
    /// used by the rest of the application.
    /// </summary>
    private static WeatherData ConvertMsnToWeatherData(MsnWeatherBody msn, double lat, double lon)
    {
        var current = msn.Current;
        int currentWmo = current is not null
            ? WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(current.Cap, current.Icon)
            : 0;

        var data = new WeatherData
        {
            Latitude = lat,
            Longitude = lon,
            Timezone = string.Empty, // MSN doesn't return timezone; UI uses local time
            Current = current is not null
                ? new WeatherCurrent
                {
                    Time = current.Created,
                    Temperature = current.Temp,
                    Humidity = current.Rh,
                    ApparentTemperature = current.Feels,
                    WeatherCode = currentWmo,
                    WindSpeed = current.WindSpd,
                    WindDirection = current.WindDir,
                    Pressure = current.Baro,
                    IsDay = current.IsDay ? 1 : 0
                }
                : null,
        };

        // Convert daily forecast
        var forecastDays = msn.Forecast?.Days;
        if (forecastDays is { Count: > 0 })
        {
            var daily = new WeatherDaily();
            foreach (var day in forecastDays)
            {
                if (day.Daily is null) continue;

                // Parse date from "valid" field (ISO 8601)
                string dateStr = string.Empty;
                if (!string.IsNullOrEmpty(day.Daily.Valid) &&
                    DateTimeOffset.TryParse(day.Daily.Valid, out var dt))
                {
                    dateStr = dt.ToString("yyyy-MM-dd");
                }

                daily.Time.Add(dateStr);
                int dayWmo = WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(
                    day.Daily.PvdrCap, day.Daily.Day?.Icon ?? 0);
                daily.WeatherCode.Add(dayWmo);
                daily.TemperatureMax.Add(day.Daily.TempHi);
                daily.TemperatureMin.Add(day.Daily.TempLo);

                // Precipitation probability: take max of day and night
                double precipMax = Math.Max(
                    day.Daily.Day?.Precip ?? 0,
                    day.Daily.Night?.Precip ?? 0);
                daily.PrecipitationProbabilityMax.Add(precipMax);

                // UV
                daily.UvIndexMax.Add(day.Daily.Uv);

                // Sunrise/sunset from almanac
                string sunrise = string.Empty;
                string sunset = string.Empty;
                if (day.Almanac is not null)
                {
                    if (DateTimeOffset.TryParse(day.Almanac.Sunrise, out var sr))
                        sunrise = sr.ToString("yyyy-MM-ddTHH:mm");
                    if (DateTimeOffset.TryParse(day.Almanac.Sunset, out var ss))
                        sunset = ss.ToString("yyyy-MM-ddTHH:mm");
                }
                daily.Sunrise.Add(sunrise);
                daily.Sunset.Add(sunset);
            }

            if (daily.Time.Count > 0)
            {
                data.Daily = daily;
            }
        }

        // Convert hourly forecast (take first day's hours)
        var firstDay = forecastDays?.FirstOrDefault();
        if (firstDay?.Hourly is { Count: > 0 })
        {
            var hourly = new WeatherHourly();
            foreach (var h in firstDay.Hourly)
            {
                // Parse time from "valid" field
                if (DateTimeOffset.TryParse(h.Valid, out var ht))
                {
                    hourly.Time.Add(ht.ToString("yyyy-MM-ddTHH:mm"));
                }
                else
                {
                    hourly.Time.Add(h.Valid);
                }

                hourly.Temperature.Add(h.Temp);
                hourly.PrecipitationProbability.Add(h.Precip);

                int hourWmo = WeatherCodeMapper.MsnDescriptionOrIconToWmoCode(h.Cap, h.Icon);
                hourly.WeatherCode.Add(hourWmo);
            }

            if (hourly.Time.Count > 0)
            {
                data.Hourly = hourly;
            }
        }

        return data;
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
