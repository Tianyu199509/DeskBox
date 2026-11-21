using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.QuickCapture;

/// <summary>
/// Quick Capture-section settings editor. Owns the section's XAML binding
/// surface (the feature switch, wide layout/open mode, the tab group incl. the
/// visibility flyout state machine, default view, tab style, content editor
/// preferences, preview line count, created-time visibility, remote images,
/// and the pushed image-cache text). This class references neither App nor
/// WinUI nor the settings adapter; localization and logging arrive as delegates.
/// </summary>
public sealed partial class QuickCaptureSettingsViewModel : ObservableObject
{
    private static readonly string[] DefaultViewValues =
    [
        QuickCaptureOptionKinds.DefaultViewRecords,
        QuickCaptureOptionKinds.DefaultViewPinned
    ];

    private static readonly string[] WideLayoutValues =
    [
        QuickCaptureOptionKinds.WideLayoutAuto,
        QuickCaptureOptionKinds.WideLayoutSinglePane,
        QuickCaptureOptionKinds.WideLayoutDualPane
    ];

    private static readonly string[] WideOpenModeValues =
    [
        QuickCaptureOptionKinds.WideOpenReading,
        QuickCaptureOptionKinds.WideOpenEditing
    ];

    private static readonly string[] FormatValues =
    [
        QuickCaptureOptionKinds.FormatMarkdown,
        QuickCaptureOptionKinds.FormatPlainText
    ];

    private static readonly string[] EnterBehaviorValues =
    [
        QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves,
        QuickCaptureOptionKinds.EnterBehaviorEnterSaves
    ];

    private readonly IQuickCaptureSettings _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _format;
    private readonly Action<string> _log;
    private readonly Action<Exception> _reportError;
    private bool _isSyncingPresentation;
    private bool _enabled;
    private bool _showTabBar = true;
    private bool _showRecordsTab = true;
    private bool _showPinnedTab = true;
    private string _selectedDefaultView = QuickCaptureOptionKinds.DefaultViewRecords;
    private string _tabStyle = QuickCaptureOptionKinds.TabStyleButton;
    private bool _showCreatedTime = true;
    private int _itemPreviewLineCount;
    private string _editorFormat = QuickCaptureOptionKinds.FormatMarkdown;
    private string _editorEnterBehavior = QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves;
    private string _wideLayout = QuickCaptureOptionKinds.WideLayoutAuto;
    private string _wideOpenMode = QuickCaptureOptionKinds.WideOpenReading;
    private bool _allowRemoteImages;
    private double _listTextSize;
    private double _contentTextSize;
    private bool _hasCustomTextSizeOverride;
    private string _imageCacheText;
    private bool _canClearImageCache;
    private string? _mirrorFolderPath;
    private string[]? _cachedDefaultViewNames;
    private string[]? _cachedWideLayoutNames;
    private string[]? _cachedWideOpenModeNames;
    private string[]? _cachedFormatNames;
    private string[]? _cachedEnterBehaviorNames;
    private string[]? _cachedPreviewLineNames;
    private int[] _previewLineCounts =
        Enumerable.Range(
            QuickCaptureOptionKinds.MinItemPreviewLineCount,
            QuickCaptureOptionKinds.MaxItemPreviewLineCount -
                QuickCaptureOptionKinds.MinItemPreviewLineCount + 1)
        .ToArray();

    public QuickCaptureSettingsViewModel(
        IQuickCaptureSettings settings,
        Func<string, string> localize,
        Func<string, object[], string> format,
        Action<string> log,
        Action<Exception> reportError)
    {
        _settings = settings;
        _localize = localize;
        _format = format;
        _log = log;
        _reportError = reportError;
        _imageCacheText = localize("Settings.QuickCapture.ImageCacheLoading");
        SyncPresentation();
    }

    public event Action? ListTextSizeCommitted;

