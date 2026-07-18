using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private int _quickCaptureItemPreviewLineCount = SettingsService.DefaultQuickCaptureItemPreviewLineCount;
    private string _quickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
    private int _todoItemPreviewLineCount = SettingsService.DefaultTodoItemPreviewLineCount;
    private string _todoEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
    private string[]? _cachedItemPreviewLineCountDisplayNames;
    private string[]? _cachedEditorEnterBehaviorDisplayNames;

    public int[] AvailableItemPreviewLineCounts { get; } =
        Enumerable.Range(
            SettingsService.MinItemPreviewLineCount,
            SettingsService.MaxItemPreviewLineCount - SettingsService.MinItemPreviewLineCount + 1)
        .ToArray();

    public string[] AvailableItemPreviewLineCountDisplayNames =>
        _cachedItemPreviewLineCountDisplayNames ??=
            AvailableItemPreviewLineCounts
                .Select(lineCount => lineCount == 1
                    ? _localizationService.T("Settings.ContentEditor.PreviewLines.Option.Single")
                    : _localizationService.Format(
                        "Settings.ContentEditor.PreviewLines.Option.Multiple",
                        lineCount))
                .ToArray();

    public string[] AvailableEditorEnterBehaviors { get; } =
    [
        SettingsService.EditorEnterBehaviorCtrlEnterSaves,
        SettingsService.EditorEnterBehaviorEnterSaves
    ];

    public string[] AvailableEditorEnterBehaviorDisplayNames =>
        _cachedEditorEnterBehaviorDisplayNames ??=
            AvailableEditorEnterBehaviors.Select(GetEditorEnterBehaviorDisplayName).ToArray();

    public int QuickCaptureItemPreviewLineCount
    {
        get => _quickCaptureItemPreviewLineCount;
        set
        {
            int normalized = SettingsService.NormalizeItemPreviewLineCount(value);
            if (!SetProperty(ref _quickCaptureItemPreviewLineCount, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedQuickCaptureItemPreviewLineCountIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureItemPreviewLineCount = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedQuickCaptureItemPreviewLineCountIndex =>
        Array.IndexOf(AvailableItemPreviewLineCounts, QuickCaptureItemPreviewLineCount);

    public string QuickCaptureEditorEnterBehavior
    {
        get => _quickCaptureEditorEnterBehavior;
        set
        {
            string normalized = SettingsService.NormalizeEditorEnterBehavior(value);
            if (!SetProperty(ref _quickCaptureEditorEnterBehavior, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedQuickCaptureEditorEnterBehaviorIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureEditorEnterBehavior = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedQuickCaptureEditorEnterBehaviorIndex =>
        Array.IndexOf(AvailableEditorEnterBehaviors, QuickCaptureEditorEnterBehavior);

    public int TodoItemPreviewLineCount
    {
        get => _todoItemPreviewLineCount;
        set
        {
            int normalized = SettingsService.NormalizeItemPreviewLineCount(value);
            if (!SetProperty(ref _todoItemPreviewLineCount, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTodoItemPreviewLineCountIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoItemPreviewLineCount = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedTodoItemPreviewLineCountIndex =>
        Array.IndexOf(AvailableItemPreviewLineCounts, TodoItemPreviewLineCount);

    public string TodoEditorEnterBehavior
    {
        get => _todoEditorEnterBehavior;
        set
        {
            string normalized = SettingsService.NormalizeEditorEnterBehavior(value);
            if (!SetProperty(ref _todoEditorEnterBehavior, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTodoEditorEnterBehaviorIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoEditorEnterBehavior = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public int SelectedTodoEditorEnterBehaviorIndex =>
        Array.IndexOf(AvailableEditorEnterBehaviors, TodoEditorEnterBehavior);

    private void InitializeContentEditorSettings(AppSettings settings)
    {
        _quickCaptureItemPreviewLineCount = SettingsService.NormalizeItemPreviewLineCount(
            settings.QuickCaptureItemPreviewLineCount);
        _quickCaptureEditorEnterBehavior = SettingsService.NormalizeEditorEnterBehavior(
            settings.QuickCaptureEditorEnterBehavior);
        _todoItemPreviewLineCount = SettingsService.NormalizeItemPreviewLineCount(
            settings.TodoItemPreviewLineCount);
        _todoEditorEnterBehavior = SettingsService.NormalizeEditorEnterBehavior(
            settings.TodoEditorEnterBehavior);
    }

    private void ApplyContentEditorSettingsSnapshot(AppSettings settings)
    {
        QuickCaptureItemPreviewLineCount = settings.QuickCaptureItemPreviewLineCount;
        QuickCaptureEditorEnterBehavior = settings.QuickCaptureEditorEnterBehavior;
        TodoItemPreviewLineCount = settings.TodoItemPreviewLineCount;
        TodoEditorEnterBehavior = settings.TodoEditorEnterBehavior;
    }

    private void RefreshContentEditorLocalizedProperties()
    {
        _cachedItemPreviewLineCountDisplayNames = null;
        _cachedEditorEnterBehaviorDisplayNames = null;
        OnPropertyChanged(nameof(AvailableItemPreviewLineCountDisplayNames));
        OnPropertyChanged(nameof(AvailableEditorEnterBehaviorDisplayNames));
        OnPropertyChanged(nameof(SelectedQuickCaptureItemPreviewLineCountIndex));
        OnPropertyChanged(nameof(SelectedQuickCaptureEditorEnterBehaviorIndex));
        OnPropertyChanged(nameof(SelectedTodoItemPreviewLineCountIndex));
        OnPropertyChanged(nameof(SelectedTodoEditorEnterBehaviorIndex));
    }

    private string GetEditorEnterBehaviorDisplayName(string behavior) =>
        SettingsService.NormalizeEditorEnterBehavior(behavior) ==
        SettingsService.EditorEnterBehaviorEnterSaves
            ? _localizationService.T("Settings.ContentEditor.EnterBehavior.EnterSaves")
            : _localizationService.T("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves");
}
