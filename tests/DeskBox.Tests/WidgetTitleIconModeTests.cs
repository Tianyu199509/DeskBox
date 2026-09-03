using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTitleIconModeTests
{
    [Fact]
    public void SearchWidget_UsesDedicatedSearchIconFamily()
    {
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Search));
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromLegacyGlyph("\uE721"));
        Assert.Equal("search", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Search));
        Assert.Equal("WidgetTitleIcon.Label.Search", WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Search));
    }

    [Fact]
    public void TodoAndQuickCaptureColorIcons_UseTheSameVisualPaletteAndCanvas()
    {
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/todo.svg"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/quick-capture.svg"));

        foreach (string icon in new[] { todo, quickCapture })
        {
            Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#38BDF8\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#2563EB\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#60A5FA\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#1D4ED8\"", icon, StringComparison.Ordinal);
            Assert.Contains("#FFFFFF", icon, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("#B3E0FF", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("#8CD0FF", quickCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void GlanceColorIcon_UsesFluentCalendarColorAsset()
    {
        string glance = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/glance.svg"));

        Assert.Equal(WidgetTitleIconKindNames.Glance, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Glance));
        Assert.Equal("glance", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Glance));
        Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", glance, StringComparison.Ordinal);
        Assert.Contains("ic_fluent_calendar_20_color__a", glance, StringComparison.Ordinal);
        Assert.Contains("#0094F0", glance, StringComparison.Ordinal);
        Assert.Contains("#2764E7", glance, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"sky\"", glance, StringComparison.Ordinal);
    }

    [Fact]
    public void PomodoroWidget_UsesDedicatedTomatoTimerIconFamily()
    {
        string pomodoro = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/pomodoro.svg"));

        Assert.Equal(
            WidgetTitleIconKindNames.Pomodoro,
            WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Pomodoro));
        Assert.Equal(
            WidgetTitleIconKindNames.Pomodoro,
            WidgetTitleIconKindNames.FromLegacyGlyph("\uE916"));
        Assert.Equal(
            "pomodoro",
            WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Pomodoro));
        Assert.Equal(
            "WidgetTitleIcon.Label.Pomodoro",
            WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Pomodoro));
        Assert.Contains("#FF786A", pomodoro, StringComparison.Ordinal);
        Assert.Contains("#2F8B63", pomodoro, StringComparison.Ordinal);
    }
}
