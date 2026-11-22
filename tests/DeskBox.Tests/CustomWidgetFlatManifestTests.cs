using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetFlatManifestTests
{
    [Fact]
    public void FlatManifestV1_ParsesRootElementsPopoversItemTemplateHoverActionsPaneAndSemanticIcons_WithHashNodePaths()
    {
        const string flatV1Json = """
        {
          "schemaVersion": 1,
          "id": "custom.flat-v1-demo",
          "name": "Flat V1 Demo",
          "icon": "📅",
          "state": { "editTitle": "", "paneOpen": true },
          "collections": { "items": { "maxItems": 50 } },
          "root": "shell_split",
          "popovers": {
            "editor": {
              "title": "Edit Item",
              "width": 300,
              "placement": "bottom",
              "root": "pop_body"
            }
          },
          "elements": {
            "shell_split": {
              "type": "SplitView",
              "isOpen": "${state.paneOpen}",
              "paneWidth": 140,
              "pane": "nav_pane",
              "children": ["main"]
            },
            "nav_pane": {
              "type": "Stack",
              "spacing": 6,
              "children": ["btn_gps", "btn_article", "btn_swap", "btn_quick_note", "btn_soundscape"]
            },
            "btn_gps": { "type": "IconButton", "icon": "gps", "onClick": { "type": "state.set", "key": "editTitle", "value": "gps" } },
            "btn_article": { "type": "IconButton", "icon": "article", "onClick": { "type": "state.set", "key": "editTitle", "value": "article" } },
            "btn_swap": { "type": "IconButton", "icon": "swap", "onClick": { "type": "state.set", "key": "editTitle", "value": "swap" } },
            "btn_quick_note": { "type": "IconButton", "icon": "quick-note", "onClick": { "type": "state.set", "key": "editTitle", "value": "note" } },
            "btn_soundscape": { "type": "IconButton", "icon": "soundscape", "onClick": { "type": "state.set", "key": "editTitle", "value": "sound" } },
            "main": { "type": "Grid", "rows": "Auto,*", "children": ["header", "list_items"] },
            "header": { "type": "Stack", "orientation": "horizontal", "children": ["title_txt", "btn_add", "btn_minus"] },
            "title_txt": { "type": "Text", "text": "Tasks", "style": "subtitle" },
            "btn_add": { "type": "Button", "icon": "add", "text": "Add", "onClick": { "type": "popover.open", "popoverId": "editor" } },
            "btn_minus": { "type": "IconButton", "icon": "minus", "onClick": { "type": "state.set", "key": "editTitle", "value": "" } },
            "list_items": { "type": "List", "collection": "items", "itemTemplate": "row_tpl" },
            "row_tpl": { "type": "Card", "children": ["row_txt"], "hoverActions": ["row_del"] },
            "row_txt": { "type": "Text", "text": "${$item.title}" },
            "row_del": { "type": "IconButton", "icon": "delete", "onClick": { "type": "collection.remove", "collection": "items", "targetId": "${$item.id}" } },
            "pop_body": { "type": "Stack", "spacing": 8, "children": ["inp_title"] },
            "inp_title": { "type": "Input", "bind": "state.editTitle" }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(flatV1Json);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code}@{d.Path}: {d.Message}")));
        Assert.Empty(parsed.Diagnostics);

        CustomWidgetManifest m = parsed.Manifest!;
        Assert.Equal(1, m.SchemaVersion);
        Assert.Equal("#shell_split", m.View.NodePath);
        Assert.Equal("shell_split", m.View.Id);
        Assert.NotNull(m.View.Pane);
        Assert.Equal("#nav_pane", m.View.Pane!.NodePath);
        Assert.Equal("\uE81D", m.View.Pane.Children[0].Icon); // gps
        Assert.Equal("\uE8A1", m.View.Pane.Children[1].Icon); // article
        Assert.Equal("\uE8AB", m.View.Pane.Children[2].Icon); // swap
        Assert.Equal("\uE8D1", m.View.Pane.Children[3].Icon); // quick-note
        Assert.Equal("\uE9D9", m.View.Pane.Children[4].Icon); // soundscape

        CustomWidgetNodeSpec mainGrid = Assert.Single(m.View.Children);
        Assert.Equal("#main", mainGrid.NodePath);
        Assert.Equal("main", mainGrid.Id);
        Assert.Equal(2, mainGrid.Children.Count);
        Assert.Equal("#header", mainGrid.Children[0].NodePath);
        Assert.Equal("\uE710", mainGrid.Children[0].Children[1].Icon); // add
        Assert.Equal("\uE738", mainGrid.Children[0].Children[2].Icon); // minus
        Assert.Equal("#row_tpl", mainGrid.Children[1].ItemTemplate?.NodePath);
        Assert.Single(mainGrid.Children[1].ItemTemplate!.HoverActions);
        Assert.Equal("\uE74D", mainGrid.Children[1].ItemTemplate!.HoverActions[0].Icon); // delete

        CustomWidgetNodeSpec editorPop = Assert.Single(m.Popovers);
        Assert.Equal("editor", editorPop.PopoverId);
        Assert.Single(editorPop.Children);
        Assert.Equal("#pop_body", editorPop.Children[0].NodePath);
        Assert.Equal("#inp_title", editorPop.Children[0].Children[0].NodePath);
    }

    [Fact]
    public void FlatManifestV1_DetectsCyclesDanglingRefsMissingRootMissingElementsAndOrphans()
    {
        const string cycleJson = """
        {
          "schemaVersion": 1,
          "id": "custom.cycle",
          "name": "Cycle",
          "root": "a",
          "elements": {
            "a": { "type": "Stack", "children": ["b"] },
            "b": { "type": "Stack", "children": ["a", "missing_node"] },
            "orphan_node": { "type": "Text", "text": "Unused" }
          }
        }
        """;

        CustomWidgetParseResult res = CustomWidgetManifestParser.Parse(cycleJson);
        Assert.False(res.IsValid);
        Assert.Contains(res.Diagnostics, static d => d.IsError && d.Code == "ElementGraphCycle");
        Assert.Contains(res.Diagnostics, static d => d.IsError && d.Code == "ElementGraphDanglingRef");
        Assert.Contains(res.Diagnostics, static d => !d.IsError && d.Code == "ElementGraphOrphanElement");

        const string missingRootJson = """
        {
          "schemaVersion": 1,
          "id": "custom.no-root",
          "name": "No Root",
          "elements": {
            "a": { "type": "Text", "text": "Hi" }
          }
        }
        """;

        CustomWidgetParseResult noRootRes = CustomWidgetManifestParser.Parse(missingRootJson);
        Assert.False(noRootRes.IsValid);
        Assert.Contains(noRootRes.Diagnostics, static d => d.IsError && d.Code == "ElementGraphMissingRoot");

        const string missingElementsJson = """
        {
          "schemaVersion": 1,
          "id": "custom.no-elements",
          "name": "No Elements",
          "root": "main"
        }
        """;

        CustomWidgetParseResult noElementsRes = CustomWidgetManifestParser.Parse(missingElementsJson);
        Assert.False(noElementsRes.IsValid);
        Assert.Contains(noElementsRes.Diagnostics, static d => d.IsError && d.Code == "MissingElementsMap");
    }

    [Fact]
    public void FlatManifestV1_StrictlyRejectsLegacyViewLegacyPopoversAndSchemaVersion2()
    {
        const string legacyViewJson = """
        {
          "schemaVersion": 1,
          "id": "custom.legacy-view",
          "name": "Legacy View",
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "Legacy nested view" }
            ]
          }
        }
        """;

        CustomWidgetParseResult legacyViewRes = CustomWidgetManifestParser.Parse(legacyViewJson);
        Assert.False(legacyViewRes.IsValid);
        Assert.Contains(
            legacyViewRes.Diagnostics,
            static d => d.IsError && d.Path == "$.view" && d.Code == "LegacyViewNotSupported");

        const string legacyPopoverViewJson = """
        {
          "schemaVersion": 1,
          "id": "custom.legacy-pop-view",
          "name": "Legacy Popover View",
          "root": "main",
          "popovers": {
            "editPop": {
              "title": "Edit",
              "width": 280,
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "Old body" }
                ]
              }
            }
          },
          "elements": {
            "main": { "type": "Text", "text": "Host" }
          }
        }
        """;

        CustomWidgetParseResult legacyPopViewRes = CustomWidgetManifestParser.Parse(legacyPopoverViewJson);
        Assert.False(legacyPopViewRes.IsValid);
        Assert.Contains(
            legacyPopViewRes.Diagnostics,
            static d => d.IsError && d.Path == "$.popovers.editPop.view" && d.Code == "LegacyViewNotSupported");

        const string legacyPopoverChildrenJson = """
        {
          "schemaVersion": 1,
          "id": "custom.legacy-pop-children",
          "name": "Legacy Popover Children",
          "root": "main",
          "popovers": {
            "editPop": {
              "title": "Edit",
              "width": 280,
              "children": [
                { "type": "Text", "text": "Old children" }
              ]
            }
          },
          "elements": {
            "main": { "type": "Text", "text": "Host" }
          }
        }
        """;

        CustomWidgetParseResult legacyPopChildrenRes = CustomWidgetManifestParser.Parse(legacyPopoverChildrenJson);
        Assert.False(legacyPopChildrenRes.IsValid);
        Assert.Contains(
            legacyPopChildrenRes.Diagnostics,
            static d => d.IsError && d.Path == "$.popovers.editPop.children" && d.Code == "LegacyViewNotSupported");

        const string schemaV2Json = """
        {
          "schemaVersion": 2,
          "id": "custom.old-v2",
          "name": "Old V2",
          "root": "main",
          "elements": {
            "main": { "type": "Text", "text": "Hello" }
          }
        }
        """;

        CustomWidgetParseResult v2Res = CustomWidgetManifestParser.Parse(schemaV2Json);
        Assert.False(v2Res.IsValid);
        Assert.Contains(
            v2Res.Diagnostics,
            static d => d.IsError && d.Path == "$.schemaVersion" && d.Code == "UnsupportedSchemaVersion");

        const string futureMinSchemaJson = """
        {
          "schemaVersion": 1,
          "minSchemaVersion": 2,
          "id": "custom.future-min-schema",
          "name": "Future Min Schema",
          "root": "main",
          "elements": {
            "main": { "type": "Text", "text": "Hello" }
          }
        }
        """;

        CustomWidgetParseResult futureMinSchemaRes = CustomWidgetManifestParser.Parse(futureMinSchemaJson);
        Assert.False(futureMinSchemaRes.IsValid);
        Assert.Contains(
            futureMinSchemaRes.Diagnostics,
            static d => d.IsError && d.Code == CustomWidgetDiagnosticCodes.IncompatibleSchemaVersion);
    }

    [Fact]
    public void WorkspaceEngine_ApplyFlatManifestEdit_UpsertsRemovesCleansDanglingRefsRejectsLegacyAndRollsBackOnError()
    {
        const string canonicalV1 = """
        {
          "schemaVersion": 1,
          "id": "custom.edit-flat",
          "name": "Edit Flat",
          "state": { "mode": "a" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["lbl", "old_btn"]
            },
            "lbl": { "type": "Text", "text": "Old" },
            "old_btn": {
              "type": "Button",
              "text": "Remove Me",
              "onClick": { "type": "state.set", "key": "mode", "value": "b" }
            }
          }
        }
        """;

        var engine = new StudioWorkspaceEngine(canonicalV1);
        StudioWorkspaceOpResult okRes = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject
            {
                ["lbl"] = new JsonObject { ["type"] = "Text", ["text"] = "Updated" },
                ["new_btn"] = new JsonObject
                {
                    ["type"] = "Button",
                    ["icon"] = "export",
                    ["text"] = "Export",
                    ["onClick"] = new JsonObject { ["type"] = "state.set", ["key"] = "mode", ["value"] = "c" }
                },
                ["main"] = new JsonObject
                {
                    ["type"] = "Stack",
                    ["children"] = new JsonArray("lbl", "old_btn", "new_btn")
                }
            },
            RemoveElements: ["old_btn"],
            PatchRoot: new JsonObject
            {
                ["state"] = new JsonObject { ["mode"] = "c" }
            }));

        Assert.True(okRes.Succeeded, okRes.ErrorMessage);
        string updatedJson = engine.GetManifestJson(indented: true)!;
        using (JsonDocument doc = JsonDocument.Parse(updatedJson))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("view", out _));
            Assert.Equal("main", doc.RootElement.GetProperty("root").GetString());
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("elements").ValueKind);
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(updatedJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code}@{d.Path}: {d.Message}")));
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(1, parsed.Manifest!.SchemaVersion);
        Assert.Equal(2, parsed.Manifest.View.Children.Count);
        Assert.Equal("Updated", parsed.Manifest.View.Children[0].Text);
        Assert.Equal("Export", parsed.Manifest.View.Children[1].Text);
        Assert.Equal("c", parsed.Manifest.State["mode"]);

        // Invalid element value should atomically roll back without corrupting state
        StudioWorkspaceOpResult badRes = engine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject { ["lbl"] = "not-an-object" }));
        Assert.False(badRes.Succeeded);
        Assert.Equal(updatedJson, engine.GetManifestJson(indented: true));

        // Legacy manifest without flat 'elements' map is rejected by ApplyFlatManifestEdit
        const string legacyManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.legacy-edit",
          "name": "Legacy Edit",
          "view": { "type": "Text", "text": "Legacy" }
        }
        """;
        var legacyEngine = new StudioWorkspaceEngine(legacyManifest);
        StudioWorkspaceOpResult legacyRes = legacyEngine.ApplyFlatManifestEdit(new StudioFlatManifestEditRequest(
            UpsertElements: new JsonObject { ["main"] = new JsonObject { ["type"] = "Text", ["text"] = "Hi" } }));
        Assert.False(legacyRes.Succeeded);
    }

    [Fact]
    public void All14BuiltInTemplates_AreSchemaVersion1FlatElementGraphs_WithZeroDiagnostics()
    {
        IReadOnlyList<string> ids = CustomWidgetTemplates.BuiltInTemplateIds;
        Assert.Equal(14, ids.Count);

        foreach (string id in ids)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(id);
            using (JsonDocument doc = JsonDocument.Parse(bundle.ManifestJson))
            {
                JsonElement root = doc.RootElement;
                Assert.True(
                    root.TryGetProperty("schemaVersion", out JsonElement svEl) &&
                    svEl.ValueKind == JsonValueKind.Number &&
                    svEl.GetInt32() == 1,
                    $"Built-in template '{id}' must declare schemaVersion 1.");
                Assert.False(
                    root.TryGetProperty("view", out _),
                    $"Built-in template '{id}' must not contain legacy 'view'.");
                Assert.True(
                    root.TryGetProperty("root", out JsonElement rootEl) &&
                    rootEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(rootEl.GetString()),
                    $"Built-in template '{id}' must declare string 'root'.");
                Assert.True(
                    root.TryGetProperty("elements", out JsonElement elementsEl) &&
                    elementsEl.ValueKind == JsonValueKind.Object,
                    $"Built-in template '{id}' must declare object 'elements'.");
            }

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
            Assert.True(parsed.IsValid, $"Template '{id}' invalid: {string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code}@{d.Path}: {d.Message}"))}");
            Assert.Equal(1, parsed.Manifest!.SchemaVersion);
            Assert.Empty(parsed.Diagnostics);
        }
    }
}
