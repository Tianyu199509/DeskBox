using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// How a user placement reached <see cref="WidgetManager.CommitUserPlacement"/>.
/// Drag-end is the unified commit for standalone widgets and group hosts
/// alike; the remaining sources are the specialized entry points from
/// spec 5.4.
/// </summary>
public enum WidgetPlacementSource
{
    Drag,
    CoordinatedMove,
    CapsuleBarDrag,
    Resize,
    MenuMove,
    MoveAll,
    External,
    Create,
    GroupMerge
}

public sealed partial class WidgetManager
{
    /// <summary>
    /// Commits a placement the user just made (spec 5.4): captures
    /// anchor/margins/size against the max-intersection display, marks the
    /// profile entry authoritative, and updates the surface's home display —
    /// "放哪属于哪". Group surfaces mirror mode/home to every member.
    /// </summary>
    public void CommitUserPlacement(
        WidgetConfig config,
        Windows.Graphics.RectInt32 contentBounds,
        Windows.Graphics.RectInt32 hostBounds,
        WidgetPlacementSource source,
        bool captureIntent = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();
        if (screens.Count == 0)
        {
            return;
        }

        // A committed placement invalidates outstanding move-all undo tokens
        // (H3): their snapshots no longer describe current placement intent.
        _placementGeneration++;

        WidgetScreenInfo display = FindDisplayByMaxIntersection(screens, hostBounds);
        var workArea = new Windows.Graphics.RectInt32(
            display.WorkArea.X,
            display.WorkArea.Y,
            display.WorkArea.Width,
            display.WorkArea.Height);

        // 1. Capture intent against the owning display's work area (content
        // bounds for the anchor, host bounds for the X/Y cache). Compact
        // placements skip this — the window already captured the capsule.
        if (captureIntent)
        {
            WidgetPositioningService.CaptureAnchor(config, contentBounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(config, hostBounds, workArea);
        }

        // 2. Home update ("放哪属于哪"), with the documented exceptions.
        string? previousHome = config.ScreenBindingMode == WidgetScreenBindingMode.Pinned
            ? config.BoundScreenId
            : null;
        ApplyHomeUpdate(config, display, screens);

        // Compact/capsule-source commits skipped the anchor capture above, so
        // the surface's last-position identity is synced here from the
        // placement display.
        if (!captureIntent)
        {
            config.PositionMonitorKey = WidgetPositioningService.CreateMonitorKey(workArea);
            config.PositionMonitorStableId = display.StableId;
            config.PositionMonitorDeviceName = display.DeviceName;
            config.PositionMonitorWasPrimary = display.IsPrimary;
        }

        // 3. Group surface: propagate mode/home onto the group, then reuse
        // the group sync chain (geometry mirror + member rebinding + save).
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(
            _settingsService.Settings,
            config.Id);
        if (group is not null)
        {
            group.ScreenBindingMode = config.ScreenBindingMode;
            group.BoundScreenId = config.BoundScreenId;
            SynchronizeGroupLayoutFromMember(config);
        }

        // 4. The active profile entry becomes authoritative (after the home
        // update so the captured entry reflects the final intent).
        MarkSurfaceEntryAuthoritative(config);

        // 5. Capsule display fields are derived from the surface (spec 4.4).
        if (config.CompactPlacement is { } placement)
        {
            placement.PositionMonitorKey = config.PositionMonitorKey;
            placement.PositionMonitorDeviceName = config.PositionMonitorDeviceName;
            placement.PositionMonitorStableId = config.PositionMonitorStableId;
            placement.PositionMonitorWasPrimary = config.PositionMonitorWasPrimary;
        }

        _settingsService.UpdateWidget(config, notifySubscribers: false);
        _settingsService.SaveDebounced(notifySubscribers: false);

        if (!string.Equals(previousHome, config.BoundScreenId, StringComparison.OrdinalIgnoreCase))
        {
            App.Log(
                $"[ScreenHome] surface={config.Id} from={previousHome ?? "-"} " +
                $"to={config.BoundScreenId ?? "-"} source={source}");
            // Only a real home change alters what the settings preview and
            // widget rows display; anchor-only commits raise nothing.
            NotifyScreenHomeChangedAction?.Invoke();
        }
    }

    /// <summary>
    /// Home-update rules from spec 5.4 step 3: remote sessions never rebind;
    /// degenerate display identities never become a home; a FollowPrimary
    /// surface dragged off the primary becomes Pinned to that display; a
    /// surface whose home is offline (fallback placement) keeps its home —
    /// moving it around on the fallback screen does not rebind.
    /// </summary>
    private void ApplyHomeUpdate(
        WidgetConfig config,
        WidgetScreenInfo display,
        IReadOnlyList<WidgetScreenInfo> screens)
    {
        if (Win32Helper.GetSystemMetrics(Win32Helper.SM_REMOTESESSION) != 0)
        {
            return;
        }

        if (DisplayPlacementResolver.IsDegenerateIdentity(display.StableId))
        {
            return;
        }

        if (config.ScreenBindingMode == WidgetScreenBindingMode.FollowPrimary)
        {
            if (!display.IsPrimary)
            {
                config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
                config.BoundScreenId = display.StableId;
                config.BoundScreenLabel = LabelFromDisplay(display);
            }

            return;
        }

        if (config.ScreenBindingMode != WidgetScreenBindingMode.Pinned ||
            string.IsNullOrWhiteSpace(config.BoundScreenId))
        {
            config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
            config.BoundScreenId = display.StableId;
            config.BoundScreenLabel = LabelFromDisplay(display);
            return;
        }

        // Existing home: move it only when the home is online (this drag is a
        // deliberate relocation) — never from a fallback placement.
        if (string.Equals(
                config.BoundScreenId.Trim(),
                display.StableId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (WidgetScreenCatalog.TryFindScreen(screens, config.BoundScreenId) is not null)
        {
            config.BoundScreenId = display.StableId;
            config.BoundScreenLabel = LabelFromDisplay(display);
        }
    }

    /// <summary>
    /// Bind-time friendly-name snapshot for a home change (H1): empty CCD
    /// names degrade to null instead of persisting an empty label.
    /// </summary>
    private static string? LabelFromDisplay(WidgetScreenInfo display) =>
        string.IsNullOrEmpty(display.FriendlyName) ? null : display.FriendlyName;

    /// <summary>
    /// Marks the active profile's entry for this surface authoritative and
    /// stamps the authored time (spec 4.2). The entry's stored monitor
    /// identity is refreshed to the placement display so "条目屏" stays
    /// decidable even under degenerate-id environments.
    /// </summary>
    private void MarkSurfaceEntryAuthoritative(
        WidgetConfig config,
        string? stableIdOverride = null,
        string? deviceNameOverride = null)
    {
        var settings = _settingsService.Settings;
        string? activeKey = settings.ActiveWidgetTopologyKey;
        if (string.IsNullOrWhiteSpace(activeKey) ||
            !settings.WidgetTopologyLayouts.TryGetValue(activeKey, out var profile))
        {
            return;
        }

        // Refresh/create the entry from the just-committed config first
        // (no-op when the topology key is transiently mismatched), then
        // promote it. Capture never promotes on its own (spec 4.2), so the
        // promotion has to happen here.
        _topologyLayoutService.CaptureCurrentSurface(settings, config);
        WidgetGroupConfig? group = WidgetGroupSettings.FindByMember(settings, config.Id);
        string surfaceId = group is not null
            ? _topologyLayoutService.ResolveSurfaceIdForGroup(group)
            : config.Id;
        if (!profile.Surfaces.TryGetValue(surfaceId, out var entry) || entry is null)
        {
            return;
        }

        entry.IsAuthoritative = true;
        entry.AuthoredAtUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(stableIdOverride))
        {
            entry.PositionMonitorStableId = stableIdOverride;
        }

        if (!string.IsNullOrWhiteSpace(deviceNameOverride))
        {
            entry.PositionMonitorDeviceName = deviceNameOverride;
        }
    }

    /// <summary>
    /// The display owning the largest intersection with the bounds (spec 5.4
    /// step 1). WinUI's GetFromRect does not document its cross-screen
    /// tie-break, so the intersection comparison is explicit here.
    /// </summary>
    internal static WidgetScreenInfo FindDisplayByMaxIntersection(
        IReadOnlyList<WidgetScreenInfo> screens,
        Windows.Graphics.RectInt32 bounds)
    {
        WidgetScreenInfo best = screens[0];
        double bestArea = -1;
        double boundsArea = (double)bounds.Width * bounds.Height;
        foreach (WidgetScreenInfo screen in screens)
        {
            long left = Math.Max(bounds.X, screen.Monitor.X);
            long top = Math.Max(bounds.Y, screen.Monitor.Y);
            long right = Math.Min(bounds.X + bounds.Width, screen.Monitor.X + screen.Monitor.Width);
            long bottom = Math.Min(bounds.Y + bounds.Height, screen.Monitor.Y + screen.Monitor.Height);
            double area = right > left && bottom > top ? (right - left) * (double)(bottom - top) : 0;
            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
            else if (area == bestArea &&
                     area > 0 &&
                     boundsArea > 0 &&
                     screen.IsPrimary &&
                     !best.IsPrimary)
            {
                // Tie: prefer the primary display (mirrors how Windows
                // resolves ambiguous ownership for system UI).
                best = screen;
            }
        }

        if (bestArea <= 0)
        {
            // No intersection at all: nearest display by center distance.
            double cx = bounds.X + bounds.Width / 2.0;
            double cy = bounds.Y + bounds.Height / 2.0;
            double bestDistance = double.PositiveInfinity;
            foreach (WidgetScreenInfo screen in screens)
            {
                double dx = cx - (screen.Monitor.X + screen.Monitor.Width / 2.0);
                double dy = cy - (screen.Monitor.Y + screen.Monitor.Height / 2.0);
                double distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = screen;
                }
            }
        }

        return best;
    }
}
