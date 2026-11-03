using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class CustomWidgetStudioCoCreationTests
{
    [Fact]
    public void NineNewWinUiPrimitives_ValidateCleanly_StampNodePaths_And_StampV160MinAppVersion()
    {
        const string manifestWithAll9Primitives = """
        {
          "schemaVersion": 1,
          "id": "custom.v160-primitives",
          "name": "Habit & Focus Studio",
          "version": "1.0.0",
          "icon": "🎯",
          "defaultSize": { "width": 340, "height": 380 },
          "state": {
            "category": "work",
            "hideDone": "false",
            "focusMinutes": "25",
            "stepMl": "250",
            "note": "",
            "statsExpanded": "true"
          },
          "collections": {
            "logs": { "maxItems": 60, "sort": "newestFirst" }
          },
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Grid",
                "columns": "*,Auto",
                "spacing": 6,
                "children": [
                  {
                    "type": "Select",
                    "column": 0,
                    "title": "分类",
                    "bind": "state.category",
                    "options": [
                      { "value": "work", "label": "工作" },
                      { "value": "life", "label": "生活" }
                    ]
                  },
                  {
                    "type": "IconButton",
                    "column": 1,
                    "icon": "\uE710",
                    "size": 28,
                    "variant": "accent",
                    "tooltip": "打卡",
                    "onClick": {
                      "type": "collection.prepend",
                      "collection": "logs",
                      "item": { "amount": "${state.stepMl}", "score": "1" }
                    }
                  }
                ]
              },
              {
                "type": "CheckBox",
                "bind": "state.hideDone",
                "text": "隐藏已完成",
                "strikethrough": true,
                "onToggle": { "type": "state.set", "key": "hideDone", "value": "true" }
              },
              {
                "type": "Slider",
                "title": "专注时长",
                "bind": "state.focusMinutes",
                "min": 5,
                "max": 90,
                "step": 5,
                "unit": "m"
              },
              {
                "type": "NumberBox",
                "title": "每次饮水",
                "bind": "state.stepMl",
                "min": 50,
                "max": 1000,
                "step": 50
              },
              {
                "type": "TextArea",
                "bind": "state.note",
                "placeholder": "写下今日复盘…",
                "minHeight": 72,
                "maxHeight": 144,
                "onSubmit": { "type": "state.set", "key": "note", "value": "" }
              },
              {
                "type": "Expander",
                "title": "趋势与打卡热力",
                "defaultExpanded": true,
                "bind": "state.statsExpanded",
                "children": [
                  {
                    "type": "Sparkline",
                    "title": "近 14 次趋势",
                    "collection": "logs",
                    "valueField": "amount",
                    "unit": "ml",
                    "height": 40,
                    "showArea": true,
                    "showLatest": true
                  },
                  {
                    "type": "HeatmapGrid",
                    "title": "近 28 天热力图",
                    "collection": "logs",
                    "valueField": "score",
                    "days": 28,
                    "color": "success"
                  }
                ]
              }
            ]
          }
        }
        """;

        CustomWidgetParseResult result = CustomWidgetManifestParser.ValidateWithRuntimeHints(manifestWithAll9Primitives);
        Assert.NotNull(result.Manifest);
        Assert.Empty(result.Diagnostics);

        CustomWidgetManifest manifest = result.Manifest!;
        Assert.Equal("$.view", manifest.View.NodePath);
        Assert.Equal("$.view.children[0]", manifest.View.Children[0].NodePath);
        Assert.Equal("$.view.children[0].children[0]", manifest.View.Children[0].Children[0].NodePath);
        Assert.Equal("$.view.children[5].children[1]", manifest.View.Children[5].Children[1].NodePath);

        CustomWidgetNodeSpec checkBox = manifest.View.Children[1];
        Assert.Equal("CheckBox", checkBox.Type);
        Assert.Equal(true, checkBox.Strikethrough);
        Assert.NotEmpty(checkBox.OnChange);

        CustomWidgetNodeSpec slider = manifest.View.Children[2];
        Assert.Equal(5, slider.Min);
        Assert.Equal(90, slider.Max);
        Assert.Equal(5, slider.Step);
        Assert.Equal("m", slider.Unit);

        CustomWidgetNodeSpec textArea = manifest.View.Children[4];
        Assert.Equal(72, textArea.MinHeight);
        Assert.Equal(144, textArea.MaxHeight);

        CustomWidgetNodeSpec expander = manifest.View.Children[5];
        Assert.Equal(true, expander.DefaultExpanded);
        Assert.Equal("Sparkline", expander.Children[0].Type);
        Assert.Equal("logs", expander.Children[0].Collection);
        Assert.Equal("amount", expander.Children[0].ValueField);
        Assert.Equal(true, expander.Children[0].ShowArea);
        Assert.Equal(true, expander.Children[0].ShowLatest);

        Assert.Equal("HeatmapGrid", expander.Children[1].Type);
        Assert.Equal(28, expander.Children[1].Days);

        Assert.True(CustomWidgetManifestCompatibility.UsesV160Features(manifest));
        string stamped = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(manifestWithAll9Primitives);
        CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(stamped);
        Assert.Equal("1.6.0", reparsed.Manifest!.MinAppVersion);

        // Zero-token metadata quick edit updates name, i18n[zh-CN].title, icon, and defaultSize cleanly
        string quickEdited = CustomWidgetManifestCompatibility.WithUpdatedMetadataJson(
            stamped,
            newName: "极简打卡台",
            newIcon: "🔥",
            newDefaultSize: new CustomWidgetSizeSpec { Width = 380, Height = 440 },
            activeCulture: "zh-CN");
        CustomWidgetManifest editedManifest = CustomWidgetManifestParser.Parse(quickEdited).Manifest!;
        Assert.Equal("🔥", editedManifest.Icon);
        Assert.Equal("极简打卡台", editedManifest.ResolveLocalizedName("zh-CN"));
        Assert.Equal(380, editedManifest.DefaultSize.Width);
        Assert.Equal(440, editedManifest.DefaultSize.Height);
    }

    [Theory]
    [InlineData("Select")]
    [InlineData("CheckBox")]
    [InlineData("Slider")]
    [InlineData("NumberBox")]
    [InlineData("TextArea")]
    [InlineData("IconButton")]
    [InlineData("Expander")]
    [InlineData("Sparkline")]
    [InlineData("HeatmapGrid")]
    public void EachIndividualV160NodeType_TriggersMinAppVersionStamping(string nodeType)
    {
        string json = $$"""
        {
          "schemaVersion": 1,
          "id": "custom.single-{{nodeType.ToLowerInvariant()}}",
          "name": "Test {{nodeType}}",
          "state": { "v": "1" },
          "collections": { "items": { "maxItems": 20 } },
          "view": {
            "type": "{{nodeType}}",
            "title": "Demo",
            "text": "Demo",
            "icon": "\uE710",
            "bind": "state.v",
            "collection": "items",
            "options": [ { "value": "1", "label": "One" } ]
          }
        }
        """;

        Assert.True(CustomWidgetManifestCompatibility.UsesV160FeaturesJson(json));
        string stamped = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(json);
        Assert.Equal("1.6.0", CustomWidgetManifestParser.Parse(stamped).Manifest!.MinAppVersion);
    }

    public static IEnumerable<object[]> TwentyFourSkillRoutingScenarios()
    {
        const string chartExistingManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.habit-chart",
          "name": "Habit Chart",
          "collections": { "logs": { "maxItems": 60 } },
          "view": { "type": "HeatmapGrid", "collection": "logs", "days": 28 }
        }
        """;

        const string aiExistingManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.ai-note",
          "name": "AI Note",
          "state": { "draft": "" },
          "view": {
            "type": "Button",
            "text": "Run",
            "onClick": { "type": "ai.run", "prompt": "${state.draft}", "stateMap": { "draft": "summary" } }
          }
        }
        """;

        // 1–5: Forms & Inputs (ZH + EN)
        yield return [null!, "做一个带下拉分类选择和优先级步进器的快速录入格子", "forms-and-inputs"];
        yield return [null!, "Build a compact form with a dropdown select, slider, and number stepper", "forms-and-inputs"];
        yield return [null!, "加一个多行备注输入框和紧凑图标按钮", "forms-and-inputs"];
        yield return [null!, "Make a daily budget counter with a slider goal and checkbox toggle", "forms-and-inputs"];
        yield return [null!, "想要一个每次喝水 250ml 的数字步进器和滑块设置目标", "forms-and-inputs"];

        // 6–10: Metrics & Microcharts (ZH + EN)
        yield return [null!, "做一个 28 天习惯打卡热力图，像 GitHub 绿格子一样", "metrics-and-microcharts"];
        yield return [null!, "Create a habit streak tracker with a 28-day heatmap and trend sparkline", "metrics-and-microcharts"];
        yield return [null!, "做一个体重折线趋势图加进度条仪表盘", "metrics-and-microcharts"];
        yield return [null!, "Show a compact CPU/RAM monitor dashboard with progress bars and chart", "metrics-and-microcharts"];
        yield return [null!, "统计每日专注时长曲线，次要统计放进折叠面板", "metrics-and-microcharts"];

        // 11–15: Collections & Filtering (ZH + EN)
        yield return [null!, "做一个支持顶部分段筛选、勾选划掉和导出 Markdown 的待办清单", "collections-and-filtering"];
        yield return [null!, "Build a todo checklist with segmented filter tabs, hover delete, and markdown export", "collections-and-filtering"];
        yield return [null!, "常用代码片段收藏夹，空列表显示友好引导，悬停一键复制或删除", "collections-and-filtering"];
        yield return [null!, "Bookmark collection list with category tabs and clear all button", "collections-and-filtering"];
        yield return [null!, "灵感清单列表，支持按状态筛选全部/未读并划掉已读项", "collections-and-filtering"];

        // 16–20: Live Sources & Drops (ZH + EN)
        yield return [null!, "做一个 Hacker News RSS 订阅源阅读器，支持手动刷新按钮", "live-sources-and-drops"];
        yield return [null!, "Create an HTTP JSON status poller widget with refresh interval and URL open", "live-sources-and-drops"];
        yield return [null!, "做一个文件拖放中转站，拖入文件显示 FileChip 点击直接定位目录", "live-sources-and-drops"];
        yield return [null!, "Drag and drop file shelf with reveal in Explorer and open actions", "live-sources-and-drops"];
        yield return [null!, "监控外部接口 JSON 数据源并展示最新动态链接", "live-sources-and-drops"];

        // 21–24: AI Micro-Workflows & Multi-Turn AST-First Preservation
        yield return [null!, "做一个闪念智能提炼箱，拖入 md 文件用 AI 自动提取摘要和行动项", "ai-micro-workflows"];
        yield return [null!, "Smart meeting notes widget using ai.run to summarize text into action items", "ai-micro-workflows"];
        // Multi-turn preservation: existing HeatmapGrid manifest + user asks for dropdown select -> keeps metrics AND adds forms!
        yield return [chartExistingManifest, "在顶部加一个下拉框选择运动类型", "metrics-and-microcharts"];
        // Multi-turn preservation: existing ai.run manifest + user asks to export list -> keeps ai-micro-workflows!
        yield return [aiExistingManifest, "加一个分段筛选和导出 Markdown 按钮", "ai-micro-workflows"];
    }

    [Theory]
    [MemberData(nameof(TwentyFourSkillRoutingScenarios))]
    public void SkillCatalog_TwentyFourScenarioBenchmark_RoutesDeterministicallyWithinCap(
        string? existingManifestJson,
        string userPrompt,
        string expectedSkillId)
    {
        IReadOnlyList<CustomWidgetSkillCard> selected = CustomWidgetSkillCatalog.SelectSkillsForTurn(
            existingManifestJson,
            userPrompt,
            maxCards: CustomWidgetSkillCatalog.DefaultMaxSelectedCards);

        Assert.InRange(selected.Count, 1, CustomWidgetSkillCatalog.DefaultMaxSelectedCards);
        Assert.Contains(selected, c => string.Equals(c.Id, expectedSkillId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkillCatalog_AllGoldenSnippetsParseWithoutSchemaErrors_And_WriteReferenceDocs()
    {
        Assert.Equal(5, CustomWidgetSkillCatalog.AllCards.Count);
        string quickIndex = CustomWidgetSkillCatalog.BuildCompactCoreComponentIndex();
        Assert.Contains("HeatmapGrid", quickIndex, StringComparison.Ordinal);
        Assert.Contains("Sparkline", quickIndex, StringComparison.Ordinal);

        var codeBlockRegex = new Regex(@"```json\s*(?<json>\{[\s\S]*?\})\s*```", RegexOptions.Compiled);
        int verifiedSnippetCount = 0;

        foreach (CustomWidgetSkillCard card in CustomWidgetSkillCatalog.AllCards)
        {
            MatchCollection matches = codeBlockRegex.Matches(card.MarkdownContent);
            Assert.NotEmpty(matches);

            foreach (Match match in matches)
            {
                string snippet = match.Groups["json"].Value.Trim();
                using JsonDocument doc = JsonDocument.Parse(snippet);
                bool isFullPartialWithOnDropOrSources =
                    doc.RootElement.TryGetProperty("sources", out _) ||
                    doc.RootElement.TryGetProperty("onDrop", out _);
                bool isSingleSourceSpec =
                    !isFullPartialWithOnDropOrSources &&
                    doc.RootElement.TryGetProperty("type", out JsonElement typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    typeEl.GetString() is "httpJson" or "rss";

                string fullManifest = isFullPartialWithOnDropOrSources
                    ? $$"""
                      {
                        "schemaVersion": 1,
                        "id": "custom.skill-{{card.Id}}",
                        "name": "Skill Test",
                        {{snippet.TrimStart('{').TrimEnd('}')}}
                      }
                      """
                    : isSingleSourceSpec
                        ? $$"""
                          {
                            "schemaVersion": 1,
                            "id": "custom.skill-{{card.Id}}",
                            "name": "Skill Test",
                            "state": { "price": "", "updated": "" },
                            "collections": { "entries": { "maxItems": 20 } },
                            "sources": [ {{snippet}} ],
                            "view": { "type": "Text", "text": "${state.price}" }
                          }
                          """
                        : $$"""
                          {
                            "schemaVersion": 1,
                            "id": "custom.skill-{{card.Id}}",
                            "name": "Skill Test",
                            "state": {
                              "draft": "",
                              "priority": "p1",
                              "dailyGoal": "5",
                              "tab": "all",
                              "filter": "all",
                              "summary": ""
                            },
                            "collections": {
                              "items": { "maxItems": 50 },
                              "logs": { "maxItems": 50 },
                              "checkins": { "maxItems": 50 },
                              "tasks": { "maxItems": 50 }
                            },
                            "view": {{snippet}}
                          }
                          """;

                CustomWidgetParseResult parsed = CustomWidgetManifestParser.ValidateWithRuntimeHints(fullManifest);
                Assert.True(
                    parsed.Manifest is not null && !parsed.Diagnostics.Any(d => d.IsError),
                    $"Golden snippet in skill '{card.Id}' failed validation: {string.Join("; ", parsed.Diagnostics.Select(d => d.Message))}");
                verifiedSnippetCount++;
            }
        }

        Assert.True(verifiedSnippetCount >= 5);

        string tempWorkspace = Path.Combine(Path.GetTempPath(), $"deskbox-skills-{Guid.NewGuid():N}");
        try
        {
            await CustomWidgetSkillCatalog.WriteReferenceDocsAsync(tempWorkspace);
            string refsDir = Path.Combine(tempWorkspace, "references");
            foreach (CustomWidgetSkillCard card in CustomWidgetSkillCatalog.AllCards)
            {
                string filePath = Path.Combine(refsDir, card.FileName);
                Assert.True(File.Exists(filePath));
            }
        }
        finally
        {
            try { Directory.Delete(tempWorkspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioSessionStore_BriefContradictionPruning_RevisionPinning_ExternalReconciliation_DraftAndClear()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CustomWidgetStudioSessionStore(tempRoot);

            // 1. Design Brief contradiction pruning + round-trip
            var initialBrief = CustomWidgetStudioSessionStore.ApplyBriefUpdates(
                new CustomWidgetDesignBrief(),
                new StudioBriefUpdateSpec
                {
                    Purpose = "每日饮水与专注打卡台",
                    AddConfirmed = ["顶部显示进度条", "支持 250ml 步进记录"],
                    AddStylePreferences = ["紧凑留白"]
                });
            Assert.Contains("顶部显示进度条", initialBrief.ConfirmedRequirements);

            // User changes mind -> AddRejected prunes contradicted ConfirmedRequirements automatically
            var updatedBrief = CustomWidgetStudioSessionStore.ApplyBriefUpdates(
                initialBrief,
                new StudioBriefUpdateSpec
                {
                    AddRejected = ["顶部显示进度条"]
                });
            Assert.DoesNotContain("顶部显示进度条", updatedBrief.ConfirmedRequirements);
            Assert.Contains("顶部显示进度条", updatedBrief.RejectedIdeas);

            string formattedMd = CustomWidgetStudioSessionStore.FormatBriefMarkdown(updatedBrief);
            CustomWidgetDesignBrief reparsedBrief = CustomWidgetStudioSessionStore.ParseBriefMarkdown(formattedMd);
            Assert.Equal("每日饮水与专注打卡台", reparsedBrief.Purpose);
            Assert.Single(reparsedBrief.ConfirmedRequirements);
            Assert.Single(reparsedBrief.RejectedIdeas);

            // 2. Revision cap (20) with up to 8 pinned IsCommitted slots surviving 26 uncommitted turns
            var manyRevisions = new List<StudioPersistedRevision>();
            for (int i = 1; i <= 10; i++)
            {
                string json = $$$"""{"schemaVersion":1,"id":"custom.pin","name":"Committed {{{i}}}","view":{"type":"Text","text":"c{{{i}}}"}}""";
                manyRevisions.Add(new StudioPersistedRevision
                {
                    RevisionId = $"commit-{i}",
                    CreatedAtUtcTicks = i * 1000L,
                    Source = "ai",
                    ChangeSummary = $"Committed {i}",
                    ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(json),
                    IsCommitted = true,
                    ManifestJson = json
                });
            }
            for (int i = 1; i <= 25; i++)
            {
                string json = $$$"""{"schemaVersion":1,"id":"custom.pin","name":"Draft {{{i}}}","view":{"type":"Text","text":"d{{{i}}}"}}""";
                manyRevisions.Add(new StudioPersistedRevision
                {
                    RevisionId = $"draft-{i}",
                    CreatedAtUtcTicks = 100_000L + i * 1000L,
                    Source = "ai",
                    ChangeSummary = $"Draft {i}",
                    ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(json),
                    IsCommitted = false,
                    ManifestJson = json
                });
            }

            IReadOnlyList<StudioPersistedRevision> trimmed = CustomWidgetStudioSessionStore.TrimRevisions(manyRevisions);
            Assert.Equal(CustomWidgetStudioSessionStore.MaxRevisions, trimmed.Count);
            Assert.Equal(
                CustomWidgetStudioSessionStore.MaxPinnedCommittedRevisions,
                trimmed.Count(static r => r.IsCommitted));
            Assert.Equal("draft-25", trimmed[^1].RevisionId);

            // 3. Create-mode draft -> Promote to committed widget -> External edit SHA256 reconciliation -> Clear history
            var createDraft = new StudioDraftSnapshot
            {
                StartDraftIdea = "饮水打卡",
                SelectedEmoji = "💧",
                BriefMarkdown = formattedMd,
                Revisions = [trimmed[^1]],
                ChatMessages =
                [
                    new StudioChatMessage { Id = "m1", Role = "user", Kind = "prompt", Text = "做一个饮水打卡" },
                    new StudioChatMessage { Id = "m2", Role = "assistant", Kind = "edit", Text = "已生成初版", LinkedRevisionId = trimmed[^1].RevisionId }
                ]
            };
            store.SaveCreateDraft(createDraft);
            Assert.NotNull(store.TryLoadCreateDraft());

            store.PromoteCreateSessionToWidget("water-1", formattedMd, trimmed, createDraft.ChatMessages);
            Assert.Null(store.TryLoadCreateDraft());
            Assert.False(Directory.Exists(store.GetCreateDraftDirectory()));

            // Simulate external tool (e.g., Claude Code / VS Code) modifying manifest.json directly on disk
            const string externallyEditedManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.pin",
              "name": "Edited Externally",
              "view": { "type": "Text", "text": "From Claude Code" }
            }
            """;
            string widgetDir = store.GetWidgetDirectory("water-1");
            File.WriteAllText(Path.Combine(widgetDir, "manifest.json"), externallyEditedManifest, Encoding.UTF8);
            File.WriteAllText(Path.Combine(widgetDir, "store.json"), """{"state":{"cups":"3"}}""", Encoding.UTF8);

            StudioLoadedSession reconciled = store.LoadOrReconcileWidgetSession(
                "water-1",
                externallyEditedManifest,
                "外部修改同步");
            Assert.True(reconciled.WasReconciledWithExternalEdit);
            Assert.Equal("external", reconciled.Revisions[^1].Source);
            Assert.Equal("外部修改同步", reconciled.Revisions[^1].ChangeSummary);

            // Save & cleanly discard edit-mode draft
            store.SaveWidgetDraft("water-1", createDraft with { TargetWidgetId = "water-1" });
            Assert.True(File.Exists(Path.Combine(widgetDir, CustomWidgetStudioSessionStore.DraftFileName)));
            store.DiscardWidgetDraft("water-1");
            Assert.False(File.Exists(Path.Combine(widgetDir, CustomWidgetStudioSessionStore.DraftFileName)));

            // Clear co-creation history wipes brief/revisions/chat while keeping manifest.json and store.json intact
            store.ClearWidgetCoCreationHistory("water-1");
            Assert.False(File.Exists(Path.Combine(widgetDir, CustomWidgetStudioSessionStore.BriefFileName)));
            Assert.False(File.Exists(Path.Combine(widgetDir, CustomWidgetStudioSessionStore.RevisionsFileName)));
            Assert.False(File.Exists(Path.Combine(widgetDir, CustomWidgetStudioSessionStore.ChatFileName)));
            Assert.True(File.Exists(Path.Combine(widgetDir, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(widgetDir, "store.json")));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BackupAndShareExport_EnforcePrivacyBoundaries_And_PreserveBriefAndRevisionsInCloudBackup()
    {
        // 1. Domain path filter assertions
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/manifest.json"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/store.json"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/brief.md"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/revisions.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "widgets/w-habit/manifest.json"));

        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/studio-chat.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-habit/studio-draft.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/_studio-draft/manifest.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/_studio-draft/studio-draft.json"));

        // 2. End-to-end scoped backup export + restore
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-bkp-{Guid.NewGuid():N}");
        string sourceAppRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "src-app")).FullName;
        string destAppRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "dst-app")).FullName;
        string exportRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "exports")).FullName;

        try
        {
            string srcDataDir = Directory.CreateDirectory(Path.Combine(sourceAppRoot, "data")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(srcDataDir, "widget-layout.json"),
                """{"schemaVersion":1,"layout":{"widgets":[{"id":"w-habit","widgetKind":"Custom"}]}}""");

            string srcWidgetDir = Directory.CreateDirectory(Path.Combine(srcDataDir, "custom-widgets", "w-habit")).FullName;
            const string v160Manifest = """
            {
              "schemaVersion": 1,
              "id": "custom.habit",
              "name": "Habit Heatmap",
              "collections": { "logs": { "maxItems": 30 } },
              "view": { "type": "HeatmapGrid", "collection": "logs", "days": 14 }
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "manifest.json"), v160Manifest);
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "store.json"), """{"state":{},"collections":{}}""");
            string srcLocalesDir = Directory.CreateDirectory(Path.Combine(srcWidgetDir, "locales")).FullName;
            await File.WriteAllTextAsync(Path.Combine(srcLocalesDir, "zh-CN.json"), """{"meta.name":"习惯热力图"}""");
            await File.WriteAllTextAsync(Path.Combine(srcLocalesDir, "_meta.json"), """{"schemaVersion":1,"baseLocale":"zh-CN","hashes":{"meta.name":"a1b2c3d4"}}""");
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "brief.md"), "# 用途定位\n每日运动热力打卡\n");
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "revisions.json"), """{"version":2,"revisions":[]}""");
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "studio-chat.json"), """{"version":1,"messages":[{"id":"secret","role":"user","kind":"prompt","text":"Private chat"}]}""");
            await File.WriteAllTextAsync(Path.Combine(srcWidgetDir, "studio-draft.json"), """{"version":2}""");

            string draftDir = Directory.CreateDirectory(Path.Combine(srcDataDir, "custom-widgets", "_studio-draft")).FullName;
            await File.WriteAllTextAsync(Path.Combine(draftDir, "manifest.json"), v160Manifest);

            var srcBackupService = new DeskBoxDataBackupService(sourceAppRoot);
            string zipPath = await srcBackupService.ExportScopedBackupAsync(exportRoot, CloudBackupDomain.CustomWidgets);

            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/manifest.json"));
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/store.json"));
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/locales/zh-CN.json"));
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/locales/_meta.json"));
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/brief.md"));
                Assert.NotNull(zip.GetEntry("data/custom-widgets/w-habit/revisions.json"));
                Assert.Null(zip.GetEntry("data/custom-widgets/w-habit/studio-chat.json"));
                Assert.Null(zip.GetEntry("data/custom-widgets/w-habit/studio-draft.json"));
                Assert.Null(zip.GetEntry("data/custom-widgets/_studio-draft/manifest.json"));
            }

            // Restore into destination app root (confirming additive merge mode)
            Directory.CreateDirectory(Path.Combine(destAppRoot, "data"));
            var dstBackupService = new DeskBoxDataBackupService(destAppRoot);
            DeskBoxRestorePreparation prep = await dstBackupService.PrepareScopedRestoreAsync(zipPath, CloudBackupDomain.CustomWidgets);
            Assert.NotNull(prep);
            Assert.True(await dstBackupService.SetPendingRestoreItemReplaceModeAsync(false));
            DeskBoxRestoreApplyResult applyRes = await dstBackupService.ApplyPendingRestoreAsync();
            Assert.True(applyRes.Succeeded, applyRes.ErrorMessage);

            Assert.True(File.Exists(Path.Combine(destAppRoot, "data", "custom-widgets", "w-habit", "locales", "zh-CN.json")));
            Assert.True(File.Exists(Path.Combine(destAppRoot, "data", "custom-widgets", "w-habit", "locales", "_meta.json")));
            Assert.True(File.Exists(Path.Combine(destAppRoot, "data", "custom-widgets", "w-habit", "brief.md")));
            Assert.True(File.Exists(Path.Combine(destAppRoot, "data", "custom-widgets", "w-habit", "revisions.json")));
            Assert.False(File.Exists(Path.Combine(destAppRoot, "data", "custom-widgets", "w-habit", "studio-chat.json")));

            // 3. Sanitized manifest stamps minAppVersion = 1.6.0
            (string sanitizedJson, _, _) = CustomWidgetPackageService.SanitizeManifestForSharing(v160Manifest);
            CustomWidgetManifest sharedManifest = CustomWidgetManifestParser.Parse(sanitizedJson).Manifest!;
            Assert.Equal("1.6.0", sharedManifest.MinAppVersion);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HealthAnalyzer_EvaluatesStoreDataSafety_MigratesRenamedCollection_And_ProtectsPrivateValues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string v1Json = """
            {
              "schemaVersion": 1,
              "id": "custom.tasks",
              "name": "Tasks",
              "state": { "goal": "10", "aiStatus": "idle" },
              "collections": { "todos": { "maxItems": 50 } },
              "view": {
                "type": "List",
                "items": "todos",
                "itemTemplate": { "type": "Text", "text": "${$item.title}" }
              }
            }
            """;

            const string v2RenamedJson = """
            {
              "schemaVersion": 1,
              "id": "custom.tasks",
              "name": "Action Items",
              "defaultSize": { "width": 320, "height": 220 },
              "state": {},
              "collections": { "actionItems": { "maxItems": 50 } },
              "view": {
                "type": "Card",
                "children": [
                  {
                    "type": "Card",
                    "children": [
                      {
                        "type": "Card",
                        "children": [
                          { "type": "Text", "text": "Deeply nested", "color": "#FFFFFF" }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """;

            CustomWidgetManifest m1 = CustomWidgetManifestParser.Parse(v1Json).Manifest!
                .WithLocales(CustomWidgetLocaleCatalog.ParseCatalogJson("""{"zh-CN":{"title":"任务"},"en-US":{"title":"Tasks"}}"""));
            CustomWidgetManifest m2 = CustomWidgetManifestParser.Parse(v2RenamedJson).Manifest!
                .WithLocales(CustomWidgetLocaleCatalog.ParseCatalogJson("""{"zh-CN":{"title":"行动项"}}"""));

            var store = new CustomWidgetDataStore("tasks-safe", tempDir);
            var payload = new CustomWidgetStorePayload
            {
                State = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["goal"] = "8",
                    ["aiStatus"] = "running" // ephemeral runtime state — must not trigger false data loss alarm
                },
                Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
                {
                    ["todos"] =
                    [
                        new CustomWidgetRecord
                        {
                            Id = "rec-1",
                            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["title"] = "CONFIDENTIAL_SALARY_99000",
                                ["done"] = "false"
                            }
                        }
                    ]
                }
            };
            await store.SaveAsync(payload);

            // 1. P0 Data Safety Gate detects dropped populated collection + 1-to-1 rename target + non-empty user state
            CustomWidgetDataSafetyReport safety = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(m1, payload, m2);
            Assert.True(safety.HasDataLossRisk);
            CustomWidgetDroppedCollectionRisk colRisk = Assert.Single(safety.DroppedCollections);
            Assert.Equal("todos", colRisk.OldCollectionKey);
            Assert.Equal(1, colRisk.LiveRecordCount);
            Assert.Equal("actionItems", colRisk.SuggestedNewCollectionKey);
            Assert.Equal(["goal"], safety.DroppedNonEmptyStateKeys);

            // 2. Structural AST diff catches dropped en-US locale and removed collection
            CustomWidgetStructuralDiff diff = CustomWidgetHealthAnalyzer.ComputeStructuralDiff(m1, m2);
            Assert.Contains("en-US", diff.DroppedLocales);
            Assert.Contains("todos", diff.RemovedCollections);
            Assert.Contains("actionItems", diff.AddedCollections);

            // 3. Live Health & Privacy-Safe Data Shape NEVER leaks private record values
            CustomWidgetHealthReport health = CustomWidgetHealthAnalyzer.AnalyzeHealth(
                rawManifestJson: v2RenamedJson,
                manifest: m2,
                storePayload: payload,
                measuredWidth: 300,
                measuredHeight: 340,
                cultureName: "zh-CN");

            Assert.True(health.VerticalOverflowPx > 0);
            Assert.Equal(3, health.MaxCardNestingDepth);
            Assert.Contains(health.Issues, i => i.Code == "layout.vertical_overflow");
            Assert.Contains(health.Issues, i => i.Code == "visual.card_nesting");
            Assert.Contains(health.Issues, i => i.Code == "theme.hardcoded_hex");

            string telemetryBlock = CustomWidgetHealthAnalyzer.FormatTelemetryPromptBlock(health);
            Assert.Contains("todos", telemetryBlock, StringComparison.Ordinal);
            Assert.Contains("title", telemetryBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("CONFIDENTIAL_SALARY_99000", telemetryBlock, StringComparison.Ordinal);

            // 4. 1-click collection rename migration preserves all records cleanly
            await CustomWidgetHealthAnalyzer.MigrateRenamedCollectionsAsync(store, [("todos", "actionItems")]);
            CustomWidgetStorePayload migrated = await store.LoadAsync();
            Assert.False(migrated.Collections.ContainsKey("todos"));
            Assert.Single(migrated.Collections["actionItems"]);
            Assert.Equal("CONFIDENTIAL_SALARY_99000", migrated.Collections["actionItems"][0].Fields["title"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StudioTurnEngine_HandlesEditWithPreviewSampleData_Clarify_Answer_BareFallback_And_AutoRepair()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-turn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Bare manifest fallback + preferredEmoji + automatic 1.6.0 stamping
            const string bareReply = """
            ```json
            {
              "schemaVersion": 1,
              "id": "custom.bare",
              "name": "Bare Demo",
              "state": { "cat": "a" },
              "view": {
                "type": "Select",
                "bind": "state.cat",
                "options": [ { "value": "a", "label": "Alpha" } ]
              }
            }
            ```
            """;
            StudioTurnResult bareParsed = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
                bareReply,
                preferredEmoji: "🚀");
            Assert.True(bareParsed.Success);
            Assert.Equal("edit", bareParsed.Kind);
            Assert.Equal("🚀", bareParsed.ParseResult!.Manifest!.Icon);
            Assert.Equal("1.6.0", bareParsed.ParseResult.Manifest.MinAppVersion);

            // 2. Conversational clarify & answer envelopes do not require or mutate manifest
            const string clarifyEnvelope = """
            {
              "kind": "clarify",
              "reply": "你希望按每天打卡还是按周统计？",
              "suggestions": ["按每天打卡（28天热力图）", "按周目标进度条"]
            }
            """;
            StudioTurnResult clarifyRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(clarifyEnvelope);
            Assert.True(clarifyRes.Success);
            Assert.Equal("clarify", clarifyRes.Kind);
            Assert.Equal(2, clarifyRes.Suggestions.Count);
            Assert.Equal(string.Empty, clarifyRes.ManifestJson);

            StudioTurnResult plainProseRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
                "当前格子使用了 HeatmapGrid 组件展示最近 28 天的打卡热力分布。");
            Assert.True(plainProseRes.Success);
            Assert.Equal("answer", plainProseRes.Kind);

            // 3. RunStudioTurnAsync with sampleData -> populates preview ViewModel in memory without touching disk
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-studio-turn-key",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));

            int callIndex = 0;
            using var httpClient = new HttpClient(new StubTurnHttpMessageHandler((_, _) =>
            {
                callIndex++;
                string assistantContent = callIndex switch
                {
                    1 => """
                         {
                           "kind": "edit",
                           "reply": "已为你创建带 28 天热力图的习惯打卡格子。",
                           "changeSummary": "创建习惯热力图初版",
                           "briefUpdates": {
                             "purpose": "每日运动打卡",
                             "addConfirmed": ["近 28 天热力图"]
                           },
                           "suggestions": ["加一个运动类型下拉框", "调成紧凑尺寸"],
                           "sampleData": {
                             "state": { "streak": "5" },
                             "collections": {
                               "logs": [
                                 { "score": "2", "note": "晨跑 5km" },
                                 { "score": "1", "note": "拉伸 15m" }
                               ]
                             }
                           },
                           "manifest": {
                             "schemaVersion": 1,
                             "id": "custom.run-heatmap",
                             "name": "运动热力打卡",
                             "state": { "streak": "0" },
                             "collections": { "logs": { "maxItems": 60 } },
                             "view": {
                               "type": "HeatmapGrid",
                               "title": "近 28 天运动热力",
                               "collection": "logs",
                               "valueField": "score",
                               "days": 28
                             }
                           }
                         }
                         """,
                    // Call 2: intentionally invalid node type "UnknownFancyChart" to trigger 1-round auto-repair
                    2 => """
                         {
                           "kind": "edit",
                           "reply": "已更新趋势图。",
                           "changeSummary": "添加趋势折线",
                           "manifest": {
                             "schemaVersion": 1,
                             "id": "custom.run-heatmap",
                             "name": "运动热力打卡",
                             "collections": { "logs": { "maxItems": 60 } },
                             "view": { "type": "UnknownFancyChart" }
                           }
                         }
                         """,
                    // Call 3: auto-repair round returns valid Sparkline manifest
                    _ => """
                         {
                           "schemaVersion": 1,
                           "id": "custom.run-heatmap",
                           "name": "运动热力打卡",
                           "collections": { "logs": { "maxItems": 60 } },
                           "view": {
                             "type": "Sparkline",
                             "collection": "logs",
                             "valueField": "score"
                           }
                         }
                         """
                };

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("choices");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WritePropertyName("message");
                    writer.WriteStartObject();
                    writer.WriteString("content", assistantContent);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json")
                });
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            var generator = new CustomWidgetAiGeneratorService(aiService);

            StudioTurnResult turn1 = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "做一个运动热力打卡",
                allowSampleData: true,
                uiLanguage: "zh-CN");

            Assert.True(turn1.Success, turn1.ErrorMessage);
            Assert.Equal("edit", turn1.Kind);
            Assert.Equal("创建习惯热力图初版", turn1.ChangeSummary);
            Assert.NotNull(turn1.BriefUpdates);
            Assert.Equal("每日运动打卡", turn1.BriefUpdates!.Purpose);
            Assert.NotNull(turn1.SampleDataJson);
            Assert.Equal("1.6.0", turn1.ParseResult!.Manifest!.MinAppVersion);

            // Verify sampleData populates preview-only ViewModel in memory without writing disk store
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var loc = new LocalizationService(settingsService);
            var diskStore = new CustomWidgetDataStore("preview-sample-check", tempDir);
            using var previewVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "preview-sample-check", Name = "Preview", WidgetKind = WidgetKind.Custom },
                loc,
                store: diskStore,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: true);

            previewVm.ApplyPreviewManifestJson(turn1.ManifestJson, preserveRuntimeData: false);
            previewVm.ApplyPreviewSampleDataJson(turn1.SampleDataJson);
            Assert.True(previewVm.HasPreviewSampleData);
            Assert.Equal("5", previewVm.State["streak"]);
            Assert.Equal(2, previewVm.Collections["logs"].Count);
            Assert.False(File.Exists(diskStore.StorePath));

            previewVm.ClearPreviewSampleData();
            Assert.False(previewVm.HasPreviewSampleData);

            // Turn 2 triggers silent 1-round auto-repair and succeeds with WasAutoRepaired == true
            StudioTurnResult turn2 = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "改成折线图",
                currentManifestJson: turn1.ManifestJson,
                uiLanguage: "zh-CN");

            Assert.True(turn2.Success, turn2.ErrorMessage);
            Assert.True(turn2.WasAutoRepaired);
            Assert.Equal("Sparkline", turn2.ParseResult!.Manifest!.View.Type);
            Assert.Equal("1.6.0", turn2.ParseResult.Manifest.MinAppVersion);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GlobalAndMainlandProviderAndWebAiOrdering_RespectsCultureIncludingTraditionalChinese()
    {
        Assert.Equal("deepseek", CustomWidgetAiGeneratorService.GetDefaultStarterProviderId("zh-CN"));
        Assert.Equal("openai", CustomWidgetAiGeneratorService.GetDefaultStarterProviderId("en-US"));
        Assert.Equal("openai", CustomWidgetAiGeneratorService.GetDefaultStarterProviderId("zh-TW"));
        Assert.Equal("openai", CustomWidgetAiGeneratorService.GetDefaultStarterProviderId("zh-HK"));
        Assert.Equal("openai", CustomWidgetAiGeneratorService.GetDefaultStarterProviderId("ja-JP"));

        IReadOnlyList<CustomWidgetAiProviderPreset> globalProviders =
            CustomWidgetAiGeneratorService.GetOrderedProvidersForCulture("en-US");
        Assert.Equal("openai", globalProviders[0].Id);
        Assert.Equal("anthropic", globalProviders[1].Id);
        Assert.Equal("gemini", globalProviders[2].Id);
        Assert.Contains(globalProviders, p => p.Id == "groq");
        Assert.Contains(globalProviders, p => p.Id == "ollama" && !p.RequiresApiKey);
        Assert.Contains(globalProviders, p => p.Id == "lmstudio" && !p.RequiresApiKey);

        IReadOnlyList<CustomWidgetAiProviderPreset> twProviders =
            CustomWidgetAiGeneratorService.GetOrderedProvidersForCulture("zh-TW");
        Assert.Equal("openai", twProviders[0].Id);

        IReadOnlyList<CustomWidgetAiProviderPreset> cnProviders =
            CustomWidgetAiGeneratorService.GetOrderedProvidersForCulture("zh-CN");
        Assert.Equal("deepseek", cnProviders[0].Id);
        Assert.Equal("qwen", cnProviders[1].Id);

        IReadOnlyList<WebAiTargetPreset> globalWeb =
            CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets("en-US", includeCopyOnly: false);
        Assert.Equal(["chatgpt", "claude", "gemini", "copilot"], globalWeb.Take(4).Select(t => t.Id).ToArray());

        IReadOnlyList<WebAiTargetPreset> cnWeb =
            CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets("zh-CN", includeCopyOnly: false);
        Assert.Equal(["deepseek", "doubao", "kimi"], cnWeb.Take(3).Select(t => t.Id).ToArray());

        string tempPrefs = Path.Combine(Path.GetTempPath(), $"deskbox-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new CustomWidgetStudioPreferencesStore(tempPrefs);
            Assert.Equal("chatgpt", prefs.ResolveEffectiveWebAiTarget("en-US"));
            Assert.Equal("chatgpt", prefs.ResolveEffectiveWebAiTarget("zh-TW"));
            Assert.Equal("deepseek", prefs.ResolveEffectiveWebAiTarget("zh-CN"));
        }
        finally
        {
            try { File.Delete(tempPrefs); } catch { }
        }
    }

    [Fact]
    public async Task KeylessLocalProviders_And_GlobalDefaultEndpoints_WorkEndToEndWithoutApiKey()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-local-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "ollama",
                ApiKey: string.Empty,
                Enabled: true,
                CustomModel: "qwen2.5-coder:7b"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "lmstudio",
                ApiKey: string.Empty,
                Enabled: false));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "qwen",
                ApiKey: "sk-qwen-intl",
                Region: "global"));

            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));
            HttpRequestMessage? capturedOllamaRequest = null;

            using var httpClient = new HttpClient(new StubTurnHttpMessageHandler((req, _) =>
            {
                capturedOllamaRequest = req;
                const string reply = """
                    {
                      "kind": "edit",
                      "reply": "Created local counter.",
                      "changeSummary": "Initial local counter",
                      "manifest": {
                        "schemaVersion": 1,
                        "id": "custom.local-counter",
                        "name": "Local Counter",
                        "state": { "count": "0" },
                        "view": { "type": "Text", "text": "${state.count}" }
                      }
                    }
                    """;

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("choices");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WritePropertyName("message");
                    writer.WriteStartObject();
                    writer.WriteString("content", reply);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json")
                });
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            IReadOnlyList<CustomWidgetAiProviderPreset> configured = aiService.GetConfiguredProviders();
            Assert.Contains(configured, p => p.Id == "ollama");
            Assert.Contains(configured, p => p.Id == "qwen");
            Assert.DoesNotContain(configured, p => p.Id == "lmstudio");

            ResolvedAiInvocation qwenGlobal = aiService.ResolveEffectiveSettings("qwen");
            Assert.Equal("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", qwenGlobal.Endpoint);

            credStore.Upsert(credStore.Get("qwen")! with { Region = "cn" });
            ResolvedAiInvocation qwenCn = aiService.ResolveEffectiveSettings("qwen");
            Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", qwenCn.Endpoint);

            var generator = new CustomWidgetAiGeneratorService(aiService);
            AiConnectionTestResult connRes = await generator.TestConnectionAsync("ollama");
            Assert.True(connRes.Success, connRes.ErrorMessage);
            Assert.NotNull(capturedOllamaRequest);
            Assert.Null(capturedOllamaRequest!.Headers.Authorization);

            StudioTurnResult turnRes = await generator.RunStudioTurnAsync(
                providerId: "ollama",
                userMessage: "Make a local counter",
                uiLanguage: "en-US");
            Assert.True(turnRes.Success, turnRes.ErrorMessage);
            Assert.Equal("Local Counter", turnRes.ParseResult!.Manifest!.Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioSessionAndPreferences_PersistTargetNodeLabel_And_PickElementTeachingTipFlag()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ux-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string prefsPath = Path.Combine(tempDir, "studio-prefs.json");
            var prefs1 = new CustomWidgetStudioPreferencesStore(prefsPath);
            Assert.False(prefs1.HasSeenPickElementTip);
            prefs1.MarkPickElementTipSeen();
            Assert.True(prefs1.HasSeenPickElementTip);

            var prefs2 = new CustomWidgetStudioPreferencesStore(prefsPath);
            Assert.True(prefs2.HasSeenPickElementTip);

            var sessionStore = new CustomWidgetStudioSessionStore(tempDir);
            const string manifestJson = """
                {
                  "schemaVersion": 1,
                  "id": "custom.water",
                  "name": "Water",
                  "view": { "type": "Text", "text": "Hi" }
                }
                """;
            var revisions = new List<StudioPersistedRevision>
            {
                new()
                {
                    RevisionId = "rev-1",
                    CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                    Source = "ai",
                    ChangeSummary = "v1",
                    ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(manifestJson),
                    IsCommitted = true,
                    ManifestJson = manifestJson
                }
            };
            var messages = new List<StudioChatMessage>
            {
                new()
                {
                    Id = "msg-1",
                    Role = "user",
                    Kind = "prompt",
                    Text = "把这个按钮改成绿色",
                    TargetNodePath = "$.view.children[1]",
                    TargetNodeLabel = "Button · 记一杯"
                }
            };

            sessionStore.SaveCommittedWidgetSession("widget-water", "# Notes", revisions, messages);
            StudioLoadedSession loaded = sessionStore.LoadOrReconcileWidgetSession("widget-water", manifestJson, "External sync");
            Assert.Single(loaded.ChatMessages);
            Assert.Equal("$.view.children[1]", loaded.ChatMessages[0].TargetNodePath);
            Assert.Equal("Button · 记一杯", loaded.ChatMessages[0].TargetNodeLabel);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SettingsExpanderItems_AcrossAllViews_OnlyContainSettingsCardElements()
    {
        string? repoRoot = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
        }

        Assert.NotNull(repoRoot);
        string viewsDir = Path.Combine(repoRoot!, "src", "DeskBox", "Views");
        string[] xamlFiles = Directory.GetFiles(viewsDir, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        int expanderItemsCount = 0;
        foreach (string xamlFile in xamlFiles)
        {
            var doc = System.Xml.Linq.XDocument.Load(xamlFile);
            foreach (System.Xml.Linq.XElement itemsElement in doc.Descendants()
                         .Where(static e => e.Name.LocalName == "SettingsExpander.Items"))
            {
                expanderItemsCount++;
                foreach (System.Xml.Linq.XElement child in itemsElement.Elements())
                {
                    Assert.True(
                        child.Name.LocalName == "SettingsCard",
                        $"{Path.GetRelativePath(repoRoot!, xamlFile)} contains <{child.Name.LocalName}> directly inside <SettingsExpander.Items>; CommunityToolkit SettingsExpander applies a SettingsCard style via ItemsRepeater_ElementPrepared and will crash at runtime when expanded unless wrapped in <toolkit:SettingsCard>.");
                }
            }
        }

        Assert.True(expanderItemsCount > 0);
    }

    [Fact]
    public void SettingsRuntimePopupAndClipboardGuards_AreEnforcedInSettingsAndAiDialogs()
    {
        string? repoRoot = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
        }

        Assert.NotNull(repoRoot);

        string aiSectionPath = Path.Combine(repoRoot!, "src", "DeskBox", "Views", "SettingsSections", "AiSettingsSection.xaml.cs");
        string customSectionPath = Path.Combine(repoRoot!, "src", "DeskBox", "Views", "SettingsSections", "CustomWidgetSettingsSection.xaml.cs");
        string dialogHelperPath = Path.Combine(repoRoot!, "src", "DeskBox", "Helpers", "CustomWidgetAiConfigDialogHelper.cs");

        string aiSectionSrc = File.ReadAllText(aiSectionPath);
        string customSectionSrc = File.ReadAllText(customSectionPath);
        string dialogHelperSrc = File.ReadAllText(dialogHelperPath);

        // Cross-process clipboard writes in DeskBox.Settings.exe must isolate Clipboard.Flush() inside try/catch.
        Assert.Contains("Clipboard.Flush();\r\n            }\r\n            catch", aiSectionSrc.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        Assert.Contains("Clipboard.Flush();\r\n            }\r\n            catch", customSectionSrc.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));

        // Post-await ComboBox dropdown opening must verify visual-tree residency & XamlRoot.
        Assert.Contains("AiKeyExpander.IsExpanded", aiSectionSrc);
        Assert.Contains("AiInlineModelComboBox.XamlRoot is not null", aiSectionSrc);
        Assert.Contains("!isDialogClosed && modelCombo.IsLoaded && modelCombo.XamlRoot is not null", dialogHelperSrc);
    }

    [Theory]
    [InlineData("TextFillColorSecondaryBrush", true, "TextFillColorSecondary")]
    [InlineData("SolidBackgroundFillColorTertiaryBrush", true, "SolidBackgroundFillColorTertiary")]
    [InlineData("CardStrokeColorDefaultBrush", true, "CardStrokeColorDefault")]
    [InlineData("Brush", false, "")]
    [InlineData("TextFillColorSecondary", false, "")]
    public void NeutralInteractionBrush_TryResolveBrushBackedColorKey_MapsWinUiBrushTokensToColorStructs(
        string resourceKey,
        bool expectedMatched,
        string expectedColorKey)
    {
        bool matched = DeskBox.Helpers.NeutralInteractionBrush.TryResolveBrushBackedColorKey(resourceKey, out string colorKey);
        Assert.Equal(expectedMatched, matched);
        Assert.Equal(expectedColorKey, colorKey);
    }

    [Fact]
    public void StudioInspectorAndLightStage_EnforceDeepestHitResolutionAndThemeSync()
    {
        CustomWidgetStarterSpec aiBoard = CustomWidgetTemplates.StudioStarters
            .First(static t => t.Emoji == "🤖");
        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(aiBoard.ManifestJson).Manifest!
            .WithLocales(aiBoard.Locales);

        var ctx = new CustomWidgetEvaluationContext
        {
            State = manifest.State,
            I18n = manifest.ResolveI18nDictionary("zh-CN"),
            CultureName = "zh-CN"
        };

        CustomWidgetNodeSpec listNode = manifest.View.Children[2];
        Assert.Equal("$.view.children[2]", listNode.NodePath);
        string listSummary = CustomWidget.ControlsInspectorSummaryBridge(listNode, ctx);
        Assert.Equal("尚无推送记录", listSummary);

        CustomWidgetNodeSpec badgeNode = manifest.View.Children[0].Children[0].Children[0].Children[2];
        Assert.Equal("$.view.children[0].children[0].children[0].children[2]", badgeNode.NodePath);
        Assert.Equal("就绪待命", CustomWidget.ControlsInspectorSummaryBridge(badgeNode, ctx));

        string? repoRoot = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
        }

        Assert.NotNull(repoRoot);
        string primitivesSrc = File.ReadAllText(
            Path.Combine(repoRoot!, "src", "DeskBox", "Controls", "WidgetContents", "CustomWidgetNativeRenderer.Primitives.cs"));
        string studioPagesSrc = File.ReadAllText(
            Path.Combine(repoRoot!, "src", "DeskBox", "Views", "CustomWidgetStudioWindow.Pages.cs"));

        // Highlight overlay must stay non-hit-testable so outer containers never occlude inner nodes.
        Assert.Contains("IsHitTestVisible = false", primitivesSrc);
        Assert.Contains("ResolveDeepestInspectableNode", primitivesSrc);
        Assert.Contains("ApplyStageWidgetTheme(targetTheme);", studioPagesSrc);
    }

    [Fact]
    public void MarkCommittedRevision_IgnoresProvenanceTimestamps_AndUpdatesActiveRevisionInPlace()
    {
        const string draftV12 = """
        {
          "schemaVersion": 1,
          "id": "custom.sichuan-menu",
          "name": "今天吃什么",
          "icon": "🍜",
          "script": { "entry": "logic.js" },
          "view": { "type": "Text", "text": "酸菜鱼" }
        }
        """;
        const string logicJs = "export function onInit(ctx) { ctx.computed.dish = '酸菜鱼'; }";

        var existing = new List<StudioPersistedRevision>
        {
            new()
            {
                RevisionId = "rev-12",
                Source = "ai",
                ChangeSummary = "筛选条件移入设置",
                ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(draftV12, logicJs),
                IsCommitted = false,
                ManifestJson = draftV12,
                LogicJs = logicJs
            }
        };

        string stampedCommitJson = CustomWidgetManifestMetadataStamper.StampProvenance(
            CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(draftV12),
            origin: CustomWidgetOriginKinds.Ai,
            isCustomized: true,
            updatedAtUtc: "2026-11-05T04:26:06.2748241+00:00");

        IReadOnlyList<StudioPersistedRevision> committed = CustomWidgetStudioSessionStore.MarkCommittedRevision(
            existing,
            stampedCommitJson,
            source: "ai",
            committedLogicJs: logicJs);

        StudioPersistedRevision single = Assert.Single(committed);
        Assert.Equal("rev-12", single.RevisionId);
        Assert.Equal("筛选条件移入设置", single.ChangeSummary);
        Assert.True(single.IsCommitted);
        Assert.Equal(stampedCommitJson, single.ManifestJson);
    }

    [Fact]
    public async Task BriefReset_And_StateToSettingsPromotion_PreventFalseDataLossAndMigrateUserOverrides()
    {
        // 1. Topic pivot brief reset clears obsolete requirements before applying new purpose
        var timerBrief = new CustomWidgetDesignBrief
        {
            Purpose = "倒计时器",
            ConfirmedRequirements = ["5分钟预设按钮", "暂停继续"],
            RejectedIdeas = ["不要黑洞格子"],
            StylePreferences = ["大字号倒计时"]
        };
        var pivotEnvelope = CustomWidgetAiGeneratorService.ParseStudioTurnResponse("""
        {
          "kind": "edit",
          "reply": "已换成川菜随机推荐格子。",
          "changeSummary": "切换为今天吃什么",
          "briefUpdates": {
            "reset": true,
            "purpose": "川菜今日菜单抽签",
            "addConfirmed": ["24道家常川菜"]
          },
          "manifest": {
            "schemaVersion": 1,
            "id": "custom.sichuan-picker",
            "name": "今天吃什么",
            "view": { "type": "Text", "text": "酸菜鱼" }
          }
        }
        """);
        Assert.True(pivotEnvelope.Success);
        Assert.NotNull(pivotEnvelope.BriefUpdates);
        Assert.True(pivotEnvelope.BriefUpdates!.Reset);

        CustomWidgetDesignBrief pivoted = CustomWidgetStudioSessionStore.ApplyBriefUpdates(timerBrief, pivotEnvelope.BriefUpdates);
        Assert.Equal("川菜今日菜单抽签", pivoted.Purpose);
        Assert.Equal(["24道家常川菜"], pivoted.ConfirmedRequirements);
        Assert.Empty(pivoted.RejectedIdeas);
        Assert.Empty(pivoted.StylePreferences);

        // 2. Object-map "settings" + state -> settings promotion without false data-loss alarm
        const string prevManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.sichuan-picker",
          "name": "今天吃什么",
          "state": { "pick": "mapo", "spicyMax": "3", "quickOnly": "false" },
          "view": { "type": "Text", "text": "${state.pick}" }
        }
        """;
        const string nextObjectSettingsJson = """
        {
          "schemaVersion": 1,
          "id": "custom.sichuan-picker",
          "name": "今天吃什么",
          "settings": {
            "spicyMax": {
              "type": "choice",
              "label": "辣度上限",
              "default": "3",
              "options": [
                { "value": "1", "label": "微辣以内" },
                { "value": "3", "label": "不限辣度" }
              ]
            },
            "quickOnly": {
              "type": "toggle",
              "label": "只看 30 分钟内",
              "default": "false"
            }
          },
          "state": { "pick": "mapo" },
          "view": { "type": "Text", "text": "${state.pick} · ${$settings.spicyMax} · ${$settings.quickOnly}" }
        }
        """;

        CustomWidgetManifest prevManifest = CustomWidgetManifestParser.Parse(prevManifestJson).Manifest!;
        CustomWidgetParseResult nextParsed = CustomWidgetManifestParser.Parse(nextObjectSettingsJson);
        Assert.NotNull(nextParsed.Manifest);
        Assert.DoesNotContain(nextParsed.Diagnostics, d => d.IsError);
        Assert.Equal(2, nextParsed.Manifest!.Settings.Count);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-promote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetDataStore("sichuan-1", tempDir);
            var livePayload = new CustomWidgetStorePayload
            {
                State = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pick"] = "shuizhuyu",
                    ["spicyMax"] = "1",     // customized by user -> promoted to settings
                    ["quickOnly"] = "false" // unmodified default -> ignored cleanly
                }
            };
            await store.SaveAsync(livePayload);

            CustomWidgetDataSafetyReport safety = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
                prevManifest,
                livePayload,
                nextParsed.Manifest);
            Assert.False(safety.HasDataLossRisk);
            Assert.Empty(safety.DroppedNonEmptyStateKeys);
            Assert.Contains("spicyMax", safety.PromotedStateKeys!);

            await CustomWidgetHealthAnalyzer.MigratePromotedStateKeysToSettingsAsync(
                store,
                prevManifest,
                nextParsed.Manifest);
            CustomWidgetStorePayload migrated = await store.LoadAsync();
            Assert.Equal("1", migrated.Settings["spicyMax"]);
            Assert.False(migrated.Settings.ContainsKey("quickOnly")); // kept default clean
            Assert.False(migrated.State.ContainsKey("spicyMax"));
            Assert.False(migrated.State.ContainsKey("quickOnly"));
            Assert.Equal("shuizhuyu", migrated.State["pick"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static class CustomWidget
    {
        public static string ControlsInspectorSummaryBridge(
            CustomWidgetNodeSpec spec,
            CustomWidgetEvaluationContext ctx) =>
            DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ResolveNodeInspectionSummary(spec, ctx);
    }

    private sealed class StubTurnHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
