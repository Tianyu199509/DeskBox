using DeskBox.Platform;

namespace DeskBox.Services;

internal enum QuickRevealOutsideSampleActionKind
{
    None = 0,
    HoldExternalPress = 1,
    DismissOutsideClick = 2,
    KeepOpenAfterDropIntoDeskBox = 3,
    DismissOutsideDragEnded = 4,
}

internal readonly record struct QuickRevealOutsideSampleResult(
    QuickRevealOutsideSampleActionKind Action,
    bool TaskbarOrigin = false);

/// <summary>
/// Tracks external pointer press/drag gestures while Quick Reveal is open so
/// users can summon Quick Reveal first and then drag files or text from a
/// background window into a DeskBox widget without the initial MouseDown on
/// the source window dismissing the revealed group.
/// </summary>
internal struct QuickRevealOutsideInteractionState
{
    private bool _lastMouseDown;
    private bool _ignoreCurrentPressUntilUp;
    private bool _trackingExternalPress;
    private bool _pressStartedOverTaskbar;
    private bool _dragThresholdExceeded;
    private Win32Helper.POINT _pressOrigin;
    private bool _hasPressOrigin;

    public readonly bool ShouldHoldForegroundDismiss => _trackingExternalPress;

    public static QuickRevealOutsideInteractionState CreateInitial(bool initialMouseDown)
    {
        return new QuickRevealOutsideInteractionState
        {
            _lastMouseDown = initialMouseDown,
            _ignoreCurrentPressUntilUp = initialMouseDown,
        };
    }

    public QuickRevealOutsideSampleResult Step(
        bool isMouseDown,
        Win32Helper.POINT? cursor,
        bool pointerOverDeskBox,
        bool pointerOverTaskbar,
        int dragThresholdX,
        int dragThresholdY)
    {
        int thresholdX = Math.Max(4, dragThresholdX);
        int thresholdY = Math.Max(4, dragThresholdY);

        if (_ignoreCurrentPressUntilUp)
        {
            _lastMouseDown = isMouseDown;
            if (!isMouseDown)
            {
                _ignoreCurrentPressUntilUp = false;
            }

            return new QuickRevealOutsideSampleResult(QuickRevealOutsideSampleActionKind.None);
        }

        if (isMouseDown && !_lastMouseDown)
        {
            _lastMouseDown = true;
            if (pointerOverDeskBox)
            {
                _trackingExternalPress = false;
                return new QuickRevealOutsideSampleResult(QuickRevealOutsideSampleActionKind.None);
            }

            _trackingExternalPress = true;
            _pressStartedOverTaskbar = pointerOverTaskbar;
            _dragThresholdExceeded = false;
            if (cursor.HasValue)
            {
                _pressOrigin = cursor.Value;
                _hasPressOrigin = true;
            }
            else
            {
                _hasPressOrigin = false;
            }

            return new QuickRevealOutsideSampleResult(
                QuickRevealOutsideSampleActionKind.HoldExternalPress,
                pointerOverTaskbar);
        }

        if (isMouseDown && _trackingExternalPress)
        {
            _lastMouseDown = true;
            if (!_dragThresholdExceeded && cursor.HasValue)
            {
                if (!_hasPressOrigin)
                {
                    _pressOrigin = cursor.Value;
                    _hasPressOrigin = true;
                }
                else if (Math.Abs(cursor.Value.X - _pressOrigin.X) >= thresholdX ||
                         Math.Abs(cursor.Value.Y - _pressOrigin.Y) >= thresholdY)
                {
                    _dragThresholdExceeded = true;
                }
            }

            return new QuickRevealOutsideSampleResult(
                QuickRevealOutsideSampleActionKind.HoldExternalPress,
                _pressStartedOverTaskbar);
        }

        if (!isMouseDown && _trackingExternalPress)
        {
            bool taskbarOrigin = _pressStartedOverTaskbar;
            bool wasDrag = _dragThresholdExceeded;
            if (!wasDrag && cursor.HasValue && _hasPressOrigin &&
                (Math.Abs(cursor.Value.X - _pressOrigin.X) >= thresholdX ||
                 Math.Abs(cursor.Value.Y - _pressOrigin.Y) >= thresholdY))
            {
                wasDrag = true;
            }

            _lastMouseDown = false;
            _trackingExternalPress = false;
            _pressStartedOverTaskbar = false;
            _dragThresholdExceeded = false;
            _hasPressOrigin = false;

            if (!wasDrag)
            {
                // Even if the user pressed outside and wiggled 1px, a plain click
                // outside DeskBox dismisses Quick Reveal on release.
                return new QuickRevealOutsideSampleResult(
                    QuickRevealOutsideSampleActionKind.DismissOutsideClick,
                    taskbarOrigin);
            }

            if (pointerOverDeskBox)
            {
                return new QuickRevealOutsideSampleResult(
                    QuickRevealOutsideSampleActionKind.KeepOpenAfterDropIntoDeskBox);
            }

            return new QuickRevealOutsideSampleResult(
                QuickRevealOutsideSampleActionKind.DismissOutsideDragEnded);
        }

        _lastMouseDown = isMouseDown;
        return new QuickRevealOutsideSampleResult(QuickRevealOutsideSampleActionKind.None);
    }
}
