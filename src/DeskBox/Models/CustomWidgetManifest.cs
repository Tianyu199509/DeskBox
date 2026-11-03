using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Models;

public static class CustomWidgetOriginKinds
{
    public const string Official = "official";
    public const string Ai = "ai";
    public const string Community = "community";
    public const string Agent = "agent";
    public const string Local = "local";

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Local;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            Official => Official,
            Ai => Ai,
            Community => Community,
            Agent => Agent,
            Local => Local,
            _ => Local
        };
    }
}

public static class CustomWidgetDiagnosticCodes
{
    public const string IncompatibleSchemaVersion = "IncompatibleSchemaVersion";
    public const string IncompatibleAppVersion = "IncompatibleAppVersion";
}

public sealed record CustomWidgetSizeSpec(int Width = 360, int Height = 460);

public sealed record CustomWidgetOptionSpec(
    string Value,
    string Label,
    string? Icon = null);

public sealed record CustomWidgetSettingDefinition(
    string Key,
    string Type,
    string Label,
    string DefaultValue,
    IReadOnlyList<CustomWidgetOptionSpec> Options);

public sealed record CustomWidgetCollectionSchema(
    int MaxItems = 200,
    string Sort = "newestFirst");

public sealed record CustomWidgetSourceSpec(
    string Type,
    int IntervalMs = 2000,
    string? Id = null,
    string? Url = null,
    string? TargetCollection = null,
    string? ItemsPath = null,
    IReadOnlyDictionary<string, string>? FieldMap = null,
    IReadOnlyDictionary<string, string>? StateMap = null,
    int MaxItems = 20,
    string? City = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record CustomWidgetActionSpec(
    string Type,
    string? Key = null,
    string? Value = null,
    string? Collection = null,
    string? TargetId = null,
    string? Format = null,
    string? Path = null,
    string? Url = null,
    string? Text = null,
    string? Message = null,
    string? Guard = null,
    IReadOnlyDictionary<string, string>? ItemFields = null,
    IReadOnlyList<CustomWidgetActionSpec>? Then = null,
    string? Prompt = null,
    string? Mode = null,
    string? ItemsPath = null,
    IReadOnlyDictionary<string, string>? StateMap = null,
    IReadOnlyDictionary<string, string>? FieldMap = null,
    int MaxItems = 20,
    string? Title = null,
    int MaxWidth = 0,
    double Quality = 0.88);

public sealed record CustomWidgetDropBehavior(
    string Accept,
    string Mode,
    IReadOnlyList<CustomWidgetActionSpec> Actions);

public sealed record CustomWidgetCapsuleSpec(
    string Title = "",
    string Summary = "",
    string Glyph = "\uE71D",
    string Badge = "",
    string BadgeIsWarning = "false",
    string Progress = "",
    bool EnableBounceOnUpdate = true);

public sealed record CustomWidgetEmptyStateSpec(
    string Icon = "\uE70F",
    string Title = "",
    string Subtitle = "");

public sealed record CustomWidgetNodeSpec
{
    public string Type { get; init; } = "Stack";
    public string NodePath { get; init; } = "$.view";
    public string? Id { get; init; }
    public string? When { get; init; }

    // Layout properties
    public string Orientation { get; init; } = "vertical";
    public double Spacing { get; init; } = 8;
    public string? Padding { get; init; }
    public string? Columns { get; init; }
    public string? Rows { get; init; }
    public int Column { get; init; }
    public int Row { get; init; }
    public int ColumnSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? Size { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxHeight { get; init; }
    public string? HorizontalAlignment { get; init; }
    public string? VerticalAlignment { get; init; }
    public double? CornerRadius { get; init; }
    public string Tone { get; init; } = "default";

    // Content & Typography
    public string? Text { get; init; }
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Placeholder { get; init; }
    public string? Value { get; init; }
    public string? Bind { get; init; }
    public string? Icon { get; init; }
    public string? Tooltip { get; init; }
    public string Style { get; init; } = "body";
    public string Color { get; init; } = "default";
    public string Variant { get; init; } = "standard";
    public int MaxLines { get; init; }
    public bool Strikethrough { get; init; }
    public bool DefaultExpanded { get; init; } = true;

    // Metrics, Sliders, Numbers & Micro-Charts
    public string? Progress { get; init; }
    public string? Indeterminate { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public string? Unit { get; init; }
    public double WarningThreshold { get; init; } = 75;
    public double DangerThreshold { get; init; } = 90;
    public string? WarningThresholdExpression { get; init; }
    public string? DangerThresholdExpression { get; init; }
    public string? Sparkline { get; init; }
    public bool ShowArea { get; init; } = true;
    public bool ShowLatest { get; init; }
    public string? Collection { get; init; }
    public string? TitleField { get; init; }
    public string? SubtitleField { get; init; }
    public string? ValueField { get; init; }
    public string? DateField { get; init; }
    public int Days { get; init; } = 28;
    public bool CompactPair { get; init; }

    // Image & FileChip properties
    public string? Src { get; init; }
    public string? Stretch { get; init; }
    public string? FileName { get; init; }
    public string? FilePath { get; init; }
    public string? StorageMode { get; init; }

    // List & Collection properties
    public string? Items { get; init; }
    public string? Filter { get; init; }
    public string? FilterField { get; init; }
    public string? FilterEquals { get; init; }
    public CustomWidgetEmptyStateSpec? EmptyState { get; init; }
    public CustomWidgetNodeSpec? ItemTemplate { get; init; }
    public IReadOnlyList<CustomWidgetNodeSpec> HoverActions { get; init; } = [];

    // Segmented options
    public IReadOnlyList<CustomWidgetOptionSpec> Options { get; init; } = [];

    // Interactive Actions
    public IReadOnlyList<CustomWidgetActionSpec> OnClick { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnSubmit { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnChange { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnDropActions { get; init; } = [];

    // Children
    public IReadOnlyList<CustomWidgetNodeSpec> Children { get; init; } = [];
}

public sealed record CustomWidgetManifest
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyI18nCatalog =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> EmptyLocaleDictionary =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private string? _cachedI18nCulture;
    private IReadOnlyDictionary<string, string>? _cachedI18nDictionary;

    public int SchemaVersion { get; init; } = CustomWidgetManifestParser.CurrentSchemaVersion;
    public string Id { get; init; } = "custom.widget";
    public string? PackageId { get; init; }
    public string Name { get; init; } = "Custom Widget";
    public string Version { get; init; } = "1.0.0";
    public string? MinAppVersion { get; init; }
    public string Author { get; init; } = "DeskBox";
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = "\uE71D";
    public string Origin { get; init; } = CustomWidgetOriginKinds.Local;
    public bool IsCustomized { get; init; }
    public string? ForkedFromId { get; init; }
    public string? ForkedFromVersion { get; init; }
    public string? CreatedAtUtc { get; init; }
    public string? UpdatedAtUtc { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public CustomWidgetSizeSpec MinSize { get; init; } = new(260, 220);
    public CustomWidgetSizeSpec DefaultSize { get; init; } = new(360, 460);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> I18n { get; init; } = EmptyI18nCatalog;
    public IReadOnlyList<CustomWidgetSettingDefinition> Settings { get; init; } = [];
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, CustomWidgetCollectionSchema> Collections { get; init; } =
        new Dictionary<string, CustomWidgetCollectionSchema>(StringComparer.Ordinal);
    public IReadOnlyList<CustomWidgetSourceSpec> Sources { get; init; } = [];
    public CustomWidgetDropBehavior? OnDrop { get; init; }
    public CustomWidgetCapsuleSpec? Capsule { get; init; }
    public CustomWidgetNodeSpec View { get; init; } = new();

    public IReadOnlyDictionary<string, string> ResolveI18nDictionary(string? cultureName = null)
    {
        if (I18n.Count == 0)
        {
            return EmptyLocaleDictionary;
        }

        string effective = string.IsNullOrWhiteSpace(cultureName)
            ? DeskBox.Services.Localized.ActiveCultureName
            : DeskBox.Services.LocalizationService.NormalizeCultureCode(cultureName);

        if (string.Equals(_cachedI18nCulture, effective, StringComparison.OrdinalIgnoreCase) &&
            _cachedI18nDictionary is not null)
        {
            return _cachedI18nDictionary;
        }

        IReadOnlyDictionary<string, string>? baseDict = TryFindLocaleDictionary("en-US")
            ?? TryFindLocaleDictionary("en")
            ?? I18n.Values.FirstOrDefault();

        IReadOnlyDictionary<string, string>? prefixDict = null;
        int dashIdx = effective.IndexOf('-');
        string primaryLang = dashIdx > 0 ? effective[..dashIdx] : effective;
        if (!string.IsNullOrWhiteSpace(primaryLang))
        {
            if (string.Equals(primaryLang, "zh", StringComparison.OrdinalIgnoreCase))
            {
                bool isTrad = effective.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                              effective.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                              effective.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                              effective.Contains("Hant", StringComparison.OrdinalIgnoreCase);
                prefixDict = isTrad
                    ? (TryFindLocaleDictionary("zh-TW") ?? TryFindLocaleDictionary("zh-CN") ?? TryFindLanguagePrefixDictionary("zh"))
                    : (TryFindLocaleDictionary("zh-CN") ?? TryFindLanguagePrefixDictionary("zh"));
            }
            else
            {
                prefixDict = TryFindLocaleDictionary(primaryLang) ?? TryFindLanguagePrefixDictionary(primaryLang);
            }
        }

        IReadOnlyDictionary<string, string>? exactDict = TryFindLocaleDictionary(effective);

        IReadOnlyDictionary<string, string> resolved;
        if (baseDict is null)
        {
            resolved = exactDict ?? prefixDict ?? EmptyLocaleDictionary;
        }
        else if ((prefixDict is null || ReferenceEquals(prefixDict, baseDict)) &&
                 (exactDict is null || ReferenceEquals(exactDict, baseDict)))
        {
            resolved = baseDict;
        }
        else
        {
            var merged = new Dictionary<string, string>(baseDict, StringComparer.Ordinal);
            if (prefixDict is not null && !ReferenceEquals(prefixDict, baseDict))
            {
                foreach ((string k, string v) in prefixDict)
                {
                    if (!string.IsNullOrEmpty(v))
                    {
                        merged[k] = v;
                    }
                }
            }

            if (exactDict is not null && !ReferenceEquals(exactDict, prefixDict) && !ReferenceEquals(exactDict, baseDict))
            {
                foreach ((string k, string v) in exactDict)
                {
                    if (!string.IsNullOrEmpty(v))
                    {
                        merged[k] = v;
                    }
                }
            }

            resolved = merged;
        }

        _cachedI18nCulture = effective;
        _cachedI18nDictionary = resolved;
        return resolved;
    }

    public string ResolveLocalizedText(string? raw, string? cultureName = null)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (I18n.Count == 0 ||
            (!raw.Contains("$i18n.", StringComparison.Ordinal) &&
             !raw.Contains("i18n.", StringComparison.Ordinal)))
        {
            return raw;
        }

        var ctx = new DeskBox.Services.CustomWidgetEvaluationContext
        {
            I18n = ResolveI18nDictionary(cultureName),
            CultureName = cultureName
        };
        return DeskBox.Services.CustomWidgetExpressionEvaluator.Interpolate(raw, ctx);
    }

    public string ResolveLocalizedName(string? cultureName = null)
    {
        IReadOnlyDictionary<string, string> dict = ResolveI18nDictionary(cultureName);
        if (dict.TryGetValue("widgetName", out string? wn) && !string.IsNullOrWhiteSpace(wn))
        {
            return wn;
        }

        if (dict.TryGetValue("name", out string? n) && !string.IsNullOrWhiteSpace(n))
        {
            return n;
        }

        return ResolveLocalizedText(Name, cultureName);
    }

    public string ResolveLocalizedDescription(string? cultureName = null)
    {
        IReadOnlyDictionary<string, string> dict = ResolveI18nDictionary(cultureName);
        if (dict.TryGetValue("widgetDescription", out string? wd) && !string.IsNullOrWhiteSpace(wd))
        {
            return wd;
        }

        if (dict.TryGetValue("description", out string? d) && !string.IsNullOrWhiteSpace(d))
        {
            return d;
        }

        return ResolveLocalizedText(Description, cultureName);
    }

    public IReadOnlyList<CustomWidgetSettingDefinition> ResolveLocalizedSettings(string? cultureName = null)
    {
        if (I18n.Count == 0 || Settings.Count == 0)
        {
            return Settings;
        }

        IReadOnlyDictionary<string, string> dict = ResolveI18nDictionary(cultureName);
        var list = new List<CustomWidgetSettingDefinition>(Settings.Count);
        foreach (CustomWidgetSettingDefinition def in Settings)
        {
            string label = dict.TryGetValue($"setting.{def.Key}.label", out string? convLabel) && !string.IsNullOrWhiteSpace(convLabel)
                ? convLabel
                : ResolveLocalizedText(def.Label, cultureName);

            IReadOnlyList<CustomWidgetOptionSpec> opts = def.Options;
            if (def.Options.Count > 0)
            {
                var localizedOpts = new List<CustomWidgetOptionSpec>(def.Options.Count);
                foreach (CustomWidgetOptionSpec opt in def.Options)
                {
                    string optLabel = dict.TryGetValue($"setting.{def.Key}.{opt.Value}", out string? convOpt) && !string.IsNullOrWhiteSpace(convOpt)
                        ? convOpt
                        : ResolveLocalizedText(opt.Label, cultureName);
                    localizedOpts.Add(opt with { Label = optLabel });
                }

                opts = localizedOpts;
            }

            list.Add(def with { Label = label, Options = opts });
        }

        return list;
    }

    private IReadOnlyDictionary<string, string>? TryFindLocaleDictionary(string localeKey)
    {
        if (I18n.TryGetValue(localeKey, out IReadOnlyDictionary<string, string>? exact))
        {
            return exact;
        }

        string normalized = localeKey.Replace('_', '-');
        return I18n.TryGetValue(normalized, out IReadOnlyDictionary<string, string>? normMatch)
            ? normMatch
            : null;
    }

    private IReadOnlyDictionary<string, string>? TryFindLanguagePrefixDictionary(string primaryLang)
    {
        string prefix = primaryLang + "-";
        foreach ((string k, IReadOnlyDictionary<string, string> dict) in I18n)
        {
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return dict;
            }
        }

        return null;
    }
}

public sealed record CustomWidgetValidationDiagnostic(
    string Path,
    string Message,
    bool IsError = true,
    string? Code = null);

public sealed record CustomWidgetParseResult(
    CustomWidgetManifest? Manifest,
    IReadOnlyList<CustomWidgetValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Manifest is not null && Diagnostics.All(d => !d.IsError);
}

/// <summary>
/// AOT-safe parser and validator for <see cref="CustomWidgetManifest"/>.
/// Uses <see cref="JsonDocument"/> directly with zero reflection.
/// </summary>
public static class CustomWidgetManifestParser
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

        if (!string.IsNullOrWhiteSpace(minAppVersion) &&
            TryParseLooseVersion(minAppVersion, out Version reqVer))
        {
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

    public static readonly HashSet<string> V160IntroducedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Select", "CheckBox", "Slider", "NumberBox", "TextArea",
        "IconButton", "Expander", "Sparkline", "HeatmapGrid",
        "CalendarGrid", "DropZone", "ControlTile",
        "Image", "Tabs", "RingGauge", "BarList"
    };

    private static readonly HashSet<string> SupportedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stack", "ModuleStack", "Grid", "Wrap", "Scroll", "Card", "List", "Expander", "Tabs",
        "Text", "Input", "TextArea", "Button", "IconButton", "Toggle", "CheckBox", "Select", "Segmented", "Badge",
        "Slider", "NumberBox", "Image",
        "ProgressBar", "ProgressRing", "RingGauge", "BarList", "Sparkline", "HeatmapGrid", "Divider", "Spacer",
        "StatCard", "GpuCard", "ProcessList", "PomodoroCard", "CountdownHero", "CountdownList", "TimeProgressCard",
        "AiFocusCard", "AiRadarList", "AiTokensCard", "AiTrendCard",
        "CalendarGrid", "DropZone", "ControlTile",
        "ProgressRow", "KeyValueRow", "FileChip", "EmptyState"
    };

    public static IReadOnlySet<string> AllSupportedNodeTypes => SupportedNodeTypes;

    private static readonly HashSet<string> SupportedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "state.set", "state.toggle", "state.increment",
        "collection.prepend", "collection.append", "collection.remove",
        "collection.update", "collection.clear", "collection.exportMarkdown",
        "file.open", "file.reveal", "file.transform", "file.pick",
        "text.transform", "launcher.open",
        "clipboard.copy", "url.open", "toast.show", "reminder.notify",
        "timer.toggle", "timer.reset",
        "pomodoro.toggle", "pomodoro.reset", "pomodoro.extend", "pomodoro.skip", "pomodoro.setPhase", "pomodoro.cyclePhase",
        "countdown.pin", "countdown.delete",
        "ai.refresh", "ai.selectProvider", "ai.run",
        "system.control", "system.openSetting",
        "source.refresh", "refreshSource"
    };

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

            int schemaVersion = GetInt(root, "schemaVersion", CurrentSchemaVersion);
            string id = GetString(root, "id", "custom.widget");
            string? packageId = TryGetString(root, "packageId");
            string name = GetString(root, "name", DeskBox.Services.Localized.T("CustomWidget.DefaultName"));
            string version = GetString(root, "version", "1.0.0");
            string? minAppVersion = TryGetString(root, "minAppVersion");
            string author = GetString(root, "author", "DeskBox");
            string description = GetString(root, "description", string.Empty);
            string icon = NormalizeGlyph(GetString(root, "icon", "\uE71D"));
            string origin = CustomWidgetOriginKinds.Normalize(TryGetString(root, "origin"));
            bool isCustomized = GetBool(root, "isCustomized", false);
            string? forkedFromId = TryGetString(root, "forkedFromId");
            string? forkedFromVersion = TryGetString(root, "forkedFromVersion");
            string? createdAtUtc = TryGetString(root, "createdAtUtc");
            string? updatedAtUtc = TryGetString(root, "updatedAtUtc");
            IReadOnlyList<string> tags = ParseStringArray(root, "tags");
            IReadOnlyList<string> capabilities = ParseStringArray(root, "capabilities");

            if (schemaVersion > CurrentSchemaVersion)
            {
                diagnostics.Add(new(
                    "$.schemaVersion",
                    DeskBox.Services.Localized.Format("CustomWidget.ClientUpgrade.SchemaTooNew", schemaVersion, CurrentSchemaVersion),
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

            var i18n = ParseI18n(root);
            var settings = ParseSettings(root, diagnostics);
            var state = ParseState(root);
            var collections = ParseCollections(root);
            var sources = ParseSources(root, diagnostics);
            var onDrop = ParseOnDrop(root, diagnostics);
            var capsule = ParseCapsule(root);

            CustomWidgetNodeSpec view;
            if ((root.TryGetProperty("view", out JsonElement viewElement) ||
                 root.TryGetProperty("root", out viewElement)) &&
                viewElement.ValueKind == JsonValueKind.Object)
            {
                view = ParseNode(viewElement, "$.view", diagnostics);
            }
            else
            {
                diagnostics.Add(new("$.view", "Missing required 'view' object."));
                view = new CustomWidgetNodeSpec
                {
                    Type = "EmptyState",
                    Title = name,
                    Subtitle = "Missing 'view' definition in manifest.json"
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
                I18n = i18n,
                Settings = settings,
                State = state,
                Collections = collections,
                Sources = sources,
                OnDrop = onDrop,
                Capsule = capsule,
                View = view
            };

            return new(manifest, diagnostics);
        }
    }

    private static readonly Regex StateRefRegex = new(@"\bstate\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex SettingRefRegex = new(@"\$settings\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex SecretRefRegex = new(@"\$secrets\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex CollectionRefRegex = new(@"\bcollections\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex I18nRefRegex = new(@"\$i18n\.([A-Za-z0-9_.-]+)\b", RegexOptions.Compiled);

    /// <summary>
    /// Performs full structural validation plus static analysis of state keys,
    /// settings keys, and collection references across expressions, bindings, and actions.
    /// </summary>
    public static CustomWidgetParseResult ValidateWithRuntimeHints(string json)
    {
        CustomWidgetParseResult baseResult = Parse(json);
        if (baseResult.Manifest is not { } manifest)
        {
            return baseResult;
        }

        var diagnostics = new List<CustomWidgetValidationDiagnostic>(baseResult.Diagnostics);
        var knownStateKeys = new HashSet<string>(manifest.State.Keys, StringComparer.Ordinal);
        var knownSettingsKeys = new HashSet<string>(
            manifest.Settings.Select(static s => s.Key),
            StringComparer.Ordinal);
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
                string bindKey = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? node.Bind["state.".Length..]
                    : node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : node.Bind;
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    knownStateKeys.Add(bindKey);
                }
            }

            CollectWritersFromActions(node.OnClick);
            CollectWritersFromActions(node.OnSubmit);
            CollectWritersFromActions(node.OnChange);
            CollectWritersFromActions(node.OnDropActions);
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
        CollectWritersFromActions(manifest.OnDrop?.Actions);

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
        }

        void InspectExpression(string? expr, string path)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            DeskBox.Services.CustomWidgetCapabilityRegistry.ValidateBuiltInTokensInExpression(expr, path, diagnostics);

            foreach (Match m in StateRefRegex.Matches(expr))
            {
                string key = m.Groups[1].Value;
                if (!knownStateKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared state key 'state.{key}'. Add '{key}' to root \"state\" or bind/write it in an action.",
                        IsError: false));
                }
            }

            foreach (Match m in SettingRefRegex.Matches(expr))
            {
                string key = m.Groups[1].Value;
                if (!knownSettingsKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared setting '$settings.{key}'. Declare '{key}' in root \"settings\".",
                        IsError: false));
                }
            }

            foreach (Match m in SecretRefRegex.Matches(expr))
            {
                string key = m.Groups[1].Value;
                if (!knownSecretKeys.Contains(key))
                {
                    diagnostics.Add(new(
                        path,
                        $"Expression references undeclared secret '$secrets.{key}'. Declare '{key}' with \"type\": \"secret\" in root \"settings\".",
                        IsError: false));
                }
            }

            foreach (Match m in CollectionRefRegex.Matches(expr))
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
                foreach (Match m in I18nRefRegex.Matches(expr))
                {
                    string i18nKey = m.Groups[1].Value;
                    if (!knownI18nKeys.Contains(i18nKey))
                    {
                        diagnostics.Add(new(
                            path,
                            $"Expression references undeclared i18n key '$i18n.{i18nKey}'. Declare '{i18nKey}' in root \"i18n\".",
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
                InspectExpression(act.Guard, $"{actPath}.guard");
                InspectExpression(act.Prompt, $"{actPath}.prompt");

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
                        string.IsNullOrWhiteSpace(act.Collection))
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

        void InspectNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null)
            {
                return;
            }

            InspectExpression(node.When, $"{path}.when");
            InspectExpression(node.Text, $"{path}.text");
            InspectExpression(node.Title, $"{path}.title");
            InspectExpression(node.Subtitle, $"{path}.subtitle");
            InspectExpression(node.Value, $"{path}.value");
            InspectExpression(node.Icon, $"{path}.icon");
            InspectExpression(node.Src, $"{path}.src");
            InspectExpression(node.Color, $"{path}.color");
            InspectExpression(node.Tone, $"{path}.tone");
            InspectExpression(node.Progress, $"{path}.progress");
            InspectExpression(node.Indeterminate, $"{path}.indeterminate");
            InspectExpression(node.Filter, $"{path}.filter");

            if ((string.Equals(node.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "BarList", StringComparison.OrdinalIgnoreCase) ||
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
                    !rawCol.Contains('{', StringComparison.Ordinal))
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
            InspectNode(node.ItemTemplate, $"{path}.itemTemplate");

            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                InspectNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                InspectNode(node.Children[i], $"{path}.children[{i}]");
            }
        }

        for (int i = 0; i < manifest.Sources.Count; i++)
        {
            CustomWidgetSourceSpec src = manifest.Sources[i];
            InspectExpression(src.Url, $"$.sources[{i}].url");
            InspectExpression(src.City, $"$.sources[{i}].city");
            if (src.Headers is not null)
            {
                foreach ((string hk, string hv) in src.Headers)
                {
                    InspectExpression(hv, $"$.sources[{i}].headers.{hk}");
                }
            }
        }

        InspectNode(manifest.View, "$.view");
        if (manifest.Capsule is { } cap)
        {
            InspectExpression(cap.Title, "$.capsule.title");
            InspectExpression(cap.Summary, "$.capsule.summary");
            InspectExpression(cap.Badge, "$.capsule.badge");
            InspectExpression(cap.Progress, "$.capsule.progress");
        }

        InspectActions(manifest.OnDrop?.Actions, "$.onDrop.actions");
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
        int height = Math.Clamp(GetInt(sizeEl, "height", defaultHeight), 120, 1200);
        return new(width, height);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseI18n(
        JsonElement root)
    {
        var catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("i18n", out JsonElement i18nEl) ||
            i18nEl.ValueKind != JsonValueKind.Object)
        {
            return catalog;
        }

        foreach (JsonProperty localeProp in i18nEl.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(localeProp.Name) ||
                localeProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            FlattenI18nObject(localeProp.Value, prefix: string.Empty, entries, depth: 0);
            if (entries.Count > 0)
            {
                catalog[localeProp.Name.Trim().Replace('_', '-')] = entries;
            }
        }

        return catalog;
    }

    private static void FlattenI18nObject(
        JsonElement obj,
        string prefix,
        Dictionary<string, string> destination,
        int depth)
    {
        if (depth > 3 || obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(prop.Name))
            {
                continue;
            }

            string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenI18nObject(prop.Value, key, destination, depth + 1);
            }
            else if (prop.Value.ValueKind != JsonValueKind.Array &&
                     prop.Value.ValueKind != JsonValueKind.Null &&
                     prop.Value.ValueKind != JsonValueKind.Undefined)
            {
                destination[key] = ConvertElementToString(prop.Value);
            }
        }
    }

    private static IReadOnlyList<CustomWidgetSettingDefinition> ParseSettings(
        JsonElement root,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!root.TryGetProperty("settings", out JsonElement settingsEl) ||
            settingsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CustomWidgetSettingDefinition>();
        int index = 0;
        foreach (JsonElement item in settingsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                string key = GetString(item, "key", string.Empty);
                string type = GetString(item, "type", "choice");
                string label = GetString(item, "label", key);
                string defaultValue = GetScalarAsString(item, "default", string.Empty);
                var options = ParseOptions(item, "options");
                if (string.IsNullOrWhiteSpace(key))
                {
                    diagnostics.Add(new($"$.settings[{index}]", "Setting definition is missing 'key'."));
                }
                else
                {
                    list.Add(new(key, type, label, defaultValue, options));
                }
            }
            index++;
        }

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
                string label = GetString(opt, "label", value);
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

    private static IReadOnlyDictionary<string, string> ParseState(JsonElement root)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("state", out JsonElement stateEl) ||
            stateEl.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (JsonProperty prop in stateEl.EnumerateObject())
        {
            dict[prop.Name] = ConvertElementToString(prop.Value);
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, CustomWidgetCollectionSchema> ParseCollections(
        JsonElement root)
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
                        dict[colName] = new(maxItems, sort);
                    }
                }
                else if (item.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    dict[item.GetString()!] = new();
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
                dict[prop.Name] = new(maxItems, sort);
            }
            else
            {
                dict[prop.Name] = new();
            }
        }

        return dict;
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
                    "clip" => "clipboard",
                    "lunar" or "lunarCalendar" => "calendar",
                    "pomo" => "pomodoro",
                    "http" or "json" or "rest" => "httpJson",
                    "atom" or "feed" => "rss",
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
                    int maxItems = Math.Clamp(GetInt(item, "maxItems", 20), 1, 50);

                    bool isDynamicTemplateUrl = !string.IsNullOrWhiteSpace(url) &&
                        (url.Contains("${", StringComparison.Ordinal) ||
                         url.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase) ||
                         url.StartsWith("$secrets.", StringComparison.OrdinalIgnoreCase));
                    if (!isDynamicTemplateUrl &&
                        !CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(url, out _, out string? urlError))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}].url",
                            urlError ?? "Network source requires a valid 'https://' URL.",
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
                else
                {
                    int defaultInterval = string.Equals(type, "weather", StringComparison.OrdinalIgnoreCase)
                        ? 900_000
                        : string.Equals(type, "calendar", StringComparison.OrdinalIgnoreCase)
                            ? 60_000
                            : string.Equals(type, "storageDrives", StringComparison.OrdinalIgnoreCase)
                                ? 15_000
                                : string.Equals(type, "deviceState", StringComparison.OrdinalIgnoreCase)
                                    ? 3_000
                                    : string.Equals(type, "clock", StringComparison.OrdinalIgnoreCase)
                                        ? 1_000
                                        : 2_000;
                    int intervalMs = Math.Clamp(GetInt(item, "intervalMs", defaultInterval), 500, 3_600_000);
                    if (!string.Equals(type, "systemMetrics", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "clock", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "pomodoro", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "countdown", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "aiMonitor", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "weather", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "calendar", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "deviceState", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "storageDrives", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(type, "clipboard", StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new(
                            $"$.sources[{index}]",
                            $"Unknown source type '{type}'. Supported: 'systemMetrics', 'weather', 'calendar', 'deviceState', 'storageDrives', 'clipboard', 'clock', 'pomodoro', 'countdown', 'aiMonitor', 'httpJson', 'rss'.",
                            IsError: false));
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
                                                    : null);
                        list.Add(new CustomWidgetSourceSpec(
                            Type: type,
                            IntervalMs: intervalMs,
                            Id: sourceId,
                            TargetCollection: targetCol,
                            City: city));
                    }
                }
            }
            index++;
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

    private static CustomWidgetCapsuleSpec? ParseCapsule(JsonElement root)
    {
        if (!root.TryGetProperty("capsule", out JsonElement capEl) ||
            capEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CustomWidgetCapsuleSpec(
            Title: GetString(capEl, "title", string.Empty),
            Summary: GetString(capEl, "summary", string.Empty),
            Glyph: NormalizeGlyph(GetString(capEl, "glyph", "\uE71D")),
            Badge: GetString(capEl, "badge", string.Empty),
            BadgeIsWarning: GetScalarAsString(capEl, "badgeIsWarning", "false"),
            Progress: GetScalarAsString(capEl, "progress", string.Empty),
            EnableBounceOnUpdate: GetBool(capEl, "enableBounceOnUpdate", true));
    }

    private const int MaxNodeTreeDepth = 64;
    private const int MaxActionChainDepth = 64;

    private static CustomWidgetNodeSpec ParseNode(
        JsonElement el,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        int depth = 0)
    {
        if (depth > MaxNodeTreeDepth)
        {
            diagnostics.Add(new(
                path,
                $"Component nesting depth exceeds the maximum allowed depth ({MaxNodeTreeDepth})."));
            return new CustomWidgetNodeSpec { Type = "Stack" };
        }

        string rawNodeType = GetString(el, "type", "Stack");
        string? impliedOrientation = null;
        string type = rawNodeType.Trim() switch
        {
            "Row" or "row" or "HStack" or "hstack" => (impliedOrientation = "horizontal") is not null ? "Stack" : "Stack",
            "Column" or "column" or "VStack" or "vstack" => (impliedOrientation = "vertical") is not null ? "Stack" : "Stack",
            _ => rawNodeType
        };
        if (!SupportedNodeTypes.Contains(type))
        {
            diagnostics.Add(new(
                path,
                $"Unknown component type '{type}'. Supported types: {string.Join(", ", SupportedNodeTypes)}."));
        }

        CustomWidgetEmptyStateSpec? emptyState = null;
        if (el.TryGetProperty("emptyState", out JsonElement emptyEl) &&
            emptyEl.ValueKind == JsonValueKind.Object)
        {
            emptyState = new CustomWidgetEmptyStateSpec(
                Icon: NormalizeGlyph(GetString(emptyEl, "icon", "\uE70F")),
                Title: GetString(emptyEl, "title", string.Empty),
                Subtitle: GetString(emptyEl, "subtitle", string.Empty));
        }

        CustomWidgetNodeSpec? itemTemplate = null;
        if (el.TryGetProperty("itemTemplate", out JsonElement itemTplEl) &&
            itemTplEl.ValueKind == JsonValueKind.Object)
        {
            itemTemplate = ParseNode(itemTplEl, $"{path}.itemTemplate", diagnostics, depth + 1);
        }

        var hoverActions = new List<CustomWidgetNodeSpec>();
        if (el.TryGetProperty("hoverActions", out JsonElement hoverEl) &&
            hoverEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement child in hoverEl.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    hoverActions.Add(ParseNode(child, $"{path}.hoverActions[{idx}]", diagnostics, depth + 1));
                }
                idx++;
            }
        }

        var children = new List<CustomWidgetNodeSpec>();
        if (el.TryGetProperty("children", out JsonElement childrenEl) &&
            childrenEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement child in childrenEl.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    children.Add(ParseNode(child, $"{path}.children[{idx}]", diagnostics, depth + 1));
                }
                idx++;
            }
        }

        string? iconRaw = TryGetString(el, "icon");
        string rawOrientation = TryGetString(el, "orientation")
            ?? TryGetString(el, "direction")
            ?? impliedOrientation
            ?? "vertical";
        string normalizedOrientation = string.Equals(rawOrientation, "horizontal", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(rawOrientation, "row", StringComparison.OrdinalIgnoreCase)
            ? "horizontal"
            : "vertical";

        return new CustomWidgetNodeSpec
        {
            Type = type,
            NodePath = path,
            Id = TryGetString(el, "id"),
            When = TryGetString(el, "$when") ?? TryGetString(el, "when") ?? TryGetScalarAsString(el, "visible"),
            Orientation = normalizedOrientation,
            Spacing = GetDouble(el, "spacing", 8),
            Padding = TryGetScalarAsString(el, "padding"),
            Columns = TryGetString(el, "columns"),
            Rows = TryGetString(el, "rows"),
            Column = el.TryGetProperty("column", out _) ? GetInt(el, "column", 0) : GetInt(el, "col", 0),
            Row = GetInt(el, "row", 0),
            ColumnSpan = Math.Max(1, el.TryGetProperty("columnSpan", out _) ? GetInt(el, "columnSpan", 1) : GetInt(el, "colSpan", 1)),
            RowSpan = Math.Max(1, el.TryGetProperty("rowSpan", out _) ? GetInt(el, "rowSpan", 1) : GetInt(el, "rowspan", 1)),
            Width = TryGetDouble(el, "width"),
            Height = TryGetDouble(el, "height"),
            Size = TryGetDouble(el, "size"),
            MinHeight = TryGetDouble(el, "minHeight"),
            MaxHeight = TryGetDouble(el, "maxHeight"),
            HorizontalAlignment = TryGetString(el, "horizontalAlignment"),
            VerticalAlignment = TryGetString(el, "verticalAlignment"),
            CornerRadius = TryGetDouble(el, "cornerRadius"),
            Tone = GetString(el, "tone", "default"),
            Text = TryGetScalarAsString(el, "text"),
            Title = TryGetScalarAsString(el, "title") ?? TryGetScalarAsString(el, "label"),
            Subtitle = TryGetScalarAsString(el, "subtitle"),
            Placeholder = TryGetString(el, "placeholder"),
            Value = TryGetScalarAsString(el, "value") ?? TryGetScalarAsString(el, "checked") ?? TryGetScalarAsString(el, "values"),
            Bind = TryGetString(el, "bind"),
            Icon = iconRaw is null ? null : NormalizeGlyph(iconRaw),
            Tooltip = TryGetString(el, "tooltip"),
            Style = GetString(el, "style", "body"),
            Color = GetString(el, "color", "default"),
            Variant = GetString(el, "variant", "standard"),
            MaxLines = GetInt(el, "maxLines", 0),
            Strikethrough = GetBool(el, "strikethrough", false),
            DefaultExpanded = GetBool(el, "defaultExpanded", GetBool(el, "isExpanded", true)),
            Progress = TryGetScalarAsString(el, "progress"),
            Indeterminate = TryGetScalarAsString(el, "indeterminate"),
            Min = GetDouble(el, "min", 0),
            Max = GetDouble(el, "max", 100),
            Step = Math.Max(0.001, GetDouble(el, "step", 1)),
            Unit = TryGetScalarAsString(el, "unit"),
            WarningThreshold = GetDouble(el, "warningThreshold", 75),
            DangerThreshold = GetDouble(el, "dangerThreshold", 90),
            WarningThresholdExpression = TryGetScalarAsString(el, "warningThreshold"),
            DangerThresholdExpression = TryGetScalarAsString(el, "dangerThreshold"),
            Sparkline = TryGetString(el, "sparkline") ?? TryGetScalarAsString(el, "values"),
            ShowArea = GetBool(el, "showArea", true),
            ShowLatest = GetBool(el, "showLatest", false),
            Collection = TryGetString(el, "collection") ?? TryGetString(el, "eventsCollection"),
            TitleField = TryGetString(el, "titleField") ?? TryGetString(el, "labelField"),
            SubtitleField = TryGetString(el, "subtitleField"),
            ValueField = TryGetString(el, "valueField"),
            DateField = TryGetString(el, "dateField"),
            Days = Math.Clamp(GetInt(el, "days", 28), 7, 180),
            CompactPair = GetBool(el, "compactPair", false),
            Src = TryGetScalarAsString(el, "src") ?? TryGetScalarAsString(el, "url") ?? TryGetScalarAsString(el, "imageUrl"),
            Stretch = TryGetString(el, "stretch") ?? TryGetString(el, "fit"),
            FileName = TryGetScalarAsString(el, "fileName"),
            FilePath = TryGetScalarAsString(el, "filePath"),
            StorageMode = TryGetScalarAsString(el, "storageMode"),
            Items = TryGetString(el, "items") ?? TryGetString(el, "itemsSource"),
            Filter = TryGetString(el, "filter"),
            FilterField = TryGetString(el, "filterField"),
            FilterEquals = TryGetString(el, "filterEquals"),
            EmptyState = emptyState,
            ItemTemplate = itemTemplate,
            HoverActions = hoverActions,
            Options = ParseOptions(el, "options"),
            OnClick = el.TryGetProperty("onClick", out _)
                ? ParseNodeActions(el, "onClick", path, diagnostics)
                : ParseNodeActions(el, "actions", path, diagnostics),
            OnSubmit = ParseNodeActions(el, "onSubmit", path, diagnostics),
            OnChange = el.TryGetProperty("onChange", out _)
                ? ParseNodeActions(el, "onChange", path, diagnostics)
                : ParseNodeActions(el, "onToggle", path, diagnostics),
            OnDropActions = ParseNodeActions(el, "onDrop", path, diagnostics),
            Children = children
        };
    }

    private static IReadOnlyList<CustomWidgetActionSpec> ParseNodeActions(
        JsonElement nodeEl,
        string propName,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!nodeEl.TryGetProperty(propName, out JsonElement actionEl))
        {
            return [];
        }

        return ParseActionList(actionEl, $"{path}.{propName}", diagnostics);
    }

    private static IReadOnlyList<CustomWidgetActionSpec> ParseActionList(
        JsonElement el,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        int depth = 0)
    {
        if (depth > MaxActionChainDepth)
        {
            diagnostics.Add(new(
                path,
                $"Action chain depth exceeds the maximum allowed depth ({MaxActionChainDepth})."));
            return [];
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            var single = ParseSingleAction(el, path, diagnostics, depth);
            return single is null ? [] : [single];
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<CustomWidgetActionSpec>();
            int idx = 0;
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseSingleAction(item, $"{path}[{idx}]", diagnostics, depth);
                    if (parsed is not null)
                    {
                        list.Add(parsed);
                    }
                }
                idx++;
            }
            return list;
        }

        return [];
    }

    private static CustomWidgetActionSpec? ParseSingleAction(
        JsonElement el,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        int depth)
    {
        string type = GetString(el, "type", string.Empty);
        if (string.IsNullOrWhiteSpace(type))
        {
            diagnostics.Add(new(path, "Action object is missing 'type'."));
            return null;
        }

        if (!SupportedActionTypes.Contains(type))
        {
            diagnostics.Add(new(
                path,
                $"Unknown action type '{type}'. Supported: {string.Join(", ", SupportedActionTypes)}."));
        }

        Dictionary<string, string>? itemFields = null;
        if ((el.TryGetProperty("item", out JsonElement itemEl) ||
             el.TryGetProperty("fields", out itemEl)) &&
            itemEl.ValueKind == JsonValueKind.Object)
        {
            itemFields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in itemEl.EnumerateObject())
            {
                itemFields[prop.Name] = ConvertElementToString(prop.Value);
            }
        }

        IReadOnlyList<CustomWidgetActionSpec>? thenActions = null;
        if (el.TryGetProperty("then", out JsonElement thenEl))
        {
            thenActions = ParseActionList(thenEl, $"{path}.then", diagnostics, depth + 1);
        }

        return new CustomWidgetActionSpec(
            Type: type,
            Key: TryGetString(el, "key") ?? TryGetString(el, "command"),
            Value: TryGetScalarAsString(el, "value"),
            Collection: TryGetString(el, "collection") ?? TryGetString(el, "targetCollection"),
            TargetId: TryGetScalarAsString(el, "targetId") ??
                      TryGetScalarAsString(el, "target") ??
                      TryGetScalarAsString(el, "id") ??
                      TryGetScalarAsString(el, "sourceId") ??
                      TryGetScalarAsString(el, "source"),
            Format: TryGetScalarAsString(el, "format") ??
                    TryGetScalarAsString(el, "op") ??
                    TryGetScalarAsString(el, "operation"),
            Path: TryGetScalarAsString(el, "path"),
            Url: TryGetScalarAsString(el, "url"),
            Text: TryGetScalarAsString(el, "text"),
            Message: TryGetScalarAsString(el, "message"),
            Guard: TryGetString(el, "guard") ?? TryGetString(el, "$when"),
            ItemFields: itemFields,
            Then: thenActions,
            Prompt: TryGetString(el, "prompt"),
            Mode: TryGetString(el, "mode"),
            ItemsPath: TryGetString(el, "itemsPath"),
            StateMap: ParseStringDictionary(el, "stateMap"),
            FieldMap: ParseStringDictionary(el, "fieldMap"),
            MaxItems: Math.Clamp(GetInt(el, "maxItems", 20), 1, 50),
            Title: TryGetScalarAsString(el, "title"),
            MaxWidth: GetInt(el, "maxWidth", 0),
            Quality: GetDouble(el, "quality", 0.88));
    }

    private static string GetString(JsonElement el, string name, string fallback) =>
        TryGetString(el, name) ?? fallback;

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out JsonElement prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static string GetScalarAsString(JsonElement el, string name, string fallback) =>
        TryGetScalarAsString(el, name) ?? fallback;

    private static string? TryGetScalarAsString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement prop))
        {
            return null;
        }
        return ConvertElementToString(prop);
    }

    private static string ConvertElementToString(JsonElement prop) =>
        prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => prop.GetRawText()
        };

    private static int GetInt(JsonElement el, string name, int fallback)
    {
        if (el.TryGetProperty(name, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
            {
                return v;
            }
            if (prop.ValueKind == JsonValueKind.String &&
                int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sv))
            {
                return sv;
            }
        }
        return fallback;
    }

    private static double GetDouble(JsonElement el, string name, double fallback) =>
        TryGetDouble(el, name) ?? fallback;

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v))
            {
                return v;
            }
            if (prop.ValueKind == JsonValueKind.String &&
                double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
            {
                return sv;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (JsonElement item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!.Trim());
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string>? ParseStringDictionary(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in prop.EnumerateObject())
        {
            dict[entry.Name] = ConvertElementToString(entry.Value);
        }
        return dict.Count > 0 ? dict : null;
    }

    private static bool GetBool(JsonElement el, string name, bool fallback)
    {
        if (el.TryGetProperty(name, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String &&
                bool.TryParse(prop.GetString(), out bool b))
            {
                return b;
            }
        }
        return fallback;
    }
}

