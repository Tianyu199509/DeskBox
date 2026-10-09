using DeskBox.Platform;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// One attached monitor normalized for user-facing surfaces: the widget
/// "belong to screen" menu, the settings display preview, and the identify
/// overlay all number and describe monitors through this catalog so the
/// numbers agree everywhere within a session.
/// </summary>
public sealed record WidgetScreenInfo(
    int Number,
    string StableId,
    string DeviceName,
    RectInt32 Monitor,
    RectInt32 WorkArea,
    bool IsPrimary,
    double DpiScale,
    string FriendlyName = "")
{
    public string PhysicalSizeText => $"{Monitor.Width}×{Monitor.Height}";

    public double EffectiveDpiScale => double.IsFinite(DpiScale) && DpiScale > 0 ? DpiScale : 1.0;

    /// <summary>
    /// The label used by menus and the settings page (spec 6.1): the CCD
    /// friendly name when available, resolution otherwise.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName)
        ? PhysicalSizeText
        : FriendlyName;
}

public static class WidgetScreenCatalog
{
    /// <summary>
    /// Captures the current monitors in enumeration order. Numbers are the
    /// 1-based enumeration index and are only meaningful together with the
    /// stable id; treat them as labels, not identities.
    /// </summary>
    public static IReadOnlyList<WidgetScreenInfo> Capture()
    {
        // CCD friendly names are keyed by the device interface path — the
        // same identity used for stable ids (spec 4.6).
        Dictionary<string, string> friendlyNames;
        try
        {
            friendlyNames = Win32Helper.QueryMonitorFriendlyNames();
        }
        catch
        {
            friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var screens = new List<WidgetScreenInfo>();
        int number = 1;
        foreach (Win32Helper.MonitorWorkAreaInfo monitor in Win32Helper.GetMonitorWorkAreaInfos())
        {
            string stableId = string.IsNullOrWhiteSpace(monitor.StableId)
                ? (monitor.DeviceName ?? string.Empty)
                : monitor.StableId;
            string friendlyName = friendlyNames.TryGetValue(stableId.Trim(), out string? name) && name is not null
                ? name
                : string.Empty;
            screens.Add(new WidgetScreenInfo(
                number++,
                stableId,
                monitor.DeviceName ?? string.Empty,
                new RectInt32(
                    monitor.Monitor.Left,
                    monitor.Monitor.Top,
                    monitor.Monitor.Right - monitor.Monitor.Left,
                    monitor.Monitor.Bottom - monitor.Monitor.Top),
                new RectInt32(
                    monitor.WorkArea.Left,
                    monitor.WorkArea.Top,
                    monitor.WorkArea.Right - monitor.WorkArea.Left,
                    monitor.WorkArea.Bottom - monitor.WorkArea.Top),
                monitor.IsPrimary,
                monitor.DpiScale,
                friendlyName));
        }

        return screens;
    }

    /// <summary>
    /// True when the persisted binding id still names an attached monitor.
    /// Ids that degenerated to the unstable <c>\\.\DISPLAYn</c> name never
    /// match, mirroring <c>WidgetPositioningService</c> resolution semantics.
    /// </summary>
    public static bool IsScreenAttached(IReadOnlyList<WidgetScreenInfo> screens, string? stableId)
    {
        return TryFindScreen(screens, stableId) is not null;
    }

    public static WidgetScreenInfo? TryFindScreen(IReadOnlyList<WidgetScreenInfo> screens, string? stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId) ||
            stableId.Trim().Equals("unknown-display", StringComparison.OrdinalIgnoreCase) ||
            stableId.Trim().StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (WidgetScreenInfo screen in screens)
        {
            if (string.Equals(screen.StableId.Trim(), stableId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }

    /// <summary>
    /// The screen whose work area contains the point, or the nearest one —
    /// used to place preview chips for widgets without an explicit binding.
    /// </summary>
    public static WidgetScreenInfo? FindScreenForPoint(
        IReadOnlyList<WidgetScreenInfo> screens,
        PointInt32 point)
    {
        foreach (WidgetScreenInfo screen in screens)
        {
            if (point.X >= screen.Monitor.X &&
                point.X < screen.Monitor.X + screen.Monitor.Width &&
                point.Y >= screen.Monitor.Y &&
                point.Y < screen.Monitor.Y + screen.Monitor.Height)
            {
                return screen;
            }
        }

        return screens.FirstOrDefault();
    }
}
