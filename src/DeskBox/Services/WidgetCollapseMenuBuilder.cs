using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetCollapseMenuBuilder
{
    public static MenuFlyoutSubItem Create(
        WidgetConfig config,
        LocalizationService localizationService,
        Action<WidgetCollapseBehavior> applyBehavior,
        Action resetCompactWidth)
    {
        WidgetCollapseBehavior selectedBehavior = WidgetCollapseBehaviorNames.GetOverride(config);
        var subItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.CollapseBehavior.Title"),
            Icon = new FontIcon { Glyph = "\uE73F" }
        };

        foreach (WidgetCollapseBehavior behavior in new[]
                 {
                     WidgetCollapseBehavior.System,
                     WidgetCollapseBehavior.Expanded,
                     WidgetCollapseBehavior.Click,
                     WidgetCollapseBehavior.Smart
                 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(GetTextKey(behavior)),
                IsChecked = selectedBehavior == behavior
            };
            item.Click += (_, _) => applyBehavior(behavior);
            subItem.Items.Add(item);
        }

        subItem.Items.Add(new MenuFlyoutSeparator());
        var resetWidthItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Compact.RestoreAutomaticWidth"),
            Icon = new FontIcon { Glyph = "\uE8A7" },
            IsEnabled = config.CompactWidth is not null
        };
        resetWidthItem.Click += (_, _) => resetCompactWidth();
        subItem.Items.Add(resetWidthItem);

        return subItem;
    }

    private static string GetTextKey(WidgetCollapseBehavior behavior)
    {
        return behavior switch
        {
            WidgetCollapseBehavior.System => "Widget.CollapseBehavior.System",
            WidgetCollapseBehavior.Expanded => "Widget.CollapseBehavior.Expanded",
            WidgetCollapseBehavior.Smart => "Widget.CollapseBehavior.Smart",
            _ => "Widget.CollapseBehavior.Click"
        };
    }
}
