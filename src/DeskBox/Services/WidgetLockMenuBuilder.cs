using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetLockMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        LocalizationService localizationService,
        bool isPositionLocked,
        bool isSizeLocked,
        Action<bool> setPositionLocked,
        Action<bool> setSizeLocked)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Lock.Title"),
            Icon = new FontIcon { Glyph = "\uE72E" }
        };

        var positionItem = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.LockPosition"),
            Icon = new FontIcon { Glyph = "\uE72E" },
            IsChecked = isPositionLocked
        };
        positionItem.Click += (_, _) => setPositionLocked(positionItem.IsChecked);
        menu.Items.Add(positionItem);

        var sizeItem = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.LockSize"),
            Icon = new FontIcon { Glyph = "\uE9CE" },
            IsChecked = isSizeLocked
        };
        sizeItem.Click += (_, _) => setSizeLocked(sizeItem.IsChecked);
        menu.Items.Add(sizeItem);

        // Scope hint (feedback 438): locking reads as "pin everything" to
        // users, while it only gates dragging/resizing. Disabled items don't
        // raise clicks but still render text — same pattern as the offline
        // display row in the screen menu.
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Lock.Hint"),
            IsEnabled = false
        });

        return menu;
    }
}
