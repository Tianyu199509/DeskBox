using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Views;

/// <summary>Body/button shape the elevation notice takes.</summary>
public enum ElevationNoticeVariant
{
    /// <summary>DeskBox is elevated above the shell. Offer a one-click exit
    /// of the elevated instance; the user restarts DeskBox normally (a
    /// plain launch lands at the shell's standard-user level). The notice
    /// deliberately no longer relaunches the app itself: the shell-parented
    /// hand-over spawned a Medium child whose startup state differed from a
    /// normal launch (silent-startup hide followed by a raised reveal), and
    /// that combination starved drags on the current tree. Self-serve keeps
    /// every restarted instance on the plain, field-proven path.</summary>
    ExitElevatedInstance,

    /// <summary>The shell runs above this process: tray creation and
    /// cross-level drag are UIPI-blocked and the app cannot change the
    /// shell's token. Explain the degraded session instead.</summary>
    ExplainShellAboveApp,

    /// <summary>DeskBox and the shell are both elevated (built-in
    /// Administrator account, UAC off, or another elevated desktop — see
    /// <see cref="FullyElevatedSessionAttribution"/>). Same-level drag-in
    /// and the tray keep working, but WinUI cannot start XAML drags in an
    /// elevated process, so drag-out is unavailable. Explain the state, the
    /// limit, and the system-level restore path — there is no DeskBox-side
    /// action to offer.</summary>
    ExplainFullyElevated
}

/// <summary>
/// One-shot notice shown when DeskBox runs elevated. The asymmetric shapes:
/// an elevated DeskBox under a standard shell can neither receive OLE drops
/// nor start XAML drags, so it offers a one-click exit; a standard DeskBox
/// under an elevated shell has its tray blocked and gets an explanation.
/// Since 2026-10-22 same-level elevated desktops are no longer silent —
/// WinUI forbids XAML drag-out in an elevated process, so they get the
/// one-time informed explanation with the restore path (ack-persisted in
/// settings; plain same-level desktops never reach this window).
/// </summary>
public sealed partial class ElevationNoticeWindow : Window
{
    private const int DesiredWindowWidth = 500;
    private const int DesiredWindowHeight = 360;
    private const int WindowWorkAreaMargin = 80;

    private const string FullyElevatedLearnMoreUrlChinese =
        "https://learn.microsoft.com/zh-cn/windows/security/application-security/application-control/user-account-control/settings-and-configuration";
    private const string FullyElevatedLearnMoreUrlEnglish =
        "https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/settings-and-configuration";

    // Copy-to-clipboard assistance only: DeskBox never writes system policy
    // itself. The command is the documented registry form of the Admin
    // Approval Mode policy for the built-in Administrator account.
    private const string FullyElevatedRegistryCommand =
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v FilterAdministratorToken /t REG_DWORD /d 1 /f";

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private bool _exitStarted;

    /// <summary>
    /// Pure variant selection, testable without a window: the notice never
    /// offers an action the environment cannot deliver, and never claims a
    /// DeskBox-side fix where only a system-level change helps.
    /// </summary>
    internal static ElevationNoticeVariant SelectVariant(StartupElevationAssessment assessment) =>
        assessment.Mismatch switch
        {
            ShellIntegrityMismatch.ShellAboveApp => ElevationNoticeVariant.ExplainShellAboveApp,
            ShellIntegrityMismatch.None => ElevationNoticeVariant.ExplainFullyElevated,
            _ => ElevationNoticeVariant.ExitElevatedInstance
        };

    /// <summary>
    /// Home editions ship without secpol.msc; launching it there surfaces a
    /// Windows error box instead of the policy editor, so the button only
    /// renders where the tool exists. Core covers plain/N/Single
    /// Language/Country Specific Home variants; CoreConnected* are the
    /// Bing-edition Home SKUs and CoreARM64 the Home ARM64 image.
    /// </summary>
    internal static bool IsHomeEdition(string? editionId) =>
        editionId is "Core" or "CoreN" or "CoreSingleLanguage"
            or "CoreSingleLanguageN" or "CoreCountrySpecific" or "CoreCountrySpecificN"
            or "CoreConnected" or "CoreConnectedN"
            or "CoreConnectedSingleLanguage" or "CoreConnectedCountrySpecific"
            or "CoreARM64";

