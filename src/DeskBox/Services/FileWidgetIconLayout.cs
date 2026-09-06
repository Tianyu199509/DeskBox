using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>Shared icon geometry for desktop widgets and organization previews.</summary>
internal readonly record struct FileWidgetIconLayout(
    double ImageSize,
    double LabelFontSize,
    double LabelMaxWidth,
    int LabelMaxLines,
    bool ShowLabel,
    double TileWidth,
    double TileHeight,
    Thickness TileMargin,
    Thickness TilePadding,
    double ContentSpacing,
    int DecodePixelWidth)
{
    public double CellWidth => Math.Ceiling(TileWidth + TileMargin.Left + TileMargin.Right);
    public double CellHeight => Math.Ceiling(TileHeight + TileMargin.Top + TileMargin.Bottom);

    public static FileWidgetIconLayout Calculate(
        AppSettings settings,
        double? iconSizeOverride = null,
        double systemTextScaleFactor = 1)
    {
        double iconSize = SettingsService.NormalizeIconSize(iconSizeOverride ?? settings.IconSize);
        double textSize = Math.Clamp(settings.TextSize, SettingsService.MinTextSize, SettingsService.MaxTextSize);
        double horizontal = NormalizeSpacing(settings.HorizontalSpacingScale);
        double vertical = NormalizeSpacing(settings.VerticalSpacingScale);
        double nameWidth = NormalizeSpacing(settings.FileNameWidthScale);
        int lines = SettingsService.NormalizeFileNameLineCount(settings.FileNameLineCount);
        double labelWidth = Math.Max(iconSize, Lerp(iconSize, textSize * 10.5, nameWidth));
        return new FileWidgetIconLayout(
            iconSize,
            textSize,
            labelWidth,
            Math.Max(SettingsService.MinFileNameLineCount, lines),
            lines != SettingsService.HiddenFileNameLineCount,
            Math.Max(iconSize + Lerp(6, 28, horizontal), labelWidth + Lerp(4, 16, horizontal)),
            ResolveTileHeight(iconSize, textSize, lines, vertical, systemTextScaleFactor),
            new Thickness(Lerp(0, 2, horizontal), Lerp(0, 2, vertical), Lerp(0, 2, horizontal), Lerp(0, 2, vertical)),
            new Thickness(Lerp(1, 5, horizontal), Lerp(1, 6, vertical), Lerp(1, 5, horizontal), Lerp(1, 6, vertical)),
            Lerp(1, 7, vertical),
            ResolveDecodePixelWidth(iconSize));
    }

    internal static double ResolveTileHeight(
        double iconSize, double textSize, int fileNameLineCount,
        double verticalScale, double systemTextScaleFactor)
    {
        int lines = SettingsService.NormalizeFileNameLineCount(fileNameLineCount);
        double vertical = Math.Clamp(verticalScale, 0, 1);
        double textScale = WindowsCompatibilityService.NormalizeSystemTextScaleFactor(systemTextScaleFactor);
        double twoLineMinimum = iconSize + Lerp(24, 70, vertical);
        double oneLineMinimum = Math.Max(iconSize + textSize + 8, twoLineMinimum - textSize - 3);
        double visualMinimum = lines switch
        {
            SettingsService.HiddenFileNameLineCount => Math.Max(iconSize + 8, oneLineMinimum - textSize - 3),
            SettingsService.MinFileNameLineCount => oneLineMinimum,
            _ => twoLineMinimum
        };
        double spacing = lines == SettingsService.HiddenFileNameLineCount ? 0 : Lerp(1, 7, vertical);
        double labelHeight = lines == SettingsService.HiddenFileNameLineCount
            ? 0 : Math.Ceiling(textSize * 1.4 * textScale) * lines;
        return Math.Ceiling(Math.Max(visualMinimum,
            Lerp(1, 6, vertical) * 2 + iconSize + spacing + labelHeight + 2));
    }

    internal static int ResolveDecodePixelWidth(double iconSize) => iconSize switch
    {
        <= 28 => 48,
        <= 34 => 64,
        <= 42 => 80,
        _ => 128
    };

    private static double NormalizeSpacing(double value) =>
        (Math.Clamp(value, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale) -
         SettingsService.MinSpacingScale) / (SettingsService.MaxSpacingScale - SettingsService.MinSpacingScale);

    private static double Lerp(double min, double max, double value) => min + (max - min) * value;
}
