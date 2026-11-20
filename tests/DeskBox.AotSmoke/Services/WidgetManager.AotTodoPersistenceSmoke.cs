#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const string AotTodoPersistenceOwnedWidgetId =
        "aot-5b4b2b2a-todo";
    private const string AotTodoStepsPersistenceOwnedWidgetId =
        "aot-5b4b2b2b1-todo-steps";
    private const string AotTodoAttachmentsPersistenceOwnedWidgetId =
        "aot-5b4b2b2b2-todo-attachments";

    internal async Task<AotTodoPersistenceHost> GetAotTodoPersistenceHostAsync(
        string widgetId)
    {
        bool isOwnedTodoWidget = string.Equals(
            widgetId,
            AotTodoPersistenceOwnedWidgetId,
            StringComparison.Ordinal) || string.Equals(
            widgetId,
            AotTodoStepsPersistenceOwnedWidgetId,
            StringComparison.Ordinal) || string.Equals(
            widgetId,
            AotTodoAttachmentsPersistenceOwnedWidgetId,
            StringComparison.Ordinal);
        if (!isOwnedTodoWidget ||
            !_contentWidgets.TryGetValue(widgetId, out ContentWidgetWindow? window))
        {
            throw new InvalidOperationException(
                $"The owned Todo host '{widgetId}' is unavailable.");
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is TodoWidgetContentAdapter adapter &&
            adapter.View is TodoWidgetContent surface)
        {
            return new AotTodoPersistenceHost(
                surface,
                window.WindowHandle.ToInt64(),
                window.WindowContentRoot?.XamlRoot is not null,
                window.Visible);
        }

        throw new InvalidOperationException(
            $"The owned Todo host '{widgetId}' has the wrong content.");
    }
}

internal sealed record AotTodoPersistenceHost(
    TodoWidgetContent Surface,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);
#endif
