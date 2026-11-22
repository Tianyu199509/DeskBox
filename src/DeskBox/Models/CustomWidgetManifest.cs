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

        return raw.AsSpan().Trim() switch
        {
            var s when s.Equals(Official, StringComparison.OrdinalIgnoreCase) => Official,
            var s when s.Equals(Ai, StringComparison.OrdinalIgnoreCase) => Ai,
            var s when s.Equals(Community, StringComparison.OrdinalIgnoreCase) => Community,
            var s when s.Equals(Agent, StringComparison.OrdinalIgnoreCase) => Agent,
            var s when s.Equals(Local, StringComparison.OrdinalIgnoreCase) => Local,
            _ => Local
        };
    }
}

public static class CustomWidgetDiagnosticCodes
{
    public const string IncompatibleSchemaVersion = "IncompatibleSchemaVersion";
    public const string IncompatibleAppVersion = "IncompatibleAppVersion";
    public const string SecretInterpolationForbiddenInViewOrState = "SecretInterpolationForbiddenInViewOrState";
}

public static class CustomWidgetSourceTypes
{
    public const string LocalFolder = "localFolder";
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
    IReadOnlyList<CustomWidgetOptionSpec> Options,
    string? Accept = null,
    string? Placeholder = null,
    string Description = "");

public sealed record CustomWidgetGradientStopSpec(
    double Offset,
    string Color);

public sealed record CustomWidgetLinearGradientSpec(
    double Angle,
    IReadOnlyList<CustomWidgetGradientStopSpec> Stops);

public sealed record CustomWidgetRadialGradientSpec(
    double CenterX,
    double CenterY,
    double RadiusX,
    double RadiusY,
    IReadOnlyList<CustomWidgetGradientStopSpec> Stops);

public sealed record CustomWidgetTextSpanSpec(
    string Text,
    string? Color = null,
    double? FontSize = null,
    string? FontWeight = null,
    string? FontFamily = null,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    bool Monospace = false,
    string? Href = null,
    bool Bold = false,
    IReadOnlyList<CustomWidgetActionSpec>? OnClick = null);

public sealed record CustomWidgetMenuItemSpec(
    string Label,
    string? Icon = null,
    bool IsSeparator = false,
    string? When = null,
    IReadOnlyList<CustomWidgetActionSpec>? OnClick = null,
    string? Value = null,
    IReadOnlyList<CustomWidgetMenuItemSpec>? Children = null);

public sealed record CustomWidgetTableColumnSpec(
    string Field,
    string Header,
    string Width = "*",
    string Align = "left",
    string? Color = null,
    string? Format = null);

public sealed record CustomWidgetChartSeriesSpec(
    string Name,
    string ValueField,
    string? Color = null,
    string? Kind = null,
    IReadOnlyList<double>? Values = null);

public sealed record CustomWidgetCanvasElementSpec(
    string Type,
    string? Id = null,
    string? X = null,
    string? Y = null,
    string? X2 = null,
    string? Y2 = null,
    string? Width = null,
    string? Height = null,
    string? Radius = null,
    string? Rx = null,
    string? Ry = null,
    string? Data = null,
    string? Points = null,
    string? Text = null,
    string? Fill = null,
    string? Stroke = null,
    double StrokeWidth = 1.5,
    double Opacity = 1.0,
    double FontSize = 12,
    double Rotation = 0,
    string? Align = null,
    string? Cx = null,
    string? Cy = null,
    string? FontWeight = null,
    string? RotationExpression = null,
    string? StartAngle = null,
    string? EndAngle = null,
    string? SweepAngle = null,
    string? StrokeCap = null,
    CustomWidgetLinearGradientSpec? LinearGradient = null,
    CustomWidgetRadialGradientSpec? RadialGradient = null);

