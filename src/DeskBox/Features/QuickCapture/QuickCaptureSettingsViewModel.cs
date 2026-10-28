using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.QuickCapture;

/// <summary>
/// Quick Capture-section settings editor. Owns the section's XAML binding
/// surface (the feature switch, the recording switches with their
/// coordinator-side enablement chaining, wide layout/open mode, the tab
/// group incl. the visibility flyout state machine, default view, tab
/// style, content editor preferences, preview line count, created-time
/// visibility, remote images, the recent-capacity NumberBox and the pushed
/// clipboard-diagnostics/image-cache texts): reads project through the
/// snapshots of <see cref="IQuickCaptureSettings"/> (normalized with the
/// old shell-constructor semantics), user edits write through the same
/// coordinator ports the legacy shell used, and external refresh paths
/// (settings broadcasts, feature-card default restores, language changes)
/// re-sync the projection instead of writing back. The recording chain's
/// host work (enabling the widget when a recording switch turns on, the
/// debounced recent-item trims) stays inside the coordinator; the editor
/// re-projects from the coordinator's <c>Changed</c> broadcasts so the
/// three linked switches follow each other. The image-cache line and its
/// cleanup gate need host services the editor must not reference, so the
/// shell scans and pushes them through <see cref="UpdateImageCachePresentation"/>.
/// The bindable property names intentionally drop the legacy
/// <c>QuickCapture</c>/<c>Selected</c> prefixes: the section-level
/// DataContext switch means they no longer need to be unique across the
/// whole shell, and unprefixed names keep the flat <c>AppSettings</c>
/// facade-name ratchet shrinking. This class references neither App nor
/// WinUI nor the settings adapter; localization and logging arrive as
/// delegates.
/// </summary>
public sealed partial class QuickCaptureSettingsViewModel : ObservableObject
{
    private static readonly string[] DefaultViewValues =
    [
        QuickCaptureOptionKinds.DefaultViewRecords,
        QuickCaptureOptionKinds.DefaultViewPinned,
        QuickCaptureOptionKinds.DefaultViewRecent
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
    private bool _clipboardEnabled;
    private bool _imageClipboardEnabled;
    private bool _showTabBar = true;
    private bool _showRecordsTab = true;
    private bool _showPinnedTab = true;
    private bool _showRecentTab = true;
    private string _selectedDefaultView = QuickCaptureOptionKinds.DefaultViewRecords;
    private string _tabStyle = QuickCaptureOptionKinds.TabStyleButton;
    private bool _showCreatedTime = true;
    private int _itemPreviewLineCount;
    private string _editorFormat = QuickCaptureOptionKinds.FormatMarkdown;
    private string _editorEnterBehavior = QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves;
    private string _wideLayout = QuickCaptureOptionKinds.WideLayoutAuto;
    private string _wideOpenMode = QuickCaptureOptionKinds.WideOpenReading;
    private bool _allowRemoteImages;
    private int _recentLimit;
    private double _listTextSize;
    private double _contentTextSize;
    private string _clipboardDiagnosticsText;
    private string _imageCacheText;
    private bool _canClearImageCache;
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
        _clipboardDiagnosticsText = localize("Settings.QuickCapture.ClipboardDiagnosticsUnavailable");
        _imageCacheText = localize("Settings.QuickCapture.ImageCacheLoading");
        SyncPresentation();
        RefreshClipboardDiagnostics();
    }

    /// <summary>
    /// Raised after a user edit committed a Quick Capture text-size write
    /// with <c>scheduleSave:false</c>; the shell answers with the shared
    /// appearance save pass (preview + debounced persistence, incl. the
    /// slider-drag suppression flags).
    /// </summary>
    public event Action? ListTextSizeCommitted;

    /// <summary>See <see cref="ListTextSizeCommitted"/>.</summary>
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

