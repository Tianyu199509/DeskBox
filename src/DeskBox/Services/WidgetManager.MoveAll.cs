using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Undo snapshot for "把所有格子移到这里" (spec 5.9): every surface's mode,
/// home id, geometry fields, and deep copy of its active-profile entry.
/// </summary>
public sealed class WidgetMoveAllUndoToken
{
    internal sealed record SurfaceSnapshot(
        string SurfaceId,
        string ApplyWidgetId,
        WidgetScreenBindingMode Mode,
        string? BoundScreenId,
        string? BoundScreenLabel,
        string? DisconnectCollapsedForScreenId,
        double X, double Y,
        string? Anchor,
        double MarginX, double MarginY,
        string? MonitorKey,
        string? MonitorDeviceName,
        string? MonitorStableId,
        bool? MonitorWasPrimary,
        double Width, double Height);

    internal List<SurfaceSnapshot> Surfaces { get; } = [];
    internal Dictionary<string, WidgetSurfaceLayoutProfile>? ActiveProfileEntries { get; set; }
    internal string? ActiveProfileKey { get; set; }

    /// <summary>
    /// Placement generation captured when the move-all ran (H3): undo refuses
    /// to restore once any later placement/binding change invalidated it.
    /// </summary>
    internal long PlacementVersion { get; set; }

    /// <summary>
    /// Whether the batch's single trailing persistence (one physical save for
    /// the whole move) succeeded. False means every in-memory binding is
    /// applied and undo still works, but nothing reached disk — callers
    /// surface that as an error without discarding the batch result.
    /// </summary>
    internal bool Persisted { get; set; }
}

