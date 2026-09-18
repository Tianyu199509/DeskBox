using System.Reflection;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// 2B-1: sync-layer field contract. Local records carry the vocabulary the
/// future sync projection needs: entity_id (Id), updated_at, device_id, and
/// a deletion tombstone (IsDeleted).
/// </summary>
public sealed class SyncLayerFieldsContractTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SyncLayerFields_AreEnumerableOnBothRecords()
    {
        var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Id"] = typeof(string),              // entity_id
            ["CreatedAt"] = typeof(DateTimeOffset),
            ["UpdatedAt"] = typeof(DateTimeOffset), // updated_at
            ["DeviceId"] = typeof(string),        // device_id (nullable)
            ["IsDeleted"] = typeof(bool),         // tombstone
        };

        foreach (Type recordType in new[] { typeof(TodoItem), typeof(QuickCaptureItem) })
        {
            foreach ((string name, Type type) in expected)
            {
                PropertyInfo? prop = recordType.GetProperty(name);
                Assert.NotNull(prop);
                Assert.True(
                    prop.PropertyType == type ||
                    (type == typeof(string) && prop.PropertyType == typeof(string)),
                    $"{recordType.Name}.{name} must be {type.Name} but was {prop.PropertyType.Name}");
            }
        }
    }

    [Fact]
    public void DeviceIdentity_IsStableAndPersisted()
    {
        Directory.CreateDirectory(_tempRoot);
        string first = DeviceIdentity.GetOrCreate(_tempRoot);
        string second = DeviceIdentity.GetOrCreate(_tempRoot);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "device.id")));
    }

    [Fact]
    public void DeviceIdentity_ConcurrentFirstReads_MintSingleId()
    {
        // The ??= read used to race: two threads could each mint a GUID,
        // split device_id across records written in the same process, and
        // race the device.id file write itself.
        Directory.CreateDirectory(_tempRoot);
        DeviceIdentity.DataRootOverride = _tempRoot; // clears the cached value

        var ids = new string[64];
        Parallel.For(0, ids.Length, i => ids[i] = DeviceIdentity.Id);

        Assert.Single(ids.Distinct(StringComparer.Ordinal));
        Assert.Equal(
            ids[0],
            File.ReadAllText(Path.Combine(_tempRoot, "device.id")).Trim());
    }

    [Fact]
    public async Task TodoStore_SaveBackfillsDeviceIdAndKeepsTombstoneSlot()
    {
        var store = new TodoWidgetStore(_tempRoot, "w1");
        var data = new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Text = "sync me", DeviceId = null, IsDeleted = false },
                new TodoItem { Text = "foreign", DeviceId = "other-device-1234" },
            ],
        };
        await store.SaveAsync(data);

        TodoWidgetData loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Items.Count);
        // Empty device_id is backfilled; an existing (foreign) device_id is kept.
        TodoItem local = Assert.Single(loaded.Items, i => i.Text == "sync me");
        TodoItem foreign = Assert.Single(loaded.Items, i => i.Text == "foreign");
        Assert.False(string.IsNullOrWhiteSpace(local.DeviceId));
        Assert.Matches("^[0-9a-f]{32}$", local.DeviceId!);
        Assert.Equal("other-device-1234", foreign.DeviceId);
        Assert.False(local.IsDeleted);
    }

    [Fact]
    public async Task QuickCaptureStore_OldFileLoadsWithDefaultsAndGetsStamped()
    {
        Directory.CreateDirectory(_tempRoot);
        // A pre-2B record: no deviceId member at all.
        File.WriteAllText(
            Path.Combine(_tempRoot, "quick-capture.json"),
            """
            {"version":4,"currentView":"Records","items":[
              {"id":"abc123","type":"Text","body":"hello","isDeleted":false,"sortOrder":0,"createdAt":"2025-01-01T00:00:00+00:00","updatedAt":"2025-01-01T00:00:00+00:00"}
            ],"recentItems":[]}
            """);

        var store = new QuickCaptureStore(_tempRoot);
        QuickCaptureStoreData loaded = await store.LoadAsync();

        Assert.Single(loaded.Items);
        Assert.False(string.IsNullOrWhiteSpace(loaded.Items[0].DeviceId)); // stamped on load+normalize
        Assert.False(loaded.Items[0].IsDeleted);
        Assert.Equal("abc123", loaded.Items[0].Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
