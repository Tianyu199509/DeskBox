using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            EnsureFolderBackedConfig();
            MappedFolderPath = Config.MappedFolderPath;
await LoadFolderContentsAsync(MappedFolderPath!);
await ConfigureFolderWatchersAsync(MappedFolderPath);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add file or folder items to the widget, or move/copy them into a mapped folder.
    /// </summary>
    [RelayCommand]
    public async Task AddItemsAsync(IEnumerable<string> paths)
    {
        await ImportPathsAsync(paths);
    }

    public async Task ImportPathsAsync(
        IEnumerable<string> paths,
        bool? moveWhenMapped = null,
        bool useShellProgress = false)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return;
        }

        EnsureFolderBackedConfig();
        MappedFolderPath = Config.MappedFolderPath;
        bool shouldMove = moveWhenMapped ?? ShouldMoveManagedItems();
        var historyEntry = await _organizerService.OrganizeDropAsync(Config, Name, normalizedPaths, shouldMove, useShellProgress);

        if (shouldMove)
        {
            foreach (var sourcePath in historyEntry.Items.Select(item => item.SourcePath))
            {
                if (Path.GetDirectoryName(sourcePath)?.Equals(MappedFolderPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    RemoveItemByPath(sourcePath);
                }
            }
        }

        foreach (var destinationPath in historyEntry.Items.Select(item => item.DestinationPath))
        {
            RecordFileAddedAt(destinationPath, DateTimeOffset.Now);
            await UpsertFolderItemAsync(destinationPath);
        }
    }

    /// <summary>
    /// Toggle between icon and list views.
    /// </summary>
    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewMode.Icon ? ViewMode.List : ViewMode.Icon;
        Config.ViewMode = ViewMode;
        _settingsService.SaveDebounced();
    }

    /// <summary>
    /// Open an item using the default shell handler.
    /// </summary>
    [RelayCommand]
    public void OpenItem(WidgetItem item)
    {
        FileService.OpenItem(item);
    }

    public FileService.OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)
    {
        var result = FileService.OpenItem(item, ownerHwnd);
        if (result == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(item.Path);
        }

        return result;
    }

    /// <summary>
    /// Reveal an item in Explorer.
    /// </summary>
    [RelayCommand]
    public void ShowInExplorer(WidgetItem item)
    {
        FileService.ShowInExplorer(item);
    }

    public async Task<int> MoveItemBackToDesktopAsync(WidgetItem item, bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemBackToDesktopAsync(Config, Name, item, useShellProgress);
        if (historyEntry.Items.Any(entry => string.Equals(entry.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveItemByPath(item.Path);
            return 1;
        }

        return 0;
    }

    public async Task<int> MoveItemsBackToDesktopAsync(IEnumerable<WidgetItem> items, bool useShellProgress = false)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var historyEntry = await _organizerService.MoveItemsBackToDesktopAsync(
            Config,
            Name,
            targets.Select(item => item.Path),
            useShellProgress);

        var movedSourcePaths = historyEntry.Items
            .Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets.Where(item => movedSourcePaths.Contains(item.Path)))
        {
            RemoveItemByPath(item.Path);
        }

        return movedSourcePaths.Count;
    }

    public async Task RefreshFromConfigAsync()
    {
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshFromConfig",
            $"id={Config.Id} name={Name}");

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        EnsureFolderBackedConfig();

        MappedFolderPath = Config.MappedFolderPath;
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

await LoadFolderContentsAsync(MappedFolderPath!, clearIconCacheBeforeHydration: true);
await ConfigureFolderWatchersAsync(MappedFolderPath);
        UpdateDependentProperties();
    }

    /// <summary>
    /// Lightweight folder refresh that only reloads the contents without
    /// restarting the folder watcher.  Use this when the watcher is already
    /// running and you just need to re-read the current disk state — e.g.
    /// after a drag-out operation where the Shell may still be moving files.
    /// Uses the same semaphore as <see cref="OnFolderChanged"/> to avoid
    /// concurrent <see cref="LoadFolderContentsAsync"/> calls.
    /// </summary>
    public async Task RefreshFolderContentsAsync()
    {
        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            return;
        }

        await _folderRefreshGate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(MappedFolderPath))
            {
                return;
            }

            await LoadFolderContentsAsync(MappedFolderPath);
            UpdateDependentProperties();
        }
        finally
        {
            _folderRefreshGate.Release();
        }
    }

    public async Task UpdateMappedFolderPathAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(normalizedPath);

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        Config.FollowsDefaultStoragePath = false;
        Config.ManagedFolderName = null;
        Config.MappedFolderPath = normalizedPath;
        Config.Items.Clear();
        ResetAddedAtTracking();
        MappedFolderPath = normalizedPath;
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));

        if (App.Current?.WidgetManager is { } widgetManager)
        {
            widgetManager.SyncMappedWidgetShortcut(Config.Id);
        }

        _settingsService.UpdateWidget(Config);
