using DeskBox.Controls;
using DeskBox.Features.Todo;
using DeskBox.Features.Search;
using DeskBox.Features.Backup;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views.SettingsSections;
using CommunityToolkit.WinUI.Controls;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow : Window
{
    private sealed record FeatureWidgetRowElements(
        SettingsCard Card,
        DeskBox.Controls.WidgetTitleIcon TitleIcon,
        TextBlock TitleText,
        TextBlock DescriptionText,
        Button? ResetButton,
        ToggleSwitch? Toggle,
        bool HasSettingsPage,
        bool HasReset,
        bool HasToggle);

    internal const int DefaultWindowWidth = 1000;
    internal const int DefaultWindowHeight = 760;
    internal const int MinWindowWidth = 500;
    internal const int MinWindowHeight = 400;
    private const int WindowWorkAreaMargin = 48;
    internal const double ContentMaxWidth = 1000;
    private const double RowStackContentThreshold = 620;
    internal const double NavigationMinimalThreshold = 640;
    internal const double NavigationCompactThreshold = 860;
    private const double MinimalContentTopClearance = 44;
    private const double DefaultCaptionInsetDip = 138;
    private const double UpdateCardBottomClearance = 196;
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SectionLayoutSettleDelay = TimeSpan.FromMilliseconds(90);
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr SettingsWindowSubclassId = new(1);

    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly SettingsService _settingsService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private readonly List<Grid> _settingRows = [];
    private readonly List<Grid> _metricRows = [];
    private readonly HashSet<Slider> _pressedAppearanceSliders = [];
    private readonly Dictionary<WidgetKind, FeatureWidgetRowElements> _featureWidgetRows = [];
    private readonly DispatcherTimer _resizeSettleTimer = new() { Interval = ResizeSettleDelay };
    private readonly DispatcherTimer _sectionLayoutSettleTimer = new() { Interval = SectionLayoutSettleDelay };
    private readonly PointerEventHandler _settingsRootPointerPressedHandler;
    private readonly PointerEventHandler _settingsRootPointerReleasedHandler;
    private bool _isSubclassInstalled;
    private bool _isClosed;
    private bool _allowRealClose;

    // Set only by the settings-host process (batch 3c-3b): user close means
    // real close there — the process exits with the window.
    internal bool _isHostProcessWindow;
    private bool _hasShownOnce;
    private NavigationViewPaneDisplayMode? _activeResponsivePaneMode;
    private SettingsWindowPlacement? _lastNormalPlacement;
    private bool _isMaximizedPlacement;
    private bool _restoreMaximizedOnFirstShow;
    private bool _isAppearanceSliderDragging;
    private bool _isRefreshingHotkeyControls;
    private bool _isRefreshingFeatureWidgetList;
    private bool _isSyncingNavigationSelection;
    private bool _isSelectingCity;
    private bool _isRefreshingBackupSnapshots;
    private string _currentSettingsSection = "General";
    private readonly Dictionary<string, FrameworkElement> _settingsSectionElements = new(StringComparer.Ordinal);
    private int _settingsNavigationGeneration;
    private IReadOnlyList<SettingsSearchResult> _settingsSearchResults = Array.Empty<SettingsSearchResult>();

    private static readonly IReadOnlyDictionary<string, SettingsSectionRoute> SectionRoutes =
        new Dictionary<string, SettingsSectionRoute>(StringComparer.Ordinal)
        {
            ["General"] = new("General", "Settings.Section.General", null, "General"),
            ["PerformanceSettings"] = new("PerformanceSettings", "Settings.Performance.Title", "General", "General"),
            ["Appearance"] = new("Appearance", "Settings.Section.Appearance", null, "Appearance"),
            ["CapsuleMode"] = new("CapsuleMode", "Settings.Section.CapsuleMode", null, "CapsuleMode"),
            ["WidgetGroups"] = new("WidgetGroups", "Settings.Section.WidgetGroups", "Appearance", "Appearance"),
            ["AppearanceDetail"] = new("AppearanceDetail", "Settings.Appearance.DetailTitle", null, "AppearanceDetail"),
            ["FeatureWidgets"] = new("FeatureWidgets", "Settings.Section.FeatureWidgets", null, "FeatureWidgets"),
            ["Interaction"] = new("Interaction", "Settings.Section.Interaction", null, "Interaction"),
            ["Displays"] = new("Displays", "Settings.Displays.Title", "General", "General"),
            ["Advanced"] = new("Advanced", "Settings.Section.Advanced", null, "Interaction"),
            ["Maintenance"] = new("Maintenance", "Settings.Section.Maintenance", null, "Maintenance"),
            ["About"] = new("About", "Settings.Nav.About", null, "About"),
            ["FileDisplaySettings"] = new("FileDisplaySettings", "Settings.Group.FileLayout.Title", "AppearanceDetail", "AppearanceDetail"),
            ["ManagedStorage"] = new("ManagedStorage", "Settings.ManagedStorage.PageTitle", "AppearanceDetail", "AppearanceDetail"),
            ["FileStackSettings"] = new("FileStackSettings", "Settings.FileStacks.PageTitle", "AppearanceDetail", "AppearanceDetail"),
            ["DesktopOrganizationSettings"] = new("DesktopOrganizationSettings", "DesktopOrganization.Settings.Title", "AppearanceDetail", "AppearanceDetail"),
            ["QuickCaptureSettings"] = new("QuickCaptureSettings", "Settings.QuickCapture.Title", "FeatureWidgets", "FeatureWidgets"),
            ["TodoSettings"] = new("TodoSettings", "Settings.Todo.Title", "FeatureWidgets", "FeatureWidgets"),
            ["MusicSettings"] = new("MusicSettings", "Settings.Music.Title", "FeatureWidgets", "FeatureWidgets"),
            ["WeatherSettings"] = new("WeatherSettings", "Settings.Weather.Title", "FeatureWidgets", "FeatureWidgets"),
            ["GlanceSettings"] = new("GlanceSettings", "Glance.Settings.Title", "FeatureWidgets", "FeatureWidgets"),
            ["CustomWidgetSettings"] = new("CustomWidgetSettings", "CustomWidget.Settings.Title", null, "FeatureWidgets"),
            ["AiSettings"] = new("AiSettings", "Settings.Nav.AiSettings", null, "AiSettings"),
            ["ClipboardSettings"] = new("ClipboardSettings", "Clipboard.Settings.Title", "FeatureWidgets", "FeatureWidgets"),
            ["SearchSettings"] = new("SearchSettings", "Settings.Search.Title", "FeatureWidgets", "FeatureWidgets"),
            ["AppearanceMaterialSettings"] = new("AppearanceMaterialSettings", "Settings.Material.Title", "Appearance", "Appearance"),
            ["CapsuleOverridesSettings"] = new("CapsuleOverridesSettings", "Settings.Capsule.Overrides.Title", "CapsuleMode", "CapsuleMode"),
            ["BackupRestoreSettings"] = new("BackupRestoreSettings", "Settings.DataBackup.Title", "Maintenance", "Maintenance"),
            ["CloudBackupSettings"] = new("CloudBackupSettings", "Settings.CloudBackup.Title", "Maintenance", "Maintenance"),
            ["DataHealthSettings"] = new("DataHealthSettings", "Settings.AttachmentHealth.Title", "Maintenance", "Maintenance"),
            ["CompatibilityDiagnosticsSettings"] = new("CompatibilityDiagnosticsSettings", "Settings.DragDropPermission.Title", "Maintenance", "Maintenance")
        };

    public SettingsViewModel ViewModel { get; }
    private readonly SearchSettingsViewModel _searchSettingsViewModel;
    private readonly BackupSettingsViewModel _backupSettingsViewModel;
    private readonly IBackupRestoreActions _backupRestoreActions;
    private readonly IBackupCommands _backupCommands;
    private readonly DeskBox.Features.Music.MusicSettingsViewModel _musicSettingsViewModel;
    private readonly DeskBox.Features.QuickCapture.QuickCaptureSettingsViewModel _quickCaptureSettingsViewModel;
    private readonly TodoSettingsViewModel _todoSettingsViewModel;
    private readonly DeskBox.Features.Weather.WeatherSettingsViewModel _weatherSettingsViewModel;
    private readonly DeskBox.Features.Interaction.InteractionSettingsViewModel _interactionSettingsViewModel;
    private readonly DeskBox.Features.FileStack.FileStackSettingsViewModel _fileStackSettingsViewModel;
    private readonly DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel _featureWidgetsSettingsViewModel;
    private readonly DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel _fileDisplaySettingsViewModel;
    private readonly DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel _managedStorageSettingsViewModel;
    private readonly DeskBox.Features.Appearance.AppearanceSettingsViewModel _appearanceSettingsViewModel;
    private readonly DeskBox.Features.Capsule.CapsuleSettingsViewModel _capsuleSettingsViewModel;
    private readonly DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel _groupNavigationSettingsViewModel;
    private readonly DeskBox.Features.Performance.PerformanceSettingsViewModel _performanceSettingsViewModel;
    private readonly ISettingsHotkeyActions _hotkeyActions;
    private readonly ISettingsWidgetActions _widgetActions;
    private readonly ISettingsRestoreActions _restoreActions;
    private readonly ISettingsAppActions _appActions;
    private readonly ISettingsDataActions _dataActions;

    /// <summary>
    /// The file-display section's editor. Exposed for the AOT managed-UI
    /// persistence smoke, which toggles the section's real binding surface;
    /// the section itself reaches the editor through its DataContext.
    /// </summary>
    public DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel FileDisplaySettings =>
        _fileDisplaySettingsViewModel;

    /// <summary>
    /// The appearance-section editor. Exposed for the AOT managed-UI
    /// persistence smoke, which drives the section family's real binding
    /// surface; the sections themselves reach the editor through their
    /// DataContext.
    /// </summary>
    public DeskBox.Features.Appearance.AppearanceSettingsViewModel AppearanceSettings =>
        _appearanceSettingsViewModel;

    /// <summary>
    /// The file-stack section's editor. Exposed for the AOT deep-settings
    /// smoke and the section's code-behind rule handlers; the section itself
    /// reaches the editor through its DataContext.
    /// </summary>
    public DeskBox.Features.FileStack.FileStackSettingsViewModel FileStackSettings =>
        _fileStackSettingsViewModel;

    public SettingsWindow(SettingsService settingsService, ThemeService themeService, LocalizationService localizationService,
        TodoSettingsViewModel todoSettings,
        DeskBox.Features.Weather.WeatherSettingsViewModel weatherSettings,
        SearchSettingsViewModel searchSettings,
        BackupSettingsViewModel backupSettings, IBackupRestoreActions backupRestoreActions,
            IQuickCaptureSettings quickCaptureSettings,
            DeskBox.Features.QuickCapture.QuickCaptureSettingsViewModel quickCaptureSettingsEditor,
            ISearchFeatureSettings searchFeatureSettings,
        IBackupCommands backupCommands,
        DeskBox.Features.Appearance.AppearanceSettingsViewModel appearanceSettings,
        DeskBox.Features.Capsule.CapsuleSettingsViewModel capsuleSettings,
        DeskBox.Features.Interaction.InteractionSettingsViewModel interactionSettings,
        DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel fileDisplaySettings,
        DeskBox.Features.FileStack.FileStackSettingsViewModel fileStackSettings,
        DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel groupNavigationSettings,
        DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel featureWidgetsSettings,
        DeskBox.Features.Music.MusicSettingsViewModel musicSettings,
        DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel managedStorageSettings,
        DeskBox.Features.Maintenance.MaintenanceSettingsViewModel maintenanceSettings,
        DeskBox.Features.Performance.PerformanceSettingsViewModel performanceSettings,
        ISettingsHotkeyActions hotkeyActions,
        ISettingsWidgetActions widgetActions,
        ISettingsRestoreActions restoreActions,
        ISettingsAppActions appActions,
        ISettingsDataActions dataActions,
        IAppUpdateService? appUpdateService)
    {
        var constructionStopwatch = Stopwatch.StartNew();
        long previousCheckpointMilliseconds = 0;
        void LogConstructionCheckpoint(string stage)
        {
            long elapsed = constructionStopwatch.ElapsedMilliseconds;
            App.Log(
                $"[SettingsPerf] Constructor stage={stage} " +
                $"stageMs={elapsed - previousCheckpointMilliseconds} totalMs={elapsed} " +
                $"createdSections={_settingsSectionElements.Count}");
            previousCheckpointMilliseconds = elapsed;
        }

        _settingsService = settingsService;
        _backupCommands = backupCommands;
        _searchSettingsViewModel = searchSettings;
        _backupSettingsViewModel = backupSettings;
        _backupRestoreActions = backupRestoreActions;
        _musicSettingsViewModel = musicSettings;
        _quickCaptureSettingsViewModel = quickCaptureSettingsEditor;
        _todoSettingsViewModel = todoSettings;
        _weatherSettingsViewModel = weatherSettings;
        _interactionSettingsViewModel = interactionSettings;
        _fileStackSettingsViewModel = fileStackSettings;
        _featureWidgetsSettingsViewModel = featureWidgetsSettings;
        _fileDisplaySettingsViewModel = fileDisplaySettings;
        _managedStorageSettingsViewModel = managedStorageSettings;
        _appearanceSettingsViewModel = appearanceSettings;
        _capsuleSettingsViewModel = capsuleSettings;
        _groupNavigationSettingsViewModel = groupNavigationSettings;
        _performanceSettingsViewModel = performanceSettings;
        _hotkeyActions = hotkeyActions;
        _widgetActions = widgetActions;
        _restoreActions = restoreActions;
        _appActions = appActions;
        _dataActions = dataActions;
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, todoSettings, weatherSettings,
            backupSettings, quickCaptureSettings, quickCaptureSettingsEditor, searchFeatureSettings, appearanceSettings,
            capsuleSettings, interactionSettings, fileDisplaySettings, fileStackSettings,
            groupNavigationSettings, featureWidgetsSettings, musicSettings,
            managedStorageSettings, maintenanceSettings, performanceSettings, hotkeyActions,
            widgetActions, appActions, dataActions, localizationService,
            appUpdateService);
        LogConstructionCheckpoint("view-model");
        _settingsRootPointerPressedHandler = SettingsRoot_PointerPressedHandled;
        _settingsRootPointerReleasedHandler = SettingsRoot_PointerReleasedHandled;
        InitializeComponent();
        LogConstructionCheckpoint("xaml");
        
        // ✅ Set localized title
        this.Title = _localizationService.T("Settings.WindowTitle");
        
        InitializeSettingsSectionElements();
        LogConstructionCheckpoint("section-registry");

        SettingsRoot.DataContext = ViewModel;
        // The General section stays on the shell (language and startup are
        // host-lifeline surfaces, batch 50 adjudication), but its two
        // cross-domain combos reach their section editors through
        // element-level DataContexts: the inline performance preset combo
        // and the attachment-storage combo resolve their {Binding} paths
        // against the performance / feature-widgets editors exactly like
        // the migrated sections do.
        PerformanceInlineExpander.DataContext = _performanceSettingsViewModel;
        PerformanceModeInlineComboBox.DataContext = _performanceSettingsViewModel;
        AttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel;
        Bindings.Initialize();
        SettingsRoot.AddHandler(
            UIElement.PointerPressedEvent,
            _settingsRootPointerPressedHandler,
            handledEventsToo: true);
        SettingsRoot.AddHandler(
            UIElement.PointerReleasedEvent,
            _settingsRootPointerReleasedHandler,
            handledEventsToo: true);
        CollectResponsiveRows(SettingsRoot);
        SettingsRoot.Loaded += SettingsRoot_Loaded;
        SettingsRoot.KeyDown += SettingsRoot_KeyDown;
        LogConstructionCheckpoint("bindings-and-responsive-tree");

        WindowsCompatibilityService.ApplySafeBackdrop(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
        InstallMinimumSizeHook();
        ApplyInitialWindowBounds(windowId);
        UpdateTitleBarInsets();
        LogConstructionCheckpoint("native-window");

        _themeService.TrackWindow(this);
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _hotkeyActions.RegistrationChanged += OnGlobalHotkeyRegistrationChanged;

        ApplyTitleBarButtonColors();
        ApplyLocalizedText();
        RefreshManagedStoragePathWarning();
        LogConstructionCheckpoint("localized-content");
        SettingsNavigationView.SelectedItem = GeneralNavItem;
        ShowSettingsSection("General");
        UpdateResponsiveLayout(GetWindowWidth());
        LogConstructionCheckpoint("initial-section");

        SizeChanged += SettingsWindow_SizeChanged;
        SaveFailedInfoBar.Closed += SaveFailedInfoBar_Closed;
        SettingsNavigationView.PaneOpening += SettingsNavigationView_PaneOpening;
        SettingsNavigationView.PaneClosing += SettingsNavigationView_PaneClosing;

        _resizeSettleTimer.Tick += ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Tick += SectionLayoutSettleTimer_Tick;
        Activated += SettingsWindow_Activated;
        _appWindow.Changed += SettingsWindow_AppWindowChanged;
        _appWindow.Closing += SettingsWindow_AppWindowClosing;
        // Batch R12 (window-state audit, third time this debt surfaced):
        // unplug/resolution changes can leave this window pressed outside
        // the remaining work area with no in-app path back. The main
        // process has a whole topology-archive system for its widgets;
        // this window only needs the cheap clamp.
        App.Current.DisplayTopologyChanged += SettingsWindow_DisplayTopologyChanged;
        // Subscribed only after _appWindow exists: ActualThemeChanged can
        // also fire on an OS theme flip while RequestedTheme is Default,
        // and the handler touches _appWindow.TitleBar.
        SettingsRoot.ActualThemeChanged += SettingsRoot_ActualThemeChanged;
        Closed += SettingsWindow_Closed;
        constructionStopwatch.Stop();
        LogConstructionCheckpoint("complete");
    }

    public void ShowWindow()
    {
        if (_isClosed)
        {
            return;
        }

        // A fresh reveal re-arms the floating update-activity card: its
        // dismissal is per window session (the PowerToys pattern).
        ViewModel.BeginUpdateActivitySession();
        // Reveal-time calibration: a missed ColorValuesChanged (or any other
        // break in the refresh chain) can never leave the window visibly
        // frozen on a stale theme.
        _themeService.ApplyToWindow(this);
        bool wasShownOnce = _hasShownOnce;
        _appWindow.Show();
        EnsureFirstShowPlacementApplied();
        UpdateTitleBarInsets();
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        // Route through the manager so a quick-reveal raised session (widget
        // group held topmost) lifts this window above the widgets instead of
        // leaving it in the normal band below them. Outside a session this is
        // the ordinary bring-to-front pulse, matching the other popups.
        _appActions.BringSettingsWindowToRaisedBand(
            _hWnd,
            "settings-shown");
        Activate();
        if (wasShownOnce)
        {
            RefreshOnReopen();
        }
    }

    private void EnsureFirstShowPlacementApplied()
    {
        if (_hasShownOnce)
        {
            return;
        }

        _hasShownOnce = true;
        if (_restoreMaximizedOnFirstShow &&
            _appWindow.Presenter is OverlappedPresenter presenter)
        {
            _restoreMaximizedOnFirstShow = false;
            presenter.Maximize();
        }
    }

    /// <summary>
    /// True only while the reused window is actually on screen. The instance
    /// stays alive while hidden, so "open" checks must not test for null.
    /// </summary>
    public bool IsVisibleToUser => !_isClosed && _appWindow.IsVisible;

    public void CloseForShutdown()
    {
        _allowRealClose = true;
        Close();
    }

    private void RefreshOnReopen()
    {
        if (_isClosed)
        {
            return;
        }

        RefreshFeatureWidgetList();
        RefreshVisibleSettingsPageData();
        UpdateTitleBarInsets();
        UpdateResponsiveLayout(GetWindowWidth());
        ApplyToggleSwitchContentVisibility();
    }

    // The file-name width/line cards migrated from the density section bind
    // through the appearance editor (FileNameWidthScale, FileNameLineCount and
    // their option lists live there), while the file-display section's
    // DataContext is the file-display editor. Re-target just this panel when
    // it loads, mirroring the element-level DataContext pattern the General
    // section uses for its cross-domain combos.
    private void FileNameLayoutPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isClosed || sender is not StackPanel panel)
        {
            return;
        }

        panel.DataContext = _appearanceSettingsViewModel;
    }

    private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
    {
        var stopwatch = Stopwatch.StartNew();
        EnsureFirstShowPlacementApplied();
        CollectResponsiveRows(SettingsRoot);
        long responsiveTreeMilliseconds = stopwatch.ElapsedMilliseconds;
        RefreshFeatureWidgetList();
        long featureWidgetMilliseconds = stopwatch.ElapsedMilliseconds;
        RefreshVisibleSettingsPageData();
        UpdateTitleBarInsets();
        UpdateResponsiveLayout(GetWindowWidth());
        ApplyToggleSwitchContentVisibility();
        stopwatch.Stop();
        App.Log(
            $"[SettingsPerf] Loaded responsiveTreeMs={responsiveTreeMilliseconds} " +
            $"featureWidgetsMs={featureWidgetMilliseconds - responsiveTreeMilliseconds} " +
            $"remainingMs={stopwatch.ElapsedMilliseconds - featureWidgetMilliseconds} " +
            $"totalMs={stopwatch.ElapsedMilliseconds}");
    }

    private void SettingsWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        UpdateTitleBarInsets();
        UpdateResponsiveLayout(args.Size.Width, preferMeasuredContentWidth: false);
        RestartResizeSettleTimer();
    }

    private void SettingsNavigationView_PaneOpening(NavigationView sender, object args)
    {
        UpdateResponsiveLayout(GetWindowWidth(), preferMeasuredContentWidth: false, explicitPaneOpenOverride: true);
        RestartResizeSettleTimer();
    }

    private void SettingsNavigationView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
    {
        UpdateResponsiveLayout(GetWindowWidth(), preferMeasuredContentWidth: false, explicitPaneOpenOverride: false);
        RestartResizeSettleTimer();
    }

    private void SettingsWindow_AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isClosed || !_hasShownOnce)
        {
            return;
        }

        if (args.DidPresenterChange || args.DidPositionChange || args.DidSizeChange)
        {
            CaptureLiveWindowPlacement();
        }
    }

    // The XAML tree of a closed window is retained by native references that
    // outlive the managed teardown in SettingsWindow_Closed, so every
    // open/close cycle used to leak a full settings tree. Cancelling at the
    // official AppWindow.Closing gate and hiding instead keeps exactly one
    // window alive for the process lifetime; real destruction only happens
    // via CloseForShutdown. Window.Closed therefore keeps its pure
    // real-close meaning, which also keeps ThemeService's
    // closed-means-untrack bookkeeping honest.
    //
    // Host mode (batch 3c-3b) bypasses both: this window lives in its own
    // process, so a user close really closes, the Closed teardown runs, and
    // the host process exits right after — no reuse means no leak and no
    // hide branch to run.
    /// <summary>
    /// Clamps the window back inside the nearest monitor's work area after
    /// a display-topology change (batch R12): the OS relocates windows off
    /// a vanished display without clamping, and this window's minimum size
    /// can exceed small-screen work areas entirely.
    /// </summary>
    private void SettingsWindow_DisplayTopologyChanged()
    {
        if (_isClosed || _hWnd == IntPtr.Zero)
        {
            return;
        }

        if (DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(SettingsWindow_DisplayTopologyChanged);
            return;
        }

        try
        {
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            {
                UpdateTitleBarInsets();
                return;
            }

            if (!Win32Helper.GetWindowRect(_hWnd, out Win32Helper.RECT rect))
            {
                return;
            }

            // A minimized window reports the (-32000,-32000) placeholder —
            // clamping that would teleport the restore position.
            if (rect.Left <= -30000 && rect.Top <= -30000)
            {
                return;
            }

            IntPtr monitor = Win32Helper.MonitorFromWindow(_hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new Win32Helper.MONITORINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32Helper.MONITORINFO>();
            if (!Win32Helper.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            Win32Helper.RECT work = info.rcWork;
            int x = rect.Left;
            int y = rect.Top;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            bool changed = false;
            if (width > work.Right - work.Left)
            {
                width = work.Right - work.Left;
                changed = true;
            }

            if (height > work.Bottom - work.Top)
            {
                height = work.Bottom - work.Top;
                changed = true;
            }

            if (x < work.Left)
            {
                x = work.Left;
                changed = true;
            }

            if (y < work.Top)
            {
                y = work.Top;
                changed = true;
            }

            if (x + width > work.Right)
            {
                x = work.Right - width;
                changed = true;
            }

            if (y + height > work.Bottom)
            {
                y = work.Bottom - height;
                changed = true;
            }

            if (changed)
            {
                Win32Helper.SetWindowPos(
                    _hWnd,
                    IntPtr.Zero,
                    x,
                    y,
                    width,
                    height,
                    0x0040 /* SWP_NOZORDER */ | 0x0004 /* SWP_NOACTIVATE */);
            }

            UpdateTitleBarInsets();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Topology clamp failed: {ex.Message}");
        }
    }

    private void SettingsWindow_AppWindowClosing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        PersistWindowPlacement();

        if (_allowRealClose || _isHostProcessWindow)
        {
            return;
        }

        args.Cancel = true;
        // Hiding the window does not close an open ContentDialog, and the
        // hotkey recorder's only hook teardown is its Closing event — force
        // it shut or its keyboard hook keeps swallowing input system-wide.
        global::DeskBox.Views.Dialogs.HotkeyRecorderDialog.ShutdownActiveRecorder();
        _appWindow.Hide();
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        _appActions.ReleaseRaisedBandGuest(
            _hWnd,
            "settings-hidden");
        _appActions.ScheduleSettingsHiddenCleanup();
    }

    private void SettingsRoot_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarButtonColors();

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        PersistWindowPlacement();
        _isClosed = true;
        // The app-scoped update service outlives this window; its download
        // event mirror must not keep the view model alive.
        ViewModel.DetachFromUpdateService();
        global::DeskBox.Views.Dialogs.HotkeyRecorderDialog.ShutdownActiveRecorder();
        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        UpdateSearchSettingsActivity();
        _searchSettingsViewModel.Dispose();
        _backupSettingsViewModel.Deactivate();
        Activated -= SettingsWindow_Activated;
        _appWindow.Changed -= SettingsWindow_AppWindowChanged;
        _appWindow.Closing -= SettingsWindow_AppWindowClosing;
        App.Current.DisplayTopologyChanged -= SettingsWindow_DisplayTopologyChanged;
        Closed -= SettingsWindow_Closed;
        SizeChanged -= SettingsWindow_SizeChanged;
        SaveFailedInfoBar.Closed -= SaveFailedInfoBar_Closed;
        SettingsNavigationView.PaneOpening -= SettingsNavigationView_PaneOpening;
        SettingsNavigationView.PaneClosing -= SettingsNavigationView_PaneClosing;
        SettingsRoot.Loaded -= SettingsRoot_Loaded;
        SettingsRoot.KeyDown -= SettingsRoot_KeyDown;
        SettingsRoot.ActualThemeChanged -= SettingsRoot_ActualThemeChanged;
        SettingsRoot.RemoveHandler(UIElement.PointerPressedEvent, _settingsRootPointerPressedHandler);
        SettingsRoot.RemoveHandler(UIElement.PointerReleasedEvent, _settingsRootPointerReleasedHandler);
        if (_cloudBackupCollectionChanged is not null)
            _backupSettingsViewModel.RemoteSnapshotItems.CollectionChanged -= _cloudBackupCollectionChanged;

        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Stop();
        _sectionLayoutSettleTimer.Tick -= SectionLayoutSettleTimer_Tick;
        ClearSettingsSearchHighlight();
        ClearFeatureSettingsExpanderCallbacks();
        foreach (FrameworkElement section in _settingsSectionElements.Values)
        {
            section.Loaded -= DeferredSettingsSection_Loaded;
            if (section is CustomWidgetSettingsSection customWidgetSection)
            {
                customWidgetSection.SubPageChanged -= OnCustomWidgetSubPageChanged;
                customWidgetSection.NavigateToAiSettingsRequested -= OnNavigateToAiSettingsRequested;
            }
            if (section is AiSettingsSection aiSettingsSection)
            {
                aiSettingsSection.NavigateToCustomWidgetRequested -= OnAiNavigateToCustomWidgetRequested;
            }
            if (section is DesktopOrganizationSettingsSection organizationSection)
            {
                organizationSection.SubPageChanged -= OnDesktopOrganizationSubPageChanged;
            }
            Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(section)?.Recycle();
            section.DataContext = null;
        }
        Win32Helper.ClearWindowTopMost(_hWnd);
        RemoveMinimumSizeHook();
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _hotkeyActions.RegistrationChanged -= OnGlobalHotkeyRegistrationChanged;

        if (AppearanceDetailSection is not null)
        {
            AppearanceDetailSection.FileStack = null;
            AppearanceDetailSection.FeatureWidgets = null;
            AppearanceDetailSection.Interaction = null;
        }
        if (CapsuleModeSection is not null)
        {
            CapsuleModeSection.ViewModel = null;
        }
        Bindings?.StopTracking();
        Localized.UntrackTree(SettingsRoot);
        SettingsRoot.DataContext = null;
        // The release-notes window is owned by App's single flight now; it
        // outlives this window on purpose and is closed by shutdown steps.
        ClearFeatureWidgetRows();
        ManagedStorageFolderList?.Children.Clear();
        ViewModel.Dispose();
        _settingsSearchResults = Array.Empty<SettingsSearchResult>();
        _settingsSectionElements.Clear();
        _settingRows.Clear();
        _metricRows.Clear();
        _pressedAppearanceSliders.Clear();
        SystemBackdrop = null;
        Content = null;
    }

    private void OnGlobalHotkeyRegistrationChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        });
    }

    private void OnAppearanceChanged()
    {
        // Title-bar button colors are driven by SettingsRoot_ActualThemeChanged
        // so they always follow the applied theme rather than event ordering.
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshFeatureWidgetList();
            return;
        }

        DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
    }

    private bool IsEffectiveSettingsThemeDark()
    {
        if (SettingsRoot is not null)
        {
            return SettingsRoot.ActualTheme switch
            {
                ElementTheme.Dark => true,
                ElementTheme.Light => false,
                _ => _themeService.CurrentTheme switch
                {
                    ElementTheme.Dark => true,
                    ElementTheme.Light => false,
                    _ => Win32Helper.IsSystemDarkMode()
                }
            };
        }

        return _themeService.CurrentTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
    }

    private void ApplyTitleBarButtonColors()
    {
        bool isDark = IsEffectiveSettingsThemeDark();

        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
    }

    private double GetWindowWidth()
    {
        return Content is FrameworkElement root && root.ActualWidth > 0
            ? root.ActualWidth
            : _appWindow.Size.Width / GetCurrentDpiScale();
    }

    private void UpdateTitleBarInsets()
    {
        if (_isClosed || TitleBarLeftInsetColumn is null || TitleBarRightInsetColumn is null)
        {
            return;
        }

        double scale = SettingsWindowPlacementStore.NormalizeDpiScale(GetCurrentDpiScale());
        int leftInsetPx = _appWindow.TitleBar.LeftInset;
        int rightInsetPx = _appWindow.TitleBar.RightInset;
        if (leftInsetPx <= 0 && rightInsetPx <= 0)
        {
            bool isRtl = SettingsRoot?.FlowDirection == FlowDirection.RightToLeft;
            TitleBarLeftInsetColumn.Width = new GridLength(isRtl ? DefaultCaptionInsetDip : 0);
            TitleBarRightInsetColumn.Width = new GridLength(isRtl ? 0 : DefaultCaptionInsetDip);
            return;
        }

        TitleBarLeftInsetColumn.Width = new GridLength(Math.Max(0, leftInsetPx / scale));
        TitleBarRightInsetColumn.Width = new GridLength(Math.Max(0, rightInsetPx / scale));
    }

    internal static NavigationViewPaneDisplayMode ResolveResponsivePaneDisplayMode(double width) =>
        width < NavigationMinimalThreshold
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : width < NavigationCompactThreshold
                ? NavigationViewPaneDisplayMode.LeftCompact
                : NavigationViewPaneDisplayMode.Left;

    internal static double ResolveResponsivePageSidePadding(double width) =>
        width < NavigationMinimalThreshold
            ? 20
            : width < NavigationCompactThreshold
                ? 28
                : 36;

    private void UpdateResponsiveLayout(
        double width,
        bool preferMeasuredContentWidth = true,
        bool? explicitPaneOpenOverride = null)
    {
        NavigationViewPaneDisplayMode targetPaneDisplayMode = ResolveResponsivePaneDisplayMode(width);

        // Stateful responsive transitions: only apply the tier's default
        // IsPaneOpen when crossing a width breakpoint so a manual hamburger
        // toggle is not undone by section switches or settle timers.
        if (_activeResponsivePaneMode != targetPaneDisplayMode)
        {
            _activeResponsivePaneMode = targetPaneDisplayMode;
            if (SettingsNavigationView.PaneDisplayMode != targetPaneDisplayMode)
            {
                SettingsNavigationView.PaneDisplayMode = targetPaneDisplayMode;
            }

            bool defaultPaneOpen = targetPaneDisplayMode == NavigationViewPaneDisplayMode.Left;
            if (SettingsNavigationView.IsPaneOpen != defaultPaneOpen)
            {
                SettingsNavigationView.IsPaneOpen = defaultPaneOpen;
            }
        }

        SettingsContentRoot.Margin = targetPaneDisplayMode == NavigationViewPaneDisplayMode.LeftMinimal
            ? new Thickness(0, MinimalContentTopClearance, 0, 0)
            : new Thickness(0);

        bool effectivePaneOpen = explicitPaneOpenOverride ?? SettingsNavigationView.IsPaneOpen;
        double expectedPaneWidth = targetPaneDisplayMode switch
        {
            NavigationViewPaneDisplayMode.LeftMinimal => 0,
            NavigationViewPaneDisplayMode.LeftCompact => SettingsNavigationView.CompactPaneLength,
            _ => effectivePaneOpen
                ? SettingsNavigationView.OpenPaneLength
                : SettingsNavigationView.CompactPaneLength
        };

        double calculatedContentSurfaceWidth = Math.Max(0, width - expectedPaneWidth);
        double contentSurfaceWidth = calculatedContentSurfaceWidth;
        if (preferMeasuredContentWidth && SettingsContentRoot.ActualWidth > 0)
        {
            contentSurfaceWidth = calculatedContentSurfaceWidth > 0
                ? Math.Min(SettingsContentRoot.ActualWidth, calculatedContentSurfaceWidth)
                : SettingsContentRoot.ActualWidth;
        }

        double pageSidePadding = ResolveResponsivePageSidePadding(width);
        double availableContentWidth = Math.Max(0, contentSurfaceWidth - pageSidePadding * 2);
        bool isNarrow = availableContentWidth < RowStackContentThreshold;
        double bottomPadding = ViewModel.UpdateActivityCardVisibility == Visibility.Visible
            ? UpdateCardBottomClearance
            : isNarrow ? 34 : 38;

        PageScroller.Padding = new Thickness(pageSidePadding, 16, pageSidePadding, bottomPadding);
        SettingsBreadcrumbHost.Padding = new Thickness(pageSidePadding, 16, pageSidePadding, 0);

        ContentHost.Width = Math.Min(ContentMaxWidth, availableContentWidth);
        ContentHost.MaxWidth = ContentMaxWidth;
        // The breadcrumb lives outside the scroller; mirror the content width
        // math so its leading edge stays aligned with the cards below.
        SettingsBreadcrumbBar.Width = Math.Min(ContentMaxWidth, availableContentWidth);
        SettingsBreadcrumbBar.MaxWidth = ContentMaxWidth;
        if (PathActionsPanel is not null)
        {
            PathActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            PathActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
            OpenPathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            PinQuickAccessButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            ChangePathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            CleanupStorageButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }
        if (AboutRightPanel is not null)
        {
            Grid.SetRow(AboutRightPanel, isNarrow ? 1 : 0);
            Grid.SetColumn(AboutRightPanel, isNarrow ? 0 : 2);
            Grid.SetColumnSpan(AboutRightPanel, isNarrow ? 3 : 1);
            AboutRightPanel.Margin = isNarrow ? new Thickness(0, 10, 0, 0) : new Thickness(0);
            AboutRightPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            AboutInfoActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            AboutInfoActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
            AboutMeButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            AboutWebsiteButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            StoreSupportButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            UpdateActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            UpdateActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
            UpdateCheckActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            UpdateCheckActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
            OneClickUpdateButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            ViewReleaseNotesButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            OpenManualUpdateDownloadButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }
        if (GlobalHotkeyActionsPanel is not null)
        {
            GlobalHotkeyActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            GlobalHotkeyCaptureButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            ResetGlobalHotkeyButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }

        foreach (var row in _settingRows)
        {
            ApplyResponsiveRowLayout(row, isNarrow);
        }

        foreach (var row in _metricRows)
        {
            ApplyResponsiveRowLayout(row, isNarrow);
        }
    }

    private void RestartResizeSettleTimer()
    {
        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Start();
    }

    private void RestartSectionLayoutSettleTimer()
    {
        _sectionLayoutSettleTimer.Stop();
        _sectionLayoutSettleTimer.Start();
    }

    private void SectionLayoutSettleTimer_Tick(object? sender, object e)
    {
        _sectionLayoutSettleTimer.Stop();

        CollectResponsiveRows(SettingsRoot);
        UpdateResponsiveLayout(GetWindowWidth());
    }

    private void ResizeSettleTimer_Tick(object? sender, object e)
    {
        _resizeSettleTimer.Stop();

        SettingsRoot.InvalidateMeasure();
        SettingsRoot.InvalidateArrange();
        SettingsRoot.UpdateLayout();
        UpdateResponsiveLayout(GetWindowWidth());
        SettingsRoot.UpdateLayout();
    }

    private void CollectResponsiveRows(DependencyObject root)
    {
        if (root == SettingsRoot)
        {
            _settingRows.Clear();
            _metricRows.Clear();
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Grid grid && grid.Tag is string tag)
            {
                if (string.Equals(tag, "SettingRow", StringComparison.Ordinal))
                {
                    _settingRows.Add(grid);
                }
                else if (string.Equals(tag, "MetricRow", StringComparison.Ordinal))
                {
                    _metricRows.Add(grid);
                }
            }

            CollectResponsiveRows(child);
        }
    }

    private static void ApplyResponsiveRowLayout(Grid row, bool isNarrow)
    {
        if (row.Children.Count < 2)
        {
            return;
        }

        var action = row.Children[1] as FrameworkElement;
        if (action is null)
        {
            return;
        }

        if (isNarrow)
        {
            if (row.RowDefinitions.Count < 2)
            {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            if (row.ColumnDefinitions.Count > 1)
            {
                row.ColumnDefinitions[1].Width = new GridLength(0);
            }

            row.ColumnSpacing = 0;
            Grid.SetRow(action, 1);
            Grid.SetColumn(action, 0);
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            if (row.ColumnDefinitions.Count > 1)
            {
                row.ColumnDefinitions[1].Width = GridLength.Auto;
            }

            row.ColumnSpacing = row.Tag is string tag && string.Equals(tag, "MetricRow", StringComparison.Ordinal)
                ? 16
                : 24;
            Grid.SetRow(action, 0);
            Grid.SetColumn(action, 1);
            action.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void ApplyInitialWindowBounds(Microsoft.UI.WindowId windowId)
    {
        SettingsWindowPlacement? savedPlacement =
            SettingsWindowPlacementStore.Shared.TryLoad(out SettingsWindowPlacement loaded)
                ? loaded
                : null;

        Windows.Graphics.RectInt32 workAreaRect = savedPlacement is { } saved
            ? DisplayArea.GetFromRect(
                new Windows.Graphics.RectInt32(
                    saved.X,
                    saved.Y,
                    Math.Max(100, saved.WidthDip),
                    Math.Max(100, saved.HeightDip)),
                DisplayAreaFallback.Nearest).WorkArea
            : DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;

        var workArea = new SettingsWindowWorkArea(
            workAreaRect.X,
            workAreaRect.Y,
            workAreaRect.Width,
            workAreaRect.Height);

        double initialScale = GetCurrentDpiScale();
        SettingsWindowResolvedBounds resolved = SettingsWindowPlacementStore.ResolveInitialBounds(
            savedPlacement,
            workArea,
            initialScale,
            DefaultWindowWidth,
            DefaultWindowHeight,
            MinWindowWidth,
            MinWindowHeight,
            WindowWorkAreaMargin);

        _appWindow.Move(new Windows.Graphics.PointInt32(resolved.X, resolved.Y));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(resolved.WidthPx, resolved.HeightPx));

        // If the saved placement landed on a secondary monitor with a different
        // DPI scale, re-evaluate once now that _hWnd lives on that monitor.
        double targetMonitorScale = GetCurrentDpiScale();
        if (Math.Abs(targetMonitorScale - initialScale) > 0.01)
        {
            resolved = SettingsWindowPlacementStore.ResolveInitialBounds(
                savedPlacement,
                workArea,
                targetMonitorScale,
                DefaultWindowWidth,
                DefaultWindowHeight,
                MinWindowWidth,
                MinWindowHeight,
                WindowWorkAreaMargin);
            _appWindow.Move(new Windows.Graphics.PointInt32(resolved.X, resolved.Y));
            _appWindow.Resize(new Windows.Graphics.SizeInt32(resolved.WidthPx, resolved.HeightPx));
        }

        double effectiveScale = SettingsWindowPlacementStore.NormalizeDpiScale(targetMonitorScale);
        _lastNormalPlacement = new SettingsWindowPlacement(
            resolved.X,
            resolved.Y,
            Math.Max(MinWindowWidth, (int)Math.Round(resolved.WidthPx / effectiveScale, MidpointRounding.AwayFromZero)),
            Math.Max(MinWindowHeight, (int)Math.Round(resolved.HeightPx / effectiveScale, MidpointRounding.AwayFromZero)),
            false);
        _isMaximizedPlacement = resolved.IsMaximized;
        _restoreMaximizedOnFirstShow = resolved.IsMaximized;
    }

    private void CaptureLiveWindowPlacement()
    {
        if (_isClosed)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _isMaximizedPlacement = true;
                return;
            }
        }

        Windows.Graphics.PointInt32 pos = _appWindow.Position;
        Windows.Graphics.SizeInt32 size = _appWindow.Size;
        if (pos.X <= -30000 || pos.Y <= -30000 || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        double scale = SettingsWindowPlacementStore.NormalizeDpiScale(GetCurrentDpiScale());
        int widthDip = Math.Max(MinWindowWidth, (int)Math.Round(size.Width / scale, MidpointRounding.AwayFromZero));
        int heightDip = Math.Max(MinWindowHeight, (int)Math.Round(size.Height / scale, MidpointRounding.AwayFromZero));
        _isMaximizedPlacement = false;
        _lastNormalPlacement = new SettingsWindowPlacement(pos.X, pos.Y, widthDip, heightDip, false);
    }

    private void PersistWindowPlacement()
    {
        if (!_hasShownOnce)
        {
            return;
        }

        CaptureLiveWindowPlacement();
        if (_lastNormalPlacement is { WidthDip: > 0, HeightDip: > 0 } normal)
        {
            SettingsWindowPlacementStore.Shared.Save(normal with { IsMaximized = _isMaximizedPlacement });
        }
    }

    private double GetCurrentDpiScale()
    {
        return Win32Helper.GetDpiScaleForWindow(_hWnd, SettingsRoot.XamlRoot);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale) =>
        SettingsWindowPlacementStore.ToPhysicalPixels(logicalPixels, scale);

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = GetCurrentDpiScale();
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, ToPhysicalPixels(MinWindowWidth, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, ToPhysicalPixels(MinWindowHeight, scale));
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        if (_isHostProcessWindow &&
            (message == WmQueryEndSession || message == WmEndSession))
        {
            // Session end never raises Window.Closed — without this the
            // host's debounced (1s) pending save was silently lost on
            // "edit setting → sign out". ExitSettingsHost flushes it while
            // the pipe to the main process is still alive (batch R6).
            // QUERYENDSESSION must answer TRUE — returning zero would
            // REFUSE the sign-off system-wide. Flushing already on QUERY
            // (not only ENDSESSION) covers a death mid-broadcast; a second
            // call from ENDSESSION is an idempotent no-op.
            PersistWindowPlacement();
            App.Current.ExitSettingsHost();
            return new IntPtr(1);
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private sealed record SettingsSectionRoute(
        string Tag,
        string TitleKey,
        string? ParentTag,
        string NavTag);

    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial record SettingsBreadcrumbItem(string SectionTag, string Title, double Opacity)
    {
        public bool IsCurrent => Opacity >= 0.99;

        public bool IsAncestor => Opacity < 0.99;

        public override string ToString()
        {
            return Title;
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial record SettingsSearchResult(
        string SectionTag,
        string Title,
        string Breadcrumb,
        string Description,
        string? TargetSectionTag,
        string? TargetHeaderKey)
    {
        public bool IsPage => TargetHeaderKey is null;

        // Real matches always carry at least one breadcrumb segment; the
        // "no results" sentinel passes an empty one and collapses the line.
        public bool HasBreadcrumb => Breadcrumb.Length > 0;

        public override string ToString()
        {
            return Title;
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial record BackupSnapshotListItem(
        string Path,
        string Title,
        string Details,
        bool CanRestore);
}
