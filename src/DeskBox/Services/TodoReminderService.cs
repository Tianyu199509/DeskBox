using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Contracts;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed record TodoReminderNotification(
    string Title,
    string Message,
    int Count,
    string? WidgetId = null,
    string? ItemId = null,
    bool HasTodayDueItem = false);

public sealed class TodoReminderService : IDisposable, ITodoReminderSession
{
    internal static readonly TimeSpan MinimumWakeupDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumWakeupDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MissedReminderGrace = TimeSpan.FromMinutes(1);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Action<TodoReminderNotification> _notify;
    private readonly Func<string, TodoWidgetStore> _storeFactory;
    private readonly Func<DateTimeOffset> _clock;

    // Deep review R15 (F5 audit): the reminder service and the widget VM are
    // two uncoordinated full-file writers of todo.json — a notification-side
    // mutation racing a live VM save silently rolled the other's change back.
    // When the widget is materialized, route reminder mutations through its
    // single-writer VM instead of the store.
    private readonly Func<string, TodoWidgetViewModel?>? _tryGetLiveViewModel;
    private readonly HashSet<string> _sessionNotifiedKeys = new(StringComparer.Ordinal);

    private int _wakeupSchedulePending;

    private DispatcherQueueTimer? _timer;
    private bool _isChecking;
    private bool _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _initialCheck = Task.CompletedTask;
    private Task<int> _activeCheck = Task.FromResult(0);

    private enum ReminderTriggerKind
    {
        Due,
        Snooze
    }