await LoadFolderContentsAsync(normalizedPath);
await ConfigureFolderWatchersAsync(normalizedPath);
        UpdateDependentProperties();
    }

    public Task HandleItemsMovedOutAsync(IEnumerable<string> sourcePaths)
    {
        var normalizedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            return Task.CompletedTask;
        }

        foreach (var path in normalizedPaths)
        {
            RemoveItemByPath(path);
        }

        return Task.CompletedTask;
    }

    public async Task RenameItemAsync(WidgetItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string sanitizedName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        string sourcePath = Path.GetFullPath(item.Path);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.FolderUnknown"));
        }

        string extension = item.IsFolder ? string.Empty : Path.GetExtension(sourcePath);
        string destinationName = item.IsFolder
            ? sanitizedName
            : BuildRenameFileName(sanitizedName, extension);
        string destinationPath = Path.Combine(parentDirectory, destinationName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException(_localizationService.T("Widget.Validation.TargetExists"));
        }

        await _fileService.RelocateEntryAsync(sourcePath, destinationPath);
        TransferFileAddedAt(sourcePath, destinationPath);
        var refreshedItem = await _fileService.CreateWidgetItemAsync(
            destinationPath,
            hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
            showImageFilesAsIcons: _showImageFilesAsIcons,
            showFileExtensions: _showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon: false,
            loadFolderItemCount: false);
        ApplyRuntimeItemData(item, refreshedItem);
        StartItemHydration();

        int originalIndex = Items.IndexOf(item);
        if (originalIndex >= 0)
        {
            Items.RemoveAt(originalIndex);
            Items.Insert(GetSortedInsertIndex(item), item);
            NormalizeSortOrder();
        }
    }

    public async Task DeleteItemsAsync(IEnumerable<WidgetItem> items)
    {
        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var existingTargets = new List<WidgetItem>();
        foreach (var item in targets)
        {
            if (File.Exists(item.Path) || Directory.Exists(item.Path))
            {
                existingTargets.Add(item);
            }
            else
            {
                Items.Remove(item);
                RemoveFileAddedAt(item.Path);
            }
        }

        foreach (var item in existingTargets)
        {
            await _fileService.DeleteEntryAsync(item.Path, recycle: true);
        }

        foreach (var item in existingTargets)
        {
            Items.Remove(item);
            RemoveFileAddedAt(item.Path);
        }

        NormalizeSortOrder();
    }

    public void UpdateBounds(double x, double y, double width, double height, bool persist)
    {
        Config.X = x;
        Config.Y = y;
        Config.Width = width;
        Config.Height = height;

        if (persist)
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
    }

    /// <summary>
    /// Rename the widget.
    /// </summary>
    public async Task RenameAsync(string newName)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            Name = Config.Name;
            MappedFolderPath = Config.MappedFolderPath;
            OnPropertyChanged(nameof(FollowsDefaultStoragePath));
            return;
        }

        Name = newName;
        Config.Name = newName;
        Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(Config);
    }

    public void SetPositionLocked(bool value)
    {
        if (IsPositionLocked == value)
        {
            return;
        }

        IsPositionLocked = value;
        Config.IsPositionLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    public void SetSizeLocked(bool value)
    {
        if (IsSizeLocked == value)
        {
            return;
        }

        IsSizeLocked = value;
        Config.IsSizeLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    [RelayCommand]
    public void TogglePositionLock()
    {
        SetPositionLocked(!IsPositionLocked);
    }

    [RelayCommand]
    public void ToggleSizeLock()
    {
        SetSizeLocked(!IsSizeLocked);
    }
}
