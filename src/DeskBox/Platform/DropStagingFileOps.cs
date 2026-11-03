using System.Runtime.InteropServices;
using DeskBox.Services;

namespace DeskBox.Platform;

/// <summary>
/// Stages transient external temp-folder <c>CF_HDROP</c> payloads (7-Zip, WinRAR,
/// Bandizip, PeaZip, WinSCP, FileZilla, Explorer archive folders, Outlook/INetCache)
/// into DeskBox's owned <c>VirtualDrops</c> root before <c>IDropTarget::Drop</c>
/// returns <c>S_OK</c> and allows the drag source to delete its staging directory.
/// </summary>
internal static partial class DropStagingFileOps
{
    private static readonly string[] ArchiveContainerExtensions =
    [
        ".zip",
        ".7z",
        ".rar",
        ".cab",
        ".tar",
        ".gz",
        ".tgz",
        ".bz2",
        ".xz",
        ".zst",
        ".iso",
    ];

    private const uint CopyFileFailIfExists = 0x0000_0001;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CopyFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CopyFileEx(
        string lpExistingFileName,
        string lpNewFileName,
        nint lpProgressRoutine,
        nint lpData,
        nint pbCancel,
        uint dwCopyFlags);

    /// <summary>
    /// Clones an existing <c>.lnk</c> or <c>.url</c> shortcut file verbatim via
    /// Win32 <c>CopyFileExW</c>, preserving raw binary PIDLs, elevation flags,
    /// expandable blocks, and NTFS alternate data streams (such as
    /// <c>:favicon:$DATA</c> on browser Internet shortcuts) instead of nesting a
    /// broken <c>.lnk → .lnk/.url</c> wrapper.
    /// </summary>
    internal static void CopyShortcutFilePreservingStreams(
        string sourceShortcutPath,
        string destinationShortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceShortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationShortcutPath);

