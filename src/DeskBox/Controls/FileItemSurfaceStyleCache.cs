using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum FileItemSurfaceVisualState
{
    Normal,
    Hover,
    Pressed,
    DropTarget
}

/// <summary>
/// Shared, cached visual styling for file-item surfaces.
/// Hosts provide the theme and selection state; the cache keeps brush allocation
/// out of the pointer-event hot path for both standalone and grouped widgets.
/// </summary>
public sealed class FileItemSurfaceStyleCache
{
    private SolidColorBrush? _normalSurfaceBrush;
    private SolidColorBrush? _selectedSurfaceBrush;
    private SolidColorBrush? _hoverSurfaceBrush;
    private SolidColorBrush? _selectedHoverSurfaceBrush;
    private SolidColorBrush? _normalBorderBrush;
    private SolidColorBrush? _selectedBorderBrush;
    private bool? _isDark;

    // Selection reads as a pointer state, not semantic emphasis, so the ring
    // uses the neutral line token (DeskBoxNeutralLineBrush) instead of accent,
    // staying in the same language as the marquee selection rectangle.
    private static readonly Thickness SelectedBorderThickness = new(1);

    public void Apply(
        Border border,
        FileItemSurfaceVisualState state,
        ElementTheme theme,
        bool isSelected,
        bool isCut,
        bool isDropTarget = false)
    {
        bool dark = theme == ElementTheme.Dark;
        EnsureBrushes(dark);

        // Every drop target - a folder to import into, a stack to add to, a
        // shortcut to open with - reads as a neutral hover surface with no
        // border, the same treatment a tile shows under the pointer. Only the
        // user's own tile states (hover, press, selection) drive the background
        // here; the accent color no longer enters the drop visual.
        bool hovered = state is
            FileItemSurfaceVisualState.Hover or
            FileItemSurfaceVisualState.Pressed ||
            isDropTarget;
        border.Background = hovered
            ? isSelected
                ? _selectedHoverSurfaceBrush
                : _hoverSurfaceBrush
            : state == FileItemSurfaceVisualState.Normal && isSelected
                ? _selectedSurfaceBrush
                : _normalSurfaceBrush;
        // Thickness is constant so toggling selection only swaps the brush —
        // changing thickness per state would re-measure every tile in batch
        // selections.
        border.BorderThickness = SelectedBorderThickness;
        border.BorderBrush = isSelected ? _selectedBorderBrush : _normalBorderBrush;
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void EnsureBrushes(bool isDark)
    {
        if (_normalSurfaceBrush is not null &&
            _isDark == isDark)
        {
            return;
        }

        _isDark = isDark;

        Windows.UI.Color selectedBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Normal,
            isSelected: true);
        Windows.UI.Color hoverBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Hover,
            isSelected: false);
        Windows.UI.Color selectedHoverBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Hover,
            isSelected: true);

        _normalSurfaceBrush = UpdateBrush(_normalSurfaceBrush, Colors.Transparent);
        _selectedSurfaceBrush = UpdateBrush(_selectedSurfaceBrush, selectedBackground);
        _hoverSurfaceBrush = UpdateBrush(_hoverSurfaceBrush, hoverBackground);
        _selectedHoverSurfaceBrush = UpdateBrush(_selectedHoverSurfaceBrush, selectedHoverBackground);
        _normalBorderBrush = UpdateBrush(_normalBorderBrush, Colors.Transparent);
        // Mirrors DeskBoxNeutralLineBrush (App.xaml): #72000000 light / #8BFFFFFF dark.
        _selectedBorderBrush = UpdateBrush(
            _selectedBorderBrush,
            isDark
                ? Windows.UI.Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x72, 0x00, 0x00, 0x00));
    }

    private static SolidColorBrush UpdateBrush(SolidColorBrush? brush, Windows.UI.Color color)
    {
        if (brush is null)
        {
            return new SolidColorBrush(color);
        }

        brush.Color = color;
        return brush;
    }

    internal static Windows.UI.Color GetNeutralStateLayer(
        bool isDark,
        FileItemSurfaceVisualState state,
        bool isSelected)
    {
        byte alpha = (state, isSelected, isDark) switch
        {
            (FileItemSurfaceVisualState.Hover, true, true) => 0x38,
            (FileItemSurfaceVisualState.Hover, true, false) => 0x2D,
            (FileItemSurfaceVisualState.Pressed, true, true) => 0x38,
            (FileItemSurfaceVisualState.Pressed, true, false) => 0x2D,
            (_, true, true) => 0x28,
            (_, true, false) => 0x20,
            (FileItemSurfaceVisualState.Pressed, false, true) => 0x20,
            (FileItemSurfaceVisualState.Pressed, false, false) => 0x18,
            (FileItemSurfaceVisualState.Hover, false, true) => 0x14,
            (FileItemSurfaceVisualState.Hover, false, false) => 0x0F,
            _ => 0x00
        };
        byte channel = isDark ? (byte)0xFF : (byte)0x00;

        // Keep this policy path usable by headless tests and non-UI callers.
        // Microsoft.UI.Colors and Microsoft.UI.ColorHelper are WinRT statics;
        // resolving them requires Windows App SDK registration on the process.
        return Windows.UI.Color.FromArgb(alpha, channel, channel, channel);
    }

    private static Windows.UI.Color BlendColors(
        Windows.UI.Color from,
        Windows.UI.Color to,
        double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Blend(byte first, byte second, double mix) =>
            (byte)Math.Clamp(
                Math.Round(first + ((second - first) * mix)),
                0,
                255);

        return ColorHelper.FromArgb(
            Blend(from.A, to.A, amount),
            Blend(from.R, to.R, amount),
            Blend(from.G, to.G, amount),
            Blend(from.B, to.B, amount));
    }
}