public sealed record CustomWidgetCollectionSchema(
    int MaxItems = 200,
    string Sort = "newestFirst",
    IReadOnlyList<DeskBox.Services.CustomWidgetRecord>? SeedItems = null);

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
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Path = null,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int TimeoutMs = 10_000,
    int TailLines = 0,
    IReadOnlyList<string>? Extensions = null,
    bool Recursive = false,
    string? Sort = null);

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
    double Quality = 0.88,
    string? Fn = null,
    string? Arg = null,
    string? Method = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null,
    int TimeoutMs = 15_000,
    IReadOnlyDictionary<string, string>? Args = null,
    IReadOnlyList<string>? CommandArgs = null,
    string? WorkingDirectory = null,
    string? PopoverId = null,
    string? FromIndex = null,
    string? ToIndex = null,
    string? Id = null,
    string? JobKey = null,
    bool LongRunning = false,
    string? OnProgress = null,
    string? OnComplete = null,
    bool Multiple = false,
    string? Accept = null,
    bool Recursive = false,
    string? Pattern = null,
    string? Sort = null,
    string? Destination = null,
    JsonElement? RawJsonValue = null,
    bool Loop = false,
    string? Volume = null,
    IReadOnlyDictionary<string, JsonElement>? ItemFieldsJson = null,
    string? DueIso = null,
    string? Priority = null,
    IReadOnlyDictionary<string, JsonElement>? ArgsJson = null);

public sealed record CustomWidgetScriptSpec(
    string Entry = "logic.js",
    int TickIntervalMs = 0);

