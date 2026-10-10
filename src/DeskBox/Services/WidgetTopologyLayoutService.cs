using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Projects one topology-specific widget layout into the legacy runtime config
/// fields. Keeping the projection centralized makes a topology switch atomic
/// from the windows' point of view and bounds persisted profile growth.
/// </summary>
internal sealed class WidgetTopologyLayoutService
{
    internal const int MaximumRetainedProfiles = 12;
    private const string CurrentTopologyKeyPrefix = "v4-";

    public bool ActivateCurrentTopology(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WidgetDisplayTopologySnapshot topology = CaptureCurrentTopology();
        return Activate(settings, topology);
    }

    public bool CaptureCurrentSurface(AppSettings settings, WidgetConfig member)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(member);

        WidgetDisplayTopologySnapshot topology = CaptureCurrentTopology();
        if (topology.Monitors.Count == 0 ||
            !string.Equals(settings.ActiveWidgetTopologyKey, topology.Key, StringComparison.Ordinal) ||
            !settings.WidgetTopologyLayouts.TryGetValue(topology.Key, out WidgetTopologyLayoutProfile? profile))
        {
            // A native DPI/display message can arrive before the coalesced
            // topology transaction. Never write current HWND geometry into the
            // profile that belongs to the previous topology.
            return false;
        }

