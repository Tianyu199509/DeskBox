using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum CompactParticleKind
{
    None,
    Rain,
    Snow
}

public sealed record WidgetCompactPresentation(
    string Title,
    string Summary,
    string Glyph,
    string DropHint,
    ImageSource? Thumbnail = null,
    bool ShowPrimaryAction = false,
    string PrimaryActionGlyph = "\uE73E",
    bool ShowMediaControls = false,
    bool IsPlaying = false,
    bool CanGoPrevious = false,
    bool CanGoNext = false,
    bool UseStackedText = false,
    bool EnableMarquee = false,
    double? Progress = null,
    bool IsProgressIndeterminate = false,
    bool IsAttention = false,
    string LiveStateKey = "",
    bool UseFullBleedBackground = false,
    string BadgeText = "",
    bool BadgeIsWarning = false,
    string EmojiIcon = "",
    // ── Visual effects ──────────────────────────────────────
    Windows.UI.Color? BackgroundColorStart = null,
    Windows.UI.Color? BackgroundColorEnd = null,
    Windows.UI.Color? EdgeGlowColor = null,
    CompactParticleKind ParticleKind = CompactParticleKind.None,
    bool ShowSpectrum = false,
    bool ShowShimmer = false,
    Windows.UI.Color? IconColor = null,
    bool EnableBounceOnUpdate = false,
    bool ShowVinyl = false,
    // ── Music capsule body progress bar (below the artist name) ──
    // Determinate fill ratio in [0,1]. Null means "no bar" (e.g. no
    // seekable timeline yet). Driven by SeekValue/SeekMaximum.
    double? MusicProgress = null);
