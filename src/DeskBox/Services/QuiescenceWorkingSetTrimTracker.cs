namespace DeskBox.Services;

/// <summary>
/// UI facts the quiescence trim needs. Active user interaction, in-flight
/// visual animation, and active transient windows block a trim outright; once
/// DeskBox becomes quiet, working-set compression runs across both visible and
/// hidden desktop states without churning steady-state sessions.
/// </summary>
internal readonly record struct QuiescenceTrimActivitySnapshot(
    bool HasVisibleWidgets,
    bool IsWidgetInteractionActive,
    bool HasActiveVisualWork,
    bool IsTransientUiOpen,
    bool IsDeskBoxForeground,
    bool IsPointerOverDeskBox,
    bool HasAmbientVisualWork = false);

internal enum QuiescenceTrimTier
{
    Hidden,
    VisibleBackground,
    VisibleForeground
}

internal readonly record struct QuiescenceTrimDecision(
    bool ShouldTrim,
    QuiescenceTrimTier Tier,
    string Reason,
    TimeSpan QuietDuration,
    bool EpisodeTrim = false)
{
    internal static QuiescenceTrimDecision Skip(
        QuiescenceTrimTier tier,
        string reason,
        TimeSpan quiet = default) =>
        new(false, tier, reason, quiet);
}

/// <summary>
/// Decides when a process-wide working-set trim should run after DeskBox goes
/// idle. Uses a two-stage model:
/// 1. Episode trim: once per idle episode (after startup, user interaction,
///    transient UI, or tier transition) once the tier's quiet window and
///    20-second minimum interval elapse and working set exceeds 80 MB.
/// 2. Growth backstop: inside the same unbroken visible idle episode, samples a
///    settled baseline after 90 s and only re-trims after 5 minutes if working
///    set regrows materially (>= max(32 MB, 25% of baseline)). Hidden sessions
///    disarm their timer completely once settled.
/// </summary>
internal sealed class QuiescenceWorkingSetTrimTracker
{
    internal static readonly TimeSpan HiddenQuietDuration = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan VisibleBackgroundQuietDuration = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan VisibleForegroundQuietDuration = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MinimumTrimInterval = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan StaticBackgroundMinimumTrimInterval =
        TimeSpan.FromMinutes(5);
    internal const long MinimumWorkingSetGrowthBytes = 32L * 1024 * 1024;
    internal const long MaximumWorkingSetGrowthBytes = 128L * 1024 * 1024;
    internal const double MinimumWorkingSetGrowthRatio = 0.25;
    internal const long EpisodeTrimMinimumWorkingSetBytes = 80L * 1024 * 1024;
    internal static readonly TimeSpan BaselineSettleDelay = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan BlockedRetryInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan BelowEpisodeFloorRetryInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan VisibleForegroundMonitorInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SteadyStatePollInterval = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private DateTimeOffset? _quietSince;
    private QuiescenceTrimTier? _quietTier;
    private DateTimeOffset? _lastTrimAt;
    private long? _settledWorkingSetBaseline;
    private bool _episodeTrimAvailable = true;
    private bool _trimmedInCurrentHiddenSession;
    private bool _lastObservedBelowEpisodeFloor;

    internal bool IsHiddenSessionSettled
    {
        get
        {
            lock (_gate)
            {
                return _trimmedInCurrentHiddenSession;
            }
        }
    }

    internal void NoteActivity()
    {
        lock (_gate)
        {
            BreakIdleEpisodeCore(rearmHiddenTrim: true);
        }
    }

    private void BreakIdleEpisodeCore(bool rearmHiddenTrim)
    {
        _quietSince = null;
        _quietTier = null;
        _settledWorkingSetBaseline = null;
        _episodeTrimAvailable = true;
        _lastObservedBelowEpisodeFloor = false;
        if (rearmHiddenTrim)
        {
            _trimmedInCurrentHiddenSession = false;
        }
    }

