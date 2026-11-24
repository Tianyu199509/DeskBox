using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Compact Mica/Acrylic floating Quick-Paste popup window summoned via global shortcut
/// (default <c>Alt + V</c>). Anchors to the active text caret, mouse cursor, or screen center,
/// supports instant type-to-filter + keyboard/number-slot selection, pastes directly back into
/// the previously focused external window, and dismisses automatically on paste, <c>Esc</c>, or blur.
/// </summary>
public sealed partial class ClipboardQuickPasteWindow : Window
{
    private const int DesiredWidthDip = 368;
    private const int DesiredHeightDip = 448;
    private const string QuickPasteIconCacheScope = "clipboard-quick-paste";

    private readonly ClipboardHistoryService _clipboardService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly ThemeService? _themeService;
    private readonly Action? _onBeforeColdClose;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly ClipboardWidgetViewModel _viewModel;
    private readonly ClipboardWidgetContent _content;
    private DispatcherQueueTimer? _coldHiddenCleanupTimer;
    private long _sessionId;
    private bool _isVisible;
    private bool _isClosed;
    private bool _isHeaderDragging;
    private Win32Helper.POINT _dragStartCursor;
    private PointInt32 _dragStartWindowPos;

    public ClipboardQuickPasteWindow(
        ClipboardHistoryService clipboardService,
        SettingsService settingsService,
        LocalizationService localizationService,
        ThemeService? themeService = null,
        Action? onBeforeColdClose = null)
    {
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);

        _clipboardService = clipboardService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _themeService = themeService;
        _onBeforeColdClose = onBeforeColdClose;

        Title = _localizationService.T("Window.ClipboardQuickPaste.Title");
        InitializeComponent();
        ApplyLocalizedChrome();
        _themeService?.TrackWindow(this);
        WindowsCompatibilityService.ApplySafeBackdrop(this);

        _hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        ConfigurePopupWindowChrome();

        var syntheticConfig = new WidgetConfig
        {
            Id = "clipboard-quick-paste",
            WidgetKind = WidgetKind.Clipboard
        };

        _viewModel = new ClipboardWidgetViewModel(
            syntheticConfig,
            _clipboardService,
            _settingsService,
            _localizationService,
            uiDispatch: static action =>
            {
                var dq = App.UiDispatcherQueue;
                if (dq is null || dq.HasThreadAccess)
                {
                    action();
                }
                else
                {
                    dq.TryEnqueue(() => action());
                }
            },
            iconLoader: static (path, decodeWidth) => IconHelper.GetIconAsync(
                path,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: true,
                decodePixelWidth: decodeWidth,
                cacheScope: QuickPasteIconCacheScope),
            thumbnailLoader: static (path, decodeWidth) => IconHelper.GetIconAsync(
                path,
                hideShortcutArrowOverlay: true,
                showImageFilesAsIcons: false,
                decodePixelWidth: decodeWidth,
                cacheScope: QuickPasteIconCacheScope));

        _viewModel.SetViewAttached(false);

        _content = new ClipboardWidgetContent(_viewModel, _settingsService, _localizationService)
        {
            IsQuickPastePopup = true,
            QuickPasteExecutor = ExecuteQuickPasteAndHideAsync,
            QuickPasteQueueNextExecutor = ExecuteQuickPasteQueueNextAndHideAsync
        };
        _content.OnWindowVisibilityChanged(false);
        _content.DismissRequested += OnContentDismissRequested;
        ContentHost.Content = _content;

        _localizationService.LanguageChanged += OnLanguageChanged;
        WindowsCompatibilityService.HighContrastChanged += OnHighContrastChanged;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    public IntPtr WindowHandle => _hwnd;

    public bool IsPopupVisible => !_isClosed && _isVisible;

    public bool IsClosed => _isClosed;

    public async Task TogglePopupAsync()
    {
        if (_isClosed)
        {
            return;
        }

        if (_isVisible &&
            (Win32Helper.GetForegroundWindow() == _hwnd ||
             _content.IsPeekPopoverOpen ||
             WidgetEditorPopoverWindow.HasOpenPopoverForOwner(_hwnd)))
        {
            HidePopup();
            return;
        }

        await ShowPopupAsync();
    }

