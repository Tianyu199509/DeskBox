using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Verifies a plugin package directory against the manifest schema v0.3
/// semantics: structural rules, the package path grammar, the
/// package.integrity chain, and the Ed25519 publisher signature. This is
/// the C# port of the Node spike validator (scripts/spike/validate-lib.mjs)
/// and the install-time gate for the product plugin pipeline (roadmap
/// 16.10); cross-implementation parity is enforced by tests that run this
/// verifier over packages built and signed by the Node tooling.
/// Uses JsonDocument (arbitrary plugin data, no reflection) so the frozen
/// JsonSerializer baseline is untouched.
/// </summary>
public static partial class PluginPackageVerifier
{
    public sealed record VerificationResult(
        bool IsValid,
        IReadOnlyList<string> Failures,
        bool UnsignedPackage,
        string? ManifestJson);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex LocalIdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+$")]
    private static partial Regex PermissionIdPattern();

    [GeneratedRegex(@"^\$\.[A-Za-z0-9_\[\].]*$")]
    private static partial Regex JsonPathPattern();

    [GeneratedRegex(@"^([0-9a-f]{64})  (.+)$")]
    private static partial Regex IntegrityLinePattern();

    private static readonly string[] RootRequired =
        ["schemaVersion", "id", "version", "publisher", "publisherPublicKey", "runtime", "hostApi", "contributions"];

    private static readonly string[] RootClosed =
        [.. RootRequired, "permissions", "data", "signature", "fallback", "dataSources", "actions", "entry"];

    private static readonly string[] WidgetClosed =
        ["type", "id", "displayName", "template", "payload", "bindings", "defaultSize", "activationEvents"];

    private static readonly string[] Templates =
        ["metric", "list", "status", "gallery", "action-list", "simple-form"];

    public static VerificationResult Verify(string packageDirectory)
    {
        var failures = new List<string>();
        string manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new VerificationResult(false, ["manifest.json missing"], false, null);
        }

