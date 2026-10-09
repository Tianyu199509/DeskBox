using Windows.UI;

namespace DeskBox.Helpers;

/// <summary>
/// Picks the shadow edge for a text color, so palette modes and custom
/// foreground colors need no shadow configuration of their own.
///
/// The edge always takes the luminance pole opposite the text: light text
/// casts a dark, slightly dropped shadow (the desktop icon-label look); dark
/// text gets a light, non-directional halo (the map-label look). A dark
/// shadow under dark text only thickens and smears the glyphs, and a dropped
/// light copy reads as an engraved effect instead of a readability edge.
///
/// The polarity flips at the WCAG luminance crossover, the point where black
/// and white give the text the same contrast ratio:
/// (L + 0.05) / 0.05 = 1.05 / (L + 0.05)  =>  L = sqrt(1.05 * 0.05) - 0.05.
/// Even at the crossover the chosen edge keeps a 4.58:1 contrast with the
/// text, so mid-tone custom colors still get a usable edge.
/// </summary>
internal static class TextShadowPalette
{
    public const double PolarityLuminanceThreshold = 0.179;

    public static readonly TextShadowEdge DarkShadow = new(
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        BlurRadius: 3f,
        OffsetY: 1f,
        Opacity: 0.9f);

    public static readonly TextShadowEdge LightHalo = new(
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        BlurRadius: 3f,
        OffsetY: 0f,
        Opacity: 1f);

    public static TextShadowEdge Resolve(Color foreground) =>
        RelativeLuminance(foreground) >= PolarityLuminanceThreshold
            ? DarkShadow
            : LightHalo;

    public static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) +
               (0.7152 * Linearize(color.G)) +
               (0.0722 * Linearize(color.B));
    }
}

internal readonly record struct TextShadowEdge(
    Color Color,
    float BlurRadius,
    float OffsetY,
    float Opacity);
