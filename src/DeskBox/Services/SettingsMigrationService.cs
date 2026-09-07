using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Defines a single settings migration step from one schema version to the next.
/// </summary>
public interface ISettingsMigration
{
    /// <summary>The source schema version this migration upgrades from.</summary>
    int FromVersion { get; }

    /// <summary>Applies the migration to the given settings instance.</summary>
    void Migrate(AppSettings settings);
}

/// <summary>
/// Pipeline that executes registered settings migrations in version order.
/// </summary>
public sealed class SettingsMigrationPipeline
{
    /// <summary>The current schema version that the application expects.</summary>
    public const int CurrentSchemaVersion = 10;

    private readonly List<ISettingsMigration> _migrations = [];

    public SettingsMigrationPipeline()
        : this(
        [
            new Migration_0_To_1(),
            new Migration_1_To_2(),
            new Migration_2_To_3(),
            new Migration_3_To_4(),
            new Migration_4_To_5(),
            new Migration_5_To_6(),
            new Migration_6_To_7(),
            new Migration_7_To_8(),
            new Migration_8_To_9(),
            new Migration_9_To_10()
        ])
    {
    }

    /// <summary>
    /// Test seam: runs an explicit migration list against isolated state so
    /// pipeline semantics (stop-on-failure, version bookkeeping) can be
    /// verified without touching the production data root.
    /// </summary>
    internal SettingsMigrationPipeline(IEnumerable<ISettingsMigration> migrations)
    {
        _migrations.AddRange(migrations);
    }

    /// <summary>
    /// Runs all necessary migrations to bring the settings from their current
    /// schema version up to <see cref="CurrentSchemaVersion"/>.
    /// A migration that throws stops the pipeline: the schema version stays
    /// at the last successful step (never stamped past a failed step) so the
    /// failed migration is retried on the next launch. Migrations that write
    /// external stores (Migration_9_To_10 creating data/music/settings.json)
    /// depend on this - stamping the version past a failed file write would
    /// strand the user's data in the legacy fields forever.
    /// Returns true if any migration was applied.
    /// </summary>
    public bool RunMigrations(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return false;
        }

        bool anyApplied = false;
        int version = settings.SchemaVersion;

        foreach (var migration in _migrations.OrderBy(m => m.FromVersion))
        {
            if (migration.FromVersion >= version && migration.FromVersion < CurrentSchemaVersion)
            {
                try
                {
                    migration.Migrate(settings);
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[SettingsMigration] Migration from version {migration.FromVersion} FAILED " +
                        $"and will retry on next launch: {ex.Message}");
                    settings.SchemaVersion = version;
                    return anyApplied;
                }

                version = migration.FromVersion + 1;
                anyApplied = true;
                App.Log($"[SettingsMigration] Applied migration from version {migration.FromVersion} to {version}");
            }
        }

        settings.SchemaVersion = CurrentSchemaVersion;
        return anyApplied;
    }
}

/// <summary>
/// Initial migration: handles legacy settings that predate the schema versioning system.
/// Consolidates scattered migration logic (WidgetCompactSettingsVersion, legacy WidgetCollapsedStyle, etc.)
/// into a single versioned step.
/// </summary>
internal sealed class Migration_0_To_1 : ISettingsMigration
{
    public int FromVersion => 0;

    public void Migrate(AppSettings settings)
    {
        // Legacy migration: ensure WidgetCompactSettingsVersion is at least 1
        // (older settings may have version 0 which used a different compact layout)
        if (settings.WidgetCompactSettingsVersion < 1)
        {
            settings.WidgetCompactSettingsVersion = 1;
        }

        // Legacy migration: normalize any obsolete WidgetCollapsedStyle values
        // The old "Collapsed" style was replaced by "Click" behavior
        if (string.Equals(settings.WidgetCollapseBehavior, "Collapsed", StringComparison.OrdinalIgnoreCase))
        {
            settings.WidgetCollapseBehavior = SettingsService.WidgetCollapseBehaviorClick;
        }

        // Ensure FeatureWidgetEnabledStates dictionary is initialized
        settings.FeatureWidgetEnabledStates ??= [];

        // Ensure Widgets list is initialized
        settings.Widgets ??= [];

        // Ensure widget groups are initialized. Older settings have no groups.
        settings.WidgetGroups ??= [];

        // Ensure DeletedWidgetIds list is initialized
        settings.DeletedWidgetIds ??= [];

        // Ensure RecentOrganizationHistory is initialized
        settings.RecentOrganizationHistory ??= [];
    }
}

