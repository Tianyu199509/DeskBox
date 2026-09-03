using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class PomodoroWidgetContentProvider : IWidgetContentProvider
{
    public WidgetKind WidgetKind => WidgetKind.Pomodoro;

    public bool CanCreateDetachedContent => true;

    public IWidgetContent CreateDetachedContent(
        WidgetConfig config,
        WidgetContentProviderContext context)
    {
        if (config.WidgetKind != WidgetKind)
        {
            throw new ArgumentException(
                "Pomodoro content requires a Pomodoro widget config.",
                nameof(config));
        }

        return new PomodoroWidgetContentAdapter(
            config,
            context.LocalizationService,
            context.SettingsService);
    }
}
