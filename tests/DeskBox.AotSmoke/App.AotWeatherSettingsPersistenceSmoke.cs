#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotWeatherBaselineCity = "Shanghai AOT Baseline";
    private const double AotWeatherBaselineLatitude = 31.2304;
    private const double AotWeatherBaselineLongitude = 121.4737;
    private const string AotWeatherMutatedCity = "Chengdu AOT Mutation";
    private const double AotWeatherMutatedLatitude = 30.5728;
    private const double AotWeatherMutatedLongitude = 104.0668;

    private async Task CaptureAotManagedUiWeatherSettingsPersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotManagedUiWeatherSettingsPersistenceEvidence evidence =
            result.WeatherSettingsPersistence ??
            throw new InvalidOperationException(
                "Weather settings persistence evidence is unavailable.");

        evidence.Before = CaptureAotWeatherSettingsState(manager);
        RequireAotWeatherSettingsHostSuppressed(result, evidence.Before);

        switch (phase)
        {
            case AotManagedUiWeatherSettingsMutatePhase:
                RequireAotWeatherSettingsState(
                    result,
                    evidence.Before,
                    expectMutation: false,
                    "WeatherSettingsBaselineVerified");
                ApplyAotWeatherSettingsState(manager, useMutation: true);
                break;

            case AotManagedUiWeatherSettingsVerifyRestorePhase:
                RequireAotWeatherSettingsState(
                    result,
                    evidence.Before,
                    expectMutation: true,
                    "WeatherSettingsRestartVerified");
                ApplyAotWeatherSettingsState(manager, useMutation: false);
                break;

            case AotManagedUiWeatherSettingsPostflightPhase:
                RequireAotWeatherSettingsState(
                    result,
                    evidence.Before,
                    expectMutation: false,
                    "WeatherSettingsPostflightVerified");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Weather settings persistence phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "WeatherSettingsPersistenceFlushed",
            "The Weather settings persistence phase did not flush successfully.");

        evidence.After = CaptureAotWeatherSettingsState(manager);
        RequireAotWeatherSettingsHostSuppressed(result, evidence.After);
        RequireAotWeatherSettingsState(
            result,
            evidence.After,
            expectMutation: phase == AotManagedUiWeatherSettingsMutatePhase,
            phase == AotManagedUiWeatherSettingsMutatePhase
                ? "WeatherSettingsMutationApplied"
                : "WeatherSettingsBaselineRestored");
    }

    private void ApplyAotWeatherSettingsState(
        WidgetManager manager,
        bool useMutation)
    {
        AppSettings settings = SettingsService.Settings;
        WeatherSettingsPolicy.SetAutoLocation(settings, enabled: false);
        bool locationApplied = WeatherSettingsPolicy.TrySetManualLocation(
            settings,
            useMutation ? AotWeatherMutatedCity : AotWeatherBaselineCity,
            useMutation
                ? AotWeatherMutatedLatitude
                : AotWeatherBaselineLatitude,
            useMutation
                ? AotWeatherMutatedLongitude
                : AotWeatherBaselineLongitude);
        if (!locationApplied)
        {
            throw new InvalidOperationException(
                "The fixed Weather location was rejected by the product policy.");
        }

        WeatherSettingsPolicy.SetTemperatureUnit(
            settings,
            useMutation
                ? SettingsService.WeatherTemperatureUnitFahrenheit
                : SettingsService.WeatherTemperatureUnitCelsius);
        WeatherSettingsPolicy.SetWindSpeedUnit(
            settings,
            useMutation
                ? SettingsService.WeatherWindSpeedUnitMph
                : SettingsService.WeatherWindSpeedUnitKmh);
        WeatherSettingsPolicy.SetDefaultView(
            settings,
            useMutation
                ? SettingsService.WeatherDefaultViewToday
                : SettingsService.WeatherDefaultViewWeek);
        WeatherSettingsPolicy.SetSkin(
            settings,
            useMutation
                ? SettingsService.WeatherSkinStandard
                : SettingsService.WeatherSkinRich);
        WeatherSettingsPolicy.SetRefreshInterval(
            settings,
            useMutation ? 15 : 60);

        bool ordinaryMetricValue = !useMutation;
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Forecast,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Sunrise,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.UvIndex,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Precipitation,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Humidity,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Wind,
            ordinaryMetricValue);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Pressure,
            useMutation);

        manager.ApplyAotWeatherSettingsViewMode(useWeekView: useMutation);
    }

    private AotManagedUiWeatherSettingsStateEvidence CaptureAotWeatherSettingsState(
        WidgetManager manager)
    {
        AppSettings settings = SettingsService.Settings;
        AotWeatherSettingsWidgetSnapshot widget =
            manager.CaptureAotWeatherSettingsWidgetSnapshot();
        return new AotManagedUiWeatherSettingsStateEvidence
        {
            AutoLocation = settings.WeatherAutoLocation,
            CityName = settings.WeatherCityName,
            Latitude = settings.WeatherLatitude,
            Longitude = settings.WeatherLongitude,
            TemperatureUnit = settings.WeatherTemperatureUnit,
            WindSpeedUnit = settings.WeatherWindSpeedUnit,
            DataSource = settings.WeatherDataSource,
            DefaultView = settings.WeatherDefaultView,
            Skin = settings.WeatherSkin,
            ShowForecast = settings.WeatherShowForecast,
            ShowSunrise = settings.WeatherShowSunrise,
            ShowUvIndex = settings.WeatherShowUvIndex,
            ShowPrecipitation = settings.WeatherShowPrecipitation,
            ShowHumidity = settings.WeatherShowHumidity,
            ShowWind = settings.WeatherShowWind,
            ShowPressure = settings.WeatherShowPressure,
            RefreshIntervalMinutes = settings.WeatherRefreshIntervalMinutes,
            Widget = new AotManagedUiWeatherSettingsWidgetEvidence
            {
                Id = widget.Id,
                WidgetKind = widget.WidgetKind,
                IsVisible = widget.IsVisible,
                IsDisabled = widget.IsDisabled,
                FeatureEnabled = widget.FeatureEnabled,
                HasViewModeOverride = widget.HasViewModeOverride,
                UseWeekView = widget.UseWeekView,
                MetadataValue = widget.MetadataValue,
                IsLoaded = widget.IsLoaded,
                WindowHandle = widget.WindowHandle,
                IsHostVisible = widget.IsHostVisible,
                HasXamlRoot = widget.HasXamlRoot
            }
        };
    }

    private static void RequireAotWeatherSettingsHostSuppressed(
        AotManagedUiSmokeResult result,
        AotManagedUiWeatherSettingsStateEvidence state)
    {
        RequireAotManagedUi(
            result,
            state.Widget.Id == AotManagedUiWeatherSettingsWidgetId &&
            state.Widget.WidgetKind == nameof(WidgetKind.Weather) &&
            state.Widget.IsVisible &&
            !state.Widget.IsDisabled &&
            !state.Widget.FeatureEnabled &&
            state.Widget.HasViewModeOverride &&
            !state.Widget.IsLoaded &&
            state.Widget.WindowHandle == 0 &&
            !state.Widget.IsHostVisible &&
            !state.Widget.HasXamlRoot,
            "WeatherSettingsHostSuppressed",
            "The local-only Weather settings matrix unexpectedly created a Weather host.");
    }

    private static void RequireAotWeatherSettingsState(
        AotManagedUiSmokeResult result,
        AotManagedUiWeatherSettingsStateEvidence state,
        bool expectMutation,
        string step)
    {
        bool valid = expectMutation
            ? IsAotWeatherSettingsMutation(state)
            : IsAotWeatherSettingsBaseline(state);
        RequireAotManagedUi(
            result,
            valid,
            step,
            expectMutation
                ? "The persisted Weather settings mutation is incomplete."
                : "The persisted Weather settings baseline is incomplete.");
    }

    private static bool IsAotWeatherSettingsBaseline(
        AotManagedUiWeatherSettingsStateEvidence state)
    {
        return IsAotWeatherSettingsCommon(state) &&
            state.CityName == AotWeatherBaselineCity &&
            Math.Abs(state.Latitude - AotWeatherBaselineLatitude) < 0.000001 &&
            Math.Abs(state.Longitude - AotWeatherBaselineLongitude) < 0.000001 &&
            state.TemperatureUnit == SettingsService.WeatherTemperatureUnitCelsius &&
            state.WindSpeedUnit == SettingsService.WeatherWindSpeedUnitKmh &&
            state.DefaultView == SettingsService.WeatherDefaultViewWeek &&
            state.Skin == SettingsService.WeatherSkinRich &&
            state.ShowForecast &&
            state.ShowSunrise &&
            state.ShowUvIndex &&
            state.ShowPrecipitation &&
            state.ShowHumidity &&
            state.ShowWind &&
            !state.ShowPressure &&
            state.RefreshIntervalMinutes == 60 &&
            !state.Widget.UseWeekView &&
            state.Widget.MetadataValue == WeatherWidgetViewModeSettings.DayValue;
    }

    private static bool IsAotWeatherSettingsMutation(
        AotManagedUiWeatherSettingsStateEvidence state)
    {
        return IsAotWeatherSettingsCommon(state) &&
            state.CityName == AotWeatherMutatedCity &&
            Math.Abs(state.Latitude - AotWeatherMutatedLatitude) < 0.000001 &&
            Math.Abs(state.Longitude - AotWeatherMutatedLongitude) < 0.000001 &&
            state.TemperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit &&
            state.WindSpeedUnit == SettingsService.WeatherWindSpeedUnitMph &&
            state.DefaultView == SettingsService.WeatherDefaultViewToday &&
            state.Skin == SettingsService.WeatherSkinStandard &&
            !state.ShowForecast &&
            !state.ShowSunrise &&
            !state.ShowUvIndex &&
            !state.ShowPrecipitation &&
            !state.ShowHumidity &&
            !state.ShowWind &&
            state.ShowPressure &&
            state.RefreshIntervalMinutes == 15 &&
            state.Widget.UseWeekView &&
            state.Widget.MetadataValue == WeatherWidgetViewModeSettings.WeekValue;
    }

    private static bool IsAotWeatherSettingsCommon(
        AotManagedUiWeatherSettingsStateEvidence state)
    {
        return !state.AutoLocation &&
            state.DataSource == SettingsService.WeatherDataSourceMsn &&
            state.Widget.Id == AotManagedUiWeatherSettingsWidgetId &&
            state.Widget.WidgetKind == nameof(WidgetKind.Weather) &&
            state.Widget.IsVisible &&
            !state.Widget.IsDisabled &&
            !state.Widget.FeatureEnabled &&
            state.Widget.HasViewModeOverride &&
            !state.Widget.IsLoaded &&
            state.Widget.WindowHandle == 0 &&
            !state.Widget.IsHostVisible &&
            !state.Widget.HasXamlRoot;
    }
}

