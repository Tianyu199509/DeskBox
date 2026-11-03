using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class Phase3EcosystemFlywheelTests
{
    [Fact]
    public async Task RemoteCatalogV2_BundleUrlZipAndLogicUrl_HydratesFullBundle_13LocaleMetadata_AndRejectsMissingLogicJs()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p3-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string musicManifestJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.music-pulse",
              "version": "2.1.0",
              "name": "${$i18n.meta.name}",
              "description": "${$i18n.meta.desc}",
              "icon": "🎵",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "sources": [
                { "type": "music", "intervalMs": 1000 }
              ],
              "view": {
                "type": "Card",
                "children": [
                  { "type": "Text", "text": "${$i18n.ui.header}: ${$computed.trackLabel}" }
                ]
              }
            }
            """;

            const string musicLogicJs = """
            export function onInit(ctx) {
              const title = (ctx.env.music && ctx.env.music.title) || 'Idle';
              ctx.computed.trackLabel = title;
            }
            """;

            var zipLocales = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["zh-CN"] = new Dictionary<string, string>
                {
                  ["meta.name"] = "灵动音律",
                  ["meta.desc"] = "实时桌面音乐律动卡片",
                  ["ui.header"] = "正在播放"
                },
                ["en-US"] = new Dictionary<string, string>
                {
                  ["meta.name"] = "Music Pulse",
                  ["meta.desc"] = "Live desktop media card",
                  ["ui.header"] = "Now Playing"
                },
                ["ja-JP"] = new Dictionary<string, string>
                {
                  ["meta.name"] = "ミュージックパルス",
                  ["meta.desc"] = "ライブ音楽ウィジェット",
                  ["ui.header"] = "再生中"
                },
                ["ar-SA"] = new Dictionary<string, string>
                {
                  ["meta.name"] = "نبض الموسيقى",
                  ["meta.desc"] = "بطاقة وسائط مباشرة",
                  ["ui.header"] = "قيد التشغيل"
                }
            };
            CustomWidgetLocaleMeta meta = CustomWidgetLocaleMeta.BuildFromBaseDictionary(
                "zh-CN",
                zipLocales["zh-CN"]);
            var bundleCatalog = new CustomWidgetLocaleCatalog(meta, zipLocales);
            byte[] zipBytes = BuildWidgetPackageZipBytes(musicManifestJson, musicLogicJs, bundleCatalog);

            const string todoManifestJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.todo-radar",
              "version": "1.4.0",
              "name": "${$i18n.meta.name}",
              "description": "${$i18n.meta.desc}",
              "icon": "✅",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "collections": { "todos": { "maxItems": 10 } },
              "sources": [
                { "type": "todo", "targetCollection": "todos", "maxItems": 10, "intervalMs": 30000 }
              ],
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$computed.headline}" }
                ]
              }
            }
            """;

            const string todoLogicJs = """
            export function onInit(ctx) {
              ctx.computed.headline = `${ctx.env.todo ? ctx.env.todo.pendingCount : 0} pending`;
            }
            """;

            const string brokenManifestWithoutScript = """
            {
              "schemaVersion": 1,
              "id": "deskbox.broken-scriptless",
              "version": "1.0.0",
              "name": "Broken",
              "script": { "entry": "logic.js" },
              "view": { "type": "Text", "text": "broken" }
            }
            """;

            string catalogJson = $$"""
            {
              "schemaVersion": 2,
              "updatedAtUtc": "2026-11-08T00:00:00Z",
              "widgets": [
                {
                  "id": "music-pulse",
                  "manifestId": "deskbox.music-pulse",
                  "name": "灵动音律",
                  "nameEn": "Music Pulse",
                  "names": {
                    "zh-CN": "灵动音律",
                    "zh-TW": "靈動音律",
                    "en-US": "Music Pulse",
                    "ja-JP": "ミュージックパルス",
                    "ar-SA": "نبض الموسيقى"
                  },
                  "description": "实时同步系统播放状态",
                  "descriptionEn": "Syncs Windows media playback",
                  "descriptions": {
                    "zh-CN": "实时同步系统播放状态",
                    "ja-JP": "システムの再生状態を同期",
                    "ar-SA": "مزامنة حالة تشغيل الوسائط"
                  },
                  "featureTag": "音频联动",
                  "featureTagEn": "Media Sync",
                  "featureTags": {
                    "ja-JP": "メディア連携",
                    "ar-SA": "مزامنة الصوت"
                  },
                  "version": "2.1.0",
                  "bundleUrl": "https://deskbox.fun/widgets/bundles/music-pulse.deskbox-widget"
                },
                {
                  "id": "todo-radar",
                  "manifestId": "deskbox.todo-radar",
                  "name": "待办雷达",
                  "nameEn": "Todo Radar",
                  "version": "1.4.0",
                  "manifestUrl": "https://deskbox.fun/widgets/manifests/todo-radar.json",
                  "logicUrl": "https://deskbox.fun/widgets/manifests/todo-radar.logic.js",
                  "baseLocale": "zh-CN",
                  "locales": {
                    "zh-CN": { "meta.name": "待办雷达", "meta.desc": "原生待办进度概览" },
                    "en-US": { "meta.name": "Todo Radar", "meta.desc": "Native Todo progress radar" }
                  }
                },
                {
                  "id": "broken-scriptless",
                  "manifestId": "deskbox.broken-scriptless",
                  "name": "Broken",
                  "version": "1.0.0",
                  "manifestJson": {{JsonSerializer.Serialize(brokenManifestWithoutScript)}}
                }
              ]
            }
            """;

            var handler = new RoutingMockHttpMessageHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url == CustomWidgetRemoteCatalogService.PrimaryCatalogUrl)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
                    };
                    resp.Headers.ETag = new EntityTagHeaderValue("\"v2-test-etag\"");
                    return resp;
                }

                if (url == "https://deskbox.fun/widgets/bundles/music-pulse.deskbox-widget")
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zipBytes)
                    };
                    resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                    return resp;
                }

                if (url == "https://deskbox.fun/widgets/manifests/todo-radar.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(todoManifestJson, Encoding.UTF8, "application/json")
                    };
                }

                if (url == "https://deskbox.fun/widgets/manifests/todo-radar.logic.js")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(todoLogicJs, Encoding.UTF8, "text/javascript")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var catalogService = new CustomWidgetRemoteCatalogService(tempRoot, handler);
            bool refreshed = await catalogService.RefreshInBackgroundAsync(force: true);
            Assert.True(refreshed);

            IReadOnlyList<CustomWidgetCatalogEntry> merged = catalogService.GetMergedOfficialTemplates();
            CustomWidgetCatalogEntry musicEntry = Assert.Single(merged, e => e.Id == "music-pulse");
            CustomWidgetCatalogEntry todoEntry = Assert.Single(merged, e => e.Id == "todo-radar");
            Assert.DoesNotContain(merged, e => e.Id == "broken-scriptless");

            Assert.NotNull(musicEntry.LogicJs);
            Assert.Contains("ctx.env.music", musicEntry.LogicJs, StringComparison.Ordinal);
            Assert.NotNull(musicEntry.Locales);
            Assert.True(musicEntry.Locales!.HasAnyLocale);

            Assert.True(catalogService.TryGetRemoteTemplateBundle("music-pulse", out CustomWidgetTemplateBundle? musicBundle));
            Assert.NotNull(musicBundle);
            Assert.Contains("ctx.env.music", musicBundle!.LogicJs, StringComparison.Ordinal);
            Assert.Equal("ミュージックパルス", musicBundle.Locales!.Locales["ja-JP"]["meta.name"]);

            Assert.True(catalogService.TryGetRemoteTemplateBundle("todo-radar", out CustomWidgetTemplateBundle? todoBundle));
            Assert.NotNull(todoBundle);
            Assert.Contains("ctx.env.todo", todoBundle!.LogicJs, StringComparison.Ordinal);
            Assert.Equal("Todo Radar", todoBundle.Locales!.Locales["en-US"]["meta.name"]);

            // Verify 13-locale metadata lookup across CJK, RTL, and fallback cultures
            string settingsDir = Path.Combine(tempRoot, "settings");
            Directory.CreateDirectory(settingsDir);
            var settingsSvc = new SettingsService(settingsDir);
            var loc = new LocalizationService(settingsSvc);

            loc.SetLanguage("ja-JP");
            Assert.Equal("ミュージックパルス", musicEntry.ResolveDisplayName(loc));
            Assert.Equal("システムの再生状態を同期", musicEntry.ResolveDescription(loc));
            Assert.Equal("メディア連携", musicEntry.ResolveFeatureTag(loc));

            loc.SetLanguage("ar-SA");
            Assert.Equal("نبض الموسيقى", musicEntry.ResolveDisplayName(loc));
            Assert.Equal("مزامنة الصوت", musicEntry.ResolveFeatureTag(loc));

            loc.SetLanguage("zh-TW");
            Assert.Equal("靈動音律", musicEntry.ResolveDisplayName(loc));

            loc.SetLanguage("de-DE");
            Assert.Equal("Music Pulse", musicEntry.ResolveDisplayName(loc));

            // Verify cold-start offline reload from persisted disk bundle cache
            var offlineHandler = new RoutingMockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var coldCatalogService = new CustomWidgetRemoteCatalogService(tempRoot, offlineHandler);
            Assert.True(coldCatalogService.TryGetRemoteTemplateBundle("music-pulse", out CustomWidgetTemplateBundle? coldBundle));
            Assert.NotNull(coldBundle);
            Assert.Contains("ctx.env.music", coldBundle!.LogicJs, StringComparison.Ordinal);
            Assert.Equal("نبض الموسيقى", coldBundle.Locales!.Locales["ar-SA"]["meta.name"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task RemoteCatalogV2_OtaUpgrade_UpdatesOfficialWidgetFullBundle_WhilePreservingForkedOrCustomizedWidgets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p3-ota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cacheDir = Path.Combine(tempRoot, "catalog-cache");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(widgetsDir);

            const string v1OfficialJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.ota-demo",
              "version": "1.0.0",
              "origin": "official",
              "isCustomized": false,
              "name": "OTA Demo",
              "view": { "type": "Text", "text": "v1" }
            }
            """;

            const string v2RemoteManifest = """
            {
              "schemaVersion": 1,
              "id": "deskbox.ota-demo",
              "version": "2.0.0",
              "name": "${$i18n.meta.name}",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "view": { "type": "Text", "text": "${$computed.msg}" }
            }
            """;

            const string v2RemoteLogic = """
            export function onInit(ctx) {
              ctx.computed.msg = 'upgraded-v2';
            }
            """;

            string catalogJson = $$"""
            {
              "schemaVersion": 2,
              "updatedAtUtc": "2026-11-08T00:00:00Z",
              "widgets": [
                {
                  "id": "ota-demo",
                  "manifestId": "deskbox.ota-demo",
                  "name": "OTA 演示",
                  "nameEn": "OTA Demo",
                  "version": "2.0.0",
                  "manifestJson": {{JsonSerializer.Serialize(v2RemoteManifest)}},
                  "logicJs": {{JsonSerializer.Serialize(v2RemoteLogic)}},
                  "baseLocale": "zh-CN",
                  "locales": {
                    "zh-CN": { "meta.name": "OTA 演示 v2" },
                    "ja-JP": { "meta.name": "OTA デモ v2" }
                  }
                }
              ]
            }
            """;

            var catalogService = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new RoutingMockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            Assert.True(await catalogService.ApplyCatalogJsonAsync(catalogJson, "\"ota-etag\"", CustomWidgetRemoteCatalogService.PrimaryCatalogUrl));

            // 1. Official unmodified widget gets atomically upgraded with manifest.json + logic.js + locales/
            var officialStore = new CustomWidgetDataStore("w-official", widgetsDir);
            await officialStore.WriteWorkspaceBundleAsync(v1OfficialJson, logicJs: null, locales: null);

            string upgradedJson = await officialStore.ReadOrCreateManifestJsonAsync(catalogOverride: catalogService);
            CustomWidgetManifest? upgradedManifest = CustomWidgetManifestParser.Parse(upgradedJson).Manifest;
            Assert.NotNull(upgradedManifest);
            Assert.Equal("2.0.0", upgradedManifest!.Version);
            Assert.Equal(CustomWidgetOriginKinds.Official, upgradedManifest.Origin);
            Assert.False(upgradedManifest.IsCustomized);
            Assert.True(File.Exists(officialStore.LogicJsPath));
            Assert.Contains("upgraded-v2", await File.ReadAllTextAsync(officialStore.LogicJsPath), StringComparison.Ordinal);

            CustomWidgetLocaleCatalog upgradedLocales = officialStore.ReadLocaleCatalog();
            Assert.Equal("OTA デモ v2", upgradedLocales.Locales["ja-JP"]["meta.name"]);

            // 2. Customized/Forked widget must NEVER be overwritten by OTA
            string customizedJson = CustomWidgetManifestMetadataStamper.StampProvenance(
                v1OfficialJson,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: true);
            var customizedStore = new CustomWidgetDataStore("w-customized", widgetsDir);
            await customizedStore.WriteWorkspaceBundleAsync(customizedJson, logicJs: null, locales: null);

            string keptJson = await customizedStore.ReadOrCreateManifestJsonAsync(catalogOverride: catalogService);
            CustomWidgetManifest? keptManifest = CustomWidgetManifestParser.Parse(keptJson).Manifest;
            Assert.NotNull(keptManifest);
            Assert.Equal("1.0.0", keptManifest!.Version);
            Assert.False(File.Exists(customizedStore.LogicJsPath));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void FirstRunSetupOnboarding_DetectsMissingSecretsAndSanitizedPaths_OrdersFieldsAndExpandsUserProfile()
    {
        const string importedManifestJson = """
        {
          "schemaVersion": 1,
          "id": "community.github-notes",
          "name": "GitHub & Notes Sync",
          "settings": [
            { "key": "refreshMinutes", "label": "Refresh Minutes", "type": "number", "default": "15" },
            { "key": "compactMode", "label": "Compact Mode", "type": "toggle", "default": "true" },
            { "key": "notesFolder", "label": "Obsidian Vault Folder", "type": "text", "default": "%USERPROFILE%\\Documents\\Obsidian" },
            { "key": "configFile", "label": "Config File Path", "type": "text", "default": "" },
            { "key": "githubToken", "label": "GitHub Personal Token", "type": "secret", "default": "" }
          ],
          "view": { "type": "Text", "text": "ok" }
        }
        """;

        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(importedManifestJson).Manifest!;
        CustomWidgetSetupInspection initial = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            activeCulture: "en-US",
            currentSettings: null,
            currentSecrets: null);

        Assert.True(initial.HasAnyConfigurableFields);
        Assert.True(initial.RequiresAttention);
        Assert.Equal(1, initial.UnconfiguredSecretCount);
        Assert.Equal(2, initial.UnconfiguredPathCount);
        Assert.Equal(5, initial.Fields.Count);

        // Secret needing attention comes first, followed by path fields needing attention, then optional preferences
        Assert.Equal("githubToken", initial.Fields[0].Definition.Key);
        Assert.True(initial.Fields[0].IsSecret);
        Assert.True(initial.Fields[0].NeedsImmediateAttention);

        var pathKeys = initial.Fields.Skip(1).Take(2).Select(static f => f.Definition.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("notesFolder", pathKeys);
        Assert.Contains("configFile", pathKeys);

        CustomWidgetSetupFieldSpec folderField = initial.Fields.Single(f => f.Definition.Key == "notesFolder");
        CustomWidgetSetupFieldSpec fileField = initial.Fields.Single(f => f.Definition.Key == "configFile");
        Assert.True(folderField.IsPathLike);
        Assert.False(folderField.IsFilePathLike);
        Assert.True(fileField.IsPathLike);
        Assert.True(fileField.IsFilePathLike);

        string expanded = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder("%USERPROFILE%\\Documents\\Obsidian");
        Assert.DoesNotContain("%USERPROFILE%", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Documents", "Obsidian"), expanded, StringComparison.OrdinalIgnoreCase);

        // Once user configures secret and concrete paths, RequiresAttention clears
        var configuredSettings = new Dictionary<string, string>
        {
            ["notesFolder"] = @"D:\Notes\Obsidian",
            ["configFile"] = @"D:\Notes\config.toml"
        };
        var configuredSecrets = new Dictionary<string, string>
        {
            ["githubToken"] = "ghp_valid_token_value_123456"
        };

        CustomWidgetSetupInspection afterSetup = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
            manifest,
            activeCulture: "en-US",
            currentSettings: configuredSettings,
            currentSecrets: configuredSecrets);

        Assert.False(afterSetup.RequiresAttention);
        Assert.Equal(0, afterSetup.UnconfiguredSecretCount);
        Assert.Equal(0, afterSetup.UnconfiguredPathCount);
    }

    [Fact]
    public void OneClickFork_PreservesLogicJsAnd13Locales_StampsLineage_AndPassesWorkspaceValidation()
    {
        CustomWidgetTemplateBundle sourceBundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.PomodoroTemplateId);
        Assert.NotNull(sourceBundle.Locales);
        Assert.True(sourceBundle.Locales!.HasAnyLocale);

        const string customLogicJs = """
        export function onInit(ctx) {
          ctx.computed.forkReady = 'yes';
        }
        """;
        string sourceWithScript = CustomWidgetTemplates.SyncManifestScriptPropertyJson(sourceBundle.ManifestJson, customLogicJs);

        CustomWidgetForkBundleResult forked = CustomWidgetOnboardingHelper.CreateForkBundle(
            sourceWithScript,
            customLogicJs,
            sourceBundle.Locales,
            activeCulture: "zh-CN",
            forkTitleSuffix: " (二创)");

        Assert.StartsWith("user.fork-", forked.NewManifestId, StringComparison.Ordinal);
        Assert.Equal("deskbox.pomodoro", forked.SourceManifestId);
        Assert.EndsWith("(二创)", forked.DisplayTitle, StringComparison.Ordinal);
        Assert.Equal(customLogicJs, forked.LogicJs);
        Assert.NotNull(forked.Locales);
        Assert.True(forked.Locales!.HasAnyLocale);

        CustomWidgetParseResult parsedFork = CustomWidgetManifestParser.Parse(forked.ManifestJson);
        Assert.NotNull(parsedFork.Manifest);
        Assert.Equal(forked.NewManifestId, parsedFork.Manifest!.Id);
        Assert.Equal(CustomWidgetOriginKinds.Local, parsedFork.Manifest.Origin);
        Assert.True(parsedFork.Manifest.IsCustomized);
        Assert.Equal("deskbox.pomodoro", parsedFork.Manifest.ForkedFromId);
        Assert.False(string.IsNullOrWhiteSpace(parsedFork.Manifest.ForkedFromVersion));

        // Verify locales + _meta.json hashes pass full workspace validation on Pomodoro 13-locale bundle
        CustomWidgetWorkspaceValidationResult bundleValidation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            forked.ManifestJson,
            forked.LogicJs,
            forked.Locales);
        Assert.DoesNotContain(bundleValidation.Diagnostics, static d => d.IsError);

        CustomWidgetManifest hydratedFork = parsedFork.Manifest.WithLocales(forked.Locales);
        Assert.EndsWith("(二创)", hydratedFork.ResolveLocalizedName("zh-CN"), StringComparison.Ordinal);
        Assert.EndsWith("(派生)", hydratedFork.ResolveLocalizedName("ja-JP"), StringComparison.Ordinal);

        // Also verify ${$i18n.meta.name} binding in manifest.json stays intact when source uses ${$i18n.*}
        const string boundSourceJson = """
        {
          "schemaVersion": 1,
          "id": "deskbox.bound-demo",
          "version": "1.2.0",
          "name": "${$i18n.meta.name}",
          "view": { "type": "Text", "text": "${$i18n.meta.name}" }
        }
        """;
        var boundDict = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new Dictionary<string, string> { ["meta.name"] = "绑定标题" },
            ["en-US"] = new Dictionary<string, string> { ["meta.name"] = "Bound Title" }
        };
        var boundLocales = new CustomWidgetLocaleCatalog(
            CustomWidgetLocaleMeta.BuildFromBaseDictionary("zh-CN", boundDict["zh-CN"]),
            boundDict);
        CustomWidgetForkBundleResult boundFork = CustomWidgetOnboardingHelper.CreateForkBundle(
            boundSourceJson,
            logicJs: null,
            boundLocales,
            activeCulture: "zh-CN",
            forkTitleSuffix: " (二创)");
        CustomWidgetManifest parsedBoundFork = CustomWidgetManifestParser.Parse(boundFork.ManifestJson).Manifest!;
        Assert.Equal("${$i18n.meta.name}", parsedBoundFork.Name);
        Assert.Equal("绑定标题 (二创)", parsedBoundFork.WithLocales(boundFork.Locales!).ResolveLocalizedName("zh-CN"));
        Assert.Equal("Bound Title (Remix)", parsedBoundFork.WithLocales(boundFork.Locales!).ResolveLocalizedName("en-US"));

        // Two consecutive forks from the same source must never collide in ID
        CustomWidgetForkBundleResult secondFork = CustomWidgetOnboardingHelper.CreateForkBundle(
            sourceWithScript,
            customLogicJs,
            sourceBundle.Locales,
            activeCulture: "en-US",
            forkTitleSuffix: " (Remix)");
        Assert.NotEqual(forked.NewManifestId, secondFork.NewManifestId);
    }

    [Fact]
    public async Task NativeTodoSnapshotService_ComputesPrivacySafeMetrics_ExcludesDeletedItems_AndStripsPrivateNotesAndAttachments()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p3-todo-{Guid.NewGuid():N}");
        string widgetsRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;
        try
        {
            DateTimeOffset nowLocal = DateTimeOffset.Now;
            DateTimeOffset yesterday = new DateTimeOffset(nowLocal.Date.AddDays(-1), nowLocal.Offset);
            DateTimeOffset todayDue = new DateTimeOffset(nowLocal.Date.AddHours(23), nowLocal.Offset);

            var todoStore = new TodoWidgetStore(widgetsRoot, "todo-main");
            var data = new TodoWidgetData
            {
                Items =
                [
                    new TodoItem
                    {
                        Id = "t-overdue",
                        Text = "Ship Phase 3 Release",
                        IsImportant = true,
                        IsCompleted = false,
                        DueDate = yesterday,
                        HasDueTime = false,
                        Notes = "SUPER_SECRET_PRIVATE_NOTE_DO_NOT_LEAK",
                        Attachments =
                        [
                            new TodoAttachment { DisplayName = "private-contract.pdf", FilePath = @"D:\Secrets\private-contract.pdf" }
                        ]
                    },
                    new TodoItem
                    {
                        Id = "t-today",
                        Text = "Review 13-locale strings",
                        IsImportant = false,
                        IsCompleted = false,
                        DueDate = todayDue,
                        HasDueTime = false,
                        Steps =
                        [
                            new TodoStep { Text = "Step 1", IsCompleted = true },
                            new TodoStep { Text = "Step 2", IsCompleted = false }
                        ]
                    },
                    new TodoItem
                    {
                        Id = "t-done",
                        Text = "Write architecture blueprint",
                        IsCompleted = true,
                        CompletedAt = nowLocal
                    },
                    new TodoItem
                    {
                        Id = "t-deleted",
                        Text = "Trashed secret item",
                        IsDeleted = true,
                        Notes = "TRASHED_SECRET"
                    }
                ]
            };
            await todoStore.SaveAsync(data);

            CustomWidgetTodoSnapshot snap = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: null,
                maxItems: 10,
                widgetsDataRootOverride: widgetsRoot);

            Assert.Equal(3, snap.TotalCount);
            Assert.Equal(2, snap.PendingCount);
            Assert.Equal(1, snap.CompletedCount);
            Assert.Equal(1, snap.TodayPendingCount);
            Assert.Equal(1, snap.TodayCompletedCount);
            Assert.Equal(1, snap.OverdueCount);
            Assert.Equal(1, snap.ImportantPendingCount);
            Assert.Equal(33, snap.CompletionPercent);
            Assert.Equal("Ship Phase 3 Release", snap.NextTaskTitle);
            Assert.Equal("1/3 (33%)", snap.Summary);
            Assert.Equal(3, snap.Records.Count);

            CustomWidgetRecord firstRow = snap.Records[0];
            Assert.Equal("Ship Phase 3 Release", firstRow.GetField("title"));
            Assert.Equal("true", firstRow.GetField("isOverdue"));
            Assert.Equal("high", firstRow.GetField("priority"));

            CustomWidgetRecord secondRow = snap.Records.Single(r => r.GetField("id") == "t-today");
            Assert.Equal("1/2", secondRow.GetField("stepsSummary"));

            // Privacy boundary: neither deleted items nor private Notes / Attachments ever leak into snapshot records
            Assert.DoesNotContain(snap.Records, r => r.GetField("id") == "t-deleted");
            foreach (CustomWidgetRecord rec in snap.Records)
            {
                Assert.False(rec.Fields.ContainsKey("notes"));
                Assert.False(rec.Fields.ContainsKey("attachments"));
                foreach (string val in rec.Fields.Values)
                {
                    Assert.DoesNotContain("SUPER_SECRET_PRIVATE_NOTE_DO_NOT_LEAK", val, StringComparison.Ordinal);
                    Assert.DoesNotContain("private-contract.pdf", val, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AutoFixerAndViewModelRuntime_MusicAndTodoSnapshots_InterpolateTokens_DriveQuickJsCtxEnv_HandleSourceRefresh_AndRespectOcclusion()
    {
        // 1. Verify AutoFixer automatically wires missing music + todo sources and todos collection
        const string unWiredManifestJson = """
        {
          "schemaVersion": 1,
          "id": "user.focus-station",
          "name": "Focus Station",
          "script": { "entry": "logic.js", "tickIntervalMs": 0 },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$music.title} - ${$music.artist} (${$music.progressPercent}%)" },
              { "type": "Text", "text": "${$todo.summary} · Next: ${$todo.nextTaskTitle}" },
              { "type": "Text", "text": "${$computed.stationHeadline}" },
              {
                "type": "List",
                "collection": "todos",
                "titleField": "title"
              },
              {
                "type": "Button",
                "label": "Refresh",
                "onClick": { "type": "source.refresh" }
              }
            ]
          }
        }
        """;

        string wiredManifestJson = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(unWiredManifestJson);
        Assert.NotEqual(unWiredManifestJson, wiredManifestJson);

        CustomWidgetParseResult parsedWired = CustomWidgetManifestParser.Parse(wiredManifestJson);
        Assert.NotNull(parsedWired.Manifest);
        Assert.DoesNotContain(parsedWired.Diagnostics, static d => d.IsError);
        Assert.Contains(parsedWired.Manifest!.Sources, static s => s.Type == "music");
        Assert.Contains(parsedWired.Manifest.Sources, static s => s.Type == "todo");
        Assert.True(parsedWired.Manifest.Collections.ContainsKey("todos"));

        // 2. End-to-end ViewModel + QuickJS runtime verification
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p3-vm-{Guid.NewGuid():N}");
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            var locService = new LocalizationService(settingsService);

            const string logicJs = """
            function syncBanner(ctx) {
              const track = (ctx.env.music && ctx.env.music.title) || 'Silence';
              const pending = ctx.env.todo ? ctx.env.todo.pendingCount : 0;
              ctx.computed.stationHeadline = `${track} | ${pending} left`;
            }

            export function onInit(ctx) {
              syncBanner(ctx);
            }

            export function onSourceData(ctx) {
              syncBanner(ctx);
            }
            """;

            var store = new CustomWidgetDataStore("w-focus-station", widgetsDir);
            await store.WriteWorkspaceBundleAsync(wiredManifestJson, logicJs, locales: null);

            int musicCalls = 0;
            int todoCalls = 0;
            string activeTrackTitle = "Clair de Lune";
            int activePendingCount = 4;

            var config = new WidgetConfig
            {
                Id = "w-focus-station",
                Name = "Focus Station",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, locService, store)
            {
                MusicSnapshotProviderOverride = _ =>
                {
                    Interlocked.Increment(ref musicCalls);
                    return Task.FromResult(new CustomWidgetMusicSnapshot(
                        HasSession: true,
                        IsPlaying: true,
                        Status: "playing",
                        Title: activeTrackTitle,
                        Artist: "Debussy",
                        Album: "Suite bergamasque",
                        SourceApp: "Spotify.exe",
                        CoverPath: string.Empty,
                        PositionSeconds: 75,
                        DurationSeconds: 300,
                        PositionText: "01:15",
                        DurationText: "05:00",
                        ProgressPercent: 25));
                },
                TodoSnapshotProviderOverride = (max, _) =>
                {
                    Interlocked.Increment(ref todoCalls);
                    var rows = new List<CustomWidgetRecord>
                    {
                        new()
                        {
                            Id = "r1",
                            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["id"] = "r1",
                                ["title"] = "Finish DeskBox Phase 3",
                                ["isCompleted"] = "false",
                                ["isImportant"] = "true",
                                ["priority"] = "high",
                                ["dueText"] = "11-08",
                                ["isOverdue"] = "false"
                            }
                        }
                    };
                    return Task.FromResult(new CustomWidgetTodoSnapshot(
                        TotalCount: activePendingCount + 2,
                        PendingCount: activePendingCount,
                        CompletedCount: 2,
                        TodayPendingCount: 2,
                        TodayCompletedCount: 1,
                        OverdueCount: 0,
                        ImportantPendingCount: 1,
                        CompletionPercent: 33,
                        NextTaskTitle: "Finish DeskBox Phase 3",
                        NextTaskDueText: "11-08",
                        Summary: $"2/{activePendingCount + 2} (33%)",
                        Records: rows));
                }
            };

            await vm.InitializeAsync();
            Assert.Null(vm.LastScriptError);

            CustomWidgetEvaluationContext ctx1 = vm.CreateEvaluationContext();
            Assert.Equal(
                "Clair de Lune - Debussy (25%)",
                CustomWidgetExpressionEvaluator.Interpolate("${$music.title} - ${$music.artist} (${$music.progressPercent}%)", ctx1));
            Assert.Equal(
                "2/6 (33%) · Next: Finish DeskBox Phase 3",
                CustomWidgetExpressionEvaluator.Interpolate("${$todo.summary} · Next: ${$todo.nextTaskTitle}", ctx1));
            Assert.Equal(
                "Clair de Lune | 4 left",
                CustomWidgetExpressionEvaluator.Interpolate("${$computed.stationHeadline}", ctx1));
            Assert.Single(ctx1.Collections["todos"]);

            // Trigger declarative source.refresh action and verify QuickJS onSourceData updates computed state
            activeTrackTitle = "Nocturne Op. 9 No. 2";
            activePendingCount = 2;
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("source.refresh")]);

            CustomWidgetEvaluationContext ctx2 = vm.CreateEvaluationContext();
            Assert.Equal(
                "Nocturne Op. 9 No. 2 | 2 left",
                CustomWidgetExpressionEvaluator.Interpolate("${$computed.stationHeadline}", ctx2));

            // Verify fullscreen occlusion suspends background polling and resuming performs catch-up refresh
            int musicBeforeOcclude = Volatile.Read(ref musicCalls);
            int todoBeforeOcclude = Volatile.Read(ref todoCalls);
            vm.OnMonitorOcclusionChanged(true);

            activeTrackTitle = "Gymnopédie No. 1";
            activePendingCount = 1;
            vm.OnMonitorOcclusionChanged(false);

            // Allow fire-and-forget catch-up tasks on resume to settle
            for (int i = 0; i < 20 && (Volatile.Read(ref musicCalls) <= musicBeforeOcclude || Volatile.Read(ref todoCalls) <= todoBeforeOcclude); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(Volatile.Read(ref musicCalls) > musicBeforeOcclude);
            Assert.True(Volatile.Read(ref todoCalls) > todoBeforeOcclude);
            CustomWidgetEvaluationContext ctx3 = vm.CreateEvaluationContext();
            Assert.Equal(
                "Gymnopédie No. 1 | 1 left",
                CustomWidgetExpressionEvaluator.Interpolate("${$computed.stationHeadline}", ctx3));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static byte[] BuildWidgetPackageZipBytes(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipTextEntry(archive, "manifest.json", manifestJson);
            if (!string.IsNullOrWhiteSpace(logicJs))
            {
                WriteZipTextEntry(archive, "logic.js", logicJs);
            }

            if (locales is { HasAnyLocale: true })
            {
                if (locales.Meta is not null)
                {
                    WriteZipTextEntry(
                        archive,
                        $"locales/{CustomWidgetLocaleCatalog.MetaFileName}",
                        CustomWidgetLocaleCatalog.FormatMetaJson(locales.Meta));
                }

                foreach ((string code, IReadOnlyDictionary<string, string> entries) in locales.Locales)
                {
                    WriteZipTextEntry(
                        archive,
                        $"locales/{code}.json",
                        CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(entries));
                }
            }
        }

        return ms.ToArray();
    }

    private static void WriteZipTextEntry(ZipArchive archive, string entryName, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class RoutingMockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> router)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(router(request));
        }
    }
}
