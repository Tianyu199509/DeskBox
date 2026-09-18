using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Local-layer domain contract for <see cref="DesktopOrganizationHistoryStore"/>:
/// the undo receipts live in their own file under the FileSafety domain,
/// migrate out of settings.json fail-closed, and keep the #393 ordering
/// guarantees (checked saves, cap stays with the policy).
/// </summary>
public sealed class DesktopOrganizationHistoryStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "deskbox-history-store-tests", Guid.NewGuid().ToString("N"));

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

    private string StorePath =>
        Path.Combine(_tempRoot, "desktop-organization-history.json");

    private static OrganizationHistoryEntry CreateEntry(string id, int itemCount = 1) =>
        new()
        {
            Id = id,
            TimestampUtc = DateTime.UtcNow,
            ActionType = OrganizationActionType.ManagedDrop,
            Items = Enumerable.Range(0, itemCount).Select(i => new OrganizationHistoryItem
            {
                SourcePath = $"C:\\src{i}.txt",
                DestinationPath = $"C:\\dst{i}.txt",
            }).ToList(),
        };

    [Fact]
    public async Task SaveLoad_RoundTripsEntries()
    {
        var store = new DesktopOrganizationHistoryStore(StorePath);
        await store.LoadAsync();
        store.Entries.Add(CreateEntry("a"));
        store.Entries.Add(CreateEntry("b", itemCount: 3));
        await store.SaveAsync();

        var reloaded = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await reloaded.LoadAsync());
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, entry => entry.Id == "a");
        Assert.Contains(reloaded.Entries,
            entry => entry.Id == "b" && entry.Items.Count == 3);
    }

    [Fact]
    public async Task LoadAsync_AdoptsLegacySeed_AndWritesFileBeforeAuthoritative()
    {
        var seed = new List<OrganizationHistoryEntry>
        {
            CreateEntry("legacy-1"),
            CreateEntry("legacy-2"),
        };
        var store = new DesktopOrganizationHistoryStore(StorePath);

        Assert.True(await store.LoadAsync(seed));
        Assert.True(File.Exists(StorePath));
        Assert.Equal(2, store.Entries.Count);

        // The store file — not the seed — is now the authority.
        seed.Clear();
        var reloaded = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await reloaded.LoadAsync());
        Assert.Equal(2, reloaded.Entries.Count);
    }

    [Fact]
    public async Task LoadAsync_FileWinsOverStaleLegacySeed()
    {
        var store = new DesktopOrganizationHistoryStore(StorePath);
        await store.LoadAsync([CreateEntry("durable")]);
        Assert.True(File.Exists(StorePath));

        // A stale settings copy (pre-migration remnant) must be ignored.
        var reloaded = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await reloaded.LoadAsync([CreateEntry("stale")]));
        Assert.Single(reloaded.Entries);
        Assert.Equal("durable", reloaded.Entries[0].Id);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_RebuildsFromLegacySeed()
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(StorePath, "{ not json");

        var store = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await store.LoadAsync([CreateEntry("rescued")]));
        Assert.Single(store.Entries);

        // The migration re-save repaired the file.
        var reloaded = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await reloaded.LoadAsync());
        Assert.Equal("rescued", Assert.Single(reloaded.Entries).Id);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_RecoversFromBackup()
    {
        var store = new DesktopOrganizationHistoryStore(StorePath);
        await store.LoadAsync();
        store.Entries.Add(CreateEntry("durable"));
        await store.SaveAsync();

        // The second save goes through File.Replace, so .bak now mirrors the
        // last-known-good primary (receipts survive a corrupt primary).
        store.Entries.Add(CreateEntry("newer"));
        await store.SaveAsync();
        Assert.True(File.Exists($"{StorePath}.bak"));

        await File.WriteAllTextAsync(StorePath, "{ not json");

        var reloaded = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await reloaded.LoadAsync());

        // .bak restores the last-good snapshot — "durable" survives, the
        // newest entry written after that backup is honestly lost.
        Assert.Equal("durable", Assert.Single(reloaded.Entries).Id);

        // The corrupt primary is preserved for forensics, not silently
        // overwritten or dropped.
        Assert.NotEmpty(Directory.GetFiles(_tempRoot, "*.corrupt-*"));
    }

    [Fact]
    public async Task SaveCheckedAsync_UnwritablePath_ReportsFalseNotThrows()
    {
        Directory.CreateDirectory(StorePath); // blocks the file write
        var store = new DesktopOrganizationHistoryStore(StorePath);
        store.Entries.Add(CreateEntry("x"));

        Assert.False(await store.SaveCheckedAsync());
    }

    [Fact]
    public void ProductionCode_PersistsReceiptsWithCheckedSaves()
    {
        // A receipt save that throws turns a physically completed file
        // operation into a reported failure (or replaces a transaction's
        // original exception inside the rollback path). Every production call
        // site must use the checked save — the unchecked SaveAsync is for the
        // store's own internals and tests only.
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string[] hits = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            })
            .Where(path => File.ReadAllText(path).Contains(".OrganizationHistory.SaveAsync()"))
            .ToArray();

        Assert.Empty(hits);
    }

    [Fact]
    public async Task LoadAsync_UnwritableMigration_NotAuthoritative_KeepsSeed()
    {
        // A directory where the history FILE should be makes every write fail.
        Directory.CreateDirectory(StorePath);
        var seed = new List<OrganizationHistoryEntry> { CreateEntry("legacy") };
        var store = new DesktopOrganizationHistoryStore(StorePath);

        Assert.False(await store.LoadAsync(seed));
        // Entries are still usable in memory; the caller keeps the legacy list.
        Assert.Single(store.Entries);
    }

    [Fact]
    public async Task LoadAsync_NormalizesWithoutCapping()
    {
        var entries = Enumerable.Range(0, SettingsService.MaxRecentOrganizationHistoryCount + 5)
            .Select(i => CreateEntry($"e{i}"))
            .ToList();
        var store = new DesktopOrganizationHistoryStore(StorePath);
        Assert.True(await store.LoadAsync(entries));

        // Ordering + fill only — never a cap during load (policy owns that,
        // and only inside the no-journal window).
        Assert.Equal(entries.Count, store.Entries.Count);
    }

    [Fact]
    public void SaveChecked_ReportsFailureInsteadOfThrowing()
    {
        Directory.CreateDirectory(StorePath); // blocks the file write
        var store = new DesktopOrganizationHistoryStore(StorePath);
        store.Entries.Add(CreateEntry("x"));

        Assert.False(store.SaveChecked());
    }

    [Fact]
    public async Task SettingsService_LoadAsync_MigratesHistoryOutOfSettingsJson()
    {
        // Seed a legacy settings.json carrying history.
        var settings = new AppSettings();
        settings.RecentOrganizationHistory.Add(CreateEntry("legacy-entry"));
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            Path.Combine(_tempRoot, "settings.json"),
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();

        // Local layer moved out of the sync candidate file.
        Assert.Empty(service.Settings.RecentOrganizationHistory);
        Assert.Single(service.OrganizationHistory.Entries);
        Assert.True(File.Exists(StorePath));

        // And settings.json itself no longer carries the receipt data.
        string json = await File.ReadAllTextAsync(Path.Combine(_tempRoot, "settings.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(
            0,
            doc.RootElement.GetProperty("recentOrganizationHistory").GetArrayLength());
    }
}
