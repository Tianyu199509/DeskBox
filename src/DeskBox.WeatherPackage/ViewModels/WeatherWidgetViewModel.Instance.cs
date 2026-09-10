using DeskBox.WeatherPackage.Services;

namespace DeskBox.WeatherPackage.ViewModels;

public sealed partial class WeatherWidgetViewModel
{
    internal void ApplyInstanceViewSelection()
    {
        if (_settingsService?.TryGetPendingWeekView(
                out bool pendingWeekView) == true)
        {
            _hasViewModeOverride = true;
            IsWeekView = pendingWeekView;
            UpdateViewSwitchButton();
            OnPropertyChanged(nameof(DisplayName));
            return;
        }

        _hasViewModeOverride = WeatherWidgetViewModeSettings.TryGetWeekView(_config, out bool week);
        IsWeekView = _hasViewModeOverride ? week : _settingsService?.Settings.WeatherDefaultView == WeatherSettingsContext.WeatherDefaultViewWeek;
        UpdateViewSwitchButton();
        OnPropertyChanged(nameof(DisplayName));
    }
}
