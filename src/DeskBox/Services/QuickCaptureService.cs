using System.Security.Cryptography;
using DeskBox.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public sealed record QuickCaptureImageCacheInfo(
    int TotalFileCount,
    long TotalBytes,
    int UnusedFileCount,
    long UnusedBytes);

public sealed record QuickCaptureImageCacheCleanupResult(
    int DeletedFileCount,
    long DeletedBytes);

public sealed record QuickCaptureDeletedItemSnapshot(
    QuickCaptureItem Item,
    bool IsRecent);

public sealed class QuickCaptureService
{
    public const int DefaultRecentLimit = 30;
    public const int MinRecentLimit = 10;
    public const int MaxRecentLimit = 100;
    private const uint ThumbnailMaxPixelSize = 180;
    private static readonly TimeSpan ExportCleanupAge = TimeSpan.FromDays(1);

    private readonly QuickCaptureStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _thumbnailTaskLock = new();
    private readonly Dictionary<string, Task<string?>> _thumbnailTasks = new(StringComparer.OrdinalIgnoreCase);
    private QuickCaptureStoreData? _data;

    public event Action? Changed;

    public QuickCaptureService(QuickCaptureStore? store = null)
    {
        _store = store ?? new QuickCaptureStore();
    }

    public async Task<QuickCaptureStoreData> GetDataAsync()
    {
        await EnsureLoadedAsync();
        return Clone(_data!);
    }

    public async Task<QuickCaptureItem> AddItemAsync(string body)
    {
        return await AddDetailedItemAsync(null, body, QuickCaptureAppearancePreset.Default);
    }

