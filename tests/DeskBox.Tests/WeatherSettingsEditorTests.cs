using System.Text.RegularExpressions;
using DeskBox.Contracts;
using DeskBox.Features.Weather;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

/// <summary>
/// Eighth copy batch of the settings-shell binding-facade retirement: the
/// Weather section (23 XAML binding paths plus the display flyout family)
/// is re-bound to a new section editor through a section-level DataContext
/// switch. These tests pin the editor's behavior (read snapshot projection
/// with the old shell normalization, write-through, no write-back on
/// external sync, the auto-location user-edit event, the shell-pushed city
/// suggestion/no-results/location-status projections, the manual city
/// selection chain, the display flyout and its summary, the fresh-install
/// reset, localization refresh) and the migration pattern itself (XAML
/// paths, converter gates, AOT bridges, window wiring, facade removal).
/// The city-search state machine itself (search service, debounce) stays
/// on the shell and is covered by its own seams.
/// </summary>
public sealed class WeatherSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    private static readonly Func<string, object[], string> PassthroughFormat =
        static (key, args) => key + ":" + string.Join("|", args);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, FeatureWidgetsSettingsCoordinator Coordinator, WeatherSettingsViewModel Editor, List<Exception> Errors)
        CreateEditor(string root, Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        var errors = new List<Exception>();
        return (settings, coordinator, new WeatherSettingsViewModel(
            coordinator, PassthroughLocalize, PassthroughFormat, errors.Add), errors);
    }

    [Fact]
    public void Constructor_ProjectsPersistedStateFromTheReadSnapshot()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(
            _root,
            settings =>
            {
                WeatherSettingsSlice slice = settings.Settings.Weather;
                slice.WeatherAutoLocation = false;
                slice.WeatherCityName = "Hanoi";
                slice.WeatherTemperatureUnit = WeatherOptionKinds.TemperatureUnitFahrenheit;
                slice.WeatherWindSpeedUnit = WeatherOptionKinds.WindSpeedUnitMph;
                slice.WeatherDefaultView = WeatherOptionKinds.DefaultViewWeek;
                slice.WeatherSkin = WeatherOptionKinds.SkinStandard;
                slice.WeatherDataSource = WeatherOptionKinds.DataSourceOpenMeteo;
                slice.WeatherShowForecast = false;
                slice.WeatherShowSunrise = false;
                slice.WeatherShowUvIndex = false;
                slice.WeatherShowPrecipitation = false;
                slice.WeatherShowHumidity = false;
                slice.WeatherShowWind = false;
                slice.WeatherShowPressure = true;
                slice.WeatherRefreshIntervalMinutes = 180;
            });

        Assert.Equal(WeatherOptionKinds.LocationModeManual, editor.SelectedLocationMode);
        Assert.Equal("Hanoi", editor.CitySearchText);
        Assert.Equal(WeatherOptionKinds.TemperatureUnitFahrenheit, editor.SelectedTemperatureUnit);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitMph, editor.SelectedWindSpeedUnit);
        Assert.Equal(WeatherOptionKinds.DefaultViewWeek, editor.SelectedDefaultView);
        Assert.Equal(WeatherOptionKinds.SkinStandard, editor.SelectedSkin);
        Assert.Equal(WeatherOptionKinds.DataSourceOpenMeteo, editor.SelectedDataSource);
        Assert.Equal(180, editor.SelectedRefreshInterval);
        Assert.False(editor.ShowForecast);
        Assert.True(editor.ShowPressure);
        Assert.True(editor.ShowCitySearch);
    }

    [Fact]
    public void Constructor_NormalizesUnknownValuesLikeTheOldShell()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(
            _root,
            settings =>
            {
                WeatherSettingsSlice slice = settings.Settings.Weather;
                slice.WeatherTemperatureUnit = "bogus";
                slice.WeatherWindSpeedUnit = "bogus";
                slice.WeatherDefaultView = "bogus";
                slice.WeatherSkin = "bogus";
                slice.WeatherDataSource = "bogus";
                slice.WeatherRefreshIntervalMinutes = 5;
            });

        Assert.Equal(WeatherOptionKinds.TemperatureUnitCelsius, editor.SelectedTemperatureUnit);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitKmh, editor.SelectedWindSpeedUnit);
        Assert.Equal(WeatherOptionKinds.DefaultViewToday, editor.SelectedDefaultView);
        Assert.Equal(WeatherOptionKinds.SkinStandard, editor.SelectedSkin);
        Assert.Equal(WeatherOptionKinds.DataSourceMsn, editor.SelectedDataSource);
        Assert.Equal(
            WeatherOptionKinds.RefreshMinMinutes,
            editor.SelectedRefreshInterval);
    }

    [Fact]
    public void UserEdits_PersistThroughTheCoordinatorAndReProject()
    {
        (SettingsService settings, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);

        editor.SelectedTemperatureUnit = WeatherOptionKinds.TemperatureUnitFahrenheit;
        editor.SelectedWindSpeedUnit = WeatherOptionKinds.WindSpeedUnitMs;
        editor.SelectedDefaultView = WeatherOptionKinds.DefaultViewWeek;
        editor.SelectedSkin = WeatherOptionKinds.SkinStandard;
        editor.SelectedDataSource = WeatherOptionKinds.DataSourceOpenMeteo;
        editor.SelectedRefreshInterval = 30;
        // Unknown picks normalize to the canonical values before writing.
        editor.SelectedTemperatureUnit = "bogus";
        editor.SelectedRefreshInterval = 9999;

        WeatherSettingsSlice slice = settings.Settings.Weather;
        Assert.Equal(WeatherOptionKinds.TemperatureUnitCelsius, slice.WeatherTemperatureUnit);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitMs, slice.WeatherWindSpeedUnit);
        Assert.Equal(WeatherOptionKinds.DefaultViewWeek, slice.WeatherDefaultView);
        Assert.Equal(WeatherOptionKinds.SkinStandard, slice.WeatherSkin);
        Assert.Equal(WeatherOptionKinds.DataSourceOpenMeteo, slice.WeatherDataSource);
        Assert.Equal(
            WeatherOptionKinds.RefreshMaxMinutes,
            slice.WeatherRefreshIntervalMinutes);
        Assert.Equal(WeatherOptionKinds.TemperatureUnitCelsius, editor.SelectedTemperatureUnit);
        Assert.Equal(
            WeatherOptionKinds.RefreshMaxMinutes,
            editor.SelectedRefreshInterval);
    }

    [Fact]
    public void ExternalRefresh_ReProjectsWithoutRaisingUserEvents()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);
        bool userEvent = false;
        editor.AutoLocationUserChanged += _ => userEvent = true;

        // Simulate an external settings change (the shell's snapshot sync
        // path): the editor re-projects, and the re-projection must not
        // look like a user edit.
        editor.Refresh();

        Assert.False(userEvent);
        Assert.Equal(WeatherOptionKinds.LocationModeAuto, editor.SelectedLocationMode);
    }

    [Fact]
    public void LocationMode_UserEditWritesAutoLocationAndRaisesTheHostEvent()
    {
        (SettingsService settings, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);
        var events = new List<bool>();
        editor.AutoLocationUserChanged += events.Add;

        editor.SelectedLocationMode = WeatherOptionKinds.LocationModeManual;
        Assert.False(settings.Settings.Weather.WeatherAutoLocation);
        editor.SelectedLocationMode = WeatherOptionKinds.LocationModeAuto;
        Assert.True(settings.Settings.Weather.WeatherAutoLocation);

        Assert.Equal(new[] { false, true }, events);
    }

    [Fact]
    public void SelectCity_PersistsManualLocationAndSwitchesAutoMode()
    {
        (SettingsService settings, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);
        var events = new List<bool>();
        editor.AutoLocationUserChanged += events.Add;
        editor.SetCitySuggestions(
        [
            new WeatherCitySearchResult
            {
                Name = "Hanoi",
                DisplayName = "Hanoi, Vietnam",
                Latitude = 21.03,
                Longitude = 105.85,
                Country = "Vietnam"
            }
        ], hasNoResults: false);
        Assert.True(editor.HasCitySuggestions);

        editor.TrySelectFirstCitySuggestion();

        WeatherSettingsSlice slice = settings.Settings.Weather;
        Assert.False(slice.WeatherAutoLocation);
        Assert.Equal("Hanoi, Vietnam", slice.WeatherCityName);
        Assert.Equal(21.03, slice.WeatherLatitude);
        Assert.Equal(105.85, slice.WeatherLongitude);
        // The selection flips the mode to manual (user event), re-projects
        // the search text onto the persisted display name and drops the
        // suggestion projection.
        Assert.Equal(WeatherOptionKinds.LocationModeManual, editor.SelectedLocationMode);
        Assert.Equal("Hanoi, Vietnam", editor.CitySearchText);
        Assert.False(editor.HasCitySuggestions);
        Assert.Empty(editor.CitySuggestionItems);
        Assert.Equal(new[] { false }, events);
    }

    [Fact]
    public void SelectCity_RejectsInvalidCoordinatesWithoutWriting()
    {
        (SettingsService settings, _, WeatherSettingsViewModel editor, List<Exception> errors) =
            CreateEditor(_root);
        var city = new WeatherCitySearchResult
        {
            Name = "Bogus",
            DisplayName = "Bogus",
            Latitude = 120,
            Longitude = 13
        };

        editor.SelectCity(city);
        editor.SelectCity(null);

        Assert.Equal(2, errors.Count);
        Assert.True(settings.Settings.Weather.WeatherAutoLocation);
        Assert.Equal(string.Empty, settings.Settings.Weather.WeatherCityName);
    }

    [Fact]
    public void CitySuggestions_ShellPushPortsUpdateTheProjection()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);
        Assert.False(editor.ShowNoCityResults);

        editor.SetCitySuggestions([], hasNoResults: true);
        Assert.True(editor.ShowNoCityResults);
        Assert.False(editor.HasCitySuggestions);

        editor.SetCitySuggestions(
        [
            new WeatherCitySearchResult { Name = "Hanoi", DisplayName = "Hanoi" },
            new WeatherCitySearchResult { Name = "Beijing", DisplayName = "Beijing" }
        ], hasNoResults: false);
        Assert.False(editor.ShowNoCityResults);
        Assert.Equal(2, editor.CitySuggestionItems.Length);

        editor.ClearCitySuggestions();
        Assert.Empty(editor.CitySuggestionItems);
        Assert.False(editor.ShowNoCityResults);
    }

    [Fact]
    public void RestoreCitySearchText_UsesThePersistedCityName()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(
            _root,
            settings => settings.Settings.Weather.WeatherCityName = "Hanoi");

        editor.CitySearchText = "typing...";
        editor.RestoreCitySearchText();

        Assert.Equal("Hanoi", editor.CitySearchText);
    }

    [Fact]
    public void LocationStatus_ShellPushDrivesTheVisibilityGate()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);

        // Auto mode + empty status → hidden until the shell pushes text.
        Assert.False(editor.ShowLocationStatus);
        editor.SetLocationStatus("Settings.Weather.AutoLocation.Locating");
        Assert.True(editor.ShowLocationStatus);
        Assert.Equal("Settings.Weather.AutoLocation.Locating", editor.LocationStatusText);

        // A user flip to manual hides the line; empty text hides it too.
        editor.SelectedLocationMode = WeatherOptionKinds.LocationModeManual;
        Assert.False(editor.ShowLocationStatus);
        editor.SelectedLocationMode = WeatherOptionKinds.LocationModeAuto;
        Assert.True(editor.ShowLocationStatus);
        editor.SetLocationStatus(string.Empty);
        Assert.False(editor.ShowLocationStatus);
    }

    [Fact]
    public void DisplayOptionToggles_PersistThroughTheEditorProperties()
    {
        // The multi-select flyout was replaced by native expander child
        // cards bound to the Show* editor properties; the persistence the
        // old flyout test covered is now driven through those properties.
        (SettingsService settings, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);

        Assert.False(editor.ShowPressure);
        editor.ShowPressure = true;
        Assert.True(settings.Settings.Weather.WeatherShowPressure);

        Assert.True(editor.ShowForecast);
        editor.ShowForecast = false;
        Assert.False(settings.Settings.Weather.WeatherShowForecast);
    }

    [Fact]
    public void ResetPreferences_WritesFreshInstallDefaultsAndReProjects()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(
            _root,
            settings =>
            {
                WeatherSettingsSlice slice = settings.Settings.Weather;
                slice.WeatherAutoLocation = false;
                slice.WeatherCityName = "Hanoi";
                slice.WeatherTemperatureUnit = WeatherOptionKinds.TemperatureUnitFahrenheit;
                slice.WeatherRefreshIntervalMinutes = 180;
                slice.WeatherShowPressure = true;
            });

        editor.ResetPreferences(scheduleSave: false);

        Assert.Equal(WeatherOptionKinds.LocationModeAuto, editor.SelectedLocationMode);
        Assert.Equal(WeatherOptionKinds.TemperatureUnitCelsius, editor.SelectedTemperatureUnit);
        Assert.Equal(
            WeatherOptionKinds.DefaultRefreshIntervalMinutes,
            editor.SelectedRefreshInterval);
        Assert.False(editor.ShowPressure);
    }

    [Fact]
    public void RefreshLocalization_RebuildsOptionTablesAndTexts()
    {
        (_, _, WeatherSettingsViewModel editor, _) = CreateEditor(_root);

        Assert.Equal(
            "Weather.CitySearch.Placeholder",
            editor.CitySearchPlaceholder);
        Assert.Equal("Weather.CitySearch.NoResults", editor.NoCityResultsText);
        IReadOnlyList<SettingsOption> options = editor.AvailableLocationModeOptions;
        Assert.Equal("Settings.Weather.LocationMode.Auto", options[0].DisplayName);

        editor.RefreshLocalization();

        Assert.Equal(2, editor.AvailableLocationModeOptions.Count);
        Assert.Equal(2, editor.AvailableTemperatureUnitOptions.Count);
        Assert.Equal(3, editor.AvailableWindSpeedUnitOptions.Count);
        Assert.Equal(2, editor.AvailableDefaultViewOptions.Count);
        Assert.Equal(2, editor.AvailableSkinOptions.Count);
        Assert.Equal(4, editor.AvailableIconStyleOptions.Count);
        Assert.Equal(2, editor.AvailableDataSourceOptions.Count);
        Assert.Equal(4, editor.AvailableRefreshIntervalOptions.Count);
    }

    [Fact]
    public void WeatherOptionKinds_SettingsServiceConstantsStayCanonicalAliases()
    {
        Assert.Equal(WeatherOptionKinds.TemperatureUnitCelsius, SettingsService.WeatherTemperatureUnitCelsius);
        Assert.Equal(WeatherOptionKinds.TemperatureUnitFahrenheit, SettingsService.WeatherTemperatureUnitFahrenheit);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitKmh, SettingsService.WeatherWindSpeedUnitKmh);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitMs, SettingsService.WeatherWindSpeedUnitMs);
        Assert.Equal(WeatherOptionKinds.WindSpeedUnitMph, SettingsService.WeatherWindSpeedUnitMph);
        Assert.Equal(WeatherOptionKinds.DefaultViewToday, SettingsService.WeatherDefaultViewToday);
        Assert.Equal(WeatherOptionKinds.DefaultViewWeek, SettingsService.WeatherDefaultViewWeek);
        Assert.Equal(WeatherOptionKinds.SkinStandard, SettingsService.WeatherSkinStandard);
        Assert.Equal(WeatherOptionKinds.SkinRich, SettingsService.WeatherSkinRich);
        Assert.Equal(WeatherOptionKinds.IconStyleFlat, SettingsService.WeatherIconStyleFlat);
        Assert.Equal(WeatherOptionKinds.IconStyleFluent, SettingsService.WeatherIconStyleFluent);
        Assert.Equal(WeatherOptionKinds.DataSourceMsn, SettingsService.WeatherDataSourceMsn);
        Assert.Equal(WeatherOptionKinds.DataSourceOpenMeteo, SettingsService.WeatherDataSourceOpenMeteo);
        Assert.Equal(WeatherOptionKinds.RefreshMinMinutes, SettingsService.WeatherRefreshMinMinutes);
        Assert.Equal(WeatherOptionKinds.RefreshMaxMinutes, SettingsService.WeatherRefreshMaxMinutes);
    }

    [Fact]
    public void ShellFacade_WeatherFamilyIsGoneFromTheSettingsViewModel()
    {
        string[] removed =
        [
            "WeatherAutoLocation",
            "WeatherCityName",
            "WeatherShowForecast",
            "WeatherShowSunrise",
            "WeatherShowUvIndex",
            "WeatherShowPrecipitation",
            "WeatherShowHumidity",
            "WeatherShowWind",
            "WeatherShowPressure",
            "SelectedWeatherLocationMode",
            "SelectedWeatherTemperatureUnit",
            "SelectedWeatherWindSpeedUnit",
            "SelectedWeatherDefaultView",
            "SelectedWeatherSkin",
            "SelectedWeatherDataSource",
            "SelectedWeatherRefreshInterval",
            "AvailableWeatherDisplayOptions",
            "AvailableWeatherLocationModeOptions",
            "AvailableWeatherTemperatureUnitOptions",
            "AvailableWeatherTemperatureUnits",
            "AvailableWeatherTemperatureUnitDisplayNames",
            "AvailableWeatherWindSpeedUnitOptions",
            "AvailableWeatherWindSpeedUnits",
            "AvailableWeatherWindSpeedUnitDisplayNames",
            "AvailableWeatherDefaultViewOptions",
            "AvailableWeatherDefaultViews",
            "AvailableWeatherDefaultViewDisplayNames",
            "AvailableWeatherSkinOptions",
            "AvailableWeatherSkins",
            "AvailableWeatherSkinDisplayNames",
            "AvailableWeatherDataSourceOptions",
            "AvailableWeatherDataSources",
            "AvailableWeatherDataSourceDisplayNames",
            "AvailableWeatherRefreshIntervalOptions",
            "AvailableWeatherRefreshIntervals",
            "AvailableWeatherRefreshIntervalDisplayNames",
            "WeatherLocationStatusText",
            "WeatherLocationStatusIsError",
            "WeatherLocationStatusVisibility",
            "WeatherCityNameVisibility",
            "WeatherCitySearchText",
            "WeatherCitySearchPlaceholder",
            "WeatherCityNoResultsText",
            "WeatherCitySuggestions",
            "WeatherCitySuggestionItems",
            "HasNoCitySearchResults",
            "HasNoCitySearchResultsVisibility",
            "IsWeatherCitySearching",
            "SelectWeatherCity",
            "ClearWeatherCitySuggestions",
            "RestoreWeatherCitySearchText",
            "RefreshWeatherLocationStatusAsync",
            "RefreshWeatherCityPopularCities",
            "PopulateNearbyPopularCitiesAsync"
        ];
        HashSet<string> publicMembers = new(
            typeof(SettingsViewModel)
                .GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Select(member => member.Name),
            StringComparer.Ordinal);
        foreach (string name in removed)
        {
            Assert.DoesNotContain(name, publicMembers);
        }
    }

    [Fact]
    public void MigrationPattern_XamlBridgesAndWindowWiringStayPinned()
    {
        string windowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string navigation = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.Navigation.cs"));
        string hotkeyAndAppearance = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs"));
        string bridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/Weather/WeatherSettingsViewModel.AotBindableProperties.cs"));
        string bindableShell = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));

        // Section-level DataContext switch; the template stays {Binding}-only
        // (no x:Bind, so its x:DataType keeps pointing at the shell type).
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedLocationMode, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableLocationModeOptions}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding LocationStatusText}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowLocationStatus, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowCitySearch, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding CitySearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding CitySuggestionItems}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowNoCityResults, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedTemperatureUnit, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWindSpeedUnit, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedDefaultView, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedSkin, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedIconStyle, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedDataSource, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedRefreshInterval, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding ShowForecast, Mode=TwoWay}\"",
            windowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Weather", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Binding SelectedWeather", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Binding AvailableWeather", windowXaml, StringComparison.Ordinal);

        Assert.Contains(
            "section.DataContext = _weatherSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "_weatherSettingsViewModel.SelectCity(result);",
            hotkeyAndAppearance,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ = ViewModel.UpdateWeatherCitySuggestionsAsync(sender.Text);",
            hotkeyAndAppearance,
            StringComparison.Ordinal);

        // The editor's {Binding} bridge and the shrunken shell bridge.
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(SelectedLocationMode)", bridge, StringComparison.Ordinal);
        Assert.Contains("nameof(CitySuggestionItems)", bridge, StringComparison.Ordinal);
        Assert.Equal(31, Regex.Matches(bridge, @"nameof\(").Count);
        Assert.Equal(33, Regex.Matches(bindableShell, @"nameof\(").Count);
    }
}
