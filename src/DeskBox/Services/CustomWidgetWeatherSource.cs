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

        if (!HasData)
        {
            return path.Trim() switch
            {
                "status" => Status,
                "error" => ErrorMessage ?? string.Empty,
                "city" or "location" => string.IsNullOrWhiteSpace(City)
                    ? LocalizationService.TranslateForCulture(c, "CustomWidget.Weather.Locating")
                    : City,
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
                    ErrorMessage = LocalizationService.TranslateForCulture(culture, "CustomWidget.Weather.Unavailable")
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
                        ? LocalizationService.TranslateForCulture(culture, "Weather.Today")
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
            ? (39.9042, 116.4074, LocalizationService.TranslateForCulture(culture, "CustomWidget.Weather.DefaultCity"))
            : (40.7128, -74.0060, LocalizationService.TranslateForCulture(culture, "CustomWidget.Weather.FallbackCity"));
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

    public static string ResolveWeatherGlyph(int wmoCode, bool isDay) =>
        ResolveWeatherGlyph(wmoCode, isDay, WindowsCompatibilityService.IsWindows11OrLater);

    public static string ResolveWeatherGlyph(int wmoCode, bool isDay, bool isWindows11OrLater) => wmoCode switch
    {
        0 => isDay ? "\uE706" : "\uE708",
        1 or 2 or 3 => "\uE753",
        _ => isWindows11OrLater ? "\uE9C4" : "\uE753"
    };

    private static readonly int[] KnownWmoCodes =
    [
        0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57, 61, 63, 65, 66, 67,
        71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99
    ];

    public static string FormatWmoCondition(int wmoCode, bool isDay, string? cultureName)
    {
        // Reuses the Weather.Condition.{code} catalog shared with the glance
        // weather widget (all 13 locales, pinned by WeatherCodeMapperLocalizationTests).
        if (!KnownWmoCodes.Contains(wmoCode))
        {
            return LocalizationService.TranslateForCulture(cultureName, "Weather.Condition.2");
        }

        if (wmoCode == 0 && !isDay)
        {
            return LocalizationService.TranslateForCulture(cultureName, "Weather.Condition.0.Night");
        }

        return LocalizationService.TranslateForCulture(cultureName, $"Weather.Condition.{wmoCode}");
    }
}
