using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class GlanceCalendarAndImageServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(360, 247, 1, 874)]
    [InlineData(360, 247, 2, 1747)]
    [InlineData(100, 100, 1, GlanceImageDecodeSizeCalculator.MinimumDecodePixelWidth)]
    [InlineData(2000, 1400, 2, GlanceImageDecodeSizeCalculator.MaximumDecodePixelWidth)]
    public void ImageDecodeSize_UsesBoundedPhysicalSupersampling(
        double width,
        double height,
        double scale,
        int expected)
    {
        Assert.Equal(expected, GlanceImageDecodeSizeCalculator.Calculate(width, height, scale));
    }

    [Theory]
    [InlineData(0, 874, true)]
    [InlineData(874, 900, true)]
    [InlineData(1747, 874, true)]
    [InlineData(1000, 850, false)]
    [InlineData(1000, 800, true)]
    [InlineData(1000, 0, false)]
    public void ImageDecodeSize_RefreshesForGrowthAndMeaningfulShrink(
        int current,
        int required,
        bool expected)
    {
        Assert.Equal(expected, GlanceImageDecodeSizeCalculator.NeedsRefresh(current, required));
    }

    [Fact]
    public async Task CalendarMonth_AlwaysBuildsSixCompleteWeeks()
    {
        var source = new LocalCalendarPresentationSource();

        GlanceCalendarMonth month = await source.GetMonthAsync(
            new DateOnly(2026, 8, 1),
            CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Equal(new DateOnly(2026, 8, 1), month.Month);
        Assert.Equal(7, month.WeekdayHeaders.Count);
        Assert.Equal(["一", "二", "三", "四", "五", "六", "日"], month.WeekdayHeaders);
        Assert.Equal(42, month.Days.Count);
        Assert.Contains(month.Days, day => day.Date == new DateOnly(2026, 8, 1) && day.IsCurrentMonth);
        Assert.All(month.Days, day => Assert.False(string.IsNullOrWhiteSpace(day.DayText)));
        Assert.Empty(await source.GetAgendaAsync(
            new DateOnly(2026, 8, 1),
            7,
            CultureInfo.GetCultureInfo("zh-CN")));
    }

    [Fact]
    public void CalendarSurface_UsesAdaptiveGlassLayoutAndSystemAccent()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));
        string visualCalculator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetMaterialVisualCalculator.cs"));
        string backdrop = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.Backdrop.cs"));
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string settingsCodeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        Assert.Contains("x:Name=\"CalendarGlassSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AccentFillColorDefaultBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{Binding CalendarPanelHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"{Binding CalendarPanelMaxWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding CalendarPanelWidth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NativeCalendarView\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CalendarViewDayItemStyle=\"{StaticResource GlanceCalendarDayItemStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CalendarViewDayItemChanging=\"NativeCalendarView_DayItemChanging\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NumberOfWeeksInView=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DayOfWeekFormat=\"{}{dayofweek.abbreviated(1)}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"{Binding CalendarCornerRadius}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GlanceCalendarAcrylicBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarMaterialSurface\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemBackdropElement", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TintOpacity=\"0.06\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TintLuminosityOpacity=\"0.24\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.88\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateAcrylic", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialOpacityProfile profile = CalculateMica", visualCalculator, StringComparison.Ordinal);
        Assert.Contains("BuildEmbeddedMicaTintOverlayColor", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildEmbeddedMicaTintOverlayColor", visualCalculator, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialSurface.Background = _calendarSolidMaterialBrush", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemBackdrop =", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildContentSolidSurfaceColor", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialOpacity", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialIntensity", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialType", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialMode", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarImageMaterialTransparency", viewModel, StringComparison.Ordinal);
        Assert.Contains("BackgroundImageTransparency", viewModel, StringComparison.Ordinal);
        Assert.Contains("BackgroundImageOpacity", viewModel, StringComparison.Ordinal);
        Assert.Contains("HasVisibleCurrentImage", viewModel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BackgroundImageLayer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyBackgroundImageOpacity", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialComboBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("CalendarImageTransparencySlider", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BackgroundImageTransparencySlider", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("BackgroundImageTransparencySlider_ValueChanged", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("_imageTransparencySaveTimer", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("TraditionalCalendarComboBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ChineseFestivalCard\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowChineseFestivalsToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ShowChineseFestivalsToggle_Toggled", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisplayContentDropDownButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DisplayContentDropDown_Click\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LocalSourceCard\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.HeaderKey=\"Glance.Background.Files\"", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("svc:Localized.HeaderKey=\"Glance.Background.LocalSummary\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SettingsMultiSelectMenu.Show", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("when _settings.LocalImagePaths.Count > 0", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Localization.T(\"Glance.Status.NoLocalImages\")", settingsCodeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("<toolkit:SettingsExpander", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.LayoutGroup.Title", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.AppearanceGroup.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.Typography.Font", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.Background.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.Background.Transparency.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.Background.Transparency.Description", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewCalendar", settingsXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SectionTitleTextStyle", settingsXaml, StringComparison.Ordinal);
        int traditionalNoneOption = settingsCodeBehind.IndexOf(
            "(GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.None), GlanceTraditionalCalendarMode.None)",
            StringComparison.Ordinal);
        int traditionalAutoOption = settingsCodeBehind.IndexOf(
            "Localization.T(\"Glance.TraditionalCalendar.Auto\")",
            StringComparison.Ordinal);
        Assert.True(traditionalNoneOption >= 0 && traditionalNoneOption < traditionalAutoOption);
        Assert.DoesNotContain("SliderSettingValueTextStyle", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Glance.CalendarMaterial.FollowImage", settingsCodeBehind, StringComparison.Ordinal);
        int layoutOption = settingsXaml.IndexOf("x:Name=\"LayoutComboBox\"", StringComparison.Ordinal);
        int calendarMaterialOption = settingsXaml.IndexOf("x:Name=\"CalendarMaterialCard\"", StringComparison.Ordinal);
        int traditionalCalendarOption = settingsXaml.IndexOf("x:Name=\"TraditionalCalendarCard\"", StringComparison.Ordinal);
        Assert.True(layoutOption >= 0 && layoutOption < calendarMaterialOption);
        Assert.True(calendarMaterialOption < traditionalCalendarOption);
        Assert.Contains("Glance.Rotation.10Seconds", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Glance.Rotation.30Seconds", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Glance.Rotation.60Seconds", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Glance.Rotation.2Minutes", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("Glance.Rotation.5Minutes", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildImagePaletteGradient", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateAcrylic", backdrop, StringComparison.Ordinal);
        Assert.Contains("WidgetMaterialVisualCalculator.CalculateMica", backdrop, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarReadabilityLayer\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#34516F", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#726B85", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#C18A72", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowNonCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowExpandedCalendarImageReadability", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ImageForegroundThemeScope\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ApplyImageAwareTheme", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GlanceCalendarDayItemStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CalendarItemCornerRadius=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TodayBackground=\"{ThemeResource AccentFillColorDefaultBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag.SecondaryText", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag.HasSecondaryText", xaml, StringComparison.Ordinal);
        Assert.Contains("day?.FestivalText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("day?.TraditionalText", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TraditionalCalendarTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("LineHeight=\"10\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0.86\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"6\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"14,8\" RowSpacing=\"3\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemControlAcrylicElementBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TraditionalCalendarTitlePresenter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#38FFFFFF\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderThickness=\"1\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ColumnDefinition Width=\"0.8*\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetCompactBoundsCalculator.ResolveOuterCornerRadius", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarPanelMaximumWidth = 360", viewModel, StringComparison.Ordinal);
        Assert.Contains("SetDisplayedCalendarMonthAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GlanceCalendarNavigationResolver.ResolveDisplayedMonth", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GlanceCalendarNavigationResolver.ResolveWheelTarget", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MonthViewScrollViewer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode = ScrollMode.Disabled", codeBehind, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryInvokeNativeCalendarNavigationButton", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ButtonAutomationPeer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IInvokeProvider", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CalendarViewDisplayMode.Month", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsCompactCalendarPresentation", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsExpandedCalendarPresentation", viewModel, StringComparison.Ordinal);
        Assert.Contains("CompactCalendarDateText", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"27\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"13,7,6,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"0,5,0,-5\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowCalendarTraditionalDetails", viewModel, StringComparison.Ordinal);
        Assert.Contains("CalendarDayItemMinimumHeight", viewModel, StringComparison.Ordinal);
        Assert.Contains("item.Height = itemHeight", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_calendarDensityRefreshTimer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("QueueCalendarDensityRefresh", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_layoutResizeTimer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LayoutResizeTimer_Tick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_layoutResizeTimer.Stop();\n        _layoutResizeTimer.Start();", codeBehind.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("_viewModel.UpdateAvailableSize(e.NewSize.Width, e.NewSize.Height)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CalendarViewHeaderNavigationButtonPadding\">8,6,8,6", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GlanceLocalization_AllLanguagesContainTheCompleteFeatureSet()
    {
        string stringsDirectory = Path.GetDirectoryName(TestPaths.FromRepository(
            "src/DeskBox/Strings/en-US.json"))!;
        Dictionary<string, string> english = ReadLocalizedStrings(
            Path.Combine(stringsDirectory, "en-US.json"));
        string[] glanceKeys = english.Keys
            .Where(key => key.StartsWith("Glance.", StringComparison.Ordinal) ||
                          key is "Widget.Settings.Glance" or
                              "WidgetContent.Glance.StatusLabel" or
                              "WidgetContent.Glance.StatusDescription")
            .ToArray();

        foreach (string file in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            Dictionary<string, string> localized = ReadLocalizedStrings(file);
            Assert.All(glanceKeys, key =>
            {
                Assert.True(localized.TryGetValue(key, out string? value),
                    $"{Path.GetFileName(file)} is missing {key}");
                Assert.False(string.IsNullOrWhiteSpace(value),
                    $"{Path.GetFileName(file)} has an empty value for {key}");
            });

            if (!file.EndsWith("en-US.json", StringComparison.OrdinalIgnoreCase))
            {
                Assert.NotEqual(english["Glance.Background.Title"], localized["Glance.Background.Title"]);
                Assert.NotEqual(english["Glance.Actions.Next"], localized["Glance.Actions.Next"]);
            }
        }
    }

    [Fact]
    public void TraditionalCalendar_AutomaticModeUsesDeskBoxLanguage()
    {
        var service = new GlanceTraditionalCalendarService();

        Assert.Equal(GlanceTraditionalCalendarMode.ChineseLunar,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "zh-CN"));
        Assert.Equal(GlanceTraditionalCalendarMode.ChineseLunar,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "zh-TW"));
        Assert.Equal(GlanceTraditionalCalendarMode.UmAlQura,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ar-SA"));
        Assert.Equal(GlanceTraditionalCalendarMode.IndianSaka,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "hi-IN"));
        Assert.Equal(GlanceTraditionalCalendarMode.JapaneseEra,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ja-JP"));
        Assert.Equal(GlanceTraditionalCalendarMode.Bangla,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "bn-BD"));
        Assert.Equal(GlanceTraditionalCalendarMode.Julian,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "ru-RU"));
        Assert.Equal(GlanceTraditionalCalendarMode.None,
            service.ResolveMode(GlanceTraditionalCalendarMode.Auto, "en-US"));
        Assert.Equal(GlanceTraditionalCalendarMode.Persian,
            service.ResolveMode(GlanceTraditionalCalendarMode.Persian, "en-US"));
    }

    [Fact]
    public void TraditionalCalendar_FormatsChineseIndianAndBanglaNewYear()
    {
        var service = new GlanceTraditionalCalendarService();
        CultureInfo chinese = CultureInfo.GetCultureInfo("zh-CN");

        Assert.Equal("正月", service.FormatDay(
            new DateOnly(2024, 2, 10),
            GlanceTraditionalCalendarMode.ChineseLunar,
            chinese));
        Assert.Contains("甲辰年", service.FormatTitle(
            new DateOnly(2024, 2, 10),
            GlanceTraditionalCalendarMode.ChineseLunar,
            chinese), StringComparison.Ordinal);
        Assert.Contains("臘月", service.FormatTitle(
            new DateOnly(2025, 1, 7),
            GlanceTraditionalCalendarMode.ChineseLunar,
            CultureInfo.GetCultureInfo("zh-TW")), StringComparison.Ordinal);
        Assert.Equal("१/१", service.FormatDay(
            new DateOnly(2024, 3, 21),
            GlanceTraditionalCalendarMode.IndianSaka,
            CultureInfo.GetCultureInfo("hi-IN")));
        Assert.Contains("१९४६", service.FormatTitle(
            new DateOnly(2024, 3, 21),
            GlanceTraditionalCalendarMode.IndianSaka,
            CultureInfo.GetCultureInfo("hi-IN")), StringComparison.Ordinal);
        Assert.Equal("১/১", service.FormatDay(
            new DateOnly(2026, 4, 14),
            GlanceTraditionalCalendarMode.Bangla,
            CultureInfo.GetCultureInfo("bn-BD")));
        Assert.Contains("১৪৩৩", service.FormatTitle(
            new DateOnly(2026, 4, 14),
            GlanceTraditionalCalendarMode.Bangla,
            CultureInfo.GetCultureInfo("bn-BD")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TraditionalCalendar_AppliesSecondaryLabelsAndCanBeDisabled()
    {
        var source = new LocalCalendarPresentationSource();
        var service = new GlanceTraditionalCalendarService();
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        DateOnly today = new(2026, 8, 18);
        GlanceCalendarMonth month = await source.GetMonthAsync(today, culture);

        GlanceCalendarMonth decorated = service.Apply(
            month,
            GlanceTraditionalCalendarMode.ChineseLunar,
            culture,
            today);
        Assert.False(string.IsNullOrWhiteSpace(decorated.TraditionalTitle));
        Assert.All(decorated.Days, day => Assert.False(string.IsNullOrWhiteSpace(day.TraditionalText)));

        GlanceCalendarMonth disabled = service.Apply(
            decorated,
            GlanceTraditionalCalendarMode.None,
            culture,
            today);
        Assert.Equal(string.Empty, disabled.TraditionalTitle);
        Assert.All(disabled.Days, day => Assert.Equal(string.Empty, day.TraditionalText));
    }

    [Theory]
    [InlineData(2024, 2, 10, "春节")]
    [InlineData(2024, 2, 24, "元宵")]
    [InlineData(2024, 4, 4, "清明")]
    [InlineData(2026, 4, 5, "清明")]
    [InlineData(2024, 6, 10, "端午")]
    [InlineData(2024, 8, 10, "七夕")]
    [InlineData(2024, 8, 18, "中元")]
    [InlineData(2024, 9, 17, "中秋")]
    [InlineData(2024, 10, 11, "重阳")]
    [InlineData(2025, 1, 7, "腊八")]
    [InlineData(2025, 1, 28, "除夕")]
    public void ChineseFestivals_ResolveCoreTraditionalDates(
        int year,
        int month,
        int day,
        string expected)
    {
        var service = new GlanceFestivalService();

        Assert.Equal(expected, service.GetChineseFestival(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(2024, 2, 10, "春節")]
    [InlineData(2024, 10, 11, "重陽")]
    [InlineData(2025, 1, 7, "臘八")]
    public void ChineseFestivals_UseTraditionalGlyphsForTraditionalChinese(
        int year,
        int month,
        int day,
        string expected)
    {
        var service = new GlanceFestivalService();

        Assert.Equal(
            expected,
            service.GetChineseFestival(new DateOnly(year, month, day), useTraditional: true));
    }

    [Fact]
    public async Task ChineseFestivals_OnlyDecorateEnabledChineseLunarCalendars()
    {
        var source = new LocalCalendarPresentationSource();
        var service = new GlanceFestivalService();
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        GlanceCalendarMonth month = await source.GetMonthAsync(new DateOnly(2024, 9, 1), culture);

        GlanceCalendarMonth decorated = service.Apply(
            month,
            showChineseFestivals: true,
            GlanceTraditionalCalendarMode.ChineseLunar,
            culture);
        GlanceCalendarDay midAutumn = Assert.Single(
            decorated.Days,
            calendarDay => calendarDay.Date == new DateOnly(2024, 9, 17));
        Assert.Equal("中秋", midAutumn.FestivalText);
        Assert.True(midAutumn.HasFestival);

        GlanceCalendarMonth disabled = service.Apply(
            decorated,
            showChineseFestivals: false,
            GlanceTraditionalCalendarMode.ChineseLunar,
            culture);
        Assert.All(disabled.Days, calendarDay => Assert.Equal(string.Empty, calendarDay.FestivalText));

        GlanceCalendarMonth nonChinese = service.Apply(
            decorated,
            showChineseFestivals: true,
            GlanceTraditionalCalendarMode.Hijri,
            culture);
        Assert.All(nonChinese.Days, calendarDay => Assert.Equal(string.Empty, calendarDay.FestivalText));
    }

    [Fact]
    public void CalendarLayout_ReservesStableNativeCalendarRows()
    {
        double standardPanel = GlanceCalendarLayoutCalculator.CalculatePanelHeight(
            availableHeight: 314,
            isCompact: false,
            hasTraditionalCalendar: false);
        double traditionalPanel = GlanceCalendarLayoutCalculator.CalculatePanelHeight(
            availableHeight: 314,
            isCompact: false,
            hasTraditionalCalendar: true);
        double standardDay = GlanceCalendarLayoutCalculator.CalculateDayHeight(
            standardPanel,
            isCompact: false,
            hasTraditionalCalendar: false);
        double traditionalDay = GlanceCalendarLayoutCalculator.CalculateDayHeight(
            traditionalPanel,
            isCompact: false,
            hasTraditionalCalendar: true);
        double compactPanel = GlanceCalendarLayoutCalculator.CalculatePanelHeight(
            availableHeight: 285,
            isCompact: true,
            hasTraditionalCalendar: true);
        double compactDay = GlanceCalendarLayoutCalculator.CalculateDayHeight(
            compactPanel,
            isCompact: true,
            hasTraditionalCalendar: true);
        double largePanel = GlanceCalendarLayoutCalculator.CalculatePanelHeight(
            availableHeight: 500,
            isCompact: false,
            hasTraditionalCalendar: true);
        double largeDay = GlanceCalendarLayoutCalculator.CalculateDayHeight(
            largePanel,
            isCompact: false,
            hasTraditionalCalendar: true);

        Assert.Equal(244, standardPanel);
        Assert.Equal(standardPanel, traditionalPanel);
        Assert.Equal(31, standardDay);
        Assert.Equal(standardDay, traditionalDay);
        Assert.Equal(245, compactPanel);
        Assert.Equal(24, compactDay);
        Assert.Equal(310, largePanel);
        Assert.Equal(42, largeDay);
        Assert.True(GlanceCalendarLayoutCalculator.IsCompact(319));
        Assert.False(GlanceCalendarLayoutCalculator.IsCompact(320));
        Assert.False(GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(
            panelWidth: 285,
            dayHeight: compactDay,
            isCompact: true,
            hasTraditionalCalendar: true));
        Assert.True(GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(
            panelWidth: 285,
            dayHeight: standardDay,
            isCompact: false,
            hasTraditionalCalendar: true));
    }

    [Fact]
    public void CompactCalendarDate_ShowsOnlyTheLocalizedDay()
    {
        DateTime date = new(2026, 8, 19);

        string chinese = GlanceWidgetViewModel.FormatCompactCalendarDateText(
            date,
            CultureInfo.GetCultureInfo("zh-CN"));
        string english = GlanceWidgetViewModel.FormatCompactCalendarDateText(
            date,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("19日", chinese);
        Assert.Equal("19", english);
        Assert.DoesNotContain("2026", chinese, StringComparison.Ordinal);
        Assert.DoesNotContain("8", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeFormat_FollowSystemUsesTheSystemShortTimePattern()
    {
        DateTime date = new(2026, 8, 19, 17, 5, 0);
        CultureInfo displayCulture = new("en-US");
        CultureInfo systemCulture = new("en-US");
        systemCulture.DateTimeFormat.ShortTimePattern = "h:mm tt";

        string actual = GlanceWidgetViewModel.FormatTimeText(
            date,
            GlanceTimeFormatMode.FollowSystem,
            displayCulture,
            systemCulture);

        Assert.Equal("5:05", actual);
    }

    [Theory]
    [InlineData(GlanceTimeFormatMode.Hour24, "17:05")]
    [InlineData(GlanceTimeFormatMode.Hour12, "5:05")]
    public void TimeFormat_ExplicitModesOverrideTheSystemPattern(
        GlanceTimeFormatMode mode,
        string expected)
    {
        DateTime date = new(2026, 8, 19, 17, 5, 0);
        CultureInfo displayCulture = new("en-US");
        CultureInfo systemCulture = new("en-US");
        systemCulture.DateTimeFormat.ShortTimePattern = "HH:mm";

        string actual = GlanceWidgetViewModel.FormatTimeText(
            date,
            mode,
            displayCulture,
            systemCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CalendarNavigation_ResolvesTheMonthOwningMostVisibleDays()
    {
        DateOnly[] augustGrid = Enumerable.Range(0, 42)
            .Select(offset => new DateOnly(2026, 7, 27).AddDays(offset))
            .ToArray();

        DateOnly resolved = GlanceCalendarNavigationResolver.ResolveDisplayedMonth(
            augustGrid,
            new DateOnly(2026, 7, 1));
        DateOnly fallback = GlanceCalendarNavigationResolver.ResolveDisplayedMonth(
            [],
            new DateOnly(2027, 3, 18));

        Assert.Equal(new DateOnly(2026, 8, 1), resolved);
        Assert.Equal(new DateOnly(2027, 3, 1), fallback);
    }

    [Theory]
    [InlineData(120, 2026, 7)]
    [InlineData(-120, 2026, 9)]
    [InlineData(960, 2026, 7)]
    [InlineData(-960, 2026, 9)]
    [InlineData(0, 2026, 8)]
    public void CalendarWheelNavigation_MovesExactlyOneWholeMonth(
        int wheelDelta,
        int expectedYear,
        int expectedMonth)
    {
        DateOnly target = GlanceCalendarNavigationResolver.ResolveWheelTarget(
            new DateOnly(2026, 8, 19),
            wheelDelta,
            new DateOnly(1900, 1, 1),
            new DateOnly(2100, 12, 1));

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, 1), target);
    }

    [Fact]
    public void CalendarWheelNavigation_StopsAtSupportedBounds()
    {
        DateOnly minimum = new(1900, 1, 1);
        DateOnly maximum = new(2100, 12, 1);

        Assert.Equal(minimum, GlanceCalendarNavigationResolver.ResolveWheelTarget(
            minimum,
            120,
            minimum,
            maximum));
        Assert.Equal(maximum, GlanceCalendarNavigationResolver.ResolveWheelTarget(
            maximum,
            -120,
            minimum,
            maximum));
    }

    [Theory]
    [InlineData(GlanceTraditionalCalendarMode.UmAlQura)]
    [InlineData(GlanceTraditionalCalendarMode.Hijri)]
    [InlineData(GlanceTraditionalCalendarMode.JapaneseEra)]
    [InlineData(GlanceTraditionalCalendarMode.Julian)]
    [InlineData(GlanceTraditionalCalendarMode.Hebrew)]
    [InlineData(GlanceTraditionalCalendarMode.Persian)]
    [InlineData(GlanceTraditionalCalendarMode.ThaiBuddhist)]
    public void TraditionalCalendar_SystemCalendarsProduceAHeader(GlanceTraditionalCalendarMode mode)
    {
        var service = new GlanceTraditionalCalendarService();
        string title = service.FormatTitle(
            new DateOnly(2026, 8, 18),
            mode,
            CultureInfo.GetCultureInfo("en-US"));

        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Fact]
    public void CalendarAcrylic_UsesTheSameOpacityCurveAsWidgetBackdrops()
    {
        WidgetMaterialOpacityProfile clearest = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: false,
            useBase: false,
            surfaceOpacity: 0,
            materialIntensity: 0);
        WidgetMaterialOpacityProfile strongest = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: false,
            useBase: false,
            surfaceOpacity: 1,
            materialIntensity: 1);
        WidgetMaterialOpacityProfile baseAcrylic = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark: true,
            useBase: true,
            surfaceOpacity: 1,
            materialIntensity: 1);

        Assert.Equal(0.0016, clearest.TintOpacity, precision: 4);
        Assert.Equal(0.0176, clearest.LuminosityOpacity, precision: 4);
        Assert.Equal(0.34, strongest.TintOpacity, precision: 4);
        Assert.Equal(0.64, strongest.LuminosityOpacity, precision: 4);
        Assert.Equal(0.72, baseAcrylic.TintOpacity, precision: 4);
        Assert.Equal(0.82, baseAcrylic.LuminosityOpacity, precision: 4);
    }

    [Theory]
    [InlineData(false, 0.0, 10)]
    [InlineData(false, 1.0, 117)]
    [InlineData(true, 0.0, 71)]
    [InlineData(true, 1.0, 209)]
    public void EmbeddedMicaTintOverlay_UsesMicaIntensityCurve(
        bool useAlt,
        double materialIntensity,
        int expectedAlpha)
    {
        Windows.UI.Color color = WidgetMaterialVisualCalculator.BuildEmbeddedMicaTintOverlayColor(
            isDark: false,
            Windows.UI.Color.FromArgb(255, 0, 120, 215),
            useAlt,
            materialIntensity);

        Assert.Equal(expectedAlpha, color.A);
    }

    [Fact]
    public void EmbeddedMica_UsesTintOverlayWithoutNestedSystemBackdrop()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));
        string visualCalculator = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetMaterialVisualCalculator.cs"));

        Assert.DoesNotContain("SystemBackdropElement", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemBackdrop =", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildContentTintColor(isDark, accentColor)", visualCalculator, StringComparison.Ordinal);
        Assert.Contains("BuildEmbeddedMicaTintOverlayColor", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildEmbeddedMicaTintOverlayColor", visualCalculator, StringComparison.Ordinal);
        Assert.Contains("CalendarMaterialSurface.Background = _calendarSolidMaterialBrush", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void ImagePalette_SeparatesTwoDominantColorFamilies()
    {
        byte[] pixels = new byte[80 * 4];
        for (int pixel = 0; pixel < 80; pixel++)
        {
            int offset = pixel * 4;
            bool red = pixel < 50;
            pixels[offset] = red ? (byte)24 : (byte)220;
            pixels[offset + 1] = red ? (byte)40 : (byte)70;
            pixels[offset + 2] = red ? (byte)220 : (byte)35;
            pixels[offset + 3] = 255;
        }

        GlanceImagePalette? extracted = GlanceImagePaletteService.ExtractPalette(pixels);
        Assert.True(extracted.HasValue);
        GlanceImagePalette palette = extracted.Value;

        Assert.True(palette.Primary.R > palette.Primary.B);
        Assert.True(palette.Secondary.B > palette.Secondary.R);
    }

    [Fact]
    public void ImagePaletteGradient_FusesPaletteWithLightAndDarkThemeBases()
    {
        var palette = new GlanceImagePalette(
            Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x46, 0x3D),
            Windows.UI.Color.FromArgb(0xFF, 0x26, 0x72, 0xCA));

        WidgetMaterialGradientProfile light =
            WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark: false, palette: palette);
        WidgetMaterialGradientProfile dark =
            WidgetMaterialVisualCalculator.BuildImagePaletteGradient(isDark: true, palette: palette);

        Assert.True(Luminance(light.StartColor) > Luminance(dark.StartColor));
        Assert.True(Luminance(light.EndColor) > Luminance(dark.EndColor));
        Assert.NotEqual(light.StartColor, light.EndColor);
        Assert.NotEqual(dark.StartColor, dark.EndColor);
    }

    private static double Luminance(Windows.UI.Color color) =>
        (color.R * 0.2126) + (color.G * 0.7152) + (color.B * 0.0722);

    [Fact]
    public async Task LocalFiles_KeepSupportedExistingImagesOnly()
    {
        Directory.CreateDirectory(_tempRoot);
        string first = Path.Combine(_tempRoot, "first.jpg");
        string second = Path.Combine(_tempRoot, "second.png");
        string ignored = Path.Combine(_tempRoot, "notes.txt");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5, 6]);
        await File.WriteAllTextAsync(ignored, "not an image");
        var service = new GlanceImageService(Path.Combine(_tempRoot, "cache"));

        IReadOnlyList<GlanceImageInfo> images = await service.GetAvailableImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.LocalFiles,
            LocalImagePaths = [first, ignored, second, Path.Combine(_tempRoot, "missing.webp")]
        });

        Assert.Equal(2, images.Count);
        Assert.Equal([first, second], images.Select(image => image.LocalPath));
        Assert.All(images, image => Assert.False(image.IsOnline));
    }

    [Fact]
    public async Task LocalFolder_DoesNotScanNestedDirectories()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_tempRoot, "nested")).FullName;
        string top = Path.Combine(_tempRoot, "top.jpg");
        string child = Path.Combine(nested, "child.jpg");
        await File.WriteAllBytesAsync(top, [1]);
        await File.WriteAllBytesAsync(child, [2]);
        var service = new GlanceImageService(Path.Combine(_tempRoot, "cache"));

        IReadOnlyList<GlanceImageInfo> images = await service.GetAvailableImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.LocalFolder,
            LocalFolderPath = _tempRoot
        });

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(top, image.LocalPath);
    }

    [Fact]
    public async Task OnlineRefresh_ClosesTemporaryFileBeforePublishingCacheEntry()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture fixture = new("first.jpg", "https://images.test/first.jpg");
        using HttpClient httpClient = CreateOnlineClient(
            [fixture],
            _ => CreateBytesResponse([1, 2, 3, 4]));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo image = Assert.Single(images);
        Assert.True(File.Exists(image.LocalPath));
        string catalogPath = Path.Combine(cacheDirectory, "catalog.json");
        Assert.True(File.Exists(catalogPath));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(image.LocalPath!));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories));
        using (JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath)))
        {
            JsonElement catalogImage = Assert.Single(catalog.RootElement.EnumerateArray());
            Assert.Equal(JsonValueKind.Number, catalogImage.GetProperty("onlineCategory").ValueKind);
            Assert.Equal(
                (int)GlanceOnlineImageCategory.Featured,
                catalogImage.GetProperty("onlineCategory").GetInt32());
            Assert.Equal(JsonValueKind.Number, catalogImage.GetProperty("onlineProvider").ValueKind);
            Assert.Equal(
                (int)GlanceOnlineImageProvider.Wikimedia,
                catalogImage.GetProperty("onlineProvider").GetInt32());
        }
        using var exclusiveProbe = new FileStream(
            image.LocalPath!,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task OnlineRefresh_ContinuesWhenOneImageDownloadFails()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture first = new("first.jpg", "https://images.test/first.jpg");
        RemoteImageFixture second = new("second.jpg", "https://images.test/second.jpg");
        using HttpClient httpClient = CreateOnlineClient(
            [first, second],
            request => request.RequestUri == new Uri(first.ImageUrl)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : CreateBytesResponse([9, 8, 7]));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(second.ImageUrl, image.RemoteImageUrl);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(image.LocalPath!));
        Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BingRefresh_UsesChinaEndpointFiltersRestrictedImagesAndKeepsAttribution()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        int archiveRequests = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("HPImageArchive.aspx", StringComparison.Ordinal) == true)
            {
                archiveRequests++;
                return Task.FromResult(CreateJsonResponse(new
                {
                    images = new object[]
                    {
                        new
                        {
                            url = "/th?id=OHR.Allowed_1920x1080.jpg",
                            urlbase = "/th?id=OHR.Allowed",
                            copyright = "A beautiful place (© Example Photographer)",
                            copyrightlink = "https://cn.bing.com/search?q=allowed",
                            title = "A beautiful place",
                            wp = true,
                            hsh = "allowed-image"
                        },
                        new
                        {
                            url = "/th?id=OHR.Restricted_1920x1080.jpg",
                            urlbase = "/th?id=OHR.Restricted",
                            copyright = "Restricted image",
                            copyrightlink = "https://cn.bing.com/search?q=restricted",
                            title = "Restricted image",
                            wp = false,
                            hsh = "restricted-image"
                        }
                    }
                }));
            }

            return Task.FromResult(CreateBytesResponse([4, 5, 6]));
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync(new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.Bing
        });

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(BingArchiveBatchCountForTest, archiveRequests);
        Assert.Equal(GlanceOnlineImageProvider.Bing, image.OnlineProvider);
        Assert.Equal("A beautiful place", image.Title);
        Assert.Contains("Example Photographer", image.Author, StringComparison.Ordinal);
        Assert.Equal("cn.bing.com", new Uri(image.RemoteImageUrl!).Host);
        Assert.Equal("https://cn.bing.com/search?q=allowed", image.SourcePageUrl);
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Wikimedia,
            GlanceOnlineImageCategory.Featured));
        Assert.Single(await service.LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Bing,
            GlanceOnlineImageCategory.Featured));
    }

    [Fact]
    public async Task OnlineRefresh_UsesSelectedCategoryAndKeepsItsCacheIsolated()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        var requestedUris = new List<Uri>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            requestedUris.Add(request.RequestUri!);
            string query = Uri.UnescapeDataString(request.RequestUri?.Query ?? string.Empty);
            if (query.Contains("list=categorymembers", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        categorymembers = new[] { new { title = "File:city.jpg" } }
                    }
                }));
            }

            if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        pages = new[]
                        {
                            new
                            {
                                imageinfo = new[]
                                {
                                    new
                                    {
                                        thumbwidth = 1600,
                                        thumbheight = 900,
                                        mime = "image/jpeg",
                                        descriptionurl = "https://commons.wikimedia.org/wiki/File:city.jpg",
                                        thumburl = "https://images.test/city.jpg"
                                    }
                                }
                            }
                        }
                    }
                }));
            }

            return Task.FromResult(CreateBytesResponse([7, 8, 9]));
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync(
            GlanceOnlineImageCategory.Cities);

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal(GlanceOnlineImageCategory.Cities, image.OnlineCategory);
        Assert.Empty(await service.LoadCachedOnlineImagesAsync(GlanceOnlineImageCategory.Featured));
        Assert.Single(await service.LoadCachedOnlineImagesAsync(GlanceOnlineImageCategory.Cities));
        Assert.Contains(
            requestedUris,
            uri => Uri.UnescapeDataString(uri.Query).Contains(
                "Category:Quality images of cityscapes",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlineRefresh_UsesLargeMetadataBatchesAndDownloadsIncrementally()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture[] fixtures = Enumerable.Range(1, 8)
            .Select(index => new RemoteImageFixture(
                $"image-{index}.jpg",
                $"https://images.test/image-{index}.jpg"))
            .ToArray();
        int imageInfoRequests = 0;
        int downloadRequests = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            string query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("list=categorymembers", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        categorymembers = fixtures.Select(fixture => new
                        {
                            title = $"File:{fixture.FileName}"
                        })
                    }
                }));
            }

            if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                imageInfoRequests++;
                return Task.FromResult(CreateJsonResponse(new
                {
                    query = new
                    {
                        pages = fixtures.Select(fixture => new
                        {
                            imageinfo = new[]
                            {
                                new
                                {
                                    thumbwidth = 1600,
                                    thumbheight = 900,
                                    mime = "image/jpeg",
                                    descriptionurl = $"https://commons.wikimedia.org/wiki/File:{fixture.FileName}",
                                    thumburl = fixture.ImageUrl
                                }
                            }
                        })
                    }
                }));
            }

            downloadRequests++;
            return Task.FromResult(CreateBytesResponse([1, 2, 3]));
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync(
            GlanceOnlineImageCategory.Landscapes);

        Assert.Equal(3, images.Count);
        Assert.Equal(1, imageInfoRequests);
        Assert.Equal(3, downloadRequests);
        Assert.All(images, image => Assert.Equal(GlanceOnlineImageCategory.Landscapes, image.OnlineCategory));
    }

    [Fact]
    public async Task OnlineRefresh_ReturnsExistingCacheWhenRemoteCatalogFails()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        RemoteImageFixture fixture = new("cached.jpg", "https://images.test/cached.jpg");
        using (HttpClient populateClient = CreateOnlineClient(
                   [fixture],
                   _ => CreateBytesResponse([5, 4, 3])))
        {
            var populateService = new GlanceImageService(cacheDirectory, populateClient, () => true);
            Assert.Single(await populateService.RefreshOnlineImagesAsync());
        }

        using var failingClient = new HttpClient(new StubHttpMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var service = new GlanceImageService(cacheDirectory, failingClient, () => true);

        IReadOnlyList<GlanceImageInfo> images = await service.RefreshOnlineImagesAsync();

        GlanceImageInfo cached = Assert.Single(images);
        Assert.Equal([5, 4, 3], await File.ReadAllBytesAsync(cached.LocalPath!));
    }

    [Fact]
    public async Task OnlineRefresh_PropagatesCancellationWithoutLeavingTemporaryFiles()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "cache");
        using var httpClient = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new GlanceImageService(cacheDirectory, httpClient, () => true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshOnlineImagesAsync(cancellation.Token));

        Assert.False(Directory.Exists(cacheDirectory) &&
                     Directory.EnumerateFiles(cacheDirectory, "*.tmp", SearchOption.AllDirectories).Any());
    }

    private static HttpClient CreateOnlineClient(
        IReadOnlyList<RemoteImageFixture> fixtures,
        Func<HttpRequestMessage, HttpResponseMessage> imageResponder)
    {
        return new HttpClient(new StubHttpMessageHandler((request, _) =>
        {
            string query = request.RequestUri?.Query ?? string.Empty;
            if (query.Contains("list=categorymembers", StringComparison.Ordinal))
            {
                object[] categorymembers = fixtures
                    .Select(fixture => (object)new { title = $"File:{fixture.FileName}" })
                    .ToArray();
                return Task.FromResult(CreateJsonResponse(new { query = new { categorymembers } }));
            }

            if (query.Contains("prop=imageinfo", StringComparison.Ordinal))
            {
                object[] pages = fixtures
                    .Select(fixture => (object)new
                    {
                        imageinfo = new[]
                        {
                            new
                            {
                                thumbwidth = 1600,
                                thumbheight = 900,
                                mime = "image/jpeg",
                                descriptionurl = $"https://commons.wikimedia.org/wiki/File:{fixture.FileName}",
                                thumburl = fixture.ImageUrl
                            }
                        }
                    })
                    .ToArray();
                return Task.FromResult(CreateJsonResponse(new { query = new { pages } }));
            }

            return Task.FromResult(imageResponder(request));
        }));
    }

    private static HttpResponseMessage CreateJsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }

    private static HttpResponseMessage CreateBytesResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }

    private sealed record RemoteImageFixture(string FileName, string ImageUrl);

    private static Dictionary<string, string> ReadLocalizedStrings(string path)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? throw new InvalidDataException($"Could not read localization file {path}.");
    }

    private const int BingArchiveBatchCountForTest = 3;

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
}
