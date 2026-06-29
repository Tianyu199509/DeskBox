using DeskBox.Models;

namespace DeskBox.Services;

public enum WidgetContentStage
{
    Implemented,
    Placeholder
}

public enum WidgetContentAvailability
{
    Available,
    Planned
}

/// <summary>
/// Describes content-level metadata without deciding whether a widget kind can create a window.
/// </summary>
public sealed record WidgetContentDescriptor(
    WidgetKind WidgetKind,
    string DefaultTitle,
    string DefaultGlyph,
    WidgetContentStage ContentStage,
    bool CanShowInCreateEntry,
    WidgetContentAvailability Availability,
    string StatusLabelKey,
    string StatusDescriptionKey)
{
    public bool HasImplementedContent => ContentStage == WidgetContentStage.Implemented;
    public bool HasPlaceholderContent => ContentStage == WidgetContentStage.Placeholder;
    public bool IsPlaceholderOnly => ContentStage == WidgetContentStage.Placeholder;
    public bool IsAvailable => Availability == WidgetContentAvailability.Available;
    public bool IsPlanned => Availability == WidgetContentAvailability.Planned;
}
