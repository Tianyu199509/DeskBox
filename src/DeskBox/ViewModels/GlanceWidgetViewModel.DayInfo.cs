using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// Style selection, size adaptation and the "what's today" surface (traditional
/// calendar, festivals, statutory holidays, To-do agenda) of the Glance widget.
/// </summary>
public sealed partial class GlanceWidgetViewModel
{
    private const int WeekLength = 7;

    private readonly GlanceTodoAgendaSource _agendaSource;
    private IReadOnlyList<GlanceAgendaItem> _gridAgenda = [];
    private IReadOnlyList<GlanceAgendaItem> _todayAgenda = [];
    private IReadOnlyList<GlanceCalendarDay> _weekDays = [];
    private GlanceCalendarDay? _todayCalendarDay;
    private GlanceHolidayCountdown? _holidayCountdown;
    private string _dayInfoLine = string.Empty;
    private string _todayTraditionalText = string.Empty;
    private int _calendarWeekRows = GlanceCalendarLayoutCalculator.DefaultWeekRows;
    private int _agendaRefreshVersion;
    private bool _agendaDirtyWhileInactive;
    private string _todoWidgetsSignature = string.Empty;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _agendaRefreshTimer;

    public GlanceStyle Style => _settings.Style;

    public GlanceStyle EffectiveStyle => GlanceStyleLayoutCalculator.ResolveEffectiveStyle(
        _settings.Style,
        _availableWidth,
        _availableHeight);

    public GlancePhotoClockAlignment PhotoClockAlignment => _settings.PhotoClockAlignment;

    public bool IsTodayCardStyle => EffectiveStyle == GlanceStyle.TodayCard;

    public Visibility TodayCardVisibility =>
        IsTodayCardStyle ? Visibility.Visible : Visibility.Collapsed;

    public bool IsWeekStripStyle => EffectiveStyle == GlanceStyle.WeekStrip;

    public Visibility WeekStripVisibility =>
        IsWeekStripStyle ? Visibility.Visible : Visibility.Collapsed;

    public bool IsMonthCalendarStyle => EffectiveStyle == GlanceStyle.MonthCalendar;

    /// <summary>Compact panels drop their in-panel clock strip once it no longer fits.</summary>
    public bool ShowCompactCalendarHeader =>
        IsCompactCalendarPresentation && !GlanceCalendarLayoutCalculator.IsMinimal(_availableHeight);

    public Visibility CompactCalendarHeaderVisibility =>
        ShowCompactCalendarHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowTimeVisibility =>
        ShowTime ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowDateVisibility =>
        ShowDate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowWeekdayVisibility =>
        ShowWeekday ? Visibility.Visible : Visibility.Collapsed;

    private bool UsesTodoAgenda =>
        _settings.ShowTodoAgenda && _settings.Style != GlanceStyle.PhotoClock;

    public int CalendarWeekRows => _calendarWeekRows;