    internal static string? ReadWindowsEditionId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string;
        }
        catch
        {
            return null;
        }
    }

    public ElevationNoticeWindow(
        LocalizationService localizationService,
        StartupElevationAssessment assessment)
    {
        InitializeComponent();

        WindowsCompatibilityService.ApplySafeBackdrop(this);

        Title = localizationService.T("Startup.ElevationNotice.Title");
        TitleText.Text = localizationService.T("Startup.ElevationNotice.Title");
        ExitButton.Content = localizationService.T("Startup.ElevationNotice.ExitButton");
        OkButton.Content = localizationService.T("Startup.ElevationNotice.OkButton");

        ElevationNoticeVariant variant = SelectVariant(assessment);
        if (variant == ElevationNoticeVariant.ExplainFullyElevated)
        {
            ConfigureFullyElevated(localizationService, assessment);
        }
        else
        {
            BodyText.Text = variant == ElevationNoticeVariant.ExplainShellAboveApp
                ? localizationService.T("Startup.ElevationNotice.BodyShellAboveApp")
                : localizationService.T("Startup.ElevationNotice.BodyExitAndRestart");

            if (variant == ElevationNoticeVariant.ExitElevatedInstance)
            {
                ExitButton.Visibility = Visibility.Visible;
                OkButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ExitButton.Visibility = Visibility.Collapsed;
                OkButton.Visibility = Visibility.Visible;
            }
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

    private void ConfigureFullyElevated(
        LocalizationService localizationService,
        StartupElevationAssessment assessment)
    {
        FullyElevatedSessionAttribution attribution = assessment.FullyElevatedAttribution;
        bool uacDisabled = attribution == FullyElevatedSessionAttribution.UacDisabled;

        string title = localizationService.T("Startup.FullyElevatedNotice.Title");
        Title = title;
        TitleText.Text = title;

        BodyText.Text = localizationService.T(attribution switch
        {
            FullyElevatedSessionAttribution.BuiltInAdminAccount =>
                "Startup.FullyElevatedNotice.BodyBuiltInAdmin",
            FullyElevatedSessionAttribution.UacDisabled =>
                "Startup.FullyElevatedNotice.BodyUacDisabled",
            _ => "Startup.FullyElevatedNotice.BodyGeneric"
        });

        DetailsHeaderText.Text = localizationService.T(uacDisabled
            ? "Startup.FullyElevatedNotice.DetailsHeaderUac"
            : "Startup.FullyElevatedNotice.DetailsHeader");
        DetailsBodyText.Text = BuildFullyElevatedDetailsBody(localizationService, attribution);

        LearnMoreLink.Content = localizationService.T("Startup.FullyElevatedNotice.LearnMoreLink");
        LearnMoreLink.NavigateUri = new Uri(localizationService.IsChinese
            ? FullyElevatedLearnMoreUrlChinese
            : FullyElevatedLearnMoreUrlEnglish);

        // The FilterAdministratorToken registry path and the secpol policy
        // only exist for the built-in Administrator account (RID 500) — on a
        // generic elevated desktop following them changes nothing, and this
        // window never offers an action the environment cannot deliver.
        bool builtInAdmin = attribution == FullyElevatedSessionAttribution.BuiltInAdminAccount;

        // Copy-to-clipboard assistance is built-in-admin only (see above).
        CopyRegCommandButton.Content = localizationService.T("Startup.FullyElevatedNotice.CopyRegCommandButton");
        CopyRegCommandButton.Visibility = builtInAdmin
            ? Visibility.Visible
            : Visibility.Collapsed;

        // secpol additionally needs a non-Home edition; a null/unknown
        // EditionID fails closed (button hidden) rather than risking the
        // Windows error box this gate exists to prevent.
        string? editionId = ReadWindowsEditionId();
        OpenSecpolButton.Content = localizationService.T("Startup.FullyElevatedNotice.OpenSecpolButton");
        OpenSecpolButton.Visibility = builtInAdmin && editionId is not null && !IsHomeEdition(editionId)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Reuses the settings-page key: same action, same wording.
        OpenUacSettingsButton.Content = localizationService.T("Settings.DragDropPermission.OpenUacButton");
        OpenUacSettingsButton.Visibility = uacDisabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        DetailsPanel.Visibility = Visibility.Visible;
        OkButton.Content = localizationService.T("Common.GotIt");
        OkButton.Visibility = Visibility.Visible;
        ExitButton.Visibility = Visibility.Collapsed;
    }

    private static string BuildFullyElevatedDetailsBody(
        LocalizationService localizationService,
        FullyElevatedSessionAttribution attribution)
    {
        if (attribution == FullyElevatedSessionAttribution.UacDisabled)
        {
            return localizationService.T("Startup.FullyElevatedNotice.StepUacSlider");
        }

        // Built-in Administrator: the Admin Approval Mode policy applies, so
        // both the secpol and the registry routes are real. Generic elevated
        // desktops (a hand-elevated Explorer, say) get neither — the policy
        // only changes RID-500 tokens — leaving the account switch as the
        // one honest step.
        if (attribution == FullyElevatedSessionAttribution.BuiltInAdminAccount)
        {
            return string.Join(
                Environment.NewLine + Environment.NewLine,
                localizationService.T("Startup.FullyElevatedNotice.StepSecpol"),
                localizationService.T("Startup.FullyElevatedNotice.StepRegistry"),
                localizationService.T("Startup.FullyElevatedNotice.StepAlternateAccount"));
        }

        return localizationService.T("Startup.FullyElevatedNotice.StepAlternateAccount");
    }

    private async void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        ExitButton.IsEnabled = false;
        OkButton.IsEnabled = false;

        // The full shutdown path carries the deadline and hard-exit
        // backstops that guarantee process death (measured 0.5 s), so the
        // single-instance mutex frees for the user's normal restart.
        App.Log("[Elevation] Exiting elevated instance from notice; user restart expected");
        Close();
        await App.Current.ShutdownForRestartAsync();
        App.Log("[Elevation] ShutdownForRestartAsync returned after notice exit");
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSecpolButton_Click(object sender, RoutedEventArgs e)
    {
        // Task.Run matches the settings-page launcher: ShellExecute off the
        // UI thread.
        _ = Task.Run(() => Win32Helper.OpenFile("secpol.msc"));
    }

    private void OpenUacSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        IntPtr ownerHwnd = _hWnd;
        _ = Task.Run(() => DragDropPermissionService.OpenUserAccountControlSettings(ownerHwnd));
    }

    private void CopyRegCommandButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(FullyElevatedRegistryCommand);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            App.Log($"[Elevation] Copy registry command failed: {ex.Message}");
        }
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
