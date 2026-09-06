using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

public sealed record WidgetTitleBarMetrics(
    double TitleIconSize,
    double TitleTextSize,
    double ActionButtonSize,
    double ActionIconSize,
    GridLength RowHeight,
    Thickness InnerTitlePadding,
    bool IsCompact);

public static class WidgetTitleBarMetricsCalculator
{
    public const double MinimumTitleContentHeight = 32;

    private const double FluentActionIconNativeSize = 20;
    private const double FluentActionIconVisualScale = 0.7;
    private const double CompactActionIconVisualScale = 0.7;

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
            : Math.Clamp(Math.Round(titleTextSize) - 1, 12, 18);

        double buttonSize = compact
            ? Math.Clamp(titleIconSize + 10, 22, 28)
            : Math.Clamp(titleIconSize + 14, 24, 34);
        double actionIconSize = compact
            ? Math.Clamp(titleIconSize - 2, 9, 13)
            : Math.Clamp(titleIconSize - 3, 10, 15);
        Thickness outerPadding = CreateOuterPadding(chromeMode);
        // Reserve one caption line plus the native tab's vertical padding and
        // stroke, so changing navigation style cannot enlarge an Auto row.
        double contentHeight = Math.Max(
            MinimumTitleContentHeight,
            Math.Ceiling(titleTextSize * 1.5) + 8);
        var rowHeight = new GridLength(
            Math.Max(contentHeight, buttonSize) + outerPadding.Top + outerPadding.Bottom);
        var innerPadding = includeInnerPadding
            ? CreateInnerPadding(titleIconSize, compact)
            : new Thickness(0);

        return new WidgetTitleBarMetrics(
            titleIconSize,
            titleTextSize,
            buttonSize,
            actionIconSize,
            rowHeight,
            innerPadding,
            compact);
    }

    public static void ApplyActionButton(Button button, WidgetTitleBarMetrics metrics)
    {
        button.Width = metrics.ActionButtonSize;
        button.Height = metrics.ActionButtonSize;
        button.MinWidth = metrics.ActionButtonSize;
        button.MinHeight = metrics.ActionButtonSize;
    }

    public static void ApplyActionIcon(FrameworkElement icon, WidgetTitleBarMetrics metrics)
    {
        bool compact = metrics.IsCompact;
        double visualScale = compact
            ? FluentActionIconVisualScale * CompactActionIconVisualScale
            : FluentActionIconVisualScale * 0.9;
        double targetSize = Math.Clamp(
            Math.Round(Math.Min(FluentActionIconNativeSize, metrics.ActionButtonSize - 4) * visualScale),
            compact ? 10 : 12,
            FluentActionIconNativeSize);

        if (icon is FontIcon fontIcon)
        {
            fontIcon.FontSize = targetSize;
            return;
        }

        if (icon is PathIcon)
        {
            icon.Width = targetSize;
            icon.Height = targetSize;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.RenderTransform = null;
            return;
        }

        icon.Width = targetSize;
        icon.Height = targetSize;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.RenderTransform = null;
    }

    public static Thickness CreateOuterPadding(WidgetChromeMode chromeMode)
    {
        return chromeMode == WidgetChromeMode.Compact
            ? new Thickness(12, 2, 10, 2)
            : new Thickness(14, 4, 12, 4);
    }

    private static Thickness CreateInnerPadding(double titleIconSize, bool compact)
    {
        double horizontal = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.72 : 0.9)), 8, 16);
        double top = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.32 : 0.5)), 3, 10);
        double bottom = Math.Clamp(Math.Round(titleIconSize * (compact ? 0.28 : 0.35)), 3, 8);
        return new Thickness(horizontal, top, horizontal - 2, bottom);
    }
}