public sealed partial class WidgetManager
{
    /// <summary>
    /// "把所有格子移到这里" (spec 5.9): one-shot move of every surface
    /// (widgets, groups, hidden, lazy, FollowPrimary) to the named display,
    /// with an undo token restoring mode/home/geometry/entries verbatim.
    /// Returns the moved count and the token.
    /// </summary>
    public async Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
        string displayStableId)
    {
        if (string.IsNullOrWhiteSpace(displayStableId))
        {
            return (0, new WidgetMoveAllUndoToken());
        }

        var token = new WidgetMoveAllUndoToken
        {
            PlacementVersion = _placementGeneration,
        };
        var settings = _settingsService.Settings;

        // 1. Snapshot: surface fields + deep copy of active-profile entries.
        string? activeKey = settings.ActiveWidgetTopologyKey;
        token.ActiveProfileKey = activeKey;
        if (!string.IsNullOrWhiteSpace(activeKey) &&
            settings.WidgetTopologyLayouts.TryGetValue(activeKey, out var activeProfile))
        {
            token.ActiveProfileEntries = activeProfile.Surfaces.ToDictionary(
                pair => pair.Key,
                pair => CloneLayoutShallow(pair.Value),
                StringComparer.Ordinal);
        }

        foreach ((string surfaceId, WidgetConfig config, WidgetGroupConfig? group) in EnumerateLayoutSurfaces(settings))
        {
            token.Surfaces.Add(new WidgetMoveAllUndoToken.SurfaceSnapshot(
                surfaceId,
                config.Id,
                group?.ScreenBindingMode ?? config.ScreenBindingMode,
                group?.BoundScreenId ?? config.BoundScreenId,
                group?.BoundScreenLabel ?? config.BoundScreenLabel,
                group?.DisconnectCollapsedForScreenId ?? config.DisconnectCollapsedForScreenId,
                group?.X ?? config.X,
                group?.Y ?? config.Y,
                group?.PositionAnchor ?? config.PositionAnchor,
                group?.PositionMarginX ?? config.PositionMarginX,
                group?.PositionMarginY ?? config.PositionMarginY,
                group?.PositionMonitorKey ?? config.PositionMonitorKey,
                group?.PositionMonitorDeviceName ?? config.PositionMonitorDeviceName,
                group?.PositionMonitorStableId ?? config.PositionMonitorStableId,
                group?.PositionMonitorWasPrimary ?? config.PositionMonitorWasPrimary,
                group?.Width ?? config.Width,
                group?.Height ?? config.Height));
        }

        // 2. Move every surface onto the display. Each binding is applied to
        // memory and live windows only; the batch persists once below.
        int moved = 0;
        foreach (WidgetMoveAllUndoToken.SurfaceSnapshot snapshot in token.Surfaces)
        {
            await ApplyScreenBindingAsync(
                snapshot.ApplyWidgetId,
                WidgetScreenBindingMode.Pinned,
                displayStableId,
                persistImmediately: false);
            moved++;
        }

        // H3: the move itself advanced the generation once per changed
        // surface; re-baseline so only LATER placement changes invalidate
        // the token. The batch runs on the UI thread with the triggering
        // button disabled, so a user drag interleaving between surfaces is
        // accepted as part of the move.
        token.PlacementVersion = _placementGeneration;

        // 3. Single transactional persistence for the whole batch (was: one
        // save per surface inside ApplyScreenBindingAsync → N physical saves
        // and N SettingsChanged notifications). The checked save reports
        // whether the batch actually reached disk; on failure the in-memory
        // bindings above are still applied and the undo token stays valid.
        token.Persisted = await _settingsService.SaveCheckedAsync();

        return (moved, token);
    }

    /// <summary>
    /// Restores every snapshot field and the active-profile entries, then
    /// recomputes actual positions for all loaded windows (spec 5.9).
    /// Returns false without touching anything when a placement change after
    /// the move invalidated the token (H3).
    /// </summary>
    public async Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.PlacementVersion != _placementGeneration)
        {
            return false;
        }

        var settings = _settingsService.Settings;

        foreach (WidgetMoveAllUndoToken.SurfaceSnapshot snapshot in token.Surfaces)
        {
            WidgetConfig? config = FindConfig(snapshot.ApplyWidgetId);
            if (config is null)
            {
                continue;
            }

            WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, config.Id);
            if (group is not null)
            {
                group.ScreenBindingMode = snapshot.Mode;
                group.BoundScreenId = snapshot.BoundScreenId;
                group.BoundScreenLabel = snapshot.BoundScreenLabel;
                group.DisconnectCollapsedForScreenId = snapshot.DisconnectCollapsedForScreenId;
                group.X = snapshot.X;
                group.Y = snapshot.Y;
                group.PositionAnchor = snapshot.Anchor;
                group.PositionMarginX = snapshot.MarginX;
                group.PositionMarginY = snapshot.MarginY;
                group.PositionMonitorKey = snapshot.MonitorKey;
                group.PositionMonitorDeviceName = snapshot.MonitorDeviceName;
                group.PositionMonitorStableId = snapshot.MonitorStableId;
                group.PositionMonitorWasPrimary = snapshot.MonitorWasPrimary;
                group.Width = snapshot.Width;
                group.Height = snapshot.Height;
            }

            config.ScreenBindingMode = snapshot.Mode;
            config.BoundScreenId = snapshot.BoundScreenId;
            config.BoundScreenLabel = snapshot.BoundScreenLabel;
            config.DisconnectCollapsedForScreenId = snapshot.DisconnectCollapsedForScreenId;
            config.X = snapshot.X;
            config.Y = snapshot.Y;
            config.PositionAnchor = snapshot.Anchor;
            config.PositionMarginX = snapshot.MarginX;
            config.PositionMarginY = snapshot.MarginY;
            config.PositionMonitorKey = snapshot.MonitorKey;
            config.PositionMonitorDeviceName = snapshot.MonitorDeviceName;
            config.PositionMonitorStableId = snapshot.MonitorStableId;
            config.PositionMonitorWasPrimary = snapshot.MonitorWasPrimary;
            config.Width = snapshot.Width;
            config.Height = snapshot.Height;
            _settingsService.UpdateWidget(config, notifySubscribers: false);
        }

        // Restore the active-profile entries last (deep copies).
        if (token.ActiveProfileKey is not null &&
            settings.WidgetTopologyLayouts.TryGetValue(token.ActiveProfileKey, out var profile))
        {
            foreach ((string surfaceId, WidgetSurfaceLayoutProfile entry) in
                     token.ActiveProfileEntries ?? [])
            {
                profile.Surfaces[surfaceId] = CloneLayoutShallow(entry);
            }
        }

        await _settingsService.SaveAsync();
        RepositionAllLoadedWindows();
        NotifyScreenHomeChangedAction?.Invoke();
        return true;
    }

    /// <summary>
    /// Single-surface move (spec 5.10) — semantic wrapper over
    /// ApplyScreenBindingAsync for menu/settings callers.
    /// </summary>
    public Task MoveSurfaceToDisplayAsync(string widgetId, string displayStableId) =>
        ApplyScreenBindingAsync(widgetId, WidgetScreenBindingMode.Pinned, displayStableId);

    /// <summary>
    /// "始终在主显示器" toggle (spec 5.10): on → FollowPrimary; off → Pinned to
    /// the display the surface currently sits on (no move).
    /// </summary>
    public async Task SetFollowPrimaryAsync(string widgetId, bool on)
    {
        if (on)
        {
            await ApplyScreenBindingAsync(widgetId, WidgetScreenBindingMode.FollowPrimary, null);
            return;
        }

        if (FindConfig(widgetId) is not { } config)
        {
            return;
        }

        var screens = WidgetScreenCatalog.Capture();
        Windows.Graphics.PointInt32 point = new(
            (int)Math.Round(config.X),
            (int)Math.Round(config.Y));
        WidgetScreenInfo? current = WidgetScreenCatalog.FindScreenForPoint(screens, point);
        await ApplyScreenBindingAsync(
            widgetId,
            WidgetScreenBindingMode.Pinned,
            current?.StableId);
    }

    /// <summary>
    /// Collapse-on-disconnect (spec 5.7): when the setting is
    /// CollapseToCapsule and a surface's home display left the new topology,
    /// collapse it non-persistently and stamp the marker; when the home
    /// returns and the surface is still system-collapsed, expand it.
    /// Called at the end of every topology activation.
    /// </summary>
    public void ApplyDisconnectCollapsePolicy(IReadOnlyCollection<string> currentDisplayIds)
    {
        if (!string.Equals(
                SettingsService.NormalizeWidgetDisplayDisconnectBehavior(
                    _settingsService.Settings.WidgetDisplayDisconnectBehavior),
                SettingsService.WidgetDisplayDisconnectCollapseToCapsule))
        {
            return;
        }

        var settings = _settingsService.Settings;
        bool HomeOnline(string? homeId) =>
            homeId is not null &&
            !DisplayPlacementResolver.IsDegenerateIdentity(homeId) &&
            currentDisplayIds.Any(id => string.Equals(id.Trim(), homeId.Trim(), StringComparison.OrdinalIgnoreCase));

        // Materialize first: SetSurfaceCollapsed updates the enumerated
        // widgets (UpdateWidget) and triggers debounced saves whose background
        // pass can normalize the Widgets list — a lazy enumeration would race
        // those mutations.
        var surfaces = EnumerateLayoutSurfaces(settings).ToList();
        foreach ((string _, WidgetConfig config, WidgetGroupConfig? group) in surfaces)
        {
            string? homeId = group?.BoundScreenId ?? config.BoundScreenId;
            string? marker = group?.DisconnectCollapsedForScreenId ?? config.DisconnectCollapsedForScreenId;
            bool visible = group?.IsVisible ?? config.IsVisible;
            bool collapsed = group?.IsCollapsed ?? config.IsCollapsed;

            if (marker is not null)
            {
                if (!HomeOnline(marker))
                {
                    continue;
                }

                // Home display returned (S23): a surface still system-collapsed
                // expands again; one the user already expanded manually just
                // clears its stale marker.
                SetSurfaceCollapsed(config, group, collapsed: false, marker: null);
            }
            else if (!collapsed && visible && !HomeOnline(homeId) && HomeIdUsable(homeId))
            {
                // Home display just left: collapse non-persistently.
                SetSurfaceCollapsed(config, group, collapsed: true, marker: homeId);
            }
        }
    }

    /// <summary>
    /// Clears a disconnect-collapse marker after a MANUAL expand/collapse
    /// (spec 5.7 / S23): once the user touched the surface during the
    /// disconnect, reconnect must not auto-expand it. Mirrors the group
    /// resolution of <see cref="SetSurfaceCollapsed"/>; persistence is left
    /// to the caller's save.
    /// </summary>
    public void ClearDisconnectCollapseMarker(WidgetConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var settings = _settingsService.Settings;
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, config.Id);
        if (group is null)
        {
            if (config.DisconnectCollapsedForScreenId is not null)
            {
                config.DisconnectCollapsedForScreenId = null;
                _settingsService.UpdateWidget(config, notifySubscribers: false);
            }

            return;
        }

        if (group.DisconnectCollapsedForScreenId is not null)
        {
            group.DisconnectCollapsedForScreenId = null;
        }

        foreach (string memberId in group.MemberIds)
        {
            if (FindConfig(memberId) is { } member &&
                member.DisconnectCollapsedForScreenId is not null)
            {
                member.DisconnectCollapsedForScreenId = null;
                _settingsService.UpdateWidget(member, notifySubscribers: false);
            }
        }
    }

    private void SetSurfaceCollapsed(
        WidgetConfig config,
        WidgetGroupConfig? group,
        bool collapsed,
        string? marker)
    {
        if (group is not null)
        {
            group.IsCollapsed = collapsed;
            group.DisconnectCollapsedForScreenId = marker;
            foreach (string memberId in group.MemberIds)
            {
                if (FindConfig(memberId) is { } member)
                {
                    member.IsCollapsed = collapsed;
                    member.DisconnectCollapsedForScreenId = marker;
                    _settingsService.UpdateWidget(member, notifySubscribers: false);
                }
            }
        }
        else
        {
            config.IsCollapsed = collapsed;
            config.DisconnectCollapsedForScreenId = marker;
            _settingsService.UpdateWidget(config, notifySubscribers: false);
        }

        // Resolve the owning group once (L7): the host window's identity may
        // be any member of the surface, not just the representative config.
        WidgetGroupConfig? ownerGroup = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);

        // Apply to the live window when present (H4): drive the real host
        // window into the requested compact state so a disconnect collapse —
        // and the expand when the home display returns — is visible at once.
        foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
        {
            if (window.Identity.WidgetId is not { } id ||
                (!string.Equals(id, config.Id, StringComparison.Ordinal) &&
                 (ownerGroup is null || !ownerGroup.MemberIds.Contains(id))))
            {
                continue;
            }

            try
            {
                window.RequestCompactState(collapsed, animate: false);
            }
            catch (Exception ex)
            {
                App.Log($"[ScreenHome] Disconnect-collapse window state request failed: {ex.Message}");
            }

            try
            {
                window.RestoreBoundsForCurrentTopology();
            }
            catch (Exception ex)
            {
                App.Log($"[ScreenHome] Disconnect-collapse reposition failed: {ex.Message}");
            }

            break;
        }
    }

    private static bool HomeIdUsable(string? homeId) =>
        !string.IsNullOrWhiteSpace(homeId) &&
        !DisplayPlacementResolver.IsDegenerateIdentity(homeId);

    private void RepositionAllLoadedWindows()
    {
        long generation = DateTimeOffset.UtcNow.UtcTicks;
        IReadOnlyList<IDesktopWidgetWindow> windows = GetLoadedDesktopWindows();
        foreach (IDesktopWidgetWindow window in windows)
        {
            window.BeginDisplayTopologyTransition(generation);
        }

        try
        {
            foreach (IDesktopWidgetWindow window in windows)
            {
                try
                {
                    window.RestoreBoundsForCurrentTopology();
                }
                catch (Exception ex)
                {
                    App.Log($"[MoveAll] Undo reposition failed for '{window.Identity.WidgetId}': {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (IDesktopWidgetWindow window in windows)
            {
                window.EndDisplayTopologyTransition(generation);
            }
        }
    }

    internal IEnumerable<(string SurfaceId, WidgetConfig Config, WidgetGroupConfig? Group)> EnumerateLayoutSurfaces(
        AppSettings settings)
    {
        var grouped = new HashSet<string>(
            settings.WidgetGroups.SelectMany(group => group.MemberIds),
            StringComparer.Ordinal);
        foreach (WidgetConfig widget in settings.Widgets)
        {
            if (widget.IsDisabled ||
                settings.DeletedWidgetIds.Contains(widget.Id) ||
                grouped.Contains(widget.Id))
            {
                continue;
            }

            yield return (widget.Id, widget, null);
        }

        foreach (WidgetGroupConfig group in settings.WidgetGroups)
        {
            WidgetConfig? representative = settings.Widgets.FirstOrDefault(widget =>
                group.MemberIds.Contains(widget.Id, StringComparer.Ordinal));
            if (representative is not null)
            {
                yield return (group.SurfaceId, representative, group);
            }
        }
    }

    private static WidgetSurfaceLayoutProfile CloneLayoutShallow(WidgetSurfaceLayoutProfile source) => new()
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
        // Deep copy: WidgetCompactPlacement is a mutable class, and the
        // snapshot must not alias the live entry (later capsule drags would
        // otherwise mutate the undo token).
        CompactPlacement = source.CompactPlacement is { } compact
            ? new WidgetCompactPlacement
            {
                X = compact.X,
                Y = compact.Y,
                PositionAnchor = compact.PositionAnchor,
                PositionMarginX = compact.PositionMarginX,
                PositionMarginY = compact.PositionMarginY,
                PositionMonitorKey = compact.PositionMonitorKey,
                PositionMonitorDeviceName = compact.PositionMonitorDeviceName,
                PositionMonitorStableId = compact.PositionMonitorStableId,
                PositionMonitorWasPrimary = compact.PositionMonitorWasPrimary,
                BoundsCoordinateVersion = compact.BoundsCoordinateVersion,
            }
            : null,
        CompactWidth = source.CompactWidth
    };
}
