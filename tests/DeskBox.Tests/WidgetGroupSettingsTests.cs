using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupSettingsTests
{
    [Fact]
    public void Normalize_RepairsMembershipActiveMemberAndSharedModes()
    {
        var settings = new AppSettings
        {
            Widgets =
            [
                CreateWidget("a"),
                CreateWidget("b"),
                CreateWidget("c"),
                CreateWidget("d")
            ],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "group-1",
                    MemberIds = ["a", "missing", "b", "b"],
                    ActiveMemberId = "missing",
                    ChromeMode = "invalid",
                    CollapseBehavior = "invalid",
                    Width = double.NaN,
                    Height = -1
                },
                new WidgetGroupConfig
                {
                    Id = "group-2",
                    MemberIds = ["b", "c", "d"],
                    ActiveMemberId = "b"
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));

        Assert.Equal(2, settings.WidgetGroups.Count);
        Assert.Equal(["a", "b"], settings.WidgetGroups[0].MemberIds);
        Assert.Equal("a", settings.WidgetGroups[0].ActiveMemberId);
        Assert.Equal(WidgetChromeModeNames.Standard, settings.WidgetGroups[0].ChromeMode);
        Assert.Equal(WidgetCollapseBehaviorNames.System, settings.WidgetGroups[0].CollapseBehavior);
        Assert.Equal(300, settings.WidgetGroups[0].Width);
        Assert.Equal(400, settings.WidgetGroups[0].Height);

        Assert.Equal(["c", "d"], settings.WidgetGroups[1].MemberIds);
        Assert.Equal("c", settings.WidgetGroups[1].ActiveMemberId);
    }

    [Fact]
    public void Normalize_RemovesDeletedAndSingleMemberGroups()
    {
        var settings = new AppSettings
        {
            Widgets = [CreateWidget("a"), CreateWidget("b"), CreateWidget("c")],
            DeletedWidgetIds = ["b"],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    MemberIds = ["a", "b"],
                    ActiveMemberId = "a"
                },
                new WidgetGroupConfig
                {
                    MemberIds = ["b", "c"],
                    ActiveMemberId = "b"
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));
        Assert.Empty(settings.WidgetGroups);
        Assert.True(WidgetGroupSettings.IsActiveMember(settings, "a"));
        Assert.True(WidgetGroupSettings.IsActiveMember(settings, "c"));
    }

    [Fact]
    public void Normalize_EnforcesMaximumMemberCountAndUniqueGroupIds()
    {
        var settings = new AppSettings
        {
            Widgets = Enumerable.Range(0, 10)
                .Select(index => CreateWidget($"widget-{index}"))
                .ToList(),
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "duplicate",
                    MemberIds = Enumerable.Range(0, 9)
                        .Select(index => $"widget-{index}")
                        .ToList(),
                    ActiveMemberId = "widget-8"
                },
                new WidgetGroupConfig
                {
                    Id = "duplicate",
                    MemberIds = ["widget-8", "widget-9"],
                    ActiveMemberId = "widget-8"
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));

        Assert.Equal(2, settings.WidgetGroups.Count);
        WidgetGroupConfig group = settings.WidgetGroups[0];
        Assert.Equal(WidgetGroupSettings.MaximumMemberCount, group.MemberIds.Count);
        Assert.Equal("widget-0", group.ActiveMemberId);
        Assert.Equal(["widget-8", "widget-9"], settings.WidgetGroups[1].MemberIds);
        Assert.NotEqual(group.Id, settings.WidgetGroups[1].Id);
    }

    [Fact]
    public void Normalize_RepairsSurfaceIdsWithoutChangingStableIdentity()
    {
        var settings = new AppSettings
        {
            Widgets =
            [
                CreateWidget("a"),
                CreateWidget("b"),
                CreateWidget("c"),
                CreateWidget("d"),
                CreateWidget("e"),
                CreateWidget("f")
            ],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "group-1",
                    SurfaceId = "stable-surface",
                    MemberIds = ["a", "b"],
                    ActiveMemberId = "a"
                },
                new WidgetGroupConfig
                {
                    Id = "group-2",
                    SurfaceId = "stable-surface",
                    MemberIds = ["c", "d"],
                    ActiveMemberId = "c"
                },
                new WidgetGroupConfig
                {
                    Id = "group-3",
                    SurfaceId = "",
                    MemberIds = ["e", "f"],
                    ActiveMemberId = "e"
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));
        Assert.Equal("stable-surface", settings.WidgetGroups[0].SurfaceId);
        Assert.Equal(3, settings.WidgetGroups.Select(group => group.SurfaceId).Distinct().Count());
        Assert.All(settings.WidgetGroups, group => Assert.False(string.IsNullOrWhiteSpace(group.SurfaceId)));
        Assert.False(WidgetGroupSettings.Normalize(settings));
    }

    [Fact]
    public void IsActiveMember_OnlyAcceptsTheVisibleMemberOfAGroup()
    {
        var settings = new AppSettings
        {
            Widgets = [CreateWidget("a"), CreateWidget("b"), CreateWidget("c")],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    MemberIds = ["a", "b"],
                    ActiveMemberId = "b"
                }
            ]
        };

        Assert.False(WidgetGroupSettings.IsActiveMember(settings, "a"));
        Assert.True(WidgetGroupSettings.IsActiveMember(settings, "b"));
        Assert.True(WidgetGroupSettings.IsActiveMember(settings, "c"));
    }

    [Fact]
    public void ResolveRestorableActiveMemberId_FallsBackWithoutChangingMembership()
    {
        var unavailable = CreateWidget("unavailable");
        var available = CreateWidget("available");
        var group = new WidgetGroupConfig
        {
            MemberIds = [unavailable.Id, available.Id],
            ActiveMemberId = unavailable.Id
        };
        var settings = new AppSettings
        {
            Widgets = [unavailable, available],
            WidgetGroups = [group]
        };

        string? activeId = WidgetGroupSettings.ResolveRestorableActiveMemberId(
            settings,
            group,
            widget => string.Equals(widget.Id, available.Id, StringComparison.Ordinal));

        Assert.Equal(available.Id, activeId);
        Assert.Equal([unavailable.Id, available.Id], group.MemberIds);
        Assert.Equal(unavailable.Id, group.ActiveMemberId);
    }

    [Fact]
    public void ApplyDefaultPreferences_PreservesWidgetGroupsAsUserLayoutData()
    {
        var group = new WidgetGroupConfig
        {
            Id = "group",
            MemberIds = ["a", "b"],
            ActiveMemberId = "a"
        };
        var settings = new AppSettings
        {
            Widgets = [CreateWidget("a"), CreateWidget("b")],
            WidgetGroups = [group]
        };

        SettingsService.ApplyDefaultPreferences(settings);

        Assert.Same(group, Assert.Single(settings.WidgetGroups));
    }

    [Fact]
    public void Normalize_RepairsNavigationStylesAndPreservesStableChoices()
    {
        var settings = new AppSettings
        {
            WidgetGroupDefaultNavigationStyle = "invalid",
            Widgets =
            [
                CreateWidget("a"),
                CreateWidget("b"),
                CreateWidget("c"),
                CreateWidget("d")
            ],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    Id = "group-1",
                    SurfaceId = "surface-1",
                    MemberIds = ["a", "b"],
                    ActiveMemberId = "a",
                    NavigationStyle = "invalid"
                },
                new WidgetGroupConfig
                {
                    Id = "group-2",
                    SurfaceId = "surface-2",
                    MemberIds = ["c", "d"],
                    ActiveMemberId = "c",
                    NavigationStyle = WidgetGroupNavigationStyles.FollowDefault
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));
        Assert.Equal(
            WidgetGroupNavigationStyles.Stack,
            settings.WidgetGroupDefaultNavigationStyle);
        Assert.Equal(
            WidgetGroupNavigationStyles.Stack,
            settings.WidgetGroups[0].NavigationStyle);
        Assert.Equal(
            WidgetGroupNavigationStyles.FollowDefault,
            settings.WidgetGroups[1].NavigationStyle);
        Assert.False(WidgetGroupSettings.Normalize(settings));
    }

    [Fact]
    public void Normalize_MigratesTitleModeWheelPreferenceAndVisibleChrome()
    {
        var settings = new AppSettings
        {
            WidgetGroupDefaultTitleDisplayMode = "invalid",
            Widgets =
            [
                CreateWidget("a"),
                CreateWidget("b")
            ],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    SurfaceId = "surface",
                    MemberIds = ["a", "b"],
                    ActiveMemberId = "a",
                    NavigationStyle = WidgetGroupNavigationStyles.Tabs,
                    TitleDisplayMode = "invalid",
                    WheelSwitchEnabled = null,
                    ChromeMode = WidgetChromeModeNames.Hidden
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));

        WidgetGroupConfig group = Assert.Single(settings.WidgetGroups);
        Assert.Equal(
            WidgetGroupTitleDisplayModes.IconAndText,
            settings.WidgetGroupDefaultTitleDisplayMode);
        Assert.Equal(
            WidgetGroupTitleDisplayModes.IconAndText,
            group.TitleDisplayMode);
        Assert.False(group.WheelSwitchEnabled);
        Assert.Equal(WidgetChromeModeNames.Standard, group.ChromeMode);
        Assert.False(WidgetGroupSettings.Normalize(settings));
    }

    [Fact]
    public void Normalize_SynchronizesMemberVisibilityWithTheVisibleGroupSurface()
    {
        var first = CreateWidget("first");
        var second = CreateWidget("second");
        first.IsVisible = true;
        second.IsVisible = false;
        var settings = new AppSettings
        {
            Widgets = [first, second],
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    SurfaceId = "surface",
                    MemberIds = [first.Id, second.Id],
                    ActiveMemberId = second.Id,
                    IsVisible = true
                }
            ]
        };

        Assert.True(WidgetGroupSettings.Normalize(settings));
        Assert.All(settings.Widgets, widget => Assert.True(widget.IsVisible));
        Assert.False(WidgetGroupSettings.Normalize(settings));

        settings.WidgetGroups[0].IsVisible = false;

        Assert.True(WidgetGroupSettings.Normalize(settings));
        Assert.All(settings.Widgets, widget => Assert.False(widget.IsVisible));
    }

    [Theory]
    [InlineData(WidgetGroupNavigationStyles.Tabs)]
    [InlineData(WidgetGroupNavigationStyles.Stack)]
    public void Resolve_FollowDefaultUsesNormalizedGlobalChoice(string defaultStyle)
    {
        Assert.Equal(
            defaultStyle,
            WidgetGroupNavigationStyles.Resolve(
                WidgetGroupNavigationStyles.FollowDefault,
                defaultStyle));
    }

    [Fact]
    public void Normalize_LegacyAutoUsesStackNavigation()
    {
        Assert.Equal(
            WidgetGroupNavigationStyles.Stack,
            WidgetGroupNavigationStyles.Normalize("Auto", allowFollowDefault: false));
    }

    private static WidgetConfig CreateWidget(string id)
    {
        return new WidgetConfig { Id = id, Name = id };
    }
}
