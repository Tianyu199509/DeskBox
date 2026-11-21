using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// Applies the native Fluent critical color to widget-closing actions. The
/// theme resource is softer than pure red and automatically follows light,
/// dark, and high-contrast modes.
/// </summary>
internal static class WidgetDangerActionStyle
{
    internal const string CriticalBrushResourceKey =
        "SystemFillColorCriticalBrush";

    internal static void Apply(MenuFlyoutItem item, DependencyObject? themeScope = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Brush brush = ResolveBrush(themeScope ?? item);
        item.Foreground = brush;
        if (item.Icon is FontIcon icon)
        {
            icon.Foreground = brush;
        }
    }

    internal static Brush ResolveBrush(DependencyObject? themeScope = null)
    {
        if (NeutralInteractionBrush.ResolveThemedResource(CriticalBrushResourceKey, themeScope) is { } brush)
        {
            return brush;
        }

        // Fluent critical foreground fallback (#FF99A4 in dark, #C42B1C in light).
        return NeutralInteractionBrush.IsDarkTheme(themeScope)
            ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFF, 0x99, 0xA4))
            : new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
    }
}
