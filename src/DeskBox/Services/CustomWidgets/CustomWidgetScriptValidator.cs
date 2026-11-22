using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetWorkspaceValidationResult(
    CustomWidgetParseResult ManifestParseResult,
    IReadOnlyList<CustomWidgetValidationDiagnostic> Diagnostics,
    CustomWidgetCompiledScript? CompiledScript)
{
    public CustomWidgetManifest? Manifest => ManifestParseResult.Manifest;

    public bool IsValid => Manifest is not null && !Diagnostics.Any(static d => d.IsError);

    public CustomWidgetParseResult ToParseResult() => new(Manifest, Diagnostics);

    public static implicit operator CustomWidgetParseResult(CustomWidgetWorkspaceValidationResult result) =>
        result.ToParseResult();
}

/// <summary>
/// Validates a Custom Widget workspace bundle (<c>manifest.json</c> + optional <c>logic.js</c>
/// + optional <c>locales/&lt;culture&gt;.json</c> catalog), verifying QuickJS ES2023 module syntax with exact line
/// numbers, <c>script.run</c> exported function targets, <c>$computed.*</c> cold-start safety,
/// and a bounded 100ms <c>onInit(ctx)</c> dry-run before rendering or committing.
/// </summary>
public static partial class CustomWidgetScriptValidator
{
    public const int MaxLogicJsBytes = CustomWidgetDataStore.MaxLogicJsBytes;
    public const int MaxSingleLocaleBytes = CustomWidgetLocaleCatalog.MaxSingleLocaleBytes;
    public const int MaxLocaleCatalogBytes = CustomWidgetLocaleCatalog.MaxTotalCatalogBytes;

    [GeneratedRegex(@"\$i18n\.([A-Za-z_][A-Za-z0-9_.-]*)|\bt\(\s*['""]([A-Za-z0-9_.-]+)['""]", RegexOptions.CultureInvariant)]
    private static partial Regex I18nTokenRegex();

