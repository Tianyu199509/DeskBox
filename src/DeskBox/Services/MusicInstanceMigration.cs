using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.Services;

// Transitional host-side adapter. The package never references this type.
internal sealed class MusicInstanceMigration : ILegacyInstanceMigration
{
    internal const string PackageId = "deskbox.music";
    internal static MusicInstanceMigration Instance { get; } = new();
    public string DataFileName => "music-settings.json";
    public string? ResolveLegacyContent(string dataDirectory, string instanceId)
    {
        MusicWidgetSettings music = string.Equals(
            dataDirectory,
            DeskBoxDataPathService.Current.DataDirectory,
            StringComparison.OrdinalIgnoreCase)
            ? MusicSettingsStore.Current.Load()
            : ReadLegacySettings(dataDirectory);
        AppSettings settings = App.Current.SettingsService.Settings;
        ThemeService theme = App.Current.ThemeService;
        var performance = PerformanceSettingsPolicy.Resolve(settings);
        Color accent = theme.GetEffectiveAccentColor();
        bool dark = theme.CurrentTheme == ElementTheme.Dark;
        if (theme.CurrentTheme == ElementTheme.Default)
        {
            var foreground = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            dark = foreground.R + foreground.G + foreground.B > 384;
        }
        string corner = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(settings.WidgetCornerPreference);
        double radius = corner switch { "Square" => 0, "Small" => 6, "Round" => 10, _ => 8 };
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("music"); WriteMusic(writer, music);
            writer.WriteString("locale", App.Current.LocalizationService.CurrentCultureName);
            writer.WriteNumber("textSize", SettingsService.NormalizeTextSize(settings.TextSize));
            writer.WriteNumber("cornerRadius", radius);
            writer.WriteString("accent", $"#{accent.A:X2}{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            writer.WriteBoolean("usesSystemAccentColor", theme.UsesSystemAccentColor);
            writer.WriteBoolean("isDark", dark);
            writer.WriteBoolean("allowSystemAnimations", WindowsCompatibilityService.ShouldAnimate);
            writer.WriteBoolean("allowTextMarqueeAnimations", performance.AllowTextMarqueeAnimations);
            writer.WriteBoolean("allowVinylRotationAnimations", performance.AllowVinylRotationAnimations);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    internal static MusicWidgetSettings ReadLegacySettings(string dataDirectory)
    {
        foreach (string path in new[] {
            Path.Combine(dataDirectory, "music", "settings.json"),
            Path.Combine(dataDirectory, "music", "settings.json.bak"),
            Path.Combine(dataDirectory, "settings.json"),
            Path.Combine(dataDirectory, "settings.json.bak") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                bool legacy = Path.GetDirectoryName(path) == dataDirectory;
                if (TryParse(doc.RootElement, legacy, false, out var value)) return value;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { App.LogVerbose($"[MusicPackage] settings recovery candidate rejected: {error.Message}"); }
        }
        return new();
    }
    internal static bool TryParse(JsonElement root, bool legacy, bool patch, out MusicWidgetSettings settings)
    {
        settings = new();
        if (root.ValueKind != JsonValueKind.Object) return false;
        string backdrop = legacy ? "musicUseArtworkBackdrop" : "useArtworkBackdrop";
        string motion = legacy ? "musicEnableCoverHoverMotion" : "enableCoverHoverMotion";
        string mode = legacy ? "musicDisplayMode" : "displayMode";
        int known = 0;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name == backdrop || p.Name == motion)
            {
                if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                if (p.Name == backdrop) settings.UseArtworkBackdrop = p.Value.GetBoolean();
                else settings.EnableCoverHoverMotion = p.Value.GetBoolean();
                known++;
            }
            else if (p.Name == mode)
            {
                if (p.Value.ValueKind != JsonValueKind.String) return false;
                string? name = p.Value.GetString();
                if (name is not ("Auto" or "Cover" or "Controls" or "RecordVertical" or "RecordHorizontal")) return false;
                settings.DisplayMode = name; known++;
            }
            else if (patch) return false;
        }
        return known > 0;
    }
    internal static void WriteMusic(Utf8JsonWriter writer, MusicWidgetSettings music)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("useArtworkBackdrop", music.UseArtworkBackdrop);
        writer.WriteBoolean("enableCoverHoverMotion", music.EnableCoverHoverMotion);
        writer.WriteString("displayMode", music.DisplayMode);
        writer.WriteEndObject();
    }
    public bool TryApplyPatch(string instanceId, string jsonPatch)
    {
        try
        {
            var app = App.Current;
            if (!app.SettingsService.Settings.Widgets.Any(w => w.Id == instanceId && w.WidgetKind == WidgetKind.Music)) return false;
            using var document = JsonDocument.Parse(jsonPatch);
            if (!TryParse(document.RootElement, false, true, out var parsed)) return false;
            bool hasBackdrop = document.RootElement.TryGetProperty("useArtworkBackdrop", out _);
            bool hasMotion = document.RootElement.TryGetProperty("enableCoverHoverMotion", out _);
            bool hasMode = document.RootElement.TryGetProperty("displayMode", out _);
            MusicSettingsStore.Current.Update(current =>
            {
                if (hasBackdrop) current.UseArtworkBackdrop = parsed.UseArtworkBackdrop;
                if (hasMotion) current.EnableCoverHoverMotion = parsed.EnableCoverHoverMotion;
                if (hasMode) current.DisplayMode = parsed.DisplayMode;
            });
            if (hasBackdrop) app.SettingsService.Settings.MusicUseArtworkBackdrop = parsed.UseArtworkBackdrop;
            if (hasMotion) app.SettingsService.Settings.MusicEnableCoverHoverMotion = parsed.EnableCoverHoverMotion;
            if (hasMode) app.SettingsService.Settings.MusicDisplayMode = parsed.DisplayMode;
            app.SettingsService.SaveDebounced();
            return true;
        }
        catch (Exception error) { App.Log($"[MusicPackage] settings patch rejected: {error.Message}"); return false; }
    }
}
