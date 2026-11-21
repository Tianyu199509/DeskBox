using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StudioModelSyncAndAgentDiagnosticsTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    [Fact]
    public void DeepSeekModelAndThinkingSync_SwitchingBetweenReasonerAndChat_UpdatesBothFieldsCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            var aiService = new DeskBoxAiService(credStore, prefsStore, new HttpClient());

            // Simulate user having enabled Thinking on DeepSeek -> defaults to deepseek-reasoner when classic pair is active.
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-sync",
                CustomModel: string.Empty,
                ThinkingMode: "enabled"));

            ResolvedAiInvocation initial = aiService.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-reasoner", initial.Model);
            Assert.Equal(AiThinkingMode.Enabled, initial.ThinkingMode);

            // Switching composer/settings back to deepseek-chat must disable thinking rather than snapping back to reasoner.
            credStore.UpdateStudioSettings("deepseek", customModel: "deepseek-chat");
            ResolvedAiInvocation switchedToChat = aiService.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-chat", switchedToChat.Model);
            Assert.Equal(AiThinkingMode.Disabled, switchedToChat.ThinkingMode);

            // Switching to deepseek-reasoner updates thinkingMode to enabled.
            credStore.UpdateStudioSettings("deepseek", customModel: "deepseek-reasoner");
            ResolvedAiInvocation switchedToReasoner = aiService.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-reasoner", switchedToReasoner.Model);
            Assert.Equal(AiThinkingMode.Enabled, switchedToReasoner.ThinkingMode);

            // Turning thinking off while on deepseek-reasoner flips back to deepseek-chat.
            credStore.UpdateStudioSettings("deepseek", thinkingMode: "disabled");
            ResolvedAiInvocation disabledThinking = aiService.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-chat", disabledThinking.Model);
            Assert.Equal(AiThinkingMode.Disabled, disabledThinking.ThinkingMode);

            // Turning thinking on while on deepseek-chat promotes cleanly to deepseek-reasoner, and clearing customModel restores default chat.
            credStore.UpdateStudioSettings("deepseek", thinkingMode: "enabled");
            Assert.Equal("deepseek-reasoner", aiService.ResolveEffectiveSettings("deepseek").Model);

            credStore.UpdateStudioSettings("deepseek", customModel: "");
            ResolvedAiInvocation resetToDefault = aiService.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-chat", resetToDefault.Model);
            Assert.Equal(AiThinkingMode.Disabled, resetToDefault.ThinkingMode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveredDeepSeekCatalog_PersistsAcrossReloads_AllowsFlashAndProThinking_AndPreservesThinkingOn8kRetry()
    {
        CustomWidgetAiProviderPreset deepseek = DeskBoxAiService.FindProviderPreset("deepseek");

        // Classic deepseek-chat <-> deepseek-reasoner sync keeps explicit model IDs cleanly.
        (string customModelChat, string thinkingChat) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
            deepseek,
            existingCustomModel: "deepseek-reasoner",
            existingThinkingWire: "enabled",
            newChosenModel: "deepseek-chat",
            newThinkingWire: "enabled");
        Assert.Equal("deepseek-chat", customModelChat);
        Assert.Equal("disabled", thinkingChat);

        (string customModelReasoner, string thinkingReasoner) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
            deepseek,
            existingCustomModel: "deepseek-chat",
            existingThinkingWire: "disabled",
            newChosenModel: "deepseek-chat",
            newThinkingWire: "enabled");
        Assert.Equal("deepseek-reasoner", customModelReasoner);
        Assert.Equal("enabled", thinkingReasoner);

        (string customModelBack, string thinkingBack) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
            deepseek,
            existingCustomModel: "deepseek-reasoner",
            existingThinkingWire: "enabled",
            newChosenModel: "deepseek-reasoner",
            newThinkingWire: "disabled");
        Assert.Equal("deepseek-chat", customModelBack);
        Assert.Equal("disabled", thinkingBack);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            int changedEvents = 0;
            credStore.CredentialsChanged += (_, _) => changedEvents++;

            var postedPayloads = new List<string>();
            using var httpClient = new HttpClient(new StubHttpHandler(async (req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                {
                    const string modelsJson = """{"data":[{"id":"deepseek-pro"},{"id":"deepseek-flash"}]}""";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
                    };
                }

                string body = await req.Content!.ReadAsStringAsync(ct);
                postedPayloads.Add(body);
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("max_tokens", out JsonElement maxTokEl) &&
                    maxTokEl.GetInt32() > 8192)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":{"message":"max_tokens must be <= 8192"}}""", Encoding.UTF8, "application/json")
                    };
                }

                const string okSse =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"kind\\\":\\\"edit\\\",\\\"reply\\\":\\\"已生成\\\",\\\"changeSummary\\\":\\\"已创建\\\",\\\"manifest\\\":{\\\"schemaVersion\\\":1,\\\"id\\\":\\\"deskbox.flash.thinking\\\",\\\"name\\\":\\\"Test\\\",\\\"icon\\\":\\\"✨\\\",\\\"defaultSize\\\":{\\\"width\\\":280,\\\"height\\\":180},\\\"root\\\":\\\"main\\\",\\\"elements\\\":{\\\"main\\\":{\\\"type\\\":\\\"Stack\\\",\\\"children\\\":[\\\"txt_ready\\\"]},\\\"txt_ready\\\":{\\\"type\\\":\\\"Text\\\",\\\"text\\\":\\\"Ready\\\"}}}}\"},\"finish_reason\":\"stop\"}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(okSse, Encoding.UTF8, "text/event-stream")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-catalog"));

            // Fetching /models persists discovered catalog and replaces absent preset defaults cleanly.
            AiModelListResult fetched = await aiService.FetchAvailableModelsAsync("deepseek", persistDiscoveredModels: true);
            Assert.True(fetched.Success);
            Assert.Equal(["deepseek-pro", "deepseek-flash"], fetched.Models);
            Assert.True(changedEvents >= 2);

            // Reloading from disk preserves FetchedModels; non-existent deepseek-chat / deepseek-reasoner are excluded.
            var reloadedStore = new CustomWidgetAiCredentialStore(tempDir);
            var reloadedAi = new DeskBoxAiService(reloadedStore, prefsStore, httpClient);
            IReadOnlyList<string> selectable = reloadedAi.GetSelectableModels("deepseek");
            Assert.Equal(["deepseek-pro", "deepseek-flash"], selectable);
            Assert.DoesNotContain("deepseek-chat", selectable);
            Assert.DoesNotContain("deepseek-reasoner", selectable);

            // Selecting deepseek-flash and enabling Deep Thinking keeps deepseek-flash + Enabled across Studio and wire payload.
            credStore.UpdateStudioSettings("deepseek", customModel: "deepseek-flash");
            credStore.UpdateStudioSettings("deepseek", thinkingMode: "enabled");
            Assert.Equal(["deepseek-pro", "deepseek-flash"], credStore.Get("deepseek")!.FetchedModels);

            ResolvedAiInvocation studioFlash = aiService.ResolveEffectiveSettingsForProfile(
                "deepseek",
                AiTaskProfile.StudioAuthoring,
                out _);
            Assert.Equal("deepseek-flash", studioFlash.Model);
            Assert.Equal(AiThinkingMode.Enabled, studioFlash.ThinkingMode);

            // Selecting deepseek-pro and enabling Deep Thinking also keeps deepseek-pro (no swap to deepseek-reasoner).
            credStore.UpdateStudioSettings("deepseek", customModel: "deepseek-pro");
            ResolvedAiInvocation studioPro = aiService.ResolveEffectiveSettingsForProfile(
                "deepseek",
                AiTaskProfile.StudioAuthoring,
                out _);
            Assert.Equal("deepseek-pro", studioPro.Model);
            Assert.Equal(AiThinkingMode.Enabled, studioPro.ThinkingMode);

            // Switch back to deepseek-flash + Enabled and verify HTTP 400 max_tokens retry keeps thinking.type="enabled".
            credStore.UpdateStudioSettings("deepseek", customModel: "deepseek-flash");
            var generator = new CustomWidgetAiGeneratorService(aiService, diagnosticArchive: new CustomWidgetDiagnosticArchive(tempDir));
            StudioTurnResult turnResult = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "创建一个极简时钟格子",
                uiLanguage: "zh-CN");
            Assert.True(turnResult.Success, turnResult.ErrorMessage);
            Assert.Equal(2, postedPayloads.Count);

            using JsonDocument req2 = JsonDocument.Parse(postedPayloads[1]);
            Assert.Equal("deepseek-flash", req2.RootElement.GetProperty("model").GetString());
            Assert.Equal(8192, req2.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("enabled", req2.RootElement.GetProperty("thinking").GetProperty("type").GetString());

            // Changing endpoint clears stale FetchedModels.
            credStore.UpdateStudioSettings("deepseek", customEndpoint: "https://other-relay.example/v1");
            Assert.Null(credStore.Get("deepseek")!.FetchedModels);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ManifestParser_DynamicStringChildrenBinding_ReturnsInvalidChildrenBindingError()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.dynamic.children",
          "name": "Dynamic Children Test",
          "root": "courseGrid",
          "elements": {
            "courseGrid": {
              "type": "Grid",
              "columns": "*,*,*",
              "children": "${$computed.gridCells}"
            }
          }
        }
        """;

        CustomWidgetParseResult result = CustomWidgetManifestParser.Parse(manifestJson);

        Assert.NotNull(result.Manifest);
        CustomWidgetValidationDiagnostic err = Assert.Single(
            result.Diagnostics,
            d => d.Code == "InvalidChildrenBinding" && d.IsError);
        Assert.Contains("ItemsGrid", err.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(CustomWidgetCompilerPipeline.Compile(manifestJson).HasHardErrors);
    }

    [Fact]
    public void NormalizeDomInPlace_NormalizesPopoverMapAndExtractsInlineCollectionRecords()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.normalize.popover.collections",
          "name": "Normalize Test",
          "collections": {
            "courses": [
              { "id": "c1", "title": "高等数学", "day": 1 },
              { "id": "c2", "title": "线性代数", "day": 3 }
            ]
          },
          "popovers": {
            "title": "编辑课程",
            "editCourseModal": {
              "root": "edit_course_stack"
            }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["addBtn"] },
            "addBtn": { "type": "Button", "text": "添加课程", "popover": "editCourseModal" },
            "edit_course_stack": { "type": "Stack", "children": ["txt_form"] },
            "txt_form": { "type": "Text", "text": "课程表单" }
          }
        }
        """;

        CustomWidgetParseResult rawParsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.NotNull(rawParsed.Manifest);
        Assert.Contains(rawParsed.Diagnostics, d => d.Code == "InlineCollectionRecordsInSchema" && !d.IsError);

        var engine = new StudioWorkspaceEngine(manifestJson);
        bool changed = engine.NormalizeDomInPlace();
        Assert.True(changed);

        // Extracted inline collection records into preview sampleDataJson.
        Assert.False(string.IsNullOrWhiteSpace(engine.ExtractedInlineSampleDataJson));
        JsonObject? sampleRoot = JsonNode.Parse(engine.ExtractedInlineSampleDataJson!) as JsonObject;
        Assert.NotNull(sampleRoot);
        JsonArray? extractedCourses = sampleRoot!["collections"]?["courses"] as JsonArray;
        Assert.NotNull(extractedCourses);
        Assert.Equal(2, extractedCourses!.Count);
        Assert.Equal("高等数学", extractedCourses[0]?["title"]?.GetValue<string>());

        string normalizedJson = engine.GetManifestJson(indented: true)!;
        JsonObject normalizedRoot = JsonNode.Parse(normalizedJson)!.AsObject();

        // Stray scalar "title" on root popovers object map is moved onto the sole popover entry.
        JsonObject popoversMap = Assert.IsType<JsonObject>(normalizedRoot["popovers"]);
        Assert.False(popoversMap.ContainsKey("title"));
        JsonObject modalEntry = Assert.IsType<JsonObject>(popoversMap["editCourseModal"]);
        Assert.Equal("编辑课程", modalEntry["title"]?.GetValue<string>());

        // Subsequent parse of normalized manifest has valid root popover and no InlineCollectionRecordsInSchema warning.
        CustomWidgetParseResult postNormalized = CustomWidgetManifestParser.Parse(normalizedJson);
        Assert.NotNull(postNormalized.Manifest);
        CustomWidgetNodeSpec parsedPopover = Assert.Single(
            postNormalized.Manifest!.Popovers,
            p => p.PopoverId == "editCourseModal" || p.Id == "editCourseModal");
        Assert.Equal("编辑课程", parsedPopover.Title);
        Assert.DoesNotContain(postNormalized.Diagnostics, d => d.Code == "InlineCollectionRecordsInSchema");
    }

    [Fact]
    public void ScriptAndManifestValidator_CatchesOrphanPopoversCollectionHallucinationsAndInvalidDispatch()
    {
        const string manifestWithOrphanPopovers = """
        {
          "schemaVersion": 1,
          "id": "test.script.semantics",
          "name": "Script Semantics Test",
          "collections": {
            "courses": {}
          },
          "popovers": {
            "validPop": {
              "title": "有效弹窗",
              "root": "pop_ok_txt"
            }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["b1", "b2", "b3"] },
            "b1": { "type": "Button", "text": "坏按钮", "popover": "missingPopover" },
            "b2": {
              "type": "Button",
              "text": "坏动作",
              "onClick": { "type": "popover.open", "id": "missingActionPopover" }
            },
            "b3": { "type": "Button", "text": "正常", "popover": "validPop" },
            "pop_ok_txt": { "type": "Text", "text": "OK" }
          }
        }
        """;

        const string badLogicJs = """
        export function onInit(ctx) {
          const directProp = ctx.collections.courses;
          const bracketProp = ctx.collections['courses'];
          ctx.collections.get('courses').push({ title: 'Physics' });
          ctx.dispatch({ type: 'popover.open', id: 'missingScriptPopover' });
          ctx.dispatch({ type: 'modal.open', id: 'fakeModal' });
          return { count: directProp?.length ?? 0 };
        }
        """;

        CustomWidgetWorkspaceValidationResult badResult = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestWithOrphanPopovers,
            badLogicJs,
            runOnInitDryRun: true);

        Assert.False(badResult.IsValid);
        var orphanErrors = badResult.Diagnostics
            .Where(d => d.Code == "UnknownPopoverTarget" && d.IsError)
            .ToList();
        Assert.Contains(orphanErrors, e => e.Message.Contains("missingPopover", StringComparison.Ordinal));
        Assert.Contains(orphanErrors, e => e.Message.Contains("missingActionPopover", StringComparison.Ordinal));
        Assert.Contains(orphanErrors, e => e.Message.Contains("missingScriptPopover", StringComparison.Ordinal));

        var collectionApiErrors = badResult.Diagnostics
            .Where(d => d.Code == "InvalidCollectionsApiUsage" && d.IsError)
            .ToList();
        Assert.Equal(2, collectionApiErrors.Count);
        Assert.Contains(collectionApiErrors, e => e.Path == "logic.js:2" && e.Message.Contains("at line 2", StringComparison.Ordinal));
        Assert.Contains(collectionApiErrors, e => e.Path == "logic.js:3" && e.Message.Contains("at line 3", StringComparison.Ordinal));

        Assert.Contains(
            badResult.Diagnostics,
            d => d.Code == "MutatingDetachedCollectionCopy" && d.IsError);

        Assert.Contains(
            badResult.Diagnostics,
            d => d.Code == "UnknownDispatchedActionType" &&
                 d.IsError &&
                 d.Message.Contains("modal.open", StringComparison.Ordinal));

        // Valid collections + valid dispatch usage produces zero errors.
        const string validManifest = """
        {
          "schemaVersion": 1,
          "id": "test.script.valid",
          "name": "Valid Script Test",
          "collections": {
            "courses": {}
          },
          "popovers": {
            "validPop": {
              "title": "有效弹窗",
              "root": "pop_ok_txt"
            }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["b3"] },
            "b3": { "type": "Button", "text": "正常", "popover": "validPop" },
            "pop_ok_txt": { "type": "Text", "text": "OK" }
          }
        }
        """;

        const string validLogicJs = """
        export function onInit(ctx) {
          const rows = ctx.collections.get('courses');
          if (rows.length === 0) {
            ctx.dispatch({ type: 'toast.show', message: 'Ready' });
          }
          return { rows };
        }
        export function addCourse(ctx) {
          ctx.collections.append('courses', { title: 'Math' });
          ctx.dispatch({ type: 'popover.open', id: 'validPop' });
        }
        """;

        CustomWidgetWorkspaceValidationResult goodResult = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            validManifest,
            validLogicJs,
            runOnInitDryRun: true);
        Assert.True(goodResult.IsValid, string.Join(" | ", goodResult.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
    }

    [Fact]
    public void CompilerPipeline_EmptyContainers_ProducesActionableLayoutEmptyContainerSoftDiagnostic()
    {
        const string manifestWithEmptyGridAndTab = """
        {
          "schemaVersion": 1,
          "id": "test.empty.container",
          "name": "Empty Container Test",
          "root": "mainTabs",
          "elements": {
            "mainTabs": {
              "type": "Tabs",
              "children": ["tab_timetable", "emptyTabPage"]
            },
            "tab_timetable": {
              "type": "Stack",
              "title": "课表",
              "children": ["timetableGrid"]
            },
            "timetableGrid": {
              "type": "Grid",
              "columns": "*,*,*",
              "children": []
            },
            "emptyTabPage": {
              "type": "Stack",
              "title": "空白页",
              "children": []
            }
          }
        }
        """;

        CustomWidgetCompilerResult result = CustomWidgetCompilerPipeline.Compile(
            manifestWithEmptyGridAndTab,
            rawLogicJs: null,
            cultureName: "zh-CN");

        Assert.False(result.HasHardErrors);
        Assert.Contains(
            result.ActionableSoftDiagnostics,
            d => d.Code == "layout.empty_container");
    }

    [Fact]
    public void CompiledRenderTruthSummary_AndOverviewSection_ExposeZeroChildGridsComputedLengthsAndPopovers()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.render.truth",
          "name": "Render Truth Test",
          "popovers": {
            "addCourse": {
              "title": "添加课程",
              "root": "pop_form_txt"
            }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["emptyGrid"] },
            "emptyGrid": {
              "type": "Grid",
              "columns": "*,*,*",
              "children": []
            },
            "pop_form_txt": { "type": "Text", "text": "Form" }
          }
        }
        """;

        const string logicJs = """
        export function onInit(ctx) {
          ctx.computed.cells = [
            { slot: 1, title: "高数" },
            { slot: 2, title: "英语" },
            { slot: 3, title: "物理" }
          ];
        }
        """;

        string summary = StudioWorkspaceEngine.BuildCompiledRenderTruthSummary(
            manifestJson,
            logicJs,
            isChinese: true);

        Assert.Contains("emptyGrid", summary, StringComparison.Ordinal);
        Assert.Contains("0 rendered children", summary, StringComparison.Ordinal);
        Assert.Contains("cells=array[3]", summary, StringComparison.Ordinal);
        Assert.Contains("addCourse", summary, StringComparison.Ordinal);

        var workspace = new StudioWorkspaceEngine(manifestJson, logicJs);
        string overview = workspace.ReadWorkspace(new StudioReadWorkspaceRequest(Section: "overview"));
        Assert.Contains("emptyGrid", overview, StringComparison.Ordinal);
        Assert.Contains("cells=array[3]", overview, StringComparison.Ordinal);
        Assert.Contains("addCourse", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchetypeCatalog_TimetableRequest_EmitsItemsGridTwoDimensionalPattern()
    {
        string blankCreationBlock = CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(
            currentManifestJson: null,
            userMessage: "帮我做一个课程表小组件",
            isChinese: true);

        Assert.Contains("ItemsGrid", blankCreationBlock, StringComparison.Ordinal);
        Assert.Contains("itemTemplate", blankCreationBlock, StringComparison.Ordinal);
        Assert.Contains("严禁写 `\"children\": \"${$computed.xxx}\"`", blankCreationBlock, StringComparison.Ordinal);

        string repairRefactorBlock = CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(
            currentManifestJson: "{\"schemaVersion\":1,\"id\":\"a\",\"name\":\"a\",\"root\":\"main\",\"elements\":{\"main\":{\"type\":\"Stack\"}}}",
            userMessage: "课表内没有空格，重做课程表格子",
            isChinese: true);

        Assert.Contains("ItemsGrid", repairRefactorBlock, StringComparison.Ordinal);
        Assert.Contains("itemTemplate", repairRefactorBlock, StringComparison.Ordinal);
    }
}
