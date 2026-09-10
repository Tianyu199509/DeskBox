using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// UI-thread-only navigation port from GlanceWidgetContent. The owner forwards
/// Loaded/Unloaded through SetLoaded; only final widget destruction calls Dispose.
/// Decoration and title visibility remain the owner's responsibility.
/// </summary>
internal sealed class GlanceCalendarNavigationController : IDisposable
{
    private readonly CalendarView _calendar;
    private readonly Action<DateOnly> _displayedMonthChanged;
    private readonly DispatcherTimer _monthSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _wheelGestureTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly PointerEventHandler _wheelHandler;
    private readonly Dictionary<CalendarViewDayItem, DateOnly> _realizedDays = [];
    private string _language = CultureInfo.CurrentCulture.Name;
    private DayOfWeek _firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    private ScrollViewer? _monthScrollViewer;
    private ScrollMode _originalScrollMode;
    private ScrollBarVisibility _originalScrollBarVisibility;
    private Button? _previousButton;
    private Button? _nextButton;
    private long? _displayModeToken;
    private bool _isLoaded;
    private bool _disposed;
    private bool _wheelGestureActive;
    private int _loadGeneration;

    public GlanceCalendarNavigationController(CalendarView calendar, Action<DateOnly> displayedMonthChanged,
        DateOnly? initialMonth = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(displayedMonthChanged);
        _calendar = calendar;
        _displayedMonthChanged = displayedMonthChanged;
        _wheelHandler = Calendar_PointerWheelChanged;
        _monthSyncTimer.Tick += MonthSyncTimer_Tick;
        _wheelGestureTimer.Tick += WheelGestureTimer_Tick;
        DisplayedMonth = GlanceCalendarNavigationPolicy.ClampMonth(
            initialMonth ?? DateOnly.FromDateTime(DateTime.Today));
    }