internal sealed class AotManagedUiWeatherSettingsPersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public AotManagedUiWeatherSettingsStateEvidence Before { get; set; } = new();
    public AotManagedUiWeatherSettingsStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiWeatherSettingsStateEvidence
{
    public bool AutoLocation { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string TemperatureUnit { get; set; } = string.Empty;
    public string WindSpeedUnit { get; set; } = string.Empty;
    public string DataSource { get; set; } = string.Empty;
    public string DefaultView { get; set; } = string.Empty;
    public string Skin { get; set; } = string.Empty;
    public bool ShowForecast { get; set; }
    public bool ShowSunrise { get; set; }
    public bool ShowUvIndex { get; set; }
    public bool ShowPrecipitation { get; set; }
    public bool ShowHumidity { get; set; }
    public bool ShowWind { get; set; }
    public bool ShowPressure { get; set; }
    public int RefreshIntervalMinutes { get; set; }
    public AotManagedUiWeatherSettingsWidgetEvidence Widget { get; set; } = new();
}

internal sealed class AotManagedUiWeatherSettingsWidgetEvidence
{
    public string Id { get; set; } = string.Empty;
    public string WidgetKind { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsDisabled { get; set; }
    public bool FeatureEnabled { get; set; }
    public bool HasViewModeOverride { get; set; }
    public bool UseWeekView { get; set; }
    public string? MetadataValue { get; set; }
    public bool IsLoaded { get; set; }
    public long WindowHandle { get; set; }
    public bool IsHostVisible { get; set; }
    public bool HasXamlRoot { get; set; }
}
#endif
