namespace DeskBox.Services;

internal readonly record struct WidgetBorderVisuals(
    double Thickness,
    Windows.UI.Color BorderColor,
    Windows.UI.Color DividerColor);

/// <summary>
/// Resolves the configured widget border once for every surface that needs to
/// visually belong to a widget, including transient stack popovers.
/// </summary>
internal static class WidgetBorderVisualCalculator
{
    internal const double MaximumThickness = 1.6;

    public static WidgetBorderVisuals Resolve(
        string? borderStyle,
        string? colorMode,
        bool isDark,
        Windows.UI.Color accentColor)
    {
        var (thickness, alpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick =>
                (MaximumThickness, (byte)0x48),
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            _ => (0.8d, (byte)0x18)
        };

        if (colorMode == SettingsService.WidgetBorderColorModeNone)
        {
            thickness = 0;
            alpha = 0;
        }

        bool useAccent = colorMode ==
            SettingsService.WidgetBorderColorModeAccent;
        byte borderAlpha = useAccent
            ? (byte)Math.Clamp(Math.Round(alpha * 1.35), 0, 255)
            : alpha;
        byte red = useAccent
            ? accentColor.R
            : isDark ? (byte)0xFF : (byte)0x00;
        byte green = useAccent
            ? accentColor.G
            : isDark ? (byte)0xFF : (byte)0x00;
        byte blue = useAccent
            ? accentColor.B
            : isDark ? (byte)0xFF : (byte)0x00;
        var borderColor = Windows.UI.Color.FromArgb(
            borderAlpha,
            red,
            green,
            blue);
        var dividerColor = Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(
                Math.Round(borderAlpha * (isDark ? 0.66 : 0.42)),
                0,
                255),
            red,
            green,
            blue);
        return new WidgetBorderVisuals(
            thickness,
            borderColor,
            dividerColor);
    }
}
