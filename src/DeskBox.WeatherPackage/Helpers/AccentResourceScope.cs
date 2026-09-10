using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.WeatherPackage.Helpers;

/// <summary>
/// Applies the effective DeskBox accent to one window's visual tree without
/// mutating application-wide WinUI resources during startup.
/// </summary>
public static class AccentResourceScope
{
    public static void Apply(FrameworkElement target, Color accent)
    {
        ArgumentNullException.ThrowIfNull(target);

        ResourceDictionary resources = target.Resources;
        resources["SystemAccentColor"] = accent;
        SetBrush(resources, "AccentFillColorDefaultBrush", accent);
        SetBrush(resources, "AccentFillColorPrimaryBrush", accent);
        SetBrush(resources, "AccentTextFillColorPrimaryBrush", accent);
        SetBrush(resources, "SystemControlHighlightAccentBrush", accent);
        SetBrush(
            resources,
            "SystemAccentColorLight2Brush",
            Color.FromArgb(0x44, accent.R, accent.G, accent.B));
    }

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources.TryGetValue(key, out object? value) &&
            value is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }
}
