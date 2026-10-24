using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private DesktopOrganizationWindow? _desktopOrganizationWindow;

    public DesktopOrganizationWindow ShowDesktopOrganizationWindow(IntPtr ownerHwnd = default)
    {
        CancelBackgroundMemoryCleanup();
        if (_desktopOrganizationWindow is null)
        {
            var window = new DesktopOrganizationWindow();
            _desktopOrganizationWindow = window;
            ThemeService.TrackWindow(window);
            window.OrganizationCompleted += DesktopOrganizationWindow_StateChanged;
            window.OrganizationUndone += DesktopOrganizationWindow_StateChanged;
            window.Closed += DesktopOrganizationWindow_Closed;
        }

        if (ownerHwnd != IntPtr.Zero)
        {
            _desktopOrganizationWindow.SetOwner(ownerHwnd);
        }

        _desktopOrganizationWindow.ShowWindow();
        return _desktopOrganizationWindow;
    }

    private void DesktopOrganizationWindow_StateChanged(object? sender, EventArgs e)
    {
#if DESKBOX_AOT_SMOKE_HARNESS
        _settingsWindow?.RefreshDesktopOrganizationState();
#else
        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HostDesktopOrgRefresh, null);
#endif
    }

    private void DesktopOrganizationWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not DesktopOrganizationWindow window)
        {
            return;
        }

        window.OrganizationCompleted -= DesktopOrganizationWindow_StateChanged;
        window.OrganizationUndone -= DesktopOrganizationWindow_StateChanged;
        window.Closed -= DesktopOrganizationWindow_Closed;
        if (ReferenceEquals(_desktopOrganizationWindow, window))
        {
            _desktopOrganizationWindow = null;
        }

        _settingsWindow?.RefreshDesktopOrganizationState();
        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("desktop-organization-closed");
    }
}
