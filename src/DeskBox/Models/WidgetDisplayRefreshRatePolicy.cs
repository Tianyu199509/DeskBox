namespace DeskBox.Models;

/// <summary>
/// Keeps display refresh-rate data sane when a driver reports an invalid or
/// transient value. The compositor remains the animation clock; this value is
/// used for frame-budget diagnostics and display-aware visual throttling.
/// </summary>
public static class WidgetDisplayRefreshRatePolicy
{
    public const int DefaultRefreshRateHz = 60;

    public static int Normalize(uint refreshRateHz, int fallbackHz = DefaultRefreshRateHz)
    {
        int safeFallback = fallbackHz is >= 24 and <= 1000
            ? fallbackHz
            : DefaultRefreshRateHz;
        return refreshRateHz is >= 24 and <= 1000
            ? (int)refreshRateHz
            : safeFallback;
    }
}
