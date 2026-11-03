using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class CustomWidgetNativeUpgradeTests
{
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }

    [Fact]
    public void ManifestParser_All12NewNodes_Popovers_RawStyling_And_NewActionsSources_RoundTripCleanly()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.native-upgrade-suite",
          "name": "Native Upgrade Suite",
          "icon": "🚀",
          "defaultSize": { "width": 380, "height": 520 },
          "settings": [
            { "key": "apiToken", "type": "secret", "label": "API Token", "default": "" },
            { "key": "notePath", "type": "text", "label": "Note Path", "default": "~/deskbox-note.md" }
          ],
          "state": {
            "notes": "# Tasks\n- [x] Ready",
            "dueDate": "2026-12-31",
            "dueTime": "09:30",
            "query": "",
            "accentColor": "#38BDF8",
            "mode": "fast",
            "stars": "4",
            "fileBody": "",
            "cliOut": ""
          },
          "collections": {
            "metrics": { "maxItems": 50 },
            "tasks": { "maxItems": 50 }
          },
          "sources": [
            { "id": "localDoc", "type": "fileText", "path": "${$settings.notePath}", "stateKey": "fileBody", "intervalMs": 5000 },
            { "id": "gitStatus", "type": "command", "executable": "git", "arguments": ["status", "--short"], "stateKey": "cliOut", "intervalMs": 15000 }
          ],
          "script": { "entry": "logic.js", "tickIntervalMs": 1000 },
          "popovers": {
            "quickEditor": {
              "width": 300,
              "height": 220,
              "placement": "bottom",
              "view": {
                "type": "Stack",
                "padding": "12",
                "children": [
                  { "type": "Markdown", "bind": "state.notes" },
                  { "type": "Button", "text": "Close", "onClick": { "type": "popover.close", "popoverId": "quickEditor" } }
                ]
              }
            }
          },
          "capsule": {
            "title": "Upgrade",
            "summary": "${state.mode}",
            "glyph": "🚀",
            "badge": "${state.stars}"
          },
          "view": {
            "type": "Stack",
            "padding": "12,10,12,12",
            "spacing": 8,
            "children": [
              {
                "type": "Card",
                "cornerRadius": 14,
                "borderColor": "${state.accentColor}",
                "borderWidth": "1",
                "linearGradient": {
                  "angle": 135,
                  "stops": [
                    { "offset": 0, "color": "#0F172A" },
                    { "offset": 1, "color": "#1E293B" }
                  ]
                },
                "children": [
                  { "type": "Text", "text": "Hero Surface", "textColor": "#F8FAFC", "fontSize": 16, "fontWeight": "bold" },
                  { "type": "Button", "text": "Open Notes", "popover": "quickEditor" },
                  { "type": "Popover", "popoverId": "quickEditor", "text": "Trigger" }
                ]
              },
              { "type": "DatePicker", "title": "Date", "bind": "state.dueDate" },
              { "type": "TimePicker", "title": "Time", "bind": "state.dueTime" },
              { "type": "AutoSuggestInput", "bind": "state.query", "suggestions": ["Alpha", "Beta"] },
              { "type": "ColorPicker", "bind": "state.accentColor", "presets": ["#38BDF8", "#10B981"] },
              {
                "type": "RadioGroup",
                "bind": "state.mode",
                "orientation": "horizontal",
                "options": [
                  { "value": "fast", "label": "Fast" },
                  { "value": "deep", "label": "Deep" }
                ]
              },
              { "type": "Rating", "bind": "state.stars", "max": 5, "allowHalfStar": true },
              {
                "type": "DonutChart",
                "collection": "metrics",
                "titleField": "name",
                "valueField": "val",
                "centerTitle": "Sum",
                "centerValue": "100"
              },
              {
                "type": "Chart",
                "chartKind": "area",
                "collection": "metrics",
                "xField": "day",
                "series": [
                  { "field": "val", "label": "Value", "color": "#38BDF8" }
                ]
              },
              {
                "type": "Table",
                "collection": "metrics",
                "sortable": true,
                "striped": true,
                "columns": [
                  { "key": "day", "header": "Day", "width": "1*" },
                  { "key": "val", "header": "Value", "width": "1*", "align": "right" }
                ],
                "onRowClick": [
                  { "type": "script.run", "fn": "selectRow", "args": { "source": "table" } }
                ]
              },
              {
                "type": "Canvas",
                "height": 120,
                "viewBox": "0 0 200 120",
                "elements": [
                  { "id": "dot", "kind": "circle", "cx": 60, "cy": 60, "r": 24, "fill": "#38BDF8" },
                  { "id": "arc", "kind": "path", "d": "M 10 10 L 90 10 L 50 80 Z", "stroke": "#10B981", "strokeWidth": 2 }
                ],
                "onItemClick": [
                  { "type": "script.run", "fn": "hitCanvas" }
                ]
              },
              {
                "type": "List",
                "items": "tasks",
                "allowReorder": true,
                "inlineEditField": "title",
                "onReorder": { "type": "collection.reorder", "collection": "tasks", "fromIndex": "0", "toIndex": "1" },
                "itemTemplate": { "type": "Text", "text": "${$item.title}" }
              },
              {
                "type": "Button",
                "text": "Sync Remote & Save",
                "onClick": [
                  {
                    "type": "http.request",
                    "method": "POST",
                    "url": "https://api.example.com/sync",
                    "headers": { "Authorization": "Bearer ${$secrets.apiToken}" },
                    "body": { "mode": "${state.mode}" }
                  },
                  { "type": "file.writeText", "path": "${$settings.notePath}", "text": "${state.notes}", "append": false },
                  { "type": "command.run", "executable": "cmd.exe", "arguments": ["/c", "echo", "ok"] }
                ]
              }
            ]
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.NotNull(parsed.Manifest);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.IsError);

        CustomWidgetManifest m = parsed.Manifest!;
        Assert.Single(m.Popovers);
        Assert.Equal("quickEditor", m.Popovers[0].PopoverId);
        Assert.NotEmpty(m.Popovers[0].Children);
        Assert.True(CustomWidgetManifestCompatibility.UsesV160Features(m));

        string stamped = CustomWidgetManifestCompatibility.StampRequiredMinAppVersion(
            CustomWidgetManifestMetadataStamper.StampProvenance(manifestJson, CustomWidgetOriginKinds.Local, isCustomized: false));
        CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(stamped);
        Assert.NotNull(reparsed.Manifest);
        Assert.DoesNotContain(reparsed.Diagnostics, d => d.IsError);
        Assert.Equal("1.6.0", reparsed.Manifest!.MinAppVersion);
        Assert.Single(reparsed.Manifest.Popovers);
        Assert.Equal(2, reparsed.Manifest.Sources.Count);

        IReadOnlyList<CustomWidgetActionSpec> dispatched = CustomWidgetManifestParser.ParseDispatchedActions(
            """[{"type":"state.set","key":"mode","value":"deep"},{"type":"popover.toggle","popoverId":"quickEditor"}]""");
        Assert.Equal(2, dispatched.Count);
        Assert.Equal("popover.toggle", dispatched[1].Type);
        Assert.Equal("quickEditor", dispatched[1].PopoverId);
    }

    [Fact]
    public void RawColorParser_SupportsHexRgbRgbaAndTransparent_And_HealthAnalyzer_ExemptsCustomStyledSurfaces()
    {
        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("#09F", out Color c3));
        Assert.Equal(255, c3.A);
        Assert.Equal(0x00, c3.R);
        Assert.Equal(0x99, c3.G);
        Assert.Equal(0xFF, c3.B);

        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("#80112233", out Color c8));
        Assert.Equal(0x80, c8.A);
        Assert.Equal(0x11, c8.R);
        Assert.Equal(0x22, c8.G);
        Assert.Equal(0x33, c8.B);

        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("rgba(15, 23, 42, 0.5)", out Color rgba));
        Assert.InRange(rgba.A, 126, 129);
        Assert.Equal(15, rgba.R);
        Assert.Equal(23, rgba.G);
        Assert.Equal(42, rgba.B);

        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("transparent", out Color tr));
        Assert.Equal(0, tr.A);

        const string styledManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.styled-dark-card",
          "name": "Dark Card",
          "icon": "🎨",
          "popovers": {
            "detail": {
              "width": 260,
              "height": 180,
              "view": { "type": "Text", "text": "Detail body" }
            }
          },
          "capsule": { "title": "Dark", "summary": "OK", "glyph": "🎨" },
          "view": {
            "type": "Stack",
            "padding": "12,10,12,12",
            "children": [
              {
                "type": "Card",
                "background": "#0F172A",
                "children": [
                  { "type": "Text", "text": "Crisp white copy on intentional dark surface", "color": "#FFFFFF" },
                  { "type": "Button", "text": "Inspect", "popover": "detail" }
                ]
              }
            ]
          }
        }
        """;

        CustomWidgetManifest parsed = CustomWidgetManifestParser.Parse(styledManifest).Manifest!;
        CustomWidgetHealthReport report = CustomWidgetHealthAnalyzer.AnalyzeHealth(styledManifest, parsed, storePayload: null);
        Assert.DoesNotContain(report.Issues, i => i.Code == "theme.hardcoded_hex");
        Assert.DoesNotContain(report.Issues, i => i.Code == "interaction.dead_button");
    }

    [Fact]
    public void TrustStore_LocalVsExternalProvenance_RiskDescriptors_PermissionOverrides_And_ManifestDetection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetTrustStore(tempDir);

            store.RegisterWidgetProvenance("custom.local-made", trustedLocalAuthoring: true);
            CustomWidgetTrustProfile localProfile = store.GetProfile("custom.local-made");
            Assert.True(localProfile.TrustedLocalAuthoring);
            Assert.True(localProfile.IsPermissionGranted(CustomWidgetPermissionKind.LocalNetwork));
            Assert.True(localProfile.IsPermissionGranted(CustomWidgetPermissionKind.LocalFileAccess));
            Assert.True(localProfile.IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));

            store.RegisterWidgetProvenance("custom.external-pkg", trustedLocalAuthoring: false);
            CustomWidgetTrustProfile extProfile = store.GetProfile("custom.external-pkg");
            Assert.False(extProfile.TrustedLocalAuthoring);
            Assert.True(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.Notifications));
            Assert.True(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: true));
            Assert.False(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.PublicNetwork, isReadOnlyPublicGet: false));
            Assert.False(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.LocalNetwork));
            Assert.False(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.LocalFileAccess));
            Assert.False(extProfile.IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));

            store.ApplyPermissionMutations(
                "custom.external-pkg",
                new Dictionary<string, bool>
                {
                    ["localNetwork"] = true,
                    ["localFileAccess"] = true
                },
                defaultTrustedIfNew: false);

            var reloaded = new CustomWidgetTrustStore(tempDir).GetProfile("custom.external-pkg");
            Assert.True(reloaded.IsPermissionGranted(CustomWidgetPermissionKind.LocalNetwork));
            Assert.True(reloaded.IsPermissionGranted(CustomWidgetPermissionKind.LocalFileAccess));
            Assert.False(reloaded.IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));

            CustomWidgetPermissionDescriptor cmdDescZh = CustomWidgetTrustStore.GetDescriptor(
                CustomWidgetPermissionKind.CommandExecution,
                "zh-CN");
            Assert.Equal(CustomWidgetPermissionRiskLevel.Critical, cmdDescZh.RiskLevel);
            Assert.Contains("高危", cmdDescZh.RiskBadgeText, StringComparison.Ordinal);

            CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse("""
            {
              "schemaVersion": 1,
              "id": "custom.perm-detect",
              "name": "Perm Detect",
              "sources": [
                { "type": "httpJson", "url": "http://127.0.0.1:8080/status", "intervalMs": 15000 },
                { "type": "fileText", "path": "~/notes.md", "stateKey": "txt" }
              ],
              "view": {
                "type": "Button",
                "text": "Run",
                "onClick": [
                  { "type": "command.run", "executable": "git" },
                  { "type": "toast.show", "title": "Hi", "message": "Done" }
                ]
              }
            }
            """).Manifest!;

            IReadOnlySet<CustomWidgetPermissionKind> detected = CustomWidgetTrustStore.DetectRequestedPermissions(manifest);
            Assert.Contains(CustomWidgetPermissionKind.LocalNetwork, detected);
            Assert.Contains(CustomWidgetPermissionKind.LocalFileAccess, detected);
            Assert.Contains(CustomWidgetPermissionKind.CommandExecution, detected);
            Assert.Contains(CustomWidgetPermissionKind.Notifications, detected);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScriptEngine_And_ViewModel_SupportStructuredArgs_EventPayload_CtxDispatch_And_LivePermissionGating()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-vm-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string targetNoteFile = Path.Combine(tempDir, "out.txt");
        string widgetId = $"custom.vm-test-{Guid.NewGuid():N}";

        try
        {
            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{widgetId}}",
              "name": "VM Upgrade",
              "icon": "⚡",
              "state": { "cell": "none", "dispatchedVal": "0" },
              "collections": { "items": { "maxItems": 20 } },
              "script": { "entry": "logic.js" },
              "view": { "type": "Text", "text": "${$computed.label}" }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              ctx.computed.label = "ready";
            }

            export function handleCanvasTap(ctx) {
              const step = ctx.args && ctx.args.step ? ctx.args.step : "1";
              const hit = ctx.event && ctx.event.id ? `${ctx.event.id}@${ctx.event.x},${ctx.event.y}` : "miss";
              ctx.state.cell = hit;
              ctx.computed.label = `${hit}#${step}`;
              ctx.dispatch([
                { type: "state.set", key: "dispatchedVal", value: step },
                { type: "collection.append", collection: "items", item: { title: hit } }
              ]);
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var config = new WidgetConfig { Id = widgetId, Name = "VM Upgrade", WidgetKind = WidgetKind.Custom };
            using var vm = new CustomWidgetViewModel(
                config,
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            Assert.Null(vm.LastScriptError);
            Assert.Equal("ready", vm.Computed["label"].GetString());

            CustomWidgetEvaluationContext pointerCtx = vm.CreateEvaluationContext()
                .WithPointerEvent("tile-3", 42.5, 88.0);
            CustomWidgetActionSpec scriptAction = CustomWidgetManifestParser.ParseDispatchedActions(
                """{"type":"script.run","fn":"handleCanvasTap","args":{"step":"9"}}""")[0];

            await vm.ExecuteActionsAsync([scriptAction], pointerCtx);

            Assert.Equal("tile-3@42.5,88", vm.State["cell"]);
            Assert.Equal("9", vm.State["dispatchedVal"]);
            Assert.Single(vm.Collections["items"]);
            Assert.Equal("tile-3@42.5,88", vm.Collections["items"][0].Fields["title"]);

            // Add a second item and verify collection.reorder
            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """{"type":"collection.append","collection":"items","item":{"title":"second"}}"""));
            Assert.Equal(2, vm.Collections["items"].Count);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """{"type":"collection.reorder","collection":"items","fromIndex":1,"toIndex":0}"""));
            Assert.Equal("second", vm.Collections["items"][0].Fields["title"]);

            // Live permission gating on file.writeText
            CustomWidgetTrustStore.Shared.SetPermissionOverride(
                widgetId,
                CustomWidgetPermissionKind.LocalFileAccess,
                enabled: false,
                defaultTrustedIfNew: true);

            CustomWidgetActionSpec writeAction = CustomWidgetManifestParser.ParseDispatchedActions(
                $$"""{"type":"file.writeText","path":{{JsonSerializer.Serialize(targetNoteFile)}},"text":"hello-perm"}""")[0];
            await vm.ExecuteActionsAsync([writeAction]);
            Assert.False(File.Exists(targetNoteFile));

            CustomWidgetTrustStore.Shared.SetPermissionOverride(
                widgetId,
                CustomWidgetPermissionKind.LocalFileAccess,
                enabled: true,
                defaultTrustedIfNew: true);
            await vm.ExecuteActionsAsync([writeAction]);
            Assert.True(File.Exists(targetNoteFile));
            Assert.Equal("hello-perm", await File.ReadAllTextAsync(targetNoteFile, Encoding.UTF8));
        }
        finally
        {
            CustomWidgetTrustStore.Shared.RemoveWidget(widgetId);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LocalFileEngine_And_CommandRunner_ReadWriteTail_BlockUncAndSystemDirs_And_RunCliWindowlessly()
    {
        Assert.False(CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(
            @"\\evil-server\share\leak.txt",
            forWrite: false,
            out _,
            out string? uncErr));
        Assert.Contains("UNC", uncErr, StringComparison.OrdinalIgnoreCase);

        string sysFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "deskbox-forbidden.txt");
        Assert.False(CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(
            sysFile,
            forWrite: true,
            out _,
            out _));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string logPath = Path.Combine(tempDir, "app.log");
            CustomWidgetFileWriteOutcome w1 = await CustomWidgetLocalFileEngine.WriteTextFileAsync(
                logPath,
                "line-1\nline-2\n",
                append: false);
            Assert.True(w1.Succeeded, w1.ErrorMessage);

            CustomWidgetFileWriteOutcome w2 = await CustomWidgetLocalFileEngine.WriteTextFileAsync(
                logPath,
                "line-3",
                append: true);
            Assert.True(w2.Succeeded, w2.ErrorMessage);

            CustomWidgetFileReadOutcome tail = await CustomWidgetLocalFileEngine.ReadTextSourceAsync(logPath, tailLines: 2);
            Assert.True(tail.Succeeded, tail.ErrorMessage);
            Assert.Equal("line-2\nline-3", tail.Content);

#if !DESKBOX_STORE
            CustomWidgetCommandOutcome cmdRes = await CustomWidgetCommandRunner.ExecuteAsync(
                "cmd.exe",
                ["/c", "echo", "deskbox-cli-ok"],
                workingDirectory: tempDir,
                timeoutMs: 5_000);
            Assert.True(cmdRes.Succeeded, cmdRes.ErrorMessage);
            Assert.Equal(0, cmdRes.ExitCode);
            Assert.Contains("deskbox-cli-ok", cmdRes.Stdout, StringComparison.Ordinal);
#endif
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NetworkEngine_Supports15sFloor_LocalHttpGating_InteractivePostWebhook_And_RawProbe()
    {
        Assert.Equal(15_000, CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs);
        Assert.False(CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
            "http://127.0.0.1:11434/api/tags",
            allowLocalAndHttp: false,
            out _,
            out bool reqLocalDenied,
            out _));
        Assert.True(reqLocalDenied);

        Assert.True(CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
            "http://127.0.0.1:11434/api/tags",
            allowLocalAndHttp: true,
            out _,
            out bool reqLocalAllowed,
            out _));
        Assert.True(reqLocalAllowed);

        string? capturedAuth = null;
        string? capturedMethod = null;
        string? capturedBody = null;

        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            capturedMethod = req.Method.Method;
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : null;

            if (req.RequestUri!.AbsolutePath.EndsWith("/html", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!DOCTYPE html><html><body>Login</body></html>", Encoding.UTF8, "text/html")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"queued","job":{"id":"job-42"},"items":[{"name":"A"}]}""", Encoding.UTF8, "application/json")
            };
        });

        string cacheDir = Path.Combine(Path.GetTempPath(), $"deskbox-net-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDir);
        try
        {
            var engine = new CustomWidgetNetworkSourceEngine(
                cacheDir,
                handler,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            CustomWidgetInteractiveHttpOutcome postRes = await engine.SendInteractiveHttpRequestAsync(
                "https://api.example.com/v1/jobs",
                method: "POST",
                resolvedHeaders: new Dictionary<string, string> { ["Authorization"] = "Bearer secret-xyz" },
                body: """{"task":"build"}""",
                stateMap: new Dictionary<string, string> { ["lastJobId"] = "job.id" });

            Assert.True(postRes.Succeeded, postRes.ErrorMessage);
            Assert.Equal("POST", capturedMethod);
            Assert.Equal("Bearer secret-xyz", capturedAuth);
            Assert.Equal("""{"task":"build"}""", capturedBody);
            Assert.Equal("job-42", postRes.StateUpdates["lastJobId"]);

            CustomWidgetRawEndpointProbeResult jsonProbe = await engine.ProbeRawEndpointAsync("https://api.example.com/v1/jobs");
            Assert.True(jsonProbe.Succeeded, jsonProbe.ErrorMessage);
            Assert.NotNull(jsonProbe.JsonSkeleton);
            Assert.Contains("job", jsonProbe.JsonSkeleton!, StringComparison.Ordinal);

            CustomWidgetRawEndpointProbeResult htmlProbe = await engine.ProbeRawEndpointAsync("https://api.example.com/html");
            Assert.False(htmlProbe.Succeeded);
            Assert.Contains("HTML", htmlProbe.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DirectApi_MultiFencedBlockProtocol_And_4RoundAgenticLoop_InspectCapabilities_And_ProbeHttp()
    {
        const string multiBlockSample = """
        ```deskbox-meta
        { "kind": "edit", "reply": "Built!", "changeSummary": "Initial build" }
        ```
        ```deskbox-manifest
        { "schemaVersion": 1, "id": "custom.clipboard-extract", "name": "Test", "view": { "type": "Text", "text": "Hi" } }
        ```
        ```deskbox-script
        export function onInit(ctx) {
          ctx.computed.msg = `Line 1\nLine 2 with "quotes"`;
        }
        ```
        """;

        (string extractedManifest, string? extractedJs) =
            CustomWidgetTemplates.ExtractWorkspaceBundleFromClipboardText(multiBlockSample);
        Assert.Contains("\"custom.clipboard-extract\"", extractedManifest, StringComparison.Ordinal);
        Assert.NotNull(extractedJs);
        Assert.Contains("Line 2 with \"quotes\"", extractedJs!, StringComparison.Ordinal);

        int llmCallIndex = 0;
        var observedStages = new List<AiGenerationStage>();
        var llmRequests = new List<string>();

        var aiHttpHandler = new StubHttpMessageHandler(async (req, ct) =>
        {
            string reqBody = await req.Content!.ReadAsStringAsync(ct);
            llmRequests.Add(reqBody);
            llmCallIndex++;

            string assistantContent = llmCallIndex switch
            {
                1 => """
                ```deskbox-action
                { "tool": "inspectCapabilities", "ids": ["vector-canvas", "interactive-http"] }
                ```
                """,
                2 => """
                ```deskbox-action
                { "tool": "probeHttp", "url": "https://api.example.com/v1/pulse", "method": "GET" }
                ```
                """,
                _ => """
                ```deskbox-meta
                {
                  "kind": "edit",
                  "reply": "已为你生成脉冲矢量仪表盘。",
                  "changeSummary": "创建脉冲矢量仪表盘",
                  "suggestions": ["切换渐变配色", "增加历史表格"]
                }
                ```
                ```deskbox-manifest
                {
                  "schemaVersion": 1,
                  "id": "custom.pulse-gauge",
                  "name": "脉冲仪表盘",
                  "description": "实时脉冲监控",
                  "icon": "📡",
                  "defaultSize": { "width": 340, "height": 360 },
                  "state": { "bpm": "72" },
                  "collections": { "points": { "maxItems": 20 } },
                  "script": { "entry": "logic.js" },
                  "capsule": {
                    "title": "脉冲",
                    "summary": "${$computed.headline}",
                    "glyph": "📡",
                    "badge": "${state.bpm}"
                  },
                  "view": {
                    "type": "Stack",
                    "padding": "12,10,12,12",
                    "spacing": 8,
                    "children": [
                      {
                        "type": "Card",
                        "linearGradient": {
                          "angle": 135,
                          "stops": [
                            { "offset": 0, "color": "#0F172A" },
                            { "offset": 1, "color": "#1E3A8A" }
                          ]
                        },
                        "children": [
                          { "type": "Text", "text": "${$computed.headline}", "textColor": "#F8FAFC", "fontWeight": "semiBold" },
                          {
                            "type": "Canvas",
                            "height": 120,
                            "viewBox": "0 0 200 120",
                            "elementsSource": "${$computed.shapes}"
                          }
                        ]
                      }
                    ]
                  }
                }
                ```
                ```deskbox-script
                export function onInit(ctx) {
                  const bpm = Number(ctx.state.bpm || 72);
                  ctx.computed.headline = `Pulse: "${bpm}" BPM`;
                  ctx.computed.shapes = [
                    { id: "ring", kind: "circle", cx: 100, cy: 60, r: 42, stroke: "#38BDF8", strokeWidth: 5, fill: "transparent" }
                  ];
                }
                ```
                ```deskbox-sample
                {
                  "collections": {
                    "points": [
                      { "label": "T1", "value": "72" }
                    ]
                  }
                }
                ```
                """
            };

            string completionJson = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = assistantContent
                        }
                    }
                }
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completionJson, Encoding.UTF8, "application/json")
            };
        });

        var probeHttpHandler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"bpm":76,"unit":"bpm"}""", Encoding.UTF8, "application/json")
            }));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            var prefStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));
            using var aiHttpClient = new HttpClient(aiHttpHandler);
            var aiService = new DeskBoxAiService(credStore, prefStore, aiHttpClient);
            var probeEngine = new CustomWidgetNetworkSourceEngine(
                Path.Combine(tempDir, "net-cache"),
                probeHttpHandler,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

            var generator = new CustomWidgetAiGeneratorService(aiService, probeEngine);

            StudioTurnResult turn = await generator.RunStudioTurnAsync(
                providerId: "ollama",
                userMessage: "用矢量画布做一个脉冲仪表盘并探测 https://api.example.com/v1/pulse",
                allowSampleData: true,
                onProgress: p => observedStages.Add(p.Stage),
                uiLanguage: "zh-CN");

            Assert.True(turn.Success, turn.ErrorMessage);
            Assert.Equal(3, llmCallIndex);
            Assert.Equal("edit", turn.Kind);
            Assert.NotEmpty(turn.ManifestJson);
            Assert.NotNull(turn.LogicJs);
            Assert.Contains("Pulse: \"${bpm}\" BPM", turn.LogicJs!, StringComparison.Ordinal);
            Assert.NotNull(turn.SampleDataJson);
            Assert.Contains("\"points\"", turn.SampleDataJson!, StringComparison.Ordinal);
            Assert.Contains(AiGenerationStage.InspectingCapabilities, observedStages);
            Assert.Contains(AiGenerationStage.ProbingApi, observedStages);
            Assert.Contains("vector-canvas", llmRequests[1], StringComparison.Ordinal);
            Assert.Contains("<external_api_sample>", llmRequests[2], StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void I18nEngine_CompilesRootPopovers_TableColumns_ChartSeries_DonutCenterTitle_CanvasText_And_RowActions()
    {
        const string rawManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.i18n-audit",
          "name": "数据看板",
          "icon": "📊",
          "popovers": {
            "detail": {
              "width": 280,
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "浮层详情说明" }
                ]
              }
            }
          },
          "view": {
            "type": "Stack",
            "children": [
              {
                "type": "Table",
                "collection": "rows",
                "columns": [
                  { "key": "name", "header": "任务名称" },
                  { "key": "status", "header": "执行状态" }
                ],
                "onRowClick": { "type": "toast.show", "title": "行点击", "message": "已选中任务" }
              },
              {
                "type": "DonutChart",
                "collection": "rows",
                "titleField": "name",
                "valueField": "val",
                "centerTitle": "总完成率"
              },
              {
                "type": "Chart",
                "chartKind": "line",
                "collection": "rows",
                "xField": "day",
                "series": [
                  { "field": "val", "label": "每日耗时" }
                ]
              },
              {
                "type": "Canvas",
                "viewBox": "0 0 100 60",
                "elements": [
                  { "kind": "text", "x": 50, "y": 30, "text": "画布核心指标" }
                ]
              }
            ]
          }
        }
        """;

        CustomWidgetI18nCompileResult compiled = CustomWidgetI18nEngine.Compile(rawManifest, fallbackCulture: "zh-CN");
        Assert.True(compiled.ManifestChanged);
        Assert.Contains("浮层详情说明", compiled.BaseEntries.Values);
        Assert.Contains("任务名称", compiled.BaseEntries.Values);
        Assert.Contains("执行状态", compiled.BaseEntries.Values);
        Assert.Contains("已选中任务", compiled.BaseEntries.Values);
        Assert.Contains("总完成率", compiled.BaseEntries.Values);
        Assert.Contains("每日耗时", compiled.BaseEntries.Values);
        Assert.Contains("画布核心指标", compiled.BaseEntries.Values);

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(compiled.CompiledManifestJson);
        Assert.NotNull(parsed.Manifest);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.IsError);
    }

    [Fact]
    public async Task ViewModel_ComputedArrayFastPath_UsesMetricsRefreshedForCanvas_And_StructuralRebuildForTable()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-perf-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string canvasWidgetId = $"custom.canvas-fast-{Guid.NewGuid():N}";
        string tableWidgetId = $"custom.table-rebuild-{Guid.NewGuid():N}";

        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);

            // 1. Canvas elementsSource bound to $computed.shapes -> must update via MetricsRefreshed (0 full rebuilds)
            string canvasManifest = $$"""
            {
              "schemaVersion": 1,
              "id": "{{canvasWidgetId}}",
              "name": "Canvas Fast",
              "script": { "entry": "logic.js" },
              "view": {
                "type": "Canvas",
                "viewBox": "0 0 100 100",
                "elementsSource": "${$computed.shapes}"
              }
            }
            """;
            const string canvasLogic = """
            export function onInit(ctx) {
              ctx.computed.shapes = [{ kind: "circle", cx: 20, cy: 20, r: 10, fill: "#38BDF8" }];
            }
            export function stepFrame(ctx) {
              ctx.computed.shapes = [{ kind: "circle", cx: 60, cy: 40, r: 12, fill: "#10B981" }];
            }
            """;

            var canvasStore = new CustomWidgetDataStore(canvasWidgetId, Path.Combine(tempDir, "widgets"));
            await canvasStore.WriteWorkspaceBundleAsync(canvasManifest, canvasLogic, locales: null);

            using var canvasVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = canvasWidgetId, Name = "Canvas Fast", WidgetKind = WidgetKind.Custom },
                locService,
                store: canvasStore,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await canvasVm.InitializeAsync();

            int canvasRebuilds = 0;
            int canvasMetrics = 0;
            canvasVm.ViewRebuildRequested += (_, _) => canvasRebuilds++;
            canvasVm.MetricsRefreshed += (_, _) => canvasMetrics++;

            await canvasVm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""{"type":"script.run","fn":"stepFrame"}"""));

            Assert.Equal(0, canvasRebuilds);
            Assert.True(canvasMetrics >= 1);

            // 2. Table bound to $computed.rows -> structural row count/order changes MUST trigger ViewRebuildRequested
            string tableManifest = $$"""
            {
              "schemaVersion": 1,
              "id": "{{tableWidgetId}}",
              "name": "Table Rebuild",
              "script": { "entry": "logic.js" },
              "view": {
                "type": "Table",
                "items": "$computed.rows",
                "columns": [{ "key": "title", "header": "Title" }]
              }
            }
            """;
            const string tableLogic = """
            export function onInit(ctx) {
              ctx.computed.rows = [{ title: "Row 1" }];
            }
            export function addRow(ctx) {
              ctx.computed.rows = [{ title: "Row 1" }, { title: "Row 2" }];
            }
            """;

            var tableStore = new CustomWidgetDataStore(tableWidgetId, Path.Combine(tempDir, "widgets"));
            await tableStore.WriteWorkspaceBundleAsync(tableManifest, tableLogic, locales: null);

            using var tableVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = tableWidgetId, Name = "Table Rebuild", WidgetKind = WidgetKind.Custom },
                locService,
                store: tableStore,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await tableVm.InitializeAsync();

            int tableRebuilds = 0;
            tableVm.ViewRebuildRequested += (_, _) => tableRebuilds++;

            await tableVm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""{"type":"script.run","fn":"addRow"}"""));

            Assert.True(tableRebuilds >= 1);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PermissionLocalization_CoversAll13Languages_And_CtxRefreshSource_RefreshesFileText()
    {
        foreach (string culture in LocalizationService.SupportedLanguageCodes)
        {
            foreach (CustomWidgetPermissionKind kind in Enum.GetValues<CustomWidgetPermissionKind>())
            {
                CustomWidgetPermissionDescriptor desc = CustomWidgetTrustStore.GetDescriptor(kind, culture);
                Assert.False(string.IsNullOrWhiteSpace(desc.Title));
                Assert.False(string.IsNullOrWhiteSpace(desc.RiskBadgeText));
                Assert.False(string.IsNullOrWhiteSpace(desc.RiskDescription));
                Assert.DoesNotContain("CustomWidget.Perm.", desc.Title, StringComparison.Ordinal);
                Assert.DoesNotContain("CustomWidget.Perm.", desc.RiskBadgeText, StringComparison.Ordinal);
                Assert.DoesNotContain("CustomWidget.Perm.", desc.RiskDescription, StringComparison.Ordinal);
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-file-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string watchedFile = Path.Combine(tempDir, "todo.md");
        await File.WriteAllTextAsync(watchedFile, "v1", Encoding.UTF8);
        string widgetId = $"custom.file-refresh-{Guid.NewGuid():N}";

        try
        {
            var trustStore = new CustomWidgetTrustStore(tempDir);
            trustStore.RegisterWidgetProvenance(widgetId, trustedLocalAuthoring: true);

            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{widgetId}}",
              "name": "File Refresh",
              "state": { "content": "" },
              "sources": [
                { "id": "doc", "type": "fileText", "path": {{JsonSerializer.Serialize(watchedFile)}}, "stateKey": "content", "intervalMs": 60000 }
              ],
              "script": { "entry": "logic.js" },
              "view": { "type": "Text", "text": "${state.content}" }
            }
            """;
            const string logicJs = """
            export function onInit(ctx) {
              ctx.computed.ok = "1";
            }
            export function triggerReload(ctx) {
              ctx.refreshSource("doc");
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = widgetId, Name = "File Refresh", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc)
            {
                TrustStoreOverride = trustStore
            };
            await vm.InitializeAsync();

            for (int i = 0; i < 30 && vm.State.GetValueOrDefault("content") != "v1"; i++)
            {
                await Task.Delay(30);
            }
            Assert.Equal("v1", vm.State["content"]);

            // Overwrite file and immediately call ctx.refreshSource("doc") despite 60s intervalMs
            await File.WriteAllTextAsync(watchedFile, "v2-forced", Encoding.UTF8);
            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""{"type":"script.run","fn":"triggerReload"}"""));

            for (int i = 0; i < 30 && vm.State.GetValueOrDefault("content") != "v2-forced"; i++)
            {
                await Task.Delay(30);
            }
            Assert.Equal("v2-forced", vm.State["content"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Round2Audit_StateSetWithEventValue_PreservesLargeMarkdown_And_UninterpretedExpressions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-r2-stateset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string widgetId = $"custom.r2-stateset-{Guid.NewGuid():N}";

        try
        {
            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{widgetId}}",
              "name": "Large Markdown Guard",
              "state": {
                "secretProbe": "LEAKED_INTERNAL_VALUE",
                "doc": "- [ ] Task 1"
              },
              "view": { "type": "Markdown", "bind": "state.doc", "text": "${state.doc}" }
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = widgetId, Name = "Large Markdown Guard", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            // > 22 KB Markdown containing literal ${state.secretProbe} and $100 tokens
            string largeBody = "- [x] Checked\nPrice: $100\nLiteral token: ${state.secretProbe}\n" + new string('A', 22_000);
            CustomWidgetEvaluationContext evCtx = vm.CreateEvaluationContext().WithEventValue(largeBody);

            await vm.ExecuteActionsAsync(
                [new CustomWidgetActionSpec("state.set", Key: "${state.doc}", Value: "$value")],
                evCtx);

            Assert.Equal(largeBody.Length, vm.State["doc"].Length);
            Assert.Contains("${state.secretProbe}", vm.State["doc"], StringComparison.Ordinal);
            Assert.DoesNotContain("LEAKED_INTERNAL_VALUE", vm.State["doc"], StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Round2Audit_NetworkAndFileSecurityBoundaries_BlockNat64_Ads_And_SelfEscalation()
    {
        // 1. NAT64 well-known prefix 64:ff9b::/96 wrapping AWS metadata 169.254.169.254 and loopback 127.0.0.1
        Assert.True(CustomWidgetNetworkSecurity.IsAlwaysForbiddenWidgetIpAddress(IPAddress.Parse("64:ff9b::a9fe:fea9")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("64:ff9b::7f00:1")));
        // 6to4 prefix 2002::/16 wrapping 169.254.169.254 and 10.0.0.1
        Assert.True(CustomWidgetNetworkSecurity.IsAlwaysForbiddenWidgetIpAddress(IPAddress.Parse("2002:a9fe:fea9::1")));
        Assert.True(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("2002:0a00:0001::1")));
        // Literal 198.18.x.x requires LocalNetwork permission, while DNS Fake-IP resolution is NOT blocked by IsBlockedIpAddress
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://198.18.0.10/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.IsBlockedIpAddress(IPAddress.Parse("198.18.0.10")));

        // 2. NTFS Alternate Data Streams & DeskBox internal directories blocked
        string adsPath = Path.Combine(Path.GetTempPath(), "safe-note.txt:zone.identifier");
        Assert.False(CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(adsPath, forWrite: false, out _, out string? adsErr));
        Assert.Contains("alternate data stream", adsErr ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        string selfConfigPath = Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "widget-trust.json");
        Assert.False(CustomWidgetLocalFileEngine.TryResolveSafeLocalPath(selfConfigPath, forWrite: false, out _, out string? selfErr));
        Assert.Contains("DeskBox", selfErr ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // 3. Community provenance preserved on fork when preserveUntrustedOrigin is set
        const string untrustedManifest = """
        {
          "schemaVersion": 1,
          "id": "ext.untrusted-feed",
          "name": "External Feed",
          "view": { "type": "Text", "text": "Hi" }
        }
        """;
        CustomWidgetForkBundleResult forked = CustomWidgetOnboardingHelper.CreateForkBundle(
            untrustedManifest,
            logicJs: null,
            locales: null,
            activeCulture: "zh-CN",
            preserveUntrustedOrigin: true);
        CustomWidgetParseResult reparsed = CustomWidgetManifestParser.Parse(forked.ManifestJson);
        Assert.Equal(CustomWidgetOriginKinds.Community, reparsed.Manifest?.Origin);
    }

    [Fact]
    public async Task Round2Audit_UntrustedHttpGetConsent_EmptyArrayClear_And_EphemeralStateExclusion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-r2-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string watchedFile = Path.Combine(tempDir, "ephemeral.txt");
        await File.WriteAllTextAsync(watchedFile, "secret-ephemeral-file-body", Encoding.UTF8);
        string widgetId = $"custom.r2-runtime-{Guid.NewGuid():N}";

        try
        {
            int networkHits = 0;
            bool returnEmptyArray = false;
            var handler = new StubHttpMessageHandler((req, _) =>
            {
                Interlocked.Increment(ref networkHits);
                string payload = returnEmptyArray
                    ? "[]"
                    : """[{"id":"1","title":"Live Row"}]""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            });
            var netEngine = new CustomWidgetNetworkSourceEngine(
                Path.Combine(tempDir, "netcache"),
                handler,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
            var trustStore = new CustomWidgetTrustStore(tempDir);
            // Untrusted Community provenance -> GET http.request MUST prompt before hitting network even when PublicNetwork is enabled
            trustStore.RegisterWidgetProvenance(widgetId, trustedLocalAuthoring: false);
            trustStore.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.PublicNetwork, enabled: true, defaultTrustedIfNew: false);
            trustStore.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.LocalFileAccess, enabled: true, defaultTrustedIfNew: false);

            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{widgetId}}",
              "origin": "community",
              "name": "Untrusted Runtime Check",
              "collections": { "rows": {} },
              "sources": [
                { "id": "f1", "type": "fileText", "path": {{JsonSerializer.Serialize(watchedFile)}} }
              ],
              "view": {
                "type": "Button",
                "text": "Fetch",
                "onClick": [
                  {
                    "type": "http.request",
                    "method": "GET",
                    "url": "https://api.example.com/items",
                    "collection": "rows",
                    "mode": "replace"
                  }
                ]
              }
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            bool allowConsent = false;
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = widgetId, Name = "Untrusted Runtime Check", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                networkSourceEngine: netEngine,
                settingsService: settingsSvc)
            {
                TrustStoreOverride = trustStore,
                ExternalActionConsentPrompt = _ => Task.FromResult(allowConsent)
            };
            await vm.InitializeAsync();

            for (int i = 0; i < 30 && !vm.State.ContainsKey("fileText"); i++)
            {
                await Task.Delay(30);
            }
            Assert.Equal("secret-ephemeral-file-body", vm.State.GetValueOrDefault("fileText"));

            // 1. Denied consent -> 0 network requests!
            await vm.ExecuteActionsAsync(vm.Manifest.View.OnClick);
            Assert.Equal(0, networkHits);

            // 2. Approved consent -> populates rows, then empty [] response clears rows!
            allowConsent = true;
            await vm.ExecuteActionsAsync(vm.Manifest.View.OnClick);
            Assert.Equal(1, networkHits);
            Assert.Single(vm.Collections["rows"]);

            returnEmptyArray = true;
            await vm.ExecuteActionsAsync(vm.Manifest.View.OnClick);
            Assert.Equal(2, networkHits);
            Assert.Empty(vm.Collections["rows"]);

            // 3. Persisted data.json must NOT contain ephemeral fileText dump
            CustomWidgetStorePayload persisted = await store.LoadAsync();
            Assert.False(persisted.State.ContainsKey("fileText"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Round2Audit_CompilerValidatorAndStudioPatch_HandleCanvasLinePivot_ZeroPadding_And_DeleteScript()
    {
        // 1. Canvas elementsSource + line with cx/cy pivot must NOT overwrite x1/y1, and runtime state keys pass validation
        const string rawManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.r2-compiler",
          "name": "Clock & File Widget",
          "sources": [
            { "id": "notes", "type": "fileText", "path": "~/notes.md" }
          ],
          "view": {
            "type": "Stack",
            "padding": "0",
            "children": [
              {
                "type": "Canvas",
                "viewBox": "0 0 200 200",
                "elementsSource": "${$computed.hands}",
                "elements": [
                  {
                    "kind": "line",
                    "x1": 100,
                    "y1": 100,
                    "x2": 100,
                    "y2": 28,
                    "cx": 100,
                    "cy": 100,
                    "rotation": "${$computed.deg}"
                  },
                  {
                    "kind": "text",
                    "x": 100,
                    "y": 170,
                    "fontWeight": "bold",
                    "text": "${state.fileLineCount}"
                  }
                ]
              }
            ]
          }
        }
        """;

        string autoWired = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(rawManifest);
        CustomWidgetParseResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            autoWired,
            """
            export function onInit(ctx) {
              ctx.computed.hands = [];
              ctx.computed.deg = 90;
            }
            """);

        Assert.NotNull(validated.Manifest);
        Assert.DoesNotContain(validated.Diagnostics, d => d.IsError);
        // Root explicit "padding": "0" preserved by AutoFixer
        Assert.Equal("0", validated.Manifest!.View.Padding);

        CustomWidgetNodeSpec canvasNode = validated.Manifest.View.Children[0];
        Assert.Equal("${$computed.hands}", canvasNode.ElementsSource);
        CustomWidgetCanvasElementSpec hand = canvasNode.Elements[0];
        Assert.Equal("100", hand.X);
        Assert.Equal("100", hand.Y);
        Assert.Equal("100", hand.Cx);
        Assert.Equal("100", hand.Cy);
        Assert.Equal("${$computed.deg}", hand.RotationExpression);
        Assert.Equal("bold", canvasNode.Elements[1].FontWeight);

        // 2. Studio deskbox-manifest-patch changing node "type" drops old type-specific props while keeping Grid column,
        //    and deskbox-meta deleteScript removes logic.js cleanly
        const string priorManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.patch-type-swap",
          "name": "Swap Test",
          "script": { "entry": "logic.js" },
          "collections": { "rows": {} },
          "view": {
            "type": "Grid",
            "columns": "*,*",
            "children": [
              { "type": "Text", "column": 0, "text": "Left" },
              {
                "type": "Stack",
                "column": 1,
                "orientation": "horizontal",
                "spacing": 12,
                "children": [{ "type": "Text", "text": "Old child" }]
              }
            ]
          }
        }
        """;

        const string patchTurn = """
        ```deskbox-meta
        { "kind": "edit", "reply": "Replaced Stack with Table and removed script.", "deleteScript": true }
        ```
        ```deskbox-manifest-patch
        {
          "view": {
            "children": [
              { "type": "Text", "column": 0, "text": "Left" },
              {
                "type": "Table",
                "items": "rows",
                "columns": [{ "key": "title", "header": "Title" }]
              }
            ]
          }
        }
        ```
        """;

        StudioTurnResult turnRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            patchTurn,
            currentLogicJs: "export function onInit(ctx) {}",
            fallbackCulture: "en-US",
            currentManifestJson: priorManifest);

        Assert.True(turnRes.Success, turnRes.ErrorMessage);
        Assert.Equal(StudioLogicJsPatchMode.Remove, turnRes.LogicJsPatchMode);
        Assert.Null(turnRes.ParseResult?.Manifest?.Script);

        CustomWidgetNodeSpec swappedChild = turnRes.ParseResult!.Manifest!.View.Children[1];
        Assert.Equal("Table", swappedChild.Type);
        Assert.Equal(1, swappedChild.Column);
        Assert.Empty(swappedChild.Children);

        // 3. Script-deletion-only turn (no manifest/patch block) also transitions cleanly
        const string deleteScriptOnlyTurn = """
        ```deskbox-meta
        { "kind": "edit", "reply": "Removed unneeded script.", "deleteScript": true }
        ```
        """;
        StudioTurnResult delOnlyRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            deleteScriptOnlyTurn,
            currentLogicJs: "export function onInit(ctx) {}",
            fallbackCulture: "en-US",
            currentManifestJson: priorManifest);
        Assert.True(delOnlyRes.Success, delOnlyRes.ErrorMessage);
        Assert.Equal("edit", delOnlyRes.Kind);
        Assert.Equal(StudioLogicJsPatchMode.Remove, delOnlyRes.LogicJsPatchMode);
        Assert.Null(delOnlyRes.ParseResult?.Manifest?.Script);
    }
}
