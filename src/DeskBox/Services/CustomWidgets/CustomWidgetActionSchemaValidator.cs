using System.Collections.Frozen;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Single-Source-of-Truth validator for runtime actions — especially dynamic action objects
/// emitted via <c>ctx.dispatch(...)</c> inside <c>logic.js</c> during dry-run smoke verification.
/// Catches mismatched field shapes (e.g. object <c>onComplete</c>), unsupported callbacks,
/// invalid <c>popoverId</c>, unsafe file paths, forbidden URL schemes, and unknown action types.
/// </summary>
public static class CustomWidgetActionSchemaValidator
{
    private static readonly FrozenSet<string> AsyncCallbackCapableActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file.writeText",
            "file.listDir",
            "file.manage",
            "file.pick",
            "file.transform",
            "http.request",
            "ai.run",
            "command.run",
            "clipboard.captureImage"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> PathBearingFileActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file.writeText",
            "file.reveal",
            "file.open",
            "file.listDir",
            "file.manage",
            "file.transform"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static void ValidateRawDispatchedActionsJson(
        string? rawDispatchedJson,
        string sourceHandlerName,
        CustomWidgetManifest manifest,
        IReadOnlySet<string> exportedScriptFunctions,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(rawDispatchedJson))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawDispatchedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int idx = 0;
            foreach (JsonElement actionEl in doc.RootElement.EnumerateArray())
            {
                ValidateSingleRawDispatchedAction(
                    actionEl,
                    $"logic.js:{sourceHandlerName}() -> ctx.dispatch[{idx}]",
                    manifest,
                    exportedScriptFunctions,
                    diagnostics);
                idx++;
            }
        }
        catch
        {
        }
    }

    private static void ValidateSingleRawDispatchedAction(
        JsonElement el,
        string diagPath,
        CustomWidgetManifest manifest,
        IReadOnlySet<string> exportedScriptFunctions,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(
                diagPath,
                "ctx.dispatch() items must be action JSON objects with a \"type\" string.",
                IsError: true,
                Code: "ScriptDispatchInvalidShape"));
            return;
        }

        string actionType = el.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? (typeEl.GetString() ?? string.Empty).Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(actionType))
        {
            diagnostics.Add(new(
                diagPath,
                "Dispatched action is missing required string property \"type\".",
                IsError: true,
                Code: "ScriptDispatchMissingType"));
            return;
        }

        if (!CustomWidgetManifestParser.IsSupportedActionType(actionType))
        {
            diagnostics.Add(new(
                diagPath,
                $"Dispatched action type '{actionType}' is not supported by the DeskBox host runtime.",
                IsError: true,
                Code: "ScriptDispatchUnknownActionType"));
            return;
        }

        // 1. Validate onComplete / onProgress shapes and targets
        ValidateCallbackProperty(el, "onComplete", actionType, diagPath, exportedScriptFunctions, diagnostics);
        ValidateCallbackProperty(el, "onProgress", actionType, diagPath, exportedScriptFunctions, diagnostics);

        // 2. Popover target validation
        if (string.Equals(actionType, "popover.open", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actionType, "popover.toggle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actionType, "popover.close", StringComparison.OrdinalIgnoreCase))
        {
            string? popId = TryGetStringProp(el, "popoverId") ?? TryGetStringProp(el, "targetId") ?? TryGetStringProp(el, "target");
            if (string.IsNullOrWhiteSpace(popId))
            {
                if (!string.Equals(actionType, "popover.close", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new(
                        diagPath,
                        $"Action '{actionType}' requires a non-empty \"popoverId\" matching a declared root popover.",
                        IsError: true,
                        Code: "ScriptDispatchMissingPopoverId"));
                }
            }
            else if (!manifest.Popovers.Any(p => string.Equals(p.PopoverId ?? p.Id, popId, StringComparison.OrdinalIgnoreCase)))
            {
                string declared = manifest.Popovers.Count > 0
                    ? string.Join(", ", manifest.Popovers.Select(static p => p.PopoverId ?? p.Id))
                    : "(none)";
                diagnostics.Add(new(
                    diagPath,
                    $"Action '{actionType}' targets popoverId '{popId}', which is not declared in root \"popovers\" (declared: {declared}).",
                    IsError: true,
                    Code: "ScriptDispatchUnknownPopoverId"));
            }
        }

        // 3. url.open protocol check (blocks file:///, javascript:, etc.)
        if (string.Equals(actionType, "url.open", StringComparison.OrdinalIgnoreCase))
        {
            string? rawUrl = TryGetStringProp(el, "url") ?? TryGetStringProp(el, "value");
            if (!string.IsNullOrWhiteSpace(rawUrl) &&
                !rawUrl.Contains("${", StringComparison.Ordinal) &&
                (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                diagnostics.Add(new(
                    diagPath,
                    $"Action 'url.open' only supports http:// or https:// URLs (got '{rawUrl}'). To open local files or folders, use 'file.reveal' or 'launcher.open' instead.",
                    IsError: true,
                    Code: "ScriptDispatchInvalidUrlScheme"));
            }
        }

        // 4. File action path validation (catches path traversal, UNC, and protected OS paths early)
        if (PathBearingFileActions.Contains(actionType))
        {
            string? rawPath = TryGetStringProp(el, "path");
            if (string.IsNullOrWhiteSpace(rawPath) &&
                string.Equals(actionType, "file.writeText", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(
                    diagPath,
                    "Action 'file.writeText' requires a non-empty \"path\" string (e.g. 'export.md', 'Desktop\\\\export.md', or a full path).",
                    IsError: true,
                    Code: "ScriptDispatchMissingFilePath"));
            }
            else if (!string.IsNullOrWhiteSpace(rawPath) && !rawPath.Contains("${", StringComparison.Ordinal))
            {
                string slug = string.IsNullOrWhiteSpace(manifest.Id) ? "widget" : manifest.Id;
                bool forWrite = string.Equals(actionType, "file.writeText", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(actionType, "file.manage", StringComparison.OrdinalIgnoreCase);
                if (!CustomWidgetLocalFileEngine.TryResolveWidgetFilePath(
                        rawPath,
                        slug,
                        forWrite,
                        out _,
                        out _,
                        out string? pathError))
                {
                    diagnostics.Add(new(
                        diagPath,
                        $"Action '{actionType}' dispatched an invalid path '{rawPath}': {pathError}",
                        IsError: true,
                        Code: "ScriptDispatchInvalidFilePath"));
                }
            }
        }

        // 5. Collection mutation target check
        if (actionType.StartsWith("collection.", StringComparison.OrdinalIgnoreCase))
        {
            string? colName = TryGetStringProp(el, "collection") ?? TryGetStringProp(el, "targetCollection");
            if (!string.IsNullOrWhiteSpace(colName) &&
                !manifest.Collections.ContainsKey(colName))
            {
                diagnostics.Add(new(
                    diagPath,
                    $"Action '{actionType}' targets undeclared collection '{colName}'. Declare it under root \"collections\".",
                    IsError: true,
                    Code: "ScriptDispatchUnknownCollection"));
            }
        }

        // 6. script.run export target check
        if (string.Equals(actionType, "script.run", StringComparison.OrdinalIgnoreCase))
        {
            string? fn = TryGetStringProp(el, "fn");
            if (!string.IsNullOrWhiteSpace(fn) && !exportedScriptFunctions.Contains(fn))
            {
                diagnostics.Add(new(
                    diagPath,
                    $"Dispatched 'script.run' targets '{fn}', which is not exported by logic.js.",
                    IsError: true,
                    Code: "ScriptDispatchMissingExport"));
            }
        }
    }

    private static void ValidateCallbackProperty(
        JsonElement el,
        string propName,
        string actionType,
        string diagPath,
        IReadOnlySet<string> exportedScriptFunctions,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!el.TryGetProperty(propName, out JsonElement cbEl) ||
            cbEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (cbEl.ValueKind != JsonValueKind.String)
        {
            diagnostics.Add(new(
                $"{diagPath}.{propName}",
                $"Property '{propName}' on action '{actionType}' MUST be an exported function name string (e.g. \"{propName}\": \"onDone\"), NOT a {cbEl.ValueKind} object.",
                IsError: true,
                Code: "ScriptDispatchInvalidCallbackShape"));
            return;
        }

        string fnName = (cbEl.GetString() ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(fnName))
        {
            return;
        }

        if (!AsyncCallbackCapableActions.Contains(actionType))
        {
            diagnostics.Add(new(
                $"{diagPath}.{propName}",
                $"Action '{actionType}' does not support '{propName}' callbacks. Async callbacks are supported on: {string.Join(", ", AsyncCallbackCapableActions)}.",
                IsError: true,
                Code: "ScriptDispatchUnsupportedCallback"));
            return;
        }

        if (!exportedScriptFunctions.Contains(fnName))
        {
            diagnostics.Add(new(
                $"{diagPath}.{propName}",
                $"Callback '{propName}: \"{fnName}\"' on '{actionType}' is not exported by logic.js. Export 'export function {fnName}(ctx, res)'.",
                IsError: true,
                Code: "ScriptDispatchMissingCallbackExport"));
        }
    }

    private static string? TryGetStringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
}
