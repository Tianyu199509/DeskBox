using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public interface IQuickCaptureClipboardReader
{
    event EventHandler<object>? ContentChanged;

    Task<QuickCaptureClipboardContent?> ReadContentAsync();
}

public sealed class QuickCaptureClipboardContent
{
    private QuickCaptureClipboardContent(string? text, byte[]? imagePngBytes)
    {
        Text = text;
        ImagePngBytes = imagePngBytes;
    }

    public string? Text { get; }

    public byte[]? ImagePngBytes { get; }

    public bool HasImage => ImagePngBytes is { Length: > 0 };

    public static QuickCaptureClipboardContent FromText(string text) => new(text, null);

    public static QuickCaptureClipboardContent FromImage(byte[] imagePngBytes) => new(null, imagePngBytes);
}

public sealed class WindowsQuickCaptureClipboardReader : IQuickCaptureClipboardReader
{
    public event EventHandler<object>? ContentChanged
    {
        add => Clipboard.ContentChanged += value;
        remove => Clipboard.ContentChanged -= value;
    }

    public async Task<QuickCaptureClipboardContent?> ReadContentAsync()
    {
        var data = Clipboard.GetContent();
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            var bitmapReference = await data.GetBitmapAsync();
            byte[]? pngBytes = await TryReadBitmapAsPngAsync(bitmapReference);
            if (pngBytes is { Length: > 0 })
            {
                return QuickCaptureClipboardContent.FromImage(pngBytes);
            }
        }

        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var storageItems = await data.GetStorageItemsAsync();
            foreach (var storageItem in storageItems)
            {
                if (storageItem is Windows.Storage.StorageFile file &&
                    IsImageFile(file.Path))
                {
                    byte[]? bytes = await TryReadStorageFileAsPngAsync(file);
                    if (bytes is { Length: > 0 })
                    {
                        return QuickCaptureClipboardContent.FromImage(bytes);
                    }
                }
            }
        }

        if (data.Contains(StandardDataFormats.Text))
        {
            string text = await data.GetTextAsync();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : QuickCaptureClipboardContent.FromText(text);
        }

        if (data.Contains(StandardDataFormats.WebLink))
        {
            var link = await data.GetWebLinkAsync();
            return string.IsNullOrWhiteSpace(link?.AbsoluteUri)
                ? null
                : QuickCaptureClipboardContent.FromText(link.AbsoluteUri);
        }

        return null;
    }

    private static async Task<byte[]?> TryReadBitmapAsPngAsync(RandomAccessStreamReference bitmapReference)
    {
        try
        {
            using var stream = await bitmapReference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetSoftwareBitmap(await decoder.GetSoftwareBitmapAsync());
            await encoder.FlushAsync();

            var bytes = new byte[outputStream.Size];
            outputStream.Seek(0);
            using var dataReader = new DataReader(outputStream.GetInputStreamAt(0));
            await dataReader.LoadAsync((uint)outputStream.Size);
            dataReader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureClipboardReader] Failed to read bitmap: {ex}");
            return null;
        }
    }

    private static async Task<byte[]?> TryReadStorageFileAsPngAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetSoftwareBitmap(await decoder.GetSoftwareBitmapAsync());
            await encoder.FlushAsync();

            var bytes = new byte[outputStream.Size];
            using var dataReader = new DataReader(outputStream.GetInputStreamAt(0));
            await dataReader.LoadAsync((uint)outputStream.Size);
            dataReader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureClipboardReader] Failed to read image file: {ex}");
            return null;
        }
    }

    private static bool IsImageFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
