namespace DeskBox.Models;

/// <summary>
/// Desktop organization rules, auto-organization baseline, and the recent-organization history (local-domain data, still stored in settings.json).
/// </summary>
public sealed class DesktopOrganizationSettingsSlice
{
    /// <summary>
    /// Recent organization history used for undo and quick review.
    /// </summary>
    public List<OrganizationHistoryEntry> RecentOrganizationHistory { get; set; } = [];

    /// <summary>
    /// Stable per-widget routing rules used by desktop one-click and automatic
    /// organization. Rules target widget IDs and therefore survive renames and
    /// group membership changes.
    /// </summary>
    public List<DesktopOrganizationRule> DesktopOrganizationRules { get; set; } = [];

    /// <summary>
    /// Whether newly created, stable desktop files may be routed automatically.
    /// This is deliberately opt-in.
    /// </summary>
    public bool DesktopAutoOrganizationEnabled { get; set; }

    /// <summary>
    /// Baseline created when automatic organization is enabled. Existing
    /// desktop content is never processed merely by enabling the feature.
    /// </summary>
    public DateTimeOffset? DesktopAutoOrganizationBaselineUtc { get; set; }
}
