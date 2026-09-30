using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using DrawingPoint = System.Drawing.Point;

namespace DeskBox.Views;

/// <summary>
/// Owns the WinUI tray menu window and its lifetime. H.NotifyIcon's
/// SecondWindow backend calls IsShownInSwitchers without guarding E_NOTIMPL;
/// this host uses HWND tool-window styles instead, including during startup.
/// </summary>
internal sealed class TrayContextMenuHostWindow : Window, IDisposable
{
    private readonly MenuFlyout _menu;
    private readonly Grid _root;
    private readonly IntPtr _hwnd;
    private bool _open;
    private bool _disposed;

    internal TrayContextMenuHostWindow(MenuFlyout menu, IntPtr owner)
    {
        _menu = menu;
        _root = new Grid
        {
            Width = 1,
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        Content = _root;
        _hwnd = WindowNative.GetWindowHandle(this);
        ToolWindowStyle.Apply(_hwnd);
        if (owner != IntPtr.Zero)
        {
            Win32Helper.SetWindowLongPtr(_hwnd, Win32Helper.GWLP_HWNDPARENT, owner);
        }

        int style = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_THICKFRAME |
                   Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME);
        Win32Helper.SetWindowLong(_hwnd, Win32Helper.GWL_STYLE, style | Win32Helper.WS_POPUP);
        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, -32000, -32000, 1, 1,
            Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        // Realize the menu host during startup, off-screen, then keep it hidden
        // between opens. No shell-readiness wait or delayed initialization.
        Activate();
        _root.UpdateLayout();
        Win32Helper.ShowWindow(_hwnd, Win32Helper.SW_HIDE);
        _menu.Opened += MenuOpened;
        _menu.Closed += MenuClosed;
        Activated += HostActivated;
    }

    internal event EventHandler? Opened;

    internal void ShowMenu(DrawingPoint anchor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _menu.Hide();
        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST,
            anchor.X, anchor.Y, 1, 1, Win32Helper.SWP_NOACTIVATE);
        _open = true;
        Activate();
        Win32Helper.SetForegroundWindow(_hwnd);
        _menu.ShowAt(_root, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Auto,
            ShowMode = FlyoutShowMode.Transient
        });
    }

    private void MenuOpened(object? sender, object args) =>
        Opened?.Invoke(this, EventArgs.Empty);

    private void MenuClosed(object? sender, object args)
    {
        _open = false;
        Win32Helper.ShowWindow(_hwnd, Win32Helper.SW_HIDE);
    }

    private void HostActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_open && args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _menu.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _open = false;
        _menu.Hide();
        _menu.Opened -= MenuOpened;
        _menu.Closed -= MenuClosed;
        Activated -= HostActivated;
        Content = null;
        Close();
    }
}
