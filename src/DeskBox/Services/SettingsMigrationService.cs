using DeskBox.Models;
using System.Text.Json;

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
    public const int CurrentSchemaVersion = 13;

    private readonly List<ISettingsMigration> _migrations = [];

    public SettingsMigrationPipeline()
    {
        // Register migrations in order
        _migrations.Add(new Migration_0_To_1());
        _migrations.Add(new Migration_1_To_2());
        _migrations.Add(new Migration_2_To_3());
        _migrations.Add(new Migration_3_To_4());
        _migrations.Add(new Migration_4_To_5());
        _migrations.Add(new Migration_5_To_6());
        _migrations.Add(new Migration_6_To_7());
        _migrations.Add(new Migration_7_To_8());
        _migrations.Add(new Migration_8_To_9());
        _migrations.Add(new Migration_9_To_10());
        _migrations.Add(new Migration_10_To_11());
        _migrations.Add(new Migration_11_To_12());
        _migrations.Add(new Migration_12_To_13());
    }

    /// <summary>
    /// Test seam for fault-injection: the chain behavior (step failures,
    /// checkpoints, ordering) is what the tests pin, not the real steps.
    /// </summary>
    internal SettingsMigrationPipeline(IEnumerable<ISettingsMigration> migrations)
    {
        _migrations.AddRange(migrations);
    }

    /// <summary>
    /// Runs all necessary migrations to bring the settings from their current
    /// schema version up to <see cref="CurrentSchemaVersion"/>. Runs
    /// copy-on-write: every step executes on a deserialized copy of the last
    /// committed state and a failed step is discarded wholesale, so the
    /// returned graph is either fully migrated through its recorded
    /// checkpoint or byte-for-byte the input. The caller replaces its
    /// settings reference with the returned one.
    /// </summary>
    public (AppSettings Settings, bool AnyApplied) RunMigrationsOnCopy(AppSettings settings)
    {
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return (settings, false);
        }

        AppSettings working = settings;
        int version = settings.SchemaVersion;
        bool anyApplied = false;

        foreach (var migration in _migrations.OrderBy(m => m.FromVersion))
        {
            if (migration.FromVersion != version)
            {
                continue;
            }

            if (migration.FromVersion >= CurrentSchemaVersion)
            {
                break;
            }

            byte[]? snapshot = TrySerializeSettings(working);
            if (snapshot is null)
            {
                App.Log(
                    $"[SettingsMigration] Migration from version {migration.FromVersion} skipped: " +
                    "the pre-step settings snapshot could not be taken.");
                break;
            }

            if (TryDeserializeSettings(snapshot) is not { } stepCopy)
            {
                App.Log(
                    $"[SettingsMigration] Migration from version {migration.FromVersion} skipped: " +
                    "the pre-step settings snapshot could not be read back.");
                break;
            }

            try
            {
                migration.Migrate(stepCopy);
            }
            catch (Exception ex)
            {
                // A failed step must stop the chain and leave the graph
                // untouched: every later migration assumes the schema the
                // failed step was supposed to produce, and the discarded copy
                // carries no half-applied mutations.
                App.Log(
                    $"[SettingsMigration] Migration from version {migration.FromVersion} failed: {ex.Message}; " +
                    $"state untouched, stopping at schema version {version} (will retry on next launch)");
                break;
            }

            working = stepCopy;
            version = migration.FromVersion + 1;
            anyApplied = true;
            App.Log($"[SettingsMigration] Applied migration from version {migration.FromVersion} to {version}");
        }

        // Record the checkpoint the chain actually reached. A partial run
        // keeps the last successful version so the failed step retries next
        // launch; only a full pass reaches CurrentSchemaVersion.
        working.SchemaVersion = version;
        return (working, anyApplied);
    }

    private static byte[]? TrySerializeSettings(AppSettings settings)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(
                settings,
                SettingsJsonContext.Default.AppSettings);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsMigration] Settings snapshot failed: {ex.Message}");
            return null;
        }
    }

    private static AppSettings? TryDeserializeSettings(byte[] snapshot)
    {
        try
        {
            return JsonSerializer.Deserialize(
                snapshot,
                SettingsJsonContext.Default.AppSettings);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsMigration] Settings snapshot read-back failed: {ex.Message}");
            return null;
        }
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

