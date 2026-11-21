namespace DeskBox.Services;

/// <summary>
/// Process-wide registry of in-flight DeskBox file drags. The XAML payload
/// token (<see cref="DeskBoxDragData.InternalFileDragTokenProperty"/>) is not
/// readable through the OLE <c>IDataObject</c> our native drop target sees,
/// so the native side identifies a DeskBox-originated drag by comparing the
/// dropped <c>CF_HDROP</c> paths against the paths an active DeskBox drag
/// registered at DragStarting time.
/// </summary>
internal static class ActiveDeskBoxDragRegistry
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Active = [];

    // A modal OLE drag cannot outlast this window, but a lost DropCompleted
    // would otherwise strand an entry forever and permanently classify the
    // file as self-dragged on every later drop.
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(10);

    private sealed class Entry(
        string sessionId,
        string widgetId,
        HashSet<string> paths,
        bool fromStackPopover,
        DateTime registeredAtUtc,
        string? sourceStackKey = null,
        string? stackReorderKey = null,
        bool isNativeShellDrag = false)
    {
        public string SessionId { get; } = sessionId;
        public string WidgetId { get; } = widgetId;
        public HashSet<string> Paths { get; } = paths;
        public bool FromStackPopover { get; } = fromStackPopover;
        public DateTime RegisteredAtUtc { get; } = registeredAtUtc;
        public string? SourceStackKey { get; } = sourceStackKey;
        public string? StackReorderKey { get; } = stackReorderKey;
        public bool IsNativeShellDrag { get; } = isNativeShellDrag;
        public string? ClaimedByTargetWidgetId { get; set; }
    }

    internal readonly record struct MatchedDragEntry(
        string SessionId,
        string WidgetId,
        bool FromStackPopover,
        string? SourceStackKey,
        string? StackReorderKey,
        bool IsNativeShellDrag);

    internal static void Begin(
        string sessionId,
        string widgetId,
        IReadOnlyCollection<string> paths,
        bool fromStackPopover,
        DateTime? utcNow = null,
        IReadOnlyCollection<string>? physicalPaths = null,
        string? sourceStackKey = null,
        string? stackReorderKey = null,
        bool isNativeShellDrag = false)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (physicalPaths is not null)
        {
            foreach (string physicalPath in physicalPaths)
            {
                if (string.IsNullOrWhiteSpace(physicalPath))
                {
                    continue;
                }

                try
                {
                    normalized.Add(Path.GetFullPath(physicalPath));
                }
                catch
                {
                }
            }
        }

        if (normalized.Count == 0)
        {
            return;
        }

        DateTime now = utcNow ?? DateTime.UtcNow;
        lock (Gate)
        {
            Active.RemoveAll(
                entry => now - entry.RegisteredAtUtc > EntryLifetime);
            // An OLE drag is modal to the pointer: when a new drag begins,
            // no earlier registration can still be in flight. Any entry
            // still present belongs to a drag whose DropCompleted was lost
            // and would otherwise misclassify this same file set as
            // self-dragged for up to EntryLifetime on its next drop.
            Active.Clear();
            Active.Add(new Entry(
                sessionId,
                widgetId,
                normalized,
                fromStackPopover,
                now,
                sourceStackKey,
                stackReorderKey,
                isNativeShellDrag));
        }
    }

    private static int s_activePointerDragCount;

    internal static bool HasActivePointerDragSession =>
        Volatile.Read(ref s_activePointerDragCount) > 0;

    internal static void BeginPointerDragSession() =>
        Interlocked.Increment(ref s_activePointerDragCount);

    internal static void EndPointerDragSession()
    {
        int current = Volatile.Read(ref s_activePointerDragCount);
        while (current > 0)
        {
            int observed = Interlocked.CompareExchange(
                ref s_activePointerDragCount,
                current - 1,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// Synchronously marks an in-flight DeskBox drag session as claimed by a
    /// DeskBox <c>IDropTarget</c> (main widget surface or stack popover) while
    /// <c>SHDoDragDrop</c> is still on the stack, so the source completion frame
    /// does not treat the drop as an external Explorer/third-party transfer.
    /// </summary>
    internal static void MarkClaimedByDeskBoxDropTarget(
        string? sessionId,
        string? targetWidgetId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(targetWidgetId))
        {
            return;
        }

        lock (Gate)
        {
            foreach (Entry entry in Active)
            {
                if (string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                {
                    entry.ClaimedByTargetWidgetId = targetWidgetId;
                    return;
                }
            }
        }
    }

    internal static void End(string sessionId) =>
        End(sessionId, out _);

    internal static void End(string sessionId, out string? claimedByTargetWidgetId)
    {
        claimedByTargetWidgetId = null;
        lock (Gate)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (Active[i].SessionId == sessionId)
                {
                    claimedByTargetWidgetId ??= Active[i].ClaimedByTargetWidgetId;
                    Active.RemoveAt(i);
                }
            }
        }
    }

    internal static bool HasActiveDrag
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                Active.RemoveAll(
                    entry => now - entry.RegisteredAtUtc > EntryLifetime);
                return Active.Count > 0;
            }
        }
    }

    internal static bool HasActiveNativeShellDrag
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            lock (Gate)
            {
                Active.RemoveAll(
                    entry => now - entry.RegisteredAtUtc > EntryLifetime);
                for (int i = 0; i < Active.Count; i++)
                {
                    if (Active[i].IsNativeShellDrag)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    internal static bool IsActiveNativeShellDragFromWidget(string? widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        lock (Gate)
        {
            Active.RemoveAll(
                entry => now - entry.RegisteredAtUtc > EntryLifetime);
            for (int i = 0; i < Active.Count; i++)
            {
                Entry entry = Active[i];
                if (entry.IsNativeShellDrag &&
                    string.Equals(entry.WidgetId, widgetId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// True when every dropped path belongs to one in-flight DeskBox drag —
    /// i.e. the payload is DeskBox's own file drag and not an Explorer or
    /// third-party drop that happens to share a path.
    /// </summary>
    internal static bool TryMatch(
        IReadOnlyCollection<string> paths,
        out string widgetId,
        out bool fromStackPopover,
        DateTime? utcNow = null) =>
        TryMatch(paths, out _, out widgetId, out fromStackPopover, utcNow);

    internal static bool TryMatch(
        IReadOnlyCollection<string> paths,
        out string sessionId,
        out string widgetId,
        out bool fromStackPopover,
        DateTime? utcNow = null)
    {
        if (TryMatchDetailed(paths, out MatchedDragEntry matched, utcNow))
        {
            sessionId = matched.SessionId;
            widgetId = matched.WidgetId;
            fromStackPopover = matched.FromStackPopover;
            return true;
        }

        sessionId = string.Empty;
        widgetId = string.Empty;
        fromStackPopover = false;
        return false;
    }

    internal static bool IsActiveCollapsedStackDrag(
        IReadOnlyCollection<string>? paths,
        DateTime? utcNow = null) =>
        paths is { Count: > 0 } &&
        TryMatchDetailed(paths, out MatchedDragEntry matched, utcNow) &&
        !string.IsNullOrWhiteSpace(matched.StackReorderKey);

    internal static bool TryMatchDetailed(
        IReadOnlyCollection<string> paths,
        out MatchedDragEntry matched,
        DateTime? utcNow = null)
    {
        matched = default;
        if (paths.Count == 0)
        {
            return false;
        }

        DateTime now = utcNow ?? DateTime.UtcNow;
        lock (Gate)
        {
            Active.RemoveAll(
                entry => now - entry.RegisteredAtUtc > EntryLifetime);

            foreach (Entry entry in Active)
            {
                bool allMatch = true;
                foreach (string path in paths)
                {
                    string full;
                    try
                    {
                        full = Path.GetFullPath(path);
                    }
                    catch
                    {
                        allMatch = false;
                        break;
                    }

                    if (!entry.Paths.Contains(full))
                    {
                        allMatch = false;
                        break;
                    }
                }

                if (allMatch)
                {
                    matched = new MatchedDragEntry(
                        entry.SessionId,
                        entry.WidgetId,
                        entry.FromStackPopover,
                        entry.SourceStackKey,
                        entry.StackReorderKey,
                        entry.IsNativeShellDrag);
                    return true;
                }
            }
        }

        return false;
    }
}
