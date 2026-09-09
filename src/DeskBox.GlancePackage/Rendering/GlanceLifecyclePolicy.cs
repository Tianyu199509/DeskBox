namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Pure lifecycle activity policy (audit rounds 18-19): which timers may run
/// given the widget's state. The rules mirror the built-in widget's
/// UpdateTimers conditions so the controller stays a thin shell and the
/// energy behavior stays testable without a UI thread.
/// </summary>
internal static class GlanceLifecyclePolicy
{
    internal readonly record struct Activity(ClockCadence Cadence, bool RotationRunning);

    /// <summary>
    /// Cadence comes from GlanceDisplayPolicy.ComputeClockCadence (the
    /// display-settings half of the decision); this gate applies the
    /// energy half - hidden/long-hidden stops everything, compact and user
    /// pause stop only the image rotation.
    /// </summary>
    internal static Activity Compute(
        bool visible,
        bool longHidden,
        bool collapsed,
        bool paused,
        ClockCadence cadence,
        bool rotationConfigured,
        bool multipleImages)
    {
        bool active = visible && !longHidden;
        return new Activity(
            Cadence: active ? cadence : ClockCadence.None,
            RotationRunning: active && !collapsed && !paused && rotationConfigured && multipleImages);
    }
}
