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
