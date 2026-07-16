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

public sealed partial class QuickCaptureWidgetWindow : WidgetWindowBase, IDesktopWidgetWindow
{
    private sealed record QuickCaptureSelectionHitTestItem(
        QuickCaptureItemViewModel Item,
        Windows.Foundation.Rect Bounds);

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
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

    // ── Backward-compatible aliases for base class fields ──────
    private SettingsService _settingsService => SettingsService;
    private IntPtr _hWnd => HWnd;
    private AppWindow _appWindow => AppWindow;
    private WidgetWindowDiagnostics _diagnostics => Diagnostics;
    private WidgetTrayAnimationController _trayAnimation => TrayAnimation;

    // Mutable field aliases for inherited protected state
    private DesktopAcrylicController? _acrylicController { get => AcrylicController; set => AcrylicController = value; }
    private MicaController? _micaController { get => MicaController; set => MicaController = value; }
    private SystemBackdropConfiguration? _backdropConfiguration { get => BackdropConfiguration; set => BackdropConfiguration = value; }
    private ICompositionSupportsSystemBackdrop? _backdropTarget { get => BackdropTarget; set => BackdropTarget = value; }
    private bool _isDragging { get => IsDragging; set => IsDragging = value; }
    private bool _hasMovedTitleBarDrag { get => HasMovedTitleBarDrag; set => HasMovedTitleBarDrag = value; }
    private bool _isResizing { get => IsResizing; set => IsResizing = value; }
    private bool _isApplyingBounds { get => IsApplyingBounds; set => IsApplyingBounds = value; }
    private string _resizeDirection { get => ResizeDirection; set => ResizeDirection = value; }
    private Win32Helper.POINT _initialCursorPt { get => InitialCursorPt; set => InitialCursorPt = value; }
    private Windows.Graphics.PointInt32 _initialWindowPos { get => InitialWindowPos; set => InitialWindowPos = value; }
    private Windows.Graphics.SizeInt32 _initialWindowSize { get => InitialWindowSize; set => InitialWindowSize = value; }
    private FrameworkElement? _dragCaptureElement { get => DragCaptureElement; set => DragCaptureElement = value; }
    private bool _isAtDesktopLayer { get => IsAtDesktopLayer; set => IsAtDesktopLayer = value; }
    private bool _keepRaisedUntilDeactivate { get => KeepRaisedUntilDeactivate; set => KeepRaisedUntilDeactivate = value; }
    private bool _restoreDesktopLayerWhenIdle { get => RestoreDesktopLayerWhenIdle; set => RestoreDesktopLayerWhenIdle = value; }
    private bool _isHideAnimationRunning { get => IsHideAnimationRunning; set => IsHideAnimationRunning = value; }
    private bool _isClosing { get => IsClosing; set => IsClosing = value; }
    private long _backdropRefreshGeneration { get => BackdropRefreshGeneration; set => BackdropRefreshGeneration = value; }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _topMostSafetyTimer { get => TopMostSafetyTimer; set => TopMostSafetyTimer = value; }
    private WidgetDisplayChangeWatcher? _displayChangeWatcher { get => DisplayChangeWatcher; set => DisplayChangeWatcher = value; }
    private DateTime _lastElevateForInteractionUtc { get => LastElevateForInteractionUtc; set => LastElevateForInteractionUtc = value; }

    // ── QuickCapture-specific state ────────────────────────────
    private readonly LocalizationService _localizationService;
    private readonly WidgetContentDescriptor _chromeDescriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private bool _focusRootAfterDragClick;
    private bool _hasPlayedInitialItemsTransition;
    private bool _isClearingData;
    private bool _isCommittingTitleRename;
    private bool _isCancellingTitleRename;
    private bool _isNativeBackdropSuppressedForTrayReveal;
    private QuickCaptureItemViewModel? _editingItem;
    private bool _isExpandingInput;
    private QuickCaptureItemViewModel? _detailItem;
    private bool _isCreatingDetail;
    private bool _detailIsPinned;
    private QuickCaptureAppearancePreset _detailAppearance = QuickCaptureAppearancePreset.Default;
    private List<DroppedFilePath> _pendingDetailAttachments = [];
    private long _statusToastGeneration;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoRestoreTimer;
    private QuickCaptureDeletedItemSnapshot? _pendingDeletedItemSnapshot;
    private MenuFlyout? _pendingDeleteConfirmFlyout;
    private string? _copySelectionAnchorId;
    private string? _draggedQuickCaptureItemId;
    private bool _isInternalQuickCaptureDrag;
    private QuickCaptureViewMode? _internalQuickCaptureDragView;
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

    public override WidgetConfig Config => ViewModel.Config;

    public Windows.Foundation.Rect AnimationBounds => _diagnostics.AnimationBounds;

