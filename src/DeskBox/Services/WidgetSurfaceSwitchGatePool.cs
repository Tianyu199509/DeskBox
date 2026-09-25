using System.Collections.Concurrent;

namespace DeskBox.Services;

/// <summary>
/// Owns one non-disposed serialization gate per stable Surface id. Gates are
/// independent from HWND lifetime so a hidden group and its later host use
/// the same gate.
/// </summary>
internal sealed class WidgetSurfaceSwitchGatePool
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public int Count => _gates.Count;

    public SemaphoreSlim Get(string surfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        return _gates.GetOrAdd(
            surfaceId,
            static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IDisposable> AcquireManyAsync(
        IEnumerable<string?> surfaceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surfaceIds);
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (string surfaceId in surfaceIds
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Select(id => id!)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(id => id, StringComparer.Ordinal))
            {
                SemaphoreSlim gate = Get(surfaceId);
                await gate.WaitAsync(cancellationToken);
                held.Add(gate);
            }

            return new GateLease(held);
        }
        catch
        {
            ReleaseHeld(held);
            throw;
        }
    }

    public bool Remove(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        // Do not dispose a removed gate: a completed caller may still execute
        // its final Release. Removal is only used after the persisted surface
        // identity has been retired while the group mutation gate is held.
        return _gates.TryRemove(surfaceId, out _);
    }

    public void Clear()
    {
        _gates.Clear();
    }

    private static void ReleaseHeld(IReadOnlyList<SemaphoreSlim> held)
    {
        for (int index = held.Count - 1; index >= 0; index--)
        {
            held[index].Release();
        }
    }

    private sealed class GateLease(List<SemaphoreSlim> held) : IDisposable
    {
        private List<SemaphoreSlim>? _held = held;

        public void Dispose()
        {
            List<SemaphoreSlim>? held = Interlocked.Exchange(ref _held, null);
            if (held is not null)
            {
                ReleaseHeld(held);
            }
        }
    }
}
