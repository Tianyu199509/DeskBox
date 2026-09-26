using DeskBox.Contracts;

namespace DeskBox.Features.FileDisplay;

/// <summary>
/// File-display section editor. The legacy settings shell keeps every
/// XAML/AOT binding plus the restoring-defaults and snapshot-application
/// guards, and forwards its persisted writes here; this editor is the
/// feature seam over <see cref="IFileDisplaySettings"/> and owns no
/// duplicated state. All persistence rules live in the coordinator.
/// </summary>
public sealed class FileDisplaySettingsViewModel
{
    private readonly IFileDisplaySettings _settings;

    public FileDisplaySettingsViewModel(IFileDisplaySettings settings)
    {
        _settings = settings;
    }

    public FileDisplaySettingsSnapshot ReadAll() => _settings.ReadAll();

    public void SetShowFileExtensions(bool value) =>
        _settings.SetShowFileExtensions(value);
    public void SetHideShortcutExtensionWhenShowingFileExtensions(bool value) =>
        _settings.SetHideShortcutExtensionWhenShowingFileExtensions(value);
    public void SetHideShortcutArrowOverlay(bool value) =>
        _settings.SetHideShortcutArrowOverlay(value);
    public void SetShowImageFilesAsIcons(bool value) =>
        _settings.SetShowImageFilesAsIcons(value);
    public void SetShowListItemDetails(bool value) =>
        _settings.SetShowListItemDetails(value);
    public void SetShowFileItemPathTooltips(bool value) =>
        _settings.SetShowFileItemPathTooltips(value);
}
