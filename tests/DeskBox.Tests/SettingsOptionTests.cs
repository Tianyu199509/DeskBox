using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class SettingsOptionTests
{
    [Fact]
    public void CreateSelectionOptionsPreservesTypedValuesAndDisplayOrder()
    {
        IReadOnlyList<DeskBox.Models.SettingsOption> options =
            SettingsViewModel.CreateSelectionOptions(
                [15, 30, 60],
                ["15 minutes", "30 minutes", "60 minutes"]);

        Assert.Equal(3, options.Count);
        Assert.Equal(15, options[0].Value);
        Assert.Equal("30 minutes", options[1].DisplayName);
        Assert.Equal(60, options[2].Value);
    }

    [Fact]
    public void CreateSelectionOptionsRejectsMismatchedValuesAndDisplayNames()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SettingsViewModel.CreateSelectionOptions(
                ["System", "Light"],
                ["Follow system"]));
    }
}
