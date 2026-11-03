using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class GlanceWidgetContextMenuBuilder
{
    private static readonly GlanceDisplayElement[] DisplayElements =
    [
        GlanceDisplayElement.Time,
        GlanceDisplayElement.Seconds,
        GlanceDisplayElement.Date,
        GlanceDisplayElement.Year,
        GlanceDisplayElement.Weekday
    ];

    private static readonly GlanceStyle[] Styles =
    [
        GlanceStyle.PhotoClock,
        GlanceStyle.Editorial,
        GlanceStyle.TodayCard,
        GlanceStyle.WeekStrip,
        GlanceStyle.MonthCalendar
    ];

    public static void Append(
        MenuFlyout flyout,
        GlanceWidgetViewModel viewModel,
        LocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(localizationService);

        flyout.Items.Add(CreateStyleMenu(viewModel, localizationService));
        flyout.Items.Add(CreateDisplayMenu(viewModel, localizationService));
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    private static MenuFlyoutSubItem CreateDisplayMenu(
        GlanceWidgetViewModel viewModel,
        LocalizationService localizationService)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Glance.Display.Title"),
            Icon = new FontIcon { Glyph = "\uE7B3" }
        };

        foreach (GlanceDisplayElement element in DisplayElements)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = localizationService.T(GetDisplayTextKey(element)),
                IsChecked = GlanceWidgetSettingsPolicy.IsDisplayElementVisible(
                    viewModel.Settings,
                    element)
            };
            item.Click += (_, _) =>
                _ = RunAsync(() => viewModel.SetDisplayElementAsync(
                    element,
                    item.IsChecked));
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Style switcher: the quickest way to change the look without opening settings.</summary>
    private static MenuFlyoutSubItem CreateStyleMenu(
        GlanceWidgetViewModel viewModel,
        LocalizationService localizationService)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Glance.Style.Title"),
            Icon = new FontIcon { Glyph = "\uE80A" }
        };

        foreach (GlanceStyle style in Styles)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = localizationService.T(DeskBox.Controls.GlanceStylePreviewFactory.GetTextKey(style)),
                GroupName = "GlanceStyle",
                IsChecked = viewModel.Settings.Style == style
            };
            item.Click += (_, _) =>
                _ = RunAsync(() => viewModel.SetStyleAsync(style));
            menu.Items.Add(item);
        }

        return menu;
    }


    private static string GetDisplayTextKey(GlanceDisplayElement element) =>
        element switch
        {
            GlanceDisplayElement.Time => "Glance.Display.Time",
            GlanceDisplayElement.Seconds => "Glance.Display.Seconds",
            GlanceDisplayElement.Date => "Glance.Display.Date",
            GlanceDisplayElement.Year => "Glance.Display.Year",
            GlanceDisplayElement.Weekday => "Glance.Display.Weekday",
            GlanceDisplayElement.Calendar => "Glance.Display.Calendar",
            _ => "Glance.Display.Title"
        };


    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceContextMenu] Command failed: {ex}");
        }
    }
}
