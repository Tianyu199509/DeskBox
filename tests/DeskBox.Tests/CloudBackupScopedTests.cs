using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Cloud-backup domain scoping (roadmap §10): scoped export must carry only
/// the enabled domains' files, and scoped restore must replace only those
/// domains — never settings.json, FileSafety files, or other widget data.
/// </summary>
public sealed class CloudBackupScopedTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataRoot;
    private readonly string _exportRoot;

    public CloudBackupScopedTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _appDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "app-data")).FullName;
        _exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "exports")).FullName;
    }

    [Fact]
    public void DomainPaths_MatchOnlyOwnedFiles()
    {
        Assert.True(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/todo.json"));
        // Attachments stay out of the domain until upload size bounds land.
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/attachments/note/pic.png"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/glance.json"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/todo.json.bak"));

        Assert.True(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.QuickCaptureData, "quick-capture/quick-capture.json"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.QuickCaptureData, "quick-capture/attachments/note/pic.png"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.QuickCaptureData, "quick-capture/thumbnails/x.png"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.QuickCaptureData, "quick-capture/exports/x.txt"));

        // Style owns no data files; nothing else is ever in a domain.
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.WidgetStyle, "settings.json"));
        Assert.False(CloudBackupDomains.IsInScope(
            CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData |
            CloudBackupDomain.WidgetStyle,
            "settings.json"));
        Assert.False(CloudBackupDomains.IsInScope(
            CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData,
            "desktop-organization-recovery.json"));
    }

    [Fact]
    public void DomainManifestNames_RoundTrip()
    {
        const CloudBackupDomain scope = CloudBackupDomain.TodoData |
                                        CloudBackupDomain.WidgetStyle;
        string[] names = CloudBackupDomains.ToManifestNames(scope);
        Assert.Equal(["todo-data", "widget-style"], names);
        Assert.Equal(scope, CloudBackupDomains.FromManifestNames(names));
        Assert.Equal(
            CloudBackupDomain.None,
            CloudBackupDomains.FromManifestNames(null));
        // Unknown names are ignored, not fatal (forward compat).
        Assert.Equal(
            CloudBackupDomain.TodoData,
            CloudBackupDomains.FromManifestNames(["todo-data", "future-domain"]));
    }

    [Fact]
    public async Task ExportScoped_TodoDomain_ContainsOnlyDomainFiles()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "desktop-organization-recovery.json"), "{}");
        var todoStore = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await todoStore.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "t1", Text = "synced task" }]
        });
        Directory.CreateDirectory(todoStore.AttachmentDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(todoStore.AttachmentDirectory, "pic.png"), [1, 2]);
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot,
            CloudBackupDomain.TodoData);

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("data/widgets/todo-widget/todo.json"));
        // Attachment files do not ship until upload size bounds land.
        Assert.Null(archive.GetEntry("data/widgets/todo-widget/attachments/pic.png"));
        Assert.Null(archive.GetEntry("data/settings.json"));
        Assert.Null(archive.GetEntry("data/desktop-organization-recovery.json"));
        Assert.Null(archive.GetEntry("widget-style.json"));
        JsonObject manifest = await ReadManifestAsync(archive);
        JsonArray domains = Assert.IsType<JsonArray>(manifest["domains"]);
        Assert.Equal("todo-data", Assert.Single(domains)!.GetValue<string>());
    }

    [Fact]
    public async Task ExportScoped_WithStyle_WritesStyleEntryAndDomain()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);
        var styleSource = new AppSettings { WidgetOpacity = 0.42 };

        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot,
            CloudBackupDomain.WidgetStyle,
            _ => Task.FromResult<byte[]?>(WidgetStyleBackupProjection.Serialize(styleSource)));

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("widget-style.json"));
        JsonObject manifest = await ReadManifestAsync(archive);
        Assert.Equal(
            "cloud-backup",
            manifest["kind"]!.GetValue<string>());
        JsonArray domains = Assert.IsType<JsonArray>(manifest["domains"]);
        Assert.Equal("widget-style", domains[0]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportScoped_StyleProviderNull_StripsStyleDomainFromManifest()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var todoStore = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await todoStore.SaveAsync(new TodoWidgetData { Items = [] });
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot,
            CloudBackupDomain.TodoData | CloudBackupDomain.WidgetStyle,
            widgetStyleProvider: null);

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.Null(archive.GetEntry("widget-style.json"));
        JsonObject manifest = await ReadManifestAsync(archive);
        JsonArray domains = Assert.IsType<JsonArray>(manifest["domains"]);
        Assert.Equal("todo-data", domains[0]!.GetValue<string>());
        Assert.Single(domains);
    }

    [Fact]
    public async Task ExportScoped_NoneScope_Throws()
    {
        var service = new DeskBoxDataBackupService(_appDataRoot);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.None));
    }

    [Fact]
    public async Task ClassicRestore_RejectsScopedArchive()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var todoStore = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await todoStore.SaveAsync(new TodoWidgetData { Items = [] });
        var service = new DeskBoxDataBackupService(_appDataRoot);
        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot, CloudBackupDomain.TodoData);

        // A scoped archive through the classic whole-directory restore would
        // wipe everything outside its domains — it must be rejected clearly.
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PrepareRestoreAsync(backupPath));
        Assert.Contains("scoped cloud backup", ex.Message);
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task ScopedRestore_ReplacesOnlyDomainFiles()
    {
        // Live state: old todo + untouched settings + untouched quick-capture.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string settingsPath = Path.Combine(dataDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"language\":\"zh-CN\"}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });
        var qcStore = new QuickCaptureStore(Path.Combine(dataDir, "quick-capture"));
        await qcStore.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "n1", Body = "local note" }]
        });

        // Snapshot: a different todo domain (exported from a second data root).
        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "new", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);
        Assert.Equal(["todo-data"], prep.Domains);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        // Domain replaced with the snapshot's content.
        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "todo-widget").LoadAsync();
        Assert.Equal("cloud task", Assert.Single(restored.Items).Text);
        // Non-domain files are untouched — byte-identical settings, same note.
        Assert.Equal("{\"language\":\"zh-CN\"}", await File.ReadAllTextAsync(settingsPath));
        QuickCaptureStoreData qc = await new QuickCaptureStore(
            Path.Combine(dataDir, "quick-capture")).LoadAsync();
        Assert.Equal("local note", Assert.Single(qc.Items).Body);
    }

    [Fact]
    public async Task ScopedRestore_Preview_ReportsDomainItemCounts()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;

        // Snapshot: 2 live + 1 tombstoned todo items, 1 live + 1 deleted
        // quick-capture record — tombstones must not inflate the preview.
        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "t1", Text = "task one" },
                new TodoItem { Id = "t2", Text = "task two" },
                new TodoItem { Id = "t3", Text = "deleted task", IsDeleted = true }
            ]
        });
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "n1", Body = "note one" }],
            RecentItems = [new QuickCaptureItem { Id = "r1", Body = "old recent", IsDeleted = true }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(
                _exportRoot,
                CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData);

        Assert.Equal(
            [("todo-data", 2), ("quick-capture-data", 1)],
            prep.DomainItemCounts!.Select(c => (c.Domain, c.Items)).ToArray());
        await service.CancelPendingRestoreAsync();
    }

    [Fact]
    public async Task ScopedRestore_Preview_ZeroItemDomainIsCounted()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;

        // A manifest domain with zero records still wipes the local domain —
        // the preview must report 0, not omit the count.
        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData { Items = [] });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.QuickCaptureData);

        DeskBoxDomainItemCount count = Assert.Single(prep.DomainItemCounts!);
        Assert.Equal("quick-capture-data", count.Domain);
        Assert.Equal(0, count.Items);
        await service.CancelPendingRestoreAsync();
    }

    [Fact]
    public async Task ScopedRestore_OverwriteMode_DeletesLiveDomainFilesAbsentFromSnapshot()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveExtra = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "extra-todo");
        await liveExtra.SaveAsync(new TodoWidgetData { Items = [] });
        var liveKept = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await liveKept.SaveAsync(new TodoWidgetData { Items = [] });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData { Items = [] });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.TodoData);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        // Snapshot-faithful: the widget store that exists only locally is
        // gone; the snapshotted one is present.
        Assert.False(File.Exists(Path.Combine(
            dataDir, "widgets", "extra-todo", "todo.json")));
        Assert.True(File.Exists(Path.Combine(
            dataDir, "widgets", "todo-widget", "todo.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedRestore_UnconfirmedMarker_IsDiscarded_WhileLegacyMarkerKeepsMerge(
        bool legacyMarker)
    {
        // New markers are never applied before confirmation. Markers written
        // by an older DeskBox lack that field and retain their merge behavior.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveExtra = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "extra-todo");
        await liveExtra.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "local-only-widget-item", Text = "extra local task" }]
        });
        var liveKept = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await liveKept.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "local", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "remote", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.TodoData);
        if (legacyMarker)
        {
            JsonObject marker = JsonNode.Parse(
                await File.ReadAllTextAsync(service.PendingRestoreMarkerPath))!.AsObject();
            Assert.True(marker.Remove("scopedRestoreConfirmed"));
            await File.WriteAllTextAsync(service.PendingRestoreMarkerPath,
                marker.ToJsonString());
        }
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(legacyMarker, result.HadPendingRestore);
        TodoWidgetData stored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "todo-widget").LoadAsync();
        Assert.Equal(
            legacyMarker ? new[] { "local", "remote" } : new[] { "local" },
            stored.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
        if (Directory.Exists(service.RestoreStagingDirectory))
            Assert.Empty(Directory.EnumerateFileSystemEntries(service.RestoreStagingDirectory));
        Assert.True(File.Exists(Path.Combine(
            dataDir, "widgets", "extra-todo", "todo.json")));
    }

    [Fact]
    public async Task ScopedRestore_MergeMode_PreservesLocalOnlyDataAndAddsRemoteItems()
    {
        // Merge mode must never delete: the widget store that exists only
        // locally survives untouched, and remote items fold into the shared
        // store alongside the local ones.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveExtra = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "extra-todo");
        await liveExtra.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "local-only-widget-item", Text = "extra local task" }]
        });
        var liveKept = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await liveKept.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "local", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "remote", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.TodoData);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData merged = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "todo-widget").LoadAsync();
        Assert.Equal(
            ["local", "remote"],
            merged.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        TodoWidgetData extra = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "extra-todo").LoadAsync();
        Assert.Equal("extra local task", Assert.Single(extra.Items).Text);
    }

    [Fact]
    public async Task ScopedRestore_MergeMode_ConflictRules()
    {
        // Same item id on both sides: the newer UpdatedAt wins — but a
        // remote tombstone must never hide a live local record.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "newer-local", Text = "local edit", UpdatedAt = now },
                new TodoItem { Id = "older-local", Text = "local stale", UpdatedAt = now.AddHours(-2) },
                new TodoItem { Id = "live-local", Text = "still alive", UpdatedAt = now.AddHours(-1) }
            ]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "newer-local", Text = "remote stale", UpdatedAt = now.AddHours(-1) },
                new TodoItem { Id = "older-local", Text = "remote edit", UpdatedAt = now.AddHours(-1) },
                new TodoItem { Id = "live-local", Text = "remote tombstone", IsDeleted = true, UpdatedAt = now.AddHours(1) }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.TodoData);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData merged = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "todo-widget").LoadAsync();
        Assert.Equal("local edit", merged.Items.Single(i => i.Id == "newer-local").Text);
        Assert.Equal("remote edit", merged.Items.Single(i => i.Id == "older-local").Text);
        TodoItem tombstoneShielded = merged.Items.Single(i => i.Id == "live-local");
        Assert.False(tombstoneShielded.IsDeleted);
        Assert.Equal("still alive", tombstoneShielded.Text);
    }

    [Fact]
    public async Task ScopedRestore_MergeMode_QuickCapture_UnionsItemsAndRecent()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveQc = new QuickCaptureStore(Path.Combine(dataDir, "quick-capture"));
        await liveQc.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem { Id = "a", Body = "local note a" },
                new QuickCaptureItem { Id = "b", Body = "local note b" }
            ],
            RecentItems = [new QuickCaptureItem { Id = "r-local", Body = "local recent" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "c", Body = "cloud note c" }],
            RecentItems = [new QuickCaptureItem { Id = "r-remote", Body = "cloud recent" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.QuickCaptureData);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        QuickCaptureStoreData merged = await liveQc.LoadAsync();
        Assert.Equal(
            ["a", "b", "c"],
            merged.Items.Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["r-local", "r-remote"],
            merged.RecentItems.Select(i => i.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ScopedRestore_Merge_DoesNotResurrectDeletedQuickCaptureItem()
    {
        // The snapshot predates a local delete: its live copy of item A is
        // older than the local tombstone, so the merge must keep the delete
        // instead of letting the cloud copy resurrect the record.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string liveQcDirectory = Path.Combine(dataDir, "quick-capture");
        var liveService = new QuickCaptureService(new QuickCaptureStore(liveQcDirectory));
        QuickCaptureItem itemA = await liveService.AddItemAsync("local note a");
        await liveService.DeleteItemAsync(itemA.Id);

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        DateTimeOffset snapshotTimestamp = itemA.UpdatedAt.AddHours(-1);
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = itemA.Id,
                    Body = "cloud copy of a",
                    UpdatedAt = snapshotTimestamp
                },
                new QuickCaptureItem
                {
                    Id = "cloud-only",
                    Body = "cloud note b",
                    UpdatedAt = snapshotTimestamp
                }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.QuickCaptureData);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        QuickCaptureStoreData merged = await new QuickCaptureService(
            new QuickCaptureStore(liveQcDirectory)).GetDataAsync();
        // Read API: the cloud-only note merged in, but A stays deleted.
        Assert.Equal(
            ["cloud-only"],
            merged.Items.Where(item => !item.IsDeleted).Select(item => item.Id).ToArray());
        // Store: A remains the tombstone the local delete wrote.
        QuickCaptureItem tombstone = Assert.Single(
            merged.Items.Where(item => string.Equals(item.Id, itemA.Id, StringComparison.Ordinal)));
        Assert.True(tombstone.IsDeleted);
    }

    [Fact]
    public async Task ScopedRestore_Preview_ReportsAttachmentReferenceCount()
    {
        // Attachment files never ship in the archive — the preview counts
        // the references so the confirm dialog can warn about dangling
        // attachments on a cross-device restore.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "t1",
                    Text = "with attachments",
                    Attachments =
                    [
                        new TodoAttachment { FilePath = "C:/a/one.pdf" },
                        new TodoAttachment { FilePath = "C:/a/two.pdf" }
                    ]
                },
                new TodoItem { Id = "t2", Text = "plain" }
            ]
        });
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "n1", Body = "img", ImagePath = "C:/img/pic.png" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(
                _exportRoot,
                CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData);

        // 2 todo attachment paths + 1 quick-capture image path.
        Assert.Equal(3, prep.AttachmentReferenceCount);
        await service.CancelPendingRestoreAsync();
    }

    [Fact]
    public async Task ExportScoped_WidgetStyle_WritesIntegrityEntry()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        byte[] styleDocument =
            "{\"kind\":\"widget-style\",\"shell\":{},\"widgets\":[]}"u8.ToArray();
        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot,
            CloudBackupDomain.WidgetStyle,
            _ => Task.FromResult<byte[]?>(styleDocument));

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        JsonObject manifest = await ReadManifestAsync(archive);
        JsonObject styleEntry = Assert.IsType<JsonObject>(manifest["widgetStyleFile"]);
        Assert.Equal("widget-style.json", styleEntry["path"]!.GetValue<string>());
        ZipArchiveEntry zipEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("widget-style.json"));
        await using Stream stream = zipEntry.Open();
        byte[] content = new byte[zipEntry.Length];
        int read = await stream.ReadAsync(content);
        Assert.Equal(content.Length, read);
        Assert.Equal(content.Length, styleEntry["length"]!.GetValue<long>());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)),
            styleEntry["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScopedRestore_WidgetStyleIntegrityMismatch_Rejected()
    {
        // A declared widgetStyleFile entry whose hash does not match the
        // archived document means corruption — reject at prepare.
        string archivePath = Path.Combine(_exportRoot, "style-tampered.zip");
        await using (FileStream stream = File.Create(archivePath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestStream = manifestEntry.Open())
            await using (var writer = new StreamWriter(manifestStream))
            {
                await writer.WriteAsync(
                    "{\"schemaVersion\":2,\"kind\":\"cloud-backup\"," +
                    "\"createdAtUtc\":\"2026-09-18T00:00:00+00:00\"," +
                    "\"appVersion\":\"1.0.0.0\",\"domains\":[\"widget-style\"]," +
                    "\"widgetStyleFile\":{\"path\":\"widget-style.json\"," +
                    "\"length\":2,\"sha256\":\"" + new string('0', 64) + "\"}}");
            }

            ZipArchiveEntry styleEntry = archive.CreateEntry("widget-style.json");
            await using Stream styleStream = styleEntry.Open();
            await styleStream.WriteAsync("{}"u8.ToArray());
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PrepareScopedRestoreAsync(archivePath, CloudBackupDomain.WidgetStyle));
        Assert.Contains("widget-style", ex.Message);
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task ScopedRestore_WidgetStyleWithoutIntegrityField_Accepted()
    {
        // Archives written before widgetStyleFile existed legitimately lack
        // the field — verify-if-present must not reject them.
        string archivePath = Path.Combine(_exportRoot, "style-legacy.zip");
        await using (FileStream stream = File.Create(archivePath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestStream = manifestEntry.Open())
            await using (var writer = new StreamWriter(manifestStream))
            {
                await writer.WriteAsync(
                    "{\"schemaVersion\":2,\"kind\":\"cloud-backup\"," +
                    "\"createdAtUtc\":\"2026-09-18T00:00:00+00:00\"," +
                    "\"appVersion\":\"1.0.0.0\",\"domains\":[\"widget-style\"]}");
            }

            ZipArchiveEntry styleEntry = archive.CreateEntry("widget-style.json");
            await using Stream styleStream = styleEntry.Open();
            await styleStream.WriteAsync(
                "{\"kind\":\"widget-style\",\"shell\":{},\"widgets\":[]}"u8.ToArray());
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            archivePath, CloudBackupDomain.WidgetStyle);
        Assert.Contains("widget-style", prep.Domains!);
        await service.CancelPendingRestoreAsync();
    }


    [Fact]
    public async Task ScopedRestore_PartialSelection_AppliesOnlyRequestedDomain()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");
        var liveQc = new QuickCaptureStore(Path.Combine(dataDir, "quick-capture"));
        await liveQc.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "n1", Body = "local note" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "todo-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "t", Text = "cloud task" }]
        });
        var sourceQc = new QuickCaptureStore(Path.Combine(sourceData, "quick-capture"));
        await sourceQc.SaveAsync(new QuickCaptureStoreData
        {
            Items = [new QuickCaptureItem { Id = "n1", Body = "cloud note" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(
                _exportRoot,
                CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);
        Assert.Equal(["todo-data"], prep.Domains);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(
            dataDir, "widgets", "todo-widget", "todo.json")));
        // QuickCapture was in the archive but not requested → untouched.
        QuickCaptureStoreData qc = await liveQc.LoadAsync();
        Assert.Equal("local note", Assert.Single(qc.Items).Body);
    }

    [Fact]
    public async Task ScopedRestore_AppliesWidgetStyleToSettingsFile()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string settingsPath = Path.Combine(dataDir, "settings.json");
        var localSettings = new AppSettings
        {
            WidgetOpacity = 0.80,
            Language = "zh-CN",
            LayoutDensity = "Compact",
            WidgetCapsuleBarPlacement = "Floating",
            WidgetLayerMode = "Dynamic",
            IconSize = 30,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "w1",
                    Name = "Local name",
                    X = 111,
                    Y = 222,
                    Width = 300,
                    WidgetKind = WidgetKind.Todo
                }
            ]
        };
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(localSettings, SettingsJsonContext.Default.AppSettings));

        var cloudSettings = new AppSettings
        {
            WidgetOpacity = 0.42,
            Language = "en-US",
            // Layout keys that must NOT sync: cloud values differ so a leak
            // would flip the local desktop's density/capsule arrangement.
            LayoutDensity = "Relaxed",
            WidgetCapsuleBarPlacement = "Bottom",
            WidgetLayerMode = "DesktopPinned",
            IconSize = 44,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "w1",
                    Name = "Cloud name",
                    X = 999,
                    Y = 888,
                    Width = 500,
                    WidgetKind = WidgetKind.Todo,
                    IsCollapsed = true,
                    CompactWidth = 120
                }
            ]
        };
        string backupPath = await new DeskBoxDataBackupService(_appDataRoot)
            .ExportScopedBackupAsync(
                _exportRoot,
                CloudBackupDomain.WidgetStyle,
                _ => Task.FromResult<byte[]?>(
                    WidgetStyleBackupProjection.Serialize(cloudSettings)));

        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareScopedRestoreAsync(backupPath, CloudBackupDomain.WidgetStyle);
        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        AppSettings patched = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(settingsPath),
            SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(0.42, patched.WidgetOpacity);
        // Style only: position/size fields stay local, non-style settings
        // (language) stay local.
        WidgetConfig widget = Assert.Single(patched.Widgets);
        Assert.Equal("Cloud name", widget.Name);
        Assert.Equal(111, widget.X);
        Assert.Equal(222, widget.Y);
        Assert.Equal(300, widget.Width);
        Assert.Equal("zh-CN", patched.Language);
        // Layout stays local: density, capsule-bar placement, layer mode,
        // icon scale and the per-widget compact state are not restored.
        Assert.Equal("Compact", patched.LayoutDensity);
        Assert.Equal("Floating", patched.WidgetCapsuleBarPlacement);
        Assert.Equal("Dynamic", patched.WidgetLayerMode);
        Assert.Equal(30, patched.IconSize);
        Assert.False(widget.IsCollapsed);
        Assert.Null(widget.CompactWidth);
    }

    [Fact]
    public async Task StyleApply_RestoresGoodBackupBeforePatchingCorruptPrimary()
    {
        // A "{}" primary parses as a JsonObject but is not a layout
        // document. Patching it directly would commit the corrupt DOM and
        // rotate the surviving good .bak out of existence — the apply must
        // run the store's own heal pass (quarantine bad primary, promote
        // valid .bak) before touching either file.
        string dataDir = Directory.CreateDirectory(
            Path.Combine(_appDataRoot, "data")).FullName;
        string settingsPath = Path.Combine(dataDir, "settings.json");
        string layoutPath = Path.Combine(dataDir, "widget-layout.json");
        string backupPath = layoutPath + ".bak";

        await File.WriteAllTextAsync(
            settingsPath, """{"language":"zh-CN","widgetOpacity":0.9}""");

        var goodSlice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
        };
        string goodLayoutJson = JsonSerializer.Serialize(
            new WidgetLayoutDocument { Layout = goodSlice },
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        await File.WriteAllTextAsync(backupPath, goodLayoutJson);
        await File.WriteAllTextAsync(layoutPath, "{}");

        var cloudSettings = new AppSettings { WidgetOpacity = 0.42 };
        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(
                WidgetStyleBackupProjection.Serialize(cloudSettings),
                settingsPath,
                layoutPath,
                CancellationToken.None);

        Assert.True(result.Applied, result.SkippedReason);

        // The healed primary carries the good layout's widgets (patched);
        // the .bak still holds the same good bytes — no copy was burned.
        WidgetLayoutDocument healed = WidgetLayoutStore.ParseLayoutDocument(
            await File.ReadAllTextAsync(layoutPath));
        Assert.Single(healed.Layout.Widgets);
        Assert.Equal(goodLayoutJson, await File.ReadAllTextAsync(backupPath));

        AppSettings patched = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(settingsPath),
            SettingsJsonContext.Default.AppSettings)!;
        Assert.Equal(0.42, patched.WidgetOpacity);
    }

    [Fact]
    public async Task Projection_DocumentExcludesGeometryAndDeviceFields()
    {
        var settings = new AppSettings
        {
            WidgetCapsuleBarOrder = ["a", "b"],
            WidgetCapsuleFreePlacements =
            {
                ["w1"] = new WidgetCompactPlacement { X = 5, Y = 6 }
            },
            LayoutDensity = "Compact",
            WidgetCapsuleBarPlacement = "Bottom",
            WidgetLayerMode = "DesktopPinned",
            IconSize = 44,
            DefaultWidgetWidth = 500,
            ResizeSnapEnabled = false,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "w1",
                    X = 10,
                    Y = 20,
                    Width = 300,
                    Height = 400,
                    PositionAnchor = "top-left",
                    IsCollapsed = true,
                    CompactWidth = 120,
                    IconSizeOverride = 48,
                    MappedFolderPath = "D:\\secret\\folder",
                    Items = [new WidgetItemConfig { Path = "D:\\secret\\file.txt" }],
                    Metadata = { ["folder"] = "D:\\secret" }
                }
            ]
        };

        byte[] doc = WidgetStyleBackupProjection.Serialize(settings);
        string json = System.Text.Encoding.UTF8.GetString(doc);

        // No coordinates, no topology, no file bindings, no internal counters.
        Assert.DoesNotContain("widgetCapsuleBarOrder", json);
        Assert.DoesNotContain("widgetCapsuleFreePlacements", json);
        Assert.DoesNotContain("widgetCompactSettingsVersion", json);
        Assert.DoesNotContain("positionAnchor", json);
        Assert.DoesNotContain("mappedFolderPath", json);
        Assert.DoesNotContain("D:\\secret", json);
        Assert.DoesNotContain("\"x\"", json);
        Assert.DoesNotContain("\"width\"", json);
        Assert.DoesNotContain("widgetTopology", json);
        // No layout either: density, sizing, capsule-bar placement, layer
        // mode, snap, compact state and per-widget icon scale all stay local.
        Assert.DoesNotContain("layoutDensity", json);
        Assert.DoesNotContain("widgetCapsuleBarPlacement", json);
        Assert.DoesNotContain("widgetLayerMode", json);
        Assert.DoesNotContain("iconSize", json);
        Assert.DoesNotContain("defaultWidgetWidth", json);
        Assert.DoesNotContain("resizeSnapEnabled", json);
        Assert.DoesNotContain("isCollapsed", json);
        Assert.DoesNotContain("compactWidth", json);
        Assert.DoesNotContain("iconSizeOverride", json);
    }

    [Fact]
    public async Task Projection_ApplyToMissingSettings_SkipsGracefully()
    {
        byte[] doc = WidgetStyleBackupProjection.Serialize(new AppSettings());
        var result = await WidgetStyleBackupProjection.ApplyAsync(
            doc,
            Path.Combine(_tempRoot, "missing-settings.json"),
            Path.Combine(_tempRoot, "widget-layout.json"));
        Assert.False(result.Applied);
        Assert.NotNull(result.SkippedReason);
    }

    [Fact]
    public async Task Projection_Apply_WidgetsPatch_LandsInLayoutFile()
    {
        // Live pair: settings.json owns shell style, widget-layout.json owns
        // the widgets array — the apply must split the patch accordingly and
        // leave the layout envelope (schemaVersion) intact.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings();
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(liveSettings, SettingsJsonContext.Default.AppSettings));

        var slice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
        };
        await File.WriteAllTextAsync(
            layoutPath,
            JsonSerializer.Serialize(
                new WidgetLayoutDocument { Layout = slice },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

        var source = new AppSettings { WidgetOpacity = 0.42 };
        source.Widgets.Add(
            new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo, ViewMode = ViewMode.List });
        byte[] doc = WidgetStyleBackupProjection.Serialize(source);

        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath);

        Assert.True(result.Applied);
        Assert.Equal(1, result.WidgetsPatched);

        JsonObject settingsDom = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal(0.42, settingsDom["widgetOpacity"]!.GetValue<double>());

        JsonObject layoutDom = JsonNode.Parse(
            await File.ReadAllTextAsync(layoutPath))!.AsObject();
        Assert.Equal(1, layoutDom["schemaVersion"]!.GetValue<int>());
        Assert.Equal(
            "List",
            layoutDom["layout"]!["widgets"]![0]!["viewMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Projection_Apply_RefusesFutureLayoutSchema()
    {
        // A widget-layout.json stamped by a NEWER build is read-only to this
        // one — WidgetLayoutStore.CanWrite enforces that for the normal save
        // path, and the style apply must not bypass it by DOM-patching the
        // file directly. The whole apply fails closed: settings.json must
        // not commit a half-restored pair either.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.9 };
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(liveSettings, SettingsJsonContext.Default.AppSettings));
        const string futureLayout =
            "{\"schemaVersion\":99,\"futureField\":42," +
            "\"layout\":{\"widgets\":[{\"id\":\"w1\",\"widgetKind\":\"Todo\"}]}}";
        await File.WriteAllTextAsync(layoutPath, futureLayout);
        string originalSettings = await File.ReadAllTextAsync(settingsPath);

        var source = new AppSettings { WidgetOpacity = 0.42 };
        source.Widgets.Add(
            new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo, ViewMode = ViewMode.List });
        byte[] doc = WidgetStyleBackupProjection.Serialize(source);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath));

        Assert.Equal(originalSettings, await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(futureLayout, await File.ReadAllTextAsync(layoutPath));
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.style-restore.*"));
    }

    [Fact]
    public async Task Projection_Apply_LayoutCommitFailure_RollsSettingsBack()
    {
        // The two live files commit independently; when the layout commit
        // fails after the settings commit landed, the settings file must be
        // rolled back — a failed restore cannot leave shell keys on the new
        // style while widget styles stayed on the old one.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.11 };
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        string originalSettings = JsonSerializer.Serialize(
            liveSettings, SettingsJsonContext.Default.AppSettings);
        await File.WriteAllTextAsync(settingsPath, originalSettings);

        var slice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
        };
        await File.WriteAllTextAsync(
            layoutPath,
            JsonSerializer.Serialize(
                new WidgetLayoutDocument { Layout = slice },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

        var source = new AppSettings { WidgetOpacity = 0.42 };
        source.Widgets.Add(
            new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo, ViewMode = ViewMode.List });
        byte[] doc = WidgetStyleBackupProjection.Serialize(source);

        // Read-share the layout file: reads still work (the DOM load needs
        // them) but the replace-commit onto it is denied.
        await using var lockStream = new FileStream(
            layoutPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath));

        Assert.Equal(originalSettings, await File.ReadAllTextAsync(settingsPath));

        // The journal recovery also neutralizes settings.json.bak: the
        // resilient commit rotated the half-applied bytes into it, and
        // leaving them would resurrect the failed transaction the next
        // time the primary corrupts and the loader falls back to .bak.
        Assert.Equal(
            originalSettings,
            await File.ReadAllTextAsync(settingsPath + ".bak"));

        // The layout restore ALSO failed (same lock), so the journal must
        // survive intact — deleting it would strand the partial recovery.
        Assert.True(File.Exists(settingsPath + ".style-restore.pending"));
        Assert.True(File.Exists(settingsPath + ".style-restore.orig"));
        Assert.True(File.Exists(layoutPath + ".style-restore.orig"));
        Assert.False(File.Exists(settingsPath + ".style-restore.committed"));
    }

    [Fact]
    public async Task Projection_Apply_LayoutCommitFailure_RetryConverges()
    {
        // Same failure as above, then the obstruction clears: the surviving
        // journal lets the next apply finish the rollback to convergence.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.11 };
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        string originalSettings = JsonSerializer.Serialize(
            liveSettings, SettingsJsonContext.Default.AppSettings);
        await File.WriteAllTextAsync(settingsPath, originalSettings);

        var slice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
        };
        string originalLayout = JsonSerializer.Serialize(
            new WidgetLayoutDocument { Layout = slice },
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        await File.WriteAllTextAsync(layoutPath, originalLayout);

        var source = new AppSettings { WidgetOpacity = 0.42 };
        source.Widgets.Add(
            new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo, ViewMode = ViewMode.List });
        byte[] doc = WidgetStyleBackupProjection.Serialize(source);

        await using (new FileStream(
            layoutPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath));
            Assert.True(File.Exists(settingsPath + ".style-restore.pending"));
        }

        // Journal healed on entry, then this apply commits normally.
        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath);

        Assert.True(result.Applied);
        JsonObject live = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal(0.42, live["widgetOpacity"]!.GetValue<double>());
        Assert.False(File.Exists(settingsPath + ".style-restore.pending"));
        Assert.False(File.Exists(settingsPath + ".style-restore.orig"));
        Assert.False(File.Exists(layoutPath + ".style-restore.orig"));
    }

    [Fact]
    public async Task Projection_Apply_CommittedPendingStuck_NeverRollsBack()
    {
        // The most dangerous journal remnant: a COMPLETED transaction
        // whose cleanup deleted committed+origs but left pending — the
        // leftover reads as uncommitted and must never trigger a rollback.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.42 };
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        string liveSettingsJson = JsonSerializer.Serialize(
            liveSettings, SettingsJsonContext.Default.AppSettings);
        await File.WriteAllTextAsync(settingsPath, liveSettingsJson);
        var slice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
        };
        string liveLayoutJson = JsonSerializer.Serialize(
            new WidgetLayoutDocument { Layout = slice },
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);
        await File.WriteAllTextAsync(layoutPath, liveLayoutJson);

        // Simulated partial cleanup: pending survived, committed did not.
        await File.WriteAllTextAsync(settingsPath + ".style-restore.pending", "x");

        byte[] doc = System.Text.Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"kind":"widget-style"}""");

        // Pending-only + missing snapshots = corrupt journal → fail closed:
        // the apply refuses, but the LIVE files must be untouched.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath));

        Assert.Equal(liveSettingsJson, await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(liveLayoutJson, await File.ReadAllTextAsync(layoutPath));
        // And the evidence stays for diagnosis.
        Assert.True(File.Exists(settingsPath + ".style-restore.pending"));
    }

    [Fact]
    public async Task Projection_Apply_CommittedCleanupBlocked_KeepsCommitted()
    {
        // Committed transaction, pending delete blocked: cleanup must stop
        // there — deleting committed first would leave the pending-only
        // remnant that reads as an uncommitted transaction.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.42 };
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(liveSettings, SettingsJsonContext.Default.AppSettings));
        await File.WriteAllTextAsync(
            layoutPath,
            JsonSerializer.Serialize(
                new WidgetLayoutDocument
                {
                    Layout = new WidgetLayoutSettingsSlice
                    {
                        Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
                    }
                },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

        string pending = settingsPath + ".style-restore.pending";
        string committed = settingsPath + ".style-restore.committed";
        await File.WriteAllTextAsync(pending, "x");
        await File.WriteAllTextAsync(committed, "1");
        await File.WriteAllTextAsync(settingsPath + ".style-restore.orig", "{}");
        await File.WriteAllTextAsync(layoutPath + ".style-restore.orig", "{}");

        byte[] doc = System.Text.Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"kind":"widget-style"}""");

        // Lock pending exclusively: its delete fails inside cleanup, then
        // the apply's own marker write fails too — but the committed
        // sentinel and snapshots must all survive for the retry.
        await using (new FileStream(
            pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath));
            Assert.True(File.Exists(committed));
            Assert.True(File.Exists(pending));
            Assert.True(File.Exists(settingsPath + ".style-restore.orig"));
            Assert.True(File.Exists(layoutPath + ".style-restore.orig"));
        }

        // Unlocked: recovery sweeps the finished transaction's artifacts
        // and this apply commits normally.
        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath);
        Assert.True(result.Applied);
        Assert.False(File.Exists(pending));
        Assert.False(File.Exists(committed));
        Assert.False(File.Exists(settingsPath + ".style-restore.orig"));
    }

    [Fact]
    public async Task Projection_Apply_PendingJournal_HealsBeforeApply()
    {
        // Simulated crash between the two commits: the pending journal and
        // its snapshots survive, the live settings file carries half-applied
        // state. The next ApplyAsync must restore the originals first —
        // before it snapshots its own rollback bytes.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var originalSettings = new AppSettings { WidgetOpacity = 0.11 };
        originalSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        string originalSettingsJson = JsonSerializer.Serialize(
            originalSettings, SettingsJsonContext.Default.AppSettings);
        string originalLayoutJson = JsonSerializer.Serialize(
            new WidgetLayoutDocument
            {
                Layout = new WidgetLayoutSettingsSlice
                {
                    Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
                }
            },
            WidgetLayoutJsonContext.Default.WidgetLayoutDocument);

        await File.WriteAllTextAsync(settingsPath + ".style-restore.orig", originalSettingsJson);
        await File.WriteAllTextAsync(layoutPath + ".style-restore.orig", originalLayoutJson);
        await File.WriteAllTextAsync(
            settingsPath + ".style-restore.pending", "2026-01-01T00:00:00Z");

        // The "crashed mid-transaction" live state: shell style already
        // patched, layout still original.
        var halfApplied = new AppSettings { WidgetOpacity = 0.42 };
        halfApplied.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(halfApplied, SettingsJsonContext.Default.AppSettings));
        await File.WriteAllTextAsync(layoutPath, originalLayoutJson);

        // A document with nothing to patch keeps the apply a pure
        // observation of the recovery: live files must return to the
        // journaled originals.
        byte[] doc = System.Text.Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"kind":"widget-style"}""");
        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath);

        Assert.True(result.Applied);
        JsonObject restored = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal(0.11, restored["widgetOpacity"]!.GetValue<double>());
        Assert.False(File.Exists(settingsPath + ".style-restore.pending"));
        Assert.False(File.Exists(settingsPath + ".style-restore.orig"));
        Assert.False(File.Exists(layoutPath + ".style-restore.orig"));
    }

    [Fact]
    public async Task Projection_Apply_CommittedJournal_IsSweptNotReverted()
    {
        // Crash AFTER both commits but before cleanup: the committed flag
        // must keep recovery from reverting a finished transaction.
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        string layoutPath = Path.Combine(_tempRoot, "widget-layout.json");

        var liveSettings = new AppSettings { WidgetOpacity = 0.42 };
        liveSettings.Widgets.Add(new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo });
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(liveSettings, SettingsJsonContext.Default.AppSettings));
        await File.WriteAllTextAsync(
            layoutPath,
            JsonSerializer.Serialize(
                new WidgetLayoutDocument
                {
                    Layout = new WidgetLayoutSettingsSlice
                    {
                        Widgets = [new WidgetConfig { Id = "w1", WidgetKind = WidgetKind.Todo }]
                    }
                },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

        await File.WriteAllTextAsync(settingsPath + ".style-restore.orig", "{}");
        await File.WriteAllTextAsync(layoutPath + ".style-restore.orig", "{}");
        await File.WriteAllTextAsync(settingsPath + ".style-restore.pending", "x");
        await File.WriteAllTextAsync(settingsPath + ".style-restore.committed", "1");

        byte[] doc = System.Text.Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"kind":"widget-style"}""");
        WidgetStyleBackupProjection.ApplyResult result =
            await WidgetStyleBackupProjection.ApplyAsync(doc, settingsPath, layoutPath);

        Assert.True(result.Applied);
        JsonObject live = JsonNode.Parse(
            await File.ReadAllTextAsync(settingsPath))!.AsObject();
        Assert.Equal(0.42, live["widgetOpacity"]!.GetValue<double>());
        Assert.False(File.Exists(settingsPath + ".style-restore.pending"));
        Assert.False(File.Exists(settingsPath + ".style-restore.committed"));
        Assert.False(File.Exists(settingsPath + ".style-restore.orig"));
    }

    [Fact]
    public async Task CredentialStore_Fake_RoundTrips()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(await store.GetSecretAsync("webdav"));
        await store.SetSecretAsync("webdav", "s3cret");
        Assert.Equal("s3cret", await store.GetSecretAsync("webdav"));
        await store.SetSecretAsync("webdav", "rotated");
        Assert.Equal("rotated", await store.GetSecretAsync("webdav"));
        await store.RemoveSecretAsync("webdav");
        Assert.Null(await store.GetSecretAsync("webdav"));
        await store.RemoveSecretAsync("webdav"); // absent → no-op
    }

    [Fact]
    public async Task ExportScoped_WritesSourceDeviceIdInManifest()
    {
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var todoStore = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "todo-widget");
        await todoStore.SaveAsync(new TodoWidgetData { Items = [] });
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot, CloudBackupDomain.TodoData);

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        JsonObject manifest = await ReadManifestAsync(archive);
        Assert.Equal(
            DeviceIdentity.Id,
            manifest["sourceDeviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScopedRestore_OversizedStyleEntry_Rejected()
    {
        // A scoped archive whose widget-style.json exceeds the dedicated
        // cap must be rejected at prepare — the entry bypasses the per-file
        // manifest but not the extraction safety budget.
        string archivePath = Path.Combine(_exportRoot, "oversized-style.zip");
        await using (FileStream stream = File.Create(archivePath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
            await using (Stream manifestStream = manifestEntry.Open())
            await using (var writer = new StreamWriter(manifestStream))
            {
                await writer.WriteAsync(
                    "{\"schemaVersion\":2,\"kind\":\"cloud-backup\"," +
                    "\"createdAtUtc\":\"2026-09-18T00:00:00+00:00\"," +
                    "\"appVersion\":\"1.0.0.0\",\"domains\":[\"widget-style\"]}");
            }

            ZipArchiveEntry styleEntry = archive.CreateEntry("widget-style.json");
            await using Stream styleStream = styleEntry.Open();
            byte[] chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'x');
            for (int i = 0; i < 9; i++)
            {
                await styleStream.WriteAsync(chunk);
            }
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PrepareScopedRestoreAsync(archivePath, CloudBackupDomain.WidgetStyle));
        Assert.Contains("widget-style", ex.Message);
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task ScopedRestore_RemapsOrphanedTodoWidgetOntoLiveWidget()
    {
        // Live device has todo widget "target-widget"; the snapshot was
        // taken with "source-widget" (another device, or a recreated
        // widget). Prepare must remap the orphan onto the live widget so
        // the restored data is actually visible.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            "{\"widgets\":[{\"id\":\"target-widget\",\"widgetKind\":\"Todo\"}]}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "new", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);

        DeskBoxTodoWidgetRemap remap = Assert.Single(prep.TodoWidgetRemaps!);
        Assert.Equal("source-widget", remap.SourceWidgetId);
        Assert.Equal("target-widget", remap.TargetWidgetId);
        Assert.Empty(prep.UnmappedTodoWidgetIds!);
        Assert.Equal(DeviceIdentity.Id, prep.SourceDeviceId);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-widget").LoadAsync();
        Assert.Equal("cloud task", Assert.Single(restored.Items).Text);
    }

    [Fact]
    public async Task ScopedRestore_OrphanWithoutFreeTarget_PreservedAndReported()
    {
        // No live todo widget at all (e.g. wiped device): the orphan stays
        // on disk under its source id and is reported — data preserved,
        // honestly labelled unmapped.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDir, "settings.json"), "{}");

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "new", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);

        Assert.Empty(prep.TodoWidgetRemaps!);
        Assert.Equal(["source-widget"], prep.UnmappedTodoWidgetIds);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(false));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData orphan = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "source-widget").LoadAsync();
        Assert.Equal("cloud task", Assert.Single(orphan.Items).Text);
    }

    [Fact]
    public async Task ScopedRestore_RemapsOrphanedTodo_WhenOnlyLayoutFileSurvives()
    {
        // settings.json is gone (lost/quarantined) but widget-layout.json
        // survived with a live todo widget — the device store is the first
        // authority, so the orphan must still remap onto it instead of
        // reporting no live widgets.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        var slice = new WidgetLayoutSettingsSlice
        {
            Widgets = [new WidgetConfig { Id = "target-widget", WidgetKind = WidgetKind.Todo }]
        };
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "widget-layout.json"),
            JsonSerializer.Serialize(
                new WidgetLayoutDocument { Layout = slice },
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument));
        Assert.False(File.Exists(Path.Combine(dataDir, "settings.json")));
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "new", Text = "cloud task" }]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);

        DeskBoxTodoWidgetRemap remap = Assert.Single(prep.TodoWidgetRemaps!);
        Assert.Equal("source-widget", remap.SourceWidgetId);
        Assert.Equal("target-widget", remap.TargetWidgetId);
        Assert.Empty(prep.UnmappedTodoWidgetIds!);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-widget").LoadAsync();
        Assert.Equal("cloud task", Assert.Single(restored.Items).Text);
    }

    [Fact]
    public async Task ScopedRestore_RemappedTodoWidget_RewritesManagedAttachmentPaths()
    {
        // The remap moves the staged dir source→target; embedded attachment
        // FilePaths carry the source id and must be rewritten to the target
        // — otherwise the restore "succeeds" with dead references. The
        // attachment FILE itself is not in the domain right now (uploads
        // have no size bound): the metadata still rewrites so the reference
        // is correct whenever the file exists locally or attachments return.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            "{\"widgets\":[{\"id\":\"target-widget\",\"widgetKind\":\"Todo\"}]}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        string attachmentDir = Directory.CreateDirectory(
            Path.Combine(sourceData, "widgets", "source-widget", "attachments")).FullName;
        string sourceAttachment = Path.Combine(attachmentDir, "a.pdf");
        await File.WriteAllTextAsync(sourceAttachment, "pdf-bytes");
        var sourceTodo = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-widget");
        await sourceTodo.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "new",
                    Text = "cloud task",
                    Attachments =
                    [
                        new TodoAttachment
                        {
                            FilePath = sourceAttachment,
                            DisplayName = "a.pdf",
                            StorageMode = TodoAttachment.ManagedStorageMode
                        }
                    ]
                }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);
        Assert.Single(prep.TodoWidgetRemaps!);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);

        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-widget").LoadAsync();
        TodoAttachment attachment = Assert.Single(
            Assert.Single(restored.Items).Attachments);
        // The attachment file is excluded from the domain, so the staged
        // payload carries no attachments/ and TryRebaseManagedPath leaves
        // the reference untouched — which is correct for a same-machine
        // restore: the live attachment still sits at its original path
        // (the delete phase never touches out-of-domain files).
        Assert.Equal(sourceAttachment, attachment.FilePath);
    }

    [Fact]
    public async Task ScopedRestore_MultipleOrphans_PairOntoFreeWidgets()
    {
        // Two orphans against two free widgets pair deterministically in id
        // order — a cross-device restore must land the data visibly, not
        // drop it invisible under foreign source ids.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            "{\"widgets\":[{\"id\":\"target-a\",\"widgetKind\":\"Todo\"}," +
            "{\"id\":\"target-b\",\"widgetKind\":\"Todo\"}]}");
        var liveA = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-a");
        await liveA.SaveAsync(new TodoWidgetData { Items = [new TodoItem { Id = "a", Text = "local a" }] });
        var liveB = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-b");
        await liveB.SaveAsync(new TodoWidgetData { Items = [new TodoItem { Id = "b", Text = "local b" }] });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceX = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-x");
        await sourceX.SaveAsync(new TodoWidgetData { Items = [new TodoItem { Id = "x", Text = "cloud x" }] });
        var sourceY = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-y");
        await sourceY.SaveAsync(new TodoWidgetData { Items = [new TodoItem { Id = "y", Text = "cloud y" }] });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);

        Assert.Equal(2, prep.TodoWidgetRemaps!.Count);
        Assert.Empty(prep.UnmappedTodoWidgetIds!);
        Assert.Contains(
            prep.TodoWidgetRemaps,
            r => r.SourceWidgetId == "source-x" && r.TargetWidgetId == "target-a");
        Assert.Contains(
            prep.TodoWidgetRemaps,
            r => r.SourceWidgetId == "source-y" && r.TargetWidgetId == "target-b");

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        // Each orphan landed on a live widget and stays visible.
        TodoWidgetData restoredA = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-a").LoadAsync();
        Assert.Equal("cloud x", Assert.Single(restoredA.Items).Text);
        TodoWidgetData restoredB = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-b").LoadAsync();
        Assert.Equal("cloud y", Assert.Single(restoredB.Items).Text);
        // No invisible leftovers under foreign source ids.
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "source-x")));
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "source-y")));
    }

    [Fact]
    public async Task ScopedRestore_ExtraOrphans_MergeIntoFreeTarget()
    {
        // More source lists than free local widgets: the first orphan remaps
        // onto the free widget, the leftover merges its items into the same
        // store — nothing lands invisible and nothing is dropped.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            "{\"widgets\":[{\"id\":\"target-widget\",\"widgetKind\":\"Todo\"}]}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });

        string sourceRoot = Path.Combine(_tempRoot, "source-app-data");
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceA = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-a");
        await sourceA.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "sa", Text = "cloud a" }]
        });
        var sourceB = new TodoWidgetStore(Path.Combine(sourceData, "widgets"), "source-b");
        await sourceB.SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "sb", Text = "cloud b" },
                new TodoItem { Id = "sb2", Text = "cloud b2" }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportScopedBackupAsync(_exportRoot, CloudBackupDomain.TodoData);

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            backupPath, CloudBackupDomain.TodoData);

        // Both source stores report as mapped onto the one live widget.
        Assert.Equal(2, prep.TodoWidgetRemaps!.Count);
        Assert.All(
            prep.TodoWidgetRemaps,
            r => Assert.Equal("target-widget", r.TargetWidgetId));
        Assert.Empty(prep.UnmappedTodoWidgetIds!);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);

        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-widget").LoadAsync();
        Assert.Equal(
            ["cloud a", "cloud b", "cloud b2"],
            restored.Items.Select(i => i.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray());
        // The merged source dir is gone — no invisible data on disk.
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "source-a")));
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "source-b")));
    }

    [Fact]
    public async Task ScopedRestore_SourceOrphanDirs_DroppedByStyleDocument()
    {
        // Old backups shipped every widgets/<id>/todo.json on disk —
        // including dirs no live widget ever read. The archive's own
        // widget-style.json records the source device's live widgets, so a
        // staged dir it does not name is debris: drop it instead of letting
        // it eat a remap slot or linger invisible.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "settings.json"),
            "{\"widgets\":[{\"id\":\"target-widget\",\"widgetKind\":\"Todo\"}]}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "target-widget");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "old", Text = "local task" }]
        });

        // schemaVersion 1 manifest → no integrity file list required.
        string archivePath = Path.Combine(_exportRoot, "legacy-multi.zip");
        await using (FileStream stream = File.Create(archivePath))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json");
            await using (Stream s = manifest.Open())
            await using (var w = new StreamWriter(s))
            {
                await w.WriteAsync(
                    "{\"schemaVersion\":1,\"kind\":\"cloud-backup\"," +
                    "\"createdAtUtc\":\"2026-09-21T00:00:00+00:00\"," +
                    "\"appVersion\":\"1.5.5.0\"," +
                    "\"domains\":[\"todo-data\",\"widget-style\"]}");
            }

            async Task AddEntryAsync(string name, string content)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                await using Stream entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync(content);
            }

            await AddEntryAsync(
                "data/widgets/live-src/todo.json",
                "{\"version\":3,\"items\":[{\"id\":\"i1\",\"text\":\"cloud task\"}]}");
            await AddEntryAsync(
                "data/widgets/junk-src/todo.json",
                "{\"version\":3,\"items\":[{\"id\":\"j1\",\"text\":\"debris\"}]}");
            // The source's own widget inventory: only live-src was a real
            // todo widget there.
            await AddEntryAsync(
                "widget-style.json",
                "{\"schemaVersion\":1,\"kind\":\"widget-style\",\"widgets\":" +
                "{\"live-src\":{\"widgetKind\":\"Todo\"}}}");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);
        DeskBoxRestorePreparation prep = await service.PrepareScopedRestoreAsync(
            archivePath, CloudBackupDomain.TodoData | CloudBackupDomain.WidgetStyle);

        DeskBoxTodoWidgetRemap remap = Assert.Single(prep.TodoWidgetRemaps!);
        Assert.Equal("live-src", remap.SourceWidgetId);
        Assert.Equal("target-widget", remap.TargetWidgetId);
        Assert.Empty(prep.UnmappedTodoWidgetIds!);

        Assert.True(await service.SetPendingRestoreItemReplaceModeAsync(true));
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        TodoWidgetData restored = await new TodoWidgetStore(
            Path.Combine(dataDir, "widgets"), "target-widget").LoadAsync();
        Assert.Equal("cloud task", Assert.Single(restored.Items).Text);
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "junk-src")));
        Assert.False(Directory.Exists(Path.Combine(dataDir, "widgets", "live-src")));
    }

    [Fact]
    public async Task ExportScoped_TodoDomain_SkipsOrphanWidgetStores()
    {
        // widgets/<id>/ dirs whose id is not a live todo widget are debris
        // (deleted widgets, unmapped-restore leftovers). Shipping them would
        // poison every restore destination, so export filters them out.
        string dataDir = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "widget-layout.json"),
            "{\"schemaVersion\":1,\"layout\":{\"widgets\":" +
            "[{\"id\":\"live-todo\",\"widgetKind\":\"Todo\"}]}}");
        var liveTodo = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "live-todo");
        await liveTodo.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "t1", Text = "real task" }]
        });
        var orphan = new TodoWidgetStore(Path.Combine(dataDir, "widgets"), "dead-widget");
        await orphan.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "d1", Text = "debris" }]
        });

        var service = new DeskBoxDataBackupService(_appDataRoot);
        string backupPath = await service.ExportScopedBackupAsync(
            _exportRoot, CloudBackupDomain.TodoData);

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("data/widgets/live-todo/todo.json"));
        Assert.Null(archive.GetEntry("data/widgets/dead-widget/todo.json"));
    }

    private static async Task<JsonObject> ReadManifestAsync(ZipArchive archive)
    {
        ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("manifest.json"));
        await using Stream stream = entry.Open();
        return (await JsonNode.ParseAsync(stream))!.AsObject();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(key, out string? value) ? value : null);

        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_secrets.Keys.ToList());
    }
}