    public event Action? ContentTextSizeCommitted;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanConfigureWideOpenMode));
            OnPropertyChanged(nameof(CanConfigureRemoteImages));
            OnPropertyChanged(nameof(CanEditRecordsTab));
            OnPropertyChanged(nameof(CanEditPinnedTab));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunUserAction(_settings.SetEnabledAsync(value, reveal: value));
        }
    }

    public string StatusText => Enabled
        ? _localize("Settings.QuickCapture.Status.Enabled")
        : _localize("Settings.QuickCapture.Status.Disabled");

    public bool ShowTabBar
    {
        get => _showTabBar;
        set
        {
            if (!SetProperty(ref _showTabBar, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TabsSummaryText));
            OnPropertyChanged(nameof(CanEditRecordsTab));
            OnPropertyChanged(nameof(CanEditPinnedTab));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetTabBarVisible(value));
        }
    }

    public bool ShowRecordsTab
    {
        get => _showRecordsTab;
        set => SetTabVisible(QuickCaptureOptionKinds.DefaultViewRecords, value);
    }

    public bool ShowPinnedTab
    {
        get => _showPinnedTab;
        set => SetTabVisible(QuickCaptureOptionKinds.DefaultViewPinned, value);
    }

    private void SetTabVisible(string view, bool visible)
    {
        if (!visible && !CanToggleTab(view) && !_isSyncingPresentation)
        {
            OnPropertyChanged(view == QuickCaptureOptionKinds.DefaultViewPinned
                ? nameof(ShowPinnedTab)
                : nameof(ShowRecordsTab));
            return;
        }

        bool changed = view switch
        {
            QuickCaptureOptionKinds.DefaultViewPinned => SetProperty(ref _showPinnedTab, visible),
            _ => SetProperty(ref _showRecordsTab, visible)
        };
        if (!changed)
        {
            return;
        }

        RefreshTabDerivedProperties();
        if (_isSyncingPresentation)
        {
            return;
        }

        RunWithFailureProjection(() => _settings.SetTabVisible(view, visible));
    }

    public string SelectedDefaultView
    {
        get => _selectedDefaultView;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeDefaultView(value);
            if (!SetProperty(ref _selectedDefaultView, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedDefaultViewKey));
            OnPropertyChanged(nameof(DefaultViewIndex));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetDefaultView(normalized));
            SyncPresentation();
        }
    }

    public string SelectedDefaultViewKey
    {
        get => SelectedDefaultView;
        set => SelectedDefaultView = value;
    }

    public int DefaultViewIndex
    {
        get => _selectedDefaultView == QuickCaptureOptionKinds.DefaultViewPinned ? 1 : 0;
        set => SelectedDefaultView = value == 1
            ? QuickCaptureOptionKinds.DefaultViewPinned
            : QuickCaptureOptionKinds.DefaultViewRecords;
    }

    public int TabStyleIndex
    {
        get => _tabStyle == QuickCaptureOptionKinds.TabStylePivot ? 0 : 1;
        set
        {
            string style = value == 0
                ? QuickCaptureOptionKinds.TabStylePivot
                : QuickCaptureOptionKinds.TabStyleButton;
            if (_tabStyle == style)
            {
                return;
            }

            _tabStyle = style;
            OnPropertyChanged(nameof(TabStyleIndex));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetTabStyle(style));
        }
    }

    public bool ShowCreatedTime
    {
        get => _showCreatedTime;
        set
        {
            if (!SetProperty(ref _showCreatedTime, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetShowCreatedTime(value));
        }
    }

    public int ItemPreviewLineCount
    {
        get => _itemPreviewLineCount;
        set
        {
            if (!SetProperty(ref _itemPreviewLineCount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ContentSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetPreviewLineCount(value));
        }
    }

    public string EditorFormat
    {
        get => _editorFormat;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeFormat(value);
            if (!SetProperty(ref _editorFormat, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(EditorFormatIndex));
            OnPropertyChanged(nameof(ShowRemoteImages));
            OnPropertyChanged(nameof(CanConfigureRemoteImages));
            OnPropertyChanged(nameof(ContentSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetEditorFormat(normalized);
        }
    }

    public string EditorEnterBehavior
    {
        get => _editorEnterBehavior;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeEnterBehavior(value);
            if (!SetProperty(ref _editorEnterBehavior, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ContentSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetEditorEnterBehavior(normalized);
        }
    }

    public string WideLayout
    {
        get => _wideLayout;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeWideLayout(value);
            if (!SetProperty(ref _wideLayout, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(LayoutSummaryText));
            OnPropertyChanged(nameof(WideLayoutSummaryText));
            OnPropertyChanged(nameof(ShowWideOptions));
            OnPropertyChanged(nameof(CanConfigureWideOpenMode));
            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetWideLayout(normalized);
        }
    }

    public string WideOpenMode
    {
        get => _wideOpenMode;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeWideOpenMode(value);
            if (!SetProperty(ref _wideOpenMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(LayoutSummaryText));
            OnPropertyChanged(nameof(WideLayoutSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            OnPropertyChanged(nameof(WideOpenModeIndex));
            _settings.SetWideOpenMode(normalized);
        }
    }

    public bool AllowRemoteImages
    {
        get => _allowRemoteImages;
        set
        {
            if (!SetProperty(ref _allowRemoteImages, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetAllowRemoteImages(value);
        }
    }

    public double ListTextSize
    {
        get => _listTextSize;
        set
        {
            if (Math.Abs(value - _listTextSize) <= 0.0001)
            {
                return;
            }

            _listTextSize = value;
            OnPropertyChanged(nameof(ListTextSize));
            OnPropertyChanged(nameof(ListTextSizeValueText));
            OnPropertyChanged(nameof(TextSizeSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            if (!double.IsFinite(value))
            {
                SyncPresentation();
                return;
            }

            double normalized = QuickCaptureOptionKinds.NormalizeTextSizeStep(value);
            if (Math.Abs(normalized - value) > 0.0001)
            {
                ListTextSize = normalized;
                return;
            }

            if (TrySetTextSize(normalized, list: true, out bool committed) &&
                committed)
            {
                ListTextSizeCommitted?.Invoke();
            }
        }
    }

    public string ListTextSizeValueText => $"{ListTextSize:0.#}pt";

    public double ContentTextSize
    {
        get => _contentTextSize;
        set
        {
            if (Math.Abs(value - _contentTextSize) <= 0.0001)
            {
                return;
            }

            _contentTextSize = value;
            OnPropertyChanged(nameof(ContentTextSize));
            OnPropertyChanged(nameof(ContentTextSizeValueText));
            OnPropertyChanged(nameof(TextSizeSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            if (!double.IsFinite(value))
            {
                SyncPresentation();
                return;
            }

            double normalized = QuickCaptureOptionKinds.NormalizeTextSizeStep(value);
            if (Math.Abs(normalized - value) > 0.0001)
            {
                ContentTextSize = normalized;
                return;
            }

            if (TrySetTextSize(normalized, list: false, out bool committed) &&
                committed)
            {
                ContentTextSizeCommitted?.Invoke();
            }
        }
    }

    public string ContentTextSizeValueText => $"{ContentTextSize:0.#}pt";

    public string TextSizeSummaryText =>
        $"{ListTextSizeValueText} · {ContentTextSizeValueText}";

    public bool HasCustomTextSizeOverride => _hasCustomTextSizeOverride;

    public void ResetTextSizesToGlobal()
    {
        bool committed = false;
        try
        {
            committed = _settings.ResetTextSizesToGlobal(scheduleSave: false);
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
        }

        SyncPresentation();
        if (committed)
        {
            ListTextSizeCommitted?.Invoke();
        }
    }

    public string LayoutSummaryText
    {
        get
        {
            string layout = GetWideLayoutDisplayName(WideLayout);
            return WideLayout == QuickCaptureOptionKinds.WideLayoutSinglePane
                ? layout
                : $"{layout} · {GetWideOpenModeDisplayName(WideOpenMode)}";
        }
    }

    public string WideLayoutSummaryText => LayoutSummaryText;

    public bool ShowWideOptions =>
        WideLayout != QuickCaptureOptionKinds.WideLayoutSinglePane;

    public bool ShowRemoteImages =>
        EditorFormat == QuickCaptureOptionKinds.FormatMarkdown;

    public string TabsSummaryText => ShowTabBar
        ? VisibleTabsText
        : _localize("Settings.Toggle.Off");

    public string VisibleTabsText => string.Join(
        " · ",
        DefaultViewValues.Where(IsTabSelected).Select(GetDefaultViewDisplayName));

    public IReadOnlyList<SettingsOption> AvailableDefaultViewOptions
    {
        get
        {
            _cachedDefaultViewNames ??= DefaultViewValues
                .Select(GetDefaultViewDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(DefaultViewValues, _cachedDefaultViewNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDefaultViewOptionItems =>
        AvailableDefaultViewOptions;

    public IReadOnlyList<SettingsOption> VisibleDefaultViewOptions
    {
        get
        {
            _cachedDefaultViewNames ??= DefaultViewValues
                .Select(GetDefaultViewDisplayName)
                .ToArray();
            SettingsOption[] options = DefaultViewValues
                .Select((value, index) => (value, index))
                .Where(item => IsTabSelected(item.value))
                .Select(item => new SettingsOption(
                    item.value,
                    _cachedDefaultViewNames[item.index]))
                .ToArray();
            return WrapOptions(options);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWideLayoutOptions
    {
        get
        {
            _cachedWideLayoutNames ??= WideLayoutValues
                .Select(GetWideLayoutDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(WideLayoutValues, _cachedWideLayoutNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWideOpenModeOptions
    {
        get
        {
            _cachedWideOpenModeNames ??= WideOpenModeValues
                .Select(GetWideOpenModeDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(WideOpenModeValues, _cachedWideOpenModeNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableFormatOptions
    {
        get
        {
            _cachedFormatNames ??= FormatValues
                .Select(GetFormatDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(FormatValues, _cachedFormatNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableEnterBehaviorOptions
    {
        get
        {
            _cachedEnterBehaviorNames ??= EnterBehaviorValues
                .Select(GetEnterBehaviorDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(EnterBehaviorValues, _cachedEnterBehaviorNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailablePreviewLineOptions
    {
        get
        {
            _cachedPreviewLineNames ??= _previewLineCounts
                .Select(GetPreviewLineDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(_previewLineCounts, _cachedPreviewLineNames));
        }
    }

    public string ContentSummaryText => string.Join(
        " · ",
        GetPreviewLineDisplayName(ItemPreviewLineCount),
        GetFormatDisplayName(EditorFormat),
        GetEnterBehaviorDisplayName(EditorEnterBehavior));

    public string ImageCacheText
    {
        get => _imageCacheText;
        private set => SetProperty(ref _imageCacheText, value);
    }

    public bool CanClearImageCache
    {
        get => _canClearImageCache;
        private set => SetProperty(ref _canClearImageCache, value);
    }

    public string[] AvailableDefaultViews => DefaultViewValues;

    public bool CanConfigureWideOpenMode => Enabled && ShowWideOptions;

    public int WideOpenModeIndex
    {
        get => WideOpenMode == QuickCaptureOptionKinds.WideOpenEditing ? 1 : 0;
        set => WideOpenMode = value == 1
            ? QuickCaptureOptionKinds.WideOpenEditing
            : QuickCaptureOptionKinds.WideOpenReading;
    }

    public int EditorFormatIndex
    {
        get => EditorFormat == QuickCaptureOptionKinds.FormatPlainText ? 1 : 0;
        set => EditorFormat = value == 1
            ? QuickCaptureOptionKinds.FormatPlainText
            : QuickCaptureOptionKinds.FormatMarkdown;
    }

    public bool CanConfigureRemoteImages => Enabled && ShowRemoteImages;

    public bool CanEditRecordsTab =>
        Enabled && ShowTabBar && CanToggleTab(QuickCaptureOptionKinds.DefaultViewRecords);

    public bool CanEditPinnedTab =>
        Enabled && ShowTabBar && CanToggleTab(QuickCaptureOptionKinds.DefaultViewPinned);

    public string? MirrorFolderPath => _mirrorFolderPath;

    public bool HasMirrorFolder => !string.IsNullOrWhiteSpace(_mirrorFolderPath);

    public string MirrorFolderDisplayText => HasMirrorFolder
        ? _mirrorFolderPath!
        : _localize("Settings.QuickCapture.MirrorFolder.Unset");

    public void UpdateMirrorFolderPresentation(string? folderPath)
    {
        string? normalized = string.IsNullOrWhiteSpace(folderPath)
            ? null
            : folderPath.Trim();
        if (string.Equals(_mirrorFolderPath, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _mirrorFolderPath = normalized;
        OnPropertyChanged(nameof(MirrorFolderPath));
        OnPropertyChanged(nameof(HasMirrorFolder));
        OnPropertyChanged(nameof(MirrorFolderDisplayText));
    }

    public string GetDefaultViewDisplayName(string view) =>
        QuickCaptureOptionKinds.NormalizeDefaultView(view) switch
        {
            QuickCaptureOptionKinds.DefaultViewPinned =>
                _localize("Settings.QuickCapture.DefaultView.Pinned"),
            _ => _localize("Settings.QuickCapture.DefaultView.Records")
        };

    public string GetWideLayoutDisplayName(string layout) =>
        QuickCaptureOptionKinds.NormalizeWideLayout(layout) switch
        {
            QuickCaptureOptionKinds.WideLayoutSinglePane =>
                _localize("Settings.QuickCapture.WideLayout.SinglePane"),
            QuickCaptureOptionKinds.WideLayoutDualPane =>
                _localize("Settings.QuickCapture.WideLayout.DualPane"),
            _ => _localize("Settings.QuickCapture.WideLayout.Auto")
        };

    public string GetWideOpenModeDisplayName(string mode) =>
        QuickCaptureOptionKinds.NormalizeWideOpenMode(mode) ==
            QuickCaptureOptionKinds.WideOpenEditing
                ? _localize("Settings.QuickCapture.WideOpen.Editing")
                : _localize("Settings.QuickCapture.WideOpen.Reading");

    public string GetFormatDisplayName(string format) =>
        QuickCaptureOptionKinds.NormalizeFormat(format) ==
            QuickCaptureOptionKinds.FormatPlainText
                ? _localize("Settings.QuickCapture.Format.PlainText")
                : _localize("Settings.QuickCapture.Format.Markdown");

    public string GetEnterBehaviorDisplayName(string behavior) =>
        QuickCaptureOptionKinds.NormalizeEnterBehavior(behavior) ==
            QuickCaptureOptionKinds.EnterBehaviorEnterSaves
                ? _localize("Settings.ContentEditor.EnterBehavior.EnterSaves")
                : _localize("Settings.ContentEditor.EnterBehavior.CtrlEnterSaves");

    public string GetPreviewLineDisplayName(int lineCount) => lineCount == 1
        ? _localize("Settings.ContentEditor.PreviewLines.Option.Single")
        : _format(
            "Settings.ContentEditor.PreviewLines.Option.Multiple",
            [lineCount]);

    public bool IsTabSelected(string view) =>
        QuickCaptureOptionKinds.NormalizeDefaultView(view) switch
        {
            QuickCaptureOptionKinds.DefaultViewPinned => _showPinnedTab,
            _ => _showRecordsTab
        };

    public bool CanToggleTab(string view) =>
        !IsTabSelected(view) || CountSelectedTabs() > 1;

    public void ToggleTab(string view)
    {
        bool selected = IsTabSelected(view);
        if (selected && !CanToggleTab(view))
        {
            return;
        }

        switch (QuickCaptureOptionKinds.NormalizeDefaultView(view))
        {
            case QuickCaptureOptionKinds.DefaultViewPinned:
                ShowPinnedTab = !selected;
                break;
            default:
                ShowRecordsTab = !selected;
                break;
        }
    }

    public void SetFeatureEnabled(bool enabled)
    {
        _isSyncingPresentation = true;
        try
        {
            Enabled = enabled;
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    public void SyncPresentation()
    {
        QuickCaptureSettingsSnapshot state = _settings.Read();
        QuickCaptureTabSettings tabs = _settings.ReadTabs();
        QuickCapturePresentationSettings presentation = _settings.ReadPresentation();
        QuickCaptureTextSizeSettings textSizes = _settings.ReadTextSizes();
        bool hasCustomTextSizeOverride = _settings.HasCustomTextSizeOverride;
        QuickCaptureEditorSettings editor = _settings.ReadEditorSettings();
        _isSyncingPresentation = true;
        try
        {
            Enabled = state.Enabled;
            ShowTabBar = tabs.ShowTabBar;
            ShowRecordsTab = tabs.ShowRecordsTab;
            ShowPinnedTab = tabs.ShowPinnedTab;
            SelectedDefaultView = tabs.DefaultView;
            TabStyleIndex = presentation.TabStyle ==
                    QuickCaptureOptionKinds.TabStylePivot
                ? 0
                : 1;
            ShowCreatedTime = presentation.ShowCreatedTime;
            ItemPreviewLineCount = presentation.PreviewLineCount;
            EditorFormat = editor.DefaultFormat;
            EditorEnterBehavior = editor.EnterBehavior;
            WideLayout = editor.WideLayout;
            WideOpenMode = editor.WideOpenMode;
            AllowRemoteImages = editor.AllowRemoteImages;
            ListTextSize = textSizes.ListTextSize;
            ContentTextSize = textSizes.ContentTextSize;
            _hasCustomTextSizeOverride = hasCustomTextSizeOverride;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        RefreshTabDerivedProperties();
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(WideLayoutSummaryText));
        OnPropertyChanged(nameof(ShowWideOptions));
        OnPropertyChanged(nameof(CanConfigureWideOpenMode));
        OnPropertyChanged(nameof(WideOpenModeIndex));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(ShowRemoteImages));
        OnPropertyChanged(nameof(CanConfigureRemoteImages));
        OnPropertyChanged(nameof(EditorFormatIndex));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TextSizeSummaryText));
        OnPropertyChanged(nameof(HasCustomTextSizeOverride));
    }

    public void RefreshLocalization()
    {
        _cachedDefaultViewNames = null;
        _cachedWideLayoutNames = null;
        _cachedWideOpenModeNames = null;
        _cachedFormatNames = null;
        _cachedEnterBehaviorNames = null;
        _cachedPreviewLineNames = null;
        OnPropertyChanged(nameof(AvailableDefaultViewOptions));
        OnPropertyChanged(nameof(AvailableDefaultViewOptionItems));
        OnPropertyChanged(nameof(AvailableWideLayoutOptions));
        OnPropertyChanged(nameof(AvailableWideOpenModeOptions));
        OnPropertyChanged(nameof(AvailableFormatOptions));
        OnPropertyChanged(nameof(AvailableEnterBehaviorOptions));
        OnPropertyChanged(nameof(AvailablePreviewLineOptions));
        OnPropertyChanged(nameof(VisibleDefaultViewOptions));
        OnPropertyChanged(nameof(SelectedDefaultView));
        OnPropertyChanged(nameof(SelectedDefaultViewKey));
        OnPropertyChanged(nameof(DefaultViewIndex));
        OnPropertyChanged(nameof(CanEditRecordsTab));
        OnPropertyChanged(nameof(CanEditPinnedTab));
        OnPropertyChanged(nameof(WideLayout));
        OnPropertyChanged(nameof(WideOpenMode));
        OnPropertyChanged(nameof(WideOpenModeIndex));
        OnPropertyChanged(nameof(EditorFormatIndex));
        OnPropertyChanged(nameof(MirrorFolderDisplayText));
        OnPropertyChanged(nameof(SelectedDefaultView));
        OnPropertyChanged(nameof(ItemPreviewLineCount));
        OnPropertyChanged(nameof(EditorFormat));
        OnPropertyChanged(nameof(EditorEnterBehavior));
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(WideLayoutSummaryText));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ListTextSizeValueText));
        OnPropertyChanged(nameof(ContentTextSizeValueText));
        OnPropertyChanged(nameof(TextSizeSummaryText));
        RefreshTabDerivedProperties();
    }

    public void UpdateImageCachePresentation(string text, bool canClear)
    {
        ImageCacheText = text;
        CanClearImageCache = canClear;
    }

    private void RefreshTabDerivedProperties()
    {
        OnPropertyChanged(nameof(VisibleTabsText));
        OnPropertyChanged(nameof(TabsSummaryText));
        OnPropertyChanged(nameof(AvailableDefaultViewOptions));
        OnPropertyChanged(nameof(AvailableDefaultViewOptionItems));
        OnPropertyChanged(nameof(VisibleDefaultViewOptions));
        OnPropertyChanged(nameof(SelectedDefaultView));
        OnPropertyChanged(nameof(SelectedDefaultViewKey));
        OnPropertyChanged(nameof(DefaultViewIndex));
        OnPropertyChanged(nameof(CanEditRecordsTab));
        OnPropertyChanged(nameof(CanEditPinnedTab));
    }

    private int CountSelectedTabs() =>
        DefaultViewValues.Count(IsTabSelected);

    private async void RunUserAction(Task action)
    {
        try
        {
            await action;
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
        }
        finally
        {
            if (!_isSyncingPresentation)
            {
                SyncPresentation();
            }
        }
    }

    private void RunWithFailureProjection(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
            SyncPresentation();
        }
    }

    private bool TrySetTextSize(double normalized, bool list, out bool committed)
    {
        try
        {
            committed = list
                ? _settings.TrySetListTextSize(normalized, scheduleSave: false)
                : _settings.TrySetContentTextSize(normalized, scheduleSave: false);
            if (committed)
            {
                SetProperty(ref _hasCustomTextSizeOverride, _settings.HasCustomTextSizeOverride, nameof(HasCustomTextSizeOverride));
            }

            return true;
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
            SyncPresentation();
            committed = false;
            return false;
        }
    }

    private static SettingsOption[] BuildOptions<T>(T[] values, string[] displayNames)
    {
        var options = new SettingsOption[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            options[index] = new SettingsOption(values[index]!, displayNames[index]);
        }

        return options;
    }

    private static IReadOnlyList<SettingsOption> WrapOptions(SettingsOption[] options) =>
        options;
}