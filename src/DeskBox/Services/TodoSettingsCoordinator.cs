using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Owns writes to Todo enablement, reminders, display, and input preferences. App
/// supplies the window/reminder host callbacks once.
/// </summary>
public sealed class TodoSettingsCoordinator : ITodoSettings
{
    private readonly SettingsService _settings;
    private readonly Func<bool, Task>? _setWidgetEnabled;
    private readonly Action<bool> _refreshReminders;
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _updatingPreferences;
    private bool _stopped;
    private Task? _stopTask;

    internal bool IsStopped => _stopped;

    public TodoSettingsCoordinator(
        SettingsService settings,
        Func<bool, Task>? setWidgetEnabled = null,
        Action<bool>? refreshReminders = null)
    {
        _settings = settings;
        _setWidgetEnabled = setWidgetEnabled;
        _refreshReminders = refreshReminders ?? (_ => { });
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public TodoReminderSettings Read()
    {
        AppSettings settings = _settings.Settings;
        return new(
            FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo),
            settings.Todo.TodoReminderEnabled,
            SettingsService.NormalizeTodoReminderOffsetMinutes(settings.Todo.TodoDefaultReminderOffsetMinutes));
    }

    public TodoLayoutSettings ReadLayout()
    {
        TodoSettingsSlice todo = _settings.Settings.Todo;
        return new(
            SettingsService.NormalizeTodoLayoutMode(
                todo.TodoLayoutMode,
                todo.TodoUseWideDetailPane),
            todo.TodoAutoSelectFirstInWideLayout);
    }

