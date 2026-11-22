using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum CustomWidgetScriptStatus
{
    Ok = 0,
    Error = 1,
    Timeout = 2,
    OutOfMemory = 3,
    InvalidArgument = 4,
    ExportNotFound = 5,
    EngineUnavailable = 6
}

public enum CustomWidgetScriptCallKind
{
    Lifecycle = 0,
    UiEvent = 1,
    SourceData = 2,
    Tick = 3,
    JobProgress = 4,
    JobResult = 5
}

[Flags]
public enum CustomWidgetScriptEnvFlags
{
    None = 0,
    IdleSeconds = 1 << 0,
    Holidays = 1 << 1,
    SystemMetrics = 1 << 2,
    Device = 1 << 3,
    Weather = 1 << 4,
    Clipboard = 1 << 5,
    Transform = 1 << 6,
    Music = 1 << 7,
    Todo = 1 << 8,
    All = IdleSeconds | Holidays | SystemMetrics | Device | Weather | Clipboard | Transform | Music | Todo
}

public sealed record CustomWidgetCompiledScript(
    byte[] Bytecode,
    string SourceSha256,
    IReadOnlySet<string> ExportedFunctions,
    IReadOnlySet<string> AllExports,
    CustomWidgetScriptEnvFlags ReferencedEnvFlags = CustomWidgetScriptEnvFlags.All)
{
    internal DeviceStateQueryFields ReferencedDeviceFields { get; init; } = DeviceStateQueryFields.All;
}

public sealed record CustomWidgetScriptCompileResult(
    bool IsSuccess,
    CustomWidgetScriptStatus Status,
    CustomWidgetCompiledScript? Compiled,
    string? ErrorMessage = null,
    int? ErrorLine = null);

public sealed record CustomWidgetScriptUiOp(
    string Type,
    string Message = "",
    string Tone = "info",
    string Text = "",
    string Title = "",
    string Body = "",
    string SourceId = "");

public sealed record CustomWidgetScriptEventPayload(
    string Arg = "",
    string Value = "",
    CustomWidgetRecord? Item = null,
    CustomWidgetDropFileContext? Drop = null,
    string Id = "",
    double? X = null,
    double? Y = null,
    int? FromIndex = null,
    int? ToIndex = null,
    IReadOnlyDictionary<string, string>? Args = null,
    IReadOnlyList<CustomWidgetDropFileContext>? Files = null,
    double? NormX = null,
    double? NormY = null,
    bool? IsPressed = null,
    string? Kind = null,
    string? Text = null,
    IReadOnlyList<CustomWidgetDropItemRecord>? Items = null,
    IReadOnlyDictionary<string, JsonElement>? ArgsJson = null);

