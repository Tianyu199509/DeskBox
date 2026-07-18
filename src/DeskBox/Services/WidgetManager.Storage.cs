// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
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

    public void SyncStorageFolderEntries()
    {
        string rootPath = GetManagedStorageRootPath();
        Directory.CreateDirectory(rootPath);

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
        if (!_widgets.TryGetValue(widgetId, out var entry) || IsDeleted(widgetId))
        {
            return;
        }

        await entry.ViewModel.HandleItemsMovedOutAsync(sourcePaths);
    }

    public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(string newRootPath)
    {
        string oldRootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        string normalizedNewRootPath = SettingsService.NormalizeManagedStorageRootPath(newRootPath);

        if (string.Equals(oldRootPath, normalizedNewRootPath, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            await _settingsService.SaveAsync();
            return new ManagedStorageMigrationResult(0, oldRootPath, normalizedNewRootPath);
        }

        Directory.CreateDirectory(normalizedNewRootPath);

        var affectedWidgets = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && widget.FollowsDefaultStoragePath && !IsDeleted(widget.Id))
            .Select(widget =>
            {
                string managedFolderName = string.IsNullOrWhiteSpace(widget.ManagedFolderName)
                    ? CreateManagedFolderName(widget.Name, widget.Id)
                    : widget.ManagedFolderName;
                string sourceFolder = string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                    ? Path.Combine(oldRootPath, managedFolderName)
                    : widget.MappedFolderPath;
                string destinationFolder = Path.Combine(normalizedNewRootPath, managedFolderName);

                return new
                {
                    Widget = widget,
                    ManagedFolderName = managedFolderName,
                    SourceFolder = sourceFolder,
                    DestinationFolder = destinationFolder
                };
            })
            .ToList();

        var completedMoves = new List<(string SourceFolder, string DestinationFolder)>(affectedWidgets.Count);
        var originalWidgetStorage = affectedWidgets.ToDictionary(
            widget => widget.Widget.Id,
            widget => (widget.Widget.ManagedFolderName, widget.Widget.MappedFolderPath),
            StringComparer.Ordinal);

        SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: true);
        try
        {
            if (affectedWidgets.Count > 0)
            {
                await Task.Delay(100);
            }

            foreach (var widgetPlan in affectedWidgets)
            {
                await _fileService.RelocateDirectoryAsync(widgetPlan.SourceFolder, widgetPlan.DestinationFolder);
                completedMoves.Add((widgetPlan.SourceFolder, widgetPlan.DestinationFolder));
            }

            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                widgetPlan.Widget.ManagedFolderName = widgetPlan.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = widgetPlan.DestinationFolder;
            }

            await _settingsService.SaveAsync();
            SyncStorageFolderEntries(oldRootPath);

            foreach (var widgetPlan in affectedWidgets)
            {
                if (!_widgets.TryGetValue(widgetPlan.Widget.Id, out var entry))
                {
                    continue;
                }

                try
                {
                    await entry.ViewModel.RefreshFromConfigAsync();
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Refresh failed for widget '{widgetPlan.Widget.Id}': {ex}");
                }
            }
        }
        catch
        {
            _settingsService.Settings.DefaultManagedStorageRootPath = oldRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                if (!originalWidgetStorage.TryGetValue(widgetPlan.Widget.Id, out var originalStorage))
                {
                    continue;
                }

                widgetPlan.Widget.ManagedFolderName = originalStorage.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = originalStorage.MappedFolderPath;
            }

            foreach (var move in completedMoves.AsEnumerable().Reverse())
            {
                try
                {
                    await _fileService.RelocateDirectoryAsync(move.DestinationFolder, move.SourceFolder);
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Rollback failed for '{move.DestinationFolder}' -> '{move.SourceFolder}': {ex}");
                }
            }

            throw;
        }
        finally
        {
            SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: false);
        }

        return new ManagedStorageMigrationResult(affectedWidgets.Count, oldRootPath, normalizedNewRootPath);
    }

    private void SetManagedStorageMigrationBusy(IEnumerable<string> widgetIds, bool isBusy)
    {
        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_widgets.TryGetValue(widgetId, out var entry))
            {
                entry.Window.SetMigrationBusy(isBusy);
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
        if (IsManagedWidgetNameInUse(newName, desiredFolderName, config.Id) ||
            IsUnavailableManagedFolderPath(destinationFolderPath, currentFolderPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameExists"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => Directory.Move(currentFolderPath, destinationFolderPath));
        }

        config.ManagedFolderName = desiredFolderName;
        config.MappedFolderPath = destinationFolderPath;

        if (_widgets.TryGetValue(config.Id, out var entry))
        {
            await entry.ViewModel.RefreshFromConfigAsync();
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
        var shortcut = ShortcutHelper.Resolve(shortcutPath);
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