/// <summary>
/// Removes the implicit wheel-off override written by the early Tabs
/// compatibility migration. A group whose navigation follows the application
/// default must also be able to follow the application's wheel setting.
/// Explicit navigation styles and future per-group choices remain untouched.
/// </summary>
internal sealed class Migration_1_To_2 : ISettingsMigration
{
    public int FromVersion => 1;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetGroups ??= [];
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (string.Equals(
                    WidgetGroupNavigationStyles.Normalize(
                        group.NavigationStyle,
                        allowFollowDefault: true),
                    WidgetGroupNavigationStyles.FollowDefault,
                    StringComparison.Ordinal) &&
                group.WheelSwitchEnabled == false)
            {
                group.WheelSwitchEnabled = null;
            }
        }
    }
}

/// <summary>
/// Repairs groups changed from Tabs to FollowDefault after schema version 2.
/// Those groups could retain the compatibility wheel-off value even though
/// the application-level wheel setting was enabled.
/// </summary>
internal sealed class Migration_2_To_3 : ISettingsMigration
{
    public int FromVersion => 2;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetGroups ??= [];
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (string.Equals(
                    WidgetGroupNavigationStyles.Normalize(
                        group.NavigationStyle,
                        allowFollowDefault: true),
                    WidgetGroupNavigationStyles.FollowDefault,
                    StringComparison.Ordinal) &&
                group.WheelSwitchEnabled == false)
            {
                group.WheelSwitchEnabled = null;
            }
        }
    }
}

/// <summary>
/// Marks the legacy default file-widget experience as already resolved. Existing
/// profiles must never receive a new default widget merely because they currently
/// contain no file widgets. SettingsService resets this flag only when it knows
/// that the settings file did not exist and a genuinely new profile was created.
/// </summary>
internal sealed class Migration_3_To_4 : ISettingsMigration
{
    public int FromVersion => 3;

    public void Migrate(AppSettings settings)
    {
        settings.HasResolvedInitialFileWidgetSetup = true;
    }
}

/// <summary>
/// Migrates the legacy search result limit that was previously treated as an
/// application default. Future 50, 100, and 200 selections are user choices
/// and are preserved by normal settings validation.
/// </summary>
internal sealed class Migration_4_To_5 : ISettingsMigration
{
    public int FromVersion => 4;

    public void Migrate(AppSettings settings)
    {
        if (settings.SearchMaxResults == 50)
        {
            settings.SearchMaxResults = 200;
        }
    }
}

/// <summary>
/// Introduces bounded per-display-topology widget layouts. Existing geometry is
/// intentionally left in place; the first stable startup captures it as the
/// initial active profile without moving a window.
/// </summary>
internal sealed class Migration_5_To_6 : ISettingsMigration
{
    public int FromVersion => 5;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetTopologyLayouts ??= [];
    }
}

/// <summary>
/// Retires DeskBox's local filename index. Existing users must explicitly opt in
/// before DeskBox sends queries to an installed Everything process.
/// </summary>
internal sealed class Migration_6_To_7 : ISettingsMigration
{
    public int FromVersion => 6;

    public void Migrate(AppSettings settings)
    {
        settings.SearchEverythingEnabled = false;
        settings.SearchEverythingExecutablePath = string.Empty;
        settings.SearchEverythingAdvancedSyntaxEnabled = false;
    }
}

