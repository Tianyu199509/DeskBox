using System.Text.Json;
using DeskBox.MusicPackage.Models;
using Windows.UI;
namespace DeskBox.MusicPackage.Services;

public static class MusicColors
{
    public static Color DefaultAccent => Color.FromArgb(255, 0, 120, 212);
}
public sealed class MusicEnvironmentContext
{
    public const double DefaultTextSize = 11.5, MinTextSize = 10, MaxTextSize = 16;
    public const string MusicDisplayModeAuto = "Auto", MusicDisplayModeCover = "Cover",
        MusicDisplayModeControls = "Controls", MusicDisplayModeRecordVertical = "RecordVertical",
        MusicDisplayModeRecordHorizontal = "RecordHorizontal";
    public MusicEnvironmentSnapshot Settings { get; private set; } = new();
    public event Action? SettingsChanged;
    public void Apply(MusicEnvironmentSnapshot snapshot)
    {
        if (Settings == snapshot) return;
        Settings = snapshot;
        SettingsChanged?.Invoke();
    }
    public static double NormalizeTextSize(double value) => double.IsFinite(value)
        ? Math.Clamp(value, MinTextSize, MaxTextSize) : DefaultTextSize;
    public static string NormalizeMusicDisplayMode(string? value) => value?.Trim() switch
    {
        MusicDisplayModeCover => MusicDisplayModeCover,
        MusicDisplayModeControls => MusicDisplayModeControls,
        MusicDisplayModeRecordVertical => MusicDisplayModeRecordVertical,
        MusicDisplayModeRecordHorizontal => MusicDisplayModeRecordHorizontal,
        _ => MusicDisplayModeAuto
    };

    // A bounded value snapshot, not a deserialized host settings object.
    internal static MusicEnvironmentSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected music settings object.");
        var music = root.TryGetProperty("music", out var m) && m.ValueKind == JsonValueKind.Object ? m : root;
        static bool B(JsonElement e, string key, bool fallback) => e.TryGetProperty(key, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
        static double N(JsonElement e, string key, double fallback) => e.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && double.IsFinite(n) ? n : fallback;
        static string S(JsonElement e, string key, string fallback) => e.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        string accent = S(root, "accent", "#FF0078D4");
        Color color = MusicColors.DefaultAccent;
        if (accent.Length == 9 && accent[0] == '#' &&
            uint.TryParse(accent.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint argb))
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        return new()
        {
            Music = new() {
                UseArtworkBackdrop = B(music, "useArtworkBackdrop", true),
                EnableCoverHoverMotion = B(music, "enableCoverHoverMotion", true),
                DisplayMode = NormalizeMusicDisplayMode(S(music, "displayMode", "Auto"))
            },
            Locale = S(root, "locale", "en-US"),
            TextSize = NormalizeTextSize(N(root, "textSize", DefaultTextSize)),
            CornerRadius = Math.Clamp(N(root, "cornerRadius", 8), 0, 64),
            Accent = color,
            UsesSystemAccentColor = B(root, "usesSystemAccentColor", true),
            IsDark = B(root, "isDark", false),
            AllowSystemAnimations = B(root, "allowSystemAnimations", true),
            AllowTextMarqueeAnimations = B(root, "allowTextMarqueeAnimations", true),
            AllowVinylRotationAnimations = B(root, "allowVinylRotationAnimations", true)
        };
    }
}

