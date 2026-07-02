using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public readonly record struct WidgetInputMetrics(
    double Height,
    double ButtonSize,
    double ActionIconSize,
    Thickness Padding)
{
    public static WidgetInputMetrics Create(double textSize)
    {
        double normalizedTextSize = Math.Max(SettingsService.MinTextSize, textSize);
        double height = Math.Clamp(Math.Round((normalizedTextSize + 30) * 0.7), 30, 34);
        double buttonSize = height;
        double actionIconSize = Math.Clamp(Math.Round(normalizedTextSize * 0.92), 11, 14);
        double horizontal = Math.Clamp(Math.Round(normalizedTextSize * 0.95), 11, 14);
        double vertical = Math.Clamp(Math.Round(normalizedTextSize * 0.42), 5, 7);

        return new WidgetInputMetrics(
            height,
            buttonSize,
            actionIconSize,
            new Thickness(horizontal, vertical, horizontal, vertical));
    }
}
