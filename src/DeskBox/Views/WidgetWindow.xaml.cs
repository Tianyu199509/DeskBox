using System.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class WidgetWindow : WidgetWindowBase, IDesktopWidgetWindow
{
    private enum ItemSurfaceState
    {
        Normal,
        Hover,
        Pressed,
        DropTarget
    }

    private sealed record SelectionHitTestItem(
        WidgetItem Item,
        Border? Surface,
        Windows.Foundation.Rect Bounds);

    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private const int ItemTransitionRestoreDelayMs = 240;
    private const string DeskBoxInternalDragToken = "DeskBox.WidgetItemDrag.v2";
    private static readonly UIntPtr FileDropSubclassId = new(0xDDB0);

    // ── Backward-compatible aliases for base class fields ──────
    // These allow the existing WidgetWindow code to reference inherited
    // protected fields by their original private names without a
    // massive find-and-replace across 5000+ lines.
    private SettingsService _settingsService => SettingsService;
    private LocalizationService _localizationService => _localizationSvc;
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

    private readonly Microsoft.UI.WindowId _windowId;
    private readonly LocalizationService _localizationSvc;
    private readonly Win32Helper.SubclassProc _fileDropSubclassProc;
    private readonly WidgetContentDescriptor _chromeDescriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private bool _isNativeBackdropSuppressedForTrayReveal;

    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private DispatcherQueueTimer? _statusToastTimer;
    private bool _emptyStateUpdateQueued;
    private bool _deletePending;
    private string[] _cutClipboardPaths = [];
    private string[] _activeDragSourcePaths = [];
    private bool _activeDragHasStorageItems;
    private string? _lastRootDragDiagnosticSignature;
    private string? _lastFolderDragDiagnosticSignature;
    private Border? _folderDropTarget;
    private bool _surfaceDragCompletionHandled;
    private int _lastSelectionAnchorIndex = -1;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private HashSet<WidgetItem> _selectionPreviewItems = [];
    private List<SelectionHitTestItem> _selectionHitTestItems = [];
    private Dictionary<WidgetItem, Border> _selectionSurfaceByItem = [];
    private readonly HashSet<Border> _interactiveSurfaces = [];
    private bool _isSynchronizingSelection;
    private MenuFlyout? _itemDeleteConfirmFlyout;
    private Flyout? _messageFlyout;
    private WidgetItem? _itemRenameTarget;
    private MenuFlyout? _deleteWidgetFlyout;
    private FrameworkElement? _lastMoreFlyoutTarget;
    private Windows.Foundation.Point? _lastMoreFlyoutPosition;
    private bool _isDeleteWidgetFlyoutOpen;
    private Windows.Foundation.Point _lastRightTapPoint;
    private TextBlock? _itemRenameNameText;
    private bool _isInlineFlyoutOpen;
    private bool _isCommittingTitleRename;
    private bool _isCommittingItemRename;
    private bool _isCancellingTitleRename;
    private bool _isCancellingItemRename;
    private DateTime _lastTitleBarClickTimeUtc;
    private Win32Helper.POINT _lastTitleBarClickPoint;
    private bool _hasPendingTitleBarClick;
    private bool _isAtDesktopLayer { get => IsAtDesktopLayer; set => IsAtDesktopLayer = value; }
    private bool _keepRaisedUntilDeactivate { get => KeepRaisedUntilDeactivate; set => KeepRaisedUntilDeactivate = value; }
    private bool _restoreDesktopLayerWhenIdle { get => RestoreDesktopLayerWhenIdle; set => RestoreDesktopLayerWhenIdle = value; }
    private bool _isHideAnimationRunning { get => IsHideAnimationRunning; set => IsHideAnimationRunning = value; }
    private bool _isMigrationBusy;
    private long _backdropRefreshGeneration { get => BackdropRefreshGeneration; set => BackdropRefreshGeneration = value; }
    private bool _areItemTransitionsSuppressed;
    private DispatcherQueueTimer? _autoRestoreTimer;
    private DispatcherQueueTimer? _topMostSafetyTimer { get => TopMostSafetyTimer; set => TopMostSafetyTimer = value; }
    private WidgetDisplayChangeWatcher? _displayChangeWatcher { get => DisplayChangeWatcher; set => DisplayChangeWatcher = value; }
    private bool _isFileDropSubclassInstalled;
    private TransitionCollection? _savedGridItemTransitions;
    private TransitionCollection? _savedListItemTransitions;
    private SolidColorBrush? _normalItemSurfaceBrush;
    private SolidColorBrush? _selectedItemSurfaceBrush;
    private SolidColorBrush? _hoverItemSurfaceBrush;
    private SolidColorBrush? _pressedItemSurfaceBrush;
    private SolidColorBrush? _selectedHoverItemSurfaceBrush;
    private SolidColorBrush? _dropTargetItemSurfaceBrush;
    private SolidColorBrush? _normalItemBorderBrush;
    private SolidColorBrush? _dropTargetItemBorderBrush;
    private bool? _itemSurfaceBrushesAreDark;
    private Windows.UI.Color? _itemSurfaceBrushesAccentColor;

    private Border BackgroundPlate => FileWidgetShell.BackgroundSurface;
    private Border HeaderDivider => FileWidgetShell.Divider;

    public WidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => HWnd;

    public WidgetWindowIdentity Identity => Diagnostics.Identity;

    public override WidgetConfig Config => ViewModel.Config;

    public Windows.Foundation.Rect AnimationBounds => Diagnostics.AnimationBounds;

    // ── WidgetWindowBase abstract overrides ────────────────────
    protected override double WidgetOpacity => ViewModel.WidgetOpacity;
    protected override FrameworkElement RootElement => RootGrid;
    protected override string LogPrefix => "Widget";
    protected override bool IsSizeLocked => ViewModel.IsSizeLocked;
    protected override bool IsPositionLocked => ViewModel.IsPositionLocked;
    protected override bool IsBackdropSuppressedForTrayReveal => _isNativeBackdropSuppressedForTrayReveal;

    protected override void OnElevated()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    protected override bool HasBlockingFlyoutOpen()
    {
        return TitleEditBox.Visibility == Visibility.Visible ||
               _deletePending ||
               _isDeleteWidgetFlyoutOpen ||
               _isInlineFlyoutOpen;
    }

    protected override void ConfigureWindowExtra()
    {
        Win32Helper.AllowShellDragDropMessages(HWnd);
        InstallFileDropSubclass();
    }

    protected override void OnRootElementLoaded()
    {
        var parent = VisualTreeHelper.GetParent(RootGrid) as FrameworkElement;
        while (parent is not null)
        {
            if (parent is Control control)
            {
                control.Background = new SolidColorBrush(Colors.Transparent);
            }
            else if (parent is Panel panel)
            {
                panel.Background = new SolidColorBrush(Colors.Transparent);
            }
            else if (parent is Border border)
            {
                border.Background = new SolidColorBrush(Colors.Transparent);
            }
            else if (parent is ContentPresenter presenter)
            {
                presenter.Background = new SolidColorBrush(Colors.Transparent);
            }

            parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
        }
        RootGrid.Focus(FocusState.Programmatic);
    }

    private bool _isVisibleOnDesktop;
    private bool _isClosing { get => IsClosing; set => IsClosing = value; }
    private DateTime _lastElevateForInteractionUtc { get => LastElevateForInteractionUtc; set => LastElevateForInteractionUtc = value; }
    public new bool Visible
    {
        get => _isVisibleOnDesktop;
        private set => _isVisibleOnDesktop = value;
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

    public WidgetWindow(WidgetViewModel viewModel, SettingsService settingsService, LocalizationService? localizationService = null)
    {
        ViewModel = viewModel;
        SettingsService = settingsService;
        _localizationSvc = localizationService ?? new LocalizationService(settingsService);
        _fileDropSubclassProc = FileDropSubclassProc;
        _chromeDescriptor = new WidgetContentFactory(_localizationSvc).GetDescriptor(WidgetKind.File);
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        ApplyLocalizedText();
        FileWidgetShell.SetDividerMargin(new Thickness(12, 0, 12, 0));

        HWnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(HWnd);
        AppWindow = AppWindow.GetFromWindowId(_windowId);
        Diagnostics = new WidgetWindowDiagnostics("File", ViewModel.Config, () => HWnd);
        TrayAnimation = new WidgetTrayAnimationController(
            AppWindow,
            RootGrid,
            DispatcherQueue,
            HWnd,
            () => Diagnostics.AnimationBounds,
            LogTrayWindow);

        ConfigureWindow();
        SetupEventHandlers();

        ViewModel.Items.CollectionChanged += (_, _) => QueueEmptyStateUpdate();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEmptyState();
        ApplyTitleBarLayout();
    }

    private void ConfigureWindow()
    {
        ConfigureWindowCore();
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;

        Activated += WidgetWindow_Activated;

        _appWindow.Changed += AppWindow_Changed;
        _displayChangeWatcher = new WidgetDisplayChangeWatcher(_hWnd, DispatcherQueue, RestoreBoundsAfterDisplayChange);

        foreach (var child in ResizeGrid.Children)
        {
            if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
            {
                element.PointerMoved += ResizeBorder_PointerMoved;
                element.PointerReleased += ResizeBorder_PointerReleased;
            }
        }

        Closed += (_, _) =>
        {
            _isClosing = true;
            Visible = false;
            WidgetLayerService.ReleaseWindow(_hWnd);
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _displayChangeWatcher?.Dispose();
            _displayChangeWatcher = null;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            StopBackdropRefreshTimer();
            RemoveFileDropSubclass();
            _trayAnimation.Stop();
            RestoreItemContainerTransitions();
            DisposeAcrylicController();
            DisposeMicaController();

            foreach (var child in ResizeGrid.Children)
            {
                if (child is FrameworkElement element && element.Tag is string tag && !string.IsNullOrEmpty(tag))
                {
                    element.PointerMoved -= ResizeBorder_PointerMoved;
                    element.PointerReleased -= ResizeBorder_PointerReleased;
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

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: true);
        return true;
    }

    private bool RestoreBoundsAfterDisplayChange()
    {
        // For hidden windows: update config coordinates to the correct screen
        // without actually moving the window. This ensures the widget appears
        // in the right place when it is next shown.
        if (!Visible)
        {
            var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(ViewModel.Config);
            var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
            WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
            _settingsService.SaveDebounced();
            return true;
        }

        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
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

    private void ApplyLocalizedText()
    {
        TitleEditBox.PlaceholderText = _localizationService.T("Widget.TitlePlaceholder");
        ToolTipService.SetToolTip(PositionLockButton, _localizationService.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(SizeLockButton, _localizationService.T("Widget.LockSize"));
        ToolTipService.SetToolTip(FileWidgetShell.PositionLockActionButton, _localizationService.T("Widget.LockPosition"));
        ToolTipService.SetToolTip(FileWidgetShell.SizeLockActionButton, _localizationService.T("Widget.LockSize"));
        ToolTipService.SetToolTip(AddButton, _localizationService.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(MoreButton, _localizationService.T("Widget.Tooltip.More"));
        ToolTipService.SetToolTip(CloseButton, _localizationService.T("Widget.Tooltip.DeleteWidget"));
        MigrationTitleText.Text = _localizationService.T("Widget.Migration.Title");
        MigrationDescriptionText.Text = _localizationService.T("Widget.Migration.Description");
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(_hWnd);
        App.LogVerbose($"[ZOrder] Widget PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    public void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(_hWnd);
        App.LogVerbose($"[ZOrder] Widget ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
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

    public void RaiseTemporarilyFromTray()
    {
        PrepareTrayShowAnimation();
        ShowPreparedRaisedFromTray();
        PlayTrayRaiseAnimationAfterFirstFrame();
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
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-visible hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (_isAtDesktopLayer)
        {
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED atDesktop hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-raised hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        App.LogVerbose($"[ZOrder] Widget EnsureRaisedFromTrayTopMost hwnd=0x{_hWnd.ToInt64():X} atDesktop={_isAtDesktopLayer}");
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        WidgetLayerService.BringToFront(_hWnd);
        HoldTemporaryTopMost();
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

    public void PlayTrayShowAnimation()
    {
        PlayTrayRaiseAnimationAfterFirstFrame();
    }

    public void PlayPreparedTrayHideAnimation()
    {
        if (!_isHideAnimationRunning)
        {
            return;
        }

        PlayTrayHideAnimation(CompleteTrayHideAnimation);
    }

    public void PrepareTrayShowAnimation()
    {
        _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();
        _isHideAnimationRunning = false;

        var animationProfile = GetTrayAnimationProfile();
        LogTrayWindow(
            $"PrepareShow gen={_trayAnimation.Generation} effect={_settingsService.Settings.WidgetAnimationEffect} " +
            $"speed={_settingsService.Settings.WidgetAnimationSpeed} enabled={animationProfile.IsEnabled} durationMs={animationProfile.DurationMs}");
        _trayAnimation.PrepareVisualState(animationProfile.ShowOffsetX, animationProfile.ShowOffsetY, animationProfile.ShowStartOpacity, animationProfile.ShowStartScale);
    }

    public void CompleteTrayShowWithoutAnimation()
    {
        var animationGeneration = _trayAnimation.NextGeneration();
        LogTrayWindow($"CompleteShowWithoutAnimation gen={animationGeneration}");
        _trayAnimation.Stop();
        SetTrayAnimationOffsetOverride(null, null);
        RestoreNativeBackdropAfterTrayReveal();
        _trayAnimation.RestoreVisualState();
        _trayAnimation.RestoreWindowPosition();

        QueueItemContainerTransitionRestore(animationGeneration);
    }

    private void PlayTrayRaiseAnimation()
    {
        var animationGeneration = _trayAnimation.NextGeneration();
        _trayAnimation.Stop();
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();
        _isHideAnimationRunning = false;

        var animationProfile = GetTrayAnimationProfile();
        if (!animationProfile.IsEnabled)
        {
            LogTrayWindow($"PlayShow skipped reason=animation-disabled gen={animationGeneration}");
            CompleteTrayShowWithoutAnimation();
            return;
        }

        LogTrayWindow($"PlayShow gen={animationGeneration} durationMs={animationProfile.DurationMs}");
        _trayAnimation.PrepareVisualState(animationProfile.ShowOffsetX, animationProfile.ShowOffsetY, animationProfile.ShowStartOpacity, animationProfile.ShowStartScale);
        _trayAnimation.Animate(
            animationProfile.ShowOffsetX,
            animationProfile.ShowOffsetY,
            0,
            0,
            animationProfile.ShowStartOpacity,
            WidgetTrayAnimationController.RestingOpacity,
            animationProfile.ShowStartScale,
            WidgetTrayAnimationController.RestingScale,
            animationProfile.DurationMs,
            true,
            animationGeneration,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
        {
            _trayAnimation.RestoreVisualState();
            _trayAnimation.RestoreWindowPosition();
            QueueItemContainerTransitionRestore(animationGeneration);
        });
    }

    private void PlayTrayRaiseAnimationAfterFirstFrame()
    {
        if (Visible)
        {
            PlayTrayRaiseAnimation();
        }
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
        RestoreItemContainerTransitions();
        SuppressItemContainerTransitions();

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

    private void PlayTrayHideAnimation(Action completed)
    {
        var animationGeneration = _trayAnimation.Generation;
        var animationProfile = GetTrayAnimationProfile();
        if (!animationProfile.IsEnabled)
        {
            LogTrayWindow($"PlayHide skipped reason=animation-disabled gen={animationGeneration}");
            completed();
            return;
        }

        LogTrayWindow($"PlayHide gen={animationGeneration} durationMs={animationProfile.DurationMs}");
        _trayAnimation.Animate(
            0,
            0,
            animationProfile.HideOffsetX,
            animationProfile.HideOffsetY,
            WidgetTrayAnimationController.RestingOpacity,
            animationProfile.HideEndOpacity,
            WidgetTrayAnimationController.RestingScale,
            animationProfile.HideEndScale,
            animationProfile.DurationMs,
            false,
            animationGeneration,
            _settingsService.Settings.WidgetAnimationEasingIntensity,
            () =>
        {
            if (Visible)
            {
                return;
            }
            completed();
        });
    }

    private void SuppressItemContainerTransitions()
    {
        if (_areItemTransitionsSuppressed)
        {
            return;
        }

        _savedGridItemTransitions = ItemsGridView.ItemContainerTransitions;
        _savedListItemTransitions = ItemsListView.ItemContainerTransitions;
        ItemsGridView.ItemContainerTransitions = new TransitionCollection();
        ItemsListView.ItemContainerTransitions = new TransitionCollection();
        _areItemTransitionsSuppressed = true;
    }

    private void RestoreItemContainerTransitions()
    {
        if (!_areItemTransitionsSuppressed)
        {
            return;
        }

        ItemsGridView.ItemContainerTransitions = _savedGridItemTransitions;
        ItemsListView.ItemContainerTransitions = _savedListItemTransitions;
        _savedGridItemTransitions = null;
        _savedListItemTransitions = null;
        _areItemTransitionsSuppressed = false;
    }

    private void QueueItemContainerTransitionRestore(long animationGeneration)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(ItemTransitionRestoreDelayMs);
            if (animationGeneration == _trayAnimation.Generation)
            {
                RestoreItemContainerTransitions();
            }
        });
    }

    public void CompleteTrayHideAnimation()
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
        QueueItemContainerTransitionRestore(_trayAnimation.Generation);
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

    private void InstallFileDropSubclass()
    {
        if (_isFileDropSubclassInstalled)
        {
            return;
        }

        _isFileDropSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _fileDropSubclassProc,
            FileDropSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeSubclassInstall " +
            $"hwnd=0x{_hWnd.ToInt64():X} installed={_isFileDropSubclassInstalled}");
    }

    private void RemoveFileDropSubclass()
    {
        if (!_isFileDropSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _fileDropSubclassProc, FileDropSubclassId);
        _isFileDropSubclassInstalled = false;
    }

    private IntPtr FileDropSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == Win32Helper.WM_DROPFILES)
        {
            var paths = Win32Helper.GetDroppedFilePaths((IntPtr)wParam);
            App.Log(
                $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropFiles " +
                $"count={paths.Count} mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} " +
                $"managed={ViewModel.FollowsDefaultStoragePath}");
            if (paths.Count > 0)
            {
                DispatcherQueue.TryEnqueue(async () => await ImportNativeDropPathsAsync(paths));
            }

            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task ImportNativeDropPathsAsync(IReadOnlyList<string> paths)
    {
        if (_isMigrationBusy || paths.Count == 0)
        {
            return;
        }

        try
        {
            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            var acceptedOperation = NormalizePathDropOperation(DataPackageOperation.Copy | DataPackageOperation.Move, movesIntoFolder);
            if (acceptedOperation == DataPackageOperation.None)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=NativeDropRejectedOperation " +
                    $"count={paths.Count} mapped={movesIntoFolder}");
                return;
            }

            bool? moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;
            await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);
            ClearCutState();
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] NativeDropFailed widget='{ViewModel.Name}' id={ViewModel.Config.Id}: {ex}");
        }
    }

    private void LogTrayWindow(string message)
    {
        App.LogVerbose(_diagnostics.FormatTrayWindowMessage(message));
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

        ViewModel.ApplyAppearancePreview();
        ApplyBackdropPreference();
        QueueBackdropRefresh();
    }

    public void SetMigrationBusy(bool isBusy)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SetMigrationBusy(isBusy));
            return;
        }

        _isMigrationBusy = isBusy;
        MigrationOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        MigrationProgressRing.IsActive = isBusy;
        ResizeGrid.IsHitTestVisible = !isBusy;
        RootGrid.Focus(FocusState.Programmatic);
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
            App.LogVerbose($"[ZOrder] Widget HoldTemporaryTopMost skipped pinned hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(_hWnd);
        App.LogVerbose($"[ZOrder] Widget HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
        StartTopMostSafetyTimer();
    }

    private void StartTopMostSafetyTimer()
    {
        if (_topMostSafetyTimer is null)
        {
            _topMostSafetyTimer = DispatcherQueue.CreateTimer();
            _topMostSafetyTimer.IsRepeating = false;
            _topMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            _topMostSafetyTimer.Tick += (_, _) =>
            {
                _topMostSafetyTimer?.Stop();
                if (!_isAtDesktopLayer && !_isDragging && !_isResizing &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    App.Log($"[ZOrder] Widget safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                    RestoreDesktopLayer(force: true);
                }
            };
        }
        else
        {
            _topMostSafetyTimer.Stop();
        }
        _topMostSafetyTimer.Start();
    }

    private void WidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ApplyBackdropPreference());

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] Widget Deactivated→QueueRestore hwnd=0x{_hWnd.ToInt64():X}");
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
            App.LogVerbose($"[ZOrder] Widget PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] Widget PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
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
            if (App.Current.IsDeskBoxWindow(foregroundWindow))
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("file-window-deactivated"))
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

    public void RestoreDesktopLayerFromManager()
    {
        RestoreDesktopLayer(force: true);
    }

    public void ForceRestoreDesktopLayerFromManager()
    {
        App.LogVerbose($"[ZOrder] Widget ForceRestore hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer}");
        ForceCancelTransientState();
        RestoreDesktopLayer(force: true);
    }

    private void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !_restoreDesktopLayerWhenIdle && _keepRaisedUntilDeactivate)
        {
            return;
        }

        if (!force && (
            _isDragging ||
            _isResizing ||
            TitleEditBox.Visibility == Visibility.Visible ||
            _deletePending ||
            _isDeleteWidgetFlyoutOpen ||
            _isInlineFlyoutOpen))
        {
            if (force || _restoreDesktopLayerWhenIdle)
            {
                _restoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        _topMostSafetyTimer?.Stop();
        _topMostSafetyTimer = null;
        _keepRaisedUntilDeactivate = false;
        _restoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    private void ForceCancelTransientState()
    {
        _restoreDesktopLayerWhenIdle = true;
        _keepRaisedUntilDeactivate = false;
        _isDeleteWidgetFlyoutOpen = false;
        _isInlineFlyoutOpen = false;
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

    private void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        ViewModel.Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
    }

    protected override void UpdateConfigBoundsFromPhysical(int x, int y, int width, int height, bool persist)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(ViewModel.Config, bounds, workArea);
        if (persist)
        {
            SettingsService.UpdateWidget(ViewModel.Config, notifySubscribers: false);
        }
    }

    private void OnSettingsChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyWindowCornerPreference();
            ApplyBackdropPreference();
            QueueBackdropRefresh();
            UpdateInteractiveSurfaces();
            ApplyTitleBarLayout();
        });
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
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);
        string materialType = _settingsService.Settings.WidgetMaterialType;

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

            // Apply DWM border color based on border style setting
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (materialType is SettingsService.WidgetMaterialTypeMica)
            {
                controllerApplied = ApplyMicaController(isDark, tintColor, surfaceOpacity);
            }

            if (!controllerApplied && materialType is SettingsService.WidgetMaterialTypeAcrylic)
            {
                controllerApplied = ApplyAcrylicController(isDark, tintColor, surfaceOpacity);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                // Solid: no system backdrop, no accent blur
                DisposeAcrylicController();
                DisposeMicaController();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(_hWnd);
            }
            else
            {
                // Fallback to Win32 accent blur
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(_hWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DisposeAcrylicController();
                DisposeMicaController();
                Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{_hWnd.ToInt64():X} material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={_acrylicController is not null} micaController={_micaController is not null}");
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    private double GetCornerRadiusFromPreference()
    {
        // Match Windows 11 DWM corner radius values exactly.
        return _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    private void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        // Setting a DWM border color causes a double-border effect with mismatched
        // corner radii (DWM corner vs XAML corner), producing pixel overflow at corners.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(_hWnd, borderNone);
    }

    private bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        _backdropConfiguration ??= new SystemBackdropConfiguration();
        _backdropConfiguration.IsInputActive = true;
        _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (_micaController is null)
        {
            // Dispose acrylic FIRST before adding mica to avoid backdrop conflicts
            DisposeAcrylicController();
            _micaController = new MicaController { Kind = MicaKind.Base };

            if (!_micaController.AddSystemBackdropTarget(_backdropTarget))
            {
                DisposeMicaController();
                return false;
            }
        }
        else
        {
            // Already have a mica controller — just make sure acrylic is gone
            DisposeAcrylicController();
        }

        _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
        _micaController.Kind = MicaKind.Base;
        _micaController.TintColor = tintColor;
        _micaController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _micaController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.5, 0.0, 1.0);
        return true;
    }

    private void DisposeMicaController()
    {
        if (_micaController is null)
        {
            return;
        }

        try
        {
            _micaController.RemoveAllSystemBackdropTargets();
            _micaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            _micaController = null;
        }
    }

    // QueueBackdropRefresh, OnBackdropRefreshTick, RefreshBackdropIfCurrent
    // are now inherited from WidgetWindowBase.

    private bool ApplyAcrylicController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
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
            // Dispose mica FIRST before adding acrylic to avoid backdrop conflicts
            DisposeMicaController();
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
        else
        {
            // Already have an acrylic controller — just make sure mica is gone
            DisposeMicaController();
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

    private bool ApplyTransparentAcrylicController(bool isDark)
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
        // Near-zero tint and luminosity for a glass-like see-through effect.
        _acrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        _acrylicController.TintOpacity = 0.0f;
        _acrylicController.LuminosityOpacity = 0.0f;

        DisposeMicaController();
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
        finally
        {
            _acrylicController = null;
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

    private void ApplySurfaceStyle()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(ViewModel.WidgetOpacity, 0.0, 1.0);
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        string materialType = _settingsService.Settings.WidgetMaterialType;
        string borderStyle = _settingsService.Settings.WidgetBorderStyle;

        // Simplified layering: only apply surface color overlay for Solid mode.
        // For Acrylic/Mica/Transparent, BackgroundPlate is transparent to let the system backdrop show through.
        if (materialType is SettingsService.WidgetMaterialTypeSolid)
        {
            var solidColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);
            BackgroundPlate.Background = new SolidColorBrush(solidColor);
        }
        else
        {
            BackgroundPlate.Background = new SolidColorBrush(Colors.Transparent);
        }

        // XAML border based on user setting
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

        var iconForeground = Windows.UI.Color.FromArgb(
            isDark ? (byte)0xE2 : (byte)0xCC,
            accentColor.R,
            accentColor.G,
            accentColor.B);

        var editorBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark
                ? ColorHelper.FromArgb(0xFF, 0x1A, 0x1C, 0x21)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.14 : 0.08,
            overlayMix: isDark ? 0.12 : 0.08);

        var editorBorder = isDark
            ? ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);

        var secondaryText = isDark
            ? ColorHelper.FromArgb(0xD8, 0xC0, 0xC3, 0xC8)
            : ColorHelper.FromArgb(0xD0, 0x62, 0x65, 0x6A);

        BackgroundPlate.BorderThickness = new Thickness(borderThickness);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        BackgroundPlate.CornerRadius = new CornerRadius(GetCornerRadiusFromPreference());
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        FileTitleIcon.AccentColor = iconForeground;
        FileTitleIcon.Mode = _settingsService.Settings.WidgetTitleIconMode;
        FileWidgetShell.TitleIconAccentColor = iconForeground;
        FileWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        TitleEditBox.Background = new SolidColorBrush(editorBackground);
        TitleEditBox.BorderBrush = new SolidColorBrush(editorBorder);
        TitleEditBox.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
        EmptyStateTitleText.Foreground = new SolidColorBrush(isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        EmptyStateDescriptionText.Foreground = new SolidColorBrush(secondaryText);
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryText);
        SelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        StatusToastText.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);

        ResetItemSurfaceBrushCache();
        UpdateInteractiveSurfaces();
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

    private void UpdateInteractiveSurfaces()
    {
        foreach (var border in _interactiveSurfaces.ToArray())
        {
            if (border.XamlRoot is null)
            {
                _interactiveSurfaces.Remove(border);
                continue;
            }

            ApplyWidgetItemLayout(border);
            ApplyWidgetItemTooltip(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void ApplyWidgetItemTooltip(Border border)
    {
        if (!ViewModel.ShowFileItemPathTooltips || border.DataContext is not WidgetItem item)
        {
            ToolTipService.SetToolTip(border, null);
            return;
        }

        var tooltipText = new TextBlock
        {
            Text = item.FullPath
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipTextStyle", out object? textStyleResource)
            && textStyleResource is Style textStyle)
        {
            tooltipText.Style = textStyle;
        }

        var tooltipContent = new Border
        {
            Child = tooltipText
        };

        if (Application.Current.Resources.TryGetValue("WidgetTooltipCardStyle", out object? cardStyleResource)
            && cardStyleResource is Style cardStyle)
        {
            tooltipContent.Style = cardStyle;
        }

        var tooltip = new ToolTip
        {
            Content = tooltipContent
        };

        if (Application.Current.Resources.TryGetValue("RoundedToolTipStyle", out object? tooltipStyleResource)
            && tooltipStyleResource is Style tooltipStyle)
        {
            tooltip.Style = tooltipStyle;
        }

        ToolTipService.SetToolTip(border, tooltip);
    }

    private void ApplyTitleBarLayout()
    {
        double listIcon = ViewModel.ListIconSize;
        double labelFont = ViewModel.ListLabelFontSize;
        var chromeMode = _chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor);
        double titleTextSize = chromeMode == WidgetChromeMode.Compact
            ? labelFont
            : labelFont * 1.27;

        var metrics = WidgetTitleBarMetricsCalculator.Create(
            listIcon * 0.54,
            titleTextSize,
            includeInnerPadding: false,
            chromeMode);

        FileWidgetShell.ChromeMode = chromeMode;
        FileWidgetShell.ShowHoverButtons = _settingsService.Settings.ShowHoverButtons;
        FileWidgetShell.TitleGlyph = ViewModel.IconGlyph;
        FileWidgetShell.TitleIconKind = ViewModel.TitleIconKind;
        FileWidgetShell.TitleIconMode = _settingsService.Settings.WidgetTitleIconMode;
        FileWidgetShell.TitleIconElement.LabelText = ViewModel.Name;
        FileWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        FileWidgetShell.TitleBarContent = chromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden
            ? null
            : TitleBarGrid;
        ApplyLegacyTitleActionButtonVisibility(chromeMode);
        ApplyLockActionIconState();

        FileTitleIcon.IconSize = metrics.TitleIconSize;
        FileTitleIcon.Glyph = ViewModel.IconGlyph;
        FileTitleIcon.IconKind = ViewModel.TitleIconKind;
        FileTitleIcon.LabelText = ViewModel.Name;
        FileTitleIcon.Mode = _settingsService.Settings.WidgetTitleIconMode;
        TitleText.FontSize = metrics.TitleTextSize;
        TitleEditBox.FontSize = Math.Max(metrics.TitleTextSize - 1, 11);

        ApplyTitleActionButtonConfiguration(chromeMode);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.PositionLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.SizeLockActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.AddActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.MoreActionButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(FileWidgetShell.CloseActionButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(PositionLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            metrics);
        WidgetActionIconHelper.ApplyPairSize(
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.AddActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.MoreActionIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(FileWidgetShell.CloseActionIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        FileWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
    }

    private void ApplyTitleActionButtonConfiguration(WidgetChromeMode chromeMode)
    {
        var actions = SettingsService.ParseWidgetHoverButtonActions(_settingsService.Settings.WidgetHoverButtonActions);
        bool showPositionLock = actions.Contains(SettingsService.WidgetHoverActionLockPosition);
        bool showSizeLock = actions.Contains(SettingsService.WidgetHoverActionLockSize);
        bool showAdd = actions.Contains(SettingsService.WidgetHoverActionAdd) &&
            ViewModel.TopAddButtonVisibility == Visibility.Visible;
        bool showMore = actions.Contains(SettingsService.WidgetHoverActionMore);
        bool showDelete = actions.Contains(SettingsService.WidgetHoverActionDelete);

        PositionLockButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        SizeLockButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = showAdd ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;

        FileWidgetShell.PositionLockActionButton.Visibility = showPositionLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.SizeLockActionButton.Visibility = showSizeLock ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.ShowAddButton = showAdd;
        FileWidgetShell.MoreActionButton.Visibility = showMore ? Visibility.Visible : Visibility.Collapsed;
        FileWidgetShell.CloseActionButton.Visibility = showDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLockActionIconState()
    {
        WidgetActionIconHelper.ApplyLockState(
            PositionLockButtonIcon,
            PositionLockButtonFilledIcon,
            ViewModel.IsPositionLocked,
            SizeLockButtonIcon,
            SizeLockButtonFilledIcon,
            ViewModel.IsSizeLocked);
        WidgetActionIconHelper.ApplyLockState(
            FileWidgetShell.PositionLockActionIcon,
            FileWidgetShell.PositionLockFilledActionIcon,
            ViewModel.IsPositionLocked,
            FileWidgetShell.SizeLockActionIcon,
            FileWidgetShell.SizeLockFilledActionIcon,
            ViewModel.IsSizeLocked);
    }

    private void ApplyLegacyTitleActionButtonVisibility(WidgetChromeMode chromeMode)
    {
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Stop();

        bool showButtons = _settingsService.Settings.ShowHoverButtons &&
            chromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden);
        RightActionButtons.Opacity = showButtons ? 1 : 0;
        RightActionButtons.IsHitTestVisible = showButtons;
        RightButtonsTransform.X = showButtons ? 0 : 12;
    }

    private IEnumerable<Border> FindInteractiveSurfaceBorders(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "InteractiveSurface")
            {
                yield return border;
            }

            foreach (var nested in FindInteractiveSurfaceBorders(child))
            {
                yield return nested;
            }
        }
    }

    private void ApplyWidgetItemSurfaceState(Border border, ItemSurfaceState state)
    {
        if (ReferenceEquals(border, _folderDropTarget) && state != ItemSurfaceState.DropTarget)
        {
            state = ItemSurfaceState.DropTarget;
        }

        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var item = border.DataContext as WidgetItem;
        bool isSelected = item?.IsSelected == true;
        bool isCut = item?.IsCut == true;

        EnsureItemSurfaceBrushCache(isDark, accentColor);

        border.Background = state switch
        {
            ItemSurfaceState.DropTarget => _dropTargetItemSurfaceBrush,
            ItemSurfaceState.Hover when isSelected => _selectedHoverItemSurfaceBrush,
            ItemSurfaceState.Pressed when isSelected => _selectedHoverItemSurfaceBrush,
            ItemSurfaceState.Hover => _hoverItemSurfaceBrush,
            ItemSurfaceState.Pressed => _pressedItemSurfaceBrush,
            _ when isSelected => _selectedItemSurfaceBrush,
            _ => _normalItemSurfaceBrush
        };
        border.BorderBrush = state == ItemSurfaceState.DropTarget
            ? _dropTargetItemBorderBrush
            : _normalItemBorderBrush;
        border.BorderThickness = state == ItemSurfaceState.DropTarget
            ? new Thickness(1)
            : new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void ResetItemSurfaceBrushCache()
    {
        _normalItemSurfaceBrush = null;
        _selectedItemSurfaceBrush = null;
        _hoverItemSurfaceBrush = null;
        _pressedItemSurfaceBrush = null;
        _selectedHoverItemSurfaceBrush = null;
        _dropTargetItemSurfaceBrush = null;
        _normalItemBorderBrush = null;
        _dropTargetItemBorderBrush = null;
        _itemSurfaceBrushesAreDark = null;
        _itemSurfaceBrushesAccentColor = null;
    }

    private void EnsureItemSurfaceBrushCache(bool isDark, Windows.UI.Color accentColor)
    {
        if (_normalItemSurfaceBrush is not null &&
            _itemSurfaceBrushesAreDark == isDark &&
            _itemSurfaceBrushesAccentColor is { } cachedAccentColor &&
            cachedAccentColor.Equals(accentColor))
        {
            return;
        }

        _itemSurfaceBrushesAreDark = isDark;
        _itemSurfaceBrushesAccentColor = accentColor;

        var defaultBackground = ColorHelper.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        var selectedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x31, 0x36, 0x3E)
                    : ColorHelper.FromArgb(0xFF, 0xF1, 0xF6, 0xFC),
                accentMix: isDark ? 0.30 : 0.18,
                overlayMix: isDark ? 0.08 : 0.04),
            isDark ? (byte)0x62 : (byte)0x72);

        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F)
                    : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        var pressedBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x2D, 0x30, 0x37)
                    : ColorHelper.FromArgb(0xFF, 0xF8, 0xF8, 0xFA),
                accentMix: isDark ? 0.24 : 0.15,
                overlayMix: isDark ? 0.10 : 0.16),
            isDark ? (byte)0x48 : (byte)0x54);

        var selectedHoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x34, 0x39, 0x42)
                    : ColorHelper.FromArgb(0xFF, 0xEC, 0xF4, 0xFC),
                accentMix: isDark ? 0.34 : 0.21,
                overlayMix: isDark ? 0.08 : 0.05),
            isDark ? (byte)0x78 : (byte)0x88);

        var dropTargetBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x35, 0x3D, 0x48)
                    : ColorHelper.FromArgb(0xFF, 0xE7, 0xF3, 0xFF),
                accentMix: isDark ? 0.42 : 0.30,
                overlayMix: isDark ? 0.06 : 0.04),
            isDark ? (byte)0x92 : (byte)0x9C);

        _normalItemSurfaceBrush = new SolidColorBrush(defaultBackground);
        _selectedItemSurfaceBrush = new SolidColorBrush(selectedBackground);
        _hoverItemSurfaceBrush = new SolidColorBrush(hoverBackground);
        _pressedItemSurfaceBrush = new SolidColorBrush(pressedBackground);
        _selectedHoverItemSurfaceBrush = new SolidColorBrush(selectedHoverBackground);
        _dropTargetItemSurfaceBrush = new SolidColorBrush(dropTargetBackground);
        _normalItemBorderBrush = new SolidColorBrush(Colors.Transparent);
        _dropTargetItemBorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xD8));
    }

    private void ApplyWidgetItemLayout(Border border)
    {
        if (IsWithinItemsListView(border))
        {
            border.Width = double.NaN;
            border.Height = double.NaN;
            border.HorizontalAlignment = HorizontalAlignment.Left;
            border.Margin = ViewModel.ListItemMargin;
            border.Padding = ViewModel.ListItemPadding;
            border.CornerRadius = GetItemSurfaceCornerRadius();

            if (border.Child is Grid listGrid)
            {
                listGrid.ColumnSpacing = 10;
                listGrid.HorizontalAlignment = HorizontalAlignment.Left;

                if (TryGetDescendant<Image>(listGrid, out var icon))
                {
                    icon.Width = ViewModel.ListIconSize;
                    icon.Height = ViewModel.ListIconSize;
                }

                if (TryGetDescendant<FontIcon>(listGrid, out var fallbackIcon))
                {
                    fallbackIcon.Width = ViewModel.ListIconSize;
                    fallbackIcon.Height = ViewModel.ListIconSize;
                    fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.ListIconSize * 0.72), 12, 20);
                }

                double textMaxWidth = GetListItemTextMaxWidth();

                if (TryGetDescendant<StackPanel>(listGrid, out var textHost, "ListItemTextHost"))
                {
                    textHost.MaxWidth = textMaxWidth;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var label))
                {
                    label.FontSize = ViewModel.ListLabelFontSize;
                    label.MaxWidth = textMaxWidth;
                    label.HorizontalAlignment = HorizontalAlignment.Left;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var nameText, "ListItemNameText"))
                {
                    nameText.FontSize = ViewModel.ListLabelFontSize;
                    nameText.MaxWidth = textMaxWidth;
                    nameText.HorizontalAlignment = HorizontalAlignment.Left;
                    nameText.Visibility = Visibility.Visible;
                }

                if (TryGetDescendant<TextBlock>(listGrid, out var detailText, "ListItemDetailText"))
                {
                    detailText.FontSize = Math.Max(ViewModel.ListLabelFontSize - 2, 9);
                    detailText.MaxWidth = textMaxWidth;
                    detailText.HorizontalAlignment = HorizontalAlignment.Left;
                    detailText.Visibility = ViewModel.ShowListItemDetails
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            return;
        }

        if (border.Parent is FrameworkElement slot && slot.Tag as string == "ItemSlot")
        {
            slot.Width = ViewModel.IconTileWidth;
            slot.Height = ViewModel.IconTileHeight;
            slot.Margin = ViewModel.IconTileMargin;
        }

        border.Width = double.NaN;
        border.Height = double.NaN;
        border.MaxWidth = Math.Max(ViewModel.IconImageSize + 18, ViewModel.IconLabelMaxWidth + 12);
        border.Margin = new Thickness(0);
        border.Padding = ViewModel.IconTilePadding;
        border.CornerRadius = GetItemSurfaceCornerRadius();

        if (border.Child is StackPanel iconStack)
        {
            iconStack.Spacing = ViewModel.IconContentSpacing;

            if (TryGetDescendant<Image>(iconStack, out var icon))
            {
                icon.Width = ViewModel.IconImageSize;
                icon.Height = ViewModel.IconImageSize;
            }

            if (TryGetDescendant<FontIcon>(iconStack, out var fallbackIcon))
            {
                fallbackIcon.Width = ViewModel.IconImageSize;
                fallbackIcon.Height = ViewModel.IconImageSize;
                fallbackIcon.FontSize = Math.Clamp(Math.Round(ViewModel.IconImageSize * 0.72), 16, 34);
            }

            if (TryGetDescendant<TextBlock>(iconStack, out var label, "IconItemNameText"))
            {
                label.FontSize = ViewModel.IconLabelFontSize;
                label.MaxWidth = ViewModel.IconLabelMaxWidth;
            }
        }
    }

    private double GetListItemTextMaxWidth()
    {
        double availableWidth = ItemsListView.ActualWidth > 0 ? ItemsListView.ActualWidth : ViewModel.Config.Width;
        double horizontalPadding = ViewModel.ListItemPadding.Left + ViewModel.ListItemPadding.Right;
        double reservedWidth = ViewModel.ListIconSize + 10 + horizontalPadding + 24;
        return Math.Max(56, availableWidth - reservedWidth);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result) where T : DependencyObject
    {
        return TryGetDescendant(parent, out result, null);
    }

    private static bool TryGetDescendant<T>(DependencyObject parent, out T result, string? name) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild &&
                (string.IsNullOrWhiteSpace(name) ||
                 (typedChild is FrameworkElement frameworkElement &&
                  string.Equals(frameworkElement.Name, name, StringComparison.Ordinal))))
            {
                result = typedChild;
                return true;
            }

            if (TryGetDescendant(child, out result, name))
            {
                return true;
            }
        }

        result = null!;
        return false;
    }

    private CornerRadius GetItemSurfaceCornerRadius()
    {
        double radius = _settingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 6,
            _ => 5
        };

        return new CornerRadius(radius);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private bool IsWithinItemsListView(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ItemsListView))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel_PropertyChanged(sender, e));
            return;
        }

        if (e.PropertyName is nameof(WidgetViewModel.IsLoading))
        {
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.TitleIconKind)
            or nameof(WidgetViewModel.IconGlyph)
            or nameof(WidgetViewModel.FollowsDefaultStoragePath))
        {
            ApplyTitleBarLayout();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.ViewMode)
            or nameof(WidgetViewModel.IconViewVisibility)
            or nameof(WidgetViewModel.ListViewVisibility)
            or nameof(WidgetViewModel.ShowListItemDetails)
            or nameof(WidgetViewModel.ShowFileItemPathTooltips)
            or nameof(WidgetViewModel.IconTileWidth)
            or nameof(WidgetViewModel.IconTileHeight)
            or nameof(WidgetViewModel.IconTileMargin)
            or nameof(WidgetViewModel.IconTilePadding)
            or nameof(WidgetViewModel.IconContentSpacing)
            or nameof(WidgetViewModel.IconImageSize)
            or nameof(WidgetViewModel.IconLabelFontSize)
            or nameof(WidgetViewModel.IconLabelMaxWidth)
            or nameof(WidgetViewModel.ListItemMargin)
            or nameof(WidgetViewModel.ListItemPadding)
            or nameof(WidgetViewModel.ListIconSize)
            or nameof(WidgetViewModel.ListLabelFontSize))
        {
            ApplyTitleBarLayout();
            UpdateInteractiveSurfaces();
            QueueEmptyStateUpdate();
        }
    }

    private void ItemsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateInteractiveSurfaces();
    }

    private ListViewBase? GetActiveItemsView()
    {
        if (ViewModel.IsIconMode)
        {
            return ItemsGridView;
        }

        if (ViewModel.IsListMode)
        {
            return ItemsListView;
        }

        return null;
    }

    private List<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView()?.SelectedItems.OfType<WidgetItem>().ToList() ?? [];
    }

    private WidgetItem? GetPrimarySelectedItem()
    {
        return GetSelectedItems().FirstOrDefault();
    }

    public void ClearItemSelection()
    {
        ClearItemSelectionCore(clearCutState: false);
    }

    public void RevealSavedItem(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => RevealSavedItem(itemPath));
            return;
        }

        var item = ViewModel.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, itemPath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        SelectSingleItem(item);
        ShowStatusToast(_localizationService.T("Widget.SavedHere"));
    }

    private void ClearItemSelectionCore(bool clearCutState)
    {
        if (clearCutState)
        {
            ClearCutState();
        }

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            SynchronizeListViewSelection(listView, []);
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsSelected = false;
        }

        _lastSelectionAnchorIndex = -1;
        UpdateInteractiveSurfaces();
    }

    private void ClearOtherWidgetSelections()
    {
        App.Current.WidgetManager?.ClearSelectionsExcept(ViewModel.Config.Id);
    }

    private void ApplySelectionState(ListViewBase? listView)
    {
        var selectedItems = listView?.SelectedItems.OfType<WidgetItem>().ToHashSet() ?? [];
        foreach (var item in ViewModel.Items)
        {
            item.IsSelected = selectedItems.Contains(item);
        }

        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ApplySelectionPreview(HashSet<WidgetItem> selectedItems)
    {
        foreach (var item in _selectionPreviewItems)
        {
            if (!selectedItems.Contains(item))
            {
                SetItemSelectionPreview(item, false);
            }
        }

        foreach (var item in selectedItems)
        {
            if (!_selectionPreviewItems.Contains(item))
            {
                SetItemSelectionPreview(item, true);
            }
        }

        _selectionPreviewItems = selectedItems;
    }

    private void SetItemSelectionPreview(WidgetItem item, bool isSelected)
    {
        if (item.IsSelected == isSelected)
        {
            return;
        }

        item.IsSelected = isSelected;
        if (_selectionSurfaceByItem.TryGetValue(item, out var surface))
        {
            ApplyWidgetItemSurfaceState(surface, ItemSurfaceState.Normal);
            return;
        }

        if (FindItemSurface(item) is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void SynchronizeListViewSelection(ListViewBase listView, IEnumerable<WidgetItem> selectedItems)
    {
        _isSynchronizingSelection = true;
        try
        {
            listView.SelectedItems.Clear();
            foreach (var item in selectedItems)
            {
                listView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();

        if (_isMigrationBusy)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            LogDropDiagnostic("RootDragOverBusy", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            LogDropDiagnostic("RootDragOverNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
            return;
        }

        bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
        e.AcceptedOperation = NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder);
        LogDropDiagnostic("RootDragOver", e.DataView, e.AcceptedOperation, movesIntoFolder);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            return;
        }

        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = movesIntoFolder
            ? _localizationService.Format(
                GetRootFolderDropCaptionKey(),
                GetAcceptedOperationCaption(e.AcceptedOperation))
            : _localizationService.T("Widget.DragCaption.Reference");
    }

    private void RootGrid_DragEnter(object sender, DragEventArgs e)
    {
        LogDropDiagnostic("RootDragEnter", e.DataView, e.AcceptedOperation, !string.IsNullOrEmpty(ViewModel.MappedFolderPath));
    }

    private string GetRootFolderDropCaptionKey()
    {
        return ViewModel.FollowsDefaultStoragePath
            ? "Widget.DragCaption.Managed"
            : "Widget.DragCaption.Mapped";
    }

    private DataPackageOperation NormalizePathDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (requestedOperation == DataPackageOperation.None)
        {
            return movesIntoFolder
                ? GetManagedDropOperation()
                : DataPackageOperation.Link;
        }

        var operation = GetAcceptedDropOperation(requestedOperation, movesIntoFolder);
        if (operation != DataPackageOperation.None ||
            !movesIntoFolder ||
            !SupportsOperation(requestedOperation, DataPackageOperation.Link))
        {
            return operation;
        }

        return DataPackageOperation.Link;
    }

    private string GetAcceptedOperationCaption(DataPackageOperation acceptedOperation)
    {
        return ShouldMoveForAcceptedOperation(acceptedOperation)
            ? _localizationService.T("Common.Move")
            : _localizationService.T("Common.Copy");
    }

    private bool ShouldMoveForAcceptedOperation(DataPackageOperation acceptedOperation)
    {
        return acceptedOperation switch
        {
            DataPackageOperation.Copy => false,
            DataPackageOperation.Move => true,
            DataPackageOperation.Link => true,
            _ => true
        };
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();
        _lastRootDragDiagnosticSignature = null;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearFolderDropTarget();
        _lastRootDragDiagnosticSignature = null;

        var deferral = e.GetDeferral();
        try
        {
            if (_isMigrationBusy)
            {
                return;
            }

            if (!HasPathDropData(e.DataView))
            {
                LogDropDiagnostic("RootDropNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: false);
                return;
            }

            bool movesIntoFolder = !string.IsNullOrEmpty(ViewModel.MappedFolderPath);
            LogDropDiagnostic("RootDrop", e.DataView, e.AcceptedOperation, movesIntoFolder);

            var paths = await GetDropPathsAsync(e.DataView);
            if (paths.Length == 0)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=RootDropNoPaths " +
                    $"mapped={movesIntoFolder} requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} " +
                    $"formats={FormatDataPackageFormats(e.DataView.AvailableFormats)}");
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder)
                : e.AcceptedOperation;
            e.AcceptedOperation = acceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                LogDropDiagnostic("RootDropRejectedOperation", e.DataView, acceptedOperation, movesIntoFolder);
                return;
            }

            bool? moveWhenMapped = movesIntoFolder
                ? ShouldMoveForAcceptedOperation(acceptedOperation)
                : null;

            string? sourceWidgetId = TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId");
            if (movesIntoFolder &&
                HasDeskBoxInternalDragData(e.DataView.Properties) &&
                string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal) &&
                moveWhenMapped == true)
            {
                return;
            }

            await ViewModel.ImportPathsAsync(paths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

            if (moveWhenMapped == true)
            {
                await SyncMoveSourceAsync(
                    TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId"),
                    TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths"));
            }

            ClearCutState();
        }
        catch (Exception ex)
        {
            App.Log($"[Widget] RootGrid_Drop failed: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ItemsView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WidgetItem item)
        {
            return;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            return;
        }

        if (!_settingsService.Settings.DoubleClickToOpen)
        {
            OpenItem(item);
        }
    }

    private void ItemsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_settingsService.Settings.DoubleClickToOpen &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private void OpenItem(WidgetItem item)
    {
        ViewModel.OpenItem(item, _hWnd);
        ClearRemovedCutPaths();
        UpdateEmptyState();
    }

    private void ItemsView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element || element.DataContext is not WidgetItem item)
        {
            return;
        }

        _lastRightTapPoint = e.GetPosition(element);

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            ClearOtherWidgetSelections();
            if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
                ApplySelectionState(listView);
            }
        }

        var selectedItems = GetSelectedItems();
        bool isMultiSelection = selectedItems.Count > 1;

        if (isMultiSelection)
        {
            var multiFlyout = new MenuFlyout();
            AddMultiSelectionItems(multiFlyout, selectedItems.Count);
            ShowFlyoutWithElevation(multiFlyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

        // ─── Win11-style MenuFlyout (custom) with "Show more options" ───
        // We always show our WinUI 3 MenuFlyout first (which IS Win11-styled:
        // rounded corners, acrylic background). At the bottom, we add a
        // "Show more options" item that invokes the full classic native shell
        // context menu — mirroring exactly what Windows 11 File Explorer does.
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Open"),
            Icon = new SymbolIcon(Symbol.OpenFile)
        };
        openItem.Click += (_, _) => OpenItem(item);
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        if (CanCopyImageText(item))
        {
            var copyImageTextItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.CopyImageText"),
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyImageTextItem.Click += async (_, _) => await CopyImageTextAsync(item);
            flyout.Items.Add(copyImageTextItem);
        }

        var copyPathItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.CopyPath"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) => CopySelectedPathsToClipboard();
        flyout.Items.Add(copyPathItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var showItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.ShowInExplorer"),
            Icon = new FontIcon { Glyph = "\uE838" }
        };
        showItem.Click += (_, _) => ViewModel.ShowInExplorerCommand.Execute(item);
        flyout.Items.Add(showItem);

        var renameItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        renameItem.Click += async (_, _) => await StartItemRenameAsync(item);
        flyout.Items.Add(renameItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        var propItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Properties"),
            Icon = new FontIcon { Glyph = "\uE946" }
        };
        propItem.Click += (_, _) => ShellContextMenuHelper.ShowProperties(_hWnd, item.Path);
        flyout.Items.Add(propItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.MoveBackToDesktop"),
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) =>
            {
                try
                {
                    int movedCount = await ViewModel.MoveItemsBackToDesktopAsync([item], useShellProgress: true);
                    ShowStatusToast(movedCount > 0
                        ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                        : _localizationService.T("Widget.NoItemsMoved"));
                }
                catch (Exception ex)
                {
                    await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
                }
            };
            flyout.Items.Add(moveBackToDesktopItem);
        }

        // ─── "Show more options" — launches the full classic native shell menu ───
        bool pathExists = !string.IsNullOrEmpty(item.Path) && (System.IO.File.Exists(item.Path) || System.IO.Directory.Exists(item.Path));
        if (pathExists)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var showMoreItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.ShowMoreOptions"),
                Icon = new FontIcon { Glyph = "\uE712" }
            };
            showMoreItem.Click += (_, _) =>
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    TryShowNativeContextMenu(item, element, _lastRightTapPoint);
                });
            };
            flyout.Items.Add(showMoreItem);
        }

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
    }

    /// <summary>
    /// Shows the native Windows Explorer context menu for a single file item.
    /// Handles Z-order elevation, coordinate conversion, and foreground window management.
    /// </summary>
    /// <returns>True if the native menu was shown (regardless of whether a command was invoked); false if it failed and the caller should fall back.</returns>
    private bool TryShowNativeContextMenu(WidgetItem item, FrameworkElement element, Windows.Foundation.Point relativePoint)
    {
        try
        {
            // Convert the relative point to screen coordinates (physical pixels)
            // TransformToVisual(null) gives coordinates relative to the window origin
            var windowPoint = element.TransformToVisual(null).TransformPoint(relativePoint);
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int screenX = (int)(_appWindow.Position.X + windowPoint.X * scale);
            int screenY = (int)(_appWindow.Position.Y + windowPoint.Y * scale);

            // Elevate the window to topmost so the context menu appears above other windows
            BeginInteractionLayer("native-context-menu-opened");

            // Set foreground window — required for TrackPopupMenuEx to dismiss properly
            // when the user clicks outside the menu
            Win32Helper.SetForegroundWindow(_hWnd);

            var result = ShellContextMenuHelper.ShowContextMenu(_hWnd, item.Path, screenX, screenY);

            // Release the interaction layer (restore desktop Z-order)
            ReleaseInteractionLayer("native-context-menu-closed");

            // Refresh the item list after the native menu closes, as the user may have
            // performed file operations (rename, delete, etc.) through the native menu
            if (result == ShellContextMenuHelper.NativeMenuResult.Invoked)
            {
                _ = DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(100);
                    await ViewModel.RefreshFromConfigAsync();
                    ClearRemovedCutPaths();
                    UpdateEmptyState();
                });
            }

            return result != ShellContextMenuHelper.NativeMenuResult.Failed;
        }
        catch (Exception ex)
        {
            App.Log($"[NativeContextMenu] Failed: {ex.Message}");
            ReleaseInteractionLayer("native-context-menu-error");
            return false;
        }
    }

    private void AddMultiSelectionItems(MenuFlyout flyout, int selectedCount)
    {
        var titleItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("Widget.SelectedCount", selectedCount),
            Icon = new FontIcon { Glyph = "\uE762" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: false);
        flyout.Items.Add(copyItem);

        var copyPathItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.CopyPath"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPathItem.Click += (_, _) => CopySelectedPathsToClipboard();
        flyout.Items.Add(copyPathItem);

        var cutItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cut"),
            Icon = new FontIcon { Glyph = "\uE8C6" }
        };
        cutItem.Click += async (_, _) => await CopySelectionToClipboardAsync(cut: true);
        flyout.Items.Add(cutItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Delete"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteItem.Click += async (_, _) => await DeleteSelectedItemsAsync();
        flyout.Items.Add(deleteItem);

        if (CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var moveBackToDesktopItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.MoveBackToDesktop"),
                Icon = new FontIcon { Glyph = "\uE74A" }
            };
            moveBackToDesktopItem.Click += async (_, _) => await MoveSelectedItemsBackToDesktopAsync();
            flyout.Items.Add(moveBackToDesktopItem);
        }
    }

    private void ItemsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            if (_isSynchronizingSelection)
            {
                return;
            }

            if (e.AddedItems.Count > 0)
            {
                ClearOtherWidgetSelections();
            }

            ApplySelectionState(listView);
            _lastSelectionAnchorIndex = GetPrimarySelectedItem() is { } item
                ? ViewModel.Items.IndexOf(item)
                : -1;
        }
    }

    private void ItemsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !CanStartBoxSelection(e.OriginalSource))
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        ClearOtherWidgetSelections();
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedItems()
            : [];
        _selectionPreviewItems = new HashSet<WidgetItem>(_selectionSnapshot);
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
        }
    }

    private void ItemsView_PointerMoved(object sender, PointerRoutedEventArgs e)
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
                CacheSelectionHitTestItems(listView);
            }
        }

        UpdateSelectionRectangleVisual();
        ApplySelectionRectanglePreview();
        e.Handled = true;
    }

    private void ItemsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        FinishSelectionRectangle(listView);
        if (_isBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!_settingsService.Settings.DoubleClickToOpen &&
            !_isBoxSelecting &&
            properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            OpenItem(item);
        }
    }

    private void ItemsView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishSelectionRectangle(listView);
        }
    }

    private void ItemsView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        e.Cancel = !TryPrepareItemDragPackage(
            e.Data,
            GetDragItems(e.Items.OfType<WidgetItem>().FirstOrDefault()));
    }

    private async void ItemsView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            await HandleItemDragCompletedAsync(args.DropResult);
        }
        catch (Exception ex)
        {
            App.Log($"[Widget] ItemsView_DragItemsCompleted failed: {ex}");
        }
    }

    private async Task HandleItemDragCompletedAsync(DataPackageOperation dropResult)
    {
        var draggedPaths = _activeDragSourcePaths;
        bool hasStorageItems = _activeDragHasStorageItems;
        _activeDragSourcePaths = [];
        _activeDragHasStorageItems = false;

        if (draggedPaths.Length == 0)
        {
            return;
        }

        if (ViewModel.FollowsDefaultStoragePath &&
            IsCursorOnDesktop() &&
            !IsCursorOverThisWindow())
        {
            if (hasStorageItems || dropResult == DataPackageOperation.Move)
            {
                await Task.Delay(250);
                await ViewModel.RefreshFromConfigAsync();
                ClearRemovedCutPaths();
                UpdateEmptyState();
                return;
            }

            await MoveDraggedPathsBackToDesktopAsync(draggedPaths, useShellProgress: true);
            return;
        }

        if (dropResult != DataPackageOperation.Move)
        {
            return;
        }

        ClearRemovedCutPaths();
    }

    private IReadOnlyList<WidgetItem> GetDragItems(WidgetItem? fallbackItem)
    {
        var selectedItems = GetSelectedItems();
        if (fallbackItem is null)
        {
            return selectedItems;
        }

        if (selectedItems.Any(item => ReferenceEquals(item, fallbackItem)))
        {
            return selectedItems;
        }

        return [fallbackItem];
    }

    private bool TryPrepareItemDragPackage(DataPackage dataPackage, IReadOnlyList<WidgetItem> draggedItems)
    {
        if (draggedItems.Count == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        var sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return false;
        }

        dataPackage.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;

        var storageItems = App.Current.FileService.GetStorageItems(sourcePaths);
        if (storageItems.Count > 0)
        {
            dataPackage.SetStorageItems(storageItems, false);
        }
        else
        {
            App.Log($"[DragStart] No StorageItems for selected paths; using path fallback. paths={sourcePaths.Length}");
        }

        dataPackage.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
        dataPackage.Properties["DeskBoxSourcePaths"] = sourcePaths;
        dataPackage.Properties["DeskBoxInternalDragToken"] = DeskBoxInternalDragToken;
        dataPackage.Properties.Title = sourcePaths.Length == 1
            ? Path.GetFileName(sourcePaths[0])
            : _localizationService.Format("Widget.ItemCount", sourcePaths.Length);
        dataPackage.SetText(string.Join(Environment.NewLine, sourcePaths));
        _activeDragSourcePaths = sourcePaths;
        _activeDragHasStorageItems = storageItems.Count > 0;
        return true;
    }

    private async void ItemsView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        await HandleItemsKeyDownAsync(e);
    }

    private async Task HandleItemsKeyDownAsync(KeyRoutedEventArgs e)
    {
        if (e.Handled || _isMigrationBusy)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            HasAncestorOfType<TextBox>(source))
        {
            return;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        bool shiftPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.A)
        {
            ClearOtherWidgetSelections();
            listView.SelectAll();
            ApplySelectionState(listView);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.C)
        {
            await CopySelectionToClipboardAsync(cut: false);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.X)
        {
            await CopySelectionToClipboardAsync(cut: true);
            e.Handled = true;
            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.V)
        {
            await PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.C)
        {
            CopySelectedPathsToClipboard();
            e.Handled = true;
            return;
        }

        if (ctrlPressed && shiftPressed && e.Key == Windows.System.VirtualKey.N)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await CreateFolderInMappedLocationAsync();
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.O)
        {
            if (GetPrimarySelectedItem() is { } openItem)
            {
                OpenItem(openItem);
                e.Handled = true;
            }

            return;
        }

        if (ctrlPressed && e.Key == Windows.System.VirtualKey.R)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            ShowStatusToast(_localizationService.T("Widget.Refreshed"));
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2)
        {
            if (GetPrimarySelectedItem() is { } renameItem)
            {
                await StartItemRenameAsync(renameItem);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            if (GetSelectedItems().Count > 0)
            {
                await DeleteSelectedItemsAsync();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ClearItemSelectionCore(clearCutState: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter && GetPrimarySelectedItem() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private void CopySelectedPathsToClipboard()
    {
        var selectedPaths = GetSelectedItems()
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectedPaths.Length == 0)
        {
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, selectedPaths);
        var package = new DataPackage();
        package.SetText(clipboardText);
        DeskBoxClipboardWriteScope.MarkWrite(text: clipboardText, paths: selectedPaths);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ShowStatusToast(_localizationService.Format("Widget.CopyPathCount", selectedPaths.Length));
    }

    private async Task CopyImageTextAsync(WidgetItem item)
    {
        if (!CanCopyImageText(item))
        {
            ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine is null)
            {
                ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
                return;
            }

            var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
            if (string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                ShowStatusToast(_localizationService.T("Widget.CopyImageTextNoText"));
                return;
            }

            var package = new DataPackage();
            package.SetText(ocrResult.Text);
            DeskBoxClipboardWriteScope.MarkWrite(text: ocrResult.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowStatusToast(_localizationService.T("Widget.CopyImageTextCopied"));
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetWindow] Failed to copy image text path='{item.Path}': {ex}");
            ShowStatusToast(_localizationService.T("Widget.CopyImageTextFailed"));
        }
    }

    private static bool CanCopyImageText(WidgetItem item)
    {
        return !item.IsFolder &&
               !string.IsNullOrWhiteSpace(item.Path) &&
               File.Exists(item.Path) &&
               IsImageFile(item.Path);
    }

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CopySelectionToClipboardAsync(bool cut)
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        var sourcePaths = selectedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            ShowStatusToast(_localizationService.T("Widget.NoCopyableItems"));
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, sourcePaths);
        DeskBoxClipboardWriteScope.MarkWrite(text: clipboardText, paths: sourcePaths);

        var package = new DataPackage();
        package.RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy;
        bool shellClipboardSet = ShellClipboardHelper.TrySetFileDropList(sourcePaths, cut);
        if (!shellClipboardSet)
        {
            var storageItems = await App.Current.FileService.GetStorageItemsAsync(sourcePaths);
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }
            else
            {
                package.SetText(clipboardText);
            }

            package.Properties["DeskBoxSourceWidgetId"] = ViewModel.Config.Id;
            package.Properties["DeskBoxSourcePaths"] = sourcePaths;
            package.Properties["DeskBoxInternalDragToken"] = DeskBoxInternalDragToken;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = cut
            ? sourcePaths
            : [];

        ApplyCutState();
        UpdateInteractiveSurfaces();
        ShowStatusToast(cut
            ? _localizationService.Format("Widget.CutCount", sourcePaths.Length)
            : _localizationService.Format("Widget.CopyCount", sourcePaths.Length));
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = Clipboard.GetContent();
        IReadOnlyList<string> sourcePaths = TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourcePaths.Count == 0 && clipboard.Contains(StandardDataFormats.StorageItems))
        {
            var items = await clipboard.GetStorageItemsAsync();
            sourcePaths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }

        if (sourcePaths.Count == 0)
        {
            return;
        }

        int itemCount = sourcePaths.Count;

        bool? moveWhenMapped = clipboard.RequestedOperation != DataPackageOperation.Copy;

        await ViewModel.ImportPathsAsync(sourcePaths, moveWhenMapped, useShellProgress: moveWhenMapped == true);

        if (moveWhenMapped == true)
        {
            await SyncMoveSourceAsync(
                TryGetPackageString(clipboard.Properties, "DeskBoxSourceWidgetId"),
                TryGetPackageStringArray(clipboard.Properties, "DeskBoxSourcePaths"));
        }

        ClearCutState();
        ShowStatusToast(moveWhenMapped == true
            ? _localizationService.Format("Widget.MovedCount", itemCount)
            : _localizationService.Format("Widget.PastedCount", itemCount));
    }

    private void ClearCutState()
    {
        _cutClipboardPaths = [];
        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ApplyCutState()
    {
        foreach (var item in ViewModel.Items)
        {
            item.IsCut = _cutClipboardPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void ClearRemovedCutPaths()
    {
        if (_cutClipboardPaths.Length == 0)
        {
            return;
        }

        var currentPaths = ViewModel.Items
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cutClipboardPaths = _cutClipboardPaths
            .Where(currentPaths.Contains)
            .ToArray();
        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ShowStatusToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusToastText.Text = message;
        StatusToast.Visibility = Visibility.Visible;
        StatusToast.Opacity = 1;

        _statusToastTimer ??= DispatcherQueue.CreateTimer();
        _statusToastTimer.Stop();
        _statusToastTimer.Interval = TimeSpan.FromSeconds(2.2);
        _statusToastTimer.Tick -= StatusToastTimer_Tick;
        _statusToastTimer.Tick += StatusToastTimer_Tick;
        _statusToastTimer.Start();
    }

    private void StatusToastTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        StatusToast.Opacity = 0;
        StatusToast.Visibility = Visibility.Collapsed;
    }

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !IsWithinInteractiveSurface(source) &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateSelectionRectangleVisual()
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

    private void CacheSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindItemSurface(item) ?? container;
            var topLeft = target.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            var surface = target as Border;

            _selectionHitTestItems.Add(new SelectionHitTestItem(item, surface, bounds));
            if (surface is not null)
            {
                _selectionSurfaceByItem[item] = surface;
            }
        }
    }

    private void ApplySelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<WidgetItem>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplySelectionPreview(selectedItems);
    }

    private void FinishSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplySelectionRectanglePreview();
            SynchronizeListViewSelection(
                listView,
                ViewModel.Items.Where(item => _selectionPreviewItems.Contains(item)));
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            ApplySelectionState(listView);
        }
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

    private async Task SyncMoveSourceAsync(string? sourceWidgetId, IReadOnlyList<string> sourcePaths)
    {
        if (string.IsNullOrWhiteSpace(sourceWidgetId) || sourcePaths.Count == 0)
        {
            return;
        }

        if (string.Equals(sourceWidgetId, ViewModel.Config.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            return;
        }

        if (App.Current.WidgetManager is { } widgetManager)
        {
            await widgetManager.NotifyItemsMovedOutAsync(sourceWidgetId, sourcePaths);
        }
    }

    private static string? TryGetPackageString(DataPackagePropertySetView properties, string key)
    {
        return properties.TryGetValue(key, out object? value) ? value as string : null;
    }

    private static IReadOnlyList<string> TryGetPackageStringArray(DataPackagePropertySetView properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value) || value is null)
        {
            return [];
        }

        return value switch
        {
            string[] array => array,
            IReadOnlyList<string> readOnlyList => readOnlyList,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            _ => []
        };
    }

    private static bool HasDeskBoxInternalDragData(DataPackagePropertySetView properties)
    {
        return string.Equals(
                   TryGetPackageString(properties, "DeskBoxInternalDragToken"),
                   DeskBoxInternalDragToken,
                   StringComparison.Ordinal) &&
               TryGetPackageStringArray(properties, "DeskBoxSourcePaths").Count > 0;
    }

    private static bool HasPathDropData(DataPackageView dataView)
    {
        return TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths").Count > 0 ||
               dataView.Contains(StandardDataFormats.StorageItems) ||
               dataView.Contains(StandardDataFormats.Text) ||
               HasFallbackFileFormats(dataView);
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

    private static async Task<string[]> GetDropPathsAsync(DataPackageView dataView)
    {
        var sourcePaths = TryGetPackageStringArray(dataView.Properties, "DeskBoxSourcePaths")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length > 0)
        {
            return sourcePaths;
        }

        if (dataView.Contains(StandardDataFormats.StorageItems) ||
            HasFallbackFileFormats(dataView))
        {
            try
            {
                var items = await dataView.GetStorageItemsAsync();
                sourcePaths = items
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                App.Log(
                    $"[DropDiagnostic] GetStorageItemsAsync count={items.Count} pathCount={sourcePaths.Length} " +
                    $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
                if (sourcePaths.Length > 0)
                {
                    return sourcePaths;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] GetStorageItemsAsync failed: {ex.Message}");
            }

            sourcePaths = await TryGetLegacyFormatPathsAsync(dataView);
            if (sourcePaths.Length > 0)
            {
                return sourcePaths;
            }
        }

        if (!dataView.Contains(StandardDataFormats.Text))
        {
            return [];
        }

        string text = await dataView.GetTextAsync();
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate => TryNormalizeDroppedPath(candidate, out string normalizedPath) ? normalizedPath : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<string[]> TryGetLegacyFormatPathsAsync(DataPackageView dataView)
    {
        var paths = new List<string>();
        foreach (string format in dataView.AvailableFormats)
        {
            if (!MayContainLegacyPathText(format))
            {
                continue;
            }

            try
            {
                object? data = await dataView.GetDataAsync(format);
                AppendCandidatePaths(paths, data);
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Legacy format read failed format='{format}': {ex.Message}");
            }
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MayContainLegacyPathText(string format)
    {
        return format.Contains("FileName", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("Shell IDList Array", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("ShellIDListArray", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameW", StringComparison.OrdinalIgnoreCase) ||
               format.Contains("FileNameMap", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCandidatePaths(List<string> paths, object? data)
    {
        switch (data)
        {
            case null:
                return;
            case string text:
                AppendCandidatePathText(paths, text);
                return;
            case IEnumerable<string> strings:
                foreach (string value in strings)
                {
                    AppendCandidatePathText(paths, value);
                }

                return;
        }

        App.Log($"[DropDiagnostic] Legacy format returned unsupported type: {data.GetType().FullName}");
    }

    private static void AppendCandidatePathText(List<string> paths, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string candidate in text.Split(
                     ["\0", "\r\n", "\n"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeDroppedPath(candidate, out string normalizedPath))
            {
                paths.Add(normalizedPath);
            }
        }
    }

    private static bool TryNormalizeDroppedPath(string candidate, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        try
        {
            string fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                normalizedPath = fullPath;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void LogDropDiagnostic(
        string stage,
        DataPackageView dataView,
        DataPackageOperation acceptedOperation,
        bool movesIntoFolder)
    {
        string signature = $"{stage}|{dataView.RequestedOperation}|{acceptedOperation}|{movesIntoFolder}|{FormatDataPackageFormats(dataView.AvailableFormats)}";
        if (stage.StartsWith("Folder", StringComparison.Ordinal))
        {
            if (string.Equals(_lastFolderDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastFolderDragDiagnosticSignature = signature;
        }
        else
        {
            if (string.Equals(_lastRootDragDiagnosticSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastRootDragDiagnosticSignature = signature;
        }

        App.Log(
            $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage={stage} " +
            $"mapped={!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath)} managed={ViewModel.FollowsDefaultStoragePath} " +
            $"requested={dataView.RequestedOperation} accepted={acceptedOperation} " +
            $"containsStorage={dataView.Contains(StandardDataFormats.StorageItems)} containsText={dataView.Contains(StandardDataFormats.Text)} " +
            $"fallback={HasFallbackFileFormats(dataView)} " +
            $"formats={FormatDataPackageFormats(dataView.AvailableFormats)}");
    }

    private static string FormatDataPackageFormats(IReadOnlyList<string> formats)
    {
        return formats.Count == 0
            ? "<none>"
            : string.Join(",", formats.OrderBy(format => format, StringComparer.Ordinal));
    }

    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (FileWidgetShell.ChromeMode is not (WidgetChromeMode.Overlay or WidgetChromeMode.Hidden) &&
            e.OriginalSource is DependencyObject source &&
            IsWithin(source, TitleBarGrid))
        {
            return;
        }

        ShowFlyoutWithElevation(CreateContentAreaFlyout(), RootGrid, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (!ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            return;
        }

        var position = e.GetPosition(TitleBarGrid);
        TrackMoreFlyoutAnchor(TitleBarGrid, position);
        ShowFlyoutWithElevation(CreateMoreFlyout(), TitleBarGrid, position);
        e.Handled = true;
    }

    private void WidgetItemSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            _interactiveSurfaces.Add(border);
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemTooltip(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            _interactiveSurfaces.Remove(border);
        }
    }

    private void WidgetItemSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy || _isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Hover);
        }
    }

    private void WidgetItemSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosing || _isHideAnimationRunning || !_isVisibleOnDesktop)
        {
            return;
        }

        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            return;
        }

        var point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
            return;
        }

        var listView = GetActiveItemsView();
        if (listView is not null)
        {
            ClearOtherWidgetSelections();
            if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
            {
                if (!item.IsSelected)
                {
                    listView.SelectedItems.Add(item);
                }
            }
            else if (!item.IsSelected)
            {
                listView.SelectedItems.Clear();
                listView.SelectedItems.Add(item);
            }

            ApplySelectionState(listView);
        }

        RootGrid.Focus(FocusState.Programmatic);
        _surfaceDragCompletionHandled = false;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Pressed);
    }

    private void WidgetItemSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            bool isInside = e.GetCurrentPoint(border).Position is var point &&
                point.X >= 0 &&
                point.Y >= 0 &&
                point.X <= border.ActualWidth &&
                point.Y <= border.ActualHeight;

            ApplyWidgetItemSurfaceState(border, isInside ? ItemSurfaceState.Hover : ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (_isMigrationBusy)
        {
            args.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        if (sender is not Border border || border.DataContext is not WidgetItem item)
        {
            args.Cancel = true;
            _activeDragSourcePaths = [];
            _activeDragHasStorageItems = false;
            return;
        }

        if (!TryPrepareItemDragPackage(args.Data, GetDragItems(item)))
        {
            args.Cancel = true;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
    }

    private async void WidgetItemSurface_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if (_surfaceDragCompletionHandled)
        {
            return;
        }

        _surfaceDragCompletionHandled = true;
        await HandleItemDragCompletedAsync(args.DropResult);
    }

    private void WidgetItemSurface_DragOver(object sender, DragEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        if (!TryGetFolderDropTarget(sender, out var border, out var targetFolder))
        {
            return;
        }

        e.Handled = true;

        if (!HasPathDropData(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            LogDropDiagnostic("FolderDragOverNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
            return;
        }

        var sourcePaths = TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths");
        if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = _localizationService.T("Widget.CannotMoveToFolder");
            ClearFolderDropTarget();
            return;
        }

        e.AcceptedOperation = NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true);
        LogDropDiagnostic("FolderDragOver", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
        if (e.AcceptedOperation == DataPackageOperation.None)
        {
            e.DragUIOverride.IsGlyphVisible = false;
            ClearFolderDropTarget();
            return;
        }

        SetFolderDropTarget(border);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = _localizationService.Format(
            ShouldMoveForAcceptedOperation(e.AcceptedOperation)
                ? "Widget.MoveToFolder"
                : "Widget.CopyToFolder",
            targetFolder.Name);
    }

    private void WidgetItemSurface_DragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _folderDropTarget))
        {
            ClearFolderDropTarget();
        }

        _lastFolderDragDiagnosticSignature = null;
    }

    private async void WidgetItemSurface_Drop(object sender, DragEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            ClearFolderDropTarget();
            return;
        }

        if (!TryGetFolderDropTarget(sender, out _, out var targetFolder))
        {
            return;
        }

        e.Handled = true;
        ClearFolderDropTarget();

        if (!HasPathDropData(e.DataView))
        {
            LogDropDiagnostic("FolderDropNoPathData", e.DataView, e.AcceptedOperation, movesIntoFolder: true);
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            LogDropDiagnostic("FolderDrop", e.DataView, e.AcceptedOperation, movesIntoFolder: true);

            var sourcePaths = await GetDropPathsAsync(e.DataView);
            if (sourcePaths.Length == 0)
            {
                App.Log(
                    $"[DropDiagnostic] widget='{ViewModel.Name}' id={ViewModel.Config.Id} stage=FolderDropNoPaths " +
                    $"requested={e.DataView.RequestedOperation} accepted={e.AcceptedOperation} " +
                    $"target='{targetFolder.Path}' formats={FormatDataPackageFormats(e.DataView.AvailableFormats)}");
                return;
            }

            if (IsInvalidFolderDrop(sourcePaths, targetFolder.Path))
            {
                ShowStatusToast(_localizationService.T("Widget.CannotMoveToFolder"));
                return;
            }

            var acceptedOperation = e.AcceptedOperation == DataPackageOperation.None
                ? NormalizePathDropOperation(e.DataView.RequestedOperation, movesIntoFolder: true)
                : e.AcceptedOperation;
            e.AcceptedOperation = acceptedOperation;
            if (acceptedOperation == DataPackageOperation.None)
            {
                LogDropDiagnostic("FolderDropRejectedOperation", e.DataView, acceptedOperation, movesIntoFolder: true);
                return;
            }

            bool move = ShouldMoveForAcceptedOperation(acceptedOperation);
            var results = await App.Current.FileService.TransferItemsWithResultAsync(sourcePaths, targetFolder.Path, move);
            if (results.Count == 0)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
            {
                await ViewModel.RefreshFromConfigAsync();
            }

            if (move)
            {
                await SyncMoveSourceAsync(
                    TryGetPackageString(e.DataView.Properties, "DeskBoxSourceWidgetId"),
                    TryGetPackageStringArray(e.DataView.Properties, "DeskBoxSourcePaths"));
                ClearRemovedCutPaths();
            }

            ShowStatusToast(_localizationService.Format(
                move ? "Widget.MovedToFolder" : "Widget.CopiedToFolder",
                targetFolder.Name,
                results.Count));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToFolderFailed"), ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryGetFolderDropTarget(object sender, out Border border, out WidgetItem folder)
    {
        if (sender is Border targetBorder &&
            targetBorder.DataContext is WidgetItem item &&
            item.IsFolder &&
            Directory.Exists(item.Path))
        {
            border = targetBorder;
            folder = item;
            return true;
        }

        border = null!;
        folder = null!;
        return false;
    }

    private void SetFolderDropTarget(Border border)
    {
        if (ReferenceEquals(_folderDropTarget, border))
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
            return;
        }

        ClearFolderDropTarget();
        _folderDropTarget = border;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.DropTarget);
    }

    private void ClearFolderDropTarget()
    {
        if (_folderDropTarget is null)
        {
            return;
        }

        var border = _folderDropTarget;
        _folderDropTarget = null;
        ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
    }

    private bool IsPointerOverFolderDropTarget()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        foreach (var border in _interactiveSurfaces.ToArray())
        {
            if (border.DataContext is not WidgetItem { IsFolder: true } folder ||
                !Directory.Exists(folder.Path) ||
                border.XamlRoot is null ||
                border.ActualWidth <= 0 ||
                border.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = border.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            var left = _appWindow.Position.X + (topLeft.X * scale);
            var top = _appWindow.Position.Y + (topLeft.Y * scale);
            var right = left + (border.ActualWidth * scale);
            var bottom = top + (border.ActualHeight * scale);

            if (cursor.X >= left &&
                cursor.X <= right &&
                cursor.Y >= top &&
                cursor.Y <= bottom)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvalidFolderDrop(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || sourcePaths.Count == 0)
        {
            return false;
        }

        string normalizedDestination = Path.GetFullPath(destinationFolder);
        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            string normalizedSource = Path.GetFullPath(sourcePath);
            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Directory.Exists(normalizedSource) &&
                FileService.IsPathUnderDirectory(normalizedDestination, normalizedSource))
            {
                return true;
            }
        }

        return false;
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDragging || _isResizing || TitleEditBox.Visibility == Visibility.Visible)
            {
                return;
            }

            StartRename();
        });
    }

    private void StartRename()
    {
        _isCancellingTitleRename = false;
        BeginInteractionLayer("file-title-rename-opened", elevate: false);
        PrepareRenameEditor();
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        FocusTextInputEditor(TitleEditBox, selectAll: true);
    }

    private void PrepareRenameEditor()
    {
        double titleWidth = TitleText.ActualWidth > 0
            ? TitleText.ActualWidth + 36
            : (ViewModel.Name.Length * 9.5) + 36;

        TitleEditBox.Width = Math.Clamp(titleWidth, 120, 220);
        TitleEditBox.Text = ViewModel.Name;
    }

    private async Task CommitRenameAsync()
    {
        if (_isCommittingTitleRename ||
            TitleEditBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = TitleEditBox.Text.Trim();
        _isCommittingTitleRename = true;
        try
        {
            if (!string.IsNullOrEmpty(newName))
            {
                await ViewModel.RenameAsync(newName);
            }

            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            ReleaseInteractionLayer("file-title-rename-committed");
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.RenameFailed"), ex.Message);
            TitleEditBox.Focus(FocusState.Programmatic);
            TitleEditBox.SelectAll();
        }
        finally
        {
            _isCommittingTitleRename = false;
        }
    }

    private void CancelRename()
    {
        _isCancellingTitleRename = true;
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        ReleaseInteractionLayer("file-title-rename-canceled");
    }

    private async void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingTitleRename)
        {
            _isCancellingTitleRename = false;
            return;
        }

        await CommitRenameAsync();
    }

    private async void TitleEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitRenameAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelRename();
            e.Handled = true;
        }
    }

    private void AddFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            return;
        }

        var target = sender as FrameworkElement ?? AddButton;
        ShowFlyoutWithElevation(CreateNewWidgetFlyout(), target);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        if (!ShouldStartTitleDrag(e.OriginalSource))
        {
            return;
        }

        var properties = e.GetCurrentPoint(TitleBarGrid).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        if (CanStartRenameFromTitleArea(e.OriginalSource) && IsTitleBarDoubleClick(cursorPt))
        {
            _hasPendingTitleBarClick = false;
            StartRename();
            e.Handled = true;
            return;
        }

        TrackTitleBarClick(e.OriginalSource, cursorPt);
        BeginWindowDrag(e, TitleBarGrid, cursorPt);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
        {
            e.Handled = true;
            return;
        }

        if (ViewModel.IsPositionLocked)
        {
            return;
        }

        var captureElement = FileWidgetShell.DragHandleElement;
        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var cursorPt);
        _hasPendingTitleBarClick = false;
        BeginWindowDrag(e, captureElement, cursorPt);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement, Win32Helper.POINT cursorPt)
    {
        _isDragging = true;
        _hasMovedTitleBarDrag = false;
        _displayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        // Begin drag-move snap session
        App.Current?.ResizeGuideOverlay.BeginDrag(_hWnd, RootGrid);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
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
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (_hasPendingTitleBarClick && dragDistanceSquared > 25)
        {
            _hasPendingTitleBarClick = false;
        }

        if (!_hasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            _hasMovedTitleBarDrag = true;
        }

        int newX = _initialWindowPos.X + deltaX;
        int newY = _initialWindowPos.Y + deltaY;

        // Apply drag-move snap
        var proposedBounds = new Windows.Graphics.RectInt32(
            newX, newY, _initialWindowSize.Width, _initialWindowSize.Height);
        var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
            ?? proposedBounds;

        ApplyWindowBounds(snappedBounds.X, snappedBounds.Y, snappedBounds.Width, snappedBounds.Height, persist: false);
        e.Handled = true;
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        _dragCaptureElement?.ReleasePointerCapture(e.Pointer);
        _dragCaptureElement = null;

        // End drag-move snap session
        App.Current?.ResizeGuideOverlay.EndDrag();

        if (_hasMovedTitleBarDrag)
        {
            var finalPosition = _appWindow.Position;
            var finalSize = _appWindow.Size;
            CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
            UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
            ReleaseInteractionLayer("file-drag-ended");
        }

        _displayChangeWatcher?.ResumeRestore();
        _hasMovedTitleBarDrag = false;
        e.Handled = true;
    }

    private async void MapFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        BeginInteractionLayer("file-map-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await app.WidgetManager.CreateFolderWidgetAsync(folderPath);
            }
        }
        finally
        {
            ReleaseInteractionLayer("file-picker-closed");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? CloseButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowDeleteWidgetFlyout(target);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var target = sender as FrameworkElement ?? MoreButton;
        TrackMoreFlyoutAnchor(target, null);
        ShowFlyoutWithElevation(CreateMoreFlyout(), target);
    }

    private MenuFlyout CreateContentAreaFlyout()
    {
        var flyout = new MenuFlyout();

        AddCurrentWidgetContentActions(flyout);

        var pasteItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Paste"),
            Icon = new FontIcon { Glyph = "\uE77F" },
            IsEnabled = CanPasteFromClipboard()
        };
        pasteItem.Click += async (_, _) =>
        {
            try
            {
                await PasteFromClipboardAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(_localizationService.T("Widget.PasteFailed"), ex.Message);
            }
        };
        flyout.Items.Add(pasteItem);

        if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            var newFolderItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Common.NewFolder"),
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            newFolderItem.Click += async (_, _) => await CreateFolderInMappedLocationAsync();
            flyout.Items.Add(newFolderItem);

            var openFolderItem = new MenuFlyoutItem
            {
                Text = ViewModel.FollowsDefaultStoragePath
                    ? _localizationService.T("Widget.OpenStorageFolder")
                    : _localizationService.T("Widget.OpenCurrentFolder"),
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolderItem.Click += (_, _) => Win32Helper.OpenFile(ViewModel.MappedFolderPath);
            flyout.Items.Add(openFolderItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var iconView = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.IconView"),
            IsChecked = ViewModel.IsIconMode
        };
        iconView.Click += SetIconView_Click;
        flyout.Items.Add(iconView);

        var listView = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.ListView"),
            IsChecked = ViewModel.IsListMode
        };
        listView.Click += SetListView_Click;
        flyout.Items.Add(listView);

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            ViewModel.Config,
            _chromeDescriptor,
            _localizationService,
            SetChromeModeOverride));

        flyout.Items.Add(new MenuFlyoutSeparator());

        var sortSubItem = new MenuFlyoutSubItem
        {
            Text = _localizationService.T("Widget.SortBy"),
            Icon = new FontIcon { Glyph = "\uE8CB" }
        };

        var sortName = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Name") };
        var sortSize = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Size") };
        var sortType = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.Type") };
        var sortDate = new ToggleMenuFlyoutItem { Text = _localizationService.T("Widget.Sort.DateModified") };

        var sortItems = new[] { sortName, sortSize, sortType, sortDate };
        var sortModes = new[] { WidgetSortMode.Name, WidgetSortMode.Size, WidgetSortMode.Type, WidgetSortMode.DateModified };
        var currentSortIndex = Array.IndexOf(sortModes, ViewModel.Config.SortMode);

        for (int i = 0; i < sortItems.Length; i++)
        {
            var item = sortItems[i];
            var mode = sortModes[i];
            item.IsChecked = i == currentSortIndex;
            item.Click += (_, _) => ViewModel.SetSortMode(mode);
            sortSubItem.Items.Add(item);
        }

        flyout.Items.Add(sortSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var refreshItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Refresh"),
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        refreshItem.Click += async (_, _) =>
        {
            try
            {
                await ViewModel.RefreshFromConfigAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync(_localizationService.T("Widget.RefreshFailed"), ex.Message);
            }
        };
        flyout.Items.Add(refreshItem);

        return flyout;
    }

    private void AddCurrentWidgetContentActions(MenuFlyout flyout)
    {
        if (!ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        var addFileItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.AddFile"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        addFileItem.Click += async (_, _) => await PickAndImportFilesAsync();
        flyout.Items.Add(addFileItem);
    }

    private MenuFlyout CreateMoreFlyout()
    {
        var flyout = new MenuFlyout();

        AddCreateWidgetItems(flyout);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var positionLock = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.LockPosition"),
            Icon = new FontIcon { Glyph = "\uE72E" },
            IsChecked = ViewModel.IsPositionLocked
        };
        positionLock.Click += TogglePositionLock_Click;
        flyout.Items.Add(positionLock);

        var sizeLock = new ToggleMenuFlyoutItem
        {
            Text = _localizationService.T("Widget.LockSize"),
            Icon = new FontIcon { Glyph = "\uE9CE" },
            IsChecked = ViewModel.IsSizeLocked
        };
        sizeLock.Click += ToggleSizeLock_Click;
        flyout.Items.Add(sizeLock);

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(WidgetChromeMenuBuilder.Create(
            ViewModel.Config,
            _chromeDescriptor,
            _localizationService,
            SetChromeModeOverride));

        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!ViewModel.FollowsDefaultStoragePath &&
            !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            var openFolder = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.OpenCurrentFolder"),
                Icon = new FontIcon { Glyph = "\uE838" }
            };
            openFolder.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
                {
                    Win32Helper.OpenFile(ViewModel.MappedFolderPath);
                }
            };
            flyout.Items.Add(openFolder);
        }

        if (!ViewModel.FollowsDefaultStoragePath)
        {
            var changeMappedPathItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.ChangeMappedPath"),
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            changeMappedPathItem.Click += async (_, _) => await PickAndApplyMappedFolderAsync();
            flyout.Items.Add(changeMappedPathItem);
        }

        var rename = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Rename"),
            Icon = new FontIcon { Glyph = "\uE8AC" }
        };
        rename.Click += Rename_Click;
        flyout.Items.Add(rename);

        var settingsItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Settings.Appearance.DetailTitle"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += (_, _) => App.Current.ShowSettings("AppearanceDetail");
        flyout.Items.Add(settingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteWidget = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.Tooltip.DeleteWidget"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        deleteWidget.Click += DeleteWidget_Click;
        flyout.Items.Add(deleteWidget);

        return flyout;
    }

    private void SetChromeModeOverride(WidgetChromeMode mode)
    {
        WidgetChromeModeNames.SetOverrideMode(ViewModel.Config, mode);
        _settingsService.UpdateWidget(ViewModel.Config);
        ApplyTitleBarLayout();
    }

    private void AddCreateWidgetItems(MenuFlyout flyout)
    {
        foreach (var descriptor in new WidgetContentFactory(_localizationService).GetCreateEntryDescriptors())
        {
            var createItem = new MenuFlyoutItem
            {
                Text = GetCreateEntryText(descriptor),
                Icon = new FontIcon { Glyph = descriptor.DefaultGlyph }
            };
            createItem.Click += async (_, _) =>
            {
                if (App.Current.WidgetManager is { } widgetManager)
                {
                    await widgetManager.CreateWidgetOfKindAsync(descriptor.WidgetKind);
                }
            };
            flyout.Items.Add(createItem);
        }

        var mapFolder = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.NewFolderMapping"),
            Icon = new FontIcon { Glyph = "\uE8B7" }
        };
        mapFolder.Click += MapFolderButton_Click;
        flyout.Items.Add(mapFolder);
    }

    private string GetCreateEntryText(WidgetContentDescriptor descriptor)
    {
        return string.IsNullOrWhiteSpace(descriptor.CreateEntryTextKey)
            ? descriptor.DefaultTitle
            : _localizationService.T(descriptor.CreateEntryTextKey);
    }

    private MenuFlyout CreateNewWidgetFlyout()
    {
        var flyout = new MenuFlyout();
        AddCreateWidgetItems(flyout);
        return flyout;
    }

    private async Task PickAndImportFilesAsync()
    {
        BeginInteractionLayer("file-import-picker-opened");

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hWnd);

            var files = await picker.PickMultipleFilesAsync();
            var paths = files
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            bool shouldMove = GetManagedDropOperation() != DataPackageOperation.Copy;
            await ViewModel.ImportPathsAsync(paths, shouldMove, useShellProgress: shouldMove);
            ShowStatusToast(paths.Length == 1
                ? _localizationService.T("Widget.AddedFile")
                : _localizationService.Format("Widget.AddedFiles", paths.Length));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.AddFileFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
        }
    }

    private async Task PickAndApplyMappedFolderAsync()
    {
        if (ViewModel.FollowsDefaultStoragePath)
        {
            return;
        }

        BeginInteractionLayer("file-mapped-folder-picker-opened");

        try
        {
            string? folderPath = FolderPickerService.PickFolder(_hWnd);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            await ViewModel.UpdateMappedFolderPathAsync(folderPath);
            ShowStatusToast(_localizationService.T("Widget.MappedPathUpdated"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.ChangeMappedPathFailed"), ex.Message);
        }
        finally
        {
            ReleaseInteractionLayer("folder-picker-closed");
        }
    }

    private void SetIconView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.Icon)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void SetListView_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ViewMode != ViewMode.List)
        {
            ViewModel.ToggleViewModeCommand.Execute(null);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        StartRename();
    }

    private async Task CreateFolderInMappedLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath))
        {
            return;
        }

        try
        {
            string folderPath = FileService.GetAvailablePath(
                Path.Combine(ViewModel.MappedFolderPath, _localizationService.T("Widget.NewFolderName")));
            Directory.CreateDirectory(folderPath);
            await ViewModel.RefreshFromConfigAsync();
            if (ViewModel.Items.FirstOrDefault(existingItem =>
                    string.Equals(existingItem.Path, folderPath, StringComparison.OrdinalIgnoreCase)) is { } newFolderItem)
            {
                SelectSingleItem(newFolderItem);
                await StartItemRenameAsync(newFolderItem);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.CreateFolderFailed"), ex.Message);
        }
    }

    private async Task DeleteSelectedItemsAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0 && GetPrimarySelectedItem() is { } primaryItem)
        {
            selectedItems = [primaryItem];
        }

        if (selectedItems.Count == 0)
        {
            return;
        }

        if (RequiresDeleteConfirmation(selectedItems))
        {
            ShowDeleteItemsConfirmFlyout(selectedItems);
            return;
        }

        await DeleteItemsWithoutConfirmAsync(selectedItems);
    }

    private static bool RequiresDeleteConfirmation(IReadOnlyList<WidgetItem> selectedItems)
    {
        return selectedItems.Any(item => item.IsFolder);
    }

    private void ShowDeleteItemsConfirmFlyout(IReadOnlyList<WidgetItem> selectedItems)
    {
        var items = selectedItems.ToArray();
        int folderCount = items.Count(item => item.IsFolder);
        if (folderCount == 0)
        {
            _ = DeleteItemsWithoutConfirmAsync(items);
            return;
        }

        _itemDeleteConfirmFlyout?.Hide();

        string message = items.Length == 1
            ? _localizationService.T("Widget.DeleteFolderNoteOne")
            : _localizationService.Format("Widget.DeleteFolderNoteMany", folderCount);
        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            new WidgetCompactConfirmationOptions(
                items.Length == 1
                    ? _localizationService.Format("Widget.DeleteItemsTitleOne", items[0].Name)
                    : _localizationService.Format("Widget.DeleteItemsTitleMany", items.Length),
                _localizationService.T("Widget.MoveToRecycleBin"),
                async () => await DeleteItemsWithoutConfirmAsync(items))
            {
                TitleGlyph = "\uE946",
                Message = message,
                CancelText = _localizationService.T("Common.Cancel")
            });

        _itemDeleteConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_itemDeleteConfirmFlyout, flyout))
            {
                return;
            }

            _itemDeleteConfirmFlyout = null;
            _isInlineFlyoutOpen = false;
            ReleaseInteractionLayer("file-delete-confirm-closed");
        };

        _isInlineFlyoutOpen = true;
        BeginInteractionLayer("file-delete-confirm-opened");

        var target = items.Length == 1
            ? FindItemSurface(items[0]) ?? GetActiveItemsView() as FrameworkElement ?? RootGrid
            : GetActiveItemsView() as FrameworkElement ?? RootGrid;
        flyout.ShowAt(target);
    }

    private async Task DeleteItemsWithoutConfirmAsync(IReadOnlyList<WidgetItem> selectedItems)
    {
        try
        {
            int deletedCount = selectedItems.Count;
            await ViewModel.DeleteItemsAsync(selectedItems);
            ClearRemovedCutPaths();
            ShowStatusToast(_localizationService.Format("Widget.MovedToRecycleBin", deletedCount));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteFailed"), ex.Message);
        }
    }

    private async Task MoveSelectedItemsBackToDesktopAsync()
    {
        var selectedItems = GetSelectedItems();
        if (selectedItems.Count == 0)
        {
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(selectedItems, useShellProgress: true);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedBackToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveBackToDesktopFailed"), ex.Message);
        }
    }

    private async Task MoveDraggedPathsBackToDesktopAsync(IReadOnlyList<string> sourcePaths, bool useShellProgress)
    {
        var pathSet = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        var draggedItems = ViewModel.Items
            .Where(item =>
                pathSet.Contains(Path.GetFullPath(item.Path)) &&
                (File.Exists(item.Path) || Directory.Exists(item.Path)))
            .ToList();
        if (draggedItems.Count == 0)
        {
            await ViewModel.RefreshFromConfigAsync();
            ClearRemovedCutPaths();
            UpdateEmptyState();
            return;
        }

        try
        {
            int movedCount = await ViewModel.MoveItemsBackToDesktopAsync(draggedItems, useShellProgress);

            ClearRemovedCutPaths();
            ShowStatusToast(movedCount > 0
                ? _localizationService.Format("Widget.MovedToDesktop", movedCount)
                : _localizationService.T("Widget.NoItemsMoved"));
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.MoveToDesktopFailed"), ex.Message);
            await ViewModel.RefreshFromConfigAsync();
            UpdateEmptyState();
        }
    }

    private async Task StartItemRenameAsync(WidgetItem item)
    {
        var nameElement = FindItemNameElement(item);
        var target = nameElement ?? FindItemSurface(item);
        var contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            return;
        }

        SelectSingleItem(item);

        _itemRenameTarget = item;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = item.Name;

        // Match the TextBox style to the TextBlock it replaces for seamless inline look
        if (nameElement is TextBlock tb)
        {
            _itemRenameNameText = tb;
            tb.Visibility = Visibility.Collapsed;
            // TextBlock.FontSize == 0 means "use default" — don't pass 0 to TextBox
            ItemRenameTextBox.FontSize = tb.FontSize > 0 ? tb.FontSize : 14;
            ItemRenameTextBox.TextAlignment = tb.TextAlignment;
            ItemRenameTextBox.HorizontalContentAlignment = tb.HorizontalAlignment switch
            {
                HorizontalAlignment.Center => HorizontalAlignment.Center,
                HorizontalAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left
            };
            ItemRenameTextBox.TextWrapping = tb.TextWrapping;
        }
        else
        {
            ItemRenameTextBox.FontSize = ViewModel.IsListMode
                ? ViewModel.ListLabelFontSize
                : ViewModel.IconLabelFontSize;
            ItemRenameTextBox.TextAlignment = TextAlignment.Center;
            ItemRenameTextBox.TextWrapping = TextWrapping.NoWrap;
        }

        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        BeginInteractionLayer("file-item-rename-opened");
        // Ensure window is foreground then select filename without extension
        HoldTemporaryTopMost();
        _appWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(_hWnd);
        SelectFilenameWithoutExtension(ItemRenameTextBox);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Selects the filename portion without the extension, matching Windows Explorer behavior.
    /// For folders or files without extension, selects all.
    /// </summary>
    private static void SelectFilenameWithoutExtension(TextBox textBox)
    {
        textBox.Focus(FocusState.Programmatic);
        string text = textBox.Text;
        int dotIndex = text.LastIndexOf('.');
        // Only treat as extension if dot is not at position 0 (hidden files like .gitignore)
        // and the extension is reasonably short (≤ 8 chars) to avoid selecting ".tar.gz" style names entirely
        if (dotIndex > 0 && text.Length - dotIndex - 1 <= 8)
        {
            textBox.Select(0, dotIndex);
        }
        else
        {
            textBox.SelectAll();
        }
    }

    private void FocusTextInputEditor(TextBox textBox, bool selectAll)
    {
        HoldTemporaryTopMost();
        _appWindow.Show();
        base.Activate();
        Win32Helper.SetForegroundWindow(_hWnd);
        FocusTextInputEditorCore(textBox, selectAll);

        DispatcherQueue.TryEnqueue(() =>
        {
            base.Activate();
            Win32Helper.SetForegroundWindow(_hWnd);
            FocusTextInputEditorCore(textBox, selectAll);
        });
    }

    private static void FocusTextInputEditorCore(TextBox textBox, bool selectAll)
    {
        textBox.Focus(FocusState.Programmatic);
        if (selectAll)
        {
            textBox.SelectAll();
        }
    }

    private async void ItemRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitItemRenameAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelItemRename();
        }
    }

    private async void ItemRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCancellingItemRename)
        {
            _isCancellingItemRename = false;
            return;
        }

        await CommitItemRenameAsync();
    }

    private async Task CommitItemRenameAsync()
    {
        if (_isCommittingItemRename ||
            _itemRenameTarget is null ||
            ItemRenameTextBox.Visibility != Visibility.Visible)
        {
            return;
        }

        string newName = ItemRenameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            CancelItemRename();
            return;
        }

        _isCommittingItemRename = true;
        try
        {
            await ViewModel.RenameItemAsync(_itemRenameTarget, newName);
            CompleteItemRename();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync(_localizationService.T("Widget.RenameFailed"), ex.Message);
            ItemRenameTextBox.Focus(FocusState.Programmatic);
            ItemRenameTextBox.SelectAll();
        }
        finally
        {
            _isCommittingItemRename = false;
        }
    }

    private void CancelItemRename()
    {
        _isCancellingItemRename = true;
        CompleteItemRename();
    }

    private void CompleteItemRename()
    {
        ItemRenameTextBox.Visibility = Visibility.Collapsed;
        ItemRenameTextBox.IsHitTestVisible = false;
        ItemRenameTextBox.Text = string.Empty;

        // Restore the hidden TextBlock
        if (_itemRenameNameText is not null)
        {
            _itemRenameNameText.Visibility = Visibility.Visible;
            _itemRenameNameText = null;
        }

        _itemRenameTarget = null;
        ReleaseInteractionLayer("file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(FrameworkElement target, UIElement contentHost)
    {
        var topLeft = target.TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));

        // TextBox has 1px border + 2px horizontal padding. We need to offset
        // so the *text inside* the TextBox aligns exactly with the TextBlock text.
        const double border = 1.0;
        const double padX = 2.0;
        double offsetX = topLeft.X - border - padX;
        double offsetY = topLeft.Y - border;

        // The TextBox is a child of the contentHost Grid, which has Padding.
        // TransformToVisual gives coordinates relative to the Grid's visual bounds,
        // but the child is laid out starting from the content area (after padding).
        // We must subtract the host's padding to get the correct Margin.
        double hostPaddingH = 0;
        double hostPaddingV = 0;
        if (contentHost is Grid grid)
        {
            hostPaddingH = grid.Padding.Left + grid.Padding.Right;
            hostPaddingV = grid.Padding.Top + grid.Padding.Bottom;
            offsetX -= grid.Padding.Left;
            offsetY -= grid.Padding.Top;
        }

        double height = Math.Max(target.ActualHeight + 2 * border, 20);

        // Width: adapt to the host's content area. The usable width is
        // host.ActualWidth - hostPadding - |offsetX| - rightMargin.
        // offsetX is relative to content area, so right edge = host.ActualWidth - hostPaddingH + offsetX.
        const double rightMargin = 8;
        double width;
        if (contentHost is FrameworkElement host)
        {
            double contentWidth = host.ActualWidth - hostPaddingH;
            // offsetX is the TextBox's left position relative to the content area.
            // Available width = content area width - TextBox left position - right margin.
            double availableWidth = contentWidth - offsetX - rightMargin;
            if (ViewModel.IsListMode)
            {
                width = Math.Clamp(availableWidth, 80, contentWidth);
            }
            else
            {
                width = Math.Clamp(target.ActualWidth + 2 * (border + padX), 60, availableWidth);
            }

            double contentHeight = host.ActualHeight - hostPaddingV;
            height = Math.Min(height, Math.Max(20, contentHeight - offsetY - 4));
        }
        else
        {
            width = Math.Max(target.ActualWidth + 2 * (border + padX), 60);
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin = new Thickness(offsetX, offsetY, 0, 0);
    }

    private FrameworkElement? FindItemNameElement(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (ViewModel.IsIconMode &&
            TryGetDescendant<TextBlock>(container, out var iconNameText, "IconItemNameText"))
        {
            return iconNameText;
        }

        if (ViewModel.IsListMode &&
            TryGetDescendant<TextBlock>(container, out var listNameText, "ListItemNameText"))
        {
            return listNameText;
        }

        return null;
    }

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (TryGetDescendant<Border>(container, out var border) && border.Tag as string == "InteractiveSurface")
        {
            return border;
        }

        return container;
    }

    private void SelectSingleItem(WidgetItem item)
    {
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        ClearOtherWidgetSelections();
        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        listView.ScrollIntoView(item);
        ApplySelectionState(listView);
    }

    private void TogglePositionLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePositionLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
        ApplyLockActionIconState();
    }

    private void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        ShowDeleteWidgetFlyout(_lastMoreFlyoutTarget ?? MoreButton, _lastMoreFlyoutPosition);
    }

    private void TrackMoreFlyoutAnchor(FrameworkElement target, Windows.Foundation.Point? position)
    {
        _lastMoreFlyoutTarget = target;
        _lastMoreFlyoutPosition = position;
    }

    private void ShowDeleteWidgetFlyout(FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        if (_deletePending || App.Current is not App app || app.WidgetManager is null)
        {
            return;
        }

        _deleteWidgetFlyout?.Hide();
        var flyout = CreateDeleteWidgetFlyout(app.WidgetManager);
        _deleteWidgetFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_deleteWidgetFlyout, flyout))
            {
                return;
            }

            _isDeleteWidgetFlyoutOpen = false;
            _deleteWidgetFlyout = null;
            ReleaseInteractionLayer("file-delete-widget-closed");
        };

        _isDeleteWidgetFlyoutOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            BeginInteractionLayer("file-delete-widget-opened");
            if (position is Windows.Foundation.Point point)
            {
                flyout.ShowAt(target, point);
            }
            else
            {
                flyout.ShowAt(target);
            }
        });
    }

    private MenuFlyout CreateDeleteWidgetFlyout(WidgetManager widgetManager)
    {
        var flyout = new MenuFlyout();
        flyout.ShouldConstrainToRootBounds = false;

        var titleItem = new MenuFlyoutItem
        {
            Text = _localizationService.Format("Widget.DeleteWidgetTitle", ViewModel.Name),
            Icon = new FontIcon { Glyph = "\uE74D" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        bool canCleanupManagedFolder = widgetManager.CanCleanupManagedStorageForWidget(ViewModel.Config.Id);
        if (!canCleanupManagedFolder)
        {
            var noteItem = new MenuFlyoutItem
            {
                Text = _localizationService.T("Widget.DeleteWidgetNote"),
                Icon = new FontIcon { Glyph = "\uE946" },
                IsEnabled = false
            };
            flyout.Items.Add(noteItem);

            var confirmItem = CreateDeleteActionItem(
                _localizationService.T("Widget.DeleteWidgetConfirm"),
                WidgetRemovalAction.RemoveWidgetOnly);
            flyout.Items.Add(confirmItem);
            flyout.Items.Add(CreateCancelDeleteItem());
            return flyout;
        }

        var managedInfoItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.DeleteManagedInfo"),
            Icon = new FontIcon { Glyph = "\uE8B7" },
            IsEnabled = false
        };
        flyout.Items.Add(managedInfoItem);

        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.KeepManagedFolder"),
            WidgetRemovalAction.RemoveWidgetOnly,
            "\uE8B7",
            false));
        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.MoveBackThenDeleteFolder"),
            WidgetRemovalAction.MoveManagedFolderContentsToDesktop,
            "\uE8CA",
            false));
        flyout.Items.Add(CreateDeleteActionItem(
            _localizationService.T("Widget.DeleteFolderToRecycleBin"),
            WidgetRemovalAction.DeleteManagedFolder,
            "\uE74D",
            true));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateCancelDeleteItem());
        return flyout;
    }

    private MenuFlyoutItem CreateDeleteActionItem(
        string text,
        WidgetRemovalAction removalAction,
        string glyph = "\uE74D",
        bool isDanger = true)
    {
        var icon = new FontIcon { Glyph = glyph };
        if (isDanger)
        {
            icon.Foreground = new SolidColorBrush(Colors.Red);
        }

        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = icon
        };
        item.Click += (_, _) => QueueDeleteWidget(removalAction);
        return item;
    }

    private MenuFlyoutItem CreateCancelDeleteItem()
    {
        var item = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cancel"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        item.Click += (_, _) => _deleteWidgetFlyout?.Hide();
        return item;
    }

    private async Task ConfirmAndDeleteWidgetAsync(WidgetRemovalAction removalAction)
    {
        if (App.Current is not App app || app.WidgetManager is null)
        {
            _deletePending = false;
            return;
        }

        try
        {
            App.Log($"[WidgetDelete] Begin delete widget '{ViewModel.Name}' ({ViewModel.Config.Id})");
            await app.WidgetManager.RemoveWidgetAsync(ViewModel.Config.Id, removalAction);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetDelete] Delete failed for '{ViewModel.Name}' ({ViewModel.Config.Id}): {ex}");
            _deletePending = false;
            RootGrid.IsHitTestVisible = true;
            await ShowErrorDialogAsync(_localizationService.T("Widget.DeleteWidgetFailed"), ex.Message);
            ReleaseInteractionLayer("file-delete-widget-failed");
        }
    }

    private void QueueDeleteWidget(WidgetRemovalAction removalAction)
    {
        if (_deletePending)
        {
            return;
        }

        _deletePending = true;
        RootGrid.IsHitTestVisible = false;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(16);
            await ConfirmAndDeleteWidgetAsync(removalAction);
        });
    }

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.IsSizeLocked)
        {
            return;
        }

        if (sender is not FrameworkElement element)
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
        _displayChangeWatcher?.SuppressRestore();
        BeginInteractionLayer("file-resize-started");
        Win32Helper.GetCursorPos(out InitialCursorPt);
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(_hWnd, RootGrid);
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

        if (_resizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width + deltaX);
        }
        else if (_resizeDirection.Contains("Left"))
        {
            int rightEdge = _initialWindowPos.X + _initialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, _initialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (_resizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height + deltaY);
        }
        else if (_resizeDirection.Contains("Top"))
        {
            int bottomEdge = _initialWindowPos.Y + _initialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, _initialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new Windows.Graphics.RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, _resizeDirection);
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
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
        App.Current.ResizeGuideOverlay.EndResize();
        var finalPosition = _appWindow.Position;
        var finalSize = _appWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        ReleaseInteractionLayer("file-resize-ended");
        _displayChangeWatcher?.ResumeRestore();
        e.Handled = true;
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = ViewModel.IsSizeLocked
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

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.Items.Count == 0 && !ViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void QueueEmptyStateUpdate()
    {
        if (_emptyStateUpdateQueued)
        {
            return;
        }

        _emptyStateUpdateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _emptyStateUpdateQueued = false;
            UpdateEmptyState();
        });
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        string displayMessage = FormatUserFacingError(message);
        var completion = new TaskCompletionSource();

        _messageFlyout?.Hide();

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };

        var messageText = new TextBlock
        {
            Text = displayMessage,
            MaxWidth = 280,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };

        var closeButton = new Button
        {
            Content = _localizationService.T("Common.Ok"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var panel = new StackPanel
        {
            Width = 300,
            Spacing = 12,
            Padding = new Thickness(4)
        };
        panel.Children.Add(titleText);
        panel.Children.Add(messageText);
        panel.Children.Add(closeButton);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false
        };

        _messageFlyout = flyout;
        closeButton.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_messageFlyout, flyout))
            {
                _messageFlyout = null;
            }

            _isInlineFlyoutOpen = false;
            completion.TrySetResult();
            ReleaseInteractionLayer("file-message-closed");
        };

        _isInlineFlyoutOpen = true;
        BeginInteractionLayer("file-message-opened");
        flyout.ShowAt(MoreButton);
        await completion.Task;
    }

    private string FormatUserFacingError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return _localizationService.T("Widget.Error.OperationIncomplete");
        }

        if (message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已取消", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.T("Widget.Error.OperationCanceled");
        }

        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("没有权限", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.Format("Widget.Error.AccessDenied", message);
        }

        if (message.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("另一个进程", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase))
        {
            string? path = TryExtractQuotedPath(message);
            return string.IsNullOrWhiteSpace(path)
                ? _localizationService.T("Widget.Error.FileInUse")
                : _localizationService.Format("Widget.Error.FileInUseWithPath", path);
        }

        if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("已存在", StringComparison.OrdinalIgnoreCase))
        {
            return _localizationService.Format("Widget.Error.DuplicateName", message);
        }

        return message;
    }

    private static string? TryExtractQuotedPath(string message)
    {
        int start = message.IndexOf('\'');
        if (start < 0)
        {
            return null;
        }

        int end = message.IndexOf('\'', start + 1);
        if (end <= start + 1)
        {
            return null;
        }

        return message[(start + 1)..end];
    }

    private void ShowFlyoutWithElevation(MenuFlyout flyout, FrameworkElement target, Windows.Foundation.Point? position = null)
    {
        BeginInteractionLayer("file-flyout-opened");
        flyout.Closed += (_, _) => ReleaseInteractionLayer("file-flyout-closed");

        if (position is Windows.Foundation.Point point)
        {
            flyout.ShowAt(target, point);
        }
        else
        {
            flyout.ShowAt(target);
        }
    }

    private void BeginInteractionLayer(string reason, bool elevate = true)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction(reason);
        if (elevate)
        {
            ElevateForInteraction();
        }
    }

    private void ReleaseInteractionLayer(string reason)
    {
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    private bool ShouldStartTitleDrag(object? originalSource)
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
               !IsWithin(source, TitleEditBox);
    }

    private bool CanStartRenameFromTitleArea(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return IsWithin(source, TitleText) || IsWithin(source, FileTitleIcon);
    }

    private bool IsTitleBarDoubleClick(Win32Helper.POINT currentPoint)
    {
        if (!_hasPendingTitleBarClick)
        {
            return false;
        }

        if ((DateTime.UtcNow - _lastTitleBarClickTimeUtc).TotalMilliseconds > 420)
        {
            return false;
        }

        int deltaX = currentPoint.X - _lastTitleBarClickPoint.X;
        int deltaY = currentPoint.Y - _lastTitleBarClickPoint.Y;
        return ((deltaX * deltaX) + (deltaY * deltaY)) <= 36;
    }

    private void TrackTitleBarClick(object? originalSource, Win32Helper.POINT currentPoint)
    {
        if (!CanStartRenameFromTitleArea(originalSource))
        {
            _hasPendingTitleBarClick = false;
            return;
        }

        _lastTitleBarClickTimeUtc = DateTime.UtcNow;
        _lastTitleBarClickPoint = currentPoint;
        _hasPendingTitleBarClick = true;
    }

    private static bool CanPasteFromClipboard()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch
        {
            return false;
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
               !IsWithin(source, TitleEditBox) &&
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

    private static bool IsWithinInteractiveSurface(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Border border && border.Tag as string == "InteractiveSurface")
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsCursorOverThisWindow()
    {
        if (!Win32Helper.GetCursorPos(out var cursor) ||
            !Win32Helper.GetWindowRect(_hWnd, out var rect))
        {
            return false;
        }

        return cursor.X >= rect.Left &&
               cursor.X <= rect.Right &&
               cursor.Y >= rect.Top &&
               cursor.Y <= rect.Bottom;
    }

    private bool IsCursorOnDesktop()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        try
        {
            var display = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cursor.X, cursor.Y),
                DisplayAreaFallback.Nearest);
            var workArea = display.WorkArea;
            return cursor.X >= workArea.X &&
                   cursor.X <= workArea.X + workArea.Width &&
                   cursor.Y >= workArea.Y &&
                   cursor.Y <= workArea.Y + workArea.Height;
        }
        catch
        {
            return true;
        }
    }

    private void EnsureStoryboards()
    {
        if (_showButtonsStoryboard is not null)
        {
            return;
        }

        _showButtonsStoryboard = new Storyboard();

        var showRightOpacity = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(showRightOpacity, "Opacity");
        _showButtonsStoryboard.Children.Add(showRightOpacity);

        var showRightX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(showRightX, "X");
        _showButtonsStoryboard.Children.Add(showRightX);

        _hideButtonsStoryboard = new Storyboard();

        var hideRightOpacity = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(hideRightOpacity, "Opacity");
        _hideButtonsStoryboard.Children.Add(hideRightOpacity);

        var hideRightX = new DoubleAnimation
        {
            To = 12,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideRightX, RightButtonsTransform);
        Storyboard.SetTargetProperty(hideRightX, "X");
        _hideButtonsStoryboard.Children.Add(hideRightX);
    }

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ApplyLegacyTitleActionButtonVisibility(_chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor));
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyLegacyTitleActionButtonVisibility(_chromeModeResolver.Resolve(ViewModel.Config, _chromeDescriptor));
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

    private DataPackageOperation GetManagedDropOperation()
    {
        return DataPackageOperation.Move;
    }

    private DataPackageOperation GetAcceptedDropOperation(DataPackageOperation requestedOperation, bool movesIntoFolder)
    {
        if (!movesIntoFolder)
        {
            if (SupportsOperation(requestedOperation, DataPackageOperation.Link))
            {
                return DataPackageOperation.Link;
            }

            return SupportsOperation(requestedOperation, DataPackageOperation.Copy) || requestedOperation == DataPackageOperation.None
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        bool ctrlPressed = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control);
        if (ctrlPressed)
        {
            return DataPackageOperation.Copy;
        }

        var preferredOperation = GetManagedDropOperation();
        if (CanUseRequestedOperation(requestedOperation, preferredOperation))
        {
            return preferredOperation;
        }

        var fallbackOperation = preferredOperation == DataPackageOperation.Move
            ? DataPackageOperation.Copy
            : DataPackageOperation.Move;
        return CanUseRequestedOperation(requestedOperation, fallbackOperation)
            ? fallbackOperation
            : DataPackageOperation.None;
    }

    private static bool CanUseRequestedOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return requestedOperation == DataPackageOperation.None ||
               SupportsOperation(requestedOperation, operation);
    }

    private static bool SupportsOperation(DataPackageOperation requestedOperation, DataPackageOperation operation)
    {
        return (requestedOperation & operation) == operation;
    }

    private bool CanMoveItemsBackToDesktop()
    {
        return !string.IsNullOrWhiteSpace(ViewModel.MappedFolderPath);
    }

}
