using System.Collections.Concurrent;
using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetWeatherSnapshot
{
    public static CustomWidgetWeatherSnapshot Empty { get; } = new();

    public bool HasData { get; init; }
    public string City { get; init; } = string.Empty;
    public double Temperature { get; init; }
    public double ApparentTemperature { get; init; }
    public double HighTemperature { get; init; }
    public double LowTemperature { get; init; }
    public double Humidity { get; init; }
    public double WindSpeedKmh { get; init; }
    public double PrecipitationProbability { get; init; }
    public double UvIndex { get; init; }
    public int WeatherCode { get; init; }
    public bool IsDay { get; init; } = true;
    public string Sunrise { get; init; } = string.Empty;
    public string Sunset { get; init; } = string.Empty;
    public string UpdatedAtText { get; init; } = string.Empty;
    public string Status { get; init; } = "idle";
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<CustomWidgetRecord> ForecastRecords { get; init; } = [];

    public string ResolveToken(string path, string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        bool isZh = c.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        if (!HasData)
        {
            return path.Trim() switch
            {
                "status" => Status,
                "error" => ErrorMessage ?? string.Empty,
                "city" or "location" => string.IsNullOrWhiteSpace(City) ? (isZh ? "定位中…" : "Locating…") : City,
                "icon" or "emoji" => "⛅",
                "glyph" => "\uE753",
                _ => "--"
            };
        }

        string tempInt = Math.Round(Temperature).ToString("0", CultureInfo.InvariantCulture);
        string feelsInt = Math.Round(ApparentTemperature).ToString("0", CultureInfo.InvariantCulture);
        string highInt = Math.Round(HighTemperature).ToString("0", CultureInfo.InvariantCulture);
        string lowInt = Math.Round(LowTemperature).ToString("0", CultureInfo.InvariantCulture);
        string condText = CustomWidgetWeatherSource.FormatWmoCondition(WeatherCode, IsDay, c);

        return path.Trim() switch
        {
            "city" or "location" or "name" => City,
            "temp" or "temperature" => tempInt,
            "tempExact" => Temperature.ToString("0.0", CultureInfo.InvariantCulture),
            "tempText" => $"{tempInt}°C",
            "feelsLike" or "apparentTemp" => feelsInt,
            "feelsLikeText" => $"{feelsInt}°C",
            "high" or "maxTemp" or "tempMax" => highInt,
            "low" or "minTemp" or "tempMin" => lowInt,
            "range" or "rangeText" => $"{lowInt}° ~ {highInt}°",
            "condition" or "summary" or "weather" or "weatherText" or "desc" => condText,
            "icon" or "emoji" => CustomWidgetWeatherSource.ResolveWeatherEmoji(WeatherCode, IsDay),
            "glyph" => CustomWidgetWeatherSource.ResolveWeatherGlyph(WeatherCode, IsDay),
            "humidity" => $"{Math.Round(Humidity):0}%",
            "humidityValue" => Math.Round(Humidity).ToString("0", CultureInfo.InvariantCulture),
            "wind" or "windText" => $"{WindSpeedKmh:0.#} km/h",
            "windSpeed" => WindSpeedKmh.ToString("0.#", CultureInfo.InvariantCulture),
            "precip" or "precipProb" or "rainProb" => $"{Math.Round(PrecipitationProbability):0}%",
            "uv" or "uvIndex" => UvIndex.ToString("0.#", CultureInfo.InvariantCulture),
            "sunrise" => Sunrise,
            "sunset" => Sunset,
            "updatedAt" => UpdatedAtText,
            "status" => Status,
            "error" => ErrorMessage ?? string.Empty,
            _ => string.Empty
        };
    }
}

/// <summary>
/// Zero-API-key native weather provider for Custom Widgets (<c>"type": "weather"</c>).
/// Reuses DeskBox's <see cref="WeatherService"/> (MSN Weather + Open-Meteo fallback + geocoding + cache).
/// </summary>
public sealed class CustomWidgetWeatherSource
{
    public static CustomWidgetWeatherSource Shared { get; } = new();

    private readonly WeatherService _weatherService;
    private readonly SettingsService? _settingsService;
    private readonly ConcurrentDictionary<string, (double Lat, double Lon, string ResolvedName)> _geoCache =
        new(StringComparer.OrdinalIgnoreCase);

    public CustomWidgetWeatherSource(
        WeatherService? weatherService = null,
        SettingsService? settingsService = null)
    {
        _weatherService = weatherService ?? new WeatherService();
        _settingsService = settingsService;
    }

