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
    string DialSubtext = "");

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

    public static int GetDefaultDurationSeconds(string phase, IReadOnlyDictionary<string, string> settings)
    {
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

    public static void ToggleStartPause(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        DateTimeOffset now)
    {
        EnsureTodayRollover(state, settings, now);
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
        EnsureTodayRollover(state, settings, now);
        CustomWidgetPomodoroSnapshot snap = ComputeSnapshot(AsReadOnly(state), settings, now);
        TransitionToNextPhase(state, settings, snap.Phase, snap.CycleInSet, autoStartAllowed: false, now);
    }

    public static void SyncIdleDurationWithSettings(
        IDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        string changedSettingKey)
    {
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
            (phase != PhaseFocus && (string.Equals(changedSettingKey, "breakPreset", StringComparison.OrdinalIgnoreCase) ||
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
            _ => string.Empty
        };
    }

    public static string NormalizePhase(string? raw)
    {
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
