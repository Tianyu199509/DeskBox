using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public sealed record QuickCaptureMergeResult(
    QuickCaptureItem? MergedItem,
    IReadOnlyList<QuickCaptureDeletedItemSnapshot> SourceSnapshots);

public sealed partial class QuickCaptureService
{
    public async Task<string?> GetMirrorFolderPathAsync()
    {
        await EnsureLoadedAsync();
        await _gate.WaitAsync();
        try
        {
            return _data?.MirrorFolderPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetMirrorFolderPathAsync(string? folderPath)
    {
        string? normalized = string.IsNullOrWhiteSpace(folderPath)
            ? null
            : Path.GetFullPath(folderPath.Trim());

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            if (string.Equals(_data!.MirrorFolderPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _data.MirrorFolderPath = normalized;
            await SaveCoreAsync(notify: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddLinkedFileNoteAsync(string filePath, bool pin = false)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(filePath);
        string fileName = Path.GetFileName(fullPath);
        string itemId = Guid.NewGuid().ToString("N");

        TodoAttachment? attachment = await AttachmentStorageService.ImportPathAsync(
            fullPath,
            Path.Combine(_store.AttachmentDirectory, itemId),
            copyToManagedStorage: false);
        if (attachment is null)
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string? primaryImagePath = string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase)
                ? attachment.FilePath
                : null;
            var item = new QuickCaptureItem
            {
                Id = itemId,
                Title = fileName,
                Body = fullPath,
                ContentFormat = TextContentFormat.PlainText,
                Type = primaryImagePath is null ? QuickCaptureItemType.Text : QuickCaptureItemType.Image,
                ImagePath = primaryImagePath,
                Attachments = [attachment],
                Tags = QuickCaptureTagParser.MergeInlineTags(null, null, null, fileName, fullPath),
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

    public Task<int> SetArchivedAsync(string itemId, bool archived)
    {
        return string.IsNullOrWhiteSpace(itemId)
            ? Task.FromResult(0)
            : SetArchivedAsync([itemId], archived);
    }

    public async Task<int> SetArchivedAsync(IEnumerable<string> itemIds, bool archived)
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
                .Where(item => !item.IsDeleted &&
                               !item.IsRecent &&
                               selectedIds.Contains(item.Id) &&
                               (item.ArchivedAt.HasValue != archived))
                .ToList();
            if (targets.Count == 0)
            {
                return 0;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool unpinnedAny = false;
            foreach (QuickCaptureItem item in targets)
            {
                item.ArchivedAt = archived ? now : null;
                if (archived && item.IsPinned)
                {
                    item.IsPinned = false;
                    item.PinnedSortOrder = -1;
                    unpinnedAny = true;
                }

                item.UpdatedAt = now;
            }

            if (unpinnedAny)
            {
                NormalizePinnedSortOrders(_data.Items);
            }

            await SaveCoreAsync();
            return targets.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureMergeResult> MergeItemsAsync(IReadOnlyList<string> orderedItemIds)
    {
        if (orderedItemIds is null || orderedItemIds.Count < 2)
        {
            return new QuickCaptureMergeResult(null, []);
        }

        var distinctOrder = new List<string>(orderedItemIds.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in orderedItemIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && seenIds.Add(id))
            {
                distinctOrder.Add(id);
            }
        }

        if (distinctOrder.Count < 2)
        {
            return new QuickCaptureMergeResult(null, []);
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            var byId = _data!.Items
                .Where(item => !item.IsDeleted && !item.IsRecent)
                .ToDictionary(item => item.Id, StringComparer.Ordinal);

            var sources = new List<QuickCaptureItem>(distinctOrder.Count);
            foreach (string id in distinctOrder)
            {
                if (byId.TryGetValue(id, out QuickCaptureItem? found))
                {
                    sources.Add(found);
                }
            }

            if (sources.Count < 2)
            {
                return new QuickCaptureMergeResult(null, []);
            }

            string mergedItemId = Guid.NewGuid().ToString("N");
            string mergedBucketDir = Path.Combine(_store.AttachmentDirectory, mergedItemId);
            var mergedAttachments = new List<TodoAttachment>();
            var seenAttachmentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mergedTags = new List<string>();
            var seenTags = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var sectionBuilder = new StringBuilder();
            bool anyPinned = false;

            for (int index = 0; index < sources.Count; index++)
            {
                QuickCaptureItem source = sources[index];
                anyPinned |= source.IsPinned;

                if (index > 0 && sectionBuilder.Length > 0)
                {
                    sectionBuilder.AppendLine();
                    sectionBuilder.AppendLine();
                    sectionBuilder.AppendLine("---");
                    sectionBuilder.AppendLine();
                }

                bool hasTitle = !string.IsNullOrWhiteSpace(source.Title);
                if (hasTitle)
                {
                    sectionBuilder.Append("## ").AppendLine(source.Title!.Trim());
                    if (!string.IsNullOrWhiteSpace(source.Body))
                    {
                        sectionBuilder.AppendLine();
                    }
                }

                if (!string.IsNullOrWhiteSpace(source.Body))
                {
                    sectionBuilder.Append(source.Body.Trim());
                }

                foreach (string tag in source.Tags ?? [])
                {
                    string? normalizedTag = QuickCaptureTagParser.NormalizeTag(tag);
                    if (normalizedTag is not null && seenTags.Add(normalizedTag))
                    {
                        mergedTags.Add(normalizedTag);
                    }
                }

                foreach (TodoAttachment attachment in source.Attachments ?? [])
                {
                    if (string.IsNullOrWhiteSpace(attachment.FilePath))
                    {
                        continue;
                    }

                    TodoAttachment cloned = attachment.Clone();
                    if (cloned.IsManagedCopy &&
                        !IsPathInsideDirectory(cloned.FilePath, _store.ImageDirectory) &&
                        IsPathInsideDirectory(cloned.FilePath, _store.AttachmentDirectory))
                    {
                        string sourceAttachmentPath = cloned.FilePath;
                        string? copiedPath = await Task.Run(() =>
                        {
                            if (!File.Exists(sourceAttachmentPath))
                            {
                                return null;
                            }

                            Directory.CreateDirectory(mergedBucketDir);
                            string destPath = FileService.GetAvailablePath(
                                Path.Combine(mergedBucketDir, Path.GetFileName(sourceAttachmentPath)));
                            QuickCaptureStorageFileOps.CopyManagedFile(sourceAttachmentPath, destPath, overwrite: false);
                            return destPath;
                        });

                        if (copiedPath is not null)
                        {
                            cloned.FilePath = copiedPath;
                        }
                    }

                    string dedupeKey = $"{cloned.Type}|{cloned.DisplayName}|{cloned.FilePath}";
                    if (seenAttachmentKeys.Add(dedupeKey))
                    {
                        mergedAttachments.Add(cloned);
                    }
                }
            }

            string mergedBody = TruncateBody(sectionBuilder.ToString(), out _);
            if (string.IsNullOrWhiteSpace(mergedBody) && mergedAttachments.Count > 0)
            {
                mergedBody = string.Join(", ", mergedAttachments.Select(a => a.DisplayName));
            }

            mergedTags = QuickCaptureTagParser.MergeInlineTags(mergedTags, null, null, sources[0].Title, mergedBody);
            string? primaryImagePath = mergedAttachments
                .FirstOrDefault(a => string.Equals(a.Type, "image", StringComparison.OrdinalIgnoreCase))
                ?.FilePath;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var sourceSnapshots = sources
                .Select(source => new QuickCaptureDeletedItemSnapshot(Clone(source), IsRecent: false))
                .ToList();
            foreach (QuickCaptureDeletedItemSnapshot snapshot in sourceSnapshots)
            {
                RegisterUndoAttachmentProtectionCore(snapshot.Item);
            }

            foreach (QuickCaptureItem source in sources)
            {
                int idx = _data.Items.IndexOf(source);
                if (idx >= 0)
                {
                    _data.Items[idx] = CreateTombstoneStub(source, now, isRecent: false);
                }
            }

            var mergedItem = new QuickCaptureItem
            {
                Id = mergedItemId,
                Title = sources[0].Title,
                Body = mergedBody,
                ContentFormat = TextContentFormat.Markdown,
                Type = primaryImagePath is not null
                    ? QuickCaptureItemType.Image
                    : (TryDetectUrl(mergedBody, out string? url) ? QuickCaptureItemType.Link : QuickCaptureItemType.Text),
                Url = primaryImagePath is null && TryDetectUrl(mergedBody, out string? detectedUrl) ? detectedUrl : null,
                ImagePath = primaryImagePath,
                Attachments = mergedAttachments,
                Tags = mergedTags,
                AppearancePreset = sources[0].AppearancePreset,
                SourceKind = QuickCaptureSourceKind.Manual,
                IsPinned = anyPinned,
                SortOrder = 0,
                PinnedSortOrder = anyPinned ? 0 : -1,
                CreatedAt = now,
                UpdatedAt = now
            };

            _data.Items.Insert(0, mergedItem);
            NormalizeSortOrders(_data.Items);
            NormalizePinnedSortOrders(_data.Items);
            await SaveCoreAsync();
            return new QuickCaptureMergeResult(Clone(mergedItem), sourceSnapshots);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddImageBytesItemAsync(
        byte[] imageBytes,
        string extension = ".png",
        bool pin = false)
    {
        if (imageBytes is not { Length: > 0 })
        {
            return null;
        }

        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedCoreAsync();
            string normalizedExt = NormalizeImageExtension($"capture{extension}");
            string contentHash = ComputeContentHash(imageBytes);
            Directory.CreateDirectory(_store.ImageDirectory);
            string cachedImagePath = Path.Combine(_store.ImageDirectory, $"{contentHash}{normalizedExt}");
            if (!File.Exists(cachedImagePath))
            {
                await File.WriteAllBytesAsync(cachedImagePath, imageBytes);
            }

            await CreateImageThumbnailAsync(cachedImagePath);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var item = new QuickCaptureItem
            {
                Body = "Image",
                ContentFormat = TextContentFormat.PlainText,
                Type = QuickCaptureItemType.Image,
                ImagePath = cachedImagePath,
                ContentHash = contentHash,
                Attachments = [CreateImageAttachment(cachedImagePath, now)],
                SourceKind = QuickCaptureSourceKind.Clipboard,
                IsPinned = pin,
                IsRecent = false,
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
            CleanupUnusedImageCacheCore();
            return Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuickCaptureItem?> AddImageBytesAttachmentAsync(
        string itemId,
        byte[] imageBytes,
        string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(itemId) || imageBytes is not { Length: > 0 })
        {
            return null;
        }

        string fileName = string.IsNullOrWhiteSpace(displayName)
            ? $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            : displayName;
        if (!IsImageFile(fileName))
        {
            fileName += ".png";
        }

        await using var memory = new MemoryStream(imageBytes, writable: false);
        TodoAttachment? attachment = await AttachmentStorageService.SaveStreamAsync(
            memory,
            fileName,
            Path.Combine(_store.AttachmentDirectory, itemId));
        if (attachment is null)
        {
            return null;
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

            item.Attachments.Add(attachment);
            item.ImagePath ??= attachment.FilePath;
            if (!string.IsNullOrWhiteSpace(item.ImagePath) && string.IsNullOrWhiteSpace(item.Body))
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

    public static async Task<byte[]?> TryExtractClipboardPngBytesAsync(
        DataPackageView? dataView,
        long maxImageBytes = 32L * 1024 * 1024)
    {
        if (dataView is not null && dataView.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                IRandomAccessStreamReference bmpRef = await dataView.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await bmpRef.OpenReadAsync();
                if (stream.Size > 0 && (long)stream.Size <= maxImageBytes * 4)
                {
                    byte[]? encoded = await TranscodeStreamToPngBytesAsync(stream);
                    if (encoded is { Length: > 0 })
                    {
                        return encoded;
                    }
                }
            }
            catch
            {
                // Fall back to Win32 CF_DIB probe below.
            }
        }

        try
        {
            if (ClipboardPlatformInterop.TryReadClipboardDibAsBmpOnce(maxImageBytes, out byte[]? dibBmpBytes) &&
                dibBmpBytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(dibBmpBytes, writable: false);
                using IRandomAccessStream winrtStream = ms.AsRandomAccessStream();
                return await TranscodeStreamToPngBytesAsync(winrtStream);
            }
        }
        catch
        {
            // Ignore clipboard contention.
        }

        return null;
    }

    private static async Task<byte[]?> TranscodeStreamToPngBytesAsync(IRandomAccessStream inputStream)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);
        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using var outputStream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetSoftwareBitmap(softwareBitmap);
        await encoder.FlushAsync();

        if (outputStream.Size == 0)
        {
            return null;
        }

        byte[] bytes = new byte[outputStream.Size];
        using Stream netStream = outputStream.AsStreamForRead();
        netStream.Position = 0;
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await netStream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset));
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == bytes.Length ? bytes : bytes[..offset];
    }
}
