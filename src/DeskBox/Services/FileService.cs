using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Storage;
using System.Collections.Concurrent;

namespace DeskBox.Services;

internal enum FolderSnapshotStatus
{
    SuccessWithItems,
    SuccessEmpty,
    Partial,
    Unavailable,
    AccessDenied,

    // Kept as a source-compatible alias for callers that only need to express
    // a successful (possibly non-empty) snapshot. New code should use the two
    // explicit success states above.
    Complete = SuccessWithItems
}

internal enum FolderEntryRefreshStatus
{
    Available,
    NotFound,
    Filtered,
    Unavailable,
    AccessDenied
}

internal sealed record FolderPathSnapshot(
    FolderSnapshotStatus Status,
    IReadOnlySet<string> Paths);

internal sealed record FolderEnumerationResult(
    FolderSnapshotStatus Status,
    IReadOnlyList<WidgetItem> Items);

internal static class FolderSnapshotStatusPolicy
{
    public static bool IsSuccessful(FolderSnapshotStatus status) =>
        status is FolderSnapshotStatus.SuccessWithItems or
            FolderSnapshotStatus.SuccessEmpty;
}

/// <summary>
/// Provides file system operations: enumerate files, resolve shortcuts, get icons.
/// </summary>
public sealed partial class FileService
{
    private const string UnsafeFolderTransferFallbackMessage =
        "A folder cannot be copied or moved into itself or one of its subfolders.";
    private static readonly TimeSpan ShortcutMetadataTimeout =
        TimeSpan.FromMilliseconds(1500);
    internal const int MaxShellKindCacheEntries = 4096;
    private readonly LocalizationService? _localizationService;
    private static readonly ConcurrentDictionary<string, string> s_shellKindCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> s_shellKindCacheOrder = new();
    private static readonly object s_shellKindCacheGate = new();
    private sealed record TransferOperation(
        string SourcePath,
        string DestinationPath,
        bool SourceIsDirectory = false);

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

    public interface IFileTransferWithCompletedResults
    {
        IReadOnlyList<FileTransferResult> CompletedResults { get; }
    }

    public sealed class FileTransferSourceCleanupException : IOException,
        IFileTransferWithCompletedResults
    {
        internal FileTransferSourceCleanupException(
            string sourcePath,
            string destinationPath,
            Exception innerException)
            : base(
                "The files were copied successfully, but source cleanup did " +
                "not finish. The complete destination was kept to protect " +
                "the data.",
                innerException)
        {
            CompletedResults =
            [
                new FileTransferResult(sourcePath, destinationPath)
            ];
        }

        public IReadOnlyList<FileTransferResult> CompletedResults { get; }
    }

    public sealed class FileTransferCanceledException : OperationCanceledException,
        IFileTransferWithCompletedResults
    {
        internal FileTransferCanceledException(
            IReadOnlyList<FileTransferResult> completedResults,
            CancellationToken cancellationToken)
            : base(
                "The Windows file operation was canceled after completing " +
                $"{completedResults.Count} item(s).",
                innerException: null,
                cancellationToken)
        {
            CompletedResults = completedResults;
        }

        public IReadOnlyList<FileTransferResult> CompletedResults { get; }
    }

    public sealed class FileTransferPartialFailureException : IOException,
        IFileTransferWithCompletedResults
    {
        internal FileTransferPartialFailureException(
            IReadOnlyList<FileTransferResult> completedResults,
            Exception? innerException = null)
            : base(
                "The Windows file operation completed only part of the " +
                $"request ({completedResults.Count} item(s) completed).",
                innerException)
        {
            CompletedResults = completedResults;
        }

        public IReadOnlyList<FileTransferResult> CompletedResults { get; }
    }

    private const uint FoMove = 0x0001;
    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmMkDir = 0x0200;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofSilent = 0x0004;
    private static readonly TimeSpan ShellMoveRecoveryProbeDelay =
        TimeSpan.FromSeconds(15);

    public FileService(LocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
    }

