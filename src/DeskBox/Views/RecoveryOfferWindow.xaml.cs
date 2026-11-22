using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// One-shot decision window shown when a launch detected a wiped
/// configuration with a surviving recovery snapshot. Shown only at an
/// interactive activation — the process then holds the foreground rights a
/// boot-time window cannot rely on (the same condition the elevation notice
/// waits for). Closing the window without a choice counts as an implicit
/// fresh start (decision A): the pending marker is cleared and the offer
/// never returns over the profile the user then rebuilds.
/// </summary>
public sealed partial class RecoveryOfferWindow : Window
{
    private const int DesiredWindowWidth = 500;
    private const int DesiredWindowHeight = 380;
    private const int WindowWorkAreaMargin = 80;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TaskCompletionSource<RecoveryOfferDecision> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _decisionRecorded;

    public Task<RecoveryOfferDecision> Completion => _completion.Task;

    public RecoveryOfferWindow(
        LocalizationService localizationService,
        RecoverySnapshotCandidate candidate,
        ThemeService? themeService = null)
    {
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        if (themeService is not null)
        {
            themeService.TrackWindow(this);
        }
        else
        {
            bool isDark = Win32Helper.IsSystemDarkMode();
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
            }

            Win32Helper.SetWindowTheme(_hWnd, isDark);
        }

        WindowsCompatibilityService.ApplySafeBackdrop(this);
        if (Content is FrameworkElement flowRoot)
        {
            flowRoot.FlowDirection = Models.CustomWidgetLocaleCatalog.IsRightToLeftCulture(localizationService.CurrentCultureName)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }

        string title = localizationService.T("Settings.Recovery.OfferTitle");
        Title = title;
        TitleText.Text = title;
        RestoreButton.Content = localizationService.T("Settings.Recovery.OfferRestoreButton");
        FreshStartButton.Content = localizationService.T("Settings.Recovery.OfferFreshStartButton");

        string body = localizationService.Format(
            "Settings.Recovery.OfferBody",
            candidate.CreatedAtUtc.ToLocalTime().ToString("g"),
            candidate.WidgetCount);
        body += "\n\n" + localizationService.T("Settings.Recovery.OfferPauseNote");
        if (candidate.IsFromNewerAppVersion && !string.IsNullOrWhiteSpace(candidate.AppVersion))
        {
            body += "\n\n" + localizationService.Format(
                "Settings.DataBackup.RestoreNewerVersionWarning",
                candidate.AppVersion);
        }

        BodyText.Text = body;

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

        Closed += (_, _) =>
        {
            SystemBackdrop = null;
            _completion.TrySetResult(RecoveryOfferDecision.Dismissed);
        };
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) =>
        Complete(RecoveryOfferDecision.Restore);

    private void FreshStartButton_Click(object sender, RoutedEventArgs e) =>
        Complete(RecoveryOfferDecision.FreshStart);

    private void Complete(RecoveryOfferDecision decision)
    {
        if (_decisionRecorded)
        {
            return;
        }

        _decisionRecorded = true;
        RestoreButton.IsEnabled = false;
        FreshStartButton.IsEnabled = false;
        _completion.TrySetResult(decision);
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
