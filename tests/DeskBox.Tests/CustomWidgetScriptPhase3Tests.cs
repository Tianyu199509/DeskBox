using System.Text;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CustomWidgetScriptPhase3Tests
{
    private const string BaseScriptManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.phase3-script",
          "name": "$i18n.title",
          "version": "1.0.0",
          "icon": "⚡",
          "defaultSize": { "width": 320, "height": 240 },
          "state": { "count": "2" },
          "script": { "entry": "logic.js" },
          "capsule": {
            "title": "$i18n.title",
            "summary": "${$computed.banner}",
            "badge": "${state.count}",
            "badgeIsWarning": "${Number(state.count) >= 10}",
            "primaryAction": {
              "icon": "\uE710",
              "tooltip": "Increment",
              "onClick": [
                { "type": "script.run", "fn": "bump", "arg": "1" }
              ]
            }
          },
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              { "type": "Text", "text": "${$computed.banner}", "style": "bodyStrong" },
              {
                "type": "Button",
                "text": "+1",
                "onClick": [
                  { "type": "script.run", "fn": "bump", "arg": "1" }
                ]
              }
            ]
          }
        }
        """;

    private const string ValidLogicJs = """
        export function onInit(ctx) {
          const n = Number(ctx.state.count || 0);
          ctx.computed.banner = ctx.t("bannerFmt", n);
        }

        export function bump(ctx, delta) {
          const next = Number(ctx.state.count || 0) + Number(delta || 1);
          ctx.state.count = String(next);
          ctx.computed.banner = ctx.t("bannerFmt", next);
        }
        """;

    private const string ValidLocalesCatalogJson = """
        {
          "zh-CN": {
            "title": "脚本计数器",
            "bannerFmt": "当前计数：{0}"
          },
          "en-US": {
            "title": "Script Counter",
            "bannerFmt": "Current count: {0}"
          }
        }
        """;

    private static CustomWidgetLocaleCatalog CreateValidLocales() =>
        CustomWidgetLocaleCatalog.ParseCatalogJson(ValidLocalesCatalogJson);

    [Fact]
    public void ValidateWorkspaceBundle_ValidBundle_PassesDryRunAndProducesCompiledScript()
    {
        CustomWidgetCompiledScript? compiled = ValidateAndReturnCompiled(
            BaseScriptManifestJson,
            ValidLogicJs,
            CreateValidLocales(),
            out CustomWidgetWorkspaceValidationResult result);

        Assert.True(result.IsValid, string.Join(" | ", result.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        Assert.NotNull(compiled);
        Assert.Contains("onInit", compiled!.ExportedFunctions);
        Assert.Contains("bump", compiled.ExportedFunctions);
    }

    [Fact]
    public void ValidateWorkspaceBundle_SyntaxError_ReportsExactLogicJsLine()
    {
        const string brokenJs = """
            export function onInit(ctx) {
              ctx.computed.banner = "ok";
              const oops = (;
            }
            """;

        CustomWidgetWorkspaceValidationResult result = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            BaseScriptManifestJson,
            brokenJs,
            CreateValidLocales());

        Assert.False(result.IsValid);
        CustomWidgetValidationDiagnostic err = Assert.Single(result.Diagnostics, d => d.IsError && d.Code == "ScriptSyntaxError");
        Assert.Equal("logic.js:3", err.Path);
    }

    [Fact]
    public void ValidateWorkspaceBundle_MissingScriptRunExport_ReportsError()
    {
        const string missingBumpJs = """
            export function onInit(ctx) {
              ctx.computed.banner = "ready";
            }
            """;

        CustomWidgetCompiledScript? _ = ValidateAndReturnCompiled(
            BaseScriptManifestJson,
            missingBumpJs,
            CreateValidLocales(),
            out CustomWidgetWorkspaceValidationResult result);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("bump", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateWorkspaceBundle_OnInitRuntimeException_ReportsLineAndMessage()
    {
        const string throwingInitJs = """
            export function onInit(ctx) {
              ctx.computed.banner = "start";
              throw new Error("boom_in_onInit");
            }

            export function bump(ctx) {
              ctx.computed.banner = "ok";
            }
            """;

        CustomWidgetCompiledScript? _ = ValidateAndReturnCompiled(
            BaseScriptManifestJson,
            throwingInitJs,
            CreateValidLocales(),
            out CustomWidgetWorkspaceValidationResult result);

        Assert.False(result.IsValid);
        CustomWidgetValidationDiagnostic err = Assert.Single(result.Diagnostics, d => d.IsError && d.Code == "ScriptOnInitRuntimeError");
        Assert.Contains("boom_in_onInit", err.Message, StringComparison.Ordinal);
        Assert.StartsWith("logic.js:", err.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLocalesDirectory_InvalidLocaleJson_ReportsError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-invalid-locales-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "zh-CN.json"), "{ invalid json", Encoding.UTF8);
            var diagnostics = new List<CustomWidgetValidationDiagnostic>();
            CustomWidgetScriptValidator.ValidateLocalesDirectory(tempDir, diagnostics);

            Assert.Contains(diagnostics, d => d.IsError && d.Path.StartsWith("locales/zh-CN.json", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SyncManifestScriptPropertyJson_And_ExtractWorkspaceBundleFromClipboardText_RoundTrip()
    {
        const string bareManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.tick-demo",
              "name": "Tick Demo",
              "view": { "type": "Text", "text": "${$computed.clock}" }
            }
            """;
        const string tickJs = """
            export function onInit(ctx) { ctx.computed.clock = "00:00"; }
            export function onTick(ctx, nowMs) { ctx.computed.clock = String(nowMs); }
            """;

        string clipboardReply = $"Here is your widget:\n```json\n{bareManifest}\n```\n```js\n{tickJs}\n```";
        (string extractedJson, string? extractedJs) = CustomWidgetTemplates.ExtractWorkspaceBundleFromClipboardText(clipboardReply);

        Assert.NotEmpty(extractedJson);
        Assert.NotNull(extractedJs);
        Assert.Contains("onTick", extractedJs, StringComparison.Ordinal);

        string withScript = CustomWidgetTemplates.SyncManifestScriptPropertyJson(extractedJson, extractedJs);
        CustomWidgetParseResult parsedWith = CustomWidgetManifestParser.Parse(withScript);
        Assert.NotNull(parsedWith.Manifest?.Script);
        Assert.Equal("logic.js", parsedWith.Manifest!.Script!.Entry);
        Assert.Equal(1000, parsedWith.Manifest.Script.TickIntervalMs);

        string stripped = CustomWidgetTemplates.SyncManifestScriptPropertyJson(withScript, null);
        CustomWidgetParseResult parsedWithout = CustomWidgetManifestParser.Parse(stripped);
        Assert.Null(parsedWithout.Manifest?.Script);
    }

    [Fact]
    public void ParseStudioTurnResponse_ThreeStateLogicJsAndLocales_RoundTrip()
    {
        CustomWidgetLocaleCatalog validLocales = CreateValidLocales();
        string keepEnvelope = $$"""
            {
              "kind": "edit",
              "reply": "Updated title",
              "changeSummary": "Title tweak",
              "manifest": {{BaseScriptManifestJson}}
            }
            """;

        StudioTurnResult keepTurn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            keepEnvelope,
            currentLogicJs: ValidLogicJs,
            currentLocales: validLocales);
        Assert.True(keepTurn.Success);
        Assert.Equal(StudioLogicJsPatchMode.KeepExisting, keepTurn.LogicJsPatchMode);
        Assert.Equal(
            ValidLogicJs,
            CustomWidgetAiGeneratorService.ResolveEffectiveLogicJs(ValidLogicJs, keepTurn.LogicJsPatchMode, keepTurn.LogicJs));

        string replaceEnvelope = $$"""
            {
              "kind": "edit",
              "reply": "Added locales and updated script",
              "changeSummary": "Script + locales",
              "logicJs": "export function onInit(ctx) { ctx.computed.banner = 'v2'; }\nexport function bump(ctx) { ctx.computed.banner = 'v3'; }",
              "locales": {{ValidLocalesCatalogJson}},
              "manifest": {{BaseScriptManifestJson}}
            }
            """;

        StudioTurnResult replaceTurn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            replaceEnvelope,
            currentLogicJs: ValidLogicJs);
        Assert.True(replaceTurn.Success);
        Assert.Equal(StudioLogicJsPatchMode.Replace, replaceTurn.LogicJsPatchMode);
        Assert.Contains("v2", replaceTurn.LogicJs, StringComparison.Ordinal);
        Assert.NotNull(replaceTurn.Locales);
        Assert.Equal("Script Counter", replaceTurn.Locales!.Locales["en-US"]["title"]);

        const string pureManifestWithoutScript = """
            {
              "schemaVersion": 1,
              "id": "custom.pure",
              "name": "Pure Declarative",
              "view": { "type": "Text", "text": "Hello" }
            }
            """;
        string removeEnvelope = $$"""
            {
              "kind": "edit",
              "reply": "Removed script",
              "changeSummary": "Pure declarative",
              "logicJs": null,
              "manifest": {{pureManifestWithoutScript}}
            }
            """;

        StudioTurnResult removeTurn = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            removeEnvelope,
            currentLogicJs: ValidLogicJs);
        Assert.True(removeTurn.Success);
        Assert.Equal(StudioLogicJsPatchMode.Remove, removeTurn.LogicJsPatchMode);
        Assert.Null(CustomWidgetAiGeneratorService.ResolveEffectiveLogicJs(ValidLogicJs, removeTurn.LogicJsPatchMode, removeTurn.LogicJs));
    }

    [Fact]
    public void StudioSessionStore_PersistsLogicJsAndLocalesInRevisions_AndReconcilesExternalScriptEdits()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-phase3-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomWidgetStudioSessionStore(tempRoot);
            const string widgetId = "phase3-session-widget";
            CustomWidgetLocaleCatalog validLocales = CreateValidLocales();

            var rev1 = new StudioPersistedRevision
            {
                RevisionId = "rev-1",
                Source = "ai",
                ChangeSummary = "Initial script widget",
                ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(
                    BaseScriptManifestJson,
                    ValidLogicJs,
                    validLocales),
                IsCommitted = true,
                ManifestJson = BaseScriptManifestJson,
                LogicJs = ValidLogicJs,
                Locales = validLocales
            };

            store.SaveCommittedWidgetSession(widgetId, "# Brief", [rev1], []);

            StudioLoadedSession loadedUnchanged = store.LoadOrReconcileWidgetSession(
                widgetId,
                BaseScriptManifestJson,
                "External sync",
                currentDiskLogicJs: ValidLogicJs,
                currentDiskLocales: validLocales);

            Assert.False(loadedUnchanged.WasReconciledWithExternalEdit);
            StudioPersistedRevision persisted = Assert.Single(loadedUnchanged.Revisions);
            Assert.Equal(ValidLogicJs, persisted.LogicJs);
            Assert.NotNull(persisted.Locales);
            Assert.Equal("脚本计数器", persisted.Locales!.Locales["zh-CN"]["title"]);

            string externallyEditedJs = ValidLogicJs + "\n// edited in external IDE";
            StudioLoadedSession reconciled = store.LoadOrReconcileWidgetSession(
                widgetId,
                BaseScriptManifestJson,
                "External sync",
                currentDiskLogicJs: externallyEditedJs,
                currentDiskLocales: validLocales);

            Assert.True(reconciled.WasReconciledWithExternalEdit);
            Assert.Equal(2, reconciled.Revisions.Count);
            Assert.Equal(externallyEditedJs, reconciled.Revisions[^1].LogicJs);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PackageService_SharePackageZip_ScrubSecretsAndRoundTripBundle()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-phase3-pkg-" + Guid.NewGuid().ToString("N"));
        string widgetId = "test-pkg-" + Guid.NewGuid().ToString("N");
        CustomWidgetDataStore widgetStore = CustomWidgetDataStore.ForWidget(widgetId);
        try
        {
            Directory.CreateDirectory(tempDir);
            string dirtyLogicJs = ValidLogicJs + "\n// path C:\\Users\\Alice\\secret.txt token sk-1234567890abcdefghijklmnop";
            await widgetStore.WriteManifestJsonAsync(BaseScriptManifestJson);
            await widgetStore.WriteOrDeleteLogicJsAsync(dirtyLogicJs);
            await widgetStore.WriteOrDeleteLocalesAsync(CreateValidLocales());

            string sharePkgPath = Path.Combine(tempDir, "counter.deskbox-widget");
            CustomWidgetExportReport shareReport = await CustomWidgetPackageService.ExportSharePackageAsync(widgetId, sharePkgPath);
            Assert.True(shareReport.SanitizedPathsCount >= 1);
            Assert.True(shareReport.StrippedSecretsCount >= 1);

            CustomWidgetImportedBundle importedShare = await CustomWidgetPackageService.ReadPackageBundleAsync(sharePkgPath);
            Assert.NotNull(importedShare.LogicJs);
            Assert.DoesNotContain("Alice", importedShare.LogicJs, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-1234567890", importedShare.LogicJs, StringComparison.Ordinal);
            Assert.NotNull(importedShare.Locales);
            Assert.False(importedShare.Locales!.IsEmpty);
            Assert.Equal("脚本计数器", importedShare.Manifest.ResolveLocalizedName("zh-CN"));
            Assert.Equal("Script Counter", importedShare.Manifest.ResolveLocalizedName("en-US"));

            string zipPkgPath = Path.Combine(tempDir, "counter.dbbox");
            _ = await CustomWidgetPackageService.ExportSharePackageAsync(widgetId, zipPkgPath);
            CustomWidgetImportedBundle importedZip = await CustomWidgetPackageService.ReadPackageBundleAsync(zipPkgPath);
            Assert.NotNull(importedZip.LogicJs);
            Assert.NotNull(importedZip.Locales);
            Assert.Equal("Script Counter", importedZip.Manifest.ResolveLocalizedName("en-US"));
        }
        finally
        {
            CustomWidgetDataStore.ReleaseWidgetIfUnused(widgetId);
            if (Directory.Exists(widgetStore.WidgetDirectory))
            {
                Directory.Delete(widgetStore.WidgetDirectory, recursive: true);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CapabilityRegistry_ScriptSandbox_AliasesAstBackfillAndCloudBackupDomain_WorkEndToEnd()
    {
        CustomWidgetCapabilitySpec? spec = CustomWidgetCapabilityRegistry.GetById("script-sandbox");
        Assert.NotNull(spec);
        Assert.Same(spec, CustomWidgetCapabilityRegistry.GetById("script"));
        Assert.Same(spec, CustomWidgetCapabilityRegistry.GetById("logic.js"));

        IReadOnlyList<string> mentioned = CustomWidgetCapabilityRegistry.ExtractMentionedCapabilityIds(
            "请使用 @script 和 @logic.js 处理复杂状态机。");
        Assert.Equal(["script-sandbox"], mentioned);

        var (_, autoSpecs) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: BaseScriptManifestJson,
            userPrompt: "改一下背景间距");
        Assert.Contains(autoSpecs, s => s.Id == "script-sandbox");

        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-demo/logic.js"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-demo/locales/zh-CN.json"));
        Assert.True(CloudBackupDomains.IsInDomain(CloudBackupDomain.CustomWidgets, "custom-widgets/w-demo/locales/_meta.json"));
    }

    [Fact]
    public async Task CliAndBridgeValidate_WithSiblingLogicJsAndLocalesDir_ValidateEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-phase4-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string manifestPath = Path.Combine(tempDir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, BaseScriptManifestJson, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "logic.js"), ValidLogicJs, Encoding.UTF8);
            CustomWidgetLocaleCatalog.SaveToDirectory(Path.Combine(tempDir, "locales"), CreateValidLocales());

            int okExit = await DeskBoxAgentCliEntry.RunWidgetCliAsync(["validate", "--file", manifestPath]);
            Assert.Equal(0, okExit);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "logic.js"), "export function onInit(ctx) { throw new Error('cli_fail'); }", Encoding.UTF8);
            int failExit = await DeskBoxAgentCliEntry.RunWidgetCliAsync(["validate", "--file", manifestPath]);
            Assert.Equal(1, failExit);

            await using var bridge = new DeskBoxAgentBridgeServer("deskbox-test-pipe-" + Guid.NewGuid().ToString("N"), static () => null);
            string reqJson = $$"""
                {
                  "op": "validate",
                  "manifestJson": {{System.Text.Json.JsonSerializer.Serialize(BaseScriptManifestJson)}},
                  "logicJs": {{System.Text.Json.JsonSerializer.Serialize(ValidLogicJs)}},
                  "locales": {{ValidLocalesCatalogJson}}
                }
                """;
            string respJson = await bridge.DispatchRequestJsonAsync(reqJson);
            Assert.Contains("\"isValid\":true", respJson.Replace(" ", string.Empty), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static CustomWidgetCompiledScript? ValidateAndReturnCompiled(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        out CustomWidgetWorkspaceValidationResult result)
    {
        result = CustomWidgetScriptValidator.ValidateWorkspaceBundle(manifestJson, logicJs, locales);
        return result.CompiledScript;
    }
}
