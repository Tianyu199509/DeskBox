using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using System.Net.Http;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public static class DeskBoxDragData
{
    public const string TextFormat = "DeskBox.Internal.Text.v1";
    public const string SourceFormat = "DeskBox.Internal.Source.v1";
    public const string SourceWidgetIdProperty = "DeskBoxSourceWidgetId";
    public const string SourcePathsProperty = "DeskBoxSourcePaths";
    public const string InternalFileDragTokenProperty =
        "DeskBoxInternalDragToken";
    public const string InternalFileDragToken =
        "DeskBox.WidgetItemDrag.v2";
    public const string DragSessionIdProperty =
        "DeskBoxDragSessionId";
    public const string StackReorderKeyProperty =
        "DeskBoxStackReorderKey";
    public const string SourceStackKeyProperty =
        "DeskBoxSourceStackKey";
    public const string ContentDragTokenProperty = "DeskBoxContentDragToken";
    public const string SourceTodo = "todo";
    public const string SourceQuickCapture = "quick-capture";

    private static readonly Lock s_contentDragGate = new();
    private static readonly TimeSpan s_contentDragTtl = TimeSpan.FromMinutes(2);
    private static string? s_activeTodoDragToken;
    private static DateTimeOffset s_activeTodoDragCreatedAt;
    private static IReadOnlyList<TodoItem>? s_activeTodoDragItems;
    private static string? s_activeQuickCaptureDragToken;
    private static DateTimeOffset s_activeQuickCaptureDragCreatedAt;
    private static IReadOnlyList<QuickCaptureItem>? s_activeQuickCaptureDragItems;
    private static readonly HttpClient s_virtualDropHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // Same ceiling the native path enforces: one dropped payload can never
    // materialize more than this onto disk or into memory.
    private const long MaxMaterializedBytes = 256L * 1024 * 1024;

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

    public static void SetTodoDragItems(DataPackage dataPackage, IReadOnlyList<TodoItem> items)
    {
        ArgumentNullException.ThrowIfNull(dataPackage);
        if (items is null || items.Count == 0)
        {
            return;
        }

        string token = "todo:" + Guid.NewGuid().ToString("N");
        lock (s_contentDragGate)
        {
            s_activeTodoDragToken = token;
            s_activeTodoDragCreatedAt = DateTimeOffset.UtcNow;
            s_activeTodoDragItems = items.ToArray();
        }

        dataPackage.Properties[ContentDragTokenProperty] = token;
    }

    public static IReadOnlyList<TodoItem>? TryGetTodoDragItems(DataPackageView dataView)
    {
        if (dataView is null ||
            !dataView.Properties.TryGetValue(ContentDragTokenProperty, out object? rawToken) ||
            rawToken is not string token ||
            !token.StartsWith("todo:", StringComparison.Ordinal))
        {
            return null;
        }

        lock (s_contentDragGate)
        {
            if (!string.Equals(s_activeTodoDragToken, token, StringComparison.Ordinal) ||
                DateTimeOffset.UtcNow - s_activeTodoDragCreatedAt > s_contentDragTtl)
            {
                return null;
            }

            return s_activeTodoDragItems;
        }
    }

    public static void SetQuickCaptureDragItems(DataPackage dataPackage, IReadOnlyList<QuickCaptureItem> items)
    {
        ArgumentNullException.ThrowIfNull(dataPackage);
        if (items is null || items.Count == 0)
        {
            return;
        }

        string token = "qc:" + Guid.NewGuid().ToString("N");
        lock (s_contentDragGate)
        {
            s_activeQuickCaptureDragToken = token;
            s_activeQuickCaptureDragCreatedAt = DateTimeOffset.UtcNow;
            s_activeQuickCaptureDragItems = items.ToArray();
        }

        dataPackage.Properties[ContentDragTokenProperty] = token;
    }

    public static IReadOnlyList<QuickCaptureItem>? TryGetQuickCaptureDragItems(DataPackageView dataView)
    {
        if (dataView is null ||
            !dataView.Properties.TryGetValue(ContentDragTokenProperty, out object? rawToken) ||
            rawToken is not string token ||
            !token.StartsWith("qc:", StringComparison.Ordinal))
        {
            return null;
        }

        lock (s_contentDragGate)
        {
            if (!string.Equals(s_activeQuickCaptureDragToken, token, StringComparison.Ordinal) ||
                DateTimeOffset.UtcNow - s_activeQuickCaptureDragCreatedAt > s_contentDragTtl)
            {
                return null;
            }

            return s_activeQuickCaptureDragItems;
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
        if (GetInternalDroppedFiles(dataView).Count > 0 ||
            dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.Contains(StandardDataFormats.Bitmap))
        {
            return true;
        }

        return dataView.AvailableFormats.Any(IsLikelyFileTransferFormat);
    }

    /// <summary>
    /// Returns whether a package can yield importable file data. Browser drags
    /// commonly expose a virtual file, a bitmap, or only an HTTP(S) URL in text.
    /// </summary>
    public static bool HasImportableFileData(DataPackageView dataView)
    {
        return HasDroppedFiles(dataView) ||
               dataView.Contains(StandardDataFormats.Text) ||
               dataView.Contains(StandardDataFormats.WebLink);
    }

    /// <summary>
    /// Identifies a file drag that originated from a DeskBox file surface. Content
    /// widgets treat this as a link/association, never as a file-system transfer.
    /// </summary>
    public static bool IsInternalFileDrag(DataPackageView dataView)
    {
        return dataView.Properties.TryGetValue(
                   InternalFileDragTokenProperty,
                   out object? token) &&
               string.Equals(
                   token as string,
                   InternalFileDragToken,
                   StringComparison.Ordinal) &&
               dataView.Properties.ContainsKey(SourcePathsProperty);
    }

    public static DataPackageOperation GetFileAssociationOperation(
        DataPackageView dataView,
        DataPackageOperation allowedOperations =
            DataPackageOperation.Copy | DataPackageOperation.Move)
    {
        return ResolveFileDragFeedbackOperation(
            dataView,
            IsInternalFileDrag(dataView)
                ? DataPackageOperation.Link
                : DataPackageOperation.Copy,
            allowedOperations);
    }

    // Internal metadata drags (todo/quick-capture reorder, tab moves) route
    // through private payload formats, so the transport effect is only a
    // glyph hint. Copy-export payloads advertise a single Copy effect —
    // feedback must fall back to Copy when Move is not in the offer.
    internal static DataPackageOperation ResolveInternalMetadataOperation(
        DataPackageOperation allowedOperations) =>
        allowedOperations.HasFlag(DataPackageOperation.Move)
            ? DataPackageOperation.Move
            : allowedOperations.HasFlag(DataPackageOperation.Copy)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;

    // Copy is the transport acknowledgement inside the app for a DeskBox file
    // drag. The source advertises Copy|Move to external receivers, but an
    // in-app target resolves file-system intent through the private paths and
    // the managed-transfer policy — OLE feedback must never authorize shell
    // source cleanup, so every non-None internal report is normalized to Copy.
    // A drag advertised with a single effect (Windows 10 collapses the set to
    // Move) has no Copy to answer with: internal feedback falls back to Move
    // there so the routed drop still fires. Completion results never flow
    // through this fallback — ResolveSafeDropCompletionOperation stays the
    // sole guard that never reports Move for a DeskBox drag.
    internal static DataPackageOperation ResolveFileDragFeedbackOperation(
        DataPackageView dataView,
        DataPackageOperation operation,
        DataPackageOperation allowedOperations =
            DataPackageOperation.Copy | DataPackageOperation.Move) =>
        operation != DataPackageOperation.None &&
        IsInternalFileDrag(dataView)
            ? !allowedOperations.HasFlag(DataPackageOperation.Copy) &&
              allowedOperations.HasFlag(DataPackageOperation.Move)
                ? DataPackageOperation.Move
                : DataPackageOperation.Copy
            : operation;

    internal static DataPackageOperation GetFileTransferOperations(
        DataPackageView dataView,
        DataPackageOperation allowedOperations)
    {
        if (IsInternalFileDrag(dataView))
        {
            // Domain B decoupling: internal DeskBox-to-DeskBox drags always
            // negotiate full Copy | Move | Link capability regardless of
            // ManagedDragOutAction == Copy or Win10 single-bit AllowedOperations.
            // Only OS/shared roots (Public Desktop, Start Menu, Program Files,
            // Windows) forbid Move.
            SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
                GetRawInternalSourcePaths(dataView),
                TimeSpan.FromMilliseconds(80));
            bool canMove = !origin.IsMoveProtectedSystemOrSharedSource;
            return canMove
                ? DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link
                : DataPackageOperation.Copy | DataPackageOperation.Link;
        }

        return allowedOperations == DataPackageOperation.None
            ? dataView.RequestedOperation
            : allowedOperations;
    }

    public static async Task<DroppedFileBatch> TryGetDroppedFilesAsync(DataPackageView dataView)
    {
        IReadOnlyList<DroppedFilePath> internalFiles =
            GetInternalDroppedFiles(dataView);
        if (internalFiles.Count > 0)
        {
            return new DroppedFileBatch(
                internalFiles,
                temporaryDirectory: null,
                skippedCount: 0);
        }

        var files = new List<DroppedFilePath>();
        string? temporaryDirectory = null;
        int skippedCount = 0;

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            dataView.AvailableFormats.Any(IsLikelyFileTransferFormat))
        {
            try
            {
                bool hasShellIdList = dataView.AvailableFormats.Any(static format =>
                    format.Contains("Shell IDList Array", StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<IStorageItem> storageItems = await dataView.GetStorageItemsAsync();
                long remainingBudgetBytes = MaxMaterializedBytes;
                foreach (IStorageItem storageItem in storageItems)
                {
                    if (storageItem is StorageFolder storageFolder &&
                        !string.IsNullOrWhiteSpace(storageFolder.Path) &&
                        Directory.Exists(storageFolder.Path))
                    {
                        string fullFolderPath = Path.GetFullPath(storageFolder.Path);
                        if (DropStagingFileOps.IsExternalTransientTempDropPath(fullFolderPath, hasShellIdList))
                        {
                            temporaryDirectory ??= CreateTemporaryDropDirectory();
                            string stagedFolderPath = DropStagingFileOps.StagePathIntoDirectory(
                                fullFolderPath,
                                temporaryDirectory);
                            files.Add(new DroppedFilePath(
                                stagedFolderPath,
                                storageFolder.Name,
                                ForceManagedCopy: true,
                                DroppedItemOriginKind.TransientArchiveOrCache));
                            continue;
                        }

                        files.Add(new DroppedFilePath(
                            fullFolderPath,
                            storageFolder.Name,
                            ForceManagedCopy: false,
                            DroppedItemOriginKind.RealFileSystemPath));
                        continue;
                    }

                    if (storageItem is not StorageFile storageFile)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(storageFile.Path) && File.Exists(storageFile.Path))
                    {
                        string fullFilePath = Path.GetFullPath(storageFile.Path);
                        if (DropStagingFileOps.IsExternalTransientTempDropPath(fullFilePath, hasShellIdList))
                        {
                            temporaryDirectory ??= CreateTemporaryDropDirectory();
                            string stagedFilePath = DropStagingFileOps.StagePathIntoDirectory(
                                fullFilePath,
                                temporaryDirectory);
                            files.Add(new DroppedFilePath(
                                stagedFilePath,
                                storageFile.Name,
                                ForceManagedCopy: true,
                                DroppedItemOriginKind.TransientArchiveOrCache));
                            continue;
                        }

                        files.Add(new DroppedFilePath(
                            fullFilePath,
                            storageFile.Name,
                            ForceManagedCopy: false,
                            DroppedItemOriginKind.RealFileSystemPath));
                        continue;
                    }

                    temporaryDirectory ??= CreateTemporaryDropDirectory();
                    (string? materializedPath, remainingBudgetBytes, bool budgetExceeded) =
                        await MaterializeStorageFileWithBudgetAsync(
                            storageFile,
                            temporaryDirectory,
                            remainingBudgetBytes);
                    if (budgetExceeded)
                    {
                        App.Log(
                            $"[DragDrop] Aborted virtual drop batch: payload exceeded {MaxMaterializedBytes}-byte budget.");
                        files.Clear();
                        DropStagingFileOps.CleanupVirtualDropBatchDirectory(temporaryDirectory);
                        temporaryDirectory = null;
                        skippedCount = Math.Max(1, storageItems.Count);
                        return new DroppedFileBatch(files, temporaryDirectory: null, skippedCount);
                    }

                    if (materializedPath is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    files.Add(new DroppedFilePath(
                        materializedPath,
                        Path.GetFileName(materializedPath),
                        ForceManagedCopy: true,
                        DroppedItemOriginKind.MaterializedVirtualStream));
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
                files.Add(new DroppedFilePath(
                    path,
                    fileName,
                    ForceManagedCopy: true,
                    DroppedItemOriginKind.MaterializedVirtualStream));
            }
            catch (Exception ex)
            {
                skippedCount++;
                App.Log($"[DragDrop] Failed to read dropped bitmap: {ex.Message}");
            }
        }

        // A browser may expose an image as FileContents/FileGroupDescriptorW,
        // but some sites only provide the resource URL. Materialize that URL as
        // a managed temporary file so the normal import pipeline can copy it.
        if (files.Count == 0)
        {
            string? webUrl = await TryGetDroppedWebUrlAsync(dataView);
            if (!string.IsNullOrWhiteSpace(webUrl))
            {
                temporaryDirectory ??= CreateTemporaryDropDirectory();
                string? materializedPath = await MaterializeWebUrlAsync(
                    webUrl,
                    temporaryDirectory);
                if (materializedPath is null)
                {
                    skippedCount++;
                }
                else
                {
                    files.Add(new DroppedFilePath(
                        materializedPath,
                        Path.GetFileName(materializedPath),
                        ForceManagedCopy: true,
                        DroppedItemOriginKind.MaterializedVirtualStream));
                }
            }
        }

        return new DroppedFileBatch(files, temporaryDirectory, skippedCount);
    }

    internal static IEnumerable<string> GetRawInternalSourcePaths(
        DataPackageView dataView)
    {
        if (!dataView.Properties.TryGetValue(
                SourcePathsProperty,
                out object? value))
        {
            return [];
        }

        return value switch
        {
            string[] array => array,
            IReadOnlyList<string> list => list,
            IEnumerable<string> sequence => sequence,
            _ => []
        };
    }

    public static IReadOnlyList<DroppedFilePath> GetInternalDroppedFiles(
        DataPackageView dataView)
    {
        IEnumerable<string> paths = GetRawInternalSourcePaths(dataView);

        var files = new List<DroppedFilePath>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                (!File.Exists(path) && !Directory.Exists(path)))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    files.Add(new DroppedFilePath(
                        fullPath,
                        Path.GetFileName(fullPath),
                        ForceManagedCopy: false));
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DragDrop] Ignored invalid internal path '{path}': " +
                    ex.Message);
            }
        }

        return files;
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

    private static async Task<string?> TryGetDroppedWebUrlAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            try
            {
                Uri? link = await dataView.GetWebLinkAsync();
                if (IsHttpUrl(link?.AbsoluteUri))
                {
                    return link!.AbsoluteUri;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DragDrop] Failed to read dropped web link: {ex.Message}");
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return null;
        }

        try
        {
            string text = await dataView.GetTextAsync();
            return text.Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(IsHttpUrl);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to read dropped text: {ex.Message}");
            return null;
        }
    }

    private static bool IsHttpUrl(string? value)
    {
        return Uri.TryCreate(value?.Trim().Trim('"'), UriKind.Absolute, out Uri? uri) &&
               uri.Scheme is "http" or "https";
    }

    private static async Task<string?> MaterializeWebUrlAsync(
        string url,
        string temporaryDirectory)
    {
        if (!Uri.TryCreate(url.Trim().Trim('"'), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        string? destinationPath = null;
        try
        {
            string fileName = FileService.SanitizeFileSystemName(
                Path.GetFileName(uri.LocalPath));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "Dropped web resource";
            }

            destinationPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, fileName));
            // Stream the response instead of buffering it whole: the URL comes
            // from the drop payload, so its size is untrusted.
            using HttpResponseMessage response = await s_virtualDropHttpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > MaxMaterializedBytes)
            {
                App.Log(
                    $"[DragDrop] Web drop '{uri}' declares {declaredLength} bytes; " +
                    "rejected over the materialization budget.");
                return null;
            }

            long totalBytes = 0;
            using Stream sourceStream = await response.Content.ReadAsStreamAsync();
            using (var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await sourceStream.ReadAsync(buffer)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > MaxMaterializedBytes)
                    {
                        App.Log(
                            $"[DragDrop] Web drop '{uri}' exceeded " +
                            $"{MaxMaterializedBytes} bytes mid-stream; aborted.");
                        destination.Dispose();
                        try { File.Delete(destinationPath); } catch { }
                        return null;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            destinationPath =
                VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                    destinationPath);
            ZoneIdentifierWriter.TryMarkFile(destinationPath, uri.AbsoluteUri);
            App.Log(
                $"[DragDrop] Materialized web drop url='{uri}' " +
                $"path='{destinationPath}' bytes={totalBytes}");
            return destinationPath;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to materialize web drop '{uri}': {ex.Message}");
            if (destinationPath is not null)
            {
                try { File.Delete(destinationPath); } catch { }
            }

            return null;
        }
    }

    internal static async Task<string?> MaterializeStorageFileAsync(
        StorageFile storageFile,
        string temporaryDirectory)
    {
        (string? path, _, _) = await MaterializeStorageFileWithBudgetAsync(
            storageFile,
            temporaryDirectory,
            MaxMaterializedBytes);
        return path;
    }

    private static async Task<(string? Path, long RemainingBudgetBytes, bool BudgetExceeded)>
        MaterializeStorageFileWithBudgetAsync(
            StorageFile storageFile,
            string temporaryDirectory,
            long remainingBudgetBytes)
    {
        string fileName = FileService.SanitizeFileSystemName(Path.GetFileName(storageFile.Name));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"Dropped file {DateTime.Now:yyyyMMdd-HHmmss}";
        }

        try
        {
            using IRandomAccessStreamWithContentType source = await storageFile.OpenReadAsync();
            (string path, long updatedRemaining) = await SaveRandomAccessStreamWithBudgetAsync(
                source,
                fileName,
                temporaryDirectory,
                remainingBudgetBytes);
            return (path, updatedRemaining, false);
        }
        catch (VirtualDropBudgetExceededException ex)
        {
            App.Log($"[DragDrop] Virtual file '{storageFile.Name}' exceeded budget: {ex.Message}");
            return (null, 0, true);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDrop] Failed to materialize virtual file '{storageFile.Name}': {ex.Message}");
            return (null, remainingBudgetBytes, false);
        }
    }

    private static async Task<string> SaveRandomAccessStreamAsync(
        IRandomAccessStream source,
        string fileName,
        string temporaryDirectory)
    {
        (string path, _) = await SaveRandomAccessStreamWithBudgetAsync(
            source,
            fileName,
            temporaryDirectory,
            MaxMaterializedBytes);
        return path;
    }

    private static async Task<(string Path, long RemainingBudgetBytes)> SaveRandomAccessStreamWithBudgetAsync(
        IRandomAccessStream source,
        string fileName,
        string temporaryDirectory,
        long remainingBudgetBytes)
    {
        Directory.CreateDirectory(temporaryDirectory);
        string destinationPath = FileService.GetAvailablePath(
            Path.Combine(temporaryDirectory, fileName));
        if (source.Size > (ulong)Math.Max(0, remainingBudgetBytes))
        {
            throw new VirtualDropBudgetExceededException(
                $"The dropped stream of {source.Size} bytes exceeds the " +
                $"remaining {remainingBudgetBytes}-byte materialization budget.");
        }

        source.Seek(0);
        using Stream sourceStream = source.AsStreamForRead();
        long totalBytes = 0;
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await sourceStream.ReadAsync(buffer)) > 0)
            {
                totalBytes += read;
                if (totalBytes > remainingBudgetBytes)
                {
                    throw new VirtualDropBudgetExceededException(
                        "The dropped stream exceeded the remaining materialization " +
                        $"budget of {remainingBudgetBytes} bytes mid-copy.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read));
            }
        }

        string resolvedPath = VirtualDropFileNameResolver.AddMissingExtensionFromContent(
            destinationPath);
        ZoneIdentifierWriter.TryMarkFile(
            resolvedPath,
            ZoneIdentifierWriter.VirtualDropSource);
        return (resolvedPath, Math.Max(0, remainingBudgetBytes - totalBytes));
    }

    private sealed class VirtualDropBudgetExceededException(string message) : IOException(message);

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

public enum DroppedItemOriginKind
{
    RealFileSystemPath = 0,
    TransientArchiveOrCache = 1,
    MaterializedVirtualStream = 2,
    ShellNamespaceLinkOnly = 3,
}

public sealed record DroppedFilePath(
    string Path,
    string DisplayName,
    bool ForceManagedCopy,
    DroppedItemOriginKind OriginKind = DroppedItemOriginKind.RealFileSystemPath)
{
    public bool IsProviderTemporary =>
        ForceManagedCopy || OriginKind != DroppedItemOriginKind.RealFileSystemPath;
}

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
