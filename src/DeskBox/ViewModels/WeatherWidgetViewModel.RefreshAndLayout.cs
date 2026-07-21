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
    /// Called when the widget is resized. Determines the layout mode (Mini/Compact/Expanded).
    /// </summary>
    public void UpdateAvailableSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height))
        {
            return;
        }

        _lastAvailableWidth = width;
        _lastAvailableHeight = height;

        if (_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        ApplyLayoutModeForSize(width, height);
    }

    internal void BeginResponsiveLayoutTransition(
        double targetWidth,
        double targetHeight,
        bool isCollapsing)
    {
        if (!double.IsFinite(targetWidth) || !double.IsFinite(targetHeight))
        {
            return;
        }

        _isResponsiveLayoutTransitionActive = true;
        _lastAvailableWidth = targetWidth;
        _lastAvailableHeight = targetHeight;
        if (!isCollapsing)
        {
            ApplyLayoutModeForSize(targetWidth, targetHeight);
        }
    }

    internal void CompleteResponsiveLayoutTransition(double finalWidth, double finalHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        UpdateAvailableSize(finalWidth, finalHeight);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        ApplyLayoutModeForSize(_lastAvailableWidth, _lastAvailableHeight);
    }

    private void ApplyLayoutModeForSize(double width, double height)
    {

        string newLayout = DetermineLayoutMode(width, height, _layoutMode);
        if (!string.Equals(newLayout, _layoutMode, StringComparison.Ordinal))
        {
            LayoutMode = newLayout;
            OnPropertyChanged(nameof(MiniLayoutVisibility));
            OnPropertyChanged(nameof(CompactLayoutVisibility));
            OnPropertyChanged(nameof(ExpandedLayoutVisibility));
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

        // Notify flexible visibility properties that depend on available height
        OnPropertyChanged(nameof(ExpandedSunriseVisibility));
        OnPropertyChanged(nameof(ExpandedHourlyPrecipVisibility));
        OnPropertyChanged(nameof(ExpandedHourlyCardHeight));
    }

    /// <summary>
    /// Determines layout mode using hysteresis: once in a higher layout, the
    /// widget stays there until size drops significantly below the upgrade
    /// threshold. This prevents flickering and the "almost fits" problem.
    /// Three levels: Mini, Compact, Expanded (merged Standard+Detailed).
    /// </summary>
    internal static string DetermineLayoutMode(double width, double height, string currentLayout)
    {
        // The content area excludes the standard 46px title row.
        const double miniUpgradeW = 178, miniUpgradeH = 126;
        const double miniDowngradeW = 168, miniDowngradeH = 116;

        const double expandedUpgradeW = 250, expandedUpgradeH = 169;
        const double expandedDowngradeW = 230, expandedDowngradeH = 154;

        // Mini is always forced for very small sizes regardless of hysteresis
        if (width <= miniDowngradeW || height <= miniDowngradeH)
        {
            return "Mini";
        }

        switch (currentLayout)
        {
            case "Mini":
                // Upgrade to Compact/Expanded when enough room
                if (width >= expandedUpgradeW && height >= expandedUpgradeH)
                {
                    return "Expanded";
                }
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    return "Compact";
                }
                return "Mini";

            case "Compact":
                // Upgrade to Expanded when enough room
                if (width >= expandedUpgradeW && height >= expandedUpgradeH)
                {
                    return "Expanded";
                }
                // Downgrade to Mini if significantly smaller
                if (width <= miniDowngradeW || height <= miniDowngradeH)
                {
                    return "Mini";
                }
                return "Compact";

            case "Expanded":
                // Downgrade to Compact if no longer enough room (with hysteresis)
                if (width <= expandedDowngradeW || height <= expandedDowngradeH)
                {
                    if (width <= miniDowngradeW || height <= miniDowngradeH)
                    {
                        return "Mini";
                    }
                    return "Compact";
                }
                return "Expanded";

            default:
                // First-time default: use upgrade thresholds
                if (width >= expandedUpgradeW && height >= expandedUpgradeH)
                {
                    return "Expanded";
                }
                if (width >= miniUpgradeW && height >= miniUpgradeH)
                {
                    return "Compact";
                }
                return "Mini";
        }
    }
}
