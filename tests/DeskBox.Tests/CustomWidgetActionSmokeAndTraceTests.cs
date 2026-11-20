using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetActionSmokeAndTraceTests
{
    [Fact]
    public void SemanticIcons_ResolveCommonNames_PreserveGlyphsAndEmoji_AndSuggestUnknownNames()
    {
        Assert.True(CustomWidgetSemanticIcons.TryResolve("add", out string addGlyph));
        Assert.Equal("\uE710", addGlyph);
        Assert.Equal("\uE710", CustomWidgetManifestParser.NormalizeGlyph("plus"));
        Assert.Equal("\uE70F", CustomWidgetManifestParser.NormalizeGlyph("edit"));
        Assert.Equal("\uE74D", CustomWidgetManifestParser.NormalizeGlyph("delete"));
        Assert.Equal("\uE74D", CustomWidgetManifestParser.NormalizeGlyph("trash"));
        Assert.Equal("\uEDE1", CustomWidgetManifestParser.NormalizeGlyph("export"));
        Assert.Equal("\uE72D", CustomWidgetManifestParser.NormalizeGlyph("share"));
        Assert.Equal("\uE8B7", CustomWidgetManifestParser.NormalizeGlyph("folder"));
        Assert.Equal("\uE713", CustomWidgetManifestParser.NormalizeGlyph("settings"));
        Assert.Equal("\uE710", CustomWidgetManifestParser.NormalizeGlyph("\\uE710"));
        Assert.Equal("\uE710", CustomWidgetManifestParser.NormalizeGlyph("E710"));
        Assert.Equal("📅", CustomWidgetManifestParser.NormalizeGlyph("📅"));

        Assert.True(CustomWidgetSemanticIcons.TryReverseLookup("\uE710", out string? reverseAdd));
        Assert.Equal("add", reverseAdd);

        const string manifestWithTypoIcon = """
        {
          "schemaVersion": 1,
          "id": "custom.icon-typo-test",
          "name": "Icon Typo Test",
          "icon": "calendar",
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["export_btn"]
            },
            "export_btn": { "type": "IconButton", "icon": "exportt", "onClick": { "type": "state.set", "key": "x", "value": "1" } }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestWithTypoIcon);
        Assert.True(parsed.IsValid);
        Assert.Equal("\uE787", parsed.Manifest!.Icon);
        CustomWidgetValidationDiagnostic? warn = Assert.Single(
            parsed.Diagnostics,
            static d => string.Equals(d.Code, "UnknownSemanticIconName", StringComparison.Ordinal));
        Assert.False(warn.IsError);
        Assert.Contains("export", warn.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WidgetFilePathResolver_SupportsManagedRelativeExports_AndBlocksTraversalOrFileScheme()
    {
        string expectedRoot = CustomWidgetLocalFileEngine.ResolveWidgetManagedDocumentsRoot("custom.schedule-weekly");

        Assert.True(CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            "exports/schedule.md",
            "custom.schedule-weekly",
            forWrite: true,
            out string managedFull,
            out bool isManaged,
            out string? err));
        Assert.Null(err);
        Assert.True(isManaged);
        Assert.StartsWith(expectedRoot, managedFull, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("exports", "schedule.md"), managedFull, StringComparison.OrdinalIgnoreCase);

        Assert.True(CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            "Desktop\\schedule.md",
            "custom.schedule-weekly",
            forWrite: true,
            out string desktopFull,
            out bool isDesktopManaged,
            out string? desktopErr));
        Assert.Null(desktopErr);
        Assert.False(isDesktopManaged);
        Assert.EndsWith("schedule.md", desktopFull, StringComparison.OrdinalIgnoreCase);

        Assert.False(CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            "../outside.txt",
            "custom.schedule-weekly",
            forWrite: true,
            out _,
            out _,
            out string? traversalErr));
        Assert.Contains("..", traversalErr ?? string.Empty, StringComparison.Ordinal);

        Assert.False(CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            "file:///C:/Users/test/a.md",
            "custom.schedule-weekly",
            forWrite: false,
            out _,
            out _,
            out string? fileUriErr));
        Assert.Contains("file://", fileUriErr ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.False(CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
            @"\\evil-server\share\a.txt",
            "custom.schedule-weekly",
            forWrite: false,
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task FileWriteText_RelativePath_WritesToManagedDocumentsAndInvokesOnCompleteCallback()
    {
        string uniqueSlug = $"custom.test-export-{Guid.NewGuid():N}";
        string managedDir = CustomWidgetLocalFileEngine.ResolveWidgetManagedDocumentsRoot(uniqueSlug);
        string tempStoreDir = Path.Combine(Path.GetTempPath(), $"deskbox-test-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStoreDir);

        try
        {
            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{uniqueSlug}}",
              "name": "Export Test",
              "origin": "local",
              "script": { "entry": "logic.js" },
              "state": { "exportedPath": "", "callbackCount": "0" },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["export_btn"]
                },
                "export_btn": { "type": "Button", "text": "Export", "onClick": { "type": "script.run", "fn": "doExport" } }
              }
            }
            """;
            const string logicJs = """
            export function doExport(ctx) {
              ctx.dispatch({
                type: "file.writeText",
                path: "weekly-report.md",
                text: "# Schedule\n- Math",
                onComplete: "onExportDone"
              });
            }
            export function onExportDone(ctx, res) {
              const r = res || {};
              ctx.state.exportedPath = r.path || "";
              ctx.state.callbackCount = r.ok ? "1" : "0";
              if (r.ok && r.bytesWritten > 0) {
                ctx.dispatch({ type: "state.set", key: "followUpRan", value: "yes" });
              }
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempStoreDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            var store = new CustomWidgetDataStore(uniqueSlug, tempStoreDir);
            var trustStore = new CustomWidgetTrustStore(Path.Combine(tempStoreDir, "trust"));
            trustStore.RegisterWidgetProvenance(uniqueSlug, trustedLocalAuthoring: true);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: logicJs, locales: null);

            var config = new WidgetConfig
            {
                Id = uniqueSlug,
                Name = "Export Test",
                WidgetKind = WidgetKind.Custom
            };
            using var vm = new CustomWidgetViewModel(config, locSvc, store, settingsService: settingsSvc, isPreviewOnly: false)
            {
                TrustStoreOverride = trustStore
            };
            await vm.InitializeAsync();

            var runAction = new CustomWidgetActionSpec("script.run", Fn: "doExport");
            await vm.ExecuteActionsAsync([runAction]);

            string expectedFile = Path.Combine(managedDir, "weekly-report.md");
            Assert.True(File.Exists(expectedFile), $"Expected exported file at '{expectedFile}'.");
            Assert.Equal("# Schedule\n- Math", await File.ReadAllTextAsync(expectedFile));
            Assert.Equal("ok", vm.StateInternal["fileWriteStatus"]);
            Assert.Equal(expectedFile, vm.StateInternal["fileWritePath"]);
            Assert.Equal(expectedFile, vm.StateInternal["exportedPath"]);
            Assert.Equal("1", vm.StateInternal["callbackCount"]);
            Assert.Equal("yes", vm.StateInternal["followUpRan"]);
            Assert.NotEmpty(vm.RuntimeTrace.SnapshotRecent());
        }
        finally
        {
            try { if (Directory.Exists(managedDir)) Directory.Delete(managedDir, recursive: true); } catch { }
            try { if (Directory.Exists(tempStoreDir)) Directory.Delete(tempStoreDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeTraceBuffer_CapsAt15Entries_AndScrubsSensitiveTokensAndUserProfile()
    {
        var buffer = new CustomWidgetRuntimeTraceBuffer();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (int i = 0; i < 20; i++)
        {
            buffer.Record(
                "http.request",
                $"{userProfile}\\secret-file-{i}.txt",
                ok: i % 2 == 0,
                detail: $"Authorization: Bearer sk-super-secret-token-{i}");
        }

        IReadOnlyList<CustomWidgetRuntimeTraceEntry> entries = buffer.SnapshotRecent();
        Assert.Equal(CustomWidgetRuntimeTraceBuffer.MaxEntries, entries.Count);
        Assert.True(buffer.HasRecentErrors);

        string formatted = buffer.FormatForAiPrompt(5);
        Assert.DoesNotContain("sk-super-secret-token", formatted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", formatted, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            Assert.DoesNotContain(userProfile, formatted, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", formatted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SmokeVerifier_CatchesRealSessionFailureClasses_ViaDryRunAndMultiStateAudit()
    {
        const string brokenManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.smoke-regression",
          "name": "Smoke Regression",
          "script": { "file": "logic.js" },
          "state": {
            "viewMode": "week",
            "editName": "",
            "editRoom": ""
          },
          "collections": {
            "courses": { "fields": ["title", "room"] }
          },
          "root": "main",
          "popovers": {
            "courseEditor": {
              "title": "Course Editor",
              "width": 280,
              "root": "course_editor_body"
            }
          },
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["view_mode_switcher", "dead_stats_pane", "toolbar"]
            },
            "view_mode_switcher": {
              "type": "Segmented",
              "bind": "state.viewMode",
              "options": [
                { "label": "Week", "value": "week" },
                { "label": "Today", "value": "today" }
              ]
            },
            "dead_stats_pane": {
              "type": "Stack",
              "id": "deadStatsPane",
              "when": "state.viewMode == 'stats'",
              "children": ["dead_stats_text"]
            },
            "dead_stats_text": { "type": "Text", "text": "Unreachable stats" },
            "toolbar": {
              "type": "Stack",
              "id": "toolbar",
              "orientation": "horizontal",
              "children": ["export_btn_primary", "export_btn_dup", "edit_btn"]
            },
            "export_btn_primary": { "type": "Button", "text": "导出课程表", "onClick": { "type": "script.run", "fn": "exportBad" } },
            "export_btn_dup": { "type": "Button", "text": "导出课程表", "onClick": { "type": "script.run", "fn": "openBadUrl" } },
            "edit_btn": { "type": "Button", "text": "编辑", "onClick": { "type": "script.run", "fn": "editWithoutOpen" } },
            "course_editor_body": {
              "type": "Stack",
              "children": ["edit_name_input", "edit_room_input"]
            },
            "edit_name_input": { "type": "Input", "bind": "state.editName" },
            "edit_room_input": { "type": "Input", "bind": "state.editRoom" }
          }
        }
        """;

        const string brokenLogicJs = """
        export function exportBad(ctx) {
          ctx.dispatch({
            type: "file.writeText",
            path: "schedule.md",
            text: "hi",
            onComplete: { type: "toast.show", message: "wrong object shape" }
          });
        }
        export function openBadUrl(ctx) {
          ctx.dispatch({
            type: "url.open",
            url: "file:///C:/Users/demo/schedule.md"
          });
        }
        export function editWithoutOpen(ctx) {
          ctx.state.editName = "Math";
          ctx.state.editRoom = "Room 101";
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(brokenManifestJson);
        Assert.NotNull(parsed.Manifest);

        IReadOnlyList<CustomWidgetValidationDiagnostic> smokeDiags = StudioBehavioralSmokeVerifier.Verify(
            parsed.Manifest,
            brokenManifestJson,
            brokenLogicJs,
            locales: null,
            sampleDataJson: null);

        Assert.Contains(smokeDiags, static d => d.IsError && d.Code == "BehavioralUnreachableStateBranch");
        Assert.Contains(smokeDiags, static d => d.IsError && d.Code == "BehavioralDuplicateSiblingButton");
        Assert.Contains(smokeDiags, static d => d.IsError && d.Code == "ScriptDispatchInvalidCallbackShape");
        Assert.Contains(smokeDiags, static d => d.IsError && d.Code == "ScriptDispatchInvalidUrlScheme");
        Assert.Contains(smokeDiags, static d => d.IsError && d.Code == "BehavioralFormEditMissingPopoverOpen");
    }

    [Fact]
    public void SmokeVerifier_PassesCleanlyWhenHandlersAndBranchesAreValid()
    {
        const string validManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.smoke-clean",
          "name": "Smoke Clean",
          "script": { "file": "logic.js" },
          "state": {
            "viewMode": "week",
            "editName": "",
            "editRoom": ""
          },
          "root": "main",
          "popovers": {
            "courseEditor": {
              "title": "Course Editor",
              "width": 280,
              "root": "course_editor_body"
            }
          },
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["view_mode_switcher", "stats_pane", "toolbar"]
            },
            "view_mode_switcher": {
              "type": "Segmented",
              "bind": "state.viewMode",
              "options": [
                { "label": "Week", "value": "week" },
                { "label": "Stats", "value": "stats" }
              ]
            },
            "stats_pane": {
              "type": "Stack",
              "id": "statsPane",
              "when": "state.viewMode == 'stats'",
              "children": ["stats_text"]
            },
            "stats_text": { "type": "Text", "text": "Reachable stats" },
            "toolbar": {
              "type": "Stack",
              "id": "toolbar",
              "orientation": "horizontal",
              "children": ["export_btn", "edit_btn"]
            },
            "export_btn": { "type": "Button", "icon": "export", "text": "导出", "onClick": { "type": "script.run", "fn": "exportGood" } },
            "edit_btn": { "type": "Button", "icon": "edit", "text": "编辑", "onClick": { "type": "script.run", "fn": "editAndOpen" } },
            "course_editor_body": {
              "type": "Stack",
              "children": ["edit_name_input", "edit_room_input"]
            },
            "edit_name_input": { "type": "Input", "bind": "state.editName" },
            "edit_room_input": { "type": "Input", "bind": "state.editRoom" }
          }
        }
        """;

        const string validLogicJs = """
        export function exportGood(ctx) {
          ctx.dispatch({
            type: "file.writeText",
            path: "schedule.md",
            text: "hi",
            onComplete: "onExported"
          });
        }
        export function onExported(ctx) {
          ctx.toast("Exported");
        }
        export function editAndOpen(ctx) {
          ctx.state.editName = "Math";
          ctx.state.editRoom = "Room 101";
          ctx.dispatch({ type: "popover.open", popoverId: "courseEditor" });
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(validManifestJson);
        Assert.True(parsed.IsValid);

        IReadOnlyList<CustomWidgetValidationDiagnostic> smokeDiags = StudioBehavioralSmokeVerifier.Verify(
            parsed.Manifest,
            validManifestJson,
            validLogicJs,
            locales: null,
            sampleDataJson: null);

        Assert.DoesNotContain(smokeDiags, static d => d.IsError);
    }
}