    [GeneratedRegex(@"\$?computed\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ComputedKeyRegex();

    /// <param name="strictAuthoring">
    /// <see langword="true"/> for authoring gates (Studio AI turns, compiler pipeline): promotes
    /// <see cref="CustomWidgetStrictAuthoringRules"/> warnings to errors. Keep <see langword="false"/> for
    /// runtime loading, catalog installs and package imports so published widgets stay compatible.
    /// </param>
    public static CustomWidgetWorkspaceValidationResult ValidateWorkspaceBundle(
        string manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        bool runOnInitDryRun = true,
        string? sampleDataJson = null,
        bool strictAuthoring = false)
    {
        CustomWidgetWorkspaceValidationResult result = ValidateWorkspaceBundleCore(
            manifestJson,
            logicJs,
            locales,
            runOnInitDryRun,
            sampleDataJson);
        return strictAuthoring
            ? result with { Diagnostics = CustomWidgetStrictAuthoringRules.PromoteToErrors(result.Diagnostics) }
            : result;
    }

    private static CustomWidgetWorkspaceValidationResult ValidateWorkspaceBundleCore(
        string manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        bool runOnInitDryRun,
        string? sampleDataJson)
    {
        CustomWidgetParseResult parseResult = CustomWidgetManifestParser.ValidateWithRuntimeHints(
            manifestJson,
            logicJs,
            locales);

        var diagnostics = new List<CustomWidgetValidationDiagnostic>(parseResult.Diagnostics);
        ValidateLocaleCatalog(locales, diagnostics);

        CustomWidgetManifest? manifest = parseResult.Manifest;
        if (manifest is null)
        {
            return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, null);
        }

        ValidatePopoverTargets(manifest, logicJs, diagnostics);

        if (locales is not { HasAnyLocale: true } &&
            (manifestJson.Contains("$i18n.", StringComparison.Ordinal) ||
             manifestJson.Contains("t(", StringComparison.Ordinal)) &&
            ManifestReferencesI18n(manifest, out string firstI18nKey, out string firstI18nPath))
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                firstI18nPath,
                $"Manifest references i18n key '{firstI18nKey}', but locales/<culture>.json is missing or empty. Provide locales/*.json or replace '$i18n.*' with literal text.",
                IsError: true,
                Code: "MissingLocaleCatalog"));
        }

        var scriptRunCalls = new List<(string ActionType, string Fn, string PropName, string Path)>();
        bool referencesComputed = ManifestReferencesComputed(manifest, scriptRunCalls);
        bool declaresScript = manifest.Script is not null;
        bool hasLogicJs = !string.IsNullOrWhiteSpace(logicJs);

        if (!hasLogicJs)
        {
            if (scriptRunCalls.Count > 0)
            {
                (string actType, string fn, string propName, string path) = scriptRunCalls[0];
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"{path}.{propName}",
                    $"Manifest invokes '{actType}' ('{fn}'), but logic.js is missing or empty. Provide an ES2023 module exporting '{fn}(ctx)'.",
                    IsError: true,
                    Code: "MissingLogicJs"));
            }
            else if (declaresScript || referencesComputed)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    declaresScript ? "$.script" : "$.view",
                    "Manifest declares \"script\" or binds '$computed.*', but logic.js is missing or empty. Provide a valid ES2023 logic.js module or remove script/$computed references.",
                    IsError: true,
                    Code: "MissingLogicJs"));
            }
            else
            {
                ValidateDeclarativeDynamicGridDryRun(manifest, locales, sampleDataJson, diagnostics);
            }

            return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, null);
        }

        int scriptBytes = Encoding.UTF8.GetByteCount(logicJs!);
        if (scriptBytes > CustomWidgetDataStore.MaxLogicJsBytes)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "logic.js",
                $"logic.js size ({scriptBytes} bytes) exceeds the maximum budget of {CustomWidgetDataStore.MaxLogicJsBytes} bytes (128 KB).",
                IsError: true,
                Code: "ScriptTooLarge"));
            return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, null);
        }

        CustomWidgetScriptCompileResult compileRes = CustomWidgetScriptEngine.Compile(logicJs!);
        if (compileRes.Status == CustomWidgetScriptStatus.EngineUnavailable)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "logic.js",
                $"QuickJS runtime engine (deskbox_qjs.dll) is unavailable in the current host environment: {compileRes.ErrorMessage}",
                IsError: true,
                Code: "ScriptEngineUnavailable"));
            return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, null);
        }

        if (!compileRes.IsSuccess || compileRes.Compiled is null)
        {
            int line = compileRes.ErrorLine ?? 0;
            string lineSuffix = line > 0 ? $":{line}" : string.Empty;
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                $"logic.js{lineSuffix}",
                $"logic.js syntax error{(line > 0 ? $" at line {line}" : string.Empty)}: {compileRes.ErrorMessage}",
                IsError: true,
                Code: "ScriptSyntaxError"));
            return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, null);
        }

        CustomWidgetCompiledScript compiled = compileRes.Compiled;
        string availableExports = compiled.ExportedFunctions.Count > 0
            ? string.Join(", ", compiled.ExportedFunctions)
            : "(none)";

        foreach ((string actType, string fn, string propName, string path) in scriptRunCalls)
        {
            if (string.IsNullOrWhiteSpace(fn))
            {
                continue;
            }

            if (!compiled.ExportedFunctions.Contains(fn))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"{path}.{propName}",
                    $"Action '{actType}' targets '{fn}', which is not exported by logic.js. Export 'export function {fn}(ctx)' in logic.js (currently exported: {availableExports}).",
                    IsError: true,
                    Code: "UnknownScriptFunction"));
            }
        }

        int tickIntervalMs = manifest.Script?.TickIntervalMs ?? 0;
        if (compiled.ExportedFunctions.Contains("onTick") && tickIntervalMs <= 0)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "$.script.tickIntervalMs",
                "logic.js exports 'onTick(ctx)', but $.script.tickIntervalMs is 0 (disabled). Set \"tickIntervalMs\": 1000 (or >= 1000) in $.script if periodic ticks are needed.",
                IsError: false,
                Code: "TickIntervalZero"));
        }

        if (referencesComputed && !compiled.ExportedFunctions.Contains("onInit"))
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "logic.js",
                "Manifest binds '$computed.*', but logic.js does not export 'onInit(ctx)'. Export 'onInit(ctx)' to populate initial ctx.computed values on cold start.",
                IsError: false,
                Code: "MissingOnInitForComputed"));
        }

        ValidateScriptApiSemantics(logicJs!, diagnostics);
        ValidateScriptActionArgsAlignment(manifest, compiled, logicJs!, diagnostics);

        if (runOnInitDryRun)
        {
            if (compiled.ExportedFunctions.Contains("onInit"))
            {
                RunOnInitDryRun(manifest, locales, compiled, logicJs!, sampleDataJson, diagnostics);
            }
            else if (scriptRunCalls.Count > 0)
            {
                RunStandaloneScriptHandlerProbe(manifest, locales, compiled, logicJs!, sampleDataJson, diagnostics);
            }
        }

        return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, compiled);
    }

    public static IReadOnlyList<CustomWidgetValidationDiagnostic> ValidateRawLocaleDirectoryFiles(
        string localesDirectory)
    {
        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        ValidateRawLocaleDirectoryFiles(localesDirectory, diagnostics);
        return diagnostics;
    }

    public static void ValidateRawLocaleDirectoryFiles(
        string localesDirectory,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(localesDirectory) || !Directory.Exists(localesDirectory))
        {
            return;
        }

        int totalBytes = 0;
        foreach (string file in Directory.EnumerateFiles(localesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.'))
            {
                continue;
            }

            string relPath = $"locales/{fileName}";
            FileInfo info = new(file);
            if (info.Length > MaxSingleLocaleBytes)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    relPath,
                    $"{relPath} size ({info.Length} bytes) exceeds the {MaxSingleLocaleBytes / 1024} KB per-locale limit.",
                    IsError: true,
                    Code: "LocaleFileTooLarge"));
                continue;
            }

            totalBytes += (int)info.Length;
            if (totalBytes > MaxLocaleCatalogBytes)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    "locales",
                    $"Total locales/ size exceeds the {MaxLocaleCatalogBytes / 1024} KB maximum budget.",
                    IsError: true,
                    Code: "LocaleCatalogTooLarge"));
                break;
            }

            try
            {
                string raw = File.ReadAllText(file, Encoding.UTF8);
                using JsonDocument doc = JsonDocument.Parse(
                    raw,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        relPath,
                        $"{relPath} root must be a JSON object.",
                        IsError: true,
                        Code: "InvalidLocaleRoot"));
                }
            }
            catch (JsonException ex)
            {
                long line = (ex.LineNumber ?? 0) + 1;
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"{relPath}:{line}",
                    $"{relPath} syntax error at line {line}: {ex.Message}",
                    IsError: true,
                    Code: "LocaleSyntaxError"));
            }
        }
    }

    private static void ValidateLocaleCatalog(
        CustomWidgetLocaleCatalog? locales,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (locales is null || locales.IsEmpty)
        {
            return;
        }

        int totalBytes = 0;
        foreach ((string code, IReadOnlyDictionary<string, string> dict) in locales.Locales)
        {
            if (!CustomWidgetLocaleCatalog.IsSafeLocaleCode(code))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"locales/{code}.json",
                    $"Invalid locale code '{code}'.",
                    IsError: true,
                    Code: "InvalidLocaleCode"));
                continue;
            }

            string json = CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(dict);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaxSingleLocaleBytes)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"locales/{code}.json",
                    $"locales/{code}.json size ({byteCount} bytes) exceeds the {MaxSingleLocaleBytes / 1024} KB per-locale limit.",
                    IsError: true,
                    Code: "LocaleFileTooLarge"));
            }

            totalBytes += byteCount;
        }

        if (totalBytes > MaxLocaleCatalogBytes)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "locales",
                $"Total locales/ size ({totalBytes} bytes) exceeds the {MaxLocaleCatalogBytes / 1024} KB maximum budget.",
                IsError: true,
                Code: "LocaleCatalogTooLarge"));
        }
    }

    private static void RunOnInitDryRun(
        CustomWidgetManifest manifest,
        CustomWidgetLocaleCatalog? locales,
        CustomWidgetCompiledScript compiled,
        string logicJs,
        string? sampleDataJson,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var state = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal);
        var stateJson = new Dictionary<string, JsonElement>(manifest.StateJson, StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in manifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(def.Key) &&
                !string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                settings[def.Key] = def.DefaultValue ?? string.Empty;
            }
        }

        var emptyCollections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        foreach (string colName in manifest.Collections.Keys)
        {
            emptyCollections[colName] = [];
        }

        CustomWidgetManifest effectiveManifest = locales is { IsEmpty: false }
            ? manifest.WithLocales(locales)
            : manifest;

        IReadOnlyDictionary<string, string> i18nTable =
            effectiveManifest.ResolveI18nDictionary("zh-CN");

        const int DualPassTimeoutMs = 120;

        // Pass A: Cold Empty Store (catches unguarded empty-array / div-by-zero crashes on first launch)
        CustomWidgetScriptExecutionResult passA = CustomWidgetScriptEngine.Execute(
            compiled,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                State = state,
                StateJson = stateJson,
                Settings = settings,
                Collections = emptyCollections,
                Computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                I18n = i18nTable,
                CultureName = "zh-CN",
                CallKind = CustomWidgetScriptCallKind.Lifecycle,
                TimeoutMs = DualPassTimeoutMs
            });

        if (!passA.IsSuccess)
        {
            int line = passA.ErrorLine ?? 0;
            string lineSuffix = line > 0 ? $":{line}" : string.Empty;
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                $"logic.js{lineSuffix}",
                $"logic.js 'onInit' failed Pass A cold empty dry-run{(line > 0 ? $" at line {line}" : string.Empty)}: {passA.ErrorMessage}",
                IsError: true,
                Code: "ScriptOnInitRuntimeError"));
            return;
        }

        // Pass B: Hydrated String-Coerced Store (catches item.field numeric assumptions on collection strings)
        CustomWidgetScriptExecutionResult activeDryRun = passA;
        Dictionary<string, string> evalState = new(state, StringComparer.Ordinal);
        Dictionary<string, JsonElement> evalStateJson = new(stateJson, StringComparer.Ordinal);
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> evalCollections = emptyCollections;

        bool shouldRunPassB = manifest.Collections.Count > 0 || !string.IsNullOrWhiteSpace(sampleDataJson);
        if (shouldRunPassB)
        {
            CustomWidgetSampleDataHydrator.TryHydrate(
                manifest,
                sampleDataJson,
                logicJs,
                out Dictionary<string, string> bState,
                out Dictionary<string, JsonElement> bStateJson,
                out Dictionary<string, List<CustomWidgetRecord>> bCollections,
                out _);

            CustomWidgetScriptExecutionResult passB = CustomWidgetScriptEngine.Execute(
                compiled,
                "onInit",
                new CustomWidgetScriptExecutionInput
                {
                    State = bState,
                    StateJson = bStateJson,
                    Settings = settings,
                    Collections = bCollections,
                    Computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                    I18n = i18nTable,
                    CultureName = "zh-CN",
                    CallKind = CustomWidgetScriptCallKind.Lifecycle,
                    TimeoutMs = DualPassTimeoutMs
                });

            if (!passB.IsSuccess)
            {
                int line = passB.ErrorLine ?? 0;
                string lineSuffix = line > 0 ? $":{line}" : string.Empty;
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"logic.js{lineSuffix}",
                    $"logic.js 'onInit' failed Pass B sampleData dry-run{(line > 0 ? $" at line {line}" : string.Empty)}: {passB.ErrorMessage}",
                    IsError: false,
                    Code: "ScriptSampleDataRuntimeError"));
            }
            else
            {
                activeDryRun = passB;
                evalState = bState;
                evalStateJson = bStateJson;
                evalCollections = bCollections;

                if (bCollections.Values.Any(static l => l.Count > 0) &&
                    !string.IsNullOrWhiteSpace(logicJs) &&
                    ScriptReadsCollections(logicJs) &&
                    passB.Computed.Count > 0 &&
                    AreComputedMapsZeroEffectMatch(passA.Computed, passB.Computed))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        "logic.js",
                        "logic.js 'onInit' produced zero/empty $computed values identical to an empty run even though sampleData collections are non-empty (check date filter against current date or collection field names).",
                        IsError: false,
                        Code: "ScriptSampleDataZeroEffect"));
                }
            }
        }

        // Non-finite numbers (NaN / Infinity) in ctx.state serialize to JsonValueKind.Null via cloneJson
        foreach ((string sk, JsonElement svEl) in activeDryRun.StateSetJson)
        {
            if (svEl.ValueKind == JsonValueKind.Null)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    "logic.js",
                    $"logic.js 'onInit' wrote non-finite number (NaN/Infinity) into ctx.state.{sk}; initialize with finite numbers (use '|| 0' guards).",
                    IsError: false,
                    Code: "ScriptNonFiniteStateValue"));
            }
        }

        var referencedComputedKeys = new HashSet<string>(StringComparer.Ordinal);
        ManifestReferencesComputed(effectiveManifest, [], referencedComputedKeys);
        foreach (string key in referencedComputedKeys)
        {
            if (!activeDryRun.Computed.TryGetValue(key, out JsonElement compEl) ||
                compEl.ValueKind == JsonValueKind.Null ||
                compEl.ValueKind == JsonValueKind.Undefined)
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    "logic.js",
                    $"logic.js 'onInit' left computed key '{key}' uninitialized or null; the widget shows a blank/broken placeholder.",
                    IsError: false,
                    Code: "ScriptComputedUninitialized"));
            }
        }

        foreach (string removedKey in activeDryRun.StateRemoved)
        {
            evalState.Remove(removedKey);
            evalStateJson.Remove(removedKey);
        }

        foreach ((string key, string val) in activeDryRun.StateSet)
        {
            evalState[key] = val;
        }

        foreach ((string key, JsonElement el) in activeDryRun.StateSetJson)
        {
            evalStateJson[key] = el.Clone();
        }

        var evalCtx = new CustomWidgetEvaluationContext
        {
            State = evalState,
            StateJson = evalStateJson,
            Settings = settings,
            Collections = evalCollections,
            Computed = activeDryRun.Computed,
            I18n = i18nTable,
            CultureName = "zh-CN"
        };

        ValidateDynamicGridAndItemsGridDryRun(effectiveManifest, evalCtx, diagnostics);
        ScanDirtyEvaluatedInterpolations(effectiveManifest, evalCtx, diagnostics);

        var coldState = new Dictionary<string, string>(state, StringComparer.Ordinal);
        var coldStateJson = new Dictionary<string, JsonElement>(stateJson, StringComparer.Ordinal);
        foreach (string rk in passA.StateRemoved)
        {
            coldState.Remove(rk);
            coldStateJson.Remove(rk);
        }
        foreach ((string k, string v) in passA.StateSet)
        {
            coldState[k] = v;
        }
        foreach ((string k, JsonElement el) in passA.StateSetJson)
        {
            coldStateJson[k] = el.Clone();
        }
        var coldCtx = new CustomWidgetEvaluationContext
        {
            State = coldState,
            StateJson = coldStateJson,
            Settings = settings,
            Collections = emptyCollections,
            Computed = passA.Computed,
            I18n = i18nTable,
            CultureName = "zh-CN"
        };
        ValidateColdStartLoadingAndSampleCoherence(
            effectiveManifest,
            coldCtx,
            evalCtx,
            hasExplicitSampleData: !string.IsNullOrWhiteSpace(sampleDataJson),
            diagnostics);

        // Apply dirty collections produced by onInit before probing script.run UI handlers
        var handlerBaseCollections = CloneCollectionsMap(evalCollections);
        foreach ((string dirtyCol, List<CustomWidgetRecord> dirtyRecs) in activeDryRun.DirtyCollections)
        {
            handlerBaseCollections[dirtyCol] = dirtyRecs.Select(static r => r.Clone()).ToList();
        }

        ProbeExportedScriptHandlers(
            effectiveManifest,
            compiled,
            evalState,
            evalStateJson,
            settings,
            handlerBaseCollections,
            activeDryRun.Computed,
            i18nTable,
            diagnostics);

        foreach (CustomWidgetActionSpec dispatched in activeDryRun.DispatchedActions)
        {
            if (string.IsNullOrWhiteSpace(dispatched.Type))
            {
                continue;
            }

            if (!CustomWidgetManifestParser.IsSupportedActionType(dispatched.Type) &&
                !diagnostics.Any(d => string.Equals(d.Code, "UnknownDispatchedActionType", StringComparison.Ordinal) &&
                                      d.Message.Contains($"'{dispatched.Type}'", StringComparison.Ordinal)))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    "logic.js",
                    BuildUnknownDispatchActionMessage(dispatched.Type, line: null),
                    IsError: true,
                    Code: "UnknownDispatchedActionType"));
                continue;
            }

            if (string.Equals(dispatched.Type, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dispatched.Type, "popover.toggle", StringComparison.OrdinalIgnoreCase))
            {
                string target = (dispatched.PopoverId ?? dispatched.TargetId ?? dispatched.Key ?? string.Empty).Trim();
                if (!IsSpecialOrDynamicPopoverId(target))
                {
                    HashSet<string> declaredPopoverIds = CollectDeclaredPopoverIds(effectiveManifest);
                    if (!declaredPopoverIds.Contains(target) &&
                        !diagnostics.Any(d => string.Equals(d.Code, "UnknownPopoverTarget", StringComparison.Ordinal) &&
                                              d.Message.Contains($"'{target}'", StringComparison.Ordinal)))
                    {
                        string availableList = declaredPopoverIds.Count > 0
                            ? string.Join(", ", declaredPopoverIds.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase))
                            : "(none)";
                        diagnostics.Add(new CustomWidgetValidationDiagnostic(
                            "logic.js",
                            $"Popover target '{target}' is not declared in root \"popovers\" (registered popovers: {availableList}). Declare popovers at top-level `\"popovers\": [{{ \"id\": \"{target}\", \"title\": \"...\", \"children\": [...] }}]` (or as a map), not inside `view.children`.",
                            IsError: true,
                            Code: "UnknownPopoverTarget"));
                    }
                }
            }
        }
    }

    private static void ValidateDeclarativeDynamicGridDryRun(
        CustomWidgetManifest manifest,
        CustomWidgetLocaleCatalog? locales,
        string? sampleDataJson,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        CustomWidgetSampleDataHydrator.TryHydrate(
            manifest,
            sampleDataJson,
            null,
            out Dictionary<string, string> state,
            out Dictionary<string, JsonElement> stateJson,
            out Dictionary<string, List<CustomWidgetRecord>> collections,
            out _);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in manifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(def.Key) &&
                !string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                settings[def.Key] = def.DefaultValue ?? string.Empty;
            }
        }

        CustomWidgetManifest effectiveManifest = locales is { IsEmpty: false }
            ? manifest.WithLocales(locales)
            : manifest;

        var evalCtx = new CustomWidgetEvaluationContext
        {
            State = state,
            StateJson = stateJson,
            Settings = settings,
            Collections = collections,
            I18n = effectiveManifest.ResolveI18nDictionary("zh-CN"),
            CultureName = "zh-CN"
        };

        ValidateDynamicGridAndItemsGridDryRun(effectiveManifest, evalCtx, diagnostics);
        ScanDirtyEvaluatedInterpolations(effectiveManifest, evalCtx, diagnostics);

        var coldCollections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        foreach ((string colKey, _) in manifest.Collections)
        {
            coldCollections[colKey] = [];
        }
        var coldCtx = new CustomWidgetEvaluationContext
        {
            State = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal),
            StateJson = manifest.StateJson.ToDictionary(static kv => kv.Key, static kv => kv.Value.Clone(), StringComparer.Ordinal),
            Settings = settings,
            Collections = coldCollections,
            I18n = evalCtx.I18n,
            CultureName = "zh-CN"
        };
        ValidateColdStartLoadingAndSampleCoherence(
            effectiveManifest,
            coldCtx,
            evalCtx,
            hasExplicitSampleData: !string.IsNullOrWhiteSpace(sampleDataJson),
            diagnostics);
    }

    private static void ValidateColdStartLoadingAndSampleCoherence(
        CustomWidgetManifest manifest,
        CustomWidgetEvaluationContext coldCtx,
        CustomWidgetEvaluationContext sampleOrLiveCtx,
        bool hasExplicitSampleData,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (manifest.View is null)
        {
            return;
        }

        bool hasConditionalLoadingRing = false;
        var coldEmptyNestedStateRefs = new List<string>();
        var sampleEmptyNestedStateRefs = new List<string>();

        void Walk(CustomWidgetNodeSpec node, bool coldVisible, bool sampleVisible, bool hasConditionalAncestor)
        {
            bool curColdVisible = coldVisible &&
                (string.IsNullOrWhiteSpace(node.When) || CustomWidgetExpressionEvaluator.EvaluateCondition(node.When, coldCtx));
            bool curSampleVisible = sampleVisible &&
                (string.IsNullOrWhiteSpace(node.When) || CustomWidgetExpressionEvaluator.EvaluateCondition(node.When, sampleOrLiveCtx));
            bool curHasCondition = hasConditionalAncestor || !string.IsNullOrWhiteSpace(node.When);

            if (curColdVisible &&
                curHasCondition &&
                string.Equals(node.Type, "ProgressRing", StringComparison.OrdinalIgnoreCase))
            {
                hasConditionalLoadingRing = true;
            }

            if ((string.Equals(node.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "Badge", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(node.Text))
            {
                foreach (Match m in NestedStateFieldBindingRegex().Matches(node.Text))
                {
                    string slotKey = m.Groups[1].Value;
                    string fullRef = $"state.{slotKey}.{m.Groups[2].Value}";
                    if (curColdVisible &&
                        IsEmptyNestedStateSlot(coldCtx, slotKey, fullRef) &&
                        !coldEmptyNestedStateRefs.Contains(fullRef, StringComparer.Ordinal))
                    {
                        coldEmptyNestedStateRefs.Add(fullRef);
                    }

                    if (hasExplicitSampleData &&
                        curSampleVisible &&
                        IsEmptyNestedStateSlot(sampleOrLiveCtx, slotKey, fullRef) &&
                        !sampleEmptyNestedStateRefs.Contains(fullRef, StringComparer.Ordinal))
                    {
                        sampleEmptyNestedStateRefs.Add(fullRef);
                    }
                }
            }

            // Do not walk into List/ItemsGrid itemTemplate where $item.* is bound per record.
            foreach (CustomWidgetNodeSpec child in node.Children)
            {
                Walk(child, curColdVisible, curSampleVisible, curHasCondition);
            }
        }

        Walk(manifest.View, coldVisible: true, sampleVisible: true, hasConditionalAncestor: false);

        if (hasConditionalLoadingRing && coldEmptyNestedStateRefs.Count > 0)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "$.view",
                $"Cold-start split state: a loading ProgressRing is visible on initial load while unguarded data cards bind to empty state objects ({string.Join(", ", coldEmptyNestedStateRefs.Take(3))}). Gate data cards behind a matching 'when' condition (e.g. '${{state.loaded == true}}'), populate initial values, or render items via a single 'List' bound to a collection with 'emptyState'.",
                IsError: false,
                Code: "view.cold_start_split_state"));
        }

        if (hasExplicitSampleData &&
            sampleOrLiveCtx.Collections.Values.Any(static l => l.Count > 0) &&
            sampleEmptyNestedStateRefs.Count > 0)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                "sampleData",
                $"sampleData populated collections but left visible nested state bindings ({string.Join(", ", sampleEmptyNestedStateRefs.Take(3))}) empty in preview. Include matching \"state\" values in sampleData or bind cards directly to the collection via 'List'.",
                IsError: false,
                Code: "view.sample_data_incomplete_state"));
        }
    }

    private static bool IsEmptyNestedStateSlot(
        CustomWidgetEvaluationContext ctx,
        string slotKey,
        string fullToken)
    {
        if (!ctx.StateJson.TryGetValue(slotKey, out JsonElement slotEl) ||
            slotEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string evaluated = CustomWidgetExpressionEvaluator.Interpolate($"${{{fullToken}}}", ctx);
        return string.IsNullOrWhiteSpace(evaluated);
    }

    [GeneratedRegex(@"\$\{\s*\$?state\.([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_.]*)\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex NestedStateFieldBindingRegex();

    private static bool ScriptReadsCollections(string logicJs) =>
        logicJs.Contains("collections.get", StringComparison.Ordinal) ||
        logicJs.Contains("collections?.get", StringComparison.Ordinal) ||
        logicJs.Contains("collections.count", StringComparison.Ordinal) ||
        logicJs.Contains("collections?.count", StringComparison.Ordinal) ||
        logicJs.Contains("collections.all", StringComparison.Ordinal) ||
        logicJs.Contains("collections[", StringComparison.Ordinal);

    private static void RunStandaloneScriptHandlerProbe(
        CustomWidgetManifest manifest,
        CustomWidgetLocaleCatalog? locales,
        CustomWidgetCompiledScript compiled,
        string logicJs,
        string? sampleDataJson,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        CustomWidgetSampleDataHydrator.TryHydrate(
            manifest,
            sampleDataJson,
            logicJs,
            out Dictionary<string, string> state,
            out Dictionary<string, JsonElement> stateJson,
            out Dictionary<string, List<CustomWidgetRecord>> collections,
            out _);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in manifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(def.Key) &&
                !string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                settings[def.Key] = def.DefaultValue ?? string.Empty;
            }
        }

        CustomWidgetManifest effectiveManifest = locales is { IsEmpty: false }
            ? manifest.WithLocales(locales)
            : manifest;
        IReadOnlyDictionary<string, string> i18nTable = effectiveManifest.ResolveI18nDictionary("zh-CN");

        ProbeExportedScriptHandlers(
            effectiveManifest,
            compiled,
            state,
            stateJson,
            settings,
            collections,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            i18nTable,
            diagnostics);
    }

    private static void ProbeExportedScriptHandlers(
        CustomWidgetManifest manifest,
        CustomWidgetCompiledScript compiled,
        IReadOnlyDictionary<string, string> baseState,
        IReadOnlyDictionary<string, JsonElement> baseStateJson,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> baseCollections,
        IReadOnlyDictionary<string, JsonElement> baseComputed,
        IReadOnlyDictionary<string, string> i18nTable,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var probes = new List<(CustomWidgetActionSpec Action, string Path, string? EnclosingItemsSource)>();

        void CollectFromActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path, string? itemsSource)
        {
            if (actions is null || actions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Fn))
                {
                    probes.Add((act, actPath, itemsSource));
                }

                if (act.Then is { Count: > 0 })
                {
                    CollectFromActions(act.Then, $"{actPath}.then", itemsSource);
                }
            }
        }

        void WalkNode(CustomWidgetNodeSpec? node, string nodePath, string? activeItemsSource)
        {
            if (node is null)
            {
                return;
            }

            string? rowItemsSource = !string.IsNullOrWhiteSpace(node.Items)
                ? node.Items
                : !string.IsNullOrWhiteSpace(node.Collection)
                    ? node.Collection
                    : activeItemsSource;

            CollectFromActions(node.OnClick, $"{nodePath}.onClick", activeItemsSource);
            CollectFromActions(node.OnSubmit, $"{nodePath}.onSubmit", activeItemsSource);
            CollectFromActions(node.OnChange, $"{nodePath}.onChange", activeItemsSource);
            CollectFromActions(node.OnDropActions, $"{nodePath}.onDrop", activeItemsSource);
            CollectFromActions(node.OnRowClick, $"{nodePath}.onRowClick", rowItemsSource);
            CollectFromActions(node.OnItemClick, $"{nodePath}.onItemClick", rowItemsSource);
            CollectFromActions(node.OnReorder, $"{nodePath}.onReorder", rowItemsSource);
            CollectFromActions(node.OnInlineCommit, $"{nodePath}.onInlineCommit", rowItemsSource);

            WalkNode(node.ItemTemplate, $"{nodePath}.itemTemplate", rowItemsSource);
            WalkNode(node.ActionButton, $"{nodePath}.actionButton", activeItemsSource);
            WalkNode(node.Pane, $"{nodePath}.pane", activeItemsSource);

            for (int i = 0; i < node.MenuItems.Count; i++)
            {
                CollectFromActions(node.MenuItems[i].OnClick, $"{nodePath}.menuItems[{i}].onClick", activeItemsSource);
            }

            for (int i = 0; i < node.ContextMenu.Count; i++)
            {
                CollectFromActions(node.ContextMenu[i].OnClick, $"{nodePath}.contextMenu[{i}].onClick", activeItemsSource);
            }

            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                WalkNode(node.HoverActions[i], $"{nodePath}.hoverActions[{i}]", activeItemsSource);
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                CustomWidgetNodeSpec child = node.Children[i];
                string childPath = !string.IsNullOrWhiteSpace(child.NodePath) ? child.NodePath : $"{nodePath}.children[{i}]";
                WalkNode(child, childPath, activeItemsSource);
            }
        }

        WalkNode(manifest.View, "$.view", null);
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popPath = !string.IsNullOrWhiteSpace(pop.NodePath) ? pop.NodePath : $"$.popovers[{i}]";
            WalkNode(pop, popPath, null);
        }

        if (manifest.Capsule?.PrimaryAction is { } pa)
        {
            CollectFromActions(pa.OnClick, "$.capsule.primaryAction.onClick", null);
        }

        if (probes.Count == 0)
        {
            return;
        }

        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        var failedFunctions = new HashSet<string>(StringComparer.Ordinal);
        int executed = 0;
        const int MaxHandlerProbes = 16;
        const uint HandlerProbeTimeoutMs = 120;

        foreach ((CustomWidgetActionSpec action, string actionPath, string? itemsSource) in probes)
        {
            if (executed >= MaxHandlerProbes)
            {
                break;
            }

            string fn = action.Fn!.Trim();
            if (!compiled.ExportedFunctions.Contains(fn) ||
                failedFunctions.Contains(fn) ||
                fn is "onInit" or "onTick" or "onSourceData" or "onHttpResponse" or "onCommandResult" or "onAiResult")
            {
                continue;
            }

            CustomWidgetRecord? sampleItem = ResolveSampleRecordForItemsSource(itemsSource, baseCollections, baseComputed);
            var evalCtx = new CustomWidgetEvaluationContext
            {
                State = baseState,
                StateJson = baseStateJson,
                Settings = settings,
                Collections = baseCollections,
                Computed = baseComputed,
                I18n = i18nTable,
                CultureName = "zh-CN",
                CurrentItem = sampleItem,
                CurrentIndex = 0,
                EventValue = "1",
                EventText = "sample",
                EventId = sampleItem?.Id ?? "item-1",
                EventFromIndex = 0,
                EventToIndex = 0
            };

            string evalArg = action.Arg is not null
                ? CustomWidgetExpressionEvaluator.Interpolate(action.Arg, evalCtx)
                : action.Value is not null
                    ? CustomWidgetExpressionEvaluator.Interpolate(action.Value, evalCtx)
                    : action.CommandArgs is { Count: > 0 }
                        ? CustomWidgetExpressionEvaluator.Interpolate(action.CommandArgs[0], evalCtx)
                        : string.Empty;
            Dictionary<string, string>? evalArgs = null;
            if (action.Args is { Count: > 0 })
            {
                evalArgs = new Dictionary<string, string>(action.Args.Count, StringComparer.Ordinal);
                foreach ((string ak, string av) in action.Args)
                {
                    evalArgs[ak] = CustomWidgetExpressionEvaluator.Interpolate(av, evalCtx);
                }
            }

            string argsSig = evalArgs is { Count: > 0 }
                ? string.Join("&", evalArgs.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}={kv.Value}"))
                : string.Empty;
            string probeSig = $"{fn}|{itemsSource ?? string.Empty}|{evalArg}|{argsSig}";
            if (!seenSignatures.Add(probeSig))
            {
                continue;
            }

            executed++;

            var clonedState = new Dictionary<string, string>(baseState, StringComparer.Ordinal);
            var clonedStateJson = new Dictionary<string, JsonElement>(baseStateJson.Count, StringComparer.Ordinal);
            foreach ((string sk, JsonElement sv) in baseStateJson)
            {
                clonedStateJson[sk] = sv.Clone();
            }
            var clonedComputed = new Dictionary<string, JsonElement>(baseComputed.Count, StringComparer.Ordinal);
            foreach ((string ck, JsonElement cv) in baseComputed)
            {
                clonedComputed[ck] = cv.Clone();
            }

            CustomWidgetScriptExecutionResult res = CustomWidgetScriptEngine.Execute(
                compiled,
                fn,
                new CustomWidgetScriptExecutionInput
                {
                    State = clonedState,
                    StateJson = clonedStateJson,
                    Settings = settings,
                    Collections = CloneCollectionsMap(baseCollections),
                    Computed = clonedComputed,
                    I18n = i18nTable,
                    Args = evalArgs,
                    ArgsJson = action.ArgsJson,
                    Event = new CustomWidgetScriptEventPayload(
                        Arg: evalArg,
                        Value: "1",
                        Item: sampleItem?.Clone(),
                        Id: sampleItem?.Id ?? "item-1",
                        X: 10,
                        Y: 10,
                        FromIndex: 0,
                        ToIndex: 0,
                        Args: evalArgs,
                        NormX: 0.5,
                        NormY: 0.5,
                        IsPressed: false,
                        Text: "sample",
                        ArgsJson: action.ArgsJson),
                    CultureName = "zh-CN",
                    CallKind = CustomWidgetScriptCallKind.UiEvent,
                    AllowCopy = true,
                    TimeoutMs = HandlerProbeTimeoutMs
                });

            if (!res.IsSuccess)
            {
                failedFunctions.Add(fn);
                int line = res.ErrorLine ?? 0;
                string lineSuffix = line > 0 ? $":{line}" : string.Empty;
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"logic.js{lineSuffix}",
                    $"logic.js handler '{fn}' (invoked by {actionPath}) threw at runtime{(line > 0 ? $" at line {line}" : string.Empty)}: {res.ErrorMessage}",
                    IsError: false,
                    Code: "ScriptHandlerRuntimeError"));
            }
        }
    }

    private static Dictionary<string, List<CustomWidgetRecord>> CloneCollectionsMap(
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> source)
    {
        var copy = new Dictionary<string, List<CustomWidgetRecord>>(source.Count, StringComparer.Ordinal);
        foreach ((string k, List<CustomWidgetRecord> list) in source)
        {
            copy[k] = list.Select(static r => r.Clone()).ToList();
        }
        return copy;
    }

    private static CustomWidgetRecord? ResolveSampleRecordForItemsSource(
        string? itemsSource,
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> collections,
        IReadOnlyDictionary<string, JsonElement> computed)
    {
        if (string.IsNullOrWhiteSpace(itemsSource))
        {
            return null;
        }

        string trimmed = itemsSource.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        if (trimmed.StartsWith("collections.", StringComparison.Ordinal))
        {
            trimmed = trimmed["collections.".Length..];
        }
        else if (trimmed.StartsWith("$collections.", StringComparison.Ordinal))
        {
            trimmed = trimmed["$collections.".Length..];
        }

        if (collections.TryGetValue(trimmed, out List<CustomWidgetRecord>? colList) && colList.Count > 0)
        {
            return colList[0];
        }

        string? compPath = trimmed.StartsWith("$computed.", StringComparison.Ordinal)
            ? trimmed["$computed.".Length..]
            : trimmed.StartsWith("computed.", StringComparison.Ordinal)
                ? trimmed["computed.".Length..]
                : null;
        if (!string.IsNullOrEmpty(compPath))
        {
            IReadOnlyList<CustomWidgetRecord> projected = CustomWidgetScriptEngine.ProjectComputedCollection(compPath, computed);
            if (projected.Count > 0)
            {
                return projected[0];
            }
        }

        return null;
    }

    private static void ScanDirtyEvaluatedInterpolations(
        CustomWidgetManifest manifest,
        CustomWidgetEvaluationContext rootCtx,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        void CheckProp(string? rawTemplate, string propPath, CustomWidgetEvaluationContext ctx, bool checkUnclosed = true)
        {
            if (string.IsNullOrWhiteSpace(rawTemplate) || !rawTemplate.Contains("${", StringComparison.Ordinal))
            {
                return;
            }

            if (checkUnclosed && HasUnclosedInterpolation(rawTemplate))
            {
                if (!diagnostics.Any(d =>
                        string.Equals(d.Code, CustomWidgetStrictAuthoringRules.UnclosedInterpolationExpression, StringComparison.Ordinal) &&
                        string.Equals(d.Path, propPath, StringComparison.Ordinal)))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        propPath,
                        $"Template '{rawTemplate}' contains an unclosed '${{...' interpolation expression (likely truncated). Close the '}}' or complete the binding.",
                        IsError: true,
                        Code: CustomWidgetStrictAuthoringRules.UnclosedInterpolationExpression));
                }
                return;
            }

            string evaluated = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, ctx).Trim();
            if (IsDirtyEvaluatedScalar(rawTemplate, evaluated))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    propPath,
                    $"Expression '{rawTemplate}' evaluated to dirty runtime value '{evaluated}'. Check undefined fields, NaN math, or object-to-string coercion.",
                    IsError: false,
                    Code: "DirtyEvaluatedInterpolation"));
            }
        }

        void WalkNode(CustomWidgetNodeSpec? node, string fallbackPath, CustomWidgetEvaluationContext ctx)
        {
            if (node is null)
            {
                return;
            }

            bool isCodeOrMarkdown = string.Equals(node.Type, "CodeBlock", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(node.Type, "Markdown", StringComparison.OrdinalIgnoreCase);

            string nodePath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : fallbackPath;
            CheckProp(node.Text, $"{nodePath}.text", ctx, checkUnclosed: !isCodeOrMarkdown);
            CheckProp(node.Title, $"{nodePath}.title", ctx);
            CheckProp(node.Subtitle, $"{nodePath}.subtitle", ctx);
            CheckProp(node.Value, $"{nodePath}.value", ctx);
            CheckProp(node.Tooltip, $"{nodePath}.tooltip", ctx);
            CheckProp(node.Progress, $"{nodePath}.progress", ctx);
            CheckProp(node.CenterTitle, $"{nodePath}.centerTitle", ctx);
            CheckProp(node.CenterValue, $"{nodePath}.centerValue", ctx);
            for (int s = 0; s < node.Spans.Count; s++)
            {
                CheckProp(node.Spans[s].Text, $"{nodePath}.spans[{s}].text", ctx);
            }

            if (node.ItemTemplate is not null)
            {
                CustomWidgetEvaluationContext itemCtx = ctx;
                string? colName = node.Collection?.Trim();
                if (!string.IsNullOrEmpty(colName) &&
                    colName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    colName = colName["collections.".Length..];
                }

                if (!string.IsNullOrEmpty(colName) &&
                    ctx.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? recs) &&
                    recs.Count > 0)
                {
                    itemCtx = ctx with { CurrentItem = recs[0], CurrentIndex = 0 };
                }

                WalkNode(node.ItemTemplate, $"{nodePath}.itemTemplate", itemCtx);
            }

            WalkNode(node.ActionButton, $"{nodePath}.actionButton", ctx);
            WalkNode(node.Pane, $"{nodePath}.pane", ctx);

            for (int i = 0; i < node.Children.Count; i++)
            {
                CustomWidgetNodeSpec child = node.Children[i];
                string childPath = !string.IsNullOrWhiteSpace(child.NodePath) ? child.NodePath : $"{nodePath}.children[{i}]";
                WalkNode(child, childPath, ctx);
            }
        }

        WalkNode(manifest.View, "$.view", rootCtx);

        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popPath = !string.IsNullOrWhiteSpace(pop.NodePath) ? pop.NodePath : $"$.popovers[{i}]";
            WalkNode(pop, popPath, rootCtx);
        }

        if (manifest.Capsule is { } cap)
        {
            CheckProp(cap.Title, "$.capsule.title", rootCtx);
            CheckProp(cap.Summary, "$.capsule.summary", rootCtx);
            CheckProp(cap.Badge, "$.capsule.badge", rootCtx);
            CheckProp(cap.Progress, "$.capsule.progress", rootCtx);
        }
    }

    private static bool HasUnclosedInterpolation(string rawTemplate)
    {
        int i = 0;
        while (i < rawTemplate.Length - 1)
        {
            if (rawTemplate[i] == '$' && rawTemplate[i + 1] == '{')
            {
                int depth = 1;
                char quote = '\0';
                bool escaped = false;
                int j = i + 2;
                while (j < rawTemplate.Length && depth > 0)
                {
                    char c = rawTemplate[j];
                    if (quote != '\0')
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == quote)
                        {
                            quote = '\0';
                        }
                    }
                    else
                    {
                        if (c is '\'' or '"')
                        {
                            quote = c;
                        }
                        else if (c == '{')
                        {
                            depth++;
                        }
                        else if (c == '}')
                        {
                            depth--;
                        }
                    }

                    j++;
                }

                if (depth > 0)
                {
                    return true;
                }

                i = j;
                continue;
            }

            i++;
        }

        return false;
    }

    private static bool IsDirtyEvaluatedScalar(string rawTemplate, string evaluated)
    {
        if (string.IsNullOrEmpty(evaluated))
        {
            return false;
        }

        if (string.Equals(evaluated, "NaN", StringComparison.Ordinal) ||
            string.Equals(evaluated, "Infinity", StringComparison.Ordinal) ||
            string.Equals(evaluated, "-Infinity", StringComparison.Ordinal) ||
            string.Equals(evaluated, "undefined", StringComparison.Ordinal) ||
            string.Equals(evaluated, "[object Object]", StringComparison.Ordinal))
        {
            return true;
        }

        if (evaluated.Contains("[object Object]", StringComparison.Ordinal) &&
            !rawTemplate.Contains("[object Object]", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string dirtyToken in new[] { "NaN", "Infinity", "undefined" })
        {
            if (ContainsWholeAsciiWord(evaluated, dirtyToken) &&
                !ContainsWholeAsciiWord(rawTemplate, dirtyToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWholeAsciiWord(string text, string token)
    {
        int start = 0;
        while (start < text.Length)
        {
            int idx = text.IndexOf(token, start, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            int afterIdx = idx + token.Length;
            bool rightOk = afterIdx >= text.Length || !char.IsLetterOrDigit(text[afterIdx]);
            if (leftOk && rightOk)
            {
                return true;
            }

            start = idx + 1;
        }

        return false;
    }

    private static void ValidateDynamicGridAndItemsGridDryRun(
        CustomWidgetManifest manifest,
        CustomWidgetEvaluationContext evalCtx,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        void Walk(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return;
            }

            if (string.Equals(node.Type, "Grid", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(node.Columns) &&
                    node.Columns.Contains("${", StringComparison.Ordinal))
                {
                    string evaluatedCols = CustomWidgetExpressionEvaluator.Interpolate(node.Columns, evalCtx).Trim();
                    string normalizedCols = evaluatedCols;
                    bool tracksValid = !string.IsNullOrEmpty(evaluatedCols) &&
                                       !evaluatedCols.Contains("${", StringComparison.Ordinal) &&
                                       CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(evaluatedCols, out normalizedCols) &&
                                       CustomWidgetManifestParser.IsValidStaticGridTrackList(normalizedCols);
                    if (!string.IsNullOrEmpty(evaluatedCols) &&
                        !evaluatedCols.Contains("${", StringComparison.Ordinal) &&
                        !tracksValid)
                    {
                        diagnostics.Add(new CustomWidgetValidationDiagnostic(
                            $"{node.NodePath}.columns",
                            $"Grid 'columns' expression '{node.Columns}' evaluated on cold start to '{evaluatedCols}', which is not a valid Grid track list (expected e.g. 'Auto,*,*,*,*').",
                            IsError: true,
                            Code: "InvalidComputedGridTracks"));
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.Rows) &&
                    node.Rows.Contains("${", StringComparison.Ordinal))
                {
                    string evaluatedRows = CustomWidgetExpressionEvaluator.Interpolate(node.Rows, evalCtx).Trim();
                    string normalizedRows = evaluatedRows;
                    bool tracksValid = !string.IsNullOrEmpty(evaluatedRows) &&
                                       !evaluatedRows.Contains("${", StringComparison.Ordinal) &&
                                       CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(evaluatedRows, out normalizedRows) &&
                                       CustomWidgetManifestParser.IsValidStaticGridTrackList(normalizedRows);
                    if (!string.IsNullOrEmpty(evaluatedRows) &&
                        !evaluatedRows.Contains("${", StringComparison.Ordinal) &&
                        !tracksValid)
                    {
                        diagnostics.Add(new CustomWidgetValidationDiagnostic(
                            $"{node.NodePath}.rows",
                            $"Grid 'rows' expression '{node.Rows}' evaluated on cold start to '{evaluatedRows}', which is not a valid Grid track list.",
                            IsError: true,
                            Code: "InvalidComputedGridTracks"));
                    }
                }
            }
            else if (string.Equals(node.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(node.Columns) &&
                     node.Columns.Contains("${", StringComparison.Ordinal))
            {
                string evaluatedCount = CustomWidgetExpressionEvaluator.Interpolate(node.Columns, evalCtx).Trim();
                if (!string.IsNullOrEmpty(evaluatedCount) &&
                    !evaluatedCount.Contains("${", StringComparison.Ordinal) &&
                    !CustomWidgetManifestParser.TryParseItemsGridColumnCount(evaluatedCount, out _))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        $"{node.NodePath}.columnsCount",
                        $"ItemsGrid 'columnsCount' expression '{node.Columns}' evaluated on cold start to '{evaluatedCount}', which is not an integer between 1 and 16.",
                        IsError: true,
                        Code: "InvalidComputedItemsGridColumns"));
                }
            }

            Walk(node.ItemTemplate);
            Walk(node.ActionButton);
            Walk(node.Pane);
            for (int i = 0; i < node.Children.Count; i++)
            {
                Walk(node.Children[i]);
            }
        }

        Walk(manifest.View);
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            Walk(manifest.Popovers[i]);
        }

        StudioStateBranchValidator.Validate(manifest, evalCtx, diagnostics);
    }

    private static IEnumerable<(CustomWidgetActionSpec Action, string Path)> EnumerateManifestActionsWithPaths(
        CustomWidgetManifest manifest)
    {
        static IEnumerable<(CustomWidgetActionSpec Action, string Path)> ExpandList(
            IReadOnlyList<CustomWidgetActionSpec>? actions,
            string basePath)
        {
            if (actions is not { Count: > 0 })
            {
                yield break;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] is not { } act)
                {
                    continue;
                }

                string actPath = $"{basePath}[{i}]";
                yield return (act, actPath);
                foreach (var nested in ExpandList(act.Then, $"{actPath}.then"))
                {
                    yield return nested;
                }
            }
        }

        static IEnumerable<(CustomWidgetActionSpec Action, string Path)> ExpandMenu(
            IReadOnlyList<CustomWidgetMenuItemSpec>? items,
            string basePath)
        {
            if (items is not { Count: > 0 })
            {
                yield break;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not { } item)
                {
                    continue;
                }

                string itemPath = $"{basePath}[{i}]";
                foreach (var act in ExpandList(item.OnClick, $"{itemPath}.onClick"))
                {
                    yield return act;
                }

                foreach (var sub in ExpandMenu(item.Children, $"{itemPath}.children"))
                {
                    yield return sub;
                }
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            string path = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : "$.view";
            foreach (var a in ExpandList(node.OnClick, $"{path}.onClick")) yield return a;
            foreach (var a in ExpandList(node.OnSubmit, $"{path}.onSubmit")) yield return a;
            foreach (var a in ExpandList(node.OnChange, $"{path}.onChange")) yield return a;
            foreach (var a in ExpandList(node.OnDropActions, $"{path}.onDrop")) yield return a;
            foreach (var a in ExpandList(node.OnRowClick, $"{path}.onRowClick")) yield return a;
            foreach (var a in ExpandList(node.OnItemClick, $"{path}.onItemClick")) yield return a;
            foreach (var a in ExpandList(node.OnReorder, $"{path}.onReorder")) yield return a;
            foreach (var a in ExpandList(node.OnInlineCommit, $"{path}.onInlineCommit")) yield return a;
            foreach (var a in ExpandList(node.OnPointerDown, $"{path}.onPointerDown")) yield return a;
            foreach (var a in ExpandList(node.OnPointerMove, $"{path}.onPointerMove")) yield return a;
            foreach (var a in ExpandList(node.OnPointerUp, $"{path}.onPointerUp")) yield return a;
            foreach (var a in ExpandMenu(node.MenuItems, $"{path}.menuItems")) yield return a;
            foreach (var a in ExpandMenu(node.ContextMenu, $"{path}.contextMenu")) yield return a;
            for (int i = 0; i < node.Spans.Count; i++)
            {
                foreach (var a in ExpandList(node.Spans[i].OnClick, $"{path}.spans[{i}].onClick"))
                {
                    yield return a;
                }
            }
        }

        if (manifest.Capsule?.PrimaryAction is { } pa)
        {
            foreach (var a in ExpandList(pa.OnClick, "$.capsule.primaryAction.onClick"))
            {
                yield return a;
            }
        }

        if (manifest.OnDrop is { } drop)
        {
            foreach (var a in ExpandList(drop.Actions, "$.onDrop.actions"))
            {
                yield return a;
            }
        }
    }

    private static bool ManifestReferencesComputed(
        CustomWidgetManifest manifest,
        List<(string ActionType, string Fn, string PropName, string Path)> scriptRunCalls,
        ISet<string>? computedKeys = null)
    {
        bool usesComputed = false;

        static bool ContainsComputedToken(string? expr) =>
            !string.IsNullOrEmpty(expr) &&
            (expr.Contains("$computed.", StringComparison.OrdinalIgnoreCase) ||
             expr.Contains("computed.", StringComparison.OrdinalIgnoreCase));

        void CheckExpr(string? expr)
        {
            if (!ContainsComputedToken(expr))
            {
                return;
            }

            usesComputed = true;
            if (computedKeys is not null)
            {
                foreach (Match match in ComputedKeyRegex().Matches(expr!))
                {
                    computedKeys.Add(match.Groups[1].Value);
                }
            }
        }

        void CheckMenuExprs(IReadOnlyList<CustomWidgetMenuItemSpec>? items)
        {
            if (items is null)
            {
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                CheckExpr(items[i].Label);
                CheckExpr(items[i].When);
                CheckExpr(items[i].Value);
                CheckMenuExprs(items[i].Children);
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            CheckExpr(node.When);
            CheckExpr(node.Columns);
            CheckExpr(node.Rows);
            CheckExpr(node.Text);
            CheckExpr(node.Title);
            CheckExpr(node.Subtitle);
            CheckExpr(node.Placeholder);
            CheckExpr(node.Value);
            CheckExpr(node.Icon);
            CheckExpr(node.Tooltip);
            CheckExpr(node.Src);
            CheckExpr(node.Color);
            CheckExpr(node.Background);
            CheckExpr(node.TextColor);
            CheckExpr(node.BorderColor);
            CheckExpr(node.Tone);
            CheckExpr(node.Progress);
            CheckExpr(node.Collection);
            CheckExpr(node.Items);
            CheckExpr(node.Sparkline);
            CheckExpr(node.Unit);
            CheckExpr(node.Filter);
            CheckExpr(node.FilterEquals);
            CheckExpr(node.ElementsSource);
            CheckExpr(node.SuggestionsSource);
            CheckExpr(node.CenterTitle);
            CheckExpr(node.CenterValue);
            CheckExpr(node.WarningThresholdExpression);
            CheckExpr(node.DangerThresholdExpression);
            CheckMenuExprs(node.MenuItems);
            CheckMenuExprs(node.ContextMenu);
            for (int i = 0; i < node.Spans.Count; i++)
            {
                CheckExpr(node.Spans[i].Text);
                CheckExpr(node.Spans[i].Color);
                CheckExpr(node.Spans[i].Href);
            }
            for (int i = 0; i < node.TableColumns.Count; i++)
            {
                CheckExpr(node.TableColumns[i].Header);
                CheckExpr(node.TableColumns[i].Color);
            }
            for (int i = 0; i < node.Series.Count; i++)
            {
                CheckExpr(node.Series[i].Name);
                CheckExpr(node.Series[i].Color);
            }
            for (int i = 0; i < node.Elements.Count; i++)
            {
                CustomWidgetCanvasElementSpec ce = node.Elements[i];
                CheckExpr(ce.X);
                CheckExpr(ce.Y);
                CheckExpr(ce.X2);
                CheckExpr(ce.Y2);
                CheckExpr(ce.Width);
                CheckExpr(ce.Height);
                CheckExpr(ce.Radius);
                CheckExpr(ce.Data);
                CheckExpr(ce.Points);
                CheckExpr(ce.Text);
                CheckExpr(ce.Fill);
                CheckExpr(ce.Stroke);
                CheckExpr(ce.Cx);
                CheckExpr(ce.Cy);
                CheckExpr(ce.RotationExpression);
            }

            if (node.EmptyState is { } empty)
            {
                CheckExpr(empty.Title);
                CheckExpr(empty.Subtitle);
            }
        }

        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
        {
            CheckExpr(act.Value);
            CheckExpr(act.Text);
            CheckExpr(act.Message);
            CheckExpr(act.Title);
            CheckExpr(act.Url);
            CheckExpr(act.Path);
            CheckExpr(act.Destination);
            CheckExpr(act.Pattern);
            CheckExpr(act.Accept);
            CheckExpr(act.JobKey);
            CheckExpr(act.Guard);
            CheckExpr(act.Arg);
            CheckExpr(act.Body);
            CheckExpr(act.WorkingDirectory);
            CheckExpr(act.PopoverId);

            if (act.Args is not null)
            {
                foreach (string v in act.Args.Values)
                {
                    CheckExpr(v);
                }
            }

            if (act.Headers is not null)
            {
                foreach (string v in act.Headers.Values)
                {
                    CheckExpr(v);
                }
            }

            if (act.CommandArgs is not null)
            {
                foreach (string v in act.CommandArgs)
                {
                    CheckExpr(v);
                }
            }

            if (act.ItemFields is not null)
            {
                foreach (string v in act.ItemFields.Values)
                {
                    CheckExpr(v);
                }
            }
        }

        foreach ((CustomWidgetActionSpec act, string actPath) in EnumerateManifestActionsWithPaths(manifest))
        {
            if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase))
            {
                scriptRunCalls.Add(("script.run", act.Fn?.Trim() ?? string.Empty, "fn", actPath));
            }
            else
            {
                if ((string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "command.run", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(act.Fn))
                {
                    scriptRunCalls.Add((act.Type, act.Fn!.Trim(), "fn", actPath));
                }

                if (!string.IsNullOrWhiteSpace(act.OnComplete))
                {
                    scriptRunCalls.Add((act.Type, act.OnComplete!.Trim(), "onComplete", actPath));
                }

                if (!string.IsNullOrWhiteSpace(act.OnProgress))
                {
                    scriptRunCalls.Add((act.Type, act.OnProgress!.Trim(), "onProgress", actPath));
                }
            }
        }

        if (manifest.Capsule is { } cap)
        {
            CheckExpr(cap.Title);
            CheckExpr(cap.Summary);
            CheckExpr(cap.Glyph);
            CheckExpr(cap.Badge);
            CheckExpr(cap.BadgeIsWarning);
            CheckExpr(cap.Progress);
            if (cap.PrimaryAction is { } pa)
            {
                CheckExpr(pa.Icon);
                CheckExpr(pa.Tooltip);
            }
        }

        if (manifest.OnDrop is { } drop)
        {
            CheckExpr(drop.Mode);
        }

        return usesComputed;
    }

    private static bool ManifestReferencesI18n(
        CustomWidgetManifest manifest,
        out string firstKey,
        out string firstPath)
    {
        string? foundKey = null;
        string? foundPath = null;

        void CheckExpr(string? expr, string path)
        {
            if (foundKey is not null || string.IsNullOrEmpty(expr))
            {
                return;
            }

            if (!expr.Contains("$i18n.", StringComparison.Ordinal) &&
                !expr.Contains("t(", StringComparison.Ordinal))
            {
                return;
            }

            Match m = I18nTokenRegex().Match(expr);
            if (m.Success)
            {
                foundKey = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                foundPath = path;
            }
        }

        void CheckMenuI18n(IReadOnlyList<CustomWidgetMenuItemSpec>? items, string basePath)
        {
            if (items is null || foundKey is not null)
            {
                return;
            }

            for (int i = 0; i < items.Count && foundKey is null; i++)
            {
                CheckExpr(items[i].Label, $"{basePath}[{i}].label");
                CheckMenuI18n(items[i].Children, $"{basePath}[{i}].children");
            }
        }

        CheckExpr(manifest.Name, "$.name");
        CheckExpr(manifest.Description, "$.description");

        for (int i = 0; i < manifest.Settings.Count && foundKey is null; i++)
        {
            CustomWidgetSettingDefinition s = manifest.Settings[i];
            CheckExpr(s.Label, $"$.settings[{i}].label");
            CheckExpr(s.DefaultValue, $"$.settings[{i}].default");
            for (int j = 0; j < s.Options.Count && foundKey is null; j++)
            {
                CheckExpr(s.Options[j].Label, $"$.settings[{i}].options[{j}].label");
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (foundKey is not null)
            {
                break;
            }

            string path = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : "$.view";
            CheckExpr(node.Text, $"{path}.text");
            CheckExpr(node.Title, $"{path}.title");
            CheckExpr(node.Subtitle, $"{path}.subtitle");
            CheckExpr(node.Placeholder, $"{path}.placeholder");
            CheckExpr(node.Value, $"{path}.value");
            CheckExpr(node.Tooltip, $"{path}.tooltip");
            CheckExpr(node.Unit, $"{path}.unit");
            CheckExpr(node.CenterTitle, $"{path}.centerTitle");
            CheckExpr(node.CenterValue, $"{path}.centerValue");

            if (node.EmptyState is { } empty)
            {
                CheckExpr(empty.Title, $"{path}.emptyState.title");
                CheckExpr(empty.Subtitle, $"{path}.emptyState.subtitle");
            }

            CheckMenuI18n(node.MenuItems, $"{path}.menuItems");
            CheckMenuI18n(node.ContextMenu, $"{path}.contextMenu");

            for (int i = 0; i < node.Spans.Count && foundKey is null; i++)
            {
                CheckExpr(node.Spans[i].Text, $"{path}.spans[{i}].text");
            }

            for (int i = 0; i < node.Options.Count && foundKey is null; i++)
            {
                CheckExpr(node.Options[i].Label, $"{path}.options[{i}].label");
            }

            for (int i = 0; i < node.TableColumns.Count && foundKey is null; i++)
            {
                CheckExpr(node.TableColumns[i].Header, $"{path}.columns[{i}].header");
            }

            for (int i = 0; i < node.Series.Count && foundKey is null; i++)
            {
                CheckExpr(node.Series[i].Name, $"{path}.series[{i}].name");
            }

            for (int i = 0; i < node.Elements.Count && foundKey is null; i++)
            {
                CheckExpr(node.Elements[i].Text, $"{path}.elements[{i}].text");
            }
        }

        if (foundKey is null)
        {
            foreach ((CustomWidgetActionSpec act, string actPath) in EnumerateManifestActionsWithPaths(manifest))
            {
                if (foundKey is not null)
                {
                    break;
                }

                CheckExpr(act.Value, $"{actPath}.value");
                CheckExpr(act.Text, $"{actPath}.text");
                CheckExpr(act.Message, $"{actPath}.message");
                CheckExpr(act.Title, $"{actPath}.title");
                CheckExpr(act.Prompt, $"{actPath}.prompt");
            }
        }

        if (manifest.Capsule is { } cap && foundKey is null)
        {
            CheckExpr(cap.Title, "$.capsule.title");
            CheckExpr(cap.Summary, "$.capsule.summary");
            CheckExpr(cap.Badge, "$.capsule.badge");
            if (cap.PrimaryAction is { } pa)
            {
                CheckExpr(pa.Tooltip, "$.capsule.primaryAction.tooltip");
            }
        }

        firstKey = foundKey ?? string.Empty;
        firstPath = foundPath ?? "$.view";
        return foundKey is not null;
    }

    private static readonly System.Collections.Frozen.FrozenSet<string> AllowedScriptCollectionMethods =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "get", "add", "prepend", "append", "update", "remove", "delete", "count", "clear", "set"
        ],
        StringComparer.Ordinal);

    [GeneratedRegex(@"(?<![.\w$])(setInterval|setTimeout|requestAnimationFrame)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenTimerGlobalCallRegex();

    [GeneratedRegex(@"\bctx\s*\.\s*collections\s*(?:\.\s*([A-Za-z_$][A-Za-z0-9_$]*)|(\[))", RegexOptions.CultureInvariant)]
    private static partial Regex CtxCollectionsAccessRegex();

    [GeneratedRegex(@"\bctx\s*\.\s*collections\s*\.\s*get\s*\([^)]*\)\s*\.\s*(push|pop|shift|unshift|splice|sort|reverse)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DetachedCollectionMutationRegex();

    [GeneratedRegex(@"\bctx\s*\.\s*(?:ui\s*\.\s*)?dispatch\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CtxDispatchCallRegex();

    [GeneratedRegex(@"\btype\s*:\s*(['""])([^'""\\${}]+)\1", RegexOptions.CultureInvariant)]
    private static partial Regex DispatchActionTypeLiteralRegex();

    [GeneratedRegex(@"\btypeof\s+ctx\s*\.\s*collections\s*(?:\?\.\s*get|\.\s*get)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CtxCollectionsGetFeatureCheckRegex();

    [GeneratedRegex(@"\b(?:popoverId|targetId|target|id)\s*:\s*(['""])([^'""\\${}]+)\1", RegexOptions.CultureInvariant)]
    private static partial Regex DispatchPopoverIdLiteralRegex();

    private static bool IsSpecialOrDynamicPopoverId(string id) =>
        string.IsNullOrWhiteSpace(id) ||
        id.Contains("${", StringComparison.Ordinal) ||
        string.Equals(id, "*", StringComparison.Ordinal) ||
        string.Equals(id, "all", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(id, "__secrets", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("__ai_credentials", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> CollectDeclaredPopoverIds(CustomWidgetManifest manifest)
    {
        var declaredPopoverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            if (!string.IsNullOrWhiteSpace(pop.PopoverId))
            {
                declaredPopoverIds.Add(pop.PopoverId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(pop.Id))
            {
                declaredPopoverIds.Add(pop.Id.Trim());
            }
        }

        if (manifest.View is not null)
        {
            foreach (CustomWidgetNodeSpec node in manifest.View.EnumerateAllNodes())
            {
                if (string.Equals(node.Type, "Popover", StringComparison.OrdinalIgnoreCase) &&
                    node.Children.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(node.PopoverId))
                    {
                        declaredPopoverIds.Add(node.PopoverId.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(node.Id))
                    {
                        declaredPopoverIds.Add(node.Id.Trim());
                    }
                }
            }
        }

        return declaredPopoverIds;
    }

    private static void ValidatePopoverTargets(
        CustomWidgetManifest manifest,
        string? logicJs,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        HashSet<string> declaredPopoverIds = CollectDeclaredPopoverIds(manifest);

        string availableList = declaredPopoverIds.Count > 0
            ? string.Join(", ", declaredPopoverIds.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase))
            : "(none)";

        void ReportMissingPopover(string path, string targetId)
        {
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                path,
                $"Popover target '{targetId}' is not declared in root \"popovers\" (registered popovers: {availableList}). Declare popovers at top-level `\"popovers\": {{ \"{targetId}\": {{ \"view\": {{ ... }} }} }}` or `\"popovers\": [{{ \"id\": \"{targetId}\", \"title\": \"...\", \"children\": [...] }}]`, not inside `view.children`.",
                IsError: true,
                Code: "UnknownPopoverTarget"));
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            string nodePath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : "$.view";
            bool isSelfDeclaringPopover = string.Equals(node.Type, "Popover", StringComparison.OrdinalIgnoreCase) &&
                                          node.Children.Count > 0;
            if (!string.IsNullOrWhiteSpace(node.Popover))
            {
                string target = node.Popover.Trim();
                bool matchesSelf = isSelfDeclaringPopover &&
                                   (string.Equals(target, node.PopoverId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(target, node.Id?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!matchesSelf && !IsSpecialOrDynamicPopoverId(target) && !declaredPopoverIds.Contains(target))
                {
                    ReportMissingPopover($"{nodePath}.popover", target);
                }
            }
        }

        foreach ((CustomWidgetActionSpec act, string actPath) in EnumerateManifestActionsWithPaths(manifest))
        {
            if (string.Equals(act.Type, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(act.Type, "popover.toggle", StringComparison.OrdinalIgnoreCase))
            {
                string target = (act.PopoverId ?? act.TargetId ?? act.Key ?? string.Empty).Trim();
                if (!IsSpecialOrDynamicPopoverId(target) && !declaredPopoverIds.Contains(target))
                {
                    ReportMissingPopover($"{actPath}.popoverId", target);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            string codeOnly = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: true);
            string commentsMasked = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: false);
            foreach ((int startIdx, int endIdx) in EnumerateCtxDispatchArgumentSlices(codeOnly))
            {
                string argSlice = commentsMasked[startIdx..endIdx];
                bool hasPopoverAction = false;
                foreach (Match typeMatch in DispatchActionTypeLiteralRegex().Matches(argSlice))
                {
                    string actionType = typeMatch.Groups[2].Value.Trim();
                    if (string.Equals(actionType, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(actionType, "popover.toggle", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPopoverAction = true;
                        break;
                    }
                }

                if (!hasPopoverAction)
                {
                    continue;
                }

                foreach (Match popMatch in DispatchPopoverIdLiteralRegex().Matches(argSlice))
                {
                    string target = popMatch.Groups[2].Value.Trim();
                    if (!IsSpecialOrDynamicPopoverId(target) && !declaredPopoverIds.Contains(target))
                    {
                        int line = GetOneBasedLineNumber(commentsMasked, startIdx + popMatch.Index);
                        ReportMissingPopover($"logic.js:{line}", target);
                    }
                }
            }
        }
    }

    private static void ValidateScriptApiSemantics(
        string logicJs,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(logicJs))
        {
            return;
        }

        string codeOnly = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: true);
        string commentsMasked = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: false);

        var reportedCollectionLines = new HashSet<int>();
        foreach (Match match in CtxCollectionsAccessRegex().Matches(codeOnly))
        {
            int line = GetOneBasedLineNumber(codeOnly, match.Index);
            int lookbackStart = Math.Max(0, match.Index - 240);
            if (CtxCollectionsGetFeatureCheckRegex().IsMatch(codeOnly.AsSpan(lookbackStart, match.Index - lookbackStart)))
            {
                continue;
            }

            if (match.Groups[2].Success)
            {
                if (!reportedCollectionLines.Add(line))
                {
                    continue;
                }

                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"logic.js:{line}",
                    $"Invalid bracket indexing 'ctx.collections[...]' at line {line}: ctx.collections is a method namespace, not a map of arrays. Use `ctx.collections.get('<name>')` to read records and `ctx.collections.append('<name>', item)` / `update` / `remove` / `set` to mutate.",
                    IsError: true,
                    Code: "InvalidCollectionsApiUsage"));
                continue;
            }

            string member = match.Groups[1].Value;
            if (!AllowedScriptCollectionMethods.Contains(member) && reportedCollectionLines.Add(line))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"logic.js:{line}",
                    $"Invalid access 'ctx.collections.{member}' at line {line}: ctx.collections is a method namespace (reading '.{member}' returns undefined). Use `ctx.collections.get('{member}')` to read records, and `ctx.collections.append('{member}', item)` / `prepend` / `update('{member}', id, patch)` / `remove('{member}', idOrFn)` / `set('{member}', items)` to persist mutations.",
                    IsError: true,
                    Code: "InvalidCollectionsApiUsage"));
            }
        }

        foreach (Match match in DetachedCollectionMutationRegex().Matches(codeOnly))
        {
            int line = GetOneBasedLineNumber(codeOnly, match.Index);
            string mutator = match.Groups[1].Value;
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                $"logic.js:{line}",
                $"Calling '.{mutator}()' directly on `ctx.collections.get(...)` at line {line} only mutates a temporary copy and will NOT persist. Use `ctx.collections.append(name, item)`, `prepend(name, item)`, `update(name, id, patch)`, `remove(name, idOrFn)`, or `set(name, updatedArray)` instead.",
                IsError: true,
                Code: "MutatingDetachedCollectionCopy"));
        }

        var reportedTimerLines = new HashSet<int>();
        HashSet<string>? declaredLocalNames = null;
        foreach (Match timerMatch in ForbiddenTimerGlobalCallRegex().Matches(codeOnly))
        {
            string timerName = timerMatch.Groups[1].Value;
            if (declaredLocalNames is null)
            {
                declaredLocalNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match declMatch in JsLocalBindingDeclRegex().Matches(codeOnly))
                {
                    declaredLocalNames.Add(declMatch.Groups[1].Value);
                }
            }

            if (declaredLocalNames.Contains(timerName))
            {
                continue;
            }

            int line = GetOneBasedLineNumber(codeOnly, timerMatch.Index);
            if (reportedTimerLines.Add(line))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"logic.js:{line}",
                    $"QuickJS sandbox does not provide '{timerName}()' at line {line} (handlers execute synchronously within a bounded budget). Remove '{timerName}()' and either compute the final state directly in the handler or declare `\"script\": {{ \"entry\": \"logic.js\", \"tickIntervalMs\": 1000 }}` in manifest.json with `export function onTick(ctx)`.",
                    IsError: true,
                    Code: "ForbiddenBrowserTimerApi"));
            }
        }

        foreach ((int startIdx, int endIdx) in EnumerateCtxDispatchArgumentSlices(codeOnly))
        {
            string argSlice = commentsMasked[startIdx..endIdx];
            foreach (Match typeMatch in DispatchActionTypeLiteralRegex().Matches(argSlice))
            {
                string actionType = typeMatch.Groups[2].Value.Trim();
                if (!CustomWidgetManifestParser.IsSupportedActionType(actionType))
                {
                    int line = GetOneBasedLineNumber(commentsMasked, startIdx + typeMatch.Index);
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        $"logic.js:{line}",
                        BuildUnknownDispatchActionMessage(actionType, line),
                        IsError: true,
                        Code: "UnknownDispatchedActionType"));
                }
            }
        }
    }

    [GeneratedRegex(@"\b(?:ctx|event)\s*(?:\?\.|\.)\s*args\s*(?:\?\.|\.)\s*([A-Za-z_$][A-Za-z0-9_$]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptArgsDotKeyRegex();

    [GeneratedRegex(@"\b(?:ctx|event)\s*(?:\?\.|\.)\s*args\s*(?:\?\.|\.)\s*get\s*\(\s*(['""])([A-Za-z_$][A-Za-z0-9_$]*)\1", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptArgsGetCallKeyRegex();

    [GeneratedRegex(@"\b(?:ctx|event)\s*(?:\?\.|\.)\s*args\s*(?:\?\.)?\s*\[\s*(['""])([A-Za-z_$][A-Za-z0-9_$]*)\1\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptArgsBracketKeyRegex();

    [GeneratedRegex(@"\b(?:function|const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsLocalBindingDeclRegex();

    [GeneratedRegex(@"\bfunction\s+(?<fn>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(|\b(?:const|let|var)\s+(?<fn>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex JsFunctionDeclarationHeaderRegex();

    [GeneratedRegex(@"\{\s*([A-Za-z0-9_$,\s:=]+)\}\s*=\s*(?:[A-Za-z_$][A-Za-z0-9_$]*\s*(?:\?\.|\.)\s*)*args\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 500)]
    private static partial Regex ScriptArgsDestructureRegex();

    [GeneratedRegex(@"\bargs\s*(?:\?\.)?\s*\[\s*[^'""\s\d]|\bObject\s*\.\s*(?:keys|values|entries|assign)\s*\([^)]*\bargs\b|\.\.\.\s*(?:ctx\s*(?:\?\.|\.)\s*)?args\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptArgsDynamicAccessRegex();

    private static void ValidateScriptActionArgsAlignment(
        CustomWidgetManifest manifest,
        CustomWidgetCompiledScript compiled,
        string logicJs,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var suppliedByFn = new Dictionary<string, (HashSet<string> Keys, string FirstPath)>(StringComparer.Ordinal);

        void CollectFromActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null || actions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Fn))
                {
                    string fn = act.Fn.Trim();
                    bool hasNamed = (act.Args is { Count: > 0 }) || (act.ArgsJson is { Count: > 0 });
                    if (hasNamed)
                    {
                        if (!suppliedByFn.TryGetValue(fn, out var entry))
                        {
                            entry = (new HashSet<string>(StringComparer.Ordinal), $"{actPath}.args");
                            suppliedByFn[fn] = entry;
                        }

                        if (act.Args is not null)
                        {
                            foreach (string k in act.Args.Keys)
                            {
                                if (!string.IsNullOrWhiteSpace(k))
                                {
                                    entry.Keys.Add(k.Trim());
                                }
                            }
                        }

                        if (act.ArgsJson is not null)
                        {
                            foreach (string k in act.ArgsJson.Keys)
                            {
                                if (!string.IsNullOrWhiteSpace(k))
                                {
                                    entry.Keys.Add(k.Trim());
                                }
                            }
                        }
                    }
                }

                if (act.Then is { Count: > 0 })
                {
                    CollectFromActions(act.Then, $"{actPath}.then");
                }
            }
        }

        void WalkNode(CustomWidgetNodeSpec? node, string nodePath)
        {
            if (node is null)
            {
                return;
            }

            CollectFromActions(node.OnClick, $"{nodePath}.onClick");
            CollectFromActions(node.OnSubmit, $"{nodePath}.onSubmit");
            CollectFromActions(node.OnChange, $"{nodePath}.onChange");
            CollectFromActions(node.OnDropActions, $"{nodePath}.onDrop");
            CollectFromActions(node.OnRowClick, $"{nodePath}.onRowClick");
            CollectFromActions(node.OnItemClick, $"{nodePath}.onItemClick");
            CollectFromActions(node.OnReorder, $"{nodePath}.onReorder");
            CollectFromActions(node.OnInlineCommit, $"{nodePath}.onInlineCommit");

            WalkNode(node.ItemTemplate, $"{nodePath}.itemTemplate");
            WalkNode(node.ActionButton, $"{nodePath}.actionButton");
            WalkNode(node.Pane, $"{nodePath}.pane");

            for (int i = 0; i < node.MenuItems.Count; i++)
            {
                CollectFromActions(node.MenuItems[i].OnClick, $"{nodePath}.menuItems[{i}].onClick");
            }

            for (int i = 0; i < node.ContextMenu.Count; i++)
            {
                CollectFromActions(node.ContextMenu[i].OnClick, $"{nodePath}.contextMenu[{i}].onClick");
            }

            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                WalkNode(node.HoverActions[i], $"{nodePath}.hoverActions[{i}]");
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                CustomWidgetNodeSpec child = node.Children[i];
                string childPath = !string.IsNullOrWhiteSpace(child.NodePath) ? child.NodePath : $"{nodePath}.children[{i}]";
                WalkNode(child, childPath);
            }
        }

        WalkNode(manifest.View, "$.view");
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popPath = !string.IsNullOrWhiteSpace(pop.NodePath) ? pop.NodePath : $"$.popovers[{i}]";
            WalkNode(pop, popPath);
        }

        if (manifest.OnDrop is not null)
        {
            CollectFromActions(manifest.OnDrop.Actions, "$.onDrop.actions");
        }

        if (manifest.Capsule?.PrimaryAction is { } pa)
        {
            CollectFromActions(pa.OnClick, "$.capsule.primaryAction.onClick");
        }

        if (suppliedByFn.Count == 0)
        {
            return;
        }

        string codeOnly = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: true);
        string commentsMasked = MaskJsCommentsAndStringsPreservingLines(logicJs, maskStrings: false);

        HashSet<string> allFileReadKeys = ExtractReadArgsKeysFromSlice(commentsMasked);

        foreach ((string fn, (HashSet<string> suppliedKeys, string firstPath)) in suppliedByFn)
        {
            if (suppliedKeys.Count == 0 ||
                !compiled.ExportedFunctions.Contains(fn) ||
                allFileReadKeys.Overlaps(suppliedKeys))
            {
                continue;
            }

            if (!TryExtractJsFunctionBodySlice(codeOnly, commentsMasked, fn, out string fnBodySlice, out int fnStartIdx))
            {
                continue;
            }

            if (ScriptArgsDynamicAccessRegex().IsMatch(fnBodySlice))
            {
                continue;
            }

            HashSet<string> fnReadKeys = ExtractReadArgsKeysFromSlice(fnBodySlice);
            if (fnReadKeys.Count == 0 || fnReadKeys.Overlaps(suppliedKeys))
            {
                continue;
            }

            int line = GetOneBasedLineNumber(commentsMasked, fnStartIdx);
            string suppliedList = string.Join(", ", suppliedKeys.OrderBy(static s => s, StringComparer.Ordinal));
            string readList = string.Join(", ", fnReadKeys.OrderBy(static s => s, StringComparer.Ordinal));
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                firstPath,
                $"Action 'script.run' passes args {{{suppliedList}}} to '{fn}(ctx)', but '{fn}' in logic.js:{line} reads ctx.args.{{{readList}}}. Align the argument key names between manifest.json and logic.js.",
                IsError: false,
                Code: CustomWidgetStrictAuthoringRules.ScriptActionArgsKeyMismatch));
        }
    }

    private static HashSet<string> ExtractReadArgsKeysFromSlice(string slice)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ScriptArgsDotKeyRegex().Matches(slice))
        {
            string key = m.Groups[1].Value;
            int afterIdx = m.Index + m.Length;
            while (afterIdx < slice.Length && char.IsWhiteSpace(slice[afterIdx]))
            {
                afterIdx++;
            }

            // Exclude method call `ctx.args.get(...)` from bare dot property matches; ScriptArgsGetCallKeyRegex extracts its string key.
            if (string.Equals(key, "get", StringComparison.Ordinal) && afterIdx < slice.Length && slice[afterIdx] == '(')
            {
                continue;
            }

            if (!string.Equals(key, "length", StringComparison.Ordinal))
            {
                keys.Add(key);
            }
        }

        foreach (Match m in ScriptArgsGetCallKeyRegex().Matches(slice))
        {
            keys.Add(m.Groups[2].Value);
        }

        foreach (Match m in ScriptArgsBracketKeyRegex().Matches(slice))
        {
            keys.Add(m.Groups[2].Value);
        }

        foreach (Match m in ScriptArgsDestructureRegex().Matches(slice))
        {
            foreach (string rawPart in m.Groups[1].Value.Split(','))
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part.StartsWith("...", StringComparison.Ordinal))
                {
                    continue;
                }

                int colonIdx = part.IndexOf(':');
                if (colonIdx > 0)
                {
                    part = part[..colonIdx].Trim();
                }

                int eqIdx = part.IndexOf('=');
                if (eqIdx > 0)
                {
                    part = part[..eqIdx].Trim();
                }

                if (part.Length > 0 && (char.IsLetter(part[0]) || part[0] is '_' or '$'))
                {
                    keys.Add(part);
                }
            }
        }

        return keys;
    }

    private static bool TryExtractJsFunctionBodySlice(
        string codeOnly,
        string commentsMasked,
        string fnName,
        out string bodySlice,
        out int fnStartIdx)
    {
        bodySlice = string.Empty;
        fnStartIdx = 0;

        Match? fnMatch = null;
        foreach (Match candidate in JsFunctionDeclarationHeaderRegex().Matches(codeOnly))
        {
            if (string.Equals(candidate.Groups["fn"].Value, fnName, StringComparison.Ordinal))
            {
                fnMatch = candidate;
                break;
            }
        }

        if (fnMatch is null || !fnMatch.Success)
        {
            return false;
        }

        fnStartIdx = fnMatch.Index;
        int openBrace = codeOnly.IndexOf('{', fnMatch.Index + fnMatch.Length);
        if (openBrace < 0)
        {
            return false;
        }

        // For `function foo(a = {}) { ... }`, skip braces inside parameter list before the closing `)`.
        if (codeOnly.AsSpan(fnMatch.Index, fnMatch.Length).EndsWith("("))
        {
            int parenDepth = 1;
            int p = fnMatch.Index + fnMatch.Length;
            while (p < codeOnly.Length && parenDepth > 0)
            {
                if (codeOnly[p] == '(') parenDepth++;
                else if (codeOnly[p] == ')') parenDepth--;
                p++;
            }

            openBrace = codeOnly.IndexOf('{', p);
            if (openBrace < 0)
            {
                return false;
            }
        }

        int depth = 1;
        int i = openBrace + 1;
        while (i < codeOnly.Length && depth > 0)
        {
            if (codeOnly[i] == '{') depth++;
            else if (codeOnly[i] == '}') depth--;
            i++;
        }

        if (depth != 0)
        {
            return false;
        }

        bodySlice = commentsMasked[openBrace..i];
        return true;
    }

    private static string BuildUnknownDispatchActionMessage(string actionType, int? line)
    {
        string atLine = line.HasValue ? $" at line {line.Value}" : string.Empty;
        if (actionType.Contains("toast", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("notify", StringComparison.OrdinalIgnoreCase))
        {
            return $"Unknown action type '{actionType}' in ctx.dispatch{atLine}. Use `ctx.ui.toast(message, tone)` or `{{ type: 'toast.show', message: '...' }}` / `{{ type: 'reminder.notify', title: '...', message: '...' }}`.";
        }

        if (actionType.Contains("modal", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("dialog", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("popover", StringComparison.OrdinalIgnoreCase))
        {
            return $"Unknown action type '{actionType}' in ctx.dispatch{atLine}. Use `{{ type: 'popover.open', popoverId: '<id>' }}`, `popover.close`, or `popover.toggle`.";
        }

        return $"Unknown action type '{actionType}' in ctx.dispatch{atLine}. Check DeskBox's supported action catalog (state.set, collection.*, popover.*, tip.*, toast.show, reminder.notify, script.run, source.refresh, etc.).";
    }

    private static List<(int Start, int End)> EnumerateCtxDispatchArgumentSlices(string codeOnly)
    {
        var slices = new List<(int Start, int End)>();
        foreach (Match match in CtxDispatchCallRegex().Matches(codeOnly))
        {
            int openParenIdx = match.Index + match.Length - 1;
            int depth = 0;
            int endIdx = codeOnly.Length;
            for (int i = openParenIdx; i < codeOnly.Length; i++)
            {
                char ch = codeOnly[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        endIdx = i;
                        break;
                    }
                }
            }

            if (openParenIdx + 1 <= endIdx)
            {
                slices.Add((openParenIdx + 1, endIdx));
            }
        }

        return slices;
    }

    private static string MaskJsCommentsAndStringsPreservingLines(string source, bool maskStrings)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        char[] chars = source.ToCharArray();
        int n = chars.Length;
        for (int i = 0; i < n; i++)
        {
            char c = chars[i];
            if (c == '/' && i + 1 < n && chars[i + 1] == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i += 2;
                while (i < n && chars[i] != '\n')
                {
                    chars[i] = chars[i] == '\r' ? '\r' : ' ';
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && chars[i + 1] == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i += 2;
                while (i < n)
                {
                    if (chars[i] == '*' && i + 1 < n && chars[i + 1] == '/')
                    {
                        chars[i] = ' ';
                        chars[i + 1] = ' ';
                        i++;
                        break;
                    }
                    if (chars[i] is not ('\r' or '\n'))
                    {
                        chars[i] = ' ';
                    }
                    i++;
                }
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                char quote = c;
                i++;
                while (i < n)
                {
                    char sc = chars[i];
                    if (sc == '\\')
                    {
                        if (maskStrings && i + 1 < n && chars[i + 1] is not ('\r' or '\n'))
                        {
                            chars[i] = ' ';
                            chars[i + 1] = ' ';
                        }
                        i += 2;
                        continue;
                    }

                    if (sc == quote)
                    {
                        break;
                    }

                    if (maskStrings && sc is not ('\r' or '\n'))
                    {
                        chars[i] = ' ';
                    }
                    i++;
                }
            }
        }

        return new string(chars);
    }

    private static int GetOneBasedLineNumber(string text, int index)
    {
        int clamped = Math.Clamp(index, 0, text.Length);
        int line = 1;
        for (int i = 0; i < clamped; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static bool AreComputedMapsZeroEffectMatch(
        IReadOnlyDictionary<string, JsonElement> passA,
        IReadOnlyDictionary<string, JsonElement> passB)
    {
        if (passB.Count == 0 || passB.Count != passA.Count)
        {
            return false;
        }

        bool anyZeroOrEmpty = false;
        foreach ((string key, JsonElement bVal) in passB)
        {
            if (!passA.TryGetValue(key, out JsonElement aVal) ||
                !string.Equals(aVal.GetRawText(), bVal.GetRawText(), StringComparison.Ordinal))
            {
                return false;
            }

            if (IsZeroOrEmptyComputedValue(bVal))
            {
                anyZeroOrEmpty = true;
            }
        }

        return anyZeroOrEmpty;
    }

    private static bool IsZeroOrEmptyComputedValue(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Number:
                return el.TryGetDouble(out double num) && Math.Abs(num) < 1e-9;
            case JsonValueKind.Array:
                return el.GetArrayLength() == 0;
            case JsonValueKind.Object:
                return !el.EnumerateObject().Any();
            case JsonValueKind.String:
            {
                string raw = (el.GetString() ?? string.Empty).Trim();
                if (raw.Length == 0 || raw is "[]" or "{}")
                {
                    return true;
                }

                string cleaned = raw.TrimStart('¥', '$', '€', '£', ' ')
                    .TrimEnd('%', '元', ' ');
                return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double strNum) &&
                       Math.Abs(strNum) < 1e-9;
            }
            default:
                return false;
        }
    }
}
