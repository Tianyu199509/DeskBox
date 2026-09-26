using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsMigrationPipelineTests
{
    [Fact]
    public void VersionTwo_ClearsLegacyWheelOverrideForFollowDefaultGroup()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.FollowDefault,
                    WheelSwitchEnabled = false
                },
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.Tabs,
                    WheelSwitchEnabled = false
                }
            ]
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.WidgetGroups[0].WheelSwitchEnabled);
        Assert.False(settings.WidgetGroups[1].WheelSwitchEnabled);
    }

    [Fact]
    public void VersionThree_RepairsFollowDefaultWheelOverrideCreatedAfterVersionTwo()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            WidgetGroups =
            [
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.FollowDefault,
                    WheelSwitchEnabled = false
                },
                new WidgetGroupConfig
                {
                    NavigationStyle = WidgetGroupNavigationStyles.Tabs,
                    WheelSwitchEnabled = false
                }
            ]
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.WidgetGroups[0].WheelSwitchEnabled);
        Assert.False(settings.WidgetGroups[1].WheelSwitchEnabled);
        Assert.True(settings.HasResolvedInitialFileWidgetSetup);
    }

    [Fact]
    public void VersionFour_MarksExistingProfileFileWidgetSetupAsResolved()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 3,
            HasResolvedInitialFileWidgetSetup = false,
            Widgets = []
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.HasResolvedInitialFileWidgetSetup);
    }

    [Theory]
    [InlineData(50, 200)]
    [InlineData(100, 100)]
    [InlineData(200, 200)]
    public void VersionFive_MigratesOnlyLegacySearchResultDefault(
        int storedLimit,
        int expectedLimit)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 4,
            SearchMaxResults = storedLimit
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(expectedLimit, settings.SearchMaxResults);
    }

    [Fact]
    public void VersionSix_PreservesLegacyGeometryForFirstTopologyCapture()
    {
        var widget = new WidgetConfig
        {
            X = 420,
            Y = 260,
            Width = 640,
            Height = 520
        };
        var settings = new AppSettings
        {
            SchemaVersion = 5,
            Widgets = [widget]
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.NotNull(settings.WidgetTopologyLayouts);
        Assert.Empty(settings.WidgetTopologyLayouts);
        Assert.Null(settings.ActiveWidgetTopologyKey);
        Assert.Equal(420, widget.X);
        Assert.Equal(260, widget.Y);
        Assert.Equal(640, widget.Width);
        Assert.Equal(520, widget.Height);
    }

    [Fact]
    public void VersionSeven_RequiresFreshEverythingConsent()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 6,
            SearchEverythingEnabled = true,
            SearchEverythingExecutablePath = @"C:\Portable\Everything.exe",
            SearchEverythingAdvancedSyntaxEnabled = true
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.SearchEverythingEnabled);
        Assert.Equal(string.Empty, settings.SearchEverythingExecutablePath);
        Assert.False(settings.SearchEverythingAdvancedSyntaxEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VersionEight_SplitsLegacyDecorativeEffectsWithoutChangingGlance(
        bool legacyEnabled)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            EnableContinuousDecorativeAnimations = legacyEnabled
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(legacyEnabled, settings.EnableTextMarqueeAnimations);
        Assert.Equal(legacyEnabled, settings.EnableVinylRotationAnimations);
        Assert.Equal(legacyEnabled, settings.EnableCompactAmbientAnimations);
        Assert.True(settings.EnableGlanceImageAutoRotation);
    }

    [Fact]
    public void VersionEight_RetiresBestVisualAndUnboundedCleanupValues()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeBestVisual,
            HiddenCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            VisibleIdleCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds = PerformanceSettingsPolicy.CleanupNever
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(PerformanceSettingsPolicy.ModeBalanced, settings.PerformanceMode);
        Assert.Equal(30, settings.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.TransientWindowReleaseDelaySeconds);
    }

    [Fact]
    public void VersionEight_CustomNeverValuesBecomeLongestFiniteChoices()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 7,
            PerformanceMode = PerformanceSettingsPolicy.ModeCustom,
            HiddenCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            VisibleIdleCacheCleanupDelaySeconds = PerformanceSettingsPolicy.CleanupNever,
            TransientWindowReleaseDelaySeconds = PerformanceSettingsPolicy.CleanupNever
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(PerformanceSettingsPolicy.ModeCustom, settings.PerformanceMode);
        Assert.Equal(5 * 60, settings.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(15 * 60, settings.VisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(10 * 60, settings.TransientWindowReleaseDelaySeconds);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void VersionNine_SplitsLegacyStackSwitchIntoMasterAndAutoPair(
        bool legacyStacksEnabled,
        bool expectedAutoStacking)
    {
        // The legacy all-in-one switch meant automatic grouping: "on" maps to
        // (master on, auto on), while legacy "off" kept manual stacks usable,
        // which is exactly (master on, auto off) — never a disabled master.
        var settings = new AppSettings
        {
            SchemaVersion = 8,
            FileStacksEnabled = legacyStacksEnabled
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.FileStacksEnabled);
        Assert.Equal(expectedAutoStacking, settings.FileStackAutoStacking);
    }

    [Fact]
    public void VersionNine_PreservesStackDetailsAndUnrelatedFields()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 8,
            FileStacksEnabled = false,
            FileStackGroupBy = SettingsService.FileStackGroupByCustom,
            FileStackThreshold = 5,
            FileStackOrderBy = SettingsService.FileStackOrderByDateModified,
            FileStackCustomRules =
            [
                new FileStackCustomRule
                {
                    Id = "stack-rule-media",
                    Name = "Media",
                    Extensions = [".png", ".mp4"]
                }
            ],
            Language = SettingsService.LanguageEnglish,
            SearchMaxResults = 100,
            WidgetOpacity = 0.8
        };

        var (migratedSettings, migrationsApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(settings);
        Assert.True(migrationsApplied);
        settings = migratedSettings;

        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, settings.SchemaVersion);
        // The master/auto split must not touch the stack behavior details.
        Assert.Equal(SettingsService.FileStackGroupByCustom, settings.FileStackGroupBy);
        Assert.Equal(5, settings.FileStackThreshold);
        Assert.Equal(SettingsService.FileStackOrderByDateModified, settings.FileStackOrderBy);
        FileStackCustomRule rule = Assert.Single(settings.FileStackCustomRules);
        Assert.Equal("stack-rule-media", rule.Id);
        Assert.Equal([".png", ".mp4"], rule.Extensions);
        // Unrelated fields ride along untouched.
        Assert.Equal(SettingsService.LanguageEnglish, settings.Language);
        Assert.Equal(100, settings.SearchMaxResults);
        Assert.Equal(0.8, settings.WidgetOpacity);
    }

    private sealed class TrackedMigration(int fromVersion) : ISettingsMigration
    {
        public int FromVersion => fromVersion;
        public bool Applied { get; private set; }

        public void Migrate(AppSettings settings)
        {
            Applied = true;
        }
    }

    private sealed class ThrowingMigration(int fromVersion) : ISettingsMigration
    {
        public int FromVersion => fromVersion;

        public void Migrate(AppSettings settings) =>
            throw new InvalidOperationException("injected fault");
    }

    private sealed class MutatingThrowingMigration(int fromVersion) : ISettingsMigration
    {
        public int FromVersion => fromVersion;

        public void Migrate(AppSettings settings)
        {
            // Mutate first, then fail: the pipeline must restore the
            // pre-step graph, not just stop the version progression.
            settings.WidgetOpacity = 0.99;
            settings.Widgets.Add(new WidgetConfig { Id = "half-migrated", Name = "ghost" });
            throw new InvalidOperationException("injected fault after mutation");
        }
    }

    [Fact]
    public void FailedStep_LeavesTheInputGraphUntouched()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 4,
            WidgetOpacity = 0.8
        };
        int widgetCountBefore = settings.Widgets.Count;

        // Copy-on-write: the mutating step runs on a discarded copy, so both
        // the returned graph and the input instance stay pristine.
        var (result, anyApplied) = new SettingsMigrationPipeline(
            [new MutatingThrowingMigration(4)]).RunMigrationsOnCopy(settings);

        Assert.False(anyApplied);
        Assert.Equal(4, result.SchemaVersion);
        Assert.Equal(0.8, result.WidgetOpacity);
        Assert.Equal(widgetCountBefore, result.Widgets.Count);
        Assert.Equal(4, settings.SchemaVersion);
        Assert.Equal(0.8, settings.WidgetOpacity);
        Assert.Equal(widgetCountBefore, settings.Widgets.Count);
    }

    [Fact]
    public void StepFailure_StopsTheChainAndKeepsTheLastSuccessfulCheckpoint()
    {
        var failing = new ThrowingMigration(5);
        var afterFailure = new TrackedMigration(6);
        var beforeFailure = new TrackedMigration(4);
        var settings = new AppSettings { SchemaVersion = 4 };

        var (result, anyApplied) = new SettingsMigrationPipeline(
            [beforeFailure, failing, afterFailure]).RunMigrationsOnCopy(settings);

        // The failed step stops the chain: the final schema version must
        // never claim migrations that did not run (the pre-fix pipeline
        // stamped the current version unconditionally).
        Assert.True(anyApplied);
        Assert.True(beforeFailure.Applied);
        Assert.Equal(5, result.SchemaVersion);
        Assert.False(afterFailure.Applied);
    }

    [Fact]
    public void FirstStepFailure_KeepsTheOriginalVersionAndReportsNothing()
    {
        var failing = new ThrowingMigration(4);
        var afterFailure = new TrackedMigration(5);
        var settings = new AppSettings { SchemaVersion = 4 };

        var (result, anyApplied) = new SettingsMigrationPipeline(
            [failing, afterFailure]).RunMigrationsOnCopy(settings);

        Assert.False(anyApplied);
        Assert.Equal(4, result.SchemaVersion);
        Assert.False(afterFailure.Applied);
    }

    [Fact]
    public void FullChain_ReachesTheCurrentSchemaVersion()
    {
        var steps = Enumerable.Range(0, SettingsMigrationPipeline.CurrentSchemaVersion)
            .Select(version => new TrackedMigration(version))
            .ToArray();
        var settings = new AppSettings { SchemaVersion = 0 };

        var (result, anyApplied) = new SettingsMigrationPipeline(steps).RunMigrationsOnCopy(settings);

        Assert.True(anyApplied);
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, result.SchemaVersion);
        Assert.All(steps, step => Assert.True(step.Applied));
    }

    [Fact]
    public void GapInTheChain_StopsAtTheMissingStep()
    {
        // No migration from version 5: the chain cannot advance past 5 and
        // must not stamp the current version over the gap.
        var steps = new[] { new TrackedMigration(4), new TrackedMigration(6) };
        var settings = new AppSettings { SchemaVersion = 4 };

        var (result, _) = new SettingsMigrationPipeline(steps).RunMigrationsOnCopy(settings);

        Assert.Equal(5, result.SchemaVersion);
        Assert.False(steps[1].Applied);
    }
}
