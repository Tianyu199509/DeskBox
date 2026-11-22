using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

/// <summary>
/// Fills raster and SVG image sources for mutable widget asset files
/// (per-widget backgrounds, title icons, shared backgrounds) through a
/// file stream. The framework's URI-keyed image cache has no eviction
/// API and serves the first decode for the whole session, so loading a
/// re-picked asset by URI would keep showing the previous image even
/// after a reset deleted and rewrote the file — stream-loaded sources
/// stay out of that cache entirely. Unlike
/// <see cref="GlanceImageStreamLoader"/> there is no URI fallback here:
/// widget asset files rewrite one fixed name, so a URI load would
/// resurrect exactly the stale decode this loader exists to avoid.
/// Failures are reported through <paramref name="settled"/> so callers
/// degrade to their fallback layer instead of keeping a blank surface.
/// Call on the UI thread; the awaits deliberately keep the captured
/// context so SetSourceAsync stays on the source's thread.
/// </summary>
internal static class WidgetImageStreamLoader
{
    /// <summary>Starts the streamed load; <c>settled(false)</c> on failure.</summary>
    public static void Fill(
        ImageSource source,
        string filePath,
        Action<bool>? settled = null)
    {
        _ = FillAsync(source, filePath, settled);
    }

    private static async Task FillAsync(
        ImageSource source,
        string filePath,
        Action<bool>? settled)
    {
        bool succeeded = false;
        try
        {
            using IRandomAccessStream stream = DeskBox.Platform.ClipboardStorageFileOps.OpenPlainUnvirtualizedReadStream(filePath);
            switch (source)
            {
                case BitmapImage bitmap:
                    await bitmap.SetSourceAsync(stream);
                    succeeded = true;
                    break;
                case SvgImageSource svg:
                    await svg.SetSourceAsync(stream);
                    succeeded = true;
                    break;
                default:
                    App.Log(
                        $"[WidgetImageStreamLoader] Unsupported source type for '{filePath}'.");
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetImageStreamLoader] Streamed load failed for '{filePath}': {ex.Message}");
        }

        try
        {
            settled?.Invoke(succeeded);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetImageStreamLoader] Settle callback threw: {ex.Message}");
        }
    }
}
