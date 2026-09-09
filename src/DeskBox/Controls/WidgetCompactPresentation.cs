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
    double? MusicProgress = null,
    // Null inherits the widget window's semantic foreground palette. A
    // presentation may opt into an explicit theme only when it owns a
    // background whose contrast cannot be represented by those brushes.
    bool? UseLightForeground = null,
    // Multiplies the standard full-bleed readability scrim. Glance feeds its
    // own None/Soft/Strong readability setting here; the default preserves
    // existing full-bleed presentations such as Music.
    double FullBleedOverlayOpacity = 1.0,
    // Glance uses the same even readability mask as its expanded image view.
    // Other full-bleed presentations retain the directional text scrim.
    bool UseUniformFullBleedOverlay = false,
    // Multiplies only the full-bleed image. Text, controls, readability masks,
    // and compact transition animations remain on independent layers.
    double FullBleedBackgroundOpacity = 1.0,
    // Optional accessible name for the capsule's primary action. The legacy
    // fallback remains the todo completion label for existing presentations.
    string PrimaryActionLabel = "",
    // 高频文本（例如倒计时）只做原位增量更新，不重新布局、重启跑马灯
    // 或重建操作按钮。离散状态变化仍通过 LiveStateKey 触发完整刷新。
    bool IsLiveTextUpdate = false);
