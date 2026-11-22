using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Windows 11 native-styled host approval dialog for external AI Agent (MCP/CLI)
/// widget commits that overwrite an existing widget, clear user data, or include
/// scripts / host-access capabilities. Initial keyboard focus lands on <c>Deny</c>
/// so an in-flight keystroke in an external IDE can never accidentally approve,
/// and unattended prompts fail closed after 60 seconds.
/// </summary>
internal sealed partial class AgentCommitApprovalWindow : Window
{
    private const int TimeoutSeconds = 60;
    private const int DesiredWindowWidth = 500;
    private const int DesiredWindowHeight = 388;
    private const int WindowWorkAreaMargin = 80;

    private readonly LocalizationService _localization;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DispatcherTimer? _timer;
    private int _remainingSeconds = TimeoutSeconds;
    private bool _decisionRecorded;
    private bool _approved;

    public Task<bool> Result => _completion.Task;

    public AgentCommitApprovalWindow(
        LocalizationService localization,
        AgentCommitApprovalRequest request,
        ThemeService? themeService = null)
    {
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(request);

        _localization = localization;
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        if (themeService is not null)
        {
            themeService.TrackWindow(this);
        }
        else
        {
            bool isDark = Win32Helper.IsSystemDarkMode();
            RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
            Win32Helper.SetWindowTheme(_hWnd, isDark);
        }

        WindowsCompatibilityService.ApplySafeBackdrop(this);
        RootGrid.FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(localization.CurrentCultureName)
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        string title = localization.T("CustomWidget.AgentBridge.Approval.Title");
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = localization.T("CustomWidget.AgentBridge.Approval.Subtitle");
        TrustHintText.Text = localization.T("CustomWidget.AgentBridge.Approval.Body");
        AllowButton.Content = localization.T("CustomWidget.AgentBridge.Approval.Allow");
        DenyButton.Content = localization.T("CustomWidget.AgentBridge.Approval.Deny");

        string displayName = !string.IsNullOrWhiteSpace(request.WidgetName)
            ? request.WidgetName.Trim()
            : request.WidgetId;
        WidgetNameText.Text = displayName;
        WidgetIdText.Text = request.WidgetId;
        WidgetIdBadge.Visibility = string.Equals(displayName, request.WidgetId, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ScopeGlyphIcon.Glyph = request.OverwritesExisting ? "\uE895" : "\uE710";
        ScopeReasonText.Text = localization.T(request.OverwritesExisting
            ? "CustomWidget.AgentBridge.Approval.ReasonOverwrite"
            : "CustomWidget.AgentBridge.Approval.ReasonCreate");

        if (request.DeclaresPrivilegedCapabilities)
        {
            PrivilegedRow.Visibility = Visibility.Visible;
            PrivilegedReasonText.Text = localization.T("CustomWidget.AgentBridge.Approval.ReasonPrivileged");
        }

        if (request.ClearsExistingData)
        {
            DataLossBanner.Visibility = Visibility.Visible;
            DataLossReasonText.Text = localization.T("CustomWidget.AgentBridge.Approval.ReasonDataLoss");
            HeaderGlyphIcon.Glyph = "\uE7BA";
        }

        UpdateTimeoutLabel();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        ResizeAndCenterForDisplay(windowId);

        RootGrid.Loaded += (_, _) =>
        {
            AutoFitHeightToMeasuredContent(windowId);
            DenyButton.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) =>
        {
            StopAndDetachTimer();
            SystemBackdrop = null;
            _completion.TrySetResult(_approved);
        };
    }

    public new void Activate()
    {
        base.Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
        DenyButton.Focus(FocusState.Programmatic);

        StopAndDetachTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnCountdownTick;
        _timer.Start();
    }

    private void StopAndDetachTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnCountdownTick;
        _timer = null;
    }

    private void OnCountdownTick(object? sender, object e)
    {
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            Complete(allow: false);
            return;
        }

