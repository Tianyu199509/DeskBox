using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class CustomWidgetCompilerAndGridGeometryTests
{
    [Fact]
    public void NormalizeDomInPlace_DemotesRedundantPlainCards_WhilePreservingSemanticToneAndInteractiveCards()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.card.demotion",
          "name": "Card Demotion Test",
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "background": "#1E293B",
              "spacing": -4,
              "children": ["outer_plain_card", "outer_chromatic_card"]
            },
            "outer_plain_card": {
              "type": "Card",
              "tone": "default",
              "cornerRadius": 12,
              "children": ["inner_single_card"]
            },
            "inner_single_card": {
              "type": "Card",
              "tone": "subtle",
              "children": ["inner_single_text"]
            },
            "inner_single_text": { "type": "Text", "text": "Inner plain single-child wrapper" },
            "outer_chromatic_card": {
              "type": "Card",
              "background": "#2563EB",
              "children": ["depth1_branch_a", "depth1_semantic", "interactive_card"]
            },
            "depth1_branch_a": {
              "type": "Card",
              "children": ["depth1_branch_a_text", "depth2_plain_card"]
            },
            "depth1_branch_a_text": { "type": "Text", "text": "Depth 1 branch A" },
            "depth2_plain_card": {
              "type": "Card",
              "children": ["depth2_plain_text"]
            },
            "depth2_plain_text": { "type": "Text", "text": "Depth 2 plain nested card" },
            "depth1_semantic": {
              "type": "Card",
              "tone": "accent",
              "children": ["depth1_semantic_text", "depth2_semantic_card"]
            },
            "depth1_semantic_text": { "type": "Text", "text": "Depth 1 semantic tone" },
            "depth2_semantic_card": {
              "type": "Card",
              "tone": "success",
              "children": ["depth2_semantic_text"]
            },
            "depth2_semantic_text": { "type": "Text", "text": "Semantic success card stays Card" },
            "interactive_card": {
              "type": "Card",
              "onClick": { "type": "setState", "key": "picked", "value": "yes" },
              "children": ["interactive_card_text"]
            },
            "interactive_card_text": { "type": "Text", "text": "Interactive button-card stays Card" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(manifestJson);
        bool changed = engine.NormalizeDomInPlace();
        Assert.True(changed);

        JsonNode? root = JsonNode.Parse(engine.GetManifestJson(indented: false)!);
        Assert.NotNull(root);
        JsonObject elements = root!["elements"]!.AsObject();
        JsonObject view = elements[root["root"]!.GetValue<string>()]!.AsObject();

        Assert.False(view.ContainsKey("background"));
        Assert.Equal(0, view["spacing"]!.GetValue<int>());

        JsonArray topChildren = view["children"]!.AsArray();

        // Outer plain Card survives; its single-child inner plain Card is demoted to Stack with 0 padding
        JsonObject firstOuterCard = elements[topChildren[0]!.GetValue<string>()]!.AsObject();
        Assert.Equal("Card", firstOuterCard["type"]!.GetValue<string>());
        Assert.False(firstOuterCard.ContainsKey("cornerRadius"));
        JsonObject demotedInnerSingle = elements[firstOuterCard["children"]!.AsArray()[0]!.GetValue<string>()]!.AsObject();
        Assert.Equal("Stack", demotedInnerSingle["type"]!.GetValue<string>());
        Assert.Equal("0", demotedInnerSingle["padding"]!.GetValue<string>());

        // Chromatic RGB hex (#2563EB) on Card is mapped to a semantic tone and raw background is removed
        JsonObject secondOuterCard = elements[topChildren[1]!.GetValue<string>()]!.AsObject();
        Assert.False(secondOuterCard.ContainsKey("background"));
        string mappedTone = secondOuterCard["tone"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(mappedTone));
        Assert.NotEqual("default", mappedTone);

        JsonArray secondOuterChildren = secondOuterCard["children"]!.AsArray();

        // Third-level plain Card (cardAncestorDepth >= 2) is demoted to Stack with padded seam
        JsonObject depth1BranchA = elements[secondOuterChildren[0]!.GetValue<string>()]!.AsObject();
        JsonObject depth2PlainCard = elements[depth1BranchA["children"]!.AsArray()[1]!.GetValue<string>()]!.AsObject();
        Assert.Equal("Stack", depth2PlainCard["type"]!.GetValue<string>());
        Assert.Equal("6,4,6,4", depth2PlainCard["padding"]!.GetValue<string>());

        // Semantic-tone nested Card and interactive onClick Card remain Cards
        JsonObject depth1Semantic = elements[secondOuterChildren[1]!.GetValue<string>()]!.AsObject();
        JsonObject semanticCard = elements[depth1Semantic["children"]!.AsArray()[1]!.GetValue<string>()]!.AsObject();
        Assert.Equal("Card", semanticCard["type"]!.GetValue<string>());
        Assert.Equal("success", semanticCard["tone"]!.GetValue<string>());

        JsonObject interactiveCard = elements[secondOuterChildren[2]!.GetValue<string>()]!.AsObject();
        Assert.Equal("Card", interactiveCard["type"]!.GetValue<string>());
    }

    [Fact]
    public void Compile_FlagsCjkColumnSqueezeOnSixColumnNarrowGrid_AndClearsAfterWidening()
    {
        static string BuildGridManifest(int width, string columns) => $$"""
        {
          "schemaVersion": 1,
          "id": "test.grid.cjk.squeeze",
          "name": "Grid Squeeze Check",
          "defaultSize": { "width": {{width}}, "height": 200 },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "padding": 12,
              "children": ["grid_node"]
            },
            "grid_node": {
              "type": "Grid",
              "columns": "{{columns}}",
              "spacing": 8,
              "children": ["c0", "c1", "c2", "c3", "c4", "c5"]
            },
            "c0": { "type": "Text", "text": "状态监控" },
            "c1": { "type": "Text", "text": "今日" },
            "c2": { "type": "Text", "text": "本周" },
            "c3": { "type": "Text", "text": "本月" },
            "c4": { "type": "Text", "text": "季度" },
            "c5": { "type": "Text", "text": "年度" }
          }
        }
        """;

        // Width 280 - 24 padding = 256; minus 5 * 8 gap = 216; / 6 cols = 36? Reduce padding slightly or use padding inside Grid to push netW < 28px.
        // Better yet: padding 16 on Grid + width 240: rootInner = 240-24 = 216; gridInner = 216-32 = 184; gaps = 40; colW = (184-40)/6 = 24px (< 28px).
        const string squeezedManifest = """
        {
          "schemaVersion": 1,
          "id": "test.grid.cjk.squeeze",
          "name": "Grid Squeeze Check",
          "defaultSize": { "width": 240, "height": 200 },
          "root": "grid_node",
          "elements": {
            "grid_node": {
              "type": "Grid",
              "padding": 24,
              "columns": "*,*,*,*,*,*",
              "spacing": 8,
              "children": ["c0", "c1", "c2", "c3", "c4", "c5"]
            },
            "c0": { "type": "Text", "text": "状态监控" },
            "c1": { "type": "Text", "text": "今日" },
            "c2": { "type": "Text", "text": "本周" },
            "c3": { "type": "Text", "text": "本月" },
            "c4": { "type": "Text", "text": "季度" },
            "c5": { "type": "Text", "text": "年度" }
          }
        }
        """;

        CustomWidgetCompilerResult narrowCompiled = CustomWidgetCompilerPipeline.Compile(
            squeezedManifest,
            cultureName: "zh-CN",
            runOnInitDryRun: false);

        Assert.False(narrowCompiled.HasHardErrors);
        Assert.NotNull(narrowCompiled.HealthReport);
        Assert.Contains(
            narrowCompiled.HealthReport!.Issues,
            i => string.Equals(i.Code, "layout.cjk_column_squeeze", StringComparison.Ordinal));

        CustomWidgetCompilerResult wideCompiled = CustomWidgetCompilerPipeline.Compile(
            BuildGridManifest(420, "*,*"),
            cultureName: "zh-CN",
            runOnInitDryRun: false);

        Assert.False(wideCompiled.HasHardErrors);
        Assert.DoesNotContain(
            wideCompiled.HealthReport!.Issues,
            i => string.Equals(i.Code, "layout.cjk_column_squeeze", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthAnalyzer_ExclusiveWhenBranches_OnlyCountTallestBranchHeight_AndStageWidthClamps()
    {
        const string exclusiveBranchesManifest = """
        {
          "schemaVersion": 1,
          "id": "test.exclusive.when",
          "name": "Exclusive When Branches",
          "defaultSize": { "width": 340, "height": 200 },
          "state": { "tab": "a" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "padding": 12,
              "spacing": 8,
              "children": ["branch_a", "branch_b", "branch_c", "branch_d", "branch_e", "branch_f"]
            },
            "branch_a": { "type": "Text", "when": "${$state.tab == 'a'}", "text": "Branch A", "style": "body" },
            "branch_b": { "type": "Text", "when": "${$state.tab == 'b'}", "text": "Branch B", "style": "body" },
            "branch_c": { "type": "Text", "when": "${$state.tab == 'c'}", "text": "Branch C", "style": "body" },
            "branch_d": { "type": "Text", "when": "${$state.tab == 'd'}", "text": "Branch D", "style": "body" },
            "branch_e": { "type": "Text", "when": "${$state.tab == 'e'}", "text": "Branch E", "style": "body" },
            "branch_f": { "type": "Text", "when": "${$state.tab == 'f'}", "text": "Branch F", "style": "body" }
          }
        }
        """;

        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(exclusiveBranchesManifest).Manifest!;
        CustomWidgetHealthReport report = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            exclusiveBranchesManifest,
            manifest,
            storePayload: null,
            cultureName: "en-US");

        Assert.DoesNotContain(
            report.Issues,
            i => string.Equals(i.Code, "layout.vertical_overflow", StringComparison.Ordinal));
        Assert.Equal(0, report.VerticalOverflowPx);

        // Stage width clamps across standard and wide widget sizes without starving the AI pane
        double standardStage = CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(availableWidth: 1100, widgetWidth: 340);
        double wideStage = CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(availableWidth: 1100, widgetWidth: 640);
        double narrowWindowStage = CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(availableWidth: 960, widgetWidth: 600);

        Assert.InRange(standardStage, 276, 460);
        Assert.InRange(wideStage, 276, 560);
        Assert.True(wideStage > standardStage);
        Assert.True(narrowWindowStage <= 560);
    }

    [Fact]
    public void CreateForkBundle_AssignsUserForkPrefix_PopulatesLocaleCatalog_AndSuppressesIncompleteI18nOnInactiveLocales()
    {
        const string templateManifest = """
        {
          "schemaVersion": 1,
          "id": "builtin.sample.clock",
          "name": "${$i18n.title}",
          "version": "2.1.0",
          "defaultSize": { "width": 340, "height": 200 },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "padding": 12,
              "children": ["subtitle_text"]
            },
            "subtitle_text": { "type": "Text", "text": "${$i18n.subtitle}", "style": "caption" }
          }
        }
        """;

        CustomWidgetLocaleCatalog incompleteCatalog = CustomWidgetLocaleCatalog.ParseCatalogJson("""
        {
          "zh-CN": {
            "title": "时钟",
            "subtitle": "本地时间"
          },
          "en-US": {
            "title": "Clock"
          }
        }
        """);

        // Non-fork manifest reports i18n.incomplete when inactive en-US is missing 'subtitle'
        CustomWidgetManifest nonForkManifest = CustomWidgetManifestParser.Parse(templateManifest).Manifest!
            .WithLocales(incompleteCatalog);
        CustomWidgetHealthReport nonForkReport = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            templateManifest,
            nonForkManifest,
            storePayload: null,
            cultureName: "zh-CN");
        Assert.Contains(
            nonForkReport.Issues,
            i => string.Equals(i.Code, "i18n.incomplete", StringComparison.Ordinal));

        CustomWidgetForkBundleResult forkBundle = CustomWidgetOnboardingHelper.CreateForkBundle(
            templateManifest,
            logicJs: null,
            locales: incompleteCatalog,
            activeCulture: "zh-CN",
            forkTitleSuffix: "副本");

        Assert.StartsWith("user.fork-", forkBundle.NewManifestId, StringComparison.Ordinal);
        Assert.Equal("builtin.sample.clock", forkBundle.SourceManifestId);
        Assert.NotNull(forkBundle.Locales);
        Assert.True(forkBundle.Locales!.HasAnyLocale);
        Assert.True(forkBundle.Locales.Locales.ContainsKey("zh-CN"));

        CustomWidgetManifest forkManifest = CustomWidgetManifestParser.Parse(forkBundle.ManifestJson).Manifest!
            .WithLocales(forkBundle.Locales);
        CustomWidgetHealthReport forkReport = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            forkBundle.ManifestJson,
            forkManifest,
            storePayload: null,
            cultureName: "zh-CN");

        // Fork suppresses i18n.incomplete for non-active locale en-US while active zh-CN is complete
        Assert.DoesNotContain(
            forkReport.Issues,
            i => string.Equals(i.Code, "i18n.incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void ViewModel_ClearPreviewSampleData_EvictsSampleItemsAndState_AndDisablesHostStandIns()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-vm-clear-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "test.preview.clear.sample",
              "name": "Preview Sample Eviction",
              "defaultSize": { "width": 340, "height": 240 },
              "state": {
                "filter": "all"
              },
              "collections": {
                "tasks": {
                  "fields": ["title", "done"]
                }
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": 12,
                  "children": ["filter_text", "cal_grid"]
                },
                "filter_text": { "type": "Text", "text": "${$state.filter}" },
                "cal_grid": {
                  "type": "CalendarGrid",
                  "monthStateKey": "calMonth"
                }
              }
            }
            """;

            const string sampleDataJson = """
            {
              "state": {
                "filter": "today"
              },
              "collections": {
                "tasks": [
                  { "title": "Sample item one", "done": "false" },
                  { "title": "Sample item two", "done": "true" }
                ]
              }
            }
            """;

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var loc = new LocalizationService(settingsService);
            var diskStore = new CustomWidgetDataStore("preview-sample-evict", tempDir);
            using var previewVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "preview-sample-evict", Name = "Preview", WidgetKind = WidgetKind.Custom },
                loc,
                store: diskStore,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: true);

            previewVm.ApplyPreviewManifestJson(manifestJson, preserveRuntimeData: false);
            bool applied = previewVm.ApplyPreviewSampleDataJson(sampleDataJson);

            Assert.True(applied);
            Assert.True(previewVm.HasPreviewSampleData);
            Assert.True(previewVm.AllowPreviewHostStandIns);
            Assert.Equal("today", previewVm.State["filter"]);
            Assert.Equal(2, previewVm.Collections["tasks"].Count);
            Assert.All(previewVm.Collections["tasks"], r => Assert.StartsWith("sample-", r.Id, StringComparison.Ordinal));

            previewVm.ClearPreviewSampleData();

            Assert.False(previewVm.HasPreviewSampleData);
            Assert.False(previewVm.AllowPreviewHostStandIns);
            Assert.DoesNotContain(
                previewVm.Collections["tasks"],
                r => r.Id.StartsWith("sample-", StringComparison.Ordinal));
            Assert.Empty(previewVm.Collections["tasks"]);
            Assert.Equal("all", previewVm.State["filter"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Compile_ProbesExportedScriptRunHandlers_AndFlagsRuntimeHandlerError()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.script.handler.probe",
          "name": "Handler Probe",
          "script": { "entry": "logic.js", "tickIntervalMs": 0 },
          "root": "run_btn",
          "elements": {
            "run_btn": {
              "type": "Button",
              "text": "Run",
              "onClick": [
                { "type": "script.run", "fn": "brokenAction" }
              ]
            }
          }
        }
        """;

        const string brokenJs = """
        export function onInit(ctx) {
          ctx.computed.ok = true;
        }

        export function brokenAction(ctx) {
          return undeclaredSymbol.boom();
        }
        """;

        CustomWidgetCompilerResult result = CustomWidgetCompilerPipeline.Compile(
            manifestJson,
            brokenJs,
            runOnInitDryRun: true);

        Assert.True(result.HasHardErrors);
        Assert.Contains(
            result.HardDiagnostics,
            d => string.Equals(d.Code, "ScriptHandlerRuntimeError", StringComparison.Ordinal) &&
                 d.Message.Contains("brokenAction", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeDomInPlace_CanonicalizesTextColorModuleListItemsAndRefreshSource()
    {
        const string messyManifest = """
        {
          "schemaVersion": 1,
          "id": "test.ast.canonicalizer",
          "name": "Canonicalizer Test",
          "settings": [
            {
              "key": "modules",
              "type": "moduleList",
              "label": "Modules",
              "items": [
                { "id": "hero", "label": "Hero", "enabled": true },
                { "id": "chart", "label": "Chart", "enabled": false }
              ]
            }
          ],
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["heading_text", "alert_text", "reload_btn"]
            },
            "heading_text": {
              "type": "Text",
              "text": "Primary heading",
              "textColor": "#F8FAFC"
            },
            "alert_text": {
              "type": "Text",
              "text": "Alert line",
              "textColor": "warning"
            },
            "reload_btn": {
              "type": "Button",
              "text": "Reload",
              "onClick": { "type": "refreshSource", "source": "feed" }
            }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(messyManifest);
        Assert.True(engine.NormalizeDomInPlace());
        Assert.False(engine.NormalizeDomInPlace()); // Idempotent fixed point!

        JsonObject root = JsonNode.Parse(engine.GetManifestJson(indented: false)!)!.AsObject();
        JsonObject modSetting = root["settings"]!.AsArray()[0]!.AsObject();
        Assert.False(modSetting.ContainsKey("items"));
        Assert.Equal("hero:1,chart:0", modSetting["default"]!.GetValue<string>());
        Assert.Equal(2, modSetting["options"]!.AsArray().Count);

        JsonObject elements = root["elements"]!.AsObject();
        JsonArray children = elements[root["root"]!.GetValue<string>()]!["children"]!.AsArray();
        JsonObject heading = elements[children[0]!.GetValue<string>()]!.AsObject();
        Assert.False(heading.ContainsKey("textColor"));
        Assert.Equal("default", heading["color"]!.GetValue<string>()); // Neutral white hex mapped to theme-safe 'default' token

        JsonObject alert = elements[children[1]!.GetValue<string>()]!.AsObject();
        Assert.False(alert.ContainsKey("textColor"));
        Assert.Equal("warning", alert["color"]!.GetValue<string>());

        JsonObject reloadAction = elements[children[2]!.GetValue<string>()]!["onClick"]!.AsObject();
        Assert.Equal("source.refresh", reloadAction["type"]!.GetValue<string>());
        Assert.False(reloadAction.ContainsKey("source"));
        Assert.Equal("feed", reloadAction["sourceId"]!.GetValue<string>());
    }
}
