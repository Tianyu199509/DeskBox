using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Models;

/// <summary>
/// AOT-safe parser and validator for <see cref="CustomWidgetManifest"/>.
/// Uses <see cref="JsonDocument"/> directly with zero reflection.
/// </summary>
public static partial class CustomWidgetManifestParser
{
    public const int CurrentSchemaVersion = 1;

    public static Version CurrentAppVersion =>
        typeof(CustomWidgetManifestParser).Assembly.GetName().Version ?? new Version(1, 6, 0, 0);

    public static bool IsIncompatibleClientVersion(
        IReadOnlyList<CustomWidgetValidationDiagnostic>? diagnostics) =>
        diagnostics is not null &&
        diagnostics.Any(static d =>
            d.Code is CustomWidgetDiagnosticCodes.IncompatibleSchemaVersion
                   or CustomWidgetDiagnosticCodes.IncompatibleAppVersion);

    public static bool TryParseLooseVersion(string? rawVersion, out Version parsedVersion)
    {
        parsedVersion = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string clean = rawVersion.Trim();
        if (clean.StartsWith('v') || clean.StartsWith('V'))
        {
            clean = clean[1..];
        }

        int dashIdx = clean.IndexOf('-');
        if (dashIdx >= 0)
        {
            clean = clean[..dashIdx];
        }

        int plusIdx = clean.IndexOf('+');
        if (plusIdx >= 0)
        {
            clean = clean[..plusIdx];
        }

        if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int majorOnly) &&
            majorOnly >= 0)
        {
            parsedVersion = new Version(majorOnly, 0, 0, 0);
            return true;
        }

        if (Version.TryParse(clean, out Version? v) && v is not null)
        {
            parsedVersion = new Version(
                Math.Max(0, v.Major),
                Math.Max(0, v.Minor),
                Math.Max(0, v.Build),
                Math.Max(0, v.Revision));
            return true;
        }

        return false;
    }

    public static bool RequiresNewerClient(int? minSchemaVersion, string? minAppVersion, out string requirementLabel)
    {
        if (minSchemaVersion is { } schema && schema > CurrentSchemaVersion)
        {
            requirementLabel = $"Schema v{schema}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(minAppVersion))
        {
            if (!TryParseLooseVersion(minAppVersion, out Version reqVer))
            {
                requirementLabel = $"v{minAppVersion.Trim().TrimStart('v', 'V')}";
                return true;
            }

            Version currentNormalized = new(
                Math.Max(0, CurrentAppVersion.Major),
                Math.Max(0, CurrentAppVersion.Minor),
                Math.Max(0, CurrentAppVersion.Build),
                Math.Max(0, CurrentAppVersion.Revision));
            if (reqVer > currentNormalized)
            {
                requirementLabel = $"v{minAppVersion.Trim().TrimStart('v', 'V')}";
                return true;
            }
        }

        requirementLabel = string.Empty;
        return false;
    }

    private static readonly FrozenSet<string> SupportedNodeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Stack", "ZStack", "ModuleStack", "Grid", "ItemsGrid", "Wrap", "Scroll", "Card", "List", "Expander",
        "Tabs", "SelectorBar", "FlipView", "SplitView", "Popover",
        "Text", "RichText", "Markdown", "CodeBlock", "Input", "TextArea", "AutoSuggestInput",
        "Button", "IconButton", "SplitButton", "DropDownButton", "Toggle", "CheckBox",
        "Select", "Segmented", "RadioGroup", "Rating", "ColorPicker", "DatePicker", "TimePicker",
        "Badge", "InfoBadge", "InfoBar", "TeachingTip",
        "Slider", "NumberBox", "Image", "MediaPlayer", "Table",
        "ProgressBar", "ProgressRing", "RingGauge", "RadialGauge", "BarList", "DonutChart", "Chart", "Sparkline", "HeatmapGrid", "Canvas",
        "Divider", "Spacer",
        "StatCard", "CalendarGrid", "DropZone", "RecycleBin", "ControlTile",
        "ProgressRow", "KeyValueRow", "FileChip", "EmptyState"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> AllSupportedNodeTypes => SupportedNodeTypes;

    public static bool IsSupportedActionType(string? type) =>
        !string.IsNullOrWhiteSpace(type) &&
        SupportedActionTypes.GetAlternateLookup<ReadOnlySpan<char>>().Contains(type.AsSpan().Trim());

    private static readonly FrozenSet<string> SupportedActionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "state.set", "state.toggle", "state.increment",
        "settings.set",
        "collection.prepend", "collection.append", "collection.remove",
        "collection.update", "collection.clear", "collection.reorder", "collection.exportMarkdown",
        "popover.open", "popover.close", "popover.toggle",
        "tip.open", "tip.close", "tip.toggle",
        "audio.cue", "audio.play", "audio.pause", "audio.stop", "audio.setVolume", "audio.stopAll",
        "http.request",
        "file.open", "file.reveal", "file.transform", "file.pick", "file.writeText", "file.listDir", "file.manage",
        "clipboard.captureImage",
        "job.cancel", "command.cancel",
#if !DESKBOX_STORE
        "command.run",
#endif
        "text.transform", "launcher.open",
        "clipboard.copy", "url.open", "toast.show", "reminder.notify",
        "timer.toggle", "timer.reset",
        "pomodoro.toggle", "pomodoro.reset", "pomodoro.extend", "pomodoro.skip", "pomodoro.setPhase", "pomodoro.cyclePhase",
        "pomodoro.sedentaryConfirm", "pomodoro.sedentarySnooze",
        "countdown.pin", "countdown.delete", "countdown.upsert",
        "todo.add", "todo.toggle", "todo.remove", "todo.cyclePriority", "todo.clearCompleted",
        "todo.update", "todo.rolloverOverdue",
        "ai.refresh", "ai.selectProvider", "ai.run",
        "system.control", "system.openSetting",
        "deskbox.search", "app.openSearch",
        "source.refresh", "refreshSource",
        "script.run"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static CustomWidgetParseResult Parse(string json)
    {
        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostics.Add(new("$", "Manifest JSON is empty."));
            return new(null, diagnostics);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new(
                $"line:{(ex.LineNumber ?? 0) + 1}",
                $"JSON syntax error: {ex.Message}"));
            return new(null, diagnostics);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new("$", "Root element must be a JSON object."));
                return new(null, diagnostics);
            }

            int schemaVersion = CurrentSchemaVersion;
            if (root.TryGetProperty("schemaVersion", out JsonElement schemaVerEl) &&
                schemaVerEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (schemaVerEl.ValueKind == JsonValueKind.Number && schemaVerEl.TryGetInt32(out int parsedSchemaVer))
                {
                    schemaVersion = parsedSchemaVer;
                }
                else if (schemaVerEl.ValueKind == JsonValueKind.String &&
                         int.TryParse(schemaVerEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStrVer))
                {
                    schemaVersion = parsedStrVer;
                }
                else
                {
                    schemaVersion = 0;
                }
            }
            string id = GetString(root, "id", "custom.widget");
            string? packageId = TryGetString(root, "packageId");
            string name = GetString(root, "name", DeskBox.Services.Localized.T("CustomWidget.DefaultName"));
            string version = GetString(root, "version", "1.0.0");
            int? minSchemaVersion = root.TryGetProperty("minSchemaVersion", out JsonElement minSchemaEl) &&
                                    minSchemaEl.ValueKind == JsonValueKind.Number &&
                                    minSchemaEl.TryGetInt32(out int parsedMinSchema)
                ? parsedMinSchema
                : null;
            string? minAppVersion = TryGetString(root, "minAppVersion");
            string author = GetString(root, "author", "DeskBox");
            string description = GetString(root, "description", string.Empty);
            string icon = NormalizeGlyphWithDiagnostics(TryGetString(root, "icon"), "$.icon", diagnostics, "\uE71D");
            string origin = CustomWidgetOriginKinds.Normalize(TryGetString(root, "origin"));
            bool isCustomized = GetBool(root, "isCustomized", false);
            string? forkedFromId = TryGetString(root, "forkedFromId");
            string? forkedFromVersion = TryGetString(root, "forkedFromVersion");
            string? createdAtUtc = TryGetString(root, "createdAtUtc");
            string? updatedAtUtc = TryGetString(root, "updatedAtUtc");
            IReadOnlyList<string> tags = ParseStringArray(root, "tags");
            IReadOnlyList<string> capabilities = ParseStringArray(root, "capabilities");

            if (schemaVersion != CurrentSchemaVersion)
            {
                diagnostics.Add(new(
                    "$.schemaVersion",
                    $"Unsupported schemaVersion {schemaVersion}. DeskBox Custom Widgets require \"schemaVersion\": {CurrentSchemaVersion} with the canonical Flat Element Graph (top-level \"root\" and \"elements\" map).",
                    IsError: true,
                    Code: "UnsupportedSchemaVersion"));
            }

            if (minSchemaVersion is { } reqSchema && reqSchema > CurrentSchemaVersion)
            {
                diagnostics.Add(new(
                    "$.minSchemaVersion",
                    DeskBox.Services.Localized.Format("CustomWidget.ClientUpgrade.AppTooOld", $"Schema v{reqSchema}"),
                    IsError: true,
                    Code: CustomWidgetDiagnosticCodes.IncompatibleSchemaVersion));
            }

            if (RequiresNewerClient(null, minAppVersion, out string reqAppVerLabel))
            {
                diagnostics.Add(new(
                    "$.minAppVersion",
                    DeskBox.Services.Localized.Format("CustomWidget.ClientUpgrade.AppTooOld", reqAppVerLabel),
                    IsError: true,
                    Code: CustomWidgetDiagnosticCodes.IncompatibleAppVersion));
            }

            CustomWidgetSizeSpec minSize = ParseSize(root, "minSize", 260, 220);
            CustomWidgetSizeSpec defaultSize = ParseSize(root, "defaultSize", 360, 460);

            if (root.TryGetProperty("i18n", out _))
            {
                diagnostics.Add(new(
                    "$.i18n",
                    "Inline \"i18n\" inside manifest.json is not allowed. Store per-locale dictionaries under locales/<culture>.json.",
                    IsError: true,
                    Code: "InlineI18nDisallowed"));
            }

            int strayRootCount = 0;
            foreach (string strayKey in new[]
            {
                "type", "when", "$when", "visible", "children", "itemTemplate",
                "valueField", "titleField", "subtitleField", "emptyState",
                "orientation", "spacing", "padding"
            })
            {
                if (!root.TryGetProperty(strayKey, out _))
                {
                    continue;
                }

                diagnostics.Add(new(
                    $"$.{strayKey}",
                    $"UI node property \"{strayKey}\" is not allowed at the manifest root ($); move UI node properties into $.elements.<id>.",
                    IsError: true,
                    Code: CustomWidgetStrictAuthoringRules.StrayViewPropertiesAtManifestRoot));
                strayRootCount++;
                if (strayRootCount >= 2)
                {
                    break;
                }
            }

            var settings = ParseSettings(root, diagnostics);
            IReadOnlyDictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? settingOptionsByKey =
                BuildSettingOptionsLookup(settings);
            (var state, var stateJson) = ParseState(root);
            var collections = ParseCollections(root, diagnostics);
            var sources = ParseSources(root, diagnostics);
            var script = ParseScript(root, diagnostics);
            var onDrop = ParseOnDrop(root, diagnostics);
            var capsule = ParseCapsule(root, diagnostics);

            bool hasLegacyView = root.TryGetProperty("view", out _);
            if (hasLegacyView)
            {
                diagnostics.Add(new(
                    "$.view",
                    "Legacy nested 'view' tree is not supported. Use canonical Flat Element Graph (schemaVersion: 1) with top-level 'root' and 'elements' map.",
                    IsError: true,
                    Code: "LegacyViewNotSupported"));
            }

            bool hasElementsMap = root.TryGetProperty("elements", out JsonElement elementsMapEl) &&
                                  elementsMapEl.ValueKind == JsonValueKind.Object;
            FlatGraphParseContext? flatGraph = hasElementsMap
                ? BuildFlatGraphContext(elementsMapEl, diagnostics)
                : null;

            var popovers = ParseRootPopovers(root, diagnostics, settingOptionsByKey, flatGraph);

            string? rootElemId = root.TryGetProperty("root", out JsonElement rootIdEl) &&
                                 rootIdEl.ValueKind == JsonValueKind.String
                ? rootIdEl.GetString()?.Trim()
                : null;

            CustomWidgetNodeSpec view;
            if (flatGraph is not null)
            {
                if (string.IsNullOrWhiteSpace(rootElemId))
                {
                    diagnostics.Add(new(
                        "$.root",
                        "Canonical Flat Element Graph manifest requires a non-empty string 'root' pointing to an element ID in 'elements'.",
                        IsError: true,
                        Code: "ElementGraphMissingRoot"));
                    view = new CustomWidgetNodeSpec
                    {
                        Type = "EmptyState",
                        Title = name,
                        Subtitle = "Missing 'root' element ID in manifest.json"
                    };
                }
                else
                {
                    view = ResolveFlatElement(
                        rootElemId,
                        "$.root",
                        flatGraph,
                        diagnostics,
                        depth: 0,
                        settingOptionsByKey);
                    ValidateRootViewLayout(view, defaultSize, diagnostics);
                }

                foreach (string declaredElemId in flatGraph.Elements.Keys)
                {
                    if (!flatGraph.VisitedIds.Contains(declaredElemId))
                    {
                        diagnostics.Add(new(
                            $"$.elements.{declaredElemId}",
                            $"Element '{declaredElemId}' is declared in 'elements' but is not reachable from 'root' or any popover. Attach it to a container or remove the unused element.",
                            IsError: false,
                            Code: "ElementGraphOrphanElement"));
                    }
                }
            }
            else
            {
                if (!hasLegacyView)
                {
                    diagnostics.Add(new(
                        "$.elements",
                        "Missing required top-level 'elements' object map. Use canonical Flat Element Graph (schemaVersion: 1) with 'root' and 'elements'.",
                        IsError: true,
                        Code: "MissingElementsMap"));
                    if (string.IsNullOrWhiteSpace(rootElemId))
                    {
                        diagnostics.Add(new(
                            "$.root",
                            "Missing required top-level string 'root' element ID pointing to an entry in 'elements'.",
                            IsError: true,
                            Code: "ElementGraphMissingRoot"));
                    }
                }

                view = new CustomWidgetNodeSpec
                {
                    Type = "EmptyState",
                    Title = name,
                    Subtitle = "Missing 'root' and 'elements' definition in manifest.json"
                };
            }

            var manifest = new CustomWidgetManifest
            {
                SchemaVersion = schemaVersion,
                Id = id,
                PackageId = packageId,
                Name = name,
                Version = version,
                MinAppVersion = minAppVersion,
                Author = author,
                Description = description,
                Icon = icon,
                Origin = origin,
                IsCustomized = isCustomized,
                ForkedFromId = forkedFromId,
                ForkedFromVersion = forkedFromVersion,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Tags = tags,
                Capabilities = capabilities,
                MinSize = minSize,
                DefaultSize = defaultSize,
                Settings = settings,
                State = state,
                StateJson = stateJson,
                Collections = collections,
                Sources = sources,
                Script = script,
                OnDrop = onDrop,
                Capsule = capsule,
                Popovers = popovers,
                View = view,
                ParsedBodyDeviceFields = DeskBox.Services.DeviceStateCapabilityProvider.ScanDeviceTokensInManifestText(json)
            };

            return new(manifest, diagnostics);
        }
    }

    [GeneratedRegex(@"\bstate\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StateRefRegex();

    [GeneratedRegex(@"\$settings\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SettingRefRegex();

    [GeneratedRegex(@"\$secrets\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecretRefRegex();

    [GeneratedRegex(@"\bcollections\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionRefRegex();

    [GeneratedRegex(@"\$i18n\.([A-Za-z0-9_.-]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex I18nRefRegex();

    [GeneratedRegex(@"\bt\(\s*['""]([A-Za-z0-9_.-]+)['""]", RegexOptions.CultureInvariant)]
    private static partial Regex TFuncRefRegex();

    [GeneratedRegex(@"\bctx\.settings(?:\.([A-Za-z0-9_]+)|\[\s*['""]([A-Za-z0-9_]+)['""]\s*\])", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxSettingRefRegex();

    [GeneratedRegex(@"\bctx\.state(?:\.([A-Za-z0-9_]+)|\[\s*['""]([A-Za-z0-9_]+)['""]\s*\])", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxStateRefRegex();

    [GeneratedRegex(@"\.([A-Za-z0-9_]+)\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex JsPropAssignRegex();

    /// <summary>
    /// Performs full structural validation plus static analysis of state keys,
    /// settings keys, locale keys, and collection references across expressions, bindings, and actions.
    /// </summary>
    public static CustomWidgetParseResult ValidateWithRuntimeHints(
        string json,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales) =>
        ValidateWithRuntimeHints(json, logicJs, locales?.Locales);

    public static CustomWidgetParseResult ValidateWithRuntimeHints(
        string json,
        string? logicJs = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? localeMap = null)
    {
        CustomWidgetParseResult baseResult = Parse(json);
        if (baseResult.Manifest is not { } manifest)
        {
            return baseResult;
        }

        if (localeMap is { Count: > 0 })
        {
            manifest = manifest.WithLocales(localeMap);
        }

        var diagnostics = new List<CustomWidgetValidationDiagnostic>(baseResult.Diagnostics);
        var knownStateKeys = new HashSet<string>(manifest.State.Keys, StringComparer.Ordinal);
        var knownSettingsKeys = new HashSet<string>(
            manifest.Settings.Select(static s => s.Key),
            StringComparer.Ordinal);
        var referencedSettingsKeys = new HashSet<string>(StringComparer.Ordinal);
        var knownSecretKeys = new HashSet<string>(
            manifest.Settings
                .Where(static s => string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(static s => s.Key),
            StringComparer.Ordinal);
        var knownCollections = new HashSet<string>(manifest.Collections.Keys, StringComparer.Ordinal);
        var knownI18nKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, string> localeDict in manifest.I18n.Values)
        {
            foreach (string k in localeDict.Keys)
            {
                knownI18nKeys.Add(k);
            }
        }

        bool hasUninspectedScript = manifest.Script is not null && string.IsNullOrWhiteSpace(logicJs);
        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            foreach (Match m in JsCtxSettingRefRegex().Matches(logicJs))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    referencedSettingsKeys.Add(key);
                }
            }

            if (logicJs.Contains("ctx.settings", StringComparison.Ordinal) && knownSettingsKeys.Count > 0)
            {
                foreach (Match tokenMatch in JsIdentifierTokenRegex().Matches(logicJs))
                {
                    string token = tokenMatch.Groups[1].Value;
                    if (knownSettingsKeys.Contains(token))
                    {
                        referencedSettingsKeys.Add(token);
                    }
                }
            }

            foreach (Match m in JsCtxStateRefRegex().Matches(logicJs))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    knownStateKeys.Add(key);
                }
            }

            if (logicJs.Contains("ctx.state", StringComparison.Ordinal))
            {
                foreach (Match m in JsPropAssignRegex().Matches(logicJs))
                {
                    if (m.Groups[1].Success)
                    {
                        knownStateKeys.Add(m.Groups[1].Value);
                    }
                }
            }
        }

        void CollectWritersFromActions(IReadOnlyList<CustomWidgetActionSpec>? actions)
        {
            if (actions is null)
            {
                return;
            }

            foreach (CustomWidgetActionSpec act in actions)
            {
                if (!string.IsNullOrWhiteSpace(act.Key) &&
                    act.Type.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
                {
                    string normalized = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? act.Key["state.".Length..]
                        : act.Key;
                    knownStateKeys.Add(normalized);
                }
                else if (string.Equals(act.Type, "ai.run", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("aiStatus");
                    knownStateKeys.Add("aiError");
                    knownStateKeys.Add("aiUpdatedAt");
                    if (act.StateMap is not null)
                    {
                        foreach (string stateKey in act.StateMap.Keys)
                        {
                            string normalized = stateKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                                ? stateKey["state.".Length..]
                                : stateKey;
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                knownStateKeys.Add(normalized);
                            }
                        }
                    }
                }
                else if (string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("httpStatus");
                    knownStateKeys.Add("httpOk");
                    knownStateKeys.Add("httpError");
                    knownStateKeys.Add("httpStatusCode");
                    knownStateKeys.Add("httpUpdatedAt");
                    if (!string.IsNullOrWhiteSpace(act.Key))
                    {
                        string normOut = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? act.Key["state.".Length..]
                            : act.Key;
                        if (!string.IsNullOrWhiteSpace(normOut))
                        {
                            knownStateKeys.Add(normOut);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(act.Collection) && !act.Collection.Contains('{'))
                    {
                        knownCollections.Add(act.Collection);
                    }
                    if (act.StateMap is not null)
                    {
                        foreach (string stateKey in act.StateMap.Keys)
                        {
                            string normalized = stateKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                                ? stateKey["state.".Length..]
                                : stateKey;
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                knownStateKeys.Add(normalized);
                            }
                        }
                    }
                }
                else if (string.Equals(act.Type, "command.run", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("commandStatus");
                    knownStateKeys.Add("commandError");
                    knownStateKeys.Add("commandExitCode");
                    knownStateKeys.Add("commandStdout");
                    knownStateKeys.Add("commandStderr");
                    if (!string.IsNullOrWhiteSpace(act.Key))
                    {
                        string normOut = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? act.Key["state.".Length..]
                            : act.Key;
                        if (!string.IsNullOrWhiteSpace(normOut))
                        {
                            knownStateKeys.Add(normOut);
                        }
                    }
                    if (act.StateMap is not null)
                    {
                        foreach (string stateKey in act.StateMap.Keys)
                        {
                            string normalized = stateKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                                ? stateKey["state.".Length..]
                                : stateKey;
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                knownStateKeys.Add(normalized);
                            }
                        }
                    }
                }
                else if (string.Equals(act.Type, "file.writeText", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(act.Type, "file.listDir", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(act.Type, "file.manage", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(act.Type, "clipboard.captureImage", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("fileWriteStatus");
                    knownStateKeys.Add("fileWriteError");
                    knownStateKeys.Add("fileStatus");
                    knownStateKeys.Add("fileError");
                    knownStateKeys.Add("clipboardImagePath");
                    if (!string.IsNullOrWhiteSpace(act.Key))
                    {
                        string normalized = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? act.Key["state.".Length..]
                            : act.Key;
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            knownStateKeys.Add(normalized);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(act.Collection) && !act.Collection.Contains('{'))
                    {
                        knownCollections.Add(act.Collection);
                    }
                }
                else if (string.Equals(act.Type, "file.transform", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(act.Type, "text.transform", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("transformStatus");
                    knownStateKeys.Add("transformOutput");
                    knownStateKeys.Add("transformError");
                    if (!string.IsNullOrWhiteSpace(act.Key))
                    {
                        string normalized = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? act.Key["state.".Length..]
                            : act.Key;
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            knownStateKeys.Add(normalized);
                        }
                    }
                }

                CollectWritersFromActions(act.Then);
            }
        }

        void CollectWritersFromNode(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(node.Bind))
            {
                if (node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
                {
                    string setKey = node.Bind["$settings.".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(setKey))
                    {
                        referencedSettingsKeys.Add(setKey);
                    }
                }
                else if (node.Bind.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
                {
                    string setKey = node.Bind["settings.".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(setKey))
                    {
                        referencedSettingsKeys.Add(setKey);
                    }
                }
                else
                {
                    string bindKey = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? node.Bind["state.".Length..]
                        : node.Bind;
                    if (!string.IsNullOrWhiteSpace(bindKey))
                    {
                        knownStateKeys.Add(bindKey);
                    }
                }
            }

            CollectWritersFromActions(node.OnClick);
            CollectWritersFromActions(node.OnSubmit);
            CollectWritersFromActions(node.OnChange);
            CollectWritersFromActions(node.OnDropActions);
            CollectWritersFromActions(node.OnRowClick);
            CollectWritersFromActions(node.OnItemClick);
            CollectWritersFromActions(node.OnReorder);
            CollectWritersFromActions(node.OnInlineCommit);
            CollectWritersFromActions(node.OnPointerDown);
            CollectWritersFromActions(node.OnPointerMove);
            CollectWritersFromActions(node.OnPointerUp);
            foreach (CustomWidgetMenuItemSpec mi in node.MenuItems)
            {
                CollectWritersFromActions(mi.OnClick);
            }
            foreach (CustomWidgetMenuItemSpec ci in node.ContextMenu)
            {
                CollectWritersFromActions(ci.OnClick);
            }
            CollectWritersFromNode(node.ActionButton);
            CollectWritersFromNode(node.Pane);
            CollectWritersFromNode(node.ItemTemplate);
            foreach (CustomWidgetNodeSpec h in node.HoverActions)
            {
                CollectWritersFromNode(h);
            }

            foreach (CustomWidgetNodeSpec c in node.Children)
            {
                CollectWritersFromNode(c);
            }
        }

        CollectWritersFromNode(manifest.View);
        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            CollectWritersFromNode(pop);
        }
        CollectWritersFromActions(manifest.OnDrop?.Actions);
        CollectWritersFromActions(manifest.Capsule?.PrimaryAction?.OnClick);

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase))
            {
                knownStateKeys.Add("feedTitle");
                knownStateKeys.Add("feedCount");
                knownStateKeys.Add("topHeadline");
                knownStateKeys.Add("lastUpdated");
                knownStateKeys.Add("networkStatus");
                knownStateKeys.Add("networkError");
                knownStateKeys.Add("sourceStatus");
                knownStateKeys.Add("sourceError");
                knownStateKeys.Add("sourceUpdatedAt");
                if (!string.IsNullOrWhiteSpace(src.TargetCollection))
                {
                    knownCollections.Add(src.TargetCollection);
                }
                else if (string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase))
                {
                    knownCollections.Add("items");
                }

                if (src.StateMap is not null)
                {
                    foreach (string stateKey in src.StateMap.Keys)
                    {
                        string normalized = stateKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? stateKey["state.".Length..]
                            : stateKey;
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            knownStateKeys.Add(normalized);
                        }
                    }
                }
            }
            else if (string.Equals(src.Type, "fileText", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(src.Type, "command", StringComparison.OrdinalIgnoreCase))
            {
                knownStateKeys.Add("sourceStatus");
                knownStateKeys.Add("sourceError");
                knownStateKeys.Add("sourceUpdatedAt");
                if (!string.IsNullOrWhiteSpace(src.Id))
                {
                    knownStateKeys.Add($"sourceStatus.{src.Id}");
                    knownStateKeys.Add($"sourceError.{src.Id}");
                }
                if (!string.IsNullOrWhiteSpace(src.TargetCollection) && !src.TargetCollection.Contains('{'))
                {
                    knownCollections.Add(src.TargetCollection);
                }
                if (string.Equals(src.Type, "fileText", StringComparison.OrdinalIgnoreCase))
                {
                    knownStateKeys.Add("fileText");
                    knownStateKeys.Add("fileLineCount");
                    knownStateKeys.Add("fileStatus");
                    knownStateKeys.Add("fileError");
                    knownStateKeys.Add("fileContent");
                    if (!string.IsNullOrWhiteSpace(src.Id))
                    {
                        knownStateKeys.Add(src.Id!);
                        knownStateKeys.Add($"fileText.{src.Id}");
                        knownStateKeys.Add($"fileLineCount.{src.Id}");
                    }
                }
                else
                {
                    knownStateKeys.Add("commandStatus");
                    knownStateKeys.Add("commandError");
                    knownStateKeys.Add("commandExitCode");
                    knownStateKeys.Add("commandStdout");
                    knownStateKeys.Add("commandStderr");
                    if (!string.IsNullOrWhiteSpace(src.Id))
                    {
                        knownStateKeys.Add(src.Id!);
                        knownStateKeys.Add($"commandStdout.{src.Id}");
                        knownStateKeys.Add($"commandStderr.{src.Id}");
                        knownStateKeys.Add($"commandExitCode.{src.Id}");
                    }
                }

                if (src.StateMap is not null)
                {
                    foreach (string stateKey in src.StateMap.Keys)
                    {
                        string normalized = stateKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? stateKey["state.".Length..]
                            : stateKey;
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            knownStateKeys.Add(normalized);
                        }
                    }
                }
            }
            else if (string.Equals(src.Type, "weather", StringComparison.OrdinalIgnoreCase))
            {
                knownCollections.Add(!string.IsNullOrWhiteSpace(src.TargetCollection) ? src.TargetCollection : "forecast");
            }
            else if (string.Equals(src.Type, "countdown", StringComparison.OrdinalIgnoreCase))
            {
                knownCollections.Add("events");
            }
            else if (string.Equals(src.Type, "storageDrives", StringComparison.OrdinalIgnoreCase))
            {
                knownCollections.Add(!string.IsNullOrWhiteSpace(src.TargetCollection) ? src.TargetCollection : "drives");
            }
            else if (string.Equals(src.Type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase))
            {
                knownStateKeys.Add("sourceStatus");
                knownStateKeys.Add("sourceError");
                knownStateKeys.Add("sourceUpdatedAt");
                knownStateKeys.Add("folderItemCount");
                if (!string.IsNullOrWhiteSpace(src.Id))
                {
                    knownStateKeys.Add($"sourceStatus.{src.Id}");
                    knownStateKeys.Add($"sourceError.{src.Id}");
                    knownStateKeys.Add($"folderItemCount.{src.Id}");
                }
                knownCollections.Add(!string.IsNullOrWhiteSpace(src.TargetCollection) ? src.TargetCollection : "items");
            }
            else if (string.Equals(src.Type, "todo", StringComparison.OrdinalIgnoreCase))
            {
                string todoCol = !string.IsNullOrWhiteSpace(src.TargetCollection) ? src.TargetCollection : "todos";
                knownCollections.Add(todoCol);
                if (!string.IsNullOrWhiteSpace(src.TargetCollection) &&
                    !string.Equals(src.TargetCollection, "todos", StringComparison.Ordinal) &&
                    !manifest.Collections.ContainsKey(src.TargetCollection))
                {
                    diagnostics.Add(new(
                        "$.sources",
                        $"Todo source targets custom collection '{src.TargetCollection}' which is not declared in 'collections'.",
                        IsError: false));
                }
            }
        }

        void InspectExpression(string? expr, string path)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            DeskBox.Services.CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression(expr, path, diagnostics);

            foreach (Match m in StateRefRegex().Matches(expr))
            {
                string key = m.Groups[1].Value;
                if (!hasUninspectedScript && !knownStateKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared state key 'state.{key}'. Add '{key}' to root \"state\" or bind/write it in an action.",
                        IsError: false));
                }
            }

            foreach (Match m in SettingRefRegex().Matches(expr))
            {
                string key = m.Groups[1].Value;
                referencedSettingsKeys.Add(key);
                if (!knownSettingsKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared setting '$settings.{key}'. Declare '{key}' in root \"settings\".",
                        IsError: false));
                }
            }

            foreach (Match m in SecretRefRegex().Matches(expr))
            {
                string key = m.Groups[1].Value;
                referencedSettingsKeys.Add(key);
                if (!knownSecretKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared secret '$secrets.{key}'. Declare '{key}' with \"type\": \"secret\" in root \"settings\".",
                        IsError: false));
                }
            }

            foreach (Match m in CollectionRefRegex().Matches(expr))
            {
                string col = m.Groups[1].Value;
                if (!knownCollections.Contains(col))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared collection 'collections.{col}'. Declare '{col}' in root \"collections\".",
                        IsError: false));
                }
            }

            if (knownI18nKeys.Count > 0)
            {
                foreach (Match m in I18nRefRegex().Matches(expr))
                {
                    string i18nKey = m.Groups[1].Value;
                    if (!knownI18nKeys.Contains(i18nKey))
                    {
                        diagnostics.Add(new(
                            path,
                            $"Expression references undeclared i18n key '$i18n.{i18nKey}'. Declare '{i18nKey}' in locales/<culture>.json.",
                            IsError: false));
                    }
                }

                foreach (Match m in TFuncRefRegex().Matches(expr))
                {
                    string tKey = m.Groups[1].Value;
                    if (!knownI18nKeys.Contains(tKey))
                    {
                        diagnostics.Add(new(
                            path,
                            $"Expression references undeclared i18n key '{tKey}' in t('{tKey}'). Declare '{tKey}' in locales/<culture>.json.",
                            IsError: false));
                    }
                }
            }
        }

        void InspectActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                InspectExpression(act.Value, $"{actPath}.value");
                InspectExpression(act.TargetId, $"{actPath}.targetId");
                InspectExpression(act.Format, $"{actPath}.format");
                InspectExpression(act.Title, $"{actPath}.title");
                InspectExpression(act.Message, $"{actPath}.message");
                InspectExpression(act.Text, $"{actPath}.text");
                InspectExpression(act.Url, $"{actPath}.url");
                InspectExpression(act.Path, $"{actPath}.path");
                InspectExpression(act.Destination, $"{actPath}.destination");
                InspectExpression(act.Pattern, $"{actPath}.pattern");
                InspectExpression(act.JobKey, $"{actPath}.jobKey");
                InspectExpression(act.Guard, $"{actPath}.guard");
                InspectExpression(act.Prompt, $"{actPath}.prompt");
                InspectExpression(act.Arg, $"{actPath}.arg");
                InspectExpression(act.Body, $"{actPath}.body");
                InspectExpression(act.WorkingDirectory, $"{actPath}.workingDirectory");
                InspectExpression(act.PopoverId, $"{actPath}.popoverId");
                InspectExpression(act.FromIndex, $"{actPath}.fromIndex");
                InspectExpression(act.ToIndex, $"{actPath}.toIndex");
                InspectExpression(act.Volume, $"{actPath}.volume");
                InspectExpression(act.DueIso, $"{actPath}.dueIso");
                InspectExpression(act.Priority, $"{actPath}.priority");
                if (act.Headers is not null)
                {
                    foreach ((string hk, string hv) in act.Headers)
                    {
                        InspectExpression(hv, $"{actPath}.headers.{hk}");
                    }
                }
                if (act.Args is not null)
                {
                    foreach ((string ak, string av) in act.Args)
                    {
                        InspectExpression(av, $"{actPath}.args.{ak}");
                    }
                }
                if (act.CommandArgs is not null)
                {
                    for (int ai = 0; ai < act.CommandArgs.Count; ai++)
                    {
                        InspectExpression(act.CommandArgs[ai], $"{actPath}.commandArgs[{ai}]");
                    }
                }

                if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(act.Fn))
                    {
                        diagnostics.Add(new(
                            $"{actPath}.fn",
                            "Action 'script.run' is missing required 'fn' (exported function name in logic.js).",
                            IsError: true));
                    }
                }

                if (string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(act.Url))
                    {
                        diagnostics.Add(new(
                            $"{actPath}.url",
                            "Action 'http.request' is missing required 'url'.",
                            IsError: true));
                    }
                }

                if (string.Equals(act.Type, "file.writeText", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(act.Path))
                    {
                        diagnostics.Add(new(
                            $"{actPath}.path",
                            "Action 'file.writeText' is missing required 'path'.",
                            IsError: true));
                    }
                }

                if (string.Equals(act.Type, "ai.run", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(act.Prompt) &&
                        string.IsNullOrWhiteSpace(act.Text) &&
                        string.IsNullOrWhiteSpace(act.Value))
                    {
                        diagnostics.Add(new(
                            actPath,
                            "Action 'ai.run' is missing required 'prompt'.",
                            IsError: true));
                    }

                    if (act.StateMap is not { Count: > 0 } &&
                        string.IsNullOrWhiteSpace(act.Collection) &&
                        string.IsNullOrWhiteSpace(act.OnComplete) &&
                        string.IsNullOrWhiteSpace(act.Fn) &&
                        string.IsNullOrWhiteSpace(act.Key))
                    {
                        diagnostics.Add(new(
                            actPath,
                            "Action 'ai.run' specifies neither 'stateMap' nor 'collection'; AI output will not be stored.",
                            IsError: false));
                    }

                    if (!string.IsNullOrWhiteSpace(act.Collection) &&
                        !knownCollections.Contains(act.Collection))
                    {
                        diagnostics.Add(new(
                            actPath,
                            $"Action 'ai.run' targets undeclared collection '{act.Collection}'. Add it to root \"collections\".",
                            IsError: false));
                    }
                }

                if (act.Type.StartsWith("collection.", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(act.Collection))
                    {
                        diagnostics.Add(new(
                            actPath,
                            $"Action '{act.Type}' is missing required 'collection' name.",
                            IsError: true));
                    }
                    else if (!knownCollections.Contains(act.Collection))
                    {
                        diagnostics.Add(new(
                            actPath,
                            $"Action '{act.Type}' targets undeclared collection '{act.Collection}'. Add it to root \"collections\".",
                            IsError: false));
                    }
                }

                if (act.ItemFields is not null)
                {
                    foreach ((string k, string v) in act.ItemFields)
                    {
                        InspectExpression(v, $"{actPath}.item.{k}");
                    }
                }

                InspectActions(act.Then, $"{actPath}.then");
            }
        }

        bool usesPomodoroTokens = false;
        bool usesCountdownTokens = false;
        bool usesAiMonitorTokens = false;
        bool usesProcessTokens = false;

        void InspectNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null)
            {
                return;
            }

            static void ScanDomainTokens(
                string? raw,
                ref bool pomo,
                ref bool cd,
                ref bool ai,
                ref bool proc)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    return;
                }
                if (raw.Contains("$pomodoro.", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("$pomo.", StringComparison.OrdinalIgnoreCase))
                {
                    pomo = true;
                }
                if (raw.Contains("$countdown.", StringComparison.OrdinalIgnoreCase))
                {
                    cd = true;
                }
                if (raw.Contains("$ai.", StringComparison.OrdinalIgnoreCase))
                {
                    ai = true;
                }
                if (raw.Contains("$sys.topProcesses", StringComparison.OrdinalIgnoreCase) ||
                    raw.Contains("$sys.processes", StringComparison.OrdinalIgnoreCase))
                {
                    proc = true;
                }
            }

            ScanDomainTokens(node.Text, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);
            ScanDomainTokens(node.Title, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);
            ScanDomainTokens(node.Subtitle, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);
            ScanDomainTokens(node.Value, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);
            ScanDomainTokens(node.Items, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);
            ScanDomainTokens(node.Collection, ref usesPomodoroTokens, ref usesCountdownTokens, ref usesAiMonitorTokens, ref usesProcessTokens);

            InspectExpression(node.When, $"{path}.when");
            InspectExpression(node.Text, $"{path}.text");
            InspectExpression(node.Title, $"{path}.title");
            InspectExpression(node.Subtitle, $"{path}.subtitle");
            InspectExpression(node.Placeholder, $"{path}.placeholder");
            InspectExpression(node.Value, $"{path}.value");
            InspectExpression(node.Icon, $"{path}.icon");
            InspectExpression(node.Tooltip, $"{path}.tooltip");
            InspectExpression(node.Src, $"{path}.src");
            InspectExpression(node.Poster, $"{path}.poster");
            InspectExpression(node.Stretch, $"{path}.stretch");
            InspectExpression(node.Volume, $"{path}.volume");
            InspectExpression(node.ShowTransportExpression, $"{path}.showTransportControls");
            InspectExpression(node.AutoPlayExpression, $"{path}.autoPlay");
            InspectExpression(node.MutedExpression, $"{path}.muted");
            InspectExpression(node.LoopExpression, $"{path}.loop");
            InspectExpression(node.IsOpen, $"{path}.isOpen");
            InspectExpression(node.CloseButtonText, $"{path}.closeButtonText");
            InspectExpression(node.FileName, $"{path}.fileName");
            InspectExpression(node.FilePath, $"{path}.filePath");
            InspectExpression(node.StorageMode, $"{path}.storageMode");
            InspectExpression(node.Color, $"{path}.color");
            InspectExpression(node.Tone, $"{path}.tone");
            InspectExpression(node.Variant, $"{path}.variant");
            InspectExpression(node.Backdrop, $"{path}.backdrop");
            InspectExpression(node.Background, $"{path}.background");
            InspectExpression(node.TextColor, $"{path}.textColor");
            InspectExpression(node.BorderColor, $"{path}.borderColor");
            InspectExpression(node.BorderWidth, $"{path}.borderWidth");
            InspectExpression(node.FontWeight, $"{path}.fontWeight");
            InspectExpression(node.OpacityExpression, $"{path}.opacity");
            InspectExpression(node.CenterTitle, $"{path}.centerTitle");
            InspectExpression(node.CenterValue, $"{path}.centerValue");
            InspectExpression(node.ElementsSource, $"{path}.elements");
            InspectExpression(node.SuggestionsSource, $"{path}.suggestions");
            InspectExpression(node.Progress, $"{path}.progress");
            InspectExpression(node.Indeterminate, $"{path}.indeterminate");
            InspectExpression(node.WarningThresholdExpression, $"{path}.warningThreshold");
            InspectExpression(node.DangerThresholdExpression, $"{path}.dangerThreshold");
            InspectExpression(node.Filter, $"{path}.filter");
            InspectExpression(node.FilterEquals, $"{path}.filterEquals");
            InspectExpression(node.Columns, $"{path}.columns");
            InspectExpression(node.Rows, $"{path}.rows");

            if (node.EmptyState is { } empty)
            {
                InspectExpression(empty.Title, $"{path}.emptyState.title");
                InspectExpression(empty.Subtitle, $"{path}.emptyState.subtitle");
            }

            for (int i = 0; i < node.Spans.Count; i++)
            {
                CustomWidgetTextSpanSpec sp = node.Spans[i];
                InspectExpression(sp.Text, $"{path}.spans[{i}].text");
                InspectExpression(sp.Color, $"{path}.spans[{i}].color");
                InspectExpression(sp.Href, $"{path}.spans[{i}].href");
            }

            for (int i = 0; i < node.MenuItems.Count; i++)
            {
                CustomWidgetMenuItemSpec mi = node.MenuItems[i];
                InspectExpression(mi.Label, $"{path}.menuItems[{i}].label");
                InspectExpression(mi.Icon, $"{path}.menuItems[{i}].icon");
                InspectExpression(mi.When, $"{path}.menuItems[{i}].when");
                InspectActions(mi.OnClick, $"{path}.menuItems[{i}].onClick");
            }

            for (int i = 0; i < node.ContextMenu.Count; i++)
            {
                CustomWidgetMenuItemSpec ci = node.ContextMenu[i];
                InspectExpression(ci.Label, $"{path}.contextMenu[{i}].label");
                InspectExpression(ci.Icon, $"{path}.contextMenu[{i}].icon");
                InspectExpression(ci.When, $"{path}.contextMenu[{i}].when");
                InspectActions(ci.OnClick, $"{path}.contextMenu[{i}].onClick");
            }

            for (int i = 0; i < node.Options.Count; i++)
            {
                InspectExpression(node.Options[i].Label, $"{path}.options[{i}].label");
            }

            for (int i = 0; i < node.TableColumns.Count; i++)
            {
                InspectExpression(node.TableColumns[i].Header, $"{path}.columns[{i}].header");
            }

            for (int i = 0; i < node.Series.Count; i++)
            {
                InspectExpression(node.Series[i].Name, $"{path}.series[{i}].name");
            }

            for (int i = 0; i < node.Elements.Count; i++)
            {
                CustomWidgetCanvasElementSpec ce = node.Elements[i];
                InspectExpression(ce.Text, $"{path}.elements[{i}].text");
                InspectExpression(ce.Fill, $"{path}.elements[{i}].fill");
                InspectExpression(ce.Stroke, $"{path}.elements[{i}].stroke");
                InspectExpression(ce.Data, $"{path}.elements[{i}].data");
                InspectExpression(ce.StartAngle, $"{path}.elements[{i}].startAngle");
                InspectExpression(ce.EndAngle, $"{path}.elements[{i}].endAngle");
                InspectExpression(ce.SweepAngle, $"{path}.elements[{i}].sweepAngle");
            }

            if ((string.Equals(node.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "FlipView", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "Table", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "BarList", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "DonutChart", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "Chart", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "Sparkline", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "HeatmapGrid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "CalendarGrid", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(node.Collection ?? node.Items))
            {
                string rawCol = (node.Collection ?? node.Items)!.Trim();
                InspectExpression(rawCol, $"{path}.collection");
                if (rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    rawCol = rawCol["collections.".Length..];
                }

                if (!knownCollections.Contains(rawCol) &&
                    !rawCol.Contains('{', StringComparison.Ordinal) &&
                    !rawCol.StartsWith('$') &&
                    !rawCol.StartsWith("state.", StringComparison.OrdinalIgnoreCase) &&
                    !rawCol.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new(
                        $"{path}.collection",
                        $"{node.Type} references undeclared collection '{rawCol}'. Declare '{rawCol}' in root \"collections\".",
                        IsError: false));
                }
            }

            InspectExpression(node.Sparkline, $"{path}.sparkline");
            InspectExpression(node.Unit, $"{path}.unit");

            InspectActions(node.OnClick, $"{path}.onClick");
            InspectActions(node.OnSubmit, $"{path}.onSubmit");
            InspectActions(node.OnChange, $"{path}.onChange");
            InspectActions(node.OnDropActions, $"{path}.onDrop");
            InspectActions(node.OnRowClick, $"{path}.onRowClick");
            InspectActions(node.OnItemClick, $"{path}.onItemClick");
            InspectActions(node.OnReorder, $"{path}.onReorder");
            InspectActions(node.OnInlineCommit, $"{path}.onInlineCommit");
            InspectActions(node.OnPointerDown, $"{path}.onPointerDown");
            InspectActions(node.OnPointerMove, $"{path}.onPointerMove");
            InspectActions(node.OnPointerUp, $"{path}.onPointerUp");
            InspectNode(node.ActionButton, $"{path}.actionButton");
            InspectNode(node.Pane, $"{path}.pane");
            InspectNode(node.ItemTemplate, $"{path}.itemTemplate");

            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                InspectNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                CustomWidgetNodeSpec child = node.Children[i];
                string childPath = !string.IsNullOrWhiteSpace(child.NodePath) ? child.NodePath : $"{path}.children[{i}]";
                InspectNode(child, childPath);
            }
        }

        for (int i = 0; i < manifest.Sources.Count; i++)
        {
            CustomWidgetSourceSpec src = manifest.Sources[i];
            InspectExpression(src.Url, $"$.sources[{i}].url");
            InspectExpression(src.City, $"$.sources[{i}].city");
            InspectExpression(src.Path, $"$.sources[{i}].path");
            InspectExpression(src.Command, $"$.sources[{i}].command");
            InspectExpression(src.WorkingDirectory, $"$.sources[{i}].workingDirectory");
            if (src.Arguments is not null)
            {
                for (int ai = 0; ai < src.Arguments.Count; ai++)
                {
                    InspectExpression(src.Arguments[ai], $"$.sources[{i}].args[{ai}]");
                }
            }
            if (src.Headers is not null)
            {
                foreach ((string hk, string hv) in src.Headers)
                {
                    InspectExpression(hv, $"$.sources[{i}].headers.{hk}");
                }
            }
        }

        InspectNode(manifest.View, !string.IsNullOrWhiteSpace(manifest.View.NodePath) ? manifest.View.NodePath : "$.root");
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popPath = !string.IsNullOrWhiteSpace(pop.NodePath) ? pop.NodePath : $"$.popovers[{i}]";
            InspectNode(pop, popPath);
        }
        if (manifest.Capsule is { } cap)
        {
            InspectExpression(cap.Title, "$.capsule.title");
            InspectExpression(cap.Summary, "$.capsule.summary");
            InspectExpression(cap.Glyph, "$.capsule.glyph");
            InspectExpression(cap.Badge, "$.capsule.badge");
            InspectExpression(cap.BadgeIsWarning, "$.capsule.badgeIsWarning");
            InspectExpression(cap.Progress, "$.capsule.progress");
            if (cap.PrimaryAction is { } pa)
            {
                InspectExpression(pa.Icon, "$.capsule.primaryAction.icon");
                InspectExpression(pa.Tooltip, "$.capsule.primaryAction.tooltip");
                InspectActions(pa.OnClick, "$.capsule.primaryAction.onClick");
            }
        }

        if (manifest.OnDrop is { } onDrop)
        {
            InspectExpression(onDrop.Mode, "$.onDrop.mode");
            InspectActions(onDrop.Actions, "$.onDrop.actions");
        }

        foreach ((string colName, CustomWidgetCollectionSchema colSchema) in manifest.Collections)
        {
            if (colSchema.SeedItems is not { Count: > 0 })
            {
                continue;
            }

            for (int si = 0; si < colSchema.SeedItems.Count; si++)
            {
                DeskBox.Services.CustomWidgetRecord seedRec = colSchema.SeedItems[si];
                foreach ((string fk, string fv) in seedRec.Fields)
                {
                    InspectExpression(fv, $"$.collections.{colName}.seedItems[{si}].{fk}");
                }
            }
        }

        bool hasSystemMetrics = manifest.Sources.Any(static s =>
            string.Equals(s.Type, "systemMetrics", StringComparison.OrdinalIgnoreCase));
        if (hasSystemMetrics || usesProcessTokens)
        {
            referencedSettingsKeys.Add("refreshRate");
            referencedSettingsKeys.Add("targetDrive");
            referencedSettingsKeys.Add("topProcessCount");
            referencedSettingsKeys.Add("defaultProcessSort");
        }

        bool hasPomodoro = usesPomodoroTokens || manifest.Sources.Any(static s =>
            string.Equals(s.Type, "pomodoro", StringComparison.OrdinalIgnoreCase));
        if (hasPomodoro)
        {
            referencedSettingsKeys.Add("focusMinutes");
            referencedSettingsKeys.Add("breakPreset");
            referencedSettingsKeys.Add("shortBreakMinutes");
            referencedSettingsKeys.Add("longBreakMinutes");
            referencedSettingsKeys.Add("dailyGoal");
            referencedSettingsKeys.Add("flowMode");
            referencedSettingsKeys.Add("notifyMode");
            // v5.0 / v7.0 dual-track settings consumed natively by CustomWidgetPomodoroEngine.
            referencedSettingsKeys.Add("rhythmMode");
            referencedSettingsKeys.Add("sedentaryIntervalMin");
            referencedSettingsKeys.Add("idleAutoResetMin");
            referencedSettingsKeys.Add("backgroundSedentary");
        }

        bool hasCountdown = usesCountdownTokens || manifest.Sources.Any(static s =>
            string.Equals(s.Type, "countdown", StringComparison.OrdinalIgnoreCase));
        if (hasCountdown)
        {
            referencedSettingsKeys.Add("expiredMode");
            referencedSettingsKeys.Add("sortMode");
            referencedSettingsKeys.Add("progressScope");
            referencedSettingsKeys.Add("notifyDueToday");
            referencedSettingsKeys.Add("workHours");
            referencedSettingsKeys.Add("urgentDays");
            referencedSettingsKeys.Add("showNextHoliday");
        }

        bool hasAiMonitor = usesAiMonitorTokens || manifest.Sources.Any(static s =>
            string.Equals(s.Type, "aiMonitor", StringComparison.OrdinalIgnoreCase));
        if (hasAiMonitor)
        {
            referencedSettingsKeys.Add("quotaDisplayMode");
            referencedSettingsKeys.Add("refreshIntervalMs");
            referencedSettingsKeys.Add("walletCurrency");
            referencedSettingsKeys.Add("walletTargetBalance");
        }

        bool hasWeather = manifest.Sources.Any(static s =>
            string.Equals(s.Type, "weather", StringComparison.OrdinalIgnoreCase));
        if (hasWeather)
        {
            referencedSettingsKeys.Add("city");
        }

        for (int i = 0; i < manifest.Settings.Count; i++)
        {
            CustomWidgetSettingDefinition setting = manifest.Settings[i];
            InspectExpression(setting.Label, $"$.settings[{i}].label");
            InspectExpression(setting.Description, $"$.settings[{i}].description");
            InspectExpression(setting.Placeholder, $"$.settings[{i}].placeholder");
            for (int j = 0; j < setting.Options.Count; j++)
            {
                InspectExpression(setting.Options[j].Label, $"$.settings[{i}].options[{j}].label");
            }

            if (!hasUninspectedScript &&
                !string.IsNullOrWhiteSpace(setting.Key) &&
                !referencedSettingsKeys.Contains(setting.Key))
            {
                string expectedToken = string.Equals(setting.Type, "secret", StringComparison.OrdinalIgnoreCase)
                    ? $"${{$secrets.{setting.Key}}}"
                    : string.Equals(setting.Type, "moduleList", StringComparison.OrdinalIgnoreCase)
                        ? $"settings.{setting.Key}"
                        : $"${{$settings.{setting.Key}}}";
                diagnostics.Add(new(
                    $"$.settings[{i}].key",
                    $"Setting '{setting.Key}' is declared in root \"settings\" but never referenced in view, sources, capsule, or actions (expected '{expectedToken}'). Wire it up or remove the unused setting.",
                    IsError: false));
            }
        }

        return new(manifest, diagnostics);
    }

    public static string NormalizeGlyph(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "\uE71D";
        }

        string trimmed = raw.Trim();
        if (trimmed.Length == 1)
        {
            return trimmed;
        }

        if (CustomWidgetSemanticIcons.TryResolve(trimmed, out string semanticGlyph))
        {
            return semanticGlyph;
        }

        string hex = trimmed.StartsWith("\\u", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..]
            : trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? trimmed[2..]
                : trimmed;
        if (hex.Length is >= 4 and <= 5 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint) &&
            codePoint is > 0 and <= 0xFFFF)
        {
            return ((char)codePoint).ToString();
        }

        return trimmed;
    }

    internal static string NormalizeGlyphWithDiagnostics(
        string? raw,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        string fallback = "\uE71D")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string trimmed = raw.Trim();
        if (trimmed.Contains("${", StringComparison.Ordinal))
        {
            return trimmed;
        }

        string normalized = NormalizeGlyph(trimmed);
        if (string.Equals(normalized, trimmed, StringComparison.Ordinal) &&
            CustomWidgetSemanticIcons.LooksLikeUnresolvedSemanticIconName(trimmed))
        {
            IReadOnlyList<string> suggestions = CustomWidgetSemanticIcons.SuggestClosest(trimmed, 3);
            string hint = suggestions.Count > 0
                ? $" Did you mean: {string.Join(", ", suggestions)}?"
                : " Use a semantic icon name (e.g. 'add', 'edit', 'delete', 'export', 'folder', 'settings'), an Emoji, or a Segoe Fluent glyph like '\\uE710'.";
            diagnostics.Add(new(
                path,
                $"Unknown semantic icon name '{trimmed}'.{hint}",
                IsError: false,
                Code: "UnknownSemanticIconName"));
        }

        return normalized;
    }

    private static CustomWidgetSizeSpec ParseSize(
        JsonElement parent,
        string propertyName,
        int defaultWidth,
        int defaultHeight)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement sizeEl) ||
            sizeEl.ValueKind != JsonValueKind.Object)
        {
            return new(defaultWidth, defaultHeight);
        }

        int width = Math.Clamp(GetInt(sizeEl, "width", defaultWidth), 180, 1200);
        int height = Math.Clamp(GetInt(sizeEl, "height", defaultHeight), 80, 1200);
        return new(width, height);
    }

    private static IReadOnlyList<CustomWidgetSettingDefinition> ParseSettings(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("settings", out JsonElement settingsEl) ||
            settingsEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        var list = new List<CustomWidgetSettingDefinition>();
        var pendingOptionsFrom = new Dictionary<int, string>();
        if (settingsEl.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in settingsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string key = GetString(item, "key", string.Empty);
                    string type = GetString(item, "type", "choice");
                    string label = GetString(item, "label", GetString(item, "title", key));
                    string defaultValue = GetScalarAsString(item, "default", string.Empty);
                    var options = ParseOptions(item, "options");
                    string? optionsFrom = TryGetString(item, "optionsFrom");
                    string? accept = TryGetString(item, "accept");
                    string? placeholder = TryGetString(item, "placeholder");
                    string description = GetString(item, "description", string.Empty);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        diagnostics.Add(new($"$.settings[{index}]", "Setting definition is missing 'key'."));
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(optionsFrom))
                        {
                            string normalizedSourceKey = optionsFrom.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
                                ? optionsFrom["$settings.".Length..].Trim()
                                : optionsFrom.StartsWith("settings.", StringComparison.OrdinalIgnoreCase)
                                    ? optionsFrom["settings.".Length..].Trim()
                                    : optionsFrom.Trim();
                            if (!string.IsNullOrEmpty(normalizedSourceKey))
                            {
                                pendingOptionsFrom[list.Count] = normalizedSourceKey;
                            }
                        }

                        list.Add(new(key, type, label, defaultValue, options, accept, placeholder, description));
                    }
                }
                index++;
            }

            if (pendingOptionsFrom.Count > 0)
            {
                var byKey = new Dictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>(StringComparer.Ordinal);
                foreach (CustomWidgetSettingDefinition def in list)
                {
                    byKey[def.Key] = def.Options;
                }

                foreach ((int idx, string sourceKey) in pendingOptionsFrom)
                {
                    if (!byKey.TryGetValue(sourceKey, out IReadOnlyList<CustomWidgetOptionSpec>? inherited) ||
                        inherited.Count == 0)
                    {
                        diagnostics.Add(new($"$.settings[{idx}].optionsFrom", $"Setting '{list[idx].Key}' references unknown or empty optionsFrom '{sourceKey}'."));
                        continue;
                    }

                    var merged = new List<CustomWidgetOptionSpec>(list[idx].Options.Count + inherited.Count);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (CustomWidgetOptionSpec opt in list[idx].Options)
                    {
                        if (seen.Add(opt.Value))
                        {
                            merged.Add(opt);
                        }
                    }

                    foreach (CustomWidgetOptionSpec opt in inherited)
                    {
                        if (seen.Add(opt.Value))
                        {
                            merged.Add(opt);
                        }
                    }

                    list[idx] = list[idx] with { Options = merged };
                }
            }

            return list;
        }

        if (settingsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in settingsEl.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name))
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    string key = GetString(prop.Value, "key", prop.Name);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        key = prop.Name;
                    }
                    string type = GetString(prop.Value, "type", "choice");
                    string label = GetString(prop.Value, "label", GetString(prop.Value, "title", key));
                    string defaultValue = GetScalarAsString(prop.Value, "default", string.Empty);
                    var options = ParseOptions(prop.Value, "options");
                    string? accept = TryGetString(prop.Value, "accept");
                    string? placeholder = TryGetString(prop.Value, "placeholder");
                    string description = GetString(prop.Value, "description", string.Empty);
                    list.Add(new(key, type, label, defaultValue, options, accept, placeholder, description));
                }
                else if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    string type = prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? "toggle" : "text";
                    string defaultValue = prop.Value.ValueKind == JsonValueKind.String
                        ? (prop.Value.GetString() ?? string.Empty)
                        : prop.Value.GetRawText();
                    list.Add(new(prop.Name, type, prop.Name, defaultValue, []));
                }
            }

            return list;
        }

        diagnostics.Add(new("$.settings", "'settings' must be an array or object of setting definitions."));
        return list;
    }

    private static IReadOnlyList<CustomWidgetOptionSpec> ParseOptions(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement optionsEl) ||
            optionsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var options = new List<CustomWidgetOptionSpec>();
        foreach (JsonElement opt in optionsEl.EnumerateArray())
        {
            if (opt.ValueKind == JsonValueKind.Object)
            {
                string value = GetScalarAsString(opt, "value", string.Empty);
                string label = GetString(opt, "label", GetString(opt, "title", value));
                string? icon = TryGetString(opt, "icon");
                options.Add(new(
                    value,
                    label,
                    icon is null ? null : NormalizeGlyph(icon)));
            }
            else if (opt.ValueKind == JsonValueKind.String)
            {
                string val = opt.GetString() ?? string.Empty;
                options.Add(new(val, val));
            }
        }

        return options;
    }

    private static (IReadOnlyDictionary<string, string> State, IReadOnlyDictionary<string, JsonElement> StateJson) ParseState(JsonElement root)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var jsonDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty("state", out JsonElement stateEl) ||
            stateEl.ValueKind != JsonValueKind.Object)
        {
            return (dict, jsonDict);
        }

        foreach (JsonProperty prop in stateEl.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                dict[prop.Name] = string.Empty;
                continue;
            }

            dict[prop.Name] = ConvertElementToString(prop.Value);
            jsonDict[prop.Name] = prop.Value.Clone();
        }

        return (dict, jsonDict);
    }

    private static IReadOnlyDictionary<string, CustomWidgetCollectionSchema> ParseCollections(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var dict = new Dictionary<string, CustomWidgetCollectionSchema>(StringComparer.Ordinal);
        if (!root.TryGetProperty("collections", out JsonElement colsEl))
        {
            return dict;
        }

        if (colsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in colsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string colName = GetString(item, "name", GetString(item, "id", string.Empty));
                    if (!string.IsNullOrWhiteSpace(colName))
                    {
                        int maxItems = Math.Clamp(GetInt(item, "maxItems", 200), 1, 2000);
                        string sort = GetString(item, "sort", "newestFirst");
                        IReadOnlyList<DeskBox.Services.CustomWidgetRecord>? seedItems =
                            item.TryGetProperty("seedItems", out JsonElement seedEl) && seedEl.ValueKind == JsonValueKind.Array
                                ? ParseSeedItemsArray(seedEl, $"$.collections.{colName}.seedItems", maxItems, diagnostics)
                                : [];
                        dict[colName] = new(maxItems, sort, seedItems);
                    }
                }
                else if (item.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    dict[item.GetString()!] = new(SeedItems: []);
                }
            }

            return dict;
        }

        if (colsEl.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (JsonProperty prop in colsEl.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                int maxItems = Math.Clamp(GetInt(prop.Value, "maxItems", 200), 1, 2000);
                string sort = GetString(prop.Value, "sort", "newestFirst");
                IReadOnlyList<DeskBox.Services.CustomWidgetRecord> seedItems =
                    prop.Value.TryGetProperty("seedItems", out JsonElement seedEl) && seedEl.ValueKind == JsonValueKind.Array
                        ? ParseSeedItemsArray(seedEl, $"$.collections.{prop.Name}.seedItems", maxItems, diagnostics)
                        : [];
                dict[prop.Name] = new(maxItems, sort, seedItems);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                if (prop.Value.GetArrayLength() > 0)
                {
                    diagnostics.Add(new(
                        $"$.collections.{prop.Name}",
                        $"Collection '{prop.Name}' specifies an inline array directly under $.collections; prefer an object schema with \"seedItems\" or preview sampleData.",
                        IsError: false,
                        Code: "InlineCollectionRecordsInSchema"));
                }

                IReadOnlyList<DeskBox.Services.CustomWidgetRecord> seedItems =
                    ParseSeedItemsArray(prop.Value, $"$.collections.{prop.Name}", 200, diagnostics);
                dict[prop.Name] = new(200, "newestFirst", seedItems);
            }
            else
            {
                dict[prop.Name] = new(SeedItems: []);
            }
        }

        return dict;
    }

    private static IReadOnlyList<DeskBox.Services.CustomWidgetRecord> ParseSeedItemsArray(
        JsonElement arrEl,
        string basePath,
        int maxItems,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (arrEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<DeskBox.Services.CustomWidgetRecord>();
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        int idx = 0;
        foreach (JsonElement item in arrEl.EnumerateArray())
        {
            if (list.Count >= maxItems)
            {
                break;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                ScanElementForSecretAndUnclosedInterpolation(
                    item,
                    $"{basePath}[{idx}]",
                    diagnostics,
                    forbidSecrets: true,
                    checkUnclosed: true);

                var rec = new DeskBox.Services.CustomWidgetRecord
                {
                    CreatedAtUtcTicks = nowTicks + idx,
                    UpdatedAtUtcTicks = nowTicks + idx
                };
                foreach (JsonProperty fp in item.EnumerateObject())
                {
                    if (fp.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (string.Equals(fp.Name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        string idStr = ConvertElementToString(fp.Value).Trim();
                        if (!string.IsNullOrEmpty(idStr))
                        {
                            rec.Id = idStr;
                        }
                    }

                    rec.FieldsJson[fp.Name] = fp.Value.Clone();
                }

                list.Add(rec);
            }

            idx++;
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetSourceSpec> ParseSources(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("sources", out JsonElement sourcesEl) ||
            sourcesEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CustomWidgetSourceSpec>();
        int index = 0;
        foreach (JsonElement item in sourcesEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                string rawType = GetString(item, "type", string.Empty);
                string type = rawType.Trim() switch
                {
                    "aiUsage" or "aiQuota" or "aiBilling" => "aiMonitor",
                    "sysMetrics" or "system" or "metrics" => "systemMetrics",
                    "device" or "controlCenter" => "deviceState",
                    "drives" or "disk" or "disks" or "storage" => "storageDrives",
                    "music" or "mediaSession" or "nowPlaying" => "music",
                    "todo" or "todos" or "tasks" => "todo",
                    "clip" => "clipboard",
                    "lunar" or "lunarCalendar" => "calendar",
                    "pomo" => "pomodoro",
                    "http" or "json" or "rest" => "httpJson",
                    "atom" or "feed" => "rss",
                    "file" or "localFile" or "textFile" => "fileText",
                    "shell" or "cli" or "exec" => "command",
                    _ => rawType
                };
                bool isNetworkSource =
                    string.Equals(type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "rss", StringComparison.OrdinalIgnoreCase);

                if (isNetworkSource)
                {
                    int rawIntervalMs = CustomWidgetNetworkSecurity.DefaultNetworkSourceIntervalMs;
                    if (item.TryGetProperty("intervalMs", out _))
                    {
                        rawIntervalMs = GetInt(item, "intervalMs", rawIntervalMs);
                    }
                    else if (item.TryGetProperty("refreshIntervalMs", out _))
                    {
                        rawIntervalMs = GetInt(item, "refreshIntervalMs", rawIntervalMs);
                    }
                    else if (item.TryGetProperty("refreshMs", out _))
                    {
                        rawIntervalMs = GetInt(item, "refreshMs", rawIntervalMs);
                    }
                    else if (item.TryGetProperty("refreshInterval", out _))
                    {
                        int refreshVal = GetInt(item, "refreshInterval", 900);
                        rawIntervalMs = refreshVal > 0 && refreshVal < 10_000 ? refreshVal * 1000 : refreshVal;
                    }

                    int intervalMs = Math.Clamp(
                        rawIntervalMs,
                        CustomWidgetNetworkSecurity.MinNetworkSourceIntervalMs,
                        CustomWidgetNetworkSecurity.MaxNetworkSourceIntervalMs);
                    string? sourceId = TryGetString(item, "id");
                    string? url = TryGetString(item, "url");
                    string? targetCollection = TryGetString(item, "targetCollection") ??
                                               TryGetString(item, "collection") ??
                                               (string.Equals(type, "rss", StringComparison.OrdinalIgnoreCase) ? "items" : null);
                    string? itemsPath = TryGetString(item, "itemsPath") ?? TryGetString(item, "path");
                    IReadOnlyDictionary<string, string>? fieldMap = ParseStringDictionary(item, "fieldMap") ??
                                                                    ParseStringDictionary(item, "itemFields") ??
                                                                    ParseStringDictionary(item, "mapping");
                    IReadOnlyDictionary<string, string>? stateMap = ParseStringDictionary(item, "stateMap");
                    IReadOnlyDictionary<string, string>? headers = ParseStringDictionary(item, "headers");
                    int maxItems = Math.Clamp(GetInt(item, "maxItems", 20), 1, 100);

                    bool isDynamicTemplateUrl = !string.IsNullOrWhiteSpace(url) &&
                        (url.Contains("${", StringComparison.Ordinal) ||
                         url.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase) ||
                         url.StartsWith("$secrets.", StringComparison.OrdinalIgnoreCase));
                    if (!isDynamicTemplateUrl &&
                        !CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
                            url,
                            allowLocalAndHttp: true,
                            out _,
                            out _,
                            out string? urlError))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].url",
                            urlError ?? "Network source requires a valid 'https://' or 'http://' URL.",
                            IsError: true));
                    }
                    else
                    {
                        list.Add(new CustomWidgetSourceSpec(
                            Type: type,
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            Url: url,
                            TargetCollection: targetCollection,
                            ItemsPath: itemsPath,
                            FieldMap: fieldMap,
                            StateMap: stateMap,
                            MaxItems: maxItems,
                            Headers: headers));
                    }
                }
                else if (string.Equals(type, "fileText", StringComparison.OrdinalIgnoreCase))
                {
                    int rawInterval = GetInt(item, "intervalMs", 5_000);
                    int intervalMs = Math.Clamp(rawInterval, 1_000, 3_600_000);
                    string? sourceId = TryGetString(item, "id") ?? "file";
                    string? filePath = TryGetString(item, "path") ?? TryGetString(item, "filePath");
                    IReadOnlyDictionary<string, string>? stateMap = ParseStringDictionary(item, "stateMap");
                    if (stateMap is null && TryGetString(item, "stateKey") is { Length: > 0 } fileStateKey)
                    {
                        stateMap = new Dictionary<string, string>(StringComparer.Ordinal) { [fileStateKey] = "text" };
                    }
                    string? targetCol = TryGetString(item, "targetCollection") ?? TryGetString(item, "collection");
                    int tailLines = Math.Clamp(GetInt(item, "tailLines", 0), 0, 5_000);
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].path",
                            "Source 'fileText' requires 'path' (or '$settings.<key>').",
                            IsError: true));
                    }
                    else
                    {
                        list.Add(new CustomWidgetSourceSpec(
                            Type: "fileText",
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            TargetCollection: targetCol,
                            StateMap: stateMap,
                            Path: filePath,
                            TailLines: tailLines));
                    }
                }
                else if (string.Equals(type, CustomWidgetSourceTypes.LocalFolder, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(type, "mediaFolder", StringComparison.OrdinalIgnoreCase))
                {
                    int rawInterval = GetInt(item, "intervalMs", 15_000);
                    int intervalMs = Math.Clamp(rawInterval, 2_000, 86_400_000);
                    string? sourceId = TryGetString(item, "id") ?? "folder";
                    string? folderPath = TryGetString(item, "path") ??
                                         TryGetString(item, "folder") ??
                                         TryGetString(item, "directory");
                    string targetCol = TryGetString(item, "targetCollection") ??
                                       TryGetString(item, "collection") ??
                                       "items";
                    IReadOnlyList<string> extensions = item.TryGetProperty("extensions", out _)
                        ? ParseScalarArray(item, "extensions")
                        : ParseScalarArray(item, "accept");
                    bool recursive = GetBool(item, "recursive", false);
                    int maxItems = Math.Clamp(GetInt(item, "maxItems", 100), 1, 500);
                    string? sort = TryGetString(item, "sort") ?? TryGetString(item, "orderBy");
                    if (string.IsNullOrWhiteSpace(folderPath))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].path",
                            "Source 'localFolder' requires 'path' (e.g. '${$settings.folderPath}').",
                            IsError: true));
                    }
                    else
                    {
                        list.Add(new CustomWidgetSourceSpec(
                            Type: CustomWidgetSourceTypes.LocalFolder,
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            TargetCollection: targetCol,
                            MaxItems: maxItems,
                            Path: folderPath,
                            Extensions: extensions,
                            Recursive: recursive,
                            Sort: sort));
                    }
                }
                else if (string.Equals(type, "command", StringComparison.OrdinalIgnoreCase))
                {
#if DESKBOX_STORE
                    diagnostics.Add(new(
                        $"$.sources[{index}]",
                        "Source 'command' is not available in the Microsoft Store edition of DeskBox.",
                        IsError: true));
#else
                    int rawInterval = GetInt(item, "intervalMs", 30_000);
                    int intervalMs = Math.Clamp(rawInterval, 3_000, 86_400_000);
                    string? sourceId = TryGetString(item, "id") ?? "cmd";
                    string? cmdExe = TryGetString(item, "command") ??
                                     TryGetString(item, "executable") ??
                                     TryGetString(item, "exe") ??
                                     TryGetString(item, "file");
                    IReadOnlyList<string> cmdArgs = item.TryGetProperty("args", out _)
                        ? ParseScalarArray(item, "args")
                        : ParseScalarArray(item, "arguments");
                    string? workDir = TryGetString(item, "workingDirectory") ?? TryGetString(item, "cwd");
                    int timeoutMs = Math.Clamp(GetInt(item, "timeoutMs", 10_000), 500, 60_000);
                    IReadOnlyDictionary<string, string>? stateMap = ParseStringDictionary(item, "stateMap");
                    if (stateMap is null && TryGetString(item, "stateKey") is { Length: > 0 } cmdStateKey)
                    {
                        stateMap = new Dictionary<string, string>(StringComparer.Ordinal) { [cmdStateKey] = "stdout" };
                    }
                    string? targetCol = TryGetString(item, "targetCollection") ?? TryGetString(item, "collection");
                    if (string.IsNullOrWhiteSpace(cmdExe))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].command",
                            "Source 'command' requires 'command' or 'executable' (executable name or path).",
                            IsError: true));
                    }
                    else
                    {
                        list.Add(new CustomWidgetSourceSpec(
                            Type: "command",
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            TargetCollection: targetCol,
                            StateMap: stateMap,
                            Command: cmdExe,
                            Arguments: cmdArgs,
                            WorkingDirectory: workDir,
                            TimeoutMs: timeoutMs));
                    }
