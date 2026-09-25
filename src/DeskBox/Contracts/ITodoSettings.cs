namespace DeskBox.Contracts;

public readonly record struct TodoReminderSettings(
    bool Enabled,
    bool RemindersEnabled,
    int DefaultOffsetMinutes)
{
    public bool ShouldRunReminders => Enabled && RemindersEnabled;
}

public readonly record struct TodoLayoutSettings(
    string LayoutMode,
    bool AutoSelectFirstInWideLayout);

public readonly record struct TodoTabSettings(
    string DefaultFilter,
    bool ShowTabBar,
    bool ShowAllTab,
    bool ShowActiveTab,
    bool ShowTodayTab,
    bool ShowThisWeekTab,
    bool ShowThisMonthTab,
    bool ShowImportantTab,
    bool ShowCompletedTab);

/// <summary>
/// Effective Todo presentation values. A stored text-size override of zero
/// inherits the shared appearance size and is resolved by the settings owner.
/// </summary>
public readonly record struct TodoContentDisplaySettings(
    int PreviewLineCount,
    double ListTextSize,
    double ContentTextSize);

public readonly record struct TodoInputSettings(
    string NewTaskPosition,
    string EditorEnterBehavior);

public readonly record struct TodoDisplayOptions(
    bool ShowCompletedTasks,
    bool ShowFooterStats,
    bool ShowClearCompletedButton,
    string TabStyle);

/// <summary>Todo's enablement, reminders, display, and input preferences.</summary>
public interface ITodoSettings
{
    TodoReminderSettings Read();
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    void SetRemindersEnabled(bool enabled);
    void SetDefaultReminderOffset(int minutes);
    void ResetReminderPreferences(bool scheduleSave = true);
    TodoLayoutSettings ReadLayout();
    void SetLayoutMode(string? mode);
    void SetAutoSelectFirstInWideLayout(bool enabled);
    void SetLegacyWideDetailPane(bool enabled);
    void ResetLayoutPreferences(bool scheduleSave = true);
    TodoTabSettings ReadTabs();
    void SetDefaultFilter(string? filter);
    void SetTabVisible(string? filter, bool visible);
    void SetTabBarVisible(bool visible);
    void ResetTabPreferences(bool scheduleSave = true);
    TodoContentDisplaySettings ReadContentDisplay();
    void SetPreviewLineCount(int lineCount);
    void SetListTextSize(double size, bool scheduleSave = true);
    void SetContentTextSize(double size, bool scheduleSave = true);
    void ResetPreviewLineCount(bool scheduleSave = true);
    TodoInputSettings ReadInput();
    void SetNewTaskPosition(string? position);
    void SetEditorEnterBehavior(string? behavior);
    void ResetInputPreferences(bool scheduleSave = true);
    TodoDisplayOptions ReadDisplayOptions();
    void SetShowCompletedTasks(bool visible);
    void SetShowFooterStats(bool visible);
    void SetShowClearCompletedButton(bool visible);
    void SetTabStyle(string? style);
    void ResetDisplayOptions(bool scheduleSave = true);
}
