using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Global borderless, non-activating bottom-center HUD flyout window modeled on
/// Windows 11's native Hardware Indicator flyout (Volume / Brightness HUD):
/// 8 DIP rounded rectangle, DWM native outer border and elevation shadow, live
/// <see cref="DesktopAcrylicController"/> surface (<c>IsInputActive = true</c>),
/// 48 DIP minimum single-line height, 14px Segoe UI Variable Text typography,
/// and monochrome primary status iconography.
/// </summary>
internal sealed class DesktopToastHudWindow : Window
{
    internal const double CardCornerRadiusDip = 8.0;
    internal const double ControlCornerRadiusDip = 4.0;
    internal const double MinCardHeightDip = 48.0;
    internal const double MinCardWidthDip = 160.0;
    internal const double MaxCardWidthDip = 600.0;
    internal const double BottomMarginDip = 24.0;
    internal const double MessageFontSizeDip = 14.0;
    internal const double MessageLineHeightDip = 20.0;
    internal const double StatusIconFontSizeDip = 16.0;
    internal const double ActionButtonFontSizeDip = 13.0;
    internal const double DismissIconFontSizeDip = 10.0;
    internal const double HorizontalPaddingDip = 16.0;
    internal const double TrailingDismissPaddingDip = 10.0;
    internal const double VerticalPaddingDip = 10.0;
    internal const double IconToMessageSpacingDip = 12.0;
    internal const double MessageToActionSpacingDip = 12.0;
    internal const double ActionToDismissSpacingDip = 6.0;
    internal const double MessageToDismissSpacingDip = 8.0;

    private const double LegacyTransparentPaddingDip = 8.0;

    private static readonly FontFamily s_hudFontFamily =
        new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private static readonly UIntPtr SubclassId = new(0xDDB9);
    private static readonly Win32Helper.SubclassProc s_subclassProc =
        CallbackDepth.WrapSubclassProc(WindowSubclassProc);
    private static readonly HashSet<IntPtr> s_nativeFrameWindows = [];
    private static readonly object s_nativeFrameGate = new();

    private readonly AppWindow _appWindow;
    private readonly bool _subclassInstalled;
    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly Grid _hostRoot;
    private readonly Border _pillBorder;
    private readonly FontIcon _statusIcon;
    private readonly TextBlock _messageText;
    private readonly Button _actionButton;
    private readonly Button _dismissButton;
    private readonly FontIcon _dismissIcon;

