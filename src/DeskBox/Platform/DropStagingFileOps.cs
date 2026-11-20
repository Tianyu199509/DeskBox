using System.Runtime.InteropServices;
using DeskBox.Services;
using DeskBox.Services.DragDrop;

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

    internal const long DefaultSyncStagingByteLimit = 512L * 1024 * 1024;

    internal static string GetStagingRootDirectory(bool? isMicrosoftStoreOverride = null)
    {
        bool isStore = isMicrosoftStoreOverride ??
            (AppDistributionService.Current.IsPackaged || AppDistributionService.Current.IsMicrosoftStore);
        if (isStore)
        {
            string? localLow = Win32Helper.TryGetLocalAppDataLowKnownFolderPath();
            if (string.IsNullOrWhiteSpace(localLow))
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(userProfile))
                {
                    localLow = Path.Combine(userProfile, "AppData", "LocalLow");
                }
            }

            if (!string.IsNullOrWhiteSpace(localLow))
            {
                return Path.GetFullPath(Path.Combine(localLow, "DeskBox", "TempStaging"));
            }
        }

        return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DeskBox"));
    }

    internal static string GetVirtualDropsRootDirectory(bool? isMicrosoftStoreOverride = null) =>
        Path.Combine(GetStagingRootDirectory(isMicrosoftStoreOverride), "VirtualDrops");

    internal static string GetDroppedFilesRootDirectory(bool? isMicrosoftStoreOverride = null) =>
        Path.Combine(GetStagingRootDirectory(isMicrosoftStoreOverride), "DroppedFiles");

    /// <summary>
    /// Verifies that the DeskBox staging root (<c>LocalLow\DeskBox\TempStaging</c>
    /// or <c>%TEMP%\DeskBox</c>) and its bucket subdirectory are not NTFS reparse
    /// points (junctions/symlinks) before creating or writing staging files.
    /// Protects the Low-IL writable <c>LocalLow</c> tree from symlink redirection.
    /// </summary>
    internal static void EnsureSafeStagingRootDirectory(string bucketDirectory)
    {
        string fullBucket = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bucketDirectory));
        string? parentDir = Path.GetDirectoryName(fullBucket);
        string? grandParentDir = string.IsNullOrWhiteSpace(parentDir)
            ? null
            : Path.GetDirectoryName(parentDir);

        foreach (string? candidate in new[] { grandParentDir, parentDir, fullBucket })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            FileAttributes attrs = File.GetAttributes(StorageMigrationNativeMethods.Extended(candidate));
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Refusing to write drop staging files through reparse point '{candidate}'.");
            }
        }
    }

    private static IEnumerable<string> EnumerateKnownStagingRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in new[]
        {
            GetStagingRootDirectory(),
            GetStagingRootDirectory(isMicrosoftStoreOverride: true),
            GetStagingRootDirectory(isMicrosoftStoreOverride: false),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    internal static bool IsAnyDeskBoxStagingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryNormalizePath(path, out string fullPath))
        {
            return false;
        }

        foreach (string root in EnumerateKnownStagingRoots())
        {
            string rootWithSep = EnsureTrailingSeparator(root);
            if (fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private const uint CopyFileFailIfExists = 0x0000_0001;
    private const uint CopyFileAllowDecryptedDestination = 0x0000_0008;
    private const int ErrorEncryptionFailed = 6000;

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
            out stagedAny,
            out _,
            maxTotalBytes: 0);
    }

    internal static IReadOnlyList<string> StageExternalTempDropPaths(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out bool stagedAny,
        long maxTotalBytes)
    {
        return StageExternalTempDropPathsWithOrigins(
            paths,
            hasShellIdList,
            out _,
            out stagedAny,
            out _,
            maxTotalBytes);
    }

    internal static IReadOnlyList<string> StageExternalTempDropPathsWithOrigins(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out IReadOnlyList<DroppedItemOriginKind> origins,
        out bool stagedAny)
    {
        return StageExternalTempDropPathsWithOrigins(
            paths,
            hasShellIdList,
            out origins,
            out stagedAny,
            out _,
            maxTotalBytes: 0);
    }

    internal static IReadOnlyList<string> StageExternalTempDropPathsWithOrigins(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out IReadOnlyList<DroppedItemOriginKind> origins,
        out bool stagedAny,
        long maxTotalBytes)
    {
        return StageExternalTempDropPathsWithOrigins(
            paths,
            hasShellIdList,
            out origins,
            out stagedAny,
            out _,
            maxTotalBytes);
    }

    internal static IReadOnlyList<string> StageExternalTempDropPathsWithOrigins(
        IReadOnlyList<string> paths,
        bool hasShellIdList,
        out IReadOnlyList<DroppedItemOriginKind> origins,
        out bool stagedAny,
        out int skippedOrFailedCount,
        long maxTotalBytes = 0)
    {
        stagedAny = false;
        skippedOrFailedCount = 0;
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
        string? unvirtualizedTempRootWithSep = ResolveUnvirtualizedUserTempRootWithSep(tempRootWithSep);
        string virtualDropsRoot = GetVirtualDropsRootDirectory();
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
                    inetCacheWithSep,
                    unvirtualizedTempRootWithSep))
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

        EnsureSafeStagingRootDirectory(GetStagingRootDirectory());
        EnsureSafeStagingRootDirectory(virtualDropsRoot);

        long effectiveByteLimit = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            virtualDropsRoot,
            explicitLimitBytes: maxTotalBytes > 0 ? maxTotalBytes : null);
        long stagedBytes = 0;
        var stagedPaths = new List<string>(paths.Count);
        var itemOrigins = new List<DroppedItemOriginKind>(paths.Count);
        var createdBatchDirs = new List<string>();
        string primaryBatchDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));

        foreach (string rawPath in paths)
        {
            if (!TryNormalizePath(rawPath, out string fullPath) ||
                !IsExternalTransientTempDropPath(
                    fullPath,
                    hasShellIdList,
                    tempRootWithSep,
                    virtualDropsRootWithSep,
                    devRootWithSep,
                    inetCacheWithSep,
                    unvirtualizedTempRootWithSep))
            {
                stagedPaths.Add(rawPath);
                itemOrigins.Add(DroppedItemOriginKind.RealFileSystemPath);
                continue;
            }

            string leafName = Path.GetFileName(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leafName))
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Skipped transient temp drop with empty leaf name: '{fullPath}'.");
                continue;
            }

            bool isFile = File.Exists(fullPath);
            bool isDirectory = !isFile && Directory.Exists(fullPath);
            if (!isFile && !isDirectory)
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Skipped missing transient temp drop item: '{fullPath}'.");
                continue;
            }

            if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(fullPath))
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Refused transient temp filesystem link: '{fullPath}'.");
                continue;
            }

            string targetDir = primaryBatchDir;
            string destPath = Path.Combine(targetDir, leafName);
            if (Path.Exists(destPath))
            {
                targetDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));
                destPath = Path.Combine(targetDir, leafName);
            }

            long bytesBeforeItem = stagedBytes;
            try
            {
                if (isFile)
                {
                    long fileLength = new FileInfo(fullPath).Length;
                    if (fileLength < 0 || fileLength > effectiveByteLimit - stagedBytes)
                    {
                        throw new IOException(
                            $"Transient staging byte limit ({effectiveByteLimit} bytes) exceeded for '{fullPath}'.");
                    }

                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageSingleFile(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                }
                else
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageDirectoryTree(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                }

                stagedPaths.Add(destPath);
                itemOrigins.Add(DroppedItemOriginKind.TransientArchiveOrCache);
                stagedAny = true;
            }
            catch (Exception ex)
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Transient temp staging skipped '{fullPath}': {ex.Message}");
                RollbackFailedStagedItem(destPath, targetDir, createdBatchDirs);
                stagedBytes = bytesBeforeItem;
            }
        }

        if (!stagedAny)
        {
            CleanupBatchDirs(createdBatchDirs);
        }

        if (stagedPaths.Count == 0)
        {
            origins = [];
            return [];
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
    internal static IReadOnlyList<string> StageWorkerHDropPaths(
        IReadOnlyList<string> paths,
        long maxTotalBytes = 0) =>
        StageWorkerHDropPaths(paths, out _, maxTotalBytes);

    internal static IReadOnlyList<string> StageWorkerHDropPaths(
        IReadOnlyList<string> paths,
        out int skippedOrFailedCount,
        long maxTotalBytes = 0)
    {
        skippedOrFailedCount = 0;
        if (paths.Count == 0)
        {
            return [];
        }

        string virtualDropsRoot = GetVirtualDropsRootDirectory();
        EnsureSafeStagingRootDirectory(virtualDropsRoot);
        long effectiveByteLimit = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            virtualDropsRoot,
            explicitLimitBytes: maxTotalBytes > 0 ? maxTotalBytes : null);
        long stagedBytes = 0;
        var stagedPaths = new List<string>(paths.Count);
        var createdBatchDirs = new List<string>();
        string primaryBatchDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));

        foreach (string rawPath in paths)
        {
            if (!TryNormalizePath(rawPath, out string fullPath))
            {
                skippedOrFailedCount++;
                continue;
            }

            string leafName = Path.GetFileName(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(leafName))
            {
                skippedOrFailedCount++;
                continue;
            }

            bool isFile = File.Exists(fullPath);
            bool isDirectory = !isFile && Directory.Exists(fullPath);
            if (!isFile && !isDirectory)
            {
                skippedOrFailedCount++;
                continue;
            }

            if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(fullPath))
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Worker CF_HDROP refused filesystem link: '{fullPath}'.");
                continue;
            }

            string targetDir = primaryBatchDir;
            string destPath = Path.Combine(targetDir, leafName);
            if (Path.Exists(destPath))
            {
                targetDir = Path.Combine(virtualDropsRoot, Guid.NewGuid().ToString("N"));
                destPath = Path.Combine(targetDir, leafName);
            }

            long bytesBeforeItem = stagedBytes;
            try
            {
                if (isFile)
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageSingleFile(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                }
                else
                {
                    EnsureBatchDirCreated(targetDir, createdBatchDirs);
                    StageDirectoryTree(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                }

                stagedPaths.Add(destPath);
            }
            catch (Exception ex)
            {
                skippedOrFailedCount++;
                App.Log($"[DropTarget] Worker CF_HDROP staging skipped '{fullPath}': {ex.Message}");
                RollbackFailedStagedItem(destPath, targetDir, createdBatchDirs);
                stagedBytes = bytesBeforeItem;
            }
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
        string? inetCacheWithSep,
        string? unvirtualizedTempRootWithSep = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        // Already inside DeskBox's owned VirtualDrops / DroppedFiles staging roots.
        if (fullPath.StartsWith(virtualDropsRootWithSep, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                tempRootWithSep + "DeskBox" + Path.DirectorySeparatorChar + "DroppedFiles" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                tempRootWithSep + "DeskBox" + Path.DirectorySeparatorChar + "VirtualDrops" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                EnsureTrailingSeparator(GetVirtualDropsRootDirectory()),
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                EnsureTrailingSeparator(GetDroppedFilesRootDirectory()),
                StringComparison.OrdinalIgnoreCase))
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

        string? matchedTempRootWithSep = null;
        if (fullPath.StartsWith(tempRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            matchedTempRootWithSep = tempRootWithSep;
        }
        else if (!string.IsNullOrEmpty(unvirtualizedTempRootWithSep) &&
                 fullPath.StartsWith(unvirtualizedTempRootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            string droppedFilesUnderUnvirt =
                unvirtualizedTempRootWithSep + "DeskBox" + Path.DirectorySeparatorChar + "DroppedFiles" + Path.DirectorySeparatorChar;
            string virtualDropsUnderUnvirt =
                unvirtualizedTempRootWithSep + "DeskBox" + Path.DirectorySeparatorChar + "VirtualDrops" + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(virtualDropsUnderUnvirt, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(droppedFilesUnderUnvirt, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matchedTempRootWithSep = unvirtualizedTempRootWithSep;
        }

        if (matchedTempRootWithSep is null)
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
        string relative = fullPath[matchedTempRootWithSep.Length..];
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
    /// Detects MSIX/AppContainer and Desktop-Bridge (<c>LocalCache</c>)
    /// transient drop staging paths under
    /// <c>%LOCALAPPDATA%\Packages\&lt;PFN&gt;\AC\INetCache</c>,
    /// <c>AC\Temp</c>, <c>TempState</c>, <c>LocalCache\Local\Temp</c>, and
    /// <c>LocalCache\Local\Microsoft\Windows\INetCache</c>.
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
               subPath.StartsWith(@"\TempState\", StringComparison.OrdinalIgnoreCase) ||
               subPath.StartsWith(@"\LocalCache\Local\Temp\", StringComparison.OrdinalIgnoreCase) ||
               subPath.StartsWith(@"\LocalCache\Local\Microsoft\Windows\INetCache\", StringComparison.OrdinalIgnoreCase);
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
        string? unvirtualizedTempRootWithSep = ResolveUnvirtualizedUserTempRootWithSep(tempRootWithSep);
        string virtualDropsRootWithSep = EnsureTrailingSeparator(GetVirtualDropsRootDirectory());
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
            inetCacheWithSep,
            unvirtualizedTempRootWithSep);
    }

    private static string? ResolveUnvirtualizedUserTempRootWithSep(string normalizedTempRootWithSep)
    {
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                return null;
            }

            string candidate = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(
                userProfile,
                "AppData",
                "Local",
                "Temp")));
            return string.Equals(candidate, normalizedTempRootWithSep, StringComparison.OrdinalIgnoreCase)
                ? null
                : candidate;
        }
        catch
        {
            return null;
        }
    }

    internal static string StagePathIntoDirectory(
        string sourcePath,
        string targetDirectory)
    {
        long stagedBytes = 0;
        return StagePathIntoDirectory(
            sourcePath,
            targetDirectory,
            ref stagedBytes,
            maxTotalBytes: 0);
    }

    internal static string StagePathIntoDirectory(
        string sourcePath,
        string targetDirectory,
        ref long stagedBytes,
        long maxTotalBytes)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        bool isFile = File.Exists(fullPath);
        bool isDirectory = !isFile && Directory.Exists(fullPath);
        if (!isFile && !isDirectory)
        {
            throw new FileNotFoundException("Transient drop source no longer exists.", fullPath);
        }

        if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(fullPath))
        {
            throw new IOException($"Refused to stage filesystem link '{fullPath}'.");
        }

        string leafName = Path.GetFileName(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leafName))
        {
            leafName = "item";
        }

        Directory.CreateDirectory(targetDirectory);
        string destPath = Path.Combine(targetDirectory, leafName);
        string? uniqueSubDir = null;
        if (Path.Exists(destPath))
        {
            uniqueSubDir = Path.Combine(targetDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uniqueSubDir);
            destPath = Path.Combine(uniqueSubDir, leafName);
        }

        long effectiveByteLimit = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            targetDirectory,
            explicitLimitBytes: maxTotalBytes > 0 ? maxTotalBytes : null);
        long bytesBeforeItem = stagedBytes;
        try
        {
            if (isFile)
            {
                StageSingleFile(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
            }
            else
            {
                StageDirectoryTree(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
            }

            return destPath;
        }
        catch
        {
            stagedBytes = bytesBeforeItem;
            RollbackFailedStagedItem(
                destPath,
                uniqueSubDir ?? targetDirectory,
                createdBatchDirs: []);
            throw;
        }
    }

    private static void StageSingleFile(
        string sourceFile,
        string destFile,
        ref long stagedBytes,
        long maxTotalBytes)
    {
        if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(sourceFile))
        {
            throw new IOException($"Refused to stage filesystem link '{sourceFile}'.");
        }

        long fileLength = new FileInfo(StorageMigrationNativeMethods.Extended(sourceFile)).Length;
        if (fileLength < 0 || fileLength > maxTotalBytes - stagedBytes)
        {
            throw new IOException(
                $"Transient staging byte limit ({maxTotalBytes} bytes) exceeded for '{sourceFile}'.");
        }

        // Always copy into an independent inode rather than CreateHardLink so
        // the source application cannot overwrite or truncate the staged data
        // through its shared temp inode after Drop returns.
        string extendedSource = StorageMigrationNativeMethods.Extended(sourceFile);
        string extendedDest = StorageMigrationNativeMethods.Extended(destFile);
        CopyFileWithReadOnlyAndDecryptedFallback(extendedSource, extendedDest, overwrite: false);

        stagedBytes += fileLength;
    }

    internal static void CopyFileWithReadOnlyAndDecryptedFallback(
        string sourcePath,
        string destPath,
        bool overwrite)
    {
        string extendedSource = StorageMigrationNativeMethods.Extended(sourcePath);
        string extendedDest = StorageMigrationNativeMethods.Extended(destPath);
        try
        {
            File.Copy(extendedSource, extendedDest, overwrite);
        }
        catch (Exception ex) when (IsEncryptionFailedException(ex))
        {
            uint flags = CopyFileAllowDecryptedDestination | (overwrite ? 0u : CopyFileFailIfExists);
            if (!CopyFileEx(
                    extendedSource,
                    extendedDest,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero,
                    flags))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"CopyFileExW(ALLOW_DECRYPTED_DESTINATION) failed for '{sourcePath}' -> '{destPath}' (Win32={error}).",
                    Marshal.GetHRForLastWin32Error());
            }
        }

        ClearReadOnlyAttributeIfPresent(extendedDest);
    }

    internal static void ClearReadOnlyAttributeIfPresent(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string extended = StorageMigrationNativeMethods.Extended(path);
            FileAttributes attrs = File.GetAttributes(extended);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(extended, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // Best-effort attribute normalization.
        }
    }

    private static bool IsEncryptionFailedException(Exception ex)
    {
        const int HResultEncryptionFailed = unchecked((int)0x80071770);
        return ex.HResult == HResultEncryptionFailed;
    }

    private static void StageDirectoryTree(
        string sourceDir,
        string destDir,
        ref long stagedBytes,
        long maxTotalBytes)
    {
        if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(sourceDir))
        {
            throw new IOException($"Refused to stage filesystem link directory '{sourceDir}'.");
        }

        Directory.CreateDirectory(StorageMigrationNativeMethods.Extended(destDir));

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            bool isReparse;
            try
            {
                isReparse = StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(file);
            }
            catch
            {
                continue;
            }

            // Never follow file symlinks / reparse points inside untrusted temp trees;
            // skip them cleanly without aborting valid siblings.
            if (isReparse)
            {
                continue;
            }

            string fileName = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                StageSingleFile(file, Path.Combine(destDir, fileName), ref stagedBytes, maxTotalBytes);
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            bool isReparse;
            try
            {
                isReparse = StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(subDir);
            }
            catch
            {
                continue;
            }

            // Never follow reparse points / junctions inside untrusted temp trees.
            if (isReparse)
            {
                continue;
            }

            string dirName = Path.GetFileName(subDir);
            if (!string.IsNullOrWhiteSpace(dirName))
            {
                StageDirectoryTree(subDir, Path.Combine(destDir, dirName), ref stagedBytes, maxTotalBytes);
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
                DeleteStagingTreeNonReparse(new DirectoryInfo(temporaryDirectory));
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

    private static void RollbackFailedStagedItem(
        string destPath,
        string targetDir,
        List<string> createdBatchDirs)
    {
        try
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            else if (Directory.Exists(destPath))
            {
                DeleteStagingTreeNonReparse(new DirectoryInfo(destPath));
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(targetDir) &&
                !Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                Directory.Delete(targetDir, recursive: false);
                createdBatchDirs.RemoveAll(d => string.Equals(d, targetDir, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
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
                    DeleteStagingTreeNonReparse(new DirectoryInfo(dir));
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
        "QuickCaptureExports",
        "ClipboardDragExports",
        "ClipboardCopy",
    ];

    private static long s_lastThrottledCleanupTicks;

    internal static void TriggerThrottledStaleStagingCleanup()
    {
        long nowTicks = Environment.TickCount64;
        long previousTicks = Interlocked.Read(ref s_lastThrottledCleanupTicks);
        if (previousTicks != 0 && nowTicks - previousTicks < 10 * 60 * 1000L)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_lastThrottledCleanupTicks, nowTicks, previousTicks) != previousTicks)
        {
            return;
        }

        _ = Task.Run(static () =>
        {
            try
            {
                _ = CleanupStaleDropStagingDirectories();
            }
            catch
            {
                // Best-effort background sweep.
            }
        });
    }

    internal const string LaunchedRetentionMarkerFileName = ".deskbox-launched-keep";
    internal static readonly TimeSpan LaunchedVirtualRetentionDuration = TimeSpan.FromHours(24);

    private static readonly object s_activeStagingLeaseGate = new();
    private static readonly Dictionary<string, int> s_activeStagingBatchLeases =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pins DeskBox-owned <c>VirtualDrops</c>/<c>DroppedFiles</c> batch
    /// directories for the lifetime of an active import so the 30-minute stale
    /// reaper never deletes staging files out from under a long-running copy.
    /// </summary>
    internal static IDisposable AcquireActiveStagingBatchLease(
        IEnumerable<string>? paths,
        string? tempRootOverride = null)
    {
        List<string> batchDirs = ResolveOwnedStagingBatchDirectories(paths, tempRootOverride);
        if (batchDirs.Count == 0)
        {
            return EmptyDisposable.Instance;
        }

        lock (s_activeStagingLeaseGate)
        {
            foreach (string dir in batchDirs)
            {
                s_activeStagingBatchLeases[dir] =
                    s_activeStagingBatchLeases.TryGetValue(dir, out int count) ? count + 1 : 1;
            }
        }

        return new StagingBatchLease(batchDirs);
    }

    private static bool IsStagingBatchLeased(string batchDirectory)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(batchDirectory));
        lock (s_activeStagingLeaseGate)
        {
            return s_activeStagingBatchLeases.TryGetValue(normalized, out int count) && count > 0;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class StagingBatchLease(List<string> batchDirs) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (s_activeStagingLeaseGate)
            {
                foreach (string dir in batchDirs)
                {
                    if (!s_activeStagingBatchLeases.TryGetValue(dir, out int count))
                    {
                        continue;
                    }

                    if (count <= 1)
                    {
                        s_activeStagingBatchLeases.Remove(dir);
                    }
                    else
                    {
                        s_activeStagingBatchLeases[dir] = count - 1;
                    }
                }
            }
        }
    }

    private static List<string> ResolveOwnedStagingBatchDirectories(
        IEnumerable<string>? paths,
        string? tempRootOverride = null)
    {
        var results = new List<string>();
        if (paths is null)
        {
            return results;
        }

        string[] stagingRoots = string.IsNullOrWhiteSpace(tempRootOverride)
            ? EnumerateKnownStagingRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [Path.GetFullPath(tempRootOverride)];
        string[] stagingBucketRootsWithSep = stagingRoots
            .SelectMany(static root => new[]
            {
                Path.GetFullPath(Path.Combine(root, "VirtualDrops")),
                Path.GetFullPath(Path.Combine(root, "DroppedFiles")),
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(EnsureTrailingSeparator)
            .ToArray();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || !TryNormalizePath(rawPath, out string fullPath))
            {
                continue;
            }

            foreach (string bucketWithSep in stagingBucketRootsWithSep)
            {
                if (!fullPath.StartsWith(bucketWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = fullPath[bucketWithSep.Length..];
                int sepIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                if (sepIndex <= 0)
                {
                    continue;
                }

                string batchDir = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.Combine(bucketWithSep, relative[..sepIndex])));
                if (seen.Add(batchDir))
                {
                    results.Add(batchDir);
                }

                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Marks the DeskBox-owned <c>VirtualDrops</c>/<c>DroppedFiles</c> batch
    /// directories for <paramref name="paths"/> as opened by an external application
    /// (D7). Launched batches are retained for 24 hours during runtime (refreshing
    /// whenever the user saves edits) and swept on the next DeskBox startup instead
    /// of being deleted by a hardcoded 20-second timer.
    /// </summary>
    internal static void MarkStagingBatchLaunchedRetention(
        IEnumerable<string>? paths,
        string? tempRootOverride = null)
    {
        foreach (string batchDir in ResolveOwnedStagingBatchDirectories(paths, tempRootOverride))
        {
            try
            {
                if (!Directory.Exists(batchDir))
                {
                    continue;
                }

                var dirInfo = new DirectoryInfo(batchDir);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string markerPath = Path.Combine(batchDir, LaunchedRetentionMarkerFileName);
                File.WriteAllText(markerPath, string.Empty);
            }
            catch
            {
            }
        }
    }

    internal static int CleanupStaleDropStagingDirectories(
        string? tempRootOverride = null,
        TimeSpan? maxAge = null,
        DateTimeOffset? nowUtc = null,
        bool isStartupSweep = false)
    {
        string[] stagingRoots;
        try
        {
            stagingRoots = string.IsNullOrWhiteSpace(tempRootOverride)
                ? EnumerateKnownStagingRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [Path.GetFullPath(tempRootOverride)];
        }
        catch
        {
            return 0;
        }

        DateTime referenceUtc = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
        TimeSpan effectiveMaxAge = maxAge ?? TimeSpan.FromMinutes(30);
        DateTime cutoffUtc = referenceUtc - effectiveMaxAge;
        DateTime launchedCutoffUtc = referenceUtc - LaunchedVirtualRetentionDuration;
        int removedCount = 0;

        foreach (string baseTemp in stagingRoots)
        {
            if (!Directory.Exists(baseTemp))
            {
                continue;
            }

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
                            if ((batchDir.Attributes & FileAttributes.ReparsePoint) != 0 ||
                                IsStagingBatchLeased(batchDir.FullName))
                            {
                                continue;
                            }

                            bool hasLaunchedMarker = File.Exists(
                                Path.Combine(batchDir.FullName, LaunchedRetentionMarkerFileName));
                            if (hasLaunchedMarker)
                            {
                                if (!isStartupSweep &&
                                    ResolveNewestBatchTimestampUtc(batchDir) > launchedCutoffUtc)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                DateTime newestTimestampUtc = batchDir.LastWriteTimeUtc > batchDir.CreationTimeUtc
                                    ? batchDir.LastWriteTimeUtc
                                    : batchDir.CreationTimeUtc;
                                if (newestTimestampUtc > cutoffUtc)
                                {
                                    continue;
                                }
                            }

                            DeleteStagingTreeNonReparse(batchDir);
                            removedCount++;
                        }
                        catch
                        {
                            // Ignore active or locked drop staging batches.
                        }
                    }

                    foreach (FileInfo stagedFile in bucketInfo.EnumerateFiles())
                    {
                        try
                        {
                            if ((stagedFile.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            DateTime newestTimestampUtc = stagedFile.LastWriteTimeUtc > stagedFile.CreationTimeUtc
                                ? stagedFile.LastWriteTimeUtc
                                : stagedFile.CreationTimeUtc;
                            if (newestTimestampUtc > cutoffUtc)
                            {
                                continue;
                            }

                            string extendedFile = StorageMigrationNativeMethods.Extended(stagedFile.FullName);
                            FileAttributes attrs = File.GetAttributes(extendedFile);
                            if ((attrs & FileAttributes.ReadOnly) != 0)
                            {
                                File.SetAttributes(extendedFile, attrs & ~FileAttributes.ReadOnly);
                            }

                            File.Delete(extendedFile);
                            removedCount++;
                        }
                        catch
                        {
                            // Ignore active or locked export files.
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible temp buckets.
                }
            }
        }

        return removedCount;
    }

    private static DateTime ResolveNewestBatchTimestampUtc(DirectoryInfo batchDir)
    {
        DateTime newest = batchDir.LastWriteTimeUtc > batchDir.CreationTimeUtc
            ? batchDir.LastWriteTimeUtc
            : batchDir.CreationTimeUtc;
        try
        {
            int inspected = 0;
            foreach (FileInfo file in batchDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (++inspected > 128)
                {
                    break;
                }

                DateTime candidate = file.LastWriteTimeUtc > file.CreationTimeUtc
                    ? file.LastWriteTimeUtc
                    : file.CreationTimeUtc;
                if (candidate > newest)
                {
                    newest = candidate;
                }
            }
        }
        catch
        {
        }

        return newest;
    }

    internal static void DeleteStagingTreeNonReparse(DirectoryInfo directory, bool isBatchRoot = true)
    {
        if (!directory.Exists)
        {
            return;
        }

        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (isBatchRoot)
            {
                return;
            }

            try
            {
                string extendedReparseDir = StorageMigrationNativeMethods.Extended(directory.FullName);
                FileAttributes dirAttrs = File.GetAttributes(extendedReparseDir);
                if ((dirAttrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(extendedReparseDir, dirAttrs & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(extendedReparseDir, recursive: false);
            }
            catch
            {
            }

            return;
        }

        try
        {
            foreach (DirectoryInfo childDir in directory.EnumerateDirectories())
            {
                try
                {
                    DeleteStagingTreeNonReparse(childDir, isBatchRoot: false);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        try
        {
            foreach (FileInfo file in directory.EnumerateFiles())
            {
                try
                {
                    string extendedFile = StorageMigrationNativeMethods.Extended(file.FullName);
                    FileAttributes attrs = File.GetAttributes(extendedFile);
                    if ((attrs & FileAttributes.ReparsePoint) == 0 &&
                        (attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(extendedFile, attrs & ~FileAttributes.ReadOnly);
                    }

                    File.Delete(extendedFile);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        try
        {
            string extendedDir = StorageMigrationNativeMethods.Extended(directory.FullName);
            FileAttributes attrs = File.GetAttributes(extendedDir);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(extendedDir, attrs & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(extendedDir, recursive: false);
        }
        catch
        {
        }
    }

    internal static bool IsUnderVirtualDropsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !TryNormalizePath(path, out string fullPath))
        {
            return false;
        }

        foreach (string root in EnumerateKnownStagingRoots())
        {
            string virtualDropsRootWithSep = EnsureTrailingSeparator(
                Path.GetFullPath(Path.Combine(root, "VirtualDrops")));
            if (fullPath.StartsWith(virtualDropsRootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes DeskBox-owned <c>VirtualDrops</c> and <c>DroppedFiles</c> batch
    /// directories referenced by <paramref name="paths"/> immediately after
    /// import, shortcut creation, or refusal completes, avoiding reparse points
    /// and paths outside those staging buckets.
    /// </summary>
    internal static int CleanupConsumedStagedPaths(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return 0;
        }

        string[] stagingBucketRoots = EnumerateKnownStagingRoots()
            .SelectMany(static root => new[]
            {
                Path.GetFullPath(Path.Combine(root, "VirtualDrops")),
                Path.GetFullPath(Path.Combine(root, "DroppedFiles")),
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] stagingBucketRootsWithSep = stagingBucketRoots
            .Select(EnsureTrailingSeparator)
            .ToArray();
        var batchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawPath in paths)
        {
            if (!TryNormalizePath(rawPath, out string fullPath))
            {
                continue;
            }

            for (int i = 0; i < stagingBucketRootsWithSep.Length; i++)
            {
                string rootWithSep = stagingBucketRootsWithSep[i];
                if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = fullPath[rootWithSep.Length..];
                int sepIndex = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                string batchSegment = sepIndex >= 0 ? relative[..sepIndex] : relative;
                if (!string.IsNullOrWhiteSpace(batchSegment))
                {
                    batchDirs.Add(Path.Combine(stagingBucketRoots[i], batchSegment));
                }

                break;
            }
        }

        int removedCount = 0;
        foreach (string batchDir in batchDirs)
        {
            try
            {
                if (!Directory.Exists(batchDir))
                {
                    continue;
                }

                var dirInfo = new DirectoryInfo(batchDir);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                DeleteStagingTreeNonReparse(dirInfo);
                removedCount++;
            }
            catch
            {
            }
        }

        return removedCount;
    }

    /// <summary>
    /// Copies any temporary/staged <see cref="DroppedFilePath"/> entries into
    /// <paramref name="durableRootDirectory"/> before the caller's drop scope
    /// deletes <c>VirtualDrops</c>, returning durable paths for all items.
    /// </summary>
    internal static IReadOnlyList<string> PersistTemporaryDroppedFiles(
        IReadOnlyList<DroppedFilePath> files,
        string durableRootDirectory,
        long maxTotalBytes = 0)
    {
        if (files.Count == 0)
        {
            return [];
        }

        long effectiveByteLimit = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            durableRootDirectory,
            explicitLimitBytes: maxTotalBytes > 0 ? maxTotalBytes : null);
        var resolved = new List<string>(files.Count);
        string? batchDir = null;
        long stagedBytes = 0;
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

            if (StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(fullPath))
            {
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
                if (!item.ForceManagedCopy &&
                    IsUnderVirtualDropsDirectory(fullPath) &&
                    TryMoveSameVolumeTempEntry(fullPath, destPath, isDirectory: false, out long movedBytes))
                {
                    ClearReadOnlyAttributeIfPresent(destPath);
                    stagedBytes += movedBytes;
                    resolved.Add(destPath);
                }
                else
                {
                    StageSingleFile(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                    resolved.Add(destPath);
                }
            }
            else if (Directory.Exists(fullPath))
            {
                if (!item.ForceManagedCopy &&
                    IsUnderVirtualDropsDirectory(fullPath) &&
                    TryMoveSameVolumeTempEntry(fullPath, destPath, isDirectory: true, out _))
                {
                    resolved.Add(destPath);
                }
                else
                {
                    StageDirectoryTree(fullPath, destPath, ref stagedBytes, effectiveByteLimit);
                    resolved.Add(destPath);
                }
            }
        }

        return resolved;
    }

    internal static void TryDeleteStagingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool TryMoveSameVolumeTempEntry(
        string sourcePath,
        string destPath,
        bool isDirectory,
        out long movedBytes)
    {
        movedBytes = 0;
        try
        {
            if (!string.Equals(
                    Path.GetPathRoot(sourcePath),
                    Path.GetPathRoot(destPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (isDirectory)
            {
                Directory.Move(sourcePath, destPath);
            }
            else
            {
                movedBytes = Math.Max(0L, new FileInfo(sourcePath).Length);
                File.Move(sourcePath, destPath, overwrite: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
