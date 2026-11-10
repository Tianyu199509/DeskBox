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
    // v5.0 dual-rhythm (Pomodoro focus / sedentary guardian) extension fields.
    // All defaults keep the historical pomodoro-only behavior for legacy stores.
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
    string SnoozeActionText = "");

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

    // v5.0 sedentary guardian keys (all optional; legacy stores simply lack them).
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

    public static bool IsRunning(IReadOnlyDictionary<string, string> state) =>
        state.TryGetValue(KeyRunning, out string? v) &&
        string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

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
    /// Reads the v5.0 dual-rhythm preferences. Every value is optional and defaults to the
    /// exact pre-v5 behavior (<c>pomodoro</c> mode), so legacy <c>store.json</c> payloads
    /// keep working untouched.
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

    public static CustomWidgetPomodoroSnapshot ComputeSnapshot(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        if (IsSedentaryMode(settings))
        {
            return ComputeSedentarySnapshot(state, settings, now, culture);
        }

        static string T(string c, string key) => LocalizationService.TranslateForCulture(c, key);
        static string F(string c, string key, params object[] args) => LocalizationService.FormatForCulture(c, key, args);

        var prefs = ReadPreferences(settings);
        string phase = NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus);
        bool running = IsRunning(state);
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

        string currentTask = state.TryGetValue(KeyCurrentTask, out string? taskRaw)
            ? taskRaw.Trim()
            : string.Empty;

        string timeText = FormatMinutesSeconds(remainingSec);
        double progressPct = totalSec > 0
            ? Math.Clamp((1.0 - (double)remainingSec / totalSec) * 100.0, 0.0, 100.0)
            : 0.0;
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
        bool isPaused = !running && remainingSec > 0 && remainingSec < totalSec;

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
        string capsuleTitle = $"{timeText} · {capsuleContext}";
        string capsuleSummary = phase == PhaseFocus
            ? F(culture, "CustomWidget.Pomodoro.CapsuleSummaryFormat", cycle, todayMinutes)
            : phaseHint;
        string capsuleBadge = running
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
            DialSubtext: dialSubtext);
    }

    /// <summary>
    /// Sedentary ("🪑 久坐护眼守护") snapshot: same dial contract as the pomodoro phases, but
    /// the ring tracks continuous desk time toward the next stand-up reminder and the subtext
    /// reflects the away-suspended / due micro states.
    /// </summary>
    private static CustomWidgetPomodoroSnapshot ComputeSedentarySnapshot(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now,
        string culture)
    {
        static string T(string c, string key) => LocalizationService.TranslateForCulture(c, key);
        static string F(string c, string key, params object[] args) => LocalizationService.FormatForCulture(c, key, args);

        var prefs = ReadPreferences(settings);
        (_, int intervalMin, int idleResetMin) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        bool running = IsRunning(state);
        bool away = ReadFlag(state, KeySedentaryAway);
        bool due = ReadFlag(state, KeySedentaryDue);

        int totalSec = ReadPositiveInt32(state, KeyTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeyRemainingSec) ?? intervalSec;
        }

        if (due)
        {
            remainingSec = 0;
        }

        if (totalSec < remainingSec)
        {
            totalSec = remainingSec;
        }

        string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool isSameDay = state.TryGetValue(KeyTodayDate, out string? savedDate) &&
                         string.Equals(savedDate, today, StringComparison.Ordinal);
        int stoodCount = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0 : 0;
        int longestSitMin = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryLongestMin) ?? 0 : 0;

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

        int dueCount = isSameDay ? ReadNonNegativeInt32(state, KeySedentaryDueCount) ?? 0 : 0;
        // While due, keep showing the micro-break tip that was fired with THIS reminder;
        // otherwise preview the next tip in the rotation.
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
            CycleInSet: 1,
            TodayCompleted: isSameDay ? ReadNonNegativeInt32(state, KeyTodayCompleted) ?? 0 : 0,
            TodayMinutes: isSameDay ? ReadNonNegativeInt32(state, KeyTodayMinutes) ?? 0 : 0,
            DailyGoal: prefs.DailyGoal,
            StreakDays: ReadNonNegativeInt32(state, KeyStreakDays) ?? 0,
            TodayDate: today,
            CurrentTask: string.Empty,
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
            SnoozeActionText: T(culture, "CustomWidget.Pomodoro.Sedentary.SnoozeBtn"));
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
    /// Applies a <c>rhythmMode</c> settings flip. Switching modes parks the other mode's
    /// countdown cleanly; daily sedentary health stats (stood count / longest sit) survive.
    /// </summary>
    public static void ApplyRhythmMode(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings)
    {
        if (IsSedentaryMode(settings))
        {
            if (!string.Equals(NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus), PhaseSedentary, StringComparison.Ordinal))
            {
                int intervalSec = ReadRhythmPreferences(settings).SedentaryIntervalMin * 60;
                state[KeyPhase] = PhaseSedentary;
                state[KeyRunning] = "false";
                state[KeyEndUtcTicks] = "0";
                state[KeyRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                state[KeyTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                ClearSedentaryTransientFlags(state);
            }
        }
        else if (string.Equals(NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus), PhaseSedentary, StringComparison.Ordinal))
        {
            int focusSec = GetDefaultDurationSeconds(PhaseFocus, settings);
            state[KeyPhase] = PhaseFocus;
            state[KeyRunning] = "false";
            state[KeyEndUtcTicks] = "0";
            state[KeyRemainingSec] = focusSec.ToString(CultureInfo.InvariantCulture);
            state[KeyTotalSec] = focusSec.ToString(CultureInfo.InvariantCulture);
            ClearSedentaryTransientFlags(state);
        }
    }

    /// <summary>
    /// Heartbeat pump that routes by rhythm mode. Pomodoro mode keeps the historical
    /// <see cref="TryAdvanceExpiredPhase"/> behavior; sedentary mode runs the away-suspend →
    /// return-reset → due-reminder state machine. <paramref name="idleSeconds"/> is the
    /// keyboard/mouse idle duration (typically from <c>user32.dll!GetLastInputInfo</c>);
    /// pass <c>null</c> when idle detection is unavailable.
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
        if (IsSedentaryMode(settings))
        {
            return PumpSedentary(state, settings, now, idleSeconds, out notice, cultureName);
        }

        return TryAdvanceExpiredPhase(state, settings, collections, now, out notice, cultureName);
    }

    /// <summary>
    /// Sedentary guardian state machine. Transitions, in priority order:
    /// 1. Monitoring + idle ≥ threshold → freeze the countdown ("away-suspended").
    /// 2. Away-suspended + fresh input → anchor a brand-new round at the return moment.
    /// 3. Due + idle ≥ threshold → the user obeyed the reminder and left: auto-confirm the
    ///    stand-up and suspend (no repeated nags while they are walking).
    /// 4. Monitoring + countdown expired (and user still active) → fire the stand-up
    ///    reminder once with rotating micro-break guidance.
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
        if (!IsSedentaryMode(settings))
        {
            return false;
        }

        EnsureTodayRollover(state, settings, now);
        if (!IsRunning(AsReadOnly(state)))
        {
            return false;
        }

        (_, int intervalMin, int idleResetMin) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;
        bool away = ReadFlag(state, KeySedentaryAway);
        bool due = ReadFlag(state, KeySedentaryDue);

        int totalSec = ReadPositiveInt32(state, KeyTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeyRemainingSec) ?? intervalSec;
        }

        if (idleResetMin > 0 && idleSeconds is double idle)
        {
            if (due && !away && idle >= idleResetMin * 60)
            {
                // The user obeyed the reminder and left: credit the stand-up, clear the
                // reminder, and wait for their return — no nagging while they walk.
                int stood = (ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0) + 1;
                state[KeySedentaryStoodCount] = stood.ToString(CultureInfo.InvariantCulture);
                RecordLongestSitSeconds(state, totalSec);
                state[KeySedentaryAway] = "1";
                state[KeySedentaryDue] = "0";
                state[KeySedentaryDueAtUtcTicks] = "0";
                state[KeyEndUtcTicks] = "0";
                state[KeyRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                state[KeyTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!away && !due && idle >= idleResetMin * 60)
            {
                // User left the desk long enough to count as real rest: freeze the round.
                RecordLongestSitSeconds(state, totalSec - remainingSec);
                state[KeySedentaryAway] = "1";
                state[KeyEndUtcTicks] = "0";
                state[KeyRemainingSec] = remainingSec.ToString(CultureInfo.InvariantCulture);
                state[KeyTotalSec] = totalSec.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (away && idle <= AwayReturnIdleSeconds)
            {
                // Back at the desk: fresh round anchored at this very moment.
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
            state[KeyEndUtcTicks] = "0";
            state[KeyRemainingSec] = "0";
            state[KeyTotalSec] = totalSec.ToString(CultureInfo.InvariantCulture);

            string culture = string.IsNullOrWhiteSpace(cultureName)
                ? LocalizationService.ResolveAmbientCultureName()
                : cultureName;
            // Rotation is keyed on the pre-increment due count so consecutive reminders
            // (including snooze loops) keep cycling through the micro-break guidance.
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

    /// <summary>"[✓ 已起身放松，重新计时]" — credits the stand-up and starts a fresh round now.</summary>
    public static void ConfirmStoodUp(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        if (!IsSedentaryMode(settings))
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        if (IsRunning(AsReadOnly(state)))
        {
            int totalSec = ReadPositiveInt32(state, KeyTotalSec) ?? intervalSec;
            int remainingSec = ResolveLiveRemainingSeconds(state, now);
            if (remainingSec < 0)
            {
                remainingSec = ReadNonNegativeInt32(state, KeyRemainingSec) ?? intervalSec;
            }

            int stood = (ReadNonNegativeInt32(state, KeySedentaryStoodCount) ?? 0) + 1;
            state[KeySedentaryStoodCount] = stood.ToString(CultureInfo.InvariantCulture);
            RecordLongestSitSeconds(state, ReadFlag(state, KeySedentaryDue) ? totalSec : totalSec - remainingSec);
        }

        StartSedentaryRound(state, now, intervalSec);
    }

    /// <summary>"[会议中 · 推迟 15m]" — pushes the next reminder out and resumes monitoring.</summary>
    public static void SnoozeSedentaryReminder(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        int extraSeconds,
        DateTimeOffset now)
    {
        if (!IsSedentaryMode(settings) || extraSeconds <= 0)
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        (_, int intervalMin, _) = ReadRhythmPreferences(settings);
        int intervalSec = intervalMin * 60;

        int totalSec = ReadPositiveInt32(state, KeyTotalSec) ?? intervalSec;
        int remainingSec = ResolveLiveRemainingSeconds(state, now);
        if (remainingSec < 0)
        {
            remainingSec = ReadNonNegativeInt32(state, KeyRemainingSec) ?? intervalSec;
        }

        bool wasDue = ReadFlag(state, KeySedentaryDue);
        int newRemaining = Math.Clamp((wasDue ? 0 : remainingSec) + extraSeconds, 1, 180 * 60);
        int newTotal = Math.Max(totalSec, newRemaining);

        state[KeyPhase] = PhaseSedentary;
        state[KeyRunning] = "true";
        state[KeyEndUtcTicks] = now.AddSeconds(newRemaining).UtcTicks.ToString(CultureInfo.InvariantCulture);
        state[KeyRemainingSec] = newRemaining.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = newTotal.ToString(CultureInfo.InvariantCulture);
        ClearSedentaryTransientFlags(state);
    }

    private static void StartSedentaryRound(
        IDictionary<string, string> state,
        DateTimeOffset now,
        int intervalSec)
    {
        state[KeyPhase] = PhaseSedentary;
        state[KeyRunning] = "true";
        state[KeyEndUtcTicks] = now.AddSeconds(intervalSec).UtcTicks.ToString(CultureInfo.InvariantCulture);
        state[KeyRemainingSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = intervalSec.ToString(CultureInfo.InvariantCulture);
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
    private static int ResolveLiveRemainingSeconds(IEnumerable<KeyValuePair<string, string>> state, DateTimeOffset now)
    {
        foreach ((string k, string? v) in state)
        {
            if (string.Equals(k, KeyEndUtcTicks, StringComparison.Ordinal) &&
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

        // In sedentary mode a due reminder is settled by playing/pausing the transport too:
        // resume == "I stood up, restart the round", pause == quiet it for now.
        if (IsSedentaryMode(settings) && ReadFlag(state, KeySedentaryDue))
        {
            if (IsRunning(AsReadOnly(state)))
            {
                ConfirmStoodUp(state, settings, now);
            }
            return;
        }

        CustomWidgetPomodoroSnapshot snap = ComputeSnapshot(AsReadOnly(state), settings, now);

        if (snap.IsRunning)
        {
            state[KeyRunning] = "false";
            state[KeyEndUtcTicks] = "0";
            state[KeyRemainingSec] = snap.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
            state[KeyTotalSec] = snap.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            int rem = snap.RemainingSeconds > 0
                ? snap.RemainingSeconds
                : GetDefaultDurationSeconds(snap.Phase, settings);
            int tot = Math.Max(snap.TotalSeconds, rem);
            long endTicks = now.AddSeconds(rem).UtcTicks;

            state[KeyPhase] = snap.Phase;
            state[KeyRunning] = "true";
            state[KeyEndUtcTicks] = endTicks.ToString(CultureInfo.InvariantCulture);
            state[KeyRemainingSec] = rem.ToString(CultureInfo.InvariantCulture);
            state[KeyTotalSec] = tot.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static void ResetCurrentPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        EnsureTodayRollover(state, settings, now);
        string phase = NormalizePhase(state.TryGetValue(KeyPhase, out string? p) ? p : PhaseFocus);
        int defaultSec = GetDefaultDurationSeconds(phase, settings);

        state[KeyPhase] = phase;
        state[KeyRunning] = "false";
        state[KeyEndUtcTicks] = "0";
        state[KeyRemainingSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = defaultSec.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(phase, PhaseSedentary, StringComparison.Ordinal))
        {
            ClearSedentaryTransientFlags(state);
        }
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
        CustomWidgetPomodoroSnapshot snap = ComputeSnapshot(AsReadOnly(state), settings, now);
        int newRem = Math.Clamp(snap.RemainingSeconds + extraSeconds, 1, 180 * 60);
        int newTot = Math.Clamp(snap.TotalSeconds + extraSeconds, newRem, 180 * 60);

        state[KeyRemainingSec] = newRem.ToString(CultureInfo.InvariantCulture);
        state[KeyTotalSec] = newTot.ToString(CultureInfo.InvariantCulture);
        if (snap.IsRunning)
        {
            state[KeyEndUtcTicks] = now.AddSeconds(newRem).UtcTicks.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static void SetPhase(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string targetPhase,
        DateTimeOffset now)
    {
        // Pomodoro phase switching is meaningless (and corrupting) inside sedentary mode.
        if (IsSedentaryMode(settings))
        {
            return;
        }

        EnsureTodayRollover(state, settings, now);
        string normalized = NormalizePhase(targetPhase);
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
        CustomWidgetPomodoroSnapshot snap = ComputeSnapshot(AsReadOnly(state), settings, now);
        TransitionToNextPhase(state, settings, snap.Phase, snap.CycleInSet, autoStartAllowed: false, now);
    }

    public static void SyncIdleDurationWithSettings(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string changedSettingKey)
    {
        if (string.Equals(changedSettingKey, SettingRhythmMode, StringComparison.OrdinalIgnoreCase))
        {
            // Mode flips park the other mode's countdown immediately (even mid-session).
            ApplyRhythmMode(state, settings);
            return;
        }

        IReadOnlyDictionary<string, string> roState = AsReadOnly(state);
        if (IsRunning(roState))
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
        bool affectsCurrentPhase =
            (phase == PhaseFocus && string.Equals(changedSettingKey, "focusMinutes", StringComparison.OrdinalIgnoreCase)) ||
            (phase == PhaseSedentary && string.Equals(changedSettingKey, SettingSedentaryIntervalMin, StringComparison.OrdinalIgnoreCase)) ||
            (phase != PhaseFocus && phase != PhaseSedentary &&
                (string.Equals(changedSettingKey, "breakPreset", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(changedSettingKey, "shortBreakMinutes", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(changedSettingKey, "longBreakMinutes", StringComparison.OrdinalIgnoreCase)));

        if (!affectsCurrentPhase)
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
        if (IsSedentaryMode(settings))
        {
            // Sedentary mode owns its own heartbeat (see PumpSedentary); pomodoro phase
            // completion must never run against a sedentary round.
            return false;
        }

        if (!IsRunning(AsReadOnly(state)))
        {
            return false;
        }

        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        CustomWidgetPomodoroSnapshot snap = ComputeSnapshot(AsReadOnly(state), settings, now, culture);
        if (snap.RemainingSeconds > 0)
        {
            return false;
        }

        EnsureTodayRollover(state, settings, now);
        var prefs = ReadPreferences(settings);
        string completedPhase = snap.Phase;

        if (completedPhase == PhaseFocus)
        {
            int focusedMinutes = Math.Max(1, (int)Math.Round(snap.TotalSeconds / 60.0));
            int nextCompleted = snap.TodayCompleted + 1;
            int nextMinutes = snap.TodayMinutes + focusedMinutes;

            state[KeyTodayCompleted] = nextCompleted.ToString(CultureInfo.InvariantCulture);
            state[KeyTodayMinutes] = nextMinutes.ToString(CultureInfo.InvariantCulture);

            string today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string lastHit = state.TryGetValue(KeyLastGoalHitDate, out string? lh) ? lh : string.Empty;
            if (nextCompleted >= prefs.DailyGoal && !string.Equals(lastHit, today, StringComparison.Ordinal))
            {
                int nextStreak = snap.StreakDays + 1;
                state[KeyStreakDays] = nextStreak.ToString(CultureInfo.InvariantCulture);
                state[KeyLastGoalHitDate] = today;
            }

            if (!collections.TryGetValue("sessions", out List<CustomWidgetRecord>? sessionList))
            {
                sessionList = [];
                collections["sessions"] = sessionList;
            }

            string taskLabel = !string.IsNullOrWhiteSpace(snap.CurrentTask)
                ? snap.CurrentTask
                : LocalizationService.FormatForCulture(culture, "CustomWidget.Pomodoro.DefaultSessionTaskFormat", nextCompleted);

            var record = new CustomWidgetRecord
            {
                CreatedAtUtcTicks = now.UtcTicks
            };
            record.Fields["task"] = taskLabel;
            record.Fields["text"] = taskLabel;
            record.Fields["duration"] = $"{focusedMinutes}m";
            record.Fields["tag"] = $"{focusedMinutes}m";
            record.Fields["cycle"] = $"#{snap.CycleInSet}/4";
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
                snap.CycleInSet,
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
                snap.CycleInSet,
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
                snap.TodayCompleted);
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
            "todayDurationText" => s.TodayMinutes >= 60 ? $"{s.TodayMinutes / 60}h {s.TodayMinutes % 60}m" : $"{s.TodayMinutes}m",
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
            // v5.0 dual-rhythm tokens (sedentary guardian).
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
            _ => string.Empty
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
