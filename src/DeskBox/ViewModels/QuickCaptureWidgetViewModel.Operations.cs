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
        _selectedView =
            _restoredViewForInitialization is { } restoredView &&
            IsViewVisible(restoredView)
                ? restoredView
                : MapDefaultView(
                    _settingsService.Settings.QuickCaptureDefaultView);
        _restoredViewForInitialization = null;
        OnPropertyChanged(nameof(SelectedView));
        OnPropertyChanged(nameof(IsRecordsView));
        OnPropertyChanged(nameof(IsPinnedView));
        OnPropertyChanged(nameof(InputAreaVisibility));
        OnPropertyChanged(nameof(CreatedTimeVisibility));
        await RefreshFromDataAsync(data);
        IsInitialized = true;
    }

    public void RefreshAfterViewReady()
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowRefreshEnabled = true;
        bool hasHiddenChanges =
            Interlocked.Exchange(ref _windowRefreshDirty, 0) != 0;
        if (hasHiddenChanges || _cachedData is null)
        {
            RefreshVisibleItemsImmediately();
        }
    }

    public void SuspendWindowRefresh()
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowRefreshEnabled = false;
        Interlocked.Increment(ref _visibleItemsRefreshGeneration);
        _searchRefreshTimer.Stop();
        _viewSwitchRefreshTimer.Stop();
    }

    public Task RefreshItemsAsync()
    {
        return RefreshVisibleItemsAsync();
    }

    public void RestoreSelectedViewImmediately(QuickCaptureViewMode view)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!IsInitialized)
        {
            // Group switching primes the incoming member before InitializeAsync
            // so its first data projection never uses the global default tab.
            _restoredViewForInitialization = view;
        }

        QuickCaptureViewMode target = IsViewVisible(view)
            ? view
            : MapDefaultView(
                SettingsService.GetFirstVisibleQuickCaptureTab(
                    _settingsService.Settings));
        SelectedView = target;
        _viewSwitchRefreshTimer.Stop();
        if (_cachedData is { } data)
        {
            _ = RefreshFromDataAsync(data);
        }
        else
        {
            SetViewSwitchLoading(false);
        }
    }

    public async Task<QuickCaptureWriteResult> AddInputAsync()
    {
        string body = InputText;
        QuickCaptureWriteResult result = await AddTextAsync(body);
        InputText = string.Empty;
        return result;
    }

    public async Task<QuickCaptureWriteResult> AddTextAsync(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new QuickCaptureWriteResult(false, false, null);
        }

        bool addPinned = IsPinnedView;
        long sequenceBeforeWrite = CurrentRefreshSequence;
        QuickCaptureWriteResult result = await _quickCaptureService.AddDetailedItemWithResultAsync(
            null,
            body,
            QuickCaptureAppearancePreset.Default,
            ResolveEditorContentFormat(),
            pin: addPinned);
        // Records and Pinned stay on their current view, so refresh it
        // explicitly if Changed did not already trigger a live refresh.
        if (CurrentRefreshSequence == sequenceBeforeWrite)
        {
            RefreshVisibleItemsImmediately();
        }

        return result;
    }

    public async Task<QuickCaptureItem?> AddDetailedItemAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        QuickCaptureWriteResult result = await AddDetailedItemWithResultAsync(
            title,
            body,
            appearancePreset,
            contentFormat);
        return result.Item;
    }

    public async Task<QuickCaptureWriteResult> AddDetailedItemWithResultAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return new QuickCaptureWriteResult(false, false, null);
        }

        bool addPinned = IsPinnedView;
        long sequenceBeforeWrite = CurrentRefreshSequence;
        QuickCaptureWriteResult result = await _quickCaptureService.AddDetailedItemWithResultAsync(
            title,
            body,
            appearancePreset,
            contentFormat ?? ResolveEditorContentFormat(),
            pin: addPinned);
        if (CurrentRefreshSequence == sequenceBeforeWrite)
        {
            RefreshVisibleItemsImmediately();
        }

        return result;
    }

    public async Task<QuickCaptureItemViewModel?> AddImageFileAsync(string imagePath)
    {
        bool addPinned = IsPinnedView;
        QuickCaptureItem? item = await _quickCaptureService.AddImageFileItemAsync(
            imagePath,
            pin: addPinned);
        if (item is null)
        {
            return null;
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

        bool addPinned = IsPinnedView;
        QuickCaptureItem? created = regularPaths.Length > 0
            ? await _quickCaptureService.AddItemWithAttachmentsAsync(
                regularPaths,
                copyLinkedFiles,
                pin: addPinned)
            : await _quickCaptureService.AddItemWithAttachmentsAsync(
                managedPaths,
                copyToManagedStorage: true,
                pin: addPinned);
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

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, created.Id, StringComparison.Ordinal))
            ?? new QuickCaptureItemViewModel(
                created,
                _localizationService,
                TextSize,
                IconSize,
                SearchText,
                contentTextSize: ContentTextSize);
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
        string? formattedText = QuickCaptureClipboardCopyPolicy.ShouldCopyBitmap(item)
            ? null
            : QuickCaptureClipboardFormatter.FormatSingle(item, _localizationService);
        await WriteItemToClipboardWithRetryAsync(item, formattedText);
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
            DeskBoxClipboardWriteScope.MarkWrite(text: recordText);
            dataPackage.SetText(recordText);
        }
        else if (item.Type == QuickCaptureItemType.Image)
        {
            if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
            {
                throw new FileNotFoundException(
                    "The captured clipboard image is no longer available.",
                    item.ImagePath);
            }

            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.ImagePath);
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
        }
        else
        {
            string text = recordText ?? item.CopyText;
            DeskBoxClipboardWriteScope.MarkWrite(text: text);
            dataPackage.SetText(text);
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
            _ => QuickCaptureViewMode.Records
        };
    }

    public async Task EditItemAsync(QuickCaptureItemViewModel item, string body)
    {
        await _quickCaptureService.UpdateItemAsync(item.Id, body);
    }

    public Task<QuickCaptureWriteResult> EditItemWithResultAsync(
        QuickCaptureItemViewModel item,
        string body)
    {
        return _quickCaptureService.UpdateItemWithResultAsync(item.Id, body);
    }

    public Task<bool> EditItemDetailsAsync(
        QuickCaptureItemViewModel item,
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        return _quickCaptureService.UpdateItemDetailsAsync(
            item.Id,
            title,
            body,
            appearancePreset,
            contentFormat);
    }

    public Task<QuickCaptureWriteResult> EditItemDetailsWithResultAsync(
        QuickCaptureItemViewModel item,
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        return _quickCaptureService.UpdateItemDetailsWithResultAsync(
            item.Id,
            title,
            body,
            appearancePreset,
            contentFormat);
    }

    private TextContentFormat ResolveEditorContentFormat() => EditorContentFormat;

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
            QuickCaptureViewMode.Records => item.IsPinned,
            QuickCaptureViewMode.Pinned => !item.IsPinned,
            _ => false
        };
    }

    public bool CanApplyTabDrop(
        IReadOnlyList<QuickCaptureItemViewModel> items,
        QuickCaptureViewMode target)
    {
        return items.Count > 0 && items.Any(item => CanApplyTabDrop(item, target));
    }

    public async Task<bool> ApplyTabDropAsync(
        QuickCaptureItemViewModel item,
        QuickCaptureViewMode target)
    {
        if (!CanApplyTabDrop(item, target))
        {
            return false;
        }

        return await _quickCaptureService.SetPinnedAsync(
            item.Id,
            isPinned: target == QuickCaptureViewMode.Pinned);
    }

    public async Task<int> ApplyTabDropAsync(
        IReadOnlyList<QuickCaptureItemViewModel> items,
        QuickCaptureViewMode target)
    {
        string[] recordIds = items
            .Where(item => CanApplyTabDrop(item, target))
            .Select(item => item.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (recordIds.Length == 0)
        {
            return 0;
        }

        return await _quickCaptureService.SetPinnedAsync(
            recordIds,
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

    public Task<bool> TogglePinnedAsync(QuickCaptureItemViewModel item)
    {
        return _quickCaptureService.SetPinnedAsync(item.Id, !item.IsPinned);
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

    public Task<QuickCaptureDeletedItemSnapshot?> DeleteItemAsync(QuickCaptureItemViewModel item)
    {
        return _quickCaptureService.DeleteItemAsync(item.Id);
    }

    public Task<IReadOnlyList<QuickCaptureDeletedItemSnapshot>> DeleteItemsAsync(
        IEnumerable<string> itemIds,
        bool isRecent = false)
    {
        return _quickCaptureService.DeleteItemsAsync(itemIds, isRecent: false);
    }

    public Task<bool> RestoreDeletedItemAsync(QuickCaptureDeletedItemSnapshot? snapshot)
    {
        return _quickCaptureService.RestoreDeletedItemAsync(snapshot);
    }

    public Task ReleaseDeletedItemSnapshotsAsync(
        IEnumerable<QuickCaptureDeletedItemSnapshot>? snapshots)
    {
        return _quickCaptureService.ReleaseDeletedItemSnapshotsAsync(snapshots);
    }

    public Task CleanupUnusedImageCacheAsync()
    {
        return _quickCaptureService.CleanupUnusedImageCacheAsync();
    }

    public Task<string?> GetOrCreateImageThumbnailPathAsync(QuickCaptureItemViewModel item)
    {
        return _quickCaptureService.GetOrCreateImageThumbnailPathAsync(item.ImagePath);
    }



    public async Task ClearAsync()
    {
        await _quickCaptureService.ClearAsync();
        await WidgetFirstRunGuideFactory.EnsureQuickCaptureGuideAsync(
            _quickCaptureService,
            _localizationService);
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
