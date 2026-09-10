using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.WeatherPackage.Helpers;
using DeskBox.WeatherPackage.Models;
using DeskBox.WeatherPackage.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.WeatherPackage.ViewModels;

public sealed partial class WeatherWidgetViewModel
{
    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        // Loading the local snapshot is the only work allowed to delay widget
        // construction. Network location and forecast requests start after the
        // host has been revealed so startup remains responsive when offline.
        await LoadCachedWeatherAsync();
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowRevealCompleted)
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync(bool userTriggered = false, bool forceRefresh = false)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isRefreshing)
        {
            // Ordinary activation/timer requests are already satisfied by the
            // in-flight operation. Preserve one explicit user/settings request.
            if (userTriggered || forceRefresh)
            {
                Interlocked.Increment(ref _refreshRequestVersion);
                _refreshPending = true;
                _pendingForceRefresh |= forceRefresh || userTriggered;
                _pendingUserTriggeredRefresh |= userTriggered;
            }
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!WeatherRefreshBackoffPolicy.CanAttempt(
                now,
                _automaticRefreshNotBeforeUtc,
                userTriggered,
                forceRefresh))
        {
            PackageLogger.LogVerbose(
                $"[WeatherWidget] Refresh deferred until=" +
                $"{_automaticRefreshNotBeforeUtc:O} failures={_consecutiveRefreshFailures}");
            return;
        }

        int requestVersion = Interlocked.Increment(ref _refreshRequestVersion);
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;

        _refreshWasUserTriggered = userTriggered;
        IsRefreshing = true;
        bool refreshSucceeded = false;
        try
        {
            await EnsureLocationAsync(cancellation.Token);
            if (_isDisposed || requestVersion != Volatile.Read(ref _refreshRequestVersion))
            {
                return;
            }

            TimeSpan cacheDuration = GetConfiguredRefreshInterval();
            _weatherData = await _weatherService.GetWeatherAsync(
                _latitude,
                _longitude,
                _locationName,
                forceRefresh: userTriggered || forceRefresh,
                cacheDuration: cacheDuration,
                dataSource: _settingsService?.Settings.WeatherDataSource,
                cancellationToken: cancellation.Token);
            if (_isDisposed || requestVersion != Volatile.Read(ref _refreshRequestVersion))
            {
                return;
            }

            if (_weatherData?.Current is not null)
            {
                ApplyWeatherData(_weatherData);
                HasData = true;
                refreshSucceeded = !_weatherData.IsStale;
                if (refreshSucceeded)
                {
                    RegisterRefreshSuccess(cacheDuration);
                }
                else
                {
                    RegisterRefreshFailure();
                }
            }
            else
            {
                // API failed and no cached data for this location.
                // Clear the display so we don't show a previous city's weather.
                HasData = false;
                RegisterRefreshFailure();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _refreshWasUserTriggered = false;
        }
        catch (Exception ex)
        {
            PackageLogger.Log($"[WeatherWidget] Refresh failed: {ex.Message}");
            RegisterRefreshFailure();
        }
        finally
        {
            _refreshCancellation = null;
            IsRefreshing = false;

            // Only show the toast for user-triggered refreshes (not auto-timer)
            if (_refreshWasUserTriggered &&
                requestVersion == Volatile.Read(ref _refreshRequestVersion))
            {
                ShowRefreshStatusToast(refreshSucceeded);
            }

            _refreshWasUserTriggered = false;
            if (_refreshPending && !_isDisposed)
            {
                bool pendingUserTriggered = _pendingUserTriggeredRefresh;
                bool pendingForceRefresh = _pendingForceRefresh;
                _refreshPending = false;
                _pendingUserTriggeredRefresh = false;
                _pendingForceRefresh = false;
                _ = RefreshAsync(pendingUserTriggered, pendingForceRefresh);
            }
        }
    }

    private void RegisterRefreshSuccess(TimeSpan refreshInterval)
    {
        _consecutiveRefreshFailures = 0;
        _automaticRefreshNotBeforeUtc = DateTimeOffset.UtcNow + refreshInterval;
    }

    private void RegisterRefreshFailure()
    {
        _consecutiveRefreshFailures++;
        TimeSpan delay = WeatherRefreshBackoffPolicy.GetFailureDelay(
            _consecutiveRefreshFailures);
        _automaticRefreshNotBeforeUtc = DateTimeOffset.UtcNow + delay;
        PackageLogger.Log(
            $"[WeatherWidget] Refresh backoff failures={_consecutiveRefreshFailures} " +
            $"delayMinutes={delay.TotalMinutes:0}");
    }

    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = WeatherSettingsContext.NormalizeTextSize(_settingsService.Settings.TextSize);
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
        if (_isWindowRevealCompleted)
        {
            _ = RefreshAsync();
        }
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

        if (!visible)
        {
            _refreshCancellation?.Cancel();
            _isWindowRevealCompleted = false;
            Interlocked.Increment(ref _refreshRequestVersion);
            _refreshPending = false;
            _pendingForceRefresh = false;
            _pendingUserTriggeredRefresh = false;
            _refreshTimer?.Stop();
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !IsWidgetActive || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        _refreshTimer?.Start();
        _ = RefreshAsync();
    }

    public void ToggleViewMode()
    {
        SetViewMode(!IsWeekView);
    }

    public void SetViewMode(bool useWeekView)
    {
        bool previousWeekView = IsWeekView;
        bool previousOverride = _hasViewModeOverride;
        _config.Metadata.TryGetValue(
            WeatherWidgetViewModeSettings.MetadataKey,
            out string? previousMetadata);

        if (IsWeekView != useWeekView)
        {
            IsWeekView = useWeekView;
        }

        _hasViewModeOverride = true;
        if (WeatherWidgetViewModeSettings.SetWeekView(
                _config,
                useWeekView))
        {
            if (_settingsService?.UpdateWidget(
                    _config,
                    notifySubscribers: false) != true)
            {
                if (previousMetadata is null)
                {
                    _config.Metadata.Remove(WeatherWidgetViewModeSettings.MetadataKey);
                }
                else
                {
                    _config.Metadata[WeatherWidgetViewModeSettings.MetadataKey] =
                        previousMetadata;
                }
                _hasViewModeOverride = previousOverride;
                IsWeekView = previousWeekView;
            }
        }
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

        WeatherLayoutPresentationState previousState = CaptureLayoutPresentationState();
        _lastAvailableWidth = width;
        _lastAvailableHeight = height;

        if (_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        ApplyLayoutModeForSize(width, height, previousState);
    }

    internal void UpdateSystemTextScaleFactor(double textScaleFactor)
    {
        double normalized =
            PackageEnvironment.NormalizeSystemTextScaleFactor(
                textScaleFactor);
        if (Math.Abs(normalized - _systemTextScaleFactor) < 0.001)
        {
            return;
        }

        WeatherLayoutPresentationState previousState =
            CaptureLayoutPresentationState();
        _systemTextScaleFactor = normalized;
        ApplyLayoutModeForSize(
            _lastAvailableWidth,
            _lastAvailableHeight,
            previousState);
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

        WeatherLayoutPresentationState previousState = CaptureLayoutPresentationState();
        _isResponsiveLayoutTransitionActive = true;
        _lastAvailableWidth = targetWidth;
        _lastAvailableHeight = targetHeight;
        if (!isCollapsing)
        {
            ApplyLayoutModeForSize(targetWidth, targetHeight, previousState);
        }
    }

    internal void CompleteResponsiveLayoutTransition(double finalWidth, double finalHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        UpdateAvailableSize(finalWidth, finalHeight);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        WeatherLayoutPresentationState previousState = CaptureLayoutPresentationState();
        _isResponsiveLayoutTransitionActive = false;
        ApplyLayoutModeForSize(
            _lastAvailableWidth,
            _lastAvailableHeight,
            previousState);
    }

    private void ApplyLayoutModeForSize(
        double width,
        double height,
        WeatherLayoutPresentationState previousState)
    {
        string newLayout = DetermineLayoutMode(
            width,
            height,
            _layoutMode,
            TextSize,
            _systemTextScaleFactor);
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

        // SizeChanged can be raised repeatedly while XAML is arranging the same
        // visual tree. Re-notifying unchanged height-derived properties creates a
        // layout feedback loop and needlessly rewrites the compact presentation.
        WeatherLayoutPresentationState currentState = CaptureLayoutPresentationState();
        if (previousState.ExpandedSunriseVisibility != currentState.ExpandedSunriseVisibility)
        {
            OnPropertyChanged(nameof(ExpandedSunriseVisibility));
        }

        if (previousState.ExpandedHourlyPrecipVisibility != currentState.ExpandedHourlyPrecipVisibility)
        {
            OnPropertyChanged(nameof(ExpandedHourlyPrecipVisibility));
        }

        if (previousState.ExpandedHourlyCardHeight != currentState.ExpandedHourlyCardHeight)
        {
            OnPropertyChanged(nameof(ExpandedHourlyCardHeight));
        }

        if (previousState.ExpandedSecondaryMetricsVisibility != currentState.ExpandedSecondaryMetricsVisibility)
        {
            OnPropertyChanged(nameof(ExpandedSecondaryMetricsVisibility));
        }

        if (previousState.ExpandedHourlyDividerVisibility != currentState.ExpandedHourlyDividerVisibility)
        {
            OnPropertyChanged(nameof(ExpandedHourlyDividerVisibility));
        }

        if (previousState.PrimaryMetricsVisibility != currentState.PrimaryMetricsVisibility)
        {
            OnPropertyChanged(nameof(PrimaryMetricsVisibility));
        }

        if (previousState.MiniDescriptionVisibility != currentState.MiniDescriptionVisibility)
        {
            OnPropertyChanged(nameof(MiniDescriptionVisibility));
        }

        if (previousState.MiniHeaderVisibility != currentState.MiniHeaderVisibility)
        {
            OnPropertyChanged(nameof(MiniHeaderVisibility));
        }

        if (previousState.MiniDetailsVisibility != currentState.MiniDetailsVisibility)
        {
            OnPropertyChanged(nameof(MiniDetailsVisibility));
        }

        if (Math.Abs(previousState.HourlyCardWidth - currentState.HourlyCardWidth) >= 0.001)
        {
            OnPropertyChanged(nameof(HourlyCardWidth));
        }

    }

    private WeatherLayoutPresentationState CaptureLayoutPresentationState()
    {
        return new WeatherLayoutPresentationState(
            ExpandedSunriseVisibility,
            ExpandedHourlyPrecipVisibility,
            ExpandedHourlyCardHeight,
            ExpandedSecondaryMetricsVisibility,
            ExpandedHourlyDividerVisibility,
            PrimaryMetricsVisibility,
            MiniHeaderVisibility,
            MiniDescriptionVisibility,
            MiniDetailsVisibility,
            HourlyCardWidth);
    }

    private readonly record struct WeatherLayoutPresentationState(
        Visibility ExpandedSunriseVisibility,
        Visibility ExpandedHourlyPrecipVisibility,
        double ExpandedHourlyCardHeight,
        Visibility ExpandedSecondaryMetricsVisibility,
        Visibility ExpandedHourlyDividerVisibility,
        Visibility PrimaryMetricsVisibility,
        Visibility MiniHeaderVisibility,
        Visibility MiniDescriptionVisibility,
        Visibility MiniDetailsVisibility,
        double HourlyCardWidth);

    /// <summary>
    /// Determines layout mode using hysteresis: once in a higher layout, the
    /// widget stays there until size drops significantly below the upgrade
    /// threshold. This prevents flickering and the "almost fits" problem.
    /// Three levels: Mini, Compact, Expanded (merged Standard+Detailed).
    /// </summary>
    internal static string DetermineLayoutMode(
        double width,
        double height,
        string currentLayout,
        double textSize = WeatherSettingsContext.DefaultTextSize,
        double systemTextScaleFactor =
            PackageEnvironment.MinSystemTextScaleFactor)
    {
        // The content area excludes the standard title row. Breakpoints grow
        // with the effective typography, so accessibility text scaling moves
        // to a simpler presentation before any text has to be squeezed.
        double typographyDelta =
            ResolveTypographyScale(textSize, systemTextScaleFactor) - 1;
        double miniUpgradeW = 190 + (42 * typographyDelta);
        double miniUpgradeH = 145 + (58 * typographyDelta);
        double miniDowngradeW = 178 + (38 * typographyDelta);
        double miniDowngradeH = 134 + (52 * typographyDelta);

        double expandedUpgradeW = 300 + (110 * typographyDelta);
        double expandedUpgradeH = 260 + (150 * typographyDelta);
        double expandedDowngradeW = 280 + (96 * typographyDelta);
        double expandedDowngradeH = 240 + (132 * typographyDelta);

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

    internal static double ResolveTypographyScale(
        double textSize,
        double systemTextScaleFactor)
    {
        double normalizedTextSize = WeatherSettingsContext.NormalizeTextSize(textSize);
        double normalizedSystemScale =
            PackageEnvironment.NormalizeSystemTextScaleFactor(
                systemTextScaleFactor);
        double appearanceScale = Math.Max(
            1,
            normalizedTextSize / WeatherSettingsContext.DefaultTextSize);
        return normalizedSystemScale * appearanceScale;
    }
}
