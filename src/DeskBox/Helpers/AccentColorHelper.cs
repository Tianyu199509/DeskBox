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
}
