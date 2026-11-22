using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Models;

public static partial class CustomWidgetManifestParser
{
    private const int MaxNodeTreeDepth = 64;
    private const int MaxActionChainDepth = 64;

    public static bool IsReadOnlyDisplayNodeType(string? nodeType) =>
        string.Equals(nodeType, "Text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "Badge", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "Markdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "CodeBlock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "RichText", StringComparison.OrdinalIgnoreCase);

    private static CustomWidgetNodeSpec ParseNode(
        JsonElement el,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        FlatGraphParseContext flatGraph,
        int depth = 0,
        IReadOnlyDictionary<string, IReadOnlyList<CustomWidgetOptionSpec>>? settingOptionsByKey = null,
        string? implicitElementId = null)
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
            "Overlay" or "OverlayStack" or "LayerStack" => "ZStack",
            "UniformGrid" or "TileGrid" => "ItemsGrid",
            "Carousel" => "FlipView",
            "PieChart" or "piechart" => "DonutChart",
            "LineChart" or "BarChart" or "AreaChart" => "Chart",
            "DataTable" or "DataGrid" => "Table",
            "RichTextBlock" => "RichText",
            "MarkdownBlock" => "Markdown",
            "LogView" or "CodeView" => "CodeBlock",
            "Gauge" => "RadialGauge",
            "VideoPlayer" or "AudioPlayer" => "MediaPlayer",
            "AutoSuggestBox" or "SearchInput" => "AutoSuggestInput",
            "Flyout" or "Modal" or "Dialog" => "Popover",
            "Svg" or "SvgCanvas" or "DrawingCanvas" => "Canvas",
            "RadioButtons" => "RadioGroup",
            "RatingControl" => "Rating",
            "ColorSwatch" => "ColorPicker",
            _ => rawNodeType
        };
        if (!SupportedNodeTypes.Contains(type))
        {
            diagnostics.Add(new(
                path,
                $"Unknown component type '{type}'. Supported types: {string.Join(", ", SupportedNodeTypes)}."));
        }

