using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Weather widget content view. Adapts its layout based on the available size
/// (Mini / Compact / Expanded) and supports switching between Today and Week views.
/// </summary>
public sealed partial class WeatherWidgetContent : UserControl
{
    private readonly WeatherWidgetViewModel _viewModel;
    private Storyboard? _rainStoryboard;
    private Storyboard? _snowStoryboard;
    private Storyboard? _thunderStoryboard;
    private Storyboard? _clearShimmerStoryboard;
    private Storyboard? _refreshRotationStoryboard;
    private bool _animationsInitialized;

    // Track fall animations so their To-value can be updated when the control resizes.
    private readonly List<DoubleAnimation> _rainFallAnims = [];
    private readonly List<DoubleAnimation> _snowFallAnims = [];
    private double _animationFallHeight = 400;

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

        // Collect all refresh icon FontIcons after template is applied
        FindRefreshIcons();
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

    private void WeatherWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        _isViewLoaded = true;
        FindRefreshIcons();

        // Initialize fall height from actual size before creating animations.
        if (ActualHeight > 0)
        {
            _animationFallHeight = Math.Max(100, ActualHeight + 20);
        }

        InitializeAnimations();
        InitializeRefreshRotation();
        UpdateAnimations();
        UpdateRichSkinTextTheme();

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
        StopAllAnimations();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WeatherWidgetViewModel.RainAnimationVisibility) or
            nameof(WeatherWidgetViewModel.SnowAnimationVisibility) or
            nameof(WeatherWidgetViewModel.ThunderAnimationVisibility) or
            nameof(WeatherWidgetViewModel.ClearAnimationVisibility) or
            nameof(WeatherWidgetViewModel.IsWidgetActive))
        {
            UpdateAnimations();
        }
        else if (e.PropertyName is nameof(WeatherWidgetViewModel.RichBackdropTopColor) or
                 nameof(WeatherWidgetViewModel.RichBackdropBottomColor))
        {
            UpdateRichSkinColors();
        }
        else if (e.PropertyName == nameof(WeatherWidgetViewModel.RichSkinUsesLightText))
        {
            UpdateRichSkinTextTheme();
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

    private void UpdateRichSkinTextTheme()
    {
        // When the rich skin background is dark (night, storms, etc.),
        // force the RootGrid to Dark theme so all ThemeResource text brushes
        // resolve to light colors — even when the app is in Light mode.
        RootGrid.RequestedTheme = _viewModel.RichSkinUsesLightText
            ? ElementTheme.Dark
            : ElementTheme.Default;
    }

    private void InitializeAnimations()
    {
        if (_animationsInitialized)
        {
            return;
        }

        _animationsInitialized = true;

        // Rain animation: falling rain drops
        _rainStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        for (int i = 0; i < 8; i++)
        {
            var drop = new TextBlock
            {
                Text = "\u2502",
                FontSize = 10 + (i % 3),
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0xD0, 0xFF)),
                Opacity = 0.4 + (i % 3) * 0.15
            };
            Canvas.SetLeft(drop, 20 + i * 25);
            RainAnimationCanvas.Children.Add(drop);

            var anim = new DoubleAnimation
            {
                From = -20,
                To = _animationFallHeight,
                Duration = new Duration(TimeSpan.FromSeconds(0.6 + (i % 4) * 0.15)),
                BeginTime = TimeSpan.FromSeconds(i * 0.12),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim, drop);
            Storyboard.SetTargetProperty(anim, "(Canvas.Top)");
            _rainStoryboard.Children.Add(anim);
            _rainFallAnims.Add(anim);
        }

        // Snow animation: falling snowflakes
        _snowStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        for (int i = 0; i < 6; i++)
        {
            var flake = new TextBlock
            {
                Text = "\u2744",
                FontSize = 10 + (i % 3) * 2,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
                Opacity = 0.5 + (i % 3) * 0.15
            };
            Canvas.SetLeft(flake, 15 + i * 30);
            SnowAnimationCanvas.Children.Add(flake);

            var anim = new DoubleAnimation
            {
                From = -20,
                To = _animationFallHeight,
                Duration = new Duration(TimeSpan.FromSeconds(3 + (i % 3) * 0.8)),
                BeginTime = TimeSpan.FromSeconds(i * 0.5),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(anim, flake);
            Storyboard.SetTargetProperty(anim, "(Canvas.Top)");
            _snowStoryboard.Children.Add(anim);
            _snowFallAnims.Add(anim);
        }

        // Thunder flash animation
        _thunderStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var flashAnim = new ColorAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.1)), Value = Color.FromArgb(0x30, 0x80, 0xD0, 0xFF) });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.3)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.1)), Value = Color.FromArgb(0x40, 0x80, 0xD0, 0xFF) });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.25)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.35)), Value = Color.FromArgb(0x30, 0x80, 0xD0, 0xFF) });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.5)), Value = Colors.Transparent });
        flashAnim.KeyFrames.Add(new LinearColorKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8)), Value = Colors.Transparent });
        Storyboard.SetTarget(flashAnim, ThunderFlashBrush);
        Storyboard.SetTargetProperty(flashAnim, "Color");
        _thunderStoryboard.Children.Add(flashAnim);

        // Clear sky shimmer: a soft glowing orb that pulses
        _clearShimmerStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var shimmerOrb = new Ellipse
        {
            Width = 80,
            Height = 80,
            Opacity = 0.1
        };
        var radialBrush = new RadialGradientBrush
        {
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        radialBrush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0x40, 0xFF, 0xD7, 0x00) });
        radialBrush.GradientStops.Add(new GradientStop { Offset = 1, Color = Colors.Transparent });
        shimmerOrb.Fill = radialBrush;
        Canvas.SetLeft(shimmerOrb, -20);
        Canvas.SetTop(shimmerOrb, -20);
        ClearShimmerCanvas.Children.Add(shimmerOrb);

        var shimmerAnim = new DoubleAnimation
        {
            From = 0.05,
            To = 0.15,
            Duration = new Duration(TimeSpan.FromSeconds(3)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(shimmerAnim, shimmerOrb);
        Storyboard.SetTargetProperty(shimmerAnim, "Opacity");
        _clearShimmerStoryboard.Children.Add(shimmerAnim);
    }

    private void UpdateAnimations()
    {
        if (!_animationsInitialized || !_isViewLoaded)
        {
            return;
        }

        // Pause all animations when the widget is not active (hidden, deactivated, or behind other windows)
        bool shouldRun = _viewModel.IsWidgetActive;

        // Rain
        if (shouldRun && _viewModel.RainAnimationVisibility == Visibility.Visible)
        {
            try { _rainStoryboard?.Begin(); } catch { }
        }
        else
        {
            try { _rainStoryboard?.Stop(); } catch { }
        }

        // Snow
        if (shouldRun && _viewModel.SnowAnimationVisibility == Visibility.Visible)
        {
            try { _snowStoryboard?.Begin(); } catch { }
        }
        else
        {
            try { _snowStoryboard?.Stop(); } catch { }
        }

        // Thunder
        if (shouldRun && _viewModel.ThunderAnimationVisibility == Visibility.Visible)
        {
            try { _thunderStoryboard?.Begin(); } catch { }
        }
        else
        {
            try { _thunderStoryboard?.Stop(); } catch { }
        }

        // Clear shimmer
        if (shouldRun && _viewModel.ClearAnimationVisibility == Visibility.Visible)
        {
            try { _clearShimmerStoryboard?.Begin(); } catch { }
        }
        else
        {
            try { _clearShimmerStoryboard?.Stop(); } catch { }
        }
    }

    private void StopAllAnimations()
    {
        try { _rainStoryboard?.Stop(); } catch { }
        try { _snowStoryboard?.Stop(); } catch { }
        try { _thunderStoryboard?.Stop(); } catch { }
        try { _clearShimmerStoryboard?.Stop(); } catch { }
        try { _refreshRotationStoryboard?.Stop(); } catch { }
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.UpdateAvailableSize(e.NewSize.Width, e.NewSize.Height);

        // Update the fall distance for rain/snow animations to match the new height.
        // The animations will pick up the new To-value on their next repeat cycle.
        double newFallHeight = Math.Max(100, e.NewSize.Height + 20);
        if (Math.Abs(newFallHeight - _animationFallHeight) > 1)
        {
            _animationFallHeight = newFallHeight;
            foreach (var anim in _rainFallAnims)
            {
                anim.To = newFallHeight;
            }
            foreach (var anim in _snowFallAnims)
            {
                anim.To = newFallHeight;
            }
        }
    }

    private void ViewSwitch_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ToggleViewMode();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.RefreshAsync(userTriggered: true);
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

        if (_viewModel.IsRefreshing)
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
}

/// <summary>
/// Converts a WeatherDayViewModel to a Thickness for the temperature range bar.
/// Left = TempBarOffset * BarWidth, Right = (1 - TempBarOffset - TempBarWidth) * BarWidth.
/// </summary>
internal sealed class TempBarMarginConverter : IValueConverter
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
/// Converts a boolean IsCurrentHour to a background brush for hourly card highlight.
/// Current hour gets a slightly brighter translucent white; others get a very faint white.
/// </summary>
internal sealed class CurrentHourBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isCurrent = value is true;
        return isCurrent
            ? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to Visibility. Set Invert to reverse the logic
/// (true -> Collapsed, false -> Visible).
/// </summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
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
