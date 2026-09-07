namespace DeskBox.Tests;

public sealed class AotStage5B4B2C2AContractTests
{
    [Fact]
    public void WeatherSettingsScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("WeatherSettingsPersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_WEATHER_SETTINGS_PHASE",
            shared,
            StringComparison.Ordinal);
        Assert.Contains("Mutate", shared, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", shared, StringComparison.Ordinal);
        Assert.Contains("Postflight", shared, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", shared, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiWeatherSettingsPersistenceEvidence", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalMutation_ReusesLocalWeatherSettingsProductPolicy()
    {
        string policy = ReadRepositoryFile("src/DeskBox/Services/WeatherSettingsPolicy.cs");
        string settings = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.WeatherOptions.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs");

        foreach (string member in new[]
        {
            "TrySetManualLocation",
            "SetTemperatureUnit",
            "SetWindSpeedUnit",
            "SetDefaultView",
            "SetSkin",
            "SetRefreshInterval",
            "SetDisplayOption"
        })
        {
            Assert.Contains(member, policy, StringComparison.Ordinal);
            Assert.Contains($"WeatherSettingsPolicy.{member}", settings, StringComparison.Ordinal);
            Assert.Contains($"WeatherSettingsPolicy.{member}", scenario, StringComparison.Ordinal);
        }

        Assert.Contains("double.IsFinite", policy, StringComparison.Ordinal);
        Assert.Contains("latitude is < -90 or > 90", policy, StringComparison.Ordinal);
        Assert.Contains("longitude is < -180 or > 180", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("WeatherDataSource", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void PerWidgetViewMode_ReusesExistingMetadataProductPath()
    {
        string viewMode = ReadRepositoryFile(
            "src/DeskBox/Services/WeatherWidgetViewModeSettings.cs");
        string weatherViewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WeatherWidgetViewModel.RefreshAndLayout.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotWeatherSettingsPersistenceSmoke.cs");

        Assert.Contains("Weather.ViewMode", viewMode, StringComparison.Ordinal);
        Assert.Contains("DayValue", viewMode, StringComparison.Ordinal);
        Assert.Contains("WeekValue", viewMode, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.SetWeekView", weatherViewModel, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.SetWeekView", manager, StringComparison.Ordinal);
        Assert.Contains("_settingsService.UpdateWidget", manager, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.TryGetWeekView", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedWeatherConfiguration_IsPersistedButNeverCreatesAHost()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotWeatherSettingsPersistenceSmoke.cs");
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("aot-5b4b2c2a-weather", shared, StringComparison.Ordinal);
        Assert.Contains("? WidgetKind.Weather", shared, StringComparison.Ordinal);
        Assert.Contains("manager.LoadedSurfaceCount == 1", shared, StringComparison.Ordinal);
        Assert.Contains("host.WidgetKind != WidgetKind.Weather", shared, StringComparison.Ordinal);
        Assert.Contains("WeatherSettingsHostSuppressed", scenario, StringComparison.Ordinal);
        Assert.Contains("!state.Widget.FeatureEnabled", scenario, StringComparison.Ordinal);
        Assert.Contains("!state.Widget.IsLoaded", scenario, StringComparison.Ordinal);
        Assert.Contains("GetLoadedDesktopWindows", manager, StringComparison.Ordinal);
        Assert.Contains("Weather = $false", script, StringComparison.Ordinal);
        Assert.Contains("\"Weather.ViewMode\" = \"Day\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_CoversEveryLocalGlobalSettingAndOpposingWidgetOverride()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs");

        foreach (string property in new[]
        {
            "AutoLocation", "CityName", "Latitude", "Longitude",
            "TemperatureUnit", "WindSpeedUnit", "DataSource", "DefaultView",
            "Skin", "ShowForecast", "ShowSunrise", "ShowUvIndex",
            "ShowPrecipitation", "ShowHumidity", "ShowWind", "ShowPressure",
            "RefreshIntervalMinutes", "HasViewModeOverride", "UseWeekView",
            "MetadataValue", "IsLoaded", "WindowHandle", "HasXamlRoot"
        })
        {
            Assert.Contains(property, scenario, StringComparison.Ordinal);
        }

        Assert.Contains("WeatherDefaultViewWeek", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.DayValue", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherDefaultViewToday", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherWidgetViewModeSettings.WeekValue", scenario, StringComparison.Ordinal);
        Assert.Contains("WeatherDataSourceMsn", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_UsesThreeFreshProcessesEqualityCleanupAndNoWeatherInitializationGate()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Invoke-WeatherSettingsPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("weatherSettingsNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", script, StringComparison.Ordinal);
        Assert.Contains("phaseExecutableHashes", script, StringComparison.Ordinal);
        Assert.Contains("Assert-WeatherSettingsStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("Assert-WeatherSettingsEvidenceState", script, StringComparison.Ordinal);
        Assert.Contains("runtimeWeatherInitializationLines", script, StringComparison.Ordinal);
        Assert.Contains("[WeatherService]", script, StringComparison.Ordinal);
        Assert.Contains("[WeatherWidgetViewModel]", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("weatherSettingsPreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
        Assert.Contains("final-settings.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_DoesNotEnterSurfaceNetworkLocationDataSourcePickerOrRustPaths()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotWeatherSettingsPersistenceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotWeatherSettingsPersistenceSmoke.cs");
        string policy = ReadRepositoryFile("src/DeskBox/Services/WeatherSettingsPolicy.cs");
        string combined = scenario + manager + policy;

        foreach (string forbidden in new[]
        {
            "WeatherService", "WeatherWidgetViewModel", "WeatherWidgetContent",
            "WeatherCurrent", "WeatherDaily", "WeatherHourly", "HttpClient",
            "WindowsLocationHelper", "CitySearchService",
            "InitializeAsync", "RefreshAsync", "FileOpenPicker", "FolderPicker",
            "NativeBackend", "LibraryImport", "JsonSerializer.Deserialize",
            "File.WriteAllText", "CreateWidget", "RemoveWidget"
        })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Scenario_ReusesSingleSourceGeneratedResultWriter()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiWeatherSettingsPersistenceEvidence? WeatherSettingsPersistence",
            shared,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void RustAbi_RemainsAtVersionTwoWithCurrentCapabilitiesAndExports()
    {
        string native = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string header = ReadRepositoryFile("native/include/deskbox_native.h");

        Assert.Contains("DESKBOX_NATIVE_ABI_VERSION: u32 = 2", native, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_QUICK_ACCESS_V1: u64 = 1 << 7", native, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(native, "pub const DESKBOX_NATIVE_CAPABILITY_"));
        Assert.Equal(10, CountOccurrences(native, "#[unsafe(no_mangle)]"));
        Assert.DoesNotContain("weather", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stage5B4B2C2A_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 59", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 59", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Weather local settings/view-mode/reload/restore/postflight", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesWeatherRunnerPolicyManagerOfflineScopeAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2C2ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2ARequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2ARequiredPolicyPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2ARequiredManagerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2ARequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2AForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2AJsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2ASourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C2AExpectedWmc1510Count", audit, StringComparison.Ordinal);
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
