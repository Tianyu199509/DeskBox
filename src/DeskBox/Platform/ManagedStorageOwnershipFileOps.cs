using System.Text;

namespace DeskBox.Platform;

internal readonly record struct ManagedStorageOwnershipSnapshot(
    bool HasManifest,
    IReadOnlySet<string> ManagedFolders,
    IReadOnlySet<string> ForeignFolders);

/// <summary>
/// Persists per-root ownership metadata (<c>.deskbox-managed-folders.manifest</c>)
/// distinguishing directories DeskBox created/managed from pre-existing user
/// directories, and filters hidden/system/reparse/reserved directories out of
/// orphan cleanup enumeration.
/// </summary>
internal static class ManagedStorageOwnershipFileOps
{
    internal const string ManifestFileName = ".deskbox-managed-folders.manifest";
    private const string HeaderLine = "v1";
    private const string ManagedPrefix = "M:";
    private const string ForeignPrefix = "F:";

    private static readonly object Gate = new();

    private static readonly HashSet<string> ReservedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "$WinREAgent",
        "System Volume Information",
        "Config.Msi",
        "Recovery",
        "PerfLogs",
        "MSOCache",
        "Documents and Settings",
    };

    internal static bool HasManifestFile(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(Path.GetFullPath(rootPath), ManifestFileName));
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasExistingUserEntries(string directoryPath, string temporaryMigrationFolderName)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                string name = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(name) ||
                    name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(temporaryMigrationFolderName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    internal static bool IsExcludedOrSystemDirectory(
        string directoryPath,
        string temporaryMigrationFolderName)
    {
        string name = Path.GetFileName(
            directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (name.StartsWith('.') ||
            name.Equals(temporaryMigrationFolderName, StringComparison.OrdinalIgnoreCase) ||
            ReservedDirectoryNames.Contains(name))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(directoryPath))
            {
                FileAttributes attributes = File.GetAttributes(directoryPath);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    internal static ManagedStorageOwnershipSnapshot ReadSnapshot(string rootPath)
    {
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new ManagedStorageOwnershipSnapshot(false, emptySet, emptySet);
        }

        lock (Gate)
        {
            return ReadSnapshotUnsafe(rootPath);
        }
    }

    internal static void RegisterManagedFolders(
        string rootPath,
        IEnumerable<string> managedFolderNames,
        string temporaryMigrationFolderName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return;
        }

        string[] incoming = managedFolderNames
            .Where(IsValidFolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (incoming.Length == 0 && !HasManifestFile(rootPath))
        {
            return;
        }

        lock (Gate)
        {
            ManagedStorageOwnershipSnapshot current = ReadSnapshotUnsafe(rootPath);
            var managed = new HashSet<string>(current.ManagedFolders, StringComparer.OrdinalIgnoreCase);
            var foreign = new HashSet<string>(current.ForeignFolders, StringComparer.OrdinalIgnoreCase);

            bool changed = false;
            if (!current.HasManifest)
            {
                // First time initializing a manifest on an existing root:
                // only record foreign folders if the root is NOT a dedicated
                // "DeskBox" directory (inside a dedicated DeskBox root, untracked
                // folders are legacy kept widget folders).
                if (!IsDedicatedDeskBoxRootPath(rootPath))
                {
                    foreach (string existingName in EnumerateCandidateChildNames(rootPath, temporaryMigrationFolderName))
                    {
                        if (!incoming.Contains(existingName, StringComparer.OrdinalIgnoreCase) &&
                            foreign.Add(existingName))
                        {
                            changed = true;
                        }
                    }
                }
                else
                {
                    foreach (string existingName in EnumerateCandidateChildNames(rootPath, temporaryMigrationFolderName))
                    {
                        if (managed.Add(existingName))
                        {
                            changed = true;
                        }
                    }
                }
            }

            foreach (string name in incoming)
            {
                if (managed.Add(name))
                {
                    changed = true;
                }

                if (foreign.Remove(name))
                {
                    changed = true;
                }
            }

            if (changed || !current.HasManifest)
            {
                WriteSnapshotUnsafe(rootPath, managed, foreign);
            }
        }
    }

    internal static string[] CaptureExistingChildNames(
        string rootPath,
        string temporaryMigrationFolderName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return [];
        }

        lock (Gate)
        {
            return EnumerateCandidateChildNames(rootPath, temporaryMigrationFolderName).ToArray();
        }
    }

    internal static void CommitMigrationDestinationOwnership(
        string newRootPath,
        IEnumerable<string> migratingManagedFolderNames,
        IEnumerable<string> preExistingChildNames)
    {
        if (string.IsNullOrWhiteSpace(newRootPath) || !Directory.Exists(newRootPath))
        {
            return;
        }

        string[] migrating = migratingManagedFolderNames
            .Where(IsValidFolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (Gate)
        {
            ManagedStorageOwnershipSnapshot current = ReadSnapshotUnsafe(newRootPath);
            var managed = new HashSet<string>(current.ManagedFolders, StringComparer.OrdinalIgnoreCase);
            var foreign = new HashSet<string>(current.ForeignFolders, StringComparer.OrdinalIgnoreCase);

            foreach (string existingName in preExistingChildNames)
            {
                if (IsValidFolderName(existingName) &&
                    !migrating.Contains(existingName, StringComparer.OrdinalIgnoreCase) &&
                    !managed.Contains(existingName))
                {
                    foreign.Add(existingName);
                }
            }

            foreach (string name in migrating)
            {
                managed.Add(name);
                foreign.Remove(name);
            }

            WriteSnapshotUnsafe(newRootPath, managed, foreign);
        }
    }

    internal static void SnapshotMigrationDestinationOwnership(
        string newRootPath,
        IEnumerable<string> migratingManagedFolderNames,
        string temporaryMigrationFolderName)
    {
        if (string.IsNullOrWhiteSpace(newRootPath))
        {
            return;
        }

        Directory.CreateDirectory(newRootPath);
        string[] existing = CaptureExistingChildNames(newRootPath, temporaryMigrationFolderName);
        CommitMigrationDestinationOwnership(newRootPath, migratingManagedFolderNames, existing);
    }

    internal static void RecordManagedFolderRename(
        string rootPath,
        string oldFolderName,
        string newFolderName,
        string temporaryMigrationFolderName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return;
        }

        lock (Gate)
        {
            ManagedStorageOwnershipSnapshot current = ReadSnapshotUnsafe(rootPath);
            if (!current.HasManifest)
            {
                RegisterManagedFolders(rootPath, [newFolderName], temporaryMigrationFolderName);
                return;
            }

            var managed = new HashSet<string>(current.ManagedFolders, StringComparer.OrdinalIgnoreCase);
            var foreign = new HashSet<string>(current.ForeignFolders, StringComparer.OrdinalIgnoreCase);
            if (IsValidFolderName(oldFolderName))
            {
                managed.Remove(oldFolderName);
            }

            if (IsValidFolderName(newFolderName))
            {
                managed.Add(newFolderName);
                foreign.Remove(newFolderName);
            }

            WriteSnapshotUnsafe(rootPath, managed, foreign);
        }
    }

    internal static void UnregisterRemovedOrphanFolder(string rootPath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !IsValidFolderName(folderName))
        {
            return;
        }

        lock (Gate)
        {
            ManagedStorageOwnershipSnapshot current = ReadSnapshotUnsafe(rootPath);
            if (!current.HasManifest)
            {
                return;
            }

            var managed = new HashSet<string>(current.ManagedFolders, StringComparer.OrdinalIgnoreCase);
            var foreign = new HashSet<string>(current.ForeignFolders, StringComparer.OrdinalIgnoreCase);
            if (managed.Remove(folderName))
            {
                WriteSnapshotUnsafe(rootPath, managed, foreign);
            }
        }
    }

    internal static bool IsDedicatedDeskBoxRootPath(string rootPath)
    {
        try
        {
            string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string leaf = Path.GetFileName(trimmed);
            if (string.Equals(leaf, "DeskBox", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                string parentLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(parent));
                if (string.Equals(parentLeaf, "DeskBox", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCandidateChildNames(
        string rootPath,
        string temporaryMigrationFolderName)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(rootPath);
        }
        catch
        {
            yield break;
        }

        foreach (string child in children)
        {
            if (IsExcludedOrSystemDirectory(child, temporaryMigrationFolderName))
            {
                continue;
            }

            string name = Path.GetFileName(child);
            if (IsValidFolderName(name))
            {
                yield return name;
            }
        }
    }

    private static ManagedStorageOwnershipSnapshot ReadSnapshotUnsafe(string rootPath)
    {
        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foreign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifestPath;
        try
        {
            manifestPath = Path.Combine(Path.GetFullPath(rootPath), ManifestFileName);
        }
        catch
        {
            return new ManagedStorageOwnershipSnapshot(false, managed, foreign);
        }

        if (!File.Exists(manifestPath))
        {
            return new ManagedStorageOwnershipSnapshot(false, managed, foreign);
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(manifestPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length <= 2 || line.Equals(HeaderLine, StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith(ManagedPrefix, StringComparison.Ordinal))
                {
                    string name = line[ManagedPrefix.Length..];
                    if (IsValidFolderName(name))
                    {
                        managed.Add(name);
                    }
                }
                else if (line.StartsWith(ForeignPrefix, StringComparison.Ordinal))
                {
                    string name = line[ForeignPrefix.Length..];
                    if (IsValidFolderName(name))
                    {
                        foreign.Add(name);
                    }
                }
            }

            return new ManagedStorageOwnershipSnapshot(true, managed, foreign);
        }
        catch
        {
            return new ManagedStorageOwnershipSnapshot(false, managed, foreign);
        }
    }

    private static void WriteSnapshotUnsafe(
        string rootPath,
        HashSet<string> managed,
        HashSet<string> foreign)
    {
        try
        {
            string fullRoot = Path.GetFullPath(rootPath);
            if (!Directory.Exists(fullRoot))
            {
                return;
            }

            string manifestPath = Path.Combine(fullRoot, ManifestFileName);
            string tempPath = Path.Combine(
                fullRoot,
                $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");

            var sb = new StringBuilder();
            sb.AppendLine(HeaderLine);
            foreach (string name in managed.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(ManagedPrefix).AppendLine(name);
            }

            foreach (string name in foreign.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!managed.Contains(name))
                {
                    sb.Append(ForeignPrefix).AppendLine(name);
                }
            }

            File.WriteAllText(tempPath, sb.ToString(), Encoding.UTF8);
            if (File.Exists(manifestPath))
            {
                File.SetAttributes(manifestPath, FileAttributes.Normal);
            }

            File.Move(tempPath, manifestPath, overwrite: true);
            File.SetAttributes(manifestPath, FileAttributes.Hidden);
        }
        catch
        {
        }
    }

    private static bool IsValidFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name is not "." and not ".." &&
            name.IndexOfAny(['\r', '\n', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
            string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
    }
}
