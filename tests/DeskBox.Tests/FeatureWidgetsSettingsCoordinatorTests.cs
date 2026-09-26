using DeskBox.Contracts;
using DeskBox.Features.FeatureWidgets;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FeatureWidgetsSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MusicAndMiscWrites_PersistThroughTheEditorSeam_AndSkipUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        var editor = new FeatureWidgetsSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.True(editor.SetMusicDisplayMode(SettingsService.MusicDisplayModeCover));
        Assert.True(editor.SetMusicUseArtworkBackdrop(false));
        Assert.True(editor.SetMusicEnableCoverHoverMotion(false));
        Assert.True(editor.SetAttachmentStorageMode(SettingsService.AttachmentStorageModeCopy));
        Assert.True(editor.SetManagedDropAction(SettingsService.ManagedDropActionFollowWindows));
        Assert.True(editor.SetFileWidgetFolderOpenBehavior(
            FileWidgetFolderOpenBehaviorNames.Embedded));

        MusicSettingsSlice music = settings.Settings.Music;
        Assert.Equal(SettingsService.MusicDisplayModeCover, music.MusicDisplayMode);
        Assert.False(music.MusicUseArtworkBackdrop);
        Assert.False(music.MusicEnableCoverHoverMotion);
        Assert.Equal(SettingsService.AttachmentStorageModeCopy,
            settings.Settings.QuickCapture.AttachmentStorageMode);
        Assert.Equal(SettingsService.ManagedDropActionFollowWindows,
            settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(FileWidgetFolderOpenBehaviorNames.Embedded,
            settings.Settings.FileWidget.FileWidgetFolderOpenBehavior);
        Assert.Equal(6, notified);

        // Unchanged re-sends report no change and never schedule a second
        // debounced save, exactly like the page setters' SetProperty gate did
        // before the migration; invalid picks normalize through the same
        // page normalizers (music → Auto, storage → Link, drop → Move,
        // folder-open → Explorer), which do change the stored selection.
        Assert.True(editor.SetMusicDisplayMode("Nonsense"));
        Assert.False(editor.SetMusicUseArtworkBackdrop(false));
        Assert.False(editor.SetMusicEnableCoverHoverMotion(false));
        Assert.True(editor.SetAttachmentStorageMode("Nonsense"));
        Assert.True(editor.SetManagedDropAction("Nonsense"));
        Assert.True(editor.SetFileWidgetFolderOpenBehavior("Nonsense"));
        Assert.Equal(10, notified);
        // Invalid music mode falls back to Auto; invalid misc picks fall
        // back to Move / Link / Explorer, matching the page normalizers.
        Assert.Equal(SettingsService.MusicDisplayModeAuto, music.MusicDisplayMode);
        Assert.Equal(SettingsService.ManagedDropActionMove,
            settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.AttachmentStorageModeLink,
            settings.Settings.QuickCapture.AttachmentStorageMode);
        Assert.Equal(FileWidgetFolderOpenBehaviorNames.Explorer,
            settings.Settings.FileWidget.FileWidgetFolderOpenBehavior);
    }

    [Fact]
    public void WeatherWrites_FollowTheSharedPolicy_AndSkipUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.True(coordinator.SetWeatherTemperatureUnit(
            SettingsService.WeatherTemperatureUnitFahrenheit));
        Assert.True(coordinator.SetWeatherWindSpeedUnit(SettingsService.WeatherWindSpeedUnitMph));
        Assert.True(coordinator.SetWeatherDefaultView(SettingsService.WeatherDefaultViewWeek));
        // Fresh-install skin default is Standard, so re-selecting it is a
        // no-op write; the flip to Rich below is the real change.
        Assert.False(coordinator.SetWeatherSkin(SettingsService.WeatherSkinStandard));
        Assert.True(coordinator.SetWeatherDataSource(SettingsService.WeatherDataSourceOpenMeteo));
        Assert.True(coordinator.SetWeatherRefreshInterval(5));
        Assert.True(coordinator.SetWeatherAutoLocation(false));
        Assert.True(coordinator.SetWeatherDisplayOption("Forecast", false));
        Assert.True(coordinator.SetWeatherDisplayOption("Pressure", true));
        Assert.Equal(8, notified);

        WeatherSettingsSlice weather = settings.Settings.Weather;
        Assert.Equal(SettingsService.WeatherTemperatureUnitFahrenheit,
            weather.WeatherTemperatureUnit);
        Assert.Equal(SettingsService.WeatherWindSpeedUnitMph, weather.WeatherWindSpeedUnit);
        Assert.Equal(SettingsService.WeatherDefaultViewWeek, weather.WeatherDefaultView);
        Assert.Equal(SettingsService.WeatherSkinStandard, weather.WeatherSkin);
        Assert.Equal(SettingsService.WeatherDataSourceOpenMeteo, weather.WeatherDataSource);
        Assert.Equal(SettingsService.WeatherRefreshMinMinutes,
            weather.WeatherRefreshIntervalMinutes);
        Assert.False(weather.WeatherAutoLocation);
        Assert.False(weather.WeatherShowForecast);
        Assert.True(weather.WeatherShowPressure);

        // Invalid values normalize through the same WeatherSettingsPolicy the
        // page used (Celsius / km/h / Today / Standard / MSN), clamped to the
        // refresh bounds; unchanged writes are skipped without a save.
        Assert.True(coordinator.SetWeatherTemperatureUnit(null));
        Assert.True(coordinator.SetWeatherWindSpeedUnit("Nonsense"));
        Assert.True(coordinator.SetWeatherDefaultView("Nonsense"));
        // The skin is already the Standard default, so the invalid pick
        // normalizes back to it without a write or a save.
        Assert.False(coordinator.SetWeatherSkin(null));
        Assert.True(coordinator.SetWeatherDataSource("Nonsense"));
        Assert.True(coordinator.SetWeatherRefreshInterval(10_000));
        Assert.False(coordinator.SetWeatherDisplayOption("Pressure", true));
        Assert.Equal(
            SettingsService.WeatherTemperatureUnitCelsius,
            weather.WeatherTemperatureUnit);
        Assert.Equal(SettingsService.WeatherWindSpeedUnitKmh, weather.WeatherWindSpeedUnit);
        Assert.Equal(SettingsService.WeatherDefaultViewToday, weather.WeatherDefaultView);
        Assert.Equal(SettingsService.WeatherSkinStandard, weather.WeatherSkin);
        Assert.Equal(SettingsService.WeatherDataSourceMsn, weather.WeatherDataSource);
        Assert.Equal(SettingsService.WeatherRefreshMaxMinutes,
            weather.WeatherRefreshIntervalMinutes);
        // Only the five real flips above saved; the two unchanged writes
        // (skin, pressure) neither wrote nor scheduled a save.
        Assert.Equal(13, notified);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => coordinator.SetWeatherDisplayOption("Nonsense", true));
    }

    [Fact]
    public void ManualLocation_FollowsThePolicyRejectingInvalidCoordinates()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);

        Assert.False(coordinator.TrySetWeatherManualLocation(
            "Nowhere", 91, 0));
        Assert.False(coordinator.TrySetWeatherManualLocation(
            "Nowhere", 0, double.NaN));
        Assert.True(settings.Settings.Weather.WeatherAutoLocation);

        Assert.True(coordinator.TrySetWeatherManualLocation("Hanoi", 21.03, 105.85));
        WeatherSettingsSlice weather = settings.Settings.Weather;
        Assert.False(weather.WeatherAutoLocation);
        Assert.Equal("Hanoi", weather.WeatherCityName);
        Assert.Equal(21.03, weather.WeatherLatitude);
        Assert.Equal(105.85, weather.WeatherLongitude);

        // Re-picking the same city is accepted (the policy validates it) but
        // the unchanged write schedules no save.
        int notified = 0;
        settings.SettingsChanged += () => notified++;
        Assert.True(coordinator.TrySetWeatherManualLocation("Hanoi", 21.03, 105.85));
        Assert.Equal(0, notified);
    }

    [Fact]
    public async Task FeatureResetDefaults_ApplyWithoutSchedulingASave_AndPersistOnTheCallerSave()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        coordinator.SetMusicDisplayMode(SettingsService.MusicDisplayModeControls);
        coordinator.SetMusicUseArtworkBackdrop(false);
        coordinator.SetMusicEnableCoverHoverMotion(false);
        coordinator.SetWeatherDataSource(SettingsService.WeatherDataSourceOpenMeteo);
        coordinator.SetWeatherAutoLocation(false);
        coordinator.TrySetWeatherManualLocation("Hanoi", 21.03, 105.85);
        coordinator.SetWeatherTemperatureUnit(SettingsService.WeatherTemperatureUnitFahrenheit);
        coordinator.SetWeatherDisplayOption("Wind", false);
        coordinator.SetWeatherDisplayOption("Pressure", true);
        coordinator.SetWeatherRefreshInterval(180);
        int notified = 0;
        settings.SettingsChanged += () => notified++;
        int beforeReset = notified;

        // The feature-card reset flow applies defaults for every feature and
        // then performs one explicit save: the reset ports must not schedule
        // intermediate debounced saves of their own.
        coordinator.ResetMusicPresentationPreferences(scheduleSave: false);
        coordinator.ResetWeatherPreferences(scheduleSave: false);
        Assert.Equal(beforeReset, notified);

        MusicSettingsSlice music = settings.Settings.Music;
        Assert.True(music.MusicUseArtworkBackdrop);
        Assert.True(music.MusicEnableCoverHoverMotion);
        Assert.Equal(SettingsService.MusicDisplayModeAuto, music.MusicDisplayMode);
        WeatherSettingsSlice weather = settings.Settings.Weather;
        Assert.True(weather.WeatherAutoLocation);
        Assert.Equal(string.Empty, weather.WeatherCityName);
        Assert.Equal(0, weather.WeatherLatitude);
        Assert.Equal(0, weather.WeatherLongitude);
        Assert.Equal(
            SettingsService.WeatherTemperatureUnitCelsius,
            weather.WeatherTemperatureUnit);
        Assert.Equal(SettingsService.WeatherWindSpeedUnitKmh, weather.WeatherWindSpeedUnit);
        Assert.Equal(SettingsService.WeatherDefaultViewToday, weather.WeatherDefaultView);
        Assert.Equal(SettingsService.WeatherSkinRich, weather.WeatherSkin);
        Assert.Equal(SettingsService.WeatherDataSourceOpenMeteo, weather.WeatherDataSource);
        Assert.True(weather.WeatherShowForecast);
        Assert.True(weather.WeatherShowSunrise);
        Assert.True(weather.WeatherShowUvIndex);
        Assert.True(weather.WeatherShowPrecipitation);
        Assert.True(weather.WeatherShowHumidity);
        Assert.True(weather.WeatherShowWind);
        Assert.False(weather.WeatherShowPressure);
        Assert.Equal(60, weather.WeatherRefreshIntervalMinutes);

        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.MusicDisplayModeAuto,
            reloaded.Settings.Music.MusicDisplayMode);
        Assert.True(reloaded.Settings.Music.MusicUseArtworkBackdrop);
        Assert.Equal(string.Empty, reloaded.Settings.Weather.WeatherCityName);
        Assert.True(reloaded.Settings.Weather.WeatherAutoLocation);
        Assert.True(reloaded.Settings.Weather.WeatherShowWind);
        Assert.False(reloaded.Settings.Weather.WeatherShowPressure);
        Assert.Equal(60, reloaded.Settings.Weather.WeatherRefreshIntervalMinutes);
        // Data source is not part of the weather reset (the page reset block
        // never wrote it either); the user's explicit pick survives.
        Assert.Equal(SettingsService.WeatherDataSourceOpenMeteo,
            reloaded.Settings.Weather.WeatherDataSource);
    }

    [Fact]
    public async Task FeatureCardEnableState_WritesWithoutSchedulingASave_AndPersistsOnSave()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // The enable port only writes the persisted flag; the shell's
        // WidgetManager sync chain owns the save, exactly like before.
        coordinator.SetFeatureWidgetEnabled(WidgetKind.Music, false);
        coordinator.SetFeatureWidgetEnabled(WidgetKind.Weather, false);
        coordinator.SetFeatureWidgetEnabled(WidgetKind.Glance, true);
        Assert.Equal(0, notified);
        Assert.False(FeatureWidgetSettings.IsEnabled(
            settings.Settings, WidgetKind.Music));
        Assert.False(FeatureWidgetSettings.IsEnabled(
            settings.Settings, WidgetKind.Weather));
        Assert.True(FeatureWidgetSettings.IsEnabled(
            settings.Settings, WidgetKind.Glance));

        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.False(FeatureWidgetSettings.IsEnabled(
            reloaded.Settings, WidgetKind.Music));
        Assert.True(FeatureWidgetSettings.IsEnabled(
            reloaded.Settings, WidgetKind.Glance));
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        await settings.LoadAsync();
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        coordinator.SetMusicUseArtworkBackdrop(false);
        coordinator.SetWeatherDataSource(SettingsService.WeatherDataSourceOpenMeteo);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetMusicDisplayMode(SettingsService.MusicDisplayModeCover));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetMusicUseArtworkBackdrop(true));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetMusicEnableCoverHoverMotion(false));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.ResetMusicPresentationPreferences(scheduleSave: false));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWeatherDataSource(SettingsService.WeatherDataSourceMsn));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWeatherAutoLocation(false));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.TrySetWeatherManualLocation("Hanoi", 21.03, 105.85));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWeatherDisplayOption("Wind", false));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.ResetWeatherPreferences(scheduleSave: false));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetAttachmentStorageMode(SettingsService.AttachmentStorageModeCopy));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetManagedDropAction(SettingsService.ManagedDropActionCopy));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFileWidgetFolderOpenBehavior(
                FileWidgetFolderOpenBehaviorNames.Embedded));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetFeatureWidgetEnabled(WidgetKind.Glance, true));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.False(reloaded.Settings.Music.MusicUseArtworkBackdrop);
        Assert.Equal(SettingsService.WeatherDataSourceOpenMeteo,
            reloaded.Settings.Weather.WeatherDataSource);
        Assert.Equal(SettingsService.MusicDisplayModeAuto,
            reloaded.Settings.Music.MusicDisplayMode);
        Assert.False(FeatureWidgetSettings.IsEnabled(
            reloaded.Settings, WidgetKind.Glance));
    }
}
