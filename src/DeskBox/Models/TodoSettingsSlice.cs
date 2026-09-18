namespace DeskBox.Models;

/// <summary>
/// Todo widget preferences: tabs, layout, preview density, reminders.
/// </summary>
public sealed class TodoSettingsSlice
{
    public bool TodoEnabled { get; set; }

    /// <summary>Where newly added Todo tasks are inserted. Valid values: <c>"Top"</c>, <c>"Bottom"</c>.</summary>
    public string TodoNewTaskPosition { get; set; } = "Top";

    /// <summary>Todo tab style. Valid values: <c>"Pivot"</c>, <c>"Button"</c>.</summary>
    public string TodoTabStyle { get; set; } = "Button";

    public bool TodoShowTabBar { get; set; } = true;

    public bool TodoShowAllTab { get; set; } = true;

    public bool TodoShowActiveTab { get; set; }

    public bool TodoShowTodayTab { get; set; } = true;

    public bool TodoShowThisWeekTab { get; set; }

    public bool TodoShowThisMonthTab { get; set; }

    public bool TodoShowImportantTab { get; set; } = true;

    public bool TodoShowCompletedTab { get; set; } = true;

    /// <summary>Default Todo filter used when the widget opens.</summary>
    public string TodoDefaultFilter { get; set; } = "All";

    /// <summary>Whether completed Todo tasks remain visible in non-completed views.</summary>
    public bool TodoShowCompletedTasks { get; set; }

    /// <summary>Maximum number of text lines shown for each Todo item in the list.</summary>
    public int TodoItemPreviewLineCount { get; set; } = 2;

    /// <summary>Text size for Todo list cards. Zero keeps the global appearance size for legacy settings.</summary>
    public double TodoListTextSize { get; set; }

    /// <summary>Text size for Todo detail content. Zero keeps the global appearance size for legacy settings.</summary>
    public double TodoContentTextSize { get; set; }

    /// <summary>Enter-key behavior used by Todo multiline editors.</summary>
    public string TodoEditorEnterBehavior { get; set; } = "CtrlEnterSaves";

    /// <summary>Whether the Todo footer item count is visible.</summary>
    public bool TodoShowFooterStats { get; set; }

    /// <summary>Whether the Todo footer clear-completed command is visible.</summary>
    public bool TodoShowClearCompletedButton { get; set; } = true;

    /// <summary>Whether Todo due-date reminders are shown while DeskBox is running.</summary>
    public bool TodoReminderEnabled { get; set; } = true;

    /// <summary>Default reminder lead time in minutes before a Todo due date.</summary>
    public int TodoDefaultReminderOffsetMinutes { get; set; } = 5;

    public bool TodoUseWideDetailPane { get; set; } = true;

    /// <summary>
    /// Todo master-detail layout preference. Valid values: <c>Auto</c>,
    /// <c>SinglePane</c>, and <c>DualPane</c>. An empty value is treated as a
    /// legacy setting and migrated from <see cref="TodoUseWideDetailPane"/>.
    /// </summary>
    public string TodoLayoutMode { get; set; } = string.Empty;

    public bool TodoAutoSelectFirstInWideLayout { get; set; } = true;
}
