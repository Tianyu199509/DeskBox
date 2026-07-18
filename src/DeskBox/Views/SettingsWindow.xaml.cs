using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
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
        FontIcon Icon,
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
    private static readonly UIntPtr SettingsWindowSubclassId = new(1);

    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
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
    private bool _allowClose;
    private bool _isAppearanceSliderDragging;
    private bool _isRecordingHotkey;
    private bool _isRefreshingFeatureWidgetList;
    private bool _isSyncingNavigationSelection;
    private bool _isSettingsRootLoaded;
    private bool _isSelectingCity;
    private bool _isRefreshingBackupSnapshots;
    private string _currentSettingsSection = "General";
    private IReadOnlyDictionary<string, FrameworkElement> _settingsSectionElements = null!;
    private IReadOnlyList<SettingsSearchResult> _settingsSearchResults = Array.Empty<SettingsSearchResult>();

    private static readonly IReadOnlyDictionary<string, SettingsSectionRoute> SectionRoutes =
        new Dictionary<string, SettingsSectionRoute>(StringComparer.Ordinal)
        {
            ["General"] = new("General", "Settings.Section.General", null, "General"),
            ["Appearance"] = new("Appearance", "Settings.Section.Appearance", null, "Appearance"),
            ["CapsuleMode"] = new("CapsuleMode", "Settings.Section.CapsuleMode", null, "CapsuleMode"),
            ["AppearanceDetail"] = new("AppearanceDetail", "Settings.Appearance.DetailTitle", null, "AppearanceDetail"),
            ["FeatureWidgets"] = new("FeatureWidgets", "Settings.Section.FeatureWidgets", null, "FeatureWidgets"),
            ["Interaction"] = new("Interaction", "Settings.Section.Interaction", null, "Interaction"),
            ["InteractionHotkeySettings"] = new("InteractionHotkeySettings", "Settings.Interaction.Hotkeys.Title", "Interaction", "Interaction"),
            ["InteractionHoverSettings"] = new("InteractionHoverSettings", "Settings.Interaction.Hover.Title", "Interaction", "Interaction"),
            ["InteractionWindowSettings"] = new("InteractionWindowSettings", "Settings.Interaction.Window.Title", "Interaction", "Interaction"),
            ["Advanced"] = new("Advanced", "Settings.Section.Advanced", null, "Interaction"),
            ["Maintenance"] = new("Maintenance", "Settings.Section.Maintenance", null, "Maintenance"),
            ["About"] = new("About", "Settings.Nav.About", null, "About"),
            ["FileDisplaySettings"] = new("FileDisplaySettings", "Settings.Group.FileLayout.Title", "AppearanceDetail", "AppearanceDetail"),
            ["FileStorageSettings"] = new("FileStorageSettings", "Settings.Group.FileStorage.Title", "AppearanceDetail", "AppearanceDetail"),
            ["ManagedStorage"] = new("ManagedStorage", "Settings.ManagedStorage.PageTitle", "FileStorageSettings", "AppearanceDetail"),
            ["FileStackSettings"] = new("FileStackSettings", "Settings.FileStacks.PageTitle", "AppearanceDetail", "AppearanceDetail"),
            ["QuickCaptureSettings"] = new("QuickCaptureSettings", "Settings.QuickCapture.Title", "FeatureWidgets", "FeatureWidgets"),
            ["TodoSettings"] = new("TodoSettings", "Settings.Todo.Title", "FeatureWidgets", "FeatureWidgets"),
            ["MusicSettings"] = new("MusicSettings", "Settings.Music.Title", "FeatureWidgets", "FeatureWidgets"),
            ["WeatherSettings"] = new("WeatherSettings", "Settings.Weather.Title", "FeatureWidgets", "FeatureWidgets"),
            ["AppearanceMaterialSettings"] = new("AppearanceMaterialSettings", "Settings.Material.Title", "Appearance", "Appearance"),
            ["AppearanceDensitySettings"] = new("AppearanceDensitySettings", "Settings.Density.Title", "Appearance", "Appearance"),
            ["AppearanceWindowSettings"] = new("AppearanceWindowSettings", "Settings.Group.AppVisual.Title", "Appearance", "Appearance"),
            ["AppearanceAnimationSettings"] = new("AppearanceAnimationSettings", "Settings.Group.Animation.Title", "Appearance", "Appearance"),
            ["CapsuleBehaviorSettings"] = new("CapsuleBehaviorSettings", "Settings.CollapseBehavior.Title", "CapsuleMode", "CapsuleMode"),
            ["CapsuleContentSettings"] = new("CapsuleContentSettings", "Settings.CompactContent.Title", "CapsuleMode", "CapsuleMode"),
            ["CapsuleAnimationSettings"] = new("CapsuleAnimationSettings", "Settings.Capsule.Animation.Title", "CapsuleMode", "CapsuleMode"),
            ["CapsuleOverridesSettings"] = new("CapsuleOverridesSettings", "Settings.Capsule.Overrides.Title", "CapsuleMode", "CapsuleMode"),
            ["BackupRestoreSettings"] = new("BackupRestoreSettings", "Settings.DataBackup.Title", "Maintenance", "Maintenance"),
            ["DataHealthSettings"] = new("DataHealthSettings", "Settings.AttachmentHealth.Title", "Maintenance", "Maintenance"),
            ["CompatibilityDiagnosticsSettings"] = new("CompatibilityDiagnosticsSettings", "Settings.DragDropPermission.Title", "Maintenance", "Maintenance"),
            ["ResetSettings"] = new("ResetSettings", "Settings.Restore.Title", "Maintenance", "Maintenance")
        };

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsService settingsService, ThemeService themeService, LocalizationService localizationService)
    {
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, localizationService, App.Current.AppUpdateService);
        _settingsRootPointerPressedHandler = SettingsRoot_PointerPressedHandled;
        _settingsRootPointerReleasedHandler = SettingsRoot_PointerReleasedHandled;
        InitializeComponent();
        InitializeSettingsSectionElements();
        RefreshSettingsSearchResults();

        SettingsRoot.DataContext = ViewModel;
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

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
        {
            Kind = MicaKind.BaseAlt
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = WindowSubclassProc;
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += AppWindow_Closing;
        AppBranding.ApplyWindowIcon(_appWindow);
        InstallMinimumSizeHook();
        ApplyInitialWindowBounds(windowId);

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
        SettingsNavigationView.SelectedItem = GeneralNavItem;
        ShowSettingsSection("General");
        UpdateResponsiveLayout(GetWindowWidth());

        SizeChanged += SettingsWindow_SizeChanged;

        _resizeSettleTimer.Tick += ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Tick += SectionLayoutSettleTimer_Tick;
        Closed += SettingsWindow_Closed;
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
        _allowClose = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _isClosed)
        {
            return;
        }

        args.Cancel = true;
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        App.ScheduleLightMemoryCleanup();
    }

    private void SettingsRoot_Loaded(object sender, RoutedEventArgs e)
    {
        CollectResponsiveRows(SettingsRoot);
        RefreshFeatureWidgetList();
        _ = ViewModel.RefreshQuickAccessStateAsync();
        ViewModel.RefreshGlobalHotkeyState();
        _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        RefreshGlobalHotkeyControls();
        UpdateResponsiveLayout(GetWindowWidth());
        ApplyToggleSwitchContentVisibility();
        _isSettingsRootLoaded = true;
        RefreshSettingsSearchResults();
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
        _appWindow.Closing -= AppWindow_Closing;
        Closed -= SettingsWindow_Closed;
        SizeChanged -= SettingsWindow_SizeChanged;
        SettingsRoot.Loaded -= SettingsRoot_Loaded;
        SettingsRoot.RemoveHandler(UIElement.PointerPressedEvent, _settingsRootPointerPressedHandler);
        SettingsRoot.RemoveHandler(UIElement.PointerReleasedEvent, _settingsRootPointerReleasedHandler);

        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Stop();
        _sectionLayoutSettleTimer.Tick -= SectionLayoutSettleTimer_Tick;
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

        Bindings.StopTracking();
        Localized.UntrackTree(SettingsRoot);
        SettingsRoot.DataContext = null;
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

    private static TextBlock CreateDialogParagraph(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.WrapWholeWords,
            LineHeight = 24
        };
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
        AboutInfoActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        AboutInfoActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        AboutReasonButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        AboutWebsiteButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        AboutRepositoryButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        UpdateActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        UpdateActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        CheckUpdateButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        DownloadUpdateButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        InstallUpdateButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        OpenUpdateReleaseNotesButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
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

    private sealed record SettingsBreadcrumbItem(string SectionTag, string Title, double Opacity)
    {
        public override string ToString()
        {
            return Title;
        }
    }

    private sealed record SettingsSearchResult(
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

    private sealed record BackupSnapshotListItem(
        string Path,
        string Title,
        string Details,
        bool CanRestore);
}