    /// <summary>
    /// Tracks active copy and move operations for all file surfaces that share
    /// this service instance. The registry is UI-only state and never owns a
    /// filesystem handle.
    /// </summary>
    public FileTransferSessionRegistry TransferSessions { get; } = new();

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
                loadIcons,
                loadShortcutTarget: true);
            item.SortOrder = sortOrder++;
            items.Add(item);
        }

        return items;
    }

    internal async Task<FolderEnumerationResult> EnumerateDirectoryForRefreshAsync(
        string directoryPath,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcons = false,
        bool loadFolderItemCounts = false)
    {
        FolderPathSnapshot before = await CaptureDirectChildSnapshotAsync(directoryPath);
        if (!FolderSnapshotStatusPolicy.IsSuccessful(before.Status))
        {
            return new FolderEnumerationResult(before.Status, []);
        }

        var entries = new List<FileSystemEntrySnapshot>();
        bool partial = false;
        foreach (string path in before.Paths)
        {
            FolderEntryRefreshStatus state = ClassifyDirectChild(before, path);
            if (state is FolderEntryRefreshStatus.Unavailable or
                FolderEntryRefreshStatus.AccessDenied ||
                state == FolderEntryRefreshStatus.NotFound)
            {
                partial = true;
                continue;
            }

            if (state == FolderEntryRefreshStatus.Filtered)
            {
                continue;
            }

            FileSystemEntrySnapshot? entry = TryCreateEntrySnapshot(path, loadFolderItemCounts);
            if (entry is null)
            {
                // The entry changed between the root snapshot and metadata read.
                // Treat that as an incomplete view instead of silently deleting it.
                partial = true;
                continue;
            }

            entries.Add(entry);
        }

        FolderPathSnapshot after = await CaptureDirectChildSnapshotAsync(directoryPath);
        if (!FolderSnapshotStatusPolicy.IsSuccessful(after.Status) ||
            !before.Paths.SetEquals(after.Paths))
        {
            partial = true;
        }

        var items = new List<WidgetItem>(entries.Count);
        int sortOrder = 0;
        foreach (FileSystemEntrySnapshot entry in entries
                     .OrderBy(entry => !entry.IsFolder)
                     .ThenBy(entry => entry.Name, NaturalStringComparer.CurrentCultureIgnoreCase))
        {
            WidgetItem item = await CreateWidgetItemAsync(
                entry,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons,
                showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions,
                loadIcons,
                loadShortcutTarget: false);
            item.SortOrder = sortOrder++;
            items.Add(item);
        }

        FolderSnapshotStatus status = partial
            ? FolderSnapshotStatus.Partial
            : items.Count == 0
                ? FolderSnapshotStatus.SuccessEmpty
                : FolderSnapshotStatus.SuccessWithItems;
        return new FolderEnumerationResult(
            status,
            items);
    }

    internal static Task<FolderPathSnapshot> CaptureDirectChildSnapshotAsync(string directoryPath)
    {
        return Task.Run(() =>
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(directoryPath);
                var paths = Directory.EnumerateFileSystemEntries(normalizedRoot)
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new FolderPathSnapshot(
                    paths.Count == 0
                        ? FolderSnapshotStatus.SuccessEmpty
                        : FolderSnapshotStatus.SuccessWithItems,
                    paths);
            }
            catch (UnauthorizedAccessException)
            {
                return new FolderPathSnapshot(
                    FolderSnapshotStatus.AccessDenied,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            catch (System.Security.SecurityException)
            {
                return new FolderPathSnapshot(
                    FolderSnapshotStatus.AccessDenied,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return new FolderPathSnapshot(
                    FolderSnapshotStatus.Unavailable,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        });
    }

    internal static FolderEntryRefreshStatus ClassifyDirectChild(
        FolderPathSnapshot snapshot,
        string path)
    {
        if (!FolderSnapshotStatusPolicy.IsSuccessful(snapshot.Status))
        {
            return FolderEntryRefreshStatus.Unavailable;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            return FolderEntryRefreshStatus.Unavailable;
        }

        if (!snapshot.Paths.Contains(normalizedPath))
        {
            return FolderEntryRefreshStatus.NotFound;
        }

        try
        {
            string name = Path.GetFileName(normalizedPath);
            System.IO.FileAttributes attributes = File.GetAttributes(normalizedPath);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                (attributes & System.IO.FileAttributes.Hidden) != 0 ||
                (Path.GetExtension(normalizedPath).Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                 IsDeadSteamShortcut(normalizedPath)))
            {
                return FolderEntryRefreshStatus.Filtered;
            }

            return FolderEntryRefreshStatus.Available;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderEntryRefreshStatus.AccessDenied;
        }
        catch (System.Security.SecurityException)
        {
            return FolderEntryRefreshStatus.AccessDenied;
        }
        catch (IOException)
        {
            // A path that was present in a complete parent enumeration but cannot
            // now be read is a race/provider failure, not proof of deletion.
            return FolderEntryRefreshStatus.Unavailable;
        }
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
        bool loadFolderItemCount = true,
        bool loadShortcutTarget = true)
    {
        FileSystemEntrySnapshot entry = await Task.Run(
            () => CaptureEntrySnapshot(path, loadFolderItemCount));
        return await CreateWidgetItemAsync(
            entry,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon,
            loadShortcutTarget);
    }

    private async Task<WidgetItem> CreateWidgetItemAsync(
        FileSystemEntrySnapshot entry,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool showFileExtensions = false,
        bool hideShortcutExtensionWhenShowingFileExtensions = true,
        bool loadIcon = true,
        bool loadShortcutTarget = true)
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
            TargetPath = entry.IsShortcut ? string.Empty : entry.Path
        };

        if (item.IsShortcut && loadShortcutTarget)
        {
            item.TargetPath = await GetStoredShortcutTargetAsync(entry.Path);
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
        bool loadFolderItemCount = true,
        bool loadShortcutTarget = true)
    {
        FileSystemEntrySnapshot? entry = await Task.Run(() =>
            ShouldDisplayEntry(path)
                ? CaptureEntrySnapshot(path, loadFolderItemCount)
                : null);
        if (entry is null)
        {
            return null;
        }

        return await CreateWidgetItemAsync(
            entry,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon,
            loadShortcutTarget);
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

        // Filter out dead Steam game shortcuts (game uninstalled but .url remains).
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase) &&
            IsDeadSteamShortcut(path))
        {
            return null;
        }

        return CaptureEntrySnapshot(path, loadFolderItemCount);
    }

    private static FileSystemEntrySnapshot CaptureEntrySnapshot(
        string path,
        bool loadFolderItemCount)
    {
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
        bool showImageFilesAsIcons = false,
        int decodePixelWidth = 0)
    {
        return IconHelper.GetIconAsync(
            path,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            decodePixelWidth);
    }

    public void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false)
    {
        IconHelper.ClearIconCache(path, hideShortcutArrowOverlay, showImageFilesAsIcons);
    }

    public async Task<string> GetStoredShortcutTargetAsync(string shortcutPath)
    {
        BoundedBackgroundWorkResult<string> result =
            await BoundedBackgroundWorkScheduler.SharedShell.RunAsync(
                () => ShortcutHelper.ReadStoredMetadata(shortcutPath)?.TargetPath ??
                    string.Empty,
                ShortcutMetadataTimeout);
        if (result.Status == BoundedBackgroundWorkStatus.Completed)
        {
            return result.Value ?? string.Empty;
        }

        if (result.Status == BoundedBackgroundWorkStatus.ExecutionTimedOut)
        {
            App.Log(
                $"[FileService] Shortcut target read timed out " +
                $"timeoutMs={ShortcutMetadataTimeout.TotalMilliseconds:0} " +
                $"path={shortcutPath}");
        }
        else if (result.Exception is not null)
        {
            App.Log(
                $"[FileService] Shortcut target read failed " +
                $"path={shortcutPath}: {result.Exception.Message}");
        }

        return string.Empty;
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

        if (await Task.Run(() => Directory.Exists(path)))
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
        CacheShellKind(path, kind);
        return kind;
    }

    internal static int ShellKindCacheEntryCount => s_shellKindCache.Count;

    internal static void CacheShellKind(string path, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(kind);

        lock (s_shellKindCacheGate)
        {
            if (!s_shellKindCache.TryAdd(path, kind))
            {
                return;
            }

            s_shellKindCacheOrder.Enqueue(path);
            while (s_shellKindCache.Count > MaxShellKindCacheEntries &&
                   s_shellKindCacheOrder.TryDequeue(out string? oldestPath))
            {
                s_shellKindCache.TryRemove(oldestPath, out _);
            }
        }
    }

    internal static void ClearShellKindCache()
    {
        lock (s_shellKindCacheGate)
        {
            s_shellKindCache.Clear();
            s_shellKindCacheOrder.Clear();
        }
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
    public async Task<IReadOnlyList<FileTransferResult>> TransferItemsWithResultAsync(
        IEnumerable<string> sourcePaths,
        string destinationFolder,
        bool move,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        // Directory.Exists/File.Exists can block for a disconnected UNC or
        // network provider. Keep all planning and probing off the UI thread.
        var plans = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedDestinationFolder = Path.GetFullPath(destinationFolder);
            var normalizedSourcePaths = sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            EnsureSafeDirectoryTransfers(normalizedSourcePaths.Select(path =>
                new TransferOperation(path, normalizedDestinationFolder)));

            if (!Directory.Exists(normalizedDestinationFolder))
            {
                Directory.CreateDirectory(normalizedDestinationFolder);
            }

            var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return normalizedSourcePaths
                .Where(path =>
                    (File.Exists(path) || Directory.Exists(path)) &&
                    !string.Equals(Path.GetDirectoryName(path), normalizedDestinationFolder, StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileTransferPlan(
                    path,
                    GetAvailablePath(Path.Combine(normalizedDestinationFolder, Path.GetFileName(path)), reservedPaths)))
                .ToList();
        }, cancellationToken);

        return await ExecuteTransferPlanAsync(
            plans,
            move,
            useShellProgress: useShellProgress,
            ownerWindowHandle: ownerWindowHandle,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Execute a precomputed transfer plan and return the realized destination paths.
    /// </summary>
    public async Task<IReadOnlyList<FileTransferResult>> ExecuteTransferPlanAsync(
        IEnumerable<FileTransferPlan> plans,
        bool move,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operations = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plannedOperations = plans
                .Where(plan => !string.IsNullOrWhiteSpace(plan.SourcePath) && !string.IsNullOrWhiteSpace(plan.DestinationPath))
                .Select(plan =>
                {
                    string sourcePath = Path.GetFullPath(plan.SourcePath);
                    return new TransferOperation(
                        sourcePath,
                        Path.GetFullPath(plan.DestinationPath),
                        Directory.Exists(sourcePath));
                })
                .Where(operation =>
                    (File.Exists(operation.SourcePath) || Directory.Exists(operation.SourcePath)) &&
                    !string.Equals(operation.SourcePath, operation.DestinationPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            EnsureSafeDirectoryTransfers(plannedOperations);
            return plannedOperations;
        }, cancellationToken);

        using FileTransferSessionLease transferSession =
            TransferSessions.Begin(
                operations.Select(operation => new FileTransferRegistration(
                    operation.SourcePath,
                    operation.DestinationPath,
                    operation.SourceIsDirectory)),
                move);

        if (useShellProgress)
        {
#if !DESKBOX_NATIVE_AOT
            // Interactive imports use the modern Windows Shell operation on a
            // dedicated STA thread. It owns enumeration, conflicts, errors,
            // cancellation and the native progress window for both copy and
            // move operations.
            return await ExecuteModernShellTransferPlanAsync(
                operations,
                move,
                ownerWindowHandle,
                progress,
                cancellationToken);
#else
            // The staged Native AOT profile still uses the validated legacy
            // move bridge. Copy operations fall through to the safe managed
            // engine until a source-generated/native Shell bridge is gated.
            if (move)
            {
                return await ExecuteShellMovePlanAsync(
                    operations,
                    ownerWindowHandle);
            }
#endif
        }

        if (progress is not null || cancellationToken.CanBeCanceled)
        {
            // Keep synchronous filesystem probes, partial-file cleanup and
            // rollback off the caller's synchronization context. In the UI the
            // progress callback marshals updates back through DispatcherQueue.
            return await Task.Run(
                () => ExecuteManagedTransferPlanWithProgressAsync(
                    operations,
                    move,
                    progress,
                    cancellationToken),
                CancellationToken.None);
        }

        if (move && operations.Any(operation => !CanUseAtomicMove(
                operation.SourcePath,
                operation.DestinationPath)))
        {
            // Headless callers such as desktop organization and storage
            // migration still need to avoid File.Move's opaque cross-volume
            // copy. They may not display byte progress, but the transfer stays
            // on DeskBox's chunked, logged and rollback-safe implementation.
            return await Task.Run(
                () => ExecuteManagedTransferPlanWithProgressAsync(
                    operations,
                    move: true,
                    progress: null,
                    CancellationToken.None),
                CancellationToken.None);
        }

        var completedOperations = new List<TransferOperation>(operations.Count);
        try
        {
            foreach (var operation in operations)
            {
                // Re-resolve both paths immediately before the filesystem
                // operation. This closes the common check-then-replace window
                // where a junction or SUBST alias is swapped during a drag.
                await Task.Run(() => EnsureSafeDirectoryTransfers([operation]));
                if (move)
                {
                    await Task.Run(() => MoveEntryAsync(
                        operation.SourcePath,
                        operation.DestinationPath));
                }
                else
                {
                    await Task.Run(() => CopyEntryAsync(
                        operation.SourcePath,
                        operation.DestinationPath));
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

    private async Task<IReadOnlyList<FileTransferResult>> ExecuteShellMovePlanAsync(
        IReadOnlyList<TransferOperation> operations,
        IntPtr ownerWindowHandle)
    {
        if (operations.Count == 0)
        {
            return [];
        }

        foreach (var operation in operations)
        {
            await Task.Run(() => EnsureSafeDirectoryTransfers([operation]));
            string? destinationDirectory = Path.GetDirectoryName(operation.DestinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                await Task.Run(() => Directory.CreateDirectory(destinationDirectory));
            }
        }

        var stopwatch = Stopwatch.StartNew();
        FileTransferPlan[] shellPlans = operations
            .Select(operation => new FileTransferPlan(
                operation.SourcePath,
                operation.DestinationPath))
            .ToArray();
        TimeSpan recoveryProbeDelay = ShellMoveRecoveryProbeDelay;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        recoveryProbeDelay = AotShellMoveFixture.GetRecoveryProbeDelay(
            shellPlans,
            recoveryProbeDelay);
#endif
        App.Log(
            $"[FileTransfer] Shell move start count={operations.Count} " +
            $"owner=0x{ownerWindowHandle.ToInt64():X}");

        Task shellMoveTask = Task.Run(() =>
        {
            EnsureSafeDirectoryTransfers(operations);
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            if (AotShellMoveFixture.TryExecute(
                    shellPlans,
                    ownerWindowHandle,
                    () => MoveEntriesWithShellProgress(
                        operations,
                        ownerWindowHandle)))
            {
                return;
            }
#endif
            MoveEntriesWithShellProgress(
                operations,
                ownerWindowHandle);
        });

        Task firstCompletion = await Task.WhenAny(
            shellMoveTask,
            Task.Delay(recoveryProbeDelay));
        if (ReferenceEquals(firstCompletion, shellMoveTask))
        {
            await shellMoveTask;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
            AotShellMoveFixture.RecordFileServiceOutcome(
                shellPlans,
                AotShellMoveFixture.ReturnedOutcome);
#endif
            App.Log(
                $"[FileTransfer] Shell move returned count={operations.Count} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        else
        {
            bool allCompleted = await Task.Run(() =>
                AreAllShellMovesCompleted(operations.Select(operation =>
                    new FileTransferPlan(
                        operation.SourcePath,
                        operation.DestinationPath))));
            if (allCompleted)
            {
                // Some shell extensions finish the filesystem move but leave
                // SHFileOperation waiting on hidden bookkeeping/UI. The import
                // must not keep covering the widget once every requested move
                // is already complete. Observe the late task so any eventual
                // failure is logged instead of becoming unobserved.
                App.Log(
                    $"[FileTransfer] Shell move recovered from pending call " +
                    $"count={operations.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
                AotShellMoveFixture.RecordFileServiceOutcome(
                    shellPlans,
                    AotShellMoveFixture.RecoveredPendingOutcome);
#endif
                _ = ObserveLateShellMoveCompletionAsync(
                    shellMoveTask,
                    operations.Count,
                    stopwatch);
            }
            else
            {
                App.Log(
                    $"[FileTransfer] Shell move still active count={operations.Count} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} " +
                    $"owner=0x{ownerWindowHandle.ToInt64():X}");
                await shellMoveTask;
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
                AotShellMoveFixture.RecordFileServiceOutcome(
                    shellPlans,
                    AotShellMoveFixture.ExtendedWaitOutcome);
#endif
                App.Log(
                    $"[FileTransfer] Shell move returned after extended wait " +
                    $"count={operations.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
        }

        return await Task.Run(() => operations
            .Where(operation => IsCompletedShellMove(
                operation.SourcePath,
                operation.DestinationPath))
            .Select(operation => new FileTransferResult(operation.SourcePath, operation.DestinationPath))
            .ToList());
    }

    private static async Task ObserveLateShellMoveCompletionAsync(
        Task shellMoveTask,
        int operationCount,
        Stopwatch stopwatch)
    {
        try
        {
            await shellMoveTask.ConfigureAwait(false);
            App.Log(
                $"[FileTransfer] Pending shell move call eventually returned " +
                $"count={operationCount} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FileTransfer] Pending shell move call failed after filesystem " +
                $"completion count={operationCount} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}: {ex}");
        }
    }

    internal static bool IsCompletedShellMove(string sourcePath, string destinationPath)
    {
        return (File.Exists(destinationPath) || Directory.Exists(destinationPath)) &&
               !File.Exists(sourcePath) &&
               !Directory.Exists(sourcePath);
    }

    internal static bool AreAllShellMovesCompleted(
        IEnumerable<FileTransferPlan> plans)
    {
        FileTransferPlan[] materialized = plans.ToArray();
        return materialized.Length > 0 &&
               materialized.All(plan => IsCompletedShellMove(
                   plan.SourcePath,
                   plan.DestinationPath));
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

    /// <summary>
    /// Renames one file-system entry without falling back to a copy or a
    /// child-by-child move. A rename must either complete atomically or leave
    /// the source and destination untouched.
    /// </summary>
    public async Task RenameEntryAsync(string sourcePath, string destinationPath)
    {
        string normalizedSource = Path.GetFullPath(sourcePath);
        string normalizedDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(normalizedSource, normalizedDestination, StringComparison.Ordinal))
        {
            return;
        }

        if (IsCaseOnlyPathChange(normalizedSource, normalizedDestination))
        {
            await MoveCaseOnlyEntryAsync(normalizedSource, normalizedDestination);
            return;
        }

        EnsureSafeDirectoryTransfers([new TransferOperation(normalizedSource, normalizedDestination)]);

        await MoveEntryAtomicallyAsync(normalizedSource, normalizedDestination);
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

    private static void MoveEntriesWithShellProgress(
        IReadOnlyList<TransferOperation> operations,
        IntPtr ownerWindowHandle)
    {
        if (TryMoveEntriesToSameFolderWithShellProgress(
                operations,
                ownerWindowHandle))
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
                        WindowHandle = ownerWindowHandle,
                        Function = FoMove,
                        From = fromPointer,
                        To = toPointer,
                        Flags = FofNoConfirmMkDir |
                                FofNoConfirmation |
                                FofNoErrorUi
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

    private static bool TryMoveEntriesToSameFolderWithShellProgress(
        IReadOnlyList<TransferOperation> operations,
        IntPtr ownerWindowHandle)
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
                    WindowHandle = ownerWindowHandle,
                    Function = FoMove,
                    From = fromPointer,
                    To = toPointer,
                    Flags = FofNoConfirmMkDir |
                            FofNoConfirmation |
                            FofNoErrorUi
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

        EnsureSafeDirectoryTransfers([new TransferOperation(normalizedSource, normalizedDestination)]);

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
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

        if (string.Equals(normalizedCandidate, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar) ||
                        normalizedDirectory.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool PathsOverlap(string firstPath, string secondPath)
    {
        if (!TryResolvePathIdentity(firstPath, out string first) ||
            !TryResolvePathIdentity(secondPath, out string second))
        {
            // This predicate is also used for validating not-yet-created
            // mapping roots.  Keep its historical lexical behavior for paths
            // whose provider cannot expose an identity; actual directory
            // transfers use IsPathUnderDirectoryResolved below, which fails
            // closed instead.
            try
            {
                return IsPathUnderDirectory(firstPath, secondPath) ||
                       IsPathUnderDirectory(secondPath, firstPath);
            }
            catch
            {
                return true;
            }
        }

        return IsPathUnderDirectory(first, second) ||
               IsPathUnderDirectory(second, first);
    }

    public static bool IsPathUnderDirectoryResolved(string candidatePath, string directoryPath)
    {
        if (!TryResolvePathIdentity(candidatePath, out string candidate) ||
            !TryResolvePathIdentity(directoryPath, out string directory))
        {
            // Fail closed for directory safety checks.  Returning false here
            // would allow an operation whose real filesystem identity could
            // not be verified.
            return true;
        }

        return IsPathUnderDirectory(
            candidate,
            directory);
    }

    public static bool TryIsPathUnderDirectoryResolved(
        string candidatePath,
        string directoryPath,
        out bool isUnderDirectory)
    {
        isUnderDirectory = false;
        if (!TryResolvePathIdentity(candidatePath, out string candidate) ||
            !TryResolvePathIdentity(directoryPath, out string directory))
        {
            return false;
        }

        isUnderDirectory = IsPathUnderDirectory(candidate, directory);
        return true;
    }

    /// <summary>
    /// Resolves existing junctions and symbolic-link directories before doing
    /// overlap checks.  A lexical comparison alone treats a junction target as
    /// unrelated, which can let a mapped widget point into another mapped
    /// widget and recreate the recursive nesting bug.
    /// <para>
    /// The final destination may not exist yet, so only existing path segments
    /// are resolved; the non-existing suffix is preserved for the normal
    /// separator-aware comparison.
    /// </para>
    /// </summary>
    private static bool TryResolvePathIdentity(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if ((Directory.Exists(fullPath) || File.Exists(fullPath)) &&
            Win32Helper.TryGetFinalPath(fullPath, out string finalPath))
        {
            resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalPath));
            return true;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string current = root;
        string remainder = fullPath[root.Length..];
        string[] segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string candidate = Path.Combine(current, segments[index]);
            bool exists = Directory.Exists(candidate) || File.Exists(candidate);
            if (!exists)
            {
                // The rest is a not-yet-created destination suffix.  The
                // existing prefix has already had every reparse point
                // resolved, so appending the suffix is identity-safe.
                for (int suffixIndex = index; suffixIndex < segments.Length; suffixIndex++)
                {
                    current = Path.Combine(current, segments[suffixIndex]);
                }

                resolvedPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(current));
                return true;
            }

            try
            {
                FileSystemInfo info = Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : new FileInfo(candidate);
                if ((info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null)
                    {
                        return false;
                    }

                    current = target.FullName;
                }
                else
                {
                    current = candidate;
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                return false;
            }
        }

        resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return true;
    }

    private void EnsureSafeDirectoryTransfers(IEnumerable<TransferOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (!Directory.Exists(operation.SourcePath))
            {
                continue;
            }

            // IsPathUnderDirectoryResolved deliberately returns true when an
            // identity cannot be verified, so a directory transfer never
            // falls back to a lexical-only safety decision.
            if (IsPathUnderDirectoryResolved(operation.DestinationPath, operation.SourcePath))
            {
                throw new InvalidOperationException(
                    _localizationService?.T("Widget.Error.UnsafeFolderTransfer") ??
                    UnsafeFolderTransferFallbackMessage);
            }
        }
    }

    private static async Task RollbackTransfersAsync(IEnumerable<TransferOperation> completedOperations, bool move)
    {
        TransferOperation[] rollbackOperations = completedOperations
            .Reverse()
            .ToArray();
        var reporter = new TransferProgressReporter(
            progress: null,
            totalItems: rollbackOperations.Length);
        foreach (var operation in rollbackOperations)
        {
            try
            {
                if (move)
                {
                    await MoveEntryWithProgressAsync(
                        operation.DestinationPath,
                        operation.SourcePath,
                        estimate: null,
                        reporter,
                        CancellationToken.None);
                }
                else
                {
                    await Task.Run(() => DeleteEntryAsync(operation.DestinationPath));
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

        // Keep the source tree intact until a complete destination tree exists.
        // A recursive child-by-child move can leave an untracked split tree if
        // deleting a source directory fails after its children were moved.
        await CopyDirectoryAsync(sourceDirectory, destinationDirectory);
        try
        {
            await Task.Run(
                () => Directory.Delete(sourceDirectory, recursive: true));
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException)
        {
            App.Log(
                $"[FileTransfer] Directory copy completed but source cleanup " +
                $"failed source='{sourceDirectory}' " +
                $"destination='{destinationDirectory}': {ex}");
            throw new FileTransferSourceCleanupException(
                sourceDirectory,
                destinationDirectory,
                ex);
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
    public static OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)
    {
        try
        {
            if (ShortcutHelper.IsShellLinkPath(item.Path) &&
                string.IsNullOrWhiteSpace(item.TargetPath))
            {
                item.TargetPath = ShortcutHelper.ReadStoredMetadata(item.Path)?.TargetPath ??
                    string.Empty;
            }

            if (ShortcutHelper.IsShellLinkPath(item.Path) && IsBrokenShortcut(item))
            {
                var resolution = ShortcutHelper.ResolveBrokenShortcutWithShellUi(item.Path, ownerHwnd);
                return resolution == BrokenShortcutResolution.ShortcutDeleted
                    ? OpenItemResult.ShortcutDeleted
                    : OpenItemResult.OpenedOrHandled;
            }

            var pathToOpen = item.IsShortcut ? item.Path : item.TargetPath;
            if (string.IsNullOrEmpty(pathToOpen))
            {
                return OpenItemResult.Failed;
            }

            // Forward the real owner hwnd so any system UI (Open With / UAC) is parented to
            // the widget instead of IntPtr.Zero, which previously left dialogs hidden behind
            // the topmost widget. Returns Failed instead of swallowing the result so the
            // caller can surface the failure to the user.
            return Win32Helper.OpenFile(ownerHwnd, pathToOpen)
                ? OpenItemResult.OpenedOrHandled
                : OpenItemResult.Failed;
        }
        catch (Exception ex)
        {
            App.Log($"[OpenItem] Unexpected failure path='{item.Path}' target='{item.TargetPath}': {ex}");
            return OpenItemResult.Failed;
        }
    }

    private static bool IsBrokenShortcut(WidgetItem item)
    {
        if (!File.Exists(item.Path))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(item.TargetPath))
        {
            // Advertised MSI shortcuts and shell-namespace links may not expose a
            // filesystem target through IShellLink.GetPath. ShellExecute can still
            // open them, so an empty target is not enough evidence that the link is
            // broken.
            return false;
        }

        string expandedTarget = Environment.ExpandEnvironmentVariables(item.TargetPath);
        if (!Path.IsPathFullyQualified(expandedTarget))
        {
            return false;
        }

        return !File.Exists(expandedTarget) && !Directory.Exists(expandedTarget);
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
        ShortcutDeleted,
        Failed
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

    // ─── Steam dead-shortcut detection ───────────────────────────────────

    private static readonly object s_steamLibLock = new();
    private static string[]? s_steamLibraryPaths;
    private static DateTime s_steamLibCacheTime;
    private static readonly TimeSpan SteamLibCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Checks whether a .url file is a Steam game shortcut whose game is no longer installed.
    /// Returns true if the shortcut should be filtered out (dead link).
    /// </summary>
    private static bool IsDeadSteamShortcut(string urlFilePath)
    {
        try
        {
            // Quick read: only parse if the file contains "steam://rungameid/"
            string content = File.ReadAllText(urlFilePath);
            int idx = content.IndexOf("steam://rungameid/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false; // Not a Steam game shortcut
            }

            // Extract the app ID
            int idStart = idx + "steam://rungameid/".Length;
            int idEnd = idStart;
            while (idEnd < content.Length && char.IsDigit(content[idEnd]))
            {
                idEnd++;
            }

            if (idEnd == idStart)
            {
                return false; // No valid app ID found
            }

            string appId = content[idStart..idEnd];
            return !IsSteamGameInstalled(appId);
        }
        catch
        {
            return false; // On any error, keep the shortcut visible
        }
    }

    /// <summary>
    /// Checks if a Steam game is installed by looking for its appmanifest file
    /// in all known Steam library folders.
    /// </summary>
    private static bool IsSteamGameInstalled(string appId)
    {
        var libraryPaths = GetSteamLibraryPaths();
        if (libraryPaths.Length == 0)
        {
            return true; // Can't determine Steam location, assume installed
        }

        foreach (var libPath in libraryPaths)
        {
            string manifestPath = Path.Combine(libPath, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(manifestPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all Steam library folder paths (cached for 5 minutes).
    /// Reads from the registry and libraryfolders.vdf.
    /// </summary>
    private static string[] GetSteamLibraryPaths()
    {
        lock (s_steamLibLock)
        {
            if (s_steamLibraryPaths is not null &&
                DateTime.UtcNow - s_steamLibCacheTime < SteamLibCacheDuration)
            {
                return s_steamLibraryPaths;
            }

            var paths = new List<string>();

            try
            {
                // Get Steam install path from registry
                string? steamPath = null;
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string sp)
                {
                    steamPath = sp.Replace('/', '\\');
                }

                if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
                {
                    s_steamLibraryPaths = [];
                    s_steamLibCacheTime = DateTime.UtcNow;
                    return s_steamLibraryPaths;
                }

                // The default library is always the Steam install directory
                paths.Add(steamPath);

                // Parse libraryfolders.vdf for additional library folders
                string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    foreach (string line in File.ReadLines(vdfPath))
                    {
                        string trimmed = line.Trim();
                        // Look for "path" entries: "path" "D:\\SteamLibrary"
                        if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            int firstQuote = trimmed.IndexOf('"', 7);
                            int secondQuote = trimmed.IndexOf('"', firstQuote + 1);
                            if (firstQuote >= 0 && secondQuote > firstQuote)
                            {
                                string libPath = trimmed[(firstQuote + 1)..secondQuote]
                                    .Replace("\\\\", "\\");
                                if (Directory.Exists(libPath) &&
                                    !paths.Contains(libPath, StringComparer.OrdinalIgnoreCase))
                                {
                                    paths.Add(libPath);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[FileService] Failed to enumerate Steam libraries: {ex.Message}");
            }

            s_steamLibraryPaths = paths.ToArray();
            s_steamLibCacheTime = DateTime.UtcNow;
            return s_steamLibraryPaths;
        }
    }
}
