using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Content provider for the standalone desktop global search widget.
/// Wires host-owned services and delegates into <see cref="SearchWidgetContentAdapter"/>.
/// </summary>
internal sealed class SearchWidgetContentProvider : IWidgetContentProvider
{
    internal static Func<SettingsService, SearchWidgetHostCallbacks>? HostCallbacksFactory { get; set; }

    public WidgetKind WidgetKind => WidgetKind.Search;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(WidgetConfig config, WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException("Search content requires a Search widget config.", nameof(config));
        }

        SettingsService settingsService = context.SettingsService ?? new SettingsService();
        SearchWidgetHostCallbacks callbacks = HostCallbacksFactory?.Invoke(settingsService)
            ?? new SearchWidgetHostCallbacks();
        return new SearchWidgetContentAdapter(
            config,
            context.LocalizationService,
            settingsService,
            callbacks);
    }
}
