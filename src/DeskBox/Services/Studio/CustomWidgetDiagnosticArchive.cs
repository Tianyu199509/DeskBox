using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed record StudioDiagnosticArchiveEntry(
    string EntryId,
    string CreatedAtUtc,
    string EventKind,
    string? WidgetId,
    string? ManifestId,
    string ProviderId,
    string Model,
    string ThinkingMode,
    bool Success,
    bool WasAutoRepaired,
    int LlmCalls,
    int ToolRounds,
    int RepairRounds,
    long ElapsedMs,
    string UserPrompt,
    string SummaryOrError,
    IReadOnlyList<string> Diagnostics,
    string ManifestJson,
    string? LogicJs);

/// <summary>
/// Bounded rolling diagnostic archive under <c>cache/studio-ai-recent.json</c> for
/// DeskBox Studio AI turns and commits. Survives widget deletion while keeping
/// API keys and secret tokens scrubbed, using <see cref="JsonDocument"/> and
/// <see cref="Utf8JsonWriter"/> exclusively for NativeAOT safety.
/// </summary>
public sealed partial class CustomWidgetDiagnosticArchive
{
    public const int MaxEntries = 10;
    public const string FileName = "studio-ai-recent.json";

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-+/=]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex BearerSecretRegex { get; }

    [GeneratedRegex(@"\b(?<prefix>sk-|AIza|ghp_|github_pat_|gsk_|xai-|xox[baprs]-)[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex KnownTokenPrefixSecretRegex { get; }

    [GeneratedRegex(@"(?<hdr>\b(?:authorization|x-api-key|api-key)\s*:\s*)[^\r\n""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex HeaderSecretRegex { get; }

    [GeneratedRegex(@"(?<qp>[?&](?:api_key|apikey|key|token|secret|password|auth)=)[^&\s""']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex QuerySecretRegex { get; }

    [GeneratedRegex(@"(?<q>[""'])(?<k>apiKey|api_key|api-key|x-api-key|secret|token|password|authorization)\k<q>\s*:\s*(?<vq>[""'])[^""']*\k<vq>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex QuotedSecretPropRegex { get; }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly object _gate = new();
    private readonly string? _rootDataDirectoryOverride;

    public static CustomWidgetDiagnosticArchive Shared { get; } = new();

    public CustomWidgetDiagnosticArchive(string? rootDataDirectory = null)
    {
        _rootDataDirectoryOverride = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? null
            : Path.GetFullPath(rootDataDirectory);
    }

    public string ArchiveFilePath => GetArchiveFilePath(createDirectory: true);

    private string GetArchiveFilePath(bool createDirectory)
    {
        string baseDir = _rootDataDirectoryOverride ?? DeskBoxDataPathService.Current.DataDirectory;
        string cacheDir = Path.Combine(baseDir, "cache");
        if (createDirectory)
        {
            Directory.CreateDirectory(cacheDir);
        }

        return Path.Combine(cacheDir, FileName);
    }

    public void Record(StudioDiagnosticArchiveEntry entry)
    {
        if (entry is null)
        {
            return;
        }

        StudioDiagnosticArchiveEntry scrubbed = SanitizeEntry(entry);
        try
        {
            lock (_gate)
            {
                string path = GetArchiveFilePath(createDirectory: true);
                List<StudioDiagnosticArchiveEntry> entries = ReadRecentUnlocked(path);
                entries.Insert(0, scrubbed);
                if (entries.Count > MaxEntries)
                {
                    entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                }

                string json = SerializeEntries(entries);
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(path, json);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[StudioAI] Failed to write diagnostic archive: {ex.Message}");
        }
    }

    public IReadOnlyList<StudioDiagnosticArchiveEntry> ReadRecent()
    {
        try
        {
            lock (_gate)
            {
                string path = GetArchiveFilePath(createDirectory: false);
                return ReadRecentUnlocked(path);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[StudioAI] Failed to read diagnostic archive: {ex.Message}");
            return [];
        }
    }

    public static string ScrubSensitiveText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0)
        {
            return string.Empty;
        }

        string scrubbed = BearerSecretRegex.Replace(text, "Bearer [REDACTED]");
        scrubbed = KnownTokenPrefixSecretRegex.Replace(scrubbed, "${prefix}[REDACTED]");
        scrubbed = HeaderSecretRegex.Replace(scrubbed, "${hdr}[REDACTED]");
        scrubbed = QuerySecretRegex.Replace(scrubbed, "${qp}[REDACTED]");
        scrubbed = QuotedSecretPropRegex.Replace(scrubbed, "${q}${k}${q}:${vq}[REDACTED]${vq}");
        if (scrubbed.Length <= maxChars)
        {
            return scrubbed;
        }

        int sliceLen = maxChars;
        if (sliceLen > 0 && char.IsHighSurrogate(scrubbed[sliceLen - 1]))
        {
            sliceLen--;
        }

        return sliceLen <= 0 ? string.Empty : scrubbed[..sliceLen];
    }

    private static StudioDiagnosticArchiveEntry SanitizeEntry(StudioDiagnosticArchiveEntry entry)
    {
        string entryId = string.IsNullOrWhiteSpace(entry.EntryId)
            ? Guid.NewGuid().ToString("N")[..12]
            : entry.EntryId.Trim();
        string createdAtUtc = string.IsNullOrWhiteSpace(entry.CreatedAtUtc)
            ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            : entry.CreatedAtUtc.Trim();

        var cleanDiagnostics = new List<string>(Math.Min(entry.Diagnostics?.Count ?? 0, 12));
        if (entry.Diagnostics is not null)
        {
            foreach (string diag in entry.Diagnostics)
            {
                if (cleanDiagnostics.Count >= 12)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(diag))
                {
                    cleanDiagnostics.Add(ScrubSensitiveText(diag.Trim(), 240));
                }
            }
        }

        return new StudioDiagnosticArchiveEntry(
            EntryId: entryId,
            CreatedAtUtc: createdAtUtc,
            EventKind: string.IsNullOrWhiteSpace(entry.EventKind) ? "studio-turn" : entry.EventKind.Trim(),
            WidgetId: string.IsNullOrWhiteSpace(entry.WidgetId) ? null : entry.WidgetId.Trim(),
            ManifestId: string.IsNullOrWhiteSpace(entry.ManifestId) ? null : entry.ManifestId.Trim(),
            ProviderId: entry.ProviderId?.Trim() ?? string.Empty,
            Model: entry.Model?.Trim() ?? string.Empty,
            ThinkingMode: entry.ThinkingMode?.Trim() ?? string.Empty,
            Success: entry.Success,
            WasAutoRepaired: entry.WasAutoRepaired,
            LlmCalls: Math.Max(0, entry.LlmCalls),
            ToolRounds: Math.Max(0, entry.ToolRounds),
            RepairRounds: Math.Max(0, entry.RepairRounds),
            ElapsedMs: Math.Max(0, entry.ElapsedMs),
            UserPrompt: ScrubSensitiveText(entry.UserPrompt, 1500),
            SummaryOrError: ScrubSensitiveText(entry.SummaryOrError, 600),
            Diagnostics: cleanDiagnostics,
            ManifestJson: ScrubSensitiveText(entry.ManifestJson, 32768),
            LogicJs: entry.LogicJs is null ? null : ScrubSensitiveText(entry.LogicJs, 16384));
    }

    private static List<StudioDiagnosticArchiveEntry> ReadRecentUnlocked(string path)
    {
        var list = new List<StudioDiagnosticArchiveEntry>();
        if (!File.Exists(path))
        {
            return list;
        }

        string raw = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        using JsonDocument doc = JsonDocument.Parse(raw, ReaderOptions);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("entries", out JsonElement entriesEl) ||
            entriesEl.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement item in entriesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var diagnostics = new List<string>();
            if (item.TryGetProperty("diagnostics", out JsonElement diagsEl) &&
                diagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in diagsEl.EnumerateArray())
                {
                    if (d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } text)
                    {
                        diagnostics.Add(text);
                    }
                }
            }

            string? logicJs = item.TryGetProperty("logicJs", out JsonElement logicEl) &&
                              logicEl.ValueKind == JsonValueKind.String
                ? logicEl.GetString()
                : null;

            list.Add(new StudioDiagnosticArchiveEntry(
                EntryId: GetStringProp(item, "entryId") ?? string.Empty,
                CreatedAtUtc: GetStringProp(item, "createdAtUtc") ?? string.Empty,
                EventKind: GetStringProp(item, "eventKind") ?? "studio-turn",
                WidgetId: GetOptionalStringProp(item, "widgetId"),
                ManifestId: GetOptionalStringProp(item, "manifestId"),
                ProviderId: GetStringProp(item, "providerId") ?? string.Empty,
                Model: GetStringProp(item, "model") ?? string.Empty,
                ThinkingMode: GetStringProp(item, "thinkingMode") ?? string.Empty,
                Success: GetBoolProp(item, "success"),
                WasAutoRepaired: GetBoolProp(item, "wasAutoRepaired"),
                LlmCalls: GetIntProp(item, "llmCalls"),
                ToolRounds: GetIntProp(item, "toolRounds"),
                RepairRounds: GetIntProp(item, "repairRounds"),
                ElapsedMs: GetLongProp(item, "elapsedMs"),
                UserPrompt: GetStringProp(item, "userPrompt") ?? string.Empty,
                SummaryOrError: GetStringProp(item, "summaryOrError") ?? string.Empty,
                Diagnostics: diagnostics,
                ManifestJson: GetStringProp(item, "manifestJson") ?? string.Empty,
                LogicJs: logicJs));
        }

        return list;
    }

    private static string SerializeEntries(IReadOnlyList<StudioDiagnosticArchiveEntry> entries)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("entries");
            foreach (StudioDiagnosticArchiveEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("entryId", entry.EntryId);
                writer.WriteString("createdAtUtc", entry.CreatedAtUtc);
                writer.WriteString("eventKind", entry.EventKind);
                if (!string.IsNullOrWhiteSpace(entry.WidgetId))
                {
                    writer.WriteString("widgetId", entry.WidgetId);
                }
                else
                {
                    writer.WriteNull("widgetId");
                }
                if (!string.IsNullOrWhiteSpace(entry.ManifestId))
                {
                    writer.WriteString("manifestId", entry.ManifestId);
                }
                else
                {
                    writer.WriteNull("manifestId");
                }
                writer.WriteString("providerId", entry.ProviderId);
                writer.WriteString("model", entry.Model);
                writer.WriteString("thinkingMode", entry.ThinkingMode);
                writer.WriteBoolean("success", entry.Success);
                writer.WriteBoolean("wasAutoRepaired", entry.WasAutoRepaired);
                writer.WriteNumber("llmCalls", entry.LlmCalls);
                writer.WriteNumber("toolRounds", entry.ToolRounds);
                writer.WriteNumber("repairRounds", entry.RepairRounds);
                writer.WriteNumber("elapsedMs", entry.ElapsedMs);
                writer.WriteString("userPrompt", entry.UserPrompt);
                writer.WriteString("summaryOrError", entry.SummaryOrError);
                writer.WriteStartArray("diagnostics");
                foreach (string diag in entry.Diagnostics)
                {
                    writer.WriteStringValue(diag);
                }
                writer.WriteEndArray();
                writer.WriteString("manifestJson", entry.ManifestJson);
                if (entry.LogicJs is not null)
                {
                    writer.WriteString("logicJs", entry.LogicJs);
                }
                else
                {
                    writer.WriteNull("logicJs");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? GetStringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string? GetOptionalStringProp(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? val = el.GetString();
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    private static bool GetBoolProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.True;

    private static int GetIntProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) &&
        el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out int val)
            ? val
            : 0;

    private static long GetLongProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) &&
        el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt64(out long val)
            ? val
            : 0L;
}
