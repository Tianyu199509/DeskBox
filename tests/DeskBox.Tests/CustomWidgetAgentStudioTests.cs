using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class CustomWidgetAgentStudioTests
{
    [Fact]
    public void StudioStarters_And_ManifestJsonSchema_AreAllValidWithoutDiagnostics()
    {
        Assert.Equal(7, CustomWidgetTemplates.StudioStarters.Count);
        Assert.Contains(CustomWidgetTemplates.StudioStarters, s => s.Id == CustomWidgetTemplates.AiInboxStarterId);

        foreach (CustomWidgetStarterSpec starter in CustomWidgetTemplates.StudioStarters)
        {
            CustomWidgetParseResult result = CustomWidgetManifestParser.ValidateWithRuntimeHints(starter.ManifestJson);
            Assert.NotNull(result.Manifest);
            Assert.Empty(result.Diagnostics);
        }

        using JsonDocument schemaDoc = JsonDocument.Parse(CustomWidgetTemplates.ManifestJsonSchema);
        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            schemaDoc.RootElement.GetProperty("$schema").GetString());
        Assert.True(schemaDoc.RootElement.TryGetProperty("$defs", out JsonElement defs));
        Assert.True(defs.TryGetProperty("nodeSpec", out _));
        Assert.True(defs.TryGetProperty("actionSpec", out _));
    }

    [Fact]
    public void ValidateWithRuntimeHints_SupportsNewControlsAliasesAndDetectsUndeclaredBindings()
    {
        const string tolerantManifest = """
        {
          "manifestVersion": 1,
          "id": "custom.test-tolerant",
          "name": "Tolerant Test",
          "state": { "enabled": "true" },
          "collections": [
            { "name": "tasks", "fields": [ { "name": "title" }, { "name": "done" } ] }
          ],
          "onDrop": { "accept": "files", "storage": "copy", "action": { "type": "collection.prepend", "collection": "tasks", "fields": { "title": "{$drop.name}" } } },
          "root": {
            "type": "Stack",
            "children": [
              {
                "type": "Wrap",
                "spacing": 6,
                "children": [
                  { "type": "Toggle", "label": "Auto Sync", "checked": "{state.enabled}", "visible": "{state.enabled == 'true'}" },
                  { "type": "Spacer" },
                  { "type": "ProgressRing", "size": 18, "indeterminate": false, "value": "{state.undeclaredProgress}" }
                ]
              },
              {
                "type": "Button",
                "text": "Mark First ({$settings.missingOpt})",
                "actions": [
                  { "type": "collection.update", "collection": "tasks", "id": "1", "record": { "done": "true" } },
                  { "type": "collection.clear", "collection": "ghostCollection" }
                ]
              },
              {
                "type": "List",
                "itemsSource": "tasks",
                "itemTemplate": { "type": "Text", "text": "{$item.title}" }
              }
            ]
          }
        }
        """;

        CustomWidgetParseResult result = CustomWidgetManifestParser.ValidateWithRuntimeHints(tolerantManifest);
        Assert.NotNull(result.Manifest);
        Assert.DoesNotContain(result.Diagnostics, d => d.IsError);

        Assert.Equal("copy", result.Manifest!.OnDrop?.Mode);
        CustomWidgetNodeSpec wrapNode = result.Manifest.View.Children[0];
        Assert.Equal("Wrap", wrapNode.Type);
        Assert.Equal("Auto Sync", wrapNode.Children[0].Title);
        Assert.Equal("{state.enabled}", wrapNode.Children[0].Value);
        Assert.Equal("{state.enabled == 'true'}", wrapNode.Children[0].When);
        Assert.Equal(18, wrapNode.Children[2].Size);
        Assert.Equal("false", wrapNode.Children[2].Indeterminate);

        string[] warningMessages = result.Diagnostics.Where(d => !d.IsError).Select(d => d.Message).ToArray();
        Assert.Contains(warningMessages, m => m.Contains("state.undeclaredProgress", StringComparison.Ordinal));
        Assert.Contains(warningMessages, m => m.Contains("$settings.missingOpt", StringComparison.Ordinal));
        Assert.Contains(warningMessages, m => m.Contains("ghostCollection", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractJsonFromClipboardText_StripsReasoningThinkBlocksCleanly()
    {
        const string replyWithThink = """
        <think>
        Let me consider a schema example {"fake": 123} before writing the real manifest.
        </think>
        Here is your DeskBox widget manifest:
        ```json
        {
          "manifestVersion": 1,
          "id": "custom.hydration",
          "name": "Hydration",
          "root": { "type": "Text", "text": "Hello" }
        }
        ```
        """;

        string extracted = CustomWidgetTemplates.ExtractJsonFromClipboardText(replyWithThink);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(extracted);
        Assert.NotNull(parsed.Manifest);
        Assert.Equal("custom.hydration", parsed.Manifest!.Id);
    }

    [Fact]
    public async Task PreviewViewModel_SupportsCollectionUpdateAndExternalAgentDataPush()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("agent-hud-test", tempDir);

            var cfg = new WidgetConfig
            {
                Id = "agent-hud-test",
                Name = "Agent HUD",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(
                cfg,
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);

            await store.WriteManifestJsonAsync(CustomWidgetTemplates.AgentHudStarterManifestJson);
            await vm.InitializeAsync();

            var stateUpdates = new Dictionary<string, string>
            {
                ["status"] = "构建中",
                ["currentTask"] = "正在编译 x64 测试",
                ["progress"] = "75"
            };
            var pushedItems = new List<Dictionary<string, string>>
            {
                new() { ["id"] = "evt-1", ["title"] = "启动流水线", ["time"] = "02:30", ["badge"] = "INFO" },
                new() { ["id"] = "evt-2", ["title"] = "单元测试通过", ["time"] = "02:31", ["badge"] = "OK" }
            };

            await vm.ApplyExternalDataPushAsync(
                stateUpdates,
                collectionName: "events",
                collectionMode: "prepend",
                records: pushedItems);

            Assert.Equal("构建中", vm.State["status"]);
            Assert.Equal("75", vm.State["progress"]);
            Assert.Equal(2, vm.Collections["events"].Count);

            // Verify upsert mutates an existing item in place
            await vm.ApplyExternalDataPushAsync(
                stateUpdates: null,
                collectionName: "events",
                collectionMode: "upsert",
                records:
                [
                    new Dictionary<string, string>
                    {
                        ["id"] = "evt-1",
                        ["title"] = "启动流水线（已完成）",
                        ["time"] = "02:32",
                        ["badge"] = "DONE"
                    }
                ]);

            Assert.Equal(2, vm.Collections["events"].Count);
            CustomWidgetRecord updatedRecord = vm.Collections["events"].Single(r => r.Id == "evt-1");
            Assert.Equal("DONE", updatedRecord.Fields["badge"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AgentBridgePipe_And_McpStdioProtocol_RoundTripCleanly()
    {
        string pipeName = $"DeskBox_Agent_Test_{Guid.NewGuid():N}";
        await using var bridge = new DeskBoxAgentBridgeServer(pipeName, static () => null);
        bridge.Start();

        // 1. Direct Named Pipe round-trip
        string? pingResponse = await DeskBoxAgentBridgeClient.TrySendAsync(
            w => w.WriteString("op", "ping"),
            pipeName: pipeName,
            connectTimeoutMs: 2000);
        Assert.NotNull(pingResponse);
        using (JsonDocument pingDoc = JsonDocument.Parse(pingResponse!))
        {
            Assert.True(pingDoc.RootElement.GetProperty("ok").GetBoolean());
        }

        // 2. MCP JSON-RPC 2.0 stdio loop (newline-delimited)
        string rpcRequests = string.Join(
            "\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_validate_manifest","arguments":{"manifestJson":"{\"manifestVersion\":1,\"id\":\"custom.ok\",\"name\":\"OK\",\"root\":{\"type\":\"Text\",\"text\":\"Hi\"}}"}}}""") + "\n";

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(rpcRequests));
        using var outputStream = new MemoryStream();

        int exitCode = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
            inputOverride: inputStream,
            outputOverride: outputStream,
            pipeNameOverride: pipeName);
        Assert.Equal(0, exitCode);

        string[] responseLines = Encoding.UTF8.GetString(outputStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, responseLines.Length);

        using JsonDocument toolsListDoc = JsonDocument.Parse(responseLines[1]);
        string[] toolNames = toolsListDoc.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(
            [
                "deskbox_get_widget_spec",
                "deskbox_get_capability_spec",
                "deskbox_list_widgets",
                "deskbox_validate_manifest",
                "deskbox_upsert_widget",
                "deskbox_capture_widget_preview",
                "deskbox_push_widget_data"
            ],
            toolNames);

        using JsonDocument validateCallDoc = JsonDocument.Parse(responseLines[2]);
        Assert.False(validateCallDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task AgentConnector_MergesMcpConfigNonDestructively_And_PreparesWorkspace()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-agent-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string claudeJsonPath = Path.Combine(tempDir, ".claude.json");
            await File.WriteAllTextAsync(
                claudeJsonPath,
                """
                {
                  "numStartups": 42,
                  "mcpServers": {
                    "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] }
                  }
                }
                """,
                Encoding.UTF8);

            CustomWidgetAgentConnectorService.MergeMcpServerIntoJsonFile(
                claudeJsonPath,
                "deskbox",
                @"C:\Program Files\DeskBox\DeskBox.exe",
                includeStdioType: true);

            using JsonDocument merged = JsonDocument.Parse(await File.ReadAllTextAsync(claudeJsonPath, Encoding.UTF8));
            Assert.Equal(42, merged.RootElement.GetProperty("numStartups").GetInt32());
            JsonElement servers = merged.RootElement.GetProperty("mcpServers");
            Assert.True(servers.TryGetProperty("github", out _));
            JsonElement deskboxServer = servers.GetProperty("deskbox");
            Assert.Equal("stdio", deskboxServer.GetProperty("type").GetString());
            Assert.Equal(@"C:\Program Files\DeskBox\DeskBox.exe", deskboxServer.GetProperty("command").GetString());

            string cursorLink = CustomWidgetAgentConnectorService.BuildCursorMcpDeepLink(@"C:\DeskBox\DeskBox.exe");
            Assert.StartsWith("cursor://anysphere.cursor-deeplink/mcp/install?name=DeskBox&config=", cursorLink, StringComparison.Ordinal);

            string vscodeLink = CustomWidgetAgentConnectorService.BuildVsCodeMcpDeepLink(@"C:\DeskBox\DeskBox.exe");
            Assert.StartsWith("vscode:mcp/install?", vscodeLink, StringComparison.Ordinal);

            string widgetDir = Path.Combine(tempDir, "widget-sample");
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(widgetDir);
            Assert.True(File.Exists(Path.Combine(widgetDir, "manifest.schema.json")));
            Assert.True(File.Exists(Path.Combine(widgetDir, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(widgetDir, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(widgetDir, "CLAUDE.md")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TrayMenu_IncludesCreateAiWidgetEntryWhenCallbackProvided()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-tray-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            bool invoked = false;

            IReadOnlyList<DeskBox.Platform.NativeTrayMenuEntry> entries = DeskBox.App.BuildTrayMenuEntriesCore(
                localization,
                canCreateWidget: true,
                hasUpdateAvailable: false,
                availableUpdateVersion: null,
                () => Task.CompletedTask,
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                });

            DeskBox.Platform.NativeTrayMenuEntry aiEntry = entries.Single(
                e => !e.IsSeparator && e.Text == localization.T("Common.CreateAiWidget"));
            await aiEntry.Action!();
            Assert.True(invoked);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioPreferencesStore_PersistsOnboardingAndWebAiChoicesCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-studio-prefs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string prefsPath = Path.Combine(tempDir, "studio-prefs.json");
            var store = new CustomWidgetStudioPreferencesStore(prefsPath);

            Assert.False(store.HasSeenWelcomeGuide);
            Assert.False(store.HasSeenKeyUpsell);
            Assert.Equal(string.Empty, store.PreferredAiProviderId);
            Assert.Equal(0, store.PreferredStudioMode);
            Assert.True(store.LocateOnDesktopAfterCommit);
            Assert.Equal(CustomWidgetStudioPreferencesStore.TargetDoubao, store.ResolveEffectiveWebAiTarget("zh-CN"));
            Assert.Equal(CustomWidgetStudioPreferencesStore.TargetChatGpt, store.ResolveEffectiveWebAiTarget("en-US"));

            store.MarkWelcomeGuideSeen();
            store.MarkKeyUpsellSeen();
            store.SetPreferredAiProviderId("provider-deepseek");
            store.SetPreferredWebAiTarget(CustomWidgetStudioPreferencesStore.TargetDeepSeek);
            store.SetPreferredStudioMode(1);
            store.SetLocateOnDesktopAfterCommit(false);

            var reloaded = new CustomWidgetStudioPreferencesStore(prefsPath);
            Assert.True(reloaded.HasSeenWelcomeGuide);
            Assert.True(reloaded.HasSeenKeyUpsell);
            Assert.Equal("provider-deepseek", reloaded.PreferredAiProviderId);
            Assert.Equal(1, reloaded.PreferredStudioMode);
            Assert.False(reloaded.LocateOnDesktopAfterCommit);
            Assert.Equal(CustomWidgetStudioPreferencesStore.TargetDeepSeek, reloaded.ResolveEffectiveWebAiTarget("zh-CN"));
            Assert.Equal(CustomWidgetStudioPreferencesStore.TargetDeepSeek, reloaded.ResolveEffectiveWebAiTarget("en-US"));

            const string v1Manifest = """{"manifestVersion":1,"id":"custom.v1","name":"V1","root":{"type":"Text","text":"Initial"}}""";
            string refinementPrompt = CustomWidgetTemplates.BuildChatAiPrompt("把字号调大一点", existingManifestJson: v1Manifest);
            Assert.Contains("custom.v1", refinementPrompt, StringComparison.Ordinal);
            Assert.Contains("把字号调大一点", refinementPrompt, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorkshopFlow_ResolvesDirectProvider_FiltersClipboard_And_SummarizesPrompt()
    {
        CustomWidgetAiProviderPreset p1 = CustomWidgetAiGeneratorService.SupportedProviders[0];
        CustomWidgetAiProviderPreset p2 = CustomWidgetAiGeneratorService.SupportedProviders[1];

        Assert.Null(CustomWidgetWorkshopFlow.ResolveDirectProvider([], p1.Id));
        Assert.Equal(p2.Id, CustomWidgetWorkshopFlow.ResolveDirectProvider([p1, p2], p2.Id));
        Assert.Equal(p1.Id, CustomWidgetWorkshopFlow.ResolveDirectProvider([p1, p2], "missing"));
        Assert.Equal(p1.Id, CustomWidgetWorkshopFlow.ResolveDirectProvider([p1, p2], null));

        const string validReply = """
            好的，这是为你生成的格子：
            ```json
            {"manifestVersion":1,"id":"custom.water","name":"喝水打卡","root":{"type":"Text","text":"今天喝水"}}
            ```
            """;
        string selfPrompt = CustomWidgetTemplates.BuildChatAiPrompt("喝水打卡");

        Assert.False(CustomWidgetWorkshopFlow.TryExtractManifestReply(selfPrompt, selfPrompt, out _));
        Assert.False(CustomWidgetWorkshopFlow.TryExtractManifestReply("今天天气不错，没有 JSON", selfPrompt, out _));
        Assert.False(CustomWidgetWorkshopFlow.TryExtractManifestReply("""{"foo":"bar"}""", selfPrompt, out _));

        Assert.True(CustomWidgetWorkshopFlow.TryExtractManifestReply(
            validReply,
            selfPrompt,
            out string extractedJson));
        Assert.Contains("custom.water", extractedJson, StringComparison.Ordinal);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(extractedJson);
        Assert.True(parsed.IsValid);
        Assert.Equal("custom.water", parsed.Manifest!.Id);

        Assert.Equal(string.Empty, CustomWidgetWorkshopFlow.SummarizePrompt("   \r\n  "));
        Assert.Equal("把字号改大 一点", CustomWidgetWorkshopFlow.SummarizePrompt("  把字号改大\r\n  一点  "));
        string truncated = CustomWidgetWorkshopFlow.SummarizePrompt(new string('好', 50), maxLength: 10);
        Assert.Equal(new string('好', 10) + "…", truncated);

        Assert.Equal(2, CustomWidgetWorkshopFlow.ResolveGalleryColumnCount(720));
        Assert.Equal(3, CustomWidgetWorkshopFlow.ResolveGalleryColumnCount(960));
        Assert.Equal(304, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(800, 320));
        Assert.Equal(360, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(1100, 320));
        Assert.Equal(304, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(1100, 260));
        Assert.Equal(418, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(1100, 420));
        Assert.Equal(440, CustomWidgetWorkshopFlow.ResolvePreviewStageWidth(1360, 400));
    }

    [Fact]
    public void RemoteSettingsWidgetActions_EncodesAndDecodesStudioRequestsLosslessly()
    {
        (string? WidgetId, string? InitialIdea)[] cases =
        [
            (null, null),
            ("widget-123", null),
            (null, "喝水打卡"),
            ("widget-456", "字号调大一点\n并且使用深色背景")
        ];

        foreach ((string? expectedWidgetId, string? expectedIdea) in cases)
        {
            string encoded = RemoteSettingsWidgetActions.EncodeStudioRequest(expectedWidgetId, expectedIdea);
            (string? actualWidgetId, string? actualIdea) = RemoteSettingsWidgetActions.DecodeStudioRequest(encoded);
            Assert.Equal(expectedWidgetId, actualWidgetId);
            Assert.Equal(expectedIdea, actualIdea);
        }
    }

    [Fact]
    public void CustomWidgetSettings_IsTopLevelNavigationTabWithMultilineComposer()
    {
        string windowXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string routesSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml.cs"));
        string sectionXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml"));

        int menuStart = windowXaml.IndexOf("<NavigationView.MenuItems>", StringComparison.Ordinal);
        int menuEnd = windowXaml.IndexOf("</NavigationView.MenuItems>", menuStart, StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuEnd > menuStart);
        string primaryMenu = windowXaml[menuStart..menuEnd];

        int featureIndex = primaryMenu.IndexOf("Tag=\"FeatureWidgets\"", StringComparison.Ordinal);
        int interactionIndex = primaryMenu.IndexOf("Tag=\"Interaction\"", StringComparison.Ordinal);
        Assert.True(featureIndex >= 0);
        Assert.DoesNotContain("Tag=\"CustomWidgetSettings\"", primaryMenu, StringComparison.Ordinal);

        Assert.Contains(
            "[\"CustomWidgetSettings\"] = new(\"CustomWidgetSettings\", \"CustomWidget.Settings.Title\", null, \"FeatureWidgets\")",
            routesSource,
            StringComparison.Ordinal);

        int aiSettingsIndex = primaryMenu.IndexOf("Tag=\"AiSettings\"", StringComparison.Ordinal);
        Assert.True(aiSettingsIndex > featureIndex);
        Assert.True(interactionIndex > aiSettingsIndex);
        Assert.Contains("ai-settings.svg", primaryMenu, StringComparison.Ordinal);
        string aiNavIconSvg = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Assets/SettingsNavIcons/ai-settings.svg"));
        Assert.Contains("ic_fluent_bot_sparkle_20_color", aiNavIconSvg, StringComparison.Ordinal);
        Assert.Contains("radialGradient", aiNavIconSvg, StringComparison.Ordinal);

        Assert.Contains(
            "[\"AiSettings\"] = new(\"AiSettings\", \"Settings.Nav.AiSettings\", null, \"AiSettings\")",
            routesSource,
            StringComparison.Ordinal);

        // Unified 2-tab hub header & toolbar live in FeatureWidgetsSectionTemplate (Pattern A)
        Assert.Contains("x:Name=\"FeatureWidgetsHubSegmented\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CustomWidgetsToolbarPanel\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FeatureWidgetListHost\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CustomWidgetHostPanel\"", windowXaml, StringComparison.Ordinal);
        int apiDocsBtnIndex = windowXaml.IndexOf("Click=\"CustomWidgetsApiDocsButton_Click\"", StringComparison.Ordinal);
        int importBtnIndex = windowXaml.IndexOf("Click=\"CustomWidgetsImportButton_Click\"", StringComparison.Ordinal);
        int quickCreateBtnIndex = windowXaml.IndexOf("Click=\"CustomWidgetsQuickCreateButton_Click\"", StringComparison.Ordinal);
        Assert.True(apiDocsBtnIndex > 0 && importBtnIndex > apiDocsBtnIndex && quickCreateBtnIndex > importBtnIndex);

        // CustomWidgetSettingsSection stacks MyWidgets + Templates + AI Connection without nested Segmented tabs
        Assert.DoesNotContain("x:Name=\"StudioTabSegmented\"", sectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource SectionTitleTextStyle}\"", sectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"QuickCreateHeroCard\"", sectionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"QuickCreateComposerBorder\"", sectionXaml, StringComparison.Ordinal);
        int myWidgetsGridIndex = sectionXaml.IndexOf("x:Name=\"MyWidgetsCardsGrid\"", StringComparison.Ordinal);
        int templatesGridIndex = sectionXaml.IndexOf("x:Name=\"TemplatesCardsGrid\"", StringComparison.Ordinal);
        int aiEntryIndex = sectionXaml.IndexOf("x:Name=\"AiSettingsEntryCard\"", StringComparison.Ordinal);
        Assert.True(myWidgetsGridIndex > 0 && templatesGridIndex > myWidgetsGridIndex && aiEntryIndex > templatesGridIndex);
        Assert.DoesNotContain("x:Name=\"AiKeyExpander\"", sectionXaml, StringComparison.Ordinal);

        string aiSectionXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/AiSettingsSection.xaml"));
        Assert.Contains("x:Name=\"AiKeyExpander\"", aiSectionXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiInlineModelComboBox\"", aiSectionXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AiInlineThinkingComboBox\"", aiSectionXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WebAiTargetComboBox\"", aiSectionXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentHubHostPanel\"", aiSectionXaml, StringComparison.Ordinal);
        Assert.True(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.Custom));
    }

    [Fact]
    public async Task AiGeneratorService_StreamsSseWithReasoning_TestsConnection_FetchesModels_And_FallsBackOnUnsupportedThinkingParam()
    {
        // 1. Endpoint normalization & /models derivation across major domestic & global API formats
        Assert.Equal(
            "https://api.myproxy.example/v1/chat/completions",
            CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint("https://api.myproxy.example/"));
        Assert.Equal(
            "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
            CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint("https://ark.cn-beijing.volces.com/api/v3"));
        Assert.Equal(
            "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint("https://open.bigmodel.cn/api/paas/v4"));
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint("https://generativelanguage.googleapis.com/v1beta/openai"));
        Assert.Equal(
            "https://dashscope.aliyuncs.com/compatible-mode/v1/models",
            CustomWidgetAiGeneratorService.DeriveModelsEndpoint("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-deepseek-123456",
                CustomEndpoint: "https://relay.example/v1",
                CustomModel: "deepseek-chat",
                ThinkingMode: "enabled"));

            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "qwen",
                ApiKey: "sk-test-qwen-123456",
                CustomEndpoint: "https://relay.example/v1",
                CustomModel: "qwen3-coder-plus",
                ThinkingMode: "disabled"));

            int qwenPostCount = 0;
            using var httpClient = new HttpClient(new StubAiHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri!.ToString();
                if (req.Method == HttpMethod.Get && url.EndsWith("/models", StringComparison.Ordinal))
                {
                    const string modelsJson = """
                    {
                      "data": [
                        { "id": "text-embedding-3-small" },
                        { "id": "whisper-1" },
                        { "id": "deepseek-reasoner" },
                        { "id": "deepseek-chat" },
                        { "id": "custom-pro-model" }
                      ]
                    }
                    """;
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(modelsJson, Encoding.UTF8, "application/json")
                    };
                }

                string reqBody = await req.Content!.ReadAsStringAsync(ct);
                using JsonDocument reqDoc = JsonDocument.Parse(reqBody);
                JsonElement root = reqDoc.RootElement;

                // Simulate a strict third-party relay that rejects vendor-specific enable_thinking with HTTP 400
                if (root.TryGetProperty("enable_thinking", out _))
                {
                    qwenPostCount++;
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                    {
                        Content = new System.Net.Http.StringContent(
                            """{"error":{"message":"Unknown parameter: enable_thinking"}}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                bool isStream = root.TryGetProperty("stream", out JsonElement sEl) && sEl.GetBoolean();
                if (!isStream)
                {
                    qwenPostCount++;
                    return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new System.Net.Http.StringContent(
                            """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                // Reasoning-only model must NOT include temperature
                string model = root.GetProperty("model").GetString()!;
                if (model == "deepseek-reasoner")
                {
                    Assert.False(root.TryGetProperty("temperature", out _));
                }

                string ssePayload = string.Join(
                    "\n\n",
                    """data: {"choices":[{"delta":{"reasoning_content":"先规划喝水打卡布局，"}}]}""",
                    """data: {"choices":[{"delta":{"reasoning_content":"包含进度条与按钮。"}}]}""",
                    """data: {"choices":[{"delta":{"content":"```json\n{\"manifestVersion\":1,\"id\":\"custom.water.stream\","}}]}""",
                    """data: {"choices":[{"delta":{"content":"\"name\":\"流式饮水打卡\",\"root\":{\"type\":\"Text\",\"text\":\"今日 8 杯\"}}\n```"}}]}""",
                    "data: [DONE]") + "\n\n";

                var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(ssePayload, Encoding.UTF8)
                };
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return resp;
            }));

            var service = new CustomWidgetAiGeneratorService(credStore, httpClient);

            // 2. DeepSeek thinking=enabled automatically promotes deepseek-chat -> deepseek-reasoner
            ResolvedAiInvocation dsResolved = service.ResolveEffectiveSettings("deepseek");
            Assert.Equal("deepseek-reasoner", dsResolved.Model);
            Assert.Equal("https://relay.example/v1/chat/completions", dsResolved.Endpoint);
            Assert.True(dsResolved.UsesCustomEndpoint);

            // 3. FetchAvailableModelsAsync filters non-chat models and keeps recommended models first
            AiModelListResult modelList = await service.FetchAvailableModelsAsync("deepseek");
            Assert.True(modelList.Success);
            Assert.Equal(["deepseek-chat", "deepseek-reasoner", "custom-pro-model"], modelList.Models);

            // 4. TestConnectionAsync falls back cleanly when relay rejects enable_thinking with HTTP 400
            AiConnectionTestResult probe = await service.TestConnectionAsync("qwen");
            Assert.True(probe.Success);
            Assert.Equal(2, qwenPostCount);

            // 5. SSE Streaming emits Thinking -> GeneratingJson -> Validating with DetectedWidgetName
            var stages = new List<AiGenerationStage>();
            string? detectedName = null;
            int maxReasoningChars = 0;

            CustomWidgetAiGenerationResult gen = await service.GenerateOrRefineAsync(
                "deepseek",
                "做一个喝水打卡格子",
                onProgress: p =>
                {
                    stages.Add(p.Stage);
                    if (p.DetectedWidgetName is not null)
                    {
                        detectedName = p.DetectedWidgetName;
                    }
                    maxReasoningChars = Math.Max(maxReasoningChars, p.ReasoningChars);
                });

            Assert.True(gen.Success);
            Assert.Equal("deepseek-reasoner", gen.EffectiveModel);
            Assert.Contains(AiGenerationStage.Thinking, stages);
            Assert.Contains(AiGenerationStage.GeneratingJson, stages);
            Assert.Contains(AiGenerationStage.Validating, stages);
            Assert.Equal("流式饮水打卡", detectedName);
            Assert.True(maxReasoningChars > 5);
            Assert.Equal("custom.water.stream", gen.ParseResult!.Manifest!.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProvenanceProtection_PreservesUserAndAiWidgets_WhileUpgradingUnmodifiedOfficialPresets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Official unmodified v1.0.0 preset automatically upgrades to built-in latest version while keeping store.json intact
            var officialStore = new CustomWidgetDataStore("w-official", tempRoot);
            string olderOfficial = CustomWidgetManifestMetadataStamper.StampProvenance(
                """
                {
                  "manifestVersion": 1,
                  "id": "deskbox.sys-pulse",
                  "name": "Old Official Pulse",
                  "version": "1.0.0",
                  "root": { "type": "Text", "text": "old" }
                }
                """,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            await officialStore.WriteManifestJsonAsync(olderOfficial);
            await officialStore.SaveAsync(new CustomWidgetStorePayload
            {
                State = new Dictionary<string, string> { ["status"] = "running" }
            });

            string resolvedOfficial = await officialStore.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.SystemPulseTemplateId);
            CustomWidgetManifest upgradedManifest = CustomWidgetManifestParser.Parse(resolvedOfficial).Manifest!;
            Assert.NotEqual("1.0.0", upgradedManifest.Version);
            Assert.Equal(CustomWidgetOriginKinds.Official, upgradedManifest.Origin);
            Assert.False(upgradedManifest.IsCustomized);
            Assert.Equal("running", (await officialStore.LoadAsync()).State["status"]);

            // 2. User/AI customized widget is NEVER overwritten even if version is 1.0.0 and id matches an official preset
            var aiStore = new CustomWidgetDataStore("w-ai", tempRoot);
            string customizedAi = CustomWidgetManifestMetadataStamper.StampProvenance(
                """
                {
                  "manifestVersion": 1,
                  "id": "deskbox.sys-pulse",
                  "name": "My Personalized AI Board",
                  "version": "1.0.0",
                  "root": { "type": "Text", "text": "custom layout" }
                }
                """,
                origin: CustomWidgetOriginKinds.Ai,
                isCustomized: true,
                forkedFromId: "deskbox.sys-pulse",
                forkedFromVersion: "1.0.0");
            await aiStore.WriteManifestJsonAsync(customizedAi);

            string resolvedAi = await aiStore.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.SystemPulseTemplateId);
            CustomWidgetManifest preservedManifest = CustomWidgetManifestParser.Parse(resolvedAi).Manifest!;
            Assert.Equal("My Personalized AI Board", preservedManifest.Name);
            Assert.Equal(CustomWidgetOriginKinds.Ai, preservedManifest.Origin);
            Assert.True(preservedManifest.IsCustomized);
            Assert.Equal("deskbox.sys-pulse", preservedManifest.ForkedFromId);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PortableAttachmentPaths_And_FullBackupRestoreRebase_WorkAcrossDirectories()
    {
        string sourceRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-src-{Guid.NewGuid():N}");
        string targetRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-dst-{Guid.NewGuid():N}");
        string extDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(extDir);
        try
        {
            string sampleFile = Path.Combine(extDir, "architecture-notes.md");
            await File.WriteAllTextAsync(sampleFile, "# Portable attachment");

            string sourceDataDir = Path.Combine(sourceRoot, "data");
            Directory.CreateDirectory(sourceDataDir);
            var store = new CustomWidgetDataStore("drop-1", sourceDataDir);
            (string portablePath, _, _, _) = await store.ResolveDroppedFileWithPortablePathAsync(sampleFile, "copy");
            Assert.StartsWith("attachments/", portablePath, StringComparison.Ordinal);

            string resolvedOnSource = store.ResolveRuntimePath(portablePath);
            Assert.True(File.Exists(resolvedOnSource));
            Assert.True(store.IsInsideManagedAttachments(resolvedOnSource));

            // Write a legacy absolute path in store.json + valid settings.json, snapshot on sourceRoot, restore onto targetRoot
            await File.WriteAllTextAsync(
                Path.Combine(sourceDataDir, "settings.json"),
                JsonSerializer.Serialize(new AppSettings(), SettingsJsonContext.Default.AppSettings));
            await store.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.WorkLoggerTemplateId);
            await store.SaveAsync(new CustomWidgetStorePayload
            {
                Collections = new Dictionary<string, List<CustomWidgetRecord>>
                {
                    ["files"] =
                    [
                        new CustomWidgetRecord
                        {
                            Id = "rec-1",
                            Fields = new Dictionary<string, string>
                            {
                                ["name"] = "architecture-notes.md",
                                ["path"] = resolvedOnSource
                            }
                        }
                    ]
                }
            });

            var sourceBackup = new DeskBoxDataBackupService(sourceRoot, Path.Combine(sourceRoot, "recovery"));
            string archivePath = await sourceBackup.ExportBackupAsync(extDir);

            var targetBackup = new DeskBoxDataBackupService(targetRoot, Path.Combine(targetRoot, "recovery"));
            await targetBackup.PrepareRestoreAsync(archivePath);
            Assert.True(await targetBackup.ConfirmPendingFullRestoreAsync());
            DeskBoxRestoreApplyResult applyResult = await targetBackup.ApplyPendingRestoreAsync();
            Assert.True(applyResult.Succeeded);

            var targetStore = new CustomWidgetDataStore("drop-1", Path.Combine(targetRoot, "data"));
            CustomWidgetStorePayload restoredPayload = await targetStore.LoadAsync();
            string rebasedStoredPath = restoredPayload.Collections["files"][0].Fields["path"];
            Assert.Equal(portablePath, rebasedStoredPath);
            Assert.True(File.Exists(targetStore.ResolveRuntimePath(rebasedStoredPath)));
        }
        finally
        {
            try { Directory.Delete(sourceRoot, recursive: true); } catch { }
            try { Directory.Delete(targetRoot, recursive: true); } catch { }
            try { Directory.Delete(extDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CloudBackupCustomWidgetsDomain_MergesRecordsByUpdatedAt_ExcludesAttachments_And_RegistersLayout()
    {
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/cw-1/manifest.json"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/cw-1/store.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "widgets/cw-1/manifest.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "widgets/cw-1/todo.json"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/cw-1/attachments/huge.zip"));
        Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/_studio-draft/manifest.json"));

        string deviceARoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-cloudA-{Guid.NewGuid():N}");
        string deviceBRoot = Path.Combine(Path.GetTempPath(), $"deskbox-cw-cloudB-{Guid.NewGuid():N}");
        string cloudOutDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-cloudZ-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(deviceARoot, "data"));
        Directory.CreateDirectory(Path.Combine(deviceBRoot, "data"));
        Directory.CreateDirectory(cloudOutDir);
        try
        {
            // Device A has custom widget cw-sync with 2 records + 1 binary attachment (excluded from cloud zip)
            var storeA = new CustomWidgetDataStore("cw-sync", Path.Combine(deviceARoot, "data"));
            await storeA.WriteManifestJsonAsync(
                """
                {
                  "manifestVersion": 1,
                  "id": "custom.sync-notes",
                  "name": "跨设备同步笔记",
                  "icon": "📝",
                  "origin": "ai",
                  "isCustomized": true,
                  "updatedAtUtc": "2026-11-01T12:00:00Z",
                  "collections": [ { "name": "notes", "fields": [ { "name": "title" } ] } ],
                  "root": { "type": "Text", "text": "notes" }
                }
                """);
            Directory.CreateDirectory(storeA.AttachmentsDirectory);
            await File.WriteAllTextAsync(Path.Combine(storeA.AttachmentsDirectory, "local-only.bin"), "binary");

            long t1 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).UtcTicks;
            long t2 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).UtcTicks;
            await storeA.SaveAsync(new CustomWidgetStorePayload
            {
                Collections = new Dictionary<string, List<CustomWidgetRecord>>
                {
                    ["notes"] =
                    [
                        new CustomWidgetRecord
                        {
                            Id = "shared-item",
                            CreatedAtUtcTicks = t1,
                            UpdatedAtUtcTicks = t2,
                            Fields = new Dictionary<string, string> { ["title"] = "Updated on Device A", ["file"] = "attachments/local-only.bin" }
                        },
                        new CustomWidgetRecord
                        {
                            Id = "only-on-a",
                            CreatedAtUtcTicks = t2,
                            UpdatedAtUtcTicks = t2,
                            Fields = new Dictionary<string, string> { ["title"] = "Created on Device A" }
                        }
                    ]
                }
            });

            // Device A layout marks cw-sync as live
            await ResilientJsonStore.SaveAsync(
                Path.Combine(deviceARoot, "data", "widget-layout.json"),
                JsonSerializer.Serialize(
                    new WidgetLayoutDocument
                    {
                        Layout = new WidgetLayoutSettingsSlice
                        {
                            Widgets = [new WidgetConfig { Id = "cw-sync", WidgetKind = WidgetKind.Custom, Name = "跨设备同步笔记" }]
                        }
                    },
                    WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

            // Device B has an older version of shared-item + its own local item, and cw-sync is NOT yet in widget-layout.json
            var storeB = new CustomWidgetDataStore("cw-sync", Path.Combine(deviceBRoot, "data"));
            await storeB.SaveAsync(new CustomWidgetStorePayload
            {
                Collections = new Dictionary<string, List<CustomWidgetRecord>>
                {
                    ["notes"] =
                    [
                        new CustomWidgetRecord
                        {
                            Id = "shared-item",
                            CreatedAtUtcTicks = t1,
                            UpdatedAtUtcTicks = t1,
                            Fields = new Dictionary<string, string> { ["title"] = "Old on Device B" }
                        },
                        new CustomWidgetRecord
                        {
                            Id = "only-on-b",
                            CreatedAtUtcTicks = t1,
                            UpdatedAtUtcTicks = t1,
                            Fields = new Dictionary<string, string> { ["title"] = "Local on Device B" }
                        }
                    ]
                }
            });
            await ResilientJsonStore.SaveAsync(
                Path.Combine(deviceBRoot, "data", "widget-layout.json"),
                JsonSerializer.Serialize(
                    new WidgetLayoutDocument { Layout = new WidgetLayoutSettingsSlice() },
                    WidgetLayoutJsonContext.Default.WidgetLayoutDocument));

            var backupA = new DeskBoxDataBackupService(deviceARoot, Path.Combine(deviceARoot, "recovery"));
            string cloudZip = await backupA.ExportScopedBackupAsync(cloudOutDir, CloudBackupDomain.CustomWidgets);

            var backupB = new DeskBoxDataBackupService(deviceBRoot, Path.Combine(deviceBRoot, "recovery"));
            DeskBoxRestorePreparation prep = await backupB.PrepareScopedRestoreAsync(cloudZip, CloudBackupDomain.CustomWidgets);
            Assert.Contains("custom-widgets", prep.Domains!);
            Assert.Equal(1, prep.DomainItemCounts!.Single(c => c.Domain == "custom-widgets").Items);
            Assert.Equal(1, prep.AttachmentReferenceCount);

            // Merge mode (replaceItemData: false)
            Assert.True(await backupB.SetPendingRestoreItemReplaceModeAsync(replaceItemData: false));
            DeskBoxRestoreApplyResult result = await backupB.ApplyPendingRestoreAsync();
            Assert.True(result.Succeeded);

            // Attachments binary did NOT ship
            Assert.False(File.Exists(Path.Combine(storeB.AttachmentsDirectory, "local-only.bin")));

            // Store merged additively with newer UpdatedAtUtcTicks winning
            CustomWidgetStorePayload mergedStore = await storeB.ReloadFromDiskAsync();
            List<CustomWidgetRecord> notes = mergedStore.Collections["notes"];
            Assert.Equal(3, notes.Count);
            Assert.Equal("Updated on Device A", notes.Single(r => r.Id == "shared-item").Fields["title"]);
            Assert.Contains(notes, r => r.Id == "only-on-a");
            Assert.Contains(notes, r => r.Id == "only-on-b");

            // Layout on Device B now registers cw-sync with WidgetKind.Custom and emoji override!
            WidgetLayoutDocument layoutB = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(deviceBRoot, "data", "widget-layout.json")),
                WidgetLayoutJsonContext.Default.WidgetLayoutDocument)!;
            WidgetConfig registered = Assert.Single(layoutB.Layout.Widgets, w => w.Id == "cw-sync");
            Assert.Equal(WidgetKind.Custom, registered.WidgetKind);
            Assert.Equal("跨设备同步笔记", registered.Name);
            Assert.True(layoutB.Layout.FeatureWidgetEnabledStates[nameof(WidgetKind.Custom)]);
        }
        finally
        {
            try { Directory.Delete(deviceARoot, recursive: true); } catch { }
            try { Directory.Delete(deviceBRoot, recursive: true); } catch { }
            try { Directory.Delete(cloudOutDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PackageService_SanitizesLocalPathsAndSecrets_StripsStore_And_EnforcesVersionCompatibility()
    {
        string syntheticApiKey = string.Join(string.Empty, "sk", "-", "abcdefghijklmnopqrstuvwxyz", "123456");
        string rawWithSecrets = """
        {
          "manifestVersion": 1,
          "id": "custom.share-demo",
          "name": "Share Demo",
          "settings": [
            { "key": "folder", "type": "text", "label": "Dir", "default": "C:\\Users\\AliceSecret\\Desktop\\Private" },
            { "key": "token", "type": "text", "label": "Key", "default": "{{syntheticApiKey}}" }
          ],
          "root": { "type": "Text", "text": "hello" }
        }
        """;

        (string sanitizedJson, int sanitizedPaths, int strippedSecrets) =
            CustomWidgetPackageService.SanitizeManifestForSharing(rawWithSecrets.Replace("{{syntheticApiKey}}", syntheticApiKey, StringComparison.Ordinal));
        Assert.True(sanitizedPaths >= 1);
        Assert.True(strippedSecrets >= 1);
        Assert.DoesNotContain("AliceSecret", sanitizedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", sanitizedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", sanitizedJson, StringComparison.Ordinal);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string pkgPath = Path.Combine(tempDir, "demo.deskbox-widget");
            await File.WriteAllTextAsync(pkgPath, CustomWidgetPackageService.WrapInSharePackageEnvelope(sanitizedJson));

            (CustomWidgetManifest importedManifest, string importedJson) = await CustomWidgetTemplates.ReadPackageOrJsonAsync(pkgPath);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(importedJson);
            Assert.NotNull(parsed.Manifest);
            Assert.Equal(CustomWidgetOriginKinds.Community, importedManifest.Origin);
            Assert.Equal(CustomWidgetOriginKinds.Community, parsed.Manifest!.Origin);

            // Future schemaVersion / minAppVersion throws CustomWidgetIncompatibleVersionException
            string futurePkgPath = Path.Combine(tempDir, "future.deskbox-widget");
            await File.WriteAllTextAsync(
                futurePkgPath,
                """
                {
                  "manifestVersion": 99,
                  "minAppVersion": "99.0.0",
                  "id": "custom.future",
                  "name": "Future Widget",
                  "root": { "type": "Text", "text": "v99" }
                }
                """);
            await Assert.ThrowsAsync<CustomWidgetIncompatibleVersionException>(
                () => CustomWidgetTemplates.ReadPackageOrJsonAsync(futurePkgPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RemoteCatalog_And_ProtocolActivation_MergeFilterFallback_And_ValidateDeepLinks()
    {
        // 1. Protocol deep-link validation
        Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://studio?idea=%E5%96%9D%E6%B0%B4%E6%89%93%E5%8D%A1",
            out DeskBoxProtocolIntent? studioIntent,
            out _));
        Assert.Equal(DeskBoxProtocolAction.OpenStudio, studioIntent!.Action);
        Assert.Equal("喝水打卡", studioIntent.Idea);

        Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://install-widget?url=https%3A%2F%2Fdeskbox.fun%2Fwidgets%2Fpomodoro.json",
            out DeskBoxProtocolIntent? installIntent,
            out _));
        Assert.Equal(DeskBoxProtocolAction.InstallWidget, installIntent!.Action);
        Assert.Equal("https://deskbox.fun/widgets/pomodoro.json", installIntent.RemoteManifestUri!.ToString());

        // Reject untrusted domain or non-HTTPS URL in install-widget
        Assert.False(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://install-widget?url=https%3A%2F%2Fevil.example.com%2Fwidget.json",
            out _,
            out _));
        Assert.False(DeskBoxProtocolActivationService.TryParseProtocolUri(
            "deskbox://install-widget?url=http%3A%2F%2Fdeskbox.fun%2Fwidget.json",
            out _,
            out _));

        // 2. Remote Catalog merge, deprecation hiding, upgrade-required badge, and offline fallback
        string cacheDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        try
        {
            const string remoteCatalogJson = """
            {
              "schemaVersion": 1,
              "updatedAtUtc": "2026-11-01T12:00:00Z",
              "widgets": [
                {
                  "id": "pomodoro-focus",
                  "manifestId": "custom.pomodoro",
                  "name": "番茄专注时钟",
                  "nameEn": "Pomodoro Focus",
                  "description": "云端下发的番茄钟模板",
                  "iconGlyph": "🍅",
                  "version": "1.2.0",
                  "manifest": {
                    "manifestVersion": 1,
                    "id": "custom.pomodoro",
                    "name": "番茄专注时钟",
                    "version": "1.2.0",
                    "root": { "type": "Text", "text": "25:00" }
                  }
                },
                {
                  "id": "retired-demo",
                  "name": "已下架模板",
                  "deprecated": true,
                  "version": "1.0.0"
                },
                {
                  "id": "future-3d-widget",
                  "manifestId": "custom.future3d",
                  "name": "未来新格子",
                  "minSchemaVersion": 99,
                  "minAppVersion": "99.0.0",
                  "version": "2.0.0"
                }
              ]
            }
            """;

            var onlineCatalog = new CustomWidgetRemoteCatalogService(
                cacheDir,
                new StubAiHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(remoteCatalogJson, Encoding.UTF8, "application/json")
                })));

            Assert.True(await onlineCatalog.RefreshInBackgroundAsync(force: true));
            IReadOnlyList<CustomWidgetCatalogEntry> merged = onlineCatalog.GetMergedOfficialTemplates();
            Assert.Contains(merged, e => e.Id == "pomodoro-focus" && !e.RequiresClientUpgrade);
            Assert.DoesNotContain(merged, e => e.Id == "retired-demo");
            Assert.Contains(merged, e => e.Id == "future-3d-widget" && e.RequiresClientUpgrade);
            Assert.True(onlineCatalog.TryGetRemoteTemplateJson("pomodoro-focus", out string? pomodoroJson));
            Assert.Contains("custom.pomodoro", pomodoroJson, StringComparison.Ordinal);

            // Offline fallback when network fails on empty cache still returns all built-in presets
            string emptyCacheDir = Path.Combine(cacheDir, "offline");
            var offlineCatalog = new CustomWidgetRemoteCatalogService(
                emptyCacheDir,
                new StubAiHttpMessageHandler((_, _) => throw new HttpRequestException("Network unreachable")));
            Assert.False(await offlineCatalog.RefreshInBackgroundAsync(force: true));
            Assert.Equal(CustomWidgetTemplates.Presets.Count, offlineCatalog.GetMergedOfficialTemplates().Count);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NetworkSecurity_And_NetworkSourceEngine_BlockSsrf_ParseRssAtomAndHttpJson_And_CacheOffline()
    {
        // 1. SSRF & private/loopback/metadata IP guard
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("http://example.com/feed.xml", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://localhost/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://127.0.0.1/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://10.0.0.5/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://192.168.1.1/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://169.254.169.254/latest/meta-data", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://100.64.0.1/api", out _, out _));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.IPv6Loopback));
        Assert.True(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://hnrss.org/frontpage", out _, out _));

        // 2. Interval floor clamping (>= 300_000 ms for network sources)
        CustomWidgetParseResult parsedRssManifest = CustomWidgetManifestParser.Parse(
            """
            {
              "manifestVersion": 1,
              "id": "custom.rss-test",
              "name": "RSS Test",
              "collections": [ { "name": "articles", "fields": [ { "name": "title" }, { "name": "link" } ] } ],
              "sources": [
                {
                  "type": "rss",
                  "url": "https://feeds.example.com/rss.xml",
                  "refreshMs": 1000,
                  "targetCollection": "articles",
                  "maxItems": 5
                }
              ],
              "root": { "type": "Button", "text": "Refresh", "actions": [ { "type": "source.refresh" } ] }
            }
            """);
        Assert.NotNull(parsedRssManifest.Manifest);
        Assert.DoesNotContain(parsedRssManifest.Diagnostics, d => d.IsError);
        Assert.True(parsedRssManifest.Manifest!.Sources[0].IntervalMs >= CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs);

        // 3. RSS 2.0, Atom 1.0, and httpJson extraction + disk cache hydration
        string cacheDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-net-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        try
        {
            const string rssXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0">
              <channel>
                <title>Tech Daily</title>
                <item>
                  <title>DeskBox Custom Widgets Released</title>
                  <link>https://deskbox.fun/news/1</link>
                  <pubDate>Sun, 01 Nov 2026 10:00:00 GMT</pubDate>
                  <description>&lt;p&gt;Native WinUI declarative widgets.&lt;/p&gt;</description>
                </item>
              </channel>
            </rss>
            """;

            const string atomXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Updates</title>
              <entry>
                <id>tag:example.com,2026:1</id>
                <title>Atom Entry One</title>
                <link rel="alternate" href="https://example.com/atom/1" />
                <updated>2026-11-01T11:00:00Z</updated>
                <summary>Clean summary</summary>
              </entry>
            </feed>
            """;

            const string apiJson = """
            {
              "meta": { "status": "healthy", "total": 42 },
              "data": {
                "releases": [
                  { "tag": "v2.1.0", "title": "Autumn Update", "html_url": "https://deskbox.fun/r/2.1" }
                ]
              }
            }
            """;

            var engine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                new StubAiHttpMessageHandler((req, _) =>
                {
                    string u = req.RequestUri!.ToString();
                    string body = u.Contains("atom", StringComparison.Ordinal)
                        ? atomXml
                        : u.Contains("api.json", StringComparison.Ordinal)
                            ? apiJson
                            : rssXml;
                    string mediaType = u.Contains("api.json", StringComparison.Ordinal) ? "application/json" : "application/xml";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, mediaType)
                    });
                }));

            CustomWidgetSourceSpec rssSpec = parsedRssManifest.Manifest.Sources[0];
            CustomWidgetNetworkFetchOutcome rssOut = await engine.FetchAsync(rssSpec, "https://feeds.example.com/rss.xml");
            Assert.True(rssOut.Succeeded);
            Assert.Single(rssOut.Items);
            Assert.Equal("DeskBox Custom Widgets Released", rssOut.Items[0].Fields["title"]);
            Assert.Equal("https://deskbox.fun/news/1", rssOut.Items[0].Fields["link"]);
            Assert.Equal("Native WinUI declarative widgets.", rssOut.Items[0].Fields["summary"]);

            // Offline disk cache loads immediately on startup
            CustomWidgetNetworkFetchOutcome? cachedRss = engine.TryLoadCachedOutcome(rssSpec, "https://feeds.example.com/rss.xml");
            Assert.NotNull(cachedRss);
            Assert.True(cachedRss!.FromDiskCache);
            Assert.Single(cachedRss.Items);

            // Atom 1.0 feed
            CustomWidgetNetworkFetchOutcome atomOut = await engine.FetchAsync(rssSpec, "https://feeds.example.com/atom.xml");
            Assert.True(atomOut.Succeeded);
            Assert.Equal("Atom Entry One", atomOut.Items[0].Fields["title"]);
            Assert.Equal("https://example.com/atom/1", atomOut.Items[0].Fields["link"]);

            // httpJson with itemsPath + fieldMap + stateMap
            var jsonSpec = CustomWidgetManifestParser.Parse(
                """
                {
                  "manifestVersion": 1,
                  "id": "custom.json-test",
                  "name": "JSON Test",
                  "state": { "serviceStatus": "", "count": "0" },
                  "collections": [ { "name": "items", "fields": [ { "name": "ver" }, { "name": "name" }, { "name": "url" } ] } ],
                  "sources": [
                    {
                      "type": "httpJson",
                      "url": "https://feeds.example.com/api.json",
                      "targetCollection": "items",
                      "itemsPath": "data.releases",
                      "fieldMap": { "ver": "tag", "name": "title", "url": "html_url" },
                      "stateMap": { "serviceStatus": "meta.status", "count": "meta.total" }
                    }
                  ],
                  "root": { "type": "Text", "text": "{state.serviceStatus}" }
                }
                """).Manifest!.Sources[0];

            CustomWidgetNetworkFetchOutcome jsonOut = await engine.FetchAsync(jsonSpec, "https://feeds.example.com/api.json");
            Assert.True(jsonOut.Succeeded);
            Assert.Equal("healthy", jsonOut.StateUpdates["serviceStatus"]);
            Assert.Equal("42", jsonOut.StateUpdates["count"]);
            Assert.Single(jsonOut.Items);
            Assert.Equal("v2.1.0", jsonOut.Items[0].Fields["ver"]);
            Assert.Equal("Autumn Update", jsonOut.Items[0].Fields["name"]);
            Assert.Equal("https://deskbox.fun/r/2.1", jsonOut.Items[0].Fields["url"]);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ManifestI18n_ResolvesExactPrimaryAndFallbackLocales_And_LiveSwitchesViewModelWithoutMutatingManifestOnDisk()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-i18n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            settingsService.Settings.Language = "zh-CN";
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("hydration-i18n-test", tempDir);

            await store.WriteManifestJsonAsync(CustomWidgetTemplates.HydrationStarterManifestJson);
            string diskManifestBefore = await File.ReadAllTextAsync(store.ManifestPath, Encoding.UTF8);

            CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(diskManifestBefore).Manifest!;
            Assert.NotNull(manifest.I18n);
            Assert.Equal("今日饮水打卡", manifest.ResolveLocalizedName("zh-CN"));
            Assert.Equal("今日飲水打卡", manifest.ResolveLocalizedName("zh-TW"));
            Assert.Equal("Hydration Tracker", manifest.ResolveLocalizedName("en-US"));
            Assert.Equal("水分補給トラッカー", manifest.ResolveLocalizedName("ja-JP"));
            Assert.Equal("오늘의 수분 섭취", manifest.ResolveLocalizedName("ko-KR"));
            // Unknown / western locale falls back cleanly to en-US
            Assert.Equal("Hydration Tracker", manifest.ResolveLocalizedName("fr-FR"));
            Assert.Equal("Hydration Tracker", manifest.ResolveLocalizedName("en-GB"));

            var cfg = new WidgetConfig
            {
                Id = "hydration-i18n-test",
                Name = string.Empty,
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(
                cfg,
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);

            int rebuildCount = 0;
            vm.ViewRebuildRequested += (_, _) => rebuildCount++;

            await vm.InitializeAsync();
            Assert.Equal("今日饮水打卡", cfg.Name);
            Assert.Contains("饮水", vm.CapsuleTitle, StringComparison.Ordinal);
            rebuildCount = 0;

            // Switch UI language to English -> live ViewRebuildRequested + Config.Name + CapsuleTitle switch immediately
            localization.SetLanguage("en-US");
            Assert.Equal(1, rebuildCount);
            Assert.Equal("Hydration Tracker", cfg.Name);
            Assert.Contains("Water", vm.CapsuleTitle, StringComparison.Ordinal);

            // Switch UI language to Japanese -> updates again
            localization.SetLanguage("ja-JP");
            Assert.Equal(2, rebuildCount);
            Assert.Equal("水分補給トラッカー", cfg.Name);
            Assert.Contains("水分補給", vm.CapsuleTitle, StringComparison.Ordinal);

            // Custom user-renamed widget title must NOT be overwritten on language change
            cfg.Name = "My Custom Cup";
            localization.SetLanguage("zh-TW");
            Assert.Equal(3, rebuildCount);
            Assert.Equal("My Custom Cup", cfg.Name);

            // Verify manifest.json on disk was never mutated by language switching
            string diskManifestAfter = await File.ReadAllTextAsync(store.ManifestPath, Encoding.UTF8);
            Assert.Equal(diskManifestBefore, diskManifestAfter);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AllThirteenOfficialTemplatesAndStarters_ParseCleanly_And_RenderLocalizedCopyInChineseAndEnglish()
    {
        string[] allManifestJsons =
        [
            CustomWidgetTemplates.SystemPulseManifestJson,
            CustomWidgetTemplates.PomodoroManifestJson,
            CustomWidgetTemplates.CountdownManifestJson,
            CustomWidgetTemplates.AiPulseManifestJson,
            CustomWidgetTemplates.WorkLoggerManifestJson,
            CustomWidgetTemplates.MilestoneTrackerManifestJson,
            CustomWidgetTemplates.QuickSnippetsManifestJson,
            CustomWidgetTemplates.HydrationStarterManifestJson,
            CustomWidgetTemplates.ReleaseChecklistStarterManifestJson,
            CustomWidgetTemplates.AgentHudStarterManifestJson,
            CustomWidgetTemplates.MiniHardwareStarterManifestJson,
            CustomWidgetTemplates.FileDropStarterManifestJson,
            CustomWidgetTemplates.NewsRssStarterManifestJson
        ];

        foreach (string json in allManifestJsons)
        {
            CustomWidgetParseResult validated = CustomWidgetManifestParser.ValidateWithRuntimeHints(json);
            Assert.True(validated.IsValid, string.Join("; ", validated.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
            Assert.Empty(validated.Diagnostics);

            CustomWidgetManifest manifest = validated.Manifest!;
            Assert.NotNull(manifest.I18n);
            Assert.Contains("en-US", manifest.I18n!.Keys);
            Assert.Contains("zh-CN", manifest.I18n.Keys);

            string zhName = manifest.ResolveLocalizedName("zh-CN");
            string enName = manifest.ResolveLocalizedName("en-US");
            Assert.NotEmpty(zhName);
            Assert.NotEmpty(enName);
            Assert.NotEqual(zhName, enName);
            Assert.DoesNotContain("$i18n", zhName, StringComparison.Ordinal);
            Assert.DoesNotContain("$i18n", enName, StringComparison.Ordinal);

            var enCtx = new CustomWidgetEvaluationContext
            {
                CultureName = "en-US",
                I18n = manifest.ResolveI18nDictionary("en-US"),
                State = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal),
                Settings = manifest.Settings.ToDictionary(s => s.Key, s => s.DefaultValue, StringComparer.Ordinal)
            };

            if (manifest.Capsule is not null)
            {
                string capTitleEn = CustomWidgetExpressionEvaluator.Interpolate(manifest.Capsule.Title, enCtx);
                string capSummaryEn = CustomWidgetExpressionEvaluator.Interpolate(manifest.Capsule.Summary, enCtx);
                Assert.DoesNotContain("$i18n.", capTitleEn, StringComparison.Ordinal);
                Assert.DoesNotContain("$i18n.", capSummaryEn, StringComparison.Ordinal);
            }

            foreach (CustomWidgetSettingDefinition setting in manifest.Settings)
            {
                string labelEn = manifest.ResolveLocalizedText(setting.Label, "en-US");
                Assert.NotEmpty(labelEn);
                Assert.DoesNotContain("$i18n.", labelEn, StringComparison.Ordinal);
                foreach (CustomWidgetOptionSpec opt in setting.Options)
                {
                    string optEn = manifest.ResolveLocalizedText(opt.Label, "en-US");
                    Assert.NotEmpty(optEn);
                    Assert.DoesNotContain("$i18n.", optEn, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void MultilingualPrompts_RouteZhAndUniversalWrappers_InjectTargetLanguage_And_ExcludeChineseBoilerplateInNonChinese()
    {
        string zhPrompt = CustomWidgetTemplates.BuildChatAiPrompt("做一个喝水打卡", uiLanguage: "zh-CN");
        Assert.Contains("Simplified Chinese (简体中文)", zhPrompt, StringComparison.Ordinal);
        Assert.Contains("输出铁律", zhPrompt, StringComparison.Ordinal);

        string enPrompt = CustomWidgetTemplates.BuildChatAiPrompt("Build a sprint timer", uiLanguage: "en-US");
        Assert.Contains("Strict Output Rules", enPrompt, StringComparison.Ordinal);
        Assert.Contains("current DeskBox UI language (English)", enPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("输出铁律", enPrompt, StringComparison.Ordinal);

        string jaPrompt = CustomWidgetTemplates.BuildChatAiPrompt("毎日の読書記録ウィジェット", uiLanguage: "ja-JP");
        Assert.Contains("Strict Output Rules", jaPrompt, StringComparison.Ordinal);
        Assert.Contains("Japanese (日本語)", jaPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("输出铁律", jaPrompt, StringComparison.Ordinal);

        string repairEn = CustomWidgetTemplates.BuildChatAiRepairPrompt(
            "{ \"schemaVersion\": 1 }",
            [new CustomWidgetValidationDiagnostic("$.view", "Missing root view")],
            uiLanguage: "de-DE");
        Assert.Contains("German (Deutsch)", repairEn, StringComparison.Ordinal);
        Assert.Contains("`$.view`: Missing root view", repairEn, StringComparison.Ordinal);
        Assert.DoesNotContain("校验未通过", repairEn, StringComparison.Ordinal);

        string agentKo = CustomWidgetTemplates.BuildLocalAgentPrompt(
            "Make a compact Pomodoro widget",
            @"C:\Data\SKILL.md",
            @"C:\Data\w1\manifest.json",
            @"C:\Data",
            uiLanguage: "ko-KR");
        Assert.Contains("Korean (한국어)", agentKo, StringComparison.Ordinal);
        Assert.Contains(@"C:\Data\SKILL.md", agentKo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemotePromptService_FetchesModularOverrides_ValidatesVersionAndTokens_BlocksDowngrades_And_FallsBackOffline()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-prompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        try
        {
            // 1. Valid modular remote override (promptVersion = 2 > BuiltInPromptVersion = 1)
            const string validRemoteJson = """
            {
              "schemaVersion": 1,
              "promptVersion": 2,
              "minAppVersion": "0.1.0",
              "systemPrompt": "OTA System Architect ({{TARGET_LANGUAGE}}) :: {{EXTRA_DESIGN_RULES}}\n{{SKILL_MARKDOWN}}",
              "extraDesignRules": "- OTA_RULE_42: Prefer crisp tabular metrics for numeric counters."
            }
            """;

            var onlineService = new CustomWidgetRemotePromptService(
                cacheDir,
                new StubAiHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(validRemoteJson, Encoding.UTF8, "application/json")
                })),
                currentAppVersionOverride: new Version(1, 5, 0, 0));

            Assert.True(await onlineService.RefreshInBackgroundAsync(force: true));
            CustomWidgetEffectivePromptPack pack = onlineService.GetEffectivePromptPack();
            Assert.True(pack.IsRemoteOverride);
            Assert.Equal(2, pack.PromptVersion);
            Assert.Contains("OTA_RULE_42", pack.ExtraDesignRules, StringComparison.Ordinal);
            Assert.Contains("OTA_RULE_42", pack.SkillMarkdown, StringComparison.Ordinal);

            string enChatWithOta = CustomWidgetTemplates.BuildChatAiPrompt(
                "Create a release tracker",
                uiLanguage: "en-US",
                promptPack: pack);
            Assert.Contains("OTA_RULE_42", enChatWithOta, StringComparison.Ordinal);

            // 2. Offline cold start hydrates from persisted disk cache (prompts.v1.json)
            var coldOfflineService = new CustomWidgetRemotePromptService(
                cacheDir,
                new StubAiHttpMessageHandler((_, _) => throw new HttpRequestException("Offline")),
                currentAppVersionOverride: new Version(1, 5, 0, 0));
            Assert.False(await coldOfflineService.RefreshInBackgroundAsync(force: true));
            CustomWidgetEffectivePromptPack hydratedPack = coldOfflineService.GetEffectivePromptPack();
            Assert.True(hydratedPack.IsRemoteOverride);
            Assert.Equal(2, hydratedPack.PromptVersion);

            // 3. Reject downgrade (promptVersion <= 1), future schemaVersion, future minAppVersion, and broken template missing required tokens
            Assert.False(onlineService.IsValidCompatibleOverride(new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 1,
                PromptVersion = 1,
                ExtraDesignRules = "downgrade"
            }));
            Assert.False(onlineService.IsValidCompatibleOverride(new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 99,
                PromptVersion = 5,
                ExtraDesignRules = "future schema"
            }));
            Assert.False(onlineService.IsValidCompatibleOverride(new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 1,
                PromptVersion = 5,
                MinAppVersion = "99.0.0",
                ExtraDesignRules = "needs newer app"
            }));
            Assert.False(onlineService.IsValidCompatibleOverride(new CustomWidgetRemotePromptDocument
            {
                SchemaVersion = 1,
                PromptVersion = 5,
                WebPromptTemplateEn = "Broken template missing required placeholders"
            }));
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AgentHttpBridge_LoopbackAuth_StreamableMcp_RestEndpoints_And_SelfTest_WorkEndToEnd()
    {
        string pipeName = $"DeskBox_Agent_HttpTest_{Guid.NewGuid():N}";
        const string testToken = "dbx_unit_test_secret_token_0123456789abcdef";

        await using var bridge = new DeskBoxAgentBridgeServer(pipeName, static () => null);
        bridge.Start();

        await using var httpBridge = new DeskBoxAgentHttpBridgeServer(
            port: 0,
            tokenProvider: static () => testToken,
            bridgeJsonDispatcher: bridge.DispatchRequestJsonAsync);
        Assert.True(httpBridge.TryStart(out string? startError), startError);

        int port = httpBridge.ActualPort;
        Assert.True(port > 0);
        Assert.True(httpBridge.IsRunning);

        using var httpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

        // 1. Missing or wrong token returns 401 Unauthorized
        using (HttpResponseMessage unauthResp = await httpClient.GetAsync("/api/v1/health"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);
        }

        using (var wrongReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health"))
        {
            wrongReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "dbx_wrong_token");
            using HttpResponseMessage wrongResp = await httpClient.SendAsync(wrongReq);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongResp.StatusCode);
        }

        // 2. Authenticated /api/v1/health and X-DeskBox-Token fallback both return 200 OK
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", testToken);
        using (HttpResponseMessage healthResp = await httpClient.GetAsync("/api/v1/health"))
        {
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
            using JsonDocument healthDoc = JsonDocument.Parse(await healthResp.Content.ReadAsStringAsync());
            Assert.True(healthDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(port, healthDoc.RootElement.GetProperty("result").GetProperty("port").GetInt32());
        }

        // 3. REST /api/v1/spec, /api/v1/schema, and /api/v1/widgets/validate
        using (HttpResponseMessage specResp = await httpClient.GetAsync("/api/v1/spec"))
        {
            Assert.Equal(HttpStatusCode.OK, specResp.StatusCode);
            string specMd = await specResp.Content.ReadAsStringAsync();
            Assert.Contains("name: deskbox-widget", specMd, StringComparison.Ordinal);
        }

        using (HttpResponseMessage schemaResp = await httpClient.GetAsync("/api/v1/schema"))
        {
            Assert.Equal(HttpStatusCode.OK, schemaResp.StatusCode);
            string schemaJson = await schemaResp.Content.ReadAsStringAsync();
            Assert.Contains("schemaVersion", schemaJson, StringComparison.Ordinal);
        }

        using (HttpResponseMessage validateResp = await httpClient.PostAsync(
                   "/api/v1/widgets/validate",
                   new StringContent(
                       """{"manifestJson":"{\"schemaVersion\":1,\"id\":\"custom.http\",\"name\":\"HTTP OK\",\"view\":{\"type\":\"Text\",\"text\":\"Hi\"}}"}""",
                       Encoding.UTF8,
                       "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, validateResp.StatusCode);
            using JsonDocument valDoc = JsonDocument.Parse(await validateResp.Content.ReadAsStringAsync());
            Assert.True(valDoc.RootElement.GetProperty("ok").GetBoolean());
            JsonElement valRes = valDoc.RootElement.GetProperty("result");
            Assert.True(valRes.GetProperty("isValid").GetBoolean());
            Assert.Equal("custom.http", valRes.GetProperty("id").GetString());
        }

        // 4. MCP Streamable HTTP (/mcp): GET -> 405, notification -> 202, initialize + batch tools/list & tools/call -> 200
        using (HttpResponseMessage getMcpResp = await httpClient.GetAsync("/mcp"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, getMcpResp.StatusCode);
        }

        using (HttpResponseMessage notifResp = await httpClient.PostAsync(
                   "/mcp",
                   new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Accepted, notifResp.StatusCode);
        }

        const string batchRpc = """
            [
              {"jsonrpc":"2.0","id":10,"method":"initialize","params":{"protocolVersion":"2025-03-26"}},
              {"jsonrpc":"2.0","method":"notifications/initialized"},
              {"jsonrpc":"2.0","id":11,"method":"tools/list"},
              {"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"deskbox_validate_manifest","arguments":{"manifestJson":"{\"schemaVersion\":1,\"id\":\"custom.mcp.http\",\"name\":\"MCP HTTP\",\"view\":{\"type\":\"Text\",\"text\":\"Hello\"}}"}}}
            ]
            """;
        using (HttpResponseMessage batchResp = await httpClient.PostAsync(
                   "/mcp",
                   new StringContent(batchRpc, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
            using JsonDocument batchDoc = JsonDocument.Parse(await batchResp.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Array, batchDoc.RootElement.ValueKind);
            Assert.Equal(3, batchDoc.RootElement.GetArrayLength());
            Assert.Equal(7, batchDoc.RootElement[1].GetProperty("result").GetProperty("tools").GetArrayLength());
            Assert.False(batchDoc.RootElement[2].GetProperty("result").GetProperty("isError").GetBoolean());
        }

        // 5. Self-test over Named Pipe succeeds and reports all 6 MCP tools
        AgentBridgeSelfTestResult selfTest = await CustomWidgetAgentConnectorService.RunBridgeSelfTestAsync(pipeName);
        Assert.True(selfTest.McpProtocolOk);
        Assert.Equal(7, selfTest.McpToolCount);
        Assert.True(selfTest.PipeOnline);
        Assert.Equal(Environment.ProcessId, selfTest.HostPid);
    }

    [Fact]
    public async Task UniversalAgentConnector_NineClients_CodexToml_VsCodeServers_MultiFormatSnippets_FileInjector_Skills_And_Playbooks_WorkCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-univ-agent-{Guid.NewGuid():N}");
        string fakeHome = Path.Combine(tempRoot, "home");
        string fakeAppData = Path.Combine(tempRoot, "appdata");
        string fakeLocalAppData = Path.Combine(tempRoot, "localappdata");
        Directory.CreateDirectory(fakeHome);
        Directory.CreateDirectory(fakeAppData);
        Directory.CreateDirectory(fakeLocalAppData);

        try
        {
            const string fakeExe = @"C:\Program Files\DeskBox\DeskBox.exe";

            // 1. Seed OpenAI Codex config.toml with existing user settings + VS Code User directory
            string codexDir = Path.Combine(fakeHome, ".codex");
            Directory.CreateDirectory(codexDir);
            string codexToml = Path.Combine(codexDir, "config.toml");
            await File.WriteAllTextAsync(
                codexToml,
                """
                model = "gpt-5-codex"
                approval_policy = "on-request"

                [mcp_servers.other]
                command = 'npx'
                args = ["-y", "other-mcp"]
                """,
                Encoding.UTF8);

            Directory.CreateDirectory(Path.Combine(fakeAppData, "Code", "User"));

            IReadOnlyList<ExternalAgentClientStatus> initialClients =
                CustomWidgetAgentConnectorService.DetectAgentClients(fakeHome, fakeAppData, fakeLocalAppData);
            Assert.Equal(11, initialClients.Count);

            ExternalAgentClientStatus codexBefore = initialClients.Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientCodex);
            Assert.True(codexBefore.IsInstalled);
            Assert.False(codexBefore.IsConfigured);

            // 2. Connect OpenAI Codex (TOML merge + .deskbox.bak + skill sync) and VS Code ("servers" root key)
            (bool codexOk, _) = await CustomWidgetAgentConnectorService.ConnectClientAsync(
                CustomWidgetAgentConnectorService.ClientCodex,
                fakeExe,
                fakeHome,
                fakeAppData);
            Assert.True(codexOk);
            Assert.True(File.Exists(codexToml + ".deskbox.bak"));
            string mergedToml = await File.ReadAllTextAsync(codexToml, Encoding.UTF8);
            Assert.Contains("model = \"gpt-5-codex\"", mergedToml, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.other]", mergedToml, StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.deskbox]", mergedToml, StringComparison.Ordinal);
            Assert.Contains($"command = '{fakeExe}'", mergedToml, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(fakeHome, ".codex", "skills", "deskbox-widget", "SKILL.md")));

            // Re-merging updates in-place without duplicating [mcp_servers.deskbox]
            CustomWidgetAgentConnectorService.MergeMcpServerIntoCodexTomlFile(codexToml, "deskbox", @"D:\DeskBox\DeskBox.exe");
            string remergedToml = await File.ReadAllTextAsync(codexToml, Encoding.UTF8);
            Assert.Equal(1, remergedToml.Split("[mcp_servers.deskbox]", StringSplitOptions.None).Length - 1);
            Assert.Contains(@"command = 'D:\DeskBox\DeskBox.exe'", remergedToml, StringComparison.Ordinal);

            (bool vscodeOk, string vscodeCfgPath) = await CustomWidgetAgentConnectorService.ConnectClientAsync(
                CustomWidgetAgentConnectorService.ClientVsCode,
                fakeExe,
                fakeHome,
                fakeAppData);
            Assert.True(vscodeOk);
            using (JsonDocument vsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(vscodeCfgPath, Encoding.UTF8)))
            {
                JsonElement vsDeskbox = vsDoc.RootElement.GetProperty("servers").GetProperty("deskbox");
                Assert.Equal("stdio", vsDeskbox.GetProperty("type").GetString());
                Assert.Equal(fakeExe, vsDeskbox.GetProperty("command").GetString());
            }

            IReadOnlyList<ExternalAgentClientStatus> afterClients =
                CustomWidgetAgentConnectorService.DetectAgentClients(fakeHome, fakeAppData, fakeLocalAppData);
            Assert.True(afterClients.Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientCodex).IsConfigured);
            Assert.True(afterClients.Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientVsCode).IsConfigured);

            (bool workBuddyOk, string workBuddyCfgPath) = await CustomWidgetAgentConnectorService.ConnectClientAsync(
                CustomWidgetAgentConnectorService.ClientWorkBuddy,
                fakeExe,
                fakeHome,
                fakeAppData);
            Assert.True(workBuddyOk);
            Assert.Equal(Path.Combine(fakeHome, ".workbuddy", "mcp.json"), workBuddyCfgPath);
            Assert.True(File.Exists(Path.Combine(fakeHome, ".workbuddy", "skills", "deskbox-widget", "SKILL.md")));
            IReadOnlyList<ExternalAgentClientStatus> afterWorkBuddy =
                CustomWidgetAgentConnectorService.DetectAgentClients(fakeHome, fakeAppData, fakeLocalAppData);
            Assert.True(afterWorkBuddy.Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientWorkBuddy).IsConfigured);

            // 3. Arbitrary File Injector handles custom JSON & TOML files and StudioPreferencesStore tracks them
            string customJsonFile = Path.Combine(tempRoot, "cherry-studio-mcp.json");
            await File.WriteAllTextAsync(customJsonFile, """{"theme":"dark"}""", Encoding.UTF8);
            CustomWidgetAgentConnectorService.InjectMcpIntoConfigFile(customJsonFile, fakeExe);
            Assert.True(File.Exists(customJsonFile + ".deskbox.bak"));
            Assert.True(CustomWidgetAgentConnectorService.HasMcpServerInAnyConfigFile(customJsonFile));

            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempRoot, "prefs.json"));
            prefsStore.SetAgentHttpPort(48123);
            prefsStore.SetAgentHttpEnabled(true);
            string generatedToken = prefsStore.GetOrCreateAgentHttpToken();
            Assert.StartsWith("dbx_", generatedToken, StringComparison.Ordinal);
            Assert.Equal(generatedToken, prefsStore.GetOrCreateAgentHttpToken());
            string rotatedToken = prefsStore.RegenerateAgentHttpToken();
            Assert.NotEqual(generatedToken, rotatedToken);
            prefsStore.AddCustomAgentConfigPath(customJsonFile);
            Assert.Single(prefsStore.CustomAgentConfigPaths);

            // 4. All 7 McpConfigFormat snippets generate valid output
            Assert.Contains("\"mcpServers\"", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.StandardJson, fakeExe), StringComparison.Ordinal);
            Assert.Contains("\"servers\"", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.VsCodeJson, fakeExe), StringComparison.Ordinal);
            Assert.Contains("[mcp_servers.deskbox]", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.CodexToml, fakeExe), StringComparison.Ordinal);
            Assert.Contains("@deepseek-ai/dsh-mcp-client", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.DeepSeekHarnessYaml, fakeExe), StringComparison.Ordinal);
            Assert.Contains("http://127.0.0.1:48123/mcp", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.StreamableHttpJson, fakeExe, 48123, rotatedToken), StringComparison.Ordinal);
            Assert.Contains("\"mcp\"", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.OpenCodeJson, fakeExe), StringComparison.Ordinal);
            Assert.Contains("claude mcp add", CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.CliRegistration, fakeExe), StringComparison.Ordinal);

            // 4b. DeepSeek Harness (dsh): ~/.dsh/cordis.patch.yml non-destructive YAML merge + idempotent re-merge + ~/.dsh/skills sync
            string dshDir = Path.Combine(fakeHome, ".dsh");
            Directory.CreateDirectory(dshDir);
            string dshPatchPath = Path.Combine(dshDir, "cordis.patch.yml");
            await File.WriteAllTextAsync(
                dshPatchPath,
                """
                # Existing Cordis patches
                - insert:
                    id: mcp-github
                    name: '@deepseek-ai/dsh-mcp-client'
                    config:
                      serverName: github
                      transport: stdio
                      command: npx
                """,
                Encoding.UTF8);

            ExternalAgentClientStatus dshBefore = CustomWidgetAgentConnectorService
                .DetectAgentClients(fakeHome, fakeAppData, fakeLocalAppData)
                .Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientDeepSeekHarness);
            Assert.True(dshBefore.IsInstalled);
            Assert.False(dshBefore.IsConfigured);

            (bool dshOk, string dshWrittenPath) = await CustomWidgetAgentConnectorService.ConnectClientAsync(
                CustomWidgetAgentConnectorService.ClientDeepSeekHarness,
                fakeExe,
                fakeHome,
                fakeAppData);
            Assert.True(dshOk);
            Assert.Equal(dshPatchPath, dshWrittenPath);
            Assert.True(File.Exists(dshPatchPath + ".deskbox.bak"));
            Assert.True(File.Exists(Path.Combine(dshDir, "skills", "deskbox-widget", "SKILL.md")));

            string mergedDshYaml = await File.ReadAllTextAsync(dshPatchPath, Encoding.UTF8);
            Assert.Contains("id: mcp-github", mergedDshYaml, StringComparison.Ordinal);
            Assert.Contains("id: mcp-deskbox", mergedDshYaml, StringComparison.Ordinal);
            Assert.Contains("serverName: deskbox", mergedDshYaml, StringComparison.Ordinal);
            Assert.Contains($"command: '{fakeExe}'", mergedDshYaml, StringComparison.Ordinal);

            CustomWidgetAgentConnectorService.InjectMcpIntoConfigFile(dshPatchPath, @"D:\DeskBox\DeskBox.exe");
            string remergedDshYaml = await File.ReadAllTextAsync(dshPatchPath, Encoding.UTF8);
            Assert.Equal(1, remergedDshYaml.Split("id: mcp-deskbox", StringSplitOptions.None).Length - 1);
            Assert.Contains(@"command: 'D:\DeskBox\DeskBox.exe'", remergedDshYaml, StringComparison.Ordinal);
            Assert.True(CustomWidgetAgentConnectorService.HasMcpServerInAnyConfigFile(dshPatchPath));

            ExternalAgentClientStatus dshAfter = CustomWidgetAgentConnectorService
                .DetectAgentClients(fakeHome, fakeAppData, fakeLocalAppData)
                .Single(c => c.ClientId == CustomWidgetAgentConnectorService.ClientDeepSeekHarness);
            Assert.True(dshAfter.IsConfigured);

            // 5. Global Skills sync & Zero-MCP root workspace docs + Playbook kickoff prompts
            Directory.CreateDirectory(Path.Combine(fakeHome, ".claude"));
            Directory.CreateDirectory(Path.Combine(fakeHome, ".cursor"));
            int syncedSkills = await CustomWidgetAgentConnectorService.SyncAllGlobalSkillsAsync(fakeHome);
            Assert.Equal(6, syncedSkills);

            string zeroMcpRoot = Path.Combine(tempRoot, "widgets-root");
            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(zeroMcpRoot);
            Assert.True(File.Exists(Path.Combine(zeroMcpRoot, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(zeroMcpRoot, "CLAUDE.md")));
            Assert.True(File.Exists(Path.Combine(zeroMcpRoot, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(zeroMcpRoot, "manifest.schema.json")));

            string createZh = CustomWidgetAgentConnectorService.BuildKickoffPrompt(AgentPlaybookKind.CreateNewWidget, "zh-CN", userIdea: "极简番茄钟");
            Assert.Contains("deskbox_get_widget_spec", createZh, StringComparison.Ordinal);
            Assert.Contains("极简番茄钟", createZh, StringComparison.Ordinal);

            string modifyEn = CustomWidgetAgentConnectorService.BuildKickoffPrompt(AgentPlaybookKind.ModifyExistingWidget, "en-US", "hydration-1", "Hydration");
            Assert.Contains("hydration-1", modifyEn, StringComparison.Ordinal);
            Assert.Contains("deskbox_upsert_widget", modifyEn, StringComparison.Ordinal);

            string hudZh = CustomWidgetAgentConnectorService.BuildKickoffPrompt(AgentPlaybookKind.PushLiveHudTelemetry, "zh-CN");
            Assert.Contains("agent-hud", hudZh, StringComparison.Ordinal);
            Assert.Contains("deskbox_push_widget_data", hudZh, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AgentAuditUpgrades_RankAndArithmetic_DynamicBindings_SsrfDnsRebindingAndProxyFallback_DynamicTabSwitch_And_McpUpgrades_WorkEndToEnd()
    {
        // 1. $rank (1-based) + safe numeric arithmetic (+, -, *, /, %) + hyphenated identifier preservation
        var stockRow0 = new CustomWidgetRecord
        {
            Id = "sh600519",
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "贵州茅台",
                ["pct"] = "3.25",
                ["price"] = "1520"
            }
        };
        var evalCtx = new CustomWidgetEvaluationContext
        {
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["my-key"] = "keep-hyphen",
                ["offset"] = "10"
            }
        };

        CustomWidgetEvaluationContext item0Ctx = evalCtx.WithItem(stockRow0, 0);
        CustomWidgetEvaluationContext item9Ctx = evalCtx.WithItem(stockRow0, 9);
        Assert.Equal("0", CustomWidgetExpressionEvaluator.Interpolate("${$index}", item0Ctx));
        Assert.Equal("1", CustomWidgetExpressionEvaluator.Interpolate("${$rank}", item0Ctx));
        Assert.Equal("10", CustomWidgetExpressionEvaluator.Interpolate("${$rank}", item9Ctx));
        Assert.Equal("14", CustomWidgetExpressionEvaluator.Interpolate("${2 + 3 * 4}", item0Ctx));
        Assert.Equal("20", CustomWidgetExpressionEvaluator.Interpolate("${(2 + 3) * 4}", item0Ctx));
        Assert.Equal("5", CustomWidgetExpressionEvaluator.Interpolate("${10 - 3 - 2}", item0Ctx));
        Assert.Equal("2", CustomWidgetExpressionEvaluator.Interpolate("${17 % 5}", item0Ctx));
        Assert.Equal("7", CustomWidgetExpressionEvaluator.Interpolate("${-5 + 12}", item0Ctx));
        Assert.Equal("0", CustomWidgetExpressionEvaluator.Interpolate("${10 / 0}", item0Ctx));
        Assert.Equal("1530", CustomWidgetExpressionEvaluator.Interpolate("${$item.price + state.offset}", item0Ctx));
        Assert.Equal("keep-hyphen", CustomWidgetExpressionEvaluator.Interpolate("${state.my-key}", item0Ctx));
        Assert.Equal("coral", CustomWidgetExpressionEvaluator.Interpolate("${$rank <= 3 ? 'coral' : 'muted'}", item0Ctx));
        Assert.Equal("muted", CustomWidgetExpressionEvaluator.Interpolate("${$rank <= 3 ? 'coral' : 'muted'}", item9Ctx));

        // 2. ValidateWithRuntimeHints: implicit httpJson state keys + expression inspection on color, tone, and sources[].url
        const string validHttpJsonManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.hot-stocks",
          "name": "股票热门榜",
          "state": { "fid": "f3" },
          "collections": [
            { "name": "stocks", "fields": [ { "name": "code" }, { "name": "name" }, { "name": "pct" } ] }
          ],
          "sources": [
            {
              "id": "eastmoney",
              "type": "httpJson",
              "url": "https://push2.eastmoney.com/api/qt/clist/get?fid=${state.fid}",
              "targetCollection": "stocks",
              "itemsPath": "data.diff",
              "fieldMap": { "code": "f12", "name": "f14", "pct": "f3" }
            }
          ],
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${state.sourceStatus == 'error' ? state.sourceError : state.sourceUpdatedAt}" },
              {
                "type": "List",
                "itemsSource": "stocks",
                "itemTemplate": {
                  "type": "Card",
                  "tone": "${$rank == 1 ? 'accent' : 'subtle'}",
                  "children": [
                    { "type": "Badge", "text": "${$rank}", "tone": "${$rank <= 3 ? 'danger' : 'muted'}" },
                    { "type": "Text", "text": "${$item.pct}%", "color": "${$item.pct >= 0 ? '#EF4444' : '#10B981'}" }
                  ]
                }
              }
            ]
          }
        }
        """;
        CustomWidgetParseResult validHttpParsed = CustomWidgetManifestParser.ValidateWithRuntimeHints(validHttpJsonManifest);
        Assert.True(validHttpParsed.IsValid);
        Assert.Empty(validHttpParsed.Diagnostics);

        const string warnBindingsManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.warn-check",
          "name": "Warn Check",
          "state": { "ok": "1" },
          "collections": [ { "name": "items", "fields": [ { "name": "title" } ] } ],
          "sources": [
            {
              "type": "httpJson",
              "url": "https://example.com/api?k=${state.missingUrlState}",
              "targetCollection": "items"
            }
          ],
          "view": {
            "type": "Card",
            "tone": "${state.missingCardTone}",
            "children": [
              { "type": "Text", "text": "Hi", "color": "${state.missingTextColor}" },
              { "type": "Badge", "text": "B", "tone": "${state.missingBadgeTone}" }
            ]
          }
        }
        """;
        CustomWidgetParseResult warnParsed = CustomWidgetManifestParser.ValidateWithRuntimeHints(warnBindingsManifest);
        string[] warnings = warnParsed.Diagnostics.Where(d => !d.IsError).Select(d => d.Message).ToArray();
        Assert.Contains(warnings, m => m.Contains("state.missingUrlState", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("state.missingCardTone", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("state.missingTextColor", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("state.missingBadgeTone", StringComparison.Ordinal));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-agent-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cacheDir = Path.Combine(tempRoot, "net-cache");
            CustomWidgetSourceSpec sourceSpec = validHttpParsed.Manifest!.Sources[0];

            // 3a. DNS rebinding guard blocks mixed public + loopback DNS answer even with custom handler
            var rebindEngine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                new StubAiHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"diff":[]}}""", Encoding.UTF8, "application/json")
                })),
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Loopback }));

            CustomWidgetNetworkFetchOutcome rebindOut = await rebindEngine.FetchAsync(
                sourceSpec,
                "https://rebind.example.com/api");
            Assert.False(rebindOut.Succeeded);
            Assert.Contains("non-public IP address", rebindOut.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // 3b. Redirect hop DNS rebinding guard blocks 302 -> private LAN host
            var redirectRebindEngine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                new StubAiHttpMessageHandler((req, _) =>
                {
                    if (req.RequestUri!.Host == "public.example.com")
                    {
                        var r = new HttpResponseMessage(HttpStatusCode.Found);
                        r.Headers.Location = new Uri("https://internal-hop.example.com/secret");
                        return Task.FromResult(r);
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }),
                dnsResolver: (host, _) => Task.FromResult(
                    host == "public.example.com"
                        ? new[] { IPAddress.Parse("93.184.216.34") }
                        : new[] { IPAddress.Parse("192.168.1.50") }));

            CustomWidgetNetworkFetchOutcome redirectRebindOut = await redirectRebindEngine.FetchAsync(
                sourceSpec,
                "https://public.example.com/start");
            Assert.False(redirectRebindOut.Succeeded);
            Assert.Contains("non-public IP address", redirectRebindOut.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // 3c. Local loopback HTTP CONNECT proxy is allowed (127.0.0.1 is NOT blocked as SSRF when acting as system proxy)
            // and automatically falls back to direct connection when the local proxy refuses/fails the tunnel
            string? capturedConnectLine = null;
            using var proxyListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            proxyListener.Start();
            int proxyPort = ((IPEndPoint)proxyListener.LocalEndpoint).Port;
            Task proxyAcceptTask = Task.Run(async () =>
            {
                using System.Net.Sockets.TcpClient client = await proxyListener.AcceptTcpClientAsync();
                await using System.Net.Sockets.NetworkStream ns = client.GetStream();
                using var reader = new StreamReader(ns, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                capturedConnectLine = await reader.ReadLineAsync();
                byte[] badGateway = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                await ns.WriteAsync(badGateway);
            });

            var loopbackProxyEngine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                httpMessageHandler: null,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
                systemProxy: new WebProxy($"http://127.0.0.1:{proxyPort}"));

            CustomWidgetNetworkFetchOutcome proxyFallbackOut = await loopbackProxyEngine.FetchAsync(
                sourceSpec,
                "https://feeds.example.invalid/api");
            await proxyAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(capturedConnectLine);
            Assert.StartsWith("CONNECT feeds.example.invalid:443", capturedConnectLine!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", proxyFallbackOut.ErrorMessage ?? string.Empty, StringComparison.Ordinal);

            // 4. Dynamic sources[].url tab switch in CustomWidgetViewModel hydrates disk cache & fetches new tab immediately
            int liveFetchCalls = 0;
            var tabEngine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                new StubAiHttpMessageHandler((req, _) =>
                {
                    Interlocked.Increment(ref liveFetchCalls);
                    string query = req.RequestUri!.Query;
                    string payload = query.Contains("fid=f6", StringComparison.Ordinal)
                        ? """{"data":{"diff":[{"f12":"601318","f14":"中国平安","f3":"2.18"}]}}"""
                        : """{"data":{"diff":[{"f12":"600519","f14":"贵州茅台","f3":"4.56"}]}}""";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    });
                }));

            var settingsService = new SettingsService(Path.Combine(tempRoot, "settings.json"));
            var localization = new LocalizationService(settingsService);
            string widgetDataDir = Path.Combine(tempRoot, "widgets");
            var stockStore = new CustomWidgetDataStore("hot-stocks-1", widgetDataDir);
            await stockStore.WriteManifestJsonAsync(validHttpJsonManifest);

            var stockCfg = new WidgetConfig
            {
                Id = "hot-stocks-1",
                Name = "股票热门榜",
                WidgetKind = WidgetKind.Custom
            };

            using var stockVm = new CustomWidgetViewModel(
                stockCfg,
                localization,
                store: stockStore,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false,
                networkSourceEngine: tabEngine);

            await stockVm.InitializeAsync();
            for (int i = 0; i < 40 && (!stockVm.Collections.TryGetValue("stocks", out var initialRows) || initialRows.Count == 0); i++)
            {
                await Task.Delay(25);
            }

            Assert.Equal("ok", stockVm.State["sourceStatus"]);
            Assert.Equal("贵州茅台", stockVm.Collections["stocks"][0].Fields["name"]);

            // Switch tab fid=f3 -> fid=f6 via state.set action (no source.refresh needed, and not blocked by f3 cooldown!)
            await stockVm.ExecuteActionsAsync(
                [new CustomWidgetActionSpec(Type: "state.set", Key: "fid", Value: "f6")],
                stockVm.CreateEvaluationContext());

            for (int i = 0; i < 40 && stockVm.Collections["stocks"][0].Fields["name"] != "中国平安"; i++)
            {
                await Task.Delay(25);
            }

            Assert.Equal("中国平安", stockVm.Collections["stocks"][0].Fields["name"]);
            Assert.True(liveFetchCalls >= 2);

            // 5. MCP deskbox_list_widgets(widgetId) single-widget inspection & deskbox_capture_widget_preview(mode)
            string pipeName = $"DeskBox_Agent_Audit_{Guid.NewGuid():N}";
            await using var bridge = new DeskBoxAgentBridgeServer(pipeName, static () => null);
            bridge.Start();

            string mcpCalls = string.Join(
                "\n",
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"deskbox_list_widgets","arguments":{"widgetId":"missing-widget"}}}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_capture_widget_preview","arguments":{"widgetId":"missing-widget","mode":"capsule"}}}""") + "\n";

            using var mcpIn = new MemoryStream(Encoding.UTF8.GetBytes(mcpCalls));
            using var mcpOut = new MemoryStream();
            int mcpExit = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
                inputOverride: mcpIn,
                outputOverride: mcpOut,
                pipeNameOverride: pipeName);
            Assert.Equal(0, mcpExit);

            string[] mcpLines = Encoding.UTF8.GetString(mcpOut.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(3, mcpLines.Length);
            using JsonDocument getSingleDoc = JsonDocument.Parse(mcpLines[1]);
            Assert.True(getSingleDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            string getSingleText = getSingleDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("DeskBox WidgetManager is still initializing", getSingleText, StringComparison.Ordinal);

            using JsonDocument previewModeDoc = JsonDocument.Parse(mcpLines[2]);
            Assert.True(previewModeDoc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SecurityHardening_DepthCaps_TrustedHostBoundaries_And_ImportedOriginDemotion()
    {
        // 1. Expression evaluator bounds deeply nested parentheses and oversized expressions without StackOverflow.
        var evalContext = new CustomWidgetEvaluationContext();
        string deepParens = "${" + new string('(', 200) + "1 + 2" + new string(')', 200) + "}";
        string evalResult = CustomWidgetExpressionEvaluator.Interpolate(deepParens, evalContext);
        Assert.NotNull(evalResult);

        string hugeTemplate = "${" + string.Concat(Enumerable.Repeat("1 + ", 2500)) + "1}";
        string hugeResult = CustomWidgetExpressionEvaluator.Interpolate(hugeTemplate, evalContext);
        Assert.NotNull(hugeResult);

        // 2. Manifest parser rejects excessive node tree depth (> 64) and action chain depth (> 64) safely.
        var nestedViewSb = new StringBuilder();
        nestedViewSb.Append("""{"manifestVersion":1,"id":"custom.deep","name":"Deep","root": """);
        for (int i = 0; i < 80; i++)
        {
            nestedViewSb.Append("""{"type":"Column","children":[""");
        }
        nestedViewSb.Append("""{"type":"Text","text":"leaf"}""");
        for (int i = 0; i < 80; i++)
        {
            nestedViewSb.Append("]}");
        }
        nestedViewSb.Append('}');

        CustomWidgetParseResult deepViewResult = CustomWidgetManifestParser.Parse(nestedViewSb.ToString());
        Assert.Contains(deepViewResult.Diagnostics, d => d.IsError);

        var nestedActionSb = new StringBuilder();
        nestedActionSb.Append("""{"manifestVersion":1,"id":"custom.deep-act","name":"DeepAct","root":{"type":"Button","label":"Go","onTap": """);
        for (int i = 0; i < 80; i++)
        {
            nestedActionSb.Append("""{"type":"state.set","key":"k","value":"v","then":[""");
        }
        nestedActionSb.Append("""{"type":"state.set","key":"k","value":"end"}""");
        for (int i = 0; i < 80; i++)
        {
            nestedActionSb.Append("]}");
        }
        nestedActionSb.Append("}}");

        CustomWidgetParseResult deepActionResult = CustomWidgetManifestParser.Parse(nestedActionSb.ToString());
        Assert.Contains(deepActionResult.Diagnostics, d => d.IsError);

        // 3. IsTrustedOfficialRemoteHost restricts GitHub hosts strictly to /Tianyu199509/DeskBox/ on default HTTPS port.
        Assert.True(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://deskbox.fun/catalog/widgets.v1.json")));
        Assert.True(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://cdn.deskbox.fun/widgets/sys-pulse.json")));
        Assert.True(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/widgets.v1.json")));

        Assert.False(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://deskbox.fun:8443/catalog/widgets.v1.json")));
        Assert.False(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://raw.githubusercontent.com/attacker/repo/main/evil.json")));
        Assert.False(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://github.com/attacker/repo/releases/download/v1/evil.json")));
        Assert.False(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://objects.githubusercontent.com/github-production-release-asset/evil.json")));
        Assert.False(CustomWidgetNetworkSecurity.IsTrustedOfficialRemoteHost(
            new Uri("https://raw.githubusercontent.com/Tianyu199509/DeskBox/%2e%2e/attacker/evil.json")));

        // 4. External imports claiming "origin": "official" and "id": "deskbox.*" are forcibly demoted to Community.
        string spoofedOfficialJson = """
            {
              "manifestVersion": 1,
              "id": "deskbox.sys-pulse",
              "name": "Spoofed Official",
              "origin": "official",
              "root": { "type": "Text", "text": "hi" }
            }
            """;
        (CustomWidgetManifest importedManifest, _) = CustomWidgetPackageService.ParseAndPrepareImportedManifest(
            spoofedOfficialJson,
            markExternalAsCommunity: true);
        Assert.Equal(CustomWidgetOriginKinds.Community, importedManifest.Origin);
    }

    [Fact]
    public async Task RedirectValidation_AtRestBearerToken_And_DefaultManifestSize_EnforceFinalReleaseGates()
    {
        // 1. Default size aligns to 360x460 when omitted in manifest JSON.
        CustomWidgetParseResult minManifest = CustomWidgetManifestParser.Parse(
            """{"schemaVersion":1,"id":"custom.size-check","name":"Size Check","view":{"type":"Text","text":"ok"}}""");
        Assert.True(minManifest.IsValid);
        Assert.Equal(new CustomWidgetSizeSpec(360, 460), minManifest.Manifest!.DefaultSize);

        // 2. Trusted redirect (deskbox.fun -> www.deskbox.fun) succeeds, while redirect to untrusted domain,
        //    untrusted GitHub repo, HTTP downgrade, or loopback IP is strictly rejected.
        using var trustedRedirectClient = new HttpClient(new StubAiHttpMessageHandler((req, _) =>
        {
            if (string.Equals(req.RequestUri?.Host, "deskbox.fun", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://www.deskbox.fun/widgets/ok.json");
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"schemaVersion":1,"id":"custom.ok","name":"OK","view":{"type":"Text","text":"ok"}}""", Encoding.UTF8, "application/json")
            });
        }));

        using (HttpResponseMessage okResp = await CustomWidgetNetworkSecurity.SendGetWithValidatedRedirectsAsync(
                   trustedRedirectClient,
                   new Uri("https://deskbox.fun/widgets/start.json"),
                   requireTrustedOfficialHost: true))
        {
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }

        string[] blockedRedirectTargets =
        [
            "https://evil.example/manifest.json",
            "https://raw.githubusercontent.com/attacker/repo/main/evil.json",
            "http://deskbox.fun/widgets/downgrade.json",
            "https://127.0.0.1/internal.json"
        ];

        foreach (string blockedTarget in blockedRedirectTargets)
        {
            using var evilRedirectHandler = new StubAiHttpMessageHandler((_, _) =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(blockedTarget);
                return Task.FromResult(redirect);
            });

            DeskBoxProtocolIntent intent = new(
                DeskBoxProtocolAction.InstallWidget,
                RemoteManifestUri: new Uri("https://deskbox.fun/widgets/redirect.json"));
            (string? manifestJson, _, string? errorMessage) =
                await DeskBoxProtocolActivationService.ResolveInstallWidgetManifestAsync(intent, evilRedirectHandler);
            Assert.Null(manifestJson);
            Assert.False(string.IsNullOrWhiteSpace(errorMessage));
        }

        // 3. Studio preferences never persist plaintext bearer token on disk and migrate legacy fallback field.
        string prefDir = Path.Combine(Path.GetTempPath(), $"deskbox-pref-atrest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(prefDir);
        try
        {
            string prefFile = Path.Combine(prefDir, "studio-prefs.json");
            var store = new CustomWidgetStudioPreferencesStore(prefFile);
            string generatedToken = store.GetOrCreateAgentHttpToken();
            Assert.StartsWith("dbx_", generatedToken, StringComparison.Ordinal);

            string persistedRaw = await File.ReadAllTextAsync(prefFile, Encoding.UTF8);
            Assert.Contains("agentHttpTokenProtected", persistedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain(generatedToken, persistedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain("agentHttpTokenFallback", persistedRaw, StringComparison.Ordinal);

            string legacyFile = Path.Combine(prefDir, "legacy-prefs.json");
            const string legacyToken = "dbx_legacy_plaintext_token_9876543210";
            await File.WriteAllTextAsync(
                legacyFile,
                $$"""{"agentHttpEnabled":true,"agentHttpPort":47821,"agentHttpTokenFallback":"{{legacyToken}}"}""",
                Encoding.UTF8);

            var migratedStore = new CustomWidgetStudioPreferencesStore(legacyFile);
            Assert.Equal(legacyToken, migratedStore.GetOrCreateAgentHttpToken());
            string migratedRaw = await File.ReadAllTextAsync(legacyFile, Encoding.UTF8);
            Assert.Contains("agentHttpTokenProtected", migratedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain(legacyToken, migratedRaw, StringComparison.Ordinal);
            Assert.DoesNotContain("agentHttpTokenFallback", migratedRaw, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(prefDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeskBoxAiService_RoutesFastMicroTaskWithoutThinking_SwapsReasonerModel_And_FallsBackCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-substrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            prefsStore.SetPreferredAiProviderId("deepseek");

            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-deepseek-substrate",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-reasoner",
                ThinkingMode: "enabled"));

            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "qwen",
                ApiKey: "sk-test-qwen-substrate",
                CustomEndpoint: string.Empty,
                CustomModel: "qwq-32b",
                ThinkingMode: "enabled"));

            var seenModels = new List<string>();
            int qwenAttempts = 0;

            using var httpClient = new HttpClient(new StubAiHttpMessageHandler(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                string model = root.GetProperty("model").GetString() ?? string.Empty;
                seenModels.Add(model);

                // Restricted relay only permits qwq-32b and rejects enable_thinking first, then 404s qwen-plus
                if (req.RequestUri!.Host.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase))
                {
                    qwenAttempts++;
                    if (root.TryGetProperty("enable_thinking", out _))
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest)
                        {
                            Content = new StringContent(
                                """{"error":{"message":"enable_thinking unsupported"}}""",
                                Encoding.UTF8,
                                "application/json")
                        };
                    }

                    if (!string.Equals(model, "qwq-32b", StringComparison.OrdinalIgnoreCase))
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(
                                """{"error":{"message":"model qwen-plus not authorized on this key"}}""",
                                Encoding.UTF8,
                                "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"choices":[{"message":{"content":"<think>Ignore {\"fake\":1}</think>\n```json\n{\"summary\":\"Relay fallback OK\"}\n```"}}]}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"message":{"content":"{\"summary\":\"Fast chat OK\",\"items\":[{\"title\":\"Ship 1.6.0\"}]}"}}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);

            // Role 1: StudioAuthoring preserves deepseek-reasoner + Enabled thinking
            ResolvedAiInvocation authoring = aiService.ResolveEffectiveSettingsForProfile(
                "deepseek",
                AiTaskProfile.StudioAuthoring,
                out string? authoringFallback);
            Assert.Equal("deepseek-reasoner", authoring.Model);
            Assert.Equal(AiThinkingMode.Enabled, authoring.ThinkingMode);
            Assert.Null(authoringFallback);

            // Role 2: FastMicroTask downgrades deepseek-reasoner -> deepseek-chat and disables thinking
            ResolvedAiInvocation micro = aiService.ResolveEffectiveSettingsForProfile(
                "deepseek",
                AiTaskProfile.FastMicroTask,
                out string? microFallback);
            Assert.Equal("deepseek-chat", micro.Model);
            Assert.Equal(AiThinkingMode.Disabled, micro.ThinkingMode);
            Assert.Equal("deepseek-reasoner", microFallback);

            AiStructuredCompletionResult fastRes = await aiService.CompleteJsonAsync(
                userPrompt: "Extract tasks",
                profile: AiTaskProfile.FastMicroTask);
            Assert.True(fastRes.Success);
            Assert.Equal("deepseek-chat", fastRes.EffectiveModel);
            Assert.Contains("Ship 1.6.0", fastRes.Json, StringComparison.Ordinal);

            // Restricted relay fallback: strips enable_thinking on 400, then falls back from qwen-plus to qwq-32b on 404, and strips <think>
            AiStructuredCompletionResult relayRes = await aiService.CompleteJsonAsync(
                userPrompt: "Summarize note",
                providerIdOverride: "qwen",
                profile: AiTaskProfile.FastMicroTask);
            Assert.True(relayRes.Success);
            Assert.Equal("qwq-32b", relayRes.EffectiveModel);
            Assert.Equal("""{"summary":"Relay fallback OK"}""", relayRes.Json);
            Assert.Equal(3, qwenAttempts);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ManifestParser_ValidatesAiRunAction_RecognizesImplicitStateWriters_And_FlagsMissingPrompt()
    {
        const string validAiRunManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.ai-micro-test",
          "name": "AI Micro Test",
          "state": { "draft": "" },
          "collections": {
            "tasks": { "maxItems": 50, "sort": "newestFirst" }
          },
          "view": {
            "type": "Stack",
            "children": [
              {
                "type": "Input",
                "bind": "state.draft",
                "onSubmit": {
                  "type": "ai.run",
                  "prompt": "Refine: ${state.draft}",
                  "stateMap": { "summary": "summary" },
                  "collection": "tasks",
                  "mode": "prepend",
                  "itemsPath": "items",
                  "fieldMap": { "title": "title" },
                  "maxItems": 5
                }
              },
              {
                "type": "Text",
                "visible": "${state.aiStatus == 'running'}",
                "text": "${state.summary} · ${state.aiUpdatedAt} · ${state.aiError}"
              }
            ]
          }
        }
        """;

        CustomWidgetParseResult okResult = CustomWidgetManifestParser.ValidateWithRuntimeHints(validAiRunManifest);
        Assert.NotNull(okResult.Manifest);
        Assert.Empty(okResult.Diagnostics);

        const string invalidAiRunManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.ai-micro-bad",
          "name": "AI Micro Bad",
          "view": {
            "type": "Button",
            "text": "Run",
            "onClick": {
              "type": "ai.run"
            }
          }
        }
        """;

        CustomWidgetParseResult badResult = CustomWidgetManifestParser.ValidateWithRuntimeHints(invalidAiRunManifest);
        Assert.Contains(badResult.Diagnostics, d => d.IsError && d.Message.Contains("prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CustomWidgetViewModel_ExecutesAiRunAndSafeDropText_PreservingDraftOnFailure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cw-airun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Verify safe ${drop.text} extraction limits & binary NUL guard
            string largeMdPath = Path.Combine(tempDir, "large-notes.md");
            await File.WriteAllTextAsync(largeMdPath, new string('A', 12_000), Encoding.UTF8);
            string truncatedText = await CustomWidgetViewModel.TryReadSafeDropTextAsync(largeMdPath, "md");
            Assert.Equal(8192, truncatedText.Length);

            string binaryFakeTxtPath = Path.Combine(tempDir, "binary.txt");
            await File.WriteAllBytesAsync(binaryFakeTxtPath, [0x48, 0x65, 0x00, 0x6C, 0x6F]);
            Assert.Equal(string.Empty, await CustomWidgetViewModel.TryReadSafeDropTextAsync(binaryFakeTxtPath, "txt"));

            string exePath = Path.Combine(tempDir, "tool.exe");
            await File.WriteAllTextAsync(exePath, "MZ fake header", Encoding.UTF8);
            Assert.Equal(string.Empty, await CustomWidgetViewModel.TryReadSafeDropTextAsync(exePath, "exe"));

            // 2. End-to-end ViewModel ai.run + ${drop.text}
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("ai-inbox-test", tempDir);
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            string lastUserPrompt = string.Empty;
            using var httpClient = new HttpClient(new StubAiHttpMessageHandler(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement messages = doc.RootElement.GetProperty("messages");
                lastUserPrompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? string.Empty;

                const string structuredReply = """
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"summary\":\"完成 1.6.0 AI 底座验收\",\"items\":[{\"title\":\"运行 x64 单元测试\",\"tag\":\"测试\",\"done\":\"false\"},{\"title\":\"验证桌面热重载\",\"tag\":\"验收\",\"done\":\"false\"}]}"
                      }
                    }
                  ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(structuredReply, Encoding.UTF8, "application/json")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            var cfg = new WidgetConfig
            {
                Id = "ai-inbox-test",
                Name = "AI Inbox",
                WidgetKind = WidgetKind.Custom
            };

            var feedbacks = new List<WidgetFeedbackRequest>();
            using var vm = new CustomWidgetViewModel(
                cfg,
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false,
                aiService: aiService,
                preferredAiProviderResolver: () => prefsStore.PreferredAiProviderId);
            vm.FeedbackRequested += (_, e) => feedbacks.Add(e.Request);

            await store.WriteManifestJsonAsync(CustomWidgetTemplates.AiInboxStarterManifestJson);
            await vm.InitializeAsync();

            // Step A: Missing API key -> friendly warning, state.aiStatus = "error", AND draft is preserved!
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("state.set", Key: "draft", Value: "周四前完成测试与发版准备")]);
            CustomWidgetNodeSpec refineButton = vm.Manifest.View.Children[0].Children[1];
            await vm.ExecuteActionsAsync(refineButton.OnClick!);

            Assert.Equal("error", vm.State["aiStatus"]);
            Assert.Equal("周四前完成测试与发版准备", vm.State["draft"]);
            Assert.NotEmpty(feedbacks);

            // Step B: Configure API key -> ai.run populates summary + items and clears draft via then
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-live-test-key",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "auto"));
            prefsStore.SetPreferredAiProviderId("deepseek");

            await vm.ExecuteActionsAsync(refineButton.OnClick!);

            Assert.Equal("idle", vm.State["aiStatus"]);
            Assert.Equal(string.Empty, vm.State["aiError"]);
            Assert.Equal(string.Empty, vm.State["draft"]);
            Assert.Equal("完成 1.6.0 AI 底座验收", vm.State["summary"]);
            Assert.Equal(2, vm.Collections["items"].Count);
            Assert.Equal("运行 x64 单元测试", vm.Collections["items"][0].Fields["title"]);
            Assert.Equal("测试", vm.Collections["items"][0].Fields["tag"]);

            // Step C: Drop a .md file -> ${drop.text} flows into ai.run and prepends 2 more items
            string meetingFile = Path.Combine(tempDir, "meeting.md");
            await File.WriteAllTextAsync(meetingFile, "# Sprint Sync\n- Verify PlanetColor icon\n- Check 13 locales", Encoding.UTF8);

            bool imported = await vm.ImportDroppedFilesAsync([meetingFile]);
            Assert.True(imported);
            Assert.Contains("Sprint Sync", lastUserPrompt, StringComparison.Ordinal);
            Assert.Equal(4, vm.Collections["items"].Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NetworkBoardWidget_SupportsDynamicListItems_SourceAliases_ItemRankFallback_MultiSourceIsolation_And_HealthDiagnostics()
    {
        const string boardManifestJson = """
        {
          "manifestVersion": 1,
          "id": "custom.hot-board-test",
          "name": "Hot Board",
          "state": {
            "board": "bili"
          },
          "collections": [
            { "name": "bili", "fields": [ { "name": "title" }, { "name": "url" } ] },
            { "name": "weibo", "fields": [ { "name": "title" }, { "name": "url" } ] }
          ],
          "sources": [
            {
              "id": "bili",
              "type": "httpJson",
              "url": "https://api.example.com/bili",
              "refreshInterval": 900,
              "targetCollection": "bili",
              "itemsPath": "$.data.list",
              "mapping": {
                "title": "$.title",
                "url": "$.short_link_v2"
              }
            },
            {
              "id": "weibo",
              "type": "httpJson",
              "url": "https://api.example.com/weibo",
              "refreshInterval": 900,
              "targetCollection": "weibo",
              "itemsPath": "$.data.realtime",
              "mapping": {
                "title": "$.word",
                "url": "$.url"
              }
            }
          ],
          "root": {
            "type": "Stack",
            "children": [
              {
                "type": "Button",
                "text": "Refresh Active",
                "onClick": { "type": "source.refresh", "source": "${state.board}" }
              },
              {
                "type": "List",
                "items": "${state.board}",
                "itemTemplate": {
                  "type": "Stack",
                  "orientation": "horizontal",
                  "children": [
                    { "type": "Badge", "text": "${$item.rank}" },
                    { "type": "Text", "text": "${$item.title}" }
                  ]
                }
              }
            ]
          }
        }
        """;

        CustomWidgetParseResult parseRes = CustomWidgetManifestParser.ValidateWithRuntimeHints(boardManifestJson);
        Assert.NotNull(parseRes.Manifest);
        Assert.Empty(parseRes.Diagnostics);

        CustomWidgetSourceSpec biliSpec = parseRes.Manifest!.Sources.Single(s => s.Id == "bili");
        Assert.Equal(900_000, biliSpec.IntervalMs);
        Assert.NotNull(biliSpec.FieldMap);
        Assert.Equal("$.title", biliSpec.FieldMap!["title"]);

        CustomWidgetNodeSpec refreshBtn = parseRes.Manifest.View.Children[0];
        Assert.Equal("${state.board}", refreshBtn.OnClick[0].TargetId);

        var rec = new CustomWidgetRecord
        {
            Id = "r1",
            Fields = new Dictionary<string, string> { ["title"] = "DeskBox 1.6 发布" }
        };
        var baseCtx = new CustomWidgetEvaluationContext
        {
            State = new Dictionary<string, string> { ["board"] = "bili" },
            Collections = new Dictionary<string, List<CustomWidgetRecord>> { ["bili"] = [rec] }
        }.WithItem(rec, 2);

        Assert.Equal("3", CustomWidgetExpressionEvaluator.Interpolate("${$item.rank}", baseCtx));
        Assert.Equal("2", CustomWidgetExpressionEvaluator.Interpolate("${$item.index}", baseCtx));
        Assert.Equal("bili", CustomWidgetExpressionEvaluator.Interpolate("${state.board}", baseCtx));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-hot-board-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("hot-board-test", tempDir);
            await store.WriteManifestJsonAsync(boardManifestJson);

            var netEngine = new CustomWidgetNetworkSourceEngine(
                Path.Combine(tempDir, "net-cache"),
                new StubAiHttpMessageHandler((req, _) =>
                {
                    if (req.RequestUri!.AbsolutePath.EndsWith("/bili", StringComparison.Ordinal))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                """{"data":{"list":[{"title":"B站热榜第一","short_link_v2":"https://b23.tv/1"}]}}""",
                                Encoding.UTF8,
                                "application/json")
                        });
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("Forbidden", Encoding.UTF8, "text/plain")
                    });
                }));

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "hot-board-test", Name = "Hot Board", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false,
                networkSourceEngine: netEngine);

            await vm.InitializeAsync();
            await vm.RefreshNetworkSourcesNowAsync();

            Assert.Single(vm.Collections["bili"]);
            Assert.Equal("B站热榜第一", vm.Collections["bili"][0].Fields["title"]);
            Assert.Equal("ok", vm.State["sourceStatus"]);
            Assert.Equal("ok", vm.State["sourceStatus.bili"]);
            Assert.Equal("error", vm.State["sourceStatus.weibo"]);
            Assert.Contains("403", vm.State["sourceError.weibo"], StringComparison.Ordinal);

            var payload = await store.LoadAsync();
            CustomWidgetHealthReport health = CustomWidgetHealthAnalyzer.AnalyzeHealth(
                boardManifestJson,
                parseRes.Manifest,
                payload,
                measuredWidth: 320,
                measuredHeight: 260,
                isChinese: true);
            Assert.Contains("sourceError.weibo", health.PrivacySafeDataShapeSummary, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomWidgetStorage_UsesDedicatedCustomWidgetsSubdirectory_DisjointFromTodoWidgets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-cw-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("custom-widgets", CustomWidgetDataStore.SubdirectoryName);

            var customStore = new CustomWidgetDataStore("agent-board", tempRoot);
            var todoStore = new TodoWidgetStore(Path.Combine(tempRoot, "widgets"), "todo-main");
            var sessionStore = new CustomWidgetStudioSessionStore(tempRoot);
            var defaultPrefsStore = new CustomWidgetStudioPreferencesStore();

            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempRoot, "custom-widgets", "agent-board")),
                Path.GetFullPath(customStore.WidgetDirectory));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempRoot, "custom-widgets")),
                Path.GetFullPath(sessionStore.WidgetsRootDirectory));
            Assert.StartsWith(
                Path.GetFullPath(Path.Combine(tempRoot, "widgets")) + Path.DirectorySeparatorChar,
                Path.GetFullPath(todoStore.StorePath),
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                Path.Combine("data", "custom-widgets", "studio-prefs.json"),
                Path.GetFullPath(defaultPrefsStore.FilePath),
                StringComparison.OrdinalIgnoreCase);

            // Cloud backup domain classification keeps built-in Todo (widgets/) and CustomWidgets (custom-widgets/) disjoint
            Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.TodoData, "widgets/todo-1/todo.json"));
            Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "widgets/todo-1/todo.json"));
            Assert.Null(CloudBackupDomains.TryGetCustomWidgetId("widgets/todo-1/todo.json"));
            Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/cw-1/manifest.json"));
            Assert.False(CloudBackupDomains.IsInDomain(CloudBackupDomain.TodoData, "custom-widgets/cw-1/manifest.json"));
            Assert.Equal("cw-1", CloudBackupDomains.TryGetCustomWidgetId("custom-widgets/cw-1/manifest.json"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CustomWidgetConsent_PromptsForExternalExecutablesAcrossAllOrigins_AndPersistsTrustToken()
    {
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Tools\deploy.ps1"));
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Windows\System32\cmd.exe"));
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"D:\Scripts\build.bat"));
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Users\Alice\Desktop\App.lnk"));
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Temp\sneaky.exe."));
        Assert.True(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Temp\readme.txt:payload.exe"));
        Assert.False(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Users\Alice\Documents\notes.txt"));
        Assert.False(CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\Users\Alice\Pictures\photo.png"));

        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-cw-exec-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string externalScriptPath = Path.Combine(tempDir, "outside-runner.cmd");
            await File.WriteAllTextAsync(externalScriptPath, "@echo off\r\n");

            const string aiOriginManifestJson = """
            {
              "schemaVersion": 1,
              "id": "ai-exec-guard",
              "name": "AI Exec Guard",
              "origin": "ai",
              "root": { "type": "text", "value": "Guard" }
            }
            """;

            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("ai-exec-guard", tempDir);
            await store.WriteManifestJsonAsync(aiOriginManifestJson);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "ai-exec-guard", Name = "AI Exec Guard", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false);
            await vm.InitializeAsync();

            var prompts = new List<CustomWidgetSecurityPrompt>();
            bool allowNextPrompt = false;
            vm.ExternalActionConsentPrompt = prompt =>
            {
                prompts.Add(prompt);
                return Task.FromResult(allowNextPrompt);
            };

            // 1. Denied prompt blocks external script execution even when manifest origin is "ai" (never reaches Process.Start)
            await vm.OpenFileWithDefaultAppAsync(externalScriptPath);
            Assert.Single(prompts);
            Assert.Equal("file.open", prompts[0].ActionKind);
            Assert.Equal(Path.GetFullPath(externalScriptPath), prompts[0].TargetDisplay);

            // 2. Non-executable external document on "ai" origin does NOT trigger community-only prompt
            prompts.Clear();
            bool docApproved = await vm.EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "file.open",
                trustToken: "path:c:\\docs\\readme.txt",
                targetDisplay: @"C:\docs\readme.txt",
                requireConsentForAllOrigins: CustomWidgetUtilityEngine.IsExecutableOrScriptPath(@"C:\docs\readme.txt"));
            Assert.True(docApproved);
            Assert.Empty(prompts);

            // 3. Approving executable target persists trust token so repeat check does not prompt again
            allowNextPrompt = true;
            string execToken = $"path:{Path.GetFullPath(externalScriptPath).ToLowerInvariant()}";
            bool firstExecApproved = await vm.EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "launcher.open",
                trustToken: execToken,
                targetDisplay: Path.GetFullPath(externalScriptPath),
                requireConsentForAllOrigins: true);
            Assert.True(firstExecApproved);
            Assert.Single(prompts);

            allowNextPrompt = false;
            bool secondExecApproved = await vm.EnsureCommunityExternalTargetApprovedAsync(
                actionKind: "launcher.open",
                trustToken: execToken,
                targetDisplay: Path.GetFullPath(externalScriptPath),
                requireConsentForAllOrigins: true);
            Assert.True(secondExecApproved);
            Assert.Single(prompts);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class StubAiHttpMessageHandler(
        Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> responder)
        : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
