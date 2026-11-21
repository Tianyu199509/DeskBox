using DeskBox.Models;
using DeskBox.Services;
using System.Text.Json;

namespace DeskBox.Tests;

public sealed class WidgetFileStackSettingsTests
{
    [Fact]
    public void Resolve_UsesGlobalDefaultsWithoutOverrides()
    {
        var config = new WidgetConfig();

        Assert.True(WidgetFileStackSettings.ResolveEnabled(config, globalDefault: true));
        Assert.Equal(
            SettingsService.FileStackGroupByDateModified,
            WidgetFileStackSettings.ResolveGroupBy(
                config,
                SettingsService.FileStackGroupByDateModified));
        Assert.Equal(5, WidgetFileStackSettings.ResolveThreshold(config, 5));
        Assert.Equal(
            SettingsService.FileStackOrderByName,
            WidgetFileStackSettings.ResolveOrderBy(
                config,
                SettingsService.FileStackOrderByName));
        Assert.Equal(
            SettingsService.FileStackOpenModePopover,
            WidgetFileStackSettings.ResolveOpenMode(
                config,
                SettingsService.FileStackOpenModePopover));
        Assert.True(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Fact]
    public void Resolve_PrefersWidgetOverrides()
    {
        var config = new WidgetConfig();
        WidgetFileStackSettings.SetEnabledOverride(config, false);
        WidgetFileStackSettings.SetGroupByOverride(
            config,
            SettingsService.FileStackGroupByDateModified);
        WidgetFileStackSettings.SetThresholdOverride(config, 2);
        WidgetFileStackSettings.SetOrderByOverride(
            config,
            SettingsService.FileStackOrderByDateModified);
        WidgetFileStackSettings.SetOpenModeOverride(
            config,
            SettingsService.FileStackOpenModePopover);

        Assert.False(WidgetFileStackSettings.ResolveEnabled(config, globalDefault: true));
        Assert.Equal(
            SettingsService.FileStackGroupByDateModified,
            WidgetFileStackSettings.ResolveGroupBy(
                config,
                SettingsService.FileStackGroupByKind));
        Assert.Equal(2, WidgetFileStackSettings.ResolveThreshold(config, 5));
        Assert.Equal(
            SettingsService.FileStackOrderByDateModified,
            WidgetFileStackSettings.ResolveOrderBy(
                config,
                SettingsService.FileStackOrderByWidget));
        Assert.Equal(
            SettingsService.FileStackOpenModePopover,
            WidgetFileStackSettings.ResolveOpenMode(
                config,
                SettingsService.FileStackOpenModeInline));
        Assert.False(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Fact]
    public void ClearOverrides_RestoresGlobalDefaults()
    {
        var config = new WidgetConfig();
        WidgetFileStackSettings.SetEnabledOverride(config, true);
        WidgetFileStackSettings.SetGroupByOverride(
            config,
            SettingsService.FileStackGroupByDateModified);
        WidgetFileStackSettings.SetThresholdOverride(config, 5);
        WidgetFileStackSettings.SetOrderByOverride(
            config,
            SettingsService.FileStackOrderByName);
        WidgetFileStackSettings.SetOpenModeOverride(
            config,
            SettingsService.FileStackOpenModePopover);

        WidgetFileStackSettings.ClearOverrides(config);

        Assert.Null(WidgetFileStackSettings.GetEnabledOverride(config));
        Assert.Null(WidgetFileStackSettings.GetGroupByOverride(config));
        Assert.Null(WidgetFileStackSettings.GetThresholdOverride(config));
        Assert.Null(WidgetFileStackSettings.GetOrderByOverride(config));
        Assert.Null(WidgetFileStackSettings.GetOpenModeOverride(config));
        Assert.True(WidgetFileStackSettings.FollowsGlobalDefaults(config));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    public void NormalizeThreshold_AllowsOnlySupportedOptions(int value, int expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeFileStackThreshold(value));
    }

    [Theory]
    [InlineData(null, SettingsService.FileStackOrderByWidget)]
    [InlineData("name", SettingsService.FileStackOrderByName)]
    [InlineData("dateadded", SettingsService.FileStackOrderByDateAdded)]
    [InlineData("datemodified", SettingsService.FileStackOrderByDateModified)]
    [InlineData("unexpected", SettingsService.FileStackOrderByWidget)]
    public void NormalizeOrder_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeFileStackOrderBy(value));
    }

