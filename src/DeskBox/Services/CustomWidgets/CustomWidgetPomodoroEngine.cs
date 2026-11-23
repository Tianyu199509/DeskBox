using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetPomodoroCompletionNotice(
    string CompletedPhase,
    string NextPhase,
    string Title,
    string Message,
    string NotifyMode,
    int CompletedFocusCountToday);

public sealed record CustomWidgetPomodoroSnapshot(
    string Phase,
    bool IsRunning,
    int TotalSeconds,
    int RemainingSeconds,
    int CycleInSet,
    int TodayCompleted,
    int TodayMinutes,
    int DailyGoal,
    int StreakDays,
    string TodayDate,
    string CurrentTask,
    string TimeText,
    double ProgressPercent,
    double GoalPercent,
    string PhaseLabel,
    string PhasePill,
    string PhaseHint,
    string Glyph,
    string CycleBadge,
    string PrimaryActionText,
    string CapsuleTitle,
    string CapsuleSummary,
    string CapsuleBadge,
    bool IsPaused = false,
    string HeaderPillText = "",
    string DialSubtext = "",
    // Dual-rhythm (Pomodoro focus / sedentary guardian) extension fields.
    string Mode = "pomodoro",
    bool IsAwaySuspended = false,
    bool IsDue = false,
    int SatMinutes = 0,
    int IntervalMin = 0,
    int StoodCount = 0,
    int LongestSitMin = 0,
    string MicroAction = "",
    string HealthSummary = "",
    string ConfirmActionText = "",
    string SnoozeActionText = "",
    // Dual-track concurrent projection tokens (both tracks observable simultaneously).
    bool SedentaryIsRunning = false,
    bool SedentaryIsDue = false,
    bool SedentaryIsAway = false,
    double SedentaryProgressPercent = 0.0,
    int SedentarySatMinutes = 0,
    bool PomodoroIsRunning = false,
    string PomodoroTimeText = "25:00");

/// <summary>
/// Deterministic, zero-drift wall-clock state machine for the native Pomodoro Custom Widget.
/// Stores an absolute UTC deadline (<c>pomo.endUtcTicks</c>) while running so laptop sleep,
/// capsule collapse, or process restarts never lose seconds.
/// </summary>
public static class CustomWidgetPomodoroEngine
{
    public const string PhaseFocus = "focus";
    public const string PhaseShortBreak = "shortBreak";
    public const string PhaseLongBreak = "longBreak";
    public const string PhaseSedentary = "sedentary";

    public const string ModePomodoro = "pomodoro";
    public const string ModeSedentary = "sedentary";

    public const string SettingRhythmMode = "rhythmMode";
    public const string SettingSedentaryIntervalMin = "sedentaryIntervalMin";
    public const string SettingIdleAutoResetMin = "idleAutoResetMin";
    public const string SettingBackgroundSedentary = "backgroundSedentary";

    public const string KeyPhase = "pomo.phase";
    public const string KeyRunning = "pomo.running";
    public const string KeyEndUtcTicks = "pomo.endUtcTicks";
    public const string KeyRemainingSec = "pomo.remainingSec";
    public const string KeyTotalSec = "pomo.totalSec";
    public const string KeyCycle = "pomo.cycle";
    public const string KeyTodayDate = "pomo.todayDate";
    public const string KeyTodayCompleted = "pomo.todayCompleted";
    public const string KeyTodayMinutes = "pomo.todayMinutes";
    public const string KeyStreakDays = "pomo.streakDays";
    public const string KeyLastGoalHitDate = "pomo.lastGoalHitDate";
    public const string KeyCurrentTask = "currentTask";
    public const string KeyLinkedTaskTitle = "pomo.linkedTaskTitle";

    // Independent sedentary guardian track keys.
    public const string KeySedentaryRunning = "pomo.sedentary.running";
    public const string KeySedentaryEndUtcTicks = "pomo.sedentary.endUtcTicks";
    public const string KeySedentaryRemainingSec = "pomo.sedentary.remainingSec";
    public const string KeySedentaryTotalSec = "pomo.sedentary.totalSec";
    public const string KeySedentaryAway = "pomo.sedentary.away";
    public const string KeySedentaryDue = "pomo.sedentary.due";
    public const string KeySedentaryDueAtUtcTicks = "pomo.sedentary.dueAtTicks";
    public const string KeySedentaryDueCount = "pomo.sedentary.dueCount";
    public const string KeySedentaryStoodCount = "pomo.sedentary.stoodCount";
    public const string KeySedentaryLongestMin = "pomo.sedentary.longestMin";

    /// <summary>
    /// Keyboard/mouse idle (seconds) below which a previously away-suspended user is
    /// considered "back at the desk"; the fresh sedentary round is anchored here.
    /// </summary>
    public const double AwayReturnIdleSeconds = 1.5;

    public static bool IsPomodoroRunning(IReadOnlyDictionary<string, string> state) =>
        state.TryGetValue(KeyRunning, out string? v) &&
        string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsSedentaryRunning(IReadOnlyDictionary<string, string> state) =>
        state.TryGetValue(KeySedentaryRunning, out string? sr) &&
        string.Equals(sr, "true", StringComparison.OrdinalIgnoreCase);

    public static bool HasAnyRunningTrack(IReadOnlyDictionary<string, string> state) =>
        IsPomodoroRunning(state) || IsSedentaryRunning(state);

    public static bool IsRunning(IReadOnlyDictionary<string, string> state) =>
        HasAnyRunningTrack(state);

