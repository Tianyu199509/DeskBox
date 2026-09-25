using DeskBox.Contracts;
using DeskBox.Features.Todo;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResetReminderPreferences_DefersRuntimeRefreshUntilOuterSave()
    {
        var settings = new SettingsService(_root);
        settings.Settings.Todo.TodoReminderEnabled = false;
        settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
        var refreshes = new List<bool>();
        var coordinator = new TodoSettingsCoordinator(settings,
            refreshReminders: refreshes.Add);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        editor.ResetReminderPreferences(scheduleSave: false);

        Assert.True(editor.RemindersEnabled);
        Assert.Equal(SettingsService.DefaultTodoReminderOffsetMinutes,
            editor.DefaultOffsetMinutes);
        Assert.Contains(nameof(TodoSettingsViewModel.RemindersEnabled), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.DefaultOffsetMinutes), changed);
        Assert.Empty(refreshes);

        await settings.SaveAsync();
        Assert.Equal(new[] { false }, refreshes);
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Settings.Todo.TodoReminderEnabled);
        Assert.Equal(SettingsService.DefaultTodoReminderOffsetMinutes,
            reloaded.Settings.Todo.TodoDefaultReminderOffsetMinutes);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task DeferredReminderReset_ReconcilesRunningSessionAfterSave()
    {
        var settings = new SettingsService(_root);
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.Todo, true);
        settings.Settings.Todo.TodoReminderEnabled = false;
        settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
        var sessions = new List<RecordingReminderSession>();
        await using var runtime = new TodoReminderRuntime(() =>
        {
            var session = new RecordingReminderSession();
            sessions.Add(session);
            return session;
        });
        TodoSettingsCoordinator? coordinator = null;
        coordinator = new TodoSettingsCoordinator(settings,
            refreshReminders: _ => runtime.Reconcile(coordinator!.Read()));
        using var editor = new TodoSettingsViewModel(coordinator, _ => { });

        editor.ResetReminderPreferences(scheduleSave: false);
        Assert.Null(runtime.Current);

        await settings.SaveAsync();
        Assert.Single(sessions);
        Assert.Same(sessions[0], runtime.Current);
        Assert.Equal(1, sessions[0].Starts);

        settings.Settings.Todo.TodoReminderEnabled = false;
        await settings.SaveAsync();
        Assert.Null(runtime.Current);
        Assert.Equal(1, sessions[0].Disposals);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ResetReminderPreferences_UserActionRefreshesOnceWithCorrectCheckNow()
    {
        var settings = new SettingsService(_root);
        settings.Settings.Todo.TodoReminderEnabled = false;
        settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
        var refreshes = new List<bool>();
        var coordinator = new TodoSettingsCoordinator(settings,
            refreshReminders: refreshes.Add);

        coordinator.ResetReminderPreferences();
        Assert.Equal(new[] { true }, refreshes);
        coordinator.ResetReminderPreferences();
        Assert.Single(refreshes);

        settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 60;
        coordinator.ResetReminderPreferences();
        Assert.Equal(new[] { true, false }, refreshes);
        await settings.FlushPendingSaveAsync();
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedReminderReset_ReportsFailureWithoutChangingEditor()
    {
        var settings = new SettingsService(_root);
        settings.Settings.Todo.TodoReminderEnabled = false;
        settings.Settings.Todo.TodoDefaultReminderOffsetMinutes = 30;
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        await coordinator.StopAsync();

        editor.ResetReminderPreferences(scheduleSave: false);

        Assert.False(editor.RemindersEnabled);
        Assert.Equal(30, editor.DefaultOffsetMinutes);
        Assert.Single(errors);
        Assert.IsType<ObjectDisposedException>(errors[0]);
    }

    [Fact]
    public async Task ReminderPreferences_PersistThroughExistingSchemaAndRefreshOnce()
    {
        var settings = new SettingsService(_root);
        settings.Settings.Language = "zh-TW";
        var checks = new List<bool>();
        var coordinator = new TodoSettingsCoordinator(settings, refreshReminders: checks.Add);
        try
        {
            coordinator.SetRemindersEnabled(false);
            coordinator.SetRemindersEnabled(false);
            Assert.Equal(new[] { false }, checks);
            coordinator.SetRemindersEnabled(true);
            Assert.Equal(new[] { false, true }, checks);
            coordinator.SetDefaultReminderOffset(30);
            Assert.Equal(30, coordinator.Read().DefaultOffsetMinutes);
            Assert.Equal("zh-TW", settings.Settings.Language);
            await settings.FlushPendingSaveAsync();

            var reloaded = new SettingsService(_root);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Settings.TodoReminderEnabled);
            Assert.Equal(30, reloaded.Settings.TodoDefaultReminderOffsetMinutes);
        }
        finally
        {
            await coordinator.StopAsync();
        }
    }

    [Fact]
    public async Task LayoutMode_PersistsWithLegacyWideFlagAndAutoSelect()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        editor.LayoutMode = SettingsService.TodoLayoutModeSinglePane;
        editor.AutoSelectFirstInWideLayout = false;
        Assert.Equal(SettingsService.TodoLayoutModeSinglePane, editor.LayoutMode);
        Assert.False(settings.Settings.Todo.TodoUseWideDetailPane);
        Assert.False(editor.AutoSelectFirstInWideLayout);

        editor.LayoutMode = SettingsService.TodoLayoutModeDualPane;
        Assert.True(settings.Settings.Todo.TodoUseWideDetailPane);
        await settings.FlushPendingSaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.TodoLayoutModeDualPane,
            reloaded.Settings.Todo.TodoLayoutMode);
        Assert.True(reloaded.Settings.Todo.TodoUseWideDetailPane);
        Assert.False(reloaded.Settings.Todo.TodoAutoSelectFirstInWideLayout);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task SelectingHiddenDefaultFilter_EnablesItsTabAndPersistsTogether()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        Assert.False(editor.Tabs.ShowThisWeekTab);
        editor.DefaultFilter = SettingsService.TodoDefaultFilterThisWeek;

        Assert.Equal(SettingsService.TodoDefaultFilterThisWeek,
            editor.DefaultFilter);
        Assert.True(editor.Tabs.ShowThisWeekTab);
        Assert.Equal(editor.DefaultFilter,
            settings.Settings.Todo.TodoDefaultFilter);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.TodoDefaultFilterThisWeek,
            reloaded.Settings.Todo.TodoDefaultFilter);
        Assert.True(reloaded.Settings.Todo.TodoShowThisWeekTab);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task HidingDefaultTab_ChoosesFirstVisibleAndNeverLeavesAllTabsHidden()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        editor.DefaultFilter = SettingsService.TodoDefaultFilterToday;

        editor.SetTabVisible(SettingsService.TodoDefaultFilterToday, false);
        Assert.Equal(SettingsService.TodoDefaultFilterAll,
            editor.DefaultFilter);
        Assert.False(editor.Tabs.ShowTodayTab);

        editor.SetTabVisible(SettingsService.TodoDefaultFilterAll, false);
        Assert.Equal(SettingsService.TodoDefaultFilterImportant,
            editor.DefaultFilter);
        editor.SetTabVisible(SettingsService.TodoDefaultFilterCompleted, false);
        editor.SetTabVisible(SettingsService.TodoDefaultFilterImportant, false);
        Assert.True(editor.Tabs.ShowAllTab);
        Assert.Equal(SettingsService.TodoDefaultFilterAll,
            editor.DefaultFilter);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task TabBarAndDefaults_FollowOneEditorAcrossExternalRestore()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        editor.ShowTabBar = false;
        editor.SetTabVisible(SettingsService.TodoDefaultFilterActive, true);
        Assert.False(editor.Tabs.ShowTabBar);
        Assert.True(editor.Tabs.ShowActiveTab);

        settings.Settings.Todo.TodoShowTabBar = true;
        settings.Settings.Todo.TodoDefaultFilter =
            SettingsService.TodoDefaultFilterActive;
        editor.Refresh();
        Assert.True(editor.ShowTabBar);
        Assert.Equal(SettingsService.TodoDefaultFilterActive,
            editor.DefaultFilter);
        Assert.Contains(nameof(TodoSettingsViewModel.Tabs), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.DefaultFilter), changed);

        editor.ResetTabPreferences();
        Assert.Equal(SettingsService.TodoDefaultFilterAll,
            editor.DefaultFilter);
        Assert.True(editor.Tabs.ShowTabBar);
        Assert.False(editor.Tabs.ShowActiveTab);
        Assert.True(editor.Tabs.ShowTodayTab);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.TodoDefaultFilterAll,
            reloaded.Settings.Todo.TodoDefaultFilter);
        Assert.False(reloaded.Settings.Todo.TodoShowActiveTab);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ExternalInvalidTabs_AreSafeForTheEditorWithoutAReadSideWrite()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        settings.Settings.Todo.TodoDefaultFilter = "Unknown";
        settings.Settings.Todo.TodoShowAllTab = false;
        settings.Settings.Todo.TodoShowActiveTab = false;
        settings.Settings.Todo.TodoShowTodayTab = false;
        settings.Settings.Todo.TodoShowThisWeekTab = false;
        settings.Settings.Todo.TodoShowThisMonthTab = false;
        settings.Settings.Todo.TodoShowImportantTab = false;
        settings.Settings.Todo.TodoShowCompletedTab = false;

        editor.Refresh();

        Assert.Equal(SettingsService.TodoDefaultFilterAll,
            editor.DefaultFilter);
        Assert.True(editor.Tabs.ShowAllTab);
        Assert.Equal("Unknown", settings.Settings.Todo.TodoDefaultFilter);
        Assert.False(settings.Settings.Todo.TodoShowAllTab);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ContentDisplay_ZeroOverridesFollowGlobalSizeUntilUserSetsAnOverride()
    {
        var settings = new SettingsService(_root);
        settings.Settings.WidgetShell.TextSize = 13.5;
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal(13.5, editor.ListTextSize);
        Assert.Equal(13.5, editor.ContentTextSize);
        Assert.Equal(0, settings.Settings.Todo.TodoListTextSize);
        Assert.Equal(0, settings.Settings.Todo.TodoContentTextSize);

        settings.Settings.WidgetShell.TextSize = 14.5;
        editor.Refresh();
        Assert.Equal(14.5, editor.ListTextSize);
        Assert.Equal(14.5, editor.ContentTextSize);
        Assert.Contains(nameof(TodoSettingsViewModel.ListTextSize), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.ContentTextSize), changed);
        Assert.Equal(0, settings.Settings.Todo.TodoListTextSize);

        Assert.True(editor.TrySetListTextSize(12.26));
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(12.5, settings.Settings.Todo.TodoListTextSize);
        settings.Settings.WidgetShell.TextSize = 15;
        editor.Refresh();
        Assert.Equal(12.5, editor.ListTextSize);
        Assert.Equal(15, editor.ContentTextSize);
        Assert.Equal(0, settings.Settings.Todo.TodoContentTextSize);

        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(12.5, reloaded.Settings.Todo.TodoListTextSize);
        Assert.Equal(0, reloaded.Settings.Todo.TodoContentTextSize);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task NonFiniteTextSizes_DoNotReplaceInheritedOverrides()
    {
        var settings = new SettingsService(_root);
        settings.Settings.WidgetShell.TextSize = 13.5;
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity,
                     double.NegativeInfinity })
        {
            Assert.False(editor.TrySetListTextSize(invalid));
            Assert.False(editor.TrySetContentTextSize(invalid));
            coordinator.SetListTextSize(invalid);
            coordinator.SetContentTextSize(invalid);
        }

        Assert.Equal(13.5, editor.ListTextSize);
        Assert.Equal(13.5, editor.ContentTextSize);
        Assert.Equal(0, settings.Settings.Todo.TodoListTextSize);
        Assert.Equal(0, settings.Settings.Todo.TodoContentTextSize);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task PreviewLinesClampAndResetThroughTodoWriter()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        editor.PreviewLineCount = 99;
        Assert.Equal(SettingsService.MaxItemPreviewLineCount,
            editor.PreviewLineCount);
        editor.PreviewLineCount = -5;
        Assert.Equal(SettingsService.MinItemPreviewLineCount,
            editor.PreviewLineCount);
        editor.ResetPreviewLineCount();
        Assert.Equal(SettingsService.DefaultTodoItemPreviewLineCount,
            editor.PreviewLineCount);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.DefaultTodoItemPreviewLineCount,
            reloaded.Settings.Todo.TodoItemPreviewLineCount);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedContentWriter_RejectsEditsWithoutChangingSnapshot()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        await coordinator.StopAsync();

        editor.PreviewLineCount = 7;
        bool textApplied = editor.TrySetListTextSize(13);

        Assert.Equal(SettingsService.DefaultTodoItemPreviewLineCount,
            editor.PreviewLineCount);
        Assert.False(textApplied);
        Assert.Equal(0, settings.Settings.Todo.TodoListTextSize);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.IsType<ObjectDisposedException>(error));
    }

    [Fact]
    public async Task InputPreferences_PersistAndKeepTheExistingEnterRule()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        editor.NewTaskPosition = SettingsService.TodoNewTaskPositionBottom;
        editor.EditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves;
        Assert.Equal(SettingsService.TodoNewTaskPositionBottom,
            editor.NewTaskPosition);
        Assert.Equal(SettingsService.EditorEnterBehaviorEnterSaves,
            editor.EditorEnterBehavior);
        Assert.True(SettingsService.ShouldSubmitEditorOnEnter(
            editor.EditorEnterBehavior,
            controlPressed: false));
        Assert.False(SettingsService.ShouldSubmitEditorOnEnter(
            editor.EditorEnterBehavior,
            controlPressed: true));

        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.TodoNewTaskPositionBottom,
            reloaded.Settings.Todo.TodoNewTaskPosition);
        Assert.Equal(SettingsService.EditorEnterBehaviorEnterSaves,
            reloaded.Settings.Todo.TodoEditorEnterBehavior);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task InputPreferences_NormalizeInvalidValuesRefreshAndReset()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        editor.NewTaskPosition = SettingsService.TodoNewTaskPositionBottom;
        editor.EditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves;
        settings.Settings.Todo.TodoNewTaskPosition = "Unexpected";
        settings.Settings.Todo.TodoEditorEnterBehavior = "Unknown";
        editor.Refresh();
        Assert.Equal(SettingsService.TodoNewTaskPositionTop,
            editor.NewTaskPosition);
        Assert.Equal(SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            editor.EditorEnterBehavior);
        Assert.Equal("Unexpected", settings.Settings.Todo.TodoNewTaskPosition);
        Assert.Contains(nameof(TodoSettingsViewModel.NewTaskPosition), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.EditorEnterBehavior), changed);

        editor.NewTaskPosition = SettingsService.TodoNewTaskPositionBottom;
        editor.EditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves;
        editor.ResetInputPreferences();
        Assert.Equal(SettingsService.TodoNewTaskPositionTop,
            settings.Settings.Todo.TodoNewTaskPosition);
        Assert.Equal(SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            settings.Settings.Todo.TodoEditorEnterBehavior);
        Assert.False(SettingsService.ShouldSubmitEditorOnEnter(
            editor.EditorEnterBehavior,
            controlPressed: false));
        Assert.True(SettingsService.ShouldSubmitEditorOnEnter(
            editor.EditorEnterBehavior,
            controlPressed: true));
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task DisplayOptions_PersistRefreshAndResetThroughOneEditor()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        editor.ShowCompletedTasks = true;
        editor.ShowFooterStats = true;
        editor.ShowClearCompletedButton = false;
        editor.TabStyle = SettingsService.WidgetTabStylePivot;
        await settings.FlushPendingSaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Settings.Todo.TodoShowCompletedTasks);
        Assert.True(reloaded.Settings.Todo.TodoShowFooterStats);
        Assert.False(reloaded.Settings.Todo.TodoShowClearCompletedButton);
        Assert.Equal(SettingsService.WidgetTabStylePivot,
            reloaded.Settings.Todo.TodoTabStyle);

        settings.Settings.Todo.TodoShowCompletedTasks = false;
        settings.Settings.Todo.TodoShowFooterStats = false;
        settings.Settings.Todo.TodoShowClearCompletedButton = true;
        settings.Settings.Todo.TodoTabStyle = "Unsupported";
        changed.Clear();
        editor.Refresh();
        Assert.False(editor.ShowCompletedTasks);
        Assert.False(editor.ShowFooterStats);
        Assert.True(editor.ShowClearCompletedButton);
        Assert.Equal(SettingsService.WidgetTabStyleButton, editor.TabStyle);
        Assert.Equal("Unsupported", settings.Settings.Todo.TodoTabStyle);
        Assert.Contains(nameof(TodoSettingsViewModel.ShowCompletedTasks), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.ShowFooterStats), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.ShowClearCompletedButton), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.TabStyle), changed);

        editor.ResetDisplayOptions();
        await settings.FlushPendingSaveAsync();
        reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.WidgetTabStyleButton,
            reloaded.Settings.Todo.TodoTabStyle);
        Assert.False(reloaded.Settings.Todo.TodoShowCompletedTasks);
        Assert.False(reloaded.Settings.Todo.TodoShowFooterStats);
        Assert.True(reloaded.Settings.Todo.TodoShowClearCompletedButton);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedDisplayWriter_ReportsFailureAndKeepsEditorValues()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        await coordinator.StopAsync();

        editor.ShowCompletedTasks = true;
        editor.ShowFooterStats = true;
        editor.ShowClearCompletedButton = false;
        editor.TabStyle = SettingsService.WidgetTabStylePivot;

        Assert.False(editor.ShowCompletedTasks);
        Assert.False(editor.ShowFooterStats);
        Assert.True(editor.ShowClearCompletedButton);
        Assert.Equal(SettingsService.WidgetTabStyleButton, editor.TabStyle);
        Assert.Equal(4, errors.Count);
        Assert.All(errors, error => Assert.IsType<ObjectDisposedException>(error));
    }

    [Fact]
    public async Task StoppedInputWriter_ReportsFailureWithoutChangingTheEditor()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        await coordinator.StopAsync();

        editor.NewTaskPosition = SettingsService.TodoNewTaskPositionBottom;
        editor.EditorEnterBehavior = SettingsService.EditorEnterBehaviorEnterSaves;

        Assert.Equal(SettingsService.TodoNewTaskPositionTop,
            editor.NewTaskPosition);
        Assert.Equal(SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            editor.EditorEnterBehavior);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.IsType<ObjectDisposedException>(error));
    }

    [Fact]
    public async Task LayoutRefreshAndDefaults_UseTheSameWriter()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        settings.Settings.Todo.TodoLayoutMode =
            SettingsService.TodoLayoutModeSinglePane;
        settings.Settings.Todo.TodoUseWideDetailPane = false;
        settings.Settings.Todo.TodoAutoSelectFirstInWideLayout = false;
        editor.Refresh();
        Assert.Equal(SettingsService.TodoLayoutModeSinglePane, editor.LayoutMode);
        Assert.False(editor.AutoSelectFirstInWideLayout);
        Assert.Contains(nameof(TodoSettingsViewModel.LayoutMode), changed);
        Assert.Contains(nameof(TodoSettingsViewModel.AutoSelectFirstInWideLayout),
            changed);

        editor.ResetLayoutPreferences();
        Assert.Equal(SettingsService.TodoLayoutModeAuto, editor.LayoutMode);
        Assert.True(editor.AutoSelectFirstInWideLayout);
        Assert.True(settings.Settings.Todo.TodoUseWideDetailPane);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.TodoLayoutModeAuto,
            reloaded.Settings.Todo.TodoLayoutMode);
        Assert.True(reloaded.Settings.Todo.TodoAutoSelectFirstInWideLayout);
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedLayoutWriter_ReportsErrorAndKeepsTheCurrentValue()
    {
        var settings = new SettingsService(_root);
        var coordinator = new TodoSettingsCoordinator(settings);
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        await coordinator.StopAsync();

        editor.LayoutMode = SettingsService.TodoLayoutModeSinglePane;

        Assert.Equal(SettingsService.TodoLayoutModeAuto, editor.LayoutMode);
        Assert.Single(errors);
        Assert.IsType<ObjectDisposedException>(errors[0]);
        Assert.True(settings.Settings.Todo.TodoUseWideDetailPane);
    }

    [Fact]
    public async Task RapidEnableChanges_AreSerializedAndLastRequestWins()
    {
        var settings = new SettingsService(_root);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = new List<bool>();
        TodoSettingsCoordinator? coordinator = null;
        coordinator = new TodoSettingsCoordinator(settings, async enabled =>
        {
            transitions.Add(enabled);
            coordinator!.CommitEnabledState(enabled);
            if (transitions.Count == 1) await releaseFirst.Task;
        });
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);

        editor.Enabled = true;
        editor.Enabled = false;
        editor.Enabled = true;
        Task finalChange = editor.PendingChange;
        editor.Refresh();
        Assert.True(editor.Enabled);
        Assert.Equal(new[] { true }, transitions);
        releaseFirst.SetResult();
        await finalChange;

        Assert.Equal(new[] { true, false, true }, transitions);
        Assert.True(editor.Enabled);
        Assert.True(settings.Settings.TodoEnabled);
        Assert.True(FeatureWidgetSettings.IsEnabled(settings.Settings, WidgetKind.Todo));
        Assert.Empty(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task FailedEnable_ReportsErrorAndDoesNotPoisonFollowingRequest()
    {
        var settings = new SettingsService(_root);
        int attempts = 0;
        TodoSettingsCoordinator? coordinator = null;
        coordinator = new TodoSettingsCoordinator(settings, enabled =>
        {
            if (++attempts == 1) throw new IOException("window creation failed");
            coordinator!.CommitEnabledState(enabled);
            return Task.CompletedTask;
        });
        var errors = new List<Exception>();
        using var editor = new TodoSettingsViewModel(coordinator, errors.Add);
        editor.Enabled = true;
        await editor.PendingChange;
        Assert.False(editor.Enabled);
        Assert.Single(errors);

        editor.Enabled = true;
        await editor.PendingChange;
        Assert.True(editor.Enabled);
        Assert.Equal(2, attempts);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task Stop_CancelsQueuedTransitionAndWaitsForActiveHostOperation()
    {
        var settings = new SettingsService(_root);
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var coordinator = new TodoSettingsCoordinator(settings, _ =>
        {
            calls++;
            return active.Task;
        });
        Task first = coordinator.SetEnabledAsync(true);
        Task queued = coordinator.SetEnabledAsync(false);
        Task stop = coordinator.StopAsync();
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        active.SetResult();
        await Task.WhenAll(first, stop);
        await coordinator.StopAsync();
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.SetEnabledAsync(true));
    }

    [Fact]
    public async Task ExternalRestore_ReconcilesRuntimeWithoutAnOpenEditor()
    {
        var settings = new SettingsService(_root);
        int refreshes = 0;
        var coordinator = new TodoSettingsCoordinator(settings, refreshReminders: _ => refreshes++);
        settings.Settings.Todo.TodoReminderEnabled = false;
        await settings.SaveAsync();
        Assert.Equal(1, refreshes);
        Assert.False(coordinator.Read().ShouldRunReminders);
        await coordinator.StopAsync();
        await settings.SaveAsync();
        Assert.Equal(1, refreshes);
    }

    private sealed class RecordingReminderSession : ITodoReminderSession
    {
        public int Starts { get; private set; }
        public int Disposals { get; private set; }
        public void Start() => Starts++;
        public void Refresh() { }
        public Task<int> CheckNowAsync(DateTimeOffset now) => Task.FromResult(0);
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
