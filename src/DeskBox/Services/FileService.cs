using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using Windows.Storage;

namespace DeskBox.Services;

/// <summary>
/// Provides file system operations: enumerate files, resolve shortcuts, get icons.
/// </summary>
public sealed class FileService
{
    private sealed record TransferOperation(string SourcePath, string DestinationPath);

    public sealed record FileTransferPlan(string SourcePath, string DestinationPath);

    public sealed record FileTransferResult(string SourcePath, string DestinationPath);

    /// <summary>
    /// Enumerate all files and folders in a directory and create WidgetItem models.
    /// </summary>
    public async Task<List<WidgetItem>> EnumerateDirectoryAsync(string directoryPath, bool hideShortcutArrowOverlay = false)
    {
        var items = new List<WidgetItem>();

        if (!Directory.Exists(directoryPath))
        {
            return items;
        }

        var entries = Directory.EnumerateFileSystemEntries(directoryPath)
            .Where(p =>
            {
                try
                {
                    var name = Path.GetFileName(p);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var attr = File.GetAttributes(p);
                    return (attr & System.IO.FileAttributes.Hidden) == 0;
                }
                catch
                {
                    return true;
                }
            })
            .OrderBy(p => !Directory.Exists(p))
            .ThenBy(p => Path.GetFileName(p));

        int sortOrder = 0;
        foreach (var entryPath in entries)
        {
            var item = await TryCreateWidgetItemAsync(entryPath, hideShortcutArrowOverlay);
            if (item is null)
            {
                continue;
            }

            item.SortOrder = sortOrder++;
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Create a WidgetItem from a file or folder path.
    /// </summary>
    public async Task<WidgetItem> CreateWidgetItemAsync(string path, bool hideShortcutArrowOverlay = false)
    {
        var item = new WidgetItem
        {
            Path = path,
            Name = Path.GetFileNameWithoutExtension(path),
            IsFolder = Directory.Exists(path),
            IsShortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        };

        if (item.IsShortcut)
        {
            var info = ShortcutHelper.Resolve(path);
            if (info is not null)
            {
                item.TargetPath = info.TargetPath;
                item.Name = Path.GetFileNameWithoutExtension(path);
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
                item.LastModified = fi.LastWriteTime;
            }
            catch
            {
            }
        }
        else if (item.IsFolder)
        {
            item.Name = Path.GetFileName(path);
        }

        item.Icon = await GetIconAsync(path, hideShortcutArrowOverlay);
        return item;
    }

    public async Task<WidgetItem?> TryCreateWidgetItemAsync(string path, bool hideShortcutArrowOverlay = false)
    {
        if (!ShouldDisplayEntry(path))
        {
            return null;
        }

        return await CreateWidgetItemAsync(path, hideShortcutArrowOverlay);
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

    public Task<BitmapImage?> GetIconAsync(string path, bool hideShortcutArrowOverlay = false)
    {
        return IconHelper.GetIconAsync(path, hideShortcutArrowOverlay);
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
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else if (File.Exists(path))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
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
    public async Task<IReadOnlyList<FileTransferResult>> ExecuteTransferPlanAsync(IEnumerable<FileTransferPlan> plans, bool move)
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
            if (File.Exists(normalizedPath))
            {
                FileSystem.DeleteFile(
                    normalizedPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
                return;
            }

            if (Directory.Exists(normalizedPath))
            {
                FileSystem.DeleteDirectory(
                    normalizedPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
        });
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
    public static void OpenItem(WidgetItem item)
    {
        var pathToOpen = item.IsShortcut ? item.Path : item.TargetPath;
        if (!string.IsNullOrEmpty(pathToOpen))
        {
            Win32Helper.OpenFile(pathToOpen);
        }
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
}
