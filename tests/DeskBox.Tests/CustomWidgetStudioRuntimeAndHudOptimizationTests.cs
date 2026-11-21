using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Controls.Studio;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetStudioRuntimeAndHudOptimizationTests
{
    [Fact]
    public void BuildChatRequestPayload_MultiTurnToolHistory_FollowsOpenAiAndDeepSeekContracts()
    {
        var aiService = new DeskBoxAiService();
        ResolvedAiInvocation deepSeekHybrid = aiService.ResolveEffectiveSettings(
            "deepseek",
            modelOverride: "deepseek-flash",
            apiKeyOverride: "test-key",
            thinkingOverride: AiThinkingMode.Enabled);

        var turns = new List<AiChatTurnMessage>
        {
            new(
                Role: "assistant",
                Content: null,
                ReasoningContent: null,
                ToolCalls:
                [
                    new AiToolCallSpec("call_1", StudioToolTransport.ToolEditManifest, "{\"upsertElements\":{}}")
                ]),
            new(
                Role: "tool",
                Content: "{\"applied\":true}",
                ToolCallId: "call_1",
                ToolName: StudioToolTransport.ToolEditManifest)
        };

        byte[] thinkingPayload = AiChatTransportClient.BuildChatRequestPayload(
            deepSeekHybrid,
            systemPrompt: "System",
            userPrompt: "Update chart",
            stream: true,
            includeThinkingParams: true,
            isProbe: false,
            structuredTurns: turns,
            includeStudioTools: true);

        using (JsonDocument doc = JsonDocument.Parse(thinkingPayload))
        {
            JsonElement messages = doc.RootElement.GetProperty("messages");
            Assert.Equal(4, messages.GetArrayLength());

            JsonElement assistantTurn = messages[2];
            Assert.Equal("assistant", assistantTurn.GetProperty("role").GetString());
            Assert.Equal(string.Empty, assistantTurn.GetProperty("content").GetString());
            Assert.Equal(string.Empty, assistantTurn.GetProperty("reasoning_content").GetString());
            Assert.Equal(1, assistantTurn.GetProperty("tool_calls").GetArrayLength());

            JsonElement toolTurn = messages[3];
            Assert.Equal("tool", toolTurn.GetProperty("role").GetString());
            Assert.Equal("call_1", toolTurn.GetProperty("tool_call_id").GetString());
            Assert.False(toolTurn.TryGetProperty("name", out _));
            Assert.Equal("{\"applied\":true}", toolTurn.GetProperty("content").GetString());
        }

        byte[] conservativeFallbackPayload = AiChatTransportClient.BuildChatRequestPayload(
            deepSeekHybrid,
            systemPrompt: "System",
            userPrompt: "Update chart",
            stream: true,
            includeThinkingParams: false,
            isProbe: false,
            structuredTurns: turns,
            includeStudioTools: true);

        using (JsonDocument fallbackDoc = JsonDocument.Parse(conservativeFallbackPayload))
        {
            JsonElement assistantTurn = fallbackDoc.RootElement.GetProperty("messages")[2];
            Assert.False(assistantTurn.TryGetProperty("reasoning_content", out _));
        }

        ResolvedAiInvocation thinkingDisabled = aiService.ResolveEffectiveSettings(
            "deepseek",
            modelOverride: "deepseek-flash",
            apiKeyOverride: "test-key",
            thinkingOverride: AiThinkingMode.Disabled);
        byte[] disabledPayload = AiChatTransportClient.BuildChatRequestPayload(
            thinkingDisabled,
            systemPrompt: "System",
            userPrompt: "Update chart",
            stream: true,
            includeThinkingParams: true,
            isProbe: false,
            structuredTurns: turns,
            includeStudioTools: true);

        using (JsonDocument disabledDoc = JsonDocument.Parse(disabledPayload))
        {
            JsonElement assistantTurn = disabledDoc.RootElement.GetProperty("messages")[2];
            Assert.False(assistantTurn.TryGetProperty("reasoning_content", out _));
        }
    }

    [Fact]
    public void WriteOpenAiToolsArray_And_ScriptPromptSection_RespectFoldingAndLinkedSymbols()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            StudioToolTransport.WriteOpenAiToolsArray(writer);
            writer.WriteEndObject();
        }

        using (JsonDocument doc = JsonDocument.Parse(stream.ToArray()))
        {
            var toolNames = doc.RootElement.GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("function").GetProperty("name").GetString()!)
                .ToList();
            Assert.Equal(7, toolNames.Count);
            Assert.Contains(StudioToolTransport.ToolReadWorkspace, toolNames);
            Assert.Contains(StudioToolTransport.ToolInspectCapabilities, toolNames);
            Assert.Contains(StudioToolTransport.ToolProbeHttp, toolNames);
            Assert.Contains(StudioToolTransport.ToolEditManifest, toolNames);
            Assert.Contains(StudioToolTransport.ToolPatchScript, toolNames);
            Assert.Contains(StudioToolTransport.ToolWriteBundle, toolNames);
            Assert.Contains(StudioToolTransport.ToolFinishTurn, toolNames);
        }

        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.folded-script-check",
          "name": "Folded Script Check",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["hero_arc_txt", "footer_txt"] },
            "hero_arc_txt": { "type": "Text", "text": "${$computed.heroArc}" },
            "footer_txt": { "type": "Text", "text": "${$computed.footerText}" }
          }
        }
        """;

        var jsBuilder = new StringBuilder();
        jsBuilder.AppendLine("function computeFooterNote() { return 'ok'; }");
        for (int i = 0; i < 16; i++)
        {
            jsBuilder.AppendLine($"function helperPad{i}(v) {{ return Number(v || 0) + {i}; }}");
        }
        jsBuilder.AppendLine("""
            function buildHeroArc(total) {
              const arcValue = Math.round(total * 10) / 10;
              return { heroArc: String(arcValue) };
            }
            export function onInit(ctx) {
              const res = buildHeroArc(42);
              ctx.computed.heroArc = res.heroArc;
              ctx.computed.footerText = computeFooterNote();
            }
            """);
        for (int i = 0; i < StudioWorkspaceEngine.ScriptFoldLineThreshold; i++)
        {
            jsBuilder.AppendLine($"// filler line {i}");
        }

        string section = StudioWorkspaceEngine.BuildScriptPromptSection(
            jsBuilder.ToString(),
            isChinese: false,
            targetNodePath: "#hero_arc_txt",
            userInstruction: "Change formatting",
            manifestJson: manifestJson);

        Assert.Contains("Focused Symbol: `buildHeroArc`", section, StringComparison.Ordinal);
        Assert.Contains("heroArc:", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleDataHydrator_RebasesStaleDates_AndDryRunFlagsZeroEffectPassB()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.stale-sample-test",
          "name": "Stale Sample Test",
          "collections": {
            "expenses": { "maxItems": 100 }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_total"] },
            "txt_total": { "type": "Text", "text": "${$computed.monthTotal}" }
          }
        }
        """;
        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;

        const string staleSampleJson = """
        {
          "collections": {
            "expenses": [
              { "title": "Lunch", "amount": 45, "date": "2023-10-25" },
              { "title": "Metro", "amount": 6, "date": "2023-10-24" }
            ]
          }
        }
        """;

        bool hydrated = CustomWidgetSampleDataHydrator.TryHydrate(
            manifest,
            staleSampleJson,
            out _,
            out _,
            out Dictionary<string, List<CustomWidgetRecord>> collections,
            out _);
        Assert.True(hydrated);
        List<CustomWidgetRecord> items = collections["expenses"];
        Assert.Equal(2, items.Count);
        string expectedToday = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string expectedYesterday = DateTimeOffset.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expectedToday, items[0].Fields["date"]);
        Assert.Equal(expectedYesterday, items[1].Fields["date"]);

        // Now verify ScriptSampleDataZeroEffect advisory when onInit filters out records and returns zero identical to Pass A
        const string zeroEffectManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.zero-effect-dryrun",
          "name": "Zero Effect Dry Run",
          "collections": {
            "expenses": { "maxItems": 100 }
          },
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_total"] },
            "txt_total": { "type": "Text", "text": "${$computed.monthTotal}" }
          }
        }
        """;
        const string logicJs = """
            export function onInit(ctx) {
              const rows = ctx.collections.get('expenses') || [];
              let total = 0;
              for (const r of rows) {
                if (r.wrongField === 'match-only') {
                  total += Number(r.amount || 0);
                }
              }
              ctx.computed.monthTotal = total;
            }
            """;

        CustomWidgetParseResult dryRunRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            zeroEffectManifestJson,
            logicJs,
            sampleDataJson: """{ "collections": { "expenses": [{ "title": "Coffee", "amount": 28 }] } }""");

        Assert.Contains(dryRunRes.Diagnostics, d => d.Code == "ScriptSampleDataZeroEffect" && !d.IsError);
    }

    [Fact]
    public async Task ViewModel_DeclarativeCollectionRemove_RecomputesOnInitDerivedTotals()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-vm-recompute-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.auto-recompute",
              "name": "Auto Recompute",
              "collections": {
                "expenses": { "maxItems": 50 }
              },
              "script": { "entry": "logic.js" },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["txt_total"] },
                "txt_total": { "type": "Text", "text": "${$computed.total}" }
              }
            }
            """;
            const string logicJs = """
                export function onInit(ctx) {
                  const rows = ctx.collections.get('expenses') || [];
                  let sum = 0;
                  for (const r of rows) {
                    sum += Number(r.amount || 0);
                  }
                  ctx.computed.total = String(sum);
                }
                """;

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "preview-recompute", WidgetKind = WidgetKind.Custom },
                localization,
                store: null,
                isPreviewOnly: true);

            vm.ApplyPreviewBundle(manifestJson, logicJs);
            bool seeded = vm.ApplyPreviewSampleDataJson("""
                {
                  "collections": {
                    "expenses": [
                      { "title": "A", "amount": 120 },
                      { "title": "B", "amount": 180 }
                    ]
                  }
                }
                """);
            Assert.True(seeded);
            Assert.Equal("300", CustomWidgetScriptEngine.ResolveComputedToken("total", vm.Computed));

            await vm.ExecuteActionsAsync([
                new CustomWidgetActionSpec(
                    Type: "collection.remove",
                    Collection: "expenses",
                    TargetId: "sample-1")
            ]);

            Assert.Equal("180", CustomWidgetScriptEngine.ResolveComputedToken("total", vm.Computed));
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
    public void DomNormalization_TabsAndInlineActionRow_AndHealthAnalyzerOverflowAndPopoverRules()
    {
        const string unnormalizedManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.norm-and-health",
          "name": "Norm & Health",
          "defaultSize": { "width": 280, "height": 460 },
          "state": {
            "tab": "missingTab",
            "amt": ""
          },
          "collections": {
            "categories": { "maxItems": 5 },
            "records": { "maxItems": 50 }
          },
          "popovers": {
            "addForm": {
              "title": "Add Expense",
              "root": "pop_add_txt"
            }
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "spacing": 8,
              "children": ["hero_card", "composer_row", "main_tabs"]
            },
            "hero_card": {
              "type": "Card",
              "children": ["hero_title", "hero_gauge"]
            },
            "hero_title": { "type": "Text", "text": "Monthly Spend", "style": "subtitle" },
            "hero_gauge": { "type": "RingGauge", "progress": 50, "size": 140, "title": "Budget" },
            "composer_row": {
              "type": "Stack",
              "orientation": "horizontal",
              "spacing": 6,
              "children": ["inp_amt", "btn_log"]
            },
            "inp_amt": { "type": "Input", "bind": "state.amt", "placeholder": "Amount" },
            "btn_log": { "type": "Button", "text": "Log", "popover": "addForm" },
            "main_tabs": {
              "type": "Tabs",
              "bind": "state.tab",
              "children": ["tab_summary", "tab_records"]
            },
            "tab_summary": {
              "type": "Stack",
              "title": "Summary",
              "value": "summary",
              "children": ["bar_categories"]
            },
            "bar_categories": { "type": "BarList", "items": "categories", "titleField": "name", "valueField": "val", "maxItems": 6 },
            "tab_records": {
              "type": "Stack",
              "title": "Records",
              "value": "records",
              "children": ["list_records"]
            },
            "list_records": { "type": "List", "items": "records", "itemTemplate": "record_item_txt" },
            "record_item_txt": { "type": "Text", "text": "${$item.title}" },
            "pop_add_txt": { "type": "Text", "text": "Form" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(unnormalizedManifest);
        Assert.True(engine.NormalizeDomInPlace());
        string normalizedJson = engine.GetManifestJson(indented: true)!;

        using (JsonDocument normDoc = JsonDocument.Parse(normalizedJson))
        {
            Assert.Equal("summary", normDoc.RootElement.GetProperty("state").GetProperty("tab").GetString());
            JsonElement elements = normDoc.RootElement.GetProperty("elements");
            JsonElement composerRow = elements.GetProperty("composer_row");
            Assert.Equal("Grid", composerRow.GetProperty("type").GetString());
            Assert.Equal("*,Auto", composerRow.GetProperty("columns").GetString());
            Assert.Equal(6, composerRow.GetProperty("columnSpacing").GetInt32());
            Assert.Equal(0, elements.GetProperty("inp_amt").GetProperty("column").GetInt32());
            Assert.Equal(1, elements.GetProperty("btn_log").GetProperty("column").GetInt32());
        }

        CustomWidgetManifest parsedManifest = CustomWidgetManifestParser.Parse(normalizedJson).Manifest!;
        Assert.False(CustomWidgetHealthAnalyzer.HasRootFlexibleListOrScroll(parsedManifest.View));

        CustomWidgetHealthReport health = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            normalizedJson,
            parsedManifest,
            storePayload: null,
            cultureName: "zh-CN");
        Assert.Contains(health.Issues, i => i.Code == "layout.vertical_overflow");

        IReadOnlyList<CustomWidgetHealthIssue> interactionIssues =
            CustomWidgetHealthAnalyzer.DetectDeadControlsAndUnreadMutations(parsedManifest, "zh-CN");
        Assert.Contains(interactionIssues, i => i.Code == "interaction.inline_input_opens_popover");
    }

    [Fact]
    public void KeyValueRowKeyAlias_And_NodeFlexWeight_ParseAndNormalizeCleanly()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.stock-detail",
          "name": "Stock",
          "popovers": {
            "detail": {
              "root": "detail_stack"
            }
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "direction": "horizontal",
              "children": ["col_name", "col_price"]
            },
            "col_name": { "type": "Stack", "flex": 2, "children": ["txt_name"] },
            "txt_name": { "type": "Text", "text": "贵州茅台" },
            "col_price": { "type": "Stack", "children": ["txt_price"] },
            "txt_price": { "type": "Text", "text": "1755.00" },
            "detail_stack": { "type": "Stack", "children": ["kv_open", "kv_close"] },
            "kv_open": { "type": "KeyValueRow", "key": "今开", "value": "1750.00" },
            "kv_close": { "type": "KeyValueRow", "label": "昨收", "value": "1740.00" }
          }
        }
        """;

        CustomWidgetManifest rawParsed = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;
        Assert.Equal("今开", rawParsed.Popovers[0].Children[0].Children[0].Title);
        Assert.Equal(2.0, rawParsed.View.Children[0].Flex);
        Assert.Null(rawParsed.View.Children[1].Flex);

        var engine = new StudioWorkspaceEngine(manifestJson);
        Assert.True(engine.NormalizeDomInPlace());
        using JsonDocument doc = JsonDocument.Parse(engine.GetManifestJson(indented: false)!);
        JsonElement firstKv = doc.RootElement
            .GetProperty("elements")
            .GetProperty("kv_open");
        Assert.Equal("今开", firstKv.GetProperty("label").GetString());
        Assert.False(firstKv.TryGetProperty("key", out _));
    }

    [Fact]
    public void ObjectMapPopover_ValidatorElementPathsAndFlatEditor_UpdatePopoverElementsCleanly()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.stock-popovers",
          "name": "Stock",
          "state": { "draftCode": "" },
          "script": { "entry": "logic.js" },
          "popovers": {
            "detail": { "root": "pop_detail_stack" },
            "addStock": { "root": "pop_add_stack" }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["btn_open_add"] },
            "btn_open_add": { "type": "Button", "text": "+", "popover": "addStock" },
            "pop_detail_stack": { "type": "Stack", "children": ["txt_detail"] },
            "txt_detail": { "type": "Text", "text": "Detail" },
            "pop_add_stack": { "type": "Stack", "children": ["inp_draft_code", "btn_add_submit"] },
            "inp_draft_code": { "type": "Input", "bind": "state.draftCode" },
            "btn_add_submit": { "type": "Button", "text": "Add", "onClick": { "type": "script.run", "fn": "missingHandler" } }
          }
        }
        """;

        IReadOnlyList<CustomWidgetValidationDiagnostic> scriptDiags = CustomWidgetScriptValidator
            .ValidateWorkspaceBundle(manifestJson, "export function onInit(ctx) {}", sampleDataJson: null)
            .Diagnostics;
        CustomWidgetValidationDiagnostic missingFn = Assert.Single(scriptDiags, d => d.Message.Contains("missingHandler", StringComparison.Ordinal));
        Assert.StartsWith("#btn_add_submit", missingFn.Path, StringComparison.Ordinal);

        // Flat editor updates popover elements directly by element ID
        var engine = new StudioWorkspaceEngine(manifestJson);
        StudioWorkspaceOpResult r1 = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["inp_draft_code"] = new JsonObject
                {
                    ["type"] = "Input",
                    ["bind"] = "state.draftCode",
                    ["placeholder"] = "600519"
                }
            }));
        Assert.True(r1.Succeeded, r1.ErrorMessage);

        StudioWorkspaceOpResult r2 = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["btn_add_submit"] = new JsonObject
                {
                    ["type"] = "Button",
                    ["text"] = "加入自选",
                    ["onClick"] = new JsonObject { ["type"] = "script.run", ["fn"] = "missingHandler" }
                }
            }));
        Assert.True(r2.Succeeded, r2.ErrorMessage);

        using JsonDocument patched = JsonDocument.Parse(engine.GetManifestJson(indented: false)!);
        JsonElement patchedElements = patched.RootElement.GetProperty("elements");
        Assert.Equal("600519", patchedElements.GetProperty("inp_draft_code").GetProperty("placeholder").GetString());
        Assert.Equal("加入自选", patchedElements.GetProperty("btn_add_submit").GetProperty("text").GetString());
    }

    [Fact]
    public void FilterSearchToolbar_DoesNotTriggerInlineInputPopoverFalsePositive_And_ZeroEffectIsActionable()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.stock-watch",
          "name": "Stock Watch",
          "state": { "keyword": "", "draftCode": "" },
          "collections": { "quotes": { "maxItems": 50 } },
          "script": { "entry": "logic.js" },
          "popovers": {
            "addStock": { "root": "pop_add_stack" }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["search_bar", "list_quotes"] },
            "search_bar": {
              "type": "Stack",
              "direction": "horizontal",
              "children": ["inp_keyword", "btn_open_add"]
            },
            "inp_keyword": { "type": "Input", "bind": "state.keyword", "placeholder": "搜索名称或代码", "flex": 1 },
            "btn_open_add": { "type": "Button", "text": "＋", "popover": "addStock" },
            "list_quotes": {
              "type": "List",
              "items": "${$computed.rows}",
              "filter": "${!state.keyword || ($item.name || '').includes(state.keyword)}",
              "itemTemplate": "quote_item_txt"
            },
            "quote_item_txt": { "type": "Text", "text": "${$item.name}" },
            "pop_add_stack": { "type": "Stack", "children": ["inp_draft_code"] },
            "inp_draft_code": { "type": "Input", "bind": "state.draftCode", "onSubmit": { "type": "script.run", "fn": "addStock" } }
          }
        }
        """;

        const string mismatchedLogicJs = """
        export function onInit(ctx) {
          const quotes = ctx.collections.get('quotes') || [];
          ctx.computed.rows = quotes.filter(q => q.code === '1.600519');
        }
        export function addStock(ctx) {}
        """;

        const string sampleDataJson = """
        {
          "collections": {
            "quotes": [{ "id": "q1", "code": "600519", "name": "贵州茅台" }]
          }
        }
        """;

        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;
        IReadOnlyList<CustomWidgetHealthIssue> deadIssues =
            CustomWidgetHealthAnalyzer.DetectDeadControlsAndUnreadMutations(manifest, "zh-CN", mismatchedLogicJs);
        Assert.DoesNotContain(deadIssues, i => i.Code == "interaction.inline_input_opens_popover");

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            mismatchedLogicJs,
            locales: null,
            sampleDataJson: sampleDataJson,
            cultureName: "zh-CN");
        Assert.Contains(compiled.ActionableSoftDiagnostics, d => d.Code == "ScriptSampleDataZeroEffect");
    }

    [Fact]
    public void ClarifyCardPrompt_LenientToolParsing_And_UnifiedStageRouter_HandleEdgeCases()
    {
        // 1. Clarify card uses readable Labels instead of internal short codes (api / cn / list)
        var qSource = new StudioClarifyQuestionSpec(
            "source",
            "行情来源",
            [new StudioClarifyOptionSpec("api", "公开接口实时拉取", Recommended: true)]);
        var qView = new StudioClarifyQuestionSpec(
            "layout",
            "核心视图",
            [new StudioClarifyOptionSpec("list", "自选列表+涨跌条", Recommended: true)]);
        var state1 = new StudioClarifyCardBuilder.QuestionSelectionState { Spec = qSource };
        state1.SelectedOptionIndices.Add(0);
        var state2 = new StudioClarifyCardBuilder.QuestionSelectionState { Spec = qView };
        state2.SelectedOptionIndices.Add(0);

        (string composedPrompt, string receipt) = StudioClarifyCardBuilder.ComposeConfirmedPlanPrompt([state1, state2], isChinese: true);
        Assert.Contains("公开接口实时拉取", composedPrompt, StringComparison.Ordinal);
        Assert.Contains("自选列表+涨跌条", composedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("：api", composedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("：list", composedPrompt, StringComparison.Ordinal);
        Assert.Contains("公开接口实时拉取", receipt, StringComparison.Ordinal);

        // 2. Lenient inspect_capabilities & read_workspace argument parsing
        Assert.True(StudioToolTransport.TryParseInspectOrProbeArgs(
            StudioToolTransport.ToolInspectCapabilities,
            "",
            out StudioAgenticActionRequest? emptyInspect,
            out _));
        Assert.Empty(emptyInspect!.CapabilityIds);

        Assert.True(StudioToolTransport.TryParseInspectOrProbeArgs(
            StudioToolTransport.ToolInspectCapabilities,
            """{"capabilityIds":"forms-inputs, widget-settings"}""",
            out StudioAgenticActionRequest? aliasInspect,
            out _));
        Assert.Equal(["forms-inputs", "widget-settings"], aliasInspect!.CapabilityIds);

        Assert.True(StudioToolTransport.TryParseInspectOrProbeArgs(
            StudioToolTransport.ToolInspectCapabilities,
            """["collections-lists","popover-and-markdown"]""",
            out StudioAgenticActionRequest? arrayInspect,
            out _));
        Assert.Equal(["collections-lists", "popover-and-markdown"], arrayInspect!.CapabilityIds);

        Assert.True(StudioToolTransport.TryParseReadWorkspaceArgs("", out StudioReadWorkspaceRequest? defaultRead, out _));
        Assert.Equal("overview", defaultRead!.Section);

        // 3. Classic deepseek-reasoner swaps to deepseek-chat + Disabled on repair/post-mutation rounds
        var aiService = new DeskBoxAiService();
        ResolvedAiInvocation classicReasoner = aiService.ResolveEffectiveSettings(
            "deepseek",
            modelOverride: "deepseek-reasoner",
            apiKeyOverride: "sk-test",
            thinkingOverride: AiThinkingMode.Enabled);
        ResolvedAiInvocation downgraded = StudioTurnStageRouter.ResolveRoundInvocation(
            classicReasoner,
            structuredTurnCount: 2,
            workspaceMutated: true,
            isRepairRound: true,
            synthesisThinkingConsumed: true,
            thinkingParamsUnsupportedOnRoute: false,
            fetchedModels: ["deepseek-chat", "deepseek-reasoner"],
            out bool allowSynth);
        Assert.False(allowSynth);
        Assert.Equal("deepseek-chat", downgraded.Model);
        Assert.Equal(AiThinkingMode.Disabled, downgraded.ThinkingMode);

        // 4. Exhausted route forces Disabled on adaptive Studio thinking mode
        ResolvedAiInvocation hybridFlash = aiService.ResolveEffectiveSettings(
            "deepseek",
            modelOverride: "deepseek-flash",
            apiKeyOverride: "sk-test",
            thinkingOverride: AiThinkingMode.Auto);
        AiThinkingMode exhaustedMode = StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
            hybridFlash,
            "做一个复杂的实时多指数股票与自选监控看板，支持分组排序与涨跌分布图",
            hasExistingManifest: false,
            targetNodePath: null,
            recentMessages: null,
            isPostMutationRound: false,
            thinkingParamsUnsupportedOnRoute: false,
            thinkingExhaustedOnRoute: true);
        Assert.Equal(AiThinkingMode.Disabled, exhaustedMode);
    }

    [Fact]
    public void UnexportedTopLevelScriptFunctions_AreAutoPromoted_WithoutTouchingHelpersOrDuplicateExports()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "minAppVersion": "1.6.0",
          "id": "custom.random-recipe-wheel",
          "name": "随机菜谱转盘",
          "icon": "🍳",
          "defaultSize": { "width": 280, "height": 380 },
          "state": { "spinning": false, "resultName": "待抽取" },
          "script": { "entry": "logic.js", "tickIntervalMs": 60 },
          "capsule": {
            "title": "随机菜谱",
            "summary": "${$computed.summary}",
            "primaryAction": {
              "icon": "\uE72C",
              "tooltip": "转盘抽一道",
              "onClick": { "type": "script.run", "fn": "spin" }
            }
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "spacing": 8,
              "children": ["txt_summary", "btn_spin", "btn_reroll", "btn_history"]
            },
            "txt_summary": { "type": "Text", "text": "${$computed.summary}" },
            "btn_spin": {
              "type": "Button",
              "text": "抽一道",
              "onClick": { "type": "script.run", "fn": "spin" }
            },
            "btn_reroll": {
              "type": "Button",
              "text": "换一道",
              "onClick": { "type": "script.run", "fn": "reroll" }
            },
            "btn_history": {
              "type": "Button",
              "text": "历史",
              "onClick": { "type": "script.run", "fn": "pickFromHistory" }
            }
          }
        }
        """;

        const string unexportedLogicJs = """
        const RECIPES = ['番茄炒蛋', '可乐鸡翅'];

        function formatLabel(name) {
          function spin() { return 'nested-should-stay-private'; }
          return `${name} · 推荐`;
        }

        function onInit(ctx) {
          ctx.computed.summary = formatLabel(RECIPES[0]);
        }

        function onTick(ctx) {
          if (!ctx.state.spinning) return;
        }

        function spin(ctx) {
          ctx.state.resultName = RECIPES[1];
          ctx.computed.summary = formatLabel(ctx.state.resultName);
        }

        const reroll = (ctx) => {
          spin(ctx);
        };

        function pickFromHistory(ctx) {
          ctx.computed.summary = formatLabel(RECIPES[0]);
        }

        export { pickFromHistory };
        """;

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            unexportedLogicJs,
            locales: null,
            cultureName: "zh-CN");

        Assert.False(compiled.HasHardErrors);
        Assert.NotNull(compiled.NormalizedLogicJs);
        Assert.Contains("export function onInit(ctx)", compiled.NormalizedLogicJs, StringComparison.Ordinal);
        Assert.Contains("export function onTick(ctx)", compiled.NormalizedLogicJs, StringComparison.Ordinal);
        Assert.Contains("export function spin(ctx)", compiled.NormalizedLogicJs, StringComparison.Ordinal);
        Assert.Contains("export const reroll = (ctx) =>", compiled.NormalizedLogicJs, StringComparison.Ordinal);
        // Internal helper and nested inner function must remain non-exported, and named export must not duplicate
        Assert.Contains("\nfunction formatLabel(name)", "\n" + compiled.NormalizedLogicJs, StringComparison.Ordinal);
        Assert.Contains("  function spin() { return 'nested-should-stay-private'; }", compiled.NormalizedLogicJs, StringComparison.Ordinal);
        Assert.Contains("\nfunction pickFromHistory(ctx)", "\n" + compiled.NormalizedLogicJs, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickJsBridge_SupportsMapAccessors_NestedSysTelemetry_AndCollectionHelpers()
    {
        const string logicJs = """
        export function onInit(ctx) {
          const prev = Number(ctx.state.get('count', 3));
          const goal = Number(ctx.settings.get('goal', '8'));
          const delta = ctx.args.get('delta', 0);
          ctx.state.set('count', prev + delta);
          if (ctx.state.has('obsolete')) {
            ctx.state.delete('obsolete');
          }
          const cpu = ctx.env.sys.cpu.percent;
          const mem = ctx.sys.memory.percent;
          ctx.computed.set('headline', `${ctx.state.get('count')}/${goal} · CPU ${cpu}% · MEM ${mem}%`);
          if (ctx.collections.count('logs') > 1) {
            ctx.collections.delete('logs', 'drop-me');
          }
        }
        """;

        CustomWidgetScriptCompileResult compiled = CustomWidgetScriptEngine.Compile(logicJs);
        Assert.True(compiled.IsSuccess, compiled.ErrorMessage);

        using JsonDocument deltaDoc = JsonDocument.Parse("2");
        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compiled.Compiled!,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                State = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = "3",
                    ["obsolete"] = "old"
                },
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["goal"] = "8"
                },
                Args = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["delta"] = "2"
                },
                ArgsJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["delta"] = deltaDoc.RootElement.Clone()
                },
                Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
                {
                    ["logs"] =
                    [
                        new CustomWidgetRecord { Id = "keep-me", Fields = new Dictionary<string, string> { ["title"] = "Keep" } },
                        new CustomWidgetRecord { Id = "drop-me", Fields = new Dictionary<string, string> { ["title"] = "Drop" } }
                    ]
                },
                SystemMetrics = new CustomWidgetSystemMetricsSnapshot
                {
                    CpuPercent = 91.4,
                    MemoryPercent = 87.6
                }
            });

        Assert.True(exec.IsSuccess, exec.ErrorMessage);
        Assert.Equal("5", exec.StateSet["count"]);
        Assert.Contains("obsolete", exec.StateRemoved);
        Assert.DoesNotContain("get", exec.StateSet.Keys);
        Assert.DoesNotContain("set", exec.StateSet.Keys);
        Assert.DoesNotContain("get", exec.Computed.Keys);
        Assert.Equal("5/8 · CPU 91.4% · MEM 87.6%", exec.Computed["headline"].GetString());
        Assert.Single(exec.DirtyCollections["logs"]);
        Assert.Equal("keep-me", exec.DirtyCollections["logs"][0].Id);
    }

    [Fact]
    public void ManifestParser_SupportsSingularArgObject_NumericArrayArgs_AndSingularChild()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.arg-and-child-compat",
          "name": "Arg Compat",
          "state": { "count": 3, "selectedTitle": "" },
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["card_minus", "btn_open"] },
            "card_minus": { "type": "Card", "child": "btn_minus" },
            "btn_minus": {
              "type": "Button",
              "text": "-1",
              "onClick": { "type": "script.run", "fn": "change", "args": [ -1 ] }
            },
            "btn_open": {
              "type": "Button",
              "text": "Open",
              "onClick": {
                "type": "script.run",
                "fn": "openDetail",
                "arg": { "title": "Release Notes", "featured": true }
              }
            }
          }
        }
        """;

        const string logicJs = """
        export function change(ctx) {
          const delta = ctx.args?.[0] ?? 0;
          if (typeof delta !== 'number') throw new Error('Expected numeric arg');
          ctx.state.count = Number(ctx.state.count || 0) + delta;
        }
        export function openDetail(ctx) {
          ctx.state.selectedTitle = `${ctx.args.title}:${ctx.args.featured === true}`;
        }
        """;

        CustomWidgetWorkspaceValidationResult val = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            logicJs,
            strictAuthoring: true);
        Assert.True(val.IsValid, string.Join("; ", val.Diagnostics.Select(d => d.Message)));

        CustomWidgetNodeSpec card = val.Manifest!.View!.Children[0];
        Assert.Single(card.Children);
        CustomWidgetActionSpec minusAct = card.Children[0].OnClick![0];
        Assert.Equal("-1", minusAct.Args!["0"]);
        Assert.Equal(JsonValueKind.Number, minusAct.ArgsJson!["0"].ValueKind);

        CustomWidgetScriptExecutionResult res = CustomWidgetScriptEngine.Execute(
            val.CompiledScript!,
            "change",
            new CustomWidgetScriptExecutionInput
            {
                State = new Dictionary<string, string> { ["count"] = "3" },
                Args = minusAct.Args,
                ArgsJson = minusAct.ArgsJson
            });
        Assert.True(res.IsSuccess, res.ErrorMessage);
        Assert.Equal("2", res.StateSet["count"]);

        CustomWidgetActionSpec openAct = val.Manifest.View.Children[1].OnClick![0];
        CustomWidgetScriptExecutionResult openRes = CustomWidgetScriptEngine.Execute(
            val.CompiledScript!,
            "openDetail",
            new CustomWidgetScriptExecutionInput
            {
                State = new Dictionary<string, string> { ["selectedTitle"] = "" },
                Args = openAct.Args,
                ArgsJson = openAct.ArgsJson
            });
        Assert.True(openRes.IsSuccess, openRes.ErrorMessage);
        Assert.Equal("Release Notes:true", openRes.StateSet["selectedTitle"]);
    }

    [Fact]
    public void WorkspaceGuardrails_PreservesInlinePopoverTriggerChildren_ShiftsOneBasedGrid_AndNormalizesSettingsSchema()
    {
        const string rawManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.guardrail-dialects",
          "name": "Guardrail Dialects",
          "settings": {
            "properties": {
              "goal": { "type": "NumberBox", "title": "每日目标", "value": 8 }
            }
          },
          "popovers": {
            "btcDetail": {
              "title": "BTC Detail",
              "root": "btc_detail_txt"
            }
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["btc_card", "form_grid"] },
            "btc_card": { "type": "Card", "children": ["btc_trigger"] },
            "btc_trigger": { "type": "Popover", "popoverId": "btcDetail", "children": ["btc_price"] },
            "btc_price": { "type": "Text", "text": "BTC $95,000", "weight": "semibold" },
            "btc_detail_txt": { "type": "Text", "text": "24h High/Low" },
            "form_grid": {
              "type": "Grid",
              "columns": "*,*,*",
              "rows": "Auto,Auto",
              "children": ["cell_a", "cell_b", "cell_c"]
            },
            "cell_a": { "type": "Text", "text": "A", "column": 1, "row": 1 },
            "cell_b": { "type": "Text", "text": "B", "column": 2, "row": 1 },
            "cell_c": { "type": "Text", "text": "C", "column": 3, "row": 2 }
          }
        }
        """;

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            rawManifestJson,
            rawLogicJs: null,
            cultureName: "zh-CN");

        Assert.False(compiled.HasHardErrors, string.Join("; ", compiled.HardDiagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(compiled.ActionableSoftDiagnostics, d => d.Code == "layout.empty_container");

        CustomWidgetManifest manifest = compiled.Manifest!;
        Assert.Single(manifest.Settings);
        Assert.Equal("goal", manifest.Settings[0].Key);
        Assert.Equal("number", manifest.Settings[0].Type);
        Assert.Equal("每日目标", manifest.Settings[0].Label);
        Assert.Equal("8", manifest.Settings[0].DefaultValue);

        CustomWidgetNodeSpec btcCard = manifest.View!.Children[0];
        Assert.NotEmpty(btcCard.Children);
        Assert.Equal("btcDetail", btcCard.Children[0].PopoverId);
        Assert.Single(btcCard.Children[0].Children);

        CustomWidgetNodeSpec grid = manifest.View.Children[1];
        Assert.Equal(0, grid.Children[0].Column);
        Assert.Equal(0, grid.Children[0].Row);
        Assert.Equal(1, grid.Children[1].Column);
        Assert.Equal(0, grid.Children[1].Row);
        Assert.Equal(2, grid.Children[2].Column);
        Assert.Equal(1, grid.Children[2].Row);
    }

    [Fact]
    public void ValidatorAndHealth_BlocksTimerGlobalsAndStrictHandlerCrash_AndFlagsEmptyCardAndStaticLoadingText()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.strict-checks",
          "name": "Strict Checks",
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["empty_card", "txt_loading", "btn_spin"] },
            "empty_card": { "type": "Card", "children": [] },
            "txt_loading": { "type": "Text", "text": "加载中…" },
            "btn_spin": { "type": "Button", "text": "Spin", "onClick": { "type": "script.run", "fn": "spin" } }
          }
        }
        """;

        const string timerJs = """
        export function spin(ctx) {
          setInterval(() => { ctx.state.angle = 10; }, 50);
        }
        """;

        CustomWidgetCompilerResult timerRes = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            timerJs,
            normalizeDom: false,
            cultureName: "zh-CN",
            strictAuthoring: true);
        Assert.Contains(timerRes.HardDiagnostics, d => d.Code == "ForbiddenBrowserTimerApi");

        const string crashJs = """
        export function spin(ctx) {
          const x = null;
          ctx.state.out = x.nonExistentProp;
        }
        """;

        CustomWidgetWorkspaceValidationResult compatRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            crashJs,
            strictAuthoring: false);
        Assert.True(compatRes.IsValid);
        Assert.Contains(compatRes.Diagnostics, d => d.Code == "ScriptHandlerRuntimeError" && !d.IsError);

        CustomWidgetCompilerResult strictRes = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            crashJs,
            normalizeDom: false,
            cultureName: "zh-CN",
            strictAuthoring: true);
        Assert.Contains(strictRes.HardDiagnostics, d => d.Code == "ScriptHandlerRuntimeError");
        Assert.Contains(strictRes.ActionableSoftDiagnostics, d => d.Code == "layout.empty_container");
        Assert.Contains(strictRes.ActionableSoftDiagnostics, d => d.Code == "view.unconditional_loading_text");
    }

    [Fact]
    public void ToolTransport_Patcher_AndAdaptiveThinking_HandleSplitBundleAndStageRouting()
    {
        bool parsedSplit = StudioToolTransport.TryParseWriteBundleArgs(
            """{"logicJs":"export function onInit(ctx) { ctx.state.ok = true; }"}""",
            out string wbManifest,
            out string? wbLogicJs,
            out _,
            out _,
            out _,
            out string? wbErr);
        Assert.True(parsedSplit, wbErr);
        Assert.Equal(string.Empty, wbManifest);
        Assert.Contains("onInit", wbLogicJs, StringComparison.Ordinal);

        var ws = new StudioWorkspaceEngine("""
        {
          "schemaVersion": 1,
          "id": "custom.edit-upsert-prop",
          "name": "Edit",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["hero"] },
            "hero": { "type": "Text", "text": "Hello" }
          }
        }
        """);
        StudioWorkspaceOpResult opRes = ws.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["hero"] = new JsonObject
                {
                    ["type"] = "Text",
                    ["text"] = "Hello",
                    ["fontWeight"] = "bold"
                }
            }));
        Assert.True(opRes.Succeeded, opRes.ErrorMessage);
        Assert.Contains("\"fontWeight\": \"bold\"", ws.GetManifestJson(indented: true)!, StringComparison.Ordinal);

        CustomWidgetAiProviderPreset glmPreset = AiProviderCatalog.SupportedProviders.First(p => p.Id == "glm");
        var baseAutoInvocation = new ResolvedAiInvocation(
            glmPreset,
            glmPreset.DefaultEndpoint,
            "glm-5.3",
            "sk-test",
            AiThinkingMode.Auto,
            UsesCustomEndpoint: false);

        // Minimalist self-contained Turn 0 -> Disabled
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseAutoInvocation,
                "做一个极简每日喝水打卡小工具，默认目标 8 杯",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: [],
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));

        // Normal incremental edit on existing manifest -> Disabled
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseAutoInvocation,
                "在设置里增加旋转速度档位，并支持一键清空历史",
                hasExistingManifest: true,
                targetNodePath: null,
                recentMessages: [],
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));

        // Cross-node symptom bug report in Auto mode also stays fast non-thinking (explicit Enabled still honored)
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseAutoInvocation,
                "这里搜索没生效，点击也没有反应",
                hasExistingManifest: true,
                targetNodePath: "#inp_search",
                recentMessages: [],
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));
        Assert.Equal(
            AiThinkingMode.Enabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseAutoInvocation with { ThinkingMode = AiThinkingMode.Enabled },
                "这里搜索没生效，点击也没有反应",
                hasExistingManifest: true,
                targetNodePath: "#inp_search",
                recentMessages: [],
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));
    }

    [Fact]
    public void DuplicateJsonKeys_AndCollectionArrayAppend_DoNotThrowOrCorruptItems()
    {
        const string duplicateKeysManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.dup-keys",
          "name": "Dup Keys",
          "collections": { "dishes": { "maxItems": 20 } },
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Card",
              "spacing": 8,
              "type": "Stack",
              "children": ["ready_txt"]
            },
            "ready_txt": { "type": "Text", "text": "Ready", "style": "caption", "style": "body" }
          }
        }
        """;

        const string logicJs = """
        export function onInit(ctx) {
          ctx.collections.append('dishes', [{ id: 'd1', name: '拉面' }, { id: 'd2', name: '披萨' }]);
        }
        """;

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            duplicateKeysManifest,
            logicJs,
            cultureName: "zh-CN");

        Assert.False(compiled.HasHardErrors, string.Join("; ", compiled.HardDiagnostics.Select(d => d.Message)));
        Assert.Equal("Stack", compiled.Manifest!.View!.Type);
        Assert.Equal("body", compiled.Manifest.View.Children[0].Style);

        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compiled.ValidationResult.CompiledScript!,
            "onInit",
            new CustomWidgetScriptExecutionInput());
        Assert.True(exec.IsSuccess, exec.ErrorMessage);
        Assert.Equal(2, exec.DirtyCollections["dishes"].Count);
        Assert.Equal("拉面", exec.DirtyCollections["dishes"][0].Fields["name"]);
        Assert.Equal("披萨", exec.DirtyCollections["dishes"][1].Fields["name"]);
    }

    [Fact]
    public void BooleanSettingsSerialization_AndCapsuleDialectNormalization_WorkEndToEnd()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.bool-settings-capsule",
          "name": "Timetable",
          "settings": [
            { "key": "showWeekend", "type": "toggle", "label": "显示周末", "default": false }
          ],
          "script": { "entry": "logic.js" },
          "capsule": {
            "title": "本周课表",
            "secondary": "今日 3 节",
            "meta": "下节 高数"
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_cols"] },
            "txt_cols": { "type": "Text", "text": "${$computed.cols}" }
          }
        }
        """;

        const string logicJs = """
        export function onInit(ctx) {
          const days = ctx.settings?.showWeekend === true ? 7 : 5;
          const falsyWhenOff = !ctx.settings?.showWeekend;
          ctx.computed.cols = `${days}:${falsyWhenOff}`;
        }
        """;

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            logicJs,
            cultureName: "zh-CN");
        Assert.False(compiled.HasHardErrors);
        Assert.Equal("今日 3 节", compiled.Manifest!.Capsule!.Summary);
        Assert.Equal("下节 高数", compiled.Manifest.Capsule.Badge);

        CustomWidgetScriptExecutionResult offExec = CustomWidgetScriptEngine.Execute(
            compiled.ValidationResult.CompiledScript!,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                Settings = new Dictionary<string, string> { ["showWeekend"] = "false" }
            });
        Assert.True(offExec.IsSuccess, offExec.ErrorMessage);
        Assert.Equal("5:true", offExec.Computed["cols"].GetString());

        CustomWidgetScriptExecutionResult onExec = CustomWidgetScriptEngine.Execute(
            compiled.ValidationResult.CompiledScript!,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                Settings = new Dictionary<string, string> { ["showWeekend"] = "true" }
            });
        Assert.True(onExec.IsSuccess, onExec.ErrorMessage);
        Assert.Equal("7:false", onExec.Computed["cols"].GetString());
    }
}
