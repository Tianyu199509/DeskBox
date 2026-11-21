using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;

namespace DeskBox.Tests;

public sealed class CustomWidgetStudioAiAndMcpTests
{
    [Fact]
    public void CapabilityRegistry_LoadsAll27CapabilitiesFromEmbeddedJson_AndSkillCatalogOnDropUsesStorage()
    {
        IReadOnlyList<CustomWidgetCapabilitySpec> all = CustomWidgetCapabilityRegistry.All;
        Assert.Equal(37, all.Count);
        Assert.All(all, cap =>
        {
            Assert.False(string.IsNullOrWhiteSpace(cap.Id));
            Assert.False(string.IsNullOrWhiteSpace(cap.TitleZh));
            Assert.False(string.IsNullOrWhiteSpace(cap.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(cap.CompactContractMarkdown));
            Assert.False(string.IsNullOrWhiteSpace(cap.GoldenSnippetJson));
            using JsonDocument snippetDoc = JsonDocument.Parse(cap.GoldenSnippetJson);
            Assert.Equal(JsonValueKind.Object, snippetDoc.RootElement.ValueKind);
        });

        string compactEn = CustomWidgetCapabilityRegistry.BuildCompactCoreComponentIndex(isChinese: false);
        string compactZh = CustomWidgetCapabilityRegistry.BuildCompactCoreComponentIndex(isChinese: true);
        Assert.Contains("RingGauge", compactEn, StringComparison.Ordinal);
        Assert.Contains("schemaVersion", compactEn, StringComparison.Ordinal);
        Assert.Contains("RingGauge", compactZh, StringComparison.Ordinal);

        CustomWidgetCapabilitySpec? dropZoneSpec = CustomWidgetCapabilityRegistry.GetById("drop-zone");
        Assert.NotNull(dropZoneSpec);
        Assert.Contains("DropZone", dropZoneSpec!.CompactContractMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("copyToWidget", dropZoneSpec.CompactContractMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("copyToWidget", dropZoneSpec.GoldenSnippetJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderPresets_FastSiblingRouting_GeminiThinkingDisable_AndTelemetryAccounting_WorkCleanly()
    {
        CustomWidgetAiProviderPreset openai = CustomWidgetAiGeneratorService.FindProviderPreset("openai");
        CustomWidgetAiProviderPreset anthropic = CustomWidgetAiGeneratorService.FindProviderPreset("anthropic");
        CustomWidgetAiProviderPreset gemini = CustomWidgetAiGeneratorService.FindProviderPreset("gemini");

        Assert.Contains("gpt-5-mini", openai.Models);
        Assert.Contains("claude-sonnet-4-5", anthropic.Models);
        Assert.Contains("gemini-2.5-flash-lite", gemini.Models);

        Assert.Equal("gpt-5-mini", AiProviderCatalog.TryResolveFastSiblingModel(openai, "gpt-5", usesCustomEndpoint: false));
        Assert.Equal("gemini-2.5-flash", AiProviderCatalog.TryResolveFastSiblingModel(gemini, "gemini-2.5-pro", usesCustomEndpoint: false));
        Assert.Equal("claude-haiku-4-5", AiProviderCatalog.TryResolveFastSiblingModel(anthropic, "claude-opus-4-1", usesCustomEndpoint: false));
        Assert.Null(AiProviderCatalog.TryResolveFastSiblingModel(openai, "gpt-5", usesCustomEndpoint: true));

        var aiService = new DeskBoxAiService();
        ResolvedAiInvocation geminiDisabled = aiService.ResolveEffectiveSettings(
            "gemini",
            modelOverride: "gemini-2.5-flash",
            apiKeyOverride: "test-key",
            thinkingOverride: AiThinkingMode.Disabled);
        byte[] payloadBytes = AiChatTransportClient.BuildChatRequestPayload(
            geminiDisabled,
            systemPrompt: "Return JSON.",
            userPrompt: "Ping",
            stream: false,
            includeThinkingParams: true,
            isProbe: false);

        using (JsonDocument payloadDoc = JsonDocument.Parse(payloadBytes))
        {
            Assert.Equal("none", payloadDoc.RootElement.GetProperty("reasoning_effort").GetString());
        }

        aiService.ResetTelemetry();
        aiService.RecordCallTelemetry(promptTokens: 1000, cachedPromptTokens: 750, completionTokens: 200, latencyMs: 420, isAutoRepair: false);
        aiService.RecordCallTelemetry(promptTokens: 600, cachedPromptTokens: 450, completionTokens: 150, latencyMs: 310, isAutoRepair: true);

        AiCallTelemetrySnapshot snap = aiService.GetTelemetrySnapshot();
        Assert.Equal(2, snap.TotalCalls);
        Assert.Equal(1, snap.AutoRepairedCalls);
        Assert.Equal(1600, snap.TotalPromptTokens);
        Assert.Equal(1200, snap.TotalCachedPromptTokens);
        Assert.Equal(350, snap.TotalCompletionTokens);
        Assert.Equal(310, snap.LastCallLatencyMs);
        Assert.Equal(0.75, snap.CacheHitRatio, precision: 3);

        aiService.ResetTelemetry();
        Assert.Equal(0, aiService.GetTelemetrySnapshot().TotalCalls);
    }

    [Fact]
    public void NetworkSecurity_BlocksSsrfLoopbackPrivateMetadataAndAllowsPublicHttps()
    {
        Assert.True(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://api.github.com/repos/dotnet/runtime", out Uri? okUri, out string? okErr));
        Assert.NotNull(okUri);
        Assert.Null(okErr);

        string[] rejectedUrls =
        [
            "http://api.github.com/zen",
            "https://user:pass@example.com/feed.xml",
            "https://localhost:8080/api",
            "https://sub.localhost/api",
            "https://printer.local/status",
            "https://service.internal/metrics",
            "https://127.0.0.1/secret",
            "https://10.0.0.8/internal",
            "https://172.16.5.4/internal",
            "https://192.168.1.1/router",
            "https://169.254.169.254/latest/meta-data/",
            "https://[::1]/loopback"
        ];

        foreach (string badUrl in rejectedUrls)
        {
            Assert.False(
                CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(badUrl, out _, out string? err),
                $"Expected '{badUrl}' to be rejected by SSRF guard.");
            Assert.False(string.IsNullOrWhiteSpace(err));
        }

        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("fd12:3456:789a::1")));
        Assert.False(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("1.1.1.1")));
    }

    [Fact]
    public void KvPrefixCacheOrdering_SharesIdenticalPrefixAcrossLocales_AndPlacesManifestBeforeTurnTail()
    {
        var baseEntries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = "Daily Hydration",
            ["goal_fmt"] = "{0} / {1} cups",
            ["add_one"] = "+1 Cup",
            ["reset"] = "Reset Today"
        };
        string[] deltaKeys = ["title", "goal_fmt", "add_one", "reset"];

        var jaPlan = new CustomWidgetLocaleTaskPlan(
            "ja-JP", "日本語", "Japanese", CustomWidgetLocaleSyncStatus.PendingFull,
            4, deltaKeys, [], deltaKeys, new Dictionary<string, string>());
        var frPlan = new CustomWidgetLocaleTaskPlan(
            "fr-FR", "Français", "French", CustomWidgetLocaleSyncStatus.PendingFull,
            4, deltaKeys, [], deltaKeys, new Dictionary<string, string>());

        string jaPrompt = CustomWidgetAiGeneratorService.BuildDeltaLocaleUserPrompt("en-US", jaPlan, baseEntries, deltaKeys, isRepair: false);
        string frPrompt = CustomWidgetAiGeneratorService.BuildDeltaLocaleUserPrompt("en-US", frPlan, baseEntries, deltaKeys, isRepair: false);

        int jaTargetIdx = jaPrompt.IndexOf("Target locale:", StringComparison.Ordinal);
        int frTargetIdx = frPrompt.IndexOf("Target locale:", StringComparison.Ordinal);
        Assert.True(jaTargetIdx > 250);
        Assert.Equal(jaTargetIdx, frTargetIdx);
        Assert.Equal(jaPrompt[..jaTargetIdx], frPrompt[..frTargetIdx]);
        Assert.True(jaTargetIdx > jaPrompt.Length * 0.80);

        const string sampleManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.water",
          "name": "Water",
          "root": "main",
          "elements": {
            "main": { "type": "Text", "text": "8 cups" }
          }
        }
        """;
        var history = new List<StudioChatMessage>
        {
            new() { Id = "m1", Role = "user", Kind = "prompt", Text = "Create water widget" },
            new() { Id = "m2", Role = "assistant", Kind = "edit", Text = "Created v1" }
        };

        string studioPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            userMessage: "Make the text accent colored",
            currentManifestJson: sampleManifest,
            briefMarkdown: "# Purpose\nTrack daily water",
            recentMessages: history,
            targetNodePath: "#main",
            uiLanguage: "en-US");

        int manifestPos = studioPrompt.IndexOf("## Current `manifest.json`", StringComparison.Ordinal);
        int historyPos = studioPrompt.IndexOf("## Recent Co-Creation Conversation Context", StringComparison.Ordinal);
        int requestPos = studioPrompt.IndexOf("## Current User Turn", StringComparison.Ordinal);
        Assert.True(manifestPos > 0);
        Assert.True(manifestPos < historyPos);
        Assert.True(historyPos < requestPos);
    }

    [Fact]
    public void FlatManifestEdit_UpdatesMetadataStateAndRemovesRootKeys_InsideParseStudioTurnResponse()
    {
        const string currentManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.hydration",
          "name": "Hydration",
          "description": "Old summary",
          "icon": "💧",
          "defaultSize": { "width": 340, "height": 380 },
          "state": { "cups": "3", "obsoleteKey": "legacy" },
          "capsule": {
            "title": "Water · ${state.cups}",
            "summary": "Keep drinking",
            "glyph": "💧",
            "badge": "${state.cups}"
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "spacing": 8, "children": ["txt_cups", "btn_add"] },
            "txt_cups": { "type": "Text", "text": "${state.cups} / 8 cups", "style": "title" },
            "btn_add": { "type": "Button", "text": "+1 Cup", "onClick": { "type": "state.increment", "key": "cups", "delta": 1 } }
          }
        }
        """;

        const string editEnvelope = """
        {
          "kind": "edit",
          "reply": "Updated size, icon, and cleaned up unused state.",
          "changeSummary": "Compact height & icon",
          "manifestEdit": {
            "patchRoot": {
              "name": "Daily Hydration Pro",
              "icon": "🚰",
              "defaultSize": { "width": 340, "height": 320 },
              "state": { "cups": "3", "goal": "8" },
              "capsule": {
                "title": "Water · ${state.cups}",
                "summary": "Keep drinking",
                "glyph": "🚰",
                "badge": "${state.cups}"
              }
            },
            "removeRootKeys": ["description"]
          }
        }
        """;

        StudioTurnResult turn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            editEnvelope,
            providerId: "deepseek",
            effectiveModel: "deepseek-chat",
            isInitialCreation: false,
            fallbackCulture: "en-US",
            currentManifestJson: currentManifest);

        Assert.True(turn.Success, turn.ErrorMessage);
        Assert.Equal("edit", turn.Kind);
        Assert.NotNull(turn.ParseResult?.Manifest);
        CustomWidgetManifest merged = turn.ParseResult!.Manifest!;
        Assert.Equal("Daily Hydration Pro", merged.Name);
        Assert.Equal("🚰", merged.Icon);
        Assert.Equal(340, merged.DefaultSize.Width);
        Assert.Equal(320, merged.DefaultSize.Height);
        Assert.True(string.IsNullOrEmpty(merged.Description));
        Assert.True(merged.State.ContainsKey("cups"));
        Assert.True(merged.State.ContainsKey("goal"));
        Assert.False(merged.State.ContainsKey("obsoleteKey"));
        Assert.Equal(2, merged.View.Children.Count);
    }

