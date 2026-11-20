using System.Globalization;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal sealed class ClockCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _clockTimer;

    internal ClockCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "clock";
    public IReadOnlyList<string> CapabilityIds => ["clock"];

    public void UpdateSubscription() => UpdateClockSourceSubscription();

    public void Stop()
    {
        if (_clockTimer is not null)
        {
            _clockTimer.Stop();
            _clockTimer.Tick -= OnClockTimerTick;
            _clockTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesClock() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "clock", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "clock.") ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "tz");

    internal void UpdateClockSourceSubscription()
    {
        bool shouldRun = !_vm.IsDisposedInternal &&
                         _vm.IsWindowVisibleInternal &&
                         !_vm.IsStudioGalleryThumbnailPreview &&
                         (!_vm.IsCompactCollapsedInternal ||
                          CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "clock.") ||
                          CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "time.") ||
                          CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "tz")) &&
                         ManifestUsesClock();

        if (!shouldRun)
        {
            if (_clockTimer is not null)
            {
                _clockTimer.Stop();
                _clockTimer.Tick -= OnClockTimerTick;
                _clockTimer = null;
            }
            return;
        }

        CustomWidgetSourceSpec? spec = _vm.ManifestInternal.Sources
            .FirstOrDefault(static s => string.Equals(s.Type, "clock", StringComparison.OrdinalIgnoreCase));
        int intervalMs = Math.Clamp(spec?.IntervalMs is > 0 ? spec.IntervalMs : 1000, 500, 60_000);

        if (_vm.DispatcherQueueInternal is not null)
        {
            if (_clockTimer is null)
            {
                _clockTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _clockTimer.IsRepeating = true;
                _clockTimer.Tick += OnClockTimerTick;
            }

            TimeSpan interval = TimeSpan.FromMilliseconds(intervalMs);
            if (_clockTimer.Interval != interval)
            {
                _clockTimer.Interval = interval;
            }
            if (!_clockTimer.IsRunning)
            {
                _clockTimer.Start();
            }
        }
    }

    internal void OnClockTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        _vm.RefreshCapsuleProjection(bumpLiveState: false);
        _vm.NotifyMetricsRefreshed();
    }

}

