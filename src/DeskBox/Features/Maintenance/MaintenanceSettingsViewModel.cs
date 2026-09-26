using DeskBox.Contracts;

namespace DeskBox.Features.Maintenance;

/// <summary>
/// Maintenance-domain editor for record-keeping writes. The legacy settings
/// shell keeps the update card, the check/download/install flows and every
/// XAML/AOT binding; this editor is the feature seam over
/// <see cref="IMaintenanceSettings"/> and owns no duplicated state. The
/// update-check timestamp persistence rule (store, one silent debounced
/// save without a SettingsChanged broadcast) lives in the coordinator.
/// </summary>
public sealed class MaintenanceSettingsViewModel
{
    private readonly IMaintenanceSettings _settings;

    public MaintenanceSettingsViewModel(IMaintenanceSettings settings)
    {
        _settings = settings;
    }

    public DateTimeOffset? ReadLastUpdateCheckAt() => _settings.ReadLastUpdateCheckAt();

    public void RecordUpdateCheck(DateTimeOffset checkedAt) =>
        _settings.RecordUpdateCheck(checkedAt);
}
