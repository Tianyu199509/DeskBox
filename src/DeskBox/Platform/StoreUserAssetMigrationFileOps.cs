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
    internal const string MigrationInProgressMarkerFileName = ".store-localcache-migrating.v1";

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

            string legacyDataDir = Path.Combine(normalizedLegacyRoot, "data");
            string durableDataDir = Path.Combine(normalizedDurableRoot, "data");
            string legacySettingsPath = Path.Combine(legacyDataDir, "settings.json");
            string durableSettingsPath = Path.Combine(durableDataDir, "settings.json");

            string sentinelPath = Path.Combine(normalizedDurableRoot, MigrationSentinelFileName);
            // A stale sentinel left behind by an earlier empty Store run must not
            // block migrating real Direct data when the Store profile has no settings yet.
            if (File.Exists(sentinelPath) &&
                (File.Exists(durableSettingsPath) || !File.Exists(legacySettingsPath)))
            {
                if (File.Exists(durableSettingsPath) && Directory.Exists(legacyDataDir))
                {
                    // Retry retiring any previously locked MSIX LocalCache shadow directory.
                    TryRetireMigratedLegacyLocalCacheDataDirectory(legacyDataDir, durableSettingsPath);
                }

                return;
            }

            EnsureHiddenDirectory(normalizedDurableRoot);
            string inProgressPath = Path.Combine(normalizedDurableRoot, MigrationInProgressMarkerFileName);
            bool allCopiedCleanly = true;

            if (Directory.Exists(legacyDataDir) &&
                (!File.Exists(durableSettingsPath) || File.Exists(inProgressPath)))
            {
                if (!File.Exists(inProgressPath))
                {
                    File.WriteAllText(inProgressPath, "v1\n", Encoding.UTF8);
                }

                allCopiedCleanly &= CopyDirectoryNonDestructive(legacyDataDir, durableDataDir);
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
                    allCopiedCleanly &= CopyDirectoryNonDestructive(normalizedLegacyRecovery, normalizedDurableRecovery);
                }
            }

            if (allCopiedCleanly)
            {
                File.WriteAllText(sentinelPath, "v1\n", Encoding.UTF8);
                if (File.Exists(inProgressPath))
                {
                    File.Delete(inProgressPath);
                }

                // Retire the physical MSIX LocalCache\Local\DeskBox\data directory
                // after verified migration so Win10 bindflt virtualization no longer
                // shadows real %LOCALAPPDATA%\DeskBox\data for Direct re-imports and
                // cross-channel orphan-folder protection. Never touches Direct's real
                // %LOCALAPPDATA%\DeskBox\data path.
                TryRetireMigratedLegacyLocalCacheDataDirectory(legacyDataDir, durableSettingsPath);
            }
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

    internal static bool CopyDirectoryNonDestructive(
        string sourceDir,
        string destinationDir,
        bool overwriteExisting = false)
    {
        DirectoryInfo rootInfo = new(sourceDir);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        Directory.CreateDirectory(destinationDir);
        bool allCopiedCleanly = true;

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
                if (overwriteExisting || !File.Exists(targetFile))
                {
                    File.Copy(filePath, targetFile, overwrite: overwriteExisting);
                }
            }
            catch
            {
                // Skip individual unreadable or locked files and retry on next launch.
                allCopiedCleanly = false;
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

                allCopiedCleanly &= CopyDirectoryNonDestructive(
                    subDir,
                    Path.Combine(destinationDir, subInfo.Name),
                    overwriteExisting);
            }
            catch
            {
                // Skip unreadable subdirectories and retry on next launch.
                allCopiedCleanly = false;
            }
        }

        return allCopiedCleanly;
    }

    internal static void RebaseMigratedJsonStorePaths(string legacyDataDir, string durableDataDir)
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

        string settingsJson = Path.Combine(durableDataDir, "settings.json");
        RewriteJsonPathPrefixesIfPresent(settingsJson, oldPrefixes, newPrefix);
        RewriteJsonPathPrefixesIfPresent(settingsJson + ".bak", oldPrefixes, newPrefix);

        string widgetLayoutJson = Path.Combine(durableDataDir, "widget-layout.json");
        RewriteJsonPathPrefixesIfPresent(widgetLayoutJson, oldPrefixes, newPrefix);
        RewriteJsonPathPrefixesIfPresent(widgetLayoutJson + ".bak", oldPrefixes, newPrefix);

        string quickCaptureJson = Path.Combine(durableDataDir, "quick-capture", "quick-capture.json");
        RewriteJsonPathPrefixesIfPresent(quickCaptureJson, oldPrefixes, newPrefix);
        RewriteJsonPathPrefixesIfPresent(quickCaptureJson + ".bak", oldPrefixes, newPrefix);

        string widgetsDir = Path.Combine(durableDataDir, "widgets");
        if (Directory.Exists(widgetsDir))
        {
            foreach (string todoJson in Directory.EnumerateFiles(widgetsDir, "todo.json*", SearchOption.AllDirectories))
            {
                RewriteJsonPathPrefixesIfPresent(todoJson, oldPrefixes, newPrefix);
            }
        }
    }

    private static void WriteTextAtomically(string filePath, string updated)
    {
        string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, updated, Encoding.UTF8);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
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
            string updated = RewriteJsonPathPrefixesContent(content, oldPrefixes, newPrefix);

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

    internal static string RewriteJsonPathPrefixesContent(
        string content,
        IReadOnlyList<string> oldPrefixes,
        string newPrefix)
    {
        string updated = content;
        string escapedNew = EscapeJsonBackslashes(newPrefix);
        string slashNew = newPrefix.Replace('\\', '/');
        string defaultEncodedNew = System.Text.Json.JsonEncodedText.Encode(
            newPrefix,
            System.Text.Encodings.Web.JavaScriptEncoder.Default).ToString();
        string defaultEncodedSlashNew = System.Text.Json.JsonEncodedText.Encode(
            slashNew,
            System.Text.Encodings.Web.JavaScriptEncoder.Default).ToString();

        foreach (string oldPrefix in oldPrefixes)
        {
            string oldSlash = oldPrefix.Replace('\\', '/');
            // 1. Default System.Text.Json JavaScriptEncoder escaping (\uXXXX for non-ASCII & HTML chars).
            string defaultEncodedOld = System.Text.Json.JsonEncodedText.Encode(
                oldPrefix,
                System.Text.Encodings.Web.JavaScriptEncoder.Default).ToString();
            updated = updated.Replace(
                defaultEncodedOld,
                defaultEncodedNew,
                StringComparison.OrdinalIgnoreCase);

            string defaultEncodedSlashOld = System.Text.Json.JsonEncodedText.Encode(
                oldSlash,
                System.Text.Encodings.Web.JavaScriptEncoder.Default).ToString();
            updated = updated.Replace(
                defaultEncodedSlashOld,
                defaultEncodedSlashNew,
                StringComparison.OrdinalIgnoreCase);

            // 2. Raw Unicode escaped-backslash and forward-slash forms.
            updated = updated.Replace(
                EscapeJsonBackslashes(oldPrefix),
                escapedNew,
                StringComparison.OrdinalIgnoreCase);
            updated = updated.Replace(
                oldSlash,
                slashNew,
                StringComparison.OrdinalIgnoreCase);
        }

        return updated;
    }

    internal static void TryRetireMigratedLegacyLocalCacheDataDirectory(
        string legacyDataDir,
        string durableSettingsPath)
    {
        try
        {
            if (!File.Exists(durableSettingsPath))
            {
                return;
            }

            string? physicalDataDir = null;
            string legacySettingsPath = Path.Combine(legacyDataDir, "settings.json");
            if (File.Exists(legacySettingsPath) &&
                Win32Helper.TryGetFinalPath(legacySettingsPath, out string physicalSettingsPath) &&
                !string.IsNullOrWhiteSpace(physicalSettingsPath) &&
                IsLegacyLocalCachePath(physicalSettingsPath))
            {
                physicalDataDir = Path.GetDirectoryName(physicalSettingsPath);
            }
            else if (Directory.Exists(legacyDataDir) &&
                     Win32Helper.TryGetFinalPath(legacyDataDir, out string resolvedDataDir) &&
                     !string.IsNullOrWhiteSpace(resolvedDataDir) &&
                     IsLegacyLocalCachePath(resolvedDataDir))
            {
                physicalDataDir = resolvedDataDir;
            }

            if (string.IsNullOrWhiteSpace(physicalDataDir) ||
                !Directory.Exists(physicalDataDir) ||
                !IsLegacyLocalCachePath(physicalDataDir))
            {
                return;
            }

            string trimmedPhysicalDataDir = TrimTrailingSeparators(physicalDataDir);
            string? parentDir = Path.GetDirectoryName(trimmedPhysicalDataDir);
            if (string.IsNullOrWhiteSpace(parentDir))
            {
                return;
            }

            string retiredPath = Path.Combine(parentDir, "data.migrated-to-appdata");
            if (Directory.Exists(retiredPath))
            {
                retiredPath = Path.Combine(parentDir, $"data.migrated-to-appdata.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            }

            Directory.Move(trimmedPhysicalDataDir, retiredPath);
        }
        catch
        {
            // Non-fatal: if an antivirus or shell handle holds LocalCache open,
            // leave it intact rather than failing startup.
        }
    }

    internal static bool IsLegacyLocalCachePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Contains(@"\Packages\", StringComparison.OrdinalIgnoreCase) &&
        path.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);

    private static string EscapeJsonBackslashes(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static string EnsureTrailingBackslash(string path) =>
        TrimTrailingSeparators(path) + "\\";

    internal static string StageTemporaryDragExportCopy(
        string sourcePath,
        string categoryFolder)
    {
        DropStagingFileOps.TriggerThrottledStaleStagingCleanup();
        string resolvedSourcePath = Win32Helper.ResolveUnvirtualizedPhysicalPath(sourcePath);
        string stagingRoot = DropStagingFileOps.GetStagingRootDirectory();
        DropStagingFileOps.EnsureSafeStagingRootDirectory(stagingRoot);
        string stageDir = Win32Helper.ResolveUnvirtualizedPhysicalPath(
            Path.Combine(
                stagingRoot,
                categoryFolder,
                Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stageDir);
        string stagedPath = Path.Combine(stageDir, Path.GetFileName(resolvedSourcePath));
        ClipboardStorageFileOps.CopyFileOverwrite(resolvedSourcePath, stagedPath);
        File.SetAttributes(stagedPath, FileAttributes.Normal);
        return Win32Helper.ResolveUnvirtualizedPhysicalPath(stagedPath);
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static void TryDeleteFileQuietly(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    internal static bool TryMirrorShortcutFileQuietly(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            {
                return false;
            }

            File.Copy(sourcePath, destinationPath, overwrite: false);
            File.SetAttributes(destinationPath, FileAttributes.Normal);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
