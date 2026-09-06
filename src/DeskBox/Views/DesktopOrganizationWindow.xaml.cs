using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class DesktopOrganizationWindow : Window
{
    private const int DesiredWidth = 900;
    private const int DesiredHeight = 680;
    private const int MinimumWidth = 600;
    private const int MinimumHeight = 500;
    private const int WorkAreaMargin = 64;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr DesktopOrganizationWindowSubclassId = new(1);

    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private bool _hasStarted;
    private bool _allowClose;
    private IntPtr _ownerHwnd;
    private bool _isSubclassInstalled;

    public DesktopOrganizationWindow()
    {
        InitializeComponent();
        Title = App.Current.LocalizationService.T("DesktopOrganization.Window.Title");
        // The XAML solid background is the no-material fallback; clear it so
        // Mica/Acrylic shows and the layered content border reads as a layer.
        if (WindowsCompatibilityService.ApplySafeBackdrop(this) != "Solid")
        {
            RootGrid.Background = null;
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = WindowSubclassProc;
        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        TaskView.OwnerWindowHandle = _hWnd;
        InstallMinimumSizeHook();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        TaskView.CloseRequested += TaskView_CloseRequested;
        TaskView.OrganizationCompleted += TaskView_OrganizationCompleted;
        TaskView.OrganizationUndone += TaskView_OrganizationUndone;
        _appWindow.Closing += AppWindow_Closing;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        Closed += DesktopOrganizationWindow_Closed;
        ResizeAndCenter(windowId);
        ApplyTitleBarColors();
    }

    public event EventHandler? OrganizationCompleted;

    public event EventHandler? OrganizationUndone;

    public void SetOwner(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero || ownerHwnd == _hWnd || ownerHwnd == _ownerHwnd)
        {
            return;
        }

        _ownerHwnd = ownerHwnd;
        _ = Win32Helper.SetWindowLongPtr(
            _hWnd,
            Win32Helper.GWLP_HWNDPARENT,
            ownerHwnd);
        ResizeAndCenter(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
    }

    public void ShowWindow()
    {
        if (!_hasStarted)
        {
            _hasStarted = true;
            TaskView.BeginScan();
        }

        _appWindow.Show();
        Win32Helper.BringWindowTemporarilyToFront(_hWnd);
        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        TaskView.CancelPendingWork();
        Close();
    }

    private void ResizeAndCenter(WindowId displayWindowId)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(
            displayWindowId,
            DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(
            _ownerHwnd != IntPtr.Zero ? _ownerHwnd : _hWnd,
            RootGrid.XamlRoot);
        int width = Math.Clamp(
            (int)Math.Round(DesiredWidth * scale),
            (int)Math.Round(MinimumWidth * scale),
            Math.Max((int)Math.Round(MinimumWidth * scale), workArea.Width - (int)Math.Round(WorkAreaMargin * scale)));
        int height = Math.Clamp(
            (int)Math.Round(DesiredHeight * scale),
            (int)Math.Round(MinimumHeight * scale),
            Math.Max((int)Math.Round(MinimumHeight * scale), workArea.Height - (int)Math.Round(WorkAreaMargin * scale)));
        _appWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        if (TaskView.IsExecutionRunning)
        {
            args.Cancel = true;
            TaskView.CancelExecutionAndCloseWhenSafe();
            return;
        }

        TaskView.CancelPendingWork();
    }

    private void TaskView_CloseRequested(object? sender, EventArgs e)
    {
        if (TaskView.IsExecutionRunning)
        {
            TaskView.CancelExecutionAndCloseWhenSafe();
            return;
        }

        _allowClose = true;
        Close();
    }

    private void TaskView_OrganizationCompleted(object? sender, EventArgs e) =>
        OrganizationCompleted?.Invoke(this, EventArgs.Empty);

    private void TaskView_OrganizationUndone(object? sender, EventArgs e) =>
        OrganizationUndone?.Invoke(this, EventArgs.Empty);

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (TaskView.IsExecutionRunning)
        {
            TaskView.CancelExecutionAndCloseWhenSafe();
        }
        else
        {
            _allowClose = true;
            Close();
        }
    }

    private void AppTitleBar_ActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTitleBarColors();

    private void ApplyTitleBarColors()
    {
        bool isDark = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xA0, 0x10, 0x10, 0x10);
    }

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _windowSubclassProc,
            DesktopOrganizationWindowSubclassId,
            UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(
            _hWnd,
            _windowSubclassProc,
            DesktopOrganizationWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
            minMaxInfo.MinTrackSize.X = Math.Max(
                minMaxInfo.MinTrackSize.X,
                ToPhysicalPixels(MinimumWidth, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(
                minMaxInfo.MinTrackSize.Y,
                ToPhysicalPixels(MinimumHeight, scale));
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(
            1,
            (int)Math.Round(logicalPixels * normalizedScale, MidpointRounding.AwayFromZero));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private void DesktopOrganizationWindow_Closed(object sender, WindowEventArgs args)
    {
        RemoveMinimumSizeHook();
        TaskView.CancelPendingWork();
        TaskView.CloseRequested -= TaskView_CloseRequested;
        TaskView.OrganizationCompleted -= TaskView_OrganizationCompleted;
        TaskView.OrganizationUndone -= TaskView_OrganizationUndone;
        _appWindow.Closing -= AppWindow_Closing;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        Closed -= DesktopOrganizationWindow_Closed;
    }
}
