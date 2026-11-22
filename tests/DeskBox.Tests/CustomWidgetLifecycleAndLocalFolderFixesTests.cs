using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetLifecycleAndLocalFolderFixesTests
{
    [Fact]
    public async Task ReloadManifestFromDisk_WhenWidgetDirectoryDeleted_DoesNotResurrectDirectoryOrDefaultTemplate()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string widgetId = "custom_deleted_reload_test";
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(widgetId, tempRoot);
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.deleted.reload.test",
              "name": "Deleted Reload Test",
              "icon": "🧪",
              "defaultSize": { "width": 280, "height": 180 },
              "root": "root",
              "elements": {
                "root": {
                  "type": "stack",
                  "children": ["alive_text"]
                },
                "alive_text": {
                  "type": "text",
                  "text": "Alive"
                }
              }
            }
            """;
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);
            Assert.True(CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId, tempRoot));

            var config = new WidgetConfig
            {
                Id = widgetId,
                Name = "Deleted Reload Test",
                WidgetKind = WidgetKind.Custom
            };
            using var vm = new CustomWidgetViewModel(config, locSvc, store, settingsService: settingsSvc);
            await vm.InitializeAsync();

            Directory.Delete(store.WidgetDirectory, recursive: true);
            Assert.False(CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId, tempRoot));

            await vm.ReloadManifestFromDiskAsync(notifyFeedback: false);

            Assert.False(CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId, tempRoot));
            Assert.False(Directory.Exists(store.WidgetDirectory));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DiagnosticArchive_CapsAtTenEntries_ScrubsSecrets_AndSurvivesWidgetDeletion()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string widgetId = "custom_deleted_widget_1";
            var store = new CustomWidgetDataStore(widgetId, tempRoot);
            Assert.True(Directory.Exists(store.WidgetDirectory));

            var archive = new CustomWidgetDiagnosticArchive(tempRoot);
            for (int i = 1; i <= 12; i++)
            {
                archive.Record(new StudioDiagnosticArchiveEntry(
                    EntryId: string.Empty,
                    CreatedAtUtc: string.Empty,
                    EventKind: "studio-edit",
                    WidgetId: widgetId,
                    ManifestId: "deskbox.diag.test",
                    ProviderId: "custom",
                    Model: "test-model",
                    ThinkingMode: "Disabled",
                    Success: i % 2 == 0,
                    WasAutoRepaired: false,
                    LlmCalls: 1,
                    ToolRounds: 0,
                    RepairRounds: 0,
                    ElapsedMs: 120 + i,
                    UserPrompt: $"Turn #{i} with Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9 and sk-abcdefghijkl123456",
                    SummaryOrError: "Key {\"apiKey\":\"super-secret-value\"}",
                    Diagnostics: [$"diag #{i} sk-xyz9876543210abcd"],
                    ManifestJson: $"{{\"schemaVersion\":1,\"id\":\"deskbox.diag.test\",\"name\":\"T{i}\",\"root\":\"root\",\"elements\":{{\"root\":{{\"type\":\"Text\",\"text\":\"T{i}\"}}}}}}",
                    LogicJs: null));
            }

            Directory.Delete(store.WidgetDirectory, recursive: true);
            Assert.False(Directory.Exists(store.WidgetDirectory));
            Assert.True(File.Exists(archive.ArchiveFilePath));

            IReadOnlyList<StudioDiagnosticArchiveEntry> recent = archive.ReadRecent();
            Assert.Equal(CustomWidgetDiagnosticArchive.MaxEntries, recent.Count);
            Assert.Contains("Turn #12", recent[0].UserPrompt, StringComparison.Ordinal);
            Assert.Contains("Turn #3", recent[^1].UserPrompt, StringComparison.Ordinal);

            string rawJson = File.ReadAllText(archive.ArchiveFilePath);
            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-abcdefghijkl123456", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-value", rawJson, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-xyz9876543210abcd", rawJson, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", rawJson, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LocalFolderSource_SupportsSortOrders_ExpressionSort_AndReloadRefresh()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-folder-sort-{Guid.NewGuid():N}");
        string watchDir = Path.Combine(tempRoot, "watched");
        Directory.CreateDirectory(watchDir);
        try
        {
            // alpha.txt: largest (3000 bytes), oldest
            // middle.txt: medium (1500 bytes), middle mtime
            // omega.txt: smallest (200 bytes), newest
            string alphaPath = Path.Combine(watchDir, "alpha.txt");
            string middlePath = Path.Combine(watchDir, "middle.txt");
            string omegaPath = Path.Combine(watchDir, "omega.txt");
            File.WriteAllText(alphaPath, new string('A', 3000));
            File.WriteAllText(middlePath, new string('M', 1500));
            File.WriteAllText(omegaPath, new string('Z', 200));

            DateTime baseUtc = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(alphaPath, baseUtc);
            File.SetLastWriteTimeUtc(middlePath, baseUtc.AddMinutes(10));
            File.SetLastWriteTimeUtc(omegaPath, baseUtc.AddMinutes(20));

            const string widgetId = "custom_folder_sort_test";
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var trustStore = new CustomWidgetTrustStore(tempRoot);
            trustStore.RegisterWidgetProvenance(widgetId, trustedLocalAuthoring: true);
            trustStore.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.LocalFileAccess, enabled: true, defaultTrustedIfNew: true);

            var store = new CustomWidgetDataStore(widgetId, tempRoot);
            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "custom.folder.sort.test",
              "name": "Folder Sort Test",
              "icon": "📂",
              "defaultSize": { "width": 280, "height": 200 },
              "settings": [
                {
                  "key": "sortOrder",
                  "type": "select",
                  "label": "Sort",
                  "default": "modifiedDesc",
                  "options": [
                    { "label": "Newest", "value": "modifiedDesc" },
                    { "label": "Largest", "value": "sizeDesc" }
                  ]
                }
              ],
              "collections": { "files": {} },
              "sources": [
                {
                  "id": "dirSrc",
                  "type": "localFolder",
                  "path": {{JsonSerializer.Serialize(watchDir)}},
                  "targetCollection": "files",
                  "sort": "${$settings.sortOrder}",
                  "intervalMs": 30000
                }
              ],
              "root": "root",
              "elements": {
                "root": {
                  "type": "stack",
                  "children": ["file_count"]
                },
                "file_count": {
                  "type": "text",
                  "text": "${collections.files.length}"
                }
              }
            }
            """;
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            var config = new WidgetConfig
            {
                Id = widgetId,
                Name = "Folder Sort Test",
                WidgetKind = WidgetKind.Custom
            };
            using var vm = new CustomWidgetViewModel(config, locSvc, store, settingsService: settingsSvc)
            {
                TrustStoreOverride = trustStore
            };
            await vm.InitializeAsync();
            await vm.RefreshFileTextSourcesNowAsync(targetSourceId: null, respectMinInterval: false);

            Assert.True(vm.Collections.TryGetValue("files", out List<CustomWidgetRecord>? initialFiles));
            Assert.Equal(["omega.txt", "middle.txt", "alpha.txt"], initialFiles!.Select(r => r.Fields["name"]).ToArray());

            // Update setting expression -> SetSettingValueAsync refreshes and re-sorts immediately.
            await vm.SetSettingValueAsync("sortOrder", "sizeDesc");
            Assert.Equal(["alpha.txt", "middle.txt", "omega.txt"], vm.Collections["files"].Select(r => r.Fields["name"]).ToArray());

            // Simulate external edit to manifest.json changing sort to nameDesc -> ReloadManifestFromDiskAsync refreshes immediately.
            string updatedManifest = manifestJson.Replace("\"${$settings.sortOrder}\"", "\"nameDesc\"", StringComparison.Ordinal);
            await store.WriteManifestJsonAsync(updatedManifest);
            await vm.ReloadManifestFromDiskAsync(notifyFeedback: false);
            Assert.Equal(["omega.txt", "middle.txt", "alpha.txt"], vm.Collections["files"].Select(r => r.Fields["name"]).ToArray());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PathResolution_ExpandsKnownUserFolders_AndRejectsAmbiguousRelativePaths()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));

        string expandedDownloads = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder("Downloads");
        string expandedDownloadsSub = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder("Downloads/sub");
        string expandedTildeSub = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder("~/test");
        string expandedDesktop = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder("Desktop");

        Assert.True(Path.IsPathFullyQualified(expandedDownloads), $"Expected rooted Downloads path, got '{expandedDownloads}'");
        Assert.True(Path.IsPathFullyQualified(expandedDownloadsSub), $"Expected rooted Downloads/sub path, got '{expandedDownloadsSub}'");
        Assert.Equal(Path.Combine(expandedDownloads, "sub"), expandedDownloadsSub);
        Assert.Equal(Path.Combine(userProfile, "test"), expandedTildeSub);
        Assert.True(Path.IsPathFullyQualified(expandedDesktop), $"Expected rooted Desktop path, got '{expandedDesktop}'");

        Assert.True(
            CustomWidgetLocalFileEngine.TryResolveSafeLocalPath("Downloads", forWrite: false, out string safeDownloads, out string? dlErr),
            dlErr);
        Assert.True(Path.IsPathFullyQualified(safeDownloads));

        Assert.False(CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(
            "ambiguous-relative-folder/sub",
            forWrite: false,
            out _,
            out string? err));
        Assert.NotNull(err);
        Assert.Contains("ambiguous", err, StringComparison.OrdinalIgnoreCase);
    }
}
