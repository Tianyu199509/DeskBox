using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class WeatherWidgetViewModel
{
    private async Task EnsureLocationAsync()
    {
        if (_settingsService is null)
        {
            _latitude = 39.9042;
            _longitude = 116.4074;
            _locationName = "Beijing";
            return;
        }

        var settings = _settingsService.Settings;
        if (settings.WeatherAutoLocation)
        {
            // Try Windows location API first
            var (lat, lon, name) = await WindowsLocationHelper.GetLocationAsync(_localizationService);
            _latitude = lat;
            _longitude = lon;
            _locationName = name;
        }
        else
        {
            if (settings.WeatherLatitude != 0 || settings.WeatherLongitude != 0)
            {
                _latitude = settings.WeatherLatitude;
                _longitude = settings.WeatherLongitude;
                _locationName = settings.WeatherCityName;
            }
            else
            {
                // Try to resolve the saved city name
                if (!string.IsNullOrWhiteSpace(settings.WeatherCityName))
                {
                    var item = await _weatherService.ResolveCityAsync(
                        settings.WeatherCityName,
                        _localizationService.IsEnglish ? "en" : "zh");
                    if (item is not null)
                    {
                        _latitude = item.Latitude;
                        _longitude = item.Longitude;
                        _locationName = item.DisplayName;
                        settings.WeatherLatitude = _latitude;
                        settings.WeatherLongitude = _longitude;
                        _settingsService.SaveDebounced();
                    }
                }
                else
                {
                    _latitude = 39.9042;
                    _longitude = 116.4074;
                    _locationName = _localizationService.IsEnglish ? "Beijing" : "\u5317\u4EAC";
                }
            }
        }
    }

    private void ApplyWeatherData(WeatherData data)
    {
        if (data.Current is null)
        {
            return;
        }

        var current = data.Current;
        bool isEnglish = _localizationService.IsEnglish;

        IsDay = current.IsDay == 1;
        CurrentWeatherCode = current.WeatherCode;
        CurrentCondition = WeatherCodeMapper.GetCondition(current.WeatherCode);
        CurrentEmoji = WeatherCodeMapper.GetEmoji(current.WeatherCode, IsDay);
        CurrentIconGlyph = WeatherCodeMapper.GetGlyph(current.WeatherCode, IsDay);
        CurrentDescription = isEnglish
            ? WeatherCodeMapper.GetDescriptionEn(current.WeatherCode)
            : WeatherCodeMapper.GetDescriptionZh(current.WeatherCode);
        CurrentTemperatureText = FormatTemperature(current.Temperature);

        ApparentTemperatureText = isEnglish
            ? $"Feels {FormatTemperature(current.ApparentTemperature)}"
            : $"\u4F53\u611F {FormatTemperature(current.ApparentTemperature)}";

        HumidityText = isEnglish
            ? $"{(int)current.Humidity}%"
            : $"\u6E7F\u5EA6 {(int)current.Humidity}%";

        WindText = isEnglish
            ? $"{FormatWindSpeed(current.WindSpeed)} {GetWindDirectionText(current.WindDirection, isEnglish)}"
            : $"{FormatWindSpeed(current.WindSpeed)} {GetWindDirectionText(current.WindDirection, isEnglish)}";

        PressureText = isEnglish
            ? $"{(int)current.Pressure} hPa"
            : $"\u6C14\u538B {(int)current.Pressure} hPa";

        LocationDisplay = string.IsNullOrWhiteSpace(data.LocationName)
            ? _locationName
            : data.LocationName;

        // Daily forecast
        if (data.Daily is not null)
        {
            PopulateDailyForecast(data.Daily, isEnglish);

            if (data.Daily.UvIndexMax.Count > 0)
            {
                double uv = data.Daily.UvIndexMax[0];
                UvIndexText = isEnglish ? $"UV {uv:0}" : $"\u7D2B\u5916 {uv:0}";
            }

            if (data.Daily.PrecipitationProbabilityMax.Count > 0)
            {
                double precip = data.Daily.PrecipitationProbabilityMax[0];
                PrecipitationText = isEnglish
                    ? $"{(int)precip}% rain"
                    : $"\u964D\u6C34\u6982\u7387 {(int)precip}%";
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

        // Hourly forecast
        if (data.Hourly is not null)
        {
            PopulateHourlyForecast(data.Hourly);
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
    }

    private void UpdateRichSkinColors()
    {
        // Condition-based gradient colors inspired by premium weather apps
        (Color top, Color bottom) = _currentCondition switch
        {
            WeatherCodeMapper.WeatherCondition.Clear when IsDay =>
                (Color.FromArgb(0xFF, 0x2E, 0x86, 0xDE), Color.FromArgb(0xFF, 0x5A, 0xBF, 0xF3)),  // Sky blue
            WeatherCodeMapper.WeatherCondition.Clear when !IsDay =>
                (Color.FromArgb(0xFF, 0x1A, 0x1A, 0x3E), Color.FromArgb(0xFF, 0x2D, 0x35, 0x61)),  // Night dark blue
            WeatherCodeMapper.WeatherCondition.Cloudy =>
                (Color.FromArgb(0xFF, 0x57, 0x60, 0x6F), Color.FromArgb(0xFF, 0x77, 0x8C, 0xA3)),  // Gray
            WeatherCodeMapper.WeatherCondition.Rain or WeatherCodeMapper.WeatherCondition.Drizzle =>
                (Color.FromArgb(0xFF, 0x37, 0x47, 0x4F), Color.FromArgb(0xFF, 0x54, 0x6E, 0x7A)),  // Slate blue
            WeatherCodeMapper.WeatherCondition.Snow =>
                (Color.FromArgb(0xFF, 0x64, 0x7D, 0x8C), Color.FromArgb(0xFF, 0xA5, 0xB4, 0xC4)),  // Light gray-blue
            WeatherCodeMapper.WeatherCondition.Thunderstorm =>
                (Color.FromArgb(0xFF, 0x2A, 0x12, 0x3A), Color.FromArgb(0xFF, 0x3D, 0x1B, 0x5E)),  // Dark purple
            WeatherCodeMapper.WeatherCondition.Fog =>
                (Color.FromArgb(0xFF, 0x6E, 0x7B, 0x8B), Color.FromArgb(0xFF, 0x9C, 0xAA, 0xBC)),  // Foggy gray
            _ => (Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9), Color.FromArgb(0xFF, 0x74, 0xB9, 0xFF))   // Default
        };

        RichBackdropTopColor = top;
        RichBackdropBottomColor = bottom;

        // Determine if the rich skin background is dark enough to need light text.
        // This handles the case where light mode + night/dark-weather conditions
        // would result in dark text on a dark background.
        bool needsLightText = _skin == SettingsService.WeatherSkinRich &&
            (IsColorDark(top) || IsColorDark(bottom));
        if (RichSkinUsesLightText != needsLightText)
        {
            RichSkinUsesLightText = needsLightText;
            OnPropertyChanged(nameof(RichSkinUsesLightText));
        }

        OnPropertyChanged(nameof(RichBackdropTopColor));
        OnPropertyChanged(nameof(RichBackdropBottomColor));
    }

    private void PopulateDailyForecast(WeatherDaily daily, bool isEnglish)
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
                dayLabel = isEnglish ? "Today" : "\u4ECA\u5929";
            }
            else if (i == 1)
            {
                dayLabel = isEnglish ? "Tomorrow" : "\u660E\u5929";
            }
            else
            {
                dayLabel = ParseDateToDayLabel(dateStr, isEnglish);
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
                Description = isEnglish
                    ? WeatherCodeMapper.GetDescriptionEn(wmoCode)
                    : WeatherCodeMapper.GetDescriptionZh(wmoCode),
                TempMaxText = FormatTemperature(tempMax),
                TempMinText = FormatTemperature(tempMin),
                PrecipitationText = $"{(int)precipProb}%",
                TempBarOffset = barOffset,
                TempBarWidth = barWidth
            });
        }
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
                IsCurrentHour = i == 0
            });
        }
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

    private static string ParseDateToDayLabel(string dateStr, bool isEnglish)
    {
        try
        {
            var dt = DateTimeOffset.Parse(dateStr);
            string[] zhDays = ["\u5468\u65E5", "\u5468\u4E00", "\u5468\u4E8C", "\u5468\u4E09", "\u5468\u56DB", "\u5468\u4E94", "\u5468\u516D"];
            string[] enDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
            int dayOfWeek = ((int)dt.DayOfWeek + 6) % 7;
            return isEnglish ? enDays[dayOfWeek] : zhDays[dayOfWeek];
        }
        catch
        {
            return dateStr;
        }
    }

    private string FormatTemperature(double celsius)
    {
        double value = _temperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit
            ? celsius * 9 / 5 + 32
            : celsius;
        string unit = _temperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit ? "\u00B0F" : "\u00B0C";
        return $"{Math.Round(value)}{unit}";
    }

    private string FormatWindSpeed(double kmh)
    {
        double value;
        string unit;
        if (_windSpeedUnit == SettingsService.WeatherWindSpeedUnitMs)
        {
            value = kmh / 3.6;
            unit = "m/s";
        }
        else if (_windSpeedUnit == SettingsService.WeatherWindSpeedUnitMph)
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

    private static string GetWindDirectionText(double direction, bool isEnglish)
    {
        string[] zhDirs = ["\u5317", "\u4E1C\u5317", "\u4E1C", "\u4E1C\u5357", "\u5357", "\u897F\u5357", "\u897F", "\u897F\u5317"];
        string[] enDirs = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int index = (int)Math.Round(direction / 45) % 8;
        return isEnglish ? enDirs[index] : zhDirs[index];
    }
}