/// <summary>
/// Schema v10 adds the global widget background fields (mode, unified and
/// panorama image names, dim, unified fit). All of them are nullable with
/// "follow material" defaults, so an existing profile loads correctly
/// without data movement — this step only advances the recorded version.
/// </summary>
internal sealed class Migration_9_To_10 : ISettingsMigration
{
    public int FromVersion => 9;

    public void Migrate(AppSettings settings)
    {
    }
}

/// <summary>
/// Schema v11 adds the dual-layer text shadow switch (default off, so
/// existing profiles load correctly without data movement).
/// </summary>
internal sealed class Migration_10_To_11 : ISettingsMigration
{
    public int FromVersion => 10;

    public void Migrate(AppSettings settings)
    {
    }
}

/// <summary>
/// Schema v12 introduces the screen-home model (spec 7.1): infers a home
/// display for every surface, stamps profile entries with authority, syncs
/// capsule monitor fields to their surfaces, and derives the new-widget
/// placement target from the legacy default-screen setting. Pure data — no
/// Win32/display access.
/// </summary>
internal sealed class Migration_11_To_12 : ISettingsMigration
{
    public int FromVersion => 11;

    public void Migrate(AppSettings settings)
    {
        settings.WidgetGroups ??= [];
        settings.WidgetTopologyLayouts ??= [];

        // 1. Infer homes: standalone widgets and group surfaces first, then
        // mirror into group members.
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.ScreenBindingMode == WidgetScreenBindingMode.FollowPrimary)
            {
                continue;
            }

            if (widget.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                !IsDegenerateId(widget.BoundScreenId))
            {
                continue;
            }

