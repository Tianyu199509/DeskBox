namespace DeskBox.Models;

/// <summary>
/// Keeps display refresh-rate data sane when a driver reports an invalid or
/// transient value. The compositor remains the animation clock; this value is
/// used for frame-budget diagnostics and display-aware visual throttling.
/// </summary>
public static class WidgetDisplayRefreshRatePolicy
{
    public const int DefaultRefreshRateHz = 60;

    // High-refresh displays need commit ticks at their native frame period;
    // a coarse floor reintroduces the beat-pattern judder this interval exists
    // to remove. The ceiling keeps sub-60Hz panels from ticking needlessly fast.
    public const double MinimumFrameTickMs = 4.0;
    public const double MaximumFrameTickMs = 16.67;

    public static int Normalize(uint refreshRateHz, int fallbackHz = DefaultRefreshRateHz)
    {
        int safeFallback = fallbackHz is >= 24 and <= 1000
            ? fallbackHz
            : DefaultRefreshRateHz;
        return refreshRateHz is >= 24 and <= 1000
            ? (int)refreshRateHz
            : safeFallback;
    }

    /// <summary>
    /// Resolves the Win10 fallback clock tick from the display refresh rate so
    /// commit cadence matches the present cadence (60Hz -> ~16.7ms, 144Hz ->
    /// ~6.9ms, 240Hz -> ~4.2ms) instead of a fixed interval that beats against
    /// the compositor.
    /// </summary>
    public static TimeSpan ResolveFrameTickInterval(int refreshRateHz)
    {
        int normalized = refreshRateHz is >= 24 and <= 1000
            ? refreshRateHz
            : DefaultRefreshRateHz;
        double intervalMs = Math.Clamp(
            1000.0 / normalized,
            MinimumFrameTickMs,
            MaximumFrameTickMs);
        return TimeSpan.FromMilliseconds(intervalMs);
    }
}
