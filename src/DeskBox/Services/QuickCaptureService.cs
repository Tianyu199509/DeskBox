using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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

public sealed record QuickCaptureWriteResult(
    bool Saved,
    bool WasTruncated,
    QuickCaptureItem? Item);

public sealed record QuickCaptureSearchProjection(
    string Id,
    string? Title,
    string Text,
    string? Url,
    QuickCaptureItemType Type,
    bool IsPinned,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TodoAttachment> Attachments);

public sealed partial class QuickCaptureService
{
    public const int DefaultRecentLimit = 30;
    public const int MinRecentLimit = 10;
    public const int MaxRecentLimit = 100;
    public const int MaxTombstones = 500;
    public const int MaxItemBodyCharacters = MarkdownDocumentService.MaxCharacters;
    private const int DefaultSearchProjectionMaxChars = 2048;
    private const int MaxColdStreamFileBytes = 32 * 1024 * 1024;
    private const int MaxPendingPrunedTombstoneIds = 4096;
    private const uint ThumbnailMaxPixelSize = 180;
    private static readonly TimeSpan ExportCleanupAge = TimeSpan.FromDays(1);
    internal static readonly TimeSpan UndoAttachmentRetentionWindow = TimeSpan.FromSeconds(30);

    private sealed record UndoAttachmentLease(
        IReadOnlyList<string> ManagedPaths,
        DateTimeOffset ExpiresAtUtc);

    private readonly QuickCaptureStore _store;
    private readonly Func<string, Task<bool>>? _recycleAttachmentFileAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _thumbnailTaskLock = new();
    private readonly Dictionary<string, Task<string?>> _thumbnailTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UndoAttachmentLease> _undoProtectedAttachmentsByItemId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingPrunedTombstoneIds =
        new(StringComparer.OrdinalIgnoreCase);
    private QuickCaptureStoreData? _data;
    private bool _pendingStartupAttachmentSweep;

    public event Action? Changed;

    public QuickCaptureService(
        QuickCaptureStore? store = null,
        Func<string, Task<bool>>? recycleAttachmentFileAsync = null)
    {
        _store = store ?? new QuickCaptureStore();
        _recycleAttachmentFileAsync = recycleAttachmentFileAsync;
    }

    public async Task<QuickCaptureStoreData> GetDataAsync()
    {
        await EnsureLoadedAsync();
        return Clone(_data!);
    }