    public bool ClipboardEnabled
    {
        get => _clipboardEnabled;
        set
        {
            if (!SetProperty(ref _clipboardEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            if (!value)
            {
                _log("[QuickCaptureClipboard] Disabled from settings");
            }

            RunUserAction(_settings.SetClipboardEnabledAsync(
                value, captureCurrent: value));
        }
    }

    public bool ImageClipboardEnabled
    {
        get => _imageClipboardEnabled;
        set
        {
            if (!SetProperty(ref _imageClipboardEnabled, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunUserAction(_settings.SetImageEnabledAsync(
                value, captureCurrent: value));
        }
    }

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

    public bool ShowRecentTab
    {
        get => _showRecentTab;
        set => SetTabVisible(QuickCaptureOptionKinds.DefaultViewRecent, value);
    }

    private void SetTabVisible(string view, bool visible)
    {
        bool changed = view switch
        {
            QuickCaptureOptionKinds.DefaultViewPinned => SetProperty(ref _showPinnedTab, visible),
            QuickCaptureOptionKinds.DefaultViewRecent => SetProperty(ref _showRecentTab, visible),
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
            if (!SetProperty(ref _selectedDefaultView, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWithFailureProjection(() => _settings.SetDefaultView(value));
        }
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

            OnPropertyChanged(nameof(ShowRemoteImages));
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
            OnPropertyChanged(nameof(ShowWideOptions));
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
            if (_isSyncingPresentation)
            {
                return;
            }

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

    public int RecentLimit
    {
        get => _recentLimit;
        set
        {
            if (!SetProperty(ref _recentLimit, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RecentLimitText));
            if (_isSyncingPresentation)
            {
                return;
            }

            // The coordinator owns the normalization, the debounced save and
            // the 350 ms quiet-period recent-item trim chain; its Changed
            // broadcast re-projects the normalized value back here.
            RunWithFailureProjection(() => _settings.SetRecentLimit(value));
        }
    }

    public string RecentLimitText => _format(
        "Settings.QuickCapture.RecentLimitValue",
        [RecentLimit]);

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
            if (_isSyncingPresentation)
            {
                return;
            }

            if (!double.IsFinite(value))
            {
                // Non-finite slider input re-projects from the effective
                // snapshot (stored zero inherits the global size).
                SyncPresentation();
                return;
            }

            double normalized = QuickCaptureOptionKinds.NormalizeTextSizeStep(value);
            if (Math.Abs(normalized - value) > 0.0001)
            {
                // Snap the control back onto the half-point grid; the
                // re-entry carries the store write and the commit event.
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

    public string ClipboardDiagnosticsText
    {
        get => _clipboardDiagnosticsText;
        private set => SetProperty(ref _clipboardDiagnosticsText, value);
    }

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

    public string GetDefaultViewDisplayName(string view) =>
        QuickCaptureOptionKinds.NormalizeDefaultView(view) switch
        {
            QuickCaptureOptionKinds.DefaultViewPinned =>
                _localize("Settings.QuickCapture.DefaultView.Pinned"),
            QuickCaptureOptionKinds.DefaultViewRecent =>
                _localize("Settings.QuickCapture.DefaultView.Recent"),
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
            QuickCaptureOptionKinds.DefaultViewRecent => _showRecentTab,
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
            case QuickCaptureOptionKinds.DefaultViewRecent:
                ShowRecentTab = !selected;
                break;
            default:
                ShowRecordsTab = !selected;
                break;
        }
    }

    /// <summary>
    /// The feature-card toggle's mirror path (the overview row in the
    /// feature-widgets section): flips the projection without re-entering
    /// the section switch's enable chain — the card itself sends the
    /// SetFeatureWidgetEnabled command, and a plain Enabled assignment here
    /// would fire a second, racing one (batch R8).
    /// </summary>
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

    /// <summary>
    /// Re-projects the persisted Quick Capture state onto the binding
    /// surface without writing back. Called on construction, settings
    /// broadcasts and default restores; the coordinator snapshots already
    /// normalize with the old shell-constructor semantics.
    /// </summary>
    public void SyncPresentation()
    {
        QuickCaptureSettingsSnapshot state = _settings.Read();
        QuickCaptureTabSettings tabs = _settings.ReadTabs();
        QuickCapturePresentationSettings presentation = _settings.ReadPresentation();
        QuickCaptureTextSizeSettings textSizes = _settings.ReadTextSizes();
        QuickCaptureEditorSettings editor = _settings.ReadEditorSettings();
        int recentLimit = _settings.ReadRecentLimit();
        _isSyncingPresentation = true;
        try
        {
            Enabled = state.Enabled;
            ClipboardEnabled = state.ClipboardEnabled;
            ImageClipboardEnabled = state.ImageEnabled;
            ShowTabBar = tabs.ShowTabBar;
            ShowRecordsTab = tabs.ShowRecordsTab;
            ShowPinnedTab = tabs.ShowPinnedTab;
            ShowRecentTab = tabs.ShowRecentTab;
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
            RecentLimit = recentLimit;
            ListTextSize = textSizes.ListTextSize;
            ContentTextSize = textSizes.ContentTextSize;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        RefreshTabDerivedProperties();
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(ShowWideOptions));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(ShowRemoteImages));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>
    /// Drops the localized option-name caches after a language change so the
    /// option tables, summaries and the tab texts re-project in the new
    /// language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedDefaultViewNames = null;
        _cachedWideLayoutNames = null;
        _cachedWideOpenModeNames = null;
        _cachedFormatNames = null;
        _cachedEnterBehaviorNames = null;
        _cachedPreviewLineNames = null;
        OnPropertyChanged(nameof(AvailableWideLayoutOptions));
        OnPropertyChanged(nameof(AvailableWideOpenModeOptions));
        OnPropertyChanged(nameof(AvailableFormatOptions));
        OnPropertyChanged(nameof(AvailableEnterBehaviorOptions));
        OnPropertyChanged(nameof(AvailablePreviewLineOptions));
        OnPropertyChanged(nameof(VisibleDefaultViewOptions));
        // Re-notify the current selections: replacing the localized option
        // arrays makes WinUI reset every bound ComboBox.SelectedIndex to -1.
        OnPropertyChanged(nameof(WideLayout));
        OnPropertyChanged(nameof(WideOpenMode));
        OnPropertyChanged(nameof(SelectedDefaultView));
        OnPropertyChanged(nameof(ItemPreviewLineCount));
        OnPropertyChanged(nameof(EditorFormat));
        OnPropertyChanged(nameof(EditorEnterBehavior));
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RecentLimitText));
        OnPropertyChanged(nameof(ListTextSizeValueText));
        OnPropertyChanged(nameof(ContentTextSizeValueText));
        RefreshTabDerivedProperties();
        RefreshClipboardDiagnostics();
    }

    /// <summary>
    /// Rebuilds the clipboard-diagnostics line from the coordinator's
    /// current diagnostics snapshot (called by the shell on the
    /// diagnostics-changed broadcast and after enable-state changes).
    /// </summary>
    public void RefreshClipboardDiagnostics()
    {
        var diagnostics = _settings.ClipboardDiagnostics;
        if (diagnostics is null)
        {
            QuickCaptureSettingsSnapshot state = _settings.Read();
            string inactiveReason = !state.Enabled
                ? "disabled:quick-capture-off"
                : !state.ClipboardEnabled
                    ? "disabled:clipboard-off"
                    : "disabled:unknown";
            ClipboardDiagnosticsText = _format(
                "Settings.QuickCapture.ClipboardDiagnosticsNotRecordingNoCapture",
                [GetClipboardReasonText(inactiveReason)]);
            return;
        }

        string reasonText = GetClipboardReasonText(diagnostics.LastReason);
        if (diagnostics.LastCapturedAt is { } capturedAt)
        {
            ClipboardDiagnosticsText = _format(
                diagnostics.IsRecording && diagnostics.IsListening
                    ? "Settings.QuickCapture.ClipboardDiagnosticsRecording"
                    : "Settings.QuickCapture.ClipboardDiagnosticsNotRecording",
                [
                    capturedAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                    reasonText
                ]);
            return;
        }

        ClipboardDiagnosticsText = _format(
            diagnostics.IsRecording && diagnostics.IsListening
                ? "Settings.QuickCapture.ClipboardDiagnosticsNoCapture"
                : "Settings.QuickCapture.ClipboardDiagnosticsNotRecordingNoCapture",
            [reasonText]);
    }

    /// <summary>
    /// Pushed by the shell after scanning the host-side image cache (the
    /// shell owns the Quick Capture service); carries the localized line and
    /// the cleanup gate.
    /// </summary>
    public void UpdateImageCachePresentation(string text, bool canClear)
    {
        ImageCacheText = text;
        CanClearImageCache = canClear;
    }

    private string GetClipboardReasonText(string reason)
    {
        string key = reason switch
        {
            "enabled" => "Settings.QuickCapture.ClipboardReason.Enabled",
            "disabled:quick-capture-off" => "Settings.QuickCapture.ClipboardReason.QuickCaptureOff",
            "disabled:clipboard-off" => "Settings.QuickCapture.ClipboardReason.ClipboardOff",
            "disabled:notice-unconfirmed" => "Settings.QuickCapture.ClipboardReason.NoticeUnconfirmed",
            "ignored:empty-or-unsupported" => "Settings.QuickCapture.ClipboardReason.EmptyOrUnsupported",
            "ignored:deskbox-write" => "Settings.QuickCapture.ClipboardReason.DeskBoxWrite",
            "ignored:image-recording-off" => "Settings.QuickCapture.ClipboardReason.ImageOff",
            "ignored:image-too-large" => "Settings.QuickCapture.ClipboardReason.ImageTooLarge",
            "ignored:text-too-large" => "Settings.QuickCapture.ClipboardReason.TextTooLarge",
            "ignored:duplicate-or-app-write" => "Settings.QuickCapture.ClipboardReason.Duplicate",
            "failed:read-or-save" => "Settings.QuickCapture.ClipboardReason.Failed",
            _ when reason.StartsWith("captured:", StringComparison.Ordinal) =>
                "Settings.QuickCapture.ClipboardReason.Captured",
            _ => "Settings.QuickCapture.ClipboardReason.Unknown"
        };

        return _localize(key);
    }

    private void RefreshTabDerivedProperties()
    {
        OnPropertyChanged(nameof(VisibleTabsText));
        OnPropertyChanged(nameof(TabsSummaryText));
        OnPropertyChanged(nameof(VisibleDefaultViewOptions));
    }

    private int CountSelectedTabs() =>
        DefaultViewValues.Count(IsTabSelected);

    // Mirrors the recording-switch chain the legacy shell tracked around
    // the coordinator calls: await the host work, report failures, and
    // re-project from the (possibly chained) coordinator state afterwards.
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
                RefreshClipboardDiagnostics();
            }
        }
    }

    // The old shell wrapped these coordinator ports with a log-and-resync
    // catch (they throw once the coordinator is stopping); keep that.
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

    // Build real SettingsOption[] arrays (not collection expressions): the
    // hidden read-only-array type cannot marshal across the WinRT ABI in
    // Native AOT builds and would leave the ItemsSource empty.
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