        string manifestJson = File.ReadAllText(manifestPath);
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException error)
        {
            return new VerificationResult(false, [$"manifest.json is not valid JSON: {error.Message}"], false, null);
        }
        using (parsed)
        {
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new VerificationResult(false, ["manifest.json root must be an object"], false, null);
            }

            bool unsigned = !root.TryGetProperty("signature", out JsonElement signature) ||
                            signature.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

            ValidateStructure(root, failures);
            VerifyIntegrityChain(packageDirectory, root, manifestJson, failures);

            if (!unsigned)
            {
                VerifySignature(packageDirectory, signature, failures);
            }

            return new VerificationResult(failures.Count == 0, failures, unsigned, manifestJson);
        }
    }

    // ---------- structural rules (schema v0.3, mirrors validate-lib.mjs) ----------
    private static void ValidateStructure(JsonElement root, List<string> failures)
    {
        void Fail(string message) => failures.Add(message);
        foreach (string key in RootRequired)
        {
            if (!root.TryGetProperty(key, out _))
            {
                Fail($"root: missing required '{key}'");
            }
        }
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!RootClosed.Contains(property.Name))
            {
                Fail($"root: unknown property '{property.Name}'");
            }
        }

        if (StringEquals(root, "schemaVersion", out string? schemaVersion) && schemaVersion != "0")
        {
            Fail("schemaVersion must be 0");
        }
        if (StringEquals(root, "id", out string? id) && !PackageIdPattern().IsMatch(id))
        {
            Fail("id pattern");
        }
        if (StringEquals(root, "version", out string? version) && !VersionPattern().IsMatch(version))
        {
            Fail("version pattern");
        }
        if (StringEquals(root, "runtime", out string? runtime) && runtime is not ("none" or "wasm" or "process"))
        {
            Fail("runtime enum");
        }

        bool hasEntry = root.TryGetProperty("entry", out JsonElement entry) && entry.ValueKind == JsonValueKind.Object;
        if (runtime == "none" && hasEntry)
        {
            Fail("runtime:none packages must not declare an entry point");
        }
        if (runtime != "none" && !hasEntry)
        {
            Fail($"runtime '{runtime}' requires an entry point");
        }
        if (hasEntry)
        {
            string? entryMain = StringEquals(entry, "main", out string? main) ? main : null;
            if (entryMain is null || entryMain.Length == 0)
            {
                Fail("entry.main must be a non-empty string");
            }
            foreach (JsonProperty property in entry.EnumerateObject())
            {
                if (property.Name != "main")
                {
                    Fail($"entry: unknown property '{property.Name}'");
                }
            }
            // The manifest path is verified against the package later; the
            // grammar itself is directory-independent so it runs here.
            if (entryMain is not null && PackagePathViolation(entryMain) is { } entryViolation)
            {
                Fail($"entry.main violates the package path grammar ({entryViolation})");
            }
        }

        if (!root.TryGetProperty("contributions", out JsonElement contributions) ||
            contributions.ValueKind != JsonValueKind.Array ||
            contributions.GetArrayLength() == 0)
        {
            Fail("contributions: minItems 1");
            return;
        }

        Dictionary<string, JsonElement> dataSources = MapOf(root, "dataSources");
        Dictionary<string, JsonElement> actions = MapOf(root, "actions");
        foreach (string key in dataSources.Keys.Concat(actions.Keys))
        {
            if (!LocalIdPattern().IsMatch(key))
            {
                Fail($"map key '{key}' must use the local id pattern (^[a-z0-9][a-z0-9-]*$)");
            }
        }

        var contributionIds = new HashSet<string>();
        var referencedActionIds = new HashSet<string>();
        int index = 0;
        foreach (JsonElement contribution in contributions.EnumerateArray())
        {
            string where = $"contributions[{index++}]";
            if (contribution.ValueKind != JsonValueKind.Object)
            {
                Fail($"{where}: must be an object");
                continue;
            }
            foreach (string key in new[] { "type", "id", "displayName", "template" })
            {
                if (!contribution.TryGetProperty(key, out _))
                {
                    Fail($"{where}: missing required '{key}'");
                }
            }
            foreach (JsonProperty property in contribution.EnumerateObject())
            {
                if (!WidgetClosed.Contains(property.Name))
                {
                    Fail($"{where}: unknown property '{property.Name}'");
                }
            }
            if (StringEquals(contribution, "type", out string? type) && type != "widget")
            {
                Fail($"{where}: unknown contribution type");
            }
            if (StringEquals(contribution, "id", out string? contributionId))
            {
                if (!LocalIdPattern().IsMatch(contributionId))
                {
                    Fail($"{where}: id pattern");
                }
                if (!contributionIds.Add(contributionId))
                {
                    Fail("contribution ids must be unique within the package");
                }
            }
            if (StringEquals(contribution, "displayName", out string? displayName) && displayName.Length == 0)
            {
                Fail($"{where}: displayName minLength 1");
            }
            if (StringEquals(contribution, "template", out string? template) && !Templates.Contains(template))
            {
                Fail($"{where}: template enum");
            }

            if (contribution.TryGetProperty("payload", out JsonElement payload) &&
                payload.ValueKind == JsonValueKind.Object)
            {
                if (!payload.TryGetProperty("version", out JsonElement payloadVersion) ||
                    !payloadVersion.TryGetInt32(out int payloadVersionValue) ||
                    payloadVersionValue < 1)
                {
                    Fail($"{where}.payload.version >= 1 required");
                }
                if (StringEquals(payload, "primaryActionId", out string? primaryActionId))
                {
                    referencedActionIds.Add(primaryActionId);
                }
            }

            if (contribution.TryGetProperty("bindings", out JsonElement bindings) &&
                bindings.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty bindingProperty in bindings.EnumerateObject())
                {
                    string bindingWhere = $"{where}.bindings['{bindingProperty.Name}']";
                    JsonElement binding = bindingProperty.Value;
                    if (binding.ValueKind != JsonValueKind.Object)
                    {
                        Fail($"{bindingWhere}: must be an object");
                        continue;
                    }
                    foreach (JsonProperty property in binding.EnumerateObject())
                    {
                        if (property.Name is not ("source" or "path"))
                        {
                            Fail($"{bindingWhere}: unknown property '{property.Name}'");
                        }
                    }
                    if (!StringEquals(binding, "source", out string? source) || source.Length == 0)
                    {
                        Fail($"{bindingWhere}: 'source' required");
                    }
                    else if (!dataSources.ContainsKey(source))
                    {
                        Fail($"{bindingWhere}: unknown data source '{source}'");
                    }
                    if (!StringEquals(binding, "path", out string? path) || path.Length == 0)
                    {
                        Fail($"{bindingWhere}: 'path' required");
                    }
                    else if (!JsonPathPattern().IsMatch(path))
                    {
                        Fail($"{bindingWhere}: path must be a minimal JSON path like $.a.b[0].c");
                    }
                    if (payload.ValueKind == JsonValueKind.Object && !payload.TryGetProperty(bindingProperty.Name, out _))
                    {
                        Fail($"{bindingWhere}: binds a field the payload does not have");
                    }
                }
            }
        }

        // v0.3 data sources: http-json only, HTTPS only, sane refresh interval.
        foreach (KeyValuePair<string, JsonElement> pair in dataSources)
        {
            string where = $"dataSources['{pair.Key}']";
            JsonElement source = pair.Value;
            foreach (string key in new[] { "type", "url", "refreshSeconds" })
            {
                if (!source.TryGetProperty(key, out _))
                {
                    Fail($"{where}: missing required '{key}'");
                }
            }
            foreach (JsonProperty property in source.EnumerateObject())
            {
                if (property.Name is not ("type" or "url" or "refreshSeconds"))
                {
                    Fail($"{where}: unknown property '{property.Name}'");
                }
            }
            if (StringEquals(source, "type", out string? sourceType) && sourceType != "http-json")
            {
                Fail($"{where}: v0.3 supports type http-json only");
            }
            if (StringEquals(source, "url", out string? url) && !url.StartsWith("https://", StringComparison.Ordinal))
            {
                Fail($"{where}: url must be HTTPS");
            }
            if (source.TryGetProperty("refreshSeconds", out JsonElement refresh) &&
                (!refresh.TryGetInt32(out int refreshValue) || refreshValue < 10))
            {
                Fail($"{where}: refreshSeconds must be an integer >= 10");
            }
        }

        // v0.3 actions: open-url only, HTTPS only; referenced ids resolve.
        foreach (KeyValuePair<string, JsonElement> pair in actions)
        {
            string where = $"actions['{pair.Key}']";
            JsonElement action = pair.Value;
            foreach (string key in new[] { "type", "url" })
            {
                if (!action.TryGetProperty(key, out _))
                {
                    Fail($"{where}: missing required '{key}'");
                }
            }
            foreach (JsonProperty property in action.EnumerateObject())
            {
                if (property.Name is not ("type" or "url"))
                {
                    Fail($"{where}: unknown property '{property.Name}'");
                }
            }
            if (StringEquals(action, "type", out string? actionType) && actionType != "open-url")
            {
                Fail($"{where}: v0.3 supports type open-url only");
            }
            if (StringEquals(action, "url", out string? actionUrl) && !actionUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                Fail($"{where}: url must be HTTPS");
            }
        }
        if (actions.Count > 0)
        {
            // action-list/simple-form entries reference actions by id; the
            // primaryActionId references were collected above.
            foreach (JsonElement contribution in contributions.EnumerateArray())
            {
                if (!contribution.TryGetProperty("payload", out JsonElement payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (JsonProperty property in payload.EnumerateObject())
                {
                    CollectActionId(property.Value, referencedActionIds);
                }
            }
            foreach (string actionId in referencedActionIds)
            {
                if (!actions.ContainsKey(actionId))
                {
                    Fail($"payload actionId '{actionId}' does not resolve to an actions entry");
                }
            }
        }

        // Permissions: shape, unique ids, then consumption rules.
        List<JsonElement> permissions = root.TryGetProperty("permissions", out JsonElement permissionsElement) &&
                                        permissionsElement.ValueKind == JsonValueKind.Array
            ? permissionsElement.EnumerateArray().ToList()
            : [];
        var permissionIds = new HashSet<string>();
        for (int i = 0; i < permissions.Count; i++)
        {
            JsonElement permission = permissions[i];
            if (!StringEquals(permission, "id", out string? permissionId) || !PermissionIdPattern().IsMatch(permissionId))
            {
                Fail($"permissions[{i}]: id pattern");
            }
            else if (!permissionIds.Add(permissionId))
            {
                // Duplicate ids with different scopes would make grant/scope
                // lookup implementation-defined across runtimes.
                Fail($"permissions: duplicate id '{permissionId}' (use one entry with multiple scope.allow hosts)");
            }
            foreach (JsonProperty property in permission.EnumerateObject())
            {
                if (property.Name is not ("id" or "required" or "scope"))
                {
                    Fail($"permissions[{i}]: unknown property '{property.Name}'");
                }
            }
        }

        foreach (KeyValuePair<string, JsonElement> pair in dataSources)
        {
            if (StringEquals(pair.Value, "type", out string? type) && type == "http-json")
            {
                if (!permissionIds.Contains("network.fetch"))
                {
                    Fail($"dataSources['{pair.Key}']: http-json requires the network.fetch permission");
                }
                else if (StringEquals(pair.Value, "url", out string? url) &&
                         !HostInScope(url, permissions, "network.fetch"))
                {
                    Fail($"dataSources['{pair.Key}']: url host is outside the declared network.fetch scope");
                }
            }
        }
        foreach (KeyValuePair<string, JsonElement> pair in actions)
        {
            if (StringEquals(pair.Value, "type", out string? type) && type == "open-url")
            {
                if (!permissionIds.Contains("shell.open"))
                {
                    Fail($"actions['{pair.Key}']: open-url requires the shell.open permission");
                }
                else if (StringEquals(pair.Value, "url", out string? url) &&
                         !HostInScope(url, permissions, "shell.open"))
                {
                    Fail($"actions['{pair.Key}']: url host is outside the declared shell.open scope");
                }
            }
        }
    }

    private static void CollectActionId(JsonElement value, HashSet<string> into)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in value.EnumerateArray())
            {
                CollectActionId(entry, into);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object &&
                 value.TryGetProperty("actionId", out JsonElement actionId) &&
                 actionId.ValueKind == JsonValueKind.String)
        {
            into.Add(actionId.GetString()!);
        }
    }

    private static bool HostInScope(string url, List<JsonElement> permissions, string permissionId)
    {
        try
        {
            string host = new Uri(url).Host.ToLowerInvariant();
            return permissions
                .Where(p => StringEquals(p, "id", out string? id) && id == permissionId)
                .SelectMany(p => p.TryGetProperty("scope", out JsonElement scope) &&
                                 scope.TryGetProperty("allow", out JsonElement allow) &&
                                 allow.ValueKind == JsonValueKind.Array
                    ? allow.EnumerateArray()
                    : Enumerable.Empty<JsonElement>())
                .Any(entry => entry.ValueKind == JsonValueKind.String &&
                              string.Equals(entry.GetString(), host, StringComparison.Ordinal));
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static Dictionary<string, JsonElement> MapOf(JsonElement root, string property)
    {
        var result = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty(property, out JsonElement map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty pair in map.EnumerateObject())
            {
                result[pair.Name] = pair.Value;
            }
        }
        return result;
    }

    private static bool StringEquals(JsonElement element, string property, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out JsonElement node) &&
            node.ValueKind == JsonValueKind.String)
        {
            value = node.GetString();
            return true;
        }
        return false;
    }

    // ---------- package path grammar (zip-slip defense, shared with the Node tooling) ----------
    internal static string? PackagePathViolation(string relativePath)
    {
        if (relativePath.StartsWith('/') || relativePath.Contains('\\') || relativePath.Contains(':'))
        {
            return "rooted path, backslash, or colon";
        }
        foreach (string segment in relativePath.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return "empty, '.', or '..' segment";
            }
        }
        return null;
    }

    // ---------- integrity + signature chain (notes walkthrough steps 1-5) ----------
    private static void VerifyIntegrityChain(string packageDirectory, JsonElement root, string manifestJson, List<string> failures)
    {
        void Fail(string message) => failures.Add(message);
        string integrityPath = Path.Combine(packageDirectory, "package.integrity");
        if (!File.Exists(integrityPath))
        {
            Fail("package.integrity missing");
            return;
        }

        var listed = new Dictionary<string, string>();
        var seenNormalized = new Dictionary<string, string>();
        foreach (string rawLine in File.ReadAllText(integrityPath).Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            Match match = IntegrityLinePattern().Match(line);
            if (!match.Success)
            {
                Fail($"package.integrity: malformed line '{Truncate(line, 40)}'");
                continue;
            }
            string digest = match.Groups[1].Value;
            string relativePath = match.Groups[2].Value;
            if (PackagePathViolation(relativePath) is { } violation)
            {
                Fail($"package.integrity: path violates the package path grammar ({violation}): {relativePath}");
                continue;
            }
            string normalized = relativePath.ToLowerInvariant();
            if (seenNormalized.TryGetValue(normalized, out string? previous))
            {
                Fail($"package.integrity: case-insensitive path collision: {relativePath} vs {previous}");
                continue;
            }
            seenNormalized[normalized] = relativePath;
            listed[relativePath] = digest;
        }

        // Step 1: the manifest's canonical form (signature = null) must match
        // its integrity line - catches a manifest edited after the build.
        string canonicalManifestHash = Sha256Hex(CanonicalizeManifest(root));
        if (!listed.TryGetValue("manifest.json", out string? manifestLine) ||
            !string.Equals(manifestLine, canonicalManifestHash, StringComparison.Ordinal))
        {
            Fail("manifest.json integrity line does not match its canonicalization (signature=null)");
        }

        // Step 2: every payload file hashes to its line and is listed.
        foreach (string file in EnumeratePayloadFiles(packageDirectory))
        {
            string relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            if (PackagePathViolation(relative) is { } violation)
            {
                Fail($"payload file violates the package path grammar ({violation}): {relative}");
                continue;
            }
            string digest = relative == "manifest.json"
                ? canonicalManifestHash
                : Sha256Hex(File.ReadAllBytes(file));
            if (!listed.TryGetValue(relative, out string? line))
            {
                Fail($"payload file not listed in package.integrity: {relative}");
            }
            else if (!string.Equals(line, digest, StringComparison.Ordinal))
            {
                Fail($"integrity mismatch for {relative}");
            }
        }
        foreach (string relative in listed.Keys)
        {
            if (relative != "manifest.json" && !File.Exists(Path.Combine(packageDirectory, relative)))
            {
                Fail($"package.integrity lists a missing file: {relative}");
            }
        }
    }

    private static void VerifySignature(string packageDirectory, JsonElement signature, List<string> failures)
    {
        void Fail(string message) => failures.Add(message);
        foreach (string key in new[] { "contentHash", "publisherSignature" })
        {
            if (signature.ValueKind != JsonValueKind.Object ||
                !signature.TryGetProperty(key, out JsonElement node) ||
                node.ValueKind != JsonValueKind.String ||
                node.GetString()!.Length == 0)
            {
                Fail($"signature: '{key}' required when the block is present");
                return;
            }
        }
        foreach (JsonProperty property in signature.EnumerateObject())
        {
            if (property.Name is not ("contentHash" or "publisherSignature"))
            {
                Fail($"signature: unknown property '{property.Name}'");
            }
        }

        string manifestJson = File.ReadAllText(Path.Combine(packageDirectory, "manifest.json"));
        using JsonDocument document = JsonDocument.Parse(manifestJson);
        JsonElement root = document.RootElement;

        string integrityPath = Path.Combine(packageDirectory, "package.integrity");
        if (!File.Exists(integrityPath))
        {
            return; // already reported by the integrity chain
        }

        // Step 3: contentHash over the package.integrity bytes.
        string contentHash = Sha256Hex(File.ReadAllBytes(integrityPath));
        if (!string.Equals(signature.GetProperty("contentHash").GetString(), contentHash, StringComparison.Ordinal))
        {
            Fail($"signature.contentHash mismatch (expected {contentHash})");
            return;
        }

        // Step 5 first (cheap): publisher == sha256(raw key bytes).
        byte[] publicKey = Convert.FromBase64String(root.GetProperty("publisherPublicKey").GetString()!);
        string fingerprint = Sha256Hex(publicKey);
        if (!string.Equals(root.GetProperty("publisher").GetString(), fingerprint, StringComparison.Ordinal))
        {
            Fail("publisher does not equal sha256(raw publisherPublicKey bytes)");
        }

        // Step 4: Ed25519 over the RAW 32-byte digest.
        byte[] signatureBytes = Convert.FromBase64String(signature.GetProperty("publisherSignature").GetString()!);
        byte[] digest = Convert.FromHexString(contentHash);
        if (!PluginEd25519.Verify(signatureBytes, digest, publicKey))
        {
            Fail("publisherSignature verification failed");
        }
    }

    internal static IEnumerable<string> EnumeratePayloadFiles(string packageDirectory)
    {
        return Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(packageDirectory, path).Replace('\\', '/');
                return relative != "package.integrity";
            });
    }

    /// <summary>
    /// JCS-subset canonicalization (parity with the Node tooling): object
    /// keys ordinal-sorted (UTF-16 code units, same as JS default sort), no
    /// whitespace, minimal string escaping, number tokens preserved verbatim
    /// (spike manifests are integer-only). The signature property is forced
    /// to null (written as an explicit null at its sorted position, exactly
    /// like the Node tooling's {...manifest, signature: null}) so the hash
    /// domain is self-reference free and byte-identical across platforms.
    /// </summary>
    internal static byte[] CanonicalizeManifest(JsonElement root)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            WriteCanonical(writer, root, forceSignatureNull: true);
        }
        return output.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool forceSignatureNull)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject()
                    .Where(p => !forceSignatureNull || p.Name != "signature")
                    .Select(p => (p.Name, Value: p.Value, IsNull: false))
                    .ToList();
                if (forceSignatureNull)
                {
                    properties.Add(("signature", Value: default, IsNull: true));
                }
                foreach ((string name, JsonElement value, bool isNull) in properties
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    if (isNull)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        WriteCanonical(writer, value, forceSignatureNull: false);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, forceSignatureNull: false);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                // RawValue preserves the original token text (no float
                // reformatting; the manifests are integer-only).
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON kind: {element.ValueKind}");
        }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
