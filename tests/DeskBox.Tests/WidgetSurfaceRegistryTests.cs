using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSurfaceRegistryTests
{
    [Fact]
    public void CommitActive_KeepsSurfaceIdentityAndPromotesPreparedHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);

        Assert.True(registry.StageCandidate("surface", "b", targetHost));
        WidgetSurfaceSession<object> committed = registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            targetHost);

        Assert.Equal("surface", committed.SurfaceId);
        Assert.Equal("b", committed.ActiveMemberId);
        Assert.Same(targetHost, committed.Host);
        Assert.Null(committed.CandidateHost);
        Assert.True(registry.TryGetByMember("a", out var fromPreviousAlias));
        Assert.Same(committed, fromPreviousAlias);
        Assert.True(registry.TryGetByMember("b", out var fromActiveAlias));
        Assert.Same(committed, fromActiveAlias);
    }

    [Fact]
    public void CommitActive_AllowsPersistentHostWithoutCandidateReplacement()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var host = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            host);

        WidgetSurfaceSession<object> committed = registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            host);

        Assert.Same(host, committed.Host);
        Assert.Equal("b", committed.ActiveMemberId);
    }

    [Fact]
    public void CancelCandidate_PreservesActiveHostAndMember()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);
        registry.StageCandidate("surface", "b", targetHost);

        Assert.True(registry.CancelCandidate("surface", targetHost));

        Assert.Same(previousHost, session.Host);
        Assert.Equal("a", session.ActiveMemberId);
        Assert.Null(session.CandidateHost);
    }

    [Fact]
    public void StageCandidate_RejectsOverlappingHostAndAllowsRetryAfterCancellation()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var activeHost = new object();
        var firstCandidate = new object();
        var secondCandidate = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            activeHost);

        Assert.False(registry.StageCandidate("surface", "b", activeHost));
        Assert.True(registry.StageCandidate("surface", "b", firstCandidate));
        Assert.True(registry.StageCandidate("surface", "b", firstCandidate));
        Assert.False(registry.StageCandidate("surface", "b", secondCandidate));
        Assert.False(registry.StageCandidate("surface", "a", firstCandidate));
        Assert.False(registry.CancelCandidate("surface", secondCandidate));
        Assert.Same(activeHost, session.Host);
        Assert.Same(firstCandidate, session.CandidateHost);
        Assert.Equal("b", session.CandidateMemberId);

        Assert.True(registry.CancelCandidate("surface", firstCandidate));
        Assert.True(registry.StageCandidate("surface", "b", secondCandidate));
        registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            secondCandidate);
        Assert.Same(secondCandidate, session.Host);
        Assert.Null(session.CandidateHost);
    }

    [Fact]
    public async Task FailedPromotion_LeavesExistingSurfaceOnPreviousHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var activeHost = new object();
        var candidate = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            activeHost);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () =>
                {
                    Assert.True(registry.StageCandidate("surface", "b", candidate));
                    return Task.FromResult(candidate);
                },
                presentCandidateAsync: _ =>
                    Task.FromException(new InvalidOperationException("frame failed")),
                commitAndRetireLegacyAsync: host =>
                {
                    registry.CommitActive(
                        CreateDefinition("surface", "b", "a", "b"),
                        host);
                    return Task.CompletedTask;
                },
                rollbackCandidate: host => registry.UnregisterHost(host)));

        Assert.Same(activeHost, session.Host);
        Assert.Equal("a", session.ActiveMemberId);
        Assert.Null(session.CandidateHost);
        Assert.True(registry.TryGetByMember("b", out var memberSession));
        Assert.Same(session, memberSession);
    }

    [Fact]
    public async Task NewGroupPromotion_TransfersClaimsOnlyAtCommit()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var legacyHost = new object();
        var candidate = new object();
        var standalone = new WidgetSurfaceDefinition("a", null, ["a"], "a");
        WidgetSurfaceSession<object> oldSession =
            registry.RegisterActive(standalone, legacyHost);

        // A new group has no Surface declaration while its candidate is
        // prepared. A failed presentation therefore leaves the old claim.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () => Task.FromResult(candidate),
                presentCandidateAsync: _ =>
                    Task.FromException(new InvalidOperationException("frame failed")),
                commitAndRetireLegacyAsync: host =>
                {
                    registry.CommitActive(
                        CreateDefinition("surface", "a", "a", "b"),
                        host);
                    return Task.CompletedTask;
                },
                rollbackCandidate: host => registry.UnregisterHost(host)));

        Assert.False(registry.TryGet("surface", out _));
        Assert.True(registry.TryGetByMember("a", out var beforeCommit));
        Assert.Same(oldSession, beforeCommit);

        WidgetSurfaceSession<object> promoted = registry.CommitActive(
            CreateDefinition("surface", "a", "a", "b"),
            candidate,
            expectedRetiringClaims: [new(oldSession)]);

        Assert.Same(candidate, promoted.Host);
        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet("a", out _));
        Assert.True(registry.TryGetByMember("a", out var fromOldAlias));
        Assert.Same(promoted, fromOldAlias);
        Assert.Equal(0, registry.UnregisterHost(legacyHost));
        Assert.Same(candidate, promoted.Host);
    }

    [Fact]
    public void Clear_RemovesActiveAndPendingCandidateClaims()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var activeHost = new object();
        var candidate = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            activeHost);
        Assert.True(registry.StageCandidate("surface", "b", candidate));

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGetByMember("a", out _));
        Assert.False(registry.TryGetByMember("b", out _));
        Assert.Null(session.CandidateHost);
        Assert.Equal(0, registry.UnregisterHost(candidate));
    }

    [Fact]
    public void DetachRollback_RestoresOriginalSurfaceAfterReplacementCloses()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var detachedHost = new object();
        var replacementHost = new object();
        WidgetSurfaceDefinition original =
            CreateDefinition("surface", "a", "a", "b");
        registry.RegisterActive(original, detachedHost);

        Assert.True(registry.RemoveSurface("surface"));
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            detachedHost);
        registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            replacementHost);

        Assert.Equal(1, registry.UnregisterHost(replacementHost));
        Assert.Equal(1, registry.UnregisterHost(detachedHost));
        WidgetSurfaceSession<object> restored =
            registry.CommitActive(original, detachedHost);

        Assert.Equal(0, registry.UnregisterHost(replacementHost));
        Assert.Equal(1, registry.Count);
        Assert.Same(detachedHost, restored.Host);
        Assert.True(registry.TryGetByMember("a", out var firstMember));
        Assert.Same(restored, firstMember);
        Assert.True(registry.TryGetByMember("b", out var secondMember));
        Assert.Same(restored, secondMember);
    }

    [Fact]
    public void UpdateDefinition_ReindexesMembersWithoutChangingHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var host = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            host);

        Assert.True(registry.UpdateDefinition(
            CreateDefinition("surface", "a", "a", "c")));

        Assert.False(registry.TryGetByMember("b", out _));
        Assert.True(registry.TryGetByMember("c", out var fromNewMember));
        Assert.Same(session, fromNewMember);
        Assert.Same(host, session.Host);
    }

    [Fact]
    public void RegisterActive_DuplicateMemberLeavesBothSurfacesIntact()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var firstHost = new object();
        var secondHost = new object();
        WidgetSurfaceSession<object> first = registry.RegisterActive(
            CreateDefinition("first", "a", "a", "b"),
            firstHost);
        WidgetSurfaceSession<object> second = registry.RegisterActive(
            CreateDefinition("second", "c", "c"),
            secondHost);

        Assert.Throws<InvalidOperationException>(() => registry.RegisterActive(
            CreateDefinition("second", "c", "b", "c"),
            secondHost));

        Assert.Equal(2, registry.Count);
        Assert.Equal(new[] { "a", "b" }, first.MemberIds);
        Assert.Equal(new[] { "c" }, second.MemberIds);
        Assert.True(registry.TryGetByMember("b", out var b));
        Assert.Same(first, b);
        Assert.True(registry.TryGetByMember("c", out var c));
        Assert.Same(second, c);
    }

    [Fact]
    public void UpdateAndSynchronizeConflict_DoNotChangeAliasesOrHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var firstHost = new object();
        var secondHost = new object();
        WidgetSurfaceSession<object> first = registry.RegisterActive(
            CreateDefinition("first", "a", "a"),
            firstHost);
        WidgetSurfaceSession<object> second = registry.RegisterActive(
            CreateDefinition("second", "b", "b"),
            secondHost);
        WidgetSurfaceDefinition overlap =
            CreateDefinition("first", "a", "a", "b");

        Assert.Throws<InvalidOperationException>(() =>
            registry.UpdateDefinition(overlap));
        Assert.Throws<InvalidOperationException>(() =>
            registry.SynchronizeActive(overlap, new object()));

        Assert.Equal(new[] { "a" }, first.MemberIds);
        Assert.Same(firstHost, first.Host);
        Assert.True(registry.TryGetByMember("a", out var a));
        Assert.Same(first, a);
        Assert.True(registry.TryGetByMember("b", out var b));
        Assert.Same(second, b);
        Assert.Same(secondHost, second.Host);
    }

    [Fact]
    public void CommitActive_TransfersOnlyTheExactCompleteSourceSessions()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var aHost = new object();
        var bHost = new object();
        var sourceGroupHost = new object();
        var targetHost = new object();
        WidgetSurfaceSession<object> a = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            aHost);
        WidgetSurfaceSession<object> b = registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            bHost);
        WidgetSurfaceSession<object> sourceGroup = registry.RegisterActive(
            CreateDefinition("source", "c", "c", "d"),
            sourceGroupHost);
        WidgetSurfaceDefinition merged =
            CreateDefinition("merged", "a", "a", "b", "c", "d");
        WidgetSurfaceClaimTransfer<object>[] claims = registry
            .CaptureGroupClaimTransfers(
                merged,
                sourceGroupId: sourceGroup.GroupId,
                sourceSurfaceId: sourceGroup.SurfaceId)
            .ToArray();
        Assert.Contains(claims, claim => ReferenceEquals(claim.Session, a));
        Assert.Contains(claims, claim => ReferenceEquals(claim.Session, b));

        Assert.Throws<InvalidOperationException>(() =>
            registry.CommitActive(
                merged,
                targetHost,
                claims.Where(claim => !ReferenceEquals(claim.Session, sourceGroup))
                    .ToArray()));
        Assert.Equal(3, registry.Count);
        Assert.True(registry.TryGetByMember("d", out var beforeCommit));
        Assert.Same(sourceGroup, beforeCommit);

        WidgetSurfaceSession<object> committed = registry.CommitActive(
            merged,
            targetHost,
            expectedRetiringClaims: claims);

        Assert.Equal(1, registry.Count);
        foreach (string memberId in merged.MemberIds)
        {
            Assert.True(registry.TryGetByMember(memberId, out var alias));
            Assert.Same(committed, alias);
        }
        Assert.Equal(0, registry.UnregisterHost(aHost));
        Assert.Equal(0, registry.UnregisterHost(bHost));
        Assert.Equal(0, registry.UnregisterHost(sourceGroupHost));
        Assert.Same(targetHost, committed.Host);
    }

    [Fact]
    public void ExistingGroupMerge_KeepsTargetHostAndRetiresSourceClaim()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var targetHost = new object();
        var sourceHost = new object();
        WidgetSurfaceSession<object> target = registry.RegisterActive(
            CreateDefinition("target", "a", "a", "b"),
            targetHost);
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("source", "c", "c", "d"),
            sourceHost);

        WidgetSurfaceDefinition mergedDefinition =
            CreateDefinition("target", "a", "a", "b", "c", "d");
        IReadOnlyList<WidgetSurfaceClaimTransfer<object>> claims =
            registry.CaptureGroupClaimTransfers(
                mergedDefinition,
                sourceGroupId: source.GroupId,
                sourceSurfaceId: source.SurfaceId);
        WidgetSurfaceSession<object> merged = registry.CommitActive(
            mergedDefinition,
            targetHost,
            expectedRetiringClaims: claims);

        Assert.Same(target, merged);
        Assert.Same(targetHost, merged.Host);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByMember("d", out var alias));
        Assert.Same(target, alias);
        Assert.Equal(0, registry.UnregisterHost(sourceHost));
        Assert.Same(target, merged);
    }

    [Fact]
    public void CommitActive_RejectsStaleAndPartialSourceSessions()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var oldHost = new object();
        WidgetSurfaceSession<object> old = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            oldHost);
        var oldClaim = new WidgetSurfaceClaimTransfer<object>(old);
        registry.RemoveSurface("a");
        var currentHost = new object();
        WidgetSurfaceSession<object> current = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            currentHost);

        Assert.Throws<InvalidOperationException>(() =>
            registry.CommitActive(
                CreateDefinition("merged", "a", "a", "b"),
                new object(),
                expectedRetiringClaims: [oldClaim]));
        Assert.True(registry.TryGetByMember("a", out var alias));
        Assert.Same(current, alias);
        Assert.Equal(1, registry.Count);

        var grouped = new WidgetSurfaceRegistry<object>();
        var groupHost = new object();
        WidgetSurfaceSession<object> source = grouped.RegisterActive(
            CreateDefinition("source", "a", "a", "b"),
            groupHost);
        var partialClaim = new WidgetSurfaceClaimTransfer<object>(source);
        Assert.Throws<InvalidOperationException>(() =>
            grouped.CommitActive(
                CreateDefinition("merged", "a", "a", "c"),
                new object(),
                expectedRetiringClaims: [partialClaim]));
        Assert.True(grouped.TryGetByMember("b", out var oldGroupAlias));
        Assert.Same(source, oldGroupAlias);
        Assert.Same(groupHost, source.Host);
    }

    [Fact]
    public void CommitActive_RejectsSourceChangedAfterClaimCapture()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var initialHost = new object();
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("source", "a", "a", "b"),
            initialHost);
        WidgetSurfaceDefinition merged =
            CreateDefinition("merged", "a", "a", "b");
        IReadOnlyList<WidgetSurfaceClaimTransfer<object>> captured =
            registry.CaptureGroupClaimTransfers(
                merged,
                sourceGroupId: source.GroupId,
                sourceSurfaceId: source.SurfaceId);
        var replacementHost = new object();
        registry.SynchronizeActive(
            CreateDefinition("source", "b", "a", "b"),
            replacementHost);

        Assert.Throws<InvalidOperationException>(() =>
            registry.CommitActive(
                merged,
                new object(),
                expectedRetiringClaims: captured));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var alias));
        Assert.Same(source, alias);
        Assert.Same(replacementHost, source.Host);
        Assert.Equal("b", source.ActiveMemberId);
    }

    [Fact]
    public void CommitActive_DoesNotRetireSourceWithPendingCandidate()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var sourceHost = new object();
        var pendingHost = new object();
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("source", "a", "a", "b"),
            sourceHost);
        var pendingClaim = new WidgetSurfaceClaimTransfer<object>(source);
        Assert.True(registry.StageCandidate("source", "b", pendingHost));

        Assert.Throws<InvalidOperationException>(() =>
            registry.CommitActive(
                CreateDefinition("merged", "a", "a", "b"),
                new object(),
                expectedRetiringClaims: [pendingClaim]));

        Assert.True(registry.TryGet("source", out var preserved));
        Assert.Same(source, preserved);
        Assert.Same(pendingHost, source.CandidateHost);
        Assert.True(registry.CancelCandidate("source", pendingHost));
        Assert.Same(sourceHost, source.Host);
    }

    [Fact]
    public void CaptureGroupClaimTransfers_AcceptsOnlyNamedSourceGroupAndStandaloneMembers()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        WidgetSurfaceSession<object> target = registry.RegisterActive(
            CreateDefinition("target", "a", "a"),
            new object());
        WidgetSurfaceSession<object> standalone = registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            new object());
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("source", "c", "c", "d"),
            new object());
        WidgetSurfaceDefinition merged =
            CreateDefinition("target", "a", "a", "b", "c", "d");

        Assert.Throws<InvalidOperationException>(() =>
            registry.CaptureGroupClaimTransfers(merged));
        IReadOnlyList<WidgetSurfaceClaimTransfer<object>> retiring =
            registry.CaptureGroupClaimTransfers(
                merged,
                sourceGroupId: source.GroupId,
                sourceSurfaceId: source.SurfaceId);

        Assert.Equal(2, retiring.Count);
        Assert.Contains(retiring, claim => ReferenceEquals(claim.Session, standalone));
        Assert.Contains(retiring, claim => ReferenceEquals(claim.Session, source));
        Assert.DoesNotContain(retiring, claim => ReferenceEquals(claim.Session, target));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void CaptureGroupClaimTransfers_RestrictedHostRejectsOtherLiveStandalone()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var activeHost = new object();
        var otherHost = new object();
        WidgetSurfaceSession<object> active = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            activeHost);
        WidgetSurfaceSession<object> other = registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            otherHost);

        Assert.Throws<InvalidOperationException>(() =>
            registry.CaptureGroupClaimTransfers(
                CreateDefinition("group", "a", "a", "b"),
                onlyRetireHost: activeHost));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var first));
        Assert.Same(active, first);
        Assert.True(registry.TryGetByMember("b", out var second));
        Assert.Same(other, second);
    }

    [Fact]
    public void SynchronizeActive_TransfersMatchingStandaloneHostAtomically()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var host = new object();
        WidgetSurfaceSession<object> standalone = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            host);

        WidgetSurfaceSession<object> group = registry.SynchronizeActive(
            CreateDefinition("group", "a", "a", "b"),
            host,
            expectedRetiringClaims: [new(standalone)]);

        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet("a", out _));
        Assert.True(registry.TryGetByMember("a", out var member));
        Assert.Same(group, member);
        Assert.Same(host, group.Host);
    }

    [Fact]
    public async Task MergeSaveFailure_PreservesEverySourceClaim()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var targetHost = new object();
        var sourceHost = new object();
        var candidate = new object();
        WidgetSurfaceSession<object> target = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            targetHost);
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("source", "b", "b", "c"),
            sourceHost);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () => Task.FromResult(candidate),
                presentCandidateAsync: _ => Task.CompletedTask,
                commitAndRetireLegacyAsync: _ =>
                    Task.FromException(new InvalidOperationException("save failed")),
                rollbackCandidate: host => registry.UnregisterHost(host)));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var targetAlias));
        Assert.Same(target, targetAlias);
        Assert.True(registry.TryGetByMember("b", out var sourceAlias));
        Assert.Same(source, sourceAlias);
        Assert.True(registry.TryGetByMember("c", out sourceAlias));
        Assert.Same(source, sourceAlias);
    }

    [Fact]
    public void StandaloneSurface_CannotStealAnotherSurfaceClaim()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var sourceHost = new object();
        WidgetSurfaceSession<object> source = registry.RegisterActive(
            CreateDefinition("group", "a", "a", "b"),
            sourceHost);

        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterActive(
                new WidgetSurfaceDefinition("a", null, ["a"], "a"),
                new object(),
                expectedRetiringClaims: [new(source)]));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var alias));
        Assert.Same(source, alias);
        Assert.Same(sourceHost, source.Host);
    }

    [Fact]
    public void DelayedGroupClose_CannotRemoveNewStandaloneMembers()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var oldGroupHost = new object();
        var aHost = new object();
        var bHost = new object();
        registry.RegisterActive(
            CreateDefinition("group", "a", "a", "b"),
            oldGroupHost);

        Assert.True(registry.RemoveSurface("group"));
        WidgetSurfaceSession<object> a = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            aHost);
        WidgetSurfaceSession<object> b = registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            bHost);

        Assert.Equal(0, registry.UnregisterHost(oldGroupHost));
        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var aAlias));
        Assert.Same(a, aAlias);
        Assert.True(registry.TryGetByMember("b", out var bAlias));
        Assert.Same(b, bAlias);
    }

    [Fact]
    public void SynchronizeActive_ReconcilesStableHostAfterTopologyChange()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var standaloneHost = new object();
        var groupHost = new object();
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            standaloneHost);

        registry.RemoveSurface("a");
        WidgetSurfaceSession<object> session = registry.SynchronizeActive(
            CreateDefinition("surface", "a", "a", "b"),
            groupHost);

        Assert.Equal("surface", session.SurfaceId);
        Assert.Same(groupHost, session.Host);
        Assert.True(registry.TryGetByMember("b", out var memberSession));
        Assert.Same(session, memberSession);
    }

    [Fact]
    public void DifferentSurfacesOwnDifferentSwitchGates()
    {
        var gates = new WidgetSurfaceSwitchGatePool();

        Assert.NotSame(gates.Get("surface-1"), gates.Get("surface-2"));
        Assert.Same(gates.Get("surface-1"), gates.Get("surface-1"));
    }

    [Fact]
    public void UnregisteringRetiredHostDoesNotRemovePromotedSurface()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);
        registry.StageCandidate("surface", "b", targetHost);
        registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            targetHost);

        Assert.Equal(0, registry.UnregisterHost(previousHost));
        Assert.True(registry.TryGet("surface", out var session));
        Assert.Same(targetHost, session!.Host);
    }

    [Fact]
    public async Task RemovingSurfaceWhileGateIsHeld_AllowsInFlightRelease()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            new object());
        var gates = new WidgetSurfaceSwitchGatePool();
        IDisposable lease = await gates.AcquireManyAsync(["surface"]);

        Assert.True(registry.RemoveSurface("surface"));
        Exception? releaseFailure = Record.Exception(
            () => { lease.Dispose(); });

        Assert.Null(releaseFailure);
        Assert.False(registry.TryGet("surface", out _));
    }

    private static WidgetSurfaceDefinition CreateDefinition(
        string surfaceId,
        string activeMemberId,
        params string[] memberIds)
    {
        return new WidgetSurfaceDefinition(
            surfaceId,
            $"group-{surfaceId}",
            memberIds,
            activeMemberId);
    }
}
