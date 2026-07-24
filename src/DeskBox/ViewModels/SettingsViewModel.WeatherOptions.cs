using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
public string[] AvailableWeatherTemperatureUnits { get; } =
[
    SettingsService.WeatherTemperatureUnitCelsius,
    SettingsService.WeatherTemperatureUnitFahrenheit
];

public string[] AvailableWeatherTemperatureUnitDisplayNames =>
    _cachedWeatherTempUnitDisplayNames ??= AvailableWeatherTemperatureUnits.Select(GetWeatherTempUnitDisplayName).ToArray();

public string SelectedWeatherTemperatureUnit
{
    get => _selectedWeatherTemperatureUnit;
    set
    {
        string normalized = value == SettingsService.WeatherTemperatureUnitFahrenheit
            ? SettingsService.WeatherTemperatureUnitFahrenheit
            : SettingsService.WeatherTemperatureUnitCelsius;
        if (!SetProperty(ref _selectedWeatherTemperatureUnit, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherTemperatureUnit = _selectedWeatherTemperatureUnit;
        _settingsService.SaveDebounced();
    }
}


public string[] AvailableWeatherWindSpeedUnits { get; } =
[
    SettingsService.WeatherWindSpeedUnitKmh,
    SettingsService.WeatherWindSpeedUnitMs,
    SettingsService.WeatherWindSpeedUnitMph
];

public string[] AvailableWeatherWindSpeedUnitDisplayNames =>
    _cachedWeatherWindUnitDisplayNames ??= AvailableWeatherWindSpeedUnits.Select(GetWeatherWindUnitDisplayName).ToArray();

public string SelectedWeatherWindSpeedUnit
{
    get => _selectedWeatherWindSpeedUnit;
    set
    {
        string normalized = value is SettingsService.WeatherWindSpeedUnitMs or SettingsService.WeatherWindSpeedUnitMph
            ? value
            : SettingsService.WeatherWindSpeedUnitKmh;
        if (!SetProperty(ref _selectedWeatherWindSpeedUnit, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherWindSpeedUnit = _selectedWeatherWindSpeedUnit;
        _settingsService.SaveDebounced();
    }
}


public string[] AvailableWeatherDefaultViews { get; } =
[
    SettingsService.WeatherDefaultViewToday,
    SettingsService.WeatherDefaultViewWeek
];

public string[] AvailableWeatherDefaultViewDisplayNames =>
    _cachedWeatherDefaultViewDisplayNames ??= AvailableWeatherDefaultViews.Select(GetWeatherDefaultViewDisplayName).ToArray();

public string SelectedWeatherDefaultView
{
    get => _selectedWeatherDefaultView;
    set
    {
        string normalized = value == SettingsService.WeatherDefaultViewWeek
            ? SettingsService.WeatherDefaultViewWeek
            : SettingsService.WeatherDefaultViewToday;
        if (!SetProperty(ref _selectedWeatherDefaultView, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherDefaultView = _selectedWeatherDefaultView;
        _settingsService.SaveDebounced();
    }
}


public string[] AvailableWeatherSkins { get; } =
[
    SettingsService.WeatherSkinStandard,
    SettingsService.WeatherSkinRich
];

public string[] AvailableWeatherSkinDisplayNames =>
    _cachedWeatherSkinDisplayNames ??= AvailableWeatherSkins.Select(GetWeatherSkinDisplayName).ToArray();

public string SelectedWeatherSkin
{
    get => _selectedWeatherSkin;
    set
    {
        string normalized = value == SettingsService.WeatherSkinRich
            ? SettingsService.WeatherSkinRich
            : SettingsService.WeatherSkinStandard;
        if (!SetProperty(ref _selectedWeatherSkin, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherSkin = _selectedWeatherSkin;
        _settingsService.SaveDebounced();
    }
}


public string[] AvailableWeatherDataSources { get; } =
[
    SettingsService.WeatherDataSourceMsn,
    SettingsService.WeatherDataSourceOpenMeteo
];

public string[] AvailableWeatherDataSourceDisplayNames =>
    _cachedWeatherDataSourceDisplayNames ??= AvailableWeatherDataSources.Select(GetWeatherDataSourceDisplayName).ToArray();

public string SelectedWeatherDataSource
{
    get => _selectedWeatherDataSource;
    set
    {
        string normalized = value == SettingsService.WeatherDataSourceOpenMeteo
            ? SettingsService.WeatherDataSourceOpenMeteo
            : SettingsService.WeatherDataSourceMsn;
        if (!SetProperty(ref _selectedWeatherDataSource, normalized))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherDataSource = _selectedWeatherDataSource;
        _settingsService.SaveDebounced();
    }
}

private string GetWeatherDataSourceDisplayName(string source) => source switch
{
    SettingsService.WeatherDataSourceOpenMeteo => _localizationService.T("Weather.DataSource.OpenMeteo"),
    _ => _localizationService.T("Weather.DataSource.MSN")
};


public int[] AvailableWeatherRefreshIntervals { get; } = [15, 30, 60, 180];

public string[] AvailableWeatherRefreshIntervalDisplayNames =>
    _cachedWeatherRefreshIntervalDisplayNames ??= AvailableWeatherRefreshIntervals.Select(GetWeatherRefreshIntervalDisplayName).ToArray();

public int SelectedWeatherRefreshInterval
{
    get => _selectedWeatherRefreshInterval;
    set
    {
        int clamped = Math.Clamp(value, SettingsService.WeatherRefreshMinMinutes, SettingsService.WeatherRefreshMaxMinutes);
        if (!SetProperty(ref _selectedWeatherRefreshInterval, clamped))
        {
            return;
        }

        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.WeatherRefreshIntervalMinutes = _selectedWeatherRefreshInterval;
        _settingsService.SaveDebounced();
    }
}


private string GetWeatherTempUnitDisplayName(string unit) => unit switch
{
    SettingsService.WeatherTemperatureUnitFahrenheit => _localizationService.T("Weather.Unit.Fahrenheit"),
    _ => _localizationService.T("Weather.Unit.Celsius")
};

private string GetWeatherWindUnitDisplayName(string unit) => unit switch
{
    SettingsService.WeatherWindSpeedUnitMs => "m/s",
    SettingsService.WeatherWindSpeedUnitMph => "mph",
    _ => "km/h"
};

private string GetWeatherDefaultViewDisplayName(string view) => view switch
{
    SettingsService.WeatherDefaultViewWeek => _localizationService.T("Weather.View.Week"),
    _ => _localizationService.T("Weather.View.Today")
};

private string GetWeatherSkinDisplayName(string skin) => skin switch
{
    SettingsService.WeatherSkinRich => _localizationService.T("Weather.Skin.Rich"),
    _ => _localizationService.T("Weather.Skin.Standard")
};

private string GetWeatherRefreshIntervalDisplayName(int minutes) => minutes switch
{
    15 => _localizationService.Format("Weather.Refresh.Minute", minutes),
    30 => _localizationService.Format("Weather.Refresh.Minute", minutes),
    60 => _localizationService.T("Weather.Refresh.Hour"),
    180 => _localizationService.Format("Weather.Refresh.Hours", 3),
    _ => $"{minutes} min"
};

partial void OnWeatherAutoLocationChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherAutoLocation = value;
    _settingsService.SaveDebounced();
    OnPropertyChanged(nameof(WeatherCityNameVisibility));
    OnPropertyChanged(nameof(WeatherLocationStatusVisibility));

    if (value)
    {
        _ = RefreshWeatherLocationStatusAsync();
    }
}

// P2-2: Search box is always visible — user can manually override even in auto mode.
public Visibility WeatherCityNameVisibility => Visibility.Visible;

// ─── P0-1: Location status feedback ───

private string _weatherLocationStatusText = string.Empty;
public string WeatherLocationStatusText
{
    get => _weatherLocationStatusText;
    private set => SetProperty(ref _weatherLocationStatusText, value);
}

private bool _weatherLocationStatusIsError;
public bool WeatherLocationStatusIsError
{
    get => _weatherLocationStatusIsError;
    private set => SetProperty(ref _weatherLocationStatusIsError, value);
}

public Visibility WeatherLocationStatusVisibility =>
    WeatherAutoLocation && !string.IsNullOrEmpty(WeatherLocationStatusText)
        ? Visibility.Visible
        : Visibility.Collapsed;

/// <summary>
/// Refreshes the location status text shown below the auto-location toggle.
/// </summary>
public async Task RefreshWeatherLocationStatusAsync()
{
    if (!WeatherAutoLocation)
    {
        WeatherLocationStatusText = string.Empty;
        OnPropertyChanged(nameof(WeatherLocationStatusVisibility));
        return;
    }

    WeatherLocationStatusText = _localizationService.T("Settings.Weather.AutoLocation.Locating");
    WeatherLocationStatusIsError = false;
    OnPropertyChanged(nameof(WeatherLocationStatusVisibility));

    try
    {
        var result = await WindowsLocationHelper.GetLocationAsync(_localizationService);
        if (result is not null)
        {
            WeatherLocationStatusText = _localizationService.Format(
                "Settings.Weather.AutoLocation.LocatedAt", result.Value.Name);
            WeatherLocationStatusIsError = false;
        }
        else
        {
            WeatherLocationStatusText = _localizationService.T("Settings.Weather.AutoLocation.Failed");
            WeatherLocationStatusIsError = true;
        }
    }
    catch (Exception ex)
    {
        App.Log($"[SettingsViewModel] Location status refresh failed: {ex.Message}");
        WeatherLocationStatusText = _localizationService.T("Settings.Weather.AutoLocation.Failed");
        WeatherLocationStatusIsError = true;
    }

    OnPropertyChanged(nameof(WeatherLocationStatusVisibility));
}

partial void OnWeatherCityNameChanged(string value)
{
    // Don't save on text change — only save when a city is selected from suggestions.
    // See WeatherCitySuggestions / SelectWeatherCity.
}

// ─── Weather city search (AutoSuggestBox) ───

private string _weatherCitySearchText = string.Empty;
private bool _isWeatherCitySearchUpdating;

public string WeatherCitySearchText
{
    get => _weatherCitySearchText;
    set => SetProperty(ref _weatherCitySearchText, value);
}

/// <summary>
/// Suggestions shown in the AutoSuggestBox dropdown.
/// Populated with nearby popular cities when empty, or search results when typing.
/// </summary>
public ObservableCollection<WeatherCitySearchResult> WeatherCitySuggestions { get; } = [];

private bool _isWeatherCitySearching;
public bool IsWeatherCitySearching
{
    get => _isWeatherCitySearching;
    private set => SetProperty(ref _isWeatherCitySearching, value);
}

/// <summary>
/// True when a search was performed but returned no results.
/// </summary>
private bool _hasNoCitySearchResults;
public bool HasNoCitySearchResults
{
    get => _hasNoCitySearchResults;
    private set
    {
        if (SetProperty(ref _hasNoCitySearchResults, value))
        {
            OnPropertyChanged(nameof(HasNoCitySearchResultsVisibility));
        }
    }
}

public Visibility HasNoCitySearchResultsVisibility => HasNoCitySearchResults
    ? Visibility.Visible
    : Visibility.Collapsed;

public string WeatherCitySearchPlaceholder => _localizationService.T("Weather.CitySearch.Placeholder");
public string WeatherCityNoResultsText => _localizationService.T("Weather.CitySearch.NoResults");

private CancellationTokenSource? _citySearchCts;
private CitySearchService? _citySearchService;
private double? _cachedLocationLat;
private double? _cachedLocationLon;
private bool _locationInitialized;

/// <summary>
/// Called from the AutoSuggestBox TextChanged event (code-behind).
/// Populates suggestions with nearby popular cities when empty,
/// or search results when the user types.
/// </summary>
public async Task UpdateWeatherCitySuggestionsAsync(string query)
{
    if (_isWeatherCitySearchUpdating)
    {
        return;
    }

    // Cancel any pending search
    _citySearchCts?.Cancel();
    _citySearchCts = new CancellationTokenSource();
    var ct = _citySearchCts.Token;

    // Empty query → show nearby popular cities
    if (string.IsNullOrWhiteSpace(query))
    {
        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;

        await PopulateNearbyPopularCitiesAsync(ct);
        return;
    }

    // Non-empty but too short → clear and wait.
    // P1-1: Single CJK character is valid (e.g. "京" → 北京).
    bool hasCjk = query.Any(c => c >= '\u4e00' && c <= '\u9fff');
    if (!hasCjk && query.Length < 2)
    {
        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;
        return;
    }

    IsWeatherCitySearching = true;
    try
    {
        await Task.Delay(300, ct);

        // Guard: if a city was selected while we were waiting, abort.
        if (_isWeatherCitySearchUpdating || ct.IsCancellationRequested)
        {
            return;
        }

        _citySearchService ??= new CitySearchService();
        var language = _localizationService.ApiLanguageCode;
        var results = await _citySearchService.SearchAsync(
            query, language, _cachedLocationLat, _cachedLocationLon, ct);

        if (ct.IsCancellationRequested || _isWeatherCitySearchUpdating)
        {
            return;
        }

        WeatherCitySuggestions.Clear();
        foreach (var r in results)
        {
            WeatherCitySuggestions.Add(r);
        }
        HasNoCitySearchResults = WeatherCitySuggestions.Count == 0;
    }
    catch (OperationCanceledException)
    {
        // Expected when a newer search supersedes this one.
    }
    catch (Exception ex)
    {
        App.Log($"[SettingsViewModel] City search failed: {ex.Message}");
    }
    finally
    {
        IsWeatherCitySearching = false;
    }
}

/// <summary>
/// Populates the suggestions with nearby popular cities based on user location.
/// Falls back to global popular cities if location is unavailable.
/// </summary>
private async Task PopulateNearbyPopularCitiesAsync(CancellationToken cancellationToken = default)
{
    cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
    try
    {
        _citySearchService ??= new CitySearchService();
        var language = _localizationService.ApiLanguageCode;

        // Try to get user location (cached after first call)
        if (!_locationInitialized)
        {
            var locResult = await WindowsLocationHelper.GetLocationAsync(_localizationService);
            cancellationToken.ThrowIfCancellationRequested();
            if (locResult is not null)
            {
                _cachedLocationLat = locResult.Value.Lat;
                _cachedLocationLon = locResult.Value.Lon;
            }
            _locationInitialized = true;
        }

        var cities = _citySearchService.GetNearbyPopularCities(
            _cachedLocationLat, _cachedLocationLon, language, maxCount: 8);
        cancellationToken.ThrowIfCancellationRequested();

        WeatherCitySuggestions.Clear();
        foreach (var c in cities)
        {
            WeatherCitySuggestions.Add(c);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        App.Log($"[SettingsViewModel] Failed to populate nearby cities: {ex.Message}");
    }
}

/// <summary>
/// Called when language changes to refresh the popular cities list.
/// Clears cached suggestions so they get repopulated on next focus.
/// </summary>
public void RefreshWeatherCityPopularCities()
{
    _citySearchCts?.Cancel();
    WeatherCitySuggestions.Clear();
    HasNoCitySearchResults = false;
}

public void SelectWeatherCity(WeatherCitySearchResult result)
{
    if (result is null)
    {
        return;
    }

    // Cancel any pending search so it can't overwrite our state.
    _citySearchCts?.Cancel();

    _isWeatherCitySearchUpdating = true;
    try
    {
        // P2-2: If user manually picks a city while auto-location is on,
        // switch to manual mode so the selection sticks.
        if (WeatherAutoLocation)
        {
            WeatherAutoLocation = false; // triggers OnWeatherAutoLocationChanged
        }

        _settingsService.Settings.WeatherCityName = result.DisplayName;
        _settingsService.Settings.WeatherLatitude = result.Latitude;
        _settingsService.Settings.WeatherLongitude = result.Longitude;
        _settingsService.SaveDebounced();

        WeatherCityName = result.DisplayName;
        _weatherCitySearchText = result.DisplayName;
        OnPropertyChanged(nameof(WeatherCitySearchText));

        WeatherCitySuggestions.Clear();
        HasNoCitySearchResults = false;
    }
    finally
    {
        _isWeatherCitySearchUpdating = false;
    }
}

public void ClearWeatherCitySuggestions()
{
    _citySearchCts?.Cancel();
    WeatherCitySuggestions.Clear();
    HasNoCitySearchResults = false;
}

public void RestoreWeatherCitySearchText()
{
    _isWeatherCitySearchUpdating = true;
    try
    {
        _weatherCitySearchText = _settingsService.Settings.WeatherCityName;
        OnPropertyChanged(nameof(WeatherCitySearchText));
    }
    finally
    {
        _isWeatherCitySearchUpdating = false;
    }
}

partial void OnWeatherShowForecastChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowForecast = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowSunriseChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowSunrise = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowUvIndexChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowUvIndex = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowPrecipitationChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowPrecipitation = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowHumidityChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowHumidity = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowWindChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowWind = value;
    _settingsService.SaveDebounced();
}

partial void OnWeatherShowPressureChanged(bool value)
{
    if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
    {
        return;
    }

    _settingsService.Settings.WeatherShowPressure = value;
    _settingsService.SaveDebounced();
}
}
