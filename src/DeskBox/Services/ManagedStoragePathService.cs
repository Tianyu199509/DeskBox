using System.IO;

namespace DeskBox.Services;

internal readonly record struct ManagedStorageDriveCandidate(
    string RootPath,
    DriveType DriveType,
    bool IsReady,
    long AvailableFreeSpace);

internal readonly record struct ManagedStoragePathAssessment(
    bool IsSystemDrive,
    bool IsCloudSynced,
    DriveType? DriveType,
    long? AvailableFreeSpace,
    bool HasSuitableNonSystemDrive);

internal static class ManagedStoragePathService
{
    internal const long MinimumRecommendedFreeSpaceBytes = 10L * 1024 * 1024 * 1024;

    public static string GetRecommendedPath()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string userName = Environment.UserName;
        string systemDriveRoot = GetSystemDriveRoot(userProfilePath);

        return SelectRecommendedPath(
            userProfilePath,
            userName,
            systemDriveRoot,
            EnumerateDriveCandidates());
    }

    internal static string SelectRecommendedPath(
        string userProfilePath,
        string userName,
        string systemDriveRoot,
        IEnumerable<ManagedStorageDriveCandidate> drives)
    {
        string fallbackPath = Path.Combine(userProfilePath, "DeskBox");
        ManagedStorageDriveCandidate? selected = drives
            .Where(drive =>
                drive.IsReady &&
                drive.DriveType == DriveType.Fixed &&
                drive.AvailableFreeSpace >= MinimumRecommendedFreeSpaceBytes &&
                !SameDriveRoot(drive.RootPath, systemDriveRoot))
            .OrderByDescending(drive => drive.AvailableFreeSpace)
            .ThenBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(drive => (ManagedStorageDriveCandidate?)drive)
            .FirstOrDefault();

        if (selected is null)
        {
            return fallbackPath;
        }

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

        return Path.Combine(selected.Value.RootPath, "DeskBox", accountFolder);
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
        bool isSystemDrive = pathRoot is not null && SameDriveRoot(pathRoot, systemDriveRoot);
        bool isCloudSynced = GetCloudSyncRoots().Any(root => IsSameOrDescendant(normalizedPath, root));

        DriveType? driveType = null;
        long? availableFreeSpace = null;
        if (normalizedPath.StartsWith(@"\\", StringComparison.Ordinal))
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
        }

        bool hasSuitableNonSystemDrive = EnumerateDriveCandidates().Any(drive =>
            drive.IsReady &&
            drive.DriveType == DriveType.Fixed &&
            drive.AvailableFreeSpace >= MinimumRecommendedFreeSpaceBytes &&
            !SameDriveRoot(drive.RootPath, systemDriveRoot));

        return new ManagedStoragePathAssessment(
            isSystemDrive,
            isCloudSynced,
            driveType,
            availableFreeSpace,
            hasSuitableNonSystemDrive);
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

            yield return new ManagedStorageDriveCandidate(
                drive.Name,
                drive.DriveType,
                isReady,
                availableFreeSpace);
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
