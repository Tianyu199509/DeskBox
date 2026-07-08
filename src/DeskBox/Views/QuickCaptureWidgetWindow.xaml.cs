using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow : Window, IDesktopWidgetWindow
{
    private sealed record QuickCaptureSelectionHitTestItem(
        QuickCaptureItemViewModel Item,
        Windows.Foundation.Rect Bounds);

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private const long MaxDroppedTextFileBytes = 1024 * 1024;
    private const long MaxDroppedImageFileBytes = QuickCaptureClipboardService.MaxClipboardImageBytes;
    private const double QuickCaptureDialogHorizontalMargin = 24.0;
    private const double QuickCaptureDialogMinWidth = 176.0;
    private const double QuickCaptureDialogMaxWidth = 360.0;
    private const double QuickCaptureDialogMinButtonWidth = 76.0;
    private const double QuickCaptureDialogMaxButtonWidth = 112.0;
    private const double DeleteConfirmFlyoutWidth = 232.0;
    private const int CopyToastMs = 900;
    private const int StatusToastDefaultMs = 1400;
    private const int StatusToastUndoMs = 4200;
    private const int ItemsViewTransitionMs = 280;
    private const int ItemsViewTransitionOffsetPx = 6;
    private static readonly string QuickCaptureTextPreviewDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskBox",
        "QuickCapture",
        "Preview");

    private Grid TitleBarGrid => QuickCaptureShell.TitleBar;
    private Border BackgroundPlate => QuickCaptureShell.BackgroundSurface;
    private Border HeaderDivider => QuickCaptureShell.Divider;
    private WidgetTitleIcon TitleIcon => QuickCaptureShell.TitleIconElement;
    private TextBlock TitleText => QuickCaptureShell.TitleTextElement;
    private StackPanel RightActionButtons => QuickCaptureShell.RightActionButtonHost;
    private Button PositionLockButton => QuickCaptureShell.PositionLockActionButton;
    private Button SizeLockButton => QuickCaptureShell.SizeLockActionButton;
    private Button AddButton => QuickCaptureShell.AddActionButton;
    private Button MoreButton => QuickCaptureShell.MoreActionButton;
    private Button CloseButton => QuickCaptureShell.CloseActionButton;
    private FrameworkElement PositionLockButtonIcon => QuickCaptureShell.PositionLockActionIcon;
    private FrameworkElement PositionLockButtonFilledIcon => QuickCaptureShell.PositionLockFilledActionIcon;
    private FrameworkElement SizeLockButtonIcon => QuickCaptureShell.SizeLockActionIcon;
    private FrameworkElement SizeLockButtonFilledIcon => QuickCaptureShell.SizeLockFilledActionIcon;
    private FrameworkElement AddButtonIcon => QuickCaptureShell.AddActionIcon;
    private FrameworkElement MoreButtonIcon => QuickCaptureShell.MoreActionIcon;
    private FrameworkElement CloseButtonIcon => QuickCaptureShell.CloseActionIcon;
    private TextBox EditTextBox => QuickCaptureInlineEditor.EditorTextBox;
    private Button EditCloseButton => QuickCaptureInlineEditor.CloseButton;
    private Button EditCancelButton => QuickCaptureInlineEditor.CancelButton;
    private Button EditSaveButton => QuickCaptureInlineEditor.SaveButton;

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly ThemeService? _themeService;
    private readonly WidgetContentDescriptor _chromeDescriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly WidgetWindowDiagnostics _diagnostics;
    private readonly WidgetTrayAnimationController _trayAnimation;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _hasMovedTitleBarDrag;
    private bool _focusRootAfterDragClick;
    private bool _isResizing;
    private bool _isApplyingBounds;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private Windows.Graphics.PointInt32 _initialWindowPos;
    private Windows.Graphics.SizeInt32 _initialWindowSize;
    private FrameworkElement? _dragCaptureElement;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private bool _isHideAnimationRunning;
    private bool _isClosing;
    private bool _isClearingData;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;
    private bool _isNativeBackdropSuppressedForTrayReveal;
    private QuickCaptureItemViewModel? _editingItem;
    private bool _isExpandingInput;
    private long _backdropRefreshGeneration;
    private long _statusToastGeneration;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoRestoreTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostSafetyTimer;
    private WidgetDisplayChangeWatcher? _displayChangeWatcher;
    private DateTime _lastElevateForInteractionUtc = DateTime.MinValue;
    private QuickCaptureDeletedItemSnapshot? _pendingDeletedItemSnapshot;
    private MenuFlyout? _pendingDeleteConfirmFlyout;
    private string? _copySelectionAnchorId;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<QuickCaptureItemViewModel> _selectionSnapshot = [];
    private HashSet<QuickCaptureItemViewModel> _selectionPreviewItems = [];
    private List<QuickCaptureSelectionHitTestItem> _selectionHitTestItems = [];

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => _hWnd;

    public WidgetWindowIdentity Identity => _diagnostics.Identity;

    public WidgetConfig Config => ViewModel.Config;

    public Windows.Foundation.Rect AnimationBounds => _diagnostics.AnimationBounds;

    private bool _isVisibleOnDesktop;
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    public QuickCaptureWidgetWindow(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ViewModel = viewModel;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _themeService = App.Current.ThemeService;
        _chromeDescriptor = new WidgetContentFactory(_localizationService).GetDescriptor(WidgetKind.QuickCapture);
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        QuickCaptureShell.TitleGlyph = "\uE70F";
        QuickCaptureShell.TitleIconKind = WidgetTitleIconKindNames.QuickCapture;
        QuickCaptureShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _diagnostics = new WidgetWindowDiagnostics("Quick", ViewModel.Config, () => _hWnd);
        _trayAnimation = new WidgetTrayAnimationController(
            _appWindow,
            RootGrid,
            DispatcherQueue,
            _hWnd,
            () => _diagnostics.AnimationBounds,
            LogTrayWindow);

        ConfigureWindow();
        SetupEventHandlers();
        ApplyLocalizedText();
        ApplySurfaceStyle();
        ApplyTitleBarLayout();
    }

    public new void Activate()
    {
        base.Activate();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(_hWnd);
        App.Log($"[ZOrder] QuickCapture PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    public void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(_hWnd);
        App.Log($"[ZOrder] QuickCapture ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    public void ShowPreparedAtDesktopLayer(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedAtDesktopLayer");
        _trayAnimation.PrepareHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        _appWindow.Show();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();
        PushToBottom();
    }

    public void SetTrayAnimationOffsetOverride(double? offsetX, double? offsetY)
    {
        _trayAnimation.SetOffsetOverride(offsetX, offsetY);
    }

    public void ShowPreparedRaisedFromTray(bool persistVisibility = true)
    {
        LogTrayWindow("ShowPreparedRaisedFromTray");
        _trayAnimation.PrepareHiddenState();
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        HoldTemporaryTopMost();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        QueueBackdropRefresh();

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);
            if (Visible)
            {
                HoldTemporaryTopMost();
            }
        });
    }

    public void EnsureRaisedFromTrayTopMost()
    {
        if (!Visible)
        {
            LogTrayWindow("EnsureRaisedTopMost skipped reason=not-visible");
            return;
        }

        if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            LogTrayWindow("EnsureRaisedTopMost skipped reason=not-raised");
            return;
        }

        LogTrayWindow("EnsureRaisedTopMost");
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(_hWnd);
        HoldTemporaryTopMost();
        QueueBackdropRefresh();
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
    }

    public void PrepareTrayShowAnimation()
    {
        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        _isHideAnimationRunning = false;
        var profile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={_trayAnimation.Generation} effect={_settingsService.Settings.WidgetAnimationEffect} " +
            $"speed={_settingsService.Settings.WidgetAnimationSpeed} enabled={profile.IsEnabled} durationMs={profile.DurationMs}");
        _trayAnimation.PrepareVisualState(profile.ShowOffsetX, profile.ShowOffsetY, profile.ShowStartOpacity, profile.ShowStartScale);
    }

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        _trayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={_trayAnimation.Generation}");
        _trayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        RestoreNativeBackdropAfterTrayReveal();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();
    }

    public bool PrepareTrayHideAnimation(bool persistVisibility = true)
    {
        if (!Visible || _isHideAnimationRunning)
        {
            LogTrayWindow($"PrepareHide skipped visible={Visible} hideRunning={_isHideAnimationRunning}");
            return false;
        }

        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreNativeBackdropAfterTrayReveal();
        _isHideAnimationRunning = true;
        Visible = false;
        ViewModel.Config.IsVisible = false;
        if (persistVisibility)
        {
            _settingsService.SaveDebounced();
        }

        LogTrayWindow($"PrepareHide gen={_trayAnimation.Generation}");
        _trayAnimation.PrepareVisualState(0, 0, WidgetTrayAnimationController.RestingOpacity, WidgetTrayAnimationController.RestingScale);
        return true;
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void RevealFromTray(bool autoRestore = true)
    {
        PrepareTrayShowAnimation();
        ElevateForInteraction();
        _trayAnimation.PrepareHiddenState();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNOACTIVATE);
        base.Activate();
        Visible = true;
        ViewModel.Config.IsVisible = true;
        _settingsService.SaveDebounced();
        QueueBackdropRefresh();
        PlayTrayRaiseAnimationAfterFirstFrame();

        if (!autoRestore)
        {
            return;
        }

        _autoRestoreTimer?.Stop();
        _autoRestoreTimer = DispatcherQueue.CreateTimer();
        _autoRestoreTimer.IsRepeating = false;
        _autoRestoreTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _autoRestoreTimer.Tick += (_, _) =>
        {
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            if (!_isDragging && !_isResizing)
            {
                RestoreDesktopLayer(force: true);
            }
        };
        _autoRestoreTimer.Start();
    }

    public void HideWindow()
    {
        if (!PrepareTrayHideAnimation())
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void CloseWindow()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CloseWindow);
            return;
        }

        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Visible = false;
        _autoRestoreTimer?.Stop();
        _autoRestoreTimer = null;
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _trayAnimation.Stop();
        WidgetLayerService.ReleaseWindow(_hWnd);
        Close();
    }

    public void ApplyAppearancePreview()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyAppearancePreview);
            return;
        }

        if (_isClosing)
        {
            return;
        }

        ViewModel.ApplyAppearancePreview();
        QuickCaptureShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ApplyWindowCornerPreference();
        ApplyBackdropPreference();
        QueueBackdropRefresh();
        ApplyTitleBarLayout();
    }

    public void RestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayer(force: true);
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        _restoreDesktopLayerWhenIdle = true;
        _keepRaisedUntilDeactivate = false;
        RestoreDesktopLayer(force: true);
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
        Win32Helper.AllowShellDragDropMessages(_hWnd);

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
        var config = ViewModel.Config;
        var workArea = DisplayArea.GetFromRect(
            new Windows.Graphics.RectInt32(
                (int)Math.Round(config.X),
                (int)Math.Round(config.Y),
                (int)Math.Round(config.Width),
                (int)Math.Round(config.Height)),
            DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WidgetPositioningService.ResolveBounds(
            config,
            workArea,
            WidgetPositioningService.GetAvailableWorkAreas());
        ApplyWindowBounds(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            persist: false);

        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(_hWnd);
        ApplyBackdropPreference();

        RootGrid.Loaded += (_, _) =>
        {
            ApplySurfaceStyle();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            QueueBackdropRefresh();
            RootGrid.Focus(FocusState.Programmatic);
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTabStyles();
                ResetItemsViewTransitionState();
                ViewModel.RefreshAfterViewReady();
            });
        };
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ApplyBackdropPreference();
            ApplySurfaceStyle();
        };
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        if (_themeService is not null)
        {
            _themeService.AppearanceChanged += OnThemeAppearanceChanged;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += QuickCaptureWidgetWindow_Activated;

        _appWindow.Changed += AppWindow_Changed;
        _displayChangeWatcher = new WidgetDisplayChangeWatcher(_hWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);

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
            _isClosing = true;
            Visible = false;
            WidgetLayerService.ReleaseWindow(_hWnd);
            _settingsService.SettingsChanged -= OnSettingsChanged;
            if (_themeService is not null)
            {
                _themeService.AppearanceChanged -= OnThemeAppearanceChanged;
            }

            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _displayChangeWatcher?.Dispose();
            _displayChangeWatcher = null;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            _topMostSafetyTimer?.Stop();
            _topMostSafetyTimer = null;

            _trayAnimation.Stop();
            _trayAnimation.RestoreVisualState();
            _trayAnimation.RestoreWindowPosition();
            DisposeAcrylicController();

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

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    private bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (_isClosing ||
            _isHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (
            _isDragging ||
            _isResizing ||
            _trayAnimation.IsApplyingBounds)
        {
            return false;
        }

        var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(ViewModel.Config);
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        if (position.X == bounds.X &&
            position.Y == bounds.Y &&
            size.Width == bounds.Width &&
            size.Height == bounds.Height)
        {
            return true;
        }

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: false);
        return true;
    }

    private bool RestoreBoundsAfterDisplayChange()
    {
        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored && Visible)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    private void ApplyLocalizedText()
    {
        string addInputText = _localizationService.T("QuickCapture.AddInput");
        string searchText = _localizationService.T("QuickCapture.SearchPlaceholder");
        string closeSearchText = _localizationService.T("QuickCapture.CloseSearch");
        string moreText = _localizationService.T("Widget.Tooltip.More");
        string closeText = _localizationService.T("Common.Off");

        InputTextBox.PlaceholderText = _localizationService.T("QuickCapture.InputPlaceholder");
        SearchTextBox.PlaceholderText = searchText;
        ToolTipService.SetToolTip(SearchButton, searchText);
        ToolTipService.SetToolTip(CloseSearchButton, closeSearchText);
        ToolTipService.SetToolTip(PositionLockButton, _localizationService.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(SizeLockButton, _localizationService.T("Widget.LockSize"));
        ToolTipService.SetToolTip(AddButton, _localizationService.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(MoreButton, moreText);
        ToolTipService.SetToolTip(CloseButton, closeText);
        ToolTipService.SetToolTip(EditCloseButton, _localizationService.T("Common.Cancel"));
        AutomationProperties.SetName(InputTextBox, _localizationService.T("QuickCapture.InputPlaceholder"));
        AutomationProperties.SetName(SearchButton, searchText);
        AutomationProperties.SetName(SearchTextBox, searchText);
        AutomationProperties.SetName(CloseSearchButton, closeSearchText);
        AutomationProperties.SetName(PositionLockButton, _localizationService.T("Widget.LockPosition"));
        AutomationProperties.SetName(SizeLockButton, _localizationService.T("Widget.LockSize"));
        AutomationProperties.SetName(AddButton, _localizationService.T("Widget.Tooltip.Add"));
        AutomationProperties.SetName(MoreButton, moreText);
        AutomationProperties.SetName(CloseButton, closeText);
        AutomationProperties.SetName(EditCloseButton, _localizationService.T("Common.Cancel"));
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        QuickCaptureInlineEditor.CancelText = _localizationService.T("Common.Cancel");
        QuickCaptureInlineEditor.SaveText = _localizationService.T("Common.Save");
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isApplyingBounds || _trayAnimation.IsApplyingBounds || (!_isDragging && !_isResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.DisplayName))
        {
            ApplyTitleBarLayout();
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.WidgetOpacity))
        {
            ApplyBackdropPreference();
            return;
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.SelectedView))
        {
            ClearQuickCaptureCopySelection();
            RefreshSelectedViewSegment();
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.ItemsViewTransitionToken))
        {
            PlayItemsViewTransition();
        }

        if (e.PropertyName == nameof(QuickCaptureWidgetViewModel.TabStyle))
        {
            ApplySegmentedStyle();
        }

        if (e.PropertyName is nameof(QuickCaptureWidgetViewModel.IconSize) or
            nameof(QuickCaptureWidgetViewModel.TextSize))
        {
            ApplyTitleBarLayout();
        }
    }

    private void OnSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnSettingsChanged);
            return;
        }

        ApplyWindowCornerPreference();
        QuickCaptureShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ApplyBackdropPreference();
        QueueBackdropRefresh();
        ApplyTitleBarLayout();
    }

    private void OnThemeAppearanceChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnThemeAppearanceChanged);
            return;
        }

        ApplyBackdropPreference();
        QueueBackdropRefresh();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddInputAsync();
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetPositionLocked(!ViewModel.Config.IsPositionLocked);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SetSizeLocked(!ViewModel.Config.IsSizeLocked);
    }

    private void ExpandInputButton_Click(object sender, RoutedEventArgs e)
    {
        _isExpandingInput = true;
        _editingItem = null;
        QuickCaptureInlineEditor.Text = InputTextBox.Text;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.InputPlaceholder");
        QuickCaptureInlineEditor.Visibility = Visibility.Visible;
        QuickCaptureInlineEditor.FocusEditor(moveCaretToEnd: true);
    }

    private async void InputTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter ||
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) &
             Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.AddInputAsync();
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ViewModel.CollapseSearch();
            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down) ||
            ItemsListView.Items.Count == 0)
        {
            return;
        }

        ItemsListView.SelectedIndex = 0;
        ItemsListView.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExpandSearch();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CollapseSearch();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void QuickCaptureViewSegmented_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySegmentedStyle();
    }

    private void QuickCaptureViewSegmented_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySegmentedLayout();
    }

    private void ApplySegmentedLayout()
    {
        if (ViewModel.TabStyle == SettingsService.WidgetTabStyleButton)
        {
            WidgetSegmentedLayoutHelper.ApplyEqualItemWidths(QuickCaptureViewSegmented);
        }
        else
        {
            WidgetSegmentedLayoutHelper.ApplyNaturalItemWidths(QuickCaptureViewSegmented);
        }
    }

    private void ApplySegmentedStyle()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        WidgetSegmentedStyleHelper.Apply(QuickCaptureViewSegmented, ViewModel.TabStyle);
        ApplySegmentedLayout();
    }

    private void QuickCaptureViewSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectView(GetSelectedSegmentView());
    }

    private void SelectView(QuickCaptureViewMode view)
    {
        if (ViewModel.SelectedView == view)
        {
            RefreshSelectedViewSegment();
            return;
        }

        ViewModel.SelectedView = view;
    }

    private async void EnableRecentCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (await QuickCaptureClipboardActivationHelper.EnableAsync(RootGrid.XamlRoot, _localizationService))
        {
            SelectView(QuickCaptureViewMode.Recent);
        }
    }

    private async void ItemsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        await OpenItemInDefaultAppAsync(item);
    }

    private async Task OpenItemInDefaultAppAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            await OpenImageInDefaultViewerAsync(item);
            return;
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }

            return;
        }

        if (item.IsRecent)
        {
            await OpenTextInDefaultEditorAsync(item);
            return;
        }

        await EditItemAsync(item);
    }

    private async Task OpenImageInDefaultViewerAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type != QuickCaptureItemType.Image ||
            string.IsNullOrWhiteSpace(item.ImagePath) ||
            !File.Exists(item.ImagePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open image preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
        }
    }

    private async Task OpenTextInNotepadAsync(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return;
        }

        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{previewPath}\"",
                UseShellExecute = true
            });

            if (process is null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
                return;
            }

            await process.WaitForExitAsync();
            await TryApplyTextPreviewEditAsync(item, previewPath);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text in notepad: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task OpenTextInDefaultEditorAsync(QuickCaptureItemViewModel item)
    {
        string? previewPath = await TryCreateTextPreviewFileAsync(item);
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(previewPath);
            if (!await Launcher.LaunchFileAsync(file))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to open text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task TryApplyTextPreviewEditAsync(QuickCaptureItemViewModel item, string previewPath)
    {
        if (item.IsRecent)
        {
            return;
        }

        try
        {
            string editedBody = await File.ReadAllTextAsync(previewPath);
            if (string.Equals(editedBody, item.Body, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(editedBody))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
                return;
            }

            await ViewModel.EditItemAsync(item, editedBody);
            ShowStatusToast(_localizationService.T("QuickCapture.Edited"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to apply notepad edit: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
        }
    }

    private async Task<string?> TryCreateTextPreviewFileAsync(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }

        try
        {
            Directory.CreateDirectory(QuickCaptureTextPreviewDirectory);
            string fileName = BuildQuickCapturePreviewFileName(item);
            string previewPath = Path.Combine(QuickCaptureTextPreviewDirectory, fileName);
            await File.WriteAllTextAsync(previewPath, item.Body);
            return previewPath;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to create text preview: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.OpenItemFailed"));
            return null;
        }
    }

    private static string BuildQuickCapturePreviewFileName(QuickCaptureItemViewModel item)
    {
        string stem = item.Body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Quick Capture";
        stem = SanitizeQuickCapturePreviewFileName(stem);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Quick Capture";
        }

        if (stem.Length > 42)
        {
            stem = stem[..42].Trim();
        }

        return $"{stem}-{item.Id[..Math.Min(8, item.Id.Length)]}.txt";
    }

    private static string SanitizeQuickCapturePreviewFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, ' ');
        }

        return string.Join(" ", fileName.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private async void ItemsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool isCtrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (isCtrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            SelectAllVisibleQuickCaptureItems();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectedQuickCaptureItemsAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && HasQuickCaptureCopySelection())
        {
            ClearQuickCaptureCopySelection();
            e.Handled = true;
            return;
        }

        if (ItemsListView.SelectedItem is not QuickCaptureItemViewModel item)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            ShowQuickCaptureDeleteConfirmFlyout(item, ItemsListView);
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private void ItemsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.RemovedItems)
        {
            SetItemActionButtonsVisibleForItem(item, false);
        }

        foreach (var item in e.AddedItems)
        {
            SetItemActionButtonsVisibleForItem(item, true);
        }
    }

    private void QuickCaptureItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        ItemsListView.Focus(FocusState.Programmatic);
        if (!item.IsCopySelected)
        {
            ClearQuickCaptureCopySelection();
            item.IsCopySelected = true;
            _copySelectionAnchorId = item.Id;
        }

        var anchor = (FrameworkElement)sender;
        var flyout = CreateItemFlyout(item, anchor);
        flyout.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private async void QuickCaptureItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuickCaptureItemViewModel item ||
            e.OriginalSource is not DependencyObject source ||
            IsItemActionSource(source))
        {
            return;
        }

        ItemsListView.Focus(FocusState.Programmatic);
        bool isCtrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        if (isCtrlPressed || isShiftPressed)
        {
            if (isShiftPressed)
            {
                SelectQuickCaptureRange(item);
            }
            else
            {
                ToggleQuickCaptureSelection(item);
            }

            e.Handled = true;
            return;
        }

        if (HasQuickCaptureCopySelection())
        {
            ClearQuickCaptureCopySelection();
        }

        e.Handled = true;
        await CopyItemWithFeedbackAsync(item);
    }

    private async Task CopyItemWithFeedbackAsync(QuickCaptureItemViewModel item)
    {
        try
        {
            await ViewModel.CopyItemAsync(item);
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy item {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async Task CopyImageTextAsync(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine is null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
                return;
            }

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            if (string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(ocrResult.Text);
            Clipboard.SetContent(dataPackage);
            ShowStatusToast(_localizationService.T("QuickCapture.CopyText") + " ✓");
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to OCR image {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async void QuickCaptureItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement element || element.DataContext is not QuickCaptureItemViewModel item)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            if (!await TryPrepareQuickCaptureDragPackageAsync(args.Data, item))
            {
                args.Cancel = true;
                return;
            }

            args.AllowedOperations = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to start drag: {ex}");
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void QuickCaptureItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        SetItemActionButtonsVisible(sender, false);
        ApplyItemSearchHighlight(sender, item);

        if (FindVisualChild<Button>(sender, "SaveRecentItemButton") is { } saveButton)
        {
            string saveToRecordsText = _localizationService.T("QuickCapture.SaveToRecords");
            saveButton.Visibility = item.IsRecent ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(saveButton, saveToRecordsText);
            AutomationProperties.SetName(saveButton, saveToRecordsText);
        }

        if (FindPinnedButton(sender) is { } pinButton)
        {
            string pinText = item.IsRecent
                ? _localizationService.T("QuickCapture.PinToRecords")
                : item.PinTooltip;
            ToolTipService.SetToolTip(pinButton, pinText);
            AutomationProperties.SetName(pinButton, pinText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemUpButton") is { } moveUpButton)
        {
            string moveUpText = _localizationService.T("QuickCapture.MoveUp");
            ToolTipService.SetToolTip(moveUpButton, moveUpText);
            AutomationProperties.SetName(moveUpButton, moveUpText);
        }

        if (FindVisualChild<Button>(sender, "MovePinnedItemDownButton") is { } moveDownButton)
        {
            string moveDownText = _localizationService.T("QuickCapture.MoveDown");
            ToolTipService.SetToolTip(moveDownButton, moveDownText);
            AutomationProperties.SetName(moveDownButton, moveDownText);
        }

        if (FindVisualChild<Button>(sender, "DeleteItemButton") is { } deleteButton)
        {
            string deleteText = _localizationService.T("Common.Delete");
            ToolTipService.SetToolTip(deleteButton, deleteText);
            AutomationProperties.SetName(deleteButton, deleteText);
        }

        if (FindVisualChild<Image>(sender, "ImagePreview") is { } imagePreview && imagePreview.IsLoaded)
        {
            _ = LoadImagePreviewAsync(imagePreview, item);
        }
    }

    private async void ImagePreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { DataContext: QuickCaptureItemViewModel item } image)
        {
            await LoadImagePreviewAsync(image, item);
        }
    }

    private async Task LoadImagePreviewAsync(Image image, QuickCaptureItemViewModel item)
    {
        string itemId = item.Id;
        if (item.Type != QuickCaptureItemType.Image ||
            string.IsNullOrWhiteSpace(item.ImagePath))
        {
            image.Tag = null;
            image.Source = null;
            return;
        }

        string targetKey = $"{item.Id}|{item.ImagePath}";
        if (string.Equals(image.Tag as string, targetKey, StringComparison.Ordinal))
        {
            return;
        }

        image.Tag = targetKey;
        image.Source = null;

        await Task.Yield();
        if (!string.Equals((image.DataContext as QuickCaptureItemViewModel)?.Id, itemId, StringComparison.Ordinal))
        {
            if (string.Equals(image.Tag as string, targetKey, StringComparison.Ordinal))
            {
                image.Tag = null;
            }

            return;
        }

        string? previewPath = await ViewModel.GetOrCreateImageThumbnailPathAsync(item);
        if (!string.Equals((image.DataContext as QuickCaptureItemViewModel)?.Id, itemId, StringComparison.Ordinal) ||
            !string.Equals(image.Tag as string, targetKey, StringComparison.Ordinal))
        {
            if (string.Equals(image.Tag as string, targetKey, StringComparison.Ordinal))
            {
                image.Tag = null;
            }

            return;
        }

        Uri? previewUri = TryCreateImageUri(previewPath);
        image.Source = previewUri is { } uri
            ? new BitmapImage
            {
                DecodePixelWidth = 140,
                UriSource = uri
            }
            : null;
    }

    private static Uri? TryCreateImageUri(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static void ApplyItemSearchHighlight(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<TextBlock>(itemRoot, "ItemTextBlock") is not { } textBlock)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (item.HighlightStartIndex < 0 || item.HighlightLength <= 0)
        {
            return;
        }

        var highlighter = new TextHighlighter
        {
            Background = GetBrushResourceOrFallback(
                "SystemAccentColorLight2Brush",
                WithAlpha(
                    App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor,
                    0x44)),
            Foreground = GetBrushResourceOrFallback(
                "TextFillColorPrimaryBrush",
                textBlock.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black)
        };
        highlighter.Ranges.Add(new TextRange
        {
            StartIndex = item.HighlightStartIndex,
            Length = item.HighlightLength
        });
        textBlock.TextHighlighters.Add(highlighter);
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void QuickCaptureItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, true);
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetItemActionButtonsVisible(sender as DependencyObject, false);
        SetItemHoverState(sender as DependencyObject, false);
    }

    private static void SetItemActionButtonsVisible(DependencyObject? itemRoot, bool isVisible)
    {
        if (itemRoot is null ||
            FindVisualChild<Border>(itemRoot, "ItemActionHost") is not { } actions)
        {
            return;
        }

        actions.Opacity = isVisible ? 1 : 0;
        actions.IsHitTestVisible = isVisible;
        ApplyActionButtonHostTheme(actions, itemRoot);
        ElementCompositionPreview.GetElementVisual(actions).StopAnimation("Offset");
    }

    private static void ApplyActionButtonHostTheme(Border actions, DependencyObject itemRoot)
    {
        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        actions.Background = new SolidColorBrush(WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x1E, 0x23, 0x29) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.18 : 0.08,
                overlayMix: isDark ? 0.03 : 0.02),
            0xFF));
        actions.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x4A : (byte)0x30));
        actions.BorderThickness = new Thickness(1);

        foreach (var button in FindVisualChildren<Button>(actions))
        {
            ApplyActionButtonTheme(button, isDark, accentColor);
        }
    }

    private static void ApplyActionButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var foreground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF2 : (byte)0xE2));

        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private static void SetItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "ItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "ImagePreviewBorder") is { } imageBorder)
        {
            imageBorder.BorderBrush = isHovered
                ? new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xE0 : (byte)0xCC))
                : GetBrushResourceOrFallback(
                    "CardStrokeColorDefaultBrush",
                    isDark
                        ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                        : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }
    }

    private void SetItemActionButtonsVisibleForItem(object? item, bool isVisible)
    {
        if (item is null)
        {
            return;
        }

        if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
        {
            SetItemActionButtonsVisible(container, isVisible);
            return;
        }

        if (isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ItemsListView.ContainerFromItem(item) is DependencyObject queuedContainer)
                {
                    SetItemActionButtonsVisible(queuedContainer, true);
                }
            });
        }
    }

    private async void PinItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            if (item.IsRecent)
            {
                await ViewModel.PinRecentItemAsync(item);
            }
            else
            {
                await ViewModel.TogglePinnedAsync(item);
            }
        }
    }

    private async void SaveRecentItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.SaveRecentItemAsync(item);
        }
    }

    private async void MovePinnedItemUpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, -1);
        }
    }

    private async void MovePinnedItemDownButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await ViewModel.MovePinnedItemAsync(item, 1);
        }
    }

    private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.Tag is QuickCaptureItemViewModel item)
        {
            ShowQuickCaptureDeleteConfirmFlyout(item, element);
        }
    }

    private async Task DeleteItemWithUndoAsync(QuickCaptureItemViewModel item)
    {
        var snapshot = await ViewModel.DeleteItemAsync(item);
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = snapshot;
        ShowStatusToast(
            _localizationService.T("QuickCapture.Deleted"),
            _localizationService.T("Common.Undo"),
            StatusToastUndoMs);
    }

    private void ShowQuickCaptureDeleteConfirmFlyout(QuickCaptureItemViewModel item, FrameworkElement anchor)
    {
        ShowConfirmMenu(
            anchor,
            _localizationService.T("QuickCapture.DeleteConfirm.Title"),
            _localizationService.T("Common.Delete"),
            async () => await DeleteItemWithUndoAsync(item));
    }

    private string GetDeleteConfirmPreviewText(QuickCaptureItemViewModel item)
    {
        string text = item.Type == QuickCaptureItemType.Image
            ? _localizationService.T("QuickCapture.ImageItem")
            : item.DisplayText;
        text = string.Join(" ", text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = _localizationService.T("QuickCapture.Name");
        }

        return text.Length <= 34 ? text : $"{text[..34].Trim()}...";
    }

    private async Task<bool> TryPrepareQuickCaptureDragPackageAsync(DataPackage dataPackage, QuickCaptureItemViewModel item)
    {
        dataPackage.RequestedOperation = DataPackageOperation.Copy;
        var selectedItems = GetSelectedQuickCaptureItemsInVisibleOrder();
        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            string text = FormatQuickCaptureBatch(selectedItems);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            DeskBoxDragData.SetText(dataPackage, text, DeskBoxDragData.SourceQuickCapture);
            dataPackage.Properties.Title = _localizationService.Format("QuickCapture.CopiedCount", selectedItems.Count);
            return true;
        }

        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            try
            {
                string? exportPath = await ViewModel.CreateImageExportFileAsync(
                    item,
                    _localizationService.T("QuickCapture.ImageExportFileNamePrefix"));
                if (string.IsNullOrWhiteSpace(exportPath))
                {
                    return false;
                }

                var file = await StorageFile.GetFileFromPathAsync(exportPath);
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                dataPackage.SetStorageItems([file]);
                dataPackage.Properties.Title = Path.GetFileName(exportPath);
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureWidget] Failed to prepare image drag package: {ex}");
                return false;
            }
        }

        if (item.Type == QuickCaptureItemType.Link &&
            Uri.TryCreate(item.Url ?? item.Body, UriKind.Absolute, out var uri))
        {
            DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            dataPackage.SetWebLink(uri);
            dataPackage.SetUri(uri);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            dataPackage.Properties.Title = item.Body;
            return true;
        }

        return false;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? MoreButton;
        ShowFlyoutWithElevation(CreateMoreFlyout(), target);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, e.GetPosition(TitleBarGrid));
        e.Handled = true;
    }

    private void QuickCaptureShell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (QuickCaptureShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden) ||
            !ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateMoreFlyout(), QuickCaptureShell, e.GetPosition(QuickCaptureShell));
        e.Handled = true;
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockPosition"),
            "\uE72E",
            ViewModel.Config.IsPositionLocked,
            SetPositionLocked));
        flyout.Items.Add(CreateToggleMenuItem(
            _localizationService.T("Widget.LockSize"),
            "\uE9CE",
            ViewModel.Config.IsSizeLocked,
            SetSizeLocked));
        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            ViewModel.Config,
            _chromeDescriptor,
            _localizationService,
            SetChromeModeOverride));
        flyout.Items.Add(new MenuFlyoutSeparator());

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += (_, _) => DispatcherQueue.TryEnqueue(StartTitleRename);
        flyout.Items.Add(renameItem);

        var settingsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("QuickCapture.OpenSettings"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += (_, _) => App.Current.ShowSettings("QuickCaptureSettings");
        flyout.Items.Add(settingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var clearItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("QuickCapture.ClearData"),
            Icon = new FontIcon
            {
                Glyph = "\uE894",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        clearItem.Click += (_, _) =>
        {
            ShowConfirmMenu(
                MoreButton,
                _localizationService.T("QuickCapture.ClearDataTitle"),
                _localizationService.T("QuickCapture.ClearData"),
                async () => await ClearDataAsync());
        };
        flyout.Items.Add(clearItem);

        return flyout;
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(ViewModel.Config, mode);
        _settingsService.UpdateWidget(ViewModel.Config);
        ApplyTitleBarLayout();
    }

    private void SetPositionLocked(bool value)
    {
        ViewModel.SetPositionLocked(value);
        ApplyLockActionIconState();
    }

    private void SetSizeLocked(bool value)
    {
        ViewModel.SetSizeLocked(value);
        ApplyLockActionIconState();
    }

    private MenuFlyout CreateItemFlyout(QuickCaptureItemViewModel item, FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var selectedItems = GetSelectedQuickCaptureItemsInVisibleOrder();

        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            var copySelectedItem = new MenuFlyoutItem
            {
                Text = _localizationService.Format("QuickCapture.CopySelected", selectedItems.Count),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copySelectedItem.Click += async (_, _) => await CopySelectedQuickCaptureItemsAsync(selectedItems);
            flyout.Items.Add(copySelectedItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) =>
        {
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);

        if (item.Type == QuickCaptureItemType.Image &&
            !string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath))
        {
            var copyTextItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.CopyText"),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyTextItem.Click += async (_, _) => await CopyImageTextAsync(item);
            flyout.Items.Add(copyTextItem);
        }

        if (CreateSaveToLastFileWidgetItem(item) is { } saveToLastItem)
        {
            flyout.Items.Add(saveToLastItem);
        }

        flyout.Items.Add(CreateSaveToFileWidgetSubItem(item));

        if (item.IsRecent)
        {
            var saveItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.SaveToRecords"),
                Icon = new FontIcon { Glyph = "\uE74E" }
            };
            saveItem.Click += async (_, _) => await ViewModel.SaveRecentItemAsync(item);
            flyout.Items.Add(saveItem);

            var pinRecentItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.PinToRecords"),
                Icon = new FontIcon { Glyph = "\uE718" }
            };
            pinRecentItem.Click += async (_, _) => await ViewModel.PinRecentItemAsync(item);
            flyout.Items.Add(pinRecentItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deleteRecentItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Common.Delete"),
                Icon = new FontIcon { Glyph = "\uE74D" }
            };
            deleteRecentItem.Click += (_, _) => ShowQuickCaptureDeleteConfirmFlyout(item, anchor);
            flyout.Items.Add(deleteRecentItem);
            return flyout;
        }

        if (item.Type != QuickCaptureItemType.Image)
        {
            var editItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.Edit"),
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            editItem.Click += async (_, _) => await EditItemAsync(item);
            flyout.Items.Add(editItem);

            var notepadItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.EditInNotepad"),
                Icon = new FontIcon { Glyph = "\uE70F" }
            };
            notepadItem.Click += async (_, _) => await OpenTextInNotepadAsync(item);
            flyout.Items.Add(notepadItem);
        }

        var pinItem = new MenuFlyoutItem
        {
            Text = item.IsPinned ? _localizationService.T("QuickCapture.Unpin") : _localizationService.T("QuickCapture.Pin"),
            Icon = new FontIcon { Glyph = item.IsPinned ? "\uE840" : "\uE718" }
        };
        pinItem.Click += async (_, _) => await ViewModel.TogglePinnedAsync(item);
        flyout.Items.Add(pinItem);

        if (ViewModel.IsPinnedView && !ViewModel.HasSearchText)
        {
            var moveUpItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.MoveUp"),
                Icon = new FontIcon { Glyph = "\uE70E" },
                IsEnabled = item.CanMovePinnedUp
            };
            moveUpItem.Click += async (_, _) => await ViewModel.MovePinnedItemAsync(item, -1);
            flyout.Items.Add(moveUpItem);

            var moveDownItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.MoveDown"),
                Icon = new FontIcon { Glyph = "\uE70D" },
                IsEnabled = item.CanMovePinnedDown
            };
            moveDownItem.Click += async (_, _) => await ViewModel.MovePinnedItemAsync(item, 1);
            flyout.Items.Add(moveDownItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => ShowQuickCaptureDeleteConfirmFlyout(item, anchor);
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyoutSubItem CreateSaveToFileWidgetSubItem(QuickCaptureItemViewModel item)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.SaveToFileWidget"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };

        var targets = App.Current.WidgetManager?.GetQuickCaptureFileWidgetTargets() ?? [];
        if (targets.Count == 0)
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = _localizationService.T("QuickCapture.NoFileWidgetTargets"),
                IsEnabled = false
            });
            return subItem;
        }

        foreach (var target in targets)
        {
            var targetItem = new MenuFlyoutItem
            {
                Text = target.Name,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            targetItem.Click += async (_, _) =>
            {
                await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
            };
            subItem.Items.Add(targetItem);
        }

        return subItem;
    }

    private MenuFlyoutItem? CreateSaveToLastFileWidgetItem(QuickCaptureItemViewModel item)
    {
        var target = App.Current.WidgetManager?.GetLastQuickCaptureFileWidgetTarget();
        if (target is null)
        {
            return null;
        }

        var menuItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("QuickCapture.SaveToLastFileWidget", target.Name),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        menuItem.Click += async (_, _) => await SaveQuickCaptureItemToFileWidgetAsync(item, target.WidgetId);
        return menuItem;
    }

    private async Task SaveQuickCaptureItemToFileWidgetAsync(QuickCaptureItemViewModel item, string targetWidgetId)
    {
        if (App.Current.WidgetManager is null)
        {
            return;
        }

        string? savedPath = await App.Current.WidgetManager.SaveQuickCaptureItemToFileWidgetAsync(
            item.ToModel(),
            targetWidgetId,
            _localizationService.T("QuickCapture.ImageExportFileNamePrefix"));
        ShowStatusToast(string.IsNullOrWhiteSpace(savedPath)
            ? _localizationService.T("QuickCapture.SaveToFileWidgetFailed")
            : _localizationService.T("QuickCapture.SavedToFileWidget"));
    }

    private Task EditItemAsync(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            return Task.CompletedTask;
        }

        _editingItem = item;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        QuickCaptureInlineEditor.Text = item.Body;
        QuickCaptureInlineEditor.Visibility = Visibility.Visible;
        QuickCaptureInlineEditor.FocusEditor(moveCaretToEnd: true);
        return Task.CompletedTask;
    }

    private async void EditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveInlineEditAsync();
    }

    private void EditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseInlineEdit();
    }

    private async void EditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseInlineEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            await SaveInlineEditAsync();
            e.Handled = true;
        }
    }

    private async Task SaveInlineEditAsync()
    {
        string body = QuickCaptureInlineEditor.Text;
        if (string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        if (_isExpandingInput)
        {
            InputTextBox.Text = body;
            await ViewModel.AddInputAsync();
            CloseInlineEdit();
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseInlineEdit();
            return;
        }

        await ViewModel.EditItemAsync(item, body);
        CloseInlineEdit();
    }

    private void CloseInlineEdit()
    {
        _editingItem = null;
        _isExpandingInput = false;
        QuickCaptureInlineEditor.Visibility = Visibility.Collapsed;
        QuickCaptureInlineEditor.Text = string.Empty;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        InputTextBox.Focus(FocusState.Programmatic);
    }

    private async Task ConfirmClearDataAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearDataTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearDataDescriptionWithCount",
                    ViewModel.RecordCount,
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearData"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ClearDataAsync()
    {
        if (_isClearingData)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ConfirmClearRecentAsync()
    {
        if (_isClearingData || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = _localizationService.T("QuickCapture.ClearRecentTitle"),
            Content = new TextBlock
            {
                Text = _localizationService.Format(
                    "QuickCapture.ClearRecentDescriptionWithCount",
                    ViewModel.RecentCount),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localizationService.T("QuickCapture.ClearRecent"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        ApplyQuickCaptureDialogSizing(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearRecentAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private async Task ClearRecentAsync()
    {
        if (_isClearingData)
        {
            return;
        }

        _isClearingData = true;
        try
        {
            await ViewModel.ClearRecentAsync();
        }
        finally
        {
            _isClearingData = false;
        }
    }

    private double GetQuickCaptureDialogWidth()
    {
        double rootWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = ViewModel.Config.Width;
        }

        if (!double.IsFinite(rootWidth) || rootWidth <= 0)
        {
            rootWidth = SettingsService.DefaultWidgetWidth;
        }

        return Math.Clamp(
            rootWidth - QuickCaptureDialogHorizontalMargin,
            QuickCaptureDialogMinWidth,
            QuickCaptureDialogMaxWidth);
    }

    private void ApplyQuickCaptureDialogSizing(ContentDialog dialog, double? dialogWidth = null)
    {
        double compactWidth = dialogWidth ?? GetQuickCaptureDialogWidth();
        double buttonMinWidth = Math.Clamp(
            (compactWidth - 24) / 2,
            QuickCaptureDialogMinButtonWidth,
            QuickCaptureDialogMaxButtonWidth);

        dialog.Resources["ContentDialogMinWidth"] = compactWidth;
        dialog.Resources["ContentDialogMaxWidth"] = compactWidth;
        dialog.Resources["ContentDialogButtonMinWidth"] = buttonMinWidth;
    }

    private MenuFlyoutItem CreateToggleMenuItem(string text, string glyph, bool isChecked, Action<bool> applyValue)
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

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.QuickCapture, enabled: false, reveal: false);
        }
    }

    private void QuickCaptureShell_TitleDoubleTapped(object? sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(StartTitleRename);
    }

    private void StartTitleRename()
    {
        if (_isDragging ||
            _isResizing ||
            QuickCaptureShell.TitleEditorContent is not null)
        {
            return;
        }

        _isCancellingTitleRename = false;
        App.Current.WidgetManager?.BeginWidgetInteraction("quick-title-rename-opened");
        var editor = CreateTitleRenameEditor();
        QuickCaptureShell.TitleEditorContent = editor;
        DispatcherQueue.TryEnqueue(() =>
        {
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        });
    }

    private TextBox CreateTitleRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 36
            : (ViewModel.DisplayName.Length * 9.5) + 36;

        var editor = new TextBox
        {
            Text = ViewModel.DisplayName,
            PlaceholderText = _localizationService.T("Widget.TitlePlaceholder"),
            Width = Math.Clamp(titleWidth, 120, 220),
            MaxWidth = 220,
            FontSize = Math.Max(TitleText.FontSize - 1, 11),
            Style = GetTextBoxStyleResource("WidgetTitleRenameTextBoxStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        editor.KeyDown += TitleRenameEditor_KeyDown;
        editor.LostFocus += TitleRenameEditor_LostFocus;
        return editor;
    }

    private static Style? GetTextBoxStyleResource(string resourceKey)
    {
        return Application.Current.Resources.TryGetValue(resourceKey, out object? resource) && resource is Style style
            ? style
            : null;
    }

    private async void TitleRenameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitTitleRenameAsync();
    }

    private async void TitleRenameEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await CommitTitleRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelTitleRename();
            e.Handled = true;
        }
    }

    private async Task CommitTitleRenameAsync()
    {
        if (_isCommittingTitleRename ||
            QuickCaptureShell.TitleEditorContent is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await ViewModel.RenameAsync(newName);
            }

            CompleteTitleRename("quick-title-rename-committed");
        }
        catch (Exception ex)
        {
            ShowStatusToast(ex.Message);
            editor.Focus(FocusState.Programmatic);
            editor.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelTitleRename()
    {
        _isCancellingTitleRename = true;
        CompleteTitleRename("quick-title-rename-canceled");
    }

    private void CompleteTitleRename(string reason)
    {
        if (QuickCaptureShell.TitleEditorContent is TextBox editor)
        {
            editor.KeyDown -= TitleRenameEditor_KeyDown;
            editor.LostFocus -= TitleRenameEditor_LostFocus;
        }

        QuickCaptureShell.TitleEditorContent = null;
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        ReleaseInteractionLayer(reason);
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        ElevateForInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction("quick-flyout-opened");
        flyout.Closed += (_, _) =>
        {
            App.Current.WidgetManager?.EndWidgetInteraction("quick-flyout-closed");
            ReleaseInteractionLayer("quick-flyout-closed");
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

    private void ReleaseInteractionLayer(string reason)
    {
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, PositionLockButton) &&
               !IsWithin(source, SizeLockButton) &&
               !IsWithin(source, AddButton) &&
               !IsWithin(source, MoreButton) &&
               !IsWithin(source, CloseButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsItemActionSource(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button ||
                current is MenuFlyoutItem ||
                (current is FrameworkElement { Name: "ItemActionHost" }))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ItemsListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartQuickCaptureBoxSelection(e.OriginalSource))
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedQuickCaptureItemsInVisibleOrder().ToList()
            : [];
        _selectionPreviewItems = new HashSet<QuickCaptureItemViewModel>(_selectionSnapshot);
        _selectionHitTestItems = [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            ClearQuickCaptureCopySelection();
            ItemsListView.SelectedItem = null;
        }
    }

    private void ItemsListView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDragDistance(_selectionStartPoint, _selectionCurrentPoint) < 6.0)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            listView.CapturePointer(e.Pointer);
            if (_selectionHitTestItems.Count == 0)
            {
                CacheQuickCaptureSelectionHitTestItems(listView);
            }
        }

        UpdateQuickCaptureSelectionRectangleVisual();
        ApplyQuickCaptureSelectionRectanglePreview();
        e.Handled = true;
    }

    private void ItemsListView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        bool wasBoxSelecting = _isBoxSelecting;
        FinishQuickCaptureSelectionRectangle(listView);
        if (wasBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void ItemsListView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishQuickCaptureSelectionRectangle(listView);
        }
    }

    private bool CanStartQuickCaptureBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return FindQuickCaptureItemContainer(source) is null &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateQuickCaptureSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, selectionRect.X);
        Canvas.SetTop(SelectionRectangle, selectionRect.Y);
        SelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        SelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        SelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CacheQuickCaptureSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindVisualChild<Grid>(container, "QuickCaptureItemRoot") ?? container as FrameworkElement;
            if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = target.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            _selectionHitTestItems.Add(new QuickCaptureSelectionHitTestItem(item, bounds));
        }
    }

    private void ApplyQuickCaptureSelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<QuickCaptureItemViewModel>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplyQuickCaptureSelectionPreview(selectedItems);
    }

    private void FinishQuickCaptureSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplyQuickCaptureSelectionRectanglePreview();
            _copySelectionAnchorId = ViewModel.Items.FirstOrDefault(item => item.IsCopySelected)?.Id;
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            listView.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyQuickCaptureSelectionPreview(HashSet<QuickCaptureItemViewModel> selectedItems)
    {
        _selectionPreviewItems = selectedItems;
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = selectedItems.Contains(item);
        }
    }

    private void ToggleQuickCaptureSelection(QuickCaptureItemViewModel item)
    {
        item.IsCopySelected = !item.IsCopySelected;
        _copySelectionAnchorId = item.Id;
    }

    private void SelectQuickCaptureRange(QuickCaptureItemViewModel item)
    {
        if (ViewModel.Items.Count == 0)
        {
            return;
        }

        int endIndex = ViewModel.Items.IndexOf(item);
        if (endIndex < 0)
        {
            return;
        }

        int startIndex = endIndex;
        if (!string.IsNullOrWhiteSpace(_copySelectionAnchorId))
        {
            for (int index = 0; index < ViewModel.Items.Count; index++)
            {
                if (string.Equals(ViewModel.Items[index].Id, _copySelectionAnchorId, StringComparison.Ordinal))
                {
                    startIndex = index;
                    break;
                }
            }
        }

        int first = Math.Min(startIndex, endIndex);
        int last = Math.Max(startIndex, endIndex);
        ClearQuickCaptureCopySelection();
        for (int index = first; index <= last; index++)
        {
            ViewModel.Items[index].IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.Items[startIndex].Id;
    }

    private void SelectAllVisibleQuickCaptureItems()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.Items.FirstOrDefault()?.Id;
    }

    private void ClearQuickCaptureCopySelection()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        _copySelectionAnchorId = null;
    }

    private bool HasQuickCaptureCopySelection()
    {
        return ViewModel.Items.Any(item => item.IsCopySelected);
    }

    private IReadOnlyList<QuickCaptureItemViewModel> GetSelectedQuickCaptureItemsInVisibleOrder()
    {
        return ViewModel.Items
            .Where(item => item.IsCopySelected)
            .ToList();
    }

    private async Task CopySelectedQuickCaptureItemsAsync()
    {
        await CopySelectedQuickCaptureItemsAsync(GetSelectedQuickCaptureItemsInVisibleOrder());
    }

    private async Task CopySelectedQuickCaptureItemsAsync(IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1)
        {
            await CopyItemWithFeedbackAsync(selectedItems[0]);
            return;
        }

        string text = FormatQuickCaptureBatch(selectedItems);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowStatusToast(
                _localizationService.Format("QuickCapture.CopiedCount", selectedItems.Count),
                durationMs: CopyToastMs);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy {selectedItems.Count} selected items: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private string FormatQuickCaptureBatch(IReadOnlyList<QuickCaptureItemViewModel> selectedItems)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            selectedItems
                .Select(FormatQuickCaptureBatchItem)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private string FormatQuickCaptureBatchItem(QuickCaptureItemViewModel item)
    {
        if (item.Type == QuickCaptureItemType.Image)
        {
            string imageText = _localizationService.T("QuickCapture.ImageItem");
            return string.IsNullOrWhiteSpace(item.ImagePath)
                ? imageText
                : $"{imageText}: {item.ImagePath}";
        }

        return (item.Body ?? string.Empty).Trim();
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    private static FrameworkElement? FindQuickCaptureItemContainer(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Grid { Name: "QuickCaptureItemRoot", DataContext: QuickCaptureItemViewModel })
            {
                return (FrameworkElement)current;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double x = Math.Min(startPoint.X, endPoint.X);
        double y = Math.Min(startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - startPoint.X);
        double height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private static double GetSelectionDragDistance(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool RectsIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            bool isFromTextBox = e.OriginalSource is DependencyObject source &&
                HasAncestorOfType<TextBox>(source);
            if (!isFromTextBox && HasQuickCaptureCopySelection())
            {
                ClearQuickCaptureCopySelection();
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (ViewModel.IsSearchExpanded)
            {
                ViewModel.CollapseSearch();
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(InputTextBox.Text))
            {
                InputTextBox.Text = string.Empty;
            }

            RootGrid.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                e.AcceptedOperation = await HasSupportedQuickCaptureStorageDropAsync(e.DataView)
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
            }
            finally
            {
                deferral.Complete();
            }

            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else if (HasFallbackFileFormats(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private static bool HasFallbackFileFormats(DataPackageView dataView)
    {
        foreach (string format in dataView.AvailableFormats)
        {
            if (IsLikelyFileTransferFormat(format))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyFileTransferFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        if (format.StartsWith("Preferred DropEffect", StringComparison.Ordinal))
        {
            return false;
        }

        return format.Contains("StorageItems", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("StorageItem", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell", StringComparison.OrdinalIgnoreCase);
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var content = await TryReadDroppedContentAsync(e.DataView);
            if (content.Texts.Count == 0 && content.ImagePaths.Count == 0)
            {
                if (content.SkippedCount > 0)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.DroppedAllSkipped"));
                }

                return;
            }

            foreach (string text in content.Texts)
            {
                await ViewModel.AddTextAsync(text);
            }

            foreach (string imagePath in content.ImagePaths)
            {
                await ViewModel.AddImageFileAsync(imagePath);
            }

            ShowStatusToast(content.SkippedCount > 0
                ? _localizationService.T("QuickCapture.DroppedWithSkipped")
                : _localizationService.T("QuickCapture.Dropped"));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowCopyToast()
    {
        ShowStatusToast(_localizationService.T("QuickCapture.Copied"), durationMs: CopyToastMs);
    }

    private void ShowStatusToast(string text, string? actionText = null, int durationMs = StatusToastDefaultMs)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatusToast(text, actionText, durationMs));
            return;
        }

        long generation = ++_statusToastGeneration;
        StatusToast.MaxWidth = Math.Max(120, _appWindow.Size.Width - 24);
        StatusToastText.Text = text;
        StatusToastActionButton.Content = actionText;
        StatusToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        StatusToast.Opacity = 1;
        _ = HideStatusToastAfterDelayAsync(generation, durationMs);
    }

    private async Task HideStatusToastAfterDelayAsync(long generation, int durationMs)
    {
        await Task.Delay(durationMs);
        if (generation != _statusToastGeneration)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => HideStatusToastIfCurrent(generation));
            return;
        }

        HideStatusToastIfCurrent(generation);
    }

    private void HideStatusToastIfCurrent(long generation)
    {
        if (generation == _statusToastGeneration)
        {
            StatusToast.Opacity = 0;
            StatusToast.IsHitTestVisible = false;
            StatusToastActionButton.Visibility = Visibility.Collapsed;
            _pendingDeletedItemSnapshot = null;
        }
    }

    private async void StatusToastActionButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _pendingDeletedItemSnapshot;
        if (snapshot is null)
        {
            return;
        }

        _pendingDeletedItemSnapshot = null;
        if (await ViewModel.RestoreDeletedItemAsync(snapshot))
        {
            ShowStatusToast(_localizationService.T("Common.Undone"));
        }
    }

    private static async Task<QuickCaptureDropContent> TryReadDroppedContentAsync(DataPackageView dataView)
    {
        string? internalText = await DeskBoxDragData.TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText.Length > QuickCaptureClipboardService.MaxClipboardTextCharacters
                ? new QuickCaptureDropContent([], [], 1)
                : new QuickCaptureDropContent([internalText], [], 0);
        }

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            HasFallbackFileFormats(dataView))
        {
            return await TryReadDroppedStorageContentAsync(dataView);
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return new QuickCaptureDropContent([link.AbsoluteUri], [], 0);
            }
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length > QuickCaptureClipboardService.MaxClipboardTextCharacters
                    ? new QuickCaptureDropContent([], [], 1)
                    : new QuickCaptureDropContent([text], [], 0);
            }
        }

        return QuickCaptureDropContent.Empty;
    }

    private static async Task<QuickCaptureDropContent> TryReadDroppedStorageContentAsync(DataPackageView dataView)
    {
        var texts = new List<string>();
        var imagePaths = new List<string>();
        int skippedCount = 0;

        try
        {
            var storageItems = await dataView.GetStorageItemsAsync();
            foreach (var storageItem in storageItems)
            {
                if (storageItem is not StorageFile file ||
                    string.IsNullOrWhiteSpace(file.Path))
                {
                    skippedCount++;
                    continue;
                }

                if (IsImageFile(file.Path))
                {
                    if (IsFileWithinSizeLimit(file.Path, MaxDroppedImageFileBytes))
                    {
                        imagePaths.Add(file.Path);
                    }
                    else
                    {
                        skippedCount++;
                    }

                    continue;
                }

                if (IsTextFile(file.Path))
                {
                    if (!IsFileWithinSizeLimit(file.Path, MaxDroppedTextFileBytes))
                    {
                        skippedCount++;
                        continue;
                    }

                    string text = await FileIO.ReadTextAsync(file);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters)
                        {
                            texts.Add(text);
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }

                    continue;
                }

                skippedCount++;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to read dropped storage content: {ex}");
        }

        return new QuickCaptureDropContent(texts, imagePaths, skippedCount);
    }

    private static async Task<bool> HasSupportedQuickCaptureStorageDropAsync(DataPackageView dataView)
    {
        try
        {
            var storageItems = await dataView.GetStorageItemsAsync();
            return storageItems.Any(storageItem =>
                storageItem is StorageFile file &&
                !string.IsNullOrWhiteSpace(file.Path) &&
                ((IsImageFile(file.Path) && IsFileWithinSizeLimit(file.Path, MaxDroppedImageFileBytes)) ||
                 (IsTextFile(file.Path) && IsFileWithinSizeLimit(file.Path, MaxDroppedTextFileBytes))));
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to inspect dropped storage content: {ex}");
            return false;
        }
    }

    private static bool IsImageFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextFile(string? path)
    {
        string extension = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetExtension(path);
        return extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileWithinSizeLimit(string path, long maxBytes)
    {
        try
        {
            return new FileInfo(path).Length <= maxBytes;
        }
        catch
        {
            return false;
        }
    }

    private sealed record QuickCaptureDropContent(
        IReadOnlyList<string> Texts,
        IReadOnlyList<string> ImagePaths,
        int SkippedCount)
    {
        public static QuickCaptureDropContent Empty { get; } = new([], [], 0);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginWindowDrag(e, TitleBarGrid, focusWhenClicked: true);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginWindowDrag(e, QuickCaptureShell.DragHandleElement, focusWhenClicked: false);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement, bool focusWhenClicked)
    {
        if (ViewModel.Config.IsPositionLocked)
        {
            return;
        }

        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        _focusRootAfterDragClick = focusWhenClicked;
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out _initialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - _initialCursorPt.X;
        int deltaY = currentPt.Y - _initialCursorPt.Y;

        if (Math.Abs(deltaX) > 2 || Math.Abs(deltaY) > 2)
        {
            _hasMovedTitleBarDrag = true;
        }

        ApplyWindowBounds(
            _initialWindowPos.X + deltaX,
            _initialWindowPos.Y + deltaY,
            _initialWindowSize.Width,
            _initialWindowSize.Height,
            persist: false);
        e.Handled = true;
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        bool hasMoved = _hasMovedTitleBarDrag;
        _dragCaptureElement?.ReleasePointerCapture(e.Pointer);
        _dragCaptureElement = null;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        CapturePositionAnchor(pos.X, pos.Y, size.Width, size.Height);
        UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: true);
        if (!hasMoved && _focusRootAfterDragClick)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }

        _hasMovedTitleBarDrag = false;
        _focusRootAfterDragClick = false;
        e.Handled = true;
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Config.IsSizeLocked || sender is not FrameworkElement element)
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
        ElevateForInteraction();
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
        var minSize = GetPhysicalMinimumWindowSize(
            _initialWindowPos.X,
            _initialWindowPos.Y,
            _initialWindowSize.Width,
            _initialWindowSize.Height);

        if (_resizeDirection.Contains("Right", StringComparison.Ordinal))
        {
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left", StringComparison.Ordinal))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom", StringComparison.Ordinal))
        {
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top", StringComparison.Ordinal))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height - deltaY);
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
        element.ReleasePointerCapture(e.Pointer);
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        CapturePositionAnchor(pos.X, pos.Y, size.Width, size.Height);
        UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: true);
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        string tag = element.Tag as string ?? string.Empty;
        var shape = tag switch
        {
            "Left" or "Right" => Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast,
            "Top" or "Bottom" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth,
            "TopLeft" or "BottomRight" => Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast,
            "TopRight" or "BottomLeft" => Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest,
            _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow
        };
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
    }

    private void QuickCaptureWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ApplyBackdropPreference());

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] QuickCapture Deactivated→QueueRestore hwnd=0x{_hWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }

            return;
        }

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing ||
            WidgetLayerService.UsesDesktopPinnedMode() ||
            (App.Current.WidgetManager is { WidgetsRaisedFromTray: true }))
        {
            App.Log($"[ZOrder] QuickCapture PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] QuickCapture PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible || _isAtDesktopLayer)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            bool foregroundIsWidget = App.Current.WidgetManager is { } wm && wm.IsWidgetWindow(foregroundWindow);
            if (foregroundIsWidget)
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("quick-window-deactivated"))
                {
                    RestoreDesktopLayer(force: true);
                }
            }
            else
            {
                RestoreDesktopLayer(force: true);
            }
        });
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer SKIPPED guard1 force={force} idle={_restoreDesktopLayerWhenIdle} keep={_keepRaisedUntilDeactivate}");
            return;
        }

        if (!force && (_isDragging || _isResizing))
        {
            App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer SKIPPED guard2 force={force} drag={_isDragging} resize={_isResizing}");
            if (force || _restoreDesktopLayerWhenIdle)
            {
                _restoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        App.Log($"[ZOrder] QuickCapture RestoreDesktopLayer EXECUTING force={force}");
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
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
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            _isAtDesktopLayer = true;
            _keepRaisedUntilDeactivate = false;
            _restoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(_hWnd);
            App.Log($"[ZOrder] QuickCapture HoldTemporaryTopMost skipped pinned hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(_hWnd);
        App.Log($"[ZOrder] QuickCapture HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    private void StartTopMostSafetyTimer()
    {
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = DispatcherQueue.CreateTimer();
        _topMostSafetyTimer.IsRepeating = false;
        _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
        _topMostSafetyTimer.Tick += (_, _) =>
        {
            _topMostSafetyTimer?.Stop();
            _topMostSafetyTimer = null;
            if (!_isAtDesktopLayer && !_isDragging && !_isResizing &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                App.Log($"[ZOrder] QuickCapture safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
        };
        _topMostSafetyTimer.Start();
    }

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
        width = Math.Max(minSize.Width, width);
        height = Math.Max(minSize.Height, height);

        _isApplyingBounds = true;
        try
        {
            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        finally
        {
            _isApplyingBounds = false;
        }

        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: false);
        }
    }

    private Windows.Graphics.SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new Windows.Graphics.RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    private void ShowConfirmMenu(FrameworkElement anchor, string title, string actionText, Func<Task> confirmedAction)
    {
        _pendingDeleteConfirmFlyout?.Hide();

        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            title,
            actionText,
            confirmedAction);
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pendingDeleteConfirmFlyout, flyout))
            {
                _pendingDeleteConfirmFlyout = null;
            }
        };

        _pendingDeleteConfirmFlyout = flyout;
        ShowFlyoutWithElevation(flyout, anchor);
    }

    private void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        ViewModel.Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
    }

    private void UpdateConfigBoundsFromPhysical(int x, int y, int width, int height, bool persist)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
        if (persist)
        {
            _settingsService.UpdateWidget(ViewModel.Config, notifySubscribers: false);
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
        if (_hWnd == IntPtr.Zero || _isClosing)
        {
            return;
        }

        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            ApplySurfaceStyle();
            return;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        Win32Helper.SetWindowTheme(_hWnd, isDark);

        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);

        try
        {
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            RootGrid.Background = new SolidColorBrush(Colors.Transparent);

            int backdropType;
            if (ApplyAcrylicController(isDark, tintColor, surfaceOpacity))
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DisposeAcrylicController();
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"QuickCapture ApplyBackdropPreference fallback: {ex}");
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
        double tintOpacity = isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44);
        double luminosityOpacity = isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86);
        _acrylicController.TintOpacity = (float)tintOpacity;
        _acrylicController.LuminosityOpacity = (float)luminosityOpacity;
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
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
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
        var secondaryForeground = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.Background = new SolidColorBrush(surfaceColor);
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        QuickCaptureShell.TitleIconAccentColor = iconForeground;
        QuickCaptureShell.TitleIconKind = WidgetTitleIconKindNames.QuickCapture;
        QuickCaptureShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryForeground);
        SelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        ApplySearchVisualStyle(isDark, accentColor);
        ApplyEditOverlayStyle(isDark, accentColor);
        RefreshSelectedViewSegment();
    }

    private void ApplyTabStyles()
    {
        RefreshSelectedViewSegment();
    }

    private void RefreshSelectedViewSegment()
    {
        if (QuickCaptureViewSegmented is null)
        {
            return;
        }

        int selectedIndex = GetViewSegmentIndex(ViewModel.SelectedView);
        if (QuickCaptureViewSegmented.SelectedIndex != selectedIndex)
        {
            QuickCaptureViewSegmented.SelectedIndex = selectedIndex;
        }
    }

    private QuickCaptureViewMode GetSelectedSegmentView()
    {
        return QuickCaptureViewSegmented?.SelectedIndex switch
        {
            1 => QuickCaptureViewMode.Pinned,
            2 => QuickCaptureViewMode.Recent,
            _ => QuickCaptureViewMode.Records
        };
    }

    private static int GetViewSegmentIndex(QuickCaptureViewMode view)
    {
        return view switch
        {
            QuickCaptureViewMode.Pinned => 1,
            QuickCaptureViewMode.Recent => 2,
            _ => 0
        };
    }

    private void ApplyTitleBarLayout()
    {
        var chromeMode = _chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? ViewModel.TextSize
            : ViewModel.TitleTextSize;
        var metrics = WidgetTitleBarMetricsCalculator.Create(
            ViewModel.TitleIconSize,
            titleTextSize,
            includeInnerPadding: true,
            chromeMode);

        QuickCaptureShell.ChromeMode = chromeMode;
        QuickCaptureShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        TitleIcon.IconSize = metrics.TitleIconSize;
        TitleText.FontSize = metrics.TitleTextSize;
        ApplyTitleActionButtonConfiguration();
        ApplyLockActionIconState();

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetActionIconHelper.ApplyPairSize(PositionLockButtonIcon, PositionLockButtonFilledIcon, metrics);
        WidgetActionIconHelper.ApplyPairSize(SizeLockButtonIcon, SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        QuickCaptureShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
    }

    private void ApplyTitleActionButtonConfiguration()
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
        PositionLockButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockPosition)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SizeLockButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionLockSize)
            ? Visibility.Visible
            : Visibility.Collapsed;
        QuickCaptureShell.ShowAddButton = actions.Contains(SettingsService.WidgetHoverActionAdd);
        MoreButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionMore)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CloseButton.Visibility = actions.Contains(SettingsService.WidgetHoverActionDelete)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            PositionLockButtonIcon,
            PositionLockButtonFilledIcon,
            ViewModel.Config.IsPositionLocked,
            SizeLockButtonIcon,
            SizeLockButtonFilledIcon,
            ViewModel.Config.IsSizeLocked);
    }

    private void ApplySearchVisualStyle(bool isDark, Windows.UI.Color accentColor)
    {
        var background = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark ? ColorHelper.FromArgb(0xFF, 0x24, 0x27, 0x2D) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: ViewModel.IsSearchExpanded ? (isDark ? 0.20 : 0.10) : 0.0,
            overlayMix: isDark ? 0.05 : 0.0);

        SearchTextBox.Background = new SolidColorBrush(WithAlpha(background, isDark ? (byte)0xE8 : (byte)0xF6));
        SearchTextBox.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xCC : (byte)0xAA));
    }

    private void ApplyEditOverlayStyle(bool isDark, Windows.UI.Color accentColor)
    {
        QuickCaptureInlineEditor.OverlaySurface.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        QuickCaptureInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        QuickCaptureInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        QuickCaptureInlineEditor.Translation = new Vector3(0, 0, 16);

        EditTextBox.Background = new SolidColorBrush(GetNeutralInputSurfaceColor(isDark));
        EditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        EditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);
    }

    private static Windows.UI.Color GetNeutralOverlaySurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x2A, 0x30, 0x38)
            : ColorHelper.FromArgb(0xFF, 0xFB, 0xFC, 0xFD);
    }

    private static Windows.UI.Color GetNeutralInputSurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x22, 0x28, 0x30)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private static Brush GetNeutralOverlayBorderBrush(bool isDark)
    {
        return GetBrushResourceOrFallback(
            "CardStrokeColorDefaultBrush",
            isDark ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    private void PlayItemsViewTransition()
    {
        if (!RootGrid.IsLoaded)
        {
            ResetItemsViewTransitionState();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ItemsListView.UpdateLayout();
            EmptyStateHost.UpdateLayout();

            if (ItemsListView.Visibility == Visibility.Visible)
            {
                ItemsListView.Opacity = 0;
                StartSubtleOffsetAnimation(ItemsListView, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(ItemsListView, 0, 1, ItemsViewTransitionMs);
            }

            if (EmptyStateHost.Visibility == Visibility.Visible)
            {
                EmptyStateHost.Opacity = 0;
                StartSubtleOffsetAnimation(EmptyStateHost, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(EmptyStateHost, 0, 1, ItemsViewTransitionMs);
            }
        });
    }

    private void ResetItemsViewTransitionState()
    {
        ResetTransitionState(ItemsListView);
        ResetTransitionState(EmptyStateHost);
    }

    private static void ResetTransitionState(UIElement element)
    {
        element.Opacity = 1;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;
    }

    private static void StartOpacityAnimation(UIElement element, double from, double to, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.InsertKeyFrame(0.0f, (float)from);
        animation.InsertKeyFrame(1.0f, (float)to, easing);
        visual.Opacity = (float)to;
        visual.StartAnimation("Opacity", animation);
    }

    private static void StartSubtleOffsetAnimation(
        UIElement element,
        double fromX,
        double toX,
        double fromY,
        double toY,
        int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Offset");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));
        var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
        offsetAnimation.Duration = TimeSpan.FromMilliseconds(durationMs);
        offsetAnimation.InsertKeyFrame(0.0f, new Vector3((float)fromX, (float)fromY, 0));
        offsetAnimation.InsertKeyFrame(1.0f, new Vector3((float)toX, (float)toY, 0), easing);
        visual.Offset = new Vector3((float)toX, (float)toY, 0);
        visual.StartAnimation("Offset", offsetAnimation);
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

    private static Windows.UI.Color ApplySurfaceOpacity(Windows.UI.Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild &&
                (string.IsNullOrEmpty(name) || string.Equals(typedChild.Name, name, StringComparison.Ordinal)))
            {
                return typedChild;
            }

            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static Button? FindPinnedButton(DependencyObject parent)
    {
        return FindVisualChild<Button>(parent, "PinItemButton");
    }

    private void QueueBackdropRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueBackdropRefresh);
            return;
        }

        long generation = ++_backdropRefreshGeneration;
        _ = RefreshBackdropAfterDelayAsync(generation, 80);
        _ = RefreshBackdropAfterDelayAsync(generation, 320);
        _ = RefreshBackdropAfterDelayAsync(generation, 900);
    }

    private async Task RefreshBackdropAfterDelayAsync(long generation, int delayMs)
    {
        await Task.Delay(delayMs);
        if (generation != _backdropRefreshGeneration)
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RefreshBackdropIfCurrent(generation));
            return;
        }

        RefreshBackdropIfCurrent(generation);
    }

    private void RefreshBackdropIfCurrent(long generation)
    {
        if (generation == _backdropRefreshGeneration)
        {
            if (!Visible || _isHideAnimationRunning)
            {
                return;
            }

            ApplyBackdropPreference();
        }
    }

    private void PlayTrayRaiseAnimation()
    {
        long generation = _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        _isHideAnimationRunning = false;

        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={generation}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={generation} durationMs={profile.DurationMs}");
        _trayAnimation.Animate(
            profile.ShowOffsetX,
            profile.ShowOffsetY,
            0,
            0,
            profile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            profile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            profile.DurationMs,
            true,
            generation,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                _trayAnimation.RestoreVisualState();
                _trayAnimation.RestoreWindowPosition();
            });
    }

    private void PlayTrayRaiseAnimationAfterFirstFrame()
    {
        if (Visible)
        {
            PlayTrayRaiseAnimation();
        }
    }

    private void PlayTrayHideAnimation(Action completed)
    {
        long generation = _trayAnimation.Generation;
        var profile = GetTrayAnimationProfile();
        if (!profile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={generation}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={generation} durationMs={profile.DurationMs}");
        _trayAnimation.Animate(
            0,
            0,
            profile.HideOffsetX,
            profile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            profile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            profile.HideEndScale,
            profile.DurationMs,
            false,
            generation,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
            {
                if (!Visible)
                {
                    completed();
                }
            });
    }

    private void CompleteTrayHideAnimation()
    {
        if (Visible)
        {
            LogTrayWindow("CompleteHide skipped reason=visible-again");
            return;
        }

        _isHideAnimationRunning = false;
        _trayAnimation.Stop();
        RestoreNativeBackdropAfterTrayReveal();
        WidgetLayerService.ClearTopMost(_hWnd);
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_HIDE);
        _appWindow.Hide();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();
        LogTrayWindow("CompleteHide");
    }

    private void SuppressNativeBackdropForTrayReveal()
    {
        if (_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = true;
        DisposeAcrylicController();
        Win32Helper.DisableAccentPolicy(_hWnd);
    }

    private void RestoreNativeBackdropAfterTrayReveal()
    {
        if (!_isNativeBackdropSuppressedForTrayReveal)
        {
            return;
        }

        _isNativeBackdropSuppressedForTrayReveal = false;
        ApplyBackdropPreference();
    }

    private WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return _trayAnimation.CreateProfile(WidgetAnimationSettings.From(_settingsService.Settings));
    }

    private void LogTrayWindow(string message)
    {
        App.LogVerbose(_diagnostics.FormatTrayWindowMessage(message));
    }
}
