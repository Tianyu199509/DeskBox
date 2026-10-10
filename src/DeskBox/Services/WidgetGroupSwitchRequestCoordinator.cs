using DeskBox.Controls;

namespace DeskBox.Services;

/// <summary>
/// Tracks the latest requested member switch for each stable widget surface.
/// A newer request cancels only the older request for the same surface, so
/// independent groups do not interfere with one another.
/// </summary>
internal sealed class WidgetGroupSwitchRequestCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WidgetGroupSwitchRequest> _currentRequests =
        new(StringComparer.Ordinal);

    public WidgetGroupSwitchRequest Begin(
        string groupId,
        string targetWidgetId,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWidgetId);

        WidgetGroupSwitchRequest? previous;
        WidgetGroupSwitchRequest request;
        lock (_gate)
        {
            _currentRequests.Remove(groupId, out previous);
            request = new WidgetGroupSwitchRequest(groupId, targetWidgetId, origin);
            _currentRequests[groupId] = request;
        }

        // Cancellation can synchronously invoke callbacks. Keep it outside the
        // coordinator lock so a callback can safely complete its old request.
        previous?.Cancel();
        previous?.Dispose();
        return request;
    }

    public bool IsCurrent(WidgetGroupSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            return _currentRequests.TryGetValue(request.GroupId, out var current) &&
                   ReferenceEquals(current, request);
        }
    }

    public bool IsCurrentTarget(string groupId, string targetWidgetId)
    {
        if (string.IsNullOrWhiteSpace(groupId) ||
            string.IsNullOrWhiteSpace(targetWidgetId))
        {
            return false;
        }

        lock (_gate)
        {
            return _currentRequests.TryGetValue(groupId, out var current) &&
                   string.Equals(
                       current.TargetWidgetId,
                       targetWidgetId,
                       StringComparison.Ordinal);
        }
    }

    public void Complete(WidgetGroupSwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_currentRequests.TryGetValue(request.GroupId, out var current) &&
                ReferenceEquals(current, request))
            {
                _currentRequests.Remove(request.GroupId);
            }
        }

        request.Dispose();
    }

    public void Cancel(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        WidgetGroupSwitchRequest? request = null;
        lock (_gate)
        {
            _currentRequests.Remove(groupId, out request);
        }

        request?.Cancel();
        request?.Dispose();
    }

    public void CancelAll()
    {
        List<WidgetGroupSwitchRequest> requests;
        lock (_gate)
        {
            requests = _currentRequests.Values.ToList();
            _currentRequests.Clear();
        }

        // Keep callbacks outside the coordinator lock for the same reason as
        // Begin and Cancel.
        foreach (WidgetGroupSwitchRequest request in requests)
        {
            request.Cancel();
            request.Dispose();
        }
    }
}

internal sealed class WidgetGroupSwitchRequest : IDisposable
{
    private readonly CancellationTokenSource _cancellationSource = new();
    private int _disposed;

    public WidgetGroupSwitchRequest(
        string groupId,
        string targetWidgetId,
        WidgetGroupSwitchOrigin origin)
    {
        GroupId = groupId;
        TargetWidgetId = targetWidgetId;
        Origin = origin;
        CancellationToken = _cancellationSource.Token;
    }

    public string GroupId { get; }

    public string TargetWidgetId { get; }
    public WidgetGroupSwitchOrigin Origin { get; }


    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Relative navigation gestures (wheel step, Ctrl+Tab) can only start
    /// from a visible surface. When one reaches execution after the group
    /// was already stowed, the gesture straddled the interruption boundary
    /// and must be dropped instead of committing a silent member flip that
    /// replays on the next reveal (feedback 387). Absolute targets (picker
    /// selection, drag hover) and programmatic changes keep the designed
    /// hidden-surface fast path.
    /// </summary>
    public static bool IsRelativeGestureOrigin(WidgetGroupSwitchOrigin origin)
    {
        return origin is WidgetGroupSwitchOrigin.Wheel or
            WidgetGroupSwitchOrigin.Keyboard;
    }

    public void Cancel()
    {
        try
        {
            _cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Complete/Cancel may race on shutdown; cancellation is already
            // terminal once the source has been disposed.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellationSource.Dispose();
        }
    }
}
