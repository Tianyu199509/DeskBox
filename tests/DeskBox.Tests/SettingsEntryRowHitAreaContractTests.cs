using System.Xml.Linq;

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
    [InlineData("src/DeskBox/Views/SettingsWindow.xaml", 7)]
    [InlineData("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml", 0)]
    [InlineData("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml", 0)]
    [InlineData("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml", 2)]
    [InlineData("src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml", 1)]
    public void EntryRows_AreClickableNativeSettingsCards(string path, int expectedCount)
    {
        string xaml = ReadRepositoryFile(path);

        int count = CountOccurrences(xaml, "IsClickEnabled=");
        Assert.Equal(expectedCount, count);

        // The manual drill-down scaffolding must not come back.
        Assert.DoesNotContain("DrillDownRowStyle", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleRows_KeepTheirInlineCombosOperableUnderGating()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");

        Assert.Contains("IsEnabled=\"{Binding IsBarSpacingEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsBarEnabled}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowHoverResponseCustom, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding ShowAnimationCustom, Converter={StaticResource SettingsBoolToVisibilityConverter}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"{Binding ShowHoverResponseCustom}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"{Binding ShowAnimationCustom}\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/SettingsWindow.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml")]
    public void EntryRows_NeverEmbedManualChevronInsideClickableSettingsCard(string path)
    {
        XDocument document = XDocument.Parse(ReadRepositoryFile(path));
        XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";

        foreach (XElement card in document.Descendants(toolkit + "SettingsCard"))
        {
            if (card.Attribute("IsClickEnabled") is null)
            {
                continue;
            }

            bool hasManualChevron = card.Descendants()
                .Any(element =>
                    element.Name.LocalName == "FontIcon" &&
                    string.Equals((string?)element.Attribute("Glyph"), "\uE76C", StringComparison.Ordinal));
            Assert.False(
                hasManualChevron,
                $"{path}: clickable SettingsCard (Tag='{card.Attribute("Tag")?.Value}') must rely on SettingsCard's built-in ActionIcon instead of embedding a duplicate manual \\uE76C FontIcon.");
        }
    }

    [Fact]
    public void FeatureWidgetRow_IsANativeSettingsCard()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.LocalizationAndWidgets.cs");

        Assert.Contains("new SettingsCard", code, StringComparison.Ordinal);
        Assert.Contains("IsClickEnabled = entry.HasSettingsPage", code, StringComparison.Ordinal);
        Assert.Contains("card.Click += FeatureWidgetSettingsButton_Click;", code, StringComparison.Ordinal);
        // The hand-built row scaffolding and manual duplicate chevron must not come back.
        Assert.DoesNotContain("Grid.SetColumnSpan", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph = \"\\uE76C\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DrillDownRowStyle", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopOrganizationRuleRows_StayClickableWithoutAManualChevron()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs");

        // Auto-organization opens directly into its 2nd-level rules page (zero 3rd-level hops)
        // via a native clickable SettingsCard, and rules use SelectorBar + ComboBox/ToggleButton pills
        // without manual chevron hacks.
        Assert.Contains("x:Name=\"AutoOrganizationEntryCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsClickEnabled=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RulesViewSelectorBar\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph = \"\\uE76C\"", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/SettingsWindow.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/ClipboardSettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml")]
    public void SettingsToggleSwitches_UseZeroMinWidthInsteadOfControlMinWidth(string path)
    {
        XDocument document = XDocument.Parse(ReadRepositoryFile(path));
        XNamespace winui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (XElement toggle in document.Descendants(winui + "ToggleSwitch"))
        {
            string? minWidth = (string?)toggle.Attribute("MinWidth");
            Assert.False(
                string.Equals(minWidth, "{StaticResource SettingsControlMinWidth}", StringComparison.Ordinal),
                $"{path}: ToggleSwitch must use MinWidth=\"0\" so it stays right-aligned inside SettingsCard.");
        }
    }

    [Fact]
    public void DesktopOrganizationAndFileGridSiblingSwitcher_StayAlignedWithTopLevelPromotion()
    {
        string orgXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");
        string fileWidgetXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");
        string windowXaml = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml");
        string navigation = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");
        string deferred = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.DeferredSections.cs");

        Assert.Contains("Style=\"{StaticResource SettingsSubsectionTitleTextStyle}\"", orgXaml, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.Key=\"DesktopOrganization.Settings.Title\"", orgXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OrganizeNowCard\"", orgXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutoOrganizationEntryCard\"", orgXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AutoOrganizationSubPagePanel\"", orgXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SectionTitleTextStyle}\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DesktopOrganizationPart\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FileGridSiblingSegmented", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FileGridSiblingSegmented", deferred, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"DesktopOrganizationSettings\", \"ManagedStorage\", \"FileDisplaySettings\", \"FileStackSettings\"",
            navigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"FileStorageSettings\" or \"FileDisplaySettings\" or \"ManagedStorage\" or \"DesktopOrganizationSettings\" => \"AppearanceDetail\"",
            navigation,
            StringComparison.Ordinal);
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
