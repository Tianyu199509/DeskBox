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

    private sealed record Entry(
        string SessionId,
        string WidgetId,
        HashSet<string> Paths,
        bool FromStackPopover,
        DateTime RegisteredAtUtc);

    internal static void Begin(
        string sessionId,
        string widgetId,
        IReadOnlyCollection<string> paths,
        bool fromStackPopover,
        DateTime? utcNow = null)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                now));
        }
    }

    internal static void End(string sessionId)
    {
        lock (Gate)
        {
            Active.RemoveAll(entry => entry.SessionId == sessionId);
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

    /// <summary>
    /// True when every dropped path belongs to one in-flight DeskBox drag —
    /// i.e. the payload is DeskBox's own file drag and not an Explorer or
    /// third-party drop that happens to share a path.
    /// </summary>
    internal static bool TryMatch(
        IReadOnlyCollection<string> paths,
        out string widgetId,
        out bool fromStackPopover,
        DateTime? utcNow = null)
    {
        widgetId = string.Empty;
        fromStackPopover = false;
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
                    widgetId = entry.WidgetId;
                    fromStackPopover = entry.FromStackPopover;
                    return true;
                }
            }
        }

        return false;
    }
}
