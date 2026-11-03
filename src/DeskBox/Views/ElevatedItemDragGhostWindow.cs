using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// A click-through, non-activating topmost visual pill that tracks the cursor
/// during an elevated pointer-driven file/stack drag where WinUI's OLE
/// <c>StartDragAsync</c> cannot run.
/// </summary>
internal sealed class ElevatedItemDragGhostWindow : IDisposable
{
    internal const int CursorOffsetPixels = 14;
    private const double GhostWidthDip = 228;
    private const double GhostHeightDip = 54;
    private const byte TrackingOpacity = 236;

    private readonly object _gate = new();
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Border _surfaceBorder;
    private readonly Image _iconImage;
    private readonly FontIcon _fallbackGlyph;
    private readonly Border _countBadge;
    private readonly TextBlock _countTextBlock;
    private readonly TextBlock _titleTextBlock;
    private readonly TextBlock _captionTextBlock;
    private RectInt32 _lastBounds;
    private bool _hasBounds;
    private bool _visible;
    private bool _closed;
    private bool _lastCanDrop = true;
    private string _lastCaption = string.Empty;

    public ElevatedItemDragGhostWindow()
    {
        bool rounded = WindowsCompatibilityService.SupportsNativeWindowCorners;
        double cornerRadius = rounded ? 8 : 0;
        Color neutralLine = NeutralInteractionBrush.Line(null);

        _iconImage = new Image
        {
            Width = 28,
            Height = 28,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        _fallbackGlyph = new FontIcon
        {
            Glyph = "\uE8A5",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(0xEB, 245, 245, 248)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _countTextBlock = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _countBadge = new Border
        {
            MinWidth = 18,
            Height = 16,
            Padding = new Thickness(4, 0, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -4, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x00, 0x67, 0xC0)),
            CornerRadius = new CornerRadius(rounded ? 8 : 2),
            Visibility = Visibility.Collapsed,
            Child = _countTextBlock
        };

        var iconHost = new Grid
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconHost.Children.Add(_fallbackGlyph);
        iconHost.Children.Add(_iconImage);
        iconHost.Children.Add(_countBadge);

        _titleTextBlock = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _captionTextBlock = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 232, 232, 238)),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };

        var textStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(_titleTextBlock);
        textStack.Children.Add(_captionTextBlock);

        var contentGrid = new Grid
        {
            ColumnSpacing = 10,
            Padding = new Thickness(10, 7, 12, 7)
        };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(textStack, 1);
        contentGrid.Children.Add(iconHost);
        contentGrid.Children.Add(textStack);

        _surfaceBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 30, 30, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(
                0xCC,
                neutralLine.R,
                neutralLine.G,
                neutralLine.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius),
            Child = contentGrid
        };

        _window = new Window
        {
            Title = "DeskBox Item Drag Preview",
            Content = _surfaceBorder
        };

        _hWnd = WindowNative.GetWindowHandle(_window);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        int extendedStyle = Win32Helper.GetWindowLong(
            _hWnd,
            Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
                         Win32Helper.WS_EX_NOACTIVATE |
                         Win32Helper.WS_EX_TRANSPARENT |
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
        _ = Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        _ = Win32Helper.SetLayeredWindowAttributes(
            _hWnd,
            0,
            TrackingOpacity,
            Win32Helper.LWA_ALPHA);
        Win32Helper.SetWindowBorderColor(
            _hWnd,
            unchecked((int)0xFFFFFFFE));
        if (rounded)
        {
            int cornerPreference = 2;
            _ = Win32Helper.TrySetDwmWindowAttribute(
                _hWnd,
                Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference);
        }
    }

    internal IntPtr WindowHandle => _hWnd;

    public void BeginSession(
        ImageSource? icon,
        string fallbackGlyph,
        string title,
        int itemCount)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (icon is not null)
            {
                _iconImage.Source = icon;
                _iconImage.Visibility = Visibility.Visible;
                _fallbackGlyph.Visibility = Visibility.Collapsed;
            }
            else
            {
                _iconImage.Source = null;
                _iconImage.Visibility = Visibility.Collapsed;
                _fallbackGlyph.Glyph = string.IsNullOrWhiteSpace(fallbackGlyph)
                    ? "\uE8A5"
                    : fallbackGlyph;
                _fallbackGlyph.Visibility = Visibility.Visible;
            }

            if (itemCount > 1)
            {
                _countTextBlock.Text = itemCount > 99 ? "99+" : itemCount.ToString();
                _countBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _countBadge.Visibility = Visibility.Collapsed;
            }

            _titleTextBlock.Text = title;
            _lastCaption = string.Empty;
            _captionTextBlock.Text = string.Empty;
            _captionTextBlock.Visibility = Visibility.Collapsed;
            _lastCanDrop = true;
            ApplyBorderTone(canDrop: true);
            _hasBounds = false;
        }
    }

    public void UpdateTracking(
        int cursorX,
        int cursorY,
        string caption,
        bool canDrop)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            string normalizedCaption = caption ?? string.Empty;
            if (!string.Equals(_lastCaption, normalizedCaption, StringComparison.Ordinal))
            {
                _lastCaption = normalizedCaption;
                _captionTextBlock.Text = normalizedCaption;
                _captionTextBlock.Visibility = string.IsNullOrWhiteSpace(normalizedCaption)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (_lastCanDrop != canDrop)
            {
                _lastCanDrop = canDrop;
                ApplyBorderTone(canDrop);
            }

            double scale = Math.Max(0.75, Win32Helper.GetDpiScaleForPoint(cursorX, cursorY));
            int widthPx = Math.Max(120, (int)Math.Ceiling(GhostWidthDip * scale));
            int heightPx = Math.Max(40, (int)Math.Ceiling(GhostHeightDip * scale));

            RectInt32? workArea = null;
            if (Win32Helper.TryGetMonitorWorkArea(
                    cursorX,
                    cursorY,
                    out _,
                    out Win32Helper.RECT monitorWork))
            {
                workArea = new RectInt32(
                    monitorWork.Left,
                    monitorWork.Top,
                    Math.Max(1, monitorWork.Right - monitorWork.Left),
                    Math.Max(1, monitorWork.Bottom - monitorWork.Top));
            }

            RectInt32 bounds = ComputeGhostWindowBounds(
                cursorX,
                cursorY,
                widthPx,
                heightPx,
                workArea);
            MoveAndShowNoLock(bounds);
        }
    }

    /// <summary>
    /// Computes physical ghost window bounds offset from the cursor so the card
    /// stays inside the monitor work area when possible while NEVER covering
    /// the physical cursor point (preserving native <c>WindowFromPoint</c> hit
    /// testing at the hot-spot).
    /// </summary>
    internal static RectInt32 ComputeGhostWindowBounds(
        int cursorX,
        int cursorY,
        int widthPx,
        int heightPx,
        RectInt32? workArea)
    {
        int safeWidth = Math.Max(1, widthPx);
        int safeHeight = Math.Max(1, heightPx);
        int x = cursorX + CursorOffsetPixels;
        int y = cursorY + CursorOffsetPixels;

        if (workArea is { } area)
        {
            int rightLimit = area.X + area.Width;
            int bottomLimit = area.Y + area.Height;
            if (x + safeWidth > rightLimit &&
                cursorX - CursorOffsetPixels - safeWidth >= area.X)
            {
                x = cursorX - CursorOffsetPixels - safeWidth;
            }

            if (y + safeHeight > bottomLimit &&
                cursorY - CursorOffsetPixels - safeHeight >= area.Y)
            {
                y = cursorY - CursorOffsetPixels - safeHeight;
            }
        }

        return new RectInt32(x, y, safeWidth, safeHeight);
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (_closed || !_visible)
            {
                return;
            }

            _ = Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
            _visible = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_visible)
            {
                _ = Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
                _visible = false;
            }
        }

        _window.Close();
    }

    private void MoveAndShowNoLock(RectInt32 bounds)
    {
        bool changed = !_hasBounds ||
                       _lastBounds.X != bounds.X ||
                       _lastBounds.Y != bounds.Y ||
                       _lastBounds.Width != bounds.Width ||
                       _lastBounds.Height != bounds.Height;
        _lastBounds = bounds;
        _hasBounds = true;

        if (!_visible)
        {
            _ = Win32Helper.SetWindowPos(
                _hWnd,
                Win32Helper.HWND_TOPMOST,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_SHOWWINDOW);
            _visible = true;
            return;
        }

        if (!changed)
        {
            return;
        }

        _ = Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_NOZORDER);
    }

    private void ApplyBorderTone(bool canDrop)
    {
        Color line = canDrop
            ? NeutralInteractionBrush.Line(null)
            : Color.FromArgb(0xD8, 0xE0, 0x6C, 0x64);
        _surfaceBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(
            0xCC,
            line.R,
            line.G,
            line.B));
        _captionTextBlock.Foreground = new SolidColorBrush(canDrop
            ? Color.FromArgb(0xCC, 232, 232, 238)
            : Color.FromArgb(0xE0, 244, 162, 156));
    }
}
