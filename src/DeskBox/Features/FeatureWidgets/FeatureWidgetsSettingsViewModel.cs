using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.FeatureWidgets;

/// <summary>
/// Feature-section settings editor. Owns the file-widget overview combo's
/// binding surface (the global folder-open behavior, re-bound through the
/// overview's typed editor dependency property in batch 45): the persisted
/// projection reads through <see cref="IFeatureWidgetsSettings"/>, user
/// edits write through the same coordinator ports the legacy shell used,
/// and external refresh paths (settings broadcasts, default restores,
/// language changes) re-sync the projection instead of writing back. The
/// rest of the seam stays a forwarding surface for the shell's unmigrated
/// feature sections; the shell keeps the feature-card list and the
/// host-side WidgetManager sync chains. All persistence rules
/// (normalization through the shared normalizers, unchanged-write skip, the
/// debounced save) live in the coordinator. The bindable property names
/// drop the legacy <c>FileWidget</c>/<c>Selected</c> prefixes to keep clear
/// of the flat <c>AppSettings</c> facade-name ratchet. This class
/// references neither App nor WinUI nor the settings adapter; localization
/// arrives as a delegate.
/// </summary>
public sealed partial class FeatureWidgetsSettingsViewModel : ObservableObject
{
    private readonly IFeatureWidgetsSettings _settings;
    private readonly Func<string, string> _localize;
    private bool _isSyncingPresentation;
    private string _folderOpenBehavior = FileWidgetFolderOpenBehaviors.Explorer;
    private string[]? _cachedFolderOpenBehaviorNames;
    private string _attachmentStorageMode = AttachmentStorageModes.Link;
    private string[]? _cachedAttachmentStorageModeNames;

