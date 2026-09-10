namespace DeskBox.MusicPackage.ViewModels;

// MusicWidgetContent deliberately shares one runtime Binding surface across
// its minimal, controls, and record layouts. Preserve only the properties
// consumed by that XAML surface when NativeAOT trims reflection metadata.
[WinRT.GeneratedBindableCustomProperty([
    nameof(Artist),
    nameof(ArtworkBackdropCornerRadius),
    nameof(ArtworkBackdropEndColor),
    nameof(ArtworkBackdropMidColor),
    nameof(ArtworkBackdropStartColor),
    nameof(ArtworkBackdropVisibility),
    nameof(CanChangePlaybackMode),
    nameof(CanGoNext),
    nameof(CanGoPrevious),
    nameof(CanPlayPause),
    nameof(CaptionTextSize),
    nameof(DurationText),
    nameof(MinimalTitleTextSize),
    nameof(MusicAccentBrush),
    nameof(NextTooltip),
    nameof(PauseIconVisibility),
    nameof(PlaybackModeGlyph),
    nameof(PlaybackModeOpacity),
    nameof(PlaybackModeTooltip),
    nameof(PlayIconVisibility),
    nameof(PlayPauseTooltip),
    nameof(PositionText),
    nameof(PreviousTooltip),
    nameof(SecondaryTextSize),
    nameof(StatusText),
    nameof(SystemVolume),
    nameof(SystemVolumeText),
    nameof(ThumbnailImage),
    nameof(ThumbnailPlaceholderVisibility),
    nameof(ThumbnailVisibility),
    nameof(Title),
    nameof(TitleTextSize),
    nameof(VolumeTextSize),
    nameof(VolumeTooltip)
], [])]
public sealed partial class MusicWidgetViewModel
{
}
