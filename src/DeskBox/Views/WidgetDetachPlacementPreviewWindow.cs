using DeskBox.Controls;
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

internal readonly record struct WidgetDetachPreviewAppearance(
    string Caption,
    double CornerRadius,
    bool IsDark,
    Color AccentColor,
    Color? CustomBackgroundColor,
    double BorderThickness,
    Color BorderColor,
    string MemberTitle = "",
    string MemberGlyph = "\uE8A5",
    string MemberIconKind = WidgetTitleIconKindNames.Default,
    string MemberIconMode = WidgetTitleIconModeNames.Color,
    string MemberCustomEmoji = "")
{
    public static WidgetDetachPreviewAppearance CreateDefault(
        string caption,
        double cornerRadius)
    {
        bool isDark = NeutralInteractionBrush.IsDarkTheme(null);
        Color accent = AccentColorHelper.DefaultAccentColor;
        Color tone = NeutralInteractionBrush.Line(null);
        return new WidgetDetachPreviewAppearance(
            caption,
            cornerRadius,
            isDark,
            accent,
            CustomBackgroundColor: null,
            BorderThickness: 1.0,
            BorderColor: Color.FromArgb(0x68, tone.R, tone.G, tone.B));
    }
}

/// <summary>
/// A click-through, non-activating placement silhouette used while a member is
/// dragged out of a widget group. Draws a lightweight translucent landing frame
/// with a simulated member header strip and a centered 8 DIP Fluent flyout chip,
/// avoiding live Mica/Acrylic backdrop controllers so cross-thread native
/// positioning stays silky-smooth while OLE or pointer capture drives the drag.
/// </summary>
internal sealed class WidgetDetachPlacementPreviewWindow : IDisposable
{
    private const byte TrackingOpacity = 255;
    private const byte CommittedOpacity = 255;

    private readonly object _gate = new();
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Border _surfaceBorder;
    private readonly Grid _headerRow;
    private readonly Border _headerDivider;
    private readonly WidgetTitleIcon _memberIcon;
    private readonly TextBlock _memberTitleTextBlock;
    private readonly Border _badgeBorder;
    private readonly FontIcon _badgeIcon;
    private readonly TextBlock _captionTextBlock;
    private readonly SolidTintSystemBackdrop _transparentBackdrop;
    private RectInt32 _lastBounds;
    private byte _opacity = TrackingOpacity;
    private bool _hasBounds;
    private bool _visible;
    private bool _closed;

