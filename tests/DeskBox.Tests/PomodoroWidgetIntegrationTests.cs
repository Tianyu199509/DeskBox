using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class PomodoroWidgetIntegrationTests
{
    private static readonly string[] RequiredLocalizationKeys =
    [
        "Pomodoro.Title",
        "Pomodoro.Phase.Focus",
        "Pomodoro.Phase.ShortBreak",
        "Pomodoro.Phase.LongBreak",
        "Pomodoro.Action.Start",
        "Pomodoro.Action.Pause",
        "Pomodoro.Action.Reset",
        "Pomodoro.Action.Skip",
        "Pomodoro.RoundSummary",
        "Pomodoro.Focus.Description",
        "Pomodoro.ShortBreak.Description",
        "Pomodoro.LongBreak.Description",
        "WidgetContent.Pomodoro.StatusLabel",
        "WidgetContent.Pomodoro.StatusDescription",
        "WidgetTitleIcon.Label.Pomodoro",
        "Widget.Settings.Pomodoro",
        "Settings.Pomodoro.Title",
        "Settings.Pomodoro.Description",
        "Settings.Pomodoro.Summary",
        "Settings.Pomodoro.RoundCount.Title",
        "Settings.Pomodoro.RoundCount.Description",
        "Settings.Pomodoro.FocusMinutes.Title",
        "Settings.Pomodoro.FocusMinutes.Description",
        "Settings.Pomodoro.ShortBreakMinutes.Title",
        "Settings.Pomodoro.ShortBreakMinutes.Description",
        "Settings.Pomodoro.LongBreakMinutes.Title",
        "Settings.Pomodoro.LongBreakMinutes.Description"
    ];

    [Fact]
    public void WidgetKind_AppendsPomodoroWithoutRenumberingExistingKinds()
    {
        Assert.Equal((int)WidgetKind.Glance + 1, (int)WidgetKind.Pomodoro);
    }

    [Fact]
    public void FeatureState_DefaultsOffAndCanBeEnabled()
    {
        var settings = new AppSettings();

        Assert.True(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.Pomodoro));
        Assert.False(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Pomodoro));

        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Pomodoro, true);

        Assert.True(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Pomodoro));
    }

    [Fact]
    public void Manager_RegistersPomodoroLifecycleAndConsistentDefaultSize()
    {
        string manager = Read("src/DeskBox/Services/WidgetManager.cs");
        string featureManager = Read("src/DeskBox/Services/WidgetManager.FeatureWidgets.cs");
        string groupManager = Read("src/DeskBox/Services/WidgetManager.Groups.cs");

        Assert.Contains("WidgetKind.Pomodoro,", manager, StringComparison.Ordinal);
        Assert.Contains("SetPomodoroFeatureWidgetEnabledAsync", manager, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Pomodoro => (300, 330)", manager, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Pomodoro => 300", featureManager, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Pomodoro => 330", featureManager, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Pomodoro => \"Pomodoro.Title\"", featureManager, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Pomodoro => \"Pomodoro.Title\"", groupManager, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetWithSettingsPage_ExposesPomodoroConfigurationMenu()
    {
        WidgetContentDescriptor descriptor =
            TestServices.CreateWidgetContentFactory().GetDescriptor(WidgetKind.Pomodoro);
        string menuSource = Read("src/DeskBox/Views/ContentWidgetWindow.Commands.cs");

        Assert.True(descriptor.HasSettingsPage);
        Assert.Equal("PomodoroSettings", descriptor.SettingsSectionTag);
        Assert.Contains("if (_descriptor.HasSettingsPage)", menuSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRuntimeLocale_ContainsCompletePomodoroSurface()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        string[] localeFiles = Directory.GetFiles(
            stringsRoot,
            "*.json",
            SearchOption.TopDirectoryOnly);

        Assert.Equal(12, localeFiles.Length);
        foreach (string path in localeFiles)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string key in RequiredLocalizationKeys)
            {
                Assert.True(document.RootElement.TryGetProperty(key, out JsonElement value),
                    $"Missing {key} in {Path.GetFileName(path)}");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()),
                    $"Empty {key} in {Path.GetFileName(path)}");
            }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
