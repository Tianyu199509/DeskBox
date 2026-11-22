using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Today card, week strip and month-calendar interactions (day detail flyout,
/// hover-revealed navigation). Visuals are built in code like the month grid:
/// fixed, small element counts that are reused across refreshes.
/// </summary>
public sealed partial class GlanceWidgetContent
{
    private const int WeekStripDayCount = 7;
    private readonly Dictionary<uint, SolidColorBrush> _agendaColorBrushes = new();
    private WeekDayCellVisual[]? _weekCells;
    private Flyout? _dayDetailFlyout;
    private bool _isCalendarPointerInside;

    private sealed class WeekDayCellVisual
    {
        public required Grid Root { get; init; }
        public required TextBlock WeekdayText { get; init; }
        public required Border TodayPill { get; init; }
        public required TextBlock DayText { get; init; }
        public required TextBlock SecondaryText { get; init; }
        public required TextBlock HolidayBadge { get; init; }
        public required Microsoft.UI.Xaml.Shapes.Ellipse AgendaDot { get; init; }
        public DateOnly Date { get; set; }
    }

    private void CalendarSurfaceRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isCalendarPointerInside = true;
        UpdateCalendarNavigationVisibility();
    }

    private void CalendarSurfaceRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isCalendarPointerInside = false;
        UpdateCalendarNavigationVisibility();
    }

    /// <summary>
    /// Month arrows stay out of the way in the current month until hovered,
    /// and remain visible while browsing another month.
    /// </summary>
    private void UpdateCalendarNavigationVisibility()
    {
        bool revealed = _isCalendarPointerInside || !_viewModel.IsDisplayingCurrentMonth;
        double opacity = revealed ? 1 : 0;
        if (CalendarPreviousMonthButton is not null)
        {
            CalendarPreviousMonthButton.Opacity = opacity;
            CalendarPreviousMonthButton.IsHitTestVisible = revealed;
        }

        if (CalendarNextMonthButton is not null)
        {
            CalendarNextMonthButton.Opacity = opacity;
            CalendarNextMonthButton.IsHitTestVisible = revealed;
        }
    }

    private async void CalendarTodayButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ReturnToCurrentMonthAsync();
    }

    private void ShowDayDetail(FrameworkElement anchor, DateOnly date)
    {
        GlanceCalendarDay? day = _viewModel.FindCalendarDay(date);
        IReadOnlyList<GlanceAgendaItem> agenda = _viewModel.GetAgendaFor(date);

        var panel = new StackPanel { Spacing = 6, MinWidth = 180, MaxWidth = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = _viewModel.FormatDayDetailTitle(date),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        string summary = day is null ? string.Empty : _viewModel.FormatDaySummary(day);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            panel.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (agenda.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _viewModel.FormatAgendaEmpty(),
                FontSize = 12,
                Opacity = 0.6
            });
        }
        else
        {
            foreach (GlanceAgendaItem item in agenda.Take(8))
            {
                panel.Children.Add(CreateAgendaRow(item, fontSize: 12.5, castShadow: false));
            }
        }

        _dayDetailFlyout ??= new Flyout { Placement = FlyoutPlacementMode.Bottom };
        _dayDetailFlyout.Content = panel;
        _dayDetailFlyout.ShowAt(anchor);
    }

    /// <summary>A single agenda row: color dot, title, time (or "overdue").</summary>
    private FrameworkElement CreateAgendaRow(GlanceAgendaItem item, double fontSize, bool castShadow)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush dotBrush = item.ColorHex is { } hex && TryParseColor(hex, out Windows.UI.Color color)
            ? GetOrCreateAgendaColorBrush(color)
            : ResolveAccentBrush();
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = dotBrush
        };
        row.Children.Add(dot);

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = fontSize,
            FontWeight = item.IsImportant
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetTextShadow.SetCast(title, castShadow);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        string timeText = _viewModel.FormatAgendaTime(item);
        if (!string.IsNullOrWhiteSpace(timeText))
        {
            bool overdue = item.Date < DateOnly.FromDateTime(DateTime.Today);
            var time = new TextBlock
            {
                Text = timeText,
                FontSize = fontSize - 1,
                Opacity = overdue ? 1 : 0.75,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (overdue)
            {
                time.Foreground = HolidayWorkBrush;
            }

            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            WidgetTextShadow.SetCast(time, castShadow);
            Grid.SetColumn(time, 2);
            row.Children.Add(time);
        }

        AutomationProperties.SetName(row, string.IsNullOrWhiteSpace(timeText) ? item.Title : $"{item.Title}, {timeText}");
        return row;
    }

    private Brush GetOrCreateAgendaColorBrush(Windows.UI.Color color)
    {
        uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (!_agendaColorBrushes.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(color);
            _agendaColorBrushes[key] = brush;
        }

        return brush;
    }

    private static bool TryParseColor(string hex, out Windows.UI.Color color)
    {
        color = default;
        string value = hex.TrimStart('#');
        if (value.Length != 6 ||
            !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return false;
        }

        color = Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private void RefreshTodayCard()
    {
        if (!_isLoaded)
        {
            return;
        }

        if (!_viewModel.IsTodayCardStyle)
        {
            if (TodayAgendaPanel.Children.Count > 0)
            {
                TodayAgendaPanel.Children.Clear();
            }

            TodayAgendaPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TodayHolidayBadge.Background = _viewModel.IsTodayHolidayOff ? HolidayOffBrush : HolidayWorkBrush;

        TodayAgendaPanel.Children.Clear();
        int capacity = _viewModel.TodayCardAgendaCapacity;
        if (capacity <= 0)
        {
            TodayAgendaPanel.Visibility = Visibility.Collapsed;
            return;
        }

        IReadOnlyList<GlanceAgendaItem> agenda = _viewModel.TodayAgenda;
        if (agenda.Count > 0)
        {
            foreach (GlanceAgendaItem item in agenda.Take(capacity))
            {
                TodayAgendaPanel.Children.Add(CreateAgendaRow(item, fontSize: 13, castShadow: true));
            }

            if (agenda.Count > capacity)
            {
                TodayAgendaPanel.Children.Add(CreateSecondaryLine(
                    _viewModel.FormatAgendaMore(agenda.Count - capacity)));
            }
        }
        else if (_viewModel.HasHolidayCountdown)
        {
            TodayAgendaPanel.Children.Add(CreateSecondaryLine(_viewModel.HolidayCountdownText));
        }

        TodayAgendaPanel.Visibility = TodayAgendaPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static TextBlock CreateSecondaryLine(string text)
    {
        var line = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Opacity = 0.8,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        WidgetTextShadow.SetCast(line, true);
        return line;
    }

    private void EnsureWeekStripInitialized()
    {
        if (_weekCells is not null && WeekStripDaysGrid.Children.Count == WeekStripDayCount)
        {
            return;
        }

        WeekStripDaysGrid.Children.Clear();
        WeekStripDaysGrid.ColumnDefinitions.Clear();
        var cells = new WeekDayCellVisual[WeekStripDayCount];
        for (int column = 0; column < WeekStripDayCount; column++)
        {
            WeekStripDaysGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            var root = new Grid { RowSpacing = 2, Background = TransparentBrush };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var weekday = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 10.5,
                Opacity = 0.72,
                MaxLines = 1,
                IsTextSelectionEnabled = false
            };
            root.Children.Add(weekday);

            var dayHost = new Grid { MinHeight = 34, Margin = new Thickness(2, 0, 2, 0) };
            Grid.SetRow(dayHost, 1);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 0,
                Padding = new Thickness(0, 3, 0, 5),
                IsHitTestVisible = false
            };
            var dayText = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTextSelectionEnabled = false
            };
            Typography.SetNumeralAlignment(dayText, FontNumeralAlignment.Tabular);
            var secondary = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 8.5,
                MaxLines = 1,
                MaxWidth = 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
                IsTextSelectionEnabled = false
            };
            stack.Children.Add(dayText);
            stack.Children.Add(secondary);
            var badge = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 3, 0),
                FontSize = 8,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            dayHost.Children.Add(pill);
            dayHost.Children.Add(stack);
            dayHost.Children.Add(badge);
            dayHost.Children.Add(dot);
            root.Children.Add(dayHost);

            Grid.SetColumn(root, column);
            WeekStripDaysGrid.Children.Add(root);
            var visual = new WeekDayCellVisual
            {
                Root = root,
                WeekdayText = weekday,
                TodayPill = pill,
                DayText = dayText,
                SecondaryText = secondary,
                HolidayBadge = badge,
                AgendaDot = dot
            };
            root.Tapped += (_, args) =>
            {
                if (visual.Date != default)
                {
                    args.Handled = true;
                    ShowDayDetail(visual.Root, visual.Date);
                }
            };
            cells[column] = visual;
        }

        _weekCells = cells;
    }

    private void RefreshWeekStrip()
    {
        if (!_isLoaded || !_viewModel.IsWeekStripStyle)
        {
            return;
        }

        EnsureWeekStripInitialized();
        if (_weekCells is null)
        {
            return;
        }

        IReadOnlyList<GlanceCalendarDay> week = _viewModel.WeekDays;
        IReadOnlyList<string> headers = _viewModel.WeekdayHeaders;
        Brush primaryBrush = WeekStripThemeProbe?.BorderBrush ?? Foreground;
        Brush accentBrush = ResolveAccentBrush();
        Brush onAccentBrush = WeekStripOnAccentThemeProbe?.Foreground
            ?? CalendarOnAccentThemeProbe?.Foreground
            ?? OnAccentFallbackBrush;
        bool showSecondary = _viewModel.WeekStripShowsSecondaryText;

        for (int index = 0; index < WeekStripDayCount; index++)
        {
            WeekDayCellVisual cell = _weekCells[index];
            GlanceCalendarDay? day = index < week.Count ? week[index] : null;
            cell.Date = day?.Date ?? default;
            cell.WeekdayText.Text = index < headers.Count ? headers[index] : string.Empty;
            cell.WeekdayText.Foreground = primaryBrush;
            cell.DayText.Text = day?.DayText ?? string.Empty;

            bool isToday = day?.IsToday == true || day?.Date == DateOnly.FromDateTime(DateTime.Today);
            cell.TodayPill.Background = accentBrush;
            cell.TodayPill.Visibility = isToday ? Visibility.Visible : Visibility.Collapsed;
            cell.DayText.Foreground = isToday ? onAccentBrush : primaryBrush;
            cell.SecondaryText.Foreground = isToday ? onAccentBrush : primaryBrush;

            WidgetTextShadow.SetCast(cell.DayText, !isToday);
            WidgetTextShadow.SetCast(cell.WeekdayText, true);

            string secondary = day is null
                ? string.Empty
                : !string.IsNullOrWhiteSpace(day.FestivalText) ? day.FestivalText : day.TraditionalText;
            cell.SecondaryText.Text = secondary;
            cell.SecondaryText.Opacity = isToday ? 0.86 : 0.66;
            cell.SecondaryText.Visibility = showSecondary && !string.IsNullOrWhiteSpace(secondary)
                ? Visibility.Visible
                : Visibility.Collapsed;
            WidgetTextShadow.SetCast(cell.SecondaryText, !isToday);

            ApplyHolidayBadge(cell.HolidayBadge, day?.HolidayMark ?? GlanceHolidayMark.None, isToday, onAccentBrush);
            cell.AgendaDot.Fill = isToday ? onAccentBrush : accentBrush;
            cell.AgendaDot.Visibility = day?.AgendaCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(
                cell.Root,
                day is null ? string.Empty : _viewModel.FormatDayDetailTitle(day.Date));
        }
    }

    private static readonly SolidColorBrush OnAccentFallbackBrush = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _accentFallbackBrush = new();

    /// <summary>
    /// Resolves the theme accent brush from whichever surface probe is active,
    /// falling back to the theme service when neither is realized.
    /// </summary>
    private Brush ResolveAccentBrush()
    {
        if (CalendarThemeProbe?.Background is { } probe)
        {
            return probe;
        }

        if (WeekStripThemeProbe?.Background is { } stripProbe)
        {
            return stripProbe;
        }

        _accentFallbackBrush.Color = App.Current?.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        return _accentFallbackBrush;
    }

    private void RefreshStyleFaces()
    {
        if (!_isLoaded || !_isWindowVisible || _isCompactCollapsed)
        {
            return;
        }

        RefreshTodayCard();
        RefreshWeekStrip();
    }
}