    [Fact]
    public void NormalizeOverrides_RemovesInvalidMetadataAndCanonicalizesValues()
    {
        var config = new WidgetConfig
        {
            Metadata = new Dictionary<string, string>
            {
                [WidgetFileStackSettings.EnabledOverrideMetadataKey] = "true",
                [WidgetFileStackSettings.GroupByOverrideMetadataKey] = "datecreated",
                [WidgetFileStackSettings.ThresholdOverrideMetadataKey] = "4",
                [WidgetFileStackSettings.OrderByOverrideMetadataKey] = "unexpected",
                [WidgetFileStackSettings.OpenModeOverrideMetadataKey] = "popover"
            }
        };

        Assert.True(WidgetFileStackSettings.NormalizeOverrides(config));
        Assert.Equal("True", config.Metadata[WidgetFileStackSettings.EnabledOverrideMetadataKey]);
        Assert.Equal(
            SettingsService.FileStackGroupByKind,
            config.Metadata[WidgetFileStackSettings.GroupByOverrideMetadataKey]);
        Assert.False(config.Metadata.ContainsKey(WidgetFileStackSettings.ThresholdOverrideMetadataKey));
        Assert.False(config.Metadata.ContainsKey(WidgetFileStackSettings.OrderByOverrideMetadataKey));
        Assert.Equal(
            SettingsService.FileStackOpenModePopover,
            config.Metadata[WidgetFileStackSettings.OpenModeOverrideMetadataKey]);
    }

