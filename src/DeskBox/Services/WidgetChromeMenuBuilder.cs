using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetChromeMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        LocalizationService localizationService,
        Action<WidgetChromeMode> applyMode)
    {
        var selectedMode = WidgetChromeModeNames.GetOverrideMode(config);
        var subItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.ChromeMode.Title"),
            Icon = new FontIcon { Glyph = "\uE771" }
        };

        foreach (var mode in new[]
                 {
                     WidgetChromeMode.System,
                     WidgetChromeMode.Standard,
                     WidgetChromeMode.Compact,
                     WidgetChromeMode.Overlay,
                     WidgetChromeMode.Hidden
                 })
        {
            if (mode == WidgetChromeMode.Hidden && !descriptor.CanHideChrome)
            {
                continue;
            }

            if (mode == WidgetChromeMode.Overlay && !descriptor.CanUseOverlayChrome)
            {
                continue;
            }

            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(GetTextKey(mode)),
                IsChecked = selectedMode == mode
            };
            item.Click += (_, _) => applyMode(mode);
            subItem.Items.Add(item);
        }

        return subItem;
    }

    private static string GetTextKey(WidgetChromeMode mode)
    {
        return mode switch
        {
            WidgetChromeMode.System => "Widget.ChromeMode.System",
            WidgetChromeMode.Compact => "Widget.ChromeMode.Compact",
            WidgetChromeMode.Overlay => "Widget.ChromeMode.Overlay",
            WidgetChromeMode.Hidden => "Widget.ChromeMode.Hidden",
            _ => "Widget.ChromeMode.Standard"
        };
    }
}
