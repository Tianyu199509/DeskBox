namespace DeskBox.Services;

/// <summary>
/// Keeps the in-memory topology aligned with the last durable settings file
/// when a replacement window fails after the new topology was saved.
/// </summary>
internal static class WidgetGroupPersistedTopologyRecovery
{
    internal static WidgetSurfaceSession<THost> ReconcileCommittedMergeClaims<THost>(
        WidgetSurfaceRegistry<THost> registry,
        WidgetSurfaceDefinition committedDefinition,
        THost survivingTargetHost,
        string? sourceGroupId,
        string? sourceSurfaceId)
        where THost : class
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(committedDefinition);
        ArgumentNullException.ThrowIfNull(survivingTargetHost);

        IReadOnlyList<WidgetSurfaceClaimTransfer<THost>> retiring =
            registry.CaptureGroupClaimTransfers(
                committedDefinition,
                sourceGroupId: sourceGroupId,
                sourceSurfaceId: sourceSurfaceId);
        return registry.SynchronizeActive(
            committedDefinition,
            survivingTargetHost,
            expectedRetiringClaims: retiring);
    }

    /// <summary>
    /// Quarantines the stale declarations that block a saved detach split
    /// after the primary reconciliation failed. The registry must end with
    /// no conflicting claims so later group notifications cannot throw; the
    /// standalone declaration for the detached host is retried once, and a
    /// false result is the observable failure marker for the saved split.
    /// </summary>
    internal static bool QuarantineCommittedDetachClaims<THost>(
        WidgetSurfaceRegistry<THost> registry,
        string originalSurfaceId,
        string removedMemberId,
        THost detachedHost,
        WidgetSurfaceDefinition detachedDefinition,
        Action<string> log)
        where THost : class
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(detachedHost);
        ArgumentNullException.ThrowIfNull(detachedDefinition);
        ArgumentNullException.ThrowIfNull(log);

        try { registry.UnregisterHost(detachedHost); }
        catch (Exception ex)
        {
            log(
                "[WidgetGroup] Detach quarantine host unregister failed " +
                $"member={removedMemberId}: {ex}");
        }

        try
        {
            if (registry.TryGetByMember(removedMemberId, out var stale) &&
                stale is not null &&
                !ReferenceEquals(stale.Host, detachedHost))
            {
                registry.UnregisterHost(stale.Host);
            }
        }
        catch (Exception ex)
        {
            log(
                "[WidgetGroup] Detach quarantine stale claim unregister " +
                $"failed member={removedMemberId}: {ex}");
        }

        try { registry.RemoveSurface(originalSurfaceId); }
        catch (Exception ex)
        {
            log(
                "[WidgetGroup] Detach quarantine surface removal failed " +
                $"surface={originalSurfaceId}: {ex}");
        }

        try
        {
            registry.RegisterActive(detachedDefinition, detachedHost);
            return true;
        }
        catch (Exception ex)
        {
            log(
                "[WidgetGroup] Detach reconciliation quarantined without a " +
                $"standalone declaration for member '{removedMemberId}': {ex}");
            return false;
        }
    }

    internal static async Task<bool> TryRestorePreviousAsync(
        Action restorePrevious,
        Func<Task<bool>> savePreviousAsync,
        Action restoreCommitted)
    {
        ArgumentNullException.ThrowIfNull(restorePrevious);
        ArgumentNullException.ThrowIfNull(savePreviousAsync);
        ArgumentNullException.ThrowIfNull(restoreCommitted);

        restorePrevious();
        bool saved;
        try
        {
            saved = await savePreviousAsync();
        }
        catch
        {
            restoreCommitted();
            throw;
        }

        if (!saved)
        {
            restoreCommitted();
        }

        return saved;
    }
}
