using DeskBox.Platform;
using DeskBox.Services.DesktopOrganization;

namespace DeskBox.Tests;

public sealed class DesktopIconPositionLedgerStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        "DesktopIconPositionLedger",
        Guid.NewGuid().ToString("N"));

    private string LedgerPath => Path.Combine(_tempRoot, "desktop-icon-positions.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task RecordAndLookup_PersistsAcrossStoreInstances_CaseInsensitively()
    {
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var store = new DesktopIconPositionLedgerStore(LedgerPath, () => now);
        string managedPath = Path.Combine(_tempRoot, "Managed", "Apps", "WeChat.lnk");
        string desktopPath = Path.Combine(_tempRoot, "Desktop", "WeChat.lnk");

        await store.RecordMovesFromDesktopAsync(
        [
            (managedPath, desktopPath, new DesktopIconPoint(2010, 406))
        ]);

        var reloaded = new DesktopIconPositionLedgerStore(LedgerPath, () => now.AddMinutes(5));
        IReadOnlyDictionary<string, DesktopIconPositionRecord> found =
            await reloaded.LookupByCurrentPathsAsync([managedPath.ToUpperInvariant()]);

        DesktopIconPositionRecord record = Assert.Single(found).Value;
        Assert.Equal(2010, record.X);
        Assert.Equal(406, record.Y);
        Assert.Equal(desktopPath, record.OriginalDesktopPath, ignoreCase: true);
    }

    [Fact]
    public async Task RelocatePathsAndDirectoryPrefix_FollowSubsequentMoves()
    {
        DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var store = new DesktopIconPositionLedgerStore(LedgerPath, () => now);
        string desktopPath = Path.Combine(_tempRoot, "Desktop", "WeChat.lnk");
        string widgetAPath = Path.Combine(_tempRoot, "RootA", "BoxA", "WeChat.lnk");
        string widgetBPath = Path.Combine(_tempRoot, "RootA", "BoxB", "WeChat.lnk");
        string migratedDir = Path.Combine(_tempRoot, "RootB", "BoxB");
        string migratedFile = Path.Combine(migratedDir, "WeChat.lnk");

        await store.RecordMovesFromDesktopAsync(
        [
            (widgetAPath, desktopPath, new DesktopIconPoint(500, 300))
        ]);
        await store.RelocatePathsAsync([(widgetAPath, widgetBPath)]);
        await store.RelocateDirectoryPrefixAsync(
            Path.Combine(_tempRoot, "RootA", "BoxB"),
            migratedDir);

        var reloaded = new DesktopIconPositionLedgerStore(LedgerPath, () => now);
        Assert.Empty(await reloaded.LookupByCurrentPathsAsync([widgetAPath, widgetBPath]));
        IReadOnlyDictionary<string, DesktopIconPositionRecord> found =
            await reloaded.LookupByCurrentPathsAsync([migratedFile]);
        DesktopIconPositionRecord record = Assert.Single(found).Value;
        Assert.Equal(500, record.X);
        Assert.Equal(300, record.Y);
    }

    [Fact]
    public async Task PrunesExpiredEntriesAndEnforcesMaxEntryCap()
    {
        DateTimeOffset clock = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new DesktopIconPositionLedgerStore(LedgerPath, () => clock);

        string expiredPath = Path.Combine(_tempRoot, "Managed", "old.lnk");
        await store.RecordMovesFromDesktopAsync(
        [
            (expiredPath, Path.Combine(_tempRoot, "Desktop", "old.lnk"), new DesktopIconPoint(10, 20))
        ]);

        clock = clock.AddDays(31);
        var batch = new List<(string, string, DesktopIconPoint)>(DesktopIconPositionLedgerStore.MaxEntries + 10);
        for (int i = 0; i < DesktopIconPositionLedgerStore.MaxEntries + 10; i++)
        {
            batch.Add((
                Path.Combine(_tempRoot, "Managed", $"item-{i}.lnk"),
                Path.Combine(_tempRoot, "Desktop", $"item-{i}.lnk"),
                new DesktopIconPoint(i * 10, 100)));
        }

        await store.RecordMovesFromDesktopAsync(batch);

        Assert.Empty(await store.LookupByCurrentPathsAsync([expiredPath]));
        Assert.Equal(DesktopIconPositionLedgerStore.MaxEntries, await store.CountForTestingAsync());
    }

    [Fact]
    public async Task CorruptedLedger_RecoversGracefullyAsEmptyWithoutThrowing()
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(LedgerPath, "{ not valid json");

        var store = new DesktopIconPositionLedgerStore(LedgerPath);
        IReadOnlyDictionary<string, DesktopIconPositionRecord> found =
            await store.LookupByCurrentPathsAsync([Path.Combine(_tempRoot, "Managed", "a.lnk")]);

        Assert.Empty(found);
        Assert.Equal(0, await store.CountForTestingAsync());
    }
}