    public DateOnly DisplayedMonth { get; private set; }

    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (_disposed) return;
        // WinUI Language expects a language tag; invariant culture has none.
        string language = string.IsNullOrEmpty(culture.Name) ? "en-US" : culture.Name;
        DayOfWeek firstDay = GlanceCalendarNavigationPolicy.ResolveFirstDayOfWeek(culture);
        if (_language == language && _firstDayOfWeek == firstDay) return;
        _language = language;
        _firstDayOfWeek = firstDay;
        if (!_isLoaded) return;
        ConfigureCulture();
        ConfigureMonthViewScrolling();
        SetNativeDisplayDate();
    }

    public void SetLoaded(bool loaded)
    {
        if (_disposed || _isLoaded == loaded) return;
        _isLoaded = loaded;
        _loadGeneration++;
        if (!loaded)
        {
            Unsubscribe();
            return;
        }

        ConfigureCulture();
        _calendar.MinDate = ToDateTimeOffset(GlanceCalendarNavigationPolicy.MinimumMonth);
        _calendar.MaxDate = ToDateTimeOffset(new DateOnly(2100, 12, 31));
        _calendar.CalendarViewDayItemChanging += Calendar_DayItemChanging;
        _calendar.SizeChanged += Calendar_SizeChanged;
        _calendar.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, handledEventsToo: true);
        _displayModeToken = _calendar.RegisterPropertyChangedCallback(CalendarView.DisplayModeProperty, (_, _) =>
        {
            if (_calendar.DisplayMode == CalendarViewDisplayMode.Month)
            {
                ConfigureMonthViewScrolling();
                QueueCalendarMonthSync();
            }
            else
            {
                _monthSyncTimer.Stop();
            }
        });
        ConfigureMonthViewScrolling();
        SetNativeDisplayDate();
    }

    /// <summary>
    /// Normalizes/clamps to a month, moves the view, then notifies on change.
    /// Echoing DisplayedMonth from the owner's rebuild is a no-op.
    /// </summary>
    public void SetMonth(DateOnly month)
    {
        if (_disposed) return;
        month = GlanceCalendarNavigationPolicy.ClampMonth(month);
        if (month == DisplayedMonth) return;
        DisplayedMonth = month;
        if (_isLoaded) SetNativeDisplayDate();
        _displayedMonthChanged(month);
    }

    private void ConfigureCulture()
    {
        _calendar.Language = string.IsNullOrEmpty(_language) ? "en-US" : _language;
        _calendar.CalendarIdentifier = Windows.Globalization.CalendarIdentifiers.Gregorian;
        _calendar.FirstDayOfWeek = _firstDayOfWeek switch
        {
            DayOfWeek.Monday => Windows.Globalization.DayOfWeek.Monday,
            DayOfWeek.Tuesday => Windows.Globalization.DayOfWeek.Tuesday,
            DayOfWeek.Wednesday => Windows.Globalization.DayOfWeek.Wednesday,
            DayOfWeek.Thursday => Windows.Globalization.DayOfWeek.Thursday,
            DayOfWeek.Friday => Windows.Globalization.DayOfWeek.Friday,
            DayOfWeek.Saturday => Windows.Globalization.DayOfWeek.Saturday,
            _ => Windows.Globalization.DayOfWeek.Sunday,
        };
    }

    private void SetNativeDisplayDate()
    {
        _calendar.SetDisplayDate(ToDateTimeOffset(DisplayedMonth.AddDays(14)));
        QueueCalendarMonthSync();
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date) =>
        new(date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local));

    private void ConfigureMonthViewScrolling()
    {
        if (!_isLoaded) return;
        ConfigureTemplateParts();
        // Template creation / culture changes can defer visual children. An old
        // load's queued callback must not act on a later load of the same widget.
        int generation = _loadGeneration;
        _calendar.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isLoaded || _disposed || generation != _loadGeneration) return;
            ConfigureTemplateParts();
            QueueCalendarMonthSync();
        });
    }

    private void ConfigureTemplateParts()
    {
        _calendar.ApplyTemplate();
        _previousButton = FindDescendantByName<Button>(_calendar, "PreviousButton");
        _nextButton = FindDescendantByName<Button>(_calendar, "NextButton");
        ScrollViewer? viewer = FindDescendantByName<ScrollViewer>(_calendar, "MonthViewScrollViewer");
        if (!ReferenceEquals(viewer, _monthScrollViewer))
        {
            ReleaseScrollViewer();
            _monthScrollViewer = viewer;
            if (viewer is not null)
            {
                _originalScrollMode = viewer.VerticalScrollMode;
                _originalScrollBarVisibility = viewer.VerticalScrollBarVisibility;
                viewer.ViewChanged += MonthScrollViewer_ViewChanged;
            }
        }
        if (viewer is not null)
        {
            // Same as the built-in: never leave the month grid between pages.
            viewer.VerticalScrollMode = ScrollMode.Disabled;
            viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        // Reused templates need not raise DayItemChanging on every reload.
        _realizedDays.Clear();
        CollectRealizedDays(_calendar);
    }

    private void CollectRealizedDays(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is CalendarViewDayItem day)
                _realizedDays[day] = DateOnly.FromDateTime(day.Date.DateTime);
            else
                CollectRealizedDays(child);
        }
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal)) return match;
            if (FindDescendantByName<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private void Calendar_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLoaded || _calendar.DisplayMode != CalendarViewDisplayMode.Month) return;
        int delta = e.GetCurrentPoint(_calendar).Properties.MouseWheelDelta;
        if (delta == 0) return;
        e.Handled = true;
        _wheelGestureTimer.Stop();
        _wheelGestureTimer.Start();
        if (_wheelGestureActive) return;
        _wheelGestureActive = true;
        DateOnly target = GlanceCalendarNavigationPolicy.ResolveWheelTarget(DisplayedMonth, delta);
        if (target == DisplayedMonth) return;
        _previousButton ??= FindDescendantByName<Button>(_calendar, "PreviousButton");
        _nextButton ??= FindDescendantByName<Button>(_calendar, "NextButton");
        if (TryInvokeNavigationButton(delta > 0 ? _previousButton : _nextButton))
            QueueCalendarMonthSync();
        else
            SetMonth(target);
    }

    private static bool TryInvokeNavigationButton(Button? button)
    {
        if (button?.IsEnabled != true) return false;
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider provider) return false;
        // Application-internal invocation, matching built-in previous/next animation.
        provider.Invoke();
        return true;
    }

    private void WheelGestureTimer_Tick(object? sender, object e)
    {
        _wheelGestureTimer.Stop();
        _wheelGestureActive = false;
    }

    private void Calendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.InRecycleQueue) _realizedDays.Remove(args.Item);
        else _realizedDays[args.Item] = DateOnly.FromDateTime(args.Item.Date.DateTime);
        QueueCalendarMonthSync();
    }

    private void Calendar_SizeChanged(object sender, SizeChangedEventArgs e) => QueueCalendarMonthSync();

    private void MonthScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Also observe movements that reuse already-realized day containers.
        _monthSyncTimer.Stop();
        if (!e.IsIntermediate) QueueCalendarMonthSync();
    }

    private void QueueCalendarMonthSync()
    {
        if (!_isLoaded || _calendar.DisplayMode != CalendarViewDisplayMode.Month) return;
        _monthSyncTimer.Stop();
        _monthSyncTimer.Start();
    }

    private void MonthSyncTimer_Tick(object? sender, object e)
    {
        _monthSyncTimer.Stop();
        if (!_isLoaded || _calendar.DisplayMode != CalendarViewDisplayMode.Month) return;
        DateOnly month = GlanceCalendarNavigationPolicy.ResolveDisplayedMonth(
            _realizedDays.Where(pair => IsDayVisible(pair.Key)).Select(pair => pair.Value), DisplayedMonth);
        if (month == DisplayedMonth) return;
        // Native buttons / year selection have already moved the view. Never
        // call SetDisplayDate here: that would restart its animation.
        DisplayedMonth = month;
        _displayedMonthChanged(month);
    }

    private bool IsDayVisible(CalendarViewDayItem item)
    {
        if (!item.IsLoaded || item.ActualWidth <= 0 || item.ActualHeight <= 0) return false;
        try
        {
            var bounds = item.TransformToVisual(_calendar).TransformBounds(
                new Windows.Foundation.Rect(0, 0, item.ActualWidth, item.ActualHeight));
            return GlanceCalendarNavigationPolicy.IsVisible(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                _calendar.ActualWidth, _calendar.ActualHeight);
        }
        catch
        {
            // Recycling / template replacement can detach the item mid-layout.
            return false;
        }
    }

    private void ReleaseScrollViewer()
    {
        if (_monthScrollViewer is not { } viewer) return;
        viewer.ViewChanged -= MonthScrollViewer_ViewChanged;
        viewer.VerticalScrollMode = _originalScrollMode;
        viewer.VerticalScrollBarVisibility = _originalScrollBarVisibility;
        _monthScrollViewer = null;
    }

    private void Unsubscribe()
    {
        _monthSyncTimer.Stop();
        _wheelGestureTimer.Stop();
        _wheelGestureActive = false;
        _calendar.CalendarViewDayItemChanging -= Calendar_DayItemChanging;
        _calendar.SizeChanged -= Calendar_SizeChanged;
        _calendar.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
        if (_displayModeToken is long token)
        {
            _calendar.UnregisterPropertyChangedCallback(CalendarView.DisplayModeProperty, token);
            _displayModeToken = null;
        }
        ReleaseScrollViewer();
        _previousButton = null;
        _nextButton = null;
        _realizedDays.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetLoaded(false);
        _disposed = true;
        _monthSyncTimer.Tick -= MonthSyncTimer_Tick;
        _wheelGestureTimer.Tick -= WheelGestureTimer_Tick;
    }
}
