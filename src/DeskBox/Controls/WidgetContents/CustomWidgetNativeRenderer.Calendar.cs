using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private sealed class CalendarCellSlot
    {
        public required Button Button { get; init; }
        public required TextBlock DayText { get; init; }
        public required TextBlock SubText { get; init; }
        public required TextBlock CornerBadge { get; init; }
        public required Ellipse EventDot { get; init; }
        public string TargetIso { get; set; } = string.Empty;
        public int ClickedMonthDelta { get; set; }
    }

    private FrameworkElement BuildCalendarGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, ctx).Trim();
        bool isCollapsible = string.Equals(rawVariant, "collapsible", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(rawVariant, "week", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(rawVariant, "month", StringComparison.OrdinalIgnoreCase);

        string bindKey = string.IsNullOrWhiteSpace(spec.Bind) ? "selectedDate" : spec.Bind.Trim();
        string bindStateExpr = bindKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
            ? bindKey
            : $"state.{bindKey}";

        var root = new StackPanel
        {
            Spacing = isCollapsible ? 4 : 6
        };

        // Top Header: Title + Subtitle | Prev / [Today] / Next + [Week | Month]
        var headerGrid = new Grid { ColumnSpacing = 6 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            FontSize = isCollapsible ? BodyFontSize + 0.5 : SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        var subTb = new TextBlock
        {
            FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
            Foreground = ResolveForegroundBrush("secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isCollapsible)
        {
            var inlineHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            inlineHeader.Children.Add(titleTb);
            inlineHeader.Children.Add(subTb);
            headerGrid.Children.Add(inlineHeader);
        }
        else
        {
            var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(titleTb);
            titleStack.Children.Add(subTb);
            headerGrid.Children.Add(titleStack);
        }

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = isCollapsible ? 3 : 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        int navBtnSize = isCollapsible ? 22 : 26;
        Button prevBtn = CreateSubtleIconButton("\uE76B", size: navBtnSize, iconSize: 10);
        navRow.Children.Add(prevBtn);

        var todayBtn = new Button
        {
            FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(isCollapsible ? 6 : 7, 1, isCollapsible ? 6 : 7, 1),
            MinHeight = isCollapsible ? 20 : 24,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleStandardCommandButton(todayBtn, CornerSmall);
        navRow.Children.Add(todayBtn);

        Button nextBtn = CreateSubtleIconButton("\uE76C", size: navBtnSize, iconSize: 10);
        navRow.Children.Add(nextBtn);

        Button? weekPillBtn = null;
        Button? monthPillBtn = null;
        if (isCollapsible)
        {
            weekPillBtn = new Button
            {
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                Padding = new Thickness(6, 1, 6, 1),
                MinHeight = 20,
                MinWidth = 22,
                CornerRadius = new CornerRadius(Math.Max(3, RadiusSmall - 1)),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            monthPillBtn = new Button
            {
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                Padding = new Thickness(6, 1, 6, 1),
                MinHeight = 20,
                MinWidth = 22,
                CornerRadius = new CornerRadius(Math.Max(3, RadiusSmall - 1)),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            MarkInteractiveElement(weekPillBtn);
            MarkInteractiveElement(monthPillBtn);

            var modeGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                Children = { weekPillBtn, monthPillBtn }
            };
            var modeHost = new Border
            {
                CornerRadius = CornerSmall,
                Padding = new Thickness(2),
                Margin = new Thickness(2, 0, 0, 0),
                Background = GetOrCreateSolidBrush(IsDark
                    ? Color.FromArgb(20, 255, 255, 255)
                    : Color.FromArgb(14, 15, 23, 42)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = modeGroup
            };
            navRow.Children.Add(modeHost);
        }

        Grid.SetColumn(navRow, 1);
        headerGrid.Children.Add(navRow);
        root.Children.Add(headerGrid);

        // Stationary Weekday Header Row
        var weekHeaderGrid = new Grid { ColumnSpacing = 2 };
        var weekdayTbs = new TextBlock[7];
        for (int c = 0; c < 7; c++)
        {
            weekHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var wdTb = new TextBlock
            {
                FontSize = Math.Max(9.5, CaptionFontSize - 1),
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(wdTb, c);
            weekHeaderGrid.Children.Add(wdTb);
            weekdayTbs[c] = wdTb;
        }
        root.Children.Add(weekHeaderGrid);

        // 6 Retained Week Rows (allows smooth Week <-> Month accordion without tree rebuild)
        var rowsPanel = new StackPanel
        {
            Spacing = 2,
            ChildrenTransitions = CreateRepositionTransitions()
        };
        var weekRowGrids = new Grid[6];
        var cellSlots = new CalendarCellSlot[42];

        for (int r = 0; r < 6; r++)
        {
            var rowGrid = new Grid { ColumnSpacing = 2 };
            for (int c = 0; c < 7; c++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int idx = (r * 7) + c;
                var dayTb = new TextBlock
                {
                    FontSize = isCollapsible ? CaptionFontSize + 0.5 : CaptionFontSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var subCellTb = new TextBlock
                {
                    FontSize = isCollapsible ? 8.5 : 9.5,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.Clip,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var eventDot = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Margin = new Thickness(0, 1, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var cellStack = new StackPanel
                {
                    Spacing = 0,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { dayTb, subCellTb, eventDot }
                };

                var cornerBadgeTb = new TextBlock
                {
                    FontSize = 7.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 2, 0),
                    Visibility = Visibility.Collapsed
                };
                WidgetTextShadow.SetCast(cornerBadgeTb, false);

                var cellContentGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Children = { cellStack, cornerBadgeTb }
                };

                var cellBtn = new Button
                {
                    Padding = new Thickness(1, 2, 1, 2),
                    MinWidth = 0,
                    MinHeight = isCollapsible ? 34 : 36,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    CornerRadius = CornerSmall,
                    BorderThickness = new Thickness(1),
                    Content = cellContentGrid
                };
                MarkInteractiveElement(cellBtn);

                var slot = new CalendarCellSlot
                {
                    Button = cellBtn,
                    DayText = dayTb,
                    SubText = subCellTb,
                    CornerBadge = cornerBadgeTb,
                    EventDot = eventDot
                };
                cellSlots[idx] = slot;

                Grid.SetColumn(cellBtn, c);
                rowGrid.Children.Add(cellBtn);
            }

            weekRowGrids[r] = rowGrid;
            rowsPanel.Children.Add(rowGrid);
        }
        root.Children.Add(rowsPanel);

        Border? gripPill = null;
        Button? handleBtn = null;
        if (isCollapsible)
        {
            gripPill = new Border
            {
                Width = 32,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = GetOrCreateSolidBrush(IsDark
                    ? Color.FromArgb(46, 255, 255, 255)
                    : Color.FromArgb(42, 15, 23, 42))
            };
            handleBtn = new Button
            {
                Height = 11,
                MinHeight = 11,
                MinWidth = 0,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0),
                Background = GetOrCreateSolidBrush(Colors.Transparent),
                Content = gripPill
            };
            Brush transparentBrush = GetOrCreateSolidBrush(Colors.Transparent);
            ApplyButtonStateOverrides(handleBtn, transparentBrush, transparentBrush, transparentBrush, transparentBrush);
            MarkInteractiveElement(handleBtn);
            handleBtn.PointerEntered += (_, _) =>
            {
                gripPill.Width = 44;
                gripPill.Background = GetOrCreateSolidBrush(IsDark
                    ? Color.FromArgb(95, 255, 255, 255)
                    : Color.FromArgb(88, 15, 23, 42));
            };
            handleBtn.PointerExited += (_, _) =>
            {
                gripPill.Width = 32;
                gripPill.Background = GetOrCreateSolidBrush(IsDark
                    ? Color.FromArgb(46, 255, 255, 255)
                    : Color.FromArgb(42, 15, 23, 42));
            };
            root.Children.Add(handleBtn);
        }

        // Mutable retained projection state
        CustomWidgetEvaluationContext capturedCtx = ctx;
        DateOnly capturedToday = DateOnly.FromDateTime(DateTime.Now);
        DateOnly capturedSelectedDate = capturedToday;
        int capturedMonthOffset = 0;
        bool capturedIsWeekMode = isCollapsible;

        void UpdateCalendarProjection(CustomWidgetEvaluationContext evalCtx, bool isInitialRender)
        {
            capturedCtx = evalCtx;
            string culture = evalCtx.CultureName ?? _viewModel.ActiveCultureName;
            bool isRtlCalendar = CustomWidgetLocaleCatalog.IsRightToLeftCulture(culture);

            capturedToday = DateOnly.FromDateTime(DateTime.Now);
            int monthOffset = 0;
            if (evalCtx.State.TryGetValue("calendarMonthOffset", out string? rawOffset) &&
                int.TryParse(rawOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOffset))
            {
                monthOffset = Math.Clamp(parsedOffset, -120, 120);
            }

            string rawSelected = CustomWidgetExpressionEvaluator.EvaluateExpression(bindStateExpr, evalCtx);
            DateOnly selectedDate = DateOnly.TryParse(rawSelected, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedSel)
                ? parsedSel
                : capturedToday;

            bool isWeekMode = false;
            if (isCollapsible)
            {
                if (evalCtx.State.TryGetValue("calMode", out string? rawCalMode) && !string.IsNullOrWhiteSpace(rawCalMode))
                {
                    isWeekMode = !string.Equals(rawCalMode.Trim(), "month", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    string liveVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, evalCtx).Trim();
                    isWeekMode = !string.Equals(liveVariant, "month", StringComparison.OrdinalIgnoreCase);
                }

                if (isWeekMode)
                {
                    monthOffset = ((selectedDate.Year - capturedToday.Year) * 12) + (selectedDate.Month - capturedToday.Month);
                }
            }

            capturedMonthOffset = monthOffset;
            capturedSelectedDate = selectedDate;
            capturedIsWeekMode = isWeekMode;

            DateOnly visibleMonth = new DateOnly(capturedToday.Year, capturedToday.Month, 1).AddMonths(monthOffset);

            IReadOnlyList<CustomWidgetRecord>? eventRecords = null;
            string rawCol = CustomWidgetExpressionEvaluator.Interpolate(spec.Collection ?? spec.Items, evalCtx).Trim();
            if (rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
            {
                rawCol = rawCol["collections.".Length..];
            }
            if (!string.IsNullOrWhiteSpace(rawCol) &&
                evalCtx.Collections.TryGetValue(rawCol, out List<CustomWidgetRecord>? explicitList))
            {
                eventRecords = explicitList;
            }
            else if (string.IsNullOrWhiteSpace(rawCol) &&
                     evalCtx.Collections.TryGetValue("events", out List<CustomWidgetRecord>? defaultEvents))
            {
                eventRecords = defaultEvents;
            }

            CustomWidgetCalendarMonthView monthView = CustomWidgetCalendarEngine.BuildMonthView(
                visibleMonth,
                capturedToday,
                selectedDate,
                culture,
                eventRecords,
                spec.DateField);

            if (prevBtn.Content is FontIcon prevFi)
            {
                prevFi.Glyph = isRtlCalendar ? "\uE76C" : "\uE76B";
            }
            if (nextBtn.Content is FontIcon nextFi)
            {
                nextFi.Glyph = isRtlCalendar ? "\uE76B" : "\uE76C";
            }

            ToolTipService.SetToolTip(prevBtn, ResolveCalendarNavTooltip(culture, isWeekMode, isPrevious: true));
            ToolTipService.SetToolTip(nextBtn, ResolveCalendarNavTooltip(culture, isWeekMode, isPrevious: false));

            todayBtn.Content = LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.Calendar.Today");
            bool isAwayFromToday = monthOffset != 0 || selectedDate != capturedToday;
            todayBtn.Visibility = isAwayFromToday ? Visibility.Visible : Visibility.Collapsed;

            bool showHolidayBadges = !evalCtx.Settings.TryGetValue("showHolidayBadge", out string? rawShowHoliday) ||
                                     !string.Equals(rawShowHoliday.Trim(), "false", StringComparison.OrdinalIgnoreCase);

            CustomWidgetCalendarCell? selectedCell = monthView.Cells.FirstOrDefault(c => c.Date == selectedDate);
            if (isCollapsible)
            {
                string selectedTitle = FormatSelectedDateWithWeekday(selectedDate, culture);
                string selectedSub = selectedCell?.SubtitleText ?? string.Empty;
                if (isWeekMode)
                {
                    titleTb.Text = selectedTitle;
                    if (showHolidayBadges &&
                        CustomWidgetCalendarEngine.TryGetNextStatutoryHolidayCountdown(
                            capturedToday,
                            culture,
                            out string holName,
                            out int holDays))
                    {
                        string holSuffix = holDays == 0
                            ? holName
                            : FormatHolidayCountdown(culture, holName, holDays);
                        subTb.Text = string.IsNullOrWhiteSpace(selectedSub) ? holSuffix : $"{selectedSub} · {holSuffix}";
                    }
                    else
                    {
                        subTb.Text = selectedSub;
                    }
                }
                else
                {
                    titleTb.Text = monthView.MonthTitle;
                    subTb.Text = string.IsNullOrWhiteSpace(selectedSub)
                        ? selectedTitle
                        : $"{selectedTitle} · {selectedSub}";
                }
                subTb.Visibility = string.IsNullOrWhiteSpace(subTb.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                titleTb.Text = monthView.MonthTitle;
                subTb.Text = monthView.TraditionalTitle;
                subTb.Visibility = string.IsNullOrWhiteSpace(monthView.TraditionalTitle)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (weekPillBtn is not null && monthPillBtn is not null)
            {
                (string weekShort, string monthShort) = ResolveWeekMonthLabels(culture);
                weekPillBtn.Content = weekShort;
                monthPillBtn.Content = monthShort;

                Brush activePillBg = GetOrCreateSolidBrush(IsDark
                    ? Color.FromArgb(46, 255, 255, 255)
                    : Color.FromArgb(230, 255, 255, 255));
                Brush activePillFg = ResolveForegroundBrush("default");
                Brush inactivePillFg = ResolveForegroundBrush("secondary");
                Brush transparentPill = GetOrCreateSolidBrush(Colors.Transparent);

                weekPillBtn.Background = isWeekMode ? activePillBg : transparentPill;
                weekPillBtn.Foreground = isWeekMode ? activePillFg : inactivePillFg;
                weekPillBtn.FontWeight = isWeekMode ? FontWeights.SemiBold : FontWeights.Normal;

                monthPillBtn.Background = !isWeekMode ? activePillBg : transparentPill;
                monthPillBtn.Foreground = !isWeekMode ? activePillFg : inactivePillFg;
                monthPillBtn.FontWeight = !isWeekMode ? FontWeights.SemiBold : FontWeights.Normal;

                if (isWeekMode)
                {
                    ApplyButtonStateOverrides(weekPillBtn, activePillBg, activePillBg, transparentPill, transparentPill);
                    ApplyButtonStateOverrides(monthPillBtn, SubtleHoverBrush, SubtlePressedBrush, transparentPill, transparentPill);
                }
                else
                {
                    ApplyButtonStateOverrides(monthPillBtn, activePillBg, activePillBg, transparentPill, transparentPill);
                    ApplyButtonStateOverrides(weekPillBtn, SubtleHoverBrush, SubtlePressedBrush, transparentPill, transparentPill);
                }
            }

            for (int c = 0; c < 7; c++)
            {
                weekdayTbs[c].Text = c < monthView.WeekdayHeaders.Count ? monthView.WeekdayHeaders[c] : string.Empty;
                bool colIsWeekend = c < monthView.Cells.Count && monthView.Cells[c].IsWeekend;
                weekdayTbs[c].Foreground = colIsWeekend && isCollapsible
                    ? ResolveSemanticBrush("cyan")
                    : ResolveForegroundBrush("secondary");
                weekdayTbs[c].Opacity = colIsWeekend && isCollapsible ? 0.85 : 1.0;
            }

            int activeWeekRow = 0;
            if (isWeekMode)
            {
                int selIdx = -1;
                for (int i = 0; i < monthView.Cells.Count; i++)
                {
                    if (monthView.Cells[i].Date == selectedDate)
                    {
                        selIdx = i;
                        break;
                    }
                }
                if (selIdx < 0)
                {
                    for (int i = 0; i < monthView.Cells.Count; i++)
                    {
                        if (monthView.Cells[i].IsToday)
                        {
                            selIdx = i;
                            break;
                        }
                    }
                }
                activeWeekRow = selIdx >= 0 ? Math.Clamp(selIdx / 7, 0, 5) : 0;
            }

            Color primaryColor = isCollapsible
                ? (IsDark ? Color.FromArgb(255, 16, 185, 129) : Color.FromArgb(255, 5, 150, 105))
                : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
            Brush selectedFill = isCollapsible
                ? GetOrCreateSolidBrush(primaryColor)
                : GetBrush("AccentFillColorDefaultBrush", primaryColor);
            Brush onSelectedFg = GetOrCreateSolidBrush(Colors.White);
            Brush todayOutlineBg = GetOrCreateSolidBrush(
                Color.FromArgb((byte)(IsDark ? 36 : 24), primaryColor.R, primaryColor.G, primaryColor.B));
            Brush todayOutlineBorder = GetOrCreateSolidBrush(
                Color.FromArgb(185, primaryColor.R, primaryColor.G, primaryColor.B));
            Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
            Brush coralDotBrush = GetOrCreateSolidBrush(Color.FromArgb(255, 244, 63, 94));
            Brush emeraldDotBrush = ResolveSemanticBrush("emerald");

            int totalRows = Math.Clamp((monthView.Cells.Count + 6) / 7, 1, 6);
            for (int r = 0; r < 6; r++)
            {
                bool rowShouldShow = r < totalRows && (!isWeekMode || r == activeWeekRow);
                Visibility desiredVis = rowShouldShow ? Visibility.Visible : Visibility.Collapsed;
                bool justRevealed = !isInitialRender &&
                                    weekRowGrids[r].Visibility == Visibility.Collapsed &&
                                    desiredVis == Visibility.Visible;
                weekRowGrids[r].Visibility = desiredVis;
                if (justRevealed)
                {
                    FluentMotionHelper.PlaySubtleFadeIn(weekRowGrids[r], durationMs: 150);
                }

                if (!rowShouldShow)
                {
                    continue;
                }

                for (int c = 0; c < 7; c++)
                {
                    int idx = (r * 7) + c;
                    CalendarCellSlot slot = cellSlots[idx];
                    if (idx >= monthView.Cells.Count)
                    {
                        slot.Button.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    slot.Button.Visibility = Visibility.Visible;
                    CustomWidgetCalendarCell cell = monthView.Cells[idx];

                    bool isSelectedHighlight = isCollapsible ? cell.IsSelected : cell.IsToday;
                    bool isSecondaryRing = isCollapsible ? (cell.IsToday && !cell.IsSelected) : (cell.IsSelected && !cell.IsToday);

                    slot.DayText.Text = cell.DayText;
                    slot.DayText.FontWeight = cell.IsToday || cell.IsSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    slot.DayText.Foreground = isSelectedHighlight
                        ? onSelectedFg
                        : (isSecondaryRing
                            ? GetOrCreateSolidBrush(primaryColor)
                            : (cell.IsWeekend && cell.IsCurrentMonth
                                ? ResolveForegroundBrush("secondary")
                                : ResolveForegroundBrush("default")));
                    WidgetTextShadow.SetCast(slot.DayText, !isSelectedHighlight);

                    slot.SubText.Text = cell.SubtitleText;
                    slot.SubText.Visibility = string.IsNullOrWhiteSpace(cell.SubtitleText)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    slot.SubText.Foreground = isSelectedHighlight
                        ? onSelectedFg
                        : (cell.IsFestival
                            ? (isCollapsible ? ResolveSemanticBrush("warning") : ResolveSemanticBrush("accent"))
                            : ResolveForegroundBrush("secondary"));
                    slot.SubText.Opacity = isSelectedHighlight ? 0.92 : 0.85;
                    WidgetTextShadow.SetCast(slot.SubText, !isSelectedHighlight);

                    if (showHolidayBadges && !isSelectedHighlight && !string.IsNullOrWhiteSpace(cell.CornerBadgeKind))
                    {
                        (string badgeText, Brush badgeBrush, double badgeOpacity) = cell.CornerBadgeKind switch
                        {
                            "holiday" => ("休", emeraldDotBrush, 0.95),
                            "swapWork" => ("班", ResolveSemanticBrush("warning"), 0.95),
                            _ => ("休", ResolveSemanticBrush("cyan"), 0.78)
                        };
                        slot.CornerBadge.Text = badgeText;
                        slot.CornerBadge.Foreground = badgeBrush;
                        slot.CornerBadge.Opacity = badgeOpacity;
                        slot.CornerBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        slot.CornerBadge.Visibility = Visibility.Collapsed;
                    }

                    slot.EventDot.Opacity = cell.EventCount > 0 ? 1.0 : 0.0;
                    slot.EventDot.Fill = isSelectedHighlight
                        ? onSelectedFg
                        : (cell.HasHighPriorityEvent
                            ? coralDotBrush
                            : (isCollapsible ? emeraldDotBrush : ResolveSemanticBrush("accent")));

                    slot.Button.Opacity = isWeekMode || cell.IsCurrentMonth ? 1.0 : 0.36;
                    slot.Button.Background = isSelectedHighlight
                        ? selectedFill
                        : (isSecondaryRing ? todayOutlineBg : transparent);
                    slot.Button.BorderBrush = isSecondaryRing ? todayOutlineBorder : transparent;

                    if (isSelectedHighlight)
                    {
                        ApplyButtonStateOverrides(slot.Button, selectedFill, selectedFill, transparent, transparent);
                    }
                    else if (isSecondaryRing)
                    {
                        ApplyButtonStateOverrides(slot.Button, todayOutlineBg, todayOutlineBg, todayOutlineBorder, todayOutlineBorder);
                    }
                    else
                    {
                        ApplyButtonStateOverrides(slot.Button, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
                    }

                    if (cell.EventCount > 0 && !string.IsNullOrWhiteSpace(cell.FirstEventTitle))
                    {
                        string tip = cell.EventCount > 1
                            ? $"{cell.IsoDate} · {cell.FirstEventTitle} (+{cell.EventCount - 1})"
                            : $"{cell.IsoDate} · {cell.FirstEventTitle}";
                        ToolTipService.SetToolTip(slot.Button, tip);
                    }
                    else
                    {
                        ToolTipService.SetToolTip(slot.Button, null);
                    }

                    slot.TargetIso = cell.IsoDate;
                    slot.ClickedMonthDelta = ((cell.Date.Year - visibleMonth.Year) * 12) + (cell.Date.Month - visibleMonth.Month);
                }
            }
        }

        // Wire retained event handlers
        prevBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (capturedIsWeekMode)
            {
                DateOnly target = capturedSelectedDate.AddDays(-7);
                int targetOffset = ((target.Year - capturedToday.Year) * 12) + (target.Month - capturedToday.Month);
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: targetOffset.ToString(CultureInfo.InvariantCulture))
                ], capturedCtx);
                await ExecuteCalendarSelectionCallbacksAsync(spec);
            }
            else
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: (capturedMonthOffset - 1).ToString(CultureInfo.InvariantCulture))
                ], capturedCtx);
            }
        });

        nextBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (capturedIsWeekMode)
            {
                DateOnly target = capturedSelectedDate.AddDays(7);
                int targetOffset = ((target.Year - capturedToday.Year) * 12) + (target.Month - capturedToday.Month);
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: targetOffset.ToString(CultureInfo.InvariantCulture))
                ], capturedCtx);
                await ExecuteCalendarSelectionCallbacksAsync(spec);
            }
            else
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: (capturedMonthOffset + 1).ToString(CultureInfo.InvariantCulture))
                ], capturedCtx);
            }
        });

        todayBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
        {
            string todayIso = capturedToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await _viewModel.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: "0"),
                new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: todayIso)
            ], capturedCtx);
            await ExecuteCalendarSelectionCallbacksAsync(spec);
        });

        if (weekPillBtn is not null)
        {
            weekPillBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                if (!capturedIsWeekMode)
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: "calMode", Value: "week")
                    ], capturedCtx);
                }
            });
        }

        if (monthPillBtn is not null)
        {
            monthPillBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                if (capturedIsWeekMode)
                {
                    int targetOffset = ((capturedSelectedDate.Year - capturedToday.Year) * 12) + (capturedSelectedDate.Month - capturedToday.Month);
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: "calMode", Value: "month"),
                        new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: targetOffset.ToString(CultureInfo.InvariantCulture))
                    ], capturedCtx);
                }
            });
        }

        if (handleBtn is not null)
        {
            handleBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                string nextMode = capturedIsWeekMode ? "month" : "week";
                int targetOffset = ((capturedSelectedDate.Year - capturedToday.Year) * 12) + (capturedSelectedDate.Month - capturedToday.Month);
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: "calMode", Value: nextMode),
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: targetOffset.ToString(CultureInfo.InvariantCulture))
                ], capturedCtx);
            });
        }

        foreach (CalendarCellSlot slot in cellSlots)
        {
            CalendarCellSlot capturedSlot = slot;
            capturedSlot.Button.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                if (string.IsNullOrWhiteSpace(capturedSlot.TargetIso))
                {
                    return;
                }

                var batch = new List<CustomWidgetActionSpec>
                {
                    new(ResolveBindSetActionType(bindKey), Key: bindKey, Value: capturedSlot.TargetIso)
                };
                if (capturedSlot.ClickedMonthDelta != 0)
                {
                    batch.Add(new("state.set", Key: "calendarMonthOffset", Value: (capturedMonthOffset + capturedSlot.ClickedMonthDelta).ToString(CultureInfo.InvariantCulture)));
                }
                await _viewModel.ExecuteActionsAsync(batch, capturedCtx);
                await ExecuteCalendarSelectionCallbacksAsync(spec);
            });
        }

        UpdateCalendarProjection(ctx, isInitialRender: true);
        RegisterRetainedPatcher(liveCtx => UpdateCalendarProjection(liveCtx, isInitialRender: false));
        return root;
    }

    private async Task ExecuteCalendarSelectionCallbacksAsync(CustomWidgetNodeSpec spec)
    {
        if (spec.OnChange.Count > 0)
        {
            await _viewModel.ExecuteActionsAsync(spec.OnChange);
        }
        else if (spec.OnClick.Count > 0)
        {
            await _viewModel.ExecuteActionsAsync(spec.OnClick);
        }
    }

    private static string FormatSelectedDateWithWeekday(DateOnly date, string cultureName)
    {
        CultureInfo culture;
        try
        {
            culture = LocalizationService.EnsureGregorianCalendar(CultureInfo.GetCultureInfo(cultureName));
        }
        catch
        {
            culture = CultureInfo.InvariantCulture;
        }

        DateTime dt = date.ToDateTime(TimeOnly.MinValue);
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{date.Month}月{date.Day}日 {dt.ToString("ddd", culture)}";
        }

        if (culture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
            culture.Name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return dt.ToString("M/d (ddd)", culture);
        }

        return dt.ToString("MMM d · ddd", culture);
    }

    private static (string WeekLabel, string MonthLabel) ResolveWeekMonthLabels(string cultureName)
    {
        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return ("周", "月");
        }

        if (cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return ("週", "月");
        }

        if (cultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return ("주", "월");
        }

        return ("W", "M");
    }

    private static string ResolveCalendarNavTooltip(string cultureName, bool isWeekMode, bool isPrevious)
    {
        if (!isWeekMode)
        {
            string key = isPrevious
                ? "CustomWidget.Renderer.Calendar.PreviousMonth"
                : "CustomWidget.Renderer.Calendar.NextMonth";
            return LocalizationService.TranslateForCulture(cultureName, key);
        }

        if (cultureName.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            return isPrevious ? "上一週" : "下一週";
        }

        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return isPrevious ? "上一周" : "下一周";
        }

        if (cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return isPrevious ? "前の週" : "次の週";
        }

        if (cultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return isPrevious ? "이전 주" : "다음 주";
        }

        return isPrevious ? "Previous week" : "Next week";
    }

    private static string FormatHolidayCountdown(string cultureName, string holidayName, int daysUntil)
    {
        if (cultureName.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            return $"距{holidayName} {daysUntil}天";
        }

        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"距{holidayName} {daysUntil}天";
        }

        return $"{holidayName} · {daysUntil}d";
    }
}
