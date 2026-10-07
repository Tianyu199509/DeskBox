namespace DeskBox.Tests;

/// <summary>
/// Entry rows are native toolkit SettingsCards with IsClickEnabled: the
/// whole card is clickable and the chevron is the template's own ActionIcon,
/// which replaced the hand-built drill-down rows (Border+Grid+Button+manual
/// chevron) whose arrow position, glyph and hit area drifted between pages.
/// Inline ComboBox/ToggleSwitch content keeps its own click semantics; the
/// capsule rows gate navigation through an IsClickEnabled binding so the
/// inline combo stays operable, and keep their "arrow only when custom"
/// behavior through IsActionIconVisible.
/// </summary>
public sealed class SettingsEntryRowHitAreaContractTests
{
    [Theory]
    [InlineData("src/DeskBox/Views/SettingsWindow.xaml", 6)]
    [InlineData("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml", 5)]
    [InlineData("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml", 1)]
    [InlineData("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml", 3)]
    public void EntryRows_AreClickableNativeSettingsCards(string path, int expectedCount)
    {
        string xaml = ReadRepositoryFile(path);

        int count = CountOccurrences(xaml, "IsClickEnabled=");
        Assert.True(count >= expectedCount,
            $"{path}: expected at least {expectedCount} IsClickEnabled entry cards, found {count}. " +
            "New entry rows must be native SettingsCards so the whole card stays clickable.");

        // The manual drill-down scaffolding must not come back.
        Assert.DoesNotContain("DrillDownRowStyle", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleRows_KeepTheirInlineCombosOperableUnderGating()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");

        // The three detail entry cards became SettingsExpanders (toolkit
        // expanders have no IsClickEnabled/IsActionIconVisible surface);
        // gating now runs through the combos' own IsEnabled bindings so the
        // inline controls stay operable, and the "custom only" child rows
        // stay visible but disabled outside custom presets — hiding them
        // left the expanders promising content they never showed.
        Assert.Contains("IsEnabled=\"{Binding IsBarSpacingEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsBarEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding ShowHoverResponseCustom}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding ShowAnimationCustom}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{Binding ShowHoverResponseCustom", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{Binding ShowAnimationCustom", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureWidgetRow_IsANativeSettingsCard()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.LocalizationAndWidgets.cs");

        Assert.Contains("new SettingsCard", code, StringComparison.Ordinal);
        Assert.Contains("IsClickEnabled = entry.HasSettingsPage", code, StringComparison.Ordinal);
        Assert.Contains("card.Click += FeatureWidgetSettingsButton_Click;", code, StringComparison.Ordinal);
        // The hand-built row scaffolding must not come back.
        Assert.DoesNotContain("Grid.SetColumnSpan", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph = \"\\uE76C\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DrillDownRowStyle", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string content, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
