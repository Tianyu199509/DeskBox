using System.Text.Json;

namespace DeskBox.Tests;

/// <summary>
/// Light pin for the plugin manifest schema v0.2 draft (roadmap stage 2.5).
/// Deliberately pins existence, vocabulary and the key validation semantics
/// tightened in v0.2 - the draft will still iterate with the runtime spike,
/// so field-by-field freezing is explicitly avoided (see
/// plugin-schema-v0-notes.md).
/// </summary>
public sealed class PluginSchemaContractTests
{
    [Fact]
    public void Schema_ExistsAndParses()
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
    public void Schema_RuntimeVocabulary_MatchesThreeRuntimes()
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
    public void Schema_ContributionsReplaceWidgets()
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
    public void Schema_TemplateVocabulary_MatchesSixTemplateDecision()
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
    public void Schema_SignatureIsOptionalButCompleteWhenPresent()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement required = schema.RootElement.GetProperty("required");

        // Signature must NOT be required (dev mode); dev packages ship without it.
        Assert.False(required.EnumerateArray().Any(v => v.GetString() == "signature"));

        // v0.2: when present it must be complete - an empty or half-filled
        // signature block used to validate.
        JsonElement signature = schema.RootElement.GetProperty("properties")
            .GetProperty("signature");
        string[] signatureRequired = signature.GetProperty("required")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(["contentHash", "publisherSignature"], signatureRequired);

        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        Assert.Contains("publisherSignature", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"publisherKey\"", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("defaultSet", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_WidgetContribution_MustBeCompleteAndClosed()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement widget = schema.RootElement.GetProperty("$defs")
            .GetProperty("widgetContribution");

        // v0.2: displayName/template are required per contribution (the
        // shared [type, id] required let stub widgets through) and unknown
        // properties are rejected instead of ignored.
        string[] required = widget.GetProperty("required")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(["displayName", "id", "template", "type"], required);
        Assert.False(widget.GetProperty("additionalProperties").GetBoolean());

        // contributions items dispatch through the $defs entry so later
        // contribution types (command/ai-tool/settings) extend the oneOf.
        JsonElement items = schema.RootElement.GetProperty("properties")
            .GetProperty("contributions").GetProperty("items");
        Assert.Equal(
            "#/$defs/widgetContribution",
            items.GetProperty("oneOf")[0].GetProperty("$ref").GetString());
    }

    [Fact]
    public void Schema_PublisherPublicKey_IsRequiredForVerification()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement required = schema.RootElement.GetProperty("required");

        // v0.2: a fingerprint alone cannot verify an Ed25519 signature; the
        // pre-account package carries the public key itself and the
        // publisher field must equal sha256(publisherPublicKey).
        Assert.Contains(
            "publisherPublicKey",
            required.EnumerateArray().Select(v => v.GetString()));

        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        Assert.Contains("sha256(publisherPublicKey)", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaAndNotes_PinJcsCanonicalizationAndIntegrityManifest()
    {
        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        string notes = File.ReadAllText(TestPaths.FromRepository(
            "docs/architecture/plugin-schema-v0-notes.md"));

        // v0.2: canonicalization is pinned to the formal standard (not an
        // ad-hoc sorted-keys rule) and package contents hash through a
        // deterministic package.integrity manifest.
        Assert.Contains("RFC 8785", schemaText, StringComparison.Ordinal);
        Assert.Contains("package.integrity", schemaText, StringComparison.Ordinal);
        Assert.Contains("RFC 8785", notes, StringComparison.Ordinal);
        Assert.Contains("package.integrity", notes, StringComparison.Ordinal);
        Assert.Contains("publisherPublicKey", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_Notes_ExistAndCoverVocabulary()
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
