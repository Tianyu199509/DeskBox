// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing Storage logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    public void SyncMappedWidgetShortcut(string widgetId)
    {
        var config = FindConfig(widgetId);
        if (config is null || IsDeleted(widgetId))
        {
            return;
        }

        SyncMappedWidgetShortcut(config);
    }

    /// <summary>
    /// Recreates the managed storage root and syncs mapped-widget shortcuts.
    /// Returns false when the root cannot be created (e.g. its drive is
    /// currently detached); callers must treat that as non-fatal so widget
    /// restoration continues without the root.
    /// </summary>
    public bool SyncStorageFolderEntries()
    {
        string rootPath = GetManagedStorageRootPath();
        try
        {
            Directory.CreateDirectory(rootPath);
        }
        catch (Exception ex)
        {
            // A detached drive must not abort the rest of startup: widgets
            // restore with unavailable folders and reconnect when the drive
            // returns.
            App.Log($"[WidgetManager] Managed storage root is unavailable ('{rootPath}'): {ex.Message}");
            return false;
        }

        var activeWidgetIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);

        RemoveStaleMappedWidgetShortcuts(rootPath, activeWidgetIds);

        foreach (var config in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
                     .ToList())
        {
            if (config.FollowsDefaultStoragePath)
            {
                continue;
            }

            SyncMappedWidgetShortcut(config);
        }

        return true;
    }

    public bool CanCleanupManagedStorageForWidget(string widgetId)
    {
        var config = FindConfig(widgetId);
        return config is not null &&
               IsDefaultManagedStorageFolder(config.MappedFolderPath) &&
               config.FollowsDefaultStoragePath &&
               Directory.Exists(config.MappedFolderPath);
    }

    public IReadOnlyList<ManagedStorageFolderCleanupCandidate> GetOrphanManagedStorageFolders()
    {
        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(widget, rootPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(rootPath)
            .Select(Path.GetFullPath)
            .Where(path => !Path.GetFileName(path).Equals(
                ManagedStorageMigrationService.TemporaryFolderName, StringComparison.OrdinalIgnoreCase))
            .Where(path => !activePaths.Contains(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Select(path => new ManagedStorageFolderCleanupCandidate(
                Path.GetFileName(path),
                path,
                CountDirectoryEntries(path)))
            .OrderBy(candidate => candidate.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await MoveManagedFolderContentsToDesktopAsync(normalizedPath);
    }

    public async Task DeleteOrphanManagedStorageFolderAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await _fileService.DeleteEntryAsync(normalizedPath, recycle: _recycleManagedFolderDeletes);
    }

    public async Task<int> RestoreOrphanManagedStorageFoldersAsync(IEnumerable<string> folderPaths)
    {
        int restoredCount = 0;
        bool canCreateWindow = CanCreateWidgetWindowOnCurrentThread();
        foreach (var folderPath in folderPaths)
        {
            string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
            if (!Directory.Exists(normalizedPath))
            {
                continue;
            }

            string folderName = Path.GetFileName(normalizedPath);
            var config = new WidgetConfig
            {
                Name = string.IsNullOrWhiteSpace(folderName)
                    ? _localizationService.T("Widget.DefaultNameShort")
                    : folderName,
                WidgetKind = WidgetKind.File,
                MappedFolderPath = normalizedPath,
                FollowsDefaultStoragePath = true,
                ManagedFolderName = folderName,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                Width = _settingsService.Settings.DefaultWidgetWidth,
                Height = _settingsService.Settings.DefaultWidgetHeight,
                IsVisible = true,
                IsDisabled = false
            };

            ApplyNewWidgetPlacement(config);
            _settingsService.Settings.Widgets.Add(config);
            if (canCreateWindow)
            {
                await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
            }
            restoredCount++;
            App.Log($"[WidgetManager] Restored orphan managed storage folder as widget: {normalizedPath} -> {config.Id}");
        }

        if (restoredCount > 0)
        {
            await _settingsService.SaveAsync();
        }

        return restoredCount;
    }

    public async Task NotifyItemsMovedOutAsync(string widgetId, IEnumerable<string> sourcePaths)
    {
        if (IsDeleted(widgetId))
        {
            return;
        }

        string[] paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (_fileWidgets.TryGetValue(widgetId, out var entry))
        {
            await entry.ViewModel.HandleItemsMovedOutAsync(paths);
            return;
        }

        ContentWidgetWindow? contentWindow =
            _contentWidgets.TryGetValue(widgetId, out var registeredWindow)
                ? registeredWindow
                : _contentWidgets.Values
                    .Distinct()
                    .FirstOrDefault(window =>
                        window.CurrentContent is FileWidgetContentAdapter surface &&
                        string.Equals(
                            surface.WidgetId,
                            widgetId,
                            StringComparison.Ordinal));
        if (contentWindow?.CurrentContent is not FileWidgetContentAdapter fileSurface ||
            !string.Equals(
                fileSurface.WidgetId,
                widgetId,
                StringComparison.Ordinal))
        {
            App.Log(
                $"[WidgetManager] Moved-out refresh target not loaded " +
                $"widget={widgetId} paths={paths.Length}");
            return;
        }

        await fileSurface.ViewModel.HandleItemsMovedOutAsync(paths);
    }

    private void SetManagedStorageMigrationBusy(IEnumerable<string> widgetIds, bool isBusy)
    {
        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_fileWidgets.TryGetValue(widgetId, out var entry))
            {
                entry.SetMigrationBusy(isBusy);
                continue;
            }

            ContentWidgetWindow? contentWindow = _contentWidgets.Values
                .Distinct()
                .FirstOrDefault(window =>
                    window.CurrentContent is FileWidgetContentAdapter surface &&
                    string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
            if (contentWindow?.CurrentContent is FileWidgetContentAdapter fileSurface)
            {
                fileSurface.SetMigrationBusy(isBusy);
            }
        }
    }

    private async Task RenameManagedWidgetFolderAsync(WidgetConfig config, string newName)
    {
        if (!config.FollowsDefaultStoragePath)
        {
            return;
        }

        string rootPath = GetManagedStorageRootPath();
        string currentFolderPath = string.IsNullOrWhiteSpace(config.MappedFolderPath)
            ? Path.Combine(rootPath, config.ManagedFolderName ?? string.Empty)
            : Path.GetFullPath(config.MappedFolderPath);
        string currentFolderName = string.IsNullOrWhiteSpace(config.ManagedFolderName)
            ? Path.GetFileName(currentFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : config.ManagedFolderName;

        if (!Directory.Exists(currentFolderPath))
        {
            throw new DirectoryNotFoundException(currentFolderPath);
        }

        string desiredFolderName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(desiredFolderName))
        {
            desiredFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        if (string.Equals(currentFolderName, desiredFolderName, StringComparison.OrdinalIgnoreCase))
        {
            config.ManagedFolderName = currentFolderName;
            config.MappedFolderPath = currentFolderPath;
            return;
        }

        string destinationFolderPath = Path.Combine(rootPath, desiredFolderName);
        if (IsManagedWidgetNameInUse(newName, desiredFolderName, config.Id))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameExists"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(destinationFolderPath))
        {
            bool currentFolderIsEmpty = !Directory.EnumerateFileSystemEntries(currentFolderPath).Any();
            if (!currentFolderIsEmpty)
            {
                // Both sides hold files and merging them could shadow either
                // copy, so the rename stays blocked with an accurate message
                // (the folder usually belongs to a closed widget, #113).
                throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameUnavailable"));
            }

            // Never adopt a folder a live widget already claims (a mapped
            // widget's folder can sit under the managed root when the root
            // moved around it): two widgets sharing one directory means the
            // first "close and delete files" wipes the other's contents.
            // The shared resolved-path guard is required here — a junction
            // or symlink alias resolves to the claimed physical directory
            // while comparing lexical paths would wave it through.
            // Whether the destination is genuinely a closed widget's kept
            // folder is unprovable — RemoveWidgetImmediate drops the config
            // and only the id tombstone survives — so live-claim exclusion
            // is the only enforceable guard.
            EnsureFileWidgetPathAvailable(
                destinationFolderPath,
                excludedWidgetId: config.Id,
                candidateFollowsDefaultStoragePath: true);

            // The empty current folder is the default folder a fresh widget
            // got; the existing destination is the managed folder a closed
            // widget kept behind on purpose. Adopt it as the storage
            // location instead of dead-ending the name (feedback #113):
            // nothing inside either folder is moved, merged, or deleted, and
            // the kept contents show up in the widget again.
            Directory.Delete(currentFolderPath);
            App.Log(
                $"[WidgetManager] Managed folder rename adopted the existing " +
                $"folder '{destinationFolderPath}' for widget '{config.Id}'");
        }
        else if (File.Exists(destinationFolderPath))
        {
            // A file holds the name: the widget needs a directory and there
            // is nothing safe to adopt here.
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameUnavailable"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(destinationFolderPath))
        {
            await Task.Run(() => MoveManagedFolderWithRetry(currentFolderPath, destinationFolderPath));
        }

        WidgetFileStackSettings.RebaseManagedFolderPaths(
            config,
            currentFolderPath,
            destinationFolderPath);
        config.ManagedFolderName = desiredFolderName;
        config.MappedFolderPath = destinationFolderPath;

        await RefreshFileWidgetAsync(config.Id);
    }

    /// <summary>
    /// A managed-folder rename can be denied by a transient handle (antivirus
    /// scan, search indexer) or a long-lived one (an Explorer window parked
    /// inside the folder). A short bounded retry absorbs the transient class;
    /// the persistent class surfaces as a localized, actionable message
    /// instead of the raw OS denial (feedback 240).
    /// </summary>
    private void MoveManagedFolderWithRetry(string sourcePath, string destinationPath)
    {
        const int maxAttempts = 3;
        const int retryDelayMilliseconds = 300;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(sourcePath, destinationPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                {
                    App.Log(
                        "[WidgetManager] Managed folder rename denied after " +
                        $"{attempt} attempts '{sourcePath}': {ex.Message}");
                    throw new IOException(
                        _localizationService.T("Widget.Error.ManagedFolderInUse"),
                        ex);
                }

                Thread.Sleep(retryDelayMilliseconds);
            }
        }
    }

    private void RemoveMappedWidgetShortcut(WidgetConfig config)
    {
        DeleteMappedWidgetShortcut(GetExistingMappedWidgetShortcutPath(config, GetManagedStorageRootPath()), config.Id);
    }

    private void DeleteMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath) ||
            !IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId))
        {
            return;
        }

        try
        {
            File.Delete(shortcutPath);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedShortcut] Failed to delete shortcut '{shortcutPath}': {ex}");
        }
    }

    private void RemoveStaleMappedWidgetShortcuts(string rootPath, ISet<string> activeWidgetIds)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId) || activeWidgetIds.Contains(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private void RemoveAllMappedWidgetShortcuts(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private string GetExistingMappedWidgetShortcutPath(WidgetConfig config, string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => IsDeskBoxMappedWidgetShortcut(path, config.Id)) ?? string.Empty;
    }

    private string BuildAvailableMappedShortcutPath(
        string displayName,
        string widgetId,
        string rootPath,
        string currentShortcutPath)
    {
        string shortcutName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(shortcutName))
        {
            shortcutName = _localizationService.T("Widget.MappedShortcutBaseName");
        }

        string desiredPath = Path.Combine(rootPath, $"{shortcutName}.lnk");
        if (!string.IsNullOrWhiteSpace(currentShortcutPath) &&
            string.Equals(Path.GetFullPath(currentShortcutPath), desiredPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentShortcutPath;
        }

        if (CanUseMappedShortcutPath(desiredPath, widgetId))
        {
            return desiredPath;
        }

        int suffix = 2;
        while (true)
        {
            string candidatePath = Path.Combine(rootPath, $"{shortcutName} ({suffix++}).lnk");
            if (CanUseMappedShortcutPath(candidatePath, widgetId))
            {
                return candidatePath;
            }
        }
    }

    private bool CanUseMappedShortcutPath(string shortcutPath, string widgetId)
    {
        if (!File.Exists(shortcutPath) && !Directory.Exists(shortcutPath))
        {
            return true;
        }

        return File.Exists(shortcutPath) && IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId);
    }

    private static bool IsDeskBoxMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        return string.Equals(
            GetDeskBoxMappedWidgetShortcutId(shortcutPath),
            widgetId,
            StringComparison.Ordinal);
    }

    private static string? GetDeskBoxMappedWidgetShortcutId(string shortcutPath)
    {
        var shortcut = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        if (shortcut?.Description.StartsWith(ManagedShortcutDescriptionPrefix, StringComparison.Ordinal) != true)
        {
            return null;
        }

        return shortcut.Description[ManagedShortcutDescriptionPrefix.Length..];
    }

    private static string BuildMappedWidgetShortcutDescription(string widgetId)
    {
        return $"{ManagedShortcutDescriptionPrefix}{widgetId}";
    }

    private async Task ApplyWidgetRemovalActionAsync(WidgetConfig config, WidgetRemovalAction removalAction)
    {
        if (removalAction == WidgetRemovalAction.RemoveWidgetOnly)
        {
            return;
        }

        if (!config.FollowsDefaultStoragePath || !IsDefaultManagedStorageFolder(config.MappedFolderPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderActionOnlyDefault"));
        }

        string folderPath = Path.GetFullPath(config.MappedFolderPath!);
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        if (removalAction == WidgetRemovalAction.MoveManagedFolderContentsToDesktop)
        {
            await MoveManagedFolderContentsToDesktopAsync(folderPath);
            return;
        }

        if (removalAction == WidgetRemovalAction.DeleteManagedFolder)
        {
            await _fileService.DeleteEntryAsync(folderPath, recycle: _recycleManagedFolderDeletes);
        }
    }

    private async Task MoveManagedFolderContentsToDesktopAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        string desktopPath = _desktopPathProvider();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = Directory.EnumerateFileSystemEntries(folderPath)
            .Select(path => new FileService.FileTransferPlan(
                path,
                FileService.GetAvailablePath(Path.Combine(desktopPath, Path.GetFileName(path)), reservedPaths)))
            .ToList();

        if (plans.Count > 0)
        {
            await _fileService.ExecuteTransferPlanAsync(plans, move: true);
        }

        if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
        {
            Directory.Delete(folderPath, recursive: false);
        }
    }

    private string ValidateOrphanManagedStorageFolderPath(string folderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath);
        if (!IsDefaultManagedStorageFolder(normalizedPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderCleanupOnlyDefault"));
        }

        if (!Directory.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(
                widget,
                SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (activePaths.Contains(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderStillActive"));
        }

        return normalizedPath;
    }

    private bool IsDefaultManagedStorageFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(rootPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(normalizedPath);
        return parentPath is not null &&
               string.Equals(
                   parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   rootPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetPossibleManagedStoragePaths(WidgetConfig widget, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            yield return Path.GetFullPath(widget.MappedFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (!string.IsNullOrWhiteSpace(widget.ManagedFolderName))
        {
            yield return Path.GetFullPath(Path.Combine(rootPath, widget.ManagedFolderName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static int CountDirectoryEntries(string folderPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folderPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    private string BuildManagedFolderPath(string managedFolderName)
    {
        return Path.Combine(
            GetManagedStorageRootPath(),
            managedFolderName);
    }

    private string GetManagedStorageRootPath()
    {
        return SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
    }

    private string CreateManagedFolderName(
        string displayName,
        string? widgetId = null,
        string? reusableFolderPath = null)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = GetManagedStorageRootPath();
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? reusablePath = string.IsNullOrWhiteSpace(reusableFolderPath)
            ? null
            : Path.GetFullPath(reusableFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) ||
               IsUnavailableManagedFolderPath(Path.Combine(rootPath, candidate), reusablePath))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private bool IsManagedWidgetNameInUse(string displayName, string managedFolderName, string widgetId)
    {
        return _settingsService.Settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id) &&
            !string.Equals(widget.Id, widgetId, StringComparison.Ordinal) &&
            (string.Equals(widget.Name.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(widget.ManagedFolderName, managedFolderName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsUnavailableManagedFolderPath(string folderPath, string? reusableFolderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (reusableFolderPath is not null &&
            string.Equals(normalizedPath, reusableFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Directory.Exists(normalizedPath) || File.Exists(normalizedPath);
    }

    private bool ShouldMoveManagedItems()
    {
        return string.Equals(
            _settingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionMove,
            StringComparison.Ordinal);
    }

}
