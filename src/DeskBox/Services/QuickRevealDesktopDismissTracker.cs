// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Correlates a quick-reveal outside dismissal with the exact desktop click
/// sequence that caused it. A dismissal is consumed at most once.
/// </summary>
internal sealed class QuickRevealDesktopDismissTracker
{
    private const int SystemMetricDoubleClickWidth = 36;
    private const int SystemMetricDoubleClickHeight = 37;

    private readonly uint _maximumIntervalMilliseconds;
    private readonly int _maximumDeltaX;
    private readonly int _maximumDeltaY;
    private bool _hasDismissal;
    private int _dismissX;
    private int _dismissY;
    private uint _dismissTime;

    public QuickRevealDesktopDismissTracker(
        uint maximumIntervalMilliseconds,
        int doubleClickWidth,
        int doubleClickHeight)
    {
        _maximumIntervalMilliseconds = Math.Max(1, maximumIntervalMilliseconds);
        _maximumDeltaX = Math.Max(1, doubleClickWidth / 2);
        _maximumDeltaY = Math.Max(1, doubleClickHeight / 2);
    }

    public static QuickRevealDesktopDismissTracker CreateForCurrentSystem()
    {
        return new QuickRevealDesktopDismissTracker(
            Win32Helper.GetDoubleClickTime(),
            Win32Helper.GetSystemMetrics(SystemMetricDoubleClickWidth),
            Win32Helper.GetSystemMetrics(SystemMetricDoubleClickHeight));
    }

    public void Record(int screenX, int screenY, uint eventTime)
    {
        _dismissX = screenX;
        _dismissY = screenY;
        _dismissTime = eventTime;
        _hasDismissal = true;
    }

    public bool ConsumeIfSameSequence(DesktopDoubleClickSequence sequence)
    {
        if (!_hasDismissal)
        {
            return false;
        }

        int dismissX = _dismissX;
        int dismissY = _dismissY;
        uint dismissTime = _dismissTime;
        Clear();

        uint firstToSecond = unchecked(sequence.SecondTime - sequence.FirstTime);
        uint firstToDismiss = unchecked(dismissTime - sequence.FirstTime);
        return firstToSecond <= _maximumIntervalMilliseconds &&
               firstToDismiss <= _maximumIntervalMilliseconds &&
               Math.Abs(dismissX - sequence.FirstX) <= _maximumDeltaX &&
               Math.Abs(dismissY - sequence.FirstY) <= _maximumDeltaY;
    }

    public void Clear()
    {
        _hasDismissal = false;
        _dismissX = 0;
        _dismissY = 0;
        _dismissTime = 0;
    }
}