internal sealed class CalendarCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _calendarDayTimer;
    private DateOnly _lastCalendarDay;

    internal CalendarCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "calendar";
    public IReadOnlyList<string> CapabilityIds => ["calendar"];

    public void UpdateSubscription() => UpdateCalendarSourceSubscription();

    public void Stop()
    {
        if (_calendarDayTimer is not null)
        {
            _calendarDayTimer.Stop();
            _calendarDayTimer.Tick -= OnCalendarDayTimerTick;
            _calendarDayTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesCalendar() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "calendar", StringComparison.OrdinalIgnoreCase)) ||
        CustomWidgetViewModel.NodeTreeContainsType(_vm.ManifestInternal.View, "CalendarGrid") ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "calendar.");

    internal void UpdateCalendarSourceSubscription()
    {
        bool shouldWatch = !_vm.IsDisposedInternal &&
                           _vm.IsWindowVisibleInternal &&
                           !_vm.IsStudioGalleryThumbnailPreview &&
                           (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "calendar.")) &&
                           ManifestUsesCalendar();

        if (!shouldWatch)
        {
            if (_calendarDayTimer is not null)
            {
                _calendarDayTimer.Stop();
                _calendarDayTimer.Tick -= OnCalendarDayTimerTick;
                _calendarDayTimer = null;
            }
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (!_vm.StateInternal.TryGetValue("selectedDate", out string? curSelected) ||
            string.IsNullOrWhiteSpace(curSelected))
        {
            _vm.StateInternal["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (_lastCalendarDay == default)
        {
            _lastCalendarDay = today;
        }
        else if (today != _lastCalendarDay)
        {
            string prevIso = _lastCalendarDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _lastCalendarDay = today;
            if (_vm.StateInternal.TryGetValue("selectedDate", out string? sel) &&
                string.Equals(sel, prevIso, StringComparison.Ordinal))
            {
                _vm.StateInternal["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }

        if (_calendarDayTimer is null && _vm.DispatcherQueueInternal is not null)
        {
            _calendarDayTimer = _vm.DispatcherQueueInternal.CreateTimer();
            _calendarDayTimer.IsRepeating = false;
            _calendarDayTimer.Tick += OnCalendarDayTimerTick;
        }

        if (_calendarDayTimer is not null && !_calendarDayTimer.IsRunning)
        {
            _calendarDayTimer.Interval = CountdownCapabilityProvider.ComputeWakeResilientMidnightDelay(now);
            _calendarDayTimer.Start();
        }
    }

    internal void OnCalendarDayTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (today != _lastCalendarDay)
        {
            string prevIso = _lastCalendarDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _lastCalendarDay = today;
            if (_vm.StateInternal.TryGetValue("selectedDate", out string? sel) &&
                string.Equals(sel, prevIso, StringComparison.Ordinal))
            {
                _vm.StateInternal["selectedDate"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (_vm.CompiledScriptInternal is not null)
            {
                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            }
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }

        sender.Interval = CountdownCapabilityProvider.ComputeWakeResilientMidnightDelay(now);
        sender.Start();
    }

}

internal sealed class CountdownCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _countdownTimer;
    private DispatcherQueueTimer? _countdownWorkTimer;
    private DateOnly _lastCountdownDay;

    internal CountdownCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "countdown";
    public IReadOnlyList<string> CapabilityIds => ["countdown"];

    internal void SyncObservedDay(DateOnly day) => _lastCountdownDay = day;

    internal void EvaluateDayRolloverAndToast(DateTimeOffset now)
    {
        DateOnly currentDay = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (_lastCountdownDay == default)
        {
            _lastCountdownDay = currentDay;
        }
        else if (currentDay != _lastCountdownDay)
        {
            _lastCountdownDay = currentDay;
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
        }
        TryDispatchDueTodayCountdownToast(now);
    }

    public void UpdateSubscription() => UpdateCountdownTimerSubscription();

    public void Stop()
    {
        if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= OnCountdownTimerTick;
            _countdownTimer = null;
        }
        if (_countdownWorkTimer is not null)
        {
            _countdownWorkTimer.Stop();
            _countdownWorkTimer.Tick -= OnCountdownWorkTimerTick;
            _countdownWorkTimer = null;
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesCountdown() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "countdown", StringComparison.OrdinalIgnoreCase)) ||
        NodeTreeReferencesCountdown(_vm.ManifestInternal.View);

    internal static bool NodeTreeReferencesCountdown(CustomWidgetNodeSpec node) =>
        CustomWidgetViewModel.NodeTreeReferencesSubstring(node, "$countdown.");

    internal static TimeSpan ComputeCountdownNextMidnightDelay(DateTimeOffset now)
    {
        DateTime localNow = now.ToLocalTime().DateTime;
        DateTime nextMidnight = localNow.Date.AddDays(1);
        TimeSpan delay = (nextMidnight - localNow) + TimeSpan.FromMilliseconds(150);
        return delay < TimeSpan.FromMilliseconds(150)
            ? TimeSpan.FromMilliseconds(150)
            : delay;
    }

    internal static TimeSpan ComputeWakeResilientMidnightDelay(DateTimeOffset now)
    {
        TimeSpan untilMidnight = ComputeCountdownNextMidnightDelay(now);
        return untilMidnight > TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : untilMidnight;
    }

    internal void UpdateCountdownTimerSubscription()
    {
        bool shouldWatchDayRollover = !_vm.IsDisposedInternal &&
                                      !_vm.IsPowerSuspendedInternal &&
                                      !_vm.IsStudioGalleryThumbnailPreview &&
                                      ManifestUsesCountdown();
        if (shouldWatchDayRollover)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            EvaluateDayRolloverAndToast(now);

            if (_countdownTimer is null && _vm.DispatcherQueueInternal is not null)
            {
                _countdownTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _countdownTimer.IsRepeating = false;
                _countdownTimer.Tick += OnCountdownTimerTick;
            }

            if (_countdownTimer is not null && !_countdownTimer.IsRunning)
            {
                _countdownTimer.Interval = _vm.IsWindowVisibleInternal
                    ? ComputeWakeResilientMidnightDelay(now)
                    : ComputeCountdownNextMidnightDelay(now);
                _countdownTimer.Start();
            }
        }
        else if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer.Tick -= OnCountdownTimerTick;
            _countdownTimer = null;
        }

        UpdateCountdownWorkTimerSubscription();
    }

    /// <summary>
    /// v3.0: when the expanded time-progress panel renders a live work-hours bar
    /// (progressScope == workAndYear / allFour), refresh the view once a minute so the
    /// bar moves smoothly. The widget stays collapsed -> no tick (nothing visible to move).
    /// </summary>
    internal void UpdateCountdownWorkTimerSubscription()
    {
        string? progressScope = _vm.SettingsInternal.TryGetValue("progressScope", out string? rawScope) &&
                                !string.IsNullOrWhiteSpace(rawScope)
            ? rawScope.Trim()
            : "workAndYear";
        bool needsMinuteTick = !_vm.IsDisposedInternal &&
                               _vm.IsWindowVisibleInternal &&
                               !_vm.IsStudioGalleryThumbnailPreview &&
                               !_vm.IsCompactCollapsedInternal &&
                               ManifestUsesCountdown() &&
                               NodeTreeReferencesCountdown(_vm.ManifestInternal.View) &&
                               (string.Equals(progressScope, "workAndYear", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(progressScope, "allFour", StringComparison.OrdinalIgnoreCase) ||
                                (_vm.StateInternal.TryGetValue("timeTab", out string? tt) &&
                                 string.Equals(tt, "work", StringComparison.OrdinalIgnoreCase)));

        if (needsMinuteTick)
        {
            if (_countdownWorkTimer is null && _vm.DispatcherQueueInternal is not null)
            {
                _countdownWorkTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _countdownWorkTimer.IsRepeating = true;
                _countdownWorkTimer.Interval = TimeSpan.FromSeconds(60);
                _countdownWorkTimer.Tick += OnCountdownWorkTimerTick;
            }

            if (_countdownWorkTimer is not null && !_countdownWorkTimer.IsRunning)
            {
                _countdownWorkTimer.Start();
            }
        }
        else if (_countdownWorkTimer is not null)
        {
            _countdownWorkTimer.Stop();
            _countdownWorkTimer.Tick -= OnCountdownWorkTimerTick;
            _countdownWorkTimer = null;
        }
    }

    internal void OnCountdownWorkTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        _vm.RefreshCapsuleProjection(bumpLiveState: false);
        _vm.NotifyViewRebuildRequested();
    }

    internal void OnCountdownTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly currentDay = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (currentDay != _lastCountdownDay)
        {
            _lastCountdownDay = currentDay;
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();
            TryDispatchDueTodayCountdownToast(now);
        }

        sender.Interval = ComputeWakeResilientMidnightDelay(now);
        sender.Start();
    }

    internal void TryDispatchDueTodayCountdownToast(DateTimeOffset now)
    {
        if (_vm.IsPreviewOnlyInternal || _vm.IsDisposedInternal)
        {
            return;
        }

        if (_vm.SettingsInternal.TryGetValue("notifyDueToday", out string? notifyFlag) &&
            string.Equals(notifyFlag, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string todayIso = DateOnly.FromDateTime(now.ToLocalTime().DateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_vm.StateInternal.TryGetValue("__lastDueToastDate", out string? lastDate) &&
            string.Equals(lastDate, todayIso, StringComparison.Ordinal))
        {
            return;
        }

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            _vm.StateInternal,
            _vm.SettingsInternal,
            _vm.CollectionsInternal,
            now,
            _vm.ActiveCultureName);
        var dueToday = snap.AllEvents
            .Where(static e => e.Direction == CountdownDirection.Today)
            .ToList();
        if (dueToday.Count == 0)
        {
            return;
        }

        _vm.StateInternal["__lastDueToastDate"] = todayIso;
        _ = _vm.PersistStoreAsync();

        string title = LocalizationService.FormatForCulture(
            _vm.ActiveCultureName,
            "CustomWidget.Todo.DueTodayTitleFormat",
            _vm.Config.Name);
        string message = dueToday.Count == 1
            ? LocalizationService.FormatForCulture(
                _vm.ActiveCultureName,
                "CustomWidget.Todo.DueTodayBodyFormat",
                dueToday[0].Title)
            : string.Join(" · ", dueToday.Take(3).Select(static e => e.Title));
        DispatchReminderToastNotification(title, message);
    }

    internal void DispatchReminderToastNotification(string title, string message)
    {
        if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.Notifications))
        {
            return;
        }

        if (_vm.IsPreviewOnlyInternal)
        {
            _vm.NotifyPreviewNotificationDispatched(title, message);
            return;
        }

        try
        {
            string shortId = _vm.Config.Id.Length > 8 ? _vm.Config.Id[..8] : _vm.Config.Id;
            _vm.NotificationServiceProviderInternal?.Invoke()?.TryShow(
                title,
                message,
                arguments: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "custom-reminder",
                    ["widgetId"] = _vm.Config.Id
                },
                options: new NativeAppNotificationOptions(
                    Tag: $"rem-{shortId}",
                    Group: "CustomReminder",
                    MuteAudio: false));
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Reminder toast notification skipped: {ex.Message}");
        }
    }

}

