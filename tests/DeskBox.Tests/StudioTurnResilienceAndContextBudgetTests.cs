using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Controls.Studio;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using Xunit;

namespace DeskBox.Tests;

public sealed class StudioTurnResilienceAndContextBudgetTests
{
    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DeskBoxTests_" + Guid.NewGuid().ToString("N"));

        public TempDirectoryScope() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int index = Interlocked.Increment(ref _callCount);
            return responder(request, index, cancellationToken);
        }
    }

    private static string BuildToolCallsSse((string Id, string Name, string ArgsJson)[] calls)
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

    private static string[] ExtractToolNames(string requestBodyJson)
    {
        using JsonDocument doc = JsonDocument.Parse(requestBodyJson);
        if (!doc.RootElement.TryGetProperty("tools", out JsonElement toolsEl) ||
            toolsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (JsonElement item in toolsEl.EnumerateArray())
        {
            if (item.TryGetProperty("function", out JsonElement fnEl) &&
                fnEl.TryGetProperty("name", out JsonElement nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                names.Add(nameEl.GetString() ?? string.Empty);
            }
        }

        return [.. names];
    }

    [Theory]
    [InlineData("auto", "disabled", "disabled")]
    [InlineData("enabled", "enabled", "disabled")]
    public async Task InitialCreation_RepairRound_ExposesPatchTools_AndOnlyDowngradesAutoThinking(
        string configuredThinking,
        string? expectedRound0Thinking,
        string expectedRepairThinking)
    {
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "glm",
            ApiKey: "sk-glm-test",
            CustomEndpoint: "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            CustomModel: "glm-5.3",
            ThinkingMode: configuredThinking));

        const string brokenManifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.repair-unlock",
              "name": "Repair Unlock",
              "icon": "🔧",
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["bad"]
                },
                "bad": { "type": "UnknownBadNode", "text": "Hi" }
              }
            }
            """;

        var capturedBodies = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(async (req, callIndex, ct) =>
        {
            string body = await req.Content!.ReadAsStringAsync(ct);
            capturedBodies.Add(body);

            if (callIndex == 1)
            {
                string sse = BuildToolCallsSse(
                [
                    ("call_wb_1", "write_bundle", JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["manifestJson"] = brokenManifestJson
                    })),
                    ("call_fin_1", "finish_turn", """{"kind":"edit","reply":"Created draft.","changeSummary":"Init"}""")
                ]);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            string repairSse = BuildToolCallsSse(
            [
                ("call_em_2", "edit_manifest", """{"upsertElements":{"bad":{"type":"Text","text":"Hi"}}}"""),
                ("call_fin_2", "finish_turn", """{"kind":"edit","reply":"Fixed invalid node.","changeSummary":"Fixed node type"}""")
            ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(repairSse, Encoding.UTF8, "text/event-stream")
            };
        }));

        var aiService = new DeskBoxAiService(credStore, httpClient: httpClient);
        var generator = new CustomWidgetAiGeneratorService(aiService);

        StudioTurnResult result = await generator.RunStudioTurnAsync(
            providerId: "glm",
            userMessage: "Build directly: multi-panel hardware telemetry monitor with CPU/RAM charts and alert thresholds",
            currentManifestJson: null,
            briefMarkdown: string.Empty,
            recentMessages: [],
            healthReport: null,
            targetNodePath: null,
            preferredEmoji: "🔧",
            allowSampleData: false,
            uiLanguage: "en-US");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, capturedBodies.Count);

        string[] round0Tools = ExtractToolNames(capturedBodies[0]);
        Assert.Equal(7, round0Tools.Length);
        Assert.Contains("write_bundle", round0Tools);
        Assert.Contains("edit_manifest", round0Tools);
        Assert.Contains("patch_script", round0Tools);

        string[] round1Tools = ExtractToolNames(capturedBodies[1]);
        Assert.Equal(round0Tools, round1Tools);

        using JsonDocument round0Doc = JsonDocument.Parse(capturedBodies[0]);
        if (expectedRound0Thinking is null)
        {
            Assert.False(round0Doc.RootElement.TryGetProperty("thinking", out _));
        }
        else
        {
            Assert.Equal(expectedRound0Thinking, round0Doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }

        using JsonDocument round1Doc = JsonDocument.Parse(capturedBodies[1]);
        Assert.Equal(expectedRepairThinking, round1Doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void TurnCancellationAndUpstreamErrors_PreserveHonestDiagnosticsWithoutArtificialTimeout()
    {
        const string upstreamGatewayError = "HTTP 504: Gateway Timeout from upstream provider";
        using (var userCts = new CancellationTokenSource())
        {
            Assert.Equal(
                upstreamGatewayError,
                StudioAiTurnOrchestrator.ResolveTurnErrorMessage(upstreamGatewayError, userCts.Token));

            userCts.Cancel();
            Assert.Equal(
                Localized.T("CustomWidget.AiApi.Error.GenerationCancelled"),
                StudioAiTurnOrchestrator.ResolveTurnErrorMessage(upstreamGatewayError, userCts.Token));
        }
    }

    [Fact]
    public void ManifestAndStateBranchValidator_CatchesNewStructuralBugs_AndPassesAllBuiltinTemplates()
    {
        const string strayRootManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.stray-root",
              "name": "Stray Root",
              "itemTemplate": "hello",
              "valueField": "percent",
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["hello"] },
                "hello": { "type": "Text", "text": "Hello" }
              }
            }
            """;
        // Structural defects are emitted directly as hard errors by the v1 parser/validators.
        CustomWidgetParseResult strayParsed = CustomWidgetManifestParser.Parse(strayRootManifest);
        Assert.False(strayParsed.IsValid);
        Assert.Contains(
            strayParsed.Diagnostics,
            d => d.IsError && string.Equals(d.Code, "StrayViewPropertiesAtManifestRoot", StringComparison.Ordinal));
        Assert.False(CustomWidgetScriptValidator.ValidateWorkspaceBundle(strayRootManifest).IsValid);

        Assert.False(CustomWidgetScriptValidator.ValidateWorkspaceBundle(strayRootManifest, strictAuthoring: true).IsValid);
        CustomWidgetCompilerResult strayCompiled = CustomWidgetCompilerPipeline.Compile(strayRootManifest);
        Assert.Contains(
            strayCompiled.HardDiagnostics,
            d => d.IsError && string.Equals(d.Code, "StrayViewPropertiesAtManifestRoot", StringComparison.Ordinal));
        Assert.Contains(
            CustomWidgetCompilerPipeline.Compile(strayRootManifest, strictAuthoring: false).HardDiagnostics,
            d => d.IsError && string.Equals(d.Code, "StrayViewPropertiesAtManifestRoot", StringComparison.Ordinal));

        const string conflictingCollectionManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.conflict-children",
              "name": "Conflict Children",
              "collections": {
                "drives": { "fields": { "title": "text", "value": "number" } }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["drives_grid"] },
                "drives_grid": {
                  "type": "ItemsGrid",
                  "collection": "drives",
                  "children": ["stale_txt"]
                },
                "stale_txt": { "type": "Text", "text": "Stale static child" }
              }
            }
            """;
        CustomWidgetParseResult conflictParsed = CustomWidgetManifestParser.Parse(conflictingCollectionManifest);
        Assert.Contains(
            conflictParsed.Diagnostics,
            d => d.IsError && string.Equals(d.Code, "ConflictingCollectionAndChildren", StringComparison.Ordinal));
        Assert.Contains(
            CustomWidgetCompilerPipeline.Compile(conflictingCollectionManifest).HardDiagnostics,
            d => d.IsError && string.Equals(d.Code, "ConflictingCollectionAndChildren", StringComparison.Ordinal));

        const string rootWhenManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.root-when",
              "name": "Root When",
              "state": { "ready": "true" },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "when": "${state.ready}",
                  "children": ["hello"]
                },
                "hello": { "type": "Text", "text": "Hello" }
              }
            }
            """;
        CustomWidgetCompilerResult rootWhenCompiled = CustomWidgetCompilerPipeline.Compile(rootWhenManifest);
        Assert.Contains(
            rootWhenCompiled.HardDiagnostics,
            d => d.IsError && string.Equals(d.Code, "RootViewConditionalWhen", StringComparison.Ordinal));

        const string deadBranchManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.dead-branch",
              "name": "Dead Branch",
              "state": { "layoutMode": "grid" },
              "collections": {
                "drives": { "fields": { "title": "text", "value": "number" } }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["seg_mode", "pb_mid", "grid_branch"] },
                "seg_mode": {
                  "type": "Segmented",
                  "bind": "${state.layoutMode}",
                  "options": [
                    { "value": "grid", "label": "Grid" },
                    { "value": "bars", "label": "Bars" }
                  ]
                },
                "pb_mid": { "type": "ProgressBar", "progress": "50" },
                "grid_branch": {
                  "type": "ItemsGrid",
                  "collection": "drives",
                  "when": "${state.layoutMode == 'grid'}"
                }
              }
            }
            """;
        CustomWidgetCompilerResult deadBranchCompiled = CustomWidgetCompilerPipeline.Compile(deadBranchManifest);
        Assert.Contains(
            deadBranchCompiled.HardDiagnostics,
            d => d.IsError && string.Equals(d.Code, "DeadStateSwitchBranch", StringComparison.Ordinal));

        const string completeBranchManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.complete-branch",
              "name": "Complete Branch",
              "state": { "layoutMode": "grid" },
              "collections": {
                "drives": { "fields": { "title": "text", "value": "number" } }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["seg_mode", "grid_branch", "bars_branch"] },
                "seg_mode": {
                  "type": "Segmented",
                  "bind": "${state.layoutMode}",
                  "options": [
                    { "value": "grid", "label": "Grid" },
                    { "value": "bars", "label": "Bars" }
                  ]
                },
                "grid_branch": {
                  "type": "ItemsGrid",
                  "collection": "drives",
                  "when": "${state.layoutMode == 'grid'}"
                },
                "bars_branch": {
                  "type": "BarList",
                  "collection": "drives",
                  "titleField": "title",
                  "valueField": "value",
                  "when": "${state.layoutMode == 'bars'}"
                }
              }
            }
            """;
        CustomWidgetCompilerResult completeCompiled = CustomWidgetCompilerPipeline.Compile(completeBranchManifest);
        Assert.DoesNotContain(
            completeCompiled.HardDiagnostics,
            d => string.Equals(d.Code, "DeadStateSwitchBranch", StringComparison.Ordinal));

        const string filterSegmentedManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.filter-bar",
              "name": "Filter Bar",
              "state": { "filter": "all" },
              "collections": {
                "tasks": { "fields": { "title": "text", "status": "text" } }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["seg_filter", "all_banner", "task_list"] },
                "seg_filter": {
                  "type": "Segmented",
                  "bind": "${state.filter}",
                  "options": [
                    { "value": "all", "label": "All" },
                    { "value": "open", "label": "Open" }
                  ]
                },
                "all_banner": {
                  "type": "Stack",
                  "when": "${state.filter == 'all'}",
                  "children": ["all_txt"]
                },
                "all_txt": { "type": "Text", "text": "Showing all tasks" },
                "task_list": {
                  "type": "List",
                  "collection": "tasks",
                  "filterEquals": "${state.filter}",
                  "titleField": "title"
                }
              }
            }
            """;
        CustomWidgetCompilerResult filterCompiled = CustomWidgetCompilerPipeline.Compile(filterSegmentedManifest);
        Assert.DoesNotContain(
            filterCompiled.HardDiagnostics,
            d => string.Equals(d.Code, "DeadStateSwitchBranch", StringComparison.Ordinal));

        // Numeric state switchers: option values are simulated with the declared JSON kind (number),
        // so `state.tab == 2` branches are recognized instead of being reported as dead.
        const string numericSwitchManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.numeric-tabs",
              "name": "Numeric Tabs",
              "state": { "tab": 1 },
              "collections": {
                "rows": { "fields": { "title": "text", "value": "number" } }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["seg_tab", "grid_tab1", "bars_tab2"] },
                "seg_tab": {
                  "type": "Segmented",
                  "bind": "${state.tab}",
                  "options": [
                    { "value": "1", "label": "One" },
                    { "value": "2", "label": "Two" }
                  ]
                },
                "grid_tab1": { "type": "ItemsGrid", "collection": "rows", "when": "${state.tab == 1}" },
                "bars_tab2": { "type": "BarList", "collection": "rows", "titleField": "title", "valueField": "value", "when": "${state.tab == 2}" }
              }
            }
            """;
        Assert.DoesNotContain(
            CustomWidgetCompilerPipeline.Compile(numericSwitchManifest).HardDiagnostics,
            d => string.Equals(d.Code, "DeadStateSwitchBranch", StringComparison.Ordinal));

        string templatesRoot = TestPaths.FromRepository("src/DeskBox/Assets/CustomWidgets/Templates");
        string[] templateDirs = Directory.GetDirectories(templatesRoot);
        Assert.NotEmpty(templateDirs);
        foreach (string dir in templateDirs)
        {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            string rawManifest = File.ReadAllText(manifestPath, Encoding.UTF8);
            string scriptPath = Path.Combine(dir, "logic.js");
            string? rawScript = File.Exists(scriptPath) ? File.ReadAllText(scriptPath, Encoding.UTF8) : null;
            string samplePath = Path.Combine(dir, "sample-data.json");
            string? sampleJson = File.Exists(samplePath) ? File.ReadAllText(samplePath, Encoding.UTF8) : null;
            string localesDir = Path.Combine(dir, "locales");
            CustomWidgetLocaleCatalog? locales = Directory.Exists(localesDir)
                ? CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir)
                : null;

            CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
                rawManifest,
                rawScript,
                locales,
                sampleJson,
                cultureName: "en-US");
            string errSummary = string.Join(
                "; ",
                compiled.HardDiagnostics.Where(static d => d.IsError).Select(static d => $"[{d.Code}] {d.Path}: {d.Message}"));
            Assert.True(
                compiled.HardDiagnostics.All(static d => !d.IsError),
                $"Built-in template '{Path.GetFileName(dir)}' failed validation: {errSummary}");
        }
    }

    [Fact]
    public void BriefSanitization_UndoRollback_AndSupersededPromptFiltering_PreventRepairContamination()
    {
        var initialBrief = new CustomWidgetDesignBrief { Purpose = string.Empty };
        CustomWidgetManifest? seedManifest = CustomWidgetManifestParser.Parse(
            """{"schemaVersion":1,"id":"custom.drive","name":"Drive","root":"main","elements":{"main":{"type":"Stack","children":[]}}}""").Manifest;
        CustomWidgetDesignBrief afterPollutedRepair = StudioContextCompactor.SanitizeAndRollBrief(
            initialBrief,
            updates: null,
            newManifest: seedManifest,
            rawUserPrompt: "[Target language: en-US]\nPlease repair all validation diagnostics in manifest.json.",
            startDraftIdea: "Build a cyber drive monitor",
            isInitialOrEmptyBrief: true);
        Assert.Equal("Build a cyber drive monitor", afterPollutedRepair.Purpose);
        Assert.DoesNotContain("Please repair all validation diagnostics", afterPollutedRepair.Purpose, StringComparison.OrdinalIgnoreCase);

        using var temp = new TempDirectoryScope();
        var settingsService = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var loc = new LocalizationService(settingsService);
        var prefs = new CustomWidgetStudioPreferencesStore(Path.Combine(temp.Path, "studio-prefs.json"));
        var sessionStore = new CustomWidgetStudioSessionStore(Path.Combine(temp.Path, "studio-sessions"));

        var session = new StudioSessionController(
            loc,
            NullStudioHostBridge.Instance,
            prefs,
            sessionStore,
            hasSystemWallpaperAvailable: false,
            initialBackdropMode: StageBackdropMode.Dark);
        session.TargetWidgetId = "custom.brief-undo";
        session.TargetWidgetName = "Brief Undo";

        // Repair/fix entry points set a display label; that marks the real prompt as system-composed.
        session.PendingPrompt = "[Target language: ja-JP]\nfix stray root keys";
        session.PendingDisplayPrompt = "AI で自動修復";
        Assert.True(session.IsSystemComposedPrompt(session.PendingPrompt));
        Assert.False(session.IsSystemComposedPrompt("Make the ring thicker"));
        session.PendingDisplayPrompt = null;
        Assert.False(session.IsSystemComposedPrompt(session.PendingPrompt));
        session.PendingPrompt = string.Empty;

        const string manifestV1 = """{"schemaVersion":1,"id":"custom.brief-undo","name":"Brief Undo","root":"main","elements":{"main":{"type":"Stack","children":[]}}}""";
        const string manifestV2 = """{"schemaVersion":1,"id":"custom.brief-undo","name":"Brief Undo V2","root":"main","elements":{"main":{"type":"Stack","children":[]}}}""";

        // Mirror StudioGenerationCoordinator: AddRevision first, then MergeTurnBriefUpdates stamps that revision.
        StudioPersistedRevision rev1 = session.AddRevision(
            manifestV1,
            "v1",
            isAiChange: true,
            source: "ai",
            isCommitted: false,
            markDirty: false);
        session.MergeTurnBriefUpdates(
            new StudioBriefUpdateSpec { Purpose = "Original purpose" },
            newManifest: null,
            userPrompt: "Create original widget");
        string briefAtV1 = session.BriefMarkdown;
        Assert.Contains("Original purpose", briefAtV1, StringComparison.Ordinal);

        StudioPersistedRevision rev2 = session.AddRevision(
            manifestV2,
            "v2",
            isAiChange: true,
            source: "ai",
            isCommitted: false,
            markDirty: false);
        session.MergeTurnBriefUpdates(
            new StudioBriefUpdateSpec { Purpose = "Expanded V2 purpose" },
            newManifest: null,
            userPrompt: "Please repair all validation diagnostics on rev2");
        Assert.Contains("Expanded V2 purpose", session.BriefMarkdown, StringComparison.Ordinal);

        // Undo back to rev1 -> BriefMarkdown rolls back and rev2 chat message is marked superseded.
        session.ChatMessages.Add(new StudioChatMessage
        {
            Id = "msg-user-1",
            Role = "user",
            Kind = "chat",
            Text = "Create original widget",
            LinkedRevisionId = rev1.RevisionId
        });
        session.ChatMessages.Add(new StudioChatMessage
        {
            Id = "msg-repair-synth",
            Role = "user",
            Kind = "chat",
            Text = "请修复校验提示：StrayViewPropertiesAtManifestRoot",
            LinkedRevisionId = rev1.RevisionId
        });
        // Structured tag works for every UI language (e.g. a Japanese repair label), not just zh/en text.
        session.ChatMessages.Add(new StudioChatMessage
        {
            Id = "msg-repair-tagged",
            Role = "user",
            Kind = StudioContextCompactor.RepairMessageKind,
            Text = "検証エラーを修正：StrayViewPropertiesAtManifestRoot",
            LinkedRevisionId = rev1.RevisionId
        });
        session.ChatMessages.Add(new StudioChatMessage
        {
            Id = "msg-user-2",
            Role = "user",
            Kind = "chat",
            Text = "Expand to V2",
            LinkedRevisionId = rev2.RevisionId
        });

        Assert.True(session.SelectRevisionIndex(0));
        session.SyncChatSupersededWithRevisionIndex();
        Assert.Equal(briefAtV1, session.BriefMarkdown);

        IReadOnlyList<StudioChatMessage> filtered = StudioContextCompactor.FilterAndClampRecentMessages(
            session.ChatMessages,
            maxMessages: 8);
        Assert.Single(filtered);
        Assert.Equal("msg-user-1", filtered[0].Id);

        // Restoring a polluted draft heals Purpose instead of carrying forward repair boilerplate.
        string pollutedDraftBrief = CustomWidgetStudioSessionStore.FormatBriefMarkdown(
            new CustomWidgetDesignBrief
            {
                Purpose = "Please repair all validation diagnostics and emit valid JSON",
                ConfirmedRequirements = ["Clean cyber ring card"]
            });
        var draft = new StudioDraftSnapshot
        {
            TargetWidgetId = "custom.brief-undo",
            StartDraftIdea = "Cyber disk monitor",
            SelectedRevisionIndex = 0,
            BriefMarkdown = pollutedDraftBrief,
            Revisions = [rev1]
        };
        session.RestoreDraftState(draft);
        Assert.DoesNotContain("Please repair all validation diagnostics", session.BriefMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cyber disk monitor", session.BriefMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextWindowPersistence_ModelsDiscovery_OllamaBudget_Compaction_AndHttp400OverflowSelfHealing()
    {
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "ollama",
            ApiKey: string.Empty,
            CustomEndpoint: "http://127.0.0.1:11434/v1/chat/completions",
            CustomModel: "qwen3:14b",
            ThinkingMode: "disabled",
            ContextWindowMode: "32k"));

        AiProviderCredentialEntry? loaded = credStore.Get("ollama");
        Assert.NotNull(loaded);
        Assert.Equal("32k", loaded!.ContextWindowMode);

        var aiServiceForConfig = new DeskBoxAiService(credStore);
        ResolvedAiInvocation ollamaResolved = aiServiceForConfig.ResolveEffectiveSettings("ollama");
        Assert.Equal(AiContextWindowMode.Standard32k, ollamaResolved.ContextWindowMode);
        Assert.Equal(32_768, AiContextBudgetResolver.Resolve(ollamaResolved).EffectiveContextTokens);

        // Auto mode on Ollama: the /v1 endpoint silently truncates (no HTTP 400 to learn from), so budget for the
        // minimum Studio-capable window and never send the ignored per-request "options.num_ctx".
        ResolvedAiInvocation ollamaAuto = ollamaResolved with { ContextWindowMode = AiContextWindowMode.Auto };
        AiContextBudget ollamaBudget = AiContextBudgetResolver.Resolve(ollamaAuto);
        Assert.Equal(AiContextBudgetResolver.OllamaAssumedContextTokens, ollamaBudget.EffectiveContextTokens);
        Assert.True(ollamaBudget.ManifestFoldCharThreshold < StudioWorkspaceEngine.ManifestFoldCharThreshold);

        byte[] ollamaPayload = AiChatTransportClient.BuildChatRequestPayload(
            ollamaAuto,
            systemPrompt: "System",
            userPrompt: "User",
            stream: true,
            includeThinkingParams: true,
            isProbe: false);
        using (JsonDocument payloadDoc = JsonDocument.Parse(ollamaPayload))
        {
            Assert.False(payloadDoc.RootElement.TryGetProperty("options", out _));
        }

        const string openAiModelsJson = """
            {
              "data": [
                { "id": "glm-5.3", "context_length": 131072 },
                { "id": "local-vllm", "max_model_len": 32768 }
              ]
            }
            """;
        const string geminiModelsJson = """
            {
              "models": [
                { "name": "models/gemini-2.5-flash", "inputTokenLimit": 1048576 }
              ]
            }
            """;
        Dictionary<string, int> parsedContexts = AiContextBudgetResolver.ParseModelContextLengthsFromModelsJson(openAiModelsJson);
        Assert.Equal(131_072, parsedContexts["glm-5.3"]);
        Assert.Equal(32_768, parsedContexts["local-vllm"]);
        Dictionary<string, int> parsedGeminiContexts = AiContextBudgetResolver.ParseModelContextLengthsFromModelsJson(geminiModelsJson);
        Assert.Equal(1_048_576, parsedGeminiContexts["gemini-2.5-flash"]);

        // Intra-turn write_bundle compaction shrinks older large bundles while preserving the newest.
        string largeArgs1 = new string('x', 1_600);
        string largeArgs2 = new string('y', 1_600);
        IReadOnlyList<AiChatTurnMessage> compacted = StudioContextCompactor.CompactSupersededStructuredTurns(
        [
            new AiChatTurnMessage("assistant", ToolCalls: [new AiToolCallSpec("tc1", StudioToolTransport.ToolWriteBundle, largeArgs1)]),
            new AiChatTurnMessage("tool", Content: "{\"ok\":true}", ToolCallId: "tc1", ToolName: StudioToolTransport.ToolWriteBundle),
            new AiChatTurnMessage("assistant", ToolCalls: [new AiToolCallSpec("tc2", StudioToolTransport.ToolWriteBundle, largeArgs2)]),
            new AiChatTurnMessage("tool", Content: "{\"ok\":true}", ToolCallId: "tc2", ToolName: StudioToolTransport.ToolWriteBundle)
        ]);
        Assert.Contains("_compacted", compacted[0].ToolCalls![0].ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(largeArgs2, compacted[2].ToolCalls![0].ArgumentsJson);

        // HTTP 400 context overflow self-healing: caches learned 16384 limit, compacts prompt/turns, retries and succeeds.
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "openai",
            ApiKey: "sk-openai-test",
            CustomEndpoint: "https://relay.example.com/v1/chat/completions",
            CustomModel: "gpt-4.1",
            ThinkingMode: "disabled",
            ContextWindowMode: "auto"));

        var capturedOverflowBodies = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(async (req, callIndex, ct) =>
        {
            string body = await req.Content!.ReadAsStringAsync(ct);
            capturedOverflowBodies.Add(body);

            if (callIndex == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"This model's maximum context length is 16384 tokens. However, your messages resulted in 18200 tokens.","type":"invalid_request_error","code":"context_length_exceeded"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            string sse = BuildToolCallsSse(
            [
                ("call_wb_ok", "write_bundle", """{"manifestJson":"{\"schemaVersion\":1,\"id\":\"custom.ctx-ok\",\"name\":\"Ctx Ok\",\"icon\":\"✨\",\"root\":\"main\",\"elements\":{\"main\":{\"type\":\"Stack\",\"children\":[\"t0\"]},\"t0\":{\"type\":\"Text\",\"text\":\"Recovered\"}}}"}"""),
                ("call_fin_ok", "finish_turn", """{"kind":"edit","reply":"Recovered after compacting context.","changeSummary":"Recovered widget"}""")
            ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }));

        var aiService = new DeskBoxAiService(credStore, httpClient: httpClient);
        var generator = new CustomWidgetAiGeneratorService(aiService);

        var longElements = new Dictionary<string, object>
        {
            ["main"] = new { type = "Stack", children = Enumerable.Range(0, 60).Select(static i => $"t{i}").ToArray() }
        };
        for (int i = 0; i < 60; i++)
        {
            longElements[$"t{i}"] = new { type = "Text", text = $"Item {i} " + new string('a', 90) };
        }
        string longUserManifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "custom.ctx-large",
            name = "Large",
            root = "main",
            elements = longElements
        });

        StudioTurnResult turn = await generator.RunStudioTurnAsync(
            providerId: "openai",
            userMessage: "Keep item list tidy",
            currentManifestJson: longUserManifest,
            briefMarkdown: string.Empty,
            recentMessages: [],
            healthReport: null,
            targetNodePath: null,
            preferredEmoji: "✨",
            allowSampleData: false,
            uiLanguage: "en-US");

        Assert.True(turn.Success, turn.ErrorMessage);
        Assert.Equal(2, capturedOverflowBodies.Count);

        ResolvedAiInvocation afterOverflow = aiService.ResolveEffectiveSettings("openai");
        Assert.Equal(16_384, aiService.GetKnownContextTokens(afterOverflow));

        using JsonDocument retryDoc = JsonDocument.Parse(capturedOverflowBodies[1]);
        Assert.True(retryDoc.RootElement.GetProperty("max_tokens").GetInt32() <= 4_096);
        Assert.True(capturedOverflowBodies[1].Length < capturedOverflowBodies[0].Length);
    }

    [Fact]
    public void CompactTokenFormatter_AndStreamingAccumulator_FormatCompactTokensWithExplicitUnit()
    {
        Assert.Equal("0 tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(0));
        Assert.Equal("214 tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(214));
        Assert.Equal("999 tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(999));
        Assert.Equal("1k tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(1_000));
        Assert.Equal("1.2k tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(1_240));
        Assert.Equal("10k tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(9_980));
        Assert.Equal("12k tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(12_340));
        Assert.Equal("1m tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(999_800));
        Assert.Equal("1.2m tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(1_240_000));
        Assert.Equal("12m tokens", StreamingTurnAccumulator.FormatCompactTokensWithUnit(12_300_000));

        var acc = new StreamingTurnAccumulator();
        acc.AppendReasoningDelta("Analyzing Sina Finance quote fields and layout hierarchy.".AsSpan());
        acc.AppendContentDelta("<think>Checking secondary trend chart.</think>".AsSpan());
        acc.AppendToolCallDelta(0, "call_1", "write_bundle", "{\"manifestJson\":\"{\\\"name\\\":\\\"股票热榜\\\"}\"}");

        Assert.True(acc.EstimatedThinkingTokens > 0);
        Assert.True(acc.EstimatedUiContentTokens > 0);
        Assert.True(acc.EstimatedThinkingTokens < acc.TotalThinkingChars);
    }

    [Fact]
    public void NetworkResponseDecoder_DecodesGb18030WithAndWithoutCharsetHeader_WhilePreservingUtf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gb = Encoding.GetEncoding("GB18030");
        const string sampleQuote = "var hq_str_sz002415=\"海康威视,33.12,34.62\";";
        byte[] gbBytes = gb.GetBytes(sampleQuote);

        Assert.Equal(sampleQuote, CustomWidgetNetworkSourceEngine.DecodeResponseBytes(gbBytes, "GBK"));
        Assert.Equal(sampleQuote, CustomWidgetNetworkSourceEngine.DecodeResponseBytes(gbBytes, charset: null));

        const string utf8Sample = "{\"title\":\"贵州茅台\",\"price\":1512.00}";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(utf8Sample);
        Assert.Equal(utf8Sample, CustomWidgetNetworkSourceEngine.DecodeResponseBytes(utf8Bytes, charset: null));
    }

    [Fact]
    public void ClarifyChipFollowUp_PreservesInitialPurpose_AndRollsChipChoiceIntoConfirmedRequirements()
    {
        using var temp = new TempDirectoryScope();
        var settingsService = new SettingsService(Path.Combine(temp.Path, "settings.json"));
        var loc = new LocalizationService(settingsService);
        var prefs = new CustomWidgetStudioPreferencesStore(Path.Combine(temp.Path, "studio-prefs.json"));
        var sessionStore = new CustomWidgetStudioSessionStore(Path.Combine(temp.Path, "studio-sessions"));

        var session = new StudioSessionController(
            loc,
            NullStudioHostBridge.Instance,
            prefs,
            sessionStore,
            hasSystemWallpaperAvailable: false,
            initialBackdropMode: StageBackdropMode.Dark);

        const string initialIdea = "做一个股票推荐格子，展示各大股票的热门榜单。样式要高级，要有趋势图展示";
        const string clarifyAnswer = "接新浪财经公开行情";
        session.StartDraftIdea = initialIdea;

        // Turn 1: AI returns clarify with null BriefUpdates and no manifest yet.
        session.AppendChatMessage(new StudioChatMessage
        {
            Id = "u1",
            Role = "user",
            Kind = "prompt",
            Text = initialIdea
        });
        session.MergeTurnBriefUpdates(updates: null, newManifest: null, userPrompt: initialIdea);

        CustomWidgetDesignBrief afterClarify = CustomWidgetStudioSessionStore.ParseBriefMarkdown(session.BriefMarkdown);
        Assert.Equal(initialIdea, afterClarify.Purpose);

        // Turn 2: User clicks short clarify chip; AI echoes short chip text as updates.Purpose.
        CustomWidgetManifest? generatedManifest = CustomWidgetManifestParser.Parse(
            """{"schemaVersion":1,"id":"custom.stocks","name":"股票热榜雷达","root":"main","elements":{"main":{"type":"Stack","children":[]}}}""").Manifest;
        session.AppendChatMessage(new StudioChatMessage
        {
            Id = "u2",
            Role = "user",
            Kind = "prompt",
            Text = clarifyAnswer
        });
        session.MergeTurnBriefUpdates(
            new StudioBriefUpdateSpec { Purpose = clarifyAnswer },
            generatedManifest,
            clarifyAnswer);

        CustomWidgetDesignBrief afterTurn2 = CustomWidgetStudioSessionStore.ParseBriefMarkdown(session.BriefMarkdown);
        Assert.Equal(initialIdea, afterTurn2.Purpose);
        Assert.Contains(clarifyAnswer, afterTurn2.ConfirmedRequirements);
    }

    [Fact]
    public void ValidatorAndFlatEditor_SuppressCollectionWriteOnlyFalsePositive_UpsertChildren_AndCleanAutoCopy()
    {
        // 1. Write-only collections.set in logic.js should NOT trigger ScriptSampleDataZeroEffect.
        const string writeOnlyManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.stock.trend",
          "name": "Stock Trend",
          "collections": {
            "trend": { "maxItems": 60 }
          },
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["hdr_txt", "trend_chart"] },
            "hdr_txt": { "type": "Text", "text": "${$computed.headerLabel}" },
            "trend_chart": { "type": "Chart", "collection": "trend", "valueField": "price", "labelField": "time" }
          }
        }
        """;
        const string writeOnlyLogic = """
            export function onInit(ctx) {
              ctx.computed.headerLabel = '';
            }
            export function onResponse(ctx) {
              ctx.collections.set('trend', [{ time: '09:30', price: '33.12' }]);
            }
            """;
        CustomWidgetWorkspaceValidationResult valRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            writeOnlyManifest,
            writeOnlyLogic,
            sampleDataJson: """{ "collections": { "trend": [{ "time": "09:30", "price": "33.12" }] } }""");
        Assert.DoesNotContain(valRes.Diagnostics, static d => d.Code == "ScriptSampleDataZeroEffect");

        // 2. Flat element graph upsert + child ordering produces clean localized auto-finalized copy without internal tokens.
        const string baseManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.patch.rel",
          "name": "Patch Rel",
          "root": "main",
          "elements": {
            "main": { "type": "Grid", "children": ["header", "footer"] },
            "header": { "type": "Text", "text": "Header" },
            "footer": { "type": "Text", "text": "Footer" }
          }
        }
        """;
        var engine = new StudioWorkspaceEngine(baseManifest, "export function onInit(ctx) {}");
        StudioWorkspaceOpResult op1 = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["mid0"] = new JsonObject { ["type"] = "Text", ["text"] = "Sub" },
                ["mid1"] = new JsonObject { ["type"] = "ItemsGrid" },
                ["main"] = new JsonObject { ["type"] = "Grid", ["children"] = new JsonArray("header", "mid0", "mid1", "footer") }
            }));
        Assert.True(op1.Succeeded, op1.ErrorMessage);
        engine.SetLogicJs("export function onInit(ctx) { ctx.state.ok = '1'; }");

        (string autoReply, _) = engine.BuildAutoFinalizedTurnCopy("Patch Rel", isInitialTurn: false, cultureName: "zh-CN");
        Assert.DoesNotContain("logic.js", autoReply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ItemsGrid", autoReply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhaseAwareThinkingRouter_AllowsPostDiscoverySynthesisOnce_ThenDisablesOnRepair_AndSkipsMicroEdits()
    {
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "glm",
            ApiKey: "sk-glm-phase",
            CustomEndpoint: "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            CustomModel: "glm-5.3-flash",
            ThinkingMode: "enabled"));

        var aiServiceForResolve = new DeskBoxAiService(credStore, httpClient: new HttpClient());
        ResolvedAiInvocation baseResolved = aiServiceForResolve.ResolveEffectiveSettings("glm");

        // Studio AI v2: explicit user-configured "enabled" is honored on Round 0 (not silently overridden by keywords),
        // while "auto" defaults to fast non-thinking tool flow across micro-edits and initial creation.
        Assert.Equal(
            AiThinkingMode.Enabled,
            StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
                baseResolved,
                fetchedModels: null,
                userPrompt: "把按钮换成蓝色，字再大一点",
                hasExistingManifest: true,
                targetNodePath: null,
                pinnedCapabilityIds: null));

        ResolvedAiInvocation autoResolved = baseResolved with { ThinkingMode = AiThinkingMode.Auto };
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
                autoResolved,
                fetchedModels: null,
                userPrompt: "把按钮换成蓝色，字再大一点",
                hasExistingManifest: true,
                targetNodePath: null,
                pinnedCapabilityIds: null));

        AiThinkingMode? clarifyOverride = StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
            autoResolved,
            fetchedModels: null,
            userPrompt: "做一个番茄钟效率看板",
            hasExistingManifest: false,
            targetNodePath: null,
            pinnedCapabilityIds: null);
        Assert.Equal(AiThinkingMode.Disabled, clarifyOverride);

        AiThinkingMode? directBuildOverride = StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
            baseResolved,
            fetchedModels: null,
            userPrompt: "直接开始构建：番茄钟效率看板",
            hasExistingManifest: false,
            targetNodePath: null,
            pinnedCapabilityIds: null);
        Assert.Equal(AiThinkingMode.Enabled, directBuildOverride);
        AiThinkingMode? createOverride = AiThinkingMode.Auto;

        const string brokenDraftManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.phase-demo",
          "name": "Phase Demo",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["n1"] },
            "n1": { "type": "InvalidWidgetNode", "text": "Hello" }
          }
        }
        """;

        var capturedPayloads = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(async (req, callIndex, ct) =>
        {
            capturedPayloads.Add(await req.Content!.ReadAsStringAsync(ct));
            if (callIndex == 1)
            {
                // Round 0: lightweight discovery call
                const string discoverySse =
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Need micro-charts spec first.\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_cap_0\",\"type\":\"function\",\"function\":{\"name\":\"inspect_capabilities\",\"arguments\":\"{\\\"ids\\\":[\\\"micro-charts\\\"]}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(discoverySse, Encoding.UTF8, "text/event-stream")
                };
            }

            if (callIndex == 2)
            {
                // Round 1: architecture synthesis round writes bundle (with a node typo to trigger Round 2 repair)
                string synthSse = BuildToolCallsSse(
                [
                    ("call_wb_1", "write_bundle", JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["manifestJson"] = brokenDraftManifest
                    })),
                    ("call_fin_1", "finish_turn", """{"kind":"edit","reply":"Draft ready.","changeSummary":"Init"}""")
                ]);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(synthSse, Encoding.UTF8, "text/event-stream")
                };
            }

            // Round 2: compiler-guided repair round
            string repairSse = BuildToolCallsSse(
            [
                ("call_em_2", "edit_manifest", """{"upsertElements":{"n1":{"type":"Text","text":"Hello"}}}"""),
                ("call_fin_2", "finish_turn", """{"kind":"edit","reply":"Fixed.","changeSummary":"Fix"}""")
            ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(repairSse, Encoding.UTF8, "text/event-stream")
            };
        }));

        var generator = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, httpClient: httpClient));
        StudioTurnResult turnRes = await generator.RunStudioTurnAsync(
            providerId: "glm",
            userMessage: "Build a compact pulse card",
            uiLanguage: "en-US",
            thinkingOverride: createOverride);

        Assert.True(turnRes.Success, turnRes.ErrorMessage);
        Assert.Equal(3, capturedPayloads.Count);

        // Round 0 (discovery) and Round 1 (first synthesis) stay in Auto (no forced disabled override; Round 1 keeps Round 0 reasoning_content).
        using JsonDocument r0 = JsonDocument.Parse(capturedPayloads[0]);
        Assert.False(r0.RootElement.TryGetProperty("thinking", out _));

        using JsonDocument r1 = JsonDocument.Parse(capturedPayloads[1]);
        Assert.False(r1.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(
            "Need micro-charts spec first.",
            r1.RootElement.GetProperty("messages")[2].GetProperty("reasoning_content").GetString());

        // Round 2 (repair) strictly forces thinking=disabled and omits historical reasoning_content.
        using JsonDocument r2 = JsonDocument.Parse(capturedPayloads[2]);
        Assert.Equal("disabled", r2.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(r2.RootElement.GetProperty("messages")[2].TryGetProperty("reasoning_content", out _));
    }

    [Fact]
    public void BuildWorkspaceTurnResult_ResolvesI18nTitle_PrunesIncompleteInitialSecondaryLocales_AndNormalizesDialectDom()
    {
        const string dialectManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.dialect-check",
          "name": "热门股票实时行情",
          "settings": [
            { "key": "sym1", "type": "text", "label": "股票代码", "default": "AAPL" }
          ],
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["row_price"] },
            "row_price": {
              "type": "Stack",
              "align": "center",
              "spacing": 8,
              "children": ["lbl_price", "flex_gap", "val_price", "btn_refresh"]
            },
            "lbl_price": { "type": "Text", "text": "价格", "bodyStrong": true },
            "flex_gap": { "type": "Stack", "flex": 1 },
            "val_price": { "type": "Text", "text": "228.50", "mono": true },
            "btn_refresh": { "type": "IconButton", "icon": "\\uE72C", "onClick": { "type": "state.set", "key": "k", "value": "1" } }
          }
        }
        """;

        var partialSecondary = new CustomWidgetLocaleCatalog(
            (CustomWidgetLocaleMeta?)null,
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["settings.sym1.label"] = "Symbol"
                }
            });

        var workspace = new StudioWorkspaceEngine(dialectManifest, null, partialSecondary);
        (StudioTurnResult turn, CustomWidgetCompilerResult? compiled) = StudioAiTurnOrchestrator.BuildWorkspaceTurnResult(
            workspace,
            firstFinish: null,
            latestFinish: null,
            capturedSampleDataJson: null,
            existingSampleDataJson: null,
            originalManifestJson: null,
            originalLogicJs: null,
            originalLocales: null,
            effectiveLanguage: "zh-CN",
            preferredEmoji: "📈",
            isInitialCreation: true,
            wasAutoRepaired: true,
            providerId: "glm",
            effectiveModel: "glm-5.3-flash");

        Assert.True(turn.Success, turn.ErrorMessage);
        Assert.DoesNotContain("${$i18n", turn.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("${$i18n", turn.ChangeSummary, StringComparison.Ordinal);
        Assert.Contains("热门股票实时行情", turn.Reply, StringComparison.Ordinal);
        Assert.Equal("热门股票实时行情", turn.ChangeSummary);

        // Incomplete 1-key en-US secondary locale on initial creation is pruned so health report stays clean.
        Assert.NotNull(turn.Locales);
        Assert.True(turn.Locales!.Locales.ContainsKey("zh-CN"));
        Assert.False(turn.Locales.Locales.ContainsKey("en-US"));
        Assert.NotNull(compiled?.HealthReport);
        Assert.DoesNotContain(compiled!.HealthReport!.Issues, static i => i.Code == "i18n.incomplete");

        CustomWidgetNodeSpec rowStack = turn.ParseResult!.Manifest!.View.Children[0];
        Assert.Equal("horizontal", rowStack.Orientation);
        Assert.Equal("bodyStrong", rowStack.Children[0].Style);
        Assert.Equal("Spacer", rowStack.Children[1].Type);
        Assert.Equal("mono", rowStack.Children[2].FontFamily);
        Assert.Equal("\uE72C", rowStack.Children[3].Icon);
    }

    [Fact]
    public void BuildWorkspaceTurnResult_CrlfBaselineUnchangedScript_StaysKeepExisting_AndRealEditStillReplaces()
    {
        const string manifest = """
        {
          "schemaVersion": 1,
          "id": "custom.crlf-gate",
          "name": "CRLF Gate",
          "script": { "entry": "logic.js" },
          "root": "root",
          "elements": {
            "root": { "type": "Text", "text": "${$computed.banner}" }
          }
        }
        """;
        const string logicJs = "export function onInit(ctx) { ctx.computed.banner = 'v1'; }\nexport function bump(ctx) { ctx.computed.banner = 'v2'; }";
        string crlfBaseline = logicJs.Replace("\n", "\r\n", StringComparison.Ordinal);

        var unchanged = new StudioWorkspaceEngine(manifest, logicJs, null);
        (StudioTurnResult unchangedTurn, _) = StudioAiTurnOrchestrator.BuildWorkspaceTurnResult(
            unchanged,
            firstFinish: null,
            latestFinish: null,
            capturedSampleDataJson: null,
            existingSampleDataJson: null,
            originalManifestJson: manifest,
            originalLogicJs: crlfBaseline,
            originalLocales: null,
            effectiveLanguage: "en-US",
            preferredEmoji: null,
            isInitialCreation: false,
            wasAutoRepaired: false,
            providerId: "test",
            effectiveModel: "test");
        Assert.True(unchangedTurn.Success, unchangedTurn.ErrorMessage);
        Assert.Equal(StudioLogicJsPatchMode.KeepExisting, unchangedTurn.LogicJsPatchMode);

        var edited = new StudioWorkspaceEngine(manifest, logicJs.Replace("v2", "v3", StringComparison.Ordinal), null);
        (StudioTurnResult editedTurn, _) = StudioAiTurnOrchestrator.BuildWorkspaceTurnResult(
            edited,
            firstFinish: null,
            latestFinish: null,
            capturedSampleDataJson: null,
            existingSampleDataJson: null,
            originalManifestJson: manifest,
            originalLogicJs: crlfBaseline,
            originalLocales: null,
            effectiveLanguage: "en-US",
            preferredEmoji: null,
            isInitialCreation: false,
            wasAutoRepaired: false,
            providerId: "test",
            effectiveModel: "test");
        Assert.True(editedTurn.Success, editedTurn.ErrorMessage);
        Assert.Equal(StudioLogicJsPatchMode.Replace, editedTurn.LogicJsPatchMode);
    }

    [Fact]
    public async Task ValidatorAndPreviewLifecycle_FlagColdStartSplitState_SampleStateGap_FailedProbeHost_AndRunImmediatePreviewOnTick()
    {
        const string splitStateManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.split-state",
          "name": "Split State Check",
          "collections": {
            "q0": { "maxItems": 20 }
          },
          "script": { "entry": "logic.js", "tickIntervalMs": 60000 },
          "state": {
            "loaded": false,
            "q0": {}
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["loading_stack", "card_q0"] },
            "loading_stack": {
              "type": "Stack",
              "when": "${state.loaded != true}",
              "children": ["ring_loading"]
            },
            "ring_loading": { "type": "ProgressRing" },
            "card_q0": {
              "type": "Card",
              "children": ["txt_q0_name"]
            },
            "txt_q0_name": { "type": "Text", "text": "${state.q0.name}" }
          }
        }
        """;
        const string splitStateJs = """
            export function onInit(ctx) {}
            export function onTick(ctx) {
              ctx.state.loaded = true;
              ctx.state.q0 = { name: 'SeededOnTick' };
            }
            """;

        CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
            splitStateManifest,
            splitStateJs,
            locales: null,
            sampleDataJson: """{"collections":{"q0":[{"v":10}]}}""",
            cultureName: "zh-CN");
        Assert.Contains(compiled.ActionableSoftDiagnostics, static d => d.Code == "view.cold_start_split_state");
        Assert.Contains(compiled.ActionableSoftDiagnostics, static d => d.Code == "view.sample_data_incomplete_state");

        // Failed probe_http host referenced in logic.js is flagged by preflight probe service.
        var probeService = new StudioPreFlightProbeService(networkSourceEngine: null);
        (_, string? softProbe) = await probeService.BuildNetworkProbeFeedbackAsync(
            compiled.ValidationResult.Manifest!,
            CancellationToken.None,
            logicJs: "const URL = 'https://unreachable.example.com/v1/quotes';",
            failedProbeHosts: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["unreachable.example.com"] = "Status: 429 (ok=false)"
            });
        Assert.NotNull(softProbe);
        Assert.Contains("unreachable.example.com", softProbe!, StringComparison.Ordinal);

        // Studio stage preview executes exported onTick immediately on ApplyPreviewBundle instead of waiting 60s.
        using var temp = new TempDirectoryScope();
        var locSvc = new LocalizationService(new SettingsService(Path.Combine(temp.Path, "settings.json")));
        using var previewVm = new DeskBox.ViewModels.CustomWidgetViewModel(
            new WidgetConfig { Id = "__studio_preview__", Name = "Preview", WidgetKind = WidgetKind.Custom },
            locSvc,
            store: null,
            dispatcherQueue: null,
            notificationServiceProvider: null,
            isPreviewOnly: true);
        previewVm.ApplyPreviewBundle(splitStateManifest, splitStateJs, locales: null, preserveRuntimeData: false);
        Assert.Equal("true", previewVm.State["loaded"]);
    }

    [Fact]
    public void ExpressionEvaluator_AndDialectNormalizer_SupportJsStringMethodsCollectionLengthBindPromotionAndElementIds()
    {
        var rec0 = new CustomWidgetRecord
        {
            Id = "r1",
            FieldsJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["title"] = CustomWidgetScriptEngine.CreateStringJsonElement("科技爱好者周刊：AI Agent 架构实战"),
                ["tag"] = CustomWidgetScriptEngine.CreateStringJsonElement("  DeepSeek  ")
            }
        };
        var ctx = new CustomWidgetEvaluationContext
        {
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["keyword"] = "agent"
            },
            Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
            {
                ["items"] = [rec0]
            }
        };

        CustomWidgetEvaluationContext itemCtx = ctx.WithItem(rec0, 0);
        Assert.True(CustomWidgetExpressionEvaluator.EvaluateCondition(
            "!state.keyword || ($item.title || '').toLowerCase().includes(state.keyword.toLowerCase())",
            itemCtx));
        Assert.False(CustomWidgetExpressionEvaluator.EvaluateCondition(
            "($item.title || '').toLowerCase().contains('nonexistent')",
            itemCtx));
        Assert.Equal("deepseek", CustomWidgetExpressionEvaluator.EvaluateExpression("$item.tag.trim().toLowerCase()", itemCtx));
        Assert.Equal("1", CustomWidgetExpressionEvaluator.EvaluateExpression("items.length", ctx));
        Assert.True(CustomWidgetExpressionEvaluator.EvaluateCondition("items.length > 0", ctx));
        Assert.True(CustomWidgetExpressionEvaluator.EvaluateCondition("$item.title.indexOf('周刊') >= 0", itemCtx));

        const string dialectManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.news",
          "name": "热点资讯",
          "collections": { "items": {} },
          "state": { "keyword": "" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["inp_keyword", "ring_1", "list_items"] },
            "inp_keyword": { "type": "Input", "bind": "state.keyword" },
            "ring_1": { "type": "ProgressRing", "$when": "state.sourceStatus == 'loading'" },
            "list_items": {
              "type": "List",
              "collection": "items",
              "itemTemplate": "item_card"
            },
            "item_card": {
              "type": "Card",
              "children": ["item_title"]
            },
            "item_title": { "type": "Text", "bind": "$item.title" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(dialectManifest);
        Assert.True(engine.NormalizeDomInPlace());
        string normalized = engine.GetManifestJson()!;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(normalized);
        Assert.True(parsed.IsValid);
        Assert.Equal("inp_keyword", parsed.Manifest!.View.Children[0].Id);
        Assert.Equal("#inp_keyword", parsed.Manifest.View.Children[0].NodePath);
        Assert.Equal("ring_1", parsed.Manifest.View.Children[1].Id);
        Assert.Equal("list_items", parsed.Manifest.View.Children[2].Id);
        CustomWidgetNodeSpec listSpec = parsed.Manifest.View.Children[2];
        Assert.Equal("${$item.title}", listSpec.ItemTemplate!.Children[0].Text);
    }

    [Fact]
    public void StageRouter_AndContextCompactor_RouteThinkingAndSubtreeGuard_AndKeepDiagnosticComplaintsOutOfBrief()
    {
        Assert.False(StudioTurnStageRouter.ShouldEnforceHardSubtreeGuard("#inp_keyword", "这里搜索没生效"));
        Assert.False(StudioTurnStageRouter.ShouldEnforceHardSubtreeGuard("#list_items", "这里一直在加载"));
        // Studio AI v2: clicked element is always a soft focus hint, never a hard Zone 3 edit lock.
        Assert.False(StudioTurnStageRouter.ShouldEnforceHardSubtreeGuard("#inp_keyword", "把这个字号改大一点"));

        Assert.True(StudioTurnStageRouter.ShouldPreferInitialClarification(
            "做一个热点资讯格子",
            hasExistingManifest: false,
            recentMessages: []));
        Assert.True(StudioTurnStageRouter.ShouldPreferInitialClarification(
            "做一个股票推荐格子，展示各大股票的热门榜单。样式要高级，要有趋势图展示",
            hasExistingManifest: false,
            recentMessages: []));
        Assert.False(StudioTurnStageRouter.ShouldPreferInitialClarification(
            "做一个每周课表与日程格子：按节次 × 周一到周五网格展示课程（可在设置开启周末列），点击格子可添加或编辑课程名、地点、时间与配色，并支持今日课程高亮与周课时统计。",
            hasExistingManifest: false,
            recentMessages: []));

        IReadOnlyList<StudioChatMessage> postClarifyHistory =
        [
            new StudioChatMessage { Id = "u1", Role = "user", Kind = "prompt", Text = "做一个热点资讯格子" },
            new StudioChatMessage { Id = "a1", Role = "assistant", Kind = "clarify", Text = "请确认数据来源与交互偏好：" }
        ];
        Assert.False(StudioTurnStageRouter.ShouldPreferInitialClarification(
            "按所选方案直接开始构建：数据来源=综合热榜",
            hasExistingManifest: false,
            recentMessages: postClarifyHistory));

        CustomWidgetAiProviderPreset glmPreset = CustomWidgetAiGeneratorService.FindProviderPreset("glm");
        var glmInvocation = new ResolvedAiInvocation(
            glmPreset,
            glmPreset.DefaultEndpoint,
            "glm-5.3",
            "sk-test",
            AiThinkingMode.Auto,
            UsesCustomEndpoint: false);

        // Turn-0 clarification & localized visual micro-edits disable thinking for instant response
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                glmInvocation,
                "做一个热点资讯格子",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: [],
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                glmInvocation,
                "把标题字号改大一点",
                hasExistingManifest: true,
                targetNodePath: "#title",
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));

        // Studio AI v2: Auto mode defaults to fast non-thinking tool flow (no fragile keyword-triggered forced thinking);
        // explicit user ThinkingMode.Enabled is honored.
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                glmInvocation,
                "这里搜索没生效",
                hasExistingManifest: true,
                targetNodePath: "#inp_keyword",
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                glmInvocation,
                "按所选方案直接开始构建：综合热榜 + 关键词搜索",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));
        Assert.Equal(
            AiThinkingMode.Enabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                glmInvocation with { ThinkingMode = AiThinkingMode.Enabled },
                "按所选方案直接开始构建：综合热榜 + 关键词搜索",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false));

        // Diagnostic complaints must NOT pollute brief.md ConfirmedRequirements
        var seedBrief = new CustomWidgetDesignBrief
        {
            Purpose = "科技热点资讯聚合卡片",
            ConfirmedRequirements = ["支持关键词过滤"]
        };
        CustomWidgetDesignBrief afterComplaint = StudioContextCompactor.SanitizeAndRollBrief(
            seedBrief,
            updates: null,
            newManifest: null,
            rawUserPrompt: "没有任何数据",
            startDraftIdea: seedBrief.Purpose,
            isInitialOrEmptyBrief: false);
        Assert.DoesNotContain("没有任何数据", afterComplaint.ConfirmedRequirements);
    }

    [Fact]
    public async Task TransportDialectResilience_AndStableToolsPrefix_PreserveToolsOnReasoningHistoryAndUnsupportedThinkingErrors()
    {
        static string SerializeTools(bool hasWorkspace, bool allowDiscovery, bool allowRead)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                StudioToolTransport.WriteOpenAiToolsArray(writer);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        string round0Tools = SerializeTools(hasWorkspace: false, allowDiscovery: true, allowRead: false);
        string round2Tools = SerializeTools(hasWorkspace: true, allowDiscovery: false, allowRead: true);
        Assert.Equal(round0Tools, round2Tools);

        CustomWidgetAiProviderPreset dsPreset = CustomWidgetAiGeneratorService.FindProviderPreset("deepseek");
        var dsFlashAuto = new ResolvedAiInvocation(
            dsPreset,
            dsPreset.DefaultEndpoint,
            "deepseek-flash",
            "sk-ds",
            AiThinkingMode.Auto,
            UsesCustomEndpoint: false);
        Assert.True(AiChatTransportClient.ShouldIncludeAssistantReasoningContentInHistory(dsFlashAuto, includeThinkingParams: true));

        const string deepSeekReasoningHistoryErr =
            """{"error":{"message":"Missing reasoning_content field in the assistant message at message index 2. Please refer to https://api-docs.deepseek.com/guides/thinking_mode#tool-calls"}}""";
        Assert.True(AiChatTransportClient.ErrorRequiresReasoningContentInHistory(deepSeekReasoningHistoryErr));
        Assert.False(AiChatTransportClient.ErrorMentionsUnsupportedTools(deepSeekReasoningHistoryErr));

        // Endpoint rejecting vendor thinking param falls back cleanly WITHOUT stripping native tools
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "glm",
            ApiKey: "sk-glm-test",
            CustomEndpoint: "https://relay.example.com/v1/chat/completions",
            CustomModel: "glm-custom-lite",
            ThinkingMode: "enabled"));

        var seenPayloads = new List<string>();
        using var httpClient = new HttpClient(new StubHttpHandler(async (req, callIndex, ct) =>
        {
            string body = await req.Content!.ReadAsStringAsync(ct);
            seenPayloads.Add(body);
            if (callIndex == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Unknown parameter: 'thinking'. extra_forbidden"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            string sse = BuildToolCallsSse(
            [
                ("call_fin_clarify", "finish_turn", """{"kind":"clarify","reply":"请确认热点来源：","questions":[{"id":"src","header":"数据来源","options":[{"value":"weibo","label":"微博热搜","recommended":true},{"value":"hn","label":"Hacker News"}]}],"suggestions":["直接按推荐生成"]}""")
            ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }));

        var aiService = new DeskBoxAiService(credStore, httpClient: httpClient);
        var generator = new CustomWidgetAiGeneratorService(aiService);

        StudioTurnResult turn = await generator.RunStudioTurnAsync(
            providerId: "glm",
            userMessage: "做一个热点资讯格子",
            currentManifestJson: null,
            uiLanguage: "zh-CN",
            thinkingOverride: AiThinkingMode.Enabled);

        Assert.True(turn.Success, turn.ErrorMessage);
        Assert.Equal("clarify", turn.Kind);
        Assert.NotNull(turn.ClarifyQuestions);
        Assert.Single(turn.ClarifyQuestions!);
        Assert.Equal(2, seenPayloads.Count);

        using JsonDocument secondPayload = JsonDocument.Parse(seenPayloads[1]);
        Assert.False(secondPayload.RootElement.TryGetProperty("thinking", out _));
        Assert.True(secondPayload.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public void BehavioralSmokeVerifier_AndClarifyQuestionsStore_CatchBlankItemTemplateAndDeadInput_AndRoundTripClarifyQuestions()
    {
        const string brokenManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.hot-news",
          "name": "热点资讯",
          "collections": { "items": {} },
          "state": { "keyword": "" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["inp_keyword", "list_items"] },
            "inp_keyword": { "type": "Input", "bind": "state.keyword" },
            "list_items": {
              "type": "List",
              "collection": "items",
              "itemTemplate": "item_card"
            },
            "item_card": { "type": "Card", "children": ["item_title"] },
            "item_title": { "type": "Text", "text": "${$item.wrongField}" }
          }
        }
        """;
        const string sampleJson = """{"collections":{"items":[{"title":"周刊第 419 期","url":"https://example.com/1"}]}}""";

        CustomWidgetManifest brokenManifest = CustomWidgetManifestParser.Parse(brokenManifestJson).Manifest!;
        IReadOnlyList<CustomWidgetValidationDiagnostic> brokenSmoke = StudioBehavioralSmokeVerifier.Verify(
            brokenManifest,
            brokenManifestJson,
            logicJs: null,
            locales: null,
            sampleDataJson: sampleJson,
            cultureName: "zh-CN");
        Assert.Contains(brokenSmoke, static d => d.Code == "BehavioralBlankItemTemplate");
        Assert.Contains(brokenSmoke, static d => d.Code == "BehavioralDeadInputBinding");

        const string fixedManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.hot-news",
          "name": "热点资讯",
          "collections": { "items": {} },
          "state": { "keyword": "" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["inp_keyword", "list_items"] },
            "inp_keyword": { "type": "Input", "bind": "state.keyword" },
            "list_items": {
              "type": "List",
              "collection": "items",
              "filter": "!state.keyword || ($item.title || '').toLowerCase().includes(state.keyword.toLowerCase())",
              "itemTemplate": "item_card"
            },
            "item_card": { "type": "Card", "children": ["item_title"] },
            "item_title": { "type": "Text", "text": "${$item.title}" }
          }
        }
        """;
        CustomWidgetManifest fixedManifest = CustomWidgetManifestParser.Parse(fixedManifestJson).Manifest!;
        IReadOnlyList<CustomWidgetValidationDiagnostic> cleanSmoke = StudioBehavioralSmokeVerifier.Verify(
            fixedManifest,
            fixedManifestJson,
            logicJs: null,
            locales: null,
            sampleDataJson: sampleJson,
            cultureName: "zh-CN");
        Assert.Empty(cleanSmoke);

        // ClarifyQuestions + SubmittedSummary round-trip in CustomWidgetStudioSessionStore
        using var temp = new TempDirectoryScope();
        var store = new CustomWidgetStudioSessionStore(temp.Path);
        var msg = new StudioChatMessage
        {
            Id = "msg-c1",
            Role = "assistant",
            Kind = "clarify",
            Text = "请确认热点来源与展示方式：",
            ClarifyQuestions =
            [
                new StudioClarifyQuestionSpec(
                    "source",
                    "数据来源",
                    [
                        new StudioClarifyOptionSpec("tech", "科技周刊 RSS", Recommended: true),
                        new StudioClarifyOptionSpec("custom", "自定义接口")
                    ])
            ],
            SubmittedSummary = "已确认方案 · 数据来源: 科技周刊 RSS"
        };
        store.SaveCreateDraft(new StudioDraftSnapshot
        {
            StartDraftIdea = "做一个热点资讯格子",
            ChatMessages = [msg]
        });

        StudioDraftSnapshot? reloaded = store.TryLoadCreateDraft();
        Assert.NotNull(reloaded);
        StudioChatMessage reloadedMsg = Assert.Single(reloaded!.ChatMessages);
        Assert.Single(reloadedMsg.ClarifyQuestions);
        Assert.Equal("数据来源", reloadedMsg.ClarifyQuestions[0].Header);
        Assert.True(reloadedMsg.ClarifyQuestions[0].Options[0].Recommended);
        Assert.Equal("已确认方案 · 数据来源: 科技周刊 RSS", reloadedMsg.SubmittedSummary);
    }

    [Fact]
    public async Task RunawayParallelWriteBundleLoop_AndEmptyWorkspaceRead_AreBoundedWithoutFalseWarnings()
    {
        // 1. Direct unit check: 1 truncated manifest write_bundle + 256 sampleData echoes -> collapsed to 1 call, while valid 1+1 split is preserved
        const string truncatedManifestCall = """{"manifest":{"schemaVersion":1,"id":"custom.flashcard","name":"每日词卡","root":"main","elements":{"main":{"type":"Stack","children":["txt_hi"]},"txt_hi":{"type":"Text","text":"Hi"}}},"logicJs":"export function onInit(ctx) {""";
        var flood = new List<AiToolCallSpec>(258)
        {
            new("call_0", StudioToolTransport.ToolWriteBundle, truncatedManifestCall)
        };
        for (int i = 1; i <= 256; i++)
        {
            flood.Add(new AiToolCallSpec($"call_{i}", StudioToolTransport.ToolWriteBundle, $@"{{""sampleData"":{{""collections"":{{""words"":[{{""id"":""w{i}""}}]}}}}}}"));
        }
        flood.Add(new AiToolCallSpec("call_257", StudioToolTransport.ToolFinishTurn, """{"kind":"edit","reply":"done"}"""));

        IReadOnlyList<AiToolCallSpec> bounded = StudioToolTransport.NormalizeAndBoundRoundToolCalls(flood, out int dropped);
        Assert.Equal(2, bounded.Count);
        Assert.Equal(256, dropped);
        Assert.Equal(StudioToolTransport.ToolWriteBundle, bounded[0].Name);
        Assert.Equal(StudioToolTransport.ToolFinishTurn, bounded[1].Name);

        // Legitimate split: 1 manifest write_bundle + 1 logicJs write_bundle + 1 finish_turn -> all 3 preserved
        AiToolCallSpec[] validSplit =
        [
            new("c1", StudioToolTransport.ToolWriteBundle, """{"manifest":{"schemaVersion":1,"id":"custom.ok","name":"OK","root":"main","elements":{"main":{"type":"Stack","children":["txt_a"]},"txt_a":{"type":"Text","text":"A"}}}}"""),
            new("c2", StudioToolTransport.ToolWriteBundle, """{"logicJs":"export function onInit(ctx) {}"}"""),
            new("c3", StudioToolTransport.ToolFinishTurn, """{"kind":"edit","reply":"ok"}""")
        ];
        IReadOnlyList<AiToolCallSpec> keptSplit = StudioToolTransport.NormalizeAndBoundRoundToolCalls(validSplit, out int splitDropped);
        Assert.Equal(3, keptSplit.Count);
        Assert.Equal(0, splitDropped);

        // 2. End-to-end orchestrator check: Round 0 read_workspace on empty workspace closes cleanly (Completed, no invalid-args warning),
        //    Round 1 60x runaway write_bundle SSE stream is bounded at both accumulator & orchestrator boundaries, and Round 2 finishes cleanly.
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "glm",
            ApiKey: "sk-glm-test",
            CustomEndpoint: "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            CustomModel: "glm-5.3",
            ThinkingMode: "disabled"));

        var capturedPayloads = new List<string>();
        using var http = new HttpClient(new StubHttpHandler(async (req, callIdx, ct) =>
        {
            capturedPayloads.Add(await req.Content!.ReadAsStringAsync(ct));
            string sse;
            if (callIdx == 1)
            {
                sse = BuildToolCallsSse([("rw_0", StudioToolTransport.ToolReadWorkspace, "{}")]);
            }
            else if (callIdx == 2)
            {
                var runaway = new List<(string Id, string Name, string ArgsJson)>(60)
                {
                    ("wb_0", StudioToolTransport.ToolWriteBundle, truncatedManifestCall)
                };
                for (int k = 1; k < 60; k++)
                {
                    runaway.Add(($"wb_{k}", StudioToolTransport.ToolWriteBundle, $@"{{""sampleData"":{{""collections"":{{""words"":[{{""id"":""{k}""}}]}}}}}}"));
                }
                sse = BuildToolCallsSse(runaway.ToArray());
            }
            else
            {
                sse = BuildToolCallsSse(
                [
                    ("ps_0", StudioToolTransport.ToolPatchScript, """{"content":"export function onInit(ctx) {}"}"""),
                    ("ft_0", StudioToolTransport.ToolFinishTurn, """{"kind":"edit","reply":"已生成词卡","changeSummary":"新建每日词卡"}""")
                ]);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }));

        var aiService = new DeskBoxAiService(credStore, httpClient: http);
        var gen = new CustomWidgetAiGeneratorService(aiService);

        StudioTurnResult res = await gen.RunStudioTurnAsync(
            providerId: "glm",
            userMessage: "我要可测验翻卡的版本",
            currentManifestJson: null,
            uiLanguage: "zh-CN");

        Assert.True(res.Success, res.ErrorMessage);
        Assert.NotNull(res.ExecutionSteps);

        // r0-read_workspace must be Completed (not Warning with invalid args)
        StudioStreamStepItem r0Step = Assert.Single(res.ExecutionSteps!, static s => s.Id == "r0-read_workspace");
        Assert.Equal(StudioStreamStepState.Completed, r0Step.State);

        // Runaway write_bundle steps (-1..-59) must never appear in ExecutionSteps or inflate Round 2 history
        Assert.DoesNotContain(res.ExecutionSteps!, static s => s.Id.StartsWith("r1-write_bundle-", StringComparison.Ordinal));
        Assert.True(capturedPayloads[2].Length < 40_000, $"Expected bounded Round 2 payload, got {capturedPayloads[2].Length} chars.");
    }

    [Fact]
    public async Task TransientThinkingExhaustion_RetriesWithinRoundWithoutPoisoningRoute_AndExplicitDisabledIsHonored()
    {
        using var temp = new TempDirectoryScope();
        var credStore = new CustomWidgetAiCredentialStore(temp.Path);
        credStore.Upsert(new AiProviderCredentialEntry(
            ProviderId: "glm",
            ApiKey: "sk-glm-think",
            CustomEndpoint: "https://open.bigmodel.cn/api/paas/v4/chat/completions",
            CustomModel: "glm-5.3",
            ThinkingMode: "auto"));

        using var http = new HttpClient(new StubHttpHandler((_, callIdx, _) =>
        {
            if (callIdx == 1)
            {
                // Round 0 attempt 1: exhausts length budget in reasoning_content with empty visible/tool output
                string frame = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["choices"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["reasoning_content"] = "Thinking deeply about image converter layout..."
                            },
                            ["finish_reason"] = "length"
                        }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"data: {frame}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
                });
            }

            // Round 0 retry (non-thinking) succeeds immediately
            string okSse = BuildToolCallsSse(
            [
                ("wb_1", StudioToolTransport.ToolWriteBundle, """{"manifest":{"schemaVersion":1,"id":"custom.img-conv","name":"图片快转","root":"main","elements":{"main":{"type":"Stack","children":["txt_ready"]},"txt_ready":{"type":"Text","text":"Ready"}}}}"""),
                ("ft_1", StudioToolTransport.ToolFinishTurn, """{"kind":"edit","reply":"完成","changeSummary":"新建"}""")
            ]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okSse, Encoding.UTF8, "text/event-stream")
            });
        }));

        var aiService = new DeskBoxAiService(credStore, httpClient: http);
        var gen = new CustomWidgetAiGeneratorService(aiService);

        StudioTurnResult turn1 = await gen.RunStudioTurnAsync(
            providerId: "glm",
            userMessage: "直接开始构建：图片格式快转盒",
            currentManifestJson: null,
            uiLanguage: "zh-CN");
        Assert.True(turn1.Success, turn1.ErrorMessage);

        // Route must NOT be permanently poisoned for subsequent complex synthesis turns
        ResolvedAiInvocation baseGlm = aiService.ResolveEffectiveSettings("glm");
        Assert.False(aiService.IsThinkingExhaustedRoute(baseGlm));

        var postClarifyHistory = new List<StudioChatMessage>
        {
            new() { Id = "u1", Role = "user", Kind = "user", Text = "做一个每日外语词卡" },
            new() { Id = "a1", Role = "assistant", Kind = "clarify", Text = "请选择版本：" }
        };
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseGlm,
                userPrompt: "我要可测验翻卡的版本",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false,
                thinkingExhaustedOnRoute: aiService.IsThinkingExhaustedRoute(baseGlm)));
        Assert.Equal(
            AiThinkingMode.Enabled,
            StudioTurnStageRouter.ResolveAdaptiveStudioThinkingMode(
                baseGlm with { ThinkingMode = AiThinkingMode.Enabled },
                userPrompt: "我要可测验翻卡的版本",
                hasExistingManifest: false,
                targetNodePath: null,
                recentMessages: postClarifyHistory,
                isPostMutationRound: false,
                thinkingParamsUnsupportedOnRoute: false,
                thinkingExhaustedOnRoute: aiService.IsThinkingExhaustedRoute(baseGlm)));

        // Explicit user-configured Disabled in settings must be honored by UI override resolver
        credStore.UpdateStudioSettings("glm", thinkingMode: "disabled");
        ResolvedAiInvocation explicitDisabled = aiService.ResolveEffectiveSettings("glm");
        Assert.Equal(
            AiThinkingMode.Disabled,
            StudioAiTurnOrchestrator.ResolveAdaptiveStudioThinkingOverride(
                explicitDisabled,
                fetchedModels: null,
                userPrompt: "直接开始构建：复杂多栏实时看板",
                hasExistingManifest: false,
                targetNodePath: null,
                pinnedCapabilityIds: null));
    }

    [Fact]
    public void CompilerGuards_CatchStateActionPollution_ScriptArgsKeyMismatch_CapsuleDialects_UnclosedInterpolations_AndRichTextDupHeaders()
    {
        // 1. CustomWidgetManifestAutoFixer must not pollute manifest.state with "set" / "toggle" / "increment" from action types
        const string stateActionManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.img-box",
          "name": "图片格式快转盒",
          "state": { "format": "webp" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "padding": "12,10,12,12",
              "children": ["btn_switch"]
            },
            "btn_switch": {
              "type": "Button",
              "text": "切换 ${state.customSlot}",
              "onClick": [
                { "type": "state.set", "key": "format", "value": "png" },
                { "type": "state.toggle", "key": "customSlot" }
              ]
            }
          }
        }
        """;
        string autoWired = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(stateActionManifest);
        CustomWidgetManifest parsedAuto = CustomWidgetManifestParser.Parse(autoWired).Manifest!;
        Assert.True(parsedAuto.State.ContainsKey("customSlot"));
        Assert.False(parsedAuto.State.ContainsKey("set"));
        Assert.False(parsedAuto.State.ContainsKey("toggle"));

        // 2. StudioWorkspaceEngine normalizes capsule.lines + capsule.hint into capsule.summary
        var ws = new StudioWorkspaceEngine();
        StudioWorkspaceOpResult wsRes = ws.ReplaceManifestJson("""
        {
          "schemaVersion": 1,
          "id": "custom.capsule-dialect",
          "name": "图片快转",
          "capsule": {
            "title": "图片快转",
            "lines": ["WebP · 85%", "已处理 3 张"],
            "hint": "拖入图片即刻转换"
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_ok"] },
            "txt_ok": { "type": "Text", "text": "OK" }
          }
        }
        """);
        Assert.True(wsRes.Succeeded, wsRes.ErrorMessage);
        Assert.True(ws.NormalizeDomInPlace());
        CustomWidgetManifest normCapManifest = CustomWidgetManifestParser.Parse(ws.GetManifestJson()!).Manifest!;
        Assert.Equal("WebP · 85% · 已处理 3 张", normCapManifest.Capsule!.Summary);
        Assert.DoesNotContain("\"lines\"", ws.GetManifestJson()!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hint\"", ws.GetManifestJson()!, StringComparison.Ordinal);

        // 3. ScriptActionArgsKeyMismatch + UnclosedInterpolationExpression + RichText duplicate header
        const string flashcardManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.daily-word-flashcard",
          "name": "每日词卡·可测验翻卡",
          "state": { "correct": "0", "wrong": "0" },
          "script": { "entry": "logic.js" },
          "capsule": {
            "title": "每日词卡",
            "summary": "答对 ${state.correct} · 答错 ${state.wrong} · ${$com"
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "padding": "12,10,12,12",
              "children": ["hdr_wrap", "btn_a"]
            },
            "hdr_wrap": { "type": "Stack", "children": ["hdr_row"] },
            "hdr_row": { "type": "Stack", "orientation": "horizontal", "children": ["hdr_rt"] },
            "hdr_rt": {
              "type": "RichText",
              "spans": [
                { "text": "📖 每日词卡", "fontWeight": "bold" },
                { "text": " 1/5", "color": "secondary" }
              ]
            },
            "btn_a": {
              "type": "Button",
              "text": "A",
              "onClick": { "type": "script.run", "fn": "answer", "args": { "choice": "abandon" } }
            }
          }
        }
        """;
        const string mismatchedLogicJs = """
        export function onInit(ctx) {}
        export function flip(ctx) { ctx.state.flipped = 'true'; }
        export function answer(ctx) {
          const choice = ctx.event?.option ?? ctx.args?.option;
          if (!choice) return flip(ctx);
          ctx.state.correct = '1';
        }
        """;

        CustomWidgetWorkspaceValidationResult strictVal = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            flashcardManifest,
            mismatchedLogicJs,
            strictAuthoring: true);
        Assert.Contains(strictVal.Diagnostics, static d =>
            d.IsError && d.Code == CustomWidgetStrictAuthoringRules.ScriptActionArgsKeyMismatch);
        Assert.Contains(strictVal.Diagnostics, static d =>
            d.IsError && d.Code == CustomWidgetStrictAuthoringRules.UnclosedInterpolationExpression && d.Path == "$.capsule.summary");

        // Aligning logic.js to ctx.args?.choice clears ScriptActionArgsKeyMismatch cleanly
        const string alignedLogicJs = """
        export function onInit(ctx) {}
        export function answer(ctx) {
          const choice = ctx.args?.choice;
          if (choice) ctx.state.correct = '1';
        }
        """;
        CustomWidgetWorkspaceValidationResult alignedVal = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            flashcardManifest.Replace("${$com", "就绪", StringComparison.Ordinal),
            alignedLogicJs,
            strictAuthoring: true);
        Assert.DoesNotContain(alignedVal.Diagnostics, static d => d.IsError);

        // RichText leading span matching primary title prefix triggers layout.duplicate_header
        CustomWidgetHealthReport health = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            flashcardManifest,
            strictVal.Manifest!,
            storePayload: null,
            cultureName: "zh-CN",
            logicJs: alignedLogicJs);
        Assert.Contains(health.Issues, static i => i.Code == "layout.duplicate_header");
    }
}
