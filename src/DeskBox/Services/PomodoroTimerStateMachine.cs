using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

internal enum PomodoroTimerPhase
{
    Focus,
    Break
}

internal enum PomodoroTimerTransition
{
    None,
    Started,
    Paused,
    Reset,
    Skipped,
    FocusCompleted,
    BreakCompleted
}

internal readonly record struct PomodoroTimerSnapshot(
    PomodoroTimerPhase Phase,
    bool IsRunning,
    TimeSpan Remaining,
    int CompletedFocusRounds,
    DateTimeOffset? DeadlineUtc);

internal readonly record struct PomodoroTimerUpdate(
    PomodoroTimerSnapshot Snapshot,
    PomodoroTimerTransition Transition,
    bool ShouldPersist);

/// <summary>
/// 管理番茄钟的纯状态转换。运行态只保存 UTC 截止时间，调用方仅在
/// <see cref="PomodoroTimerUpdate.ShouldPersist"/> 为 <see langword="true"/>
/// 时持久化配置，因此普通刷新 Tick 不会触发写盘。
/// </summary>
internal sealed class PomodoroTimerStateMachine
{
    internal static readonly TimeSpan FocusDuration = TimeSpan.FromMinutes(25);
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromMinutes(5);

    private const string MetadataPrefix = "Pomodoro.";
    private const string VersionKey = MetadataPrefix + "Version";
    private const string PhaseKey = MetadataPrefix + "Phase";
    private const string RunningKey = MetadataPrefix + "Running";
    private const string RemainingTicksKey = MetadataPrefix + "RemainingTicks";
    private const string DeadlineUtcKey = MetadataPrefix + "DeadlineUtc";
    private const string CompletedFocusRoundsKey = MetadataPrefix + "CompletedFocusRounds";
    private const string CurrentVersion = "1";

    private static readonly string[] KnownMetadataKeys =
    [
        VersionKey,
        PhaseKey,
        RunningKey,
        RemainingTicksKey,
        DeadlineUtcKey,
        CompletedFocusRoundsKey
    ];

    private readonly Dictionary<string, string> _metadata;
    private PomodoroTimerPhase _phase;
    private bool _isRunning;
    private TimeSpan _remainingWhenPaused;
    private DateTimeOffset? _deadlineUtc;
    private int _completedFocusRounds;

    public PomodoroTimerStateMachine(
        WidgetConfig config,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);

        _metadata = config.Metadata ??= [];
        SetDefaultState();

        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (!KnownMetadataKeys.Any(_metadata.ContainsKey))
        {
            return;
        }

        if (!TryRestore(normalizedNow))
        {
            SetDefaultState();
            WasMetadataRecovered = true;
            PersistMetadata();
            return;
        }

