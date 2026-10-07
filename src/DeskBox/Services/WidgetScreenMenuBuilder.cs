using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Builds the per-widget "belong to screen" submenu. Mirrors the Windows 11
/// context: a radio group over 自动 / 跟随主屏 / one entry per attached monitor,
/// hidden entirely on single-monitor systems the way Windows hides commands
/// that cannot apply.
/// </summary>
internal static class WidgetScreenMenuBuilder
{
    private const string RadioGroupName = "widget-screen-binding";

    public static MenuFlyoutSubItem? TryCreate(
        LocalizationService localizationService,
        WidgetConfig config,
        Action<WidgetScreenBindingMode, string?> applyBinding)
    {
        IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();
        if (screens.Count <= 1)
        {
            return null;
        }

        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.ScreenBinding.Title"),
            Icon = new FontIcon { Glyph = "\uE7F4" }
        };

        menu.Items.Add(CreateRadioItem(
            localizationService.T("Widget.ScreenBinding.Auto"),
            config.ScreenBindingMode == WidgetScreenBindingMode.Unbound,
            () => applyBinding(WidgetScreenBindingMode.Unbound, null)));

        menu.Items.Add(CreateRadioItem(
            localizationService.T("Widget.ScreenBinding.FollowPrimary"),
            config.ScreenBindingMode == WidgetScreenBindingMode.FollowPrimary,
            () => applyBinding(WidgetScreenBindingMode.FollowPrimary, null)));

        foreach (WidgetScreenInfo screen in screens)
        {
            bool isBound = config.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                string.Equals(
                    config.BoundScreenId?.Trim(),
                    screen.StableId.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            string text = localizationService.Format(
                "Widget.ScreenBinding.MonitorFormat",
                screen.Number,
                screen.PhysicalSizeText);
            if (screen.IsPrimary)
            {
                text += localizationService.T("Widget.ScreenBinding.PrimarySuffix");
            }

            string stableId = screen.StableId;
            menu.Items.Add(CreateRadioItem(
                text,
                isBound,
                () => applyBinding(WidgetScreenBindingMode.Pinned, stableId)));
        }

        return menu;
    }

    private static RadioMenuFlyoutItem CreateRadioItem(
        string text,
        bool isChecked,
        Action apply)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = RadioGroupName,
            IsChecked = isChecked
        };
        item.Click += (_, _) => apply();
        return item;
    }
}
