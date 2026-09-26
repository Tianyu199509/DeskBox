namespace DeskBox.Contracts;

/// <summary>
/// Settings-page writes for the managed-storage section. The section's only
/// persisted field is the default managed storage root path — the folder
/// that widgets following the default root store their content in. The
/// settings shell keeps the read-only path display, the folder picker, the
/// migration confirmation/residue dialogs and the quick-access refresh that
/// run around the write; the file migration itself (moving widget content
/// between roots, rollback, residue cleanup) stays on the host's existing
/// WidgetManager chain and is not part of this port. The write keeps the
/// original command semantics: normalize the raw path through the shared
/// SettingsService normalizer (blank or unusable paths fall back to the
/// default root), store, and schedule one debounced save with the regular
/// SettingsChanged broadcast. The settings window compares the normalized
/// path against the displayed value before invoking the flow, so no-op
/// writes stay filtered by the caller exactly as before.
/// </summary>
public interface IManagedStorageSettings
{
    string ReadDefaultRootPath();

    string SetDefaultRootPath(string path);
}
