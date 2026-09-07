namespace DeskBox.Helpers;

/// <summary>
/// Shared heuristics for identifying a Shell icon that is technically valid but
/// is only a small glyph centered inside a much larger transparent canvas.
/// This is intentionally conservative: it is used for Shell-item icons, never
/// media thumbnails, and must not reject ordinary application artwork.
/// </summary>
internal static class IconBitmapQuality
{
    // Shell returns a 256 px canvas even when the source icon only has a 16/32/48
    // or 128 px frame. Anything whose visible artwork fills less than ~60% of the
    // canvas in BOTH dimensions renders visibly smaller than neighbouring tiles,
    // so it is cropped to its visible bounds and let the tile scale it up.
    private const int MinimumCanvasDimension = 64;
    private const double MaximumVisibleDimensionRatio = 0.6;

    internal static bool IsLikelyPadded(
        int width,
        int height,
        int visibleWidth,
        int visibleHeight)
    {
        if (width < MinimumCanvasDimension ||
            height < MinimumCanvasDimension ||
            visibleWidth <= 0 ||
            visibleHeight <= 0)
        {
            return false;
        }

        // A genuinely tiny visible rectangle in both dimensions is the common
        // signature of a 16/32/48 px Shell icon scaled into a Jumbo canvas.
        return visibleWidth <= width * MaximumVisibleDimensionRatio &&
               visibleHeight <= height * MaximumVisibleDimensionRatio;
    }
}
