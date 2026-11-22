#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const string AotWeatherSettingsOwnedWidgetId =
        "aot-5b4b2c2a-weather";

    internal void ApplyAotWeatherSettingsViewMode(bool useWeekView)
    {
        WidgetConfig config = GetAotWeatherSettingsConfig();
        if (WeatherWidgetViewModeSettings.SetWeekView(config, useWeekView))
        {
            _settingsService.UpdateWidget(config, notifySubscribers: false);
        }
    }

    internal AotWeatherSettingsWidgetSnapshot
        CaptureAotWeatherSettingsWidgetSnapshot()
    {
        WidgetConfig config = GetAotWeatherSettingsConfig();
        IDesktopWidgetWindow? host = GetLoadedDesktopWindows().SingleOrDefault(window =>
            string.Equals(
                window.Identity.WidgetId,
                AotWeatherSettingsOwnedWidgetId,
                StringComparison.Ordinal));
        bool hasOverride = WeatherWidgetViewModeSettings.TryGetWeekView(
            config,
            out bool useWeekView);
        config.Metadata.TryGetValue(
            WeatherWidgetViewModeSettings.MetadataKey,
            out string? metadataValue);

        return new AotWeatherSettingsWidgetSnapshot(
            config.Id,
            config.WidgetKind.ToString(),
            config.IsVisible,
            config.IsDisabled,
            FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Weather),
            hasOverride,
            useWeekView,
            metadataValue,
            host is not null,
            host?.WindowHandle.ToInt64() ?? 0,
            host?.Visible == true,
            host?.WindowContentRoot?.XamlRoot is not null);
    }

    private WidgetConfig GetAotWeatherSettingsConfig()
    {
        WidgetConfig config = FindConfig(AotWeatherSettingsOwnedWidgetId) ??
            throw new InvalidOperationException(
                "The owned Weather settings widget configuration is unavailable.");
        if (config.WidgetKind != WidgetKind.Weather)
        {
            throw new InvalidOperationException(
                "The owned Weather settings widget has the wrong kind.");
        }

        return config;
    }
}

internal sealed record AotWeatherSettingsWidgetSnapshot(
    string Id,
    string WidgetKind,
    bool IsVisible,
    bool IsDisabled,
    bool FeatureEnabled,
    bool HasViewModeOverride,
    bool UseWeekView,
    string? MetadataValue,
    bool IsLoaded,
    long WindowHandle,
    bool IsHostVisible,
    bool HasXamlRoot);
#endif