        UpdateTimeoutLabel();
    }

    private void UpdateTimeoutLabel() =>
        TimeoutNoteText.Text = _localization.Format(
            "CustomWidget.AgentBridge.Approval.TimeoutNote",
            _remainingSeconds);

    private void AllowButton_Click(object sender, RoutedEventArgs e) =>
        Complete(allow: true);

    private void DenyButton_Click(object sender, RoutedEventArgs e) =>
        Complete(allow: false);

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(allow: false);
        }
    }

    private void Complete(bool allow)
    {
        if (_decisionRecorded)
        {
            return;
        }

        _decisionRecorded = true;
        _approved = allow;
        _timer?.Stop();
        AllowButton.IsEnabled = false;
        DenyButton.IsEnabled = false;
        Close();
    }

    private void AutoFitHeightToMeasuredContent(WindowId windowId)
    {
        try
        {
            RectInt32 workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
            double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
            if (scale <= 0)
            {
                scale = 1.0;
            }

            int currentClientWidthPx = _appWindow.ClientSize.Width;
            int currentClientHeightPx = _appWindow.ClientSize.Height;
            double clientWidthDip = currentClientWidthPx > 0
                ? currentClientWidthPx / scale
                : DesiredWindowWidth;
            double innerAvailableWidthDip = Math.Max(220, clientWidthDip - RootGrid.Padding.Left - RootGrid.Padding.Right);

            HeaderGrid.Measure(new Windows.Foundation.Size(innerAvailableWidthDip, double.PositiveInfinity));
            BodyContentStack.Measure(new Windows.Foundation.Size(innerAvailableWidthDip, double.PositiveInfinity));
            FooterGrid.Measure(new Windows.Foundation.Size(innerAvailableWidthDip, double.PositiveInfinity));

            double desiredClientDip =
                RootGrid.Padding.Top +
                RootGrid.Padding.Bottom +
                (RootGrid.RowSpacing * 2) +
                HeaderGrid.DesiredSize.Height +
                BodyContentStack.DesiredSize.Height +
                FooterGrid.DesiredSize.Height +
                6;

            int desiredClientPx = ToPhysicalPixels((int)Math.Ceiling(desiredClientDip), scale);
            int maxWindowHeightPx = Math.Max(0, workArea.Height - ToPhysicalPixels(WindowWorkAreaMargin, scale));
            int nonClientDeltaPx = Math.Max(0, _appWindow.Size.Height - currentClientHeightPx);
            int maxClientHeightPx = Math.Max(currentClientHeightPx, maxWindowHeightPx - nonClientDeltaPx);
            int targetClientHeightPx = Math.Clamp(desiredClientPx, currentClientHeightPx, maxClientHeightPx);
            if (targetClientHeightPx > currentClientHeightPx)
            {
                _appWindow.ResizeClient(new SizeInt32(currentClientWidthPx, targetClientHeightPx));
                int totalW = _appWindow.Size.Width;
                int totalH = _appWindow.Size.Height;
                _appWindow.Move(new PointInt32(
                    workArea.X + Math.Max(0, (workArea.Width - totalW) / 2),
                    workArea.Y + Math.Max(0, (workArea.Height - totalH) / 2)));
            }

            RootGrid.InvalidateMeasure();
        }
        catch
        {
        }
    }

    private void ResizeAndCenterForDisplay(WindowId windowId)
    {
        RectInt32 workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
        int extraHeight = (DataLossBanner.Visibility == Visibility.Visible ? 44 : 0) +
                          (PrivilegedRow.Visibility == Visibility.Visible ? 28 : 0);
        int width = ToPhysicalPixels(DesiredWindowWidth, scale);
        int height = ToPhysicalPixels(DesiredWindowHeight + extraHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        width = Math.Min(width, Math.Max(0, workArea.Width - workAreaMargin));
        height = Math.Min(height, Math.Max(0, workArea.Height - workAreaMargin));

        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale) =>
        (int)Math.Round(logicalPixels * scale);
}
