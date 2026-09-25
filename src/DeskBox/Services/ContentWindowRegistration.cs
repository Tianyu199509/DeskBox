namespace DeskBox.Services;

/// <summary>
/// Mutates WidgetManager's existing ID dictionary and HWND set together. It
/// owns no second collection; callers remain responsible for UI-thread access.
/// </summary>
internal sealed class ContentWindowRegistration<TWindow> where TWindow : class
{
    private readonly Dictionary<string, TWindow> _byId;
    private readonly HashSet<IntPtr> _handles;
    private readonly Func<TWindow, IntPtr> _handleOf;

    internal ContentWindowRegistration(Dictionary<string, TWindow> byId,
        HashSet<IntPtr> handles, Func<TWindow, IntPtr> handleOf)
    {
        _byId = byId;
        _handles = handles;
        _handleOf = handleOf;
    }

    internal void Register(string id, TWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(window);
        if (_byId.TryGetValue(id, out TWindow? registered) &&
            !ReferenceEquals(registered, window))
            throw new InvalidOperationException($"Widget ID '{id}' already has a different content window.");
        if (_byId.Any(entry => entry.Key != id && ReferenceEquals(entry.Value, window)))
            throw new InvalidOperationException("Content window is already registered under another widget ID.");
        IntPtr handle = _handleOf(window);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Content window handle is not ready for registration.");
        if (_byId.Values.Any(candidate => !ReferenceEquals(candidate, window) &&
                                    _handleOf(candidate) == handle))
            throw new InvalidOperationException("Content window handle is already owned by another window.");

        _byId[id] = window;
        _handles.Add(handle);
    }

    internal bool CanRebind(string targetId, TWindow window) =>
        _byId.Values.Any(candidate => ReferenceEquals(candidate, window)) &&
        (!_byId.TryGetValue(targetId, out TWindow? registered) ||
         ReferenceEquals(registered, window));

    internal void Rebind(string targetId, TWindow window)
    {
        if (!CanRebind(targetId, window))
            throw new InvalidOperationException($"Cannot rebind content window to widget ID '{targetId}'.");
        foreach (string id in RegisteredIds(window)) _byId.Remove(id);
        _byId[targetId] = window;
        _handles.Add(_handleOf(window));
    }

    internal IReadOnlyList<string> Unregister(TWindow window)
    {
        List<string> ids = RegisteredIds(window);
        foreach (string id in ids) _byId.Remove(id);
        RemoveUnusedHandle(window);
        return ids;
    }

    internal bool UnregisterIfMatch(string id, TWindow window)
    {
        if (!_byId.TryGetValue(id, out TWindow? current) ||
            !ReferenceEquals(current, window)) return false;
        _byId.Remove(id);
        RemoveUnusedHandle(window);
        return true;
    }

    internal void Clear()
    {
        _byId.Clear();
        _handles.Clear();
    }

    private List<string> RegisteredIds(TWindow window) =>
        _byId.Where(entry => ReferenceEquals(entry.Value, window))
            .Select(entry => entry.Key).ToList();

    private void RemoveUnusedHandle(TWindow window)
    {
        IntPtr handle = _handleOf(window);
        if (!_byId.Values.Any(candidate => _handleOf(candidate) == handle))
            _handles.Remove(handle);
    }
}
