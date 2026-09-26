using DeskBox.Contracts;

namespace DeskBox.Features.ManagedStorage;

/// <summary>
/// Managed-storage section editor. The legacy settings shell keeps the
/// read-only path display, the folder picker, the migration confirmation
/// and residue dialogs, the post-write quick-access refresh and every
/// XAML/AOT binding; this editor is the feature seam over
/// <see cref="IManagedStorageSettings"/> and owns no duplicated state. The
/// root-path persistence rules (normalization, store, one debounced save)
/// live in the coordinator; the file migration that moves widget content
/// between roots stays on the host's existing chain.
/// </summary>
public sealed class ManagedStorageSettingsViewModel
{
    private readonly IManagedStorageSettings _settings;

    public ManagedStorageSettingsViewModel(IManagedStorageSettings settings)
    {
        _settings = settings;
    }

    public string ReadDefaultRootPath() => _settings.ReadDefaultRootPath();

    public string SetDefaultRootPath(string path) => _settings.SetDefaultRootPath(path);
}
