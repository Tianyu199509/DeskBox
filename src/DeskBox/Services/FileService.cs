using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Storage;
using System.Collections.Concurrent;

namespace DeskBox.Services;

/// <summary>
/// Provides file system operations: enumerate files, resolve shortcuts, get icons.
/// </summary>
public sealed class FileService
{
    private static readonly ConcurrentDictionary<string, string> s_shellKindCache =
        new(StringComparer.OrdinalIgnoreCase);
    private sealed record TransferOperation(string SourcePath, string DestinationPath);

    private sealed record FileSystemEntrySnapshot(
        string Path,
        string Name,
        bool IsFolder,
        bool IsShortcut,
        long? FileSize,
        DateTime? CreatedAt,
        DateTime? LastModified,
        int? FolderItemCount);

    public sealed record FileTransferPlan(string SourcePath, string DestinationPath);

    public sealed record FileTransferResult(string SourcePath, string DestinationPath);

    private const uint FoMove = 0x0001;
    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmMkDir = 0x0200;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofSilent = 0x0004;

    /// <summary>
    /// Enumerate all files and folders in a directory and create WidgetItem models.
    /// </summary>
    public async Task<List<WidgetItem>> EnumerateDirectoryAsync(
        string directoryPath,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcons = true,
        bool loadFolderItemCounts = true)
    {
        using var perfScope = PerformanceLogger.Measure("FileService.EnumerateDirectory", $"path={directoryPath}");
        var items = new List<WidgetItem>();

        if (!Directory.Exists(directoryPath))
        {
            return items;
        }

        var entries = await Task.Run(() => EnumerateEntrySnapshots(directoryPath, loadFolderItemCounts));

        int sortOrder = 0;
        foreach (var entry in entries)
        {
            var item = await CreateWidgetItemAsync(
                entry,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons,
                showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions,
                loadIcons);
            item.SortOrder = sortOrder++;
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Create a WidgetItem from a file or folder path.
    /// </summary>
    public async Task<WidgetItem> CreateWidgetItemAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcon = true,
        bool loadFolderItemCount = true)
    {
        using var perfScope = PerformanceLogger.Measure("FileService.CreateWidgetItem", $"path={path}");
        var item = new WidgetItem
        {
            Path = path,
            Name = GetDisplayName(
                path,
                Directory.Exists(path),
                showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions),
            IsFolder = Directory.Exists(path),
            IsShortcut = ShortcutHelper.IsShortcutPath(path)
        };

        if (item.IsShortcut)
        {
            var info = ShortcutHelper.Resolve(path);
            if (info is not null)
            {
                item.TargetPath = info.TargetPath;
                item.Name = GetDisplayName(
                    path,
                    isFolder: false,
                    showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions);
            }
        }
        else
        {
            item.TargetPath = path;
        }

        if (!item.IsFolder && File.Exists(path))
        {
            try
            {
                var fi = new FileInfo(path);
                item.FileSize = fi.Length;
                item.CreatedAt = fi.CreationTime;
                item.LastModified = fi.LastWriteTime;
            }
            catch
            {
            }
        }
        else if (item.IsFolder)
        {
            item.Name = GetDisplayName(
                path,
                isFolder: true,
                showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions);
            item.IsFolderItemCountLoaded = loadFolderItemCount;
            if (loadFolderItemCount)
            {
                try
                {
                    item.FolderItemCount = CountVisibleChildren(path);
                    item.CreatedAt = Directory.GetCreationTime(path);
                    item.LastModified = Directory.GetLastWriteTime(path);
                }
                catch
                {
                    item.FolderItemCount = 0;
                }
            }
            else
            {
                TryApplyFolderLastModified(item, path);
            }
        }

        if (loadIcon)
        {
            item.Icon = await GetIconAsync(path, hideShortcutArrowOverlay, showImageFilesAsIcons);
        }

        return item;
    }

    private async Task<WidgetItem> CreateWidgetItemAsync(
        FileSystemEntrySnapshot entry,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcon = true)
    {
        using var perfScope = PerformanceLogger.Measure("FileService.CreateWidgetItem", $"path={entry.Path}");
        var item = new WidgetItem
        {
            Path = entry.Path,
            Name = GetDisplayName(
                entry.Path,
                entry.IsFolder,
                showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions),
            IsFolder = entry.IsFolder,
            IsShortcut = entry.IsShortcut,
            FileSize = entry.FileSize ?? 0,
            CreatedAt = entry.CreatedAt ?? default,
            LastModified = entry.LastModified ?? default,
            FolderItemCount = entry.FolderItemCount ?? 0,
            IsFolderItemCountLoaded = !entry.IsFolder || entry.FolderItemCount.HasValue,
            TargetPath = entry.Path
        };

        if (item.IsShortcut)
        {
            var info = ShortcutHelper.Resolve(entry.Path);
            if (info is not null)
            {
                item.TargetPath = info.TargetPath;
            }
        }

        if (loadIcon)
        {
            item.Icon = await GetIconAsync(entry.Path, hideShortcutArrowOverlay, showImageFilesAsIcons);
        }

        return item;
    }

    public async Task<WidgetItem?> TryCreateWidgetItemAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcon = true,
        bool loadFolderItemCount = true)
    {
        if (!ShouldDisplayEntry(path))
        {
            return null;
        }

        return await CreateWidgetItemAsync(
            path,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon,
            loadFolderItemCount);
    }