    private bool _isVisibleOnDesktop;
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
    }

    // ── WidgetWindowBase abstract overrides ────────────────────
    protected override double WidgetOpacity => ViewModel.WidgetOpacity;
    protected override FrameworkElement RootElement => RootGrid;
    protected override string LogPrefix => "Quick";
    protected override bool IsSizeLocked => ViewModel.Config.IsSizeLocked;
    protected override bool IsPositionLocked => ViewModel.Config.IsPositionLocked;
    protected override bool IsBackdropSuppressedForTrayReveal => _isNativeBackdropSuppressedForTrayReveal;

    protected override void OnElevated()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    protected override void ConfigureWindowExtra()
    {
        Win32Helper.AllowShellDragDropMessages(HWnd);
    }

    protected override void OnRootElementLoaded()
    {
        ApplySurfaceStyle();
        RootGrid.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTabStyles();
            ResetItemsViewTransitionState();
            // WidgetManager.InitializeAsync may have already loaded data
            // before the view was ready. Refresh the view from cache to
            // ensure items are visible.
            ViewModel.RefreshAfterViewReady();
        });
    }

    protected override void OnRootElementThemeChanged()
    {
        ApplySurfaceStyle();
    }

    protected override void OnDragEnd(bool hasMoved)
    {
        if (!hasMoved && _focusRootAfterDragClick)
        {
            RootGrid.Focus(FocusState.Programmatic);
        }

        _focusRootAfterDragClick = false;
    }

    protected override void OnResizeStart()
    {
        ElevateForInteraction();
    }

    public QuickCaptureWidgetWindow(
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ViewModel = viewModel;
        SettingsService = settingsService;
        _localizationService = localizationService;
        _chromeDescriptor = new WidgetContentFactory(_localizationService).GetDescriptor(WidgetKind.QuickCapture);
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        QuickCaptureShell.TitleGlyph = "\uE70F";
        QuickCaptureShell.TitleIconKind = WidgetTitleIconKindNames.QuickCapture;
        QuickCaptureShell.ShowHoverButtons = SettingsService.Settings.ShowHoverButtons;

        HWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(HWnd);
        AppWindow = AppWindow.GetFromWindowId(windowId);
        Diagnostics = new WidgetWindowDiagnostics("Quick", ViewModel.Config, () => HWnd);
        TrayAnimation = new WidgetTrayAnimationController(
            AppWindow,
            RootGrid,
            DispatcherQueue,
            HWnd,
            () => Diagnostics.AnimationBounds,
            LogTrayWindow);

        ConfigureWindowCore();
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
        App.LogVerbose($"[ZOrder] QuickCapture PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
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
        Win32Helper.SetForegroundWindow(_hWnd);
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
            if (!_isDragging && !_isResizing && !ShouldDeferDesktopLayerRestore())
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

        // Unsubscribe from external events BEFORE Close() so that
        // SettingsChanged / LanguageChanged / ThemeChanged callbacks
        // (which fire synchronously from SaveDebounced / SaveAsync)
        // cannot reach a window whose WinRT objects are being torn down.
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Activated -= QuickCaptureWidgetWindow_Activated;
        _appWindow.Changed -= OnAppWindowChanged;

        _autoRestoreTimer?.Stop();
        _autoRestoreTimer = null;
        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        StopBackdropRefreshTimer();
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

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += QuickCaptureWidgetWindow_Activated;

        _appWindow.Changed += OnAppWindowChanged;
        _displayChangeWatcher = new WidgetDisplayChangeWatcher(_hWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);

        foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
            {
                child.PointerMoved += ResizeBorder_PointerMoved;
                child.PointerReleased += ResizeBorder_PointerReleased;
                child.PointerEntered += ResizeBorder_PointerEntered;
                child.PointerCaptureLost += ResizeBorder_PointerCaptureLost;
            }
        }

        Closed += (_, _) =>
        {
            _isClosing = true;
            Visible = false;

            // Events were already unsubscribed in CloseWindow().
            // Repeat here for the case where the window is closed via
            // an external path (e.g. Alt+F4) that bypasses CloseWindow().
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            Activated -= QuickCaptureWidgetWindow_Activated;
            _appWindow.Changed -= OnAppWindowChanged;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;

            // TrayAnimation.Stop() was already called in CloseWindow().
            // Call again only as a fallback; Stop() is safe to call twice.
            try { _trayAnimation.Stop(); } catch { }

            try { CleanupBase(); } catch (Exception ex) { App.Log($"[QuickCapture] CleanupBase failed during close: {ex.Message}"); }

            foreach (var child in ResizeGrid.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                {
                    child.PointerMoved -= ResizeBorder_PointerMoved;
                    child.PointerReleased -= ResizeBorder_PointerReleased;
                    child.PointerEntered -= ResizeBorder_PointerEntered;
                    child.PointerCaptureLost -= ResizeBorder_PointerCaptureLost;
                }
            }
        };
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
        if (_isClosing)
        {
            return;
        }

        ApplyLocalizedText();
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

        if (_isClosing)
        {
            return;
        }

        ApplyWindowCornerPreference();
        QuickCaptureShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        ApplyBackdropPreference();
        QueueBackdropRefresh();
        ApplyTitleBarLayout();
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

}
