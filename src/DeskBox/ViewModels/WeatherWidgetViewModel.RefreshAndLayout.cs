using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class WeatherWidgetViewModel
{
    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await RefreshAsync();
        if (_isDisposed)
        {
            return;
        }

        _refreshTimer?.Start();
    }

    public async Task RefreshAsync(bool userTriggered = false, bool forceRefresh = false)
    {
        if (_isDisposed || _isRefreshing)
        {
            return;
        }

        _refreshWasUserTriggered = userTriggered;
        _isRefreshing = true;
        IsRefreshing = true;
        bool refreshSucceeded = false;
        try
        {
            await EnsureLocationAsync();
            if (_isDisposed)
            {
                return;
            }

            TimeSpan cacheDuration = TimeSpan.FromMinutes(
                _settingsService is null
                    ? 30
                    : Math.Clamp(
                        _settingsService.Settings.WeatherRefreshIntervalMinutes,
                        SettingsService.WeatherRefreshMinMinutes,
                        SettingsService.WeatherRefreshMaxMinutes));
            _weatherData = await _weatherService.GetWeatherAsync(
                _latitude,
                _longitude,
                _locationName,
                forceRefresh: userTriggered || forceRefresh,
                cacheDuration: cacheDuration);
            if (_isDisposed)
            {
                return;
            }

            if (_weatherData?.Current is not null)
            {
                ApplyWeatherData(_weatherData);
                HasData = true;
                refreshSucceeded = !_weatherData.IsStale;
            }
            else
            {
                // API failed and no cached data for this location.
                // Clear the display so we don't show a previous city's weather.
                HasData = false;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WeatherWidget] Refresh failed: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
            IsRefreshing = false;

            // Only show the toast for user-triggered refreshes (not auto-timer)
            if (_refreshWasUserTriggered && HasData)
            {
                ShowRefreshStatusToast(refreshSucceeded);
            }
        }
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyWeatherSettings(_settingsService.Settings);
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        // Refresh on user interaction, but don't change IsWidgetActive —
        // that is now driven by window visibility (OnWindowVisibilityChanged).
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
        // No-op: animation and timer lifecycle is controlled by window visibility,
        // not activation state. This prevents animations from stopping when the
        // widget is visible at the desktop layer but not foreground-activated.
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Controls animation lifecycle and refresh timer based on actual visibility.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        IsWidgetActive = visible;

        if (visible)
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _refreshTimer?.Stop();
        }
    }

    public void ToggleViewMode()
    {
        IsWeekView = !IsWeekView;
        OnPropertyChanged(nameof(ForecastVisibility));
        OnPropertyChanged(nameof(WeekForecastVisibility));
    }

    /// <summary>
    /// Called when the widget is resized. Determines the layout mode (Compact/Standard/Detailed).
    /// </summary>
    public void UpdateAvailableSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        _lastAvailableWidth = width;
        _lastAvailableHeight = height;

        string newLayout = DetermineLayoutMode(width, height, _layoutMode);
        if (!string.Equals(newLayout, _layoutMode, StringComparison.Ordinal))
        {
            LayoutMode = newLayout;
            OnPropertyChanged(nameof(MiniLayoutVisibility));
            OnPropertyChanged(nameof(CompactLayoutVisibility));
            OnPropertyChanged(nameof(StandardLayoutVisibility));
            OnPropertyChanged(nameof(DetailedLayoutVisibility));
            OnPropertyChanged(nameof(CurrentEmojiSize));
            OnPropertyChanged(nameof(ForecastEmojiSize));
            OnPropertyChanged(nameof(TemperatureTextSize));
            OnPropertyChanged(nameof(WeekEmojiSize));
            OnPropertyChanged(nameof(WeekDayLabelTextSize));
            OnPropertyChanged(nameof(WeekTempMaxSize));
            OnPropertyChanged(nameof(WeekTempMinSize));
            OnPropertyChanged(nameof(HourlyCardWidth));
            OnPropertyChanged(nameof(SunriseVisibility));
        }
    }

    /// <summary>
    /// Determines layout mode using hysteresis: once in a higher layout, the
    /// widget stays there until size drops significantly below the upgrade
    /// threshold. This prevents flickering and the "almost fits" problem where
    /// a few pixels short would unnecessarily downgrade to a smaller layout.
    /// </summary>
    internal static string DetermineLayoutMode(double width, double height, string currentLayout)
    {
        // The content area excludes the standard 46px title row. These thresholds
        // therefore map a 180x180 widget to Compact while keeping 150x150 in Mini.
        const double miniUpgradeW = 178, miniUpgradeH = 126;
        const double miniDowngradeW = 168, miniDowngradeH = 116;

        const double compactUpgradeW = 250, compactUpgradeH = 169;
        const double compactDowngradeW = 230, compactDowngradeH = 154;

        const double detailedUpgradeW = 280, detailedUpgradeH = 234;
        const double detailedDowngradeW = 260, detailedDowngradeH = 204;

        // Mini is always forced for very small sizes regardless of hysteresis
        if (width <= miniDowngradeW || height <= miniDowngradeH)
        {
            return "Mini";
        }

        switch (currentLayout)
        {
            case "Mini":
                // Upgrade to Compact when enough room
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    goto CheckCompactUp;
                }
                return "Mini";

            case "Compact":
            CheckCompactUp:
                // Upgrade to Standard/Detailed when enough room
                if (width >= compactUpgradeW && height >= compactUpgradeH)
                {
                    // Check if we can go straight to Detailed
                    if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                    {
                        return "Detailed";
                    }
                    return "Standard";
                }
                // Stay in Compact unless we need to downgrade to Mini
                if (width <= miniDowngradeW || height <= miniDowngradeH)
                {
                    return "Mini";
                }
                return "Compact";

            case "Standard":
                // Upgrade to Detailed if enough room
                if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                {
                    return "Detailed";
                }
                // Downgrade to Compact only if significantly smaller
                if (width <= compactDowngradeW || height <= compactDowngradeH)
                {
                    return "Compact";
                }
                return "Standard";

            case "Detailed":
                // Downgrade to Standard if no longer enough room (with hysteresis)
                if (width <= detailedDowngradeW || height <= detailedDowngradeH)
                {
                    // Further check if we should go to Compact
                    if (width <= compactDowngradeW || height <= compactDowngradeH)
                    {
                        return "Compact";
                    }
                    return "Standard";
                }
                return "Detailed";

            default:
                // First-time default: use mid-range thresholds
                if (width >= detailedUpgradeW && height >= detailedUpgradeH)
                {
                    return "Detailed";
                }
                if (width >= compactUpgradeW && height >= compactUpgradeH)
                {
                    return "Standard";
                }
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    return "Compact";
                }
                return "Mini";
        }
    }
}
