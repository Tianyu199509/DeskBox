using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StudioSubtreeGuardAndDualPassValidatorTests
{
    [Fact]
    public void SubtreeGuard_LocksElementPath_PermitsFlatTargetEdits_GraftsFullBundle_AndRejectsRemovedTarget()
    {
        const string initialManifest = """
        {
          "schemaVersion": 1,
          "id": "test.subtree.guard",
          "name": "Subtree Guard Test",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["siblingHeader", "selectedBody"] },
            "siblingHeader": { "type": "Text", "text": "Untouched Header" },
            "selectedBody": { "type": "Text", "text": "Original Body" }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(
            initialManifest,
            initialLogicJs: null,
            initialLocales: null,
            targetNodePath: "#selectedBody");

        Assert.Equal("$.elements.selectedBody", engine.LockedCanonicalPath);

        // 1. edit_manifest targeting the locked element succeeds while leaving untouched siblings intact
        StudioWorkspaceOpResult targetEdit = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["selectedBody"] = new JsonObject { ["type"] = "Text", ["text"] = "Refined Target" }
            }));
        Assert.True(targetEdit.Succeeded, targetEdit.ErrorMessage);

        JsonNode afterEdit = JsonNode.Parse(engine.GetManifestJson(indented: false)!)!;
        Assert.Equal("Untouched Header", afterEdit["elements"]!["siblingHeader"]!["text"]!.GetValue<string>());
        Assert.Equal("Refined Target", afterEdit["elements"]!["selectedBody"]!["text"]!.GetValue<string>());

        // 2. Full-bundle replacement that overwrites sibling while preserving target element grafts sibling back
        const string unauthorizedSiblingOverwriteBundle = """
        {
          "schemaVersion": 1,
          "id": "test.subtree.guard",
          "name": "Subtree Guard Test",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["siblingHeader", "selectedBody"] },
            "siblingHeader": { "type": "Text", "text": "Overwritten By Full Bundle" },
            "selectedBody": { "type": "Text", "text": "Target v2" }
          }
        }
        """;

        StudioWorkspaceOpResult graftedReplace = engine.ReplaceManifestJson(unauthorizedSiblingOverwriteBundle);
        Assert.True(graftedReplace.Succeeded, graftedReplace.ErrorMessage);
        Assert.Contains("Subtree Guard preserved", graftedReplace.Summary, StringComparison.Ordinal);

        JsonNode afterGraft = JsonNode.Parse(engine.GetManifestJson(indented: false)!)!;
        Assert.Equal("Untouched Header", afterGraft["elements"]!["siblingHeader"]!["text"]!.GetValue<string>());
        Assert.Equal("Target v2", afterGraft["elements"]!["selectedBody"]!["text"]!.GetValue<string>());

        // 3. Full-bundle replacement that removes the locked element from elements is rejected
        const string brokenSpineBundle = """
        {
          "schemaVersion": 1,
          "id": "test.subtree.guard",
          "name": "Subtree Guard Test",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["siblingHeader"] },
            "siblingHeader": { "type": "Text", "text": "Only one child remains" }
          }
        }
        """;

        StudioWorkspaceOpResult rejectedReplace = engine.ReplaceManifestJson(brokenSpineBundle);
        Assert.False(rejectedReplace.Succeeded);
        Assert.Contains("Subtree Guard", rejectedReplace.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateWorkspaceBundle_DualPassDryRun_CatchesPassAColdCrash_AndPassBStringCoercion()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.dualpass.validator",
          "name": "Dual Pass Validator",
          "collections": {
            "items": {
              "fields": ["title", "amount"]
            }
          },
          "script": {
            "entry": "logic.js",
            "computed": ["summary"]
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_summary"] },
            "txt_summary": { "type": "Text", "text": "${$computed.summary}" }
          }
        }
        """;

        // Pass A failure: cold empty collection dereferences items[0]
        const string passACrashJs = """
        export function onInit(ctx) {
          const items = ctx.collections.get("items") || [];
          const title = items[0].title;
          ctx.computed.summary = title.toUpperCase();
        }
        """;

        CustomWidgetWorkspaceValidationResult passAResult = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            passACrashJs,
            locales: null,
            runOnInitDryRun: true,
            sampleDataJson: null);

        Assert.False(passAResult.IsValid);
        Assert.Contains(
            passAResult.Diagnostics,
            d => d.IsError && string.Equals(d.Code, "ScriptOnInitRuntimeError", StringComparison.Ordinal));

        // Native JSON substrate preserves numeric sampleData ("amount": 18 stays JS number)
        const string passBNumberJs = """
        export function onInit(ctx) {
          const items = ctx.collections.get("items") || [];
          if (items.length === 0) {
            ctx.computed.summary = "0.00";
            return;
          }
          const formatted = items[0].amount.toFixed(2);
          ctx.computed.summary = formatted;
        }
        """;

        const string typedSampleDataJson = """
        {
          "collections": {
            "items": [
              { "title": "Coffee", "amount": 18 }
            ]
          }
        }
        """;

        CustomWidgetWorkspaceValidationResult typedPassBResult = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            passBNumberJs,
            locales: null,
            runOnInitDryRun: true,
            sampleDataJson: typedSampleDataJson);

        Assert.True(typedPassBResult.IsValid);
        Assert.DoesNotContain(
            typedPassBResult.Diagnostics,
            d => string.Equals(d.Code, "ScriptSampleDataRuntimeError", StringComparison.Ordinal));

        // Pass B failure: Pass A guards length > 0, while Pass B feeds populated item with string/mismatched field -> .toFixed throws
        const string stringSampleDataJson = """
        {
          "collections": {
            "items": [
              { "title": "Coffee", "amount": "18" }
            ]
          }
        }
        """;

        CustomWidgetWorkspaceValidationResult passBResult = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            manifestJson,
            passBNumberJs,
            locales: null,
            runOnInitDryRun: true,
            sampleDataJson: stringSampleDataJson);

        Assert.Contains(
            passBResult.Diagnostics,
            d => string.Equals(d.Code, "ScriptSampleDataRuntimeError", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionStore_PersistsCoalesceGroupKeyAndActionChips_AcrossCommitAndDraftRoundTrips()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-session-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CustomWidgetStudioSessionStore(tempRoot);
            const string widgetId = "custom.roundtrip.inspector";
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.roundtrip.inspector",
              "name": "Roundtrip Inspector",
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": ["hello_txt"] },
                "hello_txt": { "type": "Text", "text": "Hello" }
              }
            }
            """;

            var revision = new StudioPersistedRevision
            {
                RevisionId = "rev-inspector-1",
                Source = "inspector",
                ChangeSummary = "Edited text via inspector",
                ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(manifestJson),
                IsCommitted = true,
                ManifestJson = manifestJson,
                CoalesceGroupKey = "inspector:#hello_txt.text"
            };

            var chatMessage = new StudioChatMessage
            {
                Id = "msg-reset-confirm",
                Role = "assistant",
                Kind = "status",
                Text = "Confirm brief reset or apply inspector tweak",
                ActionChips =
                [
                    new StudioActionChip(
                        Label: "Confirm Brief Reset",
                        Prompt: "reset brief",
                        Kind: "confirm_brief_reset"),
                    new StudioActionChip(
                        Label: "Set Accent Tone",
                        Prompt: "use accent tone",
                        Kind: "apply_patch",
                        TargetProperty: "tone",
                        TargetValue: "accent")
                ]
            };

            store.SaveCommittedWidgetSession(
                widgetId,
                briefMarkdown: "## Purpose\nRoundtrip test",
                revisions: [revision],
                chatMessages: [chatMessage]);

            StudioLoadedSession loaded = store.LoadOrReconcileWidgetSession(widgetId, manifestJson);
            StudioPersistedRevision loadedRev = Assert.Single(loaded.Revisions);
            Assert.Equal("inspector:#hello_txt.text", loadedRev.CoalesceGroupKey);
            Assert.Equal("inspector", loadedRev.Source);

            StudioChatMessage loadedMsg = Assert.Single(loaded.ChatMessages);
            Assert.Equal(2, loadedMsg.ActionChips.Count);
            Assert.Equal("confirm_brief_reset", loadedMsg.ActionChips[0].Kind);
            Assert.Equal("Confirm Brief Reset", loadedMsg.ActionChips[0].Label);
            Assert.Equal("apply_patch", loadedMsg.ActionChips[1].Kind);
            Assert.Equal("tone", loadedMsg.ActionChips[1].TargetProperty);
            Assert.Equal("accent", loadedMsg.ActionChips[1].TargetValue);

            // Draft persistence preserves the same fields prior to commit
            store.SaveWidgetDraft(
                widgetId,
                new StudioDraftSnapshot
                {
                    TargetWidgetId = widgetId,
                    SelectedRevisionIndex = 0,
                    Revisions = [revision],
                    ChatMessages = [chatMessage]
                });

            StudioDraftSnapshot? draft = store.TryLoadWidgetDraft(widgetId);
            Assert.NotNull(draft);
            Assert.Equal("inspector:#hello_txt.text", draft!.Revisions[0].CoalesceGroupKey);
            Assert.Equal("confirm_brief_reset", draft.ChatMessages[0].ActionChips[0].Kind);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EvaluateStoreDataSafety_FlagsDroppedUserCollectionsAndState_IgnoresHostMirroredUnmodifiedAndPromotedKeys()
    {
        const string prevJson = """
        {
          "schemaVersion": 1,
          "id": "test.data.safety",
          "name": "Safety",
          "sources": [
            { "type": "todo", "targetCollection": "todos" }
          ],
          "collections": {
            "entries": { "fields": ["title"] },
            "todos": { "fields": ["title", "done"] }
          },
          "state": {
            "userNote": "default-note",
            "mode": "compact",
            "goal": "10"
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_note", "txt_goal"] },
            "txt_note": { "type": "Text", "text": "${$state.userNote}" },
            "txt_goal": { "type": "Text", "text": "${$state.goal}" }
          }
        }
        """;

        const string candidateDroppedJson = """
        {
          "schemaVersion": 1,
          "id": "test.data.safety",
          "name": "Safety",
          "settings": [
            { "key": "goal", "type": "number", "label": "Goal", "default": "10" }
          ],
          "collections": {
            "journalEntries": { "fields": ["title"] }
          },
          "state": {
            "mode": "compact"
          },
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["txt_goal"] },
            "txt_goal": { "type": "Text", "text": "${$settings.goal}" }
          }
        }
        """;

        CustomWidgetManifest prev = CustomWidgetManifestParser.Parse(prevJson).Manifest!;
        CustomWidgetManifest candidate = CustomWidgetManifestParser.Parse(candidateDroppedJson).Manifest!;

        var liveStore = new CustomWidgetStorePayload
        {
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["userNote"] = "Personal quarterly reflection", // modified -> MUST flag
                ["mode"] = "compact",                           // unmodified default -> ignored
                ["goal"] = "25",                                // promoted to settings.goal -> PromotedStateKeys
                ["aiStatus"] = "running"                        // ephemeral -> ignored
            },
            Collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
            {
                ["entries"] =
                [
                    new CustomWidgetRecord
                    {
                        Id = "e-1",
                        Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Private log" }
                    }
                ],
                ["todos"] =
                [
                    new CustomWidgetRecord
                    {
                        Id = "t-1",
                        Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Host mirrored" }
                    }
                ]
            }
        };

        CustomWidgetDataSafetyReport report = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
            prev,
            liveStore,
            candidate);

        Assert.True(report.HasDataLossRisk);
        CustomWidgetDroppedCollectionRisk droppedCol = Assert.Single(report.DroppedCollections);
        Assert.Equal("entries", droppedCol.OldCollectionKey);
        Assert.Equal("journalEntries", droppedCol.SuggestedNewCollectionKey);

        Assert.Equal(["userNote"], report.DroppedNonEmptyStateKeys);
        Assert.NotNull(report.PromotedStateKeys);
        Assert.Equal(["goal"], report.PromotedStateKeys!);
    }
}
