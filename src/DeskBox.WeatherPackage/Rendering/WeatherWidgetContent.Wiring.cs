using DeskBox.WeatherPackage.Services;
using DeskBox.WeatherPackage.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.WeatherPackage.Rendering;

public sealed partial class WeatherWidgetContent
{
    private UserControl _surface = null!;
    private Grid RootGrid = null!;
    private Border RichBackdrop = null!, RichGlossOverlay = null!;
    private Grid LoadingOverlay = null!;
    private GradientStop RichBackdropTop = null!, RichBackdropBottom = null!;
    private RadioButtons WeatherViewSegmented = null!;
    private ScrollViewer ExpandedHourlyScroll = null!, ExpandedWeekScroll = null!;
    private ItemsControl ExpandedHourlyItems = null!, ExpandedDailyItems = null!;
    private readonly List<Button> _buttons = [];
    private bool _disposed;

    private void InitializeView(string packageRoot)
    {
        _surface = (UserControl)XamlReader.Load(File.ReadAllText(Path.Combine(packageRoot, "weather.xaml")));
        Content = _surface;
        RootGrid = Find<Grid>("RootGrid");
        RichBackdrop = Find<Border>("RichBackdrop");
        RichGlossOverlay = Find<Border>("RichGlossOverlay");
        LoadingOverlay = Find<Grid>("LoadingOverlay");
        RichBackdropTop = Find<GradientStop>("RichBackdropTop");
        RichBackdropBottom = Find<GradientStop>("RichBackdropBottom");
        WeatherViewSegmented = Find<RadioButtons>("WeatherViewSegmented");
        ExpandedHourlyScroll = Find<ScrollViewer>("ExpandedHourlyScroll");
        ExpandedWeekScroll = Find<ScrollViewer>("ExpandedWeekScroll");
        ExpandedHourlyItems = Find<ItemsControl>("ExpandedHourlyItems");
        ExpandedDailyItems = Find<ItemsControl>("ExpandedDailyItems");
        // Runtime text Binding cannot project the package CLR's object array
        // through ICustomProperty into the host CLR. Add individual bindable
        // items through WinUI's native IVector instead; templates stay native.
        ExpandedHourlyItems.ItemsSource = null;
        ExpandedDailyItems.ItemsSource = null;
        WeatherViewSegmented.SelectionChanged += WeatherViewSegmented_SelectionChanged;
        SizeChanged += UserControl_SizeChanged;
        Loaded += WireLoadedButtons;
        ExpandedHourlyScroll.PointerWheelChanged += HourlyScroll_PointerWheelChanged;
        ExpandedWeekScroll.PointerWheelChanged += WeekScroll_PointerWheelChanged;
        foreach (var scroll in new[] { ExpandedHourlyScroll, ExpandedWeekScroll })
        {
            scroll.PointerPressed += ForecastScroll_PointerPressed;
            scroll.PointerMoved += ForecastScroll_PointerMoved;
            scroll.PointerReleased += ForecastScroll_PointerReleased;
            scroll.PointerCaptureLost += ForecastScroll_PointerCaptureLost;
        }
    }

    private T Find<T>(string name) where T : class => _surface.FindName(name) as T
        ?? throw new InvalidOperationException($"Weather view is missing {name} ({typeof(T).Name}).");

    private void WireLoadedButtons(object sender, RoutedEventArgs args)
    {
        foreach (var button in _buttons) button.Click -= RefreshButton_Click;
        _buttons.Clear();
        Visit(RootGrid);
        void Visit(DependencyObject parent)
        {
            if (parent is Button b && b.Content is FontIcon { Glyph: "\uE72C" })
            {
                b.Click += RefreshButton_Click;
                _buttons.Add(b);
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Visit(VisualTreeHelper.GetChild(parent, i));
        }
    }

    internal void ApplyEnvironment()
    {
        UpdateRichSkinTextTheme();
        UpdateWeatherPalette();
        ApplyRichSkinCornerRadius();
        RefreshWeatherTextScaleFactor();
        ApplySegmentedAccent();
        UpdateRefreshRotation();
    }

    private void RefreshForecastItems(bool hourly)
    {
        ItemsControl target = hourly ? ExpandedHourlyItems : ExpandedDailyItems;
        target.Items.Clear();
        if (hourly) foreach (var item in _viewModel.HourlyForecast) target.Items.Add(item);
        else foreach (var item in _viewModel.DailyForecast) target.Items.Add(item);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _refreshRotationStoryboard?.Stop(); } catch { }
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Loaded -= WeatherWidgetContent_Loaded;
        Loaded -= WireLoadedButtons;
        Unloaded -= WeatherWidgetContent_Unloaded;
        ActualThemeChanged -= WeatherWidgetContent_ActualThemeChanged;
        SizeChanged -= UserControl_SizeChanged;
        WeatherViewSegmented.SelectionChanged -= WeatherViewSegmented_SelectionChanged;
        foreach (var button in _buttons) button.Click -= RefreshButton_Click;
        _buttons.Clear();
        ExpandedHourlyScroll.PointerWheelChanged -= HourlyScroll_PointerWheelChanged;
        ExpandedWeekScroll.PointerWheelChanged -= WeekScroll_PointerWheelChanged;
        foreach (var scroll in new[] { ExpandedHourlyScroll, ExpandedWeekScroll })
        {
            scroll.PointerPressed -= ForecastScroll_PointerPressed;
            scroll.PointerMoved -= ForecastScroll_PointerMoved;
            scroll.PointerReleased -= ForecastScroll_PointerReleased;
            scroll.PointerCaptureLost -= ForecastScroll_PointerCaptureLost;
            scroll.ReleasePointerCaptures();
        }
    }
}
