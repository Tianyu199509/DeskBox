using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record StudioFlatManifestEditRequest(
    JsonObject? UpsertElements = null,
    IReadOnlyList<string>? RemoveElements = null,
    JsonObject? PatchRoot = null,
    IReadOnlyList<string>? RemoveRootKeys = null);

public sealed record StudioScriptPatchHunk(
    string Search,
    string Replace);

public sealed record StudioReadWorkspaceRequest(
    string Section = "overview",
    string? NodePath = null,
    int MaxDepth = 4,
    string? Symbol = null,
    int? StartLine = null,
    int? EndLine = null);

public sealed record StudioMutationRecord(
    string Kind,
    string Path,
    string? NodeType,
    string? PropertyName);

public sealed record StudioWorkspaceCheckpoint(
    string? ManifestJson,
    string? LogicJs,
    CustomWidgetLocaleCatalog? Locales,
    IReadOnlyList<StudioMutationRecord>? Mutations = null);

public sealed record StudioWorkspaceOpResult(
    bool Succeeded,
    string Summary,
    string? ErrorMessage = null,
    string? DiagnosticSnippet = null,
    IReadOnlyList<string>? TouchedPaths = null);

public sealed record StudioWorkspaceValidationOutcome(
    bool IsValid,
    string? ManifestJson,
    string? LogicJs,
    CustomWidgetLocaleCatalog? Locales,
    CustomWidgetWorkspaceValidationResult Validation,
    string? PreFlightFeedback = null,
    string? LocalizedRepairPrompt = null);

/// <summary>
/// Stateful Flat Element Graph + script workspace manager for Custom Widget Studio authoring.
/// Provides Context Folding over large manifests/scripts, element-ID flat graph editing (<c>edit_manifest</c>),
/// 3-tier fuzzy SEARCH/REPLACE script patching, atomic checkpoints,
/// single-pass DOM hygiene normalization, and localized validation repair snippets.
/// </summary>
public sealed partial class StudioWorkspaceEngine
{
    public const int ManifestFoldLineThreshold = 600;
    public const int ManifestFoldCharThreshold = 24_000;
    public const int ScriptFoldLineThreshold = 360;
    public const int ScriptFoldCharThreshold = 14_000;

