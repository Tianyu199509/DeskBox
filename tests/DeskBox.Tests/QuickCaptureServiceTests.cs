using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickCaptureServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _storeRoot;

    public QuickCaptureServiceTests()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _storeRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "quick-capture")).FullName;
    }

    [Fact]
    public async Task AddItemAsync_TrimsTextAndPersistsNewestFirst()
    {
        var service = CreateService();

        var first = await service.AddItemAsync(" first note ");
        var second = await service.AddItemAsync("second note");

        var reloaded = CreateService();
        var data = await reloaded.GetDataAsync();

        Assert.Collection(
            data.Items,
            item =>
            {
                Assert.Equal(second.Id, item.Id);
                Assert.Equal("second note", item.Body);
                Assert.Equal(0, item.SortOrder);
            },
            item =>
            {
                Assert.Equal(first.Id, item.Id);
                Assert.Equal("first note", item.Body);
                Assert.Equal(1, item.SortOrder);
            });
    }

    [Fact]
    public async Task AddItemAsync_DetectsHttpLinks()
    {
        var service = CreateService();

        var item = await service.AddItemAsync("https://example.com/path?q=1");

        Assert.Equal(QuickCaptureItemType.Link, item.Type);
        Assert.Equal("https://example.com/path?q=1", item.Url);
    }

    [Fact]
    public async Task ManualCreationMethods_CanCreatePinnedRecordsInNewestFirstOrder()
    {
        var service = CreateService();
        string imagePath = Path.Combine(_tempRoot, "pinned.png");
        string documentPath = Path.Combine(_tempRoot, "pinned.txt");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(documentPath, "attachment");

        QuickCaptureItem text = await service.AddDetailedItemAsync(
            null,
            "pinned text",
            QuickCaptureAppearancePreset.Default,
            pin: true);
        QuickCaptureItem? image = await service.AddImageFileItemAsync(imagePath, pin: true);
        QuickCaptureItem? attachment = await service.AddItemWithAttachmentsAsync(
            [documentPath],
            copyToManagedStorage: false,
            pin: true);
        QuickCaptureStoreData data = await service.GetDataAsync();

        Assert.NotNull(image);
        Assert.NotNull(attachment);
        Assert.All(data.Items, item => Assert.True(item.IsPinned));
        Assert.Equal(
            new[] { attachment!.Id, image!.Id, text.Id },
            data.Items.OrderBy(item => item.PinnedSortOrder).Select(item => item.Id));
    }

    [Fact]
    public async Task AddDetailedItemAsync_ExtractsInlineDataImagesIntoManagedAttachments()
    {
        var service = CreateService();
        string encoded = Convert.ToBase64String([1, 2, 3, 4, 5]);
        string markdown = $"before ![diagram](data:image/png;base64,{encoded}) after";

        QuickCaptureItem item = await service.AddDetailedItemAsync(
            null,
            markdown,
            QuickCaptureAppearancePreset.Default,
            TextContentFormat.Markdown);

        TodoAttachment attachment = Assert.Single(item.Attachments);
        Assert.DoesNotContain("data:image/", item.Body, StringComparison.Ordinal);
        Assert.Contains($"attachment:{attachment.Id}", item.Body, StringComparison.Ordinal);
        Assert.Equal(TodoAttachment.ManagedStorageMode, attachment.StorageMode);
        Assert.Equal("image", attachment.Type);
        Assert.True(File.Exists(attachment.FilePath));
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(attachment.FilePath));
    }

    [Fact]
    public async Task AddDetailedItemWithResultAsync_TruncatesOversizedBodyAndReportsIt()
    {
        var service = CreateService();
        string body = new string('a', QuickCaptureService.MaxItemBodyCharacters - 1) +
            "😀trailing";

        QuickCaptureWriteResult result = await service.AddDetailedItemWithResultAsync(
            null,
            body,
            QuickCaptureAppearancePreset.Default);

        Assert.True(result.Saved);
        Assert.True(result.WasTruncated);
        Assert.NotNull(result.Item);
        Assert.Equal(QuickCaptureService.MaxItemBodyCharacters - 1, result.Item.Body.Length);
        Assert.False(char.IsHighSurrogate(result.Item.Body[^1]));
        Assert.Equal(result.Item.Body, Assert.Single((await service.GetDataAsync()).Items).Body);
    }

    [Fact]
    public async Task AddDetailedItemWithResultAsync_ExtractsInlineImageBeforeApplyingTextLimit()
    {
        var service = CreateService();
        byte[] imageBytes = new byte[200_000];
        string markdown = $"before ![diagram](data:image/png;base64,{Convert.ToBase64String(imageBytes)}) after";
        Assert.True(markdown.Length > QuickCaptureService.MaxItemBodyCharacters);

        QuickCaptureWriteResult result = await service.AddDetailedItemWithResultAsync(
            null,
            markdown,
            QuickCaptureAppearancePreset.Default,
            TextContentFormat.Markdown);

        Assert.True(result.Saved);
        Assert.False(result.WasTruncated);
        Assert.NotNull(result.Item);
        Assert.Single(result.Item.Attachments);
        Assert.Contains("before ![diagram](attachment:", result.Item.Body, StringComparison.Ordinal);
        Assert.EndsWith(") after", result.Item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDataAsync_MigratesLegacyInlineDataImagesOnlyOnce()
    {
        string encoded = Convert.ToBase64String([9, 8, 7]);
        var store = new QuickCaptureStore(_storeRoot);
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "legacy-inline-image",
                    Body = $"![one](data:image/png;base64,{encoded}) ![two](data:image/png;base64,{encoded})",
                    ContentFormat = TextContentFormat.Markdown
                }
            ]
        });

        QuickCaptureItem migrated = Assert.Single((await CreateService().GetDataAsync()).Items);
        TodoAttachment attachment = Assert.Single(migrated.Attachments);
        Assert.Equal(
            2,
            migrated.Body.Split($"attachment:{attachment.Id}", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("data:image/", migrated.Body, StringComparison.Ordinal);

        QuickCaptureItem reloaded = Assert.Single((await CreateService().GetDataAsync()).Items);
        Assert.Single(reloaded.Attachments);
        Assert.Equal(migrated.Body, reloaded.Body);
    }

    [Fact]
    public async Task GetDataAsync_TruncatesOversizedLegacyBody()
    {
        var store = new QuickCaptureStore(_storeRoot);
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "legacy-oversized-body",
                    Body = new string('x', QuickCaptureService.MaxItemBodyCharacters + 100),
                    ContentFormat = TextContentFormat.PlainText
                }
            ]
        });

        QuickCaptureItem migrated = Assert.Single((await CreateService().GetDataAsync()).Items);
        Assert.Equal(QuickCaptureService.MaxItemBodyCharacters, migrated.Body.Length);

        QuickCaptureItem reloaded = Assert.Single((await CreateService().GetDataAsync()).Items);
        Assert.Equal(migrated.Body, reloaded.Body);
    }

    [Fact]
    public async Task UpdateItemAsync_RefreshesBodyAndType()
    {
        var service = CreateService();
        var item = await service.AddItemAsync("plain text");

        bool updated = await service.UpdateItemAsync(item.Id, "https://example.com/");
        var data = await service.GetDataAsync();

        Assert.True(updated);
        var updatedItem = Assert.Single(data.Items);
        Assert.Equal("https://example.com/", updatedItem.Body);
        Assert.Equal(QuickCaptureItemType.Link, updatedItem.Type);
        Assert.Equal("https://example.com/", updatedItem.Url);
    }

    [Fact]
    public async Task AddDetailedItemAsync_PersistsTitleAppearanceAndSource()
    {
        var service = CreateService();

        QuickCaptureItem item = await service.AddDetailedItemAsync(
            "Meeting notes",
            "Decide the launch date",
            QuickCaptureAppearancePreset.StickyYellow);
        var reloaded = CreateService();
        QuickCaptureStoreData data = await reloaded.GetDataAsync();

        QuickCaptureItem saved = Assert.Single(data.Items);
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal("Meeting notes", saved.Title);
        Assert.Equal("Decide the launch date", saved.Body);
        Assert.Equal(QuickCaptureAppearancePreset.StickyYellow, saved.AppearancePreset);
        Assert.Equal(QuickCaptureSourceKind.Manual, saved.SourceKind);
        Assert.Equal(4, data.Version);
    }

    [Fact]
    public async Task UpdateItemDetailsAsync_UpdatesTitleBodyAndAppearance()
    {
        var service = CreateService();
        QuickCaptureItem item = await service.AddItemAsync("old body");

        bool updated = await service.UpdateItemDetailsAsync(
            item.Id,
            "New title",
            "https://example.com/new",
            QuickCaptureAppearancePreset.Rose);
        QuickCaptureItem saved = Assert.Single((await service.GetDataAsync()).Items);

        Assert.True(updated);
        Assert.Equal("New title", saved.Title);
        Assert.Equal(QuickCaptureItemType.Link, saved.Type);
        Assert.Equal("https://example.com/new", saved.Url);
        Assert.Equal(QuickCaptureAppearancePreset.Rose, saved.AppearancePreset);
    }

    [Fact]
    public async Task MoveItemAsync_PersistsRecordOrder()
    {
        var service = CreateService();
        QuickCaptureItem first = await service.AddItemAsync("first");
        await service.AddItemAsync("second");
        await service.AddItemAsync("third");

        bool moved = await service.MoveItemAsync(first.Id, 0);
        QuickCaptureStoreData data = await service.GetDataAsync();
        string[] orderedBodies = data.Items
            .OrderBy(item => item.SortOrder)
            .Select(item => item.Body)
            .ToArray();

        Assert.True(moved);
        Assert.Equal(["first", "third", "second"], orderedBodies);
    }

    [Fact]
    public async Task Store_NormalizesClipboardItemsToDefaultAppearance()
    {
        var store = new QuickCaptureStore(_storeRoot);
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Version = 1,
            RecentItems =
            [
                new QuickCaptureItem
                {
                    Body = "clipboard text",
                    AppearancePreset = QuickCaptureAppearancePreset.Mint,
                    SourceKind = QuickCaptureSourceKind.Manual
                }
            ]
        });

        QuickCaptureStoreData data = await store.LoadAsync();
        QuickCaptureItem recent = Assert.Single(data.RecentItems);

        Assert.Equal(4, data.Version);
        Assert.Equal(QuickCaptureAppearancePreset.Default, recent.AppearancePreset);
        Assert.Equal(QuickCaptureSourceKind.Clipboard, recent.SourceKind);
        Assert.True(recent.IsRecent);
    }

    [Fact]
    public async Task SetPinnedAsync_PersistsPinnedState()
    {
        var service = CreateService();
        var item = await service.AddItemAsync("pin me");

        bool pinned = await service.SetPinnedAsync(item.Id, true);
        var reloaded = CreateService();
        var data = await reloaded.GetDataAsync();

        Assert.True(pinned);
        Assert.True(Assert.Single(data.Items).IsPinned);
    }

    [Fact]
    public async Task SetPinnedAsync_AssignsNewestPinnedFirst()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");

        await service.SetPinnedAsync(first.Id, true);
        await service.SetPinnedAsync(second.Id, true);
        var data = await service.GetDataAsync();

        var pinnedItems = data.Items
            .Where(item => item.IsPinned)
            .OrderBy(item => item.PinnedSortOrder)
            .ToList();
        Assert.Collection(
            pinnedItems,
            item =>
            {
                Assert.Equal(second.Id, item.Id);
                Assert.Equal(0, item.PinnedSortOrder);
            },
            item =>
            {
                Assert.Equal(first.Id, item.Id);
                Assert.Equal(1, item.PinnedSortOrder);
            });
    }

    [Fact]
    public async Task SetPinnedAsync_BatchUpdatesSelectedItemsWithSingleOrder()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");

        int updated = await service.SetPinnedAsync([first.Id, third.Id], true);
        var data = await service.GetDataAsync();

        Assert.Equal(2, updated);
        Assert.Equal(
            new[] { third.Id, first.Id },
            data.Items.Where(item => item.IsPinned).OrderBy(item => item.PinnedSortOrder).Select(item => item.Id));
        Assert.False(data.Items.Single(item => item.Id == second.Id).IsPinned);
    }

    [Fact]
    public async Task SetAppearanceAsync_BatchUpdatesSelectedItemsOnly()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");

        int updated = await service.SetAppearanceAsync([first.Id, third.Id], QuickCaptureAppearancePreset.Mint);
        var data = await service.GetDataAsync();

        Assert.Equal(2, updated);
        Assert.Equal(QuickCaptureAppearancePreset.Mint, data.Items.Single(item => item.Id == first.Id).AppearancePreset);
        Assert.Equal(QuickCaptureAppearancePreset.Default, data.Items.Single(item => item.Id == second.Id).AppearancePreset);
        Assert.Equal(QuickCaptureAppearancePreset.Mint, data.Items.Single(item => item.Id == third.Id).AppearancePreset);
    }

    [Fact]
    public async Task MovePinnedItemAsync_ReordersPinnedItemsWithoutChangingRecordSortOrder()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");

        await service.SetPinnedAsync(first.Id, true);
        await service.SetPinnedAsync(second.Id, true);
        await service.SetPinnedAsync(third.Id, true);
        bool moved = await service.MovePinnedItemAsync(first.Id, -1);
        var reloaded = CreateService();
        var data = await reloaded.GetDataAsync();

        Assert.True(moved);
        Assert.Equal(new[] { third.Id, second.Id, first.Id }, data.Items.OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, data.Items.OrderBy(item => item.PinnedSortOrder).Select(item => item.Id));
    }

    [Fact]
    public async Task MovePinnedItemAsync_ReturnsFalseAtBoundaries()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");

        await service.SetPinnedAsync(first.Id, true);
        await service.SetPinnedAsync(second.Id, true);

        Assert.False(await service.MovePinnedItemAsync(second.Id, -1));
        Assert.False(await service.MovePinnedItemAsync(first.Id, 1));
    }

    [Fact]
    public async Task MovePinnedItemToIndexAsync_PersistsPinnedOrderWithoutChangingRecordOrder()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");

        await service.SetPinnedAsync(first.Id, true);
        await service.SetPinnedAsync(second.Id, true);
        await service.SetPinnedAsync(third.Id, true);
        bool moved = await service.MovePinnedItemToIndexAsync(first.Id, 0);
        var data = await service.GetDataAsync();

        Assert.True(moved);
        Assert.Equal(
            new[] { third.Id, second.Id, first.Id },
            data.Items.OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.Equal(
            new[] { first.Id, third.Id, second.Id },
            data.Items.OrderBy(item => item.PinnedSortOrder).Select(item => item.Id));
    }

    [Fact]
    public async Task DeleteItemAsync_TombstonesMatchingItemOnly()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        await Task.Delay(5);

        var deleted = await service.DeleteItemAsync(second.Id);
        var data = await service.GetDataAsync();

        Assert.NotNull(deleted);
        Assert.Equal(second.Id, deleted.Item.Id);
        Assert.False(deleted.IsRecent);
        // Read API: the deleted entry is gone from the live view; the
        // untouched record stays visible.
        var remaining = Assert.Single(data.Items.Where(item => !item.IsDeleted));
        Assert.Equal(first.Id, remaining.Id);

        // Underlying store: the delete persists as a tombstone with a
        // refreshed timestamp, not a physical removal.
        QuickCaptureStoreData persisted = await new QuickCaptureStore(_storeRoot).LoadAsync();
        var tombstone = Assert.Single(persisted.Items.Where(item => item.Id == second.Id));
        Assert.True(tombstone.IsDeleted);
        Assert.True(tombstone.UpdatedAt > second.UpdatedAt);
        // Content stripping: the payload leaves the store file with the
        // delete — only id + timestamps remain for merge protection, so a
        // cloud backup of the store cannot leak deleted content.
        Assert.True(string.IsNullOrWhiteSpace(tombstone.Body));
        Assert.True(string.IsNullOrWhiteSpace(tombstone.Title));
        Assert.True(string.IsNullOrWhiteSpace(tombstone.ImagePath));
        Assert.Empty(tombstone.Attachments);
        var untouched = Assert.Single(persisted.Items.Where(item => item.Id == first.Id));
        Assert.False(untouched.IsDeleted);
    }

    [Fact]
    public async Task DeleteItemsAsync_TombstonesSelectedRecordsAndNormalizesOrder()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");
        await Task.Delay(5);

        var deleted = await service.DeleteItemsAsync([first.Id, third.Id], isRecent: false);
        var data = await service.GetDataAsync();

        Assert.Equal(2, deleted.Count);
        // Read API: only the untouched record stays visible.
        var remaining = Assert.Single(data.Items.Where(item => !item.IsDeleted));
        Assert.Equal(second.Id, remaining.Id);
        // Sort orders stay normalized across the whole list (live + tombstones).
        Assert.Equal(
            Enumerable.Range(0, data.Items.Count),
            data.Items.Select(item => item.SortOrder));

        // Underlying store: both deletes persist as tombstones with
        // refreshed timestamps; the untouched record stays live.
        QuickCaptureStoreData persisted = await new QuickCaptureStore(_storeRoot).LoadAsync();
        Assert.False(persisted.Items.Single(item => item.Id == second.Id).IsDeleted);
        foreach (var original in new[] { first, third })
        {
            var tombstone = Assert.Single(persisted.Items.Where(item => item.Id == original.Id));
            Assert.True(tombstone.IsDeleted);
            Assert.True(tombstone.UpdatedAt > original.UpdatedAt);
            // Content stripping: deletes must not leave payload in the file.
            Assert.True(string.IsNullOrWhiteSpace(tombstone.Body));
            Assert.Empty(tombstone.Attachments);
        }
    }

    [Fact]
    public async Task RestoreDeletedItemAsync_RestoresDeletedRecord()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");

        var deleted = await service.DeleteItemAsync(second.Id);
        bool restored = await service.RestoreDeletedItemAsync(deleted);
        var data = await service.GetDataAsync();

        Assert.True(restored);
        Assert.Equal(new[] { second.Id, first.Id }, data.Items.OrderBy(item => item.SortOrder).Select(item => item.Id));
    }

    [Fact]
    public async Task UndoDelete_RestoresItemOverTombstone()
    {
        var service = CreateService();
        var item = await service.AddItemAsync("undo me");

        var deleted = await service.DeleteItemAsync(item.Id);
        var tombstone = (await service.GetDataAsync())
            .Items.Single(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
        Assert.True(tombstone.IsDeleted);
        await Task.Delay(5);

        bool restored = await service.RestoreDeletedItemAsync(deleted);
        var data = await service.GetDataAsync();

        Assert.True(restored);
        // The undo resurrects the tombstone in place: exactly one entry
        // remains and it is live — no tombstone left behind.
        QuickCaptureItem resurrected = Assert.Single(data.Items);
        Assert.Equal(item.Id, resurrected.Id);
        Assert.Equal("undo me", resurrected.Body);
        Assert.False(resurrected.IsDeleted);
        Assert.True(resurrected.UpdatedAt > tombstone.UpdatedAt);

        // Persisted: the store holds the live record, not a tombstone.
        QuickCaptureStoreData persisted = await new QuickCaptureStore(_storeRoot).LoadAsync();
        QuickCaptureItem stored = Assert.Single(persisted.Items.Where(entry => entry.Id == item.Id));
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task ClearAsync_ReplacesAllItemsWithTombstoneStubs()
    {
        var store = new QuickCaptureStore(_storeRoot);
        var recentSeed = new QuickCaptureItem
        {
            Id = "legacy-recent-1",
            Body = "recent",
            IsRecent = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        await store.SaveAsync(new QuickCaptureStoreData
        {
            RecentItems = [recentSeed]
        });

        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        await Task.Delay(5);

        await service.ClearAsync();
        var data = await service.GetDataAsync();

        Assert.Empty(data.Items.Where(item => !item.IsDeleted));
        Assert.Empty(data.RecentItems.Where(item => !item.IsDeleted));

        QuickCaptureStoreData persisted = await new QuickCaptureStore(_storeRoot).LoadAsync();
        Assert.Equal(2, persisted.Items.Count);
        foreach (var original in new[] { first, second })
        {
            var stub = Assert.Single(persisted.Items.Where(item => item.Id == original.Id));
            Assert.True(stub.IsDeleted);
            Assert.True(string.IsNullOrWhiteSpace(stub.Body));
            Assert.True(string.IsNullOrWhiteSpace(stub.Title));
            Assert.Empty(stub.Attachments);
            Assert.Equal(original.CreatedAt, stub.CreatedAt);
            Assert.True(stub.UpdatedAt > original.UpdatedAt);
        }

        var recentStub = Assert.Single(persisted.RecentItems);
        Assert.Equal(recentSeed.Id, recentStub.Id);
        Assert.True(recentStub.IsDeleted);
        Assert.True(recentStub.IsRecent);
        Assert.True(string.IsNullOrWhiteSpace(recentStub.Body));
    }

    [Fact]
    public async Task AddImageFileItemAsync_CachesImageAndExportsFriendlyFile()
    {
        var service = CreateService();
        string sourceImagePath = Path.Combine(_tempRoot, "source.png");
        await File.WriteAllBytesAsync(sourceImagePath, [1, 2, 3, 4]);

        var item = await service.AddImageFileItemAsync(sourceImagePath);
        string? exportPath = await service.CreateImageExportFileAsync(item!, "Capture");
        var data = await service.GetDataAsync();

        Assert.NotNull(item);
        Assert.Equal(QuickCaptureItemType.Image, item!.Type);
        Assert.True(File.Exists(item.ImagePath));
        Assert.NotEqual(sourceImagePath, item.ImagePath);
        Assert.Single(data.Items);

        Assert.NotNull(exportPath);
        Assert.True(File.Exists(exportPath));
        Assert.StartsWith("Capture ", Path.GetFileName(exportPath), StringComparison.Ordinal);
        Assert.EndsWith(".png", exportPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(exportPath));
    }

    [Fact]
    public async Task GetImageCacheInfoAsync_ReportsUnusedImageFiles()
    {
        var service = CreateService();
        string sourcePath = Path.Combine(_tempRoot, "cache-info.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        var image = await service.AddImageFileItemAsync(sourcePath);
        string orphanPath = Path.Combine(_storeRoot, "images", "orphan.png");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllBytesAsync(orphanPath, [9, 9, 9]);

        var info = await service.GetImageCacheInfoAsync();

        Assert.NotNull(image);
        Assert.Equal(2, info.TotalFileCount);
        Assert.Equal(1, info.UnusedFileCount);
        Assert.True(info.TotalBytes >= 7);
        Assert.Equal(3, info.UnusedBytes);
    }

    [Fact]
    public async Task CleanupUnusedImageCacheAsync_DeletesOnlyUnreferencedFiles()
    {
        var service = CreateService();
        string sourcePath = Path.Combine(_tempRoot, "cache-cleanup.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        var image = await service.AddImageFileItemAsync(sourcePath);
        string orphanPath = Path.Combine(_storeRoot, "images", "orphan.png");
        string orphanThumbnailPath = Path.Combine(_storeRoot, "thumbnails", "orphan.png");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(orphanThumbnailPath)!);
        await File.WriteAllBytesAsync(orphanPath, [9, 9, 9]);
        await File.WriteAllBytesAsync(orphanThumbnailPath, [8, 8]);

        var result = await service.CleanupUnusedImageCacheAsync();
        var info = await service.GetImageCacheInfoAsync();

        Assert.NotNull(image);
        Assert.Equal(2, result.DeletedFileCount);
        Assert.Equal(5, result.DeletedBytes);
        Assert.True(File.Exists(image!.ImagePath));
        Assert.False(File.Exists(orphanPath));
        Assert.False(File.Exists(orphanThumbnailPath));
        Assert.Equal(1, info.TotalFileCount);
        Assert.Equal(0, info.UnusedFileCount);
    }

    [Fact]
    public async Task ClearAsync_RemovesImageCache()
    {
        var service = CreateService();
        string sourcePath = Path.Combine(_tempRoot, "cache-clear.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        await service.AddImageFileItemAsync(sourcePath);

        await service.ClearAsync();
        var info = await service.GetImageCacheInfoAsync();

        Assert.Equal(0, info.TotalFileCount);
    }

    [Fact]
    public async Task GetOrCreateImageThumbnailPathAsync_ReturnsThumbnailForValidImage()
    {
        var service = CreateService();
        string sourceImagePath = Path.Combine(_tempRoot, "valid-source.png");
        await File.WriteAllBytesAsync(sourceImagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGNkYPj/nwEJMDGgAQAAMQIEA3mLB4MAAAAASUVORK5CYII="));
        var item = await service.AddImageFileItemAsync(sourceImagePath);

        string? thumbnailPath = await service.GetOrCreateImageThumbnailPathAsync(item!.ImagePath);

        Assert.False(string.IsNullOrWhiteSpace(thumbnailPath));
        Assert.True(File.Exists(thumbnailPath));
        Assert.StartsWith(Path.Combine(_storeRoot, "thumbnails"), thumbnailPath!, StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public async Task UpdateImageItemDetailsAsync_PreservesImageAndAddsBodyText()
    {
        var service = CreateService();
        string sourceImagePath = Path.Combine(_tempRoot, "image-with-text.png");
        await File.WriteAllBytesAsync(sourceImagePath, [1, 2, 3, 4]);
        var item = await service.AddImageFileItemAsync(sourceImagePath);

        bool updated = await service.UpdateItemDetailsAsync(
            item!.Id,
            title: null,
            body: "图片说明",
            appearancePreset: QuickCaptureAppearancePreset.Default);
        var saved = Assert.Single((await service.GetDataAsync()).Items);

        Assert.True(updated);
        Assert.Equal(QuickCaptureItemType.Image, saved.Type);
        Assert.Equal(item.ImagePath, saved.ImagePath);
        Assert.Equal("图片说明", saved.Body);
    }

    [Fact]
    public async Task ReplaceItemImageAsync_PreservesTextAndReplacesCachedImage()
    {
        var service = CreateService();
        string firstPath = Path.Combine(_tempRoot, "first.png");
        string replacementPath = Path.Combine(_tempRoot, "replacement.png");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(replacementPath, [5, 6, 7, 8]);
        var item = await service.AddImageFileItemAsync(firstPath);
        await service.UpdateItemDetailsAsync(
            item!.Id,
            title: null,
            body: "保留的文字",
            appearancePreset: QuickCaptureAppearancePreset.Default);

        QuickCaptureItem? replaced = await service.ReplaceItemImageAsync(item.Id, replacementPath);
        var saved = Assert.Single((await service.GetDataAsync()).Items);

        Assert.NotNull(replaced);
        Assert.NotEqual(item.ImagePath, saved.ImagePath);
        Assert.Equal("保留的文字", saved.Body);
        Assert.Equal([5, 6, 7, 8], await File.ReadAllBytesAsync(saved.ImagePath!));
        Assert.False(File.Exists(item.ImagePath));
    }

    [Fact]
    public async Task GetOrCreateImageThumbnailPathAsync_ReusesThumbnailForSameImage()
    {
        var service = CreateService();
        string sourceImagePath = Path.Combine(_tempRoot, "same-source.png");
        await File.WriteAllBytesAsync(sourceImagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGNkYPj/nwEJMDGgAQAAMQIEA3mLB4MAAAAASUVORK5CYII="));
        var item = await service.AddImageFileItemAsync(sourceImagePath);

        string? firstThumbnailPath = await service.GetOrCreateImageThumbnailPathAsync(item!.ImagePath);
        string? secondThumbnailPath = await service.GetOrCreateImageThumbnailPathAsync(item.ImagePath);

        Assert.Equal(firstThumbnailPath, secondThumbnailPath);
        Assert.True(File.Exists(firstThumbnailPath));
    }

    [Fact]
    public async Task GetOrCreateImageThumbnailPathAsync_ReturnsNullForMissingImage()
    {
        var service = CreateService();

        string? thumbnailPath = await service.GetOrCreateImageThumbnailPathAsync(
            Path.Combine(_tempRoot, "missing.png"));

        Assert.Null(thumbnailPath);
    }

    [Fact]
    public async Task ClearRecentAsync_TombstonesRecentsWithoutTouchingRecords()
    {
        var store = new QuickCaptureStore(_storeRoot);
        var recentSeed = new QuickCaptureItem
        {
            Id = "legacy-recent-clear",
            Body = "recent",
            IsRecent = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        await store.SaveAsync(new QuickCaptureStoreData
        {
            RecentItems = [recentSeed]
        });

        var service = CreateService();
        var record = await service.AddItemAsync("record");
        await Task.Delay(5);

        await service.ClearRecentAsync();
        var data = await service.GetDataAsync();

        Assert.Empty(data.RecentItems.Where(item => !item.IsDeleted));
        var liveRecord = Assert.Single(data.Items.Where(item => !item.IsDeleted));
        Assert.Equal(record.Id, liveRecord.Id);

        QuickCaptureStoreData persisted = await new QuickCaptureStore(_storeRoot).LoadAsync();
        var storedRecord = Assert.Single(persisted.Items);
        Assert.Equal(record.Id, storedRecord.Id);
        Assert.False(storedRecord.IsDeleted);
        Assert.Equal("record", storedRecord.Body);
        var recentStub = Assert.Single(persisted.RecentItems);
        Assert.Equal(recentSeed.Id, recentStub.Id);
        Assert.True(recentStub.IsDeleted);
        Assert.True(string.IsNullOrWhiteSpace(recentStub.Body));
    }

    [Fact]
    public async Task SetCurrentViewAsync_PersistsCurrentView()
    {
        var service = CreateService();

        await service.SetCurrentViewAsync(QuickCaptureViewMode.Pinned);
        var reloaded = CreateService();
        var data = await reloaded.GetDataAsync();

        Assert.Equal(QuickCaptureViewMode.Pinned, data.CurrentView);
    }

    [Fact]
    public async Task AddItemWithAttachmentsAsync_GroupsMultipleFilesIntoOneRecord()
    {
        string imagePath = Path.Combine(_tempRoot, "first.png");
        string documentPath = Path.Combine(_tempRoot, "notes.pdf");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        await File.WriteAllBytesAsync(documentPath, [4, 5, 6]);
        var service = CreateService();

        QuickCaptureItem? created = await service.AddItemWithAttachmentsAsync(
            [imagePath, documentPath],
            copyToManagedStorage: false);
        QuickCaptureItem saved = Assert.Single((await service.GetDataAsync()).Items);

        Assert.NotNull(created);
        Assert.Equal(2, saved.Attachments.Count);
        Assert.All(saved.Attachments, attachment =>
            Assert.Equal(TodoAttachment.LinkedStorageMode, attachment.StorageMode));
        Assert.Equal(Path.GetFullPath(imagePath), saved.ImagePath);
        Assert.Equal(QuickCaptureItemType.Image, saved.Type);
        Assert.Contains("first.png", saved.Body, StringComparison.Ordinal);
        Assert.Contains("notes.pdf", saved.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAttachmentsAsync_AppendsManagedCopiesToExistingRecord()
    {
        string firstPath = Path.Combine(_tempRoot, "first.txt");
        string secondPath = Path.Combine(_tempRoot, "second.txt");
        await File.WriteAllTextAsync(firstPath, "first");
        await File.WriteAllTextAsync(secondPath, "second");
        var service = CreateService();
        QuickCaptureItem item = await service.AddItemAsync("record");

        QuickCaptureItem? firstUpdate = await service.AddAttachmentsAsync(
            item.Id,
            [firstPath],
            copyToManagedStorage: true);
        QuickCaptureItem? secondUpdate = await service.AddAttachmentsAsync(
            item.Id,
            [secondPath],
            copyToManagedStorage: true);
        QuickCaptureItem saved = Assert.Single((await service.GetDataAsync()).Items);

        Assert.NotNull(firstUpdate);
        Assert.NotNull(secondUpdate);
        Assert.Equal(2, saved.Attachments.Count);
        Assert.All(saved.Attachments, attachment =>
        {
            Assert.Equal(TodoAttachment.ManagedStorageMode, attachment.StorageMode);
            Assert.True(File.Exists(attachment.FilePath));
            Assert.StartsWith(
                Path.Combine(_storeRoot, "attachments", item.Id),
                attachment.FilePath,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Store_MigratesLegacyImagePathToManagedAttachment()
    {
        string imagePath = Path.Combine(_tempRoot, "legacy.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var store = new QuickCaptureStore(_storeRoot);
        string serializedPath = System.Text.Json.JsonSerializer.Serialize(imagePath);
        await File.WriteAllTextAsync(
            store.StorePath,
            $$"""
            {
              "version": 2,
              "items": [
                {
                  "id": "legacy-image",
                  "type": "Image",
                  "body": "Image",
                  "imagePath": {{serializedPath}}
                }
              ]
            }
            """);

        QuickCaptureStoreData data = await store.LoadAsync();
        QuickCaptureItem item = Assert.Single(data.Items);
        TodoAttachment attachment = Assert.Single(item.Attachments);

        Assert.Equal(4, data.Version);
        Assert.Equal(imagePath, attachment.FilePath);
        Assert.Equal(TodoAttachment.ManagedStorageMode, attachment.StorageMode);
        Assert.Equal("image", attachment.Type);
    }

    [Fact]
    public async Task Store_RecoversBackupWhenPrimaryJsonIsCorrupt()
    {
        var store = new QuickCaptureStore(_storeRoot);
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items = [CreateStoredItem("recover", "Recover this note")]
        });
        await store.SaveAsync(new QuickCaptureStoreData
        {
            Items = [CreateStoredItem("latest", "Latest note")]
        });
        await File.WriteAllTextAsync(store.StorePath, "{ invalid json");

        QuickCaptureStoreData recovered = await store.LoadAsync();

        QuickCaptureItem item = Assert.Single(recovered.Items);
        Assert.Equal("recover", item.Id);
        Assert.True(File.Exists(store.StorePath));
        Assert.True(File.Exists(ResilientJsonStore.GetBackupPath(store.StorePath)));
        Assert.Single(Directory.EnumerateFiles(_storeRoot, "quick-capture.json.corrupt-*"));
    }

    [Fact]
    public async Task DeleteAttachmentAsync_FallsBackToNextImage()
    {
        string firstPath = Path.Combine(_tempRoot, "first.png");
        string secondPath = Path.Combine(_tempRoot, "second.jpg");
        await File.WriteAllBytesAsync(firstPath, [1]);
        await File.WriteAllBytesAsync(secondPath, [2]);
        var service = CreateService();
        QuickCaptureItem item = (await service.AddItemWithAttachmentsAsync(
            [firstPath, secondPath],
            copyToManagedStorage: false))!;

        QuickCaptureItem? updated = await service.DeleteAttachmentAsync(
            item.Id,
            item.Attachments[0].Id);

        Assert.NotNull(updated);
        Assert.Single(updated!.Attachments);
        Assert.Equal(Path.GetFullPath(secondPath), updated.ImagePath);
        Assert.Equal(QuickCaptureItemType.Image, updated.Type);
    }

    private QuickCaptureService CreateService()
    {
        return new QuickCaptureService(new QuickCaptureStore(_storeRoot));
    }

    private static QuickCaptureItem CreateStoredItem(string id, string body)
    {
        return new QuickCaptureItem
        {
            Id = id,
            Body = body,
            CreatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z")
        };
    }

    public void Dispose()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
