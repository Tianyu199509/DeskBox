using System.Text;
using DeskBox.Services.Lyrics;

namespace DeskBox.Tests;

public sealed class LyricsCacheStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests.Lyrics",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PutAndReloadFromDisk_PreservesSyncedLyricsAndUserOffset()
    {
        DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        string cacheDir = Path.Combine(_tempRoot, "cache");
        string key = LyricsCacheStore.ComputeCacheKey("晴天 (Live)", "周杰伦 / 费玉清");

        var writerStore = new LyricsCacheStore(cacheDir, () => now);
        await writerStore.PutAsync(new CachedLyricsEntry
        {
            CacheKey = key,
            Status = CachedLyricsStatus.Synced,
            SyncedLyrics = "[00:01.00]故事的小黄花",
            DurationSeconds = 269,
            UserOffsetMs = -500,
            UpdatedUtcTicks = now.UtcTicks
        });

        // Fresh store instance forces disk read rather than memory LRU hit
        var readerStore = new LyricsCacheStore(cacheDir, () => now.AddDays(10));
        CachedLyricsEntry? loaded = await readerStore.TryGetValidAsync(key);

        Assert.NotNull(loaded);
        Assert.Equal(CachedLyricsStatus.Synced, loaded.Status);
        Assert.Equal("[00:01.00]故事的小黄花", loaded.SyncedLyrics);
        Assert.Equal(269, loaded.DurationSeconds);
        Assert.Equal(-500, loaded.UserOffsetMs);

        int updatedOffset = await readerStore.UpdateUserOffsetAsync(key, deltaMs: -500);
        Assert.Equal(-1000, updatedOffset);
        Assert.Equal(-1000, await readerStore.GetSavedUserOffsetMsAsync(key));
    }

    [Fact]
    public async Task NegativeCacheEntry_ExpiresAfter24HoursWhilePositiveLives30Days()
    {
        DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset currentClock = now;
        var store = new LyricsCacheStore(Path.Combine(_tempRoot, "ttl"), () => currentClock);

        string missKey = LyricsCacheStore.ComputeCacheKey("Unknown Song", "Unknown Artist");
        await store.PutAsync(new CachedLyricsEntry
        {
            CacheKey = missKey,
            Status = CachedLyricsStatus.NotFound,
            UpdatedUtcTicks = now.UtcTicks
        });

        currentClock = now.AddHours(23);
        Assert.NotNull(await store.TryGetValidAsync(missKey));

        currentClock = now.AddHours(25);
        Assert.Null(await store.TryGetValidAsync(missKey));
    }

    [Fact]
    public async Task LocalFileLyricsProvider_ReadsBothUtf8AndGb18030EncodedLrcFiles()
    {
        string lrcDir = Path.Combine(_tempRoot, "local_lrc");
        Directory.CreateDirectory(lrcDir);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gb18030 = Encoding.GetEncoding(54936);

        string gbPath = Path.Combine(lrcDir, "周杰伦 - 晴天.lrc");
        byte[] gbBytes = gb18030.GetBytes("[00:28.90]故事的小黄花\r\n[00:32.10]从出生那年就飘着");
        await File.WriteAllBytesAsync(gbPath, gbBytes);

        string? decodedGb = await LocalFileLyricsProvider.TryReadLocalLrcAsync(
            new LyricsQuery("晴天 (Live)", "周杰伦", string.Empty, TimeSpan.Zero),
            lrcDir);

        Assert.NotNull(decodedGb);
        Assert.Contains("故事的小黄花", decodedGb, StringComparison.Ordinal);

        string utf8Path = Path.Combine(lrcDir, "稻香.lrc");
        await File.WriteAllTextAsync(utf8Path, "[00:10.00]对这个世界如果你有太多的抱怨", new UTF8Encoding(false));

        string? decodedUtf8 = await LocalFileLyricsProvider.TryReadLocalLrcAsync(
            new LyricsQuery("稻香", "周杰伦", string.Empty, TimeSpan.Zero),
            lrcDir);

        Assert.NotNull(decodedUtf8);
        Assert.Contains("对这个世界如果你有太多的抱怨", decodedUtf8, StringComparison.Ordinal);
    }
}
