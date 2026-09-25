namespace DeskBox.Services;

/// <summary>
/// Runtime source of truth for the physical host owned by a widget surface.
/// Persistent member ids remain lookup aliases; the surface id is the stable
/// key and does not change when the active member changes.
/// </summary>
internal sealed class WidgetSurfaceRegistry<THost>
    where THost : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WidgetSurfaceSession<THost>> _sessions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _surfaceIdByMemberId =
        new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public IReadOnlyList<WidgetSurfaceSession<THost>> GetSessions()
    {
        lock (_gate)
        {
            return _sessions.Values.ToList();
        }
    }

    public WidgetSurfaceSession<THost> RegisterActive(
        WidgetSurfaceDefinition definition,
        THost host,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<THost>>? expectedRetiringClaims = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (_sessions.TryGetValue(definition.SurfaceId, out var existing))
            {
                if (!ReferenceEquals(existing.Host, host))
                {
                    throw new InvalidOperationException(
                        $"Surface '{definition.SurfaceId}' already owns another active host.");
                }

                List<WidgetSurfaceSession<THost>> retiring =
                    ValidateMemberClaimTransfer(definition, expectedRetiringClaims);
                ReindexMembers(existing.MemberIds, definition, retiring);
                existing.UpdateDefinition(definition);
                return existing;
            }

            List<WidgetSurfaceSession<THost>> newRetiring =
                ValidateMemberClaimTransfer(definition, expectedRetiringClaims);
            RetireClaimedSurfaces(newRetiring);
            var session = new WidgetSurfaceSession<THost>(definition, host);
            _sessions.Add(definition.SurfaceId, session);
            IndexMembers(definition);
            return session;
        }
    }

    /// <summary>
    /// Reconciles the registry with an already-stable runtime host. This is
    /// used at restore and after group topology changes, never as the switch
    /// commit path.
    /// </summary>
    public WidgetSurfaceSession<THost> SynchronizeActive(
        WidgetSurfaceDefinition definition,
        THost host,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<THost>>? expectedRetiringClaims = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (_sessions.TryGetValue(definition.SurfaceId, out var existing))
            {
                List<WidgetSurfaceSession<THost>> retiring =
                    ValidateMemberClaimTransfer(definition, expectedRetiringClaims);
                ReindexMembers(existing.MemberIds, definition, retiring);
                existing.CommitActive(definition, host);
                return existing;
            }

            List<WidgetSurfaceSession<THost>> newRetiring =
                ValidateMemberClaimTransfer(definition, expectedRetiringClaims);
            RetireClaimedSurfaces(newRetiring);
            var session = new WidgetSurfaceSession<THost>(definition, host);
            _sessions.Add(definition.SurfaceId, session);
            IndexMembers(definition);
            return session;
        }
    }

    public bool StageCandidate(
        string surfaceId,
        string targetMemberId,
        THost candidateHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMemberId);
        ArgumentNullException.ThrowIfNull(candidateHost);

        lock (_gate)
        {
            if (!_sessions.TryGetValue(surfaceId, out var session) ||
                !session.MemberIds.Contains(targetMemberId, StringComparer.Ordinal))
            {
                return false;
            }

            return session.StageCandidate(targetMemberId, candidateHost);
        }
    }

    public WidgetSurfaceSession<THost> CommitActive(
        WidgetSurfaceDefinition definition,
        THost host,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<THost>>? expectedRetiringClaims = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        definition.Validate();

        lock (_gate)
        {
            if (!_sessions.TryGetValue(definition.SurfaceId, out var session))
            {
                return RegisterActive(definition, host, expectedRetiringClaims);
            }

            if (!ReferenceEquals(session.Host, host) &&
                (!ReferenceEquals(session.CandidateHost, host) ||
                 !string.Equals(
                     session.CandidateMemberId,
                     definition.ActiveMemberId,
                     StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Host was not prepared for surface '{definition.SurfaceId}'.");
            }

            List<WidgetSurfaceSession<THost>> retiring =
                ValidateMemberClaimTransfer(definition, expectedRetiringClaims);
            ReindexMembers(session.MemberIds, definition, retiring);
            session.CommitActive(definition, host);
            return session;
        }
    }

    public bool CancelCandidate(string surfaceId, THost candidateHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        ArgumentNullException.ThrowIfNull(candidateHost);

        lock (_gate)
        {
            return _sessions.TryGetValue(surfaceId, out var session) &&
                   session.CancelCandidate(candidateHost);
        }
    }

    public bool UpdateDefinition(WidgetSurfaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();

        lock (_gate)
        {
            if (!_sessions.TryGetValue(definition.SurfaceId, out var session))
            {
                return false;
            }

            List<WidgetSurfaceSession<THost>> retiring =
                ValidateMemberClaimTransfer(definition, expectedRetiringClaims: null);
            ReindexMembers(session.MemberIds, definition, retiring);
            session.UpdateDefinition(definition);
            return true;
        }
    }

    public bool TryGet(
        string surfaceId,
        out WidgetSurfaceSession<THost>? session)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            session = null;
            return false;
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(surfaceId, out session);
        }
    }

    public bool TryGetByMember(
        string memberId,
        out WidgetSurfaceSession<THost>? session)
    {
        if (string.IsNullOrWhiteSpace(memberId))
        {
            session = null;
            return false;
        }

        lock (_gate)
        {
            if (!_surfaceIdByMemberId.TryGetValue(memberId, out string? surfaceId))
            {
                session = null;
                return false;
            }

            return _sessions.TryGetValue(surfaceId, out session);
        }
    }

    public bool RemoveSurface(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.Remove(surfaceId, out var session))
            {
                return false;
            }

            RemoveIndexedMembers(session.MemberIds, surfaceId);
            session.Dispose();
            return true;
        }
    }

    public int UnregisterHost(THost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        lock (_gate)
        {
            int removed = 0;
            foreach (WidgetSurfaceSession<THost> session in _sessions.Values.ToList())
            {
                if (ReferenceEquals(session.CandidateHost, host))
                {
                    session.CancelCandidate(host);
                }

                if (!ReferenceEquals(session.Host, host))
                {
                    continue;
                }

                _sessions.Remove(session.SurfaceId);
                RemoveIndexedMembers(session.MemberIds, session.SurfaceId);
                session.Dispose();
                removed++;
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (WidgetSurfaceSession<THost> session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
            _surfaceIdByMemberId.Clear();
        }
    }

    private void ReindexMembers(
        IReadOnlyList<string> previousMemberIds,
        WidgetSurfaceDefinition definition,
        IReadOnlyList<WidgetSurfaceSession<THost>> retiringSessions)
    {
        RemoveIndexedMembers(previousMemberIds, definition.SurfaceId);
        RetireClaimedSurfaces(retiringSessions);
        IndexMembers(definition);
    }

    private void IndexMembers(WidgetSurfaceDefinition definition)
    {
        foreach (string memberId in definition.MemberIds)
        {
            _surfaceIdByMemberId[memberId] = definition.SurfaceId;
        }
    }

    private List<WidgetSurfaceSession<THost>> ValidateMemberClaimTransfer(
        WidgetSurfaceDefinition definition,
        IReadOnlyCollection<WidgetSurfaceClaimTransfer<THost>>? expectedRetiringClaims)
    {
        var claimed = new HashSet<WidgetSurfaceSession<THost>>(
            ReferenceEqualityComparer.Instance);
        foreach (string memberId in definition.MemberIds)
        {
            if (!_surfaceIdByMemberId.TryGetValue(memberId, out string? claimedSurfaceId) ||
                string.Equals(claimedSurfaceId, definition.SurfaceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_sessions.TryGetValue(claimedSurfaceId, out var claimedSession) ||
                !claimedSession.MemberIds.Contains(memberId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Member '{memberId}' has an inconsistent Surface claim.");
            }

            claimed.Add(claimedSession);
        }

        var expected = new HashSet<WidgetSurfaceSession<THost>>(
            (expectedRetiringClaims ?? [])
                .Select(snapshot => snapshot.Session),
            ReferenceEqualityComparer.Instance);
        if (expectedRetiringClaims is not null &&
            expected.Count != expectedRetiringClaims.Count)
        {
            throw new InvalidOperationException(
                "A Surface claim transfer contains duplicate source sessions.");
        }

        if (!claimed.SetEquals(expected))
        {
            throw new InvalidOperationException(
                $"Surface '{definition.SurfaceId}' cannot take member claims " +
                "without the exact retiring sessions.");
        }

        if (claimed.Count > 0 && definition.GroupId is null)
        {
            throw new InvalidOperationException(
                "A standalone Surface cannot take another Surface's member claim.");
        }

        foreach (WidgetSurfaceClaimTransfer<THost> snapshot in
                 expectedRetiringClaims ?? [])
        {
            WidgetSurfaceSession<THost> source = snapshot.Session;
            if (!_sessions.TryGetValue(source.SurfaceId, out var current) ||
                !ReferenceEquals(current, source) ||
                !ReferenceEquals(source.Host, snapshot.Host) ||
                !string.Equals(
                    source.ActiveMemberId,
                    snapshot.ActiveMemberId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    source.GroupId,
                    snapshot.GroupId,
                    StringComparison.Ordinal) ||
                !source.MemberIds.SequenceEqual(
                    snapshot.MemberIds,
                    StringComparer.Ordinal) ||
                source.CandidateHost is not null ||
                source.MemberIds.Any(memberId =>
                    !definition.MemberIds.Contains(memberId, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Source Surface '{source.SurfaceId}' cannot be retired " +
                    "for this member-claim transfer.");
            }
        }

        return claimed.ToList();
    }

    private void RetireClaimedSurfaces(
        IReadOnlyList<WidgetSurfaceSession<THost>> retiringSessions)
    {
        foreach (WidgetSurfaceSession<THost> source in retiringSessions)
        {
            _sessions.Remove(source.SurfaceId);
            RemoveIndexedMembers(source.MemberIds, source.SurfaceId);
            source.Dispose();
        }
    }

    /// <summary>
    /// Captures the exact sessions a configured group may retire when its
    /// members move to one Surface. The later commit rechecks their identity
    /// under the same registry lock before changing any claim.
    /// </summary>
    public IReadOnlyList<WidgetSurfaceClaimTransfer<THost>> CaptureGroupClaimTransfers(
        WidgetSurfaceDefinition definition,
        string? sourceGroupId = null,
        string? sourceSurfaceId = null,
        THost? onlyRetireHost = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        if (definition.GroupId is null ||
            (sourceGroupId is null) != (sourceSurfaceId is null))
        {
            throw new ArgumentException(
                "A group claim transfer requires a group definition and a complete source identity.");
        }

        lock (_gate)
        {
            var retiring = new HashSet<WidgetSurfaceSession<THost>>(
                ReferenceEqualityComparer.Instance);
            foreach (string memberId in definition.MemberIds)
            {
                if (!_surfaceIdByMemberId.TryGetValue(memberId, out string? claimedId))
                {
                    continue;
                }

                if (!_sessions.TryGetValue(claimedId, out var claimed) ||
                    !claimed.MemberIds.Contains(memberId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Member '{memberId}' has an inconsistent Surface claim.");
                }

                if (string.Equals(claimedId, definition.SurfaceId, StringComparison.Ordinal))
                {
                    if (!string.Equals(
                            claimed.GroupId,
                            definition.GroupId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Surface '{definition.SurfaceId}' has a conflicting group identity.");
                    }
                    continue;
                }

                bool standaloneClaim = claimed.GroupId is null &&
                    string.Equals(claimed.SurfaceId, memberId, StringComparison.Ordinal) &&
                    claimed.MemberIds.Count == 1 &&
                    string.Equals(
                        claimed.ActiveMemberId,
                        memberId,
                        StringComparison.Ordinal);
                bool sourceGroupClaim = sourceGroupId is not null &&
                    string.Equals(claimed.GroupId, sourceGroupId, StringComparison.Ordinal) &&
                    string.Equals(
                        claimed.SurfaceId,
                        sourceSurfaceId,
                        StringComparison.Ordinal);
                if ((!standaloneClaim && !sourceGroupClaim) ||
                    (onlyRetireHost is not null &&
                     !ReferenceEquals(claimed.Host, onlyRetireHost)))
                {
                    throw new InvalidOperationException(
                        $"Member '{memberId}' belongs to another active Surface " +
                        $"'{claimed.SurfaceId}'.");
                }

                retiring.Add(claimed);
            }

            WidgetSurfaceClaimTransfer<THost>[] snapshots = retiring
                .Select(session => new WidgetSurfaceClaimTransfer<THost>(session))
                .ToArray();
            ValidateMemberClaimTransfer(definition, snapshots);
            return snapshots;
        }
    }

    private void RemoveIndexedMembers(
        IReadOnlyList<string> memberIds,
        string surfaceId)
    {
        foreach (string memberId in memberIds)
        {
            if (_surfaceIdByMemberId.TryGetValue(memberId, out string? indexedSurfaceId) &&
                string.Equals(indexedSurfaceId, surfaceId, StringComparison.Ordinal))
            {
                _surfaceIdByMemberId.Remove(memberId);
            }
        }
    }
}

internal sealed class WidgetSurfaceClaimTransfer<THost>
    where THost : class
{
    internal WidgetSurfaceClaimTransfer(WidgetSurfaceSession<THost> session)
    {
        Session = session;
        Host = session.Host;
        ActiveMemberId = session.ActiveMemberId;
        GroupId = session.GroupId;
        MemberIds = Array.AsReadOnly(session.MemberIds.ToArray());
    }

    public WidgetSurfaceSession<THost> Session { get; }

    public THost Host { get; }

    public string ActiveMemberId { get; }

    public string? GroupId { get; }

    public IReadOnlyList<string> MemberIds { get; }
}

internal sealed record WidgetSurfaceDefinition(
    string SurfaceId,
    string? GroupId,
    IReadOnlyList<string> MemberIds,
    string ActiveMemberId)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SurfaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ActiveMemberId);
        ArgumentNullException.ThrowIfNull(MemberIds);
        if (MemberIds.Count == 0 ||
            MemberIds.Any(string.IsNullOrWhiteSpace) ||
            MemberIds.Distinct(StringComparer.Ordinal).Count() != MemberIds.Count ||
            !MemberIds.Contains(ActiveMemberId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A surface definition requires unique members and a valid active member.");
        }
    }
}

internal sealed class WidgetSurfaceSession<THost> : IDisposable
    where THost : class
{
    private bool _isDisposed;

    internal WidgetSurfaceSession(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        SurfaceId = definition.SurfaceId;
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        Host = host;
        SwitchGate = new SemaphoreSlim(1, 1);
    }

    public string SurfaceId { get; }

    public string? GroupId { get; private set; }

    public IReadOnlyList<string> MemberIds { get; private set; }

    public string ActiveMemberId { get; private set; }

    public THost Host { get; private set; }

    public string? CandidateMemberId { get; private set; }

    public THost? CandidateHost { get; private set; }

    public SemaphoreSlim SwitchGate { get; }

    internal void UpdateDefinition(WidgetSurfaceDefinition definition)
    {
        ThrowIfDisposed();
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        if (CandidateMemberId is not null &&
            !MemberIds.Contains(CandidateMemberId, StringComparer.Ordinal))
        {
            CandidateMemberId = null;
            CandidateHost = null;
        }
    }

    internal bool StageCandidate(string targetMemberId, THost candidateHost)
    {
        ThrowIfDisposed();
        if (CandidateHost is not null)
        {
            return ReferenceEquals(CandidateHost, candidateHost) &&
                   string.Equals(
                       CandidateMemberId,
                       targetMemberId,
                       StringComparison.Ordinal);
        }

        if (ReferenceEquals(Host, candidateHost))
        {
            return false;
        }

        CandidateMemberId = targetMemberId;
        CandidateHost = candidateHost;
        return true;
    }

    internal bool CancelCandidate(THost candidateHost)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(CandidateHost, candidateHost))
        {
            return false;
        }

        CandidateMemberId = null;
        CandidateHost = null;
        return true;
    }

    internal void CommitActive(
        WidgetSurfaceDefinition definition,
        THost host)
    {
        ThrowIfDisposed();
        GroupId = definition.GroupId;
        MemberIds = definition.MemberIds.ToArray();
        ActiveMemberId = definition.ActiveMemberId;
        Host = host;
        CandidateMemberId = null;
        CandidateHost = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CandidateMemberId = null;
        CandidateHost = null;
        // A topology change can retire the registry entry after cancellation
        // but before an in-flight switch leaves its finally block. Keeping the
        // gate undisposed allows that holder to release it safely.
    }
}
