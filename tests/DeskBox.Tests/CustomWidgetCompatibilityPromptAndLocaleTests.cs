using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.Graphics;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetCompatibilityPromptAndLocaleTests
{
    [Fact]
    public void CalculateCrossDpiDragBounds_PreservesLogicalSizeAndRelativeGrabOffsetAcrossHeterogeneousMonitors()
    {
        // 352x440 logical DIP widget starting on a 200% (2.0x) monitor -> 704x880 physical px
        var initialPos = new PointInt32(100, 120);
        var initialSize = new SizeInt32(704, 880);
        var initialPointer = new PointInt32(276, 200); // Grabbed at 25% X (176px / 704px), 80px Y

        // Dragged onto a 100% (1.0x) secondary monitor at pointer (2300, 300)
        RectInt32 targetOn100 = WidgetPositioningService.CalculateCrossDpiDragBounds(
            initialPos,
            initialSize,
            initialPointer,
            initialDpiScale: 2.0,
            currentPointerPt: new PointInt32(2300, 300),
            targetDpiScale: 1.0);

        Assert.Equal(352, targetOn100.Width);
        Assert.Equal(440, targetOn100.Height);
        Assert.Equal(2300 - 88, targetOn100.X);
        Assert.Equal(300 - 40, targetOn100.Y);

        // Dragged back onto a 150% (1.5x) monitor -> 528x660 physical px
        RectInt32 targetOn150 = WidgetPositioningService.CalculateCrossDpiDragBounds(
            initialPos,
            initialSize,
            initialPointer,
            initialDpiScale: 2.0,
            currentPointerPt: new PointInt32(600, 400),
            targetDpiScale: 1.5);

        Assert.Equal(528, targetOn150.Width);
        Assert.Equal(660, targetOn150.Height);
    }

    [Fact]
    public void ResolveAutoLaunchExecutablePath_UsesWindowsAppsAliasWhenPackagedAndDirectExeWhenUnpackaged()
    {
        string tempLocalAppData = Path.Combine(Path.GetTempPath(), $"deskbox-alias-{Guid.NewGuid():N}");
        string aliasDir = Path.Combine(tempLocalAppData, "Microsoft", "WindowsApps");
        Directory.CreateDirectory(aliasDir);
        string aliasExe = Path.Combine(aliasDir, CustomWidgetAgentConnectorService.StoreExecutionAlias);
        File.WriteAllBytes(aliasExe, [0x4D, 0x5A]);

        string fakeUnpackagedDir = Path.Combine(tempLocalAppData, "direct");
        Directory.CreateDirectory(fakeUnpackagedDir);
        string directExe = Path.Combine(fakeUnpackagedDir, "DeskBox.exe");
        File.WriteAllBytes(directExe, [0x4D, 0x5A]);

        try
        {
            string? packagedResolved = CustomWidgetAgentConnectorService.ResolveAutoLaunchExecutablePath(
                isPackagedOverride: true,
                localAppDataOverride: tempLocalAppData,
                currentProcessPathOverride: @"C:\Program Files\WindowsApps\DeskBox_1.0\DeskBox.exe");
            Assert.Equal(aliasExe, packagedResolved);

            string? unpackagedResolved = CustomWidgetAgentConnectorService.ResolveAutoLaunchExecutablePath(
                isPackagedOverride: false,
                localAppDataOverride: tempLocalAppData,
                currentProcessPathOverride: directExe);
            Assert.Equal(directExe, unpackagedResolved);
        }
        finally
        {
            try { Directory.Delete(tempLocalAppData, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsBlockedUncOrRemoteFileUri_BlocksUncSmbAndRemoteFileUrisBeforeFilesystemProbe()
    {
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(@"\\attacker.example.com\share\logo.png"));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri("//192.168.1.50/share/secret.txt"));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(@"\\?\UNC\attacker.example.com\share\payload.png"));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri("file://attacker.example.com/share/logo.png"));

        Assert.False(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(@"C:\Users\Public\Documents\note.txt"));
        Assert.False(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri("attachments/icon.png"));
        Assert.False(CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri("https://deskbox.fun/icon.png"));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-unc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetDataStore("unc-guard-test", tempDir);
            Assert.Equal(string.Empty, store.ResolveRuntimePath(@"\\attacker.example.com\share\logo.png"));
            Assert.Equal(string.Empty, store.ResolveRuntimePath("file://attacker.example.com/share/logo.png"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PromptBlocks_EliminateHallucinatedTokensLocalizeTier0DirectoryAndAvoidDuplicateComponentIndex()
    {
        foreach (bool zh in new[] { true, false })
        {
            string blueprint = CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(
                userMessage: "做一个课程表和待办组件",
                currentManifestJson: null,
                isChinese: zh);
            Assert.DoesNotContain("$todo.items", blueprint, StringComparison.Ordinal);
            Assert.Contains("todo.add", blueprint, StringComparison.Ordinal);
            Assert.Contains("todo.toggle", blueprint, StringComparison.Ordinal);
            Assert.DoesNotContain("$clipboard.items", blueprint, StringComparison.Ordinal);
            Assert.DoesNotContain("$calendar.events", blueprint, StringComparison.Ordinal);
            Assert.Contains("$item.isEmpty == true", blueprint, StringComparison.Ordinal);
        }

        string tier0Zh = CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: true);
        string tier0En = CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: false);
        Assert.Contains("DeskBox 原生能力与组件总目录", tier0Zh, StringComparison.Ordinal);
        Assert.Contains("compactPair", tier0Zh, StringComparison.Ordinal);
        Assert.Contains("ctx.env", tier0Zh, StringComparison.Ordinal);
        Assert.Contains("ctx.args?.", tier0Zh, StringComparison.Ordinal);
        Assert.Contains("ctx.event?.item", tier0Zh, StringComparison.Ordinal);
        Assert.Contains("compactPair", tier0En, StringComparison.Ordinal);
        Assert.Contains("ctx.args?.foo", tier0En, StringComparison.Ordinal);
        Assert.Contains("ctx.event?.item", tier0En, StringComparison.Ordinal);

        string userTurnPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            userMessage: "做一个极简专注计时器",
            uiLanguage: "zh-CN");
        Assert.DoesNotContain("Native Component Index", userTurnPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyExternalDataPushAndPowerSuspension_PreserveNativeJsonTypesAndWindowVisibility()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-push-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("json-push-test", tempDir);
            var cfg = new WidgetConfig
            {
                Id = "json-push-test",
                Name = "JSON Push Test",
                WidgetKind = WidgetKind.Custom
            };

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.json-push-test",
              "name": "JSON Push",
              "icon": "📊",
              "state": {
                "online": false,
                "latencyMs": 0,
                "tags": []
              },
              "collections": {
                "events": { "maxItems": 20 }
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Card",
                  "children": ["latency_text"]
                },
                "latency_text": { "type": "Text", "text": "${$state.latencyMs}" }
              }
            }
            """;

            await store.WriteWorkspaceBundleAsync(manifestJson, null, locales: null);

            using var vm = new CustomWidgetViewModel(
                cfg,
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);
            await vm.InitializeAsync();

            using JsonDocument pushDoc = JsonDocument.Parse("""
            {
              "state": {
                "online": true,
                "latencyMs": 42.5,
                "tags": ["gpu", "nvme"],
                "meta": { "region": "ap-east" }
              },
              "record": {
                "id": "ev-1",
                "ok": true,
                "ms": 18,
                "labels": ["ok"]
              }
            }
            """);

            var stateJsonUpdates = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in pushDoc.RootElement.GetProperty("state").EnumerateObject())
            {
                stateJsonUpdates[prop.Name] = prop.Value.Clone();
            }

            var recordMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty prop in pushDoc.RootElement.GetProperty("record").EnumerateObject())
            {
                recordMap[prop.Name] = prop.Value.Clone();
            }

            await vm.ApplyExternalDataPushAsync(
                stateUpdates: null,
                collectionName: "events",
                collectionMode: "replace",
                records: null,
                stateJsonUpdates: stateJsonUpdates,
                jsonRecords: [recordMap]);

            Assert.Equal(JsonValueKind.True, vm.StateJson["online"].ValueKind);
            Assert.Equal(JsonValueKind.Number, vm.StateJson["latencyMs"].ValueKind);
            Assert.Equal(42.5, vm.StateJson["latencyMs"].GetDouble());
            Assert.Equal(JsonValueKind.Array, vm.StateJson["tags"].ValueKind);
            Assert.Equal(JsonValueKind.Object, vm.StateJson["meta"].ValueKind);

            IReadOnlyList<CustomWidgetRecord> items = vm.Collections["events"];
            Assert.Single(items);
            Assert.Equal(JsonValueKind.True, items[0].FieldsJson["ok"].ValueKind);
            Assert.Equal(18, items[0].FieldsJson["ms"].GetInt32());
            Assert.Equal(JsonValueKind.Array, items[0].FieldsJson["labels"].ValueKind);

            // Power suspension pauses effective visibility while preserving underlying window state
            Assert.True(vm.IsWindowVisible);
            vm.SetPowerSuspended(true);
            Assert.False(vm.IsWindowVisible);
            vm.SetPowerSuspended(false);
            Assert.True(vm.IsWindowVisible);

            vm.OnWindowVisibilityChanged(false);
            vm.SetPowerSuspended(true);
            vm.SetPowerSuspended(false);
            Assert.False(vm.IsWindowVisible);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OfficialTemplates_FollowHostLocaleWithoutChineseLeaksOrEnglishSampleShadowing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-tpl-i18n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            settingsService.Settings.Language = "fr-FR";
            var localization = new LocalizationService(settingsService);

            // 1. news-rss on non-Chinese host defaults to Hacker News and uses localized SSPai/ITHome titles
            CustomWidgetTemplateBundle rssLocalizedBundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.NewsRssStarterId, "fr-FR");
            Assert.Contains("\"activeChannelKey\": \"hn\"", rssLocalizedBundle.ManifestJson, StringComparison.Ordinal);

            CustomWidgetTemplateBundle rssRawBundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.NewsRssStarterId, "zh-CN");
            var rssStore = new CustomWidgetDataStore("rss-fr-test", Path.Combine(tempDir, "rss"));
            await rssStore.WriteWorkspaceBundleAsync(rssRawBundle.ManifestJson, rssRawBundle.LogicJs, locales: rssRawBundle.Locales);
            using var rssVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "rss-fr-test", Name = "RSS", WidgetKind = WidgetKind.Custom },
                localization,
                store: rssStore,
                isPreviewOnly: false);
            await rssVm.InitializeAsync();
            Assert.Equal("hn", rssVm.State["activeChannelKey"]);
            Assert.Equal("Hacker News", rssVm.Computed["channelTitle"].GetString());

            // 2. todo-agenda seeds French tasks in preview instead of hardcoded English sampleData
            CustomWidgetTemplateBundle todoBundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.TodoAgendaTemplateId, "fr-FR");
            var todoStore = new CustomWidgetDataStore("todo-fr-test", Path.Combine(tempDir, "todo"));
            using var todoVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "todo-fr-test", Name = "Todo", WidgetKind = WidgetKind.Custom },
                localization,
                store: todoStore,
                isPreviewOnly: true,
                settingsService: settingsService);
            todoVm.ApplyPreviewBundle(todoBundle.ManifestJson, todoBundle.LogicJs, todoBundle.Locales, preserveRuntimeData: false);
            Assert.Equal(3, todoVm.Collections["tasks"].Count);
            string expectedFrTask1 = todoBundle.Locales!.Locales["fr-FR"]["seedTask1"];
            Assert.Equal(expectedFrTask1, todoVm.Collections["tasks"][0].Fields["title"]);

            // 3. ai-inbox in French Auto mode targets French <-> English rather than Simplified Chinese
            string capturedPrompt = string.Empty;
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-dummy-key",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "auto"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));
            prefsStore.SetPreferredAiProviderId("deepseek");

            using var httpClient = new HttpClient(new DelegateHttpHandler(async (req, ct) =>
            {
                capturedPrompt = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"content\":\"{\\\"result\\\":\\\"Bonjour\\\",\\\"tag\\\":\\\"FR\\\",\\\"insight\\\":\\\"OK\\\"}\"}}]}",
                        Encoding.UTF8,
                        "application/json")
                };
            }));
            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            CustomWidgetTemplateBundle inboxBundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiInboxStarterId);
            var inboxStore = new CustomWidgetDataStore("inbox-fr-test", Path.Combine(tempDir, "inbox"));
            await inboxStore.WriteWorkspaceBundleAsync(inboxBundle.ManifestJson, inboxBundle.LogicJs, locales: inboxBundle.Locales);
            using var inboxVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "inbox-fr-test", Name = "AI Inbox", WidgetKind = WidgetKind.Custom },
                localization,
                store: inboxStore,
                isPreviewOnly: false,
                aiService: aiService,
                preferredAiProviderResolver: () => "deepseek");
            await inboxVm.InitializeAsync();
            await inboxVm.ExecuteActionsAsync([new CustomWidgetActionSpec("state.set", Key: "draft", Value: "Hello world")]);

            static CustomWidgetNodeSpec? FindAiRunButton(CustomWidgetNodeSpec node)
            {
                if (string.Equals(node.Type, "Button", StringComparison.OrdinalIgnoreCase) &&
                    node.OnClick.Any(static a => string.Equals(a.Fn, "prepareDraftRun", StringComparison.Ordinal)) &&
                    node.OnClick.Any(static a => string.Equals(a.Type, "ai.run", StringComparison.OrdinalIgnoreCase)))
                {
                    return node;
                }

                foreach (CustomWidgetNodeSpec child in node.Children)
                {
                    if (FindAiRunButton(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }

            CustomWidgetNodeSpec runBtn = FindAiRunButton(inboxVm.Manifest.View)!;
            Assert.NotNull(runBtn);
            await inboxVm.ExecuteActionsAsync(runBtn.OnClick!);

            for (int i = 0; i < 20 && string.IsNullOrEmpty(capturedPrompt); i++)
            {
                await Task.Delay(25);
            }

            Assert.Contains("French", capturedPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("non-Chinese -> Simplified Chinese", capturedPrompt, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalizationAndQuickJsGuards_EnforceParityRtlAndPayloadBounds()
    {
        Assert.Equal("ar-SA", LocalizationService.ResolveEffectiveCultureName("ar-SA"));
        Assert.True(CustomWidgetLocaleCatalog.IsRightToLeftCulture("ar-SA"));
        Assert.False(CustomWidgetLocaleCatalog.IsRightToLeftCulture("de-DE"));

        string stringsDir = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] locales =
        [
            "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR",
            "fr-FR", "de-DE", "es-ES", "pt-BR", "ru-RU",
            "ar-SA", "hi-IN", "bn-BD"
        ];

        HashSet<string>? baseKeys = null;
        var loadedMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string loc in locales)
        {
            string json = File.ReadAllText(Path.Combine(stringsDir, $"{loc}.json"), Encoding.UTF8);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            loadedMaps[loc] = map;
            if (baseKeys is null)
            {
                baseKeys = new HashSet<string>(map.Keys, StringComparer.Ordinal);
            }
            else
            {
                Assert.True(baseKeys.SetEquals(map.Keys), $"Strings/{loc}.json key set differs from zh-CN.json.");
            }
        }

        string enVectorTitle = loadedMaps["en-US"]["CustomWidget.Cap.vector-canvas.Title"];
        Assert.NotEqual(enVectorTitle, loadedMaps["ja-JP"]["CustomWidget.Cap.vector-canvas.Title"]);
        Assert.NotEqual(enVectorTitle, loadedMaps["de-DE"]["CustomWidget.Cap.vector-canvas.Title"]);
        Assert.NotEqual(enVectorTitle, loadedMaps["ar-SA"]["CustomWidget.Cap.vector-canvas.Title"]);

        byte[] oversizedPayload = new byte[(4 * 1024 * 1024) + 16];
        DeskBoxQjsNativeInvokeOutcome qjsOutcome = CustomWidgetQuickJsInterop.InvokeBytecode(
            bytecode: [0x02, 0x01],
            fnName: "onInit",
            requireExport: false,
            payloadJsonUtf8: oversizedPayload,
            timeoutMs: 50,
            memoryLimitBytes: 4 * 1024 * 1024);
        Assert.True(
            qjsOutcome.Status is DeskBoxQjsNativeStatus.InvalidArgument or DeskBoxQjsNativeStatus.EngineUnavailable);
    }

    [Fact]
    public async Task RemoteCatalogService_RejectsUnsupportedFutureSchemaVersion()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cat-ver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string futureCatalogJson = """
            {
              "schemaVersion": 99,
              "updatedAtUtc": "2026-11-16T00:00:00Z",
              "widgets": [
                {
                  "id": "future-widget",
                  "manifestId": "deskbox.future-widget",
                  "name": "Future Widget",
                  "version": "1.0.0"
                }
              ]
            }
            """;

            using var handler = new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(futureCatalogJson, Encoding.UTF8, "application/json")
                }));

            var service = new CustomWidgetRemoteCatalogService(tempRoot, handler);
            bool applied = await service.RefreshInBackgroundAsync(force: true);
            Assert.False(applied);
            Assert.False(service.TryGetRemoteTemplateBundle("future-widget", out _));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NonBaseLocaleI18nEdit_PreservesNewLiteralsAndBaseFallbackWithoutBlankStrings()
    {
        const string initialZhManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.i18n.edit",
          "name": "专注计时",
          "root": "main",
          "elements": {
            "main": {
              "type": "Card",
              "children": ["start_text"]
            },
            "start_text": { "type": "Text", "text": "开始专注" }
          }
        }
        """;

        CustomWidgetI18nCompileResult initialCompiled = CustomWidgetI18nEngine.Compile(
            initialZhManifest,
            logicJs: null,
            existingCatalog: null,
            fallbackCulture: "zh-CN");
        var withGermanDict = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            initialCompiled.SeededCatalog.Locales,
            StringComparer.OrdinalIgnoreCase)
        {
            ["de-DE"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "Fokus-Timer"
            }
        };
        var catalog = new CustomWidgetLocaleCatalog(withGermanDict, initialCompiled.SeededCatalog.Meta);

        // User edits in German Studio UI and AI adds a brand-new German label + leaves one base-only key
        JsonObject editedDoc = JsonNode.Parse(initialCompiled.CompiledManifestJson)!.AsObject();
        JsonObject elementsObj = editedDoc["elements"]!.AsObject();
        elementsObj["new_section"] = new JsonObject
        {
            ["type"] = "Text",
            ["text"] = "Neuer Abschnitt"
        };
        elementsObj["main"]!["children"]!.AsArray().Insert(0, "new_section");
        string editedInGermanJson = editedDoc.ToJsonString();

        string recompiledJson = CustomWidgetI18nEngine.AutoCompileIfLocalized(
            editedInGermanJson,
            logicJs: null,
            existingCatalog: catalog,
            fallbackCulture: "de-DE",
            out CustomWidgetLocaleCatalog? updatedCatalog);
        Assert.NotNull(updatedCatalog);

        CustomWidgetManifest resolvedManifest = CustomWidgetManifestParser
            .Parse(recompiledJson)
            .Manifest!
            .WithLocales(updatedCatalog!);

        IReadOnlyDictionary<string, string> deDict = resolvedManifest.ResolveI18nDictionary("de-DE");
        Assert.Contains(deDict.Values, v => string.Equals(v, "Neuer Abschnitt", StringComparison.Ordinal));
        Assert.All(deDict.Values, v => Assert.False(string.IsNullOrEmpty(v)));

        // Untranslated key in fr-FR falls back cleanly to baseLocale (zh-CN) instead of empty string
        IReadOnlyDictionary<string, string> frDict = resolvedManifest.ResolveI18nDictionary("fr-FR");
        Assert.All(frDict.Values, v => Assert.False(string.IsNullOrEmpty(v)));
    }

    [Fact]
    public async Task LifecycleOrdering_RunsOnInitBeforeDiskCacheHydration_AndSyncsPreviewSeedAndEvents()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("order-test", tempDir);
            string cacheDir = Path.Combine(tempDir, "netcache");

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.order.test",
              "name": "Order Test",
              "settings": [
                { "key": "city", "type": "text", "label": "City", "default": "Beijing" },
                { "key": "apiToken", "type": "secret", "label": "API Token" }
              ],
              "state": {
                "slug": "stale",
                "headline": "none",
                "counter": 0
              },
              "sources": [
                {
                  "id": "feed",
                  "type": "httpJson",
                  "url": "https://api.example.com/${$state.slug}",
                  "intervalMs": 600000,
                  "stateMap": {
                    "headline": "$.title"
                  }
                }
              ],
              "script": {
                "entry": "logic.js"
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Text",
                  "text": "${$state.headline}"
                }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              ctx.state.slug = "v2/live";
            }
            """;

            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            CustomWidgetManifest parsedManifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;
            using var seedHandler = new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"title\":\"Hydrated After OnInit\"}", Encoding.UTF8, "application/json")
                }));
            var seedEngine = new CustomWidgetNetworkSourceEngine(cacheDir, seedHandler);
            CustomWidgetNetworkFetchOutcome seeded = await seedEngine.FetchAsync(
                parsedManifest.Sources[0],
                "https://api.example.com/v2/live");
            Assert.True(seeded.Succeeded);

            using var offlineHandler = new DelegateHttpHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            var offlineEngine = new CustomWidgetNetworkSourceEngine(cacheDir, offlineHandler);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "order-test", Name = "Order", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                isPreviewOnly: false,
                networkSourceEngine: offlineEngine);
            await vm.InitializeAsync();

            Assert.Equal("v2/live", vm.State["slug"]);
            Assert.Equal("Hydrated After OnInit", vm.State["headline"]);

            string? observedSettingKey = null;
            string? observedSettingVal = null;
            string? observedSecretKey = null;
            string? observedSecretVal = null;
            vm.SettingChanged += (k, v) => { observedSettingKey = k; observedSettingVal = v; };
            vm.SecretChanged += (k, v) => { observedSecretKey = k; observedSecretVal = v; };

            await vm.SetSettingValueAsync("city", "Tokyo");
            await vm.SetSecretValueAsync("apiToken", "sk-live-123");
            Assert.Equal("city", observedSettingKey);
            Assert.Equal("Tokyo", observedSettingVal);
            Assert.Equal("apiToken", observedSecretKey);
            Assert.Equal("sk-live-123", observedSecretVal);

            using var previewVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "__studio_preview__", Name = "Preview", WidgetKind = WidgetKind.Custom },
                localization,
                store: null,
                isPreviewOnly: true);
            previewVm.ApplyPreviewBundle(manifestJson, logicJs, locales: null, preserveRuntimeData: false);

            var seedPayload = new CustomWidgetStorePayload();
            using JsonDocument numDoc = JsonDocument.Parse("42");
            seedPayload.StateJson["counter"] = numDoc.RootElement.Clone();
            previewVm.ApplyPreviewSeedPayload(seedPayload);
            Assert.Equal("42", previewVm.State["counter"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SecurityDecisionPrompt_SupportsAllowOnceWithoutPermanentPersistence()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "sec-once-test", Name = "Sec", WidgetKind = WidgetKind.Custom },
                localization,
                store: new CustomWidgetDataStore("sec-once-test", tempDir),
                isPreviewOnly: true);

            int promptCount = 0;
            vm.ExternalActionDecisionPrompt = _ =>
            {
                promptCount++;
                return Task.FromResult(CustomWidgetSecurityConsentDecision.AllowOnce);
            };

            CustomWidgetSecurityConsentDecision decision = await vm.PromptSecurityDecisionAsync(
                new CustomWidgetSecurityPrompt("openUrl", "https://example.com", "Sec"));
            Assert.Equal(CustomWidgetSecurityConsentDecision.AllowOnce, decision);
            Assert.Equal(1, promptCount);
            Assert.False(vm.Settings.ContainsKey(CustomWidgetViewModel.TrustedExternalTargetsSettingKey));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task KvCachedPrompt_StreamingHint_InlineAtQuery_AndMcpActivityFeed_WorkEndToEnd()
    {
        // 1. System prompt holds Tier 0 directory; User prompt omits duplicate Tier 0 and trims composition quota on Refine
        string sysPrompt = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt("de-DE", useNativeTools: true);
        Assert.Contains("DeskBox Native Capability & Component Directory", sysPrompt, StringComparison.Ordinal);
        Assert.Contains("German (Deutsch)", sysPrompt, StringComparison.Ordinal);

        const string existingManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.kv.test",
          "name": "Fokus",
          "root": "main",
          "elements": {
            "main": {
              "type": "Card",
              "children": ["titleNode"]
            },
            "titleNode": { "type": "Text", "id": "titleNode", "text": "Hallo" }
          }
        }
        """;
        string refineUserPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            userMessage: "Mach den Text etwas größer",
            currentManifestJson: existingManifest,
            briefMarkdown: null,
            recentMessages: null,
            healthReport: null,
            targetNodePath: "#titleNode (Text)",
            preferredEmoji: null,
            uiLanguage: "de-DE");
        Assert.DoesNotContain("DeskBox Native Capability & Component Directory", refineUserPrompt, StringComparison.Ordinal);

        // 2. StreamingTurnAccumulator extracts live reply & component pulse during SSE tool call streaming
        var acc = new StreamingTurnAccumulator();
        acc.AppendToolCallDelta(0, "call_1", StudioToolTransport.ToolWriteBundle, "{\"reply\":\"已为你创建番茄钟\",\"manifest\":{\"schemaVersion\":1,\"name\":\"番茄钟\",\"root\":\"main\",\"elements\":{\"main\":{\"type\":\"Stack\",\"children\":[\"c1\",\"g1\"]},\"c1\":{\"type\":\"Card\"},\"g1\":{\"type\":\"RingGauge\"}");
        string? hint = acc.GetStreamingHint();
        Assert.NotNull(hint);
        Assert.Contains("已为你创建番茄钟", hint!, StringComparison.Ordinal);
        Assert.Contains("RingGauge", hint!, StringComparison.Ordinal);

        // 3. Inline '@<query>' detection works anywhere in composer text
        const string composerSample = "帮我接入 @wea 实时卡片";
        int caretAfterWea = composerSample.IndexOf("@wea", StringComparison.Ordinal) + 4;
        Assert.True(DeskBox.Controls.Studio.StudioComposerCardBuilder.TryExtractActiveAtQuery(
            composerSample,
            caretAfterWea,
            out int atIndex,
            out string extractedQuery));
        Assert.Equal(composerSample.IndexOf('@'), atIndex);
        Assert.Equal("wea", extractedQuery);

        // 4. DeskBoxAgentBridgeServer emits McpActivityRecorded on tool operations
        var recordedOps = new List<(string Op, string Detail)>();
        void Handler(string op, string detail) => recordedOps.Add((op, detail));
        DeskBoxAgentBridgeServer.McpActivityRecorded += Handler;
        try
        {
            await using var bridge = new DeskBoxAgentBridgeServer($"DeskBox_Act_{Guid.NewGuid():N}", static () => null);
            await bridge.DispatchRequestJsonAsync("""{"op":"mcp_hello","clientName":"Cursor"}""");
            await bridge.DispatchRequestJsonAsync("""{"op":"validate","manifestJson":"{\"schemaVersion\":1,\"id\":\"custom.x\",\"name\":\"X\",\"root\":\"main\",\"elements\":{\"main\":{\"type\":\"Text\",\"text\":\"ok\"}}}"}""");
            Assert.Contains(recordedOps, x => x.Op == "mcp_hello" && x.Detail == "Cursor");
            Assert.Contains(recordedOps, x => x.Op == "validate");
        }
        finally
        {
            DeskBoxAgentBridgeServer.McpActivityRecorded -= Handler;
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
