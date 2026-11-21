using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Controls.Studio;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StudioWorkspaceEngineAndStreamingTests
{
    private sealed class CapturingHttpHandler(Func<HttpRequestMessage, string?, int, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _callCount;
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _callCount);
            string? body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            if (body is not null)
            {
                RequestBodies.Add(body);
            }

            return responder(request, body, index);
        }
    }

    [Fact]
    public void ColdStart_RuntimeTokensAndHttpClient_DeferHeavyCapabilityAndSocketAllocation()
    {
        CustomWidgetCapabilityRegistry.ReleaseIdleCache(dropWeakReference: true);
        Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

        var okDiags = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression(
            "CPU ${$sys.cpu.percent}% · ${$time.time}",
            "#main",
            okDiags);
        Assert.Empty(okDiags);
        Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

        var badDiags = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression(
            "Broken ${$sys.nonexistentValue}",
            "#txt_0.text",
            badDiags);
        Assert.Single(badDiags);
        Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

        Assert.Same(
            CustomWidgetRuntimeTokens.ValidNamespaceTokens,
            CustomWidgetRuntimeTokens.ValidNamespaceTokens);

        Assert.NotEmpty(CustomWidgetCapabilityRegistry.All);
        Assert.True(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

        CustomWidgetCapabilityRegistry.ReleaseIdleCache(dropWeakReference: true);
        Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            using var injectedClient = new HttpClient(new CapturingHttpHandler((_, _, _) =>
                new HttpResponseMessage(HttpStatusCode.OK)));
            var svc = new DeskBoxAiService(credStore, prefsStore, injectedClient);
            Assert.Same(injectedClient, svc.HttpClient);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceEngine_EditManifest_SupportsUpsertDanglingCleanupRootPatchAndAtomicRollback()
    {
        const string initial = """
        {
          "schemaVersion": 1,
          "id": "custom.patch-test",
          "name": "Patch Test",
          "description": "Obsolete description",
          "capabilities": ["http-and-rss-feeds", "HTTP-AND-RSS-FEEDS"],
          "sources": [
            { "id": "weather", "type": "httpJson", "url": "https://example.com/w", "intervalSeconds": 300 }
          ],
          "root": "rootStack",
          "elements": {
            "rootStack": {
              "type": "Stack",
              "spacing": -4,
              "children": ["header", "bodyCard", "footer"]
            },
            "header": { "type": "Text", "text": "Header" },
            "bodyCard": {
              "type": "Card",
              "children": ["statusText"]
            },
            "statusText": { "type": "Text", "text": "Offline" },
            "footer": { "type": "Text", "text": "Footer" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(initial);
        StudioWorkspaceOpResult okRes = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["statusText"] = new JsonObject { ["type"] = "Text", ["text"] = "Online" },
                ["extraRow"] = new JsonObject { ["type"] = "Text", ["text"] = "Extra" },
                ["bodyCard"] = new JsonObject
                {
                    ["type"] = "Card",
                    ["spacing"] = 12,
                    ["padding"] = "8",
                    ["children"] = new JsonArray("statusText", "extraRow")
                },
                ["topBanner"] = new JsonObject { ["type"] = "Text", ["text"] = "Top" },
                ["gap"] = new JsonObject { ["type"] = "Spacer", ["size"] = 6 },
                ["sep"] = new JsonObject { ["type"] = "Divider" },
                ["header"] = new JsonObject { ["type"] = "Text", ["text"] = "New Header" },
                ["rootStack"] = new JsonObject
                {
                    ["type"] = "Stack",
                    ["spacing"] = -4,
                    ["children"] = new JsonArray("topBanner", "gap", "header", "bodyCard", "sep", "footer")
                }
            },
            RemoveElements: ["gap"],
            PatchRoot: new JsonObject
            {
                ["sources"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "weather",
                        ["type"] = "httpJson",
                        ["url"] = "https://example.com/w",
                        ["intervalSeconds"] = 600
                    }
                }
            },
            RemoveRootKeys: ["description", "schemaVersion", "id", "root", "elements"]));

        Assert.True(okRes.Succeeded, okRes.ErrorMessage);
        engine.NormalizeDomInPlace();

        string updatedJson = engine.GetManifestJson(indented: false)!;
        JsonObject root = JsonNode.Parse(updatedJson)!.AsObject();
        Assert.Single(root["capabilities"]!.AsArray());
        Assert.Null(root["description"]);
        Assert.Equal(1, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("custom.patch-test", root["id"]!.GetValue<string>());
        Assert.Equal("rootStack", root["root"]!.GetValue<string>());
        Assert.Equal(600, root["sources"]![0]!["intervalSeconds"]!.GetValue<int>());

        JsonObject elements = root["elements"]!.AsObject();
        Assert.Null(elements["gap"]);
        Assert.Equal(0, elements["rootStack"]!["spacing"]!.GetValue<int>());

        string[] rootChildren = elements["rootStack"]!["children"]!.AsArray()
            .Select(static n => n!.GetValue<string>())
            .ToArray();
        Assert.Equal(["topBanner", "header", "bodyCard", "sep", "footer"], rootChildren);
        Assert.Equal("New Header", elements["header"]!["text"]!.GetValue<string>());

        JsonObject bodyCard = elements["bodyCard"]!.AsObject();
        Assert.Equal(12, bodyCard["spacing"]!.GetValue<int>());
        Assert.Equal("Online", elements["statusText"]!["text"]!.GetValue<string>());
        Assert.Equal(["statusText", "extraRow"], bodyCard["children"]!.AsArray().Select(static n => n!.GetValue<string>()).ToArray());

        // Atomic rollback test: first upsert mutates header, second entry is invalid non-object -> whole batch rolls back
        StudioWorkspaceOpResult badRes = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["header"] = new JsonObject { ["type"] = "Text", ["text"] = "ShouldRollback" },
                ["invalidEntry"] = JsonValue.Create("NotAnElementObject")
            }));

        Assert.False(badRes.Succeeded);
        Assert.Contains("must be a JSON object", badRes.ErrorMessage, StringComparison.Ordinal);

        JsonObject afterRollback = JsonNode.Parse(engine.GetManifestJson(indented: false)!)!.AsObject();
        Assert.Equal("New Header", afterRollback["elements"]!["header"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void WorkspaceEngine_PatchScript_SupportsThreeTiers_AmbiguityRejection_AndMissAnchor()
    {
        const string initialJs = """
        export function onInit(ctx) {
          ctx.computed.title = "Ready";
          if (ctx.state.ready) {
            ctx.computed.badge = "OK";
            ctx.computed.color = "accent";
          }
        }

        function helperOne() {
          return 1;
        }

        function helperTwo() {
          return 1;
        }
        """;

        var engine = new StudioWorkspaceEngine("{\"schemaVersion\":1,\"id\":\"custom.js\",\"name\":\"JS\",\"root\":\"main\",\"elements\":{\"main\":{\"type\":\"Stack\",\"children\":[]}}}", initialJs);

        // Tier 1 exact + Tier 2 trimmed with L-prefixes + Tier 3 relative indent align
        StudioWorkspaceOpResult res = engine.ApplyScriptPatch(
        [
            new StudioScriptPatchHunk(
                "  ctx.computed.title = \"Ready\";",
                "  ctx.computed.title = \"Live\";"),
            new StudioScriptPatchHunk(
                "L3: if (ctx.state.ready) {\nL4: ctx.computed.badge = \"OK\";\nL5: ctx.computed.color = \"accent\";\nL6: }",
                "L3: if (ctx.state.ready) {\nL4:   ctx.computed.badge = \"ACTIVE\";\nL5:   ctx.computed.color = \"success\";\nL6: }")
        ]);
        Assert.True(res.Succeeded, res.ErrorMessage);
        Assert.Contains("ctx.computed.title = \"Live\";", engine.LogicJs, StringComparison.Ordinal);
        Assert.Contains("ctx.computed.badge = \"ACTIVE\";", engine.LogicJs, StringComparison.Ordinal);

        // Tier 3 zero-indented search matching 4-space indented lines preserves base indentation
        StudioWorkspaceOpResult tier3Res = engine.ApplyScriptPatch(
        [
            new StudioScriptPatchHunk(
                "ctx.computed.badge = \"ACTIVE\";\nctx.computed.color = \"success\";",
                "ctx.computed.badge = \"DONE\";\nctx.computed.color = \"neutral\";")
        ]);
        Assert.True(tier3Res.Succeeded, tier3Res.ErrorMessage);
        Assert.Contains("    ctx.computed.badge = \"DONE\";", engine.LogicJs, StringComparison.Ordinal);

        // Ambiguous match must be rejected with line numbers
        StudioWorkspaceOpResult dupRes = engine.ApplyScriptPatch(
        [
            new StudioScriptPatchHunk("  return 1;", "  return 2;")
        ]);
        Assert.False(dupRes.Succeeded);
        Assert.Contains("locations", dupRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("L", dupRes.ErrorMessage, StringComparison.Ordinal);

        // Missing hunk emits +/- 12 anchor snippet
        StudioWorkspaceOpResult missRes = engine.ApplyScriptPatch(
        [
            new StudioScriptPatchHunk("function helperOne() {\n  return 999;\n}", "function helperOne() { return 2; }")
        ]);
        Assert.False(missRes.Succeeded);
        Assert.Contains("Nearest lines:", missRes.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceEngine_ContextFolding_And_ReadWorkspace_CleanSelectorsAndExpandSubtree()
    {
        var rowIds = new JsonArray();
        var largeElements = new JsonObject();
        for (int i = 0; i < 65; i++)
        {
            rowIds.Add($"row_{i}");
            largeElements[$"row_{i}"] = new JsonObject
            {
                ["type"] = "Stack",
                ["orientation"] = "horizontal",
                ["children"] = new JsonArray($"lbl_{i}", $"val_{i}")
            };
            largeElements[$"lbl_{i}"] = new JsonObject { ["type"] = "Text", ["text"] = $"Metric Item {i}" };
            largeElements[$"val_{i}"] = new JsonObject { ["type"] = "Text", ["text"] = $"${{$state.m_{i}}}" };
        }
        largeElements["main"] = new JsonObject
        {
            ["type"] = "Stack",
            ["children"] = rowIds
        };

        var largeRoot = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "custom.large-dashboard",
            ["name"] = "Large Dashboard",
            ["root"] = "main",
            ["elements"] = largeElements
        };

        var mediumRowIds = new JsonArray();
        var mediumElements = new JsonObject();
        for (int i = 0; i < 20; i++)
        {
            mediumRowIds.Add($"med_{i}");
            mediumElements[$"med_{i}"] = new JsonObject
            {
                ["type"] = "Stack",
                ["children"] = new JsonArray($"m_lbl_{i}")
            };
            mediumElements[$"m_lbl_{i}"] = new JsonObject { ["type"] = "Text", ["text"] = $"Item {i}" };
        }
        mediumElements["main"] = new JsonObject
        {
            ["type"] = "Stack",
            ["children"] = mediumRowIds
        };
        var mediumRoot = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "custom.medium-card",
            ["name"] = "Medium Card",
            ["root"] = "main",
            ["elements"] = mediumElements
        };
        var mediumScriptSb = new StringBuilder();
        for (int i = 0; i < 25; i++)
        {
            mediumScriptSb.AppendLine($"export function med_{i}(ctx) {{");
            mediumScriptSb.AppendLine($"  ctx.computed.v_{i} = String({i});");
            mediumScriptSb.AppendLine("}");
            mediumScriptSb.AppendLine();
        }
        Assert.False(StudioWorkspaceEngine.ShouldFoldManifest(mediumRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Assert.False(StudioWorkspaceEngine.ShouldFoldScript(mediumScriptSb.ToString()));

        var scriptSb = new StringBuilder();
        for (int i = 0; i < 65; i++)
        {
            scriptSb.AppendLine($"export function handler_{i}(ctx) {{");
            scriptSb.AppendLine($"  const v = ctx.state.m_{i} || 0;");
            scriptSb.AppendLine($"  ctx.state.m_{i} = v + 1;");
            scriptSb.AppendLine($"  return v;");
            scriptSb.AppendLine("}");
            scriptSb.AppendLine();
        }

        var engine = new StudioWorkspaceEngine(largeRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), scriptSb.ToString());
        string bigManifest = engine.GetManifestJson(indented: true)!;

        Assert.True(StudioWorkspaceEngine.ShouldFoldManifest(bigManifest));
        Assert.True(StudioWorkspaceEngine.ShouldFoldScript(engine.LogicJs));
        Assert.Equal("#lbl_42", StudioWorkspaceEngine.ExtractCleanNodeSelector("#lbl_42 (linked script bindings: handler_42, $state.m_42)"));

        string foldedSection = StudioWorkspaceEngine.BuildManifestPromptSection(
            bigManifest,
            isChinese: false,
            targetNodePath: "#lbl_42 (linked script bindings: handler_42, $state.m_42)");
        Assert.Contains("## Current `manifest.json`", foldedSection, StringComparison.Ordinal);
        Assert.Contains("Auto-Expanded Target Node Subtree", foldedSection, StringComparison.Ordinal);
        Assert.Contains("Metric Item 42", foldedSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Metric Item 55", foldedSection, StringComparison.Ordinal);

        string nodeRead = engine.ReadWorkspace(new StudioReadWorkspaceRequest("node", "#lbl_55 (linked script bindings: handler_55)", MaxDepth: 2));
        Assert.Contains("Metric Item 55", nodeRead, StringComparison.Ordinal);

        string symbolRead = engine.ReadWorkspace(new StudioReadWorkspaceRequest("script", Symbol: "handler_15"));
        Assert.Contains("export function handler_15(ctx)", symbolRead, StringComparison.Ordinal);
        Assert.DoesNotContain("export function handler_1(", symbolRead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStudioTurnAsync_NativeTools_OneRttFastExit_LocalizedRepair_And_Http400Fallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-tools", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            // Test A: 1-RTT invalid -> localized repair -> 1-RTT valid edit_manifest + finish_turn
            const string brokenManifestJson = """{"schemaVersion":1,"id":"custom.tool-demo","name":"Tool Demo","root":"main","elements":{"main":{"type":"Stack","children":["bad"]},"bad":{"type":"UnknownBadType","text":"Hi"}}}""";
            string writeBundleArgs1 = JsonSerializer.Serialize(new Dictionary<string, object?> { ["manifestJson"] = brokenManifestJson });
            string finishArgs1 = """{"kind":"edit","reply":"Created widget.","changeSummary":"Create tool demo"}""";

            string sseCall1 = BuildToolCallsSse(
            [
                ("call_wb", "write_bundle", writeBundleArgs1),
                ("call_fin1", "finish_turn", finishArgs1)
            ]);

            string editManifestArgs2 = """{"upsertElements":{"bad":{"type":"Text","text":"Fixed"}}}""";
            string finishArgs2 = """{"kind":"edit","reply":"Fixed invalid component.","changeSummary":"Fix text component"}""";
            string sseCall2 = BuildToolCallsSse(
            [
                ("call_em", "edit_manifest", editManifestArgs2),
                ("call_fin2", "finish_turn", finishArgs2)
            ]);

            var handlerA = new CapturingHttpHandler((_, _, idx) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(idx == 1 ? sseCall1 : sseCall2, Encoding.UTF8, "text/event-stream")
            });

            var aiA = new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerA));
            var genA = new CustomWidgetAiGeneratorService(aiA, diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));

            StudioTurnResult resA = await genA.RunStudioTurnAsync(
                "deepseek",
                "Build a demo card",
                uiLanguage: "en-US");

            Assert.True(resA.Success, resA.ErrorMessage);
            Assert.True(resA.WasAutoRepaired);
            Assert.Equal("Fixed", resA.ParseResult!.Manifest!.View.Children[0].Text);
            Assert.Equal(2, handlerA.RequestBodies.Count);
            Assert.Contains("\"tools\"", handlerA.RequestBodies[0], StringComparison.Ordinal);
            Assert.Contains("\"role\":\"tool\"", handlerA.RequestBodies[1], StringComparison.Ordinal);
            Assert.Contains("finish_turn REJECTED", handlerA.RequestBodies[1], StringComparison.Ordinal);

            // Test B: HTTP 400 with tools -> auto-retry Block Mode & mark route unsupported
            const string blockReplyJson = """
            ```deskbox-meta
            {"kind":"edit","reply":"Fallback block mode","changeSummary":"Block fallback"}
            ```
            ```deskbox-manifest
            {"schemaVersion":1,"id":"custom.block-fb","name":"Block FB","root":"main","elements":{"main":{"type":"Stack","children":["t0"]},"t0":{"type":"Text","text":"OK"}}}
            ```
            """;

            var handlerB = new CapturingHttpHandler((_, body, _) =>
            {
                if (body is not null && body.Contains("\"tools\"", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":{"message":"tools parameter unsupported"}}""", Encoding.UTF8, "application/json")
                    };
                }

                string sseBlock = $"data: {JsonSerializer.Serialize(new Dictionary<string, object?> { ["choices"] = new[] { new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?> { ["content"] = blockReplyJson }, ["finish_reason"] = "stop" } } })}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sseBlock, Encoding.UTF8, "text/event-stream")
                };
            });

            var aiB = new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerB));
            var genB = new CustomWidgetAiGeneratorService(aiB, diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));
            StudioTurnResult resB = await genB.RunStudioTurnAsync("deepseek", "Build simple text card", uiLanguage: "en-US");

            Assert.True(resB.Success, resB.ErrorMessage);
            Assert.Equal("Block FB", resB.ParseResult!.Manifest!.Name);
            ResolvedAiInvocation resolvedCheck = aiB.CoerceSafeStudioAuthoringInvocation(genB.ResolveEffectiveSettings("deepseek"));
            Assert.True(aiB.IsToolsUnsupportedRoute(resolvedCheck));

            // Subsequent turn on same route skips tools immediately (single HTTP call)
            StudioTurnResult resB2 = await genB.RunStudioTurnAsync("deepseek", "Another turn", uiLanguage: "en-US");
            Assert.True(resB2.Success, resB2.ErrorMessage);
            Assert.Equal(3, handlerB.RequestBodies.Count);
            Assert.DoesNotContain("\"tools\"", handlerB.RequestBodies[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunStudioTurnAsync_FinishReasonLength_ContinuesIncrementallyWithoutRestart()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-trunc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-len", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            const string salvagedManifest = """{"schemaVersion":1,"id":"custom.len-demo","name":"Len Demo","root":"main","elements":{"main":{"type":"Stack","children":["line1"]},"line1":{"type":"Text","text":"Half1"}}}""";
            string truncatedResponse = "```deskbox-manifest\n" + salvagedManifest + "\n```\n```deskbox-script\nexport function onInit(ctx) {\n  // cut off";

            string sseTrunc = BuildTextSse(truncatedResponse, "length");
            string sseContinue = BuildToolCallsSse(
            [
                ("call_em_c", "edit_manifest", """{"upsertElements":{"line1":{"type":"Text","text":"Complete"}}}"""),
                ("call_fin_c", "finish_turn", """{"kind":"edit","reply":"Completed after truncation.","changeSummary":"Complete edit"}""")
            ]);

            var handler = new CapturingHttpHandler((_, _, idx) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(idx == 1 ? sseTrunc : sseContinue, Encoding.UTF8, "text/event-stream")
            });

            var ai = new DeskBoxAiService(credStore, prefsStore, new HttpClient(handler));
            var gen = new CustomWidgetAiGeneratorService(ai, diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));

            StudioTurnResult result = await gen.RunStudioTurnAsync("deepseek", "Make card", uiLanguage: "en-US");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("Complete", result.ParseResult!.Manifest!.View.Children[0].Text);
            Assert.Equal(2, handler.RequestBodies.Count);
            Assert.Contains("finish_reason=length", handler.RequestBodies[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StreamingTurnAccumulator_BoundsTailsOver120Kb_SplitsCrossChunkThinkTags_AndLatchesName()
    {
        var acc = new StreamingTurnAccumulator();
        string fullInput = "<thinking>Chain of thought line.</thinking>{\"id\":\"custom.big\",\"name\":\"Clock Pro\",\"payload\":\"" +
                           new string('x', 135_000) + "\"}";

        // Feed in 3-char chunks to stress cross-boundary <thinking> / </thinking> tags
        for (int i = 0; i < fullInput.Length; i += 3)
        {
            int len = Math.Min(3, fullInput.Length - i);
            acc.AppendContentDelta(fullInput.AsSpan(i, len));
        }
        acc.FlushPendingTag();

        Assert.False(acc.IsInsideInlineThink);
        Assert.Equal("Chain of thought line.", acc.GetFullThinking());
        Assert.Equal("Clock Pro", acc.LatchedWidgetName);
        Assert.True(acc.TotalVisibleChars > 130_000);
        Assert.True(acc.GetThinkingTail().Length <= StreamingTurnAccumulator.MaxThinkingTailChars);
        Assert.True(acc.GetVisibleTail().Length <= StreamingTurnAccumulator.MaxVisibleTailChars);
        Assert.True(acc.GetVisibleTail().Length == StreamingTurnAccumulator.MaxVisibleTailChars);

        // Also test incremental streaming tool_calls assembly
        acc.AppendToolCallDelta(0, "tc_1", "edit_", "{\"upsert");
        acc.AppendToolCallDelta(0, null, "manifest", "Elements\":{}}");
        IReadOnlyList<AiToolCallSpec> calls = acc.BuildToolCalls();
        Assert.Single(calls);
        Assert.Equal("tc_1", calls[0].Id);
        Assert.Equal("edit_manifest", calls[0].Name);
        Assert.Equal("{\"upsertElements\":{}}", calls[0].ArgumentsJson);
    }

    [Fact]
    public void SessionStore_ScriptBlobDeduplication_And_ShareUnchangedArtifacts_ReuseReferences()
    {
        string sharedScript = "export function onInit(ctx) { ctx.computed.v = '1'; }\n" + new string('/', 512);
        string scriptCopy = new(sharedScript.ToCharArray());
        string manifest1 = """{"schemaVersion":1,"id":"custom.blob","name":"Blob","root":"main","elements":{"main":{"type":"Stack","children":[]}}}""";
        string manifestCopy = new(manifest1.ToCharArray());

        var rev1 = new StudioPersistedRevision
        {
            RevisionId = "rev-1",
            CreatedAtUtcTicks = 100,
            Source = "ai",
            ChangeSummary = "v1",
            ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(manifest1, sharedScript),
            IsCommitted = true,
            ManifestJson = manifest1,
            LogicJs = sharedScript
        };

        var rev2Raw = rev1 with
        {
            RevisionId = "rev-2",
            CreatedAtUtcTicks = 200,
            ManifestJson = manifestCopy,
            LogicJs = scriptCopy
        };

        StudioPersistedRevision rev2Shared = CustomWidgetStudioSessionStore.ShareUnchangedArtifacts(rev2Raw, rev1);
        Assert.Same(rev1.ManifestJson, rev2Shared.ManifestJson);
        Assert.Same(rev1.LogicJs, rev2Shared.LogicJs);

        string json = CustomWidgetStudioSessionStore.SerializeRevisionsJson([rev1, rev2Shared]);
        Assert.Contains("\"scriptBlobs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scriptRef\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"logicJs\"", json, StringComparison.Ordinal);

        List<StudioPersistedRevision> parsed = CustomWidgetStudioSessionStore.ParseRevisionsJson(json);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(sharedScript, parsed[0].LogicJs);
        Assert.Same(parsed[0].LogicJs, parsed[1].LogicJs);
        Assert.Same(parsed[0].ManifestJson, parsed[1].ManifestJson);
    }

    [Fact]
    public async Task McpUpsertWidget_SupportsFlatManifestEditsAndScriptHunks_WhileKeepingEightMcpTools()
    {
        string listReq = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        string? listResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(listReq, pipeNameOverride: "unused");
        Assert.NotNull(listResp);
        using (JsonDocument listDoc = JsonDocument.Parse(listResp!))
        {
            JsonElement tools = listDoc.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(8, tools.GetArrayLength());
            JsonElement upsertTool = tools.EnumerateArray().First(static t => t.GetProperty("name").GetString() == "deskbox_upsert_widget");
            JsonElement props = upsertTool.GetProperty("inputSchema").GetProperty("properties");
            Assert.True(props.TryGetProperty("upsertElements", out _));
            Assert.True(props.TryGetProperty("removeElements", out _));
            Assert.True(props.TryGetProperty("patchRoot", out _));
            Assert.True(props.TryGetProperty("removeRootKeys", out _));
            Assert.True(props.TryGetProperty("scriptHunks", out _));
        }

        string? forwardedPayload = null;
        string callReq = """
        {
          "jsonrpc": "2.0",
          "id": 2,
          "method": "tools/call",
          "params": {
            "name": "deskbox_upsert_widget",
            "arguments": {
              "widgetId": "custom.demo",
              "upsertElements": { "title": { "type": "Text", "text": "Updated" } },
              "patchRoot": { "name": "Updated Name" },
              "scriptHunks": [{ "search": "a = 1;", "replace": "a = 2;" }],
              "commit": false
            }
          }
        }
        """;

        string? callResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            callReq,
            pipeNameOverride: "unused",
            directBridgeSender: (write, _) =>
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    write(w);
                    w.WriteEndObject();
                }
                forwardedPayload = Encoding.UTF8.GetString(ms.ToArray());
                return Task.FromResult<string?>("""{"ok":true,"result":{"staged":true,"stageId":"st_1"}}""");
            });

        Assert.NotNull(callResp);
        Assert.NotNull(forwardedPayload);
        Assert.Contains("\"upsertElements\"", forwardedPayload, StringComparison.Ordinal);
        Assert.Contains("\"patchRoot\"", forwardedPayload, StringComparison.Ordinal);
        Assert.Contains("\"scriptHunks\"", forwardedPayload, StringComparison.Ordinal);

        await using var bridge = new DeskBoxAgentBridgeServer($"DeskBox.PatchErr.{Guid.NewGuid():N}", static () => null);
        string noMgrResp = await bridge.DispatchRequestJsonAsync(forwardedPayload!);
        using JsonDocument noMgrDoc = JsonDocument.Parse(noMgrResp);
        Assert.False(noMgrDoc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task StudioTurn_ConvergesAfterTwoCapabilityInspections_AndAcceptsUnescapedManifestObject()
    {
        static HashSet<string> ReadToolNames(string requestBody)
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("tools", out JsonElement toolsEl) &&
                toolsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in toolsEl.EnumerateArray())
                {
                    if (item.TryGetProperty("function", out JsonElement fnEl) &&
                        fnEl.TryGetProperty("name", out JsonElement nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        string? name = nameEl.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }

            return names;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-conv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-conv", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            string inspectRound1 = BuildToolCallsSse(
            [
                ("call_insp_1", "inspect_capabilities", """{"ids":["notify"]}""")
            ]);
            string inspectRound2 = BuildToolCallsSse(
            [
                ("call_insp_2", "inspect_capabilities", """{"ids":["heatmap"]}""")
            ]);

            string rawWriteBundleArgs = """
                {
                  "manifest": {
                    "schemaVersion": 1,
                    "id": "custom.schedule-card",
                    "name": "Schedule",
                    "state": { "title": "Today" },
                    "root": "main",
                    "elements": {
                      "main": {
                        "type": "Stack",
                        "children": ["hdr"]
                      },
                      "hdr": { "type": "Text", "text": "${$state.title}" }
                    }
                  },
                  "sampleData": {
                    "title": "Monday"
                  }
                }
                """;
            string writeBundleRound3 = BuildToolCallsSse(
            [
                ("call_wb_3", "write_bundle", rawWriteBundleArgs)
            ]);

            var handler = new CapturingHttpHandler((_, _, idx) =>
            {
                string sse = idx switch
                {
                    1 => inspectRound1,
                    2 => inspectRound2,
                    _ => writeBundleRound3
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });

            var ai = new DeskBoxAiService(credStore, prefsStore, new HttpClient(handler));
            var generator = new CustomWidgetAiGeneratorService(ai, diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));

            StudioTurnResult result = await generator.RunStudioTurnAsync(
                "deepseek",
                "Build a schedule card with heatmap and notify",
                uiLanguage: "en-US",
                allowSampleData: true);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(3, handler.RequestBodies.Count);

            HashSet<string> round1Tools = ReadToolNames(handler.RequestBodies[0]);
            Assert.Equal(7, round1Tools.Count);
            Assert.Contains("inspect_capabilities", round1Tools);
            Assert.Contains("probe_http", round1Tools);
            Assert.Contains("write_bundle", round1Tools);
            Assert.Contains("finish_turn", round1Tools);
            Assert.Contains("edit_manifest", round1Tools);
            Assert.Contains("patch_script", round1Tools);
            Assert.Contains("read_workspace", round1Tools);

            HashSet<string> round2Tools = ReadToolNames(handler.RequestBodies[1]);
            Assert.Equal(round1Tools, round2Tools);

            HashSet<string> round3Tools = ReadToolNames(handler.RequestBodies[2]);
            Assert.Equal(round1Tools, round3Tools);
            Assert.Contains("Discovery budget reached", handler.RequestBodies[2], StringComparison.Ordinal);

            Assert.NotNull(result.ParseResult?.Manifest);
            Assert.Equal("Schedule", result.ParseResult!.Manifest!.Name);
            Assert.False(string.IsNullOrWhiteSpace(result.SampleDataJson));
            Assert.Contains("Monday", result.SampleDataJson, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string BuildToolCallsSse(IReadOnlyList<(string Id, string Name, string ArgsJson)> calls)
    {
        var deltaCalls = new List<Dictionary<string, object?>>();
        for (int i = 0; i < calls.Count; i++)
        {
            deltaCalls.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = calls[i].Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = calls[i].Name,
                    ["arguments"] = calls[i].ArgsJson
                }
            });
        }

        string chunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = deltaCalls
                    },
                    ["finish_reason"] = "tool_calls"
                }
            }
        });

        return $"data: {chunk}\n\ndata: [DONE]\n\n";
    }

    private static string BuildTextSse(string content, string finishReason)
    {
        string chunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["content"] = content
                    },
                    ["finish_reason"] = finishReason
                }
            }
        });

        return $"data: {chunk}\n\ndata: [DONE]\n\n";
    }

    private static string BuildSingleToolCallWithFinishReasonSse(
        string id,
        string name,
        string argsJson,
        string finishReason)
    {
        string chunk = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["tool_calls"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["index"] = 0,
                                ["id"] = id,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = name,
                                    ["arguments"] = argsJson
                                }
                            }
                        }
                    },
                    ["finish_reason"] = finishReason
                }
            }
        });

        return $"data: {chunk}\n\ndata: [DONE]\n\n";
    }

    [Fact]
    public async Task StudioTurn_BlocksNoOpEdit_SynthesizesEditSummaryAndFineDiffPills_SalvagesTruncatedWriteBundle_AndStreamsActionSteps()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-turn-hardening-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-hardening", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            var archiveA = new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archA"));

            // (a) Truncated write_bundle salvages complete manifest and continues to finish logic.js
            const string truncatedWriteArgs = """{"manifest":{"schemaVersion":1,"id":"custom.salvaged-widget","name":"Salvaged Widget","script":{"entry":"logic.js"},"root":"main","elements":{"main":{"type":"Stack","children":["hdr"]},"hdr":{"type":"Text","text":"${$computed.msg}"}}},"logicJs":"export function onInit(ctx) { ctx.setCom""";
            string round1Trunc = BuildSingleToolCallWithFinishReasonSse("call_wb_trunc", "write_bundle", truncatedWriteArgs, "length");
            string round2PatchAndFinish = BuildToolCallsSse(
            [
                ("call_ps_2", "patch_script", """{"hunks":[{"search":"","replace":"export function onInit(ctx) {\n  ctx.computed.msg = \"Salvaged\";\n}\n"}]}"""),
                ("call_fin_2", "finish_turn", """{"kind":"edit","reply":"Created the widget.","changeSummary":"Salvaged schedule"}""")
            ]);

            var handlerA = new CapturingHttpHandler((_, _, idx) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(idx == 1 ? round1Trunc : round2PatchAndFinish, Encoding.UTF8, "text/event-stream")
            });
            var genA = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerA)),
                diagnosticArchive: archiveA);

            var traceProgressA = new List<AiGenerationStreamProgress>();
            StudioTurnResult salvageRes = await genA.RunStudioTurnAsync(
                "deepseek",
                "Build a salvaged widget with logic.js",
                uiLanguage: "en-US",
                onProgress: p => traceProgressA.Add(p));

            Assert.True(salvageRes.Success, salvageRes.ErrorMessage);
            Assert.False(salvageRes.WasNoOpEdit);
            Assert.NotNull(salvageRes.ManifestJson);
            Assert.Contains("custom.salvaged-widget", salvageRes.ManifestJson!, StringComparison.Ordinal);
            Assert.Contains("computed.msg", salvageRes.LogicJs ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(traceProgressA, p => p.Steps is { Count: > 0 });
            Assert.Contains(traceProgressA.SelectMany(p => p.Steps ?? []), s => s.Id.Contains("patch_script", StringComparison.Ordinal));
            int prevChars = 0;
            foreach (AiGenerationStreamProgress prog in traceProgressA)
            {
                Assert.True(prog.ContentChars >= prevChars, $"Expected cumulative ContentChars >= {prevChars}, got {prog.ContentChars}");
                prevChars = prog.ContentChars;
            }

            // (b) Auto-finalized follow-up edit synthesizes localized edit summary + fine-grained diff pills
            const string baselineManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.course-grid",
              "name": "Courses",
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "spacing": 6,
                  "children": ["hdr"]
                },
                "hdr": { "type": "Text", "text": "Old Header", "fontSize": 11 }
              }
            }
            """;

            var handlerB = new CapturingHttpHandler((_, _, idx) =>
            {
                string sse = idx == 1
                    ? BuildToolCallsSse(
                    [
                        ("call_em_1", "edit_manifest", """{"upsertElements":{"hdr":{"type":"Text","text":"New Header","fontSize":11}}}""")
                    ])
                    : BuildTextSse(string.Empty, "stop");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });
            var genB = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerB)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archB")));

            var traceProgressB = new List<AiGenerationStreamProgress>();
            StudioTurnResult autoEditRes = await genB.RunStudioTurnAsync(
                "deepseek",
                "Change header text to New Header",
                currentManifestJson: baselineManifest,
                uiLanguage: "en-US",
                onProgress: p => traceProgressB.Add(p));

            Assert.True(autoEditRes.Success, autoEditRes.ErrorMessage);
            Assert.Equal("edit", autoEditRes.Kind);
            Assert.False(autoEditRes.WasNoOpEdit);
            Assert.DoesNotContain("Created", autoEditRes.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("已生成", autoEditRes.Reply, StringComparison.Ordinal);
            Assert.Contains("Text", autoEditRes.Reply, StringComparison.Ordinal);
            Assert.NotNull(autoEditRes.FineGrainedDiffPills);
            Assert.Contains(
                autoEditRes.FineGrainedDiffPills!,
                pill => pill.Contains("Text", StringComparison.Ordinal));
            Assert.Contains(traceProgressB, p => string.Equals(p.ActiveToolName, "edit_manifest", StringComparison.Ordinal));

            // (c) Repeated No-Op edit retries once then downgrades cleanly to Kind == "answer" + WasNoOpEdit
            int roundCountC = 0;
            var handlerC = new CapturingHttpHandler((_, _, idx) =>
            {
                roundCountC = idx;
                string sse = BuildToolCallsSse(
                [
                    ($"call_fin_noop_{idx}", "finish_turn", """{"kind":"edit","reply":"Updated layout.","changeSummary":"Edit layout"}""")
                ]);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });
            var genC = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerC)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archC")));

            StudioTurnResult noOpRes = await genC.RunStudioTurnAsync(
                "deepseek",
                "Fix the header",
                currentManifestJson: baselineManifest,
                uiLanguage: "en-US");

            Assert.Equal(2, roundCountC);
            Assert.True(noOpRes.Success, noOpRes.ErrorMessage);
            Assert.True(noOpRes.WasNoOpEdit);
            Assert.Equal("answer", noOpRes.Kind);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkspaceEngine_EditManifest_StripsHashPrefixCleansAllDanglingRefsAndRejectsEmptyOrMalformedArgs()
    {
        const string initial = """
        {
          "schemaVersion": 1,
          "id": "custom.flat-edit-refs",
          "name": "Flat Edit Refs",
          "description": "Will be removed",
          "collections": { "items": {} },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["hdr", "list_1", "sub"] },
            "hdr": { "type": "Text", "text": "Old Header" },
            "list_1": {
              "type": "List",
              "items": "items",
              "itemTemplate": "tpl_row",
              "hoverActions": ["btn_del"]
            },
            "tpl_row": { "type": "Text", "text": "${$item.title}" },
            "btn_del": { "type": "IconButton", "icon": "delete" },
            "sub": { "type": "Text", "text": "Sub" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(initial);
        Assert.True(
            StudioToolTransport.TryParseEditManifestArgs(
                """
                {
                  "upsertElements": {
                    "#hdr": { "type": "Text", "text": "Updated Header" },
                    "btn_del": null
                  },
                  "removeElements": ["#sub", "tpl_row"],
                  "patchRoot": {
                    "name": "Updated Flat Edit",
                    "description": null,
                    "root": "main"
                  }
                }
                """,
                out StudioFlatManifestEditRequest? editReq,
                out string? parseErr),
            parseErr);

        StudioWorkspaceOpResult okRes = engine.ApplyFlatManifestEdit(editReq!);
        Assert.True(okRes.Succeeded, okRes.ErrorMessage);

        JsonObject root = JsonNode.Parse(engine.GetManifestJson(indented: false)!)!.AsObject();
        Assert.Equal("Updated Flat Edit", root["name"]!.GetValue<string>());
        Assert.Null(root["description"]);

        JsonObject elems = root["elements"]!.AsObject();
        Assert.Equal("Updated Header", elems["hdr"]!["text"]!.GetValue<string>());
        Assert.Null(elems["sub"]);
        Assert.Null(elems["tpl_row"]);
        Assert.Null(elems["btn_del"]);

        string[] mainChildren = elems["main"]!["children"]!.AsArray()
            .Select(static n => n!.GetValue<string>())
            .ToArray();
        Assert.Equal(["hdr", "list_1"], mainChildren);
        Assert.Null(elems["list_1"]!["itemTemplate"]);
        Assert.Empty(elems["list_1"]!["hoverActions"]!.AsArray());

        // Empty or malformed edit_manifest arguments fail cleanly
        Assert.False(StudioToolTransport.TryParseEditManifestArgs("{}", out _, out string? emptyErr));
        Assert.Contains("requires at least one", emptyErr, StringComparison.Ordinal);
        Assert.False(StudioToolTransport.TryParseEditManifestArgs("{broken", out _, out string? malformedErr));
        Assert.Contains("Invalid edit_manifest JSON", malformedErr, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceEngine_WriteBundle_UpdatesLocalesAtomicallyAndSalvagesTruncatedManifestHint()
    {
        const string writeBundleArgs = """
        {
          "manifest": {
            "schemaVersion": 1,
            "id": "custom.i18n-bundle",
            "name": "${$i18n.title}",
            "root": "main",
            "elements": {
              "main": { "type": "Stack", "children": ["t"] },
              "t": { "type": "Text", "text": "${$i18n.title}" }
            }
          },
          "logicJs": "export function onInit(ctx) { ctx.computed.ready = true; }\n",
          "locales": {
            "en-US": { "title": "English Title" },
            "zh-CN": { "title": "中文标题" }
          }
        }
        """;

        Assert.True(
            StudioToolTransport.TryParseWriteBundleArgs(
                writeBundleArgs,
                out string manifestJson,
                out string? logicJs,
                out CustomWidgetLocaleCatalog? locales,
                out bool deleteScript,
                out _,
                out string? parseErr),
            parseErr);
        Assert.False(deleteScript);
        Assert.NotNull(locales);
        Assert.True(locales!.HasAnyLocale);

        var engine = new StudioWorkspaceEngine();
        StudioWorkspaceOpResult bundleRes = engine.SetFullBundle(manifestJson, logicJs, locales);
        Assert.True(bundleRes.Succeeded, bundleRes.ErrorMessage);
        Assert.Equal("English Title", engine.Locales!.Locales["en-US"]["title"]);
        Assert.Equal("中文标题", engine.Locales.Locales["zh-CN"]["title"]);

        // Truncated JSON inside write_bundle: TryParse fails clearly, while TrySalvage extracts complete manifest object
        const string truncatedArgs = """{"manifest":{"schemaVersion":1,"id":"custom.salvage-check","name":"Salvage","root":"main","elements":{"main":{"type":"Stack","children":[]}}},"logicJs":"export function onInit(ctx) {""";
        Assert.False(
            StudioToolTransport.TryParseWriteBundleArgs(
                truncatedArgs,
                out _,
                out _,
                out _,
                out _,
                out _,
                out string? truncErr));
        Assert.False(string.IsNullOrWhiteSpace(truncErr));

        Assert.True(StudioWorkspaceEngine.TrySalvageManifestFromTruncatedWriteBundle(truncatedArgs, out string salvaged));
        JsonObject salvagedObj = JsonNode.Parse(salvaged)!.AsObject();
        Assert.Equal("custom.salvage-check", salvagedObj["id"]!.GetValue<string>());
    }

    [Fact]
    public void GeneratorService_StudioThinkingCoercion_HonorsExplicitEnabledAndFlagsExhaustedAutoRoutes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-think-coerce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                "deepseek",
                "sk-test",
                CustomModel: "deepseek-flash",
                ThinkingMode: "enabled"));
            credStore.Upsert(new AiProviderCredentialEntry(
                "openai",
                "sk-test-2",
                CustomModel: "gpt-4o-mini",
                ThinkingMode: "enabled"));

            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            using var client = new HttpClient(new CapturingHttpHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)));
            var aiService = new DeskBoxAiService(credStore, prefsStore, client);
            var gen = new CustomWidgetAiGeneratorService(
                aiService,
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "arch")));

            // Explicit enabled on deepseek-flash is preserved by Studio authoring invocation.
            Assert.Equal(AiThinkingMode.Enabled, gen.ResolveEffectiveStudioInvocation("deepseek").ThinkingMode);
            Assert.Equal(AiThinkingMode.Enabled, gen.ResolveEffectiveStudioInvocation("openai").ThinkingMode);

            // Auto/Enabled mode downgrades to Disabled once this route hits thinking exhaustion at runtime.
            credStore.UpdateStudioSettings("deepseek", thinkingMode: "auto");
            Assert.Equal(AiThinkingMode.Auto, gen.ResolveEffectiveStudioInvocation("deepseek").ThinkingMode);
            aiService.MarkThinkingExhaustedRoute(aiService.ResolveEffectiveSettings("deepseek"));
            Assert.Equal(AiThinkingMode.Disabled, gen.ResolveEffectiveStudioInvocation("deepseek").ThinkingMode);

            // Clearing route fallbacks restores original invocation mode.
            aiService.ClearRouteFallbacks();
            credStore.UpdateStudioSettings("deepseek", thinkingMode: "enabled");
            Assert.Equal(AiThinkingMode.Enabled, gen.ResolveEffectiveStudioInvocation("deepseek").ThinkingMode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioSessionStore_PersistsSupersededMessagesExecutionStepsAndPinnedCapabilities()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-session-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetStudioSessionStore(tempDir);
            var draft = new StudioDraftSnapshot
            {
                SelectedEmoji = "📅",
                StartDraftIdea = "Schedule idea",
                RefineDraftIdea = "Make cells taller",
                SelectedRevisionIndex = 0,
                BriefMarkdown = "# Brief",
                PinnedCapabilityIds = ["timer-and-countdown", "storage-and-collections"],
                Revisions =
                [
                    new StudioPersistedRevision
                    {
                        RevisionId = "rev-1",
                        Source = "ai",
                        ChangeSummary = "Initial schedule",
                        ManifestSha256 = "abc123",
                        ManifestJson = """{"schemaVersion":1,"id":"custom.sched","name":"Schedule","root":"main","elements":{"main":{"type":"Stack","children":[]}}}"""
                    }
                ],
                ChatMessages =
                [
                    new StudioChatMessage
                    {
                        Id = "msg-u1",
                        Role = "user",
                        Kind = "edit",
                        Text = "Build schedule",
                        IsSuperseded = true
                    },
                    new StudioChatMessage
                    {
                        Id = "msg-a1",
                        Role = "assistant",
                        Kind = "edit",
                        Text = "Built initial schedule.",
                        LinkedRevisionId = "rev-1",
                        IsSuperseded = true,
                        ExecutionSummary = "2 steps · write_bundle",
                        ExecutionSteps =
                        [
                            new StudioStreamStepItem("r1-write", StudioStreamStepState.Completed, "Write bundle"),
                            new StudioStreamStepItem("r1-val", StudioStreamStepState.Warning, "Minor warning")
                        ]
                    }
                ]
            };

            store.SaveWidgetDraft("widget-123", draft);
            StudioDraftSnapshot? loadedDraft = store.TryLoadWidgetDraft("widget-123");
            Assert.NotNull(loadedDraft);
            Assert.Equal(["timer-and-countdown", "storage-and-collections"], loadedDraft!.PinnedCapabilityIds);
            Assert.Equal(2, loadedDraft.ChatMessages.Count);
            Assert.True(loadedDraft.ChatMessages[0].IsSuperseded);
            Assert.True(loadedDraft.ChatMessages[1].IsSuperseded);
            Assert.Equal("2 steps · write_bundle", loadedDraft.ChatMessages[1].ExecutionSummary);
            Assert.Collection(
                loadedDraft.ChatMessages[1].ExecutionSteps,
                s0 =>
                {
                    Assert.Equal("r1-write", s0.Id);
                    Assert.Equal(StudioStreamStepState.Completed, s0.State);
                    Assert.Equal("Write bundle", s0.Label);
                },
                s1 =>
                {
                    Assert.Equal("r1-val", s1.Id);
                    Assert.Equal(StudioStreamStepState.Warning, s1.State);
                    Assert.Equal("Minor warning", s1.Label);
                });

            store.SaveCommittedWidgetSession("widget-456", "# Committed", draft.Revisions, draft.ChatMessages);
            IReadOnlyList<StudioChatMessage> loadedMessages =
                store.LoadOrReconcileWidgetSession("widget-456", draft.Revisions[0].ManifestJson).ChatMessages;
            Assert.Equal(2, loadedMessages.Count);
            Assert.True(loadedMessages[1].IsSuperseded);
            Assert.Equal("2 steps · write_bundle", loadedMessages[1].ExecutionSummary);
            Assert.Equal(2, loadedMessages[1].ExecutionSteps.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HealthAnalyzer_RecognizesNestedFlexibleScrollUnderTabs_WhileFlaggingStaticCardOverflow()
    {
        const string tabsScrollManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.tabs-scroll",
          "name": "Tabs Scroll",
          "defaultSize": { "width": 320, "height": 220 },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "spacing": 6, "children": ["tabs_root"] },
            "tabs_root": { "type": "Tabs", "children": ["tab1"] },
            "tab1": { "title": "Main", "type": "Stack", "children": ["scroll_rows"] },
            "scroll_rows": {
              "type": "Scroll",
              "children": ["r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8", "r9", "r10"]
            },
            "r1": { "type": "Text", "text": "Row 1" },
            "r2": { "type": "Text", "text": "Row 2" },
            "r3": { "type": "Text", "text": "Row 3" },
            "r4": { "type": "Text", "text": "Row 4" },
            "r5": { "type": "Text", "text": "Row 5" },
            "r6": { "type": "Text", "text": "Row 6" },
            "r7": { "type": "Text", "text": "Row 7" },
            "r8": { "type": "Text", "text": "Row 8" },
            "r9": { "type": "Text", "text": "Row 9" },
            "r10": { "type": "Text", "text": "Row 10" }
          }
        }
        """;

        CustomWidgetParseResult tabsParse = CustomWidgetManifestParser.Parse(tabsScrollManifestJson);
        Assert.True(tabsParse.IsValid, string.Join(" | ", tabsParse.Diagnostics.Select(d => d.Message)));
        CustomWidgetManifest tabsManifest = tabsParse.Manifest!;
        CustomWidgetHealthReport tabsHealth = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            rawManifestJson: tabsScrollManifestJson,
            manifest: tabsManifest,
            storePayload: new CustomWidgetStorePayload(),
            measuredWidth: 320,
            measuredHeight: 220,
            cultureName: "en-US");

        Assert.Equal(0, tabsHealth.VerticalOverflowPx);
        Assert.DoesNotContain(tabsHealth.Issues, i => i.Code == "layout.vertical_overflow");

        const string nestedCardsManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.static-cards-overflow",
          "name": "Static Cards",
          "defaultSize": { "width": 300, "height": 220 },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "spacing": 8, "children": ["card1"] },
            "card1": { "type": "Card", "children": ["l1", "card2"] },
            "l1": { "type": "Text", "text": "Line 1" },
            "card2": { "type": "Card", "children": ["l2", "card3"] },
            "l2": { "type": "Text", "text": "Line 2" },
            "card3": { "type": "Card", "children": ["l3", "l4", "l5", "l6", "l7", "l8"] },
            "l3": { "type": "Text", "text": "Line 3" },
            "l4": { "type": "Text", "text": "Line 4" },
            "l5": { "type": "Text", "text": "Line 5" },
            "l6": { "type": "Text", "text": "Line 6" },
            "l7": { "type": "Text", "text": "Line 7" },
            "l8": { "type": "Text", "text": "Line 8" }
          }
        }
        """;

        CustomWidgetManifest cardsManifest = CustomWidgetManifestParser.Parse(nestedCardsManifestJson).Manifest!;
        CustomWidgetHealthReport cardsHealth = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            rawManifestJson: nestedCardsManifestJson,
            manifest: cardsManifest,
            storePayload: new CustomWidgetStorePayload(),
            measuredWidth: 300,
            measuredHeight: 220,
            cultureName: "en-US");

        Assert.True(cardsHealth.VerticalOverflowPx > 0);
        Assert.Equal(3, cardsHealth.MaxCardNestingDepth);
        Assert.Contains(cardsHealth.Issues, i => i.Code == "layout.vertical_overflow");
        Assert.Contains(cardsHealth.Issues, i => i.Code == "visual.card_nesting");
    }

    private sealed class ProbeTraceRow
    {
        public bool IsRingActive { get; set; }
        public string Label { get; set; } = string.Empty;
        public StudioStreamStepState State { get; set; }
    }

    [Fact]
    public void ActionTraceReconciler_PreservesRunningIndicatorIdentityAcrossStreamingTicksAndCleansUpOnTransitionAndFold()
    {
        var cache = new List<StudioCoCreationChatPanel.ReconciledTraceEntry<ProbeTraceRow>>();
        var removedRows = new List<ProbeTraceRow>();
        int createCount = 0;
        int updateLabelCount = 0;
        int updateStateCount = 0;

        void Sync(IReadOnlyList<StudioStreamStepItem> steps) =>
            StudioCoCreationChatPanel.ReconcileTraceRows(
                cache,
                steps,
                renderEmptyState: () => { },
                clearEmptyStateIfPresent: () => { },
                createRow: s =>
                {
                    createCount++;
                    return new ProbeTraceRow
                    {
                        Label = s.Label,
                        State = s.State,
                        IsRingActive = s.State == StudioStreamStepState.Running
                    };
                },
                updateLabel: (row, label) =>
                {
                    updateLabelCount++;
                    row.Label = label;
                },
                updateState: (row, state) =>
                {
                    updateStateCount++;
                    row.State = state;
                    row.IsRingActive = state == StudioStreamStepState.Running;
                },
                removeRowAt: (_, row) =>
                {
                    row.IsRingActive = false;
                    removedRows.Add(row);
                });

        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Running, "Wrote full widget bundle")
        ]);
        Assert.Equal(2, createCount);
        ProbeTraceRow firstRunningRow = cache[1].View;
        Assert.True(firstRunningRow.IsRingActive);

        // 15 streaming chunks with identical Running step must never recreate the row or toggle its ring.
        for (int tick = 0; tick < 15; tick++)
        {
            Sync(
            [
                new("connect", StudioStreamStepState.Completed, "Connected"),
                new("r0-write_bundle", StudioStreamStepState.Running, "Wrote full widget bundle")
            ]);
        }
        Assert.Equal(2, createCount);
        Assert.Equal(0, updateStateCount);
        Assert.Same(firstRunningRow, cache[1].View);
        Assert.True(firstRunningRow.IsRingActive);

        // Label updates while Running preserve ring state without calling updateState.
        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Running, "Wrote full widget bundle · streaming")
        ]);
        Assert.Equal(1, updateLabelCount);
        Assert.Equal(0, updateStateCount);
        Assert.True(firstRunningRow.IsRingActive);

        // Consecutive warning retry starts a second row, then folds back into r0-write_bundle.
        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Warning, "Wrote full widget bundle · failed"),
            new("r1-write_bundle", StudioStreamStepState.Running, "Wrote full widget bundle")
        ]);
        Assert.Equal(1, updateStateCount);
        Assert.False(firstRunningRow.IsRingActive);
        ProbeTraceRow secondRunningRow = cache[2].View;
        Assert.True(secondRunningRow.IsRingActive);

        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Warning, "Wrote full widget bundle (retry #2) · failed")
        ]);
        Assert.Single(removedRows);
        Assert.Same(secondRunningRow, removedRows[0]);
        Assert.False(secondRunningRow.IsRingActive);
        Assert.Equal(2, cache.Count);

        // Transitioning Running -> Completed deactivates ring once.
        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Warning, "Wrote full widget bundle (retry #2) · failed"),
            new("r2-patch_script", StudioStreamStepState.Running, "Updated script")
        ]);
        ProbeTraceRow scriptRow = cache[2].View;
        Assert.True(scriptRow.IsRingActive);
        int stateUpdatesBeforeCompletion = updateStateCount;
        Sync(
        [
            new("connect", StudioStreamStepState.Completed, "Connected"),
            new("r0-write_bundle", StudioStreamStepState.Warning, "Wrote full widget bundle (retry #2) · failed"),
            new("r2-patch_script", StudioStreamStepState.Completed, "Updated script")
        ]);
        Assert.Equal(stateUpdatesBeforeCompletion + 1, updateStateCount);
        Assert.False(scriptRow.IsRingActive);
    }

    [Fact]
    public void WorkspaceEngine_SalvageTruncatedWriteBundle_ClosesFlatManifestAndDropsIncompleteTailElement()
    {
        var elements = new JsonObject
        {
            ["main"] = new JsonObject
            {
                ["type"] = "Stack",
                ["spacing"] = 8,
                ["children"] = new JsonArray("card1", "card2")
            }
        };
        var padIds1 = new JsonArray();
        var padIds2 = new JsonArray();
        for (int i = 0; i < 24; i++)
        {
            padIds1.Add($"c1_t{i}");
            padIds2.Add($"c2_t{i}");
        }
        elements["card1"] = new JsonObject { ["type"] = "Card", ["children"] = padIds1 };
        for (int i = 0; i < 24; i++)
        {
            elements[$"c1_t{i}"] = new JsonObject { ["type"] = "Text", ["text"] = $"Budget item row {i:D2} · Food & Dining summary entry" };
        }
        elements["card2"] = new JsonObject { ["type"] = "Card", ["children"] = padIds2 };
        for (int i = 0; i < 24; i++)
        {
            elements[$"c2_t{i}"] = new JsonObject { ["type"] = "Text", ["text"] = $"Category item row {i:D2} · Transport & Subscription summary entry" };
        }

        string prefixObj = JsonSerializer.Serialize(new JsonObject
        {
            ["manifest"] = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = "custom.ledger-salvage",
                ["name"] = "Quick Ledger",
                ["root"] = "main",
                ["elements"] = elements
            }
        });

        // Strip final "}}}" so complete elements remain, followed by a truncated card3 entry.
        int closeIdx = prefixObj.LastIndexOf("}}}", StringComparison.Ordinal);
        Assert.True(closeIdx > 0);
        string truncatedBundleArgs = prefixObj[..closeIdx] + ""","card3":{"type":"Card","children":["p0"]},"p0":{"type":"ProgressBar","value":""";
        Assert.True(truncatedBundleArgs.Length > 4500, $"Expected ~5KB payload, got {truncatedBundleArgs.Length}");

        Assert.True(StudioWorkspaceEngine.TrySalvageManifestFromTruncatedWriteBundle(truncatedBundleArgs, out string salvaged));

        JsonObject salvagedObj = JsonNode.Parse(salvaged)!.AsObject();
        JsonObject salvagedElements = salvagedObj["elements"]!.AsObject();
        JsonArray salvagedChildren = salvagedElements["main"]!["children"]!.AsArray();
        Assert.Equal(2, salvagedChildren.Count);
        Assert.Null(salvagedElements["card3"]);

        CustomWidgetParseResult parseRes = CustomWidgetManifestParser.Parse(salvaged);
        Assert.True(parseRes.IsValid, string.Join(" | ", parseRes.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(parseRes.Diagnostics, d => d.IsError);

        // Pre-elements truncation: state/collections/script exist, truncated before any valid element graph completes ->
        // seed clean skeleton placeholder (#skeleton_placeholder) while preserving earlier manifest sections.
        const string preElementsTruncatedBundleArgs =
            """{"manifest":{"schemaVersion":1,"id":"custom.pre-view-cut","name":"PreView Ledger","state":{"budget":3000},"collections":{"entries":{}},"script":{"entry":"logic.js"},"root":"main","elements":{"main":{"type":"Stack","children":["card1"]},"card1":{"type":"Card","pad""";
        Assert.True(StudioWorkspaceEngine.TrySalvageManifestFromTruncatedWriteBundle(preElementsTruncatedBundleArgs, out string preViewSalvaged));

        JsonObject preViewObj = JsonNode.Parse(preViewSalvaged)!.AsObject();
        Assert.Equal("custom.pre-view-cut", preViewObj["id"]?.GetValue<string?>());
        Assert.Equal(3000, preViewObj["state"]?["budget"]?.GetValue<int>());
        Assert.NotNull(preViewObj["collections"]?["entries"]);
        Assert.Equal("logic.js", preViewObj["script"]?["entry"]?.GetValue<string?>());
        Assert.Contains("skeleton_placeholder", preViewSalvaged, StringComparison.Ordinal);

        CustomWidgetParseResult preViewParse = CustomWidgetManifestParser.Parse(preViewSalvaged);
        Assert.True(preViewParse.IsValid, string.Join(" | ", preViewParse.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(preViewParse.Diagnostics, d => d.IsError);
    }

    [Fact]
    public void ToolTransport_TryParsePatchScriptArgs_AcceptsDirectContentForCreationAndHunksForEdits()
    {
        const string contentPayload = """{"content":"export function onInit(ctx) { ctx.computed.x = 1; }"}""";

        Assert.True(
            StudioToolTransport.TryParsePatchScriptArgs(contentPayload, out List<StudioScriptPatchHunk> hunks4, out string? fullContent, out string? err4),
            err4);
        Assert.Empty(hunks4);
        Assert.Contains("ctx.computed.x = 1", fullContent, StringComparison.Ordinal);

        Assert.True(
            StudioToolTransport.TryParsePatchScriptArgs(contentPayload, out List<StudioScriptPatchHunk> hunks3, out string? err3),
            err3);
        StudioScriptPatchHunk singleHunk = Assert.Single(hunks3);
        Assert.Equal(string.Empty, singleHunk.Search);
        Assert.Contains("ctx.computed.x = 1", singleHunk.Replace, StringComparison.Ordinal);

        const string hunksPayload = """{"hunks":[{"search":"const a = 1;","replace":"const a = 2;"}]}""";
        Assert.True(
            StudioToolTransport.TryParsePatchScriptArgs(hunksPayload, out List<StudioScriptPatchHunk> editHunks, out string? editFull, out string? editErr),
            editErr);
        Assert.Null(editFull);
        StudioScriptPatchHunk editHunk = Assert.Single(editHunks);
        Assert.Equal("const a = 1;", editHunk.Search);
        Assert.Equal("const a = 2;", editHunk.Replace);

        Assert.False(StudioToolTransport.TryParsePatchScriptArgs("""{"hunks":[]}""", out _, out _, out string? emptyErr));
        Assert.Equal("'hunks' array must not be empty.", emptyErr);
    }

    [Fact]
    public async Task RunStudioTurnAsync_FoldsConsecutiveTruncatedWriteBundleWarnings_RecoversViaSplitSteps_AndSealsRunningStepsOnExhaustion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-turn-steps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-steps", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            // Subcase A: Two proxy-masked truncated write_bundle calls (finish_reason=tool_calls, early cut before view)
            // fold into a single Warning step with retry count and recover cleanly via split write_bundle + patch_script(content).
            var handlerA = new CapturingHttpHandler((_, _, idx) =>
            {
                string sse = idx switch
                {
                    1 => BuildSingleToolCallWithFinishReasonSse("call_wb_1", "write_bundle", """{"manifest":{"schemaVersion":1,"id":"custom.early""", "tool_calls"),
                    2 => BuildSingleToolCallWithFinishReasonSse("call_wb_2", "write_bundle", """{"manifest":{"schemaVersion":1,"id":"custom.early2""", "tool_calls"),
                    3 => BuildSingleToolCallWithFinishReasonSse(
                        "call_wb_3",
                        "write_bundle",
                        """{"manifest":{"schemaVersion":1,"id":"custom.ledger-split","name":"Ledger","script":{"entry":"logic.js"},"root":"main","elements":{"main":{"type":"Stack","children":["tot"]},"tot":{"type":"Text","text":"${$computed.total}"}}}}""",
                        "tool_calls"),
                    _ => BuildToolCallsSse(
                    [
                        ("call_ps_4", "patch_script", """{"content":"export function onInit(ctx) { ctx.computed.total = '¥0'; }"}"""),
                        ("call_fin_4", "finish_turn", """{"kind":"edit","reply":"Created the ledger.","changeSummary":"Split ledger build"}""")
                    ])
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });

            var genA = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerA)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archA")));

            var progressA = new List<AiGenerationStreamProgress>();
            StudioTurnResult resA = await genA.RunStudioTurnAsync(
                "deepseek",
                "Build a pocket ledger widget",
                uiLanguage: "en-US",
                onProgress: p => progressA.Add(p));

            Assert.True(resA.Success, resA.ErrorMessage);
            Assert.NotNull(resA.ExecutionSteps);
            Assert.DoesNotContain(resA.ExecutionSteps!, s => s.State == StudioStreamStepState.Running);

            // Second LLM request should guide Step 1 write_bundle(manifest only), never claiming manifest was already salvaged.
            Assert.True(handlerA.RequestBodies.Count >= 2);
            Assert.Contains("Step 1: call write_bundle with ONLY a concise 'manifest' object", handlerA.RequestBodies[1], StringComparison.Ordinal);
            Assert.DoesNotContain("ALREADY been salvaged", handlerA.RequestBodies[1], StringComparison.Ordinal);

            List<StudioStreamStepItem> wbWarningsA = resA.ExecutionSteps!
                .Where(s => s.State == StudioStreamStepState.Warning && s.Id.Contains("write_bundle", StringComparison.Ordinal))
                .ToList();
            StudioStreamStepItem foldedWarning = Assert.Single(wbWarningsA);
            Assert.Contains("(retry #2)", foldedWarning.Label, StringComparison.Ordinal);
            Assert.Contains("Output hit single-call limit", foldedWarning.Label, StringComparison.Ordinal);
            Assert.Contains(resA.ExecutionSteps!, s => s.State == StudioStreamStepState.Completed && s.Id.Contains("patch_script", StringComparison.Ordinal));
            Assert.Contains(resA.ExecutionSteps!, s => s.State == StudioStreamStepState.Completed && s.Id == "validate");

            // Subcase B: Mid-stream error during round 2 after valid write_bundle seals lingering Running step to Warning.
            string abortedPatchDelta = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["tool_calls"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["index"] = 0,
                                    ["id"] = "call_ps_abort",
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?>
                                    {
                                        ["name"] = "patch_script",
                                        ["arguments"] = "{\"hunks\":"
                                    }
                                }
                            }
                        }
                    }
                }
            });
            string midStreamErrorSse =
                $"data: {abortedPatchDelta}\n\n" +
                """data: {"error":{"message":"upstream stream interrupted"}}""" + "\n\n";

            var handlerB = new CapturingHttpHandler((_, _, idx) =>
            {
                string sse = idx == 1
                    ? BuildSingleToolCallWithFinishReasonSse(
                        "call_wb_soft",
                        "write_bundle",
                        """{"manifest":{"schemaVersion":1,"id":"custom.soft-seal","name":"Soft Seal","script":{"entry":"logic.js"},"collections":{"items":{}},"root":"main","elements":{"main":{"type":"Stack","children":["val","btn"]},"val":{"type":"Text","text":"${$computed.sum}"},"btn":{"type":"Button","text":"Add"}}},"logicJs":"export function onInit(ctx) { ctx.computed.sum = ctx.collections.get('items').length > 0 ? String(ctx.collections.get('items').length) : ''; }","sampleData":{"collections":{"items":[{"name":"Coffee"}]}}}""",
                        "tool_calls")
                    : midStreamErrorSse;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            });

            var genB = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerB)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archB")));

            var progressB = new List<AiGenerationStreamProgress>();
            StudioTurnResult resB = await genB.RunStudioTurnAsync(
                "deepseek",
                "Build a soft-repair seal test",
                uiLanguage: "en-US",
                onProgress: p => progressB.Add(p));

            Assert.True(resB.Success, resB.ErrorMessage);
            Assert.NotNull(resB.ExecutionSteps);
            Assert.DoesNotContain(resB.ExecutionSteps!, s => s.State == StudioStreamStepState.Running);
            Assert.Contains(resB.ExecutionSteps!, s => s.State == StudioStreamStepState.Warning && s.Id.Contains("patch_script", StringComparison.Ordinal));
            Assert.NotEmpty(progressB);
            Assert.DoesNotContain(progressB[^1].Steps ?? [], s => s.State == StudioStreamStepState.Running);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkspaceEngine_ExtractsAndStripsStrayInlineLogicJsAndSampleDataFromManifest()
    {
        const string pollutedManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.polluted-bundle",
          "name": "Polluted Bundle",
          "script": { "entry": "logic.js" },
          "collections": { "rows": {} },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["txt"]
            },
            "txt": { "type": "Text", "text": "${$computed.headline}" }
          },
          "logicJs": "export function onInit(ctx) {\n  ctx.computed.headline = 'Promoted';\n}\n",
          "sampleData": {
            "collections": {
              "rows": [
                { "title": "Row A" }
              ]
            }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine();
        StudioWorkspaceOpResult op = engine.SetFullBundle(pollutedManifest, logicJs: null);
        Assert.True(op.Succeeded, op.ErrorMessage);
        Assert.Contains("ctx.computed.headline = 'Promoted'", engine.LogicJs ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Row A", engine.ExtractedInlineSampleDataJson ?? string.Empty, StringComparison.Ordinal);

        string cleanedManifest = engine.GetManifestJson(indented: false)!;
        using JsonDocument doc = JsonDocument.Parse(cleanedManifest);
        Assert.False(doc.RootElement.TryGetProperty("logicJs", out _));
        Assert.False(doc.RootElement.TryGetProperty("sampleData", out _));
        Assert.True(doc.RootElement.TryGetProperty("script", out JsonElement scriptMeta));
        Assert.Equal("logic.js", scriptMeta.GetProperty("entry").GetString());

        // Explicit outer logicJs takes precedence over stray inline manifest.logicJs while still stripping it
        var engineExplicit = new StudioWorkspaceEngine();
        StudioWorkspaceOpResult opExplicit = engineExplicit.SetFullBundle(
            pollutedManifest,
            logicJs: "export function onInit(ctx) { ctx.computed.headline = 'ExplicitOuter'; }");
        Assert.True(opExplicit.Succeeded, opExplicit.ErrorMessage);
        Assert.Contains("ExplicitOuter", engineExplicit.LogicJs ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("Promoted", engineExplicit.LogicJs ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\"logicJs\"", engineExplicit.GetManifestJson(indented: false)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectCapabilities_EmbedsCanonicalIdsInSchema_And_ResolvesUnmatchedHyphenatedIdsViaFuzzyTokens()
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            StudioToolTransport.WriteOpenAiToolsArray(writer);
            writer.WriteEndObject();
        }

        string toolsJson = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("edit_manifest", toolsJson, StringComparison.Ordinal);
        Assert.Contains("network-and-feeds", toolsJson, StringComparison.Ordinal);
        Assert.Contains("local-files-and-cli", toolsJson, StringComparison.Ordinal);
        Assert.Contains("do NOT nest logicJs", toolsJson, StringComparison.Ordinal);

        Assert.True(StudioToolTransport.TryParseInspectOrProbeArgs(
            StudioToolTransport.ToolInspectCapabilities,
            """{"ids":["script-sandbox","http-json","http-request"]}""",
            out StudioAgenticActionRequest? req,
            out string? parseErr), parseErr);
        string result = await StudioTurnResponseParser.ExecuteStudioAgenticActionAsync(
            networkSourceEngine: null,
            request: req!,
            useNativeTools: false,
            cancellationToken: CancellationToken.None);

        Assert.Contains("`@script-sandbox`", result, StringComparison.Ordinal);
        Assert.Contains("`@http-and-rss-feeds`", result, StringComparison.Ordinal);
        Assert.Contains("`@interactive-http`", result, StringComparison.Ordinal);
        Assert.Contains("Canonical capability IDs:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiToolStreamingRound_PerToolStepIds_SalvagesInlineLogicJs_And_SkipsUnchangedStartupDocsExport()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-univ-opt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. EnsureAiAgentWorkspaceDocsAsync stamp fast-path avoids re-materializing CustomWidgetCapabilityRegistry
            string docsRoot = Path.Combine(tempDir, "workspace-docs");
            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(docsRoot, includeWorkspaceAgentGuides: true);
            CustomWidgetCapabilityRegistry.ReleaseIdleCache(dropWeakReference: true);
            Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(docsRoot, includeWorkspaceAgentGuides: true);
            Assert.False(CustomWidgetCapabilityRegistry.IsRegistryMaterialized);

            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry("deepseek", "sk-test-multi", CustomModel: "deepseek-chat", ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            // 2. Incremental multi-frame SSE with [edit_manifest, patch_script, finish_turn] produces 0 false Warning steps
            static string BuildIncrementalMultiToolSse((string Id, string Name, string ArgsJson)[] calls)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < calls.Length; i++)
                {
                    string frame = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["choices"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["delta"] = new Dictionary<string, object?>
                                {
                                    ["tool_calls"] = new[]
                                    {
                                        new Dictionary<string, object?>
                                        {
                                            ["index"] = i,
                                            ["id"] = calls[i].Id,
                                            ["type"] = "function",
                                            ["function"] = new Dictionary<string, object?>
                                            {
                                                ["name"] = calls[i].Name,
                                                ["arguments"] = calls[i].ArgsJson
                                            }
                                        }
                                    }
                                },
                                ["finish_reason"] = i == calls.Length - 1 ? "tool_calls" : null
                            }
                        }
                    });
                    sb.Append("data: ").Append(frame).Append("\n\n");
                }

                sb.Append("data: [DONE]\n\n");
                return sb.ToString();
            }

            const string baseManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.multi-step-test",
              "name": "Multi Step",
              "script": { "entry": "logic.js" },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": "12,10,12,12",
                  "children": ["lbl"]
                },
                "lbl": { "type": "Text", "text": "${$computed.label}" }
              }
            }
            """;
            const string baseLogic = "export function onInit(ctx) {\n  ctx.computed.label = 'Before';\n}\n";

            string multiToolSse = BuildIncrementalMultiToolSse(
            [
                ("tc_em", "edit_manifest", """{"patchRoot":{"name":"Multi Step v2"}}"""),
                ("tc_ps", "patch_script", """{"hunks":[{"search":"ctx.computed.label = 'Before';","replace":"ctx.computed.label = 'After';"}]}"""),
                ("tc_fin", "finish_turn", """{"kind":"edit","reply":"Updated label and title.","changeSummary":"Update label"}""")
            ]);

            var handlerMulti = new CapturingHttpHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(multiToolSse, Encoding.UTF8, "text/event-stream")
            });
            var genMulti = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerMulti)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archMulti")));

            var multiProgress = new List<AiGenerationStreamProgress>();
            StudioTurnResult resMulti = await genMulti.RunStudioTurnAsync(
                "deepseek",
                "Update the label to After",
                currentManifestJson: baseManifest,
                currentLogicJs: baseLogic,
                uiLanguage: "zh-CN",
                onProgress: p => multiProgress.Add(p));

            Assert.True(resMulti.Success, resMulti.ErrorMessage);
            Assert.NotNull(resMulti.ExecutionSteps);
            Assert.DoesNotContain(resMulti.ExecutionSteps!, s => s.State == StudioStreamStepState.Warning);
            Assert.DoesNotContain(resMulti.ExecutionSteps!, s => s.State == StudioStreamStepState.Running);
            Assert.Contains(resMulti.ExecutionSteps!, s => s.Id == "r0-edit_manifest" && s.State == StudioStreamStepState.Completed);
            Assert.Contains(resMulti.ExecutionSteps!, s => s.Id == "r0-patch_script" && s.State == StudioStreamStepState.Completed);

            // 3. Truncated write_bundle with inline manifest.logicJs salvages both clean manifest and logic.js
            const string truncatedPollutedArgs = """{"manifest":{"schemaVersion":1,"id":"custom.inline-salvage","name":"Inline Salvage","script":{"entry":"logic.js"},"root":"main","elements":{"main":{"type":"Stack","padding":"12,10,12,12","children":["val"]},"val":{"type":"Text","text":"${$computed.msg}"}},"logicJs":"export function onInit(ctx) { ctx.computed.msg = 'RecoveredInline'; }"},"logicJs":"export function onInit(ctx) { ctx.compu""";
            string round1Salvage = BuildSingleToolCallWithFinishReasonSse("call_wb_inline", "write_bundle", truncatedPollutedArgs, "length");
            string round2FinishOnly = BuildToolCallsSse(
            [
                ("call_fin_inline", "finish_turn", """{"kind":"edit","reply":"Done.","changeSummary":"Create widget"}""")
            ]);

            var handlerSalvage = new CapturingHttpHandler((_, _, idx) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(idx == 1 ? round1Salvage : round2FinishOnly, Encoding.UTF8, "text/event-stream")
            });
            var genSalvage = new CustomWidgetAiGeneratorService(
                new DeskBoxAiService(credStore, prefsStore, new HttpClient(handlerSalvage)),
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archSalvage")));

            StudioTurnResult resSalvage = await genSalvage.RunStudioTurnAsync(
                "deepseek",
                "Create inline salvage widget",
                uiLanguage: "en-US");

            Assert.True(resSalvage.Success, resSalvage.ErrorMessage);
            Assert.DoesNotContain("\"logicJs\"", resSalvage.ManifestJson, StringComparison.Ordinal);
            Assert.Contains("RecoveredInline", resSalvage.LogicJs ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("Manifest and logic.js salvaged", handlerSalvage.RequestBodies[1], StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
