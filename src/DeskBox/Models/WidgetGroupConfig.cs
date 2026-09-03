using System.Text.Json.Serialization;

namespace DeskBox.Models;

/// <summary>
/// Persisted identity and shared window state for a group of widgets.
/// Members remain normal <see cref="WidgetConfig"/> instances so their data and
/// settings survive grouping, reordering, and detaching.
/// </summary>
public sealed class WidgetGroupConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Stable identity of the desktop surface owned by this group. Unlike the
    /// active member id, this value does not change when group content changes.
    /// </summary>
    public string SurfaceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> MemberIds { get; set; } = [];

    public string ActiveMemberId { get; set; } = string.Empty;

    public bool IsVisible { get; set; } = true;

    public double X { get; set; } = 100;

    public double Y { get; set; } = 100;

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

    public bool IsPositionLocked { get; set; }

    public bool IsSizeLocked { get; set; }

    public bool IsAlwaysOnTop { get; set; }

    public bool IsCollapsed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WidgetCompactPlacement? CompactPlacement { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CompactWidth { get; set; }

    /// <summary>
    /// Title navigation layout. Stack/Auto use the compact combined selector;
    /// Tabs exposes every member as a flat title-bar tab.
    /// </summary>
    public string NavigationStyle { get; set; } =
        WidgetGroupNavigationStyles.FollowDefault;

    /// <summary>
    /// Group-level title identity layout. FollowDefault resolves through the
    /// current application setting.
    /// </summary>
    public string TitleDisplayMode { get; set; } =
        WidgetGroupTitleDisplayModes.FollowDefault;

    /// <summary>
    /// Optional group-level wheel override. Null follows the application
    /// default; click selection is always available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WheelSwitchEnabled { get; set; }

    /// <summary>
    /// Enables delayed pointer-hover activation for flat title tabs. This is
    /// deliberately opt-in to prevent accidental switches while moving across
    /// the title bar. Null follows the application default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HoverSwitchEnabled { get; set; }

    /// <summary>
    /// Concrete shared title presentation. Groups permit Standard or Compact
    /// only so the member selector is always visible.
    /// </summary>
    public string ChromeMode { get; set; } = "Standard";

    /// <summary>Shared expand/collapse behavior override. "System" keeps the app default.</summary>
    public string CollapseBehavior { get; set; } = "System";
}
