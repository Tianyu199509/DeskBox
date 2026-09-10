using DeskBox.WeatherPackage.Models;

namespace DeskBox.WeatherPackage.Services;

internal enum WeatherDisplayOption
{
    Forecast,
    Sunrise,
    UvIndex,
    Precipitation,
    Humidity,
    Wind,
    Pressure
}

/// <summary>
/// Applies the persisted, local-only weather preferences shared by the settings
/// surface and persistence validation. Network and location resolution do not
/// belong to this policy.
/// </summary>
internal static class WeatherSettingsPolicy
{
    internal static void SetAutoLocation(WeatherPreferences settings, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherAutoLocation = enabled;
    }

    internal static bool TrySetManualLocation(
        WeatherPreferences settings,
        string cityName,
        double latitude,
        double longitude)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(cityName);
        if (!double.IsFinite(latitude) ||
            !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 ||
            longitude is < -180 or > 180)
        {
            return false;
        }

        settings.WeatherAutoLocation = false;
        settings.WeatherCityName = cityName;
        settings.WeatherLatitude = latitude;
        settings.WeatherLongitude = longitude;
        return true;
    }

    internal static void SetTemperatureUnit(WeatherPreferences settings, string value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherTemperatureUnit =
            value == WeatherSettingsContext.WeatherTemperatureUnitFahrenheit
                ? WeatherSettingsContext.WeatherTemperatureUnitFahrenheit
                : WeatherSettingsContext.WeatherTemperatureUnitCelsius;
    }

    internal static void SetWindSpeedUnit(WeatherPreferences settings, string value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherWindSpeedUnit = value is
            WeatherSettingsContext.WeatherWindSpeedUnitMs or
            WeatherSettingsContext.WeatherWindSpeedUnitMph
                ? value
                : WeatherSettingsContext.WeatherWindSpeedUnitKmh;
    }

    internal static void SetDefaultView(WeatherPreferences settings, string value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherDefaultView = value == WeatherSettingsContext.WeatherDefaultViewWeek
            ? WeatherSettingsContext.WeatherDefaultViewWeek
            : WeatherSettingsContext.WeatherDefaultViewToday;
    }

    internal static void SetSkin(WeatherPreferences settings, string value)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherSkin = value == WeatherSettingsContext.WeatherSkinRich
            ? WeatherSettingsContext.WeatherSkinRich
            : WeatherSettingsContext.WeatherSkinStandard;
    }

    internal static void SetRefreshInterval(WeatherPreferences settings, int minutes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.WeatherRefreshIntervalMinutes = Math.Clamp(
            minutes,
            WeatherSettingsContext.WeatherRefreshMinMinutes,
            WeatherSettingsContext.WeatherRefreshMaxMinutes);
    }

    internal static void SetDisplayOption(
        WeatherPreferences settings,
        WeatherDisplayOption option,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (option)
        {
            case WeatherDisplayOption.Forecast:
                settings.WeatherShowForecast = enabled;
                break;
            case WeatherDisplayOption.Sunrise:
                settings.WeatherShowSunrise = enabled;
                break;
            case WeatherDisplayOption.UvIndex:
                settings.WeatherShowUvIndex = enabled;
                break;
            case WeatherDisplayOption.Precipitation:
                settings.WeatherShowPrecipitation = enabled;
                break;
            case WeatherDisplayOption.Humidity:
                settings.WeatherShowHumidity = enabled;
                break;
            case WeatherDisplayOption.Wind:
                settings.WeatherShowWind = enabled;
                break;
            case WeatherDisplayOption.Pressure:
                settings.WeatherShowPressure = enabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, null);
        }
    }
}