public sealed record CustomWidgetCapsuleActionSpec(
    string Icon = "\uE710",
    string Tooltip = "",
    IReadOnlyList<CustomWidgetActionSpec>? OnClick = null);

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
    bool EnableBounceOnUpdate = true,
    CustomWidgetCapsuleActionSpec? PrimaryAction = null);

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

    // Layout & Free Raw Styling properties
    public string Orientation { get; init; } = "vertical";
    public double? Flex { get; init; }
    public double Spacing { get; init; } = 8;
    public bool HasExplicitSpacing { get; init; }
    public double? ColumnSpacing { get; init; }
    public double? RowSpacing { get; init; }
    public string? Dir { get; init; }
    public string? Padding { get; init; }
    public string? Margin { get; init; }
    public string? Columns { get; init; }
    public string? Rows { get; init; }
    public int Column { get; init; }
    public int Row { get; init; }
    public bool HasExplicitColumn { get; init; }
    public bool HasExplicitRow { get; init; }
    public int ColumnSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double? Size { get; init; }
    public double? MinWidth { get; init; }
    public double? MaxWidth { get; init; }
    public double? MinHeight { get; init; }
    public double? MaxHeight { get; init; }
    public string? HorizontalAlignment { get; init; }
    public string? VerticalAlignment { get; init; }
    public double? CornerRadius { get; init; }
    public string Tone { get; init; } = "default";
    public string? Background { get; init; }
    public CustomWidgetLinearGradientSpec? LinearGradient { get; init; }
    public CustomWidgetRadialGradientSpec? RadialGradient { get; init; }
    public string? Backdrop { get; init; }
    public string? Shadow { get; init; }
    public double? AspectRatio { get; init; }
    public string? TextColor { get; init; }
    public string? BorderColor { get; init; }
    public string? BorderWidth { get; init; }
    public double? FontSize { get; init; }
    public string? FontWeight { get; init; }
    public string? FontFamily { get; init; }
    public double? Opacity { get; init; }
    public string? OpacityExpression { get; init; }

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
    public bool AnimateChange { get; init; }
    public string? TextAlignment { get; init; }
    public IReadOnlyList<CustomWidgetTextSpanSpec> Spans { get; init; } = [];
    public string? Language { get; init; }
    public bool WrapText { get; init; } = true;
    public bool AutoScrollTail { get; init; }
    public bool ShowCopyButton { get; init; } = true;
    public bool DefaultExpanded { get; init; } = true;

    // Popover, InfoBar, TeachingTip, InfoBadge, SplitView, FlipView & ItemsGrid properties
    public string? PopoverId { get; init; }
    public string? Popover { get; init; }
    public string? Placement { get; init; }
    public string Severity { get; init; } = "informational";
    public bool IsClosable { get; init; } = true;
    public string? Target { get; init; }
    public string? CloseButtonText { get; init; }
    public CustomWidgetNodeSpec? ActionButton { get; init; }
    public CustomWidgetNodeSpec? Pane { get; init; }
    public string DisplayMode { get; init; } = "compactInline";
    public double PaneWidth { get; init; } = 180;
    public double CompactPaneWidth { get; init; } = 40;
    public string PanePlacement { get; init; } = "left";
    public string? IsOpen { get; init; }
    public double? MinItemWidth { get; init; }
    public int MaxItems { get; init; }
    public bool ShowPager { get; init; } = true;
    public IReadOnlyList<CustomWidgetMenuItemSpec> MenuItems { get; init; } = [];
    public IReadOnlyList<CustomWidgetMenuItemSpec> ContextMenu { get; init; } = [];

    // Metrics, Sliders, Numbers, RadialGauge, Charts & Canvas
    public string? Progress { get; init; }
    public string? Indeterminate { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 100;
    public double Step { get; init; } = 1;
    public string? Unit { get; init; }
    public double StartAngle { get; init; } = -135;
    public double SweepAngle { get; init; } = 270;
    public double GaugeThickness { get; init; } = 10;
    public bool ShowTicks { get; init; }
    public double TickStep { get; init; }
    public bool IsInteractive { get; init; }
    public double? WarningThreshold { get; init; }
    public double? DangerThreshold { get; init; }
    public string? WarningThresholdExpression { get; init; }
    public string? DangerThresholdExpression { get; init; }
    public string? Sparkline { get; init; }
    public bool ShowArea { get; init; } = true;
    public bool ShowLatest { get; init; }
    public string ChartKind { get; init; } = "line";
    public string? ColorField { get; init; }
    public string? XField { get; init; }
    public string? CenterTitle { get; init; }
    public string? CenterValue { get; init; }
    public bool ShowLegend { get; init; } = true;
    public bool ShowGrid { get; init; } = true;
    public IReadOnlyList<CustomWidgetChartSeriesSpec> Series { get; init; } = [];
    public string? ViewBox { get; init; }
    public string? ElementsSource { get; init; }
    public IReadOnlyList<CustomWidgetCanvasElementSpec> Elements { get; init; } = [];
    public string? Collection { get; init; }
    public string? TitleField { get; init; }
    public string? SubtitleField { get; init; }
    public string? ValueField { get; init; }
    public string? DateField { get; init; }
    public int Days { get; init; } = 28;
    public bool CompactPair { get; init; }
    public string? PairGroup { get; init; }
    public string? PairWidth { get; init; }
    public string? PinTrailingGroup { get; init; }

    // Table properties
    public IReadOnlyList<CustomWidgetTableColumnSpec> TableColumns { get; init; } = [];
    public bool ShowHeader { get; init; } = true;
    public bool Striped { get; init; }
    public bool Sortable { get; init; } = true;

    // Advanced Inputs (AutoSuggest, ColorPicker, Rating)
    public string? SuggestionsSource { get; init; }
    public IReadOnlyList<string> Suggestions { get; init; } = [];
    public IReadOnlyList<string> Presets { get; init; } = [];
    public bool AllowHalfStar { get; init; }

    // Image, MediaPlayer & FileChip properties
    public string? Src { get; init; }
    public string? Poster { get; init; }
    public string? Stretch { get; init; }
    public bool ShowTransportControls { get; init; } = true;
    public string? ShowTransportExpression { get; init; }
    public bool AutoPlay { get; init; }
    public string? AutoPlayExpression { get; init; }
    public bool Muted { get; init; }
    public string? MutedExpression { get; init; }
    public bool Loop { get; init; }
    public string? LoopExpression { get; init; }
    public string? Volume { get; init; }
    public string? FileName { get; init; }
    public string? FilePath { get; init; }
    public string? StorageMode { get; init; }
    public string? Accept { get; init; }

    // List & Collection properties
    public string? Items { get; init; }
    public string? Filter { get; init; }
    public string? FilterField { get; init; }
    public string? FilterEquals { get; init; }
    public bool AllowReorder { get; init; }
    public string? InlineEditField { get; init; }
    public CustomWidgetEmptyStateSpec? EmptyState { get; init; }
    public CustomWidgetNodeSpec? ItemTemplate { get; init; }
    public IReadOnlyList<CustomWidgetNodeSpec> HoverActions { get; init; } = [];

    // Segmented / Select / RadioGroup options
    public IReadOnlyList<CustomWidgetOptionSpec> Options { get; init; } = [];

    // Interactive Actions
    public IReadOnlyList<CustomWidgetActionSpec> OnClick { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnSubmit { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnChange { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnDropActions { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnRowClick { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnItemClick { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnReorder { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnInlineCommit { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnPointerDown { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnPointerMove { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> OnPointerUp { get; init; } = [];

    // Children
    public IReadOnlyList<CustomWidgetNodeSpec> Children { get; init; } = [];

    public IEnumerable<CustomWidgetNodeSpec> EnumerateAllNodes()
    {
        var stack = new Stack<CustomWidgetNodeSpec>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            CustomWidgetNodeSpec current = stack.Pop();
            yield return current;

            if (current.Children is { Count: > 0 } children)
            {
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    if (children[i] is { } child)
                    {
                        stack.Push(child);
                    }
                }
            }

            if (current.HoverActions is { Count: > 0 } hoverActions)
            {
                for (int i = hoverActions.Count - 1; i >= 0; i--)
                {
                    if (hoverActions[i] is { } hover)
                    {
                        stack.Push(hover);
                    }
                }
            }

            if (current.Pane is { } pane)
            {
                stack.Push(pane);
            }

            if (current.ActionButton is { } actionButton)
            {
                stack.Push(actionButton);
            }

            if (current.ItemTemplate is { } itemTemplate)
            {
                stack.Push(itemTemplate);
            }
        }
    }

    public IEnumerable<CustomWidgetActionSpec> EnumerateAllActions() =>
        EnumerateDirectAndNestedActions(includeChildNodes: true);

    public IEnumerable<CustomWidgetActionSpec> EnumerateDirectAndNestedActions(bool includeChildNodes = true)
    {
        IEnumerable<CustomWidgetNodeSpec> targetNodes = includeChildNodes ? EnumerateAllNodes() : [this];
        foreach (CustomWidgetNodeSpec node in targetNodes)
        {
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnClick)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnSubmit)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnChange)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnDropActions)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnRowClick)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnItemClick)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnReorder)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnInlineCommit)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnPointerDown)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnPointerMove)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandActionList(node.OnPointerUp)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandMenuItemActions(node.MenuItems)) yield return act;
            foreach (CustomWidgetActionSpec act in ExpandMenuItemActions(node.ContextMenu)) yield return act;

            if (node.Spans is { Count: > 0 } spans)
            {
                for (int i = 0; i < spans.Count; i++)
                {
                    if (spans[i] is { } span)
                    {
                        foreach (CustomWidgetActionSpec act in ExpandActionList(span.OnClick))
                        {
                            yield return act;
                        }
                    }
                }
            }
        }
    }

    internal static IEnumerable<CustomWidgetActionSpec> ExpandActionList(IReadOnlyList<CustomWidgetActionSpec>? actions)
    {
        if (actions is not { Count: > 0 })
        {
            yield break;
        }

        var stack = new Stack<CustomWidgetActionSpec>();
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            if (actions[i] is { } rootAct)
            {
                stack.Push(rootAct);
            }
        }

        while (stack.Count > 0)
        {
            CustomWidgetActionSpec current = stack.Pop();
            yield return current;

            if (current.Then is { Count: > 0 } thenList)
            {
                for (int i = thenList.Count - 1; i >= 0; i--)
                {
                    if (thenList[i] is { } nextAct)
                    {
                        stack.Push(nextAct);
                    }
                }
            }
        }
    }

    private static IEnumerable<CustomWidgetActionSpec> ExpandMenuItemActions(IReadOnlyList<CustomWidgetMenuItemSpec>? menuItems)
    {
        if (menuItems is not { Count: > 0 })
        {
            yield break;
        }

        var stack = new Stack<CustomWidgetMenuItemSpec>();
        for (int i = menuItems.Count - 1; i >= 0; i--)
        {
            if (menuItems[i] is { } item)
            {
                stack.Push(item);
            }
        }

        while (stack.Count > 0)
        {
            CustomWidgetMenuItemSpec current = stack.Pop();
            foreach (CustomWidgetActionSpec act in ExpandActionList(current.OnClick))
            {
                yield return act;
            }

            if (current.Children is { Count: > 0 } subItems)
            {
                for (int i = subItems.Count - 1; i >= 0; i--)
                {
                    if (subItems[i] is { } sub)
                    {
                        stack.Push(sub);
                    }
                }
            }
        }
    }
}

