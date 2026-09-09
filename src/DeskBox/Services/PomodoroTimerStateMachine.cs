using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public enum PomodoroTimerPhase
{
    Focus,
    ShortBreak,
    LongBreak
}

internal enum PomodoroTimerTransition
{
    None,
    Started,
    Paused,
    Reset,
    Skipped,
    FocusCompleted,
    ShortBreakCompleted,
    LongBreakCompleted
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
    internal static readonly TimeSpan FocusDuration = TimeSpan.FromMinutes(
        PomodoroSettingsPolicy.DefaultFocusMinutes);
    internal static readonly TimeSpan ShortBreakDuration = TimeSpan.FromMinutes(
        PomodoroSettingsPolicy.DefaultShortBreakMinutes);
    internal static readonly TimeSpan LongBreakDuration = TimeSpan.FromMinutes(
        PomodoroSettingsPolicy.DefaultLongBreakMinutes);

    private const string MetadataPrefix = "Pomodoro.";
    private const string VersionKey = MetadataPrefix + "Version";
    private const string PhaseKey = MetadataPrefix + "Phase";
    private const string RunningKey = MetadataPrefix + "Running";
    private const string RemainingTicksKey = MetadataPrefix + "RemainingTicks";
    private const string DeadlineUtcKey = MetadataPrefix + "DeadlineUtc";
    private const string CompletedFocusRoundsKey = MetadataPrefix + "CompletedFocusRounds";
    private const string LegacyVersion = "1";
    private const string CurrentVersion = "2";

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
    private TimeSpan _focusDuration;
    private TimeSpan _shortBreakDuration;
    private TimeSpan _longBreakDuration;
    private int _roundCount;

    public PomodoroTimerStateMachine(
        WidgetConfig config,
        DateTimeOffset utcNow,
        int focusMinutes = PomodoroSettingsPolicy.DefaultFocusMinutes,
        int shortBreakMinutes = PomodoroSettingsPolicy.DefaultShortBreakMinutes,
        int longBreakMinutes = PomodoroSettingsPolicy.DefaultLongBreakMinutes,
        int roundCount = PomodoroSettingsPolicy.DefaultRoundCount)
    {
        ArgumentNullException.ThrowIfNull(config);

        _metadata = config.Metadata ??= [];
        ApplyDurations(
            focusMinutes,
            shortBreakMinutes,
            longBreakMinutes,
            roundCount);
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
        else if (WasMetadataMigrated)
        {
            PersistMetadata();
        }
    }

    /// <summary>
    /// 指示构造期间是否发现损坏或不完整的番茄钟元数据并恢复为安全默认值。
    /// </summary>
    public bool WasMetadataRecovered { get; private set; }

    /// <summary>
    /// 指示构造期间是否把旧版阶段或轮次元数据迁移为当前三阶段模型。
    /// </summary>
    public bool WasMetadataMigrated { get; private set; }

    /// <summary>
    /// 指示构造期间是否因修复元数据或处理已过期计时器而需要保存配置。
    /// </summary>
    public bool ShouldPersistRestore => WasMetadataRecovered ||
        WasMetadataMigrated ||
        RestoreTransition != PomodoroTimerTransition.None;

    /// <summary>
    /// 构造期间处理过期运行态时产生的自然完成转换。
    /// </summary>
    public PomodoroTimerTransition RestoreTransition { get; private set; }

    public TimeSpan CurrentPhaseDuration => DurationFor(_phase);

    public int RoundCount => _roundCount;

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

        // “跳过”表示主动结束当前阶段。若跳过的是专注阶段，也必须推进
        // 当前轮次；否则连续跳过“专注 → 休息”后会再次回到第一轮。
        AdvanceFocusRoundIfNeeded();
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

