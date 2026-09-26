namespace DeskBox.Contracts;

/// <summary>
/// Resource lease contract for a feature that owns long-lived runtime
/// resources (watchers, COM subscriptions, timers, caches). Enable acquires
/// the lease through <see cref="StartAsync"/>; disable releases it through
/// <see cref="IAsyncDisposable.DisposeAsync"/> and every held resource
/// returns to zero. This formalizes the repository's manual "disabling a
/// feature releases its complete runtime" contract; it is deliberately not a
/// PowerToys-style module lifecycle (no Initialize/Activate/Suspend/Shutdown
/// stages) — a runtime that holds no long-lived resources does not implement
/// this interface at all.
/// </summary>
/// <remarks>
/// The state machine below is part of the contract, not just the signatures:
/// <list type="bullet">
/// <item><b>Idempotency.</b> Repeated <see cref="StartAsync"/> calls are
/// no-ops that never build a second lease; repeated
/// <see cref="IAsyncDisposable.DisposeAsync"/> calls are no-ops, not double
/// releases.</item>
/// <item><b>Cancellable start.</b> <see cref="StartAsync"/> honors its
/// cancellation token, and a partial start must release the resources it
/// already acquired in reverse acquisition order before throwing — no half
/// lease survives a failed start.</item>
/// <item><b>Serialized disposal.</b> A
/// <see cref="IAsyncDisposable.DisposeAsync"/> that races an in-progress
/// <see cref="StartAsync"/> is resolved through the state machine: either the
/// start completes and the disposal follows it, or the start is cancelled and
/// the disposal follows that. Concurrent interleaving of the two transitions
/// is not permitted.</item>
/// <item><b>Bounded disposal.</b> A runtime must not be able to block host
/// shutdown indefinitely: the host bounds the wait, records diagnostics, and
/// puts the unreleased runtime on the leak isolation list rather than waiting
/// forever.</item>
/// </list>
/// Runtime instances are owned by the feature registry at the composition
/// root; callers borrow capabilities through the registry instead of caching
/// runtime instances.
/// </remarks>
public interface IFeatureRuntime : IAsyncDisposable
{
    /// <summary>
    /// Acquires the feature's long-lived resources (the "enable"
    /// transition). Idempotent: while the lease is held this is a no-op. A
    /// partial acquisition that fails must be rolled back in reverse
    /// acquisition order before the exception propagates.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cooperatively cancels the acquisition. A token that is already
    /// cancelled must fail the call before any resource is acquired.
    /// </param>
    Task StartAsync(CancellationToken cancellationToken = default);

    // DisposeAsync is inherited from IAsyncDisposable: the "disable"/shutdown
    // transition that releases every held resource back to zero. Idempotent,
    // cancellable-draining, and serialized against StartAsync per the state
    // machine in the remarks above.
}