        if (_isRunning && _deadlineUtc <= normalizedNow)
        {
            RestoreTransition = CompleteNaturally();
            PersistMetadata();
        }
    }

    /// <summary>
    /// 指示构造期间是否发现损坏或不完整的番茄钟元数据并恢复为安全默认值。
    /// </summary>
    public bool WasMetadataRecovered { get; private set; }

    /// <summary>
    /// 指示构造期间是否因修复元数据或处理已过期计时器而需要保存配置。
    /// </summary>
    public bool ShouldPersistRestore => WasMetadataRecovered ||
        RestoreTransition != PomodoroTimerTransition.None;

    /// <summary>
    /// 构造期间处理过期运行态时产生的自然完成转换。
    /// </summary>
    public PomodoroTimerTransition RestoreTransition { get; private set; }

    public PomodoroTimerSnapshot GetSnapshot(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        TimeSpan remaining = _isRunning && _deadlineUtc is { } deadline
            ? ClampRemaining(deadline - normalizedNow, DurationFor(_phase))
            : _remainingWhenPaused;
        return new PomodoroTimerSnapshot(
            _phase,
            _isRunning,
            remaining,
            _completedFocusRounds,
            _deadlineUtc);
    }

    public PomodoroTimerUpdate Start(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed))
        {
            return completed;
        }

        if (_isRunning)
        {
            return NoChange(normalizedNow);
        }

        _isRunning = true;
        _deadlineUtc = normalizedNow + _remainingWhenPaused;
        PersistMetadata();
        return Changed(normalizedNow, PomodoroTimerTransition.Started);
    }

    public PomodoroTimerUpdate Pause(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed))
        {
            return completed;
        }

        if (!_isRunning || _deadlineUtc is not { } deadline)
        {
            return NoChange(normalizedNow);
        }

        _remainingWhenPaused = ClampRemaining(
            deadline - normalizedNow,
            DurationFor(_phase));
        _isRunning = false;
        _deadlineUtc = null;
        PersistMetadata();
        return Changed(normalizedNow, PomodoroTimerTransition.Paused);
    }

    public PomodoroTimerUpdate Reset(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed))
        {
            return completed;
        }

        TimeSpan duration = DurationFor(_phase);
        if (!_isRunning &&
            _deadlineUtc is null &&
            _remainingWhenPaused == duration)
        {
            return NoChange(normalizedNow);
        }

        _isRunning = false;
        _deadlineUtc = null;
        _remainingWhenPaused = duration;
        PersistMetadata();
        return Changed(normalizedNow, PomodoroTimerTransition.Reset);
    }

    public PomodoroTimerUpdate Skip(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed))
        {
            return completed;
        }

        SwitchPhasePaused();
        PersistMetadata();
        return Changed(normalizedNow, PomodoroTimerTransition.Skipped);
    }

    public PomodoroTimerUpdate Tick(DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        return TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed)
            ? completed
            : NoChange(normalizedNow);
    }

    private bool TryRestore(DateTimeOffset utcNow)
    {
        if (!_metadata.TryGetValue(VersionKey, out string? version) ||
            version != CurrentVersion ||
            !_metadata.TryGetValue(PhaseKey, out string? phaseValue) ||
            !Enum.TryParse(phaseValue, ignoreCase: false, out _phase) ||
            !Enum.IsDefined(_phase) ||
            !_metadata.TryGetValue(RunningKey, out string? runningValue) ||
            !bool.TryParse(runningValue, out _isRunning) ||
            !_metadata.TryGetValue(CompletedFocusRoundsKey, out string? roundsValue) ||
            !int.TryParse(
                roundsValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _completedFocusRounds) ||
            _completedFocusRounds < 0)
        {
            return false;
        }

        TimeSpan duration = DurationFor(_phase);
        if (_isRunning)
        {
            if (!_metadata.TryGetValue(DeadlineUtcKey, out string? deadlineValue) ||
                !DateTimeOffset.TryParseExact(
                    deadlineValue,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset deadline))
            {
                return false;
            }

            _deadlineUtc = deadline.ToUniversalTime();
            _remainingWhenPaused = duration;

            // 截止时间超过本阶段最大时长，通常意味着元数据损坏或系统时钟回拨。
            // 安全恢复比让计时器异常延长更可预测。
            if (_deadlineUtc.Value - utcNow > duration)
            {
                return false;
            }

            return !_metadata.ContainsKey(RemainingTicksKey);
        }

        if (!_metadata.TryGetValue(RemainingTicksKey, out string? remainingValue) ||
            !long.TryParse(
                remainingValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long remainingTicks) ||
            remainingTicks <= 0 ||
            remainingTicks > duration.Ticks ||
            _metadata.ContainsKey(DeadlineUtcKey))
        {
            return false;
        }

        _remainingWhenPaused = TimeSpan.FromTicks(remainingTicks);
        _deadlineUtc = null;
        return true;
    }

    private bool TryCompleteExpired(
        DateTimeOffset utcNow,
        out PomodoroTimerUpdate update)
    {
        if (!_isRunning ||
            _deadlineUtc is not { } deadline ||
            deadline > utcNow)
        {
            update = default;
            return false;
        }

        PomodoroTimerTransition transition = CompleteNaturally();
        PersistMetadata();
        update = Changed(utcNow, transition);
        return true;
    }

    private PomodoroTimerTransition CompleteNaturally()
    {
        PomodoroTimerPhase completedPhase = _phase;
        if (completedPhase == PomodoroTimerPhase.Focus &&
            _completedFocusRounds < int.MaxValue)
        {
            _completedFocusRounds++;
        }

        SwitchPhasePaused();
        return completedPhase == PomodoroTimerPhase.Focus
            ? PomodoroTimerTransition.FocusCompleted
            : PomodoroTimerTransition.BreakCompleted;
    }

    private void SwitchPhasePaused()
    {
        _phase = _phase == PomodoroTimerPhase.Focus
            ? PomodoroTimerPhase.Break
            : PomodoroTimerPhase.Focus;
        _isRunning = false;
        _deadlineUtc = null;
        _remainingWhenPaused = DurationFor(_phase);
    }

    private void SetDefaultState()
    {
        _phase = PomodoroTimerPhase.Focus;
        _isRunning = false;
        _remainingWhenPaused = FocusDuration;
        _deadlineUtc = null;
        _completedFocusRounds = 0;
        RestoreTransition = PomodoroTimerTransition.None;
    }

    private void PersistMetadata()
    {
        _metadata[VersionKey] = CurrentVersion;
        _metadata[PhaseKey] = _phase.ToString();
        _metadata[RunningKey] = _isRunning ? bool.TrueString : bool.FalseString;
        _metadata[CompletedFocusRoundsKey] =
            _completedFocusRounds.ToString(CultureInfo.InvariantCulture);

        if (_isRunning && _deadlineUtc is { } deadline)
        {
            _metadata[DeadlineUtcKey] = deadline
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            _metadata.Remove(RemainingTicksKey);
        }
        else
        {
            _metadata[RemainingTicksKey] =
                _remainingWhenPaused.Ticks.ToString(CultureInfo.InvariantCulture);
            _metadata.Remove(DeadlineUtcKey);
        }
    }

    private PomodoroTimerUpdate Changed(
        DateTimeOffset utcNow,
        PomodoroTimerTransition transition) =>
        new(GetSnapshot(utcNow), transition, ShouldPersist: true);

    private PomodoroTimerUpdate NoChange(DateTimeOffset utcNow) =>
        new(GetSnapshot(utcNow), PomodoroTimerTransition.None, ShouldPersist: false);

    private static TimeSpan DurationFor(PomodoroTimerPhase phase) =>
        phase == PomodoroTimerPhase.Focus ? FocusDuration : BreakDuration;

    private static TimeSpan ClampRemaining(TimeSpan value, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > maximum ? maximum : value;
    }
}