    [Fact]
    public void HealthAnalyzer_EmitsSettingsFirstHintForHardcodedParameters_AndSuppressesWhenParameterized()
    {
        const string hardcodedWeatherManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.shanghai-weather",
          "name": "Weather",
          "icon": "🌤️",
          "sources": [
            { "type": "weather", "city": "Shanghai", "intervalMs": 1800000 }
          ],
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_weather"] },
            "txt_weather": { "type": "Text", "text": "${$weather.city} · ${$weather.tempText}" }
          }
        }
        """;

        CustomWidgetManifest parsedHardcoded = CustomWidgetManifestParser.Parse(hardcodedWeatherManifest).Manifest!;
        CustomWidgetHealthReport report1 = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            hardcodedWeatherManifest,
            parsedHardcoded,
            storePayload: null,
            cultureName: "en-US");

        CustomWidgetHealthIssue hint = Assert.Single(report1.Issues, i => i.Code == "settings.hardcoded_parameter");
        Assert.False(hint.IsWarning);
        Assert.Contains("Shanghai", hint.Message, StringComparison.Ordinal);
        Assert.Contains("$settings.<key>", hint.SuggestedPrompt, StringComparison.Ordinal);

        const string parameterizedWeatherManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.city-weather",
          "name": "Weather",
          "icon": "🌤️",
          "settings": [
            { "key": "city", "type": "text", "label": "City", "default": "Shanghai" }
          ],
          "sources": [
            { "type": "weather", "city": "${$settings.city}", "intervalMs": 1800000 }
          ],
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_weather"] },
            "txt_weather": { "type": "Text", "text": "${$weather.city} · ${$weather.tempText}" }
          }
        }
        """;

        CustomWidgetManifest parsedParam = CustomWidgetManifestParser.Parse(parameterizedWeatherManifest).Manifest!;
        CustomWidgetHealthReport report2 = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            parameterizedWeatherManifest,
            parsedParam,
            storePayload: null,
            cultureName: "en-US");

        Assert.DoesNotContain(report2.Issues, i => i.Code == "settings.hardcoded_parameter");
    }

    [Fact]
    public void WebAiPromptDiet_AndSameTabFollowUpBuilder_StayCompactAndPreserveContracts()
    {
        string systemZh = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(uiLanguage: "zh-CN");
        string systemEn = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(uiLanguage: "en-US");

        Assert.InRange(systemZh.Length, 1500, 18_500);
        Assert.InRange(systemEn.Length, 1500, 22_000);
        Assert.Contains("StatCard", systemEn, StringComparison.Ordinal);
        Assert.Contains("schemaVersion", systemEn, StringComparison.Ordinal);
        Assert.Contains("硬性运行时契约", systemZh, StringComparison.Ordinal);

        string followUpZh = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "把这个按钮改成柔和绿色并加上进度条",
            uiLanguage: "zh-CN",
            targetNodePath: "#btn_action",
            pinnedCapabilityIds: ["micro-charts"],
            isCapsuleViewport: true);

        Assert.Contains("#btn_action", followUpZh, StringComparison.Ordinal);
        Assert.Contains("$.capsule", followUpZh, StringComparison.Ordinal);
        Assert.Contains("micro-charts", followUpZh, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpProtocol_Exposes8AnnotatedTools_CompactSpec_BidirectionalStudioContext_AndDshSnippet()
    {
        string dshYaml = CustomWidgetAgentConnectorService.BuildMcpSnippet(
            McpConfigFormat.DeepSeekHarnessYaml,
            exePathOverride: @"C:\Tools\DeskBox\DeskBox.exe",
            isPackagedOverride: false);
        Assert.Contains("\"@deepseek-ai/dsh-mcp-client\":", dshYaml, StringComparison.Ordinal);
        Assert.Contains("transport: stdio", dshYaml, StringComparison.Ordinal);
        Assert.Contains("C:\\\\Tools\\\\DeskBox\\\\DeskBox.exe", dshYaml, StringComparison.Ordinal);

        string pipeName = $"DeskBox.AgentBridge.Phase2Test.{Guid.NewGuid():N}";
        await using var bridge = new DeskBoxAgentBridgeServer(
            pipeName,
            widgetManagerProvider: static () => null,
            studioContextProvider: static includeManifest =>
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteBoolean("ok", true);
                    w.WriteBoolean("hostOnline", true);
                    w.WriteBoolean("isOpen", true);
                    w.WriteString("mode", "edit");
                    w.WriteString("widgetId", "custom.live-draft");
                    w.WriteBoolean("isCapsuleViewport", true);
                    w.WriteStartObject("selectedNode");
                    w.WriteString("path", "#stat_cpu");
                    w.WriteString("type", "StatCard");
                    w.WriteEndObject();
                    if (includeManifest)
                    {
                        w.WriteString("draftManifestJson", "{\"schemaVersion\":1,\"id\":\"custom.live-draft\",\"root\":\"stat_cpu\",\"elements\":{\"stat_cpu\":{\"type\":\"StatCard\",\"title\":\"CPU\",\"value\":\"12%\"}}}");
                    }
                    w.WriteEndObject();
                }
                return Task.FromResult(Encoding.UTF8.GetString(ms.ToArray()));
            });
        bridge.Start();

        string rpcInput = string.Join(
            "\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_get_widget_spec","arguments":{"detail":"compact"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"deskbox_get_widget_spec","arguments":{"detail":"full"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"deskbox_get_studio_context","arguments":{"includeManifest":true}}}""") + "\n";

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(rpcInput));
        using var outStream = new MemoryStream();
        int exitCode = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
            inputOverride: inStream,
            outputOverride: outStream,
            pipeNameOverride: pipeName);
        Assert.Equal(0, exitCode);

        string[] lines = Encoding.UTF8.GetString(outStream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(5, lines.Length);

        using (JsonDocument initDoc = JsonDocument.Parse(lines[0]))
        {
            string serverVer = initDoc.RootElement
                .GetProperty("result")
                .GetProperty("serverInfo")
                .GetProperty("version")
                .GetString()!;
            Assert.Equal(CustomWidgetAgentConnectorService.GetAppVersionString(), serverVer);
        }

        using (JsonDocument listDoc = JsonDocument.Parse(lines[1]))
        {
            JsonElement tools = listDoc.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(8, tools.GetArrayLength());
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
                JsonElement annotations = tool.GetProperty("annotations");
                Assert.Equal(JsonValueKind.Object, annotations.ValueKind);
                Assert.True(annotations.TryGetProperty("readOnlyHint", out _));
                Assert.True(annotations.TryGetProperty("destructiveHint", out _));
                Assert.True(annotations.TryGetProperty("idempotentHint", out _));
                Assert.True(annotations.TryGetProperty("openWorldHint", out _));
            }
        }

        string compactSpecText;
        string fullSpecText;
        using (JsonDocument compactDoc = JsonDocument.Parse(lines[2]))
        {
            compactSpecText = compactDoc.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString()!;
        }
        using (JsonDocument fullDoc = JsonDocument.Parse(lines[3]))
        {
            fullSpecText = fullDoc.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString()!;
        }

        Assert.True(compactSpecText.Length < fullSpecText.Length * 0.40,
            $"Expected compact spec ({compactSpecText.Length} chars) < 40% of full spec ({fullSpecText.Length} chars).");
        foreach (CustomWidgetCapabilitySpec cap in CustomWidgetCapabilityRegistry.All)
        {
            Assert.Contains(cap.Id, compactSpecText, StringComparison.Ordinal);
        }

        using (JsonDocument ctxDoc = JsonDocument.Parse(lines[4]))
        {
            string ctxJson = ctxDoc.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString()!;
            using JsonDocument innerCtx = JsonDocument.Parse(ctxJson);
            Assert.True(innerCtx.RootElement.GetProperty("isOpen").GetBoolean());
            Assert.Equal("custom.live-draft", innerCtx.RootElement.GetProperty("widgetId").GetString());
            Assert.Equal("#stat_cpu", innerCtx.RootElement.GetProperty("selectedNode").GetProperty("path").GetString());
        }

        // Offline graceful fallback when host pipe is not running
        string offlineRpc = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"deskbox_get_studio_context","arguments":{}}}""" + "\n";
        using var offIn = new MemoryStream(Encoding.UTF8.GetBytes(offlineRpc));
        using var offOut = new MemoryStream();
        int offExit = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
            inputOverride: offIn,
            outputOverride: offOut,
            pipeNameOverride: $"DeskBox.NonExistent.{Guid.NewGuid():N}");
        Assert.Equal(0, offExit);
        using JsonDocument offDoc = JsonDocument.Parse(Encoding.UTF8.GetString(offOut.ToArray()).Trim());
        string offInnerText = offDoc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using JsonDocument offInner = JsonDocument.Parse(offInnerText);
        Assert.True(offInner.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(offInner.RootElement.GetProperty("hostOnline").GetBoolean());
        Assert.False(offInner.RootElement.GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public void DynamicBilingualSync_PairsActiveUiCultureWithEnglishOrBaseLocale()
    {
        Assert.Equal(["zh-CN", "en-US"], CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes("zh-CN"));
        Assert.Equal(["ja-JP", "en-US"], CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes("ja-JP"));
        Assert.Equal(["ar-SA", "en-US"], CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes("ar-SA"));
        Assert.Equal(["en-US", "ko-KR"], CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes("en-US", baseLocale: "ko-KR"));
        Assert.Equal(["en-US", "zh-CN"], CustomWidgetLocaleCatalog.ResolveBilingualCultureCodes("en-US"));
    }

    [Fact]
    public async Task McpClientHandshakeTelemetry_AutoFixerTargetCollections_BilingualStudioPrompts_AndPackageExport_WorkEndToEnd()
    {
        // 1. MCP initialize clientInfo -> Bridge LastMcpClient & ping telemetry
        DeskBoxAgentBridgeServer.ClearLastMcpClientForTests();
        string pipeName = $"DeskBox.AgentBridge.McpHello.{Guid.NewGuid():N}";
        ConnectedMcpClientInfo? eventClient = null;
        void OnConnected(ConnectedMcpClientInfo info) => eventClient = info;
        DeskBoxAgentBridgeServer.McpClientConnected += OnConnected;
        try
        {
            await using var bridge = new DeskBoxAgentBridgeServer(pipeName, static () => null);
            bridge.Start();

            string initRpc =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"Cursor","version":"1.2.0"}}}""" + "\n";
            using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(initRpc));
            using var outStream = new MemoryStream();
            int exitCode = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
                inputOverride: inStream,
                outputOverride: outStream,
                pipeNameOverride: pipeName);
            Assert.Equal(0, exitCode);

            for (int i = 0; i < 20 && DeskBoxAgentBridgeServer.LastMcpClient is null; i++)
            {
                await Task.Delay(25);
            }

            Assert.NotNull(DeskBoxAgentBridgeServer.LastMcpClient);
            Assert.Equal("Cursor", DeskBoxAgentBridgeServer.LastMcpClient!.ClientName);
            Assert.Equal("1.2.0", DeskBoxAgentBridgeServer.LastMcpClient.ClientVersion);
            Assert.NotNull(eventClient);
            Assert.Equal("Cursor", eventClient!.ClientName);

            using JsonDocument pingDoc = JsonDocument.Parse(
                await bridge.DispatchRequestJsonAsync("{\"op\":\"ping\"}"));
            JsonElement pingRes = pingDoc.RootElement.GetProperty("result");
            Assert.Equal("Cursor", pingRes.GetProperty("lastMcpClientName").GetString());
            Assert.Equal("1.2.0", pingRes.GetProperty("lastMcpClientVersion").GetString());
        }
        finally
        {
            DeskBoxAgentBridgeServer.McpClientConnected -= OnConnected;
            DeskBoxAgentBridgeServer.ClearLastMcpClientForTests();
        }

        // 2. AutoFixer wires targetCollection for weather ("forecast") and storageDrives ("drives")
        const string unWiredManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.weather-and-drives",
          "name": "Weather & Drives",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_summary", "list_forecast", "bar_drives"] },
            "txt_summary": { "type": "Text", "text": "${$weather.tempText} · ${$drives.summary}" },
            "list_forecast": { "type": "List", "items": "forecast", "itemTemplate": "forecast_item_txt" },
            "forecast_item_txt": { "type": "Text", "text": "${$item.day}" },
            "bar_drives": { "type": "BarList", "collection": "drives", "titleField": "name", "valueField": "usedPercent" }
          }
        }
        """;
        string autoFixed = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(unWiredManifest);
        CustomWidgetManifest fixedManifest = CustomWidgetManifestParser.Parse(autoFixed).Manifest!;
        CustomWidgetSourceSpec weatherSrc = Assert.Single(fixedManifest.Sources, s => s.Type == "weather");
        CustomWidgetSourceSpec drivesSrc = Assert.Single(fixedManifest.Sources, s => s.Type == "storageDrives");
        Assert.Equal("forecast", weatherSrc.TargetCollection);
        Assert.Equal("drives", drivesSrc.TargetCollection);
        Assert.Contains(fixedManifest.Collections, c => c.Key == "forecast");
        Assert.Contains(fixedManifest.Collections, c => c.Key == "drives");

        // 3. Native Chinese Studio co-creation prompts & no deskbox-meta leak on empty-reply clarify turns
        string sysZh = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(uiLanguage: "zh-CN");
        string userZh = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            userMessage: "把文字颜色调柔和一点",
            currentManifestJson: autoFixed,
            briefMarkdown: "# 设计偏好\n极简风格",
            recentMessages: [],
            targetNodePath: "#txt_summary",
            uiLanguage: "zh-CN",
            isCapsuleViewport: true);
        Assert.Contains("deskbox-meta", sysZh, StringComparison.Ordinal);
        Assert.Contains("## 本轮用户需求", userZh, StringComparison.Ordinal);
        Assert.Contains("当前工坊激活视口：桌面胶囊模式", userZh, StringComparison.Ordinal);
        Assert.DoesNotContain("manifestPatch", userZh, StringComparison.Ordinal);

        const string emptyReplyClarifyRaw = """
        ```deskbox-meta
        {
          "kind": "clarify",
          "reply": "",
          "suggestions": ["深色极简", "明亮卡片"]
        }
        ```
        """;
        StudioTurnResult clarifyTurn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            emptyReplyClarifyRaw,
            providerId: "deepseek",
            effectiveModel: "deepseek-chat",
            isInitialCreation: true,
            fallbackCulture: "zh-CN");
        Assert.True(clarifyTurn.Success);
        Assert.Equal("clarify", clarifyTurn.Kind);
        Assert.DoesNotContain("deskbox-meta", clarifyTurn.Reply, StringComparison.Ordinal);
        Assert.Equal(["深色极简", "明亮卡片"], clarifyTurn.Suggestions);

        // 4. StudioPreferencesStore new preferences & PackageService bundle export round-trip
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-studio-opt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string prefsPath = Path.Combine(tempDir, "studio-prefs.json");
            var prefs = new CustomWidgetStudioPreferencesStore(prefsPath);
            Assert.Equal(CustomWidgetStudioPreferencesStore.EditorAuto, prefs.PreferredCodeEditor);
            Assert.Equal(CustomWidgetStudioPreferencesStore.StageThemeAuto, prefs.StageWidgetTheme);

            prefs.SetPreferredCodeEditor(CustomWidgetStudioPreferencesStore.EditorAntigravity);
            prefs.SetStageWidgetTheme(CustomWidgetStudioPreferencesStore.StageThemeDark);

            var reloadedPrefs = new CustomWidgetStudioPreferencesStore(prefsPath);
            Assert.Equal(CustomWidgetStudioPreferencesStore.EditorAntigravity, reloadedPrefs.PreferredCodeEditor);
            Assert.Equal(CustomWidgetStudioPreferencesStore.StageThemeDark, reloadedPrefs.StageWidgetTheme);

            string pkgPath = Path.Combine(tempDir, "exported.dbxwidget");
            var locales = new CustomWidgetLocaleCatalog(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh-CN"] = new Dictionary<string, string> { ["title"] = "天气磁盘" },
                    ["en-US"] = new Dictionary<string, string> { ["title"] = "Weather & Drives" }
                });
            CustomWidgetExportReport report = await CustomWidgetPackageService.ExportSharePackageFromBundleAsync(
                autoFixed,
                rawLogicJs: "export function onInit(ctx) { ctx.computed.ready = '1'; }",
                locales: locales,
                destinationPath: pkgPath);
            Assert.Equal(pkgPath, report.ExportedFilePath);
            Assert.True(File.Exists(pkgPath));

            CustomWidgetImportedBundle imported = await CustomWidgetPackageService.ReadPackageBundleAsync(pkgPath);
            Assert.Equal("custom.weather-and-drives", imported.Manifest.Id);
            Assert.False(string.IsNullOrWhiteSpace(imported.LogicJs));
            Assert.NotNull(imported.Locales);
            Assert.Equal(2, imported.Locales!.Locales.Count);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildChatRequestPayload_KimiK25AndAnthropicThinking_OmitFixedTemperatureAndClampMaxTokensAboveBudget()
    {
        var aiService = new DeskBoxAiService();

        CustomWidgetAiProviderPreset kimiPreset = CustomWidgetAiGeneratorService.FindProviderPreset("kimi");
        Assert.Equal("kimi-k2.5", kimiPreset.DefaultModel);

        ResolvedAiInvocation kimiEnabled = aiService.ResolveEffectiveSettings(
            "kimi",
            modelOverride: "kimi-k2.5",
            apiKeyOverride: "sk-kimi",
            thinkingOverride: AiThinkingMode.Enabled);
        byte[] kimiEnabledBytes = AiChatTransportClient.BuildChatRequestPayload(
            kimiEnabled,
            systemPrompt: "System",
            userPrompt: "Hello",
            stream: false,
            includeThinkingParams: true,
            isProbe: false);
        using (JsonDocument kimiEnabledDoc = JsonDocument.Parse(kimiEnabledBytes))
        {
            Assert.False(kimiEnabledDoc.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal("enabled", kimiEnabledDoc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }

        ResolvedAiInvocation kimiAuto = aiService.ResolveEffectiveSettings(
            "kimi",
            modelOverride: "kimi-k2.5",
            apiKeyOverride: "sk-kimi",
            thinkingOverride: AiThinkingMode.Auto);
        byte[] kimiAutoBytes = AiChatTransportClient.BuildChatRequestPayload(
            kimiAuto,
            systemPrompt: "System",
            userPrompt: "Hello",
            stream: false,
            includeThinkingParams: true,
            isProbe: false);
        using (JsonDocument kimiAutoDoc = JsonDocument.Parse(kimiAutoBytes))
        {
            Assert.False(kimiAutoDoc.RootElement.TryGetProperty("temperature", out _));
        }

        ResolvedAiInvocation anthropicThinking = aiService.ResolveEffectiveSettings(
            "anthropic",
            modelOverride: "claude-sonnet-4-5",
            apiKeyOverride: "sk-ant",
            thinkingOverride: AiThinkingMode.Enabled);
        byte[] anthropicDefaultBytes = AiChatTransportClient.BuildChatRequestPayload(
            anthropicThinking,
            systemPrompt: "System",
            userPrompt: "Build widget",
            stream: false,
            includeThinkingParams: true,
            isProbe: false);
        using (JsonDocument anthropicDefaultDoc = JsonDocument.Parse(anthropicDefaultBytes))
        {
            Assert.False(anthropicDefaultDoc.RootElement.TryGetProperty("temperature", out _));
            int maxTokens = anthropicDefaultDoc.RootElement.GetProperty("max_tokens").GetInt32();
            JsonElement thinkingObj = anthropicDefaultDoc.RootElement.GetProperty("thinking");
            Assert.Equal("enabled", thinkingObj.GetProperty("type").GetString());
            int budgetTokens = thinkingObj.GetProperty("budget_tokens").GetInt32();
            Assert.True(maxTokens > budgetTokens);
        }

        byte[] anthropicConstrainedBytes = AiChatTransportClient.BuildChatRequestPayload(
            anthropicThinking,
            systemPrompt: "System",
            userPrompt: "Build widget",
            stream: false,
            includeThinkingParams: true,
            isProbe: false,
            includeExplicitStudioMaxTokens: true,
            studioMaxTokensOverride: 2048);
        using (JsonDocument anthropicConstrainedDoc = JsonDocument.Parse(anthropicConstrainedBytes))
        {
            Assert.False(anthropicConstrainedDoc.RootElement.TryGetProperty("temperature", out _));
            int maxTokens = anthropicConstrainedDoc.RootElement.GetProperty("max_tokens").GetInt32();
            int budgetTokens = anthropicConstrainedDoc.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
            Assert.Equal(2048, maxTokens);
            Assert.True(maxTokens > budgetTokens);
        }
    }

    [Fact]
    public void StudioSessionController_DiscardActiveDraftCleanly_PreventsExitHookFromResavingDraft()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-discard-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var loc = new LocalizationService(settingsService);
            var prefs = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            var sessionStore = new CustomWidgetStudioSessionStore(Path.Combine(tempDir, "sessions"));

            var session = new StudioSessionController(loc, NullStudioHostBridge.Instance, prefs, sessionStore);
            const string manifestV1 = """
            {
              "schemaVersion": 1,
              "id": "custom.draft-check",
              "name": "Draft Check",
              "root": "txt_v1",
              "elements": {
                "txt_v1": { "type": "Text", "text": "v1" }
              }
            }
            """;

            session.AddRevision(manifestV1, "Initial draft", isAiChange: true, source: "ai", isCommitted: false, markDirty: true);
            session.PersistActiveDraftIfDirty();
            Assert.NotNull(sessionStore.TryLoadCreateDraft());

            session.DiscardActiveDraftCleanly();
            Assert.Null(sessionStore.TryLoadCreateDraft());

            // Simulate window exit hook firing after user clicked Discard Draft
            session.HasDirtySettingOverrides = true;
            session.PersistActiveDraftIfDirty();
            Assert.Null(sessionStore.TryLoadCreateDraft());

            // Subsequent fresh edit clears suppression and allows saving again
            const string manifestV2 = """
            {
              "schemaVersion": 1,
              "id": "custom.draft-check",
              "name": "Draft Check V2",
              "root": "txt_v2",
              "elements": {
                "txt_v2": { "type": "Text", "text": "v2" }
              }
            }
            """;
            session.AddRevision(manifestV2, "New edit", isAiChange: true, source: "ai", isCommitted: false, markDirty: true);
            session.PersistActiveDraftIfDirty();
            Assert.NotNull(sessionStore.TryLoadCreateDraft());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StudioToolTransport_WriteBundleWithoutFinishTurn_MarksPendingFinishTurnTrue()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-pending-finish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test",
                CustomModel: "deepseek-chat",
                ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));

            const string validManifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.two-step",
              "name": "Two Step Widget",
              "script": { "entry": "logic.js" },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": "12,10,12,12",
                  "children": ["txt_label"]
                },
                "txt_label": { "type": "Text", "text": "${$computed.label}" }
              }
            }
            """;

            string writeArgs = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["manifestJson"] = validManifestJson
            });
            string round1Sse = BuildNativeToolCallsSse([("call_wb_1", StudioToolTransport.ToolWriteBundle, writeArgs)]);
            string round2Sse = BuildNativeToolCallsSse(
            [
                ("call_ps_2", StudioToolTransport.ToolPatchScript, """{"content":"export function onInit(ctx) { ctx.computed.label = 'Ready'; }"}"""),
                ("call_fin_2", StudioToolTransport.ToolFinishTurn, """{"kind":"edit","reply":"Created widget.","changeSummary":"Initial widget"}""")
            ]);

            var requestBodies = new List<string>();
            int callIdx = 0;
            using var httpClient = new HttpClient(new DelegateHttpMessageHandler(async (req, ct) =>
            {
                string body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : string.Empty;
                requestBodies.Add(body);
                int current = Interlocked.Increment(ref callIdx);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(current == 1 ? round1Sse : round2Sse, Encoding.UTF8, "text/event-stream")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            var generator = new CustomWidgetAiGeneratorService(
                aiService,
                diagnosticArchive: new CustomWidgetDiagnosticArchive(Path.Combine(tempDir, "archive")));

            StudioTurnResult result = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "Create a simple status card",
                uiLanguage: "en-US");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.WasAutoRepaired);
            Assert.Equal(2, requestBodies.Count);

            using JsonDocument round2ReqDoc = JsonDocument.Parse(requestBodies[1]);
            string? toolMsgContent = round2ReqDoc.RootElement
                .GetProperty("messages")
                .EnumerateArray()
                .FirstOrDefault(static m => m.GetProperty("role").GetString() == "tool")
                .GetProperty("content")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(toolMsgContent));
            using JsonDocument toolResDoc = JsonDocument.Parse(toolMsgContent!);
            Assert.True(toolResDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(toolResDoc.RootElement.GetProperty("pendingFinishTurn").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomWidgetCapabilityRegistry_ApplyRemotePatches_OverridesGotchasAndRestoresOnClear()
    {
        try
        {
            IReadOnlyList<CustomWidgetCapabilitySpec> baseline = CustomWidgetCapabilityRegistry.All;
            CustomWidgetCapabilitySpec originalHttp = baseline.Single(c => c.Id == "http-and-rss-feeds");

            var patch = new CustomWidgetCapabilityPatchDto
            {
                Id = "http-and-rss-feeds",
                CompactContractMarkdown = "- ExtraGotchas: enforce 15s min interval.\n- RecommendedEndpointsJson: {\"fx\":\"https://open.er-api.com/v6/latest/USD\"}",
                GoldenSnippetJson = """{"type":"httpJson","url":"https://open.er-api.com/v6/latest/USD","intervalMs":60000}""",
                TriggerKeywords = ["remote-patched-fx"]
            };

            IReadOnlyList<CustomWidgetCapabilitySpec> patchedList =
                CustomWidgetCapabilityRegistry.ApplyCapabilityPatches(baseline, [patch]);
            CustomWidgetCapabilitySpec patchedHttp = patchedList.Single(c => c.Id == "http-and-rss-feeds");
            Assert.Contains("ExtraGotchas: enforce 15s min interval.", patchedHttp.CompactContractMarkdown, StringComparison.Ordinal);
            Assert.Contains("RecommendedEndpointsJson", patchedHttp.CompactContractMarkdown, StringComparison.Ordinal);
            Assert.Contains("remote-patched-fx", patchedHttp.TriggerKeywords);

            IReadOnlyList<CustomWidgetCapabilitySpec> restoredList =
                CustomWidgetCapabilityRegistry.ApplyCapabilityPatches(baseline, null);
            CustomWidgetCapabilitySpec restoredHttp = restoredList.Single(c => c.Id == "http-and-rss-feeds");
            Assert.Equal(originalHttp.CompactContractMarkdown, restoredHttp.CompactContractMarkdown);
            Assert.Equal(originalHttp.GoldenSnippetJson, restoredHttp.GoldenSnippetJson);
            Assert.DoesNotContain("remote-patched-fx", restoredHttp.TriggerKeywords);
        }
        finally
        {
            CustomWidgetCapabilityRegistry.ReleaseIdleCache();
        }
    }

    [Fact]
    public void AiMonitorPreferencesStore_HidingOrRemovingMonitorKey_NeverMutatesGlobalStudioCredentials()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-aimonitor-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var globalStore = new CustomWidgetAiCredentialStore(tempDir);
            var monitorPrefs = new CustomWidgetAiMonitorPreferencesStore(tempDir);

            globalStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-global-studio-secret",
                Enabled: true,
                CustomModel: "deepseek-chat"));

            AiProviderCredentialEntry? initialEffective = monitorPrefs.ResolveEffectiveCredential("deepseek", globalStore);
            Assert.NotNull(initialEffective);
            Assert.Equal("sk-global-studio-secret", initialEffective!.ApiKey);
            Assert.True(initialEffective.Enabled);

            monitorPrefs.SetProviderMonitorEnabled("deepseek", false);
            AiProviderCredentialEntry? disabledEffective = monitorPrefs.ResolveEffectiveCredential("deepseek", globalStore);
            Assert.NotNull(disabledEffective);
            Assert.False(disabledEffective!.Enabled);

            AiProviderCredentialEntry? globalAfterDisable = globalStore.Get("deepseek");
            Assert.NotNull(globalAfterDisable);
            Assert.Equal("sk-global-studio-secret", globalAfterDisable!.ApiKey);
            Assert.True(globalAfterDisable.Enabled);

            monitorPrefs.Upsert(new AiMonitorProviderPreference(
                ProviderId: "deepseek",
                Enabled: true,
                ApiKey: "sk-monitor-override-key"));
            Assert.Equal("sk-monitor-override-key", monitorPrefs.ResolveEffectiveCredential("deepseek", globalStore)!.ApiKey);

            monitorPrefs.RemoveMonitorKeyOrHideProvider("deepseek", globalStore);
            AiProviderCredentialEntry? afterRemoveEffective = monitorPrefs.ResolveEffectiveCredential("deepseek", globalStore);
            Assert.NotNull(afterRemoveEffective);
            Assert.False(afterRemoveEffective!.Enabled);

            AiProviderCredentialEntry? globalAfterRemove = globalStore.Get("deepseek");
            Assert.NotNull(globalAfterRemove);
            Assert.Equal("sk-global-studio-secret", globalAfterRemove!.ApiKey);
            Assert.True(globalAfterRemove.Enabled);

            var reloadedGlobalStore = new CustomWidgetAiCredentialStore(tempDir);
            AiProviderCredentialEntry? persistedGlobal = reloadedGlobalStore.Get("deepseek");
            Assert.NotNull(persistedGlobal);
            Assert.Equal("sk-global-studio-secret", persistedGlobal!.ApiKey);
            Assert.True(persistedGlobal.Enabled);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string BuildNativeToolCallsSse(IReadOnlyList<(string Id, string Name, string ArgsJson)> calls)
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

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }
}
