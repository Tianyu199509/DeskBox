using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// One decoded source per shared asset file (the global unified
/// background and the panorama) for every widget window in the process —
/// the explicit replacement for the cross-window dedupe the framework's
/// URI-keyed cache used to provide before these sources moved to
/// streams. Entries revalidate against the file's length + last-write
/// stamp, so a re-pick that rewrites the file produces a fresh decode
/// instead of a stale shared surface. The settle callbacks replace
/// per-window event subscriptions on the shared source (no window keeps
/// the cached instance's events pinned): a decode that fails mid-flight
/// is pinned by the file stamp, every registered failure callback runs
/// once so each window degrades to its fallback layer, and re-applies
/// keep degrading until the file content changes; a successful decode
/// notifies the registered success callbacks (the panorama positions
/// its slice then). UI thread only — windows resolve during apply on
/// the dispatcher, and the stream settle callbacks arrive back on the
/// same thread.
/// </summary>
internal static class WidgetSharedImageSourceCache
{
    private sealed class Entry
    {
        public required long Length { get; init; }

        public required DateTime LastWriteUtc { get; init; }

        public required ImageSource Source { get; init; }

        public List<Action>? PendingFailureCallbacks { get; set; }

        public List<Action>? PendingSuccessCallbacks { get; set; }
    }

    private static readonly Dictionary<string, Entry> s_entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> s_failedStamps =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Clear()
    {
        s_entries.Clear();
        s_failedStamps.Clear();
    }

    public static void EvictExcept(string? activePath1, string? activePath2 = null)
    {
        if (s_entries.Count == 0 && s_failedStamps.Count == 0)
        {
            return;
        }

        List<string>? staleKeys = null;
        foreach (string key in s_entries.Keys)
        {
            if (!PathsEqual(key, activePath1) && !PathsEqual(key, activePath2))
            {
                (staleKeys ??= []).Add(key);
            }
        }

        if (staleKeys is not null)
        {
            foreach (string key in staleKeys)
            {
                s_entries.Remove(key);
            }
        }

        staleKeys = null;
        foreach (string key in s_failedStamps.Keys)
        {
            if (!PathsEqual(key, activePath1) && !PathsEqual(key, activePath2))
            {
                (staleKeys ??= []).Add(key);
            }
        }

        if (staleKeys is not null)
        {
            foreach (string key in staleKeys)
            {
                s_failedStamps.Remove(key);
            }
        }
    }

    private static bool PathsEqual(string left, string? right) =>
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static ImageSource? TryGet(
        string? filePath,
        Func<string, ImageSource?> sourceFactory,
        Action? loadFailed = null,
        Action? loadSucceeded = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                s_entries.Remove(filePath);
                s_failedStamps.Remove(filePath);
                return null;
            }

            var info = new FileInfo(filePath);
            (long length, DateTime stamp) = (info.Length, info.LastWriteTimeUtc);

            if (s_failedStamps.TryGetValue(
                    filePath,
                    out (long Length, DateTime LastWriteUtc) failed) &&
                failed.Length == length &&
                failed.LastWriteUtc == stamp)
            {
                // Pinned failure: keep degrading until the content changes.
                return null;
            }

            s_failedStamps.Remove(filePath);

            if (s_entries.TryGetValue(filePath, out Entry? entry) &&
                entry.Length == length &&
                entry.LastWriteUtc == stamp)
            {
                if (loadFailed is not null)
                {
                    // Pending loads take more callbacks; settled entries
                    // cannot fail anymore (null list skips).
                    entry.PendingFailureCallbacks?.Add(loadFailed);
                }

                if (loadSucceeded is not null)
                {
                    entry.PendingSuccessCallbacks?.Add(loadSucceeded);
                }

                return entry.Source;
            }

            // Content changed (or first resolve): the stale decode drops
            // with the old entry and this resolves decode fresh.
            s_entries.Remove(filePath);
            entry = new Entry
            {
                Length = length,
                LastWriteUtc = stamp,
                Source = sourceFactory(filePath) ??
                    throw new InvalidOperationException(
                        $"The source factory refused '{filePath}'."),
                PendingFailureCallbacks = loadFailed is null ? [] : [loadFailed],
                PendingSuccessCallbacks = loadSucceeded is null ? [] : [loadSucceeded],
            };
            s_entries[filePath] = entry;
            WidgetImageStreamLoader.Fill(
                entry.Source,
                filePath,
                settled => Settle(filePath, entry, settled));
            return entry.Source;
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSharedImageSourceCache] Resolve failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    private static void Settle(string filePath, Entry entry, bool succeeded)
    {
        if (!succeeded &&
            s_entries.TryGetValue(filePath, out Entry? current) &&
            ReferenceEquals(current, entry))
        {
            // A newer decode (a re-pick racing this load) must not be
            // evicted by an older fill's failure.
            s_entries.Remove(filePath);
            s_failedStamps[filePath] = (entry.Length, entry.LastWriteUtc);
        }

        List<Action>? callbacks = succeeded
            ? entry.PendingSuccessCallbacks
            : entry.PendingFailureCallbacks;
        entry.PendingFailureCallbacks = null;
        entry.PendingSuccessCallbacks = null;
        if (callbacks is null)
        {
            return;
        }

        foreach (Action callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetSharedImageSourceCache] Settle callback threw: {ex.Message}");
            }
        }
    }
}
