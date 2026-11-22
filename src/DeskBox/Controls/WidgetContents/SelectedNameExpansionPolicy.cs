namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Pure gating rules for the Windows-desktop-style selected-name expansion
/// (a trimmed icon label grows downward on sole selection and collapses when
/// the selection changes) and for the slow-click rename entry (clicking the
/// label of the item that was already the sole selection). Hosts keep the
/// state; these functions keep the policy pinned by contract tests.
/// </summary>
public static class SelectedNameExpansionPolicy
{
    /// <summary>
    /// Sole selection may expand its trimmed label only in double-click-to-
    /// open mode while the surface window actually holds keyboard focus. In
    /// single-click mode a click opens the item immediately, and in unfocused
    /// desktop-pinned mode (MA_NOACTIVATE) expanding a floating selection card
    /// would falsely suggest keyboard shortcuts apply to the widget. Marquee
    /// box selection and active drags pass through momentary single-selection
    /// states and must not flash an expansion while they do.
    /// </summary>
    public static bool ShouldExpandSelectedItemName(
        bool isDoubleClickToOpen,
        int selectedItemCount,
        bool isBoxSelecting,
        bool isDragActive,
        bool isWindowFocused = true,
        bool isOpenInFlightOrSuppressed = false) =>
        isWindowFocused &&
        isDoubleClickToOpen &&
        selectedItemCount == 1 &&
        !isBoxSelecting &&
        !isDragActive &&
        !isOpenInFlightOrSuppressed;

    /// <summary>
    /// A click may start inline rename only when the surface window holds
    /// keyboard focus and the click lands on the name label of the item that
    /// was already the sole selection before this click (and had been settled
    /// as the sole selection for at least one double-click interval, with no
    /// open or recent double-tap in flight), with no modifier keys, in
    /// double-click-to-open mode.
    /// </summary>
    public static bool ShouldStartRenameFromItemClick(
        bool isDoubleClickToOpen,
        bool hasModifierKey,
        bool wasSoleSelectedItemBeforeClick,
        bool pointerPressedOnLabel,
        bool isWindowFocused = true,
        bool isSelectionSettledPastDoubleClick = true,
        bool isOpenInFlightOrSuppressed = false) =>
        isWindowFocused &&
        isDoubleClickToOpen &&
        !hasModifierKey &&
        wasSoleSelectedItemBeforeClick &&
        pointerPressedOnLabel &&
        isSelectionSettledPastDoubleClick &&
        !isOpenInFlightOrSuppressed;

    /// <summary>
    /// Returns true only after the item has remained the sole selection for at
    /// least one system double-click interval, preventing the second stroke of
    /// a double-click on a freshly clicked item from arming slow-click rename.
    /// </summary>
    public static bool IsSoleSelectionSettledForSlowClickRename(
        long nowTick64,
        long soleSelectionCommittedTick64,
        uint doubleClickTimeMs)
    {
        if (soleSelectionCommittedTick64 <= 0)
        {
            return false;
        }

        long windowMs = doubleClickTimeMs == 0 ? 500L : doubleClickTimeMs;
        return nowTick64 - soleSelectionCommittedTick64 >= windowMs;
    }

    /// <summary>
    /// Suppresses slow-click rename both when <c>ItemClick</c> arrives on the
    /// trailing release of a <c>DoubleTapped</c> gesture and when a previously
    /// armed rename timer ticks while an open from that double-tap is running.
    /// </summary>
    public static bool IsWithinRecentDoubleTapSuppressionWindow(
        long nowTick64,
        long lastDoubleTapTick64,
        uint doubleClickTimeMs,
        int graceMs = 150)
    {
        if (lastDoubleTapTick64 <= 0)
        {
            return false;
        }

        long elapsed = nowTick64 - lastDoubleTapTick64;
        if (elapsed < 0)
        {
            return false;
        }

        long windowMs = doubleClickTimeMs == 0 ? 500L : doubleClickTimeMs;
        long suppressionWindowMs = (windowMs * 2L) + Math.Max(0, graceMs);
        return elapsed < suppressionWindowMs;
    }

    /// <summary>
    /// Delay before growing a pointer-selected item's trimmed filename overlay
    /// downward so a normal double-click opens the item cleanly without
    /// flashing a multi-line height expansion between the two clicks.
    /// </summary>
    public static int ComputePointerNameExpansionDelayMs(uint doubleClickTimeMs)
    {
        uint windowMs = doubleClickTimeMs == 0 ? 500u : doubleClickTimeMs;
        return Math.Clamp((int)Math.Round(windowMs * 0.65), 200, 380);
    }

    /// <summary>
    /// Height for the inline rename editor in icon mode: at least the tile's
    /// own label height, grown to the measured wrapped full-name height,
    /// clamped to the distance from the label to the host surface bottom so
    /// the desktop-style vertical editor never detaches from its item.
    /// </summary>
    public static double ClampRenameEditorHeight(
        double measuredNameHeight,
        double tileHeight,
        double availableHeight,
        double minimumHeight)
    {
        double ceiling = Math.Max(minimumHeight, availableHeight);
        return Math.Clamp(
            Math.Max(measuredNameHeight, tileHeight),
            minimumHeight,
            ceiling);
    }
}
