namespace DeskBox.Platform;

/// <summary>
/// Confines file-system mutations for the owned %DataDirectory%/clipboard/
/// storage root inside <c>DeskBox.Platform</c>.
/// </summary>
internal static class ClipboardStorageFileOps
{
    public static async Task WriteBytesIfMissingAsync(string targetPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (File.Exists(targetPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static void CopyFileOverwrite(string sourcePath, string destinationPath)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    public static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // Ignore transient file locks on thumbnails or temp exports.
        }

        return false;
    }

    public static (int FileCount, long TotalBytes) MeasureDirectoryFiles(string directoryPath) =>
        MeasureDirectoryFiles(directoryPath, SearchOption.TopDirectoryOnly);

    public static (int FileCount, long TotalBytes) MeasureDirectoryFilesRecursive(string directoryPath) =>
        MeasureDirectoryFiles(directoryPath, SearchOption.AllDirectories);

    private static (int FileCount, long TotalBytes) MeasureDirectoryFiles(
        string directoryPath,
        SearchOption searchOption)
    {
        if (!Directory.Exists(directoryPath))
        {
            return (0, 0L);
        }

        int count = 0;
        long bytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", searchOption))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists)
                    {
                        count++;
                        bytes += info.Length;
                    }
                }
                catch
                {
                    // Skip concurrently deleted files.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration failures.
        }

        return (count, bytes);
    }

    public static int DeleteOrphanedDroppedEntries(
        string droppedDirectoryPath,
        IReadOnlySet<string> referencedFullPaths)
    {
        if (!Directory.Exists(droppedDirectoryPath))
        {
            return 0;
        }

        var normalizedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in referencedFullPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                normalizedRefs.Add(Path.GetFullPath(raw).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(droppedDirectoryPath, "*", SearchOption.AllDirectories))
            {
                string fullFile;
                try
                {
                    fullFile = Path.GetFullPath(file);
                }
                catch
                {
                    continue;
                }

                if (!IsPathReferencedByDroppedEntries(fullFile, normalizedRefs) &&
                    TryDeleteFile(fullFile))
                {
                    deleted++;
                }
            }

            // Prune emptied batch/folder trees deepest-first while keeping droppedDirectoryPath itself.
            string[] dirs = Directory.EnumerateDirectories(droppedDirectoryPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(static d => d.Length)
                .ToArray();
            foreach (string dir in dirs)
            {
                try
                {
                    string fullDir = Path.GetFullPath(dir).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    if (!IsPathReferencedByDroppedEntries(fullDir, normalizedRefs) &&
                        Directory.Exists(dir) &&
                        !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                }
                catch
                {
                    // Best-effort empty directory cleanup.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }

        return deleted;
    }

    private static bool IsPathReferencedByDroppedEntries(
        string candidateFullPath,
        HashSet<string> normalizedRefs)
    {
        if (normalizedRefs.Contains(candidateFullPath))
        {
            return true;
        }

        foreach (string referenced in normalizedRefs)
        {
            string prefix = referenced.EndsWith(Path.DirectorySeparatorChar)
                ? referenced
                : referenced + Path.DirectorySeparatorChar;
            if (candidateFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static int DeleteOrphanedFiles(string directoryPath, IReadOnlySet<string> referencedFileNames)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!referencedFileNames.Contains(name) && TryDeleteFile(file))
                {
                    deleted++;
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }

        return deleted;
    }

    public static void CleanupOldFiles(string directoryPath, TimeSpan maxAge)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        DateTime cutoffUtc = DateTime.UtcNow - maxAge;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        TryDeleteFile(file);
                    }
                }
                catch
                {
                    // Ignore locked export files.
                }
            }
        }
        catch
        {
            // Ignore directory enumeration errors.
        }
    }
}
