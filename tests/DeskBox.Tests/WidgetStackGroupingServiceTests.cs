using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetStackGroupingServiceTests
{
    [Theory]
    [InlineData("photo.png", WidgetStackCategory.Images)]
    [InlineData("report.docx", WidgetStackCategory.Documents)]
    [InlineData("clip.mp4", WidgetStackCategory.Videos)]
    [InlineData("song.flac", WidgetStackCategory.Audio)]
    [InlineData("backup.7z", WidgetStackCategory.Archives)]
    [InlineData("tool.exe", WidgetStackCategory.Applications)]
    [InlineData("unknown.bin", WidgetStackCategory.Other)]
    public void ResolveCategory_GroupsCommonFileKinds(string path, WidgetStackCategory expected)
    {
        var item = new WidgetItem { Path = path };

        Assert.Equal(
            expected,
            WidgetStackGroupingService.ResolveCategory(
                item,
                SettingsService.FileStackGroupByKind));
    }

    [Fact]
    public void ResolveCategory_PrioritizesFoldersAndShortcuts()
    {
        Assert.Equal(
            WidgetStackCategory.Folders,
            WidgetStackGroupingService.ResolveCategory(
                new WidgetItem { Path = "photo.png", IsFolder = true },
                SettingsService.FileStackGroupByKind));
        Assert.Equal(
            WidgetStackCategory.Applications,
            WidgetStackGroupingService.ResolveCategory(
                new WidgetItem { Path = "report.docx.lnk", IsShortcut = true },
                SettingsService.FileStackGroupByKind));
    }

    [Theory]
    [InlineData("picture", WidgetStackCategory.Images)]
    [InlineData("document", WidgetStackCategory.Documents)]
    [InlineData("music", WidgetStackCategory.Audio)]
    [InlineData("video", WidgetStackCategory.Videos)]
    [InlineData("program", WidgetStackCategory.Applications)]
    public void ResolveCategory_PrefersWindowsShellKind(string kind, WidgetStackCategory expected)
    {
        var item = new WidgetItem
        {
            Path = "unknown.custom",
            ShellKind = kind,
            IsShellKindLoaded = true
        };

        Assert.Equal(
            expected,
            WidgetStackGroupingService.ResolveCategory(
                item,
                SettingsService.FileStackGroupByKind));
    }

    [Theory]
    [InlineData(0, WidgetStackCategory.Today)]
    [InlineData(-1, WidgetStackCategory.Yesterday)]
    [InlineData(-5, WidgetStackCategory.PreviousSevenDays)]
    [InlineData(-20, WidgetStackCategory.PreviousThirtyDays)]
    [InlineData(-90, WidgetStackCategory.Earlier)]
    public void ResolveCategory_UsesFriendlyDateBuckets(int dayOffset, WidgetStackCategory expected)
    {
        var now = new DateTime(2026, 7, 17, 12, 0, 0);
        var item = new WidgetItem { AddedAt = new DateTimeOffset(now.AddDays(dayOffset)) };

        Assert.Equal(
            expected,
            WidgetStackGroupingService.ResolveCategory(
                item,
                SettingsService.FileStackGroupByDateAdded,
                now));
    }

    [Fact]
    public void NormalizeGroupBy_MigratesLegacyCreatedDateValue()
    {
        Assert.Equal(
            SettingsService.FileStackGroupByDateAdded,
            SettingsService.NormalizeFileStackGroupBy(
                SettingsService.FileStackGroupByDateCreated));
    }

    [Fact]
    public void Group_PreservesSourceOrderWithinEachStack()
    {
        var first = new WidgetItem { Path = "b.png" };
        var second = new WidgetItem { Path = "a.png" };

        var group = Assert.Single(WidgetStackGroupingService.Group(
            [first, second],
            SettingsService.FileStackGroupByKind));

        Assert.Equal([first, second], group.Items);
    }

    [Fact]
    public void Group_OrdersMembersByNameWithStableTies()
    {
        var firstA = new WidgetItem { Path = "first.png", Name = "A" };
        var b = new WidgetItem { Path = "b.png", Name = "B" };
        var secondA = new WidgetItem { Path = "second.png", Name = "A" };

        var group = Assert.Single(WidgetStackGroupingService.Group(
            [b, firstA, secondA],
            SettingsService.FileStackGroupByKind,
            orderBy: SettingsService.FileStackOrderByName));

        Assert.Equal([firstA, secondA, b], group.Items);
    }

    [Fact]
    public void Group_OrdersNewestAddedAndModifiedFirst()
    {
        DateTime now = new(2026, 7, 17, 12, 0, 0);
        var older = new WidgetItem
        {
            Path = "older.png",
            AddedAt = new DateTimeOffset(now.AddHours(-2)),
            LastModified = now.AddHours(-1)
        };
        var newer = new WidgetItem
        {
            Path = "newer.png",
            AddedAt = new DateTimeOffset(now.AddHours(-1)),
            LastModified = now
        };

        var byAdded = Assert.Single(WidgetStackGroupingService.Group(
            [older, newer],
            SettingsService.FileStackGroupByKind,
            orderBy: SettingsService.FileStackOrderByDateAdded));
        var byModified = Assert.Single(WidgetStackGroupingService.Group(
            [older, newer],
            SettingsService.FileStackGroupByKind,
            orderBy: SettingsService.FileStackOrderByDateModified));

        Assert.Equal([newer, older], byAdded.Items);
        Assert.Equal([newer, older], byModified.Items);
    }

    [Fact]
    public void Group_CustomRulesUseFirstMatchAndRulePriority()
    {
        var png = new WidgetItem { Path = "cover.png" };
        var psd = new WidgetItem { Path = "draft.psd" };
        FileStackCustomRule[] rules =
        [
            new()
            {
                Id = "images",
                Name = "Images",
                Extensions = [".png"]
            },
            new()
            {
                Id = "design",
                Name = "Design",
                Extensions = [".png", ".psd"]
            }
        ];

        var groups = WidgetStackGroupingService.Group(
            [png, psd],
            SettingsService.FileStackGroupByCustom,
            customRules: rules);

        Assert.Collection(
            groups,
            group =>
            {
                Assert.Equal("Custom:images", group.EffectiveKey);
                Assert.Equal("Images", group.DisplayName);
                Assert.Equal([png], group.Items);
            },
            group =>
            {
                Assert.Equal("Custom:design", group.EffectiveKey);
                Assert.Equal("Design", group.DisplayName);
                Assert.Equal([psd], group.Items);
            });
    }

    [Fact]
    public void Group_CustomRulesCanKeepUnmatchedItemsLoose()
    {
        var first = new WidgetItem { Path = "first.bin" };
        var second = new WidgetItem { Path = "second.raw" };

        var groups = WidgetStackGroupingService.Group(
            [first, second],
            SettingsService.FileStackGroupByCustom,
            customRules: [],
            unmatchedBehavior: SettingsService.FileStackUnmatchedKeepLoose);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.False(group.CanStack));
        Assert.Equal([first], groups[0].Items);
        Assert.Equal([second], groups[1].Items);
    }

    [Fact]
    public void Group_CustomRulesCanCollectUnmatchedItemsIntoOther()
    {
        var first = new WidgetItem { Path = "first.bin" };
        var second = new WidgetItem { Path = "second.raw" };

        var group = Assert.Single(WidgetStackGroupingService.Group(
            [first, second],
            SettingsService.FileStackGroupByCustom,
            customRules: [],
            unmatchedBehavior: SettingsService.FileStackUnmatchedOther));

        Assert.True(group.CanStack);
        Assert.Equal("Custom:Other", group.EffectiveKey);
        Assert.Equal([first, second], group.Items);
    }

    [Fact]
    public void Group_CustomRulesExtendBuiltInKindGrouping()
    {
        var prototype = new WidgetItem { Path = "prototype.rp" };
        var document = new WidgetItem { Path = "notes.txt" };
        FileStackCustomRule[] rules =
        [
            new()
            {
                Id = "prototype",
                Name = "Prototype",
                Extensions = [".rp"]
            }
        ];

        var groups = WidgetStackGroupingService.Group(
            [prototype, document],
            SettingsService.FileStackGroupByKind,
            customRules: rules);

        Assert.Collection(
            groups,
            group =>
            {
                Assert.Equal("Custom:prototype", group.EffectiveKey);
                Assert.Equal("Prototype", group.DisplayName);
                Assert.Equal([prototype], group.Items);
            },
            group =>
            {
                Assert.Equal(WidgetStackCategory.Documents, group.Category);
                Assert.Equal([document], group.Items);
            });
    }
}