/// <summary>
/// AOT-safe JSON metadata stamper for <c>manifest.json</c> provenance and versioning fields.
/// Preserves all existing JSON properties while updating or inserting provenance keys.
/// </summary>
public static class CustomWidgetManifestMetadataStamper
{
    public static string StampProvenance(
        string rawManifestJson,
        string origin,
        bool isCustomized,
        string? forkedFromId = null,
        string? forkedFromVersion = null,
        string? updatedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return rawManifestJson;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch
        {
            return rawManifestJson;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return rawManifestJson;
            }

            string normalizedOrigin = CustomWidgetOriginKinds.Normalize(origin);
            string nowIso = updatedAtUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            string? effectiveForkedId = forkedFromId;
            string? effectiveForkedVersion = forkedFromVersion;
            string? existingCreatedAt = null;

            if (doc.RootElement.TryGetProperty("forkedFromId", out JsonElement existingForkId) &&
                existingForkId.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(effectiveForkedId))
            {
                effectiveForkedId = existingForkId.GetString();
            }

            if (doc.RootElement.TryGetProperty("forkedFromVersion", out JsonElement existingForkVer) &&
                existingForkVer.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(effectiveForkedVersion))
            {
                effectiveForkedVersion = existingForkVer.GetString();
            }

            if (doc.RootElement.TryGetProperty("createdAtUtc", out JsonElement existingCreated) &&
                existingCreated.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(existingCreated.GetString()))
            {
                existingCreatedAt = existingCreated.GetString();
            }

            var managedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "origin",
                "isCustomized",
                "forkedFromId",
                "forkedFromVersion",
                "createdAtUtc",
                "updatedAtUtc"
            };

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       ms,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                bool injectedMetadata = false;