internal sealed class PomodoroCapabilityProvider : ICustomWidgetCapabilityProvider
{
    private readonly CustomWidgetViewModel _vm;
    private DispatcherQueueTimer? _pomodoroTimer;
    private string? _activeFocusSoundscape;
    private double _activeFocusSoundscapeVolume = 0.35;
    private string? _lastProjectedPhase;
    private string? _lastProjectedStatus;
    private string? _lastProjectedSecondaryStatus;
    private int _lastProjectedStageBucket = -1;
    private int _lastProjectedCompletedSessions = -1;
    private DateOnly _lastProjectedDayKey;

    internal PomodoroCapabilityProvider(CustomWidgetViewModel vm) => _vm = vm;

    public string ProviderId => "pomodoro";
    public IReadOnlyList<string> CapabilityIds => ["pomodoro"];

    public void UpdateSubscription() => UpdatePomodoroTimerSubscription();

    public void Stop()
    {
        if (_pomodoroTimer is not null)
        {
            _pomodoroTimer.Stop();
            _pomodoroTimer.Tick -= OnPomodoroTimerTick;
            _pomodoroTimer = null;
        }

        StopFocusSoundscapeIfActive();
        if (!string.IsNullOrWhiteSpace(_vm.Config.Id))
        {
            CustomWidgetAudioService.Shared.StopTracksForOwner(_vm.Config.Id);
        }
    }

