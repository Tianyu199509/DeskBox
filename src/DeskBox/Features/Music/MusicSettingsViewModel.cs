using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Features.Music;

/// <summary>
/// Music-section settings editor. Owns the section's XAML binding surface
/// (display mode combo, presentation toggles, and synchronized lyrics options): reads project
/// through the read snapshot of <see cref="IFeatureWidgetsSettings"/>, user
/// edits write through the same coordinator ports the legacy shell used,
/// and external refresh paths (settings broadcasts, feature-card default
/// restores, language changes) re-sync the projection instead of writing
/// back. The bindable property names intentionally drop the legacy
/// <c>Music</c> prefix: the section-level DataContext switch means they no
/// longer need to be unique across the whole shell, and unprefixed names
/// keep the flat <c>AppSettings</c> facade-name ratchet shrinking. Like the
/// Todo editor, this class references neither App nor WinUI nor the
/// settings adapter; localization arrives as a delegate.
/// </summary>
public sealed partial class MusicSettingsViewModel : ObservableObject
{
    private static readonly string[] DisplayModes =
    [
        MusicDisplayModes.Auto,
        MusicDisplayModes.Cover,
        MusicDisplayModes.Controls,
        MusicDisplayModes.RecordVertical,
        MusicDisplayModes.RecordHorizontal
    ];

    private static readonly string[] DisplayModeNameKeys =
    [
        "Settings.Music.DisplayMode.Auto",
        "Settings.Music.DisplayMode.Cover",
        "Settings.Music.DisplayMode.Controls",
        "Settings.Music.DisplayMode.RecordVertical",
        "Settings.Music.DisplayMode.RecordHorizontal"
    ];

    private static readonly string[] SpectrumStyles =
    [
        MusicSpectrumStyles.Auto,
        MusicSpectrumStyles.SymmetricWave,
        MusicSpectrumStyles.WaveformScrub,
        MusicSpectrumStyles.PeakHoldBars,
        MusicSpectrumStyles.AuroraSilk,
        MusicSpectrumStyles.MatrixLed,
        MusicSpectrumStyles.VinylRadialHalo,
        MusicSpectrumStyles.Off
    ];

    private static readonly string[] SpectrumStyleNameKeys =
    [
        "Settings.Music.SpectrumStyle.Auto",
        "Settings.Music.SpectrumStyle.SymmetricWave",
        "Settings.Music.SpectrumStyle.WaveformScrub",
        "Settings.Music.SpectrumStyle.PeakHoldBars",
        "Settings.Music.SpectrumStyle.AuroraSilk",
        "Settings.Music.SpectrumStyle.MatrixLed",
        "Settings.Music.SpectrumStyle.VinylRadialHalo",
        "Settings.Music.SpectrumStyle.Off"
    ];

    private readonly IFeatureWidgetsSettings _settings;
    private readonly Func<string, string> _localize;
    private bool _isSyncingPresentation;
    private string[]? _cachedDisplayModeNames;
    private string[]? _cachedSpectrumStyleNames;
    private string _displayMode = MusicDisplayModes.Auto;
    private string _spectrumStyle = MusicSpectrumStyles.Auto;

    public MusicSettingsViewModel(
        IFeatureWidgetsSettings settings,
        Func<string, string> localize)
    {
        _settings = settings;
        _localize = localize;
        SyncPresentation();
    }

    [ObservableProperty]
    public partial bool UseArtworkBackdrop { get; set; } = true;

    partial void OnUseArtworkBackdropChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicUseArtworkBackdrop(value);
        }
    }

    [ObservableProperty]
    public partial bool EnableCoverHoverMotion { get; set; } = true;

    partial void OnEnableCoverHoverMotionChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicEnableCoverHoverMotion(value);
        }
    }

    [ObservableProperty]
    public partial bool HideTimelineWhenUnavailable { get; set; } = true;

    partial void OnHideTimelineWhenUnavailableChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicHideTimelineWhenUnavailable(value);
        }
    }

    [ObservableProperty]
    public partial bool ShowLyrics { get; set; } = true;

    partial void OnShowLyricsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicShowLyrics(value);
        }
    }

    [ObservableProperty]
    public partial bool ShowCapsuleLyrics { get; set; } = true;

    partial void OnShowCapsuleLyricsChanged(bool value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicShowCapsuleLyrics(value);
        }
    }

    [ObservableProperty]
    public partial string LocalLyricsFolder { get; set; } = string.Empty;

    partial void OnLocalLyricsFolderChanged(string value)
    {
        if (!_isSyncingPresentation)
        {
            _settings.SetMusicLocalLyricsFolder(value);
        }

        OnPropertyChanged(nameof(HasLocalLyricsFolder));
        OnPropertyChanged(nameof(LocalLyricsFolderDisplayText));
    }

    public bool HasLocalLyricsFolder => !string.IsNullOrWhiteSpace(LocalLyricsFolder);

    public string LocalLyricsFolderDisplayText => HasLocalLyricsFolder
        ? LocalLyricsFolder
        : _localize("Settings.Music.LocalLyricsFolder.Unset");

    public string DisplayMode
    {
        get => _displayMode;
        set
        {
            // The combo only offers canonical values and the coordinator
            // normalizes writes, so the editor stores the raw selection and
            // re-syncs from the normalized snapshot on the next broadcast.
            if (!SetProperty(ref _displayMode, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetMusicDisplayMode(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableDisplayModeOptions
    {
        get
        {
            // Build a real SettingsOption[] (not a collection expression): the
            // hidden read-only-array type cannot marshal across the WinRT ABI
            // in Native AOT builds and would leave the ItemsSource empty.
            _cachedDisplayModeNames ??= DisplayModeNameKeys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[DisplayModes.Length];
            for (int index = 0; index < DisplayModes.Length; index++)
            {
                options[index] = new SettingsOption(DisplayModes[index], _cachedDisplayModeNames[index]);
            }

            return options;
        }
    }

    public string SpectrumStyle
    {
        get => _spectrumStyle;
        set
        {
            if (!SetProperty(ref _spectrumStyle, value))
            {
                return;
            }

            if (!_isSyncingPresentation)
            {
                _settings.SetMusicSpectrumStyle(value);
            }
        }
    }

    public IReadOnlyList<SettingsOption> AvailableSpectrumStyleOptions
    {
        get
        {
            _cachedSpectrumStyleNames ??= SpectrumStyleNameKeys.Select(key => _localize(key)).ToArray();
            var options = new SettingsOption[SpectrumStyles.Length];
            for (int index = 0; index < SpectrumStyles.Length; index++)
            {
                options[index] = new SettingsOption(SpectrumStyles[index], _cachedSpectrumStyleNames[index]);
            }

            return options;
        }
    }

    /// <summary>
    /// Re-projects the persisted music presentation state onto the binding
    /// surface without writing back. Called on construction, settings
    /// broadcasts and feature-card default restores.
    /// </summary>
    public void SyncPresentation()
    {
        MusicPresentationSettings snapshot = _settings.ReadMusicPresentation();
        _isSyncingPresentation = true;
        try
        {
            UseArtworkBackdrop = snapshot.UseArtworkBackdrop;
            EnableCoverHoverMotion = snapshot.EnableCoverHoverMotion;
            DisplayMode = snapshot.DisplayMode;
            SpectrumStyle = snapshot.SpectrumStyle;
            HideTimelineWhenUnavailable = snapshot.HideTimelineWhenUnavailable;
            ShowLyrics = snapshot.ShowLyrics;
            ShowCapsuleLyrics = snapshot.ShowCapsuleLyrics;
            LocalLyricsFolder = snapshot.LocalLyricsFolder ?? string.Empty;
        }
        finally
        {
            _isSyncingPresentation = false;
        }

        OnPropertyChanged(nameof(HasLocalLyricsFolder));
        OnPropertyChanged(nameof(LocalLyricsFolderDisplayText));
    }

    /// <summary>
    /// Drops the localized display-name cache after a language change so the
    /// options list re-projects in the new language.
    /// </summary>
    public void RefreshLocalization()
    {
        _cachedDisplayModeNames = null;
        _cachedSpectrumStyleNames = null;
        OnPropertyChanged(nameof(AvailableDisplayModeOptions));
        OnPropertyChanged(nameof(AvailableSpectrumStyleOptions));
        OnPropertyChanged(nameof(LocalLyricsFolderDisplayText));
    }
}
