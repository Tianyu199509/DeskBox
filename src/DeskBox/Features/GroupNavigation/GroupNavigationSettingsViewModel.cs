using DeskBox.Contracts;

namespace DeskBox.Features.GroupNavigation;

/// <summary>
/// Group-navigation section editor. The legacy settings shell keeps every
/// XAML/AOT binding, the overview/existing-group projections, the per-group
/// override editors and the explicit host notification it runs after a real
/// change; this editor is the feature seam over
/// <see cref="IGroupNavigationSettings"/> and owns no duplicated state. All
/// persistence rules (normalization, unchanged-write skip, the debounced
/// save) live in the coordinator.
/// </summary>
public sealed class GroupNavigationSettingsViewModel
{
    private readonly IGroupNavigationSettings _settings;

    public GroupNavigationSettingsViewModel(IGroupNavigationSettings settings)
    {
        _settings = settings;
    }

    public GroupNavigationSettingsSnapshot ReadAll() => _settings.ReadAll();

    public bool SetDefaultNavigationStyle(string? value) =>
        _settings.SetDefaultNavigationStyle(value);

    public bool SetDefaultTitleDisplayMode(string? value) =>
        _settings.SetDefaultTitleDisplayMode(value);

    public bool SetWheelSwitchEnabled(bool value) =>
        _settings.SetWheelSwitchEnabled(value);

    public bool SetHoverSwitchEnabled(bool value) =>
        _settings.SetHoverSwitchEnabled(value);
}
