using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

public sealed record WidgetTitleBarMetrics(
    double TitleIconSize,
    double TitleTextSize,
    double ActionButtonSize,
    double ActionIconSize,
    GridLength RowHeight,
    Thickness InnerTitlePadding);

public static class WidgetTitleBarMetricsCalculator
{
    public static WidgetTitleBarMetrics Create(
        double titleIconSize,
        double titleTextSize,
        bool includeInnerPadding,
        WidgetChromeMode chromeMode = WidgetChromeMode.Standard)
    {
        bool compact = chromeMode == WidgetChromeMode.Compact;
        titleIconSize = compact
            ? Math.Clamp(Math.Round(titleIconSize * 0.88), 10, 15)
            : Math.Clamp(Math.Round(titleIconSize), 11, 18);
        titleTextSize = compact
            ? Math.Clamp(Math.Round(titleTextSize), SettingsService.MinTextSize, SettingsService.MaxTextSize)
            : Math.Clamp(Math.Round(titleTextSize), 12, 18);

        double buttonSize = compact
            ? Math.Clamp(titleIconSize + 10, 22, 28)
            : Math.Clamp(titleIconSize + 14, 24, 34);
        double actionIconSize = compact
            ? Math.Clamp(titleIconSize - 2, 9, 13)
            : Math.Clamp(titleIconSize - 3, 10, 15);
        var rowHeight = compact
            ? new GridLength(Math.Clamp(titleIconSize + 22, 30, 36))
            : new GridLength(Math.Clamp(titleIconSize + 32, 40, 54));
        var innerPadding = includeInnerPadding
            ? CreateInnerPadding(titleIconSize, compact)
            : new Thickness(0);

        return new WidgetTitleBarMetrics(
            titleIconSize,
            titleTextSize,
            buttonSize,
            actionIconSize,
            rowHeight,
            innerPadding);
    }

    public static void ApplyActionButton(Button button, WidgetTitleBarMetrics metrics)
    {
        button.Width = metrics.ActionButtonSize;
        button.Height = metrics.ActionButtonSize;
        button.MinWidth = metrics.ActionButtonSize;
        button.MinHeight = metrics.ActionButtonSize;
    }

    public static void ApplyActionIcon(FontIcon icon, WidgetTitleBarMetrics metrics)
    {
        icon.FontSize = metrics.ActionIconSize;
    }

    public static Thickness CreateOuterPadding(WidgetChromeMode chromeMode)
    {
        return chromeMode == WidgetChromeMode.Compact
            ? new Thickness(12, 4, 10, 4)
            : new Thickness(14, 7, 12, 5);
    }

    private static Thickness CreateInnerPadding(double titleIconSize, bool compact)
    {
        double horizontal = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.72 : 0.9)), 8, 16);
        double top = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.32 : 0.5)), 3, 10);
        double bottom = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.28 : 0.35)), 3, 8);
        return new Thickness(horizontal, top, horizontal - 2, bottom);
    }
}
