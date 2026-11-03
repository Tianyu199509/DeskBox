using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeskBox.Tests;

public sealed class CustomWidgetCapabilityEcosystemTests
{
    [Fact]
    public async Task CapabilityRegistry_AllCapsules_HaveValidSnippets_DependencyLinks_MustUseSelection_And_WorkspaceDocs()
    {
        IReadOnlyList<CustomWidgetCapabilitySpec> all = CustomWidgetCapabilityRegistry.All;
        Assert.True(all.Count >= 19, $"Expected at least 19 capability capsules, got {all.Count}.");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetCapabilitySpec spec in all)
        {
            Assert.True(seenIds.Add(spec.Id), $"Duplicate capability ID '{spec.Id}'.");
            Assert.False(string.IsNullOrWhiteSpace(spec.TitleZh));
            Assert.False(string.IsNullOrWhiteSpace(spec.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(spec.CompactContractMarkdown));
            Assert.NotEmpty(spec.Members);

            using JsonDocument snippetDoc = JsonDocument.Parse(
                spec.GoldenSnippetJson,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Assert.Equal(JsonValueKind.Object, snippetDoc.RootElement.ValueKind);
        }

        // Dependency linking: secret-vault -> http-and-rss-feeds, calendar-grid -> calendar, control-tile -> device-state + system-control
        IReadOnlyList<CustomWidgetCapabilitySpec> authExpanded =
            CustomWidgetCapabilityRegistry.ExpandWithDependencies(["secret-vault"]);
        Assert.Contains(authExpanded, c => c.Id == "secret-vault");
        Assert.Contains(authExpanded, c => c.Id == "http-and-rss-feeds");

        IReadOnlyList<CustomWidgetCapabilitySpec> calExpanded =
            CustomWidgetCapabilityRegistry.ExpandWithDependencies(["calendar-grid"]);
        Assert.Contains(calExpanded, c => c.Id == "calendar-grid");
        Assert.Contains(calExpanded, c => c.Id == "calendar");

        // Inline @mention extraction + Must-Use pinning
        IReadOnlyList<string> mentions = CustomWidgetCapabilityRegistry.ExtractMentionedCapabilityIds(
            "帮我用 @weather 和 @calendar-grid 做一个桌面日历天气卡，再加 @system-control");
        Assert.Contains("weather", mentions);
        Assert.Contains("calendar-grid", mentions);
        Assert.Contains("system-control", mentions);

        var selection = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: "做一个天气日历格子",
            pinnedCapabilityIds: ["file-transform", "secret-vault"]);
        Assert.Contains(selection.PinnedSpecs, c => c.Id == "file-transform");
        Assert.Contains(selection.PinnedSpecs, c => c.Id == "drop-zone");
        Assert.Contains(selection.PinnedSpecs, c => c.Id == "http-and-rss-feeds");
        Assert.Contains(selection.PinnedSpecs, c => c.Id == "secret-vault");

        // AST backfill during auto-repair when model attempted a capability it didn't get in Tier 1
        IReadOnlyList<CustomWidgetCapabilitySpec> backfilled =
            CustomWidgetCapabilityRegistry.FindBackfillCapabilitiesForRepair(
                candidateManifestJson: """{ "type": "Button", "onClick": [{ "type": "system.control", "target": "volume.muteToggle" }], "text": "${$weather.temp}" }""",
                diagnostics: null);
        Assert.Contains(backfilled, c => c.Id == "system-control");
        Assert.Contains(backfilled, c => c.Id == "weather");

        // Workspace docs export writes API_REFERENCE.md alongside SKILL.md and manifest.schema.json
        string tempWorkspace = Path.Combine(Path.GetTempPath(), $"deskbox-cap-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(tempWorkspace);
            string apiRefPath = Path.Combine(tempWorkspace, "API_REFERENCE.md");
            Assert.True(File.Exists(apiRefPath));
            string apiRefText = await File.ReadAllTextAsync(apiRefPath, Encoding.UTF8);
            Assert.Contains("@secret-vault", apiRefText, StringComparison.Ordinal);
            Assert.Contains("@calendar-grid", apiRefText, StringComparison.Ordinal);
            Assert.Contains("@file-transform", apiRefText, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempWorkspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TokenValidator_FlagsHallucinatedTokensWithDidYouMean_And_AcceptsDocumentedAliases()
    {
        const string badTokensManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.bad-tokens",
          "name": "Bad Tokens",
          "sources": [
            { "type": "weather", "intervalMs": 900000 },
            { "type": "systemMetrics", "intervalMs": 2000 }
          ],
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$weather.barometerHpa}" },
              { "type": "Text", "text": "${$sys.cpuTempCelsius}" }
            ]
          }
        }
        """;

        CustomWidgetParseResult badRes = CustomWidgetManifestParser.ValidateWithRuntimeHints(badTokensManifest);
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("$weather.barometerHpa", StringComparison.Ordinal));
        Assert.Contains(badRes.Diagnostics, d => d.Message.Contains("$sys.cpuTempCelsius", StringComparison.Ordinal));

        const string aliasManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.good-aliases",
          "name": "Good Aliases",
          "sources": [
            { "type": "weather", "intervalMs": 900000 },
            { "type": "systemMetrics", "intervalMs": 2000 },
            { "type": "pomodoro", "intervalMs": 1000 },
            { "type": "countdown", "intervalMs": 60000 },
            { "type": "aiUsage", "intervalMs": 60000 }
          ],
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$sys.ram.percent}% · ${$weather.temperature}°C" },
              { "type": "Text", "text": "${$pomo.remaining} · ${$countdown.daysLeft} · ${$ai.tokens}" }
            ]
          }
        }
        """;