    public static bool IsBackgroundSedentaryEnabled(IReadOnlyDictionary<string, string> settings) =>
        settings.TryGetValue(SettingBackgroundSedentary, out string? raw) &&
        (string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw?.Trim(), "1", StringComparison.Ordinal));

    public static (int FocusMin, int ShortBreakMin, int LongBreakMin, int DailyGoal, string FlowMode, string NotifyMode)
        ReadPreferences(IReadOnlyDictionary<string, string> settings)
    {
        int focusMin = ParseClampedInt(settings, "focusMinutes", 25, 1, 180);

        int shortBreakMin = 5;
        int longBreakMin = 15;
        if (settings.TryGetValue("breakPreset", out string? preset) && !string.IsNullOrWhiteSpace(preset))
        {
            string[] parts = preset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sb))
            {
                shortBreakMin = Math.Clamp(sb, 1, 60);
            }
            if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lb))
            {
                longBreakMin = Math.Clamp(lb, 1, 90);
            }
        }
        else
        {
            shortBreakMin = ParseClampedInt(settings, "shortBreakMinutes", 5, 1, 60);
            longBreakMin = ParseClampedInt(settings, "longBreakMinutes", 15, 1, 90);
        }

        int dailyGoal = ParseClampedInt(settings, "dailyGoal", 8, 1, 30);
        string flowMode = settings.TryGetValue("flowMode", out string? fm) && !string.IsNullOrWhiteSpace(fm)
            ? fm.Trim().ToLowerInvariant()
            : "semi";
        string notifyMode = settings.TryGetValue("notifyMode", out string? nm) && !string.IsNullOrWhiteSpace(nm)
            ? nm.Trim().ToLowerInvariant()
            : "toast_sound";

        return (focusMin, shortBreakMin, longBreakMin, dailyGoal, flowMode, notifyMode);
    }

    /// <summary>
    /// Reads the dual-rhythm preferences, defaulting to <c>pomodoro</c> mode when omitted.
    /// </summary>
    public static (string RhythmMode, int SedentaryIntervalMin, int IdleAutoResetMin) ReadRhythmPreferences(
        IReadOnlyDictionary<string, string> settings)
    {
        string rhythmMode = settings.TryGetValue(SettingRhythmMode, out string? rm) &&
                            string.Equals(rm?.Trim(), ModeSedentary, StringComparison.OrdinalIgnoreCase)
            ? ModeSedentary
            : ModePomodoro;

        int intervalMin = ParseClampedInt(settings, SettingSedentaryIntervalMin, 50, 20, 180);
        int idleResetMin = ParseClampedInt(settings, SettingIdleAutoResetMin, 5, 0, 60);
        return (rhythmMode, intervalMin, idleResetMin);
    }

    public static bool IsSedentaryMode(IReadOnlyDictionary<string, string> settings) =>
        ReadRhythmPreferences(settings).RhythmMode == ModeSedentary;

    public static bool IsSedentaryAway(IReadOnlyDictionary<string, string> state) =>
        ReadFlag(state, KeySedentaryAway);

    public static bool IsSedentaryDue(IReadOnlyDictionary<string, string> state) =>
        ReadFlag(state, KeySedentaryDue);

    public static int GetDefaultDurationSeconds(string phase, IReadOnlyDictionary<string, string> settings)
    {
        if (string.Equals(NormalizePhase(phase), PhaseSedentary, StringComparison.Ordinal))
        {
            return ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
        }

        var prefs = ReadPreferences(settings);
        return NormalizePhase(phase) switch
        {
            PhaseShortBreak => prefs.ShortBreakMin * 60,
            PhaseLongBreak => prefs.LongBreakMin * 60,
            _ => prefs.FocusMin * 60
        };
    }

    public static bool EnsureTodayRollover(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string recordedDate = state.TryGetValue(KeyTodayDate, out string? d) ? d : string.Empty;
        if (string.Equals(recordedDate, today, StringComparison.Ordinal))
        {
            return false;
        }

        state[KeyTodayDate] = today;
        state[KeyTodayCompleted] = "0";
        state[KeyTodayMinutes] = "0";
        state[KeySedentaryStoodCount] = "0";
        state[KeySedentaryLongestMin] = "0";
        state[KeySedentaryDueCount] = "0";

        // If streak was active, verify last goal hit was at least yesterday; otherwise reset streak.
        if (state.TryGetValue(KeyLastGoalHitDate, out string? lastHit) &&
            !string.IsNullOrWhiteSpace(lastHit) &&
            DateOnly.TryParseExact(lastHit, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly hitDay) &&
            DateOnly.TryParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly currentDay))
        {
            int gapDays = currentDay.DayNumber - hitDay.DayNumber;
            if (gapDays > 1)
            {
                state[KeyStreakDays] = "0";
            }
        }

        return true;
    }

    private readonly record struct PomodoroTrackProjection(
        string Phase,
        bool IsRunning,
        int TotalSeconds,
        int RemainingSeconds,
        int CycleInSet,
        string TimeText,
        double ProgressPercent,
        bool IsPaused);

    private readonly record struct SedentaryTrackProjection(
        bool IsRunning,
        bool IsAwaySuspended,
        bool IsDue,
        int TotalSeconds,
        int RemainingSeconds,
        int SatMinutes,
        int OverdueSeconds,
        double ProgressPercent,
        bool IsPaused,
        string TimeText);

    private static PomodoroTrackProjection ComputePomodoroTrackProjection(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        string rawPhase = state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus;
        string phase = string.Equals(rawPhase, PhaseSedentary, StringComparison.OrdinalIgnoreCase)
            ? PhaseFocus
            : NormalizePhase(rawPhase);
        bool running = IsPomodoroRunning(state);
        int defaultSec = GetDefaultDurationSeconds(phase, settings);

        int totalSec = state.TryGetValue(KeyTotalSec, out string? totRaw) &&
                       int.TryParse(totRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTot) &&
                       parsedTot > 0
            ? parsedTot
            : defaultSec;

        int remainingSec;
        if (running &&
            state.TryGetValue(KeyEndUtcTicks, out string? endRaw) &&
            long.TryParse(endRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long endTicks) &&
            endTicks > 0)
        {
            double diffSec = TimeSpan.FromTicks(endTicks - now.UtcTicks).TotalSeconds;
            remainingSec = Math.Max(0, (int)Math.Ceiling(diffSec));
        }
        else if (state.TryGetValue(KeyRemainingSec, out string? remRaw) &&
                 int.TryParse(remRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRem) &&
                 parsedRem >= 0)
        {
            remainingSec = parsedRem;
        }
        else
        {
            remainingSec = defaultSec;
        }

        if (totalSec < remainingSec)
        {
            totalSec = remainingSec;
        }

        int cycle = state.TryGetValue(KeyCycle, out string? cycRaw) &&
                    int.TryParse(cycRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCyc)
            ? Math.Clamp(parsedCyc, 1, 4)
            : 1;

        string timeText = FormatMinutesSeconds(remainingSec);
        double progressPct = totalSec > 0
            ? Math.Clamp((1.0 - (double)remainingSec / totalSec) * 100.0, 0.0, 100.0)
            : 0.0;
        bool isPaused = !running && remainingSec > 0 && remainingSec < totalSec;

        return new PomodoroTrackProjection(
            phase, running, totalSec, remainingSec, cycle, timeText, progressPct, isPaused);
    }

    private static SedentaryTrackProjection ComputeSedentaryTrackProjection(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        bool running = IsSedentaryRunning(state);
        bool away = ReadFlag(state, KeySedentaryAway);
        bool due = ReadFlag(state, KeySedentaryDue);

        int totalSec = ReadPositiveInt32(state, KeySedentaryTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now, KeySedentaryEndUtcTicks);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeySedentaryRemainingSec) ?? intervalSec;
        }

        if (due)
        {
            remainingSec = 0;
        }

        if (totalSec < remainingSec)
        {
            totalSec = remainingSec;
        }

        bool isPaused = !running && remainingSec > 0 && remainingSec < totalSec;
        int elapsedSec = Math.Max(0, totalSec - remainingSec);
        int satMinutes = elapsedSec / 60;

        int overdueSec = 0;
        if (due &&
            state.TryGetValue(KeySedentaryDueAtUtcTicks, out string? dueAtRaw) &&
            long.TryParse(dueAtRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dueAtTicks) &&
            dueAtTicks > 0)
        {
            overdueSec = Math.Max(0, (int)Math.Ceiling(TimeSpan.FromTicks(now.UtcTicks - dueAtTicks).TotalSeconds));
        }

        string timeText = due
            ? FormatMinutesSeconds(overdueSec)
            : FormatMinutesSeconds(remainingSec);
        double progressPct = due
            ? 100.0
            : (totalSec > 0
                ? Math.Clamp((1.0 - (double)remainingSec / totalSec) * 100.0, 0.0, 100.0)
                : 0.0);

        return new SedentaryTrackProjection(
            running, away, due, totalSec, remainingSec, satMinutes, overdueSec, progressPct, isPaused, timeText);
    }

    public static CustomWidgetPomodoroSnapshot ComputeSnapshot(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        PomodoroTrackProjection pomo = ComputePomodoroTrackProjection(state, settings, now);
        SedentaryTrackProjection sed = ComputeSedentaryTrackProjection(state, settings, now);

        if (IsSedentaryMode(settings))
        {
            return ComputeSedentarySnapshot(state, settings, now, culture, pomo, sed);
        }

        static string T(string c, string key) => LocalizationService.TranslateForCulture(c, key);
        static string F(string c, string key, params object[] args) => LocalizationService.FormatForCulture(c, key, args);

        var prefs = ReadPreferences(settings);
        string phase = pomo.Phase;
        bool running = pomo.IsRunning;
        int totalSec = pomo.TotalSeconds;
        int remainingSec = pomo.RemainingSeconds;
        int cycle = pomo.CycleInSet;

        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isSameDay = state.TryGetValue(KeyTodayDate, out string? savedDate) &&
                         string.Equals(savedDate, today, StringComparison.Ordinal);

        int todayCompleted = isSameDay &&
                             state.TryGetValue(KeyTodayCompleted, out string? tcRaw) &&
                             int.TryParse(tcRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tc)
            ? Math.Max(0, tc)
            : 0;

        int todayMinutes = isSameDay &&
                           state.TryGetValue(KeyTodayMinutes, out string? tmRaw) &&
                           int.TryParse(tmRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tm)
            ? Math.Max(0, tm)
            : 0;

        int streakDays = state.TryGetValue(KeyStreakDays, out string? stRaw) &&
                         int.TryParse(stRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int st)
            ? Math.Max(0, st)
            : 0;

        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int stoodCount = isSameDay &&
                         state.TryGetValue(KeySedentaryStoodCount, out string? scRaw) &&
                         int.TryParse(scRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sc)
            ? Math.Max(0, sc)
            : 0;
        int longestSitMin = isSameDay &&
                            state.TryGetValue(KeySedentaryLongestMin, out string? lsRaw) &&
                            int.TryParse(lsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ls)
            ? Math.Max(0, ls)
            : 0;

        string currentTask = state.TryGetValue(KeyCurrentTask, out string? taskRaw) && !string.IsNullOrWhiteSpace(taskRaw)
            ? taskRaw.Trim()
            : (state.TryGetValue(KeyLinkedTaskTitle, out string? linkedRaw) ? linkedRaw.Trim() : string.Empty);

        string timeText = pomo.TimeText;
        double progressPct = pomo.ProgressPercent;
        double goalPct = prefs.DailyGoal > 0
            ? Math.Clamp((double)todayCompleted / prefs.DailyGoal * 100.0, 0.0, 100.0)
            : 0.0;

        string pauseText = T(culture, "CustomWidget.Pomodoro.ActionPause");
        (string phaseLabel, string phasePill, string phaseHint, string glyph, string actionText) = phase switch
        {
            PhaseShortBreak => (
                T(culture, "CustomWidget.Pomodoro.PhaseShortLabel"),
                running ? T(culture, "CustomWidget.Pomodoro.PhaseShortRunning") : T(culture, "CustomWidget.Pomodoro.PhaseShortIdle"),
                T(culture, "CustomWidget.Pomodoro.PhaseShortHint"),
                "\uEC32",
                running ? pauseText : T(culture, "CustomWidget.Pomodoro.ActionStartBreak")),
            PhaseLongBreak => (
                T(culture, "CustomWidget.Pomodoro.PhaseLongLabel"),
                running ? T(culture, "CustomWidget.Pomodoro.PhaseLongRunning") : T(culture, "CustomWidget.Pomodoro.PhaseLongIdle"),
                T(culture, "CustomWidget.Pomodoro.PhaseLongHint"),
                "\uE823",
                running ? pauseText : T(culture, "CustomWidget.Pomodoro.ActionStartBreak")),
            _ => (
                T(culture, "CustomWidget.Pomodoro.PhaseFocusLabel"),
                running ? T(culture, "CustomWidget.Pomodoro.PhaseFocusRunning") : T(culture, "CustomWidget.Pomodoro.PhaseFocusIdle"),
                !string.IsNullOrEmpty(currentTask)
                    ? F(culture, "CustomWidget.Pomodoro.PhaseFocusTaskHintFormat", currentTask)
                    : (running
                        ? T(culture, "CustomWidget.Pomodoro.PhaseFocusRunningHint")
                        : T(culture, "CustomWidget.Pomodoro.PhaseFocusIdleHint")),
                "\uE916",
                running ? pauseText : T(culture, "CustomWidget.Pomodoro.ActionStartFocus"))
        };

        string cycleBadge = F(culture, "CustomWidget.Pomodoro.CycleBadgeFormat", cycle);
        bool isPaused = pomo.IsPaused;

        string headerPillText = phase switch
        {
            PhaseShortBreak => running
                ? T(culture, "CustomWidget.Pomodoro.PhaseShortRunning")
                : (isPaused ? T(culture, "CustomWidget.Pomodoro.PhaseShortPaused") : T(culture, "CustomWidget.Pomodoro.PhaseShortFullLabel")),
            PhaseLongBreak => running
                ? T(culture, "CustomWidget.Pomodoro.PhaseLongRunning")
                : (isPaused ? T(culture, "CustomWidget.Pomodoro.PhaseLongPaused") : T(culture, "CustomWidget.Pomodoro.PhaseLongFullLabel")),
            _ => running
                ? T(culture, "CustomWidget.Pomodoro.PhaseFocusRunning")
                : (isPaused ? T(culture, "CustomWidget.Pomodoro.Paused") : T(culture, "CustomWidget.Pomodoro.PhaseFocusLabel"))
        };

        string dialSubtext;
        if (running)
        {
            string finishClock = now.ToLocalTime().AddSeconds(remainingSec).ToString("HH:mm", CultureInfo.InvariantCulture);
            dialSubtext = F(culture, "CustomWidget.Pomodoro.DialFinishAtFormat", finishClock);
        }
        else if (isPaused)
        {
            dialSubtext = T(culture, "CustomWidget.Pomodoro.Paused");
        }
        else if (phase == PhaseFocus && !string.IsNullOrEmpty(currentTask))
        {
            dialSubtext = currentTask;
        }
        else if (phase == PhaseFocus && todayCompleted > 0)
        {
            dialSubtext = F(culture, "CustomWidget.Pomodoro.DialTodayCompletedFormat", todayCompleted);
        }
        else
        {
            dialSubtext = string.Empty;
        }

        string capsuleContext = phase switch
        {
            PhaseFocus => !string.IsNullOrEmpty(currentTask)
                ? currentTask
                : (running
                    ? T(culture, "CustomWidget.Pomodoro.CapsuleFocusSession")
                    : F(culture, "CustomWidget.Pomodoro.CapsuleTodayRatioFormat", todayCompleted, prefs.DailyGoal)),
            PhaseShortBreak => T(culture, "CustomWidget.Pomodoro.CapsuleShortBreak"),
            _ => T(culture, "CustomWidget.Pomodoro.CapsuleLongBreak")
        };
        string capsuleTitle = sed.IsDue
            ? $"{timeText} · {T(culture, "CustomWidget.Pomodoro.Sedentary.CapsuleDue")}"
            : $"{timeText} · {capsuleContext}";
        string capsuleSummary = phase == PhaseFocus
            ? F(culture, "CustomWidget.Pomodoro.CapsuleSummaryFormat", cycle, todayMinutes)
            : phaseHint;
        string capsuleBadge = sed.IsDue
            ? T(culture, "CustomWidget.Pomodoro.Sedentary.CapsuleDue")
            : running
                ? (phase == PhaseFocus
                    ? F(culture, "CustomWidget.Pomodoro.CapsuleBadgeFocusRunningFormat", cycle)
                    : F(culture, "CustomWidget.Pomodoro.CapsuleBadgeBreakRunningFormat", phaseLabel))
                : (todayCompleted > 0
                    ? F(culture, "CustomWidget.Pomodoro.CapsuleBadgeCompletedFormat", todayCompleted)
                    : string.Empty);

        return new CustomWidgetPomodoroSnapshot(
            Phase: phase,
            IsRunning: running,
            TotalSeconds: totalSec,
            RemainingSeconds: remainingSec,
            CycleInSet: cycle,
            TodayCompleted: todayCompleted,
            TodayMinutes: todayMinutes,
            DailyGoal: prefs.DailyGoal,
            StreakDays: streakDays,
            TodayDate: today,
            CurrentTask: currentTask,
            TimeText: timeText,
            ProgressPercent: progressPct,
            GoalPercent: goalPct,
            PhaseLabel: phaseLabel,
            PhasePill: phasePill,
            PhaseHint: phaseHint,
            Glyph: glyph,
            CycleBadge: cycleBadge,
            PrimaryActionText: actionText,
            CapsuleTitle: capsuleTitle,
            CapsuleSummary: capsuleSummary,
            CapsuleBadge: capsuleBadge,
            IsPaused: isPaused,
            HeaderPillText: headerPillText,
            DialSubtext: dialSubtext,
            IntervalMin: intervalMin,
            StoodCount: stoodCount,
            LongestSitMin: longestSitMin,
            ConfirmActionText: T(culture, "CustomWidget.Pomodoro.Sedentary.ConfirmBtn"),
            SnoozeActionText: T(culture, "CustomWidget.Pomodoro.Sedentary.SnoozeBtn"),
            SedentaryIsRunning: sed.IsRunning,
            SedentaryIsDue: sed.IsDue,
            SedentaryIsAway: sed.IsAwaySuspended,
            SedentaryProgressPercent: sed.ProgressPercent,
            SedentarySatMinutes: sed.SatMinutes,
            PomodoroIsRunning: running,
            PomodoroTimeText: timeText);
    }

    /// <summary>
    /// Sedentary ("久坐护眼守护") snapshot: reads the independent sedentary track while also
    /// projecting concurrent Pomodoro track status.
    /// </summary>
    private static CustomWidgetPomodoroSnapshot ComputeSedentarySnapshot(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        string culture,
        PomodoroTrackProjection pomo,
        SedentaryTrackProjection sed)
    {
        static string T(string c, string key) => LocalizationService.TranslateForCulture(c, key);
        static string F(string c, string key, params object[] args) => LocalizationService.FormatForCulture(c, key, args);

        var prefs = ReadPreferences(settings);
        (_, int intervalMin, int idleResetMin) = ReadRhythmPreferences(settings);

        bool running = sed.IsRunning;
        bool away = sed.IsAwaySuspended;
        bool due = sed.IsDue;
        int totalSec = sed.TotalSeconds;
        int remainingSec = sed.RemainingSeconds;
        bool isPaused = sed.IsPaused;
        int satMinutes = sed.SatMinutes;
        string timeText = sed.TimeText;
        double progressPct = sed.ProgressPercent;

        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isSameDay = state.TryGetValue(KeyTodayDate, out string? savedDate) &&
                         string.Equals(savedDate, today, StringComparison.Ordinal);
        int stoodCount = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0 : 0;
        int longestSitMin = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryLongestMin) ?? 0 : 0;

        int dueCount = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryDueCount) ?? 0 : 0;
        string microAction = ResolveMicroAction(due ? Math.Max(0, dueCount - 1) : dueCount, culture);
        string modeLabel = T(culture, "CustomWidget.Pomodoro.Sedentary.ModeLabel");

        string headerPill = due
            ? T(culture, "CustomWidget.Pomodoro.Sedentary.HeaderDue")
            : away
                ? T(culture, "CustomWidget.Pomodoro.Sedentary.HeaderAway")
                : running
                    ? T(culture, "CustomWidget.Pomodoro.Sedentary.HeaderRunning")
                    : (isPaused ? T(culture, "CustomWidget.Pomodoro.Paused") : modeLabel);

        string dialSubtext = due
            ? microAction
            : away
                ? T(culture, "CustomWidget.Pomodoro.Sedentary.AwayHint")
                : running
                    ? (idleResetMin > 0
                        ? F(culture, "CustomWidget.Pomodoro.Sedentary.MonitoringIdleDetectHintFormat", satMinutes, idleResetMin)
                        : F(culture, "CustomWidget.Pomodoro.Sedentary.MonitoringHintFormat", satMinutes))
                    : (isPaused
                        ? T(culture, "CustomWidget.Pomodoro.Paused")
                        : T(culture, "CustomWidget.Pomodoro.Sedentary.Hint"));

        string phaseHint = due
            ? microAction
            : away
                ? T(culture, "CustomWidget.Pomodoro.Sedentary.AwayHint")
                : T(culture, "CustomWidget.Pomodoro.Sedentary.Hint");

        string primaryActionText = running
            ? T(culture, "CustomWidget.Pomodoro.ActionPause")
            : T(culture, "CustomWidget.Pomodoro.Sedentary.ActionStartGuardian");

        string capsuleTitle = due
            ? T(culture, "CustomWidget.Pomodoro.Sedentary.CapsuleDue")
            : F(culture, "CustomWidget.Pomodoro.Sedentary.CapsuleTitleFormat", satMinutes, intervalMin);
        string capsuleSummary = away
            ? T(culture, "CustomWidget.Pomodoro.Sedentary.HeaderAway")
            : running
                ? T(culture, "CustomWidget.Pomodoro.Sedentary.HeaderRunning")
                : (isPaused ? T(culture, "CustomWidget.Pomodoro.Paused") : modeLabel);
        string capsuleBadge = due
            ? T(culture, "CustomWidget.Pomodoro.Sedentary.CapsuleDue")
            : (stoodCount > 0
                ? F(culture, "CustomWidget.Pomodoro.Sedentary.StoodBadgeFormat", stoodCount)
                : string.Empty);

        return new CustomWidgetPomodoroSnapshot(
            Phase: PhaseSedentary,
            IsRunning: running,
            TotalSeconds: totalSec,
            RemainingSeconds: remainingSec,
            CycleInSet: pomo.CycleInSet,
            TodayCompleted: isSameDay ? ReadNonNegativeInt32(state, KeyTodayCompleted) ?? 0 : 0,
            TodayMinutes: isSameDay ? ReadNonNegativeInt32(state, KeyTodayMinutes) ?? 0 : 0,
            DailyGoal: prefs.DailyGoal,
            StreakDays: ReadNonNegativeInt32(state, KeyStreakDays) ?? 0,
            TodayDate: today,
            CurrentTask: state.TryGetValue(KeyCurrentTask, out string? taskRaw) ? taskRaw.Trim() : string.Empty,
            TimeText: timeText,
            ProgressPercent: progressPct,
            GoalPercent: 0.0,
            PhaseLabel: modeLabel,
            PhasePill: headerPill,
            PhaseHint: phaseHint,
            Glyph: due ? "\uE7BA" : "\uE7B3",
            CycleBadge: F(culture, "CustomWidget.Pomodoro.Sedentary.StoodBadgeFormat", stoodCount),
            PrimaryActionText: primaryActionText,
            CapsuleTitle: capsuleTitle,
            CapsuleSummary: capsuleSummary,
            CapsuleBadge: capsuleBadge,
            IsPaused: isPaused,
            HeaderPillText: headerPill,
            DialSubtext: dialSubtext,
            Mode: ModeSedentary,
            IsAwaySuspended: away,
            IsDue: due,
            SatMinutes: satMinutes,
            IntervalMin: intervalMin,
            StoodCount: stoodCount,
            LongestSitMin: longestSitMin,
            MicroAction: microAction,
            HealthSummary: F(culture, "CustomWidget.Pomodoro.Sedentary.HealthSummaryFormat", stoodCount, longestSitMin),
            ConfirmActionText: T(culture, "CustomWidget.Pomodoro.Sedentary.ConfirmBtn"),
            SnoozeActionText: T(culture, "CustomWidget.Pomodoro.Sedentary.SnoozeBtn"),
            SedentaryIsRunning: running,
            SedentaryIsDue: due,
            SedentaryIsAway: away,
            SedentaryProgressPercent: progressPct,
            SedentarySatMinutes: satMinutes,
            PomodoroIsRunning: pomo.IsRunning,
            PomodoroTimeText: pomo.TimeText);
    }

    /// <summary>
    /// Rotating micro-break guidance (stretch / 20-20-20 sight break / hydration), advanced by
    /// the reminder rotation index so consecutive reminders never repeat verbatim.
    /// </summary>
    public static string ResolveMicroAction(int rotationIndex, string culture)
    {
        int index = ((Math.Max(0, rotationIndex) % 3) + 3) % 3;
        return index switch
        {
            0 => LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.Sedentary.Micro0"),
            1 => LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.Sedentary.Micro1"),
            _ => LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.Sedentary.Micro2")
        };
    }

    /// <summary>
    /// Applies a <c>rhythmMode</c> view flip non-destructively: neither track is paused or reset
    /// when switching tabs; uninitialized target track defaults are seeded cleanly.
    /// </summary>
    public static void ApplyRhythmMode(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings)
    {
        if (IsSedentaryMode(settings))
        {
            int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
            if (!state.ContainsKey(KeySedentaryRunning))
            {
                state[KeySedentaryRunning] = "false";
            }
            if (!state.ContainsKey(KeySedentaryEndUtcTicks))
            {
                state[KeySedentaryEndUtcTicks] = "0";
            }
            if (!state.ContainsKey(KeySedentaryRemainingSec))
            {
                state[KeySedentaryRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            }
            if (!state.ContainsKey(KeySedentaryTotalSec))
            {
                state[KeySedentaryTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            string currentPhase = state.TryGetValue(KeyPhase, out string? p) ? NormalizePhase(p) : PhaseFocus;
            if (string.Equals(currentPhase, PhaseSedentary, StringComparison.Ordinal))
            {
                currentPhase = PhaseFocus;
            }
            state[KeyPhase] = currentPhase;
            int defaultSec = GetDefaultDurationSeconds(currentPhase, settings);
            if (!state.ContainsKey(KeyRunning))
            {
                state[KeyRunning] = "false";
            }
            if (!state.ContainsKey(KeyEndUtcTicks))
            {
                state[KeyEndUtcTicks] = "0";
            }
            if (!state.ContainsKey(KeyRemainingSec))
            {
                state[KeyRemainingSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
            }
            if (!state.ContainsKey(KeyTotalSec))
            {
                state[KeyTotalSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Dual-track heartbeat pump: advances both any running Pomodoro phase and any running
    /// background/foreground Sedentary guardian track simultaneously.
    /// </summary>
    public static bool TryPumpForMode(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        IDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset now,
        double? idleSeconds,
        out CustomWidgetPomodoroCompletionNotice? notice,
        string? cultureName = null)
    {
        bool pomoChanged = TryAdvanceExpiredPhase(
            state, settings, collections, now, out CustomWidgetPomodoroCompletionNotice? pomoNotice, cultureName);
        bool sedChanged = PumpSedentary(
            state, settings, now, idleSeconds, out CustomWidgetPomodoroCompletionNotice? sedNotice, cultureName);
        notice = pomoNotice ?? sedNotice;
        return pomoChanged || sedChanged;
    }

    /// <summary>
    /// Sedentary guardian state machine (runs independently in either view mode when active).
    /// </summary>
    public static bool PumpSedentary(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        double? idleSeconds,
        out CustomWidgetPomodoroCompletionNotice? notice,
        string? cultureName = null)
    {
        notice = null;
        EnsureTodayRollover(state, settings, now);
        if (!IsSedentaryRunning(AsReadOnly(state)))
        {
            return false;
        }

        (_, int intervalMin, int idleResetMin) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;
        bool away = ReadFlag(state, KeySedentaryAway);
        bool due = ReadFlag(state, KeySedentaryDue);

        int totalSec = ReadPositiveInt32(state, KeySedentaryTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now, KeySedentaryEndUtcTicks);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeySedentaryRemainingSec) ?? intervalSec;
        }

        if (idleResetMin > 0 && idleSeconds is double idle)
        {
            if (due && !away && idle >= idleResetMin * 60)
            {
                int stood = (ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0) + 1;
                state[KeySedentaryStoodCount] = stood.ToString(CultureInfo.InvariantCulture);
                RecordLongestSitSeconds(state, totalSec);
                state[KeySedentaryAway] = "1";
                state[KeySedentaryDue] = "0";
                state[KeySedentaryDueAtUtcTicks] = "0";
                state[KeySedentaryEndUtcTicks] = "0";
                state[KeySedentaryRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                state[KeySedentaryTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!away && !due && idle >= idleResetMin * 60)
            {
                RecordLongestSitSeconds(state, totalSec - remainingSec);
                state[KeySedentaryAway] = "1";
                state[KeySedentaryEndUtcTicks] = "0";
                state[KeySedentaryRemainingSec] = remainingSec.ToString(CultureInfo.InvariantCulture);
                state[KeySedentaryTotalSec] = totalSec.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (away && idle <= AwayReturnIdleSeconds)
            {
                StartSedentaryRound(state, now, intervalSec);
                return true;
            }
        }

        if (due)
        {
            return false;
        }

        if (!away && remainingSec <= 0)
        {
            RecordLongestSitSeconds(state, totalSec);
            state[KeySedentaryDue] = "1";
            state[KeySedentaryDueAtUtcTicks] = now.UtcTicks.ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryDueCount] = ((ReadNonNegativeInt32(state, KeySedentaryDueCount) ?? 0) + 1)
                .ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryEndUtcTicks] = "0";
            state[KeySedentaryRemainingSec] = "0";
            state[KeySedentaryTotalSec] = totalSec.ToString(CultureInfo.InvariantCulture);

            string culture = string.IsNullOrWhiteSpace(cultureName)
                ? LocalizationService.ResolveAmbientCultureName()
                : cultureName;
            string micro = ResolveMicroAction((ReadNonNegativeInt32(state, KeySedentaryDueCount) ?? 1) - 1, culture);
            int stoodCount = ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0;
            notice = new CustomWidgetPomodoroCompletionNotice(
                PhaseSedentary,
                PhaseSedentary,
                LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.Sedentary.NoticeTitle"),
                LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Pomodoro.Sedentary.NoticeFormat",
                    totalSec / 60,
                    micro),
                ReadPreferences(settings).NotifyMode,
                stoodCount);
            return true;
        }

        return false;
    }

    /// <summary>Credits the stand-up and starts a fresh sedentary round immediately (works in any view mode).</summary>
    public static void ConfirmStoodUp(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        EnsureTodayRollover(state, settings, now);
        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        if (IsSedentaryRunning(AsReadOnly(state)))
        {
            int totalSec = ReadPositiveInt32(state, KeySedentaryTotalSec) ?? intervalSec;
            int remainingSec = ResolveLiveRemainingSeconds(state, now, KeySedentaryEndUtcTicks);
            if (remainingSec < 0)
            {
                remainingSec = ReadNonNegativeInt32(state, KeySedentaryRemainingSec) ?? intervalSec;
            }

            int stood = (ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0) + 1;
            state[KeySedentaryStoodCount] = stood.ToString(CultureInfo.InvariantCulture);
            RecordLongestSitSeconds(state, ReadFlag(state, KeySedentaryDue) ? totalSec : totalSec - remainingSec);
        }

        StartSedentaryRound(state, now, intervalSec);
    }

    /// <summary>Pushes the next sedentary reminder out and resumes monitoring (works in any view mode).</summary>
    public static void SnoozeSedentaryReminder(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        int extraSeconds,
        DateTimeOffset now)
    {
        if (extraSeconds <= 0)
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        int totalSec = ReadPositiveInt32(state, KeySedentaryTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now, KeySedentaryEndUtcTicks);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeySedentaryRemainingSec) ?? intervalSec;
        }

        bool wasDue = ReadFlag(state, KeySedentaryDue);
        int newRemaining = Math.Clamp((wasDue ? 0 : remainingSec) + extraSeconds, 1, 180 * 60);
        int newTotal = Math.Max(totalSec, newRemaining);

        state[KeySedentaryRunning] = "true";
        state[KeySedentaryEndUtcTicks] = now.AddSeconds(newRemaining).UtcTicks.ToString(CultureInfo.InvariantCulture);
        state[KeySedentaryRemainingSec] = newRemaining.ToString(CultureInfo.InvariantCulture);
        state[KeySedentaryTotalSec] = newTotal.ToString(CultureInfo.InvariantCulture);
        ClearSedentaryTransientFlags(state);
    }

    private static void StartSedentaryRound(
        IDictionary<string, string> state,
        DateTimeOffset now,
        int intervalSec)
    {
        state[KeySedentaryRunning] = "true";
        state[KeySedentaryEndUtcTicks] = now.AddSeconds(intervalSec).UtcTicks.ToString(CultureInfo.InvariantCulture);
        state[KeySedentaryRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
        state[KeySedentaryTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
        ClearSedentaryTransientFlags(state);
    }

    private static void ClearSedentaryTransientFlags(IDictionary<string, string> state)
    {
        state[KeySedentaryAway] = "0";
        state[KeySedentaryDue] = "0";
        state[KeySedentaryDueAtUtcTicks] = "0";
    }

    private static void RecordLongestSitSeconds(IDictionary<string, string> state, int satSeconds)
    {
        if (satSeconds <= 0)
        {
            return;
        }

        int satMinutes = satSeconds / 60;
        int longest = ReadNonNegativeInt32(state, KeySedentaryLongestMin) ?? 0;
        if (satMinutes > longest)
        {
            state[KeySedentaryLongestMin] = satMinutes.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Live remaining seconds from the absolute UTC deadline, or -1 when not deadline-backed.</summary>
    private static int ResolveLiveRemainingSeconds(
        IEnumerable<KeyValuePair<string, string>> state,
        DateTimeOffset now,
        string deadlineKey = KeyEndUtcTicks)
    {
        foreach ((string k, string? v) in state)
        {
            if (string.Equals(k, deadlineKey, StringComparison.Ordinal) &&
                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long endTicks) &&
                endTicks > 0)
            {
                double diffSec = TimeSpan.FromTicks(endTicks - now.UtcTicks).TotalSeconds;
                return Math.Max(0, (int)Math.Ceiling(diffSec));
            }
        }

        return -1;
    }

    private static bool ReadFlag(IEnumerable<KeyValuePair<string, string>> state, string key) =>
        state.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.Ordinal)) is { } found &&
        string.Equals(found.Value, "1", StringComparison.Ordinal);

    private static int? ReadPositiveInt32(IEnumerable<KeyValuePair<string, string>> state, string key)
    {
        foreach ((string k, string? v) in state)
        {
            if (string.Equals(k, key, StringComparison.Ordinal) &&
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) &&
                val > 0)
            {
                return val;
            }
        }

        return null;
    }

    private static int? ReadNonNegativeInt32(IEnumerable<KeyValuePair<string, string>> state, string key)
    {
        foreach ((string k, string? v) in state)
        {
            if (string.Equals(k, key, StringComparison.Ordinal) &&
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) &&
                val >= 0)
            {
                return val;
            }
        }

        return null;
    }

    public static void ToggleStartPause(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        EnsureTodayRollover(state, settings, now);

        if (IsSedentaryMode(settings))
        {
            if (ReadFlag(state, KeySedentaryDue))
            {
                if (IsSedentaryRunning(AsReadOnly(state)))
                {
                    ConfirmStoodUp(state, settings, now);
                }
                return;
            }

            SedentaryTrackProjection sed = ComputeSedentaryTrackProjection(AsReadOnly(state), settings, now);
            if (sed.IsRunning)
            {
                state[KeySedentaryRunning] = "false";
                state[KeySedentaryEndUtcTicks] = "0";
                state[KeySedentaryRemainingSec] = sed.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
                state[KeySedentaryTotalSec] = sed.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
                int rem = sed.RemainingSeconds > 0 ? sed.RemainingSeconds : intervalSec;
                int tot = Math.Max(sed.TotalSeconds, rem);
                state[KeySedentaryRunning] = "true";
                state[KeySedentaryEndUtcTicks] = now.AddSeconds(rem).UtcTicks.ToString(CultureInfo.InvariantCulture);
                state[KeySedentaryRemainingSec] = rem.ToString(CultureInfo.InvariantCulture);
                state[KeySedentaryTotalSec] = tot.ToString(CultureInfo.InvariantCulture);
            }
            return;
        }

        PomodoroTrackProjection pomo = ComputePomodoroTrackProjection(AsReadOnly(state), settings, now);
        if (pomo.IsRunning)
        {
            state[KeyRunning] = "false";
            state[KeyEndUtcTicks] = "0";
            state[KeyRemainingSec] = pomo.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
            state[KeyTotalSec] = pomo.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            int rem = pomo.RemainingSeconds > 0
                ? pomo.RemainingSeconds
                : GetDefaultDurationSeconds(pomo.Phase, settings);
            int tot = Math.Max(pomo.TotalSeconds, rem);
            long endTicks = now.AddSeconds(rem).UtcTicks;

            state[KeyPhase] = pomo.Phase;
            state[KeyRunning] = "true";
            state[KeyEndUtcTicks] = endTicks.ToString(CultureInfo.InvariantCulture);
            state[KeyRemainingSec] = rem.ToString(CultureInfo.InvariantCulture);
            state[KeyTotalSec] = tot.ToString(CultureInfo.InvariantCulture);

            if (IsBackgroundSedentaryEnabled(settings) &&
                !IsSedentaryRunning(AsReadOnly(state)) &&
                !ReadFlag(state, KeySedentaryDue))
            {
                int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
                StartSedentaryRound(state, now, intervalSec);
            }
        }
    }

    public static void ResetCurrentPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        EnsureTodayRollover(state, settings, now);

        if (IsSedentaryMode(settings))
        {
            int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
            state[KeySedentaryRunning] = "false";
            state[KeySedentaryEndUtcTicks] = "0";
            state[KeySedentaryRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            ClearSedentaryTransientFlags(state);
            return;
        }

        string rawPhase = state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus;
        string phase = string.Equals(rawPhase, PhaseSedentary, StringComparison.OrdinalIgnoreCase)
            ? PhaseFocus
            : NormalizePhase(rawPhase);
        int defaultSec = GetDefaultDurationSeconds(phase, settings);

        state[KeyPhase] = phase;
        state[KeyRunning] = "false";
        state[KeyEndUtcTicks] = "0";
        state[KeyRemainingSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
    }

    public static void ExtendSeconds(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        int extraSeconds,
        DateTimeOffset now)
    {
        if (extraSeconds <= 0)
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);

        if (IsSedentaryMode(settings))
        {
            SedentaryTrackProjection sed = ComputeSedentaryTrackProjection(AsReadOnly(state), settings, now);
            int newRem = Math.Clamp(sed.RemainingSeconds + extraSeconds, 1, 180 * 60);
            int newTot = Math.Clamp(sed.TotalSeconds + extraSeconds, newRem, 180 * 60);

            state[KeySedentaryRemainingSec] = newRem.ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryTotalSec] = newTot.ToString(CultureInfo.InvariantCulture);
            if (sed.IsRunning)
            {
                state[KeySedentaryEndUtcTicks] = now.AddSeconds(newRem).UtcTicks.ToString(CultureInfo.InvariantCulture);
            }
            return;
        }

        PomodoroTrackProjection pomo = ComputePomodoroTrackProjection(AsReadOnly(state), settings, now);
        int nextRem = Math.Clamp(pomo.RemainingSeconds + extraSeconds, 1, 180 * 60);
        int nextTot = Math.Clamp(pomo.TotalSeconds + extraSeconds, nextRem, 180 * 60);

        state[KeyRemainingSec] = nextRem.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = nextTot.ToString(CultureInfo.InvariantCulture);
        if (pomo.IsRunning)
        {
            state[KeyEndUtcTicks] = now.AddSeconds(nextRem).UtcTicks.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static void SetPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string targetPhase,
        DateTimeOffset now)
    {
        if (IsSedentaryMode(settings))
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        string normalized = NormalizePhase(targetPhase);
        if (string.Equals(normalized, PhaseSedentary, StringComparison.Ordinal))
        {
            normalized = PhaseFocus;
        }
        int defaultSec = GetDefaultDurationSeconds(normalized, settings);

        state[KeyPhase] = normalized;
        state[KeyRunning] = "false";
        state[KeyEndUtcTicks] = "0";
        state[KeyRemainingSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
    }

    public static void CyclePhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        if (IsSedentaryMode(settings))
        {
            return;
        }

        string current = NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus);
        string next = current switch
        {
            PhaseFocus => PhaseShortBreak,
            PhaseShortBreak => PhaseLongBreak,
            _ => PhaseFocus
        };
        SetPhase(state, settings, next, now);
    }

    public static void SkipPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        if (IsSedentaryMode(settings))
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        PomodoroTrackProjection pomo = ComputePomodoroTrackProjection(AsReadOnly(state), settings, now);
        TransitionToNextPhase(state, settings, pomo.Phase, pomo.CycleInSet, autoStartAllowed: false, now);
    }

    public static void SyncIdleDurationWithSettings(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string changedSettingKey)
    {
        if (string.Equals(changedSettingKey, SettingRhythmMode, StringComparison.OrdinalIgnoreCase))
        {
            ApplyRhythmMode(state, settings);
            return;
        }

        IReadOnlyDictionary<string, string> roState = AsReadOnly(state);

        if (string.Equals(changedSettingKey, SettingSedentaryIntervalMin, StringComparison.OrdinalIgnoreCase))
        {
            if (IsSedentaryRunning(roState))
            {
                return;
            }

            if (roState.TryGetValue(KeySedentaryRemainingSec, out string? sRemRaw) &&
                int.TryParse(sRemRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sRem) &&
                roState.TryGetValue(KeySedentaryTotalSec, out string? sTotRaw) &&
                int.TryParse(sTotRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sTot) &&
                sRem > 0 &&
                sTot > 0 &&
                sRem < sTot)
            {
                return;
            }

            int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
            state[KeySedentaryRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
            state[KeySedentaryEndUtcTicks] = "0";
            return;
        }

        if (IsPomodoroRunning(roState))
        {
            return;
        }

        if (roState.TryGetValue(KeyRemainingSec, out string? remRaw) &&
            int.TryParse(remRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedRem) &&
            roState.TryGetValue(KeyTotalSec, out string? totRaw) &&
            int.TryParse(totRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTot) &&
            parsedRem > 0 &&
            parsedTot > 0 &&
            parsedRem < parsedTot)
        {
            return;
        }

        string phase = NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus);
        if (string.Equals(phase, PhaseSedentary, StringComparison.Ordinal))
        {
            phase = PhaseFocus;
        }

        bool affectsPomodoroPhase =
            (phase == PhaseFocus && string.Equals(changedSettingKey, "focusMinutes", StringComparison.OrdinalIgnoreCase)) ||
            (phase != PhaseFocus &&
                (string.Equals(changedSettingKey, "breakPreset", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(changedSettingKey, "shortBreakMinutes", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(changedSettingKey, "longBreakMinutes", StringComparison.OrdinalIgnoreCase)));

        if (!affectsPomodoroPhase)
        {
            return;
        }

        int newDefaultSec = GetDefaultDurationSeconds(phase, settings);
        state[KeyRemainingSec] = newDefaultSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = newDefaultSec.ToString(CultureInfo.InvariantCulture);
        state[KeyEndUtcTicks] = "0";
    }

    public static bool TryAdvanceExpiredPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        IDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset now,
        out CustomWidgetPomodoroCompletionNotice? notice,
        string? cultureName = null)
    {
        notice = null;
        if (!IsPomodoroRunning(AsReadOnly(state)))
        {
            return false;
        }

        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        IReadOnlyDictionary<string, string> roState = AsReadOnly(state);
        PomodoroTrackProjection pomo = ComputePomodoroTrackProjection(roState, settings, now);
        if (pomo.RemainingSeconds > 0)
        {
            return false;
        }

        EnsureTodayRollover(state, settings, now);
        var prefs = ReadPreferences(settings);
        string completedPhase = pomo.Phase;

        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isSameDay = state.TryGetValue(KeyTodayDate, out string? savedDate) &&
                         string.Equals(savedDate, today, StringComparison.Ordinal);
        int todayCompleted = isSameDay ? ReadNonNegativeInt32(roState, KeyTodayCompleted) ?? 0 : 0;
        int todayMinutes = isSameDay ? ReadNonNegativeInt32(roState, KeyTodayMinutes) ?? 0 : 0;
        int streakDays = ReadNonNegativeInt32(roState, KeyStreakDays) ?? 0;
        string currentTask = state.TryGetValue(KeyCurrentTask, out string? taskRaw) && !string.IsNullOrWhiteSpace(taskRaw)
            ? taskRaw.Trim()
            : (state.TryGetValue(KeyLinkedTaskTitle, out string? linkedRaw) ? linkedRaw.Trim() : string.Empty);

        if (completedPhase == PhaseFocus)
        {
            int focusedMinutes = Math.Max(1, (int)Math.Round(pomo.TotalSeconds / 60.0));
            int nextCompleted = todayCompleted + 1;
            int nextMinutes = todayMinutes + focusedMinutes;

            state[KeyTodayCompleted] = nextCompleted.ToString(CultureInfo.InvariantCulture);
            state[KeyTodayMinutes] = nextMinutes.ToString(CultureInfo.InvariantCulture);

            string lastHit = state.TryGetValue(KeyLastGoalHitDate, out string? lh) ? lh : string.Empty;
            if (nextCompleted >= prefs.DailyGoal && !string.Equals(lastHit, today, StringComparison.Ordinal))
            {
                int nextStreak = streakDays + 1;
                state[KeyStreakDays] = nextStreak.ToString(CultureInfo.InvariantCulture);
                state[KeyLastGoalHitDate] = today;
            }

            if (!collections.TryGetValue("sessions", out List<CustomWidgetRecord>? sessionList))
            {
                sessionList = [];
                collections["sessions"] = sessionList;
            }

            string taskLabel = !string.IsNullOrWhiteSpace(currentTask)
                ? currentTask
                : LocalizationService.FormatForCulture(culture, "CustomWidget.Pomodoro.DefaultSessionTaskFormat", nextCompleted);

            var record = new CustomWidgetRecord
            {
                CreatedAtUtcTicks = now.UtcTicks
            };
            record.Fields["task"] = taskLabel;
            record.Fields["text"] = taskLabel;
            record.Fields["duration"] = $"{focusedMinutes}m";
            record.Fields["tag"] = $"{focusedMinutes}m";
            record.Fields["cycle"] = $"#{pomo.CycleInSet}/4";
            record.Fields["date"] = today;
            sessionList.Insert(0, record);
            while (sessionList.Count > 100)
            {
                sessionList.RemoveAt(sessionList.Count - 1);
            }

            bool autoStartBreak = prefs.FlowMode is "semi" or "auto";
            string nextPhase = TransitionToNextPhase(
                state,
                settings,
                completedPhase,
                pomo.CycleInSet,
                autoStartBreak,
                now);

            int breakMin = nextPhase == PhaseLongBreak ? prefs.LongBreakMin : prefs.ShortBreakMin;
            string breakName = LocalizationService.TranslateForCulture(
                culture,
                nextPhase == PhaseLongBreak
                    ? "CustomWidget.Pomodoro.PhaseLongFullLabel"
                    : "CustomWidget.Pomodoro.PhaseShortFullLabel");
            string title = LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Pomodoro.NoticeFocusDoneTitleFormat",
                nextCompleted);
            string message = LocalizationService.FormatForCulture(
                culture,
                autoStartBreak
                    ? "CustomWidget.Pomodoro.NoticeFocusDoneAutoBreakFormat"
                    : "CustomWidget.Pomodoro.NoticeFocusDoneManualBreakFormat",
                taskLabel,
                focusedMinutes,
                breakMin,
                breakName);

            notice = new CustomWidgetPomodoroCompletionNotice(
                completedPhase,
                nextPhase,
                title,
                message,
                prefs.NotifyMode,
                nextCompleted);
            return true;
        }
        else
        {
            bool autoStartFocus = string.Equals(prefs.FlowMode, "auto", StringComparison.OrdinalIgnoreCase);
            string nextPhase = TransitionToNextPhase(
                state,
                settings,
                completedPhase,
                pomo.CycleInSet,
                autoStartFocus,
                now);

            int nextCycle = state.TryGetValue(KeyCycle, out string? cStr) &&
                            int.TryParse(cStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cVal)
                ? cVal
                : 1;

            string title = LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.NoticeBreakDoneTitle");
            string message = LocalizationService.FormatForCulture(
                culture,
                autoStartFocus
                    ? "CustomWidget.Pomodoro.NoticeBreakDoneAutoFocusFormat"
                    : "CustomWidget.Pomodoro.NoticeBreakDoneManualFocusFormat",
                nextCycle,
                prefs.FocusMin);

            notice = new CustomWidgetPomodoroCompletionNotice(
                completedPhase,
                nextPhase,
                title,
                message,
                prefs.NotifyMode,
                todayCompleted);
            return true;
        }
    }

    public static string ResolveToken(
        string subPath,
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        string? cultureName = null) =>
        ResolveToken(subPath, ComputeSnapshot(state, settings, now, cultureName), cultureName);

    public static string ResolveToken(
        string subPath,
        CustomWidgetPomodoroSnapshot s,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        switch (subPath)
        {
            case "tipX":
            {
                double rad = ((s.ProgressPercent * 3.6) - 90.0) * (Math.PI / 180.0);
                return (78.0 + (68.0 * Math.Cos(rad))).ToString("0.0", CultureInfo.InvariantCulture);
            }
            case "tipY":
            {
                double rad = ((s.ProgressPercent * 3.6) - 90.0) * (Math.PI / 180.0);
                return (78.0 + (68.0 * Math.Sin(rad))).ToString("0.0", CultureInfo.InvariantCulture);
            }
        }

        return subPath switch
        {
            "phase" => s.Phase,
            "isRunning" => s.IsRunning ? "true" : "false",
            "isPaused" => s.IsPaused ? "true" : "false",
            "totalSeconds" => s.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            "remainingSeconds" => s.RemainingSeconds.ToString(CultureInfo.InvariantCulture),
            "timeText" or "remainingText" or "remaining" or "time" => s.TimeText,
            "progressPercent" => s.ProgressPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "cycle" => s.CycleInSet.ToString(CultureInfo.InvariantCulture),
            "cycleBadge" => s.CycleBadge,
            "todayCompleted" or "completedToday" => s.TodayCompleted.ToString(CultureInfo.InvariantCulture),
            "todayMinutes" => s.TodayMinutes.ToString(CultureInfo.InvariantCulture),
            "todayDurationText" => FormatDurationText(s.TodayMinutes, culture),
            "dailyGoal" => s.DailyGoal.ToString(CultureInfo.InvariantCulture),
            "goalPercent" => s.GoalPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "goalSummary" => LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Pomodoro.GoalSummaryFormat",
                s.TodayMinutes,
                Math.Round(s.GoalPercent).ToString("0", CultureInfo.InvariantCulture)),
            "streakDays" => s.StreakDays.ToString(CultureInfo.InvariantCulture),
            "streakBadge" => s.StreakDays > 0
                ? LocalizationService.FormatForCulture(culture, "CustomWidget.Pomodoro.StreakBadgeFormat", s.StreakDays)
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.Pomodoro.DailyGoalLabel"),
            "todayDate" => s.TodayDate,
            "phaseLabel" => s.PhaseLabel,
            "phasePill" => s.PhasePill,
            "headerPill" => s.HeaderPillText,
            "dialSubtext" => s.DialSubtext,
            "phaseHint" => s.PhaseHint,
            "glyph" => s.Glyph,
            "primaryActionText" => s.PrimaryActionText,
            "capsuleTitle" => s.CapsuleTitle,
            "capsuleSummary" => s.CapsuleSummary,
            "capsuleBadge" => s.CapsuleBadge,
            // Dual-track tokens.
            "mode" or "rhythmMode" => s.Mode,
            "isAway" or "isAwaySuspended" => s.IsAwaySuspended ? "true" : "false",
            "isDue" => s.IsDue ? "true" : "false",
            "satMinutes" or "satMinutesText" => s.SatMinutes.ToString(CultureInfo.InvariantCulture),
            "intervalMin" or "sedentaryIntervalMin" => s.IntervalMin.ToString(CultureInfo.InvariantCulture),
            "stoodCount" => s.StoodCount.ToString(CultureInfo.InvariantCulture),
            "longestSitMin" => s.LongestSitMin.ToString(CultureInfo.InvariantCulture),
            "microAction" => s.MicroAction,
            "healthSummary" => s.HealthSummary,
            "confirmActionText" => s.ConfirmActionText,
            "snoozeActionText" => s.SnoozeActionText,
            "sedentaryIsRunning" => s.SedentaryIsRunning ? "true" : "false",
            "sedentaryIsDue" => s.SedentaryIsDue ? "true" : "false",
            "sedentaryIsAway" => s.SedentaryIsAway ? "true" : "false",
            "sedentaryProgressPercent" => s.SedentaryProgressPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "sedentarySatMinutes" => s.SedentarySatMinutes.ToString(CultureInfo.InvariantCulture),
            "pomoIsRunning" => s.PomodoroIsRunning ? "true" : "false",
            "pomoTimeText" => s.PomodoroTimeText,
            _ => string.Empty
        };
    }

    internal static string FormatDurationText(int totalMinutes, string? cultureName)
    {
        int mins = Math.Max(0, totalMinutes);
        int h = mins / 60;
        int m = mins % 60;
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName.Trim();

        if (c.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool isTw = LocalizationService.IsTraditionalChineseCulture(c);
            string hUnit = isTw ? "小時" : "小时";
            string mUnit = isTw ? "分鐘" : "分钟";
            return h > 0 ? $"{h} {hUnit} {m} {mUnit}" : $"{m} {mUnit}";
        }

        string lang = c.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "en";
        return lang switch
        {
            "ja" => h > 0 ? $"{h}時間{m}分" : $"{m}分",
            "ko" => h > 0 ? $"{h}시간 {m}분" : $"{m}분",
            "de" => h > 0 ? $"{h} Std. {m} Min." : $"{m} Min.",
            "fr" or "es" or "pt" => h > 0 ? $"{h} h {m} min" : $"{m} min",
            "ru" => h > 0 ? $"{h} ч {m} мин" : $"{m} мин",
            "hi" => h > 0 ? $"{h} घं {m} मि" : $"{m} मि",
            "ar" => h > 0 ? $"{h} س {m} د" : $"{m} د",
            "bn" => h > 0 ? $"{h} ঘ {m} মি" : $"{m} মি",
            _ => h > 0 ? $"{h}h {m}m" : $"{m}m"
        };
    }

    public static string NormalizePhase(string? raw)
    {
        if (string.Equals(raw, PhaseSedentary, StringComparison.OrdinalIgnoreCase))
        {
            return PhaseSedentary;
        }

        if (string.Equals(raw, PhaseShortBreak, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "short", StringComparison.OrdinalIgnoreCase))
        {
            return PhaseShortBreak;
        }

        if (string.Equals(raw, PhaseLongBreak, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "long", StringComparison.OrdinalIgnoreCase))
        {
            return PhaseLongBreak;
        }

        return PhaseFocus;
    }

    public static string FormatMinutesSeconds(int totalSeconds)
    {
        int clamped = Math.Max(0, totalSeconds);
        int mins = clamped / 60;
        int secs = clamped % 60;
        return $"{mins:D2}:{secs:D2}";
    }

    private static string TransitionToNextPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string fromPhase,
        int currentCycle,
        bool autoStartAllowed,
        DateTimeOffset now)
    {
        string nextPhase;
        int nextCycle = currentCycle;

        if (fromPhase == PhaseFocus)
        {
            nextPhase = currentCycle >= 4 ? PhaseLongBreak : PhaseShortBreak;
        }
        else
        {
            nextPhase = PhaseFocus;
            nextCycle = fromPhase == PhaseLongBreak ? 1 : Math.Min(4, currentCycle + 1);
        }

        int nextDurationSec = GetDefaultDurationSeconds(nextPhase, settings);
        state[KeyPhase] = nextPhase;
        state[KeyCycle] = nextCycle.ToString(CultureInfo.InvariantCulture);
        state[KeyRemainingSec] = nextDurationSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = nextDurationSec.ToString(CultureInfo.InvariantCulture);

        if (autoStartAllowed)
        {
            state[KeyRunning] = "true";
            state[KeyEndUtcTicks] = now.AddSeconds(nextDurationSec).UtcTicks.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            state[KeyRunning] = "false";
            state[KeyEndUtcTicks] = "0";
        }

        return nextPhase;
    }

    private static int ParseClampedInt(
        IReadOnlyDictionary<string, string> dict,
        string key,
        int fallback,
        int min,
        int max)
    {
        if (dict.TryGetValue(key, out string? raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            return Math.Clamp(val, min, max);
        }

        return fallback;
    }

    private static IReadOnlyDictionary<string, string> AsReadOnly(IDictionary<string, string> dict) =>
        dict is IReadOnlyDictionary<string, string> ro
            ? ro
            : new Dictionary<string, string>(dict, StringComparer.Ordinal);
}
