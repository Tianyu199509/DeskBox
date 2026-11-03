using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class GlanceWidgetContent : UserControl, IDisposable
{
    private const int CalendarColumnCount = 7;
    private const int CalendarRowCount = 6;
    private const int CalendarCellCount = CalendarColumnCount * CalendarRowCount;
    private static readonly DateOnly MinCalendarMonth = new(1900, 1, 1);
    private static readonly DateOnly MaxCalendarMonth = new(2100, 12, 1);
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private readonly GlanceWidgetViewModel _viewModel;
    private readonly GlanceImagePaletteService _paletteService = GlanceImagePaletteService.Shared;
    private readonly DispatcherTimer _loadingDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _imageResizeDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _layoutResizeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DispatcherTimer? _calendarWheelGestureTimer;
    private readonly SolidColorBrush _calendarSolidMaterialBrush = new();
    private readonly LinearGradientBrush _calendarImageGradientBrush = new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1)
    };
    private readonly GradientStop _calendarImageGradientStart = new() { Offset = 0 };
    private readonly GradientStop _calendarImageGradientEnd = new() { Offset = 1 };
    private Storyboard? _transitionStoryboard;
    private TextBlock[]? _calendarWeekdayLabels;
    private CalendarDayCellVisual[]? _calendarCellVisuals;
    private bool _isAActive;
    private bool _isLoaded;
    private bool _isCalendarWheelGestureActive;
    private bool _isCalendarNavigationInProgress;
    private int _imageLoadVersion;
    private bool _shellCustomBackgroundActive;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;
    private bool _isDisposed;
    private string? _calendarImagePalettePath;
    private GlanceImagePalette? _calendarImagePalette;
    private CancellationTokenSource? _paletteCts;
    private string? _requestedImagePath;
    private int _requestedImageDecodePixelWidth;
    private string? _decodedImagePath;
    private int _decodedImagePixelWidth;
    private double _pendingAvailableWidth;
    private double _pendingAvailableHeight;
    private bool _hasPendingAvailableSize;

    private sealed class CalendarDayCellVisual
    {
        public required Grid Root { get; init; }
        public required Border TodayPill { get; init; }
        public required StackPanel TextStack { get; init; }
        public required TextBlock PrimaryText { get; init; }
        public required TextBlock SecondaryText { get; init; }
        public DateOnly Date { get; set; }
        public bool IsCurrentMonth { get; set; }
        public bool IsToday { get; set; }
        public bool IsPointerOver { get; set; }
    }

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
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public bool IsCalendarElementLoaded =>
        _isLoaded &&
        _viewModel.IsCalendarLayout &&
        _isWindowVisible &&
        !_isCompactCollapsed;

    internal bool IsCalendarSurfaceRealized => CalendarSurfaceRoot is not null;

    internal bool IsNativeCalendarViewRealized => CalendarSurfaceRoot is not null;

    internal void OnWindowVisibilityChanged(bool visible)
    {
        if (_isWindowVisible == visible)
        {
            return;
        }

        _isWindowVisible = visible;
        ReconcileExpandedImageResidency();
        ReconcileCalendarSurfaceRealization();
    }

    internal void OnCompactStateChanged(bool collapsed)
    {
        if (_isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        ReconcileExpandedImageResidency();
        ReconcileCalendarSurfaceRealization();
    }

    private void ReconcileExpandedImageResidency()
    {
        if (!_isWindowVisible || _isCompactCollapsed)
        {
            CancelPaletteUpdate();
            _imageResizeDelayTimer.Stop();
            ClearBackgroundImage();
            return;
        }

        if (_isLoaded &&
            !_shellCustomBackgroundActive &&
            _decodedImagePath is null &&
            _requestedImagePath is null)
        {
            QueueCalendarImagePaletteUpdate(_viewModel.CurrentImagePath);
            BeginLoadImage(_viewModel.CurrentImagePath, allowTransition: false);
        }
    }

    private DispatcherTimer EnsureCalendarWheelGestureTimer()
    {
        if (_calendarWheelGestureTimer is null)
        {
            _calendarWheelGestureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _calendarWheelGestureTimer.Tick += CalendarWheelGestureTimer_Tick;
        }

        return _calendarWheelGestureTimer;
    }

    private void ReconcileCalendarSurfaceRealization()
    {
        if (!_isLoaded)
        {
            return;
        }

        if (!IsCalendarElementLoaded)
        {
            _calendarWheelGestureTimer?.Stop();
            _isCalendarWheelGestureActive = false;
            _calendarWeekdayLabels = null;
            _calendarCellVisuals = null;
            Bindings.Update();
            UpdateTraditionalCalendarTitleVisibility();
            return;
        }

        Bindings.Update();
        if (CalendarSurfaceRoot is not null)
        {
            EnsureCalendarGridInitialized();
            RefreshCalendarSurface();
        }

        UpdateTraditionalCalendarTitleVisibility();
    }

    private void CalendarSurfaceRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (!IsCalendarElementLoaded)
        {
            return;
        }

        EnsureCalendarGridInitialized();
        RefreshCalendarSurface();
    }

    private void CalendarSurfaceRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        _calendarWeekdayLabels = null;
        _calendarCellVisuals = null;
        UpdateTraditionalCalendarTitleVisibility();
    }

    private void EnsureCalendarGridInitialized()
    {
        if (CalendarWeekdayHeaderGrid is not { } weekdayGrid ||
            CalendarDaysGrid is not { } daysGrid)
        {
            return;
        }

        if (_calendarWeekdayLabels is null || weekdayGrid.Children.Count != CalendarColumnCount)
        {
            weekdayGrid.Children.Clear();
            weekdayGrid.ColumnDefinitions.Clear();
            var labels = new TextBlock[CalendarColumnCount];
            for (int column = 0; column < CalendarColumnCount; column++)
            {
                weekdayGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

                var weekdayLabel = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10.5,
                    MaxLines = 1,
                    TextAlignment = TextAlignment.Center,
                    IsTextSelectionEnabled = false
                };
                WidgetTextShadow.SetCast(weekdayLabel, false);
                Grid.SetColumn(weekdayLabel, column);
                weekdayGrid.Children.Add(weekdayLabel);
                labels[column] = weekdayLabel;
            }

            _calendarWeekdayLabels = labels;
        }

        if (_calendarCellVisuals is null || daysGrid.Children.Count != CalendarCellCount)
        {
            daysGrid.Children.Clear();
            daysGrid.ColumnDefinitions.Clear();
            daysGrid.RowDefinitions.Clear();

            for (int column = 0; column < CalendarColumnCount; column++)
            {
                daysGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            for (int row = 0; row < CalendarRowCount; row++)
            {
                daysGrid.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star)
                });
            }

            var cells = new CalendarDayCellVisual[CalendarCellCount];
            for (int index = 0; index < CalendarCellCount; index++)
            {
                int row = index / CalendarColumnCount;
                int column = index % CalendarColumnCount;

                var cellRoot = new Grid
                {
                    MinWidth = 30,
                    MinHeight = 22,
                    Background = TransparentBrush,
                    CornerRadius = new CornerRadius(6)
                };

                var todayPill = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };

                var primaryText = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    LineHeight = 13,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    IsTextSelectionEnabled = false
                };
                Typography.SetNumeralAlignment(primaryText, FontNumeralAlignment.Tabular);
                WidgetTextShadow.SetCast(primaryText, false);

                var secondaryText = new TextBlock
                {
                    MaxWidth = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 7.5,
                    LineHeight = 10,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Visibility = Visibility.Collapsed,
                    IsTextSelectionEnabled = false
                };
                WidgetTextShadow.SetCast(secondaryText, false);

                var textStack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 0,
                    IsHitTestVisible = false
                };
                textStack.Children.Add(primaryText);
                textStack.Children.Add(secondaryText);

                cellRoot.Children.Add(todayPill);
                cellRoot.Children.Add(textStack);
                Grid.SetRow(cellRoot, row);
                Grid.SetColumn(cellRoot, column);
                daysGrid.Children.Add(cellRoot);

                var visual = new CalendarDayCellVisual
                {
                    Root = cellRoot,
                    TodayPill = todayPill,
                    TextStack = textStack,
                    PrimaryText = primaryText,
                    SecondaryText = secondaryText
                };

                cellRoot.PointerEntered += (_, _) =>
                {
                    visual.IsPointerOver = true;
                    UpdateCalendarCellBackground(visual);
                };
                cellRoot.PointerExited += (_, _) =>
                {
                    visual.IsPointerOver = false;
                    UpdateCalendarCellBackground(visual);
                };
                cellRoot.Tapped += async (_, args) =>
                {
                    if (!visual.IsCurrentMonth && visual.Date != default)
                    {
                        args.Handled = true;
                        await NavigateToCalendarMonthAsync(new DateOnly(visual.Date.Year, visual.Date.Month, 1));
                    }
                };

                cells[index] = visual;
            }

            _calendarCellVisuals = cells;
        }
    }

    private void UpdateCalendarCellBackground(CalendarDayCellVisual cell)
    {
        if (cell.IsPointerOver && !cell.IsToday)
        {
            cell.Root.Background = CalendarThemeProbe?.BorderBrush ?? TransparentBrush;
        }
        else
        {
            cell.Root.Background = TransparentBrush;
        }
    }

    private void RefreshCalendarSurface()
    {
        if (!IsCalendarElementLoaded || CalendarSurfaceRoot is null)
        {
            UpdateTraditionalCalendarTitleVisibility();
            return;
        }

        EnsureCalendarGridInitialized();
        if (_calendarWeekdayLabels is null || _calendarCellVisuals is null)
        {
            return;
        }

        Brush primaryBrush = CalendarMonthHeaderText?.Foreground ?? Foreground;
        Brush secondaryBrush = TraditionalCalendarTitlePresenter?.Foreground ?? primaryBrush;
        Brush accentBrush = CalendarThemeProbe?.Background ?? primaryBrush;
        Brush onAccentBrush = CalendarOnAccentThemeProbe?.Foreground ?? primaryBrush;

        if (CalendarMonthHeaderText is not null)
        {
            CalendarMonthHeaderText.Text = _viewModel.FormatDisplayedCalendarMonthHeader();
        }

        DateOnly displayedMonth = _viewModel.DisplayedCalendarMonth;
        if (CalendarPreviousMonthButton is not null)
        {
            CalendarPreviousMonthButton.IsEnabled = displayedMonth > MinCalendarMonth;
        }

        if (CalendarNextMonthButton is not null)
        {
            CalendarNextMonthButton.IsEnabled = displayedMonth < MaxCalendarMonth;
        }

        var weekdayHeaders = _viewModel.WeekdayHeaders;
        for (int column = 0; column < CalendarColumnCount; column++)
        {
            TextBlock label = _calendarWeekdayLabels[column];
            label.Foreground = secondaryBrush;
            label.Text = column < weekdayHeaders.Count ? weekdayHeaders[column] : string.Empty;
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        bool showTraditional = _viewModel.ShowCalendarTraditionalDetails;
        string calendarLanguage = _viewModel.CalendarLanguage;
        double itemHeight = _viewModel.CalendarDayItemMinimumHeight;
        var days = _viewModel.CalendarDays;

        DateOnly fallbackStart = GetFallbackGridStartDate(displayedMonth);
        for (int index = 0; index < CalendarCellCount; index++)
        {
            CalendarDayCellVisual cell = _calendarCellVisuals[index];
            GlanceCalendarDay? day = index < days.Count ? days[index] : null;
            DateOnly date = day?.Date ?? fallbackStart.AddDays(index);

            GlanceCalendarDayDecoration decoration = GlanceCalendarDayDecorationBuilder.Build(
                day,
                date,
                today,
                showTraditional,
                displayedMonth,
                calendarLanguage);

            cell.Date = date;
            cell.IsCurrentMonth = day?.IsCurrentMonth ??
                (date.Year == displayedMonth.Year && date.Month == displayedMonth.Month);
            cell.IsToday = decoration.IsToday;
            if (Math.Abs(cell.Root.MinHeight - itemHeight) >= 0.1)
            {
                cell.Root.MinHeight = itemHeight;
            }

            cell.TodayPill.Background = accentBrush;
            cell.TodayPill.Visibility = decoration.IsToday
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateCalendarCellBackground(cell);

            cell.TextStack.Opacity = decoration.IsToday ? 1.0 : decoration.PrimaryOpacity;
            cell.PrimaryText.Text = decoration.DayText;
            cell.PrimaryText.FontWeight = decoration.IsToday
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            cell.PrimaryText.Foreground = decoration.IsToday ? onAccentBrush : primaryBrush;

            if (decoration.HasSecondaryText)
            {
                cell.SecondaryText.Visibility = Visibility.Visible;
                cell.SecondaryText.Text = decoration.SecondaryText;
                cell.SecondaryText.FontWeight = decoration.IsFestival
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                cell.SecondaryText.Foreground = decoration.IsToday ? onAccentBrush : secondaryBrush;
                cell.SecondaryText.Opacity = decoration.IsToday ? 0.86 : decoration.SecondaryOpacity;
            }
            else
            {
                cell.SecondaryText.Visibility = Visibility.Collapsed;
                cell.SecondaryText.Text = string.Empty;
            }
        }

        UpdateTraditionalCalendarTitleVisibility();
    }

    private DateOnly GetFallbackGridStartDate(DateOnly displayedMonth)
    {
        DateOnly monthStart = new(displayedMonth.Year, displayedMonth.Month, 1);
        DayOfWeek firstDayOfWeek = DayOfWeek.Sunday;
        try
        {
            firstDayOfWeek = System.Globalization.CultureInfo
                .GetCultureInfo(_viewModel.CalendarLanguage)
                .DateTimeFormat
                .FirstDayOfWeek;
        }
        catch
        {
        }

        int offset = (7 + (monthStart.DayOfWeek - firstDayOfWeek)) % 7;
        return monthStart.AddDays(-offset);
    }

    private async void CalendarPreviousMonthButton_Click(object sender, RoutedEventArgs e)
    {
        DateOnly current = _viewModel.DisplayedCalendarMonth;
        if (current <= MinCalendarMonth)
        {
            return;
        }

        await NavigateToCalendarMonthAsync(current.AddMonths(-1));
    }

    private async void CalendarNextMonthButton_Click(object sender, RoutedEventArgs e)
    {
        DateOnly current = _viewModel.DisplayedCalendarMonth;
        if (current >= MaxCalendarMonth)
        {
            return;
        }

        await NavigateToCalendarMonthAsync(current.AddMonths(1));
    }

    private async void CalendarSurfaceRoot_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!IsCalendarElementLoaded || CalendarSurfaceRoot is not { } surfaceRoot)
        {
            return;
        }

        int wheelDelta = e.GetCurrentPoint(surfaceRoot).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        e.Handled = true;
        DispatcherTimer wheelTimer = EnsureCalendarWheelGestureTimer();
        wheelTimer.Stop();
        wheelTimer.Start();
        if (_isCalendarWheelGestureActive || _isCalendarNavigationInProgress)
        {
            return;
        }

        _isCalendarWheelGestureActive = true;
        DateOnly targetMonth = GlanceCalendarNavigationResolver.ResolveWheelTarget(
            _viewModel.DisplayedCalendarMonth,
            wheelDelta,
            MinCalendarMonth,
            MaxCalendarMonth);
        if (targetMonth == _viewModel.DisplayedCalendarMonth)
        {
            return;
        }

        await NavigateToCalendarMonthAsync(targetMonth);
    }

    private async Task NavigateToCalendarMonthAsync(DateOnly targetMonth)
    {
        DateOnly clamped = targetMonth < MinCalendarMonth
            ? MinCalendarMonth
            : targetMonth > MaxCalendarMonth
                ? MaxCalendarMonth
                : new DateOnly(targetMonth.Year, targetMonth.Month, 1);
        if (_isCalendarNavigationInProgress || clamped == _viewModel.DisplayedCalendarMonth)
        {
            return;
        }

        _isCalendarNavigationInProgress = true;
        try
        {
            await _viewModel.SetDisplayedCalendarMonthAsync(clamped);
        }
        finally
        {
            _isCalendarNavigationInProgress = false;
        }
    }

    private void CalendarWheelGestureTimer_Tick(object? sender, object e)
    {
        _calendarWheelGestureTimer?.Stop();
        _isCalendarWheelGestureActive = false;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        _hasPendingAvailableSize = false;
        _viewModel.UpdateAvailableSize(ActualWidth, ActualHeight);
        ApplyBackgroundBrushOptions();
        ApplyBackgroundImageOpacity();
        ApplyImageAwareTheme();
        ReconcileCalendarSurfaceRealization();
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
        _calendarWheelGestureTimer?.Stop();
        _isCalendarWheelGestureActive = false;
        DelayedLoadingRing.IsActive = false;
        DelayedLoadingRing.Visibility = Visibility.Collapsed;
        SettleInterruptedTransition();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isLoaded = false;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _loadingDelayTimer.Stop();
        _loadingDelayTimer.Tick -= LoadingDelayTimer_Tick;
        _imageResizeDelayTimer.Stop();
        _imageResizeDelayTimer.Tick -= ImageResizeDelayTimer_Tick;
        _layoutResizeTimer.Stop();
        _layoutResizeTimer.Tick -= LayoutResizeTimer_Tick;
        if (_calendarWheelGestureTimer is not null)
        {
            _calendarWheelGestureTimer.Stop();
            _calendarWheelGestureTimer.Tick -= CalendarWheelGestureTimer_Tick;
            _calendarWheelGestureTimer = null;
        }

        _hasPendingAvailableSize = false;
        _isCalendarWheelGestureActive = false;
        CancelPaletteUpdate();
        ClearBackgroundImage();
        _calendarWeekdayLabels = null;
        _calendarCellVisuals = null;
        Bindings.Update();
        StopAndClearTransitionStoryboard();
        DataContext = null;
        Content = null;
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pendingAvailableWidth = e.NewSize.Width;
        _pendingAvailableHeight = e.NewSize.Height;
        _hasPendingAvailableSize = true;
        // Debounce responsive layout sizing during drag resize and settle once.
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

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(GlanceWidgetViewModel.IsCalendarLayout))
        {
            ReconcileCalendarSurfaceRealization();
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                return;
            }
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
        else if (e.PropertyName is
            nameof(GlanceWidgetViewModel.CalendarDays) or
            nameof(GlanceWidgetViewModel.WeekdayHeaders) or
            nameof(GlanceWidgetViewModel.DisplayedCalendarMonth) or
            nameof(GlanceWidgetViewModel.CalendarLanguage) or
            nameof(GlanceWidgetViewModel.CalendarDayItemMinimumHeight) or
            nameof(GlanceWidgetViewModel.ShowCalendarTraditionalDetails))
        {
            RefreshCalendarSurface();
        }
        else if (e.PropertyName == nameof(GlanceWidgetViewModel.TraditionalCalendarTitle))
        {
            UpdateTraditionalCalendarTitleVisibility();
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
            RefreshCalendarSurface();
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

    private void UpdateTraditionalCalendarTitleVisibility()
    {
        TraditionalCalendarTitlePresenter.Visibility =
            IsCalendarElementLoaded &&
            CalendarSurfaceRoot is not null &&
            _viewModel.ShowCalendarTraditionalDetails &&
            _viewModel.HasTraditionalCalendar
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void QueueCalendarImagePaletteUpdate(string? path)
    {
        if (!_isLoaded || !_isWindowVisible || _isCompactCollapsed || _viewModel.CalendarMaterialMode != GlanceCalendarMaterialMode.FollowImage)
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
        if (_shellCustomBackgroundActive || !_isWindowVisible || _isCompactCollapsed)
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

        SettleInterruptedTransition();
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
        // Rotation pins every decoded frame of the set when it goes through
        // the URI-keyed framework cache, which has no eviction API — and
        // small sets are the common case, so a size threshold cannot bound
        // it. Streamed sources stay out of that cache and keep the live
        // footprint at ~2 bitmaps; the crossfade runs only after
        // ImageOpened, so the re-decode on each rotation is invisible.
        _ = GlanceImageStreamLoader.LoadAsync(bitmap, path);
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
            XamlRoot?.RasterizationScale ?? 1,
            resourceSaver: App.Current?.IsResourceSaverPerformanceMode == true);
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

    private void StopAndClearTransitionStoryboard()
    {
        _transitionStoryboard?.Stop();
        _transitionStoryboard?.Children.Clear();
        _transitionStoryboard = null;
    }

    private void SettleInterruptedTransition()
    {
        if (_transitionStoryboard is not null)
        {
            StopAndClearTransitionStoryboard();
        }

        Border preferred = _isAActive ? BackgroundA : BackgroundB;
        Border alternate = _isAActive ? BackgroundB : BackgroundA;
        if (preferred.Background is not null)
        {
            preferred.Opacity = 1;
            ResetTransform(preferred);
            alternate.Background = null;
            alternate.Opacity = 0;
            ResetTransform(alternate);
        }
        else if (alternate.Background is not null)
        {
            alternate.Opacity = 1;
            ResetTransform(alternate);
            preferred.Opacity = 0;
            ResetTransform(preferred);
            _isAActive = ReferenceEquals(alternate, BackgroundA);
        }
        else
        {
            BackgroundA.Opacity = 0;
            BackgroundB.Opacity = 0;
            ResetTransform(BackgroundA);
            ResetTransform(BackgroundB);
            _isAActive = false;
        }
    }

    private void ClearBackgroundImage()
    {
        StopAndClearTransitionStoryboard();

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
        StopAndClearTransitionStoryboard();
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

        _isAActive = ReferenceEquals(incoming, BackgroundA);
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

        EventHandler<object>? completedHandler = null;
        completedHandler = (_, _) =>
        {
            storyboard.Completed -= completedHandler;
            if (!ReferenceEquals(_transitionStoryboard, storyboard))
            {
                return;
            }

            _transitionStoryboard = null;
            // WinUI keeps HoldEnd storyboards in an active clock state during
            // Completed; Stop() must precede Children.Clear() to avoid 0x800F1000.
            storyboard.Stop();
            storyboard.Children.Clear();
            incoming.Opacity = 1;
            outgoing.Background = null;
            outgoing.Opacity = 0;
            ResetTransform(incoming);
            _isAActive = ReferenceEquals(incoming, BackgroundA);
        };
        storyboard.Completed += completedHandler;
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
