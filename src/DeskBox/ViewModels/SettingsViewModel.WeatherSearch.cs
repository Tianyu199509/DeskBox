using System.Threading;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private CancellationTokenSource? _citySearchCts;
    private CitySearchService? _citySearchService;
    private double? _cachedLocationLat;
    private double? _cachedLocationLon;
    private bool _locationInitialized;

    /// <summary>
    /// Answers the weather editor's user location-mode edits: a switch back
    /// to automatic location re-runs the Windows location lookup and pushes
    /// the status line into the editor (batch 48 host linkage).
    /// </summary>
    private void OnWeatherAutoLocationUserChanged(bool value)
    {
        if (value)
        {
            _ = RefreshWeatherLocationStatusAsync();
        }
    }

    /// <summary>
    /// Runs the auto-location lookup and pushes the status line into the
    /// weather editor. The lookup itself (the Windows location helper) is a
    /// host service the editor must not reference.
    /// </summary>
    internal async Task RefreshWeatherLocationStatusAsync()
    {
        if (_weatherSettings.SelectedLocationMode != WeatherOptionKinds.LocationModeAuto)
        {
            _weatherSettings.SetLocationStatus(string.Empty);
            return;
        }

        _weatherSettings.SetLocationStatus(
            _localizationService.T("Settings.Weather.AutoLocation.Locating"));
        try
        {
            var result = await WindowsLocationHelper.GetLocationAsync(_localizationService);
            if (result is not null)
            {
                _weatherSettings.SetLocationStatus(_localizationService.Format(
                    "Settings.Weather.AutoLocation.LocatedAt", result.Value.Name));
            }
            else
            {
                _weatherSettings.SetLocationStatus(
                    _localizationService.T("Settings.Weather.AutoLocation.Failed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Location status refresh failed: {ex.Message}");
            _weatherSettings.SetLocationStatus(
                _localizationService.T("Settings.Weather.AutoLocation.Failed"));
        }
    }

    /// <summary>
    /// Called from the AutoSuggestBox TextChanged event (code-behind).
    /// Populates suggestions with nearby popular cities when empty,
    /// or search results when the user types. The results are pushed into
    /// the weather editor's binding surface.
    /// </summary>
    internal async Task UpdateWeatherCitySuggestionsAsync(string query)
    {
        // Cancel any pending search. The superseded search's source is
        // disposed by that invocation's own finally once it unwinds on the
        // canceled token — never inline here, because the pending search
        // still holds and uses its token (audit P3).
        _citySearchCts?.Cancel();
        var search = new CancellationTokenSource();
        _citySearchCts = search;
        var ct = search.Token;
        try
        {
            // Empty query → show nearby popular cities
            if (string.IsNullOrWhiteSpace(query))
            {
                _weatherSettings.SetCitySuggestions(
                    Array.Empty<WeatherCitySearchResult>(), hasNoResults: false);
                await PopulateWeatherNearbyCitiesAsync(ct);
                return;
            }

            // Non-empty but too short → clear and wait.
            // P1-1: Single CJK character is valid (e.g., "京" → 北京).
            bool hasCjk = query.Any(c => c >= '\u4e00' && c <= '\u9fff');
            if (!hasCjk && query.Length < 2)
            {
                _weatherSettings.SetCitySuggestions(
                    Array.Empty<WeatherCitySearchResult>(), hasNoResults: false);
                return;
            }

            try
            {
                await Task.Delay(300, ct);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _citySearchService ??= new CitySearchService();
                var language = _localizationService.CurrentCultureName;
                var results = await _citySearchService.SearchAsync(
                    query, language, _cachedLocationLat, _cachedLocationLon, ct);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _weatherSettings.SetCitySuggestions(results, hasNoResults: results.Count == 0);
            }
            catch (OperationCanceledException)
            {
                // Expected when a newer search supersedes this one.
            }
            catch (Exception ex)
            {
                App.Log($"[SettingsViewModel] City search failed: {ex.Message}");
            }
        }
        finally
        {
            // This invocation owns the source it created; release it only
            // after its own awaits settled (cancel-then-dispose ordering).
            if (ReferenceEquals(_citySearchCts, search))
            {
                _citySearchCts = null;
            }
            search.Dispose();
        }
    }

    /// <summary>
    /// Lazily populates nearby popular cities the first time the Weather settings
    /// section is opened, avoiding unnecessary GPS / Geo-IP requests on every
    /// SettingsHost cold start.
    /// </summary>
    internal Task EnsureWeatherNearbyCitiesInitializedAsync() =>
        _locationInitialized
            ? Task.CompletedTask
            : PopulateWeatherNearbyCitiesAsync();

    /// <summary>
    /// Populates the editor's suggestions with nearby popular cities based
    /// on user location. Falls back to global popular cities if location is
    /// unavailable.
    /// </summary>
    private async Task PopulateWeatherNearbyCitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        try
        {
            _citySearchService ??= new CitySearchService();
            var language = _localizationService.CurrentCultureName;

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

            if (!cancellationToken.IsCancellationRequested)
            {
                _weatherSettings.SetCitySuggestions(cities, hasNoResults: false);
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
}
