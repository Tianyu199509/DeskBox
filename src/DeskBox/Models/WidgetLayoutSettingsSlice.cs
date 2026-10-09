namespace DeskBox.Models;

/// <summary>
/// Device-domain widget inventory: widget configs, groups, per-topology layouts, and deletion tombstones.
/// </summary>
public sealed class WidgetLayoutSettingsSlice
{
    /// <summary>
    /// Enabled state for singleton feature widgets, keyed by <see cref="WidgetKind"/> name.
    /// Legacy boolean properties are still kept as compatibility mirrors.
    /// </summary>
    public Dictionary<string, bool> FeatureWidgetEnabledStates { get; set; } = [];

    /// <summary>All configured widgets.</summary>
    public List<WidgetConfig> Widgets { get; set; } = [];

    /// <summary>
    /// Groups that present several normal widget configs through one desktop
    /// surface. Widget data stays in <see cref="Widgets"/>; this collection owns
    /// group membership, active member, and shared window state.
    /// </summary>
    public List<WidgetGroupConfig> WidgetGroups { get; set; } = [];

    /// <summary>
    /// Bounded layout snapshots keyed by the connected-display topology. This
    /// prevents a temporary DPI/topology transition from overwriting the layout
    /// that belongs to another monitor arrangement.
    /// </summary>
    public Dictionary<string, WidgetTopologyLayoutProfile> WidgetTopologyLayouts { get; set; } = [];

    /// <summary>The topology profile currently projected into Widgets/WidgetGroups.</summary>
    public string? ActiveWidgetTopologyKey { get; set; }

    /// <summary>
    /// Legacy compatibility flag. Widget grouping is now always available;
    /// normalization keeps this value true for older settings files.
    /// </summary>
    public bool WidgetGroupsEnabled { get; set; } = true;

    /// <summary>
    /// Legacy navigation style retained only so pre-title-switcher settings can
    /// be migrated without losing user intent.
    /// </summary>
    public string WidgetGroupDefaultNavigationStyle { get; set; } = "Tabs";

    /// <summary>
    /// Default identity layout used by the title-bar member selector.
    /// </summary>
    public string WidgetGroupDefaultTitleDisplayMode { get; set; } =
        WidgetGroupTitleDisplayModes.IconAndText;

    /// <summary>
    /// Enables sequential member switching while the pointer is over the
    /// title-bar member selector.
    /// </summary>
    public bool WidgetGroupWheelSwitchEnabled { get; set; } = true;

    /// <summary>
    /// Enables delayed pointer-hover activation for flat group title tabs.
    /// Individual groups may override this default.
    /// </summary>
    public bool WidgetGroupHoverSwitchEnabled { get; set; }

    /// <summary>
    /// Member-switch transition style for widget groups
    /// (<see cref="WidgetGroupSwitchAnimationStyles"/>). Auto follows each
    /// group's resolved navigation style at switch time.
    /// </summary>
    public string WidgetGroupSwitchAnimationStyle { get; set; } =
        WidgetGroupSwitchAnimationStyles.Auto;

    /// <summary>Widget ids that were deleted and should not be restored.</summary>
    public List<string> DeletedWidgetIds { get; set; } = [];

    /// <summary>
    /// Stable monitor identity chosen with "设为格子主屏幕". New widgets pin to
    /// this monitor instead of the cursor screen; null keeps the legacy
    /// cursor-based first placement.
    /// </summary>
    public string? WidgetDefaultBoundScreenId { get; set; }

    /// <summary>
    /// "新格子出现在"（spec D6/4.5）：CursorDisplay（默认）/ MainDisplay /
    /// SpecificDisplay（目标为 <see cref="WidgetDefaultBoundScreenId"/>）。
    /// 只决定创建落点，新格子归属 = 实际创建所在屏，不隐式固定。
    /// </summary>
    public string WidgetNewPlacementTarget { get; set; } = "CursorDisplay";

    /// <summary>
    /// "显示器断开时"（spec D3/4.5）：MoveToRemaining（默认，把它的格子移到
    /// 其他显示器）/ CollapseToCapsule（收起为胶囊，重连后自动展开）。
    /// </summary>
    public string WidgetDisplayDisconnectBehavior { get; set; } = "MoveToRemaining";

    /// <summary>
    /// Replaces every member with <paramref name="other"/>'s values. The
    /// layout store keeps this slice as the live object for the session
    /// (AppSettings.WidgetLayout is get-only by the 2A facade contract), so
    /// adoption copies data in place instead of swapping the reference.
    /// </summary>
    internal void CopyFrom(WidgetLayoutSettingsSlice other)
    {
        ArgumentNullException.ThrowIfNull(other);

        FeatureWidgetEnabledStates = other.FeatureWidgetEnabledStates;
        Widgets = other.Widgets;
        WidgetGroups = other.WidgetGroups;
        WidgetTopologyLayouts = other.WidgetTopologyLayouts;
        ActiveWidgetTopologyKey = other.ActiveWidgetTopologyKey;
        WidgetGroupsEnabled = other.WidgetGroupsEnabled;
        WidgetGroupDefaultNavigationStyle = other.WidgetGroupDefaultNavigationStyle;
        WidgetGroupDefaultTitleDisplayMode = other.WidgetGroupDefaultTitleDisplayMode;
        WidgetGroupWheelSwitchEnabled = other.WidgetGroupWheelSwitchEnabled;
        WidgetGroupHoverSwitchEnabled = other.WidgetGroupHoverSwitchEnabled;
        WidgetGroupSwitchAnimationStyle = other.WidgetGroupSwitchAnimationStyle;
        DeletedWidgetIds = other.DeletedWidgetIds;
        WidgetDefaultBoundScreenId = other.WidgetDefaultBoundScreenId;
        WidgetNewPlacementTarget = other.WidgetNewPlacementTarget;
        WidgetDisplayDisconnectBehavior = other.WidgetDisplayDisconnectBehavior;
    }
}
