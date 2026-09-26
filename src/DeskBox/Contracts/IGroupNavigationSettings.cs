namespace DeskBox.Contracts;

public readonly record struct GroupNavigationSettingsSnapshot(
    string DefaultNavigationStyle,
    string DefaultTitleDisplayMode,
    bool WheelSwitchEnabled,
    bool HoverSwitchEnabled);

/// <summary>
/// Settings-page writes for the group-navigation defaults: the default
/// navigation style and title display mode for widget groups, plus the
/// default wheel-switch and hover-switch toggles of the title-bar member
/// selector. The values live on the device-domain WidgetLayout slice and are
/// persisted through the regular paired layout+settings commit; the settings
/// shell keeps every XAML/AOT binding, the overview/existing-group
/// projections and the explicit host notification it runs after a real
/// change. Per-group overrides are not part of this port — they stay on the
/// shell's group editing methods, which mutate the group configs directly.
/// Every write keeps the section's original save semantics: normalize
/// (<see cref="DeskBox.Models.WidgetGroupNavigationStyles"/> /
/// <see cref="DeskBox.Models.WidgetGroupTitleDisplayModes"/> with
/// FollowDefault never allowed for the defaults), skip unchanged writes,
/// store, and schedule one debounced save; the write returns whether the
/// persisted value changed so the shell can keep firing its notifications
/// only on real changes. Group switching behavior itself stays outside this
/// port — it is a host-side consumer of the regular SettingsChanged broadcast
/// and the shell's explicit group-presentation notification, exactly as
/// before.
/// </summary>
public interface IGroupNavigationSettings
{
    GroupNavigationSettingsSnapshot ReadAll();

    bool SetDefaultNavigationStyle(string? value);

    bool SetDefaultTitleDisplayMode(string? value);

    bool SetWheelSwitchEnabled(bool value);

    bool SetHoverSwitchEnabled(bool value);
}
