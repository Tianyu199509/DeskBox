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

    public static CustomWidgetWorkspaceValidationResult ValidateWorkspaceBundle(
        string manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null,
        bool runOnInitDryRun = true)
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

        var scriptRunCalls = new List<(string ActionType, string Fn, string Path)>();
        bool referencesComputed = ManifestReferencesComputed(manifest, scriptRunCalls);
        bool declaresScript = manifest.Script is not null;
        bool hasLogicJs = !string.IsNullOrWhiteSpace(logicJs);

        if (!hasLogicJs)
        {
            if (scriptRunCalls.Count > 0)
            {
                (string actType, string fn, string path) = scriptRunCalls[0];
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    path,
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

        foreach ((string actType, string fn, string path) in scriptRunCalls)
        {
            if (string.IsNullOrWhiteSpace(fn))
            {
                continue;
            }

            if (!compiled.ExportedFunctions.Contains(fn))
            {
                diagnostics.Add(new CustomWidgetValidationDiagnostic(
                    $"{path}.fn",
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

        if (runOnInitDryRun && compiled.ExportedFunctions.Contains("onInit"))
        {
            RunOnInitDryRun(manifest, locales, compiled, diagnostics);
        }

        return new CustomWidgetWorkspaceValidationResult(parseResult, diagnostics, compiled);
    }

    public static CustomWidgetWorkspaceValidationResult ValidateWorkspaceDirectory(
        string manifestJson,
        string? logicJs,
        string? localesDirectory,
        bool runOnInitDryRun = true)
    {
        var rawFileErrors = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetLocaleCatalog? locales = null;
        if (!string.IsNullOrWhiteSpace(localesDirectory) && Directory.Exists(localesDirectory))
        {
            ValidateRawLocaleDirectoryFiles(localesDirectory, rawFileErrors);
            locales = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDirectory);
        }

        CustomWidgetWorkspaceValidationResult res = ValidateWorkspaceBundle(
            manifestJson,
            logicJs,
            locales,
            runOnInitDryRun);

        if (rawFileErrors.Count == 0)
        {
            return res;
        }

        var combined = new List<CustomWidgetValidationDiagnostic>(res.Diagnostics.Count + rawFileErrors.Count);
        combined.AddRange(rawFileErrors);
        combined.AddRange(res.Diagnostics);
        return new CustomWidgetWorkspaceValidationResult(res.ManifestParseResult, combined, res.CompiledScript);
    }

    public static IReadOnlyList<CustomWidgetValidationDiagnostic> ValidateRawLocaleDirectoryFiles(
        string localesDirectory)
    {
        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        ValidateRawLocaleDirectoryFiles(localesDirectory, diagnostics);
        return diagnostics;
    }

    public static void ValidateLocalesDirectory(
        string localesDirectory,
        List<CustomWidgetValidationDiagnostic> diagnostics) =>
        ValidateRawLocaleDirectoryFiles(localesDirectory, diagnostics);

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
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var state = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in manifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(def.Key) &&
                !string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                settings[def.Key] = def.DefaultValue ?? string.Empty;
            }
        }

        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        foreach (string colName in manifest.Collections.Keys)
        {
            collections[colName] = [];
        }

        CustomWidgetManifest effectiveManifest = locales is { IsEmpty: false }
            ? manifest.WithLocales(locales)
            : manifest;

        IReadOnlyDictionary<string, string> i18nTable =
            effectiveManifest.ResolveI18nDictionary("zh-CN");

        CustomWidgetScriptExecutionResult dryRun = CustomWidgetScriptEngine.Execute(
            compiled,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                State = state,
                Settings = settings,
                Collections = collections,
                Computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                I18n = i18nTable,
                CultureName = "zh-CN",
                TimeoutMs = CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs
            });

        if (!dryRun.IsSuccess)
        {
            int line = dryRun.ErrorLine ?? 0;
            string lineSuffix = line > 0 ? $":{line}" : string.Empty;
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                $"logic.js{lineSuffix}",
                $"logic.js 'onInit' failed dry-run{(line > 0 ? $" at line {line}" : string.Empty)}: {dryRun.ErrorMessage}",
                IsError: true,
                Code: "ScriptOnInitRuntimeError"));
        }
    }

    private static bool ManifestReferencesComputed(
        CustomWidgetManifest manifest,
        List<(string ActionType, string Fn, string Path)> scriptRunCalls)
    {
        bool usesComputed = false;

        static bool ContainsComputedToken(string? expr) =>
            !string.IsNullOrEmpty(expr) &&
            (expr.Contains("$computed.", StringComparison.OrdinalIgnoreCase) ||
             expr.Contains("computed.", StringComparison.OrdinalIgnoreCase));

        void CheckExpr(string? expr)
        {
            if (!usesComputed && ContainsComputedToken(expr))
            {
                usesComputed = true;
            }
        }

        void WalkActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                CheckExpr(act.Value);
                CheckExpr(act.Text);
                CheckExpr(act.Message);
                CheckExpr(act.Title);
                CheckExpr(act.Url);
                CheckExpr(act.Path);
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

                if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase))
                {
                    scriptRunCalls.Add(("script.run", act.Fn?.Trim() ?? string.Empty, actPath));
                }
                else if ((string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(act.Type, "command.run", StringComparison.OrdinalIgnoreCase)) &&
                         !string.IsNullOrWhiteSpace(act.Fn))
                {
                    scriptRunCalls.Add((act.Type, act.Fn!.Trim(), actPath));
                }

                if (act.ItemFields is not null)
                {
                    foreach (string v in act.ItemFields.Values)
                    {
                        CheckExpr(v);
                    }
                }

                WalkActions(act.Then, $"{actPath}.then");
            }
        }

        void WalkNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null)
            {
                return;
            }

            CheckExpr(node.When);
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

            WalkActions(node.OnClick, $"{path}.onClick");
            WalkActions(node.OnSubmit, $"{path}.onSubmit");
            WalkActions(node.OnChange, $"{path}.onChange");
            WalkActions(node.OnDropActions, $"{path}.onDrop");
            WalkActions(node.OnRowClick, $"{path}.onRowClick");
            WalkActions(node.OnItemClick, $"{path}.onItemClick");
            WalkActions(node.OnReorder, $"{path}.onReorder");
            WalkActions(node.OnInlineCommit, $"{path}.onInlineCommit");
            WalkNode(node.ItemTemplate, $"{path}.itemTemplate");

            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                WalkNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                WalkNode(node.Children[i], $"{path}.children[{i}]");
            }
        }

        WalkNode(manifest.View, "$.view");
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            WalkNode(manifest.Popovers[i], $"$.popovers[{i}]");
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
                WalkActions(pa.OnClick, "$.capsule.primaryAction.onClick");
            }
        }

        if (manifest.OnDrop is { } drop)
        {
            CheckExpr(drop.Mode);
            WalkActions(drop.Actions, "$.onDrop.actions");
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

        void WalkActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null || foundKey is not null)
            {
                return;
            }

            for (int i = 0; i < actions.Count && foundKey is null; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                CheckExpr(act.Value, $"{actPath}.value");
                CheckExpr(act.Text, $"{actPath}.text");
                CheckExpr(act.Message, $"{actPath}.message");
                CheckExpr(act.Title, $"{actPath}.title");
                CheckExpr(act.Prompt, $"{actPath}.prompt");
                WalkActions(act.Then, $"{actPath}.then");
            }
        }

        void WalkNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null || foundKey is not null)
            {
                return;
            }

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

            WalkActions(node.OnClick, $"{path}.onClick");
            WalkActions(node.OnSubmit, $"{path}.onSubmit");
            WalkActions(node.OnChange, $"{path}.onChange");
            WalkActions(node.OnDropActions, $"{path}.onDrop");
            WalkActions(node.OnRowClick, $"{path}.onRowClick");
            WalkActions(node.OnItemClick, $"{path}.onItemClick");
            WalkActions(node.OnReorder, $"{path}.onReorder");
            WalkActions(node.OnInlineCommit, $"{path}.onInlineCommit");
            WalkNode(node.ItemTemplate, $"{path}.itemTemplate");

            for (int i = 0; i < node.HoverActions.Count && foundKey is null; i++)
            {
                WalkNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }

            for (int i = 0; i < node.Children.Count && foundKey is null; i++)
            {
                WalkNode(node.Children[i], $"{path}.children[{i}]");
            }
        }

        WalkNode(manifest.View, "$.view");
        for (int i = 0; i < manifest.Popovers.Count && foundKey is null; i++)
        {
            WalkNode(manifest.Popovers[i], $"$.popovers[{i}]");
        }

        if (manifest.Capsule is { } cap && foundKey is null)
        {
            CheckExpr(cap.Title, "$.capsule.title");
            CheckExpr(cap.Summary, "$.capsule.summary");
            CheckExpr(cap.Badge, "$.capsule.badge");
            if (cap.PrimaryAction is { } pa)
            {
                CheckExpr(pa.Tooltip, "$.capsule.primaryAction.tooltip");
                WalkActions(pa.OnClick, "$.capsule.primaryAction.onClick");
            }
        }

        firstKey = foundKey ?? string.Empty;
        firstPath = foundPath ?? "$.view";
        return foundKey is not null;
    }
}
