namespace DeskBox.Services;

/// <summary>
/// Loads glance photos into a BitmapImage through a file stream. The
/// framework's URI-keyed image cache has no eviction API, so every URI load
/// stays resident for the session; streamed sources stay out of that cache
/// entirely and keep the live footprint at the bitmaps actually displayed.
/// The URI path remains only as a failure fallback for providers whose
/// streams cannot be opened for random access.
/// </summary>
internal static class GlanceImageStreamLoader
{
    public static async Task LoadAsync(
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap,
        string path)
    {
        try
        {
            using Windows.Storage.Streams.IRandomAccessStream stream =
                DeskBox.Platform.ClipboardStorageFileOps.OpenPlainUnvirtualizedReadStream(path);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[GlanceImageStreamLoader] Streamed load failed for '{path}': {ex.Message}");
            try
            {
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
            }
            catch (Exception uriEx)
            {
                App.Log(
                    $"[GlanceImageStreamLoader] URI fallback failed for " +
                    $"'{path}': {uriEx.Message}");
            }
        }
    }
}
