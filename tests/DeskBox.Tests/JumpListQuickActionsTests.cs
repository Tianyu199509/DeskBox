using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class JumpListQuickActionsTests
{
    [Fact]
    public void BuildQuickActionItems_SeparatesManagedNewWidgetFromFolderMappingInEnglishAndChinese()
    {
        var enLoc = TestServices.CreateLocalizationService(SettingsService.LanguageEnglish);
        IReadOnlyList<(string DisplayName, string Argument)> enItems =
            JumpListService.BuildQuickActionItems(enLoc);

        Assert.Equal(5, enItems.Count);
        Assert.Equal(("New widget", JumpListService.ArgNewWidget), enItems[0]);
        Assert.Equal(("New folder mapping", JumpListService.ArgNewFolderMapping), enItems[1]);
        AssertStandardActionsPresent(enItems);

        var zhLoc = TestServices.CreateLocalizationService(SettingsService.LanguageChinese);
        IReadOnlyList<(string DisplayName, string Argument)> zhItems =
            JumpListService.BuildQuickActionItems(zhLoc);

        Assert.Equal(5, zhItems.Count);
        Assert.Equal(("新建格子", JumpListService.ArgNewWidget), zhItems[0]);
        Assert.Equal(("新建文件夹映射", JumpListService.ArgNewFolderMapping), zhItems[1]);
        AssertStandardActionsPresent(zhItems);
    }

    [Theory]
    [InlineData("--new-widget", JumpListService.ArgNewWidget)]
    [InlineData("--new-folder-mapping", JumpListService.ArgNewFolderMapping)]
    [InlineData("--new-folder-widget", JumpListService.ArgLegacyNewFolderWidget)]
    [InlineData("\"C:\\Program Files\\DeskBox\\DeskBox.exe\" \"--new-folder-widget\"", JumpListService.ArgLegacyNewFolderWidget)]
    [InlineData("\"C:\\Program Files\\DeskBox\\DeskBox.exe\" --new-widget", JumpListService.ArgNewWidget)]
    [InlineData("--toggle-widgets", JumpListService.ArgToggleWidgets)]
    [InlineData("--open-storage", JumpListService.ArgOpenStorage)]
    [InlineData("--open-settings", JumpListService.ArgOpenSettings)]
    [InlineData("--settings", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryGetJumpListArgument_ParsesCurrentAndLegacyArguments(
        string? commandLine,
        string? expected)
    {
        Assert.Equal(expected, JumpListService.TryGetJumpListArgument(commandLine));
    }

    private static void AssertStandardActionsPresent(
        IReadOnlyList<(string DisplayName, string Argument)> items)
    {
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(item.Argument));
        });

        Assert.Equal(
            items.Count,
            items.Select(item => item.DisplayName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            items.Count,
            items.Select(item => item.Argument).Distinct(StringComparer.Ordinal).Count());

        Assert.Contains(items, item => item.Argument == JumpListService.ArgToggleWidgets);
        Assert.Contains(items, item => item.Argument == JumpListService.ArgOpenStorage);
        Assert.Contains(items, item => item.Argument == JumpListService.ArgOpenSettings);
    }
}