    private readonly SolidColorBrush _iconBrush = new(Colors.White);
    private readonly SolidColorBrush _bgBrush = new(Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C));
    private readonly SolidColorBrush _borderBrush = new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _textBrush = new(Colors.White);
    private readonly SolidColorBrush _actionBgBrush = new(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _actionBorderBrush = new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private readonly SolidColorBrush _dismissBgBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _dismissIconBrush = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    private HudFlyoutAcrylicBackdrop? _flyoutAcrylicBackdrop;
    private SolidTintSystemBackdrop? _transparentBackdrop;
    private Func<Task>? _currentActionCallback;
    private Func<Task>? _currentDismissCallback;
    private bool _nativeFlyoutFrameConfigured;
    private bool _currentLightTheme;
    private bool _currentHighContrast;
    private bool _isDismissHovered;
    private bool _isActionHovered;
    private bool _isPointerInside;
    private bool _isShown;
    private bool _closed;
    private long _sessionId;
    private MonitorAndBackdropInterop.NativeRect _lastToastWorkArea;
    private double _lastToastScale;

    internal readonly record struct HudPaletteSnapshot(
        Color FallbackBackground,
        Color Border,
        Color PrimaryText,
        Color StatusIcon,
        Color SecondaryGlyph);

    public DesktopToastHudWindow()
    {
        Title = "DeskBox Drag HUD";
        WindowHandle = WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);

        bool useNativeFrame = ShouldUseNativeWin11FlyoutFrame();
        ConfigureWindowFrameMode(useNativeFrame, initialSetup: true);

        _subclassInstalled = Win32Helper.SetWindowSubclass(
            WindowHandle,
            s_subclassProc,
            SubclassId,
            UIntPtr.Zero);

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += AutoHideTimer_Tick;

        _statusIcon = new FontIcon
        {
            FontSize = StatusIconFontSizeDip,
            Width = StatusIconFontSizeDip,
            Height = StatusIconFontSizeDip,
            Margin = new Thickness(0, 0, IconToMessageSpacingDip, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Glyph = "\uE946",
        };

        _messageText = new TextBlock
        {
            FontFamily = s_hudFontFamily,
            FontSize = MessageFontSizeDip,
            FontWeight = FontWeights.Normal,
            LineHeight = MessageLineHeightDip,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxLines = 2,
            MaxWidth = 440,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _actionButton = new Button
        {
            FontFamily = s_hudFontFamily,
            FontSize = ActionButtonFontSizeDip,
            FontWeight = FontWeights.Normal,
            CornerRadius = new CornerRadius(ControlCornerRadiusDip),
            Padding = new Thickness(10, 3, 10, 4),
            MinHeight = 28,
            MinWidth = 0,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(MessageToActionSpacingDip, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _actionButton.Click += ActionButton_Click;
        _actionButton.PointerEntered += ActionButton_PointerEntered;
        _actionButton.PointerExited += ActionButton_PointerExited;

        _dismissIcon = new FontIcon
        {
            Glyph = "\uE711",
            FontSize = DismissIconFontSizeDip,
            Foreground = _dismissIconBrush,
        };

        _dismissButton = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            MinWidth = 28,
            MinHeight = 28,
            CornerRadius = new CornerRadius(ControlCornerRadiusDip),
            Background = _dismissBgBrush,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(MessageToDismissSpacingDip, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = _dismissIcon,
        };
        _dismissButton.Click += DismissButton_Click;
        _dismissButton.PointerEntered += DismissButton_PointerEntered;
        _dismissButton.PointerExited += DismissButton_PointerExited;

        var layoutGrid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_statusIcon, 0);
        Grid.SetColumn(_messageText, 1);
        Grid.SetColumn(_actionButton, 2);
        Grid.SetColumn(_dismissButton, 3);
        layoutGrid.Children.Add(_statusIcon);
        layoutGrid.Children.Add(_messageText);
        layoutGrid.Children.Add(_actionButton);
        layoutGrid.Children.Add(_dismissButton);

        _pillBorder = new Border
        {
            CornerRadius = new CornerRadius(CardCornerRadiusDip),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(
                HorizontalPaddingDip,
                VerticalPaddingDip,
                TrailingDismissPaddingDip,
                VerticalPaddingDip),
            MinWidth = MinCardWidthDip,
            MinHeight = MinCardHeightDip,
            MaxWidth = MaxCardWidthDip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = layoutGrid,
        };
        _pillBorder.PointerEntered += PillBorder_PointerEntered;
        _pillBorder.PointerExited += PillBorder_PointerExited;

        _hostRoot = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0),
        };
        _hostRoot.Children.Add(_pillBorder);
        Content = _hostRoot;
    }

    internal IntPtr WindowHandle { get; }

    internal bool IsShown => !_closed && _isShown;

    internal bool IsPointerHovered => !_closed && _isShown && _isPointerInside;

    internal static HudPaletteSnapshot ResolveFluentPalette(
        DesktopToastSeverity severity,
        bool light,
        bool highContrast)
    {
        if (highContrast)
        {
            Color hcBg = WindowsCompatibilityService.TryGetUiColor(
                    Windows.UI.ViewManagement.UIColorType.Background)
                ?? Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            Color hcFg = WindowsCompatibilityService.TryGetUiColor(
                    Windows.UI.ViewManagement.UIColorType.Foreground)
                ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            Color hcCaution = severity == DesktopToastSeverity.Warning
                ? Color.FromArgb(0xFF, 0xFF, 0xEB, 0x3B)
                : hcFg;
            return new HudPaletteSnapshot(
                FallbackBackground: hcBg,
                Border: hcCaution,
                PrimaryText: hcFg,
                StatusIcon: hcCaution,
                SecondaryGlyph: hcFg);
        }

        // Pure neutral Fluent 2 Flyout palette (matching Windows 11 Volume HUD):
        // #2C2C2C dark / #F9F9F9 light base, crisp monochrome primary glyphs for
        // Info & Success, and Fluent SystemFillColorCaution only for safety warnings.
        Color bg = light
            ? Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9)
            : Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
        Color border = light
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        Color primaryText = light
            ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color statusIcon = severity == DesktopToastSeverity.Warning
            ? (light
                ? Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00)
                : Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00))
            : primaryText;
        Color secondaryGlyph = light
            ? Color.FromArgb(0x8F, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

        return new HudPaletteSnapshot(bg, border, primaryText, statusIcon, secondaryGlyph);
    }

    internal void ShowToast(DesktopToastRequest request, bool preferLightTheme)
    {
        if (_closed)
        {
            return;
        }

        bool wasShown = _isShown;
        ++_sessionId;
        _isPointerInside = false;
        _isActionHovered = false;
        _isDismissHovered = false;
        _autoHideTimer.Stop();

        _currentActionCallback = request.OnActionAsync;
        _currentDismissCallback = request.OnDismissAsync;

        bool useNativeFrame = ShouldUseNativeWin11FlyoutFrame();
        if (useNativeFrame != _nativeFlyoutFrameConfigured)
        {
            ConfigureWindowFrameMode(useNativeFrame, initialSetup: false);
        }

        bool isDark = !preferLightTheme;
        bool liveAcrylicActive = EnsureSurfaceBackdrop(useNativeFrame, isDark);

        bool hasAction = !string.IsNullOrWhiteSpace(request.ActionButtonText) &&
            request.OnActionAsync is not null;
        if (hasAction)
        {
            _actionButton.Content = request.ActionButtonText;
            _actionButton.Visibility = Visibility.Visible;
        }
        else
        {
            _actionButton.Content = null;
            _actionButton.Visibility = Visibility.Collapsed;
        }

        bool hasDismiss = request.ShowDismissButton;
        _dismissButton.Visibility = hasDismiss
            ? Visibility.Visible
            : Visibility.Collapsed;
        _dismissButton.Margin = new Thickness(
            hasAction ? ActionToDismissSpacingDip : MessageToDismissSpacingDip,
            0,
            0,
            0);

        double trailingPaddingDip = hasDismiss
            ? TrailingDismissPaddingDip
            : HorizontalPaddingDip;
        _pillBorder.Padding = new Thickness(
            HorizontalPaddingDip,
            VerticalPaddingDip,
            trailingPaddingDip,
            VerticalPaddingDip);

        ApplyPalette(
            request.Severity,
            preferLightTheme,
            useNativeFrame,
            liveAcrylicActive);
        _messageText.Text = request.Message;

        DesktopToastHudInterop.GetCursorMonitorWorkArea(
            out MonitorAndBackdropInterop.NativeRect workArea,
            out double scale);

        bool monitorOrScaleChanged =
            !_isShown ||
            Math.Abs(_lastToastScale - scale) > 0.005 ||
            _lastToastWorkArea.Left != workArea.Left ||
            _lastToastWorkArea.Top != workArea.Top ||
            _lastToastWorkArea.Right != workArea.Right ||
            _lastToastWorkArea.Bottom != workArea.Bottom;
        if (monitorOrScaleChanged)
        {
            DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: true);
            // Seed the cloaked window onto the target monitor before measuring so
            // XamlRoot adopts that monitor's DPI / rasterization scale first,
            // preventing a 1-frame size jump when transitioning across monitors
            // while an earlier drag-start tip is still visible.
            _appWindow.MoveAndResize(new RectInt32(
                workArea.Left + 16,
                workArea.Top + 16,
                220,
                (int)Math.Ceiling(MinCardHeightDip * scale)));
        }

        _lastToastWorkArea = workArea;
        _lastToastScale = scale;

        double hostPaddingDip = useNativeFrame || _currentHighContrast
            ? 0.0
            : LegacyTransparentPaddingDip;
        double workWidthDip = Math.Max(320.0, (workArea.Right - workArea.Left) / scale);
        double workHeightDip = Math.Max(240.0, (workArea.Bottom - workArea.Top) / scale);
        double maxCardHeightDip = Math.Clamp(workHeightDip * 0.35, 96.0, 200.0);
        double effectiveMaxCardWidthDip = Math.Clamp(MaxCardWidthDip, MinCardWidthDip, workWidthDip - 48.0);

        // Measure intrinsic content width first so short messages hug cleanly
        // instead of stretching to MaxWidth.
        _pillBorder.HorizontalAlignment = HorizontalAlignment.Left;
        _pillBorder.VerticalAlignment = VerticalAlignment.Top;
        _pillBorder.MaxWidth = effectiveMaxCardWidthDip;

        double nonTextChromeDip =
            HorizontalPaddingDip +
            trailingPaddingDip +
            StatusIconFontSizeDip +
            IconToMessageSpacingDip +
            (hasDismiss ? (28.0 + (hasAction ? ActionToDismissSpacingDip : MessageToDismissSpacingDip)) : 0.0);
        if (hasAction)
        {
            _actionButton.Measure(new Size(Math.Max(96.0, effectiveMaxCardWidthDip * 0.45), maxCardHeightDip));
            nonTextChromeDip += Math.Ceiling(_actionButton.DesiredSize.Width) + MessageToActionSpacingDip;
        }

        _messageText.MaxWidth = Math.Max(140.0, effectiveMaxCardWidthDip - nonTextChromeDip - 4.0);
        _pillBorder.Measure(new Size(effectiveMaxCardWidthDip, maxCardHeightDip));

        double cardWidthDip = Math.Clamp(
            Math.Ceiling(_pillBorder.DesiredSize.Width) + 2.0,
            MinCardWidthDip,
            effectiveMaxCardWidthDip);
        double cardHeightDip = Math.Clamp(
            Math.Ceiling(_pillBorder.DesiredSize.Height),
            MinCardHeightDip,
            maxCardHeightDip);

        _pillBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pillBorder.VerticalAlignment = VerticalAlignment.Stretch;

        double totalWidthDip = cardWidthDip + hostPaddingDip;
        double totalHeightDip = cardHeightDip + hostPaddingDip;

        int widthPx = Math.Max(120, (int)Math.Ceiling(totalWidthDip * scale));
        int heightPx = Math.Max(
            (int)Math.Round(MinCardHeightDip * scale),
            (int)Math.Ceiling(totalHeightDip * scale));
        int bottomOffsetPx = (int)Math.Round((BottomMarginDip - (hostPaddingDip / 2.0)) * scale);

        int x = workArea.Left + Math.Max(8, ((workArea.Right - workArea.Left) - widthPx) / 2);
        int y = Math.Max(workArea.Top + 8, workArea.Bottom - heightPx - Math.Max(8, bottomOffsetPx));

        DesktopToastHudInterop.EnsureNoActivateExtendedStyle(WindowHandle);
        var targetRect = new RectInt32(x, y, widthPx, heightPx);
        _appWindow.MoveAndResize(targetRect);
        if (_appWindow.Size.Width != widthPx ||
            _appWindow.Size.Height != heightPx ||
            _appWindow.Position.X != x ||
            _appWindow.Position.Y != y)
        {
            _appWindow.MoveAndResize(targetRect);
        }

        if (!_appWindow.IsVisible)
        {
            _ = Win32Helper.ShowWindow(WindowHandle, DesktopToastHudInterop.SwShowNoActivate);
        }

        if (useNativeFrame)
        {
            DesktopToastHudInterop.ApplyNativeFlyoutDwmFrame(WindowHandle);
        }

        _hostRoot.UpdateLayout();
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE);
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(WindowHandle);
        DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: false);
        _isShown = true;

        if (!wasShown)
        {
            PlaySubtleEntranceIfEnabled();
        }

        int durationMs = Math.Clamp(request.DurationMs, 1800, 12_000);
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _autoHideTimer.Start();
    }

    internal void HideImmediate()
    {
        if (_closed)
        {
            return;
        }

        ++_sessionId;
        _autoHideTimer.Stop();
        _isPointerInside = false;
        _isActionHovered = false;
        _isDismissHovered = false;
        _currentActionCallback = null;
        _currentDismissCallback = null;
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(IntPtr.Zero);

        if (!_isShown)
        {
            return;
        }

        _isShown = false;
        DesktopToastHudInterop.SetCloaked(WindowHandle, cloaked: true);
        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }

        ReleaseBackdrops();
        DesktopToastHudService.Shared.NotifyWindowHidden();
    }

    internal void Destroy()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _isShown = false;
        _autoHideTimer.Stop();
        _currentActionCallback = null;
        _currentDismissCallback = null;
        DesktopToastHudService.Shared.SetActiveHudWindowHandle(IntPtr.Zero);
        UnregisterNativeFrameWindow(WindowHandle);

        if (_subclassInstalled)
        {
            _ = Win32Helper.RemoveWindowSubclass(
                WindowHandle,
                s_subclassProc,
                SubclassId);
        }

        ReleaseBackdrops();
        try
        {
            Close();
        }
        catch
        {
        }
    }

    private static bool ShouldUseNativeWin11FlyoutFrame() =>
        WindowsCompatibilityService.SupportsNativeWindowCorners &&
        !WindowsCompatibilityService.IsHighContrast;

    private void ConfigureWindowFrameMode(bool useNativeFrame, bool initialSetup)
    {
        _nativeFlyoutFrameConfigured = useNativeFrame;
        if (useNativeFrame)
        {
            RegisterNativeFrameWindow(WindowHandle);
            if (!initialSetup)
            {
                ReleaseTransparentBackdropOnly();
            }

            WindowShellState.TryApplyFloatingPopupPresenter(_appWindow);
        }
        else
        {
            UnregisterNativeFrameWindow(WindowHandle);
            if (!initialSetup)
            {
                ReleaseFlyoutAcrylicBackdropOnly();
            }

            WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);
        }

        DesktopToastHudInterop.ApplyNoActivateHudWindowStyles(WindowHandle, useNativeFrame);
    }

    private bool EnsureSurfaceBackdrop(bool useNativeFrame, bool isDark)
    {
        if (WindowsCompatibilityService.IsHighContrast)
        {
            ReleaseBackdrops();
            return false;
        }

        if (useNativeFrame)
        {
            ReleaseTransparentBackdropOnly();
            if (!WindowsCompatibilityService.SupportsDesktopAcrylic ||
                !WindowsCompatibilityService.AreAdvancedEffectsEnabled)
            {
                ReleaseFlyoutAcrylicBackdropOnly();
                return false;
            }

            try
            {
                _flyoutAcrylicBackdrop ??= new HudFlyoutAcrylicBackdrop(isDark);
                _flyoutAcrylicBackdrop.SetDarkTheme(isDark);
                if (!ReferenceEquals(SystemBackdrop, _flyoutAcrylicBackdrop))
                {
                    SystemBackdrop = _flyoutAcrylicBackdrop;
                }

                return _flyoutAcrylicBackdrop.IsControllerActive || _flyoutAcrylicBackdrop.IsPendingTarget;
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[DragHud] Native flyout acrylic fallback: {ex.Message}");
                ReleaseFlyoutAcrylicBackdropOnly();
                return false;
            }
        }

        ReleaseFlyoutAcrylicBackdropOnly();
        try
        {
            Win32Helper.EnableTransparentWindowHost(WindowHandle);
            _transparentBackdrop ??= new SolidTintSystemBackdrop(Color.FromArgb(0, 0, 0, 0));
            if (!ReferenceEquals(SystemBackdrop, _transparentBackdrop))
            {
                SystemBackdrop = _transparentBackdrop;
            }

            return false;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DragHud] Transparent backdrop fallback unavailable: {ex.Message}");
            ReleaseTransparentBackdropOnly();
            return false;
        }
    }

    private void ReleaseBackdrops()
    {
        ReleaseFlyoutAcrylicBackdropOnly();
        ReleaseTransparentBackdropOnly();
    }

    private void ReleaseFlyoutAcrylicBackdropOnly()
    {
        if (_flyoutAcrylicBackdrop is not null)
        {
            if (ReferenceEquals(SystemBackdrop, _flyoutAcrylicBackdrop))
            {
                SystemBackdrop = null;
            }

            _flyoutAcrylicBackdrop.Dispose();
            _flyoutAcrylicBackdrop = null;
        }
    }

    private void ReleaseTransparentBackdropOnly()
    {
        if (_transparentBackdrop is not null)
        {
            if (ReferenceEquals(SystemBackdrop, _transparentBackdrop))
            {
                SystemBackdrop = null;
            }

            _transparentBackdrop.Dispose();
            _transparentBackdrop = null;
        }

        if (!_nativeFlyoutFrameConfigured)
        {
            Win32Helper.DisableTransparentWindowHost(WindowHandle);
        }
    }

    private void ApplyPalette(
        DesktopToastSeverity severity,
        bool light,
        bool useNativeFrame,
        bool liveAcrylicActive)
    {
        bool highContrast = WindowsCompatibilityService.IsHighContrast;
        _currentLightTheme = light;
        _currentHighContrast = highContrast;

        Win32Helper.SetWindowTheme(WindowHandle, isDark: !light && !highContrast);
        _hostRoot.RequestedTheme = highContrast
            ? ElementTheme.Default
            : (light ? ElementTheme.Light : ElementTheme.Dark);

        HudPaletteSnapshot palette = ResolveFluentPalette(severity, light, highContrast);

        if (useNativeFrame)
        {
            _hostRoot.Padding = new Thickness(0);
            _pillBorder.CornerRadius = new CornerRadius(CardCornerRadiusDip);
            // DWM DWMWCP_ROUND draws the native 1px outer flyout border and drop shadow
            // outside the client rect, matching Windows 11's Volume HUD pixel-for-pixel.
            _pillBorder.BorderThickness = new Thickness(0);
            _bgBrush.Color = liveAcrylicActive
                ? Colors.Transparent
                : palette.FallbackBackground;
        }
        else if (highContrast)
        {
            _hostRoot.Padding = new Thickness(0);
            _pillBorder.CornerRadius = new CornerRadius(0);
            _pillBorder.BorderThickness = new Thickness(1);
            _bgBrush.Color = palette.FallbackBackground;
        }
        else
        {
            bool transparentHostReady = _transparentBackdrop is not null;
            _hostRoot.Padding = transparentHostReady
                ? new Thickness(LegacyTransparentPaddingDip / 2.0)
                : new Thickness(0);
            _pillBorder.CornerRadius = transparentHostReady
                ? new CornerRadius(CardCornerRadiusDip)
                : new CornerRadius(0);
            _pillBorder.BorderThickness = new Thickness(1);
            byte alpha = transparentHostReady ? (light ? (byte)0xF8 : (byte)0xF5) : (byte)0xFF;
            _bgBrush.Color = Color.FromArgb(
                alpha,
                palette.FallbackBackground.R,
                palette.FallbackBackground.G,
                palette.FallbackBackground.B);
        }

        _statusIcon.Glyph = severity switch
        {
            DesktopToastSeverity.Warning => "\uE7BA",
            DesktopToastSeverity.Success => "\uE73E",
            _ => "\uE946",
        };

        _iconBrush.Color = palette.StatusIcon;
        _borderBrush.Color = palette.Border;
        _textBrush.Color = palette.PrimaryText;

        _statusIcon.Foreground = _iconBrush;
        _pillBorder.Background = _bgBrush;
        _pillBorder.BorderBrush = _borderBrush;
        _messageText.Foreground = _textBrush;

        _actionButton.Foreground = _textBrush;
        _actionButton.Background = _actionBgBrush;
        _actionButton.BorderBrush = _actionBorderBrush;
        UpdateInteractiveControlBrushes();
    }

    private void UpdateInteractiveControlBrushes()
    {
        if (_currentHighContrast)
        {
            Color hcFg = _textBrush.Color;
            _actionBgBrush.Color = Colors.Transparent;
            _actionBorderBrush.Color = hcFg;
            _dismissBgBrush.Color = Colors.Transparent;
            _dismissIconBrush.Color = hcFg;
            return;
        }

        if (_currentLightTheme)
        {
            _actionBgBrush.Color = _isActionHovered
                ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);
            _actionBorderBrush.Color = Color.FromArgb(0x14, 0x00, 0x00, 0x00);

            _dismissBgBrush.Color = _isDismissHovered
                ? Color.FromArgb(0x0D, 0x00, 0x00, 0x00)
                : Colors.Transparent;
            _dismissIconBrush.Color = _isDismissHovered
                ? Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x8F, 0x00, 0x00, 0x00);
        }
        else
        {
            _actionBgBrush.Color = _isActionHovered
                ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);
            _actionBorderBrush.Color = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);

            _dismissBgBrush.Color = _isDismissHovered
                ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)
                : Colors.Transparent;
            _dismissIconBrush.Color = _isDismissHovered
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        }
    }

    private void PlaySubtleEntranceIfEnabled()
    {
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(_pillBorder);
            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easeOut = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.0f, 0.0f),
                new Vector2(0.0f, 1.0f));

            ScalarKeyFrameAnimation fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.InsertKeyFrame(0.0f, 0.0f);
            fadeAnim.InsertKeyFrame(1.0f, 1.0f, easeOut);
            fadeAnim.Duration = TimeSpan.FromMilliseconds(120);

            Vector3KeyFrameAnimation offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0.0f, new Vector3(0.0f, 4.0f, 0.0f));
            offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easeOut);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(140);

            visual.StartAnimation("Opacity", fadeAnim);
            visual.StartAnimation("Offset", offsetAnim);
        }
        catch
        {
        }
    }

    private void ActionButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isActionHovered = true;
        UpdateInteractiveControlBrushes();
    }

    private void ActionButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isActionHovered = false;
        UpdateInteractiveControlBrushes();
    }

    private void DismissButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isDismissHovered = true;
        UpdateInteractiveControlBrushes();
    }

    private void DismissButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isDismissHovered = false;
        UpdateInteractiveControlBrushes();
    }

    private void PillBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = true;
        _autoHideTimer.Stop();
    }

    private void PillBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        if (_isShown && !_closed)
        {
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _autoHideTimer.Start();
        }
    }

    private void AutoHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerInside || _closed || !_isShown)
        {
            return;
        }

        long session = _sessionId;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _isShown && session == _sessionId && !_isPointerInside)
            {
                HideImmediate();
            }
        });
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        long session = _sessionId;
        Func<Task>? callback = _currentActionCallback;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _sessionId == session)
            {
                HideImmediate();
            }
        });
        if (callback is not null)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Action button handler failed: {ex.Message}");
            }
        }
    }

    private async void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        long session = _sessionId;
        Func<Task>? callback = _currentDismissCallback;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!_closed && _sessionId == session)
            {
                HideImmediate();
            }
        });
        if (callback is not null)
        {
            try
            {
                await callback();
            }
            catch (Exception ex)
            {
                App.Log($"[DragHud] Dismiss button handler failed: {ex.Message}");
            }
        }
    }

    private static void RegisterNativeFrameWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (s_nativeFrameGate)
        {
            s_nativeFrameWindows.Add(hwnd);
        }
    }

    private static void UnregisterNativeFrameWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (s_nativeFrameGate)
        {
            s_nativeFrameWindows.Remove(hwnd);
        }
    }

    private static bool IsNativeFrameWindow(IntPtr hwnd)
    {
        lock (s_nativeFrameGate)
        {
            return s_nativeFrameWindows.Contains(hwnd);
        }
    }

    private static IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (uMsg == DesktopToastHudInterop.WmMouseActivate)
        {
            return new IntPtr(DesktopToastHudInterop.MaNoActivate);
        }

        if (uMsg == DesktopToastHudInterop.WmGetMinMaxInfo)
        {
            DesktopToastHudInterop.RelaxMinTrackSize(lParam);
            return IntPtr.Zero;
        }

        if (uMsg == DesktopToastHudInterop.WmNcCalcSize &&
            wParam != UIntPtr.Zero &&
            IsNativeFrameWindow(hWnd))
        {
            // Expand client rect to the full window rect so WS_BORDER retains
            // DWM's 8 DIP corner clip, 1px outer border, and drop shadow without
            // reserving an interior non-client strip.
            return IntPtr.Zero;
        }

        if (uMsg == DesktopToastHudInterop.WmNcDestroy)
        {
            UnregisterNativeFrameWindow(hWnd);
            _ = Win32Helper.RemoveWindowSubclass(hWnd, s_subclassProc, uIdSubclass);
        }

        return Win32Helper.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    /// <summary>
    /// Always-active <see cref="DesktopAcrylicController"/> backdrop for the
    /// non-activating (<c>WS_EX_NOACTIVATE</c>) bottom HUD flyout window so it
    /// retains Windows 11's live transient Flyout Acrylic without stealing focus.
    /// </summary>
    private sealed partial class HudFlyoutAcrylicBackdrop : SystemBackdrop, IDisposable
    {
        private ICompositionSupportsSystemBackdrop? _target;
        private SystemBackdropConfiguration? _configuration;
        private DesktopAcrylicController? _controller;
        private bool _isDark;
        private bool _disposed;

        public HudFlyoutAcrylicBackdrop(bool isDark)
        {
            _isDark = isDark;
        }

        public bool IsPendingTarget => !_disposed && _target is null;

        public bool IsControllerActive =>
            !_disposed && _controller is { IsClosed: false };

        public void SetDarkTheme(bool isDark)
        {
            _isDark = isDark;
            if (_configuration is not null && !_disposed)
            {
                _configuration.IsInputActive = true;
                _configuration.Theme = isDark
                    ? SystemBackdropTheme.Dark
                    : SystemBackdropTheme.Light;
                if (_controller is { IsClosed: false })
                {
                    _controller.SetSystemBackdropConfiguration(_configuration);
                }
            }
        }

        protected override void OnTargetConnected(
            ICompositionSupportsSystemBackdrop connectedTarget,
            XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            if (_disposed)
            {
                return;
            }

            DisposeController();
            _target = connectedTarget;

            try
            {
                _configuration = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = _isDark
                        ? SystemBackdropTheme.Dark
                        : SystemBackdropTheme.Light,
                };

                var controller = new DesktopAcrylicController();
                controller.SetSystemBackdropConfiguration(_configuration);
                if (!controller.AddSystemBackdropTarget(connectedTarget))
                {
                    controller.Dispose();
                    return;
                }

                _controller = controller;
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[DragHud] DesktopAcrylicController attach failed: {ex.Message}");
                DisposeController();
            }
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(
            ICompositionSupportsSystemBackdrop target,
            XamlRoot xamlRoot)
        {
            // Intentionally keep IsInputActive = true regardless of OS foreground
            // activation state because the HUD window uses WS_EX_NOACTIVATE.
            if (_configuration is not null && !_disposed)
            {
                _configuration.IsInputActive = true;
                _configuration.Theme = _isDark
                    ? SystemBackdropTheme.Dark
                    : SystemBackdropTheme.Light;
            }
        }

        protected override void OnTargetDisconnected(
            ICompositionSupportsSystemBackdrop disconnectedTarget)
        {
            try
            {
                base.OnTargetDisconnected(disconnectedTarget);
            }
            finally
            {
                DisposeController();
                if (ReferenceEquals(_target, disconnectedTarget))
                {
                    _target = null;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeController();
            _target = null;
            _configuration = null;
        }

        private void DisposeController()
        {
            if (_controller is null)
            {
                return;
            }

            try
            {
                _controller.RemoveAllSystemBackdropTargets();
                _controller.Dispose();
            }
            catch
            {
            }
            finally
            {
                _controller = null;
            }
        }
    }
}