public sealed class CustomWidgetScriptExecutionInput
{
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement>? StateJson { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Computed { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> Collections { get; init; } =
        new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlySet<string>? SecretKeysToExclude { get; init; }
    public IReadOnlyDictionary<string, string> I18n { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string>? Args { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? ArgsJson { get; init; }
    public CustomWidgetScriptEventPayload? Event { get; init; }
    public DateTimeOffset? ClockOverride { get; init; }
    public double? IdleSecondsOverride { get; init; }
    public string? CultureName { get; init; }
    public string? WidgetId { get; init; }
    public IReadOnlyList<string>? ClockZones { get; init; }
    public CustomWidgetSystemMetricsSnapshot? SystemMetrics { get; init; }
    public CustomWidgetDeviceStateSnapshot? DeviceState { get; init; }
    public CustomWidgetWeatherSnapshot? Weather { get; init; }
    public CustomWidgetMusicSnapshot? Music { get; init; }
    public CustomWidgetTodoSnapshot? Todo { get; init; }
    public bool TodoHasLiveStore { get; init; } = true;
    public bool IsPreview { get; init; }
    public bool IsAmbientPlaying { get; init; }
    public CustomWidgetClipboardSnapshot? Clipboard { get; init; }
    public CustomWidgetTransformResult? TransformResult { get; init; }
    public CustomWidgetScriptCallKind? CallKind { get; init; }
    public string? SourceId { get; init; }
    public string? RawSourcePayload { get; init; }
    public JsonElement? RawJsonPayload { get; init; }
    public bool AllowCopy { get; init; }
    public int MaxCollectionItems { get; init; } = CustomWidgetScriptEngine.MaxCollectionItems;
    public uint TimeoutMs { get; init; } = CustomWidgetScriptEngine.DefaultActionTimeoutMs;
    public nuint MemoryLimitBytes { get; init; } = CustomWidgetScriptEngine.DefaultMemoryLimitBytes;
}

public sealed record CustomWidgetScriptExecutionResult
{
    public required bool IsSuccess { get; init; }
    public required bool Invoked { get; init; }
    public required CustomWidgetScriptStatus Status { get; init; }
    public bool StateChanged { get; init; }
    public IReadOnlyDictionary<string, string> StateSet { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, JsonElement> StateSetJson { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyList<string> StateRemoved { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Computed { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> DirtyCollections { get; init; } =
        new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
    public IReadOnlyList<CustomWidgetScriptUiOp> UiOps { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> DispatchedActions { get; init; } = [];
    public string? DispatchedActionsRawJson { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ErrorLine { get; init; }
}

internal interface IComputedRecordCacheOwner
{
    IReadOnlyList<CustomWidgetRecord> GetOrAddProjectedRecords(
        string normalizedSubPath,
        Func<string, IReadOnlyList<CustomWidgetRecord>> factory);
}

/// <summary>
/// Stateless, zero-idle-memory ES2023 script execution engine backed by <c>deskbox_qjs.dll</c>.
/// Precompiles <c>logic.js</c> into QuickJS bytecode and runs each hook/action inside an
/// isolated context with strict CPU (<c>50ms</c>/<c>100ms</c>) and memory (<c>4 MB</c>) caps.
/// </summary>
public static partial class CustomWidgetScriptEngine
{
    public const int MaxSourceUtf8Bytes = 128 * 1024;
    public const nuint DefaultMemoryLimitBytes = 4u * 1024u * 1024u;
    public const uint DefaultActionTimeoutMs = 50;
    public const uint DefaultInitOrSourceTimeoutMs = 100;
    public const int MaxCollectionItems = 500;

    private static readonly JsonWriterOptions CompactJsonWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Lazy<string> CachedCliPath =
        new(() => CustomWidgetAgentConnectorService.ResolveMcpCommandExecutable());

    [GeneratedRegex(
        @"(?:logic\.js|<input>|<module>):(?<line>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLineRegex();

    public static bool IsNativeEngineAvailable(out string? reason) =>
        CustomWidgetQuickJsInterop.IsAvailable(out reason);

    public static string ComputeSourceSha256(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return string.Empty;
        }

        string normalized = sourceCode.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    public static CustomWidgetScriptCompileResult Compile(
        string? sourceCode,
        string moduleName = "logic.js",
        uint timeoutMs = DefaultInitOrSourceTimeoutMs,
        nuint memoryLimitBytes = DefaultMemoryLimitBytes)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return new CustomWidgetScriptCompileResult(
                false,
                CustomWidgetScriptStatus.InvalidArgument,
                null,
                "logic.js source is empty.");
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(sourceCode);
        if (utf8.Length > MaxSourceUtf8Bytes)
        {
            return new CustomWidgetScriptCompileResult(
                false,
                CustomWidgetScriptStatus.InvalidArgument,
                null,
                $"logic.js exceeds maximum size ({utf8.Length} bytes > {MaxSourceUtf8Bytes} bytes).");
        }

        DeskBoxQjsNativeCompileOutcome native = CustomWidgetQuickJsInterop.CompileModule(
            utf8,
            moduleName,
            timeoutMs,
            memoryLimitBytes);

        CustomWidgetScriptStatus status = MapStatus(native.Status);
        if (status != CustomWidgetScriptStatus.Ok || native.Bytecode.Length == 0)
        {
            return new CustomWidgetScriptCompileResult(
                false,
                status,
                null,
                string.IsNullOrWhiteSpace(native.ErrorMessage) ? "Failed to compile logic.js." : native.ErrorMessage,
                ExtractLineNumber(native.ErrorMessage));
        }

        (HashSet<string> exportedFunctions, HashSet<string> allExports) = ParseExportsJson(native.ExportsJsonUtf8);
        var compiled = new CustomWidgetCompiledScript(
            native.Bytecode,
            ComputeSourceSha256(sourceCode),
            exportedFunctions,
            allExports,
            ComputeReferencedEnvFlags(sourceCode))
        {
            ReferencedDeviceFields = ComputeReferencedDeviceFields(sourceCode)
        };

        return new CustomWidgetScriptCompileResult(true, CustomWidgetScriptStatus.Ok, compiled);
    }

    private static DeviceStateQueryFields ComputeReferencedDeviceFields(string sourceCode)
    {
        if (!sourceCode.Contains("device", StringComparison.Ordinal))
        {
            return DeviceStateQueryFields.None;
        }

        if (sourceCode.Contains("JSON.stringify", StringComparison.Ordinal) ||
            sourceCode.Contains("Object.keys", StringComparison.Ordinal) ||
            sourceCode.Contains("Object.entries", StringComparison.Ordinal) ||
            sourceCode.Contains("Object.values", StringComparison.Ordinal) ||
            sourceCode.Contains("Reflect.", StringComparison.Ordinal))
        {
            return DeviceStateQueryFields.All;
        }

        DeviceStateQueryFields fields = DeviceStateQueryFields.None;
        if (sourceCode.Contains("volume", StringComparison.Ordinal) ||
            sourceCode.Contains("audioVolume", StringComparison.Ordinal) ||
            sourceCode.Contains("isMuted", StringComparison.Ordinal) ||
            sourceCode.Contains("audioMuted", StringComparison.Ordinal) ||
            sourceCode.Contains("muted", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.Volume;
        }
        if (sourceCode.Contains("audioDevice", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.AudioDevice;
        }
        if (sourceCode.Contains("brightness", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.Brightness;
        }
        if (sourceCode.Contains("wifi", StringComparison.Ordinal) ||
            sourceCode.Contains("bluetooth", StringComparison.Ordinal) ||
            sourceCode.Contains("networkConnected", StringComparison.Ordinal) ||
            sourceCode.Contains("networkType", StringComparison.Ordinal) ||
            sourceCode.Contains("online", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.Radios;
        }
        if (sourceCode.Contains("keepAwake", StringComparison.Ordinal) ||
            sourceCode.Contains("caffeine", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.KeepAwake;
        }
        if (sourceCode.Contains("darkTheme", StringComparison.Ordinal) ||
            sourceCode.Contains("isDark", StringComparison.Ordinal) ||
            sourceCode.Contains("themeStatus", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.Theme;
        }
        if (sourceCode.Contains("media", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.Media;
        }
        if (sourceCode.Contains("recycleBin", StringComparison.Ordinal))
        {
            fields |= DeviceStateQueryFields.RecycleBin;
        }

        // Dynamic property access fallback (e.g., env.device[key]) when 'device' is referenced
        // without any static sub-property name appearing in source.
        if (fields == DeviceStateQueryFields.None)
        {
            return DeviceStateQueryFields.All;
        }

        return fields;
    }

    private static CustomWidgetScriptEnvFlags ComputeReferencedEnvFlags(string sourceCode)
    {
        if (sourceCode.Contains("env", StringComparison.Ordinal) &&
            (sourceCode.Contains("JSON.stringify", StringComparison.Ordinal) ||
             sourceCode.Contains("Object.keys", StringComparison.Ordinal) ||
             sourceCode.Contains("Object.entries", StringComparison.Ordinal) ||
             sourceCode.Contains("Object.values", StringComparison.Ordinal) ||
             sourceCode.Contains("Reflect.", StringComparison.Ordinal)))
        {
            return CustomWidgetScriptEnvFlags.All;
        }

        CustomWidgetScriptEnvFlags flags = CustomWidgetScriptEnvFlags.None;
        if (sourceCode.Contains("idleSeconds", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.IdleSeconds;
        }
        if (sourceCode.Contains("dayType", StringComparison.Ordinal) ||
            sourceCode.Contains("isHoliday", StringComparison.Ordinal) ||
            sourceCode.Contains("isWorkday", StringComparison.Ordinal) ||
            sourceCode.Contains("holidayName", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Holidays;
        }
        if (sourceCode.Contains("sys", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.SystemMetrics;
        }
        if (sourceCode.Contains("device", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Device;
        }
        if (sourceCode.Contains("weather", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Weather;
        }
        if (sourceCode.Contains("clipboard", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Clipboard;
        }
        if (sourceCode.Contains("transform", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Transform;
        }
        if (sourceCode.Contains("music", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Music;
        }
        if (sourceCode.Contains("todo", StringComparison.Ordinal))
        {
            flags |= CustomWidgetScriptEnvFlags.Todo;
        }

        return flags;
    }

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_payloadBuffer;
    private const int MaxPooledPayloadBufferCapacity = 64 * 1024;

    public static CustomWidgetScriptExecutionResult Execute(
        CustomWidgetCompiledScript compiled,
        string functionName,
        CustomWidgetScriptExecutionInput input,
        bool requireExport = true)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(functionName))
        {
            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = false,
                Invoked = false,
                Status = CustomWidgetScriptStatus.InvalidArgument,
                ErrorMessage = "Script function name cannot be empty."
            };
        }

        if (!requireExport && !compiled.ExportedFunctions.Contains(functionName))
        {
            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = true,
                Invoked = false,
                Status = CustomWidgetScriptStatus.Ok,
                Computed = CloneComputedDictionary(input.Computed)
            };
        }

        ArrayBufferWriter<byte> buffer = t_payloadBuffer ??= new ArrayBufferWriter<byte>(4096);
        buffer.Clear();
        WritePayloadJson(buffer, input, compiled.ReferencedEnvFlags);

        DeskBoxQjsNativeInvokeOutcome outcome = CustomWidgetQuickJsInterop.InvokeBytecodePooled(
            compiled.Bytecode,
            functionName,
            requireExport,
            buffer.WrittenSpan,
            input.TimeoutMs > 0 ? input.TimeoutMs : DefaultActionTimeoutMs,
            input.MemoryLimitBytes > 0 ? input.MemoryLimitBytes : DefaultMemoryLimitBytes,
            out int resultByteLength);

        if (buffer.Capacity > MaxPooledPayloadBufferCapacity)
        {
            t_payloadBuffer = new ArrayBufferWriter<byte>(4096);
        }
        else
        {
            buffer.Clear();
        }

        try
        {
            CustomWidgetScriptStatus status = MapStatus(outcome.Status);
            if (status != CustomWidgetScriptStatus.Ok)
            {
                return new CustomWidgetScriptExecutionResult
                {
                    IsSuccess = false,
                    Invoked = false,
                    Status = status,
                    Computed = CloneComputedDictionary(input.Computed),
                    ErrorMessage = string.IsNullOrWhiteSpace(outcome.ErrorMessage)
                        ? $"Script error in '{functionName}'."
                        : outcome.ErrorMessage,
                    ErrorLine = ExtractLineNumber(outcome.ErrorMessage)
                };
            }

            ReadOnlyMemory<byte> resultMem = resultByteLength > 0
                ? outcome.ResultJsonUtf8.AsMemory(0, resultByteLength)
                : ReadOnlyMemory<byte>.Empty;
            return ParseExecutionResultJson(resultMem, input.Computed);
        }
        finally
        {
            if (outcome.ResultJsonUtf8.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(outcome.ResultJsonUtf8);
            }
        }
    }

    public static string ResolveComputedToken(
        string path,
        IReadOnlyDictionary<string, JsonElement> computed)
    {
        if (string.IsNullOrWhiteSpace(path) || computed.Count == 0)
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        int dotIndex = trimmed.IndexOf('.');
        int bracketIndex = trimmed.IndexOf('[');
        int splitAt = dotIndex >= 0 && bracketIndex >= 0
            ? Math.Min(dotIndex, bracketIndex)
            : Math.Max(dotIndex, bracketIndex);

        string rootKey = splitAt >= 0 ? trimmed[..splitAt] : trimmed;
        if (!computed.TryGetValue(rootKey, out JsonElement current))
        {
            return string.Empty;
        }

        if (splitAt < 0)
        {
            return JsonElementToDisplayString(current);
        }

        string remainder = trimmed[splitAt..];
        int cursor = 0;
        while (cursor < remainder.Length)
        {
            if (remainder[cursor] == '.')
            {
                cursor++;
                int nextDot = remainder.IndexOf('.', cursor);
                int nextBracket = remainder.IndexOf('[', cursor);
                int end = nextDot >= 0 && nextBracket >= 0
                    ? Math.Min(nextDot, nextBracket)
                    : nextDot >= 0 ? nextDot : nextBracket >= 0 ? nextBracket : remainder.Length;
                string propName = remainder[cursor..end];
                if (propName.Length == 0)
                {
                    return string.Empty;
                }

                if (string.Equals(propName, "length", StringComparison.Ordinal) ||
                    string.Equals(propName, "count", StringComparison.Ordinal))
                {
                    if (current.ValueKind == JsonValueKind.Array)
                    {
                        return current.GetArrayLength().ToString(CultureInfo.InvariantCulture);
                    }
                    if (current.ValueKind == JsonValueKind.String)
                    {
                        return (current.GetString()?.Length ?? 0).ToString(CultureInfo.InvariantCulture);
                    }
                }

                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(propName, out current))
                {
                    return string.Empty;
                }

                cursor = end;
            }
            else if (remainder[cursor] == '[')
            {
                int closeBracket = remainder.IndexOf(']', cursor + 1);
                if (closeBracket < 0)
                {
                    return string.Empty;
                }

                string idxStr = remainder.Substring(cursor + 1, closeBracket - cursor - 1).Trim();
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                    current.ValueKind != JsonValueKind.Array)
                {
                    return string.Empty;
                }

                int len = current.GetArrayLength();
                if (index < 0)
                {
                    index = len + index;
                }
                if (index < 0 || index >= len)
                {
                    return string.Empty;
                }

                current = current[index];
                cursor = closeBracket + 1;
            }
            else
            {
                return string.Empty;
            }
        }

        return JsonElementToDisplayString(current);
    }

    public static IReadOnlyList<CustomWidgetRecord> ProjectComputedCollection(
        string? itemsBinding,
        IReadOnlyDictionary<string, JsonElement> computed,
        IReadOnlyDictionary<string, JsonElement>? stateJson = null)
    {
        if (string.IsNullOrWhiteSpace(itemsBinding))
        {
            return [];
        }

        string trimmed = itemsBinding.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        bool isStateRef = false;
        string path = string.Empty;
        if (trimmed.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase))
        {
            path = trimmed["$computed.".Length..];
        }
        else if (trimmed.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
        {
            path = trimmed["computed.".Length..];
        }
        else if (trimmed.StartsWith("$state.", StringComparison.OrdinalIgnoreCase))
        {
            isStateRef = true;
            path = trimmed["$state.".Length..];
        }
        else if (trimmed.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            isStateRef = true;
            path = trimmed["state.".Length..];
        }

        IReadOnlyDictionary<string, JsonElement> sourceMap = isStateRef && stateJson is not null
            ? stateJson
            : computed;

        if (string.IsNullOrWhiteSpace(path) || sourceMap.Count == 0)
        {
            return [];
        }

        if (!isStateRef && sourceMap is IComputedRecordCacheOwner cacheOwner)
        {
            return cacheOwner.GetOrAddProjectedRecords(
                path,
                subPath => ProjectRecordsFromSourceMap(subPath, sourceMap));
        }

        return ProjectRecordsFromSourceMap(path, sourceMap);
    }

    private static IReadOnlyList<CustomWidgetRecord> ProjectRecordsFromSourceMap(
        string path,
        IReadOnlyDictionary<string, JsonElement> sourceMap)
    {
        int dotIndex = path.IndexOf('.');
        string rootKey = dotIndex >= 0 ? path[..dotIndex] : path;
        if (!sourceMap.TryGetValue(rootKey, out JsonElement element))
        {
            return [];
        }

        if (dotIndex >= 0)
        {
            ReadOnlySpan<char> subPath = path.AsSpan(dotIndex + 1);
            foreach (Range segRange in subPath.Split('.'))
            {
                ReadOnlySpan<char> seg = subPath[segRange];
                if (seg.IsEmpty)
                {
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(seg, out element))
                {
                    return [];
                }
            }
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int count = Math.Min(MaxCollectionItems, element.GetArrayLength());
        var records = new List<CustomWidgetRecord>(count);
        int idx = 0;
        foreach (JsonElement itemEl in element.EnumerateArray())
        {
            if (idx >= MaxCollectionItems)
            {
                break;
            }

            var rec = new CustomWidgetRecord
            {
                Id = $"comp-{idx.ToString(CultureInfo.InvariantCulture)}"
            };

            if (itemEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in itemEl.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "id", StringComparison.Ordinal))
                    {
                        string val = JsonElementToDisplayString(prop.Value);
                        if (!string.IsNullOrEmpty(val))
                        {
                            rec.Id = val;
                        }
                    }

                    if (prop.Value.ValueKind != JsonValueKind.Undefined)
                    {
                        rec.FieldsJson[prop.Name] = prop.Value.Clone();
                    }
                }
            }
            else if (itemEl.ValueKind != JsonValueKind.Undefined)
            {
                JsonElement clonedScalar = itemEl.Clone();
                rec.FieldsJson["value"] = clonedScalar;
                rec.FieldsJson["text"] = clonedScalar;
                rec.FieldsJson["title"] = clonedScalar;
            }

            records.Add(rec);
            idx++;
        }

        return records;
    }

    public static string JsonElementToDisplayString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Array => element.GetRawText(),
        JsonValueKind.Object => element.GetRawText(),
        _ => string.Empty
    };

    private static readonly JsonElement EmptyStringJsonElement = ParseStaticJsonLiteral("\"\"");
    private static readonly JsonElement TrueJsonElement = ParseStaticJsonLiteral("true");
    private static readonly JsonElement FalseJsonElement = ParseStaticJsonLiteral("false");
    private static readonly JsonElement ZeroJsonElement = ParseStaticJsonLiteral("0");

    private static JsonElement ParseStaticJsonLiteral(string rawJson)
    {
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }

    public static JsonElement CreateStringJsonElement(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyStringJsonElement;
        }

        return JsonSerializer.SerializeToElement(value, CustomWidgetStoreJsonContext.Default.String);
    }

    public static JsonElement CreateBooleanJsonElement(bool value) =>
        value ? TrueJsonElement : FalseJsonElement;

    public static JsonElement CreateNumberJsonElement(double value)
    {
        if (!double.IsFinite(value))
        {
            return EmptyStringJsonElement;
        }

        if (value == 0.0 && !double.IsNegative(value))
        {
            return ZeroJsonElement;
        }

        return JsonSerializer.SerializeToElement(value, CustomWidgetStoreJsonContext.Default.Double);
    }

    public static bool TryParseStructuredJsonElement(string? rawJson, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        string trimmed = rawJson.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            element = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FormatCallKind(CustomWidgetScriptCallKind? callKind) => callKind switch
    {
        CustomWidgetScriptCallKind.Lifecycle => "lifecycle",
        CustomWidgetScriptCallKind.UiEvent => "uiEvent",
        CustomWidgetScriptCallKind.SourceData => "sourceData",
        CustomWidgetScriptCallKind.Tick => "tick",
        CustomWidgetScriptCallKind.JobProgress => "jobProgress",
        CustomWidgetScriptCallKind.JobResult => "jobResult",
        _ => null
    };

    private static void WritePayloadJson(
        IBufferWriter<byte> buffer,
        CustomWidgetScriptExecutionInput input,
        CustomWidgetScriptEnvFlags envFlags = CustomWidgetScriptEnvFlags.All)
    {
        using var writer = new Utf8JsonWriter(buffer, CompactJsonWriterOptions);
        DateTimeOffset now = (input.ClockOverride ?? DateTimeOffset.Now).ToLocalTime();
        string locale = !string.IsNullOrWhiteSpace(input.CultureName)
            ? input.CultureName!
            : CultureInfo.CurrentUICulture.Name;

        writer.WriteStartObject();
        writer.WriteBoolean("allowCopy", input.AllowCopy);
        writer.WriteNumber("maxCollectionItems", Math.Clamp(input.MaxCollectionItems, 1, MaxCollectionItems));
        if (FormatCallKind(input.CallKind) is { } callKindStr)
        {
            writer.WriteString("callKind", callKindStr);
        }

        writer.WritePropertyName("state");
        writer.WriteStartObject();
        if (input.StateJson is { Count: > 0 } stateJson)
        {
            foreach ((string k, string v) in input.State)
            {
                if (stateJson.TryGetValue(k, out JsonElement el) &&
                    el.ValueKind != JsonValueKind.Undefined &&
                    string.Equals(JsonElementToDisplayString(el), v ?? string.Empty, StringComparison.Ordinal))
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
                else
                {
                    writer.WriteString(k, v ?? string.Empty);
                }
            }

            foreach ((string k, JsonElement el) in stateJson)
            {
                if (!input.State.ContainsKey(k) && el.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
            }
        }
        else
        {
            foreach ((string k, string v) in input.State)
            {
                writer.WriteString(k, v ?? string.Empty);
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("computed");
        writer.WriteStartObject();
        foreach ((string k, JsonElement el) in input.Computed)
        {
            if (el.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName(k);
                el.WriteTo(writer);
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        foreach ((string k, string v) in input.Settings)
        {
            if (input.SecretKeysToExclude is not null && input.SecretKeysToExclude.Contains(k))
            {
                continue;
            }
            if (string.Equals(k, "__trustedTargets", StringComparison.Ordinal))
            {
                continue;
            }
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteBoolean(k, true);
            }
            else if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteBoolean(k, false);
            }
            else
            {
                writer.WriteString(k, v ?? string.Empty);
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("i18n");
        writer.WriteStartObject();
        foreach ((string k, string v) in input.I18n)
        {
            writer.WriteString(k, v ?? string.Empty);
        }
        writer.WriteEndObject();

        writer.WritePropertyName("collections");
        writer.WriteStartObject();
        foreach ((string colName, List<CustomWidgetRecord> items) in input.Collections)
        {
            writer.WritePropertyName(colName);
            writer.WriteStartArray();
            int limit = Math.Min(MaxCollectionItems, items.Count);
            for (int i = 0; i < limit; i++)
            {
                WriteRecordJson(writer, items[i]);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WritePropertyName("args");
        writer.WriteStartObject();
        if (input.Args is { Count: > 0 })
        {
            foreach ((string k, string v) in input.Args)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    if (input.ArgsJson is not null &&
                        input.ArgsJson.TryGetValue(k, out JsonElement argJson) &&
                        argJson.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                    {
                        writer.WritePropertyName(k);
                        argJson.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteString(k, v ?? string.Empty);
                    }
                }
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("event");
        writer.WriteStartObject();
        writer.WriteString("arg", input.Event?.Arg ?? string.Empty);
        writer.WriteString("value", input.Event?.Value ?? string.Empty);
        writer.WriteString("id", input.Event?.Id ?? string.Empty);
        string? effectiveKind = !string.IsNullOrWhiteSpace(input.Event?.Kind)
            ? input.Event!.Kind
            : (input.Event?.Drop is not null || input.Event?.Files is { Count: > 0 })
                ? "files"
                : null;
        if (!string.IsNullOrEmpty(effectiveKind))
        {
            writer.WriteString("kind", effectiveKind);
        }
        if (input.Event?.Text is not null || !string.IsNullOrEmpty(effectiveKind))
        {
            writer.WriteString("text", input.Event?.Text ?? input.Event?.Value ?? string.Empty);
        }
        if (input.Event?.Items is { } evItems)
        {
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            int itemLimit = Math.Min(200, evItems.Count);
            for (int i = 0; i < itemLimit; i++)
            {
                CustomWidgetDropItemRecord item = evItems[i];
                writer.WriteStartObject();
                writer.WriteString("id", item.Id ?? string.Empty);
                writer.WriteString("title", item.Title ?? string.Empty);
                writer.WriteString("text", item.Text ?? string.Empty);
                writer.WriteString("body", item.Body ?? string.Empty);
                writer.WriteString("url", item.Url ?? string.Empty);
                writer.WriteBoolean("completed", item.IsCompleted);
                writer.WriteString("source", item.Source ?? string.Empty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (input.Event?.X is { } evX)
        {
            writer.WriteNumber("x", evX);
        }
        if (input.Event?.Y is { } evY)
        {
            writer.WriteNumber("y", evY);
        }
        if (input.Event?.NormX is { } evNormX)
        {
            writer.WriteNumber("normX", evNormX);
        }
        if (input.Event?.NormY is { } evNormY)
        {
            writer.WriteNumber("normY", evNormY);
        }
        if (input.Event?.IsPressed is { } evIsPressed)
        {
            writer.WriteBoolean("isPressed", evIsPressed);
        }
        if (input.Event?.FromIndex is { } evFrom)
        {
            writer.WriteNumber("fromIndex", evFrom);
        }
        if (input.Event?.ToIndex is { } evTo)
        {
            writer.WriteNumber("toIndex", evTo);
        }
        if (input.Event?.Item is { } evItem)
        {
            writer.WritePropertyName("item");
            WriteFlattenedRecordJson(writer, evItem);
        }
        else
        {
            writer.WriteNull("item");
        }

        if (input.Event?.Drop is { } evDrop)
        {
            writer.WritePropertyName("drop");
            WriteDropFileJson(writer, evDrop);
        }
        else
        {
            writer.WriteNull("drop");
        }

        IReadOnlyList<CustomWidgetDropFileContext>? evFiles = input.Event?.Files;
        if (evFiles is { Count: > 0 })
        {
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            int limit = Math.Min(200, evFiles.Count);
            for (int i = 0; i < limit; i++)
            {
                WriteDropFileJson(writer, evFiles[i]);
            }
            writer.WriteEndArray();
        }
        else if (input.Event?.Drop is { } singleDrop)
        {
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            WriteDropFileJson(writer, singleDrop);
            writer.WriteEndArray();
        }

        if (input.Event?.Args is { Count: > 0 } evArgs)
        {
            IReadOnlyDictionary<string, JsonElement>? evArgsJson = input.Event.ArgsJson ?? input.ArgsJson;
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach ((string k, string v) in evArgs)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    if (evArgsJson is not null &&
                        evArgsJson.TryGetValue(k, out JsonElement evArgJson) &&
                        evArgJson.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Array or JsonValueKind.Object)
                    {
                        writer.WritePropertyName(k);
                        evArgJson.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteString(k, v ?? string.Empty);
                    }
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WritePropertyName("env");
        writer.WriteStartObject();
        writer.WriteString("widgetId", input.WidgetId ?? string.Empty);
        writer.WriteString("cliPath", CachedCliPath.Value);
        writer.WriteNumber("nowMs", now.ToUnixTimeMilliseconds());
        string isoDateStr = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        writer.WriteString("isoDate", isoDateStr);
        writer.WriteString("date", isoDateStr);
        writer.WriteString("isoDateTime", now.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("time", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        writer.WriteString("timeZone", TimeZoneInfo.Local.Id);
        writer.WriteNumber("dayOfWeek", (int)now.DayOfWeek);
        writer.WriteString("locale", locale);
        writer.WriteBoolean("isPreview", input.IsPreview);
        writer.WriteBoolean("ambientPlaying", input.IsAmbientPlaying);

        if ((envFlags & CustomWidgetScriptEnvFlags.IdleSeconds) != 0)
        {
            double? probedIdleSeconds = input.IdleSecondsOverride ??
                                        (input.ClockOverride is null
                                            ? PomodoroCapabilityProvider.ProbeKeyboardMouseIdleSeconds()
                                            : 0.0);
            writer.WriteNumber("idleSeconds", Math.Round(Math.Max(0.0, probedIdleSeconds ?? 0.0), 2));
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Holidays) != 0)
        {
            CultureInfo holidayCulture = CultureInfo.CurrentUICulture;
            if (!string.IsNullOrWhiteSpace(locale))
            {
                try
                {
                    holidayCulture = CultureInfo.GetCultureInfo(locale);
                }
                catch (CultureNotFoundException)
                {
                }
            }

            DateOnly localDate = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
            bool prcHolidaysApplicable = GlanceHolidayMarkService.IsApplicable(holidayCulture);
            CustomWidgetHolidayDayType dayType = prcHolidaysApplicable
                ? CustomWidgetHolidaySchedule.GetDayType(localDate)
                : (localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? CustomWidgetHolidayDayType.Weekend
                    : CustomWidgetHolidayDayType.Ordinary);
            string dayTypeText = dayType switch
            {
                CustomWidgetHolidayDayType.StatutoryHoliday => "holiday",
                CustomWidgetHolidayDayType.SwapWorkday => "workday_swap",
                CustomWidgetHolidayDayType.Weekend => "weekend",
                _ => "workday"
            };
            writer.WriteString("dayType", dayTypeText);
            writer.WriteBoolean("isHoliday", dayType == CustomWidgetHolidayDayType.StatutoryHoliday);
            writer.WriteBoolean("isWorkday", dayType is CustomWidgetHolidayDayType.Ordinary or CustomWidgetHolidayDayType.SwapWorkday);
            if (prcHolidaysApplicable &&
                CustomWidgetHolidaySchedule.TryGetHolidayAt(localDate, out CustomWidgetHolidayEntry activeHoliday))
            {
                writer.WriteString("holidayName", CustomWidgetHolidaySchedule.GetLocalizedName(activeHoliday, locale));
            }
        }

        if (input.ClockZones is { Count: > 0 })
        {
            double scrubHours = 0;
            bool hasScrubOffset = false;
            if (input.State.TryGetValue("scrubHours", out string? rawScrub) &&
                double.TryParse(rawScrub, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedScrub) &&
                !double.IsNaN(parsedScrub) &&
                !double.IsInfinity(parsedScrub) &&
                Math.Abs(parsedScrub) > 1e-9 &&
                Math.Abs(parsedScrub) <= 24 * 30)
            {
                scrubHours = parsedScrub;
                hasScrubOffset = true;
            }
            else if (input.StateJson is not null &&
                     input.StateJson.TryGetValue("scrubHours", out JsonElement scrubEl) &&
                     scrubEl.ValueKind == JsonValueKind.Number &&
                     scrubEl.TryGetDouble(out double jsonScrub) &&
                     !double.IsNaN(jsonScrub) &&
                     !double.IsInfinity(jsonScrub) &&
                     Math.Abs(jsonScrub) > 1e-9 &&
                     Math.Abs(jsonScrub) <= 24 * 30)
            {
                scrubHours = jsonScrub;
                hasScrubOffset = true;
            }

            DateTimeOffset scrubbedInstant = hasScrubOffset ? now.AddHours(scrubHours) : now;
            writer.WritePropertyName("clockZones");
            writer.WriteStartObject();
            var seenZones = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawZone in input.ClockZones)
            {
                string zoneId = rawZone?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(zoneId) ||
                    string.Equals(zoneId, "none", StringComparison.OrdinalIgnoreCase) ||
                    !seenZones.Add(zoneId))
                {
                    continue;
                }

                (int offsetMinutes, string isoDate, double hourFloat, string phase) =
                    CustomWidgetUtilityEngine.ResolveTimeZoneSnapshot(zoneId, now);
                writer.WritePropertyName(zoneId);
                writer.WriteStartObject();
                writer.WriteNumber("offsetMinutes", offsetMinutes);
                writer.WriteString("isoDate", isoDate);
                writer.WriteNumber("hourFloat", Math.Round(hourFloat, 4));
                writer.WriteString("phase", phase);
                if (hasScrubOffset)
                {
                    (int scrubbedOffsetMinutes, string scrubbedIsoDate, _, _) =
                        CustomWidgetUtilityEngine.ResolveTimeZoneSnapshot(zoneId, scrubbedInstant);
                    writer.WriteNumber("scrubbedOffsetMinutes", scrubbedOffsetMinutes);
                    writer.WriteString("scrubbedIsoDate", scrubbedIsoDate);
                    writer.WriteString(
                        "scrubbedFormatted24h",
                        CustomWidgetUtilityEngine.ResolveTimeZoneFormatted(zoneId, "HH:mm", now, locale, scrubHours));
                    writer.WriteString(
                        "scrubbedFormatted12h",
                        CustomWidgetUtilityEngine.ResolveTimeZoneFormatted(zoneId, "h:mm tt", now, locale, scrubHours));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        CustomWidgetSystemMetricsSnapshot sys = input.SystemMetrics ?? CustomWidgetSystemMetricsSnapshot.Empty;
        if ((envFlags & CustomWidgetScriptEnvFlags.SystemMetrics) != 0)
        {
            string memSummaryText = sys.ResolveToken("memory.summaryText", input.Settings, locale);
            string gpuVramSummaryText = sys.ResolveToken("gpu.vramSummary", input.Settings, locale);
            string diskSummaryText = sys.ResolveToken("disk.summary", input.Settings, locale);
            string netDownText = sys.ResolveToken("net.downText", input.Settings, locale);
            string netUpText = sys.ResolveToken("net.upText", input.Settings, locale);
            string netSummaryText = sys.ResolveToken("net.summaryText", input.Settings, locale);
            string batteryStatusText = sys.ResolveToken("battery.statusText", input.Settings, locale);
            string uptimeText = sys.ResolveToken("uptime.text", input.Settings, locale);
            double selectedDiskPct = double.TryParse(
                sys.ResolveToken("disk.percent", input.Settings, locale),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedDiskPct)
                ? parsedDiskPct
                : Math.Round(sys.DiskPrimaryPercent);
            double selectedDiskFreeGb = double.TryParse(
                sys.ResolveToken("disk.freeGb", input.Settings, locale),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedDiskFree)
                ? parsedDiskFree
                : Math.Round(sys.DiskPrimaryFreeGb);
            double selectedDiskTotalGb = double.TryParse(
                sys.ResolveToken("disk.totalGb", input.Settings, locale),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsedDiskTotal)
                ? parsedDiskTotal
                : Math.Round(sys.DiskPrimaryTotalGb);

            writer.WritePropertyName("sys");
            writer.WriteStartObject();
            writer.WriteString("cpuPercent", sys.ResolveToken("cpu.percent", input.Settings, locale));
            writer.WriteString("memoryPercent", sys.ResolveToken("memory.percent", input.Settings, locale));
            writer.WriteString("memoryUsedGb", sys.ResolveToken("memory.usedGb", input.Settings, locale));
            writer.WriteString("memoryTotalGb", sys.ResolveToken("memory.totalGb", input.Settings, locale));
            writer.WriteString("memorySummary", memSummaryText);
            writer.WriteString("gpuPercent", sys.ResolveToken("gpu.percent", input.Settings, locale));
            writer.WriteString("gpuName", sys.GpuName ?? "GPU");
            writer.WriteString("gpuVramSummary", gpuVramSummaryText);
            writer.WriteString("diskPercent", sys.ResolveToken("disk.percent", input.Settings, locale));
            writer.WriteString("diskSummary", diskSummaryText);
            writer.WriteString("netDown", netDownText);
            writer.WriteString("netUp", netUpText);
            writer.WriteString("batteryPercent", sys.ResolveToken("battery.percent", input.Settings, locale));
            writer.WriteString("batteryStatus", batteryStatusText);
            writer.WriteString("uptime", uptimeText);

            writer.WritePropertyName("cpu");
            writer.WriteStartObject();
            writer.WriteNumber("percent", Math.Round(sys.CpuPercent, 1));
            writer.WriteNumber("percentExact", Math.Round(sys.CpuPercent, 1));
            writer.WriteEndObject();

            writer.WritePropertyName("memory");
            writer.WriteStartObject();
            writer.WriteNumber("percent", Math.Round(sys.MemoryPercent, 1));
            writer.WriteNumber("usedGb", Math.Round(sys.MemoryUsedGb, 1));
            writer.WriteNumber("totalGb", Math.Round(sys.MemoryTotalGb, 1));
            writer.WriteString("summaryText", memSummaryText);
            writer.WriteString("summary", memSummaryText);
            writer.WriteEndObject();

            writer.WritePropertyName("gpu");
            writer.WriteStartObject();
            writer.WriteString("name", sys.GpuName ?? "GPU");
            writer.WriteNumber("percent", Math.Round(sys.GpuPercent, 1));
            writer.WriteNumber("vramUsedGb", Math.Round(sys.GpuVramUsedGb, 1));
            writer.WriteNumber("vramTotalGb", Math.Round(sys.GpuVramTotalGb, 1));
            writer.WriteNumber("vramPercent", Math.Round(sys.GpuVramPercent, 1));
            writer.WriteString("vramSummary", gpuVramSummaryText);
            writer.WriteEndObject();

            writer.WritePropertyName("disk");
            writer.WriteStartObject();
            writer.WriteNumber("percent", selectedDiskPct);
            writer.WriteNumber("freeGb", selectedDiskFreeGb);
            writer.WriteNumber("totalGb", selectedDiskTotalGb);
            writer.WriteString("name", sys.ResolveToken("disk.name", input.Settings, locale));
            writer.WriteString("summary", diskSummaryText);
            writer.WriteEndObject();

            writer.WritePropertyName("net");
            writer.WriteStartObject();
            writer.WriteString("downText", netDownText);
            writer.WriteString("upText", netUpText);
            writer.WriteString("summaryText", netSummaryText);
            writer.WriteEndObject();

            writer.WritePropertyName("battery");
            writer.WriteStartObject();
            writer.WriteNumber("percent", sys.BatteryPercent);
            writer.WriteBoolean("hasBattery", sys.HasBattery);
            writer.WriteBoolean("onAc", sys.BatteryOnAc);
            writer.WriteString("statusText", batteryStatusText);
            writer.WriteEndObject();

            writer.WritePropertyName("uptime");
            writer.WriteStartObject();
            writer.WriteString("text", uptimeText);
            writer.WriteNumber("ms", sys.UptimeMs);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Device) != 0)
        {
            CustomWidgetDeviceStateSnapshot dev = input.DeviceState ?? CustomWidgetDeviceStateSnapshot.Empty;
            writer.WritePropertyName("device");
            writer.WriteStartObject();
            string vol = dev.ResolveToken("volume", locale);
            string isMuted = dev.ResolveToken("isMuted", locale);
            string netConnected = dev.ResolveToken("networkConnected", locale);
            writer.WriteString("volume", vol);
            writer.WriteString("audioVolume", vol);
            writer.WriteString("isMuted", isMuted);
            writer.WriteString("audioMuted", isMuted);
            writer.WriteString("audioDeviceName", dev.ResolveToken("audioDeviceName", locale));
            writer.WriteString("brightness", dev.ResolveToken("brightness", locale));
            writer.WriteString("wifiEnabled", dev.ResolveToken("wifiEnabled", locale));
            writer.WriteString("wifiStatus", dev.ResolveToken("wifiStatus", locale));
            writer.WriteString("bluetoothEnabled", dev.ResolveToken("bluetoothEnabled", locale));
            writer.WriteString("bluetoothStatus", dev.ResolveToken("bluetoothStatus", locale));
            writer.WriteString("networkConnected", netConnected);
            writer.WriteString("networkType", dev.ResolveToken("wifiStatus", locale));
            writer.WriteString("keepAwake", dev.ResolveToken("keepAwake", locale));
            writer.WriteString("batteryPercent", sys.ResolveToken("battery.percent", input.Settings, locale));
            writer.WriteString("isCharging", sys.BatteryOnAc ? "true" : "false");
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Weather) != 0)
        {
            CustomWidgetWeatherSnapshot weather = input.Weather ?? CustomWidgetWeatherSnapshot.Empty;
            writer.WritePropertyName("weather");
            writer.WriteStartObject();
            writer.WriteBoolean("hasData", weather.HasData);
            writer.WriteString("city", weather.ResolveToken("city", locale));
            writer.WriteString("temp", weather.ResolveToken("temp", locale));
            writer.WriteNumber("tempNum", weather.Temperature);
            writer.WriteString("feelsLike", weather.ResolveToken("feelsLike", locale));
            writer.WriteNumber("feelsLikeNum", weather.ApparentTemperature);
            writer.WriteString("high", weather.ResolveToken("high", locale));
            writer.WriteNumber("highNum", weather.HighTemperature);
            writer.WriteString("low", weather.ResolveToken("low", locale));
            writer.WriteNumber("lowNum", weather.LowTemperature);
            writer.WriteString("condition", weather.ResolveToken("condition", locale));
            writer.WriteString("icon", weather.ResolveToken("icon", locale));
            writer.WriteString("humidity", weather.ResolveToken("humidity", locale));
            writer.WriteNumber("humidityNum", weather.Humidity);
            writer.WriteString("wind", weather.ResolveToken("wind", locale));
            writer.WriteNumber("windSpeedKmh", weather.WindSpeedKmh);
            writer.WriteString("precip", weather.ResolveToken("precip", locale));
            writer.WriteNumber("precipNum", weather.PrecipitationProbability);
            writer.WriteString("uv", weather.ResolveToken("uv", locale));
            writer.WriteNumber("uvNum", weather.UvIndex);
            writer.WriteNumber("weatherCode", weather.WeatherCode);
            writer.WriteBoolean("isDay", weather.IsDay);
            writer.WriteString("sunrise", weather.Sunrise);
            writer.WriteString("sunset", weather.Sunset);
            writer.WriteString("updatedAt", weather.UpdatedAtText);
            writer.WriteString("status", weather.Status);
            writer.WriteString("error", weather.ErrorMessage ?? string.Empty);
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Clipboard) != 0)
        {
            CustomWidgetClipboardSnapshot clip = input.Clipboard ?? CustomWidgetClipboardSnapshot.Empty;
            writer.WritePropertyName("clipboard");
            writer.WriteStartObject();
            writer.WriteBoolean("hasText", clip.HasText);
            writer.WriteString("kind", clip.Kind);
            writer.WriteString("kindLabel", clip.ResolveToken("kindLabel", locale));
            writer.WriteString("preview", clip.Preview);
            writer.WriteString("text", clip.Text);
            writer.WriteNumber("length", clip.Length);
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Transform) != 0)
        {
            CustomWidgetTransformResult? tr = input.TransformResult;
            writer.WritePropertyName("transform");
            writer.WriteStartObject();
            writer.WriteBoolean("succeeded", tr?.Succeeded ?? false);
            writer.WriteString("status", tr?.ResolveToken("status") ?? string.Empty);
            writer.WriteString("operation", tr?.Operation ?? string.Empty);
            writer.WriteString("jwtState", tr?.JwtState ?? string.Empty);
            writer.WriteString("error", tr?.ErrorMessage ?? string.Empty);
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Music) != 0)
        {
            CustomWidgetMusicSnapshot music = input.Music ?? CustomWidgetMusicSnapshot.Empty;
            writer.WritePropertyName("music");
            writer.WriteStartObject();
            writer.WriteBoolean("hasSession", music.HasSession);
            writer.WriteBoolean("isPlaying", music.IsPlaying);
            writer.WriteString("status", music.Status);
            writer.WriteString("title", music.Title);
            writer.WriteString("artist", music.Artist);
            writer.WriteString("album", music.Album);
            writer.WriteString("sourceApp", music.SourceApp);
            writer.WriteString("coverPath", music.CoverPath);
            writer.WriteNumber("positionSeconds", music.PositionSeconds);
            writer.WriteNumber("durationSeconds", music.DurationSeconds);
            writer.WriteString("positionText", music.PositionText);
            writer.WriteString("durationText", music.DurationText);
            writer.WriteNumber("progressPercent", music.ProgressPercent);
            writer.WriteEndObject();
        }

        if ((envFlags & CustomWidgetScriptEnvFlags.Todo) != 0)
        {
            CustomWidgetTodoSnapshot todo = input.Todo ?? CustomWidgetTodoSnapshot.Empty;
            writer.WritePropertyName("todo");
            writer.WriteStartObject();
            writer.WriteBoolean("hasLiveStore", input.TodoHasLiveStore);
            writer.WriteNumber("totalCount", todo.TotalCount);
            writer.WriteNumber("pendingCount", todo.PendingCount);
            writer.WriteNumber("completedCount", todo.CompletedCount);
            writer.WriteNumber("todayPendingCount", todo.TodayPendingCount);
            writer.WriteNumber("todayCompletedCount", todo.TodayCompletedCount);
            writer.WriteNumber("overdueCount", todo.OverdueCount);
            writer.WriteNumber("importantPendingCount", todo.ImportantPendingCount);
            writer.WriteNumber("completionPercent", todo.CompletionPercent);
            writer.WriteString("nextTaskTitle", todo.NextTaskTitle);
            writer.WriteString("nextTaskDueText", todo.NextTaskDueText);
            writer.WriteString("summary", todo.Summary);
            writer.WriteEndObject();
        }
        writer.WriteEndObject(); // env

        if (!string.IsNullOrEmpty(input.SourceId))
        {
            writer.WriteString("sourceId", input.SourceId);
        }

        if (input.RawJsonPayload is { ValueKind: not JsonValueKind.Undefined } rawEl)
        {
            writer.WritePropertyName("rawPayload");
            rawEl.WriteTo(writer);
        }
        else if (input.RawSourcePayload is not null)
        {
            writer.WritePropertyName("rawPayload");
            if (!TryWriteRawJsonOrString(writer, input.RawSourcePayload))
            {
                writer.WriteStringValue(input.RawSourcePayload);
            }
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteDropFileJson(Utf8JsonWriter writer, CustomWidgetDropFileContext f)
    {
        writer.WriteStartObject();
        writer.WriteString("name", f.Name);
        writer.WriteString("path", f.Path);
        writer.WriteString("ext", f.Extension);
        writer.WriteNumber("sizeBytes", f.SizeBytes);
        writer.WriteString("sizeText", f.SizeText);
        writer.WriteString("modifiedIso", f.ModifiedIso);
        writer.WriteString("mode", f.Mode);
        writer.WriteString("text", f.Text);
        writer.WriteEndObject();
    }

    private static bool TryWriteRawJsonOrString(Utf8JsonWriter writer, string rawPayload)
    {
        string trimmed = rawPayload.Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            doc.RootElement.WriteTo(writer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRecordJson(Utf8JsonWriter writer, CustomWidgetRecord rec)
    {
        writer.WriteStartObject();
        writer.WriteString("id", rec.Id);
        writer.WriteString("createdAt", rec.GetField("createdAt"));
        writer.WriteNumber("createdAtUtcTicks", rec.CreatedAtUtcTicks);
        writer.WritePropertyName("fields");
        writer.WriteStartObject();
        foreach ((string k, JsonElement el) in rec.FieldsJson)
        {
            if (string.IsNullOrEmpty(k) || el.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }
            writer.WritePropertyName(k);
            el.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteFlattenedRecordJson(Utf8JsonWriter writer, CustomWidgetRecord rec)
    {
        writer.WriteStartObject();
        foreach ((string k, JsonElement el) in rec.FieldsJson)
        {
            if (string.IsNullOrEmpty(k) ||
                el.ValueKind == JsonValueKind.Undefined ||
                string.Equals(k, "id", StringComparison.Ordinal) ||
                string.Equals(k, "createdAt", StringComparison.Ordinal) ||
                string.Equals(k, "timeText", StringComparison.Ordinal) ||
                string.Equals(k, "dateText", StringComparison.Ordinal))
            {
                continue;
            }
            writer.WritePropertyName(k);
            el.WriteTo(writer);
        }
        writer.WriteString("id", rec.Id);
        writer.WriteString("createdAt", rec.GetField("createdAt"));
        writer.WriteString("timeText", rec.TimeText);
        writer.WriteString("dateText", rec.DateText);
        writer.WriteEndObject();
    }

    private static CustomWidgetScriptExecutionResult ParseExecutionResultJson(
        ReadOnlyMemory<byte> resultJsonUtf8,
        IReadOnlyDictionary<string, JsonElement> previousComputed)
    {
        if (resultJsonUtf8.IsEmpty)
        {
            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = true,
                Invoked = false,
                Status = CustomWidgetScriptStatus.Ok,
                Computed = CloneComputedDictionary(previousComputed)
            };
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(resultJsonUtf8);
            JsonElement root = doc.RootElement;
            bool invoked = root.TryGetProperty("invoked", out JsonElement invEl) && invEl.ValueKind == JsonValueKind.True;
            if (!invoked)
            {
                return new CustomWidgetScriptExecutionResult
                {
                    IsSuccess = true,
                    Invoked = false,
                    Status = CustomWidgetScriptStatus.Ok,
                    Computed = CloneComputedDictionary(previousComputed)
                };
            }

            bool stateChanged = root.TryGetProperty("stateChanged", out JsonElement scEl) && scEl.ValueKind == JsonValueKind.True;
            var stateSet = new Dictionary<string, string>(StringComparer.Ordinal);
            var stateSetJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("stateSet", out JsonElement setEl) && setEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in setEl.EnumerateObject())
                {
                    stateSet[prop.Name] = JsonElementToDisplayString(prop.Value);
                    stateSetJson[prop.Name] = prop.Value.Clone();
                }
            }

            var stateRemoved = new List<string>();
            if (root.TryGetProperty("stateRemoved", out JsonElement remEl) && remEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in remEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } key)
                    {
                        stateRemoved.Add(key);
                    }
                }
            }

            var computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("computed", out JsonElement compEl) && compEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in compEl.EnumerateObject())
                {
                    computed[prop.Name] = prop.Value.Clone();
                }
            }

            var dirtyCollections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
            if (root.TryGetProperty("collectionsDirty", out JsonElement colsEl) && colsEl.ValueKind == JsonValueKind.Object)
            {
                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                foreach (JsonProperty colProp in colsEl.EnumerateObject())
                {
                    if (colProp.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var list = new List<CustomWidgetRecord>();
                    int idx = 0;
                    foreach (JsonElement recEl in colProp.Value.EnumerateArray())
                    {
                        if (idx++ >= MaxCollectionItems || recEl.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var rec = new CustomWidgetRecord
                        {
                            UpdatedAtUtcTicks = nowTicks
                        };

                        if (recEl.TryGetProperty("id", out JsonElement idEl) &&
                            idEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(idEl.GetString()))
                        {
                            rec.Id = idEl.GetString()!;
                        }

                        if (recEl.TryGetProperty("createdAtUtcTicks", out JsonElement ticksEl) &&
                            ticksEl.ValueKind == JsonValueKind.Number &&
                            ticksEl.TryGetInt64(out long ticks) &&
                            ticks > 0)
                        {
                            rec.CreatedAtUtcTicks = ticks;
                        }

                        if (recEl.TryGetProperty("fields", out JsonElement fieldsEl) &&
                            fieldsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty fp in fieldsEl.EnumerateObject())
                            {
                                if (!string.IsNullOrEmpty(fp.Name) && fp.Value.ValueKind != JsonValueKind.Undefined)
                                {
                                    rec.FieldsJson[fp.Name] = fp.Value.Clone();
                                }
                            }
                        }

                        list.Add(rec);
                    }

                    dirtyCollections[colProp.Name] = list;
                }
            }

            var uiOps = new List<CustomWidgetScriptUiOp>();
            if (root.TryGetProperty("uiOps", out JsonElement uiEl) && uiEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement opEl in uiEl.EnumerateArray())
                {
                    if (opEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string type = opEl.TryGetProperty("type", out JsonElement tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        continue;
                    }

                    uiOps.Add(new CustomWidgetScriptUiOp(
                        Type: type,
                        Message: ReadStringProp(opEl, "message"),
                        Tone: ReadStringProp(opEl, "tone", "info"),
                        Text: ReadStringProp(opEl, "text"),
                        Title: ReadStringProp(opEl, "title"),
                        Body: ReadStringProp(opEl, "body"),
                        SourceId: ReadStringProp(opEl, "sourceId")));
                }
            }

            IReadOnlyList<CustomWidgetActionSpec> dispatchedActions = [];
            string? dispatchedActionsRawJson = null;
            if (root.TryGetProperty("dispatchedActions", out JsonElement dispEl) &&
                dispEl.ValueKind == JsonValueKind.Array &&
                dispEl.GetArrayLength() > 0)
            {
                dispatchedActionsRawJson = dispEl.GetRawText();
                dispatchedActions = CustomWidgetManifestParser.ParseDispatchedActions(dispatchedActionsRawJson);
            }

            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = true,
                Invoked = true,
                Status = CustomWidgetScriptStatus.Ok,
                StateChanged = stateChanged,
                StateSet = stateSet,
                StateSetJson = stateSetJson,
                StateRemoved = stateRemoved,
                Computed = computed,
                DirtyCollections = dirtyCollections,
                UiOps = uiOps,
                DispatchedActions = dispatchedActions,
                DispatchedActionsRawJson = dispatchedActionsRawJson
            };
        }
        catch (Exception ex)
        {
            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = false,
                Invoked = false,
                Status = CustomWidgetScriptStatus.Error,
                Computed = CloneComputedDictionary(previousComputed),
                ErrorMessage = $"Failed to parse script execution output: {ex.Message}"
            };
        }
    }

    private static (HashSet<string> Functions, HashSet<string> All) ParseExportsJson(byte[] exportsJsonUtf8)
    {
        var fns = new HashSet<string>(StringComparer.Ordinal);
        var all = new HashSet<string>(StringComparer.Ordinal);
        if (exportsJsonUtf8 is not { Length: > 0 })
        {
            return (fns, all);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(exportsJsonUtf8);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("functions", out JsonElement fnArr) && fnArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in fnArr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                        {
                            fns.Add(s);
                        }
                    }
                }

                if (root.TryGetProperty("all", out JsonElement allArr) && allArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in allArr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                        {
                            all.Add(s);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return (fns, all);
    }

    private static Dictionary<string, JsonElement> CloneComputedDictionary(
        IReadOnlyDictionary<string, JsonElement> source)
    {
        var copy = new Dictionary<string, JsonElement>(source.Count, StringComparer.Ordinal);
        foreach ((string k, JsonElement v) in source)
        {
            if (v.ValueKind != JsonValueKind.Undefined)
            {
                copy[k] = v.Clone();
            }
        }
        return copy;
    }

    private static string ReadStringProp(JsonElement obj, string name, string fallback = "") =>
        obj.TryGetProperty(name, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? fallback
            : fallback;

    private static int? ExtractLineNumber(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return null;
        }

        Match m = ErrorLineRegex().Match(errorMessage);
        if (m.Success && int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
        {
            return line;
        }

        return null;
    }

    private static CustomWidgetScriptStatus MapStatus(DeskBoxQjsNativeStatus status) => status switch
    {
        DeskBoxQjsNativeStatus.Ok => CustomWidgetScriptStatus.Ok,
        DeskBoxQjsNativeStatus.Error => CustomWidgetScriptStatus.Error,
        DeskBoxQjsNativeStatus.Timeout => CustomWidgetScriptStatus.Timeout,
        DeskBoxQjsNativeStatus.OutOfMemory => CustomWidgetScriptStatus.OutOfMemory,
        DeskBoxQjsNativeStatus.InvalidArgument => CustomWidgetScriptStatus.InvalidArgument,
        DeskBoxQjsNativeStatus.ExportNotFound => CustomWidgetScriptStatus.ExportNotFound,
        DeskBoxQjsNativeStatus.EngineUnavailable => CustomWidgetScriptStatus.EngineUnavailable,
        _ => CustomWidgetScriptStatus.Error
    };
}
