namespace DeskBox.Services.DragDrop;

/// <summary>
/// Tracks a short-lived, single-use Windows 10 Move→Copy fallback token after
/// a non-Explorer target (such as a browser or chat app) rejects a single-bit
/// <c>Move</c> drag. Re-dragging the exact same file batch within the TTL
/// promotes only that single drag to single-bit <c>Copy</c> and immediately
/// consumes the token so subsequent drops to Desktop/Explorer stay <c>Move</c>.
/// </summary>
internal sealed class Win10MoveCopyFallbackPolicy
{
    internal const long DefaultTtlMs = 15_000;

    internal static Win10MoveCopyFallbackPolicy Shared { get; } = new();

    private readonly object _gate = new();
    private readonly long _ttlMs;
    private readonly Func<long> _tickProvider;
    private string[]? _armedNormalizedPaths;
    private long _armedTick64;

    internal Win10MoveCopyFallbackPolicy(
        long ttlMs = DefaultTtlMs,
        Func<long>? tickProvider = null)
    {
        _ttlMs = Math.Max(1_000, ttlMs);
        _tickProvider = tickProvider ?? (static () => Environment.TickCount64);
    }

    internal void ArmForRejectedPaths(IReadOnlyList<string> sourcePaths)
    {
        string[] normalized = NormalizePaths(sourcePaths);
        if (normalized.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _armedNormalizedPaths = normalized;
            _armedTick64 = _tickProvider();
        }
    }

    internal bool TryConsumeForPaths(IReadOnlyList<string> sourcePaths)
    {
        string[] candidate = NormalizePaths(sourcePaths);
        if (candidate.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_armedNormalizedPaths is null)
            {
                return false;
            }

            long elapsed = _tickProvider() - _armedTick64;
            if (elapsed < 0 || elapsed > _ttlMs)
            {
                _armedNormalizedPaths = null;
                _armedTick64 = 0;
                return false;
            }

            if (!AreSamePathSet(_armedNormalizedPaths, candidate))
            {
                return false;
            }

            _armedNormalizedPaths = null;
            _armedTick64 = 0;
            return true;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _armedNormalizedPaths = null;
            _armedTick64 = 0;
        }
    }

    private static string[] NormalizePaths(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
        {
            string raw = paths[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
            }
            catch
            {
                normalized = raw.Trim();
            }

            if (normalized.Length > 0)
            {
                unique.Add(normalized);
            }
        }

        if (unique.Count == 0)
        {
            return [];
        }

        string[] result = [.. unique];
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool AreSamePathSet(string[] left, string[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