    public static bool ShouldDisplayEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var attr = File.GetAttributes(path);
            return (attr & System.IO.FileAttributes.Hidden) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<FileSystemEntrySnapshot> EnumerateEntrySnapshots(
        string directoryPath,
        bool loadFolderItemCounts)
    {
        return Directory.EnumerateFileSystemEntries(directoryPath)
            .Select(path => TryCreateEntrySnapshot(path, loadFolderItemCounts))
            .OfType<FileSystemEntrySnapshot>()
            .OrderBy(entry => !entry.IsFolder)
            .ThenBy(entry => entry.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static FileSystemEntrySnapshot? TryCreateEntrySnapshot(string path, bool loadFolderItemCount)
    {
        if (!ShouldDisplayEntry(path))
        {
            return null;
        }

        bool isFolder = Directory.Exists(path);
        bool isShortcut = ShortcutHelper.IsShortcutPath(path);
        string name = isFolder
            ? Path.GetFileName(path)
            : Path.GetFileNameWithoutExtension(path);
        long? fileSize = null;
        DateTime? createdAt = null;
        DateTime? lastModified = null;
        int? folderItemCount = null;

        if (!isFolder && File.Exists(path))
        {
            try
            {
                var fileInfo = new FileInfo(path);
                fileSize = fileInfo.Length;
                createdAt = fileInfo.CreationTime;
                lastModified = fileInfo.LastWriteTime;
            }
            catch
            {
            }
        }
        else if (isFolder)
        {
            try
            {
                if (loadFolderItemCount)
                {
                    folderItemCount = CountVisibleChildren(path);
                }

                createdAt = Directory.GetCreationTime(path);
                lastModified = Directory.GetLastWriteTime(path);
            }
            catch
            {
                folderItemCount = loadFolderItemCount ? 0 : null;
            }
        }

        return new FileSystemEntrySnapshot(
            path,
            name,
            isFolder,
            isShortcut,
            fileSize,
            createdAt,
            lastModified,
            folderItemCount);
    }

    public static string GetDisplayName(
        string path,
        bool isFolder,
        bool showFileExtensions,
        bool hideShortcutExtensionWhenShowingFileExtensions = true)
    {
        bool shouldHideExtension = !showFileExtensions ||
            (hideShortcutExtensionWhenShowingFileExtensions &&
             ShortcutHelper.IsShortcutPath(path));

        if (isFolder || !shouldHideExtension)
        {
            return Path.GetFileName(path);
        }

        string nameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(nameWithoutExtension)
            ? Path.GetFileName(path)
            : nameWithoutExtension;
    }

    public Task<BitmapImage?> GetIconAsync(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        return IconHelper.GetIconAsync(path, hideShortcutArrowOverlay, showImageFilesAsIcons);
    }

    public void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        IconHelper.ClearIconCache(path, hideShortcutArrowOverlay, showImageFilesAsIcons);
    }

    public async Task<string> GetShellKindAsync(WidgetItem item)
    {
        string path = item.IsShortcut && !string.IsNullOrWhiteSpace(item.TargetPath)
            ? item.TargetPath
            : item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Directory.Exists(path))
        {
            return "folder";
        }

        if (s_shellKindCache.TryGetValue(path, out string? cached))
        {
            return cached;
        }

        string kind = string.Empty;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.RetrievePropertiesAsync(["System.Kind"]);
            if (properties.TryGetValue("System.Kind", out object? value))
            {
                kind = value switch
                {
                    string text => text,
                    IEnumerable<string> values => values.FirstOrDefault() ?? string.Empty,
                    _ => string.Empty
                };
            }
        }
        catch
        {
            // Some shell namespaces and protected paths do not expose WinRT properties.
        }