                void WriteMetadataBlock()
                {
                    if (injectedMetadata)
                    {
                        return;
                    }

                    injectedMetadata = true;
                    writer.WriteString("origin", normalizedOrigin);
                    writer.WriteBoolean("isCustomized", isCustomized);
                    if (!string.IsNullOrWhiteSpace(effectiveForkedId))
                    {
                        writer.WriteString("forkedFromId", effectiveForkedId);
                    }
                    if (!string.IsNullOrWhiteSpace(effectiveForkedVersion))
                    {
                        writer.WriteString("forkedFromVersion", effectiveForkedVersion);
                    }
                    writer.WriteString("createdAtUtc", existingCreatedAt ?? nowIso);
                    writer.WriteString("updatedAtUtc", nowIso);
                }

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (managedKeys.Contains(prop.Name))
                    {
                        continue;
                    }

                    prop.WriteTo(writer);

                    if (!injectedMetadata &&
                        (string.Equals(prop.Name, "icon", StringComparison.Ordinal) ||
                         string.Equals(prop.Name, "description", StringComparison.Ordinal)))
                    {
                        WriteMetadataBlock();
                    }
                }

                WriteMetadataBlock();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public static bool ShouldAllowAutomaticOfficialUpgrade(string rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return true;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("isCustomized", out JsonElement customizedEl))
            {
                if (customizedEl.ValueKind == JsonValueKind.True ||
                    (customizedEl.ValueKind == JsonValueKind.String &&
                     string.Equals(customizedEl.GetString(), "true", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            if (doc.RootElement.TryGetProperty("origin", out JsonElement originEl) &&
                originEl.ValueKind == JsonValueKind.String)
            {
                string? origin = originEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(origin) &&
                    !string.Equals(origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record CustomWidgetModuleEntry(
    string Id,
    string Label,
    bool Enabled,
    string? Icon = null);

public static class CustomWidgetModuleListHelper
{
    public static IReadOnlyList<CustomWidgetModuleEntry> ParseEntries(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions)
    {
        if (definedOptions.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return [];
            }

            var adHoc = new List<CustomWidgetModuleEntry>();
            foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (string id, bool? explicitState) = SplitToken(rawToken);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    adHoc.Add(new CustomWidgetModuleEntry(id, id, explicitState ?? true));
                }
            }
            return adHoc;
        }

        var optionMap = new Dictionary<string, CustomWidgetOptionSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetOptionSpec opt in definedOptions)
        {
            if (!string.IsNullOrWhiteSpace(opt.Value))
            {
                optionMap[opt.Value] = opt;
            }
        }

        var entries = new List<CustomWidgetModuleEntry>(definedOptions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasAnyToken = false;
        bool usesExplicitFlags = false;

        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (string id, bool? explicitState) = SplitToken(rawToken);
                if (explicitState.HasValue)
                {
                    usesExplicitFlags = true;
                }

                if (string.IsNullOrWhiteSpace(id) ||
                    !optionMap.TryGetValue(id, out CustomWidgetOptionSpec? opt) ||
                    !seen.Add(opt.Value))
                {
                    continue;
                }

                hasAnyToken = true;
                entries.Add(new CustomWidgetModuleEntry(
                    opt.Value,
                    opt.Label,
                    explicitState ?? true,
                    opt.Icon));
            }
        }

        foreach (CustomWidgetOptionSpec opt in definedOptions)
        {
            if (string.IsNullOrWhiteSpace(opt.Value) || !seen.Add(opt.Value))
            {
                continue;
            }

            bool defaultEnabled = !hasAnyToken || usesExplicitFlags;
            entries.Add(new CustomWidgetModuleEntry(opt.Value, opt.Label, defaultEnabled, opt.Icon));
        }

        if (entries.Count > 0 && entries.All(static e => !e.Enabled))
        {
            entries[0] = entries[0] with { Enabled = true };
        }

        return entries;
    }

    public static string SerializeEntries(IEnumerable<CustomWidgetModuleEntry> entries) =>
        string.Join(',', entries.Select(static e => $"{e.Id}:{(e.Enabled ? "1" : "0")}"));

    public static bool IsModuleEnabled(
        string? rawValue,
        string moduleId,
        IReadOnlyList<CustomWidgetOptionSpec>? definedOptions = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return false;
        }

        if (definedOptions is { Count: > 0 })
        {
            IReadOnlyList<CustomWidgetModuleEntry> parsed = ParseEntries(rawValue, definedOptions);
            CustomWidgetModuleEntry? match = parsed.FirstOrDefault(
                e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            return match?.Enabled ?? true;
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        bool usesExplicitFlags = false;
        foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (string id, bool? explicitState) = SplitToken(rawToken);
            if (explicitState.HasValue)
            {
                usesExplicitFlags = true;
            }
            if (string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase))
            {
                return explicitState ?? true;
            }
        }

        return usesExplicitFlags;
    }

    public static string SetModuleEnabled(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string moduleId,
        bool enabled)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        int idx = list.FindIndex(e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return SerializeEntries(list);
        }

        if (!enabled && list.Count(static e => e.Enabled) <= 1 && list[idx].Enabled)
        {
            // Keep at least one module visible on the widget.
            return SerializeEntries(list);
        }

        list[idx] = list[idx] with { Enabled = enabled };
        return SerializeEntries(list);
    }

    public static string MoveModule(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string moduleId,
        int delta)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        int idx = list.FindIndex(e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || delta == 0)
        {
            return SerializeEntries(list);
        }

        int targetIdx = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (targetIdx == idx)
        {
            return SerializeEntries(list);
        }

        CustomWidgetModuleEntry item = list[idx];
        list.RemoveAt(idx);
        list.Insert(targetIdx, item);
        return SerializeEntries(list);
    }

    public static string ReorderEnabledModule(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string draggedModuleId,
        int targetEnabledIndex)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        var enabledIndices = new List<int>();
        int draggedEnabledSlot = -1;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Enabled)
            {
                if (string.Equals(list[i].Id, draggedModuleId, StringComparison.OrdinalIgnoreCase))
                {
                    draggedEnabledSlot = enabledIndices.Count;
                }
                enabledIndices.Add(i);
            }
        }

        if (draggedEnabledSlot < 0 || enabledIndices.Count <= 1)
        {
            return SerializeEntries(list);
        }

        int clampedSlot = Math.Clamp(targetEnabledIndex, 0, enabledIndices.Count - 1);
        if (clampedSlot == draggedEnabledSlot)
        {
            return SerializeEntries(list);
        }

        var enabledItems = enabledIndices.Select(idx => list[idx]).ToList();
        CustomWidgetModuleEntry draggedItem = enabledItems[draggedEnabledSlot];
        enabledItems.RemoveAt(draggedEnabledSlot);
        enabledItems.Insert(clampedSlot, draggedItem);

        for (int s = 0; s < enabledIndices.Count; s++)
        {
            list[enabledIndices[s]] = enabledItems[s];
        }

        return SerializeEntries(list);
    }

    private static (string Id, bool? Enabled) SplitToken(string rawToken)
    {
        int colon = rawToken.IndexOf(':');
        if (colon < 0)
        {
            return (rawToken.Trim(), null);
        }

        string id = rawToken[..colon].Trim();
        string flag = rawToken[(colon + 1)..].Trim();
        bool enabled = !string.Equals(flag, "0", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(flag, "off", StringComparison.OrdinalIgnoreCase);
        return (id, enabled);
    }
}
