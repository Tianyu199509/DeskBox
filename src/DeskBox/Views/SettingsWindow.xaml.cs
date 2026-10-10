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

    private const int DefaultWindowWidth = 920;
    private const int DefaultWindowHeight = 760;
    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;
    private const int WindowWorkAreaMargin = 48;
    private const double ContentMaxWidth = 760;
    private const double PageSidePadding = 20;
    private const double RowStackContentThreshold = 620;
    private const double NarrowTitleThreshold = 560;
    private const double NavigationCompactThreshold = 760;
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SectionLayoutSettleDelay = TimeSpan.FromMilliseconds(90);
    private const uint WmGetMinMaxInfo = 0x0024;
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
    private bool _hasShownOnce;
    private bool _isAppearanceSliderDragging;
    private bool _isRefreshingHotkeyControls;
    private bool _isRefreshingFeatureWidgetList;
    private bool _isSyncingNavigationSelection;
    private bool _isSelectingCity;
    private bool _isRefreshingBackupSnapshots;
    private ReleaseNotesWindow? _releaseNotesWindow;
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
            ["SearchSettings"] = new("SearchSettings", "Settings.Search.Title", "FeatureWidgets", "FeatureWidgets"),
            ["AppearanceMaterialSettings"] = new("AppearanceMaterialSettings", "Settings.Material.Title", "Appearance", "Appearance"),
            ["AppearanceDensitySettings"] = new("AppearanceDensitySettings", "Settings.Density.Title", "Appearance", "Appearance"),
            ["AppearanceWindowSettings"] = new("AppearanceWindowSettings", "Settings.Group.AppVisual.Title", "Appearance", "Appearance"),
            ["AppearanceAnimationSettings"] = new("AppearanceAnimationSettings", "Settings.Group.Animation.Title", "Appearance", "Appearance"),
            ["CapsuleOverridesSettings"] = new("CapsuleOverridesSettings", "Settings.Capsule.Overrides.Title", "CapsuleMode", "CapsuleMode"),
            ["BackupRestoreSettings"] = new("BackupRestoreSettings", "Settings.DataBackup.Title", "Maintenance", "Maintenance"),
            ["CloudBackupSettings"] = new("CloudBackupSettings", "Settings.CloudBackup.Title", "Maintenance", "Maintenance"),
            ["DataHealthSettings"] = new("DataHealthSettings", "Settings.AttachmentHealth.Title", "Maintenance", "Maintenance"),
            ["CompatibilityDiagnosticsSettings"] = new("CompatibilityDiagnosticsSettings", "Settings.DragDropPermission.Title", "Maintenance", "Maintenance")
        };

    public SettingsViewModel ViewModel { get; }
    private readonly SearchSettingsViewModel _searchSettingsViewModel;
    private readonly BackupSettingsViewModel _backupSettingsViewModel;
    private readonly BackupRestoreActions _backupRestoreActions;
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
        BackupSettingsViewModel backupSettings, BackupRestoreActions backupRestoreActions,
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
        DeskBox.Features.Performance.PerformanceSettingsViewModel performanceSettings)
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
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, todoSettings, weatherSettings,
            backupSettings, quickCaptureSettings, quickCaptureSettingsEditor, searchFeatureSettings, appearanceSettings,
            capsuleSettings, interactionSettings, fileDisplaySettings, fileStackSettings,
            groupNavigationSettings, featureWidgetsSettings, musicSettings,
            managedStorageSettings, maintenanceSettings, performanceSettings, localizationService,
            App.Current.AppUpdateService);
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
        LogConstructionCheckpoint("bindings-and-responsive-tree");

        WindowsCompatibilityService.ApplySafeBackdrop(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = WindowSubclassProc;
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        InstallMinimumSizeHook();
        ApplyInitialWindowBounds(windowId);
        LogConstructionCheckpoint("native-window");

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        _themeService.TrackWindow(this);
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (App.Current.GlobalHotkeyService is { } hotkeyService)
        {
            hotkeyService.RegistrationChanged += OnGlobalHotkeyRegistrationChanged;
        }

        ApplyTitleBarButtonColors();
        ApplyLocalizedText();
        RefreshManagedStoragePathWarning();
        LogConstructionCheckpoint("localized-content");
        SettingsNavigationView.SelectedItem = GeneralNavItem;
        ShowSettingsSection("General");
        UpdateResponsiveLayout(GetWindowWidth());
        LogConstructionCheckpoint("initial-section");

        SizeChanged += SettingsWindow_SizeChanged;

        _resizeSettleTimer.Tick += ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Tick += SectionLayoutSettleTimer_Tick;
        Activated += SettingsWindow_Activated;
        _appWindow.Closing += SettingsWindow_AppWindowClosing;
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

        // Reveal-time calibration: a missed ColorValuesChanged (or any other
        // break in the refresh chain) can never leave the window visibly
        // frozen on a stale theme.
        _themeService.ApplyToWindow(this);
        _appWindow.Show();
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        // Route through the manager so a quick-reveal raised session (widget
        // group held topmost) lifts this window above the widgets instead of
        // leaving it in the normal band below them. Outside a session this is
        // the ordinary bring-to-front pulse, matching the other popups.
        App.Current.WidgetManager?.BringAuxiliaryWindowToFront(
            _hWnd,
            "settings-shown");
        Activate();
        if (_hasShownOnce)
        {
            RefreshOnReopen();
        }
        else
        {
            _hasShownOnce = true;
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
        CollectResponsiveRows(SettingsRoot);
        long responsiveTreeMilliseconds = stopwatch.ElapsedMilliseconds;
        RefreshFeatureWidgetList();
        long featureWidgetMilliseconds = stopwatch.ElapsedMilliseconds;
        RefreshVisibleSettingsPageData();
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
        UpdateResponsiveLayout(args.Size.Width, preferMeasuredContentWidth: false);
        RestartResizeSettleTimer();
    }

    // The XAML tree of a closed window is retained by native references that
    // outlive the managed teardown in SettingsWindow_Closed, so every
    // open/close cycle used to leak a full settings tree. Cancelling at the
    // official AppWindow.Closing gate and hiding instead keeps exactly one
    // window alive for the process lifetime; real destruction only happens
    // via CloseForShutdown. Window.Closed therefore keeps its pure
    // real-close meaning, which also keeps ThemeService's
    // closed-means-untrack bookkeeping honest.
    private void SettingsWindow_AppWindowClosing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_allowRealClose)
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
        App.Current.WidgetManager?.ReleaseRaisedBandGuest(
            _hWnd,
            "settings-hidden");
        App.ScheduleBackgroundMemoryCleanup("settings-hidden");
    }

    private void SettingsRoot_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarButtonColors();

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        global::DeskBox.Views.Dialogs.HotkeyRecorderDialog.ShutdownActiveRecorder();
        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        UpdateSearchSettingsActivity();
        _searchSettingsViewModel.Dispose();
        _backupSettingsViewModel.Deactivate();
        Activated -= SettingsWindow_Activated;
        _appWindow.Closing -= SettingsWindow_AppWindowClosing;
        Closed -= SettingsWindow_Closed;
        SizeChanged -= SettingsWindow_SizeChanged;
        SettingsRoot.Loaded -= SettingsRoot_Loaded;
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
            Microsoft.UI.Xaml.Markup.XamlBindingHelper.GetDataTemplateComponent(section)?.Recycle();
            section.DataContext = null;
        }
        Win32Helper.ClearWindowTopMost(_hWnd);
        RemoveMinimumSizeHook();
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (App.Current.GlobalHotkeyService is { } hotkeyService)
        {
            hotkeyService.RegistrationChanged -= OnGlobalHotkeyRegistrationChanged;
        }

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
        Bindings.StopTracking();
        Localized.UntrackTree(SettingsRoot);
        SettingsRoot.DataContext = null;
        _releaseNotesWindow?.Close();
        _releaseNotesWindow = null;
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

    private void UpdateResponsiveLayout(double width, bool preferMeasuredContentWidth = true)
    {
        bool useCompactNavigation = width < NavigationCompactThreshold;

        var targetPaneDisplayMode = useCompactNavigation
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        if (SettingsNavigationView.PaneDisplayMode != targetPaneDisplayMode)
        {
            SettingsNavigationView.PaneDisplayMode = targetPaneDisplayMode;
        }

        bool targetPaneOpen = !useCompactNavigation;
        if (SettingsNavigationView.IsPaneOpen != targetPaneOpen)
        {
            SettingsNavigationView.IsPaneOpen = targetPaneOpen;
        }

        double expectedPaneWidth = useCompactNavigation
            ? SettingsNavigationView.CompactPaneLength
            : SettingsNavigationView.OpenPaneLength;
        double calculatedContentSurfaceWidth = Math.Max(0, width - expectedPaneWidth);
        double contentSurfaceWidth = calculatedContentSurfaceWidth;
        if (preferMeasuredContentWidth && SettingsContentRoot.ActualWidth > 0)
        {
            contentSurfaceWidth = calculatedContentSurfaceWidth > 0
                ? Math.Min(SettingsContentRoot.ActualWidth, calculatedContentSurfaceWidth)
                : SettingsContentRoot.ActualWidth;
        }

        double availableContentWidth = Math.Max(0, contentSurfaceWidth - PageSidePadding * 2);
        bool isNarrow = availableContentWidth < RowStackContentThreshold;

        PageScroller.Padding = isNarrow
            ? new Thickness(PageSidePadding, 16, PageSidePadding, 34)
            : new Thickness(PageSidePadding, 16, PageSidePadding, 38);
        SettingsBreadcrumbHost.Padding = new Thickness(PageSidePadding, 16, PageSidePadding, 0);

        // The search box has no explicit Height (a fixed 32px height corrupted
        // the template's re-measure on width changes: corners flattened and
        // the query icon escaped the box). Width stays responsive so the box
        // keeps its intended size instead of collapsing to MinWidth.
        SettingsSearchBox.Width = Math.Min(
            520,
            Math.Max(260, width * 0.5));

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

        TitleTextHost.Visibility = width < NarrowTitleThreshold ? Visibility.Collapsed : Visibility.Visible;
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
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetCurrentDpiScale();
        int desiredWidth = ToPhysicalPixels(DefaultWindowWidth, scale);
        int desiredHeight = ToPhysicalPixels(DefaultWindowHeight, scale);
        int minWidth = ToPhysicalPixels(MinWindowWidth, scale);
        int minHeight = ToPhysicalPixels(MinWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);

        int width = Math.Clamp(
            desiredWidth,
            minWidth,
            Math.Max(minWidth, workArea.Width - workAreaMargin));
        int height = Math.Clamp(
            desiredHeight,
            minHeight,
            Math.Max(minHeight, workArea.Height - workAreaMargin));

        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private double GetCurrentDpiScale()
    {
        return Win32Helper.GetDpiScaleForWindow(_hWnd, SettingsRoot.XamlRoot);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(logicalPixels * normalizedScale, MidpointRounding.AwayFromZero));
    }

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
