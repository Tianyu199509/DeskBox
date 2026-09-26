using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the feature section: the music presentation
/// options, the weather options (city, units, default view, skin, data
/// source, display toggles, refresh interval — normalized through the shared
/// <see cref="WeatherSettingsPolicy"/>), the feature-card enable states and
/// the section's misc presentation picks (attachment storage, managed-drop
/// action, folder-open behavior). Every edit keeps the original semantics:
/// normalize through the existing shared normalizers, compare against the raw
/// stored value, skip unchanged writes, store on the slice, and schedule one
/// debounced save; the boolean returns report whether the persisted value
/// changed. The reset ports write the fresh-install defaults without
/// scheduling a save because the feature-card reset flow performs one
/// explicit save after applying every feature's defaults. Feature enable and
/// disable reach the host only through the shell's existing
/// <see cref="FeatureWidgetSettings"/> write plus the WidgetManager sync
/// chain — no widget, runtime or broadcast behavior lives here.
/// </summary>
public sealed class FeatureWidgetsSettingsCoordinator : IFeatureWidgetsSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public FeatureWidgetsSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public void SetFeatureWidgetEnabled(WidgetKind kind, bool enabled)
    {
        ThrowIfStopped();
        // Write-only port: the caller's WidgetManager sync chain owns the
        // persistence for this path, exactly like the shell did.
        FeatureWidgetSettings.SetEnabled(_settings.Settings, kind, enabled);
    }

    public bool SetMusicDisplayMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeMusicDisplayMode(mode);
        MusicSettingsSlice music = _settings.Settings.Music;
        if (string.Equals(music.MusicDisplayMode, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        music.MusicDisplayMode = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetMusicUseArtworkBackdrop(bool value)
    {
        ThrowIfStopped();
        MusicSettingsSlice music = _settings.Settings.Music;
        if (music.MusicUseArtworkBackdrop == value)
        {
            return false;
        }

        music.MusicUseArtworkBackdrop = value;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetMusicEnableCoverHoverMotion(bool value)
    {
        ThrowIfStopped();
        MusicSettingsSlice music = _settings.Settings.Music;
        if (music.MusicEnableCoverHoverMotion == value)
        {
            return false;
        }

        music.MusicEnableCoverHoverMotion = value;
        _settings.SaveDebounced();
        return true;
    }

    public void ResetMusicPresentationPreferences(bool scheduleSave = true)
    {
        ThrowIfStopped();
        MusicSettingsSlice music = _settings.Settings.Music;
        bool changed =
            music.MusicUseArtworkBackdrop != true ||
            music.MusicEnableCoverHoverMotion != true ||
            !string.Equals(
                music.MusicDisplayMode,
                SettingsService.MusicDisplayModeAuto,
                StringComparison.Ordinal);
        music.MusicUseArtworkBackdrop = true;
        music.MusicEnableCoverHoverMotion = true;
        music.MusicDisplayMode = SettingsService.MusicDisplayModeAuto;
        if (scheduleSave && changed)
        {
            _settings.SaveDebounced();
        }
    }

    public bool SetWeatherTemperatureUnit(string? unit)
    {
        ThrowIfStopped();
        string input = unit ?? string.Empty;
        return ApplyWeatherPolicy(
            weather => weather.WeatherTemperatureUnit,
            settings => WeatherSettingsPolicy.SetTemperatureUnit(settings, input));
    }

    public bool SetWeatherWindSpeedUnit(string? unit)
    {
        ThrowIfStopped();
        string input = unit ?? string.Empty;
        return ApplyWeatherPolicy(
            weather => weather.WeatherWindSpeedUnit,
            settings => WeatherSettingsPolicy.SetWindSpeedUnit(settings, input));
    }

    public bool SetWeatherDefaultView(string? view)
    {
        ThrowIfStopped();
        string input = view ?? string.Empty;
        return ApplyWeatherPolicy(
            weather => weather.WeatherDefaultView,
            settings => WeatherSettingsPolicy.SetDefaultView(settings, input));
    }

    public bool SetWeatherSkin(string? skin)
    {
        ThrowIfStopped();
        string input = skin ?? string.Empty;
        return ApplyWeatherPolicy(
            weather => weather.WeatherSkin,
            settings => WeatherSettingsPolicy.SetSkin(settings, input));
    }

    public bool SetWeatherDataSource(string? source)
    {
        ThrowIfStopped();
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        string normalized = source == SettingsService.WeatherDataSourceOpenMeteo
            ? SettingsService.WeatherDataSourceOpenMeteo
            : SettingsService.WeatherDataSourceMsn;
        if (string.Equals(weather.WeatherDataSource, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        weather.WeatherDataSource = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetWeatherRefreshInterval(int minutes)
    {
        ThrowIfStopped();
        return ApplyWeatherPolicy(
            weather => weather.WeatherRefreshIntervalMinutes,
            settings => WeatherSettingsPolicy.SetRefreshInterval(settings, minutes));
    }

    public bool SetWeatherAutoLocation(bool enabled)
    {
        ThrowIfStopped();
        return ApplyWeatherPolicy(
            weather => weather.WeatherAutoLocation,
            settings => WeatherSettingsPolicy.SetAutoLocation(settings, enabled));
    }

    public bool TrySetWeatherManualLocation(
        string cityName,
        double latitude,
        double longitude)
    {
        ThrowIfStopped();
        ArgumentNullException.ThrowIfNull(cityName);
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        bool changed =
            weather.WeatherAutoLocation != false ||
            !string.Equals(weather.WeatherCityName, cityName, StringComparison.Ordinal) ||
            weather.WeatherLatitude != latitude ||
            weather.WeatherLongitude != longitude;
        if (!WeatherSettingsPolicy.TrySetManualLocation(
                _settings.Settings, cityName, latitude, longitude))
        {
            return false;
        }

        if (changed)
        {
            _settings.SaveDebounced();
        }

        return true;
    }

    public bool SetWeatherDisplayOption(string option, bool enabled)
    {
        ThrowIfStopped();
        WeatherDisplayOption displayOption = option switch
        {
            "Forecast" => WeatherDisplayOption.Forecast,
            "Sunrise" => WeatherDisplayOption.Sunrise,
            "UvIndex" => WeatherDisplayOption.UvIndex,
            "Precipitation" => WeatherDisplayOption.Precipitation,
            "Humidity" => WeatherDisplayOption.Humidity,
            "Wind" => WeatherDisplayOption.Wind,
            "Pressure" => WeatherDisplayOption.Pressure,
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
        };

        WeatherSettingsSlice weather = _settings.Settings.Weather;
        bool before = ReadWeatherDisplayOption(weather, displayOption);
        WeatherSettingsPolicy.SetDisplayOption(
            _settings.Settings, displayOption, enabled);
        if (before == enabled)
        {
            return false;
        }

        _settings.SaveDebounced();
        return true;
    }

    public void ResetWeatherPreferences(bool scheduleSave = true)
    {
        ThrowIfStopped();
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        bool changed =
            weather.WeatherAutoLocation != true ||
            !string.Equals(weather.WeatherCityName, string.Empty, StringComparison.Ordinal) ||
            weather.WeatherLatitude != 0 ||
            weather.WeatherLongitude != 0 ||
            !string.Equals(
                weather.WeatherTemperatureUnit,
                SettingsService.WeatherTemperatureUnitCelsius,
                StringComparison.Ordinal) ||
            !string.Equals(
                weather.WeatherWindSpeedUnit,
                SettingsService.WeatherWindSpeedUnitKmh,
                StringComparison.Ordinal) ||
            !string.Equals(
                weather.WeatherDefaultView,
                SettingsService.WeatherDefaultViewToday,
                StringComparison.Ordinal) ||
            !string.Equals(
                weather.WeatherSkin,
                SettingsService.WeatherSkinRich,
                StringComparison.Ordinal) ||
            weather.WeatherShowForecast != true ||
            weather.WeatherShowSunrise != true ||
            weather.WeatherShowUvIndex != true ||
            weather.WeatherShowPrecipitation != true ||
            weather.WeatherShowHumidity != true ||
            weather.WeatherShowWind != true ||
            weather.WeatherShowPressure != false ||
            weather.WeatherRefreshIntervalMinutes != 60;
        weather.WeatherAutoLocation = true;
        weather.WeatherCityName = string.Empty;
        weather.WeatherLatitude = 0;
        weather.WeatherLongitude = 0;
        weather.WeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
        weather.WeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
        weather.WeatherDefaultView = SettingsService.WeatherDefaultViewToday;
        weather.WeatherSkin = SettingsService.WeatherSkinRich;
        weather.WeatherShowForecast = true;
        weather.WeatherShowSunrise = true;
        weather.WeatherShowUvIndex = true;
        weather.WeatherShowPrecipitation = true;
        weather.WeatherShowHumidity = true;
        weather.WeatherShowWind = true;
        weather.WeatherShowPressure = false;
        weather.WeatherRefreshIntervalMinutes = 60;
        if (scheduleSave && changed)
        {
            _settings.SaveDebounced();
        }
    }

    public bool SetAttachmentStorageMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeAttachmentStorageMode(mode);
        QuickCaptureSettingsSlice quickCapture = _settings.Settings.QuickCapture;
        if (string.Equals(
                quickCapture.AttachmentStorageMode,
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        quickCapture.AttachmentStorageMode = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetManagedDropAction(string? action)
    {
        ThrowIfStopped();
        string normalized = action switch
        {
            SettingsService.ManagedDropActionCopy =>
                SettingsService.ManagedDropActionCopy,
            SettingsService.ManagedDropActionFollowWindows =>
                SettingsService.ManagedDropActionFollowWindows,
            _ => SettingsService.ManagedDropActionMove
        };
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(
                fileWidget.ManagedDropAction,
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        fileWidget.ManagedDropAction = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetFileWidgetFolderOpenBehavior(string? behavior)
    {
        ThrowIfStopped();
        string normalized =
            FileWidgetFolderOpenBehaviorNames.NormalizeGlobal(behavior);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (string.Equals(
                fileWidget.FileWidgetFolderOpenBehavior,
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        fileWidget.FileWidgetFolderOpenBehavior = normalized;
        _settings.SaveDebounced();
        return true;
    }

    /// <summary>
    /// Applies one <see cref="WeatherSettingsPolicy"/> write (the shared
    /// normalizer the page used before the migration), then skips the
    /// debounced save when the normalized value equals the raw stored value —
    /// the same unchanged-write skip every migrated section uses.
    /// </summary>
    private bool ApplyWeatherPolicy(
        Func<WeatherSettingsSlice, string> readString,
        Action<AppSettings> apply)
    {
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        string before = readString(weather);
        apply(_settings.Settings);
        if (string.Equals(before, readString(weather), StringComparison.Ordinal))
        {
            return false;
        }

        _settings.SaveDebounced();
        return true;
    }

    private bool ApplyWeatherPolicy(
        Func<WeatherSettingsSlice, bool> readBool,
        Action<AppSettings> apply)
    {
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        bool before = readBool(weather);
        apply(_settings.Settings);
        if (before == readBool(weather))
        {
            return false;
        }

        _settings.SaveDebounced();
        return true;
    }

    private bool ApplyWeatherPolicy(
        Func<WeatherSettingsSlice, int> readInt,
        Action<AppSettings> apply)
    {
        WeatherSettingsSlice weather = _settings.Settings.Weather;
        int before = readInt(weather);
        apply(_settings.Settings);
        if (before == readInt(weather))
        {
            return false;
        }

        _settings.SaveDebounced();
        return true;
    }

    private static bool ReadWeatherDisplayOption(
        WeatherSettingsSlice weather,
        WeatherDisplayOption option) => option switch
    {
        WeatherDisplayOption.Forecast => weather.WeatherShowForecast,
        WeatherDisplayOption.Sunrise => weather.WeatherShowSunrise,
        WeatherDisplayOption.UvIndex => weather.WeatherShowUvIndex,
        WeatherDisplayOption.Precipitation => weather.WeatherShowPrecipitation,
        WeatherDisplayOption.Humidity => weather.WeatherShowHumidity,
        WeatherDisplayOption.Wind => weather.WeatherShowWind,
        WeatherDisplayOption.Pressure => weather.WeatherShowPressure,
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, null)
    };

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
