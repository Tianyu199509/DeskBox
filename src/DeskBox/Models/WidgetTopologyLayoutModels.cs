using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// A bounded, per-display-topology snapshot of every independent widget surface.
/// The active <see cref="WidgetConfig"/> remains the runtime source of truth; these
/// profiles let that source switch atomically when monitors are connected or removed.
/// </summary>
public sealed class WidgetTopologyLayoutProfile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset LastUsedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<WidgetTopologyMonitorProfile> Monitors { get; set; } = [];

    public Dictionary<string, WidgetSurfaceLayoutProfile> Surfaces { get; set; } = [];
}

/// <summary>Monitor geometry and scale captured with a topology profile.</summary>
public sealed class WidgetTopologyMonitorProfile
{
    /// <summary>
    /// Best-effort PnP/display-interface identity. DeviceName is retained as a
    /// fallback because virtual and remote displays may not expose a PnP id.
    /// </summary>
    public string StableId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public bool IsPrimary { get; set; }

    public int MonitorX { get; set; }

    public int MonitorY { get; set; }

    public int MonitorWidth { get; set; }

    public int MonitorHeight { get; set; }

    public int WorkAreaX { get; set; }

    public int WorkAreaY { get; set; }

    public int WorkAreaWidth { get; set; }

    public int WorkAreaHeight { get; set; }

    public double DpiScale { get; set; } = 1;
}

/// <summary>
/// Geometry for one independent desktop surface. Group members share the group
/// surface id so switching the active tab cannot create competing layouts.
/// </summary>
public sealed class WidgetSurfaceLayoutProfile
{
    public string? PositionMonitorStableId { get; set; }

    /// <summary>
    /// Monitor binding intent carried through profiles unchanged. Binding is
    /// topology-independent user intent, so profile switches reposition but
    /// never rebind the surface.
    /// </summary>
    [JsonConverter(typeof(WidgetScreenBindingModeJsonConverter))]
    public WidgetScreenBindingMode ScreenBindingMode { get; set; } = WidgetScreenBindingMode.Unbound;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundScreenId { get; set; }

    /// <summary>
    /// True when this entry records a placement the user made by hand (or the
    /// initial layout captured on first run). Seeded/projected entries stay
    /// false; captures never promote, they only preserve an existing flag.
    /// Seeding prefers the newest authoritative entry for a surface.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsAuthoritative { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AuthoredAtUtc { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public string? PositionAnchor { get; set; }

    public double PositionMarginX { get; set; }

    public double PositionMarginY { get; set; }

    public string? PositionMonitorKey { get; set; }

    public string? PositionMonitorDeviceName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PositionMonitorWasPrimary { get; set; }

    public int BoundsCoordinateVersion { get; set; } = WidgetConfig.CurrentBoundsCoordinateVersion;

    public double Width { get; set; } = 300;

    public double Height { get; set; } = 400;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WidgetCompactPlacement? CompactPlacement { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CompactWidth { get; set; }
}
