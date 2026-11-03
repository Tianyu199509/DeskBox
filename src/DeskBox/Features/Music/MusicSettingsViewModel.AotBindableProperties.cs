#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Music;

// The music settings section keeps its runtime {Binding} surface and binds
// through a section-level DataContext switch. Expose only the properties
// used by that XAML surface in NativeAOT builds, mirroring the
// GlanceWidgetViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AvailableDisplayModeOptions),
    nameof(DisplayMode),
    nameof(EnableCoverHoverMotion),
    nameof(HasLocalLyricsFolder),
    nameof(HideTimelineWhenUnavailable),
    nameof(LocalLyricsFolder),
    nameof(LocalLyricsFolderDisplayText),
    nameof(ShowCapsuleLyrics),
    nameof(ShowLyrics),
    nameof(UseArtworkBackdrop)
], [])]
public sealed partial class MusicSettingsViewModel
{
}
#endif
