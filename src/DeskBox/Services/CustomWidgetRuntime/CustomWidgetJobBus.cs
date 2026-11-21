using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Services;

/// <summary>
/// Per-widget asynchronous job coordinator providing <c>jobKey</c>-isolated concurrency,
/// <c>job.cancel</c> cancellation (single job or cancel-all), 4Hz (<c>250ms</c>) progress throttling,
/// background continuity across capsule collapse, and canonical AOT-safe JSON envelopes for
/// <c>onProgress(ctx, progress)</c> and <c>onComplete(ctx, res)</c>.
/// </summary>
public sealed class CustomWidgetJobBus : IDisposable
{
    public const int DefaultProgressThrottleMs = 250;

    private static readonly JsonWriterOptions CompactJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveSlot> _activeJobs = new(StringComparer.Ordinal);
    private long _nextGeneration;
    private bool _disposed;

    public event Action? ActiveStateChanged;

    internal sealed class ActiveSlot
    {
        public required long Generation { get; init; }
        public required string Id { get; init; }
        public required string JobKey { get; init; }
        public required CancellationTokenSource Cts { get; init; }
    }

    public sealed class ActiveJobHandle : IDisposable
    {
        private readonly CustomWidgetJobBus _owner;
        private readonly ActiveSlot _slot;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastProgressTick;
        private int _released;

        internal ActiveJobHandle(CustomWidgetJobBus owner, ActiveSlot slot)
        {
            _owner = owner;
            _slot = slot;
        }

        public string Id => _slot.Id;
        public string JobKey => _slot.JobKey;
        public CancellationToken Token => _slot.Cts.Token;
        public long ElapsedMs => _stopwatch.ElapsedMilliseconds;
        public bool IsCancellationRequested => _slot.Cts.IsCancellationRequested;

