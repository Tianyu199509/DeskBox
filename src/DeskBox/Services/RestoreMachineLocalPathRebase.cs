using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>Outcome of the post-restore machine-local path rebase.</summary>
/// <param name="Performed">The source and current user profiles differ, so the pass ran.</param>
/// <param name="SettingsPathsRebased">How many settings-level paths moved to the current profile.</param>
/// <param name="WidgetsRebased">How many default-storage file widgets were repointed.</param>
/// <param name="SourceProfileRoot">The source machine's user profile the paths came from.</param>
/// <param name="RebasedManagedStorageRootPath">
/// Normalized new managed storage root when it moved, null otherwise — the
/// caller creates this directory so file widgets resolve immediately.
/// </param>
internal sealed record RestorePathRebaseResult(
    bool Performed,
    int SettingsPathsRebased,
    int WidgetsRebased,
    string? SourceProfileRoot,
    string? RebasedManagedStorageRootPath)
{
    internal static RestorePathRebaseResult Skipped { get; } = new(false, 0, 0, null, null);

    internal bool Changed => SettingsPathsRebased > 0 || WidgetsRebased > 0;
}

/// <summary>
/// Rebases user-profile-local paths after a full restore. The archived
/// settings.json stores machine-local absolute paths (managed storage root,
/// custom snapshot folder, desktop-shortcut folder) and file widgets store
/// absolute <see cref="WidgetConfig.MappedFolderPath"/> values; restored onto
/// a machine with a different user account they all dangle. The source
/// profile is derived precisely from the backup manifest's data path (both
/// direct <c>...\AppData\Local\DeskBox\data</c> and packaged
/// <c>...\AppData\Local\Packages\&lt;PFN&gt;\...</c> layouts carry the segment),
/// never from existence heuristics — a temporarily missing drive must not
/// rewrite configured paths. Deliberately untouched: custom (non
/// default-following) widget folders and other user-chosen locations outside
/// the profile — their contents did not survive the move either, and silently
/// repointing them would manufacture plausible-looking empty folders.
/// </summary>
internal static class RestoreMachineLocalPathRebase
{
    internal static string? TryDeriveSourceProfileRoot(string? sourceDataPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDataPath))
        {
            return null;
        }

        string trimmed = sourceDataPath.Trim();
        int segmentIndex = trimmed.LastIndexOf("\\DeskBox\\.appdata\\", StringComparison.OrdinalIgnoreCase);
        if (segmentIndex <= 0 &&
            trimmed.EndsWith("\\DeskBox\\.appdata", StringComparison.OrdinalIgnoreCase))
        {
            segmentIndex = trimmed.Length - "\\DeskBox\\.appdata".Length;
        }

        if (segmentIndex <= 0)
        {
            segmentIndex = trimmed.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase);
        }

        if (segmentIndex <= 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(trimmed[..segmentIndex]);
        }
        catch
        {
            return null;
        }
    }

    internal static RestorePathRebaseResult Rebase(
        AppSettings settings,
        string? sourceDataPath,
        string currentProfileRoot)
    {
        string? sourceProfile = TryDeriveSourceProfileRoot(sourceDataPath);
        if (sourceProfile is null || string.IsNullOrWhiteSpace(currentProfileRoot))
        {
            return RestorePathRebaseResult.Skipped;
        }

        string targetProfile;
        try
        {
            targetProfile = Path.GetFullPath(currentProfileRoot.Trim());
        }
        catch
        {
            return RestorePathRebaseResult.Skipped;
        }

        // Same machine or same account: every stored path is still valid.
        if (string.Equals(sourceProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            return RestorePathRebaseResult.Skipped;
        }

        int settingsCount = 0;
        string? rebasedStorageRoot = null;
        if (TryRebaseProfilePath(
                settings.FileWidget.DefaultManagedStorageRootPath,
                sourceProfile,
                targetProfile,
                out string newStorageRoot))
        {
            settings.FileWidget.DefaultManagedStorageRootPath = newStorageRoot;
            rebasedStorageRoot = SettingsService.NormalizeManagedStorageRootPath(newStorageRoot);
            settingsCount++;
        }

        if (TryRebaseProfilePath(
                settings.Backup.AutomaticBackupDirectory,
                sourceProfile,
                targetProfile,
                out string newBackupDirectory))
        {
            settings.Backup.AutomaticBackupDirectory = newBackupDirectory;
            settingsCount++;
        }

        if (TryRebaseProfilePath(
                settings.FileWidget.ManagedStorageDesktopShortcutPath,
                sourceProfile,
                targetProfile,
                out string newShortcutPath))
        {
            settings.FileWidget.ManagedStorageDesktopShortcutPath = newShortcutPath;
            settingsCount++;
        }

        int widgetCount = 0;
        foreach (WidgetConfig widget in settings.WidgetLayout.Widgets)
        {
            if (widget.WidgetKind != WidgetKind.File ||
                !widget.FollowsDefaultStoragePath ||
                string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                continue;
            }

            if (TryRebaseProfilePath(widget.MappedFolderPath, sourceProfile, targetProfile, out string newMappedPath))
            {
                WidgetPathRebaseHelper.RebaseFolderTree(widget, widget.MappedFolderPath, newMappedPath);
                widget.MappedFolderPath = newMappedPath;
                widgetCount++;
            }
        }

        return new RestorePathRebaseResult(true, settingsCount, widgetCount, sourceProfile, rebasedStorageRoot);
    }

    private static bool TryRebaseProfilePath(
        string? path,
        string sourceProfile,
        string targetProfile,
        out string rebasedPath)
    {
        rebasedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string trimmed = path.Trim();
        if (!IsStrictlyUnderRoot(trimmed, sourceProfile))
        {
            return false;
        }

        string suffix = trimmed[sourceProfile.TrimEnd('\\', '/').Length..].TrimStart('\\', '/');
        try
        {
            rebasedPath = Path.GetFullPath(Path.Combine(targetProfile, suffix));
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool IsStrictlyUnderRoot(string path, string root)
    {
        string trimmedRoot = root.TrimEnd('\\', '/');
        return path.Length > trimmedRoot.Length &&
               path.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase) &&
               (path[trimmedRoot.Length] == '\\' || path[trimmedRoot.Length] == '/');
    }
}
