namespace DeskBox.Services;

/// <summary>
/// Coalesces repeated whole-group raises triggered by widget title
/// activations. Every title press used to run a full raise (and every release
/// a full restore) even when the previous raise was still in effect, so rapid
/// title interaction replayed group Z-order transactions in a tight loop
/// (#375).
/// </summary>
internal static class TitleActivationRaisePolicy
{
    /// <summary>
    /// A repeat activation of the same window within this window reuses the
    /// raise that is already live instead of re-issuing it.
    /// </summary>
    public static readonly TimeSpan RepeatRaiseSuppressWindow =
        TimeSpan.FromSeconds(2);

    public static bool ShouldSkipRepeatRaise(
        bool previousRaiseStillTracked,
        bool sameActiveWindow,
        TimeSpan elapsedSincePreviousRaise)
    {
        return previousRaiseStillTracked &&
            sameActiveWindow &&
            elapsedSincePreviousRaise >= TimeSpan.Zero &&
            elapsedSincePreviousRaise < RepeatRaiseSuppressWindow;
    }
}