public sealed record CustomWidgetManifest
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyI18nCatalog =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> EmptyLocaleDictionary =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record CachedI18nEntry(
        string Culture,
        IReadOnlyDictionary<string, string> Dictionary);

    private CachedI18nEntry? _cachedI18n;

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
    public string? I18nBaseLocale { get; init; }
    public IReadOnlyList<CustomWidgetSettingDefinition> Settings { get; init; } = [];
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> StateJson { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, CustomWidgetCollectionSchema> Collections { get; init; } =
        new Dictionary<string, CustomWidgetCollectionSchema>(StringComparer.Ordinal);
    public IReadOnlyList<CustomWidgetSourceSpec> Sources { get; init; } = [];
    public CustomWidgetScriptSpec? Script { get; init; }
    public CustomWidgetDropBehavior? OnDrop { get; init; }
    public CustomWidgetCapsuleSpec? Capsule { get; init; }
    public IReadOnlyList<CustomWidgetNodeSpec> Popovers { get; init; } = [];
    public CustomWidgetNodeSpec View { get; init; } = new();
    internal DeskBox.Services.DeviceStateQueryFields? ParsedBodyDeviceFields { get; init; }

    public IEnumerable<CustomWidgetNodeSpec> EnumerateAllNodes()
    {
        if (View is not null)
        {
            foreach (CustomWidgetNodeSpec node in View.EnumerateAllNodes())
            {
                yield return node;
            }
        }

        if (Popovers is { Count: > 0 })
        {
            for (int i = 0; i < Popovers.Count; i++)
            {
                if (Popovers[i] is { } pop)
                {
                    foreach (CustomWidgetNodeSpec node in pop.EnumerateAllNodes())
                    {
                        yield return node;
                    }
                }
            }
        }
    }

    public IEnumerable<CustomWidgetActionSpec> EnumerateAllActions()
    {
        foreach (CustomWidgetNodeSpec node in EnumerateAllNodes())
        {
            foreach (CustomWidgetActionSpec act in node.EnumerateDirectAndNestedActions(includeChildNodes: false))
            {
                yield return act;
            }
        }

        foreach (CustomWidgetActionSpec act in CustomWidgetNodeSpec.ExpandActionList(OnDrop?.Actions))
        {
            yield return act;
        }

        foreach (CustomWidgetActionSpec act in CustomWidgetNodeSpec.ExpandActionList(Capsule?.PrimaryAction?.OnClick))
        {
            yield return act;
        }
    }

    public CustomWidgetManifest WithLocales(CustomWidgetLocaleCatalog? catalog) =>
        catalog is null || catalog.IsEmpty
            ? this
            : WithLocales(catalog.Locales, catalog.Meta?.BaseLocale);

    public CustomWidgetManifest WithLocales(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? localeCatalog,
        string? baseLocale = null)
    {
        if (localeCatalog is not { Count: > 0 })
        {
            return this;
        }

        var merged = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string locale, IReadOnlyDictionary<string, string> entries) in I18n)
        {
            merged[locale] = entries;
        }

        foreach ((string locale, IReadOnlyDictionary<string, string> nextEntries) in localeCatalog)
        {
            if (nextEntries.Count == 0)
            {
                continue;
            }

            if (merged.TryGetValue(locale, out IReadOnlyDictionary<string, string>? existing) && existing.Count > 0)
            {
                var combined = new Dictionary<string, string>(existing, StringComparer.Ordinal);
                foreach ((string k, string v) in nextEntries)
                {
                    combined[k] = v;
                }
                merged[locale] = combined;
            }
            else
            {
                merged[locale] = nextEntries;
            }
        }

        CustomWidgetManifest updated = this with
        {
            I18n = merged,
            I18nBaseLocale = !string.IsNullOrWhiteSpace(baseLocale) ? baseLocale : I18nBaseLocale
        };
        Volatile.Write(ref updated._cachedI18n, null);
        return updated;
    }

    public IReadOnlyDictionary<string, string> ResolveI18nDictionary(string? cultureName = null)
    {
        if (I18n.Count == 0)
        {
            return EmptyLocaleDictionary;
        }

        string effective = string.IsNullOrWhiteSpace(cultureName)
            ? DeskBox.Services.Localized.ActiveCultureName
            : DeskBox.Services.LocalizationService.NormalizeCultureCode(cultureName);

        if (Volatile.Read(ref _cachedI18n) is { } cached &&
            string.Equals(cached.Culture, effective, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Dictionary;
        }

        IReadOnlyDictionary<string, string>? metaBaseDict =
            (!string.IsNullOrWhiteSpace(I18nBaseLocale) ? TryFindLocaleDictionary(I18nBaseLocale) : null)
            ?? TryFindLocaleDictionary("zh-CN");
        IReadOnlyDictionary<string, string>? englishDict =
            TryFindLocaleDictionary("en-US") ?? TryFindLocaleDictionary("en");
        IReadOnlyDictionary<string, string>? baseDict = englishDict
            ?? metaBaseDict
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
        if (baseDict is null && metaBaseDict is null)
        {
            resolved = exactDict ?? prefixDict ?? EmptyLocaleDictionary;
        }
        else if ((metaBaseDict is null || ReferenceEquals(metaBaseDict, baseDict)) &&
                 (prefixDict is null || ReferenceEquals(prefixDict, baseDict)) &&
                 (exactDict is null || ReferenceEquals(exactDict, baseDict)))
        {
            resolved = baseDict!;
        }
        else
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            static void OverlayNonEmpty(Dictionary<string, string> target, IReadOnlyDictionary<string, string>? layer)
            {
                if (layer is null)
                {
                    return;
                }

                foreach ((string k, string v) in layer)
                {
                    if (!string.IsNullOrEmpty(v))
                    {
                        target[k] = v;
                    }
                }
            }

            OverlayNonEmpty(merged, metaBaseDict);
            if (!ReferenceEquals(baseDict, metaBaseDict))
            {
                OverlayNonEmpty(merged, baseDict);
            }
            if (!ReferenceEquals(prefixDict, baseDict))
            {
                OverlayNonEmpty(merged, prefixDict);
            }
            if (!ReferenceEquals(exactDict, prefixDict) &&
                !(prefixDict is null && ReferenceEquals(exactDict, baseDict)))
            {
                OverlayNonEmpty(merged, exactDict);
            }

            resolved = merged;
        }

        Volatile.Write(ref _cachedI18n, new CachedI18nEntry(effective, resolved));
        return resolved;
    }

    public string ResolveLocalizedText(string? raw, string? cultureName = null)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (!raw.Contains('$', StringComparison.Ordinal))
        {
            return raw;
        }

        var ctx = new DeskBox.Services.CustomWidgetEvaluationContext
        {
            I18n = I18n.Count > 0
                ? ResolveI18nDictionary(cultureName)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            CultureName = cultureName
        };
        return DeskBox.Services.CustomWidgetExpressionEvaluator.Interpolate(raw, ctx);
    }

    public string ResolveLocalizedName(string? cultureName = null)
    {
        IReadOnlyDictionary<string, string> dict = ResolveI18nDictionary(cultureName);
        if (dict.TryGetValue("meta.name", out string? mn) && !string.IsNullOrWhiteSpace(mn))
        {
            return mn;
        }

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
        if (dict.TryGetValue("meta.description", out string? md) && !string.IsNullOrWhiteSpace(md))
        {
            return md;
        }

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
        if (Settings.Count == 0)
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

            string? placeholder = string.IsNullOrEmpty(def.Placeholder)
                ? def.Placeholder
                : (dict.TryGetValue($"setting.{def.Key}.placeholder", out string? convPh) && !string.IsNullOrWhiteSpace(convPh)
                    ? convPh
                    : ResolveLocalizedText(def.Placeholder, cultureName));

            string description = dict.TryGetValue($"setting.{def.Key}.description", out string? convDesc) && !string.IsNullOrWhiteSpace(convDesc)
                ? convDesc
                : (string.IsNullOrEmpty(def.Description)
                    ? string.Empty
                    : ResolveLocalizedText(def.Description, cultureName));

            list.Add(def with { Label = label, Options = opts, Placeholder = placeholder, Description = description });
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
