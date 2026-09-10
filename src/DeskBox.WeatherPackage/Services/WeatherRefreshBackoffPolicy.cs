namespace DeskBox.WeatherPackage.Services;

internal static class WeatherRefreshBackoffPolicy
{
    private static readonly TimeSpan[] s_failureDelays =
    [
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30)
    ];

    internal static readonly TimeSpan LocationReuseDuration = TimeSpan.FromHours(24);
    internal static readonly TimeSpan LocationFailureDelay = TimeSpan.FromMinutes(30);

    public static bool CanAttempt(
        DateTimeOffset now,
        DateTimeOffset automaticRefreshNotBeforeUtc,
        bool userTriggered,
        bool forceRefresh) =>
        userTriggered ||
        forceRefresh ||
        now >= automaticRefreshNotBeforeUtc;

    public static TimeSpan GetFailureDelay(int consecutiveFailures)
    {
        int index = Math.Clamp(consecutiveFailures - 1, 0, s_failureDelays.Length - 1);
        return s_failureDelays[index];
    }
}
