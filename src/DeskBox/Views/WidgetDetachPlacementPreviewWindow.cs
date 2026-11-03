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
    string MaterialType,
    bool IsDark,
    Color AccentColor,
    double SurfaceOpacity,
    double MaterialIntensity,
    Color? CustomBackgroundColor,
    double BorderThickness,
    Color BorderColor)
{
    public static WidgetDetachPreviewAppearance CreateDefault(
        string caption,
        double cornerRadius)
    {
        bool isDark = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
        Color accent = AccentColorHelper.DefaultAccentColor;
        Color tone = NeutralInteractionBrush.Line(null);
        return new WidgetDetachPreviewAppearance(
            caption,
            cornerRadius,
            SettingsService.WidgetMaterialTypeMica,
            isDark,
            accent,
            SurfaceOpacity: 0.72,
            MaterialIntensity: SettingsService.DefaultWidgetMaterialIntensity,
            CustomBackgroundColor: null,
            BorderThickness: 1.0,
            BorderColor: Color.FromArgb(0x68, tone.R, tone.G, tone.B));
    }
}

/// <summary>
/// A click-through, non-activating placement silhouette used while a member is
/// dragged out of a widget group. Native positioning keeps it responsive while
/// OLE owns the UI thread's modal drag loop.
/// </summary>
internal sealed class WidgetDetachPlacementPreviewWindow : IDisposable
{
    private const byte TrackingOpacity = 228;
    private const byte CommittedOpacity = 242;
    private readonly object _gate = new();
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly SystemBackdropElement _backdropElement;
    private readonly Border _surfaceBorder;
    private readonly Border _badgeBorder;
    private readonly TextBlock _captionTextBlock;
    private readonly SolidTintSystemBackdrop _transparentBackdrop;
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private RectInt32 _lastBounds;
    private byte _opacity = TrackingOpacity;
    private int _animationGeneration;
    private bool _hasBounds;
    private bool _visible;
    private bool _closed;

