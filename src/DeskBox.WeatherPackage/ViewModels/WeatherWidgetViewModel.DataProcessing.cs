using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.WeatherPackage.Helpers;
using DeskBox.WeatherPackage.Models;
using DeskBox.WeatherPackage.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.WeatherPackage.ViewModels;

public sealed partial class WeatherWidgetViewModel
{
    private async Task LoadCachedWeatherAsync()
    {
        WeatherCacheState state = await _weatherService.LoadCacheStateAsync();
        if (_isDisposed)
        {
            return;
        }

        WeatherCachedForecast? forecast = state.LastForecast;
        if (forecast?.IsValid == true &&
            forecast.Data is not null &&
            CanUseCachedForecast(forecast))
        {
            _latitude = forecast.Latitude;
            _longitude = forecast.Longitude;
            _locationName = forecast.LocationName;
            _locationInitialized = true;
            _locationResolvedAtUtc = state.LastLocation?.IsValid == true &&
                WeatherCacheStore.IsSameLocation(
                    state.LastLocation.Latitude,
                    state.LastLocation.Longitude,
                    forecast.Latitude,
                    forecast.Longitude)
                    ? state.LastLocation.ResolvedAtUtc
                    : default;

            TimeSpan refreshInterval = GetConfiguredRefreshInterval();
            bool isStale = DateTimeOffset.UtcNow - forecast.FetchedAtUtc >= refreshInterval;
            forecast.Data.LocationName = forecast.LocationName;
            forecast.Data.IsStale = isStale;
            forecast.Data.IsFallback = !string.Equals(
                forecast.RequestedSource,
                forecast.ActualSource,
                StringComparison.Ordinal);
            _weatherData = forecast.Data;
            ApplyWeatherData(forecast.Data);
            HasData = true;
            _automaticRefreshNotBeforeUtc = isStale
                ? DateTimeOffset.MinValue
                : forecast.FetchedAtUtc + refreshInterval;
            PackageLogger.Log(
                $"[WeatherWidget] Restored cached forecast " +
                $"location='{forecast.LocationName}' stale={isStale}");
            return;
        }

        if ((_settingsService?.Settings.WeatherAutoLocation ?? true) &&
            state.LastLocation?.IsValid == true)
        {
            _latitude = state.LastLocation.Latitude;
            _longitude = state.LastLocation.Longitude;
            _locationName = state.LastLocation.Name;
            _locationInitialized = true;
            _locationResolvedAtUtc = state.LastLocation.ResolvedAtUtc;
        }
    }