    internal QuiescenceTrimDecision Observe(
        DateTimeOffset now,
        QuiescenceTrimActivitySnapshot activity,
        Func<long> readWorkingSetBytes)
    {
        lock (_gate)
        {
            QuiescenceTrimTier tier = ResolveTier(activity);
            if (tier != QuiescenceTrimTier.Hidden)
            {
                _trimmedInCurrentHiddenSession = false;
            }

            if (activity.IsWidgetInteractionActive ||
                activity.HasActiveVisualWork ||
                activity.IsTransientUiOpen)
            {
                BreakIdleEpisodeCore(rearmHiddenTrim: true);
                return QuiescenceTrimDecision.Skip(tier, DescribeBlocker(activity));
            }

            if (_quietTier is null)
            {
                _quietTier = tier;
                if (!_trimmedInCurrentHiddenSession)
                {
                    _quietSince ??= now;
                }
            }
            else if (_quietTier != tier)
            {
                bool wasEpisodeAvailable = _episodeTrimAvailable;
                QuiescenceTrimTier previousTier = _quietTier.Value;
                _episodeTrimAvailable = true;
                _settledWorkingSetBaseline = null;
                _lastObservedBelowEpisodeFloor = false;
                if (!wasEpisodeAvailable ||
                    tier == QuiescenceTrimTier.VisibleForeground ||
                    previousTier == QuiescenceTrimTier.Hidden ||
                    tier == QuiescenceTrimTier.Hidden)
                {
                    _quietSince = now;
                }

                _quietTier = tier;
            }

            if (tier == QuiescenceTrimTier.Hidden && _trimmedInCurrentHiddenSession)
            {
                return QuiescenceTrimDecision.Skip(
                    QuiescenceTrimTier.Hidden,
                    "hidden-session-settled");
            }

            if (!_episodeTrimAvailable &&
                _settledWorkingSetBaseline is null &&
                _lastTrimAt is DateTimeOffset settledAfterTrimAt &&
                now - settledAfterTrimAt >= BaselineSettleDelay)
            {
                _settledWorkingSetBaseline = readWorkingSetBytes();
            }

            _quietSince ??= now;
            TimeSpan quiet = now - _quietSince.Value;
            TimeSpan requiredQuiet = GetEffectiveRequiredQuietDuration(tier, activity);
            if (quiet < requiredQuiet)
            {
                return QuiescenceTrimDecision.Skip(
                    tier,
                    "waiting-for-quiet",
                    quiet);
            }

            if (_episodeTrimAvailable)
            {
                if (_lastTrimAt is DateTimeOffset lastEpisodeTrimAt &&
                    now - lastEpisodeTrimAt < MinimumTrimInterval)
                {
                    return QuiescenceTrimDecision.Skip(
                        tier,
                        "trim-cooldown",
                        quiet);
                }

                long currentWorkingSet = readWorkingSetBytes();
                if (currentWorkingSet < EpisodeTrimMinimumWorkingSetBytes)
                {
                    _lastObservedBelowEpisodeFloor = true;
                    if (tier == QuiescenceTrimTier.Hidden)
                    {
                        _trimmedInCurrentHiddenSession = true;
                    }

                    return QuiescenceTrimDecision.Skip(
                        tier,
                        "below-episode-floor",
                        quiet);
                }

                _lastObservedBelowEpisodeFloor = false;
                return new QuiescenceTrimDecision(
                    true,
                    tier,
                    "episode-idle",
                    quiet,
                    EpisodeTrim: true);
            }

            TimeSpan steadyStateCooldown = tier == QuiescenceTrimTier.Hidden
                ? MinimumTrimInterval
                : StaticBackgroundMinimumTrimInterval;
            if (_lastTrimAt is DateTimeOffset lastSteadyTrimAt &&
                now - lastSteadyTrimAt < steadyStateCooldown)
            {
                return QuiescenceTrimDecision.Skip(
                    tier,
                    "trim-cooldown",
                    quiet);
            }

            if (_settledWorkingSetBaseline is not long baseline)
            {
                _settledWorkingSetBaseline = readWorkingSetBytes();
                return QuiescenceTrimDecision.Skip(
                    tier,
                    "sampling-baseline",
                    quiet);
            }

            long growth = readWorkingSetBytes() - baseline;
            long requiredGrowth = Math.Clamp(
                (long)(baseline * MinimumWorkingSetGrowthRatio),
                MinimumWorkingSetGrowthBytes,
                MaximumWorkingSetGrowthBytes);
            if (growth < requiredGrowth)
            {
                return QuiescenceTrimDecision.Skip(
                    tier,
                    "insufficient-growth",
                    quiet);
            }

            return new QuiescenceTrimDecision(
                true,
                tier,
                "growth-backstop",
                quiet,
                EpisodeTrim: false);
        }
    }

