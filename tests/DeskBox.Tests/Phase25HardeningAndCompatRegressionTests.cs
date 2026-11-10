using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class Phase25HardeningAndCompatRegressionTests
{
    private static readonly byte[] MinimalValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    [Fact]
    public void IsBlockedIpAddress_BlocksIpv6TransitionPrefixes_AndAllowsPublicIpv6()
    {
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("64:ff9b::7f00:1")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("64:ff9b::a9fe:a9fe")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("64:ff9b:1::1")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("2002:7f00:0001::")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("2002:0a00:0001::")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("2001:0000:4136:e378:8000:63bf:3fff:fdd2")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("::127.0.0.1")));

        Assert.False(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("2606:4700:4700::1111")));
        Assert.False(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("64:ff9b::0101:0101")));
    }

    [Fact]
    public async Task FetchImageBytesAsync_ValidatesRasterMagicBytes_EnforcesConsent_AndCachesOffline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-img-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool networkOnline = true;
            var handler = new StubHttpMessageHandler(req =>
            {
                if (!networkOnline)
                {
                    throw new HttpRequestException("Simulated offline.");
                }

                string url = req.RequestUri!.AbsoluteUri;
                if (url == "https://cdn.example.com/fake.png")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<svg><script>alert(1)</script></svg>"))
                    };
                }

                if (url == "https://cdn.example.com/redirect.png")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://tracker.other.example/pixel.png");
                    return redirect;
                }

                if (url == "https://tracker.other.example/pixel.png" ||
                    url == "https://cdn.example.com/valid.png")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(MinimalValidPngBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var engine = new CustomWidgetNetworkSourceEngine(
                cacheDirectory: tempDir,
                httpMessageHandler: handler,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            // 1. Rejects SVG/HTML masquerading as raster image
            byte[]? fakeBytes = await engine.FetchImageBytesAsync("https://cdn.example.com/fake.png");
            Assert.Null(fakeBytes);

            // 2. Blocks cross-origin redirect when second host is denied
            var visitedHosts = new List<string>();
            byte[]? redirectedDenied = await engine.FetchImageBytesAsync(
                "https://cdn.example.com/redirect.png",
                hostApprover: (uri, _) =>
                {
                    visitedHosts.Add(uri.DnsSafeHost);
                    return Task.FromResult(string.Equals(uri.DnsSafeHost, "cdn.example.com", StringComparison.OrdinalIgnoreCase));
                });
            Assert.Null(redirectedDenied);
            Assert.Equal(["cdn.example.com", "tracker.other.example"], visitedHosts);

            // 3. Valid PNG caches to disk and serves offline when approved
            byte[]? validOnline = await engine.FetchImageBytesAsync(
                "https://cdn.example.com/valid.png",
                hostApprover: (_, _) => Task.FromResult(true));
            Assert.NotNull(validOnline);
            Assert.Equal(MinimalValidPngBytes, validOnline);

            networkOnline = false;
            byte[]? validOffline = await engine.FetchImageBytesAsync(
                "https://cdn.example.com/valid.png",
                hostApprover: (_, _) => Task.FromResult(true));
            Assert.NotNull(validOffline);
            Assert.Equal(MinimalValidPngBytes, validOffline);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FetchAsync_ReChecksCommunityConsentOnCrossOriginRedirect()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-src-redir-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StubHttpMessageHandler(req =>
            {
                if (req.RequestUri!.AbsoluteUri == "https://api.first.example/feed.json")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    redirect.Headers.Location = new Uri("https://api.second.example/feed.json");
                    return redirect;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[{"title":"ok"}]}""", Encoding.UTF8, "application/json")
                };
            });

            var engine = new CustomWidgetNetworkSourceEngine(
                cacheDirectory: tempDir,
                httpMessageHandler: handler,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            var spec = new CustomWidgetSourceSpec(
                Type: "httpJson",
                IntervalMs: 300_000,
                Id: "feed",
                Url: "https://api.first.example/feed.json",
                TargetCollection: "items",
                ItemsPath: "$.items",
                FieldMap: new Dictionary<string, string> { ["title"] = "title" });

            string? redirectedHost = null;
            CustomWidgetNetworkFetchOutcome deniedOutcome = await engine.FetchAsync(
                spec,
                spec.Url!,
                crossOriginHostApprover: (uri, _) =>
                {
                    redirectedHost = uri.DnsSafeHost;
                    return Task.FromResult(false);
                });

            Assert.False(deniedOutcome.Succeeded);
            Assert.Equal("api.second.example", redirectedHost);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveInstallWidgetBundleAsync_PreservesOfficialBundleAndRemoteZipCommunityOrigin()
    {
        // 1. Official template protocol install preserves locales + logic.js + official origin
        Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(
            $"deskbox://install-widget?template={Uri.EscapeDataString(CustomWidgetTemplates.PomodoroTemplateId)}",
            out DeskBoxProtocolIntent? tplIntent,
            out _));
        Assert.NotNull(tplIntent);

        DeskBoxProtocolResolvedBundle officialBundle =
            await DeskBoxProtocolActivationService.ResolveInstallWidgetBundleAsync(tplIntent!);
        Assert.Null(officialBundle.ErrorMessage);
        Assert.NotNull(officialBundle.ManifestJson);
        Assert.Equal(CustomWidgetOriginKinds.Official, officialBundle.OriginKind);
        Assert.NotNull(officialBundle.Locales);
        Assert.True(officialBundle.Locales!.HasAnyLocale);
        Assert.DoesNotContain("${$i18n", officialBundle.DisplayLabel ?? string.Empty, StringComparison.Ordinal);

        // 2. Remote .deskbox-widget ZIP URL preserves logic.js + locales + stamps community origin
        byte[] zipBytes = BuildWidgetZipBytes(
            manifestJson: """
            {
              "schemaVersion": 1,
              "id": "remote-zip-demo",
              "name": "${$i18n.meta.name}",
              "size": { "default": "2x2" },
              "script": { "entry": "logic.js", "exports": ["tick"] },
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$i18n.meta.name}" },
                  { "type": "Button", "label": "Run", "onClick": [{ "type": "script.run", "handler": "tick" }] }
                ]
              }
            }
            """,
            logicJs: "export function tick(ctx) { ctx.state.n = '1'; }",
            locales: new Dictionary<string, string>
            {
                ["en-US"] = """{"meta.name":"Remote Zip Counter"}""",
                ["zh-CN"] = """{"meta.name":"远程压缩包计数器"}"""
            });

        var remoteHandler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
        });

        Assert.False(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://install-widget?url=https%3A%2F%2Fexample.com%2Fremote-zip-demo.deskbox-widget",
            out _,
            out _));

        Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://install-widget?url=https%3A%2F%2Fraw.githubusercontent.com%2FTianyu199509%2FDeskBox%2Fmain%2Fremote-zip-demo.deskbox-widget",
            out DeskBoxProtocolIntent? urlIntent,
            out _));
        Assert.NotNull(urlIntent);

        DeskBoxProtocolResolvedBundle communityBundle =
            await DeskBoxProtocolActivationService.ResolveInstallWidgetBundleAsync(urlIntent!, remoteHandler);
        Assert.Null(communityBundle.ErrorMessage);
        Assert.Equal(CustomWidgetOriginKinds.Community, communityBundle.OriginKind);
        Assert.Contains("function tick", communityBundle.LogicJs ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(communityBundle.Locales);
        Assert.True(communityBundle.Locales!.Locales.ContainsKey("zh-CN"));
    }

    [Fact]
    public async Task LegacyInlineBundleMigration_RescuesInlineI18nAndScript_WhileDirectParserRemainsStrict()
    {
        const string legacyManifestJson = """
        {
          "schemaVersion": 1,
          "id": "legacy-inline-widget",
          "name": "${$i18n.title}",
          "size": { "default": "2x1" },
          "i18n": {
            "en-US": { "title": "Legacy Hello" },
            "zh-CN": { "title": "旧版你好" }
          },
          "script": {
            "code": "export function ping(ctx) { ctx.state.ok = '1'; }"
          },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$i18n.title}" },
              { "type": "Button", "label": "Go", "onClick": [{ "type": "script.run", "handler": "ping" }] }
            ]
          }
        }
        """;

        // Direct strict parser contract remains intact
        CustomWidgetParseResult strictParse = CustomWidgetManifestParser.Parse(legacyManifestJson);
        Assert.Contains(strictParse.Diagnostics, d => d.IsError && d.Code == "InlineI18nDisallowed");

        // Package ingestion rescues inline i18n + inline script.code cleanly
        CustomWidgetImportedBundle imported = await CustomWidgetPackageService.ReadPackageBundleFromBytesAsync(
            Encoding.UTF8.GetBytes(legacyManifestJson));
        Assert.Equal("legacy-inline-widget", imported.Manifest.Id);
        Assert.Contains("function ping", imported.LogicJs ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(imported.Locales);
        Assert.Equal("旧版你好", imported.Manifest.ResolveLocalizedName("zh-CN"));
    }

    [Fact]
    public async Task ReadPackageBundleFromBytesAsync_RejectsMissingLogicJs_AndMissingLocaleCatalog()
    {
        byte[] missingLogicZip = BuildWidgetZipBytes(
            manifestJson: """
            {
              "schemaVersion": 1,
              "id": "broken-no-js",
              "name": "Broken JS",
              "size": { "default": "2x2" },
              "script": { "entry": "logic.js", "exports": ["runNow"] },
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Button", "label": "Run", "onClick": [{ "type": "script.run", "handler": "runNow" }] }
                ]
              }
            }
            """,
            logicJs: null,
            locales: null);

        InvalidDataException jsEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => CustomWidgetPackageService.ReadPackageBundleFromBytesAsync(missingLogicZip));
        Assert.Contains("logic.js", jsEx.Message, StringComparison.OrdinalIgnoreCase);

        byte[] missingLocalesZip = BuildWidgetZipBytes(
            manifestJson: """
            {
              "schemaVersion": 1,
              "id": "broken-no-locales",
              "name": "${$i18n.meta.name}",
              "size": { "default": "2x1" },
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$i18n.meta.name}" }
                ]
              }
            }
            """,
            logicJs: null,
            locales: null);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CustomWidgetPackageService.ReadPackageBundleFromBytesAsync(missingLocalesZip));
    }

    [Fact]
    public async Task OnDrop_CopyToWidgetLegacyFlag_AndStorageAuto_ResolveToCopyMode()
    {
        const string copyToWidgetJson = """
        {
          "schemaVersion": 1,
          "id": "drop-legacy",
          "name": "Drop Legacy",
          "size": { "default": "2x2" },
          "onDrop": {
            "copyToWidget": true,
            "actions": [{ "type": "collection.append", "collection": "files", "item": { "path": "${$drop.path}" } }]
          },
          "view": { "type": "Stack", "children": [{ "type": "Text", "text": "Drop" }] }
        }
        """;

        const string autoStorageJson = """
        {
          "schemaVersion": 1,
          "id": "drop-auto",
          "name": "Drop Auto",
          "size": { "default": "2x2" },
          "onDrop": {
            "storage": "auto",
            "actions": [{ "type": "collection.append", "collection": "files", "item": { "path": "${$drop.path}" } }]
          },
          "view": { "type": "Stack", "children": [{ "type": "Text", "text": "Drop" }] }
        }
        """;

        CustomWidgetParseResult parsedLegacy = CustomWidgetManifestParser.Parse(copyToWidgetJson);
        CustomWidgetParseResult parsedAuto = CustomWidgetManifestParser.Parse(autoStorageJson);
        Assert.Equal("copy", parsedLegacy.Manifest?.OnDrop?.Mode);
        Assert.Equal("auto", parsedAuto.Manifest?.OnDrop?.Mode);

        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-drop-auto-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);

            using var vmLegacy = new CustomWidgetViewModel(
                new WidgetConfig { Id = "__studio_drop_legacy_" + Guid.NewGuid().ToString("N"), WidgetKind = WidgetKind.Custom },
                localization,
                isPreviewOnly: true);
            vmLegacy.ApplyPreviewManifestJson(copyToWidgetJson);
            Assert.Equal("copy", vmLegacy.EffectiveDropMode);

            using var vmAuto = new CustomWidgetViewModel(
                new WidgetConfig { Id = "__studio_drop_auto_" + Guid.NewGuid().ToString("N"), WidgetKind = WidgetKind.Custom },
                localization,
                isPreviewOnly: true);
            vmAuto.ApplyPreviewManifestJson(autoStorageJson);
            Assert.Equal("copy", vmAuto.EffectiveDropMode);

            string srcFile = Path.Combine(tempRoot, "note.txt");
            await File.WriteAllTextAsync(srcFile, "hello");

            string storeDir = Path.Combine(tempRoot, "widget-store");
            var store = new CustomWidgetDataStore("drop-auto", storeDir);
            var resolved = await store.ResolveDroppedFileWithPortablePathAsync(srcFile, "auto");
            Assert.Equal("copy", resolved.Mode);
            Assert.StartsWith("attachments/", resolved.StoredPath, StringComparison.Ordinal);
            Assert.True(File.Exists(resolved.FullPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PreviewOnlyViewModel_CreatesZeroDirectory_AndDenialCooldownSuppressesPromptStorm()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-preview-hygiene-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);

            string previewId = "__studio_template_zero_dir_" + Guid.NewGuid().ToString("N");
            string expectedWidgetDir = new CustomWidgetDataStore(previewId, createDirectory: false).WidgetDirectory;
            Assert.False(Directory.Exists(expectedWidgetDir));

            using (var previewVm = new CustomWidgetViewModel(
                       new WidgetConfig { Id = previewId, WidgetKind = WidgetKind.Custom },
                       localization,
                       store: null,
                       isPreviewOnly: true))
            {
                Assert.False(Directory.Exists(expectedWidgetDir));
            }

            Assert.False(Directory.Exists(expectedWidgetDir));

            // Verify community external consent prompt cooldown on background polling after denial
            const string communityManifestJson = """
            {
              "schemaVersion": 1,
              "id": "community-net",
              "name": "Community Net",
              "origin": "community",
              "size": { "default": "2x2" },
              "view": { "type": "Stack", "children": [{ "type": "Text", "text": "Hi" }] }
            }
            """;

            string tempStoreDir = Path.Combine(tempRoot, "community-store");
            var store = new CustomWidgetDataStore("community-net", tempStoreDir);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "community-net", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                isPreviewOnly: false);
            vm.ApplyPreviewManifestJson(communityManifestJson);

            int promptCount = 0;
            vm.ExternalActionConsentPrompt = _ =>
            {
                Interlocked.Increment(ref promptCount);
                return Task.FromResult(false);
            };

            bool firstBg = await vm.EnsureCommunityExternalTargetApprovedAsync(
                "network.fetch",
                "host:api.example.com",
                "https://api.example.com/data",
                respectDenialCooldown: true);
            bool secondBg = await vm.EnsureCommunityExternalTargetApprovedAsync(
                "network.fetch",
                "host:api.example.com",
                "https://api.example.com/data",
                respectDenialCooldown: true);
            Assert.False(firstBg);
            Assert.False(secondBg);
            Assert.Equal(1, promptCount);

            // Explicit interactive user click bypasses background cooldown and re-prompts
            bool explicitClick = await vm.EnsureCommunityExternalTargetApprovedAsync(
                "url.open",
                "host:api.example.com",
                "https://api.example.com/data",
                respectDenialCooldown: false);
            Assert.False(explicitClick);
            Assert.Equal(2, promptCount);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }

        // Verify wake-resilient midnight delay caps long waits at 1 minute while preserving exact midnight helper
        var midday = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.True(CustomWidgetViewModel.ComputeCountdownNextMidnightDelay(midday) > TimeSpan.FromHours(11));
        Assert.Equal(TimeSpan.FromMinutes(1), CustomWidgetViewModel.ComputeWakeResilientMidnightDelay(midday));
    }

    [Fact]
    public void StudioTurnAndAiHardening_AppliesManifestPatch_PreservesSameTurnTranslations_AndExtractsRelayErrors()
    {
        const string baseManifest = """
        {
          "schemaVersion": 1,
          "id": "water-tracker",
          "name": "Water Tracker",
          "size": { "default": "2x2" },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "Hello" }
            ]
          }
        }
        """;

        // 1. Pasted manifestPatch + same-turn en-US & zh-CN locales preserved without pruning zh-CN as stale
        const string assistantPatchTurn = """
        {
          "reply": "Updated title with bilingual labels.",
          "manifestPatch": {
            "name": "${$i18n.meta.name}",
            "view": {
              "type": "Stack",
              "children": [
                { "type": "Text", "text": "${$i18n.greeting}" }
              ]
            }
          },
          "locales": {
            "en-US": {
              "meta.name": "Hydration Goal",
              "greeting": "Stay hydrated!"
            },
            "zh-CN": {
              "meta.name": "喝水目标",
              "greeting": "记得喝水！"
            }
          }
        }
        """;

        StudioTurnResult turn =
            CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
                assistantPatchTurn,
                currentManifestJson: baseManifest);

        Assert.NotNull(turn.ManifestJson);
        Assert.NotNull(turn.Locales);
        Assert.True(turn.Locales!.Locales.TryGetValue("zh-CN", out var zhMap));
        Assert.Equal("喝水目标", zhMap!["meta.name"]);
        Assert.Equal("记得喝水！", zhMap["greeting"]);

        // 2. Endpoint normalization, reasoning model flags & HTTP 200 relay error extraction
        Assert.Equal(
            "https://api.anthropic.com/v1/chat/completions",
            CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint("https://api.anthropic.com/v1/messages"));
        Assert.False(CustomWidgetAiGeneratorService.IsReasoningOnlyModel("gpt-5-chat-latest"));
        Assert.True(CustomWidgetAiGeneratorService.IsReasoningOnlyModel("gpt-5"));
        Assert.False(DeskBoxAiService.SupportsLowReasoningEffort("openai/o1-mini"));
        Assert.False(DeskBoxAiService.SupportsLowReasoningEffort("gpt-5-pro"));
        Assert.True(DeskBoxAiService.SupportsLowReasoningEffort("o3-mini"));

        Assert.Equal(
            "Insufficient quota on relay",
            DeskBoxAiService.TryExtractStructuredJsonErrorMessage("""{"error":{"message":"Insufficient quota on relay"}}"""));
    }

    [Fact]
    public async Task McpStdioServer_HandlesBatchRequests_InvalidJsonObjects_AndProtocolNegotiation()
    {
        // 1. Non-object JSON returns -32600 Invalid Request instead of crashing
        string? scalarResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync("\"not-an-object\"");
        Assert.NotNull(scalarResp);
        using (JsonDocument scalarDoc = JsonDocument.Parse(scalarResp!))
        {
            Assert.Equal(-32600, scalarDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        string? emptyArrayResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync("[]");
        Assert.NotNull(emptyArrayResp);
        using (JsonDocument emptyDoc = JsonDocument.Parse(emptyArrayResp!))
        {
            Assert.Equal(-32600, emptyDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        // 2. Batch array processes each item and negotiates unknown protocolVersion to 2025-06-18
        const string batchReq = """
        [
          { "jsonrpc": "2.0", "id": 1, "method": "ping" },
          { "jsonrpc": "2.0", "id": 2, "method": "initialize", "params": { "protocolVersion": "2099-99-99" } }
        ]
        """;
        string? batchResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(batchReq);
        Assert.NotNull(batchResp);
        using JsonDocument batchDoc = JsonDocument.Parse(batchResp!);
        Assert.Equal(JsonValueKind.Array, batchDoc.RootElement.ValueKind);
        Assert.Equal(2, batchDoc.RootElement.GetArrayLength());
        Assert.Equal(
            "2025-06-18",
            batchDoc.RootElement[1].GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Windows10GlyphFallbacks_MapToSegoeMdl2CompatibleCodepoints()
    {
        Assert.Equal("\uE753", CustomWidgetWeatherSource.ResolveWeatherGlyph(61, isDay: true, isWindows11OrLater: false));
        Assert.Equal("\uE9C4", CustomWidgetWeatherSource.ResolveWeatherGlyph(61, isDay: true, isWindows11OrLater: true));

        Assert.Equal("\uE753", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uE9C4", isWindows11OrLater: false));
        Assert.Equal("\uE73F", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uE91D", isWindows11OrLater: false));
        Assert.Equal("\uE82D", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uEAFD", isWindows11OrLater: false));
        Assert.Equal("\uE91D", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uE91D", isWindows11OrLater: true));
    }

    private static byte[] BuildWidgetZipBytes(
        string manifestJson,
        string? logicJs,
        IReadOnlyDictionary<string, string>? locales)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifestEntry = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(manifestJson);
            }

            if (logicJs is not null)
            {
                ZipArchiveEntry jsEntry = zip.CreateEntry("logic.js");
                using var writer = new StreamWriter(jsEntry.Open(), new UTF8Encoding(false));
                writer.Write(logicJs);
            }

            if (locales is not null)
            {
                foreach ((string culture, string json) in locales)
                {
                    ZipArchiveEntry locEntry = zip.CreateEntry($"locales/{culture}.json");
                    using var writer = new StreamWriter(locEntry.Open(), new UTF8Encoding(false));
                    writer.Write(json);
                }
            }
        }

        return ms.ToArray();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