        bool isCodeOrMarkdownNode = string.Equals(type, "CodeBlock", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(type, "Markdown", StringComparison.OrdinalIgnoreCase);
        foreach (JsonProperty nodeProp in el.EnumerateObject())
        {
            if (nodeProp.Name is "children" or "child" or "itemTemplate" or "actionButton" or "pane" or "hoverActions"
                or "onClick" or "onSubmit" or "onChange" or "onDrop" or "onRowClick" or "onItemClick" or "onReorder" or "onInlineCommit"
                or "view")
            {
                continue;
            }

            bool skipUnclosed = isCodeOrMarkdownNode &&
                                nodeProp.Name is "text" or "code" or "markdown" or "content";
            ScanElementForSecretAndUnclosedInterpolation(
                nodeProp.Value,
                $"{path}.{nodeProp.Name}",
                diagnostics,
                forbidSecrets: true,
                checkUnclosed: !skipUnclosed,
                skipActionPropertiesInNestedObjects: nodeProp.Name is "menuItems" or "contextMenu" or "content");
        }

        string? impliedChartKind = rawNodeType.Trim() switch
        {
            "BarChart" => "bar",
            "AreaChart" => "area",
            "LineChart" => "line",
            _ => null
        };

        CustomWidgetEmptyStateSpec? emptyState = null;
        if (el.TryGetProperty("emptyState", out JsonElement emptyEl) &&
            emptyEl.ValueKind == JsonValueKind.Object)
        {
            emptyState = new CustomWidgetEmptyStateSpec(
                Icon: NormalizeGlyphWithDiagnostics(TryGetString(emptyEl, "icon"), $"{path}.emptyState.icon", diagnostics, "\uE70F"),
                Title: GetString(emptyEl, "title", string.Empty),
                Subtitle: GetString(emptyEl, "subtitle", string.Empty));
        }

        CustomWidgetNodeSpec? itemTemplate = null;
        if (el.TryGetProperty("itemTemplate", out JsonElement itemTplEl))
        {
            if (itemTplEl.ValueKind == JsonValueKind.Object)
            {
                itemTemplate = ParseNode(itemTplEl, $"{path}.itemTemplate", diagnostics, flatGraph, depth + 1, settingOptionsByKey);
            }
            else if (itemTplEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(itemTplEl.GetString()))
            {
                itemTemplate = ResolveFlatElement(itemTplEl.GetString()!, $"{path}.itemTemplate", flatGraph, diagnostics, depth + 1, settingOptionsByKey);
            }
        }

        CustomWidgetNodeSpec? actionButton = null;
        if (el.TryGetProperty("actionButton", out JsonElement actBtnEl))
        {
            if (actBtnEl.ValueKind == JsonValueKind.Object)
            {
                actionButton = ParseNode(actBtnEl, $"{path}.actionButton", diagnostics, flatGraph, depth + 1, settingOptionsByKey);
            }
            else if (actBtnEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(actBtnEl.GetString()))
            {
                actionButton = ResolveFlatElement(actBtnEl.GetString()!, $"{path}.actionButton", flatGraph, diagnostics, depth + 1, settingOptionsByKey);
            }
        }

        CustomWidgetNodeSpec? paneNode = null;
        if (el.TryGetProperty("pane", out JsonElement paneEl))
        {
            if (paneEl.ValueKind == JsonValueKind.Object)
            {
                paneNode = ParseNode(paneEl, $"{path}.pane", diagnostics, flatGraph, depth + 1, settingOptionsByKey);
            }
            else if (paneEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(paneEl.GetString()))
            {
                paneNode = ResolveFlatElement(paneEl.GetString()!, $"{path}.pane", flatGraph, diagnostics, depth + 1, settingOptionsByKey);
            }
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
                    hoverActions.Add(ParseNode(child, $"{path}.hoverActions[{idx}]", diagnostics, flatGraph, depth + 1, settingOptionsByKey));
                }
                else if (child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString()))
                {
                    hoverActions.Add(ResolveFlatElement(child.GetString()!, $"{path}.hoverActions[{idx}]", flatGraph, diagnostics, depth + 1, settingOptionsByKey));
                }
                idx++;
            }
        }

        var children = new List<CustomWidgetNodeSpec>();
        bool hasExplicitChildrenProp = el.TryGetProperty("children", out JsonElement childrenEl) &&
                                       childrenEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (hasExplicitChildrenProp)
        {
            if (childrenEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (JsonElement child in childrenEl.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        children.Add(ParseNode(child, $"{path}.children[{idx}]", diagnostics, flatGraph, depth + 1, settingOptionsByKey));
                    }
                    else if (child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString()))
                    {
                        children.Add(ResolveFlatElement(child.GetString()!, $"{path}.children[{idx}]", flatGraph, diagnostics, depth + 1, settingOptionsByKey));
                    }
                    else if (child.ValueKind != JsonValueKind.Null)
                    {
                        diagnostics.Add(new(
                            $"{path}.children[{idx}]",
                            $"Child entry at '{path}.children[{idx}]' must be an element ID string or node object, not {child.ValueKind}.",
                            IsError: true,
                            Code: "InvalidChildrenBinding"));
                    }
                    idx++;
                }
            }
            else
            {
                diagnostics.Add(new(
                    $"{path}.children",
                    $"'{type}.children' must be a static JSON array of element ID strings, not {childrenEl.ValueKind}. DeskBox compiles native WinUI visual trees and does NOT support dynamic VDOM arrays in 'children'. For dynamic 2D grids/timetables, use '\"type\": \"ItemsGrid\"' with integer '\"columns\": 5' (or 6), '\"items\": \"${{$computed.cells}}\"', compact cell '\"padding\": \"4,3,4,3\"', and a declarative '\"itemTemplate\"' using 'when' branches for filled cards vs. empty '+' buttons (or use '\"type\": \"List\"').",
                    IsError: true,
                    Code: "InvalidChildrenBinding"));
            }
        }
        else if (el.TryGetProperty("child", out JsonElement singleChildEl) &&
                 singleChildEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
        {
            if (singleChildEl.ValueKind == JsonValueKind.Object)
            {
                children.Add(ParseNode(singleChildEl, $"{path}.children[0]", diagnostics, flatGraph, depth + 1, settingOptionsByKey));
            }
            else if (singleChildEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(singleChildEl.GetString()))
            {
                children.Add(ResolveFlatElement(singleChildEl.GetString()!, $"{path}.child", flatGraph, diagnostics, depth + 1, settingOptionsByKey));
            }
            else if (singleChildEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (JsonElement child in singleChildEl.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        children.Add(ParseNode(child, $"{path}.children[{idx}]", diagnostics, flatGraph, depth + 1, settingOptionsByKey));
                    }
                    else if (child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString()))
                    {
                        children.Add(ResolveFlatElement(child.GetString()!, $"{path}.children[{idx}]", flatGraph, diagnostics, depth + 1, settingOptionsByKey));
                    }
                    idx++;
                }
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

        CustomWidgetLinearGradientSpec? linearGradient = ParseLinearGradient(el);
        CustomWidgetRadialGradientSpec? radialGradient = ParseRadialGradient(el);
        IReadOnlyList<CustomWidgetTextSpanSpec> textSpans = ParseTextSpans(el, $"{path}.spans", diagnostics);
        IReadOnlyList<CustomWidgetMenuItemSpec> menuItems = ParseMenuItems(el, "menuItems", $"{path}.menuItems", diagnostics);
        if (menuItems.Count == 0 && (string.Equals(type, "SplitButton", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(type, "DropDownButton", StringComparison.OrdinalIgnoreCase)))
        {
            menuItems = ParseMenuItems(el, "items", $"{path}.items", diagnostics);
        }
        IReadOnlyList<CustomWidgetMenuItemSpec> contextMenu = ParseMenuItems(el, "contextMenu", $"{path}.contextMenu", diagnostics);
        if (contextMenu.Count == 0 && menuItems.Count > 0 &&
            !string.Equals(type, "SplitButton", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "DropDownButton", StringComparison.OrdinalIgnoreCase))
        {
            contextMenu = menuItems;
        }
        string? rawBackground = TryGetString(el, "background") ?? TryGetString(el, "backgroundColor") ?? TryGetString(el, "bg");
        string? rawElementsSource = TryGetScalarAsString(el, "elementsSource") ?? TryGetScalarAsString(el, "shapesSource");
        IReadOnlyList<CustomWidgetCanvasElementSpec> canvasElements = [];
        if (el.TryGetProperty("elements", out JsonElement elementsProp) ||
            el.TryGetProperty("shapes", out elementsProp))
        {
            if (elementsProp.ValueKind == JsonValueKind.String)
            {
                rawElementsSource ??= elementsProp.GetString();
            }
            else if (elementsProp.ValueKind == JsonValueKind.Array)
            {
                canvasElements = ParseCanvasElements(elementsProp);
            }
        }
        if (string.Equals(type, "Canvas", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rawElementsSource))
        {
            rawElementsSource = TryGetScalarAsString(el, "items") ?? TryGetScalarAsString(el, "data");
        }

        string? rawSuggestionsSource = TryGetScalarAsString(el, "suggestionsSource");
        IReadOnlyList<string> staticSuggestions = [];
        if (el.TryGetProperty("suggestions", out JsonElement sugProp))
        {
            if (sugProp.ValueKind == JsonValueKind.String)
            {
                rawSuggestionsSource ??= sugProp.GetString();
            }
            else if (sugProp.ValueKind == JsonValueKind.Array)
            {
                staticSuggestions = ParseScalarArray(el, "suggestions");
            }
        }
        if (string.Equals(type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rawSuggestionsSource))
        {
            rawSuggestionsSource = TryGetScalarAsString(el, "items") ?? TryGetScalarAsString(el, "data");
        }

        string? parsedColumns = string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase)
            ? CustomWidgetGridTrackNormalizer.TryExtractGridTrackProperty(el, "columns", "cols", "columnCount", "columnsCount", "gridTemplateColumns")
            : string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase)
                ? (el.TryGetProperty("columns", out JsonElement igColsProp) && igColsProp.ValueKind != JsonValueKind.Array
                    ? TryGetScalarAsString(el, "columns")
                    : null)
                  ?? TryGetScalarAsString(el, "columnsCount")
                : TryGetString(el, "columns");
        string? parsedRows = string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase)
            ? CustomWidgetGridTrackNormalizer.TryExtractGridTrackProperty(el, "rows", "rowsCount", "gridTemplateRows")
            : TryGetString(el, "rows");

        if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(parsedColumns) &&
                !parsedColumns.Contains("${", StringComparison.Ordinal))
            {
                if (CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(parsedColumns, out string normalizedCols))
                {
                    parsedColumns = normalizedCols;
                }

                if (!IsValidStaticGridTrackList(parsedColumns))
                {
                    diagnostics.Add(new(
                        $"{path}.columns",
                        $"Grid 'columns' value '{parsedColumns}' is invalid. Expected comma-separated track tokens like 'Auto', '*', '2*', or numeric pixel widths (or a '${{...}}' expression)."));
                }
            }

            if (!string.IsNullOrWhiteSpace(parsedRows) &&
                !parsedRows.Contains("${", StringComparison.Ordinal))
            {
                if (CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(parsedRows, out string normalizedRows))
                {
                    parsedRows = normalizedRows;
                }

                if (!IsValidStaticGridTrackList(parsedRows))
                {
                    diagnostics.Add(new(
                        $"{path}.rows",
                        $"Grid 'rows' value '{parsedRows}' is invalid. Expected comma-separated track tokens like 'Auto', '*', '2*', or numeric pixel heights (or a '${{...}}' expression)."));
                }
            }
        }
        else if (string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(parsedColumns) &&
                 !parsedColumns.Contains("${", StringComparison.Ordinal) &&
                 !TryParseItemsGridColumnCount(parsedColumns, out _))
        {
            string colField = el.TryGetProperty("columns", out _) ? "columns" : "columnsCount";
            diagnostics.Add(new(
                $"{path}.{colField}",
                $"ItemsGrid '{colField}' must be an integer between 1 and 16 (or a '${{...}}' expression evaluating to an integer), not Grid star syntax '{parsedColumns}'."));
        }

        string? parsedBind = TryGetString(el, "bind") ?? TryGetString(el, "stateKey");
        IReadOnlyList<CustomWidgetOptionSpec> options = ParseOptions(el, "options");
        if (options.Count == 0 &&
            settingOptionsByKey is not null &&
            !string.IsNullOrWhiteSpace(parsedBind) &&
            (string.Equals(type, "Select", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "Segmented", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "RadioGroup", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "SelectorBar", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase)))
        {
            options = ResolveBoundSettingOptions(parsedBind, settingOptionsByKey);
        }

        string? rawDir = TryGetString(el, "dir")?.Trim();
        string? normalizedDir = string.IsNullOrEmpty(rawDir) ? null : rawDir.ToLowerInvariant();
        if (normalizedDir is not null &&
            !normalizedDir.Contains("${", StringComparison.Ordinal) &&
            normalizedDir is not ("ltr" or "rtl" or "auto"))
        {
            diagnostics.Add(new(
                $"{path}.dir",
                $"Invalid 'dir' value '{rawDir}'. Expected 'ltr', 'rtl', or 'auto'.",
                IsError: false));
            normalizedDir = null;
        }

        string? boundCollection = TryGetString(el, "collection") ?? TryGetString(el, "eventsCollection");
        string? boundItems = TryGetString(el, "items") ?? TryGetString(el, "itemsSource");
        if (children.Count > 0 &&
            (!string.IsNullOrWhiteSpace(boundCollection) || !string.IsNullOrWhiteSpace(boundItems)) &&
            (string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "List", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(type, "BarList", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new(
                path,
                $"Node type \"{type}\" binds dynamic data ({boundCollection ?? boundItems}) and also defines static \"children\" ({children.Count}); remove \"children\" or use \"itemTemplate\" / a static container instead.",
                IsError: true,
                Code: CustomWidgetStrictAuthoringRules.ConflictingCollectionAndChildren));
        }

        double? rawFlex = TryGetOptionalDouble(el, "flex");
        double? normalizedFlex = rawFlex is double fv && double.IsFinite(fv) && fv > 0 ? fv : null;

        return new CustomWidgetNodeSpec
        {
            Type = type,
            NodePath = path,
            Id = TryGetString(el, "id") ?? implicitElementId,
            When = TryGetString(el, "$when") ?? TryGetString(el, "when") ?? TryGetScalarAsString(el, "visible"),
            Orientation = normalizedOrientation,
            Flex = normalizedFlex,
            Spacing = GetDouble(el, "spacing", 8),
            HasExplicitSpacing = el.TryGetProperty("spacing", out _),
            ColumnSpacing = TryGetOptionalDouble(el, "columnSpacing"),
            RowSpacing = TryGetOptionalDouble(el, "rowSpacing"),
            Dir = normalizedDir,
            Padding = TryGetScalarAsString(el, "padding"),
            Margin = TryGetScalarAsString(el, "margin"),
            Columns = parsedColumns,
            Rows = parsedRows,
            Column = el.TryGetProperty("column", out _) ? GetInt(el, "column", 0) : GetInt(el, "col", 0),
            Row = GetInt(el, "row", 0),
            HasExplicitColumn = el.TryGetProperty("column", out _) || el.TryGetProperty("col", out _),
            HasExplicitRow = el.TryGetProperty("row", out _),
            ColumnSpan = Math.Max(1, el.TryGetProperty("columnSpan", out _) ? GetInt(el, "columnSpan", 1) : GetInt(el, "colSpan", 1)),
            RowSpan = Math.Max(1, el.TryGetProperty("rowSpan", out _) ? GetInt(el, "rowSpan", 1) : GetInt(el, "rowspan", 1)),
            Width = TryGetDouble(el, "width"),
            Height = TryGetDouble(el, "height"),
            Size = TryGetDouble(el, "size"),
            MinWidth = TryGetDouble(el, "minWidth"),
            MaxWidth = TryGetDouble(el, "maxWidth"),
            MinHeight = TryGetDouble(el, "minHeight"),
            MaxHeight = TryGetDouble(el, "maxHeight"),
            HorizontalAlignment = TryGetString(el, "horizontalAlignment") ?? TryGetString(el, "align"),
            VerticalAlignment = TryGetString(el, "verticalAlignment") ?? TryGetString(el, "valign"),
            CornerRadius = TryGetDouble(el, "cornerRadius") ?? TryGetDouble(el, "radius"),
            Tone = GetString(el, "tone", "default"),
            Background = rawBackground,
            LinearGradient = linearGradient,
            RadialGradient = radialGradient,
            Backdrop = TryGetString(el, "backdrop") ?? TryGetString(el, "material"),
            Shadow = TryGetString(el, "shadow") ?? TryGetString(el, "elevation"),
            AspectRatio = TryGetDouble(el, "aspectRatio"),
            TextColor = TryGetString(el, "textColor") ?? TryGetString(el, "foreground") ?? TryGetString(el, "fg"),
            BorderColor = TryGetString(el, "borderColor") ?? TryGetString(el, "stroke"),
            BorderWidth = TryGetScalarAsString(el, "borderWidth") ?? TryGetScalarAsString(el, "borderThickness"),
            FontSize = TryGetDouble(el, "fontSize"),
            FontWeight = TryGetScalarAsString(el, "fontWeight") ?? TryGetScalarAsString(el, "weight"),
            FontFamily = TryGetString(el, "fontFamily") ?? TryGetString(el, "font"),
            Opacity = TryGetDouble(el, "opacity"),
            OpacityExpression = TryGetScalarAsString(el, "opacity"),
            Text = TryGetScalarAsString(el, "text") ??
                   TryGetScalarAsString(el, "code") ??
                   TryGetScalarAsString(el, "markdown") ??
                   (el.TryGetProperty("content", out JsonElement contentProp) &&
                    contentProp.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                       ? null
                       : TryGetScalarAsString(el, "content")) ??
                   (!string.IsNullOrWhiteSpace(parsedBind) &&
                    textSpans.Count == 0 &&
                    IsReadOnlyDisplayNodeType(type)
                       ? (parsedBind.Contains("${", StringComparison.Ordinal) ? parsedBind : $"${{{parsedBind.Trim()}}}")
                       : null),
            Title = TryGetScalarAsString(el, "title") ??
                    TryGetScalarAsString(el, "label") ??
                    TryGetScalarAsString(el, "header") ??
                    (string.Equals(type, "KeyValueRow", StringComparison.OrdinalIgnoreCase)
                        ? TryGetScalarAsString(el, "key")
                        : null),
            Subtitle = TryGetScalarAsString(el, "subtitle") ?? TryGetScalarAsString(el, "message"),
            Placeholder = TryGetString(el, "placeholder"),
            Value = TryGetScalarAsString(el, "value") ?? TryGetScalarAsString(el, "checked") ?? TryGetScalarAsString(el, "values"),
            Bind = parsedBind,
            Icon = iconRaw is null ? null : NormalizeGlyphWithDiagnostics(iconRaw, $"{path}.icon", diagnostics, "\uE71D"),
            Tooltip = TryGetString(el, "tooltip"),
            Style = GetString(el, "style", "body"),
            Color = GetString(el, "color", "default"),
            Variant = GetString(el, "variant", "standard"),
            MaxLines = GetInt(el, "maxLines", 0),
            Strikethrough = GetBool(el, "strikethrough", false),
            AnimateChange = GetBool(el, "animateChange", false),
            TextAlignment = TryGetString(el, "textAlignment") ?? TryGetString(el, "textAlign"),
            Spans = textSpans,
            Language = TryGetString(el, "language") ?? TryGetString(el, "lang"),
            WrapText = GetBool(el, "wrapText", GetBool(el, "wrap", true)),
            AutoScrollTail = GetBool(el, "autoScrollTail", GetBool(el, "followTail", false)),
            ShowCopyButton = GetBool(el, "showCopyButton", GetBool(el, "copyable", true)),
            DefaultExpanded = GetBool(el, "defaultExpanded", GetBool(el, "isExpanded", true)),
            PopoverId = TryGetString(el, "popoverId") ??
                        ((string.Equals(type, "Popover", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(type, "TeachingTip", StringComparison.OrdinalIgnoreCase))
                            ? TryGetString(el, "id")
                            : null),
            Popover = TryGetString(el, "popover") ?? TryGetString(el, "targetPopover"),
            Placement = TryGetString(el, "placement"),
            Severity = GetString(el, "severity", "informational"),
            IsClosable = GetBool(el, "isClosable", true),
            Target = TryGetString(el, "target") ?? TryGetString(el, "targetId"),
            CloseButtonText = TryGetScalarAsString(el, "closeButtonText"),
            ActionButton = actionButton,
            Pane = paneNode,
            DisplayMode = GetString(el, "displayMode", "compactInline"),
            PaneWidth = Math.Clamp(GetDouble(el, "paneWidth", GetDouble(el, "openPaneLength", 180)), 48, 600),
            CompactPaneWidth = Math.Clamp(GetDouble(el, "compactPaneWidth", GetDouble(el, "compactPaneLength", 40)), 0, 200),
            PanePlacement = GetString(el, "panePlacement", "left"),
            IsOpen = TryGetScalarAsString(el, "isOpen") ?? TryGetScalarAsString(el, "isPaneOpen"),
            MinItemWidth = TryGetDouble(el, "minItemWidth") ?? TryGetDouble(el, "minColumnWidth"),
            MaxItems = Math.Max(0, GetInt(el, "maxItems", 0)),
            ShowPager = GetBool(el, "showPager", true),
            MenuItems = menuItems,
            ContextMenu = contextMenu,
            Progress = TryGetScalarAsString(el, "progress"),
            Indeterminate = TryGetScalarAsString(el, "indeterminate") ?? TryGetScalarAsString(el, "isIndeterminate"),
            Min = GetDouble(el, "min", 0),
            Max = GetDouble(el, "max", string.Equals(type, "Rating", StringComparison.OrdinalIgnoreCase) ? 5 : 100),
            Step = Math.Max(0.001, GetDouble(el, "step", 1)),
            Unit = TryGetScalarAsString(el, "unit"),
            StartAngle = GetDouble(el, "startAngle", -135),
            SweepAngle = Math.Clamp(GetDouble(el, "sweepAngle", 270), 10, 360),
            GaugeThickness = Math.Clamp(GetDouble(el, "thickness", GetDouble(el, "strokeWidth", 10)), 2, 48),
            ShowTicks = GetBool(el, "showTicks", false),
            TickStep = Math.Max(0, GetDouble(el, "tickStep", 0)),
            IsInteractive = GetBool(el, "isInteractive", false),
            WarningThreshold = TryGetOptionalDouble(el, "warningThreshold"),
            DangerThreshold = TryGetOptionalDouble(el, "dangerThreshold"),
            WarningThresholdExpression = TryGetScalarAsString(el, "warningThreshold"),
            DangerThresholdExpression = TryGetScalarAsString(el, "dangerThreshold"),
            Sparkline = TryGetString(el, "sparkline") ?? TryGetScalarAsString(el, "values"),
            ShowArea = GetBool(el, "showArea", true),
            ShowLatest = GetBool(el, "showLatest", false),
            ChartKind = TryGetString(el, "chartKind") ?? TryGetString(el, "kind") ?? impliedChartKind ?? "line",
            ColorField = TryGetString(el, "colorField"),
            XField = TryGetString(el, "xField") ?? TryGetString(el, "categoryField") ?? TryGetString(el, "nameField"),
            CenterTitle = TryGetScalarAsString(el, "centerTitle"),
            CenterValue = TryGetScalarAsString(el, "centerValue"),
            ShowLegend = GetBool(el, "showLegend", true),
            ShowGrid = GetBool(el, "showGrid", true),
            Series = ParseChartSeries(el),
            ViewBox = TryGetString(el, "viewBox"),
            ElementsSource = rawElementsSource,
            Elements = canvasElements,
            Collection = boundCollection,
            TitleField = TryGetString(el, "titleField") ?? TryGetString(el, "labelField"),
            SubtitleField = TryGetString(el, "subtitleField"),
            ValueField = TryGetString(el, "valueField"),
            DateField = TryGetString(el, "dateField"),
            Days = Math.Clamp(GetInt(el, "days", 28), 7, 180),
            CompactPair = GetBool(el, "compactPair", false),
            PairGroup = TryGetString(el, "pairGroup"),
            PairWidth = TryGetString(el, "pairWidth"),
            PinTrailingGroup = TryGetString(el, "pinTrailingGroup"),
            TableColumns = ParseTableColumns(el),
            ShowHeader = GetBool(el, "showHeader", true),
            Striped = GetBool(el, "striped", false),
            Sortable = GetBool(el, "sortable", true),
            SuggestionsSource = rawSuggestionsSource,
            Suggestions = staticSuggestions,
            Presets = ParseScalarArray(el, "presets"),
            AllowHalfStar = GetBool(el, "allowHalfStar", false),
            Src = TryGetScalarAsString(el, "src") ?? TryGetScalarAsString(el, "url") ?? TryGetScalarAsString(el, "imageUrl"),
            Poster = TryGetScalarAsString(el, "poster") ?? TryGetScalarAsString(el, "thumbnail"),
            Stretch = TryGetString(el, "stretch") ?? TryGetString(el, "fit"),
            ShowTransportControls = GetBool(el, "showTransportControls", GetBool(el, "showTransport", GetBool(el, "controls", true))),
            ShowTransportExpression = TryGetScalarAsString(el, "showTransportControls") ??
                                      TryGetScalarAsString(el, "showTransport") ??
                                      TryGetScalarAsString(el, "controls"),
            AutoPlay = GetBool(el, "autoPlay", false),
            AutoPlayExpression = TryGetScalarAsString(el, "autoPlay"),
            Muted = GetBool(el, "muted", false),
            MutedExpression = TryGetScalarAsString(el, "muted"),
            Loop = GetBool(el, "loop", false),
            LoopExpression = TryGetScalarAsString(el, "loop"),
            Volume = TryGetScalarAsString(el, "volume"),
            FileName = TryGetScalarAsString(el, "fileName"),
            FilePath = TryGetScalarAsString(el, "filePath"),
            StorageMode = TryGetScalarAsString(el, "storageMode") ??
                TryGetScalarAsString(el, "dropMode") ??
                (string.Equals(type, "DropZone", StringComparison.OrdinalIgnoreCase)
                    ? TryGetScalarAsString(el, "mode")
                    : null),
            Accept = TryGetScalarAsString(el, "accept"),
            Items = boundItems,
            Filter = TryGetString(el, "filter"),
            FilterField = TryGetString(el, "filterField"),
            FilterEquals = TryGetString(el, "filterEquals"),
            AllowReorder = GetBool(el, "allowReorder", GetBool(el, "reorderable", false)),
            InlineEditField = TryGetString(el, "inlineEditField") ?? TryGetString(el, "editableField"),
            EmptyState = emptyState,
            ItemTemplate = itemTemplate,
            HoverActions = hoverActions,
            Options = options,
            OnClick = el.TryGetProperty("onClick", out _)
                ? ParseNodeActions(el, "onClick", path, diagnostics)
                : ParseNodeActions(el, "actions", path, diagnostics),
            OnSubmit = ParseNodeActions(el, "onSubmit", path, diagnostics),
            OnChange = el.TryGetProperty("onChange", out _)
                ? ParseNodeActions(el, "onChange", path, diagnostics)
                : el.TryGetProperty("onTextChange", out _)
                    ? ParseNodeActions(el, "onTextChange", path, diagnostics)
                    : ParseNodeActions(el, "onToggle", path, diagnostics),
            OnDropActions = ParseNodeActions(el, "onDrop", path, diagnostics),
            OnRowClick = ParseNodeActions(el, "onRowClick", path, diagnostics),
            OnItemClick = ParseNodeActions(el, "onItemClick", path, diagnostics),
            OnReorder = ParseNodeActions(el, "onReorder", path, diagnostics),
            OnInlineCommit = ParseNodeActions(el, "onInlineCommit", path, diagnostics),
            OnPointerDown = ParseNodeActions(el, "onPointerDown", path, diagnostics),
            OnPointerMove = ParseNodeActions(el, "onPointerMove", path, diagnostics),
            OnPointerUp = ParseNodeActions(el, "onPointerUp", path, diagnostics),
            Children = children
        };
    }

    public static IReadOnlyList<CustomWidgetActionSpec> ParseDispatchedActions(string? rawJson) =>
        ParseDispatchedActions(rawJson, diagnostics: null);

    public static IReadOnlyList<CustomWidgetActionSpec> ParseDispatchedActions(
        string? rawJson,
        List<CustomWidgetValidationDiagnostic>? diagnostics)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            List<CustomWidgetValidationDiagnostic> targetDiags = diagnostics ?? [];
            return ParseActionList(doc.RootElement, "$.dispatch", targetDiags);
        }
        catch
        {
            return [];
        }
    }

    private static List<CustomWidgetGradientStopSpec> ParseGradientStops(JsonElement gradEl)
    {
        var stops = new List<CustomWidgetGradientStopSpec>();
        if (gradEl.TryGetProperty("stops", out JsonElement stopsEl) && stopsEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            int count = stopsEl.GetArrayLength();
            foreach (JsonElement s in stopsEl.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.Object)
                {
                    double fallbackOffset = count > 1 ? (double)idx / (count - 1) : 0;
                    double offset = Math.Clamp(GetDouble(s, "offset", fallbackOffset), 0, 1);
                    string color = GetString(s, "color", "#00000000");
                    stops.Add(new CustomWidgetGradientStopSpec(offset, color));
                }
                else if (s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                {
                    double offset = count > 1 ? (double)idx / (count - 1) : 0;
                    stops.Add(new CustomWidgetGradientStopSpec(offset, s.GetString()!));
                }
                idx++;
            }
        }
        else if (gradEl.TryGetProperty("colors", out JsonElement colorsEl) && colorsEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            int count = colorsEl.GetArrayLength();
            foreach (JsonElement c in colorsEl.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                {
                    double offset = count > 1 ? (double)idx / (count - 1) : 0;
                    stops.Add(new CustomWidgetGradientStopSpec(offset, c.GetString()!));
                }
                idx++;
            }
        }
        return stops;
    }

    private static CustomWidgetLinearGradientSpec? ParseLinearGradient(JsonElement el)
    {
        JsonElement gradEl = default;
        if (el.TryGetProperty("linearGradient", out JsonElement lgProp) && lgProp.ValueKind == JsonValueKind.Object)
        {
            gradEl = lgProp;
        }
        else if (el.TryGetProperty("gradient", out JsonElement gProp) && gProp.ValueKind == JsonValueKind.Object &&
                 !string.Equals(TryGetString(gProp, "type"), "radial", StringComparison.OrdinalIgnoreCase))
        {
            gradEl = gProp;
        }
        else if (el.TryGetProperty("background", out JsonElement bgProp) && bgProp.ValueKind == JsonValueKind.Object &&
                 !string.Equals(TryGetString(bgProp, "type"), "radial", StringComparison.OrdinalIgnoreCase))
        {
            gradEl = bgProp;
        }
        else
        {
            return null;
        }

        double angle = GetDouble(gradEl, "angle", 135);
        List<CustomWidgetGradientStopSpec> stops = ParseGradientStops(gradEl);
        return stops.Count >= 2 ? new CustomWidgetLinearGradientSpec(angle, stops) : null;
    }

    private static CustomWidgetRadialGradientSpec? ParseRadialGradient(JsonElement el)
    {
        JsonElement gradEl = default;
        if (el.TryGetProperty("radialGradient", out JsonElement rgProp) && rgProp.ValueKind == JsonValueKind.Object)
        {
            gradEl = rgProp;
        }
        else if (el.TryGetProperty("gradient", out JsonElement gProp) && gProp.ValueKind == JsonValueKind.Object &&
                 string.Equals(TryGetString(gProp, "type"), "radial", StringComparison.OrdinalIgnoreCase))
        {
            gradEl = gProp;
        }
        else if (el.TryGetProperty("background", out JsonElement bgProp) && bgProp.ValueKind == JsonValueKind.Object &&
                 string.Equals(TryGetString(bgProp, "type"), "radial", StringComparison.OrdinalIgnoreCase))
        {
            gradEl = bgProp;
        }
        else
        {
            return null;
        }

        double centerX = Math.Clamp(GetDouble(gradEl, "centerX", GetDouble(gradEl, "cx", 0.5)), 0, 1);
        double centerY = Math.Clamp(GetDouble(gradEl, "centerY", GetDouble(gradEl, "cy", 0.5)), 0, 1);
        double radiusX = Math.Clamp(GetDouble(gradEl, "radiusX", GetDouble(gradEl, "rx", GetDouble(gradEl, "radius", 0.5))), 0.01, 4);
        double radiusY = Math.Clamp(GetDouble(gradEl, "radiusY", GetDouble(gradEl, "ry", radiusX)), 0.01, 4);
        List<CustomWidgetGradientStopSpec> stops = ParseGradientStops(gradEl);
        return stops.Count >= 2 ? new CustomWidgetRadialGradientSpec(centerX, centerY, radiusX, radiusY, stops) : null;
    }

    private static IReadOnlyList<CustomWidgetTextSpanSpec> ParseTextSpans(
        JsonElement el,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (!el.TryGetProperty("spans", out JsonElement spansEl) &&
            !el.TryGetProperty("runs", out spansEl))
        {
            return [];
        }

        if (spansEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CustomWidgetTextSpanSpec>();
        int idx = 0;
        foreach (JsonElement item in spansEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(new CustomWidgetTextSpanSpec(item.GetString() ?? string.Empty));
                idx++;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                idx++;
                continue;
            }

            string text = GetScalarAsString(item, "text", GetScalarAsString(item, "value", string.Empty));
            string? color = TryGetString(item, "color") ?? TryGetString(item, "textColor") ?? TryGetString(item, "fg");
            double? fontSize = TryGetDouble(item, "fontSize") ?? TryGetDouble(item, "size");
            string? fontWeight = TryGetScalarAsString(item, "fontWeight") ?? TryGetScalarAsString(item, "weight");
            string? fontFamily = TryGetString(item, "fontFamily") ?? TryGetString(item, "font");
            bool bold = GetBool(item, "bold", false);
            bool italic = GetBool(item, "italic", false);
            bool underline = GetBool(item, "underline", false);
            bool strikethrough = GetBool(item, "strikethrough", false);
            bool monospace = GetBool(item, "monospace", GetBool(item, "code", false));
            string? href = TryGetString(item, "href") ?? TryGetString(item, "url");
            IReadOnlyList<CustomWidgetActionSpec> onClick = item.TryGetProperty("onClick", out _)
                ? ParseNodeActions(item, "onClick", $"{path}[{idx}]", diagnostics)
                : ParseNodeActions(item, "actions", $"{path}[{idx}]", diagnostics);
            list.Add(new CustomWidgetTextSpanSpec(
                text,
                color,
                fontSize,
                fontWeight,
                fontFamily,
                italic,
                underline,
                strikethrough,
                monospace,
                href,
                bold,
                onClick.Count > 0 ? onClick : null));
            idx++;
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetMenuItemSpec> ParseMenuItems(
        JsonElement el,
        string propName,
        string path,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        int depth = 0)
    {
        if (depth > 4 || !el.TryGetProperty(propName, out JsonElement arrEl) || arrEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CustomWidgetMenuItemSpec>();
        int idx = 0;
        foreach (JsonElement item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                (string.Equals(item.GetString(), "-", StringComparison.Ordinal) ||
                 string.Equals(item.GetString(), "separator", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new CustomWidgetMenuItemSpec(string.Empty, IsSeparator: true));
                idx++;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                idx++;
                continue;
            }

            string itemType = GetString(item, "type", "item");
            bool isSep = GetBool(item, "separator", false) ||
                         GetBool(item, "isSeparator", false) ||
                         string.Equals(itemType, "separator", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(itemType, "divider", StringComparison.OrdinalIgnoreCase);
            string label = GetScalarAsString(item, "label", GetScalarAsString(item, "title", GetScalarAsString(item, "text", string.Empty)));
            string? rawIcon = TryGetString(item, "icon");
            string? icon = rawIcon is null ? null : NormalizeGlyphWithDiagnostics(rawIcon, $"{path}[{idx}].icon", diagnostics, "\uE71D");
            string? when = TryGetString(item, "$when") ?? TryGetString(item, "when") ?? TryGetScalarAsString(item, "visible");
            string? value = TryGetScalarAsString(item, "value");
            IReadOnlyList<CustomWidgetActionSpec> onClick = item.TryGetProperty("onClick", out _)
                ? ParseNodeActions(item, "onClick", $"{path}[{idx}]", diagnostics)
                : ParseNodeActions(item, "actions", $"{path}[{idx}]", diagnostics);
            IReadOnlyList<CustomWidgetMenuItemSpec> subChildren = item.TryGetProperty("children", out _)
                ? ParseMenuItems(item, "children", $"{path}[{idx}].children", diagnostics, depth + 1)
                : ParseMenuItems(item, "items", $"{path}[{idx}].items", diagnostics, depth + 1);
            list.Add(new CustomWidgetMenuItemSpec(
                label,
                icon,
                isSep,
                when,
                onClick.Count > 0 ? onClick : null,
                value,
                subChildren.Count > 0 ? subChildren : null));
            idx++;
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetTableColumnSpec> ParseTableColumns(JsonElement el)
    {
        JsonElement colsEl = default;
        if (el.TryGetProperty("columns", out JsonElement cProp) && cProp.ValueKind == JsonValueKind.Array)
        {
            colsEl = cProp;
        }
        else if (el.TryGetProperty("tableColumns", out JsonElement tcProp) && tcProp.ValueKind == JsonValueKind.Array)
        {
            colsEl = tcProp;
        }
        else
        {
            return [];
        }

        var cols = new List<CustomWidgetTableColumnSpec>();
        foreach (JsonElement item in colsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                string field = GetString(item, "field", GetString(item, "key", string.Empty));
                string header = GetString(item, "header", GetString(item, "title", GetString(item, "label", field)));
                string width = GetScalarAsString(item, "width", "*");
                string align = GetString(item, "align", "left");
                string? color = TryGetString(item, "color") ?? TryGetString(item, "textColor");
                string? format = TryGetString(item, "format");
                if (!string.IsNullOrWhiteSpace(field))
                {
                    cols.Add(new CustomWidgetTableColumnSpec(field, header, width, align, color, format));
                }
            }
            else if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                string f = item.GetString()!.Trim();
                cols.Add(new CustomWidgetTableColumnSpec(f, f));
            }
        }

        return cols;
    }

    private static IReadOnlyList<CustomWidgetChartSeriesSpec> ParseChartSeries(JsonElement el)
    {
        JsonElement serEl = default;
        if (el.TryGetProperty("series", out JsonElement sProp) && sProp.ValueKind == JsonValueKind.Array)
        {
            serEl = sProp;
        }
        else if (el.TryGetProperty("segments", out JsonElement segProp) && segProp.ValueKind == JsonValueKind.Array)
        {
            serEl = segProp;
        }
        else
        {
            return [];
        }

        var list = new List<CustomWidgetChartSeriesSpec>();
        foreach (JsonElement item in serEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = GetString(item, "name", GetString(item, "label", GetString(item, "title", string.Empty)));
            string valueField = GetString(item, "valueField", GetString(item, "field", string.Empty));
            string? color = TryGetString(item, "color");
            string? kind = TryGetString(item, "kind") ?? TryGetString(item, "type");
            List<double>? values = null;
            if (item.TryGetProperty("values", out JsonElement valsEl) && valsEl.ValueKind == JsonValueKind.Array)
            {
                values = new List<double>();
                foreach (JsonElement vEl in valsEl.EnumerateArray())
                {
                    if (vEl.ValueKind == JsonValueKind.Number && vEl.TryGetDouble(out double dv))
                    {
                        values.Add(dv);
                    }
                    else if (vEl.ValueKind == JsonValueKind.String &&
                             double.TryParse(vEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
                    {
                        values.Add(sv);
                    }
                }
            }
            else if (item.TryGetProperty("value", out JsonElement singleVal))
            {
                if (singleVal.ValueKind == JsonValueKind.Number && singleVal.TryGetDouble(out double dv))
                {
                    values = [dv];
                }
                else if (singleVal.ValueKind == JsonValueKind.String &&
                         double.TryParse(singleVal.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv))
                {
                    values = [sv];
                }
            }

            list.Add(new CustomWidgetChartSeriesSpec(name, valueField, color, kind, values));
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetCanvasElementSpec> ParseCanvasElements(JsonElement arrEl)
    {
        if (arrEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<CustomWidgetCanvasElementSpec>();
        foreach (JsonElement item in arrEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string shapeType = TryGetString(item, "type") ?? TryGetString(item, "kind") ?? "rect";
            bool isLine = string.Equals(shapeType.Trim(), "line", StringComparison.OrdinalIgnoreCase);
            string? cxVal = TryGetScalarAsString(item, "cx") ?? TryGetScalarAsString(item, "centerX");
            string? cyVal = TryGetScalarAsString(item, "cy") ?? TryGetScalarAsString(item, "centerY");
            string? rotExpr = TryGetScalarAsString(item, "rotation") ??
                              TryGetScalarAsString(item, "rotate") ??
                              TryGetScalarAsString(item, "angle");
            list.Add(new CustomWidgetCanvasElementSpec(
                Type: shapeType,
                Id: TryGetScalarAsString(item, "id"),
                X: TryGetScalarAsString(item, "x") ?? (isLine ? TryGetScalarAsString(item, "x1") : (cxVal ?? TryGetScalarAsString(item, "x1"))),
                Y: TryGetScalarAsString(item, "y") ?? (isLine ? TryGetScalarAsString(item, "y1") : (cyVal ?? TryGetScalarAsString(item, "y1"))),
                X2: TryGetScalarAsString(item, "x2"),
                Y2: TryGetScalarAsString(item, "y2"),
                Width: TryGetScalarAsString(item, "width") ?? TryGetScalarAsString(item, "w"),
                Height: TryGetScalarAsString(item, "height") ?? TryGetScalarAsString(item, "h"),
                Radius: TryGetScalarAsString(item, "r") ?? TryGetScalarAsString(item, "radius"),
                Rx: TryGetScalarAsString(item, "rx") ?? TryGetScalarAsString(item, "cornerRadius"),
                Ry: TryGetScalarAsString(item, "ry"),
                Data: TryGetString(item, "d") ?? TryGetString(item, "data") ?? TryGetString(item, "path"),
                Points: TryGetString(item, "points"),
                Text: TryGetScalarAsString(item, "text") ?? TryGetScalarAsString(item, "label") ?? TryGetScalarAsString(item, "value"),
                Fill: TryGetString(item, "fill") ?? TryGetString(item, "color"),
                Stroke: TryGetString(item, "stroke") ?? TryGetString(item, "borderColor"),
                StrokeWidth: GetDouble(item, "strokeWidth", 1.5),
                Opacity: Math.Clamp(GetDouble(item, "opacity", 1.0), 0.0, 1.0),
                FontSize: Math.Clamp(GetDouble(item, "fontSize", 12.0), 6.0, 96.0),
                Rotation: GetDouble(item, "rotation", GetDouble(item, "rotate", GetDouble(item, "angle", 0))),
                Align: TryGetString(item, "align") ?? TryGetString(item, "textAnchor"),
                Cx: cxVal,
                Cy: cyVal,
                FontWeight: TryGetScalarAsString(item, "fontWeight") ?? TryGetScalarAsString(item, "weight"),
                RotationExpression: rotExpr,
                StartAngle: TryGetScalarAsString(item, "startAngle"),
                EndAngle: TryGetScalarAsString(item, "endAngle"),
                SweepAngle: TryGetScalarAsString(item, "sweepAngle"),
                StrokeCap: TryGetString(item, "strokeCap") ?? TryGetString(item, "lineCap"),
                LinearGradient: ParseLinearGradient(item),
                RadialGradient: ParseRadialGradient(item)));
        }

        return list;
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

#if DESKBOX_STORE
        if (string.Equals(type, "command.run", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                path,
                "Action 'command.run' is not available in the Microsoft Store edition of DeskBox."));
            return null;
        }
#endif
        if (!SupportedActionTypes.Contains(type))
        {
            diagnostics.Add(new(
                path,
                $"Unknown action type '{type}'. Supported: {string.Join(", ", SupportedActionTypes)}."));
        }

        bool forbidActionSecrets =
            type.StartsWith("state.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("collection.", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("clipboard.", StringComparison.OrdinalIgnoreCase);
        foreach (JsonProperty actProp in el.EnumerateObject())
        {
            if (string.Equals(actProp.Name, "then", StringComparison.Ordinal))
            {
                continue;
            }

            bool skipBodyUnclosed = string.Equals(type, "http.request", StringComparison.OrdinalIgnoreCase) &&
                                    actProp.Name is "body" or "payload" or "json" &&
                                    actProp.Value.ValueKind != JsonValueKind.String;
            ScanElementForSecretAndUnclosedInterpolation(
                actProp.Value,
                $"{path}.{actProp.Name}",
                diagnostics,
                forbidSecrets: forbidActionSecrets,
                checkUnclosed: !skipBodyUnclosed);
        }

        Dictionary<string, string>? itemFields = null;
        Dictionary<string, JsonElement>? itemFieldsJson = null;
        if ((el.TryGetProperty("item", out JsonElement itemEl) ||
             el.TryGetProperty("fields", out itemEl)) &&
            itemEl.ValueKind == JsonValueKind.Object)
        {
            itemFields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in itemEl.EnumerateObject())
            {
                itemFields[prop.Name] = ConvertElementToString(prop.Value);
                if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                {
                    itemFieldsJson ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    itemFieldsJson[prop.Name] = prop.Value.Clone();
                }
            }
        }

        IReadOnlyList<CustomWidgetActionSpec>? thenActions = null;
        if (el.TryGetProperty("then", out JsonElement thenEl))
        {
            thenActions = ParseActionList(thenEl, $"{path}.then", diagnostics, depth + 1);
        }

        string? bodyRaw = null;
        if (el.TryGetProperty("body", out JsonElement bodyEl) ||
            el.TryGetProperty("payload", out bodyEl) ||
            el.TryGetProperty("json", out bodyEl))
        {
            bodyRaw = bodyEl.ValueKind == JsonValueKind.String
                ? bodyEl.GetString()
                : bodyEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : bodyEl.GetRawText();
        }

        Dictionary<string, string>? structuredArgs = null;
        Dictionary<string, JsonElement>? structuredArgsJson = null;
        IReadOnlyList<string>? commandArgs = null;
        if (el.TryGetProperty("args", out JsonElement argsProp) && argsProp.ValueKind == JsonValueKind.Object)
        {
            structuredArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in argsProp.EnumerateObject())
            {
                structuredArgs[prop.Name] = ConvertElementToString(prop.Value);
                if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                {
                    structuredArgsJson ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    structuredArgsJson[prop.Name] = prop.Value.Clone();
                }
            }
        }
        else if (el.TryGetProperty("args", out argsProp) && argsProp.ValueKind == JsonValueKind.Array)
        {
            commandArgs = ParseScalarArray(el, "args");
            structuredArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            int argIdx = 0;
            foreach (JsonElement item in argsProp.EnumerateArray())
            {
                string idxKey = argIdx.ToString(CultureInfo.InvariantCulture);
                structuredArgs[idxKey] = ConvertElementToString(item);
                if (item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                {
                    structuredArgsJson ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    structuredArgsJson[idxKey] = item.Clone();
                }
                argIdx++;
            }
        }
        else if (el.TryGetProperty("arg", out JsonElement singularArgObj) && singularArgObj.ValueKind == JsonValueKind.Object)
        {
            structuredArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in singularArgObj.EnumerateObject())
            {
                structuredArgs[prop.Name] = ConvertElementToString(prop.Value);
                if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                {
                    structuredArgsJson ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    structuredArgsJson[prop.Name] = prop.Value.Clone();
                }
            }
        }
        else if (el.TryGetProperty("arg", out JsonElement singularArgArr) && singularArgArr.ValueKind == JsonValueKind.Array)
        {
            commandArgs = ParseScalarArray(el, "arg");
            structuredArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            int argIdx = 0;
            foreach (JsonElement item in singularArgArr.EnumerateArray())
            {
                string idxKey = argIdx.ToString(CultureInfo.InvariantCulture);
                structuredArgs[idxKey] = ConvertElementToString(item);
                if (item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                {
                    structuredArgsJson ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    structuredArgsJson[idxKey] = item.Clone();
                }
                argIdx++;
            }
        }

        if (commandArgs is null && (el.TryGetProperty("arguments", out _) || el.TryGetProperty("commandArgs", out _)))
        {
            commandArgs = el.TryGetProperty("arguments", out _)
                ? ParseScalarArray(el, "arguments")
                : ParseScalarArray(el, "commandArgs");
        }

        bool isCommandRun = string.Equals(type, "command.run", StringComparison.OrdinalIgnoreCase);
        bool isFileListDir = string.Equals(type, "file.listDir", StringComparison.OrdinalIgnoreCase);
        bool isFileManage = string.Equals(type, "file.manage", StringComparison.OrdinalIgnoreCase);
        bool longRunning = GetBool(el, "longRunning", false);
        int defaultTimeoutMs = isCommandRun ? 30_000 : 15_000;
        int parsedTimeoutMs;
        if (longRunning)
        {
            parsedTimeoutMs = el.TryGetProperty("timeoutMs", out _)
                ? Math.Max(0, GetInt(el, "timeoutMs", 0))
                : 0;
        }
        else if (el.TryGetProperty("timeoutMs", out _) && GetInt(el, "timeoutMs", defaultTimeoutMs) == 0)
        {
            parsedTimeoutMs = 0;
            longRunning = true;
        }
        else
        {
            parsedTimeoutMs = Math.Clamp(GetInt(el, "timeoutMs", defaultTimeoutMs), 500, 3_600_000);
        }

        string? actionMode = TryGetString(el, "mode");
        if (string.IsNullOrWhiteSpace(actionMode) &&
            el.TryGetProperty("append", out JsonElement appendProp) &&
            appendProp.ValueKind == JsonValueKind.True)
        {
            actionMode = "append";
        }

        bool multiple = GetBool(el, "multiple", false) ||
                        string.Equals(actionMode, "files", StringComparison.OrdinalIgnoreCase);
        JsonElement? rawJsonValue = null;
        if (el.TryGetProperty("value", out JsonElement valProp) &&
            valProp.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            rawJsonValue = valProp.Clone();
        }

        string? actionId = TryGetScalarAsString(el, "id") ??
                           TryGetScalarAsString(el, "trackId");
        string? jobKey = TryGetScalarAsString(el, "jobKey") ?? actionId;
        string? destination = TryGetScalarAsString(el, "destination") ??
                              TryGetScalarAsString(el, "dest") ??
                              TryGetScalarAsString(el, "targetPath") ??
                              TryGetScalarAsString(el, "newName");

        return new CustomWidgetActionSpec(
            Type: type,
            Key: (isCommandRun || isFileListDir || isFileManage)
                ? (TryGetString(el, "key") ?? TryGetString(el, "stateKey"))
                : (TryGetString(el, "key") ?? TryGetString(el, "stateKey") ?? TryGetString(el, "command") ?? TryGetString(el, "exe")),
            Value: TryGetScalarAsString(el, "value") ??
                   TryGetScalarAsString(el, "content") ??
                   TryGetScalarAsString(el, "cue"),
            Collection: TryGetString(el, "collection") ?? TryGetString(el, "targetCollection"),
            TargetId: TryGetScalarAsString(el, "targetId") ??
                      TryGetScalarAsString(el, "target") ??
                      TryGetScalarAsString(el, "trackId") ??
                      actionId ??
                      TryGetScalarAsString(el, "sourceId") ??
                      TryGetScalarAsString(el, "source"),
            Format: TryGetScalarAsString(el, "format") ??
                    TryGetScalarAsString(el, "op") ??
                    TryGetScalarAsString(el, "operation"),
            Path: isCommandRun
                ? (TryGetScalarAsString(el, "command") ??
                   TryGetScalarAsString(el, "executable") ??
                   TryGetScalarAsString(el, "exe") ??
                   TryGetScalarAsString(el, "path") ??
                   TryGetScalarAsString(el, "filePath"))
                : (TryGetScalarAsString(el, "path") ?? TryGetScalarAsString(el, "filePath") ?? TryGetScalarAsString(el, "dir")),
            Url: TryGetScalarAsString(el, "url") ??
                 TryGetScalarAsString(el, "src"),
            Text: TryGetScalarAsString(el, "text"),
            Message: TryGetScalarAsString(el, "message"),
            Guard: TryGetString(el, "guard") ?? TryGetString(el, "$when"),
            ItemFields: itemFields,
            Then: thenActions,
            Prompt: TryGetString(el, "prompt"),
            Mode: actionMode,
            ItemsPath: TryGetString(el, "itemsPath"),
            StateMap: ParseStringDictionary(el, "stateMap"),
            FieldMap: ParseStringDictionary(el, "fieldMap"),
            MaxItems: Math.Clamp(GetInt(el, "maxItems", isFileListDir ? 200 : 20), 1, 500),
            Title: TryGetScalarAsString(el, "title"),
            MaxWidth: GetInt(el, "maxWidth", 0),
            Quality: GetDouble(el, "quality", 0.88),
            Fn: TryGetString(el, "fn") ?? TryGetString(el, "function") ?? TryGetString(el, "handler"),
            Arg: TryGetScalarAsString(el, "arg"),
            Method: TryGetString(el, "method"),
            Headers: ParseStringDictionary(el, "headers"),
            Body: bodyRaw,
            TimeoutMs: parsedTimeoutMs,
            Args: structuredArgs,
            CommandArgs: commandArgs,
            WorkingDirectory: TryGetString(el, "workingDirectory") ?? TryGetString(el, "cwd"),
            PopoverId: TryGetScalarAsString(el, "popoverId") ?? TryGetScalarAsString(el, "popover") ?? TryGetScalarAsString(el, "tipId"),
            FromIndex: TryGetScalarAsString(el, "fromIndex") ?? TryGetScalarAsString(el, "from"),
            ToIndex: TryGetScalarAsString(el, "toIndex") ?? TryGetScalarAsString(el, "to"),
            Id: actionId,
            JobKey: jobKey,
            LongRunning: longRunning,
            OnProgress: TryGetString(el, "onProgress"),
            OnComplete: TryGetString(el, "onComplete") ?? TryGetString(el, "callback"),
            Multiple: multiple,
            Accept: TryGetString(el, "accept"),
            Recursive: GetBool(el, "recursive", false),
            Pattern: TryGetString(el, "pattern") ?? TryGetString(el, "filter"),
            Sort: TryGetString(el, "sort") ?? TryGetString(el, "orderBy"),
            Destination: destination,
            RawJsonValue: rawJsonValue,
            Loop: GetBool(el, "loop", false),
            Volume: TryGetScalarAsString(el, "volume"),
            ItemFieldsJson: itemFieldsJson,
            DueIso: TryGetScalarAsString(el, "dueIso") ?? TryGetScalarAsString(el, "dueDate") ?? TryGetScalarAsString(el, "due"),
            Priority: TryGetScalarAsString(el, "priority"),
            ArgsJson: structuredArgsJson);
    }

    private static IReadOnlyList<string> ParseScalarArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out JsonElement prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (JsonElement item in prop.EnumerateArray())
        {
            string val = ConvertElementToString(item);
            if (!string.IsNullOrEmpty(val))
            {
                result.Add(val);
            }
        }
        return result;
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

    private static double? TryGetOptionalDouble(JsonElement el, string name)
    {
        double? val = TryGetDouble(el, name);
        return val is double d && double.IsFinite(d) ? d : null;
    }

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

    internal static bool IsValidStaticGridTrackList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        string[] tokens = raw.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            if (string.Equals(token, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.EndsWith('*'))
            {
                string numPart = token[..^1].Trim();
                if (numPart.Length == 0)
                {
                    continue;
                }

                if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.IsFinite(w) &&
                    w > 0)
                {
                    continue;
                }

                return false;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
                double.IsFinite(px) &&
                px >= 0)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool TryParseItemsGridColumnCount(string? raw, out int columnCount)
    {
        columnCount = 0;
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intCols))
        {
            if (intCols is >= 1 and <= 16)
            {
                columnCount = intCols;
                return true;
            }

            return false;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblCols) &&
            double.IsFinite(dblCols) &&
            dblCols >= 1.0 &&
            dblCols <= 16.0 &&
            Math.Abs(dblCols - Math.Round(dblCols)) < 0.001)
        {
            columnCount = (int)Math.Round(dblCols);
            return true;
        }

        return false;
    }

    internal static void ScanElementForSecretAndUnclosedInterpolation(
        JsonElement el,
        string propPath,
        List<CustomWidgetValidationDiagnostic> diagnostics,
        bool forbidSecrets,
        bool checkUnclosed,
        bool skipActionPropertiesInNestedObjects = false)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
            {
                string? raw = el.GetString();
                if (string.IsNullOrEmpty(raw) || !raw.Contains("${", StringComparison.Ordinal))
                {
                    return;
                }

                if (checkUnclosed && HasUnclosedInterpolation(raw))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        propPath,
                        $"Template '{raw}' contains an unclosed '${{...' interpolation expression. Close the '}}' or complete the binding.",
                        IsError: true,
                        Code: CustomWidgetStrictAuthoringRules.UnclosedInterpolationExpression));
                }

                if (forbidSecrets && ContainsSecretInterpolation(raw))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        propPath,
                        $"Secret interpolation ('${{$secrets.<key>}}') is forbidden in '{propPath}'. Secrets may only be interpolated in 'sources[].url', 'sources[].headers', and 'http.request' url/headers/body.",
                        IsError: true,
                        Code: CustomWidgetDiagnosticCodes.SecretInterpolationForbiddenInViewOrState));
                }
                break;
            }

            case JsonValueKind.Array:
            {
                int idx = 0;
                foreach (JsonElement item in el.EnumerateArray())
                {
                    ScanElementForSecretAndUnclosedInterpolation(
                        item,
                        $"{propPath}[{idx}]",
                        diagnostics,
                        forbidSecrets,
                        checkUnclosed,
                        skipActionPropertiesInNestedObjects);
                    idx++;
                }
                break;
            }

            case JsonValueKind.Object:
            {
                foreach (JsonProperty nested in el.EnumerateObject())
                {
                    if (skipActionPropertiesInNestedObjects &&
                        nested.Name is "onClick" or "action" or "actions" or "onSubmit" or "onChange" or "then")
                    {
                        continue;
                    }

                    ScanElementForSecretAndUnclosedInterpolation(
                        nested.Value,
                        $"{propPath}.{nested.Name}",
                        diagnostics,
                        forbidSecrets,
                        checkUnclosed,
                        skipActionPropertiesInNestedObjects);
                }
                break;
            }
        }
    }

    internal static bool ContainsSecretInterpolation(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        if (!raw.Contains("secrets.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SecretInterpolationExprRegex().IsMatch(raw);
    }

    internal static bool HasUnclosedInterpolation(string? rawTemplate)
    {
        if (string.IsNullOrEmpty(rawTemplate) || !rawTemplate.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        int i = 0;
        int n = rawTemplate.Length;
        while (i < n - 1)
        {
            if (rawTemplate[i] == '$' && rawTemplate[i + 1] == '{')
            {
                int depth = 1;
                int j = i + 2;
                char quote = '\0';
                bool escaped = false;
                while (j < n && depth > 0)
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

    [GeneratedRegex(@"\$\{[^}]*(?:\$secrets|\bsecrets)\.[A-Za-z0-9_]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretInterpolationExprRegex();
}
