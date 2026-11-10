using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly ClipboardWidgetViewModel _viewModel;
    private readonly ClipboardWidgetContent _content;
    private long _sessionId;
    private bool _isVisible;
    private bool _isClosed;

    public ClipboardQuickPasteWindow(
        ClipboardHistoryService clipboardService,
        SettingsService settingsService,
        LocalizationService localizationService,
        ThemeService? themeService = null)
    {
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);

        _clipboardService = clipboardService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _themeService = themeService;

        Title = _localizationService.T("Window.ClipboardQuickPaste.Title");
        InitializeComponent();
        WindowsCompatibilityService.ApplySafeBackdrop(this);
        ExtendsContentIntoTitleBar = true;

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
        _content.DismissRequested += (_, _) => HidePopup();
        ContentHost.Content = _content;

        _themeService?.TrackWindow(this);
        _localizationService.LanguageChanged += OnLanguageChanged;
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
    }

    private async Task ExecuteQuickPasteAndHideAsync(ClipboardItemViewModel item, bool preferPlainText)
    {
        // Hide popup first so SetForegroundWindow returns focus cleanly to the target window,
        // then inject Ctrl+V and release popup visual assets afterward.
        HidePopupCore(detachViewAssets: false);
        try
        {
            await _viewModel.PasteItemAsync(item, preferPlainText);
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
        HidePopupCore(detachViewAssets: false);
        try
        {
            await _viewModel.PasteNextQueueItemAsync(preferPlainText);
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

        int exStyle = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TOPMOST;
        exStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE, exStyle);
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
                            !_content.IsPeekPopoverOpen)
                        {
                            HidePopup();
                        }
                    });
                return;
            }

            HidePopup();
        });
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

        Title = _localizationService.T("Window.ClipboardQuickPaste.Title");
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _isVisible = false;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;
        _content.DisposeLeafOnly();
        _viewModel.Dispose();
        IconHelper.ClearCacheScope(QuickPasteIconCacheScope);
    }
}
