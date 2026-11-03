using DeskBox.Services;

namespace DeskBox.Platform;

/// <summary>
/// Platform filesystem helpers for <see cref="ArchiveZipService"/>: staging
/// <c>.partial</c> archive commits, failure cleanup, and symlink/junction-safe
/// directory traversal.
/// </summary>
internal static class ArchiveFileOps
{
    public static string CreatePartialArchivePath(string targetArchivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetArchivePath);
        string fullTarget = Path.GetFullPath(targetArchivePath);
        string directory = Path.GetDirectoryName(fullTarget) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(fullTarget);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.partial");
    }

    public static string CommitPartialArchive(string partialPath, string desiredArchivePath)
    {
        string finalPath = File.Exists(desiredArchivePath) || Directory.Exists(desiredArchivePath)
            ? FileService.GetAvailablePath(desiredArchivePath)
            : desiredArchivePath;
        File.Move(partialPath, finalPath, overwrite: false);
        return finalPath;
    }

    public static void TryDeletePartialArchive(string? partialPath)
    {
        if (string.IsNullOrWhiteSpace(partialPath))
        {
            return;
        }

        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ArchiveZipService] Failed to delete partial archive '{partialPath}': {ex.Message}");
        }
    }

    public static void CleanupFailedExtractionDirectory(string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return;
        }

        try
        {
            if (!Directory.Exists(destinationDirectory) ||
                !StorageMigrationNativeMethods.IsSafeTraversablePath(destinationDirectory))
            {
                return;
            }

            Directory.Delete(destinationDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            App.Log($"[ArchiveZipService] Failed to clean failed extraction folder '{destinationDirectory}': {ex.Message}");
        }
    }

    public static bool CanTraverseDirectory(string directoryPath) =>
        StorageMigrationNativeMethods.IsSafeTraversablePath(directoryPath);

    public static void CollectDirectoryEntries(
        string rootDirectory,
        IReadOnlySet<string> excludedFilePaths,
        List<string> emptyDirectories,
        List<string> files,
        CancellationToken cancellationToken)
    {
        string fullRoot = Path.GetFullPath(rootDirectory);
        if (!CanTraverseDirectory(fullRoot))
        {
            return;
        }

        var pending = new Stack<(string Path, bool IsTopLevel)>();
        pending.Push((fullRoot, true));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string currentDir, bool isTopLevel) = pending.Pop();

            string[] childFiles;
            string[] childDirectories;
            try
            {
                childFiles = Directory.GetFiles(currentDir);
                childDirectories = Directory.GetDirectories(currentDir);
            }
            catch (Exception ex) when (
                !isTopLevel &&
                ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                App.Log($"[ArchiveZipService] Skipped inaccessible subdirectory '{currentDir}': {ex.Message}");
                continue;
            }

            int addedFiles = 0;
            foreach (string childFile in childFiles)
            {
                string fullFile = Path.GetFullPath(childFile);
                if (excludedFilePaths.Contains(fullFile) ||
                    !StorageMigrationNativeMethods.IsSafeTraversablePath(fullFile))
                {
                    continue;
                }

                files.Add(fullFile);
                addedFiles++;
            }

            int traversableSubdirectories = 0;
            for (int index = childDirectories.Length - 1; index >= 0; index--)
            {
                string fullSubdir = Path.GetFullPath(childDirectories[index]);
                if (!CanTraverseDirectory(fullSubdir))
                {
                    App.Log($"[ArchiveZipService] Skipped reparse/unhydrated directory '{fullSubdir}'.");
                    continue;
                }

                traversableSubdirectories++;
                pending.Push((fullSubdir, false));
            }

            if (!isTopLevel && addedFiles == 0 && traversableSubdirectories == 0)
            {
                emptyDirectories.Add(currentDir);
            }
        }
    }
}
