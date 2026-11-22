#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private const string AotGlancePersistenceOwnedWidgetId =
        "aot-5b4b2c1-glance";

    internal async Task<AotGlancePersistenceHost> GetAotGlancePersistenceHostAsync()
    {
        if (!_contentWidgets.TryGetValue(
                AotGlancePersistenceOwnedWidgetId,
                out ContentWidgetWindow? window))
        {
            throw new InvalidOperationException(
                "The owned Glance persistence host is unavailable.");
        }

        await window.ContentReadyTask;
        if (window.CurrentContent is GlanceWidgetContentAdapter adapter &&
            adapter.View is GlanceWidgetContent surface)
        {
            return new AotGlancePersistenceHost(
                surface,
                adapter.ViewModel,
                window.WindowHandle.ToInt64(),
                window.WindowContentRoot?.XamlRoot is not null,
                window.Visible);
        }

        throw new InvalidOperationException(
            "The owned Glance persistence host has the wrong content.");
    }
}

internal sealed record AotGlancePersistenceHost(
    GlanceWidgetContent Surface,
    GlanceWidgetViewModel ViewModel,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);
#endif
