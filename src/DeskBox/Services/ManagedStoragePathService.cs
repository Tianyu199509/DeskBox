using System.IO;
using DeskBox.Helpers;

namespace DeskBox.Services;

internal readonly record struct ManagedStorageDriveCandidate(
    string RootPath,
    DriveType DriveType,
    bool IsReady,
    long AvailableFreeSpace,
    bool IsTransientBus);

internal readonly record struct ManagedStoragePathAssessment(
    bool IsSystemDrive,
    bool IsCloudSynced,
    DriveType? DriveType,
    long? AvailableFreeSpace,
    bool HasSuitableNonSystemDrive,
    string? SuitableNonSystemDrivePath,
    bool IsTransientBusDrive);

internal static class ManagedStoragePathService
{
    internal const long MinimumRecommendedFreeSpaceBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// The default storage root for new profiles. It must never depend on the
    /// hardware that happens to be attached at first launch: drives reporting
    /// as fixed (portable SSDs, UASP enclosures) can vanish later and take the
    /// managed storage with them. Suitable non-system drives are only offered
    /// as an explicit suggestion during onboarding.
    /// </summary>
    public static string GetRecommendedPath()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfilePath, "DeskBox");
    }

    /// <summary>
    /// Picks the best internal non-system drive for an opt-in migration
    /// suggestion, or null when no such drive exists. Detachable buses that
    /// report as fixed disks are excluded.
    /// </summary>
    internal static ManagedStorageDriveCandidate? SelectSuitableNonSystemDrive(
        string systemDriveRoot,
        IEnumerable<ManagedStorageDriveCandidate> drives)
    {
        return drives
            .Where(drive => IsSuitableNonSystemCandidate(drive, systemDriveRoot))
            .OrderByDescending(drive => drive.AvailableFreeSpace)
            .ThenBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(drive => (ManagedStorageDriveCandidate?)drive)
            .FirstOrDefault();
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

        ManagedStorageDriveCandidate? suitableNonSystemDrive =
            SelectSuitableNonSystemDrive(systemDriveRoot, EnumerateDriveCandidates());
        string? suitableNonSystemDrivePath = suitableNonSystemDrive is null
            ? null
            : Path.Combine(
                suitableNonSystemDrive.Value.RootPath,
                "DeskBox",
                BuildAccountSubfolder(userProfilePath, Environment.UserName));

        return new ManagedStoragePathAssessment(
            isSystemDrive,
            isCloudSynced,
            driveType,
            availableFreeSpace,
            suitableNonSystemDrive is not null,
            suitableNonSystemDrivePath,
            isTransientBusDrive);
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
