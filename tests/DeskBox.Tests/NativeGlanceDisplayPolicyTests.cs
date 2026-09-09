extern alias GlancePkg;

using System.Globalization;

namespace DeskBox.Tests;

using Display = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDisplayPolicy;
using NativeLayout = GlancePkg::DeskBox.GlancePackage.Rendering.NativeLayout;
using GlanceTimeFormatMode = GlancePkg::DeskBox.Models.GlanceTimeFormatMode;

/// <summary>
/// Display composition parity (behavior parity batch 1): the ported
/// FormatTimeText / FormatDateText / FormatCompactCalendarDateText must
/// produce the same strings as the built-in view model for the same inputs.
/// </summary>
public class NativeGlanceDisplayPolicyTests
{
    private static readonly DateTime Sample = new(2026, 9, 9, 15, 5, 0); // 3:05 PM
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    [Fact]
    public void Hour24AndHour12UseExplicitPatterns()
    {
        Assert.Equal("15:05", Display.FormatTimeText(Sample, GlanceTimeFormatMode.Hour24, En));
        Assert.Equal("3:05", Display.FormatTimeText(Sample, GlanceTimeFormatMode.Hour12, En));
    }

    [Fact]
    public void FollowSystemUsesSystemCultureAndStripsAmPm()
    {
        // en-US ShortTimePattern is "h:mm tt"; FollowSystem strips the
        // AM/PM designator but keeps the system culture's 12-hour numbers.
        string text = Display.FormatTimeText(
            Sample, GlanceTimeFormatMode.FollowSystem, Zh, systemCulture: En);
        Assert.Equal("3:05", text);
        Assert.DoesNotContain("AM", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PM", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DateTextWithoutYearUsesCultureMonthDay()
    {
        string text = Display.FormatDateText(Sample, Zh, includeYear: false);
        Assert.Equal("9月9日", text);
    }

    [Fact]
    public void DateTextWithYearUsesLongDatePatternWithoutWeekday()
    {
        string text = Display.FormatDateText(Sample, En, includeYear: true);
        // en-US LongDatePattern "dddd, MMMM d, yyyy" minus weekday.
        Assert.Equal("September 9, 2026", text);
        Assert.DoesNotContain("Wednesday", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompactDateSuffixFollowsLocale()
    {
        Assert.Equal("9日", Display.FormatCompactCalendarDateText(Sample, Zh));
        Assert.Equal("9日", Display.FormatCompactCalendarDateText(Sample, CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("9", Display.FormatCompactCalendarDateText(Sample, En));
    }

    [Fact]
    public void LayoutResolutionMatchesBuiltIn()
    {
        var immersive = GlancePkg::DeskBox.Models.GlanceLayoutMode.Immersive;
        var centered = GlancePkg::DeskBox.Models.GlanceLayoutMode.Centered;
        var editorial = GlancePkg::DeskBox.Models.GlanceLayoutMode.Editorial;
        var calendar = GlancePkg::DeskBox.Models.GlanceLayoutMode.Calendar;

        Assert.Equal(NativeLayout.Immersive, Display.ResolveLayout(immersive, showCalendarEffective: true));
        Assert.Equal(NativeLayout.Centered, Display.ResolveLayout(centered, showCalendarEffective: false));
        Assert.Equal(NativeLayout.Editorial, Display.ResolveLayout(editorial, showCalendarEffective: false));
        // Calendar degrades to Immersive when the calendar is off or below
        // the responsive floor.
        Assert.Equal(NativeLayout.Calendar, Display.ResolveLayout(calendar, showCalendarEffective: true));
        Assert.Equal(NativeLayout.Immersive, Display.ResolveLayout(calendar, showCalendarEffective: false));
    }

    [Fact]
    public void TimeFontSizeClampsToTheBuiltInRange()
    {
        // 440x560: min(79.2, 156.8) -> clamp 78.
        Assert.Equal(78, Display.TimeFontSize(440, 560));
        // Tiny surface: clamps at the 38 floor.
        Assert.Equal(38, Display.TimeFontSize(120, 120));
        // Mid-size: min(0.18w, 0.28h) rounded to the half-step.
        Assert.Equal(45.5, Display.TimeFontSize(253, 162.5));
    }

    [Fact]
    public void TwelveHourSurvivesQuotedLiterals()
    {
        // A pattern with a quoted literal containing "t" must not lose the
        // literal - the designator stripper respects quotes.
        var culture = (CultureInfo)En.Clone();
        culture.DateTimeFormat.ShortTimePattern = "'at' h:mm tt";
        string text = Display.FormatTimeText(Sample, GlanceTimeFormatMode.FollowSystem, culture, systemCulture: culture);
        Assert.Equal("at 3:05", text);
    }
}
