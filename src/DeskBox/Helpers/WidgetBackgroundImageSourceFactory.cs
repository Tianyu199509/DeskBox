using DeskBox.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Helpers;

/// <summary>
/// Builds the XAML image source for a per-widget custom background. Decodes
/// are bounded so a full-resolution photo never enters the decoded-bitmap
/// budget: 1280 physical pixels covers a maximally sized widget at 2x DPI
/// while keeping each background at a few MB regardless of source size.
/// Content streams in through <see cref="WidgetImageStreamLoader"/>: the
/// asset file is rewritten under one fixed name on every re-pick, and a
/// URI source would keep serving the framework cache's first decode for
/// the whole session.
/// </summary>
public static class WidgetBackgroundImageSourceFactory
{
    public const int MinDecodedBackgroundPixels = 320;
    public const int MaxDecodedBackgroundPixels = 1280;

    public static int ResolveBoundedDecodePixels(double physicalSpanPixels)
    {
        if (!double.IsFinite(physicalSpanPixels) || physicalSpanPixels <= 0)
        {
            return MaxDecodedBackgroundPixels;
        }

        int quantized = (int)Math.Ceiling(physicalSpanPixels / 64.0) * 64;
        return Math.Clamp(quantized, MinDecodedBackgroundPixels, MaxDecodedBackgroundPixels);
    }

    /// <summary>
    /// Creates the source with decode bounds set but no content loaded —
    /// the caller owns streaming the bytes (the shared-source cache does
    /// this so one decode serves every window).
    /// </summary>
    public static ImageSource? CreateSource(string? filePath) =>
        CreateSource(filePath, MaxDecodedBackgroundPixels);

    public static ImageSource? CreateSource(string? filePath, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        int boundedPixels = Math.Clamp(
            decodePixelWidth,
            MinDecodedBackgroundPixels,
            MaxDecodedBackgroundPixels);

        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (!WidgetTitleIconCustomization.IsSupportedImageExtension(extension))
            {
                return null;
            }

            if (extension == ".svg")
            {
                return new SvgImageSource
                {
                    RasterizePixelWidth = boundedPixels
                };
            }

            return new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = boundedPixels
            };
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetBackground] Failed to create image source for '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates the source and streams the file into it.
    /// <paramref name="loadFailed"/> runs when the streamed decode fails
    /// so callers can degrade to their fallback layer.
    /// </summary>
    public static ImageSource? TryCreate(
        string? filePath,
        Action? loadFailed = null,
        int decodePixelWidth = MaxDecodedBackgroundPixels)
    {
        ImageSource? source = CreateSource(filePath, decodePixelWidth);
        if (source is null)
        {
            return null;
        }

        WidgetImageStreamLoader.Fill(
            source,
            filePath!,
            settled =>
            {
                if (!settled)
                {
                    loadFailed?.Invoke();
                }
            });
        return source;
    }
}
