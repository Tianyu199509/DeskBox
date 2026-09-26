#if DESKBOX_NATIVE_AOT
using DeskBox.Controls.WidgetContents;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const string AotQuickCapturePersistenceOwnedWidgetId =
        "aot-5b4b2b1-quick-capture";

    internal async Task<AotQuickCapturePersistenceHost> GetAotQuickCapturePersistenceHostAsync(
        string widgetId)
    {
        if (!string.Equals(
                widgetId,
                AotQuickCapturePersistenceOwnedWidgetId,
                StringComparison.Ordinal) ||
            !_contentWidgets.TryGetValue(widgetId, out ContentWidgetWindow? window))
        {
            throw new InvalidOperationException(
                $"The owned Quick Capture host '{widgetId}' is unavailable.");
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is QuickCaptureWidgetContentAdapter adapter &&
            adapter.View is QuickCaptureSurfaceContent surface)
        {
            return new AotQuickCapturePersistenceHost(
                surface,
                window.WindowHandle.ToInt64(),
                window.WindowContentRoot?.XamlRoot is not null,
                window.Visible);
        }

        throw new InvalidOperationException(
            $"The owned Quick Capture host '{widgetId}' has the wrong content.");
    }
}

internal sealed record AotQuickCapturePersistenceHost(
    QuickCaptureSurfaceContent Surface,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);
#endif
