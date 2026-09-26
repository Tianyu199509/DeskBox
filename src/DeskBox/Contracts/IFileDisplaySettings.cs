namespace DeskBox.Contracts;

public readonly record struct FileDisplaySettingsSnapshot(
    bool ShowFileExtensions,
    bool HideShortcutExtensionWhenShowingFileExtensions,
    bool HideShortcutArrowOverlay,
    bool ShowImageFilesAsIcons,
    bool ShowListItemDetails,
    bool ShowFileItemPathTooltips);

/// <summary>
/// Settings-page writes for the file-display section: file-name extension
/// visibility (plus the shortcut-extension hiding that only applies while
/// extensions are shown), the shortcut link-arrow overlay, image-file icon
/// projection, list-item detail lines and file-item path tooltips. The
/// settings shell keeps the XAML/AOT binding surface and the
/// restoring-defaults / snapshot-application guards that decide whether a
/// callback writes at all; this port owns only the raw persisted values with
/// their original semantics: store and schedule one debounced save. The icon
/// cache clearing and file reprojection that follow extension or image-icon
/// changes stay outside this port — they are host-side consumers of the
/// regular SettingsChanged broadcast, exactly as before.
/// </summary>
public interface IFileDisplaySettings
{
    FileDisplaySettingsSnapshot ReadAll();

    void SetShowFileExtensions(bool value);
    void SetHideShortcutExtensionWhenShowingFileExtensions(bool value);
    void SetHideShortcutArrowOverlay(bool value);
    void SetShowImageFilesAsIcons(bool value);
    void SetShowListItemDetails(bool value);
    void SetShowFileItemPathTooltips(bool value);
}
