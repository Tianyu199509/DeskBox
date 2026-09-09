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
    public void Schema_RuntimeVocabulary_MatchesFourRuntimes()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement runtime = schema.RootElement.GetProperty("properties")
            .GetProperty("runtime").GetProperty("enum");

        string[] values = runtime.EnumerateArray()
            .Select(value => value.GetString()!)
            .Order()
            .ToArray();
        Assert.Equal(["native", "none", "process", "wasm"], values);
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
    public void Schema_WidgetContributions_AreConditionallySplitByRuntime()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement defs = schema.RootElement.GetProperty("$defs");

        // Templated (non-native) contributions require template; native
        // contributions do not and instead accept a fallback block.
        JsonElement templated = defs.GetProperty("templatedWidgetContribution");
        string[] templatedRequired = templated.GetProperty("required")
            .EnumerateArray().Select(v => v.GetString()!).Order().ToArray();
        Assert.Equal(["displayName", "id", "template", "type"], templatedRequired);
        Assert.True(templated.GetProperty("additionalProperties").GetBoolean() is false ||
            !templated.TryGetProperty("additionalProperties", out _));

        JsonElement native = defs.GetProperty("nativeWidgetContribution");
        string[] nativeRequired = native.GetProperty("required")
            .EnumerateArray().Select(v => v.GetString()!).Order().ToArray();
        Assert.Equal(["displayName", "id", "type"], nativeRequired);
        Assert.True(native.GetProperty("properties").TryGetProperty("fallback", out _));

        // Root allOf dispatches by runtime value.
        JsonElement allOf = schema.RootElement.GetProperty("allOf");
        Assert.True(allOf.GetArrayLength() >= 2);
    }

    [Fact]
    public void Schema_PublisherPublicKey_IsRequiredForVerification()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement required = schema.RootElement.GetProperty("required");

        // v0.2: a fingerprint alone cannot verify an Ed25519 signature; the
        // pre-account package carries the public key itself and the
        // publisher field must equal the fingerprint of the RAW key bytes.
        Assert.Contains(
            "publisherPublicKey",
            required.EnumerateArray().Select(v => v.GetString()));

        string schemaText = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json"));
        Assert.Contains("sha256(raw key bytes)", schemaText, StringComparison.Ordinal);
        Assert.Contains("raw 32-byte", schemaText, StringComparison.Ordinal);
        Assert.Contains("lowercase hex", schemaText, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_DeclarativeExecutionVocabulary_MatchesV03()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/plugin-schema-v0.json")));
        JsonElement defs = schema.RootElement.GetProperty("$defs");
        JsonElement properties = schema.RootElement.GetProperty("properties");
        string schemaText = schema.RootElement.GetRawText();

        // v0.3 (round 6, spike leg 1B prerequisite): dataSources (http-json
        // fetched by the HOST), bindings (JSON-path payload overrides), and
        // actions (open-url) - runtime:none packages execute no third-party
        // code, but the host executes governed capabilities for them.
        Assert.True(defs.TryGetProperty("dataSource", out _));
        Assert.True(defs.TryGetProperty("fieldBinding", out _));
        Assert.True(defs.TryGetProperty("packageAction", out _));
        Assert.True(properties.TryGetProperty("dataSources", out _));
        Assert.True(properties.TryGetProperty("actions", out _));

        Assert.Contains("http-json", schemaText, StringComparison.Ordinal);
        Assert.Contains("open-url", schemaText, StringComparison.Ordinal);
        Assert.Contains("shell.open", schemaText, StringComparison.Ordinal);
        // HTTPS-only for declarative fetch and open (no plaintext redirects).
        Assert.Contains("^https://", schemaText, StringComparison.Ordinal);

        string notes = File.ReadAllText(TestPaths.FromRepository(
            "docs/architecture/plugin-schema-v0-notes.md"));
        Assert.Contains("声明式≠无害", notes, StringComparison.Ordinal);
        Assert.Contains("per-element fallback", notes, StringComparison.Ordinal);
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

        // Round 5: the integrity manifest never lists itself (self-reference
        // has no fixed point; its integrity is covered transitively), and
        // hash/signature inputs are pinned to raw bytes so three independent
        // implementations cannot each pick a different reading.
        Assert.Contains("NEVER listed", schemaText, StringComparison.Ordinal);
        Assert.Contains("永不列入", notes, StringComparison.Ordinal);
        Assert.Contains("raw 32 字节", notes, StringComparison.Ordinal);
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
