using DeskBox.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Controls;

/// <summary>
/// Resolves theme-aware paper/sticky colors and provides a window/surface-local
/// <see cref="SolidColorBrush"/> cache so virtualized list scrolling never allocates
/// per-row brushes AND separate WinUI <c>Window</c> instances (e.g. Popout vs Widget)
/// never share XAML <see cref="SolidColorBrush"/> DependencyObjects across window teardown.
/// </summary>
internal sealed class QuickCaptureMaterialBrushCache
{
    private readonly Dictionary<(bool IsDark, QuickCaptureAppearancePreset Preset), SolidColorBrush> _surfaceBrushes = [];
    private readonly Dictionary<(bool IsDark, QuickCaptureAppearancePreset Preset), SolidColorBrush> _swatchBrushes = [];

    public SolidColorBrush? GetSurfaceBrush(QuickCaptureAppearancePreset preset, bool isDarkTheme)
    {
        if (!TryGetSurfaceColor(preset, isDarkTheme, out Color color))
        {
            return null;
        }

        var key = (isDarkTheme, preset);
        if (!_surfaceBrushes.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            _surfaceBrushes[key] = brush;
        }

        return brush;
    }

    public SolidColorBrush? GetSwatchBrush(QuickCaptureAppearancePreset preset, bool isDarkTheme)
    {
        if (!TryGetSwatchColor(preset, isDarkTheme, out Color color))
        {
            return null;
        }

        var key = (isDarkTheme, preset);
        if (!_swatchBrushes.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            _swatchBrushes[key] = brush;
        }

        return brush;
    }

    public void Clear()
    {
        _surfaceBrushes.Clear();
        _swatchBrushes.Clear();
    }

    public static bool TryGetSurfaceColor(
        QuickCaptureAppearancePreset preset,
        bool isDarkTheme,
        out Color color)
    {
        switch (preset)
        {
            case QuickCaptureAppearancePreset.Paper:
                color = isDarkTheme
                    ? Color.FromArgb(0xB8, 0x3A, 0x36, 0x30)
                    : Color.FromArgb(0xEC, 0xFA, 0xF5, 0xEA);
                return true;
            case QuickCaptureAppearancePreset.StickyYellow:
                color = isDarkTheme
                    ? Color.FromArgb(0xB8, 0x4A, 0x40, 0x25)
                    : Color.FromArgb(0xEC, 0xFF, 0xF0, 0xB3);
                return true;
            case QuickCaptureAppearancePreset.Rose:
                color = isDarkTheme
                    ? Color.FromArgb(0xB8, 0x47, 0x2E, 0x38)
                    : Color.FromArgb(0xEC, 0xFC, 0xE3, 0xEA);
                return true;
            case QuickCaptureAppearancePreset.Mint:
                color = isDarkTheme
                    ? Color.FromArgb(0xB8, 0x28, 0x42, 0x35)
                    : Color.FromArgb(0xEC, 0xDD, 0xF3, 0xE3);
                return true;
            case QuickCaptureAppearancePreset.MistBlue:
                color = isDarkTheme
                    ? Color.FromArgb(0xB8, 0x2B, 0x3D, 0x53)
                    : Color.FromArgb(0xEC, 0xDF, 0xEC, 0xF8);
                return true;
            default:
                color = default;
                return false;
        }
    }

    public static bool TryGetSwatchColor(
        QuickCaptureAppearancePreset preset,
        bool isDarkTheme,
        out Color color)
    {
        switch (preset)
        {
            case QuickCaptureAppearancePreset.Paper:
                color = isDarkTheme
                    ? Color.FromArgb(0xFF, 0x62, 0x5A, 0x4E)
                    : Color.FromArgb(0xFF, 0xF4, 0xEE, 0xE2);
                return true;
            case QuickCaptureAppearancePreset.StickyYellow:
                color = isDarkTheme
                    ? Color.FromArgb(0xFF, 0x84, 0x70, 0x38)
                    : Color.FromArgb(0xFF, 0xFF, 0xD9, 0x6A);
                return true;
            case QuickCaptureAppearancePreset.Rose:
                color = isDarkTheme
                    ? Color.FromArgb(0xFF, 0x7E, 0x4A, 0x5C)
                    : Color.FromArgb(0xFF, 0xF2, 0xA7, 0xB8);
                return true;
            case QuickCaptureAppearancePreset.Mint:
                color = isDarkTheme
                    ? Color.FromArgb(0xFF, 0x43, 0x72, 0x58)
                    : Color.FromArgb(0xFF, 0xA8, 0xD8, 0xB5);
                return true;
            case QuickCaptureAppearancePreset.MistBlue:
                color = isDarkTheme
                    ? Color.FromArgb(0xFF, 0x46, 0x64, 0x86)
                    : Color.FromArgb(0xFF, 0xA9, 0xC7, 0xE8);
                return true;
            default:
                color = default;
                return false;
        }
    }
}
