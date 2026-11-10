using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Phase2AiExperienceAndMcpRegressionTests
{
    [Fact]
    public void CapabilityRegistry_LoadsAll27CapabilitiesFromEmbeddedJson_AndSkillCatalogOnDropUsesStorage()
    {
        IReadOnlyList<CustomWidgetCapabilitySpec> all = CustomWidgetCapabilityRegistry.All;
        Assert.Equal(35, all.Count);
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

        CustomWidgetSkillCard feedAndDropCard = CustomWidgetSkillCatalog.AllCards.Single(
            c => c.MarkdownContent.Contains("Desktop File Drop (`onDrop`)", StringComparison.Ordinal));
        Assert.Contains("\"storage\": \"reference\"", feedAndDropCard.MarkdownContent, StringComparison.Ordinal);
        Assert.DoesNotContain("copyToWidget", feedAndDropCard.MarkdownContent, StringComparison.Ordinal);
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

        Assert.Equal("gpt-5-mini", DeskBoxAiService.TryResolveFastSiblingModel(openai, "gpt-5", usesCustomEndpoint: false));
        Assert.Equal("gemini-2.5-flash", DeskBoxAiService.TryResolveFastSiblingModel(gemini, "gemini-2.5-pro", usesCustomEndpoint: false));
        Assert.Equal("claude-haiku-4-5", DeskBoxAiService.TryResolveFastSiblingModel(anthropic, "claude-opus-4-1", usesCustomEndpoint: false));
        Assert.Null(DeskBoxAiService.TryResolveFastSiblingModel(openai, "gpt-5", usesCustomEndpoint: true));

        var aiService = new DeskBoxAiService();
        ResolvedAiInvocation geminiDisabled = aiService.ResolveEffectiveSettings(
            "gemini",
            modelOverride: "gemini-2.5-flash",
            apiKeyOverride: "test-key",
            thinkingOverride: AiThinkingMode.Disabled);
        byte[] payloadBytes = DeskBoxAiService.BuildChatRequestPayload(
            geminiDisabled,
            systemPrompt: "Return JSON.",
            userPrompt: "Ping",
            assistantPrefill: null,
            repairPrompt: null,
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
          "view": { "type": "Text", "text": "8 cups" }
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
            targetNodePath: "$.view",
            uiLanguage: "en-US");

        int manifestPos = studioPrompt.IndexOf("## Current `manifest.json`", StringComparison.Ordinal);
        int historyPos = studioPrompt.IndexOf("## Recent Co-Creation Conversation Context", StringComparison.Ordinal);
        int requestPos = studioPrompt.IndexOf("## Current User Turn", StringComparison.Ordinal);
        Assert.True(manifestPos > 0);
        Assert.True(manifestPos < historyPos);
        Assert.True(historyPos < requestPos);
    }

    [Fact]
    public void Rfc7396ManifestPatch_MergesMetadataAndDeletesNullKeys_InsideParseStudioTurnResponse()
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
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              { "type": "Text", "text": "${state.cups} / 8 cups", "style": "title" },
              { "type": "Button", "text": "+1 Cup", "onClick": { "type": "state.increment", "key": "cups", "delta": 1 } }
            ]
          }
        }
        """;

        const string patchEnvelope = """
        {
          "kind": "edit",
          "reply": "Updated size, icon, and cleaned up unused state.",
          "changeSummary": "Compact height & icon",
          "manifestPatch": {
            "name": "Daily Hydration Pro",
            "icon": "🚰",
            "defaultSize": { "height": 320 },
            "state": { "obsoleteKey": null, "goal": "8" },
            "capsule": { "glyph": "🚰" }
          }
        }
        """;

        StudioTurnResult turn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            patchEnvelope,
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
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$weather.city} · ${$weather.tempText}" }
            ]
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
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$weather.city} · ${$weather.tempText}" }
            ]
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
        string standaloneZh = CustomWidgetTemplates.BuildChatAiPrompt(
            "做一个每日喝水打卡格子",
            uiLanguage: "zh-CN");
        string standaloneEn = CustomWidgetTemplates.BuildChatAiPrompt(
            "Build a daily hydration tracker widget",
            uiLanguage: "en-US");

        Assert.InRange(standaloneZh.Length, 1500, 15_500);
        Assert.InRange(standaloneEn.Length, 1500, 17_000);
        Assert.Contains("StatCard", standaloneEn, StringComparison.Ordinal);
        Assert.Contains("Strict Output Rules", standaloneEn, StringComparison.Ordinal);
        Assert.Contains("输出铁律", standaloneZh, StringComparison.Ordinal);

        string followUpZh = CustomWidgetTemplates.BuildChatAiFollowUpPrompt(
            "把这个按钮改成柔和绿色并加上进度条",
            uiLanguage: "zh-CN",
            targetNodePath: "$.view.children[1]",
            pinnedCapabilityIds: ["micro-charts"],
            isCapsuleViewport: true);

        Assert.InRange(followUpZh.Length, 120, 2200);
        Assert.Contains("$.view.children[1]", followUpZh, StringComparison.Ordinal);
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
                    w.WriteString("path", "$.view.children[0]");
                    w.WriteString("type", "StatCard");
                    w.WriteEndObject();
                    if (includeManifest)
                    {
                        w.WriteString("draftManifestJson", "{\"schemaVersion\":1,\"id\":\"custom.live-draft\"}");
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
            Assert.Equal("$.view.children[0]", innerCtx.RootElement.GetProperty("selectedNode").GetProperty("path").GetString());
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
}
