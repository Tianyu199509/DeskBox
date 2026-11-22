using System.Buffers;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public sealed partial class ClipboardHistoryService
{
    private static async Task<(byte[]? PngBytes, byte[]? ThumbBytes, int Width, int Height)> TranscodeRentedBmpBufferOffUiThreadAsync(
        byte[] rentedBuffer,
        int byteLength,
        long maxImageBytes,
        CancellationToken token)
    {
        try
        {
            return await Task.Run(async () =>
            {
                using var ms = new MemoryStream(rentedBuffer, 0, byteLength, writable: false);
                using IRandomAccessStream winrtStream = ms.AsRandomAccessStream();
                return await DecodeOnceAndEncodeImageWithThumbnailAsync(winrtStream, maxImageBytes, token)
                    .ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static async Task<(byte[]? PngBytes, byte[]? ThumbBytes, int Width, int Height)> DecodeOnceAndEncodeImageWithThumbnailAsync(
        IRandomAccessStream inputStream,
        long maxImageBytes,
        CancellationToken token)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream).AsTask().ConfigureAwait(false);
        uint pixelWidth = decoder.PixelWidth;
        uint pixelHeight = decoder.PixelHeight;
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            return (null, null, 0, 0);
        }

        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);

        byte[]? fullPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            scaledWidth: 0,
            scaledHeight: 0,
            maxImageBytes,
            token).ConfigureAwait(false);
        if (fullPngBytes is not { Length: > 0 })
        {
            return (null, null, 0, 0);
        }

        if (pixelWidth <= ThumbnailMaxPixelSize && pixelHeight <= ThumbnailMaxPixelSize)
        {
            return (fullPngBytes, fullPngBytes, (int)pixelWidth, (int)pixelHeight);
        }

        ComputeScaledDimensions(pixelWidth, pixelHeight, ThumbnailMaxPixelSize, out uint thumbW, out uint thumbH);
        byte[]? thumbPngBytes = await EncodeSoftwareBitmapToPngBytesAsync(
            softwareBitmap,
            thumbW,
            thumbH,
            maxImageBytes,
            token).ConfigureAwait(false);

        return (fullPngBytes, thumbPngBytes, (int)pixelWidth, (int)pixelHeight);
    }

    private static async Task<byte[]?> EncodeSoftwareBitmapToPngBytesAsync(
        SoftwareBitmap softwareBitmap,
        uint scaledWidth,
        uint scaledHeight,
        long maxBytes,
        CancellationToken token)
    {
        using var outMemory = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outMemory)
            .AsTask().ConfigureAwait(false);
        encoder.SetSoftwareBitmap(softwareBitmap);
        if (scaledWidth > 0 && scaledHeight > 0)
        {
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            encoder.BitmapTransform.ScaledWidth = scaledWidth;
            encoder.BitmapTransform.ScaledHeight = scaledHeight;
        }

        await encoder.FlushAsync().AsTask().ConfigureAwait(false);
        if (outMemory.Size == 0 || (long)outMemory.Size > maxBytes)
        {
            return null;
        }

        var bytes = new byte[(int)outMemory.Size];
        outMemory.Seek(0);
        using Stream netStream = outMemory.AsStreamForRead();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await netStream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return bytes;
    }

    private static void ComputeScaledDimensions(
        uint pixelWidth,
        uint pixelHeight,
        uint maxSide,
        out uint scaledWidth,
        out uint scaledHeight)
    {
        if (pixelWidth >= pixelHeight)
        {
            scaledWidth = Math.Min(pixelWidth, maxSide);
            scaledHeight = Math.Max(1, (uint)Math.Round(pixelHeight * (scaledWidth / (double)pixelWidth)));
        }
        else
        {
            scaledHeight = Math.Min(pixelHeight, maxSide);
            scaledWidth = Math.Max(1, (uint)Math.Round(pixelWidth * (scaledHeight / (double)pixelHeight)));
        }
    }
}