    public async Task<QuickCaptureItem> AddDetailedItemAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset)
    {
        string normalizedBody = NormalizeBody(body);
        string? normalizedTitle = NormalizeOptionalText(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedBody))
        {
            throw new ArgumentException("Quick Capture title and body cannot both be empty.", nameof(body));
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = normalizedBody,
                Title = normalizedTitle,
                Type = TryDetectUrl(normalizedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text,
                Url = url,
                AppearancePreset = NormalizeAppearancePreset(appearancePreset),
                SourceKind = QuickCaptureSourceKind.Manual,
                IsRecent = false,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data!.Items)
            {
                existing.SortOrder++;
            }

            _data.Items.Insert(0, item);
            await SaveCoreAsync();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateItemAsync(string itemId, string body)
    {
        string normalizedBody = NormalizeBody(body);
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(normalizedBody))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.Ordinal));
            if (item is null || item.IsDeleted)
            {
                return false;
            }

            item.Body = normalizedBody;
            item.Type = TryDetectUrl(normalizedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text;
            item.Url = url;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddRecentClipboardItemAsync(string body, int maxRecentItems)
    {
        string normalizedBody = NormalizeBody(body);
        if (string.IsNullOrWhiteSpace(normalizedBody) || ShouldIgnoreClipboardText(normalizedBody))
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            if (_data!.RecentItems.FirstOrDefault(item => !item.IsDeleted) is { } latest &&
                string.Equals(latest.Body, normalizedBody, StringComparison.Ordinal))
            {
                return null;
            }

            _data.RecentItems.RemoveAll(item => string.Equals(item.Body, normalizedBody, StringComparison.Ordinal));
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = normalizedBody,
                Type = TryDetectUrl(normalizedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text,
                Url = url,
                SourceKind = QuickCaptureSourceKind.Clipboard,
                IsRecent = true,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data.RecentItems)
            {
                existing.SortOrder++;
            }

            _data.RecentItems.Insert(0, item);
            TrimRecentItemsCore(NormalizeRecentLimit(maxRecentItems));
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddRecentClipboardImageAsync(byte[] imagePngBytes, int maxRecentItems)
    {
        if (imagePngBytes.Length == 0)
        {
            return null;
        }

        string contentHash = ComputeContentHash(imagePngBytes);
        string imagePath = await SaveImageAsync(imagePngBytes, contentHash);

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            if (_data!.RecentItems.FirstOrDefault(item => !item.IsDeleted) is { } latest &&
                string.Equals(latest.ContentHash, contentHash, StringComparison.Ordinal))
            {
                return null;
            }

            _data.RecentItems.RemoveAll(item => string.Equals(item.ContentHash, contentHash, StringComparison.Ordinal));
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = "Image",
                Type = QuickCaptureItemType.Image,
                ImagePath = imagePath,
                ContentHash = contentHash,
                Attachments = [CreateImageAttachment(imagePath, now)],
                SourceKind = QuickCaptureSourceKind.Clipboard,
                IsRecent = true,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data.RecentItems)
            {
                existing.SortOrder++;
            }

            _data.RecentItems.Insert(0, item);
            TrimRecentItemsCore(NormalizeRecentLimit(maxRecentItems));
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddImageFileItemAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !IsImageFile(imagePath))
        {
            return null;
        }

        string cachedImagePath = await SaveImageFileAsync(imagePath);
        string contentHash = Path.GetFileNameWithoutExtension(cachedImagePath);

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = "Image",
                Type = QuickCaptureItemType.Image,
                ImagePath = cachedImagePath,
                ContentHash = contentHash,
                Attachments = [CreateImageAttachment(cachedImagePath, now)],
                SourceKind = QuickCaptureSourceKind.Image,
                IsRecent = false,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data!.Items)
            {
                existing.SortOrder++;
            }

            _data.Items.Insert(0, item);
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> CreateImageExportFileAsync(QuickCaptureItem item, string? fileNamePrefix = null)
    {
        if (item.Type != QuickCaptureItemType.Image ||
            string.IsNullOrWhiteSpace(item.ImagePath) ||
            !File.Exists(item.ImagePath))
        {
            return null;
        }

        Directory.CreateDirectory(_store.ExportDirectory);
        CleanupOldExportFiles();

        string exportFileName = BuildImageExportFileName(
            fileNamePrefix,
            item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt,
            item.ImagePath);
        string exportPath = FileService.GetAvailablePath(Path.Combine(_store.ExportDirectory, exportFileName));
        await Task.Run(() => File.Copy(item.ImagePath, exportPath));
        return exportPath;
    }

    public async Task<QuickCaptureItem?> SaveRecentItemToRecordsAsync(string recentItemId, bool pin)
    {
        if (string.IsNullOrWhiteSpace(recentItemId))
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var recentItem = _data!.RecentItems.FirstOrDefault(entry =>
                string.Equals(entry.Id, recentItemId, StringComparison.Ordinal) &&
                !entry.IsDeleted);
            if (recentItem is null ||
                (string.IsNullOrWhiteSpace(recentItem.Body) &&
                 string.IsNullOrWhiteSpace(recentItem.ImagePath)))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = recentItem.Body,
                Title = recentItem.Title,
                Type = recentItem.Type,
                Url = recentItem.Url,
                ImagePath = recentItem.ImagePath,
                ContentHash = recentItem.ContentHash,
                Attachments = recentItem.Attachments.Select(attachment => attachment.Clone()).ToList(),
                SourceKind = QuickCaptureSourceKind.Clipboard,
                IsPinned = pin,
                IsRecent = false,
                SortOrder = 0,
                PinnedSortOrder = pin ? 0 : -1,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data.Items)
            {
                existing.SortOrder++;
                if (pin && existing.IsPinned)
                {
                    existing.PinnedSortOrder++;
                }
            }

            _data.Items.Insert(0, item);
            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetPinnedAsync(string itemId, bool isPinned)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.Ordinal));
            if (item is null || item.IsDeleted || item.IsPinned == isPinned)
            {
                return false;
            }

            item.IsPinned = isPinned;
            if (isPinned)
            {
                foreach (var existing in _data.Items.Where(entry => entry.IsPinned && entry != item))
                {
                    existing.PinnedSortOrder++;
                }

                item.PinnedSortOrder = 0;
            }
            else
            {
                item.PinnedSortOrder = -1;
            }

            NormalizePinnedSortOrders(_data.Items);
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> SetPinnedAsync(IEnumerable<string> itemIds, bool isPinned)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var targets = _data!.Items
                .Where(item => !item.IsDeleted && selectedIds.Contains(item.Id) && item.IsPinned != isPinned)
                .OrderBy(item => item.SortOrder)
                .ToList();
            if (targets.Count == 0)
            {
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var item in targets)
            {
                item.IsPinned = isPinned;
                item.PinnedSortOrder = isPinned ? 0 : -1;
                item.UpdatedAt = now;
            }

            if (isPinned)
            {
                var targetIdSet = targets.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                var pinnedOrder = targets
                    .Concat(_data.Items
                        .Where(item => item.IsPinned && !targetIdSet.Contains(item.Id))
                        .OrderBy(item => item.PinnedSortOrder))
                    .ToList();
                for (int index = 0; index < pinnedOrder.Count; index++)
                {
                    pinnedOrder[index].PinnedSortOrder = index;
                }
            }

            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return targets.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddItemWithAttachmentsAsync(
        IEnumerable<string> filePaths,
        bool copyToManagedStorage)
    {
        string[] paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return null;
        }

        string itemId = Guid.NewGuid().ToString("N");
        var attachments = new List<TodoAttachment>();
        foreach (string path in paths)
        {
            TodoAttachment? attachment = await AttachmentStorageService.ImportPathAsync(
                path,
                Path.Combine(_store.AttachmentDirectory, itemId),
                copyToManagedStorage);
            if (attachment is not null)
            {
                attachments.Add(attachment);
            }
        }

        if (attachments.Count == 0)
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var now = DateTimeOffset.UtcNow;
            string? primaryImagePath = attachments
                .FirstOrDefault(attachment => string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase))
                ?.FilePath;
            var item = new QuickCaptureItem
            {
                Id = itemId,
                Body = string.Join(", ", attachments.Select(attachment => attachment.DisplayName)),
                Type = primaryImagePath is null ? QuickCaptureItemType.Text : QuickCaptureItemType.Image,
                ImagePath = primaryImagePath,
                Attachments = attachments,
                SourceKind = QuickCaptureSourceKind.DragDrop,
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (QuickCaptureItem existing in _data!.Items)
            {
                existing.SortOrder++;
            }

            _data.Items.Insert(0, item);
            await SaveCoreAsync();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddAttachmentsAsync(
        string itemId,
        IEnumerable<string> filePaths,
        bool copyToManagedStorage)
    {
        string[] paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(itemId) || paths.Length == 0)
        {
            return null;
        }

        var imported = new List<TodoAttachment>();
        foreach (string path in paths)
        {
            TodoAttachment? attachment = await AttachmentStorageService.ImportPathAsync(
                path,
                Path.Combine(_store.AttachmentDirectory, itemId),
                copyToManagedStorage);
            if (attachment is not null)
            {
                imported.Add(attachment);
            }
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            QuickCaptureItem? item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                return null;
            }

            foreach (TodoAttachment attachment in imported)
            {
                if (!item.Attachments.Any(existing =>
                        string.Equals(existing.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    item.Attachments.Add(attachment);
                }
            }

            item.ImagePath ??= item.Attachments
                .FirstOrDefault(attachment => string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase))
                ?.FilePath;
            if (!string.IsNullOrWhiteSpace(item.ImagePath))
            {
                item.Type = QuickCaptureItemType.Image;
            }
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> DeleteAttachmentAsync(string itemId, string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(attachmentId))
        {
            return null;
        }

        TodoAttachment? removedAttachment = null;
        bool removalPersisted = false;
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            QuickCaptureItem? item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                return null;
            }

            removedAttachment = item.Attachments.FirstOrDefault(attachment =>
                string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal));
            if (removedAttachment is null)
            {
                return Clone(item);
            }

            item.Attachments.Remove(removedAttachment);
            if (string.Equals(item.ImagePath, removedAttachment.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                item.ImagePath = item.Attachments
                    .FirstOrDefault(attachment =>
                        string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase))
                    ?.FilePath;
                item.ContentHash = null;
                if (string.IsNullOrWhiteSpace(item.ImagePath))
                {
                    item.Type = TryDetectUrl(item.Body, out string? url)
                        ? QuickCaptureItemType.Link
                        : QuickCaptureItemType.Text;
                    item.Url = url;
                }
            }

            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            removalPersisted = true;
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
            if (removalPersisted &&
                removedAttachment is { IsManagedCopy: true } &&
                File.Exists(removedAttachment.FilePath) &&
                !IsPathInsideDirectory(removedAttachment.FilePath, _store.ImageDirectory))
            {
                try
                {
                    File.Delete(removedAttachment.FilePath);
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCaptureService] Failed to delete managed attachment: {ex.Message}");
                }
            }
        }
    }

    public async Task<bool> MovePinnedItemAsync(string itemId, int direction)
    {
        if (string.IsNullOrWhiteSpace(itemId) || direction == 0)
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            NormalizePinnedSortOrders(_data!.Items);

            var pinnedItems = _data.Items
                .Where(item => !item.IsDeleted && item.IsPinned)
                .OrderBy(item => item.PinnedSortOrder)
                .ThenBy(item => item.SortOrder)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList();

            int currentIndex = pinnedItems.FindIndex(item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (currentIndex < 0)
            {
                return false;
            }

            int targetIndex = currentIndex + (direction < 0 ? -1 : 1);
            if (targetIndex < 0 || targetIndex >= pinnedItems.Count)
            {
                return false;
            }

            (pinnedItems[currentIndex].PinnedSortOrder, pinnedItems[targetIndex].PinnedSortOrder) =
                (pinnedItems[targetIndex].PinnedSortOrder, pinnedItems[currentIndex].PinnedSortOrder);

            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureDeletedItemSnapshot?> DeleteItemAsync(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return null;
            }

            var deletedItem = Clone(item);
            _data.Items.Remove(item);
            NormalizeSortOrders(_data.Items);
            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return new QuickCaptureDeletedItemSnapshot(deletedItem, IsRecent: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QuickCaptureDeletedItemSnapshot>> DeleteItemsAsync(
        IEnumerable<string> itemIds,
        bool isRecent)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return [];
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            List<QuickCaptureItem> source = isRecent ? _data!.RecentItems : _data!.Items;
            var itemsToDelete = source
                .Where(item => selectedIds.Contains(item.Id))
                .ToList();
            if (itemsToDelete.Count == 0)
            {
                return [];
            }

            var snapshots = itemsToDelete
                .Select(item => new QuickCaptureDeletedItemSnapshot(Clone(item), isRecent))
                .ToList();
            foreach (var item in itemsToDelete)
            {
                source.Remove(item);
            }

            NormalizeSortOrders(source);
            if (!isRecent)
            {
                NormalizePinnedSortOrders(_data.Items);
            }

            await SaveCoreAsync();
            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureDeletedItemSnapshot?> DeleteRecentItemAsync(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.RecentItems.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.Ordinal));
            if (item is null)
            {
                return null;
            }

            var deletedItem = Clone(item);
            _data.RecentItems.Remove(item);
            NormalizeSortOrders(_data.RecentItems);
            await SaveCoreAsync();
            return new QuickCaptureDeletedItemSnapshot(deletedItem, IsRecent: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RestoreDeletedItemAsync(QuickCaptureDeletedItemSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var targetItems = snapshot.IsRecent ? _data!.RecentItems : _data!.Items;
            if (targetItems.Any(item => string.Equals(item.Id, snapshot.Item.Id, StringComparison.Ordinal)))
            {
                return false;
            }

            var item = Clone(snapshot.Item);
            int insertIndex = Math.Clamp(item.SortOrder, 0, targetItems.Count);
            targetItems.Insert(insertIndex, item);

            NormalizeSortOrders(targetItems);
            if (!snapshot.IsRecent)
            {
                NormalizePinnedSortOrders(_data.Items);
            }

            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            _data!.Items.Clear();
            _data.RecentItems.Clear();
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            TryDeleteDirectory(_store.AttachmentDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearRecentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            _data!.RecentItems.Clear();
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TrimRecentItemsAsync(int maxRecentItems)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            int before = _data!.RecentItems.Count;
            TrimRecentItemsCore(NormalizeRecentLimit(maxRecentItems));
            if (_data.RecentItems.Count != before)
            {
                await SaveCoreAsync();
                CleanupUnusedImageCacheCore();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCurrentViewAsync(QuickCaptureViewMode viewMode)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            if (_data!.CurrentView == viewMode)
            {
                return;
            }

            _data.CurrentView = viewMode;
            await SaveCoreAsync(notify: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureImageCacheInfo> GetImageCacheInfoAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            return GetImageCacheInfoCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureImageCacheCleanupResult> CleanupUnusedImageCacheAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            return CleanupUnusedImageCacheCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string?> GetOrCreateImageThumbnailPathAsync(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Task.FromResult<string?>(null);
        }

        string normalizedPath = Path.GetFullPath(imagePath);
        if (!File.Exists(normalizedPath) || !IsImageFile(normalizedPath))
        {
            return Task.FromResult<string?>(null);
        }

        string thumbnailPath = GetThumbnailPath(normalizedPath);
        if (File.Exists(thumbnailPath))
        {
            return Task.FromResult<string?>(thumbnailPath);
        }

        lock (_thumbnailTaskLock)
        {
            if (_thumbnailTasks.TryGetValue(normalizedPath, out var existingTask))
            {
                return existingTask;
            }

            var task = CreateImageThumbnailAndRemoveTaskAsync(normalizedPath);
            _thumbnailTasks[normalizedPath] = task;
            return task;
        }
    }

    public async Task<bool> MovePinnedItemToIndexAsync(string itemId, int targetIndex)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            NormalizePinnedSortOrders(_data!.Items);

            var pinnedItems = _data.Items
                .Where(item => !item.IsDeleted && item.IsPinned)
                .OrderBy(item => item.PinnedSortOrder)
                .ThenBy(item => item.SortOrder)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList();
            int currentIndex = pinnedItems.FindIndex(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (currentIndex < 0)
            {
                return false;
            }

            QuickCaptureItem movedItem = pinnedItems[currentIndex];
            pinnedItems.RemoveAt(currentIndex);
            pinnedItems.Insert(Math.Clamp(targetIndex, 0, pinnedItems.Count), movedItem);
            for (int index = 0; index < pinnedItems.Count; index++)
            {
                pinnedItems[index].PinnedSortOrder = index;
            }

            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> ReplaceItemImageAsync(string itemId, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(imagePath) ||
            !File.Exists(imagePath) ||
            !IsImageFile(imagePath))
        {
            return null;
        }

        string cachedImagePath = await SaveImageFileAsync(imagePath);
        string contentHash = Path.GetFileNameWithoutExtension(cachedImagePath);

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                CleanupUnusedImageCacheCore();
                return null;
            }

            TodoAttachment? primaryImageAttachment = item.Attachments.FirstOrDefault(attachment =>
                string.Equals(attachment.FilePath, item.ImagePath, StringComparison.OrdinalIgnoreCase));
            if (primaryImageAttachment is null)
            {
                item.Attachments.Insert(0, CreateImageAttachment(cachedImagePath, DateTimeOffset.UtcNow));
            }
            else
            {
                primaryImageAttachment.FilePath = cachedImagePath;
                primaryImageAttachment.DisplayName = Path.GetFileName(cachedImagePath);
                primaryImageAttachment.Type = "image";
                primaryImageAttachment.StorageMode = TodoAttachment.ManagedStorageMode;
            }

            item.Type = QuickCaptureItemType.Image;
            item.ImagePath = cachedImagePath;
            item.ContentHash = contentHash;
            item.SourceKind = QuickCaptureSourceKind.Image;
            item.Url = null;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> MoveItemAsync(string itemId, int targetIndex)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var items = _data!.Items
                .Where(item => !item.IsDeleted)
                .OrderBy(item => item.SortOrder)
                .ThenByDescending(item => item.UpdatedAt)
                .ToList();
            int currentIndex = items.FindIndex(item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (currentIndex < 0)
            {
                return false;
            }

            QuickCaptureItem movedItem = items[currentIndex];
            items.RemoveAt(currentIndex);
            items.Insert(Math.Clamp(targetIndex, 0, items.Count), movedItem);
            NormalizeSortOrders(items);
            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateItemDetailsAsync(
        string itemId,
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset)
    {
        string normalizedBody = NormalizeBody(body);
        string? normalizedTitle = NormalizeOptionalText(title);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                return false;
            }

            if (item.Type != QuickCaptureItemType.Image &&
                string.IsNullOrWhiteSpace(normalizedTitle) &&
                string.IsNullOrWhiteSpace(normalizedBody))
            {
                return false;
            }

            item.Title = normalizedTitle;
            item.Body = normalizedBody;
            if (item.Type != QuickCaptureItemType.Image)
            {
                item.Type = TryDetectUrl(normalizedBody, out string? url)
                    ? QuickCaptureItemType.Link
                    : QuickCaptureItemType.Text;
                item.Url = url;
            }

            item.AppearancePreset = NormalizeAppearancePreset(appearancePreset);
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> SetAppearanceAsync(
        IEnumerable<string> itemIds,
        QuickCaptureAppearancePreset appearancePreset)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        QuickCaptureAppearancePreset normalizedPreset = NormalizeAppearancePreset(appearancePreset);
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var targets = _data!.Items
                .Where(item => !item.IsDeleted &&
                               selectedIds.Contains(item.Id) &&
                               item.AppearancePreset != normalizedPreset)
                .ToList();
            if (targets.Count == 0)
            {
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var item in targets)
            {
                item.AppearancePreset = normalizedPreset;
                item.UpdatedAt = now;
            }

            await SaveCoreAsync();
            return targets.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> CreateImageThumbnailAndRemoveTaskAsync(string imagePath)
    {
        try
        {
            return await CreateImageThumbnailAsync(imagePath);
        }
        finally
        {
            lock (_thumbnailTaskLock)
            {
                _thumbnailTasks.Remove(imagePath);
            }
        }
    }

    private async Task EnsureLoadedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedCoreAsync()
    {
        _data ??= await _store.LoadAsync();
    }

    private async Task SaveCoreAsync(bool notify = true)
    {
        await _store.SaveAsync(_data!);
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    private QuickCaptureImageCacheInfo GetImageCacheInfoCore()
    {
        return GetImageCacheInfoCore(GetReferencedImagePathsCore());
    }

    private QuickCaptureImageCacheCleanupResult CleanupUnusedImageCacheCore()
    {
        return CleanupUnusedImageCacheCore(GetReferencedImagePathsCore());
    }

    private static string NormalizeBody(string? body)
    {
        return string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : body.Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static QuickCaptureAppearancePreset NormalizeAppearancePreset(QuickCaptureAppearancePreset value)
    {
        return Enum.IsDefined(value) ? value : QuickCaptureAppearancePreset.Default;
    }

    public void MarkClipboardTextWrittenByDeskBox(string? body)
    {
        DeskBoxClipboardWriteScope.MarkText(NormalizeBody(body));
    }

    public static int NormalizeRecentLimit(int value)
    {
        if (value < MinRecentLimit)
        {
            return DefaultRecentLimit;
        }

        return Math.Clamp(value, MinRecentLimit, MaxRecentLimit);
    }

    private bool ShouldIgnoreClipboardText(string body)
    {
        return DeskBoxClipboardWriteScope.ShouldIgnoreText(body);
    }

    private static bool TryDetectUrl(string body, out string? url)
    {
        url = null;
        if (Uri.TryCreate(body.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            url = uri.AbsoluteUri;
            return true;
        }

        return false;
    }

    private HashSet<string> GetReferencedImagePathsCore()
    {
        return _data!.Items
            .Concat(_data.RecentItems)
            .Where(item => item is not null &&
                           item.Type == QuickCaptureItemType.Image &&
                           !string.IsNullOrWhiteSpace(item.ImagePath))
            .Select(item => NormalizePath(item.ImagePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> GetReferencedThumbnailPathsCore(HashSet<string> referencedImagePaths)
    {
        return referencedImagePaths
            .Select(GetThumbnailPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private QuickCaptureImageCacheInfo GetImageCacheInfoCore(HashSet<string> referencedImagePaths)
    {
        if (!Directory.Exists(_store.ImageDirectory) && !Directory.Exists(_store.ThumbnailDirectory))
        {
            return new QuickCaptureImageCacheInfo(0, 0, 0, 0);
        }

        int totalFileCount = 0;
        long totalBytes = 0;
        int unusedFileCount = 0;
        long unusedBytes = 0;
        var referencedThumbnailPaths = GetReferencedThumbnailPathsCore(referencedImagePaths);

        foreach (var filePath in EnumerateCacheFiles(_store.ImageDirectory).Concat(EnumerateCacheFiles(_store.ThumbnailDirectory)))
        {
            var fileInfo = new FileInfo(filePath);
            totalFileCount++;
            totalBytes += fileInfo.Length;
            string normalizedFilePath = NormalizePath(filePath);
            bool isReferenced = referencedImagePaths.Contains(normalizedFilePath) ||
                referencedThumbnailPaths.Contains(normalizedFilePath);
            if (!isReferenced)
            {
                unusedFileCount++;
                unusedBytes += fileInfo.Length;
            }
        }

        return new QuickCaptureImageCacheInfo(totalFileCount, totalBytes, unusedFileCount, unusedBytes);
    }

    private QuickCaptureImageCacheCleanupResult CleanupUnusedImageCacheCore(HashSet<string> referencedImagePaths)
    {
        if (!Directory.Exists(_store.ImageDirectory) && !Directory.Exists(_store.ThumbnailDirectory))
        {
            return new QuickCaptureImageCacheCleanupResult(0, 0);
        }

        int deletedFileCount = 0;
        long deletedBytes = 0;
        var referencedThumbnailPaths = GetReferencedThumbnailPathsCore(referencedImagePaths);

        foreach (var filePath in EnumerateCacheFiles(_store.ImageDirectory).Concat(EnumerateCacheFiles(_store.ThumbnailDirectory)))
        {
            string normalizedFilePath = NormalizePath(filePath);
            if (referencedImagePaths.Contains(normalizedFilePath) ||
                referencedThumbnailPaths.Contains(normalizedFilePath))
            {
                continue;
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                deletedBytes += fileInfo.Exists ? fileInfo.Length : 0;
                File.Delete(filePath);
                deletedFileCount++;
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureService] Failed to delete image cache file: {ex}");
            }
        }

        return new QuickCaptureImageCacheCleanupResult(deletedFileCount, deletedBytes);
    }

    private static IEnumerable<string> EnumerateCacheFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToList()
            : [];
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static QuickCaptureStoreData Clone(QuickCaptureStoreData data)
    {
        return new QuickCaptureStoreData
        {
            Version = data.Version,
            CurrentView = data.CurrentView,
            Items = data.Items.Select(Clone).ToList(),
            RecentItems = data.RecentItems.Select(Clone).ToList()
        };
    }

    private static QuickCaptureItem Clone(QuickCaptureItem item)
    {
        return new QuickCaptureItem
        {
            Id = item.Id,
            Type = item.Type,
            Body = item.Body,
            Title = item.Title,
            Url = item.Url,
            ImagePath = item.ImagePath,
            ContentHash = item.ContentHash,
            IsPinned = item.IsPinned,
            IsRecent = item.IsRecent,
            IsDeleted = item.IsDeleted,
            AppearancePreset = item.AppearancePreset,
            SourceKind = item.SourceKind,
            Tags = item.Tags is null ? [] : [.. item.Tags],
            Attachments = item.Attachments?.Select(attachment => attachment.Clone()).ToList() ?? [],
            ArchivedAt = item.ArchivedAt,
            SortOrder = item.SortOrder,
            PinnedSortOrder = item.PinnedSortOrder,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private void TrimRecentItemsCore(int maxRecentItems)
    {
        _data!.RecentItems = _data.RecentItems
            .Where(item => !item.IsDeleted &&
                           (!string.IsNullOrWhiteSpace(item.Body) ||
                            (item.Type == QuickCaptureItemType.Image && !string.IsNullOrWhiteSpace(item.ImagePath))))
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(maxRecentItems)
            .ToList();

        NormalizeSortOrders(_data.RecentItems);
    }

    private async Task<string> SaveImageAsync(byte[] imagePngBytes, string contentHash)
    {
        Directory.CreateDirectory(_store.ImageDirectory);
        string imagePath = Path.Combine(_store.ImageDirectory, $"{contentHash}.png");
        if (!File.Exists(imagePath))
        {
            await File.WriteAllBytesAsync(imagePath, imagePngBytes);
        }

        _ = CreateImageThumbnailAsync(imagePath);
        return imagePath;
    }

    private static TodoAttachment CreateImageAttachment(string imagePath, DateTimeOffset addedAt)
    {
        return new TodoAttachment
        {
            FilePath = imagePath,
            DisplayName = Path.GetFileName(imagePath),
            Type = "image",
            StorageMode = TodoAttachment.ManagedStorageMode,
            AddedAt = addedAt
        };
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureService] Failed to clear attachment directory: {ex.Message}");
        }
    }

    private async Task<string> SaveImageFileAsync(string sourceImagePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(sourceImagePath);
        string contentHash = ComputeContentHash(bytes);
        string extension = NormalizeImageExtension(sourceImagePath);
        Directory.CreateDirectory(_store.ImageDirectory);
        string imagePath = Path.Combine(_store.ImageDirectory, $"{contentHash}{extension}");
        if (!File.Exists(imagePath))
        {
            await File.WriteAllBytesAsync(imagePath, bytes);
        }

        _ = CreateImageThumbnailAsync(imagePath);
        return imagePath;
    }

    private async Task<string?> CreateImageThumbnailAsync(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) ||
            !File.Exists(imagePath) ||
            !IsImageFile(imagePath))
        {
            return null;
        }

        string thumbnailPath = GetThumbnailPath(imagePath);
        if (File.Exists(thumbnailPath))
        {
            return thumbnailPath;
        }

        try
        {
            Directory.CreateDirectory(_store.ThumbnailDirectory);
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using IRandomAccessStream inputStream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            if (decoder.PixelWidth >= decoder.PixelHeight)
            {
                transform.ScaledWidth = Math.Min(decoder.PixelWidth, ThumbnailMaxPixelSize);
                transform.ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * (transform.ScaledWidth / (double)decoder.PixelWidth)));
            }
            else
            {
                transform.ScaledHeight = Math.Min(decoder.PixelHeight, ThumbnailMaxPixelSize);
                transform.ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * (transform.ScaledHeight / (double)decoder.PixelHeight)));
            }

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            string tempPath = $"{thumbnailPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var outputFileStream = File.Open(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using IRandomAccessStream outputStream = outputFileStream.AsRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                }

                if (File.Exists(thumbnailPath))
                {
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, thumbnailPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            return thumbnailPath;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureService] Failed to create thumbnail for '{imagePath}': {ex}");
            return File.Exists(thumbnailPath) ? thumbnailPath : null;
        }
    }

    private string GetThumbnailPath(string imagePath)
    {
        string hash = !string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(imagePath))
            ? Path.GetFileNameWithoutExtension(imagePath)
            : ComputeContentHash(File.ReadAllBytes(imagePath));
        return Path.Combine(_store.ThumbnailDirectory, $"{hash}.png");
    }

    private static string ComputeContentHash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string BuildImageExportFileName(
        string? fileNamePrefix,
        DateTimeOffset timestamp,
        string sourceImagePath)
    {
        string prefix = FileService.SanitizeFileSystemName(fileNamePrefix);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "Capture";
        }

        string extension = NormalizeImageExtension(sourceImagePath);
        return $"{prefix} {timestamp.ToLocalTime():yyyy-MM-dd HH-mm-ss}{extension}";
    }

    private void CleanupOldExportFiles()
    {
        if (!Directory.Exists(_store.ExportDirectory))
        {
            return;
        }

        DateTime cutoffUtc = DateTime.UtcNow - ExportCleanupAge;
        foreach (string filePath in Directory.EnumerateFiles(_store.ExportDirectory, "*", SearchOption.TopDirectoryOnly).ToList())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoffUtc)
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureService] Failed to clean image export file: {ex}");
            }
        }
    }

    private static bool IsImageFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageExtension(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return IsImageFile(path)
            ? extension.ToLowerInvariant()
            : ".png";
    }

    private static void NormalizeSortOrders(List<QuickCaptureItem> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            items[index].SortOrder = index;
        }
    }

    private static void NormalizePinnedSortOrders(List<QuickCaptureItem> items)
    {
        var pinnedItems = items
            .Where(item => !item.IsDeleted && item.IsPinned)
            .OrderBy(item => item.PinnedSortOrder < 0 ? int.MaxValue : item.PinnedSortOrder)
            .ThenBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        for (int index = 0; index < pinnedItems.Count; index++)
        {
            pinnedItems[index].PinnedSortOrder = index;
        }

        foreach (var item in items.Where(item => !item.IsPinned))
        {
            item.PinnedSortOrder = -1;
        }
    }
}