    private bool CanUseCachedForecast(WeatherCachedForecast forecast)
    {
        if (_settingsService is null || _settingsService.Settings.WeatherAutoLocation)
        {
            return true;
        }

        WeatherPreferences settings = _settingsService.Settings;
        if (settings.WeatherLatitude != 0 || settings.WeatherLongitude != 0)
        {
            return WeatherCacheStore.IsSameLocation(
                settings.WeatherLatitude,
                settings.WeatherLongitude,
                forecast.Latitude,
                forecast.Longitude);
        }

        return !string.IsNullOrWhiteSpace(settings.WeatherCityName) &&
            string.Equals(
                settings.WeatherCityName.Trim(),
                forecast.LocationName.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureLocationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lang = _localizationService.CurrentCultureName;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_settingsService is null)
        {
            if (_locationInitialized &&
                now - _locationResolvedAtUtc < WeatherRefreshBackoffPolicy.LocationReuseDuration)
            {
                return;
            }

            // No settings service: try auto-detect, fall back to a well-known default.
            var autoLoc = await WindowsLocationHelper.GetLocationAsync(_localizationService)
                .WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (autoLoc is not null)
            {
                _latitude = autoLoc.Value.Lat;
                _longitude = autoLoc.Value.Lon;
                _locationName = RefineLocationName(autoLoc.Value.Lat, autoLoc.Value.Lon, autoLoc.Value.Name, lang);
                _locationInitialized = true;
                _locationResolvedAtUtc = now;
                _locationRetryNotBeforeUtc = default;
                IsUsingFallbackLocation = false;
                await _weatherService.SaveResolvedLocationAsync(
                    _latitude,
                    _longitude,
                    _locationName,
                    now);
            }
            else
            {
                _latitude = 39.9042;
                _longitude = 116.4074;
                _locationName = _localizationService.ApiLanguageCode switch { "zh" => "北京", "ja" => "北京", "de" => "Peking", "pt" => "Pequim", _ => "Beijing" };
                _locationInitialized = true;
                _locationRetryNotBeforeUtc = now + WeatherRefreshBackoffPolicy.LocationFailureDelay;
                IsUsingFallbackLocation = true;
            }
            return;
        }
    
        var settings = _settingsService.Settings;
        if (settings.WeatherAutoLocation)
        {
            if (_locationInitialized && now < _locationRetryNotBeforeUtc)
            {
                return;
            }

            if (_locationInitialized &&
                !IsUsingFallbackLocation &&
                now - _locationResolvedAtUtc < WeatherRefreshBackoffPolicy.LocationReuseDuration)
            {
                return;
            }

            // Try Windows location API + multi-source IP fallback
            var result = await WindowsLocationHelper.GetLocationAsync(_localizationService)
                .WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is not null)
            {
                _latitude = result.Value.Lat;
                _longitude = result.Value.Lon;
                _locationName = RefineLocationName(result.Value.Lat, result.Value.Lon, result.Value.Name, lang);
                _locationInitialized = true;
                _locationResolvedAtUtc = now;
                _locationRetryNotBeforeUtc = default;
                IsUsingFallbackLocation = false;
                await _weatherService.SaveResolvedLocationAsync(
                    _latitude,
                    _longitude,
                    _locationName,
                    now);
                return;
            }

            _locationRetryNotBeforeUtc = now + WeatherRefreshBackoffPolicy.LocationFailureDelay;
            if (_locationInitialized)
            {
                PackageLogger.Log("[Weather] Auto-location failed, retaining cached location");
                return;
            }
    
            // Auto-location failed: if user has a saved city, use it.
            // Otherwise fall back to a well-known default so the widget still shows data.
            PackageLogger.Log("[Weather] Auto-location failed, using saved city or default");
            if (!string.IsNullOrWhiteSpace(settings.WeatherCityName))
            {
                var item = await _weatherService.ResolveCityAsync(
                    settings.WeatherCityName,
                    lang,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (item is not null)
                {
                    _latitude = item.Latitude;
                    _longitude = item.Longitude;
                    _locationName = item.DisplayName;
                    _locationInitialized = true;
                    _locationResolvedAtUtc = now;
                    IsUsingFallbackLocation = false;
                    return;
                }
            }
    
            // Last resort: use a neutral default so the widget renders something.
            _latitude = 39.9042;
            _longitude = 116.4074;
            _locationName = _localizationService.ApiLanguageCode switch { "zh" => "北京", "ja" => "北京", "de" => "Peking", "pt" => "Pequim", _ => "Beijing" };
            _locationInitialized = true;
            IsUsingFallbackLocation = true;
        }
        else
        {
            if (settings.WeatherLatitude != 0 || settings.WeatherLongitude != 0)
            {
                _latitude = settings.WeatherLatitude;
                _longitude = settings.WeatherLongitude;
                _locationName = settings.WeatherCityName;
                _locationInitialized = true;
                _locationResolvedAtUtc = now;
                IsUsingFallbackLocation = false;
            }
            else
            {
                // Try to resolve the saved city name
                if (!string.IsNullOrWhiteSpace(settings.WeatherCityName))
                {
                    var item = await _weatherService.ResolveCityAsync(
                        settings.WeatherCityName,
                        lang,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is not null)
                    {
                        _latitude = item.Latitude;
                        _longitude = item.Longitude;
                        _locationName = item.DisplayName;
                        _locationInitialized = true;
                        _locationResolvedAtUtc = now;
                        IsUsingFallbackLocation = false;
                        settings.WeatherLatitude = _latitude;
                        settings.WeatherLongitude = _longitude;
                        _settingsService.SaveDebounced();
                    }
                }

                if (!_locationInitialized)
                {
                    _latitude = 39.9042;
                    _longitude = 116.4074;
                    _locationName = _localizationService.ApiLanguageCode switch { "zh" => "北京", "ja" => "北京", "de" => "Peking", "pt" => "Pequim", _ => "Beijing" };
                    _locationInitialized = true;
                    IsUsingFallbackLocation = true;
                }
            }
        }
    }

