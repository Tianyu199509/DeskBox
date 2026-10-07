using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Weather;

/// <summary>
/// Weather-section settings editor. Owns the section's XAML binding surface
/// (the location-mode combo, the pushed location-status line, the manual
/// city AutoSuggestBox projection, the data-source/temperature-unit/wind-
/// speed-unit/default-view/skin/refresh combos and the seven-item display
/// flyout with its summary): reads project through the normalized snapshot
/// of <see cref="IFeatureWidgetsSettings.ReadWeatherPresentation"/>, user
/// edits write through the same coordinator ports the legacy shell used
/// (the batch-38 weather policy path), and external refresh paths (settings
/// broadcasts, feature-card default restores, language changes) call
/// <see cref="Refresh"/>/<see cref="RefreshLocalization"/> to re-sync the
/// projection without writing back. The city-search state machine itself
/// stays on the shell (it drives <c>CitySearchService</c> and the Windows
/// location helper, which the editor must not reference): the shell pushes
/// the suggestion list, the no-results flag and the location status text
/// through <see cref="SetCitySuggestions"/>/<see cref="SetLocationStatus"/>,
/// and the editor answers user edits with the
/// <see cref="AutoLocationUserChanged"/> event so the shell can re-run the
/// location lookup. The bindable property names intentionally drop the
/// legacy <c>Weather</c>/<c>Selected</c> prefixes: the section-level
/// DataContext switch means they no longer need to be unique across the
/// whole shell, and unprefixed names keep the flat <c>AppSettings</c>
/// facade-name ratchet shrinking. This class references neither App nor
/// WinUI nor the settings adapter; localization and error reporting arrive
/// as delegates.
/// </summary>
public sealed partial class WeatherSettingsViewModel : ObservableObject
{
    private static readonly string[] TemperatureUnitValues =
    [
        WeatherOptionKinds.TemperatureUnitCelsius,
        WeatherOptionKinds.TemperatureUnitFahrenheit
    ];

    private static readonly string[] WindSpeedUnitValues =
    [
        WeatherOptionKinds.WindSpeedUnitKmh,
        WeatherOptionKinds.WindSpeedUnitMs,
        WeatherOptionKinds.WindSpeedUnitMph
    ];

    private static readonly string[] DefaultViewValues =
    [
        WeatherOptionKinds.DefaultViewToday,
        WeatherOptionKinds.DefaultViewWeek
    ];

    private static readonly string[] SkinValues =
    [
        WeatherOptionKinds.SkinStandard,
        WeatherOptionKinds.SkinRich
    ];

    private static readonly string[] IconStyleValues = WeatherOptionKinds.IconStyleValues;

    private static readonly string[] DataSourceValues =
    [
        WeatherOptionKinds.DataSourceMsn,
        WeatherOptionKinds.DataSourceOpenMeteo
    ];

    private readonly IFeatureWidgetsSettings _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _format;
    private readonly Action<Exception> _reportError;
    private readonly List<WeatherCitySearchResult> _citySuggestions = [];
    private bool _isSyncingPresentation;
    private string _selectedLocationMode = WeatherOptionKinds.LocationModeAuto;
    private string _locationStatusText = string.Empty;
    private string _citySearchText = string.Empty;
    private bool _hasNoCityResults;
    private string _selectedTemperatureUnit = WeatherOptionKinds.TemperatureUnitCelsius;
    private string _selectedWindSpeedUnit = WeatherOptionKinds.WindSpeedUnitKmh;
    private string _selectedDefaultView = WeatherOptionKinds.DefaultViewToday;
    private string _selectedSkin = WeatherOptionKinds.SkinRich;
    private string _selectedIconStyle = WeatherOptionKinds.DefaultIconStyle;
    private string _selectedDataSource = WeatherOptionKinds.DataSourceMsn;
    private int _selectedRefreshInterval = WeatherOptionKinds.DefaultRefreshIntervalMinutes;
    private bool _showForecast = true;
    private bool _showSunrise = true;
    private bool _showUvIndex = true;
    private bool _showPrecipitation = true;
    private bool _showHumidity = true;
    private bool _showWind = true;
    private bool _showPressure;
    private string[]? _cachedLocationModeNames;
    private string[]? _cachedTemperatureUnitNames;
    private string[]? _cachedWindSpeedUnitNames;
    private string[]? _cachedDefaultViewNames;
    private string[]? _cachedSkinNames;
    private string[]? _cachedIconStyleNames;
    private string[]? _cachedDataSourceNames;
    private string[]? _cachedRefreshIntervalNames;

