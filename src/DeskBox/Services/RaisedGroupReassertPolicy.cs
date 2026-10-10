namespace DeskBox.Services;

/// <summary>
/// Chooses how a raised widget group reasserts its band after one of its
/// windows takes activation. The initial tray-raise lift runs while DeskBox
/// may still be a background process, and Windows pins a background
/// process's windows below the foreground application — only the widget that
/// activates crosses that line. Peer reordering is deliberately relative and
/// never crosses a foreign window, so once DeskBox owns the foreground a
/// still-split band must repeat the full group lift instead.
/// </summary>
internal static class RaisedGroupReassertPolicy
{
    internal enum ReassertAction
    {
        /// <summary>Repeats the group-wide HWND_TOP lift.</summary>
        LiftGroupAboveForeignWindows,

        /// <summary>Applies the repaint-free peer-relative reorder.</summary>
        ReorderPeersOnly,
    }

    public static ReassertAction Resolve(bool foreignWindowAboveRaisedPeer) =>
        foreignWindowAboveRaisedPeer
            ? ReassertAction.LiftGroupAboveForeignWindows
            : ReassertAction.ReorderPeersOnly;
}
