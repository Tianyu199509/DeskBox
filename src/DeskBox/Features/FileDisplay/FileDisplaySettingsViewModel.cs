using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;

namespace DeskBox.Features.FileDisplay;

/// <summary>
/// File-display section settings editor. Owns the section's XAML binding
/// surface (the six pure-persisted toggles: file-name extension visibility
/// plus the shortcut-extension hiding that only applies while extensions are
/// shown, the shortcut link-arrow overlay, image-file icon projection,
/// list-item detail lines and file-item path tooltips): reads project
/// through the read snapshot of <see cref="IFileDisplaySettings"/>, user
/// edits write through the same coordinator ports the legacy shell used,
/// and external refresh paths (settings broadcasts, feature-card default
/// restores) re-sync the projection instead of writing back. The icon cache
/// clearing and file reprojection that follow extension or image-icon
/// changes remain host-side consumers of the regular SettingsChanged
/// broadcast, exactly as before. The section-level DataContext switch means
/// the property names no longer need to be unique across the whole shell;
/// they stay unprefixed because they never carried one. Like the music
/// editor, this class references neither App nor WinUI nor the settings
/// adapter and needs no localization (the toggles carry no editor-side
/// text).
/// </summary>
public sealed partial class FileDisplaySettingsViewModel : ObservableObject
{
    private readonly IFileDisplaySettings _settings;
    private bool _isSyncingPresentation;

    public FileDisplaySettingsViewModel(IFileDisplaySettings settings)
    {
        _settings = settings;
        SyncPresentation();
    }

    [ObservableProperty]
    public partial bool ShowFileExtensions { get; set; }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetShowFileExtensions(value);
        }
    }

    [ObservableProperty]
    public partial bool HideShortcutExtensionWhenShowingFileExtensions { get; set; } = true;

    partial void OnHideShortcutExtensionWhenShowingFileExtensionsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetHideShortcutExtensionWhenShowingFileExtensions(value);
        }
    }

    [ObservableProperty]
    public partial bool HideShortcutArrowOverlay { get; set; }

    partial void OnHideShortcutArrowOverlayChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetHideShortcutArrowOverlay(value);
        }
    }

    [ObservableProperty]
    public partial bool ShowImageFilesAsIcons { get; set; }

    public bool ShowImageThumbnails
    {
        get => !ShowImageFilesAsIcons;
        set => ShowImageFilesAsIcons = !value;
    }

    partial void OnShowImageFilesAsIconsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowImageThumbnails));
        if (!_isSyncingPresentation)
        {
            _settings.SetShowImageFilesAsIcons(value);
        }
    }

    [ObservableProperty]
    public partial bool ShowListItemDetails { get; set; }

    partial void OnShowListItemDetailsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetShowListItemDetails(value);
        }
    }

    [ObservableProperty]
    public partial bool ShowFileItemPathTooltips { get; set; } = true;

    partial void OnShowFileItemPathTooltipsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetShowFileItemPathTooltips(value);
        }
    }

    /// <summary>
    /// Re-projects the persisted file-display state onto the binding surface
    /// without writing back. Called on construction, settings broadcasts and
    /// feature-card default restores.
    /// </summary>
    public void SyncPresentation()
    {
        FileDisplaySettingsSnapshot snapshot = _settings.ReadAll();
        _isSyncingPresentation = true;
        try
        {
            ShowFileExtensions = snapshot.ShowFileExtensions;
            HideShortcutExtensionWhenShowingFileExtensions =
                snapshot.HideShortcutExtensionWhenShowingFileExtensions;
            HideShortcutArrowOverlay = snapshot.HideShortcutArrowOverlay;
            ShowImageFilesAsIcons = snapshot.ShowImageFilesAsIcons;
            ShowListItemDetails = snapshot.ShowListItemDetails;
            ShowFileItemPathTooltips = snapshot.ShowFileItemPathTooltips;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(ShowImageThumbnails));
    }
}
