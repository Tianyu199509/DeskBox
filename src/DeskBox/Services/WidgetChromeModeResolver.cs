using DeskBox.Models;

namespace DeskBox.Services;

public sealed class WidgetChromeModeResolver
{
    private readonly SettingsService _settingsService;

    public WidgetChromeModeResolver(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public WidgetChromeMode Resolve(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        WidgetChromeCategory? categoryOverride = null)
    {
        var overrideMode = WidgetChromeModeNames.GetOverrideMode(config);
        if (overrideMode != WidgetChromeMode.System)
        {
            return CoerceAllowedMode(overrideMode, descriptor);
        }

        var settings = _settingsService.Settings;
        WidgetChromeCategory effectiveCategory = categoryOverride ?? descriptor.ChromeCategory;
        var globalValue = effectiveCategory == WidgetChromeCategory.Display
            ? settings.DisplayWidgetChromeMode
            : settings.InteractiveWidgetChromeMode;
        WidgetChromeMode defaultChromeMode = categoryOverride.HasValue
            ? (effectiveCategory == WidgetChromeCategory.Display
                ? WidgetChromeMode.Overlay
                : WidgetChromeMode.Standard)
            : descriptor.DefaultChromeMode;
        var globalMode = WidgetChromeModeNames.NormalizeMode(globalValue, defaultChromeMode);
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
