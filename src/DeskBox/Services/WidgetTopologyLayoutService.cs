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
    private const string CurrentTopologyKeyPrefix = "v3-";
    private const uint EddGetDeviceInterfaceName = 0x00000001;

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
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, member.Id);
        if (group is not null)
        {
            string surfaceId = ResolveGroupSurfaceId(group);
            profile.Surfaces[surfaceId] = CaptureGroupLayout(group, profile.Monitors);
        }
        else
        {
            profile.Surfaces[member.Id] = CaptureWidgetLayout(member, profile.Monitors);
        }

        return true;
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
                CaptureAllSurfaces(settings, targetProfile);
            }
            else
            {
                SeedProfile(settings, sourceProfile, targetProfile);
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
        changed |= PruneProfiles(settings, topology.Key);
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
        string signature = CreateTopologySignature(monitors);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
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
                .Select(monitor =>
                    FormattableString.Invariant(
                        $"{NormalizeStableIdentityForKey(monitor.StableId)};{monitor.IsPrimary};{NormalizeScale(monitor.DpiScale):F3};{monitor.MonitorX},{monitor.MonitorY},{monitor.MonitorWidth},{monitor.MonitorHeight}")));
        return signature;
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
        foreach ((string surfaceId, WidgetSurfaceLayoutProfile layout) in profile.Surfaces.ToList())
        {
            profile.Surfaces[surfaceId] = MapToTopology(
                layout,
                sourceMonitors,
                targetMonitors);
        }
    }

    private static void SeedProfile(
        AppSettings settings,
        WidgetTopologyLayoutProfile? sourceProfile,
        WidgetTopologyLayoutProfile targetProfile)
    {
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateSurfaces(settings))
        {
            WidgetSurfaceLayoutProfile current = group is null
                ? CaptureWidgetLayout(config, sourceProfile?.Monitors ?? targetProfile.Monitors)
                : CaptureGroupLayout(group, sourceProfile?.Monitors ?? targetProfile.Monitors);
            WidgetSurfaceLayoutProfile source = sourceProfile?.Surfaces.TryGetValue(surfaceId, out WidgetSurfaceLayoutProfile? saved) == true
                ? saved
                : current;
            targetProfile.Surfaces[surfaceId] = MapToTopology(
                source,
                sourceProfile?.Monitors ?? targetProfile.Monitors,
                targetProfile.Monitors);
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

            WidgetSurfaceLayoutProfile current = group is null
                ? CaptureWidgetLayout(config, sourceProfile?.Monitors ?? targetProfile.Monitors)
                : CaptureGroupLayout(group, sourceProfile?.Monitors ?? targetProfile.Monitors);
            WidgetSurfaceLayoutProfile source = sourceProfile?.Surfaces.TryGetValue(surfaceId, out WidgetSurfaceLayoutProfile? saved) == true
                ? saved
                : current;
            targetProfile.Surfaces[surfaceId] = MapToTopology(
                source,
                sourceProfile?.Monitors ?? targetProfile.Monitors,
                targetProfile.Monitors);
            changed = true;
        }

        return changed;
    }

    private static void CaptureAllSurfaces(AppSettings settings, WidgetTopologyLayoutProfile profile)
    {
        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateSurfaces(settings))
        {
            profile.Surfaces[surfaceId] = group is null
                ? CaptureWidgetLayout(config, profile.Monitors)
                : CaptureGroupLayout(group, profile.Monitors);
        }

        profile.LastUsedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void ApplyProfile(AppSettings settings, WidgetTopologyLayoutProfile profile)
    {
        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            if (!profile.Surfaces.TryGetValue(
                    ResolveGroupSurfaceId(group),
                    out WidgetSurfaceLayoutProfile? layout))
            {
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

        HashSet<string> groupedMemberIds = settings.WidgetGroups
            .SelectMany(group => group.MemberIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (!groupedMemberIds.Contains(widget.Id) &&
                profile.Surfaces.TryGetValue(widget.Id, out WidgetSurfaceLayoutProfile? layout))
            {
                ApplyToWidget(widget, layout);
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
            compact.PositionMonitorDeviceName = targetMonitor.DeviceName;
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
            BoundsCoordinateVersion = group.BoundsCoordinateVersion,
            Width = group.Width,
            Height = group.Height,
            CompactPlacement = CloneCompactPlacement(group.CompactPlacement),
            CompactWidth = group.CompactWidth
        };
        layout.PositionMonitorStableId = SelectSourceMonitor(layout, monitors)?.StableId;
        return layout;
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
        if (!string.IsNullOrWhiteSpace(layout.PositionMonitorStableId))
        {
            WidgetTopologyMonitorProfile? stable = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.StableId, layout.PositionMonitorStableId, StringComparison.OrdinalIgnoreCase));
            if (stable is not null)
            {
                return stable;
            }
        }

        if (!string.IsNullOrWhiteSpace(layout.PositionMonitorDeviceName))
        {
            WidgetTopologyMonitorProfile? device = monitors.FirstOrDefault(monitor =>
                string.Equals(monitor.DeviceName, layout.PositionMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (device is not null)
            {
                return device;
            }
        }

        if (!string.IsNullOrWhiteSpace(layout.PositionMonitorKey))
        {
            WidgetTopologyMonitorProfile? keyed = monitors.FirstOrDefault(monitor =>
                string.Equals(CreateWorkAreaKey(monitor), layout.PositionMonitorKey, StringComparison.Ordinal));
            if (keyed is not null)
            {
                return keyed;
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
        if (sourceMonitor is not null)
        {
            WidgetTopologyMonitorProfile? stable = targets.FirstOrDefault(target =>
                MonitorIdentityEquals(sourceMonitor, target));
            if (stable is not null)
            {
                return stable;
            }

            WidgetTopologyMonitorProfile? samePlacement = targets.FirstOrDefault(target =>
                MonitorPlacementEquals(sourceMonitor, target));
            if (samePlacement is not null)
            {
                return samePlacement;
            }
        }

        if (layout.PositionMonitorWasPrimary == true || sourceMonitor?.IsPrimary == true)
        {
            WidgetTopologyMonitorProfile? primary = targets.FirstOrDefault(target => target.IsPrimary);
            if (primary is not null)
            {
                return primary;
            }
        }

        if (!string.IsNullOrWhiteSpace(layout.PositionMonitorDeviceName))
        {
            WidgetTopologyMonitorProfile? device = targets.FirstOrDefault(target =>
                string.Equals(target.DeviceName, layout.PositionMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (device is not null)
            {
                return device;
            }
        }

        return targets.FirstOrDefault(target => target.IsPrimary) ?? targets.FirstOrDefault();
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

    private static bool PruneProfiles(AppSettings settings, string activeKey)
    {
        bool changed = false;
        while (settings.WidgetTopologyLayouts.Count > MaximumRetainedProfiles)
        {
            string? oldest = settings.WidgetTopologyLayouts
                .Where(pair => !string.Equals(pair.Key, activeKey, StringComparison.Ordinal))
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
                PositionMonitorWasPrimary = source.PositionMonitorWasPrimary,
                BoundsCoordinateVersion = source.BoundsCoordinateVersion
            };

    private static string ResolveStableMonitorId(string? deviceName)
    {
        string fallback = string.IsNullOrWhiteSpace(deviceName) ? "unknown-display" : deviceName.Trim();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return fallback;
        }

        try
        {
            var displayDevice = new Win32Helper.DisplayDevice
            {
                Size = Marshal.SizeOf<Win32Helper.DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty
            };
            if (Win32Helper.EnumDisplayDevices(
                    deviceName,
                    0,
                    ref displayDevice,
                    EddGetDeviceInterfaceName))
            {
                if (!string.IsNullOrWhiteSpace(displayDevice.DeviceId))
                {
                    return displayDevice.DeviceId.Trim();
                }

                if (!string.IsNullOrWhiteSpace(displayDevice.DeviceKey))
                {
                    return displayDevice.DeviceKey.Trim();
                }
            }
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                MarshalDirectiveException or
                TypeLoadException)
        {
        }

        return fallback;
    }
}

internal sealed record WidgetDisplayTopologySnapshot(
    string Key,
    IReadOnlyList<WidgetTopologyMonitorProfile> Monitors);