        profile.Monitors = CloneMonitors(topology.Monitors);
        profile.LastUsedAtUtc = DateTimeOffset.UtcNow;
        // A capture here means the user placed the surface by hand (drag
        // persist / explicit move): the profile is no longer provisional.
        PromoteProvisionalProfile(topology.Key);
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, member.Id);
        if (group is not null)
        {
            string surfaceId = ResolveGroupSurfaceId(group);
            profile.Surfaces[surfaceId] = PreserveAuthority(
                profile.Surfaces.GetValueOrDefault(surfaceId),
                CaptureGroupLayout(group, profile.Monitors));
        }
        else
        {
            profile.Surfaces[member.Id] = PreserveAuthority(
                profile.Surfaces.GetValueOrDefault(member.Id),
                CaptureWidgetLayout(member, profile.Monitors));
        }

        return true;
    }

    /// <summary>
    /// Captures overwrite geometry but never promote authority (spec 4.2):
    /// an existing authoritative flag survives the rewrite; fresh captures
    /// stay non-authoritative until the user places the surface by hand.
    /// </summary>
    private static WidgetSurfaceLayoutProfile PreserveAuthority(
        WidgetSurfaceLayoutProfile? existing,
        WidgetSurfaceLayoutProfile captured)
    {
        if (existing?.IsAuthoritative == true)
        {
            captured.IsAuthoritative = true;
            captured.AuthoredAtUtc = existing.AuthoredAtUtc;
        }

        return captured;
    }

    public bool RemoveSurface(AppSettings settings, string surfaceId)
    {
        bool changed = false;
        foreach (WidgetTopologyLayoutProfile profile in settings.WidgetTopologyLayouts.Values)
        {
            changed |= profile.Surfaces.Remove(surfaceId);
        }

        return changed;
    }

    /// <summary>
    /// Group surface id for callers outside this service (screen-home
    /// commits address group surfaces, not individual members).
    /// </summary>
    internal string ResolveSurfaceIdForGroup(WidgetGroupConfig group) => ResolveGroupSurfaceId(group);

    internal bool Activate(AppSettings settings, WidgetDisplayTopologySnapshot topology)
    {
        settings.WidgetTopologyLayouts ??= [];
        if (topology.Monitors.Count == 0 || string.IsNullOrWhiteSpace(topology.Key))
        {
            return false;
        }

        bool changed = false;
        string? previousKey = settings.ActiveWidgetTopologyKey;
        bool initialCapture = string.IsNullOrWhiteSpace(previousKey);
        bool targetMonitorProjectionChanged = false;
        bool targetWasSeededFromCompatibleProfile = false;
        WidgetTopologyLayoutProfile? sourceProfile = null;
        if (!string.IsNullOrWhiteSpace(previousKey) &&
            settings.WidgetTopologyLayouts.TryGetValue(previousKey, out sourceProfile))
        {
            // Runtime configs are the live projection. Refresh the outgoing
            // profile before replacing them so a final drag/resize cannot be
            // lost even if its debounced file write has not happened yet.
            CaptureAllSurfaces(settings, sourceProfile);
        }

        if (!settings.WidgetTopologyLayouts.TryGetValue(topology.Key, out WidgetTopologyLayoutProfile? targetProfile))
        {
            WidgetTopologyLayoutProfile? compatibleProfile = FindCompatibleProfile(
                settings,
                topology,
                out string? compatibleKey);
            targetProfile = new WidgetTopologyLayoutProfile
            {
                Monitors = CloneMonitors(topology.Monitors),
                LastUsedAtUtc = DateTimeOffset.UtcNow
            };

            if (compatibleProfile is not null)
            {
                // Legacy v1/v2 profiles used earlier key algorithms, including
                // keys influenced by the transient \\.\DISPLAYn alias. Lazily
                // project the newest semantically equivalent profile into the
                // stable v3 key instead of making the user arrange it again.
                SeedProfile(settings, compatibleProfile, targetProfile);
                targetWasSeededFromCompatibleProfile = true;
                App.Log(
                    $"[DisplayTopology] Migrated compatible layout profile " +
                    $"{compatibleKey} -> {topology.Key}");
            }
            else if (initialCapture && sourceProfile is null)
            {
                CaptureAllSurfaces(settings, targetProfile, initialAuthoritative: true);
            }
            else
            {
                SeedProfile(settings, sourceProfile, targetProfile);
                // First-seen combination: provisional until it survives 10 s
                // or receives a user placement commit (spec 5.6) — a
                // transient topology cannot evict real arrangements via LRU.
                TrackProvisionalProfile(topology.Key);
            }
            settings.WidgetTopologyLayouts[topology.Key] = targetProfile;
            changed = true;
        }
        else
        {
            List<WidgetTopologyMonitorProfile> savedMonitors = CloneMonitors(targetProfile.Monitors);
            targetMonitorProjectionChanged = !HaveSameProjectionMetadata(
                savedMonitors,
                topology.Monitors);
            if (targetMonitorProjectionChanged)
            {
                // The stable topology can remain the same while Windows
                // reassigns \\.\DISPLAYn aliases or changes the taskbar work
                // area. Rebind saved surfaces to the current monitor metadata
                // before projecting them into the runtime WidgetConfig fields.
                ReprojectSurfaces(targetProfile, savedMonitors, topology.Monitors);
                changed = true;
            }

            targetProfile.Version = WidgetTopologyLayoutProfile.CurrentVersion;
            targetProfile.Monitors = CloneMonitors(topology.Monitors);
            targetProfile.LastUsedAtUtc = DateTimeOffset.UtcNow;
            changed |= EnsureMissingSurfaces(settings, sourceProfile, targetProfile);
        }

        bool topologyKeyChanged = !string.Equals(previousKey, topology.Key, StringComparison.Ordinal);
        if (topologyKeyChanged || targetMonitorProjectionChanged)
        {
            if (!initialCapture ||
                sourceProfile is not null ||
                targetWasSeededFromCompatibleProfile)
            {
                ApplyProfile(settings, targetProfile);
            }

            if (topologyKeyChanged)
            {
                settings.ActiveWidgetTopologyKey = topology.Key;
                changed = true;
            }
        }
        else
        {
            // The first run after migration already has the correct active
            // geometry. Capture it without needlessly moving any HWND.
            CaptureAllSurfaces(settings, targetProfile);
        }

        changed |= RemoveStaleSurfaces(settings);
        changed |= PruneProfiles(settings, topology.Key, _provisionalProfiles);
        return changed;
    }

    internal static WidgetDisplayTopologySnapshot CreateSnapshotForTest(
        params WidgetTopologyMonitorProfile[] monitors)
    {
        List<WidgetTopologyMonitorProfile> normalized = CloneMonitors(monitors);
        return new WidgetDisplayTopologySnapshot(CreateTopologyKey(normalized), normalized);
    }

    private static WidgetDisplayTopologySnapshot CaptureCurrentTopology()
    {
        var monitors = Win32Helper.GetMonitorWorkAreaInfos()
            .Select(area => new WidgetTopologyMonitorProfile
            {
                StableId = ResolveStableMonitorId(area.DeviceName),
                DeviceName = area.DeviceName ?? string.Empty,
                IsPrimary = area.IsPrimary,
                MonitorX = area.Monitor.Left,
                MonitorY = area.Monitor.Top,
                MonitorWidth = Math.Max(1, area.Monitor.Right - area.Monitor.Left),
                MonitorHeight = Math.Max(1, area.Monitor.Bottom - area.Monitor.Top),
                WorkAreaX = area.WorkArea.Left,
                WorkAreaY = area.WorkArea.Top,
                WorkAreaWidth = Math.Max(1, area.WorkArea.Right - area.WorkArea.Left),
                WorkAreaHeight = Math.Max(1, area.WorkArea.Bottom - area.WorkArea.Top),
                DpiScale = NormalizeScale(area.DpiScale)
            })
            .ToList();

        return new WidgetDisplayTopologySnapshot(CreateTopologyKey(monitors), monitors);
    }

    private static string CreateTopologyKey(IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        // v4: the stable-id SET is the topology identity. Position, primary
        // flag, DPI, and geometry are metadata that can change under one key
        // without spawning a new profile (spec 5.6 / D5 — resolution flips
        // must not revive old arrangements).
        IEnumerable<string> tokens = monitors
            .Select(monitor => DisplayIdentityTokens.TokenFor(
                monitor.StableId,
                monitor.MonitorWidth,
                monitor.MonitorHeight))
            .OrderBy(token => token, StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", tokens)));
        return CurrentTopologyKeyPrefix + Convert.ToHexString(hash.AsSpan(0, 12));
    }

    private static string CreateTopologySignature(
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        string signature = string.Join(
            "|",
            monitors
                .OrderBy(
                    monitor => NormalizeStableIdentityForKey(monitor.StableId),
                    StringComparer.Ordinal)
                .ThenBy(monitor => monitor.MonitorX)
                .ThenBy(monitor => monitor.MonitorY)
                .ThenBy(monitor => monitor.MonitorWidth)
                .ThenBy(monitor => monitor.MonitorHeight)
                .Select(FormatTopologySignatureSegment));
        return signature;
    }

    /// <summary>
    /// One monitor's compatibility-signature segment. Degenerate
    /// (geometry-only) monitors contribute identity + primary + DPI but NOT
    /// X/Y/W/H: a same-spec degenerate display changing resolution must still
    /// match its stored profile so the arrangement is reused after the flip
    /// (the v4 goal) — the geo topology KEY keeps the resolution, and that
    /// key/content split is the price of the persisted format. Accepted
    /// trade: equal-count degenerate sets are mutually compatible regardless
    /// of side-by-side vs stacked arrangement; the count match plus anchor
    /// re-realization at apply time bounds the damage. Non-degenerate
    /// monitors keep the full segment, so real stable ids never lose
    /// precision.
    /// </summary>
    private static string FormatTopologySignatureSegment(WidgetTopologyMonitorProfile monitor)
    {
        string identity = NormalizeStableIdentityForKey(monitor.StableId);
        if (identity.Equals("geometry-only", StringComparison.Ordinal))
        {
            return FormattableString.Invariant(
                $"{identity};{monitor.IsPrimary};{NormalizeScale(monitor.DpiScale):F3}");
        }

        return FormattableString.Invariant(
            $"{identity};{monitor.IsPrimary};{NormalizeScale(monitor.DpiScale):F3};{monitor.MonitorX},{monitor.MonitorY},{monitor.MonitorWidth},{monitor.MonitorHeight}");
    }

    private static WidgetTopologyLayoutProfile? FindCompatibleProfile(
        AppSettings settings,
        WidgetDisplayTopologySnapshot topology,
        out string? compatibleKey)
    {
        string targetSignature = CreateTopologySignature(topology.Monitors);
        foreach ((string key, WidgetTopologyLayoutProfile profile) in
                 settings.WidgetTopologyLayouts
                     .Where(pair =>
                         pair.Value is not null &&
                         pair.Value.Monitors is { Count: > 0 } &&
                         string.Equals(
                             CreateTopologySignature(pair.Value.Monitors),
                             targetSignature,
                             StringComparison.Ordinal))
                     .OrderByDescending(pair => pair.Value.LastUsedAtUtc))
        {
            compatibleKey = key;
            return profile;
        }

        compatibleKey = null;
        return null;
    }

    private static string NormalizeStableIdentityForKey(string? stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId))
        {
            return "geometry-only";
        }

        string normalized = stableId.Trim();
        if (normalized.Equals("unknown-display", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return "geometry-only";
        }

        return normalized.ToUpperInvariant();
    }

    private static bool HaveSameProjectionMetadata(
        IReadOnlyList<WidgetTopologyMonitorProfile> left,
        IReadOnlyList<WidgetTopologyMonitorProfile> right) =>
        string.Equals(
            CreateProjectionMetadataSignature(left),
            CreateProjectionMetadataSignature(right),
            StringComparison.Ordinal);

    private static string CreateProjectionMetadataSignature(
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors) =>
        string.Join(
            "|",
            monitors
                .OrderBy(
                    monitor => NormalizeStableIdentityForKey(monitor.StableId),
                    StringComparer.Ordinal)
                .ThenBy(monitor => monitor.MonitorX)
                .ThenBy(monitor => monitor.MonitorY)
                .Select(monitor =>
                    FormattableString.Invariant(
                        $"{NormalizeStableIdentityForKey(monitor.StableId)};{(monitor.DeviceName ?? string.Empty).Trim().ToUpperInvariant()};{monitor.IsPrimary};{NormalizeScale(monitor.DpiScale):F3};{monitor.MonitorX},{monitor.MonitorY},{monitor.MonitorWidth},{monitor.MonitorHeight};{monitor.WorkAreaX},{monitor.WorkAreaY},{monitor.WorkAreaWidth},{monitor.WorkAreaHeight}")));

    private static void ReprojectSurfaces(
        WidgetTopologyLayoutProfile profile,
        IReadOnlyList<WidgetTopologyMonitorProfile> sourceMonitors,
        IReadOnlyList<WidgetTopologyMonitorProfile> targetMonitors)
    {
        // Same v4 key, only metadata moved (resolution/DPI/primary/alias):
        // refresh the identity HINTS on each entry but never rewrite the
        // stored sizes/margins — the placement intent survives intact and is
        // only clamped when realized (spec 5.6, fixing defect B5).
        Dictionary<string, WidgetTopologyMonitorProfile> byStableId = [];
        foreach (var tokenGroup in targetMonitors.GroupBy(
                     monitor => DisplayIdentityTokens.TokenFor(monitor),
                     StringComparer.OrdinalIgnoreCase))
        {
            WidgetTopologyMonitorProfile[] groupMonitors = [.. tokenGroup];
            if (groupMonitors.Length > 1)
            {
                // Two same-spec degenerate monitors collapse onto one geo
                // token: which one an entry belongs to is undecidable, so
                // leave those entries' stale hints in place (same severity
                // as a token with no live match) instead of throwing on the
                // duplicate key.
                App.Log(
                    $"[DisplayTopology] Monitor token {tokenGroup.Key} is ambiguous " +
                    $"across {groupMonitors.Length} monitors; skipping hint refresh");
                continue;
            }

            byStableId[tokenGroup.Key] = groupMonitors[0];
        }
        foreach ((string surfaceId, WidgetSurfaceLayoutProfile layout) in profile.Surfaces.ToList())
        {
            bool hasGeometry = ParseGeometryFromKey(
                layout.PositionMonitorKey,
                out int _,
                out int _);
            string token = DisplayIdentityTokens.TokenFor(
                layout.PositionMonitorStableId,
                hasGeometry ? SafeWidth(layout.PositionMonitorKey) : 0,
                hasGeometry ? SafeHeight(layout.PositionMonitorKey) : 0);
            if (byStableId.TryGetValue(token, out WidgetTopologyMonitorProfile? monitor))
            {
                layout.PositionMonitorDeviceName = monitor.DeviceName;
                layout.PositionMonitorKey = CreateWorkAreaKey(monitor);
                layout.PositionMonitorWasPrimary = monitor.IsPrimary;
                if (!DisplayPlacementResolver.IsDegenerateIdentity(monitor.StableId))
                {
                    layout.PositionMonitorStableId = monitor.StableId;
                }

                if (layout.CompactPlacement is { } compact)
                {
                    compact.PositionMonitorDeviceName = monitor.DeviceName;
                    compact.PositionMonitorKey = CreateWorkAreaKey(monitor);
                    compact.PositionMonitorWasPrimary = monitor.IsPrimary;
                    if (!DisplayPlacementResolver.IsDegenerateIdentity(monitor.StableId))
                    {
                        compact.PositionMonitorStableId = monitor.StableId;
                    }
                }
            }
        }
    }

    private static int SafeWidth(string? key) =>
        ParseGeometryFromKey(key, out int width, out _) ? width : 0;

    private static int SafeHeight(string? key) =>
        ParseGeometryFromKey(key, out _, out int height) ? height : 0;

    private static bool ParseGeometryFromKey(string? key, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split(':');
        if (parts.Length == 4 &&
            int.TryParse(parts[2], out width) &&
            int.TryParse(parts[3], out height) &&
            width > 0 && height > 0)
        {
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    /// <summary>
    /// Seeds a fresh profile surface-by-surface (spec 5.3): every surface
    /// independently picks its newest authoritative entry across ALL stored
    /// profiles (home-aware), instead of inheriting one whole profile — the
    /// old whole-profile seeding propagated fallback-state placements into
    /// brand-new display combinations (defect B3).
    /// </summary>
    private static void SeedProfile(
        AppSettings settings,
        WidgetTopologyLayoutProfile? sourceProfile,
        WidgetTopologyLayoutProfile targetProfile)
    {
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateSurfaces(settings))
        {
            targetProfile.Surfaces[surfaceId] = SeedSurfaceEntry(
                settings,
                surfaceId,
                config,
                group,
                targetProfile);
        }
    }

    private static bool EnsureMissingSurfaces(
        AppSettings settings,
        WidgetTopologyLayoutProfile? sourceProfile,
        WidgetTopologyLayoutProfile targetProfile)
    {
        bool changed = false;
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateSurfaces(settings))
        {
            if (targetProfile.Surfaces.ContainsKey(surfaceId))
            {
                continue;
            }

            targetProfile.Surfaces[surfaceId] = SeedSurfaceEntry(
                settings,
                surfaceId,
                config,
                group,
                targetProfile);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Resolves the entry a surface should use when <paramref name="targetProfile"/>
    /// activates: its own entry when it still matches the surface's intent,
    /// otherwise a freshly seeded one (spec 5.3).
    /// </summary>
    private static WidgetSurfaceLayoutProfile ResolveSurfaceEntryForActivation(
        AppSettings settings,
        string surfaceId,
        WidgetConfig config,
        WidgetGroupConfig? group,
        WidgetTopologyLayoutProfile targetProfile)
    {
        if (!targetProfile.Surfaces.TryGetValue(surfaceId, out WidgetSurfaceLayoutProfile? existing) ||
            existing is null)
        {
            return SeedSurfaceEntry(settings, surfaceId, config, group, targetProfile);
        }

        var intent = SurfaceIntent(config, group);
        bool needsReseed = intent.Mode == WidgetScreenBindingMode.FollowPrimary
            ? EntryDisplay(existing, targetProfile)?.IsPrimary != true
            : !string.IsNullOrWhiteSpace(intent.HomeId) &&
              !string.Equals(
                  EntryStableId(existing),
                  intent.HomeId?.Trim(),
                  StringComparison.OrdinalIgnoreCase);

        return needsReseed
            ? SeedSurfaceEntry(settings, surfaceId, config, group, targetProfile)
            : existing;
    }

    private static WidgetSurfaceLayoutProfile SeedSurfaceEntry(
        AppSettings settings,
        string surfaceId,
        WidgetConfig config,
        WidgetGroupConfig? group,
        WidgetTopologyLayoutProfile targetProfile)
    {
        var intent = SurfaceIntent(config, group);

        // Newest-authoritative-first history across every stored profile.
        // Ties on the authored timestamp (batch captures share one) break to
        // the fuller display set — an arrangement captured with more monitors
        // attached is the more deliberate placement.
        List<(WidgetSurfaceLayoutProfile Entry, WidgetTopologyLayoutProfile Profile)> authored = settings
            .WidgetTopologyLayouts.Values
            .Where(profile => profile.Surfaces.TryGetValue(surfaceId, out _))
            .SelectMany(profile => profile.Surfaces
                .Where(pair => string.Equals(pair.Key, surfaceId, StringComparison.Ordinal))
                .Select(pair => (Entry: pair.Value, Profile: profile)))
            .Where(candidate => candidate.Entry.IsAuthoritative == true)
            .OrderByDescending(candidate => candidate.Entry.AuthoredAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.Profile.Monitors.Count)
            .ToList();

        WidgetSurfaceLayoutProfile FallbackCurrent()
        {
            return group is null
                ? CaptureWidgetLayout(config, targetProfile.Monitors)
                : CaptureGroupLayout(group, targetProfile.Monitors);
        }

        WidgetSurfaceLayoutProfile src;
        WidgetTopologyLayoutProfile srcProfile;
        WidgetScreenInfo? targetDisplay = null;
        if (intent.Mode == WidgetScreenBindingMode.FollowPrimary)
        {
            (src, srcProfile) = authored.Count > 0 ? (authored[0].Entry, authored[0].Profile) : (FallbackCurrent(), targetProfile);
            targetDisplay = PrimaryOf(targetProfile);
        }
        else if (!string.IsNullOrWhiteSpace(intent.HomeId))
        {
            var onHome = authored.FirstOrDefault(candidate =>
                string.Equals(
                    EntryStableId(candidate.Entry),
                    intent.HomeId!.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (onHome.Entry is not null)
            {
                (src, srcProfile) = (onHome.Entry, onHome.Profile);
            }
            else if (authored.Count > 0)
            {
                (src, srcProfile) = (authored[0].Entry, authored[0].Profile);
            }
            else
            {
                (src, srcProfile) = (FallbackCurrent(), targetProfile);
            }

            targetDisplay = FindMonitor(targetProfile, intent.HomeId) ?? ResolveHeuristicTarget(src, srcProfile, targetProfile);
        }
        else
        {
            // Newest authoritative entry whose display is online wins; among
            // equally fresh ones the fuller display set wins (ordering above).
            var onlineNow = authored.FirstOrDefault(candidate =>
                FindMonitor(targetProfile, EntryStableId(candidate.Entry)) is not null);
            if (onlineNow.Entry is not null)
            {
                (src, srcProfile) = (onlineNow.Entry, onlineNow.Profile);
                targetDisplay = FindMonitor(targetProfile, EntryStableId(onlineNow.Entry));
            }
            else if (authored.Count > 0)
            {
                (src, srcProfile) = (authored[0].Entry, authored[0].Profile);
                targetDisplay = ResolveHeuristicTarget(src, srcProfile, targetProfile);
            }
            else
            {
                (src, srcProfile) = (FallbackCurrent(), targetProfile);
                targetDisplay = ResolveHeuristicTarget(src, srcProfile, targetProfile);
            }
        }

        WidgetScreenInfo? srcDisplay = FindMonitor(srcProfile, EntryStableId(src));
        bool sameDisplay = targetDisplay is not null &&
            srcDisplay is not null &&
            string.Equals(
                targetDisplay.StableId.Trim(),
                srcDisplay.StableId.Trim(),
                StringComparison.OrdinalIgnoreCase);

        WidgetSurfaceLayoutProfile seeded;
        var srcMonitorInfo = MonitorProfileFor(srcProfile, srcDisplay);
        var targetMonitorInfo = MonitorProfileFor(targetProfile, targetDisplay);
        if (srcProfile.Monitors.Count == 0 || targetDisplay is null || srcMonitorInfo is null || targetMonitorInfo is null)
        {
            seeded = CloneLayout(src);
            seeded.IsAuthoritative = false;
            seeded.AuthoredAtUtc = null;
        }
        else
        {
            // Always run the mapper: even on the same display a DPI/geometry
            // change must recompute the physical X/Y cache from the anchors
            // (a ratio-1 clone would keep the old-scale pixels).
            seeded = MapToTopology(
                src,
                [.. srcProfile.Monitors],
                [.. targetProfile.Monitors]);
            // Same-display projections keep the user's authored placement;
            // cross-display mappings are heuristic and never authoritative.
            seeded.IsAuthoritative = sameDisplay && src.IsAuthoritative == true;
            seeded.AuthoredAtUtc = seeded.IsAuthoritative == true ? src.AuthoredAtUtc : null;
        }

        return seeded;
    }

    private static DisplayPlacementIntent SurfaceIntent(WidgetConfig config, WidgetGroupConfig? group) =>
        group is not null
            ? new DisplayPlacementIntent(group.ScreenBindingMode, group.BoundScreenId)
            : new DisplayPlacementIntent(config.ScreenBindingMode, config.BoundScreenId);

    private static string? EntryStableId(WidgetSurfaceLayoutProfile entry) =>
        DisplayPlacementResolver.IsDegenerateIdentity(entry.PositionMonitorStableId)
            ? null
            : entry.PositionMonitorStableId?.Trim();

    private static WidgetScreenInfo? EntryDisplay(
        WidgetSurfaceLayoutProfile entry,
        WidgetTopologyLayoutProfile profile)
    {
        if (profile.Monitors.Count == 0)
        {
            return null;
        }

        return DisplayPlacementResolver.ResolveEntryDisplay(
            new DisplayPlacementEntryReference(
                entry.PositionMonitorStableId,
                entry.PositionMonitorDeviceName,
                entry.PositionMonitorKey,
                entry.PositionMonitorWasPrimary),
            [.. profile.Monitors.Select((monitor, index) => ToScreenInfo(monitor, index + 1))]);
    }

    private static WidgetScreenInfo? ResolveHeuristicTarget(
        WidgetSurfaceLayoutProfile src,
        WidgetTopologyLayoutProfile srcProfile,
        WidgetTopologyLayoutProfile targetProfile)
    {
        if (targetProfile.Monitors.Count == 0)
        {
            return null;
        }

        ResolvedDisplayPlacement decision = DisplayPlacementResolver.Resolve(
            new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
            new DisplayPlacementEntryReference(
                src.PositionMonitorStableId,
                src.PositionMonitorDeviceName,
                src.PositionMonitorKey,
                src.PositionMonitorWasPrimary),
            [.. targetProfile.Monitors.Select((monitor, index) => ToScreenInfo(monitor, index + 1))]);
        return decision.Display;
    }

    private static WidgetScreenInfo? PrimaryOf(WidgetTopologyLayoutProfile profile)
    {
        var primary = profile.Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ??
            profile.Monitors.FirstOrDefault();
        return primary is null ? null : ToScreenInfo(primary, 1);
    }

    private static WidgetScreenInfo? FindMonitor(WidgetTopologyLayoutProfile profile, string? stableId)
    {
        if (DisplayPlacementResolver.IsDegenerateIdentity(stableId))
        {
            return null;
        }

        WidgetTopologyMonitorProfile? match = profile.Monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.StableId.Trim(), stableId!.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null ? null : ToScreenInfo(match, 1);
    }

    private static WidgetTopologyMonitorProfile? MonitorProfileFor(
        WidgetTopologyLayoutProfile profile,
        WidgetScreenInfo? screen) =>
        screen is null
            ? null
            : profile.Monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.StableId.Trim(), screen.StableId.Trim(), StringComparison.OrdinalIgnoreCase));

    private static WidgetScreenInfo ToScreenInfo(WidgetTopologyMonitorProfile monitor, int number) =>
        new(
            number,
            monitor.StableId,
            monitor.DeviceName,
            new Windows.Graphics.RectInt32(
                monitor.MonitorX,
                monitor.MonitorY,
                monitor.MonitorWidth,
                monitor.MonitorHeight),
            new Windows.Graphics.RectInt32(
                monitor.WorkAreaX,
                monitor.WorkAreaY,
                monitor.WorkAreaWidth,
                monitor.WorkAreaHeight),
            monitor.IsPrimary,
            NormalizeScale(monitor.DpiScale));

    private static void CaptureAllSurfaces(
        AppSettings settings,
        WidgetTopologyLayoutProfile profile,
        bool initialAuthoritative = false)
    {
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateSurfaces(settings))
        {
            WidgetSurfaceLayoutProfile captured = group is null
                ? CaptureWidgetLayout(config, profile.Monitors)
                : CaptureGroupLayout(group, profile.Monitors);
            if (initialAuthoritative)
            {
                // First-run capture records the user's existing arrangement.
                captured.IsAuthoritative = true;
                captured.AuthoredAtUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                captured = PreserveAuthority(profile.Surfaces.GetValueOrDefault(surfaceId), captured);
            }

            profile.Surfaces[surfaceId] = captured;
        }

        profile.LastUsedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void ApplyProfile(AppSettings settings, WidgetTopologyLayoutProfile profile)
    {
        // Activation re-resolves each surface's entry against its intent
        // (spec 5.3): an entry whose screen no longer matches the surface's
        // home / follow-primary intent is re-seeded instead of applied.
        WidgetSurfaceLayoutProfile ResolveEntry(
            string surfaceId,
            WidgetConfig config,
            WidgetGroupConfig? group)
        {
            WidgetSurfaceLayoutProfile entry = ResolveSurfaceEntryForActivation(
                settings,
                surfaceId,
                config,
                group,
                profile);
            RealizeEntryPhysicalCache(entry, profile.Monitors);
            profile.Surfaces[surfaceId] = entry;
            return entry;
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            WidgetConfig? representative = settings.Widgets.FirstOrDefault(widget =>
                group.MemberIds.Contains(widget.Id, StringComparer.Ordinal));
            WidgetSurfaceLayoutProfile layout = ResolveEntry(
                ResolveGroupSurfaceId(group),
                representative ?? new WidgetConfig { Id = group.MemberIds.FirstOrDefault() ?? group.SurfaceId },
                group);

            ApplyToGroup(group, layout);
            foreach (string memberId in group.MemberIds)
            {
                WidgetConfig? member = settings.Widgets.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, memberId, StringComparison.Ordinal));
                if (member is not null)
                {
                    ApplyToWidget(member, layout);
                }
            }
        }

        HashSet<string> groupedMemberIds = settings.WidgetGroups
            .SelectMany(group => group.MemberIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (!groupedMemberIds.Contains(widget.Id))
            {
                ApplyToWidget(widget, ResolveEntry(widget.Id, widget, null));
            }
        }
    }

    internal static WidgetSurfaceLayoutProfile MapToTopology(
        WidgetSurfaceLayoutProfile source,
        IReadOnlyList<WidgetTopologyMonitorProfile> sourceMonitors,
        IReadOnlyList<WidgetTopologyMonitorProfile> targetMonitors)
    {
        WidgetTopologyMonitorProfile? sourceMonitor = SelectSourceMonitor(source, sourceMonitors);
        WidgetTopologyMonitorProfile? targetMonitor = SelectTargetMonitor(source, sourceMonitor, targetMonitors);
        if (targetMonitor is null)
        {
            return CloneLayout(source);
        }

        bool sameMonitor = sourceMonitor is not null && MonitorIdentityEquals(sourceMonitor, targetMonitor);
        double ratioX = sameMonitor || sourceMonitor is null
            ? 1
            : EffectiveExtent(targetMonitor.WorkAreaWidth, targetMonitor.DpiScale) /
              EffectiveExtent(sourceMonitor.WorkAreaWidth, sourceMonitor.DpiScale);
        double ratioY = sameMonitor || sourceMonitor is null
            ? 1
            : EffectiveExtent(targetMonitor.WorkAreaHeight, targetMonitor.DpiScale) /
              EffectiveExtent(sourceMonitor.WorkAreaHeight, sourceMonitor.DpiScale);

        double targetEffectiveWidth = EffectiveExtent(targetMonitor.WorkAreaWidth, targetMonitor.DpiScale);
        double targetEffectiveHeight = EffectiveExtent(targetMonitor.WorkAreaHeight, targetMonitor.DpiScale);
        var mapped = CloneLayout(source);
        mapped.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        mapped.Width = ClampLogicalSize(
            source.Width * ratioX,
            SettingsService.MinWidgetWidth,
            targetEffectiveWidth);
        mapped.Height = ClampLogicalSize(
            source.Height * ratioY,
            SettingsService.MinWidgetHeight,
            targetEffectiveHeight);
        mapped.PositionMarginX = Math.Max(0, source.PositionMarginX * ratioX);
        mapped.PositionMarginY = Math.Max(0, source.PositionMarginY * ratioY);
        mapped.PositionMonitorStableId = targetMonitor.StableId;
        mapped.PositionMonitorDeviceName = targetMonitor.DeviceName;
        mapped.PositionMonitorWasPrimary = targetMonitor.IsPrimary;
        mapped.PositionMonitorKey = CreateWorkAreaKey(targetMonitor);
        mapped.CompactWidth = source.CompactWidth is { } compactWidth
            ? Math.Max(WidgetCompactBoundsCalculator.MinWidth, compactWidth * ratioX)
            : null;
        if (mapped.CompactPlacement is { } compact)
        {
            compact.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
            compact.PositionMarginX = Math.Max(0, compact.PositionMarginX * ratioX);
            compact.PositionMarginY = Math.Max(0, compact.PositionMarginY * ratioY);
            // The capsule is derived from its surface (spec 4.4): migration
            // writes the surface's target-monitor identity into the capsule
            // so a stale capsule reference can never resolve to the old
            // screen (defect B4).
            compact.PositionMonitorDeviceName = targetMonitor.DeviceName;
            compact.PositionMonitorStableId = mapped.PositionMonitorStableId;
            compact.PositionMonitorWasPrimary = targetMonitor.IsPrimary;
            compact.PositionMonitorKey = CreateWorkAreaKey(targetMonitor);
        }

        ResolvePhysicalPosition(mapped, source, sourceMonitor, targetMonitor);
        return mapped;
    }

    private static void ResolvePhysicalPosition(
        WidgetSurfaceLayoutProfile mapped,
        WidgetSurfaceLayoutProfile source,
        WidgetTopologyMonitorProfile? sourceMonitor,
        WidgetTopologyMonitorProfile targetMonitor)
    {
        double targetScale = NormalizeScale(targetMonitor.DpiScale);
        int width = Math.Max(1, (int)Math.Round(mapped.Width * targetScale));
        int height = Math.Max(1, (int)Math.Round(mapped.Height * targetScale));
        bool anchorRight = mapped.PositionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = mapped.PositionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        bool validAnchor = mapped.PositionAnchor is
            WidgetPositionAnchors.LeftTop or WidgetPositionAnchors.RightTop or
            WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;

        int x;
        int y;
        if (validAnchor)
        {
            int marginX = Math.Max(0, (int)Math.Round(mapped.PositionMarginX * targetScale));
            int marginY = Math.Max(0, (int)Math.Round(mapped.PositionMarginY * targetScale));
            x = anchorRight
                ? targetMonitor.WorkAreaX + targetMonitor.WorkAreaWidth - width - marginX
                : targetMonitor.WorkAreaX + marginX;
            y = anchorBottom
                ? targetMonitor.WorkAreaY + targetMonitor.WorkAreaHeight - height - marginY
                : targetMonitor.WorkAreaY + marginY;
        }
        else if (sourceMonitor is not null)
        {
            double relativeX = (source.X - sourceMonitor.WorkAreaX) /
                Math.Max(1, sourceMonitor.WorkAreaWidth);
            double relativeY = (source.Y - sourceMonitor.WorkAreaY) /
                Math.Max(1, sourceMonitor.WorkAreaHeight);
            x = targetMonitor.WorkAreaX + (int)Math.Round(relativeX * targetMonitor.WorkAreaWidth);
            y = targetMonitor.WorkAreaY + (int)Math.Round(relativeY * targetMonitor.WorkAreaHeight);
        }
        else
        {
            x = targetMonitor.WorkAreaX + 32;
            y = targetMonitor.WorkAreaY + 32;
        }

        int maxX = Math.Max(targetMonitor.WorkAreaX, targetMonitor.WorkAreaX + targetMonitor.WorkAreaWidth - width);
        int maxY = Math.Max(targetMonitor.WorkAreaY, targetMonitor.WorkAreaY + targetMonitor.WorkAreaHeight - height);
        mapped.X = Math.Clamp(x, targetMonitor.WorkAreaX, maxX);
        mapped.Y = Math.Clamp(y, targetMonitor.WorkAreaY, maxY);
        if (mapped.CompactPlacement is { } compact)
        {
            // A valid compact anchor is resolved by WidgetCompactBoundsCalculator.
            // Keeping a safe physical fallback also handles legacy unanchored data.
            compact.X = mapped.X;
            compact.Y = mapped.Y;
        }
    }

    private static IEnumerable<(string SurfaceId, WidgetConfig Config, WidgetGroupConfig? Group)>
        EnumerateSurfaces(AppSettings settings)
    {
        var groupedMemberIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            foreach (string memberId in group.MemberIds)
            {
                groupedMemberIds.Add(memberId);
            }

            WidgetConfig? active = settings.Widgets.FirstOrDefault(widget =>
                string.Equals(widget.Id, group.ActiveMemberId, StringComparison.Ordinal)) ??
                settings.Widgets.FirstOrDefault(widget => group.MemberIds.Contains(widget.Id, StringComparer.Ordinal));
            if (active is not null)
            {
                yield return (ResolveGroupSurfaceId(group), active, group);
            }
        }

        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (!groupedMemberIds.Contains(widget.Id))
            {
                yield return (widget.Id, widget, null);
            }
        }
    }

    private static WidgetSurfaceLayoutProfile CaptureWidgetLayout(
        WidgetConfig config,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        var layout = new WidgetSurfaceLayoutProfile
        {
            X = config.X,
            Y = config.Y,
            PositionAnchor = config.PositionAnchor,
            PositionMarginX = config.PositionMarginX,
            PositionMarginY = config.PositionMarginY,
            PositionMonitorKey = config.PositionMonitorKey,
            PositionMonitorDeviceName = config.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = config.PositionMonitorWasPrimary,
            ScreenBindingMode = config.ScreenBindingMode,
            BoundScreenId = config.BoundScreenId,
            BoundsCoordinateVersion = config.BoundsCoordinateVersion,
            Width = config.Width,
            Height = config.Height,
            CompactPlacement = CloneCompactPlacement(config.CompactPlacement),
            CompactWidth = config.CompactWidth
        };
        layout.PositionMonitorStableId = SelectSourceMonitor(layout, monitors)?.StableId;
        return layout;
    }

    private static WidgetSurfaceLayoutProfile CaptureGroupLayout(
        WidgetGroupConfig group,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        var layout = new WidgetSurfaceLayoutProfile
        {
            X = group.X,
            Y = group.Y,
            PositionAnchor = group.PositionAnchor,
            PositionMarginX = group.PositionMarginX,
            PositionMarginY = group.PositionMarginY,
            PositionMonitorKey = group.PositionMonitorKey,
            PositionMonitorDeviceName = group.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = group.PositionMonitorWasPrimary,
            ScreenBindingMode = group.ScreenBindingMode,
            BoundScreenId = group.BoundScreenId,
            BoundsCoordinateVersion = group.BoundsCoordinateVersion,
            Width = group.Width,
            Height = group.Height,
            CompactPlacement = CloneCompactPlacement(group.CompactPlacement),
            CompactWidth = group.CompactWidth
        };
        layout.PositionMonitorStableId = SelectSourceMonitor(layout, monitors)?.StableId;
        return layout;
    }

    /// <summary>
    /// Realizes the physical X/Y cache of an entry from its anchor + margins
    /// (DIP) at the CURRENT monitor metadata (spec 5.2). The stored sizes
    /// and margins — the placement intent — are never touched here; only the
    /// "last actual position" cache follows DPI/geometry changes under the
    /// same v4 key.
    /// </summary>
    private static void RealizeEntryPhysicalCache(
        WidgetSurfaceLayoutProfile entry,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        if (monitors.Count == 0)
        {
            return;
        }

        string token = DisplayIdentityTokens.TokenFor(
            entry.PositionMonitorStableId,
            SafeWidth(entry.PositionMonitorKey),
            SafeHeight(entry.PositionMonitorKey));
        List<WidgetTopologyMonitorProfile> tokenMatches = monitors.Where(candidate =>
            string.Equals(
                DisplayIdentityTokens.TokenFor(candidate),
                token,
                StringComparison.OrdinalIgnoreCase)).ToList();
        WidgetTopologyMonitorProfile? monitor;
        if (tokenMatches.Count > 1)
        {
            // Same-spec degenerate monitors share one geo token: prefer the
            // candidate whose alias still matches the entry's device-name
            // hint, then the shared fallback chain.
            App.Log(
                $"[DisplayTopology] Monitor token {token} is ambiguous across " +
                $"{tokenMatches.Count} monitors");
            monitor = DisplayPlacementResolver.IsDegenerateIdentity(entry.PositionMonitorStableId)
                ? tokenMatches.FirstOrDefault(candidate => string.Equals(
                    candidate.DeviceName,
                    entry.PositionMonitorDeviceName,
                    StringComparison.OrdinalIgnoreCase))
                : tokenMatches[0];
            monitor ??= monitors.FirstOrDefault(candidate => candidate.IsPrimary) ??
                monitors[0];
        }
        else
        {
            monitor = tokenMatches.FirstOrDefault() ??
                monitors.FirstOrDefault(candidate => candidate.IsPrimary) ??
                monitors[0];
        }

        double scale = NormalizeScale(monitor.DpiScale);
        int width = Math.Max(1, (int)Math.Round(Math.Max(SettingsService.MinWidgetWidth, entry.Width) * scale));
        int height = Math.Max(1, (int)Math.Round(Math.Max(SettingsService.MinWidgetHeight, entry.Height) * scale));
        bool anchorRight = entry.PositionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = entry.PositionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        int marginX = Math.Max(0, (int)Math.Round(Math.Max(0, entry.PositionMarginX) * scale));
        int marginY = Math.Max(0, (int)Math.Round(Math.Max(0, entry.PositionMarginY) * scale));
        entry.X = anchorRight
            ? monitor.WorkAreaX + monitor.WorkAreaWidth - width - marginX
            : monitor.WorkAreaX + marginX;
        entry.Y = anchorBottom
            ? monitor.WorkAreaY + monitor.WorkAreaHeight - height - marginY
            : monitor.WorkAreaY + marginY;
        entry.Width = Math.Max(SettingsService.MinWidgetWidth, entry.Width);
        entry.Height = Math.Max(SettingsService.MinWidgetHeight, entry.Height);
    }

    private static void ApplyToWidget(WidgetConfig config, WidgetSurfaceLayoutProfile layout)
    {
        config.X = layout.X;
        config.Y = layout.Y;
        config.PositionAnchor = layout.PositionAnchor;
        config.PositionMarginX = layout.PositionMarginX;
        config.PositionMarginY = layout.PositionMarginY;
        config.PositionMonitorKey = layout.PositionMonitorKey;
        config.PositionMonitorDeviceName = layout.PositionMonitorDeviceName;
        config.PositionMonitorWasPrimary = layout.PositionMonitorWasPrimary;
        // The stable id must be backfilled together with the device name:
        // WidgetPositioningService ranks it ABOVE the device name, so leaving
        // a stale id behind would keep resolving the freshly projected
        // geometry onto the previous monitor (the wrong-screen regression).
        config.PositionMonitorStableId = layout.PositionMonitorStableId;
        // Screen binding is topology-independent user intent: profile
        // activation repositions but never rebinds the surface, so a stale
        // profile captured before a pin cannot undo it.
        config.BoundsCoordinateVersion = layout.BoundsCoordinateVersion;
        config.Width = layout.Width;
        config.Height = layout.Height;
        config.CompactPlacement = CloneCompactPlacement(layout.CompactPlacement);
        config.CompactWidth = layout.CompactWidth;
    }

    private static void ApplyToGroup(WidgetGroupConfig group, WidgetSurfaceLayoutProfile layout)
    {
        group.X = layout.X;
        group.Y = layout.Y;
        group.PositionAnchor = layout.PositionAnchor;
        group.PositionMarginX = layout.PositionMarginX;
        group.PositionMarginY = layout.PositionMarginY;
        group.PositionMonitorKey = layout.PositionMonitorKey;
        group.PositionMonitorDeviceName = layout.PositionMonitorDeviceName;
        group.PositionMonitorWasPrimary = layout.PositionMonitorWasPrimary;
        // Same stable-id backfill as ApplyToWidget: the group surface resolves
        // through the identical stable-id-first chain.
        group.PositionMonitorStableId = layout.PositionMonitorStableId;
        // Same orthogonality rule as ApplyToWidget: never rebind from a profile.
        group.BoundsCoordinateVersion = layout.BoundsCoordinateVersion;
        group.Width = layout.Width;
        group.Height = layout.Height;
        group.CompactPlacement = CloneCompactPlacement(layout.CompactPlacement);
        group.CompactWidth = layout.CompactWidth;
    }

    private static WidgetTopologyMonitorProfile? SelectSourceMonitor(
        WidgetSurfaceLayoutProfile layout,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        // Identity resolution goes through the shared resolver (spec 5.1) so
        // degenerate stable ids can never match a renumbered alias here.
        WidgetScreenInfo? resolved = DisplayPlacementResolver.ResolveEntryDisplay(
            new DisplayPlacementEntryReference(
                layout.PositionMonitorStableId,
                layout.PositionMonitorDeviceName,
                layout.PositionMonitorKey,
                layout.PositionMonitorWasPrimary),
            [.. monitors.Select((monitor, index) => ToScreenInfo(monitor, index + 1))]);
        if (resolved is not null)
        {
            WidgetTopologyMonitorProfile? matched = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.StableId.Trim(), resolved.StableId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        return monitors.FirstOrDefault(monitor =>
                   layout.X >= monitor.MonitorX &&
                   layout.X < monitor.MonitorX + monitor.MonitorWidth &&
                   layout.Y >= monitor.MonitorY &&
                   layout.Y < monitor.MonitorY + monitor.MonitorHeight) ??
               (layout.PositionMonitorWasPrimary == true
                   ? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
                   : null) ??
               monitors.FirstOrDefault();
    }

    private static WidgetTopologyMonitorProfile? SelectTargetMonitor(
        WidgetSurfaceLayoutProfile layout,
        WidgetTopologyMonitorProfile? sourceMonitor,
        IReadOnlyList<WidgetTopologyMonitorProfile> targets)
    {
        // An explicit pin outranks every positional heuristic: when the bound
        // monitor exists in the target topology, the layout migrates onto it.
        if (layout.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
            !string.IsNullOrWhiteSpace(layout.BoundScreenId))
        {
            WidgetTopologyMonitorProfile? bound = targets.FirstOrDefault(target =>
                MonitorStableIdEquals(target.StableId, layout.BoundScreenId));
            if (bound is not null)
            {
                return bound;
            }
        }

        // Target selection funnels through the shared resolver (spec 5.1):
        // entry identity first (same physical monitor), then the direction /
        // size / alias heuristic. This replaces the previous placement-and-
        // WasPrimary heuristics that could diverge from runtime resolution.
        if (targets.Count > 0)
        {
            ResolvedDisplayPlacement decision = DisplayPlacementResolver.Resolve(
                new DisplayPlacementIntent(WidgetScreenBindingMode.Unbound, null),
                new DisplayPlacementEntryReference(
                    sourceMonitor?.StableId ?? layout.PositionMonitorStableId,
                    sourceMonitor?.DeviceName ?? layout.PositionMonitorDeviceName,
                    sourceMonitor is null ? layout.PositionMonitorKey : null,
                    layout.PositionMonitorWasPrimary ?? sourceMonitor?.IsPrimary),
                [.. targets.Select((target, index) => ToScreenInfo(target, index + 1))]);
            WidgetTopologyMonitorProfile? matched = targets.FirstOrDefault(target =>
                string.Equals(target.StableId.Trim(), decision.Display.StableId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return matched;
            }
        }

        return targets.FirstOrDefault(target => target.IsPrimary) ?? targets.FirstOrDefault();
    }

    /// <summary>
    /// Stable-id equality for binding comparisons: ids that degenerated to the
    /// unstable <c>\\.\DISPLAYn</c> name or "unknown-display" never match, so a
    /// pin cannot accidentally latch onto a renumbered monitor.
    /// </summary>
    private static bool MonitorStableIdEquals(string? left, string? right)
    {
        string normalizedLeft = NormalizeStableIdentityForKey(left);
        string normalizedRight = NormalizeStableIdentityForKey(right);
        if (string.Equals(normalizedLeft, "geometry-only", StringComparison.Ordinal) ||
            string.Equals(normalizedRight, "geometry-only", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static bool MonitorIdentityEquals(
        WidgetTopologyMonitorProfile left,
        WidgetTopologyMonitorProfile right)
    {
        string leftStableId = NormalizeStableIdentityForKey(left.StableId);
        string rightStableId = NormalizeStableIdentityForKey(right.StableId);
        if (!string.Equals(leftStableId, "geometry-only", StringComparison.Ordinal) &&
            !string.Equals(rightStableId, "geometry-only", StringComparison.Ordinal))
        {
            return string.Equals(leftStableId, rightStableId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(left.DeviceName) &&
            string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MonitorPlacementEquals(
        WidgetTopologyMonitorProfile left,
        WidgetTopologyMonitorProfile right) =>
        left.IsPrimary == right.IsPrimary &&
        left.MonitorX == right.MonitorX &&
        left.MonitorY == right.MonitorY &&
        left.MonitorWidth == right.MonitorWidth &&
        left.MonitorHeight == right.MonitorHeight &&
        Math.Abs(NormalizeScale(left.DpiScale) - NormalizeScale(right.DpiScale)) < 0.001;

    /// <summary>
    /// Provisional (first-seen) profile bookkeeping (spec 5.6): keys that
    /// have neither survived the 10-second window nor received a user
    /// placement commit are excluded from LRU eviction so a transient
    /// topology cannot evict a real arrangement. In-memory only.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _provisionalProfiles = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProvisionalSurvival = TimeSpan.FromSeconds(10);

    internal void PromoteProvisionalProfile(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _provisionalProfiles.Remove(key);
        }
    }

    private void TrackProvisionalProfile(string key)
    {
        // Lazy promotion on activity: surviving past the window makes the
        // profile a real candidate for retention/eviction.
        foreach ((string provisionalKey, DateTimeOffset firstSeen) in _provisionalProfiles.ToList())
        {
            if (DateTimeOffset.UtcNow - firstSeen >= ProvisionalSurvival)
            {
                _provisionalProfiles.Remove(provisionalKey);
            }
        }

        if (!_provisionalProfiles.ContainsKey(key))
        {
            _provisionalProfiles[key] = DateTimeOffset.UtcNow;
        }
    }

    private static bool PruneProfiles(
        AppSettings settings,
        string activeKey,
        IReadOnlyDictionary<string, DateTimeOffset> provisionalProfiles)
    {
        bool changed = false;
        while (settings.WidgetTopologyLayouts.Count > MaximumRetainedProfiles)
        {
            string? oldest = settings.WidgetTopologyLayouts
                .Where(pair => !string.Equals(pair.Key, activeKey, StringComparison.Ordinal) &&
                               !provisionalProfiles.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Value.LastUsedAtUtc)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (oldest is null)
            {
                break;
            }

            changed |= settings.WidgetTopologyLayouts.Remove(oldest);
        }

        return changed;
    }

    private static bool RemoveStaleSurfaces(AppSettings settings)
    {
        HashSet<string> validSurfaceIds = EnumerateSurfaces(settings)
            .Select(surface => surface.SurfaceId)
            .ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        foreach (WidgetTopologyLayoutProfile profile in settings.WidgetTopologyLayouts.Values)
        {
            foreach (string staleId in profile.Surfaces.Keys
                         .Where(surfaceId => !validSurfaceIds.Contains(surfaceId))
                         .ToList())
            {
                changed |= profile.Surfaces.Remove(staleId);
            }
        }

        return changed;
    }

    private static string ResolveGroupSurfaceId(WidgetGroupConfig group) =>
        string.IsNullOrWhiteSpace(group.SurfaceId) ? $"group:{group.Id}" : group.SurfaceId;

    private static string CreateWorkAreaKey(WidgetTopologyMonitorProfile monitor) =>
        $"{monitor.WorkAreaX}:{monitor.WorkAreaY}:{monitor.WorkAreaWidth}:{monitor.WorkAreaHeight}";

    private static double EffectiveExtent(int physicalPixels, double scale) =>
        Math.Max(1, physicalPixels) / NormalizeScale(scale);

    private static double ClampLogicalSize(double value, double minimum, double maximum)
    {
        double upper = Math.Max(minimum, maximum);
        double finite = double.IsFinite(value) ? value : minimum;
        return Math.Clamp(finite, minimum, upper);
    }

    private static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1;

    private static List<WidgetTopologyMonitorProfile> CloneMonitors(
        IEnumerable<WidgetTopologyMonitorProfile> monitors) =>
        monitors.Select(monitor => new WidgetTopologyMonitorProfile
        {
            StableId = monitor.StableId,
            DeviceName = monitor.DeviceName,
            IsPrimary = monitor.IsPrimary,
            MonitorX = monitor.MonitorX,
            MonitorY = monitor.MonitorY,
            MonitorWidth = monitor.MonitorWidth,
            MonitorHeight = monitor.MonitorHeight,
            WorkAreaX = monitor.WorkAreaX,
            WorkAreaY = monitor.WorkAreaY,
            WorkAreaWidth = monitor.WorkAreaWidth,
            WorkAreaHeight = monitor.WorkAreaHeight,
            DpiScale = NormalizeScale(monitor.DpiScale)
        }).ToList();

    private static WidgetSurfaceLayoutProfile CloneLayout(WidgetSurfaceLayoutProfile source) =>
        new()
        {
            PositionMonitorStableId = source.PositionMonitorStableId,
            ScreenBindingMode = source.ScreenBindingMode,
            BoundScreenId = source.BoundScreenId,
            IsAuthoritative = source.IsAuthoritative,
            AuthoredAtUtc = source.AuthoredAtUtc,
            X = source.X,
            Y = source.Y,
            PositionAnchor = source.PositionAnchor,
            PositionMarginX = source.PositionMarginX,
            PositionMarginY = source.PositionMarginY,
            PositionMonitorKey = source.PositionMonitorKey,
            PositionMonitorDeviceName = source.PositionMonitorDeviceName,
            PositionMonitorWasPrimary = source.PositionMonitorWasPrimary,
            BoundsCoordinateVersion = source.BoundsCoordinateVersion,
            Width = source.Width,
            Height = source.Height,
            CompactPlacement = CloneCompactPlacement(source.CompactPlacement),
            CompactWidth = source.CompactWidth
        };

    private static WidgetCompactPlacement? CloneCompactPlacement(WidgetCompactPlacement? source) =>
        source is null
            ? null
            : new WidgetCompactPlacement
            {
                X = source.X,
                Y = source.Y,
                PositionAnchor = source.PositionAnchor,
                PositionMarginX = source.PositionMarginX,
                PositionMarginY = source.PositionMarginY,
                PositionMonitorKey = source.PositionMonitorKey,
                PositionMonitorDeviceName = source.PositionMonitorDeviceName,
                PositionMonitorStableId = source.PositionMonitorStableId,
                PositionMonitorWasPrimary = source.PositionMonitorWasPrimary,
                BoundsCoordinateVersion = source.BoundsCoordinateVersion
            };

    private static string ResolveStableMonitorId(string? deviceName)
    {
        return Win32Helper.ResolveStableMonitorId(deviceName);
    }
}

internal sealed record WidgetDisplayTopologySnapshot(
    string Key,
    IReadOnlyList<WidgetTopologyMonitorProfile> Monitors);