#endif
                }
                else
                {
                    int defaultInterval = string.Equals(type, "weather", StringComparison.OrdinalIgnoreCase)
                        ? 900_000
                        : string.Equals(type, "calendar", StringComparison.OrdinalIgnoreCase)
                            ? 60_000
                            : string.Equals(type, "todo", StringComparison.OrdinalIgnoreCase)
                                ? 30_000
                                : string.Equals(type, "storageDrives", StringComparison.OrdinalIgnoreCase)
                                    ? 15_000
                                    : string.Equals(type, "deviceState", StringComparison.OrdinalIgnoreCase)
                                        ? 3_000
                                        : string.Equals(type, "clock", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(type, "music", StringComparison.OrdinalIgnoreCase)
                                            ? 1_000
                                            : 2_000;
                    int rawInterval = GetInt(item, "intervalMs", defaultInterval);
                    int intervalMs = Math.Clamp(rawInterval, 500, 3_600_000);
                    if (string.Equals(type, "music", StringComparison.OrdinalIgnoreCase) && rawInterval is > 0 and < 1000)
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].intervalMs",
                            $"Source 'music' intervalMs ({rawInterval}) is below minimum 1000ms and will be clamped to 1000ms.",
                            IsError: false));
                        intervalMs = 1000;
                    }
                    else if (string.Equals(type, "todo", StringComparison.OrdinalIgnoreCase) && rawInterval is > 0 and < 5000)
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].intervalMs",
                            $"Source 'todo' intervalMs ({rawInterval}) is below minimum 5000ms and will be clamped to 5000ms.",
                            IsError: false));
                        intervalMs = 5000;
                    }
                    int maxItems = Math.Clamp(GetInt(item, "maxItems", 20), 1, 50);
                    if (!string.Equals(type, "systemMetrics", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "clock", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "pomodoro", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "countdown", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "aiMonitor", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "weather", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "calendar", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "deviceState", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "storageDrives", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "clipboard", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "music", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "todo", StringComparison.OrdinalIgnoreCase))
                    {
                        string sourceTypeHint = string.IsNullOrWhiteSpace(type)
                            ? "Missing 'type' on source entry. Each item in \"sources\" must be a flat object like { \"id\": \"feed\", \"type\": \"httpJson\", \"url\": \"...\" } (never wrap it under a custom key object)."
                            : $"Unknown source type '{type}'. Supported: 'systemMetrics', 'weather', 'calendar', 'deviceState', 'storageDrives', 'clipboard', 'music', 'todo', 'clock', 'pomodoro', 'countdown', 'aiMonitor', 'httpJson', 'rss', 'fileText', 'localFolder', 'command'.";
                        diagnostics.Add(new(
                            $"$.sources[{index}]",
                            sourceTypeHint,
                            IsError: true,
                            Code: "UnknownSourceType"));
                    }
                    else
                    {
                        string? sourceId = TryGetString(item, "id");
                        string? city = TryGetString(item, "city") ?? TryGetString(item, "location");
                        string? targetCol = TryGetString(item, "targetCollection") ??
                                            TryGetString(item, "collection") ??
                                            (string.Equals(type, "weather", StringComparison.OrdinalIgnoreCase)
                                                ? "forecast"
                                                : string.Equals(type, "storageDrives", StringComparison.OrdinalIgnoreCase)
                                                    ? "drives"
                                                    : string.Equals(type, "todo", StringComparison.OrdinalIgnoreCase)
                                                        ? "todos"
                                                        : null);
                        list.Add(new CustomWidgetSourceSpec(
                            Type: type,
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            TargetCollection: targetCol,
                            MaxItems: maxItems,
                            City: city));
                    }
                }
            }
            index++;
        }

        return list;
    }

    private static Dictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? BuildSettingOptionsLookup(
        IReadOnlyList<CustomWidgetSettingDefinition> settings)
    {
        Dictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? lookup = null;
        foreach (CustomWidgetSettingDefinition setting in settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.Key) && setting.Options.Count > 0)
            {
                lookup ??= new Dictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>(StringComparer.Ordinal);
                lookup.TryAdd(setting.Key, setting.Options);
            }
        }
        return lookup;
    }

    private static IReadOnlyList<CustomWidgetOptionSpec> ResolveBoundSettingOptions(
        string bind,
        IReadOnlyDictionary<string, IReadOnlyList<CustomWidgetOptionSpec>> settingOptionsByKey)
    {
        string trimmed = bind.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        string? key = trimmed.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
            ? trimmed["$settings.".Length..]
            : trimmed.StartsWith("settings.", StringComparison.OrdinalIgnoreCase)
                ? trimmed["settings.".Length..]
                : null;
        if (!string.IsNullOrWhiteSpace(key) &&
            settingOptionsByKey.TryGetValue(key.Trim(), out IReadOnlyList<CustomWidgetOptionSpec>? opts))
        {
            return opts;
        }

        return [];
    }

    private sealed class FlatGraphParseContext
    {
        public Dictionary<string, JsonElement> Elements { get; } = new(StringComparer.Ordinal);
        public HashSet<string> VisitedIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ActiveStack { get; } = new(StringComparer.Ordinal);
    }

    private static FlatGraphParseContext BuildFlatGraphContext(
        JsonElement elementsObj,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var ctx = new FlatGraphParseContext();
        foreach (JsonProperty prop in elementsObj.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
            {
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new(
                    $"$.elements.{prop.Name}",
                    $"Element '{prop.Name}' in 'elements' must be a JSON object, not {prop.Value.ValueKind}.",
                    IsError: true,
                    Code: "ElementGraphInvalidElement"));
                continue;
            }

            ctx.Elements[prop.Name] = prop.Value;
        }

        return ctx;
    }

    private static CustomWidgetNodeSpec ResolveFlatElement(
        string elemId,
        string refPath,
        FlatGraphParseContext flatGraph,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        int depth,
        IReadOnlyDictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? settingOptionsByKey)
    {
        string trimmedId = elemId.Trim();
        if (!flatGraph.Elements.TryGetValue(trimmedId, out JsonElement elemJson))
        {
            diagnostics.Add(new(
                refPath,
                $"Element ID '{trimmedId}' referenced at '{refPath}' does not exist in root 'elements'.",
                IsError: true,
                Code: "ElementGraphDanglingRef"));
            return new CustomWidgetNodeSpec { Type = "Stack", NodePath = $"#{trimmedId}", Id = trimmedId };
        }

        if (!flatGraph.ActiveStack.Add(trimmedId))
        {
            diagnostics.Add(new(
                refPath,
                $"Circular element reference detected involving '#{trimmedId}' at '{refPath}'.",
                IsError: true,
                Code: "ElementGraphCycle"));
            return new CustomWidgetNodeSpec { Type = "Stack", NodePath = $"#{trimmedId}", Id = trimmedId };
        }

        flatGraph.VisitedIds.Add(trimmedId);
        try
        {
            return ParseNode(
                elemJson,
                $"#{trimmedId}",
                diagnostics,
                flatGraph,
                depth,
                settingOptionsByKey,
                implicitElementId: trimmedId);
        }
        finally
        {
            flatGraph.ActiveStack.Remove(trimmedId);
        }
    }

    private static void ValidateRootViewLayout(
        CustomWidgetNodeSpec view,
        CustomWidgetSizeSpec defaultSize,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        string rootDiagPath = !string.IsNullOrWhiteSpace(view.NodePath) ? view.NodePath : "$.root";
        if (!string.IsNullOrWhiteSpace(view.When))
        {
            diagnostics.Add(new(
                $"{rootDiagPath}.when",
                "Root element cannot declare a conditional 'when' guard; move conditional branches into child elements.",
                IsError: true,
                Code: CustomWidgetStrictAuthoringRules.RootViewConditionalWhen));
        }

        if (string.Equals(view.Type, "Stack", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(view.Orientation, "vertical", StringComparison.OrdinalIgnoreCase) &&
            view.Children.Count >= 2)
        {
            double fixedHeightSum = 0;
            int unconditionalChildren = 0;
            foreach (CustomWidgetNodeSpec rootChild in view.Children)
            {
                if (!string.IsNullOrWhiteSpace(rootChild.When))
                {
                    continue;
                }

                unconditionalChildren++;
                if (rootChild.Height is { } ch && ch > 0)
                {
                    fixedHeightSum += ch;
                }
            }

            if (fixedHeightSum > 0 && unconditionalChildren >= 2)
            {
                double totalVerticalSpacing = Math.Max(0, unconditionalChildren - 1) * Math.Max(0, view.Spacing);
                double verticalPadding = 0;
                if (!string.IsNullOrWhiteSpace(view.Padding) &&
                    double.TryParse(view.Padding.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double padAll) &&
                    padAll > 0)
                {
                    verticalPadding = padAll * 2;
                }

                double estimatedMinHeight = fixedHeightSum + totalVerticalSpacing + verticalPadding;
                if (estimatedMinHeight > defaultSize.Height)
                {
                    diagnostics.Add(new(
                        rootDiagPath,
                        $"Root vertical Stack children specify fixed heights totaling ~{estimatedMinHeight:0}px (including spacing/padding), exceeding defaultSize.height ({defaultSize.Height}px) and clipping bottom controls. Use a root Grid with rows: \"*,Auto\" instead of fixed child heights.",
                        IsError: false));
                }
            }
        }
    }

    private static IReadOnlyList<CustomWidgetNodeSpec> ParseRootPopovers(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        IReadOnlyDictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? settingOptionsByKey = null,
        FlatGraphParseContext? flatGraph = null)
    {
        if (!root.TryGetProperty("popovers", out JsonElement popoversEl) ||
            popoversEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (popoversEl.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(
                "$.popovers",
                "Top-level 'popovers' must be a JSON object mapping popover IDs to { \"root\": \"<elementId>\", ... }.",
                IsError: true,
                Code: "InvalidPopoversMap"));
            return [];
        }

        var list = new List<CustomWidgetNodeSpec>();
        foreach (JsonProperty prop in popoversEl.EnumerateObject())
        {
            string popPath = $"$.popovers.{prop.Name}";
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new(
                    popPath,
                    $"Popover '{prop.Name}' must be a JSON object with a string 'root' element ID.",
                    IsError: true,
                    Code: "InvalidPopoversMap"));
                continue;
            }

            foreach (JsonProperty popProp in prop.Value.EnumerateObject())
            {
                if (popProp.Name is "view" or "children" or "child" or "content")
                {
                    continue;
                }

                ScanElementForSecretAndUnclosedInterpolation(
                    popProp.Value,
                    $"{popPath}.{popProp.Name}",
                    diagnostics,
                    forbidSecrets: true,
                    checkUnclosed: true);
            }

            bool hasLegacyBody = false;
            if (prop.Value.TryGetProperty("view", out _))
            {
                hasLegacyBody = true;
                diagnostics.Add(new(
                    $"{popPath}.view",
                    $"Legacy nested popover 'view' at '{popPath}.view' is not supported. Define popover body elements in top-level 'elements' and reference the root ID via '{popPath}.root'.",
                    IsError: true,
                    Code: "LegacyViewNotSupported"));
            }

            if (prop.Value.TryGetProperty("children", out _) || prop.Value.TryGetProperty("child", out _))
            {
                hasLegacyBody = true;
                string legacyKey = prop.Value.TryGetProperty("children", out _) ? "children" : "child";
                diagnostics.Add(new(
                    $"{popPath}.{legacyKey}",
                    $"Legacy nested popover '{legacyKey}' at '{popPath}.{legacyKey}' is not supported. Define popover body elements in top-level 'elements' and reference the root ID via '{popPath}.root'.",
                    IsError: true,
                    Code: "LegacyViewNotSupported"));
            }

            if (prop.Value.TryGetProperty("content", out JsonElement popContentEl) &&
                popContentEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                hasLegacyBody = true;
                diagnostics.Add(new(
                    $"{popPath}.content",
                    $"Legacy nested popover 'content' at '{popPath}.content' is not supported. Define popover body elements in top-level 'elements' and reference the root ID via '{popPath}.root'.",
                    IsError: true,
                    Code: "LegacyViewNotSupported"));
            }

            IReadOnlyList<CustomWidgetNodeSpec> bodyChildren = [];
            string? popRootId = prop.Value.TryGetProperty("root", out JsonElement popRootIdEl) &&
                                popRootIdEl.ValueKind == JsonValueKind.String
                ? popRootIdEl.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(popRootId))
            {
                if (!hasLegacyBody)
                {
                    diagnostics.Add(new(
                        $"{popPath}.root",
                        $"Popover '{prop.Name}' requires a non-empty string 'root' pointing to an element ID in top-level 'elements'.",
                        IsError: true,
                        Code: "ElementGraphMissingRoot"));
                }
            }
            else if (flatGraph is not null)
            {
                CustomWidgetNodeSpec body = ResolveFlatElement(
                    popRootId,
                    $"{popPath}.root",
                    flatGraph,
                    diagnostics,
                    depth: 2,
                    settingOptionsByKey);
                bodyChildren = [body];
            }

            string popoverId = TryGetString(prop.Value, "popoverId") ?? TryGetString(prop.Value, "id") ?? prop.Name;
            string? rawBackground = TryGetString(prop.Value, "background") ?? TryGetString(prop.Value, "backgroundColor") ?? TryGetString(prop.Value, "bg");
            list.Add(new CustomWidgetNodeSpec
            {
                Type = "Popover",
                NodePath = popPath,
                Id = TryGetString(prop.Value, "id") ?? prop.Name,
                PopoverId = popoverId,
                Title = TryGetScalarAsString(prop.Value, "title") ??
                        TryGetScalarAsString(prop.Value, "label") ??
                        TryGetScalarAsString(prop.Value, "header"),
                Subtitle = TryGetScalarAsString(prop.Value, "subtitle") ?? TryGetScalarAsString(prop.Value, "message"),
                Width = TryGetDouble(prop.Value, "width"),
                Height = TryGetDouble(prop.Value, "height"),
                MinWidth = TryGetDouble(prop.Value, "minWidth"),
                MaxWidth = TryGetDouble(prop.Value, "maxWidth"),
                MinHeight = TryGetDouble(prop.Value, "minHeight"),
                MaxHeight = TryGetDouble(prop.Value, "maxHeight"),
                Spacing = GetDouble(prop.Value, "spacing", 8),
                HasExplicitSpacing = prop.Value.TryGetProperty("spacing", out _),
                Padding = TryGetScalarAsString(prop.Value, "padding"),
                Placement = TryGetString(prop.Value, "placement"),
                Tone = GetString(prop.Value, "tone", "default"),
                Background = rawBackground,
                LinearGradient = ParseLinearGradient(prop.Value),
                RadialGradient = ParseRadialGradient(prop.Value),
                Backdrop = TryGetString(prop.Value, "backdrop") ?? TryGetString(prop.Value, "material"),
                Shadow = TryGetString(prop.Value, "shadow") ?? TryGetString(prop.Value, "elevation"),
                CornerRadius = TryGetDouble(prop.Value, "cornerRadius") ?? TryGetDouble(prop.Value, "radius"),
                BorderColor = TryGetString(prop.Value, "borderColor") ?? TryGetString(prop.Value, "stroke"),
                BorderWidth = TryGetScalarAsString(prop.Value, "borderWidth") ?? TryGetScalarAsString(prop.Value, "borderThickness"),
                Children = bodyChildren
            });
        }

        return list;
    }

    private static CustomWidgetDropBehavior? ParseOnDrop(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("onDrop", out JsonElement dropEl) ||
            dropEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string accept = GetString(dropEl, "accept", "files");
        string mode = GetString(dropEl, "mode", TryGetString(dropEl, "storage") ?? "$settings.dropMode");
        IReadOnlyList<CustomWidgetActionSpec> actions = [];
        if (dropEl.TryGetProperty("action", out JsonElement actionEl))
        {
            actions = ParseActionList(actionEl, "$.onDrop.action", diagnostics);
        }
        else if (dropEl.TryGetProperty("actions", out JsonElement actionsEl))
        {
            actions = ParseActionList(actionsEl, "$.onDrop.actions", diagnostics);
        }

        return new(accept, mode, actions);
    }

    private static CustomWidgetScriptSpec? ParseScript(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("script", out JsonElement scriptEl))
        {
            return null;
        }

        if (scriptEl.ValueKind == JsonValueKind.False || scriptEl.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (scriptEl.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new(
                "$.script",
                "Property 'script' must be an object '{ \"entry\": \"logic.js\", \"tickIntervalMs\": 0 }'.",
                IsError: true));
            return null;
        }

        string entry = GetString(scriptEl, "entry", "logic.js").Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            entry = "logic.js";
        }

        if (!string.Equals(entry, "logic.js", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                "$.script.entry",
                $"Script entry must be 'logic.js' (got '{entry}').",
                IsError: true));
        }

        int rawTickMs = GetInt(scriptEl, "tickIntervalMs", 0);
        int tickIntervalMs = rawTickMs <= 0
            ? 0
            : Math.Clamp(rawTickMs, 1000, 3_600_000);
        return new CustomWidgetScriptSpec("logic.js", tickIntervalMs);
    }

    private static CustomWidgetCapsuleSpec? ParseCapsule(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("capsule", out JsonElement capEl) ||
            capEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty capProp in capEl.EnumerateObject())
        {
            if (string.Equals(capProp.Name, "primaryAction", StringComparison.Ordinal))
            {
                if (capProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty paProp in capProp.Value.EnumerateObject())
                    {
                        if (paProp.Name is "onClick" or "action" or "actions")
                        {
                            continue;
                        }

                        ScanElementForSecretAndUnclosedInterpolation(
                            paProp.Value,
                            $"$.capsule.primaryAction.{paProp.Name}",
                            diagnostics,
                            forbidSecrets: true,
                            checkUnclosed: true);
                    }
                }
                continue;
            }

            ScanElementForSecretAndUnclosedInterpolation(
                capProp.Value,
                $"$.capsule.{capProp.Name}",
                diagnostics,
                forbidSecrets: true,
                checkUnclosed: true);
        }

        CustomWidgetCapsuleActionSpec? primaryAction = null;
        if (capEl.TryGetProperty("primaryAction", out JsonElement paEl) &&
            paEl.ValueKind == JsonValueKind.Object)
        {
            IReadOnlyList<CustomWidgetActionSpec> onClick = [];
            if (paEl.TryGetProperty("onClick", out JsonElement onClickEl))
            {
                onClick = ParseActionList(onClickEl, "$.capsule.primaryAction.onClick", diagnostics);
            }
            else if (paEl.TryGetProperty("action", out JsonElement actionEl))
            {
                onClick = ParseActionList(actionEl, "$.capsule.primaryAction.action", diagnostics);
            }
            else if (paEl.TryGetProperty("actions", out JsonElement actionsEl))
            {
                onClick = ParseActionList(actionsEl, "$.capsule.primaryAction.actions", diagnostics);
            }

            primaryAction = new CustomWidgetCapsuleActionSpec(
                Icon: NormalizeGlyphWithDiagnostics(TryGetString(paEl, "icon"), "$.capsule.primaryAction.icon", diagnostics, "\uE710"),
                Tooltip: GetString(paEl, "tooltip", string.Empty),
                OnClick: onClick);
        }

        string fallbackGlyph = NormalizeGlyph(GetString(root, "icon", "\uE71D"));
        string? rawCapsuleGlyph = TryGetString(capEl, "glyph");
        string capsuleGlyph = string.IsNullOrWhiteSpace(rawCapsuleGlyph)
            ? fallbackGlyph
            : NormalizeGlyphWithDiagnostics(rawCapsuleGlyph, "$.capsule.glyph", diagnostics, fallbackGlyph);

        return new CustomWidgetCapsuleSpec(
            Title: GetString(capEl, "title", string.Empty),
            Summary: GetString(capEl, "summary", string.Empty),
            Glyph: capsuleGlyph,
            Badge: GetString(capEl, "badge", string.Empty),
            BadgeIsWarning: GetScalarAsString(capEl, "badgeIsWarning", "false"),
            Progress: GetScalarAsString(capEl, "progress", string.Empty),
            EnableBounceOnUpdate: GetBool(capEl, "enableBounceOnUpdate", true),
            PrimaryAction: primaryAction);
    }

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsIdentifierTokenRegex();
}
