#if DESKBOX_NATIVE_AOT
using DeskBox.Controls.WidgetContents;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    internal Task<AotLocalFileSurfaceHost> GetAotLocalFileSurfaceHostAsync()
    {
        return GetAotLocalFileSurfaceHostAsync(
            AotLocalFileSurfaceFixture.OwnedWidgetId);
    }

    internal async Task<AotLocalFileSurfaceHost> GetAotLocalFileSurfaceHostAsync(
        string ownedWidgetId)
    {
        if (!_fileWidgets.TryGetValue(
                ownedWidgetId,
                out FileWidgetSession? session))
        {
            throw new InvalidOperationException(
                "The owned local-file surface host is unavailable.");
        }

        await session.Host.ContentReadyTask;
        if (!ReferenceEquals(session.Host.CurrentContent, session.Content))
        {
            throw new InvalidOperationException(
                "The owned local-file host does not expose its registered product surface.");
        }

        return new AotLocalFileSurfaceHost(
            session.Content.RequireSurface(),
            session.ViewModel,
            session.Host.WindowHandle.ToInt64(),
            session.Host.WindowContentRoot?.XamlRoot is not null,
            session.Host.Visible);
    }

    internal async Task<AotNativeDropSurfaceHost>
        GetAotNativeDropSurfaceHostAsync(string ownedWidgetId)
    {
        if (!_fileWidgets.TryGetValue(
                ownedWidgetId,
                out FileWidgetSession? session))
        {
            throw new InvalidOperationException(
                "The owned native-drop surface host is unavailable.");
        }

        await session.Host.ContentReadyTask;
        if (!ReferenceEquals(session.Host.CurrentContent, session.Content))
        {
            throw new InvalidOperationException(
                "The native-drop host does not expose its registered product surface.");
        }

        return new AotNativeDropSurfaceHost(
            session.Host,
            session.Content.RequireSurface(),
            session.ViewModel,
            session.Host.WindowHandle.ToInt64(),
            session.Host.WindowContentRoot?.XamlRoot is not null,
            session.Host.Visible);
    }
}

internal sealed record AotLocalFileSurfaceHost(
    FileSurfaceContent Surface,
    WidgetViewModel ViewModel,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);

internal sealed record AotNativeDropSurfaceHost(
    ContentWidgetWindow Window,
    FileSurfaceContent Surface,
    WidgetViewModel ViewModel,
    long WindowHandle,
    bool HasXamlRoot,
    bool Visible);
#endif
