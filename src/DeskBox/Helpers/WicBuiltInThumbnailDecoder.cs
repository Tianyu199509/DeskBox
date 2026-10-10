using System.Collections.Concurrent;

namespace DeskBox.Helpers;

/// <summary>
/// In-process last-resort thumbnail decode for the formats every Windows
/// install can read with a built-in WIC decoder (png/jpg/jpeg/tiff/tif/bmp).
/// Used only after the isolated Shell proxy failed or blacklisted a path
/// (feedback 226/452/495: after sleep/resume the proxy process could die with
/// CRT runtime error R6016, leaving these images without any preview).
/// <see cref="DecodeAsync"/> always creates the decoder through
/// <c>BitmapDecoder.CreateAsync(decoderId, stream)</c> with one of the
/// built-in decoder identifiers, which per the Windows documentation
/// "explicitly selects the bitmap decoder to be used and bypasses any
/// automatic codec arbitration" — no third-party WIC codec is ever loaded
/// into the DeskBox process by this fallback.
/// </summary>
internal static class WicBuiltInThumbnailDecoder
{
    private const long FailureRetryDelayMilliseconds = 30_000;
    private const int MaximumFailureEntries = 256;
    // Bound the transient decode allocation. The in-host XAML media path has
    // no such cap, so this stays at the same order of magnitude: 64 MP is an
    // 8000x8000 photo and costs ~256 MB of BGRA while scaling down.
    private const long MaximumSourcePixels = 64_000_000;
    // Absurd image files (embedded payloads in a renamed container) are not
    // worth streaming at all.
    private const long MaximumFileBytes = 512L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, long> s_recentFailures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the file extension matches a container format that a
    /// built-in WIC decoder can read. This whitelist is deliberately narrow:
    /// anything not on it keeps going through the proxy (or falls back to the
    /// icon path) rather than risking a third-party codec in-process.
    /// </summary>
    internal static bool TryGetBuiltInDecoderId(string path, out Guid decoderId)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        decoderId = extension switch
        {
            ".png" => Windows.Graphics.Imaging.BitmapDecoder.PngDecoderId,
            ".jpg" or ".jpeg" => Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId,
            ".tiff" or ".tif" => Windows.Graphics.Imaging.BitmapDecoder.TiffDecoderId,
            ".bmp" => Windows.Graphics.Imaging.BitmapDecoder.BmpDecoderId,
            _ => Guid.Empty,
        };
        return decoderId != Guid.Empty;
    }

    /// <summary>
    /// Decodes one whitelisted image to a BMPv5 payload shaped exactly like
    /// the native proxy's output, so the regular visibility check and XAML
    /// decode path apply unchanged. Returns null for non-whitelisted
    /// extensions, blacklisted paths, and any decode failure.
    /// </summary>
    internal static async Task<byte[]?> DecodeAsync(string path, int requestedSize)
    {
        if (!TryGetBuiltInDecoderId(path, out Guid decoderId))
        {
            return null;
        }

        string normalizedPath = NormalizePath(path);
        if (IsRecentFailure(normalizedPath))
        {
            return null;
        }

        byte[]? payload = null;
        try
        {
            payload = await DecodeWhitelistedImageAsync(path, decoderId, requestedSize);
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WicBuiltInThumbnailDecoder] Decode failed path={normalizedPath}: " +
                $"{ex.Message}");
        }

        if (payload is null || !ShellThumbnailProxy.IsVisibleBitmapPayload(payload))
        {
            RecordFailure(normalizedPath);
            return null;
        }

        s_recentFailures.TryRemove(normalizedPath, out _);
        return payload;
    }

    private static async Task<byte[]?> DecodeWhitelistedImageAsync(
        string path,
        Guid decoderId,
        int requestedSize)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaximumFileBytes)
        {
            return null;
        }

        Windows.Storage.StorageFile file =
            await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using Windows.Storage.Streams.IRandomAccessStream stream =
            await file.OpenReadAsync();
        // The Guid overload pins the exact built-in decoder; the plain
        // CreateAsync(stream) overload would run automatic codec arbitration
        // and must never be used here.
        Windows.Graphics.Imaging.BitmapDecoder decoder =
            await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(decoderId, stream);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
        {
            return null;
        }

        if ((long)decoder.PixelWidth * decoder.PixelHeight > MaximumSourcePixels)
        {
            return null;
        }

        (int scaledWidth, int scaledHeight) = ComputeScaledDimensions(
            decoder.PixelWidth,
            decoder.PixelHeight,
            requestedSize);
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            ScaledWidth = (uint)scaledWidth,
            ScaledHeight = (uint)scaledHeight,
        };
        Windows.Graphics.Imaging.PixelDataProvider pixels =
            await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);
        byte[] bgra = pixels.DetachPixelData();

        // EXIF rotation swaps the frame's aspect, so the scaled rectangle
        // comes back rotated when the oriented dimensions are transposed.
        (int outputWidth, int outputHeight) = ResolveOutputDimensions(
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.OrientedPixelWidth,
            decoder.OrientedPixelHeight,
            scaledWidth,
            scaledHeight);
        if (bgra.Length != checked(outputWidth * outputHeight * 4))
        {
            return null;
        }

        return EncodeBgraAsBitmapV5(outputWidth, outputHeight, bgra);
    }

    /// <summary>
    /// Fits the frame inside the requested box without upscaling: sources
    /// already at or below the box keep their native size.
    /// </summary>
    internal static (int Width, int Height) ComputeScaledDimensions(
        uint pixelWidth,
        uint pixelHeight,
        int boxSize)
    {
        int width = (int)Math.Min(pixelWidth, int.MaxValue);
        int height = (int)Math.Min(pixelHeight, int.MaxValue);
        if (width <= boxSize && height <= boxSize)
        {
            return (width, height);
        }

        double scale = Math.Min(
            (double)boxSize / width,
            (double)boxSize / height);
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Maps the pre-orientation scaled rectangle onto the post-orientation
    /// output: the 90-degree EXIF rotations transpose width and height.
    /// Square frames are unaffected either way.
    /// </summary>
    internal static (int Width, int Height) ResolveOutputDimensions(
        uint pixelWidth,
        uint pixelHeight,
        uint orientedPixelWidth,
        uint orientedPixelHeight,
        int scaledWidth,
        int scaledHeight) =>
        orientedPixelWidth == pixelHeight && orientedPixelHeight == pixelWidth
            ? (scaledHeight, scaledWidth)
            : (scaledWidth, scaledHeight);

    /// <summary>
    /// Encodes top-down 32-bit BGRA pixels in the same BMPv5 layout the
    /// native proxy emits (BITMAP_PIXEL_OFFSET 138, BI_BITFIELDS masks,
    /// sRGB color space), so downstream payload validation and the XAML
    /// decode path are shared with proxy results.
    /// </summary>
    internal static byte[] EncodeBgraAsBitmapV5(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
        {
            throw new InvalidOperationException(
                "Invalid BGRA payload for the built-in decoder.");
        }

        const int pixelOffset = 138;
        byte[] output = new byte[checked(pixelOffset + bgra.Length)];
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        WriteInt32(output, 2, output.Length);
        WriteInt32(output, 10, pixelOffset);
        WriteInt32(output, 14, 124); // BITMAPV5HEADER
        WriteInt32(output, 18, width);
        WriteInt32(output, 22, -height); // top-down
        output[26] = 1; // planes
        output[28] = 32; // bits per pixel
        WriteInt32(output, 30, 3); // BI_BITFIELDS
        WriteInt32(output, 34, bgra.Length);
        WriteInt32(output, 54, 0x00FF_0000); // red mask
        WriteInt32(output, 58, 0x0000_FF00); // green mask
        WriteInt32(output, 62, 0x0000_00FF); // blue mask
        WriteInt32(output, 66, unchecked((int)0xFF00_0000)); // alpha mask
        WriteInt32(output, 70, 0x7352_4742); // LCS_sRGB ('sRGB')
        WriteInt32(output, 122, 4); // LCS_GM_IMAGES intent
        Buffer.BlockCopy(bgra, 0, output, pixelOffset, bgra.Length);
        return output;
    }

    internal static void Invalidate(string path)
    {
        s_recentFailures.TryRemove(NormalizePath(path), out _);
    }

    internal static void ClearTransientFailures()
    {
        s_recentFailures.Clear();
    }

    private static bool IsRecentFailure(string normalizedPath)
    {
        if (!s_recentFailures.TryGetValue(normalizedPath, out long failedAt))
        {
            return false;
        }

        if (Environment.TickCount64 - failedAt < FailureRetryDelayMilliseconds)
        {
            return true;
        }

        s_recentFailures.TryRemove(normalizedPath, out _);
        return false;
    }

    private static void RecordFailure(string normalizedPath)
    {
        if (s_recentFailures.Count >= MaximumFailureEntries &&
            !s_recentFailures.ContainsKey(normalizedPath))
        {
            s_recentFailures.Clear();
        }

        s_recentFailures[normalizedPath] = Environment.TickCount64;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
    }
}
