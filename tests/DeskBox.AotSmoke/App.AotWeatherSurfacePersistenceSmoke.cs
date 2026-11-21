#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotWeatherBaselineTemperatureText = "20°C";
    private const string AotWeatherMutatedTemperatureText = "68°F";
    private const string AotWeatherBaselineWindText = "18 km/h";
    private const string AotWeatherMutatedWindText = "11.2 mph";

    private async Task CaptureAotManagedUiWeatherSurfacePersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotWeatherSurfaceHost host = await manager.GetAotWeatherSurfaceHostAsync();
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "WeatherSurfaceHostReady",
            "The real Weather widget HWND or XamlRoot is unavailable.");

        AotManagedUiWeatherSurfacePersistenceEvidence evidence =
            result.WeatherSurfacePersistence ??
            throw new InvalidOperationException(
                "Weather surface persistence evidence is unavailable.");
        evidence.WindowHandle = host.WindowHandle;
        evidence.HasXamlRoot = host.HasXamlRoot;
        evidence.Visible = host.Visible;

        bool beforeMutation = phase == AotManagedUiWeatherSurfaceVerifyRestorePhase;
        evidence.Before = await CaptureAotWeatherSurfaceStateAsync(
            host,
            expectMutation: beforeMutation);
        RequireAotWeatherSurfaceState(
            result,
            evidence.Before,
            beforeMutation,
            beforeMutation
                ? "WeatherSurfaceRestartMutationVerified"
                : "WeatherSurfaceBaselineVerified");

        switch (phase)
        {
            case AotManagedUiWeatherSurfaceMutatePhase:
                await ApplyAotWeatherSurfaceStateAsync(host, useMutation: true);
                evidence.After = await CaptureAotWeatherSurfaceStateAsync(
                    host,
                    expectMutation: true);
                RequireAotWeatherSurfaceState(
                    result,
                    evidence.After,
                    expectMutation: true,
                    "WeatherSurfaceMutationApplied");
                break;

            case AotManagedUiWeatherSurfaceVerifyRestorePhase:
                await ApplyAotWeatherSurfaceStateAsync(host, useMutation: false);
                evidence.After = await CaptureAotWeatherSurfaceStateAsync(
                    host,
                    expectMutation: false);
                RequireAotWeatherSurfaceState(
                    result,
                    evidence.After,
                    expectMutation: false,
                    "WeatherSurfaceBaselineRestored");
                break;

            case AotManagedUiWeatherSurfacePostflightPhase:
                evidence.After = await CaptureAotWeatherSurfaceStateAsync(
                    host,
                    expectMutation: false);
                RequireAotWeatherSurfaceState(
                    result,
                    evidence.After,
                    expectMutation: false,
                    "WeatherSurfacePostflightVerified");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Weather surface persistence phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "WeatherSurfacePersistenceFlushed",
            "The Weather surface persistence phase did not flush successfully.");
    }

    private async Task ApplyAotWeatherSurfaceStateAsync(
        AotWeatherSurfaceHost host,
        bool useMutation)
    {
        AppSettings settings = SettingsService.Settings;
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
        WeatherSettingsPolicy.SetSkin(
            settings,
            useMutation
                ? SettingsService.WeatherSkinStandard
                : SettingsService.WeatherSkinRich);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.UvIndex,
            enabled: !useMutation);
        WeatherSettingsPolicy.SetDisplayOption(
            settings,
            WeatherDisplayOption.Pressure,
            enabled: !useMutation);
        SettingsService.SaveDebounced();
        await host.Surface.SetAotWeatherSurfaceViewModeAsync(useMutation);
    }

    private async Task<AotManagedUiWeatherSurfaceStateEvidence>
        CaptureAotWeatherSurfaceStateAsync(
            AotWeatherSurfaceHost host,
            bool expectMutation)
    {
        AotWeatherSurfaceSnapshot surface =
            await host.Surface.WaitForAotWeatherSurfaceAsync(
                expectWeekView: expectMutation,
                expectedTemperatureText: expectMutation
                    ? AotWeatherMutatedTemperatureText
                    : AotWeatherBaselineTemperatureText,
                expectedWindValueText: expectMutation
                    ? AotWeatherMutatedWindText
                    : AotWeatherBaselineWindText,
                expectRichSkin: !expectMutation);
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotWeatherCompactSurfaceSnapshot compactSurface =
            await manager.CaptureAotWeatherCompactSurfaceAsync(
                host,
                expectWeekView: expectMutation,
                expectedTemperatureText: expectMutation
                    ? AotWeatherMutatedTemperatureText
                    : AotWeatherBaselineTemperatureText,
                expectedWindValueText: expectMutation
                    ? AotWeatherMutatedWindText
                    : AotWeatherBaselineWindText,
                expectRichSkin: !expectMutation);
        AppSettings settings = SettingsService.Settings;
        WidgetConfig widget = settings.Widgets.Single(candidate => string.Equals(
            candidate.Id,
            AotWeatherSurfaceFixture.OwnedWidgetId,
            StringComparison.Ordinal));
        bool hasViewModeOverride = WeatherWidgetViewModeSettings.TryGetWeekView(
            widget,
            out bool useWeekView);
        widget.Metadata.TryGetValue(
            WeatherWidgetViewModeSettings.MetadataKey,
            out string? metadataValue);

        return new AotManagedUiWeatherSurfaceStateEvidence
        {
            AutoLocation = settings.WeatherAutoLocation,
            CityName = settings.WeatherCityName,
            Latitude = settings.WeatherLatitude,
            Longitude = settings.WeatherLongitude,
            TemperatureUnit = settings.WeatherTemperatureUnit,
            WindSpeedUnit = settings.WeatherWindSpeedUnit,
            DataSource = settings.WeatherDataSource,
            Skin = settings.WeatherSkin,
            ShowForecast = settings.WeatherShowForecast,
            ShowSunrise = settings.WeatherShowSunrise,
            ShowUvIndex = settings.WeatherShowUvIndex,
            ShowPrecipitation = settings.WeatherShowPrecipitation,
            ShowHumidity = settings.WeatherShowHumidity,
            ShowWind = settings.WeatherShowWind,
            ShowPressure = settings.WeatherShowPressure,
            RefreshIntervalMinutes = settings.WeatherRefreshIntervalMinutes,
            Widget = new AotManagedUiWeatherSurfaceWidgetEvidence
            {
                Id = widget.Id,
                WidgetKind = widget.WidgetKind.ToString(),
                IsVisible = widget.IsVisible,
                IsDisabled = widget.IsDisabled,
                FeatureEnabled = FeatureWidgetSettings.IsEnabled(
                    settings,
                    WidgetKind.Weather),
                HasViewModeOverride = hasViewModeOverride,
                UseWeekView = useWeekView,
                MetadataValue = metadataValue
            },
            Surface = MapAotWeatherSurface(surface),
            CompactSurface = MapAotWeatherCompactSurface(compactSurface)
        };
    }

    private static AotManagedUiWeatherSurfaceEvidence MapAotWeatherSurface(
        AotWeatherSurfaceSnapshot snapshot)
    {
        return new AotManagedUiWeatherSurfaceEvidence
        {
            IsLoaded = snapshot.IsLoaded,
            HasXamlRoot = snapshot.HasXamlRoot,
            DataContextMatchesViewModel = snapshot.DataContextMatchesViewModel,
            ActualWidth = snapshot.ActualWidth,
            ActualHeight = snapshot.ActualHeight,
            HasData = snapshot.HasData,
            LayoutMode = snapshot.LayoutMode,
            IsWeekView = snapshot.IsWeekView,
            SelectedViewIndex = snapshot.SelectedViewIndex,
            RichBackdropVisible = snapshot.RichBackdropVisible,
            RichBackdropTopColor = snapshot.RichBackdropTopColor,
            RichBackdropBottomColor = snapshot.RichBackdropBottomColor,
            ExpandedLayoutVisible = snapshot.ExpandedLayoutVisible,
            HourlyForecastVisible = snapshot.HourlyForecastVisible,
            WeekForecastVisible = snapshot.WeekForecastVisible,
            LoadingOverlayHidden = snapshot.LoadingOverlayHidden,
            LocationDisplay = snapshot.LocationDisplay,
            SurfaceLocationText = snapshot.SurfaceLocationText,
            CurrentTemperatureText = snapshot.CurrentTemperatureText,
            SurfaceTemperatureText = snapshot.SurfaceTemperatureText,
            CurrentDescription = snapshot.CurrentDescription,
            SurfaceDescriptionText = snapshot.SurfaceDescriptionText,
            HumidityValueText = snapshot.HumidityValueText,
            SurfaceHumidityValueText = snapshot.SurfaceHumidityValueText,
            WindValueText = snapshot.WindValueText,
            SurfaceWindText = snapshot.SurfaceWindText,
            PrecipitationValueText = snapshot.PrecipitationValueText,
            SurfacePrecipitationValueText = snapshot.SurfacePrecipitationValueText,
            UvIndexValueText = snapshot.UvIndexValueText,
            SurfaceUvIndexValueText = snapshot.SurfaceUvIndexValueText,
            PressureValueText = snapshot.PressureValueText,
            SurfacePressureValueText = snapshot.SurfacePressureValueText,
            UvMetricVisible = snapshot.UvMetricVisible,
            PressureMetricVisible = snapshot.PressureMetricVisible,
            HourlyViewModelCount = snapshot.HourlyViewModelCount,
            DailyViewModelCount = snapshot.DailyViewModelCount,
            HourlyItemsCount = snapshot.HourlyItemsCount,
            DailyItemsCount = snapshot.DailyItemsCount,
            HourlyContainerRealized = snapshot.HourlyContainerRealized,
            DailyContainerRealized = snapshot.DailyContainerRealized,
            FirstHourlyHourLabel = snapshot.FirstHourlyHourLabel,
            FirstHourlyTemperatureText = snapshot.FirstHourlyTemperatureText,
            SurfaceFirstHourlyHourText = snapshot.SurfaceFirstHourlyHourText,
            SurfaceFirstHourlyTemperatureText =
                snapshot.SurfaceFirstHourlyTemperatureText,
            HourlyTemplateTextProjected = snapshot.HourlyTemplateTextProjected,
            FirstDailyDayLabel = snapshot.FirstDailyDayLabel,
            FirstDailyMaxText = snapshot.FirstDailyMaxText,
            FirstDailyMinText = snapshot.FirstDailyMinText,
            SurfaceFirstDailyDayText = snapshot.SurfaceFirstDailyDayText,
            SurfaceFirstDailyMaxText = snapshot.SurfaceFirstDailyMaxText,
            SurfaceFirstDailyMinText = snapshot.SurfaceFirstDailyMinText,
            DailyTemplateTextProjected = snapshot.DailyTemplateTextProjected
        };
    }

    private static AotManagedUiWeatherCompactSurfaceEvidence
        MapAotWeatherCompactSurface(AotWeatherCompactSurfaceSnapshot snapshot)
    {
        return new AotManagedUiWeatherCompactSurfaceEvidence
        {
            IsLoaded = snapshot.IsLoaded,
            HasXamlRoot = snapshot.HasXamlRoot,
            DataContextMatchesViewModel = snapshot.DataContextMatchesViewModel,
            ActualWidth = snapshot.ActualWidth,
            ActualHeight = snapshot.ActualHeight,
            HasData = snapshot.HasData,
            LayoutMode = snapshot.LayoutMode,
            MiniLayoutVisible = snapshot.MiniLayoutVisible,
            CompactLayoutVisible = snapshot.CompactLayoutVisible,
            ExpandedLayoutVisible = snapshot.ExpandedLayoutVisible,
            RichBackdropVisible = snapshot.RichBackdropVisible,
            LoadingOverlayHidden = snapshot.LoadingOverlayHidden,
            LocationDisplay = snapshot.LocationDisplay,
            SurfaceLocationText = snapshot.SurfaceLocationText,
            CurrentTemperatureText = snapshot.CurrentTemperatureText,
            SurfaceTemperatureText = snapshot.SurfaceTemperatureText,
            CurrentDescription = snapshot.CurrentDescription,
            SurfaceDescriptionText = snapshot.SurfaceDescriptionText,
            SurfaceHumidityValueText = snapshot.SurfaceHumidityValueText,
            SurfaceWindValueText = snapshot.SurfaceWindValueText,
            SurfacePrecipitationValueText = snapshot.SurfacePrecipitationValueText
        };
    }

    private static void RequireAotWeatherSurfaceState(
        AotManagedUiSmokeResult result,
        AotManagedUiWeatherSurfaceStateEvidence state,
        bool expectMutation,
        string step)
    {
        bool valid = IsAotWeatherSurfaceCommon(state) &&
            (expectMutation
                ? IsAotWeatherSurfaceMutation(state)
                : IsAotWeatherSurfaceBaseline(state));
        RequireAotManagedUi(
            result,
            valid,
            step,
            expectMutation
                ? "The real Weather surface did not project the persisted mutation."
                : "The real Weather surface did not project the deterministic baseline.");
    }

    private static bool IsAotWeatherSurfaceCommon(
        AotManagedUiWeatherSurfaceStateEvidence state)
    {
        AotManagedUiWeatherSurfaceEvidence surface = state.Surface;
        AotManagedUiWeatherCompactSurfaceEvidence compact = state.CompactSurface;
        return !state.AutoLocation &&
            state.CityName == AotWeatherSurfaceFixture.LocationName &&
            Math.Abs(state.Latitude - AotWeatherSurfaceFixture.Latitude) < 0.000001 &&
            Math.Abs(state.Longitude - AotWeatherSurfaceFixture.Longitude) < 0.000001 &&
            state.DataSource == SettingsService.WeatherDataSourceMsn &&
            state.ShowForecast &&
            state.ShowSunrise &&
            state.ShowPrecipitation &&
            state.ShowHumidity &&
            state.ShowWind &&
            state.RefreshIntervalMinutes == 60 &&
            state.Widget.Id == AotWeatherSurfaceFixture.OwnedWidgetId &&
            state.Widget.WidgetKind == nameof(WidgetKind.Weather) &&
            state.Widget.IsVisible &&
            !state.Widget.IsDisabled &&
            state.Widget.FeatureEnabled &&
            state.Widget.HasViewModeOverride &&
            surface.IsLoaded &&
            surface.HasXamlRoot &&
            surface.DataContextMatchesViewModel &&
            surface.ActualWidth > 0 &&
            surface.ActualHeight > 0 &&
            surface.HasData &&
            surface.LayoutMode == "Expanded" &&
            surface.ExpandedLayoutVisible &&
            surface.LoadingOverlayHidden &&
            surface.LocationDisplay == AotWeatherSurfaceFixture.LocationName &&
            surface.SurfaceLocationText == surface.LocationDisplay &&
            !string.IsNullOrWhiteSpace(surface.CurrentDescription) &&
            surface.SurfaceDescriptionText == surface.CurrentDescription &&
            surface.HumidityValueText == "64%" &&
            surface.SurfaceHumidityValueText == surface.HumidityValueText &&
            surface.PrecipitationValueText == "70%" &&
            surface.SurfacePrecipitationValueText == surface.PrecipitationValueText &&
            surface.UvIndexValueText == "5" &&
            surface.SurfaceUvIndexValueText == surface.UvIndexValueText &&
            surface.PressureValueText == "1012 hPa" &&
            surface.SurfacePressureValueText == surface.PressureValueText &&
            surface.HourlyViewModelCount == 24 &&
            surface.DailyViewModelCount == 7 &&
            !string.IsNullOrWhiteSpace(surface.RichBackdropTopColor) &&
            !string.IsNullOrWhiteSpace(surface.RichBackdropBottomColor) &&
            compact.IsLoaded &&
            compact.HasXamlRoot &&
            compact.DataContextMatchesViewModel &&
            compact.ActualWidth > 0 &&
            compact.ActualWidth < surface.ActualWidth &&
            compact.ActualHeight > 0 &&
            compact.HasData &&
            compact.LayoutMode == "Compact" &&
            !compact.MiniLayoutVisible &&
            compact.CompactLayoutVisible &&
            !compact.ExpandedLayoutVisible &&
            compact.LoadingOverlayHidden &&
            compact.LocationDisplay == AotWeatherSurfaceFixture.LocationName &&
            compact.SurfaceLocationText == compact.LocationDisplay &&
            compact.CurrentDescription == surface.CurrentDescription &&
            compact.SurfaceDescriptionText == compact.CurrentDescription &&
            compact.SurfaceHumidityValueText == "64%" &&
            compact.SurfacePrecipitationValueText == "70%";
    }

    private static bool IsAotWeatherSurfaceBaseline(
        AotManagedUiWeatherSurfaceStateEvidence state)
    {
        AotManagedUiWeatherSurfaceEvidence surface = state.Surface;
        AotManagedUiWeatherCompactSurfaceEvidence compact = state.CompactSurface;
        return state.TemperatureUnit == SettingsService.WeatherTemperatureUnitCelsius &&
            state.WindSpeedUnit == SettingsService.WeatherWindSpeedUnitKmh &&
            state.Skin == SettingsService.WeatherSkinRich &&
            state.ShowUvIndex &&
            state.ShowPressure &&
            !state.Widget.UseWeekView &&
            state.Widget.MetadataValue == WeatherWidgetViewModeSettings.DayValue &&
            !surface.IsWeekView &&
            surface.SelectedViewIndex == 0 &&
            surface.RichBackdropVisible &&
            surface.UvMetricVisible &&
            surface.PressureMetricVisible &&
            surface.HourlyForecastVisible &&
            !surface.WeekForecastVisible &&
            surface.CurrentTemperatureText == AotWeatherBaselineTemperatureText &&
            surface.SurfaceTemperatureText == AotWeatherBaselineTemperatureText &&
            surface.WindValueText == AotWeatherBaselineWindText &&
            surface.SurfaceWindText.StartsWith(
                AotWeatherBaselineWindText,
                StringComparison.Ordinal) &&
            surface.HourlyItemsCount == 24 &&
            surface.HourlyContainerRealized &&
            surface.HourlyTemplateTextProjected &&
            surface.FirstHourlyTemperatureText == AotWeatherBaselineTemperatureText &&
            surface.SurfaceFirstHourlyTemperatureText ==
                AotWeatherBaselineTemperatureText &&
            surface.SurfaceFirstHourlyHourText == surface.FirstHourlyHourLabel &&
            compact.RichBackdropVisible &&
            compact.CurrentTemperatureText == AotWeatherBaselineTemperatureText &&
            compact.SurfaceTemperatureText == AotWeatherBaselineTemperatureText &&
            compact.SurfaceWindValueText == AotWeatherBaselineWindText;
    }

    private static bool IsAotWeatherSurfaceMutation(
        AotManagedUiWeatherSurfaceStateEvidence state)
    {
        AotManagedUiWeatherSurfaceEvidence surface = state.Surface;
        AotManagedUiWeatherCompactSurfaceEvidence compact = state.CompactSurface;
        return state.TemperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit &&
            state.WindSpeedUnit == SettingsService.WeatherWindSpeedUnitMph &&
            state.Skin == SettingsService.WeatherSkinStandard &&
            !state.ShowUvIndex &&
            !state.ShowPressure &&
            state.Widget.UseWeekView &&
            state.Widget.MetadataValue == WeatherWidgetViewModeSettings.WeekValue &&
            surface.IsWeekView &&
            surface.SelectedViewIndex == 1 &&
            !surface.RichBackdropVisible &&
            !surface.UvMetricVisible &&
            !surface.PressureMetricVisible &&
            !surface.HourlyForecastVisible &&
            surface.WeekForecastVisible &&
            surface.CurrentTemperatureText == AotWeatherMutatedTemperatureText &&
            surface.SurfaceTemperatureText == AotWeatherMutatedTemperatureText &&
            surface.WindValueText == AotWeatherMutatedWindText &&
            surface.SurfaceWindText.StartsWith(
                AotWeatherMutatedWindText,
                StringComparison.Ordinal) &&
            surface.DailyItemsCount == 7 &&
            surface.DailyContainerRealized &&
            surface.DailyTemplateTextProjected &&
            surface.FirstDailyMaxText == "75°F" &&
            surface.SurfaceFirstDailyMaxText == "75°F" &&
            surface.FirstDailyMinText == "61°F" &&
            surface.SurfaceFirstDailyMinText == "61°F" &&
            surface.SurfaceFirstDailyDayText == surface.FirstDailyDayLabel &&
            !compact.RichBackdropVisible &&
            compact.CurrentTemperatureText == AotWeatherMutatedTemperatureText &&
            compact.SurfaceTemperatureText == AotWeatherMutatedTemperatureText &&
            compact.SurfaceWindValueText == AotWeatherMutatedWindText;
    }
}

