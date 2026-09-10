using System.Globalization;
using System.Text.Json;
using Windows.UI;

namespace DeskBox.GlancePackage.Services;

/// <summary>Value-only host tokens. Unknown/missing fields keep compatible defaults.</summary>
internal sealed record PackageAppearance(bool IsDark, Color Accent, string MaterialType,
    double MaterialOpacity, double MaterialIntensity)
{
    internal static PackageAppearance Default { get; } =
        new(true, Color.FromArgb(255, 0, 120, 212), "Mica", 0.8, 0.65);

    internal static PackageAppearance Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default;
            string? ReadString(string key) => root.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            double ReadNumber(string key, double fallback) => root.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
                    ? Math.Clamp(number, 0, 1) : fallback;
            string? hex = ReadString("accent");
            Color accent = Default.Accent;
            if (hex is { Length: 7 or 9 } && hex[0] == '#' &&
                uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                accent = Color.FromArgb(hex.Length == 7 ? (byte)255 : (byte)(argb >> 24),
                    (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            string material = ReadString("materialType") switch
            {
                "MicaAlt" => "MicaAlt", "Acrylic" => "Acrylic", "AcrylicBase" => "AcrylicBase", "Solid" => "Solid",
                _ => "Mica",
            };
            return new(ReadString("theme") != "Light", accent, material,
                ReadNumber("materialOpacity", Default.MaterialOpacity), ReadNumber("materialIntensity", Default.MaterialIntensity));
        }
        catch (JsonException) { return Default; }
    }
}
