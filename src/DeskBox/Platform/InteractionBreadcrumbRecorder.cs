namespace DeskBox.Platform;

/// <summary>
/// Bounded, zero-idle-cost circular buffer recording recent interaction, focus,
/// and animation-transition breadcrumbs (tags and static state tokens only;
/// never user text or file paths) so freeze diagnostics include immediate context
/// without enabling verbose logging or background timers.
/// </summary>
internal static class InteractionBreadcrumbRecorder
{
    internal const int Capacity = 24;

    private static readonly object s_sync = new();
    private static readonly string[] s_ring = new string[Capacity];
    private static int s_nextIndex;
    private static int s_count;
    private static string? s_latest;

    public static void Record(string tag, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        string entry = string.IsNullOrWhiteSpace(detail)
            ? $"{DateTime.UtcNow:HH:mm:ss.fff} [{tag}]"
            : $"{DateTime.UtcNow:HH:mm:ss.fff} [{tag}] {detail}";

        lock (s_sync)
        {
            s_ring[s_nextIndex] = entry;
            s_nextIndex = (s_nextIndex + 1) % Capacity;
            if (s_count < Capacity)
            {
                s_count++;
            }

            s_latest = entry;
        }
    }

    public static string? GetLatest()
    {
        lock (s_sync)
        {
            return s_latest;
        }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (s_sync)
        {
            if (s_count == 0)
            {
                return [];
            }

            var result = new string[s_count];
            int startIndex = (s_nextIndex - s_count + Capacity) % Capacity;
            for (int i = 0; i < s_count; i++)
            {
                result[i] = s_ring[(startIndex + i) % Capacity];
            }

            return result;
        }
    }

    internal static void ResetForTests()
    {
        lock (s_sync)
        {
            Array.Clear(s_ring);
            s_nextIndex = 0;
            s_count = 0;
            s_latest = null;
        }
    }
}
