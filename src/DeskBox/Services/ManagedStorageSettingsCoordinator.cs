using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the managed-storage section: the default
/// managed storage root path (<see cref="FileWidgetSettingsSlice"/>). The
/// write is the commit step that follows the host's existing storage
/// migration chain — the WidgetManager moves widget content between roots
/// with its own rollback, residue cleanup and stored-path writes inside
/// that chain; this coordinator owns only the settings page's persisted
/// write. Every write keeps the original command semantics: normalize the
/// raw path through the shared SettingsService normalizer (blank or
/// unusable paths fall back to the default root), store, and schedule one
/// debounced save with the regular SettingsChanged broadcast. The settings
/// window filters no-op changes before invoking the flow, so the write is
/// unconditional, matching the pre-migration command.
/// </summary>
public sealed class ManagedStorageSettingsCoordinator : IManagedStorageSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public ManagedStorageSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public string ReadDefaultRootPath() =>
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath;

    public string SetDefaultRootPath(string path)
    {
        ThrowIfStopped();
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(path);
        _settings.Settings.FileWidget.DefaultManagedStorageRootPath = normalizedPath;
        _settings.SaveDebounced();
        return normalizedPath;
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
