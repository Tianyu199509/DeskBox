namespace DeskBox.Services;

/// <summary>
/// Process-wide short-lived writeback guard so clipboard writes initiated by
/// DeskBox itself (Clipboard widget copy/paste, QuickCapture copy, File widget
/// Ctrl+C, Todo copy, Custom widget copy) do not re-trigger history ingestion.
/// </summary>
public static class DeskBoxClipboardWriteScope
{
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMilliseconds(2500);
    private static readonly Lock Gate = new();

    private static string? s_lastText;
    private static long s_lastTextTick;
    private static long s_lastImageTick;
    private static string? s_lastPathsKey;
    private static long s_lastPathsTick;
    private static readonly Dictionary<string, long> s_suppressedHashes = new(StringComparer.Ordinal);

    public static void MarkWrite(
        string? text = null,
        bool hasImage = false,
        IEnumerable<string>? paths = null)
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            PruneLocked(now);
            if (!string.IsNullOrEmpty(text))
            {
                s_lastText = NormalizeText(text);
                s_lastTextTick = now;
            }

            if (hasImage)
            {
                s_lastImageTick = now;
            }

            if (paths is not null)
            {
                string key = NormalizePathsKey(paths);
                if (key.Length > 0)
                {
                    s_lastPathsKey = key;
                    s_lastPathsTick = now;
                }
            }
        }
    }

    public static void MarkText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        MarkWrite(text: text);
    }

    public static void MarkContentHash(string? contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        long now = Environment.TickCount64;
        lock (Gate)
        {
            PruneLocked(now);
            s_suppressedHashes[contentHash] = now;
        }
    }

    public static bool ShouldIgnoreImage()
    {
        long now = Environment.TickCount64;
        lock (Gate)
        {
            return s_lastImageTick != 0 &&
                   now - s_lastImageTick <= (long)SuppressionWindow.TotalMilliseconds;
        }
    }

    public static bool ShouldIgnoreText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string normalized = NormalizeText(text);
        long now = Environment.TickCount64;
        lock (Gate)
        {
            return s_lastText is not null &&
                   now - s_lastTextTick <= (long)SuppressionWindow.TotalMilliseconds &&
                   string.Equals(s_lastText, normalized, StringComparison.Ordinal);
        }
    }

    public static bool ShouldIgnorePaths(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return false;
        }

        string key = NormalizePathsKey(paths);
        if (key.Length == 0)
        {
            return false;
        }

        long now = Environment.TickCount64;
        lock (Gate)
        {
            return s_lastPathsKey is not null &&
                   now - s_lastPathsTick <= (long)SuppressionWindow.TotalMilliseconds &&
                   string.Equals(s_lastPathsKey, key, StringComparison.Ordinal);
        }
    }

    public static bool ShouldIgnoreContentHash(string? contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return false;
        }

        long now = Environment.TickCount64;
        lock (Gate)
        {
            PruneLocked(now);
            return s_suppressedHashes.ContainsKey(contentHash);
        }
    }

    internal static void ClearForTesting()
    {
        lock (Gate)
        {
            s_lastText = null;
            s_lastTextTick = 0;
            s_lastImageTick = 0;
            s_lastPathsKey = null;
            s_lastPathsTick = 0;
            s_suppressedHashes.Clear();
        }
    }

    private static void PruneLocked(long now)
    {
        long windowMs = (long)SuppressionWindow.TotalMilliseconds;
        if (s_suppressedHashes.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var (hash, tick) in s_suppressedHashes)
        {
            if (now - tick > windowMs)
            {
                expired ??= [];
                expired.Add(hash);
            }
        }

        if (expired is not null)
        {
            foreach (string hash in expired)
            {
                s_suppressedHashes.Remove(hash);
            }
        }
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string NormalizePathsKey(IEnumerable<string> paths)
    {
        string[] normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\', '/').ToUpperInvariant())
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        return string.Join('|', normalized);
    }
}