        public bool TryClaimProgressWindow(out long elapsedMs, int throttleMs = DefaultProgressThrottleMs)
        {
            elapsedMs = _stopwatch.ElapsedMilliseconds;
            int minGap = Math.Max(25, throttleMs);
            long nowTick = Environment.TickCount64;
            while (true)
            {
                long prev = Interlocked.Read(ref _lastProgressTick);
                if (prev != 0 && nowTick - prev < minGap)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _lastProgressTick, nowTick, prev) == prev)
                {
                    return true;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            _stopwatch.Stop();
            _owner.ReleaseSlot(_slot);
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _activeJobs.Count;
            }
        }
    }

    public bool IsRunning(string? jobKey)
    {
        if (string.IsNullOrWhiteSpace(jobKey))
        {
            return false;
        }

        lock (_gate)
        {
            return _activeJobs.ContainsKey(jobKey.Trim());
        }
    }

    public ActiveJobHandle BeginJob(string? rawJobKey, string? fallbackId, CancellationToken externalToken = default, bool avoidSupersede = false)
    {
        string normalizedId = !string.IsNullOrWhiteSpace(fallbackId)
            ? fallbackId.Trim()
            : (!string.IsNullOrWhiteSpace(rawJobKey) ? rawJobKey.Trim() : "job");
        string normalizedKey = !string.IsNullOrWhiteSpace(rawJobKey)
            ? rawJobKey.Trim()
            : normalizedId;

        ActiveSlot? superseded = null;
        ActiveSlot slot;
        bool becameActive = false;
        lock (_gate)
        {
            bool wasEmpty = _activeJobs.Count == 0;
            CancellationTokenSource cts = externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource();

            if (_disposed)
            {
                cts.Cancel();
            }

            long gen = ++_nextGeneration;
            string slotKey = !_disposed && avoidSupersede && _activeJobs.ContainsKey(normalizedKey)
                ? $"{normalizedKey}#{gen}"
                : normalizedKey;
            slot = new ActiveSlot
            {
                Generation = gen,
                Id = normalizedId,
                JobKey = slotKey,
                Cts = cts
            };

            if (!_disposed)
            {
                if (_activeJobs.TryGetValue(slotKey, out ActiveSlot? existing))
                {
                    superseded = existing;
                }

                _activeJobs[slotKey] = slot;
                becameActive = wasEmpty && _activeJobs.Count > 0;
            }
        }

        if (superseded is not null)
        {
            TryCancelSlot(superseded);
        }

        if (becameActive)
        {
            ActiveStateChanged?.Invoke();
        }

        return new ActiveJobHandle(this, slot);
    }

    public int Cancel(string? jobKey = null)
    {
        List<ActiveSlot>? targets = null;
        bool becameIdle = false;
        lock (_gate)
        {
            if (_activeJobs.Count == 0)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(jobKey) || string.Equals(jobKey.Trim(), "*", StringComparison.Ordinal))
            {
                targets = [.. _activeJobs.Values];
                _activeJobs.Clear();
                becameIdle = true;
            }
            else
            {
                string key = jobKey.Trim();
                if (_activeJobs.Remove(key, out ActiveSlot? matched))
                {
                    targets = [matched];
                    becameIdle = _activeJobs.Count == 0;
                }
            }
        }

        if (targets is null || targets.Count == 0)
        {
            return 0;
        }

        foreach (ActiveSlot slot in targets)
        {
            TryCancelSlot(slot);
        }

        if (becameIdle)
        {
            ActiveStateChanged?.Invoke();
        }

        return targets.Count;
    }

    private void ReleaseSlot(ActiveSlot slot)
    {
        bool becameIdle = false;
        lock (_gate)
        {
            if (_activeJobs.TryGetValue(slot.JobKey, out ActiveSlot? current) &&
                current.Generation == slot.Generation)
            {
                _activeJobs.Remove(slot.JobKey);
                becameIdle = !_disposed && _activeJobs.Count == 0;
            }
        }

        try
        {
            slot.Cts.Dispose();
        }
        catch
        {
        }

        if (becameIdle)
        {
            ActiveStateChanged?.Invoke();
        }
    }

    private static void TryCancelSlot(ActiveSlot slot)
    {
        try
        {
            slot.Cts.Cancel();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        List<ActiveSlot>? active = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_activeJobs.Count > 0)
            {
                active = [.. _activeJobs.Values];
                _activeJobs.Clear();
            }
        }

        if (active is not null)
        {
            foreach (ActiveSlot slot in active)
            {
                TryCancelSlot(slot);
            }
        }
    }

    public static string BuildProgressPayloadJson(
        string id,
        string jobKey,
        string actionType,
        string line,
        string accumulatedText,
        string stream,
        long elapsedMs)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id ?? string.Empty);
            writer.WriteString("jobKey", string.IsNullOrWhiteSpace(jobKey) ? (id ?? string.Empty) : jobKey);
            writer.WriteString("type", actionType ?? string.Empty);
            writer.WriteString("line", line ?? string.Empty);
            writer.WriteString("text", accumulatedText ?? string.Empty);
            writer.WriteString("stream", string.IsNullOrWhiteSpace(stream) ? "stdout" : stream);
            writer.WriteNumber("elapsedMs", Math.Max(0, elapsedMs));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildCommandResultPayloadJson(
        string id,
        string jobKey,
        CustomWidgetCommandOutcome outcome)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;
        string stdout = outcome.Stdout ?? string.Empty;
        string stderr = outcome.Stderr ?? string.Empty;
        string error = outcome.ErrorMessage ?? string.Empty;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", "command.run");
            writer.WriteBoolean("ok", outcome.Succeeded);
            writer.WriteNumber("status", outcome.ExitCode);
            writer.WriteString("text", stdout);
            writer.WritePropertyName("data");
            WriteParsedJsonOrNull(writer, stdout);
            writer.WriteString("error", error);
            writer.WriteBoolean("timedOut", outcome.TimedOut);
            writer.WriteBoolean("cancelled", outcome.Cancelled);
            writer.WriteNumber("durationMs", Math.Max(0, outcome.DurationMs));

            // Convenience direct fields alongside canonical meta
            writer.WriteNumber("exitCode", outcome.ExitCode);
            writer.WriteString("stdout", stdout);
            writer.WriteString("stderr", stderr);

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteNumber("exitCode", outcome.ExitCode);
            writer.WriteString("stderr", stderr);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildHttpResultPayloadJson(
        string id,
        string jobKey,
        CustomWidgetInteractiveHttpOutcome outcome,
        long durationMs = 0,
        bool timedOut = false,
        bool cancelled = false)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;
        string body = outcome.ResponseBody ?? string.Empty;
        string error = outcome.ErrorMessage ?? string.Empty;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", "http.request");
            writer.WriteBoolean("ok", outcome.Succeeded);
            writer.WriteNumber("status", outcome.StatusCode);
            writer.WriteString("text", body);
            writer.WritePropertyName("data");
            WriteParsedJsonOrNull(writer, body);
            writer.WriteString("error", error);
            writer.WriteBoolean("timedOut", timedOut);
            writer.WriteBoolean("cancelled", cancelled);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            // Convenience direct fields alongside canonical meta
            writer.WriteString("statusText", outcome.StatusText ?? string.Empty);
            writer.WriteString("body", body);
            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach ((string k, string v) in outcome.ResponseHeaders)
            {
                writer.WriteString(k, v ?? string.Empty);
            }
            writer.WriteEndObject();

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteString("statusText", outcome.StatusText ?? string.Empty);
            writer.WritePropertyName("headers");
            writer.WriteStartObject();
            foreach ((string k, string v) in outcome.ResponseHeaders)
            {
                writer.WriteString(k, v ?? string.Empty);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildAiResultPayloadJson(
        string id,
        string jobKey,
        bool ok,
        string text,
        string? error,
        string? model = null,
        long durationMs = 0,
        bool timedOut = false,
        bool cancelled = false)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;
        string cleanText = text ?? string.Empty;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", "ai.run");
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("status", ok ? 200 : (cancelled ? 499 : 500));
            writer.WriteString("text", cleanText);
            writer.WritePropertyName("data");
            WriteParsedJsonOrNull(writer, cleanText);
            writer.WriteString("error", error ?? string.Empty);
            writer.WriteBoolean("timedOut", timedOut);
            writer.WriteBoolean("cancelled", cancelled);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteString("model", model ?? string.Empty);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildFileListResultPayloadJson(
        string id,
        string jobKey,
        string actionType,
        bool ok,
        string primaryPath,
        IReadOnlyList<CustomWidgetDropFileContext> files,
        string? error,
        long durationMs = 0,
        bool cancelled = false)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", actionType ?? "file.listDir");
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("status", ok ? 0 : -1);
            writer.WriteString("text", primaryPath ?? string.Empty);

            writer.WritePropertyName("data");
            WriteDropFileArray(writer, files);

            writer.WriteString("error", error ?? string.Empty);
            writer.WriteBoolean("timedOut", false);
            writer.WriteBoolean("cancelled", cancelled);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            writer.WritePropertyName("files");
            WriteDropFileArray(writer, files);

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteNumber("count", files?.Count ?? 0);
            writer.WriteString("path", primaryPath ?? string.Empty);
            writer.WritePropertyName("files");
            WriteDropFileArray(writer, files);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildFileOperationResultPayloadJson(
        string id,
        string jobKey,
        string actionType,
        string op,
        bool ok,
        string sourcePath,
        string targetPath,
        string summary,
        string? error,
        long durationMs = 0,
        bool cancelled = false)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", actionType ?? "file.manage");
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("status", ok ? 0 : -1);
            writer.WriteString("text", !string.IsNullOrWhiteSpace(targetPath) ? targetPath : (summary ?? string.Empty));

            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteString("op", op ?? string.Empty);
            writer.WriteString("sourcePath", sourcePath ?? string.Empty);
            writer.WriteString("targetPath", targetPath ?? string.Empty);
            writer.WriteString("summary", summary ?? string.Empty);
            writer.WriteEndObject();

            writer.WriteString("error", error ?? string.Empty);
            writer.WriteBoolean("timedOut", false);
            writer.WriteBoolean("cancelled", cancelled);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteString("op", op ?? string.Empty);
            writer.WriteString("sourcePath", sourcePath ?? string.Empty);
            writer.WriteString("targetPath", targetPath ?? string.Empty);
            writer.WriteString("summary", summary ?? string.Empty);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildClipboardImageResultPayloadJson(
        string id,
        string jobKey,
        bool ok,
        CustomWidgetDropFileContext? file,
        int width,
        int height,
        string? error,
        long durationMs = 0)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", "clipboard.captureImage");
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("status", ok ? 0 : -1);
            writer.WriteString("text", file?.Path ?? string.Empty);

            writer.WritePropertyName("data");
            if (file is not null)
            {
                writer.WriteStartObject();
                WriteDropFileProperties(writer, file);
                writer.WriteNumber("width", Math.Max(0, width));
                writer.WriteNumber("height", Math.Max(0, height));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteString("error", error ?? string.Empty);
            writer.WriteBoolean("timedOut", false);
            writer.WriteBoolean("cancelled", false);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteString("path", file?.Path ?? string.Empty);
            writer.WriteNumber("width", Math.Max(0, width));
            writer.WriteNumber("height", Math.Max(0, height));
            writer.WriteNumber("sizeBytes", file?.SizeBytes ?? 0);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string BuildFileWriteResultPayloadJson(
        string id,
        string jobKey,
        bool ok,
        string resolvedPath,
        int bytesWritten,
        string? error,
        long durationMs)
    {
        string normalizedId = id ?? string.Empty;
        string normalizedKey = string.IsNullOrWhiteSpace(jobKey) ? normalizedId : jobKey;
        string safePath = resolvedPath ?? string.Empty;
        string directory = string.Empty;
        string fileName = string.Empty;
        if (!string.IsNullOrWhiteSpace(safePath))
        {
            try
            {
                directory = Path.GetDirectoryName(safePath) ?? string.Empty;
                fileName = Path.GetFileName(safePath);
            }
            catch
            {
            }
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", normalizedId);
            writer.WriteString("jobKey", normalizedKey);
            writer.WriteString("type", "file.writeText");
            writer.WriteBoolean("ok", ok);
            writer.WriteNumber("status", ok ? 0 : -1);
            writer.WriteString("path", safePath);
            writer.WriteString("directory", directory);
            writer.WriteString("fileName", fileName);
            writer.WriteNumber("bytesWritten", Math.Max(0, bytesWritten));
            writer.WriteString("text", safePath);
            writer.WriteString("error", error ?? string.Empty);
            writer.WriteBoolean("timedOut", false);
            writer.WriteBoolean("cancelled", false);
            writer.WriteNumber("durationMs", Math.Max(0, durationMs));

            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteString("path", safePath);
            writer.WriteString("directory", directory);
            writer.WriteString("fileName", fileName);
            writer.WriteNumber("bytesWritten", Math.Max(0, bytesWritten));
            writer.WriteEndObject();

            writer.WritePropertyName("meta");
            writer.WriteStartObject();
            writer.WriteString("path", safePath);
            writer.WriteString("directory", directory);
            writer.WriteNumber("bytesWritten", Math.Max(0, bytesWritten));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteDropFileArray(Utf8JsonWriter writer, IReadOnlyList<CustomWidgetDropFileContext>? files)
    {
        writer.WriteStartArray();
        if (files is { Count: > 0 })
        {
            foreach (CustomWidgetDropFileContext f in files)
            {
                writer.WriteStartObject();
                WriteDropFileProperties(writer, f);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteDropFileProperties(Utf8JsonWriter writer, CustomWidgetDropFileContext f)
    {
        writer.WriteString("name", f.Name ?? string.Empty);
        writer.WriteString("path", f.Path ?? string.Empty);
        writer.WriteString("ext", f.Extension ?? string.Empty);
        writer.WriteString("size", f.SizeText ?? string.Empty);
        writer.WriteNumber("sizeBytes", f.SizeBytes);
        writer.WriteString("modifiedIso", f.ModifiedIso ?? string.Empty);
    }

    private static void WriteParsedJsonOrNull(Utf8JsonWriter writer, string? rawText)
    {
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            string trimmed = rawText.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(
                        trimmed,
                        new JsonDocumentOptions
                        {
                            AllowTrailingCommas = true,
                            CommentHandling = JsonCommentHandling.Skip
                        });
                    doc.RootElement.WriteTo(writer);
                    return;
                }
                catch
                {
                }
            }
        }

        writer.WriteNullValue();
    }
}