    private TimeSpan GetConfiguredRefreshInterval()
    {
        int minutes = _settingsService is null
            ? 30
            : Math.Clamp(
                _settingsService.Settings.WeatherRefreshIntervalMinutes,
                WeatherSettingsContext.WeatherRefreshMinMinutes,
                WeatherSettingsContext.WeatherRefreshMaxMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    private void ApplyWeatherData(WeatherData data)
    {
        if (data.Current is null)
        {
            return;
        }

        var current = data.Current;
        
        IsDay = current.IsDay == 1;
        CurrentWeatherCode = current.WeatherCode;
        CurrentCondition = WeatherCodeMapper.GetCondition(current.WeatherCode);
        CurrentEmoji = WeatherCodeMapper.GetEmoji(current.WeatherCode, IsDay);
        CurrentIconGlyph = WeatherCodeMapper.GetGlyph(current.WeatherCode, IsDay);
        CurrentDescription = WeatherCodeMapper.GetDescription(current.WeatherCode, _localizationService.CurrentCultureName);
        CurrentTemperatureText = FormatTemperature(current.Temperature);
        
        ApparentTemperatureText = _localizationService.Format("Weather.FeelsLike", FormatTemperature(current.ApparentTemperature));
        
        HumidityValueText = $"{(int)current.Humidity}%";
        HumidityText = _localizationService.Format("Weather.HumidityLabel", HumidityValueText);
        
        WindValueText = FormatWindSpeed(current.WindSpeed);
        WindText = $"{WindValueText} {GetWindDirectionText(current.WindDirection)}";
        
        PressureValueText = $"{(int)current.Pressure} hPa";
        PressureText = _localizationService.Format("Weather.PressureLabel", PressureValueText);
        
        LocationDisplay = string.IsNullOrWhiteSpace(data.LocationName)
            ? _locationName
            : data.LocationName;
        
        // Daily forecast
        if (data.Daily is not null)
        {
            PopulateDailyForecast(data.Daily);
        
            if (data.Daily.UvIndexMax.Count > 0)
            {
                double uv = data.Daily.UvIndexMax[0];
                UvIndexValueText = $"{uv:0}";
                UvIndexText = _localizationService.Format("Weather.UVLabel", UvIndexValueText);
            }
        
            if (data.Daily.PrecipitationProbabilityMax.Count > 0)
            {
                double precip = data.Daily.PrecipitationProbabilityMax[0];
                PrecipitationValueText = $"{(int)precip}%";
                PrecipitationText = _localizationService.Format("Weather.PrecipChance", PrecipitationValueText);
            }

            if (data.Daily.Sunrise.Count > 0)
            {
                SunriseText = FormatTime(data.Daily.Sunrise[0]);
            }

            if (data.Daily.Sunset.Count > 0)
            {
                SunsetText = FormatTime(data.Daily.Sunset[0]);
            }
        }
        else
        {
            DailyForecast.Clear();
            OnPropertyChanged(nameof(DailyForecastItemsSource));
            UvIndexValueText = string.Empty;
            UvIndexText = string.Empty;
            PrecipitationValueText = string.Empty;
            PrecipitationText = string.Empty;
            SunriseText = string.Empty;
            SunsetText = string.Empty;
        }

        // Hourly forecast
        if (data.Hourly is not null)
        {
            PopulateHourlyForecast(data.Hourly);
        }
        else
        {
            HourlyForecast.Clear();
            OnPropertyChanged(nameof(HourlyForecastItemsSource));
        }

        // Update rich skin gradient based on condition
        UpdateRichSkinColors();

        // Raise all visibility/animation property changes
        OnPropertyChanged(nameof(ForecastVisibility));
        OnPropertyChanged(nameof(WeekForecastVisibility));
        OnPropertyChanged(nameof(SunriseVisibility));
        OnPropertyChanged(nameof(UvIndexVisibility));
        OnPropertyChanged(nameof(PrecipitationVisibility));
        OnPropertyChanged(nameof(HumidityVisibility));
        OnPropertyChanged(nameof(WindVisibility));
        OnPropertyChanged(nameof(PressureVisibility));
        OnPropertyChanged(nameof(RainAnimationVisibility));
        OnPropertyChanged(nameof(SnowAnimationVisibility));
        OnPropertyChanged(nameof(ThunderAnimationVisibility));
        OnPropertyChanged(nameof(ClearAnimationVisibility));
        OnPropertyChanged(nameof(MiniHumidityText));
        OnPropertyChanged(nameof(MiniWindText));
        OnPropertyChanged(nameof(MiniPrecipText));
        OnPropertyChanged(nameof(MiniHumidityVisibility));
        OnPropertyChanged(nameof(MiniWindVisibility));
        OnPropertyChanged(nameof(MiniPrecipVisibility));
        OnPropertyChanged(nameof(MiniLocationVisibility));
        OnPropertyChanged(nameof(MiniDescriptionVisibility));
        OnPropertyChanged(nameof(MiniDetailsVisibility));
    }

    private void UpdateRichSkinColors()
    {
        // Each Rich gradient stop keeps at least a 4.5:1 contrast ratio against
        // white text. The same pair is also reused by the collapsed capsule so
        // both presentations remain visually identical.
        (Color top, Color bottom) = _currentCondition switch
        {
            WeatherCodeMapper.WeatherCondition.Clear when IsDay =>
                (Color.FromArgb(0xFF, 0x1F, 0x5F, 0x9B), Color.FromArgb(0xFF, 0x2D, 0x72, 0x97)),
            WeatherCodeMapper.WeatherCondition.Clear when !IsDay =>
                (Color.FromArgb(0xFF, 0x10, 0x19, 0x2E), Color.FromArgb(0xFF, 0x29, 0x3D, 0x69)),
            WeatherCodeMapper.WeatherCondition.Cloudy =>
                (Color.FromArgb(0xFF, 0x3C, 0x52, 0x66), Color.FromArgb(0xFF, 0x52, 0x68, 0x78)),
            WeatherCodeMapper.WeatherCondition.Rain or WeatherCodeMapper.WeatherCondition.Drizzle =>
                (Color.FromArgb(0xFF, 0x15, 0x3A, 0x5A), Color.FromArgb(0xFF, 0x35, 0x6B, 0x88)),
            WeatherCodeMapper.WeatherCondition.Snow =>
                (Color.FromArgb(0xFF, 0x4B, 0x69, 0x7A), Color.FromArgb(0xFF, 0x5C, 0x72, 0x7E)),
            WeatherCodeMapper.WeatherCondition.Thunderstorm =>
                (Color.FromArgb(0xFF, 0x25, 0x21, 0x3E), Color.FromArgb(0xFF, 0x51, 0x4B, 0x74)),
            WeatherCodeMapper.WeatherCondition.Fog =>
                (Color.FromArgb(0xFF, 0x53, 0x62, 0x6F), Color.FromArgb(0xFF, 0x60, 0x71, 0x7E)),
            _ => (Color.FromArgb(0xFF, 0x28, 0x5F, 0x8E), Color.FromArgb(0xFF, 0x3C, 0x76, 0x94))
        };

        RichBackdropTopColor = top;
        RichBackdropBottomColor = bottom;

        bool needsLightText = UsesRichSkin && ShouldUseLightText(top, bottom);
        if (RichSkinUsesLightText != needsLightText)
        {
            RichSkinUsesLightText = needsLightText;
            OnPropertyChanged(nameof(RichSkinUsesLightText));
        }

        OnPropertyChanged(nameof(RichBackdropTopColor));
        OnPropertyChanged(nameof(RichBackdropBottomColor));
    }

    private void PopulateDailyForecast(WeatherDaily daily)
    {
        DailyForecast.Clear();
        int count = Math.Min(daily.Time.Count, 7);
    
        // First pass: find the overall weekly min/max for the temperature range bar
        double weekMin = double.MaxValue, weekMax = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            double tMax = i < daily.TemperatureMax.Count ? daily.TemperatureMax[i] : 0;
            double tMin = i < daily.TemperatureMin.Count ? daily.TemperatureMin[i] : 0;
            if (tMax > weekMax) weekMax = tMax;
            if (tMin < weekMin) weekMin = tMin;
        }
        double weekRange = weekMax - weekMin;
        if (weekRange < 1) weekRange = 1; // avoid division by zero
    
        string lang = _localizationService.CurrentCultureName;
        for (int i = 0; i < count; i++)
        {
            string dateStr = i < daily.Time.Count ? daily.Time[i] : string.Empty;
            int wmoCode = i < daily.WeatherCode.Count ? daily.WeatherCode[i] : 0;
            double tempMax = i < daily.TemperatureMax.Count ? daily.TemperatureMax[i] : 0;
            double tempMin = i < daily.TemperatureMin.Count ? daily.TemperatureMin[i] : 0;
            double precipProb = i < daily.PrecipitationProbabilityMax.Count ? daily.PrecipitationProbabilityMax[i] : 0;
    
            string dayLabel;
            if (i == 0)
            {
                dayLabel = _localizationService.T("Weather.Today");
            }
            else if (i == 1)
            {
                dayLabel = _localizationService.T("Weather.Tomorrow");
            }
            else
            {
                dayLabel = ParseDateToDayLabel(dateStr, lang);
            }
    
            double barOffset = (tempMin - weekMin) / weekRange;
            double barWidth = (tempMax - tempMin) / weekRange;
            barOffset = Math.Clamp(barOffset, 0, 1);
            barWidth = Math.Clamp(barWidth, 0.04, 1); // minimum visible width
    
            DailyForecast.Add(new WeatherDayViewModel
            {
                DayLabel = dayLabel,
                Emoji = WeatherCodeMapper.GetEmoji(wmoCode, isDay: true),
                IconGlyph = WeatherCodeMapper.GetGlyph(wmoCode, isDay: true),
                Description = WeatherCodeMapper.GetDescription(wmoCode, lang),
                TempMaxText = FormatTemperature(tempMax),
                TempMinText = FormatTemperature(tempMin),
                PrecipitationText = $"{(int)precipProb}%",
                TempBarOffset = barOffset,
                TempBarWidth = barWidth
            });
        }

        OnPropertyChanged(nameof(DailyForecastItemsSource));
    }

    private void PopulateHourlyForecast(WeatherHourly hourly)
    {
        HourlyForecast.Clear();
        int startIndex = FindCurrentHourIndex(hourly.Time);
        int count = Math.Min(24, hourly.Time.Count - startIndex);
        for (int i = 0; i < count; i++)
        {
            int idx = startIndex + i;
            if (idx >= hourly.Time.Count)
            {
                break;
            }

            string timeStr = hourly.Time[idx];
            double temp = idx < hourly.Temperature.Count ? hourly.Temperature[idx] : 0;
            double precip = idx < hourly.PrecipitationProbability.Count ? hourly.PrecipitationProbability[idx] : 0;
            int wmoCode = idx < hourly.WeatherCode.Count ? hourly.WeatherCode[idx] : 0;

            string hourLabel = FormatHourLabel(timeStr);
            bool isDaytime = IsDaytimeHour(timeStr);

            HourlyForecast.Add(new WeatherHourViewModel
            {
                HourLabel = hourLabel,
                TemperatureText = FormatTemperature(temp),
                PrecipitationText = precip > 0 ? $"{(int)precip}%" : "",
                Emoji = WeatherCodeMapper.GetEmoji(wmoCode, isDaytime),
                IconGlyph = WeatherCodeMapper.GetGlyph(wmoCode, isDaytime),
                IsCurrentHour = i == 0,
                ForecastHourTextSize = this.ForecastHourTextSize
            });
        }

        OnPropertyChanged(nameof(HourlyForecastItemsSource));
    }

    private static int FindCurrentHourIndex(List<string> times)
    {
        if (times.Count == 0)
        {
            return 0;
        }

        string now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH");
        for (int i = 0; i < times.Count; i++)
        {
            if (times[i].StartsWith(now, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool IsDaytimeHour(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.Hour >= 6 && dt.Hour < 19;
        }
        catch
        {
            return true;
        }
    }

    private static string FormatHourLabel(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.Hour == 0 ? "0:00" : $"{dt.Hour}:00";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatTime(string isoTime)
    {
        try
        {
            var dt = DateTimeOffset.Parse(isoTime);
            return dt.ToString("HH:mm");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ParseDateToDayLabel(string dateStr, string language)
    {
        try
        {
            var dt = DateTimeOffset.Parse(dateStr);
            var culture = language switch
            {
                "zh-CN" => new System.Globalization.CultureInfo("zh-CN"),
                "zh-TW" => new System.Globalization.CultureInfo("zh-TW"),
                "ja-JP" => new System.Globalization.CultureInfo("ja-JP"),
                "de-DE" => new System.Globalization.CultureInfo("de-DE"),
                "pt-BR" => new System.Globalization.CultureInfo("pt-BR"),
                "hi-IN" => new System.Globalization.CultureInfo("hi-IN"),
                "es-ES" => new System.Globalization.CultureInfo("es-ES"),
                "fr-FR" => new System.Globalization.CultureInfo("fr-FR"),
                "ar-SA" => new System.Globalization.CultureInfo("ar-SA"),
                "bn-BD" => new System.Globalization.CultureInfo("bn-BD"),
                "ru-RU" => new System.Globalization.CultureInfo("ru-RU"),
                _ => new System.Globalization.CultureInfo("en-US")
            };
            return dt.ToString("ddd", culture);
        }
        catch
        {
            return dateStr;
        }
    }

    private string FormatTemperature(double celsius)
    {
        double value = _temperatureUnit == WeatherSettingsContext.WeatherTemperatureUnitFahrenheit
            ? celsius * 9 / 5 + 32
            : celsius;
        string unit = _temperatureUnit == WeatherSettingsContext.WeatherTemperatureUnitFahrenheit ? "\u00B0F" : "\u00B0C";
        return $"{Math.Round(value)}{unit}";
    }

    private string FormatWindSpeed(double kmh)
    {
        double value;
        string unit;
        if (_windSpeedUnit == WeatherSettingsContext.WeatherWindSpeedUnitMs)
        {
            value = kmh / 3.6;
            unit = "m/s";
        }
        else if (_windSpeedUnit == WeatherSettingsContext.WeatherWindSpeedUnitMph)
        {
            value = kmh / 1.609;
            unit = "mph";
        }
        else
        {
            value = kmh;
            unit = "km/h";
        }

        return $"{Math.Round(value, 1)} {unit}";
    }

    private string GetWindDirectionText(double direction)
    {
        string[] keys = ["Weather.Wind.N", "Weather.Wind.NE", "Weather.Wind.E", "Weather.Wind.SE", "Weather.Wind.S", "Weather.Wind.SW", "Weather.Wind.W", "Weather.Wind.NW"];
        int index = (int)Math.Round(direction / 45) % 8;
        return _localizationService.T(keys[index]);
    }

    /// <summary>
    /// P2-1: Refines the IP-location city name by looking up the nearest city
    /// in the local database. If a close match is found (within 80 km),
    /// returns the localized database name; otherwise keeps the original.
    /// </summary>
    private static string RefineLocationName(double lat, double lon, string originalName, string language)
    {
        try
        {
            var nearest = Services.CitySearchService.GetNearestCityName(lat, lon, language);
            if (!string.IsNullOrEmpty(nearest))
            {
                PackageLogger.Log($"[Weather] Refined location name: '{originalName}' → '{nearest}'");
                return nearest;
            }
        }
        catch (Exception ex)
        {
            PackageLogger.Log($"[Weather] RefineLocationName failed: {ex.Message}");
        }

        return originalName;
    }
}
