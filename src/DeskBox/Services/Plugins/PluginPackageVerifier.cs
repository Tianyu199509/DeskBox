using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Verification policy (round 9): the store path requires a publisher
/// signature; unsigned packages verify only under an explicit Development
/// policy so a caller can never "forget" the extra UnsignedPackage check.
/// </summary>
public enum PluginPackageVerificationPolicy
{
    /// <summary>Signature mandatory - the default for product install flows.</summary>
    Store,

    /// <summary>Unsigned packages allowed (dev tooling / local iteration only).</summary>
    Development,
}

/// <summary>
/// Verifies a plugin package directory against the manifest schema v0.3
/// semantics: structural rules, the package path grammar, the
/// package.integrity chain, and the Ed25519 publisher signature. This is
/// the C# port of the Node spike validator (scripts/spike/validate-lib.mjs)
/// and the install-time gate for the product plugin pipeline (roadmap
/// 16.10); cross-implementation parity is enforced by tests that run this
/// verifier over packages built and signed by the Node tooling.
///
/// Security posture (round 9): the input is FULLY UNTRUSTED, so Verify is
/// a fail-closed total function - any bytes yield Valid=false with
/// diagnostics, never an unhandled exception - and phases stop on first
/// failure (structure, then integrity, then signature). Manifest numbers
/// must be integers (no float canonicalization ambiguity across
/// platforms). Uses JsonDocument (arbitrary plugin data, no reflection)
/// so the frozen JsonSerializer baseline is untouched.
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

    public static VerificationResult Verify(
        string packageDirectory,
        PluginPackageVerificationPolicy policy = PluginPackageVerificationPolicy.Store,
        PluginVerificationLimits? limits = null)
    {
        try
        {
            return VerifyCore(packageDirectory, policy, limits ?? PluginVerificationLimits.Default);
        }
        catch (Exception error)
        {
            // Total function: package-controlled input must never surface
            // as an unhandled exception to the host - always a result.
            return new VerificationResult(
                false,
                [$"verifier internal error (treated as invalid): {error.GetType().Name}: {error.Message}"],
                false,
                null);
        }
    }

    private static VerificationResult VerifyCore(
        string packageDirectory,
        PluginPackageVerificationPolicy policy,
        PluginVerificationLimits limits)
    {
        var failures = new List<string>();
        string manifestPath = Path.Combine(packageDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new VerificationResult(false, ["manifest.json missing"], false, null);
        }

        // Input budgets FIRST (roadmap 16.12): a hostile package must not
        // burn memory reading a giant manifest before anything else runs.
        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > limits.MaxManifestBytes)
        {
            return new VerificationResult(
                false,
                [$"manifest.json exceeds the {limits.MaxManifestBytes}-byte input budget ({manifestInfo.Length} bytes)"],
                false,
                null);
        }
        string integrityPath = Path.Combine(packageDirectory, "package.integrity");
        if (File.Exists(integrityPath) && new FileInfo(integrityPath).Length > limits.MaxIntegrityBytes)
        {
            return new VerificationResult(
                false,
                [$"package.integrity exceeds the {limits.MaxIntegrityBytes}-byte input budget"],
                false,
                null);
        }

        (List<string> _, List<string> treeFailures) = WalkPackageTree(packageDirectory, limits);
        if (treeFailures.Count != 0)
        {
            return new VerificationResult(false, treeFailures, false, null);
        }
        string manifestJson = PluginPackageStorage.ReadText(manifestPath, limits.MaxManifestBytes);
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

            // ---------- Phase B: strict structure (STOP on failure) ----------
            ValidateStructure(root, packageDirectory, failures);
            if (failures.Count > 0)
            {
                return Result(failures, unsigned, manifestJson);
            }

            // ---------- Phase C: integrity chain (STOP on failure) ----------
            VerifyIntegrityChain(packageDirectory, root, manifestJson, failures, limits);
            if (failures.Count > 0)
            {
                return Result(failures, unsigned, manifestJson);
            }

            // ---------- Phase D: signature ----------
            if (policy == PluginPackageVerificationPolicy.Store && unsigned)
            {
                failures.Add("signature required for store packages (unsigned is Development-policy only)");
                return Result(failures, unsigned, manifestJson);
            }
            // runtime:native is in-process full-trust code; unsigned native
            // packages are never valid regardless of the caller's policy.
            if (StringValue(root, "runtime") == "native" && unsigned)
            {
                failures.Add("runtime:native requires a signature; unsigned native packages are never valid");
                return Result(failures, unsigned, manifestJson);
            }
            if (!unsigned)
            {
                VerifySignature(packageDirectory, root, signature, failures);
            }

            return Result(failures, unsigned, manifestJson);
        }
    }

    private static VerificationResult Result(List<string> failures, bool unsigned, string manifestJson) =>
        new(failures.Count == 0, failures, unsigned, manifestJson);

    // ---------- structural rules (schema v0.3, mirrors validate-lib.mjs) ----------
    // Every check FAILS CLOSED on wrong types: "required property present"
    // never silently substitutes for "property has the right type".
    private static void ValidateStructure(JsonElement root, string packageDirectory, List<string> failures)
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

        RequireInteger(root, "schemaVersion", value => value == 0, "schemaVersion must be the integer 0", Fail);
        if (StringValue(root, "id") is null)
        {
            Fail("id must be a string");
        }
        else if (!PackageIdPattern().IsMatch(StringValue(root, "id")!))
        {
            Fail("id pattern");
        }
        if (StringValue(root, "version") is null)
        {
            Fail("version must be a string");
        }
        else if (!IsValidVersion(StringValue(root, "version")))
        {
            Fail("version pattern");
        }
        string? runtime = StringValue(root, "runtime");
        if (runtime is null)
        {
            Fail("runtime must be a string");
        }
        else if (runtime is not ("none" or "wasm" or "process" or "native"))
        {
            Fail("runtime enum");
        }
        if (StringValue(root, "publisher") is not { Length: > 0 })
        {
            Fail("publisher must be a non-empty string");
        }
        if (StringValue(root, "publisherPublicKey") is not { Length: > 0 })
        {
            Fail("publisherPublicKey must be a non-empty string");
        }

        bool hasEntry = root.TryGetProperty("entry", out JsonElement entry) && entry.ValueKind == JsonValueKind.Object;
        if (runtime is "none" && hasEntry)
        {
            Fail("runtime:none packages must not declare an entry point");
        }
        if (runtime is "wasm" or "process" or "native" && !hasEntry)
        {
            Fail($"runtime '{runtime}' requires an entry point");
        }
        if (hasEntry)
        {
            foreach (JsonProperty property in entry.EnumerateObject())
            {
                if (property.Name != "main")
                {
                    Fail($"entry: unknown property '{property.Name}'");
                }
            }
            if (StringValue(entry, "main") is not { } entryMain || entryMain.Length == 0)
            {
                Fail("entry.main must be a non-empty string");
            }
            else
            {
                if (PackagePathViolation(entryMain) is { } entryViolation)
                {
                    Fail($"entry.main violates the package path grammar ({entryViolation})");
                }
                else if (!File.Exists(Path.Combine(packageDirectory, entryMain)))
                {
                    Fail($"entry.main file not found in package: {entryMain}");
                }
            }
        }

        if (!root.TryGetProperty("hostApi", out JsonElement hostApi) || hostApi.ValueKind != JsonValueKind.Object)
        {
            Fail("hostApi must be an object with string min/max");
        }
        else
        {
            foreach (string key in new[] { "min", "max" })
            {
                if (StringValue(hostApi, key) is not { Length: > 0 })
                {
                    Fail($"hostApi.{key} must be a non-empty string");
                }
                else if (!IsValidVersion(StringValue(hostApi, key)))
                {
                    Fail($"hostApi.{key} must be a supported three-part version");
                }
            }
            foreach (JsonProperty property in hostApi.EnumerateObject())
            {
                if (property.Name is not ("min" or "max"))
                {
                    Fail($"hostApi: unknown property '{property.Name}'");
                }
            }
            if (Version.TryParse(StringValue(hostApi, "min"), out Version? minimum) &&
                Version.TryParse(StringValue(hostApi, "max"), out Version? maximum) && minimum > maximum)
            {
                Fail("hostApi.min must not exceed hostApi.max");
            }
        }

        // Manifest numbers are integer-only within the JSON safe range and
        // duplicate keys are rejected (round 10): both are parser-drift
        // hazards between Node's IEEE-754/last-wins semantics and C#.
        RejectAmbiguousNumbersAndKeys(root, "manifest", Fail);

        if (root.TryGetProperty("dataSources", out JsonElement dataSourcesElement) &&
            dataSourcesElement.ValueKind != JsonValueKind.Object)
        {
            Fail("dataSources must be an object map");
        }
        if (root.TryGetProperty("actions", out JsonElement actionsElement) &&
            actionsElement.ValueKind != JsonValueKind.Object)
        {
            Fail("actions must be an object map");
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

        if (!root.TryGetProperty("contributions", out JsonElement contributions) ||
            contributions.ValueKind != JsonValueKind.Array ||
            contributions.GetArrayLength() == 0)
        {
            Fail("contributions: minItems 1");
            return;
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
            if (StringValue(contribution, "type") is not { } type || type != "widget")
            {
                Fail($"{where}: unknown contribution type");
            }
            if (StringValue(contribution, "id") is not { } contributionId)
            {
                Fail($"{where}: id must be a string");
            }
            else
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
            if (StringValue(contribution, "displayName") is not { Length: > 0 })
            {
                Fail($"{where}: displayName must be a non-empty string");
            }
            if (StringValue(contribution, "template") is not { } template || !Templates.Contains(template))
            {
                Fail($"{where}: template enum");
            }

            if (contribution.TryGetProperty("defaultSize", out JsonElement size))
            {
                if (size.ValueKind != JsonValueKind.Object)
                {
                    Fail($"{where}.defaultSize must be an object");
                }
                else
                {
                    foreach (string dimension in new[] { "width", "height" })
                    {
                        if (!size.TryGetProperty(dimension, out _))
                            Fail($"{where}.defaultSize.{dimension} is required");
                        RequireInteger(size, dimension, value => value > 0 && value <= int.MaxValue,
                            $"{where}.defaultSize.{dimension} must be a positive 32-bit integer", Fail);
                    }
                    if (size.EnumerateObject().Any(p => p.Name is not ("width" or "height")))
                        Fail($"{where}.defaultSize has unknown properties");
                }
            }
            if (contribution.TryGetProperty("activationEvents", out JsonElement activation))
            {
                string[] known = ["onStartupFinished", "onWidgetOpen", "onCommand", "onSchedule", "onFileAssociation", "onEvent"];
                if (activation.ValueKind != JsonValueKind.Array ||
                    activation.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String ||
                        !known.Contains(e.GetString(), StringComparer.Ordinal)))
                {
                    Fail($"{where}.activationEvents must contain only known activation events");
                }
            }
            if (contribution.TryGetProperty("payload", out JsonElement payloadProperty) &&
                payloadProperty.ValueKind != JsonValueKind.Object)
            {
                Fail($"{where}: payload must be an object");
            }
            JsonElement payload = contribution.TryGetProperty("payload", out JsonElement payloadElement) &&
                                  payloadElement.ValueKind == JsonValueKind.Object
                ? payloadElement
                : default;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (!payload.TryGetProperty("version", out JsonElement payloadVersion) ||
                    !payloadVersion.TryGetInt32(out int payloadVersionValue) ||
                    payloadVersionValue < 1)
                {
                    Fail($"{where}.payload.version >= 1 required");
                }
                if (StringValue(payload, "primaryActionId", out string? primaryActionId) &&
                    primaryActionId!.Length > 0)
                {
                    referencedActionIds.Add(primaryActionId);
                }
            }

            if (contribution.TryGetProperty("bindings", out JsonElement bindings))
            {
                if (bindings.ValueKind != JsonValueKind.Object)
                {
                    Fail($"{where}: bindings must be an object");
                }
                else
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
                    if (StringValue(binding, "source", out string? source) && source!.Length > 0)
                    {
                        if (!dataSources.ContainsKey(source))
                        {
                            Fail($"{bindingWhere}: unknown data source '{source}'");
                        }
                    }
                    else
                    {
                        Fail($"{bindingWhere}: 'source' must be a non-empty string");
                    }
                    if (StringValue(binding, "path", out string? path) && path!.Length > 0)
                    {
                        if (!PluginJsonPath.IsValid(path))
                        {
                            Fail($"{bindingWhere}: path must be a minimal JSON path like $.a.b[0].c");
                        }
                    }
                    else
                    {
                        Fail($"{bindingWhere}: 'path' must be a non-empty string");
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
            if (source.ValueKind != JsonValueKind.Object)
            {
                Fail($"{where}: must be an object");
                continue;
            }
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
            if (StringValue(source, "type") is not { } sourceType || sourceType != "http-json")
            {
                Fail($"{where}: v0.3 supports type http-json only");
            }
            if (StringValue(source, "url", out string? url) && url!.Length > 0)
            {
                if (!url.StartsWith("https://", StringComparison.Ordinal))
                {
                    Fail($"{where}: url must be HTTPS");
                }
            }
            else
            {
                Fail($"{where}: url must be a non-empty string");
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
            if (action.ValueKind != JsonValueKind.Object)
            {
                Fail($"{where}: must be an object");
                continue;
            }
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
            if (StringValue(action, "type") is not { } actionType || actionType != "open-url")
            {
                Fail($"{where}: v0.3 supports type open-url only");
            }
            if (StringValue(action, "url", out string? actionUrl) && actionUrl!.Length > 0)
            {
                if (!actionUrl.StartsWith("https://", StringComparison.Ordinal))
                {
                    Fail($"{where}: url must be HTTPS");
                }
            }
            else
            {
                Fail($"{where}: url must be a non-empty string");
            }
        }
        if (actions.Count > 0)
        {
            // action-list/simple-form entries reference actions by id; the
            // primaryActionId references were collected above.
            foreach (JsonElement contribution in contributions.EnumerateArray())
            {
                if (!contribution.TryGetProperty("payload", out JsonElement payloadElement) ||
                    payloadElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (JsonProperty property in payloadElement.EnumerateObject())
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

        // Permissions: shape (fail-closed on types), unique ids, consumption rules.
        if (root.TryGetProperty("permissions", out JsonElement permissionsProperty) &&
            permissionsProperty.ValueKind != JsonValueKind.Array)
        {
            Fail("permissions must be an array");
        }
        List<JsonElement> permissions = root.TryGetProperty("permissions", out JsonElement permissionsElement) &&
                                        permissionsElement.ValueKind == JsonValueKind.Array
            ? permissionsElement.EnumerateArray().ToList()
            : [];
        var permissionIds = new HashSet<string>();
        for (int i = 0; i < permissions.Count; i++)
        {
            JsonElement permission = permissions[i];
            string where = $"permissions[{i}]";
            if (permission.ValueKind != JsonValueKind.Object)
            {
                Fail($"{where}: must be an object");
                continue;
            }
            foreach (JsonProperty property in permission.EnumerateObject())
            {
                if (property.Name is not ("id" or "required" or "scope"))
                {
                    Fail($"{where}: unknown property '{property.Name}'");
                }
            }
            if (StringValue(permission, "id", out string? permissionId) && permissionId!.Length > 0)
            {
                if (!PermissionIdPattern().IsMatch(permissionId))
                {
                    Fail($"{where}: id pattern");
                }
                else if (!KnownPermissionIds.Contains(permissionId))
                {
                    Fail($"{where}: unknown permission id '{permissionId}' (registry: {string.Join(", ", KnownPermissionIds)})");
                }
                else if (!permissionIds.Add(permissionId))
                {
                    // Duplicate ids with different scopes would make grant/scope
                    // lookup implementation-defined across runtimes.
                    Fail($"permissions: duplicate id '{permissionId}' (use one entry with multiple scope.allow hosts)");
                }
            }
            else
            {
                Fail($"{where}: id must be a non-empty string");
            }
            if (permission.TryGetProperty("required", out JsonElement required) &&
                required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Fail($"{where}: required must be a boolean");
            }
            if (permission.TryGetProperty("scope", out JsonElement scope))
            {
                if (scope.ValueKind != JsonValueKind.Object)
                {
                    Fail($"{where}: scope must be an object");
                }
                else
                {
                    foreach (JsonProperty scopeProperty in scope.EnumerateObject())
                    {
                        if (scopeProperty.Name != "allow")
                        {
                            Fail($"{where}.scope: unknown property '{scopeProperty.Name}'");
                        }
                    }
                    if (scope.TryGetProperty("allow", out JsonElement allow))
                    {
                        if (allow.ValueKind != JsonValueKind.Array)
                        {
                            Fail($"{where}.scope.allow must be an array");
                        }
                        else
                        {
                            foreach (JsonElement allowEntry in allow.EnumerateArray())
                            {
                                if (allowEntry.ValueKind != JsonValueKind.String)
                                {
                                    Fail($"{where}.scope.allow entries must be strings");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (KeyValuePair<string, JsonElement> pair in dataSources)
        {
            if (StringValue(pair.Value, "type", out string? type) && type == "http-json")
            {
                if (!permissionIds.Contains("network.fetch"))
                {
                    Fail($"dataSources['{pair.Key}']: http-json requires the network.fetch permission");
                }
                else if (StringValue(pair.Value, "url", out string? url) &&
                         !HostInScope(url!, permissions, "network.fetch"))
                {
                    Fail($"dataSources['{pair.Key}']: url host is outside the declared network.fetch scope");
                }
            }
        }
        foreach (KeyValuePair<string, JsonElement> pair in actions)
        {
            if (StringValue(pair.Value, "type", out string? type) && type == "open-url")
            {
                if (!permissionIds.Contains("shell.open"))
                {
                    Fail($"actions['{pair.Key}']: open-url requires the shell.open permission");
                }
                else if (StringValue(pair.Value, "url", out string? url) &&
                         !HostInScope(url!, permissions, "shell.open"))
                {
                    Fail($"actions['{pair.Key}']: url host is outside the declared shell.open scope");
                }
            }
        }
    }

    /// <summary>
    /// Walks the tree and rejects duplicate JSON property names (parsers
    /// disagree on last-vs-first-wins; a signed manifest has zero use for
    /// them), non-integer numbers, values beyond the JSON safe-integer
    /// range ±(2^53-1) (Node re-rounds them via IEEE-754), and "-0"
    /// (Node canonicalizes to "0", C# raw tokens differ).
    /// </summary>
    private static void RejectAmbiguousNumbersAndKeys(JsonElement element, string where, Action<string> fail)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        fail($"{where}: duplicate property '{property.Name}' (parser-ambiguous, rejected)");
                    }
                    RejectAmbiguousNumbersAndKeys(property.Value, $"{where}.{property.Name}", fail);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    RejectAmbiguousNumbersAndKeys(item, $"{where}[{index++}]", fail);
                }
                break;
            case JsonValueKind.Number:
                string raw = element.GetRawText();
                if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                {
                    fail($"{where}: manifest numbers must be integers (floats are rejected to avoid cross-platform canonicalization drift)");
                }
                else if (raw == "-0")
                {
                    fail($"{where}: -0 is rejected (Node canonicalizes it to 0; raw tokens differ)");
                }
                else if (!element.TryGetInt64(out long value) ||
                         value is > 9007199254740991 or < -9007199254740991)
                {
                    fail($"{where}: manifest integers must be within the JSON safe range ±(2^53-1) (Node re-rounds larger values via IEEE-754)");
                }
                break;
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
                .Where(p => StringValue(p, "id", out string? id) && id == permissionId)
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

    /// <summary>The string value when the property exists and is a string; null otherwise.</summary>
    private static string? StringValue(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out JsonElement node) &&
               node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static bool StringValue(JsonElement element, string property, out string? value)
    {
        value = StringValue(element, property);
        return value is not null;
    }

    private static void RequireInteger(
        JsonElement element,
        string property,
        Func<int, bool> predicate,
        string message,
        Action<string> fail)
    {
        if (!element.TryGetProperty(property, out JsonElement node))
        {
            return; // missing already reported by the required scan
        }
        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt32(out int value))
        {
            fail($"{property} must be an integer");
        }
        else if (!predicate(value))
        {
            fail(message);
        }
    }

    // The v0 permission registry (round 10): exactly two capabilities
    // exist; unknown ids are rejected at install time - "register it into
    // the database and decide later" is not allowed.
    private static readonly string[] KnownPermissionIds = ["network.fetch", "shell.open"];

    // Windows-safe path grammar (round 10): besides the zip-slip rules,
    // reject DOS device names (CON/NUL/COM1... incl. with extensions),
    // trailing dots/spaces, and control characters - the filesystem and
    // the integrity strings must denote the same object.
    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

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
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                return "segment ending in space or dot";
            }
            if (segment.Any(ch => char.IsControl(ch)))
            {
                return "control character in segment";
            }
            string baseName = segment.Split('.')[0];
            if (ReservedDeviceNames.FirstOrDefault(name =>
                    string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) is { } reserved)
            {
                return $"reserved Windows device name '{reserved}'";
            }
        }
        return null;
    }

    // ---------- integrity + signature chain (notes walkthrough steps 1-5) ----------
    private static void VerifyIntegrityChain(
        string packageDirectory,
        JsonElement root,
        string manifestJson,
        List<string> failures,
        PluginVerificationLimits limits)
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
        foreach (string rawLine in PluginPackageStorage.ReadText(integrityPath, limits.MaxIntegrityBytes).Split('\n'))
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
        string canonicalManifestHash;
        try
        {
            canonicalManifestHash = Sha256Hex(CanonicalizeManifest(root));
        }
        catch (Exception error)
        {
            Fail($"manifest canonicalization failed: {error.Message}");
            return;
        }
        if (!listed.TryGetValue("manifest.json", out string? manifestLine) ||
            !string.Equals(manifestLine, canonicalManifestHash, StringComparison.Ordinal))
        {
            Fail("manifest.json integrity line does not match its canonicalization (signature=null)");
        }

        // Step 2: every payload file hashes to its line and is listed. The
        // walk never follows reparse points; any reparse point in the tree
        // is itself a failure (round 10). File-level budgets (count, size,
        // path length) are enforced DURING the walk so a hostile package
        // cannot exhaust resources before its lines even get checked.
        (List<string> payloadFiles, List<string> walkFailures) = WalkPackageTree(packageDirectory, limits);
        foreach (string walkFailure in walkFailures)
        {
            Fail(walkFailure);
        }
        if (walkFailures.Count != 0) return;
        long totalBytes = 0;
        if (payloadFiles.Count > limits.MaxFileCount)
        {
            Fail($"package exceeds the file-count budget ({payloadFiles.Count} > {limits.MaxFileCount})");
        }
        foreach (string file in payloadFiles)
        {
            string relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            if (relative == "package.integrity")
            {
                continue;
            }
            if (relative.Length > limits.MaxRelativePathLength)
            {
                Fail($"payload path exceeds the {limits.MaxRelativePathLength}-character budget: {Truncate(relative, 40)}…");
                continue;
            }
            if (PackagePathViolation(relative) is { } violation)
            {
                Fail($"payload file violates the package path grammar ({violation}): {relative}");
                continue;
            }
            if (relative == "manifest.json")
            {
                continue;
            }
            long fileLength = new FileInfo(file).Length;
            totalBytes += fileLength;
            if (fileLength > limits.MaxSingleFileBytes)
            {
                Fail($"payload file exceeds the single-file budget ({fileLength} > {limits.MaxSingleFileBytes} bytes): {relative}");
                continue;
            }
            string digest = Sha256HexFile(file);
            if (!listed.TryGetValue(relative, out string? line))
            {
                Fail($"payload file not listed in package.integrity: {relative}");
            }
            else if (!string.Equals(line, digest, StringComparison.Ordinal))
            {
                Fail($"integrity mismatch for {relative}");
            }
        }
        if (totalBytes > limits.MaxTotalExpandedBytes)
        {
            Fail($"package exceeds the total-expanded budget ({totalBytes} > {limits.MaxTotalExpandedBytes} bytes)");
        }
        foreach (string relative in listed.Keys)
        {
            if (relative != "manifest.json" && !File.Exists(Path.Combine(packageDirectory, relative)))
            {
                Fail($"package.integrity lists a missing file: {relative}");
            }
        }
    }

    /// <summary>Streaming hash (roadmap 16.12): payload assets hash without a full in-memory copy.</summary>
    internal static string Sha256HexFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void VerifySignature(string packageDirectory, JsonElement root, JsonElement signature, List<string> failures)
    {
        void Fail(string message) => failures.Add(message);
        if (signature.ValueKind != JsonValueKind.Object)
        {
            Fail("signature must be an object when present");
            return;
        }
        foreach (string key in new[] { "contentHash", "publisherSignature" })
        {
            if (!StringValue(signature, key, out string? value) || value!.Length == 0)
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
        if (StringValue(root, "publisherPublicKey") is not { } publicKeyText)
        {
            return; // already failed structurally
        }
        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(publicKeyText);
        }
        catch (FormatException)
        {
            Fail("publisherPublicKey is not valid base64");
            return;
        }
        if (publicKey.Length != 32)
        {
            Fail($"publisherPublicKey must decode to 32 raw bytes, got {publicKey.Length}");
            return;
        }
        string fingerprint = Sha256Hex(publicKey);
        if (!string.Equals(StringValue(root, "publisher"), fingerprint, StringComparison.Ordinal))
        {
            Fail("publisher does not equal sha256(raw publisherPublicKey bytes)");
        }

        // Step 4: Ed25519 over the RAW 32-byte digest.
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature.GetProperty("publisherSignature").GetString()!);
        }
        catch (FormatException)
        {
            Fail("publisherSignature is not valid base64");
            return;
        }
        if (!PluginEd25519.Verify(signatureBytes, Convert.FromHexString(contentHash), publicKey))
        {
            Fail("publisherSignature verification failed");
        }
    }

    /// <summary>
    /// Explicit tree walk that NEVER follows reparse points (round 10):
    /// SearchOption.AllDirectories follows symlinks/junctions, letting a
    /// hostile package read outside its directory or loop forever. Any
    /// reparse point in the package tree is a FAILURE, not an invisible
    /// skip - a symlink would also be absent from the integrity inventory.
    /// </summary>
    internal static (List<string> Files, List<string> Failures) WalkPackageTree(
        string packageDirectory, PluginVerificationLimits? inputLimits = null)
    {
        PluginVerificationLimits limits = inputLimits ?? PluginVerificationLimits.Default;
        var files = new List<string>();
        var failures = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((packageDirectory, 0));
        int directories = 0;
        long totalBytes = 0;
        while (queue.Count > 0 && failures.Count == 0)
        {
            (string directory, int depth) = queue.Dequeue();
            PluginPackageStorage.RejectReparsePoint(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(packageDirectory, entry).Replace('\\', '/');
                if (relative.Length > limits.MaxRelativePathLength)
                {
                    failures.Add($"payload path exceeds the {limits.MaxRelativePathLength}-character budget");
                    break;
                }
                if (PackagePathViolation(relative) is { } violation)
                {
                    failures.Add($"payload path violates the package path grammar ({violation}): {relative}");
                    break;
                }
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    failures.Add(
                        $"package tree contains a reparse point (symlink/junction/mount): " +
                        Path.GetRelativePath(packageDirectory, entry).Replace('\\', '/'));
                    break;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (depth + 1 > limits.MaxTreeDepth || ++directories > limits.MaxDirectoryCount)
                    {
                        failures.Add("package exceeds the tree-depth or directory-count budget");
                        break;
                    }
                    queue.Enqueue((entry, depth + 1));
                }
                else
                {
                    if (files.Count >= limits.MaxFileCount)
                    {
                        failures.Add("package exceeds the file-count budget");
                        break;
                    }
                    long length = new FileInfo(entry).Length;
                    long maximum = relative == "manifest.json" ? limits.MaxManifestBytes :
                        relative == "package.integrity" ? limits.MaxIntegrityBytes : limits.MaxSingleFileBytes;
                    if (length > maximum)
                    {
                        failures.Add($"payload file exceeds the single-file input budget: {relative}");
                        break;
                    }
                    if (length > limits.MaxTotalExpandedBytes - totalBytes)
                    {
                        failures.Add("package exceeds the total-expanded budget");
                        break;
                    }
                    totalBytes += length;
                    files.Add(entry);
                }
            }
        }
        return (files, failures);
    }

    internal static bool IsValidVersion(string? value) =>
        value is { Length: <= 32 } && VersionPattern().IsMatch(value) &&
        Version.TryParse(value, out Version? version) && version.Build >= 0 && version.Revision == -1;

    /// <summary>
    /// JCS-subset canonicalization (parity with the Node tooling): object
    /// keys ordinal-sorted (UTF-16 code units, same as JS default sort), no
    /// whitespace, minimal string escaping, number tokens preserved verbatim
    /// - valid because manifests are integer-only (enforced structurally).
    /// The signature property is forced to null (written as an explicit null
    /// at its sorted position, exactly like the Node tooling's
    /// {...manifest, signature: null}) so the hash domain is self-reference
    /// free and byte-identical across platforms.
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
                // Parsed-and-rewritten decimal (round 10), NOT the raw
                // token: integers are safe-range only (enforced by the
                // structural pass), so the rewrite is byte-stable with the
                // Node canonicalization.
                writer.WriteNumberValue(element.GetInt64());
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