/// <summary>
/// Replaces the legacy all-or-nothing decorative-animation switch with
/// individually selectable effects, and repairs retired unbounded performance
/// values to finite choices.
/// </summary>
/// <summary>
/// Splits the old stack master switch into the new master/auto pair. Legacy
/// "enabled" meant automatic grouping, so it maps onto the new auto-stacking
/// switch. The legacy "off" state kept manual stacks visible, which in the
/// redesigned model is exactly (master on, auto off) — so every profile ends
/// up with the master switch on and only automatic grouping opt-in.
/// </summary>
internal sealed class Migration_8_To_9 : ISettingsMigration
{
    public int FromVersion => 8;

    public void Migrate(AppSettings settings)
    {
        settings.FileStackAutoStacking = settings.FileStacksEnabled;
        settings.FileStacksEnabled = true;
    }
}

internal sealed class Migration_7_To_8 : ISettingsMigration
{
    public int FromVersion => 7;

    public void Migrate(AppSettings settings)
    {
        bool legacyAnimationsEnabled =
            settings.EnableContinuousDecorativeAnimations;
        settings.EnableTextMarqueeAnimations = legacyAnimationsEnabled;
        settings.EnableVinylRotationAnimations = legacyAnimationsEnabled;
        settings.EnableCompactAmbientAnimations = legacyAnimationsEnabled;

        // Glance image rotation was independent of the retired switch. Preserve
        // the existing user-visible behavior during upgrade.
        settings.EnableGlanceImageAutoRotation = true;

        bool retiredBestVisual = string.Equals(
                settings.PerformanceMode,
                PerformanceSettingsPolicy.ModeBestVisual,
                StringComparison.OrdinalIgnoreCase);
        if (retiredBestVisual)
        {
            PerformanceSettingsPolicy.ApplyPreset(
                settings,
                PerformanceSettingsPolicy.ModeBalanced);
            return;
        }

        settings.HiddenCacheCleanupDelaySeconds =
            PerformanceSettingsPolicy.NormalizeHiddenCacheCleanupDelaySeconds(
                settings.HiddenCacheCleanupDelaySeconds);
        settings.VisibleIdleCacheCleanupDelaySeconds =
            PerformanceSettingsPolicy.NormalizeVisibleIdleCacheCleanupDelaySeconds(
                settings.VisibleIdleCacheCleanupDelaySeconds);
        settings.TransientWindowReleaseDelaySeconds =
            PerformanceSettingsPolicy.NormalizeTransientWindowReleaseDelaySeconds(
                settings.TransientWindowReleaseDelaySeconds);
    }
}

/// <summary>
/// Copies the three Music feature fields out of the global AppSettings into
/// the per-kind MusicSettingsStore (pluginization roadmap stage 2, the
/// per-kind store pilot). Copy-style: the AppSettings fields are left in
/// place as an inert compatibility source until the N+2 cleanup release
/// removes them, so a downgrade within the window keeps working. The store
/// is created only when it does not exist yet - re-running the migration
/// never overwrites user changes made after the first cutover.
/// </summary>
internal sealed class Migration_9_To_10 : ISettingsMigration
{
    public int FromVersion => 9;

    public void Migrate(AppSettings settings) =>
        Migrate(settings, DeskBoxDataPathService.Current.DataDirectory);

    internal static void Migrate(AppSettings settings, string dataDirectory)
    {
        string musicDataDirectory = Path.Combine(dataDirectory, "music");
        string storePath = Path.Combine(musicDataDirectory, "settings.json");
        if (File.Exists(storePath))
        {
            return;
        }

        var store = new MusicSettingsStore(musicDataDirectory);
        var migrated = store.Load();
        migrated.UseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        migrated.EnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        migrated.DisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.MusicDisplayMode);
        // Synchronous write that THROWS on failure: a swallowed write error
        // here would let the pipeline stamp SchemaVersion=10 while the store
        // was never created, permanently stranding the legacy values. It
        // must also never block on an async continuation (UI-thread startup
        // deadlock), hence the synchronous save path.
        store.SaveSynchronously(migrated);
    }
}

