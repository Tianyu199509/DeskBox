using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Controls.Studio;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class CustomWidgetStudioHostProcessTests
{
    [Fact]
    public void StudioHostLaunch_ParsesMainProcessIdAndRejectsUnrelatedFlags()
    {
        Assert.True(App.StudioHostLaunch.IsStudioHostLaunch(["DeskBox.exe", "--studio", "37604"]));
        Assert.True(App.StudioHostLaunch.IsStudioHostLaunch(["DeskBox.exe", "--STUDIO", "37604"]));
        Assert.Equal(37604, App.StudioHostLaunch.TryGetMainProcessId(["DeskBox.exe", "--studio", "37604"]));

        Assert.False(App.StudioHostLaunch.IsStudioHostLaunch(["DeskBox.exe", "--open-studio"]));
        Assert.False(App.StudioHostLaunch.IsStudioHostLaunch(["DeskBox.exe", "--settings", "37604"]));
        Assert.Equal(0, App.StudioHostLaunch.TryGetMainProcessId(["DeskBox.exe", "--studio"]));
        Assert.Equal(0, App.StudioHostLaunch.TryGetMainProcessId(["DeskBox.exe", "--studio", "invalid"]));
        Assert.Equal(0, App.StudioHostLaunch.TryGetMainProcessId(["DeskBox.exe", "--studio", "-5"]));
    }

    [Fact]
    public void DataPathService_AssignsIsolatedStudioPipeAndLogFile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.StudioPathTests", Guid.NewGuid().ToString("N"));
        var paths = new DeskBoxDataPathService(
            rootPath: tempRoot,
            isMicrosoftStore: false,
            localAppDataPath: Path.GetTempPath(),
            userProfilePath: Path.GetTempPath());

        Assert.StartsWith("DeskBox_Studio_Ipc_", paths.StudioIpcPipeName, StringComparison.Ordinal);
        Assert.NotEqual(paths.SettingsIpcPipeName, paths.StudioIpcPipeName);
        Assert.NotEqual(paths.AgentBridgePipeName, paths.StudioIpcPipeName);
        Assert.Equal(Path.Combine(Path.GetFullPath(tempRoot), "DeskBox.Studio.log"), paths.StudioHostLogFilePath);
        Assert.NotEqual(paths.LogFilePath, paths.StudioHostLogFilePath);
        Assert.NotEqual(paths.SettingsHostLogFilePath, paths.StudioHostLogFilePath);
    }

    [Fact]
    public async Task RemoteStudioHostBridge_LoopbackPipe_SynchronizesBootstrapMutationsAndLifecycle()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.StudioIpcLoopback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string pipeName = $"DeskBox_Studio_Test_{Guid.NewGuid():N}";

        try
        {
            var mainSettings = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            mainSettings.Settings.DefaultWidgetWidth = 360;
            mainSettings.Settings.DefaultWidgetHeight = 240;
            FeatureWidgetSettings.SetEnabled(mainSettings.Settings, WidgetKind.Custom, true);
            mainSettings.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
            {
                Id = "custom.initial",
                Name = "Initial Custom",
                WidgetKind = WidgetKind.Custom,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CustomWidgetViewModel.TemplateMetadataKey] = "deskbox.system-pulse"
                }
            });

            bool bootReadyCalled = false;
            string? farewellPayload = null;
            (long Hwnd, string? TargetId) lastWindowState = default;
            StudioContextReportPayload? lastContextReport = null;
            CustomWidgetSettingsMutation? lastMutation = null;
            string? committedStageId = null;
            string? discardedStageId = null;

            StudioShowIntentPayload? pendingInitialShow = new("custom.initial", "make it compact", AutoStartGeneration: false);
            var fakeInProcessBridge = new LoopbackTestHostBridge(
                mainSettings,
                onMutation: m => lastMutation = m,
                onCommitStage: stageId =>
                {
                    committedStageId = stageId;
                    mainSettings.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
                    {
                        Id = "custom.from-stage",
                        Name = "Staged Widget",
                        WidgetKind = WidgetKind.Custom
                    });
                    return "{\"ok\":true,\"result\":{\"committed\":true,\"widgetId\":\"custom.from-stage\"}}";
                },
                onDiscardStage: stageId => discardedStageId = stageId);

            var connectedClient = new ConnectedMcpClientInfo("TestAgent", "1.2.0", DateTimeOffset.UtcNow);
            var router = new StudioIpcRouter(
                settingsService: mainSettings,
                getInProcessBridge: () => fakeInProcessBridge,
                getLastMcpClient: () => connectedClient,
                consumeInitialBootstrapIntents: () =>
                {
                    StudioShowIntentPayload? consumed = pendingInitialShow;
                    pendingInitialShow = null;
                    return (consumed, null);
                },
                onHostBootReady: () => bootReadyCalled = true,
                onHostFarewell: payload => farewellPayload = payload,
                onWindowStateReported: (hwnd, target) => lastWindowState = (hwnd, target),
                onStudioContextReported: report => lastContextReport = report);

            await using SettingsIpcServer server = SettingsIpcServer.Start(pipeName, router.DispatchAsync);
            await using SettingsIpcClient client = await SettingsIpcClient.ConnectAsync(
                pipeName,
                TimeSpan.FromMilliseconds(500),
                attempts: 10);

            // 1. Bootstrap adopts snapshot & consumes initial show intent once.
            (bool bootOk, string? bootJson, string? bootErr) = await client.RequestAsync(StudioIpcMethods.Bootstrap);
            Assert.True(bootOk, bootErr);
            StudioBootstrapResponse? bootstrap = SettingsIpcPayload.Decode(
                bootJson!,
                SettingsIpcJsonContext.Default.StudioBootstrapResponse);
            Assert.NotNull(bootstrap);
            Assert.Equal("TestAgent", bootstrap!.LastMcpClient?.ClientName);
            Assert.Equal("custom.initial", bootstrap.InitialShowIntent?.WidgetId);

            (_, string? secondBootJson, _) = await client.RequestAsync(StudioIpcMethods.Bootstrap);
            StudioBootstrapResponse? secondBootstrap = SettingsIpcPayload.Decode(
                secondBootJson!,
                SettingsIpcJsonContext.Default.StudioBootstrapResponse);
            Assert.Null(secondBootstrap!.InitialShowIntent);

            var hostSettings = new SettingsService(ReadOnlySettingsPersistenceSink.Instance);
            hostSettings.AdoptExternalSnapshot(bootstrap.SettingsSnapshotJson);
            var hostTheme = new ThemeService(hostSettings);
            var remoteBridge = new RemoteStudioHostBridge(
                client,
                hostSettings,
                hostTheme,
                static _ => { },
                static _ => { });

            Assert.Equal((360, 240), remoteBridge.GetDefaultWidgetSize());
            Assert.Single(remoteBridge.GetCustomWidgetInstances());
            Assert.Equal("custom.initial", remoteBridge.ResolveCustomWidgetConfig("system-pulse")?.Id);

            // 2. CreateCustomWidgetAsync updates main layout AND mirrors immediately on Studio host before returning.
            var locales = new CustomWidgetLocaleCatalog(
                null,
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "你好" },
                    ["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Hello" }
                });
            GlanceWidgetInstanceInfo? created = await remoteBridge.CreateCustomWidgetAsync(
                "{\"schemaVersion\":1}",
                logicJsOverride: "export function onInit() {}",
                hasLogicJsOverride: true,
                localesOverride: locales,
                hasLocalesOverride: true,
                initialSettings: new Dictionary<string, string> { ["city"] = "Shanghai" });
            Assert.NotNull(created);
            Assert.Equal("custom.created-1", created!.Id);
            Assert.Equal(2, remoteBridge.GetCustomWidgetInstances().Count);
            Assert.NotNull(remoteBridge.ResolveCustomWidgetConfig("custom.created-1"));
            Assert.Equal("你好", fakeInProcessBridge.LastLocales?.Locales["zh-CN"]["title"]);

            // 3. UpdateCustomWidgetManifestAsync, ApplyMutation, SetEnabled, CommitAgentStage, RemoveWidget.
            GlanceWidgetInstanceInfo? updated = await remoteBridge.UpdateCustomWidgetManifestAsync(
                "custom.created-1",
                "{\"schemaVersion\":1,\"name\":\"Renamed\"}",
                reveal: true);
            Assert.Equal("Renamed", updated?.Name);
            Assert.Equal("Renamed", remoteBridge.ResolveCustomWidgetConfig("custom.created-1")?.Name);

            await remoteBridge.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(
                "custom.created-1",
                Settings: new Dictionary<string, string> { ["city"] = "Beijing" }));
            Assert.Equal("Beijing", lastMutation?.Settings?["city"]);

            await remoteBridge.SetCustomWidgetInstanceEnabledAsync("custom.created-1", false);
            Assert.False(remoteBridge.GetCustomWidgetInstances().Single(x => x.Id == "custom.created-1").IsEnabled);

            string stageResp = await remoteBridge.CommitAgentStageAsync("0123456789abcdef0123456789abcdef");
            Assert.Contains("\"committed\":true", stageResp, StringComparison.Ordinal);
            Assert.Equal("0123456789abcdef0123456789abcdef", committedStageId);
            Assert.NotNull(remoteBridge.ResolveCustomWidgetConfig("custom.from-stage"));

            await remoteBridge.DiscardAgentStageAsync("fedcba9876543210fedcba9876543210");
            Assert.Equal("fedcba9876543210fedcba9876543210", discardedStageId);

            await remoteBridge.RemoveCustomWidgetAsync("custom.created-1");
            Assert.Null(remoteBridge.ResolveCustomWidgetConfig("custom.created-1"));

            // 4. Window state, context report, boot-ready, and farewell round-trip.
            string windowStateJson = SettingsIpcPayload.Encode(
                new StudioWindowStatePayload(0x123456, "custom.from-stage"),
                SettingsIpcJsonContext.Default.StudioWindowStatePayload);
            (bool stateOk, _, _) = await client.RequestAsync(StudioIpcMethods.ReportWindowState, windowStateJson);
            Assert.True(stateOk);
            Assert.Equal((0x123456L, "custom.from-stage"), lastWindowState);

            string contextReportJson = SettingsIpcPayload.Encode(
                new StudioContextReportPayload("req-1", "{\"ok\":true}", "custom.from-stage"),
                SettingsIpcJsonContext.Default.StudioContextReportPayload);
            (bool ctxOk, _, _) = await client.RequestAsync(StudioIpcMethods.ReportStudioContext, contextReportJson);
            Assert.True(ctxOk);
            Assert.Equal("req-1", lastContextReport?.RequestId);
            Assert.Equal("{\"ok\":true}", lastContextReport?.ContextJson);

            (bool readyOk, _, _) = await client.RequestAsync(StudioIpcMethods.HostBootReady);
            Assert.True(readyOk);
            Assert.True(bootReadyCalled);

            (bool byeOk, _, _) = await client.RequestAsync(StudioIpcMethods.HostFarewell, "9876");
            Assert.True(byeOk);
            Assert.Equal("9876", farewellPayload);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomWidgetAudioService_CanonicalizesCueKeysToFiniteSet()
    {
        var distinctCanonical = new HashSet<string>(StringComparer.Ordinal);
        string[] inputs =
        [
            "tick", "click", "pop", "soft", "gentle",
            "alert", "warning", "error", "bell", "complete",
            "custom-cue-1", "custom-cue-2", "unknown-xyz"
        ];

        foreach (string cue in inputs)
        {
            distinctCanonical.Add(CustomWidgetAudioService.ResolveCanonicalCueKey(cue));
        }

        Assert.Equal(6, distinctCanonical.Count);
        Assert.Equal("tick", CustomWidgetAudioService.ResolveCanonicalCueKey("click"));
        Assert.Equal("alert", CustomWidgetAudioService.ResolveCanonicalCueKey("error"));
        Assert.Equal("complete", CustomWidgetAudioService.ResolveCanonicalCueKey("arbitrary-script-cue-999"));
    }

    [Fact]
    public void IconHelper_OversizedNonSharedPayload_DoesNotRetainBytesOrOrphanScopeIndex()
    {
        string scope = $"loh-scope-{Guid.NewGuid():N}";
        string nonSharedKey = $"source:C:\\temp\\{Guid.NewGuid():N}.ico|path:ico:size=256";
        byte[] smallBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] oversizedNonBmpBytes = new byte[90_000];
        oversizedNonBmpBytes[0] = 0x89;

        try
        {
            IconHelper.StoreCachedIconBytes(nonSharedKey, smallBytes);
            IconHelper.IndexIconBytesScopeKey(scope, nonSharedKey);
            Assert.True(IconHelper.HasCachedIconBytesForTest(nonSharedKey));

            IconHelper.RemoveCachedIconBytesForTest(nonSharedKey);
            IconHelper.IndexIconBytesScopeKey(scope, nonSharedKey);
            IconHelper.StoreCachedIconBytes(nonSharedKey, oversizedNonBmpBytes);

            Assert.False(IconHelper.HasCachedIconBytesForTest(nonSharedKey));
        }
        finally
        {
            IconHelper.RemoveCachedIconBytesForTest(nonSharedKey);
        }
    }

    [Fact]
    public void LocalSourcePayloadHelper_RecordBoundedTimestamp_EvictsOldestEntriesAboveCap()
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < 100; i++)
        {
            LocalSourcePayloadHelper.RecordBoundedTimestamp(map, $"source-{i}", 1000L + i);
        }

        Assert.Equal(64, map.Count);
        Assert.False(map.ContainsKey("source-0"));
        Assert.False(map.ContainsKey("source-35"));
        Assert.True(map.ContainsKey("source-36"));
        Assert.True(map.ContainsKey("source-99"));
    }

    [Fact]
    public void CredentialStore_CheckForExternalChangesAndNotify_BumpsGenerationAndRaisesEventOnce()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.CredSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var studioReader = new CustomWidgetAiCredentialStore(tempRoot);
            var settingsWriter = new CustomWidgetAiCredentialStore(tempRoot);

            Assert.Null(studioReader.Get("openai"));
            long initialGen = studioReader.Generation;
            int eventCount = 0;
            studioReader.CredentialsChanged += (_, _) => eventCount++;

            Assert.False(studioReader.CheckForExternalChangesAndNotify());
            Assert.Equal(0, eventCount);

            settingsWriter.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-test-cross-process-1234",
                CustomEndpoint: "https://api.openai.com/v1",
                CustomModel: "gpt-4o"));
            File.SetLastWriteTimeUtc(studioReader.FilePath, DateTime.UtcNow.AddSeconds(2));

            Assert.True(studioReader.CheckForExternalChangesAndNotify());
            Assert.Equal(1, eventCount);
            Assert.True(studioReader.Generation > initialGen);
            Assert.Equal("gpt-4o", studioReader.Get("openai")?.CustomModel);

            // Idempotent when file timestamp has not changed again.
            Assert.False(studioReader.CheckForExternalChangesAndNotify());
            Assert.Equal(1, eventCount);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OptimizeProcessHeaps_AndMemoryReclaimerTryCollect_SucceedAtRuntime()
    {
        Assert.True(DeskBox.Platform.Win32Helper.OptimizeProcessHeaps());

        MemoryReclaimResult result = MemoryReclaimer.TryCollect(
            reason: "unit-test-heap-optimize",
            bypassCooldown: true,
            updateCooldown: false);
        Assert.Equal(MemoryReclaimStatus.Completed, result.Status);
        Assert.True(result.Executed);
    }

    [Fact]
    public void StudioStageViewport_ResolveCommitButtonPresentation_ShowsPlaceForNewOrParkedAndSaveForVisible()
    {
        (string createGlyph, string createKey) =
            StudioStageViewportController.ResolveCommitButtonPresentation(isEditMode: false, isVisibleOnDesktop: false);
        Assert.Equal("\uE710", createGlyph);
        Assert.Equal("CustomWidget.Workshop.Footer.Place", createKey);

        (string parkedGlyph, string parkedKey) =
            StudioStageViewportController.ResolveCommitButtonPresentation(isEditMode: true, isVisibleOnDesktop: false);
        Assert.Equal("\uE710", parkedGlyph);
        Assert.Equal("CustomWidget.Workshop.Footer.Place", parkedKey);

        (string visibleGlyph, string visibleKey) =
            StudioStageViewportController.ResolveCommitButtonPresentation(isEditMode: true, isVisibleOnDesktop: true);
        Assert.Equal("\uE74E", visibleGlyph);
        Assert.Equal("CustomWidget.Workshop.Footer.Save", visibleKey);
    }

    [Fact]
    public void StudioCommit_ShouldRevealOnEditCommit_AlwaysRevealsParkedWidget()
    {
        Assert.True(StudioCommitAndWorkspaceCoordinator.ShouldRevealOnEditCommit(
            wasParkedOrHidden: true,
            locateOnDesktopAfterCommit: false));
        Assert.True(StudioCommitAndWorkspaceCoordinator.ShouldRevealOnEditCommit(
            wasParkedOrHidden: true,
            locateOnDesktopAfterCommit: true));
        Assert.True(StudioCommitAndWorkspaceCoordinator.ShouldRevealOnEditCommit(
            wasParkedOrHidden: false,
            locateOnDesktopAfterCommit: true));
        Assert.False(StudioCommitAndWorkspaceCoordinator.ShouldRevealOnEditCommit(
            wasParkedOrHidden: false,
            locateOnDesktopAfterCommit: false));
    }

    [Fact]
    public void WidgetManager_ApplyCustomWidgetRevealOnUpdate_UnparksAndEnablesWhenRevealingAndPreservesWhenSilent()
    {
        var parkedConfig = new WidgetConfig
        {
            Id = "custom-parked-1",
            Name = "Habit Wall",
            WidgetKind = WidgetKind.Custom,
            IsDisabled = true,
            IsVisible = false,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["custom.templateId"] = "custom.habit-sticker-wall",
                ["custom.parkedByUser"] = "true"
            }
        };

        bool featureEnabled = false;
        int enableFeatureCalls = 0;

        // Silent update (reveal: false) must preserve parked/disabled state.
        WidgetManager.ApplyCustomWidgetRevealOnUpdate(
            parkedConfig,
            reveal: false,
            isFeatureEnabled: () => featureEnabled,
            enableFeature: () => { featureEnabled = true; enableFeatureCalls++; });
        Assert.True(parkedConfig.IsDisabled);
        Assert.False(parkedConfig.IsVisible);
        Assert.Equal("true", parkedConfig.Metadata["custom.parkedByUser"]);
        Assert.Equal(0, enableFeatureCalls);

        // Revealing update (reveal: true) must unpark, enable custom widget feature, and mark visible.
        WidgetManager.ApplyCustomWidgetRevealOnUpdate(
            parkedConfig,
            reveal: true,
            isFeatureEnabled: () => featureEnabled,
            enableFeature: () => { featureEnabled = true; enableFeatureCalls++; });
        Assert.False(parkedConfig.IsDisabled);
        Assert.True(parkedConfig.IsVisible);
        Assert.False(parkedConfig.Metadata.ContainsKey("custom.parkedByUser"));
        Assert.True(featureEnabled);
        Assert.Equal(1, enableFeatureCalls);
    }

    [Fact]
    public void StudioPreviewFrameThemeRenderer_ResolveStaticThumbnailSurfaceColor_IsAlwaysOpaqueAcrossMaterialsAndThemes()
    {
        string[] materials =
        [
            SettingsService.WidgetMaterialTypeMica,
            SettingsService.WidgetMaterialTypeMicaAlt,
            SettingsService.WidgetMaterialTypeAcrylicBase,
            SettingsService.WidgetMaterialTypeAcrylic,
            SettingsService.WidgetMaterialTypeSolid
        ];
        Windows.UI.Color accent = Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);

        foreach (string material in materials)
        {
            var visual = new StudioShellVisualSettings(
                DefaultWidgetWidth: 320,
                DefaultWidgetHeight: 360,
                WidgetMaterialType: material,
                WidgetOpacity: 0.45,
                WidgetMaterialIntensity: 0.6,
                WidgetCornerPreference: "Default",
                WidgetTextShadowEnabled: false,
                WidgetBorderStyle: "Default",
                WidgetBorderColorMode: "Default",
                WidgetTitleIconMode: "Default",
                TextSize: 12,
                IconSize: 28);

            Windows.UI.Color darkColor = StudioPreviewFrameThemeRenderer.ResolveStaticThumbnailSurfaceColor(visual, isDark: true, accent);
            Windows.UI.Color lightColor = StudioPreviewFrameThemeRenderer.ResolveStaticThumbnailSurfaceColor(visual, isDark: false, accent);

            Assert.Equal(0xFF, darkColor.A);
            Assert.Equal(0xFF, lightColor.A);
            Assert.True(darkColor.R < lightColor.R, $"Expected dark thumbnail surface darker than light for material '{material}'.");
        }
    }

    [Fact]
    public void StudioGalleryThumbnailCache_PathsAndContentHash_TrackVersionThemeCultureMaterialAndEdits()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox.GalleryCachePathTests", Guid.NewGuid().ToString("N"));
        try
        {
            var micaVisual = NullStudioHostBridge.DefaultShellVisualSettings with
            {
                WidgetMaterialType = SettingsService.WidgetMaterialTypeMica
            };
            var acrylicVisual = NullStudioHostBridge.DefaultShellVisualSettings with
            {
                WidgetMaterialType = SettingsService.WidgetMaterialTypeAcrylicBase
            };

            string micaFp = StudioGalleryThumbnailCache.BuildMaterialFingerprint(micaVisual);
            string acrylicFp = StudioGalleryThumbnailCache.BuildMaterialFingerprint(acrylicVisual);
            Assert.NotEqual(micaFp, acrylicFp);

            string tplDarkPath = StudioGalleryThumbnailCache.ResolveTemplateThumbCachePath(
                "todo-agenda", "3.0.0", Microsoft.UI.Xaml.ElementTheme.Dark, "zh-CN", micaFp, tempDir);
            string tplLightPath = StudioGalleryThumbnailCache.ResolveTemplateThumbCachePath(
                "todo-agenda", "3.0.0", Microsoft.UI.Xaml.ElementTheme.Light, "zh-CN", micaFp, tempDir);
            Assert.StartsWith("v2__tpl__", Path.GetFileName(tplDarkPath), StringComparison.Ordinal);
            Assert.NotEqual(tplDarkPath, tplLightPath);

            string h1 = StudioGalleryThumbnailCache.ComputeUserWidgetContentHash("{\"id\":\"custom.a\"}", "export function onInit(){}", null, "zh-CN");
            string h1Repeat = StudioGalleryThumbnailCache.ComputeUserWidgetContentHash("{\"id\":\"custom.a\"}", "export function onInit(){}", null, "zh-CN");
            string h2 = StudioGalleryThumbnailCache.ComputeUserWidgetContentHash("{\"id\":\"custom.a\",\"name\":\"v2\"}", "export function onInit(){}", null, "zh-CN");
            string h3 = StudioGalleryThumbnailCache.ComputeUserWidgetContentHash("{\"id\":\"custom.a\"}", "export function onInit(){ /* changed */ }", null, "zh-CN");

            Assert.Equal(12, h1.Length);
            Assert.Equal(h1, h1Repeat);
            Assert.NotEqual(h1, h2);
            Assert.NotEqual(h1, h3);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioGalleryThumbnailCache_PruneAndCleanup_EvictsLegacyV1AndOldestBeyondLruCap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox.GalleryCachePruneTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string legacyV1 = Path.Combine(tempDir, "todo-agenda__3.0.0__dark__zh-CN.png");
            string v2Old = Path.Combine(tempDir, "v2__tpl__old__1.0.0__dark__zh-CN__mica.png");
            string v2Mid = Path.Combine(tempDir, "v2__tpl__mid__1.0.0__dark__zh-CN__mica.png");
            string v2New = Path.Combine(tempDir, "v2__tpl__new__1.0.0__dark__zh-CN__mica.png");
            string usrOld = Path.Combine(tempDir, "v2__usr__widget-1__aaaaaaaaaaaa__dark__zh-CN__mica.png");
            string usrKeep = Path.Combine(tempDir, "v2__usr__widget-1__bbbbbbbbbbbb__dark__zh-CN__mica.png");
            string usrOther = Path.Combine(tempDir, "v2__usr__widget-2__cccccccccccc__dark__zh-CN__mica.png");

            foreach (string file in new[] { legacyV1, v2Old, v2Mid, v2New, usrOld, usrKeep, usrOther })
            {
                File.WriteAllBytes(file, new byte[64]);
            }

            StudioGalleryThumbnailCache.DeleteStaleUserWidgetThumbs("widget-1", usrKeep, tempDir);
            Assert.False(File.Exists(usrOld));
            Assert.True(File.Exists(usrKeep));
            Assert.True(File.Exists(usrOther));

            StudioGalleryThumbnailCache.DeleteAllUserWidgetThumbs("widget-2", tempDir);
            Assert.False(File.Exists(usrOther));
            Assert.True(File.Exists(usrKeep));

            DateTime baseUtc = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(v2Old, baseUtc);
            File.SetLastWriteTimeUtc(v2Mid, baseUtc.AddMinutes(1));
            File.SetLastWriteTimeUtc(v2New, baseUtc.AddMinutes(2));
            File.SetLastWriteTimeUtc(usrKeep, baseUtc.AddMinutes(3));

            int pruned = StudioGalleryThumbnailCache.PruneCacheDirectory(tempDir, maxFiles: 2, maxTotalBytes: 1024 * 1024);
            Assert.Equal(3, pruned); // legacyV1 + v2Old + v2Mid
            Assert.False(File.Exists(legacyV1));
            Assert.False(File.Exists(v2Old));
            Assert.False(File.Exists(v2Mid));
            Assert.True(File.Exists(v2New));
            Assert.True(File.Exists(usrKeep));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioGalleryThumbnailCache_RowVirtualizationSlicing_CoversAllItemsContiguouslyWithoutIntermediateGaps()
    {
        foreach (int cols in new[] { 2, 3 })
        {
            foreach (int total in new[] { 0, 1, 2, 3, 14, 50, 101 })
            {
                int rowCount = StudioGalleryThumbnailCache.ComputeRowCount(total, cols);
                if (total == 0)
                {
                    Assert.Equal(0, rowCount);
                    Assert.Equal((0, 0), StudioGalleryThumbnailCache.ResolveRowSlice(0, cols, 0));
                    continue;
                }

                var seen = new List<int>(total);
                for (int r = 0; r < rowCount; r++)
                {
                    (int start, int count) = StudioGalleryThumbnailCache.ResolveRowSlice(total, cols, r);
                    Assert.Equal(r * cols, start);
                    if (r < rowCount - 1)
                    {
                        Assert.Equal(cols, count);
                    }
                    else
                    {
                        Assert.InRange(count, 1, cols);
                    }

                    for (int c = 0; c < count; c++)
                    {
                        seen.Add(start + c);
                    }
                }

                Assert.Equal(Enumerable.Range(0, total).ToArray(), seen);
                Assert.Equal((rowCount * cols, 0), StudioGalleryThumbnailCache.ResolveRowSlice(total, cols, rowCount));
            }
        }
    }

    private sealed class LoopbackTestHostBridge : IStudioHostBridge
    {
        private readonly SettingsService _settings;
        private readonly Action<CustomWidgetSettingsMutation> _onMutation;
        private readonly Func<string, string> _onCommitStage;
        private readonly Action<string> _onDiscardStage;

        public LoopbackTestHostBridge(
            SettingsService settings,
            Action<CustomWidgetSettingsMutation> onMutation,
            Func<string, string> onCommitStage,
            Action<string> onDiscardStage)
        {
            _settings = settings;
            _onMutation = onMutation;
            _onCommitStage = onCommitStage;
            _onDiscardStage = onDiscardStage;
        }

        public CustomWidgetLocaleCatalog? LastLocales { get; private set; }

        public event Action? DisplayTopologyChanged { add { } remove { } }
        public event Action? AppearanceChanged { add { } remove { } }
        public bool HasWidgetManager => true;
        public Windows.UI.Color GetEffectiveAccentColor() => default;
        public (int Width, int Height) GetDefaultWidgetSize() => (300, 200);
        public StudioShellVisualSettings GetShellVisualSettings() => NullStudioHostBridge.DefaultShellVisualSettings;
        public WidgetChromeMode ResolvePreviewWidgetChromeMode(CustomWidgetManifest? manifest, string? existingWidgetId, LocalizationService localization) => WidgetChromeMode.Standard;
        public WidgetBorderVisuals ResolvePreviewBorderVisuals(bool isDark, Windows.UI.Color accentColor, string? existingWidgetId) => default!;
        public IReadOnlyList<GlanceWidgetInstanceInfo> GetCustomWidgetInstances() => [];
        public WidgetConfig? ResolveCustomWidgetConfig(string widgetId) => null;
        public void BringAuxiliaryWindowToFront(IntPtr hwnd, string reason) { }
        public void ReleaseRaisedBandGuest(IntPtr hwnd, string reason) { }

        public Task<GlanceWidgetInstanceInfo?> CreateCustomWidgetAsync(
            string? templateOrPackageSpec,
            string? logicJsOverride = null,
            bool hasLogicJsOverride = false,
            CustomWidgetLocaleCatalog? localesOverride = null,
            bool hasLocalesOverride = false,
            IReadOnlyDictionary<string, string>? initialSettings = null,
            IReadOnlyDictionary<string, string>? initialSecrets = null)
        {
            LastLocales = localesOverride;
            var cfg = new WidgetConfig
            {
                Id = "custom.created-1",
                Name = "Created Widget",
                WidgetKind = WidgetKind.Custom
            };
            _settings.Settings.WidgetLayout.Widgets.Add(cfg);
            return Task.FromResult<GlanceWidgetInstanceInfo?>(
                new GlanceWidgetInstanceInfo(cfg.Id, cfg.Name, IsEnabled: true));
        }

        public Task<GlanceWidgetInstanceInfo?> UpdateCustomWidgetManifestAsync(
            string widgetId,
            string manifestJson,
            bool resizeToDefault = false,
            bool reveal = false,
            string? logicJs = null,
            bool updateLogicJs = false,
            CustomWidgetLocaleCatalog? locales = null,
            bool updateLocales = false)
        {
            WidgetConfig? cfg = _settings.Settings.WidgetLayout.Widgets.FirstOrDefault(w => w.Id == widgetId);
            if (cfg is null)
            {
                return Task.FromResult<GlanceWidgetInstanceInfo?>(null);
            }

            cfg.Name = "Renamed";
            return Task.FromResult<GlanceWidgetInstanceInfo?>(
                new GlanceWidgetInstanceInfo(cfg.Id, cfg.Name, !cfg.IsDisabled));
        }

        public Task ApplyCustomWidgetMutationAsync(CustomWidgetSettingsMutation mutation)
        {
            _onMutation(mutation);
            return Task.CompletedTask;
        }

        public Task SetCustomWidgetInstanceEnabledAsync(string widgetId, bool enabled)
        {
            WidgetConfig? cfg = _settings.Settings.WidgetLayout.Widgets.FirstOrDefault(w => w.Id == widgetId);
            if (cfg is not null)
            {
                cfg.IsDisabled = !enabled;
            }

            return Task.CompletedTask;
        }

        public Task LocateCustomWidgetAsync(string widgetId) => Task.CompletedTask;

        public Task RemoveCustomWidgetAsync(string widgetId)
        {
            _settings.Settings.WidgetLayout.Widgets.RemoveAll(w => w.Id == widgetId);
            if (!_settings.Settings.DeletedWidgetIds.Contains(widgetId))
            {
                _settings.Settings.DeletedWidgetIds.Add(widgetId);
            }

            return Task.CompletedTask;
        }

        public void ShowNotification(string title, string body) { }
        public IReadOnlyList<AgentStageSummary> GetPendingAgentStages() => [];
        public bool TryReadAgentStage(string stageId, out AgentStageBundle? bundle, out string error)
        {
            bundle = null;
            error = string.Empty;
            return false;
        }

        public Task<string> CommitAgentStageAsync(string stageId) => Task.FromResult(_onCommitStage(stageId));

        public Task DiscardAgentStageAsync(string stageId)
        {
            _onDiscardStage(stageId);
            return Task.CompletedTask;
        }

        public void Log(string message) { }
        public void LogVerbose(string message) { }
    }
}
