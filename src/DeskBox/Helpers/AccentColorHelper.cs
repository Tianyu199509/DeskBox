using Windows.UI;

namespace DeskBox.Helpers;

public static class AccentColorHelper
{
    public const string DefaultAccentColorHex = "#0078D4";
    private static readonly Color s_defaultAccentColor = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

    public static Color DefaultAccentColor => s_defaultAccentColor;

    public static bool TryParseHex(string? value, out Color color)
    {
        color = s_defaultAccentColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        try
        {
            byte a = 0xFF;
            int offset = 0;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex[..2], 16);
                offset = 2;
            }

            byte r = Convert.ToByte(hex.Substring(offset, 2), 16);
            byte g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Color FromHex(string? value)
    {
        return TryParseHex(value, out var color) ? color : DefaultAccentColor;
    }

    public static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// Builds a conditioned check-control palette from the effective system or
    /// custom accent color. Overly bright, neon, or near-white accents are
    /// toned down in saturation and relative luminance so completed checkboxes
    /// feel calm and refined while keeping high contrast against the checkmark.
    /// </summary>
    public static AccentCheckPalette BuildTonedCheckPalette(Color accent, bool isDark)
    {
        RgbToHsl(accent, out double h, out double s, out double l);

        // Near-achromatic accents (pure white, light silver, pure black) map
        // to a calm slate-blue tone so checked boxes never become glaring white
        // bricks or vanish into a dark card.
        double conditionedH;
        double conditionedS;
        double conditionedL;

        if (s < 0.14)
        {
            conditionedH = 214.0;
            conditionedS = isDark ? 0.24 : 0.26;
            conditionedL = isDark ? 0.43 : 0.45;
        }
        else
        {
            conditionedH = h;

            // Soften neon saturation into a satin/Morandi range.
            double maxSoftSaturation = isDark ? 0.64 : 0.70;
            double saturationKnee = isDark ? 0.54 : 0.60;
            conditionedS = s <= saturationKnee
                ? Math.Max(s, 0.26)
                : Math.Min(maxSoftSaturation, saturationKnee + ((s - saturationKnee) * 0.28));

            // Yellow, lime, and cyan hues (40..195 deg) have much higher
            // perceptual brightness at the same HSL lightness; pull their
            // ceiling lower so bright yellow/green/cyan accents become warm
            // amber, sage, or deep teal instead of glaring neon.
            double brightHueWeight = CalculatePerceptualBrightHueWeight(conditionedH);
            double maxLightness = (isDark ? 0.48 : 0.50) - (0.09 * brightHueWeight);
            double minLightness = isDark ? 0.37 : 0.35;
            conditionedL = Math.Clamp(l, minLightness, maxLightness);
        }

        Color fill = HslToRgb(conditionedH, conditionedS, conditionedL);

        // Blend slightly with the surface plate so the badge sits naturally on
        // translucent glass/acrylic cards.
        Color surfaceAnchor = isDark
            ? Color.FromArgb(0xFF, 0x22, 0x26, 0x2E)
            : Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9);
        fill = Blend(fill, surfaceAnchor, isDark ? 0.12 : 0.06);

        // Strict WCAG relative-luminance ceiling so a bright theme color is
        // guaranteed to be toned down and the crisp white vector checkmark
        // always maintains >= 3.6:1 contrast.
        double brightWeight = CalculatePerceptualBrightHueWeight(conditionedH);
        double maxLuminance = (isDark ? 0.22 : 0.23) - (0.04 * brightWeight);
        Color darkAnchor = Color.FromArgb(0xFF, 0x1A, 0x1E, 0x26);
        int guardIterations = 0;
        while (GetRelativeLuminance(fill) > maxLuminance && guardIterations++ < 16)
        {
            fill = Blend(fill, darkAnchor, 0.14);
        }

