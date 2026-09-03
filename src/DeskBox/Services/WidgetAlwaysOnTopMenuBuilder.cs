using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetAlwaysOnTopMenuBuilder
{
    public static ToggleMenuFlyoutItem Create(
        LocalizationService localizationService,
        bool isAlwaysOnTop,
        Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.AlwaysOnTop"),
            Icon = new FontIcon { Glyph = "\uE718" },
            IsChecked = isAlwaysOnTop
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }
}
