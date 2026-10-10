namespace DeskBox.Services;

/// <summary>
/// Tracks the consecutive-observation state for the desktop-pinned bedding
/// watchdog. A repair may only run after the same leak pattern (identified by
/// the foreign window found below the group) persists across consecutive
/// observations, so transient activation states never trigger a re-bed.
/// </summary>
internal static class BeddingLeakConfirmationPolicy
{
    /// <summary>
    /// Persistent leak-tracking state: the foreign window that witnessed the
    /// previous observation, and how many consecutive observations it has
    /// been seen in.
    /// </summary>
    public readonly record struct LeakTracking(IntPtr LastWitness, int Confirmations)
    {
        public static LeakTracking Empty { get; } = new(IntPtr.Zero, 0);
    }

    /// <summary>
    /// Observes one leak witness. A different witness restarts the count at
    /// one; the same witness seen <paramref name="requiredConfirmations"/>
    /// times in a row confirms the leak.
    /// </summary>
    public static (LeakTracking Tracking, bool Confirmed) Observe(
        LeakTracking tracking,
        IntPtr witness,
        int requiredConfirmations)
    {
        if (requiredConfirmations <= 1)
        {
            return (new LeakTracking(witness, 1), true);
        }

        int confirmations = witness == tracking.LastWitness
            ? tracking.Confirmations + 1
            : 1;
        LeakTracking updated = new(witness, confirmations);
        return (updated, confirmations >= requiredConfirmations);
    }
}
