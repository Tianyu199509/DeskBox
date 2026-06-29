using DeskBox.Models;

namespace DeskBox.Services;

public enum WidgetContentStage
{
    Implemented,
    Placeholder
}

/// <summary>
/// Describes content-level metadata without deciding whether a widget kind can create a window.
/// </summary>
public sealed record WidgetContentDescriptor(
    WidgetKind WidgetKind,
    string DefaultTitle,
    string DefaultGlyph,
    WidgetContentStage ContentStage,
    bool CanShowInCreateEntry)
{
    public bool HasImplementedContent => ContentStage == WidgetContentStage.Implemented;
    public bool HasPlaceholderContent => ContentStage == WidgetContentStage.Placeholder;
    public bool IsPlaceholderOnly => ContentStage == WidgetContentStage.Placeholder;
}
