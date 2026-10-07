using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// The "identify" affordance from Windows display settings: a solid blue
/// overlay with the monitor's number, shown for a couple of seconds on every
/// attached screen. Opaque by design — a transparent top-level XAML window
/// would render black.
/// </summary>
internal sealed class DisplayIdentifyOverlayWindow
{
    // Windows' own identify badge blue; fixed rather than theme-aware so the
    // overlay reads identically in light and dark themes.
    private static readonly Color BadgeColor = Color.FromArgb(0xFF, 0x00, 0x5C, 0xE6);

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly RectInt32 _bounds;
    private bool _closed;

    private DisplayIdentifyOverlayWindow(int number, RectInt32 monitorBounds)
    {
        _bounds = monitorBounds;

        var text = new TextBlock
        {
            Text = number.ToString(),
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Scale the numeral with the physical monitor so it stays prominent on
        // high-DPI panels instead of inheriting the settings window's scale.
        double numeralHeight = Math.Clamp(monitorBounds.Height * 0.35, 64, 300);
        text.FontSize = numeralHeight * 72.0 / 96.0;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        var root = new Border
        {
            Background = new SolidColorBrush(BadgeColor),
            Child = text
        };
        _window = new Window
        {
            Title = "DeskBox Display Identify",
            Content = root
        };

        _hWnd = WindowNative.GetWindowHandle(_window);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        int extendedStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
                         Win32Helper.WS_EX_NOACTIVATE |
                         Win32Helper.WS_EX_LAYERED |
                         Win32Helper.WS_EX_TOPMOST;
        _ = Win32Helper.SetWindowLongPtr(
            _hWnd,
            Win32Helper.GWL_EXSTYLE,
            new IntPtr(extendedStyle));
        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWL_STYLE, new IntPtr(style));

        _appWindow.MoveAndResize(monitorBounds);
        _appWindow.Show();
    }

    /// <summary>
    /// Flashes the catalog's numbers on every attached monitor and closes the
    /// overlays after roughly two and a half seconds.
    /// </summary>
    public static void IdentifyAll(IReadOnlyList<WidgetScreenInfo> screens)
    {
        foreach (WidgetScreenInfo screen in screens)
        {
            var overlay = new DisplayIdentifyOverlayWindow(screen.Number, screen.Monitor);
            _ = RunAutoDismissAsync(overlay);
        }
    }

    private static async Task RunAutoDismissAsync(DisplayIdentifyOverlayWindow overlay)
    {
        await Task.Delay(2300);
        foreach (byte opacity in new byte[] { 160, 96, 36 })
        {
            if (overlay._closed)
            {
                return;
            }

            _ = Win32Helper.SetLayeredWindowAttributes(
                overlay._hWnd,
                0,
                opacity,
                Win32Helper.LWA_ALPHA);
            await Task.Delay(45);
        }

        overlay.Close();
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _window.Close();
    }
}
