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
    public void ClipboardTodoAndQuickCaptureColorIcons_UseFluentClipboardColorFamily()
    {
        string clipboard = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/clipboard.svg"));
        string todo = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/todo.svg"));
        string quickCapture = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Assets/WidgetTitleIcons/quick-capture.svg"));

        Assert.Contains("ic_fluent_clipboard_20_color__a", clipboard, StringComparison.Ordinal);
        Assert.Contains("ic_fluent_clipboard_task_20_color__a", todo, StringComparison.Ordinal);
        Assert.Contains("ic_fluent_clipboard_text_edit_20_color__a", quickCapture, StringComparison.Ordinal);

        foreach (string icon in new[] { clipboard, todo, quickCapture })
        {
            Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#36dff1\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#0094f0\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#ffe06b\"", icon, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#fab500\"", icon, StringComparison.Ordinal);
        }
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
    public void CustomWidget_UsesDedicatedCustomIconFamily()
    {
        Assert.Equal(WidgetTitleIconKindNames.Custom, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Custom));
        Assert.Equal("custom", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Custom));
        Assert.Equal("CustomWidget.Title", WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Custom));
    }

    [Fact]
    public void OfficialCustomPresets_UseFluentTitleIconFamiliesAndMigrateLegacyEmojisOnce()
    {
        Assert.Equal(WidgetTitleIconKindNames.SystemMonitor, WidgetTitleIconKindNames.FromCustomTemplateId("sys-pulse"));
        Assert.Equal(WidgetTitleIconKindNames.Pomodoro, WidgetTitleIconKindNames.FromCustomTemplateId("deskbox.pomodoro"));
        Assert.Equal(WidgetTitleIconKindNames.Countdown, WidgetTitleIconKindNames.FromCustomTemplateId("countdown"));
        Assert.Equal(WidgetTitleIconKindNames.AiMonitor, WidgetTitleIconKindNames.FromCustomTemplateId("deskbox.ai-pulse"));
        Assert.Null(WidgetTitleIconKindNames.FromCustomTemplateId("work-logger"));

        Assert.Equal("pomodoro", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Pomodoro));
        Assert.Equal("countdown", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Countdown));
        Assert.Equal("ai-monitor", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.AiMonitor));

        Assert.True(File.Exists(TestPaths.FromRepository("src/DeskBox/Assets/WidgetTitleIcons/system-monitor.svg")));
        foreach (string assetFile in new[] { "pomodoro.svg", "countdown.svg", "ai-monitor.svg" })
        {
            string svg = File.ReadAllText(TestPaths.FromRepository($"src/DeskBox/Assets/WidgetTitleIcons/{assetFile}"));
            Assert.Contains("width=\"20\" height=\"20\" viewBox=\"0 0 20 20\"", svg, StringComparison.Ordinal);
        }

        // Legacy auto-seeded default emoji ("🍅") is cleared once so global TitleIconMode applies,
        // while subsequent manual emoji overrides by the user are preserved.
        var pomoConfig = new WidgetConfig
        {
            Id = "pomo-1",
            WidgetKind = WidgetKind.Custom,
            Metadata = new Dictionary<string, string>
            {
                ["CustomTemplateId"] = CustomWidgetTemplates.PomodoroTemplateId,
                [WidgetTitleIconCustomization.EmojiMetadataKey] = "🍅"
            }
        };
        Assert.Equal(WidgetTitleIconKindNames.Pomodoro, WidgetTitleIconKindNames.FromWidgetConfig(pomoConfig));
        Assert.True(WidgetTitleIconCustomization.NormalizeOverrides(pomoConfig));
        Assert.Null(WidgetTitleIconCustomization.GetEmojiOverride(pomoConfig));

        WidgetTitleIconCustomization.SetEmojiOverride(pomoConfig, "🍅");
        Assert.False(WidgetTitleIconCustomization.NormalizeOverrides(pomoConfig));
        Assert.Equal("🍅", WidgetTitleIconCustomization.GetEmojiOverride(pomoConfig));
    }
}
