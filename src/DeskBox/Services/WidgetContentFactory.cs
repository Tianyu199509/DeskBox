using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Creates widget content views without owning host windows or z-order behavior.
/// Future widget kinds can be validated here before they become creatable windows.
/// </summary>
public sealed class WidgetContentFactory
{
    private static readonly IReadOnlyDictionary<WidgetKind, WidgetContentDescriptor> Descriptors =
        new Dictionary<WidgetKind, WidgetContentDescriptor>
        {
            [WidgetKind.File] = new(WidgetKind.File, "DeskBox", "\uE8A5", HasPlaceholderContent: false),
            [WidgetKind.QuickCapture] = new(WidgetKind.QuickCapture, "Quick Capture", "\uE70F", HasPlaceholderContent: false),
            [WidgetKind.Weather] = new(WidgetKind.Weather, "Weather", "\uE706", HasPlaceholderContent: true),
            [WidgetKind.Todo] = new(WidgetKind.Todo, "Todo", "\uE9D5", HasPlaceholderContent: true),
            [WidgetKind.Tags] = new(WidgetKind.Tags, "Tags", "\uE8EC", HasPlaceholderContent: true),
            [WidgetKind.Music] = new(WidgetKind.Music, "Music", "\uEC4F", HasPlaceholderContent: true),
            [WidgetKind.SystemMonitor] = new(WidgetKind.SystemMonitor, "System Monitor", "\uE9D9", HasPlaceholderContent: true)
        };

    public IWidgetContent CreateExistingContent(WidgetConfig config, FrameworkElement view)
    {
        return new ExistingWidgetContent(config, view);
    }

    public IWidgetContent CreatePlaceholderContent(WidgetConfig config)
    {
        var descriptor = GetDescriptor(config.WidgetKind);
        if (!descriptor.HasPlaceholderContent)
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' does not have placeholder content.");
        }

        return new PlaceholderWidgetContent(config, descriptor);
    }

    public WidgetContentDescriptor GetDescriptor(WidgetKind widgetKind)
    {
        if (Descriptors.TryGetValue(widgetKind, out var descriptor))
        {
            return descriptor;
        }

        throw new NotSupportedException($"Widget kind '{widgetKind}' does not have a content descriptor.");
    }

    public bool CanCreatePlaceholderContent(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.HasPlaceholderContent;
    }
}
