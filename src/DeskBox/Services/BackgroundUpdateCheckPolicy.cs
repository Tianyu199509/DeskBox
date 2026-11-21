using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure pacing rules for the periodic background update-check loop (the
/// PowerToys runner cadence): a definitive answer — new version found or
/// verifiably up to date — waits a full day, while anything that left the
/// question open retries in two hours. Invalid manifests retry short too:
/// a partially deployed feed self-heals on the next attempt. The short
/// retry exists because deskbox.fun DNS blackouts on individual machines
/// are a documented field reality, and a one-shot launch check that fails
/// used to silence updates for the whole process lifetime.
/// </summary>
public static class BackgroundUpdateCheckPolicy
{
    public static readonly TimeSpan DefinitiveAnswerInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(2);
    public static readonly TimeSpan StartupCooldownInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FutureClockSkewTolerance = TimeSpan.FromMinutes(5);

    public static bool IsDefinitive(AppUpdateCheckStatus status)
    {
        return status is AppUpdateCheckStatus.UpdateAvailable or AppUpdateCheckStatus.UpToDate;
    }

    public static TimeSpan GetDelayUntilNextCheck(AppUpdateCheckStatus? status)
    {
        return status is not null && IsDefinitive(status.Value)
            ? DefinitiveAnswerInterval
            : FailureRetryInterval;
    }

    /// <summary>
    /// Computes the first check delay on app launch. Honors a recent persisted
    /// <paramref name="lastCheckAt"/> within <see cref="StartupCooldownInterval"/>
    /// so rapid restarts do not hammer stable.json or rewrite settings every
    /// 12 seconds, while keeping cross-session discovery bounded to 30 minutes
    /// and ignoring implausibly future-dated timestamps.
    /// </summary>
    public static TimeSpan GetInitialStartupDelay(
        TimeSpan minimumStartupDelay,
        DateTimeOffset? lastCheckAt,
        DateTimeOffset now)
    {
        TimeSpan floor = minimumStartupDelay < TimeSpan.Zero ? TimeSpan.Zero : minimumStartupDelay;
        if (lastCheckAt is not DateTimeOffset recorded ||
            recorded > now + FutureClockSkewTolerance)
        {
            return floor;
        }

        TimeSpan elapsed = now - recorded;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed >= StartupCooldownInterval)
        {
            return floor;
        }

        TimeSpan remaining = StartupCooldownInterval - elapsed;
        return remaining > floor ? remaining : floor;
    }
}
