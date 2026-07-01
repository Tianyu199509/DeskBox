using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class PlaceholderWidgetContentProvider : IWidgetContentProvider
{
    public PlaceholderWidgetContentProvider(WidgetKind widgetKind)
    {
        WidgetKind = widgetKind;
    }

    public WidgetKind WidgetKind { get; }

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException(
                $"Placeholder provider for '{WidgetKind}' received '{config.WidgetKind}'.",
                nameof(config));
        }

        var descriptor = context.GetDescriptor(config.WidgetKind);
        if (!descriptor.HasPlaceholderContent)
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' does not have placeholder content.");
        }

        return new PlaceholderWidgetContent(config, descriptor);
    }
}
