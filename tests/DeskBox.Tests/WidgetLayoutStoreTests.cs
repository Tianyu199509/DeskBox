using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Device-layer domain contract for <see cref="WidgetLayoutStore"/> and the
/// 2B migration: widget layout lives in its own file under
/// DeskBox.Core.Persistence, migrates out of settings.json fail-closed, and
/// stays out of every sync projection.
/// </summary>
public sealed class WidgetLayoutStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "deskbox-layout-store-tests", Guid.NewGuid().ToString("N"));

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

    private string LayoutPath => Path.Combine(_tempRoot, "widget-layout.json");
    private string SettingsPath => Path.Combine(_tempRoot, "settings.json");

    private static WidgetLayoutSettingsSlice CreateSeed(string widgetId, int groupCount = 0) =>
        new()
        {
            Widgets = [new WidgetConfig { Id = widgetId, WidgetKind = WidgetKind.Todo }],
            WidgetGroups = Enumerable
                .Range(0, groupCount)
                .Select(_ => new WidgetGroupConfig())
                .ToList(),
            FeatureWidgetEnabledStates = new Dictionary<string, bool> { ["Todo"] = true },
            DeletedWidgetIds = [$"tomb-{widgetId}"],
        };

    private static Func<string, Task> WriterFor(WidgetLayoutSettingsSlice slice) =>
        async tempPath =>
        {
            await using var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(
                stream,
                new WidgetLayoutDocument { Layout = slice },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
            stream.Flush(flushToDisk: true);
        };

    private static async Task<JsonObject> ReadObjectAsync(string path)
    {
        byte[] json = await File.ReadAllBytesAsync(path);
        return JsonNode.Parse(json)!.AsObject();
    }

    // ── Three-domain ownership contract ────────────────────────────────

    [Fact]
    public void SettingsWireKeys_AreExactlyTheElevenLayoutMembers()
    {
        // The strip set IS the migration surface: a member added to the slice
        // without a conscious ownership decision fails here, and a facade key
        // renamed without updating the slice fails here too.
        string[] expected =
        [
            "featureWidgetEnabledStates",
            "widgets",
            "widgetGroups",
            "widgetTopologyLayouts",
            "activeWidgetTopologyKey",
            "widgetGroupsEnabled",
            "widgetGroupDefaultNavigationStyle",
            "widgetGroupDefaultTitleDisplayMode",
            "widgetGroupWheelSwitchEnabled",
            "widgetGroupHoverSwitchEnabled",
            "deletedWidgetIds",
        ];

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            WidgetLayoutStore.SettingsWireKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LayoutFile_IsNeverInAnyCloudBackupDomain()
    {
        foreach (CloudBackupDomain scope in Enum.GetValues<CloudBackupDomain>())
        {
            Assert.False(
                CloudBackupDomains.IsInScope(scope, "widget-layout.json"),
                $"widget-layout.json must stay device-local (scope {scope})");
        }
    }

    [Fact]
    public void SliceHasExactlyTheExpectedMembers()
    {
        // A 12th member is a device-vs-user-data decision, not a casual add:
        // it lands in the file AND the settings strip set automatically, so
        // this pins the count.
        Assert.Equal(
            11,
            typeof(WidgetLayoutSettingsSlice).GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public).Length);
    }

    // ── Store behavior ─────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_AdoptsSeed_WritesFile_ThenAuthoritative()
    {
        var store = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutSettingsSlice seed = CreateSeed("w1", groupCount: 1);

        WidgetLayoutLoadResult result = await store.LoadAsync(seed);

        Assert.True(result.Authoritative);
        Assert.True(store.IsAuthoritative);
        Assert.True(File.Exists(LayoutPath));
        Assert.Same(seed, result.Data);

        JsonObject doc = await ReadObjectAsync(LayoutPath);
        Assert.Equal(1, doc["schemaVersion"]!.GetValue<int>());
        JsonObject layout = doc["layout"]!.AsObject();
        Assert.Single(layout["widgets"]!.AsArray());
        Assert.Single(layout["widgetGroups"]!.AsArray());
        Assert.True(layout["featureWidgetEnabledStates"]!["Todo"]!.GetValue<bool>());

        // The file — not the seed — is now the authority.
        seed.Widgets.Clear();
        var reloaded = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult second = await reloaded.LoadAsync(CreateSeed("other"));
        Assert.True(second.Authoritative);
        Assert.Equal("w1", Assert.Single(second.Data.Widgets).Id);
    }

    [Fact]
    public async Task LoadAsync_FileWinsOverStaleLegacySeed()
    {
        var store = new WidgetLayoutStore(LayoutPath);
        await store.LoadAsync(CreateSeed("durable"));
        Assert.True(File.Exists(LayoutPath));

        var reloaded = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await reloaded.LoadAsync(CreateSeed("stale"));

        Assert.True(result.Authoritative);
        Assert.Equal("durable", Assert.Single(result.Data.Widgets).Id);
    }

    [Fact]
    public async Task LoadAsync_UnwritableAdoption_StaysPending_KeepsSeed()
    {
        // A directory where the layout FILE should be makes every write fail.
        Directory.CreateDirectory(LayoutPath);
        var store = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutSettingsSlice seed = CreateSeed("legacy");

        WidgetLayoutLoadResult result = await store.LoadAsync(seed);

        Assert.False(result.Authoritative);
        Assert.False(store.IsAuthoritative);
        Assert.Same(seed, result.Data);
        Assert.Single(result.Data.Widgets);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_StaysPending_PreservesQuarantine()
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(LayoutPath, "{ not json");

        var store = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await store.LoadAsync(CreateSeed("rescued"));

        // Fail-closed: the corrupt primary was quarantined for forensics, and
        // the store must NOT stamp a fresh file over the only surviving copy.
        Assert.False(result.Authoritative);
        Assert.Single(result.Data.Widgets);
        Assert.NotEmpty(Directory.GetFiles(_tempRoot, "*.corrupt-*"));
        Assert.False(File.Exists(LayoutPath));
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_RecoversFromBackup()
    {
        var store = new WidgetLayoutStore(LayoutPath);
        await store.LoadAsync(CreateSeed("durable"));
        Assert.True(await store.SaveCheckedAsync(WriterFor(CreateSeed("durable"))));
        Assert.True(File.Exists($"{LayoutPath}.bak"));

        await File.WriteAllTextAsync(LayoutPath, "{ not json");

        var reloaded = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await reloaded.LoadAsync(CreateSeed("stale"));

        // .bak restores the last-good layout; the corrupt primary is
        // preserved for forensics.
        Assert.True(result.Authoritative);
        Assert.Equal("durable", Assert.Single(result.Data.Widgets).Id);
        Assert.NotEmpty(Directory.GetFiles(_tempRoot, "*.corrupt-*"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"layout":null}""")]
    public async Task LoadAsync_StructurallyEmptyFile_StaysPending_KeepsSeed(string content)
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(LayoutPath, content);

        var store = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await store.LoadAsync(CreateSeed("rescued"));

        // Same fail-closed contract as torn JSON: a parseable-but-empty
        // document must never become an authoritative empty layout — the
        // next save would strip the real keys out of settings.json too.
        Assert.False(result.Authoritative);
        Assert.False(store.IsAuthoritative);
        Assert.Single(result.Data.Widgets);
        Assert.NotEmpty(Directory.GetFiles(_tempRoot, "*.corrupt-*"));
    }

    [Fact]
    public async Task LoadAsync_StructurallyEmptyPrimary_RecoversFromBackup()
    {
        var store = new WidgetLayoutStore(LayoutPath);
        await store.LoadAsync(CreateSeed("durable"));
        Assert.True(await store.SaveCheckedAsync(WriterFor(CreateSeed("durable"))));

        await File.WriteAllTextAsync(LayoutPath, "null");

        var reloaded = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await reloaded.LoadAsync(CreateSeed("stale"));

        Assert.True(result.Authoritative);
        Assert.Equal("durable", Assert.Single(result.Data.Widgets).Id);
        Assert.NotEmpty(Directory.GetFiles(_tempRoot, "*.corrupt-*"));
    }

    [Fact]
    public async Task LoadAsync_NullCollections_FlaggedForPersist()
    {
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            LayoutPath,
            """{"schemaVersion":1,"layout":{"widgets":null,"widgetGroups":null}}""");

        var store = new WidgetLayoutStore(LayoutPath);
        WidgetLayoutLoadResult result = await store.LoadAsync(CreateSeed("x"));

        Assert.True(result.Authoritative);
        Assert.True(result.NeedsPersist);
        Assert.NotNull(result.Data.Widgets);
        Assert.NotNull(result.Data.WidgetGroups);
    }

    // ── SettingsService integration ────────────────────────────────────

    [Fact]
    public async Task SettingsService_AdoptsLayout_ThenStripsKeysOnSave()
    {
        var settings = new AppSettings();
        settings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        settings.FeatureWidgetEnabledStates["Todo"] = true;
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();

        Assert.True(service.Layout.IsAuthoritative);
        Assert.True(File.Exists(LayoutPath));
        Assert.Equal("w1", Assert.Single(service.Settings.Widgets).Id);

        await service.SaveAsync();

        JsonObject saved = await ReadObjectAsync(SettingsPath);
        foreach (string key in WidgetLayoutStore.SettingsWireKeys)
        {
            Assert.False(saved.ContainsKey(key), $"settings.json still carries '{key}'");
        }
        Assert.True(saved.ContainsKey("theme"));
        Assert.True(saved.ContainsKey("schemaVersion"));

        JsonObject layout = await ReadObjectAsync(LayoutPath);
        Assert.Equal("w1",
            layout["layout"]!["widgets"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("Todo",
            layout["layout"]!["widgets"]![0]!["widgetKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task SettingsService_LayoutRoundTrip_SurvivesReload()
    {
        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();
        service.Settings.Widgets.Add(new WidgetConfig { Id = "persisted", WidgetKind = WidgetKind.File });
        await service.SaveAsync();

        var reloaded = new SettingsService(_tempRoot);
        await reloaded.LoadAsync();

        Assert.Equal("persisted", Assert.Single(reloaded.Settings.Widgets).Id);
    }

    [Fact]
    public async Task SettingsService_PendingAdoption_KeepsKeysInSettingsJson()
    {
        // The layout path is a directory → the adoption write always fails.
        Directory.CreateDirectory(LayoutPath);
        var settings = new AppSettings();
        settings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();
        Assert.False(service.Layout.IsAuthoritative);
        Assert.Single(service.Settings.Widgets);

        await service.SaveAsync();

        // Fail-closed: layout data still lives (and round-trips) through
        // settings.json until the adoption write can succeed.
        JsonObject saved = await ReadObjectAsync(SettingsPath);
        Assert.Single(saved["widgets"]!.AsArray());
        Assert.Equal("w1", saved["widgets"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task SettingsService_FileWinsOverStaleSettingsKeys()
    {
        // Layout file exists with real data while settings.json still carries
        // different legacy keys (the crash-window state) — the file must win.
        var store = new WidgetLayoutStore(LayoutPath);
        await store.LoadAsync(CreateSeed("durable"));

        var settings = new AppSettings();
        settings.Widgets.Add(new WidgetConfig { Id = "stale", WidgetKind = WidgetKind.File });
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();

        Assert.Equal("durable", Assert.Single(service.Settings.Widgets).Id);
    }

    [Fact]
    public async Task SettingsService_LayoutWriteFailure_KeepsKeysInSettingsJson()
    {
        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();
        Assert.True(service.Layout.IsAuthoritative);
        Assert.True(File.Exists(LayoutPath));

        // Break the layout path: the next layout commit cannot land, so the
        // slice exists only in memory — settings.json must keep carrying the
        // keys (fail-closed) rather than strip them into the void.
        File.Delete(LayoutPath);
        Directory.CreateDirectory(LayoutPath);

        service.Settings.Widgets.Add(new WidgetConfig { Id = "survivor", WidgetKind = WidgetKind.Todo });
        bool saved = await service.SaveCheckedAsync(notifySubscribers: false);

        Assert.False(saved);
        JsonObject settings = await ReadObjectAsync(SettingsPath);
        Assert.Equal("survivor", settings["widgets"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task SettingsService_FailedAdoption_RetriesOnNextLaunch()
    {
        // First launch: the layout path is a directory, so adoption cannot
        // commit — pending, data stays in settings.json.
        Directory.CreateDirectory(LayoutPath);
        var settings = new AppSettings();
        settings.Widgets.Add(new WidgetConfig { Id = "retry", WidgetKind = WidgetKind.Todo });
        await File.WriteAllTextAsync(
            SettingsPath,
            JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));

        var first = new SettingsService(_tempRoot);
        await first.LoadAsync();
        Assert.False(first.Layout.IsAuthoritative);

        // Next launch: the obstruction is gone; DefaultMissing adopts again.
        Directory.Delete(LayoutPath);
        var second = new SettingsService(_tempRoot);
        await second.LoadAsync();

        Assert.True(second.Layout.IsAuthoritative);
        Assert.Equal("retry", Assert.Single(second.Settings.Widgets).Id);
        JsonObject doc = await ReadObjectAsync(LayoutPath);
        Assert.Equal("retry", doc["layout"]!["widgets"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task SettingsService_NewerSchemaLayout_IsNeverOverwritten()
    {
        // A file stamped by a future build still loads (its known fields are
        // authoritative) but must stay pristine: the typed slice cannot
        // represent "futureField", so writing it back would silently destroy
        // data the next build expects.
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(
            LayoutPath,
            """
            {"schemaVersion":99,"futureField":42,"layout":{"widgets":[{"id":"future","widgetKind":"Todo"}]}}
            """);

        var service = new SettingsService(_tempRoot);
        await service.LoadAsync();

        Assert.True(service.Layout.IsAuthoritative);
        Assert.False(service.Layout.CanWrite);
        Assert.Equal("future", Assert.Single(service.Settings.Widgets).Id);

        // Layout mutations cannot persist anywhere while the file is
        // read-only — the save must report failure (and keep the redundant
        // settings keys) instead of a false success that discards them.
        Assert.False(await service.SaveCheckedAsync(notifySubscribers: false));
        Assert.NotNull(service.LastPersistenceFailure);

        string layout = await File.ReadAllTextAsync(LayoutPath);
        Assert.Contains("\"schemaVersion\":99", layout);
        Assert.Contains("\"futureField\":42", layout);

        JsonObject saved = await ReadObjectAsync(SettingsPath);
        Assert.True(saved.ContainsKey("widgets"));
    }
}
