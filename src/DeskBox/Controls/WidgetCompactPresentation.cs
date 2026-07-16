using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public sealed record WidgetCompactPresentation(
    string Title,
    string Summary,
    string Glyph,
    string DropHint,
    ImageSource? Thumbnail = null,
    bool ShowMediaControls = false,
    bool IsPlaying = false,
    bool CanGoPrevious = false,
    bool CanGoNext = false);
