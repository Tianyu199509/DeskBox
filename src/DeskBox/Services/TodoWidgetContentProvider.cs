using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class TodoWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.Todo;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException("Todo content requires a Todo widget config.", nameof(config));
        }

        var store = (context.TodoStoreFactory ?? (widget => new TodoWidgetStore(widget.Id)))(config);
        return context.SettingsService is null
            ? new TodoWidgetContentAdapter(config, store, context.LocalizationService)
            : new TodoWidgetContentAdapter(config, store, context.LocalizationService, context.SettingsService);
    }
}
