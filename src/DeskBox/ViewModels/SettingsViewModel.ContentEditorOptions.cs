using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _quickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
    private string _quickCaptureEditorFormat = SettingsService.QuickCaptureFormatMarkdown;
    private string _quickCaptureWideLayout = SettingsService.QuickCaptureWideLayoutAuto;
    private string _quickCaptureWideOpenMode = SettingsService.QuickCaptureWideOpenReading;
    private bool _quickCaptureAllowRemoteImages;
    private string[]? _cachedItemPreviewLineCountDisplayNames;
    private string[]? _cachedEditorEnterBehaviorDisplayNames;
    private string[]? _cachedQuickCaptureFormatDisplayNames;
    private string[]? _cachedQuickCaptureWideLayoutDisplayNames;
    private string[]? _cachedQuickCaptureWideOpenModeDisplayNames;

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

    public string[] AvailableQuickCaptureFormats { get; } =
    [
        SettingsService.QuickCaptureFormatMarkdown,
        SettingsService.QuickCaptureFormatPlainText
    ];

    public string[] AvailableQuickCaptureFormatDisplayNames =>
        _cachedQuickCaptureFormatDisplayNames ??=
            AvailableQuickCaptureFormats.Select(GetQuickCaptureFormatDisplayName).ToArray();

    public string[] AvailableQuickCaptureWideLayouts { get; } =
    [
        SettingsService.QuickCaptureWideLayoutAuto,
        SettingsService.QuickCaptureWideLayoutSinglePane,
        SettingsService.QuickCaptureWideLayoutDualPane
    ];

    public string[] AvailableQuickCaptureWideLayoutDisplayNames =>
        _cachedQuickCaptureWideLayoutDisplayNames ??=
            AvailableQuickCaptureWideLayouts.Select(GetQuickCaptureWideLayoutDisplayName).ToArray();

    public string[] AvailableQuickCaptureWideOpenModes { get; } =
    [
        SettingsService.QuickCaptureWideOpenReading,
        SettingsService.QuickCaptureWideOpenEditing
    ];

    public string[] AvailableQuickCaptureWideOpenModeDisplayNames =>
        _cachedQuickCaptureWideOpenModeDisplayNames ??=
            AvailableQuickCaptureWideOpenModes.Select(GetQuickCaptureWideOpenModeDisplayName).ToArray();

    public int QuickCaptureItemPreviewLineCount
    {
        get => _quickCaptureSettings.ReadPresentation().PreviewLineCount;
        set => ApplyQuickCapturePreviewLineCount(value);
    }


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

            RefreshQuickCaptureContentPresentation();

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureEditorEnterBehavior = normalized;
            _settingsService.SaveDebounced();
        }
    }

    public string QuickCaptureEditorFormat
    {
        get => _quickCaptureEditorFormat;
        set
        {
            SetQuickCaptureSetting(
                ref _quickCaptureEditorFormat,
                SettingsService.NormalizeQuickCaptureFormat(value),
                normalized => _settingsService.Settings.QuickCaptureDefaultFormat = normalized,
                nameof(QuickCaptureEditorFormat));
            RefreshQuickCaptureContentPresentation();
        }
    }

    public string QuickCaptureWideLayout
    {
        get => _quickCaptureWideLayout;
        set
        {
            SetQuickCaptureSetting(
                ref _quickCaptureWideLayout,
                SettingsService.NormalizeQuickCaptureWideLayout(value),
                normalized => _settingsService.Settings.QuickCaptureWideLayout = normalized,
                nameof(QuickCaptureWideLayout));
            OnPropertyChanged(nameof(QuickCaptureLayoutSummaryText));
            OnPropertyChanged(nameof(QuickCaptureWideOptionsVisibility));
        }
    }

    public string QuickCaptureWideOpenMode
    {
        get => _quickCaptureWideOpenMode;
        set
        {
            SetQuickCaptureSetting(
                ref _quickCaptureWideOpenMode,
                SettingsService.NormalizeQuickCaptureWideOpenMode(value),
                normalized => _settingsService.Settings.QuickCaptureWideOpenMode = normalized,
                nameof(QuickCaptureWideOpenMode));
            OnPropertyChanged(nameof(QuickCaptureLayoutSummaryText));
        }
    }

    public bool QuickCaptureAllowRemoteImages
    {
        get => _quickCaptureAllowRemoteImages;
        set
        {
            if (!SetProperty(ref _quickCaptureAllowRemoteImages, value) ||
                _isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureAllowRemoteImages = value;
            _settingsService.SaveDebounced();
        }
    }


    public int TodoItemPreviewLineCount
    {
        get => _todoSettings.PreviewLineCount;
        set
        {
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }
            _todoSettings.PreviewLineCount = value;
        }
    }


    public string TodoEditorEnterBehavior
    {
        get => _todoSettings.EditorEnterBehavior;
        set
        {
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }
            _todoSettings.EditorEnterBehavior = value;
        }
    }


    private void InitializeContentEditorSettings(AppSettings settings)
    {
        _quickCaptureEditorEnterBehavior = SettingsService.NormalizeEditorEnterBehavior(
            settings.QuickCaptureEditorEnterBehavior);
        _quickCaptureEditorFormat = SettingsService.NormalizeQuickCaptureFormat(
            settings.QuickCaptureDefaultFormat);
        _quickCaptureWideLayout = SettingsService.NormalizeQuickCaptureWideLayout(
            settings.QuickCaptureWideLayout);
        _quickCaptureWideOpenMode = SettingsService.NormalizeQuickCaptureWideOpenMode(
            settings.QuickCaptureWideOpenMode);
        _quickCaptureAllowRemoteImages = settings.QuickCaptureAllowRemoteImages;
    }

    private void ApplyContentEditorSettingsSnapshot(AppSettings settings)
    {
        QuickCaptureEditorEnterBehavior = settings.QuickCaptureEditorEnterBehavior;
        QuickCaptureEditorFormat = settings.QuickCaptureDefaultFormat;
        QuickCaptureWideLayout = settings.QuickCaptureWideLayout;
        QuickCaptureWideOpenMode = settings.QuickCaptureWideOpenMode;
        QuickCaptureAllowRemoteImages = settings.QuickCaptureAllowRemoteImages;
    }

    private void RefreshContentEditorLocalizedProperties()
    {
        _cachedItemPreviewLineCountDisplayNames = null;
        _cachedEditorEnterBehaviorDisplayNames = null;
        _cachedQuickCaptureFormatDisplayNames = null;
        _cachedQuickCaptureWideLayoutDisplayNames = null;
        _cachedQuickCaptureWideOpenModeDisplayNames = null;
        OnPropertyChanged(nameof(AvailableItemPreviewLineCountDisplayNames));
        OnPropertyChanged(nameof(AvailableEditorEnterBehaviorDisplayNames));
        OnPropertyChanged(nameof(AvailableQuickCaptureFormatDisplayNames));
        OnPropertyChanged(nameof(AvailableQuickCaptureWideLayoutDisplayNames));
        OnPropertyChanged(nameof(AvailableQuickCaptureWideOpenModeDisplayNames));
        OnPropertyChanged(nameof(QuickCaptureLayoutSummaryText));
        OnPropertyChanged(nameof(QuickCaptureContentSummaryText));
        OnPropertyChanged(nameof(TodoContentSummaryText));
    }

    private string GetEditorEnterBehaviorDisplayName(string behavior) =>
        SettingsService.NormalizeEditorEnterBehavior(behavior) ==
        SettingsService.EditorEnterBehaviorEnterSaves
            ? _localizationService.T("Settings.ContentEditor.EnterBehavior.EnterSaves")
            : _localizationService.T("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves");

    private string GetQuickCaptureFormatDisplayName(string format) =>
        SettingsService.NormalizeQuickCaptureFormat(format) == SettingsService.QuickCaptureFormatPlainText
            ? _localizationService.T("Settings.QuickCapture.Format.PlainText")
            : _localizationService.T("Settings.QuickCapture.Format.Markdown");

    private string GetQuickCaptureWideLayoutDisplayName(string layout) =>
        SettingsService.NormalizeQuickCaptureWideLayout(layout) switch
        {
            SettingsService.QuickCaptureWideLayoutSinglePane =>
                _localizationService.T("Settings.QuickCapture.WideLayout.SinglePane"),
            SettingsService.QuickCaptureWideLayoutDualPane =>
                _localizationService.T("Settings.QuickCapture.WideLayout.DualPane"),
            _ => _localizationService.T("Settings.QuickCapture.WideLayout.Auto")
        };

    private string GetQuickCaptureWideOpenModeDisplayName(string mode) =>
        SettingsService.NormalizeQuickCaptureWideOpenMode(mode) == SettingsService.QuickCaptureWideOpenEditing
            ? _localizationService.T("Settings.QuickCapture.WideOpen.Editing")
            : _localizationService.T("Settings.QuickCapture.WideOpen.Reading");

    private void SetQuickCaptureSetting(
        ref string field,
        string value,
        Action<string> apply,
        string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName) || _isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        apply(value);
        _settingsService.SaveDebounced();
    }
}
