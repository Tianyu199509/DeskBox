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
    public string WidgetGroupDefaultNavigationStyle { get; set; } = "Stack";

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

    /// <summary>Widget ids that were deleted and should not be restored.</summary>
    public List<string> DeletedWidgetIds { get; set; } = [];
}
