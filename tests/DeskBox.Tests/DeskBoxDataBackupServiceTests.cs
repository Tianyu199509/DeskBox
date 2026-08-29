using System.IO.Compression;
using System.Security.Cryptography;
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
        string manifestJson;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifestJson = await reader.ReadToEndAsync();
        }

        Assert.Contains("\n  \"schemaVersion\": 2", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", manifestJson, StringComparison.Ordinal);
        using JsonDocument manifest = JsonDocument.Parse(manifestJson);
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("manual", manifest.RootElement.GetProperty("kind").GetString());
        JsonElement[] files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, file =>
        {
            Assert.Equal(JsonValueKind.String, file.GetProperty("path").ValueKind);
            Assert.Equal(JsonValueKind.Number, file.GetProperty("length").ValueKind);
            Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length);
            Assert.False(file.TryGetProperty("Path", out _));
            Assert.False(file.TryGetProperty("Length", out _));
            Assert.False(file.TryGetProperty("Sha256", out _));
        });
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
    public async Task CreateAutomaticSnapshotNowAsync_CreatesSnapshotEvenWhenOneAlreadyExistsToday()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);

        string? firstPath = await service.CreateAutomaticSnapshotNowAsync();
        string? secondPath = await service.CreateAutomaticSnapshotNowAsync();

        Assert.NotNull(firstPath);
        Assert.NotNull(secondPath);
        Assert.NotEqual(firstPath, secondPath);
        Assert.Equal(2, Directory.EnumerateFiles(service.AutomaticSnapshotDirectory, "*.zip").Count());
    }

    [Fact]
    public async Task AutomaticSnapshot_IsStoredOutsideAppDataAndCanBeDiscoveredAfterReinstall()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{\"theme\":\"Dark\"}");
        string recoveryRoot = Path.Combine(_tempRoot, "DeskBox-Recovery");
        var service = new DeskBoxDataBackupService(_appDataRoot, recoveryRoot);

        string snapshotPath = Assert.IsType<string>(await service.CreateAutomaticSnapshotNowAsync());

        Assert.True(File.Exists(snapshotPath));
        Assert.StartsWith(recoveryRoot, snapshotPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(snapshotPath.StartsWith(_appDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        Directory.Delete(_appDataRoot, recursive: true);
        DeskBoxBackupSnapshotInfo? recoverySnapshot = await service.GetLatestRecoverySnapshotAsync();

        Assert.NotNull(recoverySnapshot);
        Assert.Equal(snapshotPath, recoverySnapshot.Path, ignoreCase: true);
        Assert.True(recoverySnapshot.IsReadable);
    }

    [Fact]
    public async Task SnapshotInventory_ReportsUnreadableEntriesAndAllowsManagedDeletion()
    {
        string dataDirectory = Directory.CreateDirectory(Path.Combine(_appDataRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "settings.json"), "{}");
        var service = new DeskBoxDataBackupService(_appDataRoot);
        string snapshotPath = Assert.IsType<string>(await service.CreateAutomaticSnapshotNowAsync());
        Directory.CreateDirectory(service.PreRestoreBackupDirectory);
        string unreadablePath = Path.Combine(service.PreRestoreBackupDirectory, "DeskBox-PreRestore-broken.zip");
        await File.WriteAllTextAsync(unreadablePath, "not a zip archive");

        IReadOnlyList<DeskBoxBackupSnapshotInfo> snapshots = await service.GetSnapshotInventoryAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.Contains(snapshots, snapshot => snapshot.Path == snapshotPath && snapshot.IsReadable);
        Assert.Contains(snapshots, snapshot => snapshot.Path == unreadablePath && !snapshot.IsReadable);
        Assert.True(await service.DeleteSnapshotAsync(unreadablePath));
        Assert.False(File.Exists(unreadablePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSnapshotAsync(Path.Combine(_exportRoot, "outside.zip")));
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
            WidgetCompactWidthMode = SettingsService.WidgetCompactWidthModeIndependent,
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
        Assert.Equal(
            SettingsService.WidgetCompactWidthModeIndependent,
            restoredSettings.WidgetCompactWidthMode);
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
    public async Task PrepareRestoreAsync_AcceptsUnknownControlFieldsAndWritesCanonicalPendingMarker()
    {
        const string settingsJson = "{}";
        byte[] settingsBytes = System.Text.Encoding.UTF8.GetBytes(settingsJson);
        string settingsSha256 = Convert.ToHexString(SHA256.HashData(settingsBytes));
        string archivePath = Path.Combine(_exportRoot, "future-control-fields.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                $$"""
                {
                  "schemaVersion": 2,
                  "kind": "manual",
                  "createdAtUtc": "2026-07-01T00:00:00Z",
                  "appVersion": "1.2.9",
                  "files": [
                    {
                      "path": "settings.json",
                      "length": {{settingsBytes.Length}},
                      "sha256": "{{settingsSha256}}",
                      "futureFileField": { "ignored": true }
                    }
                  ],
                  "futureManifestField": true
                }
                """);
            WriteEntry(archive, "data/settings.json", settingsJson);
        }
        var service = new DeskBoxDataBackupService(_appDataRoot);

        DeskBoxRestorePreparation preparation = await service.PrepareRestoreAsync(archivePath);

        Assert.Equal(2, preparation.BackupSchemaVersion);
        Assert.True(preparation.HasIntegrityManifest);
        string markerJson = await File.ReadAllTextAsync(service.PendingRestoreMarkerPath);
        Assert.Contains("\n  \"stagingRoot\":", markerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"StagingRoot\"", markerJson, StringComparison.Ordinal);
        using JsonDocument marker = JsonDocument.Parse(markerJson);
        Assert.Equal(
            new[]
            {
                "appVersion",
                "archivePath",
                "backupCreatedAtUtc",
                "preparedAtUtc",
                "stagingRoot"
            },
            marker.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        string stagingRoot = marker.RootElement.GetProperty("stagingRoot").GetString()!;
        Assert.True(Directory.Exists(stagingRoot));

        string trimmedMarker = markerJson.TrimEnd();
        string markerWithUnknownField =
            trimmedMarker[..^1] + ",\n  \"futureMarkerField\": true\n}";
        await File.WriteAllTextAsync(service.PendingRestoreMarkerPath, markerWithUnknownField);
        await service.CancelPendingRestoreAsync();

        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public async Task PrepareRestoreAsync_RejectsPascalCaseControlManifestProperties()
    {
        string archivePath = Path.Combine(_exportRoot, "pascal-case-manifest.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "manifest.json",
                "{\"SchemaVersion\":1,\"Kind\":\"manual\",\"CreatedAtUtc\":\"2026-07-01T00:00:00Z\",\"AppVersion\":\"1.2.9\"}");
            WriteEntry(archive, "data/settings.json", "{}");
        }
        var service = new DeskBoxDataBackupService(_appDataRoot);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareRestoreAsync(archivePath));

        Assert.False(File.Exists(service.PendingRestoreMarkerPath));
    }

    [Fact]
    public async Task PrepareRestoreAsync_PreservesCaseInsensitiveUserDataAndEnumCompatibility()
    {
        string archivePath = Path.Combine(_exportRoot, "mixed-case-user-data.zip");
        string sourceDataPath = Path.Combine(_tempRoot, "legacy-source", "data");
        string quickRelativePath = Path.Combine(
            "quick-capture",
            "attachments",
            "mixed-note",
            "image.png");
        string todoRelativePath = Path.Combine(
            "widgets",
            "todo-widget",
            "attachments",
            "mixed-task",
            "spec.pdf");
        string sourceQuickAttachment = Path.Combine(sourceDataPath, quickRelativePath);
        string sourceTodoAttachment = Path.Combine(sourceDataPath, todoRelativePath);
        string sourceQuickAttachmentJson = JsonSerializer.Serialize(sourceQuickAttachment);
        string sourceTodoAttachmentJson = JsonSerializer.Serialize(sourceTodoAttachment);
        string manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "manual",
            createdAtUtc = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            appVersion = "1.2.9",
            sourceDataPath
        });
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "manifest.json", manifestJson);
            WriteEntry(
                archive,
                "data/settings.json",
                """
                {
                  "LANGUAGE": "zh-CN",
                  "WIDGETS": [
                    {
                      "ID": "mixed-files",
                      "WIDGETKIND": "File",
                      "VIEWMODE": "List",
                      "SORTMODE": 3,
                      "FUTUREWIDGETFIELD": true
                    }
                  ],
                  "FUTUREROOTFIELD": true
                }
                """);
            WriteEntry(
                archive,
                "data/quick-capture/quick-capture.json",
                $$"""
                {
                  "VERSION": 4,
                  "CURRENTVIEW": "Pinned",
                  "ITEMS": [
                    {
                      "ID": "mixed-note",
                      "TYPE": "Link",
                      "APPEARANCEPRESET": 1,
                      "SOURCEKIND": "Clipboard",
                      "ATTACHMENTS": [
                        {
                          "FILEPATH": {{sourceQuickAttachmentJson}},
                          "STORAGEMODE": "managed",
                          "FUTUREATTACHMENTFIELD": true
                        }
                      ],
                      "FUTUREITEMFIELD": "ignored"
                    }
                  ],
                  "FUTUREROOTFIELD": true
                }
                """);
            WriteEntry(
                archive,
                "data/widgets/todo-widget/todo.json",
                $$"""
                {
                  "VERSION": 3,
                  "ITEMS": [
                    {
                      "ID": "mixed-task",
                      "TEXT": "Mixed case task",
                      "ATTACHMENTS": [
                        {
                          "FILEPATH": {{sourceTodoAttachmentJson}},
                          "STORAGEMODE": "managed",
                          "FUTUREATTACHMENTFIELD": true
                        }
                      ],
                      "FUTUREITEMFIELD": "ignored"
                    }
                  ],
                  "FUTUREROOTFIELD": true
                }
                """);
            WriteEntry(
                archive,
                "data/quick-capture/attachments/mixed-note/image.png",
                "quick attachment");
            WriteEntry(
                archive,
                "data/widgets/todo-widget/attachments/mixed-task/spec.pdf",
                "todo attachment");
        }
        var service = new DeskBoxDataBackupService(_appDataRoot);

        DeskBoxRestorePreparation preparation = await service.PrepareRestoreAsync(archivePath);

        Assert.Equal(1, preparation.BackupSchemaVersion);
        using JsonDocument marker = JsonDocument.Parse(
            await File.ReadAllTextAsync(service.PendingRestoreMarkerPath));
        string stagingRoot = marker.RootElement.GetProperty("stagingRoot").GetString()!;
        string stagedData = Path.Combine(stagingRoot, "data");
        using JsonDocument quick = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(stagedData, "quick-capture", "quick-capture.json")));
        JsonElement quickItem = Assert.Single(
            quick.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Pinned", quick.RootElement.GetProperty("currentView").GetString());
        Assert.Equal("Link", quickItem.GetProperty("type").GetString());
        Assert.Equal("Paper", quickItem.GetProperty("appearancePreset").GetString());
        Assert.Equal("Clipboard", quickItem.GetProperty("sourceKind").GetString());
        Assert.False(quick.RootElement.TryGetProperty("CURRENTVIEW", out _));
        Assert.False(quick.RootElement.TryGetProperty("FUTUREROOTFIELD", out _));
        Assert.Equal(
            Path.Combine(service.DataDirectory, quickRelativePath),
            Assert.Single(quickItem.GetProperty("attachments").EnumerateArray())
                .GetProperty("filePath")
                .GetString(),
            ignoreCase: true);

        using JsonDocument todo = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(stagedData, "widgets", "todo-widget", "todo.json")));
        JsonElement todoItem = Assert.Single(todo.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(todo.RootElement.TryGetProperty("ITEMS", out _));
        Assert.False(todo.RootElement.TryGetProperty("FUTUREROOTFIELD", out _));
        Assert.Equal(
            Path.Combine(service.DataDirectory, todoRelativePath),
            Assert.Single(todoItem.GetProperty("attachments").EnumerateArray())
                .GetProperty("filePath")
                .GetString(),
            ignoreCase: true);
        Assert.Contains(
            "\"LANGUAGE\"",
            await File.ReadAllTextAsync(Path.Combine(stagedData, "settings.json")),
            StringComparison.Ordinal);

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
