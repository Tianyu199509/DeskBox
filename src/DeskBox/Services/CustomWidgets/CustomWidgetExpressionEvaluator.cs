using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

public sealed record CustomWidgetEvaluationContext
{
    private sealed class SnapshotCache
    {
        public CustomWidgetPomodoroSnapshot? Pomodoro;
        public CustomWidgetCountdownSnapshot? Countdown;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyComputed =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private SnapshotCache? _snapshotCache;

    public IReadOnlyDictionary<string, string> I18n { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> StateJson { get; init; } = EmptyComputed;
    public IReadOnlyDictionary<string, JsonElement> Computed { get; init; } = EmptyComputed;
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> Collections { get; init; } =
        new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
    public CustomWidgetSystemMetricsSnapshot SystemMetrics { get; init; } =
        CustomWidgetSystemMetricsSnapshot.Empty;
    public CustomWidgetAiMonitorSnapshot AiMonitor { get; init; } =
        CustomWidgetAiMonitorSnapshot.Empty;
    public CustomWidgetWeatherSnapshot Weather { get; init; } =
        CustomWidgetWeatherSnapshot.Empty;
    public CustomWidgetDeviceStateSnapshot DeviceState { get; init; } =
        CustomWidgetDeviceStateSnapshot.Empty;
    public CustomWidgetClipboardSnapshot Clipboard { get; init; } =
        CustomWidgetClipboardSnapshot.Empty;
    public CustomWidgetStorageDrivesSnapshot StorageDrives { get; init; } =
        CustomWidgetStorageDrivesSnapshot.Empty;
    public CustomWidgetMusicSnapshot Music { get; init; } =
        CustomWidgetMusicSnapshot.Empty;
    public CustomWidgetTodoSnapshot Todo { get; init; } =
        CustomWidgetTodoSnapshot.Empty;
    public IReadOnlyDictionary<string, string> Secrets { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public CustomWidgetTransformResult? TransformResult { get; init; }
    public CustomWidgetRecord? CurrentItem { get; init; }
    public int CurrentIndex { get; init; }
    public CustomWidgetDropFileContext? DropFile { get; init; }
    public IReadOnlyList<CustomWidgetDropFileContext>? DropFiles { get; init; }
    public string? EventDropKind { get; init; }
    public string? EventText { get; init; }
    public IReadOnlyList<CustomWidgetDropItemRecord>? EventDropItems { get; init; }
    public string? EventValue { get; init; }
    public string? EventId { get; init; }
    public double? EventX { get; init; }
    public double? EventY { get; init; }
    public double? EventNormX { get; init; }
    public double? EventNormY { get; init; }
    public bool? EventIsPressed { get; init; }
    public int? EventFromIndex { get; init; }
    public int? EventToIndex { get; init; }
    public DateTimeOffset? ClockOverride { get; init; }
    public string? CultureName { get; init; }
    public string? WidgetId { get; init; }
    public string SearchShortcutText { get; init; } = string.Empty;
    public bool IsPairedSlot { get; init; }

    public CustomWidgetEvaluationContext WithPairedSlot(bool paired) =>
        IsPairedSlot == paired
            ? this
            : this with
            {
                _snapshotCache = _snapshotCache ??= new SnapshotCache(),
                IsPairedSlot = paired
            };

    internal CustomWidgetPomodoroSnapshot GetOrComputePomodoroSnapshot()
    {
        SnapshotCache cache = _snapshotCache ??= new SnapshotCache();
        return cache.Pomodoro ??= CustomWidgetPomodoroEngine.ComputeSnapshot(
            State,
            Settings,
            ClockOverride ?? DateTimeOffset.Now,
            CultureName);
    }

    internal CustomWidgetCountdownSnapshot GetOrComputeCountdownSnapshot()
    {
        SnapshotCache cache = _snapshotCache ??= new SnapshotCache();
        return cache.Countdown ??= CustomWidgetCountdownEngine.ComputeSnapshot(
            State,
            Settings,
            Collections,
            ClockOverride ?? DateTimeOffset.Now,
            CultureName);
    }

    public CustomWidgetEvaluationContext WithItem(CustomWidgetRecord item, int index) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            CurrentItem = item,
            CurrentIndex = index
        };

    public CustomWidgetEvaluationContext WithDrop(
        CustomWidgetDropFileContext? drop,
        IReadOnlyList<CustomWidgetDropFileContext>? files = null) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            DropFile = drop ?? (files is { Count: > 0 } ? files[0] : null),
            DropFiles = files ?? DropFiles ?? (drop is not null ? [drop] : null),
            EventDropKind = EventDropKind ?? ((drop is not null || files is { Count: > 0 }) ? "files" : null)
        };

    public CustomWidgetEvaluationContext WithStructuredDrop(
        string? dropKind,
        string? dropText,
        IReadOnlyList<CustomWidgetDropItemRecord>? items,
        CustomWidgetDropFileContext? drop = null,
        IReadOnlyList<CustomWidgetDropFileContext>? files = null)
    {
        CustomWidgetDropFileContext? resolvedDrop = drop ?? (files is { Count: > 0 } ? files[0] : DropFile);
        IReadOnlyList<CustomWidgetDropFileContext>? resolvedFiles = files ?? DropFiles ?? (drop is not null ? [drop] : null);
        string? fallbackValue = !string.IsNullOrEmpty(dropText)
            ? dropText
            : items is { Count: > 0 } && !string.IsNullOrEmpty(items[0].Text)
                ? items[0].Text
                : resolvedDrop?.Path ?? EventValue;
        return this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            DropFile = resolvedDrop,
            DropFiles = resolvedFiles,
            EventDropKind = dropKind,
            EventText = dropText,
            EventDropItems = items,
            EventValue = fallbackValue
        };
    }

    public CustomWidgetEvaluationContext WithClipboard(CustomWidgetClipboardSnapshot clipboard) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            Clipboard = clipboard
        };

    public CustomWidgetEvaluationContext WithTransform(CustomWidgetTransformResult transform) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            TransformResult = transform
        };

    public CustomWidgetEvaluationContext WithEventValue(string? eventValue) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            EventValue = eventValue
        };

    public CustomWidgetEvaluationContext WithPointerEvent(
        string? id,
        double x,
        double y,
        double? normX = null,
        double? normY = null,
        bool? isPressed = null) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            EventValue = id ?? EventValue,
            EventId = id,
            EventX = x,
            EventY = y,
            EventNormX = normX,
            EventNormY = normY,
            EventIsPressed = isPressed
        };

    public CustomWidgetEvaluationContext WithReorderEvent(int fromIndex, int toIndex) =>
        this with
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            EventFromIndex = fromIndex,
            EventToIndex = toIndex
        };

    public bool AllowSecrets { get; init; }

    public CustomWidgetEvaluationContext WithAllowSecrets(bool allowSecrets = true) =>
        AllowSecrets == allowSecrets
            ? this
            : this with
            {
                _snapshotCache = _snapshotCache ??= new SnapshotCache(),
                AllowSecrets = allowSecrets
            };
}

/// <summary>
/// AOT-safe expression and template evaluator for Custom Widgets.
/// Supports <c>${...}</c> interpolation, null-coalescing (<c>??</c>),
/// ternary (<c>? :</c>), boolean logic (<c>&amp;&amp;</c>, <c>||</c>),
/// comparisons (<c>==</c>, <c>!=</c>, <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c>),
/// numeric arithmetic (<c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>), and <c>$rank</c> / <c>$index</c>.
/// </summary>
public static class CustomWidgetExpressionEvaluator
{
    public const int MaxTemplateLength = 16384;
    public const int MaxExpressionLength = 4096;
    public const int MaxEvaluationDepth = 64;

    public static string Interpolate(string? template, CustomWidgetEvaluationContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        if (template.Length > MaxTemplateLength)
        {
            template = template[..MaxTemplateLength];
        }

        if (!template.Contains("${", StringComparison.Ordinal))
        {
            if (template.StartsWith('$') && !template.Contains(' '))
            {
                return ResolveSingleExpression(template, context);
            }
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        int cursor = 0;
        while (cursor < template.Length)
        {
            int open = template.IndexOf("${", cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(template, cursor, template.Length - cursor);
                break;
            }

            if (open > cursor)
            {
                sb.Append(template, cursor, open - cursor);
            }

            int close = FindMatchingBrace(template, open + 2);
            if (close < 0)
            {
                sb.Append(template, open, template.Length - open);
                break;
            }

            string expr = template.AsSpan(open + 2, close - (open + 2)).Trim().ToString();
            sb.Append(EvaluateExpression(expr, context));
            cursor = close + 1;
        }

        return sb.ToString();
    }

    public static bool EvaluateCondition(string? expression, CustomWidgetEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        string trimmed = expression.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) &&
            trimmed.EndsWith('}') &&
            FindMatchingBrace(trimmed, 2) == trimmed.Length - 1)
        {
            trimmed = trimmed[2..^1].Trim();
        }

