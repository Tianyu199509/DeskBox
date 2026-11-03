// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using Color = Windows.UI.Color;
using DrawingPoint = System.Drawing.Point;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Views;

/// <summary>
/// Singleton shadowless 1x1 XAML anchor host for the native WinUI 3
/// <see cref="MenuFlyout"/> tray context menu.
/// <para>
/// Cold startup keeps zero XAML windows via <see cref="NativeTrayIconHost"/>.
/// After startup settles (or on first tray right-click), one reusable 1x1
/// transparent anchor window with <see cref="Win32Helper.DisableWindowFrameShadow"/>
/// realizes a warm <see cref="XamlRoot"/> and hosts the exact same
/// <see cref="MenuFlyout"/> (<c>ShouldConstrainToRootBounds = false</c> +
/// <c>App.xaml</c> direct <see cref="ThemedMenuAcrylicBackdrop"/>) used by widget
/// context menus. On dismissal the anchor window is cloaked and hidden rather
/// than destroyed, eliminating per-click window/backdrop cold-creation lag and
/// eliminating any DWM window-shadow-before-content flash.
/// </para>
/// </summary>
internal sealed class TrayContextMenuWindow : Window
{
    private const double MinMenuWidthDip = 196;
    private const double TaskbarGapDip = 6;
    private const double ScreenEdgeMarginDip = 6;

    private static TrayContextMenuWindow? s_instance;

    private readonly AppWindow _appWindow;
    private readonly Grid _rootGrid;
    private readonly Border _anchorElement;
    private SolidTintSystemBackdrop? _transparentBackdrop;
    private DispatcherQueueTimer? _outsidePressTimer;
    private FlyoutBridgeBandPromoter? _bandPromoter;
    private MenuFlyout? _activeFlyout;
    private Action<NativeTrayMenuEntry>? _onEntryInvoked;
    private NativeTrayMenuEntry? _invokedEntry;
    private long _sessionId;
    private bool _isMenuOpen;
    private bool _hasBeenActivated;
    private bool _wasMouseButtonDown;
    private bool _isShutdown;

    internal readonly record struct TrayAnchorPlacement(
        DrawingPoint AnchorPoint,
        FlyoutPlacementMode PlacementMode);

    private TrayContextMenuWindow()
    {
        Title = "DeskBox Tray Menu Anchor";

        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        ConfigureShadowlessAnchorWindowStyles();
        AttachTransparentBackdrop();
        SetCloaked(true);

        _anchorElement = new Border
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
        };

        _rootGrid = new Grid
        {
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            FlowDirection = FlowDirection.LeftToRight,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _rootGrid.Children.Add(_anchorElement);
        _rootGrid.KeyDown += OnRootGridKeyDown;

        Content = _rootGrid;

        Activated += OnWindowActivated;
        Closed += (_, _) => _isShutdown = true;
    }

    internal IntPtr WindowHandle { get; }

    internal static bool HasActiveMenu =>
        s_instance is { _isShutdown: false, _isMenuOpen: true };

