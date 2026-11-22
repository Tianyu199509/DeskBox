namespace DeskBox.Platform;

/// <summary>
/// Confined filesystem discovery and empty-folder cleanup helpers for DeskBox's
/// managed attachment trees (<c>attachments/&lt;itemId&gt;/</c>). Never deletes files
/// directly: file removal always goes through the caller's Recycle-Bin kernel.
/// </summary>
internal static class AttachmentCleanupFileOps
{
    public static IReadOnlyList<string> FindUnreferencedManagedFiles(
        string managedRootDirectory,
        IReadOnlySet<string> referencedPaths,
        IReadOnlySet<string>? knownTombstoneItemIds = null,
        TimeSpan? unknownDirectoryGracePeriod = null)
    {
        if (string.IsNullOrWhiteSpace(managedRootDirectory) ||
            !Directory.Exists(managedRootDirectory))
        {
            return [];
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(managedRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return [];
        }

        if (!StorageMigrationNativeMethods.IsSafeTraversablePath(normalizedRoot))
        {
            return [];
        }

        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        DateTime unknownCutoffUtc = unknownDirectoryGracePeriod is { } grace && grace > TimeSpan.Zero
            ? DateTime.UtcNow - grace
            : DateTime.MaxValue;

        var results = new List<string>();
        var directories = new Stack<(string Path, string? TopLevelBucketId)>();
        directories.Push((normalizedRoot, null));

        while (directories.Count > 0)
        {
            (string currentDir, string? bucketId) = directories.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
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

                if (!fullFile.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                    referencedPaths.Contains(fullFile))
                {
                    continue;
                }

                bool isKnownTombstone = bucketId is not null &&
                    knownTombstoneItemIds?.Contains(bucketId) == true;
                if (!isKnownTombstone && unknownCutoffUtc != DateTime.MaxValue)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(fullFile) > unknownCutoffUtc)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                results.Add(fullFile);
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (string subdirectory in subdirectories)
            {
                string fullSubdirectory;
                try
                {
                    fullSubdirectory = Path.GetFullPath(subdirectory);
                }
                catch
                {
                    continue;
                }

                if (!fullSubdirectory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                    !StorageMigrationNativeMethods.IsSafeTraversablePath(fullSubdirectory))
                {
                    continue;
                }

                string nextBucketId = bucketId ?? Path.GetFileName(fullSubdirectory);
                directories.Push((fullSubdirectory, nextBucketId));
            }
        }

        return results;
    }

    public static void RemoveEmptyManagedSubdirectories(string managedRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(managedRootDirectory) ||
            !Directory.Exists(managedRootDirectory))
        {
            return;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(managedRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return;
        }

        if (!StorageMigrationNativeMethods.IsSafeTraversablePath(normalizedRoot))
        {
            return;
        }

        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        var postOrderDirectories = new List<string>();
        var stack = new Stack<string>();
        stack.Push(normalizedRoot);

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            if (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                postOrderDirectories.Add(current);
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string child in children)
            {
                string fullChild;
                try
                {
                    fullChild = Path.GetFullPath(child);
                }
                catch
                {
                    continue;
                }

                if (!fullChild.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                    !StorageMigrationNativeMethods.IsSafeTraversablePath(fullChild))
                {
                    continue;
                }

                stack.Push(fullChild);
            }
        }

        for (int index = postOrderDirectories.Count - 1; index >= 0; index--)
        {
            string candidate = postOrderDirectories[index];
            try
            {
                if (Directory.Exists(candidate) &&
                    !Directory.EnumerateFileSystemEntries(candidate).Any())
                {
                    Directory.Delete(candidate, recursive: false);
                }
            }
            catch
            {
                // Best-effort empty bucket pruning.
            }
        }
    }
}
