namespace DeskBox.Contracts;

/// <summary>
/// The settings-UI-authored widget-layout writes (batch R2). The host is the
/// only editor of these fields, but the MAIN process owns the layout
/// domain — snapshots never carry it — so each edit rides its own command.
/// Payloads are per-target snapshots (idempotent re-apply): Unset/null
/// fields are left untouched, so a reset sends explicit values for all.
/// </summary>
public sealed record GroupPresentationChange(
    string GroupId,
    string? NavigationStyle = null,
    string? TitleDisplayMode = null,
    GroupPresentationTriState WheelSwitch = GroupPresentationTriState.Unset,
    GroupPresentationTriState HoverSwitch = GroupPresentationTriState.Unset,
    string? CollapseBehavior = null,
    string? ChromeMode = null);

/// <summary>
/// Tri-state for the nullable group presentation booleans: Unset leaves the
/// field alone; the other three assign that exact value (including null,
/// which is the "follow default" reset).
/// </summary>
public enum GroupPresentationTriState
{
    Unset = 0,
    False = 1,
    True = 2,
    Null = 3,
}

public sealed record GroupNavigationDefaultsChange(
    string? NavigationStyle = null,
    string? TitleDisplayMode = null,
    bool? WheelSwitchEnabled = null,
    bool? HoverSwitchEnabled = null);

public sealed record LayoutPreferencesChange(
    string? NewPlacementTarget = null,
    string? DefaultBoundScreenId = null,
    string? DisconnectBehavior = null);

/// <summary>The capsule-override reset scopes the settings page offers.</summary>
public enum CapsuleOverrideResetScope
{
    BehaviorOnly,
    WidthsOnly,
    WidgetAndGroup,
    All,
}

public sealed record CapsuleOverrideReset(CapsuleOverrideResetScope Scope, string? WidgetId = null);
