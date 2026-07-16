using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

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
    bool IsAttention = false,
    string LiveStateKey = "");
