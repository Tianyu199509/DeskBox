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
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private readonly List<Grid> _settingRows = [];
    private readonly List<Grid> _metricRows = [];
    private readonly HashSet<Slider> _pressedAppearanceSliders = [];
    private readonly DispatcherTimer _resizeSettleTimer = new() { Interval = ResizeSettleDelay };
    private readonly DispatcherTimer _sectionLayoutSettleTimer = new() { Interval = SectionLayoutSettleDelay };
    private bool _isSubclassInstalled;
    private bool _isAppearanceSliderDragging;
    private bool _isRecordingHotkey;
    private bool _isRefreshingFeatureWidgetList;
    private bool _isSyncingNavigationSelection;
    private bool _isSettingsRootLoaded;
    private bool _isSelectingCity;
    private string _currentSettingsSection = "General";

    private static readonly IReadOnlyDictionary<string, SettingsSectionRoute> SectionRoutes =
        new Dictionary<string, SettingsSectionRoute>(StringComparer.Ordinal)
        {
            ["General"] = new("General", "Settings.Section.General", null, "General"),
            ["Appearance"] = new("Appearance", "Settings.Section.Appearance", null, "Appearance"),
            ["AppearanceDetail"] = new("AppearanceDetail", "Settings.Appearance.DetailTitle", null, "AppearanceDetail"),
            ["FeatureWidgets"] = new("FeatureWidgets", "Settings.Section.FeatureWidgets", null, "FeatureWidgets"),
            ["Interaction"] = new("Interaction", "Settings.Section.Interaction", null, "Interaction"),
            ["Advanced"] = new("Advanced", "Settings.Section.Advanced", null, "Interaction"),
            ["Maintenance"] = new("Maintenance", "Settings.Section.Maintenance", null, "Maintenance"),
            ["About"] = new("About", "Settings.Nav.About", null, "About"),
            ["ManagedStorage"] = new("ManagedStorage", "Settings.ManagedStorage.PageTitle", "AppearanceDetail", "AppearanceDetail"),
            ["QuickCaptureSettings"] = new("QuickCaptureSettings", "Settings.QuickCapture.Title", "FeatureWidgets", "FeatureWidgets"),
            ["TodoSettings"] = new("TodoSettings", "Settings.Todo.Title", "FeatureWidgets", "FeatureWidgets"),
            ["MusicSettings"] = new("MusicSettings", "Settings.Music.Title", "FeatureWidgets", "FeatureWidgets"),
            ["WeatherSettings"] = new("WeatherSettings", "Settings.Weather.Title", "FeatureWidgets", "FeatureWidgets")
        };

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsService settingsService, ThemeService themeService, LocalizationService localizationService)
    {
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, localizationService, App.Current.AppUpdateService);
        InitializeComponent();

        SettingsRoot.DataContext = ViewModel;
        SettingsRoot.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(SettingsRoot_PointerPressedHandled),
            handledEventsToo: true);
        SettingsRoot.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(SettingsRoot_PointerReleasedHandled),
            handledEventsToo: true);
        CollectResponsiveRows(SettingsRoot);
        SettingsRoot.Loaded += (_, _) =>
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
        };

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

        SizeChanged += (_, args) =>
        {
            UpdateResponsiveLayout(args.Size.Width, preferMeasuredContentWidth: false);
            RestartResizeSettleTimer();
        };

        _resizeSettleTimer.Tick += ResizeSettleTimer_Tick;
        _sectionLayoutSettleTimer.Tick += SectionLayoutSettleTimer_Tick;

        Closed += (_, _) =>
        {
            _resizeSettleTimer.Stop();
            _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
            _sectionLayoutSettleTimer.Stop();
            _sectionLayoutSettleTimer.Tick -= SectionLayoutSettleTimer_Tick;
            Win32Helper.ClearWindowTopMost(_hWnd);
            RemoveMinimumSizeHook();
            _themeService.AppearanceChanged -= OnAppearanceChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (App.Current.GlobalHotkeyService is { } hotkeyService)
            {
                hotkeyService.RegistrationChanged -= OnGlobalHotkeyRegistrationChanged;
            }
            ViewModel.Dispose();
            SettingsRoot.DataContext = null;
            _settingRows.Clear();
            _metricRows.Clear();
            _pressedAppearanceSliders.Clear();
        };
    }

    private void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingNavigationSelection)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string sectionTag })
        {
            ShowSettingsSection(sectionTag, isNestedSection: false);
        }
    }

    private void SettingsNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (TryGetSectionRoute(_currentSettingsSection, out var route) &&
            !string.IsNullOrWhiteSpace(route.ParentTag))
        {
            NavigateToSettingsSection(route.ParentTag);
        }
    }

    public void ShowSection(string sectionTag)
    {
        NavigateToSettingsSection(sectionTag);
    }

    public void RefreshUpdateStateFromService()
    {
        ViewModel.RefreshCachedUpdateState();
    }

    private NavigationViewItem? FindNavItemByTag(string tag)
    {
        foreach (var item in SettingsNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string navTag && navTag == tag)
            {
                return navItem;
            }
        }
        return null;
    }

    private void NavigateToSettingsSection(string sectionTag)
    {
        ShowSettingsSection(sectionTag);
        var navItem = GetNavItemForSection(sectionTag);
        if (navItem is not null && !ReferenceEquals(SettingsNavigationView.SelectedItem, navItem))
        {
            _isSyncingNavigationSelection = true;
            try
            {
                SettingsNavigationView.SelectedItem = navItem;
            }
            finally
            {
                _isSyncingNavigationSelection = false;
            }
        }
    }

    private NavigationViewItem? GetNavItemForSection(string sectionTag)
    {
        return TryGetSectionRoute(sectionTag, out var route)
            ? FindNavItemByTag(route.NavTag) ?? GeneralNavItem
            : GeneralNavItem;
    }

    private void ShowSettingsSection(string sectionTag, bool isNestedSection = false)
    {
        if (!TryGetSectionRoute(sectionTag, out var route))
        {
            sectionTag = "General";
            route = SectionRoutes[sectionTag];
        }

        isNestedSection = !string.IsNullOrWhiteSpace(route.ParentTag);
        _currentSettingsSection = sectionTag;
        AppearanceSection.Visibility = sectionTag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceDetailSection.Visibility = sectionTag == "AppearanceDetail" ? Visibility.Visible : Visibility.Collapsed;
        FeatureWidgetsSection.Visibility = sectionTag == "FeatureWidgets" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "FeatureWidgets")
        {
            RefreshFeatureWidgetList();
        }
        QuickCaptureSettingsSection.Visibility = sectionTag == "QuickCaptureSettings" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "QuickCaptureSettings")
        {
            ViewModel.RefreshQuickCaptureClipboardDiagnostics();
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        TodoSettingsSection.Visibility = sectionTag == "TodoSettings" ? Visibility.Visible : Visibility.Collapsed;
        MusicSettingsSection.Visibility = sectionTag == "MusicSettings" ? Visibility.Visible : Visibility.Collapsed;
WeatherSettingsSection.Visibility = sectionTag == "WeatherSettings" ? Visibility.Visible : Visibility.Collapsed;
        InteractionSection.Visibility = sectionTag is "Interaction" or "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        ManagedStorageSection.Visibility = sectionTag == "ManagedStorage" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "ManagedStorage")
        {
            RefreshManagedStorageFolderList();
        }
        GeneralSection.Visibility = sectionTag == "General" ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceSection.Visibility = sectionTag == "Maintenance" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "Maintenance")
        {
            ViewModel.RefreshDragDropPermissionDiagnostic();
        }
        AboutSection.Visibility = sectionTag == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsNavigationView.IsBackButtonVisible = isNestedSection
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        UpdateBreadcrumb(route);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            PageScroller.ChangeView(null, 0, null, disableAnimation: true);
            RestartSectionLayoutSettleTimer();
        });
    }

    private void UpdateBreadcrumb(SettingsSectionRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.ParentTag) ||
            !TryGetSectionRoute(route.ParentTag, out var parentRoute))
        {
            SettingsBreadcrumbBar.Visibility = Visibility.Collapsed;
            SettingsBreadcrumbBar.ItemsSource = null;
            return;
        }

        SettingsBreadcrumbBar.ItemsSource = new[]
        {
            new SettingsBreadcrumbItem(parentRoute.Tag, _localizationService.T(parentRoute.TitleKey), 0.62),
            new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 1.0)
        };
        SettingsBreadcrumbBar.Visibility = Visibility.Visible;
    }

    private void SettingsBreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is not SettingsBreadcrumbItem item ||
            string.Equals(item.SectionTag, _currentSettingsSection, StringComparison.Ordinal))
        {
            return;
        }

        NavigateToSettingsSection(item.SectionTag);
    }

    private static bool TryGetSectionRoute(string sectionTag, out SettingsSectionRoute route)
    {
        return SectionRoutes.TryGetValue(sectionTag, out route!);
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        ViewModel.SetCustomAccentColor(color);
    }

    private void SettingsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo ||
            combo.Tag is not string menuKind ||
            combo.SelectedIndex < 0 ||
            e.AddedItems.Count == 0 ||
            e.AddedItems[0] is not string displayName)
        {
            return;
        }

        switch (menuKind)
        {
            case "Theme":
                ViewModel.SelectedTheme = ViewModel.AvailableThemes[combo.SelectedIndex];
                break;
            case "Language":
                ViewModel.SelectedLanguage = ViewModel.AvailableLanguages[combo.SelectedIndex];
                break;
            case "WidgetCorner":
                ViewModel.SelectedWidgetCornerPreference = ViewModel.AvailableWidgetCornerPreferences[combo.SelectedIndex];
                break;
            case "WidgetMaterial":
                ViewModel.SelectedWidgetMaterialType = ViewModel.AvailableWidgetMaterialTypes[combo.SelectedIndex];
                break;
            case "WidgetBorder":
                ViewModel.SelectedWidgetBorderStyle = ViewModel.AvailableWidgetBorderStyles[combo.SelectedIndex];
                break;
            case "WidgetAnimationEffect":
                ViewModel.SelectedWidgetAnimationEffect = ViewModel.AvailableWidgetAnimationEffects[combo.SelectedIndex];
                break;
            case "WidgetAnimationSpeed":
                ViewModel.SelectedWidgetAnimationSpeed = ViewModel.AvailableWidgetAnimationSpeeds[combo.SelectedIndex];
                break;
            case "WidgetAnimationSlideDirection":
                ViewModel.SelectedWidgetAnimationSlideDirection = ViewModel.AvailableWidgetAnimationSlideDirections[combo.SelectedIndex];
                break;
            case "WidgetAnimationEasingIntensity":
                ViewModel.SelectedWidgetAnimationEasingIntensity = ViewModel.AvailableWidgetAnimationEasingIntensities[combo.SelectedIndex];
                break;
            case "DisplayWidgetChromeMode":
                ViewModel.SelectedDisplayWidgetChromeMode = ViewModel.AvailableDisplayWidgetChromeModes[combo.SelectedIndex];
                break;
            case "InteractiveWidgetChromeMode":
                ViewModel.SelectedInteractiveWidgetChromeMode = ViewModel.AvailableInteractiveWidgetChromeModes[combo.SelectedIndex];
                break;
            case "WidgetTitleIconMode":
                ViewModel.SelectedWidgetTitleIconMode = ViewModel.AvailableWidgetTitleIconModes[combo.SelectedIndex];
                break;
            case "WidgetLayerMode":
                if (!_isSettingsRootLoaded)
                {
                    return;
                }

                ViewModel.SelectedWidgetLayerMode = ViewModel.AvailableWidgetLayerModes[combo.SelectedIndex];
                break;
            case "QuickCaptureDefaultView":
                ViewModel.SelectedQuickCaptureDefaultView = ViewModel.AvailableQuickCaptureDefaultViews[combo.SelectedIndex];
                break;
            case "QuickCaptureTabStyle":
                ViewModel.SelectedQuickCaptureTabStyle = ViewModel.AvailableWidgetTabStyles[combo.SelectedIndex];
                break;
            case "TodoNewTaskPosition":
                ViewModel.SelectedTodoNewTaskPosition = ViewModel.AvailableTodoNewTaskPositions[combo.SelectedIndex];
                break;
            case "TodoDefaultFilter":
                ViewModel.SelectedTodoDefaultFilter = ViewModel.AvailableTodoDefaultFilters[combo.SelectedIndex];
                break;
            case "TodoTabStyle":
                ViewModel.SelectedTodoTabStyle = ViewModel.AvailableWidgetTabStyles[combo.SelectedIndex];
                break;
            case "TodoReminderOffset":
                ViewModel.SelectedTodoReminderOffsetMinutes = ViewModel.AvailableTodoReminderOffsetMinutes[combo.SelectedIndex];
                break;
case "MusicRhythmStyle":
ViewModel.SelectedMusicRhythmStyle = ViewModel.AvailableMusicRhythmStyles[combo.SelectedIndex];
break;
case "WeatherTemperatureUnit":
ViewModel.SelectedWeatherTemperatureUnit = ViewModel.AvailableWeatherTemperatureUnits[combo.SelectedIndex];
break;
case "WeatherWindSpeedUnit":
ViewModel.SelectedWeatherWindSpeedUnit = ViewModel.AvailableWeatherWindSpeedUnits[combo.SelectedIndex];
break;
case "WeatherDefaultView":
ViewModel.SelectedWeatherDefaultView = ViewModel.AvailableWeatherDefaultViews[combo.SelectedIndex];
break;
case "WeatherSkin":
ViewModel.SelectedWeatherSkin = ViewModel.AvailableWeatherSkins[combo.SelectedIndex];
break;
case "WeatherRefreshInterval":
ViewModel.SelectedWeatherRefreshInterval = ViewModel.AvailableWeatherRefreshIntervals[combo.SelectedIndex];
break;
            case "TrayIconStyle":
                ViewModel.SelectedTrayIconStyle = ViewModel.AvailableTrayIconStyles[combo.SelectedIndex];
                break;
        }
    }

    private void SettingsDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button || button.Tag is not string menuKind)
        {
            return;
        }

        string selectedValue;
        IReadOnlyList<string> values;
        Action<string> applyValue;
        Func<string, string> displayValue;

        switch (menuKind)
        {
            case "Theme":
                selectedValue = ViewModel.SelectedTheme;
                values = ViewModel.AvailableThemes;
                applyValue = value => ViewModel.SelectedTheme = value;
                displayValue = ViewModel.GetThemeDisplayName;
                break;

            case "Language":
                selectedValue = ViewModel.SelectedLanguage;
                values = ViewModel.AvailableLanguages;
                applyValue = value => ViewModel.SelectedLanguage = value;
                displayValue = ViewModel.GetLanguageDisplayName;
                break;

            case "WidgetCorner":
                selectedValue = ViewModel.SelectedWidgetCornerPreference;
                values = ViewModel.AvailableWidgetCornerPreferences;
                applyValue = value => ViewModel.SelectedWidgetCornerPreference = value;
                displayValue = ViewModel.GetCornerDisplayName;
                break;

            case "WidgetAnimationEffect":
                selectedValue = ViewModel.SelectedWidgetAnimationEffect;
                values = ViewModel.AvailableWidgetAnimationEffects;
                applyValue = value => ViewModel.SelectedWidgetAnimationEffect = value;
                displayValue = ViewModel.GetWidgetAnimationEffectDisplayName;
                break;

            case "WidgetAnimationSpeed":
                selectedValue = ViewModel.SelectedWidgetAnimationSpeed;
                values = ViewModel.AvailableWidgetAnimationSpeeds;
                applyValue = value => ViewModel.SelectedWidgetAnimationSpeed = value;
                displayValue = ViewModel.GetWidgetAnimationSpeedDisplayName;
                break;

            case "WidgetAnimationSlideDirection":
                selectedValue = ViewModel.SelectedWidgetAnimationSlideDirection;
                values = ViewModel.AvailableWidgetAnimationSlideDirections;
                applyValue = value => ViewModel.SelectedWidgetAnimationSlideDirection = value;
                displayValue = ViewModel.GetWidgetAnimationSlideDirectionDisplayName;
                break;

            case "WidgetAnimationEasingIntensity":
                selectedValue = ViewModel.SelectedWidgetAnimationEasingIntensity;
                values = ViewModel.AvailableWidgetAnimationEasingIntensities;
                applyValue = value => ViewModel.SelectedWidgetAnimationEasingIntensity = value;
                displayValue = ViewModel.GetWidgetAnimationEasingIntensityDisplayName;
                break;

            default:
                return;
        }

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        foreach (string value in values)
        {
            var item = new MenuFlyoutItem
            {
                Text = displayValue(value),
                MinWidth = button.ActualWidth > 0 ? button.ActualWidth : button.MinWidth,
                Icon = string.Equals(value, selectedValue, StringComparison.Ordinal)
                    ? new FontIcon { Glyph = "\uE73E" }
                    : null
            };
            item.Click += (_, _) => applyValue(value);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void HoverButtonActionsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        double flyoutWidth = Math.Max(220, Math.Max(button.ActualWidth, button.MinWidth));
        var panel = new StackPanel
        {
            Width = flyoutWidth,
            Padding = new Thickness(6),
            Spacing = 2
        };

        foreach (string action in ViewModel.AvailableWidgetHoverButtonActions)
        {
            panel.Children.Add(CreateHoverButtonActionFlyoutRow(action, flyoutWidth));
        }

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(FrameworkElement.MinWidthProperty, flyoutWidth)
                }
            }
        };
        flyout.ShowAt(button);
    }

    private Button CreateHoverButtonActionFlyoutRow(string action, double flyoutWidth)
    {
        var checkIcon = new FontIcon
        {
            Width = 18,
            FontSize = 13,
            Glyph = "\uE73E",
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = ViewModel.IsHoverButtonActionSelected(action)
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        var textBlock = new TextBlock
        {
            Text = ViewModel.GetHoverButtonActionDisplayName(action),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rowContent = new Grid
        {
            ColumnSpacing = 8
        };
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(checkIcon, 0);
        Grid.SetColumn(textBlock, 1);
        rowContent.Children.Add(checkIcon);
        rowContent.Children.Add(textBlock);

        var rowButton = new Button
        {
            Tag = action,
            Content = rowContent,
            Width = flyoutWidth - 12,
            MinHeight = 34,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Opacity = ViewModel.CanToggleHoverButtonAction(action) ? 1.0 : 0.48
        };
        rowButton.Click += (_, _) =>
        {
            if (!ViewModel.CanToggleHoverButtonAction(action))
            {
                return;
            }

            ViewModel.ToggleHoverButtonAction(action);
            RefreshHoverButtonActionsFlyout((StackPanel)rowButton.Parent);
        };
        return rowButton;
    }

    private void RefreshHoverButtonActionsFlyout(StackPanel panel)
    {
        foreach (var child in panel.Children.OfType<Button>())
        {
            if (child.Tag is not string action)
            {
                continue;
            }

            child.Opacity = ViewModel.CanToggleHoverButtonAction(action) ? 1.0 : 0.48;
            if (child.Content is Grid rowContent &&
                rowContent.Children.OfType<FontIcon>().FirstOrDefault() is FontIcon checkIcon)
            {
                checkIcon.Visibility = ViewModel.IsHoverButtonActionSelected(action)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.FeatureWidgetEntries))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
            return;
        }

        RefreshFeatureWidgetList();
    }

    private void OnLanguageChanged()
    {
        App.Current.RefreshSettingsWindow();
    }

    private void ApplyLocalizedText()
    {
        Title = _localizationService.T("Settings.WindowTitle");
        Localized.RefreshAll(_localizationService);
        ApplyToggleSwitchContentVisibility();
        RefreshFeatureWidgetList();
        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
        if (string.Equals(_currentSettingsSection, "ManagedStorage", StringComparison.Ordinal))
        {
            RefreshManagedStorageFolderList();
        }
    }

    private void ApplyToggleSwitchContentVisibility()
    {
        foreach (var toggle in FindDescendants<ToggleSwitch>(SettingsRoot))
        {
            ClearToggleSwitchContent(toggle);
        }
    }

    private static void ClearToggleSwitchContent(ToggleSwitch toggle)
    {
        toggle.OnContent = string.Empty;
        toggle.OffContent = string.Empty;
    }

    private void RefreshFeatureWidgetList()
    {
        if (FeatureWidgetList is null)
        {
            return;
        }

        _isRefreshingFeatureWidgetList = true;
        try
        {
            FeatureWidgetList.Children.Clear();

            foreach (var entry in ViewModel.FeatureWidgetEntries)
            {
                FeatureWidgetList.Children.Add(CreateFeatureWidgetRow(entry));
            }
        }
        finally
        {
            _isRefreshingFeatureWidgetList = false;
        }

        ApplyToggleSwitchContentVisibility();
    }

    private Border CreateFeatureWidgetRow(FeatureWidgetEntry entry)
    {
        var border = new Border
        {
            Style = (Style)SettingsRoot.Resources["SettingsGroupStyle"]
        };

        var root = new Grid
        {
            MinHeight = 70
        };

        if (entry.HasSettingsPage && !string.IsNullOrWhiteSpace(entry.SettingsSectionTag))
        {
            var button = new Button
            {
                Padding = new Thickness(0),
                Style = (Style)SettingsRoot.Resources["DrillDownRowStyle"],
                Tag = entry.SettingsSectionTag
            };
            button.Click += FeatureWidgetSettingsButton_Click;
            root.Children.Add(button);
        }

        var content = new Grid
        {
            Padding = new Thickness(12, 10, 10, 10),
            ColumnSpacing = 10
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = entry.Glyph,
            FontSize = 18,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            Foreground = CreateFeatureWidgetIconBrush(),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var textPanel = new StackPanel
        {
            IsHitTestVisible = false,
            Style = (Style)SettingsRoot.Resources["SettingTextPanelStyle"]
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = entry.Title,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = entry.DisplayDescription,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });
        Grid.SetColumn(textPanel, 1);
        content.Children.Add(textPanel);

        if (FeatureWidgetSettings.IsFeatureWidget(entry.Kind))
        {
            var resetButton = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)SettingsRoot.Resources["IconActionButtonStyle"],
                Tag = entry.Kind,
                Content = new FontIcon
                {
                    Glyph = "\uE72C",
                    FontSize = 13,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    Foreground = CreateFeatureWidgetIconBrush()
                }
            };
            ToolTipService.SetToolTip(resetButton, _localizationService.T("Settings.FeatureWidgets.ResetTooltip"));
            resetButton.Click += FeatureWidgetResetButton_Click;
            Canvas.SetZIndex(resetButton, 2);
            Grid.SetColumn(resetButton, 2);
            content.Children.Add(resetButton);
        }

        if (entry.ShowToggle)
        {
            var toggle = new ToggleSwitch
            {
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsOn = entry.IsEnabled,
                IsEnabled = entry.CanToggle,
                Tag = entry.Kind
            };
            ClearToggleSwitchContent(toggle);
            toggle.Toggled += FeatureWidgetToggle_Toggled;
            Canvas.SetZIndex(toggle, 2);
            Grid.SetColumn(toggle, 3);
            content.Children.Add(toggle);
        }

        if (entry.HasSettingsPage && !string.IsNullOrWhiteSpace(entry.SettingsSectionTag))
        {
            var arrow = new FontIcon
            {
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                Glyph = "\uE974",
                Foreground = CreateFeatureWidgetIconBrush(),
                Margin = new Thickness(0, 0, -4, 0)
            };
            Grid.SetColumn(arrow, 4);
            content.Children.Add(arrow);
        }

        root.Children.Add(content);
        border.Child = root;
        return border;
    }

    private void FeatureWidgetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigateToSettingsSection(sectionTag);
        }
    }

    private async void FeatureWidgetResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WidgetKind kind } button)
        {
            button.IsEnabled = false;
            try
            {
                await ViewModel.ResetFeatureWidgetAsync(kind);
                RefreshFeatureWidgetList();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private void FeatureWidgetToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingFeatureWidgetList)
        {
            return;
        }

        if (sender is ToggleSwitch { Tag: WidgetKind kind } toggle)
        {
            ViewModel.SetWidgetEnabled(kind, toggle.IsOn);
            DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
        }
    }

    private void EditableSettingsTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Tag = textBox.Text;
        }
    }

    private void EditableSettingsTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || sender is not TextBox textBox)
        {
            return;
        }

        if (textBox.Tag is string originalText)
        {
            textBox.Text = originalText;
        }

        SettingsRoot.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void WeatherCitySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Suppress search when a city is being selected (SuggestionChosen → TextChanged → QuerySubmitted chain)
        if (_isSelectingCity || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _ = ViewModel.UpdateWeatherCitySuggestionsAsync(sender.Text);
    }

    private void WeatherCitySearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is WeatherCitySearchResult result)
        {
            _isSelectingCity = true;
            ViewModel.SelectWeatherCity(result);
            // Reset on next dispatch cycle, after TextChanged and QuerySubmitted have fired
            DispatcherQueue.TryEnqueue(() => _isSelectingCity = false);
        }
    }

    private void WeatherCitySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // If a suggestion was chosen, SuggestionChosen already handled it
        if (args.ChosenSuggestion is not null)
        {
            return;
        }

        // User pressed Enter without selecting a suggestion — pick the first match
        if (!string.IsNullOrWhiteSpace(args.QueryText) && ViewModel.WeatherCitySuggestions.Count > 0)
        {
            _isSelectingCity = true;
            ViewModel.SelectWeatherCity(ViewModel.WeatherCitySuggestions[0]);
            DispatcherQueue.TryEnqueue(() => _isSelectingCity = false);
        }
    }

    private void WeatherCitySearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Clear search results and restore the saved city name if the user didn't select anything.
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ClearWeatherCitySuggestions();
            ViewModel.RestoreWeatherCitySearchText();
        });
    }

    private void ChangeGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        BeginHotkeyRecording();
    }

    private void GlobalHotkeyCaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new DeskBox.Models.GlobalHotkeyGesture(
            GetPressedHotkeyModifiers(),
            (int)e.Key);
        _ = ApplyRecordedHotkeyAsync(gesture);
        e.Handled = true;
    }

    private void GlobalHotkeyCaptureButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            EndHotkeyRecording();
        }
    }

    private async void ResetGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.ResetToDefault(out string? error))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"));
        }

        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
    }

    private void AppearanceSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider)
        {
            return;
        }

        BeginAppearanceSliderDrag();
    }

    private void AppearanceSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        BeginAppearanceSliderDrag();
    }

    private void AppearanceSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState == FocusState.Pointer)
        {
            BeginAppearanceSliderDrag();
            KeepSliderThumbExpanded(slider);
        }
    }

    private void SettingsRoot_PointerPressedHandled(object sender, PointerRoutedEventArgs e)
    {
        if (TryFindAncestor<Slider>(e.OriginalSource as DependencyObject, out var slider))
        {
            BeginAppearanceSliderDrag();
            _pressedAppearanceSliders.Add(slider);
            KeepSliderThumbExpanded(slider);
        }
    }

    private void SettingsRoot_PointerReleasedHandled(object sender, PointerRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void BeginAppearanceSliderDrag()
    {
        _isAppearanceSliderDragging = true;
        ViewModel.SuppressAppearanceNotifications = true;
        ViewModel.DeferAppearancePersistence = true;
    }

    private void CommitAppearanceSliderDrag()
    {
        if (!_isAppearanceSliderDragging)
        {
            return;
        }

        _isAppearanceSliderDragging = false;
        ViewModel.DeferAppearancePersistence = false;
        ViewModel.SuppressAppearanceNotifications = false;
        ResetPressedAppearanceSliders();
        ViewModel.CommitAppearanceChanges();
    }

    private void KeepSliderThumbExpanded(Slider slider)
    {
        foreach (var ellipse in FindDescendants<Ellipse>(slider))
        {
            if (ellipse.Name != "SliderInnerThumb")
            {
                continue;
            }

            if (ellipse.RenderTransform is CompositeTransform transform)
            {
                transform.ScaleX = 1.167;
                transform.ScaleY = 1.167;
            }
        }
    }

    private void ResetPressedAppearanceSliders()
    {
        foreach (var slider in _pressedAppearanceSliders.ToList())
        {
            foreach (var ellipse in FindDescendants<Ellipse>(slider))
            {
                if (ellipse.Name != "SliderInnerThumb")
                {
                    continue;
                }

                if (ellipse.RenderTransform is CompositeTransform transform)
                {
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                }
            }
        }

        _pressedAppearanceSliders.Clear();
    }

    private void BeginHotkeyRecording()
    {
        _isRecordingHotkey = true;
        GlobalHotkeyCaptureButton.Content = _localizationService.T("Settings.GlobalHotkey.Recording");
        GlobalHotkeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        RefreshGlobalHotkeyControls();
    }

    private void RefreshGlobalHotkeyControls()
    {
        if (!_isRecordingHotkey)
        {
            GlobalHotkeyCaptureButton.Content = ViewModel.GlobalHotkeyText;
        }
    }

    private async Task ApplyRecordedHotkeyAsync(DeskBox.Models.GlobalHotkeyGesture gesture)
    {
        EndHotkeyRecording();
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.TryApplyGesture(gesture, out string? error))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"));
        }

        ViewModel.RefreshGlobalHotkeyState();
    }

    private DeskBox.Models.HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        var modifiers = DeskBox.Models.HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Control;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Alt;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Shift;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Windows.System.VirtualKey key)
    {
        return key is
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows;
    }

    private static bool TryFindAncestor<T>(DependencyObject? source, out T result) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                result = typed;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        result = null!;
        return false;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nestedChild in FindDescendants<T>(child))
            {
                yield return nestedChild;
            }
        }
    }

    private async void ChangeManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        string? folderPath = FolderPickerService.PickFolder(_hWnd);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folderPath);
        if (string.Equals(normalizedPath, ViewModel.ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.Dialog.MigrateTitle"),
                PrimaryButtonText = _localizationService.T("Settings.Dialog.MigrateButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.Format(
                        "Settings.Dialog.MigrateBody",
                        affectedCount,
                        ViewModel.ManagedStorageRootPath,
                        normalizedPath),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                var result = await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
                await ShowInfoDialogAsync(
                    _localizationService.T("Settings.Dialog.MigrateCompleteTitle"),
                    _localizationService.Format(
                        "Settings.Dialog.MigrateCompleteBody",
                        result.AffectedWidgetCount,
                        result.OldRootPath,
                        result.NewRootPath));
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = _localizationService.Format("Settings.Dialog.MigrateFailedBody", ex.Message),
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await errorDialog.ShowAsync();
                return;
            }
        }

        ViewModel.UpdateManagedStorageRootPath(normalizedPath);
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        string path = ViewModel.ManagedStorageRootPath;
        Directory.CreateDirectory(path);
        Win32Helper.OpenFile(path);
    }

    private async void PinManagedStorageToQuickAccessButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanInvokeQuickAccessAction)
        {
            return;
        }

        string path = ViewModel.ManagedStorageRootPath;
        bool shouldUnpin = ViewModel.ShouldUnpinManagedStorageFromQuickAccess;

        ViewModel.SetQuickAccessBusy(true);
        try
        {
            QuickAccessOperationResult result = shouldUnpin
                ? await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(path)
                : await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(path);

            if (result.Succeeded)
            {
                ViewModel.SetQuickAccessPinState(shouldUnpin ? QuickAccessPinState.NotPinned : QuickAccessPinState.Pinned);
                await Task.Delay(500);
                await ViewModel.RefreshQuickAccessStateAsync();
                return;
            }

            if (SettingsRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = shouldUnpin
                    ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                    : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                CloseButtonText = _localizationService.T("Common.Ok"),
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    Text = shouldUnpin
                        ? _localizationService.Format("Settings.Dialog.UnpinQuickAccessFailedBody", result.Error ?? string.Empty)
                        : _localizationService.Format("Settings.Dialog.PinQuickAccessFailedBody", result.Error ?? string.Empty),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsWindow] Failed to update Quick Access pin state: {ex}");
            if (SettingsRoot.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = shouldUnpin
                        ? _localizationService.T("Settings.Dialog.UnpinQuickAccessFailedTitle")
                        : _localizationService.T("Settings.Dialog.PinQuickAccessFailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = shouldUnpin
                            ? _localizationService.Format("Settings.Dialog.UnpinQuickAccessFailedBody", ex.Message)
                            : _localizationService.Format("Settings.Dialog.PinQuickAccessFailedBody", ex.Message),
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await dialog.ShowAsync();
            }
        }
        finally
        {
            ViewModel.SetQuickAccessBusy(false);
        }
    }

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OpenSourceRepositoryUrl);
    }

    private void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OfficialWebsiteLink);
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckForUpdatesAsync();
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadAvailableUpdateAsync();
    }

    private void OpenUpdateReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.AvailableUpdateReleaseNotesUrl))
        {
            Win32Helper.OpenFile(ViewModel.AvailableUpdateReleaseNotesUrl);
        }
    }

    private void OpenManualUpdateDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.UpdateFallbackUrl))
        {
            Win32Helper.OpenFile(ViewModel.UpdateFallbackUrl);
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Update.InstallConfirmTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.Update.InstallConfirmBody"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("Settings.Update.Install"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = ViewModel.StartDownloadedUpdateInstall();
        if (!result.Success)
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Update.InstallStartFailedTitle"),
                result.ErrorMessage ?? _localizationService.T("Settings.Update.InstallStartFailedBody"));
            return;
        }

        await App.Current.ShutdownForUpdateAsync();
    }

    private void RefreshDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDragDropPermissionDiagnostic();
    }

    private async void RepairDragDropPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = ViewModel.RepairDragDropPermission();
        if (result.NeedsRelaunch)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = _localizationService.T("Settings.DragDropPermission.RelaunchTitle"),
                PrimaryButtonText = _localizationService.T("Settings.DragDropPermission.RelaunchButton"),
                CloseButtonText = _localizationService.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = _localizationService.T("Settings.DragDropPermission.RelaunchBody"),
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (DragDropPermissionService.TryRelaunchAsExplorerUser())
                {
                    App.Current.Exit();
                }
                else
                {
                    await ShowInfoDialogAsync(
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedTitle"),
                        _localizationService.T("Settings.DragDropPermission.RelaunchFailedBody"));
                }
            }

            return;
        }

        await ShowInfoDialogAsync(
            _localizationService.T(result.Success
                ? "Settings.DragDropPermission.RepairCompleteTitle"
                : "Settings.DragDropPermission.RepairFailedTitle"),
            result.Success
                ? _localizationService.Format("Settings.DragDropPermission.RepairCompleteBody", result.RepairedCount)
                : result.FailureMessage);
    }

    private async void OpenUacSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => Win32Helper.OpenFile("UserAccountControlSettings.exe"));
    }

    private void OpenQuickCaptureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("QuickCaptureSettings");
    }

    private void OpenTodoSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("TodoSettings");
    }

    private void OpenAppearanceDetailButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("AppearanceDetail");
    }

    private async void ClearQuickCaptureDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recordCount = data.Items.Count(item => !item.IsDeleted);
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    recordCount,
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void ClearQuickCaptureRecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var data = await App.Current.QuickCaptureService.GetDataAsync();
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    recentCount),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await App.Current.QuickCaptureService.ClearRecentAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
    }

    private async void CleanupQuickCaptureImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var result = await App.Current.QuickCaptureService.CleanupUnusedImageCacheAsync();
        await ViewModel.RefreshQuickCaptureImageCacheInfoAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.QuickCapture.ImageCacheCleanupTitle"),
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "Settings.QuickCapture.ImageCacheCleanupDescription",
                    result.DeletedFileCount,
                    SettingsViewModel.FormatBytes(result.DeletedBytes)),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void ShowOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowOnboarding();
    }

    private async void RestoreDefaultSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Dialog.RestoreTitle"),
            PrimaryButtonText = _localizationService.T("Common.Restore"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.Dialog.RestoreBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RestoreDefaultPreferencesAsync();
    }

    private async void ShowProductReasonButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var content = new StackPanel
        {
            MaxWidth = 560,
            Spacing = 16
        };

        for (int index = 1; index <= 5; index++)
        {
            content.Children.Add(CreateDialogParagraph(
                _localizationService.T($"Settings.Dialog.ProductReasonP{index}")));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.About.ReasonTitle"),
            CloseButtonText = _localizationService.T("Settings.Dialog.ProductReasonClose"),
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        await dialog.ShowAsync();
    }

    private void CleanupManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSettingsSection("ManagedStorage");
    }

    private void RefreshManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshManagedStorageFolderList();
    }

    private void RefreshManagedStorageFolderList()
    {
        ManagedStorageFolderList.Children.Clear();

        if (App.Current.WidgetManager is not { } widgetManager)
        {
            ManagedStorageEmptyState.Visibility = Visibility.Visible;
            ManagedStorageFolderList.Visibility = Visibility.Collapsed;
            ManagedStorageSummaryText.Text = _localizationService.T("Settings.ManagedStorage.SummaryUnavailable");
            return;
        }

        var candidates = widgetManager.GetOrphanManagedStorageFolders();
        bool hasCandidates = candidates.Count > 0;
        ManagedStorageEmptyState.Visibility = hasCandidates ? Visibility.Collapsed : Visibility.Visible;
        ManagedStorageFolderList.Visibility = hasCandidates ? Visibility.Visible : Visibility.Collapsed;
        ManagedStorageSummaryText.Text = hasCandidates
            ? _localizationService.Format("Settings.ManagedStorage.Summary", candidates.Count)
            : _localizationService.T("Settings.ManagedStorage.SummaryEmpty");

        for (int index = 0; index < candidates.Count; index++)
        {
            if (index > 0)
            {
                ManagedStorageFolderList.Children.Add(CreateSettingDivider());
            }

            ManagedStorageFolderList.Children.Add(CreateManagedStorageFolderRow(candidates[index]));
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            CollectResponsiveRows(SettingsRoot);
            UpdateResponsiveLayout(GetWindowWidth());
        });
    }

    private Grid CreateManagedStorageFolderRow(ManagedStorageFolderCleanupCandidate candidate)
    {
        var row = new Grid
        {
            Style = (Style)SettingsRoot.Resources["SettingRowStyle"]
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel
        {
            Style = (Style)SettingsRoot.Resources["SettingTextPanelStyle"]
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Name,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = _localizationService.Format("Settings.ManagedStorage.ItemCount", candidate.ItemCount),
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = candidate.Path,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        });

        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };

        string folderPath = candidate.Path;
        string folderName = candidate.Name;
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.RestoreAction",
            "Settings.ManagedStorage.RestoreTooltip",
            async () => await RestoreManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.OpenAction",
            "Settings.ManagedStorage.OpenTooltip",
            async () => await OpenManagedStorageFolderAsync(folderPath)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.MoveAction",
            "Settings.ManagedStorage.MoveTooltip",
            async () => await MoveManagedStorageFolderToDesktopAsync(folderPath, folderName)));
        actionsPanel.Children.Add(CreateManagedStorageActionButton(
            "Settings.ManagedStorage.DeleteAction",
            "Settings.ManagedStorage.DeleteTooltip",
            async () => await DeleteManagedStorageFolderAsync(folderPath, folderName)));

        row.Children.Add(textPanel);
        row.Children.Add(actionsPanel);
        Grid.SetColumn(actionsPanel, 1);

        return row;
    }

    private Button CreateManagedStorageActionButton(string textKey, string tooltipKey, Func<Task> action)
    {
        var button = new Button
        {
            Style = (Style)SettingsRoot.Resources["CompactTextActionButtonStyle"],
            Content = _localizationService.T(textKey)
        };
        ToolTipService.SetToolTip(button, _localizationService.T(tooltipKey));
        button.Click += async (_, _) => await action();
        return button;
    }

    private Border CreateSettingDivider()
    {
        return new Border
        {
            Style = (Style)SettingsRoot.Resources["SettingDividerStyle"]
        };
    }

    private async Task RestoreManagedStorageFolderAsync(string folderPath)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        try
        {
            int restoredCount = await App.Current.WidgetManager.RestoreOrphanManagedStorageFoldersAsync([folderPath]);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.RestoreCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.RestoreCompleteBody", restoredCount));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task OpenManagedStorageFolderAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.T("Settings.ManagedStorage.MissingFolder"));
            return;
        }

        Win32Helper.OpenFile(folderPath);
    }

    private async Task MoveManagedStorageFolderToDesktopAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.MoveConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveConfirmBody", folderName),
                _localizationService.T("Common.Move")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.MoveCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.MoveCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task DeleteManagedStorageFolderAsync(string folderPath, string folderName)
    {
        if (App.Current.WidgetManager is null ||
            !await ConfirmManagedStorageActionAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteConfirmTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteConfirmBody", folderName),
                _localizationService.T("Common.Delete")))
        {
            return;
        }

        try
        {
            await App.Current.WidgetManager.DeleteOrphanManagedStorageFolderAsync(folderPath);
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.DeleteCompleteTitle"),
                _localizationService.Format("Settings.ManagedStorage.DeleteCompleteBody", folderName));
        }
        catch (Exception ex)
        {
            RefreshManagedStorageFolderList();
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.ManagedStorage.ActionFailedTitle"),
                _localizationService.Format("Settings.ManagedStorage.ActionFailedBody", ex.Message));
        }
    }

    private async Task<bool> ConfirmManagedStorageActionAsync(string title, string message, string primaryButtonText)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            CloseButtonText = _localizationService.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
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
        AppTitleBar.Padding = width < NarrowTitleThreshold
            ? new Thickness(12, 0, 88, 0)
            : new Thickness(18, 0, 128, 0);
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
}
