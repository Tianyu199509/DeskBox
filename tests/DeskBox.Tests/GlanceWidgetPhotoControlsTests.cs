using System.Globalization;
using System.Text.Json;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class GlanceWidgetPhotoControlsTests
{
    [Fact]
    public void SettingsAndWidget_WireOptionalAcrylicPhotoControlBar()
    {
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string settingsCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));
        string widgetXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));

        Assert.Contains("x:Name=\"ShowPhotoControlsToggle\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ShowPhotoControlsToggle_Toggled", settingsCode, StringComparison.Ordinal);
        Assert.Contains("GlanceActionBarAcrylicBrush", widgetXaml, StringComparison.Ordinal);
        Assert.Contains("TintLuminosityOpacity=\"0.52\"", widgetXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"9\"", widgetXaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowPhotoControls, Converter={StaticResource BoolToVisibility}}\"",
            widgetXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhotoControlSetting_IsPresentInEveryLocale()
    {
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.TryGetProperty("Glance.PhotoControls.Title", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.PhotoControls.Description", out _), path);
        }
    }

    [Fact]
    public void ActionBar_UsesConsistentFluentReactRegularIconsAtTopRight()
    {
        string widgetXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));
        int actionBarStart = widgetXaml.IndexOf("x:Name=\"ActionLayer\"", StringComparison.Ordinal);
        int actionBarEnd = widgetXaml.IndexOf("<ProgressRing", actionBarStart, StringComparison.Ordinal);
        Assert.True(actionBarStart >= 0 && actionBarEnd > actionBarStart);
        string actionBar = widgetXaml[actionBarStart..actionBarEnd];

        Assert.Contains("VerticalAlignment=\"Top\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Play16RegularIcon\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Pause16RegularIcon\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ArrowRight16RegularIcon\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"Info16RegularIcon\"", actionBar, StringComparison.Ordinal);
        Assert.Contains("<Canvas Width=\"16\" Height=\"16\">", actionBar, StringComparison.Ordinal);
        Assert.DoesNotContain("FilledIcon", actionBar, StringComparison.Ordinal);
        Assert.DoesNotContain("<FontIcon", actionBar, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlineImageCategorySetting_IsWiredAndLocalized()
    {
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string settingsCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        Assert.Contains("x:Name=\"OnlineImageCategoryCard\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OnlineImageCategoryComboBox\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("OnlineImageCategoryComboBox_SelectionChanged", settingsCode, StringComparison.Ordinal);

        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.TryGetProperty("Glance.Background.Category.Title", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Background.Category.Featured", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Background.Category.People", out _), path);
        }
    }

    [Fact]
    public void BackgroundSettings_PutLocalActionsOnRightAndWireBingAndClear()
    {
        string settingsXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml"));
        string settingsCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"));

        Assert.Contains("x:Name=\"ClearLocalSourceButton\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("ClearLocalSourceButton_Click", settingsCode, StringComparison.Ordinal);
        Assert.Contains("GlanceBackgroundSource.Bing", settingsCode, StringComparison.Ordinal);

        int localOption = settingsCode.IndexOf("GlanceBackgroundSource.LocalFiles", StringComparison.Ordinal);
        int bingOption = settingsCode.IndexOf("GlanceBackgroundSource.Bing", StringComparison.Ordinal);
        int wikiOption = settingsCode.IndexOf("GlanceBackgroundSource.Online", StringComparison.Ordinal);
        Assert.True(localOption >= 0 && localOption < bingOption && bingOption < wikiOption);

        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.TryGetProperty("Glance.Background.Bing", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Background.ClearSelection", out _), path);
        }
    }

    [Fact]
    public void ClearingCurrentImage_RemovesBothTransitionBackgroundLayers()
    {
        string widgetCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));

        Assert.Contains(
            "if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))",
            widgetCode,
            StringComparison.Ordinal);
        Assert.Contains("ClearBackgroundImage();", widgetCode, StringComparison.Ordinal);
        Assert.Contains("_transitionStoryboard?.Stop();", widgetCode, StringComparison.Ordinal);
        Assert.Contains("background.Background = null;", widgetCode, StringComparison.Ordinal);
        Assert.Contains("background.Opacity = 0;", widgetCode, StringComparison.Ordinal);
        Assert.Contains("_isAActive = false;", widgetCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactPresentation_UsesFullBleedPhotoWithStackedTimeDateAndTraditionalCalendar()
    {
        string host = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string adapter = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContentAdapter.cs"));
        string viewModel = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs"));

        Assert.Contains(
            "GlanceWidgetContentAdapter glance =>",
            host,
            StringComparison.Ordinal);
        Assert.Contains("CreateGlanceCompactPresentation", host, StringComparison.Ordinal);
        Assert.Contains("ImageSource? backgroundImage = viewModel.HasVisibleCurrentImage", host, StringComparison.Ordinal);
        Assert.Contains("((IWidgetCompactBackgroundContent)glance).GetCompactBackground()", host, StringComparison.Ordinal);
        Assert.Contains("Thumbnail: backgroundImage", host, StringComparison.Ordinal);
        Assert.Contains("UseFullBleedBackground: backgroundImage is not null", host, StringComparison.Ordinal);
        Assert.Contains(
            "FullBleedOverlayOpacity: hasText ? viewModel.ReadabilityStrengthOpacity : 0",
            host,
            StringComparison.Ordinal);
        Assert.Contains("UseUniformFullBleedOverlay: true", host, StringComparison.Ordinal);
        Assert.Contains("FullBleedBackgroundOpacity: background?.Opacity ?? 0", host, StringComparison.Ordinal);
        Assert.Contains("UseStackedText: true", host, StringComparison.Ordinal);
        Assert.Contains("viewModel.TimeText", host, StringComparison.Ordinal);
        Assert.Contains("viewModel.DateText", host, StringComparison.Ordinal);
        Assert.Contains("viewModel.WeekdayText", host, StringComparison.Ordinal);
        Assert.Contains("viewModel.TraditionalCalendarTitle", host, StringComparison.Ordinal);
        Assert.Contains("DecodePixelWidth = 768", adapter, StringComparison.Ordinal);
        Assert.Contains("ResolveFullBleedOverlayOpacity()", shell, StringComparison.Ordinal);
        Assert.Contains("ResolveFullBleedBackgroundOpacity()", shell, StringComparison.Ordinal);
        Assert.Contains("CompactFullBleedClip.Opacity = useFullBleed", shell, StringComparison.Ordinal);
        Assert.Contains("UseUniformFullBleedOverlay", shell, StringComparison.Ordinal);
        Assert.Contains(
            "presentation.ShowMediaControls || presentation.ShowVinyl",
            shell,
            StringComparison.Ordinal);
        int clockTimerStart = viewModel.IndexOf(
            "private void UpdateClockTimer()",
            StringComparison.Ordinal);
        int rotationTimerStart = viewModel.IndexOf(
            "private void UpdateRotationTimer()",
            clockTimerStart,
            StringComparison.Ordinal);
        Assert.True(clockTimerStart >= 0 && rotationTimerStart > clockTimerStart);
        Assert.DoesNotContain(
            "_isCompact",
            viewModel[clockTimerStart..rotationTimerStart],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Calendar_RemovesGregorianMonthHeaderButKeepsTraditionalCalendarHeader()
    {
        string widgetXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml"));

        Assert.DoesNotContain("Text=\"{Binding MonthTitle}\"", widgetXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TraditionalCalendarTitle}\"", widgetXaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-CN", false, "8月18日")]
    [InlineData("zh-CN", true, "2026年8月18日")]
    [InlineData("en-US", false, "August 18")]
    [InlineData("en-US", true, "August 18, 2026")]
    public void DateText_UsesIndependentLocalizedYearOption(
        string cultureName,
        bool showYear,
        string expected)
    {
        string actual = GlanceWidgetViewModel.FormatDateText(
            new DateTime(2026, 8, 18),
            CultureInfo.GetCultureInfo(cultureName),
            showYear);

        Assert.Equal(expected, actual);
    }
}
