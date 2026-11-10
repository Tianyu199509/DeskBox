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

public sealed record CustomWidgetCompiledScript(
    byte[] Bytecode,
    string SourceSha256,
    IReadOnlySet<string> ExportedFunctions,
    IReadOnlySet<string> AllExports);

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
    IReadOnlyDictionary<string, string>? Args = null);

public sealed class CustomWidgetScriptExecutionInput
{
    public IReadOnlyDictionary<string, string> State { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
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
    public CustomWidgetScriptEventPayload? Event { get; init; }
    public DateTimeOffset? ClockOverride { get; init; }
    public string? CultureName { get; init; }
    public CustomWidgetSystemMetricsSnapshot? SystemMetrics { get; init; }
    public CustomWidgetDeviceStateSnapshot? DeviceState { get; init; }
    public CustomWidgetWeatherSnapshot? Weather { get; init; }
    public CustomWidgetMusicSnapshot? Music { get; init; }
    public CustomWidgetTodoSnapshot? Todo { get; init; }
    public string? SourceId { get; init; }
    public string? RawSourcePayload { get; init; }
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
    public IReadOnlyList<string> StateRemoved { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Computed { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, List<CustomWidgetRecord>> DirtyCollections { get; init; } =
        new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
    public IReadOnlyList<CustomWidgetScriptUiOp> UiOps { get; init; } = [];
    public IReadOnlyList<CustomWidgetActionSpec> DispatchedActions { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public int? ErrorLine { get; init; }
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

    private static readonly Regex ErrorLineRegex = new(
        @"(?:logic\.js|<input>|<module>):(?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        (HashSet<string> exportedFunctions, HashSet<string> allExports) = ParseExportsJson(native.ExportsJson);
        var compiled = new CustomWidgetCompiledScript(
            native.Bytecode,
            ComputeSourceSha256(sourceCode),
            exportedFunctions,
            allExports);

        return new CustomWidgetScriptCompileResult(true, CustomWidgetScriptStatus.Ok, compiled);
    }

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

        var buffer = new ArrayBufferWriter<byte>(2048);
        WritePayloadJson(buffer, input);

        DeskBoxQjsNativeInvokeOutcome outcome = CustomWidgetQuickJsInterop.InvokeBytecode(
            compiled.Bytecode,
            functionName,
            requireExport,
            buffer.WrittenSpan,
            input.TimeoutMs > 0 ? input.TimeoutMs : DefaultActionTimeoutMs,
            input.MemoryLimitBytes > 0 ? input.MemoryLimitBytes : DefaultMemoryLimitBytes);

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

        return ParseExecutionResultJson(outcome.ResultJson, input.Computed);
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
        IReadOnlyDictionary<string, JsonElement> computed)
    {
        if (string.IsNullOrWhiteSpace(itemsBinding) || computed.Count == 0)
        {
            return [];
        }

        string trimmed = itemsBinding.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        string path = trimmed.StartsWith("$computed.", StringComparison.Ordinal)
            ? trimmed["$computed.".Length..]
            : trimmed.StartsWith("computed.", StringComparison.Ordinal)
                ? trimmed["computed.".Length..]
                : string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        int dotIndex = path.IndexOf('.');
        string rootKey = dotIndex >= 0 ? path[..dotIndex] : path;
        if (!computed.TryGetValue(rootKey, out JsonElement element))
        {
            return [];
        }

        if (dotIndex >= 0)
        {
            string[] segments = path[(dotIndex + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
            foreach (string seg in segments)
            {
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
                    string val = JsonElementToDisplayString(prop.Value);
                    if (string.Equals(prop.Name, "id", StringComparison.Ordinal) && !string.IsNullOrEmpty(val))
                    {
                        rec.Id = val;
                    }
                    rec.Fields[prop.Name] = val;
                }
            }
            else
            {
                string scalar = JsonElementToDisplayString(itemEl);
                rec.Fields["value"] = scalar;
                rec.Fields["text"] = scalar;
                rec.Fields["title"] = scalar;
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

    private static void WritePayloadJson(IBufferWriter<byte> buffer, CustomWidgetScriptExecutionInput input)
    {
        using var writer = new Utf8JsonWriter(buffer, CompactJsonWriterOptions);
        DateTimeOffset now = (input.ClockOverride ?? DateTimeOffset.Now).ToLocalTime();
        string locale = !string.IsNullOrWhiteSpace(input.CultureName)
            ? input.CultureName!
            : CultureInfo.CurrentUICulture.Name;

        writer.WriteStartObject();
        writer.WriteBoolean("allowCopy", input.AllowCopy);
        writer.WriteNumber("maxCollectionItems", Math.Clamp(input.MaxCollectionItems, 1, MaxCollectionItems));

        writer.WritePropertyName("state");
        writer.WriteStartObject();
        foreach ((string k, string v) in input.State)
        {
            writer.WriteString(k, v ?? string.Empty);
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
            writer.WriteString(k, v ?? string.Empty);
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
                    writer.WriteString(k, v ?? string.Empty);
                }
            }
        }
        writer.WriteEndObject();

        writer.WritePropertyName("event");
        writer.WriteStartObject();
        writer.WriteString("arg", input.Event?.Arg ?? string.Empty);
        writer.WriteString("value", input.Event?.Value ?? string.Empty);
        writer.WriteString("id", input.Event?.Id ?? string.Empty);
        if (input.Event?.X is { } evX)
        {
            writer.WriteNumber("x", evX);
        }
        if (input.Event?.Y is { } evY)
        {
            writer.WriteNumber("y", evY);
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
            writer.WriteStartObject();
            writer.WriteString("name", evDrop.Name);
            writer.WriteString("path", evDrop.Path);
            writer.WriteString("ext", evDrop.Extension);
            writer.WriteString("sizeText", evDrop.SizeText);
            writer.WriteString("mode", evDrop.Mode);
            writer.WriteString("text", evDrop.Text);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("drop");
        }
        if (input.Event?.Args is { Count: > 0 } evArgs)
        {
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach ((string k, string v) in evArgs)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    writer.WriteString(k, v ?? string.Empty);
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WritePropertyName("env");
        writer.WriteStartObject();
        writer.WriteNumber("nowMs", now.ToUnixTimeMilliseconds());
        writer.WriteString("isoDate", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        writer.WriteString("isoDateTime", now.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("time", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        writer.WriteString("timeZone", TimeZoneInfo.Local.Id);
        writer.WriteNumber("dayOfWeek", (int)now.DayOfWeek);
        writer.WriteString("locale", locale);

        CustomWidgetSystemMetricsSnapshot sys = input.SystemMetrics ?? CustomWidgetSystemMetricsSnapshot.Empty;
        writer.WritePropertyName("sys");
        writer.WriteStartObject();
        writer.WriteString("cpuPercent", sys.ResolveToken("cpu.percent", input.Settings, locale));
        writer.WriteString("memoryPercent", sys.ResolveToken("mem.percent", input.Settings, locale));
        writer.WriteString("memoryUsedGb", sys.ResolveToken("mem.usedGb", input.Settings, locale));
        writer.WriteString("diskPercent", sys.ResolveToken("disk.percent", input.Settings, locale));
        writer.WriteString("netDown", sys.ResolveToken("net.downText", input.Settings, locale));
        writer.WriteString("netUp", sys.ResolveToken("net.upText", input.Settings, locale));
        writer.WriteEndObject();

        CustomWidgetDeviceStateSnapshot dev = input.DeviceState ?? CustomWidgetDeviceStateSnapshot.Empty;
        writer.WritePropertyName("device");
        writer.WriteStartObject();
        writer.WriteString("batteryPercent", dev.ResolveToken("battery.percent", locale));
        writer.WriteString("isCharging", dev.ResolveToken("battery.isCharging", locale));
        writer.WriteString("volume", dev.ResolveToken("audio.volume", locale));
        writer.WriteString("isMuted", dev.ResolveToken("audio.isMuted", locale));
        writer.WriteString("networkType", dev.ResolveToken("network.type", locale));
        writer.WriteEndObject();

        CustomWidgetWeatherSnapshot weather = input.Weather ?? CustomWidgetWeatherSnapshot.Empty;
        writer.WritePropertyName("weather");
        writer.WriteStartObject();
        writer.WriteString("city", weather.ResolveToken("city", locale));
        writer.WriteString("temp", weather.ResolveToken("temp", locale));
        writer.WriteString("condition", weather.ResolveToken("condition", locale));
        writer.WriteString("humidity", weather.ResolveToken("humidity", locale));
        writer.WriteString("wind", weather.ResolveToken("wind", locale));
        writer.WriteEndObject();

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

        CustomWidgetTodoSnapshot todo = input.Todo ?? CustomWidgetTodoSnapshot.Empty;
        writer.WritePropertyName("todo");
        writer.WriteStartObject();
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
        writer.WriteEndObject(); // env

        if (!string.IsNullOrEmpty(input.SourceId))
        {
            writer.WriteString("sourceId", input.SourceId);
        }

        if (input.RawSourcePayload is not null)
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
        foreach ((string k, string v) in rec.Fields)
        {
            writer.WriteString(k, v ?? string.Empty);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteFlattenedRecordJson(Utf8JsonWriter writer, CustomWidgetRecord rec)
    {
        writer.WriteStartObject();
        foreach ((string k, string v) in rec.Fields)
        {
            writer.WriteString(k, v ?? string.Empty);
        }
        writer.WriteString("id", rec.Id);
        writer.WriteString("createdAt", rec.GetField("createdAt"));
        writer.WriteString("timeText", rec.TimeText);
        writer.WriteString("dateText", rec.DateText);
        writer.WriteEndObject();
    }

    private static CustomWidgetScriptExecutionResult ParseExecutionResultJson(
        string resultJson,
        IReadOnlyDictionary<string, JsonElement> previousComputed)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
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
            using JsonDocument doc = JsonDocument.Parse(resultJson);
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
            if (root.TryGetProperty("stateSet", out JsonElement setEl) && setEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in setEl.EnumerateObject())
                {
                    stateSet[prop.Name] = JsonElementToDisplayString(prop.Value);
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
                                rec.Fields[fp.Name] = JsonElementToDisplayString(fp.Value);
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
            if (root.TryGetProperty("dispatchedActions", out JsonElement dispEl) &&
                dispEl.ValueKind == JsonValueKind.Array &&
                dispEl.GetArrayLength() > 0)
            {
                dispatchedActions = CustomWidgetManifestParser.ParseDispatchedActions(dispEl.GetRawText());
            }

            return new CustomWidgetScriptExecutionResult
            {
                IsSuccess = true,
                Invoked = true,
                Status = CustomWidgetScriptStatus.Ok,
                StateChanged = stateChanged,
                StateSet = stateSet,
                StateRemoved = stateRemoved,
                Computed = computed,
                DirtyCollections = dirtyCollections,
                UiOps = uiOps,
                DispatchedActions = dispatchedActions
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

    private static (HashSet<string> Functions, HashSet<string> All) ParseExportsJson(string exportsJson)
    {
        var fns = new HashSet<string>(StringComparer.Ordinal);
        var all = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(exportsJson))
        {
            return (fns, all);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(exportsJson);
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

        Match m = ErrorLineRegex.Match(errorMessage);
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
