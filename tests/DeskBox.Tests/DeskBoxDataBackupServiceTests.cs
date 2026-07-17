using System.IO.Compression;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DeskBoxDataBackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataRoot;
    private readonly string _exportRoot;

    public DeskBoxDataBackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _appDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "app-data")).FullName;
        _exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "exports")).FullName;
    }

    [Fact]
    public async Task ExportBackupAsync_IncludesManifestDataAndNestedAttachments()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string attachmentDirectory = Directory.CreateDirectory(
            Path.Combine(dataDirectory, "widgets", "todo", "attachments")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{\"language\":\"en-US\"}");
        await File.WriteAllBytesAsync(Path.Combine(attachmentDirectory, "spec.pdf"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "ignored.tmp"), "partial");
        string thumbnailDirectory = Directory.CreateDirectory(
            Path.Combine(dataDirectory, "quick-capture", "thumbnails")).FullName;
        string exportDirectory = Directory.CreateDirectory(
            Path.Combine(dataDirectory, "quick-capture", "exports")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(thumbnailDirectory, "cached.png"), [4, 5, 6]);
        await File.WriteAllTextAsync(Path.Combine(exportDirectory, "temporary.txt"), "temporary");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string backupPath = await service.ExportBackupAsync(_exportRoot);

        Assert.True(File.Exists(backupPath));
        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        Assert.NotNull(archive.GetEntry("data/settings.json"));
        Assert.NotNull(archive.GetEntry("data/widgets/todo/attachments/spec.pdf"));
        Assert.Null(archive.GetEntry("data/ignored.tmp"));
        Assert.Null(archive.GetEntry("data/quick-capture/thumbnails/cached.png"));
        Assert.Null(archive.GetEntry("data/quick-capture/exports/temporary.txt"));
        ZipArchiveEntry manifestEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("manifest.json"));
        using Stream manifestStream = manifestEntry.Open();
        using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream);
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("manual", manifest.RootElement.GetProperty("kind").GetString());
        JsonElement[] files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, file => Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length));
        Assert.False(Directory.Exists(service.BackupSnapshotStagingDirectory));
    }

    [Fact]
    public async Task ExportBackupAsync_ArchivesStableSnapshotWhenSourceChangesAfterStaging()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string settingsPath = Path.Combine(dataDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{\"theme\":\"Light\"}");
        string largeFilePath = Path.Combine(dataDirectory, "000-large.bin");
        await File.WriteAllBytesAsync(largeFilePath, new byte[24 * 1024 * 1024]);
        var service = new DeskBoxDataBackupService(_appDataRoot);

        Task<string> exportTask = service.ExportBackupAsync(_exportRoot);
        string stagedSettingsPath = await WaitForStagedFileAsync(
            service.BackupSnapshotStagingDirectory,
            Path.Combine("data", "settings.json"));
        Assert.True(File.Exists(stagedSettingsPath));

        await File.WriteAllTextAsync(settingsPath, "{\"theme\":\"Dark\"}");
        await File.WriteAllBytesAsync(largeFilePath, [9, 8, 7]);
        string backupPath = await exportTask;

        using (ZipArchive archive = ZipFile.OpenRead(backupPath))
        {
            ZipArchiveEntry settingsEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("data/settings.json"));
            using var reader = new StreamReader(settingsEntry.Open());
            Assert.Contains("Light", await reader.ReadToEndAsync());
        }

        string restoreRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "snapshot-restore")).FullName;
        var restoreService = new DeskBoxDataBackupService(restoreRoot);
        DeskBoxRestorePreparation preparation = await restoreService.PrepareRestoreAsync(backupPath);
        Assert.True(preparation.HasIntegrityManifest);
        Assert.False(Directory.Exists(service.BackupSnapshotStagingDirectory));
    }

    [Fact]
    public async Task ExportBackupAsync_RemovesSnapshotStagingAfterValidationFailure()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{ invalid json");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportBackupAsync(_exportRoot));

        Assert.False(Directory.Exists(service.BackupSnapshotStagingDirectory));
        Assert.Empty(Directory.EnumerateFiles(_exportRoot));
    }

    [Fact]
    public async Task CreateAutomaticSnapshotIfDueAsync_CreatesOnlyOneSnapshotPerDay()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string? firstPath = await service.CreateAutomaticSnapshotIfDueAsync();
        string? secondPath = await service.CreateAutomaticSnapshotIfDueAsync();

        Assert.NotNull(firstPath);
        Assert.True(File.Exists(firstPath));
        Assert.Null(secondPath);
        Assert.Single(Directory.EnumerateFiles(service.AutomaticSnapshotDirectory, "DeskBox-Auto-*.zip"));
        Assert.False(Directory.Exists(service.BackupSnapshotStagingDirectory));
    }

    [Fact]
    public async Task CreateAutomaticSnapshotIfDueAsync_ReturnsNullWhenThereIsNoData()
    {
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string? snapshotPath = await service.CreateAutomaticSnapshotIfDueAsync();

        Assert.Null(snapshotPath);
        Assert.False(Directory.Exists(service.AutomaticSnapshotDirectory));
    }

    [Fact]
    public async Task PrepareAndApplyRestore_ReplacesDataAndRebasesManagedAttachments()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "source-app-data")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        var sourceSettings = new AppSettings
        {
            Language = "zh-CN",
            WidgetCapsuleModeEnabled = true,
            WidgetCompactContentMode = SettingsService.WidgetCompactContentModeSummary,
            FileStacksEnabled = true,
            FileStackGroupBy = SettingsService.FileStackGroupByCustom,
            FileStackUnmatchedBehavior = SettingsService.FileStackUnmatchedOther,
            FileStackCustomRules =
            [
                new FileStackCustomRule
                {
                    Id = "design",
                    Name = "Design",
                    Extensions = [".psd", ".fig"]
                }
            ],
            QuickCaptureItemPreviewLineCount = 3,
            QuickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves,
            TodoItemPreviewLineCount = 2,
            TodoEditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "files",
                    Name = "Files",
                    WidgetKind = WidgetKind.File,
                    CompactWidth = 196,
                    CompactPlacement = new WidgetCompactPlacement
                    {
                        X = 80,
                        Y = 120,
                        PositionAnchor = "LeftTop"
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        [WidgetCollapseBehaviorNames.MetadataKey] = "Smart",
                        [WidgetFileStackSettings.GroupByOverrideMetadataKey] =
                            SettingsService.FileStackGroupByCustom
                    }
                }
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(sourceData, "settings.json"),
            JsonSerializer.Serialize(sourceSettings, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        string sourceQuickCapture = Directory.CreateDirectory(
            Path.Combine(sourceData, "quick-capture")).FullName;
        string sourceAttachment = Path.Combine(
            sourceQuickCapture,
            "attachments",
            "note",
            "image.png");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAttachment)!);
        await File.WriteAllBytesAsync(sourceAttachment, [1, 2, 3, 4]);
        var sourceStore = new QuickCaptureStore(sourceQuickCapture);
        await sourceStore.SaveAsync(new QuickCaptureStoreData
        {
            Items =
            [
                new QuickCaptureItem
                {
                    Id = "note",
                    Body = "Restored note",
                    ImagePath = sourceAttachment,
                    Attachments =
                    [
                        new TodoAttachment
                        {
                            Id = "image",
                            FilePath = sourceAttachment,
                            DisplayName = "image.png",
                            Type = "image",
                            StorageMode = TodoAttachment.ManagedStorageMode
                        }
                    ]
                }
            ]
        });
        string backupPath = await new DeskBoxDataBackupService(sourceRoot)
            .ExportBackupAsync(_exportRoot);

        string targetData = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(targetData, "settings.json"), "{\"language\":\"en-US\"}");
        var targetService = new DeskBoxDataBackupService(_appDataRoot);

        DeskBoxRestorePreparation preparation = await targetService.PrepareRestoreAsync(backupPath);
        DeskBoxRestoreApplyResult result = await targetService.ApplyPendingRestoreAsync();

        Assert.True(preparation.FileCount >= 3);
        Assert.Equal(2, preparation.BackupSchemaVersion);
        Assert.True(preparation.HasIntegrityManifest);
        Assert.True(result.HadPendingRestore);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(File.Exists(targetService.PendingRestoreMarkerPath));
        string restoredAttachment = Path.Combine(
            targetData,
            "quick-capture",
            "attachments",
            "note",
            "image.png");
        Assert.True(File.Exists(restoredAttachment));
        QuickCaptureStoreData restored = await new QuickCaptureStore(
            Path.Combine(targetData, "quick-capture")).LoadAsync();
        QuickCaptureItem restoredItem = Assert.Single(restored.Items);
        Assert.Equal(restoredAttachment, restoredItem.ImagePath, ignoreCase: true);
        Assert.Equal(
            restoredAttachment,
            Assert.Single(restoredItem.Attachments).FilePath,
            ignoreCase: true);
        AppSettings restoredSettings = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(Path.Combine(targetData, "settings.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(restoredSettings.WidgetCapsuleModeEnabled);
        Assert.Equal(SettingsService.WidgetCompactContentModeSummary, restoredSettings.WidgetCompactContentMode);
        Assert.True(restoredSettings.FileStacksEnabled);
        Assert.Equal(SettingsService.FileStackUnmatchedOther, restoredSettings.FileStackUnmatchedBehavior);
        Assert.Equal("Design", Assert.Single(restoredSettings.FileStackCustomRules).Name);
        Assert.Equal(3, restoredSettings.QuickCaptureItemPreviewLineCount);
        Assert.Equal(2, restoredSettings.TodoItemPreviewLineCount);
        WidgetConfig restoredWidget = Assert.Single(restoredSettings.Widgets);
        Assert.Equal(196, restoredWidget.CompactWidth);
        Assert.Equal("LeftTop", restoredWidget.CompactPlacement?.PositionAnchor);
        Assert.Equal("Smart", restoredWidget.Metadata[WidgetCollapseBehaviorNames.MetadataKey]);
        Assert.Single(Directory.EnumerateFiles(
            targetService.PreRestoreBackupDirectory,
            "DeskBox-PreRestore-*.zip"));
        Assert.False(Directory.Exists(targetService.BackupSnapshotStagingDirectory));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsPathTraversalWithoutLeavingPendingRestore()
    {
        string archivePath = Path.Combine(_exportRoot, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/../outside.txt", "unsafe");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
        Assert.False(File.Exists(Path.Combine(_appDataRoot, "outside.txt")));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsInvalidCoreJson()
    {
        string archivePath = Path.Combine(_exportRoot, "invalid-json.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/settings.json", "{ invalid json");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task PrepareRestoreAsync_AcceptsLegacySchemaOneBackupWithCoreSettings()
    {
        string archivePath = Path.Combine(_exportRoot, "legacy.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/settings.json", "{\"theme\":\"Dark\"}");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        DeskBoxRestorePreparation preparation = await service.PrepareRestoreAsync(archivePath);

        Assert.Equal(1, preparation.BackupSchemaVersion);
        Assert.False(preparation.HasIntegrityManifest);
        Assert.True(File.Exists(service.PendingRestoreMarkerPath));
        await service.CancelPendingRestoreAsync();
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsBackupFromNewerDeskBoxVersion()
    {
        string archivePath = Path.Combine(_exportRoot, "newer-version.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"99.0.0\"}");
            WriteEntry(archive, "data/settings.json", "{}");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsLegacyBackupWithoutSettings()
    {
        string archivePath = Path.Combine(_exportRoot, "missing-settings.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"schemaVersion\":1,\"kind\":\"manual\",\"createdAtUtc\":\"2026-07-01T00:00:00Z\",\"appVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/other.json", "{}");
        }

        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsTamperedSchemaTwoFile()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);
        string archivePath = await service.ExportBackupAsync(_exportRoot);

        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry settingsEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("data/settings.json"));
            settingsEntry.Delete();
            WriteEntry(archive, "data/settings.json", "{\"theme\":\"Dark\"}");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task ApplyPendingRestoreAsync_FailureClearsPendingRestoreAndPreservesCurrentData()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "failed-restore-source")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sourceData, "settings.json"), "{\"theme\":\"Dark\"}");
        string archivePath = await new DeskBoxDataBackupService(sourceRoot).ExportBackupAsync(_exportRoot);

        string currentData = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        string currentSettingsPath = Path.Combine(currentData, "settings.json");
        await File.WriteAllTextAsync(currentSettingsPath, "{\"theme\":\"Light\"}");
        var service = new DeskBoxDataBackupService(_appDataRoot);
        await service.PrepareRestoreAsync(archivePath);
        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllTextAsync(service.PendingRestoreMarkerPath));
        string stagingRoot = marker.RootElement.GetProperty("stagingRoot").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(stagingRoot, "data", "settings.json"), "{ invalid json");

        DeskBoxRestoreApplyResult result = await service.ApplyPendingRestoreAsync();

        Assert.True(result.HadPendingRestore);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
        Assert.Contains("Light", await File.ReadAllTextAsync(currentSettingsPath));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static async Task<string> WaitForStagedFileAsync(
        string stagingDirectory,
        string relativePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            if (Directory.Exists(stagingDirectory))
            {
                string? stagedFile = Directory
                    .EnumerateDirectories(stagingDirectory)
                    .Select(snapshotRoot => Path.Combine(snapshotRoot, relativePath))
                    .FirstOrDefault(File.Exists);
                if (stagedFile is not null)
                {
                    return stagedFile;
                }
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException($"Timed out waiting for staged file '{relativePath}'.");
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
        catch
        {
        }
    }
}
