using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.True(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/attachments/note/pic.png"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/glance.json"));
        Assert.False(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.TodoData, "widgets/todo-widget/todo.json.bak"));

        Assert.True(CloudBackupDomains.IsInDomain(
            CloudBackupDomain.QuickCaptureData, "quick-capture/quick-capture.json"));
        Assert.True(CloudBackupDomains.IsInDomain(
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
        Assert.NotNull(archive.GetEntry("data/widgets/todo-widget/attachments/pic.png"));
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
    public async Task ScopedRestore_DeletesLiveDomainFilesAbsentFromSnapshot()
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
        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.Succeeded, result.ErrorMessage);
        // Snapshot-faithful: the widget store that exists only locally is
        // gone; the snapshotted one is present.
        Assert.False(File.Exists(Path.Combine(
            dataDir, "widgets", "extra-todo", "todo.json")));
        Assert.True(File.Exists(Path.Combine(
            dataDir, "widgets", "todo-widget", "todo.json")));
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
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "w1",
                    Name = "Cloud name",
                    X = 999,
                    Y = 888,
                    Width = 500,
                    WidgetKind = WidgetKind.Todo
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
    }

    [Fact]
    public async Task Projection_ApplyToMissingSettings_SkipsGracefully()
    {
        byte[] doc = WidgetStyleBackupProjection.Serialize(new AppSettings());
        var result = await WidgetStyleBackupProjection.ApplyToSettingsFileAsync(
            doc, Path.Combine(_tempRoot, "missing-settings.json"));
        Assert.False(result.Applied);
        Assert.NotNull(result.SkippedReason);
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
    }
}