    public TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Action<TodoReminderNotification> notify,
        Func<string, TodoWidgetViewModel?>? tryGetLiveViewModel = null)
        : this(
            settingsService,
            localizationService,
            dispatcherQueue,
            notify,
            widgetId => new TodoWidgetStore(widgetId),
            () => DateTimeOffset.Now,
            tryGetLiveViewModel)
    {
    }

    internal TodoReminderService(
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue? dispatcherQueue,
        Action<TodoReminderNotification> notify,
        Func<string, TodoWidgetStore> storeFactory,
        Func<DateTimeOffset> clock,
        Func<string, TodoWidgetViewModel?>? tryGetLiveViewModel = null)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dispatcherQueue = dispatcherQueue;
        _notify = notify;
        _storeFactory = storeFactory;
        _clock = clock;
        _tryGetLiveViewModel = tryGetLiveViewModel;
        TodoWidgetStore.StoreChanged += OnTodoStoreChanged;
    }

    public void Start()
    {
        if (_disposed || _dispatcherQueue is null || _timer is not null)
        {
            return;
        }

        if (!ShouldBeRunning())
        {
            return;
        }

        // Clear notified keys from any previous session so that reminders
        // that were already shown before the app restarted can fire again.
        _sessionNotifiedKeys.Clear();

        _timer = _dispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += Timer_Tick;

        _initialCheck = RunDelayedInitialCheckAsync(_lifetime.Token);
    }

    /// <summary>
    /// Called when settings change. Starts or stops the timer based on whether
    /// the Todo widget and reminder feature are enabled.
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (ShouldBeRunning())
        {
            if (_timer is null && _dispatcherQueue is not null)
            {
                // Clear notified keys when the timer is being (re)started
                // after being stopped — old keys from a previous active
                // period are no longer relevant.
                _sessionNotifiedKeys.Clear();
                _timer = _dispatcherQueue.CreateTimer();
                _timer.IsRepeating = false;
                _timer.Tick += Timer_Tick;
            }

            RequestWakeupReschedule();
        }
        else if (_timer is not null)
        {
            _timer.Tick -= Timer_Tick;
            _timer.Stop();
            _timer = null;
            // Clear notified keys when the feature is disabled so they
            // don't accumulate indefinitely while inactive.
            _sessionNotifiedKeys.Clear();
        }
    }

    private bool ShouldBeRunning()
    {
        var settings = _settingsService.Settings;
        return settings.TodoReminderEnabled &&
               FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
    }

    public Task<int> CheckNowAsync(DateTimeOffset now)
    {
        if (_disposed || _isChecking)
        {
            return Task.FromResult(0);
        }

        return _activeCheck = CheckCoreAsync(now);
    }

    private async Task<int> CheckCoreAsync(DateTimeOffset now)
    {
        _isChecking = true;
        try
        {
            var settings = _settingsService.Settings;
            if (!settings.TodoReminderEnabled ||
                !FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
            {
                return 0;
            }

            int defaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                settings.TodoDefaultReminderOffsetMinutes);
            bool allDayRemindersEnabled = settings.Todo.TodoAllDayReminderEnabled;
            int allDayReminderTimeMinutes = SettingsService.NormalizeTodoAllDayReminderTimeMinutes(
                settings.Todo.TodoAllDayReminderTimeMinutes);
            var widgets = settings.Widgets
                .Where(widget =>
                    widget.WidgetKind == WidgetKind.Todo &&
                    !widget.IsDisabled &&
                    !settings.DeletedWidgetIds.Contains(widget.Id))
                .ToList();

            if (widgets.Count == 0)
            {
                StopWakeupTimerOnUiThread();
                return 0;
            }

            List<TodoReminderCandidate> candidates = [];
            DateTimeOffset? earliestNextWakeup = null;
            foreach (var widget in widgets)
            {
                if (_disposed || !ShouldBeRunning()) return 0;
                DateTimeOffset? widgetNextWakeup = await CollectWidgetCandidatesAsync(
                    widget,
                    now,
                    defaultOffsetMinutes,
                    allDayRemindersEnabled,
                    allDayReminderTimeMinutes,
                    candidates);
                if (widgetNextWakeup is { } wakeupAt &&
                    (earliestNextWakeup is null || wakeupAt < earliestNextWakeup.Value))
                {
                    earliestNextWakeup = wakeupAt;
                }
            }

            if (!_disposed && ShouldBeRunning())
            {
                ApplyNextWakeupSchedule(now, earliestNextWakeup);
            }

            if (candidates.Count == 0 || _disposed || !ShouldBeRunning())
            {
                return 0;
            }

            _notify(BuildNotification(candidates));
            return candidates.Count;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Check failed: {ex}");
            return 0;
        }
        finally
        {
            _isChecking = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TodoWidgetStore.StoreChanged -= OnTodoStoreChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_timer is not null)
        {
            _timer.Tick -= Timer_Tick;
            _timer.Stop();
            _timer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.WhenAll(_initialCheck, _activeCheck);
    }

    internal static bool ShouldNotify(TodoItem item, DateTimeOffset now, TimeSpan reminderOffset)
    {
        return ShouldNotifyDue(item, now, reminderOffset);
    }

    internal static bool ShouldNotify(
        TodoItem item,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        bool allDayRemindersEnabled = true,
        int allDayReminderTimeMinutes = TodoOptionKinds.DefaultAllDayReminderTimeMinutes)
    {
        return TryGetReminderTrigger(
            item,
            now,
            defaultOffsetMinutes,
            allDayRemindersEnabled,
            allDayReminderTimeMinutes,
            out _,
            out _);
    }

    public async Task<bool> SnoozeAsync(string? widgetId, string? itemId, TimeSpan snoozeFor)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            snoozeFor <= TimeSpan.Zero)
        {
            return false;
        }

        return await SnoozeUntilAsync(widgetId, itemId, _clock().Add(snoozeFor));
    }

    public async Task<bool> SnoozeUntilAsync(string? widgetId, string? itemId, DateTimeOffset snoozedUntil)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId) ||
            snoozedUntil <= _clock())
        {
            return false;
        }

        try
        {
            if (!TryGetTodoReminderWidget(widgetId, requireReminderEnabled: true, out WidgetConfig? widget))
            {
                return false;
            }

            // Single-writer routing (see CompleteAsync).
            if (_tryGetLiveViewModel?.Invoke(widget.Id) is { } liveViewModel)
            {
                bool routed = await liveViewModel.SnoozeReminderUntilAsync(
                    itemId,
                    snoozedUntil,
                    _clock(),
                    rejectReminderOff: true);
                if (routed)
                {
                    App.Log($"[TodoReminder] Snoozed via live VM widget={widgetId} item={itemId} until={snoozedUntil:O}");
                }

                return routed;
            }

            var store = _storeFactory(widget.Id);
            var data = await store.LoadAsync();
            var item = data.Items.FirstOrDefault(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (item is null ||
                item.IsCompleted ||
                item.DueDate is null ||
                TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
            {
                return false;
            }

            item.SnoozedUntil = snoozedUntil;
            item.SnoozeLastNotifiedAt = null;
            item.ReminderDismissedForDueDate = item.DueDate;
            item.UpdatedAt = _clock().ToUniversalTime();
            await store.SaveAsync(data);
            App.Log($"[TodoReminder] Snoozed widget={widgetId} item={itemId} until={item.SnoozedUntil:O}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Snooze failed: {ex}");
            return false;
        }
    }

    public async Task<bool> CompleteAsync(string? widgetId, string? itemId)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(widgetId) ||
            string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        try
        {
            if (!TryGetTodoReminderWidget(widgetId, requireReminderEnabled: false, out WidgetConfig? widget))
            {
                return false;
            }

            // Single-writer routing (deep review R15, F5): when the widget
            // VM is live, its in-memory graph is the freshest state and its
            // save pipeline is the only writer — mutate through it so a
            // concurrent VM save cannot roll this completion back.
            if (_tryGetLiveViewModel?.Invoke(widget.Id) is { } liveViewModel)
            {
                bool routed = await liveViewModel.SetCompletedAsync(itemId, isCompleted: true);
                if (routed)
                {
                    App.Log($"[TodoReminder] Completed via live VM widget={widgetId} item={itemId}");
                }

                return routed;
            }

            var store = _storeFactory(widget.Id);
            var data = await store.LoadAsync();
            int itemIndex = data.Items.FindIndex(item =>
                string.Equals(item.Id, itemId, StringComparison.Ordinal));
            if (itemIndex < 0)
            {
                return false;
            }

            var item = data.Items[itemIndex];
            if (item.IsCompleted)
            {
                return true;
            }

            DateTimeOffset now = _clock().ToUniversalTime();
            if (item.Recurrence is not null)
            {
                item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
            }

            item.IsCompleted = true;
            item.CompletedAt = now;
            item.UpdatedAt = now;
            item.GeneratedNextItemId = null;
            item.SnoozedUntil = null;
            item.SnoozeLastNotifiedAt = null;

            if (TodoRecurrenceService.TryCreateNextOccurrence(item, now, out TodoItem? nextItem) &&
                nextItem is not null)
            {
                item.GeneratedNextItemId = nextItem.Id;
                data.Items.Insert(Math.Clamp(itemIndex + 1, 0, data.Items.Count), nextItem);
            }

            await store.SaveAsync(data);
            App.Log($"[TodoReminder] Completed from notification widget={widgetId} item={itemId}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Complete failed: {ex}");
            return false;
        }
    }

    private async Task RunDelayedInitialCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken);
            await CheckNowAsync(_clock());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Initial check failed: {ex}");
        }
    }

    private bool TryGetTodoReminderWidget(
        string widgetId,
        bool requireReminderEnabled,
        out WidgetConfig widget)
    {
        widget = null!;
        var settings = _settingsService.Settings;
        if ((requireReminderEnabled && !settings.TodoReminderEnabled) ||
            !FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo))
        {
            return false;
        }

        var match = settings.Widgets.FirstOrDefault(entry =>
            entry.WidgetKind == WidgetKind.Todo &&
            !entry.IsDisabled &&
            !settings.DeletedWidgetIds.Contains(entry.Id) &&
            string.Equals(entry.Id, widgetId, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        widget = match;
        return true;
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        await CheckNowAsync(_clock());
    }

    private void OnTodoStoreChanged(string storePath, string widgetId)
    {
        if (_disposed || _dispatcherQueue is null)
        {
            return;
        }

        RequestWakeupReschedule();
    }

    private void RequestWakeupReschedule()
    {
        if (_disposed || _dispatcherQueue is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _wakeupSchedulePending, 1) != 0)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            Interlocked.Exchange(ref _wakeupSchedulePending, 0);
            if (_disposed || !ShouldBeRunning())
            {
                StopWakeupTimerOnUiThread();
                return;
            }

            await RefreshWakeupScheduleOnlyAsync(_clock());
        }))
        {
            Interlocked.Exchange(ref _wakeupSchedulePending, 0);
        }
    }

    private async Task RefreshWakeupScheduleOnlyAsync(DateTimeOffset now)
    {
        if (_disposed || _isChecking || !ShouldBeRunning())
        {
            return;
        }

        try
        {
            var settings = _settingsService.Settings;
            int defaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                settings.Todo.TodoDefaultReminderOffsetMinutes);
            bool allDayRemindersEnabled = settings.Todo.TodoAllDayReminderEnabled;
            int allDayReminderTimeMinutes = SettingsService.NormalizeTodoAllDayReminderTimeMinutes(
                settings.Todo.TodoAllDayReminderTimeMinutes);
            var widgets = settings.WidgetLayout.Widgets
                .Where(widget =>
                    widget.WidgetKind == WidgetKind.Todo &&
                    !widget.IsDisabled &&
                    !settings.WidgetLayout.DeletedWidgetIds.Contains(widget.Id))
                .ToList();

            DateTimeOffset? earliestNextWakeup = null;
            foreach (WidgetConfig widget in widgets)
            {
                if (_disposed || !ShouldBeRunning())
                {
                    return;
                }

                IEnumerable<TodoItem> items;
                if (_tryGetLiveViewModel?.Invoke(widget.Id) is { IsInitialized: true, IsDisposed: false } liveViewModel)
                {
                    items = liveViewModel.Items.Select(vm => vm.Item);
                }
                else
                {
                    TodoWidgetData snapshot = await _storeFactory(widget.Id).LoadReadOnlySnapshotAsync();
                    items = snapshot.Items;
                }

                DateTimeOffset? widgetWakeup = FindEarliestNextWakeup(
                    widget.Id,
                    items,
                    now,
                    defaultOffsetMinutes,
                    _sessionNotifiedKeys,
                    allDayRemindersEnabled,
                    allDayReminderTimeMinutes);
                if (widgetWakeup is { } wakeupAt &&
                    (earliestNextWakeup is null || wakeupAt < earliestNextWakeup.Value))
                {
                    earliestNextWakeup = wakeupAt;
                }
            }

            if (!_disposed && ShouldBeRunning())
            {
                ApplyNextWakeupSchedule(now, earliestNextWakeup);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoReminder] Wakeup schedule refresh failed: {ex.Message}");
        }
    }

    private void ApplyNextWakeupSchedule(DateTimeOffset now, DateTimeOffset? nextWakeupAt)
    {
        if (_dispatcherQueue is null || _timer is null)
        {
            return;
        }

        void UpdateTimer()
        {
            if (_disposed || _timer is null || !ShouldBeRunning())
            {
                _timer?.Stop();
                return;
            }

            TimeSpan? delay = ComputeClampedWakeupDelay(now, nextWakeupAt);
            if (delay is null)
            {
                _timer.Stop();
                return;
            }

            _timer.Stop();
            _timer.Interval = delay.Value;
            _timer.Start();
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            UpdateTimer();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(UpdateTimer);
        }
    }

    private void StopWakeupTimerOnUiThread()
    {
        if (_dispatcherQueue is null || _timer is null)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            _timer.Stop();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => _timer?.Stop());
        }
    }

    internal static TimeSpan? ComputeClampedWakeupDelay(DateTimeOffset now, DateTimeOffset? nextWakeupAt)
    {
        if (nextWakeupAt is not { } target)
        {
            return null;
        }

        TimeSpan rawDelay = target - now;
        if (rawDelay <= MinimumWakeupDelay)
        {
            return MinimumWakeupDelay;
        }

        return rawDelay > MaximumWakeupDelay
            ? MaximumWakeupDelay
            : rawDelay;
    }

    internal static DateTimeOffset? FindEarliestNextWakeup(
        string widgetId,
        IEnumerable<TodoItem> items,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        IReadOnlySet<string>? notifiedKeys = null,
        bool allDayRemindersEnabled = true,
        int allDayReminderTimeMinutes = TodoOptionKinds.DefaultAllDayReminderTimeMinutes)
    {
        int normalizedDefaultOffset = SettingsService.NormalizeTodoReminderOffsetMinutes(defaultOffsetMinutes);
        int normalizedAllDayMinutes = SettingsService.NormalizeTodoAllDayReminderTimeMinutes(allDayReminderTimeMinutes);
        DateTimeOffset? earliest = null;
        foreach (TodoItem item in items)
        {
            if (!TryGetItemNextWakeup(
                    widgetId,
                    item,
                    now,
                    normalizedDefaultOffset,
                    allDayRemindersEnabled,
                    normalizedAllDayMinutes,
                    notifiedKeys,
                    out DateTimeOffset wakeupAt))
            {
                continue;
            }

            if (item.DueDate is { } itemDueDate &&
                TodoRecurrenceService.IsDeferredOccurrence(item, now.ToLocalTime().Date))
            {
                DateTimeOffset unlockAt = ComputeAllDayReminderAt(
                    itemDueDate,
                    normalizedAllDayReminderTimeMinutes: 0,
                    itemReminderOffsetMinutes: 0,
                    out _);
                if (wakeupAt < unlockAt)
                {
                    wakeupAt = unlockAt;
                }
            }

            if (earliest is null || wakeupAt < earliest.Value)
            {
                earliest = wakeupAt;
            }
        }

        return earliest;
    }

    private static bool TryGetItemNextWakeup(
        string widgetId,
        TodoItem item,
        DateTimeOffset now,
        int normalizedDefaultOffsetMinutes,
        bool allDayRemindersEnabled,
        int normalizedAllDayReminderTimeMinutes,
        IReadOnlySet<string>? notifiedKeys,
        out DateTimeOffset wakeupAt)
    {
        wakeupAt = default;
        if (item.IsDeleted || item.IsCompleted || item.DueDate is not { } dueDate)
        {
            return false;
        }

        if (TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
        {
            return false;
        }

        if (item.SnoozedUntil is { } snoozedUntil)
        {
            if (snoozedUntil > now)
            {
                wakeupAt = snoozedUntil;
                return true;
            }

            string snoozeKey = GetReminderKey(widgetId, item, ReminderTriggerKind.Snooze, effectiveOffsetMinutes: null);
            if (notifiedKeys is null || !notifiedKeys.Contains(snoozeKey))
            {
                wakeupAt = now;
                return true;
            }

            return false;
        }

        if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
            DateTimeOffset.Equals(dismissedForDueDate, dueDate))
        {
            return false;
        }

        if (!item.HasDueTime)
        {
            if (!allDayRemindersEnabled)
            {
                return false;
            }

            DateTimeOffset allDayReminderAt = ComputeAllDayReminderAt(
                dueDate,
                normalizedAllDayReminderTimeMinutes,
                item.ReminderOffsetMinutes,
                out int allDayKeyMinutes);
            if (allDayReminderAt > now)
            {
                wakeupAt = allDayReminderAt;
                return true;
            }

            if (CanTriggerAllDayReminderNow(item, dueDate, allDayReminderAt, now))
            {
                string dueKey = GetReminderKey(widgetId, item, ReminderTriggerKind.Due, allDayKeyMinutes);
                if (notifiedKeys is null || !notifiedKeys.Contains(dueKey))
                {
                    wakeupAt = now;
                    return true;
                }
            }

            return false;
        }

        int? effectiveOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes) ??
                                      normalizedDefaultOffsetMinutes;
        if (TodoReminderOptions.IsReminderOff(effectiveOffsetMinutes))
        {
            return false;
        }

        DateTimeOffset reminderAt = dueDate - TimeSpan.FromMinutes(Math.Max(0, effectiveOffsetMinutes.Value));
        if (reminderAt > now)
        {
            wakeupAt = reminderAt;
            return true;
        }

        if (now <= dueDate + MissedReminderGrace)
        {
            string dueKey = GetReminderKey(widgetId, item, ReminderTriggerKind.Due, effectiveOffsetMinutes);
            if (notifiedKeys is null || !notifiedKeys.Contains(dueKey))
            {
                wakeupAt = now;
                return true;
            }
        }

        return false;
    }

    private async Task<DateTimeOffset?> CollectWidgetCandidatesAsync(
        WidgetConfig widget,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        bool allDayRemindersEnabled,
        int allDayReminderTimeMinutes,
        List<TodoReminderCandidate> candidates)
    {
        var store = _storeFactory(widget.Id);
        if (_tryGetLiveViewModel?.Invoke(widget.Id) is { IsInitialized: true, IsDisposed: false } liveViewModel)
        {
            if (_disposed || !ShouldBeRunning())
            {
                return null;
            }

            bool liveChanged = false;
            foreach (TodoItemViewModel itemVm in liveViewModel.Items)
            {
                TodoItem item = itemVm.Item;
                if (!TryGetReminderTrigger(
                        item,
                        now,
                        defaultOffsetMinutes,
                        allDayRemindersEnabled,
                        allDayReminderTimeMinutes,
                        out ReminderTriggerKind triggerKind,
                        out int? effectiveOffsetMinutes))
                {
                    continue;
                }

                string reminderKey = GetReminderKey(widget.Id, item, triggerKind, effectiveOffsetMinutes);
                if (!_sessionNotifiedKeys.Add(reminderKey))
                {
                    continue;
                }

                if (triggerKind == ReminderTriggerKind.Snooze)
                {
                    item.SnoozeLastNotifiedAt = now;
                    itemVm.SnoozedUntil = null;
                }
                else
                {
                    item.ReminderLastNotifiedAt = now;
                    item.ReminderDismissedForDueDate = item.DueDate;
                }

                itemVm.UpdatedAt = now.ToUniversalTime();
                liveChanged = true;
                candidates.Add(new TodoReminderCandidate(widget.Id, widget.Name, item));
            }

            if (liveChanged)
            {
                await liveViewModel.SaveAsync();
            }

            return FindEarliestNextWakeup(
                widget.Id,
                liveViewModel.Items.Select(vm => vm.Item),
                now,
                defaultOffsetMinutes,
                _sessionNotifiedKeys,
                allDayRemindersEnabled,
                allDayReminderTimeMinutes);
        }

        TodoWidgetData snapshot = await store.LoadReadOnlySnapshotAsync();
        if (_disposed || !ShouldBeRunning())
        {
            return null;
        }

        List<(int Index, ReminderTriggerKind TriggerKind, int? EffectiveOffsetMinutes, string ReminderKey)>? triggered = null;
        for (int index = 0; index < snapshot.Items.Count; index++)
        {
            TodoItem item = snapshot.Items[index];
            if (item.IsDeleted ||
                !TryGetReminderTrigger(
                    item,
                    now,
                    defaultOffsetMinutes,
                    allDayRemindersEnabled,
                    allDayReminderTimeMinutes,
                    out ReminderTriggerKind triggerKind,
                    out int? effectiveOffsetMinutes))
            {
                continue;
            }

            string reminderKey = GetReminderKey(widget.Id, item, triggerKind, effectiveOffsetMinutes);
            if (_sessionNotifiedKeys.Contains(reminderKey))
            {
                continue;
            }

            triggered ??= [];
            triggered.Add((index, triggerKind, effectiveOffsetMinutes, reminderKey));
        }

        if (triggered is null || triggered.Count == 0)
        {
            return FindEarliestNextWakeup(
                widget.Id,
                snapshot.Items,
                now,
                defaultOffsetMinutes,
                _sessionNotifiedKeys,
                allDayRemindersEnabled,
                allDayReminderTimeMinutes);
        }

        TodoWidgetData mutableData = TodoWidgetStore.CloneData(snapshot);
        foreach ((int index, ReminderTriggerKind triggerKind, _, string reminderKey) in triggered)
        {
            if (!_sessionNotifiedKeys.Add(reminderKey))
            {
                continue;
            }

            TodoItem item = mutableData.Items[index];
            if (triggerKind == ReminderTriggerKind.Snooze)
            {
                item.SnoozeLastNotifiedAt = now;
                item.SnoozedUntil = null;
            }
            else
            {
                item.ReminderLastNotifiedAt = now;
                item.ReminderDismissedForDueDate = item.DueDate;
            }

            candidates.Add(new TodoReminderCandidate(widget.Id, widget.Name, item));
        }

        await store.SaveAsync(mutableData);
        return FindEarliestNextWakeup(
            widget.Id,
            mutableData.Items,
            now,
            defaultOffsetMinutes,
            _sessionNotifiedKeys,
            allDayRemindersEnabled,
            allDayReminderTimeMinutes);
    }

    private TodoReminderNotification BuildNotification(IReadOnlyList<TodoReminderCandidate> candidates)
    {
        var first = candidates
            .OrderBy(candidate => candidate.Item.DueDate)
            .First();
        string title = _localizationService.T("Todo.Reminder.NotificationTitle");
        string dueText = FormatDueDate(first.Item.DueDate!.Value, first.Item.HasDueTime);
        string itemText = NormalizeNotificationText(first.Item.Text);
        string message = candidates.Count == 1
            ? _localizationService.Format("Todo.Reminder.NotificationSingle", itemText, dueText)
            : _localizationService.Format("Todo.Reminder.NotificationMultiple", candidates.Count, itemText, dueText);

        bool hasTodayDueItem = candidates.Any(candidate =>
            candidate.Item.DueDate is { } dueDate &&
            dueDate.ToLocalTime().Date == _clock().Date);

        return new TodoReminderNotification(
            title,
            message,
            candidates.Count,
            first.WidgetId,
            first.Item.Id,
            hasTodayDueItem);
    }

    private string FormatDueDate(DateTimeOffset dueDate, bool hasDueTime)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        var today = _clock().Date;

        if (!hasDueTime)
        {
            if (localDueDate.Date == today)
            {
                return _localizationService.T("Todo.Due.Today");
            }

            if (localDueDate.Date == today.AddDays(1))
            {
                return _localizationService.T("Todo.Due.Tomorrow");
            }

            return localDueDate.ToString("yyyy/M/d");
        }

        string time = localDueDate.Second == 0
            ? localDueDate.ToString("HH:mm")
            : localDueDate.ToString("HH:mm:ss");

        if (localDueDate.Date == today)
        {
            return _localizationService.Format("Todo.Due.TodayAt", time);
        }

        if (localDueDate.Date == today.AddDays(1))
        {
            return _localizationService.Format("Todo.Due.TomorrowAt", time);
        }

        return localDueDate.Second == 0
            ? localDueDate.ToString("yyyy/M/d HH:mm")
            : localDueDate.ToString("yyyy/M/d HH:mm:ss");
    }

    private string NormalizeNotificationText(string? text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty)
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return _localizationService.T("Todo.Reminder.Untitled");
        }

        const int maxLength = 48;
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static bool TryGetReminderTrigger(
        TodoItem item,
        DateTimeOffset now,
        int defaultOffsetMinutes,
        bool allDayRemindersEnabled,
        int allDayReminderTimeMinutes,
        out ReminderTriggerKind triggerKind,
        out int? effectiveOffsetMinutes)
    {
        triggerKind = ReminderTriggerKind.Due;
        effectiveOffsetMinutes = null;

        if (item.IsCompleted ||
            item.DueDate is not { } dueDate ||
            TodoRecurrenceService.IsDeferredOccurrence(item, now.ToLocalTime().Date))
        {
            return false;
        }

        if (TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
        {
            return false;
        }

        if (item.SnoozedUntil is { } snoozedUntil)
        {
            triggerKind = ReminderTriggerKind.Snooze;
            return now >= snoozedUntil;
        }

        if (!item.HasDueTime)
        {
            if (!allDayRemindersEnabled)
            {
                return false;
            }

            int normalizedAllDayMinutes = SettingsService.NormalizeTodoAllDayReminderTimeMinutes(allDayReminderTimeMinutes);
            DateTimeOffset allDayReminderAt = ComputeAllDayReminderAt(
                dueDate,
                normalizedAllDayMinutes,
                item.ReminderOffsetMinutes,
                out int allDayKeyMinutes);
            effectiveOffsetMinutes = allDayKeyMinutes;
            return CanTriggerAllDayReminderNow(item, dueDate, allDayReminderAt, now);
        }

        int normalizedDefaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(defaultOffsetMinutes);
        effectiveOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes) ??
                                 normalizedDefaultOffsetMinutes;
        if (TodoReminderOptions.IsReminderOff(effectiveOffsetMinutes))
        {
            return false;
        }

        return ShouldNotifyDue(
            item,
            now,
            TimeSpan.FromMinutes(Math.Max(0, effectiveOffsetMinutes.Value)));
    }

    private static DateTimeOffset ComputeAllDayReminderAt(
        DateTimeOffset dueDate,
        int normalizedAllDayReminderTimeMinutes,
        int? itemReminderOffsetMinutes,
        out int effectiveKeyMinutes)
    {
        int explicitOffset = Math.Max(0, TodoReminderOptions.NormalizeOffsetMinutes(itemReminderOffsetMinutes) ?? 0);
        effectiveKeyMinutes = normalizedAllDayReminderTimeMinutes - explicitOffset;

        TimeZoneInfo localZone = TimeZoneInfo.Local;
        DateTime unspecifiedLocal = DateTime.SpecifyKind(
            dueDate.ToLocalTime().Date.AddMinutes(normalizedAllDayReminderTimeMinutes),
            DateTimeKind.Unspecified);
        if (localZone.IsInvalidTime(unspecifiedLocal))
        {
            unspecifiedLocal = unspecifiedLocal.AddHours(1);
        }

        TimeSpan offset = localZone.GetUtcOffset(unspecifiedLocal);
        DateTimeOffset baseTrigger = new DateTimeOffset(unspecifiedLocal, offset);
        return baseTrigger - TimeSpan.FromMinutes(explicitOffset);
    }

    private static bool CanTriggerAllDayReminderNow(
        TodoItem item,
        DateTimeOffset dueDate,
        DateTimeOffset reminderAt,
        DateTimeOffset now)
    {
        if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
            DateTimeOffset.Equals(dismissedForDueDate, dueDate))
        {
            return false;
        }

        if (now < reminderAt)
        {
            return false;
        }

        if (now <= reminderAt + MissedReminderGrace)
        {
            return true;
        }

        return now.ToLocalTime().Date == reminderAt.ToLocalTime().Date &&
               item.CreatedAt <= reminderAt;
    }

    private static bool ShouldNotifyDue(TodoItem item, DateTimeOffset now, TimeSpan reminderOffset)
    {
        if (item.IsCompleted ||
            item.DueDate is not { } dueDate ||
            TodoRecurrenceService.IsDeferredOccurrence(item, now.ToLocalTime().Date))
        {
            return false;
        }

        if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
            DateTimeOffset.Equals(dismissedForDueDate, dueDate))
        {
            return false;
        }

        DateTimeOffset reminderAt = dueDate - reminderOffset;
        if (now < reminderAt)
        {
            return false;
        }

        return now <= dueDate + MissedReminderGrace;
    }

    private static string GetReminderKey(
        string widgetId,
        TodoItem item,
        ReminderTriggerKind triggerKind,
        int? effectiveOffsetMinutes)
    {
        string dueKey = item.DueDate?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        string triggerKey = triggerKind == ReminderTriggerKind.Snooze
            ? item.SnoozedUntil?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"
            : effectiveOffsetMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "default";
        return $"{widgetId}:{item.Id}:{triggerKind}:{dueKey}:{triggerKey}";
    }

    private sealed record TodoReminderCandidate(
        string WidgetId,
        string WidgetName,
        TodoItem Item);
}
