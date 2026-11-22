using DeskBox.Platform;

namespace DeskBox.Services.DragDrop;

/// <summary>
/// Thrown when a packaged Microsoft Store build attempts to write, copy, move,
/// or create shortcuts inside Windows MSIX-virtualized <c>%LocalAppData%</c> or
/// <c>%AppData%</c> directories, where writes would silently redirect into
/// package-private <c>LocalCache</c> and be destroyed on app uninstall.
/// </summary>
public sealed class StoreVirtualizedDestinationException : UnauthorizedAccessException
{
    public string DestinationPath { get; }

    public StoreVirtualizedDestinationException(string destinationPath)
        : base($"Cannot write into MSIX-virtualized AppData directory '{destinationPath}' in the Microsoft Store build.")
    {
        DestinationPath = destinationPath;
    }
}

/// <summary>
/// Central policy governing Microsoft Store MSIX AppData virtualization boundaries
/// and outbound Shell source path resolution:
/// 1. Store builds may browse mapped <c>%LocalAppData%</c>/<c>%AppData%</c> folders
///    read-only, but all drop/write/move/shortcut destinations under them are blocked.
/// 2. Outbound drags originating from Store-virtualized AppData paths are forced
///    Copy-only so the source is never relocated out of the virtualized namespace.
/// 3. Outbound Shell <c>IDataObject</c> path resolution never dereferences leaf
///    NTFS symlinks, directory junctions, or mount points (FL-01), and only
///    resolves unvirtualized physical paths for genuine Store MSIX AppData files.
/// </summary>
internal static class StoreVirtualizedPathPolicy
{
    private readonly record struct VirtualizedAppDataRoots(
        string? DeskBoxRoot,
        string? LocalAppData,
        string? RoamingAppData,
        string? ProfileLocalAppData,
        string? ProfileRoamingAppData);

    private static readonly Lazy<VirtualizedAppDataRoots> s_defaultRoots = new(static () =>
    {
        string? deskBoxRoot = NormalizeOptionalRoot(TryGetDeskBoxDataRoot());
        string? localAppData = NormalizeOptionalRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string? roamingAppData = NormalizeOptionalRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        string? userProfile = NormalizeOptionalRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string? profileLocalAppData = !string.IsNullOrWhiteSpace(userProfile)
            ? NormalizeOptionalRoot(Path.Combine(userProfile, "AppData", "Local"))
            : null;
        string? profileRoamingAppData = !string.IsNullOrWhiteSpace(userProfile)
            ? NormalizeOptionalRoot(Path.Combine(userProfile, "AppData", "Roaming"))
            : null;
        return new VirtualizedAppDataRoots(
            deskBoxRoot,
            localAppData,
            roamingAppData,
            profileLocalAppData,
            profileRoamingAppData);
    });

    internal static bool IsPackagedStoreVirtualizationActive(bool? isPackagedOverride = null) =>
        isPackagedOverride ??
        (AppDistributionService.Current.IsPackaged || AppDistributionService.Current.IsMicrosoftStore);

    internal static bool IsWriteBlockedDestination(
        string? destinationDirectory,
        bool? isPackagedOverride = null,
        string? localAppDataOverride = null,
        string? roamingAppDataOverride = null,
        string? userProfileOverride = null,
        string? deskBoxRootOverride = null)
    {
        if (!IsPackagedStoreVirtualizationActive(isPackagedOverride))
        {
            return false;
        }

        return IsVirtualizedAppDataPath(
            destinationDirectory,
            localAppDataOverride,
            roamingAppDataOverride,
            userProfileOverride,
            deskBoxRootOverride);
    }

    internal static void ThrowIfWriteBlockedDestination(
        string? destinationDirectory,
        bool? isPackagedOverride = null)
    {
        if (IsWriteBlockedDestination(destinationDirectory, isPackagedOverride))
        {
            throw new StoreVirtualizedDestinationException(destinationDirectory ?? string.Empty);
        }
    }