    internal static void WarmUpIfNeeded(
        Action ensureLifelineBeforeClose,
        bool isDarkTheme)
    {
        if (s_instance is { _isShutdown: false })
        {
            return;
        }

        try
        {
            ensureLifelineBeforeClose();
            TrayContextMenuWindow host = EnsureInstance();
            host.RealizeWarmXamlRoot(isDarkTheme);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Tray] Anchor host prewarm skipped: {ex.Message}");
        }
    }

    internal static void DismissActiveIfOpen()
    {
        if (s_instance is { _isShutdown: false, _isMenuOpen: true } active)
        {
            active.CloseMenu(invokedEntry: null);
        }
    }

    internal static void Shutdown()
    {
        TrayContextMenuWindow? instance = s_instance;
        s_instance = null;
        instance?.ShutdownCore();
    }

    internal static bool TryShow(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment,
        bool isDarkTheme,
        bool isRightToLeft,
        Action ensureLifelineBeforeClose,
        Action<NativeTrayMenuEntry> onEntryInvoked)
    {
        if (entries.Count == 0)
        {
            return false;
        }

        DismissActiveIfOpen();
        ensureLifelineBeforeClose();

        try
        {
            TrayContextMenuWindow host = EnsureInstance();
            host.ShowMenu(
                entries,
                anchor,
                horizontalAlignment,
                verticalAlignment,
                isDarkTheme,
                isRightToLeft,
                onEntryInvoked);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Tray] Native Fluent MenuFlyout failed; falling back to Win32 menu: {ex.Message}");
            try
            {
                DismissActiveIfOpen();
            }
            catch
            {
                // Best-effort cleanup before native HMENU fallback.
            }

            return false;
        }
    }

    internal static TrayAnchorPlacement ComputeAnchorPlacement(
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment,
        double dpiScale,
        Win32Helper.RECT workArea)
    {
        double safeScale = dpiScale > 0 ? dpiScale : 1.0;
        int gapPx = Math.Max(2, (int)Math.Round(TaskbarGapDip * safeScale));
        int edgeMarginPx = Math.Max(2, (int)Math.Round(ScreenEdgeMarginDip * safeScale));

        int horizontalGapPx = verticalAlignment == TrayMenuVerticalAlignment.Center ? gapPx : 0;
        int x = horizontalAlignment switch
        {
            TrayMenuHorizontalAlignment.Right => anchor.X - horizontalGapPx,
            TrayMenuHorizontalAlignment.Left => anchor.X + horizontalGapPx,
            _ => anchor.X,
        };

        int y = verticalAlignment switch
        {
            TrayMenuVerticalAlignment.Top => anchor.Y + gapPx,
            TrayMenuVerticalAlignment.Center => anchor.Y,
            _ => anchor.Y - gapPx,
        };

        int minX = workArea.Left + edgeMarginPx;
        int maxX = Math.Max(minX, workArea.Right - edgeMarginPx);
        int minY = workArea.Top + edgeMarginPx;
        int maxY = Math.Max(minY, workArea.Bottom - edgeMarginPx);

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        FlyoutPlacementMode placementMode = (horizontalAlignment, verticalAlignment) switch
        {
            (TrayMenuHorizontalAlignment.Center, TrayMenuVerticalAlignment.Bottom) => FlyoutPlacementMode.Top,
            (TrayMenuHorizontalAlignment.Right, TrayMenuVerticalAlignment.Bottom) => FlyoutPlacementMode.TopEdgeAlignedRight,
            (TrayMenuHorizontalAlignment.Left, TrayMenuVerticalAlignment.Bottom) => FlyoutPlacementMode.TopEdgeAlignedLeft,
            (TrayMenuHorizontalAlignment.Center, TrayMenuVerticalAlignment.Top) => FlyoutPlacementMode.Bottom,
            (TrayMenuHorizontalAlignment.Right, TrayMenuVerticalAlignment.Top) => FlyoutPlacementMode.BottomEdgeAlignedRight,
            (TrayMenuHorizontalAlignment.Left, TrayMenuVerticalAlignment.Top) => FlyoutPlacementMode.BottomEdgeAlignedLeft,
            (TrayMenuHorizontalAlignment.Right, TrayMenuVerticalAlignment.Center) => FlyoutPlacementMode.Left,
            (TrayMenuHorizontalAlignment.Left, TrayMenuVerticalAlignment.Center) => FlyoutPlacementMode.Right,
            _ => FlyoutPlacementMode.Top,
        };

        return new TrayAnchorPlacement(new DrawingPoint(x, y), placementMode);
    }

    private static TrayContextMenuWindow EnsureInstance()
    {
        if (s_instance is not { _isShutdown: false } existing)
        {
            existing = new TrayContextMenuWindow();
            s_instance = existing;
        }

        return existing;
    }

    private void ConfigureShadowlessAnchorWindowStyles()
    {
        int extendedStyle = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TOPMOST;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE, extendedStyle);

        int style = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_STYLE, style);

        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            1,
            1,
            Win32Helper.SWP_NOMOVE |
            Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_FRAMECHANGED);

        Win32Helper.DisableWindowFrameShadow(WindowHandle);
    }

    private void AttachTransparentBackdrop()
    {
        try
        {
            Win32Helper.EnableTransparentWindowHost(WindowHandle);
            _transparentBackdrop ??= new SolidTintSystemBackdrop(Color.FromArgb(0, 0, 0, 0));
            if (!ReferenceEquals(SystemBackdrop, _transparentBackdrop))
            {
                SystemBackdrop = _transparentBackdrop;
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Tray] Transparent anchor backdrop unavailable: {ex.Message}");
            DisposeTransparentBackdrop();
            SystemBackdrop = null;
        }
    }

    private void DisposeTransparentBackdrop()
    {
        if (_transparentBackdrop is not null)
        {
            _transparentBackdrop.Dispose();
            _transparentBackdrop = null;
        }

        Win32Helper.DisableTransparentWindowHost(WindowHandle);
    }

    private void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Win32Helper.TrySetDwmWindowAttribute(WindowHandle, Win32Helper.DWMWA_CLOAK, ref value);
    }

    private void RealizeWarmXamlRoot(bool isDarkTheme)
    {
        if (_isShutdown || _isMenuOpen)
        {
            return;
        }

        SetCloaked(true);
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        Win32Helper.SetWindowTheme(WindowHandle, isDarkTheme);
        _rootGrid.RequestedTheme = WindowsCompatibilityService.IsHighContrast
            ? ElementTheme.Default
            : (isDarkTheme ? ElementTheme.Dark : ElementTheme.Light);

        _appWindow.MoveAndResize(new RectInt32(0, 0, 1, 1));
        _ = Win32Helper.ShowWindow(WindowHandle, Win32Helper.SW_SHOWNOACTIVATE);
        _rootGrid.UpdateLayout();
        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }

        _ = Win32Helper.ShowWindow(WindowHandle, Win32Helper.SW_HIDE);
        _ = CreatePresenterStyle(isDarkTheme, isRightToLeft: false, maxHeightDip: 960);
    }

    private void ShowMenu(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        DrawingPoint anchor,
        TrayMenuHorizontalAlignment horizontalAlignment,
        TrayMenuVerticalAlignment verticalAlignment,
        bool isDarkTheme,
        bool isRightToLeft,
        Action<NativeTrayMenuEntry> onEntryInvoked)
    {
        long session = ++_sessionId;
        _isMenuOpen = false;
        _hasBeenActivated = false;
        _invokedEntry = null;
        _onEntryInvoked = onEntryInvoked;

        AttachTransparentBackdrop();
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        Win32Helper.SetWindowTheme(WindowHandle, isDarkTheme);
        _rootGrid.RequestedTheme = WindowsCompatibilityService.IsHighContrast
            ? ElementTheme.Default
            : (isDarkTheme ? ElementTheme.Dark : ElementTheme.Light);

        if (!Win32Helper.TryGetMonitorWorkArea(anchor.X, anchor.Y, out _, out Win32Helper.RECT workArea))
        {
            workArea = new Win32Helper.RECT
            {
                Left = anchor.X - 640,
                Top = anchor.Y - 640,
                Right = anchor.X + 640,
                Bottom = anchor.Y + 640,
            };
        }

        // Move the cloaked anchor onto the target monitor first so Per-Monitor-V2
        // DPI scaling reflects the display where the tray icon lives.
        SetCloaked(true);
        _appWindow.MoveAndResize(new RectInt32(anchor.X, anchor.Y, 1, 1));

        double dpiScale = Win32Helper.GetDpiScaleForWindow(WindowHandle, _rootGrid.XamlRoot);
        TrayAnchorPlacement placement = ComputeAnchorPlacement(
            anchor,
            horizontalAlignment,
            verticalAlignment,
            dpiScale,
            workArea);

        RectInt32 anchorBounds = new(placement.AnchorPoint.X, placement.AnchorPoint.Y, 1, 1);
        _appWindow.MoveAndResize(anchorBounds);
        if (!_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        SetCloaked(false);
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            anchorBounds.X,
            anchorBounds.Y,
            1,
            1,
            Win32Helper.SWP_SHOWWINDOW);
        _rootGrid.UpdateLayout();

        _ = Win32Helper.SetForegroundWindow(WindowHandle);
        Activate();
        _rootGrid.Focus(FocusState.Programmatic);

        double safeScale = dpiScale > 0 ? dpiScale : 1.0;
        double maxHeightDip = Math.Max(
            320,
            ((workArea.Bottom - workArea.Top) / safeScale) - (ScreenEdgeMarginDip * 2));
        MenuFlyout flyout = BuildMenuFlyout(
            entries,
            isDarkTheme,
            isRightToLeft,
            maxHeightDip,
            session);
        _activeFlyout = flyout;
        _isMenuOpen = true;

        flyout.ShowAt(
            _anchorElement,
            new FlyoutShowOptions
            {
                Placement = placement.PlacementMode,
                ShowMode = FlyoutShowMode.Standard,
            });
    }

    private MenuFlyout BuildMenuFlyout(
        IReadOnlyList<NativeTrayMenuEntry> entries,
        bool isDarkTheme,
        bool isRightToLeft,
        double maxHeightDip,
        long session)
    {
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false,
        };

        if (CreatePresenterStyle(isDarkTheme, isRightToLeft, maxHeightDip) is { } presenterStyle)
        {
            flyout.MenuFlyoutPresenterStyle = presenterStyle;
        }

        foreach (NativeTrayMenuEntry entry in entries)
        {
            if (entry.IsSeparator)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            NativeTrayMenuEntry captured = entry;
            var item = new MenuFlyoutItem
            {
                Text = captured.Text ?? string.Empty,
                IsEnabled = captured.IsEnabled,
            };
            if (!string.IsNullOrEmpty(captured.Glyph))
            {
                item.Icon = new FontIcon { Glyph = captured.Glyph };
            }

            item.Click += (_, _) =>
            {
                if (_sessionId == session && captured.IsEnabled)
                {
                    _invokedEntry = captured;
                }
            };

            flyout.Items.Add(item);
        }

        flyout.Opened += (_, _) =>
        {
            if (_isShutdown || !_isMenuOpen || _sessionId != session)
            {
                return;
            }

            StopBandPromoter();
            _bandPromoter = FlyoutBridgeBandPromoter.TryBegin();
            StartOutsidePressMonitor(session);
        };

        flyout.Closed += (_, _) =>
        {
            if (_sessionId == session && _isMenuOpen)
            {
                CloseMenu(invokedEntry: _invokedEntry);
            }
        };

        return flyout;
    }

    private static Style? CreatePresenterStyle(
        bool isDarkTheme,
        bool isRightToLeft,
        double maxHeightDip)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(typeof(MenuFlyoutPresenter), out object? baseValue) &&
            baseValue is Style baseStyle)
        {
            style.BasedOn = baseStyle;
        }

        if (!WindowsCompatibilityService.IsHighContrast)
        {
            style.Setters.Add(new Setter(
                FrameworkElement.RequestedThemeProperty,
                isDarkTheme ? ElementTheme.Dark : ElementTheme.Light));
        }

        style.Setters.Add(new Setter(
            FrameworkElement.FlowDirectionProperty,
            isRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight));
        style.Setters.Add(new Setter(
            FrameworkElement.MinWidthProperty,
            MinMenuWidthDip));
        style.Setters.Add(new Setter(
            FrameworkElement.MaxHeightProperty,
            maxHeightDip));
        return style;
    }

    private void StartOutsidePressMonitor(long session)
    {
        _wasMouseButtonDown = Win32Helper.IsAnyMouseButtonDown();
        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        if (_outsidePressTimer is null)
        {
            _outsidePressTimer = queue.CreateTimer();
            _outsidePressTimer.Interval = TimeSpan.FromMilliseconds(50);
            _outsidePressTimer.IsRepeating = true;
            _outsidePressTimer.Tick += OutsidePressTimer_Tick;
        }

        _outsidePressTimer.Start();
        _ = session;
    }

    private void StopOutsidePressMonitor()
    {
        _outsidePressTimer?.Stop();
    }

    private void OutsidePressTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isShutdown || !_isMenuOpen)
        {
            sender.Stop();
            return;
        }

        bool downNow = Win32Helper.IsAnyMouseButtonDown();
        bool risingEdge = downNow && !_wasMouseButtonDown;
        _wasMouseButtonDown = downNow;
        if (!risingEdge)
        {
            return;
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) &&
            !IsPointInsideOpenMenuBridge(cursor))
        {
            long session = _sessionId;
            UiDispatch.PostAfterCallback(() =>
            {
                if (_sessionId == session && _isMenuOpen)
                {
                    CloseMenu(invokedEntry: _invokedEntry);
                }
            });
        }
    }

    private static bool IsPointInsideOpenMenuBridge(Win32Helper.POINT pt)
    {
        foreach (IntPtr bridge in Win32Helper.GetVisiblePopupBridgeWindows())
        {
            if (Win32Helper.GetWindowRect(bridge, out Win32Helper.RECT rect) &&
                pt.X >= rect.Left &&
                pt.X < rect.Right &&
                pt.Y >= rect.Top &&
                pt.Y < rect.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCurrentProcessPopupBridge(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = Win32Helper.GetAncestor(hwnd, Win32Helper.GA_ROOT);
        IntPtr candidate = root != IntPtr.Zero ? root : hwnd;
        foreach (IntPtr bridge in Win32Helper.GetVisiblePopupBridgeWindows())
        {
            if (bridge == candidate || bridge == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    private void StopBandPromoter()
    {
        _bandPromoter?.Dispose();
        _bandPromoter = null;
    }

    private void OnRootGridKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_isShutdown || !_isMenuOpen)
        {
            return;
        }

        if (args.Key is VirtualKey.Escape or VirtualKey.Menu or VirtualKey.F10)
        {
            args.Handled = true;
            CloseMenu(invokedEntry: null);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isShutdown || !_isMenuOpen)
        {
            return;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _hasBeenActivated = true;
            return;
        }

        if (_hasBeenActivated)
        {
            long session = _sessionId;
            SetCloaked(true);
            UiDispatch.PostAfterCallback(() =>
            {
                if (_sessionId != session || !_isMenuOpen)
                {
                    return;
                }

                IntPtr fg = Win32Helper.GetForegroundWindow();
                if (fg == WindowHandle || IsCurrentProcessPopupBridge(fg))
                {
                    SetCloaked(false);
                    return;
                }

                CloseMenu(invokedEntry: _invokedEntry);
            });
        }
    }

    private void CloseMenu(NativeTrayMenuEntry? invokedEntry)
    {
        if (!_isMenuOpen)
        {
            return;
        }

        _isMenuOpen = false;
        ++_sessionId;

        StopOutsidePressMonitor();
        StopBandPromoter();

        MenuFlyout? flyoutToHide = _activeFlyout;
        _activeFlyout = null;
        Action<NativeTrayMenuEntry>? callback = _onEntryInvoked;
        _onEntryInvoked = null;
        _invokedEntry = null;

        try
        {
            flyoutToHide?.Hide();
        }
        catch
        {
            // Flyout may already be closing natively.
        }

        SetCloaked(true);

        IntPtr foregroundBeforeClose = Win32Helper.GetForegroundWindow();
        if (invokedEntry is null &&
            (foregroundBeforeClose == WindowHandle || IsCurrentProcessPopupBridge(foregroundBeforeClose)))
        {
            App.Current?.WidgetManager?.RestoreForegroundToRaisedWidgetBeforeAuxiliaryHide(WindowHandle);
            foregroundBeforeClose = Win32Helper.GetForegroundWindow();
        }

        if (!_isShutdown)
        {
            try
            {
                if (_appWindow.IsVisible)
                {
                    _appWindow.Hide();
                }

                _ = Win32Helper.ShowWindow(WindowHandle, Win32Helper.SW_HIDE);
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[Tray] Anchor window park failed: {ex.Message}");
            }
        }

        if (invokedEntry is null &&
            foregroundBeforeClose != IntPtr.Zero &&
            foregroundBeforeClose != WindowHandle &&
            !IsCurrentProcessPopupBridge(foregroundBeforeClose) &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            App.Current?.IsDeskBoxWindow(foregroundBeforeClose) == true &&
            Win32Helper.IsWindow(foregroundBeforeClose) &&
            Win32Helper.IsWindowVisible(foregroundBeforeClose) &&
            Win32Helper.GetForegroundWindow() != foregroundBeforeClose)
        {
            _ = Win32Helper.SetForegroundWindow(foregroundBeforeClose);
        }

        if (invokedEntry is { IsEnabled: true, Action: not null } && callback is not null)
        {
            UiDispatch.PostAfterCallback(() => callback(invokedEntry));
        }
    }

    private void ShutdownCore()
    {
        if (_isShutdown)
        {
            return;
        }

        if (_isMenuOpen)
        {
            CloseMenu(invokedEntry: null);
        }

        _isShutdown = true;
        if (_outsidePressTimer is not null)
        {
            _outsidePressTimer.Stop();
            _outsidePressTimer.Tick -= OutsidePressTimer_Tick;
            _outsidePressTimer = null;
        }

        StopBandPromoter();
        _rootGrid.KeyDown -= OnRootGridKeyDown;
        Activated -= OnWindowActivated;

        _rootGrid.Children.Clear();
        Content = null;
        SystemBackdrop = null;
        DisposeTransparentBackdrop();

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            App.Log($"[Tray] Anchor window shutdown close failed: {ex.Message}");
        }
    }
}
