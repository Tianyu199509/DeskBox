using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the file-display section fields: file-name
/// extension visibility plus the shortcut-extension hiding rule, the shortcut
/// link-arrow overlay, image-file icon projection, list-item detail lines and
/// file-item path tooltips. The section has no live-preview coupling with the
/// appearance machinery: every edit keeps the original semantics of store and
/// schedule one debounced save, so extension and image-icon changes still
/// reach the icon-cache clearing and file reprojection only through the
/// regular SettingsChanged consumers (WidgetViewModel/FileService), and the
/// shell keeps its restoring-defaults and snapshot-application guards that
/// decide whether a callback writes at all. Unchanged writes skip the
/// redundant debounced save, matching the callback-driven flow where the
/// observable property only fires on real changes.
/// </summary>
public sealed class FileDisplaySettingsCoordinator : IFileDisplaySettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public FileDisplaySettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public FileDisplaySettingsSnapshot ReadAll()
    {
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        return new(
            fileWidget.ShowFileExtensions,
            fileWidget.HideShortcutExtensionWhenShowingFileExtensions,
            fileWidget.HideShortcutArrowOverlay,
            fileWidget.ShowImageFilesAsIcons,
            fileWidget.ShowListItemDetails,
            fileWidget.ShowFileItemPathTooltips);
    }

    public void SetShowFileExtensions(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.ShowFileExtensions == value) return;
        fileWidget.ShowFileExtensions = value;
        _settings.SaveDebounced();
    }

    public void SetHideShortcutExtensionWhenShowingFileExtensions(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.HideShortcutExtensionWhenShowingFileExtensions == value) return;
        fileWidget.HideShortcutExtensionWhenShowingFileExtensions = value;
        _settings.SaveDebounced();
    }

    public void SetHideShortcutArrowOverlay(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.HideShortcutArrowOverlay == value) return;
        fileWidget.HideShortcutArrowOverlay = value;
        _settings.SaveDebounced();
    }

    public void SetShowImageFilesAsIcons(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.ShowImageFilesAsIcons == value) return;
        fileWidget.ShowImageFilesAsIcons = value;
        _settings.SaveDebounced();
    }

    public void SetShowListItemDetails(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.ShowListItemDetails == value) return;
        fileWidget.ShowListItemDetails = value;
        _settings.SaveDebounced();
    }

    public void SetShowFileItemPathTooltips(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.ShowFileItemPathTooltips == value) return;
        fileWidget.ShowFileItemPathTooltips = value;
        _settings.SaveDebounced();
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
