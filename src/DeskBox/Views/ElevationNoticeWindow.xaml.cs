using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// One-shot notice shown when DeskBox was launched elevated. Elevated
/// DeskBox can neither receive OLE drops from Explorer (UIPI) nor start XAML
/// drags, so an elevated launch either offers a one-click unelevated restart
/// (when a standard-user context exists) or explains the limitation.
/// </summary>
public sealed partial class ElevationNoticeWindow : Window
{
    private const int DesiredWindowWidth = 500;
    private const int DesiredWindowHeight = 360;
    private const int WindowWorkAreaMargin = 80;

    private readonly StartupElevationAssessment _assessment;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private bool _relaunchAttempted;

    public ElevationNoticeWindow(
        LocalizationService localizationService,
        StartupElevationAssessment assessment)
    {
        _assessment = assessment;
        InitializeComponent();

        WindowsCompatibilityService.ApplySafeBackdrop(this);

        Title = localizationService.T("Startup.ElevationNotice.Title");
        TitleText.Text = localizationService.T("Startup.ElevationNotice.Title");
        BodyText.Text = _assessment.CanRelaunchUnelevated
            ? localizationService.T("Startup.ElevationNotice.BodyCanRelaunch")
            : localizationService.T("Startup.ElevationNotice.BodyCannotRelaunch");
        RelaunchButton.Content = localizationService.T("Startup.ElevationNotice.RelaunchButton");
        ContinueButton.Content = localizationService.T("Startup.ElevationNotice.ContinueButton");
        OkButton.Content = localizationService.T("Startup.ElevationNotice.OkButton");

        if (!_assessment.CanRelaunchUnelevated)
        {
            RelaunchButton.Visibility = Visibility.Collapsed;
            ContinueButton.Visibility = Visibility.Collapsed;
            OkButton.Visibility = Visibility.Visible;
        }

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ResizeAndCenterForDisplay(windowId);
    }

    private async void RelaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_relaunchAttempted)
        {
            return;
        }

        _relaunchAttempted = true;
        RelaunchButton.IsEnabled = false;
        ContinueButton.IsEnabled = false;

        // A persisted RUNASADMIN compatibility layer would relaunch elevated
        // again, so clear it before handing over.
        _ = DragDropPermissionService.ClearElevationCompatibilityFlags();

        string arguments = $"--await-parent-exit {Environment.ProcessId}";
        if (DragDropPermissionService.TryRelaunchAsExplorerUser(arguments))
        {
            App.Log("[Elevation] Unelevated relaunch scheduled; shutting down elevated instance");
            // A bare Application.Exit() called inline from an input handler is
            // deferred through the XAML message loop and never consumed — the
            // process stayed alive with its tray second window closed (real
            // machine: relaunch watchdog timed out and reactivated the
            // elevated zombie). The full shutdown path has the deadline and
            // Environment.Exit fallbacks that guarantee process death.
            Close();
            await App.Current.ShutdownForRestartAsync();
            // Not reached on a healthy exit; surface the state if it ever is.
            App.Log("[Elevation] ShutdownForRestartAsync returned after relaunch");
        }

        App.Log("[Elevation] Unelevated relaunch failed from notice");
        BodyText.Text = App.Current.LocalizationService.T(
            "Settings.DragDropPermission.RelaunchFailedBody");
        RelaunchButton.Visibility = Visibility.Collapsed;
        ContinueButton.IsEnabled = true;
        ContinueButton.Focus(FocusState.Programmatic);
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ResizeAndCenterForDisplay(Microsoft.UI.WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
        int width = ToPhysicalPixels(DesiredWindowWidth, scale);
        int height = ToPhysicalPixels(DesiredWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        width = Math.Min(width, Math.Max(0, workArea.Width - workAreaMargin));
        height = Math.Min(height, Math.Max(0, workArea.Height - workAreaMargin));

        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        return (int)Math.Round(logicalPixels * scale);
    }
}
