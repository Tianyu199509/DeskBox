using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public readonly record struct WidgetSegmentedMetrics(
    double TextSize,
    double Height,
    Thickness Padding)
{
    public static WidgetSegmentedMetrics Create(double textSize)
    {
        double normalizedTextSize = Math.Max(SettingsService.MinTextSize, textSize);
        double height = Math.Clamp(Math.Round(normalizedTextSize + 17), 28, 34);
        double horizontal = Math.Clamp(Math.Round(normalizedTextSize * 0.55), 6, 8);
        double vertical = Math.Clamp(Math.Round(normalizedTextSize * 0.18), 2, 4);

        return new WidgetSegmentedMetrics(
            normalizedTextSize,
            height,
            new Thickness(horizontal, vertical, horizontal, vertical + 1));
    }
}