        CustomWidgetParseResult aliasRes = CustomWidgetManifestParser.ValidateWithRuntimeHints(aliasManifest);
        Assert.NotNull(aliasRes.Manifest);
        Assert.Empty(aliasRes.Diagnostics);
    }

    [Fact]
    public void ManifestAutoFixer_WiresMissingNativeSources_Collections_And_StateKeys()
    {
        const string unWiredManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.unwired-all-in-one",
          "name": "Unwired Hub",
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              { "type": "Text", "text": "${$weather.city} ${$weather.tempText} · ${state.activeTab}" },
              { "type": "CalendarGrid", "eventsCollection": "birthdays", "dateField": "date", "bind": "selectedDate" },
              {
                "type": "ControlTile",
                "title": "静音",
                "icon": "Volume3",
                "active": "${$device.muted}",
                "onClick": [{ "type": "system.control", "target": "volume.muteToggle" }]
              }
            ]
          }
        }
        """;

        string fixedJson = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(unWiredManifest);
        CustomWidgetParseResult validated = CustomWidgetManifestParser.ValidateWithRuntimeHints(fixedJson);
        Assert.NotNull(validated.Manifest);
        Assert.Empty(validated.Diagnostics);

        CustomWidgetManifest m = validated.Manifest!;
        Assert.Contains(m.Sources, s => s.Type == "weather");
        Assert.Contains(m.Sources, s => s.Type == "calendar");
        Assert.Contains(m.Sources, s => s.Type == "deviceState");
        Assert.True(m.Collections.ContainsKey("birthdays"));
        Assert.True(m.State.ContainsKey("activeTab"));
        Assert.True(m.State.ContainsKey("selectedDate"));
    }

    [Fact]
    public async Task SecretVault_DpapiEncryption_SameOriginAuthHeaders_And_CrossOriginRedirectStrip_WorkSecurely()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store1 = new CustomWidgetSecretStore(tempDir);
            const string rawSecret = "sk-live-super-secret-987654321";
            store1.SetSecret("widget-alpha", "apiKey", rawSecret);

            Assert.True(File.Exists(store1.FilePath));
            string rawDiskJson = await File.ReadAllTextAsync(store1.FilePath, Encoding.UTF8);
            Assert.DoesNotContain(rawSecret, rawDiskJson, StringComparison.Ordinal);
            using (JsonDocument vaultDoc = JsonDocument.Parse(rawDiskJson))
            {
                string cipherBase64 = vaultDoc.RootElement
                    .GetProperty("widgets")
                    .GetProperty("widget-alpha")
                    .GetProperty("apiKey")
                    .GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(cipherBase64));
                Assert.NotEqual(rawSecret, cipherBase64);
                Assert.True(Convert.FromBase64String(cipherBase64).Length > 16);
            }

            // Reload in fresh instance
            var store2 = new CustomWidgetSecretStore(tempDir);
            Assert.Equal(rawSecret, store2.GetSecret("widget-alpha", "apiKey"));
            Assert.Contains("••••", CustomWidgetSecretStore.MaskSecret(rawSecret), StringComparison.Ordinal);

            // Same-origin header injection + cross-origin redirect header stripping
            var capturedHeadersByHost = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var engine = new CustomWidgetNetworkSourceEngine(
                cacheDirectory: Path.Combine(tempDir, "net-cache"),
                httpMessageHandler: new StubHttpHandler((req, _) =>
                {
                    string host = req.RequestUri!.Host;
                    capturedHeadersByHost[host] = req.Headers.Authorization?.ToString();

                    if (string.Equals(host, "api.origin.example", StringComparison.OrdinalIgnoreCase))
                    {
                        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                        redirect.Headers.Location = new Uri("https://cdn.other-domain.example/feed.json");
                        return Task.FromResult(redirect);
                    }

                    const string jsonPayload = """{ "items": [ { "title": "Quota OK" } ] }""";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                    });
                }),
                dnsResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            var parseRes = CustomWidgetManifestParser.Parse("""
            {
              "schemaVersion": 1,
              "id": "custom.auth-test",
              "name": "Auth Test",
              "settings": [
                { "key": "apiKey", "type": "secret", "label": "API Key" }
              ],
              "collections": { "rows": { "maxItems": 10 } },
              "sources": [
                {
                  "type": "httpJson",
                  "url": "https://api.origin.example/v1/usage",
                  "headers": {
                    "Authorization": "Bearer ${$secrets.apiKey}"
                  },
                  "targetCollection": "rows",
                  "itemsPath": "items",
                  "fieldMap": { "title": "title" }
                }
              ],
              "view": { "type": "Text", "text": "OK" }
            }
            """);
            Assert.NotNull(parseRes.Manifest);

            var evalCtx = new CustomWidgetEvaluationContext
            {
                Secrets = store2.GetSecretsForWidget("widget-alpha")
            };

            CustomWidgetSourceSpec sourceSpec = parseRes.Manifest!.Sources[0];
            var resolvedHeaders = sourceSpec.Headers!.ToDictionary(
                static kv => kv.Key,
                kv => CustomWidgetExpressionEvaluator.Interpolate(kv.Value, evalCtx),
                StringComparer.OrdinalIgnoreCase);

            CustomWidgetNetworkFetchOutcome fetchRes = await engine.FetchAsync(
                sourceSpec,
                sourceSpec.Url!,
                resolvedHeaders: resolvedHeaders);

            Assert.True(fetchRes.Succeeded, fetchRes.ErrorMessage);
            Assert.Single(fetchRes.Items);
            Assert.Equal($"Bearer {rawSecret}", capturedHeadersByHost["api.origin.example"]);
            Assert.Null(capturedHeadersByHost["cdn.other-domain.example"]);

            store2.RemoveAllForWidget("widget-alpha");
            Assert.Equal(string.Empty, store2.GetSecret("widget-alpha", "apiKey"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreFlightProbe_And_FacadeDetector_SelfHealBrokenHttpJsonPathsDuringStudioTurn()
    {
        // 1. Dead-control & facade mutation audit
        CustomWidgetManifest facadeManifest = CustomWidgetManifestParser.Parse("""
        {
          "schemaVersion": 1,
          "id": "custom.facade",
          "name": "Facade Check",
          "state": { "fakeWifi": "off" },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Button", "label": "Dead Button" },
              {
                "type": "Button",
                "label": "Fake Toggle",
                "onClick": [{ "type": "state.set", "key": "fakeWifi", "value": "on" }]
              }
            ]
          }
        }
        """).Manifest!;

        IReadOnlyList<CustomWidgetHealthIssue> facadeIssues =
            CustomWidgetHealthAnalyzer.DetectDeadControlsAndUnreadMutations(facadeManifest, cultureName: "en-US");
        Assert.Contains(facadeIssues, i => i.Code == "interaction.dead_button");
        Assert.Contains(facadeIssues, i => i.Code == "interaction.unread_state");

        // 2. Live Pre-Flight HTTP JSON skeleton extraction + 1-round AI auto-repair
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var netEngine = new CustomWidgetNetworkSourceEngine(
                cacheDirectory: Path.Combine(tempDir, "probe-cache"),
                httpMessageHandler: new StubHttpHandler((_, _) =>
                {
                    const string liveApiJson = """
                    {
                      "code": 0,
                      "payload": {
                        "hotList": [
                          { "headline": "DeskBox 2.0 Released", "scoreText": "998w", "linkUrl": "https://example.com/news/1" },
                          { "headline": "WinUI 3 NativeAOT", "scoreText": "650w", "linkUrl": "https://example.com/news/2" }
                        ]
                      }
                    }
                    """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(liveApiJson, Encoding.UTF8, "application/json")
                    });
                }),
                dnsResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-preflight",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));

            int aiCallCount = 0;
            string capturedSecondPrompt = string.Empty;
            using var aiHttp = new HttpClient(new StubHttpHandler(async (req, ct) =>
            {
                aiCallCount++;
                string reqBody = await req.Content!.ReadAsStringAsync(ct);
                if (aiCallCount == 2)
                {
                    capturedSecondPrompt = reqBody;
                }

                // Call 1 guesses wrong itemsPath ("data.items") and wrong field ("title");
                // Call 2 repairs using the live JSON skeleton ("payload.hotList" + "headline")!
                string replyJson = aiCallCount == 1
                    ? """
                      {
                        "kind": "edit",
                        "reply": "初版热榜已生成",
                        "changeSummary": "创建热榜",
                        "manifest": {
                          "schemaVersion": 1,
                          "id": "custom.live-hotlist",
                          "name": "实时热榜",
                          "collections": { "items": { "maxItems": 20 } },
                          "sources": [
                            {
                              "type": "httpJson",
                              "url": "https://news.example.com/api/hot",
                              "targetCollection": "items",
                              "itemsPath": "data.items",
                              "fieldMap": { "title": "title", "url": "url" }
                            }
                          ],
                          "view": {
                            "type": "List",
                            "items": "items",
                            "itemTemplate": { "type": "Text", "text": "${$item.title}" }
                          }
                        }
                      }
                      """
                    : """
                      {
                        "schemaVersion": 1,
                        "id": "custom.live-hotlist",
                        "name": "实时热榜",
                        "collections": { "items": { "maxItems": 20 } },
                        "sources": [
                          {
                            "type": "httpJson",
                            "url": "https://news.example.com/api/hot",
                            "targetCollection": "items",
                            "itemsPath": "payload.hotList",
                            "fieldMap": { "title": "headline", "hot": "scoreText", "url": "linkUrl" }
                          }
                        ],
                        "view": {
                          "type": "List",
                          "items": "items",
                          "itemTemplate": { "type": "Text", "text": "${$item.title}" }
                        }
                      }
                      """;

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("choices");
                    writer.WriteStartObject();
                    writer.WriteStartObject("message");
                    writer.WriteString("content", replyJson);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, aiHttp);
            var generator = new CustomWidgetAiGeneratorService(aiService, netEngine);

            StudioTurnResult turn = await generator.RunStudioTurnAsync(
                providerId: "deepseek",
                userMessage: "帮我做一个实时热榜格子",
                uiLanguage: "zh-CN",
                pinnedCapabilityIds: ["http-and-rss-feeds"]);

            Assert.True(turn.Success, turn.ErrorMessage);
            Assert.Equal(2, aiCallCount);
            Assert.Contains("hotList", capturedSecondPrompt, StringComparison.Ordinal);
            Assert.Contains("headline", capturedSecondPrompt, StringComparison.Ordinal);
            Assert.Equal("payload.hotList", turn.ParseResult!.Manifest!.Sources[0].ItemsPath);
            Assert.Equal("headline", turn.ParseResult.Manifest.Sources[0].FieldMap!["title"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WeatherSnapshot_CalendarLunarEngine_And_FileTransformService_WorkEndToEnd()
    {
        // 1. Weather snapshot token evaluation
        var weatherSnap = new CustomWidgetWeatherSnapshot
        {
            HasData = true,
            City = "深圳",
            Temperature = 26.4,
            ApparentTemperature = 28.1,
            HighTemperature = 29.0,
            LowTemperature = 22.0,
            Humidity = 68,
            WindSpeedKmh = 14.2,
            PrecipitationProbability = 20,
            UvIndex = 6,
            WeatherCode = 1,
            IsDay = true,
            Status = "ok"
        };
        Assert.Equal("26°C", weatherSnap.ResolveToken("tempText", "zh-CN"));
        Assert.Equal("22° ~ 29°", weatherSnap.ResolveToken("rangeText", "zh-CN"));
        Assert.Equal("68%", weatherSnap.ResolveToken("humidity", "zh-CN"));
        Assert.False(string.IsNullOrWhiteSpace(weatherSnap.ResolveToken("condition", "zh-CN")));

        // 2. Calendar + Lunar + Birthday matching
        var events = new List<CustomWidgetRecord>
        {
            new() { Id = "1", Fields = new Dictionary<string, string> { ["title"] = "妈妈生日", ["date"] = "05-12" } },
            new() { Id = "2", Fields = new Dictionary<string, string> { ["title"] = "项目发版", ["date"] = "2026-05-20" } }
        };
        CustomWidgetCalendarMonthView monthView = CustomWidgetCalendarEngine.BuildMonthView(
            visibleMonth: new DateOnly(2026, 5, 1),
            today: new DateOnly(2026, 5, 12),
            selectedDate: new DateOnly(2026, 5, 20),
            cultureName: "zh-CN",
            eventsCollection: events,
            dateField: "date");

        Assert.Equal(42, monthView.Cells.Count);
        CustomWidgetCalendarCell may12 = monthView.Cells.Single(c => c.Date == new DateOnly(2026, 5, 12));
        Assert.True(may12.IsToday);
        Assert.Equal(1, may12.EventCount);
        Assert.Equal("妈妈生日", may12.FirstEventTitle);
        Assert.False(string.IsNullOrWhiteSpace(may12.SubtitleText));

        CustomWidgetCalendarCell may20 = monthView.Cells.Single(c => c.Date == new DateOnly(2026, 5, 20));
        Assert.True(may20.IsSelected);
        Assert.Equal(1, may20.EventCount);

        // 3. Local File Transform (CSV <-> JSON, Markdown -> HTML, SHA-256, PNG -> ICO / JPG / WebP)
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-transform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string csvPath = Path.Combine(tempDir, "users.csv");
            await File.WriteAllTextAsync(csvPath, "name,role\r\nAlice,Engineer\r\nBob,Designer\r\n", Encoding.UTF8);

            CustomWidgetTransformResult csvToJson = await CustomWidgetFileTransformService.TransformFileAsync(csvPath, "csvToJson");
            Assert.True(csvToJson.Succeeded, csvToJson.ErrorMessage);
            Assert.True(File.Exists(csvToJson.OutputPath));
            using (JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(csvToJson.OutputPath, Encoding.UTF8)))
            {
                Assert.Equal(2, doc.RootElement.GetArrayLength());
                Assert.Equal("Alice", doc.RootElement[0].GetProperty("name").GetString());
            }

            CustomWidgetTransformResult jsonToCsv = await CustomWidgetFileTransformService.TransformFileAsync(csvToJson.OutputPath, "jsonToCsv");
            Assert.True(jsonToCsv.Succeeded, jsonToCsv.ErrorMessage);
            string roundTripCsv = await File.ReadAllTextAsync(jsonToCsv.OutputPath, Encoding.UTF8);
            Assert.Contains("Alice,Engineer", roundTripCsv, StringComparison.Ordinal);

            string mdPath = Path.Combine(tempDir, "note.md");
            await File.WriteAllTextAsync(mdPath, "# DeskBox\n\n- **Fast** NativeAOT\n", Encoding.UTF8);
            CustomWidgetTransformResult mdToHtml = await CustomWidgetFileTransformService.TransformFileAsync(mdPath, "mdToHtml");
            Assert.True(mdToHtml.Succeeded, mdToHtml.ErrorMessage);
            string html = await File.ReadAllTextAsync(mdToHtml.OutputPath, Encoding.UTF8);
            Assert.Contains("<h1>DeskBox</h1>", html, StringComparison.Ordinal);
            Assert.Contains("<strong>Fast</strong>", html, StringComparison.Ordinal);

            CustomWidgetTransformResult shaRes = await CustomWidgetFileTransformService.TransformFileAsync(mdPath, "sha256");
            Assert.True(shaRes.Succeeded, shaRes.ErrorMessage);
            Assert.Equal(64, shaRes.ResultText.Length);

            // Generate a real 16x16 RGBA PNG via WinRT BitmapEncoder and convert to ICO + JPG
            string pngPath = Path.Combine(tempDir, "sample.png");
            await CreateSamplePngAsync(pngPath, 32, 32);

            CustomWidgetTransformResult icoRes = await CustomWidgetFileTransformService.TransformFileAsync(pngPath, "ico");
            Assert.True(icoRes.Succeeded, icoRes.ErrorMessage);
            byte[] icoBytes = await File.ReadAllBytesAsync(icoRes.OutputPath);
            Assert.True(icoBytes.Length > 22);
            Assert.Equal(0, icoBytes[0]);
            Assert.Equal(0, icoBytes[1]);
            Assert.Equal(1, icoBytes[2]); // ICO type = 1
            Assert.Equal(0, icoBytes[3]);

            CustomWidgetTransformResult jpgRes = await CustomWidgetFileTransformService.TransformFileAsync(pngPath, "jpg", maxWidth: 16);
            Assert.True(jpgRes.Succeeded, jpgRes.ErrorMessage);
            Assert.True(File.Exists(jpgRes.OutputPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task McpServer_GetCapabilitySpec_SupportsCapabilityId_Query_And_FullReference()
    {
        const string rpcLines = """
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deskbox_get_capability_spec","arguments":{"capabilityId":"secret-vault"}}}
        {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"deskbox_get_capability_spec","arguments":{"query":"lunar"}}}
        {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deskbox_get_capability_spec","arguments":{"includeFullReference":true}}}
        """;

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(rpcLines));
        using var output = new MemoryStream();
        int code = await DeskBoxAgentCliEntry.RunMcpStdioLoopAsync(input, output, pipeNameOverride: "deskbox-nonexistent-pipe");
        Assert.Equal(0, code);

        string[] responses = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, responses.Length);

        using JsonDocument r1 = JsonDocument.Parse(responses[0]);
        string text1 = r1.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("http-and-rss-feeds", text1, StringComparison.Ordinal);
        Assert.Contains("secret-vault", text1, StringComparison.Ordinal);

        using JsonDocument r2 = JsonDocument.Parse(responses[1]);
        string text2 = r2.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("calendar", text2, StringComparison.Ordinal);

        using JsonDocument r3 = JsonDocument.Parse(responses[2]);
        string text3 = r3.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("# DeskBox Custom Widget Capability & API Reference", text3, StringComparison.Ordinal);
    }

    [Fact]
    public void V160Capabilities_ExpressionFunctions_DeviceTokens_TextTransform_And_QuickLauncher_WorkEndToEnd()
    {
        var fixedUtc = new DateTimeOffset(2026, 11, 3, 6, 30, 45, TimeSpan.Zero);
        var deviceSnap = new CustomWidgetDeviceStateSnapshot
        {
            VolumePercent = 64,
            IsMuted = false,
            BrightnessPercent = 82,
            BrightnessSupported = true,
            AudioDeviceName = "Realtek Speakers",
            MediaPlaying = true,
            MediaTitle = "Clair de Lune",
            MediaArtist = "Debussy",
            MediaCoverPath = @"C:\Temp\cover.png"
        };
        var clipSnap = CustomWidgetClipboardSnapshot.CreateFromText("{\"hello\":\"deskbox\"}\n");
        Assert.Equal("json", clipSnap.Kind);
        Assert.Equal("JSON", clipSnap.ResolveToken("kindLabel", "zh-CN"));

        var evalCtx = new CustomWidgetEvaluationContext
        {
            ClockOverride = fixedUtc,
            CultureName = "zh-CN",
            DeviceState = deviceSnap,
            Clipboard = clipSnap
        }.WithEventValue("75");

        // Event payload $value + tz() multi-timezone formatting + $clock.* + $device.* + $clipboard.*
        Assert.Equal("75", CustomWidgetExpressionEvaluator.Interpolate("${$value}", evalCtx));
        Assert.Equal("06:30", CustomWidgetExpressionEvaluator.Interpolate("${tz('UTC', 'HH:mm')}", evalCtx));
        Assert.Equal("14:30", CustomWidgetExpressionEvaluator.Interpolate("${tz('Asia/Shanghai', 'HH:mm')}", evalCtx));
        Assert.Equal("01:30", CustomWidgetExpressionEvaluator.Interpolate("${tz('UTC-5', 'HH:mm')}", evalCtx));
        Assert.Equal("06:30", CustomWidgetExpressionEvaluator.Interpolate("${$clock.utcTime}", evalCtx));
        Assert.Equal("82%", CustomWidgetExpressionEvaluator.Interpolate("${$device.brightnessText}", evalCtx));
        Assert.Equal("Realtek Speakers", CustomWidgetExpressionEvaluator.Interpolate("${$device.audioDeviceName}", evalCtx));
        Assert.Equal("播放中", CustomWidgetExpressionEvaluator.Interpolate("${$device.mediaStatus}", evalCtx));
        Assert.Equal("\uE769", CustomWidgetExpressionEvaluator.Interpolate("${$device.mediaToggleIcon}", evalCtx));
        Assert.Equal(@"C:\Temp\cover.png", CustomWidgetExpressionEvaluator.Interpolate("${$device.mediaCover}", evalCtx));
        Assert.Equal("json", CustomWidgetExpressionEvaluator.Interpolate("${$clipboard.kind}", evalCtx));

        // Paused media switches glyph to Play (\uE768)
        var pausedSnap = deviceSnap with { MediaPlaying = false };
        Assert.Equal("\uE768", pausedSnap.ResolveToken("mediaToggleIcon", "zh-CN"));
        Assert.Equal("已暂停", pausedSnap.ResolveToken("mediaStatus", "zh-CN"));

        // Deterministic pure in-memory text.transform operations
        CustomWidgetTransformResult pretty = CustomWidgetUtilityEngine.ExecuteTextTransform("{\"a\":1,\"b\":2}", "jsonPretty");
        Assert.True(pretty.Succeeded);
        Assert.Contains("\n", pretty.ResultText, StringComparison.Ordinal);

        CustomWidgetTransformResult b64Enc = CustomWidgetUtilityEngine.ExecuteTextTransform("DeskBox 1.6", "base64Encode");
        Assert.True(b64Enc.Succeeded);
        CustomWidgetTransformResult b64Dec = CustomWidgetUtilityEngine.ExecuteTextTransform(b64Enc.ResultText, "base64Decode");
        Assert.Equal("DeskBox 1.6", b64Dec.ResultText);

        CustomWidgetTransformResult urlEnc = CustomWidgetUtilityEngine.ExecuteTextTransform("a b+c", "urlEncode");
        Assert.Equal("a%20b%2Bc", urlEnc.ResultText);
        Assert.Equal("a b+c", CustomWidgetUtilityEngine.ExecuteTextTransform(urlEnc.ResultText, "urlDecode").ResultText);

        CustomWidgetTransformResult tsRes = CustomWidgetUtilityEngine.ExecuteTextTransform("1700000000", "timestampToDate");
        Assert.True(tsRes.Succeeded);
        Assert.Contains("2023-11-", tsRes.ResultText, StringComparison.Ordinal);

        CustomWidgetTransformResult wcRes = CustomWidgetUtilityEngine.ExecuteTextTransform("你好 DeskBox world", "wordCount");
        Assert.True(wcRes.Succeeded);
        Assert.Contains("4 words", wcRes.ResultText, StringComparison.Ordinal);

        CustomWidgetTransformResult shaRes = CustomWidgetUtilityEngine.ExecuteTextTransform("deskbox", "sha256");
        Assert.True(shaRes.Succeeded);
        Assert.Equal(64, shaRes.ResultText.Length);

        // Storage drives snapshot
        CustomWidgetStorageDrivesSnapshot drivesSnap = CustomWidgetStorageDrivesSnapshot.Capture();
        Assert.True(drivesSnap.Count >= 1);
        Assert.NotEmpty(drivesSnap.Records);

        // Quick launcher whitelist & blocklist
        Assert.True(CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget("ms-screenclip:", null, out QuickLaunchTarget clipUri));
        Assert.True(clipUri.IsUri);
        Assert.False(clipUri.RequiresCommunityConsent);

        Assert.True(CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget("vscode://file/d:/project", null, out QuickLaunchTarget vscUri));
        Assert.True(vscUri.IsUri);
        Assert.True(vscUri.RequiresCommunityConsent);

        Assert.False(CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget("javascript:alert(1)", null, out _));
        Assert.False(CustomWidgetUtilityEngine.TryResolveQuickLaunchTarget("powershell://-enc", null, out _));
    }

    [Fact]
    public void V160Nodes_AutoFixer_CompatibilityVersion_DuplicateHeaderAudit_And_FormalDocs_WorkCleanly()
    {
        // 1. Formal 29-module registry & zero colloquial prose in generated API Reference
        Assert.Equal(35, CustomWidgetCapabilityRegistry.All.Count);
        string zhRef = CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(isChinese: true);
        Assert.Contains("## 架构与布局规范", zhRef, StringComparison.Ordinal);
        Assert.Contains("@script-sandbox", zhRef, StringComparison.Ordinal);
        Assert.Contains("@visual-containers", zhRef, StringComparison.Ordinal);
        Assert.Contains("@display-and-audio", zhRef, StringComparison.Ordinal);
        Assert.Contains("@storage-drives", zhRef, StringComparison.Ordinal);
        Assert.Contains("@clipboard-workspace", zhRef, StringComparison.Ordinal);
        Assert.Contains("@quick-launcher", zhRef, StringComparison.Ordinal);
        Assert.Contains("@world-clock", zhRef, StringComparison.Ordinal);
        Assert.DoesNotContain("积木", zhRef, StringComparison.Ordinal);
        Assert.DoesNotContain("套娃", zhRef, StringComparison.Ordinal);
        Assert.DoesNotContain("咖啡因", zhRef, StringComparison.Ordinal);
        Assert.DoesNotContain("绿格子", zhRef, StringComparison.Ordinal);

        // 2. Unwired v1.6 manifest using Tabs, Image, RingGauge, BarList, two-way Slider ($value), tz(), text.transform, launcher.open
        const string unwiredV16 = """
        {
          "schemaVersion": 1,
          "id": "custom.command-deck",
          "name": "控制面板",
          "icon": "🎛️",
          "view": {
            "type": "Tabs",
            "bind": "state.tab",
            "children": [
              {
                "title": "设备",
                "value": "dev",
                "children": [
                  {
                    "type": "Slider",
                    "title": "主音量",
                    "value": "${$device.volume}",
                    "min": 0,
                    "max": 100,
                    "onChange": { "type": "system.control", "targetId": "volume.set", "value": "${$value}" }
                  },
                  {
                    "type": "Image",
                    "src": "${$device.mediaCover}",
                    "width": 40,
                    "height": 40,
                    "stretch": "uniformToFill"
                  }
                ]
              },
              {
                "title": "存储与工具",
                "value": "storage",
                "children": [
                  { "type": "RingGauge", "progress": "${$drives.primaryPercent}", "title": "主盘", "unit": "%" },
                  { "type": "BarList", "collection": "drives", "titleField": "title", "valueField": "percent", "unit": "%" },
                  { "type": "Text", "text": "${tz('UTC', 'HH:mm')} · ${$clipboard.kindLabel}" },
                  { "type": "Button", "text": "JSON 格式化", "onClick": { "type": "text.transform", "format": "jsonPretty", "key": "outJson" } },
                  { "type": "Button", "text": "截图", "onClick": { "type": "launcher.open", "targetId": "ms-screenclip:" } }
                ]
              }
            ]
          }
        }
        """;

        string autoWired = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(unwiredV16);
        CustomWidgetParseResult validated = CustomWidgetManifestParser.ValidateWithRuntimeHints(autoWired);
        Assert.NotNull(validated.Manifest);
        Assert.Empty(validated.Diagnostics);

        CustomWidgetManifest m = validated.Manifest!;
        Assert.Contains(m.Sources, s => s.Type == "deviceState");
        Assert.Contains(m.Sources, s => s.Type == "storageDrives");
        Assert.Contains(m.Sources, s => s.Type == "clipboard");
        Assert.Contains(m.Sources, s => s.Type == "clock");
        Assert.True(m.Collections.ContainsKey("drives"));
        Assert.True(m.State.ContainsKey("tab"));
        Assert.True(m.State.ContainsKey("outJson"));
        Assert.True(CustomWidgetManifestCompatibility.UsesV160Features(m));
        Assert.True(CustomWidgetManifestCompatibility.UsesV160FeaturesJson(autoWired));

        // Clean Tabs root has zero duplicate-header warnings
        CustomWidgetHealthReport cleanHealth = CustomWidgetHealthAnalyzer.AnalyzeHealth(autoWired, m, null, cultureName: "zh-CN");
        Assert.DoesNotContain(cleanHealth.Issues, i => i.Code == "layout.duplicate_header");

        // 3. Duplicate inner widget-title header detection
        const string duplicateHeaderJson = """
        {
          "schemaVersion": 1,
          "id": "custom.dup-header",
          "name": "控制中心",
          "icon": "🎛️",
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Grid",
                "columns": "*,Auto",
                "children": [
                  { "type": "Text", "column": 0, "text": "🎛️ 控制中心", "style": "subtitle" },
                  { "type": "IconButton", "column": 1, "icon": "\uE72C", "onClick": { "type": "source.refresh" } }
                ]
              },
              { "type": "Text", "text": "正文区域" }
            ]
          }
        }
        """;
        CustomWidgetManifest dupManifest = CustomWidgetManifestParser.Parse(duplicateHeaderJson).Manifest!;
        CustomWidgetHealthReport dupHealth = CustomWidgetHealthAnalyzer.AnalyzeHealth(duplicateHeaderJson, dupManifest, null, cultureName: "zh-CN");
        Assert.Contains(dupHealth.Issues, i => i.Code == "layout.duplicate_header");

        // 4. Safe root viewport inset & layout alias normalization ("direction", "Row", "col", "colSpan")
        Microsoft.UI.Xaml.Thickness defaultSafe = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ResolveRootSafePadding(null);
        Assert.Equal(new Microsoft.UI.Xaml.Thickness(12, 10, 12, 12), defaultSafe);
        Assert.Equal(
            new Microsoft.UI.Xaml.Thickness(12, 10, 12, 12),
            DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ResolveRootSafePadding("0"));
        Assert.Equal(
            new Microsoft.UI.Xaml.Thickness(10, 6, 10, 10),
            DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ResolveRootSafePadding("10,6,10,10"));
        Assert.Equal(
            new Microsoft.UI.Xaml.Thickness(10, 6, 10, 10),
            DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ResolveRootSafePadding("4"));

        const string unpaddedAndAliasedJson = """
        {
          "schemaVersion": 1,
          "id": "custom.inbox-layout",
          "name": "灵感收集匣",
          "view": {
            "type": "Stack",
            "direction": "vertical",
            "spacing": 0,
            "children": [
              {
                "type": "Row",
                "spacing": 8,
                "children": [
                  { "type": "Text", "text": "标题" },
                  { "type": "Badge", "text": "灵感" }
                ]
              },
              {
                "type": "Grid",
                "columns": "*,Auto",
                "children": [
                  { "type": "Input", "placeholder": "速记" },
                  { "type": "IconButton", "col": 1, "colSpan": 2, "icon": "\uE710" }
                ]
              }
            ]
          }
        }
        """;
        string paddedJson = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(unpaddedAndAliasedJson);
        CustomWidgetParseResult aliasedParsed = CustomWidgetManifestParser.ValidateWithRuntimeHints(paddedJson);
        Assert.NotNull(aliasedParsed.Manifest);
        Assert.Empty(aliasedParsed.Diagnostics);
        Assert.Equal("12,10,12,12", aliasedParsed.Manifest!.View.Padding);
        Assert.Equal("Stack", aliasedParsed.Manifest.View.Children[0].Type);
        Assert.Equal("horizontal", aliasedParsed.Manifest.View.Children[0].Orientation);
        Assert.Equal(1, aliasedParsed.Manifest.View.Children[1].Children[1].Column);
        Assert.Equal(2, aliasedParsed.Manifest.View.Children[1].Children[1].ColumnSpan);
    }

    private static async Task CreateSamplePngAsync(string filePath, uint width, uint height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 64;      // B
            pixels[i + 1] = 128; // G
            pixels[i + 2] = 255; // R
            pixels[i + 3] = 255; // A
        }

        using var ras = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        ras.Seek(0);
        await using FileStream fs = File.Create(filePath);
        await ras.AsStreamForRead().CopyToAsync(fs);
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
