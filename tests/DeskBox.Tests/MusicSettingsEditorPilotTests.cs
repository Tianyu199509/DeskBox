using DeskBox.Contracts;
using DeskBox.Features.Music;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Pilot batch for retiring the settings shell's binding facade: the music
/// section is the first section re-bound to its own Features editor through
/// a section-level DataContext switch. These tests pin both the editor's
/// behavior (read snapshot projection, write-through, no write-back on
/// external sync, localization refresh) and the migration pattern itself
/// (XAML paths, AOT bridge, window wiring, facade removal) so later batches
/// can copy the shape instead of re-deriving it.
/// </summary>
public sealed class MusicSettingsEditorPilotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, MusicSettingsViewModel Editor) CreateEditor(
        string root,
        Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        return (settings, new MusicSettingsViewModel(coordinator, PassthroughLocalize));
    }

    [Fact]
    public void Defaults_EnableLyricsDisplayOutOfTheBox()
    {
        (SettingsService settings, MusicSettingsViewModel editor) = CreateEditor(_root);

        Assert.True(settings.Settings.Music.MusicShowLyrics);
        Assert.True(settings.Settings.Music.MusicShowCapsuleLyrics);
        Assert.True(editor.ShowLyrics);
        Assert.True(editor.ShowCapsuleLyrics);
    }

    [Fact]
    public void Constructor_ProjectsPersistedPresentationFromTheReadSnapshot()
    {
        (_, MusicSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                MusicSettingsSlice music = settings.Settings.Music;
                music.MusicDisplayMode = MusicDisplayModes.Cover;
                music.MusicUseArtworkBackdrop = false;
                music.MusicEnableCoverHoverMotion = false;
                music.MusicHideTimelineWhenUnavailable = false;
                music.MusicShowLyrics = false;
                music.MusicShowCapsuleLyrics = false;
                music.MusicLocalLyricsFolder = @"D:\Music\Lyrics";
            });

        Assert.False(editor.UseArtworkBackdrop);
        Assert.False(editor.EnableCoverHoverMotion);
        Assert.False(editor.HideTimelineWhenUnavailable);
        Assert.False(editor.ShowLyrics);
        Assert.False(editor.ShowCapsuleLyrics);
        Assert.Equal(@"D:\Music\Lyrics", editor.LocalLyricsFolder);
        Assert.True(editor.HasLocalLyricsFolder);
        Assert.Equal(MusicDisplayModes.Cover, editor.DisplayMode);
    }

    [Fact]
    public void ReadSnapshot_NormalizesTheDisplayMode()
    {
        (_, MusicSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.Music.MusicDisplayMode = "Nonsense");

        Assert.Equal(MusicDisplayModes.Auto, editor.DisplayMode);
    }

    [Fact]
    public void UserEdits_WriteThroughTheCoordinatorAndPersist()
    {
        (SettingsService settings, MusicSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.UseArtworkBackdrop = false;
        editor.EnableCoverHoverMotion = false;
        editor.HideTimelineWhenUnavailable = false;
        editor.ShowLyrics = false;
        editor.ShowCapsuleLyrics = false;
        editor.LocalLyricsFolder = @"D:\Music\Lrc";
        editor.DisplayMode = MusicDisplayModes.RecordVertical;

        MusicSettingsSlice music = settings.Settings.Music;
        Assert.False(music.MusicUseArtworkBackdrop);
        Assert.False(music.MusicEnableCoverHoverMotion);
        Assert.False(music.MusicHideTimelineWhenUnavailable);
        Assert.False(music.MusicShowLyrics);
        Assert.False(music.MusicShowCapsuleLyrics);
        Assert.Equal(@"D:\Music\Lrc", music.MusicLocalLyricsFolder);
        Assert.Equal(MusicDisplayModes.RecordVertical, music.MusicDisplayMode);
        Assert.Equal(7, notified);
    }

    [Fact]
    public void ExternalSync_RefreshesTheProjectionWithoutWritingBack()
    {
        (SettingsService settings, MusicSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // External mutation (restore, snapshot apply, feature-card reset).
        MusicSettingsSlice music = settings.Settings.Music;
        music.MusicDisplayMode = MusicDisplayModes.Controls;
        music.MusicUseArtworkBackdrop = false;
        music.MusicEnableCoverHoverMotion = false;
        music.MusicHideTimelineWhenUnavailable = false;
        music.MusicShowLyrics = false;
        music.MusicShowCapsuleLyrics = false;
        music.MusicLocalLyricsFolder = @"D:\External\Lyrics";

        int notifiedBeforeSync = notified;
        editor.SyncPresentation();

        Assert.False(editor.UseArtworkBackdrop);
        Assert.False(editor.EnableCoverHoverMotion);
        Assert.False(editor.HideTimelineWhenUnavailable);
        Assert.False(editor.ShowLyrics);
        Assert.False(editor.ShowCapsuleLyrics);
        Assert.Equal(@"D:\External\Lyrics", editor.LocalLyricsFolder);
        Assert.Equal(MusicDisplayModes.Controls, editor.DisplayMode);
        Assert.Equal(notifiedBeforeSync, notified);
    }

    [Fact]
    public void Options_ListCanonicalValuesWithLocalizedNames()
    {
        (_, MusicSettingsViewModel editor) = CreateEditor(_root);

        SettingsOption[] options = [.. editor.AvailableDisplayModeOptions];
        Assert.Equal(5, options.Length);
        Assert.Equal(MusicDisplayModes.Auto, options[0].Value);
        Assert.Equal(MusicDisplayModes.Cover, options[1].Value);
        Assert.Equal(MusicDisplayModes.Controls, options[2].Value);
        Assert.Equal(MusicDisplayModes.RecordVertical, options[3].Value);
        Assert.Equal(MusicDisplayModes.RecordHorizontal, options[4].Value);
        Assert.Equal("Settings.Music.DisplayMode.Auto", options[0].DisplayName);
        Assert.Equal("Settings.Music.DisplayMode.RecordHorizontal", options[4].DisplayName);

        SettingsOption[] spectrumOptions = [.. editor.AvailableSpectrumStyleOptions];
        Assert.Equal(8, spectrumOptions.Length);
        Assert.Equal(MusicSpectrumStyles.Auto, spectrumOptions[0].Value);
        Assert.Equal(MusicSpectrumStyles.SymmetricWave, spectrumOptions[1].Value);
        Assert.Equal(MusicSpectrumStyles.WaveformScrub, spectrumOptions[2].Value);
        Assert.Equal(MusicSpectrumStyles.PeakHoldBars, spectrumOptions[3].Value);
        Assert.Equal(MusicSpectrumStyles.AuroraSilk, spectrumOptions[4].Value);
        Assert.Equal(MusicSpectrumStyles.MatrixLed, spectrumOptions[5].Value);
        Assert.Equal(MusicSpectrumStyles.VinylRadialHalo, spectrumOptions[6].Value);
        Assert.Equal(MusicSpectrumStyles.Off, spectrumOptions[7].Value);
    }

    [Fact]
    public void RefreshLocalization_RebuildsTheOptionsAndNotifies()
    {
        (_, MusicSettingsViewModel editor) = CreateEditor(_root);
        Assert.Equal(
            "Settings.Music.DisplayMode.Cover",
            editor.AvailableDisplayModeOptions[1].DisplayName);

        int notified = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MusicSettingsViewModel.AvailableDisplayModeOptions))
            {
                notified++;
            }
        };
        // A later language switch localizes through the injected delegate.
        var relabeled = new MusicSettingsViewModel(
            new FeatureWidgetsSettingsCoordinator(
                new SettingsService(Path.Combine(_root, "second"))),
            static key => key == "Settings.Music.DisplayMode.Cover" ? "zh:Cover" : key);
        relabeled.RefreshLocalization();
        Assert.Equal("zh:Cover", relabeled.AvailableDisplayModeOptions[1].DisplayName);

        editor.RefreshLocalization();
        Assert.Equal(1, notified);
        // The cache rebuild keeps the canonical values untouched.
        Assert.Equal(MusicDisplayModes.Cover, editor.AvailableDisplayModeOptions[1].Value);
    }

    [Fact]
    public void SettingsService_MusicConstants_AliasTheContractCanonicalValues()
    {
        Assert.Equal(MusicDisplayModes.Auto, SettingsService.MusicDisplayModeAuto);
        Assert.Equal(MusicDisplayModes.Cover, SettingsService.MusicDisplayModeCover);
        Assert.Equal(MusicDisplayModes.Controls, SettingsService.MusicDisplayModeControls);
        Assert.Equal(
            MusicDisplayModes.RecordVertical,
            SettingsService.MusicDisplayModeRecordVertical);
        Assert.Equal(
            MusicDisplayModes.RecordHorizontal,
            SettingsService.MusicDisplayModeRecordHorizontal);
    }

    [Fact]
    public void SettingsShell_NoLongerExposesTheMusicCompatFacade()
    {
        System.Reflection.PropertyInfo[] properties = typeof(ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        string[] removed =
        [
            "MusicUseArtworkBackdrop",
            "MusicEnableCoverHoverMotion",
            "SelectedMusicDisplayMode",
            "SelectedMusicDisplayModeText",
            "AvailableMusicDisplayModeOptions"
        ];
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }
    }

    [Fact]
    public void MusicSection_BindsToTheEditorThroughSectionLevelDataContext()
    {
        string xaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string bindable = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));
        string editorBridge = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Features/Music/MusicSettingsViewModel.AotBindableProperties.cs"));
        string sync = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs"));

        // {Binding} markup stays (WMC1510 count unchanged); lyrics cards
        // bind via compiled {x:Bind} typed to MusicSettingsViewModel.
        Assert.Contains("ItemsSource=\"{Binding AvailableDisplayModeOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:SettingsComboBox.Value=\"{Binding DisplayMode, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding UseArtworkBackdrop, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding EnableCoverHoverMotion, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ShowLyrics, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{x:Bind ShowCapsuleLyrics, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind LocalLyricsFolderDisplayText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding MusicUseArtworkBackdrop", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding MusicEnableCoverHoverMotion", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding SelectedMusicDisplayMode", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding AvailableMusicDisplayModeOptions", xaml, StringComparison.Ordinal);

        // The deferred-section host switches the section DataContext to the
        // editor instead of leaving the shell view model in place.
        Assert.Contains("if (sectionTag == \"MusicSettings\")", deferred, StringComparison.Ordinal);
        Assert.Contains("section.DataContext = _musicSettingsViewModel;", deferred, StringComparison.Ordinal);

        // The shell bridge drops the four music entries; the editor carries
        // its own NativeAOT custom-property bridge.
        Assert.DoesNotContain("nameof(AvailableMusicDisplayModeOptions)", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(MusicUseArtworkBackdrop)", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(MusicEnableCoverHoverMotion)", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(SelectedMusicDisplayMode)", bindable, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", editorBridge, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(AvailableDisplayModeOptions)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(DisplayMode)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(UseArtworkBackdrop)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(EnableCoverHoverMotion)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(HideTimelineWhenUnavailable)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(ShowLyrics)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(ShowCapsuleLyrics)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(LocalLyricsFolder)", editorBridge, StringComparison.Ordinal);

        // External refresh paths re-sync the editor projection instead of
        // assigning shell facade properties.
        Assert.Contains("_musicSettings.SyncPresentation();", sync, StringComparison.Ordinal);
        Assert.Contains("_musicSettings.RefreshLocalization();", sync, StringComparison.Ordinal);
    }
}