    public void Dispose() => Stop();

    internal bool ManifestUsesPomodoro() =>
        _vm.ManifestInternal.Sources.Any(static s => string.Equals(s.Type, "pomodoro", StringComparison.OrdinalIgnoreCase)) ||
        NodeTreeReferencesPomodoro(_vm.ManifestInternal.View) ||
        CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "pomodoro.");

    internal static bool NodeTreeReferencesPomodoro(CustomWidgetNodeSpec node) =>
        CustomWidgetViewModel.NodeTreeReferencesSubstring(node, "$pomodoro.") ||
        CustomWidgetViewModel.NodeTreeReferencesSubstring(node, "$pomo.");

    internal void UpdatePomodoroTimerSubscription()
    {
        bool isRunning = CustomWidgetPomodoroEngine.HasAnyRunningTrack(_vm.StateInternal);
        bool shouldTickRunning = !_vm.IsDisposedInternal &&
                                 !_vm.IsStudioGalleryThumbnailPreview &&
                                 isRunning;
        bool shouldWatchIdleRollover = !shouldTickRunning &&
                                       !_vm.IsDisposedInternal &&
                                       !_vm.IsPowerSuspendedInternal &&
                                       !_vm.IsStudioGalleryThumbnailPreview &&
                                       ManifestUsesPomodoro();

        if (shouldTickRunning || shouldWatchIdleRollover)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (_lastProjectedDayKey != default)
            {
                EnsureDailyRollover(now);
            }
            _ = CaptureAndDetectProjectedStateChange(now);

            if (_pomodoroTimer is null && _vm.DispatcherQueueInternal is not null)
            {
                _pomodoroTimer = _vm.DispatcherQueueInternal.CreateTimer();
                _pomodoroTimer.IsRepeating = true;
                _pomodoroTimer.Tick += OnPomodoroTimerTick;
            }

            if (_pomodoroTimer is not null)
            {
                if (shouldTickRunning)
                {
                    bool needsUiTick = _vm.IsWindowVisibleInternal &&
                                       (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "pomodoro."));
                    TimeSpan targetInterval = needsUiTick
                        ? TimeSpan.FromSeconds(1)
                        : ComputePomodoroSleepInterval();
                    if (_pomodoroTimer.Interval != targetInterval || !_needsUiTickMatchesCurrent(needsUiTick, _pomodoroTimer.Interval))
                    {
                        _pomodoroTimer.Interval = targetInterval;
                    }
                    if (!_pomodoroTimer.IsRunning || !needsUiTick)
                    {
                        _pomodoroTimer.Start();
                    }
                }
                else
                {
                    TimeSpan idleInterval = CountdownCapabilityProvider.ComputeWakeResilientMidnightDelay(now);
                    if (_pomodoroTimer.Interval != idleInterval)
                    {
                        _pomodoroTimer.Interval = idleInterval;
                    }
                    if (!_pomodoroTimer.IsRunning)
                    {
                        _pomodoroTimer.Start();
                    }
                }
            }
        }
        else if (_pomodoroTimer is not null)
        {
            _pomodoroTimer.Stop();
            _pomodoroTimer.Tick -= OnPomodoroTimerTick;
            _pomodoroTimer = null;
        }

        SyncFocusSoundscape();
    }

    private double ResolveDesiredSoundscapeVolume()
    {
        if (_vm.StateInternal.TryGetValue("soundVolume", out string? rawVol) &&
            double.TryParse(rawVol, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVol) &&
            double.IsFinite(parsedVol))
        {
            return parsedVol > 1.0
                ? Math.Clamp(parsedVol / 100.0, 0.0, 1.0)
                : Math.Clamp(parsedVol, 0.0, 1.0);
        }

        return 0.35;
    }

    private void SyncFocusSoundscape()
    {
        string? desiredMode = null;
        if (!_vm.IsDisposedInternal &&
            !_vm.IsPreviewOnlyInternal &&
            !_vm.IsStudioGalleryThumbnailPreview &&
            CustomWidgetPomodoroEngine.IsPomodoroRunning(_vm.StateInternal))
        {
            string phase = CustomWidgetPomodoroEngine.NormalizePhase(
                _vm.StateInternal.TryGetValue(CustomWidgetPomodoroEngine.KeyPhase, out string? rawPhase)
                    ? rawPhase
                    : CustomWidgetPomodoroEngine.PhaseFocus);
            if (string.Equals(phase, CustomWidgetPomodoroEngine.PhaseFocus, StringComparison.Ordinal) &&
                _vm.SettingsInternal.TryGetValue("focusSoundscape", out string? rawScape) &&
                !string.IsNullOrWhiteSpace(rawScape))
            {
                string candidate = rawScape.Trim().ToLowerInvariant();
                if (candidate is "rain" or "pink" or "brown")
                {
                    desiredMode = candidate;
                }
            }
        }

        double desiredVolume = ResolveDesiredSoundscapeVolume();
        string trackId = string.IsNullOrWhiteSpace(_vm.Config.Id)
            ? "ambient:default"
            : $"ambient:{_vm.Config.Id.Trim()}";

        if (string.Equals(_activeFocusSoundscape, desiredMode, StringComparison.Ordinal))
        {
            if (desiredMode is not null && Math.Abs(_activeFocusSoundscapeVolume - desiredVolume) > 0.005)
            {
                _activeFocusSoundscapeVolume = desiredVolume;
                CustomWidgetAudioService.Shared.SetVolume(trackId, desiredVolume);
            }
            return;
        }

        if (desiredMode is null)
        {
            StopFocusSoundscapeIfActive();
            return;
        }

        _activeFocusSoundscape = desiredMode;
        _activeFocusSoundscapeVolume = desiredVolume;
        _ = CustomWidgetAudioService.Shared.PlayTrack(
            trackId,
            $"noise:{desiredMode}",
            volume: desiredVolume,
            loop: true,
            ambientOwnerId: _vm.Config.Id);
    }

    private void StopFocusSoundscapeIfActive()
    {
        if (_activeFocusSoundscape is null)
        {
            return;
        }

        _activeFocusSoundscape = null;
        CustomWidgetAudioService.Shared.StopTracksForOwner(_vm.Config.Id);
    }

    private (string Phase, string Status, string SecondaryStatus, int StageBucket, int CompletedSessions, DateOnly DayKey) ReadProjectedState(DateTimeOffset now)
    {
        DateOnly dayKey = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        CustomWidgetPomodoroSnapshot snap = CustomWidgetPomodoroEngine.ComputeSnapshot(
            _vm.StateInternal,
            _vm.SettingsInternal,
            now,
            _vm.ActiveCultureName);

        string status;
        if (string.Equals(snap.Mode, CustomWidgetPomodoroEngine.ModeSedentary, StringComparison.Ordinal))
        {
            status = !snap.IsRunning
                ? "idle"
                : snap.IsAwaySuspended
                    ? "away"
                    : snap.IsDue
                        ? "due"
                        : "running";
        }
        else
        {
            status = snap.IsRunning
                ? "running"
                : (snap.IsPaused ? "paused" : "idle");
        }

        string secondaryStatus =
            $"{(snap.SedentaryIsRunning ? "1" : "0")}:{(snap.SedentaryIsAway ? "1" : "0")}:{(snap.SedentaryIsDue ? "1" : "0")}:{(snap.PomodoroIsRunning ? "1" : "0")}";

        int stageBucket;
        if (string.Equals(snap.Mode, CustomWidgetPomodoroEngine.ModeSedentary, StringComparison.Ordinal))
        {
            stageBucket = snap.ProgressPercent >= 80.0 ? 1 : 0;
        }
        else if (string.Equals(snap.Phase, CustomWidgetPomodoroEngine.PhaseFocus, StringComparison.Ordinal))
        {
            stageBucket = snap.ProgressPercent < 25.0
                ? 0
                : (snap.ProgressPercent < 60.0
                    ? 1
                    : (snap.ProgressPercent < 90.0 ? 2 : 3));
        }
        else
        {
            stageBucket = 0;
        }

        int completedSessions = string.Equals(snap.Mode, CustomWidgetPomodoroEngine.ModeSedentary, StringComparison.Ordinal)
            ? snap.StoodCount
            : snap.TodayCompleted;

        return (snap.Phase, status, secondaryStatus, stageBucket, completedSessions, dayKey);
    }

    private bool CaptureAndDetectProjectedStateChange(DateTimeOffset now)
    {
        (string phase, string status, string secondaryStatus, int stageBucket, int completedSessions, DateOnly dayKey) = ReadProjectedState(now);
        bool changed = !string.Equals(_lastProjectedPhase, phase, StringComparison.Ordinal) ||
                       !string.Equals(_lastProjectedStatus, status, StringComparison.Ordinal) ||
                       !string.Equals(_lastProjectedSecondaryStatus, secondaryStatus, StringComparison.Ordinal) ||
                       _lastProjectedStageBucket != stageBucket ||
                       _lastProjectedCompletedSessions != completedSessions ||
                       _lastProjectedDayKey != dayKey;

        _lastProjectedPhase = phase;
        _lastProjectedStatus = status;
        _lastProjectedSecondaryStatus = secondaryStatus;
        _lastProjectedStageBucket = stageBucket;
        _lastProjectedCompletedSessions = completedSessions;
        _lastProjectedDayKey = dayKey;
        return changed;
    }

    internal bool EnsureDailyRollover(DateTimeOffset now)
    {
        DateOnly todayKey = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        string todayIso = todayKey.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool hasStaleRecordedDate =
            _vm.StateInternal.TryGetValue(CustomWidgetPomodoroEngine.KeyTodayDate, out string? recordedDate) &&
            !string.IsNullOrWhiteSpace(recordedDate) &&
            !string.Equals(recordedDate, todayIso, StringComparison.Ordinal);
        bool observedDayChanged = _lastProjectedDayKey != default && todayKey != _lastProjectedDayKey;
        if (!hasStaleRecordedDate && !observedDayChanged)
        {
            return false;
        }

        bool storeMutated = CustomWidgetPomodoroEngine.EnsureTodayRollover(
            _vm.StateInternal,
            _vm.SettingsInternal,
            now);
        _ = CaptureAndDetectProjectedStateChange(now);
        if (storeMutated)
        {
            _ = _vm.PersistStoreAsync();
        }
        if (_vm.CompiledScriptInternal is not null)
        {
            _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
        }
        _vm.RefreshCapsuleProjection(bumpLiveState: true);
        _vm.NotifyMetricsRefreshed();
        return true;
    }

    internal static bool _needsUiTickMatchesCurrent(bool needsUiTick, TimeSpan currentInterval) =>
        !needsUiTick || currentInterval == TimeSpan.FromSeconds(1);

    /// <summary>
    /// Seconds without any keyboard/mouse input session-wide (<c>user32.dll!GetLastInputInfo</c>
    /// via <see cref="Win32Helper.TryGetLastInputTickCount"/>). Probed only inside the pomodoro
    /// 1-second heartbeat while a sedentary-guardian round is running — no global hooks.
    /// </summary>
    internal static double? ProbeKeyboardMouseIdleSeconds()
    {
        if (Win32Helper.TryGetLastInputTickCount(out uint lastInputTickCount))
        {
            uint idleMilliseconds = unchecked((uint)Environment.TickCount - lastInputTickCount);
            return idleMilliseconds / 1000.0;
        }

        return null;
    }

    internal TimeSpan ComputePomodoroSleepInterval()
    {
        bool sedentaryMonitoring = CustomWidgetPomodoroEngine.IsSedentaryRunning(_vm.StateInternal);
        if (sedentaryMonitoring &&
            (CustomWidgetPomodoroEngine.IsSedentaryAway(_vm.StateInternal) ||
             CustomWidgetPomodoroEngine.IsSedentaryDue(_vm.StateInternal)))
        {
            return TimeSpan.FromSeconds(15);
        }

        long minRemainingTicks = long.MaxValue;
        long nowUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

        if (CustomWidgetPomodoroEngine.IsPomodoroRunning(_vm.StateInternal) &&
            _vm.StateInternal.TryGetValue(CustomWidgetPomodoroEngine.KeyEndUtcTicks, out string? pomoEndRaw) &&
            long.TryParse(pomoEndRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long pomoEndTicks) &&
            pomoEndTicks > 0)
        {
            minRemainingTicks = Math.Min(minRemainingTicks, pomoEndTicks - nowUtcTicks);
        }

        if (sedentaryMonitoring &&
            _vm.StateInternal.TryGetValue(CustomWidgetPomodoroEngine.KeySedentaryEndUtcTicks, out string? sedEndRaw) &&
            long.TryParse(sedEndRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sedEndTicks) &&
            sedEndTicks > 0)
        {
            minRemainingTicks = Math.Min(minRemainingTicks, sedEndTicks - nowUtcTicks);
        }

        if (minRemainingTicks < long.MaxValue)
        {
            if (minRemainingTicks <= TimeSpan.FromMilliseconds(250).Ticks)
            {
                return TimeSpan.FromMilliseconds(250);
            }

            long maxSleepTicks = (sedentaryMonitoring ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5)).Ticks;
            return TimeSpan.FromTicks(Math.Min(minRemainingTicks, maxSleepTicks));
        }

        return TimeSpan.FromSeconds(1);
    }

    internal void OnPomodoroTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_vm.IsDisposedInternal)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if (!CustomWidgetPomodoroEngine.HasAnyRunningTrack(_vm.StateInternal))
        {
            EnsureDailyRollover(now);
            SyncFocusSoundscape();
            TimeSpan idleDelay = CountdownCapabilityProvider.ComputeWakeResilientMidnightDelay(now);
            if (sender.Interval != idleDelay)
            {
                sender.Interval = idleDelay;
            }
            if (!sender.IsRunning)
            {
                sender.Start();
            }
            return;
        }

        if (CustomWidgetPomodoroEngine.TryPumpForMode(
                _vm.StateInternal,
                _vm.SettingsInternal,
                _vm.CollectionsInternal,
                now,
                CustomWidgetPomodoroEngine.IsSedentaryRunning(_vm.StateInternal) ? ProbeKeyboardMouseIdleSeconds() : null,
                out CustomWidgetPomodoroCompletionNotice? notice,
                _vm.ActiveCultureName))
        {
            _vm.BumpCollectionsVersion();
            UpdatePomodoroTimerSubscription();
            _ = CaptureAndDetectProjectedStateChange(now);
            if (_vm.CompiledScriptInternal is not null)
            {
                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            }
            _ = _vm.PersistStoreAsync();
            _vm.RefreshCapsuleProjection(bumpLiveState: true);
            _vm.NotifyViewRebuildRequested();

            if (notice is not null)
            {
                DispatchPomodoroCompletionNotification(notice);
            }
            return;
        }

        bool rolledOverAtMidnight = EnsureDailyRollover(now);
        SyncFocusSoundscape();

        bool needsUiTick = _vm.IsWindowVisibleInternal &&
                           (!_vm.IsCompactCollapsedInternal || CustomWidgetViewModel.CapsuleReferencesToken(_vm.ManifestInternal.Capsule, "pomodoro."));
        if (!needsUiTick)
        {
            UpdatePomodoroTimerSubscription();
            return;
        }

        if (sender.Interval != TimeSpan.FromSeconds(1))
        {
            sender.Interval = TimeSpan.FromSeconds(1);
        }

        if (!rolledOverAtMidnight)
        {
            if (CaptureAndDetectProjectedStateChange(now) && _vm.CompiledScriptInternal is not null)
            {
                _vm.TryRunScriptLifecycleHook("onInit", CustomWidgetScriptEngine.DefaultInitOrSourceTimeoutMs, persistIfMutated: false);
            }
            _vm.RefreshCapsuleProjection(bumpLiveState: false);
            _vm.NotifyMetricsRefreshed();
        }
    }

    internal void DispatchPomodoroCompletionNotification(CustomWidgetPomodoroCompletionNotice notice)
    {
        _vm.RaiseFeedback(notice.Title, WidgetFeedbackSeverity.Info);

        if (!_vm.IsPermissionAllowed(CustomWidgetPermissionKind.Notifications) ||
            string.Equals(notice.NotifyMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            bool muteAudio = string.Equals(notice.NotifyMode, "toast_silent", StringComparison.OrdinalIgnoreCase);
            string shortId = _vm.Config.Id.Length > 8 ? _vm.Config.Id[..8] : _vm.Config.Id;
            _vm.NotificationServiceProviderInternal?.Invoke()?.TryShow(
                notice.Title,
                notice.Message,
                arguments: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "custom-pomodoro",
                    ["widgetId"] = _vm.Config.Id
                },
                options: new NativeAppNotificationOptions(
                    Tag: $"pomo-{shortId}",
                    Group: "CustomPomodoro",
                    MuteAudio: muteAudio));
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Pomodoro toast notification skipped: {ex.Message}");
        }
    }

}
