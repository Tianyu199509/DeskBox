namespace DeskBox.Services;

/// <summary>
/// Preparation runs before animation callbacks exist. A native move failure
/// must therefore finalize the host's requested visibility immediately.
/// </summary>
internal static class WidgetTrayAnimationPreparation
{
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
