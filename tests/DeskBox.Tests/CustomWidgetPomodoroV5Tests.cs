using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Task ④ (deskbox.pomodoro v5.0) acceptance tests: dual rhythm mode (🍅 pomodoro focus /
/// 🪑 sedentary guardian), the GetLastInputInfo-driven away-suspend → return-reset state
/// machine, snooze/confirm actions, rotating micro-break guidance, daily health stats, and
/// the 100% backward-compatibility contract with pre-v5 store.json payloads.
/// </summary>
public sealed class CustomWidgetPomodoroV5Tests
{
    private const string ZhCn = "zh-CN";

    private static readonly string[] AllLocales =
    [
        "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR",
        "hi-IN", "ja-JP", "ko-KR", "pt-BR", "ru-RU", "zh-CN", "zh-TW"
    ];

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) =>
        new(2026, 11, 4, hour, minute, second, TimeSpan.FromHours(8));

    private static Dictionary<string, string> SedentarySettings(
        int intervalMin = 50,
        int idleResetMin = 5,
        bool sedentary = true) => new(StringComparer.Ordinal)
    {
        ["rhythmMode"] = sedentary ? "sedentary" : "pomodoro",
        ["sedentaryIntervalMin"] = intervalMin.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["idleAutoResetMin"] = idleResetMin.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    // ---------------------------------------------------------------- manifest gate

    [Fact]
    public void ManifestV5_PassesStaticGateWithZeroErrorsAndWarnings_AndDeclaresDualRhythm()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.PomodoroTemplateId);
        CustomWidgetParseResult parsed =
            CustomWidgetScriptValidator.ValidateWorkspaceBundle(bundle.ManifestJson, bundle.LogicJs, bundle.Locales);

        Assert.NotNull(parsed.Manifest);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        Assert.Empty(parsed.Diagnostics);

        CustomWidgetManifest manifest = parsed.Manifest!;
        Assert.Equal("deskbox.pomodoro", manifest.Id);
        Assert.Equal("5.0.0", manifest.Version);
        Assert.Equal("番茄专注 & 久坐提醒", manifest.Name);

        // New settings must default to the exact pre-v5 pomodoro behavior.
        Assert.Contains(manifest.Settings, s => s.Key == "rhythmMode" && s.DefaultValue == "pomodoro");
        Assert.Contains(manifest.Settings, s => s.Key == "sedentaryIntervalMin" && s.DefaultValue == "50");
        Assert.Contains(manifest.Settings, s => s.Key == "idleAutoResetMin" && s.DefaultValue == "5");

        // Pomodoro-only modules are mode-gated; the health module appears only in sedentary mode.
        Assert.Equal("${$pomodoro.mode == 'pomodoro'}",
            manifest.View.Children.Single(c => c.Id == "task").When);
        Assert.Equal("${$pomodoro.mode == 'pomodoro'}",
            manifest.View.Children.Single(c => c.Id == "goal").When);
        Assert.Equal("${$pomodoro.mode == 'pomodoro'}",
            manifest.View.Children.Single(c => c.Id == "history").When);
        Assert.Equal("${$pomodoro.mode == 'sedentary'}",
            manifest.View.Children.Single(c => c.Id == "health").When);

        Assert.NotNull(manifest.Capsule);
        Assert.Equal("${$pomodoro.isDue}", manifest.Capsule!.BadgeIsWarning);
    }

    [Fact]
    public void ManifestV5_NewI18nKeysCoverAllThirteenLocales()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.PomodoroTemplateId);
        Assert.False(bundle.Locales.IsEmpty);

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        Assert.NotNull(parsed.Manifest);
        Assert.Empty(parsed.Manifest!.I18n);

        CustomWidgetManifest manifest = parsed.Manifest.WithLocales(bundle.Locales);

        string[] requiredKeys =
        [
            "setRhythmMode", "optRhythmPomodoro", "optRhythmSedentary",
            "setSedentaryInterval", "optSed40", "optSed50", "optSed60", "optSed90",
            "setIdleReset", "optIdleOff", "optIdle5", "optIdle10",
            "modHealth", "nextMicroPrefix", "widgetName", "widgetDescription"
        ];

        foreach (string locale in AllLocales)
        {
            Assert.Contains(locale, manifest.I18n.Keys);
            foreach (string key in requiredKeys)
            {
                Assert.True(
                    manifest.I18n[locale].TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value),
                    $"locale {locale} is missing i18n key '{key}'");
            }
        }
    }

    // ---------------------------------------------------------------- backward compatibility

    [Fact]
    public void LegacyStore_WithoutRhythmSettings_KeepsPomodoroBehaviorUnchanged()
    {
        // Pre-v5 payload: no rhythmMode/sedentaryIntervalMin/idleAutoResetMin keys at all.
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["focusMinutes"] = "25",
            ["breakPreset"] = "5,15",
            ["flowMode"] = "semi"
        };
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);

        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        CustomWidgetPomodoroSnapshot running =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t0.AddMinutes(5), cultureName: ZhCn);
        Assert.Equal(CustomWidgetPomodoroEngine.ModePomodoro, running.Mode);
        Assert.False(running.IsAwaySuspended);
        Assert.False(running.IsDue);
        Assert.Equal("专注中", running.HeaderPillText);
        Assert.Equal("sedentary", CustomWidgetPomodoroEngine.NormalizePhase("sedentary"));

        // The pomodoro completion pump still settles expired focus rounds on the legacy path.
        Assert.True(CustomWidgetPomodoroEngine.TryPumpForMode(
            state, settings, collections, t0.AddMinutes(25), idleSeconds: 0, notice: out _));
        Assert.Equal(CustomWidgetPomodoroEngine.PhaseShortBreak, state[CustomWidgetPomodoroEngine.KeyPhase]);
    }

    // ---------------------------------------------------------------- mode switching

    [Fact]
    public void RhythmModeSwitch_ParksRunningPomodoroAndRestoresItSymmetrically()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["focusMinutes"] = "25",
            ["breakPreset"] = "5,15"
        };
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);

        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);
        Assert.True(CustomWidgetPomodoroEngine.IsRunning(state));

        // Flip the live setting (exactly what SetSettingValueAsync does on change).
        settings["rhythmMode"] = "sedentary";
        settings["sedentaryIntervalMin"] = "50";
        settings["idleAutoResetMin"] = "5";
        CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(state, settings, "rhythmMode");

        Assert.Equal(CustomWidgetPomodoroEngine.PhaseSedentary, state[CustomWidgetPomodoroEngine.KeyPhase]);
        Assert.False(CustomWidgetPomodoroEngine.IsRunning(state));
        CustomWidgetPomodoroSnapshot idle =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t0, cultureName: ZhCn);
        Assert.Equal(CustomWidgetPomodoroEngine.ModeSedentary, idle.Mode);
        Assert.Equal(50 * 60, idle.TotalSeconds);
        Assert.Equal(50 * 60, idle.RemainingSeconds);
        Assert.Equal("久坐守护", idle.HeaderPillText);

        // Daily health stats survive mode flips.
        state[CustomWidgetPomodoroEngine.KeySedentaryStoodCount] = "3";
        settings["rhythmMode"] = "pomodoro";
        CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(state, settings, "rhythmMode");
        Assert.Equal(CustomWidgetPomodoroEngine.PhaseFocus, state[CustomWidgetPomodoroEngine.KeyPhase]);
        Assert.Equal("1500", state[CustomWidgetPomodoroEngine.KeyRemainingSec]);
        Assert.Equal("3", state[CustomWidgetPomodoroEngine.KeySedentaryStoodCount]);
    }

    // ---------------------------------------------------------------- idle suspend → return reset

    [Fact]
    public void SedentaryRound_SuspendsWhenAway_AndResetsFromZeroOnReturn()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);

        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);
        Assert.True(CustomWidgetPomodoroEngine.IsRunning(state));

        // 20 minutes in, user is actively typing: nothing changes.
        DateTimeOffset t20 = t0.AddMinutes(20);
        Assert.False(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, t20, idleSeconds: 8, out _, ZhCn));
        CustomWidgetPomodoroSnapshot mid =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t20, cultureName: ZhCn);
        Assert.True(mid.IsRunning);
        Assert.False(mid.IsAwaySuspended);
        Assert.Equal(20, mid.SatMinutes);
        Assert.Equal("伏案监测中", mid.HeaderPillText);
        Assert.Contains("键鼠", mid.DialSubtext, StringComparison.Ordinal);
        Assert.Equal(30 * 60, mid.RemainingSeconds);
        Assert.Equal("已坐 20m / 50m", mid.CapsuleTitle); // calm "已坐 Xm / Ym" capsule while monitoring

        // User leaves for 5m10s: the round freezes into the away-suspended state.
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, t20.AddSeconds(10), idleSeconds: 310, out _, ZhCn));
        CustomWidgetPomodoroSnapshot away =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t20.AddSeconds(10), cultureName: ZhCn);
        Assert.True(away.IsAwaySuspended);
        Assert.True(away.IsRunning);
        Assert.Equal("离座休息中", away.HeaderPillText);
        Assert.Equal(30 * 60 - 10, away.RemainingSeconds); // frozen at the suspend poll, not draining

        // Coming back 10 minutes later: the moment input resumes the round starts from ZERO —
        // never "you sat for 2 minutes and instantly got nagged".
        DateTimeOffset tBack = t20.AddMinutes(11);
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, tBack, idleSeconds: 0.4, out _, ZhCn));
        CustomWidgetPomodoroSnapshot fresh =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tBack, cultureName: ZhCn);
        Assert.False(fresh.IsAwaySuspended);
        Assert.Equal(50 * 60, fresh.RemainingSeconds);
        Assert.Equal(0, fresh.SatMinutes);
        // The abandoned 20-minute sitting stint was recorded as today's longest.
        Assert.Equal(20, fresh.LongestSitMin);
    }

    [Fact]
    public void SedentaryRound_ShortLeaveBelowThreshold_DoesNotReset()
    {
        var settings = SedentarySettings(idleResetMin: 5);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        DateTimeOffset t30 = t0.AddMinutes(30);
        // Gone 4m59s — below the 5-minute threshold, the countdown keeps running.
        Assert.False(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, t30, idleSeconds: 299, out _, ZhCn));
        CustomWidgetPomodoroSnapshot snap =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t30, cultureName: ZhCn);
        Assert.False(snap.IsAwaySuspended);
        Assert.Equal(20 * 60, snap.RemainingSeconds);
    }

    // ---------------------------------------------------------------- due → snooze / confirm / rotation

    [Fact]
    public void SedentaryDue_FiresNoticeOnce_SnoozesFifteenMinutes_AndRotatesMicroActions()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        // Deadline reached while the user is at the keyboard.
        DateTimeOffset tDue = t0.AddMinutes(50).AddSeconds(1);
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, tDue, idleSeconds: 3, out CustomWidgetPomodoroCompletionNotice? notice, ZhCn));
        Assert.NotNull(notice);
        Assert.Equal("该起身活动啦", notice.Title);
        Assert.Contains("50", notice.Message, StringComparison.Ordinal);

        CustomWidgetPomodoroSnapshot dueSnap =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tDue, cultureName: ZhCn);
        Assert.True(dueSnap.IsDue);
        Assert.Equal(100.0, dueSnap.ProgressPercent, 5);
        Assert.Contains("起身活动肩颈", dueSnap.MicroAction, StringComparison.Ordinal); // micro #0
        Assert.Equal("已起身放松", dueSnap.ConfirmActionText);
        Assert.Equal("推迟 15m", dueSnap.SnoozeActionText);
        Assert.Equal("该起身活动啦", dueSnap.CapsuleTitle); // due capsules escalate per the plan

        // Overdue time counts up in the dial instead of freezing at 00:00.
        CustomWidgetPomodoroSnapshot overdue =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tDue.AddMinutes(2), cultureName: ZhCn);
        Assert.Equal("02:00", overdue.TimeText);

        // Repeated pumps while due do not spam a second notice.
        Assert.False(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, tDue.AddSeconds(30), idleSeconds: 2, out _, ZhCn));

        // [会议中 · 推迟 15m]: the reminder is pushed out and monitoring resumes.
        CustomWidgetPomodoroEngine.SnoozeSedentaryReminder(state, settings, 900, tDue.AddMinutes(1));
        CustomWidgetPomodoroSnapshot snoozed =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tDue.AddMinutes(1), cultureName: ZhCn);
        Assert.False(snoozed.IsDue);
        Assert.True(snoozed.IsRunning);
        Assert.Equal(900, snoozed.RemainingSeconds);

        // Second due rotates to micro action #1 (20-20-20 sight break).
        DateTimeOffset tDue2 = tDue.AddMinutes(1).AddSeconds(900);
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(state, settings, tDue2, idleSeconds: 1, out _, ZhCn));
        CustomWidgetPomodoroSnapshot due2 =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tDue2, cultureName: ZhCn);
        Assert.Contains("20-20-20", due2.MicroAction, StringComparison.Ordinal);

        // [✓ 已起身放松]: credits the stand-up and starts a brand-new round immediately.
        CustomWidgetPomodoroEngine.ConfirmStoodUp(state, settings, tDue2.AddMinutes(1));
        CustomWidgetPomodoroSnapshot confirmed =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, tDue2.AddMinutes(1), cultureName: ZhCn);
        Assert.False(confirmed.IsDue);
        Assert.True(confirmed.IsRunning);
        Assert.Equal(50 * 60, confirmed.RemainingSeconds);
        Assert.Equal(1, confirmed.StoodCount);
        Assert.Contains("已按时起身 1 次", confirmed.HealthSummary, StringComparison.Ordinal);
        Assert.Contains("最长连续久坐 50 分钟", confirmed.HealthSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void SedentaryAwayWhileDue_AutoConfirmsTheStandUpInsteadOfNagging()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(
            state, settings, t0.AddMinutes(50).AddSeconds(1), idleSeconds: 2, out _, ZhCn));

        // The user obeys the reminder and walks away for 6 minutes: auto-confirm + suspend.
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(
            state, settings, t0.AddMinutes(56), idleSeconds: 360, out _, ZhCn));
        Assert.Equal("1", state[CustomWidgetPomodoroEngine.KeySedentaryStoodCount]);
        Assert.Equal("1", state[CustomWidgetPomodoroEngine.KeySedentaryAway]);
        Assert.Equal("0", state[CustomWidgetPomodoroEngine.KeySedentaryDue]);

        // Returning starts the next round from zero with the follow-up micro action queued.
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(
            state, settings, t0.AddMinutes(57), idleSeconds: 0.3, out _, ZhCn));
        CustomWidgetPomodoroSnapshot next =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t0.AddMinutes(57), cultureName: ZhCn);
        Assert.False(next.IsAwaySuspended);
        Assert.Equal(50 * 60, next.RemainingSeconds);
        Assert.Contains("20-20-20", next.MicroAction, StringComparison.Ordinal); // next in rotation
    }

    [Fact]
    public void SedentaryIdleDetectionOff_StillFiresDueAtTheDeadline()
    {
        var settings = SedentarySettings(idleResetMin: 0);
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        // Even hours of idle mean nothing when detection is off: the deadline rules.
        Assert.False(CustomWidgetPomodoroEngine.PumpSedentary(
            state, settings, t0.AddMinutes(30), idleSeconds: 4000, out _, ZhCn));
        Assert.True(CustomWidgetPomodoroEngine.PumpSedentary(
            state, settings, t0.AddMinutes(50).AddSeconds(1), idleSeconds: 4000, out _, ZhCn));
        Assert.Equal("1", state[CustomWidgetPomodoroEngine.KeySedentaryDue]);
    }

    // ---------------------------------------------------------------- guards & stats

    [Fact]
    public void SedentaryMode_GuardsPomodoroPhaseOperationsToNoOps()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        CustomWidgetPomodoroEngine.SetPhase(state, settings, "shortBreak", t0);
        CustomWidgetPomodoroEngine.CyclePhase(state, settings, t0);
        CustomWidgetPomodoroEngine.SkipPhase(state, settings, t0);

        Assert.Equal(CustomWidgetPomodoroEngine.PhaseSedentary, state[CustomWidgetPomodoroEngine.KeyPhase]);
        Assert.True(CustomWidgetPomodoroEngine.IsRunning(state));

        // The legacy pomodoro completion pump must never run against a sedentary round.
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        Assert.False(CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(
            state, settings, collections, t0.AddHours(2), out _));

        // Reset parks the guardian back to a full untouched interval.
        CustomWidgetPomodoroEngine.ResetCurrentPhase(state, settings, t0);
        CustomWidgetPomodoroSnapshot reset =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t0, cultureName: ZhCn);
        Assert.False(reset.IsRunning);
        Assert.Equal(50 * 60, reset.RemainingSeconds);
        Assert.False(reset.IsDue);
        Assert.False(reset.IsAwaySuspended);
    }

    [Fact]
    public void SedentaryDailyRollover_ResetsHealthStatsButKeepsMode()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomWidgetPomodoroEngine.KeySedentaryStoodCount] = "4",
            [CustomWidgetPomodoroEngine.KeySedentaryLongestMin] = "88",
            [CustomWidgetPomodoroEngine.KeySedentaryDueCount] = "6"
        };

        DateTimeOffset nextDay = new(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(8));
        Assert.True(CustomWidgetPomodoroEngine.EnsureTodayRollover(state, settings, nextDay));
        Assert.Equal("0", state[CustomWidgetPomodoroEngine.KeySedentaryStoodCount]);
        Assert.Equal("0", state[CustomWidgetPomodoroEngine.KeySedentaryLongestMin]);
        Assert.Equal("0", state[CustomWidgetPomodoroEngine.KeySedentaryDueCount]);

        CustomWidgetPomodoroSnapshot snap =
            CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, nextDay, cultureName: ZhCn);
        Assert.Equal(CustomWidgetPomodoroEngine.ModeSedentary, snap.Mode);
        Assert.Equal(0, snap.StoodCount);
        Assert.Contains("起身活动肩颈", snap.MicroAction, StringComparison.Ordinal); // rotation restarts
    }

    [Fact]
    public void SedentaryTokens_ResolveForManifestExpressions()
    {
        var settings = SedentarySettings();
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset t0 = At(9);
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);

        string mode = CustomWidgetPomodoroEngine.ResolveToken("mode", state, settings, t0.AddMinutes(10), ZhCn);
        string sat = CustomWidgetPomodoroEngine.ResolveToken("satMinutes", state, settings, t0.AddMinutes(10), ZhCn);
        string isAway = CustomWidgetPomodoroEngine.ResolveToken("isAway", state, settings, t0.AddMinutes(10), ZhCn);
        string isDue = CustomWidgetPomodoroEngine.ResolveToken("isDue", state, settings, t0.AddMinutes(10), ZhCn);
        string interval = CustomWidgetPomodoroEngine.ResolveToken("intervalMin", state, settings, t0.AddMinutes(10), ZhCn);
        string health = CustomWidgetPomodoroEngine.ResolveToken("healthSummary", state, settings, t0.AddMinutes(10), ZhCn);

        Assert.Equal("sedentary", mode);
        Assert.Equal("10", sat);
        Assert.Equal("false", isAway);
        Assert.Equal("false", isDue);
        Assert.Equal("50", interval);
        Assert.Contains("0 次", health, StringComparison.Ordinal);
    }

    [Fact]
    public void SedentaryPrefs_ParseDefaultsAndCustomValues()
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        (string rhythm, int interval, int idleReset) = CustomWidgetPomodoroEngine.ReadRhythmPreferences(empty);
        Assert.Equal(("pomodoro", 50, 5), (rhythm, interval, idleReset));
        Assert.False(CustomWidgetPomodoroEngine.IsSedentaryMode(empty));

        var custom = SedentarySettings(intervalMin: 90, idleResetMin: 10);
        (rhythm, interval, idleReset) = CustomWidgetPomodoroEngine.ReadRhythmPreferences(custom);
        Assert.Equal(("sedentary", 90, 10), (rhythm, interval, idleReset));
        Assert.True(CustomWidgetPomodoroEngine.IsSedentaryMode(custom));

        Assert.Equal(90 * 60, CustomWidgetPomodoroEngine.GetDefaultDurationSeconds("sedentary", custom));
    }
}