    public void SetLayoutMode(string? mode)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        string normalized = SettingsService.NormalizeTodoLayoutMode(mode);
        bool useWideDetail = normalized != SettingsService.TodoLayoutModeSinglePane;
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoLayoutMode == normalized &&
            todo.TodoUseWideDetailPane == useWideDetail)
        {
            return;
        }

        todo.TodoLayoutMode = normalized;
        todo.TodoUseWideDetailPane = useWideDetail;
        _settings.SaveDebounced();
    }

    public void SetAutoSelectFirstInWideLayout(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoAutoSelectFirstInWideLayout == enabled) return;
        todo.TodoAutoSelectFirstInWideLayout = enabled;
        _settings.SaveDebounced();
    }

    public void SetLegacyWideDetailPane(bool enabled)
    {
        // The compatibility property remains writable for old callers; the
        // layout selector uses SetLayoutMode to update both fields together.
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoUseWideDetailPane == enabled) return;
        todo.TodoUseWideDetailPane = enabled;
        _settings.SaveDebounced();
    }

    public void ResetLayoutPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        todo.TodoLayoutMode = SettingsService.TodoLayoutModeAuto;
        todo.TodoUseWideDetailPane = true;
        todo.TodoAutoSelectFirstInWideLayout = true;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public TodoTabSettings ReadTabs()
    {
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoTabSettings snapshot = ReadTabsCore(todo);
        if (!HasVisibleTab(todo))
        {
            return snapshot with
            {
                DefaultFilter = SettingsService.TodoDefaultFilterAll,
                ShowAllTab = true
            };
        }

        string defaultFilter = SettingsService.NormalizeTodoDefaultFilter(
            todo.TodoDefaultFilter);
        if (!SettingsService.IsTodoTabVisible(todo, defaultFilter))
        {
            defaultFilter = SettingsService.GetFirstVisibleTodoTab(todo);
        }
        return snapshot with { DefaultFilter = defaultFilter };
    }

    public void SetDefaultFilter(string? filter)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoTabSettings before = ReadTabsCore(todo);
        string normalized = SettingsService.NormalizeTodoDefaultFilter(filter);
        SetTabVisibleCore(todo, normalized, visible: true);
        todo.TodoDefaultFilter = normalized;
        SaveTabsIfChanged(todo, before);
    }

    public void SetTabVisible(string? filter, bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoTabSettings before = ReadTabsCore(todo);
        SetTabVisibleCore(
            todo,
            SettingsService.NormalizeTodoDefaultFilter(filter),
            visible);
        SaveTabsIfChanged(todo, before);
    }

    public void SetTabBarVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoTabSettings before = ReadTabsCore(todo);
        todo.TodoShowTabBar = visible;
        SaveTabsIfChanged(todo, before);
    }

    public void ResetTabPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoTabSettings before = ReadTabsCore(todo);
        todo.TodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
        todo.TodoShowTabBar = true;
        todo.TodoShowAllTab = true;
        todo.TodoShowActiveTab = false;
        todo.TodoShowTodayTab = true;
        todo.TodoShowThisWeekTab = false;
        todo.TodoShowThisMonthTab = false;
        todo.TodoShowImportantTab = true;
        todo.TodoShowCompletedTab = true;
        if (scheduleSave && before != ReadTabsCore(todo))
            _settings.SaveDebounced();
    }

    private void SaveTabsIfChanged(
        TodoSettingsSlice todo,
        TodoTabSettings before)
    {
        if (!HasVisibleTab(todo))
        {
            todo.TodoShowAllTab = true;
        }

        todo.TodoDefaultFilter = SettingsService.NormalizeTodoDefaultFilter(
            todo.TodoDefaultFilter);
        if (!SettingsService.IsTodoTabVisible(todo, todo.TodoDefaultFilter))
        {
            todo.TodoDefaultFilter = SettingsService.GetFirstVisibleTodoTab(todo);
        }

        if (before != ReadTabsCore(todo))
            _settings.SaveDebounced();
    }

    private static TodoTabSettings ReadTabsCore(TodoSettingsSlice todo) => new(
        todo.TodoDefaultFilter,
        todo.TodoShowTabBar,
        todo.TodoShowAllTab,
        todo.TodoShowActiveTab,
        todo.TodoShowTodayTab,
        todo.TodoShowThisWeekTab,
        todo.TodoShowThisMonthTab,
        todo.TodoShowImportantTab,
        todo.TodoShowCompletedTab);

    private static bool HasVisibleTab(TodoSettingsSlice todo) =>
        todo.TodoShowAllTab || todo.TodoShowActiveTab ||
        todo.TodoShowTodayTab || todo.TodoShowThisWeekTab ||
        todo.TodoShowThisMonthTab || todo.TodoShowImportantTab ||
        todo.TodoShowCompletedTab;

    private static void SetTabVisibleCore(
        TodoSettingsSlice todo,
        string filter,
        bool visible)
    {
        switch (filter)
        {
            case SettingsService.TodoDefaultFilterActive:
                todo.TodoShowActiveTab = visible;
                break;
            case SettingsService.TodoDefaultFilterToday:
                todo.TodoShowTodayTab = visible;
                break;
            case SettingsService.TodoDefaultFilterThisWeek:
                todo.TodoShowThisWeekTab = visible;
                break;
            case SettingsService.TodoDefaultFilterThisMonth:
                todo.TodoShowThisMonthTab = visible;
                break;
            case SettingsService.TodoDefaultFilterImportant:
                todo.TodoShowImportantTab = visible;
                break;
            case SettingsService.TodoDefaultFilterCompleted:
                todo.TodoShowCompletedTab = visible;
                break;
            default:
                todo.TodoShowAllTab = visible;
                break;
        }
    }

    public TodoContentDisplaySettings ReadContentDisplay()
    {
        AppSettings settings = _settings.Settings;
        TodoSettingsSlice todo = settings.Todo;
        return new(
            SettingsService.NormalizeItemPreviewLineCount(
                todo.TodoItemPreviewLineCount),
            SettingsService.NormalizeTextSize(
                todo.TodoListTextSize > 0
                    ? todo.TodoListTextSize
                    : settings.WidgetShell.TextSize),
            SettingsService.NormalizeTextSize(
                todo.TodoContentTextSize > 0
                    ? todo.TodoContentTextSize
                    : settings.WidgetShell.TextSize));
    }

    public void SetPreviewLineCount(int lineCount)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        int normalized = SettingsService.NormalizeItemPreviewLineCount(
            lineCount);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoItemPreviewLineCount == normalized) return;
        todo.TodoItemPreviewLineCount = normalized;
        _settings.SaveDebounced();
    }

    public void SetListTextSize(double size, bool scheduleSave = true) =>
        SetTextSize(size, list: true, scheduleSave: scheduleSave);

    public void SetContentTextSize(double size, bool scheduleSave = true) =>
        SetTextSize(size, list: false, scheduleSave: scheduleSave);

    public void ResetPreviewLineCount(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoItemPreviewLineCount ==
            SettingsService.DefaultTodoItemPreviewLineCount)
        {
            return;
        }
        todo.TodoItemPreviewLineCount =
            SettingsService.DefaultTodoItemPreviewLineCount;
        if (scheduleSave) _settings.SaveDebounced();
    }

    private void SetTextSize(double size, bool list, bool scheduleSave)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        double normalized = double.IsFinite(size)
            ? Math.Clamp(
                Math.Round(size * 2d, MidpointRounding.AwayFromZero) / 2d,
                SettingsService.MinTextSize,
                SettingsService.MaxTextSize)
            : SettingsService.NormalizeTextSize(
                _settings.Settings.WidgetShell.TextSize);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        double previous = list
            ? todo.TodoListTextSize
            : todo.TodoContentTextSize;
        if (Math.Abs(previous - normalized) <= 0.0001) return;
        if (list)
            todo.TodoListTextSize = normalized;
        else
            todo.TodoContentTextSize = normalized;

        if (scheduleSave)
        {
            _settings.RequestAppearancePreview();
            _settings.SaveDebounced(changeKind: SettingsChangeKind.Appearance);
        }
    }

    public TodoInputSettings ReadInput()
    {
        TodoSettingsSlice todo = _settings.Settings.Todo;
        return new(
            SettingsService.NormalizeTodoNewTaskPosition(
                todo.TodoNewTaskPosition),
            SettingsService.NormalizeEditorEnterBehavior(
                todo.TodoEditorEnterBehavior));
    }

    public void SetNewTaskPosition(string? position)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        string normalized = SettingsService.NormalizeTodoNewTaskPosition(
            position);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoNewTaskPosition == normalized) return;
        todo.TodoNewTaskPosition = normalized;
        _settings.SaveDebounced();
    }

    public void SetEditorEnterBehavior(string? behavior)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        string normalized = SettingsService.NormalizeEditorEnterBehavior(
            behavior);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoEditorEnterBehavior == normalized) return;
        todo.TodoEditorEnterBehavior = normalized;
        _settings.SaveDebounced();
    }

    public void ResetInputPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoNewTaskPosition ==
                SettingsService.TodoNewTaskPositionTop &&
            todo.TodoEditorEnterBehavior ==
                SettingsService.EditorEnterBehaviorCtrlEnterSaves)
        {
            return;
        }
        todo.TodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
        todo.TodoEditorEnterBehavior =
            SettingsService.EditorEnterBehaviorCtrlEnterSaves;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public TodoDisplayOptions ReadDisplayOptions()
    {
        TodoSettingsSlice todo = _settings.Settings.Todo;
        return new(
            todo.TodoShowCompletedTasks,
            todo.TodoShowFooterStats,
            todo.TodoShowClearCompletedButton,
            SettingsService.NormalizeWidgetTabStyle(todo.TodoTabStyle));
    }

    public void SetShowCompletedTasks(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoShowCompletedTasks == visible) return;
        todo.TodoShowCompletedTasks = visible;
        _settings.SaveDebounced();
    }

    public void SetShowFooterStats(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoShowFooterStats == visible) return;
        todo.TodoShowFooterStats = visible;
        _settings.SaveDebounced();
    }

    public void SetShowClearCompletedButton(bool visible)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoShowClearCompletedButton == visible) return;
        todo.TodoShowClearCompletedButton = visible;
        _settings.SaveDebounced();
    }

    public void SetTabStyle(string? style)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        string normalized = SettingsService.NormalizeWidgetTabStyle(style);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        if (todo.TodoTabStyle == normalized) return;
        todo.TodoTabStyle = normalized;
        _settings.SaveDebounced();
    }

    public void ResetDisplayOptions(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        TodoDisplayOptions before = ReadDisplayOptions();
        string previousTabStyle = todo.TodoTabStyle;
        todo.TodoShowCompletedTasks = false;
        todo.TodoShowFooterStats = false;
        todo.TodoShowClearCompletedButton = true;
        todo.TodoTabStyle = SettingsService.WidgetTabStyleButton;
        if (scheduleSave &&
            (before != ReadDisplayOptions() ||
             previousTabStyle != todo.TodoTabStyle))
            _settings.SaveDebounced();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _enableGate.WaitAsync(linked.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_setWidgetEnabled is not null)
            {
                await _setWidgetEnabled(enabled);
            }
            else
            {
                CommitEnabledState(enabled);
                await _settings.SaveAsync();
            }
        }
        finally
        {
            _enableGate.Release();
        }
    }

    // Window creation/removal also uses this entry, so those paths cannot
    // silently bypass the runtime when no settings window exists.
    internal void CommitEnabledState(bool enabled)
    {
        if (_stopped) return;
        FeatureWidgetSettings.SetEnabled(_settings.Settings, WidgetKind.Todo, enabled);
        _refreshReminders(false);
    }

    public void SetRemindersEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        if (_settings.Settings.Todo.TodoReminderEnabled == enabled) return;
        _settings.Settings.Todo.TodoReminderEnabled = enabled;
        SavePreference(checkNow: enabled);
    }

    public void SetDefaultReminderOffset(int minutes)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        int normalized = SettingsService.NormalizeTodoReminderOffsetMinutes(minutes);
        if (_settings.Settings.Todo.TodoDefaultReminderOffsetMinutes == normalized) return;
        _settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = normalized;
        SavePreference(checkNow: false);
    }

    public void ResetReminderPreferences(bool scheduleSave = true)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        TodoSettingsSlice todo = _settings.Settings.Todo;
        bool wasEnabled = todo.TodoReminderEnabled;
        if (wasEnabled &&
            todo.TodoDefaultReminderOffsetMinutes ==
                SettingsService.DefaultTodoReminderOffsetMinutes)
        {
            return;
        }

        todo.TodoReminderEnabled = true;
        todo.TodoDefaultReminderOffsetMinutes =
            SettingsService.DefaultTodoReminderOffsetMinutes;
        if (scheduleSave) SavePreference(checkNow: !wasEnabled);
    }

    private void SavePreference(bool checkNow)
    {
        _updatingPreferences = true;
        try
        {
            _settings.SaveDebounced();
        }
        finally
        {
            _updatingPreferences = false;
        }
        _refreshReminders(checkNow);
    }

    private void OnSettingsChanged()
    {
        if (!_stopped && !_updatingPreferences)
        {
            // Covers restore/defaults and legacy callers during migration.
            _refreshReminders(false);
        }
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            _settings.SettingsChanged -= OnSettingsChanged;
            _lifetime.Cancel();
        }
        // Let an already-started window transition finish before App tears
        // down widget windows. Pending transitions have been cancelled.
        await _enableGate.WaitAsync();
        _lifetime.Dispose();
        _enableGate.Dispose();
    }
}
