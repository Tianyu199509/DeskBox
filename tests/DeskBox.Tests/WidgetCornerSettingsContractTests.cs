namespace DeskBox.Tests;

public sealed class WidgetCornerSettingsContractTests
{
    [Fact]
    public void CornerSelector_OffersRoundSmallAndSquareWithRoundFirst()
    {
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs"));

        Assert.Contains(
            "WidgetCornerKinds.Round,\n        WidgetCornerKinds.Small,\n        WidgetCornerKinds.Square",
            editor.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("CornerDefault", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Corner.Default", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void CornerSelector_LabelKeysAlignWithTheValueArray()
    {
        // The label keys zip onto the Round,Small,Square value array by index;
        // swapping the key order made "Large radius" apply Square and vice
        // versa, so pin the alignment.
        string editor = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs"));

        int round = editor.IndexOf("\"Settings.Corner.Round\"", StringComparison.Ordinal);
        int small = editor.IndexOf("\"Settings.Corner.Small\"", StringComparison.Ordinal);
        int square = editor.IndexOf("\"Settings.Corner.Square\"", StringComparison.Ordinal);

        Assert.True(round >= 0 && small >= 0 && square >= 0,
            "The corner option label keys are missing.");
        Assert.True(round < small && small < square,
            "Corner label keys must stay index-aligned with the Round,Small,Square value array.");
    }

    [Fact]
    public void CornerLocalization_NoLongerContainsSystemDefaultOption()
    {
        string stringsRoot = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string file in Directory.EnumerateFiles(stringsRoot, "*.json"))
        {
            string json = File.ReadAllText(file);
            Assert.DoesNotContain("\"Settings.Corner.Default\"", json, StringComparison.Ordinal);
        }
    }
}
