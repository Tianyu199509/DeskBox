using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class Phase1AiAgentAndI18nRegressionTests
{
    [Fact]
    public async Task McpStdioLoop_HandlesMultiByteCjkAndEmojiContentLengthFramesBackToBack_AndExposesDynamicCapabilityEnum()
    {
        var utf8 = new UTF8Encoding(false);
        using var stdin = new MemoryStream();
        using var stdout = new MemoryStream();

        static void WriteFramed(Stream target, Encoding enc, string json)
        {
            byte[] payload = enc.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            target.Write(header, 0, header.Length);
            target.Write(payload, 0, payload.Length);
        }

        string initReq = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"test-client","version":"1.0"}}}""";
        string listToolsReq = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

        string multiByteManifest = """
        {
          "schemaVersion": 1,
          "id": "deskbox.test.multibyte",
          "name": "今日喝水 🚀 東京の天気 العربية",
          "icon": "💧",
          "defaultSize": { "width": 280, "height": 180 },
          "view": {
            "type": "stack",
            "children": [
              { "type": "text", "text": "✨ 進捗 100% · حالة ممتازة", "style": "title" }
            ]
          }
        }
        """;
        string escapedManifest = JsonSerializer.Serialize(multiByteManifest);
        string validateReq = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"deskbox_validate_manifest\",\"arguments\":{\"manifestJson\":" + escapedManifest + "}}}";

        WriteFramed(stdin, utf8, initReq);
        WriteFramed(stdin, utf8, listToolsReq);
        WriteFramed(stdin, utf8, validateReq);
        stdin.Position = 0;

        int exitCode = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(
            inputOverride: stdin,
            outputOverride: stdout,
            pipeNameOverride: $"deskbox-nonexistent-pipe-{Guid.NewGuid():N}");
        Assert.Equal(0, exitCode);

        stdout.Position = 0;
        List<JsonDocument> responses = ReadAllFramedJsonResponses(stdout);
        Assert.Equal(3, responses.Count);

        // Response 2: tools/list must expose dynamic capability enum matching CustomWidgetCapabilityRegistry.All
        JsonElement tools = responses[1].RootElement.GetProperty("result").GetProperty("tools");
        JsonElement capTool = default;
        bool foundCapTool = false;
        foreach (JsonElement t in tools.EnumerateArray())
        {
            if (string.Equals(t.GetProperty("name").GetString(), "deskbox_get_capability_spec", StringComparison.Ordinal))
            {
                capTool = t;
                foundCapTool = true;
                break;
            }
        }

        Assert.True(foundCapTool);
        JsonElement idProp = capTool.GetProperty("inputSchema").GetProperty("properties").GetProperty("capabilityId");
        JsonElement enumArr = idProp.GetProperty("enum");
        Assert.Equal(CustomWidgetCapabilityRegistry.All.Count, enumArr.GetArrayLength());
        var enumValues = enumArr.EnumerateArray().Select(static x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("system-control", enumValues);
        Assert.Contains("secret-vault", enumValues);

        // Response 3: deskbox_validate_manifest parsed multi-byte UTF-8 frame without truncation or hang
        string validateText = responses[2].RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
        Assert.Contains("\"isValid\":true", validateText.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

        foreach (JsonDocument d in responses)
        {
            d.Dispose();
        }
    }

    [Fact]
    public void StageStore_TryConsumeBundle_HydratesFullBundleAndRejectsTamperedLogicJsOrLocales()
    {
        string tempStageRoot = Path.Combine(Path.GetTempPath(), $"deskbox-p1-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStageRoot);
        string prevRoot = CustomWidgetAgentStageStore.RootOverride;
        CustomWidgetAgentStageStore.RootOverride = tempStageRoot;
        try
        {
            const string manifestCompact = "{\"id\":\"deskbox.stage.hydrate\",\"schemaVersion\":1}";
            const string logicJs = "function onInit(ctx) { ctx.state.ready = '1'; }";
            const string localesJson = "{\"_meta\":{\"schemaVersion\":1,\"baseLocale\":\"en-US\",\"hashes\":{\"title\":\"abcd1234\"}},\"en-US\":{\"title\":\"Hydrate\"}}";
            const string localesJsonReordered = "{\n  \"en-US\": { \"title\": \"Hydrate\" },\n  \"_meta\": { \"hashes\": { \"title\": \"abcd1234\" }, \"baseLocale\": \"en-US\", \"schemaVersion\": 1 }\n}";

            string stageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                WidgetId: "w-hydrate-1",
                ManifestJson: manifestCompact,
                LogicJs: logicJs,
                HasLogicJs: true,
                LocalesJson: localesJson,
                HasLocales: true,
                ResizeToDefault: true));

            // Commit supplying ONLY manifestJson (omitting logicJs & locales, just like MCP callers)
            bool consumed = CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId,
                manifestCompact,
                hasCallerLogicJs: false,
                callerLogicJs: null,
                hasCallerLocales: false,
                callerLocalesJson: null,
                out AgentStageBundle? hydratedBundle,
                out string consumeErr,
                cultureName: "en-US");

            Assert.True(consumed, consumeErr);
            Assert.NotNull(hydratedBundle);
            Assert.Equal("w-hydrate-1", hydratedBundle!.WidgetId);
            Assert.True(hydratedBundle.HasLogicJs);
            Assert.Equal(logicJs, hydratedBundle.LogicJs);
            Assert.True(hydratedBundle.HasLocales);
            Assert.Equal(localesJson, hydratedBundle.LocalesJson);
            Assert.True(hydratedBundle.ResizeToDefault);

            // Canonical JSON hash treats reordered/pretty-printed locales JSON identically
            Assert.Equal(
                CustomWidgetAgentStageStore.ComputeCanonicalJsonHash(localesJson),
                CustomWidgetAgentStageStore.ComputeCanonicalJsonHash(localesJsonReordered));

            // Second stage: tampering with logicJs or locales at commit time must be rejected
            string stageId2 = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                WidgetId: "w-hydrate-2",
                ManifestJson: manifestCompact,
                LogicJs: logicJs,
                HasLogicJs: true,
                LocalesJson: localesJson,
                HasLocales: true,
                ResizeToDefault: false));

            bool tamperedLogic = CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId2,
                manifestCompact,
                hasCallerLogicJs: true,
                callerLogicJs: "function onInit(ctx) { ctx.state.tampered = '1'; }",
                hasCallerLocales: false,
                callerLocalesJson: null,
                out _,
                out string tamperErr,
                cultureName: "zh-CN");
            Assert.False(tamperedLogic);
            Assert.Contains("不一致", tamperErr);

            bool tamperedLocales = CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId2,
                manifestCompact,
                hasCallerLogicJs: false,
                callerLogicJs: null,
                hasCallerLocales: true,
                callerLocalesJson: "{\"en-US\":{\"title\":\"Tampered\"}}",
                out _,
                out string tamperLocErr,
                cultureName: "zh-CN");
            Assert.False(tamperedLocales);
            Assert.Contains("不一致", tamperLocErr);

            // Original untampered stage remains intact and consumable
            Assert.True(CustomWidgetAgentStageStore.TryConsumeBundle(
                stageId2,
                manifestCompact,
                hasCallerLogicJs: false,
                callerLogicJs: null,
                hasCallerLocales: false,
                callerLocalesJson: null,
                out _,
                out _));
        }
        finally
        {
            CustomWidgetAgentStageStore.RootOverride = prevRoot;
            try
            {
                Directory.Delete(tempStageRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void AllCapabilityRegistryGoldenSnippets_AndBuiltinAiTemplates_ParseAndValidateCleanly()
    {
        Assert.True(CustomWidgetCapabilityRegistry.All.Count >= 26);
        foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
        {
            string wrappedManifest = WrapCapabilitySnippetAsManifest(spec.GoldenSnippetJson);
            CustomWidgetParseResult parsed = string.Equals(spec.Id, "script-sandbox", StringComparison.Ordinal)
                ? CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                    wrappedManifest,
                    "export function onInit(ctx) { ctx.computed.summary = 'ok'; ctx.computed.score = '95'; }\nexport function recordSession(ctx, arg) {}",
                    locales: null).ToParseResult()
                : CustomWidgetManifestParser.Parse(wrappedManifest);
            string diagSummary = string.Join(
                "; ",
                parsed.Diagnostics.Where(static d => d.IsError).Select(static d => $"{d.Path}: {d.Message}"));
            Assert.True(parsed.IsValid, $"Capability '{spec.Id}' GoldenSnippetJson has errors: {diagSummary}");
        }

        foreach (string templateFolder in new[] { "ai-inbox", "agent-hud" })
        {
            string templateDir = TestPaths.FromRepository($"src/DeskBox/Assets/CustomWidgets/Templates/{templateFolder}");
            string manifestJson = File.ReadAllText(Path.Combine(templateDir, "manifest.json"), Encoding.UTF8);
            CustomWidgetLocaleCatalog locales = CustomWidgetLocaleCatalog.LoadFromDirectory(Path.Combine(templateDir, "locales"));
            Assert.Equal(13, locales.Locales.Count);

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(static d => d.Message)));

            if (templateFolder == "agent-hud")
            {
                foreach ((string culture, IReadOnlyDictionary<string, string> dict) in locales.Locales)
                {
                    foreach (string v in dict.Values)
                    {
                        Assert.DoesNotContain("WorkBuddy", v, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task AiCoTGuard_AndStudioRound2Repair_NeverScrapeReasoningOrLoseEnvelopeContract()
    {
        // 1. Multiple <think> blocks and unclosed <think> handling
        CustomWidgetAiGeneratorService.SplitInlineThinkBlock(
            "<think>draft {\"wrong\":1}</think>Hello<think>second thought</think>{\"ok\":true}",
            out string think1,
            out string visible1,
            out bool unclosed1);
        Assert.False(unclosed1);
        Assert.Contains("draft", think1);
        Assert.Contains("second thought", think1);
        Assert.Equal("Hello{\"ok\":true}", visible1);

        CustomWidgetAiGeneratorService.SplitInlineThinkBlock(
            "<think>truncated chain of thought {\"half\":1}",
            out string think2,
            out string visible2,
            out bool unclosed2);
        Assert.True(unclosed2);
        Assert.Contains("truncated chain of thought", think2);
        Assert.Equal(string.Empty, visible2);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-p1-ai-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-123456",
                CustomEndpoint: "https://relay.example/v1/chat/completions",
                CustomModel: "deepseek-reasoner",
                ThinkingMode: "enabled"));

            int callIndex = 0;
            var capturedBodies = new List<string>();
            using var httpClient = new HttpClient(new StubHttpHandler(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                capturedBodies.Add(body);
                callIndex++;

                // Call 1: CompleteJsonAsync receives empty content + draft JSON in reasoning_content -> MUST fail!
                if (callIndex == 1)
                {
                    const string cotOnlyJson = """
                    {
                      "choices": [
                        {
                          "message": {
                            "role": "assistant",
                            "content": "",
                            "reasoning_content": "{\"poisonedDraft\":true}"
                          }
                        }
                      ]
                    }
                    """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(cotOnlyJson, Encoding.UTF8, "application/json")
                    };
                }

                // Call 2: Studio Turn Round 1 returns broken manifest (invalid component type)
                if (callIndex == 2)
                {
                    const string round1Sse =
                        "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"kind\\\":\\\"edit\\\",\\\"reply\\\":\\\"Done\\\",\\\"changeSummary\\\":\\\"Init\\\",\\\"manifest\\\":{\\\"schemaVersion\\\":1,\\\"id\\\":\\\"deskbox.broken\\\",\\\"name\\\":\\\"Test\\\",\\\"icon\\\":\\\"✨\\\",\\\"defaultSize\\\":{\\\"width\\\":260,\\\"height\\\":160},\\\"view\\\":{\\\"type\\\":\\\"unknown_bad_widget_node\\\"}}}\"}}]}\n\n" +
                        "data: [DONE]\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(round1Sse, Encoding.UTF8, "text/event-stream")
                    };
                }

                // Call 3: Studio Turn Round 2 auto-repair returns valid Studio envelope
                const string round2Sse =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"kind\\\":\\\"edit\\\",\\\"reply\\\":\\\"Fixed!\\\",\\\"changeSummary\\\":\\\"Repaired view\\\",\\\"manifest\\\":{\\\"schemaVersion\\\":1,\\\"id\\\":\\\"deskbox.repaired\\\",\\\"name\\\":\\\"Repaired\\\",\\\"icon\\\":\\\"✨\\\",\\\"defaultSize\\\":{\\\"width\\\":260,\\\"height\\\":160},\\\"view\\\":{\\\"type\\\":\\\"stack\\\",\\\"children\\\":[{\\\"type\\\":\\\"text\\\",\\\"text\\\":\\\"Ready\\\"}]}}}\"}}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(round2Sse, Encoding.UTF8, "text/event-stream")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, httpClient: httpClient);
            AiStructuredCompletionResult cotRes = await aiService.CompleteJsonAsync(
                userPrompt: "Return JSON",
                providerIdOverride: "deepseek");
            Assert.False(cotRes.Success);
            Assert.DoesNotContain("poisonedDraft", cotRes.Json);

            var generator = new CustomWidgetAiGeneratorService(aiService);
            StudioTurnResult studioRes = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "Make a simple status card",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: "✨",
                allowSampleData: false,
                uiLanguage: "en-US");

            Assert.True(studioRes.Success, studioRes.ErrorMessage);
            Assert.Equal("edit", studioRes.Kind);
            Assert.Equal("Fixed!", studioRes.Reply);

            // Verify Round 2 request preserved Studio envelope system prompt & omitted duplicate broken JSON echo
            Assert.Equal(3, capturedBodies.Count);
            using JsonDocument round2Doc = JsonDocument.Parse(capturedBodies[2]);
            JsonElement msgs = round2Doc.RootElement.GetProperty("messages");
            string round2System = msgs[0].GetProperty("content").GetString()!;
            string round2RepairUser = msgs[msgs.GetArrayLength() - 1].GetProperty("content").GetString()!;
            Assert.Contains("\"kind\"", round2System, StringComparison.Ordinal);
            Assert.DoesNotContain("unknown_bad_widget_node\"}}}", round2RepairUser, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ViewModel_ClipboardSequenceShortCircuit_AndOcclusionResume_WorkDeterministically()
    {
        string tempStoreDir = Path.Combine(Path.GetTempPath(), $"deskbox-p1-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStoreDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.test.clip",
              "name": "Clip Watcher",
              "icon": "📋",
              "defaultSize": { "width": 260, "height": 150 },
              "sources": [
                { "id": "clip", "type": "clipboard", "intervalMs": 2000 }
              ],
              "view": {
                "type": "stack",
                "children": [
                  { "type": "text", "text": "${$clipboard.text}" }
                ]
              }
            }
            """;

            var config = new WidgetConfig
            {
                Id = "custom-clip-test",
                Name = "Clip Watcher",
                WidgetKind = WidgetKind.Custom
            };
            var settingsService = new SettingsService(Path.Combine(tempStoreDir, "settings.json"));
            var loc = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("custom-clip-test", tempStoreDir);
            using var vm = new CustomWidgetViewModel(config, loc, store, dispatcherQueue: null, isPreviewOnly: true);

            uint currentSeq = 100;
            int comReadCount = 0;
            string currentClipText = "First copied text";
            vm.ClipboardSequenceNumberProviderOverride = () => currentSeq;
            vm.ClipboardTextReaderOverride = () =>
            {
                comReadCount++;
                return Task.FromResult<string?>(currentClipText);
            };

            vm.ApplyPreviewBundle(manifestJson, null, null, preserveRuntimeData: false);
            await vm.RefreshClipboardNowAsync();
            int readsAfterInitial = comReadCount;
            Assert.True(readsAfterInitial >= 1);

            int rebuilds = 0;
            int metricRefreshes = 0;
            vm.ViewRebuildRequested += (_, _) => rebuilds++;
            vm.MetricsRefreshed += (_, _) => metricRefreshes++;

            // Unchanged sequence number -> zero COM Clipboard reads!
            await vm.RefreshClipboardNowAsync();
            await vm.RefreshClipboardNowAsync();
            Assert.Equal(readsAfterInitial, comReadCount);

            // New sequence number with text -> 1 COM read, MetricsRefreshed (in-place scalar update, zero XAML rebuild!)
            currentSeq = 101;
            currentClipText = "Updated copied text";
            await vm.RefreshClipboardNowAsync();
            Assert.Equal(readsAfterInitial + 1, comReadCount);
            Assert.Equal(0, rebuilds);
            Assert.True(metricRefreshes >= 1);

            // Monitor occlusion suspend & resume -> fires immediate MetricsRefreshed on wake
            int wakeRefreshes = 0;
            vm.MetricsRefreshed += (_, _) => wakeRefreshes++;
            vm.OnMonitorOcclusionChanged(isOccluded: true);
            Assert.False(vm.IsWindowVisible);
            vm.OnMonitorOcclusionChanged(isOccluded: false);
            Assert.True(vm.IsWindowVisible);
            Assert.True(wakeRefreshes >= 1);

            vm.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(tempStoreDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void I18nEngine_DetectsAll13Scripts_ExtractsEnglishClipboardProse_AndEnforcesExactPlaceholderSetEquality()
    {
        static string BuildSimpleManifest(string title, string buttonLabel, string? copyText = null)
        {
            string actionJson = copyText is null
                ? """{ "type": "state.set", "key": "k", "value": "1" }"""
                : $$"""{ "type": "clipboard.copy", "text": {{JsonSerializer.Serialize(copyText)}} }""";
            return $$"""
            {
              "schemaVersion": 1,
              "id": "deskbox.test.i18n",
              "name": {{JsonSerializer.Serialize(title)}},
              "icon": "🌐",
              "defaultSize": { "width": 260, "height": 150 },
              "view": {
                "type": "stack",
                "children": [
                  {
                    "type": "button",
                    "label": {{JsonSerializer.Serialize(buttonLabel)}},
                    "onClick": {{actionJson}}
                  }
                ]
              }
            }
            """;
        }

        Assert.Equal("ja-JP", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("東京の天気", "リセット"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("ja-JP", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("天気予報", "更新"), fallbackCulture: "ja-JP").BaseLocale);
        Assert.Equal("ko-KR", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("오늘의 할 일", "초기화"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("ar-SA", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("الطقس اليوم", "تحديث"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("ru-RU", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("Список задач", "Сбросить"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("hi-IN", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("आज का कार्य", "रीसेट"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("bn-BD", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("আজকের কাজ", "রিসেট"), fallbackCulture: "en-US").BaseLocale);
        Assert.Equal("de-DE", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("Tagesziel", "Zurücksetzen"), fallbackCulture: "de-DE").BaseLocale);
        Assert.Equal("en-US", CustomWidgetI18nEngine.Compile(BuildSimpleManifest("Daily Hydration", "Add Cup"), fallbackCulture: "ja-JP").BaseLocale);

        // English prose in clipboard.copy is localized, whereas CLI commands & URLs remain literal
        var proseCompiled = CustomWidgetI18nEngine.Compile(
            BuildSimpleManifest("Hydration", "Copy", "Copied daily hydration summary!"),
            fallbackCulture: "en-US");
        Assert.Contains("Copied daily hydration summary!", proseCompiled.BaseEntries.Values);

        var cliCompiled = CustomWidgetI18nEngine.Compile(
            BuildSimpleManifest("Agent", "Copy CLI", "DeskBox.exe widget push --id demo"),
            fallbackCulture: "en-US");
        Assert.DoesNotContain("DeskBox.exe widget push --id demo", cliCompiled.BaseEntries.Values);

        // Exact placeholder set equality & Japanese Kana residue detection
        Assert.False(CustomWidgetI18nEngine.HasMatchingPlaceholders("Save settings", "Einstellungen {0} speichern"));
        Assert.False(CustomWidgetI18nEngine.HasMatchingPlaceholders("Done {0}", "Fertig {0} / {1}"));
        Assert.True(CustomWidgetI18nEngine.HasMatchingPlaceholders("Step {0} of {1}", "全 {1} 件中 {0} 件目"));

        Assert.True(CustomWidgetI18nEngine.HasUntranslatedCjkResidue("en-US", false, "東京の天気", "Tokyo の weather"));
        Assert.False(CustomWidgetI18nEngine.HasUntranslatedCjkResidue("ja-JP", true, "東京の天気", "東京の天気"));

        // RTL detection
        Assert.True(CustomWidgetLocaleCatalog.IsRightToLeftCulture("ar-SA"));
        Assert.False(CustomWidgetLocaleCatalog.IsRightToLeftCulture("ja-JP"));
        Assert.False(CustomWidgetLocaleCatalog.IsRightToLeftCulture("zh-CN"));
    }

    private static string WrapCapabilitySnippetAsManifest(string snippetJson)
    {
        using JsonDocument doc = JsonDocument.Parse(snippetJson);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("id", "deskbox.cap.snippet");
            writer.WriteString("name", "Capability Snippet");
            writer.WriteString("icon", "✨");

            if (root.TryGetProperty("type", out _))
            {
                // Focused UI-node snippet: supply referenced demo state/collections and mount as view
                writer.WritePropertyName("state");
                writer.WriteStartObject();
                writer.WriteString("draft", "");
                writer.WriteString("targetFormat", "webp");
                writer.WriteEndObject();

                writer.WritePropertyName("collections");
                writer.WriteStartObject();
                foreach (string col in new[] { "items", "history", "checkins", "tasks" })
                {
                    writer.WritePropertyName(col);
                    writer.WriteStartObject();
                    writer.WriteNumber("maxItems", 50);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();

                writer.WritePropertyName("view");
                root.WriteTo(writer);
            }
            else
            {
                bool hasView = false;
                foreach (JsonProperty prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("schemaVersion") || prop.NameEquals("id") || prop.NameEquals("name") || prop.NameEquals("icon"))
                    {
                        continue;
                    }

                    if (prop.NameEquals("view"))
                    {
                        hasView = true;
                    }

                    prop.WriteTo(writer);
                }

                if (!hasView)
                {
                    writer.WritePropertyName("view");
                    writer.WriteStartObject();
                    writer.WriteString("type", "Text");
                    writer.WriteString("text", "${$computed.summary}");
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<JsonDocument> ReadAllFramedJsonResponses(Stream stream)
    {
        var list = new List<JsonDocument>();
        byte[] all = ((MemoryStream)stream).ToArray();
        int idx = 0;
        while (idx < all.Length)
        {
            int headerEnd = FindCrLfCrLf(all, idx);
            Assert.True(headerEnd > idx, "Missing CRLFCRLF in MCP stdio output.");
            string header = Encoding.ASCII.GetString(all, idx, headerEnd - idx);
            Assert.StartsWith("Content-Length:", header, StringComparison.OrdinalIgnoreCase);
            int len = int.Parse(header["Content-Length:".Length..].Trim());
            int bodyStart = headerEnd + 4;
            Assert.True(bodyStart + len <= all.Length);
            string json = Encoding.UTF8.GetString(all, bodyStart, len);
            list.Add(JsonDocument.Parse(json));
            idx = bodyStart + len;
        }

        return list;
    }

    private static int FindCrLfCrLf(byte[] buffer, int start)
    {
        for (int i = start; i <= buffer.Length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