        string result = EvaluateExpression(trimmed, context);
        return IsTruthy(result);
    }

    public static double? EvaluateDouble(string? expression, CustomWidgetEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        string evaluated = Interpolate(expression, context).Trim();
        if (double.TryParse(evaluated, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            return val;
        }

        return null;
    }

    public static IReadOnlyList<double> ResolveHistory(
        string? sparklineBinding,
        CustomWidgetEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(sparklineBinding))
        {
            return [];
        }

        string trimmed = sparklineBinding.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) &&
            trimmed.EndsWith('}') &&
            FindMatchingBrace(trimmed, 2) == trimmed.Length - 1)
        {
            trimmed = trimmed[2..^1].Trim();
        }

        if ((trimmed.StartsWith("$computed.", StringComparison.Ordinal) ||
             trimmed.StartsWith("computed.", StringComparison.Ordinal)) &&
            context.Computed.Count > 0)
        {
            string path = trimmed.StartsWith('$')
                ? trimmed["$computed.".Length..]
                : trimmed["computed.".Length..];
            if (TryExtractDoubleArrayFromDictionary(context.Computed, path, out IReadOnlyList<double> computedPoints))
            {
                return computedPoints;
            }
        }

        if (trimmed.StartsWith("$item.", StringComparison.Ordinal) &&
            context.CurrentItem is { FieldsJson.Count: > 0 } curItem)
        {
            string path = trimmed["$item.".Length..];
            if (TryExtractDoubleArrayFromDictionary(curItem.FieldsJson, path, out IReadOnlyList<double> itemPoints))
            {
                return itemPoints;
            }
        }

        if ((trimmed.StartsWith("$state.", StringComparison.Ordinal) ||
             trimmed.StartsWith("state.", StringComparison.Ordinal)) &&
            context.StateJson.Count > 0)
        {
            string path = trimmed.StartsWith('$')
                ? trimmed["$state.".Length..]
                : trimmed["state.".Length..];
            if (TryExtractDoubleArrayFromDictionary(context.StateJson, path, out IReadOnlyList<double> statePoints))
            {
                return statePoints;
            }
        }

        return context.SystemMetrics.ResolveHistory(sparklineBinding);
    }

    private static bool TryExtractDoubleArrayFromDictionary(
        IReadOnlyDictionary<string, JsonElement> dict,
        string path,
        out IReadOnlyList<double> points)
    {
        points = [];
        int dot = path.IndexOf('.');
        string rootKey = dot < 0 ? path : path[..dot];
        if (!dict.TryGetValue(rootKey, out JsonElement cur))
        {
            return false;
        }

        if (dot >= 0)
        {
            ReadOnlySpan<char> subPath = path.AsSpan(dot + 1);
            foreach (Range segRange in subPath.Split('.'))
            {
                ReadOnlySpan<char> seg = subPath[segRange];
                if (seg.IsEmpty)
                {
                    continue;
                }

                if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(seg, out JsonElement child))
                {
                    cur = child;
                }
                else
                {
                    return false;
                }
            }
        }

        if (cur.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<double>();
        foreach (JsonElement el in cur.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
            {
                list.Add(d);
            }
            else if (el.ValueKind == JsonValueKind.String &&
                     double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd) &&
                     double.IsFinite(sd))
            {
                list.Add(sd);
            }
        }

        points = list;
        return true;
    }

    private static readonly string[] ComparisonOperators = ["==", "!=", ">=", "<=", ">", "<"];

    public static string EvaluateExpression(string expr, CustomWidgetEvaluationContext context) =>
        EvaluateExpressionCore(expr, context, depth: 0);

    private static string EvaluateExpressionCore(string expr, CustomWidgetEvaluationContext context, int depth)
    {
        if (depth > MaxEvaluationDepth || string.IsNullOrEmpty(expr) || expr.Length > MaxExpressionLength)
        {
            return string.Empty;
        }

        expr = expr.Trim();
        int parenStripCount = 0;
        while (HasOuterBalancedParentheses(expr))
        {
            if (++parenStripCount > MaxEvaluationDepth)
            {
                return string.Empty;
            }

            expr = expr[1..^1].Trim();
        }

        if (string.IsNullOrEmpty(expr))
        {
            return string.Empty;
        }

        // 1. Ternary operator: cond ? trueBranch : falseBranch
        int questionIdx = FindTopLevelOperator(expr, "?");
        if (questionIdx > 0)
        {
            int actualColon = FindMatchingTernaryColon(expr, questionIdx + 1);
            if (actualColon > questionIdx)
            {
                string condPart = expr[..questionIdx].Trim();
                string truePart = expr.AsSpan(questionIdx + 1, actualColon - (questionIdx + 1)).Trim().ToString();
                string falsePart = expr[(actualColon + 1)..].Trim();
                return IsTruthy(EvaluateExpressionCore(condPart, context, depth + 1))
                    ? EvaluateExpressionCore(truePart, context, depth + 1)
                    : EvaluateExpressionCore(falsePart, context, depth + 1);
            }
        }

        // 2. Null/empty coalescing: left ?? right
        int coalesceIdx = FindTopLevelOperator(expr, "??");
        if (coalesceIdx > 0)
        {
            string left = EvaluateExpressionCore(expr[..coalesceIdx], context, depth + 1);
            if (!string.IsNullOrWhiteSpace(left))
            {
                return left;
            }
            return EvaluateExpressionCore(expr[(coalesceIdx + 2)..], context, depth + 1);
        }

        // 3. Logical OR: left || right (JS value-preserving short-circuit: returns leftVal if truthy, else rightVal)
        int orIdx = FindTopLevelOperator(expr, "||");
        if (orIdx > 0)
        {
            string leftVal = EvaluateExpressionCore(expr[..orIdx], context, depth + 1);
            if (IsTruthy(leftVal)) return leftVal;
            return EvaluateExpressionCore(expr[(orIdx + 2)..], context, depth + 1);
        }

        // 4. Logical AND: left && right (JS value-preserving short-circuit: returns leftVal if falsy, else rightVal)
        int andIdx = FindTopLevelOperator(expr, "&&");
        if (andIdx > 0)
        {
            string leftVal = EvaluateExpressionCore(expr[..andIdx], context, depth + 1);
            if (!IsTruthy(leftVal)) return leftVal;
            return EvaluateExpressionCore(expr[(andIdx + 2)..], context, depth + 1);
        }

        // 5. Comparisons: ==, !=, >=, <=, >, <
        foreach (string op in ComparisonOperators)
        {
            int opIdx = FindTopLevelOperator(expr, op);
            if (opIdx > 0)
            {
                string leftVal = EvaluateExpressionCore(expr[..opIdx], context, depth + 1);
                string rightVal = EvaluateExpressionCore(expr[(opIdx + op.Length)..], context, depth + 1);
                return CompareValues(leftVal, rightVal, op) ? "true" : "false";
            }
        }

        // 6. Numeric additive operators (+, -), left-associative (scan right-to-left)
        if (!IsDirectHyphenatedIdentifier(expr, context) &&
            TryEvaluateTopLevelArithmetic(expr, context, ['+', '-'], depth + 1, out string additiveResult))
        {
            return additiveResult;
        }

        // 7. Numeric multiplicative operators (*, /, %), left-associative (scan right-to-left)
        if (TryEvaluateTopLevelArithmetic(expr, context, ['*', '/', '%'], depth + 1, out string multiplicativeResult))
        {
            return multiplicativeResult;
        }

        // 8. Unary NOT: !expr
        if (expr.StartsWith('!'))
        {
            return IsTruthy(EvaluateExpressionCore(expr[1..], context, depth + 1)) ? "false" : "true";
        }

        // 9. Unary numeric sign: -expr / +expr (when not already a literal float)
        if ((expr[0] is '-' or '+') &&
            expr.Length > 1 &&
            !double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            string innerVal = EvaluateExpressionCore(expr[1..], context, depth + 1);
            if (double.TryParse(innerVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                return FormatNumericResult(expr[0] == '-' ? -num : num);
            }
        }

        if (TryEvaluatePostfixMemberChain(expr, context, depth, out string memberResult))
        {
            return memberResult;
        }

        if (TryEvaluateFunctionCall(expr, context, depth, out string fnResult))
        {
            return fnResult;
        }

        return ResolveSingleExpression(expr, context);
    }

    private static readonly System.Collections.Frozen.FrozenSet<string> ReservedRootNamespaces =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "state", "$state", "settings", "$settings", "secrets", "$secrets",
            "i18n", "$i18n", "collections", "$collections", "computed", "$computed",
            "event", "$event", "$item", "$sys", "$weather", "$calendar", "$device",
            "$clipboard", "$drives", "$music", "$todo", "$clock", "$pomodoro", "$pomo",
            "$countdown", "$ai", "$widget", "$time", "$slot", "$drop", "$transform"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static bool TryEvaluatePostfixMemberChain(
        string expr,
        CustomWidgetEvaluationContext context,
        int depth,
        out string result)
    {
        result = string.Empty;
        if (depth > MaxEvaluationDepth || expr.Length < 4 || !expr.Contains('.'))
        {
            return false;
        }

        // Case A: Method call `<receiver>.<method>(<args>)`
        if (expr[^1] == ')')
        {
            int openParen = FindMatchingOpenParenFromEnd(expr);
            if (openParen > 2)
            {
                ReadOnlySpan<char> head = expr.AsSpan(0, openParen).TrimEnd();
                int dotIdx = FindLastTopLevelDot(head);
                if (dotIdx > 0 && dotIdx < head.Length - 1)
                {
                    string receiverRaw = head[..dotIdx].Trim().ToString();
                    string methodName = head[(dotIdx + 1)..].Trim().ToString();
                    if (receiverRaw.Length > 0 && IsAsciiIdentifier(methodName))
                    {
                        string argsBody = expr.Substring(openParen + 1, expr.Length - openParen - 2);
                        if (TryDispatchStringMethod(receiverRaw, methodName, argsBody, context, depth, out result))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // Case B: Property `.length` or `.count` on a collection name or evaluated string expression
        int propDot = FindLastTopLevelDot(expr.AsSpan());
        if (propDot <= 0 || propDot >= expr.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> memberSpan = expr.AsSpan(propDot + 1).Trim();
        bool isLength = memberSpan.Equals("length", StringComparison.Ordinal);
        bool isCount = memberSpan.Equals("count", StringComparison.Ordinal);
        if (!isLength && !isCount)
        {
            return false;
        }

        string receiverToken = expr[..propDot].Trim();
        if (receiverToken.Length == 0 || ReservedRootNamespaces.Contains(receiverToken))
        {
            return false;
        }

        // Direct collection lookup: `items.length`, `articles.count` (when not shadowed by a flat state key `items.length`)
        if (!context.State.ContainsKey(expr) &&
            context.Collections.TryGetValue(receiverToken, out List<CustomWidgetRecord>? colRecords))
        {
            result = (colRecords?.Count ?? 0).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        // `collections.<name>.length` / `$collections.<name>.length`
        if (receiverToken.StartsWith("collections.", StringComparison.Ordinal) ||
            receiverToken.StartsWith("$collections.", StringComparison.Ordinal))
        {
            string colName = receiverToken.StartsWith('$')
                ? receiverToken["$collections.".Length..]
                : receiverToken["collections.".Length..];
            if (!colName.Contains('.') && context.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? namedRecords))
            {
                result = (namedRecords?.Count ?? 0).ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (!colName.Contains('.'))
            {
                result = "0";
                return true;
            }
        }

        // String `.length` on any expression (e.g. `state.keyword.trim().length`, `$item.title.length`, `(state.q ?? '').length`)
        if (isLength && !context.State.ContainsKey(expr))
        {
            if (receiverToken.StartsWith("state.", StringComparison.Ordinal) ||
                receiverToken.StartsWith("$state.", StringComparison.Ordinal))
            {
                string stateSubKey = receiverToken.StartsWith('$')
                    ? receiverToken["$state.".Length..] + ".length"
                    : receiverToken["state.".Length..] + ".length";
                if (context.State.ContainsKey(stateSubKey))
                {
                    return false;
                }
            }

            // If a JSON array lives in StateJson or Computed (e.g. `state.tags.length` or `$computed.rows.length`), return array length!
            if (TryResolveJsonArrayLength(receiverToken, context, out int jsonArrLen))
            {
                result = jsonArrLen.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            string evaluatedReceiver = EvaluateExpressionCore(receiverToken, context, depth + 1);
            result = evaluatedReceiver.Length.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool TryResolveJsonArrayLength(string receiverToken, CustomWidgetEvaluationContext context, out int length)
    {
        length = 0;
        IReadOnlyDictionary<string, JsonElement>? source = null;
        string subPath = string.Empty;
        if (receiverToken.StartsWith("$computed.", StringComparison.Ordinal) ||
            receiverToken.StartsWith("computed.", StringComparison.Ordinal))
        {
            source = context.Computed;
            subPath = receiverToken.StartsWith('$') ? receiverToken["$computed.".Length..] : receiverToken["computed.".Length..];
        }
        else if (receiverToken.StartsWith("$state.", StringComparison.Ordinal) ||
                 receiverToken.StartsWith("state.", StringComparison.Ordinal))
        {
            source = context.StateJson;
            subPath = receiverToken.StartsWith('$') ? receiverToken["$state.".Length..] : receiverToken["state.".Length..];
        }
        else if ((receiverToken.StartsWith("$item.", StringComparison.Ordinal) ||
                  receiverToken.StartsWith("item.", StringComparison.Ordinal)) &&
                 context.CurrentItem is not null)
        {
            source = context.CurrentItem.FieldsJson;
            subPath = receiverToken.StartsWith('$') ? receiverToken["$item.".Length..] : receiverToken["item.".Length..];
        }
        else if (context.StateJson.ContainsKey(receiverToken))
        {
            source = context.StateJson;
            subPath = receiverToken;
        }

        if (source is null || source.Count == 0 || string.IsNullOrEmpty(subPath))
        {
            return false;
        }

        int dot = subPath.IndexOf('.');
        string rootKey = dot < 0 ? subPath : subPath[..dot];
        if (!source.TryGetValue(rootKey, out JsonElement cur))
        {
            return false;
        }

        if (dot >= 0)
        {
            ReadOnlySpan<char> rest = subPath.AsSpan(dot + 1);
            foreach (Range segRange in rest.Split('.'))
            {
                ReadOnlySpan<char> seg = rest[segRange];
                if (seg.IsEmpty) continue;
                if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(seg, out JsonElement child))
                {
                    cur = child;
                }
                else
                {
                    return false;
                }
            }
        }

        if (cur.ValueKind == JsonValueKind.Array)
        {
            length = cur.GetArrayLength();
            return true;
        }

        return false;
    }

    private static bool TryDispatchStringMethod(
        string receiverRaw,
        string methodName,
        string argsBody,
        CustomWidgetEvaluationContext context,
        int depth,
        out string result)
    {
        result = string.Empty;
        string receiver = EvaluateExpressionCore(receiverRaw, context, depth + 1);
        List<string> rawArgs = SplitTopLevelArguments(argsBody);

        string EvalArg(int idx) =>
            idx < rawArgs.Count ? EvaluateExpressionCore(rawArgs[idx].Trim(), context, depth + 1) : string.Empty;

        switch (methodName)
        {
            case "toLowerCase":
            case "toLocaleLowerCase":
                result = receiver.ToLowerInvariant();
                return true;
            case "toUpperCase":
            case "toLocaleUpperCase":
                result = receiver.ToUpperInvariant();
                return true;
            case "trim":
                result = receiver.Trim();
                return true;
            case "trimStart":
            case "trimLeft":
                result = receiver.TrimStart();
                return true;
            case "trimEnd":
            case "trimRight":
                result = receiver.TrimEnd();
                return true;
            case "toString":
                result = receiver;
                return true;
            case "includes":
            case "contains":
            {
                string sub = EvalArg(0);
                result = sub.Length == 0 || receiver.Contains(sub, StringComparison.Ordinal) ? "true" : "false";
                return true;
            }
            case "startsWith":
            {
                string prefix = EvalArg(0);
                result = receiver.StartsWith(prefix, StringComparison.Ordinal) ? "true" : "false";
                return true;
            }
            case "endsWith":
            {
                string suffix = EvalArg(0);
                result = receiver.EndsWith(suffix, StringComparison.Ordinal) ? "true" : "false";
                return true;
            }
            case "indexOf":
            {
                string sub = EvalArg(0);
                int fromIndex = rawArgs.Count >= 2 && int.TryParse(EvalArg(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fi)
                    ? Math.Clamp(fi, 0, receiver.Length)
                    : 0;
                int found = receiver.IndexOf(sub, fromIndex, StringComparison.Ordinal);
                result = found.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case "lastIndexOf":
            {
                string sub = EvalArg(0);
                int found = receiver.LastIndexOf(sub, StringComparison.Ordinal);
                result = found.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            case "replace":
            case "replaceAll":
            {
                string oldVal = EvalArg(0);
                string newVal = EvalArg(1);
                if (oldVal.Length == 0)
                {
                    result = receiver;
                    return true;
                }
                if (string.Equals(methodName, "replace", StringComparison.Ordinal))
                {
                    int first = receiver.IndexOf(oldVal, StringComparison.Ordinal);
                    result = first < 0 ? receiver : string.Concat(receiver.AsSpan(0, first), newVal, receiver.AsSpan(first + oldVal.Length));
                }
                else
                {
                    result = receiver.Replace(oldVal, newVal, StringComparison.Ordinal);
                }
                return true;
            }
            case "slice":
            case "substring":
            {
                int len = receiver.Length;
                int start = rawArgs.Count >= 1 && int.TryParse(EvalArg(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
                    ? s
                    : 0;
                int end = rawArgs.Count >= 2 && int.TryParse(EvalArg(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e)
                    ? e
                    : len;
                if (string.Equals(methodName, "slice", StringComparison.Ordinal))
                {
                    if (start < 0) start = Math.Max(0, len + start);
                    else start = Math.Min(len, start);
                    if (end < 0) end = Math.Max(0, len + end);
                    else end = Math.Min(len, end);
                    result = end > start ? receiver[start..end] : string.Empty;
                }
                else
                {
                    start = Math.Clamp(start, 0, len);
                    end = Math.Clamp(end, 0, len);
                    if (start > end) (start, end) = (end, start);
                    result = receiver[start..end];
                }
                return true;
            }
            default:
                return false;
        }
    }

    private static bool IsAsciiIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        for (int i = 1; i < name.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static int FindMatchingOpenParenFromEnd(string expr)
    {
        if (expr.Length < 2 || expr[^1] != ')')
        {
            return -1;
        }

        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        int matchOpen = -1;
        var stack = new Stack<int>(4);
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if ((inSingle || inDouble) && c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }
            if (inSingle || inDouble)
            {
                continue;
            }

            if (c == '(')
            {
                stack.Push(i);
            }
            else if (c == ')')
            {
                if (stack.Count == 0)
                {
                    return -1;
                }
                int openedAt = stack.Pop();
                if (i == expr.Length - 1 && stack.Count == 0)
                {
                    matchOpen = openedAt;
                }
            }
        }

        return !inSingle && !inDouble ? matchOpen : -1;
    }

    private static int FindLastTopLevelDot(ReadOnlySpan<char> span)
    {
        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        int parenDepth = 0;
        int lastDot = -1;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if ((inSingle || inDouble) && c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }
            if (inSingle || inDouble)
            {
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
            }
            else if (c == '.' && parenDepth == 0)
            {
                bool numericDecimal = i > 0 && i + 1 < span.Length && char.IsAsciiDigit(span[i - 1]) && char.IsAsciiDigit(span[i + 1]);
                if (!numericDecimal)
                {
                    lastDot = i;
                }
            }
        }

        return lastDot;
    }

    private static bool TryEvaluateFunctionCall(
        string expr,
        CustomWidgetEvaluationContext context,
        int depth,
        out string result)
    {
        result = string.Empty;
        if (expr.Length < 4 || expr[^1] != ')')
        {
            return false;
        }

        int openParen = expr.IndexOf('(');
        if (openParen <= 0 || openParen >= expr.Length - 1)
        {
            return false;
        }

        string fnName = expr[..openParen].Trim();
        if (string.Equals(fnName, "t", StringComparison.Ordinal))
        {
            string tParenBlock = expr[openParen..];
            if (!HasOuterBalancedParentheses(tParenBlock))
            {
                return false;
            }

            List<string> tArgs = SplitTopLevelArguments(tParenBlock[1..^1]);
            if (tArgs.Count == 0)
            {
                result = string.Empty;
                return true;
            }

            string key = EvaluateFunctionArgument(tArgs[0], context, depth);
            if (string.IsNullOrWhiteSpace(key))
            {
                result = string.Empty;
                return true;
            }

            string tpl = context.I18n.TryGetValue(key, out string? found) && !string.IsNullOrEmpty(found)
                ? found
                : key;

            if (tArgs.Count == 1)
            {
                result = tpl;
                return true;
            }

            var evaluatedArgs = new string[tArgs.Count - 1];
            for (int i = 1; i < tArgs.Count; i++)
            {
                evaluatedArgs[i - 1] = EvaluateExpressionCore(tArgs[i].Trim(), context, depth + 1);
            }

            result = FormatPositionalPlaceholders(tpl, evaluatedArgs);
            return true;
        }

        bool isTz = string.Equals(fnName, "tz", StringComparison.OrdinalIgnoreCase);
        bool isTzDiffLabel = string.Equals(fnName, "tzDiffLabel", StringComparison.OrdinalIgnoreCase);
        bool isTzShortDiff = string.Equals(fnName, "tzShortDiff", StringComparison.OrdinalIgnoreCase);
        bool isTzWorkState = string.Equals(fnName, "tzWorkState", StringComparison.OrdinalIgnoreCase);
        bool isTzCity = string.Equals(fnName, "tzCity", StringComparison.OrdinalIgnoreCase);
        bool isTzHourFloat = string.Equals(fnName, "tzHourFloat", StringComparison.OrdinalIgnoreCase);
        bool isTzDayPhase = string.Equals(fnName, "tzDayPhase", StringComparison.OrdinalIgnoreCase);
        bool isTzSolarProgress = string.Equals(fnName, "tzSolarProgress", StringComparison.OrdinalIgnoreCase);
        bool isTzFloorHour = string.Equals(fnName, "tzFloorHour", StringComparison.OrdinalIgnoreCase);
        if (!isTz && !isTzDiffLabel && !isTzShortDiff && !isTzWorkState && !isTzCity &&
            !isTzHourFloat && !isTzDayPhase && !isTzSolarProgress && !isTzFloorHour)
        {
            return TryEvaluateUtilityFunctionCall(fnName, expr[openParen..], context, depth, out result);
        }

        string parenBlock = expr[openParen..];
        if (!HasOuterBalancedParentheses(parenBlock))
        {
            return false;
        }

        string innerArgs = parenBlock[1..^1];
        List<string> rawArgs = SplitTopLevelArguments(innerArgs);
        DateTimeOffset now = context.ClockOverride ?? DateTimeOffset.Now;

        if (rawArgs.Count == 0)
        {
            result = isTz
                ? CustomWidgetUtilityEngine.ResolveTimeZoneFormatted("UTC", "HH:mm", now, context.CultureName)
                : isTzCity ? "UTC" : string.Empty;
            return true;
        }

        string zone = EvaluateFunctionArgument(rawArgs[0], context, depth);

        if (isTzCity)
        {
            result = CustomWidgetUtilityEngine.ResolveTimeZoneCityName(zone, context.CultureName);
            return true;
        }

        if (isTzDiffLabel || isTzShortDiff || isTzHourFloat || isTzDayPhase || isTzSolarProgress || isTzFloorHour)
        {
            double argOffset = rawArgs.Count >= 2
                ? ParseScrubOffset(EvaluateFunctionArgument(rawArgs[1], context, depth))
                : 0;
            if (isTzDiffLabel)
            {
                result = CustomWidgetUtilityEngine.ResolveTimeZoneDiffLabel(zone, argOffset, now, context.CultureName);
            }
            else if (isTzShortDiff)
            {
                result = CustomWidgetUtilityEngine.ResolveTimeZoneShortDiff(zone, argOffset, now, context.CultureName);
            }
            else if (isTzHourFloat)
            {
                result = CustomWidgetUtilityEngine
                    .ResolveTimeZoneHourFloat(zone, argOffset, now)
                    .ToString("0.####", CultureInfo.InvariantCulture);
            }
            else if (isTzSolarProgress)
            {
                result = CustomWidgetUtilityEngine
                    .ResolveTimeZoneSolarProgress(zone, argOffset, now)
                    .ToString("0.####", CultureInfo.InvariantCulture);
            }
            else if (isTzFloorHour)
            {
                result = CustomWidgetUtilityEngine
                    .ResolveTimeZoneFloorHour(zone, argOffset, now)
                    .ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                result = CustomWidgetUtilityEngine.ResolveTimeZoneDayPhase(zone, argOffset, now);
            }
            return true;
        }

        if (isTzWorkState)
        {
            // Signature: tzWorkState(zoneId, workWindow, offsetHours); a lone numeric 2nd arg is
            // tolerated as an offset so `tzWorkState('UTC', 6)` also works.
            string workWindow = "09-18";
            double workOffset = 0;
            if (rawArgs.Count >= 2)
            {
                string secondArg = EvaluateFunctionArgument(rawArgs[1], context, depth);
                if (rawArgs.Count == 2 &&
                    !rawArgs[1].Trim().StartsWith('\'') &&
                    !rawArgs[1].Trim().StartsWith('"') &&
                    double.TryParse(secondArg, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    workOffset = ParseScrubOffset(secondArg);
                }
                else if (!string.IsNullOrWhiteSpace(secondArg))
                {
                    workWindow = secondArg;
                }
            }

            if (rawArgs.Count >= 3)
            {
                workOffset = ParseScrubOffset(EvaluateFunctionArgument(rawArgs[2], context, depth));
            }

            result = CustomWidgetUtilityEngine.ResolveTimeZoneWorkState(zone, workWindow, workOffset, now);
            return true;
        }

        // tz(zoneId, format?, offsetHours?) — a lone numeric 2nd arg is treated as an offset so
        // `tz('UTC', 6)` works; quoted literals always stay format strings.
        string format = "HH:mm";
        double offset = 0;
        if (rawArgs.Count >= 2)
        {
            string fmtRaw = rawArgs[1].Trim();
            string secondArg = EvaluateFunctionArgument(rawArgs[1], context, depth);
            bool secondArgIsOffset = rawArgs.Count >= 2 &&
                !fmtRaw.StartsWith('\'') &&
                !fmtRaw.StartsWith('"') &&
                double.TryParse(secondArg, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (secondArgIsOffset)
            {
                offset = ParseScrubOffset(secondArg);
            }
            else if (!string.IsNullOrWhiteSpace(secondArg))
            {
                format = secondArg;
            }
            else if (!fmtRaw.StartsWith('$') &&
                     !fmtRaw.StartsWith("state.", StringComparison.OrdinalIgnoreCase) &&
                     !fmtRaw.StartsWith('\'') &&
                     !fmtRaw.StartsWith('"'))
            {
                format = fmtRaw;
            }

            if (!secondArgIsOffset && rawArgs.Count >= 3)
            {
                offset = ParseScrubOffset(EvaluateFunctionArgument(rawArgs[2], context, depth));
            }
        }

        result = CustomWidgetUtilityEngine.ResolveTimeZoneFormatted(
            zone,
            format,
            now,
            context.CultureName,
            offset);
        return true;
    }

    /// <summary>
    /// In-expression bridge to the pure in-memory <see cref="CustomWidgetUtilityEngine.ExecuteTextTransform"/>
    /// operators, e.g. <c>${jwtdecode($clipboard.text)}</c>, <c>${unicodeunescape(state.draft)}</c>, <c>${uuid()}</c>.
    /// 100% local: zero network, zero reflection.
    /// </summary>
    private static bool TryEvaluateUtilityFunctionCall(
        string fnName,
        string parenBlock,
        CustomWidgetEvaluationContext context,
        int depth,
        out string result)
    {
        result = string.Empty;
        if (fnName.Length is < 3 or > 32 ||
            !char.IsLetter(fnName[0]) ||
            !fnName.All(static c => char.IsAsciiLetterOrDigit(c)))
        {
            return false;
        }

        string op = CustomWidgetUtilityEngine.NormalizeTextOp(fnName);
        if (!CustomWidgetUtilityEngine.IsSupportedTextTransform(op) ||
            op is "upper" or "lower" or "trim" or "wordcount")
        {
            // upper/lower/trim/wordcount stay action-only to avoid shadowing plain words.
            return false;
        }

        if (!HasOuterBalancedParentheses(parenBlock))
        {
            return false;
        }

        List<string> rawArgs = SplitTopLevelArguments(parenBlock[1..^1]);
        string inputText = string.Empty;
        if (op.StartsWith("uuid", StringComparison.Ordinal))
        {
            // uuid() / uuid('upper') / uuidnodash('lower'): the only optional arg is the case hint.
            if (rawArgs.Count > 0)
            {
                ReadOnlySpan<char> caseHint = EvaluateExpressionCore(rawArgs[0].Trim(), context, depth + 1).AsSpan().Trim();
                if (caseHint.Equals("upper", StringComparison.OrdinalIgnoreCase))
                {
                    op = op.EndsWith("nodash", StringComparison.Ordinal) ? "uuidnodashupper" : "uuidupper";
                }
            }
        }
        else if (rawArgs.Count > 0)
        {
            string argRaw = rawArgs[0].Trim();
            bool isQuotedLiteral = argRaw.Length >= 2 &&
                ((argRaw[0] == '\'' && argRaw[^1] == '\'') ||
                 (argRaw[0] == '"' && argRaw[^1] == '"'));
            if (isQuotedLiteral)
            {
                // Raw literal: escape sequences (\uXXXX, \n, …) stay verbatim so
                // escape-processing operators such as unicodeUnescape receive them intact.
                inputText = argRaw[1..^1];
            }
            else
            {
                string argEvaluated = EvaluateExpressionCore(argRaw, context, depth + 1);
                if (!string.IsNullOrWhiteSpace(argEvaluated))
                {
                    inputText = argEvaluated;
                }
                else if (!argRaw.StartsWith('$') &&
                         !argRaw.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
                {
                    inputText = argRaw;
                }
            }
        }

        CustomWidgetTransformResult transform = CustomWidgetUtilityEngine.ExecuteTextTransform(
            inputText,
            op,
            context.ClockOverride,
            context.CultureName);
        if (!transform.Succeeded)
        {
            return false;
        }

        result = transform.ResultText;
        return true;
    }

    private static string EvaluateFunctionArgument(string rawArg, CustomWidgetEvaluationContext context, int depth)
    {
        string raw = rawArg.Trim();
        string evaluated = EvaluateExpressionCore(raw, context, depth + 1);
        if (!string.IsNullOrWhiteSpace(evaluated))
        {
            return evaluated;
        }

        if (raw.StartsWith('$') ||
            raw.StartsWith("state.", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith('\'') ||
            raw.StartsWith('"'))
        {
            return string.Empty;
        }

        return raw;
    }

    private static double ParseScrubOffset(string? raw)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return Math.Clamp(value, -24 * 30, 24 * 30);
        }

        return 0;
    }

    private static List<string> SplitTopLevelArguments(string argsText)
    {
        var args = new List<string>(2);
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return args;
        }

        bool inSingle = false;
        bool inDouble = false;
        bool escaped = false;
        int parenDepth = 0;
        int segStart = 0;

        for (int i = 0; i < argsText.Length; i++)
        {
            char c = argsText[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingle || inDouble) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (inSingle || inDouble)
            {
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
            }
            else if (c == ',' && parenDepth == 0)
            {
                args.Add(argsText[segStart..i]);
                segStart = i + 1;
            }
        }

        args.Add(argsText[segStart..]);
        return args;
    }

    public static string FormatPositionalPlaceholders(string template, IReadOnlyList<string> args)
    {
        if (string.IsNullOrEmpty(template) || args.Count == 0 || !template.Contains('{'))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i + 1 && close - i <= 5)
                {
                    ReadOnlySpan<char> digits = template.AsSpan(i + 1, close - i - 1);
                    if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) &&
                        idx >= 0 && idx < args.Count)
                    {
                        sb.Append(args[idx]);
                        i = close + 1;
                        continue;
                    }
                }
            }

            sb.Append(template[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string ResolveSingleExpression(string token, CustomWidgetEvaluationContext context)
    {
        token = token.Trim();
        if (token.Length >= 2 &&
            ((token.StartsWith('\'') && token.EndsWith('\'')) ||
             (token.StartsWith('"') && token.EndsWith('"'))))
        {
            string inner = token[1..^1];
            return inner.Contains('\\') ? UnescapeQuotedLiteral(inner) : inner;
        }

        if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)) return "true";
        if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase)) return "false";
        if (string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return token;
        }

        if (token.StartsWith("$i18n.", StringComparison.Ordinal))
        {
            string key = token["$i18n.".Length..];
            return context.I18n.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("i18n.", StringComparison.Ordinal))
        {
            string key = token["i18n.".Length..];
            return context.I18n.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("state.", StringComparison.Ordinal) ||
            token.StartsWith("$state.", StringComparison.Ordinal))
        {
            string key = token.StartsWith("$state.", StringComparison.Ordinal)
                ? token["$state.".Length..]
                : token["state.".Length..];
            if (context.State.TryGetValue(key, out string? v))
            {
                return v;
            }
            if (context.StateJson.Count > 0)
            {
                return CustomWidgetScriptEngine.ResolveComputedToken(key, context.StateJson);
            }
            return string.Empty;
        }

        if (token.StartsWith("$settings.", StringComparison.Ordinal))
        {
            string key = token["$settings.".Length..];
            return context.Settings.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("settings.", StringComparison.Ordinal))
        {
            string key = token["settings.".Length..];
            return context.Settings.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("$secrets.", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.AllowSecrets)
            {
                return string.Empty;
            }

            string key = token["$secrets.".Length..];
            return context.Secrets.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.AllowSecrets)
            {
                return string.Empty;
            }

            string key = token["secrets.".Length..];
            return context.Secrets.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("$slot.", StringComparison.Ordinal))
        {
            string slotKey = token["$slot.".Length..];
            return string.Equals(slotKey, "paired", StringComparison.OrdinalIgnoreCase)
                ? (context.IsPairedSlot ? "true" : "false")
                : string.Empty;
        }

        if (token.StartsWith("$sys.", StringComparison.Ordinal))
        {
            string path = token["$sys.".Length..];
            if (string.Equals(path, "searchShortcut", StringComparison.OrdinalIgnoreCase))
            {
                return context.SearchShortcutText;
            }
            return context.SystemMetrics.ResolveToken(path, context.Settings, context.CultureName);
        }

        if (token.StartsWith("$weather.", StringComparison.Ordinal))
        {
            string path = token["$weather.".Length..];
            return context.Weather.ResolveToken(path, context.CultureName);
        }

        if (token.StartsWith("$calendar.", StringComparison.Ordinal))
        {
            string path = token["$calendar.".Length..];
            return CustomWidgetCalendarEngine.ResolveToken(
                path,
                context.ClockOverride ?? DateTimeOffset.Now,
                context.State,
                context.CultureName);
        }

        if (token.StartsWith("$device.", StringComparison.Ordinal))
        {
            string path = token["$device.".Length..];
            if (string.Equals(path, "searchShortcut", StringComparison.OrdinalIgnoreCase))
            {
                return context.SearchShortcutText;
            }
            return context.DeviceState.ResolveToken(path, context.CultureName);
        }

        if (token.StartsWith("$clipboard.", StringComparison.Ordinal))
        {
            string path = token["$clipboard.".Length..];
            return context.Clipboard.ResolveToken(path, context.CultureName);
        }

        if (token.StartsWith("$drives.", StringComparison.Ordinal))
        {
            string path = token["$drives.".Length..];
            return context.StorageDrives.ResolveToken(path);
        }

        if (token.StartsWith("$music.", StringComparison.Ordinal))
        {
            string path = token["$music.".Length..];
            return context.Music.ResolveToken(path);
        }

        if (token.StartsWith("$todo.", StringComparison.Ordinal))
        {
            string path = token["$todo.".Length..];
            return context.Todo.ResolveToken(path);
        }

        if (token.StartsWith("$clock.", StringComparison.Ordinal))
        {
            string path = token["$clock.".Length..];
            return CustomWidgetUtilityEngine.ResolveClockToken(
                path,
                context.ClockOverride ?? DateTimeOffset.Now);
        }

        if (token.StartsWith("$pomodoro.", StringComparison.Ordinal))
        {
            string path = token["$pomodoro.".Length..];
            return CustomWidgetPomodoroEngine.ResolveToken(
                path,
                context.GetOrComputePomodoroSnapshot(),
                context.CultureName);
        }

        if (token.StartsWith("$pomo.", StringComparison.Ordinal))
        {
            string path = token["$pomo.".Length..];
            return CustomWidgetPomodoroEngine.ResolveToken(
                path,
                context.GetOrComputePomodoroSnapshot(),
                context.CultureName);
        }

        if (token.StartsWith("$countdown.", StringComparison.Ordinal))
        {
            string path = token["$countdown.".Length..];
            return CustomWidgetCountdownEngine.ResolveToken(
                path,
                context.GetOrComputeCountdownSnapshot(),
                context.CultureName);
        }

        if (token.StartsWith("$ai.", StringComparison.Ordinal))
        {
            string path = token["$ai.".Length..];
            return CustomWidgetAiMonitorEngine.ResolveToken(
                path,
                context.AiMonitor,
                context.State,
                context.Settings,
                context.CultureName);
        }

        if (token.StartsWith("$widget.", StringComparison.Ordinal))
        {
            string prop = token["$widget.".Length..];
            return prop switch
            {
                "id" => context.WidgetId ?? string.Empty,
                _ => string.Empty
            };
        }

        if (token.StartsWith("$time.", StringComparison.Ordinal))
        {
            string key = token["$time.".Length..];
            DateTimeOffset now = (context.ClockOverride ?? DateTimeOffset.Now).ToLocalTime();
            CultureInfo weekdayCulture = CultureInfo.CurrentUICulture;
            if (!string.IsNullOrWhiteSpace(context.CultureName))
            {
                try
                {
                    weekdayCulture = CultureInfo.GetCultureInfo(context.CultureName);
                }
                catch (CultureNotFoundException)
                {
                }
            }

            return key switch
            {
                "now" => now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "time" => now.ToString("HH:mm", CultureInfo.InvariantCulture),
                "date" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "monthDay" => now.ToString("MM-dd", CultureInfo.InvariantCulture),
                "weekday" => now.ToString("dddd", weekdayCulture),
                _ => string.Empty
            };
        }

        if (string.Equals(token, "$value", StringComparison.Ordinal))
        {
            return context.EventValue ?? string.Empty;
        }

        if (token.StartsWith("$event.", StringComparison.Ordinal) ||
            token.StartsWith("event.", StringComparison.Ordinal))
        {
            string eventProp = token.StartsWith('$')
                ? token["$event.".Length..]
                : token["event.".Length..];
            return eventProp switch
            {
                "value" => context.EventValue ?? string.Empty,
                "kind" => context.EventDropKind ?? (context.DropFile is not null ? "files" : string.Empty),
                "text" => context.EventText ?? context.EventValue ?? string.Empty,
                "title" => context.EventDropItems is { Count: > 0 } dItems && !string.IsNullOrEmpty(dItems[0].Title)
                    ? dItems[0].Title
                    : context.DropFile is { Name.Length: > 0 } df
                        ? df.Name
                        : ExtractFirstLineForEventTitle(context.EventText ?? context.EventValue),
                "url" => context.EventDropItems is { Count: > 0 } uItems ? (uItems[0].Url ?? string.Empty) : string.Empty,
                "source" => context.EventDropItems is { Count: > 0 } sItems ? sItems[0].Source : string.Empty,
                "count" or "length" => context.EventDropItems is { Count: > 0 } cItems
                    ? cItems.Count.ToString(CultureInfo.InvariantCulture)
                    : context.DropFiles is { Count: > 0 } dfList
                        ? dfList.Count.ToString(CultureInfo.InvariantCulture)
                        : (context.DropFile is not null ? "1" : "0"),
                "id" => context.EventId ?? (context.EventDropItems is { Count: > 0 } idItems ? idItems[0].Id : string.Empty),
                "x" => context.EventX?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                "y" => context.EventY?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                "normX" => context.EventNormX?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                "normY" => context.EventNormY?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                "isPressed" => context.EventIsPressed is { } p ? (p ? "true" : "false") : string.Empty,
                "fromIndex" => context.EventFromIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                "toIndex" => context.EventToIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                _ => string.Empty
            };
        }

        if (string.Equals(token, "$index", StringComparison.Ordinal))
        {
            return context.CurrentIndex.ToString(CultureInfo.InvariantCulture);
        }

        if (string.Equals(token, "$rank", StringComparison.Ordinal))
        {
            return (context.CurrentIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (token.StartsWith("$item.", StringComparison.Ordinal))
        {
            string key = token["$item.".Length..];
            string? fieldVal = context.CurrentItem?.GetField(key);
            if (!string.IsNullOrEmpty(fieldVal))
            {
                return fieldVal;
            }

            if (string.Equals(key, "rank", StringComparison.Ordinal))
            {
                return (context.CurrentIndex + 1).ToString(CultureInfo.InvariantCulture);
            }

            if (string.Equals(key, "index", StringComparison.Ordinal))
            {
                return context.CurrentIndex.ToString(CultureInfo.InvariantCulture);
            }

            return string.Empty;
        }

        if (token.StartsWith("$drop.", StringComparison.Ordinal))
        {
            string key = token["$drop.".Length..];
            if (string.Equals(key, "count", StringComparison.Ordinal) ||
                string.Equals(key, "length", StringComparison.Ordinal))
            {
                int count = context.DropFiles?.Count ?? (context.DropFile is not null ? 1 : 0);
                return count.ToString(CultureInfo.InvariantCulture);
            }
            return context.DropFile?.Resolve(key) ?? string.Empty;
        }

        if (token.StartsWith("$transform.", StringComparison.Ordinal))
        {
            string key = token["$transform.".Length..];
            return context.TransformResult?.ResolveToken(key) ?? string.Empty;
        }

        if (token.StartsWith("$computed.", StringComparison.Ordinal))
        {
            string path = token["$computed.".Length..];
            return CustomWidgetScriptEngine.ResolveComputedToken(path, context.Computed);
        }

        if (token.StartsWith("computed.", StringComparison.Ordinal))
        {
            string path = token["computed.".Length..];
            return CustomWidgetScriptEngine.ResolveComputedToken(path, context.Computed);
        }

        if (token.StartsWith("collections.", StringComparison.Ordinal))
        {
            return ResolveCollectionToken(token["collections.".Length..], context);
        }

        if (token.StartsWith("$collections.", StringComparison.Ordinal))
        {
            return ResolveCollectionToken(token["$collections.".Length..], context);
        }

        // Bare state fallback
        if (context.State.TryGetValue(token, out string? stateVal))
        {
            return stateVal;
        }
        if (context.StateJson.Count > 0)
        {
            return CustomWidgetScriptEngine.ResolveComputedToken(token, context.StateJson);
        }

        return string.Empty;
    }

    private static string ResolveCollectionToken(string subPath, CustomWidgetEvaluationContext context)
    {
        int dot = subPath.IndexOf('.');
        if (dot <= 0)
        {
            return string.Empty;
        }

        string collectionName = subPath[..dot];
        string remainder = subPath[(dot + 1)..];
        if (!context.Collections.TryGetValue(collectionName, out List<CustomWidgetRecord>? list) ||
            list is null)
        {
            return remainder switch
            {
                "count" or "length" or "todayCount" => "0",
                _ => string.Empty
            };
        }

        if (string.Equals(remainder, "count", StringComparison.Ordinal) ||
            string.Equals(remainder, "length", StringComparison.Ordinal))
        {
            return list.Count.ToString(CultureInfo.InvariantCulture);
        }

        if (string.Equals(remainder, "todayCount", StringComparison.Ordinal))
        {
            int todayCount = list.Count(r => r.IsToday);
            return todayCount.ToString(CultureInfo.InvariantCulture);
        }

        if (remainder.StartsWith("latest.", StringComparison.Ordinal) && list.Count > 0)
        {
            string field = remainder["latest.".Length..];
            CustomWidgetRecord latest = list[0];
            string value = latest.GetField(field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            if (string.Equals(field, "text", StringComparison.Ordinal))
            {
                string fileName = latest.GetField("fileName");
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return $"📎 {fileName}";
                }
            }
            return value;
        }

        return string.Empty;
    }

    private static bool CompareValues(string left, string right, string op)
    {
        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double lNum) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double rNum))
        {
            return op switch
            {
                "==" => Math.Abs(lNum - rNum) < 0.0001,
                "!=" => Math.Abs(lNum - rNum) >= 0.0001,
                ">=" => lNum >= rNum,
                "<=" => lNum <= rNum,
                ">" => lNum > rNum,
                "<" => lNum < rNum,
                _ => false
            };
        }

        int cmp = string.Compare(left, right, StringComparison.Ordinal);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            _ => false
        };
    }

    public static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "0", StringComparison.Ordinal) ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string UnescapeQuotedLiteral(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                sb.Append(next switch
                {
                    '\'' => '\'',
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next
                });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool HasOuterBalancedParentheses(string expr)
    {
        if (expr.Length < 2 || expr[0] != '(' || expr[^1] != ')')
        {
            return false;
        }

        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;
        int depth = 0;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingleQuote || inDoubleQuote) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0 && i < expr.Length - 1)
                {
                    return false;
                }
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0 && !inSingleQuote && !inDoubleQuote;
    }

    private static int FindMatchingBrace(string text, int startIndex)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;
        int braceDepth = 0;
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingleQuote || inDoubleQuote) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (!inSingleQuote && !inDoubleQuote)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}')
                {
                    if (braceDepth == 0)
                    {
                        return i;
                    }
                    braceDepth--;
                }
            }
        }
        return -1;
    }

    private static int FindMatchingTernaryColon(string expr, int startIndex)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;
        int parenDepth = 0;
        int nestedTernaryDepth = 0;

        for (int i = startIndex; i < expr.Length; i++)
        {
            char c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingleQuote || inDoubleQuote) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                continue;
            }

            if (c == ')')
            {
                if (parenDepth > 0)
                {
                    parenDepth--;
                }
                continue;
            }

            if (parenDepth > 0)
            {
                continue;
            }

            if (c == '?' &&
                !(i + 1 < expr.Length && expr[i + 1] == '?') &&
                !(i > 0 && expr[i - 1] == '?'))
            {
                nestedTernaryDepth++;
                continue;
            }

            if (c == ':')
            {
                if (nestedTernaryDepth == 0)
                {
                    return i;
                }
                nestedTernaryDepth--;
            }
        }

        return -1;
    }

    private static int FindTopLevelOperator(string expr, string op)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;
        int parenDepth = 0;
        for (int i = 0; i <= expr.Length - op.Length; i++)
        {
            char c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingleQuote || inDoubleQuote) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                continue;
            }
            if (c == ')')
            {
                if (parenDepth > 0)
                {
                    parenDepth--;
                }
                continue;
            }
            if (parenDepth > 0)
            {
                continue;
            }

            if (expr.AsSpan(i, op.Length).SequenceEqual(op.AsSpan()))
            {
                // Avoid matching single '?' inside '??' or '>' inside '>='
                if (op == "?" &&
                    ((i + 1 < expr.Length && expr[i + 1] == '?') ||
                     (i > 0 && expr[i - 1] == '?')))
                {
                    continue;
                }
                if ((op == ">" || op == "<") &&
                    i + 1 < expr.Length && expr[i + 1] == '=')
                {
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static bool IsDirectHyphenatedIdentifier(string expr, CustomWidgetEvaluationContext context)
    {
        if (!expr.Contains('-') || expr.Contains('+') || expr.Contains(' ') || expr.Contains('\t'))
        {
            return false;
        }

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (!(char.IsLetterOrDigit(c) || c is '$' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        // If the unsplit token directly resolves to a known key (e.g. state.my-key or $i18n.btn-save),
        // preserve it rather than splitting on '-' as subtraction.
        if (expr.StartsWith("state.", StringComparison.Ordinal))
        {
            return context.State.ContainsKey(expr["state.".Length..]);
        }
        if (expr.StartsWith("$i18n.", StringComparison.Ordinal))
        {
            return context.I18n.ContainsKey(expr["$i18n.".Length..]);
        }
        if (expr.StartsWith("i18n.", StringComparison.Ordinal))
        {
            return context.I18n.ContainsKey(expr["i18n.".Length..]);
        }
        if (expr.StartsWith("$settings.", StringComparison.Ordinal))
        {
            return context.Settings.ContainsKey(expr["$settings.".Length..]);
        }
        if (expr.StartsWith("settings.", StringComparison.Ordinal))
        {
            return context.Settings.ContainsKey(expr["settings.".Length..]);
        }
        if (expr.StartsWith("$item.", StringComparison.Ordinal))
        {
            return context.CurrentItem?.Fields.ContainsKey(expr["$item.".Length..]) == true;
        }

        return context.State.ContainsKey(expr);
    }

    private static bool TryEvaluateTopLevelArithmetic(
        string expr,
        CustomWidgetEvaluationContext context,
        ReadOnlySpan<char> operators,
        int depth,
        out string formattedResult)
    {
        formattedResult = string.Empty;
        if (depth > MaxEvaluationDepth)
        {
            return false;
        }

        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;
        int parenDepth = 0;
        List<int>? candidateIndices = null;

        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingleQuote || inDoubleQuote) && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }
            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (c == '(')
            {
                parenDepth++;
                continue;
            }
            if (c == ')')
            {
                if (parenDepth > 0)
                {
                    parenDepth--;
                }
                continue;
            }
            if (parenDepth > 0 || i == 0 || i == expr.Length - 1)
            {
                continue;
            }

            if (!operators.Contains(c))
            {
                continue;
            }

            // Skip scientific notation exponent signs such as 1e-5 or 2.5E+3
            if ((c is '+' or '-') &&
                (expr[i - 1] is 'e' or 'E') &&
                i >= 2 &&
                (char.IsDigit(expr[i - 2]) || expr[i - 2] == '.'))
            {
                continue;
            }

            // Skip unary '+' / '-' immediately following another operator or opening delimiter
            if (c is '+' or '-')
            {
                int prev = i - 1;
                while (prev >= 0 && char.IsWhiteSpace(expr[prev]))
                {
                    prev--;
                }

                if (prev < 0 ||
                    expr[prev] is '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '&' or '|' or '?' or ':' or '(')
                {
                    continue;
                }
            }

            (candidateIndices ??= []).Add(i);
        }

        if (candidateIndices is null || candidateIndices.Count == 0)
        {
            return false;
        }

        // Evaluate right-to-left so left-associative chains (e.g. 10 - 3 - 2 = 5, 20 / 5 / 2 = 2)
        // group as ((a op b) op c).
        for (int idx = candidateIndices.Count - 1; idx >= 0; idx--)
        {
            int opPos = candidateIndices[idx];
            char op = expr[opPos];
            string leftRaw = expr[..opPos].Trim();
            string rightRaw = expr[(opPos + 1)..].Trim();
            if (leftRaw.Length == 0 || rightRaw.Length == 0)
            {
                continue;
            }

            string leftEval = EvaluateExpressionCore(leftRaw, context, depth);
            string rightEval = EvaluateExpressionCore(rightRaw, context, depth);
            if (!double.TryParse(leftEval, NumberStyles.Float, CultureInfo.InvariantCulture, out double lNum) ||
                !double.TryParse(rightEval, NumberStyles.Float, CultureInfo.InvariantCulture, out double rNum))
            {
                continue;
            }

            double computed = op switch
            {
                '+' => lNum + rNum,
                '-' => lNum - rNum,
                '*' => lNum * rNum,
                '/' => Math.Abs(rNum) < 1e-12 ? 0.0 : lNum / rNum,
                '%' => Math.Abs(rNum) < 1e-12 ? 0.0 : lNum % rNum,
                _ => 0.0
            };

            formattedResult = FormatNumericResult(computed);
            return true;
        }

        return false;
    }

    private static string FormatNumericResult(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        double rounded = Math.Round(value, 10, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) < 1e-12)
        {
            return "0";
        }

        if (Math.Abs(rounded - Math.Truncate(rounded)) < 1e-9 && Math.Abs(rounded) < 1e15)
        {
            return ((long)Math.Round(rounded, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        }

        return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    public static bool TryResolveVirtualCollection(
        string? rawBinding,
        CustomWidgetEvaluationContext ctx,
        out IReadOnlyList<CustomWidgetRecord> records)
    {
        records = [];
        if (string.IsNullOrWhiteSpace(rawBinding))
        {
            return false;
        }

        string trimmed = rawBinding.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        if (string.Equals(trimmed, "$sys.topProcesses", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "$sys.processes", StringComparison.OrdinalIgnoreCase))
        {
            string sortMode = ctx.State.TryGetValue("processSort", out string? stSort) && !string.IsNullOrWhiteSpace(stSort)
                ? stSort.Trim()
                : (ctx.Settings.TryGetValue("defaultProcessSort", out string? defSort) && !string.IsNullOrWhiteSpace(defSort)
                    ? defSort.Trim()
                    : "memory");
            bool isCpu = string.Equals(sortMode, "cpu", StringComparison.OrdinalIgnoreCase);

            int maxCount = 5;
            if (ctx.Settings.TryGetValue("topProcessCount", out string? cntRaw) &&
                int.TryParse(cntRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCnt))
            {
                maxCount = Math.Clamp(parsedCnt, 1, 15);
            }

            IReadOnlyList<CustomWidgetProcessEntry> source = isCpu
                ? ctx.SystemMetrics.TopProcessesByCpu
                : ctx.SystemMetrics.TopProcessesByMemory;

            double totalMemBytes = ctx.SystemMetrics.MemoryTotalGb * 1024.0 * 1024.0 * 1024.0;
            var list = new List<CustomWidgetRecord>(Math.Min(maxCount, source.Count));
            for (int i = 0; i < source.Count && i < maxCount; i++)
            {
                CustomWidgetProcessEntry p = source[i];
                double barPct = isCpu ? p.CpuBarPercent : p.MemoryBarPercent;
                string barPctText = barPct.ToString("0.0", CultureInfo.InvariantCulture);
                double realUsagePct = isCpu
                    ? Math.Clamp(p.CpuPercent, 0.0, 100.0)
                    : (totalMemBytes > 0 ? Math.Clamp(p.MemoryBytes * 100.0 / totalMemBytes, 0.0, 100.0) : 0.0);
                string realUsagePctText = realUsagePct.ToString("0.0", CultureInfo.InvariantCulture);
                string valTxt = isCpu ? p.CpuText : p.MemoryText;
                string displayName = p.GetLocalizedName(ctx.CultureName);
                string instSuffix = p.InstanceCount > 1 ? $"×{p.InstanceCount}" : string.Empty;
                list.Add(CustomWidgetRecord.CreateProjected(
                    !string.IsNullOrEmpty(p.GroupKey) ? p.GroupKey : $"{p.Name}#{i}",
                    ("title", displayName),
                    ("name", displayName),
                    ("subtitle", instSuffix),
                    ("value", barPctText),
                    ("barPercent", barPctText),
                    ("percent", realUsagePctText),
                    ("valueText", valTxt),
                    ("cpuText", p.CpuText),
                    ("memoryText", p.MemoryText),
                    ("instances", p.InstanceCount.ToString(CultureInfo.InvariantCulture))));
            }

            records = list;
            return true;
        }

        if (string.Equals(trimmed, "$ai.providers", StringComparison.OrdinalIgnoreCase))
        {
            bool showRem = CustomWidgetAiMonitorEngine.IsRemainingMode(ctx.Settings);
            CustomWidgetAiProviderSnapshot? focus = CustomWidgetAiMonitorEngine.SelectFocusProvider(ctx.AiMonitor, ctx.State);
            DateTimeOffset evalNow = ctx.AiMonitor.UpdatedAt > DateTimeOffset.MinValue
                ? ctx.AiMonitor.UpdatedAt
                : DateTimeOffset.Now;
            var list = new List<CustomWidgetRecord>(ctx.AiMonitor.ActiveProviders.Count);
            foreach (CustomWidgetAiProviderSnapshot p in ctx.AiMonitor.ActiveProviders)
            {
                var (pct, effHealth, tone, statusIcon) = CustomWidgetAiMonitorEngine.EvaluateEffectiveProviderMetrics(
                    p,
                    ctx.Settings,
                    ctx.CultureName);
                string ledgerSummary = CustomWidgetAiMonitorEngine.BuildProviderLedgerSummary(
                    p,
                    ctx.Settings,
                    evalNow,
                    ctx.CultureName);
                string badgeText = CustomWidgetAiMonitorEngine.ResolveProviderHealthBadgeLabel(
                    p,
                    effHealth,
                    ctx.CultureName);
                string pctText = pct.ToString("0.0", CultureInfo.InvariantCulture);
                string valueText = p.BillingKind == AiProviderBillingKind.WindowQuota &&
                                   p.HasLiveData &&
                                   p.PrimaryWindow is { } pw
                    ? CustomWidgetAiMonitorEngine.FormatWindowPercentText(pw, showRem, out _, ctx.CultureName)
                    : p.RadarValueText;
                string subtitle = p.ErrorMessage ?? p.RadarSubtext;

                string metaLine = subtitle;
                if (!string.IsNullOrWhiteSpace(p.PlanBadge))
                {
                    if (string.IsNullOrWhiteSpace(subtitle) ||
                        string.Equals(subtitle, $"GLM {p.PlanBadge}", StringComparison.OrdinalIgnoreCase))
                    {
                        metaLine = p.PlanBadge;
                    }
                    else if (!subtitle.Contains(p.PlanBadge, StringComparison.OrdinalIgnoreCase))
                    {
                        metaLine = $"{p.PlanBadge} · {subtitle}";
                    }
                }

                bool hasError = !p.HasLiveData || !string.IsNullOrWhiteSpace(p.ErrorMessage);
                bool isWindowQuota = p.BillingKind == AiProviderBillingKind.WindowQuota;
                bool hasSecondaryBar = isWindowQuota && p.SecondaryWindow is not null;

                string detailLabel = string.Empty;
                string detailSubtext = string.Empty;
                string detailValue = string.Empty;
                string detailPercent = "0";
                string detailTone = tone;
                bool hasDetail = false;

                if (hasError && !string.IsNullOrWhiteSpace(p.ErrorMessage))
                {
                    hasDetail = true;
                    detailSubtext = p.ErrorMessage;
                    detailTone = "warning";
                }
                else if (isWindowQuota && p.SecondaryWindow is { } sw)
                {
                    hasDetail = true;
                    detailLabel = sw.Label;
                    detailSubtext = sw.ResetText;
                    detailValue = CustomWidgetAiMonitorEngine.FormatWindowPercentText(sw, showRem, out _, ctx.CultureName);
                    double secPct = showRem ? sw.RemainingPercent : sw.UsedPercent;
                    detailPercent = secPct.ToString("0.0", CultureInfo.InvariantCulture);
                    detailTone = sw.Health switch
                    {
                        AiQuotaHealth.Critical => "danger",
                        AiQuotaHealth.Warning => "warning",
                        _ => "emerald"
                    };
                }
                else if (!isWindowQuota && !string.IsNullOrWhiteSpace(p.WalletBreakdown))
                {
                    hasDetail = true;
                    detailSubtext = p.WalletBreakdown;
                    detailValue = FormattableString.Invariant($"{Math.Round(pct):0}%");
                }

                bool isSelected = focus is not null && string.Equals(p.Id, focus.Id, StringComparison.OrdinalIgnoreCase);
                list.Add(CustomWidgetRecord.CreateProjected(
                    p.Id,
                    ("id", p.Id),
                    ("title", p.Name),
                    ("name", p.Name),
                    ("shortName", p.ShortName),
                    ("monogram", p.Monogram),
                    ("color", p.BrandHex),
                    ("badge", p.PlanBadge),
                    ("badgeText", badgeText),
                    ("ledgerSummary", ledgerSummary),
                    ("metaLine", metaLine),
                    ("value", pctText),
                    ("percent", pctText),
                    ("valueText", valueText),
                    ("subtitle", subtitle),
                    ("subtext", subtitle),
                    ("tone", tone),
                    ("statusIcon", statusIcon),
                    ("hasError", hasError ? "true" : "false"),
                    ("isWindowQuota", isWindowQuota ? "true" : "false"),
                    ("hasDetail", hasDetail ? "true" : "false"),
                    ("hasSecondaryBar", hasSecondaryBar ? "true" : "false"),
                    ("detailLabel", detailLabel),
                    ("detailSubtext", detailSubtext),
                    ("detailValue", detailValue),
                    ("detailPercent", detailPercent),
                    ("detailTone", detailTone),
                    ("error", p.ErrorMessage ?? string.Empty),
                    ("isSelected", isSelected ? "true" : "false")));
            }

            records = list;
            return true;
        }

        if (string.Equals(trimmed, "$ai.dailyTrend", StringComparison.OrdinalIgnoreCase))
        {
            bool isCost = ctx.State.TryGetValue(CustomWidgetAiMonitorEngine.KeyTrendMetric, out string? metric) &&
                          string.Equals(metric, "cost", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<CustomWidgetAiDailyBucket> all28 = ctx.AiMonitor.LocalSummary.EffectiveLast28Days;
            int startIdx = Math.Max(0, all28.Count - 14);
            string dayMonthFormat = ResolveShortDayMonthFormat(ctx.CultureName);
            var list = new List<CustomWidgetRecord>(all28.Count - startIdx);
            for (int i = startIdx; i < all28.Count; i++)
            {
                CustomWidgetAiDailyBucket b = all28[i];
                string dateIso = b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string dateLabel = b.Date.ToString(dayMonthFormat, CultureInfo.InvariantCulture);
                string tokensK = (b.TotalTokens / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
                string costText = b.CostUsd.ToString("0.00", CultureInfo.InvariantCulture);
                string numericVal = isCost
                    ? b.CostUsd.ToString("0.000", CultureInfo.InvariantCulture)
                    : tokensK;
                list.Add(CustomWidgetRecord.CreateProjected(
                    dateIso,
                    ("date", dateIso),
                    ("dateLabel", dateLabel),
                    ("day", dateLabel),
                    ("title", dateLabel),
                    ("tokens", b.TotalTokens.ToString(CultureInfo.InvariantCulture)),
                    ("tokensK", tokensK),
                    ("tokensFormatted", CustomWidgetAiMonitorEngine.FormatCompactTokens(b.TotalTokens)),
                    ("cost", costText),
                    ("costUsd", costText),
                    ("value", numericVal)));
            }

            records = list;
            return true;
        }

        if (string.Equals(trimmed, "$ai.daily28", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<CustomWidgetAiDailyBucket> buckets = ctx.AiMonitor.LocalSummary.EffectiveLast28Days;
            if (buckets.Count == 0)
            {
                DateOnly todayFallback = DateOnly.FromDateTime(DateTime.Now);
                var empty28 = new CustomWidgetAiDailyBucket[28];
                for (int i = 0; i < 28; i++)
                {
                    empty28[i] = new CustomWidgetAiDailyBucket(todayFallback.AddDays(-(27 - i)), 0, 0);
                }
                buckets = empty28;
            }
            string dayMonthFormat = ResolveShortDayMonthFormat(ctx.CultureName);
            int activeCount = 0;
            long sumActiveTokens = 0;
            double sumActiveCost = 0;
            for (int i = 0; i < buckets.Count; i++)
            {
                CustomWidgetAiDailyBucket b = buckets[i];
                if (b.TotalTokens > 0 || b.CostUsd > 0.0005)
                {
                    activeCount++;
                    sumActiveTokens += b.TotalTokens;
                    sumActiveCost += b.CostUsd;
                }
            }

            double meanTokens = activeCount > 0 ? (double)sumActiveTokens / activeCount : 0;
            double meanCost = activeCount > 0 ? sumActiveCost / activeCount : 0;
            string idleLabel = LocalizationService.TranslateForCulture(ctx.CultureName, "CustomWidget.AiMonitor.DayIdle");

            var list = new List<CustomWidgetRecord>(buckets.Count);
            foreach (CustomWidgetAiDailyBucket b in buckets)
            {
                string dateIso = b.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string dateLabel = b.Date.ToString(dayMonthFormat, CultureInfo.InvariantCulture);
                string tone = CustomWidgetAiMonitorEngine.ClassifyDailyBucketTone(b, meanTokens, meanCost);
                string tokensFmt = CustomWidgetAiMonitorEngine.FormatCompactTokens(b.TotalTokens);
                string costFmt = FormattableString.Invariant($"${b.CostUsd:0.00}");

                string summary;
                if (b.TotalTokens > 0)
                {
                    summary = $"{dateLabel} · {tokensFmt} tokens ({costFmt})";
                }
                else if (b.CostUsd > 0.0005)
                {
                    summary = $"{dateLabel} · {costFmt}";
                }
                else
                {
                    summary = $"{dateLabel} · {idleLabel}";
                }

                list.Add(CustomWidgetRecord.CreateProjected(
                    dateIso,
                    ("date", dateIso),
                    ("dateLabel", dateLabel),
                    ("title", dateLabel),
                    ("tone", tone),
                    ("tokens", b.TotalTokens.ToString(CultureInfo.InvariantCulture)),
                    ("tokensFormatted", tokensFmt),
                    ("cost", b.CostUsd.ToString("0.00", CultureInfo.InvariantCulture)),
                    ("summary", summary),
                    ("tooltip", summary),
                    ("value", b.TotalTokens.ToString(CultureInfo.InvariantCulture))));
            }

            records = list;
            return true;
        }

        if (string.Equals(trimmed, "$countdown.items", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "$countdown.secondaryItems", StringComparison.OrdinalIgnoreCase))
        {
            CustomWidgetCountdownSnapshot snap = ctx.GetOrComputeCountdownSnapshot();
            IReadOnlyList<CustomWidgetCountdownEventView> source = string.Equals(trimmed, "$countdown.secondaryItems", StringComparison.OrdinalIgnoreCase)
                ? snap.SecondaryEvents
                : snap.AllEvents;
            var list = new List<CustomWidgetRecord>(source.Count);
            foreach (CustomWidgetCountdownEventView ev in source)
            {
                string tone = ev.IsUrgent
                    ? "danger"
                    : ev.IsApproaching
                        ? "warning"
                        : ev.Direction == CountdownDirection.CountUp
                            ? "success"
                            : "accent";
                string rawDays = ev.DisplayDays.ToString(CultureInfo.InvariantCulture);
                string daysDisplay = ev.Direction == CountdownDirection.Today
                    ? ev.StatusWord
                    : rawDays;
                string progressText = ev.ProgressPercent.ToString("0.0", CultureInfo.InvariantCulture);
                list.Add(CustomWidgetRecord.CreateProjected(
                    ev.Id,
                    ("id", ev.Id),
                    ("title", ev.Title),
                    ("emoji", ev.Emoji),
                    ("days", rawDays),
                    ("daysText", daysDisplay),
                    ("deltaText", ev.DeltaText),
                    ("statusWord", ev.StatusWord),
                    ("subtitle", ev.ListSubtitle),
                    ("heroSubtitle", ev.HeroSubtitle),
                    ("badge", ev.BadgeText),
                    ("milestone", ev.MilestoneText),
                    ("value", progressText),
                    ("percent", progressText),
                    ("tone", tone),
                    ("isPinned", ev.IsPinned ? "true" : "false"),
                    ("isUrgent", ev.IsUrgent ? "true" : "false"),
                    ("isToday", ev.Direction == CountdownDirection.Today ? "true" : "false"),
                    ("isCountUp", ev.Direction == CountdownDirection.CountUp ? "true" : "false"),
                    ("rawDate", ev.RawDate),
                    ("kind", ev.Kind),
                    ("category", ev.Category),
                    ("calendar", ev.CalendarType),
                    ("repeat", ev.RepeatMode),
                    ("recurrence", ev.RepeatMode),
                    ("weekendRule", ev.WeekendRule),
                    ("isLunar", string.Equals(ev.CalendarType, CustomWidgetCountdownEngine.CalendarLunar, StringComparison.OrdinalIgnoreCase) ? "true" : "false"),
                    ("isLunarLeap", ev.IsLunarLeap ? "true" : "false"),
                    ("lunarYear", ev.LunarYear),
                    ("lunarMonth", ev.LunarMonth),
                    ("lunarDay", ev.LunarDay)));
            }

            records = list;
            return true;
        }

        if (string.Equals(trimmed, "$countdown.progressBars", StringComparison.OrdinalIgnoreCase))
        {
            CustomWidgetCountdownSnapshot snap = ctx.GetOrComputeCountdownSnapshot();
            ReadOnlySpan<char> scope = ctx.Settings.TryGetValue("progressScope", out string? rawScope) && !string.IsNullOrWhiteSpace(rawScope)
                ? rawScope.AsSpan().Trim()
                : "year_month";
            var list = new List<CustomWidgetRecord>(4);

            void AddBar(string id, string title, string summary, double pct, string color)
            {
                string pctText = pct.ToString("0.0", CultureInfo.InvariantCulture);
                list.Add(CustomWidgetRecord.CreateProjected(
                    id,
                    ("id", id),
                    ("title", title),
                    ("subtitle", summary),
                    ("value", pctText),
                    ("percent", pctText),
                    ("valueText", summary),
                    ("color", color)));
            }

            if (!scope.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                bool isAll = scope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                             scope.Equals("allfour", StringComparison.OrdinalIgnoreCase);
                bool isBothYearMonth = scope.Equals("both", StringComparison.OrdinalIgnoreCase) ||
                                       scope.Equals("year_month", StringComparison.OrdinalIgnoreCase);
                bool showWork = isAll || scope.Equals("work_only", StringComparison.OrdinalIgnoreCase) || scope.Contains("work", StringComparison.OrdinalIgnoreCase);
                bool showWeek = isAll || scope.Equals("week_only", StringComparison.OrdinalIgnoreCase) || scope.Contains("week", StringComparison.OrdinalIgnoreCase);
                bool showMonth = isAll || isBothYearMonth || scope.Equals("month_only", StringComparison.OrdinalIgnoreCase) || scope.Contains("month", StringComparison.OrdinalIgnoreCase);
                bool showYear = isAll || isBothYearMonth || scope.Equals("year_only", StringComparison.OrdinalIgnoreCase) || scope.Contains("year", StringComparison.OrdinalIgnoreCase);

                if (showWork)
                {
                    AddBar("work", snap.Workday.Title, snap.Workday.Summary, snap.Workday.Percent, "#F59E0B");
                }
                if (showWeek)
                {
                    AddBar("week", snap.TimeProgress.WeekTitle, snap.TimeProgress.WeekSummary, snap.TimeProgress.WeekPercent, "#10B981");
                }
                if (showMonth)
                {
                    AddBar("month", snap.TimeProgress.MonthTitle, snap.TimeProgress.MonthSummary, snap.TimeProgress.MonthPercent, "#8B5CF6");
                }
                if (showYear)
                {
                    AddBar("year", snap.TimeProgress.YearTitle, snap.TimeProgress.YearSummary, snap.TimeProgress.YearPercent, "#3B82F6");
                }
            }

            records = list;
            return true;
        }

        return false;
    }

    private static string ResolveShortDayMonthFormat(string? cultureName)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
            }
        }

        string shortPattern = culture.DateTimeFormat.ShortDatePattern;
        int dIdx = shortPattern.IndexOf('d');
        int mIdx = shortPattern.IndexOf('M');
        if (dIdx >= 0 && mIdx >= 0 && dIdx < mIdx)
        {
            return shortPattern.Contains('.', StringComparison.Ordinal) ? "dd.MM" : "dd/MM";
        }

        return "MM/dd";
    }

    private static string ExtractFirstLineForEventTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        int newline = span.IndexOfAny('\r', '\n');
        ReadOnlySpan<char> firstLine = newline >= 0 ? span[..newline].Trim() : span;
        return firstLine.Length > 120 ? string.Concat(firstLine[..120], "…") : firstLine.ToString();
    }
}
