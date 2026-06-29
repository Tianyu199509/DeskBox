using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Describes content-level metadata without deciding whether a widget kind can create a window.
/// </summary>
public sealed record WidgetContentDescriptor(
    WidgetKind WidgetKind,
    string DefaultTitle,
    string DefaultGlyph,
    bool HasPlaceholderContent);
