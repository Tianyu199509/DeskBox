using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Tests;

/// <summary>
/// Phase 3.5 final hardening regression suite covering:
/// 1) $music.* / $todo.* live metrics tokens, status/field aliases, trailing replay, and full fingerprints;
/// 2) Authoritative cross-process Custom Widget mutations, store.json SHA-256 self-write echo suppression,
///    hyphenated secret keys, and host-mirrored collection exclusion from store.json / data-loss alerts;
/// 3) Fork lineage preservation across AI turns (structured + bare repair/paste), capsule title sync,
///    and cross-locale idempotent fork suffixes;
/// 4) Remote Catalog v2 retired logic.js/locales cleanup, ETag suppression on sub-artifact failure,
///    and avoiding built-in fallbackLogicJs grafting on scriptless v2 manifests;
/// 5) Image local path sandbox, package sanitizer rooted-path scrubbing, UNC rejection on CLI/protocol
///    package launch, and per-user deskbox:// registration guard for Debug/HKLM builds.
/// </summary>
public sealed class Phase35FinalHardeningRegressionTests
{
    [Fact]
    public async Task MusicLiveMetrics_IdleStatus_ConcurrentInit_AndTrailingRefreshReplay_WorkEndToEnd()
    {
        // [P0-1] Renderer must treat $music.* and $todo.* as live-refreshable metric tokens
        Assert.True(CustomWidgetNativeRenderer.ContainsSystemMetricsToken("${$music.positionText}"));
        Assert.True(CustomWidgetNativeRenderer.ContainsSystemMetricsToken("${$music.progressPercent}"));
        Assert.True(CustomWidgetNativeRenderer.ContainsSystemMetricsToken("${$todo.completionPercent}"));

        // [P0-2] Empty/stopped music snapshot normalizes status to "idle" matching docs & prompts
        Assert.Equal("idle", CustomWidgetMusicSnapshot.Empty.Status);

        // [P1-1] Concurrent InitializeAsync + Dispose must be race-free
        using (var sessionSvc = new MusicSessionService())
        {
            await Task.WhenAll(sessionSvc.InitializeAsync(), sessionSvc.InitializeAsync());
        }

        // [P0-1] + [P1-3] Same-track timeline advance fires MetricsRefreshed, and queued pending refresh replays
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p35-music-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore("w-music", Path.Combine(tempRoot, "widgets"));

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "test.music-live",
              "name": "Music Live",
              "sources": [{ "type": "music", "intervalMs": 1000 }],
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$music.title} (${$music.positionSeconds}/${$music.durationSeconds})" }
                ]
              }
            }
            """;
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            int callIndex = 0;
            var firstCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int metricsRefreshCount = 0;

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-music", Name = "Music Live", WidgetKind = WidgetKind.Custom },
                locSvc,
                store,
                dispatcherQueue: null,
                settingsService: settingsSvc)
            {
                MusicSnapshotProviderOverride = async _ =>
                {
                    int n = Interlocked.Increment(ref callIndex);
                    if (n == 2)
                    {
                        firstCallEntered.TrySetResult();
                        await releaseFirstCall.Task;
                    }

                    int pos = n switch
                    {
                        1 => 10,
                        2 => 11,
                        _ => 42
                    };
                    return new CustomWidgetMusicSnapshot(
                        HasSession: true,
                        IsPlaying: true,
                        Status: "playing",
                        Title: "Clair de Lune",
                        Artist: "Debussy",
                        Album: "Suite",
                        SourceApp: "Spotify.exe",
                        CoverPath: string.Empty,
                        PositionSeconds: pos,
                        DurationSeconds: 100,
                        PositionText: $"00:{pos:00}",
                        DurationText: "01:40",
                        ProgressPercent: pos);
                }
            };
            vm.MetricsRefreshed += (_, _) => Interlocked.Increment(ref metricsRefreshCount);

            await vm.InitializeAsync();
            Assert.Equal("10", vm.CreateEvaluationContext().Music?.ResolveToken("positionSeconds"));

            // Start an in-flight refresh (call #2) and queue another refresh while it is blocked (call #3)
            Task inflight = vm.RefreshMusicNowAsync(triggerRebuild: true);
            await firstCallEntered.Task;
            Task overlapping = vm.RefreshMusicNowAsync(triggerRebuild: true);
            await overlapping;

            releaseFirstCall.TrySetResult();
            await inflight;

            for (int i = 0; i < 40 && vm.CreateEvaluationContext().Music?.PositionSeconds != 42; i++)
            {
                await Task.Delay(20);
            }

            Assert.Equal(42, vm.CreateEvaluationContext().Music?.PositionSeconds);
            Assert.True(Volatile.Read(ref metricsRefreshCount) >= 1);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task TodoSnapshotAliases_DefaultListFieldFallback_AndSecondItemMutationFingerprint_WorkEndToEnd()
    {
        // [P0-2] List without itemTemplate honors titleField/subtitleField and falls back text -> title
        CustomWidgetParseResult listParse = CustomWidgetManifestParser.Parse("""
        {
          "schemaVersion": 1,
          "id": "test.todo-list",
          "name": "Todo List",
          "collections": { "todos": { "maxItems": 12 } },
          "sources": [{ "type": "todo", "targetCollection": "todos", "intervalMs": 30000 }],
          "view": {
            "type": "List",
            "collection": "todos",
            "titleField": "title",
            "subtitleField": "dueDate"
          }
        }
        """);
        Assert.NotNull(listParse.Manifest);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p35-todo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string todoWidgetsRoot = Path.Combine(tempRoot, "todo-stores");
            Directory.CreateDirectory(todoWidgetsRoot);

            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            settingsSvc.Settings.Widgets.Add(new WidgetConfig
            {
                Id = "native-todo-1",
                Name = "Work Tasks",
                WidgetKind = WidgetKind.Todo,
                IsVisible = true
            });

            var nativeTodoStore = new TodoWidgetStore(todoWidgetsRoot, "native-todo-1");
            await nativeTodoStore.SaveAsync(new TodoWidgetData
            {
                Items =
                [
                    new TodoItem
                    {
                        Id = "t1",
                        Text = "Ship Phase 3.5",
                        IsCompleted = false,
                        IsImportant = true,
                        DueDate = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.FromHours(8))
                    },
                    new TodoItem
                    {
                        Id = "t2",
                        Text = "Write regression tests",
                        IsCompleted = false,
                        IsImportant = false,
                        DueDate = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(8))
                    }
                ]
            });

            // [P0-2] CaptureTodoSnapshotAsync projects both canonical & documented alias fields
            CustomWidgetTodoSnapshot snap = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: settingsSvc,
                maxItems: 10,
                widgetsDataRootOverride: todoWidgetsRoot);
            Assert.Equal(2, snap.Records.Count);
            CustomWidgetRecord first = snap.Records[0];
            Assert.Equal("Ship Phase 3.5", first.GetField("title"));
            Assert.Equal("Ship Phase 3.5", first.GetField("text"));
            Assert.Equal("false", first.GetField("done"));
            Assert.Equal("false", first.GetField("isCompleted"));
            Assert.Equal("true", first.GetField("important"));
            Assert.False(string.IsNullOrWhiteSpace(first.GetField("dueDate")));
            Assert.Equal(first.GetField("dueText"), first.GetField("dueDate"));
            Assert.Equal("Work Tasks", first.GetField("listName"));

            (string primary, string secondary) = CustomWidgetNativeRenderer.ResolveDefaultListItemTexts(
                listParse.Manifest!.View,
                first);
            Assert.Equal("Ship Phase 3.5", primary);
            Assert.Equal(first.GetField("dueDate"), secondary);

            // [P1-3] Mutating ONLY the 2nd item's title triggers ViewRebuildRequested via full snapshot fingerprint
            CustomWidgetCapabilitySpec todoCap = CustomWidgetCapabilityRegistry.All.First(
                static c => string.Equals(c.Id, "todo-progress-summary", StringComparison.Ordinal));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore("w-todo", Path.Combine(tempRoot, "widgets"));
            await store.WriteWorkspaceBundleAsync(
                $$"""
                {
                  "schemaVersion": 1,
                  "id": "test.todo-golden",
                  "name": "Todo Golden",
                  {{todoCap.GoldenSnippetJson.TrimStart().TrimStart('{').TrimEnd().TrimEnd('}')}}
                }
                """,
                logicJs: null,
                locales: null);

            string secondTitle = "Write regression tests";
            int rebuildCount = 0;
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-todo", Name = "Todo", WidgetKind = WidgetKind.Custom },
                locSvc,
                store,
                dispatcherQueue: null,
                settingsService: settingsSvc)
            {
                TodoSnapshotProviderOverride = (_, _) => Task.FromResult(new CustomWidgetTodoSnapshot(
                    TotalCount: 2,
                    PendingCount: 2,
                    CompletedCount: 0,
                    TodayPendingCount: 0,
                    TodayCompletedCount: 0,
                    OverdueCount: 0,
                    ImportantPendingCount: 1,
                    CompletionPercent: 0,
                    NextTaskTitle: "Ship Phase 3.5",
                    NextTaskDueText: "11-09",
                    Summary: "0/2 (0%)",
                    Records:
                    [
                        new CustomWidgetRecord
                        {
                            Id = "t1",
                            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Ship Phase 3.5" }
                        },
                        new CustomWidgetRecord
                        {
                            Id = "t2",
                            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = secondTitle }
                        }
                    ]))
            };
            await vm.InitializeAsync();
            vm.ViewRebuildRequested += (_, _) => Interlocked.Increment(ref rebuildCount);

            secondTitle = "Write exhaustive regression tests";
            await vm.RefreshTodoNowAsync(triggerRebuild: true);

            Assert.True(Volatile.Read(ref rebuildCount) >= 1);
            Assert.Equal(
                "Write exhaustive regression tests",
                vm.CreateEvaluationContext().Collections["todos"][1].GetField("title"));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AuthoritativeMutation_StoreHashDeduplication_HyphenatedSecrets_AndMirroredCollectionExclusion_Work()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p35-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore("w-auth", Path.Combine(tempRoot, "widgets"));

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "test.auth-sync",
              "name": "Auth Sync",
              "settings": [
                { "key": "city", "type": "text", "label": "City", "default": "Beijing" },
                { "key": "api-key", "type": "secret", "label": "API Key", "default": "" }
              ],
              "state": { "note": "initial" },
              "collections": {
                "todos": { "maxItems": 10 },
                "notes": { "maxItems": 10 }
              },
              "sources": [
                { "type": "todo", "targetCollection": "todos", "intervalMs": 30000 }
              ],
              "view": {
                "type": "Text",
                "text": "${$settings.city}:${$secrets.api-key}:${state.note}"
              }
            }
            """;
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-auth", Name = "Auth Sync", WidgetKind = WidgetKind.Custom },
                locSvc,
                store,
                dispatcherQueue: null,
                settingsService: settingsSvc)
            {
                TodoSnapshotProviderOverride = (_, _) => Task.FromResult(new CustomWidgetTodoSnapshot(
                    TotalCount: 1,
                    PendingCount: 1,
                    CompletedCount: 0,
                    TodayPendingCount: 1,
                    TodayCompletedCount: 0,
                    OverdueCount: 0,
                    ImportantPendingCount: 0,
                    CompletionPercent: 0,
                    NextTaskTitle: "Private host task",
                    NextTaskDueText: "",
                    Summary: "0/1 (0%)",
                    Records:
                    [
                        new CustomWidgetRecord
                        {
                            Id = "host-1",
                            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Private host task" }
                        }
                    ]))
            };

            await vm.InitializeAsync();

            // [P0-3] + [P2-4] ApplyAuthoritativeMutationAsync updates settings & hyphenated secret in one shot
            await vm.ApplyAuthoritativeMutationAsync(new CustomWidgetSettingsMutation(
                WidgetId: "w-auth",
                Settings: new Dictionary<string, string>(StringComparer.Ordinal) { ["city"] = "Shanghai" },
                Secrets: new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "sk-hyphen-999" },
                ClearAllData: false));

            CustomWidgetEvaluationContext ctxAfterMut = vm.CreateEvaluationContext();
            Assert.Equal(
                "Shanghai:sk-hyphen-999:initial",
                CustomWidgetExpressionEvaluator.Interpolate("${$settings.city}:${$secrets.api-key}:${state.note}", ctxAfterMut));

            // [P1-5] Host-mirrored "todos" collection must NOT leak into persisted store.json
            string persistedStoreJson = await File.ReadAllTextAsync(Path.Combine(store.WidgetDirectory, "store.json"));
            Assert.DoesNotContain("Private host task", persistedStoreJson, StringComparison.Ordinal);

            // [P1-5] Removing "todos" from manifest must NOT trigger EvaluateStoreDataSafety data-loss warning
            CustomWidgetParseResult withTodos = CustomWidgetManifestParser.Parse(manifestJson);
            CustomWidgetParseResult withoutTodos = CustomWidgetManifestParser.Parse("""
            {
              "schemaVersion": 1,
              "id": "test.auth-sync",
              "name": "Auth Sync",
              "collections": { "notes": { "maxItems": 10 } },
              "view": { "type": "Text", "text": "clean" }
            }
            """);
            var fakePayload = new CustomWidgetStorePayload();
            fakePayload.Collections["todos"] =
            [
                new CustomWidgetRecord { Id = "host-1", Fields = new Dictionary<string, string> { ["title"] = "Private host task" } }
            ];
            CustomWidgetDataSafetyReport safety = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
                previousManifest: withTodos.Manifest,
                liveStore: fakePayload,
                candidateManifest: withoutTodos.Manifest!);
            Assert.False(safety.HasDataLossRisk);

            // [P0-3] Content-hash self-write deduplication: own save matches hash, immediate external edit differs!
            string? ownHash = store.LastSavedStoreContentHash;
            Assert.False(string.IsNullOrWhiteSpace(ownHash));
            Assert.Equal(ownHash, store.ReadCurrentStoreContentHash());

            // Simulate immediate (<10ms) external CLI write to store.json without waiting 750ms
            var externalStore = new CustomWidgetDataStore("w-auth", Path.Combine(tempRoot, "widgets"));
            CustomWidgetStorePayload extPayload = await externalStore.LoadAsync();
            extPayload.State["note"] = "external-cli-instant";
            await externalStore.SaveAsync(extPayload);

            Assert.NotEqual(ownHash, store.ReadCurrentStoreContentHash());
            await vm.ReloadManifestFromDiskAsync();
            Assert.Equal("external-cli-instant", vm.CreateEvaluationContext().State["note"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ForkLineage_PreservedAcrossAiTurns_SyncsStaticCapsuleTitle_AndDedupesCrossLocaleSuffix()
    {
        const string sourceJson = """
        {
          "schemaVersion": 1,
          "id": "deskbox.focus-pro",
          "version": "2.3.0",
          "name": "Focus Pro",
          "capsule": {
            "title": "Focus Pro",
            "summary": "${$music.title}"
          },
          "view": { "type": "Text", "text": "hello" }
        }
        """;

        // [P0-4] + [P2-1] Initial fork in en-US updates name + static capsule.title and stamps lineage + createdAtUtc
        CustomWidgetForkBundleResult forkEn = CustomWidgetOnboardingHelper.CreateForkBundle(
            sourceJson,
            logicJs: null,
            locales: null,
            activeCulture: "en-US");
        CustomWidgetManifest? forkEnManifest = CustomWidgetManifestParser.Parse(forkEn.ManifestJson).Manifest;
        Assert.NotNull(forkEnManifest);
        Assert.Equal("deskbox.focus-pro", forkEnManifest!.ForkedFromId);
        Assert.Equal("2.3.0", forkEnManifest.ForkedFromVersion);
        Assert.False(string.IsNullOrWhiteSpace(forkEnManifest.CreatedAtUtc));
        Assert.Equal("Focus Pro (Remix)", forkEnManifest.Name);
        Assert.Equal("Focus Pro (Remix)", forkEnManifest.Capsule?.Title);

        // [P2-1] Re-forking in zh-CN replaces "(Remix)" cleanly instead of stacking "Focus Pro (Remix) (二创)"
        CustomWidgetForkBundleResult reforkZh = CustomWidgetOnboardingHelper.CreateForkBundle(
            forkEn.ManifestJson,
            logicJs: null,
            locales: null,
            activeCulture: "zh-CN");
        CustomWidgetManifest? reforkZhManifest = CustomWidgetManifestParser.Parse(reforkZh.ManifestJson).Manifest;
        Assert.NotNull(reforkZhManifest);
        Assert.DoesNotContain("(Remix)", reforkZhManifest!.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("二创", reforkZhManifest.Name, StringComparison.Ordinal);

        // [P0-4] Follow-up AI generate turn omitting lineage fields MUST preserve forkedFromId/Version/createdAtUtc
        const string aiTurnEnvelope = """
        ```json
        {
          "kind": "edit",
          "reply": "Updated text.",
          "changeSummary": "Changed greeting",
          "manifest": {
            "schemaVersion": 1,
            "id": "user.fork-focus-pro-abc123",
            "version": "1.0.0",
            "name": "Focus Pro (Fork)",
            "view": { "type": "Text", "text": "updated by AI" }
          }
        }
        ```
        """;
        StudioTurnResult turnRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            aiTurnEnvelope,
            isInitialCreation: false,
            fallbackCulture: "en-US",
            currentManifestJson: forkEn.ManifestJson);
        Assert.True(turnRes.Success);
        CustomWidgetManifest? afterAi = turnRes.ParseResult?.Manifest;
        Assert.NotNull(afterAi);
        Assert.Equal("deskbox.focus-pro", afterAi!.ForkedFromId);
        Assert.Equal("2.3.0", afterAi.ForkedFromVersion);
        Assert.Equal(forkEnManifest.CreatedAtUtc, afterAi.CreatedAtUtc);

        // [P0-4] Bare manifest paste / auto-repair turn ALSO preserves lineage
        const string bareRepairTurn = """
        {
          "schemaVersion": 1,
          "id": "user.fork-focus-pro-abc123",
          "version": "1.0.0",
          "name": "Focus Pro (Fork)",
          "view": { "type": "Text", "text": "repaired" }
        }
        """;
        StudioTurnResult bareRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            bareRepairTurn,
            isInitialCreation: false,
            fallbackCulture: "en-US",
            currentManifestJson: forkEn.ManifestJson);
        Assert.True(bareRes.Success);
        Assert.Equal("deskbox.focus-pro", bareRes.ParseResult?.Manifest?.ForkedFromId);
    }

    [Fact]
    public async Task RemoteCatalogV2_CleansRetiredLogicAndLocales_SkipsEtagOnSubArtifactFailure_AndAvoidsFallbackScriptGraft()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p35-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cacheDir = Path.Combine(tempRoot, "cache");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(widgetsDir);

            // 1) [P0-5] Upgrade scripted v2.0.0 -> scriptless v2.1.0 cleans retired logic.js & locales in cache + OTA store
            string v20Manifest = """
            {
              "schemaVersion": 1,
              "id": "deskbox.clean-test",
              "version": "2.0.0",
              "name": "${$i18n.meta.name}",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "view": { "type": "Text", "text": "${$computed.v}" }
            }
            """;
            string v20Catalog = $$"""
            {
              "schemaVersion": 2,
              "widgets": [
                {
                  "id": "clean-test",
                  "manifestId": "deskbox.clean-test",
                  "name": "Clean Test",
                  "version": "2.0.0",
                  "manifestJson": {{JsonSerializer.Serialize(v20Manifest)}},
                  "logicJs": "export function onInit(ctx) { ctx.computed.v = '2.0'; }",
                  "locales": { "zh-CN": { "meta.name": "测试 2.0" }, "ja-JP": { "meta.name": "テスト 2.0" } }
                }
              ]
            }
            """;

            var catalogSvc = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new MockRouterHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            Assert.True(await catalogSvc.ApplyCatalogJsonAsync(v20Catalog, "\"etag-v20\"", CustomWidgetRemoteCatalogService.PrimaryCatalogUrl));

            var otaStore = new CustomWidgetDataStore("w-clean", widgetsDir);
            await otaStore.ReadOrCreateManifestJsonAsync("clean-test", catalogOverride: catalogSvc);
            Assert.True(File.Exists(otaStore.LogicJsPath));
            Assert.True(Directory.Exists(otaStore.LocalesDirectoryPath));

            string v21Manifest = """
            {
              "schemaVersion": 1,
              "id": "deskbox.clean-test",
              "version": "2.1.0",
              "name": "Clean Test Declarative",
              "view": { "type": "Text", "text": "pure-v2.1" }
            }
            """;
            string v21Catalog = $$"""
            {
              "schemaVersion": 2,
              "widgets": [
                {
                  "id": "clean-test",
                  "manifestId": "deskbox.clean-test",
                  "name": "Clean Test",
                  "version": "2.1.0",
                  "manifestJson": {{JsonSerializer.Serialize(v21Manifest)}}
                }
              ]
            }
            """;

            Assert.True(await catalogSvc.ApplyCatalogJsonAsync(v21Catalog, "\"etag-v21\"", CustomWidgetRemoteCatalogService.PrimaryCatalogUrl));
            Assert.True(catalogSvc.TryGetRemoteTemplateBundle("clean-test", out CustomWidgetTemplateBundle? v21Bundle));
            Assert.Null(v21Bundle!.LogicJs);

            // Cold-start reload from disk cache must NOT resurrect v2.0 logic.js!
            var coldCatalog = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new MockRouterHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            Assert.True(coldCatalog.TryGetRemoteTemplateBundle("clean-test", out CustomWidgetTemplateBundle? coldV21));
            Assert.Null(coldV21!.LogicJs);

            // OTA upgrade on existing widget instance must also remove retired logic.js & locales/
            string upgradedInstanceJson = await otaStore.ReadOrCreateManifestJsonAsync("clean-test", catalogOverride: catalogSvc);
            Assert.Equal("2.1.0", CustomWidgetManifestParser.Parse(upgradedInstanceJson).Manifest?.Version);
            Assert.False(File.Exists(otaStore.LogicJsPath));
            Assert.False(Directory.Exists(otaStore.LocalesDirectoryPath));

            // 2) [P1-2] Transient 503 on bundleUrl must NOT lock in top-level ETag in meta.json
            string partialFailCacheDir = Path.Combine(tempRoot, "partial-cache");
            var failingSubArtifactCatalog = new CustomWidgetRemoteCatalogService(
                partialFailCacheDir,
                new MockRouterHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            string catalogWithGoodAndBrokenSubUrl = $$"""
            {
              "schemaVersion": 2,
              "widgets": [
                {
                  "id": "ok-item",
                  "manifestId": "deskbox.ok-item",
                  "name": "OK",
                  "version": "1.0.0",
                  "manifestJson": {{JsonSerializer.Serialize(v21Manifest)}}
                },
                {
                  "id": "broken-sub",
                  "manifestId": "deskbox.broken-sub",
                  "name": "Broken",
                  "version": "1.0.0",
                  "bundleUrl": "https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/missing.deskbox-widget"
                }
              ]
            }
            """;
            Assert.True(await failingSubArtifactCatalog.ApplyCatalogJsonAsync(
                catalogWithGoodAndBrokenSubUrl,
                "\"etag-do-not-save\"",
                CustomWidgetRemoteCatalogService.PrimaryCatalogUrl));

            string metaPath = Path.Combine(partialFailCacheDir, "meta.json");
            if (File.Exists(metaPath))
            {
                string rawMeta = await File.ReadAllTextAsync(metaPath);
                Assert.DoesNotContain("etag-do-not-save", rawMeta, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task ImagePathSandbox_PackageSanitizer_ProtocolUncGuard_AndValidatorDiagnostics_EnforceContracts()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p35-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1) [P1-4] ResolveAuthorizedImagePath blocks outside files & env expansion, allows widget dir & smtc-covers
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore("w-img", Path.Combine(tempRoot, "widgets"));
            await store.WriteWorkspaceBundleAsync(
                """{"schemaVersion":1,"id":"test.img","name":"Img","view":{"type":"Text","text":"ok"}}""",
                logicJs: null,
                locales: null);

            string insideAsset = Path.Combine(store.WidgetDirectory, "cover.png");
            await File.WriteAllBytesAsync(insideAsset, [0x89, 0x50, 0x4E, 0x47]);

            string outsideSecretImg = Path.Combine(tempRoot, "outside-private.png");
            await File.WriteAllBytesAsync(outsideSecretImg, [0x89, 0x50, 0x4E, 0x47]);

            string smtcDir = CustomWidgetHostSnapshotService.GetSmtcCoverCacheDirectory();
            Directory.CreateDirectory(smtcDir);
            string smtcCover = Path.Combine(smtcDir, $"test-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(smtcCover, [0x89, 0x50, 0x4E, 0x47]);

            try
            {
                using var vm = new CustomWidgetViewModel(
                    new WidgetConfig { Id = "w-img", Name = "Img", WidgetKind = WidgetKind.Custom },
                    locSvc,
                    store,
                    dispatcherQueue: null,
                    settingsService: settingsSvc);
                await vm.InitializeAsync();

                Assert.Equal(Path.GetFullPath(insideAsset), vm.ResolveAuthorizedImagePath("cover.png"));
                Assert.Equal(Path.GetFullPath(smtcCover), vm.ResolveAuthorizedImagePath(smtcCover));
                Assert.Empty(vm.ResolveAuthorizedImagePath(outsideSecretImg));
                Assert.Empty(vm.ResolveAuthorizedImagePath("%USERPROFILE%\\Desktop\\secret.png"));
            }
            finally
            {
                CustomWidgetStorageFileOps.DeleteFileIfExists(smtcCover);
            }

            // 2) [P1-6] SanitizeManifestForSharing scrubs rooted non-USERPROFILE drive paths, and FirstRunSetup flags missing default paths
            const string rawWithDrivePaths = """
            {
              "schemaVersion": 1,
              "id": "user.vault-helper",
              "name": "Vault Helper",
              "settings": [
                { "key": "vaultFolder", "type": "text", "label": "Vault Path", "default": "E:\\PersonalObsidian\\MyVault" }
              ],
              "state": {
                "lastOpenedPath": "E:\\PersonalObsidian\\secret-note.md"
              },
              "view": { "type": "Text", "text": "${$settings.vaultFolder}" }
            }
            """;
            (string sanitizedJson, int sanitizedPathsCount, _) =
                CustomWidgetPackageService.SanitizeManifestForSharing(rawWithDrivePaths);
            Assert.True(sanitizedPathsCount >= 2);
            Assert.DoesNotContain("PersonalObsidian", sanitizedJson, StringComparison.Ordinal);

            string pkgPath = Path.Combine(tempRoot, "shared.deskbox-widget");
            await using (FileStream fs = new(pkgPath, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry("manifest.json");
                await using Stream es = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(sanitizedJson);
                await es.WriteAsync(bytes);
            }

            // Also verify older packages with non-existent rooted defaults trigger First-Run Setup UNTIL user supplies an override
            CustomWidgetManifest stalePathManifest = CustomWidgetManifestParser.Parse(rawWithDrivePaths).Manifest!;
            CustomWidgetSetupInspection beforeOverride = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(stalePathManifest, "en-US");
            Assert.True(beforeOverride.RequiresAttention);

            CustomWidgetSetupInspection afterOverride = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
                stalePathManifest,
                "en-US",
                currentSettings: new Dictionary<string, string>(StringComparer.Ordinal) { ["vaultFolder"] = @"D:\Configured\Vault" });
            Assert.False(afterOverride.RequiresAttention);

            // 3) [P1-6] + [P2-5] CLI local .deskbox-widget extraction works, UNC paths are rejected, Debug/HKLM protocol reg skipped
            string? cliProtocol = DeskBoxProtocolActivationService.TryExtractProtocolUri(["DeskBox.exe", pkgPath]);
            Assert.NotNull(cliProtocol);
            Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(cliProtocol, out DeskBoxProtocolIntent? cliIntent, out _));
            Assert.Equal(Path.GetFullPath(pkgPath), cliIntent!.LocalPackageFilePath);

            Assert.Null(DeskBoxProtocolActivationService.TryExtractProtocolUri(["DeskBox.exe", @"\\192.0.2.9\evil\trap.deskbox-widget"]));
            Assert.False(DeskBoxProtocolActivationService.TryParseProtocolUri(
                "deskbox://install-widget?file=%5C%5C192.0.2.9%5Cevil%5Ctrap.deskbox-widget",
                out _,
                out _));

            Assert.True(CustomWidgetStorageFileOps.ShouldSkipPerUserProtocolRegistration(
                @"D:\project\wingezi\src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe",
                machineRegisteredCommand: null));
            Assert.True(CustomWidgetStorageFileOps.ShouldSkipPerUserProtocolRegistration(
                @"C:\Program Files\DeskBox\DeskBox.exe",
                machineRegisteredCommand: "\"C:\\Program Files\\DeskBox\\DeskBox.exe\" \"%1\""));
            Assert.False(CustomWidgetStorageFileOps.ShouldSkipPerUserProtocolRegistration(
                @"D:\PortableApps\DeskBox\DeskBox.exe",
                machineRegisteredCommand: null));

            // 4) [P2-2] + [P1-7] Validator warns on clamped music/todo intervals & undeclared todo collection; compatibility stamps 1.6.0
            CustomWidgetParseResult warnParse = CustomWidgetScriptValidator.ValidateWorkspaceBundle("""
            {
              "schemaVersion": 1,
              "id": "test.warn-sources",
              "name": "Warn Sources",
              "sources": [
                { "type": "music", "intervalMs": 300 },
                { "type": "todo", "targetCollection": "undeclaredTodos", "intervalMs": 1000 }
              ],
              "view": { "type": "Text", "text": "${$music.title}" }
            }
            """).ToParseResult();
            Assert.NotNull(warnParse.Manifest);
            Assert.Equal(3, warnParse.Diagnostics.Count(static d => !d.IsError));
            Assert.Contains("\"minAppVersion\": \"1.6.0\"", CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(
                """{"schemaVersion":1,"id":"test.m","name":"M","sources":[{"type":"music"}],"view":{"type":"Text","text":"ok"}}"""),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void MultiLineWinUiTextBoxes_NeverAssignTextBeforeAcceptsReturn_AndMcpDialogUsesTwoOptionLayout()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DeskBox", "DeskBox.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        string srcRoot = Path.Combine(dir.FullName, "src", "DeskBox");

        var initializerRegex = new System.Text.RegularExpressions.Regex(
            @"new\s+TextBox\s*\r?\n?\s*\{(?<body>[^{}]*)\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (string csFile in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(csFile);
            foreach (System.Text.RegularExpressions.Match match in initializerRegex.Matches(source))
            {
                string body = match.Groups["body"].Value;
                var acceptsMatch = System.Text.RegularExpressions.Regex.Match(body, @"\bAcceptsReturn\s*=\s*true\b");
                var textMatch = System.Text.RegularExpressions.Regex.Match(body, @"(?<!Placeholder)\bText\s*=");
                if (acceptsMatch.Success && textMatch.Success)
                {
                    Assert.True(
                        acceptsMatch.Index < textMatch.Index,
                        $"WinUI 3 TextBox in '{Path.GetRelativePath(srcRoot, csFile)}' sets Text before AcceptsReturn = true, which truncates multi-line text at the first newline.");
                }
            }
        }

        string studioPages = File.ReadAllText(Path.Combine(srcRoot, "Views", "CustomWidgetStudioWindow.Pages.cs"));
        int dlgStart = studioPages.IndexOf("private async Task ShowAgentMcpQuickConnectDialogAsync", StringComparison.Ordinal);
        int dlgEnd = studioPages.IndexOf("private static string TruncateTextElements", dlgStart, StringComparison.Ordinal);
        Assert.True(dlgStart > 0 && dlgEnd > dlgStart);
        string dlgBody = studioPages[dlgStart..dlgEnd];

        Assert.Contains("CustomWidget.Workshop.AgentMcp.AutoMethodTitle", dlgBody, StringComparison.Ordinal);
        Assert.Contains("CustomWidget.Workshop.AgentMcp.ManualMethodTitle", dlgBody, StringComparison.Ordinal);
        Assert.Contains("McpConfigFormat.VsCodeJson", dlgBody, StringComparison.Ordinal);
        Assert.Contains("McpConfigFormat.CodexToml", dlgBody, StringComparison.Ordinal);
        Assert.Contains("snippetBox.Text = currentSnippetText;", dlgBody, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.None", dlgBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyToClipboard(snippetBox.Text)", studioPages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveDesktopOfficialWidget_AutoHotUpgradesOnRemoteCatalogUpdate_PreservingUserData_AndSupportsRemoteDeprecation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-ota-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var catalogSvc = new CustomWidgetRemoteCatalogService(Path.Combine(tempRoot, "catalog-cache"));

            // 1. Official unmodified desktop widget on v1.0.0 with user data
            var officialStore = new CustomWidgetDataStore("w-official", Path.Combine(tempRoot, "widgets"));
            await officialStore.WriteWorkspaceBundleAsync(
                """
                {
                  "schemaVersion": 1,
                  "id": "deskbox.ota-demo",
                  "name": "OTA Demo v1",
                  "version": "1.0.0",
                  "origin": "official",
                  "isCustomized": false,
                  "state": { "cups": "0" },
                  "view": { "type": "Text", "text": "v1:${state.cups}" }
                }
                """,
                logicJs: null,
                locales: null);
            await officialStore.SaveAsync(new CustomWidgetStorePayload
            {
                State = new Dictionary<string, string>(StringComparer.Ordinal) { ["cups"] = "6" }
            });

            // 2. User-customized instance of the same template ID on v1.0.0
            var customizedStore = new CustomWidgetDataStore("w-customized", Path.Combine(tempRoot, "widgets"));
            await customizedStore.WriteWorkspaceBundleAsync(
                """
                {
                  "schemaVersion": 1,
                  "id": "deskbox.ota-demo",
                  "name": "My Custom OTA Demo",
                  "version": "1.0.0",
                  "origin": "official",
                  "isCustomized": true,
                  "state": { "cups": "3" },
                  "view": { "type": "Text", "text": "custom:${state.cups}" }
                }
                """,
                logicJs: null,
                locales: null);

            var rebuildTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var officialVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-official", Name = "OTA Demo", WidgetKind = WidgetKind.Custom },
                locSvc,
                officialStore,
                dispatcherQueue: null,
                settingsService: settingsSvc,
                remoteCatalogService: catalogSvc);
            await officialVm.InitializeAsync();
            officialVm.ViewRebuildRequested += (_, _) => rebuildTcs.TrySetResult(true);

            using var customizedVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-customized", Name = "My Custom", WidgetKind = WidgetKind.Custom },
                locSvc,
                customizedStore,
                dispatcherQueue: null,
                settingsService: settingsSvc,
                remoteCatalogService: catalogSvc);
            await customizedVm.InitializeAsync();

            // 3. Push remote OTA catalog update v1.1.0 + deprecate built-in 'hydration'
            bool applied = await catalogSvc.ApplyCatalogJsonAsync(
                """
                {
                  "schemaVersion": 2,
                  "updatedAtUtc": "2026-11-08T12:00:00Z",
                  "widgets": [
                    {
                      "id": "ota-demo",
                      "manifestId": "deskbox.ota-demo",
                      "name": "OTA Demo v2",
                      "version": "1.1.0",
                      "deprecated": false,
                      "manifest": {
                        "schemaVersion": 1,
                        "id": "deskbox.ota-demo",
                        "name": "OTA Demo v2",
                        "version": "1.1.0",
                        "state": { "cups": "0" },
                        "view": { "type": "Text", "text": "v2:${state.cups}" }
                      }
                    },
                    {
                      "id": "hydration",
                      "manifestId": "deskbox.hydration",
                      "name": "hydration",
                      "version": "999.0.0",
                      "deprecated": true
                    }
                  ]
                }
                """);
            Assert.True(applied);

            // Wait for live desktop official widget to auto-upgrade in place
            Task completed = await Task.WhenAny(rebuildTcs.Task, Task.Delay(3000));
            Assert.Same(rebuildTcs.Task, completed);

            Assert.Equal("1.1.0", officialVm.Manifest.Version);
            Assert.Equal("6", officialVm.CreateEvaluationContext().State["cups"]);

            // Customized widget remains untouched at 1.0.0
            await Task.Delay(100);
            Assert.Equal("1.0.0", customizedVm.Manifest.Version);

            // Deprecated template is hidden from Studio merged gallery, while new remote template is visible
            IReadOnlyList<CustomWidgetCatalogEntry> gallery = catalogSvc.GetMergedOfficialTemplates();
            Assert.DoesNotContain(gallery, e => string.Equals(e.Id, "hydration", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(gallery, e => string.Equals(e.Id, "ota-demo", StringComparison.OrdinalIgnoreCase) && e.Version == "1.1.0");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task StudioGallerySync_ReportsAccurateDiffStatus_AndBuildWorkshopGallerySpecs_HonorsDeprecationUpgradeAndNewBadges()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-studio-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locSvc = new LocalizationService(settingsSvc);

            const string remoteCatalogPayload = """
            {
              "schemaVersion": 2,
              "updatedAtUtc": "2026-11-08T16:45:00Z",
              "widgets": [
                {
                  "id": "hydration",
                  "manifestId": "starter.hydration",
                  "name": "hydration",
                  "version": "999.0.0",
                  "deprecated": true
                },
                {
                  "id": "countdown",
                  "manifestId": "deskbox.countdown",
                  "name": "倒数日",
                  "description": "云端升级版倒数日，支持农历与置顶高亮。",
                  "version": "99.1.0",
                  "deprecated": false,
                  "manifest": {
                    "schemaVersion": 1,
                    "id": "deskbox.countdown",
                    "name": "倒数日",
                    "version": "99.1.0",
                    "view": { "type": "Text", "text": "Countdown v99.1.0" }
                  }
                },
                {
                  "id": "cloud-kanban",
                  "manifestId": "deskbox.cloud-kanban",
                  "name": "云端看板",
                  "nameEn": "Cloud Kanban",
                  "description": "轻量三列任务看板",
                  "iconGlyph": "📋",
                  "version": "1.2.0",
                  "deprecated": false,
                  "manifest": {
                    "schemaVersion": 1,
                    "id": "deskbox.cloud-kanban",
                    "name": "云端看板",
                    "version": "1.2.0",
                    "view": { "type": "Text", "text": "Kanban" }
                  }
                }
              ]
            }
            """;

            bool simulateOffline = false;
            using var handler = new MockRouterHttpHandler(req =>
            {
                if (simulateOffline)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(remoteCatalogPayload, Encoding.UTF8, "application/json")
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"sync-v1\"");
                return resp;
            });

            var catalogSvc = new CustomWidgetRemoteCatalogService(Path.Combine(tempRoot, "catalog-cache"), handler);
            Assert.Null(catalogSvc.GetLastCheckedUtc());

            int catalogUpdatedEvents = 0;
            catalogSvc.CatalogUpdated += (_, _) => Interlocked.Increment(ref catalogUpdatedEvents);

            // 1. First manual force sync detects 3 changes (1 deprecated + 1 upgraded + 1 remote-only added)
            CustomWidgetCatalogSyncResult firstSync = await catalogSvc.SyncAsync(force: true);
            Assert.Equal(CustomWidgetCatalogSyncStatus.Updated, firstSync.Status);
            Assert.Equal(3, firstSync.ChangedCount);
            Assert.NotNull(firstSync.LastCheckedUtc);
            Assert.NotNull(catalogSvc.GetLastCheckedUtc());
            Assert.Equal(1, Volatile.Read(ref catalogUpdatedEvents));

            // 2. Clicking manual sync again against the identical 200 OK payload reports UpToDate (0 changes, no spurious event)
            CustomWidgetCatalogSyncResult secondSync = await catalogSvc.SyncAsync(force: true);
            Assert.Equal(CustomWidgetCatalogSyncStatus.UpToDate, secondSync.Status);
            Assert.Equal(0, secondSync.ChangedCount);
            Assert.Equal(1, Volatile.Read(ref catalogUpdatedEvents));

            // 3. Offline manual sync reports OfflineOrFailed while preserving cached official templates
            simulateOffline = true;
            CustomWidgetCatalogSyncResult offlineSync = await catalogSvc.SyncAsync(force: true);
            Assert.Equal(CustomWidgetCatalogSyncStatus.OfflineOrFailed, offlineSync.Status);

            // 4. Studio BuildWorkshopGallerySpecs accurately maps deprecation, remote upgrade, and remote-only metadata
            var studioSpecs = CustomWidgetStudioWindow.BuildWorkshopGallerySpecs(catalogSvc, locSvc);
            Assert.DoesNotContain(studioSpecs, s => string.Equals(s.Id, CustomWidgetTemplates.HydrationStarterId, StringComparison.OrdinalIgnoreCase));

            var upgradedCountdown = Assert.Single(
                studioSpecs,
                s => string.Equals(s.Id, CustomWidgetTemplates.CountdownTemplateId, StringComparison.OrdinalIgnoreCase));
            Assert.True(upgradedCountdown.IsRemoteUpgraded);
            Assert.False(upgradedCountdown.IsRemoteOnly);
            Assert.Equal("99.1.0", upgradedCountdown.Version);
            Assert.Null(upgradedCountdown.DirectLabel); // Preserves 13-locale native LabelKey when title wasn't renamed
            Assert.Equal("云端升级版倒数日，支持农历与置顶高亮。", upgradedCountdown.DirectDescription);

            var cloudKanban = Assert.Single(
                studioSpecs,
                s => string.Equals(s.Id, "cloud-kanban", StringComparison.OrdinalIgnoreCase));
            Assert.True(cloudKanban.IsRemoteOnly);
            Assert.False(cloudKanban.IsRemoteUpgraded);
            Assert.Equal("1.2.0", cloudKanban.Version);
            Assert.Equal("📋", cloudKanban.Emoji);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
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

    private sealed class MockRouterHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