        kind = kind.Trim().ToLowerInvariant();
        s_shellKindCache[path] = kind;
        return kind;
    }

    public Task<int> CountVisibleChildrenAsync(string folderPath)
    {
        return Task.Run(() => CountVisibleChildren(folderPath));
    }

    private static int CountVisibleChildren(string folderPath)
    {
        return Directory.EnumerateFileSystemEntries(folderPath)
            .Count(ShouldDisplayEntry);
    }

    private static void TryApplyFolderLastModified(WidgetItem item, string path)
    {
        try
        {
            item.LastModified = Directory.GetLastWriteTime(path);
        }
        catch
        {
        }
    }

    public async Task<IReadOnlyList<IStorageItem>> GetStorageItemsAsync(IEnumerable<string> sourcePaths)
    {
        var items = new List<IStorageItem>();

        foreach (string path in sourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var folder = await TryGetStorageFolderAsync(path);
                    if (folder is not null)
                    {
                        items.Add(folder);
                    }
                }
                else if (File.Exists(path))
                {
                    var file = await TryGetStorageFileAsync(path);
                    if (file is not null)
                    {
                        items.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[StorageItems] Failed to access '{path}': {ex.Message}");
            }
        }

        return items;
    }

    public IReadOnlyList<IStorageItem> GetStorageItems(IEnumerable<string> sourcePaths)
    {
        var items = new List<IStorageItem>();

        foreach (string path in sourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var folder = TryGetStorageFolder(path);
                    if (folder is not null)
                    {
                        items.Add(folder);
                    }
                }
                else if (File.Exists(path))
                {
                    var file = TryGetStorageFile(path);
                    if (file is not null)
                    {
                        items.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[StorageItems] Failed to access '{path}': {ex.Message}");
            }
        }

        return items;
    }

    /// <summary>
    /// WinRT's StorageFile/StorageFolder APIs cannot access files or folders
    /// that carry <see cref="FileAttributes.Hidden"/> or
    /// <see cref="FileAttributes.System"/> attributes, failing with
    /// UNABLE_TO_MASK_PATH (0x8007016C).  This is especially common for
    /// .lnk shortcut files created by certain installers.
    ///
    /// This helper temporarily strips those blocking attributes, returns the
    /// original attribute set (so the caller can restore them), and logs the
    /// action for diagnostics.
    /// </summary>
    private static System.IO.FileAttributes? StripBlockingAttributes(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            var blocking = attrs & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System);
            if (blocking == 0)
            {
                return null;
            }

            File.SetAttributes(path, attrs & ~blocking);
            App.Log($"[StorageItems] Temporarily stripped {blocking} attributes from '{path}'");
            return attrs; // return original so caller can restore
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreAttributes(string path, System.IO.FileAttributes? original)
    {
        if (original is null)
        {
            return;
        }

        try
        {
            File.SetAttributes(path, original.Value);
        }
        catch
        {
            // Best-effort restore; the file may have been moved/deleted.
        }
    }

    private static async Task<StorageFile?> TryGetStorageFileAsync(string path)
    {
        // WinRT broker cannot access files with Hidden/System attributes.
        var originalAttrs = StripBlockingAttributes(path);

        try
        {
            return await StorageFile.GetFileFromPathAsync(path);
        }
        catch (Exception directEx)
        {
            try
            {
                string? parentPath = Path.GetDirectoryName(path);
                string fileName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(fileName))
                {
                    App.Log($"[StorageItems] Failed to access '{path}': {directEx.Message}");
                    return null;
                }

                // Also strip attributes from the parent folder if needed.
                var parentAttrs = StripBlockingAttributes(parentPath);
                try
                {
                    var parent = await StorageFolder.GetFolderFromPathAsync(parentPath);
                    return await parent.GetFileAsync(fileName);
                }
                finally
                {
                    RestoreAttributes(parentPath, parentAttrs);
                }
            }
            catch (Exception parentEx)
            {
                App.Log($"[StorageItems] Failed to access '{path}': {directEx.Message}; parent lookup: {parentEx.Message}");
                return null;
            }
        }
        finally
        {
            RestoreAttributes(path, originalAttrs);
        }
    }

    private static StorageFile? TryGetStorageFile(string path)
    {
        var originalAttrs = StripBlockingAttributes(path);

        try
        {
            return StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception directEx)
        {
            try
            {
                string? parentPath = Path.GetDirectoryName(path);
                string fileName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(fileName))
                {
                    App.Log($"[StorageItems] Failed to access '{path}': {directEx.Message}");
                    return null;
                }

                var parentAttrs = StripBlockingAttributes(parentPath);
                try
                {
                    var parent = StorageFolder.GetFolderFromPathAsync(parentPath).AsTask().GetAwaiter().GetResult();
                    return parent.GetFileAsync(fileName).AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    RestoreAttributes(parentPath, parentAttrs);
                }
            }
            catch (Exception parentEx)
            {
                App.Log($"[StorageItems] Failed to access '{path}': {directEx.Message}; parent lookup: {parentEx.Message}");
                return null;
            }
        }
        finally
        {
            RestoreAttributes(path, originalAttrs);
        }
    }

    private static async Task<StorageFolder?> TryGetStorageFolderAsync(string path)
    {
        var originalAttrs = StripBlockingAttributes(path);
        try
        {
            return await StorageFolder.GetFolderFromPathAsync(path);
        }
        catch (Exception ex)
        {
            App.Log($"[StorageItems] Failed to access folder '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            RestoreAttributes(path, originalAttrs);
        }
    }

    private static StorageFolder? TryGetStorageFolder(string path)
    {
        var originalAttrs = StripBlockingAttributes(path);
        try
        {
            return StorageFolder.GetFolderFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            App.Log($"[StorageItems] Failed to access folder '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            RestoreAttributes(path, originalAttrs);
        }
    }

    /// <summary>
    /// Move or copy the given files or folders into a destination folder.
    /// </summary>
    public async Task TransferItemsAsync(IEnumerable<string> sourcePaths, string destinationFolder, bool move)
    {
        await TransferItemsWithResultAsync(sourcePaths, destinationFolder, move);
    }

    /// <summary>
    /// Move or copy the given files or folders into a destination folder and return the realized destination paths.
    /// </summary>
    public async Task<IReadOnlyList<FileTransferResult>> TransferItemsWithResultAsync(IEnumerable<string> sourcePaths, string destinationFolder, bool move)
    {
        string normalizedDestinationFolder = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(normalizedDestinationFolder))
        {
            Directory.CreateDirectory(normalizedDestinationFolder);
        }

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
                (File.Exists(path) || Directory.Exists(path)) &&
                !string.Equals(Path.GetDirectoryName(path), normalizedDestinationFolder, StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileTransferPlan(
                path,
                GetAvailablePath(Path.Combine(normalizedDestinationFolder, Path.GetFileName(path)), reservedPaths)))
            .ToList();

        return await ExecuteTransferPlanAsync(plans, move);
    }

    /// <summary>
    /// Execute a precomputed transfer plan and return the realized destination paths.
    /// </summary>
    public async Task<IReadOnlyList<FileTransferResult>> ExecuteTransferPlanAsync(
        IEnumerable<FileTransferPlan> plans,
        bool move,
        bool useShellProgress = false)
    {
        var operations = plans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.SourcePath) && !string.IsNullOrWhiteSpace(plan.DestinationPath))
            .Select(plan => new TransferOperation(
                Path.GetFullPath(plan.SourcePath),
                Path.GetFullPath(plan.DestinationPath)))
            .Where(operation =>
                (File.Exists(operation.SourcePath) || Directory.Exists(operation.SourcePath)) &&
                !string.Equals(operation.SourcePath, operation.DestinationPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (move && useShellProgress)
        {
            return await ExecuteShellMovePlanAsync(operations);
        }

        var completedOperations = new List<TransferOperation>(operations.Count);
        try
        {
            foreach (var operation in operations)
            {
                if (move)
                {
                    await MoveEntryAsync(operation.SourcePath, operation.DestinationPath);
                }
                else
                {
                    await CopyEntryAsync(operation.SourcePath, operation.DestinationPath);
                }

                completedOperations.Add(operation);
            }
        }
        catch
        {
            await RollbackTransfersAsync(completedOperations, move);
            throw;
        }

        return completedOperations
            .Select(operation => new FileTransferResult(operation.SourcePath, operation.DestinationPath))
            .ToList();
    }

    private static async Task<IReadOnlyList<FileTransferResult>> ExecuteShellMovePlanAsync(IReadOnlyList<TransferOperation> operations)
    {
        if (operations.Count == 0)
        {
            return [];
        }

        foreach (var operation in operations)
        {
            string? destinationDirectory = Path.GetDirectoryName(operation.DestinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
        }

        await Task.Run(() => MoveEntriesWithShellProgress(operations));

        return operations
            .Where(operation => File.Exists(operation.DestinationPath) || Directory.Exists(operation.DestinationPath))
            .Select(operation => new FileTransferResult(operation.SourcePath, operation.DestinationPath))
            .ToList();
    }

    /// <summary>
    /// Move the given files or folders into a destination folder.
    /// </summary>
    public async Task MoveItemsAsync(IEnumerable<string> sourcePaths, string destinationFolder)
    {
        await TransferItemsAsync(sourcePaths, destinationFolder, move: true);
    }

    /// <summary>
    /// Copy the given files or folders into a destination folder.
    /// </summary>
    public async Task CopyItemsAsync(IEnumerable<string> sourcePaths, string destinationFolder)
    {
        await TransferItemsAsync(sourcePaths, destinationFolder, move: false);
    }

    public async Task RelocateEntryAsync(string sourcePath, string destinationPath)
    {
        string normalizedSource = Path.GetFullPath(sourcePath);
        string normalizedDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await MoveEntryAsync(normalizedSource, normalizedDestination);
    }

    public async Task DeleteEntryAsync(string path, bool recycle = true)
    {
        string normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
        {
            return;
        }

        if (!recycle)
        {
            await DeleteEntryAsync(normalizedPath);
            return;
        }

        await Task.Run(() =>
        {
            DeleteEntryToRecycleBin(normalizedPath);
        });
    }

    private static void DeleteEntryToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        string from = path + "\0\0";
        unsafe
        {
            fixed (char* fromPointer = from)
            {
                var operation = new ShFileOperation
                {
                    WindowHandle = IntPtr.Zero,
                    Function = FoDelete,
                    From = fromPointer,
                    To = null,
                    Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent
                };

                int result = SHFileOperation(ref operation);
                if (result != 0 && result is not 2 and not 3 and not 1223)
                {
                    throw new Win32Exception(result);
                }
            }
        }
    }

    private static void MoveEntriesWithShellProgress(IReadOnlyList<TransferOperation> operations)
    {
        if (TryMoveEntriesToSameFolderWithShellProgress(operations))
        {
            return;
        }

        foreach (var operation in operations)
        {
            string from = operation.SourcePath + "\0\0";
            string to = operation.DestinationPath + "\0\0";
            unsafe
            {
                fixed (char* fromPointer = from)
                fixed (char* toPointer = to)
                {
                    var fileOperation = new ShFileOperation
                    {
                        WindowHandle = IntPtr.Zero,
                        Function = FoMove,
                        From = fromPointer,
                        To = toPointer,
                        Flags = FofNoConfirmMkDir
                    };

                    int result = SHFileOperation(ref fileOperation);
                    if (result == 1223 || fileOperation.AnyOperationsAborted != 0)
                    {
                        return;
                    }

                    if (result != 0 && result != 1223)
                    {
                        throw new Win32Exception(result);
                    }
                }
            }
        }
    }

    private static bool TryMoveEntriesToSameFolderWithShellProgress(IReadOnlyList<TransferOperation> operations)
    {
        if (operations.Count == 0)
        {
            return true;
        }

        string? destinationFolder = Path.GetDirectoryName(operations[0].DestinationPath);
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            return false;
        }

        if (operations.Any(operation =>
                !string.Equals(Path.GetDirectoryName(operation.DestinationPath), destinationFolder, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(operation.SourcePath),
                    Path.GetFileName(operation.DestinationPath),
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string from = string.Join('\0', operations.Select(operation => operation.SourcePath)) + "\0\0";
        string to = destinationFolder + "\0\0";
        unsafe
        {
            fixed (char* fromPointer = from)
            fixed (char* toPointer = to)
            {
                var fileOperation = new ShFileOperation
                {
                    WindowHandle = IntPtr.Zero,
                    Function = FoMove,
                    From = fromPointer,
                    To = toPointer,
                    Flags = FofNoConfirmMkDir
                };

                int result = SHFileOperation(ref fileOperation);
                if (result == 1223 || fileOperation.AnyOperationsAborted != 0)
                {
                    return true;
                }

                if (result != 0 && result != 1223)
                {
                    throw new Win32Exception(result);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Move an entire folder to a new location. Falls back to moving its contents when a direct move is not possible.
    /// </summary>
    public async Task RelocateDirectoryAsync(string sourceFolder, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(destinationFolder))
        {
            return;
        }

        string normalizedSource = Path.GetFullPath(sourceFolder);
        string normalizedDestination = Path.GetFullPath(destinationFolder);
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(normalizedDestination);
            return;
        }

        if (!Directory.Exists(normalizedSource))
        {
            Directory.CreateDirectory(normalizedDestination);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(normalizedDestination)!);

        try
        {
            if (!Directory.Exists(normalizedDestination))
            {
                await Task.Run(() => Directory.Move(normalizedSource, normalizedDestination));
                return;
            }
        }
        catch
        {
        }

        Directory.CreateDirectory(normalizedDestination);
        var entries = Directory.EnumerateFileSystemEntries(normalizedSource).ToList();
        await MoveItemsAsync(entries, normalizedDestination);

        if (!Directory.EnumerateFileSystemEntries(normalizedSource).Any())
        {
            Directory.Delete(normalizedSource, recursive: false);
        }
    }

    public static string SanitizeFileSystemName(string? name)
    {
        string sanitized = string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Trim();

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '-');
        }

        sanitized = sanitized.Trim().TrimEnd('.');
        return sanitized;
    }

    public static string GetAvailablePath(string desiredPath, ISet<string>? reservedPaths = null)
    {
        string normalizedPath = Path.GetFullPath(desiredPath);
        if (!PathExists(normalizedPath) && ReservePath(normalizedPath, reservedPaths))
        {
            return normalizedPath;
        }

        string? directoryPath = Path.GetDirectoryName(normalizedPath);
        string name = Path.GetFileName(normalizedPath);
        string extension = Path.GetExtension(name);
        string baseName = string.IsNullOrEmpty(extension)
            ? name
            : Path.GetFileNameWithoutExtension(name);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            directoryPath = Directory.GetCurrentDirectory();
        }

        for (int index = 2; ; index++)
        {
            string candidateName = string.IsNullOrEmpty(extension)
                ? $"{baseName} ({index})"
                : $"{baseName} ({index}){extension}";
            string candidatePath = Path.Combine(directoryPath, candidateName);
            if (!PathExists(candidatePath) && ReservePath(candidatePath, reservedPaths))
            {
                return candidatePath;
            }
        }
    }

    public static bool IsPathUnderDirectory(string candidatePath, string directoryPath)
    {
        string normalizedCandidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RollbackTransfersAsync(IEnumerable<TransferOperation> completedOperations, bool move)
    {
        foreach (var operation in completedOperations.Reverse())
        {
            try
            {
                if (move)
                {
                    await MoveEntryAsync(operation.DestinationPath, operation.SourcePath);
                }
                else
                {
                    await DeleteEntryAsync(operation.DestinationPath);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[TransferRollback] Failed to rollback '{operation.DestinationPath}' -> '{operation.SourcePath}': {ex}");
            }
        }
    }

    private static async Task CopyEntryAsync(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            try
            {
                await Task.Run(() => File.Copy(sourcePath, destinationPath, overwrite: false));
            }
            catch
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                throw;
            }
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await CopyDirectoryAsync(sourcePath, destinationPath);
        }
    }

    private static async Task MoveEntryAsync(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            await MoveFileAsync(sourcePath, destinationPath);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await MoveDirectoryAsync(sourcePath, destinationPath);
        }
    }

    private static async Task MoveFileAsync(string sourceFilePath, string destinationFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

        try
        {
            await Task.Run(() => File.Move(sourceFilePath, destinationFilePath));
        }
        catch (IOException)
        {
            bool copied = false;
            try
            {
                await Task.Run(() => File.Copy(sourceFilePath, destinationFilePath, overwrite: false));
                copied = true;
                await Task.Run(() => File.Delete(sourceFilePath));
            }
            catch
            {
                if (copied && File.Exists(destinationFilePath))
                {
                    File.Delete(destinationFilePath);
                }

                throw;
            }
        }
    }

    private static async Task MoveDirectoryAsync(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationDirectory)!);

        try
        {
            if (!Directory.Exists(destinationDirectory))
            {
                await Task.Run(() => Directory.Move(sourceDirectory, destinationDirectory));
                return;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        Directory.CreateDirectory(destinationDirectory);

        var completedChildOperations = new List<TransferOperation>();
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(sourceDirectory))
            {
                string destinationFilePath = GetAvailableDestinationPath(destinationDirectory, Path.GetFileName(filePath));
                await MoveFileAsync(filePath, destinationFilePath);
                completedChildOperations.Add(new TransferOperation(filePath, destinationFilePath));
            }

            foreach (string subDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                string folderName = Path.GetFileName(subDirectory);
                string destinationSubDirectory = GetAvailableDestinationPath(destinationDirectory, folderName);
                await MoveDirectoryAsync(subDirectory, destinationSubDirectory);
                completedChildOperations.Add(new TransferOperation(subDirectory, destinationSubDirectory));
            }
        }
        catch
        {
            await RollbackTransfersAsync(completedChildOperations, move: true);
            throw;
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
        {
            Directory.Delete(sourceDirectory, recursive: false);
        }
    }

    private static async Task DeleteEntryAsync(string path)
    {
        if (File.Exists(path))
        {
            await Task.Run(() => File.Delete(path));
            return;
        }

        if (Directory.Exists(path))
        {
            await Task.Run(() => Directory.Delete(path, recursive: true));
        }
    }

    private static string GetAvailableDestinationPath(string destinationFolder, string name)
    {
        return GetAvailablePath(Path.Combine(destinationFolder, name));
    }

    /// <summary>
    /// Open a file or shortcut using the default application.
    /// </summary>
    public static OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd = default)
    {
        if (ShortcutHelper.IsShellLinkPath(item.Path) && IsBrokenShortcut(item))
        {
            var resolution = ShortcutHelper.ResolveBrokenShortcutWithShellUi(item.Path, ownerHwnd);
            return resolution == BrokenShortcutResolution.ShortcutDeleted
                ? OpenItemResult.ShortcutDeleted
                : OpenItemResult.OpenedOrHandled;
        }

        var pathToOpen = item.IsShortcut ? item.Path : item.TargetPath;
        if (!string.IsNullOrEmpty(pathToOpen))
        {
            Win32Helper.OpenFile(pathToOpen);
        }

        return OpenItemResult.OpenedOrHandled;
    }

    private static bool IsBrokenShortcut(WidgetItem item)
    {
        if (!File.Exists(item.Path))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(item.TargetPath))
        {
            return true;
        }

        return !File.Exists(item.TargetPath) && !Directory.Exists(item.TargetPath);
    }

    /// <summary>
    /// Show a file in Windows Explorer with it selected.
    /// </summary>
    public static void ShowInExplorer(WidgetItem item)
    {
        var path = item.Path;
        if (!string.IsNullOrEmpty(path))
        {
            Win32Helper.ShowInExplorer(path);
        }
    }

    public enum OpenItemResult
    {
        OpenedOrHandled,
        ShortcutDeleted
    }

    /// <summary>
    /// Get the desktop folder paths (user and public).
    /// </summary>
    public static (string UserDesktop, string PublicDesktop) GetDesktopPaths()
    {
        return (
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        );
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        var completedChildOperations = new List<TransferOperation>();
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(sourceDirectory))
            {
                string destinationFilePath = GetAvailableDestinationPath(destinationDirectory, Path.GetFileName(filePath));
                await CopyEntryAsync(filePath, destinationFilePath);
                completedChildOperations.Add(new TransferOperation(filePath, destinationFilePath));
            }

            foreach (string subDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                string folderName = Path.GetFileName(subDirectory);
                string destinationSubDirectory = GetAvailableDestinationPath(destinationDirectory, folderName);
                await CopyDirectoryAsync(subDirectory, destinationSubDirectory);
                completedChildOperations.Add(new TransferOperation(subDirectory, destinationSubDirectory));
            }
        }
        catch
        {
            await RollbackTransfersAsync(completedChildOperations, move: false);
            if (Directory.Exists(destinationDirectory) && !Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
            {
                Directory.Delete(destinationDirectory, recursive: false);
            }

            throw;
        }
    }

    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static bool ReservePath(string path, ISet<string>? reservedPaths)
    {
        if (reservedPaths is null)
        {
            return true;
        }

        return reservedPaths.Add(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ShFileOperation
    {
        public IntPtr WindowHandle;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public char* ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOperation fileOperation);
}
