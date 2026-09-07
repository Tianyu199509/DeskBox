using System.Text.Json;

namespace DeskBox.Tests;

/// <summary>
/// Light pin for the plugin manifest schema v0.1 draft (roadmap stage 2.5).
/// Deliberately pins existence and vocabulary only - the draft will iterate
/// with the runtime spike, so field-by-field freezing is explicitly avoided
/// (see plugin-schema-v0-notes.md).
/// </summary>
public sealed class PluginSchemaContractTests
{
    [Fact]
    public void SchemaV01_ExistsAndParses()
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
    public void SchemaV01_RuntimeVocabulary_MatchesThreeRuntimes()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement runtime = schema.RootElement.GetProperty("properties")
            .GetProperty("runtime").GetProperty("enum");

        string[] values = runtime.EnumerateArray()
            .Select(value => value.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(["none", "process", "wasm"], values);
    }

    [Fact]
    public void SchemaV01_ContributionsReplaceWidgets()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement properties = schema.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("contributions", out _));
        Assert.False(properties.TryGetProperty("widgets", out _));
        Assert.False(properties.TryGetProperty("category", out _));

        // The contribution type discriminator must include "widget".
        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        Assert.Contains("\"widget\"", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV01_TemplateVocabulary_MatchesSixTemplateDecision()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));

        foreach (string template in new[]
                 {
                     "metric", "list", "status", "gallery", "action-list", "simple-form"
                 })
        {
            Assert.Contains($"\"{template}\"", schemaText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SchemaV01_SignatureIsOptionalAndCorrectlyNamed()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement required = schema.RootElement.GetProperty("required");

        // Signature must NOT be required (dev mode); dev packages ship without it.
        Assert.False(required.EnumerateArray().Any(v => v.GetString() == "signature"));

        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        Assert.Contains("publisherSignature", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("publisherKey", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultSet", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV01_Notes_ExistAndCoverV01Changes()
    {
        string notes = File.ReadAllText(TestPaths.FromRepository(
            "docs/architecture/plugin-schema-v0-notes.md"));
        Assert.Contains("Capability Call", notes, StringComparison.Ordinal);
        Assert.Contains("Lifecycle/Event", notes, StringComparison.Ordinal);
        Assert.Contains("任意宿主函数 invoke", notes, StringComparison.Ordinal);
        Assert.Contains("contributions", notes, StringComparison.Ordinal);
        Assert.Contains("publisherSignature", notes, StringComparison.Ordinal);
    }
}
