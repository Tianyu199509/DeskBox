using System.Text.Json;

namespace DeskBox.Tests;

/// <summary>
/// Light pin for the plugin manifest schema v0 draft (roadmap stage 2.5).
/// Deliberately pins existence and vocabulary only - the draft will iterate
/// with the runtime spike, so field-by-field freezing is explicitly avoided
/// (see plugin-schema-v0-notes.md).
/// </summary>
public sealed class PluginSchemaContractTests
{
    [Fact]
    public void SchemaV0_ExistsAndParses()
    {
        string schemaPath = TestPaths.FromRepository(
            "docs/architecture/plugin-schema-v0.json");
        Assert.True(File.Exists(schemaPath), "plugin-schema-v0.json is missing.");

        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        JsonElement root = schema.RootElement;

        Assert.Equal(
            0,
            root.GetProperty("properties").GetProperty("schemaVersion")
                .GetProperty("const").GetInt32());
    }

    [Fact]
    public void SchemaV0_CategoryVocabulary_MatchesRuntimeMatrix()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement category = schema.RootElement.GetProperty("properties")
            .GetProperty("category").GetProperty("enum");

        string[] categories = category.EnumerateArray()
            .Select(value => value.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(["out-of-proc", "resource-pack", "wasm"], categories);
    }

    [Fact]
    public void SchemaV0_TemplateVocabulary_MatchesSixTemplateDecision()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        // Walk to widgets[].items.properties.template.enum
        JsonElement template = schema.RootElement.GetProperty("properties")
            .GetProperty("widgets").GetProperty("items")
            .GetProperty("properties").GetProperty("template")
            .GetProperty("enum");

        string[] templates = template.EnumerateArray()
            .Select(value => value.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(
            ["action-list", "gallery", "list", "metric", "simple-form", "status"],
            templates);
    }

    [Fact]
    public void SchemaV0_Notes_ExistAndReferenceChannelTrichotomy()
    {
        string notes = File.ReadAllText(TestPaths.FromRepository(
            "docs/architecture/plugin-schema-v0-notes.md"));
        Assert.Contains("Capability Call", notes, StringComparison.Ordinal);
        Assert.Contains("Lifecycle/Event", notes, StringComparison.Ordinal);
        Assert.Contains("任意宿主函数 invoke", notes, StringComparison.Ordinal);
    }
}