    private static readonly JsonWriterOptions IndentedWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonWriterOptions CompactWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonNodeOptions PermissiveNodeOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private static readonly JsonDocumentOptions PermissiveDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128
    };

    private static readonly FrozenSet<string> ChildContainerKeys = new[]
    {
        "children", "child", "items", "tabs", "itemTemplate", "pane", "actionButton"
    }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^\s*(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(")]
    private static partial Regex JsFunctionDeclarationRegex();

    [GeneratedRegex(@"^\s*(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:function\b|\([^)]*\)\s*=>|[A-Za-z_$][A-Za-z0-9_$]*\s*=>)")]
    private static partial Regex JsConstFunctionRegex();

    [GeneratedRegex(@"^\s*L?\d+:\s?")]
    private static partial Regex LineNumberPrefixRegex();

    [GeneratedRegex(@"\bline\s+(\d+)\b|\bL(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticScriptLineRegex();

    private readonly List<StudioMutationRecord> _mutations = [];
    private JsonObject? _manifestRoot;
    private string? _logicJs;
    private CustomWidgetLocaleCatalog? _locales;
    private bool _deleteScript;
    private string? _lockedCanonicalPath;
    private JsonObject? _baselineManifestSnapshot;

    public StudioWorkspaceEngine(
        string? initialManifestJson = null,
        string? initialLogicJs = null,
        CustomWidgetLocaleCatalog? initialLocales = null,
        string? targetNodePath = null)
    {
        LoadBundle(initialManifestJson, initialLogicJs, initialLocales, targetNodePath);
    }

    public JsonObject? ManifestRoot => _manifestRoot;

    public string? LogicJs => _logicJs;

    public CustomWidgetLocaleCatalog? Locales
    {
        get => _locales;
        set => _locales = value;
    }

    public bool DeleteScript
    {
        get => _deleteScript;
        set => _deleteScript = value;
    }

    public bool HasManifest => _manifestRoot is not null;

    public string? LockedCanonicalPath => _lockedCanonicalPath;

    public string? LastSubtreeGraftNote { get; private set; }

    public string? ExtractedInlineSampleDataJson { get; private set; }

    public IReadOnlyList<StudioMutationRecord> RecordedMutations => _mutations;

    /// <summary>
    /// Creates an isolated deep clone of this workspace so candidate DOM normalization and
    /// compilation never shift array indices or mutate live workspace state before validation passes.
    /// </summary>
    public StudioWorkspaceEngine CloneForCandidateCompilation()
    {
        var clone = new StudioWorkspaceEngine
        {
            _manifestRoot = _manifestRoot?.DeepClone() as JsonObject,
            _baselineManifestSnapshot = _baselineManifestSnapshot?.DeepClone() as JsonObject,
            _logicJs = _logicJs,
            _locales = _locales,
            _deleteScript = _deleteScript,
            _lockedCanonicalPath = _lockedCanonicalPath,
            LastSubtreeGraftNote = LastSubtreeGraftNote,
            ExtractedInlineSampleDataJson = ExtractedInlineSampleDataJson
        };
        clone._mutations.AddRange(_mutations);
        return clone;
    }

    public void LoadBundle(
        string? manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        string? targetNodePath = null)
    {
        _manifestRoot = ParseManifestObject(manifestJson);
        _logicJs = string.IsNullOrWhiteSpace(logicJs) ? null : NormalizeNewlines(logicJs);
        _locales = locales;
        _deleteScript = false;
        _mutations.Clear();
        LastSubtreeGraftNote = null;
        ExtractedInlineSampleDataJson = null;
        ExtractAndStripStrayBundleFieldsInPlace(allowOverwriteExistingLogic: false, out _);
        InitializeSubtreeGuard(targetNodePath);
    }

    private void InitializeSubtreeGuard(string? rawTargetNodePath)
    {
        _lockedCanonicalPath = null;
        _baselineManifestSnapshot = _manifestRoot?.DeepClone() as JsonObject;

        if (_manifestRoot is null || string.IsNullOrWhiteSpace(rawTargetNodePath))
        {
            return;
        }

        string? cleanSelector = ExtractCleanNodeSelector(rawTargetNodePath);
        if (string.IsNullOrWhiteSpace(cleanSelector) ||
            string.Equals(cleanSelector, "$", StringComparison.Ordinal) ||
            string.Equals(cleanSelector, "$.elements", StringComparison.Ordinal))
        {
            return;
        }

        if (TryResolveNode(_manifestRoot, cleanSelector, out JsonNode? target, out _, out _, out string resolvedPath, out _) &&
            target is not null &&
            !string.IsNullOrWhiteSpace(resolvedPath) &&
            !string.Equals(resolvedPath, "$", StringComparison.Ordinal) &&
            !string.Equals(resolvedPath, "$.elements", StringComparison.Ordinal))
        {
            _lockedCanonicalPath = resolvedPath;
            _baselineManifestSnapshot = _manifestRoot.DeepClone() as JsonObject;
        }
    }

    public StudioWorkspaceCheckpoint CaptureCheckpoint() =>
        new(
            GetManifestJson(indented: false),
            _logicJs,
            _locales,
            _mutations.ToArray());

    public void RestoreCheckpoint(StudioWorkspaceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _manifestRoot = ParseManifestObject(checkpoint.ManifestJson);
        _logicJs = checkpoint.LogicJs;
        _locales = checkpoint.Locales;
        _mutations.Clear();
        if (checkpoint.Mutations is { Count: > 0 })
        {
            _mutations.AddRange(checkpoint.Mutations);
        }
    }

    public string? GetManifestJson(bool indented = true)
    {
        if (_manifestRoot is null)
        {
            return null;
        }

        return SerializeNode(_manifestRoot, indented);
    }

    public StudioWorkspaceOpResult SetFullBundle(
        string manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        bool deleteScript = false)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return new StudioWorkspaceOpResult(false, "write_bundle failed", "manifestJson must not be empty.");
        }

        JsonObject? parsed = ParseManifestObject(manifestJson);
        if (parsed is null)
        {
            return new StudioWorkspaceOpResult(false, "write_bundle failed", "manifestJson must be a valid JSON object.");
        }

        string? graftNote = null;
        if (!EnforceSubtreeGuardOnFullReplace(parsed, out string? guardError, out graftNote))
        {
            return new StudioWorkspaceOpResult(
                false,
                "write_bundle rejected by Subtree Guard",
                guardError);
        }

        LastSubtreeGraftNote = graftNote;
        _manifestRoot = parsed;
        _mutations.Add(new StudioMutationRecord("bundle", "$", _manifestRoot["type"]?.ToString(), null));
        bool allowInlineLogicPromotion = !deleteScript && string.IsNullOrWhiteSpace(logicJs);
        if (deleteScript)
        {
            _logicJs = null;
            _deleteScript = true;
            _mutations.Add(new StudioMutationRecord("delete_script", "logic.js", null, null));
        }
        else if (logicJs is not null)
        {
            _logicJs = string.IsNullOrWhiteSpace(logicJs) ? null : NormalizeNewlines(logicJs);
            _deleteScript = false;
            if (!string.IsNullOrWhiteSpace(_logicJs))
            {
                _mutations.Add(new StudioMutationRecord("script", "logic.js", null, null));
            }
        }

        ExtractAndStripStrayBundleFieldsInPlace(allowOverwriteExistingLogic: allowInlineLogicPromotion, out bool promotedScript);
        if (promotedScript)
        {
            _mutations.Add(new StudioMutationRecord("script", "logic.js", null, null));
        }

        if (locales is not null)
        {
            _locales = locales;
        }

        string summary = string.IsNullOrWhiteSpace(graftNote)
            ? "Updated full workspace bundle."
            : $"Updated full workspace bundle. {graftNote}";
        return new StudioWorkspaceOpResult(true, summary, TouchedPaths: ["$", "logic.js"]);
    }

    public StudioWorkspaceOpResult ReplaceManifestJson(string manifestJson, bool bypassSubtreeGuard = false)
    {
        JsonObject? parsed = ParseManifestObject(manifestJson);
        if (parsed is null)
        {
            return new StudioWorkspaceOpResult(false, "Manifest update failed", "manifestJson must be a valid JSON object.");
        }

        string? graftNote = null;
        if (!bypassSubtreeGuard &&
            !EnforceSubtreeGuardOnFullReplace(parsed, out string? guardError, out graftNote))
        {
            return new StudioWorkspaceOpResult(
                false,
                "Manifest replacement rejected by Subtree Guard",
                guardError);
        }

        if (!string.IsNullOrWhiteSpace(graftNote))
        {
            LastSubtreeGraftNote = graftNote;
        }

        _manifestRoot = parsed;
        _mutations.Add(new StudioMutationRecord("replace_manifest", "$", _manifestRoot["type"]?.ToString(), null));
        ExtractAndStripStrayBundleFieldsInPlace(allowOverwriteExistingLogic: !_deleteScript, out bool promotedScript);
        if (promotedScript)
        {
            _mutations.Add(new StudioMutationRecord("script", "logic.js", null, null));
        }

        string summary = string.IsNullOrWhiteSpace(graftNote)
            ? "Replaced manifest JSON."
            : $"Replaced manifest JSON. {graftNote}";
        return new StudioWorkspaceOpResult(true, summary, TouchedPaths: promotedScript ? ["$", "logic.js"] : ["$"]);
    }

    public void SetLogicJs(string? logicJs, bool deleteScript = false)
    {
        if (deleteScript)
        {
            _logicJs = null;
            _deleteScript = true;
            _mutations.Add(new StudioMutationRecord("delete_script", "logic.js", null, null));
            return;
        }

        _logicJs = string.IsNullOrWhiteSpace(logicJs) ? null : NormalizeNewlines(logicJs);
        _deleteScript = false;
        _mutations.Add(new StudioMutationRecord("script", "logic.js", null, null));
    }

    public string ReadWorkspace(StudioReadWorkspaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string section = string.IsNullOrWhiteSpace(request.Section)
            ? "overview"
            : request.Section.Trim().ToLowerInvariant();
        int maxDepth = Math.Clamp(request.MaxDepth, 1, 8);

        return section switch
        {
            "overview" or "summary" => ReadOverviewSection(),
            "node" => ReadNodeSection(request.NodePath, maxDepth),
            "manifest_section" or "manifest" => ReadManifestSection(request.NodePath, maxDepth, request.StartLine, request.EndLine),
            "script" or "logic" => ReadScriptSection(request.Symbol, request.StartLine, request.EndLine),
            "locales" or "locale" => ReadLocalesSection(request.NodePath ?? request.Symbol),
            _ => $"Unknown workspace section '{request.Section}'. Valid sections: overview, node, manifest_section, script, locales."
        };
    }

    public StudioWorkspaceOpResult ApplyFlatManifestEdit(StudioFlatManifestEditRequest request)
    {
        if (_manifestRoot is null)
        {
            return new StudioWorkspaceOpResult(
                false,
                "edit_manifest failed",
                "Workspace does not have an initialized manifest.json yet. Call write_bundle first to create the initial manifest.");
        }

        if (request is null ||
            ((request.UpsertElements is null || request.UpsertElements.Count == 0) &&
             (request.RemoveElements is null || request.RemoveElements.Count == 0) &&
             (request.PatchRoot is null || request.PatchRoot.Count == 0) &&
             (request.RemoveRootKeys is null || request.RemoveRootKeys.Count == 0)))
        {
            return new StudioWorkspaceOpResult(
                false,
                "edit_manifest failed",
                "Provide at least one of upsertElements, removeElements, patchRoot, or removeRootKeys.");
        }

        if (_manifestRoot["elements"] is not JsonObject elementsObj)
        {
            return new StudioWorkspaceOpResult(
                false,
                "edit_manifest failed",
                "Workspace manifest does not have a canonical schemaVersion 1 flat 'elements' object (root + elements). Call write_bundle with a canonical schemaVersion 1 flat element graph manifest first.");
        }

        StudioWorkspaceCheckpoint checkpoint = CaptureCheckpoint();
        var touched = new List<string>();
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            _manifestRoot["schemaVersion"] = JsonValue.Create(1);

            if (request.RemoveRootKeys is { Count: > 0 })
            {
                foreach (string rawKey in request.RemoveRootKeys)
                {
                    string key = (rawKey ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(key) ||
                        key is "schemaVersion" or "id" or "elements" or "root")
                    {
                        continue;
                    }

                    if (_manifestRoot.Remove(key))
                    {
                        touched.Add($"$.{key}");
                        _mutations.Add(new StudioMutationRecord("remove", $"$.{key}", null, key));
                    }
                }
            }

            if (request.PatchRoot is { Count: > 0 })
            {
                foreach (KeyValuePair<string, JsonNode?> kv in request.PatchRoot)
                {
                    string key = kv.Key.Trim();
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (string.Equals(key, "schemaVersion", StringComparison.Ordinal))
                    {
                        _manifestRoot["schemaVersion"] = JsonValue.Create(1);
                        continue;
                    }

                    if (string.Equals(key, "elements", StringComparison.Ordinal) && kv.Value is JsonObject nestedElems)
                    {
                        foreach (KeyValuePair<string, JsonNode?> ekv in nestedElems)
                        {
                            if (ekv.Value is JsonObject eObj)
                            {
                                elementsObj[ekv.Key] = eObj.DeepClone();
                                touched.Add($"#{ekv.Key}");
                                _mutations.Add(new StudioMutationRecord("upsert_element", $"#{ekv.Key}", eObj["type"]?.ToString(), null));
                            }
                            else if (ekv.Value is null && elementsObj.Remove(ekv.Key))
                            {
                                removedIds.Add(ekv.Key);
                                touched.Add($"#{ekv.Key}");
                                _mutations.Add(new StudioMutationRecord("remove_element", $"#{ekv.Key}", null, null));
                            }
                        }
                        continue;
                    }

                    if (kv.Value is null)
                    {
                        if (key is not ("schemaVersion" or "id" or "elements" or "root") && _manifestRoot.Remove(key))
                        {
                            touched.Add($"$.{key}");
                            _mutations.Add(new StudioMutationRecord("remove", $"$.{key}", null, key));
                        }
                    }
                    else
                    {
                        _manifestRoot[key] = kv.Value.DeepClone();
                        touched.Add($"$.{key}");
                        _mutations.Add(new StudioMutationRecord("set", $"$.{key}", null, key));
                    }
                }
            }

            if (request.UpsertElements is { Count: > 0 })
            {
                foreach (KeyValuePair<string, JsonNode?> kv in request.UpsertElements)
                {
                    string elemId = kv.Key.Trim().TrimStart('#');
                    if (string.IsNullOrEmpty(elemId))
                    {
                        continue;
                    }

                    if (kv.Value is null)
                    {
                        if (elementsObj.Remove(elemId))
                        {
                            removedIds.Add(elemId);
                            touched.Add($"#{elemId}");
                            _mutations.Add(new StudioMutationRecord("remove_element", $"#{elemId}", null, null));
                        }
                        continue;
                    }

                    if (kv.Value is not JsonObject elemObj)
                    {
                        RestoreCheckpoint(checkpoint);
                        return new StudioWorkspaceOpResult(
                            false,
                            $"edit_manifest failed at upsertElements.{elemId}",
                            $"Element '{elemId}' must be a JSON object (with 'type' and properties) or null to remove.");
                    }

                    elementsObj[elemId] = elemObj.DeepClone();
                    touched.Add($"#{elemId}");
                    _mutations.Add(new StudioMutationRecord("upsert_element", $"#{elemId}", elemObj["type"]?.ToString(), null));
                }
            }

            if (request.RemoveElements is { Count: > 0 })
            {
                foreach (string rawId in request.RemoveElements)
                {
                    string elemId = (rawId ?? string.Empty).Trim().TrimStart('#');
                    if (string.IsNullOrEmpty(elemId))
                    {
                        continue;
                    }

                    if (elementsObj.Remove(elemId))
                    {
                        removedIds.Add(elemId);
                        touched.Add($"#{elemId}");
                        _mutations.Add(new StudioMutationRecord("remove_element", $"#{elemId}", null, null));
                    }
                }
            }

            // Clean dangling element ID references from remaining elements after removals
            if (removedIds.Count > 0)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in elementsObj)
                {
                    if (kv.Value is not JsonObject elemObj)
                    {
                        continue;
                    }

                    foreach (string arrProp in (ReadOnlySpan<string>)["children", "hoverActions"])
                    {
                        if (elemObj[arrProp] is JsonArray arr)
                        {
                            for (int idx = arr.Count - 1; idx >= 0; idx--)
                            {
                                if (arr[idx] is JsonValue v &&
                                    v.TryGetValue<string>(out string? refId) &&
                                    !string.IsNullOrWhiteSpace(refId) &&
                                    removedIds.Contains(refId.Trim()))
                                {
                                    arr.RemoveAt(idx);
                                }
                            }
                        }
                    }

                    foreach (string refProp in (ReadOnlySpan<string>)["itemTemplate", "pane", "actionButton"])
                    {
                        if (elemObj[refProp] is JsonValue v &&
                            v.TryGetValue<string>(out string? refId) &&
                            !string.IsNullOrWhiteSpace(refId) &&
                            removedIds.Contains(refId.Trim()))
                        {
                            elemObj.Remove(refProp);
                        }
                    }
                }
            }

            return new StudioWorkspaceOpResult(
                true,
                $"Applied flat manifest edit ({touched.Count} target(s) updated).",
                TouchedPaths: touched);
        }
        catch (Exception ex)
        {
            RestoreCheckpoint(checkpoint);
            return new StudioWorkspaceOpResult(
                false,
                "edit_manifest rolled back due to unexpected error",
                ex.Message);
        }
    }

    public StudioWorkspaceOpResult ApplyScriptPatch(IReadOnlyList<StudioScriptPatchHunk> hunks)
    {
        if (hunks is null || hunks.Count == 0)
        {
            return new StudioWorkspaceOpResult(
                false,
                "patch_script failed",
                "At least one SEARCH/REPLACE hunk is required.");
        }

        string originalScript = _logicJs ?? string.Empty;
        bool hadCrlf = originalScript.Contains("\r\n", StringComparison.Ordinal);
        string working = NormalizeNewlines(originalScript);
        StudioWorkspaceCheckpoint checkpoint = CaptureCheckpoint();

        for (int i = 0; i < hunks.Count; i++)
        {
            StudioScriptPatchHunk hunk = hunks[i];
            string search = NormalizeNewlines(hunk.Search ?? string.Empty);
            string replace = NormalizeNewlines(hunk.Replace ?? string.Empty);

            if (string.IsNullOrEmpty(search))
            {
                if (string.IsNullOrWhiteSpace(working))
                {
                    working = replace;
                    continue;
                }

                RestoreCheckpoint(checkpoint);
                return new StudioWorkspaceOpResult(
                    false,
                    $"patch_script failed at hunk[{i}]",
                    "Empty SEARCH block is only allowed when logic.js does not exist yet. Provide exact existing lines from logic.js to anchor your edit.");
            }

            if (HasSuspiciousLineNumberPrefixes(search))
            {
                string strippedSearch = StripLineNumberPrefixes(search);
                string strippedReplace = HasSuspiciousLineNumberPrefixes(replace)
                    ? StripLineNumberPrefixes(replace)
                    : replace;
                search = strippedSearch;
                replace = strippedReplace;
            }

            if (TryApplySingleHunkThreeTier(working, search, replace, out string updated, out string? error, out string? snippet))
            {
                working = updated;
            }
            else
            {
                RestoreCheckpoint(checkpoint);
                return new StudioWorkspaceOpResult(
                    false,
                    $"patch_script rolled back at hunk[{i}]",
                    error,
                    snippet);
            }
        }

        _logicJs = string.IsNullOrWhiteSpace(working)
            ? null
            : (hadCrlf ? working.Replace("\n", "\r\n", StringComparison.Ordinal) : working);
        _deleteScript = string.IsNullOrWhiteSpace(_logicJs);
        _mutations.Add(new StudioMutationRecord("script_patch", "logic.js", null, null));

        return new StudioWorkspaceOpResult(
            true,
            $"Applied {hunks.Count} script patch hunk(s) to logic.js.",
            TouchedPaths: ["logic.js"]);
    }

    private static bool HasSuspiciousLineNumberPrefixes(string text)
    {
        string[] lines = text.Split('\n');
        int nonEmpty = 0;
        int prefixed = 0;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            nonEmpty++;
            if (LineNumberPrefixRegex().IsMatch(line))
            {
                prefixed++;
            }
        }

        return nonEmpty > 0 && prefixed == nonEmpty;
    }

    private static string StripLineNumberPrefixes(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = LineNumberPrefixRegex().Replace(lines[i], string.Empty, 1);
        }

        return string.Join('\n', lines);
    }

    private static bool TryApplySingleHunkThreeTier(
        string document,
        string search,
        string replace,
        out string updated,
        out string? errorMessage,
        out string? anchorSnippet)
    {
        updated = document;
        errorMessage = null;
        anchorSnippet = null;

        // Tier 1: Exact substring match
        List<int> exactOffsets = FindAllExactOffsets(document, search);
        if (exactOffsets.Count == 1)
        {
            int idx = exactOffsets[0];
            updated = string.Concat(document.AsSpan(0, idx), replace, document.AsSpan(idx + search.Length));
            return true;
        }

        if (exactOffsets.Count > 1)
        {
            string lineList = string.Join(", ", exactOffsets.Take(6).Select(o => $"L{LineNumberAtOffset(document, o)}"));
            errorMessage = $"SEARCH block matched {exactOffsets.Count} locations ({lineList}). Include more surrounding context lines to make it unique.";
            return false;
        }

        string[] docLines = document.Split('\n');
        string[] searchLines = search.Split('\n');
        string[] replaceLines = replace.Split('\n');

        // Tier 2: Relative indentation alignment + re-indent replace lines to match anchor indentation delta
        List<int> tier2Matches = FindRelativeIndentMatches(docLines, searchLines, out int searchBaseIndent);
        if (tier2Matches.Count == 1)
        {
            int startLine = tier2Matches[0];
            int docBaseIndent = ComputeMinimumIndent(docLines, startLine, searchLines.Length);
            int indentDelta = docBaseIndent - searchBaseIndent;
            string[] reindentedReplace = ReindentLines(replaceLines, indentDelta);
            updated = ReplaceLineRange(docLines, startLine, searchLines.Length, reindentedReplace);
            return true;
        }

        if (tier2Matches.Count > 1)
        {
            string lineList = string.Join(", ", tier2Matches.Take(6).Select(static l => $"L{l + 1}"));
            errorMessage = $"SEARCH block matched {tier2Matches.Count} locations after indentation normalization ({lineList}). Include more surrounding context lines.";
            return false;
        }

        // Tier 3: Line-trimmed match (tolerant of flattened internal indentation)
        List<int> tier3Matches = FindLineTrimmedMatches(docLines, searchLines);
        if (tier3Matches.Count == 1)
        {
            int startLine = tier3Matches[0];
            int docBaseIndent = ComputeMinimumIndent(docLines, startLine, searchLines.Length);
            int tier3SearchBase = ComputeMinimumIndent(searchLines, 0, searchLines.Length);
            int tier3ReplaceBase = ComputeMinimumIndent(replaceLines, 0, replaceLines.Length);
            int indentDelta = tier3SearchBase == tier3ReplaceBase ? docBaseIndent - tier3SearchBase : 0;
            string[] adjustedReplace = ReindentLines(replaceLines, indentDelta);
            updated = ReplaceLineRange(docLines, startLine, searchLines.Length, adjustedReplace);
            return true;
        }

        if (tier3Matches.Count > 1)
        {
            string lineList = string.Join(", ", tier3Matches.Take(6).Select(static l => $"L{l + 1}"));
            errorMessage = $"SEARCH block matched {tier3Matches.Count} locations after whitespace trimming ({lineList}). Include more unique surrounding lines.";
            return false;
        }

        // Miss: emit +/- 12 line anchor snippet from best candidate line in document
        int bestLine = FindBestCandidateAnchorLine(docLines, searchLines);
        var snippetSb = new StringBuilder();
        AppendScriptWindow(snippetSb, docLines, bestLine, radius: 12);
        anchorSnippet = snippetSb.ToString().TrimEnd();
        errorMessage = $"SEARCH block not found in logic.js (closest anchor around L{bestLine}). Nearest lines:\n{anchorSnippet}";
        return false;
    }

    private static List<int> FindAllExactOffsets(string document, string search)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(search))
        {
            return matches;
        }

        int pos = 0;
        while (pos <= document.Length - search.Length)
        {
            int found = document.IndexOf(search, pos, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            matches.Add(found);
            pos = found + Math.Max(1, search.Length);
        }

        return matches;
    }

    private static int LineNumberAtOffset(string document, int offset)
    {
        int line = 1;
        int limit = Math.Min(offset, document.Length);
        for (int i = 0; i < limit; i++)
        {
            if (document[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static List<int> FindLineTrimmedMatches(string[] docLines, string[] searchLines)
    {
        var matches = new List<int>();
        if (searchLines.Length == 0 || docLines.Length < searchLines.Length)
        {
            return matches;
        }

        for (int i = 0; i <= docLines.Length - searchLines.Length; i++)
        {
            bool allMatch = true;
            for (int j = 0; j < searchLines.Length; j++)
            {
                if (!string.Equals(docLines[i + j].Trim(), searchLines[j].Trim(), StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    private static List<int> FindRelativeIndentMatches(
        string[] docLines,
        string[] searchLines,
        out int searchBaseIndent)
    {
        var matches = new List<int>();
        searchBaseIndent = ComputeMinimumIndent(searchLines, 0, searchLines.Length);
        if (searchLines.Length == 0 || docLines.Length < searchLines.Length)
        {
            return matches;
        }

        int[] expectedRel = new int[searchLines.Length];
        for (int j = 0; j < searchLines.Length; j++)
        {
            expectedRel[j] = string.IsNullOrWhiteSpace(searchLines[j])
                ? 0
                : Math.Max(0, CountLeadingSpaces(searchLines[j]) - searchBaseIndent);
        }

        for (int i = 0; i <= docLines.Length - searchLines.Length; i++)
        {
            int windowBase = ComputeMinimumIndent(docLines, i, searchLines.Length);
            bool allMatch = true;
            for (int j = 0; j < searchLines.Length; j++)
            {
                string docLine = docLines[i + j];
                string searchLine = searchLines[j];
                if (string.IsNullOrWhiteSpace(searchLine) && string.IsNullOrWhiteSpace(docLine))
                {
                    continue;
                }

                if (!string.Equals(docLine.Trim(), searchLine.Trim(), StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }

                int docRel = Math.Max(0, CountLeadingSpaces(docLine) - windowBase);
                if (docRel != expectedRel[j])
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    private static int ComputeMinimumIndent(string[] lines, int start, int count)
    {
        int min = int.MaxValue;
        int end = Math.Min(lines.Length, start + count);
        for (int i = start; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }
            min = Math.Min(min, CountLeadingSpaces(lines[i]));
        }

        return min == int.MaxValue ? 0 : min;
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ') count++;
            else if (line[i] == '\t') count += 2;
            else break;
        }
        return count;
    }

    private static string[] ReindentLines(string[] lines, int indentDelta)
    {
        if (indentDelta == 0)
        {
            return lines;
        }

        string[] result = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                result[i] = string.Empty;
                continue;
            }

            if (indentDelta > 0)
            {
                result[i] = new string(' ', indentDelta) + line;
            }
            else
            {
                int remove = Math.Min(-indentDelta, CountLeadingSpaces(line));
                int cut = 0;
                int removedWidth = 0;
                while (cut < line.Length && removedWidth < remove)
                {
                    removedWidth += line[cut] == '\t' ? 2 : 1;
                    cut++;
                }
                result[i] = line[cut..];
            }
        }
        return result;
    }

    private static string ReplaceLineRange(string[] docLines, int startLine, int deleteCount, string[] replacementLines)
    {
        var result = new List<string>(docLines.Length - deleteCount + replacementLines.Length);
        for (int i = 0; i < startLine; i++)
        {
            result.Add(docLines[i]);
        }
        result.AddRange(replacementLines);
        for (int i = startLine + deleteCount; i < docLines.Length; i++)
        {
            result.Add(docLines[i]);
        }
        return string.Join('\n', result);
    }

    private static int FindBestCandidateAnchorLine(string[] docLines, string[] searchLines)
    {
        if (docLines.Length == 0)
        {
            return 1;
        }

        List<string> probes = searchLines
            .Select(static l => l.Trim())
            .Where(static l => l.Length >= 4)
            .OrderByDescending(static l => l.Length)
            .Take(3)
            .ToList();

        if (probes.Count == 0)
        {
            return 1;
        }

        int bestIdx = 0;
        int bestScore = 0;
        for (int i = 0; i < docLines.Length; i++)
        {
            string line = docLines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int score = 0;
            foreach (string probe in probes)
            {
                if (string.Equals(line, probe, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }
                else if (line.Contains(probe, StringComparison.OrdinalIgnoreCase) ||
                         probe.Contains(line, StringComparison.OrdinalIgnoreCase))
                {
                    score += Math.Min(line.Length, probe.Length);
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return bestIdx + 1;
    }

    public (string Reply, string ChangeSummary) BuildAutoFinalizedTurnCopy(
        string widgetTitle,
        bool isInitialTurn,
        string? cultureName = null)
    {
        string effectiveCulture = string.IsNullOrWhiteSpace(cultureName)
            ? Localized.ActiveCultureName
            : cultureName;
        bool isChinese = effectiveCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string safeTitle = string.IsNullOrWhiteSpace(widgetTitle) ? "Widget" : widgetTitle.Trim();

        if (isInitialTurn)
        {
            const string createdKey = "CustomWidget.Workshop.CoCreate.AutoSummaryCreated";
            string template = LocalizationService.TranslateForCulture(effectiveCulture, createdKey);
            string createdText = !string.IsNullOrWhiteSpace(template) && !string.Equals(template, createdKey, StringComparison.Ordinal)
                ? string.Format(CultureInfo.InvariantCulture, template, safeTitle)
                : (isChinese ? $"已生成「{safeTitle}」。" : $"Created \"{safeTitle}\".");
            return (createdText, safeTitle);
        }

        bool hasScriptEdits = false;
        bool hasManifestEdits = false;
        var nodeTypes = new List<string>();
        var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (StudioMutationRecord m in _mutations)
        {
            if (m.Kind is "script" or "script_patch" or "delete_script")
            {
                hasScriptEdits = true;
                continue;
            }

            hasManifestEdits = true;
            if (!string.IsNullOrWhiteSpace(m.NodeType))
            {
                string friendly = MapFriendlyNodeLabel(m.NodeType, isChinese);
                if (!string.IsNullOrWhiteSpace(friendly) &&
                    seenTypes.Add(friendly) &&
                    nodeTypes.Count < 3)
                {
                    nodeTypes.Add(friendly);
                }
            }
        }

        string text;
        if (nodeTypes.Count > 0 && hasScriptEdits)
        {
            string joined = string.Join(isChinese ? "、" : ", ", nodeTypes);
            text = string.Format(
                CultureInfo.InvariantCulture,
                LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.CoCreate.AutoSummaryEditNodesAndScript"),
                joined);
        }
        else if (nodeTypes.Count > 0)
        {
            string joined = string.Join(isChinese ? "、" : ", ", nodeTypes);
            text = string.Format(
                CultureInfo.InvariantCulture,
                LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.CoCreate.AutoSummaryEditNodesOnly"),
                joined);
        }
        else if (hasScriptEdits && !hasManifestEdits)
        {
            text = LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.CoCreate.AutoSummaryEditScriptOnly");
        }
        else
        {
            text = LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.CoCreate.AutoSummaryEditGeneric");
        }

        string summary = text.TrimEnd('。', '.');
        return (text, string.IsNullOrWhiteSpace(summary) ? safeTitle : summary);
    }

    private static string MapFriendlyNodeLabel(string nodeType, bool isChinese) =>
        nodeType.Trim().ToLowerInvariant() switch
        {
            "grid" => isChinese ? "网格布局" : "grid layout",
            "itemsgrid" or "list" or "repeater" => isChinese ? "列表区域" : "list view",
            "stack" or "row" or "column" or "wrap" => isChinese ? "布局容器" : "layout",
            "card" or "border" or "panel" => isChinese ? "卡片样式" : "card style",
            "chart" or "sparkline" or "canvas" => isChinese ? "趋势图表" : "Chart",
            "text" => isChinese ? "文案排版" : "Text",
            "markdown" => isChinese ? "富文本" : "Markdown",
            "button" or "iconbutton" or "segmented" or "tabs" => isChinese ? "交互按钮" : "Button",
            "input" or "textbox" or "select" or "slider" or "toggle" => isChinese ? "输入控件" : "Input",
            "badge" or "pill" or "tag" => isChinese ? "状态标签" : "Badge",
            "progress" or "ring" or "gauge" => isChinese ? "进度指示" : "Progress",
            "image" or "icon" => isChinese ? "图标视觉" : "Image",
            _ => isChinese ? "界面模块" : "view section"
        };

    public IReadOnlyList<string> BuildFineGrainedDiffPills(string? cultureName = null)
    {
        if (_mutations.Count == 0)
        {
            return [];
        }

        string effectiveCulture = string.IsNullOrWhiteSpace(cultureName)
            ? Localized.ActiveCultureName
            : cultureName;

        var orderedTypes = new List<string>();
        var byType = new Dictionary<string, (int Count, List<string> Props)>(StringComparer.OrdinalIgnoreCase);

        foreach (StudioMutationRecord m in _mutations)
        {
            if (m.Kind is "script" or "script_patch" or "delete_script" or "bundle" or "replace_manifest" ||
                string.IsNullOrWhiteSpace(m.NodeType))
            {
                continue;
            }

            if (!byType.TryGetValue(m.NodeType, out (int Count, List<string> Props) entry))
            {
                entry = (0, new List<string>(2));
                orderedTypes.Add(m.NodeType);
            }

            if (!string.IsNullOrWhiteSpace(m.PropertyName) &&
                !entry.Props.Contains(m.PropertyName, StringComparer.OrdinalIgnoreCase))
            {
                entry.Props.Add(m.PropertyName);
            }

            byType[m.NodeType] = (entry.Count + 1, entry.Props);
        }

        if (orderedTypes.Count == 0)
        {
            return [];
        }

        var pills = new List<string>(Math.Min(3, orderedTypes.Count));
        foreach (string nodeType in orderedTypes)
        {
            if (pills.Count >= 3)
            {
                break;
            }

            (int count, List<string> props) = byType[nodeType];
            if (count == 1 && props.Count == 1)
            {
                pills.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.Diff.ModifiedNodePropFormat"),
                    nodeType,
                    props[0]));
            }
            else if (count > 1)
            {
                pills.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.Diff.ModifiedNodeCountFormat"),
                    nodeType,
                    count));
            }
            else
            {
                pills.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    LocalizationService.TranslateForCulture(effectiveCulture, "CustomWidget.Workshop.Diff.ModifiedNodeFormat"),
                    nodeType));
            }
        }

        return pills;
    }

    public static bool TrySalvageManifestFromTruncatedWriteBundle(string? rawArgsJson, out string manifestJson)
    {
        manifestJson = string.Empty;
        if (string.IsNullOrWhiteSpace(rawArgsJson))
        {
            return false;
        }

        int i = SkipJsonWhitespace(rawArgsJson, 0);
        if (i >= rawArgsJson.Length || rawArgsJson[i] != '{')
        {
            return false;
        }

        i++;
        while (i < rawArgsJson.Length)
        {
            i = SkipJsonWhitespace(rawArgsJson, i);
            if (i >= rawArgsJson.Length || rawArgsJson[i] == '}')
            {
                break;
            }

            if (rawArgsJson[i] == ',')
            {
                i++;
                continue;
            }

            if (rawArgsJson[i] != '"')
            {
                break;
            }

            if (!TryScanJsonStringEnd(rawArgsJson, i, out int keyEnd))
            {
                break;
            }

            string keyRaw = rawArgsJson[(i + 1)..keyEnd];
            i = SkipJsonWhitespace(rawArgsJson, keyEnd + 1);
            if (i >= rawArgsJson.Length || rawArgsJson[i] != ':')
            {
                break;
            }

            i = SkipJsonWhitespace(rawArgsJson, i + 1);
            if (i >= rawArgsJson.Length)
            {
                break;
            }

            bool isManifestObjectKey = string.Equals(keyRaw, "manifest", StringComparison.Ordinal) ||
                                       string.Equals(keyRaw, "bundle", StringComparison.Ordinal) ||
                                       string.Equals(keyRaw, "root", StringComparison.Ordinal);
            bool isManifestStringKey = string.Equals(keyRaw, "manifestJson", StringComparison.Ordinal) ||
                                       string.Equals(keyRaw, "manifest", StringComparison.Ordinal);

            if (isManifestObjectKey && rawArgsJson[i] == '{')
            {
                if (!TryScanBalancedJsonContainer(rawArgsJson, i, out int objEnd))
                {
                    if (TrySalvageTruncatedJsonObject(rawArgsJson.AsSpan(i), out JsonObject? partialParsed) &&
                        partialParsed is not null)
                    {
                        manifestJson = SerializeNode(partialParsed, indented: true);
                        return true;
                    }
                    return false;
                }

                string candidate = rawArgsJson[i..(objEnd + 1)];
                JsonObject? parsed = ParseManifestObject(candidate);
                if (parsed is not null && parsed["elements"] is JsonObject)
                {
                    manifestJson = SerializeNode(parsed, indented: true);
                    return true;
                }

                i = objEnd + 1;
                continue;
            }

            if (isManifestStringKey && rawArgsJson[i] == '"')
            {
                if (!TryScanJsonStringEnd(rawArgsJson, i, out int strEnd))
                {
                    return false;
                }

                string literal = rawArgsJson[i..(strEnd + 1)];
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(literal, PermissiveDocOptions);
                    string? unescaped = doc.RootElement.GetString();
                    JsonObject? parsed = ParseManifestObject(unescaped);
                    if (parsed is not null && parsed["elements"] is JsonObject)
                    {
                        manifestJson = SerializeNode(parsed, indented: true);
                        return true;
                    }
                }
                catch
                {
                }

                i = strEnd + 1;
                continue;
            }

            if (!TrySkipJsonValue(rawArgsJson, i, out int nextIdx))
            {
                break;
            }

            i = nextIdx;
        }

        JsonObject? directParsed = ParseManifestObject(rawArgsJson);
        if (directParsed is not null && directParsed["elements"] is JsonObject)
        {
            manifestJson = SerializeNode(directParsed, indented: true);
            return true;
        }

        return false;
    }

    private static bool TrySalvageTruncatedJsonObject(ReadOnlySpan<char> span, out JsonObject? salvagedObj)
    {
        salvagedObj = null;
        if (span.IsEmpty || span[0] != '{')
        {
            return false;
        }

        var stack = new List<char>(32);
        var structuralCheckpoints = new List<(int CutLength, char[] OpenStack)>(64);
        var commaCheckpoints = new List<(int CutLength, char[] OpenStack)>(64);
        bool inString = false;
        bool escaped = false;
        char lastNonWs = '\0';

        for (int idx = 0; idx < span.Length; idx++)
        {
            char c = span[idx];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                    lastNonWs = '"';
                }
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c is '{' or '[')
            {
                stack.Add(c);
                lastNonWs = c;
                continue;
            }

            if (c == ',')
            {
                if (stack.Count >= 1 && lastNonWs is not '{' and not '[' and not ':' and not ',')
                {
                    commaCheckpoints.Add((idx, stack.ToArray()));
                }
                lastNonWs = ',';
                continue;
            }

            if (c is '}' or ']')
            {
                char expected = c == '}' ? '{' : '[';
                if (stack.Count == 0 || stack[^1] != expected)
                {
                    break;
                }

                stack.RemoveAt(stack.Count - 1);
                lastNonWs = c;
                structuralCheckpoints.Add((idx + 1, stack.ToArray()));
                if (stack.Count == 0)
                {
                    break;
                }
                continue;
            }

            lastNonWs = c;
        }

        static bool HasSubstantiveViewContent(CustomWidgetNodeSpec view)
        {
            bool isPureContainer = view.Type is "Stack" or "Grid" or "ZStack" or "Wrap" or "Card" or "Scroll";
            if (!isPureContainer)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(view.Text) ||
                !string.IsNullOrWhiteSpace(view.Title) ||
                !string.IsNullOrWhiteSpace(view.Items) ||
                !string.IsNullOrWhiteSpace(view.Collection) ||
                !string.IsNullOrWhiteSpace(view.ElementsSource) ||
                (view.Elements is { Count: > 0 }))
            {
                return true;
            }

            for (int c = 0; c < view.Children.Count; c++)
            {
                if (HasSubstantiveViewContent(view.Children[c]))
                {
                    return true;
                }
            }

            return false;
        }

        static JsonObject? CloseCheckpoint(
            ReadOnlySpan<char> source,
            (int CutLength, char[] OpenStack) checkpoint)
        {
            var sb = new StringBuilder(checkpoint.CutLength + checkpoint.OpenStack.Length);
            sb.Append(source[..checkpoint.CutLength]);
            for (int s = checkpoint.OpenStack.Length - 1; s >= 0; s--)
            {
                sb.Append(checkpoint.OpenStack[s] == '{' ? '}' : ']');
            }

            return ParseManifestObject(sb.ToString());
        }

        static void PruneUnreachableFlatElements(JsonObject manifestObj)
        {
            if (manifestObj["elements"] is not JsonObject elementsObj || elementsObj.Count == 0)
            {
                return;
            }

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            void Enqueue(string? id)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    string trimmedId = id.Trim();
                    if (elementsObj.ContainsKey(trimmedId) && reachable.Add(trimmedId))
                    {
                        queue.Enqueue(trimmedId);
                    }
                }
            }

            if (manifestObj["root"] is JsonValue rootVal && rootVal.TryGetValue(out string? rootId))
            {
                Enqueue(rootId);
            }

            if (manifestObj["popovers"] is JsonObject popoversObj)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in popoversObj)
                {
                    if (kv.Value is JsonObject popObj &&
                        popObj["root"] is JsonValue popRootVal &&
                        popRootVal.TryGetValue(out string? popRootId))
                    {
                        Enqueue(popRootId);
                    }
                }
            }

            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();
                if (elementsObj[currentId] is not JsonObject elemObj)
                {
                    continue;
                }

                if (elemObj["children"] is JsonArray childrenArr)
                {
                    for (int i = 0; i < childrenArr.Count; i++)
                    {
                        if (childrenArr[i] is JsonValue cv && cv.TryGetValue(out string? cid))
                        {
                            Enqueue(cid);
                        }
                    }
                }

                if (elemObj["hoverActions"] is JsonArray hoverArr)
                {
                    for (int i = 0; i < hoverArr.Count; i++)
                    {
                        if (hoverArr[i] is JsonValue hv && hv.TryGetValue(out string? hid))
                        {
                            Enqueue(hid);
                        }
                    }
                }

                foreach (string refKey in (ReadOnlySpan<string>)["itemTemplate", "pane", "actionButton", "child"])
                {
                    if (elemObj[refKey] is JsonValue rv && rv.TryGetValue(out string? rid))
                    {
                        Enqueue(rid);
                    }
                }
            }

            var orphanKeys = new List<string>();
            foreach (KeyValuePair<string, JsonNode?> kv in elementsObj)
            {
                if (!reachable.Contains(kv.Key))
                {
                    orphanKeys.Add(kv.Key);
                }
            }

            foreach (string key in orphanKeys)
            {
                elementsObj.Remove(key);
            }
        }

        static (JsonObject? CleanObj, JsonObject? FallbackObj, int CleanCutLength) EvaluateViewCheckpoints(
            ReadOnlySpan<char> source,
            List<(int CutLength, char[] OpenStack)> checkpoints)
        {
            JsonObject? fallbackWithView = null;
            int minIndex = Math.Max(0, checkpoints.Count - 20);
            for (int k = checkpoints.Count - 1; k >= minIndex; k--)
            {
                JsonObject? parsed = CloseCheckpoint(source, checkpoints[k]);
                if (parsed is null || parsed["elements"] is not JsonObject)
                {
                    continue;
                }

                PruneUnreachableFlatElements(parsed);
                fallbackWithView ??= parsed;
                CustomWidgetParseResult parseRes = CustomWidgetManifestParser.Parse(SerializeNode(parsed, indented: false));
                if (parseRes.Manifest is not null &&
                    !parseRes.Diagnostics.Any(static d => d.IsError) &&
                    HasSubstantiveViewContent(parseRes.Manifest.View))
                {
                    return (parsed, fallbackWithView, checkpoints[k].CutLength);
                }
            }

            return (null, fallbackWithView, -1);
        }

        static (JsonObject? SkeletonObj, int CutLength) EvaluateSkeletonCheckpoints(
            ReadOnlySpan<char> source,
            List<(int CutLength, char[] OpenStack)> checkpoints)
        {
            int minIndex = Math.Max(0, checkpoints.Count - 20);
            for (int k = checkpoints.Count - 1; k >= minIndex; k--)
            {
                JsonObject? parsed = CloseCheckpoint(source, checkpoints[k]);
                if (parsed is null)
                {
                    continue;
                }

                string? id = parsed["id"] is JsonValue idVal && idVal.TryGetValue<string>(out string? idStr) ? idStr : null;
                string? name = parsed["name"] is JsonValue nameVal && nameVal.TryGetValue<string>(out string? nameStr) ? nameStr : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                parsed["schemaVersion"] = JsonValue.Create(1);
                parsed.Remove("body");
                parsed.Remove("view");
                parsed["root"] = JsonValue.Create("main");
                parsed["elements"] = new JsonObject
                {
                    ["main"] = new JsonObject
                    {
                        ["type"] = JsonValue.Create("Stack"),
                        ["spacing"] = JsonValue.Create(8),
                        ["children"] = new JsonArray((JsonNode?)JsonValue.Create("skeleton_placeholder"))
                    },
                    ["skeleton_placeholder"] = new JsonObject
                    {
                        ["type"] = JsonValue.Create("Text"),
                        ["text"] = JsonValue.Create("...")
                    }
                };

                CustomWidgetParseResult parseRes = CustomWidgetManifestParser.Parse(SerializeNode(parsed, indented: false));
                if (parseRes.Manifest is null || parseRes.Diagnostics.Any(static d => d.IsError))
                {
                    parsed.Remove("popovers");
                    parsed.Remove("capsule");
                    parseRes = CustomWidgetManifestParser.Parse(SerializeNode(parsed, indented: false));
                }

                if (parseRes.Manifest is not null && !parseRes.Diagnostics.Any(static d => d.IsError))
                {
                    return (parsed, checkpoints[k].CutLength);
                }
            }

            return (null, -1);
        }

        (JsonObject? sClean, JsonObject? sFallback, int _) = EvaluateViewCheckpoints(span, structuralCheckpoints);
        if (sClean is not null)
        {
            salvagedObj = sClean;
            return true;
        }

        (JsonObject? cClean, JsonObject? cFallback, int _) = EvaluateViewCheckpoints(span, commaCheckpoints);
        if (cClean is not null)
        {
            salvagedObj = cClean;
            return true;
        }

        (JsonObject? sSkel, int sSkelCut) = EvaluateSkeletonCheckpoints(span, structuralCheckpoints);
        (JsonObject? cSkel, int cSkelCut) = EvaluateSkeletonCheckpoints(span, commaCheckpoints);
        salvagedObj = sSkelCut >= cSkelCut ? (sSkel ?? cSkel) : (cSkel ?? sSkel);
        salvagedObj ??= sFallback ?? cFallback;
        return salvagedObj is not null;
    }

    private static bool TryTrimLastSelectorSegment(string path, out string parentPath)
    {
        parentPath = "$";
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "$", StringComparison.Ordinal))
        {
            return false;
        }

        if (path[^1] == ']')
        {
            int openBracket = path.LastIndexOf('[');
            if (openBracket <= 0)
            {
                return false;
            }

            parentPath = path[..openBracket];
            return parentPath.Length >= 1;
        }

        int lastDot = path.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        parentPath = lastDot == 0 ? "$" : path[..lastDot];
        return true;
    }

    private static int SkipJsonWhitespace(string json, int idx)
    {
        while (idx < json.Length && char.IsWhiteSpace(json[idx]))
        {
            idx++;
        }

        return idx;
    }

    private static bool TryScanJsonStringEnd(string json, int quoteStart, out int quoteEnd)
    {
        quoteEnd = -1;
        bool escaped = false;
        for (int i = quoteStart + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                quoteEnd = i;
                return true;
            }
        }

        return false;
    }

    private static bool TryScanBalancedJsonContainer(string json, int openIdx, out int closeIdx)
    {
        closeIdx = -1;
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = openIdx; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    closeIdx = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySkipJsonValue(string json, int startIdx, out int nextIdx)
    {
        nextIdx = startIdx;
        if (startIdx >= json.Length)
        {
            return false;
        }

        char c = json[startIdx];
        if (c == '"')
        {
            if (!TryScanJsonStringEnd(json, startIdx, out int end))
            {
                return false;
            }

            nextIdx = end + 1;
            return true;
        }

        if (c is '{' or '[')
        {
            if (!TryScanBalancedJsonContainer(json, startIdx, out int end))
            {
                return false;
            }

            nextIdx = end + 1;
            return true;
        }

        int p = startIdx;
        while (p < json.Length && json[p] is not ',' and not '}' and not ']' && !char.IsWhiteSpace(json[p]))
        {
            p++;
        }

        nextIdx = p;
        return p > startIdx;
    }

    public bool NormalizeDomInPlace()
    {
        if (_manifestRoot is null)
        {
            return false;
        }

        bool changed = false;

        if (!_manifestRoot.ContainsKey("schemaVersion"))
        {
            _manifestRoot["schemaVersion"] = JsonValue.Create(1);
            changed = true;
        }

        if (_manifestRoot["capabilities"] is JsonArray capArr)
        {
            var seenCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = capArr.Count - 1; i >= 0; i--)
            {
                string? cap = capArr[i]?.GetValue<string?>()?.Trim();
                if (string.IsNullOrWhiteSpace(cap) || !seenCaps.Add(cap))
                {
                    capArr.RemoveAt(i);
                    changed = true;
                }
            }
        }

        changed |= ExtractAndStripStrayBundleFieldsInPlace(allowOverwriteExistingLogic: false, out _);
        changed |= ExtractInlineCollectionSeedDataInPlace();
        changed |= NormalizeSettingsInPlace(_manifestRoot);
        changed |= NormalizeUnicodeGlyphPropertyInPlace(_manifestRoot, "icon");

        if (_manifestRoot["capsule"] is JsonObject capObj)
        {
            if (capObj.TryGetPropertyValue("lines", out JsonNode? capLinesNode))
            {
                if (!capObj.ContainsKey("summary") && capLinesNode is JsonArray linesArr)
                {
                    var joinedLines = new List<string>(linesArr.Count);
                    foreach (JsonNode? lineNode in linesArr)
                    {
                        if (lineNode is JsonValue lv)
                        {
                            string? lineStr = lv.TryGetValue(out string? directStr)
                                ? directStr
                                : (lv.TryGetValue(out JsonElement je) && je.ValueKind == JsonValueKind.String ? je.GetString() : null);
                            if (!string.IsNullOrWhiteSpace(lineStr))
                            {
                                joinedLines.Add(lineStr.Trim());
                            }
                        }
                    }

                    if (joinedLines.Count > 0)
                    {
                        capObj["summary"] = JsonValue.Create(string.Join(" · ", joinedLines));
                    }
                }

                capObj.Remove("lines");
                changed = true;
            }

            if (!capObj.ContainsKey("summary"))
            {
                foreach (string alias in new[] { "subtitle", "secondary", "primary", "text", "description", "hint" })
                {
                    if (capObj.TryGetPropertyValue(alias, out JsonNode? capSubNode) &&
                        capSubNode is not null)
                    {
                        capObj["summary"] = capSubNode.DeepClone();
                        capObj.Remove(alias);
                        changed = true;
                        break;
                    }
                }
            }

            if (capObj.Remove("hint"))
            {
                changed = true;
            }

            if (!capObj.ContainsKey("badge"))
            {
                foreach (string badgeAlias in new[] { "meta", "primary" })
                {
                    if (capObj.TryGetPropertyValue(badgeAlias, out JsonNode? badgeNode) &&
                        badgeNode is not null)
                    {
                        capObj["badge"] = badgeNode.DeepClone();
                        capObj.Remove(badgeAlias);
                        changed = true;
                        break;
                    }
                }
            }

            if (!capObj.ContainsKey("glyph") &&
                capObj.TryGetPropertyValue("icon", out JsonNode? capIconNode) &&
                capIconNode is not null)
            {
                capObj["glyph"] = capIconNode.DeepClone();
                capObj.Remove("icon");
                changed = true;
            }

            changed |= NormalizeUnicodeGlyphPropertyInPlace(capObj, "glyph");
            if (capObj["primaryAction"] is JsonObject primActObj)
            {
                changed |= NormalizeUnicodeGlyphPropertyInPlace(primActObj, "icon");
                if (primActObj.TryGetPropertyValue("onClick", out JsonNode? capClick) &&
                    capClick is not null)
                {
                    changed |= NormalizeActionTreeInPlace(capClick);
                }
            }
        }

        if (_manifestRoot["popovers"] is JsonObject popMap)
        {
            var strayPopScalarKeys = new List<string>();
            JsonObject? solePopoverEntry = null;
            int popObjCount = 0;
            foreach (KeyValuePair<string, JsonNode?> kv in popMap)
            {
                if (kv.Value is JsonObject pObj)
                {
                    popObjCount++;
                    solePopoverEntry = pObj;
                }
                else
                {
                    strayPopScalarKeys.Add(kv.Key);
                }
            }

            if (strayPopScalarKeys.Count > 0)
            {
                foreach (string strayKey in strayPopScalarKeys)
                {
                    if (popObjCount == 1 &&
                        solePopoverEntry is not null &&
                        strayKey is "title" or "width" or "height" or "placement" &&
                        !solePopoverEntry.ContainsKey(strayKey) &&
                        popMap[strayKey] is { } strayVal)
                    {
                        solePopoverEntry[strayKey] = strayVal.DeepClone();
                    }
                    popMap.Remove(strayKey);
                    changed = true;
                }
            }
        }

        if (_manifestRoot["elements"] is JsonObject flatElementsObj)
        {
            var visitedElemIds = new HashSet<string>(StringComparer.Ordinal);
            string? flatRootId = _manifestRoot["root"]?.GetValue<string?>()?.Trim();
            if (!string.IsNullOrWhiteSpace(flatRootId))
            {
                changed |= NormalizeFlatElementGraphSubtree(
                    flatElementsObj,
                    flatRootId,
                    visitedElemIds,
                    isRootView: true,
                    cardAncestorDepth: 0,
                    directParentIsSingleChildCard: false,
                    _lockedCanonicalPath,
                    _baselineManifestSnapshot);
            }

            if (_manifestRoot["popovers"] is JsonObject flatPopMap)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in flatPopMap)
                {
                    if (kv.Value is JsonObject popObj &&
                        popObj["root"] is JsonValue popRootVal &&
                        popRootVal.TryGetValue(out string? popRootId) &&
                        !string.IsNullOrWhiteSpace(popRootId))
                    {
                        changed |= NormalizeFlatElementGraphSubtree(
                            flatElementsObj,
                            popRootId.Trim(),
                            visitedElemIds,
                            isRootView: true,
                            cardAncestorDepth: 0,
                            directParentIsSingleChildCard: false,
                            _lockedCanonicalPath,
                            _baselineManifestSnapshot);
                    }
                }
            }

            // Normalize any remaining unvisited elements (e.g. detached templates or shared nodes)
            foreach (string elemKey in flatElementsObj.Select(static kv => kv.Key).ToList())
            {
                if (!visitedElemIds.Contains(elemKey))
                {
                    changed |= NormalizeFlatElementGraphSubtree(
                        flatElementsObj,
                        elemKey,
                        visitedElemIds,
                        isRootView: false,
                        cardAncestorDepth: 0,
                        directParentIsSingleChildCard: false,
                        _lockedCanonicalPath,
                        _baselineManifestSnapshot);
                }
            }

            changed |= AlignTabsInitialStateInPlace(_manifestRoot, flatElementsObj);
        }

        if ((!_manifestRoot.ContainsKey("capsule") || _manifestRoot["capsule"] is null) &&
            _manifestRoot["elements"] is JsonObject elemsForCap &&
            _manifestRoot["root"]?.GetValue<string?>() is { Length: > 0 } capRootId &&
            SynthesizeSafeCapsuleFallback(_manifestRoot, elemsForCap, capRootId.Trim()) is JsonObject synthesizedCapsule)
        {
            _manifestRoot["capsule"] = synthesizedCapsule;
            changed = true;
        }

        if (!_deleteScript && !string.IsNullOrWhiteSpace(_logicJs))
        {
            string? promotedJs = PromoteMissingTopLevelScriptExports(_manifestRoot, _logicJs, out bool jsChanged);
            if (jsChanged && promotedJs is not null)
            {
                _logicJs = promotedJs;
                changed = true;
            }
        }

        return changed;
    }

    private bool ExtractAndStripStrayBundleFieldsInPlace(bool allowOverwriteExistingLogic, out bool promotedScript)
    {
        promotedScript = false;
        if (_manifestRoot is null)
        {
            return false;
        }

        bool changed = false;
        string? inlineLogic = null;
        foreach (string scriptKey in (ReadOnlySpan<string>)["logicJs", "scriptContent"])
        {
            if (_manifestRoot.TryGetPropertyValue(scriptKey, out JsonNode? scriptNode))
            {
                if (inlineLogic is null &&
                    scriptNode is JsonValue scriptVal &&
                    scriptVal.TryGetValue<string>(out string? rawJs) &&
                    !string.IsNullOrWhiteSpace(rawJs))
                {
                    inlineLogic = rawJs;
                }

                _manifestRoot.Remove(scriptKey);
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(inlineLogic) &&
            !_deleteScript &&
            (allowOverwriteExistingLogic || string.IsNullOrWhiteSpace(_logicJs)))
        {
            _logicJs = NormalizeNewlines(inlineLogic);
            promotedScript = true;
        }

        JsonObject? extractedSample = null;
        foreach (string sampleKey in (ReadOnlySpan<string>)["sampleData", "sampleDataJson"])
        {
            if (_manifestRoot.TryGetPropertyValue(sampleKey, out JsonNode? sampleNode))
            {
                if (extractedSample is null)
                {
                    if (sampleNode is JsonObject sampleObj && sampleObj.Count > 0)
                    {
                        extractedSample = sampleObj.DeepClone() as JsonObject;
                    }
                    else if (sampleNode is JsonValue sampleVal &&
                             sampleVal.TryGetValue<string>(out string? sampleStr) &&
                             !string.IsNullOrWhiteSpace(sampleStr))
                    {
                        JsonObject? parsedSample = ParseManifestObject(sampleStr);
                        if (parsedSample is { Count: > 0 })
                        {
                            extractedSample = parsedSample;
                        }
                    }
                }

                _manifestRoot.Remove(sampleKey);
                changed = true;
            }
        }

        if (extractedSample is { Count: > 0 } &&
            string.IsNullOrWhiteSpace(ExtractedInlineSampleDataJson))
        {
            ExtractedInlineSampleDataJson = SerializeNode(extractedSample, indented: true);
        }

        return changed;
    }

    private bool ExtractInlineCollectionSeedDataInPlace()
    {
        if (_manifestRoot?["collections"] is not JsonObject colsObj)
        {
            return false;
        }

        bool changed = false;
        Dictionary<string, JsonArray>? newlyExtracted = null;
        foreach (string key in colsObj.Select(static kv => kv.Key).ToList())
        {
            JsonNode? entryNode = colsObj[key];
            if (entryNode is JsonArray seedArr)
            {
                var seedForManifest = new JsonArray();
                var seedForSample = new JsonArray();
                foreach (JsonNode? item in seedArr)
                {
                    if (item is JsonObject obj)
                    {
                        seedForManifest.Add(obj.DeepClone());
                        seedForSample.Add(obj.DeepClone());
                    }
                }

                var normalizedColDef = new JsonObject();
                if (seedForManifest.Count > 0)
                {
                    normalizedColDef["seedItems"] = seedForManifest;
                    newlyExtracted ??= new Dictionary<string, JsonArray>(StringComparer.Ordinal);
                    newlyExtracted[key] = seedForSample;
                }

                colsObj[key] = normalizedColDef;
                changed = true;
            }
            else if (entryNode is JsonObject colDefObj)
            {
                foreach (string aliasKey in (ReadOnlySpan<string>)["items", "records", "initialItems"])
                {
                    if (colDefObj[aliasKey] is JsonArray aliasArr)
                    {
                        if (colDefObj["seedItems"] is not JsonArray)
                        {
                            var migratedSeeds = new JsonArray();
                            foreach (JsonNode? item in aliasArr)
                            {
                                if (item is JsonObject obj)
                                {
                                    migratedSeeds.Add(obj.DeepClone());
                                }
                            }

                            if (migratedSeeds.Count > 0)
                            {
                                colDefObj["seedItems"] = migratedSeeds;
                            }
                        }

                        colDefObj.Remove(aliasKey);
                        changed = true;
                    }
                }

                if (colDefObj["seedItems"] is JsonArray existingSeeds && existingSeeds.Count > 0)
                {
                    var sampleClone = new JsonArray();
                    foreach (JsonNode? item in existingSeeds)
                    {
                        if (item is JsonObject obj)
                        {
                            sampleClone.Add(obj.DeepClone());
                        }
                    }

                    if (sampleClone.Count > 0)
                    {
                        newlyExtracted ??= new Dictionary<string, JsonArray>(StringComparer.Ordinal);
                        newlyExtracted[key] = sampleClone;
                    }
                }
            }
        }

        if (newlyExtracted is { Count: > 0 })
        {
            JsonObject sampleRoot = !string.IsNullOrWhiteSpace(ExtractedInlineSampleDataJson)
                ? (ParseManifestObject(ExtractedInlineSampleDataJson) ?? new JsonObject())
                : new JsonObject();
            if (sampleRoot["collections"] is not JsonObject sampleCols)
            {
                sampleCols = new JsonObject();
                sampleRoot["collections"] = sampleCols;
            }

            foreach ((string colKey, JsonArray rows) in newlyExtracted)
            {
                if (sampleCols[colKey] is not JsonArray existingSampleRows || existingSampleRows.Count == 0)
                {
                    sampleCols[colKey] = rows;
                }
            }

            ExtractedInlineSampleDataJson = SerializeNode(sampleRoot, indented: true);
        }

        return changed;
    }

    private static bool NormalizeFlatElementGraphSubtree(
        JsonObject elementsMap,
        string elemId,
        HashSet<string> visitedElemIds,
        bool isRootView,
        int cardAncestorDepth,
        bool directParentIsSingleChildCard,
        string? lockedCanonicalPath,
        JsonObject? baselineRoot)
    {
        if (string.IsNullOrWhiteSpace(elemId) ||
            !visitedElemIds.Add(elemId) ||
            elementsMap[elemId] is not JsonObject elemObj)
        {
            return false;
        }

        bool changed = NormalizeLayoutNodeInPlace(
            elemObj,
            elemId,
            elementsMap,
            currentPath: $"$.elements.{elemId}",
            isRootView: isRootView,
            cardAncestorDepth: cardAncestorDepth,
            directParentIsSingleChildCard: directParentIsSingleChildCard,
            lockedCanonicalPath: lockedCanonicalPath,
            baselineRoot: baselineRoot,
            out string? effectiveNodeType,
            out bool isPlainCardShell);

        int nextCardDepth = string.Equals(effectiveNodeType, "Card", StringComparison.OrdinalIgnoreCase)
            ? cardAncestorDepth + 1
            : cardAncestorDepth;

        if (elemObj["children"] is JsonArray childIds)
        {
            bool childSingleWrapper = isPlainCardShell && childIds.Count == 1;
            var snapshotChildIds = new List<string>(childIds.Count);
            foreach (JsonNode? c in childIds)
            {
                if (c is JsonValue cv && cv.TryGetValue(out string? cid) && !string.IsNullOrWhiteSpace(cid))
                {
                    snapshotChildIds.Add(cid.Trim());
                }
            }

            foreach (string cid in snapshotChildIds)
            {
                changed |= NormalizeFlatElementGraphSubtree(
                    elementsMap,
                    cid,
                    visitedElemIds,
                    isRootView: false,
                    cardAncestorDepth: nextCardDepth,
                    directParentIsSingleChildCard: childSingleWrapper,
                    lockedCanonicalPath: lockedCanonicalPath,
                    baselineRoot: baselineRoot);
            }
        }

        if (elemObj["itemTemplate"] is JsonValue itVal &&
            itVal.TryGetValue(out string? itemTemplateId) &&
            !string.IsNullOrWhiteSpace(itemTemplateId))
        {
            changed |= NormalizeFlatElementGraphSubtree(
                elementsMap,
                itemTemplateId.Trim(),
                visitedElemIds,
                isRootView: false,
                cardAncestorDepth: 0,
                directParentIsSingleChildCard: false,
                lockedCanonicalPath: lockedCanonicalPath,
                baselineRoot: baselineRoot);
        }

        foreach (string refKey in (ReadOnlySpan<string>)["child", "pane", "actionButton"])
        {
            if (elemObj[refKey] is JsonValue rv &&
                rv.TryGetValue(out string? refId) &&
                !string.IsNullOrWhiteSpace(refId))
            {
                changed |= NormalizeFlatElementGraphSubtree(
                    elementsMap,
                    refId.Trim(),
                    visitedElemIds,
                    isRootView: false,
                    cardAncestorDepth: nextCardDepth,
                    directParentIsSingleChildCard: false,
                    lockedCanonicalPath: lockedCanonicalPath,
                    baselineRoot: baselineRoot);
            }
        }

        if (elemObj["hoverActions"] is JsonArray hoverArr)
        {
            var hoverIds = new List<string>(hoverArr.Count);
            foreach (JsonNode? h in hoverArr)
            {
                if (h is JsonValue hv && hv.TryGetValue(out string? hid) && !string.IsNullOrWhiteSpace(hid))
                {
                    hoverIds.Add(hid.Trim());
                }
            }

            foreach (string hid in hoverIds)
            {
                changed |= NormalizeFlatElementGraphSubtree(
                    elementsMap,
                    hid,
                    visitedElemIds,
                    isRootView: false,
                    cardAncestorDepth: nextCardDepth,
                    directParentIsSingleChildCard: false,
                    lockedCanonicalPath: lockedCanonicalPath,
                    baselineRoot: baselineRoot);
            }
        }

        return changed;
    }

    public static string? ExtractCleanNodeSelector(string? rawTargetNodePath)
    {
        if (string.IsNullOrWhiteSpace(rawTargetNodePath))
        {
            return null;
        }

        string trimmed = rawTargetNodePath.Trim();
        int linkedIdx = trimmed.IndexOf(" (linked script bindings:", StringComparison.OrdinalIgnoreCase);
        if (linkedIdx >= 0)
        {
            trimmed = trimmed[..linkedIdx].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

}
