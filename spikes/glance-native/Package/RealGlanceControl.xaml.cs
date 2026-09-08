using System.Text.Json;
using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Glance.NativePackage;

/// <summary>
/// Compiled-XAML (XBF) variant of the real-Glance slice. The XAML restores the
/// production converter pattern and ThemeResource references; day decoration
/// uses the production GlanceCalendarDayDecoration record directly.
/// </summary>
public sealed partial class RealGlanceControl : UserControl
{
    private readonly DeskBox.Models.GlanceCalendarMonth _month;
    private readonly double _dayItemHeight;
    private readonly bool _showSecondaryText;
    private readonly System.Globalization.CultureInfo _culture = RealGlanceModel.Culture;
    private readonly DateOnly _pinnedMonth;
    private int _decorated;

    public RealGlanceControl()
    {
        InitializeComponent();
        (_month, bool isCompact, double panelHeight, double panelWidth, double dayItemHeight, bool showSecondaryText) = RealGlanceModel.Build();
        _dayItemHeight = dayItemHeight;
        _showSecondaryText = showSecondaryText;
        _pinnedMonth = new DateOnly(RealGlanceModel.PinnedYear, RealGlanceModel.PinnedMonth, 1);
        DataContext = RealGlanceModel.CreatePresentation(_month, isCompact, panelHeight, panelWidth);
        Loaded += (_, _) =>
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(600);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                WriteSummary();
            };
            timer.Start();
        };
    }

    private void OnDayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        CalendarViewDayItem item = args.Item;
        if (args.InRecycleQueue)
        {
            item.Tag = null;
            return;
        }
        // Replicates GlanceWidgetContent.ApplyCalendarDayDecoration with the
        // production decoration record (converters resolve in compiled XAML).
        DateOnly date = DateOnly.FromDateTime(item.Date.DateTime);
        DeskBox.Models.GlanceCalendarDay? day = null;
        foreach (DeskBox.Models.GlanceCalendarDay candidate in _month.Days)
        {
            if (candidate.Date == date) { day = candidate; break; }
        }
        string secondaryText = _showSecondaryText
            ? !string.IsNullOrWhiteSpace(day?.FestivalText)
                ? day.FestivalText
                : day?.TraditionalText ?? string.Empty
            : string.Empty;
        bool hasSecondaryText = !string.IsNullOrWhiteSpace(secondaryText);
        bool isFestival = hasSecondaryText && day?.HasFestival == true;
        bool isCurrentMonth = day?.IsCurrentMonth ?? date.Month == _pinnedMonth.Month;
        item.MinHeight = _dayItemHeight;
        item.Height = _dayItemHeight;
        item.Tag = new GlanceCalendarDayDecoration(
            day?.DayText ?? date.Day.ToString(_culture),
            secondaryText,
            hasSecondaryText,
            date == DateOnly.FromDateTime(DateTime.Today),
            isFestival,
            isCurrentMonth ? 1.0 : 0.42,
            !isCurrentMonth ? 0.34 : isFestival ? 0.88 : 0.62);
        if (hasSecondaryText) _decorated++;
    }

    private void WriteSummary()
    {
        // The runtime-text slice writes real-summary.json into the package
        // directory; the compiled variant keeps the same contract but with its
        // own file so both slices can run in one process.
        string path = Path.Combine(PackageContext.Root, "compiled-summary.json");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("variant", "compiled-xbf");
        writer.WriteString("pinnedMonth", $"{RealGlanceModel.PinnedYear:0000}-{RealGlanceModel.PinnedMonth:00}");
        writer.WriteString("traditionalTitle", _month.TraditionalTitle);
        writer.WriteNumber("traditionalTextDayCount",
            _month.Days.Count(day => !string.IsNullOrWhiteSpace(day.TraditionalText)));
        writer.WriteStartArray("festivalDays");
        foreach (DeskBox.Models.GlanceCalendarDay day in _month.Days)
        {
            if (day.HasFestival) writer.WriteStringValue($"{day.Date:yyyy-MM-dd} {day.FestivalText}");
        }
        writer.WriteEndArray();
        writer.WriteNumber("decoratedDayCount", _decorated);
        writer.WriteEndObject();
    }
}

/// <summary>Set by the export before construction: package directory for diagnostics.</summary>
internal static class PackageContext
{
    public static string Root { get; set; } = "";
}
