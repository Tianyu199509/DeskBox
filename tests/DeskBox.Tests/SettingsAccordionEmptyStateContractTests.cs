using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Contract tests for the accordion empty/invalid-state pass:
/// 1. The capsule hover-response and animation expanders keep their
///    custom-value sub-cards always present but disabled (IsEnabled bound
///    to the same view-model flags), so a preset with no custom stage
///    reads as a grayed row instead of an expandable shell that opens
///    empty. The expander-level preset gating (whole expander hidden when
///    its preset entry is unavailable) stays in place.
/// 2. The search hotkey expander is never disabled as a whole: only its
///    content controls gray out when the hotkey is unavailable, so the
///    expander can always be opened to read the status notice.
/// 3. The merged drag-and-drop behavior expander header carries a
///    description, matching the other group expanders.
/// </summary>
public sealed class SettingsAccordionEmptyStateContractTests
{
    private const string CapsuleSectionXaml =
        "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml";
    private const string SearchSectionXaml =
        "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml";
    private const string SearchSectionCode =
        "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs";
    private const string SettingsWindowXaml = "src/DeskBox/Views/SettingsWindow.xaml";

    [Fact]
    public void CapsuleMode_HoverAndAnimationSubCards_UseIsEnabledNotVisibility()
    {
        string xaml = ReadRepositoryFile(CapsuleSectionXaml);

        Assert.Contains("IsEnabled=\"{Binding ShowHoverResponseCustom}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding ShowAnimationCustom}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{Binding ShowHoverResponseCustom", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"{Binding ShowAnimationCustom", xaml, StringComparison.Ordinal);

        // The expander-level preset semantics survive: the hover expander
        // itself still hides when its preset entry is unavailable.
        Assert.Contains(
            "Visibility=\"{Binding ShowHoverResponseEntry", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleMode_CustomSubCardGatingCounts_ArePinned()
    {
        string xaml = ReadRepositoryFile(CapsuleSectionXaml);

        // Expand/collapse delay sub-cards in the hover expander and the
        // duration sub-card in the animation expander.
        Assert.Equal(2, CountOccurrences(xaml, "IsEnabled=\"{Binding ShowHoverResponseCustom}\""));
        Assert.Equal(1, CountOccurrences(xaml, "IsEnabled=\"{Binding ShowAnimationCustom}\""));
    }

    [Fact]
    public void SearchHotkey_ExpanderIsNeverGatedAsAWhole()
    {
        string code = ReadRepositoryFile(SearchSectionCode);

        Assert.DoesNotContain("SearchHotkeyExpander.IsEnabled", code, StringComparison.Ordinal);

        // Content-level gating instead: toggle, capture and reset controls
        // gray out when the hotkey is unavailable. The preset picker moved
        // into the recorder dialog, so it no longer needs section-level
        // gating.
        Assert.Contains("SearchHotkeyToggle.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("SearchHotkeyCaptureButton.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("ResetSearchHotkeyButton.IsEnabled", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHotkey_ExpanderStaysPlainInXaml()
    {
        string xaml = ReadRepositoryFile(SearchSectionXaml);

        Match? expander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.Search.Hotkey.Title\"");
        Assert.NotNull(expander);
        Assert.DoesNotContain("IsEnabled", expander.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void DragBehavior_ExpanderHeaderCarriesADescription()
    {
        string xaml = ReadRepositoryFile(SettingsWindowXaml);

        // The merged drag-and-drop group (拖放行为) folds the old 拖放提示
        // expander in; its header must keep carrying a description.
        Match? expander = FindOpeningTag(
            xaml, "toolkit:SettingsExpander", "HeaderKey=\"Settings.DragBehavior.Group.Title\"");
        Assert.NotNull(expander);
        Assert.Contains(
            "DescriptionKey=\"Settings.DragBehavior.Group.Description\"",
            expander.Value,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Finds the first opening tag of <paramref name="elementName"/> whose
    /// attribute list contains <paramref name="requiredContent"/>, or null
    /// when no such tag exists.
    /// </summary>
    private static Match? FindOpeningTag(string content, string elementName, string requiredContent)
    {
        var tagRegex = new Regex(
            "<" + Regex.Escape(elementName) + "(?:(?!>).)*?>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        foreach (Match tag in tagRegex.Matches(content))
        {
            if (tag.Value.Contains(requiredContent, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
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
