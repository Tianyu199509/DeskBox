using DeskBox.Models;

namespace DeskBox.Services;

public sealed class WidgetChromeModeResolver
{
    private readonly SettingsService _settingsService;

    public WidgetChromeModeResolver(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public WidgetChromeMode Resolve(WidgetConfig config, WidgetContentDescriptor descriptor)
    {
        var overrideMode = WidgetChromeModeNames.GetOverrideMode(config);
        if (overrideMode != WidgetChromeMode.System)
        {
            return CoerceAllowedMode(overrideMode, descriptor);
        }

        var settings = _settingsService.Settings;
        var globalValue = descriptor.ChromeCategory == WidgetChromeCategory.Display
            ? settings.DisplayWidgetChromeMode
            : settings.InteractiveWidgetChromeMode;
        var globalMode = WidgetChromeModeNames.NormalizeMode(globalValue, descriptor.DefaultChromeMode);
        return CoerceAllowedMode(globalMode, descriptor);
    }

    public static WidgetChromeMode CoerceAllowedMode(WidgetChromeMode mode, WidgetContentDescriptor descriptor)
    {
        return mode switch
        {
            WidgetChromeMode.Overlay when !descriptor.CanUseOverlayChrome => descriptor.DefaultChromeMode,
            WidgetChromeMode.Hidden when !descriptor.CanHideChrome => descriptor.CanUseOverlayChrome
                ? WidgetChromeMode.Overlay
                : descriptor.DefaultChromeMode,
            WidgetChromeMode.System => descriptor.DefaultChromeMode,
            _ => mode
        };
    }
}