    [Fact]
    public void RebaseManagedFolderPaths_PreservesStacksWhenWidgetFolderIsRenamed()
    {
        string oldRoot = @"C:\DeskBox\Old Widget";
        string newRoot = @"C:\DeskBox\Renamed Widget";
        var addedAt = new DateTimeOffset(
            2026,
            8,
            24,
            9,
            30,
            0,
            TimeSpan.FromHours(8));
        var config = new WidgetConfig
        {
            FileAddedAtByPath = new Dictionary<string, DateTimeOffset>
            {
                [Path.Combine(oldRoot, "one.txt")] = addedAt,
                [@"D:\Outside\keep.txt"] = addedAt.AddMinutes(1)
            }
        };
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>
            {
                ["Manual:alpha"] =
                [
                    Path.Combine(oldRoot, "one.txt"),
                    Path.Combine(oldRoot, "nested", "two.png")
                ]
            });
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                "Manual:alpha",
                "Item:" + Path.Combine(oldRoot, "loose.docx").ToUpperInvariant(),
                @"Item:D:\OUTSIDE\KEEP.TXT"
            ]);

        Assert.True(WidgetFileStackSettings.RebaseManagedFolderPaths(
            config,
            oldRoot,
            newRoot));

        Dictionary<string, List<string>> members =
            WidgetFileStackSettings.GetStackMemberOverrides(config);
        Assert.Equal(
            [
                Path.Combine(newRoot, "one.txt"),
                Path.Combine(newRoot, "nested", "two.png")
            ],
            members["Manual:alpha"]);
        Assert.Equal(
            [
                "Manual:alpha",
                "Item:" + Path.Combine(newRoot, "loose.docx").ToUpperInvariant(),
                @"Item:D:\OUTSIDE\KEEP.TXT"
            ],
            WidgetFileStackSettings.GetStackOrder(config));
        Assert.Equal(
            addedAt,
            config.FileAddedAtByPath[Path.Combine(newRoot, "one.txt")]);
        Assert.Equal(
            addedAt.AddMinutes(1),
            config.FileAddedAtByPath[@"D:\Outside\keep.txt"]);
        Assert.DoesNotContain(
            config.FileAddedAtByPath.Keys,
            path => path.StartsWith(
                oldRoot,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeOverrides_PrunesOnlyOrphanedManualStackMetadata()
    {
        var config = new WidgetConfig();
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>
            {
                ["Manual:active"] = [@"C:\A.txt", @"C:\B.txt"],
                ["Manual:orphan"] = [@"C:\Only.txt"]
            });
        WidgetFileStackSettings.SetStackNameOverrides(
            config,
            new Dictionary<string, string>
            {
                ["Manual:active"] = "Active",
                ["Manual:orphan"] = "Old manual name",
                ["Documents"] = "Automatic name"
            });
        WidgetFileStackSettings.SetDisabledStacks(
            config,
            ["Manual:orphan", "Documents"]);
        WidgetFileStackSettings.SetStackOrder(
            config,
            ["Manual:active", "Manual:orphan", "Documents"]);

        Assert.True(WidgetFileStackSettings.NormalizeOverrides(config));

        Assert.Equal(
            ["Manual:active"],
            WidgetFileStackSettings.GetStackMemberOverrides(config).Keys);
        Dictionary<string, string> names =
            WidgetFileStackSettings.GetStackNameOverrides(config);
        Assert.Equal("Active", names["Manual:active"]);
        Assert.Equal("Automatic name", names["Documents"]);
        Assert.DoesNotContain("Manual:orphan", names.Keys);
        Assert.Equal(
            ["Documents"],
            WidgetFileStackSettings.GetDisabledStacks(config));
        Assert.Equal(
            ["Manual:active", "Documents"],
            WidgetFileStackSettings.GetStackOrder(config));
    }

    [Fact]
    public void WidgetConfig_FileAddedTimesRoundTripThroughJson()
    {
        var addedAt = new DateTimeOffset(2026, 7, 17, 1, 45, 0, TimeSpan.FromHours(8));
        var config = new WidgetConfig
        {
            FileAddedAtTrackingInitialized = true,
            FileAddedAtByPath = new Dictionary<string, DateTimeOffset>
            {
                [@"C:\Work\report.docx"] = addedAt
            }
        };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.NotNull(restored);
        Assert.True(restored.FileAddedAtTrackingInitialized);
        Assert.Equal(addedAt, restored.FileAddedAtByPath[@"C:\Work\report.docx"]);
    }

    [Fact]
    public void CustomGroupingOverride_IsAcceptedAndCanonicalized()
    {
        var config = new WidgetConfig();

        WidgetFileStackSettings.SetGroupByOverride(config, "custom");

        Assert.Equal(
            SettingsService.FileStackGroupByCustom,
            WidgetFileStackSettings.GetGroupByOverride(config));
        Assert.Equal(
            SettingsService.FileStackGroupByCustom,
            config.Metadata[WidgetFileStackSettings.GroupByOverrideMetadataKey]);
    }

    [Fact]
    public void NormalizeExtensions_AcceptsFriendlyInputAndRemovesDuplicates()
    {
        var extensions = SettingsService.NormalizeFileStackExtensions(
            ["PSD", "*.AI", ".psd", "  .FIG  ", @"bad\path"]);

        Assert.Equal([".psd", ".ai", ".fig"], extensions);
    }

    [Fact]
    public void StackOrder_RoundTripPreservesMixedDisplayUnitOrder()
    {
        var config = new WidgetConfig();
        string[] order =
        [
            "Documents",
            @"Item:C:\B",
            "Images",
            @"Item:C:\A"
        ];

        WidgetFileStackSettings.SetStackOrder(config, order);

        Assert.Equal(order, WidgetFileStackSettings.GetStackOrder(config));
    }

    [Fact]
    public void StackMemberOverrides_RoundTripPreservesManualMemberships()
    {
        var config = new WidgetConfig();
        var memberships =
            new Dictionary<string, List<string>>
            {
                ["Manual:alpha"] =
                [
                    @"C:\Work\one.txt",
                    @"C:\Work\two.png"
                ],
                ["Documents"] =
                [
                    @"C:\Work\forced.pdf"
                ]
            };

        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            memberships);

        Dictionary<string, List<string>> restored =
            WidgetFileStackSettings.GetStackMemberOverrides(config);
        Assert.Equal(
            memberships.Keys,
            restored.Keys);
        Assert.Equal(
            memberships["Manual:alpha"],
            restored["Manual:alpha"]);
        Assert.Equal(
            memberships["Documents"],
            restored["Documents"]);
    }

    [Fact]
    public void WidgetMetadataJson_PreservesDictionaryKeysAndCompactFormat()
    {
        var config = new WidgetConfig();
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MiXeD:Group"] = "Display name"
        };

        WidgetFileStackSettings.SetStackNameOverrides(config, names);

        string json = config.Metadata[WidgetFileStackSettings.StackNameOverridesMetadataKey];
        Assert.Equal("{\"MiXeD:Group\":\"Display name\"}", json);
        Assert.Equal(
            "Display name",
            WidgetFileStackSettings.GetStackNameOverrides(config)["MiXeD:Group"]);
    }

    [Fact]
    public void AppSettings_CustomRulesRoundTripThroughJson()
    {
        var settings = new AppSettings
        {
            FileStackGroupBy = SettingsService.FileStackGroupByCustom,
            FileStackUnmatchedBehavior = SettingsService.FileStackUnmatchedOther,
            FileStackCustomRules =
            [
                new FileStackCustomRule
                {
                    Id = "design",
                    Name = "Design",
                    Extensions = [".psd", ".fig"]
                }
            ]
        };

        string json = JsonSerializer.Serialize(settings);
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(SettingsService.FileStackGroupByCustom, restored.FileStackGroupBy);
        Assert.Equal(SettingsService.FileStackUnmatchedOther, restored.FileStackUnmatchedBehavior);
        FileStackCustomRule rule = Assert.Single(restored.FileStackCustomRules);
        Assert.Equal("design", rule.Id);
        Assert.Equal([".psd", ".fig"], rule.Extensions);
    }

    [Fact]
    public void NormalizeOverrides_PrunesOrphanedManualStackAndSplicesSurvivingMemberInManualOrder()
    {
        var config = new WidgetConfig
        {
            SortMode = WidgetSortMode.Manual
        };
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>
            {
                ["Manual:active"] = [@"C:\Work\one.txt", @"C:\Work\two.txt"],
                ["Manual:orphan"] = [@"C:\Work\survivor.txt"]
            });
        WidgetFileStackSettings.SetStackOrder(
            config,
            [
                @"Item:C:\WORK\FIRST.TXT",
                "Manual:orphan",
                "Manual:active",
                @"Item:C:\WORK\LAST.TXT"
            ]);

        Assert.True(WidgetFileStackSettings.NormalizeOverrides(config));

        Assert.Equal(
            ["Manual:active"],
            WidgetFileStackSettings.GetStackMemberOverrides(config).Keys);
        Assert.Equal(
            [
                @"Item:C:\WORK\FIRST.TXT",
                @"Item:C:\WORK\SURVIVOR.TXT",
                "Manual:active",
                @"Item:C:\WORK\LAST.TXT"
            ],
            WidgetFileStackSettings.GetStackOrder(config));
    }

    [Fact]
    public void SubfolderSortAndOrderMetadata_RoundTripsAndRebasesManagedPaths()
    {
        const string oldRoot = @"C:\OldManaged\WidgetA";
        const string newRoot = @"D:\NewManaged\WidgetA";
        string oldSubA = Path.Combine(oldRoot, "SubA");
        string oldSubB = Path.Combine(oldRoot, "SubB", "Nested");
        string newSubAKey = WidgetFileStackSettings.NormalizeFolderScopeKey(
            Path.Combine(newRoot, "SubA"));
        string newSubBKey = WidgetFileStackSettings.NormalizeFolderScopeKey(
            Path.Combine(newRoot, "SubB", "Nested"));

        var config = new WidgetConfig();
        WidgetFileStackSettings.SetSubfolderSortOverrides(
            config,
            new Dictionary<string, WidgetFolderSortOverride>
            {
                [oldSubA] = new WidgetFolderSortOverride(WidgetSortMode.Manual, false),
                [oldSubB] = new WidgetFolderSortOverride(WidgetSortMode.DateModified, true)
            });
        WidgetFileStackSettings.SetSubfolderDisplayOrders(
            config,
            new Dictionary<string, List<string>>
            {
                [oldSubA] =
                [
                    "Item:" + Path.Combine(oldSubA, "b.lnk").ToUpperInvariant(),
                    "Documents",
                    "Item:" + Path.Combine(oldSubA, "a.lnk").ToUpperInvariant()
                ]
            });
        WidgetFileStackSettings.SetSubfolderManualItemOrders(
            config,
            new Dictionary<string, List<string>>
            {
                [oldSubA] =
                [
                    Path.Combine(oldSubA, "b.lnk"),
                    Path.Combine(oldSubA, "a.lnk")
                ]
            });

        Assert.True(WidgetFileStackSettings.RebaseManagedFolderPaths(
            config,
            oldRoot,
            newRoot));

        Dictionary<string, WidgetFolderSortOverride> sortOverrides =
            WidgetFileStackSettings.GetSubfolderSortOverrides(config);
        Assert.Equal(
            new WidgetFolderSortOverride(WidgetSortMode.Manual, false),
            sortOverrides[newSubAKey]);
        Assert.Equal(
            new WidgetFolderSortOverride(WidgetSortMode.DateModified, true),
            sortOverrides[newSubBKey]);

        Dictionary<string, List<string>> displayOrders =
            WidgetFileStackSettings.GetSubfolderDisplayOrders(config);
        Assert.Equal(
            [
                "Item:" + Path.Combine(newRoot, "SubA", "b.lnk").ToUpperInvariant(),
                "Documents",
                "Item:" + Path.Combine(newRoot, "SubA", "a.lnk").ToUpperInvariant()
            ],
            displayOrders[newSubAKey]);

        Dictionary<string, List<string>> manualOrders =
            WidgetFileStackSettings.GetSubfolderManualItemOrders(config);
        Assert.Equal(
            [
                Path.Combine(newRoot, "SubA", "b.lnk"),
                Path.Combine(newRoot, "SubA", "a.lnk")
            ],
            manualOrders[newSubAKey]);
    }

    [Fact]
    public void NormalizeOverrides_PrunesOrphanedManualStackInSubfolderDisplayOrders()
    {
        const string subPath = @"C:\Work\Root\Sub";
        string subKey = WidgetFileStackSettings.NormalizeFolderScopeKey(subPath);
        var config = new WidgetConfig
        {
            SortMode = WidgetSortMode.Name
        };
        WidgetFileStackSettings.SetSubfolderSortOverrides(
            config,
            new Dictionary<string, WidgetFolderSortOverride>
            {
                [subKey] = new WidgetFolderSortOverride(WidgetSortMode.Manual, false)
            });
        WidgetFileStackSettings.SetStackMemberOverrides(
            config,
            new Dictionary<string, List<string>>
            {
                ["Manual:sub-active"] =
                [
                    Path.Combine(subPath, "one.txt"),
                    Path.Combine(subPath, "two.txt")
                ],
                ["Manual:sub-orphan"] =
                [
                    Path.Combine(subPath, "survivor.txt")
                ]
            });
        WidgetFileStackSettings.SetSubfolderDisplayOrders(
            config,
            new Dictionary<string, List<string>>
            {
                [subKey] =
                [
                    @"Item:C:\WORK\ROOT\SUB\FIRST.TXT",
                    "Manual:sub-orphan",
                    "Manual:sub-active"
                ]
            });

        Assert.True(WidgetFileStackSettings.NormalizeOverrides(config));

        Assert.Equal(
            ["Manual:sub-active"],
            WidgetFileStackSettings.GetStackMemberOverrides(config).Keys);
        Assert.Equal(
            [
                @"Item:C:\WORK\ROOT\SUB\FIRST.TXT",
                @"Item:C:\WORK\ROOT\SUB\SURVIVOR.TXT",
                "Manual:sub-active"
            ],
            WidgetFileStackSettings.GetSubfolderDisplayOrders(config)[subKey]);
    }
}