    public async Task<(int ActiveCount, int RecentCount)> GetCountsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_data is not null)
            {
                return (
                    _data.Items.Count(item => !item.IsDeleted),
                    _data.RecentItems.Count(item => !item.IsDeleted));
            }

            if (TryResolveColdStoreJsonPath() is { } coldJsonPath)
            {
                (int ActiveCount, int RecentCount)? streamedCounts =
                    await TryStreamCountsFromFileAsync(coldJsonPath, cancellationToken).ConfigureAwait(false);
                if (streamedCounts.HasValue)
                {
                    return streamedCounts.Value;
                }
            }

            QuickCaptureStoreData coldSnapshot = await _store
                .LoadAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return (
                coldSnapshot.Items.Count(item => !item.IsDeleted),
                coldSnapshot.RecentItems.Count(item => !item.IsDeleted));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QuickCaptureSearchProjection>> GetSearchIndexProjectionsAsync(
        int maxBodyChars = DefaultSearchProjectionMaxChars,
        CancellationToken cancellationToken = default)
    {
        int effectiveMaxChars = maxBodyChars > 0 ? maxBodyChars : DefaultSearchProjectionMaxChars;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_data is not null)
            {
                return ProjectSearchItems(_data.Items, effectiveMaxChars, cancellationToken);
            }

            if (TryResolveColdStoreJsonPath() is { } coldJsonPath)
            {
                IReadOnlyList<QuickCaptureSearchProjection>? streamed =
                    await TryStreamSearchProjectionsFromFileAsync(
                        coldJsonPath,
                        effectiveMaxChars,
                        cancellationToken).ConfigureAwait(false);
                if (streamed is not null)
                {
                    return streamed;
                }
            }

            QuickCaptureStoreData coldSnapshot = await _store
                .LoadAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return ProjectSearchItems(coldSnapshot.Items, effectiveMaxChars, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem> AddItemAsync(string body)
    {
        return await AddDetailedItemAsync(null, body, QuickCaptureAppearancePreset.Default);
    }

    public async Task<QuickCaptureItem> AddDetailedItemAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat contentFormat = TextContentFormat.PlainText,
        bool pin = false)
    {
        QuickCaptureWriteResult result = await AddDetailedItemWithResultAsync(
            title,
            body,
            appearancePreset,
            contentFormat,
            pin);
        return result.Item!;
    }

    public async Task<QuickCaptureWriteResult> AddDetailedItemWithResultAsync(
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat contentFormat = TextContentFormat.PlainText,
        bool pin = false)
    {
        contentFormat = NormalizeContentFormat(contentFormat);
        string normalizedBody = NormalizeBody(body, contentFormat);
        string? normalizedTitle = NormalizeOptionalText(title);
        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedBody))
        {
            throw new ArgumentException("Quick Capture title and body cannot both be empty.", nameof(body));
        }

        string itemId = Guid.NewGuid().ToString("N");
        var attachments = new List<TodoAttachment>();
        normalizedBody = await ExtractInlineMarkdownImagesAsync(
            itemId,
            normalizedBody,
            contentFormat,
            attachments);
        normalizedBody = TruncateBody(normalizedBody, out bool wasTruncated);

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Id = itemId,
                Body = normalizedBody,
                ContentFormat = contentFormat,
                Title = normalizedTitle,
                Type = TryDetectUrl(normalizedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text,
                Url = url,
                Attachments = attachments,
                AppearancePreset = NormalizeAppearancePreset(appearancePreset),
                SourceKind = QuickCaptureSourceKind.Manual,
                Tags = QuickCaptureTagParser.MergeInlineTags(null, null, null, normalizedTitle, normalizedBody),
                IsPinned = pin,
                IsRecent = false,
                SortOrder = 0,
                PinnedSortOrder = pin ? 0 : -1,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data!.Items)
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
            return new QuickCaptureWriteResult(true, wasTruncated, Clone(item));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdateItemAsync(string itemId, string body)
    {
        QuickCaptureWriteResult result = await UpdateItemWithResultAsync(itemId, body);
        return result.Saved;
    }

    public async Task<QuickCaptureWriteResult> UpdateItemWithResultAsync(string itemId, string body)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return new QuickCaptureWriteResult(false, false, null);
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.Ordinal));
            if (item is null || item.IsDeleted)
            {
                return new QuickCaptureWriteResult(false, false, null);
            }

            string previousBody = item.Body;
            string normalizedBody = NormalizeBody(body, item.ContentFormat);
            if (string.IsNullOrWhiteSpace(normalizedBody))
            {
                return new QuickCaptureWriteResult(false, false, null);
            }

            normalizedBody = await ExtractInlineMarkdownImagesAsync(
                item.Id,
                normalizedBody,
                item.ContentFormat,
                item.Attachments);
            normalizedBody = TruncateBody(normalizedBody, out bool wasTruncated);
            item.Body = normalizedBody;
            item.Tags = QuickCaptureTagParser.MergeInlineTags(item.Tags, item.Title, previousBody, item.Title, normalizedBody);
            item.Type = TryDetectUrl(normalizedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text;
            item.Url = url;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveCoreAsync();
            return new QuickCaptureWriteResult(true, wasTruncated, Clone(item));
        }
        finally
        {
            _gate.Release();
        }
    }



    public async Task<QuickCaptureItem?> AddImageFileItemAsync(
        string imagePath,
        bool pin = false)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !IsImageFile(imagePath))
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            string cachedImagePath = await SaveImageFileAsync(imagePath);
            string contentHash = Path.GetFileNameWithoutExtension(cachedImagePath);
            var now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = "Image",
                ContentFormat = TextContentFormat.PlainText,
                Type = QuickCaptureItemType.Image,
                ImagePath = cachedImagePath,
                ContentHash = contentHash,
                Attachments = [CreateImageAttachment(cachedImagePath, now)],
                SourceKind = QuickCaptureSourceKind.Image,
                IsPinned = pin,
                IsRecent = false,
                SortOrder = 0,
                PinnedSortOrder = pin ? 0 : -1,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (var existing in _data!.Items)
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
        bool copyToManagedStorage,
        bool pin = false)
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
                ContentFormat = TextContentFormat.PlainText,
                Type = primaryImagePath is null ? QuickCaptureItemType.Text : QuickCaptureItemType.Image,
                ImagePath = primaryImagePath,
                Attachments = attachments,
                SourceKind = QuickCaptureSourceKind.DragDrop,
                IsPinned = pin,
                SortOrder = 0,
                PinnedSortOrder = pin ? 0 : -1,
                CreatedAt = now,
                UpdatedAt = now
            };

            foreach (QuickCaptureItem existing in _data!.Items)
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
                !IsPathInsideDirectory(removedAttachment.FilePath, _store.ImageDirectory))
            {
                // Files in the shared image cache stay owned by the refcount
                // GC above; anything else is deleted only when provably
                // inside the managed attachment tree, through the recycle
                // kernel (containment gate inside TryDeleteManagedCopyAsync).
                await AttachmentStorageService.TryDeleteManagedCopyAsync(
                    removedAttachment.FilePath,
                    _store.AttachmentDirectory,
                    _recycleAttachmentFileAsync);
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
            RegisterUndoAttachmentProtectionCore(deletedItem);
            // Tombstone, not physical removal: a merge restore treats an
            // absent id as "unknown to this device" and would resurrect the
            // item. The stub keeps the delete winning by LWW while stripping
            // the payload from the store file — undo restores from the
            // in-memory snapshot, not from the store.
            int itemIndex = _data.Items.IndexOf(item);
            _data.Items[itemIndex] = CreateTombstoneStub(item, DateTimeOffset.UtcNow, isRecent: false);
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
            foreach (QuickCaptureDeletedItemSnapshot snapshot in snapshots)
            {
                RegisterUndoAttachmentProtectionCore(snapshot.Item);
            }

            var deletedAt = DateTimeOffset.UtcNow;
            foreach (var item in itemsToDelete)
            {
                // Content-stripped tombstone (see DeleteItemAsync): merge
                // protection needs only id + timestamps, and the payload
                // must not survive a delete in the store file or in cloud
                // backups of it.
                int index = source.IndexOf(item);
                if (index >= 0)
                {
                    source[index] = CreateTombstoneStub(item, deletedAt, isRecent);
                }
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
            if (targetItems.FirstOrDefault(item =>
                    string.Equals(item.Id, snapshot.Item.Id, StringComparison.Ordinal)) is { } existing)
            {
                if (!existing.IsDeleted)
                {
                    return false;
                }

                // Undo resurrects the tombstone left by the delete — the
                // refreshed timestamp also wins any later merge conflict.
                int existingIndex = targetItems.IndexOf(existing);
                var resurrected = Clone(snapshot.Item);
                resurrected.IsDeleted = false;
                resurrected.UpdatedAt = DateTimeOffset.UtcNow;
                targetItems[existingIndex] = resurrected;

                NormalizeSortOrders(targetItems);
                if (!snapshot.IsRecent)
                {
                    NormalizePinnedSortOrders(_data.Items);
                }

                _undoProtectedAttachmentsByItemId.Remove(resurrected.Id);
                await SaveCoreAsync();
                return true;
            }

            var item = Clone(snapshot.Item);
            int insertIndex = Math.Clamp(item.SortOrder, 0, targetItems.Count);
            targetItems.Insert(insertIndex, item);

            NormalizeSortOrders(targetItems);
            if (!snapshot.IsRecent)
            {
                NormalizePinnedSortOrders(_data.Items);
            }

            _undoProtectedAttachmentsByItemId.Remove(item.Id);
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> RestoreMergedItemsAsync(
        string mergedItemId,
        IReadOnlyList<QuickCaptureDeletedItemSnapshot>? sourceSnapshots)
    {
        if (sourceSnapshots is null || sourceSnapshots.Count == 0)
        {
            return Task.FromResult(false);
        }

        QuickCaptureItem[] items = sourceSnapshots
            .Where(snapshot => snapshot?.Item is not null)
            .Select(snapshot => snapshot.Item)
            .ToArray();
        return RestoreMergedItemsAsync(mergedItemId, (IReadOnlyList<QuickCaptureItem>)items);
    }

    public async Task<bool> RestoreMergedItemsAsync(
        string mergedItemId,
        IReadOnlyList<QuickCaptureItem>? sourceSnapshots)
    {
        if (string.IsNullOrWhiteSpace(mergedItemId) ||
            sourceSnapshots is null ||
            sourceSnapshots.Count == 0)
        {
            return false;
        }

        HashSet<string>? referencedPaths = null;
        HashSet<string>? tombstoneIds = null;
        bool restoredAny = false;

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (_data!.Items.FirstOrDefault(item =>
                    string.Equals(item.Id, mergedItemId, StringComparison.Ordinal)) is { IsDeleted: false } mergedExisting)
            {
                int mergedIndex = _data.Items.IndexOf(mergedExisting);
                if (mergedIndex >= 0)
                {
                    _data.Items[mergedIndex] = CreateTombstoneStub(mergedExisting, now, isRecent: false);
                }

                _undoProtectedAttachmentsByItemId.Remove(mergedItemId);
                restoredAny = true;
            }

            foreach (QuickCaptureItem snapshot in sourceSnapshots.OrderBy(item => item.SortOrder))
            {
                if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Id))
                {
                    continue;
                }

                var resurrected = Clone(snapshot);
                resurrected.IsDeleted = false;
                resurrected.UpdatedAt = now;

                if (_data.Items.FirstOrDefault(item =>
                        string.Equals(item.Id, resurrected.Id, StringComparison.Ordinal)) is { } existingSource)
                {
                    int existingIndex = _data.Items.IndexOf(existingSource);
                    if (existingIndex >= 0)
                    {
                        _data.Items[existingIndex] = resurrected;
                        restoredAny = true;
                    }
                }
                else
                {
                    int insertIndex = Math.Clamp(resurrected.SortOrder, 0, _data.Items.Count);
                    _data.Items.Insert(insertIndex, resurrected);
                    restoredAny = true;
                }

                _undoProtectedAttachmentsByItemId.Remove(resurrected.Id);
            }

            if (!restoredAny)
            {
                return false;
            }

            NormalizeSortOrders(_data.Items);
            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            (referencedPaths, tombstoneIds) = SnapshotAttachmentSweepInputsCore();
        }
        finally
        {
            _gate.Release();
        }

        if (referencedPaths is not null && tombstoneIds is not null)
        {
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                _store.AttachmentDirectory,
                referencedPaths,
                _recycleAttachmentFileAsync,
                tombstoneIds,
                TimeSpan.FromMinutes(2));
        }

        return true;
    }

    internal bool TryGetCachedPopoutBounds(
        string itemId,
        out (double X, double Y, double Width, double Height) bounds)
    {
        bounds = default;
        if (string.IsNullOrWhiteSpace(itemId) || !_gate.Wait(0))
        {
            return false;
        }

        try
        {
            QuickCaptureItem? existing = _data?.Items.FirstOrDefault(item =>
                !item.IsDeleted &&
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (existing is
                {
                    PopoutX: double x,
                    PopoutY: double y,
                    PopoutWidth: double width,
                    PopoutHeight: double height
                } &&
                double.IsFinite(x) &&
                double.IsFinite(y) &&
                double.IsFinite(width) &&
                double.IsFinite(height) &&
                width > 0 &&
                height > 0)
            {
                bounds = (x, y, width, height);
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UpdatePopoutBoundsAsync(
        string itemId,
        double x,
        double y,
        double width,
        double height)
    {
        if (string.IsNullOrWhiteSpace(itemId) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        double roundedX = Math.Round(x, 1);
        double roundedY = Math.Round(y, 1);
        double roundedWidth = Math.Round(width, 1);
        double roundedHeight = Math.Round(height, 1);

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            QuickCaptureItem? existing = _data!.Items.FirstOrDefault(item =>
                !item.IsDeleted &&
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (existing is null)
            {
                return false;
            }

            bool unchanged =
                existing.PopoutX.HasValue && Math.Abs(existing.PopoutX.Value - roundedX) < 0.5 &&
                existing.PopoutY.HasValue && Math.Abs(existing.PopoutY.Value - roundedY) < 0.5 &&
                existing.PopoutWidth.HasValue && Math.Abs(existing.PopoutWidth.Value - roundedWidth) < 0.5 &&
                existing.PopoutHeight.HasValue && Math.Abs(existing.PopoutHeight.Value - roundedHeight) < 0.5;
            if (unchanged)
            {
                return false;
            }

            existing.PopoutX = roundedX;
            existing.PopoutY = roundedY;
            existing.PopoutWidth = roundedWidth;
            existing.PopoutHeight = roundedHeight;
            await SaveCoreAsync(notify: false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ImportTodoItemsAsync(
        IReadOnlyList<TodoItem>? todoItems,
        bool copyLinkedFiles = false,
        bool? pinOverride = null)
    {
        if (todoItems is null || todoItems.Count == 0)
        {
            return 0;
        }

        int importedCount = 0;
        foreach (TodoItem todoItem in todoItems)
        {
            if (todoItem is null)
            {
                continue;
            }

            (string? title, string body, bool pinFromTodo) = QuickCaptureTagParser.FormatTodoForQuickCapture(todoItem);
            bool pin = pinFromTodo || (pinOverride == true);
            string[] attachmentPaths = (todoItem.Attachments ?? [])
                .Where(attachment =>
                    !string.IsNullOrWhiteSpace(attachment.FilePath) &&
                    (File.Exists(attachment.FilePath) || Directory.Exists(attachment.FilePath)))
                .Select(attachment => attachment.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            QuickCaptureWriteResult writeResult = await AddDetailedItemWithResultAsync(
                title,
                body,
                QuickCaptureAppearancePreset.Default,
                TextContentFormat.Markdown,
                pin: pin);
            if (writeResult.Saved && writeResult.Item is not null)
            {
                if (attachmentPaths.Length > 0)
                {
                    await AddAttachmentsAsync(
                        writeResult.Item.Id,
                        attachmentPaths,
                        copyToManagedStorage: copyLinkedFiles);
                }

                importedCount++;
            }
            else if (attachmentPaths.Length > 0)
            {
                QuickCaptureItem? attachmentOnly = await AddItemWithAttachmentsAsync(
                    attachmentPaths,
                    copyToManagedStorage: copyLinkedFiles,
                    pin: pin);
                if (attachmentOnly is not null)
                {
                    importedCount++;
                }
            }
        }

        return importedCount;
    }

    public async Task ReleaseDeletedItemSnapshotsAsync(
        IEnumerable<QuickCaptureDeletedItemSnapshot>? snapshots)
    {
        if (snapshots is null)
        {
            return;
        }

        HashSet<string>? referencedPaths = null;
        HashSet<string>? tombstoneIds = null;
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            foreach (QuickCaptureDeletedItemSnapshot snapshot in snapshots)
            {
                if (snapshot?.Item?.Id is { Length: > 0 } id)
                {
                    _undoProtectedAttachmentsByItemId.Remove(id);
                }
            }

            (referencedPaths, tombstoneIds) = SnapshotAttachmentSweepInputsCore();
        }
        finally
        {
            _gate.Release();
        }

        if (referencedPaths is not null && tombstoneIds is not null)
        {
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                _store.AttachmentDirectory,
                referencedPaths,
                _recycleAttachmentFileAsync,
                tombstoneIds,
                TimeSpan.FromMinutes(2));
        }
    }

    public async Task ClearAsync()
    {
        HashSet<string>? referencedPaths = null;
        HashSet<string>? tombstoneIds = null;
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            _undoProtectedAttachmentsByItemId.Clear();
            // Stub tombstones keep the wipe winning merge restores without
            // retaining cleared content in the store file.
            var clearedAt = DateTimeOffset.UtcNow;
            _data!.Items = _data.Items
                .Select(item => item.IsDeleted ? item : CreateTombstoneStub(item, clearedAt, isRecent: false))
                .ToList();
            _data.RecentItems = _data.RecentItems
                .Select(item => item.IsDeleted ? item : CreateTombstoneStub(item, clearedAt, isRecent: true))
                .ToList();
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
            (referencedPaths, tombstoneIds) = SnapshotAttachmentSweepInputsCore();
        }
        finally
        {
            _gate.Release();
        }

        if (referencedPaths is not null && tombstoneIds is not null)
        {
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                _store.AttachmentDirectory,
                referencedPaths,
                _recycleAttachmentFileAsync,
                tombstoneIds,
                TimeSpan.Zero);
        }
    }

    public async Task ClearRecentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var clearedAt = DateTimeOffset.UtcNow;
            _data!.RecentItems = _data.RecentItems
                .Select(item => item.IsDeleted ? item : CreateTombstoneStub(item, clearedAt, isRecent: true))
                .ToList();
            await SaveCoreAsync();
            CleanupUnusedImageCacheCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One-time idempotent migration from legacy v1.5.9 <see cref="QuickCaptureStoreData.RecentItems"/>
    /// into <see cref="ClipboardStore"/>. Fails safe if <see cref="ClipboardStore.SaveAsync"/> throws
    /// so <see cref="QuickCaptureStoreData.RecentItems"/> is never cleared before durable persistence.
    /// </summary>
    public async Task<int> MigrateLegacyRecentItemsToClipboardAsync(
        ClipboardStore? targetClipboardStore = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync();
            if (_data is null || _data.RecentItems.Count == 0)
            {
                return 0;
            }

            ClipboardStore clipboardStore = targetClipboardStore ?? new ClipboardStore(ResolveSiblingClipboardDirectory());
            ClipboardStoreDocument doc = await clipboardStore.LoadAsync().ConfigureAwait(false);

            var existingContentHashes = new HashSet<string>(StringComparer.Ordinal);
            var existingNormalizedTexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (ClipboardHistoryItem existing in doc.Items)
            {
                if (!string.IsNullOrWhiteSpace(existing.ContentHash))
                {
                    existingContentHashes.Add(existing.ContentHash);
                }

                if (existing.Kind == ClipboardItemKind.Text)
                {
                    string? fullText = !string.IsNullOrWhiteSpace(existing.TextPayloadFileName)
                        ? await clipboardStore.ReadPayloadTextAsync(existing.TextPayloadFileName, cancellationToken).ConfigureAwait(false)
                        : existing.TextContent;
                    string normalizedExisting = (fullText ?? existing.Summary ?? string.Empty).Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
                    if (normalizedExisting.Length > 0)
                    {
                        existingNormalizedTexts.Add(normalizedExisting);
                    }
                }
            }

            int importedCount = 0;
            foreach (QuickCaptureItem recent in _data.RecentItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recent is null || recent.IsDeleted)
                {
                    continue;
                }

                DateTimeOffset createdDto = recent.CreatedAt != default ? recent.CreatedAt : DateTimeOffset.UtcNow;
                DateTimeOffset updatedDto = recent.UpdatedAt != default ? recent.UpdatedAt : createdDto;
                long createdTicks = createdDto.UtcTicks;
                long updatedTicks = updatedDto.UtcTicks;

                bool importedAsImage = false;
                if (recent.Type == QuickCaptureItemType.Image ||
                    (!string.IsNullOrWhiteSpace(recent.ImagePath) && string.IsNullOrWhiteSpace(recent.Body)))
                {
                    string? sourceImagePath = ResolveLegacyRecentImagePath(recent);
                    if (!string.IsNullOrWhiteSpace(sourceImagePath) && File.Exists(sourceImagePath))
                    {
                        byte[] imgBytes = await File.ReadAllBytesAsync(sourceImagePath, cancellationToken).ConfigureAwait(false);
                        if (imgBytes.Length > 0)
                        {
                            string sha256 = Convert.ToHexStringLower(SHA256.HashData(imgBytes));
                            string contentHash = $"img:{sha256}";
                            if (existingContentHashes.Add(contentHash))
                            {
                                string ext = NormalizeImageExtension(sourceImagePath);
                                string destFileName = $"{sha256}{ext}";
                                string destImagePath = Path.Combine(clipboardStore.ImageDirectory, destFileName);
                                await ClipboardStorageFileOps.WriteBytesIfMissingAsync(destImagePath, imgBytes, cancellationToken)
                                    .ConfigureAwait(false);

                                string thumbFileName = destFileName;
                                string qcThumbPath = GetThumbnailPath(sourceImagePath);
                                if (File.Exists(qcThumbPath))
                                {
                                    byte[] thumbBytes = await File.ReadAllBytesAsync(qcThumbPath, cancellationToken).ConfigureAwait(false);
                                    if (thumbBytes.Length > 0)
                                    {
                                        string destThumbFileName = $"{sha256}.png";
                                        string destThumbPath = Path.Combine(clipboardStore.ThumbnailDirectory, destThumbFileName);
                                        await ClipboardStorageFileOps.WriteBytesIfMissingAsync(destThumbPath, thumbBytes, cancellationToken)
                                            .ConfigureAwait(false);
                                        thumbFileName = destThumbFileName;
                                    }
                                }

                                _ = ClipboardPlatformInterop.TryProbeImageFileDimensions(destImagePath, out int imgW, out int imgH);
                                string summary = imgW > 0 && imgH > 0
                                    ? $"{imgW} × {imgH}"
                                    : (!string.IsNullOrWhiteSpace(recent.Title) ? recent.Title.Trim() : destFileName);

                                doc.Items.Add(new ClipboardHistoryItem
                                {
                                    Kind = ClipboardItemKind.Image,
                                    ContentHash = contentHash,
                                    Summary = summary,
                                    ImageFileName = destFileName,
                                    ThumbnailFileName = thumbFileName,
                                    ImageWidth = imgW,
                                    ImageHeight = imgH,
                                    ByteSize = imgBytes.LongLength,
                                    IsPinned = recent.IsPinned,
                                    CreatedAtUtcTicks = createdTicks,
                                    UpdatedAtUtcTicks = updatedTicks
                                });
                                importedCount++;
                            }
                        }

                        importedAsImage = true;
                    }
                    else if (string.IsNullOrWhiteSpace(recent.Body) && string.IsNullOrWhiteSpace(recent.Url))
                    {
                        continue;
                    }
                }

                if (importedAsImage)
                {
                    continue;
                }

                string rawText = !string.IsNullOrWhiteSpace(recent.Body)
                    ? recent.Body
                    : (recent.Url ?? string.Empty);
                string trimmed = rawText.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string normalizedKey = trimmed.Replace("\r\n", "\n", StringComparison.Ordinal);
                string textHash = $"txt:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))}";
                if (!existingContentHashes.Add(textHash) || !existingNormalizedTexts.Add(normalizedKey))
                {
                    continue;
                }

                ClipboardClassifier.ClassifyText(
                    trimmed,
                    htmlContent: null,
                    rtfContent: null,
                    out ClipboardTextSubKind subKind,
                    out string? colorHex,
                    out string? codeLanguage);
                if (recent.Type == QuickCaptureItemType.Link &&
                    subKind == ClipboardTextSubKind.Plain &&
                    !string.IsNullOrWhiteSpace(recent.Url))
                {
                    subKind = ClipboardTextSubKind.Url;
                }

                bool isSecret = ClipboardClassifier.IsHighConfidenceSecret(trimmed);
                var clipItem = new ClipboardHistoryItem
                {
                    Kind = ClipboardItemKind.Text,
                    SubKind = subKind,
                    ContentHash = textHash,
                    Summary = ClipboardClassifier.BuildSummary(trimmed),
                    TextContent = rawText,
                    ColorHex = colorHex,
                    UrlTitle = subKind == ClipboardTextSubKind.Url && !string.IsNullOrWhiteSpace(recent.Title)
                        ? recent.Title.Trim()
                        : null,
                    CodeLanguage = codeLanguage,
                    IsSensitive = isSecret,
                    IsPinned = recent.IsPinned,
                    CharCount = rawText.Length,
                    LineCount = ClipboardHistoryItem.CountLines(rawText),
                    ByteSize = Encoding.UTF8.GetByteCount(rawText),
                    CreatedAtUtcTicks = createdTicks,
                    UpdatedAtUtcTicks = updatedTicks
                };
                _ = await clipboardStore.ExternalizeItemPayloadsAsync(clipItem, cancellationToken).ConfigureAwait(false);
                doc.Items.Add(clipItem);
                importedCount++;
            }

            if (importedCount > 0)
            {
                doc.Items.Sort(static (a, b) =>
                {
                    int cmp = b.UpdatedAtUtcTicks.CompareTo(a.UpdatedAtUtcTicks);
                    return cmp != 0 ? cmp : b.CreatedAtUtcTicks.CompareTo(a.CreatedAtUtcTicks);
                });
            }

            // Always persist ClipboardStore first so a write failure leaves RecentItems intact.
            await clipboardStore.SaveAsync(doc).ConfigureAwait(false);

            _data.RecentItems.Clear();
            await SaveCoreAsync(notify: true);
            CleanupUnusedImageCacheCore();
            return importedCount;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveSiblingClipboardDirectory()
    {
        string? qcDir = Path.GetDirectoryName(_store.StorePath);
        string? parentDir = !string.IsNullOrWhiteSpace(qcDir) ? Path.GetDirectoryName(qcDir) : null;
        return !string.IsNullOrWhiteSpace(parentDir)
            ? Path.Combine(parentDir, "clipboard")
            : Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "clipboard");
    }

    private string? ResolveLegacyRecentImagePath(QuickCaptureItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ImagePath))
        {
            if (File.Exists(item.ImagePath))
            {
                return item.ImagePath;
            }

            string candidateInStore = Path.Combine(_store.ImageDirectory, Path.GetFileName(item.ImagePath));
            if (File.Exists(candidateInStore))
            {
                return candidateInStore;
            }
        }

        string? attachmentPath = item.Attachments?
            .FirstOrDefault(static a => a is not null && !string.IsNullOrWhiteSpace(a.FilePath))
            ?.FilePath;
        if (!string.IsNullOrWhiteSpace(attachmentPath))
        {
            if (File.Exists(attachmentPath))
            {
                return attachmentPath;
            }

            string candidateInStore = Path.Combine(_store.ImageDirectory, Path.GetFileName(attachmentPath));
            if (File.Exists(candidateInStore))
            {
                return candidateInStore;
            }
        }

        return null;
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
        QuickCaptureImageCacheCleanupResult result;
        HashSet<string>? referencedPaths = null;
        HashSet<string>? tombstoneIds = null;
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            result = CleanupUnusedImageCacheCore();
            (referencedPaths, tombstoneIds) = SnapshotAttachmentSweepInputsCore();
        }
        finally
        {
            _gate.Release();
        }

        if (referencedPaths is not null && tombstoneIds is not null)
        {
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                _store.AttachmentDirectory,
                referencedPaths,
                _recycleAttachmentFileAsync,
                tombstoneIds,
                TimeSpan.FromMinutes(2));
        }

        return result;
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

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                return null;
            }

            string cachedImagePath = await SaveImageFileAsync(imagePath);
            string contentHash = Path.GetFileNameWithoutExtension(cachedImagePath);
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
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        QuickCaptureWriteResult result = await UpdateItemDetailsWithResultAsync(
            itemId,
            title,
            body,
            appearancePreset,
            contentFormat);
        return result.Saved;
    }

    public async Task<QuickCaptureWriteResult> UpdateItemDetailsWithResultAsync(
        string itemId,
        string? title,
        string body,
        QuickCaptureAppearancePreset appearancePreset,
        TextContentFormat? contentFormat = null)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return new QuickCaptureWriteResult(false, false, null);
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var item = _data!.Items.FirstOrDefault(entry =>
                string.Equals(entry.Id, itemId, StringComparison.Ordinal) && !entry.IsDeleted);
            if (item is null)
            {
                return new QuickCaptureWriteResult(false, false, null);
            }

            TextContentFormat effectiveFormat = NormalizeContentFormat(contentFormat ?? item.ContentFormat);
            string normalizedBody = NormalizeBody(body, effectiveFormat);
            string? normalizedTitle = NormalizeOptionalText(title);

            if (item.Type != QuickCaptureItemType.Image &&
                string.IsNullOrWhiteSpace(normalizedTitle) &&
                string.IsNullOrWhiteSpace(normalizedBody))
            {
                return new QuickCaptureWriteResult(false, false, null);
            }

            normalizedBody = await ExtractInlineMarkdownImagesAsync(
                item.Id,
                normalizedBody,
                effectiveFormat,
                item.Attachments);
            normalizedBody = TruncateBody(normalizedBody, out bool wasTruncated);
            item.Tags = QuickCaptureTagParser.MergeInlineTags(item.Tags, item.Title, item.Body, normalizedTitle, normalizedBody);
            item.Title = normalizedTitle;
            item.Body = normalizedBody;
            item.ContentFormat = effectiveFormat;
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
            return new QuickCaptureWriteResult(true, wasTruncated, Clone(item));
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
        HashSet<string>? startupReferencedPaths = null;
        HashSet<string>? startupTombstoneIds = null;
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            if (_pendingStartupAttachmentSweep)
            {
                _pendingStartupAttachmentSweep = false;
                (startupReferencedPaths, startupTombstoneIds) = SnapshotAttachmentSweepInputsCore();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (startupReferencedPaths is not null && startupTombstoneIds is not null)
        {
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                _store.AttachmentDirectory,
                startupReferencedPaths,
                _recycleAttachmentFileAsync,
                startupTombstoneIds,
                TimeSpan.Zero);
        }
    }

    private async Task EnsureLoadedCoreAsync()
    {
        if (_data is not null)
        {
            return;
        }

        _data = await _store.LoadAsync();
        _pendingStartupAttachmentSweep = _recycleAttachmentFileAsync is not null;
        bool migrated = false;
        foreach (QuickCaptureItem item in _data.Items.Where(item => !item.IsDeleted))
        {
            string body = await ExtractInlineMarkdownImagesAsync(
                item.Id,
                item.Body,
                item.ContentFormat,
                item.Attachments);
            body = TruncateBody(body, out _);
            if (!string.Equals(body, item.Body, StringComparison.Ordinal))
            {
                item.Body = body;
                migrated = true;
            }
        }

        foreach (QuickCaptureItem item in _data.Items.Where(item => !item.IsDeleted && !item.IsRecent))
        {
            List<string> syncedTags = QuickCaptureTagParser.MergeInlineTags(item.Tags, null, null, item.Title, item.Body);
            if (!syncedTags.SequenceEqual(item.Tags ?? [], StringComparer.Ordinal))
            {
                item.Tags = syncedTags;
                migrated = true;
            }
        }

        if (PruneExcessTombstonesCore(_data))
        {
            migrated = true;
        }

        if (migrated)
        {
            await SaveCoreAsync(notify: false);
        }
    }

    private async Task<string> ExtractInlineMarkdownImagesAsync(
        string itemId,
        string body,
        TextContentFormat contentFormat,
        List<TodoAttachment> attachments)
    {
        if (contentFormat != TextContentFormat.Markdown || string.IsNullOrEmpty(body))
        {
            return body;
        }

        MarkdownInlineImageExtractionResult result = await MarkdownInlineImageExtractor.ExtractAsync(
            body,
            Path.Combine(_store.AttachmentDirectory, itemId));
        foreach (TodoAttachment attachment in result.Attachments)
        {
            if (!attachments.Any(existing =>
                    string.Equals(existing.Id, attachment.Id, StringComparison.Ordinal)))
            {
                attachments.Add(attachment);
            }
        }

        return result.Body;
    }

    private async Task SaveCoreAsync(bool notify = true)
    {
        PruneExcessTombstonesCore(_data!);
        await _store.SaveAsync(_data!);
        if (notify)
        {
            Changed?.Invoke();
        }
    }

    private bool PruneExcessTombstonesCore(QuickCaptureStoreData data)
    {
        PruneExpiredUndoAttachmentProtectionCore();
        int itemsTombstones = data.Items.Count(item => item is not null && item.IsDeleted);
        int recentTombstones = data.RecentItems.Count(item => item is not null && item.IsDeleted);
        if (itemsTombstones + recentTombstones <= MaxTombstones)
        {
            return false;
        }

        var allTombstones = new List<(QuickCaptureItem Item, bool ProtectedByUndo, DateTimeOffset Timestamp, int Order)>(
            itemsTombstones + recentTombstones);
        int order = 0;
        foreach (QuickCaptureItem item in data.Items)
        {
            if (item is { IsDeleted: true })
            {
                bool undoProtected = !string.IsNullOrWhiteSpace(item.Id) &&
                                     _undoProtectedAttachmentsByItemId.ContainsKey(item.Id);
                DateTimeOffset ts = item.UpdatedAt != default ? item.UpdatedAt : item.CreatedAt;
                allTombstones.Add((item, undoProtected, ts, order++));
            }
        }

        foreach (QuickCaptureItem item in data.RecentItems)
        {
            if (item is { IsDeleted: true })
            {
                bool undoProtected = !string.IsNullOrWhiteSpace(item.Id) &&
                                     _undoProtectedAttachmentsByItemId.ContainsKey(item.Id);
                DateTimeOffset ts = item.UpdatedAt != default ? item.UpdatedAt : item.CreatedAt;
                allTombstones.Add((item, undoProtected, ts, order++));
            }
        }

        var kept = new HashSet<QuickCaptureItem>(ReferenceEqualityComparer.Instance);
        foreach (var entry in allTombstones
                     .OrderByDescending(entry => entry.ProtectedByUndo)
                     .ThenByDescending(entry => entry.Timestamp)
                     .ThenBy(entry => entry.Order))
        {
            if (kept.Count < MaxTombstones || entry.ProtectedByUndo)
            {
                kept.Add(entry.Item);
            }
            else if (!string.IsNullOrWhiteSpace(entry.Item.Id) &&
                     _pendingPrunedTombstoneIds.Count < MaxPendingPrunedTombstoneIds)
            {
                _pendingPrunedTombstoneIds.Add(entry.Item.Id);
            }
        }

        int removedFromItems = data.Items.RemoveAll(item => item is not null && item.IsDeleted && !kept.Contains(item));
        int removedFromRecent = data.RecentItems.RemoveAll(item => item is not null && item.IsDeleted && !kept.Contains(item));
        if (removedFromItems > 0)
        {
            NormalizeSortOrders(data.Items);
            NormalizePinnedSortOrders(data.Items);
        }

        if (removedFromRecent > 0)
        {
            NormalizeSortOrders(data.RecentItems);
        }

        return removedFromItems > 0 || removedFromRecent > 0;
    }

    private QuickCaptureImageCacheInfo GetImageCacheInfoCore()
    {
        return GetImageCacheInfoCore(GetReferencedImagePathsCore());
    }

    private QuickCaptureImageCacheCleanupResult CleanupUnusedImageCacheCore()
    {
        return CleanupUnusedImageCacheCore(GetReferencedImagePathsCore());
    }

    private static string NormalizeBody(
        string? body,
        TextContentFormat contentFormat = TextContentFormat.PlainText)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string value = body.Replace("\0", string.Empty, StringComparison.Ordinal);
        return contentFormat == TextContentFormat.Markdown ? value : value.Trim();
    }

    private static string TruncateBody(string body, out bool wasTruncated)
    {
        wasTruncated = body.Length > MaxItemBodyCharacters;
        if (!wasTruncated)
        {
            return body;
        }

        int length = MaxItemBodyCharacters;
        if (char.IsHighSurrogate(body[length - 1]) && char.IsLowSurrogate(body[length]))
        {
            length--;
        }

        return body[..length];
    }

    private static string? NormalizeOptionalText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static QuickCaptureAppearancePreset NormalizeAppearancePreset(QuickCaptureAppearancePreset value)
    {
        return Enum.IsDefined(value) ? value : QuickCaptureAppearancePreset.Default;
    }

    private static TextContentFormat NormalizeContentFormat(TextContentFormat value) =>
        Enum.IsDefined(value) ? value : TextContentFormat.PlainText;

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
                           !item.IsDeleted &&
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
            MirrorFolderPath = data.MirrorFolderPath,
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
            ContentFormat = item.ContentFormat,
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
            UpdatedAt = item.UpdatedAt,
            DeviceId = item.DeviceId,
            PopoutX = item.PopoutX,
            PopoutY = item.PopoutY,
            PopoutWidth = item.PopoutWidth,
            PopoutHeight = item.PopoutHeight
        };
    }

    /// <summary>
    /// A tombstone stripped of user content: keeps the id + timestamps so a
    /// merge restore cannot resurrect a deleted or wiped item, while the
    /// payload actually leaves the store file (and any cloud backup of it).
    /// Undo restores from the in-memory delete snapshot, not from this stub.
    /// </summary>
    private static QuickCaptureItem CreateTombstoneStub(
        QuickCaptureItem item,
        DateTimeOffset deletedAt,
        bool isRecent) =>
        new()
        {
            Id = item.Id,
            IsDeleted = true,
            IsRecent = isRecent,
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            UpdatedAt = deletedAt
        };



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

    private void RegisterUndoAttachmentProtectionCore(QuickCaptureItem item)
    {
        PruneExpiredUndoAttachmentProtectionCore();
        if (item.Attachments is not { Count: > 0 })
        {
            return;
        }

        var managedPaths = new List<string>();
        foreach (TodoAttachment attachment in item.Attachments)
        {
            if (attachment is not { IsManagedCopy: true } ||
                string.IsNullOrWhiteSpace(attachment.FilePath) ||
                IsPathInsideDirectory(attachment.FilePath, _store.ImageDirectory))
            {
                continue;
            }

            try
            {
                if (IsPathInsideDirectory(attachment.FilePath, _store.AttachmentDirectory))
                {
                    managedPaths.Add(Path.GetFullPath(attachment.FilePath));
                }
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        if (managedPaths.Count > 0)
        {
            _undoProtectedAttachmentsByItemId[item.Id] = new UndoAttachmentLease(
                managedPaths,
                DateTimeOffset.UtcNow + UndoAttachmentRetentionWindow);
        }
    }

    private void PruneExpiredUndoAttachmentProtectionCore()
    {
        if (_undoProtectedAttachmentsByItemId.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var expiredIds = _undoProtectedAttachmentsByItemId
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(pair => pair.Key)
            .ToList();
        foreach (string id in expiredIds)
        {
            _undoProtectedAttachmentsByItemId.Remove(id);
        }
    }

    private (HashSet<string> ReferencedPaths, HashSet<string> TombstoneIds) SnapshotAttachmentSweepInputsCore()
    {
        PruneExpiredUndoAttachmentProtectionCore();
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_data is not null)
        {
            foreach (TodoAttachment attachment in _data.Items
                         .Concat(_data.RecentItems)
                         .Where(item => item is not null && !item.IsDeleted)
                         .SelectMany(item => item.Attachments ?? []))
            {
                if (attachment is null || string.IsNullOrWhiteSpace(attachment.FilePath))
                {
                    continue;
                }

                referencedPaths.Add(NormalizePath(attachment.FilePath));
            }
        }

        foreach (UndoAttachmentLease lease in _undoProtectedAttachmentsByItemId.Values)
        {
            foreach (string path in lease.ManagedPaths)
            {
                referencedPaths.Add(path);
            }
        }

        var tombstoneIds = (_data?.Items ?? [])
            .Concat(_data?.RecentItems ?? [])
            .Where(item => item is not null &&
                           item.IsDeleted &&
                           !string.IsNullOrWhiteSpace(item.Id) &&
                           !_undoProtectedAttachmentsByItemId.ContainsKey(item.Id))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_pendingPrunedTombstoneIds.Count > 0)
        {
            foreach (string prunedId in _pendingPrunedTombstoneIds)
            {
                if (!_undoProtectedAttachmentsByItemId.ContainsKey(prunedId))
                {
                    tombstoneIds.Add(prunedId);
                }
            }

            _pendingPrunedTombstoneIds.Clear();
        }

        return (referencedPaths, tombstoneIds);
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

        await CreateImageThumbnailAsync(imagePath);
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

    private string? TryResolveColdStoreJsonPath()
    {
        try
        {
            string? storeRoot = Path.GetDirectoryName(_store.ImageDirectory);
            if (!string.IsNullOrWhiteSpace(storeRoot))
            {
                string directCandidate = Path.Combine(storeRoot, "quick-capture.json");
                if (File.Exists(directCandidate))
                {
                    return directCandidate;
                }

                string folderName = Path.GetFileName(
                    storeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.Equals(folderName, "quick-capture", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(folderName, "QuickCapture", StringComparison.OrdinalIgnoreCase))
                {
                    string? parentDir = Path.GetDirectoryName(storeRoot);
                    if (!string.IsNullOrWhiteSpace(parentDir))
                    {
                        string parentCandidate = Path.Combine(parentDir, "quick-capture.json");
                        if (File.Exists(parentCandidate))
                        {
                            return parentCandidate;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall back to _store.LoadAsync() if path probing fails.
        }

        return null;
    }

    private static IReadOnlyList<QuickCaptureSearchProjection> ProjectSearchItems(
        IReadOnlyList<QuickCaptureItem> items,
        int maxBodyChars,
        CancellationToken cancellationToken)
    {
        var projections = new List<QuickCaptureSearchProjection>(items.Count);
        foreach (QuickCaptureItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || item.IsDeleted || string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            string? title = item.Title is { Length: > 0 } rawTitle
                ? TruncateForProjection(rawTitle, maxBodyChars)
                : null;
            string text = TruncateForProjection(item.Body, maxBodyChars);
            string? url = item.Url is { Length: > 0 } rawUrl
                ? TruncateForProjection(rawUrl, maxBodyChars)
                : null;
            IReadOnlyList<TodoAttachment> attachments = item.Attachments is { Count: > 0 }
                ? item.Attachments.Select(attachment => attachment.Clone()).ToList()
                : [];
            DateTimeOffset updatedAt = item.UpdatedAt != default ? item.UpdatedAt : item.CreatedAt;

            projections.Add(new QuickCaptureSearchProjection(
                item.Id,
                title,
                text,
                url,
                item.Type,
                item.IsPinned,
                updatedAt,
                attachments));
        }

        return projections;
    }

    private static async Task<IReadOnlyList<QuickCaptureSearchProjection>?> TryStreamSearchProjectionsFromFileAsync(
        string filePath,
        int maxBodyChars,
        CancellationToken cancellationToken)
    {
        byte[]? rented = null;
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > MaxColdStreamFileBytes)
            {
                return null;
            }

            int byteLength = (int)length;
            rented = ArrayPool<byte>.Shared.Rent(byteLength);
            int totalRead = 0;
            while (totalRead < byteLength)
            {
                int read = await stream
                    .ReadAsync(rented.AsMemory(totalRead, byteLength - totalRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return ParseSearchProjectionsFromUtf8(
                rented.AsSpan(0, totalRead),
                maxBodyChars,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static async Task<(int ActiveCount, int RecentCount)?> TryStreamCountsFromFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        byte[]? rented = null;
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > MaxColdStreamFileBytes)
            {
                return null;
            }

            int byteLength = (int)length;
            rented = ArrayPool<byte>.Shared.Rent(byteLength);
            int totalRead = 0;
            while (totalRead < byteLength)
            {
                int read = await stream
                    .ReadAsync(rented.AsMemory(totalRead, byteLength - totalRead), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return ParseCountsFromUtf8(rented.AsSpan(0, totalRead), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static IReadOnlyList<QuickCaptureSearchProjection> ParseSearchProjectionsFromUtf8(
        ReadOnlySpan<byte> utf8Json,
        int maxBodyChars,
        CancellationToken cancellationToken)
    {
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected QuickCaptureStoreData JSON object.");
        }

        var results = new List<QuickCaptureSearchProjection>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isItems = PropertyEqualsIgnoreCase(ref reader, "items"u8, "items");
            if (!reader.Read())
            {
                break;
            }

            if (isItems && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (ReadProjectedItem(ref reader, maxBodyChars) is { } projection)
                        {
                            results.Add(projection);
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return results;
    }

    private static (int ActiveCount, int RecentCount) ParseCountsFromUtf8(
        ReadOnlySpan<byte> utf8Json,
        CancellationToken cancellationToken)
    {
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected QuickCaptureStoreData JSON object.");
        }

        int activeCount = 0;
        int recentCount = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            bool isItems = PropertyEqualsIgnoreCase(ref reader, "items"u8, "items");
            bool isRecentItems = !isItems &&
                                 PropertyEqualsIgnoreCase(ref reader, "recentItems"u8, "recentItems");
            if (!reader.Read())
            {
                break;
            }

            if ((isItems || isRecentItems) && reader.TokenType == JsonTokenType.StartArray)
            {
                int count = CountNonDeletedArrayItems(ref reader, cancellationToken);
                if (isItems)
                {
                    activeCount = count;
                }
                else
                {
                    recentCount = count;
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return (activeCount, recentCount);
    }

    private static int CountNonDeletedArrayItems(
        ref Utf8JsonReader reader,
        CancellationToken cancellationToken)
    {
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            bool isDeleted = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool isDeletedProp = PropertyEqualsIgnoreCase(ref reader, "isDeleted"u8, "isDeleted");
                if (!reader.Read())
                {
                    break;
                }

                if (isDeletedProp && reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                {
                    isDeleted = reader.GetBoolean();
                }
                else
                {
                    reader.Skip();
                }
            }

            if (!isDeleted)
            {
                count++;
            }
        }

        return count;
    }

    private enum ProjectedItemProperty
    {
        Other,
        Id,
        IsDeleted,
        Title,
        Body,
        Url,
        Type,
        IsPinned,
        UpdatedAt,
        CreatedAt,
        Attachments
    }

    private static ProjectedItemProperty ClassifyProjectedItemProperty(ref Utf8JsonReader reader)
    {
        if (PropertyEqualsIgnoreCase(ref reader, "id"u8, "id"))
        {
            return ProjectedItemProperty.Id;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "isDeleted"u8, "isDeleted"))
        {
            return ProjectedItemProperty.IsDeleted;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "title"u8, "title"))
        {
            return ProjectedItemProperty.Title;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "body"u8, "body"))
        {
            return ProjectedItemProperty.Body;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "url"u8, "url"))
        {
            return ProjectedItemProperty.Url;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "type"u8, "type"))
        {
            return ProjectedItemProperty.Type;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "isPinned"u8, "isPinned"))
        {
            return ProjectedItemProperty.IsPinned;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "updatedAt"u8, "updatedAt"))
        {
            return ProjectedItemProperty.UpdatedAt;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "createdAt"u8, "createdAt"))
        {
            return ProjectedItemProperty.CreatedAt;
        }

        if (PropertyEqualsIgnoreCase(ref reader, "attachments"u8, "attachments"))
        {
            return ProjectedItemProperty.Attachments;
        }

        return ProjectedItemProperty.Other;
    }

    private static QuickCaptureSearchProjection? ReadProjectedItem(
        ref Utf8JsonReader reader,
        int maxBodyChars)
    {
        string? id = null;
        bool isDeleted = false;
        string? title = null;
        string body = string.Empty;
        string? url = null;
        QuickCaptureItemType type = QuickCaptureItemType.Text;
        bool hasExplicitType = false;
        bool isPinned = false;
        DateTimeOffset updatedAt = default;
        DateTimeOffset createdAt = default;
        List<TodoAttachment>? attachments = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            ProjectedItemProperty prop = ClassifyProjectedItemProperty(ref reader);
            if (!reader.Read())
            {
                break;
            }

            switch (prop)
            {
                case ProjectedItemProperty.Id:
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        id = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.IsDeleted:
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                    {
                        isDeleted = reader.GetBoolean();
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.Title:
                    if (!isDeleted && reader.TokenType == JsonTokenType.String)
                    {
                        title = NormalizeOptionalText(ReadTruncatedJsonString(ref reader, maxBodyChars));
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.Body:
                    if (!isDeleted && reader.TokenType == JsonTokenType.String)
                    {
                        body = ReadTruncatedJsonString(ref reader, maxBodyChars);
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.Url:
                    if (!isDeleted && reader.TokenType == JsonTokenType.String)
                    {
                        url = NormalizeOptionalText(ReadTruncatedJsonString(ref reader, maxBodyChars));
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.Type:
                    if (reader.TokenType == JsonTokenType.Number &&
                        reader.TryGetInt32(out int numericType) &&
                        Enum.IsDefined((QuickCaptureItemType)numericType))
                    {
                        type = (QuickCaptureItemType)numericType;
                        hasExplicitType = true;
                    }
                    else if (reader.TokenType == JsonTokenType.String &&
                             Enum.TryParse<QuickCaptureItemType>(reader.GetString(), ignoreCase: true, out var parsedType))
                    {
                        type = parsedType;
                        hasExplicitType = true;
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.IsPinned:
                    if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                    {
                        isPinned = reader.GetBoolean();
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.UpdatedAt:
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        TryReadDateTimeOffset(ref reader, out updatedAt);
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.CreatedAt:
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        TryReadDateTimeOffset(ref reader, out createdAt);
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                case ProjectedItemProperty.Attachments:
                    if (!isDeleted && reader.TokenType == JsonTokenType.StartArray)
                    {
                        attachments = ReadProjectedAttachments(ref reader);
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        if (isDeleted || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!hasExplicitType && TryDetectUrl(body, out string? detectedUrl))
        {
            type = QuickCaptureItemType.Link;
            url ??= detectedUrl;
        }

        DateTimeOffset effectiveUpdatedAt = updatedAt != default ? updatedAt : createdAt;
        return new QuickCaptureSearchProjection(
            id,
            title,
            body,
            url,
            type,
            isPinned,
            effectiveUpdatedAt,
            attachments ?? (IReadOnlyList<TodoAttachment>)[]);
    }

    private static List<TodoAttachment> ReadProjectedAttachments(ref Utf8JsonReader reader)
    {
        var list = new List<TodoAttachment>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var attachment = new TodoAttachment();
            bool hasAnyField = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool isId = PropertyEqualsIgnoreCase(ref reader, "id"u8, "id");
                bool isFilePath = !isId && PropertyEqualsIgnoreCase(ref reader, "filePath"u8, "filePath");
                bool isDisplayName = !isId && !isFilePath &&
                                     PropertyEqualsIgnoreCase(ref reader, "displayName"u8, "displayName");
                bool isType = !isId && !isFilePath && !isDisplayName &&
                              PropertyEqualsIgnoreCase(ref reader, "type"u8, "type");
                bool isStorageMode = !isId && !isFilePath && !isDisplayName && !isType &&
                                     PropertyEqualsIgnoreCase(ref reader, "storageMode"u8, "storageMode");
                bool isAddedAt = !isId && !isFilePath && !isDisplayName && !isType && !isStorageMode &&
                                 PropertyEqualsIgnoreCase(ref reader, "addedAt"u8, "addedAt");
                if (!reader.Read())
                {
                    break;
                }

                if (isId && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.GetString() is { Length: > 0 } value)
                    {
                        attachment.Id = value;
                    }
                }
                else if (isFilePath && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.GetString() is { } value)
                    {
                        attachment.FilePath = value;
                        hasAnyField = true;
                    }
                }
                else if (isDisplayName && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.GetString() is { } value)
                    {
                        attachment.DisplayName = value;
                        hasAnyField = true;
                    }
                }
                else if (isType && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.GetString() is { Length: > 0 } value)
                    {
                        attachment.Type = value;
                    }
                }
                else if (isStorageMode && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.GetString() is { Length: > 0 } value)
                    {
                        attachment.StorageMode = value;
                    }
                }
                else if (isAddedAt && reader.TokenType == JsonTokenType.String)
                {
                    if (TryReadDateTimeOffset(ref reader, out DateTimeOffset addedAt))
                    {
                        attachment.AddedAt = addedAt;
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (hasAnyField)
            {
                list.Add(attachment);
            }
        }

        return list;
    }

    private static bool TryReadDateTimeOffset(ref Utf8JsonReader reader, out DateTimeOffset value)
    {
        if (reader.TryGetDateTimeOffset(out value))
        {
            return true;
        }

        string? raw = reader.GetString();
        return DateTimeOffset.TryParse(raw, out value);
    }

    private static bool PropertyEqualsIgnoreCase(
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> expectedAscii,
        string expectedText)
    {
        if (!reader.HasValueSequence)
        {
            ReadOnlySpan<byte> span = reader.ValueSpan;
            if (span.IndexOf((byte)'\\') < 0)
            {
                if (span.Length != expectedAscii.Length)
                {
                    return false;
                }

                for (int i = 0; i < span.Length; i++)
                {
                    byte a = span[i];
                    byte b = expectedAscii[i];
                    if (a == b)
                    {
                        continue;
                    }

                    if ((uint)(a - 'A') <= ('Z' - 'A'))
                    {
                        a = (byte)(a + 32);
                    }

                    if ((uint)(b - 'A') <= ('Z' - 'A'))
                    {
                        b = (byte)(b + 32);
                    }

                    if (a != b)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        return string.Equals(reader.GetString(), expectedText, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTruncatedJsonString(ref Utf8JsonReader reader, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (!reader.HasValueSequence)
        {
            ReadOnlySpan<byte> span = reader.ValueSpan;
            if (span.Length <= maxChars * 2)
            {
                return TruncateForProjection(reader.GetString(), maxChars);
            }

            int byteBudget = Math.Min(span.Length, (maxChars * 6) + 16);
            return DecodeJsonStringPrefix(span[..byteBudget], maxChars);
        }

        ReadOnlySequence<byte> seq = reader.ValueSequence;
        if (seq.Length <= maxChars * 2)
        {
            return TruncateForProjection(reader.GetString(), maxChars);
        }

        int seqByteBudget = (int)Math.Min(seq.Length, (long)(maxChars * 6) + 16);
        byte[] rented = ArrayPool<byte>.Shared.Rent(seqByteBudget);
        try
        {
            seq.Slice(0, seqByteBudget).CopyTo(rented);
            return DecodeJsonStringPrefix(rented.AsSpan(0, seqByteBudget), maxChars);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string DecodeJsonStringPrefix(ReadOnlySpan<byte> rawUtf8, int maxChars)
    {
        if (rawUtf8.IsEmpty || maxChars <= 0)
        {
            return string.Empty;
        }

        char[] charBuffer = ArrayPool<char>.Shared.Rent(maxChars + 2);
        try
        {
            int charCount = 0;
            int i = 0;
            while (i < rawUtf8.Length && charCount < maxChars)
            {
                byte b = rawUtf8[i];
                if (b == (byte)'\\')
                {
                    if (i + 1 >= rawUtf8.Length)
                    {
                        break;
                    }

                    byte esc = rawUtf8[i + 1];
                    i += 2;
                    switch (esc)
                    {
                        case (byte)'"':
                            charBuffer[charCount++] = '"';
                            break;
                        case (byte)'\\':
                            charBuffer[charCount++] = '\\';
                            break;
                        case (byte)'/':
                            charBuffer[charCount++] = '/';
                            break;
                        case (byte)'b':
                            charBuffer[charCount++] = '\b';
                            break;
                        case (byte)'f':
                            charBuffer[charCount++] = '\f';
                            break;
                        case (byte)'n':
                            charBuffer[charCount++] = '\n';
                            break;
                        case (byte)'r':
                            charBuffer[charCount++] = '\r';
                            break;
                        case (byte)'t':
                            charBuffer[charCount++] = '\t';
                            break;
                        case (byte)'u':
                            if (i + 4 > rawUtf8.Length || !TryParseHex4(rawUtf8.Slice(i, 4), out ushort codeUnit))
                            {
                                goto Done;
                            }

                            i += 4;
                            char ch = (char)codeUnit;
                            if (char.IsHighSurrogate(ch))
                            {
                                if (charCount + 1 >= maxChars)
                                {
                                    goto Done;
                                }

                                if (i + 6 <= rawUtf8.Length &&
                                    rawUtf8[i] == (byte)'\\' &&
                                    rawUtf8[i + 1] == (byte)'u' &&
                                    TryParseHex4(rawUtf8.Slice(i + 2, 4), out ushort lowUnit) &&
                                    char.IsLowSurrogate((char)lowUnit))
                                {
                                    charBuffer[charCount++] = ch;
                                    charBuffer[charCount++] = (char)lowUnit;
                                    i += 6;
                                }
                            }
                            else
                            {
                                charBuffer[charCount++] = ch;
                            }
                            break;
                        default:
                            charBuffer[charCount++] = (char)esc;
                            break;
                    }
                }
                else if (b < 0x80)
                {
                    charBuffer[charCount++] = (char)b;
                    i++;
                }
                else
                {
                    OperationStatus status = Rune.DecodeFromUtf8(rawUtf8[i..], out Rune rune, out int consumed);
                    if (status != OperationStatus.Done || consumed <= 0)
                    {
                        break;
                    }

                    if (charCount + rune.Utf16SequenceLength > maxChars)
                    {
                        break;
                    }

                    charCount += rune.EncodeToUtf16(charBuffer.AsSpan(charCount));
                    i += consumed;
                }
            }

        Done:
            return new string(charBuffer, 0, charCount);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    private static bool TryParseHex4(ReadOnlySpan<byte> hex, out ushort value)
    {
        value = 0;
        if (hex.Length < 4)
        {
            return false;
        }

        int acc = 0;
        for (int i = 0; i < 4; i++)
        {
            byte c = hex[i];
            int nibble = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1
            };
            if (nibble < 0)
            {
                return false;
            }

            acc = (acc << 4) | nibble;
        }

        value = (ushort)acc;
        return true;
    }

    private static string TruncateForProjection(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        int length = maxChars;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return length <= 0 ? string.Empty : text[..length];
    }
}
