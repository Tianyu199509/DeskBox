namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Pure lifecycle activity policy (audit rounds 18-19): which timers may run
/// given the widget's state. The rules mirror the built-in widget's
/// UpdateTimers conditions so the controller stays a thin shell and the
/// energy behavior stays testable without a UI thread.
/// </summary>
internal static class GlanceLifecyclePolicy
{
    internal readonly record struct Activity(bool ClockRunning, bool RotationRunning);

    internal static Activity Compute(
        bool visible,
        bool longHidden,
        bool collapsed,
        bool paused,
        bool rotationConfigured,
        bool multipleImages)
    {
        bool active = visible && !longHidden;
        return new Activity(
            ClockRunning: active,
            RotationRunning: active && !collapsed && !paused && rotationConfigured && multipleImages);
    }

    /// <summary>
    /// One-shot interval to the next minute boundary (+50 ms guard so the
    /// tick lands just past the boundary, mirroring the built-in cadence).
    /// </summary>
    internal static TimeSpan DelayToNextMinute(DateTime now)
    {
        double remainingMs = 60_000 - (now.Second * 1000) - now.Millisecond;
        return TimeSpan.FromMilliseconds(Math.Max(1, remainingMs) + 50);
    }
}
