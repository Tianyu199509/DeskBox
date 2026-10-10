namespace DeskBox.Services;

/// <summary>
/// Classification of one window observed while walking the Z order below a
/// desktop-pinned widget. The classification is intentionally separate from
/// the Win32 walk so the bedding decision stays unit-testable.
/// </summary>
internal enum DesktopLayerBeddingWindowKind
{
    /// <summary>A DeskBox widget window, or a transient window owned by one.</summary>
    DeskBoxWindow,

    /// <summary>A desktop shell window: Progman, WorkerW, or SHELLDLL_DefView.</summary>
    ShellDesktopWindow,

    /// <summary>
    /// A window that occupies a Z-order slot below the widget but carries no
    /// visual meaning: invisible, minimized (iconic windows rest at the band
    /// bottom by design), or DWM-cloaked (suspended UWP / other virtual
    /// desktop).
    /// </summary>
    IgnoredWindow,

    /// <summary>A visible, non-minimized foreign application window.</summary>
    ForeignAppWindow,
}

/// <summary>
/// Decides whether a desktop-pinned widget already rests in the Explorer
/// desktop band. A visible foreign application window sitting below the
/// widget means the widget group is floating above that application — the
/// leak state behind #289/#447/#468 — so any bottom reassert must first be
/// validated with this predicate instead of blindly re-issuing
/// SetWindowPos(HWND_BOTTOM) transactions that repaint the DefView owner
/// band (the #241/#249/#449 flicker).
/// </summary>
internal static class DesktopLayerBeddingPolicy
{
    /// <summary>
    /// The widget is safely bedded when every window between it and the shell
    /// desktop host is DeskBox-owned, a shell desktop window, or ignorable.
    /// </summary>
    public static bool IsBeddedAtDesktopLayer(
        IEnumerable<DesktopLayerBeddingWindowKind> windowsBelow) =>
        windowsBelow.All(static kind =>
            kind is DesktopLayerBeddingWindowKind.DeskBoxWindow or
                DesktopLayerBeddingWindowKind.ShellDesktopWindow or
                DesktopLayerBeddingWindowKind.IgnoredWindow);

    /// <summary>
    /// Picks the foreign application window that witnesses a leak, for callers
    /// that need a stable identity when confirming the same leak across
    /// repeated observations. Ignored windows never witness a leak (they are
    /// not visually below the widget), so they are skipped.
    /// </summary>
    public static DesktopLayerBeddingWindowKind? FindLeakWitness(
        IEnumerable<DesktopLayerBeddingWindowKind> windowsBelow)
    {
        foreach (DesktopLayerBeddingWindowKind kind in windowsBelow)
        {
            if (kind == DesktopLayerBeddingWindowKind.ForeignAppWindow)
            {
                return kind;
            }
        }

        return null;
    }
}