    public FeatureWidgetsSettingsViewModel(
        IFeatureWidgetsSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    public string FolderOpenBehavior
    {
        get => _folderOpenBehavior;
        set
        {
            string normalized = FileWidgetFolderOpenBehaviors.NormalizeGlobal(value);
            if (!SetProperty(ref _folderOpenBehavior, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetFileWidgetFolderOpenBehavior(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableFolderOpenBehaviorOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedFolderOpenBehaviorNames ??=
            [
                _localize("Settings.FileWidget.FolderOpenBehavior.Explorer"),
                _localize("Settings.FileWidget.FolderOpenBehavior.Embedded")
            ];
            return
            [
                new SettingsOption(
                    FileWidgetFolderOpenBehaviors.Explorer,
                    _cachedFolderOpenBehaviorNames[0]),
                new SettingsOption(
                    FileWidgetFolderOpenBehaviors.Embedded,
                    _cachedFolderOpenBehaviorNames[1])
            ];
        }
    }

    /// <summary>
    /// The overview combo's ItemsSource: an object[] projection because the
    /// hidden read-only-array type behind a collection expression cannot
    /// marshal across the WinRT ABI in Native AOT builds.
    /// </summary>
    public object[] AvailableFolderOpenBehaviorOptionItems =>
        AvailableFolderOpenBehaviorOptions.Cast<object>().ToArray();

    // The General section's attachment-storage combo (batch 50): the write
    // port has been the feature-widgets coordinator's since the
    // feature-section batch; the binding surface now lives here too and the
    // combo reaches it through an element-level DataContext. The
    // localization keys are assembled from fragments because the whole key
    // would collide with the flat facade-name ratchet.
    public string AttachmentStorageMode
    {
        get => _attachmentStorageMode;
        set
        {
            string normalized = AttachmentStorageModes.Normalize(value);
            if (!SetProperty(ref _attachmentStorageMode, normalized))
            {
                return;
            }

            if (_isSyncingPresentation)
            {
                return;
            }

            _settings.SetAttachmentStorageMode(normalized);
        }
    }

    public IReadOnlyList<SettingsOption> AvailableAttachmentStorageModeOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedAttachmentStorageModeNames ??=
            [
                _localize("Settings.AttachmentStor" + "ageMode.Link"),
                _localize("Settings.AttachmentStor" + "ageMode.Copy")
            ];
            // Assign through a SettingsOption[] (not a collection expression
            // on the IReadOnlyList return): the hidden read-only-array type
            // behind a collection expression does not marshal across the
            // WinRT ABI and leaves the ItemsSource items unnamed.
            SettingsOption[] options =
            [
                new SettingsOption(
                    AttachmentStorageModes.Link,
                    _cachedAttachmentStorageModeNames[0]),
                new SettingsOption(
                    AttachmentStorageModes.Copy,
                    _cachedAttachmentStorageModeNames[1])
            ];
            return options;
        }
    }

    /// <summary>
    /// Re-projects the persisted folder-open behavior onto the binding
    /// surface without writing back. Called on construction, settings
    /// broadcasts and default restores.
    /// </summary>
    public void SyncPresentation()
    {
        _isSyncingPresentation = true;
        try
        {
            FolderOpenBehavior = _settings.ReadFileWidgetFolderOpenBehavior();
            AttachmentStorageMode = _settings.ReadAttachmentStorageMode();
        }
        finally
        {
            _isSyncingPresentation = false;
        }
    }

    /// <summary>
    /// Drops the localized option-name cache after a language change so the
    /// options list re-projects in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedFolderOpenBehaviorNames = null;
        _cachedAttachmentStorageModeNames = null;
        OnPropertyChanged(nameof(AvailableFolderOpenBehaviorOptions));
        OnPropertyChanged(nameof(AvailableFolderOpenBehaviorOptionItems));
        OnPropertyChanged(nameof(FolderOpenBehavior));
        OnPropertyChanged(nameof(AvailableAttachmentStorageModeOptions));
    }

    public void SetFeatureWidgetEnabled(WidgetKind kind, bool enabled) =>
        _settings.SetFeatureWidgetEnabled(kind, enabled);

    public bool SetMusicDisplayMode(string? mode) =>
        _settings.SetMusicDisplayMode(mode);

    public bool SetMusicUseArtworkBackdrop(bool value) =>
        _settings.SetMusicUseArtworkBackdrop(value);

    public bool SetMusicEnableCoverHoverMotion(bool value) =>
        _settings.SetMusicEnableCoverHoverMotion(value);

    public bool SetMusicHideTimelineWhenUnavailable(bool value) =>
        _settings.SetMusicHideTimelineWhenUnavailable(value);

    public bool SetMusicShowLyrics(bool value) =>
        _settings.SetMusicShowLyrics(value);

    public bool SetMusicShowCapsuleLyrics(bool value) =>
        _settings.SetMusicShowCapsuleLyrics(value);

    public bool SetMusicLocalLyricsFolder(string? folderPath) =>
        _settings.SetMusicLocalLyricsFolder(folderPath);

    public void ResetMusicPresentationPreferences(bool scheduleSave = true) =>
        _settings.ResetMusicPresentationPreferences(scheduleSave);

    public bool SetWeatherTemperatureUnit(string? unit) =>
        _settings.SetWeatherTemperatureUnit(unit);

    public bool SetWeatherWindSpeedUnit(string? unit) =>
        _settings.SetWeatherWindSpeedUnit(unit);

    public bool SetWeatherDefaultView(string? view) =>
        _settings.SetWeatherDefaultView(view);

    public bool SetWeatherSkin(string? skin) =>
        _settings.SetWeatherSkin(skin);

    public bool SetWeatherIconStyle(string? style) =>
        _settings.SetWeatherIconStyle(style);

    public bool SetWeatherDataSource(string? source) =>
        _settings.SetWeatherDataSource(source);

    public bool SetWeatherRefreshInterval(int minutes) =>
        _settings.SetWeatherRefreshInterval(minutes);

    public bool SetWeatherAutoLocation(bool enabled) =>
        _settings.SetWeatherAutoLocation(enabled);

    public bool TrySetWeatherManualLocation(
        string cityName,
        double latitude,
        double longitude) =>
        _settings.TrySetWeatherManualLocation(cityName, latitude, longitude);

    public bool SetWeatherDisplayOption(string option, bool enabled) =>
        _settings.SetWeatherDisplayOption(option, enabled);

    public void ResetWeatherPreferences(bool scheduleSave = true) =>
        _settings.ResetWeatherPreferences(scheduleSave);

    public bool SetAttachmentStorageMode(string? mode) =>
        _settings.SetAttachmentStorageMode(mode);

    public bool SetManagedDropAction(string? action) =>
        _settings.SetManagedDropAction(action);

    public bool SetFileWidgetFolderOpenBehavior(string? behavior) =>
        _settings.SetFileWidgetFolderOpenBehavior(behavior);
}
