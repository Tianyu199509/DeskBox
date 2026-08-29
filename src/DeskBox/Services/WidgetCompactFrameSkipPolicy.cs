namespace DeskBox.Services;

/// <summary>
/// Budget-driven HWND resize cadence for capsule transitions. Animations start
/// at the display's native rate and escalate one level only when ticks keep
/// overrunning the frame budget, so high-refresh machines animate at full
/// rate while saturated machines fall back to the previous ~60fps behavior.
/// </summary>
internal static class WidgetCompactFrameSkipPolicy
{
    public const int FullRateLevel = 1;
    public const int SixtyFpsLevel = 2;
    public const int ThirtyFpsLevel = 3;

    // A tick counts as overrun when its interval exceeds the frame budget by
    // this factor (same threshold as WidgetCompactAnimationFrameTracker).
    public const double OverrunBudgetFactor = 1.5;

    public const int TickWindow = 8;
    public const int MinimumOverrunTicksToEscalate = 6;

    public static int ResolveSkip(int refreshRateHz, int level)
    {
        int rate = Math.Max(1, refreshRateHz);
        return level switch
        {
            SixtyFpsLevel => Math.Max(1, (int)Math.Round(rate / 60.0)),
            >= ThirtyFpsLevel => Math.Max(1, (int)Math.Round(rate / 30.0)),
            _ => 1
        };
    }

    public static bool IsOverrun(double intervalMs, double frameBudgetMs)
    {
        return intervalMs > frameBudgetMs * OverrunBudgetFactor;
    }

    public static bool ShouldEscalate(int overrunTicks, int sampledTicks)
    {
        return sampledTicks >= TickWindow &&
            overrunTicks >= MinimumOverrunTicksToEscalate;
    }

    public static int Escalate(int level)
    {
        return Math.Min(ThirtyFpsLevel, level + 1);
    }

    public static int ClampLevel(int level)
    {
        return Math.Clamp(level, FullRateLevel, ThirtyFpsLevel);
    }
}