            string? inferred = InferHomeFromProfiles(settings, widget.Id) ??
                NonDegenerateId(widget.PositionMonitorStableId);
            if (inferred is not null)
            {
                widget.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
                widget.BoundScreenId = inferred;
            }
            else
            {
                widget.ScreenBindingMode = WidgetScreenBindingMode.Unbound;
                widget.BoundScreenId = null;
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (group.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                !IsDegenerateId(group.BoundScreenId))
            {
                continue;
            }

            // Group profile entries are keyed by the group surface id, not
            // member ids — try both, surface id first.
            string? inferred = InferHomeFromProfiles(settings, group.SurfaceId) ??
                (group.MemberIds.FirstOrDefault() is { } representativeId
                    ? InferHomeFromProfiles(settings, representativeId)
                    : null);
            if (inferred is not null)
            {
                group.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
                group.BoundScreenId = inferred;
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            foreach (string memberId in group.MemberIds)
            {
                if (settings.Widgets.FirstOrDefault(widget =>
                        string.Equals(widget.Id, memberId, StringComparison.Ordinal)) is { } member)
                {
                    member.ScreenBindingMode = group.ScreenBindingMode;
                    member.BoundScreenId = group.BoundScreenId;
                }
            }
        }

        // FollowPrimary surfaces never keep a stale bound id.
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.ScreenBindingMode == WidgetScreenBindingMode.FollowPrimary)
            {
                widget.BoundScreenId = null;
            }
        }

        // 2. Profile entry authority + 3. capsule field sync.
        foreach (WidgetTopologyLayoutProfile profile in settings.WidgetTopologyLayouts.Values)
        {
            foreach ((string surfaceId, WidgetSurfaceLayoutProfile entry) in profile.Surfaces)
            {
                string? home = HomeOfSurface(settings, surfaceId);
                entry.IsAuthoritative = entry.IsAuthoritative == true ||
                    (home is not null &&
                     string.Equals(
                         NonDegenerateId(entry.PositionMonitorStableId),
                         home,
                         StringComparison.OrdinalIgnoreCase));
                entry.AuthoredAtUtc ??= profile.LastUsedAtUtc;

                // Binding intent is topology-independent and profile switches
                // must carry it verbatim: mirror the home inferred in step 1
                // into the entry so the first topology projection after
                // upgrade does not fall back to heuristics (an entry pin
                // outranks every heuristic in SelectTargetMonitor).
                if (home is not null)
                {
                    entry.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
                    entry.BoundScreenId = home;
                }

                if (entry.CompactPlacement is { } compact)
                {
                    compact.PositionMonitorKey = entry.PositionMonitorKey;
                    compact.PositionMonitorDeviceName = entry.PositionMonitorDeviceName;
                    compact.PositionMonitorStableId = entry.PositionMonitorStableId;
                    compact.PositionMonitorWasPrimary = entry.PositionMonitorWasPrimary;
                }
            }
        }

        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.CompactPlacement is { } compact)
            {
                compact.PositionMonitorKey = widget.PositionMonitorKey;
                compact.PositionMonitorDeviceName = widget.PositionMonitorDeviceName;
                compact.PositionMonitorStableId = widget.PositionMonitorStableId;
                compact.PositionMonitorWasPrimary = widget.PositionMonitorWasPrimary;
            }
        }

        // 4. New-widget placement target derives from the legacy default
        // screen setting; 5. disconnect behavior keeps its default.
        settings.WidgetNewPlacementTarget = string.IsNullOrWhiteSpace(
            settings.WidgetDefaultBoundScreenId)
            ? SettingsService.WidgetNewPlacementCursorDisplay
            : SettingsService.WidgetNewPlacementSpecificDisplay;
    }

    private static string? HomeOfSurface(AppSettings settings, string surfaceId)
    {
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (string.Equals(widget.Id, surfaceId, StringComparison.Ordinal) &&
                widget.ScreenBindingMode == WidgetScreenBindingMode.Pinned)
            {
                return NonDegenerateId(widget.BoundScreenId);
            }
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (group.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                string.Equals(group.SurfaceId, surfaceId, StringComparison.Ordinal))
            {
                return NonDegenerateId(group.BoundScreenId);
            }
        }

        return null;
    }

    /// <summary>
    /// The home for an Unbound surface: its entry in the profile with the
    /// most monitors (positions captured with the fullest display set are
    /// the most likely to be user-chosen), falling back to its config
    /// identity field.
    /// </summary>
    private static string? InferHomeFromProfiles(AppSettings settings, string surfaceId)
    {
        WidgetSurfaceLayoutProfile? best = null;
        int bestMonitorCount = -1;
        DateTimeOffset bestUsedAt = DateTimeOffset.MinValue;
        foreach (WidgetTopologyLayoutProfile profile in settings.WidgetTopologyLayouts.Values)
        {
            if (!profile.Surfaces.TryGetValue(surfaceId, out WidgetSurfaceLayoutProfile? entry) ||
                entry is null)
            {
                continue;
            }

            int monitorCount = profile.Monitors?.Count ?? 0;
            if (monitorCount > bestMonitorCount ||
                (monitorCount == bestMonitorCount && profile.LastUsedAtUtc > bestUsedAt))
            {
                bestMonitorCount = monitorCount;
                bestUsedAt = profile.LastUsedAtUtc;
                best = entry;
            }
        }

        return NonDegenerateId(best?.PositionMonitorStableId);
    }

    private static bool IsDegenerateId(string? stableId) =>
        string.IsNullOrWhiteSpace(stableId) ||
        stableId.Trim().Equals("unknown-display", StringComparison.OrdinalIgnoreCase) ||
        stableId.Trim().StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase);

    private static string? NonDegenerateId(string? stableId) =>
        IsDegenerateId(stableId) ? null : stableId!.Trim();
}

