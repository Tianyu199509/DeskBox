namespace DeskBox.Services;

/// <summary>
/// Governs quick-reveal sessions raised through the tray icon. After a
/// tray-icon click the shell returns the foreground to the taskbar, and the
/// click's own down-press can dismiss an active session an instant before the
/// click's up event enqueues the toggle; neither effect may undo the user's
/// single click.
/// </summary>
internal static class QuickRevealTrayRaisePolicy
{
    public const double TaskbarPressDismissCooldownMilliseconds = 400.0;

    public static bool KeepsRaisedStateOnTaskbarForeground(
        bool isQuickRevealMode,
        bool raisedFromTrayIcon)
    {
        return isQuickRevealMode && raisedFromTrayIcon;
    }

    public static bool ShouldSuppressTrayRaise(
        bool isQuickRevealMode,
        bool isTrayIconSource,
        bool lastDismissTaskbarOrigin,
        double millisecondsSinceLastDismiss)
    {
        return isQuickRevealMode &&
               isTrayIconSource &&
               lastDismissTaskbarOrigin &&
               millisecondsSinceLastDismiss >= 0.0 &&
               millisecondsSinceLastDismiss <= TaskbarPressDismissCooldownMilliseconds;
    }
}