    public WidgetDetachPlacementPreviewWindow(
        WidgetDetachPreviewAppearance appearance)
    {
        double surfaceRadius = Math.Clamp(appearance.CornerRadius, 0, 32);
        var innerLayout = new Grid();
        innerLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        innerLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _memberIcon = new WidgetTitleIcon
        {
            IconSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        _memberTitleTextBlock = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetTextShadow.SetCast(_memberTitleTextBlock, false);

        var headerIdentity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerIdentity.Children.Add(_memberIcon);
        headerIdentity.Children.Add(_memberTitleTextBlock);

        _headerDivider = new Border
        {
            Height = 1,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };

        _headerRow = new Grid();
        _headerRow.Children.Add(headerIdentity);
        _headerRow.Children.Add(_headerDivider);
        Grid.SetRow(_headerRow, 0);

        _badgeIcon = new FontIcon
        {
            Glyph = "\uE8A7",
            Width = 16,
            Height = 16,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _captionTextBlock = new TextBlock
        {
            Text = appearance.Caption,
            MaxWidth = 240,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        WidgetTextShadow.SetCast(_captionTextBlock, false);
        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        badgeRow.Children.Add(_badgeIcon);
        badgeRow.Children.Add(_captionTextBlock);

        _badgeBorder = new Border
        {
            MinHeight = 34,
            Margin = new Thickness(12),
            Padding = new Thickness(12, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = badgeRow
        };
        Grid.SetRow(_badgeBorder, 1);

        innerLayout.Children.Add(_headerRow);
        innerLayout.Children.Add(_badgeBorder);

        _surfaceBorder = new Border
        {
            CornerRadius = new CornerRadius(surfaceRadius),
            IsHitTestVisible = false,
            Child = innerLayout
        };

        var root = new Grid();
        root.Children.Add(_surfaceBorder);
        _window = new Window
        {
            Title = "DeskBox Detach Preview",
            Content = root
        };

        _hWnd = WindowNative.GetWindowHandle(_window);
        Microsoft.UI.WindowId windowId =
            Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        int extendedStyle = Win32Helper.GetWindowLong(
            _hWnd,
            Win32Helper.GWL_EXSTYLE);
        // WS_EX_LAYERED is deliberately excluded: mixing LWA_ALPHA with
        // EnableTransparentWindowHost + SolidTintSystemBackdrop(Transparent)
        // breaks DWM per-pixel alpha on Win10 and renders black rectangular
        // corners outside rounded XAML borders. Opacity is applied on
        // _surfaceBorder, and mouse passthrough uses WS_EX_TRANSPARENT across
        // the window tree (matching DesktopToastHudInterop).
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW |
                         Win32Helper.WS_EX_NOACTIVATE |
                         Win32Helper.WS_EX_TRANSPARENT |
                         Win32Helper.WS_EX_TOPMOST;
        extendedStyle &= ~Win32Helper.WS_EX_LAYERED;
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
        _ = Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
            Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOZORDER |
            Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_FRAMECHANGED);
        DesktopToastHudInterop.SetWindowTreeMousePassthrough(_hWnd, passthrough: true);
        _surfaceBorder.Opacity = TrackingOpacity / 255.0;
        Win32Helper.DisableWindowFrameShadow(_hWnd);
        Win32Helper.EnableTransparentWindowHost(_hWnd);
        _transparentBackdrop = new SolidTintSystemBackdrop(Colors.Transparent);
        _window.SystemBackdrop = _transparentBackdrop;
        ApplyAppearanceNoLock(appearance);
    }

    public void BeginTracking(WidgetDetachPreviewAppearance appearance)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            ApplyAppearanceNoLock(appearance);
            SetOpacityNoLock(TrackingOpacity);
            HideNoLock();
            _hasBounds = false;
        }
    }

    public void Update(RectInt32 bounds, bool visible)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            RectInt32 normalized = NormalizeBounds(bounds);
            if (!visible)
            {
                _lastBounds = normalized;
                _hasBounds = true;
                HideNoLock();
                return;
            }

            MoveAndShowNoLock(normalized);
        }
    }

    public void MarkCommitted(RectInt32 bounds)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            SetOpacityNoLock(CommittedOpacity);
            MoveAndShowNoLock(NormalizeBounds(bounds));
        }
    }

    internal bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return !_closed && _visible;
            }
        }
    }

    public void Hide()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            HideNoLock();
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
            HideNoLock();
        }

        _window.SystemBackdrop = null;
        _transparentBackdrop.Dispose();
        Win32Helper.DisableTransparentWindowHost(_hWnd);
        _window.Close();
    }

    private void MoveAndShowNoLock(RectInt32 bounds)
    {
        bool boundsChanged = !_hasBounds || !AreEqual(_lastBounds, bounds);
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

        if (!boundsChanged)
        {
            return;
        }

        // WS_EX_TOPMOST is established on the hidden -> visible transition.
        // Tracking frames only move the silhouette; they must not rebuild the
        // global Z-order or issue another show request on every poll.
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

    private void HideNoLock()
    {
        if (!_visible)
        {
            return;
        }

        _ = Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _visible = false;
    }

    private void SetOpacityNoLock(byte opacity)
    {
        if (_opacity == opacity)
        {
            return;
        }

        _surfaceBorder.Opacity = opacity / 255.0;
        _opacity = opacity;
    }

    private void ApplyAppearanceNoLock(WidgetDetachPreviewAppearance appearance)
    {
        Color tone = ResolveNeutralTone();
        double surfaceRadius = Math.Clamp(appearance.CornerRadius, 0, 32);
        Win32Helper.SetWindowTheme(_hWnd, appearance.IsDark);
        if (_window.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = appearance.IsDark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }

        // Lightweight translucent landing frame (~34% alpha) instead of a heavy
        // opaque block so the desktop and neighboring widgets stay legible.
        Color basePlate = appearance.CustomBackgroundColor ??
            (appearance.IsDark
                ? Color.FromArgb(0xFF, 28, 30, 36)
                : Color.FromArgb(0xFF, 246, 248, 252));
        byte surfaceAlpha = appearance.IsDark ? (byte)0x58 : (byte)0x66;
        _surfaceBorder.Background = SharedBrushCache.GetOrCreate(
            Color.FromArgb(surfaceAlpha, basePlate.R, basePlate.G, basePlate.B));

        Color headerWash = appearance.IsDark
            ? Color.FromArgb(0x36, 255, 255, 255)
            : Color.FromArgb(0x28, 0, 0, 0);
        _headerRow.Background = SharedBrushCache.GetOrCreate(
            Color.FromArgb(
                appearance.IsDark ? (byte)0x42 : (byte)0x38,
                basePlate.R,
                basePlate.G,
                basePlate.B));
        _headerDivider.Background = SharedBrushCache.GetOrCreate(
            Color.FromArgb(appearance.IsDark ? (byte)0x34 : (byte)0x26, tone.R, tone.G, tone.B));
        _ = headerWash;

        bool hasMemberTitle = !string.IsNullOrWhiteSpace(appearance.MemberTitle);
        _headerRow.Visibility = hasMemberTitle ? Visibility.Visible : Visibility.Collapsed;
        if (hasMemberTitle)
        {
            _memberTitleTextBlock.Text = appearance.MemberTitle;
            _memberIcon.LabelText = appearance.MemberTitle;
            _memberIcon.Glyph = string.IsNullOrWhiteSpace(appearance.MemberGlyph)
                ? "\uE8A5"
                : appearance.MemberGlyph;
            _memberIcon.IconKind = string.IsNullOrWhiteSpace(appearance.MemberIconKind)
                ? WidgetTitleIconKindNames.Default
                : appearance.MemberIconKind;
            _memberIcon.Mode = string.IsNullOrWhiteSpace(appearance.MemberIconMode)
                ? WidgetTitleIconModeNames.Color
                : appearance.MemberIconMode;
            _memberIcon.AccentColor = appearance.AccentColor;
            _memberIcon.CustomEmoji = appearance.MemberCustomEmoji ?? string.Empty;
        }

        bool highContrast = WindowsCompatibilityService.IsHighContrast;
        Color primaryText = highContrast
            ? Color.FromArgb(0xFF, tone.R, tone.G, tone.B)
            : appearance.IsDark
                ? Colors.White
                : Color.FromArgb(0xE4, 0, 0, 0);
        SolidColorBrush textBrush = SharedBrushCache.GetOrCreate(primaryText);
        _memberTitleTextBlock.Foreground = textBrush;
        _captionTextBlock.Text = appearance.Caption;
        _captionTextBlock.Foreground = textBrush;
        _badgeIcon.Foreground = textBrush;

        double resolvedBorderThickness = highContrast
            ? 2.0
            : appearance.BorderThickness > 0 && appearance.BorderColor.A > 0
                ? Math.Max(1.0, appearance.BorderThickness)
                : 1.0;
        Color resolvedBorderColor = highContrast
            ? Color.FromArgb(0xFF, tone.R, tone.G, tone.B)
            : appearance.BorderThickness > 0 && appearance.BorderColor.A > 0
                ? Color.FromArgb(
                    Math.Max(appearance.BorderColor.A, appearance.IsDark ? (byte)0x78 : (byte)0x60),
                    appearance.BorderColor.R,
                    appearance.BorderColor.G,
                    appearance.BorderColor.B)
                : Color.FromArgb(
                    appearance.IsDark ? (byte)0x68 : (byte)0x52,
                    tone.R,
                    tone.G,
                    tone.B);

        _surfaceBorder.BorderBrush = SharedBrushCache.GetOrCreate(resolvedBorderColor);
        _surfaceBorder.BorderThickness = new Thickness(resolvedBorderThickness);
        _surfaceBorder.CornerRadius = new CornerRadius(surfaceRadius);

        Color flyoutBase = highContrast
            ? (appearance.IsDark ? Colors.Black : Colors.White)
            : appearance.IsDark
                ? Color.FromArgb(0xF0, 44, 44, 44)
                : Color.FromArgb(0xF4, 249, 249, 249);
        if (!highContrast && appearance.CustomBackgroundColor is { } customTint)
        {
            flyoutBase = Color.FromArgb(
                flyoutBase.A,
                LerpChannel(flyoutBase.R, customTint.R, 0.16),
                LerpChannel(flyoutBase.G, customTint.G, 0.16),
                LerpChannel(flyoutBase.B, customTint.B, 0.16));
        }

        Color flyoutStroke = highContrast
            ? Color.FromArgb(0xFF, tone.R, tone.G, tone.B)
            : appearance.IsDark
                ? Color.FromArgb(0x1D, 255, 255, 255)
                : Color.FromArgb(0x14, 0, 0, 0);

        _badgeBorder.Background = SharedBrushCache.GetOrCreate(flyoutBase);
        _badgeBorder.BorderBrush = SharedBrushCache.GetOrCreate(flyoutStroke);
    }

    private static byte LerpChannel(byte from, byte to, double amount) =>
        (byte)Math.Clamp((int)Math.Round(from + ((to - from) * amount)), 0, 255);

    private static RectInt32 NormalizeBounds(RectInt32 bounds)
    {
        return new RectInt32(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height));
    }

    private static bool AreEqual(RectInt32 left, RectInt32 right)
    {
        return left.X == right.X &&
               left.Y == right.Y &&
               left.Width == right.Width &&
               left.Height == right.Height;
    }

    private static Color ResolveNeutralTone() =>
        NeutralInteractionBrush.Line(null);
}
