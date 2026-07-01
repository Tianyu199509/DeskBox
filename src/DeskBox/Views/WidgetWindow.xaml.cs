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

public sealed partial class WidgetWindow : Window, IDesktopWidgetWindow
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

    private readonly Microsoft.UI.WindowId _windowId;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly WidgetWindowDiagnostics _diagnostics;
    private readonly WidgetTrayAnimationController _trayAnimation;
    private readonly Win32Helper.SubclassProc _fileDropSubclassProc;
    private readonly WidgetContentDescriptor _chromeDescriptor;
    private readonly WidgetChromeModeResolver _chromeModeResolver;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _isDragging;
    private bool _hasMovedTitleBarDrag;
    private bool _isResizing;
    private string _resizeDirection = string.Empty;
    private Win32Helper.POINT _initialCursorPt;
    private Windows.Graphics.PointInt32 _initialWindowPos;
    private Windows.Graphics.SizeInt32 _initialWindowSize;
    private FrameworkElement? _dragCaptureElement;

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
    private bool _isSynchronizingSelection;
    private MenuFlyout? _itemDeleteConfirmFlyout;
    private Flyout? _messageFlyout;
    private WidgetItem? _itemRenameTarget;
    private MenuFlyout? _deleteWidgetFlyout;
    private FrameworkElement? _lastMoreFlyoutTarget;
    private Windows.Foundation.Point? _lastMoreFlyoutPosition;
    private bool _isDeleteWidgetFlyoutOpen;
    private bool _isInlineFlyoutOpen;
    private bool _isCommittingTitleRename;
    private bool _isCommittingItemRename;
    private bool _isCancellingTitleRename;
    private bool _isCancellingItemRename;
    private DateTime _lastTitleBarClickTimeUtc;
    private Win32Helper.POINT _lastTitleBarClickPoint;
    private bool _hasPendingTitleBarClick;
    private bool _isAtDesktopLayer;
    private bool _keepRaisedUntilDeactivate;
    private bool _restoreDesktopLayerWhenIdle;
    private bool _isHideAnimationRunning;
    private bool _isMigrationBusy;
    private bool _isNativeBackdropSuppressedForTrayReveal;
    private long _backdropRefreshGeneration;
    private bool _areItemTransitionsSuppressed;
    private DispatcherQueueTimer? _autoRestoreTimer;
    private DispatcherQueueTimer? _topMostSafetyTimer;
    private bool _isFileDropSubclassInstalled;
    private TransitionCollection? _savedGridItemTransitions;
    private TransitionCollection? _savedListItemTransitions;

    private Border BackgroundPlate => FileWidgetShell.BackgroundSurface;
    private Border HeaderDivider => FileWidgetShell.Divider;

    public WidgetViewModel ViewModel { get; }

    public IntPtr WindowHandle => _hWnd;

    public WidgetWindowIdentity Identity => _diagnostics.Identity;

    public WidgetConfig Config => ViewModel.Config;

    public Windows.Foundation.Rect AnimationBounds => _diagnostics.AnimationBounds;

    private bool _isVisibleOnDesktop;
    private bool _isClosing;
    private DateTime _lastElevateForInteractionUtc = DateTime.MinValue;
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
        _settingsService = settingsService;
        _localizationService = localizationService ?? new LocalizationService(settingsService);
        _fileDropSubclassProc = FileDropSubclassProc;
        _chromeDescriptor = new WidgetContentFactory(_localizationService).GetDescriptor(WidgetKind.File);
        _chromeModeResolver = new WidgetChromeModeResolver(settingsService);
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        ApplyLocalizedText();
        FileWidgetShell.SetDividerMargin(new Thickness(12, 0, 12, 0));

        _hWnd = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        _diagnostics = new WidgetWindowDiagnostics("File", ViewModel.Config, () => _hWnd);
        _trayAnimation = new WidgetTrayAnimationController(
            _appWindow,
            RootGrid,
            DispatcherQueue,
            _hWnd,
            () => _diagnostics.AnimationBounds,
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
        InstallFileDropSubclass();

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

            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(_hWnd);
            QueueBackdropRefresh();
        };

        RootGrid.ActualThemeChanged += (_, _) => ApplyBackdropPreference();
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
    }

    private void SetupEventHandlers()
    {
        _settingsService.SettingsChanged += OnSettingsChanged;
        _localizationService.LanguageChanged += OnLanguageChanged;

        Activated += WidgetWindow_Activated;

        _appWindow.Changed += AppWindow_Changed;

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
            _settingsService.SettingsChanged -= OnSettingsChanged;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _appWindow.Changed -= AppWindow_Changed;
            _autoRestoreTimer?.Stop();
            _autoRestoreTimer = null;
            RemoveFileDropSubclass();
            _trayAnimation.Stop();
            _trayAnimation.RestoreVisualState();
            _trayAnimation.RestoreWindowPosition();
            RestoreItemContainerTransitions();
            DisposeAcrylicController();

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

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_trayAnimation.IsApplyingBounds)
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = _appWindow.Position;
            var size = _appWindow.Size;
            ViewModel.UpdateBounds(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    private void ApplyLocalizedText()
    {
        TitleEditBox.PlaceholderText = _localizationService.T("Widget.TitlePlaceholder");
        ToolTipService.SetToolTip(AddButton, _localizationService.T("Widget.Tooltip.Add"));
        ToolTipService.SetToolTip(MoreButton, _localizationService.T("Widget.Tooltip.More"));
        ToolTipService.SetToolTip(CloseButton, _localizationService.T("Widget.Tooltip.DeleteWidget"));
        MigrationTitleText.Text = _localizationService.T("Widget.Migration.Title");
        MigrationDescriptionText.Text = _localizationService.T("Widget.Migration.Description");
    }

    public void PushToBottom()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        Win32Helper.SetWindowToBottom(_hWnd);
        App.Log($"[ZOrder] Widget PushToBottom hwnd=0x{_hWnd.ToInt64():X}");
    }

    public void ClearTopMostOnly()
    {
        _isAtDesktopLayer = true;
        Win32Helper.ClearWindowTopMost(_hWnd);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _hWnd)
        {
            Win32Helper.BringWindowToFront(foreground);
        }
        App.Log($"[ZOrder] Widget ClearTopMostOnly hwnd=0x{_hWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
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
            App.Log($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-visible hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (_isAtDesktopLayer)
        {
            App.Log($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED atDesktop hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            App.Log($"[ZOrder] Widget EnsureRaisedFromTrayTopMost SKIPPED not-raised hwnd=0x{_hWnd.ToInt64():X}");
            return;
        }

        App.Log($"[ZOrder] Widget EnsureRaisedFromTrayTopMost hwnd=0x{_hWnd.ToInt64():X} atDesktop={_isAtDesktopLayer}");
        _appWindow.Show();
        Win32Helper.ShowWindow(_hWnd, Win32Helper.SW_SHOWNORMAL);
        Win32Helper.BringWindowToFront(_hWnd);
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
        Win32Helper.ClearWindowTopMost(_hWnd);
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
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        Win32Helper.SetWindowTopMost(_hWnd);
        var stack = new System.Diagnostics.StackTrace(true);
        var frame = stack.GetFrame(1);
        App.Log($"[ZOrder] Widget HoldTemporaryTopMost hwnd=0x{_hWnd.ToInt64():X} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray} from={frame?.GetMethod()?.DeclaringType?.Name}.{frame?.GetMethod()?.Name}");
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
                App.Log($"[ZOrder] Widget safety timer: force restore hwnd=0x{_hWnd.ToInt64():X}");
                RestoreDesktopLayer(force: true);
            }
        };
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
            (App.Current.WidgetManager is { WidgetsRaisedFromTray: true }))
        {
            App.Log($"[ZOrder] Widget PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] Widget PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(_hWnd);
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
        App.Log($"[ZOrder] Widget ForceRestore hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer}");
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

    private void ApplyWindowBounds(int x, int y, int width, int height, bool persist)
    {
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        ViewModel.UpdateBounds(x, y, width, height, persist: false);

        if (persist)
        {
            ViewModel.UpdateBounds(x, y, width, height, persist: true);
        }
    }

    private void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new Windows.Graphics.RectInt32(x, y, width, height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        WidgetPositioningService.CaptureAnchor(ViewModel.Config, bounds, workArea);
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

        try
        {
            Win32Helper.SetWindowTheme(_hWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(_hWnd);

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

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{_hWnd.ToInt64():X} backdrop=acrylic isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={_acrylicController is not null}");
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            Win32Helper.ApplyAccentBlur(_hWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
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
        if (generation != _backdropRefreshGeneration)
        {
            return;
        }

        if (!Visible || _isHideAnimationRunning)
        {
            return;
        }

        ApplyBackdropPreference();
    }

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
        finally
        {
            _acrylicController = null;
        }
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

        var backgroundColor = BuildFrostedSurfaceColor(isDark, accentColor, surfaceOpacity);

        byte chromeAlpha = 0x18;

        var borderColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.75), 0, 255), 0xFF, 0xFF, 0xFF)
            : WithAlpha(BlendColors(ColorHelper.FromArgb(0xFF, 0x00, 0x00, 0x00), accentColor, 0.22), chromeAlpha);

        var dividerColor = isDark
            ? ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.66), 0, 255), 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb((byte)Math.Clamp(Math.Round(chromeAlpha * 0.42), 0, 255), 0x00, 0x00, 0x00);

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

        BackgroundPlate.Background = new SolidColorBrush(backgroundColor);
        BackgroundPlate.BorderBrush = new SolidColorBrush(borderColor);
        HeaderDivider.Background = new SolidColorBrush(dividerColor);
        TitleIcon.Foreground = new SolidColorBrush(iconForeground);
        TitleEditBox.Background = new SolidColorBrush(editorBackground);
        TitleEditBox.BorderBrush = new SolidColorBrush(editorBorder);
        TitleEditBox.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);
        EmptyStateTitleText.Foreground = new SolidColorBrush(isDark ? Colors.White : ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
        EmptyStateDescriptionText.Foreground = new SolidColorBrush(secondaryText);
        EmptyStateIcon.Foreground = new SolidColorBrush(secondaryText);
        SelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        SelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
        StatusToastText.Foreground = new SolidColorBrush(isDark ? Colors.White : Colors.Black);

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
        foreach (var border in FindInteractiveSurfaceBorders(RootGrid))
        {
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
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
        FileWidgetShell.ShowAddButton = ViewModel.TopAddButtonVisibility == Visibility.Visible;
        FileWidgetShell.TitleGlyph = ViewModel.IconGlyph;
        FileWidgetShell.SetTitleBarPadding(WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode));
        FileWidgetShell.TitleBarContent = chromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden
            ? null
            : TitleBarGrid;
        ApplyLegacyTitleActionButtonVisibility(chromeMode);

        TitleIcon.FontSize = metrics.TitleIconSize;
        TitleText.FontSize = metrics.TitleTextSize;
        TitleEditBox.FontSize = Math.Max(metrics.TitleTextSize - 1, 11);

        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);

        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);

        RootGrid.RowDefinitions[0].Height = metrics.RowHeight;
        FileWidgetShell.SetTitleBarRowHeight(metrics.RowHeight);
        TitleBarGrid.Padding = metrics.InnerTitlePadding;
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

        var backgroundColor = state switch
        {
            ItemSurfaceState.DropTarget => dropTargetBackground,
            ItemSurfaceState.Hover when isSelected => selectedHoverBackground,
            ItemSurfaceState.Pressed when isSelected => selectedHoverBackground,
            ItemSurfaceState.Hover => hoverBackground,
            ItemSurfaceState.Pressed => pressedBackground,
            _ when isSelected => selectedBackground,
            _ => defaultBackground
        };

        border.Background = new SolidColorBrush(backgroundColor);
        border.BorderBrush = new SolidColorBrush(state == ItemSurfaceState.DropTarget
            ? WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xD8)
            : Colors.Transparent);
        border.BorderThickness = state == ItemSurfaceState.DropTarget
            ? new Thickness(1)
            : new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
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
        else if (e.PropertyName is nameof(WidgetViewModel.WidgetOpacity) or nameof(WidgetViewModel.MappedFolderPath))
        {
            ApplyBackdropPreference();
            UpdateEmptyState();
        }
        else if (e.PropertyName is nameof(WidgetViewModel.ViewMode)
            or nameof(WidgetViewModel.IconViewVisibility)
            or nameof(WidgetViewModel.ListViewVisibility)
            or nameof(WidgetViewModel.ShowListItemDetails)
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
        var flyout = new MenuFlyout();

        if (isMultiSelection)
        {
            AddMultiSelectionItems(flyout, selectedItems.Count);
            ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
            e.Handled = true;
            return;
        }

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

        ShowFlyoutWithElevation(flyout, element, e.GetPosition(element));
        e.Handled = true;
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
            ApplyWidgetItemLayout(border);
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void WidgetItemSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isMigrationBusy)
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

    private void WidgetItemSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
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

        foreach (var border in FindInteractiveSurfaceBorders(RootGrid))
        {
            if (border.DataContext is not WidgetItem { IsFolder: true } folder ||
                !Directory.Exists(folder.Path) ||
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
        ElevateForInteraction();
        _initialCursorPt = cursorPt;
        _initialWindowPos = _appWindow.Position;
        _initialWindowSize = _appWindow.Size;
        _dragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;
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
        ApplyWindowBounds(newX, newY, _initialWindowSize.Width, _initialWindowSize.Height, persist: false);
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
        if (_hasMovedTitleBarDrag)
        {
            var finalPosition = _appWindow.Position;
            var finalSize = _appWindow.Size;
            CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
            ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
            ReleaseInteractionLayer("file-drag-ended");
        }

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
            Icon = new FontIcon { Glyph = "\uE740" },
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

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        var titleItem = new MenuFlyoutItem
        {
            Text = items.Length == 1
                ? _localizationService.Format("Widget.DeleteItemsTitleOne", items[0].Name)
                : _localizationService.Format("Widget.DeleteItemsTitleMany", items.Length),
            Icon = new FontIcon { Glyph = "\uE946" },
            IsEnabled = false
        };
        flyout.Items.Add(titleItem);

        string message = items.Length == 1
            ? _localizationService.T("Widget.DeleteFolderNoteOne")
            : _localizationService.Format("Widget.DeleteFolderNoteMany", folderCount);
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = message,
            Icon = new FontIcon { Glyph = "\uE783" },
            IsEnabled = false
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        var confirmItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Widget.MoveToRecycleBin"),
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        confirmItem.Click += async (_, _) => await DeleteItemsWithoutConfirmAsync(items);
        flyout.Items.Add(confirmItem);

        var cancelItem = new MenuFlyoutItem
        {
            Text = _localizationService.T("Common.Cancel"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        cancelItem.Click += (_, _) => flyout.Hide();
        flyout.Items.Add(cancelItem);

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
        var target = FindItemNameElement(item) ?? FindItemSurface(item);
        var contentHost = SelectionOverlay.Parent as UIElement;
        if (target is null || contentHost is null)
        {
            return;
        }

        SelectSingleItem(item);

        _itemRenameTarget = item;
        _isCancellingItemRename = false;
        ItemRenameTextBox.Text = item.Name;
        PositionItemRenameTextBox(target, contentHost);
        ItemRenameTextBox.Visibility = Visibility.Visible;
        ItemRenameTextBox.IsHitTestVisible = true;
        BeginInteractionLayer("file-item-rename-opened");
        FocusTextInputEditor(ItemRenameTextBox, selectAll: true);
        await Task.CompletedTask;
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
        _itemRenameTarget = null;
        ReleaseInteractionLayer("file-item-rename-closed");
    }

    private void PositionItemRenameTextBox(FrameworkElement target, UIElement contentHost)
    {
        var topLeft = target.TransformToVisual(contentHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        double width = Math.Clamp(target.ActualWidth > 0 ? target.ActualWidth + 12 : 220, 120, 280);
        double height = Math.Max(30, target.ActualHeight > 0 ? target.ActualHeight + 6 : 30);

        if (contentHost is FrameworkElement host)
        {
            width = Math.Min(width, Math.Max(120, host.ActualWidth - topLeft.X - 8));
            height = Math.Min(height, Math.Max(30, host.ActualHeight - topLeft.Y - 8));
        }

        ItemRenameTextBox.Width = width;
        ItemRenameTextBox.Height = height;
        ItemRenameTextBox.Margin = new Thickness(topLeft.X - 6, topLeft.Y - 3, 0, 0);
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
    }

    private void ToggleSizeLock_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSizeLockCommand.Execute(null);
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
        BeginInteractionLayer("file-resize-started");
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
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        ViewModel.UpdateBounds(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        ReleaseInteractionLayer("file-resize-ended");
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

        return !IsWithin(source, AddButton) &&
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

        return IsWithin(source, TitleText) || IsWithin(source, TitleIcon);
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

        return !IsWithin(source, AddButton) &&
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
