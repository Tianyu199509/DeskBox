namespace DeskBox.Models;

/// <summary>
/// Music widget presentation preferences.
/// </summary>
public sealed class MusicSettingsSlice
{
    /// <summary>Whether the Music widget uses album artwork color as a soft backdrop.</summary>
    public bool MusicUseArtworkBackdrop { get; set; } = true;

    /// <summary>Whether the Music widget album cover reacts lightly to pointer hover.</summary>
    public bool MusicEnableCoverHoverMotion { get; set; } = true;

    /// <summary>
    /// Music widget layout mode. Valid values: <c>"Auto"</c>, <c>"Cover"</c>, <c>"Controls"</c>.
    /// </summary>
    public string MusicDisplayMode { get; set; } = "Auto";

    /// <summary>
    /// Whether the progress bar and time labels collapse when the player
    /// publishes no timeline (position and duration) through SMTC.
    /// </summary>
    public bool MusicHideTimelineWhenUnavailable { get; set; } = true;

    /// <summary>Whether synchronized lyrics are matched and displayed in the Music widget.</summary>
    public bool MusicShowLyrics { get; set; } = true;

    /// <summary>Whether the active lyric line is shown as the subtitle when collapsed into a capsule.</summary>
    public bool MusicShowCapsuleLyrics { get; set; } = true;

    /// <summary>Optional user-selected folder containing local <c>.lrc</c> files.</summary>
    public string MusicLocalLyricsFolder { get; set; } = string.Empty;
}
