using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the group-navigation defaults: the default
/// navigation style, the default title display mode, the wheel-switch toggle
/// and the hover-switch toggle. The four values live on the device-domain
/// WidgetLayout slice (widget-layout.json), so every write goes through the
/// slice and the regular debounced save performs the paired layout+settings
/// commit — there is no separate persistence path. The section has no
/// live-preview coupling: every edit keeps the original semantics of
/// normalize (the shared style/title normalizers with FollowDefault never
/// allowed for the defaults, invalid values collapse to the fresh-install
/// defaults exactly like the page setters did), skip unchanged writes, store,
/// and schedule one debounced save; the boolean return reports whether the
/// persisted value changed so the shell keeps firing its notifications only
/// on real changes. Group switching behavior still reaches the host only
/// through the regular SettingsChanged broadcast plus the shell-side explicit
/// group-presentation notification that runs after the write.
/// </summary>
public sealed class GroupNavigationSettingsCoordinator : IGroupNavigationSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public GroupNavigationSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public GroupNavigationSettingsSnapshot ReadAll()
    {
        WidgetLayoutSettingsSlice layout = _settings.Settings.WidgetLayout;
        return new(
            layout.WidgetGroupDefaultNavigationStyle,
            layout.WidgetGroupDefaultTitleDisplayMode,
            layout.WidgetGroupWheelSwitchEnabled,
            layout.WidgetGroupHoverSwitchEnabled);
    }

    public bool SetDefaultNavigationStyle(string? value)
    {
        ThrowIfStopped();
        string normalized = WidgetGroupNavigationStyles.Normalize(
            value,
            allowFollowDefault: false);
        WidgetLayoutSettingsSlice layout = _settings.Settings.WidgetLayout;
        if (string.Equals(
                layout.WidgetGroupDefaultNavigationStyle,
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        layout.WidgetGroupDefaultNavigationStyle = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetDefaultTitleDisplayMode(string? value)
    {
        ThrowIfStopped();
        string normalized = WidgetGroupTitleDisplayModes.Normalize(
            value,
            allowFollowDefault: false);
        WidgetLayoutSettingsSlice layout = _settings.Settings.WidgetLayout;
        if (string.Equals(
                layout.WidgetGroupDefaultTitleDisplayMode,
                normalized,
                StringComparison.Ordinal))
        {
            return false;
        }

        layout.WidgetGroupDefaultTitleDisplayMode = normalized;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetWheelSwitchEnabled(bool value)
    {
        ThrowIfStopped();
        WidgetLayoutSettingsSlice layout = _settings.Settings.WidgetLayout;
        if (layout.WidgetGroupWheelSwitchEnabled == value)
        {
            return false;
        }

        layout.WidgetGroupWheelSwitchEnabled = value;
        _settings.SaveDebounced();
        return true;
    }

    public bool SetHoverSwitchEnabled(bool value)
    {
        ThrowIfStopped();
        WidgetLayoutSettingsSlice layout = _settings.Settings.WidgetLayout;
        if (layout.WidgetGroupHoverSwitchEnabled == value)
        {
            return false;
        }

        layout.WidgetGroupHoverSwitchEnabled = value;
        _settings.SaveDebounced();
        return true;
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
