using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.ViewModels;

public sealed partial class QuickCaptureWidgetViewModel
{
    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        var data = await _quickCaptureService.GetDataAsync();
        if (_isDisposed)
        {
            return;
        }

        _cachedData = data;
        ApplyTabVisibilityFromSettings();
        _selectedView = MapDefaultView(_settingsService.Settings.QuickCaptureDefaultView);
        OnPropertyChanged(nameof(SelectedView));
        OnPropertyChanged(nameof(IsRecordsView));
        OnPropertyChanged(nameof(IsPinnedView));
        OnPropertyChanged(nameof(IsRecentView));
        OnPropertyChanged(nameof(InputAreaVisibility));
        OnPropertyChanged(nameof(RecentCaptureStatusVisibility));
        OnPropertyChanged(nameof(RecentCaptureActionVisibility));
        OnPropertyChanged(nameof(CreatedTimeVisibility));
        await RefreshFromDataAsync(data);
    }

    public void RefreshAfterViewReady()
    {
        if (_isDisposed)
        {
            return;
        }

        RefreshVisibleItemsFromCacheOrService();
    }

    public Task RefreshItemsAsync()
    {
        return RefreshVisibleItemsAsync();
    }

    public async Task AddInputAsync()
    {
        string body = InputText;
        await AddTextAsync(body);
        InputText = string.Empty;
    }

    public async Task AddTextAsync(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        await _quickCaptureService.AddItemAsync(body);
        if (SelectedView is QuickCaptureViewMode.Pinned or QuickCaptureViewMode.Recent)
        {
            SelectedView = QuickCaptureViewMode.Records;
        }
        else
        {
            // Already on Records view — the view-switch refresh won't fire,
            // so trigger an explicit refresh to ensure the new item appears.
            RefreshVisibleItemsImmediately();
        }
    }

    public async Task<QuickCaptureItem?> AddDetailedItemAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        QuickCaptureItem item = await _quickCaptureService.AddDetailedItemAsync(title, body, appearancePreset);
        if (SelectedView != QuickCaptureViewMode.Records)
        {
            SelectedView = QuickCaptureViewMode.Records;
        }
        else
        {
            RefreshVisibleItemsImmediately();
        }

        return item;
    }

    public async Task<QuickCaptureItemViewModel?> AddImageFileAsync(string imagePath)
    {
        QuickCaptureItem? item = await _quickCaptureService.AddImageFileItemAsync(imagePath);
        if (item is null)
        {
            return null;
        }

        if (SelectedView != QuickCaptureViewMode.Records)
        {
            SelectedView = QuickCaptureViewMode.Records;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
    }

    public async Task<QuickCaptureItemViewModel?> AddItemWithAttachmentsAsync(
        IReadOnlyList<DroppedFilePath> droppedFiles)
    {
        if (droppedFiles.Count == 0)
        {
            return null;
        }

        bool copyLinkedFiles = SettingsService.NormalizeAttachmentStorageMode(
            _settingsService.Settings.AttachmentStorageMode) == SettingsService.AttachmentStorageModeCopy;
        string[] regularPaths = droppedFiles
            .Where(file => !file.ForceManagedCopy)
            .Select(file => file.Path)
            .ToArray();
        string[] managedPaths = droppedFiles
            .Where(file => file.ForceManagedCopy)
            .Select(file => file.Path)
            .ToArray();

        QuickCaptureItem? created = regularPaths.Length > 0
            ? await _quickCaptureService.AddItemWithAttachmentsAsync(regularPaths, copyLinkedFiles)
            : await _quickCaptureService.AddItemWithAttachmentsAsync(managedPaths, copyToManagedStorage: true);
        if (created is null)
        {
            return null;
        }

        if (regularPaths.Length > 0 && managedPaths.Length > 0)
        {
            created = await _quickCaptureService.AddAttachmentsAsync(
                created.Id,
                managedPaths,
                copyToManagedStorage: true) ?? created;
        }

        if (SelectedView != QuickCaptureViewMode.Records)
        {
            SelectedView = QuickCaptureViewMode.Records;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, created.Id, StringComparison.Ordinal));
    }

    public async Task<QuickCaptureItemViewModel?> AddAttachmentsAsync(
        QuickCaptureItemViewModel item,
        IReadOnlyList<DroppedFilePath> droppedFiles)
    {
        if (droppedFiles.Count == 0)
        {
            return item;
        }

        bool copyLinkedFiles = SettingsService.NormalizeAttachmentStorageMode(
            _settingsService.Settings.AttachmentStorageMode) == SettingsService.AttachmentStorageModeCopy;
        QuickCaptureItem? updated = null;
        string[] regularPaths = droppedFiles
            .Where(file => !file.ForceManagedCopy)
            .Select(file => file.Path)
            .ToArray();
        string[] managedPaths = droppedFiles
            .Where(file => file.ForceManagedCopy)
            .Select(file => file.Path)
            .ToArray();
        if (regularPaths.Length > 0)
        {
            updated = await _quickCaptureService.AddAttachmentsAsync(item.Id, regularPaths, copyLinkedFiles);
        }
        if (managedPaths.Length > 0)
        {
            updated = await _quickCaptureService.AddAttachmentsAsync(
                item.Id,
                managedPaths,
                copyToManagedStorage: true) ?? updated;
        }

        if (updated is null)
        {
            return null;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
    }

    public async Task<QuickCaptureItemViewModel?> DeleteAttachmentAsync(
        QuickCaptureItemViewModel item,
        string attachmentId)
    {
        QuickCaptureItem? updated = await _quickCaptureService.DeleteAttachmentAsync(item.Id, attachmentId);
        if (updated is null)
        {
            return null;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
    }

    public async Task<QuickCaptureItemViewModel?> ReplaceItemImageAsync(
        QuickCaptureItemViewModel item,
        string imagePath)
    {
        QuickCaptureItem? updated = await _quickCaptureService.ReplaceItemImageAsync(item.Id, imagePath);
        if (updated is null)
        {
            return null;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
    }

    public Task<string?> CreateImageExportFileAsync(QuickCaptureItemViewModel item, string fileNamePrefix)
    {
        return _quickCaptureService.CreateImageExportFileAsync(item.ToModel(), fileNamePrefix);
    }

    public async Task CopyItemAsync(QuickCaptureItemViewModel item)
    {
        string formattedText = QuickCaptureClipboardFormatter.FormatSingle(item, _localizationService);
        await WriteItemToClipboardWithRetryAsync(item, formattedText);
        if (!string.IsNullOrWhiteSpace(formattedText))
        {
            _quickCaptureService.MarkClipboardTextWrittenByDeskBox(formattedText);
        }
    }

    public Task CopyImageAsync(QuickCaptureItemViewModel item)
    {
        return WriteItemToClipboardWithRetryAsync(item, recordText: null);
    }

    private async Task WriteItemToClipboardWithRetryAsync(
        QuickCaptureItemViewModel item,
        string? recordText)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= s_clipboardRetryDelaysMs.Length; attempt++)
        {
            try
            {
                await WriteItemToClipboardOnceAsync(item, recordText);
                return;
            }
            catch (COMException ex) when (IsRetryableClipboardException(ex) && attempt < s_clipboardRetryDelaysMs.Length)
            {
                lastException = ex;
                await Task.Delay(s_clipboardRetryDelaysMs[attempt]);
            }
        }

        if (lastException is not null)
        {
            throw lastException;
        }
    }

    private static async Task WriteItemToClipboardOnceAsync(
        QuickCaptureItemViewModel item,
        string? recordText)
    {
        var dataPackage = new DataPackage();
        if (!string.IsNullOrWhiteSpace(recordText))
        {
            dataPackage.SetText(recordText);
            DeskBoxClipboardWriteScope.MarkWrite(text: recordText);
        }
        else if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.ImagePath);
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            DeskBoxClipboardWriteScope.MarkWrite(
                hasImage: true,
                paths: [item.ImagePath]);
        }
        else
        {
            string text = recordText ?? item.CopyText;
            dataPackage.SetText(text);
            DeskBoxClipboardWriteScope.MarkWrite(text: text);
        }

        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
    }

    private static bool IsRetryableClipboardException(COMException ex)
    {
        return ex.HResult == ClipboardCannotOpenHResult;
    }

    private static QuickCaptureViewMode MapDefaultView(string? defaultView)
    {
        return defaultView switch
        {
            SettingsService.QuickCaptureDefaultViewPinned => QuickCaptureViewMode.Pinned,
            SettingsService.QuickCaptureDefaultViewRecent => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };
    }

    public async Task EditItemAsync(QuickCaptureItemViewModel item, string body)
    {
        await _quickCaptureService.UpdateItemAsync(item.Id, body);
    }

    public Task<bool> EditItemDetailsAsync(
        QuickCaptureItemViewModel item,
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset)
    {
        return _quickCaptureService.UpdateItemDetailsAsync(item.Id, title, body, appearancePreset);
    }

    public Task<bool> SetPinnedAsync(string itemId, bool isPinned)
    {
        return _quickCaptureService.SetPinnedAsync(itemId, isPinned);
    }

    public Task<int> SetPinnedAsync(IEnumerable<string> itemIds, bool isPinned)
    {
        return _quickCaptureService.SetPinnedAsync(itemIds, isPinned);
    }

    public bool CanApplyTabDrop(QuickCaptureItemViewModel item, QuickCaptureViewMode target)
    {
        return target switch
        {
            QuickCaptureViewMode.Records => item.IsRecent || item.IsPinned,
            QuickCaptureViewMode.Pinned => item.IsRecent || !item.IsPinned,
            _ => false
        };
    }

    public async Task<bool> ApplyTabDropAsync(
        QuickCaptureItemViewModel item,
        QuickCaptureViewMode target)
    {
        if (!CanApplyTabDrop(item, target))
        {
            return false;
        }

        if (item.IsRecent)
        {
            return await _quickCaptureService.SaveRecentItemToRecordsAsync(
                item.Id,
                pin: target == QuickCaptureViewMode.Pinned) is not null;
        }

        return await _quickCaptureService.SetPinnedAsync(
            item.Id,
            isPinned: target == QuickCaptureViewMode.Pinned);
    }

    public Task<bool> SetAppearanceAsync(
        QuickCaptureItemViewModel item,
        QuickCaptureAppearancePreset appearancePreset)
    {
        return _quickCaptureService.UpdateItemDetailsAsync(item.Id, item.Title, item.Body, appearancePreset);
    }

    public Task<int> SetAppearanceAsync(
        IEnumerable<string> itemIds,
        QuickCaptureAppearancePreset appearancePreset)
    {
        return _quickCaptureService.SetAppearanceAsync(itemIds, appearancePreset);
    }

    public async Task TogglePinnedAsync(QuickCaptureItemViewModel item)
    {
        await _quickCaptureService.SetPinnedAsync(item.Id, !item.IsPinned);
    }

    public async Task MovePinnedItemAsync(QuickCaptureItemViewModel item, int direction)
    {
        if (SelectedView != QuickCaptureViewMode.Pinned || HasSearchText)
        {
            return;
        }

        await _quickCaptureService.MovePinnedItemAsync(item.Id, direction);
    }

    public Task<bool> MovePinnedItemToIndexAsync(QuickCaptureItemViewModel item, int targetIndex)
    {
        return SelectedView == QuickCaptureViewMode.Pinned && !HasSearchText
            ? _quickCaptureService.MovePinnedItemToIndexAsync(item.Id, targetIndex)
            : Task.FromResult(false);
    }

    public Task<bool> MoveItemAsync(QuickCaptureItemViewModel item, int targetIndex)
    {
        return SelectedView == QuickCaptureViewMode.Records && !HasSearchText
            ? _quickCaptureService.MoveItemAsync(item.Id, targetIndex)
            : Task.FromResult(false);
    }

    public async Task<QuickCaptureDeletedItemSnapshot?> DeleteItemAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return await _quickCaptureService.DeleteRecentItemAsync(item.Id);
        }

        return await _quickCaptureService.DeleteItemAsync(item.Id);
    }

    public Task<IReadOnlyList<QuickCaptureDeletedItemSnapshot>> DeleteItemsAsync(
        IEnumerable<string> itemIds,
        bool isRecent)
    {
        return _quickCaptureService.DeleteItemsAsync(itemIds, isRecent);
    }

    public Task<bool> RestoreDeletedItemAsync(QuickCaptureDeletedItemSnapshot? snapshot)
    {
        return _quickCaptureService.RestoreDeletedItemAsync(snapshot);
    }

    public Task CleanupUnusedImageCacheAsync()
    {
        return _quickCaptureService.CleanupUnusedImageCacheAsync();
    }

    public Task<string?> GetOrCreateImageThumbnailPathAsync(QuickCaptureItemViewModel item)
    {
        return _quickCaptureService.GetOrCreateImageThumbnailPathAsync(item.ImagePath);
    }

    public async Task SaveRecentItemAsync(QuickCaptureItemViewModel item)
    {
        if (!item.IsRecent)
        {
            return;
        }

        await _quickCaptureService.SaveRecentItemToRecordsAsync(item.Id, pin: false);
    }

    public async Task PinRecentItemAsync(QuickCaptureItemViewModel item)
    {
        if (!item.IsRecent)
        {
            return;
        }

        await _quickCaptureService.SaveRecentItemToRecordsAsync(item.Id, pin: true);
    }

    public async Task ClearAsync()
    {
        await _quickCaptureService.ClearAsync();
    }

    public async Task ClearRecentAsync()
    {
        await _quickCaptureService.ClearRecentAsync();
    }

    public async Task RenameAsync(string newName)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            Name = Config.Name;
            return;
        }

        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        Config.Name = newName;
        Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(Config);
        OnPropertyChanged(nameof(DisplayName));
    }
}
