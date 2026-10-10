using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class GlanceWidgetContent : UserControl
{
    private readonly GlanceWidgetViewModel _viewModel;
    private readonly GlanceImagePaletteService _paletteService = new();
    private readonly DispatcherTimer _loadingDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _imageResizeDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _layoutResizeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _calendarMonthSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _calendarDensityRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _calendarWheelGestureTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Dictionary<CalendarViewDayItem, DateOnly> _realizedCalendarDays = [];
    private readonly SolidColorBrush _calendarSolidMaterialBrush = new();
    private readonly LinearGradientBrush _calendarImageGradientBrush = new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1)
    };
    private readonly GradientStop _calendarImageGradientStart = new() { Offset = 0 };
    private readonly GradientStop _calendarImageGradientEnd = new() { Offset = 1 };
    private Storyboard? _transitionStoryboard;
    private bool _isAActive;
    private bool _isLoaded;
    private bool _nativeCalendarConfigured;
    private bool _isSynchronizingCalendarMonth;
    private bool _isCalendarWheelGestureActive;
    private bool _isCalendarWheelNavigationInProgress;
    private int _imageLoadVersion;
    private bool _shellCustomBackgroundActive;
    private long? _calendarDisplayModeCallbackToken;
    private string? _calendarImagePalettePath;
    private GlanceImagePalette? _calendarImagePalette;
    private CancellationTokenSource? _paletteCts;
    private string? _requestedImagePath;
    private int _requestedImageDecodePixelWidth;
    private string? _decodedImagePath;
    private int _decodedImagePixelWidth;
    private ScrollViewer? _monthViewScrollViewer;
    private Button? _calendarPreviousButton;
    private Button? _calendarNextButton;
    private PointerEventHandler? _calendarPointerWheelHandler;
    private double _pendingAvailableWidth;
    private double _pendingAvailableHeight;
    private bool _hasPendingAvailableSize;

    public GlanceWidgetContent(GlanceWidgetViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _calendarImageGradientBrush.GradientStops.Add(_calendarImageGradientStart);
        _calendarImageGradientBrush.GradientStops.Add(_calendarImageGradientEnd);
        DataContext = viewModel;
        _loadingDelayTimer.Tick += LoadingDelayTimer_Tick;
        _imageResizeDelayTimer.Tick += ImageResizeDelayTimer_Tick;
        _layoutResizeTimer.Tick += LayoutResizeTimer_Tick;
        _calendarMonthSyncTimer.Tick += CalendarMonthSyncTimer_Tick;
        _calendarDensityRefreshTimer.Tick += CalendarDensityRefreshTimer_Tick;
        _calendarWheelGestureTimer.Tick += CalendarWheelGestureTimer_Tick;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _hasPendingAvailableSize = false;
        _viewModel.UpdateAvailableSize(ActualWidth, ActualHeight);
        ApplyBackgroundBrushOptions();
        ApplyBackgroundImageOpacity();
        ApplyImageAwareTheme();
        ConfigureNativeCalendarView();
        ApplyCalendarMaterial();
        QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
        BeginLoadImage(_viewModel.CurrentImagePath);
        UpdateLoadingIndicator();
    }

    /// <summary>
    /// Toggles whether a user-set widget background (applied by the hosting
    /// window) suppresses this surface's own background image.
    /// </summary>
    internal void SetShellCustomBackgroundActive(bool active)
    {
        if (_shellCustomBackgroundActive == active)
        {
            return;
        }

        _shellCustomBackgroundActive = active;
        if (_isLoaded)
        {
            if (active)
            {
                ClearBackgroundImage();
            }
            else
            {
                BeginLoadImage(_viewModel.CurrentImagePath);
            }
        }
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        CancelPaletteUpdate();
        _loadingDelayTimer.Stop();
        _imageResizeDelayTimer.Stop();
        _layoutResizeTimer.Stop();
        _hasPendingAvailableSize = false;
        _calendarMonthSyncTimer.Stop();
        _calendarDensityRefreshTimer.Stop();
        _calendarWheelGestureTimer.Stop();
        _isCalendarWheelGestureActive = false;
        UnconfigureNativeCalendarView();
        DelayedLoadingRing.IsActive = false;
        DelayedLoadingRing.Visibility = Visibility.Collapsed;
        _transitionStoryboard?.Stop();
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pendingAvailableWidth = e.NewSize.Width;
        _pendingAvailableHeight = e.NewSize.Height;
        _hasPendingAvailableSize = true;
        // CalendarView is expensive to remeasure. Keep the window resize itself responsive,
        // then apply only the latest responsive layout after the drag briefly settles.
        _layoutResizeTimer.Stop();
        _layoutResizeTimer.Start();

        QueueImageQualityRefresh();
    }

    private void LayoutResizeTimer_Tick(object? sender, object e)
    {
        _layoutResizeTimer.Stop();
        if (!_isLoaded || !_hasPendingAvailableSize)
        {
            return;
        }

        _hasPendingAvailableSize = false;
        _viewModel.UpdateAvailableSize(_pendingAvailableWidth, _pendingAvailableHeight);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        if (e.PropertyName == nameof(GlanceWidgetViewModel.CurrentImagePath))
        {
            BeginLoadImage(_viewModel.CurrentImagePath);
            QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
            ApplyImageAwareTheme();
        }
        else if (e.PropertyName is nameof(GlanceWidgetViewModel.ImageFit) or nameof(GlanceWidgetViewModel.ImageFocus))
        {
            ApplyBackgroundBrushOptions();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.BackgroundImageOpacity))
        {
            ApplyBackgroundImageOpacity();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.HasVisibleCurrentImage))
        {
            ApplyImageAwareTheme();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.IsLoading))
        {
            UpdateLoadingIndicator();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.CalendarDays))
        {
            RefreshRealizedCalendarDays();
        }
        else if (e.PropertyName is
            nameof(GlanceWidgetViewModel.CalendarDayItemMinimumHeight) or
            nameof(GlanceWidgetViewModel.ShowCalendarTraditionalDetails))
        {
            QueueCalendarDensityRefresh();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.TraditionalCalendarTitle))
        {
            UpdateTraditionalCalendarTitleVisibility();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.CalendarLanguage))
        {
            ConfigureNativeCalendarCulture();
            SetNativeCalendarDisplayDate(_viewModel.DisplayedCalendarMonth);
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.DisplayedCalendarMonth) &&
                 !_isSynchronizingCalendarMonth)
        {
            SetNativeCalendarDisplayDate(_viewModel.DisplayedCalendarMonth);
        }
        else if (e.PropertyName is
            nameof(GlanceWidgetViewModel.CalendarMaterialType) or
            nameof(GlanceWidgetViewModel.CalendarMaterialOpacity) or
            nameof(GlanceWidgetViewModel.CalendarMaterialIntensity) or
            nameof(GlanceWidgetViewModel.CalendarMaterialMode) or
            nameof(GlanceWidgetViewModel.CalendarImageMaterialTransparency))
        {
            ApplyCalendarMaterial();
            if (e.PropertyName == nameof(GlanceWidgetViewModel.CalendarMaterialMode))
            {
                QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
            }
        }
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_isLoaded)
        {
            ApplyImageAwareTheme();
            ApplyCalendarMaterial();
        }
    }

    private void ApplyImageAwareTheme()
    {
        ImageForegroundThemeScope.RequestedTheme = _viewModel.HasVisibleCurrentImage
            ? ElementTheme.Dark
            : ElementTheme.Default;
        CalendarGlassSurface.RequestedTheme = RootGrid.ActualTheme;
    }

    private void ApplyBackgroundImageOpacity()
    {
        BackgroundImageLayer.Opacity = _viewModel.BackgroundImageOpacity;
    }

    private void ApplyCalendarMaterial()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _viewModel.CalendarMaterialType;

        if (_viewModel.CalendarMaterialMode == GlanceCalendarMaterialMode.FollowImage)
        {
            var fallbackTint = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            GlanceImagePalette palette = _calendarImagePalette ?? new GlanceImagePalette(
                accentColor,
                fallbackTint);
            WidgetMaterialGradientProfile gradient =
                WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark, palette);
            _calendarImageGradientStart.Color = gradient.StartColor;
            _calendarImageGradientEnd.Color = gradient.EndColor;
            CalendarMaterialSurface.Background = _calendarImageGradientBrush;
            CalendarMaterialSurface.Opacity = 1.0 -
                _viewModel.CalendarImageMaterialTransparency;
            return;
        }

        CalendarMaterialSurface.Opacity = 1;

        if (SettingsService.IsMicaMaterial(materialType))
        {
            bool useAlt = materialType == SettingsService.WidgetMaterialTypeMicaAlt;
            _calendarSolidMaterialBrush.Color =
                WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
                    isDark,
                    accentColor,
                    useAlt,
                    _viewModel.CalendarMaterialIntensity);
            CalendarMaterialSurface.Background = _calendarSolidMaterialBrush;
            return;
        }

        if (SettingsService.IsAcrylicMaterial(materialType) &&
            Resources["GlanceCalendarAcrylicBrush"] is AcrylicBrush acrylicBrush)
        {
            WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
                isDark,
                materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                _viewModel.CalendarMaterialOpacity,
                _viewModel.CalendarMaterialIntensity);
            var tintColor = WidgetMaterialVisualCalculator.BuildContentTintColor(isDark, accentColor);
            acrylicBrush.TintColor = tintColor;
            acrylicBrush.FallbackColor = Windows.UI.Color.FromArgb(
                0xFF,
                tintColor.R,
                tintColor.G,
                tintColor.B);
            acrylicBrush.TintOpacity = profile.TintOpacity;
            acrylicBrush.TintLuminosityOpacity = profile.LuminosityOpacity;
            CalendarMaterialSurface.Background = acrylicBrush;
            return;
        }

        Windows.UI.Color surfaceColor = materialType switch
        {
            SettingsService.WidgetMaterialTypeSolid =>
                WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                    isDark,
                    accentColor,
                    _viewModel.CalendarMaterialOpacity),
            _ => WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                isDark,
                accentColor,
                _viewModel.CalendarMaterialOpacity)
        };
        _calendarSolidMaterialBrush.Color = surfaceColor;
        CalendarMaterialSurface.Background = _calendarSolidMaterialBrush;
    }

    private void ConfigureNativeCalendarView()
    {
        if (_nativeCalendarConfigured)
        {
            return;
        }

        _nativeCalendarConfigured = true;
        ConfigureNativeCalendarCulture();
        NativeCalendarView.MinDate = ToDateTimeOffset(new DateOnly(1900, 1, 1));
        NativeCalendarView.MaxDate = ToDateTimeOffset(new DateOnly(2100, 12, 31));
        _calendarPointerWheelHandler ??= NativeCalendarView_PointerWheelChanged;
        NativeCalendarView.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _calendarPointerWheelHandler,
            handledEventsToo: true);
        ConfigureMonthViewScrolling();
        _calendarDisplayModeCallbackToken = NativeCalendarView.RegisterPropertyChangedCallback(
            CalendarView.DisplayModeProperty,
            (_, _) =>
            {
                UpdateTraditionalCalendarTitleVisibility();
                if (NativeCalendarView.DisplayMode == CalendarViewDisplayMode.Month)
                {
                    QueueCalendarMonthSync();
                }
            });
        SetNativeCalendarDisplayDate(_viewModel.DisplayedCalendarMonth);
        UpdateTraditionalCalendarTitleVisibility();
    }

    private void UnconfigureNativeCalendarView()
    {
        if (_calendarPointerWheelHandler is not null)
        {
            NativeCalendarView.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                _calendarPointerWheelHandler);
        }

        if (_monthViewScrollViewer is not null)
        {
            _monthViewScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            _monthViewScrollViewer = null;
        }
        _calendarPreviousButton = null;
        _calendarNextButton = null;

        if (_calendarDisplayModeCallbackToken is long token)
        {
            NativeCalendarView.UnregisterPropertyChangedCallback(
                CalendarView.DisplayModeProperty,
                token);
            _calendarDisplayModeCallbackToken = null;
        }

        foreach (CalendarViewDayItem item in _realizedCalendarDays.Keys)
        {
            item.Tag = null;
        }

        _realizedCalendarDays.Clear();
        _nativeCalendarConfigured = false;
    }

    private void ConfigureMonthViewScrolling()
    {
        if (!_nativeCalendarConfigured)
        {
            return;
        }

        NativeCalendarView.ApplyTemplate();
        CacheNativeCalendarNavigationButtons();
        _monthViewScrollViewer = FindDescendantByName<ScrollViewer>(
            NativeCalendarView,
            "MonthViewScrollViewer");
        if (_monthViewScrollViewer is not null)
        {
            DisableFreeMonthViewScrolling(_monthViewScrollViewer);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_nativeCalendarConfigured)
            {
                return;
            }

            NativeCalendarView.ApplyTemplate();
            CacheNativeCalendarNavigationButtons();
            _monthViewScrollViewer = FindDescendantByName<ScrollViewer>(
                NativeCalendarView,
                "MonthViewScrollViewer");
            if (_monthViewScrollViewer is not null)
            {
                DisableFreeMonthViewScrolling(_monthViewScrollViewer);
            }
        });
    }

    private void CacheNativeCalendarNavigationButtons()
    {
        _calendarPreviousButton ??= FindDescendantByName<Button>(
            NativeCalendarView,
            "PreviousButton");
        _calendarNextButton ??= FindDescendantByName<Button>(
            NativeCalendarView,
            "NextButton");
    }

    private static void DisableFreeMonthViewScrolling(ScrollViewer scrollViewer)
    {
        // CalendarView defaults to optional snap points and can stop between
        // months. User wheel input is handled below as discrete month paging.
        scrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            T? nestedMatch = FindDescendantByName<T>(child, name);
            if (nestedMatch is not null)
            {
                return nestedMatch;
            }
        }

        return null;
    }

    private void ConfigureNativeCalendarCulture()
    {
        NativeCalendarView.Language = _viewModel.CalendarLanguage;
        NativeCalendarView.CalendarIdentifier =
            Windows.Globalization.CalendarIdentifiers.Gregorian;
        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(
                _viewModel.CalendarLanguage);
            NativeCalendarView.FirstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek switch
            {
                DayOfWeek.Monday => Windows.Globalization.DayOfWeek.Monday,
                DayOfWeek.Tuesday => Windows.Globalization.DayOfWeek.Tuesday,
                DayOfWeek.Wednesday => Windows.Globalization.DayOfWeek.Wednesday,
                DayOfWeek.Thursday => Windows.Globalization.DayOfWeek.Thursday,
                DayOfWeek.Friday => Windows.Globalization.DayOfWeek.Friday,
                DayOfWeek.Saturday => Windows.Globalization.DayOfWeek.Saturday,
                _ => Windows.Globalization.DayOfWeek.Sunday
            };
        }
        catch
        {
            NativeCalendarView.FirstDayOfWeek = Windows.Globalization.DayOfWeek.Sunday;
        }
    }

    private void SetNativeCalendarDisplayDate(DateOnly month)
    {
        if (!_nativeCalendarConfigured)
        {
            return;
        }

        DateOnly middleOfMonth = new DateOnly(month.Year, month.Month, 1).AddDays(14);
        NativeCalendarView.SetDisplayDate(ToDateTimeOffset(middleOfMonth));
        QueueCalendarMonthSync();
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date) =>
        new(date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local));

    private async void NativeCalendarView_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_isLoaded || NativeCalendarView.DisplayMode != CalendarViewDisplayMode.Month)
        {
            return;
        }

        int wheelDelta = e.GetCurrentPoint(NativeCalendarView).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        e.Handled = true;
        _calendarWheelGestureTimer.Stop();
        _calendarWheelGestureTimer.Start();
        if (_isCalendarWheelGestureActive || _isCalendarWheelNavigationInProgress)
        {
            return;
        }

        _isCalendarWheelGestureActive = true;
        DateOnly targetMonth = GlanceCalendarNavigationResolver.ResolveWheelTarget(
            _viewModel.DisplayedCalendarMonth,
            wheelDelta,
            new DateOnly(1900, 1, 1),
            new DateOnly(2100, 12, 1));
        if (targetMonth == _viewModel.DisplayedCalendarMonth)
        {
            return;
        }

        CacheNativeCalendarNavigationButtons();
        Button? navigationButton = wheelDelta > 0
            ? _calendarPreviousButton
            : _calendarNextButton;
        if (TryInvokeNativeCalendarNavigationButton(navigationButton))
        {
            return;
        }

        _isCalendarWheelNavigationInProgress = true;
        try
        {
            await _viewModel.SetDisplayedCalendarMonthAsync(targetMonth);
        }
        finally
        {
            _isCalendarWheelNavigationInProgress = false;
        }
    }

    private static bool TryInvokeNativeCalendarNavigationButton(Button? button)
    {
        if (button?.IsEnabled != true)
        {
            return false;
        }

        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invokeProvider)
        {
            return false;
        }

        // Reuse CalendarView's own previous/next command so wheel navigation
        // receives exactly the same month transition as clicking the buttons.
        invokeProvider.Invoke();
        return true;
    }

    private void CalendarWheelGestureTimer_Tick(object? sender, object e)
    {
        _calendarWheelGestureTimer.Stop();
        _isCalendarWheelGestureActive = false;
    }

    private void NativeCalendarView_DayItemChanging(
        CalendarView sender,
        CalendarViewDayItemChangingEventArgs args)
    {
        CalendarViewDayItem item = args.Item;
        if (args.InRecycleQueue)
        {
            _realizedCalendarDays.Remove(item);
            item.Tag = null;
            return;
        }

        DateOnly date = DateOnly.FromDateTime(item.Date.DateTime);
        _realizedCalendarDays[item] = date;
        ApplyCalendarDayDecoration(item, date, DateOnly.FromDateTime(DateTime.Today));
        if (sender.DisplayMode == CalendarViewDisplayMode.Month)
        {
            QueueCalendarMonthSync();
        }
    }

    private void ApplyCalendarDayDecoration(CalendarViewDayItem item, DateOnly date, DateOnly today)
    {
        GlanceCalendarDay? day = _viewModel.FindCalendarDay(date);
        double itemHeight = _viewModel.CalendarDayItemMinimumHeight;
        if (Math.Abs(item.MinHeight - itemHeight) >= 0.1)
        {
            item.MinHeight = itemHeight;
        }
        if (double.IsNaN(item.Height) || Math.Abs(item.Height - itemHeight) >= 0.1)
        {
            item.Height = itemHeight;
        }

        GlanceCalendarDayDecoration decoration =
            GlanceCalendarDayDecorationBuilder.Build(
                day,
                date,
                today,
                _viewModel.ShowCalendarTraditionalDetails,
                _viewModel.DisplayedCalendarMonth,
                _viewModel.CalendarLanguage);
        if (!Equals(item.Tag, decoration))
        {
            item.Tag = decoration;
        }
    }

    private void RefreshRealizedCalendarDays()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        foreach ((CalendarViewDayItem item, DateOnly date) in _realizedCalendarDays.ToArray())
        {
            ApplyCalendarDayDecoration(item, date, today);
        }

        UpdateTraditionalCalendarTitleVisibility();
    }

    private void QueueCalendarDensityRefresh()
    {
        if (!_isLoaded)
        {
            return;
        }

        _calendarDensityRefreshTimer.Stop();
        _calendarDensityRefreshTimer.Start();
    }

    private void CalendarDensityRefreshTimer_Tick(object? sender, object e)
    {
        _calendarDensityRefreshTimer.Stop();
        RefreshRealizedCalendarDays();
    }

    private void QueueCalendarMonthSync()
    {
        if (!_isLoaded || NativeCalendarView.DisplayMode != CalendarViewDisplayMode.Month)
        {
            return;
        }

        _calendarMonthSyncTimer.Stop();
        _calendarMonthSyncTimer.Start();
    }

    private async void CalendarMonthSyncTimer_Tick(object? sender, object e)
    {
        _calendarMonthSyncTimer.Stop();
        if (!_isLoaded || NativeCalendarView.DisplayMode != CalendarViewDisplayMode.Month)
        {
            return;
        }

        DateOnly[] visibleDates = _realizedCalendarDays
            .Where(pair => IsCalendarDayVisible(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        if (visibleDates.Length == 0)
        {
            return;
        }

        DateOnly displayedMonth = GlanceCalendarNavigationResolver.ResolveDisplayedMonth(
            visibleDates,
            _viewModel.DisplayedCalendarMonth);
        _isSynchronizingCalendarMonth = true;
        try
        {
            await _viewModel.SetDisplayedCalendarMonthAsync(displayedMonth);
        }
        finally
        {
            _isSynchronizingCalendarMonth = false;
        }

        if (_isLoaded)
        {
            RefreshRealizedCalendarDays();
        }
    }

    private bool IsCalendarDayVisible(CalendarViewDayItem item)
    {
        if (!item.IsLoaded || item.ActualWidth <= 0 || item.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Rect bounds = item
                .TransformToVisual(NativeCalendarView)
                .TransformBounds(new Windows.Foundation.Rect(
                    0,
                    0,
                    item.ActualWidth,
                    item.ActualHeight));
            return bounds.Right > 0 &&
                bounds.Left < NativeCalendarView.ActualWidth &&
                bounds.Bottom > 0 &&
                bounds.Top < NativeCalendarView.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateTraditionalCalendarTitleVisibility()
    {
        TraditionalCalendarTitlePresenter.Visibility =
            _isLoaded &&
            NativeCalendarView.DisplayMode == CalendarViewDisplayMode.Month &&
            _viewModel.ShowCalendarTraditionalDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void QueueCalendarImagePaletteUpdate(string? path)
    {
        if (!_isLoaded || _viewModel.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage)
        {
            return;
        }

        if (_calendarImagePalette is not null &&
            string.Equals(_calendarImagePalettePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelPaletteUpdate();
        if (string.IsNullOrWhiteSpace(path))
        {
            _calendarImagePalettePath = null;
            _calendarImagePalette = null;
            ApplyCalendarMaterial();
            return;
        }

        var paletteCts = new CancellationTokenSource();
        _paletteCts = paletteCts;
        _ = UpdateCalendarImagePaletteAsync(path, paletteCts);
    }

    private async Task UpdateCalendarImagePaletteAsync(
        string path,
        CancellationTokenSource paletteCts)
    {
        try
        {
            GlanceImagePalette? palette = await _paletteService.GetPaletteAsync(
                path,
                paletteCts.Token);
            if (paletteCts.IsCancellationRequested ||
                !_isLoaded ||
                _viewModel.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage ||
                !string.Equals(_viewModel.CurrentImagePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _calendarImagePalettePath = path;
            _calendarImagePalette = palette;
            ApplyCalendarMaterial();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_paletteCts, paletteCts))
            {
                _paletteCts = null;
            }

            paletteCts.Dispose();
        }
    }

    private void CancelPaletteUpdate()
    {
        CancellationTokenSource? paletteCts = _paletteCts;
        _paletteCts = null;
        if (paletteCts is null)
        {
            return;
        }

        try
        {
            paletteCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void BeginLoadImage(string? path, bool allowTransition = true)
    {
        // A user-set widget background wins over this surface's own
        // background image (per-widget customization rule): yield by keeping
        // the layer clear; removing the override restores it.
        if (_shellCustomBackgroundActive)
        {
            ClearBackgroundImage();
            return;
        }

        int version = ++_imageLoadVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearBackgroundImage();
            return;
        }

        Border incoming = _isAActive ? BackgroundB : BackgroundA;
        int decodePixelWidth = CalculateImageDecodePixelWidth();
        int knownDecodePixelWidth = GetKnownDecodePixelWidth(path);
        bool isDecodeRefresh = knownDecodePixelWidth > 0 && decodePixelWidth != knownDecodePixelWidth;
        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = decodePixelWidth,
            CreateOptions = isDecodeRefresh
                ? BitmapCreateOptions.IgnoreImageCache
                : BitmapCreateOptions.None
        };
        _requestedImagePath = path;
        _requestedImageDecodePixelWidth = decodePixelWidth;
        bitmap.ImageOpened += (_, _) =>
        {
            if (version == _imageLoadVersion && _isLoaded)
            {
                _decodedImagePath = path;
                _decodedImagePixelWidth = decodePixelWidth;
                App.LogVerbose(
                    $"[GlanceWidgetContent] Image opened '{path}', " +
                    $"requestedWidth={decodePixelWidth}, decoded={bitmap.PixelWidth}x{bitmap.PixelHeight}");
                RunTransition(incoming, allowTransition);
            }
        };
        bitmap.ImageFailed += (_, args) =>
        {
            if (version == _imageLoadVersion)
            {
                _requestedImagePath = _decodedImagePath;
                _requestedImageDecodePixelWidth = _decodedImagePixelWidth;
            }

            App.Log($"[GlanceWidgetContent] Image decode failed for '{path}': {args.ErrorMessage}");
        };

        ImageBrush brush = CreateImageBrush(bitmap);
        incoming.Background = brush;
        if (!_viewModel.IsCurrentImageOnline &&
            _viewModel.ImageCount > LargeLocalStreamingImageSetThreshold)
        {
            // The framework's URI-keyed image cache has no eviction API, so
            // a large local rotation set would pin every decoded frame for
            // the session. Streamed sources stay out of that cache and keep
            // the live footprint at ~2 bitmaps; small sets keep URI reuse.
            _ = LoadImageFromRandomAccessStreamAsync(bitmap, path);
        }
        else
        {
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
        }
    }

    private const int LargeLocalStreamingImageSetThreshold = 24;

    private static async Task LoadImageFromRandomAccessStreamAsync(
        BitmapImage bitmap,
        string path)
    {
        try
        {
            using Windows.Storage.Streams.IRandomAccessStream stream =
                await Windows.Storage.Streams.FileRandomAccessStream.OpenAsync(
                    path,
                    Windows.Storage.FileAccessMode.Read);
            await bitmap.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[GlanceWidgetContent] Streamed image load failed for '{path}': {ex.Message}");
        }
    }

    private void QueueImageQualityRefresh()
    {
        _imageResizeDelayTimer.Stop();
        if (!_isLoaded ||
            string.IsNullOrWhiteSpace(_viewModel.CurrentImagePath) ||
            !File.Exists(_viewModel.CurrentImagePath))
        {
            return;
        }

        int requiredDecodePixelWidth = CalculateImageDecodePixelWidth();
        int knownDecodePixelWidth = GetKnownDecodePixelWidth(_viewModel.CurrentImagePath);
        if (!GlanceImageDecodeSizeCalculator.NeedsRefresh(
                knownDecodePixelWidth,
                requiredDecodePixelWidth))
        {
            return;
        }

        _imageResizeDelayTimer.Start();
    }

    private void ImageResizeDelayTimer_Tick(object? sender, object e)
    {
        _imageResizeDelayTimer.Stop();
        string? path = _viewModel.CurrentImagePath;
        if (!_isLoaded || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        int requiredDecodePixelWidth = CalculateImageDecodePixelWidth();
        if (GlanceImageDecodeSizeCalculator.NeedsRefresh(
                GetKnownDecodePixelWidth(path),
                requiredDecodePixelWidth))
        {
            BeginLoadImage(path, allowTransition: false);
        }
    }

    private int CalculateImageDecodePixelWidth()
    {
        return GlanceImageDecodeSizeCalculator.Calculate(
            ActualWidth,
            ActualHeight,
            XamlRoot?.RasterizationScale ?? 1);
    }

    private int GetKnownDecodePixelWidth(string path)
    {
        int knownDecodePixelWidth = 0;
        if (string.Equals(path, _requestedImagePath, StringComparison.OrdinalIgnoreCase))
        {
            knownDecodePixelWidth = _requestedImageDecodePixelWidth;
        }

        if (string.Equals(path, _decodedImagePath, StringComparison.OrdinalIgnoreCase))
        {
            knownDecodePixelWidth = Math.Max(knownDecodePixelWidth, _decodedImagePixelWidth);
        }

        return knownDecodePixelWidth;
    }

    private void ClearBackgroundImage()
    {
        _transitionStoryboard?.Stop();
        _transitionStoryboard = null;

        foreach (Border background in new[] { BackgroundA, BackgroundB })
        {
            background.Background = null;
            background.Opacity = 0;
            ResetTransform(background);
        }

        _isAActive = false;
        _requestedImagePath = null;
        _requestedImageDecodePixelWidth = 0;
        _decodedImagePath = null;
        _decodedImagePixelWidth = 0;
    }

    private void RunTransition(Border incoming, bool allowTransition)
    {
        Border outgoing = ReferenceEquals(incoming, BackgroundA) ? BackgroundB : BackgroundA;
        _transitionStoryboard?.Stop();
        ResetTransform(incoming);
        ResetTransform(outgoing);

        bool animate = allowTransition &&
            WindowsCompatibilityService.ShouldAnimate &&
            _viewModel.Transition != GlanceTransitionMode.None &&
            outgoing.Background is not null;
        if (!animate)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            outgoing.Background = null;
            _isAActive = ReferenceEquals(incoming, BackgroundA);
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(_viewModel.TransitionSpeed switch
        {
            GlanceTransitionSpeed.Fast => 170,
            GlanceTransitionSpeed.Relaxed => 520,
            _ => 300
        });
        incoming.Opacity = 0;
        outgoing.Opacity = 1;

        var storyboard = new Storyboard();
        AddAnimation(storyboard, incoming, "Opacity", 0, 1, duration);
        AddAnimation(storyboard, outgoing, "Opacity", 1, 0, duration);

        if (_viewModel.Transition == GlanceTransitionMode.SlideFade && incoming.RenderTransform is CompositeTransform slide)
        {
            slide.TranslateY = 16;
            AddAnimation(storyboard, slide, "TranslateY", 16, 0, duration);
        }
        else if (_viewModel.Transition == GlanceTransitionMode.ZoomFade && incoming.RenderTransform is CompositeTransform zoom)
        {
            zoom.ScaleX = 1.035;
            zoom.ScaleY = 1.035;
            AddAnimation(storyboard, zoom, "ScaleX", 1.035, 1, duration);
            AddAnimation(storyboard, zoom, "ScaleY", 1.035, 1, duration);
        }

        storyboard.Completed += (_, _) =>
        {
            outgoing.Background = null;
            outgoing.Opacity = 0;
            ResetTransform(incoming);
            _isAActive = ReferenceEquals(incoming, BackgroundA);
        };
        _transitionStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void ApplyBackgroundBrushOptions()
    {
        foreach (Border background in new[] { BackgroundA, BackgroundB })
        {
            if (background.Background is ImageBrush brush)
            {
                ApplyBackgroundBrushOptions(brush);
            }
        }
    }

    private ImageBrush CreateImageBrush(ImageSource source)
    {
        var brush = new ImageBrush { ImageSource = source };
        ApplyBackgroundBrushOptions(brush);
        return brush;
    }

    private void ApplyBackgroundBrushOptions(ImageBrush brush)
    {
        brush.Stretch = _viewModel.ImageFit == GlanceImageFitMode.Fit ? Stretch.Uniform : Stretch.UniformToFill;
        brush.AlignmentX = _viewModel.ImageFocus switch
        {
            GlanceImageFocus.Left => AlignmentX.Left,
            GlanceImageFocus.Right => AlignmentX.Right,
            _ => AlignmentX.Center
        };
        brush.AlignmentY = _viewModel.ImageFocus switch
        {
            GlanceImageFocus.Top => AlignmentY.Top,
            GlanceImageFocus.Bottom => AlignmentY.Bottom,
            _ => AlignmentY.Center
        };
    }

    private static void ResetTransform(Border border)
    {
        if (border.RenderTransform is CompositeTransform transform)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }

    private void UpdateLoadingIndicator()
    {
        _loadingDelayTimer.Stop();
        if (!_viewModel.IsLoading)
        {
            DelayedLoadingRing.IsActive = false;
            DelayedLoadingRing.Visibility = Visibility.Collapsed;
            return;
        }

        _loadingDelayTimer.Start();
    }

    private void LoadingDelayTimer_Tick(object? sender, object e)
    {
        _loadingDelayTimer.Stop();
        if (_viewModel.IsLoading && _isLoaded)
        {
            DelayedLoadingRing.Visibility = Visibility.Visible;
            DelayedLoadingRing.IsActive = true;
        }
    }

    private void Root_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActionLayer.Opacity = 1;
    }

    private void Root_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ActionLayer.Opacity = 0;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _viewModel.TogglePause();
    private void NextButton_Click(object sender, RoutedEventArgs e) => _viewModel.NextImage();
    private async void PhotoInfoButton_Click(object sender, RoutedEventArgs e) => await _viewModel.OpenPhotoInfoAsync();
}

public sealed partial class GlanceBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed partial class GlanceInverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed partial class GlanceBoolToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