internal sealed class AotManagedUiWeatherSurfacePersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool Visible { get; set; }
    public AotManagedUiWeatherSurfaceStateEvidence Before { get; set; } = new();
    public AotManagedUiWeatherSurfaceStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiWeatherSurfaceStateEvidence
{
    public bool AutoLocation { get; set; }
    public string CityName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string TemperatureUnit { get; set; } = string.Empty;
    public string WindSpeedUnit { get; set; } = string.Empty;
    public string DataSource { get; set; } = string.Empty;
    public string Skin { get; set; } = string.Empty;
    public bool ShowForecast { get; set; }
    public bool ShowSunrise { get; set; }
    public bool ShowUvIndex { get; set; }
    public bool ShowPrecipitation { get; set; }
    public bool ShowHumidity { get; set; }
    public bool ShowWind { get; set; }
    public bool ShowPressure { get; set; }
    public int RefreshIntervalMinutes { get; set; }
    public AotManagedUiWeatherSurfaceWidgetEvidence Widget { get; set; } = new();
    public AotManagedUiWeatherSurfaceEvidence Surface { get; set; } = new();
    public AotManagedUiWeatherCompactSurfaceEvidence CompactSurface { get; set; } = new();
}

internal sealed class AotManagedUiWeatherSurfaceWidgetEvidence
{
    public string Id { get; set; } = string.Empty;
    public string WidgetKind { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsDisabled { get; set; }
    public bool FeatureEnabled { get; set; }
    public bool HasViewModeOverride { get; set; }
    public bool UseWeekView { get; set; }
    public string? MetadataValue { get; set; }
}

internal sealed class AotManagedUiWeatherSurfaceEvidence
{
    public bool IsLoaded { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool DataContextMatchesViewModel { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public bool HasData { get; set; }
    public string LayoutMode { get; set; } = string.Empty;
    public bool IsWeekView { get; set; }
    public int SelectedViewIndex { get; set; }
    public bool RichBackdropVisible { get; set; }
    public string RichBackdropTopColor { get; set; } = string.Empty;
    public string RichBackdropBottomColor { get; set; } = string.Empty;
    public bool ExpandedLayoutVisible { get; set; }
    public bool HourlyForecastVisible { get; set; }
    public bool WeekForecastVisible { get; set; }
    public bool LoadingOverlayHidden { get; set; }
    public string LocationDisplay { get; set; } = string.Empty;
    public string SurfaceLocationText { get; set; } = string.Empty;
    public string CurrentTemperatureText { get; set; } = string.Empty;
    public string SurfaceTemperatureText { get; set; } = string.Empty;
    public string CurrentDescription { get; set; } = string.Empty;
    public string SurfaceDescriptionText { get; set; } = string.Empty;
    public string HumidityValueText { get; set; } = string.Empty;
    public string SurfaceHumidityValueText { get; set; } = string.Empty;
    public string WindValueText { get; set; } = string.Empty;
    public string SurfaceWindText { get; set; } = string.Empty;
    public string PrecipitationValueText { get; set; } = string.Empty;
    public string SurfacePrecipitationValueText { get; set; } = string.Empty;
    public string UvIndexValueText { get; set; } = string.Empty;
    public string SurfaceUvIndexValueText { get; set; } = string.Empty;
    public string PressureValueText { get; set; } = string.Empty;
    public string SurfacePressureValueText { get; set; } = string.Empty;
    public bool UvMetricVisible { get; set; }
    public bool PressureMetricVisible { get; set; }
    public int HourlyViewModelCount { get; set; }
    public int DailyViewModelCount { get; set; }
    public int HourlyItemsCount { get; set; }
    public int DailyItemsCount { get; set; }
    public bool HourlyContainerRealized { get; set; }
    public bool DailyContainerRealized { get; set; }
    public string FirstHourlyHourLabel { get; set; } = string.Empty;
    public string FirstHourlyTemperatureText { get; set; } = string.Empty;
    public string SurfaceFirstHourlyHourText { get; set; } = string.Empty;
    public string SurfaceFirstHourlyTemperatureText { get; set; } = string.Empty;
    public bool HourlyTemplateTextProjected { get; set; }
    public string FirstDailyDayLabel { get; set; } = string.Empty;
    public string FirstDailyMaxText { get; set; } = string.Empty;
    public string FirstDailyMinText { get; set; } = string.Empty;
    public string SurfaceFirstDailyDayText { get; set; } = string.Empty;
    public string SurfaceFirstDailyMaxText { get; set; } = string.Empty;
    public string SurfaceFirstDailyMinText { get; set; } = string.Empty;
    public bool DailyTemplateTextProjected { get; set; }
}

internal sealed class AotManagedUiWeatherCompactSurfaceEvidence
{
    public bool IsLoaded { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool DataContextMatchesViewModel { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public bool HasData { get; set; }
    public string LayoutMode { get; set; } = string.Empty;
    public bool MiniLayoutVisible { get; set; }
    public bool CompactLayoutVisible { get; set; }
    public bool ExpandedLayoutVisible { get; set; }
    public bool RichBackdropVisible { get; set; }
    public bool LoadingOverlayHidden { get; set; }
    public string LocationDisplay { get; set; } = string.Empty;
    public string SurfaceLocationText { get; set; } = string.Empty;
    public string CurrentTemperatureText { get; set; } = string.Empty;
    public string SurfaceTemperatureText { get; set; } = string.Empty;
    public string CurrentDescription { get; set; } = string.Empty;
    public string SurfaceDescriptionText { get; set; } = string.Empty;
    public string SurfaceHumidityValueText { get; set; } = string.Empty;
    public string SurfaceWindValueText { get; set; } = string.Empty;
    public string SurfacePrecipitationValueText { get; set; } = string.Empty;
}
#endif
