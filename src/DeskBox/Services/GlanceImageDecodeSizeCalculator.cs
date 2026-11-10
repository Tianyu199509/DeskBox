namespace DeskBox.Services;

internal static class GlanceImageDecodeSizeCalculator
{
    // Quantize physical decode widths to 128 px buckets over the actual
    // viewport coverage (max(width, height * 16/9) * scale) instead of
    // inflating by diagonal * 1.25 (~2.44x pixel area). Caps at 1920 normal
    // and 1280 under ResourceSaver cut peak BGRA surfaces by ~55% while
    // keeping landscape and portrait crops sharp.
    internal const int MinimumDecodePixelWidth = 384;
    internal const int DecodeStepPixels = 128;
    internal const int MaximumDecodePixelWidth = 1920;
    internal const int ResourceSaverMaximumDecodePixelWidth = 1280;

    private const double DefaultLogicalWidth = 360;
    private const double DefaultLogicalHeight = 240;
    private const double LandscapeCoverAspectRatio = 16.0 / 9.0;
    private const double ShrinkRefreshRatio = 0.75;

    // The compact capsule background re-decodes only when the capsule width
    // actually changes, using 1:1 physical capsule width without extra inflation.
    internal const double CompactBackgroundSupersamplingFactor = 1.0;

    internal static int Calculate(
        double logicalWidth,
        double logicalHeight,
        double rasterizationScale,
        bool resourceSaver = false)
    {
        double width = NormalizeDimension(logicalWidth, DefaultLogicalWidth);
        double height = NormalizeDimension(logicalHeight, DefaultLogicalHeight);
        double scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0
            ? rasterizationScale
            : 1;
        scale = Math.Clamp(scale, 1, 4);

        double effectiveLogicalWidth = Math.Max(width, height * LandscapeCoverAspectRatio);
        double physicalWidth = Math.Ceiling(effectiveLogicalWidth * scale);
        int quantizedWidth = (int)(Math.Ceiling(physicalWidth / DecodeStepPixels) * DecodeStepPixels);
        if (quantizedWidth <= MinimumDecodePixelWidth)
        {
            return MinimumDecodePixelWidth;
        }

        int maxWidth = resourceSaver
            ? ResourceSaverMaximumDecodePixelWidth
            : MaximumDecodePixelWidth;
        if (quantizedWidth >= maxWidth)
        {
            return maxWidth;
        }

        return quantizedWidth;
    }

    internal static bool NeedsRefresh(int currentDecodePixelWidth, int requiredDecodePixelWidth)
    {
        if (requiredDecodePixelWidth <= 0)
        {
            return false;
        }

        if (currentDecodePixelWidth <= 0 || requiredDecodePixelWidth > currentDecodePixelWidth)
        {
            return true;
        }

        return requiredDecodePixelWidth <= Math.Floor(currentDecodePixelWidth * ShrinkRefreshRatio);
    }

    private static double NormalizeDimension(double value, double fallback)
    {
        return double.IsFinite(value) && value > 0 ? value : fallback;
    }
}
