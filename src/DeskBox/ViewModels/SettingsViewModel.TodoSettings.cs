using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Features.Todo;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private readonly TodoSettingsViewModel _todoSettings;

    // Preserve the established XAML/AOT binding surface while Todo owns the
    // editable state and commands.
    public bool TodoEnabled
    {
        get => _todoSettings.Enabled;
        set
        {
            if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
                _todoSettings.Enabled = value;
        }
    }

    public bool TodoReminderEnabled
    {
        get => _todoSettings.RemindersEnabled;
        set
        {
            if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
                _todoSettings.RemindersEnabled = value;
        }
    }

    private void OnTodoSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TodoSettingsViewModel.Enabled):
                OnPropertyChanged(nameof(TodoEnabled));
                OnPropertyChanged(nameof(FeatureWidgetEntries));
                break;
            case nameof(TodoSettingsViewModel.RemindersEnabled):
                OnPropertyChanged(nameof(TodoReminderEnabled));
                OnPropertyChanged(nameof(TodoReminderSummaryText));
                break;
            case nameof(TodoSettingsViewModel.DefaultOffsetMinutes):
                OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutes));
                OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
                OnPropertyChanged(nameof(TodoReminderSummaryText));
                break;
            case nameof(TodoSettingsViewModel.LayoutMode):
                OnPropertyChanged(nameof(SelectedTodoLayoutMode));
                OnPropertyChanged(nameof(TodoLayoutSummaryText));
                OnPropertyChanged(nameof(TodoWideOptionsVisibility));
                bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
                _isApplyingSettingsSnapshot = true;
                try
                {
                    TodoUseWideDetailPane =
                        _todoSettings.LayoutMode !=
                        DeskBox.Services.SettingsService.TodoLayoutModeSinglePane;
                }
                finally
                {
                    _isApplyingSettingsSnapshot = wasApplyingSnapshot;
                }
                break;
            case nameof(TodoSettingsViewModel.AutoSelectFirstInWideLayout):
                bool wasApplyingAutoSelect = _isApplyingSettingsSnapshot;
                _isApplyingSettingsSnapshot = true;
                try
                {
                    TodoAutoSelectFirstInWideLayout =
                        _todoSettings.AutoSelectFirstInWideLayout;
                }
                finally
                {
                    _isApplyingSettingsSnapshot = wasApplyingAutoSelect;
                }
                OnPropertyChanged(nameof(TodoLayoutSummaryText));
                break;
            case nameof(TodoSettingsViewModel.Tabs):
                SyncTodoTabFacade();
                RefreshTodoTabsPresentation();
                break;
            case nameof(TodoSettingsViewModel.DefaultFilter):
                OnPropertyChanged(nameof(SelectedTodoDefaultFilter));
                OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
                RefreshTodoTabsPresentation();
                break;
            case nameof(TodoSettingsViewModel.PreviewLineCount):
                OnPropertyChanged(nameof(TodoItemPreviewLineCount));
                RefreshTodoContentPresentation();
                break;
            case nameof(TodoSettingsViewModel.ListTextSize):
            case nameof(TodoSettingsViewModel.ContentTextSize):
                SyncTodoTextSizeFacade();
                OnPropertyChanged(nameof(TodoListTextSizeValueText));
                OnPropertyChanged(nameof(TodoContentTextSizeValueText));
                break;
            case nameof(TodoSettingsViewModel.NewTaskPosition):
                OnPropertyChanged(nameof(SelectedTodoNewTaskPosition));
                OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
                RefreshTodoContentPresentation();
                break;
            case nameof(TodoSettingsViewModel.EditorEnterBehavior):
                OnPropertyChanged(nameof(TodoEditorEnterBehavior));
                RefreshTodoContentPresentation();
                break;
            case nameof(TodoSettingsViewModel.ShowCompletedTasks):
                SyncTodoDisplayFacade();
                RefreshTodoContentPresentation();
                break;
            case nameof(TodoSettingsViewModel.ShowFooterStats):
            case nameof(TodoSettingsViewModel.ShowClearCompletedButton):
                SyncTodoDisplayFacade();
                OnPropertyChanged(nameof(TodoFooterDisplaySummaryText));
                break;
            case nameof(TodoSettingsViewModel.TabStyle):
                OnPropertyChanged(nameof(SelectedTodoTabStyle));
                OnPropertyChanged(nameof(SelectedTodoTabStyleText));
                OnPropertyChanged(nameof(TodoTabStyleIndex));
                break;
        }
    }

    private void SyncTodoDisplayFacade()
    {
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            TodoShowCompletedTasks = _todoSettings.ShowCompletedTasks;
            TodoShowFooterStats = _todoSettings.ShowFooterStats;
            TodoShowClearCompletedButton = _todoSettings.ShowClearCompletedButton;
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }
    }

    private void SyncTodoTabFacade()
    {
        TodoTabSettings tabs = _todoSettings.Tabs;
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            TodoShowTabBar = tabs.ShowTabBar;
            TodoShowAllTab = tabs.ShowAllTab;
            TodoShowActiveTab = tabs.ShowActiveTab;
            TodoShowTodayTab = tabs.ShowTodayTab;
            TodoShowThisWeekTab = tabs.ShowThisWeekTab;
            TodoShowThisMonthTab = tabs.ShowThisMonthTab;
            TodoShowImportantTab = tabs.ShowImportantTab;
            TodoShowCompletedTab = tabs.ShowCompletedTab;
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }
    }

    private void ApplyTodoTabVisibility(string filter, bool visible)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        _todoSettings.SetTabVisible(filter, visible);
        SyncTodoTabFacade();
        RefreshTodoTabsPresentation();
    }

    private void ApplyTodoTabBarVisibility(bool visible)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot) return;
        _todoSettings.ShowTabBar = visible;
        SyncTodoTabFacade();
        RefreshTodoTabsPresentation();
    }

    private void SyncTodoTextSizeFacade()
    {
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            TodoListTextSize = _todoSettings.ListTextSize;
            TodoContentTextSize = _todoSettings.ContentTextSize;
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }
    }
}
