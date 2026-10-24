using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Todo;

/// <summary>
/// Todo-section settings editor. Owns the section's XAML binding surface
/// (the feature switch, the reminder group, the layout group incl. the
/// wide-layout auto-select gate, the tab group incl. the visibility-flyout
/// state machine and the default-filter linkage, the content group with
/// both text-size sliders, preview line count, new-task position and enter
/// behavior, and the footer-display flyout): reads project through the
/// snapshots of <see cref="ITodoSettings"/> (normalized with the old
/// shell-constructor semantics), user edits write through the same
/// coordinator ports the legacy shell used, and the write path immediately
/// re-projects from the coordinator's snapshot so the batch-14 linkage
/// (picking a hidden default filter reveals its tab, hiding the
/// default-filter tab falls the filter back to the first visible one, the
/// last visible tab is protected) renders through the coordinator's own
/// normalization instead of editor-side state duplication. External refresh
/// paths (settings broadcasts, appearance text-size commits, feature-card
/// default restores, language changes) call <see cref="Refresh"/>/
/// <see cref="RefreshLocalization"/> to re-sync the projection without
/// writing back. The enable switch keeps its serialized async chain from
/// batch 1 (generation guard + pending-projection). The bindable property
/// names intentionally drop the legacy <c>Todo</c>/<c>Selected</c> prefixes:
/// the section-level DataContext switch means they no longer need to be
/// unique across the whole shell, and unprefixed names keep the flat
/// <c>AppSettings</c> facade-name ratchet shrinking. This class references
/// neither App nor WinUI nor the settings adapter; localization and error
/// reporting arrive as delegates.
/// </summary>
public sealed partial class TodoSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] DefaultFilterValues =
    [
        TodoOptionKinds.DefaultFilterAll,
        TodoOptionKinds.DefaultFilterActive,
        TodoOptionKinds.DefaultFilterToday,
        TodoOptionKinds.DefaultFilterThisWeek,
        TodoOptionKinds.DefaultFilterThisMonth,
        TodoOptionKinds.DefaultFilterImportant,
        TodoOptionKinds.DefaultFilterCompleted
    ];

    private static readonly string[] LayoutModeValues =
    [
        TodoOptionKinds.LayoutModeAuto,
        TodoOptionKinds.LayoutModeSinglePane,
        TodoOptionKinds.LayoutModeDualPane
    ];

    private static readonly string[] NewTaskPositionValues =
    [
        TodoOptionKinds.NewTaskPositionTop,
        TodoOptionKinds.NewTaskPositionBottom
    ];

    private static readonly string[] EnterBehaviorValues =
    [
        QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves,
        QuickCaptureOptionKinds.EnterBehaviorEnterSaves
    ];

    private static readonly string[] FooterDisplayValues = ["Stats", "ClearCompleted"];

    private readonly ITodoSettings _settings;
    private readonly Func<string, string> _localize;
    private readonly Func<string, object[], string> _format;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int[] _previewLineCounts =
        Enumerable.Range(
            QuickCaptureOptionKinds.MinItemPreviewLineCount,
            QuickCaptureOptionKinds.MaxItemPreviewLineCount -
                QuickCaptureOptionKinds.MinItemPreviewLineCount + 1)
        .ToArray();
    private bool _isSyncingPresentation;
    private bool _enabled;
    private bool _remindersEnabled = true;
    private int _defaultOffsetMinutes = TodoOptionKinds.DefaultReminderOffsetMinutes;
    private string _layoutMode = TodoOptionKinds.LayoutModeAuto;
    private bool _autoSelectFirstInWideLayout = true;
    private bool _showTabBar = true;
    private bool _showAllTab = true;
    private bool _showActiveTab;
    private bool _showTodayTab = true;
    private bool _showThisWeekTab;
    private bool _showThisMonthTab;
    private bool _showImportantTab = true;
    private bool _showCompletedTab = true;
    private string _defaultFilter = TodoOptionKinds.DefaultFilterAll;
    private string _tabStyle = QuickCaptureOptionKinds.TabStyleButton;
    private bool _showCompletedTasks;
    private bool _showFooterStats;
    private bool _showClearCompletedButton = true;
    private int _itemPreviewLineCount = TodoOptionKinds.DefaultItemPreviewLineCount;
    private string _newTaskPosition = TodoOptionKinds.NewTaskPositionTop;
    private string _editorEnterBehavior = QuickCaptureOptionKinds.EnterBehaviorCtrlEnterSaves;
    private double _listTextSize;
    private double _contentTextSize;
    private string[]? _cachedDefaultFilterNames;
    private string[]? _cachedLayoutModeNames;
    private string[]? _cachedNewTaskPositionNames;
    private string[]? _cachedEnterBehaviorNames;
    private string[]? _cachedPreviewLineNames;
    private string[]? _cachedReminderOffsetNames;
    private int _enableGeneration;
    private bool _enablePending;
    private bool _disposed;

    public TodoSettingsViewModel(
        ITodoSettings settings,
        Func<string, string> localize,
        Func<string, object[], string> format,
        Action<Exception> reportError)
    {
        _settings = settings;
        _localize = localize;
        _format = format;
        _reportError = reportError;
        Refresh();
    }

    /// <summary>
    /// The serialized enable-switch chain's tail (batch 1): awaiting it
    /// waits out an in-flight enable/disable transition.
    /// </summary>
    public Task PendingChange { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Raised after a user edit committed a Todo text-size write with
    /// <c>scheduleSave:false</c>; the shell answers with the shared
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

            if (_isSyncingPresentation)
            {
                return;
            }

            _enablePending = true;
            PendingChange = ApplyEnabledAsync(value, ++_enableGeneration);
        }
    }

    /// <summary>
    /// The feature-card toggle's mirror path: flips the switch projection
    /// without re-entering the enable write chain — the card itself sends
    /// the SetFeatureWidgetEnabled command, and a plain Enabled assignment
    /// here would fire a second, racing one (batch R8).
    /// </summary>
    public void SetEnabledPresentationOnly(bool enabled)
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

    public bool RemindersEnabled
    {
        get => _remindersEnabled;
        set
        {
            if (!SetProperty(ref _remindersEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ReminderSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetRemindersEnabled(value));
        }
    }

    public int DefaultOffsetMinutes
    {
        get => _defaultOffsetMinutes;
        set
        {
            if (!SetProperty(ref _defaultOffsetMinutes, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ReminderSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetDefaultReminderOffset(value));
        }
    }

    public string LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (!SetProperty(ref _layoutMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(LayoutSummaryText));
            OnPropertyChanged(nameof(ShowWideOptions));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetLayoutMode(value));
        }
    }

    public bool AutoSelectFirstInWideLayout
    {
        get => _autoSelectFirstInWideLayout;
        set
        {
            if (!SetProperty(ref _autoSelectFirstInWideLayout, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetAutoSelectFirstInWideLayout(value));
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

            RunWrite(() => _settings.SetTabBarVisible(value));
        }
    }

    public string DefaultFilter
    {
        get => _defaultFilter;
        set
        {
            if (!SetProperty(ref _defaultFilter, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            // The coordinator reveals a hidden target tab and falls the
            // filter back when its tab gets hidden; the re-projection below
            // renders both linkages (batch 14).
            RunWrite(() => _settings.SetDefaultFilter(value));
        }
    }

    /// <summary>
    /// The persisted tab style. The XAML segment control binds the index
    /// projection (<see cref="TabStyleIndex"/>); this canonical string form
    /// is the batch-14 coordinator contract kept for callers and tests.
    /// </summary>
    public string TabStyle
    {
        get => _tabStyle;
        set
        {
            string normalized = QuickCaptureOptionKinds.NormalizeTabStyle(value);
            if (!SetProperty(ref _tabStyle, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(TabStyleIndex));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetTabStyle(normalized));
        }
    }

    /// <summary>
    /// The tab-group snapshot (default filter, tab bar, all seven tab
    /// visibility bits) as the coordinator normalized it.
    /// </summary>
    public TodoTabSettings Tabs => new(
        DefaultFilter,
        ShowTabBar,
        _showAllTab,
        _showActiveTab,
        _showTodayTab,
        _showThisWeekTab,
        _showThisMonthTab,
        _showImportantTab,
        _showCompletedTab);

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
            OnPropertyChanged(nameof(TabStyle));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetTabStyle(style));
        }
    }

    public bool ShowCompletedTasks
    {
        get => _showCompletedTasks;
        set
        {
            if (!SetProperty(ref _showCompletedTasks, value))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetShowCompletedTasks(value));
        }
    }

    public bool ShowFooterStats
    {
        get => _showFooterStats;
        set
        {
            if (!SetProperty(ref _showFooterStats, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FooterDisplaySummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetShowFooterStats(value));
        }
    }

    public bool ShowClearCompletedButton
    {
        get => _showClearCompletedButton;
        set
        {
            if (!SetProperty(ref _showClearCompletedButton, value))
            {
                return;
            }

            OnPropertyChanged(nameof(FooterDisplaySummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetShowClearCompletedButton(value));
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

            RunWrite(() => _settings.SetPreviewLineCount(value));
        }
    }

    public string NewTaskPosition
    {
        get => _newTaskPosition;
        set
        {
            if (!SetProperty(ref _newTaskPosition, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ContentSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetNewTaskPosition(value));
        }
    }

    public string EditorEnterBehavior
    {
        get => _editorEnterBehavior;
        set
        {
            if (!SetProperty(ref _editorEnterBehavior, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ContentSummaryText));
            if (_isSyncingPresentation)
            {
                return;
            }

            RunWrite(() => _settings.SetEditorEnterBehavior(value));
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
            if (_isSyncingPresentation)
            {
                return;
            }

            if (!double.IsFinite(value))
            {
                // Non-finite slider input re-projects from the effective
                // snapshot (stored zero inherits the global size).
                Refresh();
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
                Refresh();
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

    public bool ShowWideOptions =>
        LayoutMode != TodoOptionKinds.LayoutModeSinglePane;

    public string LayoutSummaryText => GetLayoutModeDisplayName(LayoutMode);

    public string TabsSummaryText => ShowTabBar
        ? VisibleTabsText
        : _localize("Settings.Toggle.Off");

    public string VisibleTabsText => string.Join(
        " · ",
        DefaultFilterValues.Where(IsTabSelected).Select(GetDefaultFilterDisplayName));

    public string ContentSummaryText => string.Join(
        " · ",
        GetPreviewLineDisplayName(ItemPreviewLineCount),
        GetNewTaskPositionDisplayName(NewTaskPosition),
        GetEnterBehaviorDisplayName(EditorEnterBehavior));

    public string ReminderSummaryText => RemindersEnabled
        ? GetReminderOffsetDisplayName(DefaultOffsetMinutes)
        : _localize("Settings.Toggle.Off");

    public string FooterDisplaySummaryText
    {
        get
        {
            var selected = new List<string>(2);
            if (ShowFooterStats)
            {
                selected.Add(GetFooterDisplayOptionName("Stats"));
            }

            if (ShowClearCompletedButton)
            {
                selected.Add(GetFooterDisplayOptionName("ClearCompleted"));
            }

            return selected.Count == 0
                ? _localize("Settings.Toggle.Off")
                : string.Join(" · ", selected);
        }
    }

    public IReadOnlyList<SettingsOption> VisibleDefaultFilterOptions
    {
        get
        {
            _cachedDefaultFilterNames ??= DefaultFilterValues
                .Select(GetDefaultFilterDisplayName)
                .ToArray();
            SettingsOption[] options = DefaultFilterValues
                .Select((value, index) => (value, index))
                .Where(item => IsTabSelected(item.value))
                .Select(item => new SettingsOption(
                    item.value,
                    _cachedDefaultFilterNames[item.index]))
                .ToArray();
            return WrapOptions(options);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableLayoutOptions
    {
        get
        {
            _cachedLayoutModeNames ??= LayoutModeValues
                .Select(GetLayoutModeDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(LayoutModeValues, _cachedLayoutModeNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableNewTaskPositionOptions
    {
        get
        {
            _cachedNewTaskPositionNames ??= NewTaskPositionValues
                .Select(GetNewTaskPositionDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(
                NewTaskPositionValues, _cachedNewTaskPositionNames));
        }
    }

    public IReadOnlyList<SettingsOption> AvailableEnterBehaviorOptions
    {
        get
        {
            _cachedEnterBehaviorNames ??= EnterBehaviorValues
                .Select(GetEnterBehaviorDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(
                EnterBehaviorValues, _cachedEnterBehaviorNames));
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

    public IReadOnlyList<SettingsOption> AvailableReminderOffsetOptions
    {
        get
        {
            _cachedReminderOffsetNames ??= TodoOptionKinds.ReminderOffsetSteps
                .Select(GetReminderOffsetDisplayName)
                .ToArray();
            return WrapOptions(BuildOptions(
                TodoOptionKinds.ReminderOffsetSteps, _cachedReminderOffsetNames));
        }
    }

    /// <summary>The tab filters offered by the visibility flyout.</summary>
    public string[] AvailableDefaultFilters => DefaultFilterValues;

    /// <summary>The options offered by the footer-display flyout.</summary>
    public string[] AvailableFooterDisplayOptions => FooterDisplayValues;

    public string GetDefaultFilterDisplayName(string filter) =>
        TodoOptionKinds.NormalizeDefaultFilter(filter) switch
        {
            TodoOptionKinds.DefaultFilterActive =>
                _localize("Settings.Todo.DefaultFilter.Active"),
            TodoOptionKinds.DefaultFilterToday =>
                _localize("Settings.Todo.DefaultFilter.Today"),
            TodoOptionKinds.DefaultFilterThisWeek =>
                _localize("Settings.Todo.DefaultFilter.ThisWeek"),
            TodoOptionKinds.DefaultFilterThisMonth =>
                _localize("Settings.Todo.DefaultFilter.ThisMonth"),
            TodoOptionKinds.DefaultFilterImportant =>
                _localize("Settings.Todo.DefaultFilter.Important"),
            TodoOptionKinds.DefaultFilterCompleted =>
                _localize("Settings.Todo.DefaultFilter.Completed"),
            _ => _localize("Settings.Todo.DefaultFilter.All")
        };

    public string GetLayoutModeDisplayName(string mode) =>
        TodoOptionKinds.NormalizeLayoutMode(mode) switch
        {
            TodoOptionKinds.LayoutModeSinglePane =>
                _localize("Settings.Todo.LayoutMode.SinglePane"),
            TodoOptionKinds.LayoutModeDualPane =>
                _localize("Settings.Todo.LayoutMode.DualPane"),
            _ => _localize("Settings.Todo.LayoutMode.Auto")
        };

    public string GetNewTaskPositionDisplayName(string position) =>
        TodoOptionKinds.NormalizeNewTaskPosition(position) ==
            TodoOptionKinds.NewTaskPositionBottom
            ? _localize("Settings.Todo.NewTaskPosition.Bottom")
            : _localize("Settings.Todo.NewTaskPosition.Top");

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

    public string GetReminderOffsetDisplayName(int minutes) =>
        TodoOptionKinds.NormalizeReminderOffsetMinutes(minutes) switch
        {
            0 => _localize("Settings.Todo.ReminderOffset.AtDueTime"),
            60 => _localize("Settings.Todo.ReminderOffset.OneHour"),
            1440 => _localize("Settings.Todo.ReminderOffset.OneDay"),
            var value => _format("Settings.Todo.ReminderOffset.Minutes", [value])
        };

    public string GetTabDisplayName(string filter) =>
        GetDefaultFilterDisplayName(filter);

    public string GetFooterDisplayOptionName(string option) => option switch
    {
        "Stats" => _localize("Settings.Todo.ShowFooterStats.Title"),
        "ClearCompleted" => _localize("Settings.Todo.ShowClearCompleted.Title"),
        _ => string.Empty
    };

    /// <summary>
    /// Writes one tab's visibility through the coordinator (the flyout's
    /// <see cref="ToggleTab"/> and external callers share this port); the
    /// write path's re-projection renders the batch-14 linkage.
    /// </summary>
    public void SetTabVisible(string? filter, bool visible)
    {
        SetTabVisibleCore(TodoOptionKinds.NormalizeDefaultFilter(filter), visible);
    }

    public bool IsTabSelected(string filter) =>
        TodoOptionKinds.NormalizeDefaultFilter(filter) switch
        {
            TodoOptionKinds.DefaultFilterActive => _showActiveTab,
            TodoOptionKinds.DefaultFilterToday => _showTodayTab,
            TodoOptionKinds.DefaultFilterThisWeek => _showThisWeekTab,
            TodoOptionKinds.DefaultFilterThisMonth => _showThisMonthTab,
            TodoOptionKinds.DefaultFilterImportant => _showImportantTab,
            TodoOptionKinds.DefaultFilterCompleted => _showCompletedTab,
            _ => _showAllTab
        };

    public bool CanToggleTab(string filter) =>
        !IsTabSelected(filter) || CountSelectedTabs() > 1;

    public void ToggleTab(string filter)
    {
        bool selected = IsTabSelected(filter);
        if (selected && !CanToggleTab(filter))
        {
            return;
        }

        SetTabVisibleCore(TodoOptionKinds.NormalizeDefaultFilter(filter), !selected);
    }

    private void SetTabVisibleCore(string filter, bool visible)
    {
        string normalized = TodoOptionKinds.NormalizeDefaultFilter(filter);
        bool changed = normalized switch
        {
            TodoOptionKinds.DefaultFilterActive => SetProperty(ref _showActiveTab, visible),
            TodoOptionKinds.DefaultFilterToday => SetProperty(ref _showTodayTab, visible),
            TodoOptionKinds.DefaultFilterThisWeek => SetProperty(ref _showThisWeekTab, visible),
            TodoOptionKinds.DefaultFilterThisMonth => SetProperty(ref _showThisMonthTab, visible),
            TodoOptionKinds.DefaultFilterImportant => SetProperty(ref _showImportantTab, visible),
            TodoOptionKinds.DefaultFilterCompleted => SetProperty(ref _showCompletedTab, visible),
            _ => SetProperty(ref _showAllTab, visible)
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

        // The coordinator protects the last visible tab and falls the
        // default filter back; the write path's re-projection renders the
        // linkage (batch 14).
        RunWrite(() => _settings.SetTabVisible(normalized, visible));
    }

    public bool IsFooterDisplayOptionSelected(string option) => option switch
    {
        "Stats" => ShowFooterStats,
        "ClearCompleted" => ShowClearCompletedButton,
        _ => false
    };

    public void ToggleFooterDisplayOption(string option)
    {
        switch (option)
        {
            case "Stats":
                ShowFooterStats = !ShowFooterStats;
                break;
            case "ClearCompleted":
                ShowClearCompletedButton = !ShowClearCompletedButton;
                break;
        }
    }

    /// <summary>
    /// The feature-card toggle's write path (the overview row in the
    /// feature-widgets section): mirrors the section switch's enable chain.
    /// </summary>
    public void SetFeatureEnabled(bool enabled)
    {
        Enabled = enabled;
    }

    /// <summary>
    /// Re-projects the persisted Todo state onto the binding surface
    /// without writing back. Called on construction, settings broadcasts,
    /// appearance text-size commits and default restores; the coordinator
    /// snapshots already normalize with the old shell-constructor
    /// semantics (incl. the default-filter linkage and the last-tab
    /// protection).
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        TodoReminderSettings reminder = _settings.Read();
        TodoLayoutSettings layout = _settings.ReadLayout();
        TodoTabSettings tabs = _settings.ReadTabs();
        TodoContentDisplaySettings contentDisplay = _settings.ReadContentDisplay();
        TodoInputSettings input = _settings.ReadInput();
        TodoDisplayOptions display = _settings.ReadDisplayOptions();
        _isSyncingPresentation = true;
        try
        {
            // An in-flight enable switch keeps the user's choice visible
            // until its serialized transition completes (batch 1).
            Enabled = _enablePending ? Enabled : reminder.Enabled;
            RemindersEnabled = reminder.RemindersEnabled;
            DefaultOffsetMinutes = reminder.DefaultOffsetMinutes;
            LayoutMode = layout.LayoutMode;
            AutoSelectFirstInWideLayout = layout.AutoSelectFirstInWideLayout;
            ShowTabBar = tabs.ShowTabBar;
            _showAllTab = tabs.ShowAllTab;
            _showActiveTab = tabs.ShowActiveTab;
            _showTodayTab = tabs.ShowTodayTab;
            _showThisWeekTab = tabs.ShowThisWeekTab;
            _showThisMonthTab = tabs.ShowThisMonthTab;
            _showImportantTab = tabs.ShowImportantTab;
            _showCompletedTab = tabs.ShowCompletedTab;
            DefaultFilter = tabs.DefaultFilter;
            TabStyleIndex = display.TabStyle ==
                    QuickCaptureOptionKinds.TabStylePivot
                ? 0
                : 1;
            ShowCompletedTasks = display.ShowCompletedTasks;
            ShowFooterStats = display.ShowFooterStats;
            ShowClearCompletedButton = display.ShowClearCompletedButton;
            ItemPreviewLineCount = contentDisplay.PreviewLineCount;
            NewTaskPosition = input.NewTaskPosition;
            EditorEnterBehavior = input.EditorEnterBehavior;
            ListTextSize = contentDisplay.ListTextSize;
            ContentTextSize = contentDisplay.ContentTextSize;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(TabStyleIndex));
        OnPropertyChanged(nameof(TabStyle));
        OnPropertyChanged(nameof(Tabs));
        OnPropertyChanged(nameof(ShowWideOptions));
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(TabsSummaryText));
        OnPropertyChanged(nameof(ReminderSummaryText));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(FooterDisplaySummaryText));
        RefreshTabDerivedProperties();
    }

    /// <summary>
    /// Drops the localized option-name caches after a language change so
    /// the option tables, summaries and the tab texts re-project in the
    /// new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedDefaultFilterNames = null;
        _cachedLayoutModeNames = null;
        _cachedNewTaskPositionNames = null;
        _cachedEnterBehaviorNames = null;
        _cachedPreviewLineNames = null;
        _cachedReminderOffsetNames = null;
        OnPropertyChanged(nameof(AvailableLayoutOptions));
        OnPropertyChanged(nameof(AvailableNewTaskPositionOptions));
        OnPropertyChanged(nameof(AvailableEnterBehaviorOptions));
        OnPropertyChanged(nameof(AvailablePreviewLineOptions));
        OnPropertyChanged(nameof(AvailableReminderOffsetOptions));
        OnPropertyChanged(nameof(VisibleDefaultFilterOptions));
        // Re-notify the current selections: replacing the localized option
        // arrays makes WinUI reset every bound ComboBox.SelectedIndex to -1.
        OnPropertyChanged(nameof(LayoutMode));
        OnPropertyChanged(nameof(DefaultFilter));
        OnPropertyChanged(nameof(ItemPreviewLineCount));
        OnPropertyChanged(nameof(NewTaskPosition));
        OnPropertyChanged(nameof(EditorEnterBehavior));
        OnPropertyChanged(nameof(DefaultOffsetMinutes));
        OnPropertyChanged(nameof(LayoutSummaryText));
        OnPropertyChanged(nameof(TabsSummaryText));
        OnPropertyChanged(nameof(ContentSummaryText));
        OnPropertyChanged(nameof(ReminderSummaryText));
        OnPropertyChanged(nameof(FooterDisplaySummaryText));
        OnPropertyChanged(nameof(ListTextSizeValueText));
        OnPropertyChanged(nameof(ContentTextSizeValueText));
        RefreshTabDerivedProperties();
    }

    public void ResetReminderPreferences(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetReminderPreferences(scheduleSave));
    }

    public void ResetLayoutPreferences(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetLayoutPreferences(scheduleSave));
    }

    public void ResetTabPreferences(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetTabPreferences(scheduleSave));
    }

    public void ResetPreviewLineCount(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetPreviewLineCount(scheduleSave));
    }

    public void ResetInputPreferences(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetInputPreferences(scheduleSave));
    }

    public void ResetDisplayOptions(bool scheduleSave = true)
    {
        RunWrite(() => _settings.ResetDisplayOptions(scheduleSave));
    }

    private void RefreshTabDerivedProperties()
    {
        OnPropertyChanged(nameof(VisibleTabsText));
        OnPropertyChanged(nameof(TabsSummaryText));
        OnPropertyChanged(nameof(VisibleDefaultFilterOptions));
        OnPropertyChanged(nameof(Tabs));
    }

    private int CountSelectedTabs() =>
        DefaultFilterValues.Count(IsTabSelected);

    // The coordinator's writes are synchronous, but its tab/default-filter
    // normalization (the batch-14 linkage) can differ from the editor's
    // optimistic projection: report failures, then always re-project from
    // the coordinator snapshot so the linked visuals follow.
    private void RunWrite(Action write)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            write();
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
        }

        if (!_isSyncingPresentation)
        {
            Refresh();
        }
    }

    private async Task ApplyEnabledAsync(bool enabled, int generation)
    {
        try
        {
            await _settings.SetEnabledAsync(enabled, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
        }
        finally
        {
            if (generation == _enableGeneration && !_disposed)
            {
                _enablePending = false;
                Refresh();
            }
        }
    }

    /// <summary>
    /// Writes a text-size override through the coordinator with its own
    /// normalization and re-projection (the old shell facade contract).
    /// </summary>
    public bool TrySetListTextSize(double size, bool scheduleSave = true)
    {
        if (_disposed || !double.IsFinite(size))
        {
            return false;
        }

        bool result = false;
        RunWrite(() =>
        {
            _settings.SetListTextSize(size, scheduleSave);
            result = true;
        });
        return result;
    }

    /// <summary>See <see cref="TrySetListTextSize"/>.</summary>
    public bool TrySetContentTextSize(double size, bool scheduleSave = true)
    {
        if (_disposed || !double.IsFinite(size))
        {
            return false;
        }

        bool result = false;
        RunWrite(() =>
        {
            _settings.SetContentTextSize(size, scheduleSave);
            result = true;
        });
        return result;
    }

    private bool TrySetTextSize(double normalized, bool list, out bool committed)
    {
        if (_disposed)
        {
            committed = false;
            return false;
        }

        try
        {
            if (list)
            {
                _settings.SetListTextSize(normalized, scheduleSave: false);
            }
            else
            {
                _settings.SetContentTextSize(normalized, scheduleSave: false);
            }

            committed = true;
            return true;
        }
        catch (Exception ex)
        {
            try { _reportError(ex); }
            catch { /* The re-projection below must still run. */ }
            Refresh();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