        string fullSource = Path.GetFullPath(sourceShortcutPath);
        string fullDestination = Path.GetFullPath(destinationShortcutPath);
        string? parentDir = Path.GetDirectoryName(fullDestination);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        if (!CopyFileEx(
                StorageMigrationNativeMethods.Extended(fullSource),
                StorageMigrationNativeMethods.Extended(fullDestination),
                nint.Zero,
                nint.Zero,
                nint.Zero,
                CopyFileFailIfExists))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CopyFileExW failed for shortcut '{fullSource}' -> '{fullDestination}' (Win32={error}).",
                Marshal.GetHRForLastWin32Error());
        }
    }

    internal static IReadOnlyList<string> StageExternalTempDropPaths(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out bool stagedAny)
    {
        return StageExternalTempDropPathsWithOrigins(
            paths,
            hasShellIdList,
            out _,
            out stagedAny);
    }

    internal static IReadOnlyList<string> StageExternalTempDropPathsWithOrigins(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out IReadOnlyList<DroppedItemOriginKind> origins,
        out bool stagedAny)
    {
        stagedAny = false;
        if (paths.Count == 0)
        {
            origins = [];
            return paths;
        }

        if (ActiveDeskBoxDragRegistry.TryMatch(paths, out _, out _))
        {
            origins = CreateUniformOrigins(paths.Count, DroppedItemOriginKind.RealFileSystemPath);
            return paths;
        }

        string tempRootWithSep = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
        string virtualDropsRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops"));
        string virtualDropsRootWithSep = EnsureTrailingSeparator(virtualDropsRoot);
        string? devRootWithSep = DeskBoxDataPathService.Current.IsDevelopmentRoot
            ? EnsureTrailingSeparator(Path.GetFullPath(DeskBoxDataPathService.Current.RootPath))
            : null;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? inetCacheWithSep = !string.IsNullOrWhiteSpace(localAppData)
            ? EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
                localAppData,
                "Microsoft",
                "Windows",
                "INetCache")))
            : null;

        bool hasCandidate = false;
        foreach (string rawPath in paths)
        {
            if (TryNormalizePath(rawPath, out string candidate) &&
                IsExternalTransientTempDropPath(
                    candidate,
                    hasShellIdList,
                    tempRootWithSep,
                    virtualDropsRootWithSep,
                    devRootWithSep,
                    inetCacheWithSep))
            {
                hasCandidate = true;
                break;
            }
        }

        if (!hasCandidate)
        {
            origins = CreateUniformOrigins(paths.Count, DroppedItemOriginKind.RealFileSystemPath);
            return paths;
        }

        var stagedPaths = new List<string>(paths.Count);
        var itemOrigins = new List<DroppedItemOriginKind>(paths.Count);
        var createdBatchDirs = new List<string>();
        string primaryBatchDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));

        try
        {
            foreach (string rawPath in paths)
            {
                if (!TryNormalizePath(rawPath, out string fullPath) ||
                    !IsExternalTransientTempDropPath(
                        fullPath,
                        hasShellIdList,
                        tempRootWithSep,
                        virtualDropsRootWithSep,
                        devRootWithSep,
                        inetCacheWithSep))
                {
                    stagedPaths.Add(rawPath);
                    itemOrigins.Add(DroppedItemOriginKind.RealFileSystemPath);
                    continue;
                }

                string leafName = Path.GetFileName(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(leafName))
                {
                    stagedPaths.Add(rawPath);
                    itemOrigins.Add(DroppedItemOriginKind.RealFileSystemPath);
                    continue;
                }

                string targetDir = primaryBatchDir;
                string destPath = Path.Combine(targetDir, leafName);
                if (Path.Exists(destPath))
                {
                    targetDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));
                    destPath = Path.Combine(targetDir, leafName);
                }

                if (File.Exists(fullPath))
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageSingleFile(fullPath, destPath);
                    stagedPaths.Add(destPath);
                    itemOrigins.Add(DroppedItemOriginKind.TransientArchiveOrCache);
                    stagedAny = true;
                }
                else if (Directory.Exists(fullPath))
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageDirectoryTree(fullPath, destPath);
                    stagedPaths.Add(destPath);
                    itemOrigins.Add(DroppedItemOriginKind.TransientArchiveOrCache);
                    stagedAny = true;
                }
                else
                {
                    stagedPaths.Add(rawPath);
                    itemOrigins.Add(DroppedItemOriginKind.RealFileSystemPath);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Transient temp staging failed: {ex.Message}");
            CleanupBatchDirs(createdBatchDirs);
            stagedAny = false;
            origins = CreateUniformOrigins(paths.Count, DroppedItemOriginKind.RealFileSystemPath);
            return paths;
        }

        if (!stagedAny)
        {
            CleanupBatchDirs(createdBatchDirs);
            origins = CreateUniformOrigins(paths.Count, DroppedItemOriginKind.RealFileSystemPath);
            return paths;
        }

        origins = itemOrigins;
        return stagedPaths;
    }

    internal static IReadOnlyList<DroppedItemOriginKind> CreateUniformOrigins(
        int count,
        DroppedItemOriginKind kind)
    {
        if (count <= 0)
        {
            return [];
        }

        var array = new DroppedItemOriginKind[count];
        if (kind != DroppedItemOriginKind.RealFileSystemPath)
        {
            Array.Fill(array, kind);
        }

        return array;
    }

    /// <summary>
    /// Stages any paths returned by an asynchronous <c>CF_HDROP</c> provider on
    /// the deferred worker into <c>VirtualDrops</c> before <c>EndOperation</c>
    /// signals the source that it may delete its temporary attachment cache.
    /// </summary>
    internal static IReadOnlyList<string> StageWorkerHDropPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        string virtualDropsRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops"));
        var stagedPaths = new List<string>(paths.Count);
        var createdBatchDirs = new List<string>();
        string primaryBatchDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));

        try
        {
            foreach (string rawPath in paths)
            {
                if (!TryNormalizePath(rawPath, out string fullPath))
                {
                    continue;
                }

                string leafName = Path.GetFileName(
                    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(leafName))
                {
                    continue;
                }

                string targetDir = primaryBatchDir;
                string destPath = Path.Combine(targetDir, leafName);
                if (Path.Exists(destPath))
                {
                    targetDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));
                    destPath = Path.Combine(targetDir, leafName);
                }

                if (File.Exists(fullPath))
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageSingleFile(fullPath, destPath);
                    stagedPaths.Add(destPath);
                }
                else if (Directory.Exists(fullPath))
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageDirectoryTree(fullPath, destPath);
                    stagedPaths.Add(destPath);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Worker CF_HDROP staging failed: {ex.Message}");
            CleanupBatchDirs(createdBatchDirs);
            return [];
        }

        if (stagedPaths.Count == 0)
        {
            CleanupBatchDirs(createdBatchDirs);
        }

        return stagedPaths;
    }

    internal static bool IsExternalTransientTempDropPath(
        string fullPath,
        bool hasShellIdList,
        string tempRootWithSep,
        string virtualDropsRootWithSep,
        string? devRootWithSep,
        string? inetCacheWithSep)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        // Already inside DeskBox's owned VirtualDrops staging root.
        if (fullPath.StartsWith(virtualDropsRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Smoke harness / isolated test roots may sit under %TEMP%; preserve
        // their native Move/Copy semantics.
        if (!string.IsNullOrEmpty(devRootWithSep) &&
            fullPath.StartsWith(devRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Outlook / IE / WebView2 attachment cache under LocalAppData\Microsoft\Windows\INetCache.
        if (!string.IsNullOrEmpty(inetCacheWithSep) &&
            fullPath.StartsWith(inetCacheWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ContainsSegmentPrefix(fullPath, "Content.Outlook") ||
            IsMsixPackageTransientDropPath(fullPath))
        {
            return true;
        }

        if (!fullPath.StartsWith(tempRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Archive managers and FTP clients (7-Zip, WinRAR, Bandizip, PeaZip,
        // WinSCP, FileZilla) emit CF_HDROP into %TEMP% without Shell IDList Array
        // and purge the staging directory as soon as DoDragDrop returns.
        if (!hasShellIdList)
        {
            return true;
        }

        // Explorer's zipfldr / archive Shell extensions DO include Shell IDList
        // Array while staging extracted entries under %TEMP%\Temp1_*.zip\...
        string relative = fullPath[tempRootWithSep.Length..];
        int sepIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        ReadOnlySpan<char> topSegment = sepIndex >= 0
            ? relative.AsSpan(0, sepIndex)
            : relative.AsSpan();
        return IsKnownTransientTempDirectorySegment(topSegment);
    }

    internal static bool IsKnownTransientTempDirectorySegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
        {
            return false;
        }

        if (segment.StartsWith("7z", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("Rar$", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("BNZ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("PeaZip", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("scp", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("fz3", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("wz", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("Content.Outlook", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Explorer zipfldr pattern: Temp1_archive.zip .. Temp99_archive.zip
        if (segment.StartsWith("Temp", StringComparison.OrdinalIgnoreCase) &&
            segment.Length > 6)
        {
            int idx = 4;
            while (idx < segment.Length && char.IsAsciiDigit(segment[idx]))
            {
                idx++;
            }

            if (idx > 4 && idx < segment.Length && segment[idx] == '_')
            {
                foreach (string ext in ArchiveContainerExtensions)
                {
                    if (segment.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Detects MSIX/AppContainer transient drop staging paths under
    /// <c>%LOCALAPPDATA%\Packages\&lt;PFN&gt;\AC\INetCache</c>,
    /// <c>AC\Temp</c>, and <c>TempState</c> (New Outlook, Teams 2.0, NanaZip, etc.).
    /// </summary>
    internal static bool IsMsixPackageTransientDropPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string normalized = fullPath.Replace('/', '\\');
        int packagesIdx = normalized.IndexOf(@"\Packages\", StringComparison.OrdinalIgnoreCase);
        if (packagesIdx < 0)
        {
            return false;
        }

        ReadOnlySpan<char> afterPackages = normalized.AsSpan(packagesIdx + @"\Packages\".Length);
        int familyEnd = afterPackages.IndexOf('\\');
        if (familyEnd <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> subPath = afterPackages[familyEnd..];
        return subPath.StartsWith(@"\AC\INetCache\", StringComparison.OrdinalIgnoreCase) ||
               subPath.StartsWith(@"\AC\Temp\", StringComparison.OrdinalIgnoreCase) ||
               subPath.StartsWith(@"\TempState\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExternalTransientTempDropPath(
        string path,
        bool hasShellIdList = false)
    {
        if (!TryNormalizePath(path, out string fullPath) ||
            ActiveDeskBoxDragRegistry.TryMatch([fullPath], out _, out _))
        {
            return false;
        }

        string tempRootWithSep = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
        string virtualDropsRootWithSep = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops")));
        string? devRootWithSep = DeskBoxDataPathService.Current.IsDevelopmentRoot
            ? EnsureTrailingSeparator(Path.GetFullPath(DeskBoxDataPathService.Current.RootPath))
            : null;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? inetCacheWithSep = !string.IsNullOrWhiteSpace(localAppData)
            ? EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
                localAppData,
                "Microsoft",
                "Windows",
                "INetCache")))
            : null;

        return IsExternalTransientTempDropPath(
            fullPath,
            hasShellIdList,
            tempRootWithSep,
            virtualDropsRootWithSep,
            devRootWithSep,
            inetCacheWithSep);
    }

    internal static string StagePathIntoDirectory(
        string sourcePath,
        string targetDirectory)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        string leafName = Path.GetFileName(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leafName))
        {
            leafName = "item";
        }

        Directory.CreateDirectory(targetDirectory);
        string destPath = Path.Combine(targetDirectory, leafName);
        if (Path.Exists(destPath))
        {
            string uniqueSubDir = Path.Combine(targetDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uniqueSubDir);
            destPath = Path.Combine(uniqueSubDir, leafName);
        }

        if (File.Exists(fullPath))
        {
            StageSingleFile(fullPath, destPath);
            return destPath;
        }

        if (Directory.Exists(fullPath))
        {
            StageDirectoryTree(fullPath, destPath);
            return destPath;
        }

        throw new FileNotFoundException("Transient drop source no longer exists.", fullPath);
    }

    private static void StageSingleFile(string sourceFile, string destFile)
    {
        if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(sourceFile))
        {
            throw new IOException($"Refused to stage filesystem link '{sourceFile}'.");
        }

        // Always copy into an independent inode rather than CreateHardLink so
        // the source application cannot overwrite or truncate the staged data
        // through its shared temp inode after Drop returns.
        File.Copy(sourceFile, destFile, overwrite: false);
    }

    private static void StageDirectoryTree(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                StageSingleFile(file, Path.Combine(destDir, fileName));
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(subDir);
            }
            catch
            {
                continue;
            }

            // Never follow reparse points / junctions inside untrusted temp trees.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string dirName = Path.GetFileName(subDir);
            if (!string.IsNullOrWhiteSpace(dirName))
            {
                StageDirectoryTree(subDir, Path.Combine(destDir, dirName));
            }
        }
    }

    internal static void CleanupVirtualDropBatchDirectory(string? temporaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Validates and builds a safe relative destination path inside
    /// <paramref name="temporaryDirectory"/> for a <c>CFSTR_FILEDESCRIPTORW</c>
    /// entry while rejecting rooted paths, drive specifiers, alternate data
    /// streams, and <c>..</c> traversal segments.
    /// </summary>
    internal static bool TryBuildVirtualDropDestinationPath(
        string temporaryDirectory,
        string? rawDescriptorName,
        bool isDirectory,
        int index,
        out string fullDestinationPath,
        out string topLevelEntryPath)
    {
        fullDestinationPath = string.Empty;
        topLevelEntryPath = string.Empty;
        if (string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            return false;
        }

        string tempRootFull = Path.GetFullPath(temporaryDirectory);
        string tempRootWithSep = EnsureTrailingSeparator(tempRootFull);

        if (string.IsNullOrWhiteSpace(rawDescriptorName))
        {
            if (isDirectory)
            {
                return false;
            }

            string fallbackName = $"Dropped file {index + 1}";
            fullDestinationPath = FileService.GetAvailablePath(
                Path.Combine(tempRootFull, fallbackName));
            topLevelEntryPath = fullDestinationPath;
            return true;
        }

        if (rawDescriptorName.IndexOfAny([':', '\0']) >= 0 ||
            rawDescriptorName[0] is '\\' or '/')
        {
            return false;
        }

        string[] rawSegments = rawDescriptorName.Split(
            ['\\', '/'],
            StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            return false;
        }

        var cleanSegments = new List<string>(rawSegments.Length);
        for (int i = 0; i < rawSegments.Length; i++)
        {
            string rawSegment = rawSegments[i];
            if (rawSegment is "." or "..")
            {
                return false;
            }

            string sanitized = FileService.SanitizeFileSystemName(rawSegment);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
            {
                if (rawSegments.Length == 1 && !isDirectory)
                {
                    sanitized = $"Dropped file {index + 1}";
                }
                else
                {
                    return false;
                }
            }

            cleanSegments.Add(sanitized);
        }

        string candidatePath = tempRootFull;
        foreach (string segment in cleanSegments)
        {
            candidatePath = Path.Combine(candidatePath, segment);
        }

        string normalizedCandidate = Path.GetFullPath(candidatePath);
        if (!normalizedCandidate.StartsWith(tempRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (isDirectory)
            {
                if (File.Exists(normalizedCandidate))
                {
                    return false;
                }

                Directory.CreateDirectory(normalizedCandidate);
                fullDestinationPath = normalizedCandidate;
                topLevelEntryPath = Path.Combine(tempRootFull, cleanSegments[0]);
                return true;
            }

            if (cleanSegments.Count == 1)
            {
                fullDestinationPath = FileService.GetAvailablePath(normalizedCandidate);
                topLevelEntryPath = fullDestinationPath;
                return true;
            }

            string? parentDir = Path.GetDirectoryName(normalizedCandidate);
            if (string.IsNullOrWhiteSpace(parentDir) || File.Exists(parentDir))
            {
                return false;
            }

            Directory.CreateDirectory(parentDir);
            fullDestinationPath = FileService.GetAvailablePath(normalizedCandidate);
            topLevelEntryPath = Path.Combine(tempRootFull, cleanSegments[0]);
            return true;
        }
        catch
        {
            fullDestinationPath = string.Empty;
            topLevelEntryPath = string.Empty;
            return false;
        }
    }

    private static void EnsureBatchDirCreated(string dir, List<string> createdBatchDirs)
    {
        if (!createdBatchDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dir);
            createdBatchDirs.Add(dir);
        }
    }

    private static void CleanupBatchDirs(List<string> createdBatchDirs)
    {
        foreach (string dir in createdBatchDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static bool TryNormalizePath(string rawPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(rawPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
    }

    private static bool ContainsSegmentPrefix(string fullPath, string prefix)
    {
        int start = 0;
        while (start < fullPath.Length)
        {
            int nextSep = fullPath.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                start);
            ReadOnlySpan<char> segment = nextSep >= 0
                ? fullPath.AsSpan(start, nextSep - start)
                : fullPath.AsSpan(start);
            if (segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (nextSep < 0)
            {
                break;
            }

            start = nextSep + 1;
        }

        return false;
    }

    private static readonly string[] StaleDropBucketNames =
    [
        "VirtualDrops",
        "StagedDrops",
        "DroppedFiles",
    ];

    internal static int CleanupStaleDropStagingDirectories(
        string? tempRootOverride = null,
        TimeSpan? maxAge = null,
        DateTimeOffset? nowUtc = null)
    {
        string baseTemp;
        try
        {
            baseTemp = Path.GetFullPath(
                string.IsNullOrWhiteSpace(tempRootOverride)
                    ? Path.Combine(Path.GetTempPath(), "DeskBox")
                    : tempRootOverride);
        }
        catch
        {
            return 0;
        }

        if (!Directory.Exists(baseTemp))
        {
            return 0;
        }

        TimeSpan effectiveMaxAge = maxAge ?? TimeSpan.FromHours(2);
        DateTime cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime - effectiveMaxAge;
        int removedCount = 0;

        foreach (string bucketName in StaleDropBucketNames)
        {
            string bucketPath = Path.Combine(baseTemp, bucketName);
            if (!Directory.Exists(bucketPath))
            {
                continue;
            }

            try
            {
                var bucketInfo = new DirectoryInfo(bucketPath);
                if ((bucketInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (DirectoryInfo batchDir in bucketInfo.EnumerateDirectories())
                {
                    try
                    {
                        if ((batchDir.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        DateTime newestTimestampUtc = batchDir.LastWriteTimeUtc > batchDir.CreationTimeUtc
                            ? batchDir.LastWriteTimeUtc
                            : batchDir.CreationTimeUtc;
                        if (newestTimestampUtc > cutoffUtc)
                        {
                            continue;
                        }

                        DeleteStagingTreeNonReparse(batchDir);
                        removedCount++;
                    }
                    catch
                    {
                        // Ignore active or locked drop staging batches.
                    }
                }
            }
            catch
            {
                // Ignore inaccessible temp buckets.
            }
        }

        return removedCount;
    }

    private static void DeleteStagingTreeNonReparse(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        foreach (DirectoryInfo childDir in directory.EnumerateDirectories())
        {
            DeleteStagingTreeNonReparse(childDir);
        }

        foreach (FileInfo file in directory.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }

            file.Delete();
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        directory.Delete(recursive: false);
    }

    internal static bool IsUnderVirtualDropsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryNormalizePath(path, out string fullPath))
        {
            return false;
        }

        string virtualDropsRootWithSep = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops")));
        return fullPath.StartsWith(virtualDropsRootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies any temporary/staged <see cref="DroppedFilePath"/> entries into
    /// <paramref name="durableRootDirectory"/> before the caller's drop scope
    /// deletes <c>VirtualDrops</c>, returning durable paths for all items.
    /// </summary>
    internal static IReadOnlyList<string> PersistTemporaryDroppedFiles(
        IReadOnlyList<DroppedFilePath> files,
        string durableRootDirectory)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var resolved = new List<string>(files.Count);
        string? batchDir = null;
        foreach (DroppedFilePath item in files)
        {
            if (!TryNormalizePath(item.Path, out string fullPath))
            {
                continue;
            }

            if (!item.ForceManagedCopy && !IsUnderVirtualDropsDirectory(fullPath))
            {
                resolved.Add(fullPath);
                continue;
            }

            string leafName = !string.IsNullOrWhiteSpace(item.DisplayName)
                ? Path.GetFileName(item.DisplayName)
                : Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leafName))
            {
                continue;
            }

            batchDir ??= Path.Combine(durableRootDirectory, Guid.NewGuid().ToString("N"));
            string destPath = Path.Combine(batchDir, leafName);
            if (Path.Exists(destPath))
            {
                destPath = Path.Combine(durableRootDirectory, Guid.NewGuid().ToString("N"), leafName);
            }

            string? destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            if (File.Exists(fullPath))
            {
                StageSingleFile(fullPath, destPath);
                resolved.Add(destPath);
            }
            else if (Directory.Exists(fullPath))
            {
                StageDirectoryTree(fullPath, destPath);
                resolved.Add(destPath);
            }
        }

        return resolved;
    }
}
