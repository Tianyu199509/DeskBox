using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupPersistedTopologyRecoveryTests
{
    [Fact]
    public async Task SuccessfulRollback_RestoresPreviousMemoryAndDiskState()
    {
        string memory = "committed";
        string disk = "committed";

        bool restored = await WidgetGroupPersistedTopologyRecovery.TryRestorePreviousAsync(
            restorePrevious: () => memory = "previous",
            savePreviousAsync: () =>
            {
                disk = memory;
                return Task.FromResult(true);
            },
            restoreCommitted: () => memory = "committed");

        Assert.True(restored);
        Assert.Equal("previous", memory);
        Assert.Equal("previous", disk);
    }

    [Fact]
    public async Task FailedRollbackWrite_RestoresLastDurableTopologyInMemory()
    {
        string memory = "committed";
        string disk = "committed";

        bool restored = await WidgetGroupPersistedTopologyRecovery.TryRestorePreviousAsync(
            restorePrevious: () => memory = "previous",
            savePreviousAsync: () => Task.FromResult(false),
            restoreCommitted: () => memory = "committed");

        Assert.False(restored);
        Assert.Equal(disk, memory);
    }

    [Fact]
    public async Task RollbackWriteException_RestoresLastDurableTopologyInMemory()
    {
        string memory = "committed";
        string disk = "committed";

        await Assert.ThrowsAsync<IOException>(() =>
            WidgetGroupPersistedTopologyRecovery.TryRestorePreviousAsync(
                restorePrevious: () => memory = "previous",
                savePreviousAsync: () =>
                    Task.FromException<bool>(new IOException("disk unavailable")),
                restoreCommitted: () => memory = "committed"));

        Assert.Equal(disk, memory);
    }

    [Fact]
    public async Task ReusedDetach_RollbackWriteFailureKeepsSavedSurfaceClaims()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var detachedHost = new object();
        var replacementHost = new object();
        registry.RegisterActive(
            new WidgetSurfaceDefinition("group", "group-id", ["a", "b"], "a"),
            detachedHost);
        registry.RemoveSurface("group");
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            detachedHost);
        registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            replacementHost);
        string memory = "split";
        string disk = "split";

        bool rollbackSaved = await WidgetGroupPersistedTopologyRecovery
            .TryRestorePreviousAsync(
                restorePrevious: () => memory = "group",
                savePreviousAsync: () => Task.FromResult(false),
                restoreCommitted: () => memory = "split");

        Assert.False(rollbackSaved);
        Assert.Equal(disk, memory);
        Assert.True(registry.TryGetByMember("a", out var detached));
        Assert.Same(detachedHost, detached!.Host);
        Assert.True(registry.TryGetByMember("b", out var remaining));
        Assert.Same(replacementHost, remaining!.Host);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task ReusedDetach_DurableRollbackRestoresOriginalHostClaim()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var detachedHost = new object();
        var replacementHost = new object();
        var original = new WidgetSurfaceDefinition(
            "group", "group-id", ["a", "b"], "a");
        registry.RegisterActive(original, detachedHost);
        registry.RemoveSurface("group");
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            detachedHost);
        registry.RegisterActive(
            new WidgetSurfaceDefinition("b", null, ["b"], "b"),
            replacementHost);
        string memory = "split";
        string disk = "split";

        bool rollbackSaved = await WidgetGroupPersistedTopologyRecovery
            .TryRestorePreviousAsync(
                restorePrevious: () => memory = "group",
                savePreviousAsync: () =>
                {
                    disk = memory;
                    return Task.FromResult(true);
                },
                restoreCommitted: () => memory = "split");
        if (rollbackSaved)
        {
            registry.UnregisterHost(replacementHost);
            registry.UnregisterHost(detachedHost);
            registry.CommitActive(original, detachedHost);
        }

        Assert.True(rollbackSaved);
        Assert.Equal("group", memory);
        Assert.Equal(memory, disk);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var fromA));
        Assert.True(registry.TryGetByMember("b", out var fromB));
        Assert.Same(fromA, fromB);
        Assert.Same(detachedHost, fromA!.Host);
        Assert.Equal(0, registry.UnregisterHost(replacementHost));
    }

    [Fact]
    public async Task Merge_DoubleFailureAdoptsSurvivingTargetAndTransfersEveryClaim()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var targetHost = new object();
        var sourceHost = new object();
        var candidate = new object();
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"), targetHost);
        registry.RegisterActive(
            new WidgetSurfaceDefinition("source", "source-id", ["b", "c"], "b"),
            sourceHost);
        var committed = new WidgetSurfaceDefinition(
            "merged", "merged-id", ["a", "b", "c"], "a");
        string memory = "merged";
        string disk = "merged";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () => Task.FromResult(candidate),
                presentCandidateAsync: _ => Task.CompletedTask,
                commitAndRetireLegacyAsync: _ =>
                    Task.FromException(new InvalidOperationException("post-save host failure")),
                rollbackCandidate: host => registry.UnregisterHost(host)));
        bool rollbackSaved = await WidgetGroupPersistedTopologyRecovery
            .TryRestorePreviousAsync(
                restorePrevious: () => memory = "previous",
                savePreviousAsync: () => Task.FromResult(false),
                restoreCommitted: () => memory = "merged");
        Assert.False(rollbackSaved);
        Assert.Equal(disk, memory);

        WidgetSurfaceSession<object> recovered =
            WidgetGroupPersistedTopologyRecovery.ReconcileCommittedMergeClaims(
                registry, committed, targetHost, "source-id", "source");

        Assert.Equal(1, registry.Count);
        Assert.Same(targetHost, recovered.Host);
        foreach (string memberId in committed.MemberIds)
        {
            Assert.True(registry.TryGetByMember(memberId, out var claim));
            Assert.Same(recovered, claim);
        }
        Assert.Equal(0, registry.UnregisterHost(sourceHost));
        Assert.True(registry.UpdateDefinition(committed));
    }

    [Fact]
    public void MergeRecovery_RejectsUnexpectedClaimWithoutChangingEitherOwner()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var targetHost = new object();
        var unrelatedHost = new object();
        WidgetSurfaceSession<object> target = registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"), targetHost);
        WidgetSurfaceSession<object> unrelated = registry.RegisterActive(
            new WidgetSurfaceDefinition("other", "other-id", ["b", "c"], "b"),
            unrelatedHost);
        var committed = new WidgetSurfaceDefinition(
            "merged", "merged-id", ["a", "b", "c"], "a");

        Assert.Throws<InvalidOperationException>(() =>
            WidgetGroupPersistedTopologyRecovery.ReconcileCommittedMergeClaims(
                registry, committed, targetHost, "source-id", "source"));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetByMember("a", out var targetClaim));
        Assert.Same(target, targetClaim);
        Assert.True(registry.TryGetByMember("b", out var unrelatedClaim));
        Assert.Same(unrelated, unrelatedClaim);
    }
}