/// <summary>
/// Schema v13 rewrites topology profile keys to v4 (display identity set
/// only, spec 7.2): each stored profile's key is recomputed from its
/// persisted Monitors; profiles collapsing onto the same v4 key merge
/// (newest LastUsedAtUtc wins, ties broken by v3 key ordinal; missing
/// surfaces filled from sibling profiles' newest authoritative entries).
/// Profiles with empty or all-degenerate Monitors are dropped — their v4
/// key would collide (the empty set).
/// </summary>
internal sealed class Migration_12_To_13 : ISettingsMigration
{
    public int FromVersion => 12;

    public void Migrate(AppSettings settings)
    {
        if (settings.WidgetTopologyLayouts.Count == 0)
        {
            return;
        }

        var merged = new Dictionary<string, WidgetTopologyLayoutProfile>(StringComparer.Ordinal);
        foreach ((string key, WidgetTopologyLayoutProfile? profile) in
                 settings.WidgetTopologyLayouts.Where(pair => pair.Value is not null)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (profile!.Monitors is not { Count: > 0 })
            {
                continue;
            }

            string v4Key = ComputeV4Key(profile.Monitors);
            if (!merged.TryGetValue(v4Key, out WidgetTopologyLayoutProfile? target))
            {
                profile.Monitors = profile.Monitors.ToList();
                merged[v4Key] = profile;
                continue;
            }

            // Same identity set: keep the newest usage as the base, merge
            // missing surfaces from the sibling's authoritative entries.
            WidgetTopologyLayoutProfile older;
            if (profile.LastUsedAtUtc <= target.LastUsedAtUtc)
            {
                older = profile;
            }
            else
            {
                // Incoming profile is newer: it becomes the base and must
                // receive the previous base's unique surfaces — reassign
                // target, otherwise the merge loop below would compare the
                // old base against itself and silently drop those entries.
                older = target;
                merged[v4Key] = profile;
                target = profile;
            }
            foreach ((string surfaceId, WidgetSurfaceLayoutProfile? siblingEntry) in older.Surfaces)
            {
                if (!target.Surfaces.ContainsKey(surfaceId) && siblingEntry is not null)
                {
                    target.Surfaces[surfaceId] = siblingEntry;
                }
            }

            target.Monitors = target.Monitors.Count >= older.Monitors.Count
                ? target.Monitors
                : older.Monitors.ToList();
        }

        string? activeKey = settings.ActiveWidgetTopologyKey;
        string? remappedActiveKey = null;
        if (activeKey is not null &&
            settings.WidgetTopologyLayouts.TryGetValue(activeKey, out WidgetTopologyLayoutProfile? activeProfile) &&
            activeProfile is not null &&
            activeProfile.Monitors is { Count: > 0 })
        {
            remappedActiveKey = ComputeV4Key(activeProfile.Monitors);
        }

        settings.WidgetTopologyLayouts = merged;
        settings.ActiveWidgetTopologyKey =
            remappedActiveKey is not null && merged.ContainsKey(remappedActiveKey)
                ? remappedActiveKey
                : null;
    }

    private static string ComputeV4Key(IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        IEnumerable<string> tokens = monitors
            .Select(monitor => MonitorToken(monitor))
            .OrderBy(token => token, StringComparer.Ordinal);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("|", tokens)));
        return "v4-" + Convert.ToHexString(hash.AsSpan(0, 12));
    }

    private static string MonitorToken(WidgetTopologyMonitorProfile monitor)
    {
        string? stableId = string.IsNullOrWhiteSpace(monitor.StableId)
            ? null
            : monitor.StableId.Trim();
        if (stableId is null ||
            stableId.Equals("unknown-display", StringComparison.OrdinalIgnoreCase) ||
            stableId.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return $"geo:{Math.Max(1, monitor.MonitorWidth)}x{Math.Max(1, monitor.MonitorHeight)}";
        }

        return stableId.ToUpperInvariant();
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
