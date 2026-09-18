using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
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

internal enum SteamGameInstallState
{
    Installed,
    NotInstalled,
    Unknown
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
        int? FolderItemCount,
        bool IsFiltered = false);

    /// <summary>
    /// A source file that a directory move copied to the destination, with
    /// its size and write time captured BEFORE the copy started. Cleanup may
    /// only delete files whose current state still matches this record; a
    /// mismatch means someone wrote to the source mid-move and aborts the
    /// cleanup before anything is deleted.
    /// </summary>
    internal readonly record struct CopiedSourceFileRecord(
        string SourceFilePath,
        long Length,
        DateTime LastWriteTimeUtc,
        FileTransferSourceIdentity? Identity);

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

    public sealed class FileTransferSourceChangedException : IOException,
        IFileTransferWithCompletedResults
    {
        internal FileTransferSourceChangedException(
            string sourcePath,
            string destinationPath)
            : base(
                "The source folder changed while its files were being " +
                "copied. Nothing was deleted; both copies were kept to " +
                "protect the data.")
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

        if (!TryResolveExistingPathForTraversal(
                directoryPath,
                out string normalizedDirectoryPath))
        {
            return items;
        }

        var entries = await Task.Run(() => EnumerateEntrySnapshots(
            normalizedDirectoryPath,
            loadFolderItemCounts));

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
        if (!TryResolveExistingPathForTraversal(directoryPath, out string normalizedRoot))
        {
            return new FolderEnumerationResult(FolderSnapshotStatus.Unavailable, []);
        }

        // One directory-stream pass returns names, attributes, sizes, and
        // timestamps together on NTFS. The previous implementation listed
        // bare paths first and then re-stat'ed every entry five to six times,
        // which dominated large-folder load time (measured ~226 ms for 2088
        // items, roughly 0.1 ms per item).
        (List<FileSystemEntrySnapshot> Entries, FolderSnapshotStatus Status) enumeration =
            await Task.Run(() => EnumerateSnapshotsFromDirectoryStream(
                normalizedRoot,
                loadFolderItemCounts));
        if (!FolderSnapshotStatusPolicy.IsSuccessful(enumeration.Status))
        {
            return new FolderEnumerationResult(enumeration.Status, []);
        }

        // Stability pass, same contract as before: entries that changed
        // between the two passes mark the view partial instead of silently
        // showing a torn snapshot. Both sets are unfiltered; hidden entries
        // participate in the comparison and are dropped when items build.
        FolderPathSnapshot after = await CaptureDirectChildSnapshotAsync(directoryPath);
        bool partial =
            !FolderSnapshotStatusPolicy.IsSuccessful(after.Status) ||
            !enumeration.Entries.Select(entry => entry.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(after.Paths);

        var items = new List<WidgetItem>(enumeration.Entries.Count);
        int sortOrder = 0;
        foreach (FileSystemEntrySnapshot entry in enumeration.Entries
                     .Where(entry => !entry.IsFiltered)
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

    /// <summary>
    /// Materializes direct-child snapshots from a single NTFS directory
    /// stream. The stream already carries attributes, sizes, and timestamps,
    /// so no per-entry re-stat is needed for files. Folders keep the previous
    /// per-folder stat behavior (junction-resolved timestamps and, when
    /// requested, child counts), which is why they are re-read here — folders
    /// are a small minority of a large directory.
    /// </summary>
    private static (List<FileSystemEntrySnapshot> Entries, FolderSnapshotStatus Status)
        EnumerateSnapshotsFromDirectoryStream(
            string normalizedRoot,
            bool loadFolderItemCounts)
    {
        var entries = new List<FileSystemEntrySnapshot>();
        try
        {
            var enumerable = new FileSystemEnumerable<FileSystemEntrySnapshot>(
                normalizedRoot,
                (ref FileSystemEntry entry) =>
                {
                    bool isFolder = entry.IsDirectory;
                    string fullPath = entry.ToFullPath();
                    string fileName = entry.FileName.ToString();
                    // Hidden entries and desktop.ini stay in the snapshot so
                    // the stability pass compares like-for-like path sets
                    // (the plain re-enumeration does not filter); they are
                    // dropped when the visible item list is built.
                    // Stale Steam game shortcuts (.url whose game is
                    // uninstalled) are deliberately NOT filtered: opening
                    // them launches Steam's install flow, so they remain
                    // useful and must stay visible in the box.
                    bool isFiltered =
                        entry.Attributes.HasFlag(System.IO.FileAttributes.Hidden) ||
                        entry.FileName.Equals(
                            "desktop.ini",
                            StringComparison.OrdinalIgnoreCase);
                    return new FileSystemEntrySnapshot(
                        fullPath,
                        isFolder
                            ? fileName
                            : Path.GetFileNameWithoutExtension(fileName),
                        isFolder,
                        ShortcutHelper.IsShortcutPath(fullPath),
                        isFolder ? null : entry.Length,
                        entry.CreationTimeUtc.LocalDateTime,
                        entry.LastWriteTimeUtc.LocalDateTime,
                        null,
                        isFiltered);
                },
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    // An unreadable entry is tolerated; the stability pass
                    // still reports it as reduced visibility via Partial.
                    IgnoreInaccessible = true,
                    AttributesToSkip = System.IO.FileAttributes.None,
                    ReturnSpecialDirectories = false,
                });

            foreach (FileSystemEntrySnapshot entry in enumerable)
            {
                entries.Add(entry);
            }

            for (int index = 0; index < entries.Count; index++)
            {
                FileSystemEntrySnapshot entry = entries[index];
                if (!entry.IsFolder || entry.IsFiltered)
                {
                    continue;
                }

                string folderAccessPath = entry.Path;
                _ = TryResolveExistingPathForTraversal(entry.Path, out folderAccessPath);
                try
                {
                    int? folderItemCount = loadFolderItemCounts
                        ? CountVisibleChildren(folderAccessPath)
                        : null;
                    entries[index] = entry with
                    {
                        FolderItemCount = folderItemCount,
                        CreatedAt = Directory.GetCreationTime(folderAccessPath),
                        LastModified = Directory.GetLastWriteTime(folderAccessPath),
                    };
                }
                catch
                {
                    entries[index] = entry with
                    {
                        FolderItemCount = loadFolderItemCounts ? 0 : null,
                    };
                }
            }

            return (entries, FolderSnapshotStatus.SuccessWithItems);
        }
        catch (UnauthorizedAccessException)
        {
            return (entries, FolderSnapshotStatus.AccessDenied);
        }
        catch (System.Security.SecurityException)
        {
            return (entries, FolderSnapshotStatus.AccessDenied);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return (entries, FolderSnapshotStatus.Unavailable);
        }
    }

    internal static Task<FolderPathSnapshot> CaptureDirectChildSnapshotAsync(string directoryPath)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!TryResolveExistingPathForTraversal(
                        directoryPath,
                        out string normalizedRoot))
                {
                    return new FolderPathSnapshot(
                        FolderSnapshotStatus.Unavailable,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

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
            // Stale Steam game shortcuts (.url whose game is uninstalled) are
            // deliberately NOT filtered: opening them launches Steam's install
            // flow, so they remain useful and must stay visible in the box.
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                (attributes & System.IO.FileAttributes.Hidden) != 0)
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
            item.Icon = await GetIconAsync(
                entry.Path,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons);
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
            string folderAccessPath = path;
            _ = TryResolveExistingPathForTraversal(path, out folderAccessPath);
            try
            {
                if (loadFolderItemCount)
                {
                    folderItemCount = CountVisibleChildren(folderAccessPath);
                }

                createdAt = Directory.GetCreationTime(folderAccessPath);
                lastModified = Directory.GetLastWriteTime(folderAccessPath);
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
        string iconPath = path;
        if (!ShortcutHelper.IsShortcutPath(path) &&
            TryResolveExistingPathForTraversal(path, out string resolvedPath))
        {
            iconPath = resolvedPath;
        }

        return IconHelper.GetIconAsync(
            iconPath,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            decodePixelWidth);
    }

    public void ClearIconCache(
        string path,
        bool hideShortcutArrowOverlay = false,
        bool showImageFilesAsIcons = false,
        bool resetTransientFailures = true)
    {
        IconHelper.ClearIconCache(
            path,
            hideShortcutArrowOverlay,
            showImageFilesAsIcons,
            resetTransientFailures);
        if (!ShortcutHelper.IsShortcutPath(path) &&
            TryResolveExistingPathForTraversal(path, out string resolvedPath) &&
            !string.Equals(path, resolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            IconHelper.ClearIconCache(
                resolvedPath,
                hideShortcutArrowOverlay,
                showImageFilesAsIcons,
                resetTransientFailures);
        }
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

        if (!item.IsShortcut &&
            TryResolveExistingPathForTraversal(path, out string resolvedPath))
        {
            path = resolvedPath;
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
        _ = ReleaseHiddenShellKindCache();
    }

    internal static int ReleaseHiddenShellKindCache()
    {
        lock (s_shellKindCacheGate)
        {
            int released = s_shellKindCache.Count;
            s_shellKindCache.Clear();
            s_shellKindCacheOrder.Clear();
            return released;
        }
    }

    public Task<int> CountVisibleChildrenAsync(string folderPath)
    {
        return Task.Run(() => CountVisibleChildren(folderPath));
    }

    private static int CountVisibleChildren(string folderPath)
    {
        if (!TryResolveExistingPathForTraversal(
                folderPath,
                out string resolvedFolderPath))
        {
            throw new DirectoryNotFoundException(folderPath);
        }

        return Directory.EnumerateFileSystemEntries(resolvedFolderPath)
            .Count(ShouldDisplayEntry);
    }

    private static void TryApplyFolderLastModified(WidgetItem item, string path)
    {
        try
        {
            string accessPath = TryResolveExistingPathForTraversal(
                    path,
                    out string resolvedPath)
                ? resolvedPath
                : path;
            item.LastModified = Directory.GetLastWriteTime(accessPath);
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
                string accessPath = ResolveStorageAccessPath(path);
                if (Directory.Exists(accessPath))
                {
                    var folder = await TryGetStorageFolderAsync(accessPath);
                    if (folder is not null)
                    {
                        items.Add(folder);
                    }
                }
                else if (File.Exists(accessPath))
                {
                    var file = await TryGetStorageFileAsync(accessPath);
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
                string accessPath = ResolveStorageAccessPath(path);
                if (Directory.Exists(accessPath))
                {
                    var folder = TryGetStorageFolder(accessPath);
                    if (folder is not null)
                    {
                        items.Add(folder);
                    }
                }
                else if (File.Exists(accessPath))
                {
                    var file = TryGetStorageFile(accessPath);
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

    private static string ResolveStorageAccessPath(string path)
    {
        if (!ShortcutHelper.IsShortcutPath(path) &&
            TryResolveExistingPathForTraversal(path, out string resolvedPath))
        {
            return resolvedPath;
        }

        return path;
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
    /// <summary>
    /// Serializes the strip/restore read-modify-write pairs across threads.
    /// Without the gate, a second stripper can read the already-stripped
    /// state as the "original" and restore it over the first one's restore,
    /// permanently dropping the file's Hidden/System attributes.
    /// </summary>
    private static readonly object s_attributeStripGate = new();

    internal static System.IO.FileAttributes? StripBlockingAttributes(string path)
    {
        try
        {
            lock (s_attributeStripGate)
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
        }
        catch
        {
            return null;
        }
    }

    internal static void RestoreAttributes(string path, System.IO.FileAttributes? original)
    {
        if (original is null)
        {
            return;
        }

        try
        {
            lock (s_attributeStripGate)
            {
                File.SetAttributes(path, original.Value);
            }
        }
        catch (Exception ex)
        {
            // Best-effort restore; the file may have been moved/deleted.
            App.Log(
                $"[StorageItems] Failed to restore attributes on '{path}': " +
                $"{ex.Message}");
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

            List<string> transferableSourcePaths = normalizedSourcePaths
                .Where(path =>
                    (File.Exists(path) || Directory.Exists(path)) &&
                    !IsEntryDirectlyInDirectoryResolved(
                        path,
                        normalizedDestinationFolder))
                .ToList();

            EnsureSafeDirectoryTransfers(transferableSourcePaths.Select(path =>
                new TransferOperation(path, normalizedDestinationFolder)));

            if (!Directory.Exists(normalizedDestinationFolder))
            {
                Directory.CreateDirectory(normalizedDestinationFolder);
            }

            var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return transferableSourcePaths
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
        CancellationToken cancellationToken = default,
        bool allowShellElevation = false,
        Action<FileTransferResult>? itemCompleted = null)
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

        if (allowShellElevation)
        {
            if (ownerWindowHandle == IntPtr.Zero)
                throw new InvalidOperationException("Interactive desktop transfers require an owner window.");
            return await ExecuteModernShellTransferPlanAsync(
                operations, move, ownerWindowHandle, progress, cancellationToken,
                keepBoth: true, itemCompleted: itemCompleted);
        }

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
            if (move && CanUseLegacyShellMove(operations.Select(operation =>
                    new FileTransferPlan(
                        operation.SourcePath,
                        operation.DestinationPath))))
            {
                return await ExecuteShellMovePlanAsync(
                    operations,
                    ownerWindowHandle);
            }

            if (move)
            {
                App.Log(
                    "[FileTransfer] Legacy Shell move bypassed because one or " +
                    "more items cross filesystem roots.");
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
                    completedOperations.Add(operation);
                }
                else
                {
                    await Task.Run(() => CopyEntryAsync(
                        operation.SourcePath,
                        operation.DestinationPath));
                    completedOperations.Add(operation);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Partial completion (Explorer semantics): completed items stay;
            // the completed results ride the exception for callers.
            throw new FileTransferCanceledException(
                completedOperations
                    .Select(operation => new FileTransferResult(operation.SourcePath, operation.DestinationPath))
                    .ToList(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Partial completion: completed items stay; the failure
            // propagates with the completed results attached (including any
            // item-level receipts the inner exception already carries).
            var completedSnapshot = completedOperations
                .Select(operation => new FileTransferResult(operation.SourcePath, operation.DestinationPath))
                .ToList();
            if (exception is IFileTransferWithCompletedResults itemLevelResults)
            {
                completedSnapshot.AddRange(itemLevelResults.CompletedResults);
            }

            throw new FileTransferPartialFailureException(completedSnapshot, exception);
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

    /// <summary>
    /// Deletes a file or directory. Interactive callers pass an owner window
    /// handle so the Windows Shell owns confirmation and progress UI, following
    /// the user's own Explorer settings; headless callers (rollback, storage
    /// cleanup) keep the handle zeroed and stay silent.
    /// Returns false when the user cancelled the Shell confirmation.
    /// </summary>
    public async Task<bool> DeleteEntryAsync(
        string path,
        bool recycle = true,
        IntPtr ownerHandle = default)
    {
        string normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath) && !Directory.Exists(normalizedPath))
        {
            return true;
        }

        if (!recycle)
        {
            if (ownerHandle != IntPtr.Zero)
            {
                // Let the Shell ask "permanently delete?" according to the
                // user's own confirmation settings instead of a DeskBox dialog.
                return await Task.Run(() =>
                    DeleteEntryWithShell(normalizedPath, ownerHandle, allowUndo: false));
            }

            await DeleteEntryAsync(normalizedPath);
            return true;
        }

        return await Task.Run(() =>
            DeleteEntryWithShell(normalizedPath, ownerHandle, allowUndo: true));
    }

    /// <summary>
    /// Deletes multiple interactive items in one Shell operation so the user
    /// receives one confirmation dialog for the batch, matching Explorer's
    /// multi-selection behavior. The returned paths are the entries that no
    /// longer exist after the Shell returns; a partial user cancellation is
    /// therefore represented without turning the remaining entries into
    /// failures.
    /// </summary>
    public async Task<IReadOnlySet<string>> DeleteEntriesWithShellAsync(
        IEnumerable<string> paths,
        bool recycle,
        IntPtr ownerHandle)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (ownerHandle == IntPtr.Zero)
        {
            throw new ArgumentException(
                "An owner window handle is required for an interactive Shell batch.",
                nameof(ownerHandle));
        }

        string[] normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return await Task.Run(() =>
            DeleteEntriesWithShell(normalizedPaths, ownerHandle, allowUndo: recycle));
    }

    private static bool DeleteEntryWithShell(
        string path,
        IntPtr ownerHandle,
        bool allowUndo)
    {
        IReadOnlySet<string> deletedPaths = DeleteEntriesWithShell(
            [path],
            ownerHandle,
            allowUndo);
        return deletedPaths.Contains(path);
    }

    private static IReadOnlySet<string> DeleteEntriesWithShell(
        IReadOnlyList<string> paths,
        IntPtr ownerHandle,
        bool allowUndo)
    {
        var completedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] existingPaths = paths
            .Where(path =>
                File.Exists(path) ||
                Directory.Exists(path))
            .ToArray();
        foreach (string path in paths)
        {
            if (!existingPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                completedPaths.Add(path);
            }
        }

        if (existingPaths.Length == 0)
        {
            return completedPaths;
        }

        // The confirmation dialog is a Shell feature governed by
        // FofNoConfirmation: interactive callers omit that flag so the native
        // dialog (recycle or permanent, per the user's Explorer settings)
        // appears; headless callers keep it suppressed.
        bool interactive = ownerHandle != IntPtr.Zero;
        ushort flags = FofNoErrorUi | FofSilent;
        if (allowUndo)
        {
            flags |= FofAllowUndo;
        }

        if (!interactive)
        {
            flags |= FofNoConfirmation;
        }

        string from = string.Join('\0', existingPaths) + "\0\0";
        unsafe
        {
            fixed (char* fromPointer = from)
            {
                var operation = new ShFileOperation
                {
                    WindowHandle = ownerHandle,
                    Function = FoDelete,
                    From = fromPointer,
                    To = null,
                    Flags = flags
                };

                int result = SHFileOperation(ref operation);
                foreach (string existingPath in existingPaths)
                {
                    if (!File.Exists(existingPath) &&
                        !Directory.Exists(existingPath))
                    {
                        completedPaths.Add(existingPath);
                    }
                }

                if (result == 1223 || operation.AnyOperationsAborted != 0)
                {
                    // The user answered "No" (or cancelled) the confirmation.
                    return completedPaths;
                }

                if (result != 0 && result is not 2 and not 3)
                {
                    throw new Win32Exception(result);
                }

                return completedPaths;
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
                        // Omitting the confirmation-suppression flag lets the
                        // Shell own the name-conflict dialog (Replace/Keep
                        // both) instead of silently overwriting on a
                        // plan/execute race.
                        Flags = FofNoConfirmMkDir |
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
                    // Same as above: keep the Shell conflict dialog available.
                    Flags = FofNoConfirmMkDir |
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

    /// <summary>
    /// Resolves the destination file name for an inline rename. Folders and
    /// shortcuts always keep their original extension. With extensions hidden
    /// the input is treated as a name-only edit and the original extension is
    /// appended. With extensions visible a typed extension that differs from
    /// the original (compared with Path.GetExtension, case-insensitive) is
    /// honored, but only after the caller confirms it through
    /// <c>requiresExtensionChangeConfirmation</c>; an input without any
    /// extension is conservatively treated as a name-only edit. The input must
    /// already be sanitized via <see cref="SanitizeFileSystemName"/>.
    /// </summary>
    public static string ResolveRenameDestination(
        string originalFileName,
        string sanitizedName,
        bool isFolder,
        bool isShortcut,
        bool showFileExtensions,
        out bool requiresExtensionChangeConfirmation)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);
        ArgumentNullException.ThrowIfNull(sanitizedName);

        requiresExtensionChangeConfirmation = false;
        if (isFolder)
        {
            return sanitizedName;
        }

        string originalExtension = Path.GetExtension(originalFileName);

        if (isShortcut || !showFileExtensions)
        {
            return AppendExtensionUnlessPresent(sanitizedName, originalExtension);
        }

        string inputExtension = Path.GetExtension(sanitizedName);
        if (string.Equals(inputExtension, originalExtension, StringComparison.OrdinalIgnoreCase))
        {
            return sanitizedName;
        }

        if (string.IsNullOrEmpty(inputExtension))
        {
            return AppendExtensionUnlessPresent(sanitizedName, originalExtension);
        }

        requiresExtensionChangeConfirmation = true;
        return sanitizedName;
    }

    private static string AppendExtensionUnlessPresent(string name, string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return name;
        }

        return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + extension;
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

    /// <summary>
    /// Returns whether an entry is already a direct child of a destination
    /// directory after resolving junctions, symbolic links and filesystem
    /// aliases. If the physical identity cannot be resolved, an exact lexical
    /// parent match is retained as a conservative fallback.
    /// </summary>
    public static bool IsEntryDirectlyInDirectoryResolved(
        string entryPath,
        string directoryPath)
    {
        try
        {
            string normalizedEntry = Path.GetFullPath(entryPath);
            string? parentPath = Path.GetDirectoryName(normalizedEntry);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            if (TryResolvePathIdentity(parentPath, out string resolvedParent) &&
                TryResolvePathIdentity(directoryPath, out string resolvedDirectory))
            {
                return string.Equals(
                    resolvedParent,
                    resolvedDirectory,
                    StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether any source directory contains the destination directory
    /// after resolving junctions, symbolic links and filesystem aliases.
    /// Transfers matching this condition must be rejected before a Shell
    /// operation is started.
    /// </summary>
    internal static bool IsUnsafeDirectoryTransfer(
        IEnumerable<string> sourcePaths,
        string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        string normalizedDestination;
        try
        {
            normalizedDestination = Path.GetFullPath(destinationPath);
        }
        catch
        {
            return false;
        }

        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource;
            try
            {
                normalizedSource = Path.GetFullPath(sourcePath);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(normalizedSource) &&
                IsPathUnderDirectoryResolved(
                    normalizedDestination,
                    normalizedSource))
            {
                return true;
            }
        }

        return false;
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
    /// overlap checks. The final destination may not exist yet, so only the
    /// existing prefix is resolved and the missing suffix is preserved.
    /// </summary>
    private static bool TryResolvePathIdentity(string path, out string resolvedPath)
    {
        if (!TryResolvePathWithMissingSuffix(path, out resolvedPath))
        {
            return false;
        }

        if ((Directory.Exists(resolvedPath) || File.Exists(resolvedPath)) &&
            Win32Helper.TryGetFinalPath(resolvedPath, out string finalPath))
        {
            resolvedPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(finalPath));
        }

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
            if (IsFileSystemLink(operation.SourcePath) ||
                IsPathUnderDirectoryResolved(operation.DestinationPath, operation.SourcePath))
            {
                throw new InvalidOperationException(
                    _localizationService?.T("Widget.Error.UnsafeFolderTransfer") ??
                    UnsafeFolderTransferFallbackMessage);
            }
        }
    }

    private static void EnsureSafeRecursiveDirectoryCopy(
        string sourceDirectory,
        string destinationDirectory,
        ISet<string> visitedSourceDirectories)
    {
        if (IsFileSystemLink(sourceDirectory) ||
            IsPathUnderDirectoryResolved(destinationDirectory, sourceDirectory) ||
            !TryResolvePathIdentity(sourceDirectory, out string sourceIdentity) ||
            !visitedSourceDirectories.Add(sourceIdentity))
        {
            throw new InvalidOperationException(
                UnsafeFolderTransferFallbackMessage);
        }
    }

    /// <summary>
    /// Copies one entry through the shared CreateNew-based core: a competing
    /// file at the planned destination fails the copy untouched, and this
    /// copy's own partial destination is cleaned up through its handle.
    /// </summary>
    private static async Task CopyEntryAsync(
        string sourcePath,
        string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            await CopyFileWithProgressAsync(
                sourcePath,
                destinationPath,
                new TransferProgressReporter(progress: null, totalItems: 1),
                CancellationToken.None);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            await CopyDirectoryAsync(
                sourcePath,
                destinationPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new List<CopiedSourceFileRecord>());
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

    private static Task MoveFileAsync(string sourceFilePath, string destinationFilePath)
    {
        // The progress path already tries the atomic rename first and then
        // runs the handle-held cross-volume transaction; headless callers
        // just pass a null reporter.
        return MoveFileWithProgressAsync(
            sourceFilePath,
            destinationFilePath,
            new TransferProgressReporter(progress: null, totalItems: 1),
            CancellationToken.None);
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
        var copiedSourceFiles = new List<CopiedSourceFileRecord>();
        await CopyDirectoryAsync(
            sourceDirectory,
            destinationDirectory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            copiedSourceFiles);
        try
        {
            await Task.Run(
                () => DeleteSourceTreeByManifest(
                    sourceDirectory,
                    destinationDirectory,
                    copiedSourceFiles));
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
                (IOException and not FileTransferSourceChangedException))
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

    /// <summary>
    /// Deletes a moved directory's source tree strictly against the manifest
    /// of files that were copied to the destination. The current source tree
    /// must match the manifest exactly: a file that appeared, disappeared, or
    /// changed after the copy aborts the whole cleanup with
    /// <see cref="FileTransferSourceChangedException"/> before anything is
    /// deleted, so a concurrent writer can never lose data. Read-only files
    /// are cleared before their delete and restored when it fails, matching
    /// the single-file move path.
    /// </summary>
    internal static void DeleteSourceTreeByManifest(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<CopiedSourceFileRecord> copiedFiles)
    {
        var manifest = new Dictionary<string, CopiedSourceFileRecord>(
            StringComparer.OrdinalIgnoreCase);
        foreach (CopiedSourceFileRecord record in copiedFiles)
        {
            manifest[record.SourceFilePath] = record;
        }

        int currentFileCount = 0;
        foreach (string filePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            currentFileCount++;
            if (!manifest.TryGetValue(filePath, out CopiedSourceFileRecord record))
            {
                throw new FileTransferSourceChangedException(
                    sourceDirectory,
                    destinationDirectory);
            }

            var currentInfo = new FileInfo(filePath);
            if (!currentInfo.Exists ||
                currentInfo.Length != record.Length ||
                currentInfo.LastWriteTimeUtc != record.LastWriteTimeUtc)
            {
                throw new FileTransferSourceChangedException(
                    sourceDirectory,
                    destinationDirectory);
            }
        }

        if (currentFileCount != manifest.Count)
        {
            // Every remaining file matched, so a manifest entry is gone: the
            // source was manipulated during the copy. Fail closed.
            throw new FileTransferSourceChangedException(
                sourceDirectory,
                destinationDirectory);
        }

        foreach (CopiedSourceFileRecord record in copiedFiles)
        {
            string filePath = record.SourceFilePath;
            if (!File.Exists(filePath))
            {
                throw new IOException(
                    $"The copied source file disappeared before cleanup: '{filePath}'");
            }

            // Handle-bound deletion: the object at the path is deleted only
            // while its identity still matches the manifest record, closing
            // the gap between the tree validation above and each delete.
            // Without a recorded identity there is no deletion authority:
            // fail closed (both copies stay).
            if (record.Identity is not { } recordIdentity)
            {
                throw new FileTransferSourceCleanupException(
                    sourceDirectory,
                    destinationDirectory,
                    new IOException(
                        $"The copied source file has no recorded object identity: '{filePath}'"));
            }

            if (!TryDeleteFileByIdentity(filePath, recordIdentity))
            {
                // Could not delete through a verified handle (locked by
                // another process, or the object was swapped in the
                // narrow window after the tree validation): the complete
                // destination stays and the source is kept — fail closed
                // either way, reported as a cleanup failure.
                throw new FileTransferSourceCleanupException(
                    sourceDirectory,
                    destinationDirectory,
                    new IOException(
                        $"The copied source file could not be deleted through a verified handle: '{filePath}'"));
            }
        }

        // Empty directories (original and newly created ones alike) are safe
        // to remove: a non-empty directory fails the non-recursive delete, so
        // unmanifested content can only survive, never disappear.
        foreach (string directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory, recursive: false);
        }

        Directory.Delete(sourceDirectory, recursive: false);
    }

    /// <summary>
    /// Captures the current file manifest of a directory tree. Used by
    /// best-effort deletions of DeskBox-owned copies, where "everything that
    /// is there right now" is the exact set to remove.
    /// </summary>
    internal static List<CopiedSourceFileRecord> CollectCurrentFileManifest(
        string directoryPath)
    {
        var records = new List<CopiedSourceFileRecord>();
        foreach (string filePath in Directory.EnumerateFiles(
                     directoryPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            var info = new FileInfo(filePath);
            records.Add(new CopiedSourceFileRecord(
                filePath,
                info.Length,
                info.LastWriteTimeUtc,
                TryCaptureSourceIdentity(filePath)));
        }

        return records;
    }

    /// <summary>
    /// Merges a migrated directory copy back to its original location,
    /// conservatively: a child whose original is missing moves back; a child
    /// with an existing original twin is KEPT as a duplicate (content
    /// equality cannot be proven without deleting data, and this runs in a
    /// rollback where the original side may already have been partially
    /// deleted — a same-named subtree on the copy side can be the ONLY
    /// remaining copy of some of its files); same-named directories merge
    /// recursively. The copy directory disappears only once empty.
    /// </summary>
    internal static async Task RestoreMigratedDirectoryPreservingExistingAsync(
        string copiedDirectory,
        string originalDirectory)
    {
        if (!Directory.Exists(copiedDirectory))
        {
            return;
        }

        Directory.CreateDirectory(originalDirectory);
        var reporter = new TransferProgressReporter(progress: null, totalItems: 1);
        foreach (string copiedChild in Directory.EnumerateFileSystemEntries(copiedDirectory).ToList())
        {
            string originalChild = Path.Combine(
                originalDirectory,
                Path.GetFileName(copiedChild));
            try
            {
                if (File.Exists(originalChild))
                {
                    // Both copies stay: deciding "same content" from size and
                    // timestamp cannot authorize a delete, and the migration
                    // residue flow already offers the user manual cleanup.
                    App.Log(
                        $"[FileTransfer] Migration merge-back kept " +
                        $"'{copiedChild}' next to its original " +
                        $"'{originalChild}' (duplicates are never auto-deleted).");
                }
                else if (Directory.Exists(originalChild))
                {
                    if (Directory.Exists(copiedChild))
                    {
                        // Recurse child-by-child: never delete a same-named
                        // copied subtree wholesale — part of it may be the
                        // last remaining copy of files already deleted from
                        // the original during the failed source cleanup.
                        await RestoreMigratedDirectoryPreservingExistingAsync(
                            copiedChild,
                            originalChild);
                    }
                }
                else if (Directory.Exists(copiedChild))
                {
                    Directory.CreateDirectory(originalChild);
                    await RestoreMigratedDirectoryPreservingExistingAsync(
                        copiedChild,
                        originalChild);
                }
                else
                {
                    await MoveEntryWithProgressAsync(
                        copiedChild,
                        originalChild,
                        estimate: null,
                        reporter,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[FileTransfer] Migration merge-back skipped " +
                    $"'{copiedChild}' -> '{originalChild}': {ex.Message}");
            }
        }

        if (Directory.Exists(copiedDirectory) &&
            !Directory.EnumerateFileSystemEntries(copiedDirectory).Any())
        {
            Directory.Delete(copiedDirectory, recursive: false);
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
        if (item is null)
        {
            return OpenItemResult.Failed;
        }

        string itemPath = item.Path;
        string targetPath = item.TargetPath;
        bool isShortcut = item.IsShortcut;
        OpenItemResult result = OpenItemCore(
            itemPath,
            targetPath,
            isShortcut,
            ownerHwnd,
            trace: null,
            out string resolvedTargetPath);

        // Keep the synchronous compatibility path's existing hydration
        // behavior, but never mutate a WidgetItem from the asynchronous launch
        // worker. The UI path uses OpenItemAsync below and only needs the result.
        if (ShortcutHelper.IsShellLinkPath(itemPath) &&
            string.IsNullOrWhiteSpace(item.TargetPath) &&
            !string.IsNullOrWhiteSpace(resolvedTargetPath))
        {
            item.TargetPath = resolvedTargetPath;
        }

        return result;
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

        /// <summary>
        /// The shortcut's stored target is missing, so the link was handed to
        /// Windows instead of being launched. Nothing was opened here, which is
        /// why this is not folded into <see cref="OpenedOrHandled"/>.
        /// </summary>
        ShortcutTargetMissing,
        ShortcutDeleted,
        Busy,

        /// <summary>
        /// No shell association exists for this item, so any Shell dispatch
        /// would spawn its own picker and report a dismissal as a silent
        /// success. The caller must show an observable picker instead.
        /// </summary>
        RequiresOpenWithPicker,

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

    private static Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory)
    {
        return CopyDirectoryAsync(
            sourceDirectory,
            destinationDirectory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new List<CopiedSourceFileRecord>());
    }

    /// <summary>
    /// Copies one directory tree through the shared core. Completed children
    /// are never rolled back (Explorer semantics); the manifest of copied
    /// source files feeds the directory-move source cleanup, which runs only
    /// after the whole tree has copied and been verified.
    /// </summary>
    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        ISet<string> visitedSourceDirectories,
        List<CopiedSourceFileRecord> copiedSourceFiles)
    {
        EnsureSafeRecursiveDirectoryCopy(
            sourceDirectory,
            destinationDirectory,
            visitedSourceDirectories);
        Directory.CreateDirectory(destinationDirectory);

        foreach (string filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            string destinationFilePath = GetAvailableDestinationPath(destinationDirectory, Path.GetFileName(filePath));
            // Capture the pre-copy state: a mismatch during cleanup means
            // the file changed while it was being copied. FileInfo stats
            // lazily on first property access, so read both values now.
            var sourceInfo = new FileInfo(filePath);
            long sourceLength = sourceInfo.Length;
            DateTime sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
            // The source identity comes from the copy's own handle: it
            // describes the object that was actually read, never whatever
            // may have appeared at the path after the copy finished.
            FileTransferSourceIdentity? sourceIdentity = await CopyFileWithProgressAsync(
                filePath,
                destinationFilePath,
                new TransferProgressReporter(progress: null, totalItems: 1),
                CancellationToken.None);
            copiedSourceFiles.Add(new CopiedSourceFileRecord(
                filePath,
                sourceLength,
                sourceLastWriteUtc,
                sourceIdentity));
        }

        foreach (string subDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            string folderName = Path.GetFileName(subDirectory);
            string destinationSubDirectory = GetAvailableDestinationPath(destinationDirectory, folderName);
            await CopyDirectoryAsync(
                subDirectory,
                destinationSubDirectory,
                visitedSourceDirectories,
                copiedSourceFiles);
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

    // ─── Copy-then-delete source identity ───────────────────────────────

    /// <summary>
    /// Identity of a source file captured before a copy-then-delete move.
    /// The NTFS file key survives path-based replacement (a new file created
    /// at the same path gets a new key), which length/timestamp comparison
    /// cannot detect. The key does NOT survive in-place edits: opening and
    /// rewriting a file keeps its key, so length and timestamp must still
    /// match before the source may be deleted. File systems that provide no
    /// stable file keys grant no deletion authority at all: matching never
    /// falls back to length + timestamp for destructive operations.
    /// </summary>
    /// <summary>
    /// A full 128-bit file system object id (FILE_ID_128). The legacy 64-bit
    /// nFileIndex from BY_HANDLE_FILE_INFORMATION is not guaranteed unique
    /// on ReFS, so identity comparisons use the full id from
    /// GetFileInformationByHandleEx(FileIdInfo).
    /// </summary>
    internal readonly record struct FileId128(ulong High, ulong Low);

    internal readonly record struct FileTransferSourceIdentity(
        long Length,
        DateTime LastWriteTimeUtc,
        ulong VolumeSerialNumber,
        FileId128? FileId);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    internal static FileTransferSourceIdentity? TryCaptureSourceIdentity(string path)
    {
        try
        {
            // Native open (not FileStream): FILE_FLAG_BACKUP_SEMANTICS makes
            // this work for DIRECTORIES as well as files, so recovery
            // receipts can identify folder items too.
            using SafeFileHandle handle = CreateFileW(
                path,
                GenericReadAccess,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            return IdentityFromHandle(handle);
        }
        catch
        {
            return null;
        }
    }

    internal static bool SourceFileMatchesIdentity(
        string path,
        FileTransferSourceIdentity expected)
    {
        return TryCaptureSourceIdentity(path) is { } current &&
            SourceIdentityMatches(current, expected);
    }

    /// <summary>
    /// Captures the durable undo receipt for a history item: the object
    /// identity of whatever this result's physical move just produced at its
    /// destination. Null means the file system could not provide one, and the
    /// entry gets no automatic undo authority.
    /// </summary>
    internal static Models.DesktopOrganizationDestinationIdentity? CaptureUndoReceiptIdentity(
        string destinationPath)
    {
        return TryCaptureSourceIdentity(destinationPath) is { } identity &&
            identity.FileId is { } fileId
                ? new Models.DesktopOrganizationDestinationIdentity
                {
                    VolumeSerialNumber = identity.VolumeSerialNumber,
                    FileIdHigh = fileId.High,
                    FileIdLow = fileId.Low,
                }
                : null;
    }

    /// <summary>
    /// True while the object at the history item's destination still carries
    /// the recorded receipt. Entries without a receipt (legacy history) have
    /// no automatic undo authority.
    /// </summary>
    internal static bool UndoReceiptStillMatches(
        string destinationPath,
        Models.DesktopOrganizationDestinationIdentity? receipt)
    {
        if (receipt is null)
        {
            return false;
        }

        return TryCaptureSourceIdentity(destinationPath) is { } current &&
            current.FileId is { } currentId &&
            currentId == new FileId128(receipt.FileIdHigh, receipt.FileIdLow) &&
            current.VolumeSerialNumber == receipt.VolumeSerialNumber;
    }

    internal static bool SourceIdentityMatches(
        FileTransferSourceIdentity current,
        FileTransferSourceIdentity expected)
    {
        if (expected.FileId is null && current.FileId is null)
        {
            // This file system provides no stable object ids: metadata
            // alone is not an ownership proof, so destructive operations
            // have no say here. (Copies still work; deletions do not.)
            return false;
        }

        if ((expected.FileId is null) != (current.FileId is null))
        {
            // The same file system answers file-id queries consistently for
            // the same object; one stat seeing an id and the other not means
            // the object at the path is no longer the one that was captured.
            return false;
        }

        if (expected.FileId is { } expectedId &&
            current.FileId is { } currentId &&
            expectedId != currentId)
        {
            // The path holds a different file object than the one copied.
            return false;
        }

        if (expected.VolumeSerialNumber != current.VolumeSerialNumber)
        {
            // The object now reports a different volume (reparse point swap).
            return false;
        }

        // A matching file key proves the same object, NOT that its content
        // is unchanged: an in-place edit keeps the key. Only an identical
        // length and write time together prove the copied content is still
        // current, so the source may not be deleted without all of them.
        return current.Length == expected.Length &&
            current.LastWriteTimeUtc == expected.LastWriteTimeUtc;
    }

    // ─── Handle-bound source deletion ─────────────────────────────────

    private const uint GenericReadAccess = 0x80000000;
    private const uint GenericWriteAccess = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileWriteAttributesAccess = 0x00000100;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint ShareNone = 0;
    private const uint OpenExisting = 3;
    private const uint CreateNewDisposition = 1;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileDispositionInfoClass = 4; // FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        // FILE_DISPOSITION_INFO carries a 1-byte BOOLEAN, not a 4-byte BOOL:
        // without U1 marshaling the native call sees garbage.
        [MarshalAs(UnmanagedType.U1)]
        public bool Delete;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfo lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    private static extern bool SetFileBasicInfoByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileBasicInfo lpFileInformation,
        int dwBufferSize);

    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18; // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const uint ReadOnlyAttribute = 0x00000001;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FileIdInfo
    {
        // Must mirror the native FILE_ID_INFO exactly: the volume serial is
        // ULONGLONG (8 bytes), not DWORD — an undersized layout makes
        // GetFileInformationByHandleEx reject the buffer outright.
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        int dwBufferSize);

    /// <summary>
    /// Reads the object identity from an already-open handle. The identity
    /// describes exactly the object the handle is bound to, so callers that
    /// keep the handle open can validate and act without any path race. The
    /// id comes from FileIdInfo (full 128 bits — the 64-bit index is not
    /// unique on ReFS); length and timestamps come from the classic
    /// BY_HANDLE view in the same call sequence. A failed 128-bit query
    /// returns null: FileIdInfo is supported everywhere DeskBox runs, so a
    /// failure means an exotic provider, and identity authority is denied
    /// rather than degraded to the non-unique 64-bit index.
    /// </summary>
    internal static FileTransferSourceIdentity? IdentityFromHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            return null;
        }

        long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        long lastWrite =
            ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        unsafe
        {
            FileIdInfo idInfo = default;
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileIdInfoClass,
                    out idInfo,
                    sizeof(FileIdInfo)))
            {
                return null;
            }

            ulong low = *(ulong*)idInfo.FileId;
            ulong high = *(ulong*)(idInfo.FileId + 8);
            return new FileTransferSourceIdentity(
                length,
                DateTime.FromFileTimeUtc(lastWrite),
                idInfo.VolumeSerialNumber,
                new FileId128(high, low));
        }
    }

    /// <summary>
    /// Marks the object bound to the handle for POSIX-style deletion
    /// (effective when the last handle closes). Read-only objects are
    /// cleared through the same handle, so the deleted object is always the
    /// verified one.
    /// </summary>
    private static bool TrySetDispositionByHandle(
        SafeFileHandle handle,
        in ByHandleFileInformation information,
        string path)
    {
        var disposition = new FileDispositionInfo { Delete = true };
        int dispositionSize = Marshal.SizeOf<FileDispositionInfo>();
        if (SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                dispositionSize))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != ErrorAccessDenied)
        {
            App.Log($"[FileTransfer] Disposition failed for '{path}' (win32={error}).");
            return false;
        }

        if ((information.FileAttributes & ReadOnlyAttribute) == 0 ||
            !TryClearReadOnlyByHandle(handle, in information))
        {
            App.Log(
                $"[FileTransfer] Read-only clear failed for '{path}' " +
                $"(win32={Marshal.GetLastWin32Error()}, readonly={(information.FileAttributes & ReadOnlyAttribute) != 0}).");
            return false;
        }

        bool retryDeleted = SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            dispositionSize);
        if (!retryDeleted)
        {
            App.Log(
                $"[FileTransfer] Disposition retry after read-only clear failed for " +
                $"'{path}' (win32={Marshal.GetLastWin32Error()}).");
        }

        return retryDeleted;
    }

    private static bool TryClearReadOnlyByHandle(
        SafeFileHandle handle,
        in ByHandleFileInformation information)
    {
        // FILE_BASIC_INFO rewrites every field; zeros mean "keep current"
        // for ChangeTime, and the timestamps must be echoed back verbatim.
        // An attributes value of 0 also means "no change", so a file whose
        // only attribute was read-only must become FILE_ATTRIBUTE_NORMAL.
        uint clearedAttributes = information.FileAttributes & ~ReadOnlyAttribute;
        var basic = new FileBasicInfo
        {
            CreationTime = ((long)information.CreationTimeHigh << 32) | information.CreationTimeLow,
            LastAccessTime = ((long)information.LastAccessTimeHigh << 32) | information.LastAccessTimeLow,
            LastWriteTime = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow,
            ChangeTime = 0,
            FileAttributes = clearedAttributes == 0 ? FileAttributeNormal : clearedAttributes,
        };
        return SetFileBasicInfoByHandle(
            handle,
            FileBasicInfoClass,
            ref basic,
            Marshal.SizeOf<FileBasicInfo>());
    }

    /// <summary>
    /// Deletes the object at a path only while the handle opened at that
    /// path carries the expected identity: validation and deletion share the
    /// handle, so a file swapped in at the path cannot be deleted instead.
    /// Best-effort — false means the file was kept (mismatch, unavailable
    /// identity, or a failed disposition) and the caller decides how to
    /// surface that.
    /// </summary>
    internal static bool TryDeleteFileByIdentity(
        string path,
        FileTransferSourceIdentity expectedIdentity)
    {
        if (TryDeleteFileByIdentityPass(path, expectedIdentity))
        {
            return true;
        }

        // The delete check reads the read-only attribute snapshotted when
        // the handle was opened: an attribute cleared through the first
        // handle only takes effect for a fresh handle. Retry once after the
        // (verified) clear.
        return TryDeleteFileByIdentityPass(path, expectedIdentity);
    }

    private static bool TryDeleteFileByIdentityPass(
        string path,
        FileTransferSourceIdentity expectedIdentity)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(
                path,
                GenericReadAccess | DeleteAccess | FileWriteAttributesAccess,
                ShareRead,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                // A writer holding the file refuses us (or an unrelated
                // sharing conflict): no deletion without exclusivity.
                App.Log(
                    $"[FileTransfer] Kept '{path}': it could not be opened " +
                    $"for deletion (win32={Marshal.GetLastWin32Error()}).");
                return false;
            }

            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information) ||
                IdentityFromHandle(handle) is not { } currentIdentity)
            {
                App.Log($"[FileTransfer] Kept '{path}': its identity could not be read for deletion.");
                return false;
            }

            if (!SourceIdentityMatches(currentIdentity, expectedIdentity))
            {
                App.Log($"[FileTransfer] Kept '{path}': it no longer matches the recorded identity.");
                return false;
            }

            if (!TrySetDispositionByHandle(handle, in information, path))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[FileTransfer] Kept '{path}': deletion failed ({ex.Message}).");
            return false;
        }
    }

    // ─── Steam dead-shortcut detection ───────────────────────────────────

    /// <summary>
    /// True when the path is a Steam game shortcut (.url) whose game is no
    /// longer installed. This is NOT a display filter: since 2026-09-13 such
    /// shortcuts stay visible because opening them launches Steam's install
    /// flow. The detection is kept as a data source for future organize-mode
    /// cleanup suggestions ("N shortcuts of uninstalled games were found").
    /// </summary>
    internal static bool IsDeadSteamShortcutUrl(string path)
    {
        return Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase) &&
            IsDeadSteamShortcut(path);
    }

    /// <summary>
    /// True when a widget item list can never display this path: missing
    /// entries, desktop.ini, and hidden attributes are excluded by folder
    /// enumeration and watcher refreshes. Steam .url shortcuts - even for
    /// uninstalled games - are always displayable.
    /// </summary>
    internal static bool IsFilteredFromWidgetDisplay(string path)
    {
        return !ShouldDisplayEntry(path);
    }

    private static readonly object s_steamLibLock = new();
    private static SteamLibrarySnapshot? s_steamLibrarySnapshot;
    private static DateTime s_steamLibCacheTime;
    private static readonly TimeSpan SteamLibCacheDuration = TimeSpan.FromMinutes(5);

    private sealed record SteamLibrarySnapshot(
        string? SteamPath,
        string[] DeclaredLibraryPaths,
        long LibraryFileVersion,
        bool IsAuthoritative);

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
            return GetSteamGameInstallState(appId) ==
                SteamGameInstallState.NotInstalled;
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
    private static SteamGameInstallState GetSteamGameInstallState(
        string appId)
    {
        SteamLibrarySnapshot snapshot = GetSteamLibrarySnapshot();
        var evidence = new List<(bool IsAvailable, bool HasManifest)>();

        foreach (string libraryPath in snapshot.DeclaredLibraryPaths)
        {
            evidence.Add(InspectSteamLibraryManifest(libraryPath, appId));
        }

        return EvaluateSteamGameInstallState(
            snapshot.IsAuthoritative,
            evidence);
    }

    internal static SteamGameInstallState EvaluateSteamGameInstallState(
        bool snapshotIsAuthoritative,
        IReadOnlyList<(bool IsAvailable, bool HasManifest)> libraries)
    {
        if (libraries.Any(library => library.HasManifest))
        {
            return SteamGameInstallState.Installed;
        }

        if (!snapshotIsAuthoritative ||
            libraries.Count == 0 ||
            libraries.Any(library => !library.IsAvailable))
        {
            return SteamGameInstallState.Unknown;
        }

        return SteamGameInstallState.NotInstalled;
    }

    private static (bool IsAvailable, bool HasManifest)
        InspectSteamLibraryManifest(
            string libraryPath,
            string appId)
    {
        if (!Directory.Exists(libraryPath))
        {
            return (false, false);
        }

        string manifestPath = Path.Combine(
            libraryPath,
            "steamapps",
            $"appmanifest_{appId}.acf");
        try
        {
            System.IO.FileAttributes attributes = File.GetAttributes(manifestPath);
            return (true, !attributes.HasFlag(System.IO.FileAttributes.Directory));
        }
        catch (FileNotFoundException)
        {
            return (true, false);
        }
        catch (DirectoryNotFoundException)
        {
            return (true, false);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            // File.Exists collapses access errors into false, which could hide
            // a valid shortcut. Treat an unreadable library as unknown instead.
            return (false, false);
        }
    }

    /// <summary>
    /// Gets all Steam library folder paths (cached for 5 minutes).
    /// Reads from the registry and libraryfolders.vdf.
    /// </summary>
    private static SteamLibrarySnapshot GetSteamLibrarySnapshot()
    {
        lock (s_steamLibLock)
        {
            string? steamPath = TryGetSteamInstallPath();
            string? libraryFilePath = string.IsNullOrWhiteSpace(steamPath)
                ? null
                : Path.Combine(
                    steamPath,
                    "steamapps",
                    "libraryfolders.vdf");
            long libraryFileVersion = GetOptionalFileVersion(libraryFilePath);
            if (s_steamLibrarySnapshot is not null &&
                string.Equals(
                    s_steamLibrarySnapshot.SteamPath,
                    steamPath,
                    StringComparison.OrdinalIgnoreCase) &&
                s_steamLibrarySnapshot.LibraryFileVersion == libraryFileVersion &&
                DateTime.UtcNow - s_steamLibCacheTime < SteamLibCacheDuration)
            {
                return s_steamLibrarySnapshot;
            }

            var paths = new List<string>();
            bool isAuthoritative = true;

            try
            {
                if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
                {
                    isAuthoritative = false;
                }
                else
                {
                    // Keep every declared path, including currently unavailable
                    // removable/network libraries. Their absence means the
                    // installation state is unknown, not that the game was
                    // uninstalled.
                    paths.Add(steamPath);

                    if (libraryFilePath is not null &&
                        File.Exists(libraryFilePath))
                    {
                        int parsedPathCount = 0;
                        foreach (string line in File.ReadLines(libraryFilePath))
                        {
                            string trimmed = line.Trim();
                            // Look for "path" entries: "path" "D:\\SteamLibrary"
                            if (trimmed.StartsWith(
                                    "\"path\"",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                int firstQuote = trimmed.IndexOf('"', 7);
                                int secondQuote = firstQuote >= 0
                                    ? trimmed.IndexOf('"', firstQuote + 1)
                                    : -1;
                                if (firstQuote >= 0 && secondQuote > firstQuote)
                                {
                                    string libraryPath =
                                        trimmed[(firstQuote + 1)..secondQuote]
                                            .Replace("\\\\", "\\");
                                    parsedPathCount++;
                                    if (!paths.Contains(
                                            libraryPath,
                                            StringComparer.OrdinalIgnoreCase))
                                    {
                                        paths.Add(libraryPath);
                                    }
                                }
                            }
                        }

                        // A current Steam library file normally contains at
                        // least its default path. Treat an unreadable/unknown
                        // format conservatively instead of hiding shortcuts.
                        if (parsedPathCount == 0)
                        {
                            isAuthoritative = false;
                        }
                    }
                    else
                    {
                        // Without the library registry Steam may still have
                        // games on secondary drives, so root-only evidence is
                        // insufficient to declare a shortcut dead.
                        isAuthoritative = false;
                    }
                }
            }
            catch (Exception ex)
            {
                isAuthoritative = false;
                App.Log($"[FileService] Failed to enumerate Steam libraries: {ex.Message}");
            }

            s_steamLibrarySnapshot = new SteamLibrarySnapshot(
                steamPath,
                paths.ToArray(),
                libraryFileVersion,
                isAuthoritative);
            s_steamLibCacheTime = DateTime.UtcNow;
            return s_steamLibrarySnapshot;
        }
    }

    private static string? TryGetSteamInstallPath()
    {
        string? userPath = null;
        try
        {
            using var currentUserKey =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Valve\Steam");
            if (currentUserKey?.GetValue("SteamPath") is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                userPath = value.Replace('/', '\\');
                if (Directory.Exists(userPath))
                {
                    return userPath;
                }
            }
        }
        catch
        {
            // Keep checking the machine-wide registration.
        }

        string? machinePath = null;
        try
        {
            using var localMachineKey =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Valve\Steam");
            if (localMachineKey?.GetValue("InstallPath") is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                machinePath = value.Replace('/', '\\');
                if (Directory.Exists(machinePath))
                {
                    return machinePath;
                }
            }
        }
        catch
        {
            // Registry access is advisory. Unknown state keeps the shortcut.
        }

        return userPath ?? machinePath;
    }

    private static long GetOptionalFileVersion(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
