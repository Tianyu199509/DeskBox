namespace DeskBox.Contracts;

/// <summary>
/// Maintenance-domain record-keeping writes from the settings page. The
/// only field today is the update-check timestamp: a bookkeeping stamp
/// written after every completed update check, never read back by the UI
/// and never broadcast. The write keeps the original semantics: store the
/// caller-provided timestamp and schedule one silent debounced save with no
/// SettingsChanged pass, so recording a check cannot disturb live widgets.
/// Update checks themselves (network, manifests, download, install) stay in
/// the update services and the settings shell's update card; the host's own
/// background check keeps its separate write.
/// </summary>
public interface IMaintenanceSettings
{
    DateTimeOffset? ReadLastUpdateCheckAt();

    void RecordUpdateCheck(DateTimeOffset checkedAt);
}
