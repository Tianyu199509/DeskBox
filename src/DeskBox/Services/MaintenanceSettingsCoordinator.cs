using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for maintenance-domain record-keeping fields.
/// Today that is the update-check timestamp
/// (<see cref="CoreSettingsSlice.LastUpdateCheckAt"/>): a pure record write
/// stamped after a completed check, never read back by the UI, persisted
/// with one silent debounced save — no SettingsChanged broadcast, so
/// recording a check cannot trigger widget refreshes. The host's own
/// background update check keeps its separate write; this coordinator owns
/// only the settings page's stamp.
/// </summary>
public sealed class MaintenanceSettingsCoordinator : IMaintenanceSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public MaintenanceSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public DateTimeOffset? ReadLastUpdateCheckAt() =>
        _settings.Settings.Core.LastUpdateCheckAt;

    public void RecordUpdateCheck(DateTimeOffset checkedAt)
    {
        ThrowIfStopped();
        _settings.Settings.Core.LastUpdateCheckAt = checkedAt;
        _settings.SaveDebounced(notifySubscribers: false);
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