    public async Task ShowPopupAsync()
    {
        if (_isClosed)
        {
            return;
        }

        CancelColdHiddenCleanupTimer();
        long session = ++_sessionId;

        // Capture the external foreground window BEFORE activating our popup so
        // PasteItemToExternalWindowAsync restores focus to the exact target app.
        IntPtr currentForeground = Win32Helper.GetForegroundWindow();
        if (currentForeground != _hwnd &&
            App.Current?.IsDeskBoxWindow(currentForeground) != true)
        {
            _clipboardService.CaptureExternalForegroundWindow();
        }

        RectInt32 targetBounds = ComputeTargetBounds();

        _viewModel.SetViewAttached(true);
        if (!_viewModel.IsInitialized)
        {
            await _viewModel.InitializeAsync();
        }

        if (_isClosed || session != _sessionId)
        {
            return;
        }

        WindowsCompatibilityService.ApplySafeBackdrop(this);
        _appWindow.MoveAndResize(targetBounds);
        _isVisible = true;
        _content.OnWindowVisibilityChanged(true);
        _appWindow.Show();
        _ = Win32Helper.SetWindowPos(
            _hwnd,
            Win32Helper.HWND_TOPMOST,
            targetBounds.X,
            targetBounds.Y,
            targetBounds.Width,
            targetBounds.Height,
            Win32Helper.SWP_SHOWWINDOW);
        DeskBox.Helpers.TransientInputFocusGuard.ActivateWindowAndFocusIsland(this, _hwnd);
        _content.PrepareForQuickPastePopup();
    }

    public void HidePopup()
    {
        HidePopupCore(detachViewAssets: true);
    }

    private void HidePopupCore(bool detachViewAssets)
    {
        if (_isClosed || !_isVisible)
        {
            return;
        }

        _sessionId++;
        _isHeaderDragging = false;
        _isVisible = false;
        _content.OnWindowVisibilityChanged(false);

        try
        {
            _appWindow.Hide();
        }
        catch
        {
        }

        if (detachViewAssets)
        {
            ReleaseColdViewAssets();
        }

        ArmColdHiddenCleanupTimer();
    }

    private void OnContentDismissRequested(object? sender, EventArgs e) => HidePopup();

    private void ArmColdHiddenCleanupTimer()
    {
        int delaySeconds = PerformanceSettingsPolicy
            .Resolve(_settingsService.Settings)
            .TransientWindowReleaseDelaySeconds;
        if (delaySeconds <= 0)
        {
            CancelColdHiddenCleanupTimer();
            return;
        }

        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        TimeSpan interval = TimeSpan.FromSeconds(delaySeconds);
        if (_coldHiddenCleanupTimer is null)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = interval;
            timer.Tick += ColdHiddenCleanupTimer_Tick;
            _coldHiddenCleanupTimer = timer;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _coldHiddenCleanupTimer.Stop();
            _coldHiddenCleanupTimer.Interval = interval;
        }