    public bool IsDisplayingCurrentMonth =>
        _displayedCalendarMonth == new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);

    public bool ShowStatutoryHolidays =>
        _settings.ShowStatutoryHolidays && GlanceHolidayMarkService.IsApplicable(GetCulture());

    public bool ShowTodoAgenda => _settings.ShowTodoAgenda;

    /// <summary>"九月十八 · 重阳 · 国庆节" — traditional date, festival and holiday for today.</summary>
    public string DayInfoLine
    {
        get => _dayInfoLine;
        private set
        {
            if (SetProperty(ref _dayInfoLine, value))
            {
                OnPropertyChanged(nameof(HasDayInfoLine));
                OnPropertyChanged(nameof(DayInfoLineVisibility));
            }
        }
    }

    public bool HasDayInfoLine => !string.IsNullOrWhiteSpace(_dayInfoLine);

    public Visibility DayInfoLineVisibility =>
        HasDayInfoLine ? Visibility.Visible : Visibility.Collapsed;

    public string TodayTraditionalText
    {
        get => _todayTraditionalText;
        private set
        {
            if (SetProperty(ref _todayTraditionalText, value))
            {
                OnPropertyChanged(nameof(HasTodayTraditionalText));
            }
        }
    }

    public bool HasTodayTraditionalText => !string.IsNullOrWhiteSpace(_todayTraditionalText);

    public GlanceHolidayMark TodayHolidayMark =>
        ShowStatutoryHolidays ? GlanceHolidayMarkService.GetMark(DateOnly.FromDateTime(DateTime.Today)) : GlanceHolidayMark.None;

    public bool HasTodayHolidayBadge => TodayHolidayMark != GlanceHolidayMark.None;

    public Visibility TodayHolidayBadgeVisibility =>
        HasTodayHolidayBadge ? Visibility.Visible : Visibility.Collapsed;

    public bool IsTodayHolidayOff => TodayHolidayMark == GlanceHolidayMark.Off;

    public string TodayHolidayBadgeText => FormatHolidayBadge(TodayHolidayMark);

    public string HolidayCountdownText
    {
        get
        {
            if (_holidayCountdown is not { } countdown)
            {
                return string.Empty;
            }

            if (countdown.IsInProgress)
            {
                return countdown.DayOfBreak == 1
                    ? string.Format(GetCulture(), _localizationService.T("Glance.Countdown.Today"), countdown.HolidayName)
                    : string.Format(GetCulture(), _localizationService.T("Glance.Countdown.InProgress"), countdown.HolidayName, countdown.DayOfBreak);
            }

            return string.Format(
                GetCulture(),
                _localizationService.T("Glance.Countdown.DaysUntil"),
                countdown.HolidayName,
                countdown.DaysUntil);
        }
    }

    public bool HasHolidayCountdown => _holidayCountdown is not null;

    public string TodayDayNumberText =>
        DateTime.Today.Day.ToString(GetCulture());

    public string TodayMonthText
    {
        get
        {
            CultureInfo culture = GetCulture();
            return _settings.ShowYear
                ? DateTime.Today.ToString(culture.DateTimeFormat.YearMonthPattern, culture)
                : DateTime.Today.ToString("MMMM", culture);
        }
    }

    public double TodayCardDayFontSize => GlanceStyleLayoutCalculator.CalculateTodayCardDayFontSize(
        _availableWidth,
        _availableHeight,
        _settings.TimeScale);

    public int TodayCardAgendaCapacity => ShowTodoAgenda
        ? GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(_availableWidth, _availableHeight)
        : 0;

    public double WeekStripTimeFontSize => GlanceStyleLayoutCalculator.CalculateWeekStripTimeFontSize(
        _availableWidth,
        _availableHeight,
        _settings.TimeScale,
        ShowSeconds);

    public bool WeekStripShowsAgendaLine =>
        GlanceStyleLayoutCalculator.WeekStripShowsAgendaLine(_availableHeight) && HasAgendaLine;

    public Visibility WeekStripAgendaLineVisibility =>
        WeekStripShowsAgendaLine ? Visibility.Visible : Visibility.Collapsed;

    public bool WeekStripShowsSecondaryText =>
        GlanceStyleLayoutCalculator.WeekStripShowsSecondaryText(_availableWidth, _availableHeight);

    /// <summary>Today's open items plus anything overdue, already sorted.</summary>
    public IReadOnlyList<GlanceAgendaItem> TodayAgenda => _todayAgenda;

    public bool HasTodayAgenda => _todayAgenda.Count > 0;

    /// <summary>One-line summary: the first open item, else the holiday countdown.</summary>
    public string AgendaLineText
    {
        get
        {
            if (_todayAgenda.Count > 0)
            {
                string first = _todayAgenda[0].Title;
                return _todayAgenda.Count == 1
                    ? first
                    : $"{first} · {string.Format(GetCulture(), _localizationService.T("Glance.Agenda.More"), _todayAgenda.Count - 1)}";
            }

            return HolidayCountdownText;
        }
    }

    public bool HasAgendaLine => !string.IsNullOrWhiteSpace(AgendaLineText);

    public Visibility AgendaLineVisibility =>
        HasAgendaLine ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The seven days of the current week, sliced from the current month's grid.</summary>
    public IReadOnlyList<GlanceCalendarDay> WeekDays => _weekDays;

    internal string FormatHolidayBadge(GlanceHolidayMark mark) => mark switch
    {
        GlanceHolidayMark.Off => _localizationService.T("Glance.Holiday.Off"),
        GlanceHolidayMark.Work => _localizationService.T("Glance.Holiday.Work"),
        _ => string.Empty
    };

    internal IReadOnlyList<GlanceAgendaItem> GetAgendaFor(DateOnly date) =>
        _gridAgenda.Where(item => item.Date == date).ToArray();

    internal string FormatAgendaOverdue() => _localizationService.T("Glance.Agenda.Overdue");

    internal string FormatAgendaMore(int count) =>
        string.Format(GetCulture(), _localizationService.T("Glance.Agenda.More"), count);

    internal string FormatAgendaEmpty() => _localizationService.T("Glance.DayDetail.Nothing");

    internal string FormatBackToToday() => _localizationService.T("Glance.Calendar.BackToToday");

    internal string FormatPreviousMonth() => _localizationService.T("Glance.Calendar.PreviousMonth");

    internal string FormatNextMonth() => _localizationService.T("Glance.Calendar.NextMonth");

    internal string FormatDayDetailTitle(DateOnly date)
    {
        CultureInfo culture = GetCulture();
        DateTime value = date.ToDateTime(TimeOnly.MinValue);
        return $"{value.ToString(culture.DateTimeFormat.MonthDayPattern, culture)} {value.ToString("dddd", culture)}";
    }

    internal string FormatAgendaTime(GlanceAgendaItem item)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (item.Date < today)
        {
            return FormatAgendaOverdue();
        }

        return item.Time is { } time
            ? FormatTimeText(today.ToDateTime(time), _settings.TimeFormat, GetCulture())
            : string.Empty;
    }

    /// <summary>Secondary line for a day: festival / holiday / traditional date.</summary>
    internal string FormatDaySummary(GlanceCalendarDay day)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(day.TraditionalText))
        {
            parts.Add(day.TraditionalText);
        }

        if (!string.IsNullOrWhiteSpace(day.FestivalText))
        {
            parts.Add(day.FestivalText);
        }

        if (!string.IsNullOrWhiteSpace(day.HolidayName))
        {
            parts.Add(day.HolidayName);
        }
        else if (day.HolidayMark == GlanceHolidayMark.Work)
        {
            parts.Add(FormatHolidayBadge(GlanceHolidayMark.Work));
        }

        return string.Join(" · ", parts.Distinct(StringComparer.Ordinal));
    }

    public Task SetStyleAsync(GlanceStyle style)
    {
        return _store.UpdateAsync(settings =>
            GlanceWidgetSettingsPolicy.SetStyle(settings, style));
    }

    /// <summary>Returns the displayed month to the current month (header shortcut).</summary>
    public Task ReturnToCurrentMonthAsync() =>
        SetDisplayedCalendarMonthAsync(new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1));

    private void InitializeDayInfo()
    {
        _todoWidgetsSignature = _agendaSource.GetEnabledWidgetsSignature();
        TodoWidgetStore.StoreChanged += TodoWidgetStore_StoreChanged;
    }

    private void DisposeDayInfo()
    {
        TodoWidgetStore.StoreChanged -= TodoWidgetStore_StoreChanged;
        if (_agendaRefreshTimer is not null)
        {
            _agendaRefreshTimer.Stop();
            _agendaRefreshTimer.Tick -= AgendaRefreshTimer_Tick;
            _agendaRefreshTimer = null;
        }
    }

    private void TodoWidgetStore_StoreChanged(string storePath, string widgetId)
    {
        if (_isDisposed || !UsesTodoAgenda)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || !UsesTodoAgenda)
            {
                return;
            }

            if (!_isWindowVisible || _isCompact)
            {
                _agendaDirtyWhileInactive = true;
                return;
            }

            ScheduleDebouncedAgendaRefresh();
        });
    }

    private void ScheduleDebouncedAgendaRefresh()
    {
        if (_agendaRefreshTimer is null)
        {
            _agendaRefreshTimer = _dispatcherQueue.CreateTimer();
            _agendaRefreshTimer.IsRepeating = false;
            _agendaRefreshTimer.Interval = TimeSpan.FromMilliseconds(400);
            _agendaRefreshTimer.Tick += AgendaRefreshTimer_Tick;
        }

        // Debounce bursts of store writes (step toggles, reorders).
        _agendaRefreshTimer.Stop();
        _agendaRefreshTimer.Start();
    }

    private void FlushDeferredAgendaIfNeeded()
    {
        if (!_agendaDirtyWhileInactive || !_isWindowVisible || _isCompact || !UsesTodoAgenda)
        {
            return;
        }

        _agendaDirtyWhileInactive = false;
        _ = RefreshAgendaAsync();
    }

    private void RefreshAgendaIfTodoWidgetsChanged()
    {
        string signature = _agendaSource.GetEnabledWidgetsSignature();
        if (string.Equals(signature, _todoWidgetsSignature, StringComparison.Ordinal))
        {
            return;
        }

        _todoWidgetsSignature = signature;
        if (!UsesTodoAgenda)
        {
            return;
        }

        if (!_isWindowVisible || _isCompact)
        {
            _agendaDirtyWhileInactive = true;
            return;
        }

        _ = RefreshAgendaAsync();
    }

    private void AgendaRefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _ = RefreshAgendaAsync();
    }

    /// <summary>
    /// Re-reads only the To-do agenda (store edits) and re-projects it onto
    /// the current grid without rebuilding traditional/holiday decorations.
    /// </summary>
    private async Task RefreshAgendaAsync()
    {
        if (_isDisposed || CalendarDays.Count == 0)
        {
            return;
        }

        int version = ++_agendaRefreshVersion;
        DateOnly requestedMonth = _displayedCalendarMonth;
        IReadOnlyList<GlanceAgendaItem> agenda = await LoadGridAgendaAsync(CalendarDays);
        if (_isDisposed || version != _agendaRefreshVersion || requestedMonth != _displayedCalendarMonth)
        {
            return;
        }

        GlanceCalendarDay[] days = CalendarDays.ToArray();
        ApplyAgendaCounts(days, agenda);
        CalendarDays.Clear();
        foreach (GlanceCalendarDay day in days)
        {
            CalendarDays.Add(day);
        }

        _gridAgenda = agenda;
        OnPropertyChanged(nameof(CalendarDays));
        UpdateDayInfo(days);
    }

    private async Task<IReadOnlyList<GlanceAgendaItem>> LoadGridAgendaAsync(IReadOnlyList<GlanceCalendarDay> days)
    {
        _agendaDirtyWhileInactive = false;
        _todoWidgetsSignature = _agendaSource.GetEnabledWidgetsSignature();
        if (!UsesTodoAgenda || days.Count == 0)
        {
            return [];
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly start = days[0].Date;
        DateOnly end = days[^1].Date;
        if (today > end)
        {
            end = today;
        }

        try
        {
            return await _agendaSource.GetAsync(start, end, includeOverdueBefore: today, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceWidgetViewModel] Agenda load failed: {ex.Message}");
            return [];
        }
    }

    private static void ApplyAgendaCounts(GlanceCalendarDay[] days, IReadOnlyList<GlanceAgendaItem> agenda)
    {
        Dictionary<DateOnly, int> counts = agenda
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.Count());
        for (int index = 0; index < days.Length; index++)
        {
            int count = counts.TryGetValue(days[index].Date, out int value) ? value : 0;
            if (days[index].AgendaCount != count)
            {
                days[index] = days[index] with { AgendaCount = count };
            }
        }
    }

    /// <summary>
    /// Recomputes today's surface (traditional text, festival/holiday line,
    /// countdown, agenda, current week) from the freshest grid.
    /// </summary>
    private void UpdateDayInfo(IReadOnlyList<GlanceCalendarDay>? currentMonthDays = null)
    {
        CultureInfo culture = GetCulture();
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly currentMonth = new(today.Year, today.Month, 1);

        if (currentMonthDays is not null && _displayedCalendarMonth == currentMonth)
        {
            _todayCalendarDay = currentMonthDays.FirstOrDefault(day => day.Date == today);
            _weekDays = SliceWeek(currentMonthDays, today);
        }
        else if (_todayCalendarDay is { } cached && cached.Date != today)
        {
            _todayCalendarDay = null;
            _weekDays = [];
        }

        string traditional = _traditionalCalendarService.FormatDay(today, _settings.TraditionalCalendarMode, culture);
        string traditionalTitle = _traditionalCalendarService.FormatTitle(today, _settings.TraditionalCalendarMode, culture);
        TodayTraditionalText = traditionalTitle;

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(traditionalTitle))
        {
            // The title carries the cyclical year for Chinese ("乙巳年 九月十八");
            // the widget line only needs the month/day part.
            int space = traditionalTitle.LastIndexOf(' ');
            parts.Add(space > 0 ? traditionalTitle[(space + 1)..] : traditionalTitle);
        }
        else if (!string.IsNullOrWhiteSpace(traditional))
        {
            parts.Add(traditional);
        }

        string festival = _todayCalendarDay is { Date: var cachedDate } &&
            cachedDate == today &&
            _displayedCalendarMonth == currentMonth
                ? _todayCalendarDay.FestivalText
                : _festivalService.GetFestivalText(
                    today,
                    _settings.ShowChineseFestivals,
                    _settings.TraditionalCalendarMode,
                    culture);
        if (!string.IsNullOrWhiteSpace(festival))
        {
            parts.Add(festival);
        }

        bool holidays = ShowStatutoryHolidays;
        if (holidays &&
            GlanceHolidayMarkService.GetMark(today) == GlanceHolidayMark.Off &&
            CustomWidgetHolidaySchedule.TryGetHolidayAt(today, out CustomWidgetHolidayEntry entry))
        {
            parts.Add(CustomWidgetHolidaySchedule.GetLocalizedName(entry, culture.Name));
        }
        else if (holidays && GlanceHolidayMarkService.GetMark(today) == GlanceHolidayMark.Work)
        {
            parts.Add(FormatHolidayBadge(GlanceHolidayMark.Work));
        }

        DayInfoLine = string.Join(" · ", parts.Distinct(StringComparer.Ordinal));
        _holidayCountdown = holidays ? GlanceHolidayMarkService.GetCountdown(today, culture) : null;
        _todayAgenda = _gridAgenda.Where(item => item.Date <= today).ToArray();
        RaiseDayInfoProperties();
    }

    private static IReadOnlyList<GlanceCalendarDay> SliceWeek(IReadOnlyList<GlanceCalendarDay> days, DateOnly today)
    {
        for (int index = 0; index < days.Count; index++)
        {
            if (days[index].Date != today)
            {
                continue;
            }

            int start = index / WeekLength * WeekLength;
            if (start + WeekLength > days.Count)
            {
                break;
            }

            var week = new GlanceCalendarDay[WeekLength];
            for (int offset = 0; offset < WeekLength; offset++)
            {
                week[offset] = days[start + offset];
            }

            return week;
        }

        return [];
    }

    private void RaiseDayInfoProperties()
    {
        OnPropertyChanged(nameof(TodayHolidayMark));
        OnPropertyChanged(nameof(HasTodayHolidayBadge));
        OnPropertyChanged(nameof(TodayHolidayBadgeVisibility));
        OnPropertyChanged(nameof(IsTodayHolidayOff));
        OnPropertyChanged(nameof(TodayHolidayBadgeText));
        OnPropertyChanged(nameof(HolidayCountdownText));
        OnPropertyChanged(nameof(HasHolidayCountdown));
        OnPropertyChanged(nameof(TodayDayNumberText));
        OnPropertyChanged(nameof(TodayMonthText));
        OnPropertyChanged(nameof(TodayAgenda));
        OnPropertyChanged(nameof(HasTodayAgenda));
        OnPropertyChanged(nameof(AgendaLineText));
        OnPropertyChanged(nameof(HasAgendaLine));
        OnPropertyChanged(nameof(AgendaLineVisibility));
        OnPropertyChanged(nameof(WeekStripShowsAgendaLine));
        OnPropertyChanged(nameof(WeekStripAgendaLineVisibility));
        OnPropertyChanged(nameof(WeekDays));
        OnPropertyChanged(nameof(IsDisplayingCurrentMonth));
    }

    private void RaiseStyleProperties()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(EffectiveStyle));
        OnPropertyChanged(nameof(PhotoClockAlignment));
        OnPropertyChanged(nameof(IsTodayCardStyle));
        OnPropertyChanged(nameof(TodayCardVisibility));
        OnPropertyChanged(nameof(IsWeekStripStyle));
        OnPropertyChanged(nameof(WeekStripVisibility));
        OnPropertyChanged(nameof(IsMonthCalendarStyle));
        OnPropertyChanged(nameof(ShowCompactCalendarHeader));
        OnPropertyChanged(nameof(CompactCalendarHeaderVisibility));
        OnPropertyChanged(nameof(ShowTimeVisibility));
        OnPropertyChanged(nameof(ShowDateVisibility));
        OnPropertyChanged(nameof(ShowWeekdayVisibility));
        OnPropertyChanged(nameof(ShowStatutoryHolidays));
        OnPropertyChanged(nameof(ShowTodoAgenda));
        RaiseSizeDependentStyleProperties();
    }

    private void RaiseSizeDependentStyleProperties()
    {
        OnPropertyChanged(nameof(TodayCardDayFontSize));
        OnPropertyChanged(nameof(TodayCardAgendaCapacity));
        OnPropertyChanged(nameof(WeekStripTimeFontSize));
        OnPropertyChanged(nameof(WeekStripShowsAgendaLine));
        OnPropertyChanged(nameof(WeekStripAgendaLineVisibility));
        OnPropertyChanged(nameof(WeekStripShowsSecondaryText));
        OnPropertyChanged(nameof(ShowCompactCalendarHeader));
        OnPropertyChanged(nameof(CompactCalendarHeaderVisibility));
    }
}
