namespace DeskBox.Services;

/// <summary>
/// Preparation runs before animation callbacks exist. A native move failure
/// must therefore finalize the host's requested visibility immediately.
/// </summary>
internal static class WidgetTrayAnimationPreparation
{
    internal const int DefaultVisualCommitFallbackTimeoutMs = 160;

    public static bool ShouldDeferWindowReveal(
        bool deferRevealUntilAnimationReady,
        bool isTrayShowAnimationInFlight)
    {
        return deferRevealUntilAnimationReady && isTrayShowAnimationInFlight;
    }

    public static void RevealAllSafely<TWindow>(
        IEnumerable<TWindow> windows,
        Action<TWindow> revealWindow,
        Action<TWindow, Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(revealWindow);
        ArgumentNullException.ThrowIfNull(reportFailure);

        foreach (TWindow window in windows)
        {
            try
            {
                revealWindow(window);
            }
            catch (Exception ex)
            {
                reportFailure(window, ex);
            }
        }
    }

    /// <summary>
    /// Yields at low dispatcher priority so newly shown XAML surfaces finish
    /// layout and backdrop target attachment, then awaits the thread compositor
    /// commit before removing DWM cloak. Bounded by a short timer fallback so a
    /// hung/lost compositor or shutting-down dispatcher never strands cloaked HWNDs.
    /// </summary>
    public static async Task AwaitVisualCommitWithFallbackAsync(
        Func<Action, bool> tryEnqueueLowPriority,
        Func<Task?> requestCompositorCommitAsync,
        Func<TimeSpan, Action, IDisposable> scheduleFallbackTimer,
        int fallbackTimeoutMs = DefaultVisualCommitFallbackTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(tryEnqueueLowPriority);
        ArgumentNullException.ThrowIfNull(requestCompositorCommitAsync);
        ArgumentNullException.ThrowIfNull(scheduleFallbackTimer);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int settled = 0;

        void CompleteOnce()
        {
            if (Interlocked.Exchange(ref settled, 1) == 0)
            {
                completion.TrySetResult();
            }
        }

        TimeSpan timeout = TimeSpan.FromMilliseconds(Math.Max(1, fallbackTimeoutMs));
        IDisposable timerRegistration = scheduleFallbackTimer(timeout, CompleteOnce);
        try
        {
            bool enqueued = false;
            try
            {
                enqueued = tryEnqueueLowPriority(() =>
                {
                    if (Volatile.Read(ref settled) != 0)
                    {
                        return;
                    }

                    Task? commitTask;
                    try
                    {
                        commitTask = requestCompositorCommitAsync();
                    }
                    catch
                    {
                        CompleteOnce();
                        return;
                    }

                    if (commitTask is null || commitTask.IsCompleted)
                    {
                        CompleteOnce();
                        return;
                    }

                    _ = ObserveCommitAsync(commitTask, CompleteOnce);
                });
            }
            catch
            {
                CompleteOnce();
            }

            if (!enqueued)
            {
                CompleteOnce();
            }

            await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            timerRegistration.Dispose();
        }
    }

    private static async Task ObserveCommitAsync(Task commitTask, Action onSettled)
    {
        try
        {
            await commitTask.ConfigureAwait(false);
        }
        catch
        {
            // Device-loss or shutdown races on RequestCommitAsync are non-fatal;
            // completing the barrier lets the caller uncloak immediately.
        }
        finally
        {
            onSettled();
        }
    }

    public static bool TryPrepare(Action prepare, Action completeWithoutAnimation, Action<Exception> reportFailure)
    {
        try
        {
            prepare();
            return true;
        }
        catch (Exception ex)
        {
            reportFailure(ex);
            completeWithoutAnimation();
            return false;
        }
    }

    public static void CompleteShow(
        Action restorePosition,
        Action revealWindow,
        Action resumeContent,
        Action<Exception> reportFailure)
    {
        // Position and DWM-cloak cleanup are independent. Even a destroyed or
        // otherwise unmovable HWND must not prevent the content lifecycle from
        // observing the visibility transition.
        try { restorePosition(); }
        catch (Exception ex) { reportFailure(ex); }
        try { revealWindow(); }
        catch (Exception ex) { reportFailure(ex); }
        resumeContent();
    }
}