    public async Task<CustomWidgetWeatherSnapshot> FetchSnapshotAsync(
        string? requestedCity,
        string? cultureName = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        bool isZh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        try
        {
            (double lat, double lon, string resolvedCity) = await ResolveCoordinatesAsync(
                requestedCity,
                culture,
                cancellationToken).ConfigureAwait(false);

            WeatherData? data = await _weatherService.GetWeatherAsync(
                lat,
                lon,
                resolvedCity,
                forceRefresh: forceRefresh,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (data?.Current is null)
            {
                return new CustomWidgetWeatherSnapshot
                {
                    HasData = false,
                    City = resolvedCity,
                    Status = "error",
                    ErrorMessage = isZh ? "暂时无法获取天气数据" : "Weather service temporarily unavailable"
                };
            }

            return BuildSnapshotFromWeatherData(data, resolvedCity, culture);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CustomWidgetWeatherSnapshot
            {
                HasData = false,
                City = requestedCity ?? string.Empty,
                Status = "error",
                ErrorMessage = ex.Message
            };
        }
    }

    public static CustomWidgetWeatherSnapshot BuildSnapshotFromWeatherData(
        WeatherData data,
        string fallbackCity,
        string? cultureName = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        CultureInfo ci;
        try
        {
            ci = CultureInfo.GetCultureInfo(culture);
        }
        catch (CultureNotFoundException)
        {
            ci = CultureInfo.InvariantCulture;
        }

        WeatherCurrent cur = data.Current ?? new WeatherCurrent();
        WeatherDaily? daily = data.Daily;

        double todayHigh = daily is { TemperatureMax.Count: > 0 } ? daily.TemperatureMax[0] : cur.Temperature;
        double todayLow = daily is { TemperatureMin.Count: > 0 } ? daily.TemperatureMin[0] : cur.Temperature;
        double todayPrecip = daily is { PrecipitationProbabilityMax.Count: > 0 } ? daily.PrecipitationProbabilityMax[0] : 0;
        double todayUv = daily is { UvIndexMax.Count: > 0 } ? daily.UvIndexMax[0] : 0;
        string sunrise = FormatClockPart(daily is { Sunrise.Count: > 0 } ? daily.Sunrise[0] : null);
        string sunset = FormatClockPart(daily is { Sunset.Count: > 0 } ? daily.Sunset[0] : null);

        var forecast = new List<CustomWidgetRecord>();
        if (daily?.Time is { Count: > 0 })
        {
            int count = Math.Min(7, daily.Time.Count);
            long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            for (int i = 0; i < count; i++)
            {
                string rawDate = daily.Time[i];
                int code = i < daily.WeatherCode.Count ? daily.WeatherCode[i] : cur.WeatherCode;
                double hi = i < daily.TemperatureMax.Count ? daily.TemperatureMax[i] : todayHigh;
                double lo = i < daily.TemperatureMin.Count ? daily.TemperatureMin[i] : todayLow;
                double pop = i < daily.PrecipitationProbabilityMax.Count ? daily.PrecipitationProbabilityMax[i] : 0;

                string weekday = rawDate;
                string shortDate = rawDate;
                if (DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDate))
                {
                    shortDate = parsedDate.ToString("MM-dd", CultureInfo.InvariantCulture);
                    weekday = i == 0
                        ? (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "今天" : "Today")
                        : parsedDate.ToDateTime(TimeOnly.MinValue).ToString("ddd", ci);
                }

                var rec = new CustomWidgetRecord
                {
                    Id = $"weather-day-{i}",
                    CreatedAtUtcTicks = nowTicks - i,
                    UpdatedAtUtcTicks = nowTicks
                };
                rec.Fields["date"] = rawDate;
                rec.Fields["shortDate"] = shortDate;
                rec.Fields["weekday"] = weekday;
                rec.Fields["high"] = Math.Round(hi).ToString("0", CultureInfo.InvariantCulture);
                rec.Fields["low"] = Math.Round(lo).ToString("0", CultureInfo.InvariantCulture);
                rec.Fields["range"] = $"{Math.Round(lo):0}° ~ {Math.Round(hi):0}°";
                rec.Fields["condition"] = FormatWmoCondition(code, isDay: true, culture);
                rec.Fields["icon"] = ResolveWeatherEmoji(code, isDay: true);
                rec.Fields["precip"] = $"{Math.Round(pop):0}%";
                forecast.Add(rec);
            }
        }

        string city = !string.IsNullOrWhiteSpace(data.LocationName)
            ? data.LocationName
            : fallbackCity;

        return new CustomWidgetWeatherSnapshot
        {
            HasData = true,
            City = city,
            Temperature = cur.Temperature,
            ApparentTemperature = cur.ApparentTemperature,
            HighTemperature = todayHigh,
            LowTemperature = todayLow,
            Humidity = cur.Humidity,
            WindSpeedKmh = cur.WindSpeed,
            PrecipitationProbability = todayPrecip,
            UvIndex = todayUv,
            WeatherCode = cur.WeatherCode,
            IsDay = cur.IsDay != 0,
            Sunrise = sunrise,
            Sunset = sunset,
            UpdatedAtText = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Status = "ok",
            ForecastRecords = forecast
        };
    }