    /// <summary>
    /// 将新的番茄钟偏好应用到当前阶段。保留已经消耗的时间，避免修改设置后
    /// 计时器跳回起点；当新时长短于已消耗时间时，将剩余时间收敛到一秒，
    /// 由下一次 Tick 正常完成阶段。
    /// </summary>
    public PomodoroTimerUpdate UpdateSettings(
        int focusMinutes,
        int shortBreakMinutes,
        int longBreakMinutes,
        int roundCount,
        DateTimeOffset utcNow)
    {
        DateTimeOffset normalizedNow = utcNow.ToUniversalTime();
        if (TryCompleteExpired(normalizedNow, out PomodoroTimerUpdate completed))
        {
            return completed;
        }

        TimeSpan oldDuration = DurationFor(_phase);
        TimeSpan oldRemaining = _isRunning && _deadlineUtc is { } deadline
            ? ClampRemaining(deadline - normalizedNow, oldDuration)
            : _remainingWhenPaused;

        TimeSpan oldFocusDuration = _focusDuration;
        TimeSpan oldShortBreakDuration = _shortBreakDuration;
        TimeSpan oldLongBreakDuration = _longBreakDuration;
        int oldRoundCount = _roundCount;
        PomodoroTimerPhase oldPhase = _phase;
        int oldCompletedFocusRounds = _completedFocusRounds;
        ApplyDurations(
            focusMinutes,
            shortBreakMinutes,
            longBreakMinutes,
            roundCount);
        ReconcileCycleState();

        TimeSpan newDuration = DurationFor(_phase);
        if (oldFocusDuration == _focusDuration &&
            oldShortBreakDuration == _shortBreakDuration &&
            oldLongBreakDuration == _longBreakDuration &&
            oldRoundCount == _roundCount &&
            oldPhase == _phase &&
            oldCompletedFocusRounds == _completedFocusRounds)
        {
            return NoChange(normalizedNow);
        }

        TimeSpan elapsed = oldDuration - oldRemaining;
        TimeSpan adjustedRemaining = newDuration - elapsed;
        if (adjustedRemaining <= TimeSpan.Zero)
        {
            adjustedRemaining = TimeSpan.FromSeconds(1);
        }
        else if (adjustedRemaining > newDuration)
        {
            adjustedRemaining = newDuration;
        }

        _remainingWhenPaused = adjustedRemaining;
        _deadlineUtc = _isRunning
            ? normalizedNow + adjustedRemaining
            : null;
        PersistMetadata();
        return Changed(normalizedNow, PomodoroTimerTransition.None);
    }

