using System.Text;

namespace DeskBox.Services;

public sealed record CustomWidgetRuntimeTraceEntry(
    DateTimeOffset TimestampUtc,
    string Trigger,
    string? ScriptFunction,
    string ActionType,
    string TargetSummary,
    string Outcome,
    string Detail);

/// <summary>
/// Lightweight, zero-disk-overhead ring buffer capturing the most recent runtime actions,
/// script errors, path/permission rejections, and failures for a widget instance.
/// Automatically surfaced to Studio AI on repair or bug-feedback turns so the model sees
/// real host diagnostics instead of opaque UI toast summaries.
/// </summary>
public sealed class CustomWidgetRuntimeTraceBuffer
{
    public const int DefaultCapacity = 15;
    public const int MaxEntries = DefaultCapacity;

    private static readonly string s_userProfileDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly object _gate = new();
    private readonly CustomWidgetRuntimeTraceEntry[] _ring;
    private int _nextIndex;
    private int _count;

    public CustomWidgetRuntimeTraceBuffer(int capacity = DefaultCapacity)
    {
        _ring = new CustomWidgetRuntimeTraceEntry[Math.Clamp(capacity, 4, 64)];
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public event Action<CustomWidgetRuntimeTraceEntry>? ErrorRecorded;

    public CustomWidgetRuntimeTraceEntry? LatestError
    {
        get
        {
            lock (_gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_nextIndex - 1 - i + _ring.Length) % _ring.Length;
                    CustomWidgetRuntimeTraceEntry candidate = _ring[idx];
                    if (string.Equals(candidate.Outcome, "error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.Outcome, "denied", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                return null;
            }
        }
    }

    public bool HasRecentErrors => LatestError is not null;

    public void Record(
        string actionType,
        string? targetSummary,
        bool ok,
        string? detail = null) =>
        Record(
            trigger: actionType,
            scriptFunction: null,
            actionType: actionType,
            targetSummary: targetSummary,
            outcome: ok ? "ok" : "error",
            detail: detail);

    public void Record(
        string trigger,
        string? scriptFunction,
        string actionType,
        string? targetSummary,
        string outcome,
        string? detail)
    {
        var entry = new CustomWidgetRuntimeTraceEntry(
            DateTimeOffset.UtcNow,
            ScrubForTrace(trigger, 60),
            string.IsNullOrWhiteSpace(scriptFunction) ? null : ScrubForTrace(scriptFunction, 60),
            ScrubForTrace(actionType, 48),
            ScrubForTrace(targetSummary, 140),
            string.IsNullOrWhiteSpace(outcome) ? "info" : outcome.Trim().ToLowerInvariant(),
            ScrubForTrace(detail, 240));

        lock (_gate)
        {
            _ring[_nextIndex] = entry;
            _nextIndex = (_nextIndex + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }

        if (entry.Outcome is "error" or "denied")
        {
            ErrorRecorded?.Invoke(entry);
        }
    }

    private static string ScrubForTrace(string? raw, int maxChars)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        string text = raw;
        if (!string.IsNullOrWhiteSpace(s_userProfileDir))
        {
            text = text.Replace(s_userProfileDir, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return CustomWidgetDiagnosticArchive.ScrubSensitiveText(text, maxChars);
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _nextIndex = 0;
            _count = 0;
        }
    }

    public IReadOnlyList<CustomWidgetRuntimeTraceEntry> SnapshotRecent(int maxEntries = DefaultCapacity)
    {
        int limit = Math.Clamp(maxEntries, 1, _ring.Length);
        lock (_gate)
        {
            if (_count == 0)
            {
                return [];
            }

            int take = Math.Min(_count, limit);
            var list = new List<CustomWidgetRuntimeTraceEntry>(take);
            // Return in chronological order (oldest -> newest of the last `take` entries)
            int startOffset = _count - take;
            int oldestIdx = (_nextIndex - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < take; i++)
            {
                int idx = (oldestIdx + startOffset + i) % _ring.Length;
                list.Add(_ring[idx]);
            }

            return list;
        }
    }

    public string FormatForAiPrompt(int maxEntries = 6, bool errorsOnlyWhenQuiet = false)
    {
        IReadOnlyList<CustomWidgetRuntimeTraceEntry> all = SnapshotRecent(_ring.Length);
        if (all.Count == 0)
        {
            return string.Empty;
        }

        List<CustomWidgetRuntimeTraceEntry> selected;
        if (errorsOnlyWhenQuiet)
        {
            selected = all
                .Where(static e => e.Outcome is "error" or "denied")
                .TakeLast(Math.Max(1, maxEntries))
                .ToList();
        }
        else
        {
            var failures = all.Where(static e => e.Outcome is "error" or "denied").TakeLast(Math.Max(1, maxEntries)).ToList();
            if (failures.Count >= maxEntries)
            {
                selected = failures;
            }
            else
            {
                selected = all.TakeLast(Math.Max(1, maxEntries)).ToList();
            }
        }

        if (selected.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(512);
        foreach (CustomWidgetRuntimeTraceEntry e in selected)
        {
            sb.Append("- [").Append(e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"))
              .Append("] [").Append(e.Outcome.ToUpperInvariant()).Append("] ");
            if (!string.IsNullOrWhiteSpace(e.Trigger))
            {
                sb.Append(e.Trigger).Append(" -> ");
            }
            if (!string.IsNullOrWhiteSpace(e.ScriptFunction))
            {
                sb.Append("script:").Append(e.ScriptFunction).Append("() -> ");
            }
            sb.Append(e.ActionType);
            if (!string.IsNullOrWhiteSpace(e.TargetSummary))
            {
                sb.Append('(').Append(e.TargetSummary).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(e.Detail))
            {
                sb.Append(": ").Append(e.Detail);
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