        _coldHiddenCleanupTimer.Start();
    }

    private void CancelColdHiddenCleanupTimer()
    {
        _coldHiddenCleanupTimer?.Stop();
    }

    private void DisposeColdHiddenCleanupTimer()
    {
        if (_coldHiddenCleanupTimer is not { } timer)
        {
            return;
        }

        _coldHiddenCleanupTimer = null;
        timer.Tick -= ColdHiddenCleanupTimer_Tick;
        try
        {
            timer.Stop();
        }
        catch
        {
        }

        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void ColdHiddenCleanupTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (IsPopupVisible || _isClosed)
        {
            return;
        }

        if (_content.IsModalDialogOpen ||
            _content.IsPeekPopoverOpen ||
            WidgetEditorPopoverWindow.HasOpenPopoverForOwner(_hwnd))
        {
            ArmColdHiddenCleanupTimer();
            return;
        }

        ReleaseColdViewAssets();
        App.Log("[Clipboard] Cold-hidden quick-paste popup closing to reclaim window shell");
        _onBeforeColdClose?.Invoke();
        Close();
    }

    private void TransferForegroundBeforePasteHide()
    {
        IntPtr targetHwnd = _clipboardService.LastExternalForegroundWindow;
        if (targetHwnd != IntPtr.Zero &&
            targetHwnd != _hwnd &&
            Win32Helper.IsWindow(targetHwnd) &&
            Win32Helper.IsWindowVisible(targetHwnd))
        {
            _ = Win32Helper.SetForegroundWindow(targetHwnd);
        }
    }

    private async Task ExecuteQuickPasteAndHideAsync(ClipboardItemViewModel item, bool preferPlainText)
    {
        // Transfer foreground to the target external window BEFORE hiding our
        // popup so DeskBox exercises its live Win32 foreground lock cleanly.
        TransferForegroundBeforePasteHide();
        HidePopupCore(detachViewAssets: false);
        try
        {
            (bool copied, bool pasted) = await _viewModel.PasteItemAsync(item, preferPlainText);
            if (copied && !pasted)
            {
                App.Log("[Clipboard] Quick-paste fell back to clipboard copy (UIPI or target unavailable)");
            }
        }
        finally
        {
            if (!_isVisible && !_isClosed)
            {
                ReleaseColdViewAssets();
            }
        }
    }

    private async Task ExecuteQuickPasteQueueNextAndHideAsync(bool preferPlainText)
    {
        TransferForegroundBeforePasteHide();
        HidePopupCore(detachViewAssets: false);
        try
        {
            (bool copied, bool pasted) = await _viewModel.PasteNextQueueItemAsync(preferPlainText);
            if (copied && !pasted)
            {
                App.Log("[Clipboard] Quick-paste queue next fell back to clipboard copy (UIPI or target unavailable)");
            }
        }
        finally
        {
            if (!_isVisible && !_isClosed)
            {
                ReleaseColdViewAssets();
            }
        }
    }

    private void ReleaseColdViewAssets()
    {
        _viewModel.SetViewAttached(false);
        _viewModel.ReleaseCachedThumbnails();
        IconHelper.ClearCacheScope(QuickPasteIconCacheScope);
    }

    private RectInt32 ComputeTargetBounds()
    {
        string placementMode = _settingsService.Settings.Clipboard.ClipboardPopupPlacementMode;
        _ = Win32Helper.GetCursorPos(out Win32Helper.POINT mousePoint);
        if (!ClipboardPlatformInterop.TryGetActiveInputAnchorPoint(
                _clipboardService.LastExternalForegroundWindow,
                out Win32Helper.POINT anchor,
                out _))
        {
            anchor = mousePoint;
        }

        Win32Helper.POINT referencePoint = string.Equals(
            placementMode,
            ClipboardSettingsSlice.PopupFollowMouse,
            StringComparison.OrdinalIgnoreCase)
            ? mousePoint
            : anchor;

        double scale = Win32Helper.GetDpiScaleForPoint(referencePoint.X, referencePoint.Y);
        int widthPx = Math.Max(280, (int)Math.Round(DesiredWidthDip * scale));
        int heightPx = Math.Max(320, (int)Math.Round(DesiredHeightDip * scale));
        int marginPx = Math.Max(8, (int)Math.Round(12 * scale));
        int caretLineHeightPx = Math.Max(18, (int)Math.Round(26 * scale));

        if (!Win32Helper.TryGetMonitorWorkArea(referencePoint.X, referencePoint.Y, out _, out Win32Helper.RECT workArea))
        {
            workArea = new Win32Helper.RECT
            {
                Left = 0,
                Top = 0,
                Right = Math.Max(widthPx + 32, 1920),
                Bottom = Math.Max(heightPx + 32, 1080)
            };
        }

        Win32Helper.POINT clamped = ClipboardPlatformInterop.ComputeClampedPopupPosition(
            placementMode,
            anchor,
            mousePoint,
            workArea,
            widthPx,
            heightPx,
            marginPx,
            caretLineHeightPx);

        return new RectInt32(clamped.X, clamped.Y, widthPx, heightPx);
    }

    private void ConfigurePopupWindowChrome()
    {
        WindowShellState.TryApplyFloatingPopupPresenter(_appWindow);
        SurfaceBorder.CornerRadius = WindowsCompatibilityService.SupportsNativeWindowCorners
            ? new CornerRadius(8)
            : new CornerRadius(0);
        if (WindowsCompatibilityService.SupportsNativeWindowCorners)
        {
            int cornerPreference = Win32Helper.DWMWCP_ROUND;
            _ = Win32Helper.TrySetDwmWindowAttribute(
                _hwnd,
                Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference);
            Win32Helper.SetWindowBorderColor(_hwnd, unchecked((int)0xFFFFFFFE));
        }

        int exStyle = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TOPMOST;
        exStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE, exStyle);
        _ = Win32Helper.SetWindowPos(
            _hwnd,
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
    }

    private void ApplyLocalizedChrome()
    {
        string title = _localizationService.T("Window.ClipboardQuickPaste.Title");
        Title = title;
        PopupTitleText.Text = title;
        string closeLabel = _localizationService.T("Common.Close");
        AutomationProperties.SetName(ClosePopupButton, closeLabel);
        ToolTipService.SetToolTip(ClosePopupButton, closeLabel);
    }

    private void ClosePopupButton_Click(object sender, RoutedEventArgs e) => HidePopup();

    private void PopupHeaderGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosed || IsClickFromCloseButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(PopupHeaderGrid);
        if (!point.Properties.IsLeftButtonPressed ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        _isHeaderDragging = true;
        _dragStartCursor = cursor;
        _dragStartWindowPos = _appWindow.Position;
        PopupHeaderGrid.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PopupHeaderGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHeaderDragging || _isClosed)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(PopupHeaderGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndHeaderDrag(e.Pointer);
            return;
        }

        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            int targetX = _dragStartWindowPos.X + (cursor.X - _dragStartCursor.X);
            int targetY = _dragStartWindowPos.Y + (cursor.Y - _dragStartCursor.Y);
            // Move only with SWP_NOSIZE during pointer drag so WinUI 3's
            // WM_DPICHANGED handler scales once when the HWND crosses monitors
            // instead of compounding pre-resized bounds by 2.25x.
            WindowShellState.MoveWindowInteractive(_appWindow, _hwnd, targetX, targetY);
            e.Handled = true;
        }
    }

    private void PopupHeaderGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndHeaderDrag(e.Pointer);
    }

    private void EndHeaderDrag(Pointer pointer)
    {
        if (!_isHeaderDragging)
        {
            return;
        }

        _isHeaderDragging = false;
        try
        {
            PopupHeaderGrid.ReleasePointerCapture(pointer);
        }
        catch
        {
        }

        if (!_isClosed && _isVisible && _hwnd != IntPtr.Zero)
        {
            double hwndScale = Win32Helper.GetDpiScaleForWindow(_hwnd, RootGrid.XamlRoot);
            int expectedWidthPx = Math.Max(280, (int)Math.Round(DesiredWidthDip * hwndScale));
            int expectedHeightPx = Math.Max(320, (int)Math.Round(DesiredHeightDip * hwndScale));
            if (Math.Abs(_appWindow.Size.Width - expectedWidthPx) > 1 ||
                Math.Abs(_appWindow.Size.Height - expectedHeightPx) > 1)
            {
                PointInt32 pos = _appWindow.Position;
                WindowShellState.ResizeWindowInteractive(
                    _appWindow,
                    _hwnd,
                    pos.X,
                    pos.Y,
                    expectedWidthPx,
                    expectedHeightPx);
            }
        }
    }

    private bool IsClickFromCloseButton(DependencyObject? source)
    {
        for (DependencyObject? current = source;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ClosePopupButton))
            {
                return true;
            }

            if (ReferenceEquals(current, PopupHeaderGrid))
            {
                return false;
            }
        }

        return false;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated ||
            _isClosed ||
            !_isVisible)
        {
            return;
        }

        if (_content.IsModalDialogOpen ||
            _content.IsPeekPopoverOpen ||
            WidgetEditorPopoverWindow.HasOpenPopoverForOwner(_hwnd) ||
            (RootGrid.XamlRoot is { } xamlRoot &&
             VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0))
        {
            return;
        }

        long session = _sessionId;
        UiDispatch.PostAfterCallback(() =>
        {
            if (_isClosed || !_isVisible || session != _sessionId)
            {
                return;
            }

            if (_content.IsModalDialogOpen ||
                _content.IsPeekPopoverOpen ||
                WidgetEditorPopoverWindow.HasOpenPopoverForOwner(_hwnd) ||
                (RootGrid.XamlRoot is { } currentRoot &&
                 VisualTreeHelper.GetOpenPopupsForXamlRoot(currentRoot).Count > 0))
            {
                return;
            }

            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (fg == _hwnd)
            {
                return;
            }

            if (DeskBox.Helpers.TransientInputFocusGuard.IsWindowsTextInputExperienceWindow(fg) ||
                DeskBox.Helpers.TransientInputFocusGuard.IsCurrentProcessPopupBridge(fg))
            {
                DeskBox.Helpers.TransientInputFocusGuard.WatchPopupWindowInputExperienceFocus(
                    DispatcherQueue,
                    _hwnd,
                    () => !_isClosed && _isVisible && session == _sessionId,
                    () =>
                    {
                        if (!_isClosed && _isVisible && session == _sessionId &&
                            !_content.IsModalDialogOpen &&
                            !_content.IsPeekPopoverOpen &&
                            !WidgetEditorPopoverWindow.HasOpenPopoverForOwner(_hwnd))
                        {
                            HidePopup();
                        }
                    });
                return;
            }

            HidePopup();
        });
    }

    private void OnHighContrastChanged()
    {
        if (_isClosed)
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } dq)
        {
            dq.TryEnqueue(() =>
            {
                if (!_isClosed)
                {
                    WindowsCompatibilityService.ApplySafeBackdrop(this);
                }
            });
            return;
        }

        WindowsCompatibilityService.ApplySafeBackdrop(this);
    }

    private void OnLanguageChanged()
    {
        if (_isClosed)
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } dq)
        {
            dq.TryEnqueue(OnLanguageChanged);
            return;
        }

        ApplyLocalizedChrome();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _isHeaderDragging = false;
        _isVisible = false;
        DisposeColdHiddenCleanupTimer();
        SystemBackdrop = null;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        WindowsCompatibilityService.HighContrastChanged -= OnHighContrastChanged;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
        _content.DismissRequested -= OnContentDismissRequested;
        ContentHost.Content = null;
        RootGrid.Children.Clear();
        Content = null;
        _content.DisposeLeafOnly();
        _viewModel.Dispose();
        IconHelper.ClearCacheScope(QuickPasteIconCacheScope);
    }
}