    private bool TryRestore(DateTimeOffset utcNow)
    {
        if (!_metadata.TryGetValue(VersionKey, out string? version) ||
            version is not (LegacyVersion or CurrentVersion) ||
            !_metadata.TryGetValue(PhaseKey, out string? phaseValue) ||
            !_metadata.TryGetValue(RunningKey, out string? runningValue) ||
            !bool.TryParse(runningValue, out _isRunning) ||
            !_metadata.TryGetValue(CompletedFocusRoundsKey, out string? roundsValue) ||
            !int.TryParse(
                roundsValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int completedFocusRounds) ||
            completedFocusRounds < 0 ||
            !TryResolveRestoredPhase(version, phaseValue, out _phase))
        {
            return false;
        }

        _completedFocusRounds = completedFocusRounds;
        PomodoroTimerPhase restoredPhase = _phase;
        int restoredCompletedFocusRounds = _completedFocusRounds;
        ReconcileCycleState();
        WasMetadataMigrated = version == LegacyVersion ||
            restoredPhase != _phase ||
            restoredCompletedFocusRounds != _completedFocusRounds;

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
        AdvanceFocusRoundIfNeeded();

        SwitchPhasePaused();
        return completedPhase switch
        {
            PomodoroTimerPhase.Focus => PomodoroTimerTransition.FocusCompleted,
            PomodoroTimerPhase.ShortBreak =>
                PomodoroTimerTransition.ShortBreakCompleted,
            PomodoroTimerPhase.LongBreak =>
                PomodoroTimerTransition.LongBreakCompleted,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private void SwitchPhasePaused()
    {
        switch (_phase)
        {
            case PomodoroTimerPhase.Focus:
                _phase = _completedFocusRounds >= _roundCount
                    ? PomodoroTimerPhase.LongBreak
                    : PomodoroTimerPhase.ShortBreak;
                break;
            case PomodoroTimerPhase.ShortBreak:
                _phase = PomodoroTimerPhase.Focus;
                break;
            case PomodoroTimerPhase.LongBreak:
                _phase = PomodoroTimerPhase.Focus;
                _completedFocusRounds = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        _isRunning = false;
        _deadlineUtc = null;
        _remainingWhenPaused = DurationFor(_phase);
    }

    private void SetDefaultState()
    {
        _phase = PomodoroTimerPhase.Focus;
        _isRunning = false;
        _remainingWhenPaused = _focusDuration;
        _deadlineUtc = null;
        _completedFocusRounds = 0;
        RestoreTransition = PomodoroTimerTransition.None;
        WasMetadataMigrated = false;
    }

    private void AdvanceFocusRoundIfNeeded()
    {
        if (_phase == PomodoroTimerPhase.Focus)
        {
            _completedFocusRounds = Math.Min(
                _completedFocusRounds + 1,
                _roundCount);
        }
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

    private TimeSpan DurationFor(PomodoroTimerPhase phase) => phase switch
    {
        PomodoroTimerPhase.Focus => _focusDuration,
        PomodoroTimerPhase.ShortBreak => _shortBreakDuration,
        PomodoroTimerPhase.LongBreak => _longBreakDuration,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private void ApplyDurations(
        int focusMinutes,
        int shortBreakMinutes,
        int longBreakMinutes,
        int roundCount)
    {
        _focusDuration = TimeSpan.FromMinutes(
            PomodoroSettingsPolicy.NormalizeFocusMinutes(focusMinutes));
        _shortBreakDuration = TimeSpan.FromMinutes(
            PomodoroSettingsPolicy.NormalizeShortBreakMinutes(shortBreakMinutes));
        _longBreakDuration = TimeSpan.FromMinutes(
            PomodoroSettingsPolicy.NormalizeLongBreakMinutes(longBreakMinutes));
        _roundCount = PomodoroSettingsPolicy.NormalizeRoundCount(roundCount);
    }

    private static bool TryResolveRestoredPhase(
        string version,
        string phaseValue,
        out PomodoroTimerPhase phase)
    {
        if (version == LegacyVersion)
        {
            if (phaseValue == nameof(PomodoroTimerPhase.Focus))
            {
                phase = PomodoroTimerPhase.Focus;
                return true;
            }

            if (phaseValue == "Break")
            {
                // 旧版只有一个休息阶段；具体映射会结合已完成轮数，
                // 在 ReconcileCycleState 中确定短休息或长休息。
                phase = PomodoroTimerPhase.ShortBreak;
                return true;
            }

            phase = default;
            return false;
        }

        return Enum.TryParse(phaseValue, ignoreCase: false, out phase) &&
            Enum.IsDefined(phase);
    }

    private void ReconcileCycleState()
    {
        switch (_phase)
        {
            case PomodoroTimerPhase.Focus:
                _completedFocusRounds %= _roundCount;
                break;
            case PomodoroTimerPhase.ShortBreak:
            {
                int completedInCycle = _completedFocusRounds % _roundCount;
                if (_completedFocusRounds == 0)
                {
                    // 旧版允许“跳过专注”后进入休息但不推进轮数。迁移时将
                    // 该休息视为第一轮之后的休息，避免继续显示第 1 轮。
                    completedInCycle = 1;
                }

                if (_roundCount == 1 || completedInCycle == 0)
                {
                    _phase = PomodoroTimerPhase.LongBreak;
                    _completedFocusRounds = _roundCount;
                }
                else
                {
                    _completedFocusRounds = completedInCycle;
                }

                break;
            }
            case PomodoroTimerPhase.LongBreak:
                _completedFocusRounds = _roundCount;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static TimeSpan ClampRemaining(TimeSpan value, TimeSpan maximum)
    {
        if (value <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > maximum ? maximum : value;
    }
}
