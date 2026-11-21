using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class ClipboardWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.Clipboard;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException("Clipboard content requires a Clipboard widget config.", nameof(config));
        }

        return new ClipboardWidgetContentAdapter(
            config,
            context.LocalizationService,
            context.SettingsService);
    }
}