    public WidgetDetachPlacementPreviewWindow(
        WidgetDetachPreviewAppearance appearance)
    {
        double surfaceRadius = Math.Clamp(appearance.CornerRadius, 0, 32);
        var root = new Grid();
        _backdropElement = new SystemBackdropElement
        {
            CornerRadius = new CornerRadius(0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _surfaceBorder = new Border
        {
            CornerRadius = new CornerRadius(surfaceRadius),
            IsHitTestVisible = false
        };
        _captionTextBlock = new TextBlock
        {
            Text = appearance.Caption,
            MaxWidth = 260,
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _badgeBorder = new Border
        {
            Margin = new Thickness(10, 0, 10, 8),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Clamp(surfaceRadius * 0.45, 4, 6)),
            Child = _captionTextBlock
        };
        root.Children.Add(_backdropElement);
        root.Children.Add(_surfaceBorder);
        root.Children.Add(_badgeBorder);
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

            _animationGeneration++;
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
            MoveAndShowNoLock(NormalizeBounds(new RectInt32(
                bounds.X - 2,
                bounds.Y - 2,
                bounds.Width + 4,
                bounds.Height + 4)));
        }
    }

    public async Task FadeOutAndHideAsync()
    {
        int generation;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            generation = ++_animationGeneration;
        }

        foreach (byte opacity in new byte[] { 176, 118, 58, 16 })
        {
            lock (_gate)
            {
                if (_closed || generation != _animationGeneration)
                {
                    return;
                }

                SetOpacityNoLock(opacity);
            }

            await Task.Delay(28);
        }

        lock (_gate)
        {
            if (!_closed && generation == _animationGeneration)
            {
                HideNoLock();
            }
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

            _animationGeneration++;
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
            _animationGeneration++;
            HideNoLock();
        }

        _materialBackdrop?.Dispose();
        _materialBackdrop = null;
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

        _ = Win32Helper.SetLayeredWindowAttributes(
            _hWnd,
            0,
            opacity,
            Win32Helper.LWA_ALPHA);
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

        _captionTextBlock.Text = appearance.Caption;
        _captionTextBlock.Foreground = new SolidColorBrush(
            appearance.IsDark
                ? Colors.White
                : Color.FromArgb(0xEC, 20, 22, 26));

        bool hasCustomColor = appearance.CustomBackgroundColor.HasValue;
        bool useSystemMaterial = !hasCustomColor &&
            WidgetMaterialSystemBackdrop.IsSupported(appearance.MaterialType);

        if (useSystemMaterial)
        {
            double materialSurfaceOpacity = SettingsService.IsAcrylicMaterial(appearance.MaterialType) &&
                appearance.SurfaceOpacity <= 0.08
                    ? 0.42
                    : appearance.SurfaceOpacity;
            var backdropAppearance = new WidgetMaterialBackdropAppearance(
                appearance.MaterialType,
                appearance.IsDark,
                appearance.AccentColor,
                materialSurfaceOpacity,
                appearance.MaterialIntensity);
            _materialBackdrop ??= new WidgetMaterialSystemBackdrop(
                backdropAppearance,
                closeElementTargetOnDisconnect: true);
            _materialBackdrop.SetCornerRadius(surfaceRadius);
            _materialBackdrop.UpdateAppearance(backdropAppearance);
            _backdropElement.Visibility = Visibility.Visible;
            if (!ReferenceEquals(_backdropElement.SystemBackdrop, _materialBackdrop))
            {
                _backdropElement.SystemBackdrop = _materialBackdrop;
            }

            // Provide a gentle theme-tinted backing plate so the material silhouette
            // reads immediately on the first frame even before the composition target settles.
            Color backingColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                appearance.IsDark,
                appearance.AccentColor,
                surfaceOpacity: appearance.IsDark ? 0.24 : 0.30);
            _surfaceBorder.Background = new SolidColorBrush(backingColor);
        }
        else
        {
            _materialBackdrop?.Suspend();
            _backdropElement.Visibility = Visibility.Collapsed;

            // In Solid Color mode (or custom background color / fallback),
            // when in fully transparent mode (surfaceOpacity <= 0.05), render
            // a semi-transparent surface of the corresponding theme/custom color.
            bool isFullyTransparent = appearance.SurfaceOpacity <= 0.05;
            double effectiveSolidOpacity = isFullyTransparent
                ? (appearance.IsDark ? 0.56 : 0.64)
                : Math.Clamp(appearance.SurfaceOpacity, 0.28, 0.94);

            Color surfaceColor;
            if (appearance.CustomBackgroundColor is { } customColor)
            {
                byte alpha = (byte)Math.Clamp(Math.Round(effectiveSolidOpacity * 255), 0, 255);
                surfaceColor = Color.FromArgb(alpha, customColor.R, customColor.G, customColor.B);
            }
            else
            {
                surfaceColor = WidgetMaterialVisualCalculator.BuildContentSolidSurfaceColor(
                    appearance.IsDark,
                    appearance.AccentColor,
                    effectiveSolidOpacity);
            }

            _surfaceBorder.Background = new SolidColorBrush(surfaceColor);
        }

        Color resolvedBorderColor;
        double resolvedBorderThickness;
        if (appearance.BorderThickness > 0 && appearance.BorderColor.A > 0)
        {
            resolvedBorderThickness = Math.Max(1.0, appearance.BorderThickness);
            byte minBorderAlpha = appearance.IsDark ? (byte)0x5C : (byte)0x48;
            resolvedBorderColor = Color.FromArgb(
                Math.Max(appearance.BorderColor.A, minBorderAlpha),
                appearance.BorderColor.R,
                appearance.BorderColor.G,
                appearance.BorderColor.B);
        }
        else
        {
            resolvedBorderThickness = 1.0;
            resolvedBorderColor = Color.FromArgb(
                appearance.IsDark ? (byte)0x48 : (byte)0x38,
                tone.R,
                tone.G,
                tone.B);
        }

        _surfaceBorder.BorderBrush = new SolidColorBrush(resolvedBorderColor);
        _surfaceBorder.BorderThickness = new Thickness(resolvedBorderThickness);
        _surfaceBorder.CornerRadius = new CornerRadius(surfaceRadius);

        _badgeBorder.Background = new SolidColorBrush(
            appearance.IsDark
                ? Color.FromArgb(0xE8, 26, 28, 34)
                : Color.FromArgb(0xEE, 248, 249, 252));
        _badgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(
            appearance.IsDark ? (byte)0x68 : (byte)0x48,
            tone.R,
            tone.G,
            tone.B));
        _badgeBorder.CornerRadius = new CornerRadius(
            Math.Clamp(surfaceRadius * 0.45, 4, 6));
    }

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

    /// <summary>
    /// The neutral interaction tone this window draws its drag silhouette with.
    /// The window owns no visual tree yet when the constructor builds one, so it
    /// reads the application theme resources directly.
    /// </summary>
    private static Color ResolveNeutralTone() =>
        NeutralInteractionBrush.Line(null);
}
