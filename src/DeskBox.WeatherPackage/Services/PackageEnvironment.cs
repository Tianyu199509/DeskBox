using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
using System.Text.Json;
using System.Globalization;

namespace DeskBox.WeatherPackage.Services;

internal static class PackageEnvironment
{
    public const double MinSystemTextScaleFactor = 1;
    internal static double CornerRadius { get; set; } = 12;
    internal static double TextSize { get; set; } = WeatherSettingsContext.DefaultTextSize;
    internal static bool AllowAnimations { get; set; } = true;
    internal static bool AllowAutomaticRefresh { get; set; } = true;
    internal static Color Accent { get; set; } = Color.FromArgb(255, 31, 111, 173);
    internal static ElementTheme Theme { get; set; } = ElementTheme.Default;
    internal static void Apply(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        if (value.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String)
            Theme = theme.GetString() switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        if (value.TryGetProperty("accent", out var accent) && accent.ValueKind == JsonValueKind.String &&
            accent.GetString() is { Length: 9 } hex && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
            Accent = Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        if (value.TryGetProperty("textSize", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetDouble(out double text)) TextSize = WeatherSettingsContext.NormalizeTextSize(text);
        if (value.TryGetProperty("cornerRadius", out var radius) && radius.ValueKind == JsonValueKind.Number && radius.TryGetDouble(out double corner) && double.IsFinite(corner)) CornerRadius = Math.Clamp(corner, 0, 64);
        if (value.TryGetProperty("allowAnimations", out var motion) && motion.ValueKind is JsonValueKind.True or JsonValueKind.False) AllowAnimations = motion.GetBoolean();
    }
    internal static bool ShouldAnimate
    {
        get { try { return AllowAnimations && new UISettings().AnimationsEnabled; } catch { return false; } }
    }
    public static double NormalizeSystemTextScaleFactor(double value) => double.IsFinite(value) ? Math.Clamp(value, 1, 2.25) : 1;
    public static double ResolveSystemTextScaleFactor()
    {
        try { return NormalizeSystemTextScaleFactor(new UISettings().TextScaleFactor); } catch { return 1; }
    }
}
