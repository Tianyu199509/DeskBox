using DeskBox.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Controls;

/// <summary>
/// Caches SolidColorBrush instances for Quick Capture paper/sticky presets across
/// light and dark themes so virtualized list scrolling never allocates per-row brushes
/// and material palette swatches stay legible in Dark theme.
/// </summary>
internal static class QuickCaptureMaterialBrushCache
{
    private static readonly Dictionary<(bool IsDark, QuickCaptureAppearancePreset Preset), SolidColorBrush> s_surfaceCache = [];
    private static readonly Dictionary<(bool IsDark, QuickCaptureAppearancePreset Preset), SolidColorBrush> s_swatchCache = [];

    public static SolidColorBrush? GetSurfaceBrush(QuickCaptureAppearancePreset preset, bool isDarkTheme)
    {
        if (preset == QuickCaptureAppearancePreset.Default)
        {
            return null;
        }

        var key = (isDarkTheme, preset);
        if (s_surfaceCache.TryGetValue(key, out SolidColorBrush? cached))
        {
            return cached;
        }

        Color color = preset switch
        {
            QuickCaptureAppearancePreset.Paper => isDarkTheme
                ? Color.FromArgb(0xB8, 0x3A, 0x36, 0x30)
                : Color.FromArgb(0xEC, 0xFA, 0xF5, 0xEA),
            QuickCaptureAppearancePreset.StickyYellow => isDarkTheme
                ? Color.FromArgb(0xB8, 0x4A, 0x40, 0x25)
                : Color.FromArgb(0xEC, 0xFF, 0xF0, 0xB3),
            QuickCaptureAppearancePreset.Rose => isDarkTheme
                ? Color.FromArgb(0xB8, 0x47, 0x2E, 0x38)
                : Color.FromArgb(0xEC, 0xFC, 0xE3, 0xEA),
            QuickCaptureAppearancePreset.Mint => isDarkTheme
                ? Color.FromArgb(0xB8, 0x28, 0x42, 0x35)
                : Color.FromArgb(0xEC, 0xDD, 0xF3, 0xE3),
            QuickCaptureAppearancePreset.MistBlue => isDarkTheme
                ? Color.FromArgb(0xB8, 0x2B, 0x3D, 0x53)
                : Color.FromArgb(0xEC, 0xDF, 0xEC, 0xF8),
            _ => Color.FromArgb(0x00, 0x00, 0x00, 0x00)
        };

        var brush = new SolidColorBrush(color);
        s_surfaceCache[key] = brush;
        return brush;
    }

    public static SolidColorBrush? GetSwatchBrush(QuickCaptureAppearancePreset preset, bool isDarkTheme)
    {
        if (preset == QuickCaptureAppearancePreset.Default)
        {
            return null;
        }

        var key = (isDarkTheme, preset);
        if (s_swatchCache.TryGetValue(key, out SolidColorBrush? cached))
        {
            return cached;
        }

        Color color = preset switch
        {
            QuickCaptureAppearancePreset.Paper => isDarkTheme
                ? Color.FromArgb(0xFF, 0x62, 0x5A, 0x4E)
                : Color.FromArgb(0xFF, 0xF4, 0xEE, 0xE2),
            QuickCaptureAppearancePreset.StickyYellow => isDarkTheme
                ? Color.FromArgb(0xFF, 0x84, 0x70, 0x38)
                : Color.FromArgb(0xFF, 0xFF, 0xD9, 0x6A),
            QuickCaptureAppearancePreset.Rose => isDarkTheme
                ? Color.FromArgb(0xFF, 0x7E, 0x4A, 0x5C)
                : Color.FromArgb(0xFF, 0xF2, 0xA7, 0xB8),
            QuickCaptureAppearancePreset.Mint => isDarkTheme
                ? Color.FromArgb(0xFF, 0x43, 0x72, 0x58)
                : Color.FromArgb(0xFF, 0xA8, 0xD8, 0xB5),
            QuickCaptureAppearancePreset.MistBlue => isDarkTheme
                ? Color.FromArgb(0xFF, 0x46, 0x64, 0x86)
                : Color.FromArgb(0xFF, 0xA9, 0xC7, 0xE8),
            _ => Color.FromArgb(0x00, 0x00, 0x00, 0x00)
        };

        var brush = new SolidColorBrush(color);
        s_swatchCache[key] = brush;
        return brush;
    }
}
