using DeskBox.ViewModels;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Weather widget content view. Adapts its layout based on the available size
/// (Mini / Compact / Expanded) and supports switching between Today and Week views.
/// </summary>
public sealed partial class WeatherWidgetContent : UserControl
{
    private readonly WeatherWidgetViewModel _viewModel;
    private Storyboard? _refreshRotationStoryboard;

    // Track the refresh icon elements across all layouts for rotation animation
    private readonly List<FrameworkElement> _refreshIcons = [];

    // Drag-to-scroll state for the forecast ScrollViewers (hourly horizontal / week vertical)
    private bool _forecastDragging;
    private Windows.Foundation.Point _forecastDragStart;
    private double _forecastDragStartHOffset;
    private double _forecastDragStartVOffset;

    public WeatherWidgetContent(WeatherWidgetViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += WeatherWidgetContent_Loaded;
        Unloaded += WeatherWidgetContent_Unloaded;
        ActualThemeChanged += WeatherWidgetContent_ActualThemeChanged;

        // Collect all refresh icon FontIcons after template is applied
        FindRefreshIcons();
    }

    private void WeatherWidgetContent_ActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateRichSkinTextTheme();
        UpdateWeatherPalette();
        ApplySegmentedAccent();
    }

    private void FindRefreshIcons()
    {
        // The refresh buttons contain FontIcon children with glyph E72C
        // We find them by traversing the visual tree after load
        _refreshIcons.Clear();
        FindRefreshIconsRecursive(RootGrid);
    }

    private void FindRefreshIconsRecursive(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FontIcon icon)
            {
                var glyph = icon.Glyph;
                if (glyph == "\uE72C")
                {
                    _refreshIcons.Add(icon);
                }
            }
            FindRefreshIconsRecursive(child);
        }
    }

    private bool _isViewLoaded;
    private bool _isSynchronizingViewSelection;

    private void WeatherWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        _isViewLoaded = true;
        FindRefreshIcons();

        InitializeRefreshRotation();
        UpdateRichSkinTextTheme();
        ApplyRichSkinCornerRadius();
        UpdateWeatherPalette();
        UpdateWeatherViewSelection();
        App.Current.ThemeService.AppearanceChanged -= OnThemeAppearanceChanged;
        App.Current.ThemeService.AppearanceChanged += OnThemeAppearanceChanged;
        ApplySegmentedAccent();

        // Ensure the layout mode reflects the actual control size.
        // SizeChanged may fire with 0x0 before the control is fully laid out.
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _viewModel.UpdateAvailableSize(ActualWidth, ActualHeight);
        }
    }

    private void WeatherWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        _isViewLoaded = false;
        App.Current.ThemeService.AppearanceChanged -= OnThemeAppearanceChanged;
        try { _refreshRotationStoryboard?.Stop(); } catch { }
    }

    private void OnThemeAppearanceChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(OnThemeAppearanceChanged);
            return;
        }

        ApplySegmentedAccent();
    }

    private void ApplySegmentedAccent()
    {
        AccentResourceScope.Apply(
            WeatherViewSegmented,
            App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor);
    }

    private void UpdateWeatherPalette()
    {
        bool usesRichSkin = _viewModel.RichSkinVisibility == Visibility.Visible;
        bool usesLightText = _viewModel.RichSkinUsesLightText;
        bool usesDarkTheme = RootGrid.ActualTheme == ElementTheme.Dark;

        Color interactionColor = usesRichSkin
            ? usesLightText
                ? Color.FromArgb(0xFF, 0x8B, 0xD3, 0xFF)
                : Color.FromArgb(0xFF, 0x16, 0x5A, 0x8C)
            : usesDarkTheme
                ? Color.FromArgb(0xFF, 0x6C, 0xB8, 0xF0)
                : Color.FromArgb(0xFF, 0x1F, 0x6F, 0xAD);
        Color daylightColor = usesRichSkin
            ? usesLightText
                ? Color.FromArgb(0xFF, 0xFF, 0xD0, 0x8A)
                : Color.FromArgb(0xFF, 0x8A, 0x4F, 0x00)
            : usesDarkTheme
                ? Color.FromArgb(0xFF, 0xF2, 0xBC, 0x66)
                : Color.FromArgb(0xFF, 0xA8, 0x5F, 0x00);

        SetBrushColor("WeatherAccentBrush", interactionColor);
        SetBrushColor("WeatherDaylightBrush", daylightColor);
    }

    private void SetBrushColor(string resourceKey, Color color)
    {
        if (Resources.TryGetValue(resourceKey, out object? resource) &&
            resource is SolidColorBrush brush)
        {
            brush.Color = color;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WeatherWidgetViewModel.RichBackdropTopColor) or
            nameof(WeatherWidgetViewModel.RichBackdropBottomColor))
        {
            UpdateRichSkinColors();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.RichSkinUsesLightText))
        {
            UpdateRichSkinTextTheme();
            UpdateWeatherPalette();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.RichSkinVisibility))
        {
            UpdateRichSkinTextTheme();
            UpdateWeatherPalette();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.WidgetCornerRadius))
        {
            ApplyRichSkinCornerRadius();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.IsWeekView))
        {
            UpdateWeatherViewSelection();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.IsRefreshing))
        {
            UpdateRefreshRotation();
        }
    }

    private void UpdateRichSkinColors()
    {
        // Sync the gradient stop colors from the ViewModel
        RichBackdropTop.Color = _viewModel.RichBackdropTopColor;
        RichBackdropBottom.Color = _viewModel.RichBackdropBottomColor;
    }

    private void ApplyRichSkinCornerRadius()
    {
        CornerRadius cornerRadius = _viewModel.WidgetCornerRadius;
        RichBackdrop.CornerRadius = cornerRadius;
        LoadingOverlay.CornerRadius = cornerRadius;

        double radius = Math.Max(0, cornerRadius.TopLeft);
        RichGlossOverlay.CornerRadius = new CornerRadius(radius, radius, 0, 0);
    }

    private void UpdateRichSkinTextTheme()
    {
        // Standard follows the app theme. Rich follows the gradient contrast,
        // independently of the app theme, so a dark weather backdrop never
        // receives dark text (and vice versa).
        RootGrid.RequestedTheme = !_viewModel.UsesRichSkin
            ? ElementTheme.Default
            : _viewModel.RichSkinUsesLightText
                ? ElementTheme.Dark
                : ElementTheme.Light;
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.UpdateAvailableSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void WeatherViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isViewLoaded &&
            !_isSynchronizingViewSelection &&
            DataContext is WeatherWidgetViewModel viewModel &&
            WeatherViewSegmented.SelectedIndex >= 0)
        {
            viewModel.SetViewMode(useWeekView: WeatherViewSegmented.SelectedIndex == 1);
        }
    }

    private void UpdateWeatherViewSelection()
    {
        int selectedIndex = _viewModel.IsWeekView ? 1 : 0;
        if (WeatherViewSegmented.SelectedIndex != selectedIndex)
        {
            _isSynchronizingViewSelection = true;
            try
            {
                WeatherViewSegmented.SelectedIndex = selectedIndex;
            }
            finally
            {
                _isSynchronizingViewSelection = false;
            }
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            SpinRefreshIcon(btn);
        }
        _ = _viewModel.RefreshAsync(userTriggered: true);
    }

    private void SpinRefreshIcon(Button button)
    {
        // Find the FontIcon child
        if (button.Content is not FontIcon icon) return;

        // Ensure RenderTransform exists
        if (icon.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform { Angle = 0 };
            icon.RenderTransform = rotate;
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
        if (!AreSystemAnimationsEnabled())
        {
            rotate.Angle = 0;
            return;
        }

        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.SpatialMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, rotate);
        Storyboard.SetTargetProperty(anim, "Angle");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private static bool IsControlKeyDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void HourlyScroll_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            var props = e.GetCurrentPoint(sv).Properties;
            // Horizontal scroll only while Ctrl is held; plain wheel is left unhandled.
            if (IsControlKeyDown() && props.MouseWheelDelta != 0)
            {
                // Natural horizontal scroll: wheel up scrolls left, wheel down scrolls right.
                // Amplify by 2x for smoother navigation through 24 hours.
                double offset = sv.HorizontalOffset - props.MouseWheelDelta * 2;
                sv.ChangeView(offset, null, null);
                e.Handled = true;
            }
        }
    }

    private void WeekScroll_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            var props = e.GetCurrentPoint(sv).Properties;
            // Vertical scroll only while Ctrl is held; plain wheel is left unhandled.
            if (IsControlKeyDown() && props.MouseWheelDelta != 0)
            {
                double offset = sv.VerticalOffset - props.MouseWheelDelta * 2;
                sv.ChangeView(null, offset, null);
                e.Handled = true;
            }
        }
    }

    private void ForecastScroll_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            var point = e.GetCurrentPoint(sv);
            if (point.Properties.IsLeftButtonPressed)
            {
                _forecastDragging = true;
                _forecastDragStart = point.Position;
                _forecastDragStartHOffset = sv.HorizontalOffset;
                _forecastDragStartVOffset = sv.VerticalOffset;
                sv.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }
    }

    private void ForecastScroll_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_forecastDragging && sender is ScrollViewer sv)
        {
            var pos = e.GetCurrentPoint(sv).Position;
            if (ReferenceEquals(sv, ExpandedHourlyScroll))
            {
                // Horizontal drag: moving the pointer right drags content right (scrolls left).
                double delta = pos.X - _forecastDragStart.X;
                sv.ChangeView(_forecastDragStartHOffset - delta, null, null, disableAnimation: true);
            }
            else
            {
                // Vertical drag: moving the pointer down drags content down (scrolls up).
                double delta = pos.Y - _forecastDragStart.Y;
                sv.ChangeView(null, _forecastDragStartVOffset - delta, null, disableAnimation: true);
            }
        }
    }

    private void ForecastScroll_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            _forecastDragging = false;
            sv.ReleasePointerCapture(e.Pointer);
        }
    }

    private void ForecastScroll_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _forecastDragging = false;
    }

    private void InitializeRefreshRotation()
    {
        // Rebuild storyboard each time icons may have changed (layout switch)
        _refreshRotationStoryboard?.Stop();
        _refreshRotationStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        foreach (var icon in _refreshIcons)
        {
            // Ensure each icon has a RotateTransform
            if (icon.RenderTransform is not RotateTransform)
            {
                icon.RenderTransform = new RotateTransform { Angle = 0 };
                icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            }

            var rotateAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(0.8)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(rotateAnim, icon);
            Storyboard.SetTargetProperty(rotateAnim, "(UIElement.RenderTransform).(RotateTransform.Angle)");
            _refreshRotationStoryboard.Children.Add(rotateAnim);
        }
    }

    private void UpdateRefreshRotation()
    {
        if (!_isViewLoaded)
        {
            return;
        }

        if (_viewModel.IsRefreshing && AreSystemAnimationsEnabled())
        {
            // Rebuild in case layout changed and icons were recreated
            FindRefreshIcons();
            InitializeRefreshRotation();
            try { _refreshRotationStoryboard?.Begin(); } catch { }
        }
        else
        {
            try { _refreshRotationStoryboard?.Stop(); } catch { }
            foreach (var icon in _refreshIcons)
            {
                if (icon.RenderTransform is RotateTransform rt)
                {
                    rt.Angle = 0;
                }
            }
        }
    }

    private static bool AreSystemAnimationsEnabled()
    {
        return WindowsCompatibilityService.ShouldAnimate;
    }
}

/// <summary>
/// Converts a WeatherDayViewModel to a Thickness for the temperature range bar.
/// Left = TempBarOffset * BarWidth, Right = (1 - TempBarOffset - TempBarWidth) * BarWidth.
/// </summary>
internal sealed partial class TempBarMarginConverter : IValueConverter
{
    public double BarWidth { get; set; } = 80;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is WeatherDayViewModel vm)
        {
            double left = vm.TempBarOffset * BarWidth;
            double right = (1.0 - vm.TempBarOffset - vm.TempBarWidth) * BarWidth;
            if (right < 0) right = 0;
            return new Thickness(left, 0, right, 0);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to Visibility. Set Invert to reverse the logic
/// (true -> Collapsed, false -> Visible).
/// </summary>
internal sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
