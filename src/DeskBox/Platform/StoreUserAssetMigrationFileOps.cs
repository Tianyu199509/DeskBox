using System.Text;

namespace DeskBox.Platform;

/// <summary>
/// One-time non-destructive migration from MSIX-virtualized <c>%LOCALAPPDATA%\DeskBox</c>
/// (<c>Packages\&lt;PFN&gt;\LocalCache\Local\DeskBox</c>) and <c>%LOCALAPPDATA%\DeskBox-Recovery</c>
/// into the unvirtualized user-profile Store data root (<c>%USERPROFILE%\DeskBox\.appdata</c>)
/// and recovery root (<c>%USERPROFILE%\DeskBox\.appdata-recovery</c>) so Store resets or
/// reinstalls do not wipe managed attachments, Quick Capture images, custom widget assets,
/// or automatic recovery snapshots.
/// </summary>
internal static class StoreUserAssetMigrationFileOps
{
    internal const string MigrationSentinelFileName = ".store-localcache-migrated.v1";

    internal static void EnsureStoreDurableRootInitialized(
        string legacyLocalAppDataRoot,
        string durableRootPath,
        string legacyRecoveryRoot,
        string durableRecoveryPath)
    {
        if (string.IsNullOrWhiteSpace(legacyLocalAppDataRoot) ||
            string.IsNullOrWhiteSpace(durableRootPath))
        {
            return;
        }

        try
        {
            string normalizedLegacyRoot = TrimTrailingSeparators(Path.GetFullPath(legacyLocalAppDataRoot));
            string normalizedDurableRoot = TrimTrailingSeparators(Path.GetFullPath(durableRootPath));
            if (string.Equals(normalizedLegacyRoot, normalizedDurableRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string sentinelPath = Path.Combine(normalizedDurableRoot, MigrationSentinelFileName);
            if (File.Exists(sentinelPath))
            {
                return;
            }

            EnsureHiddenDirectory(normalizedDurableRoot);

            string legacyDataDir = Path.Combine(normalizedLegacyRoot, "data");
            string durableDataDir = Path.Combine(normalizedDurableRoot, "data");
            string durableSettingsPath = Path.Combine(durableDataDir, "settings.json");

            if (Directory.Exists(legacyDataDir) && !File.Exists(durableSettingsPath))
            {
                CopyDirectoryNonDestructive(legacyDataDir, durableDataDir);
                RebaseMigratedJsonStorePaths(legacyDataDir, durableDataDir);
            }

            if (!string.IsNullOrWhiteSpace(legacyRecoveryRoot) &&
                !string.IsNullOrWhiteSpace(durableRecoveryPath))
            {
                string normalizedLegacyRecovery = TrimTrailingSeparators(Path.GetFullPath(legacyRecoveryRoot));
                string normalizedDurableRecovery = TrimTrailingSeparators(Path.GetFullPath(durableRecoveryPath));
                if (!string.Equals(normalizedLegacyRecovery, normalizedDurableRecovery, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(normalizedLegacyRecovery))
                {
                    EnsureHiddenDirectory(normalizedDurableRecovery);
                    CopyDirectoryNonDestructive(normalizedLegacyRecovery, normalizedDurableRecovery);
                }
            }

            File.WriteAllText(sentinelPath, "v1\n", Encoding.UTF8);
        }
        catch
        {
            // Never block or crash startup if migration encounters a locked file or read-only volume.
        }
    }

    internal static void EnsureHiddenDirectory(string directoryPath)
    {
        DirectoryInfo info = Directory.CreateDirectory(directoryPath);
        try
        {
            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch
        {
            // Attribute stamping is cosmetic and best-effort.
        }
    }

    private static void CopyDirectoryNonDestructive(string sourceDir, string destinationDir)
    {
        DirectoryInfo rootInfo = new(sourceDir);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        Directory.CreateDirectory(destinationDir);

        foreach (string filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(filePath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string targetFile = Path.Combine(destinationDir, Path.GetFileName(filePath));
                if (!File.Exists(targetFile))
                {
                    File.Copy(filePath, targetFile, overwrite: false);
                }
            }
            catch
            {
                // Skip individual unreadable or locked files.
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                DirectoryInfo subInfo = new(subDir);
                if ((subInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                CopyDirectoryNonDestructive(
                    subDir,
                    Path.Combine(destinationDir, subInfo.Name));
            }
            catch
            {
                // Skip unreadable subdirectories.
            }
        }
    }

    private static void RebaseMigratedJsonStorePaths(string legacyDataDir, string durableDataDir)
    {
        var oldPrefixes = new List<string>(2)
        {
            EnsureTrailingBackslash(legacyDataDir)
        };

        if (Win32Helper.TryGetFinalPath(legacyDataDir, out string physicalLegacyDataDir) &&
            !string.IsNullOrWhiteSpace(physicalLegacyDataDir))
        {
            string withSep = EnsureTrailingBackslash(physicalLegacyDataDir);
            if (!oldPrefixes.Contains(withSep, StringComparer.OrdinalIgnoreCase))
            {
                oldPrefixes.Add(withSep);
            }
        }

        string newPrefix = EnsureTrailingBackslash(durableDataDir);

        string quickCaptureJson = Path.Combine(durableDataDir, "quick-capture", "quick-capture.json");
        RewriteJsonPathPrefixesIfPresent(quickCaptureJson, oldPrefixes, newPrefix);
        RewriteJsonPathPrefixesIfPresent(quickCaptureJson + ".bak", oldPrefixes, newPrefix);

        string widgetsDir = Path.Combine(durableDataDir, "widgets");
        if (!Directory.Exists(widgetsDir))
        {
            return;
        }

        foreach (string todoJson in Directory.EnumerateFiles(widgetsDir, "todo.json*", SearchOption.AllDirectories))
        {
            RewriteJsonPathPrefixesIfPresent(todoJson, oldPrefixes, newPrefix);
        }

        foreach (string storeJson in Directory.EnumerateFiles(widgetsDir, "store.json*", SearchOption.AllDirectories))
        {
            RewriteJsonPathPrefixesIfPresent(storeJson, oldPrefixes, newPrefix);
        }
    }

    private static void RewriteJsonPathPrefixesIfPresent(
        string filePath,
        IReadOnlyList<string> oldPrefixes,
        string newPrefix)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            string content = File.ReadAllText(filePath, Encoding.UTF8);
            string updated = content;
            string escapedNew = EscapeJsonBackslashes(newPrefix);
            string slashNew = newPrefix.Replace('\\', '/');

            foreach (string oldPrefix in oldPrefixes)
            {
                updated = updated.Replace(
                    EscapeJsonBackslashes(oldPrefix),
                    escapedNew,
                    StringComparison.OrdinalIgnoreCase);
                updated = updated.Replace(
                    oldPrefix.Replace('\\', '/'),
                    slashNew,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                return;
            }

            string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, updated, Encoding.UTF8);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // Best-effort path rebase; backup/restore path rebasing remains available as fallback.
        }
    }

    private static string EscapeJsonBackslashes(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string EnsureTrailingBackslash(string path) =>
        TrimTrailingSeparators(path) + "\\";

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
