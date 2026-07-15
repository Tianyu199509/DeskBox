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
using Windows.Storage.Pickers;
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

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanAddInput)
        {
            await ViewModel.AddInputAsync();
            InputTextBox.Focus(FocusState.Programmatic);
            return;
        }

        OpenNewDetail();
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
        OpenNewDetail(InputTextBox.Text);
        InputTextBox.Text = string.Empty;
    }

    private void AddNoteCardButton_Click(object sender, RoutedEventArgs e)
    {
        OpenNewDetail();
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

    private void OpenNewDetail(string? initialBody = null)
    {
        _detailItem = null;
        _isCreatingDetail = true;
        _detailIsPinned = false;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        DetailBodyTextBox.Text = initialBody ?? string.Empty;
        RefreshDetailAttachmentList();
        DetailTimestampText.Text = _localizationService.Format(
            "QuickCapture.Detail.Created",
            DateTimeOffset.Now.ToString("yyyy/M/d HH:mm"));
        ShowDetailPage();
    }

    private void OpenDetail(QuickCaptureItemViewModel item)
    {
        if (item.IsRecent)
        {
            return;
        }

        _detailItem = item;
        _isCreatingDetail = false;
        _detailIsPinned = item.IsPinned;
        _detailAppearance = item.AppearancePreset;
        _pendingDetailAttachments = [];
        DetailTitleTextBox.Text = string.Empty;
        DetailBodyTextBox.Text = item.Type == QuickCaptureItemType.Image &&
                                 string.Equals(item.Body, "Image", StringComparison.Ordinal)
            ? string.Empty
            : BuildBodyText(item);
        DetailTimestampText.Text = BuildDetailTimestampText(item);
        RefreshDetailAttachmentList();
        ShowDetailPage();
    }

    private void ShowDetailPage()
    {
        ClearQuickCaptureCopySelection();
        ClearQuickCaptureListContainerSelection();
        CloseInlineEdit(restoreInputFocus: false);
        ListPage.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Visible;
        UpdateDetailPinVisual();
        ApplyDetailMaterialSurface();
        DispatcherQueue.TryEnqueue(() =>
        {
            DetailBodyTextBox.Focus(FocusState.Programmatic);
            DetailBodyTextBox.Select(DetailBodyTextBox.Text.Length, 0);
        });
    }

    private static string BuildBodyText(QuickCaptureItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Body;
        }

        return string.IsNullOrWhiteSpace(item.Body)
            ? item.Title
            : $"{item.Title}{Environment.NewLine}{item.Body}";
    }

    private async void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAndCloseDetailAsync();
    }

    private async Task<bool> SaveAndCloseDetailAsync()
    {
        string body = DetailBodyTextBox.Text;
        if (_isCreatingDetail)
        {
            if (_pendingDetailAttachments.Count > 0)
            {
                QuickCaptureItemViewModel? created = await ViewModel.AddItemWithAttachmentsAsync(
                    _pendingDetailAttachments);
                if (created is null)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.OpenImageFailed"));
                    return false;
                }

                await ViewModel.EditItemDetailsAsync(created, null, body, _detailAppearance);
                if (_detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(created.Id, true);
                }

                await ViewModel.RefreshItemsAsync();
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                QuickCaptureItem? created = await ViewModel.AddDetailedItemAsync(null, body, _detailAppearance);
                if (created is not null && _detailIsPinned)
                {
                    await ViewModel.SetPinnedAsync(created.Id, true);
                }
            }

            CloseDetailPage();
            return true;
        }

        if (_detailItem is not { } item)
        {
            CloseDetailPage();
            return true;
        }

        if (item.Type != QuickCaptureItemType.Image &&
            string.IsNullOrWhiteSpace(body))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return false;
        }

        bool saved = await ViewModel.EditItemDetailsAsync(item, null, body, _detailAppearance);
        if (!saved)
        {
            return false;
        }

        if (_detailIsPinned != item.IsPinned)
        {
            await ViewModel.SetPinnedAsync(item.Id, _detailIsPinned);
        }

        await ViewModel.RefreshItemsAsync();

        CloseDetailPage();
        return true;
    }

    private void CloseDetailPage()
    {
        _detailItem = null;
        _isCreatingDetail = false;
        _detailIsPinned = false;
        _detailAppearance = QuickCaptureAppearancePreset.Default;
        _pendingDetailAttachments = [];
        DetailAttachmentsList.ItemsSource = null;
        DetailAttachmentScroller.Visibility = Visibility.Collapsed;
        DetailPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        ClearQuickCaptureListContainerSelection();
        RefreshItemMaterialSurfaces();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void ClearQuickCaptureListContainerSelection()
    {
        ItemsListView.SelectedItem = null;
        foreach (object visibleItem in ItemsListView.Items)
        {
            if (ItemsListView.ContainerFromItem(visibleItem) is ListViewItem container)
            {
                container.IsSelected = false;
            }
        }
    }

    private async void DetailPinButton_Click(object sender, RoutedEventArgs e)
    {
        bool wasPinned = _detailIsPinned;
        bool isPinned = !wasPinned;
        _detailIsPinned = isPinned;
        UpdateDetailPinVisual();

        if (_detailItem is not null && !await ViewModel.SetPinnedAsync(_detailItem.Id, isPinned))
        {
            _detailIsPinned = wasPinned;
            UpdateDetailPinVisual();
            return;
        }

        if (isPinned)
        {
            ShowStatusToast(_localizationService.T("QuickCapture.PinnedSuccess"));
        }
    }

    private void UpdateDetailPinVisual()
    {
        DetailPinIcon.Glyph = _detailIsPinned ? "\uE840" : "\uE718";
        DetailUnpinSlash.Visibility = _detailIsPinned ? Visibility.Visible : Visibility.Collapsed;
        DetailPinButton.Background = _detailIsPinned
            ? GetBrushResourceOrFallback(
                "SubtleFillColorSecondaryBrush",
                DetailPinButton.ActualTheme == ElementTheme.Dark
                    ? ColorHelper.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Colors.Transparent);
        string tooltip = _localizationService.T(_detailIsPinned ? "QuickCapture.Unpin" : "QuickCapture.Pin");
        ToolTipService.SetToolTip(DetailPinButton, tooltip);
        AutomationProperties.SetName(DetailPinButton, tooltip);
    }

    private void DetailCopyButton_Click(object sender, RoutedEventArgs e)
    {
        string content = string.IsNullOrWhiteSpace(DetailBodyTextBox.Text)
            ? DetailTitleTextBox.Text.Trim()
            : DetailBodyTextBox.Text.Trim();
        IEnumerable<TodoAttachment> attachments = _detailItem?.Attachments
            .Select(attachment => attachment.Attachment) ??
            _pendingDetailAttachments.Select(file => new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path)
            });
        string text = QuickCaptureClipboardFormatter.FormatContent(
            content,
            attachments,
            _localizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
        App.Current.QuickCaptureService?.MarkClipboardTextWrittenByDeskBox(text);
        ShowCopyToast();
    }

    private async void DetailAddFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        var droppedFiles = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path))
            .Select(file => new DroppedFilePath(file.Path, file.Name, ForceManagedCopy: false))
            .ToList();
        await AddFilesToCurrentDetailAsync(droppedFiles);
    }

    private async Task AddFilesToCurrentDetailAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            foreach (DroppedFilePath file in files)
            {
                if (!_pendingDetailAttachments.Any(existing =>
                        string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingDetailAttachments.Add(file);
                }
            }
            RefreshDetailAttachmentList();
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(_detailItem, files);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void DetailOpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        try
        {
            if (!File.Exists(attachment.FilePath))
            {
                ShowStatusToast(_localizationService.T("Todo.Detail.FileMissing"));
                return;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to open attachment: {ex}");
        }
    }

    private async void DetailRemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            return;
        }

        if (_isCreatingDetail || _detailItem is null)
        {
            _pendingDetailAttachments.RemoveAll(file =>
                string.Equals(file.Path, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshDetailAttachmentList();
            return;
        }

        if (_detailItem.Attachments.Count == 1 && string.IsNullOrWhiteSpace(DetailBodyTextBox.Text))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.EmptyEdit"));
            return;
        }

        QuickCaptureItemViewModel? updated = await ViewModel.DeleteAttachmentAsync(
            _detailItem,
            attachment.Id);
        if (updated is not null)
        {
            _detailItem = updated;
            RefreshDetailAttachmentList();
        }
    }

    private async void QuickCaptureAttachmentPreview_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoAttachmentViewModel attachment })
        {
            await attachment.EnsureThumbnailAsync();
        }
    }

    private void RefreshDetailAttachmentList()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments = _detailItem?.Attachments ??
            _pendingDetailAttachments.Select(file => new TodoAttachmentViewModel(new TodoAttachment
            {
                FilePath = file.Path,
                DisplayName = file.DisplayName,
                Type = AttachmentStorageService.GetAttachmentType(file.Path),
                StorageMode = TodoAttachment.LinkedStorageMode
            })).ToList();
        DetailAttachmentsList.ItemsSource = attachments;
        DetailAttachmentScroller.Visibility = attachments.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out QuickCaptureAppearancePreset preset))
        {
            _detailAppearance = preset;
            ApplyDetailMaterialSurface();
        }
    }

    private void ApplyDetailMaterialSurface()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        DetailMaterialSurface.Background = GetMaterialBrush(_detailAppearance, isDark);
        DetailMaterialSurface.BorderBrush = GetMaterialBorderBrush(_detailAppearance, isDark);
        foreach (Button button in GetMaterialButtons())
        {
            bool isSelected = string.Equals(button.Tag as string, _detailAppearance.ToString(), StringComparison.Ordinal);
            button.BorderBrush = isSelected
                ? GetBrushResourceOrFallback(
                    "AccentFillColorDefaultBrush",
                    isDark ? ColorHelper.FromArgb(0xFF, 0x60, 0xCD, 0xFF) : ColorHelper.FromArgb(0xFF, 0x00, 0x5F, 0xB8))
                : new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
        }
    }

    private IEnumerable<Button> GetMaterialButtons()
    {
        yield return DefaultMaterialButton;
        yield return PaperMaterialButton;
        yield return YellowMaterialButton;
        yield return RoseMaterialButton;
        yield return MintMaterialButton;
        yield return BlueMaterialButton;
    }

    private void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCreatingDetail || _detailItem is not { } item || sender is not FrameworkElement anchor)
        {
            CloseDetailPage();
            return;
        }

        ShowConfirmMenu(
            anchor,
            _localizationService.T("QuickCapture.DeleteConfirm.Title"),
            _localizationService.T("Common.Delete"),
            async () =>
            {
                await DeleteItemWithUndoAsync(item);
                CloseDetailPage();
            });
    }

    private string BuildDetailTimestampText(QuickCaptureItemViewModel item)
    {
        QuickCaptureItem model = item.ToModel();
        string created = _localizationService.Format(
            "QuickCapture.Detail.Created",
            model.CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));
        return created;
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
            if (item.IsRecent)
            {
                await CopyItemWithFeedbackAsync(item);
            }
            else
            {
                OpenDetail(item);
            }
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
        var selectedItems = GetSelectedQuickCaptureItemsInVisibleOrder();
        var flyout = selectedItems.Count > 1 && item.IsCopySelected
            ? CreateMultiItemFlyout(selectedItems, anchor)
            : CreateItemFlyout(item, anchor);
        ShowFlyoutWithElevation(flyout, anchor, e.GetPosition(anchor));
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
        if (item.IsRecent)
        {
            await CopyItemWithFeedbackAsync(item);
        }
        else
        {
            OpenDetail(item);
        }
    }

    private async void CopyItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QuickCaptureItemViewModel item)
        {
            await CopyItemWithFeedbackAsync(item);
        }
    }

    private async void QuickCaptureAttachmentCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoAttachmentViewModel attachment } ||
            !attachment.IsImage ||
            !File.Exists(attachment.FilePath))
        {
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(attachment.FilePath);
            var dataPackage = new DataPackage();
            dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            DeskBoxClipboardWriteScope.MarkWrite(hasImage: true, paths: [attachment.FilePath]);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy attachment image: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
    }

    private async Task CopyImageWithFeedbackAsync(QuickCaptureItemViewModel item)
    {
        try
        {
            await ViewModel.CopyImageAsync(item);
            ShowCopyToast();
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to copy image {item.Id}: {ex}");
            ShowStatusToast(_localizationService.T("QuickCapture.CopyFailed"));
        }
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

    private void QuickCaptureItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not QuickCaptureItemViewModel item)
        {
            return;
        }

        SetItemActionButtonsVisible(sender, false);
        ApplyItemSearchHighlight(sender, item);
        ApplyItemMaterialSurface(sender, item);

        if (FindVisualChild<Button>(sender, "CopyItemButton") is { } copyButton)
        {
            string copyText = _localizationService.T("Common.Copy");
            ToolTipService.SetToolTip(copyButton, copyText);
            AutomationProperties.SetName(copyButton, copyText);
        }

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

    private void ItemsListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        QuickCaptureItemViewModel? item = e.Items.OfType<QuickCaptureItemViewModel>().FirstOrDefault();
        if (item is null)
        {
            _draggedQuickCaptureItemId = null;
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
            return;
        }

        bool canReorder = !item.IsRecent &&
                          ViewModel.SelectedView is QuickCaptureViewMode.Records or QuickCaptureViewMode.Pinned &&
                          !ViewModel.HasSearchText;
        _draggedQuickCaptureItemId = canReorder ? item.Id : null;
        _isInternalQuickCaptureDrag = canReorder;
        _internalQuickCaptureDragView = canReorder ? ViewModel.SelectedView : null;
        ItemsListView.CanReorderItems = canReorder;

        try
        {
            if (!TryPrepareQuickCaptureDragPackage(e.Data, item))
            {
                _draggedQuickCaptureItemId = null;
                _isInternalQuickCaptureDrag = false;
                _internalQuickCaptureDragView = null;
                e.Cancel = true;
                return;
            }

            e.Data.RequestedOperation = canReorder
                ? DataPackageOperation.Copy | DataPackageOperation.Move
                : DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureWidget] Failed to start drag: {ex}");
            _draggedQuickCaptureItemId = null;
            _isInternalQuickCaptureDrag = false;
            _internalQuickCaptureDragView = null;
            e.Cancel = true;
        }
    }

    private async void ItemsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        string? itemId = _draggedQuickCaptureItemId;
        QuickCaptureViewMode? dragView = _internalQuickCaptureDragView;
        _draggedQuickCaptureItemId = null;
        _internalQuickCaptureDragView = null;
        ItemsListView.CanReorderItems = true;
        DispatcherQueue.TryEnqueue(() => _isInternalQuickCaptureDrag = false);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        QuickCaptureItemViewModel? item = ViewModel.Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is not null)
        {
            int targetIndex = ViewModel.Items.IndexOf(item);
            if (dragView == QuickCaptureViewMode.Pinned)
            {
                await ViewModel.MovePinnedItemToIndexAsync(item, targetIndex);
            }
            else
            {
                await ViewModel.MoveItemAsync(item, targetIndex);
            }
        }
    }

    private static void ApplyItemMaterialSurface(DependencyObject itemRoot, QuickCaptureItemViewModel item)
    {
        if (FindVisualChild<Border>(itemRoot, "ItemMaterialBackground") is not { } surface)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        QuickCaptureAppearancePreset preset = item.IsRecent
            ? QuickCaptureAppearancePreset.Default
            : item.AppearancePreset;
        surface.Background = GetOrUpdateSolidColorBrush(surface.Background, GetMaterialColor(preset, isDark));
        surface.BorderBrush = preset == QuickCaptureAppearancePreset.Default
            ? GetMaterialBorderBrush(preset, isDark)
            : GetOrUpdateSolidColorBrush(
                surface.BorderBrush,
                isDark
                    ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private static Brush GetMaterialBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return new SolidColorBrush(GetMaterialColor(preset, isDark));
    }

    private static Windows.UI.Color GetMaterialColor(QuickCaptureAppearancePreset preset, bool isDark)
    {
        return (preset, isDark) switch
        {
            (QuickCaptureAppearancePreset.Paper, true) => ColorHelper.FromArgb(0xB8, 0x3A, 0x36, 0x30),
            (QuickCaptureAppearancePreset.Paper, false) => ColorHelper.FromArgb(0xEC, 0xFA, 0xF5, 0xEA),
            (QuickCaptureAppearancePreset.StickyYellow, true) => ColorHelper.FromArgb(0xB8, 0x4A, 0x40, 0x25),
            (QuickCaptureAppearancePreset.StickyYellow, false) => ColorHelper.FromArgb(0xEC, 0xFF, 0xF0, 0xB3),
            (QuickCaptureAppearancePreset.Rose, true) => ColorHelper.FromArgb(0xB8, 0x47, 0x2E, 0x38),
            (QuickCaptureAppearancePreset.Rose, false) => ColorHelper.FromArgb(0xEC, 0xFC, 0xE3, 0xEA),
            (QuickCaptureAppearancePreset.Mint, true) => ColorHelper.FromArgb(0xB8, 0x28, 0x42, 0x35),
            (QuickCaptureAppearancePreset.Mint, false) => ColorHelper.FromArgb(0xEC, 0xDD, 0xF3, 0xE3),
            (QuickCaptureAppearancePreset.MistBlue, true) => ColorHelper.FromArgb(0xB8, 0x2B, 0x3D, 0x53),
            (QuickCaptureAppearancePreset.MistBlue, false) => ColorHelper.FromArgb(0xEC, 0xDF, 0xEC, 0xF8),
            _ => Colors.Transparent
        };
    }

    private static Brush GetMaterialBorderBrush(QuickCaptureAppearancePreset preset, bool isDark)
    {
        if (preset == QuickCaptureAppearancePreset.Default)
        {
            return GetBrushResourceOrFallback(
                "CardStrokeColorDefaultBrush",
                isDark
                    ? ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x1F, 0x00, 0x00, 0x00));
        }

        return new SolidColorBrush(isDark
            ? ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x16, 0x00, 0x00, 0x00));
    }

    private void RefreshItemMaterialSurfaces()
    {
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsListView.ContainerFromItem(item) is DependencyObject container)
            {
                ApplyItemMaterialSurface(container, item);
            }
        }

        if (DetailPage.Visibility == Visibility.Visible)
        {
            ApplyDetailMaterialSurface();
        }
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

    private void QuickCaptureItem_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag || !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsGlyphVisible = true;
        SetItemHoverState(sender as DependencyObject, true);
    }

    private void QuickCaptureItem_DragLeave(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetItemHoverState(sender as DependencyObject, false);
        }
    }

    private async void QuickCaptureItem_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag ||
            !DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            sender is not FrameworkElement { DataContext: QuickCaptureItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        SetItemHoverState(sender as DependencyObject, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            QuickCaptureItemViewModel? updated = await ViewModel.AddAttachmentsAsync(item, batch.Files);
            e.AcceptedOperation = updated is null
                ? DataPackageOperation.None
                : DataPackageOperation.Copy;
            if (updated is not null)
            {
                ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapture] Failed to attach dropped files: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
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

    private void ShowQuickCaptureDeleteSelectedConfirmFlyout(
        IReadOnlyList<string> selectedIds,
        bool isRecent,
        FrameworkElement anchor)
    {
        if (selectedIds.Count == 0)
        {
            return;
        }

        ShowConfirmMenu(
            anchor,
            _localizationService.Format("QuickCapture.DeleteSelectedConfirm.Title", selectedIds.Count),
            _localizationService.T("Common.Delete"),
            async () =>
            {
                ClearQuickCaptureCopySelection();
                var deletedItems = await ViewModel.DeleteItemsAsync(selectedIds, isRecent);
                if (deletedItems.Count > 0)
                {
                    ShowStatusToast(_localizationService.Format("QuickCapture.DeletedCount", deletedItems.Count));
                }
            });
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

    private bool TryPrepareQuickCaptureDragPackage(DataPackage dataPackage, QuickCaptureItemViewModel item)
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
            string imagePath = item.ImagePath;
            Uri? imageUri = TryCreateImageUri(imagePath);
            if (imageUri is not null)
            {
                dataPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromUri(imageUri));
            }

            dataPackage.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    request.SetData(new List<IStorageItem> { file });
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCaptureWidget] Failed to provide dragged image: {ex}");
                }
                finally
                {
                    deferral.Complete();
                }
            });
            if (!string.IsNullOrWhiteSpace(item.Body) &&
                !string.Equals(item.Body, "Image", StringComparison.Ordinal))
            {
                DeskBoxDragData.SetText(dataPackage, item.Body, DeskBoxDragData.SourceQuickCapture);
            }

            dataPackage.Properties.Title = Path.GetFileName(imagePath);
            return true;
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
        bool startRenameWhenClosed = false;
        renameItem.Click += (_, _) => startRenameWhenClosed = true;
        flyout.Closed += (_, _) =>
        {
            if (startRenameWhenClosed)
            {
                DispatcherQueue.TryEnqueue(StartTitleRename);
            }
        };
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

        // Turning off a feature widget preserves its content, configuration, and position.
        flyout.Items.Add(new MenuFlyoutSeparator());
        var disableWidget = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.FeatureWidget.Disable"),
            Icon = new FontIcon { Glyph = "\uE7E8" }
        };
        disableWidget.Click += async (_, _) =>
        {
            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.QuickCapture, enabled: false, reveal: false);
            }
        };
        flyout.Items.Add(disableWidget);

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
        var copyItem = CreateQuickCaptureContextCommand("Common.Copy", "\uE8C8");
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopyItemWithFeedbackAsync(item);
        };
        flyout.Items.Add(copyItem);

        if (item.IsRecent)
        {
            var saveItem = CreateQuickCaptureContextCommand("QuickCapture.SaveToRecords", "\uE74E");
            saveItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.SaveRecentItemAsync(item);
            };
            flyout.Items.Add(saveItem);

            var pinRecentItem = CreateQuickCaptureContextCommand("QuickCapture.PinToRecords", "\uE718");
            pinRecentItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await ViewModel.PinRecentItemAsync(item);
            };
            flyout.Items.Add(pinRecentItem);

            var deleteRecentItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
            deleteRecentItem.Click += (_, _) =>
            {
                flyout.Hide();
                DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteConfirmFlyout(item, anchor));
            };
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteRecentItem);
            return flyout;
        }

        var editItem = CreateQuickCaptureContextCommand("QuickCapture.Edit", "\uE70F");
        editItem.Click += (_, _) =>
        {
            flyout.Hide();
            OpenDetail(item);
        };
        flyout.Items.Add(editItem);

        var pinItem = new MenuFlyoutItem
        {
            Text = item.IsPinned ? _localizationService.T("QuickCapture.Unpin") : _localizationService.T("QuickCapture.Pin"),
            Icon = new FontIcon { Glyph = item.IsPinned ? "\uE840" : "\uE718" }
        };
        pinItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ViewModel.TogglePinnedAsync(item);
        };
        flyout.Items.Add(pinItem);

        var deleteItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteConfirmFlyout(item, anchor));
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (item.Type != QuickCaptureItemType.Image)
        {
            var notepadItem = CreateQuickCaptureContextCommand("QuickCapture.EditInNotepad", "\uE70F");
            notepadItem.Click += async (_, _) =>
            {
                flyout.Hide();
                await OpenTextInNotepadAsync(item);
            };
            flyout.Items.Add(notepadItem);
        }

        flyout.Items.Add(CreateAppearanceFlyout(item, flyout));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);
        return flyout;
    }

    private MenuFlyout CreateMultiItemFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems,
        FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        string[] selectedIds = selectedItems.Select(item => item.Id).ToArray();
        bool isRecent = selectedItems.All(item => item.IsRecent);

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("QuickCapture.CopySelected", selectedItems.Count),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await CopySelectedQuickCaptureItemsAsync(selectedItems);
        };
        flyout.Items.Add(copyItem);

        if (!isRecent)
        {
            bool shouldPin = !selectedItems.All(item => item.IsPinned);
            var pinItem = new MenuFlyoutItem
            {
                Text = _localizationService.T(shouldPin ? "QuickCapture.Pin" : "QuickCapture.Unpin"),
                Icon = new FontIcon { Glyph = shouldPin ? "\uE718" : "\uE840" }
            };
            pinItem.Click += async (_, _) =>
            {
                flyout.Hide();
                ClearQuickCaptureCopySelection();
                await ViewModel.SetPinnedAsync(selectedIds, shouldPin);
                if (shouldPin)
                {
                    ShowStatusToast(_localizationService.T("QuickCapture.PinnedSuccess"));
                }
            };
            flyout.Items.Add(pinItem);
        }

        var deleteItem = CreateQuickCaptureContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += (_, _) =>
        {
            flyout.Hide();
            DispatcherQueue.TryEnqueue(() => ShowQuickCaptureDeleteSelectedConfirmFlyout(
                selectedIds,
                isRecent,
                anchor));
        };
        if (!isRecent)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(CreateBatchAppearanceFlyout(selectedItems, selectedIds, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateQuickCaptureContextCommand(string localizationKey, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = _localizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private MenuFlyoutSubItem CreateAppearanceFlyout(QuickCaptureItemViewModel item, MenuFlyout owner)
    {
        var appearanceMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };

        foreach (var (preset, textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var menuItem = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(textKey),
                IsChecked = item.AppearancePreset == preset
            };
            menuItem.Click += async (_, _) =>
            {
                owner.Hide();
                await ViewModel.SetAppearanceAsync(item, preset);
                DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            };
            appearanceMenu.Items.Add(menuItem);
        }

        return appearanceMenu;
    }

    private MenuFlyoutSubItem CreateBatchAppearanceFlyout(
        IReadOnlyList<QuickCaptureItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        MenuFlyout owner)
    {
        var appearanceMenu = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("QuickCapture.Detail.Appearance"),
            Icon = new FontIcon { Glyph = "\uE790" }
        };
        foreach (var (preset, textKey) in new[]
        {
            (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
            (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
            (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
            (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
            (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
            (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
        })
        {
            var menuItem = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(textKey),
                IsChecked = selectedItems.All(item => item.AppearancePreset == preset)
            };
            menuItem.Click += async (_, _) =>
            {
                owner.Hide();
                ClearQuickCaptureCopySelection();
                await ViewModel.SetAppearanceAsync(selectedIds, preset);
                DispatcherQueue.TryEnqueue(RefreshItemMaterialSurfaces);
            };
            appearanceMenu.Items.Add(menuItem);
        }

        return appearanceMenu;
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

    private void CloseInlineEdit(bool restoreInputFocus = true)
    {
        _editingItem = null;
        _isExpandingInput = false;
        QuickCaptureInlineEditor.Visibility = Visibility.Collapsed;
        QuickCaptureInlineEditor.Text = string.Empty;
        QuickCaptureInlineEditor.Title = _localizationService.T("QuickCapture.Edit");
        if (restoreInputFocus)
        {
            InputTextBox.Focus(FocusState.Programmatic);
        }
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
        return QuickCaptureClipboardFormatter.FormatBatch(selectedItems, _localizationService);
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        Clipboard.Flush();
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

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (DetailPage.Visibility == Visibility.Visible)
            {
                await SaveAndCloseDetailAsync();
                e.Handled = true;
                return;
            }

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

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_isInternalQuickCaptureDrag)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count == 0)
                {
                    string? fallbackText = await TryReadDroppedTextAsync(e.DataView);
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        await ViewModel.AddTextAsync(fallbackText);
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
                        return;
                    }

                    if (batch.SkippedCount > 0)
                    {
                        ShowStatusToast(_localizationService.T("QuickCapture.DroppedAllSkipped"));
                    }
                    e.AcceptedOperation = DataPackageOperation.None;
                    return;
                }

                QuickCaptureItemViewModel? imported;
                if (DetailPage.Visibility == Visibility.Visible && _detailItem is not null)
                {
                    imported = await ViewModel.AddAttachmentsAsync(_detailItem, batch.Files);
                    if (imported is not null)
                    {
                        _detailItem = imported;
                        RefreshDetailAttachmentList();
                    }
                }
                else
                {
                    imported = await ViewModel.AddItemWithAttachmentsAsync(batch.Files);
                    if (imported is not null &&
                        DetailPage.Visibility == Visibility.Visible &&
                        _isCreatingDetail)
                    {
                        _detailItem = imported;
                        _isCreatingDetail = false;
                        _pendingDetailAttachments = [];
                        RefreshDetailAttachmentList();
                    }
                }

                e.AcceptedOperation = imported is null
                    ? DataPackageOperation.None
                    : DataPackageOperation.Copy;
                if (imported is not null)
                {
                    ShowStatusToast(batch.SkippedCount > 0
                        ? _localizationService.T("QuickCapture.DroppedWithSkipped")
                        : _localizationService.T("QuickCapture.Dropped"));
                }
                return;
            }

            string? text = await TryReadDroppedTextAsync(e.DataView);
            if (string.IsNullOrWhiteSpace(text))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            await ViewModel.AddTextAsync(text);
            ViewModel.RefreshAfterViewReady();
            e.AcceptedOperation = DataPackageOperation.Copy;
            ShowStatusToast(_localizationService.T("QuickCapture.Dropped"));
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

    private static async Task<string?> TryReadDroppedTextAsync(DataPackageView dataView)
    {
        string? internalText = await DeskBoxDragData.TryGetInternalTextAsync(dataView);
        if (!string.IsNullOrWhiteSpace(internalText))
        {
            return internalText.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
                ? internalText
                : null;
        }

        if (dataView.Contains(StandardDataFormats.WebLink))
        {
            var link = await dataView.GetWebLinkAsync();
            if (!string.IsNullOrWhiteSpace(link?.AbsoluteUri))
            {
                return link.AbsoluteUri;
            }
        }

        if (dataView.Contains(StandardDataFormats.Text))
        {
            string text = await dataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
                    ? text
                    : null;
            }
        }

        return null;
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool isLeftButtonPressed = e.GetCurrentPoint(TitleBarGrid).Properties.IsLeftButtonPressed;
        if (isLeftButtonPressed &&
            QuickCaptureShell.TitleEditorContent is TextBox &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitTitleRenameAsync();
            e.Handled = true;
            return;
        }

        if (isLeftButtonPressed && ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(_hWnd);
        }

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

        _focusRootAfterDragClick = focusWhenClicked;
        BeginWindowDragCore(e, captureElement);
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerPressedCore(sender, e);
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerMovedCore(sender, e);
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerReleasedCore(sender, e);
    }

    private void ResizeBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerCaptureLostCore(sender, e);
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
            App.LogVerbose($"[ZOrder] QuickCapture PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] QuickCapture PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
        StartTopMostSafetyTimer();
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

    protected override void UpdateConfigBoundsFromPhysical(int x, int y, int width, int height, bool persist)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
        if (persist)
        {
            _settingsService.UpdateWidget(ViewModel.Config, notifySubscribers: false);
        }
    }

    protected override Windows.UI.Color BuildNativeBackdropTintColor(bool isDark)
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

    protected override void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _settingsService.Settings.WidgetMaterialType;
        string borderStyle = _settingsService.Settings.WidgetBorderStyle;

        // Simplified layering: only apply surface color overlay for Solid mode.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var surfaceColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, surfaceColor);
        }
        else
        {
            BackgroundPlate.Background = GetOrUpdateSolidColorBrush(BackgroundPlate.Background, Colors.Transparent);
        }

        var (borderThickness, borderAlpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            _ => (0.8d, (byte)0x18),
        };

        var borderColor = isDark
            ? ColorHelper.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), borderAlpha);
        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(borderAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(borderAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);
        var iconForeground = ColorHelper.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);
        var secondaryForeground = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.BorderThickness = new Thickness(borderThickness);
        BackgroundPlate.BorderBrush = GetOrUpdateSolidColorBrush(BackgroundPlate.BorderBrush, borderColor);
        BackgroundPlate.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());
        HeaderDivider.Background = GetOrUpdateSolidColorBrush(HeaderDivider.Background, dividerColor);
        QuickCaptureShell.TitleIconAccentColor = iconForeground;
        QuickCaptureShell.TitleIconKind = WidgetTitleIconKindNames.QuickCapture;
        QuickCaptureShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        EmptyStateIcon.Foreground = GetOrUpdateSolidColorBrush(EmptyStateIcon.Foreground, secondaryForeground);
        SelectionRectangle.Background = GetOrUpdateSolidColorBrush(
            SelectionRectangle.Background,
            WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = GetOrUpdateSolidColorBrush(
            SelectionRectangle.BorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        ApplySearchVisualStyle(isDark, accentColor);
        ApplyEditOverlayStyle(isDark, accentColor);
        RefreshSelectedViewSegment();
        RefreshItemMaterialSurfaces();
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

        SearchTextBox.Background = GetOrUpdateSolidColorBrush(
            SearchTextBox.Background,
            WithAlpha(background, isDark ? (byte)0xE8 : (byte)0xF6));
        SearchTextBox.BorderBrush = GetOrUpdateSolidColorBrush(
            SearchTextBox.BorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xCC : (byte)0xAA));
    }

    private void ApplyEditOverlayStyle(bool isDark, Windows.UI.Color accentColor)
    {
        QuickCaptureInlineEditor.OverlaySurface.Background = GetOrUpdateSolidColorBrush(
            QuickCaptureInlineEditor.OverlaySurface.Background,
            GetNeutralOverlaySurfaceColor(isDark));
        QuickCaptureInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        QuickCaptureInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        QuickCaptureInlineEditor.Translation = new Vector3(0, 0, 16);

        EditTextBox.Background = GetOrUpdateSolidColorBrush(
            EditTextBox.Background,
            GetNeutralInputSurfaceColor(isDark));
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

        // Skip the fade animation on the very first data load.
        // The Composition Visual may not be fully ready, and if the
        // animation fails the opacity stays at 0, making items invisible.
        if (!_hasPlayedInitialItemsTransition)
        {
            _hasPlayedInitialItemsTransition = true;
            ResetItemsViewTransitionState();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ItemsListView.UpdateLayout();
            EmptyStateHost.UpdateLayout();

            if (ItemsListView.Visibility == Visibility.Visible)
            {
                StartSubtleOffsetAnimation(ItemsListView, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(ItemsListView, 0, 1, ItemsViewTransitionMs);
                ScheduleTransitionSafetyFallback(ItemsListView);
            }

            if (EmptyStateHost.Visibility == Visibility.Visible)
            {
                StartSubtleOffsetAnimation(EmptyStateHost, 0, 0, ItemsViewTransitionOffsetPx, 0, ItemsViewTransitionMs);
                StartOpacityAnimation(EmptyStateHost, 0, 1, ItemsViewTransitionMs);
                ScheduleTransitionSafetyFallback(EmptyStateHost);
            }
        });
    }

    private void ScheduleTransitionSafetyFallback(UIElement element)
    {
        // Safety fallback: if the Composition animation fails to start or
        // complete (e.g., the Visual isn't fully composed yet), ensure the
        // element is still visible after the expected animation duration.
        DispatcherQueue.TryEnqueue(() =>
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ItemsViewTransitionMs + 80);
            timer.IsRepeating = false;
            timer.Tick += OnTransitionSafetyFallbackTick;

            void OnTransitionSafetyFallbackTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
            {
                sender.Stop();
                sender.Tick -= OnTransitionSafetyFallbackTick;
                if (element.Visibility == Visibility.Visible)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    if (visual.Opacity < 0.99f)
                    {
                        visual.StopAnimation("Opacity");
                        visual.Opacity = 1f;
                    }
                    element.Opacity = 1;
                }
            }
            timer.Start();
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
