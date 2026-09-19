using DeskBox.Core.Persistence;
using DeskBox.Sync;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Outbox/state/revisions store contract for the 2C PR-1 plumbing
/// (sync-protocol-contract §6.3/§8): the queue is durable across process
/// boundaries, one corrupt entry cannot wedge it, and every file stays
/// inside data/sync/.
/// </summary>
public sealed class SyncOutboxStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "deskbox-sync-store-tests", Guid.NewGuid().ToString("N"));

    private string OutboxDir => Path.Combine(_tempRoot, "outbox");
    private string StatePath => Path.Combine(_tempRoot, "state.json");
    private string RevisionsPath => Path.Combine(_tempRoot, "revisions.json");

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

    private static SyncEnvelope Envelope(
        string domain = SyncDomains.TodoData,
        string collection = "widget-1",
        string entity = "entity-1",
        string operation = "op-1") =>
        new()
        {
            Domain = domain,
            CollectionId = collection,
            EntityId = entity,
            SchemaVersion = 1,
            DeviceId = "device-a",
            OperationId = operation
        };

    // ── Outbox durability ─────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_SurvivesAcrossStoreInstances()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await store.EnqueueAsync(Envelope(operation: "op-a"));
        await store.EnqueueAsync(Envelope(operation: "op-b", entity: "entity-2"));

        // A killed process leaves the files; the next instance must see
        // exactly the same queue — no loss, no duplication.
        var reloaded = new SyncOutboxStore(OutboxDir);
        IReadOnlyList<SyncEnvelope> pending = await reloaded.ListAsync(SyncDomains.TodoData);

        Assert.Equal(2, pending.Count);
        Assert.Equal(2, reloaded.PendingCount(SyncDomains.TodoData));
        Assert.Contains(pending, e => e.OperationId == "op-a");
        Assert.Contains(pending, e => e.OperationId == "op-b");
    }

    [Fact]
    public async Task Enqueue_SameOperationId_ReplacesEntry()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await store.EnqueueAsync(Envelope(operation: "op-x", entity: "v1"));
        await store.EnqueueAsync(Envelope(operation: "op-x", entity: "v2"));

        IReadOnlyList<SyncEnvelope> pending = await store.ListAsync(SyncDomains.TodoData);
        Assert.Single(pending);
        Assert.Equal("v2", pending[0].EntityId);
    }

    [Fact]
    public async Task Enqueue_RejectsUnknownDomainAndMissingIds()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(Envelope(domain: "settings")));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(Envelope(operation: "")));
    }

    [Fact]
    public async Task List_QuarantinesCorruptEntry_AndContinues()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await store.EnqueueAsync(Envelope(operation: "op-good"));
        string badDir = Directory.CreateDirectory(
            Path.Combine(OutboxDir, SyncDomains.TodoData)).FullName;
        await File.WriteAllTextAsync(Path.Combine(badDir, "op-bad.json"), "{ not json");

        IReadOnlyList<SyncEnvelope> pending = await store.ListAsync(SyncDomains.TodoData);

        Assert.Single(pending);
        Assert.Equal("op-good", pending[0].OperationId);
        Assert.NotEmpty(Directory.GetFiles(badDir, "*.corrupt-*"));
        // A retry sees the same single good entry — the quarantine does not
        // resurrect the bad one.
        Assert.Single(await store.ListAsync(SyncDomains.TodoData));
    }

    [Fact]
    public async Task Dequeue_RemovesEntry_IsIdempotent()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await store.EnqueueAsync(Envelope(operation: "op-done"));

        Assert.True(await store.DequeueAsync(SyncDomains.TodoData, "op-done"));
        Assert.Equal(0, store.PendingCount(SyncDomains.TodoData));
        // Second dequeue is a no-op, not an error — the push may have been
        // confirmed twice.
        Assert.False(await store.DequeueAsync(SyncDomains.TodoData, "op-done"));
    }

    [Fact]
    public async Task Domains_QueueIndependently()
    {
        var store = new SyncOutboxStore(OutboxDir);
        await store.EnqueueAsync(Envelope(domain: SyncDomains.TodoData, operation: "t1"));
        await store.EnqueueAsync(
            Envelope(domain: SyncDomains.QuickCaptureData, collection: "quick-capture", operation: "q1"));

        Assert.Equal(1, store.PendingCount(SyncDomains.TodoData));
        Assert.Equal(1, store.PendingCount(SyncDomains.QuickCaptureData));
        Assert.Equal(0, store.PendingCount(SyncDomains.WidgetStyle));

        await store.DequeueAsync(SyncDomains.TodoData, "t1");
        Assert.Equal(0, store.PendingCount(SyncDomains.TodoData));
        Assert.Equal(1, store.PendingCount(SyncDomains.QuickCaptureData));
    }

    // ── State store ───────────────────────────────────────────────────

    [Fact]
    public async Task StateStore_RoundTripsCursorsEpochAndAccount()
    {
        var store = new SyncStateStore(StatePath);
        var document = new SyncStateDocument
        {
            AccountId = "acct-1",
            Domains =
            {
                [SyncDomains.TodoData] = new SyncDomainState
                {
                    Cursor = "cursor-9", Epoch = 3, LastSyncAtUtc = DateTimeOffset.UtcNow
                }
            }
        };
        Assert.True(await store.SaveCheckedAsync(document));

        SyncStateDocument loaded = await store.LoadAsync();

        Assert.Equal("acct-1", loaded.AccountId);
        Assert.Equal("cursor-9", loaded.Domains[SyncDomains.TodoData].Cursor);
        Assert.Equal(3, loaded.Domains[SyncDomains.TodoData].Epoch);
        Assert.NotNull(loaded.Domains[SyncDomains.TodoData].LastSyncAtUtc);
    }

    [Fact]
    public async Task StateStore_MissingFile_DefaultsEmpty()
    {
        SyncStateDocument loaded = await new SyncStateStore(StatePath).LoadAsync();
        Assert.Null(loaded.AccountId);
        Assert.Empty(loaded.Domains);
    }

    // ── Revisions store ───────────────────────────────────────────────

    [Fact]
    public async Task RevisionsStore_RoundTripsEntityMap()
    {
        var store = new SyncRevisionsStore(RevisionsPath);
        string key = SyncRevisionRecord.Key(SyncDomains.TodoData, "widget-1", "entity-1");
        var document = new SyncRevisionsDocument
        {
            Entities =
            {
                [key] = new SyncRevisionRecord
                {
                    ServerRevision = 42, BaseRevision = 41, OperationId = "op-9"
                }
            }
        };
        Assert.True(await store.SaveCheckedAsync(document));

        SyncRevisionsDocument loaded = await store.LoadAsync();

        Assert.Equal(42, loaded.Entities[key].ServerRevision);
        Assert.Equal(41, loaded.Entities[key].BaseRevision);
        Assert.Equal("op-9", loaded.Entities[key].OperationId);
    }

    [Fact]
    public async Task RevisionsStore_MissingFile_DefaultsEmpty()
    {
        SyncRevisionsDocument loaded = await new SyncRevisionsStore(RevisionsPath).LoadAsync();
        Assert.Empty(loaded.Entities);
    }
}