    internal static bool IsCopyOnlyVirtualizedSource(
        IEnumerable<string>? sourcePaths,
        bool? isPackagedOverride = null,
        string? localAppDataOverride = null,
        string? roamingAppDataOverride = null,
        string? userProfileOverride = null,
        string? deskBoxRootOverride = null)
    {
        if (!IsPackagedStoreVirtualizationActive(isPackagedOverride) || sourcePaths is null)
        {
            return false;
        }

        foreach (string rawPath in sourcePaths)
        {
            if (IsVirtualizedAppDataPath(
                    rawPath,
                    localAppDataOverride,
                    roamingAppDataOverride,
                    userProfileOverride,
                    deskBoxRootOverride))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsVirtualizedAppDataPath(
        string? path,
        string? localAppDataOverride = null,
        string? roamingAppDataOverride = null,
        string? userProfileOverride = null,
        string? deskBoxRootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return false;
        }

        VirtualizedAppDataRoots roots;
        if (localAppDataOverride is null &&
            roamingAppDataOverride is null &&
            userProfileOverride is null &&
            deskBoxRootOverride is null)
        {
            roots = s_defaultRoots.Value;
        }
        else
        {
            string? userProfile = NormalizeOptionalRoot(
                userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            roots = new VirtualizedAppDataRoots(
                NormalizeOptionalRoot(deskBoxRootOverride ?? TryGetDeskBoxDataRoot()),
                NormalizeOptionalRoot(
                    localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                NormalizeOptionalRoot(
                    roamingAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                !string.IsNullOrWhiteSpace(userProfile)
                    ? NormalizeOptionalRoot(Path.Combine(userProfile, "AppData", "Local"))
                    : null,
                !string.IsNullOrWhiteSpace(userProfile)
                    ? NormalizeOptionalRoot(Path.Combine(userProfile, "AppData", "Roaming"))
                    : null);
        }

        if (MatchesVirtualizedAppDataCandidate(
                fullPath,
                roots.DeskBoxRoot,
                roots.LocalAppData,
                roots.RoamingAppData,
                roots.ProfileLocalAppData,
                roots.ProfileRoamingAppData))
        {
            return true;
        }

        // Also inspect the resolved physical/reparse target when the path exists
        // so a junction or symlink outside AppData pointing INTO %AppData% /
        // %LocalAppData% cannot bypass the MSIX read-only boundary.
        string? resolvedPath = TryResolveRealTargetPath(fullPath);
        if (!string.IsNullOrWhiteSpace(resolvedPath) &&
            !string.Equals(resolvedPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            MatchesVirtualizedAppDataCandidate(
                resolvedPath,
                roots.DeskBoxRoot,
                roots.LocalAppData,
                roots.RoamingAppData,
                roots.ProfileLocalAppData,
                roots.ProfileRoamingAppData))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a source path for an outbound Shell <c>IDataObject</c> without
    /// dereferencing leaf NTFS symlinks, directory junctions, or mount points.
    /// Only genuine MSIX-virtualized AppData files in a packaged build are
    /// resolved to their physical <c>LocalCache</c> path so out-of-process
    /// receivers (Explorer) can read them.
    /// </summary>
    internal static string ResolveOutboundShellSourcePath(
        string path,
        bool? isPackagedOverride = null,
        Func<string, bool>? isLinkOrMountPointProbe = null,
        Func<string, string>? unvirtualizeProbe = null,
        string? localAppDataOverride = null,
        string? roamingAppDataOverride = null,
        string? userProfileOverride = null,
        string? deskBoxRootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }

        bool isLinkOrMount = false;
        try
        {
            isLinkOrMount = (isLinkOrMountPointProbe ??
                StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint)(fullPath);
        }
        catch
        {
        }

        if (isLinkOrMount)
        {
            return fullPath;
        }

        if (IsPackagedStoreVirtualizationActive(isPackagedOverride) &&
            IsVirtualizedAppDataPath(
                fullPath,
                localAppDataOverride,
                roamingAppDataOverride,
                userProfileOverride,
                deskBoxRootOverride))
        {
            return (unvirtualizeProbe ?? Win32Helper.ResolveUnvirtualizedPhysicalPath)(fullPath);
        }

        return fullPath;
    }

    private static bool MatchesVirtualizedAppDataCandidate(
        string candidatePath,
        string? deskBoxRoot,
        string? localAppData,
        string? roamingAppData,
        string? profileLocalAppData,
        string? profileRoamingAppData)
    {
        if (!string.IsNullOrWhiteSpace(deskBoxRoot) &&
            IsSameOrDescendant(candidatePath, deskBoxRoot))
        {
            return false;
        }

        return IsSameOrDescendant(candidatePath, localAppData) ||
               IsSameOrDescendant(candidatePath, roamingAppData) ||
               IsSameOrDescendant(candidatePath, profileLocalAppData) ||
               IsSameOrDescendant(candidatePath, profileRoamingAppData);
    }

    private static string? TryResolveRealTargetPath(string fullPath)
    {
        try
        {
            string substResolved = PathOriginClassifier.ResolveSubstPath(fullPath);
            string candidate = string.IsNullOrWhiteSpace(substResolved) ? fullPath : substResolved;
            string? resolvedViaLink = TryResolveViaAncestorReparsePoints(candidate);
            if (!string.IsNullOrWhiteSpace(resolvedViaLink))
            {
                return NormalizeOptionalRoot(resolvedViaLink);
            }

            return NormalizeOptionalRoot(candidate);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolveViaAncestorReparsePoints(string candidate)
    {
        bool hasLinkAncestor = false;
        string? current = candidate;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                StorageMigrationNativeMethods.IsFileSystemLinkOrMountPoint(current))
            {
                hasLinkAncestor = true;
                break;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        if (!hasLinkAncestor)
        {
            return null;
        }

        string unvirtualized = Win32Helper.ResolveUnvirtualizedPhysicalPath(candidate);
        if (!string.IsNullOrWhiteSpace(unvirtualized) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(unvirtualized),
                Path.TrimEndingDirectorySeparator(candidate),
                StringComparison.OrdinalIgnoreCase))
        {
            return unvirtualized;
        }

        // Fallback for broken/unopenable symlinks or junctions where CreateFileW cannot open the target.
        if (!string.IsNullOrWhiteSpace(current))
        {
            FileSystemInfo? fsi = File.Exists(current)
                ? new FileInfo(current)
                : Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : null;
            FileSystemInfo? resolvedLink = fsi?.ResolveLinkTarget(returnFinalTarget: true);
            if (resolvedLink is not null)
            {
                string resolvedBase = Path.GetFullPath(resolvedLink.FullName);
                string relativeFromLink = Path.GetRelativePath(current, candidate);
                if (!string.IsNullOrWhiteSpace(relativeFromLink) && relativeFromLink != ".")
                {
                    return Path.GetFullPath(Path.Combine(resolvedBase, relativeFromLink));
                }

                return resolvedBase;
            }
        }

        return unvirtualized;
    }

    private static string? TryGetDeskBoxDataRoot()
    {
        try
        {
            return DeskBoxDataPathService.Current.RootPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeOptionalRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrDescendant(string candidatePath, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        if (string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
