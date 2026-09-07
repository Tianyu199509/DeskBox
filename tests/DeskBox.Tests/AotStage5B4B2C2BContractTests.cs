namespace DeskBox.Tests;

public sealed class AotStage5B4B2C2BContractTests
{
    [Fact]
    public void WeatherSurfaceScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSurfacePersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSurfacePersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE",
            shared,
            StringComparison.Ordinal);
        Assert.Contains("Mutate", shared, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", shared, StringComparison.Ordinal);
        Assert.Contains("Postflight", shared, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", shared, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiWeatherSurfacePersistenceEvidence", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_IsExactScenarioWidgetPhaseAndCoordinatesOnly()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotWeatherSurfaceFixture.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", fixture, StringComparison.Ordinal);
        Assert.Contains("WeatherSurfacePersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2c2b-weather", fixture, StringComparison.Ordinal);
        Assert.Contains("Shanghai AOT Surface", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_SMOKE", fixture, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_WEATHER_SURFACE_PHASE",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains("phase is not \"Mutate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("Math.Abs(latitude - Latitude)", fixture, StringComparison.Ordinal);
        Assert.Contains("new WeatherService(CreateData)", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsLocationHelper", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("CitySearchService", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherServiceInjection_IsCompileTimeAotAndProviderScoped()
    {
        string service = ReadRepositoryFile("src/DeskBox/Services/WeatherService.cs");
        string provider = ReadRepositoryFile(
            "src/DeskBox/Services/WeatherWidgetContentProvider.cs");
        string adapter = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContentAdapter.cs");

        Assert.Contains(
            "private readonly Func<double, double, string, WeatherData>? _aotWeatherDataFactory",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal WeatherService(Func<double, double, string, WeatherData> weatherDataFactory)",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "WeatherData fixture = _aotWeatherDataFactory",
            service,
            StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", provider, StringComparison.Ordinal);
        Assert.Contains(
            "weatherService = AotWeatherSurfaceFixture.TryCreateService(config)",
            provider,
            StringComparison.Ordinal);
        Assert.Contains("WeatherService? weatherService = null", adapter, StringComparison.Ordinal);
        Assert.Contains("weatherService ?? new WeatherService()", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_ProvidesNonEmptyCurrentDailyAndTwentyFourHourlyValues()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotWeatherSurfaceFixture.cs");

        Assert.Contains("new List<string>(24)", fixture, StringComparison.Ordinal);
        Assert.Contains("for (int hour = 0; hour < 24; hour++)", fixture, StringComparison.Ordinal);
        Assert.Contains("WeatherCode = 61", fixture, StringComparison.Ordinal);
        Assert.Contains("Temperature = 20", fixture, StringComparison.Ordinal);
        Assert.Contains("Humidity = 64", fixture, StringComparison.Ordinal);
        Assert.Contains("WindSpeed = 18", fixture, StringComparison.Ordinal);
        Assert.Contains("Pressure = 1012", fixture, StringComparison.Ordinal);
        Assert.Contains("TemperatureMax = [24, 23, 26, 25, 8, 18, 20]", fixture, StringComparison.Ordinal);
        Assert.Contains("TemperatureMin = [16, 15, 17, 18, -1, 10, 12]", fixture, StringComparison.Ordinal);
        Assert.Contains("PrecipitationProbabilityMax = [70, 20, 0, 10, 80, 90, 15]", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherBindings_UseThreeNarrowGeneratedProviders()
    {
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WeatherViewModels.AotBindableProperties.cs");

        Assert.Equal(3, CountOccurrences(
            bindable,
            "[WinRT.GeneratedBindableCustomProperty("));
        Assert.Contains("public sealed partial class WeatherWidgetViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class WeatherDayViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class WeatherHourViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(HourlyForecastItemsSource)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(DailyForecastItemsSource)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(TemperatureText)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(TempMaxText)", bindable, StringComparison.Ordinal);
    }

    [Fact]
    public void ForecastCollections_KeepTypedModelsButProjectObjectArraysAtXamlBoundary()
    {
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WeatherWidgetViewModel.cs");
        string processing = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WeatherWidgetViewModel.DataProcessing.cs");

        Assert.Contains(
            "ObservableCollection<WeatherDayViewModel> DailyForecast",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ObservableCollection<WeatherHourViewModel> HourlyForecast",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "DailyForecastItemsSource => DailyForecast.Cast<object>().ToArray()",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "HourlyForecastItemsSource => HourlyForecast.Cast<object>().ToArray()",
            viewModel,
            StringComparison.Ordinal);
        Assert.True(CountOccurrences(
            processing,
            "OnPropertyChanged(nameof(DailyForecastItemsSource))") >= 2);
        Assert.True(CountOccurrences(
            processing,
            "OnPropertyChanged(nameof(HourlyForecastItemsSource))") >= 2);
    }

    [Fact]
    public void RealXamlSurface_BindsObjectArraysAndNamesActualEvidenceControls()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml");

        Assert.Contains(
            "ItemsSource=\"{Binding HourlyForecastItemsSource}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding DailyForecastItemsSource}\"",
            xaml,
            StringComparison.Ordinal);
        foreach (string name in new[]
        {
            "ExpandedLocationText", "ExpandedTemperatureText",
            "CompactLocationText", "CompactTemperatureText",
            "CompactHumidityValueText", "CompactWindText",
            "CompactPrecipitationValueText",
            "ExpandedHourlyForecastSection", "ExpandedHourlyItems",
            "HourlyHourText", "HourlyTemperatureText",
            "ExpandedWeekForecastSection", "ExpandedDailyItems",
            "DailyDayText", "DailyMaxText", "DailyMinText",
            "ExpandedUvMetric", "ExpandedPressureMetric"
        })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SurfaceProbe_RequiresHwndXamlRootDataTemplatesAndRealSegmentedPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.AotSurfaceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotWeatherSurfaceSmoke.cs");

        Assert.Contains("WaitForAotWeatherSurfaceAsync", surface, StringComparison.Ordinal);
        Assert.Contains("WaitForAotWeatherCompactSurfaceAsync", surface, StringComparison.Ordinal);
        Assert.Contains("AotWeatherCompactSurfaceSnapshot", surface, StringComparison.Ordinal);
        Assert.Contains("DataContextMatchesViewModel", surface, StringComparison.Ordinal);
        Assert.Contains("ContainerFromIndex(0)", surface, StringComparison.Ordinal);
        Assert.Contains("HourlyTemplateTextProjected", surface, StringComparison.Ordinal);
        Assert.Contains("DailyTemplateTextProjected", surface, StringComparison.Ordinal);
        Assert.Contains("WeatherViewSegmented.SelectedIndex", surface, StringComparison.Ordinal);
        Assert.Contains("GetAotWeatherSurfaceHostAsync", manager, StringComparison.Ordinal);
        Assert.Contains("ContentReadyTask", manager, StringComparison.Ordinal);
        Assert.Contains("window.WindowHandle", manager, StringComparison.Ordinal);
        Assert.Contains("window.WindowContentRoot?.XamlRoot", manager, StringComparison.Ordinal);
        Assert.Contains("CaptureAotWeatherCompactSurfaceAsync", manager, StringComparison.Ordinal);
        Assert.Contains("CaptureAotPersistenceSmokeBounds", manager, StringComparison.Ordinal);
        Assert.Contains("ApplyAotPersistenceSmokeBounds", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_VerifiesUnitsSkinViewModeAndActualSurfaceAcrossRestart()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSurfacePersistenceSmoke.cs");

        Assert.Contains("WeatherTemperatureUnitCelsius", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherTemperatureUnitFahrenheit", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWindSpeedUnitKmh", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWindSpeedUnitMph", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSkinRich", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSkinStandard", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherDisplayOption.UvIndex", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherDisplayOption.Pressure", scenario, StringComparison.Ordinal);
        Assert.Contains("CompactSurface", scenario, StringComparison.Ordinal);
        Assert.Contains("UvMetricVisible", scenario, StringComparison.Ordinal);
        Assert.Contains("PressureMetricVisible", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.DayValue", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.WeekValue", scenario, StringComparison.Ordinal);
        Assert.Contains("20°C", scenario, StringComparison.Ordinal);
        Assert.Contains("68°F", scenario, StringComparison.Ordinal);
        Assert.Contains("75°F", scenario, StringComparison.Ordinal);
        Assert.Contains("61°F", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSurfaceRestartMutationVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSurfaceBaselineRestored", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherSurfacePostflightVerified", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_UsesThreeProcessesFixtureOfflineAndProductionIsolationGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Invoke-WeatherSurfacePersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("Assert-WeatherSurfaceStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("Assert-WeatherSurfaceEvidenceState", script, StringComparison.Ordinal);
        Assert.Contains("$State.compactSurface", script, StringComparison.Ordinal);
        Assert.Contains("$surface.uvMetricVisible", script, StringComparison.Ordinal);
        Assert.Contains("$surface.pressureMetricVisible", script, StringComparison.Ordinal);
        Assert.Contains("weatherSurfaceNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", script, StringComparison.Ordinal);
        Assert.Contains("phaseExecutableHashes", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFixtureLogLines", script, StringComparison.Ordinal);
        Assert.Contains(
            "[AotWeatherSurfaceFixture] Served deterministic WeatherData request",
            script,
            StringComparison.Ordinal);
        Assert.Contains("runtimeNetworkLogLines", script, StringComparison.Ordinal);
        Assert.Contains("[WeatherService]", script, StringComparison.Ordinal);
        Assert.Contains("[WindowsLocation]", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("weatherSurfacePreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
        Assert.Contains("final-settings.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_ReusesSingleSourceGeneratedResultWriter()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiWeatherSurfacePersistenceEvidence? WeatherSurfacePersistence",
            shared,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void RustAbi_RemainsUnchangedAndDoesNotAcquireWeatherSurfaceWork()
    {
        string native = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string header = ReadRepositoryFile("native/include/deskbox_native.h");

        Assert.Contains("DESKBOX_NATIVE_ABI_VERSION: u32 = 2", native, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(native, "pub const DESKBOX_NATIVE_CAPABILITY_"));
        Assert.Equal(10, CountOccurrences(native, "#[unsafe(no_mangle)]"));
        Assert.DoesNotContain("weather", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stage5B4B2C2B_ProfileSchemaProjectAndAuditAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 61", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2B", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 61", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("deterministic non-empty WeatherData", project, StringComparison.Ordinal);
        Assert.Contains("real Weather network/location", project, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