        // Minimum luminance floor in dark mode so ultra-dark accents stay
        // clearly distinguishable as a solid filled badge.
        if (isDark && GetRelativeLuminance(fill) < 0.085)
        {
            Color lightAnchor = HslToRgb(conditionedH, Math.Max(conditionedS, 0.28), 0.52);
            while (GetRelativeLuminance(fill) < 0.085 && guardIterations++ < 28)
            {
                fill = Blend(fill, lightAnchor, 0.15);
            }
        }

        Color fillHover = Blend(
            fill,
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            isDark ? 0.12 : 0.10);
        Color fillPressed = Blend(
            fill,
            Color.FromArgb(0xFF, 0x10, 0x13, 0x18),
            isDark ? 0.16 : 0.14);

        Color neutralBorder = isDark
            ? Color.FromArgb(0xFF, 0x8A, 0x94, 0xA6)
            : Color.FromArgb(0xFF, 0x64, 0x74, 0x8B);
        Color strokeHover = Blend(fillHover, neutralBorder, 0.35);
        strokeHover = Color.FromArgb(isDark ? (byte)0xCC : (byte)0xB8, strokeHover.R, strokeHover.G, strokeHover.B);

        Color glyph = GetRelativeLuminance(fill) > 0.42
            ? Color.FromArgb(0xFF, 0x16, 0x1A, 0x22)
            : Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC);

        return new AccentCheckPalette(
            FillChecked: fill,
            FillCheckedPointerOver: fillHover,
            FillCheckedPressed: fillPressed,
            StrokePointerOver: strokeHover,
            GlyphForeground: glyph);
    }

    public static double GetRelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double sRgb = channel / 255.0;
            return sRgb <= 0.03928
                ? sRgb / 12.92
                : Math.Pow((sRgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) +
               (0.7152 * Linearize(color.G)) +
               (0.0722 * Linearize(color.B));
    }

    private static double CalculatePerceptualBrightHueWeight(double hueDegrees)
    {
        // Peaks across yellow -> lime -> cyan (40..195 deg).
        if (hueDegrees is < 30.0 or > 205.0)
        {
            return 0.0;
        }

        if (hueDegrees is >= 50.0 and <= 165.0)
        {
            return 1.0;
        }

        return hueDegrees < 50.0
            ? (hueDegrees - 30.0) / 20.0
            : (205.0 - hueDegrees) / 40.0;
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            0xFF,
            (byte)Math.Clamp(Math.Round(from.R + ((to.R - from.R) * t)), 0, 255),
            (byte)Math.Clamp(Math.Round(from.G + ((to.G - from.G) * t)), 0, 255),
            (byte)Math.Clamp(Math.Round(from.B + ((to.B - from.B) * t)), 0, 255));
    }

    private static void RgbToHsl(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        l = (max + min) * 0.5;
        if (delta < 1e-6)
        {
            h = 0.0;
            s = 0.0;
            return;
        }

        s = l > 0.5
            ? delta / (2.0 - max - min)
            : delta / (max + min);

        if (max == r)
        {
            h = ((g - b) / delta) + (g < b ? 6.0 : 0.0);
        }
        else if (max == g)
        {
            h = ((b - r) / delta) + 2.0;
        }
        else
        {
            h = ((r - g) / delta) + 4.0;
        }

        h *= 60.0;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        l = Math.Clamp(l, 0.0, 1.0);

        double c = (1.0 - Math.Abs((2.0 * l) - 1.0)) * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = l - (c * 0.5);

        (double r1, double g1, double b1) = h switch
        {
            < 60.0 => (c, x, 0.0),
            < 120.0 => (x, c, 0.0),
            < 180.0 => (0.0, c, x),
            < 240.0 => (0.0, x, c),
            < 300.0 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromArgb(
            0xFF,
            (byte)Math.Clamp(Math.Round((r1 + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((g1 + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((b1 + m) * 255.0), 0, 255));
    }
}

public readonly record struct AccentCheckPalette(
    Color FillChecked,
    Color FillCheckedPointerOver,
    Color FillCheckedPressed,
    Color StrokePointerOver,
    Color GlyphForeground);
