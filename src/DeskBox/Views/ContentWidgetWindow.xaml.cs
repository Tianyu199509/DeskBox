using System.Numerics;
using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Lightweight host window for future non-file widget content.
/// User-facing creation remains gated by WidgetRegistry.
/// </summary>
public sealed partial class ContentWidgetWindow : Window, IDesktopWidgetWindow
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

    private readonly WidgetConfig _config;
    private readonly SettingsService _settingsService;
    private readonly WidgetWindowDiagnostics _diagnostics;
    private readonly WidgetShellContentHost _contentHost;
    private readonly ContentWidgetTitleViewModel _titleViewModel;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _isResizing;
    private bool _isApplyingBounds;
    private bool _isHidePrepared;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private PointInt32 _initialWindowPos;
    private SizeInt32 _initialWindowSize;

    public ContentWidgetWindow(
        WidgetConfig config,
        IWidgetContent content,
        SettingsService settingsService,
        WidgetContentDescriptor descriptor)
    {
        _config = config;
        _settingsService = settingsService;

        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _diagnostics = new WidgetWindowDiagnostics("Content", _config, () => _hWnd);
        _contentHost = new WidgetShellContentHost(ContentWidgetShell);

        _titleViewModel = new ContentWidgetTitleViewModel(_config);
        ContentWidgetShell.DataContext = _titleViewModel;
        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;

        ConfigureWindow();
        SetupEventHandlers();
        _ = _contentHost.SetContentAsync(content);

        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        _titleViewModel.RefreshDisplayName();
    }

    public IntPtr WindowHandle => _hWnd;

    public WidgetWindowIdentity Identity => _diagnostics.Identity;

    public Windows.Foundation.Rect AnimationBounds => _diagnostics.AnimationBounds;

    public WidgetConfig Config => _config;

    private bool _isVisibleOnDesktop;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private DateTime _lastElevateForInteractionUtc = DateTime.MinValue;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostSafetyTimer;

    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        ApplySurfaceStyle();
        _contentHost.ApplyAppearance();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
    }

    public void PrepareTrayShowAnimation()
    {
        _isHidePrepared = false;
        RestoreVisualState();
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        PushToBottom();
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        ShowWithoutActivation(persistVisibility);
        HoldTemporaryTopMost();
    }

    public void PlayTrayShowAnimation()
    {
        RestoreVisualState();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible)
        {
            return false;
        }

        _isHidePrepared = true;
        Visible = false;
        _config.IsVisible = false;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHidePrepared)
        {
            return;
        }

        HideWindow();
        _isHidePrepared = false;
    }

    public void ActivateRaisedFromTrayBatch()
    {
        if (!Visible)
        {
            return;
        }

        HoldTemporaryTopMost();
        base.Activate();
        RootGrid.Focus(FocusState.Programmatic);
        _contentHost.OnActivated();
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            return;
        }

        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        Win32Helper.BringWindowToFront(_hWnd);
        HoldTemporaryTopMost();
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayerFromManager();
    }

    public void RestoreDesktopLayerFromManager()
    {
        if (!Visible)
        {
            return;
        }

        RestoreDesktopLayer(force: true);
        _contentHost.OnDeactivated();
    }

    public void HideWindow()
    {
        Visible = false;
        _config.IsVisible = false;
        _settingsService.SaveDebounced();
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        RestoreVisualState();
        _contentHost.OnDeactivated();
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        int style = Win32Helper.GetWindowLong(_hWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(_hWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            _hWnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        _appWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;
        ApplyWindowBounds(
            (int)Math.Round(_config.X),
            (int)Math.Round(_config.Y),
            (int)Math.Round(_config.Width),
            (int)Math.Round(_config.Height),
            persist: false);

        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            RootGrid.Focus(FocusState.Programmatic);
        };
        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        Activated += ContentWidgetWindow_Activated;
        _appWindow.Changed += AppWindow_Changed;

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                child.PointerMoved += ResizeBorder_PointerMoved;
                child.PointerReleased += ResizeBorder_PointerReleased;
                child.PointerEntered += ResizeBorder_PointerEntered;
            }
        }

        Closed += (_, _) =>
        {
            Visible = false;
            App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _appWindow.Changed -= AppWindow_Changed;
            DisposeAcrylicController();
            _contentHost.OnDeactivated();

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    child.PointerMoved -= ResizeBorder_PointerMoved;
                    child.PointerReleased -= ResizeBorder_PointerReleased;
                    child.PointerEntered -= ResizeBorder_PointerEntered;
                }
            }
        };
    }

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _contentHost.OnDeactivated();
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] Content Deactivated→Restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
            return;
        }

        _contentHost.OnActivated();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingBounds)
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            UpdateConfigBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    private void OnSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ContentWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ApplyAppearancePreview();
    }

    private void ShowWithoutActivation(bool persistVisibility)
    {
        RestoreVisualState();
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        _config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        ApplyBackdropPreference();
    }

    private void PushToBottom()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.SetWindowToBottom(_hWnd);
        App.Log($"[ZOrder] Content PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    private void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hWnd)
        {
            Win32Helper.BringWindowToFront(foreground);
        }
        App.Log($"[ZOrder] Content ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    private void HoldTemporaryTopMost()
    {
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        Win32Helper.SetWindowTopMost(_hWnd);
        App.Log($"[ZOrder] Content HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    private void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        _lastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            return;
        }

        App.Log($"[ZOrder] Content RestoreDesktopLayer EXECUTING force={force}");
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
    }

    private void StartTopMostSafetyTimer()
    {
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = DispatcherQueue.CreateTimer();
        _topMostSafetyTimer.IsRepeating = false;
        _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
        _topMostSafetyTimer.Tick += (_, _) =>
        {
            _topMostSafetyTimer.Stop();
            _topMostSafetyTimer = null;
            if (!_isAtDesktopLayer && App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] Content safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
        };
        _topMostSafetyTimer.Start();
    }

    private void RestoreVisualState()
    {
        RootGrid.Opacity = 1;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RootGrid);
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Offset = Vector3.Zero;
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist)
    {
        width = Math.Max(MinWidth, width);
        height = Math.Max(MinHeight, height);
        _isApplyingBounds = true;
        try
        {
            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally
        {
            _isApplyingBounds = false;
        }

        UpdateConfigBounds(x, y, width, height, persist);
    }

    private void UpdateConfigBounds(int x, int y, int width, int height, bool persist)
    {
        _config.X = x;
        _config.Y = y;
        _config.Width = width;
        _config.Height = height;
        if (persist)
        {
            _settingsService.UpdateWidget(_config, notifySubscribers: false);
            _settingsService.SaveDebounced();
        }
    }

    private void ApplyWindowCornerPreference()
    {
        int cornerPreference = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    private void ApplyBackdropPreference()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(_settingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            int backdropType;
            if (ApplyAcrylicController(isDark, tintColor, surfaceOpacity))
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                SystemBackdrop ??= new DesktopAcrylicBackdrop();
            }

            Win32Helper.DisableAccentPolicy(_hWnd);
        }
        catch (Exception ex)
        {
            App.Log($"ContentWidget ApplyBackdropPreference fallback: {ex}");
            SystemBackdrop = null;
            DisposeAcrylicController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private bool ApplyAcrylicController(bool isDark, Windows.UI.Color tintColor, double surfaceOpacity)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _backdropConfiguration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (_acrylicController is null || _acrylicController.IsClosed)
        {
            SystemBackdrop = null;
            _acrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!_acrylicController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }

        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _acrylicController.Kind = DesktopAcrylicKind.Thin;
        _acrylicController.TintColor = tintColor;
        _acrylicController.FallbackColor = tintColor;
        _acrylicController.TintOpacity = (float)(isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44));
        _acrylicController.LuminosityOpacity = (float)(isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86));
        return true;
    }

    private void DisposeAcrylicController()
    {
        if (_acrylicController is null)
        {
            return;
        }

        try
        {
            _acrylicController.RemoveAllSystemBackdropTargets();
            _acrylicController.Dispose();
        }
        catch
        {
        }

        _acrylicController = null;
    }

    private Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
    {
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var baseColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BuildAccentSurfaceColor(
            isDark,
            accentColor,
            baseColor,
            accentMix: isDark ? 0.08 : 0.16,
            overlayMix: isDark ? 0.04 : 0.08);
    }

    private void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(_settingsService.Settings.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
        byte chromeAlpha = 0x18;
        var borderColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.75), 0, 255), 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), chromeAlpha);
        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        ContentWidgetShell.BackgroundSurface.Background = new SolidColorBrush(surfaceColor);
        ContentWidgetShell.BackgroundSurface.BorderBrush = new SolidColorBrush(borderColor);
        ContentWidgetShell.Divider.Background = new SolidColorBrush(dividerColor);
        ContentWidgetShell.TitleIconElement.Foreground = new SolidColorBrush(iconForeground);
    }

    private static Windows.UI.Color BuildFrostedSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        double surfaceOpacity)
    {
        double materialOpacity = isDark
            ? Math.Clamp(surfaceOpacity * 0.78, 0.10, 0.82)
            : Math.Clamp(surfaceOpacity * 0.78, 0.0, 0.78);

        return ApplySurfaceOpacity(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x21, 0x24, 0x2A)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.18,
                overlayMix: isDark ? 0.15 : 0.04),
            materialOpacity);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var mixed = BlendColors(baseColor, accentColor, accentMix);
        var overlay = isDark
            ? ColorHelper.FromArgb(0xFF, 0x2B, 0x2F, 0x36)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(mixed, overlay, overlayMix);
    }

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        return Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            color.R,
            color.G,
            color.B);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color from, Windows.UI.Color to, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);
        return Windows.UI.Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsPositionLocked)
        {
            return;
        }

        var properties = e.GetCurrentPoint(ContentWidgetShell.TitleBar).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        ContentWidgetShell.TitleBar.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        ApplyWindowBounds(
            _initialWindowPos.X + deltaX,
            _initialWindowPos.Y + deltaY,
            _initialWindowSize.Width,
            _initialWindowSize.Height,
            persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ContentWidgetShell.TitleBar.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        UpdateConfigBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        e.Handled = true;
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsSizeLocked || sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeDirection = element.Tag as string ?? string.Empty;
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;
        int newWidth = _initialWindowSize.Width;
        int newHeight = _initialWindowSize.Height;
        int newX = _initialWindowPos.X;
        int newY = _initialWindowPos.Y;

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(MinWidth, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(MinHeight, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        ApplyWindowBounds(newX, newY, newWidth, newHeight, persist: false);
        e.Handled = true;
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizing || sender is not FrameworkElement element)
        {
            return;
        }

        _isResizing = false;
        _resizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        UpdateConfigBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = _config.IsSizeLocked
            ? InputSystemCursorShape.Arrow
            : element is FrameworkElement frameworkElement
                ? frameworkElement.Tag switch
                {
                    "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
                    "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
                    "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
                    "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
                    _ => InputSystemCursorShape.Arrow
                }
                : InputSystemCursorShape.Arrow;

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell.MoreActionButton);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ShowFlyoutWithInteraction(CreateMoreFlyout(), ContentWidgetShell.TitleBar, e.GetPosition(ContentWidgetShell.TitleBar));
        e.Handled = true;
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        flyout.Items.Add(CreateToggleMenuItem(
            App.Current.LocalizationService.T("Widget.LockPosition"),
            "\uE72E",
            _config.IsPositionLocked,
            SetPositionLocked));
        flyout.Items.Add(CreateToggleMenuItem(
            App.Current.LocalizationService.T("Widget.LockSize"),
            "\uE740",
            _config.IsSizeLocked,
            SetSizeLocked));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += async (_, _) => await ShowRenameDialogAsync();
        flyout.Items.Add(rename);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteWidget = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T("Widget.Tooltip.DeleteWidget"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += async (_, _) =>
        {
            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.RemoveWidgetAsync(_config.Id);
            }
        };
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private static ToggleMenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            IsChecked = isChecked
        };
        item.Click += (_, _) => applyValue(item.IsChecked);
        return item;
    }

    private void SetPositionLocked(bool value)
    {
        if (_config.IsPositionLocked == value)
        {
            return;
        }

        _config.IsPositionLocked = value;
        _settingsService.UpdateWidget(_config);
    }

    private void SetSizeLocked(bool value)
    {
        if (_config.IsSizeLocked == value)
        {
            return;
        }

        _config.IsSizeLocked = value;
        _settingsService.UpdateWidget(_config);
    }

    private async Task ShowRenameDialogAsync()
    {
        var localization = App.Current.LocalizationService;
        var textBox = new TextBox
        {
            Text = _config.Name,
            PlaceholderText = localization.T("Widget.TitlePlaceholder"),
            MinWidth = 260
        };

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = localization.T("Common.Rename"),
            Content = textBox,
            PrimaryButtonText = localization.T("Common.Save"),
            CloseButtonText = localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.Opened += (_, _) =>
        {
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectAll();
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = textBox.Text.Trim();
        try
        {
            await App.Current.WidgetManager!.RenameWidgetAsync(_config.Id, newName);
            _titleViewModel.RefreshDisplayName();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(localization.T("Widget.RenameFailed"), ex.Message);
        }
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var localization = App.Current.LocalizationService;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxWidth = 320
            },
            CloseButtonText = localization.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    private void ShowFlyoutWithInteraction(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction("content-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            App.Current.WidgetManager?.EndWidgetInteraction("content-flyout-closed");
            if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer("content-flyout-closed") == true)
            {
                return;
            }

            RestoreDesktopLayerFromManager();
        };

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private sealed class ContentWidgetTitleViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public ContentWidgetTitleViewModel(WidgetConfig config)
        {
            Config = config;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public WidgetConfig Config { get; }

        public string DisplayName
        {
            get
            {
                if (Config.IsDefaultTitle)
                {
                    var localization = App.Current.LocalizationService;
                    var key = Config.WidgetKind switch
                    {
                        WidgetKind.Todo => "Todo.Title",
                        WidgetKind.Weather => "Weather.Title",
                        WidgetKind.Tags => "Tags.Title",
                        WidgetKind.Music => "Music.Title",
                        WidgetKind.SystemMonitor => "SystemMonitor.Title",
                        _ => ""
                    };
                    if (!string.IsNullOrEmpty(key))
                    {
                        var localized = localization.T(key);
                        if (!string.IsNullOrEmpty(localized))
                            return localized;
                    }
                }

                return string.IsNullOrWhiteSpace(Config.Name)
                    ? Config.WidgetKind.ToString()
                    : Config.Name;
            }
        }

        public double TitleIconSize => Math.Clamp(Math.Round(SettingsService.DefaultIconSize * 0.72 * 0.56 * 0.54), 11, 18);

        public double TitleTextSize => 14;

        public void RefreshDisplayName()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }
}
