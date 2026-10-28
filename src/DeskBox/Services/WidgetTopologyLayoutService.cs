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
        Dictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans = BuildDisplayPlans(settings, profile);
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, member.Id);
        if (group is not null)
        {
            string surfaceId = ResolveGroupSurfaceId(group);
            profile.Surfaces[surfaceId] = MergeCapturedEntry(
                profile.Surfaces.GetValueOrDefault(surfaceId),
                CaptureGroupLayout(group, profile.Monitors),
                profile.Monitors,
                plans,
                IsFixedContentSurface(settings, member, group));
        }
        else
        {
            profile.Surfaces[member.Id] = MergeCapturedEntry(
                profile.Surfaces.GetValueOrDefault(member.Id),
                CaptureWidgetLayout(member, profile.Monitors),
                profile.Monitors,
                plans,
                IsFixedContentSurface(settings, member, null));
        }

        return true;
    }

    private static WidgetSurfaceLayoutProfile MergeCapturedEntry(
        WidgetSurfaceLayoutProfile? existing,
        WidgetSurfaceLayoutProfile captured,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors,
        IReadOnlyDictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans,
        bool fixedContent)
    {
        captured = PreserveAuthority(existing, captured);
        if (existing?.Reference is { } reference && ProjectionMatches(existing, captured))
        {
            captured.Reference = reference.Clone();
            return captured;
        }

        WidgetTopologyMonitorProfile? monitor = ResolveRealizationMonitor(captured, monitors);
        captured.Reference = InvertProjection(
            captured,
            monitor,
            monitor is not null && plans.TryGetValue(monitor, out ElasticDisplayPlan? plan) ? plan : null,
            fixedContent);
        return captured;
    }

    private static bool ProjectionMatches(
        WidgetSurfaceLayoutProfile existing,
        WidgetSurfaceLayoutProfile captured) =>
        string.Equals(existing.PositionAnchor, captured.PositionAnchor, StringComparison.Ordinal) &&
        NearlyEqual(existing.PositionMarginX, captured.PositionMarginX) &&
        NearlyEqual(existing.PositionMarginY, captured.PositionMarginY) &&
        NearlyEqual(existing.Width, captured.Width) &&
        NearlyEqual(existing.Height, captured.Height) &&
        string.Equals(
            existing.PositionMonitorStableId?.Trim(),
            captured.PositionMonitorStableId?.Trim(),
            StringComparison.OrdinalIgnoreCase) &&
        (existing.CompactPlacement, captured.CompactPlacement) switch
        {
            (null, null) => true,
            ({ } left, { } right) =>
                string.Equals(left.PositionAnchor, right.PositionAnchor, StringComparison.Ordinal) &&
                NearlyEqual(left.PositionMarginX, right.PositionMarginX) &&
                NearlyEqual(left.PositionMarginY, right.PositionMarginY),
            _ => false
        };

    // HWND pixel quantization moves realized geometry by up to half a
    // physical pixel (one pixel is 0.8 DIP at 125%). A one-DIP tolerance
    // absorbs that noise so pure DPI/resolution flips keep the authored
    // reference untouched, while any deliberate drag (≥ 1 DIP) still
    // re-captures through the inversion path.
    private const double CaptureToleranceDips = 1.0;

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < CaptureToleranceDips;

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
        foreach ((string key, WidgetTopologyLayoutProfile profile) in settings.WidgetLayout.WidgetTopologyLayouts)
        {
            changed |= profile is not null && UpgradeProfile(key, profile, previousKey);
        }

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
                SeedProfile(settings, targetProfile);
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
                SeedProfile(settings, targetProfile);
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
    // Deep review R17 (G3 audit): the sourceProfile parameter was never
    // read — removed.
    private static void SeedProfile(
        AppSettings settings,
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

        // P3 arbitration: applying a stored entry older than the config's
        // deliberate placement would resurrect a stale snapshot over the
        // user's latest position (the reveal-jump incident's executor).
        bool configOutranks = group is null &&
            ConfigPlacementOutranksEntry(config, existing) &&
            SameEntryDisplayAsConfig(existing, config, targetProfile);

        return needsReseed || configOutranks
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

        // P3 arbitration: a deliberate placement newer than whatever the
        // history picked is the truth regardless of which profile captured
        // it (the capture may even live in a pruned profile) — seed from
        // the live config instead. Cross-display entries keep their
        // projected intent; only the same-display lineage defers.
        if (group is null && ConfigPlacementOutranksEntry(config, src))
        {
            src = FallbackCurrent();
            srcProfile = targetProfile;
            targetDisplay = FindMonitor(targetProfile, EntryStableId(src)) ??
                ResolveHeuristicTarget(src, srcProfile, targetProfile);
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
                [.. targetProfile.Monitors],
                out bool usedFallbackPlacement);
            // Same-display projections keep the user's authored placement;
            // cross-display mappings are heuristic and never authoritative;
            // fallback placements are shelter geometry and never intent.
            seeded.IsAuthoritative = !usedFallbackPlacement &&
                sameDisplay &&
                src.IsAuthoritative == true;
            seeded.AuthoredAtUtc = seeded.IsAuthoritative == true ? src.AuthoredAtUtc : null;
        }

        return seeded;
    }

    private static DisplayPlacementIntent SurfaceIntent(WidgetConfig config, WidgetGroupConfig? group) =>
        group is not null
            ? new DisplayPlacementIntent(group.ScreenBindingMode, group.BoundScreenId)
            : new DisplayPlacementIntent(config.ScreenBindingMode, config.BoundScreenId);

    /// <summary>
    /// The deliberate-placement arbitration (P3): the config's
    /// <see cref="WidgetConfig.LastUserPlacementUtc"/> outranks a stored
    /// entry whose authoritative timestamp is older (or absent). Entries
    /// captured from fallback geometry carry no timestamp and always lose.
    /// </summary>
    internal static bool ConfigPlacementOutranksEntry(
        WidgetConfig config,
        WidgetSurfaceLayoutProfile entry)
    {
        return config.LastUserPlacementUtc is { } configUtc &&
            (entry.AuthoredAtUtc is not { } entryUtc || configUtc > entryUtc);
    }

    /// <summary>
    /// The arbitration is same-display only: an entry targeting another
    /// physical monitor describes that screen's own placement history and
    /// keeps its projected intent; the config's stamp belongs to the
    /// display it was placed on.
    /// </summary>
    internal static bool SameEntryDisplayAsConfig(
        WidgetSurfaceLayoutProfile entry,
        WidgetConfig config,
        WidgetTopologyLayoutProfile targetProfile)
    {
        string? entryId = EntryStableId(entry);
        if (entryId is null)
        {
            return true;
        }

        string? configId = DisplayPlacementResolver.IsDegenerateIdentity(config.PositionMonitorStableId)
            ? null
            : config.PositionMonitorStableId?.Trim();
        return configId is null ||
            string.Equals(entryId, configId, StringComparison.OrdinalIgnoreCase);
    }

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
        Dictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans = initialAuthoritative
            ? []
            : BuildDisplayPlans(settings, profile);
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
                captured = MergeCapturedEntry(
                    profile.Surfaces.GetValueOrDefault(surfaceId),
                    captured,
                    profile.Monitors,
                    plans,
                    IsFixedContentSurface(settings, config, group));
            }

            profile.Surfaces[surfaceId] = captured;
        }

        profile.LastUsedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void ApplyProfile(AppSettings settings, WidgetTopologyLayoutProfile profile)
    {
        List<(string SurfaceId, WidgetConfig Config, WidgetGroupConfig? Group)> surfaces = [];
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            WidgetConfig? representative = settings.Widgets.FirstOrDefault(widget =>
                group.MemberIds.Contains(widget.Id, StringComparer.Ordinal));
            surfaces.Add((
                ResolveGroupSurfaceId(group),
                representative ?? new WidgetConfig { Id = group.MemberIds.FirstOrDefault() ?? group.SurfaceId },
                group));
        }

        HashSet<string> groupedMemberIds = settings.WidgetGroups
            .SelectMany(group => group.MemberIds)
            .ToHashSet(StringComparer.Ordinal);
        surfaces.AddRange(settings.Widgets
            .Where(widget => !groupedMemberIds.Contains(widget.Id))
            .Select(widget => (widget.Id, widget, (WidgetGroupConfig?)null)));

        // Activation re-resolves each surface's entry against its intent
        // (spec 5.3): an entry whose screen no longer matches the surface's
        // home / follow-primary intent is re-seeded instead of applied.
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in surfaces)
        {
            profile.Surfaces[surfaceId] = ResolveSurfaceEntryForActivation(
                settings,
                surfaceId,
                config,
                group,
                profile);
        }

        Dictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans = BuildDisplayPlans(settings, profile);
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in surfaces)
        {
            WidgetSurfaceLayoutProfile layout = RealizeEntry(
                profile.Surfaces[surfaceId],
                profile.Monitors,
                plans,
                IsFixedContentSurface(settings, config, group));
            profile.Surfaces[surfaceId] = layout;
            if (group is null)
            {
                ApplyToWidget(config, layout);
                continue;
            }

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
    }

    private static Dictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> BuildDisplayPlans(
        AppSettings settings,
        WidgetTopologyLayoutProfile profile)
    {
        Dictionary<WidgetTopologyMonitorProfile, List<(WidgetSurfaceLayoutReference Reference, bool Occupies)>> placements = [];
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? _) in EnumerateSurfaces(settings))
        {
            if (!profile.Surfaces.TryGetValue(surfaceId, out WidgetSurfaceLayoutProfile? entry) ||
                entry is null ||
                ResolveRealizationMonitor(entry, profile.Monitors) is not { } monitor ||
                UsableReference(entry, monitor) is not { } reference)
            {
                continue;
            }

            if (!placements.TryGetValue(monitor, out var list))
            {
                placements[monitor] = list = [];
            }

            list.Add((reference, !config.IsDisabled));
        }

        // Keyed by monitor instance rather than token: degenerate monitors can
        // share their geo:WxH token while still needing separate plans (e.g.
        // different DPI), and every lookup resolves through the same
        // profile.Monitors list instances.
        Dictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans = [];
        foreach (WidgetTopologyMonitorProfile monitor in profile.Monitors)
        {
            (double width, double height) = EffectiveWorkArea(monitor);
            List<(WidgetSurfaceLayoutReference Reference, bool Occupies)> items =
                placements.GetValueOrDefault(monitor) ?? [];
            (double frameWidth, double frameHeight) = items.Count == 0
                ? (width, height)
                : items
                    .GroupBy(item => (Math.Round(item.Reference.FrameWidth), Math.Round(item.Reference.FrameHeight)))
                    .OrderByDescending(frame => frame.Count())
                    .ThenBy(frame => Math.Abs(frame.Key.Item1 - width) + Math.Abs(frame.Key.Item2 - height))
                    .Select(frame => (frame.First().Reference.FrameWidth, frame.First().Reference.FrameHeight))
                    .First();
            plans[monitor] = ElasticDisplayPlan.Create(
                frameWidth,
                frameHeight,
                width,
                height,
                [.. items
                    .Where(item => item.Occupies)
                    .Select(item => PlaceRect(item.Reference, frameWidth, frameHeight))]);
        }

        return plans;
    }

    private static WidgetSurfaceLayoutProfile RealizeEntry(
        WidgetSurfaceLayoutProfile entry,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors,
        IReadOnlyDictionary<WidgetTopologyMonitorProfile, ElasticDisplayPlan> plans,
        bool fixedContent)
    {
        WidgetSurfaceLayoutProfile realized = CloneLayout(entry);
        if (ResolveRealizationMonitor(entry, monitors) is { } monitor &&
            UsableReference(entry, monitor) is { } reference &&
            plans.TryGetValue(monitor, out ElasticDisplayPlan? plan))
        {
            realized.Reference = reference.Clone();
            double maxGrow = fixedContent ? WidgetElasticLayout.FixedContentMaxGrowScale : double.PositiveInfinity;
            ElasticRect rect = PlaceRect(reference, plan.FrameWidth, plan.FrameHeight);
            realized.PositionAnchor = reference.PositionAnchor;
            (realized.PositionMarginX, realized.Width) = ProjectAxis(
                plan.X,
                rect.Left,
                rect.Right,
                IsFarAnchorX(reference.PositionAnchor),
                reference.PositionMarginX,
                reference.Width,
                maxGrow,
                plan.AdaptiveMinimumWidth);
            (realized.PositionMarginY, realized.Height) = ProjectAxis(
                plan.Y,
                rect.Top,
                rect.Bottom,
                IsFarAnchorY(reference.PositionAnchor),
                reference.PositionMarginY,
                reference.Height,
                maxGrow,
                plan.AdaptiveMinimumHeight);
            if (realized.CompactPlacement is { } compact &&
                reference.CompactPositionAnchor is { } compactAnchor &&
                reference.CompactPositionMarginX is { } compactMarginX &&
                reference.CompactPositionMarginY is { } compactMarginY)
            {
                compact.PositionAnchor = compactAnchor;
                compact.PositionMarginX = ProjectPoint(plan.X, IsFarAnchorX(compactAnchor), compactMarginX);
                compact.PositionMarginY = ProjectPoint(plan.Y, IsFarAnchorY(compactAnchor), compactMarginY);
            }
        }

        RealizeEntryPhysicalCache(realized, monitors);
        return realized;
    }

    private static (double Margin, double Length) ProjectAxis(
        ElasticAxisPlan axis,
        double start,
        double end,
        bool anchorFar,
        double margin,
        double length,
        double maxGrow,
        double minimumLength)
    {
        if (axis.IsIdentity)
        {
            return (margin, length);
        }

        (double mappedStart, double mappedEnd) = axis.MapItem(start, end, anchorFar, maxGrow);
        double mappedLength = Math.Clamp(
            mappedEnd - mappedStart,
            Math.Min(minimumLength, axis.TargetLength),
            axis.TargetLength);
        double mappedMargin = anchorFar ? axis.TargetLength - mappedEnd : mappedStart;
        return (Math.Clamp(mappedMargin, 0, Math.Max(0, axis.TargetLength - mappedLength)), mappedLength);
    }

    private static double ProjectPoint(ElasticAxisPlan axis, bool anchorFar, double margin)
    {
        if (axis.IsIdentity)
        {
            return margin;
        }

        double mapped = axis.Map(anchorFar ? axis.ReferenceLength - margin : margin);
        return Math.Max(0, anchorFar ? axis.TargetLength - mapped : mapped);
    }

    private static WidgetSurfaceLayoutReference? InvertProjection(
        WidgetSurfaceLayoutProfile projection,
        WidgetTopologyMonitorProfile? monitor,
        ElasticDisplayPlan? plan,
        bool fixedContent)
    {
        WidgetSurfaceLayoutReference? projected = DeriveReference(projection, monitor);
        if (projected is null || plan is null)
        {
            return projected;
        }

        double maxGrow = fixedContent ? WidgetElasticLayout.FixedContentMaxGrowScale : double.PositiveInfinity;
        ElasticRect rect = PlaceRect(projected, plan.TargetWidth, plan.TargetHeight);
        WidgetSurfaceLayoutReference reference = projected.Clone();
        reference.FrameWidth = plan.FrameWidth;
        reference.FrameHeight = plan.FrameHeight;
        (reference.PositionMarginX, reference.Width) = UnprojectAxis(
            plan.X,
            rect.Left,
            rect.Right,
            IsFarAnchorX(projected.PositionAnchor),
            projected.PositionMarginX,
            projected.Width,
            maxGrow);
        (reference.PositionMarginY, reference.Height) = UnprojectAxis(
            plan.Y,
            rect.Top,
            rect.Bottom,
            IsFarAnchorY(projected.PositionAnchor),
            projected.PositionMarginY,
            projected.Height,
            maxGrow);
        if (projected.CompactPositionAnchor is { } compactAnchor &&
            projected.CompactPositionMarginX is { } compactMarginX &&
            projected.CompactPositionMarginY is { } compactMarginY)
        {
            reference.CompactPositionMarginX = UnprojectPoint(plan.X, IsFarAnchorX(compactAnchor), compactMarginX);
            reference.CompactPositionMarginY = UnprojectPoint(plan.Y, IsFarAnchorY(compactAnchor), compactMarginY);
        }

        return reference;
    }

    private static (double Margin, double Length) UnprojectAxis(
        ElasticAxisPlan axis,
        double start,
        double end,
        bool anchorFar,
        double margin,
        double length,
        double maxGrow)
    {
        if (axis.IsIdentity)
        {
            return (margin, length);
        }

        (double referenceStart, double referenceEnd) = axis.UnmapItem(start, end, anchorFar, maxGrow);
        double referenceMargin = anchorFar ? axis.ReferenceLength - referenceEnd : referenceStart;
        return (Math.Max(0, referenceMargin), Math.Max(1, referenceEnd - referenceStart));
    }

    private static double UnprojectPoint(ElasticAxisPlan axis, bool anchorFar, double margin)
    {
        if (axis.IsIdentity)
        {
            return margin;
        }

        double reference = axis.Unmap(anchorFar ? axis.TargetLength - margin : margin);
        return Math.Max(0, anchorFar ? axis.ReferenceLength - reference : reference);
    }

    private static WidgetSurfaceLayoutReference? UsableReference(
        WidgetSurfaceLayoutProfile entry,
        WidgetTopologyMonitorProfile monitor) =>
        entry.Reference is { } reference &&
        IsCornerAnchor(reference.PositionAnchor) &&
        double.IsFinite(reference.FrameWidth) && reference.FrameWidth > 0 &&
        double.IsFinite(reference.FrameHeight) && reference.FrameHeight > 0 &&
        double.IsFinite(reference.Width) && double.IsFinite(reference.Height) &&
        double.IsFinite(reference.PositionMarginX) && double.IsFinite(reference.PositionMarginY)
            ? reference
            : DeriveReference(entry, monitor);

    private static WidgetSurfaceLayoutReference? DeriveReference(
        WidgetSurfaceLayoutProfile entry,
        WidgetTopologyMonitorProfile? monitor)
    {
        if (monitor is null ||
            !IsCornerAnchor(entry.PositionAnchor) ||
            entry.BoundsCoordinateVersion < WidgetConfig.CurrentBoundsCoordinateVersion ||
            !double.IsFinite(entry.Width) || !double.IsFinite(entry.Height) ||
            !double.IsFinite(entry.PositionMarginX) || !double.IsFinite(entry.PositionMarginY))
        {
            return null;
        }

        (double frameWidth, double frameHeight) = EffectiveWorkArea(monitor);
        var reference = new WidgetSurfaceLayoutReference
        {
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            PositionAnchor = entry.PositionAnchor,
            PositionMarginX = entry.PositionMarginX,
            PositionMarginY = entry.PositionMarginY,
            Width = entry.Width,
            Height = entry.Height
        };
        if (entry.CompactPlacement is { } compact &&
            IsCornerAnchor(compact.PositionAnchor) &&
            compact.BoundsCoordinateVersion >= WidgetConfig.CurrentBoundsCoordinateVersion &&
            double.IsFinite(compact.PositionMarginX) &&
            double.IsFinite(compact.PositionMarginY))
        {
            reference.CompactPositionAnchor = compact.PositionAnchor;
            reference.CompactPositionMarginX = compact.PositionMarginX;
            reference.CompactPositionMarginY = compact.PositionMarginY;
        }

        return reference;
    }

    private static ElasticRect PlaceRect(
        WidgetSurfaceLayoutReference reference,
        double frameWidth,
        double frameHeight)
    {
        double left = IsFarAnchorX(reference.PositionAnchor)
            ? frameWidth - reference.PositionMarginX - reference.Width
            : reference.PositionMarginX;
        double top = IsFarAnchorY(reference.PositionAnchor)
            ? frameHeight - reference.PositionMarginY - reference.Height
            : reference.PositionMarginY;
        return new ElasticRect(left, top, left + reference.Width, top + reference.Height);
    }

    private static (double Width, double Height) EffectiveWorkArea(WidgetTopologyMonitorProfile monitor) =>
        (EffectiveExtent(monitor.WorkAreaWidth, monitor.DpiScale),
         EffectiveExtent(monitor.WorkAreaHeight, monitor.DpiScale));

    private static bool IsCornerAnchor(string? anchor) =>
        anchor is WidgetPositionAnchors.LeftTop or WidgetPositionAnchors.RightTop or
            WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;

    private static bool IsFarAnchorX(string? anchor) =>
        anchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;

    private static bool IsFarAnchorY(string? anchor) =>
        anchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;

    private static bool IsFixedContentSurface(
        AppSettings settings,
        WidgetConfig config,
        WidgetGroupConfig? group)
    {
        if (group is null)
        {
            return IsFixedContentKind(config.WidgetKind);
        }

        List<WidgetConfig> members = settings.WidgetLayout.Widgets
            .Where(widget => !widget.IsDisabled && group.MemberIds.Contains(widget.Id, StringComparer.Ordinal))
            .ToList();
        return members.Count > 0 && members.All(member => IsFixedContentKind(member.WidgetKind));
    }

    private static bool IsFixedContentKind(WidgetKind kind) =>
        kind is WidgetKind.Weather or WidgetKind.Music or WidgetKind.SystemMonitor or WidgetKind.Glance;

    /// <summary>
    /// Upgrades a pre-elastic profile (spec D11): derives each entry's
    /// reference from the geometry the profile last saw, and drops
    /// non-authoritative entries of inactive profiles, whose sizes the old
    /// ratio mapping baked in; activation re-seeds them unscaled.
    /// </summary>
    internal static bool UpgradeProfile(string key, WidgetTopologyLayoutProfile profile, string? activeKey)
    {
        if (profile.Version >= WidgetTopologyLayoutProfile.CurrentVersion)
        {
            return false;
        }

        profile.Monitors ??= [];
        profile.Surfaces ??= [];
        if (!string.Equals(key, activeKey, StringComparison.Ordinal))
        {
            foreach (string seededId in profile.Surfaces
                         .Where(pair => pair.Value?.IsAuthoritative != true)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                profile.Surfaces.Remove(seededId);
            }
        }

        foreach (WidgetSurfaceLayoutProfile entry in profile.Surfaces.Values)
        {
            if (entry is not null)
            {
                entry.Reference ??= DeriveReference(entry, ResolveRealizationMonitor(entry, profile.Monitors));
            }
        }

        profile.Version = WidgetTopologyLayoutProfile.CurrentVersion;
        return true;
    }

    internal static WidgetSurfaceLayoutProfile MapToTopology(
        WidgetSurfaceLayoutProfile source,
        IReadOnlyList<WidgetTopologyMonitorProfile> sourceMonitors,
        IReadOnlyList<WidgetTopologyMonitorProfile> targetMonitors) =>
        MapToTopology(source, sourceMonitors, targetMonitors, out _);

    internal static WidgetSurfaceLayoutProfile MapToTopology(
        WidgetSurfaceLayoutProfile source,
        IReadOnlyList<WidgetTopologyMonitorProfile> sourceMonitors,
        IReadOnlyList<WidgetTopologyMonitorProfile> targetMonitors,
        out bool usedFallbackPlacement)
    {
        WidgetTopologyMonitorProfile? sourceMonitor = SelectSourceMonitor(source, sourceMonitors);
        WidgetTopologyMonitorProfile? targetMonitor = SelectTargetMonitor(source, sourceMonitor, targetMonitors);
        if (targetMonitor is null)
        {
            // No target to project onto: a plain clone is not shelter
            // geometry.
            usedFallbackPlacement = false;
            return CloneLayout(source);
        }

        (double targetEffectiveWidth, double targetEffectiveHeight) = EffectiveWorkArea(targetMonitor);
        var mapped = CloneLayout(source);
        if (sourceMonitor is not null)
        {
            mapped.Reference = UsableReference(source, sourceMonitor)?.Clone();
        }

        mapped.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        // Seeding may carry an elastically projected size below the preferred
        // minimum; clamping here would inflate it back over its neighbours.
        mapped.Width = ClampLogicalSize(
            source.Width,
            WidgetElasticLayout.AbsoluteMinWidgetSizeDips,
            targetEffectiveWidth);
        mapped.Height = ClampLogicalSize(
            source.Height,
            WidgetElasticLayout.AbsoluteMinWidgetSizeDips,
            targetEffectiveHeight);
        mapped.PositionMarginX = Math.Max(0, source.PositionMarginX);
        mapped.PositionMarginY = Math.Max(0, source.PositionMarginY);
        mapped.PositionMonitorStableId = targetMonitor.StableId;
        mapped.PositionMonitorDeviceName = targetMonitor.DeviceName;
        mapped.PositionMonitorWasPrimary = targetMonitor.IsPrimary;
        mapped.PositionMonitorKey = CreateWorkAreaKey(targetMonitor);
        if (mapped.CompactPlacement is { } compact)
        {
            compact.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
            // The capsule is derived from its surface (spec 4.4): migration
            // writes the surface's target-monitor identity into the capsule
            // so a stale capsule reference can never resolve to the old
            // screen (defect B4).
            compact.PositionMonitorDeviceName = targetMonitor.DeviceName;
            compact.PositionMonitorStableId = mapped.PositionMonitorStableId;
            compact.PositionMonitorWasPrimary = targetMonitor.IsPrimary;
            compact.PositionMonitorKey = CreateWorkAreaKey(targetMonitor);
        }

        ResolvePhysicalPosition(
            mapped,
            source,
            sourceMonitor,
            targetMonitor,
            out usedFallbackPlacement);
        if (usedFallbackPlacement)
        {
            // Shelter geometry, not user intent. SeedSurfaceEntry also
            // consumes the flag so its own authority assignment cannot
            // resurrect the fallback as authoritative intent.
            mapped.IsAuthoritative = false;
            mapped.AuthoredAtUtc = null;
        }

        return mapped;
    }

    private static void ResolvePhysicalPosition(
        WidgetSurfaceLayoutProfile mapped,
        WidgetSurfaceLayoutProfile source,
        WidgetTopologyMonitorProfile? sourceMonitor,
        WidgetTopologyMonitorProfile targetMonitor,
        out bool usedFallbackPlacement)
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
        usedFallbackPlacement = false;
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
            // Shelter geometry, not user intent: this branch fires when the
            // source carries neither a valid anchor nor a source monitor.
            // The caller must strip authority so the fallback can never
            // outrank a later deliberate placement (2026-10-20 lineage).
            x = targetMonitor.WorkAreaX + 32;
            y = targetMonitor.WorkAreaY + 32;
            usedFallbackPlacement = true;
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
        WidgetTopologyMonitorProfile? monitor = SelectSourceMonitor(layout, monitors);
        layout.PositionMonitorStableId = monitor?.StableId;
        layout.Reference = DeriveReference(layout, monitor);
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
        WidgetTopologyMonitorProfile? monitor = SelectSourceMonitor(layout, monitors);
        layout.PositionMonitorStableId = monitor?.StableId;
        layout.Reference = DeriveReference(layout, monitor);
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
        if (ResolveRealizationMonitor(entry, monitors, logAmbiguity: true) is not { } monitor)
        {
            return;
        }

        double scale = NormalizeScale(monitor.DpiScale);
        int width = Math.Max(1, (int)Math.Round(Math.Max(WidgetElasticLayout.AbsoluteMinWidgetSizeDips, entry.Width) * scale));
        int height = Math.Max(1, (int)Math.Round(Math.Max(WidgetElasticLayout.AbsoluteMinWidgetSizeDips, entry.Height) * scale));
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
        entry.Width = Math.Max(WidgetElasticLayout.AbsoluteMinWidgetSizeDips, entry.Width);
        entry.Height = Math.Max(WidgetElasticLayout.AbsoluteMinWidgetSizeDips, entry.Height);
    }

    private static WidgetTopologyMonitorProfile? ResolveRealizationMonitor(
        WidgetSurfaceLayoutProfile entry,
        IReadOnlyList<WidgetTopologyMonitorProfile> monitors,
        bool logAmbiguity = false)
    {
        if (monitors.Count == 0)
        {
            return null;
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
        if (tokenMatches.Count <= 1)
        {
            return tokenMatches.FirstOrDefault() ??
                monitors.FirstOrDefault(candidate => candidate.IsPrimary) ??
                monitors[0];
        }

        // Same-spec degenerate monitors share one geo token: prefer the
        // candidate whose alias still matches the entry's device-name
        // hint, then the shared fallback chain.
        if (logAmbiguity)
        {
            App.Log(
                $"[DisplayTopology] Monitor token {token} is ambiguous across " +
                $"{tokenMatches.Count} monitors");
        }

        WidgetTopologyMonitorProfile? monitor = DisplayPlacementResolver.IsDegenerateIdentity(entry.PositionMonitorStableId)
            ? tokenMatches.FirstOrDefault(candidate => string.Equals(
                candidate.DeviceName,
                entry.PositionMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase))
            : tokenMatches[0];
        return monitor ??
            monitors.FirstOrDefault(candidate => candidate.IsPrimary) ??
            monitors[0];
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
            CompactWidth = source.CompactWidth,
            Reference = source.Reference?.Clone()
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
