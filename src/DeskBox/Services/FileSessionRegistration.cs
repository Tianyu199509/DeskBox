namespace DeskBox.Services;

/// <summary>
/// Mutates WidgetManager's existing standalone-file session dictionary. A
/// host has at most one standalone alias; group surfaces have none.
/// </summary>
internal sealed class FileSessionRegistration<TSession, THost>
    where TSession : class where THost : class
{
    private readonly Dictionary<string, TSession> _byId;
    private readonly Func<TSession, THost> _hostOf;
    private readonly Func<TSession, object?> _contentOf;

    internal FileSessionRegistration(Dictionary<string, TSession> byId,
        Func<TSession, THost> hostOf, Func<TSession, object?> contentOf)
    {
        _byId = byId;
        _hostOf = hostOf;
        _contentOf = contentOf;
    }

    /// <returns>True when a new session was installed; false for an exact replay.</returns>
    internal bool RegisterOrReplace(string id, TSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(session);
        THost host = _hostOf(session);
        if (_byId.Any(entry => entry.Key != id &&
                               ReferenceEquals(_hostOf(entry.Value), host)))
            throw new InvalidOperationException("A file host already has another standalone widget ID.");
        if (_byId.TryGetValue(id, out TSession? current) &&
            ReferenceEquals(_hostOf(current), host) &&
            ReferenceEquals(_contentOf(current), _contentOf(session)))
            return false;

        _byId[id] = session;
        return true;
    }

    internal bool UnregisterIfMatch(string id, TSession session)
    {
        if (!_byId.TryGetValue(id, out TSession? current) ||
            !ReferenceEquals(current, session)) return false;
        _byId.Remove(id);
        return true;
    }

    internal IReadOnlyList<string> UnregisterHost(THost host)
    {
        List<string> ids = _byId.Where(entry => ReferenceEquals(_hostOf(entry.Value), host))
            .Select(entry => entry.Key).ToList();
        foreach (string id in ids) _byId.Remove(id);
        return ids;
    }

    internal void Clear() => _byId.Clear();
}
