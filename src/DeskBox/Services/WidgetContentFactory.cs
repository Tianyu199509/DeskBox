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
    public IWidgetContent CreateExistingContent(WidgetConfig config, FrameworkElement view)
    {
        return new ExistingWidgetContent(config, view);
    }

    public IWidgetContent CreatePlaceholderContent(WidgetConfig config)
    {
        if (!IsPlaceholderKind(config.WidgetKind))
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' does not have placeholder content.");
        }

        return new PlaceholderWidgetContent(config);
    }

    public bool CanCreatePlaceholderContent(WidgetKind widgetKind)
    {
        return IsPlaceholderKind(widgetKind);
    }

    private static bool IsPlaceholderKind(WidgetKind widgetKind)
    {
        return widgetKind is WidgetKind.Weather
            or WidgetKind.Todo
            or WidgetKind.Tags
            or WidgetKind.Music
            or WidgetKind.SystemMonitor;
    }
}