    internal TimeSpan? GetNextEvaluationDelay(
        DateTimeOffset now,
        QuiescenceTrimActivitySnapshot activity)
    {
        lock (_gate)
        {
            if (!activity.HasVisibleWidgets && _trimmedInCurrentHiddenSession)
            {
                return null;
            }

            if (activity.IsWidgetInteractionActive ||
                activity.HasActiveVisualWork ||
                activity.IsTransientUiOpen)
            {
                return BlockedRetryInterval;
            }

            QuiescenceTrimTier tier = ResolveTier(activity);
            if (_episodeTrimAvailable)
            {
                if (_lastObservedBelowEpisodeFloor)
                {
                    return tier == QuiescenceTrimTier.VisibleForeground
                        ? VisibleForegroundMonitorInterval
                        : BelowEpisodeFloorRetryInterval;
                }

                TimeSpan requiredQuiet = GetEffectiveRequiredQuietDuration(tier, activity);
                TimeSpan remainingQuiet = _quietSince is DateTimeOffset quietSince
                    ? requiredQuiet - (now - quietSince)
                    : requiredQuiet;
                TimeSpan remainingCooldown = _lastTrimAt is DateTimeOffset lastTrimAt
                    ? MinimumTrimInterval - (now - lastTrimAt)
                    : TimeSpan.Zero;

                TimeSpan delay = remainingQuiet > remainingCooldown
                    ? remainingQuiet
                    : remainingCooldown;
                if (tier == QuiescenceTrimTier.VisibleForeground &&
                    remainingCooldown < delay &&
                    delay > VisibleBackgroundQuietDuration)
                {
                    delay = VisibleBackgroundQuietDuration;
                }

                return delay > TimeSpan.FromMilliseconds(250)
                    ? delay
                    : TimeSpan.FromMilliseconds(250);
            }

            if (tier == QuiescenceTrimTier.VisibleForeground)
            {
                return VisibleForegroundMonitorInterval;
            }

            if (_settledWorkingSetBaseline is null &&
                _lastTrimAt is DateTimeOffset trimmedAt)
            {
                TimeSpan untilBaseline = BaselineSettleDelay - (now - trimmedAt);
                if (untilBaseline > TimeSpan.FromSeconds(1) &&
                    untilBaseline < SteadyStatePollInterval)
                {
                    return untilBaseline;
                }
            }

            return SteadyStatePollInterval;
        }
    }

    /// <summary>
    /// Records a completed trim from any path (quiescence, immediate hidden,
    /// or deep cleanup) so cooldown and growth accounting stay unified.
    /// </summary>
    internal void CommitTrim(DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastTrimAt = now;
            _settledWorkingSetBaseline = null;
            _episodeTrimAvailable = false;
            _lastObservedBelowEpisodeFloor = false;
            _quietSince = now;
            _trimmedInCurrentHiddenSession =
                _quietTier is null or QuiescenceTrimTier.Hidden;
        }
    }

    /// <summary>
    /// Whether any trim path committed within <paramref name="interval"/>.
    /// Other trim callers use this to skip redundant back-to-back trims.
    /// </summary>
    internal bool LastTrimWithin(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            return _lastTrimAt is DateTimeOffset trimmedAt && now - trimmedAt < interval;
        }
    }

    internal static TimeSpan GetRequiredQuietDuration(QuiescenceTrimTier tier) => tier switch
    {
        QuiescenceTrimTier.Hidden => HiddenQuietDuration,
        QuiescenceTrimTier.VisibleForeground => VisibleForegroundQuietDuration,
        _ => VisibleBackgroundQuietDuration
    };

    private static TimeSpan GetEffectiveRequiredQuietDuration(
        QuiescenceTrimTier tier,
        QuiescenceTrimActivitySnapshot activity) =>
        tier == QuiescenceTrimTier.VisibleBackground && activity.HasAmbientVisualWork
            ? VisibleForegroundQuietDuration
            : GetRequiredQuietDuration(tier);

    private static QuiescenceTrimTier ResolveTier(QuiescenceTrimActivitySnapshot activity)
    {
        if (!activity.HasVisibleWidgets)
        {
            return QuiescenceTrimTier.Hidden;
        }

        return activity.IsDeskBoxForeground || activity.IsPointerOverDeskBox
            ? QuiescenceTrimTier.VisibleForeground
            : QuiescenceTrimTier.VisibleBackground;
    }

    private static string DescribeBlocker(QuiescenceTrimActivitySnapshot activity)
    {
        if (activity.IsWidgetInteractionActive)
        {
            return "blocked:interaction";
        }

        return activity.HasActiveVisualWork
            ? "blocked:visual-work"
            : "blocked:transient-ui";
    }
}
