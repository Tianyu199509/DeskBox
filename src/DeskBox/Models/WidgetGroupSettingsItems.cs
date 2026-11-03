namespace DeskBox.Models;

/// <summary>
/// Projection record for one existing widget group in the settings section's
/// "existing groups" list. The shell's group-projection state machine builds
/// these; the group-navigation editor exposes them as a pushed binding
/// surface. Kept WinRT-bindable for the XAML item templates.
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record WidgetGroupSettingsItem(
    string GroupId,
    string? FirstMemberId,
    string DisplayName,
    string Summary,
    bool HasOverrides,
    string NavigationStyle,
    IReadOnlyList<SettingsOption> NavigationOptions,
    string TitleDisplayMode,
    IReadOnlyList<SettingsOption> TitleOptions,
    string WheelSetting,
    IReadOnlyList<SettingsOption> WheelOptions,
    string HoverSetting,
    IReadOnlyList<SettingsOption> HoverOptions,
    string CollapseBehavior,
    IReadOnlyList<SettingsOption> CollapseOptions,
    string ChromeMode,
    IReadOnlyList<SettingsOption> ChromeOptions,
    IReadOnlyList<WidgetGroupMemberSettingsItem> Members,
    string RawName,
    bool IsHoverSwitchApplicable);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record WidgetGroupMemberSettingsItem(
    string GroupId,
    string WidgetId,
    string DisplayName,
    string? MoveUpTargetWidgetId,
    string? MoveDownTargetWidgetId)
{
    public bool CanMoveUp => MoveUpTargetWidgetId is not null;
    public bool CanMoveDown => MoveDownTargetWidgetId is not null;
}