    private async Task<(double Lat, double Lon, string Name)> ResolveCoordinatesAsync(
        string? requestedCity,
        string culture,
        CancellationToken cancellationToken)
    {
        string trimmed = requestedCity?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (_geoCache.TryGetValue(trimmed, out var cached))
            {
                return cached;
            }

            // Support explicit "lat,lon" coordinate strings
            int commaIdx = trimmed.IndexOf(',');
            if (commaIdx > 0 &&
                double.TryParse(trimmed[..commaIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double explicitLat) &&
                double.TryParse(trimmed[(commaIdx + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double explicitLon) &&
                explicitLat is >= -90 and <= 90 &&
                explicitLon is >= -180 and <= 180)
            {
                return (explicitLat, explicitLon, trimmed);
            }

            string lang = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
            List<WeatherGeocodingItem> matches = await _weatherService
                .SearchCityAsync(trimmed, lang, cancellationToken)
                .ConfigureAwait(false);
            if (matches.Count > 0)
            {
                WeatherGeocodingItem top = matches[0];
                var resolved = (top.Latitude, top.Longitude, string.IsNullOrWhiteSpace(top.Name) ? trimmed : top.Name);
                _geoCache[trimmed] = resolved;
                return resolved;
            }
        }

        // Reuse DeskBox's user-configured Weather widget location when available
        try
        {
            if (_settingsService?.Settings.Weather is { } weatherSettings &&
                !string.IsNullOrWhiteSpace(weatherSettings.WeatherCityName) &&
                double.IsFinite(weatherSettings.WeatherLatitude) &&
                double.IsFinite(weatherSettings.WeatherLongitude) &&
                (Math.Abs(weatherSettings.WeatherLatitude) > 0.001 || Math.Abs(weatherSettings.WeatherLongitude) > 0.001))
            {
                return (weatherSettings.WeatherLatitude, weatherSettings.WeatherLongitude, weatherSettings.WeatherCityName);
            }
        }
        catch
        {
        }

        return culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (39.9042, 116.4074, "北京")
            : (40.7128, -74.0060, "New York");
    }

    private static string FormatClockPart(string? isoTime)
    {
        if (string.IsNullOrWhiteSpace(isoTime))
        {
            return "--:--";
        }

        int tIdx = isoTime.IndexOf('T');
        if (tIdx >= 0 && isoTime.Length >= tIdx + 6)
        {
            return isoTime.Substring(tIdx + 1, 5);
        }

        return isoTime;
    }

    public static string ResolveWeatherEmoji(int wmoCode, bool isDay) => wmoCode switch
    {
        0 => isDay ? "☀️" : "🌙",
        1 or 2 => isDay ? "⛅" : "☁️",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 => "🌦️",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧️",
        71 or 73 or 75 or 77 or 85 or 86 => "❄️",
        95 or 96 or 99 => "⛈️",
        _ => "⛅"
    };

    public static string ResolveWeatherGlyph(int wmoCode, bool isDay) => wmoCode switch
    {
        0 => isDay ? "\uE706" : "\uE708",
        1 or 2 or 3 => "\uE753",
        _ => "\uE9C4"
    };

    public static string FormatWmoCondition(int wmoCode, bool isDay, string? cultureName)
    {
        bool isZh = (cultureName ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isZh)
        {
            return wmoCode switch
            {
                0 => isDay ? "晴朗" : "晴夜",
                1 => "大部晴朗",
                2 => "多云",
                3 => "阴",
                45 or 48 => "雾",
                51 or 53 or 55 => "毛毛雨",
                56 or 57 => "冻毛毛雨",
                61 => "小雨",
                63 => "中雨",
                65 => "大雨",
                66 or 67 => "冻雨",
                71 => "小雪",
                73 => "中雪",
                75 or 77 => "大雪",
                80 or 81 or 82 => "阵雨",
                85 or 86 => "阵雪",
                95 or 96 or 99 => "雷阵雨",
                _ => "多云"
            };
        }

        return wmoCode switch
        {
            0 => isDay ? "Clear" : "Clear Night",
            1 => "Mostly Clear",
            2 => "Partly Cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing Drizzle",
            61 => "Light Rain",
            63 => "Rain",
            65 => "Heavy Rain",
            66 or 67 => "Freezing Rain",
            71 => "Light Snow",
            73 => "Snow",
            75 or 77 => "Heavy Snow",
            80 or 81 or 82 => "Rain Showers",
            85 or 86 => "Snow Showers",
            95 or 96 or 99 => "Thunderstorm",
            _ => "Cloudy"
        };
    }
}
