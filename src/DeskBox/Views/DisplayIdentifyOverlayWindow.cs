using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
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
    // Fallback badge blue (Windows accent default) if UISettings cannot be
    // queried; the badge itself paints with the user's system accent color.
    private static readonly Color FallbackBadgeColor = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);

    // High-contrast themes commonly resolve the accent to bright yellow
    // (#FFFF00), where white numerals are unreadable; pick by luminance.
    private static Color ContrastForegroundColor(Color background)
    {
        double luma = background.R * 0.2126 + background.G * 0.7152 + background.B * 0.0722;
        return luma > 127 ? Colors.Black : Colors.White;
    }

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly RectInt32 _bounds;
    private bool _closed;

    private DisplayIdentifyOverlayWindow(int number, RectInt32 monitorBounds, double dpiScale)
    {
        _bounds = monitorBounds;

        Color badgeColor = ResolveBadgeColor();
        var text = new TextBlock
        {
            Text = number.ToString(),
            Foreground = new SolidColorBrush(ContrastForegroundColor(badgeColor)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // The numeral's PHYSICAL height targets ~35% of the monitor (clamped
        // so tiny/large panels stay sane). FontSize is in DIPs and the window
        // renders at this monitor's DPI, so convert: px → DIP by dividing by
        // the per-monitor scale, then px height → font size via 72/96 = 0.75.
        // Dividing by EffectiveDpiScale (instead of the old fixed 72/96) keeps
        // the physical size on any panel instead of shrinking on 4K screens.
        double numeralHeightPx = Math.Clamp(monitorBounds.Height * 0.35, 64, 300);
        double dipHeight = numeralHeightPx / dpiScale;
        text.FontSize = dipHeight * 0.75;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, $"Display {number}");

        var root = new Border
        {
            Background = new SolidColorBrush(badgeColor),
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

        // A monitor reconfiguration (primary switch, hot-plug) between the
        // style surgery and the AppWindow positioning leaves the target
        // monitor coordinate invalid and AppWindow throws E_NOTFOUND — the
        // overlay is a two-second visual, dropping it beats crashing.
        try
        {
            _appWindow.MoveAndResize(monitorBounds);
            _appWindow.Show();
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or ArgumentException)
        {
            _closed = true;
            CloseWindowQuietly();
            App.Log($"[Displays] Identify overlay failed to place on monitor: {ex.Message}");
            return;
        }

        lock (s_activeOverlaysGate)
        {
            s_activeOverlays.Add(this);
        }
    }

    private static readonly object s_activeOverlaysGate = new();
    private static readonly List<DisplayIdentifyOverlayWindow> s_activeOverlays = [];

    /// <summary>
    /// Closes every live identify overlay. Called when the display topology
    /// changes: an overlay positioned on a monitor that is being
    /// reconfigured (primary switch, resolution flip) either sits on stale
    /// coordinates or dies with the monitor — closing them all is both the
    /// promised behavior and the crash guard.
    /// </summary>
    public static void CloseAllOnTopologyChange()
    {
        List<DisplayIdentifyOverlayWindow> snapshots;
        lock (s_activeOverlaysGate)
        {
            snapshots = [.. s_activeOverlays];
            s_activeOverlays.Clear();
        }

        foreach (DisplayIdentifyOverlayWindow overlay in snapshots)
        {
            overlay.CloseWindowQuietly();
        }
    }

    /// <summary>
    /// Flashes the catalog's numbers on every attached monitor and closes the
    /// overlays after roughly three seconds.
    /// </summary>
    public static void IdentifyAll(IReadOnlyList<WidgetScreenInfo> screens)
    {
        // Any topology change during the flash retires the overlays at once.
        App.Current.DisplayTopologyChanged -= OnTopologyChangedCloseAll;
        App.Current.DisplayTopologyChanged += OnTopologyChangedCloseAll;
        foreach (WidgetScreenInfo screen in screens)
        {
            var overlay = new DisplayIdentifyOverlayWindow(
                screen.Number,
                screen.Monitor,
                screen.EffectiveDpiScale);
            if (overlay._closed)
            {
                continue;
            }

            _ = RunAutoDismissAsync(overlay);
        }
    }

    private static void OnTopologyChangedCloseAll() => CloseAllOnTopologyChange();

    private static Color ResolveBadgeColor()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch
        {
            return FallbackBadgeColor;
        }
    }

    private static async Task RunAutoDismissAsync(DisplayIdentifyOverlayWindow overlay)
    {
        await Task.Delay(3000);
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

        overlay.CloseWindowQuietly();
    }

    /// <summary>
    /// Close guarded on every path: a topology change can already be tearing
    /// the window down (the XAML island dies with its monitor), and calling
    /// into a half-dead island is exactly where the engine raises
    /// stowed exceptions.
    /// </summary>
    private void CloseWindowQuietly()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        lock (s_activeOverlaysGate)
        {
            s_activeOverlays.Remove(this);
        }

        try
        {
            _window.Close();
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException)
        {
        }
    }
}