    public WeatherSettingsViewModel(
        IFeatureWidgetsSettings settings,
        Func<string, string> localize,
        Func<string, object[], string> format,
        Action<Exception> reportError)
    {
        _settings = settings;
        _localize = localize;
        _format = format;
        _reportError = reportError;
        Refresh();
    }

    /// <summary>
    /// Raised after a user edit flipped the persisted auto-location mode;
    /// the shell answers a <c>true</c> value by re-running the Windows
    /// location lookup and pushing the status line back in.
    /// </summary>
    public event Action<bool>? AutoLocationUserChanged;

    public string SelectedLocationMode
    {
        get => _selectedLocationMode;
        set
        {
            string normalized = value == WeatherOptionKinds.LocationModeManual
                ? WeatherOptionKinds.LocationModeManual
                : WeatherOptionKinds.LocationModeAuto;
            if (!SetProperty(ref _selectedLocationMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowLocationStatus));
            if (_isSyncingPresentation)
            {
                return;
            }

            bool enabled = normalized == WeatherOptionKinds.LocationModeAuto;
            // Fire only after the write actually persisted: the coordinator
            // port returns false for the unchanged-write skip, so the shell's
            // location lookup only runs for real persisted flips (audit P3).
            bool persisted = false;
            RunWrite(() => persisted = _settings.SetWeatherAutoLocation(enabled));
            if (persisted)
            {
                AutoLocationUserChanged?.Invoke(enabled);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableLocationModeOptions
    {
        get
        {
            _cachedLocationModeNames ??=
            [
                _localize("Settings.Weather.LocationMode.Auto"),
                _localize("Settings.Weather.LocationMode.Manual")
            ];
            return WrapOptions(BuildOptions(
                [WeatherOptionKinds.LocationModeAuto, WeatherOptionKinds.LocationModeManual],
                _cachedLocationModeNames));
        }
    }

    // P2-2: the search box is always visible — the user can manually
    // override the city even in auto mode.
    public bool ShowCitySearch => true;

    public string LocationStatusText
    {
        get => _locationStatusText;
        private set
        {
            if (SetProperty(ref _locationStatusText, value))
            {
                OnPropertyChanged(nameof(ShowLocationStatus));
            }
        }
    }

    public bool ShowLocationStatus =>
        _selectedLocationMode == WeatherOptionKinds.LocationModeAuto &&
        !string.IsNullOrEmpty(_locationStatusText);

    /// <summary>
    /// Pushes the auto-location status line (the Windows location lookup
    /// runs on the shell); empty text hides the line.
    /// </summary>
    public void SetLocationStatus(string text) => LocationStatusText = text ?? string.Empty;

    public string SelectedTemperatureUnit
    {
        get => _selectedTemperatureUnit;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeTemperatureUnit(value);
            if (!SetProperty(ref _selectedTemperatureUnit, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherTemperatureUnit(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableTemperatureUnitOptions
    {
        get
        {
            _cachedTemperatureUnitNames ??= TemperatureUnitValues
                .Select(GetTemperatureUnitDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(TemperatureUnitValues, _cachedTemperatureUnitNames));
        }
    }

    public string SelectedWindSpeedUnit
    {
        get => _selectedWindSpeedUnit;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeWindSpeedUnit(value);
            if (!SetProperty(ref _selectedWindSpeedUnit, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherWindSpeedUnit(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWindSpeedUnitOptions
    {
        get
        {
            _cachedWindSpeedUnitNames ??= WindSpeedUnitValues
                .Select(GetWindSpeedUnitDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(WindSpeedUnitValues, _cachedWindSpeedUnitNames));
        }
    }

    public string SelectedDefaultView
    {
        get => _selectedDefaultView;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeDefaultView(value);
            if (!SetProperty(ref _selectedDefaultView, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherDefaultView(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDefaultViewOptions
    {
        get
        {
            _cachedDefaultViewNames ??= DefaultViewValues
                .Select(GetDefaultViewDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(DefaultViewValues, _cachedDefaultViewNames));
        }
    }

    public string SelectedSkin
    {
        get => _selectedSkin;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeSkin(value);
            if (!SetProperty(ref _selectedSkin, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherSkin(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableSkinOptions
    {
        get
        {
            _cachedSkinNames ??= SkinValues.Select(GetSkinDisplayName).ToArray();
            return WrapOptions(BuildOptions(SkinValues, _cachedSkinNames));
        }
    }

    public string SelectedIconStyle
    {
        get => _selectedIconStyle;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeIconStyle(value);
            if (!SetProperty(ref _selectedIconStyle, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherIconStyle(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableIconStyleOptions
    {
        get
        {
            _cachedIconStyleNames ??= IconStyleValues.Select(GetIconStyleDisplayName).ToArray();
            return WrapOptions(BuildOptions(IconStyleValues, _cachedIconStyleNames));
        }
    }

    public string SelectedDataSource
    {
        get => _selectedDataSource;
        set
        {
            string normalized = WeatherOptionKinds.NormalizeDataSource(value);
            if (!SetProperty(ref _selectedDataSource, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherDataSource(normalized));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDataSourceOptions
    {
        get
        {
            _cachedDataSourceNames ??= DataSourceValues
                .Select(GetDataSourceDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(DataSourceValues, _cachedDataSourceNames));
        }
    }

    public int SelectedRefreshInterval
    {
        get => _selectedRefreshInterval;
        set
        {
            int clamped = WeatherOptionKinds.NormalizeRefreshInterval(value);
            if (!SetProperty(ref _selectedRefreshInterval, clamped))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetWeatherRefreshInterval(clamped));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableRefreshIntervalOptions
    {
        get
        {
            _cachedRefreshIntervalNames ??= WeatherOptionKinds.RefreshIntervalSteps
                .Select(GetRefreshIntervalDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(
                WeatherOptionKinds.RefreshIntervalSteps, _cachedRefreshIntervalNames));
        }
    }

    // ─── Manual city search (shell-pushed suggestion projection) ───

    public string CitySearchText
    {
        get => _citySearchText;
        set => SetProperty(ref _citySearchText, value);
    }

    /// <summary>
    /// The AutoSuggestBox dropdown's ItemsSource: an object[] projection
    /// because the hidden read-only-array type behind a collection
    /// expression cannot marshal across the WinRT ABI in Native AOT builds.
    /// </summary>
    public object[] CitySuggestionItems => _citySuggestions.Cast<object>().ToArray();

    public bool HasCitySuggestions => _citySuggestions.Count > 0;

    public string CitySearchPlaceholder => _localize("Weather.CitySearch.Placeholder");

    public string NoCityResultsText => _localize("Weather.CitySearch.NoResults");

    public bool ShowNoCityResults
    {
        get => _hasNoCityResults;
        private set => SetProperty(ref _hasNoCityResults, value);
    }

    /// <summary>
    /// Pushes the suggestion list (nearby popular cities or live search
    /// results) computed by the shell's city-search state machine.
    /// </summary>
    public void SetCitySuggestions(
        IReadOnlyList<WeatherCitySearchResult> suggestions,
        bool hasNoResults)
    {
        _citySuggestions.Clear();
        if (suggestions.Count > 0)
        {
            _citySuggestions.AddRange(suggestions);
        }

        OnPropertyChanged(nameof(CitySuggestionItems));
        OnPropertyChanged(nameof(HasCitySuggestions));
        ShowNoCityResults = hasNoResults;
    }

    /// <summary>
    /// Persists a manually chosen city: validates the coordinates, flips
    /// the location mode to manual (a user edit, so a pending auto lookup
    /// is superseded), writes through the coordinator's policy port and
    /// re-projects so the search box shows the persisted display name.
    /// </summary>
    public void SelectCity(WeatherCitySearchResult? result)
    {
        if (result is null ||
            !double.IsFinite(result.Latitude) ||
            !double.IsFinite(result.Longitude) ||
            result.Latitude is < -90 or > 90 ||
            result.Longitude is < -180 or > 180)
        {
            _reportError(new InvalidOperationException(
                "Ignored city selection with invalid coordinates"));
            return;
        }

        if (_selectedLocationMode == WeatherOptionKinds.LocationModeAuto)
        {
            // P2-2: a manual pick while auto-location is on switches to
            // manual mode so the selection sticks (a user edit through the
            // regular setter, event included).
            SelectedLocationMode = WeatherOptionKinds.LocationModeManual;
        }

        bool accepted = false;
        RunWrite(() =>
            accepted = _settings.TrySetWeatherManualLocation(
                result.DisplayName,
                result.Latitude,
                result.Longitude));
        if (!accepted)
        {
            _reportError(new InvalidOperationException(
                "Ignored city selection rejected by weather policy"));
        }

        _citySuggestions.Clear();
        OnPropertyChanged(nameof(CitySuggestionItems));
        OnPropertyChanged(nameof(HasCitySuggestions));
        ShowNoCityResults = false;
    }

    /// <summary>
    /// Picks the first suggestion when the query is submitted with Enter
    /// without an explicit selection; returns whether a suggestion existed.
    /// </summary>
    public bool TrySelectFirstCitySuggestion()
    {
        if (_citySuggestions.Count == 0)
        {
            return false;
        }

        SelectCity(_citySuggestions[0]);
        return true;
    }

    /// <summary>
    /// Drops the suggestion projection (lost focus, language change) so the
    /// dropdown repopulates fresh on the next interaction.
    /// </summary>
    public void ClearCitySuggestions() => SetCitySuggestions([], hasNoResults: false);

    /// <summary>
    /// Restores the search text to the persisted city name after the box
    /// loses focus without a selection.
    /// </summary>
    public void RestoreCitySearchText()
    {
        _citySearchText = _settings.ReadWeatherPresentation().CityName;
        OnPropertyChanged(nameof(CitySearchText));
    }

    // ─── Display options ───
    // The seven display switches render as settings sub-cards bound straight
    // to the properties below; the retired flyout menu builders that used to
    // project them (summary text, name lookup, selection probe and toggle
    // dispatcher) were removed with the flyout.

    public bool ShowForecast
    {
        get => _showForecast;
        set => SetDisplayOption(ref _showForecast, value, "Forecast");
    }

    public bool ShowSunrise
    {
        get => _showSunrise;
        set => SetDisplayOption(ref _showSunrise, value, "Sunrise");
    }

    public bool ShowUvIndex
    {
        get => _showUvIndex;
        set => SetDisplayOption(ref _showUvIndex, value, "UvIndex");
    }

    public bool ShowPrecipitation
    {
        get => _showPrecipitation;
        set => SetDisplayOption(ref _showPrecipitation, value, "Precipitation");
    }

    public bool ShowHumidity
    {
        get => _showHumidity;
        set => SetDisplayOption(ref _showHumidity, value, "Humidity");
    }

    public bool ShowWind
    {
        get => _showWind;
        set => SetDisplayOption(ref _showWind, value, "Wind");
    }

    public bool ShowPressure
    {
        get => _showPressure;
        set => SetDisplayOption(ref _showPressure, value, "Pressure");
    }

    private void SetDisplayOption(ref bool field, bool value, string optionKey)
    {
        if (!SetProperty(ref field, value))
        {
            return;
        }

        if (_isSyncingPresentation)
        {
            return;
        }

        RunWrite(() => _settings.SetWeatherDisplayOption(optionKey, value));
    }

    /// <summary>
    /// The feature-card reset's write path: writes the fresh-install
    /// weather defaults (the coordinator's reset port) and re-projects.
    /// </summary>
    public void ResetPreferences(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetWeatherPreferences(scheduleSave));
    }

    /// <summary>
    /// Re-projects the persisted weather state onto the binding surface
    /// without writing back. Called on construction, settings broadcasts
    /// and default restores; the coordinator snapshot normalizes with the
    /// old shell-constructor semantics.
    /// </summary>
    public void Refresh()
    {
        WeatherPresentationSettings snapshot = _settings.ReadWeatherPresentation();
        _isSyncingPresentation = true;
        try
        {
            SelectedLocationMode = snapshot.AutoLocation
                ? WeatherOptionKinds.LocationModeAuto
                : WeatherOptionKinds.LocationModeManual;
            SelectedTemperatureUnit = snapshot.TemperatureUnit;
            SelectedWindSpeedUnit = snapshot.WindSpeedUnit;
            SelectedDefaultView = snapshot.DefaultView;
            SelectedSkin = snapshot.Skin;
            SelectedIconStyle = snapshot.IconStyle;
            SelectedDataSource = snapshot.DataSource;
            SelectedRefreshInterval = snapshot.RefreshIntervalMinutes;
            ShowForecast = snapshot.ShowForecast;
            ShowSunrise = snapshot.ShowSunrise;
            ShowUvIndex = snapshot.ShowUvIndex;
            ShowPrecipitation = snapshot.ShowPrecipitation;
            ShowHumidity = snapshot.ShowHumidity;
            ShowWind = snapshot.ShowWind;
            ShowPressure = snapshot.ShowPressure;
            _citySearchText = snapshot.CityName;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(CitySearchText));
        OnPropertyChanged(nameof(ShowLocationStatus));
    }

    /// <summary>
    /// Drops the localized option-name caches after a language change so
    /// the option tables, the placeholder and the no-results text
    /// re-project in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedLocationModeNames = null;
        _cachedTemperatureUnitNames = null;
        _cachedWindSpeedUnitNames = null;
        _cachedDefaultViewNames = null;
        _cachedSkinNames = null;
        _cachedIconStyleNames = null;
        _cachedDataSourceNames = null;
        _cachedRefreshIntervalNames = null;
        OnPropertyChanged(nameof(AvailableLocationModeOptions));
        OnPropertyChanged(nameof(AvailableTemperatureUnitOptions));
        OnPropertyChanged(nameof(AvailableWindSpeedUnitOptions));
        OnPropertyChanged(nameof(AvailableDefaultViewOptions));
        OnPropertyChanged(nameof(AvailableSkinOptions));
        OnPropertyChanged(nameof(AvailableIconStyleOptions));
        OnPropertyChanged(nameof(AvailableDataSourceOptions));
        OnPropertyChanged(nameof(AvailableRefreshIntervalOptions));
        // Re-notify the current selections: replacing the localized option
        // arrays makes WinUI reset every bound ComboBox.SelectedIndex to -1.
        OnPropertyChanged(nameof(SelectedLocationMode));
        OnPropertyChanged(nameof(SelectedTemperatureUnit));
        OnPropertyChanged(nameof(SelectedWindSpeedUnit));
        OnPropertyChanged(nameof(SelectedDefaultView));
        OnPropertyChanged(nameof(SelectedSkin));
        OnPropertyChanged(nameof(SelectedIconStyle));
        OnPropertyChanged(nameof(SelectedDataSource));
        OnPropertyChanged(nameof(SelectedRefreshInterval));
        OnPropertyChanged(nameof(CitySearchPlaceholder));
        OnPropertyChanged(nameof(NoCityResultsText));
    }

    private string GetTemperatureUnitDisplayName(string unit) => unit switch
    {
        WeatherOptionKinds.TemperatureUnitFahrenheit =>
            _localize("Weather.Unit.Fahrenheit"),
        _ => _localize("Weather.Unit.Celsius")
    };

    private string GetWindSpeedUnitDisplayName(string unit) => unit switch
    {
        WeatherOptionKinds.WindSpeedUnitMs => "m/s",
        WeatherOptionKinds.WindSpeedUnitMph => "mph",
        _ => "km/h"
    };

    private string GetDefaultViewDisplayName(string view) => view switch
    {
        WeatherOptionKinds.DefaultViewWeek => _localize("Weather.View.Week"),
        _ => _localize("Weather.View.Today")
    };

    private string GetSkinDisplayName(string skin) => skin switch
    {
        WeatherOptionKinds.SkinRich => _localize("Weather.Skin.Rich"),
        _ => _localize("Weather.Skin.Standard")
    };

    private string GetIconStyleDisplayName(string style) => style switch
    {
        WeatherOptionKinds.IconStyleDeskBox => _localize("Weather.IconStyle.DeskBox"),
        WeatherOptionKinds.IconStyleLine => _localize("Weather.IconStyle.Line"),
        WeatherOptionKinds.IconStyleFluent => _localize("Weather.IconStyle.Fluent"),
        _ => _localize("Weather.IconStyle.Flat")
    };

    private string GetDataSourceDisplayName(string source) => source switch
    {
        WeatherOptionKinds.DataSourceOpenMeteo => _localize("Weather.DataSource.OpenMeteo"),
        _ => _localize("Weather.DataSource.MSN")
    };

    private string GetRefreshIntervalDisplayName(int minutes) => minutes switch
    {
        15 => _format("Weather.Refresh.Minute", [minutes]),
        30 => _format("Weather.Refresh.Minute", [minutes]),
        60 => _localize("Weather.Refresh.Hour"),
        180 => _format("Weather.Refresh.Hours", [3]),
        _ => $"{minutes} min"
    };

    private void RunWrite(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
        }

        if (!_isSyncingPresentation)
        {
            Refresh();
        }
    }

    // Build real SettingsOption[] arrays (not collection expressions): the
    // hidden read-only-array type cannot marshal across the WinRT ABI in
    // Native AOT builds and would leave the ItemsSource empty.
    private static SettingsOption[] BuildOptions<T>(T[] values, string[] displayNames)
    {
        var options = new SettingsOption[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            options[index] = new SettingsOption(values[index]!, displayNames[index]);
        }

        return options;
    }

    private static IReadOnlyList<SettingsOption> WrapOptions(SettingsOption[] options) =>
        options;
}
