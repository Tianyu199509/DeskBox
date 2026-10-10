using System.IO;

namespace DeskBox.Tests;

/// <summary>
/// The desktop-organization checkbox templates must keep hover/press and
/// check visuals in one cartesian VisualStateGroup. Two separate groups
/// (CommonStates + CheckStates) both wrote SelectionBox.Background; the
/// most recently entered state wins, so hovering a checked row erased the
/// checked fill and the check glyph became invisible in both themes
/// (audit 2026-10-11, P0).
/// </summary>
public sealed class DesktopOrganizationCheckBoxStatesContractTests
{
    [Theory]
    [InlineData("src/DeskBox/Controls/DesktopOrganizationTaskView.xaml", "NeutralSourceCheckBoxStyle")]
    [InlineData("src/DeskBox/Controls/DesktopOrganizationPreviewCard.xaml", "GroupSelectionToggleStyle")]
    public void CheckboxStyles_KeepCheckAndPointerStatesInOneCartesianGroup(
        string path,
        string styleKey)
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, path));

        int styleStart = xaml.IndexOf($"x:Key=\"{styleKey}\"", StringComparison.Ordinal);
        Assert.True(styleStart >= 0, $"{styleKey} not found in {path}");
        int styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        string style = xaml[styleStart..styleEnd];

        // One combined group; separate CommonStates/CheckStates groups are
        // the regression that erased the checked fill on hover.
        Assert.Contains("x:Name=\"CombinedStates\"", style, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CommonStates\"", style, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CheckStates\"", style, StringComparison.Ordinal);

        // The full cartesian set must stay complete; a missing combined
        // state falls back to the base template values mid-interaction.
        foreach (string state in new[]
        {
            "Unchecked", "UncheckedPointerOver", "UncheckedPressed", "UncheckedDisabled",
            "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled",
            "Indeterminate", "IndeterminatePointerOver", "IndeterminatePressed", "IndeterminateDisabled"
        })
        {
            Assert.Contains($"x:Name=\"{state}\"", style, StringComparison.Ordinal);
        }

        // Checked hover must keep the primary fill (the original bug).
        int checkedPointerOver = style.IndexOf("x:Name=\"CheckedPointerOver\"", StringComparison.Ordinal);
        int checkedPointerOverEnd = style.IndexOf("<VisualState x:Name=", checkedPointerOver + 1, StringComparison.Ordinal);
        if (checkedPointerOverEnd < 0)
        {
            checkedPointerOverEnd = style.Length;
        }
        string checkedPointerOverBody = style[checkedPointerOver..checkedPointerOverEnd];
        Assert.Contains(
            "SelectionBox.Background\" Value=\"{ThemeResource TextFillColorPrimaryBrush}\"",
            checkedPointerOverBody,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null &&
            !File.Exists(Path.Combine(dir, "DeskBox.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.False(dir is null, "Repository root not found");
        return dir;
    }
}
