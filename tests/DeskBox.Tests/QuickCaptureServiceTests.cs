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
        Assert.Equal(3, data.Version);
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

        Assert.Equal(3, data.Version);
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
    public async Task DeleteItemAsync_RemovesMatchingItemOnly()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");

        var deleted = await service.DeleteItemAsync(second.Id);
        var data = await service.GetDataAsync();

        Assert.NotNull(deleted);
        Assert.Equal(second.Id, deleted.Item.Id);
        Assert.False(deleted.IsRecent);
        var item = Assert.Single(data.Items);
        Assert.Equal(first.Id, item.Id);
    }

    [Fact]
    public async Task DeleteItemsAsync_RemovesSelectedRecordsAndNormalizesOrder()
    {
        var service = CreateService();
        var first = await service.AddItemAsync("first");
        var second = await service.AddItemAsync("second");
        var third = await service.AddItemAsync("third");

        var deleted = await service.DeleteItemsAsync([first.Id, third.Id], isRecent: false);
        var data = await service.GetDataAsync();

        Assert.Equal(2, deleted.Count);
        var remaining = Assert.Single(data.Items);
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal(0, remaining.SortOrder);
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
    public async Task ClearAsync_RemovesAllItems()
    {
        var service = CreateService();
        await service.AddItemAsync("first");
        await service.AddItemAsync("second");
        await service.AddRecentClipboardItemAsync("recent", QuickCaptureService.DefaultRecentLimit);

        await service.ClearAsync();
        var data = await service.GetDataAsync();

        Assert.Empty(data.Items);
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task AddRecentClipboardItemAsync_TrimsAndPersistsNewestFirst()
    {
        var service = CreateService();

        var first = await service.AddRecentClipboardItemAsync(" first copied ", QuickCaptureService.DefaultRecentLimit);
        var second = await service.AddRecentClipboardItemAsync("https://example.com/path", QuickCaptureService.DefaultRecentLimit);

        var reloaded = CreateService();
        var data = await reloaded.GetDataAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Empty(data.Items);
        Assert.Collection(
            data.RecentItems,
            item =>
            {
                Assert.Equal(second.Id, item.Id);
                Assert.Equal("https://example.com/path", item.Body);
                Assert.Equal(QuickCaptureItemType.Link, item.Type);
                Assert.True(item.IsRecent);
                Assert.Equal(0, item.SortOrder);
            },
            item =>
            {
                Assert.Equal(first.Id, item.Id);
                Assert.Equal("first copied", item.Body);
                Assert.Equal(QuickCaptureItemType.Text, item.Type);
                Assert.True(item.IsRecent);
                Assert.Equal(1, item.SortOrder);
            });
    }

    [Fact]
    public async Task AddRecentClipboardItemAsync_DeduplicatesByBodyAndKeepsNewest()
    {
        var service = CreateService();

        await service.AddRecentClipboardItemAsync("same", QuickCaptureService.DefaultRecentLimit);
        await Task.Delay(5);
        var newest = await service.AddRecentClipboardItemAsync("other", QuickCaptureService.DefaultRecentLimit);
        await Task.Delay(5);
        var duplicate = await service.AddRecentClipboardItemAsync("same", QuickCaptureService.DefaultRecentLimit);
        var data = await service.GetDataAsync();

        Assert.NotNull(newest);
        Assert.NotNull(duplicate);
        Assert.Collection(
            data.RecentItems,
            item =>
            {
                Assert.Equal(duplicate.Id, item.Id);
                Assert.Equal("same", item.Body);
                Assert.Equal(0, item.SortOrder);
            },
            item =>
            {
                Assert.Equal(newest.Id, item.Id);
                Assert.Equal("other", item.Body);
                Assert.Equal(1, item.SortOrder);
            });
    }

    [Fact]
    public async Task AddRecentClipboardItemAsync_IgnoresLatestDuplicate()
    {
        var service = CreateService();

        var first = await service.AddRecentClipboardItemAsync("same", QuickCaptureService.DefaultRecentLimit);
        var duplicate = await service.AddRecentClipboardItemAsync("same", QuickCaptureService.DefaultRecentLimit);
        var data = await service.GetDataAsync();

        Assert.NotNull(first);
        Assert.Null(duplicate);
        Assert.Single(data.RecentItems);
    }

    [Fact]
    public async Task AddRecentClipboardItemAsync_IgnoresTextWrittenByDeskBox()
    {
        var service = CreateService();

        service.MarkClipboardTextWrittenByDeskBox("copied from deskbox");
        var item = await service.AddRecentClipboardItemAsync("copied from deskbox", QuickCaptureService.DefaultRecentLimit);
        var data = await service.GetDataAsync();

        Assert.Null(item);
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task AddRecentClipboardItemAsync_TrimsToLimit()
    {
        var service = CreateService();

        for (int index = 0; index < 12; index++)
        {
            await service.AddRecentClipboardItemAsync($"item {index}", 10);
        }

        var data = await service.GetDataAsync();

        Assert.Equal(10, data.RecentItems.Count);
        Assert.Equal("item 11", data.RecentItems[0].Body);
        Assert.Equal("item 2", data.RecentItems[^1].Body);
        Assert.Equal(Enumerable.Range(0, 10), data.RecentItems.Select(item => item.SortOrder));
    }

    [Fact]
    public async Task SaveRecentItemToRecordsAsync_CreatesRecord()
    {
        var service = CreateService();
        var recent = await service.AddRecentClipboardItemAsync("save me", QuickCaptureService.DefaultRecentLimit);

        var saved = await service.SaveRecentItemToRecordsAsync(recent!.Id, pin: false);
        var data = await service.GetDataAsync();

        Assert.NotNull(saved);
        Assert.False(saved.IsPinned);
        Assert.False(saved.IsRecent);
        Assert.Equal("save me", saved.Body);
        Assert.Single(data.RecentItems);
        var record = Assert.Single(data.Items);
        Assert.Equal(saved.Id, record.Id);
        Assert.False(record.IsPinned);
    }

    [Fact]
    public async Task SaveRecentItemToRecordsAsync_CanPinRecord()
    {
        var service = CreateService();
        var recent = await service.AddRecentClipboardItemAsync("pin me", QuickCaptureService.DefaultRecentLimit);

        var saved = await service.SaveRecentItemToRecordsAsync(recent!.Id, pin: true);
        var data = await service.GetDataAsync();

        Assert.NotNull(saved);
        Assert.True(saved.IsPinned);
        var record = Assert.Single(data.Items);
        Assert.True(record.IsPinned);
        Assert.False(record.IsRecent);
    }

    [Fact]
    public async Task SaveRecentItemToRecordsAsync_PreservesImageMetadata()
    {
        var service = CreateService();
        var recent = await service.AddRecentClipboardImageAsync([1, 2, 3, 4], QuickCaptureService.DefaultRecentLimit);

        var saved = await service.SaveRecentItemToRecordsAsync(recent!.Id, pin: false);
        var data = await service.GetDataAsync();

        Assert.NotNull(saved);
        Assert.Equal(QuickCaptureItemType.Image, saved.Type);
        Assert.False(string.IsNullOrWhiteSpace(saved.ImagePath));
        var record = Assert.Single(data.Items);
        Assert.Equal(saved.ImagePath, record.ImagePath);
        Assert.Equal(saved.ContentHash, record.ContentHash);
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
        var image = await service.AddRecentClipboardImageAsync([1, 2, 3, 4], QuickCaptureService.DefaultRecentLimit);
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
        var image = await service.AddRecentClipboardImageAsync([1, 2, 3, 4], QuickCaptureService.DefaultRecentLimit);
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
        await service.AddRecentClipboardImageAsync([1, 2, 3, 4], QuickCaptureService.DefaultRecentLimit);

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
    public async Task DeleteRecentItemAsync_OnlyRemovesRecentItem()
    {
        var service = CreateService();
        await service.AddItemAsync("record");
        var recent = await service.AddRecentClipboardItemAsync("recent", QuickCaptureService.DefaultRecentLimit);

        var deleted = await service.DeleteRecentItemAsync(recent!.Id);
        var data = await service.GetDataAsync();

        Assert.NotNull(deleted);
        Assert.Equal(recent.Id, deleted.Item.Id);
        Assert.True(deleted.IsRecent);
        Assert.Single(data.Items);
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task ClearRecentAsync_DoesNotClearRecords()
    {
        var service = CreateService();
        await service.AddItemAsync("record");
        await service.AddRecentClipboardItemAsync("recent", QuickCaptureService.DefaultRecentLimit);

        await service.ClearRecentAsync();
        var data = await service.GetDataAsync();

        Assert.Single(data.Items);
        Assert.Empty(data.RecentItems);
    }

    [Fact]
    public async Task TrimRecentItemsAsync_UsesConfiguredLimit()
    {
        var service = CreateService();
        for (int index = 0; index < 12; index++)
        {
            await service.AddRecentClipboardItemAsync($"item {index}", QuickCaptureService.DefaultRecentLimit);
        }

        await service.TrimRecentItemsAsync(10);
        var data = await service.GetDataAsync();

        Assert.Equal(10, data.RecentItems.Count);
        Assert.Equal("item 11", data.RecentItems[0].Body);
        Assert.Equal("item 2", data.RecentItems[^1].Body);
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

        Assert.Equal(3, data.Version);
        Assert.Equal(imagePath, attachment.FilePath);
        Assert.Equal(TodoAttachment.ManagedStorageMode, attachment.StorageMode);
        Assert.Equal("image", attachment.Type);
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

    public void Dispose()
    {
        DeskBoxClipboardWriteScope.ClearForTesting();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
