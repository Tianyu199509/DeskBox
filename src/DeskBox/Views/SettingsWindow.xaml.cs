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
using System.Text;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow : Window
{
    private const int DefaultWindowWidth = 760;
    private const int DefaultWindowHeight = 760;
    private const int MinWindowWidth = 600;
    private const int MinWindowHeight = 560;
    private const double ContentMaxWidth = 720;
    private const double PageSidePadding = 20;
    private const double NarrowLayoutThreshold = 560;
    private const double NarrowTitleThreshold = 560;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr SettingsWindowSubclassId = new(1);

    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly SubclassProc _windowSubclassProc;
    private readonly List<Grid> _settingRows = [];
    private readonly List<Grid> _metricRows = [];
    private readonly HashSet<Slider> _pressedAppearanceSliders = [];
    private bool _isSubclassInstalled;
    private bool _isAppearanceSliderDragging;
    private bool _keepTopMostUntilDeactivate;

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

        ApplyTitleBarButtonColors();
        ApplyLocalizedText();
        UpdateResponsiveLayout(GetWindowWidth());

        SizeChanged += (_, args) =>
        {
            UpdateResponsiveLayout(args.Size.Width);
        };

        Activated += SettingsWindow_Activated;
        Closed += (_, _) =>
        {
            Win32Helper.ClearWindowTopMost(_hWnd);
            RemoveMinimumSizeHook();
            _themeService.AppearanceChanged -= OnAppearanceChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.Dispose();
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
                await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
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

    private void OpenRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        Win32Helper.OpenFile(ViewModel.OpenSourceRepositoryUrl);
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

    private async void CleanupManagedStorageButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null || App.Current.WidgetManager is null)
        {
            return;
        }

        var candidates = App.Current.WidgetManager.GetOrphanManagedStorageFolders();
        if (candidates.Count == 0)
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Dialog.CleanupTitle"),
                _localizationService.T("Settings.Dialog.CleanupNone"));
            return;
        }

        var optionBox = new RadioButtons
        {
            MaxWidth = 520,
            SelectedIndex = 0
        };
        optionBox.Items.Add(new TextBlock
        {
            Text = _localizationService.T("Settings.Dialog.CleanupOptionOpen"),
            TextWrapping = TextWrapping.Wrap
        });
        optionBox.Items.Add(new TextBlock
        {
            Text = _localizationService.T("Settings.Dialog.CleanupOptionMove"),
            TextWrapping = TextWrapping.Wrap
        });
        optionBox.Items.Add(new TextBlock
        {
            Text = _localizationService.T("Settings.Dialog.CleanupOptionDelete"),
            TextWrapping = TextWrapping.Wrap
        });

        var content = new StackPanel
        {
            Spacing = 12
        };
        content.Children.Add(new TextBlock
        {
            Text = _localizationService.Format("Settings.Dialog.CleanupBody", candidates.Count),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(optionBox);
        content.Children.Add(new TextBlock
        {
            Text = BuildCleanupCandidateSummary(candidates),
            FontSize = 12,
            Opacity = 0.78,
            TextWrapping = TextWrapping.WrapWholeWords
        });

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T("Settings.Dialog.CleanupTitle"),
            PrimaryButtonText = _localizationService.T("Common.Continue"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = content
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            switch (optionBox.SelectedIndex)
            {
                case 1:
                    foreach (var candidate in candidates)
                    {
                        await App.Current.WidgetManager.MoveOrphanManagedStorageFolderContentsToDesktopAsync(candidate.Path);
                    }
                    await ShowInfoDialogAsync(
                        _localizationService.T("Settings.Dialog.CleanupComplete"),
                        _localizationService.T("Settings.Dialog.CleanupMoved"));
                    break;

                case 2:
                    foreach (var candidate in candidates)
                    {
                        await App.Current.WidgetManager.DeleteOrphanManagedStorageFolderAsync(candidate.Path);
                    }
                    await ShowInfoDialogAsync(
                        _localizationService.T("Settings.Dialog.CleanupComplete"),
                        _localizationService.T("Settings.Dialog.CleanupDeleted"));
                    break;

                default:
                    Directory.CreateDirectory(ViewModel.ManagedStorageRootPath);
                    Win32Helper.OpenFile(ViewModel.ManagedStorageRootPath);
                    break;
            }
        }
        catch (Exception ex)
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.Dialog.CleanupFailed"),
                _localizationService.Format("Settings.Dialog.CleanupFailedBody", ex.Message));
        }
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

    private static TextBlock CreateDialogParagraph(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.WrapWholeWords,
            LineHeight = 22
        };
    }

    private string BuildCleanupCandidateSummary(IReadOnlyList<ManagedStorageFolderCleanupCandidate> candidates)
    {
        var builder = new StringBuilder();
        foreach (var candidate in candidates.Take(8))
        {
            builder.Append("- ");
            builder.Append(candidate.Name);
            builder.Append(" (");
            builder.Append(candidate.ItemCount);
            builder.Append(' ');
            builder.Append(_localizationService.T("Settings.Cleanup.ItemCount"));
            builder.AppendLine(")");
        }

        if (candidates.Count > 8)
        {
            builder.AppendLine(_localizationService.Format("Settings.Cleanup.MoreFolders", candidates.Count - 8));
        }

        return builder.ToString().TrimEnd();
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

    private void UpdateResponsiveLayout(double width)
    {
        bool isNarrow = width < NarrowLayoutThreshold;

        PageScroller.Padding = isNarrow
            ? new Thickness(PageSidePadding, 16, PageSidePadding, 34)
            : new Thickness(PageSidePadding, 16, PageSidePadding, 38);

        double availableContentWidth = Math.Max(0, width - PageSidePadding * 2);
        ContentHost.Width = Math.Min(ContentMaxWidth, availableContentWidth);
        ContentHost.MaxWidth = ContentMaxWidth;
        PathActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        PathActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        OpenPathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ChangePathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        CleanupStorageButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

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
        _isSubclassInstalled = SetWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        RemoveWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId);
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

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

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
