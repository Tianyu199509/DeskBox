using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlanceWidgetContextMenuTests
{
    [Fact]
    public void DisplayPolicy_AllowsPhotoOnlyModeAndKeepsCalendarStateConsistent()
    {
        var settings = new GlanceWidgetData();

        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Time, false);
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Year, true);
        Assert.True(settings.ShowDate);
        Assert.True(settings.ShowYear);
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Date, false);
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Weekday, false);
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Calendar, false);

        Assert.False(settings.ShowTime);
        Assert.False(settings.ShowDate);
        Assert.False(settings.ShowYear);
        Assert.False(settings.ShowWeekday);
        Assert.False(settings.ShowCalendar);
        Assert.Equal(GlanceLayoutMode.Centered, settings.Layout);

        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Calendar, true);
        Assert.True(settings.ShowCalendar);
        Assert.Equal(GlanceLayoutMode.Calendar, settings.Layout);

        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Calendar, false);
        Assert.False(settings.ShowCalendar);
        Assert.Equal(GlanceLayoutMode.Centered, settings.Layout);
    }

    [Theory]
    [InlineData(GlanceLayoutMode.Immersive, false)]
    [InlineData(GlanceLayoutMode.Centered, false)]
    [InlineData(GlanceLayoutMode.Editorial, false)]
    [InlineData(GlanceLayoutMode.Calendar, true)]
    public void LayoutPolicy_SynchronizesCalendarVisibility(
        GlanceLayoutMode layout,
        bool expectedCalendarVisibility)
    {
        var settings = new GlanceWidgetData { ShowCalendar = true };

        GlanceWidgetSettingsPolicy.SetLayout(settings, layout);

        Assert.Equal(layout, settings.Layout);
        Assert.Equal(expectedCalendarVisibility, settings.ShowCalendar);
    }

    [Fact]
    public void LocalImagePolicy_NormalizesPathsAndKeepsLocalFilesSource()
    {
        var settings = new GlanceWidgetData
        {
            BackgroundSource = GlanceBackgroundSource.Bing
        };

        GlanceWidgetSettingsPolicy.SetLocalImageFiles(
            settings,
            [" C:\\Pictures\\one.png ", "c:\\pictures\\ONE.png", ""]);

        Assert.Equal(GlanceBackgroundSource.LocalFiles, settings.BackgroundSource);
        Assert.Equal([@"C:\Pictures\one.png"], settings.LocalImagePaths);

        GlanceWidgetSettingsPolicy.ClearLocalSource(settings);
        Assert.Equal(GlanceBackgroundSource.LocalFiles, settings.BackgroundSource);
        Assert.Empty(settings.LocalImagePaths);
    }

    [Fact]
    public void PhotoPlaybackPolicy_AppliesOneCoherentPreferenceSet()
    {
        var settings = new GlanceWidgetData();

        GlanceWidgetSettingsPolicy.SetPhotoPlayback(
            settings,
            rotationIntervalMinutes: 0,
            randomOrder: false,
            GlanceTransitionMode.None,
            GlanceTransitionSpeed.Fast,
            GlanceReadabilityMode.Strong,
            showPhotoControls: true);

        Assert.Equal(0, settings.RotationIntervalMinutes);
        Assert.False(settings.RandomOrder);
        Assert.Equal(GlanceTransitionMode.None, settings.Transition);
        Assert.Equal(GlanceTransitionSpeed.Fast, settings.TransitionSpeed);
        Assert.Equal(GlanceReadabilityMode.Strong, settings.Readability);
        Assert.True(settings.ShowPhotoControls);
    }

    [Fact]
    public void ContextMenu_KeepsOnlyStyleAndDisplayBeforeSharedWidgetCommands()
    {
        string builder = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/GlanceWidgetContextMenuBuilder.cs"));
        string host = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Contains("new ToggleMenuFlyoutItem", builder, StringComparison.Ordinal);
        Assert.Contains("new RadioMenuFlyoutItem", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("new MenuFlyoutItem", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.Actions.Next", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.Actions.Pause", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.Actions.OpenSource", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("Glance.Actions.ShowInExplorer", builder, StringComparison.Ordinal);
        Assert.Contains("Glance.Display.Title", builder, StringComparison.Ordinal);
        Assert.Contains("Glance.Display.Seconds", builder, StringComparison.Ordinal);
        Assert.Contains("Glance.Style.Title", builder, StringComparison.Ordinal);
        Assert.Contains("CurrentContent is GlanceWidgetContentAdapter", host, StringComparison.Ordinal);
        Assert.True(
            host.IndexOf("GlanceWidgetContextMenuBuilder.Append", StringComparison.Ordinal) <
            host.IndexOf("var rename = new MenuFlyoutItem", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsEntry_IsPresentInEveryLocale()
    {
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.TryGetProperty("Widget.Settings.Glance", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Display.Seconds", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Display.Year", out _), path);
            Assert.True(document.RootElement.TryGetProperty("Glance.Speed.Title", out _), path);
        }
    }

    [Fact]
    public void ChineseMenuLabels_DescribeSettingsAndVisualLayoutPrecisely()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Strings/zh-CN.json")));

        Assert.Equal(
            "时光设置",
            document.RootElement.GetProperty("Widget.Settings.Glance").GetString());
        Assert.Equal(
            "杂志式",
            document.RootElement.GetProperty("Glance.Layout.Editorial").GetString());
        Assert.Equal(
            "字体大小",
            document.RootElement.GetProperty("Glance.Typography.Title").GetString());
    }
}
