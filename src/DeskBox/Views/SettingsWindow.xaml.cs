using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
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
        Border Container,
        WidgetTitleIcon Icon,
        TextBlock Title,
        TextBlock Description,
        Button? SettingsButton,
        Button? ResetButton,
        ToggleSwitch? Toggle,
        FontIcon? Arrow,
        bool HasSettingsPage,
        bool HasReset,
        bool HasToggle);

    private const int DefaultWindowWidth = 920;
    private const int DefaultWindowHeight = 760;
    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;
    private const int WindowWorkAreaMargin = 48;
    private const double ContentMaxWidth = 760;
    private const double SettingsSearchMinWidth = 260;
    private const double SettingsSearchMaxWidth = 520;
    private const double SettingsSearchWidthRatio = 0.5;
    private const double PageSidePadding = 20;
    private const double RowStackContentThreshold = 620;
    private const double NarrowTitleThreshold = 560;
    private const double NavigationCompactThreshold = 760;
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SectionLayoutSettleDelay = TimeSpan.FromMilliseconds(90);
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmReservedHotkeyCapture = 0x8443;
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
    private readonly ReservedHotkeyHookService _hotkeyRecordingHook = new();
    private bool _isSubclassInstalled;
    private bool _isClosed;
    private bool _isAppearanceSliderDragging;
    private bool _isRecordingHotkey;
    private bool _isRefreshingHotkeyControls;
    private bool _isRefreshingFeatureWidgetList;
    private bool _isSyncingNavigationSelection;
    private bool _isSettingsRootLoaded;
    private bool _isSelectingCity;
    private bool _isRefreshingBackupSnapshots;
    private ReleaseNotesWindow? _releaseNotesWindow;
    private string _currentSettingsSection = "General";
    private IReadOnlyDictionary<string, FrameworkElement> _settingsSectionElements = null!;
    private IReadOnlyList<SettingsSearchResult> _settingsSearchResults = Array.Empty<SettingsSearchResult>();

    private static readonly IReadOnlyDictionary<string, SettingsSectionRoute> SectionRoutes =
        new Dictionary<string, SettingsSectionRoute>(StringComparer.Ordinal)
        {
            ["General"] = new("General", "Settings.Section.General", null, "General"),
            ["PerformanceSettings"] = new("PerformanceSettings", "Settings.Performance.Title", "General", "General"),
            ["Appearance"] = new("Appearance", "Settings.Section.Appearance", null, "Appearance"),
            ["CapsuleMode"] = new("CapsuleMode", "Settings.Section.CapsuleMode", "Appearance", "Appearance"),
            ["WidgetGroups"] = new("WidgetGroups", "Settings.Section.WidgetGroups", "Appearance", "Appearance"),
            ["AppearanceDetail"] = new("AppearanceDetail", "Settings.Appearance.DetailTitle", null, "AppearanceDetail"),
            ["FeatureWidgets"] = new("FeatureWidgets", "Settings.Section.FeatureWidgets", null, "FeatureWidgets"),
            ["Interaction"] = new("Interaction", "Settings.Section.Interaction", null, "Interaction"),
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
            ["CapsuleBehaviorSettings"] = new("CapsuleBehaviorSettings", "Settings.Capsule.HoverResponse.Title", "CapsuleMode", "Appearance"),
            ["CapsuleArrangementSettings"] = new("CapsuleArrangementSettings", "Settings.Capsule.ArrangementDetails.Title", "CapsuleMode", "Appearance"),
            ["CapsuleAnimationSettings"] = new("CapsuleAnimationSettings", "Settings.Capsule.Animation.Title", "CapsuleMode", "Appearance"),
            ["CapsuleOverridesSettings"] = new("CapsuleOverridesSettings", "Settings.Capsule.Overrides.Title", "CapsuleMode", "Appearance"),
            ["BackupRestoreSettings"] = new("BackupRestoreSettings", "Settings.DataBackup.Title", "Maintenance", "Maintenance"),
            ["DataHealthSettings"] = new("DataHealthSettings", "Settings.AttachmentHealth.Title", "Maintenance", "Maintenance"),
            ["CompatibilityDiagnosticsSettings"] = new("CompatibilityDiagnosticsSettings", "Settings.DragDropPermission.Title", "Maintenance", "Maintenance")
        };

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsService settingsService, ThemeService themeService, LocalizationService localizationService)
    {
        var constructionStopwatch = Stopwatch.StartNew();
        long previousCheckpointMilliseconds = 0;
        void LogConstructionCheckpoint(string stage)
        {
            long elapsed = constructionStopwatch.ElapsedMilliseconds;
            App.Log(
                $"[SettingsPerf] Constructor stage={stage} " +
                $"stageMs={elapsed - previousCheckpointMilliseconds} totalMs={elapsed}");
            previousCheckpointMilliseconds = elapsed;
        }

        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, localizationService, App.Current.AppUpdateService);
        LogConstructionCheckpoint("view-model");
        _settingsRootPointerPressedHandler = SettingsRoot_PointerPressedHandled;
        _settingsRootPointerReleasedHandler = SettingsRoot_PointerReleasedHandled;
        InitializeComponent();
        LogConstructionCheckpoint("xaml");
        
        // ✅ Set localized title
        this.Title = _localizationService.T("Window.Settings.Title");
        
        InitializeSettingsSectionElements();
        RefreshSettingsSearchResults();
        LogConstructionCheckpoint("section-registry");

        SettingsRoot.DataContext = ViewModel;
        AppearanceDetailSection.ViewModel = ViewModel;
        CapsuleModeSection.ViewModel = ViewModel;
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
        GlanceSettingsSection.SetOwnerWindow(_hWnd);
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

        _appWindow.Show();
        Activate();
    }

    public void CloseForShutdown()
    {
        Close();
    }

    private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
    {
        var stopwatch = Stopwatch.StartNew();
        CollectResponsiveRows(SettingsRoot);
        long responsiveTreeMilliseconds = stopwatch.ElapsedMilliseconds;
        RefreshFeatureWidgetList();
        long featureWidgetMilliseconds = stopwatch.ElapsedMilliseconds;
        _ = ViewModel.RefreshQuickAccessStateAsync();
        ViewModel.RefreshGlobalHotkeyState();
        _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        RefreshGlobalHotkeyControls();
        UpdateResponsiveLayout(GetWindowWidth());
        ApplyToggleSwitchContentVisibility();
        _isSettingsRootLoaded = true;
        RefreshSettingsSearchResults();
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

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        Closed -= SettingsWindow_Closed;
        SizeChanged -= SettingsWindow_SizeChanged;
        SettingsRoot.Loaded -= SettingsRoot_Loaded;
        SettingsRoot.RemoveHandler(UIElement.PointerPressedEvent, _settingsRootPointerPressedHandler);
        SettingsRoot.RemoveHandler(UIElement.PointerReleasedEvent, _settingsRootPointerReleasedHandler);

        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Stop();
        _sectionLayoutSettleTimer.Tick -= SectionLayoutSettleTimer_Tick;
        _isRecordingHotkey = false;
        _hotkeyRecordingHook.Dispose();
        ClearSettingsSearchHighlight();
        Win32Helper.ClearWindowTopMost(_hWnd);
        RemoveMinimumSizeHook();
        _themeService.AppearanceChanged -= OnAppearanceChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (App.Current.GlobalHotkeyService is { } hotkeyService)
        {
            hotkeyService.RegistrationChanged -= OnGlobalHotkeyRegistrationChanged;
        }

        AppearanceDetailSection.ViewModel = null;
        CapsuleModeSection.ViewModel = null;
        Bindings.StopTracking();
        Localized.UntrackTree(SettingsRoot);
        SettingsRoot.DataContext = null;
        _releaseNotesWindow?.Close();
        _releaseNotesWindow = null;
        ClearFeatureWidgetRows();
        ManagedStorageFolderList.Children.Clear();
        ViewModel.Dispose();
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
        void Apply()
        {
            ApplyTitleBarButtonColors();
            RefreshFeatureWidgetList();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Apply();
            return;
        }

        DispatcherQueue.TryEnqueue(Apply);
    }

    private Brush CreateFeatureWidgetIconBrush()
    {
        return new SolidColorBrush(IsEffectiveSettingsThemeDark() ? Colors.White : Colors.Black);
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

        double searchWidth = Math.Min(
            SettingsSearchMaxWidth,
            Math.Max(SettingsSearchMinWidth, width * SettingsSearchWidthRatio));

        SettingsSearchBox.Width = searchWidth;

        ContentHost.Width = Math.Min(ContentMaxWidth, availableContentWidth);
        ContentHost.MaxWidth = ContentMaxWidth;
        PathActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        PathActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        OpenPathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        PinQuickAccessButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ChangePathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        CleanupStorageButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetRow(AboutRightPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(AboutRightPanel, isNarrow ? 0 : 2);
        Grid.SetColumnSpan(AboutRightPanel, isNarrow ? 3 : 1);
        AboutRightPanel.Margin = isNarrow ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        AboutRightPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        AboutInfoActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        AboutInfoActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        AboutMeButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        AboutWebsiteButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        FeedbackEmailButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        StoreSupportButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        UpdateActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        UpdateActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        OneClickUpdateButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ViewReleaseNotesButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        OpenManualUpdateDownloadButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        GlobalHotkeyActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        GlobalHotkeyCaptureButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ResetGlobalHotkeyButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

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
        if (message == WmReservedHotkeyCapture)
        {
            if (_isRecordingHotkey)
            {
                _ = ApplyRecordedHotkeyAsync(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Windows,
                    (int)VirtualKey.Space));
            }

            return IntPtr.Zero;
        }

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
        FrameworkElement? TargetElement)
    {
        public bool IsPage => TargetElement is null;

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
