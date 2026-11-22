using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record AiToolCallSpec(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record AiChatTurnMessage(
    string Role,
    string? Content = null,
    string? ReasoningContent = null,
    IReadOnlyList<AiToolCallSpec>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null);

public sealed record StudioFinishTurnSpec(
    string Kind,
    string Reply,
    string ChangeSummary,
    bool DeleteScript,
    string? SampleDataJson,
    IReadOnlyList<string> Suggestions,
    StudioBriefUpdateSpec? BriefUpdates,
    IReadOnlyList<StudioClarifyQuestionSpec>? ClarifyQuestions = null,
    string? LimitationNote = null);

public sealed record StudioAgenticActionRequest(
    string Action,
    IReadOnlyList<string> CapabilityIds,
    string? Query,
    string? Url,
    string Method,
    IReadOnlyDictionary<string, string>? Headers,
    string? Body);

/// <summary>
/// $O(\Delta)$ zero-LOH streaming accumulator for SSE thinking, content, and OpenAI delta.tool_calls.
/// Ensures UI telemetry slices remain strictly bounded (&lt;= 3,000 thinking tail / &lt;= 4,400 visible tail)
/// even when total streamed text exceeds 120 KB, and latches widget name once within the first 3,072 chars.
/// </summary>
public sealed partial class StreamingTurnAccumulator
{
    public const int MaxThinkingTailChars = 3_000;
    public const int MaxVisibleTailChars = 4_400;
    public const int NameLatchScanWindowChars = 3_072;
    public const int MaxTrackedStreamingToolCalls = 24;
    private const int ReservedFinishTurnSlot = int.MaxValue - 1;

    private static readonly string[] OpenThinkTags = ["<think>", "<thinking>", "<thought>"];
    private static readonly string[] CloseThinkTags = ["</think>", "</thinking>", "</thought>"];

    [GeneratedRegex("\"name\"\\s*:\\s*\"(?<name>(?:\\\\.|[^\"\\\\]){1,60})\"", RegexOptions.CultureInvariant)]
    private static partial Regex StreamingJsonNameRegex();

    [GeneratedRegex("\\\\\"name\\\\\"\\s*:\\s*\\\\\"(?<name>(?:\\\\\\\\.|[^\\\\\"]){1,60})\\\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedJsonNameRegex();

    [GeneratedRegex("\"reply\"\\s*:\\s*\"(?<reply>(?:\\\\.|[^\"\\\\]){4,120})", RegexOptions.CultureInvariant)]
    private static partial Regex StreamingReplyPreviewRegex();

    [GeneratedRegex("\"type\"\\s*:\\s*\"(?<type>[A-Z][A-Za-z]{2,22})\"", RegexOptions.CultureInvariant)]
    private static partial Regex StreamingNodeTypeRegex();

    [GeneratedRegex("export\\s+function\\s+(?<fn>[A-Za-z_][A-Za-z0-9_]{1,36})", RegexOptions.CultureInvariant)]
    private static partial Regex StreamingExportFunctionRegex();

    private sealed class ToolCallDraft
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new(256);
    }

    private readonly StringBuilder _reasoningBuilder = new(1024);
    private readonly StringBuilder _inlineThinkBuilder = new(1024);
    private readonly StringBuilder _visibleBuilder = new(2048);
    private readonly SortedDictionary<int, ToolCallDraft> _toolCalls = new();

    private readonly char[] _tagBuf = new char[16];
    private int _tagLen;
    private bool _insideInlineThink;
    private string _activeCloseTag = "</think>";

    private string? _latchedWidgetName;
    private bool _visibleNameLatchDone;
    private bool _toolArgNameLatchDone;
    private long _thinkingTokenMilliUnits;
    private long _visibleTokenMilliUnits;
    private long _toolArgTokenMilliUnits;

    public bool IsInsideInlineThink => _insideInlineThink;

    public string? LatchedWidgetName => _latchedWidgetName;

    public bool HasToolCalls => _toolCalls.Count > 0;

    public string? ActiveToolName
    {
        get
        {
            string? last = null;
            foreach (ToolCallDraft draft in _toolCalls.Values)
            {
                if (!string.IsNullOrWhiteSpace(draft.Name))
                {
                    last = draft.Name;
                }
            }

            return last;
        }
    }

    public int TotalReasoningFieldChars => _reasoningBuilder.Length;

    public int TotalThinkingChars => _reasoningBuilder.Length + _inlineThinkBuilder.Length;

    public int TotalVisibleChars => _visibleBuilder.Length;

    public int TotalToolCallArgumentChars
    {
        get
        {
            int sum = 0;
            foreach (ToolCallDraft draft in _toolCalls.Values)
            {
                sum += draft.Arguments.Length;
            }
            return sum;
        }
    }

    public int TotalUiContentChars => _visibleBuilder.Length > 0 ? _visibleBuilder.Length : TotalToolCallArgumentChars;

    public int EstimatedThinkingTokens =>
        TotalThinkingChars > 0 ? MilliUnitsToTokens(_thinkingTokenMilliUnits) : 0;

    public int EstimatedVisibleTokens =>
        _visibleBuilder.Length > 0 ? MilliUnitsToTokens(_visibleTokenMilliUnits) : 0;

    public int EstimatedToolCallArgumentTokens =>
        _toolArgTokenMilliUnits > 0 ? MilliUnitsToTokens(_toolArgTokenMilliUnits) : 0;

    public int EstimatedUiContentTokens =>
        _visibleBuilder.Length > 0
            ? EstimatedVisibleTokens + EstimatedToolCallArgumentTokens
            : EstimatedToolCallArgumentTokens;

    public static int EstimateTokenCount(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        return MilliUnitsToTokens(ComputeTokenMilliUnits(text));
    }

    public static string FormatCompactTokenCount(long tokens)
    {
        if (tokens < 0)
        {
            tokens = 0;
        }

        if (tokens < 1_000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        if (tokens < 1_000_000)
        {
            if (tokens < 10_000)
            {
                double kTenths = Math.Round(tokens / 1_000.0, 1, MidpointRounding.AwayFromZero);
                if (kTenths >= 10.0)
                {
                    return "10k";
                }

                return FormatOneDecimalOrInt(kTenths) + "k";
            }

            long kWhole = (long)Math.Round(tokens / 1_000.0, MidpointRounding.AwayFromZero);
            if (kWhole >= 1_000)
            {
                return "1m";
            }

            return kWhole.ToString(CultureInfo.InvariantCulture) + "k";
        }

        if (tokens < 10_000_000)
        {
            double mTenths = Math.Round(tokens / 1_000_000.0, 1, MidpointRounding.AwayFromZero);
            if (mTenths >= 10.0)
            {
                return "10m";
            }

            return FormatOneDecimalOrInt(mTenths) + "m";
        }

        long mWhole = (long)Math.Round(tokens / 1_000_000.0, MidpointRounding.AwayFromZero);
        return mWhole.ToString(CultureInfo.InvariantCulture) + "m";
    }

    public static string FormatCompactTokensWithUnit(long tokens) =>
        $"{FormatCompactTokenCount(tokens)} tokens";

    private static string FormatOneDecimalOrInt(double value)
    {
        string formatted = value.ToString("0.0", CultureInfo.InvariantCulture);
        return formatted.EndsWith(".0", StringComparison.Ordinal)
            ? formatted[..^2]
            : formatted;
    }

    private static int MilliUnitsToTokens(long milliUnits) =>
        milliUnits <= 0 ? 0 : (int)Math.Clamp((milliUnits + 999) / 1_000, 1, int.MaxValue);

    private static long ComputeTokenMilliUnits(ReadOnlySpan<char> span)
    {
        long units = 0;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                units += 550;
            }
            else if (c <= ' ')
            {
                units += 120;
            }
            else if ((c >= '\u2E80' && c <= '\u9FFF') ||
                     (c >= '\uF900' && c <= '\uFAFF') ||
                     (c >= '\uFF00' && c <= '\uFFEF') ||
                     (c >= '\uAC00' && c <= '\uD7AF'))
            {
                units += 650;
            }
            else
            {
                units += 270;
            }
        }

        return units;
    }

    public void AppendReasoningDelta(ReadOnlySpan<char> delta)
    {
        if (!delta.IsEmpty)
        {
            _reasoningBuilder.Append(delta);
            _thinkingTokenMilliUnits += ComputeTokenMilliUnits(delta);
        }
    }

    public void AppendContentDelta(ReadOnlySpan<char> delta)
    {
        if (delta.IsEmpty)
        {
            return;
        }

        int literalStart = 0;
        for (int i = 0; i < delta.Length; i++)
        {
            char c = delta[i];
            if (_tagLen == 0)
            {
                if (c == '<')
                {
                    if (i > literalStart)
                    {
                        AppendToActiveContentBuilder(delta.Slice(literalStart, i - literalStart));
                    }

                    _tagBuf[0] = '<';
                    _tagLen = 1;
                    literalStart = i + 1;
                }
                continue;
            }

            if (c == '<')
            {
                // Previous '<...' was not a think tag; flush it and start a new candidate tag.
                AppendToActiveContentBuilder(_tagBuf.AsSpan(0, _tagLen));
                _tagBuf[0] = '<';
                _tagLen = 1;
                literalStart = i + 1;
                continue;
            }

            if (_tagLen >= _tagBuf.Length)
            {
                AppendToActiveContentBuilder(_tagBuf.AsSpan(0, _tagLen));
                _tagLen = 0;
                literalStart = i;
                continue;
            }

            _tagBuf[_tagLen++] = c;
            literalStart = i + 1;

            if (c == '>')
            {
                if (TryHandleCompletedTag())
                {
                    _tagLen = 0;
                }
                else
                {
                    AppendToActiveContentBuilder(_tagBuf.AsSpan(0, _tagLen));
                    _tagLen = 0;
                }
                continue;
            }

            if (!CouldBeThinkTagPrefix(_tagBuf.AsSpan(0, _tagLen), _insideInlineThink, _activeCloseTag))
            {
                AppendToActiveContentBuilder(_tagBuf.AsSpan(0, _tagLen));
                _tagLen = 0;
            }
        }

        if (_tagLen == 0 && literalStart < delta.Length)
        {
            AppendToActiveContentBuilder(delta[literalStart..]);
        }

        TryLatchWidgetNameFromVisible();
    }

    public void AppendToolCallDelta(int index, string? id, string? nameDelta, string? argumentsDelta)
    {
        if (index < 0)
        {
            index = 0;
        }

        if (!_toolCalls.TryGetValue(index, out ToolCallDraft? draft))
        {
            if (_toolCalls.Count >= MaxTrackedStreamingToolCalls)
            {
                if (string.Equals(nameDelta?.Trim(), StudioToolTransport.ToolFinishTurn, StringComparison.Ordinal))
                {
                    index = ReservedFinishTurnSlot;
                    if (!_toolCalls.TryGetValue(index, out draft))
                    {
                        draft = new ToolCallDraft { Id = $"call_{index}" };
                        _toolCalls[index] = draft;
                    }
                }
                else
                {
                    return;
                }
            }
            else
            {
                draft = new ToolCallDraft { Id = $"call_{index}" };
                _toolCalls[index] = draft;
            }
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            draft.Id = id.Trim();
        }

        if (!string.IsNullOrEmpty(nameDelta))
        {
            draft.Name = string.Concat(draft.Name, nameDelta);
        }

        if (!string.IsNullOrEmpty(argumentsDelta))
        {
            draft.Arguments.Append(argumentsDelta);
            _toolArgTokenMilliUnits += ComputeTokenMilliUnits(argumentsDelta.AsSpan());
            TryLatchWidgetNameFromToolArgs(draft.Arguments);
        }
    }

    public void FlushPendingTag()
    {
        if (_tagLen > 0)
        {
            AppendToActiveContentBuilder(_tagBuf.AsSpan(0, _tagLen));
            _tagLen = 0;
            TryLatchWidgetNameFromVisible();
        }
    }

    public string GetThinkingTail(int maxChars = MaxThinkingTailChars)
    {
        int totalInline = _inlineThinkBuilder.Length;
        int totalField = _reasoningBuilder.Length;
        if (totalField == 0 && totalInline == 0)
        {
            return string.Empty;
        }

        if (totalInline == 0)
        {
            return GetBuilderTail(_reasoningBuilder, maxChars);
        }

        if (totalField == 0)
        {
            return GetBuilderTail(_inlineThinkBuilder, maxChars);
        }

        // Take newest characters across separator without formatting full strings.
        if (totalInline + 2 >= maxChars)
        {
            return GetBuilderTail(_inlineThinkBuilder, maxChars);
        }

        string inlineTail = _inlineThinkBuilder.ToString(0, totalInline);
        int fieldBudget = Math.Max(0, maxChars - inlineTail.Length - 2);
        string fieldTail = fieldBudget > 0 ? GetBuilderTail(_reasoningBuilder, fieldBudget) : string.Empty;
        return fieldTail.Length > 0 ? $"{fieldTail}\n\n{inlineTail}" : inlineTail;
    }

    public string GetVisibleTail(int maxChars = MaxVisibleTailChars)
    {
        if (_visibleBuilder.Length > 0)
        {
            return GetBuilderTail(_visibleBuilder, maxChars);
        }

        if (_toolCalls.Count > 0)
        {
            ToolCallDraft last = _toolCalls.Values.Last();
            if (last.Arguments.Length > 0)
            {
                return GetBuilderTail(last.Arguments, maxChars);
            }
        }

        return string.Empty;
    }

    public string GetFullThinking()
    {
        string field = _reasoningBuilder.ToString().Trim();
        string inline = _inlineThinkBuilder.ToString().Trim();
        if (field.Length == 0) return inline;
        if (inline.Length == 0) return field;
        return $"{field}\n\n{inline}";
    }

    public string? GetStreamingHint()
    {
        string tail = GetVisibleTail(1600);
        if (string.IsNullOrWhiteSpace(tail))
        {
            return null;
        }

        string? replySnippet = null;
        Match replyMatch = StreamingReplyPreviewRegex().Match(tail);
        if (replyMatch.Success)
        {
            string rawReply = replyMatch.Groups["reply"].Value
                .Replace("\\n", " ", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Trim();
            if (rawReply.Length >= 4)
            {
                replySnippet = rawReply.Length > 52 ? rawReply[..52] + "…" : rawReply;
            }
        }

        string? pulse = null;
        MatchCollection fnMatches = StreamingExportFunctionRegex().Matches(tail);
        if (fnMatches.Count > 0)
        {
            var fns = new List<string>(4);
            for (int i = Math.Max(0, fnMatches.Count - 4); i < fnMatches.Count; i++)
            {
                string fn = fnMatches[i].Groups["fn"].Value + "()";
                if (!fns.Contains(fn, StringComparer.Ordinal))
                {
                    fns.Add(fn);
                }
            }
            if (fns.Count > 0)
            {
                pulse = "logic.js → " + string.Join(" · ", fns);
            }
        }

        if (pulse is null)
        {
            MatchCollection typeMatches = StreamingNodeTypeRegex().Matches(tail);
            if (typeMatches.Count > 0)
            {
                var types = new List<string>(5);
                for (int i = Math.Max(0, typeMatches.Count - 8); i < typeMatches.Count && types.Count < 5; i++)
                {
                    string t = typeMatches[i].Groups["type"].Value;
                    if (!types.Contains(t, StringComparer.Ordinal))
                    {
                        types.Add(t);
                    }
                }
                if (types.Count > 0)
                {
                    pulse = string.Join(" → ", types);
                }
            }
        }

        if (replySnippet is not null && pulse is not null)
        {
            return $"{replySnippet} · {pulse}";
        }

        return pulse ?? replySnippet;
    }

    public string GetFullVisibleContent()
    {
        FlushPendingTag();
        return _visibleBuilder.ToString();
    }

    public IReadOnlyList<AiToolCallSpec> BuildToolCalls()
    {
        if (_toolCalls.Count == 0)
        {
            return [];
        }

        var result = new List<AiToolCallSpec>(_toolCalls.Count);
        foreach (KeyValuePair<int, ToolCallDraft> kv in _toolCalls)
        {
            ToolCallDraft d = kv.Value;
            if (string.IsNullOrWhiteSpace(d.Name))
            {
                continue;
            }

            string id = string.IsNullOrWhiteSpace(d.Id) ? $"call_{kv.Key}" : d.Id;
            string args = d.Arguments.Length == 0 ? "{}" : d.Arguments.ToString();
            result.Add(new AiToolCallSpec(id, d.Name.Trim(), args));
        }

        return result;
    }

    private void AppendToActiveContentBuilder(ReadOnlySpan<char> span)
    {
        if (!span.IsEmpty)
        {
            long units = ComputeTokenMilliUnits(span);
            if (_insideInlineThink)
            {
                _inlineThinkBuilder.Append(span);
                _thinkingTokenMilliUnits += units;
            }
            else
            {
                _visibleBuilder.Append(span);
                _visibleTokenMilliUnits += units;
            }
        }
    }

    private bool TryHandleCompletedTag()
    {
        ReadOnlySpan<char> candidate = _tagBuf.AsSpan(0, _tagLen);
        if (!_insideInlineThink)
        {
            for (int i = 0; i < OpenThinkTags.Length; i++)
            {
                if (candidate.Equals(OpenThinkTags[i].AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    _insideInlineThink = true;
                    _activeCloseTag = CloseThinkTags[i];
                    return true;
                }
            }
            return false;
        }

        if (candidate.Equals(_activeCloseTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            _insideInlineThink = false;
            return true;
        }

        return false;
    }

    private static bool CouldBeThinkTagPrefix(ReadOnlySpan<char> candidate, bool insideThink, string activeCloseTag)
    {
        if (insideThink)
        {
            return activeCloseTag.AsSpan().StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
        }

        foreach (string openTag in OpenThinkTags)
        {
            if (openTag.AsSpan().StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void TryLatchWidgetNameFromVisible()
    {
        if (_latchedWidgetName is not null || _visibleNameLatchDone || _visibleBuilder.Length < 8)
        {
            return;
        }

        int scanLen = Math.Min(_visibleBuilder.Length, NameLatchScanWindowChars);
        string prefix = _visibleBuilder.ToString(0, scanLen);
        Match match = StreamingJsonNameRegex().Match(prefix);
        if (match.Success)
        {
            string candidate = match.Groups["name"].Value.Trim();
            if (candidate.Length > 0)
            {
                _latchedWidgetName = candidate;
                _visibleNameLatchDone = true;
                return;
            }
        }

        if (_visibleBuilder.Length >= NameLatchScanWindowChars)
        {
            _visibleNameLatchDone = true;
        }
    }

    private void TryLatchWidgetNameFromToolArgs(StringBuilder argsBuilder)
    {
        if (_latchedWidgetName is not null || _toolArgNameLatchDone || argsBuilder.Length < 10)
        {
            return;
        }

        int scanLen = Math.Min(argsBuilder.Length, NameLatchScanWindowChars);
        string prefix = argsBuilder.ToString(0, scanLen);

        Match escaped = EscapedJsonNameRegex().Match(prefix);
        if (escaped.Success)
        {
            string name = escaped.Groups["name"].Value.Trim();
            if (name.Length > 0)
            {
                _latchedWidgetName = name;
                _toolArgNameLatchDone = true;
                return;
            }
        }

        Match direct = StreamingJsonNameRegex().Match(prefix);
        if (direct.Success)
        {
            string name = direct.Groups["name"].Value.Trim();
            if (name.Length > 0 &&
                !string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                _latchedWidgetName = name;
                _toolArgNameLatchDone = true;
                return;
            }
        }

        if (argsBuilder.Length >= NameLatchScanWindowChars)
        {
            _toolArgNameLatchDone = true;
        }
    }

    private static string GetBuilderTail(StringBuilder sb, int maxChars)
    {
        int len = sb.Length;
        if (len == 0 || maxChars <= 0)
        {
            return string.Empty;
        }

        if (len <= maxChars)
        {
            return sb.ToString();
        }

        return sb.ToString(len - maxChars, maxChars);
    }
}

/// <summary>
/// Dual-mode transport layer supporting OpenAI native <c>tools</c> function calling
/// and tagged fenced-block fallback (<c>deskbox-action</c>, <c>deskbox-manifest-edit</c>, <c>deskbox-script-patch</c>).
/// Strictly NativeAOT-safe with <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>.
/// </summary>
public static partial class StudioToolTransport
{
    public const string ToolReadWorkspace = "read_workspace";
    public const string ToolInspectCapabilities = "inspect_capabilities";
    public const string ToolProbeHttp = "probe_http";
    public const string ToolEditManifest = "edit_manifest";
    public const string ToolPatchScript = "patch_script";
    public const string ToolWriteBundle = "write_bundle";
    public const string ToolFinishTurn = "finish_turn";

    private static readonly JsonDocumentOptions PermissiveJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128
    };

    [GeneratedRegex(@"<<<<<<<\s*SEARCH\s*\r?\n(?<search>[\s\S]*?)\r?\n=======\s*\r?\n(?<replace>[\s\S]*?)\r?\n>>>>>>>\s*REPLACE", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 500)]
    private static partial Regex GitConflictHunkRegex();

    private static readonly Lazy<byte[]> s_cachedOpenAiToolsArrayUtf8 = new(
        BuildCachedOpenAiToolsArrayUtf8,
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    public static void WriteOpenAiToolsArray(Utf8JsonWriter writer)
    {
        // StablePrefix: always emit the exact same 7 tool definitions in canonical order across all rounds & turns
        // so provider KV-caches ([tools + system]) remain 100% warm, using pre-serialized UTF-8 bytes for zero allocation.
        writer.WritePropertyName("tools"u8);
        writer.WriteRawValue(s_cachedOpenAiToolsArrayUtf8.Value, skipInputValidation: true);
    }

    private static byte[] BuildCachedOpenAiToolsArrayUtf8()
    {
        using var ms = new MemoryStream(4096);
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions
                   {
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            WriteOpenAiToolsArrayBody(writer);
        }

        return ms.ToArray();
    }

    private static void WriteOpenAiToolsArrayBody(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();

        // 1. read_workspace
        WriteToolFunctionHeader(
            writer,
            ToolReadWorkspace,
            "Read a folded section, subtree node, symbol body, or line slice of an existing widget workspace without echoing the whole file.");
        writer.WriteStartObject("properties");
        WriteEnumStringProperty(writer, "section", "Which slice to inspect.", ["overview", "node", "manifest_section", "script", "locales"]);
        WriteStringProperty(writer, "nodePath", "Element ID (#elemId) or JSONPath selector for section='node' or 'manifest_section', e.g. '#inp_keyword' or '$.elements.header_row'.");
        WriteIntegerProperty(writer, "maxDepth", "Maximum subtree nesting depth for 'node' inspection (1-8, default 4).");
        WriteStringProperty(writer, "symbol", "Function/symbol name to read inside logic.js for section='script'.");
        WriteIntegerProperty(writer, "startLine", "1-based start line for sliced reading.");
        WriteIntegerProperty(writer, "endLine", "1-based inclusive end line for sliced reading.");
        writer.WriteEndObject();
        WriteRequiredArray(writer, ["section"]);
        WriteToolFunctionFooter(writer);

        // 2. inspect_capabilities
        WriteToolFunctionHeader(
            writer,
            ToolInspectCapabilities,
            "Retrieve exact schema contracts and golden JSON examples for DeskBox Domain Capability Packs (or legacy atomic IDs) by ID or search query. Batch ALL needed pack IDs into a single call rather than splitting across rounds.");
        writer.WriteStartObject("properties");
        string validPackIds = string.Join(", ", CustomWidgetCapabilityRegistry.AllDomainPacks.Select(static p => p.Id));
        WriteStringArrayProperty(
            writer,
            "ids",
            $"Domain Pack IDs to inspect (pass all needed packs together in one call; legacy 37 atomic IDs also resolve). Domain Packs: {validPackIds}.");
        WriteStringProperty(writer, "query", "Free-text keyword query across DeskBox capabilities and node types.");
        writer.WriteEndObject();
        WriteRequiredArray(writer, []);
        WriteToolFunctionFooter(writer);

        // 3. probe_http
        WriteToolFunctionHeader(
            writer,
            ToolProbeHttp,
            "Execute a read-only GET or HEAD HTTP request to inspect a public API endpoint's status and JSON response skeleton.");
        writer.WriteStartObject("properties");
        WriteStringProperty(writer, "url", "Target http:// or https:// URL.");
        WriteEnumStringProperty(writer, "method", "HTTP method (GET or HEAD).", ["GET", "HEAD"]);
        WriteObjectProperty(writer, "headers", "Optional key-value HTTP headers.");
        writer.WriteEndObject();
        WriteRequiredArray(writer, ["url"]);
        WriteToolFunctionFooter(writer);

        // 4. edit_manifest (canonical schemaVersion 1 Flat Element Graph surgical editor)
        WriteToolFunctionHeader(
            writer,
            ToolEditManifest,
            "Surgically update a canonical schemaVersion 1 Flat Element Graph manifest.json ('root' + 'elements' + 'popovers[*].root') by element ID or top-level key without rewriting untouched elements.");
        writer.WriteStartObject("properties");
        WriteObjectProperty(
            writer,
            "upsertElements",
            "Map of element ID -> element object to create or replace in 'elements' (containers reference child IDs as string arrays, and icons use semantic names like 'add', 'edit', 'delete', 'download', e.g. { \"btn_export\": { \"type\": \"Button\", \"icon\": \"download\", \"text\": \"Export\", \"onClick\": ... } }).");
        WriteStringArrayProperty(
            writer,
            "removeElements",
            "Element IDs to delete from 'elements' (dangling parent references are cleaned up automatically).");
        WriteObjectProperty(
            writer,
            "patchRoot",
            "Top-level manifest keys to set or merge (e.g. 'state', 'settings', 'sources', 'collections', 'popovers', 'capsule', 'script', 'root', 'defaultSize').");
        WriteStringArrayProperty(
            writer,
            "removeRootKeys",
            "Top-level manifest keys to remove (e.g. 'script', 'onDrop').");
        writer.WriteEndObject();
        WriteRequiredArray(writer, []);
        WriteToolFunctionFooter(writer);

        // 5. patch_script
        WriteToolFunctionHeader(
            writer,
            ToolPatchScript,
            "Create, replace, or patch logic.js. Pass 'hunks' for targeted SEARCH/REPLACE edits on existing logic.js, or 'content' when creating a brand-new logic.js.");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("hunks");
        writer.WriteString("type", "array");
        writer.WriteString("description", "Ordered SEARCH/REPLACE hunks applied atomically to logic.js.");
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        WriteStringProperty(writer, "search", "Unique verbatim block of lines from logic.js (empty only when creating a brand-new logic.js).");
        WriteStringProperty(writer, "replace", "Replacement ES2023 JavaScript code.");
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("search");
        writer.WriteStringValue("replace");
        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteStringProperty(writer, "content", "Complete ES2023 logic.js module source when creating logic.js from scratch (prefer 'hunks' when editing an existing script).");
        writer.WriteEndObject();
        WriteRequiredArray(writer, []);
        WriteToolFunctionFooter(writer);

        // 6. write_bundle (accept raw JSON object `manifest` + `sampleData` to eliminate double string-escaping, plus split-call file updates)
        WriteToolFunctionHeader(
            writer,
            ToolWriteBundle,
            "Write a widget bundle (use for initial creation or full topic pivot; for widgets with >80 lines of logic.js, you may write 'manifest' + 'sampleData' first and then supply 'logicJs' via patch_script or a second write_bundle call to avoid output truncation).");
        writer.WriteStartObject("properties");
        WriteObjectProperty(
            writer,
            "manifest",
            "Complete valid schemaVersion 1 Flat Element Graph manifest.json object with 'root', 'elements' dictionary, 'popovers[*].root', and semantic icon names (pass as a raw JSON object, not string-escaped; do NOT nest logicJs, locales, or sampleData inside manifest). May be omitted when updating only logicJs/locales/sampleData on an already-written manifest.");
        WriteStringProperty(writer, "logicJs", "Optional complete ES2023 logic.js script.");
        WriteObjectProperty(writer, "locales", "Optional localization catalog object mapping culture codes to dictionaries: { \"zh-CN\": { ... }, \"en-US\": { ... } }.");
        writer.WriteStartObject("deleteScript");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Set true to delete existing logic.js.");
        writer.WriteEndObject();
        WriteObjectProperty(writer, "sampleData", "Optional preview sample data object: { \"collections\": { \"<name>\": [...] } }.");
        writer.WriteEndObject();
        WriteRequiredArray(writer, []);
        WriteToolFunctionFooter(writer);

        // 7. finish_turn
        WriteToolFunctionHeader(
            writer,
            ToolFinishTurn,
            "Complete the Studio turn and commit your reply/edits to the user. May be called in the same assistant response alongside edit_manifest/patch_script/write_bundle for 1-round completion.");
        writer.WriteStartObject("properties");
        WriteEnumStringProperty(
            writer,
            "kind",
            "Turn outcome kind: 'edit' after mutating workspace; 'clarify' for Turn-0 multi-dimension confirmation; 'answer' for pure Q&A/consultation without editing; 'unsupported' when the request exceeds DeskBox capabilities (always explain why and provide 1-3 feasible alternatives in suggestions).",
            ["edit", "clarify", "answer", "unsupported"]);
        WriteStringProperty(writer, "reply", "Friendly 1-2 sentence explanation in the user's language.");
        WriteStringProperty(writer, "changeSummary", "Concise revision summary <= 24 chars.");
        WriteStringProperty(writer, "limitationNote", "Optional concise 1-sentence note ONLY when kind='edit' implements the feasible core of a partially supported request while omitting an out-of-scope sub-feature (omit when fully fulfilled).");
        writer.WriteStartObject("deleteScript");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Set true when deleting existing logic.js.");
        writer.WriteEndObject();
        WriteObjectProperty(writer, "sampleData", "Optional preview sample data object: { \"collections\": { \"<name>\": [...] } }.");
        WriteStringArrayProperty(writer, "suggestions", "Optional 1-3 short quick-action pills (<= 18 chars each).");
        writer.WriteStartObject("questions");
        writer.WriteString("type", "array");
        writer.WriteString("description", "For kind='clarify': 1-3 structured multi-dimension confirmation questions with preselected recommended options.");
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        WriteStringProperty(writer, "id", "Short dimension ID, e.g. 'source' or 'layout'.");
        WriteStringProperty(writer, "header", "Short dimension title <= 16 chars, e.g. '数据来源' or '展示样式'.");
        writer.WriteStartObject("multiSelect");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "True when multiple options can be selected together.");
        writer.WriteEndObject();
        writer.WriteStartObject("options");
        writer.WriteString("type", "array");
        writer.WriteStartObject("items");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        WriteStringProperty(writer, "value", "Concrete choice value.");
        WriteStringProperty(writer, "label", "Short button label <= 14 chars.");
        writer.WriteStartObject("recommended");
        writer.WriteString("type", "boolean");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("label");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("header");
        writer.WriteStringValue("options");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteStringProperty(writer, "briefPurpose", "Optional 1-line widget purpose update for brief.md.");
        WriteStringArrayProperty(writer, "addConfirmed", "Newly confirmed requirements for brief.md.");
        WriteStringArrayProperty(writer, "addRejected", "Explicitly rejected directions for brief.md.");
        WriteStringArrayProperty(writer, "addStylePreferences", "Confirmed visual/layout preferences for brief.md.");
        writer.WriteStartObject("resetBrief");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Set true on full widget topic replacement.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        WriteRequiredArray(writer, ["kind", "reply"]);
        WriteToolFunctionFooter(writer);

        writer.WriteEndArray();
    }

    public static bool TryExtractBlockToolCalls(
        string visibleText,
        out List<AiToolCallSpec> calls,
        bool allowCompleteBundleBlocks = true)
    {
        calls = [];
        if (string.IsNullOrWhiteSpace(visibleText))
        {
            return false;
        }

        Dictionary<string, List<string>> blocks =
            StudioTurnResponseParser.ExtractTaggedFencedBlocks(visibleText, out string proseOutsideFences);

        int seq = 0;
        if (blocks.Count == 0)
        {
            string trimmed = visibleText.Trim();
            if (trimmed.StartsWith('{'))
            {
                if (TryNormalizeBlockActionToToolCall(trimmed, seq++, out AiToolCallSpec? rawCall) &&
                    rawCall is not null)
                {
                    calls.Add(rawCall);
                    return true;
                }

                if (allowCompleteBundleBlocks &&
                    TryNormalizeRawJsonEnvelopeOrManifestToToolCalls(trimmed, ref seq, calls))
                {
                    return true;
                }
            }

            return false;
        }

        if (blocks.TryGetValue("deskbox-action", out List<string>? actionBlocks))
        {
            foreach (string raw in actionBlocks)
            {
                if (TryNormalizeBlockActionToToolCall(raw, seq++, out AiToolCallSpec? spec) && spec is not null)
                {
                    calls.Add(spec);
                }
            }
        }
        else if (blocks.Count == 1 &&
                 blocks.TryGetValue("json", out List<string>? jsonActionBlocks) &&
                 jsonActionBlocks.Count == 1 &&
                 TryNormalizeBlockActionToToolCall(jsonActionBlocks[0], seq++, out AiToolCallSpec? jsonCall) &&
                 jsonCall is not null)
        {
            calls.Add(jsonCall);
        }

        if (blocks.TryGetValue("deskbox-manifest-edit", out List<string>? editBlocks))
        {
            foreach (string raw in editBlocks)
            {
                string trimmedEdit = raw.Trim();
                if (trimmedEdit.StartsWith('{'))
                {
                    calls.Add(new AiToolCallSpec($"block_{seq++}", ToolEditManifest, trimmedEdit));
                }
            }
        }

        if (blocks.TryGetValue("deskbox-script-patch", out List<string>? hunkBlocks))
        {
            foreach (string raw in hunkBlocks)
            {
                if (TryWrapScriptPatchBlock(raw, seq++, out AiToolCallSpec? patchCall) && patchCall is not null)
                {
                    calls.Add(patchCall);
                }
            }
        }

        bool hasIncrementalOps = calls.Any(static c =>
            c.Name is ToolEditManifest or ToolPatchScript);

        // Single-Core Orchestrator: also normalize full fenced-block bundle / clarify / answer responses
        // directly into write_bundle / patch_script + finish_turn so BOTH native tools and fenced-block
        // fallback providers flow through the ONE unified tool loop.
        if (allowCompleteBundleBlocks && calls.Count == 0)
        {
            TryExtractCompleteFencedBundleToToolCalls(blocks, proseOutsideFences, ref seq, calls);
        }
        else if (hasIncrementalOps &&
                 blocks.TryGetValue("deskbox-meta", out List<string>? metaBlocks) &&
                 metaBlocks.Count > 0)
        {
            calls.Add(new AiToolCallSpec($"block_{seq++}", ToolFinishTurn, metaBlocks[^1].Trim()));
        }

        return calls.Count > 0;
    }

    private static void TryExtractCompleteFencedBundleToToolCalls(
        Dictionary<string, List<string>> blocks,
        string proseOutsideFences,
        ref int seq,
        List<AiToolCallSpec> calls)
    {
        string? rawMeta = blocks.TryGetValue("deskbox-meta", out List<string>? metaList) && metaList.Count > 0
            ? metaList[^1].Trim()
            : null;
        string? rawManifest = blocks.TryGetValue("deskbox-manifest", out List<string>? manList) && manList.Count > 0
            ? manList[^1].Trim()
            : null;
        string? rawScript = null;
        if (blocks.TryGetValue("deskbox-script", out List<string>? dsList) && dsList.Count > 0)
        {
            rawScript = dsList[^1];
        }
        else if (blocks.TryGetValue("js", out List<string>? jsList) && jsList.Count > 0)
        {
            rawScript = jsList[^1];
        }
        else if (blocks.TryGetValue("javascript", out List<string>? jsvList) && jsvList.Count > 0)
        {
            rawScript = jsvList[^1];
        }

        string? rawLocales = blocks.TryGetValue("deskbox-locales", out List<string>? locList) && locList.Count > 0
            ? locList[^1].Trim()
            : null;
        string? rawSample = blocks.TryGetValue("deskbox-sample", out List<string>? sampleList) && sampleList.Count > 0
            ? sampleList[^1].Trim()
            : null;

        // Check generic ```json``` block when explicit deskbox-manifest is absent.
        if (rawManifest is null && blocks.TryGetValue("json", out List<string>? jsonList) && jsonList.Count > 0)
        {
            string candidate = jsonList[^1].Trim();
            if (TryNormalizeRawJsonEnvelopeOrManifestToToolCalls(candidate, ref seq, calls, rawScript, rawLocales, rawSample, proseOutsideFences))
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(rawManifest))
        {
            bool deleteScript = rawMeta is not null && TryReadBoolFlag(rawMeta, "deleteScript");
            calls.Add(new AiToolCallSpec(
                $"block_{seq++}",
                ToolWriteBundle,
                SerializeSyntheticWriteBundleArgs(rawManifest, rawScript, rawLocales, deleteScript, rawSample)));
            calls.Add(new AiToolCallSpec(
                $"block_{seq++}",
                ToolFinishTurn,
                !string.IsNullOrWhiteSpace(rawMeta)
                    ? rawMeta!
                    : SerializeSyntheticFinishTurnArgs("edit", proseOutsideFences, rawSample)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(rawScript) && !string.IsNullOrWhiteSpace(rawMeta))
        {
            calls.Add(new AiToolCallSpec(
                $"block_{seq++}",
                ToolPatchScript,
                SerializeSyntheticPatchScriptContentArgs(rawScript)));
            calls.Add(new AiToolCallSpec($"block_{seq++}", ToolFinishTurn, rawMeta));
            return;
        }

        if (!string.IsNullOrWhiteSpace(rawMeta))
        {
            // Pure clarify / answer / deleteScript-only deskbox-meta block
            calls.Add(new AiToolCallSpec($"block_{seq++}", ToolFinishTurn, rawMeta));
        }
    }

    private static bool TryNormalizeRawJsonEnvelopeOrManifestToToolCalls(
        string rawJson,
        ref int seq,
        List<AiToolCallSpec> calls,
        string? companionScript = null,
        string? companionLocales = null,
        string? companionSample = null,
        string? fallbackReply = null)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson, PermissiveJsonOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Case 1: Bare canonical Flat Element Graph manifest.json object
            bool isBareManifest =
                root.TryGetProperty("elements", out JsonElement elemsEl) &&
                elemsEl.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("root", out _);
            if (isBareManifest)
            {
                calls.Add(new AiToolCallSpec(
                    $"block_{seq++}",
                    ToolWriteBundle,
                    SerializeSyntheticWriteBundleArgs(rawJson, companionScript, companionLocales, deleteScript: false, companionSample)));
                calls.Add(new AiToolCallSpec(
                    $"block_{seq++}",
                    ToolFinishTurn,
                    SerializeSyntheticFinishTurnArgs("edit", fallbackReply ?? string.Empty, companionSample)));
                return true;
            }

            // Case 2: Legacy Studio JSON envelope { "kind": "edit"|"clarify"|"answer"|"unsupported", "manifest": ..., "reply": ... }
            string? kind = NormalizeTurnKindOrNull(GetOptionalString(root, "kind"));
            if (kind is "edit" or "clarify" or "answer" or "unsupported")
            {
                string? envManifest = ExtractObjectOrJsonStringProperty(root, "manifest", "manifestJson");
                string? envScript = companionScript ?? GetOptionalString(root, "logicJs");
                string? envLocales = companionLocales ?? ExtractObjectOrJsonStringProperty(root, "locales", "i18n");
                string? envSample = companionSample ?? ExtractObjectOrJsonStringProperty(root, "sampleData", "sampleDataJson");
                bool envDeleteScript = root.TryGetProperty("deleteScript", out JsonElement dsEl) && dsEl.ValueKind == JsonValueKind.True;

                if (!string.IsNullOrWhiteSpace(envManifest) || !string.IsNullOrWhiteSpace(envScript))
                {
                    calls.Add(new AiToolCallSpec(
                        $"block_{seq++}",
                        ToolWriteBundle,
                        SerializeSyntheticWriteBundleArgs(envManifest, envScript, envLocales, envDeleteScript, envSample)));
                }

                calls.Add(new AiToolCallSpec($"block_{seq++}", ToolFinishTurn, rawJson));
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadBoolFlag(string json, string propertyName)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, PermissiveJsonOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(propertyName, out JsonElement el) &&
                   el.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string SerializeSyntheticWriteBundleArgs(
        string? manifestJson,
        string? logicJs,
        string? localesJson,
        bool deleteScript,
        string? sampleDataJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(manifestJson))
            {
                w.WriteString("manifestJson", manifestJson);
            }
            if (!string.IsNullOrWhiteSpace(logicJs))
            {
                w.WriteString("logicJs", logicJs);
            }
            if (!string.IsNullOrWhiteSpace(localesJson))
            {
                w.WriteString("locales", localesJson);
            }
            if (deleteScript)
            {
                w.WriteBoolean("deleteScript", true);
            }
            if (!string.IsNullOrWhiteSpace(sampleDataJson))
            {
                w.WriteString("sampleDataJson", sampleDataJson);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string SerializeSyntheticPatchScriptContentArgs(string scriptContent)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("content", scriptContent);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string SerializeSyntheticFinishTurnArgs(string kind, string reply, string? sampleDataJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            w.WriteString("reply", reply ?? string.Empty);
            w.WriteString("changeSummary", string.Empty);
            if (!string.IsNullOrWhiteSpace(sampleDataJson))
            {
                w.WriteString("sampleDataJson", sampleDataJson);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static bool TryParseReadWorkspaceArgs(string argsJson, out StudioReadWorkspaceRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (string.IsNullOrWhiteSpace(argsJson) || string.Equals(argsJson.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            request = new StudioReadWorkspaceRequest("overview", null, 4, null, null, null);
            return true;
        }

        if (!TryParseRootObject(argsJson ?? string.Empty, out JsonDocument? doc, out error))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc!.RootElement;
            string section = GetOptionalString(root, "section") ?? "overview";
            string? nodePath = GetOptionalString(root, "nodePath");
            int maxDepth = GetOptionalInt(root, "maxDepth") ?? 4;
            string? symbol = GetOptionalString(root, "symbol");
            int? startLine = GetOptionalInt(root, "startLine");
            int? endLine = GetOptionalInt(root, "endLine");
            request = new StudioReadWorkspaceRequest(section, nodePath, maxDepth, symbol, startLine, endLine);
            return true;
        }
    }

    public static bool TryParseEditManifestArgs(
        string argsJson,
        out StudioFlatManifestEditRequest? editRequest,
        out string? error)
    {
        editRequest = null;
        error = null;
        if (string.IsNullOrWhiteSpace(argsJson))
        {
            error = "edit_manifest arguments must not be empty.";
            return false;
        }

        try
        {
            JsonNode? parsed = StudioWorkspaceEngine.ParsePermissiveJsonNode(argsJson);
            if (parsed is not JsonObject rootObj)
            {
                error = "edit_manifest arguments must be a JSON object.";
                return false;
            }

            JsonObject? upsertElements = null;
            if (rootObj["upsertElements"] is JsonObject ue)
            {
                upsertElements = (JsonObject)ue.DeepClone();
            }

            List<string>? removeElements = null;
            if (rootObj["removeElements"] is JsonArray re)
            {
                removeElements = [];
                foreach (JsonNode? item in re)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
                    {
                        removeElements.Add(s.Trim());
                    }
                }
            }

            JsonObject? patchRoot = null;
            if (rootObj["patchRoot"] is JsonObject pr)
            {
                patchRoot = (JsonObject)pr.DeepClone();
            }

            List<string>? removeRootKeys = null;
            if (rootObj["removeRootKeys"] is JsonArray rrk)
            {
                removeRootKeys = [];
                foreach (JsonNode? item in rrk)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
                    {
                        removeRootKeys.Add(s.Trim());
                    }
                }
            }

            if ((upsertElements is null || upsertElements.Count == 0) &&
                (removeElements is null || removeElements.Count == 0) &&
                (patchRoot is null || patchRoot.Count == 0) &&
                (removeRootKeys is null || removeRootKeys.Count == 0))
            {
                error = "edit_manifest requires at least one of upsertElements, removeElements, patchRoot, or removeRootKeys.";
                return false;
            }

            editRequest = new StudioFlatManifestEditRequest(upsertElements, removeElements, patchRoot, removeRootKeys);
            return true;
        }
        catch (JsonException jex)
        {
            error = $"Invalid edit_manifest JSON arguments: {jex.Message}";
            return false;
        }
    }

    public static bool TryParsePatchScriptArgs(string argsJson, out List<StudioScriptPatchHunk> hunks, out string? error)
    {
        if (!TryParsePatchScriptArgs(argsJson, out hunks, out string? fullScriptContent, out error))
        {
            return false;
        }

        if (fullScriptContent is not null)
        {
            hunks.Add(new StudioScriptPatchHunk(string.Empty, fullScriptContent));
        }

        return true;
    }

    public static bool TryParsePatchScriptArgs(
        string argsJson,
        out List<StudioScriptPatchHunk> hunks,
        out string? fullScriptContent,
        out string? error)
    {
        hunks = [];
        fullScriptContent = null;
        error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson, PermissiveJsonOptions);
            JsonElement root = doc.RootElement;
            JsonElement arrEl = default;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arrEl = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                bool hasHunksProp = root.TryGetProperty("hunks", out JsonElement hp) &&
                                    hp.ValueKind == JsonValueKind.Array;
                if (hasHunksProp && hp.GetArrayLength() > 0)
                {
                    arrEl = hp;
                }
                else if (root.TryGetProperty("search", out JsonElement sEl) &&
                         sEl.ValueKind == JsonValueKind.String)
                {
                    string replaceSingle = root.TryGetProperty("replace", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.String
                        ? rEl.GetString() ?? string.Empty
                        : string.Empty;
                    hunks.Add(new StudioScriptPatchHunk(sEl.GetString() ?? string.Empty, replaceSingle));
                    return true;
                }
                else
                {
                    string? directScript = GetOptionalString(root, "content")
                        ?? GetOptionalString(root, "logicJs")
                        ?? GetOptionalString(root, "script")
                        ?? GetOptionalString(root, "code")
                        ?? GetOptionalString(root, "replace");
                    if (!string.IsNullOrWhiteSpace(directScript))
                    {
                        fullScriptContent = directScript;
                        return true;
                    }

                    error = hasHunksProp
                        ? "'hunks' array must not be empty."
                        : "patch_script requires either { \"hunks\": [{ \"search\": \"...\", \"replace\": \"...\" }] } or { \"content\": \"export function onInit(ctx) { ... }\" }.";
                    return false;
                }
            }
            else
            {
                error = "patch_script requires either { \"hunks\": [{ \"search\": \"...\", \"replace\": \"...\" }] } or { \"content\": \"export function onInit(ctx) { ... }\" }.";
                return false;
            }

            foreach (JsonElement item in arrEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("search", out JsonElement sEl) ||
                    sEl.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("replace", out JsonElement rEl) ||
                    rEl.ValueKind != JsonValueKind.String)
                {
                    error = "Each hunk must be an object with string 'search' and 'replace'.";
                    return false;
                }

                hunks.Add(new StudioScriptPatchHunk(sEl.GetString() ?? string.Empty, rEl.GetString() ?? string.Empty));
            }

            if (hunks.Count == 0)
            {
                error = "'hunks' array must not be empty.";
                return false;
            }

            return true;
        }
        catch (JsonException jex)
        {
            error = $"Invalid patch_script JSON arguments: {jex.Message}";
            return false;
        }
    }

    public static bool TryParseWriteBundleArgs(
        string argsJson,
        out string manifestJson,
        out string? logicJs,
        out bool deleteScript,
        out string? sampleDataJson,
        out string? error) =>
        TryParseWriteBundleArgs(
            argsJson,
            out manifestJson,
            out logicJs,
            out _,
            out deleteScript,
            out sampleDataJson,
            out error);

    public static bool TryParseWriteBundleArgs(
        string argsJson,
        out string manifestJson,
        out string? logicJs,
        out CustomWidgetLocaleCatalog? locales,
        out bool deleteScript,
        out string? sampleDataJson,
        out string? error)
    {
        manifestJson = string.Empty;
        logicJs = null;
        locales = null;
        deleteScript = false;
        sampleDataJson = null;
        error = null;

        if (!TryParseRootObject(argsJson ?? string.Empty, out JsonDocument? doc, out error))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc!.RootElement;
            manifestJson = ExtractObjectOrJsonStringProperty(root, "manifest", "manifestJson") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(manifestJson) &&
                root.TryGetProperty("elements", out _))
            {
                manifestJson = root.GetRawText();
            }

            logicJs = GetOptionalString(root, "logicJs")
                      ?? GetOptionalString(root, "scriptContent");
            string? localesRaw = ExtractObjectOrJsonStringProperty(root, "locales", "i18n");
            if (!string.IsNullOrWhiteSpace(localesRaw))
            {
                try
                {
                    using JsonDocument locDoc = JsonDocument.Parse(
                        localesRaw,
                        PermissiveJsonOptions);
                    if (locDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        CustomWidgetLocaleCatalog parsedCatalog = CustomWidgetLocaleCatalog.ParseCatalogElement(locDoc.RootElement);
                        if (parsedCatalog.HasAnyLocale)
                        {
                            locales = parsedCatalog;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed optional locales block; manifest validation handles required keys.
                }
            }

            deleteScript = root.TryGetProperty("deleteScript", out JsonElement dsEl) && dsEl.ValueKind == JsonValueKind.True;
            sampleDataJson = ExtractObjectOrJsonStringProperty(root, "sampleData", "sampleDataJson");

            if (string.IsNullOrWhiteSpace(manifestJson) &&
                string.IsNullOrWhiteSpace(logicJs) &&
                locales is null &&
                string.IsNullOrWhiteSpace(sampleDataJson) &&
                !deleteScript)
            {
                error = "write_bundle requires a non-empty 'manifest' JSON object (or 'logicJs'/'locales'/'sampleData' when updating an existing workspace).";
                return false;
            }

            return true;
        }
    }

    public const int MaxExecutedToolCallsPerRound = 8;

    /// <summary>
    /// Deduplicates runaway/repeated tool calls within a single assistant round and enforces a strict per-round budget
    /// so degenerate parallel-tool loops (e.g. 250+ repeated <c>write_bundle</c> calls) cannot flood UI step traces
    /// or inflate subsequent multi-turn prompt context.
    /// </summary>
    public static IReadOnlyList<AiToolCallSpec> NormalizeAndBoundRoundToolCalls(
        IReadOnlyList<AiToolCallSpec> rawCalls,
        out int droppedCount)
    {
        droppedCount = 0;
        if (rawCalls is null || rawCalls.Count == 0)
        {
            return [];
        }

        if (rawCalls.Count == 1)
        {
            return rawCalls;
        }

        var exactSeen = new HashSet<string>(StringComparer.Ordinal);
        AiToolCallSpec? bestManifestWriteBundle = null;
        bool bestManifestWriteBundleValid = false;
        AiToolCallSpec? bestCompanionWriteBundle = null;
        bool bestCompanionHasScriptOrLocales = false;
        AiToolCallSpec? bestFinishTurn = null;
        bool bestFinishTurnValid = false;

        var orderedCandidates = new List<AiToolCallSpec>(Math.Min(rawCalls.Count, MaxExecutedToolCallsPerRound + 2));

        foreach (AiToolCallSpec tc in rawCalls)
        {
            string trimmedArgs = (tc.ArgumentsJson ?? string.Empty).Trim();
            string exactKey = $"{tc.Name}\u001F{trimmedArgs}";
            if (!exactSeen.Add(exactKey))
            {
                continue;
            }

            if (string.Equals(tc.Name, ToolWriteBundle, StringComparison.Ordinal))
            {
                bool parsed = TryParseWriteBundleArgs(
                    tc.ArgumentsJson ?? string.Empty,
                    out string wbManifest,
                    out string? wbLogicJs,
                    out CustomWidgetLocaleCatalog? wbLocales,
                    out bool wbDelScript,
                    out _,
                    out _);

                bool hasManifestPayload = parsed
                    ? !string.IsNullOrWhiteSpace(wbManifest)
                    : (trimmedArgs.Contains("\"manifest\"", StringComparison.Ordinal) ||
                       trimmedArgs.Contains("\"elements\"", StringComparison.Ordinal));

                if (hasManifestPayload)
                {
                    if (bestManifestWriteBundle is null ||
                        (parsed && !bestManifestWriteBundleValid) ||
                        (parsed == bestManifestWriteBundleValid &&
                         trimmedArgs.Length > (bestManifestWriteBundle.ArgumentsJson?.Length ?? 0)))
                    {
                        bestManifestWriteBundle = tc;
                        bestManifestWriteBundleValid = parsed;
                    }
                }
                else if (parsed)
                {
                    bool hasScriptOrLocales = !string.IsNullOrWhiteSpace(wbLogicJs) || wbLocales is not null || wbDelScript;
                    if (bestCompanionWriteBundle is null || (hasScriptOrLocales && !bestCompanionHasScriptOrLocales))
                    {
                        bestCompanionWriteBundle = tc;
                        bestCompanionHasScriptOrLocales = hasScriptOrLocales;
                    }
                }
                else if (bestManifestWriteBundle is null && bestCompanionWriteBundle is null)
                {
                    bestManifestWriteBundle = tc;
                }

                orderedCandidates.Add(tc);
                continue;
            }

            if (string.Equals(tc.Name, ToolFinishTurn, StringComparison.Ordinal))
            {
                bool finValid = TryParseFinishTurnArgs(tc.ArgumentsJson ?? string.Empty, out _, out _);
                if (bestFinishTurn is null || finValid || !bestFinishTurnValid)
                {
                    bestFinishTurn = tc;
                    bestFinishTurnValid = finValid;
                }

                continue;
            }

            orderedCandidates.Add(tc);
        }

        // If the primary manifest write_bundle was truncated/unparseable, drop sampleData-only companion echoes
        // (keep companion write_bundle only when it supplies logicJs/locales/deleteScript).
        if (bestManifestWriteBundle is not null &&
            !bestManifestWriteBundleValid &&
            bestCompanionWriteBundle is not null &&
            !bestCompanionHasScriptOrLocales)
        {
            bestCompanionWriteBundle = null;
        }

        int nonFinishCap = bestFinishTurn is not null
            ? MaxExecutedToolCallsPerRound - 1
            : MaxExecutedToolCallsPerRound;

        var result = new List<AiToolCallSpec>(MaxExecutedToolCallsPerRound);
        bool emittedManifestBundle = false;
        bool emittedCompanionBundle = false;

        foreach (AiToolCallSpec tc in orderedCandidates)
        {
            if (result.Count >= nonFinishCap)
            {
                break;
            }

            if (string.Equals(tc.Name, ToolWriteBundle, StringComparison.Ordinal))
            {
                if (!emittedManifestBundle && ReferenceEquals(tc, bestManifestWriteBundle))
                {
                    result.Add(tc);
                    emittedManifestBundle = true;
                }
                else if (!emittedCompanionBundle && ReferenceEquals(tc, bestCompanionWriteBundle))
                {
                    result.Add(tc);
                    emittedCompanionBundle = true;
                }

                continue;
            }

            result.Add(tc);
        }

        if (bestFinishTurn is not null)
        {
            result.Add(bestFinishTurn);
        }

        droppedCount = Math.Max(0, rawCalls.Count - result.Count);
        return result;
    }

    public static string? NormalizeTurnKindOrNull(string? rawKind)
    {
        if (string.IsNullOrWhiteSpace(rawKind))
        {
            return null;
        }

        return rawKind.Trim().ToLowerInvariant() switch
        {
            "edit" => "edit",
            "clarify" => "clarify",
            "answer" => "answer",
            "unsupported" or "refuse" or "refused" or "cannot" or "impossible" or "out_of_scope" or "out-of-scope" or "declined" => "unsupported",
            _ => null
        };
    }

    public static bool TryParseFinishTurnArgs(string argsJson, out StudioFinishTurnSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (!TryParseRootObject(argsJson ?? string.Empty, out JsonDocument? doc, out error))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc!.RootElement;
            string kind = NormalizeTurnKindOrNull(GetOptionalString(root, "kind")) ?? "edit";

            string reply = GetOptionalString(root, "reply") ?? string.Empty;
            string changeSummary = GetOptionalString(root, "changeSummary") ?? string.Empty;
            string? limitationNote = NormalizeOptionalLimitationNote(GetOptionalString(root, "limitationNote"));
            bool deleteScript = root.TryGetProperty("deleteScript", out JsonElement dsEl) && dsEl.ValueKind == JsonValueKind.True;

            string? sampleDataJson = ExtractObjectOrJsonStringProperty(root, "sampleData", "sampleDataJson");

            var suggestions = ReadStringList(root, "suggestions", 3);
            List<StudioClarifyQuestionSpec> clarifyQuestions = CustomWidgetStudioSessionStore.ParseClarifyQuestionsFromObject(root, maxQuestions: 3);
            StudioBriefUpdateSpec? brief = ParseBriefUpdatesFromObject(root);
            spec = new StudioFinishTurnSpec(
                kind,
                reply,
                changeSummary,
                deleteScript,
                sampleDataJson,
                suggestions,
                brief,
                clarifyQuestions.Count > 0 ? clarifyQuestions : null,
                limitationNote);
            return true;
        }
    }

    internal static string? NormalizeOptionalLimitationNote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        return trimmed.Length > 280 ? $"{trimmed[..279].TrimEnd()}…" : trimmed;
    }

    public static bool TryParseInspectOrProbeArgs(
        string toolName,
        string argsJson,
        out StudioAgenticActionRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        bool isInspect = string.Equals(toolName, ToolInspectCapabilities, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(toolName, "inspectCapabilities", StringComparison.OrdinalIgnoreCase);
        if (isInspect)
        {
            string trimmedArgs = argsJson?.Trim() ?? string.Empty;
            if (trimmedArgs.Length == 0 || string.Equals(trimmedArgs, "null", StringComparison.OrdinalIgnoreCase))
            {
                request = new StudioAgenticActionRequest(
                    "inspectCapabilities",
                    [],
                    null,
                    null,
                    "GET",
                    null,
                    null);
                return true;
            }

            if (trimmedArgs.StartsWith('['))
            {
                try
                {
                    using JsonDocument arrDoc = JsonDocument.Parse(trimmedArgs, PermissiveJsonOptions);
                    if (arrDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var directIds = new List<string>();
                        foreach (JsonElement item in arrDoc.RootElement.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                directIds.Add(item.GetString()!.Trim());
                                if (directIds.Count >= 12)
                                {
                                    break;
                                }
                            }
                        }

                        request = new StudioAgenticActionRequest(
                            "inspectCapabilities",
                            directIds,
                            null,
                            null,
                            "GET",
                            null,
                            null);
                        return true;
                    }
                }
                catch
                {
                    // Fall through to standard object error reporting if truncated/malformed JSON
                }
            }
        }

        if (!TryParseRootObject(argsJson ?? string.Empty, out JsonDocument? doc, out error))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc!.RootElement;
            if (isInspect)
            {
                List<string> ids = ReadStringOrArrayTokens(root, "ids", 12);
                if (ids.Count == 0)
                {
                    ids = ReadStringOrArrayTokens(root, "capabilityIds", 12);
                }
                if (ids.Count == 0)
                {
                    ids = ReadStringOrArrayTokens(root, "capabilities", 12);
                }
                if (ids.Count == 0)
                {
                    ids = ReadStringOrArrayTokens(root, "id", 12);
                }
                if (ids.Count == 0)
                {
                    ids = ReadStringOrArrayTokens(root, "capability", 12);
                }

                string? query = GetOptionalString(root, "query")
                                ?? GetOptionalString(root, "keyword")
                                ?? GetOptionalString(root, "search");
                request = new StudioAgenticActionRequest(
                    "inspectCapabilities",
                    ids,
                    query,
                    null,
                    "GET",
                    null,
                    null);
                return true;
            }

            if (string.Equals(toolName, ToolProbeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "probeHttp", StringComparison.OrdinalIgnoreCase))
            {
                string? url = GetOptionalString(root, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    error = "probe_http requires a non-empty 'url'.";
                    return false;
                }

                string method = (GetOptionalString(root, "method") ?? "GET").Trim().ToUpperInvariant();
                Dictionary<string, string>? headers = null;
                if (root.TryGetProperty("headers", out JsonElement hEl) && hEl.ValueKind == JsonValueKind.Object)
                {
                    headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty p in hEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                        {
                            headers[p.Name] = p.Value.GetString()!;
                        }
                    }
                }

                request = new StudioAgenticActionRequest(
                    "probeHttp",
                    [],
                    null,
                    url.Trim(),
                    method,
                    headers,
                    null);
                return true;
            }

            error = $"Unknown capability/probe tool '{toolName}'.";
            return false;
        }
    }

    private static bool TryNormalizeBlockActionToToolCall(string rawBlock, int index, out AiToolCallSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(rawBlock))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawBlock.Trim(), PermissiveJsonOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Do not misclassify bare manifest.json or co-creation envelope objects as tool calls.
            if (root.TryGetProperty("schemaVersion", out _) ||
                (root.TryGetProperty("elements", out JsonElement elemsEl) && elemsEl.ValueKind == JsonValueKind.Object && root.TryGetProperty("root", out _)))
            {
                return false;
            }

            string? rawTool = GetOptionalString(root, "tool")
                              ?? GetOptionalString(root, "action");
            if (string.IsNullOrWhiteSpace(rawTool))
            {
                return false;
            }

            string mappedTool = rawTool.Trim().ToLowerInvariant() switch
            {
                "inspectcapabilities" or "inspect_capabilities" => ToolInspectCapabilities,
                "probehttp" or "probe_http" => ToolProbeHttp,
                "readworkspace" or "read_workspace" => ToolReadWorkspace,
                "editmanifest" or "edit_manifest" => ToolEditManifest,
                "patchscript" or "patch_script" => ToolPatchScript,
                "writebundle" or "write_bundle" => ToolWriteBundle,
                "finishturn" or "finish_turn" => ToolFinishTurn,
                _ => string.Empty
            };

            if (mappedTool.Length == 0)
            {
                return false;
            }

            string argsPayload = root.TryGetProperty("arguments", out JsonElement argsEl)
                ? (argsEl.ValueKind == JsonValueKind.String ? (argsEl.GetString() ?? "{}") : argsEl.GetRawText())
                : root.GetRawText();

            spec = new AiToolCallSpec($"block_{index}", mappedTool, argsPayload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWrapScriptPatchBlock(string rawBlock, int index, out AiToolCallSpec? spec)
    {
        spec = null;
        string trimmed = rawBlock.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(trimmed, PermissiveJsonOptions);
                string payload = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? $"{{\"hunks\":{doc.RootElement.GetRawText()}}}"
                    : doc.RootElement.GetRawText();
                spec = new AiToolCallSpec($"block_{index}", ToolPatchScript, payload);
                return true;
            }
            catch
            {
                return false;
            }
        }

        MatchCollection matches = GitConflictHunkRegex().Matches(rawBlock);
        if (matches.Count == 0)
        {
            return false;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartArray("hunks");
            foreach (Match m in matches)
            {
                w.WriteStartObject();
                w.WriteString("search", m.Groups["search"].Value);
                w.WriteString("replace", m.Groups["replace"].Value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        spec = new AiToolCallSpec($"block_{index}", ToolPatchScript, Encoding.UTF8.GetString(ms.ToArray()));
        return true;
    }

    private static StudioBriefUpdateSpec? ParseBriefUpdatesFromObject(JsonElement root)
    {
        if (root.TryGetProperty("briefUpdates", out JsonElement buEl) && buEl.ValueKind == JsonValueKind.Object)
        {
            bool nestedReset = buEl.TryGetProperty("reset", out JsonElement rEl) && rEl.ValueKind == JsonValueKind.True;
            string? nestedPurpose = GetOptionalString(buEl, "purpose");
            IReadOnlyList<string> nestedConf = ReadStringList(buEl, "addConfirmed", 8);
            IReadOnlyList<string> nestedRej = ReadStringList(buEl, "addRejected", 8);
            IReadOnlyList<string> nestedStyle = ReadStringList(buEl, "addStylePreferences", 8);
            var specNested = new StudioBriefUpdateSpec
            {
                Reset = nestedReset,
                Purpose = nestedPurpose,
                AddConfirmed = nestedConf,
                AddRejected = nestedRej,
                AddStylePreferences = nestedStyle
            };
            return specNested.HasAnyChange ? specNested : null;
        }

        bool reset = root.TryGetProperty("resetBrief", out JsonElement rbEl) && rbEl.ValueKind == JsonValueKind.True;
        string? purpose = GetOptionalString(root, "briefPurpose");
        IReadOnlyList<string> confirmed = ReadStringList(root, "addConfirmed", 8);
        IReadOnlyList<string> rejected = ReadStringList(root, "addRejected", 8);
        IReadOnlyList<string> style = ReadStringList(root, "addStylePreferences", 8);
        var spec = new StudioBriefUpdateSpec
        {
            Reset = reset,
            Purpose = purpose,
            AddConfirmed = confirmed,
            AddRejected = rejected,
            AddStylePreferences = style
        };
        return spec.HasAnyChange ? spec : null;
    }

    private static bool TryParseRootObject(string json, out JsonDocument? doc, out string? error)
    {
        doc = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Tool arguments JSON must not be empty.";
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(json, PermissiveJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                doc = null;
                error = "Tool arguments must be a JSON object.";
                return false;
            }
            return true;
        }
        catch (JsonException jex)
        {
            error = $"Invalid tool arguments JSON (possibly truncated): {jex.Message}";
            return false;
        }
    }

    private static string? ExtractObjectOrJsonStringProperty(
        JsonElement obj,
        string objectPropName,
        string stringPropName)
    {
        if (obj.TryGetProperty(objectPropName, out JsonElement primary))
        {
            if (primary.ValueKind == JsonValueKind.Object)
            {
                return primary.GetRawText();
            }

            if (primary.ValueKind == JsonValueKind.String)
            {
                return primary.GetString();
            }
        }

        if (obj.TryGetProperty(stringPropName, out JsonElement fallback))
        {
            if (fallback.ValueKind == JsonValueKind.String)
            {
                return fallback.GetString();
            }

            if (fallback.ValueKind == JsonValueKind.Object)
            {
                return fallback.GetRawText();
            }
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? GetOptionalInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) &&
        el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out int v)
            ? v
            : null;

    private static List<string> ReadStringOrArrayTokens(JsonElement obj, string prop, int maxItems)
    {
        List<string> list = ReadStringList(obj, prop, maxItems);
        if (list.Count > 0 || !obj.TryGetProperty(prop, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return list;
        }

        string? raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        foreach (string part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0)
            {
                list.Add(part);
                if (list.Count >= maxItems)
                {
                    break;
                }
            }
        }

        return list;
    }

    private static List<string> ReadStringList(JsonElement obj, string prop, int maxItems)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(prop, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s))
                {
                    list.Add(s);
                    if (list.Count >= maxItems)
                    {
                        break;
                    }
                }
            }
        }
        return list;
    }

    private static void WriteToolFunctionHeader(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WriteStartObject("function");
        writer.WriteString("name", name);
        writer.WriteString("description", description);
        writer.WriteStartObject("parameters");
        writer.WriteString("type", "object");
        writer.WriteBoolean("additionalProperties", false);
    }

    private static void WriteToolFunctionFooter(Utf8JsonWriter writer)
    {
        writer.WriteEndObject(); // parameters
        writer.WriteEndObject(); // function
        writer.WriteEndObject(); // tool entry
    }

    private static void WriteStringProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteObjectProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "object");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteIntegerProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "integer");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteEnumStringProperty(Utf8JsonWriter writer, string name, string description, IReadOnlyList<string> values)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteString("description", description);
        writer.WriteStartArray("enum");
        foreach (string v in values)
        {
            writer.WriteStringValue(v);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStringArrayProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "array");
        writer.WriteString("description", description);
        writer.WriteStartObject("items");
        writer.WriteString("type", "string");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteRequiredArray(Utf8JsonWriter writer, IReadOnlyList<string> requiredProps)
    {
        writer.WriteStartArray("required");
        foreach (string r in requiredProps)
        {
            writer.WriteStringValue(r);
        }
        writer.WriteEndArray();
    }
}
