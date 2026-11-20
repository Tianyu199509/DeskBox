using System.IO;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

internal readonly record struct ManagedStorageDriveCandidate(
    string RootPath,
    DriveType DriveType,
    bool IsReady,
    long AvailableFreeSpace,
    bool IsTransientBus);

internal readonly record struct ManagedStorageSelectableDriveOption(
    string DriveLabel,
    string DriveRootPath,
    string TargetRootPath,
    long AvailableFreeSpace,
    bool IsSystemDrive);

internal readonly record struct ManagedStoragePathAssessment(
    bool IsSystemDrive,
    bool IsCloudSynced,
    DriveType? DriveType,
    long? AvailableFreeSpace,
    bool HasSuitableNonSystemDrive,
    string? SuitableNonSystemDrivePath,
    bool IsTransientBusDrive);

internal enum ManagedStorageRootValidationProblem
{
    None,
    InvalidPath,
    DriveOrShareRoot,
    DesktopOverlap,
    SystemOrProfileRoot,
    StoreVirtualizedAppData,
}

internal readonly record struct ManagedStorageRootValidationResult(
    ManagedStorageRootValidationProblem Problem,
    string NormalizedPath)
{
    internal bool IsValid => Problem == ManagedStorageRootValidationProblem.None;
}

internal static class ManagedStoragePathService
{
    internal const string PreferredNonSystemDriveRoot = @"D:\";
    internal const long MinimumRecommendedFreeSpaceBytes = 10L * 1024 * 1024 * 1024;
    internal const long MinimumInteractiveDriveFreeSpaceBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Resolves the default storage root for new profiles. Prefers <c>D:\DeskBox</c>
    /// when <c>D:\</c> is an internal fixed non-system drive with at least 10 GB
    /// free, otherwise the largest suitable internal non-system drive, falling back
    /// to <c>%USERPROFILE%\DeskBox</c> when only the system drive (or detachable
    /// media) is available.
    /// </summary>
    public static string GetRecommendedPath()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string systemDriveRoot = GetSystemDriveRoot(userProfilePath);
        bool isStoreChannel = AppDistributionService.Current.IsMicrosoftStore;
        return SelectRecommendedPath(
            userProfilePath,
            systemDriveRoot,
            EnumerateDriveCandidates(),
            candidatePath => ValidateStorageRootCandidate(candidatePath, isStoreChannel).IsValid);
    }

    internal static string BuildDriveStorageRootPath(string driveRootPath)
    {
        return Path.Combine(driveRootPath, "DeskBox");
    }

    internal static string SelectRecommendedPath(
        string userProfilePath,
        string systemDriveRoot,
        IEnumerable<ManagedStorageDriveCandidate> drives,
        Func<string, bool>? isCandidatePathValid = null)
    {
        string fallbackPath = Path.Combine(userProfilePath, "DeskBox");
        IEnumerable<ManagedStorageDriveCandidate> candidateDrives = isCandidatePathValid is null
            ? drives
            : drives.Where(drive => isCandidatePathValid(BuildDriveStorageRootPath(drive.RootPath)));

        ManagedStorageDriveCandidate? selected =
            SelectSuitableNonSystemDrive(systemDriveRoot, candidateDrives);

        return selected is null
            ? fallbackPath
            : BuildDriveStorageRootPath(selected.Value.RootPath);
    }

    /// <summary>
    /// Picks the best internal non-system drive: always prefers <c>D:\</c> when
    /// it qualifies as a suitable internal fixed drive, otherwise the largest
    /// qualifying internal non-system drive. Detachable buses that report as
    /// fixed disks are excluded.
    /// </summary>
    internal static ManagedStorageDriveCandidate? SelectSuitableNonSystemDrive(
        string systemDriveRoot,
        IEnumerable<ManagedStorageDriveCandidate> drives)
    {
        return drives
            .Where(drive => IsSuitableNonSystemCandidate(drive, systemDriveRoot))
            .OrderByDescending(drive => SameDriveRoot(drive.RootPath, PreferredNonSystemDriveRoot))
            .ThenByDescending(drive => drive.AvailableFreeSpace)
            .ThenBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(drive => (ManagedStorageDriveCandidate?)drive)
            .FirstOrDefault();
    }

    internal static IReadOnlyList<ManagedStorageSelectableDriveOption> GetSelectableStorageDriveOptions()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string systemDriveRoot = GetSystemDriveRoot(userProfilePath);
        bool isStoreChannel = AppDistributionService.Current.IsMicrosoftStore;
        return BuildSelectableStorageDriveOptions(
            userProfilePath,
            systemDriveRoot,
            EnumerateDriveCandidates(),
            candidatePath => ValidateStorageRootCandidate(candidatePath, isStoreChannel).IsValid);
    }

    internal static IReadOnlyList<ManagedStorageSelectableDriveOption> BuildSelectableStorageDriveOptions(
        string userProfilePath,
        string systemDriveRoot,
        IEnumerable<ManagedStorageDriveCandidate> drives,
        Func<string, bool>? isCandidatePathValid = null)
    {
        var options = new List<ManagedStorageSelectableDriveOption>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ManagedStorageDriveCandidate drive in drives
                     .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                     .OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            string? normalizedRoot = TryGetPathRoot(drive.RootPath);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !seenRoots.Add(normalizedRoot))
            {
                continue;
            }

            bool isSystem = SameDriveRoot(normalizedRoot, systemDriveRoot);
            if (!isSystem &&
                (drive.IsTransientBus || drive.AvailableFreeSpace < MinimumInteractiveDriveFreeSpaceBytes))
            {
                continue;
            }

            string targetPath = isSystem
                ? Path.Combine(userProfilePath, "DeskBox")
                : BuildDriveStorageRootPath(normalizedRoot);
            if (isCandidatePathValid is not null && !isCandidatePathValid(targetPath))
            {
                continue;
            }

            string label = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            options.Add(new ManagedStorageSelectableDriveOption(
                label,
                normalizedRoot,
                targetPath,
                drive.AvailableFreeSpace,
                isSystem));
        }

        return options;
    }

    internal static bool IsSuitableNonSystemCandidate(
        ManagedStorageDriveCandidate drive,
        string systemDriveRoot)
    {
        return drive.IsReady &&
               drive.DriveType == DriveType.Fixed &&
               !drive.IsTransientBus &&
               drive.AvailableFreeSpace >= MinimumRecommendedFreeSpaceBytes &&
               !SameDriveRoot(drive.RootPath, systemDriveRoot);
    }

    internal static string BuildAccountSubfolder(string userProfilePath, string userName)
    {
        string accountFolder = SanitizePathSegment(userName);
        if (string.IsNullOrWhiteSpace(accountFolder))
        {
            accountFolder = SanitizePathSegment(Path.GetFileName(
                Path.TrimEndingDirectorySeparator(userProfilePath)));
        }
        if (string.IsNullOrWhiteSpace(accountFolder))
        {
            accountFolder = "User";
        }

        return accountFolder;
    }

    /// <summary>
    /// True when the candidate storage root overlaps the user desktop or common
    /// (public) desktop in either direction (inside Desktop, or an ancestor that
    /// contains Desktop such as <c>%USERPROFILE%</c> or <c>%OneDrive%</c>).
    /// </summary>
    public static bool OverlapsDesktop(string path)
    {
        return OverlapsDesktop(
            path,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
    }

    internal static bool OverlapsDesktop(
        string path,
        string userDesktopPath,
        string commonDesktopPath)
    {
        return PathsOverlapEitherDirection(path, userDesktopPath) ||
            PathsOverlapEitherDirection(path, commonDesktopPath);
    }

    /// <summary>
    /// Normalizes a folder chosen in the interactive folder picker so drive roots
    /// (<c>D:\</c>) and non-empty pre-existing user folders (<c>D:\Work</c>) are
    /// scoped to a dedicated <c>DeskBox</c> child folder instead of mixing widget
    /// directories with unrelated user directories.
    /// </summary>
    internal static string ResolvePickedStorageRootPath(
        string pickedPath,
        string temporaryMigrationFolderName = ".deskbox-migration-temp")
    {
        if (string.IsNullOrWhiteSpace(pickedPath))
        {
            return string.Empty;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(pickedPath);
        }
        catch
        {
            return pickedPath.Trim();
        }

        if (IsDriveOrShareRoot(normalized))
        {
            return Path.Combine(normalized, "DeskBox");
        }

        string trimmed = Path.TrimEndingDirectorySeparator(normalized);
        if (!ManagedStorageOwnershipFileOps.IsDedicatedDeskBoxRootPath(trimmed) &&
            !ManagedStorageOwnershipFileOps.HasManifestFile(trimmed) &&
            ManagedStorageOwnershipFileOps.HasExistingUserEntries(trimmed, temporaryMigrationFolderName))
        {
            return Path.Combine(trimmed, "DeskBox");
        }

        return trimmed;
    }

    internal static ManagedStorageRootValidationResult ValidateStorageRootCandidate(
        string? path,
        bool isStoreChannel = false)
    {
        return ValidateStorageRootCandidate(
            path,
            isStoreChannel,
            userDesktopPath: Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            commonDesktopPath: Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            userProfilePath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            localAppDataPath: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            roamingAppDataPath: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    internal static ManagedStorageRootValidationResult ValidateStorageRootCandidate(
        string? path,
        bool isStoreChannel,
        string userDesktopPath,
        string commonDesktopPath,
        string userProfilePath,
        string localAppDataPath,
        string roamingAppDataPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.InvalidPath,
                string.Empty);
        }

        string normalized;
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return new ManagedStorageRootValidationResult(
                    ManagedStorageRootValidationProblem.InvalidPath,
                    path);
            }

            normalized = Path.GetFullPath(path);
        }
        catch
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.InvalidPath,
                path);
        }

        if (IsDriveOrShareRoot(normalized))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.DriveOrShareRoot,
                normalized);
        }

        if (OverlapsDesktop(normalized, userDesktopPath, commonDesktopPath))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.DesktopOverlap,
                normalized);
        }

        string trimmed = Path.TrimEndingDirectorySeparator(normalized);
        if (IsSameOrAncestorOf(trimmed, userProfilePath))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.SystemOrProfileRoot,
                trimmed);
        }

        ReadOnlySpan<Environment.SpecialFolder> protectedSystemRoots =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
        ];
        foreach (Environment.SpecialFolder special in protectedSystemRoots)
        {
            string systemDir = Environment.GetFolderPath(special);
            if (PathsOverlapEitherDirection(trimmed, systemDir))
            {
                return new ManagedStorageRootValidationResult(
                    ManagedStorageRootValidationProblem.SystemOrProfileRoot,
                    trimmed);
            }
        }

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (IsSameOrAncestorOf(trimmed, programData))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.SystemOrProfileRoot,
                trimmed);
        }

        if (isStoreChannel &&
            (PathsOverlapEitherDirection(trimmed, localAppDataPath) ||
             PathsOverlapEitherDirection(trimmed, roamingAppDataPath) ||
             DragDrop.StoreVirtualizedPathPolicy.IsVirtualizedAppDataPath(
                 trimmed,
                 localAppDataOverride: localAppDataPath,
                 roamingAppDataOverride: roamingAppDataPath,
                 userProfileOverride: userProfilePath)))
        {
            return new ManagedStorageRootValidationResult(
                ManagedStorageRootValidationProblem.StoreVirtualizedAppData,
                trimmed);
        }

        if (!string.IsNullOrWhiteSpace(userProfilePath))
        {
            string storeAppData = Path.Combine(
                userProfilePath,
                "DeskBox",
                DeskBoxDataPathService.StoreDurableSubdirectoryName);
            string storeRecovery = Path.Combine(
                userProfilePath,
                "DeskBox",
                DeskBoxDataPathService.StoreDurableRecoverySubdirectoryName);
            if (IsSameOrAncestorOf(storeAppData, trimmed) ||
                IsSameOrAncestorOf(storeRecovery, trimmed))
            {
                return new ManagedStorageRootValidationResult(
                    ManagedStorageRootValidationProblem.SystemOrProfileRoot,
                    trimmed);
            }
        }

        return new ManagedStorageRootValidationResult(
            ManagedStorageRootValidationProblem.None,
            trimmed);
    }

    internal static bool IsDriveOrShareRoot(string path)
    {
        string? pathRoot = TryGetPathRoot(path);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return true;
        }

        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathRoot));
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool PathsOverlapEitherDirection(string left, string right)
    {
        return IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);
    }

    private static bool IsSameOrAncestorOf(string candidateAncestor, string targetPath)
    {
        return !string.IsNullOrWhiteSpace(targetPath) &&
            IsSameOrDescendant(targetPath, candidateAncestor);
    }

    public static ManagedStoragePathAssessment AssessPath(string path)
    {
        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            normalizedPath = path;
        }

        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string systemDriveRoot = GetSystemDriveRoot(userProfilePath);
        string? pathRoot = TryGetPathRoot(normalizedPath);
        bool isUncPath = normalizedPath.StartsWith(@"\\", StringComparison.Ordinal);
        bool isSystemDrive = pathRoot is not null && SameDriveRoot(pathRoot, systemDriveRoot);
        bool isCloudSynced = GetCloudSyncRoots().Any(root => IsSameOrDescendant(normalizedPath, root));

        DriveType? driveType = null;
        long? availableFreeSpace = null;
        bool isTransientBusDrive = false;
        if (isUncPath)
        {
            driveType = DriveType.Network;
        }
        else if (pathRoot is not null)
        {
            try
            {
                var drive = new DriveInfo(pathRoot);
                driveType = drive.DriveType;
                if (drive.IsReady)
                {
                    availableFreeSpace = drive.AvailableFreeSpace;
                }
            }
            catch
            {
                // The path can still be selected. The onboarding UI will label
                // its storage details as unknown instead of blocking the user.
            }

            // DriveType misses detachable disks that report as fixed; the bus
            // type is what actually tells them apart.
            isTransientBusDrive = StorageBusTypeHelper.IsTransientBus(
                StorageBusTypeHelper.TryGetBusType(pathRoot));
        }

        bool isStoreChannel = AppDistributionService.Current.IsMicrosoftStore;
        ManagedStorageDriveCandidate? suitableNonSystemDrive =
            SelectSuitableNonSystemDrive(
                systemDriveRoot,
                EnumerateDriveCandidates().Where(drive =>
                    ValidateStorageRootCandidate(
                        BuildDriveStorageRootPath(drive.RootPath),
                        isStoreChannel).IsValid));
        string? suitableNonSystemDrivePath = suitableNonSystemDrive is null
            ? null
            : BuildDriveStorageRootPath(suitableNonSystemDrive.Value.RootPath);

        return new ManagedStoragePathAssessment(
            isSystemDrive,
            isCloudSynced,
            driveType,
            availableFreeSpace,
            suitableNonSystemDrive is not null,
            suitableNonSystemDrivePath,
            isTransientBusDrive);
    }

    internal static bool IsDirectChildOfRoot(string? path, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? parent = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detects when <paramref name="currentRootPath"/> has drifted away from
    /// every live managed file widget (e.g. after <c>settings.json</c> was
    /// recovered/reset while <c>widget-layout.json</c> survived intact) and
    /// all live managed widgets unanimously live directly under one valid
    /// parent directory. Returns <c>false</c> whenever any live managed widget
    /// already resides under <paramref name="currentRootPath"/> or when
    /// managed widgets span multiple parent directories.
    /// </summary>
    internal static bool TryInferUnanimousManagedStorageRoot(
        IEnumerable<Models.WidgetConfig> widgets,
        IEnumerable<string>? deletedWidgetIds,
        string? currentRootPath,
        bool isStoreChannel,
        bool requireExistingDirectory,
        out string inferredRoot)
    {
        inferredRoot = string.Empty;
        HashSet<string>? deletedSet = deletedWidgetIds is null
            ? null
            : new HashSet<string>(deletedWidgetIds, StringComparer.Ordinal);
        string normalizedCurrentRoot = string.Empty;
        if (!string.IsNullOrWhiteSpace(currentRootPath))
        {
            try
            {
                normalizedCurrentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRootPath));
            }
            catch
            {
                normalizedCurrentRoot = string.Empty;
            }
        }

        string? commonParent = null;
        int managedWidgetCount = 0;

        foreach (Models.WidgetConfig widget in widgets)
        {
            if (widget is null ||
                widget.WidgetKind != Models.WidgetKind.File ||
                !widget.FollowsDefaultStoragePath ||
                (deletedSet is not null && deletedSet.Contains(widget.Id)) ||
                string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                continue;
            }

            string normalizedMapped;
            string? parent;
            try
            {
                normalizedMapped = Path.TrimEndingDirectorySeparator(Path.GetFullPath(widget.MappedFolderPath));
                parent = Path.GetDirectoryName(normalizedMapped);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            string normalizedParent = Path.TrimEndingDirectorySeparator(parent);
            if (string.Equals(normalizedParent, normalizedMapped, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Never override a root that already owns at least one live managed widget.
            if (!string.IsNullOrEmpty(normalizedCurrentRoot) &&
                string.Equals(normalizedParent, normalizedCurrentRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            managedWidgetCount++;
            if (commonParent is null)
            {
                commonParent = normalizedParent;
            }
            else if (!string.Equals(commonParent, normalizedParent, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (managedWidgetCount == 0 || string.IsNullOrWhiteSpace(commonParent))
        {
            return false;
        }

        if (!ValidateStorageRootCandidate(commonParent, isStoreChannel).IsValid)
        {
            return false;
        }

        if (requireExistingDirectory && !Directory.Exists(commonParent))
        {
            return false;
        }

        inferredRoot = commonParent;
        return true;
    }

    internal static bool IsSameOrDescendant(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string directoryPrefix = normalizedDirectory + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<ManagedStorageDriveCandidate> EnumerateDriveCandidates()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            bool isReady;
            long availableFreeSpace;
            try
            {
                isReady = drive.IsReady;
                availableFreeSpace = isReady ? drive.AvailableFreeSpace : 0;
            }
            catch
            {
                isReady = false;
                availableFreeSpace = 0;
            }

            bool isTransientBus = drive.DriveType != DriveType.Fixed ||
                                  StorageBusTypeHelper.IsTransientBus(
                                      StorageBusTypeHelper.TryGetBusType(drive.Name));

            yield return new ManagedStorageDriveCandidate(
                drive.Name,
                drive.DriveType,
                isReady,
                availableFreeSpace,
                isTransientBus);
        }
    }

    private static IEnumerable<string> GetCloudSyncRoots()
    {
        string[] variableNames = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string variableName in variableNames)
        {
            string? value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(value);
            }
            catch
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static string GetSystemDriveRoot(string userProfilePath)
    {
        string? systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        string? root = TryGetPathRoot(systemDrive);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        root = TryGetPathRoot(Environment.SystemDirectory);
        return !string.IsNullOrWhiteSpace(root)
            ? root
            : TryGetPathRoot(userProfilePath) ?? userProfilePath;
    }

    private static string? TryGetPathRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string candidate = path.Length == 2 && path[1] == ':'
                ? path + Path.DirectorySeparatorChar
                : path;
            return Path.GetPathRoot(Path.GetFullPath(candidate));
        }
        catch
        {
            return null;
        }
    }

    private static bool SameDriveRoot(string left, string right)
    {
        string? leftRoot = TryGetPathRoot(left);
        string? rightRoot = TryGetPathRoot(right);
        return leftRoot is not null &&
               rightRoot is not null &&
               string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value.Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd('.', ' ');
    }
}
