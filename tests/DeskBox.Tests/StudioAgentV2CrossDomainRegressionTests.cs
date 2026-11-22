using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Cross-domain regression suite validating DeskBox Studio AI v2 across 11 diverse widget domains:
/// CRUD forms/popovers, multi-tab calendars, local file export + runtime trace, live HTTP/RSS dashboards,
/// Canvas animation, system telemetry, countdown/audio, CLI long-running jobs, external MCP flat graph edits,
/// strict schemaVersion 1 cutover + dynamic semantic icon resolution, and model/cache/orchestrator robustness.
/// </summary>
public sealed class StudioAgentV2CrossDomainRegressionTests
{
    private sealed class StubHttpHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, Interlocked.Increment(ref _calls), cancellationToken);
    }

    [Fact]
    public void Scenario01_CrudFormPopover_DetectsMissingPopoverOpenAndPassesWhenWired()
    {
        const string brokenManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-crud-notes",
          "name": "Quick Notes",
          "icon": "📝",
          "state": { "editingId": "", "draftTitle": "", "draftBody": "" },
          "collections": { "notes": { "maxItems": 50 } },
          "script": { "entry": "logic.js" },
          "capsule": { "title": "Notes", "summary": "${collections.notes.count} notes", "glyph": "📝" },
          "root": "main",
          "popovers": {
            "editPop": { "title": "Edit Note", "width": 280, "root": "pop_body" }
          },
          "elements": {
            "main": { "type": "Stack", "padding": "12", "spacing": 8, "children": ["btn_add", "list_notes"] },
            "btn_add": {
              "type": "Button",
              "icon": "plus",
              "text": "New Note",
              "onClick": { "type": "script.run", "fn": "openEditor" }
            },
            "list_notes": { "type": "List", "items": "notes", "itemTemplate": "note_row" },
            "note_row": { "type": "Text", "text": "${$item.title}" },
            "pop_body": { "type": "Stack", "padding": "10", "spacing": 8, "children": ["inp_title", "inp_body", "btn_save"] },
            "inp_title": { "type": "Input", "bind": "state.draftTitle", "placeholder": "Title" },
            "inp_body": { "type": "TextArea", "bind": "state.draftBody", "placeholder": "Body" },
            "btn_save": {
              "type": "Button",
              "icon": "save",
              "text": "Save",
              "onClick": { "type": "script.run", "fn": "saveNote" }
            }
          }
        }
        """;

        const string brokenJs = """
        export function onInit(ctx) {}
        export function openEditor(ctx) {
          ctx.state.editingId = 'new';
          ctx.state.draftTitle = 'Untitled Note';
          ctx.state.draftBody = 'Draft body';
        }
        export function saveNote(ctx) {
          if (ctx.state.draftTitle) {
            ctx.collections.prepend('notes', { title: ctx.state.draftTitle, body: ctx.state.draftBody });
          }
          ctx.dispatch({ type: 'popover.close', popoverId: 'editPop' });
        }
        """;

        CustomWidgetParseResult parsedBroken = CustomWidgetManifestParser.Parse(brokenManifest);
        Assert.True(parsedBroken.IsValid, string.Join("; ", parsedBroken.Diagnostics.Select(static d => d.Message)));
        IReadOnlyList<CustomWidgetValidationDiagnostic> brokenFindings =
            StudioBehavioralSmokeVerifier.Verify(parsedBroken.Manifest!, brokenManifest, brokenJs, null, null);
        Assert.Contains(brokenFindings, static d => d.Code == "BehavioralFormEditMissingPopoverOpen");

        const string fixedJs = """
        export function onInit(ctx) {}
        export function openEditor(ctx) {
          ctx.state.editingId = 'new';
          ctx.state.draftTitle = 'Untitled Note';
          ctx.state.draftBody = 'Draft body';
          ctx.dispatch({ type: 'popover.open', popoverId: 'editPop' });
        }
        export function saveNote(ctx) {
          if (ctx.state.draftTitle) {
            ctx.collections.prepend('notes', { title: ctx.state.draftTitle, body: ctx.state.draftBody });
          }
          ctx.dispatch({ type: 'popover.close', popoverId: 'editPop' });
        }
        """;

        IReadOnlyList<CustomWidgetValidationDiagnostic> fixedFindings =
            StudioBehavioralSmokeVerifier.Verify(parsedBroken.Manifest!, brokenManifest, fixedJs, null, null);
        Assert.DoesNotContain(fixedFindings, static d => d.Code == "BehavioralFormEditMissingPopoverOpen");
    }

    [Fact]
    public void Scenario02_MultiTabCalendar_RejectsUnreachableTabAndDuplicateSiblingButtons()
    {
        const string brokenCalendarManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-schedule",
          "name": "Schedule",
          "icon": "📅",
          "state": { "viewMode": "month", "selectedDate": "2026-11-19" },
          "collections": { "events": { "maxItems": 100 } },
          "capsule": { "title": "Schedule", "summary": "${state.selectedDate}", "glyph": "📅" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "spacing": 8, "children": ["nav_row", "seg_mode", "tab_month", "tab_dead"] },
            "nav_row": { "type": "Stack", "orientation": "horizontal", "spacing": 6, "children": ["btn_dup1", "btn_dup2"] },
            "seg_mode": {
              "type": "Segmented",
              "bind": "state.viewMode",
              "options": [
                { "value": "month", "label": "Month" },
                { "value": "week", "label": "Week" }
              ]
            },
            "btn_dup1": { "type": "Button", "text": "Today", "onClick": { "type": "state.set", "key": "selectedDate", "value": "2026-11-19" } },
            "btn_dup2": { "type": "Button", "text": "Today", "onClick": { "type": "state.set", "key": "selectedDate", "value": "2026-11-19" } },
            "tab_month": { "type": "CalendarGrid", "when": "${state.viewMode == 'month'}", "bind": "state.selectedDate", "collection": "events" },
            "tab_dead": { "type": "Text", "when": "${state.viewMode == 'year'}", "text": "Yearly summary" }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(brokenCalendarManifest);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(static d => d.Message)));
        IReadOnlyList<CustomWidgetValidationDiagnostic> findings =
            StudioBehavioralSmokeVerifier.Verify(parsed.Manifest!, brokenCalendarManifest, null, null, null);
        Assert.Contains(findings, static d => d.Code == "BehavioralUnreachableStateBranch");
        Assert.Contains(findings, static d => d.Code == "BehavioralDuplicateSiblingButton");
    }

    [Fact]
    public void Scenario03_LocalFileExport_DualTierManagedPathAndRuntimeTraceScrubbing()
    {
        bool okRelative = CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            rawPath: "exports/report-2026.csv",
            widgetFolderSlug: "custom.finance-ledger",
            forWrite: true,
            out string fullPath,
            out bool isManaged,
            out string? err);
        Assert.True(okRelative, err);
        Assert.True(isManaged);
        Assert.Contains("DeskBox", fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("custom-finance-ledger", fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("exports", "report-2026.csv"), fullPath, StringComparison.OrdinalIgnoreCase);

        bool blockedTraversal = CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            rawPath: "../../Windows/System32/drivers/etc/hosts",
            widgetFolderSlug: "custom.finance-ledger",
            forWrite: true,
            out _,
            out _,
            out _);
        Assert.False(blockedTraversal);

        var trace = new CustomWidgetRuntimeTraceBuffer();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        trace.Record(
            actionType: "file.writeText",
            targetSummary: "exports/report.csv",
            ok: false,
            detail: $"Write failed at {Path.Combine(userProfile, "Documents", "secret.txt")} with Bearer sk-secret1234567890");
        string promptSlice = trace.FormatForAiPrompt(5);
        Assert.Contains("file.writeText", promptSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret1234567890", promptSlice, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            Assert.DoesNotContain(userProfile, promptSlice, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Scenario04_LiveHttpRssDashboard_FlatV2EditManifestUpdatesElementsAtomically()
    {
        const string rssManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-rss",
          "name": "Tech Radar",
          "icon": "📰",
          "state": { "keyword": "" },
          "collections": { "articles": { "maxItems": 20 } },
          "sources": [
            { "id": "feed", "type": "rss", "url": "https://hnrss.org/frontpage", "targetCollection": "articles", "intervalMs": 900000 }
          ],
          "capsule": { "title": "Tech Radar", "summary": "${state.topHeadline ?? 'Ready'}", "glyph": "📰" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "spacing": 8, "children": ["inp_search", "list_articles"] },
            "inp_search": { "type": "Input", "bind": "state.keyword", "placeholder": "Filter headlines..." },
            "list_articles": {
              "type": "List",
              "items": "articles",
              "filter": "!state.keyword || ($item.title || '').toLowerCase().includes(state.keyword.toLowerCase())",
              "itemTemplate": "row_article"
            },
            "row_article": { "type": "Text", "text": "${$item.title}", "maxLines": 2 }
          }
        }
        """;

        var ws = new StudioWorkspaceEngine(rssManifest);
        StudioWorkspaceOpResult editRes = ws.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["btn_refresh"] = new JsonObject
                {
                    ["type"] = "IconButton",
                    ["icon"] = "refresh",
                    ["onClick"] = new JsonObject { ["type"] = "source.refresh", ["targetId"] = "feed" }
                },
                ["main"] = new JsonObject
                {
                    ["type"] = "Stack",
                    ["padding"] = "12",
                    ["spacing"] = 8,
                    ["children"] = new JsonArray(
                        (JsonNode?)JsonValue.Create("btn_refresh"),
                        (JsonNode?)JsonValue.Create("inp_search"),
                        (JsonNode?)JsonValue.Create("list_articles"))
                }
            }));

        Assert.True(editRes.Succeeded, editRes.ErrorMessage);
        CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(ws.GetManifestJson()!);
        Assert.True(reparsed.IsValid, string.Join("; ", reparsed.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(3, reparsed.Manifest!.View.Children.Count);
        Assert.Equal("IconButton", reparsed.Manifest.View.Children[0].Type);
        Assert.Equal("\uE72C", reparsed.Manifest.View.Children[0].Icon);
    }

    [Fact]
    public void Scenario05_CanvasAnimation_CompilesFlatV2WithTickAndPointerHandlers()
    {
        const string canvasManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-canvas-pong",
          "name": "Orbital Pong",
          "icon": "🎮",
          "state": { "score": 0, "paused": false },
          "script": { "entry": "logic.js", "tickIntervalMs": 100 },
          "capsule": { "title": "Pong", "summary": "Score ${state.score}", "glyph": "🎮" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "10", "spacing": 8, "children": ["stage_canvas", "btn_toggle"] },
            "stage_canvas": {
              "type": "Canvas",
              "width": 240,
              "height": 140,
              "viewBox": "0 0 240 140",
              "elementsSource": "${$computed.shapes}",
              "onPointerDown": { "type": "script.run", "fn": "onTap" }
            },
            "btn_toggle": {
              "type": "Button",
              "icon": "play",
              "text": "${state.paused ? 'Resume' : 'Pause'}",
              "onClick": { "type": "script.run", "fn": "togglePause" }
            }
          }
        }
        """;

        const string canvasJs = """
        export function onInit(ctx) {
          ctx.computed.shapes = [{ type: 'circle', cx: 120, cy: 70, r: 10, fill: '#38BDF8' }];
        }
        export function onTick(ctx) {
          if (ctx.state.paused) return;
          ctx.computed.shapes = [{ type: 'circle', cx: 124, cy: 70, r: 10, fill: '#38BDF8' }];
        }
        export function onTap(ctx) {
          const nx = Number(ctx.event?.normX ?? 0.5);
          ctx.state.score = Number(ctx.state.score || 0) + (nx > 0.5 ? 1 : 0);
        }
        export function togglePause(ctx) {
          ctx.state.paused = !ctx.state.paused;
        }
        """;

        CustomWidgetParseResult res = CustomWidgetScriptValidator.ValidateWorkspaceBundle(canvasManifest, canvasJs);
        Assert.True(res.IsValid, string.Join("; ", res.Diagnostics.Select(static d => $"{d.Path}: {d.Message}")));
        Assert.Equal("Canvas", res.Manifest!.View.Children[0].Type);
        Assert.Equal("\uE768", res.Manifest.View.Children[1].Icon);
    }

    [Fact]
    public void Scenario06_SystemTelemetry_RemoveElementCleansDanglingParentReferences()
    {
        const string sysManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-sys",
          "name": "Sys Monitor",
          "icon": "📊",
          "sources": [
            { "id": "sys", "type": "systemMetrics", "intervalMs": 2000 },
            { "id": "drv", "type": "storageDrives", "targetCollection": "drives", "intervalMs": 15000 }
          ],
          "capsule": { "title": "CPU", "summary": "${$sys.cpu.percent}%", "glyph": "📊" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "spacing": 8, "children": ["stat_cpu", "stat_mem", "spark_cpu"] },
            "stat_cpu": { "type": "StatCard", "title": "CPU", "value": "${$sys.cpu.percent}%" },
            "stat_mem": { "type": "StatCard", "title": "RAM", "value": "${$sys.memory.percent}%" },
            "spark_cpu": { "type": "Sparkline", "sparkline": "$sys.cpu.history", "height": 32 }
          }
        }
        """;

        var ws = new StudioWorkspaceEngine(sysManifest);
        StudioWorkspaceOpResult removeRes = ws.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            RemoveElements: ["spark_cpu"]));
        Assert.True(removeRes.Succeeded, removeRes.ErrorMessage);

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(ws.GetManifestJson()!);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(2, parsed.Manifest!.View.Children.Count);
        Assert.DoesNotContain(parsed.Diagnostics, static d => d.Code == "ElementGraphDanglingRef");
    }

    [Fact]
    public void Scenario07_CountdownAudioAndSemanticIcons_ResolvesAliasesAndSuggestsTypos()
    {
        Assert.True(CustomWidgetSemanticIcons.TryResolve("play", out string playGlyph));
        Assert.Equal("\uE768", playGlyph);
        Assert.True(CustomWidgetSemanticIcons.TryResolve("folder-open", out string folderGlyph));
        Assert.Equal("\uE838", folderGlyph);
        Assert.True(CustomWidgetSemanticIcons.TryReverseLookup("\uE768", out string? canonicalPlay));
        Assert.Equal("play", canonicalPlay);

        const string typoManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.v2-timer",
          "name": "Focus Timer",
          "icon": "⏱️",
          "sources": [{ "type": "pomodoro", "intervalMs": 1000 }],
          "capsule": { "title": "Timer", "summary": "${$pomodoro.remainingText}", "glyph": "⏱️" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "spacing": 8, "children": ["btn_start"] },
            "btn_start": {
              "type": "Button",
              "icon": "playy",
              "text": "Start",
              "onClick": { "type": "pomodoro.toggle" }
            }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(typoManifest);
        Assert.True(parsed.IsValid);
        CustomWidgetValidationDiagnostic? iconWarn = parsed.Diagnostics.FirstOrDefault(static d => d.Code == "UnknownSemanticIconName");
        Assert.NotNull(iconWarn);
        Assert.Contains("play", iconWarn!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scenario08_CliLongRunningJob_ActionSchemaValidatorChecksDispatchedActions()
    {
        CustomWidgetManifest cliManifest = CustomWidgetManifestParser.Parse("""
            {
              "schemaVersion": 1,
              "id": "custom.v2-cli",
              "name": "CLI Runner",
              "icon": "💻",
              "capsule": { "title": "CLI", "summary": "Ready", "glyph": "💻" },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["txt"] },
                "txt": { "type": "Text", "text": "Ready" }
              }
            }
            """).Manifest!;
        var exportedFns = new HashSet<string>(StringComparer.Ordinal) { "onBuildDone" };

        const string validDispatch = """
        [
          { "type": "command.run", "id": "build", "jobKey": "buildJob", "executable": "dotnet", "arguments": ["--version"], "longRunning": true, "onComplete": "onBuildDone" },
          { "type": "job.cancel", "jobKey": "buildJob" }
        ]
        """;
        var validDiags = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetActionSchemaValidator.ValidateRawDispatchedActionsJson(
            validDispatch,
            "runBuild",
            cliManifest,
            exportedFns,
            validDiags);
        Assert.DoesNotContain(validDiags, static d => d.IsError);

        const string invalidDispatch = """
        [
          { "type": "nonexistent.magic", "foo": "bar" },
          { "type": "file.writeText", "path": "../../../Windows/win.ini", "text": "bad" }
        ]
        """;
        var invalidDiags = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetActionSchemaValidator.ValidateRawDispatchedActionsJson(
            invalidDispatch,
            "badDispatch",
            cliManifest,
            exportedFns,
            invalidDiags);
        Assert.True(invalidDiags.Count(static d => d.IsError) >= 2);
    }

    [Fact]
    public async Task Scenario09_ExternalMcpV2_Supports12DomainPacksAndUpsertElementsStaging()
    {
        // 1. Verify all 37 atomic capabilities map into exactly 12 Domain Packs
        Assert.Equal(12, CustomWidgetCapabilityRegistry.AllDomainPacks.Count);
        foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
        {
            string? packId = CustomWidgetCapabilityRegistry.ResolveDomainPackId(spec.Id);
            Assert.False(string.IsNullOrWhiteSpace(packId), $"Atomic capability '{spec.Id}' is not assigned to any Domain Pack.");
        }

        // 2. MCP deskbox_get_capability_spec resolves Domain Pack IDs directly
        string? packResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deskbox_get_capability_spec","arguments":{"capabilityId":"local-files-and-cli"}}}""",
            cancellationToken: CancellationToken.None);
        Assert.NotNull(packResp);
        Assert.Contains("file.writeText", packResp, StringComparison.Ordinal);

        // 3. Bridge server upsert with flat v2 upsertElements/patchRoot stages cleanly
        const string baseManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.mcp-v2",
          "name": "MCP V2",
          "icon": "🧪",
          "capsule": { "title": "MCP V2", "summary": "Ready", "glyph": "🧪" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "children": ["txt_hello"] },
            "txt_hello": { "type": "Text", "text": "Hello" }
          }
        }
        """;

        string? capturedBridgePayload = null;
        string? mcpUpsertResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            """
            {
              "jsonrpc": "2.0",
              "id": 2,
              "method": "tools/call",
              "params": {
                "name": "deskbox_upsert_widget",
                "arguments": {
                  "widgetId": "custom.mcp-v2",
                  "upsertElements": { "txt_hello": { "type": "Text", "text": "Hello V2!" } },
                  "patchRoot": { "name": "MCP V2 Updated" }
                }
              }
            }
            """,
            directBridgeSender: (writePayload, _) =>
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    writePayload(w);
                    w.WriteEndObject();
                }
                capturedBridgePayload = Encoding.UTF8.GetString(ms.ToArray());
                return Task.FromResult<string?>("""{"ok":true,"result":{"staged":true,"stageId":"stg_v2_test"}}""");
            },
            cancellationToken: CancellationToken.None);

        Assert.NotNull(capturedBridgePayload);
        Assert.Contains("\"upsertElements\"", capturedBridgePayload, StringComparison.Ordinal);
        Assert.Contains("\"patchRoot\"", capturedBridgePayload, StringComparison.Ordinal);
        Assert.NotNull(mcpUpsertResp);
        Assert.Contains("stg_v2_test", mcpUpsertResp, StringComparison.Ordinal);
        Assert.True(CustomWidgetManifestParser.Parse(baseManifest).IsValid);
    }

    [Fact]
    public async Task Scenario10_StrictSchemaV1Cutover_RejectsLegacyViewAndSchemaV2_AndResolvesDynamicSemanticIcons()
    {
        // 1. Legacy nested "view" is strictly rejected
        const string legacyViewManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.community-legacy",
          "name": "Legacy Community Card",
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "Legacy" }
            ]
          }
        }
        """;
        CustomWidgetParseResult legacyViewParsed = CustomWidgetManifestParser.Parse(legacyViewManifest);
        Assert.False(legacyViewParsed.IsValid);
        Assert.Contains(legacyViewParsed.Diagnostics, static d => d.IsError && d.Code == "LegacyViewNotSupported");

        // 2. Legacy popover "children" and "schemaVersion": 2 are strictly rejected
        const string legacyPopoverAndV2Manifest = """
        {
          "schemaVersion": 2,
          "id": "custom.legacy-v2-pop",
          "name": "Legacy V2 Pop",
          "root": "main",
          "popovers": {
            "addPop": {
              "title": "Add Item",
              "width": 280,
              "children": [
                { "type": "Text", "text": "Old" }
              ]
            }
          },
          "elements": {
            "main": { "type": "Text", "text": "Host" }
          }
        }
        """;
        CustomWidgetParseResult v2PopParsed = CustomWidgetManifestParser.Parse(legacyPopoverAndV2Manifest);
        Assert.False(v2PopParsed.IsValid);
        Assert.Contains(v2PopParsed.Diagnostics, static d => d.IsError && d.Code == "UnsupportedSchemaVersion");
        Assert.Contains(v2PopParsed.Diagnostics, static d => d.IsError && d.Code == "LegacyViewNotSupported");

        // 3. Static semantic icon resolution covers all expanded template & capability icon names
        Assert.Equal("\uE738", CustomWidgetManifestParser.NormalizeGlyph("minus"));
        Assert.Equal("\uE81D", CustomWidgetManifestParser.NormalizeGlyph("gps"));
        Assert.Equal("\uE8A1", CustomWidgetManifestParser.NormalizeGlyph("article"));
        Assert.Equal("\uE8AB", CustomWidgetManifestParser.NormalizeGlyph("swap"));
        Assert.Equal("\uE8D1", CustomWidgetManifestParser.NormalizeGlyph("quick-note"));
        Assert.Equal("\uE8D2", CustomWidgetManifestParser.NormalizeGlyph("font-size"));
        Assert.Equal("\uE8E4", CustomWidgetManifestParser.NormalizeGlyph("align-left"));
        Assert.Equal("\uE8E5", CustomWidgetManifestParser.NormalizeGlyph("open-file"));
        Assert.Equal("\uE8F1", CustomWidgetManifestParser.NormalizeGlyph("library"));
        Assert.Equal("\uE8F2", CustomWidgetManifestParser.NormalizeGlyph("dictionary"));
        Assert.Equal("\uE9D9", CustomWidgetManifestParser.NormalizeGlyph("soundscape"));

        // 4. Dynamic runtime semantic icon resolution through CustomWidgetViewModel (NormalizeRuntimeGlyphOrFallback)
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-v1-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            const string dynamicIconManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.dynamic-semantic-icons",
              "name": "Dynamic Semantic Icons",
              "icon": "🎧",
              "state": { "playing": "false", "capsuleIcon": "soundscape" },
              "capsule": {
                "title": "Focus Audio",
                "summary": "Ambient",
                "glyph": "${state.capsuleIcon}",
                "primaryAction": {
                  "icon": "${state.playing == 'true' ? 'pause' : 'play'}",
                  "tooltip": "Toggle playback",
                  "onClick": { "type": "state.set", "key": "playing", "value": "true" }
                }
              },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "padding": "12", "children": ["status_txt"] },
                "status_txt": { "type": "Text", "text": "${state.capsuleIcon}" }
              }
            }
            """;

            CustomWidgetParseResult dynamicParsed = CustomWidgetManifestParser.Parse(dynamicIconManifest);
            Assert.True(dynamicParsed.IsValid, string.Join("; ", dynamicParsed.Diagnostics.Select(static d => d.Message)));
            Assert.Empty(dynamicParsed.Diagnostics);
            Assert.Equal(1, dynamicParsed.Manifest!.SchemaVersion);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("custom_dynamic_icons", widgetsDir);
            await store.WriteWorkspaceBundleAsync(dynamicIconManifest, logicJs: null, locales: null);

            var config = new WidgetConfig
            {
                Id = "custom_dynamic_icons",
                Name = "Dynamic Semantic Icons",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();
            vm.OnCompactStateChanged(collapsed: true);

            Assert.Equal("\uE9D9", vm.CapsuleGlyph); // soundscape
            Assert.Equal("\uE768", vm.CapsulePrimaryActionGlyph); // play

            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec(Type: "state.set", Key: "playing", Value: "true"),
                new CustomWidgetActionSpec(Type: "state.set", Key: "capsuleIcon", Value: "gps")
            ]);

            Assert.Equal("\uE81D", vm.CapsuleGlyph); // gps
            Assert.Equal("\uE769", vm.CapsulePrimaryActionGlyph); // pause
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Scenario11_ModelRobustness_ThreeTierCache_BriefPurity_AndSingleCoreBlockLoop()
    {
        // 1. DeepSeek reasoning_content preserved on assistant tool turn even when thinking is Disabled/downgraded
        var aiService = new DeskBoxAiService();
        ResolvedAiInvocation deepSeekDowngraded = aiService.ResolveEffectiveSettings(
            "deepseek",
            modelOverride: "deepseek-chat",
            apiKeyOverride: "sk-test",
            thinkingOverride: AiThinkingMode.Disabled);
        IReadOnlyList<AiChatTurnMessage> deepSeekTurns =
        [
            new AiChatTurnMessage(
                "assistant",
                Content: null,
                ReasoningContent: "Let me inspect the local-files-and-cli pack first.",
                ToolCalls: [new AiToolCallSpec("tc_1", StudioToolTransport.ToolInspectCapabilities, """{"ids":["local-files-and-cli"]}""")]),
            new AiChatTurnMessage("tool", Content: "pack docs", ToolCallId: "tc_1", ToolName: StudioToolTransport.ToolInspectCapabilities)
        ];
        byte[] dsPayload = AiChatTransportClient.BuildChatRequestPayload(
            deepSeekDowngraded,
            systemPrompt: "Sys",
            userPrompt: "User",
            stream: true,
            includeThinkingParams: false,
            isProbe: false,
            structuredTurns: deepSeekTurns,
            includeStudioTools: true);
        using (JsonDocument dsDoc = JsonDocument.Parse(dsPayload))
        {
            Assert.Equal(
                "Let me inspect the local-files-and-cli pack first.",
                dsDoc.RootElement.GetProperty("messages")[2].GetProperty("reasoning_content").GetString());
        }

        // 2. 45-second reasoning-only soft timeout aborts stalled thinking even below 20k chars
        Assert.True(AiChatTransportClient.ShouldAbortForRunawayReasoning(
            totalUiContentChars: 0,
            hasToolCalls: false,
            totalThinkingChars: 1200,
            elapsedMs: 46_000,
            isReasoningOnlyModel: false,
            thinkingMode: AiThinkingMode.Enabled));
        Assert.False(AiChatTransportClient.ShouldAbortForRunawayReasoning(
            totalUiContentChars: 10,
            hasToolCalls: false,
            totalThinkingChars: 1200,
            elapsedMs: 46_000,
            isReasoningOnlyModel: false,
            thinkingMode: AiThinkingMode.Enabled));

        // 3. Repair-round compaction MUST preserve the active latest write_bundle (only compact on explicit HTTP 400 overflow)
        string latestBundleArgs = new string('z', 2_000);
        IReadOnlyList<AiChatTurnMessage> singleBundleTurns =
        [
            new AiChatTurnMessage("assistant", ToolCalls: [new AiToolCallSpec("tc_latest", StudioToolTransport.ToolWriteBundle, latestBundleArgs)]),
            new AiChatTurnMessage("tool", Content: "validation failed", ToolCallId: "tc_latest", ToolName: StudioToolTransport.ToolWriteBundle)
        ];
        IReadOnlyList<AiChatTurnMessage> repairCompacted = StudioContextCompactor.CompactSupersededStructuredTurns(
            singleBundleTurns,
            compactAllWriteBundles: true,
            forceCompactLatestForOverflow: false);
        Assert.Equal(latestBundleArgs, repairCompacted[0].ToolCalls![0].ArgumentsJson);

        IReadOnlyList<AiChatTurnMessage> overflowCompacted = StudioContextCompactor.CompactSupersededStructuredTurns(
            singleBundleTurns,
            compactAllWriteBundles: true,
            forceCompactLatestForOverflow: true);
        Assert.Contains("_compacted", overflowCompacted[0].ToolCalls![0].ArgumentsJson, StringComparison.Ordinal);

        // 4. brief.md stays clean: raw user follow-up bug complaints never leak into ConfirmedRequirements when updates is null
        var existingBrief = new CustomWidgetDesignBrief
        {
            Purpose = "个人记账本",
            ConfirmedRequirements = ["支持按月统计"]
        };
        CustomWidgetDesignBrief afterBugReport = StudioContextCompactor.SanitizeAndRollBrief(
            existingBrief,
            updates: null,
            newManifest: null,
            rawUserPrompt: "打开导出文件夹按钮点了没反应，麻烦再查一下",
            startDraftIdea: "个人记账本",
            isInitialOrEmptyBrief: false);
        Assert.Single(afterBugReport.ConfirmedRequirements);
        Assert.Equal("支持按月统计", afterBugReport.ConfirmedRequirements[0]);

        // 5. 3-tier KV-cache prefix + Single-Core Fenced-Block orchestration end-to-end
        string blockResponse = """
        ```deskbox-meta
        { "kind": "edit", "reply": "已创建极简看板。", "changeSummary": "创建极简看板" }
        ```
        ```deskbox-manifest
        {
          "schemaVersion": 1,
          "id": "custom.v2-block-singlecore",
          "name": "Block Single Core",
          "icon": "⚡",
          "capsule": { "title": "Single Core", "summary": "OK", "glyph": "⚡" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "children": ["txt_ok"] },
            "txt_ok": { "type": "Text", "text": "Unified Loop Works" }
          }
        }
        ```
        """;
        Assert.True(StudioToolTransport.TryExtractBlockToolCalls(blockResponse, out List<AiToolCallSpec> extractedCalls));
        Assert.Equal(2, extractedCalls.Count);
        Assert.Equal(StudioToolTransport.ToolWriteBundle, extractedCalls[0].Name);
        Assert.Equal(StudioToolTransport.ToolFinishTurn, extractedCalls[1].Name);

        string tempCredDir = Path.Combine(Path.GetTempPath(), $"deskbox-v2-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCredDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempCredDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-test",
                CustomEndpoint: "https://mock.local/v1/chat/completions",
                CustomModel: "gpt-4.1",
                ThinkingMode: "disabled"));

            string? capturedBody = null;
            using var httpClient = new HttpClient(new StubHttpHandler(async (req, _, ct) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                string sse = "data: " + JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            delta = new { content = blockResponse },
                            finish_reason = "stop"
                        }
                    }
                }) + "\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }));

            var gen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, httpClient: httpClient));
            StudioTurnResult turn = await gen.RunStudioTurnAsync(
                providerId: "openai",
                userMessage: "创建一个极简看板",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: "⚡",
                allowSampleData: false,
                uiLanguage: "zh-CN",
                sessionCapabilityPackIds: ["script-sandbox", "local-files-and-cli"]);

            Assert.True(turn.Success, turn.ErrorMessage);
            Assert.Equal("edit", turn.Kind);
            Assert.NotNull(turn.ParseResult?.Manifest);
            Assert.Equal(1, turn.ParseResult!.Manifest!.SchemaVersion);
            Assert.NotNull(turn.DiscoveredCapabilityPackIds);
            Assert.Contains("local-files-and-cli", turn.DiscoveredCapabilityPackIds!);
            Assert.Contains("script-sandbox", turn.DiscoveredCapabilityPackIds!);

            // Verify Tier-2 KV-cacheable prefix messages were emitted in canonical sorted order before volatile user prompt
            Assert.NotNull(capturedBody);
            using JsonDocument bodyDoc = JsonDocument.Parse(capturedBody!);
            JsonElement msgs = bodyDoc.RootElement.GetProperty("messages");
            Assert.Equal(4, msgs.GetArrayLength());
            Assert.Equal("system", msgs[0].GetProperty("role").GetString());
            Assert.Equal("user", msgs[1].GetProperty("role").GetString());
            Assert.Equal("assistant", msgs[2].GetProperty("role").GetString());
            Assert.Equal("user", msgs[3].GetProperty("role").GetString());
            string tier2Text = msgs[1].GetProperty("content").GetString() ?? string.Empty;
            int idxFiles = tier2Text.IndexOf("`local-files-and-cli`", StringComparison.Ordinal);
            int idxScript = tier2Text.IndexOf("`script-sandbox`", StringComparison.Ordinal);
            Assert.True(idxFiles >= 0 && idxScript > idxFiles, "Tier-2 packs must be sorted canonically for KV-cache stability.");
        }
        finally
        {
            try { Directory.Delete(tempCredDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Scenario12_DotArrayIndexJsonPath_AndStrictUnknownSourceTypeError()
    {
        // 1. Dot-separated numeric array indexing (e.g. Datamuse "defs.0") works alongside bracket syntax "defs[0]"
        using JsonDocument doc = JsonDocument.Parse("""
        {
          "word": "serene",
          "defs": ["adj\tcalm, peaceful, and untroubled", "n\tan expanse of clear sky"],
          "data": { "items": [{ "title": "First" }, { "title": "Second" }] },
          "matrix": [["a0", "a1"], ["b0", "b1"]]
        }
        """);

        Assert.True(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(doc.RootElement, "defs.0", out JsonElement def0));
        Assert.Equal("adj\tcalm, peaceful, and untroubled", def0.GetString());
        Assert.True(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(doc.RootElement, "defs[1]", out JsonElement def1));
        Assert.Equal("n\tan expanse of clear sky", def1.GetString());
        Assert.True(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(doc.RootElement, "data.items.1.title", out JsonElement item1));
        Assert.Equal("Second", item1.GetString());
        Assert.True(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(doc.RootElement, "matrix.1[0]", out JsonElement m10));
        Assert.Equal("b0", m10.GetString());
        Assert.False(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(doc.RootElement, "defs.5", out _));

        using JsonDocument rootArrDoc = JsonDocument.Parse("""[{"word":"lucid"},{"word":"vivid"}]""");
        Assert.True(CustomWidgetNetworkSourceEngine.TryResolveJsonPath(rootArrDoc.RootElement, "0.word", out JsonElement w0));
        Assert.Equal("lucid", w0.GetString());

        // 2. Malformed wrapped source entry (empty type) and unknown source type MUST be blocking errors (IsError == true)
        const string wrappedSourceManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.bad-source-wrap",
          "name": "Bad Source",
          "icon": "📖",
          "sources": [
            { "wotd": { "type": "httpJson", "url": "https://api.datamuse.com/words?ml=happy" } },
            { "id": "x", "type": "bogusSource" }
          ],
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt"] },
            "txt": { "type": "Text", "text": "Hi" }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(wrappedSourceManifest);
        Assert.False(parsed.IsValid);
        List<CustomWidgetValidationDiagnostic> sourceErrors = parsed.Diagnostics
            .Where(static d => d.IsError && d.Code == "UnknownSourceType")
            .ToList();
        Assert.Equal(2, sourceErrors.Count);
    }

    [Fact]
    public async Task Scenario13_PureConsultationAndUnsupportedRequests_EndToEndRouting()
    {
        // 1. Tool schema exposes "unsupported" alongside "edit", "clarify", "answer"
        using var schemaMs = new MemoryStream();
        using (var w = new Utf8JsonWriter(schemaMs))
        {
            w.WriteStartObject();
            StudioToolTransport.WriteOpenAiToolsArray(w);
            w.WriteEndObject();
        }
        string toolsJson = Encoding.UTF8.GetString(schemaMs.ToArray());
        Assert.Contains("\"unsupported\"", toolsJson, StringComparison.Ordinal);

        // 2. FinishTurn parser normalizes "unsupported" and synonym aliases cleanly
        Assert.True(StudioToolTransport.TryParseFinishTurnArgs(
            """{"kind":"refuse","reply":"DeskBox 不支持全局键盘钩子。","suggestions":["做一个快捷指令面板"]}""",
            out StudioFinishTurnSpec? normalizedRefuse,
            out _));
        Assert.NotNull(normalizedRefuse);
        Assert.Equal("unsupported", normalizedRefuse!.Kind);
        Assert.Single(normalizedRefuse.Suggestions);

        // 3. System prompt explicitly declares capability boundaries and 4-way routing in zh & en
        string zhSys = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt("zh-CN", useNativeTools: true);
        string enSys = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt("en-US", useNativeTools: true);
        Assert.Contains("kind=\"unsupported\"", zhSys, StringComparison.Ordinal);
        Assert.Contains("kind=\"answer\"", zhSys, StringComparison.Ordinal);
        Assert.Contains("kind=\"unsupported\"", enSys, StringComparison.Ordinal);
        Assert.Contains("kind=\"answer\"", enSys, StringComparison.Ordinal);

        // 4. Fenced deskbox-meta with kind="unsupported" parses without error and keeps suggestions
        const string unsupportedBlock = """
        ```deskbox-meta
        {
          "kind": "unsupported",
          "reply": "DeskBox 不支持自动操控微信发消息，但可以做一个快捷话术剪贴板格子。",
          "suggestions": ["做一个高频话术一键复制格子", "做一个微信快捷启动卡片"]
        }
        ```
        """;
        StudioTurnResult parsedBlock = StudioTurnResponseParser.ParseStudioTurnResponse(
            unsupportedBlock,
            preferredEmoji: null,
            providerId: "openai",
            isInitialCreation: true);
        Assert.True(parsedBlock.Success);
        Assert.Equal("unsupported", parsedBlock.Kind);
        Assert.Equal(2, parsedBlock.Suggestions.Count);

        // 5. LooksLikeConversationalProseOnly accepts natural Q&A/refusal on Turn-0 while rejecting truncated JSON/fences
        Assert.True(StudioTurnResponseParser.LooksLikeConversationalProseOnly("DeskBox 目前支持天气、RSS、系统监控和本地脚本等能力，但暂不支持嵌入网页。"));
        Assert.False(StudioTurnResponseParser.LooksLikeConversationalProseOnly("""{"schemaVersion":1,"root":"main"""));
        Assert.False(StudioTurnResponseParser.LooksLikeConversationalProseOnly("```deskbox-manifest\n{\"schemaVersion\":1"));

        // 6. End-to-end RunStudioTurnAsync: Turn-0 unsupported via native tool + Turn-0 plain-text Q&A fast-exit in 1 call each
        string tempCredDir = Path.Combine(Path.GetTempPath(), $"deskbox-consult-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCredDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempCredDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-test",
                CustomEndpoint: "https://mock.local/v1/chat/completions",
                CustomModel: "gpt-4.1",
                ThinkingMode: "disabled"));

            int unsupportedCalls = 0;
            using var unsupportedHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
            {
                unsupportedCalls++;
                string sse = "data: " + JsonSerializer.Serialize(new Dictionary<string, object?>
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
                                        ["index"] = 0,
                                        ["id"] = "tc_unsup",
                                        ["type"] = "function",
                                        ["function"] = new Dictionary<string, object?>
                                        {
                                            ["name"] = StudioToolTransport.ToolFinishTurn,
                                            ["arguments"] = """{"kind":"unsupported","reply":"DeskBox 不支持全局自动抢红包或操控微信客户端。","suggestions":["做一个快捷回复话术盒","做一个待办提醒格子"]}"""
                                        }
                                    }
                                }
                            },
                            ["finish_reason"] = "tool_calls"
                        }
                    }
                }) + "\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                });
            }));

            var unsupGen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, httpClient: unsupportedHttp));
            StudioTurnResult unsupRes = await unsupGen.RunStudioTurnAsync(
                providerId: "openai",
                userMessage: "帮我做一个微信自动抢红包格子",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: null,
                allowSampleData: false,
                uiLanguage: "zh-CN");
            Assert.True(unsupRes.Success, unsupRes.ErrorMessage);
            Assert.Equal("unsupported", unsupRes.Kind);
            Assert.Equal(1, unsupportedCalls);
            Assert.Equal(2, unsupRes.Suggestions.Count);
            Assert.True(string.IsNullOrWhiteSpace(unsupRes.ManifestJson));

            int qaCalls = 0;
            using var qaHttp = new HttpClient(new StubHttpHandler((_, _, _) =>
            {
                qaCalls++;
                string sse = "data: " + JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            delta = new { content = "DeskBox 支持天气、RSS、JSON API、系统监控、倒计时、番茄钟和本地文件导出等原生能力，你可以直接告诉我想要什么桌面小组件。" },
                            finish_reason = "stop"
                        }
                    }
                }) + "\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                });
            }));

            var qaGen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, httpClient: qaHttp));
            StudioTurnResult qaRes = await qaGen.RunStudioTurnAsync(
                providerId: "openai",
                userMessage: "DeskBox 小组件目前都支持哪些能力？",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: null,
                allowSampleData: false,
                uiLanguage: "zh-CN");
            Assert.True(qaRes.Success, qaRes.ErrorMessage);
            Assert.Equal("answer", qaRes.Kind);
            Assert.Equal(1, qaCalls);
            Assert.Contains("天气", qaRes.Reply, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempCredDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Scenario14_PartialFeasibilityLimitationNote_RoundTripsAcrossSchemaParserOrchestratorAndSessionStore()
    {
        // 1. OpenAI finish_turn tool schema exposes limitationNote
        using var schemaStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(schemaStream))
        {
            writer.WriteStartObject();
            StudioToolTransport.WriteOpenAiToolsArray(writer);
            writer.WriteEndObject();
        }

        string toolsJson = Encoding.UTF8.GetString(schemaStream.ToArray());
        Assert.Contains("\"limitationNote\"", toolsJson, StringComparison.Ordinal);

        // 2. TryParseFinishTurnArgs parses and trims limitationNote (and omits blank)
        Assert.True(StudioToolTransport.TryParseFinishTurnArgs(
            """{"kind":"edit","reply":"已做好待办提醒卡片。","changeSummary":"待办提醒","limitationNote":"  关闭 DeskBox 后无法在后台常驻弹窗，可改用开机自启。  "}""",
            out StudioFinishTurnSpec? finSpec,
            out string? finErr), finErr);
        Assert.NotNull(finSpec);
        Assert.Equal("关闭 DeskBox 后无法在后台常驻弹窗，可改用开机自启。", finSpec!.LimitationNote);

        Assert.True(StudioToolTransport.TryParseFinishTurnArgs(
            """{"kind":"edit","reply":"已生成。","changeSummary":"生成","limitationNote":"   "}""",
            out StudioFinishTurnSpec? blankSpec,
            out _));
        Assert.Null(blankSpec!.LimitationNote);

        // 3. Multi-block deskbox-meta parser preserves limitationNote
        const string multiBlock = """
        ```deskbox-meta
        {
          "kind": "edit",
          "reply": "已做好日常提醒卡片。",
          "changeSummary": "日常提醒卡片",
          "limitationNote": "DeskBox 关闭后不运行后台守护服务，需保持应用在托盘运行。"
        }
        ```
        ```deskbox-manifest
        {
          "schemaVersion": 1,
          "id": "custom.daily-reminder",
          "name": "日常提醒",
          "defaultSize": { "width": 320, "height": 180 },
          "capsule": { "title": "提醒", "summary": "今日 2 条" },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "padding": "12", "children": ["headline"] },
            "headline": { "type": "Text", "text": "每日提醒已就绪", "style": "subtitle" }
          }
        }
        ```
        """;
        StudioTurnResult parsedMulti = StudioTurnResponseParser.ParseStudioTurnResponse(
            multiBlock,
            isInitialCreation: true,
            fallbackCulture: "zh-CN");
        Assert.True(parsedMulti.Success, parsedMulti.ErrorMessage);
        Assert.Equal("edit", parsedMulti.Kind);
        Assert.Equal("DeskBox 关闭后不运行后台守护服务，需保持应用在托盘运行。", parsedMulti.LimitationNote);

        // 4. End-to-end RunStudioTurnAsync propagates limitationNote onto StudioTurnResult
        string tempCredDir = Path.Combine(Path.GetTempPath(), $"deskbox-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCredDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempCredDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-test-limit-key",
                CustomEndpoint: "https://mock.local/v1/chat/completions",
                CustomModel: "gpt-4o-mini",
                ThinkingMode: "disabled"));

            string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.focus-reminder",
              "name": "专注提醒",
              "defaultSize": { "width": 320, "height": 180 },
              "capsule": { "title": "专注", "summary": "就绪" },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "padding": "12", "children": ["title_text"] },
                "title_text": { "type": "Text", "text": "专注提醒卡片", "style": "subtitle" }
              }
            }
            """;
            using var http = new HttpClient(new StubHttpHandler((_, _, _) =>
            {
                string sse = "data: " + JsonSerializer.Serialize(new JsonObject
                {
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["delta"] = new JsonObject
                            {
                                ["tool_calls"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["index"] = 0,
                                        ["id"] = "call_wb_limit",
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = "write_bundle",
                                            ["arguments"] = JsonSerializer.Serialize(new JsonObject
                                            {
                                                ["manifest"] = JsonNode.Parse(manifestJson)
                                            })
                                        }
                                    },
                                    new JsonObject
                                    {
                                        ["index"] = 1,
                                        ["id"] = "call_fin_limit",
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = "finish_turn",
                                            ["arguments"] = JsonSerializer.Serialize(new
                                            {
                                                kind = "edit",
                                                reply = "已做好专注提醒小组件。",
                                                changeSummary = "专注提醒",
                                                limitationNote = "无法在关闭 DeskBox 后常驻后台弹窗，建议开启托盘常驻。"
                                            })
                                        }
                                    }
                                }
                            },
                            ["finish_reason"] = "tool_calls"
                        }
                    }
                }) + "\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                });
            }));

            var gen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, httpClient: http));
            StudioTurnResult turn = await gen.RunStudioTurnAsync(
                providerId: "openai",
                userMessage: "帮我做一个专注提醒卡片，顺便支持关闭 DeskBox 后依然后台弹窗",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: null,
                allowSampleData: false,
                uiLanguage: "zh-CN");
            Assert.True(turn.Success, turn.ErrorMessage);
            Assert.Equal("edit", turn.Kind);
            Assert.Equal("无法在关闭 DeskBox 后常驻后台弹窗，建议开启托盘常驻。", turn.LimitationNote);

            // 5. Session store serializes and reloads StudioChatMessage.LimitationNote
            string serialized = CustomWidgetStudioSessionStore.SerializeChatMessagesJson(
            [
                new StudioChatMessage
                {
                    Id = "msg-limit-1",
                    Role = "assistant",
                    Kind = "edit",
                    Text = turn.Reply,
                    LimitationNote = turn.LimitationNote
                }
            ]);
            List<StudioChatMessage> reloaded = CustomWidgetStudioSessionStore.ParseChatMessagesJson(serialized);
            Assert.Single(reloaded);
            Assert.Equal(turn.LimitationNote, reloaded[0].LimitationNote);
        }
        finally
        {
            try { Directory.Delete(tempCredDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Scenario15_RuntimeTraceErrorEvent_PreviewStatePreservationAndReset_AndThinkingModeSetting()
    {
        // 1. CustomWidgetRuntimeTraceBuffer fires ErrorRecorded only on error/denied and clears cleanly
        var trace = new CustomWidgetRuntimeTraceBuffer();
        var captured = new List<CustomWidgetRuntimeTraceEntry>();
        trace.ErrorRecorded += captured.Add;

        trace.Record("http.request", "https://api.example.com/ok", ok: true, detail: "200 OK");
        Assert.Empty(captured);
        Assert.Null(trace.LatestError);

        trace.Record("http.request", "https://api.example.com/fail", ok: false, detail: "HTTP 502 Bad Gateway");
        Assert.Single(captured);
        Assert.Equal("http.request", trace.LatestError!.ActionType);
        Assert.Contains("502", trace.LatestError.Detail, StringComparison.Ordinal);

        trace.Record("click", "exportNotes", "file.writeText", "C:/Windows/win.ini", "denied", "Path outside managed folder");
        Assert.Equal(2, captured.Count);
        Assert.Equal("denied", trace.LatestError.Outcome);

        trace.Clear();
        Assert.Null(trace.LatestError);
        Assert.False(trace.HasRecentErrors);

        // 2. Preview VM forwards RuntimeTraceErrorRecorded, preserves user state on edit, resets cleanly on reset
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-prevstate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var config = new WidgetConfig
            {
                Id = "__studio_preview_test__",
                Name = "Preview State Test",
                WidgetKind = WidgetKind.Custom
            };

            const string manifestV1 = """
            {
              "schemaVersion": 1,
              "id": "custom.flip-card",
              "name": "单词卡片",
              "defaultSize": { "width": 320, "height": 200 },
              "state": { "flipped": "false", "keyword": "" },
              "capsule": { "title": "单词", "summary": "v1" },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "padding": "12", "children": ["title_text"] },
                "title_text": { "type": "Text", "text": "${state.flipped}", "style": "subtitle" }
              }
            }
            """;
            const string manifestV2 = """
            {
              "schemaVersion": 1,
              "id": "custom.flip-card",
              "name": "单词卡片 · 优化字号",
              "defaultSize": { "width": 320, "height": 200 },
              "state": { "flipped": "false", "keyword": "" },
              "capsule": { "title": "单词", "summary": "v2" },
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "padding": "12", "children": ["title_text"] },
                "title_text": { "type": "Text", "text": "${state.flipped}", "style": "title" }
              }
            }
            """;

            var vmErrors = new List<CustomWidgetRuntimeTraceEntry>();
            CustomWidgetViewModel vm = new(config, locSvc, store: null, isPreviewOnly: true);
            try
            {
                vm.RuntimeTraceErrorRecorded += vmErrors.Add;
                vm.ApplyPreviewBundle(manifestV1, logicJs: null, locales: null, preserveRuntimeData: true);

                vm.SetStateValueWithoutRebuild("flipped", "true");
                vm.SetStateValueWithoutRebuild("keyword", "ephemeral");
                Assert.True(vm.HasUserRuntimeMutations);

                // Minor edit with preserveRuntimeData: true keeps live user preview state
                vm.ApplyPreviewBundle(manifestV2, logicJs: null, locales: null, preserveRuntimeData: true);
                Assert.Equal("true", vm.State["flipped"]);
                Assert.Equal("ephemeral", vm.State["keyword"]);

                // Runtime trace error forwards through VM event
                vm.RuntimeTrace.Record("http.request", "https://dict.example.com", ok: false, detail: "Timeout");
                Assert.Single(vmErrors);

                // Explicit reset (preserveRuntimeData: false) restores manifest defaults and clears runtime trace + mutation flags
                vm.ApplyPreviewBundle(manifestV2, logicJs: null, locales: null, preserveRuntimeData: false);
                Assert.Equal("false", vm.State["flipped"]);
                Assert.Equal(string.Empty, vm.State["keyword"]);
                Assert.False(vm.HasUserRuntimeMutations);
                Assert.Null(vm.RuntimeTrace.LatestError);
            }
            finally
            {
                vm.Dispose();
            }

            // Disposed VM no longer forwards trace error events
            vm.RuntimeTrace.Record("http.request", "https://dict.example.com", ok: false, detail: "After dispose");
            Assert.Single(vmErrors);

            // 3. Thinking mode persistence via CustomWidgetAiCredentialStore.UpdateStudioSettings (merged into model selector)
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-test-thinking-key",
                CustomModel: "gpt-4o",
                ThinkingMode: "auto"));
            credStore.UpdateStudioSettings("openai", customModel: "gpt-4o", thinkingMode: DeskBoxAiService.ToThinkingWireValue(AiThinkingMode.Enabled));
            Assert.Equal(AiThinkingMode.Enabled, DeskBoxAiService.ParseThinkingMode(credStore.Get("openai")?.ThinkingMode));

            credStore.UpdateStudioSettings("openai", thinkingMode: DeskBoxAiService.ToThinkingWireValue(AiThinkingMode.Disabled));
            Assert.Equal(AiThinkingMode.Disabled, DeskBoxAiService.ParseThinkingMode(credStore.Get("openai")?.ThinkingMode));

            credStore.UpdateStudioSettings("openai", thinkingMode: DeskBoxAiService.ToThinkingWireValue(AiThinkingMode.Auto));
            Assert.Equal(AiThinkingMode.Auto, DeskBoxAiService.ParseThinkingMode(credStore.Get("openai")?.ThinkingMode));

            // 4. Studio composer send-shortcut preference defaults to Ctrl+Enter (SendOnEnter == false) and round-trips cleanly
            string prefsPath = Path.Combine(tempDir, "studio-prefs.json");
            using (var prefs1 = new CustomWidgetStudioPreferencesStore(prefsPath))
            {
                Assert.False(prefs1.SendOnEnter);
                prefs1.SetSendOnEnter(true);
                Assert.True(prefs1.SendOnEnter);
            }
            using (var prefs2 = new CustomWidgetStudioPreferencesStore(prefsPath))
            {
                Assert.True(prefs2.SendOnEnter);
                prefs2.SetSendOnEnter(false);
                Assert.False(prefs2.SendOnEnter);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
