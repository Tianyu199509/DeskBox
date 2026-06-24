using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow : Window
{
    private const int DefaultWindowWidth = 920;
    private const int DefaultWindowHeight = 760;
    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;
    private const double ContentMaxWidth = 720;
    private const double PageSidePadding = 20;
    private const double RowStackContentThreshold = 620;
    private const double NarrowTitleThreshold = 560;
    private const double NavigationCompactThreshold = 900;
    private const string DownloadLink = "https://pan.quark.cn/s/f7a6769cdaf3";
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
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
    private bool _isSubclassInstalled;
    private bool _isAppearanceSliderDragging;
    private bool _keepTopMostUntilDeactivate;
    private bool _isRecordingHotkey;
    private bool _isApplyingQuickCaptureClipboardToggle;
    private string _currentSettingsSection = "General";

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsService settingsService, ThemeService themeService, LocalizationService localizationService)
    {
        _themeService = themeService;
        _localizationService = localizationService;
        ViewModel = new SettingsViewModel(settingsService, themeService, localizationService);
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
            ViewModel.RefreshQuickAccessState();
            ViewModel.RefreshGlobalHotkeyState();
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
            RefreshGlobalHotkeyControls();
            UpdateResponsiveLayout(GetWindowWidth());
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
        _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (_appWindow.Size.Width < workArea.Width ? (workArea.Width - _appWindow.Size.Width) / 2 : 0)),
            workArea.Y + Math.Max(0, (_appWindow.Size.Height < workArea.Height ? (workArea.Height - _appWindow.Size.Height) / 2 : 0))));

        _themeService.TrackWindow(this);
        _themeService.AppearanceChanged += OnAppearanceChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
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

        Activated += SettingsWindow_Activated;
        Closed += (_, _) =>
        {
            _resizeSettleTimer.Stop();
            _resizeSettleTimer.Tick -= ResizeSettleTimer_Tick;
            Win32Helper.ClearWindowTopMost(_hWnd);
            RemoveMinimumSizeHook();
            _themeService.AppearanceChanged -= OnAppearanceChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
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

    public void ActivateFromTray()
    {
        _keepTopMostUntilDeactivate = true;
        Win32Helper.SetWindowTopMost(_hWnd);
        Activate();

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (_keepTopMostUntilDeactivate)
            {
                Win32Helper.SetWindowTopMost(_hWnd);
            }
        });
    }

    private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated ||
            !_keepTopMostUntilDeactivate)
        {
            return;
        }

        _keepTopMostUntilDeactivate = false;
        Win32Helper.ClearWindowTopMost(_hWnd);
    }

    private void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string sectionTag })
        {
            ShowSettingsSection(sectionTag, isNestedSection: false);
        }
    }

    private void SettingsNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        switch (_currentSettingsSection)
        {
            case "AppearanceDetail":
                ShowSettingsSection("Appearance", isNestedSection: false);
                SettingsNavigationView.SelectedItem = FindNavItemByTag("Appearance");
                break;
            case "ManagedStorage":
                ShowSettingsSection("Advanced", isNestedSection: false);
                SettingsNavigationView.SelectedItem = FindNavItemByTag("Advanced");
                break;
            case "QuickCaptureSettings":
                ShowSettingsSection("FeatureWidgets", isNestedSection: false);
                SettingsNavigationView.SelectedItem = FeatureWidgetsNavItem;
                break;
            default:
                ShowSettingsSection("FeatureWidgets", isNestedSection: false);
                SettingsNavigationView.SelectedItem = FeatureWidgetsNavItem;
                break;
        }
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

    private void ShowSettingsSection(string sectionTag, bool isNestedSection = false)
    {
        _currentSettingsSection = sectionTag;
        AppearanceSection.Visibility = sectionTag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceDetailSection.Visibility = sectionTag == "AppearanceDetail" ? Visibility.Visible : Visibility.Collapsed;
        FeatureWidgetsSection.Visibility = sectionTag == "FeatureWidgets" ? Visibility.Visible : Visibility.Collapsed;
        QuickCaptureSettingsSection.Visibility = sectionTag == "QuickCaptureSettings" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "QuickCaptureSettings")
        {
            ViewModel.RefreshQuickCaptureClipboardDiagnostics();
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        AdvancedSection.Visibility = sectionTag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
        ManagedStorageSection.Visibility = sectionTag == "ManagedStorage" ? Visibility.Visible : Visibility.Collapsed;
        if (sectionTag == "ManagedStorage")
        {
            RefreshManagedStorageFolderList();
        }
        GeneralSection.Visibility = sectionTag == "General" ? Visibility.Visible : Visibility.Collapsed;
        MaintenanceSection.Visibility = sectionTag == "Maintenance" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = sectionTag == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsNavigationView.IsBackButtonVisible = isNestedSection
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;

        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            CollectResponsiveRows(SettingsRoot);
            UpdateResponsiveLayout(GetWindowWidth());
        });
    }

    private async void AccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditCustomAccent || SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var picker = new ColorPicker
        {
            Color = ViewModel.GetCurrentAccentColor(),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true,
            IsColorSliderVisible = true,
            IsColorSpectrumVisible = true,
            IsHexInputVisible = true,
            MinWidth = 320
        };

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Dialog.AccentTitle"),
            PrimaryButtonText = _localizationService.T("Common.Ok"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = picker
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SetCustomAccentColor(picker.Color);
        }
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
            case "WidgetAnimationEffect":
                ViewModel.SelectedWidgetAnimationEffect = ViewModel.AvailableWidgetAnimationEffects[combo.SelectedIndex];
                break;
            case "WidgetAnimationSpeed":
                ViewModel.SelectedWidgetAnimationSpeed = ViewModel.AvailableWidgetAnimationSpeeds[combo.SelectedIndex];
                break;
            case "ManagedDropAction":
                ViewModel.SelectedManagedDropAction = ViewModel.AvailableManagedDropActions[combo.SelectedIndex];
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

            case "ManagedDropAction":
                selectedValue = ViewModel.SelectedManagedDropAction;
                values = ViewModel.AvailableManagedDropActions;
                applyValue = value => ViewModel.SelectedManagedDropAction = value;
                displayValue = ViewModel.GetManagedDropActionDisplayName;
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

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        Title = _localizationService.T("Settings.WindowTitle");
        Localized.RefreshAll(_localizationService);
        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
        if (string.Equals(_currentSettingsSection, "ManagedStorage", StringComparison.Ordinal))
        {
            RefreshManagedStorageFolderList();
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
        string path = ViewModel.ManagedStorageRootPath;
        bool shouldUnpin = ViewModel.ShouldUnpinManagedStorageFromQuickAccess;
        string? error;
        bool succeeded;
        if (shouldUnpin)
        {
            succeeded = ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccess(path, out error);
        }
        else
        {
            succeeded = ExplorerQuickAccessHelper.TryPinFolderToQuickAccess(path, out error);
        }

        ViewModel.RefreshQuickAccessState();

        if (succeeded)
        {
            await Task.Delay(350);
            ViewModel.RefreshQuickAccessState();
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
                    ? _localizationService.Format("Settings.Dialog.UnpinQuickAccessFailedBody", error ?? string.Empty)
                    : _localizationService.Format("Settings.Dialog.PinQuickAccessFailedBody", error ?? string.Empty),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OpenSourceRepositoryUrl);
    }

    private async void CopyDownloadLinkButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(DownloadLink);
        DeskBoxClipboardWriteScope.MarkWrite(text: DownloadLink);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        if (SettingsRoot.XamlRoot is not null)
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Download.CopiedTitle"),
                DownloadLink);
        }
    }

    private async void OpenDownloadLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(DownloadLink, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private void OpenQuickCaptureSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("QuickCaptureSettings", isNestedSection: true);
    }

    private void OpenAppearanceDetailButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("AppearanceDetail", isNestedSection: true);
    }

    private void AppearanceBreadcrumbBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("Appearance", isNestedSection: false);
        SettingsNavigationView.SelectedItem = FindNavItemByTag("Appearance");
    }

    private void QuickCaptureBreadcrumbBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("FeatureWidgets", isNestedSection: false);
        SettingsNavigationView.SelectedItem = FeatureWidgetsNavItem;
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

    private async void QuickCaptureClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingQuickCaptureClipboardToggle ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == ViewModel.QuickCaptureClipboardEnabled)
        {
            return;
        }

        if (!toggle.IsOn)
        {
            App.Log("[QuickCaptureClipboard] Disabled from settings");
            ViewModel.QuickCaptureClipboardEnabled = false;
            App.Current.QuickCaptureClipboardService?.Refresh();
            ViewModel.RefreshQuickCaptureClipboardDiagnostics();
            return;
        }

        if (!App.Current.SettingsService.Settings.HasConfirmedQuickCaptureClipboardNotice)
        {
            bool confirmed = await QuickCaptureClipboardActivationHelper.EnableAsync(SettingsRoot.XamlRoot, _localizationService);
            if (!confirmed)
            {
                SetQuickCaptureClipboardToggle(false);
                return;
            }
        }
        else
        {
            await QuickCaptureClipboardActivationHelper.EnableAsync(SettingsRoot.XamlRoot, _localizationService);
        }

        ViewModel.QuickCaptureClipboardEnabled = App.Current.SettingsService.Settings.QuickCaptureClipboardEnabled;
        ViewModel.QuickCaptureEnabled = App.Current.SettingsService.Settings.QuickCaptureEnabled;
        App.Current.QuickCaptureClipboardService?.Refresh();
        ViewModel.RefreshQuickCaptureClipboardDiagnostics();
    }

    private void SetQuickCaptureClipboardToggle(bool isOn)
    {
        _isApplyingQuickCaptureClipboardToggle = true;
        try
        {
            QuickCaptureClipboardToggle.IsOn = isOn;
        }
        finally
        {
            _isApplyingQuickCaptureClipboardToggle = false;
        }
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
            Spacing = 12
        };

        content.Children.Add(CreateDialogParagraph(
            _localizationService.T("Settings.Dialog.ProductReasonP1")));
        content.Children.Add(CreateDialogParagraph(
            _localizationService.T("Settings.Dialog.ProductReasonP2")));
        content.Children.Add(CreateDialogParagraph(
            _localizationService.T("Settings.Dialog.ProductReasonP3")));

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
        ShowSettingsSection("ManagedStorage", isNestedSection: true);
    }

    private void ManagedStorageBreadcrumbBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsSection("Advanced", isNestedSection: false);
        SettingsNavigationView.SelectedItem = FindNavItemByTag("Advanced");
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
            LineHeight = 22
        };
    }

    private void OnAppearanceChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTitleBarButtonColors();
            return;
        }

        DispatcherQueue.TryEnqueue(ApplyTitleBarButtonColors);
    }

    private void ApplyTitleBarButtonColors()
    {
        bool isDark = _themeService.CurrentTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };

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
            : _appWindow.Size.Width;
    }

    private void UpdateResponsiveLayout(double width, bool preferMeasuredContentWidth = true)
    {
        bool useCompactNavigation = width < NavigationCompactThreshold;

        SettingsNavigationView.PaneDisplayMode = useCompactNavigation
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        SettingsNavigationView.IsPaneOpen = !useCompactNavigation;

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
        DownloadLinkActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        DownloadLinkActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        CopyDownloadLinkButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        OpenDownloadLinkButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
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
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, MinWindowWidth);
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, MinWindowHeight);
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
}
