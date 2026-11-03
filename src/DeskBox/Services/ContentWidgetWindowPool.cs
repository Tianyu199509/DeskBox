using DeskBox.Models;

namespace DeskBox.Services;

internal readonly record struct HiddenContentHostCandidate(
    string WidgetId,
    WidgetKind WidgetKind,
    bool IsVisible,
    bool IsNativeVisible,
    bool IsClosing,
    bool IsTransitionActive,
    bool IsGroupMember,
    bool HasFolderWatcherObligation);

/// <summary>
/// Bounded warm-host retention and shell pooling policy for <see cref="Views.ContentWidgetWindow"/>.
/// Keeps at least <see cref="MinRetainedHiddenWindowCount"/> desktop XAML window alive so
/// <c>DXamlCore</c> never tears down the UI message loop while reclaiming excess long-hidden
/// recreatable widget HWNDs, <c>DesktopWindowXamlSource</c> instances, and DXGI swapchains.
/// </summary>
internal static class ContentWidgetWindowPool
{
    public const int MinRetainedHiddenWindowCount = 1;
    public const int MaxWarmRecreatableHiddenWindowsSmall = 1;
    public const int MaxWarmRecreatableHiddenWindowsBalanced = 2;
    public const int DefaultShellPoolCapacity = 2;

    public static int ResolveWarmHiddenHostCapacity(
        string? cleanupScope,
        string? cacheBudget)
    {
        string normalizedScope =
            PerformanceSettingsPolicy.NormalizeHiddenCacheCleanupScope(cleanupScope);
        if (!string.Equals(
                normalizedScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        return PerformanceSettingsPolicy.NormalizeCacheBudget(cacheBudget) switch
        {
            PerformanceSettingsPolicy.CacheBudgetSmall =>
                MaxWarmRecreatableHiddenWindowsSmall,
            _ => MaxWarmRecreatableHiddenWindowsBalanced
        };
    }

    public static IReadOnlyList<string> SelectExcessHiddenHostsToEvict(
        IReadOnlyList<HiddenContentHostCandidate> candidates,
        int totalLoadedDesktopWindows,
        string? cleanupScope,
        string? cacheBudget)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        int warmCapacity = ResolveWarmHiddenHostCapacity(cleanupScope, cacheBudget);
        if (warmCapacity == int.MaxValue ||
            totalLoadedDesktopWindows <= MinRetainedHiddenWindowCount ||
            candidates.Count == 0)
        {
            return [];
        }

        List<HiddenContentHostCandidate> eligible = [];
        foreach (HiddenContentHostCandidate candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.WidgetId) ||
                candidate.IsVisible ||
                candidate.IsNativeVisible ||
                candidate.IsClosing ||
                candidate.IsTransitionActive ||
                candidate.IsGroupMember ||
                candidate.HasFolderWatcherObligation ||
                candidate.WidgetKind == WidgetKind.File)
            {
                continue;
            }

            eligible.Add(candidate);
        }

        int excessAboveWarmBudget = eligible.Count - warmCapacity;
        int excessAboveLifelineFloor =
            totalLoadedDesktopWindows - MinRetainedHiddenWindowCount;
        int targetEvictCount = Math.Min(
            Math.Max(0, excessAboveWarmBudget),
            Math.Max(0, excessAboveLifelineFloor));
        if (targetEvictCount <= 0)
        {
            return [];
        }

        eligible.Sort(static (left, right) =>
        {
            int priorityComparison =
                GetEvictionPriority(left.WidgetKind).CompareTo(
                    GetEvictionPriority(right.WidgetKind));
            return priorityComparison != 0
                ? priorityComparison
                : StringComparer.Ordinal.Compare(left.WidgetId, right.WidgetId);
        });

        var selected = new string[targetEvictCount];
        for (int index = 0; index < targetEvictCount; index++)
        {
            selected[index] = eligible[index].WidgetId;
        }

        return selected;
    }

    private static int GetEvictionPriority(WidgetKind kind) =>
        kind switch
        {
            // Heaviest Composition / bitmap / RichText / ItemsRepeater trees evict first.
            WidgetKind.Glance => 0,
            WidgetKind.Weather => 1,
            WidgetKind.Custom => 2,
            WidgetKind.Music => 3,
            WidgetKind.Todo => 4,
            WidgetKind.QuickCapture => 5,
            _ => 6
        };
}

/// <summary>
/// Bounded FIFO/LIFO warm shell parking ring with deterministic eviction when trimmed.
/// </summary>
internal sealed class WarmShellPool<TShell> where TShell : class
{
    private readonly List<TShell> _parked = [];
    private readonly Action<TShell> _destroyShell;
    private int _maxCapacity;

    public WarmShellPool(
        int maxCapacity,
        Action<TShell> destroyShell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        ArgumentNullException.ThrowIfNull(destroyShell);
        _maxCapacity = maxCapacity;
        _destroyShell = destroyShell;
    }

    public int Count => _parked.Count;

    public int MaxCapacity => _maxCapacity;

    public void SetMaxCapacity(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        _maxCapacity = maxCapacity;
        TrimToCapacity(_maxCapacity);
    }

    public bool TryAcquire(out TShell? shell)
    {
        if (_parked.Count == 0)
        {
            shell = null;
            return false;
        }

        int lastIndex = _parked.Count - 1;
        shell = _parked[lastIndex];
        _parked.RemoveAt(lastIndex);
        return true;
    }

    public bool ParkOrDestroy(TShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_parked.Count >= _maxCapacity)
        {
            _destroyShell(shell);
            return false;
        }

        _parked.Add(shell);
        return true;
    }

    public int TrimToCapacity(int targetCapacity)
    {
        targetCapacity = Math.Max(0, targetCapacity);
        int destroyed = 0;
        while (_parked.Count > targetCapacity)
        {
            TShell evicted = _parked[0];
            _parked.RemoveAt(0);
            _destroyShell(evicted);
            destroyed++;
        }

        return destroyed;
    }
}
