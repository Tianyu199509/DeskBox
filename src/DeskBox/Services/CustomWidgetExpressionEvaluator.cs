using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services;

public sealed class CustomWidgetDropFileContext
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string Mode { get; init; } = "link";
    public string Text { get; init; } = string.Empty;

    public string Resolve(string field) => field switch
    {
        "name" or "fileName" => Name,
        "path" or "filePath" => Path,
        "ext" or "extension" => Extension,
        "sizeText" => SizeText,
        "mode" or "storageMode" => Mode,
        "text" or "content" => Text,
        _ => string.Empty
    };
}

public sealed class CustomWidgetEvaluationContext
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
    public string? EventValue { get; init; }
    public string? EventId { get; init; }
    public double? EventX { get; init; }
    public double? EventY { get; init; }
    public int? EventFromIndex { get; init; }
    public int? EventToIndex { get; init; }
    public DateTimeOffset? ClockOverride { get; init; }
    public string? CultureName { get; init; }

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
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = item,
            CurrentIndex = index,
            DropFile = DropFile,
            EventValue = EventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithDrop(CustomWidgetDropFileContext drop) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = drop,
            EventValue = EventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithClipboard(CustomWidgetClipboardSnapshot clipboard) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = DropFile,
            EventValue = EventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithTransform(CustomWidgetTransformResult transform) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = transform,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = DropFile,
            EventValue = EventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithEventValue(string? eventValue) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = DropFile,
            EventValue = eventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithPointerEvent(string? id, double x, double y) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = DropFile,
            EventValue = id ?? EventValue,
            EventId = id,
            EventX = x,
            EventY = y,
            EventFromIndex = EventFromIndex,
            EventToIndex = EventToIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
        };

    public CustomWidgetEvaluationContext WithReorderEvent(int fromIndex, int toIndex) =>
        new()
        {
            _snapshotCache = _snapshotCache ??= new SnapshotCache(),
            I18n = I18n,
            Settings = Settings,
            State = State,
            Computed = Computed,
            Collections = Collections,
            SystemMetrics = SystemMetrics,
            AiMonitor = AiMonitor,
            Weather = Weather,
            DeviceState = DeviceState,
            Clipboard = Clipboard,
            StorageDrives = StorageDrives,
            Music = Music,
            Todo = Todo,
            Secrets = Secrets,
            TransformResult = TransformResult,
            CurrentItem = CurrentItem,
            CurrentIndex = CurrentIndex,
            DropFile = DropFile,
            EventValue = EventValue,
            EventId = EventId,
            EventX = EventX,
            EventY = EventY,
            EventFromIndex = fromIndex,
            EventToIndex = toIndex,
            ClockOverride = ClockOverride,
            CultureName = CultureName
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

            string expr = template.Substring(open + 2, close - (open + 2)).Trim();
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
            int dot = path.IndexOf('.');
            string rootKey = dot < 0 ? path : path[..dot];
            if (context.Computed.TryGetValue(rootKey, out JsonElement cur))
            {
                if (dot >= 0)
                {
                    foreach (string seg in path[(dot + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(seg, out JsonElement child))
                        {
                            cur = child;
                        }
                        else
                        {
                            cur = default;
                            break;
                        }
                    }
                }

                if (cur.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<double>();
                    foreach (JsonElement el in cur.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
                        {
                            points.Add(d);
                        }
                        else if (el.ValueKind == JsonValueKind.String &&
                                 double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sd) &&
                                 double.IsFinite(sd))
                        {
                            points.Add(sd);
                        }
                    }

                    return points;
                }
            }
        }

        return context.SystemMetrics.ResolveHistory(sparklineBinding);
    }

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
                string truePart = expr.Substring(questionIdx + 1, actualColon - (questionIdx + 1)).Trim();
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

        // 3. Logical OR: left || right
        int orIdx = FindTopLevelOperator(expr, "||");
        if (orIdx > 0)
        {
            bool left = IsTruthy(EvaluateExpressionCore(expr[..orIdx], context, depth + 1));
            if (left) return "true";
            bool right = IsTruthy(EvaluateExpressionCore(expr[(orIdx + 2)..], context, depth + 1));
            return right ? "true" : "false";
        }

        // 4. Logical AND: left && right
        int andIdx = FindTopLevelOperator(expr, "&&");
        if (andIdx > 0)
        {
            bool left = IsTruthy(EvaluateExpressionCore(expr[..andIdx], context, depth + 1));
            if (!left) return "false";
            bool right = IsTruthy(EvaluateExpressionCore(expr[(andIdx + 2)..], context, depth + 1));
            return right ? "true" : "false";
        }

        // 5. Comparisons: ==, !=, >=, <=, >, <
        foreach (string op in new[] { "==", "!=", ">=", "<=", ">", "<" })
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

        if (TryEvaluateFunctionCall(expr, context, depth, out string fnResult))
        {
            return fnResult;
        }

        return ResolveSingleExpression(expr, context);
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
        bool isTzWorkState = string.Equals(fnName, "tzWorkState", StringComparison.OrdinalIgnoreCase);
        bool isTzCity = string.Equals(fnName, "tzCity", StringComparison.OrdinalIgnoreCase);
        if (!isTz && !isTzDiffLabel && !isTzWorkState && !isTzCity)
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

        if (isTzDiffLabel)
        {
            double diffOffset = rawArgs.Count >= 2
                ? ParseScrubOffset(EvaluateFunctionArgument(rawArgs[1], context, depth))
                : 0;
            result = CustomWidgetUtilityEngine.ResolveTimeZoneDiffLabel(zone, diffOffset, now, context.CultureName);
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
                string caseHint = EvaluateExpressionCore(rawArgs[0].Trim(), context, depth + 1)
                    .Trim().ToLowerInvariant();
                if (caseHint == "upper")
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

        if (token.StartsWith("state.", StringComparison.Ordinal))
        {
            string key = token["state.".Length..];
            return context.State.TryGetValue(key, out string? v) ? v : string.Empty;
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

        if (token.StartsWith("$secrets.", StringComparison.Ordinal))
        {
            string key = token["$secrets.".Length..];
            return context.Secrets.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("secrets.", StringComparison.Ordinal))
        {
            string key = token["secrets.".Length..];
            return context.Secrets.TryGetValue(key, out string? v) ? v : string.Empty;
        }

        if (token.StartsWith("$sys.", StringComparison.Ordinal))
        {
            string path = token["$sys.".Length..];
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

        if (token.StartsWith("$event.", StringComparison.Ordinal))
        {
            string eventProp = token["$event.".Length..];
            return eventProp switch
            {
                "value" => context.EventValue ?? string.Empty,
                "id" => context.EventId ?? string.Empty,
                "x" => context.EventX?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                "y" => context.EventY?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
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
                "count" or "todayCount" => "0",
                _ => string.Empty
            };
        }

        if (string.Equals(remainder, "count", StringComparison.Ordinal))
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
}
