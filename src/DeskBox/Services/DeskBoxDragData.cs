using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public static class DeskBoxDragData
{
    public const string TextFormat = "DeskBox.Internal.Text.v1";
    public const string SourceFormat = "DeskBox.Internal.Source.v1";
    public const string TodoColorMarkerFormat = "DeskBox.Todo.ColorMarker.v1";
    public const string SourceTodo = "todo";
    public const string SourceQuickCapture = "quick-capture";

    public static void SetText(DataPackage dataPackage, string? text, string source)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return;
        }

        dataPackage.SetText(normalizedText);
        dataPackage.SetData(TextFormat, normalizedText);
        dataPackage.SetData(SourceFormat, source);
    }

    public static void SetTodoColorMarker(DataPackage dataPackage, string colorMarker)
    {
        if (!string.IsNullOrWhiteSpace(colorMarker))
        {
            dataPackage.SetData(TodoColorMarkerFormat, colorMarker.Trim());
        }
    }

    public static async Task<string?> TryGetTodoColorMarkerAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(TodoColorMarkerFormat))
        {
            return null;
        }

        try
        {
            return await dataView.GetDataAsync(TodoColorMarkerFormat) as string;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read todo color marker: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> TryGetTextAsync(DataPackageView dataView)
    {
        string? internalText = await TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText;
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeText(text);
            }
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        return null;
    }

    public static bool HasDroppedFiles(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.Contains(StandardDataFormats.Bitmap))
        {
            return true;
        }

        return dataView.AvailableFormats.Any(IsLikelyFileTransferFormat);
    }

    public static async Task<DroppedFileBatch> TryGetDroppedFilesAsync(DataPackageView dataView)
    {
        var files = new List<DroppedFilePath>();
        string? temporaryDirectory = null;
        int skippedCount = 0;

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.AvailableFormats.Any(IsLikelyFileTransferFormat))
        {
            try
            {
                IReadOnlyList<IStorageItem> storageItems = await dataView.GetStorageItemsAsync();
                foreach (IStorageItem storageItem in storageItems)
                {
                    if (storageItem is not StorageFile storageFile)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(storageFile.Path) && File.Exists(storageFile.Path))
                    {
                        files.Add(new DroppedFilePath(
                            Path.GetFullPath(storageFile.Path),
                            storageFile.Name,
                            ForceManagedCopy: false));
                        continue;
                    }

                    temporaryDirectory ??= CreateTemporaryDropDirectory();
                    string? materializedPath = await MaterializeStorageFileAsync(storageFile, temporaryDirectory);
                    if (materializedPath is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    files.Add(new DroppedFilePath(
                        materializedPath,
                        Path.GetFileName(materializedPath),
                        ForceManagedCopy: true));
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DragDrop] Failed to read dropped storage items: {ex.Message}");
            }
        }

        if (files.Count == 0 && dataView.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                RandomAccessStreamReference bitmapReference = await dataView.GetBitmapAsync();
                using IRandomAccessStreamWithContentType bitmapStream = await bitmapReference.OpenReadAsync();
                temporaryDirectory ??= CreateTemporaryDropDirectory();
                string extension = GetBitmapExtension(bitmapStream.ContentType);
                string fileName = $"Dropped image {DateTime.Now:yyyyMMdd-HHmmss}{extension}";
                string path = await SaveRandomAccessStreamAsync(bitmapStream, fileName, temporaryDirectory);
                files.Add(new DroppedFilePath(path, fileName, ForceManagedCopy: true));
            }
            catch (Exception ex)
            {
                skippedCount++;
                App.Log($"[DragDrop] Failed to read dropped bitmap: {ex.Message}");
            }
        }

        return new DroppedFileBatch(files, temporaryDirectory, skippedCount);
    }

    public static async Task<string?> TryGetInternalTextAsync(DataPackageView dataView)
    {
        if (!dataView.Contains(TextFormat))
        {
            return null;
        }

        try
        {
            if (await dataView.GetDataAsync(TextFormat) is string internalText &&
                !string.IsNullOrWhiteSpace(internalText))
            {
                return NormalizeText(internalText);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read DeskBox internal text: {ex.Message}");
        }

        return null;
    }

    private static string NormalizeText(string? text)
    {
        return (text ?? string.Empty).Trim();
    }

    private static bool IsLikelyFileTransferFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format) ||
            format.StartsWith("Preferred DropEffect", StringComparison.Ordinal))
        {
            return false;
        }

        return format.Contains("StorageItems", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("StorageItem", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileName", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> MaterializeStorageFileAsync(
        StorageFile storageFile,
        string temporaryDirectory)
    {
        string fileName = FileService.SanitizeFileSystemName(Path.GetFileName(storageFile.Name));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"Dropped file {DateTime.Now:yyyyMMdd-HHmmss}";
        }

        try
        {
            using IRandomAccessStreamWithContentType source = await storageFile.OpenReadAsync();
            return await SaveRandomAccessStreamAsync(source, fileName, temporaryDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to materialize virtual file '{storageFile.Name}': {ex.Message}");
            return null;
        }
    }

    private static async Task<string> SaveRandomAccessStreamAsync(
        IRandomAccessStream source,
        string fileName,
        string temporaryDirectory)
    {
        Directory.CreateDirectory(temporaryDirectory);
        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(temporaryDirectory, fileName));
        source.Seek(0);
        using Stream sourceStream = source.AsStreamForRead();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await sourceStream.CopyToAsync(destination);
        return destinationPath;
    }

    private static string CreateTemporaryDropDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "DroppedFiles",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetBitmapExtension(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            _ => ".png"
        };
    }
}

public sealed record DroppedFilePath(string Path, string DisplayName, bool ForceManagedCopy);

public sealed class DroppedFileBatch : IDisposable
{
    private readonly string? _temporaryDirectory;

    internal DroppedFileBatch(
        IReadOnlyList<DroppedFilePath> files,
        string? temporaryDirectory,
        int skippedCount)
    {
        Files = files;
        _temporaryDirectory = temporaryDirectory;
        SkippedCount = skippedCount;
    }

    public IReadOnlyList<DroppedFilePath> Files { get; }

    public int SkippedCount { get; }

    public void Dispose()
    {
        if (string.IsNullOrWhiteSpace(_temporaryDirectory) ||
            !Directory.Exists(_temporaryDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to clean temporary drop files: {ex.Message}");
        }
    }
}
